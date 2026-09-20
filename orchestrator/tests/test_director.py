import time
from uuid import uuid4

import pytest
from fastapi.testclient import TestClient

from orchestrator.app import create_app
from orchestrator.director import GoalStore
from orchestrator.models import GoalTick, Verdict
from orchestrator.tests.test_api import Scenarios
from orchestrator.tests.test_grading import Speech


class Judge:
    """A director double: ticks every open goal whose id is in `meets`, and records
    what it was asked so a test can see which goals were still open."""

    def __init__(self, meets=("order",), quote="Quisiera un café, por favor."):
        self.meets = set(meets)
        self.quote = quote
        self.calls = []

    async def review(self, pending, transcript):
        self.calls.append(([g.id for g in pending], transcript))
        return Verdict(achieved=[GoalTick(id=g.id, evidence_quote=self.quote)
                                 for g in pending if g.id in self.meets])


class Failing:
    async def review(self, pending, transcript):
        raise RuntimeError("provider down")


def app(tmp_path, **kwargs):
    return create_app(data_dir=tmp_path, runs_dir=tmp_path / "runs", **kwargs)


def speak(client, run_id, npc_id="luis", **params):
    return client.post("/v1/speech", params={"session_id": str(uuid4()), "npc_id": npc_id,
                                             "run_id": run_id, "response_format": "json",
                                             **params},
                       content=b"aa", headers={"Content-Type": "audio/wav"})


def settled(client, run_id, **params):
    """The goal state once the review kicked off by the last turn has finished."""
    for _ in range(100):
        body = client.get(f"/v1/runs/{run_id}/goals", params=params).json()
        if not body["reviewing"]:
            return body
        time.sleep(0.02)
    pytest.fail("director never finished reviewing")


def test_goals_tick_after_a_turn_and_stay_ticked(tmp_path):
    judge = Judge(meets={"order"})
    with TestClient(app(tmp_path, scenarios=Scenarios(), speech=Speech(), director=judge)) as client:
        saved = client.post("/v1/scenarios", json={"prompt": "Order at a café"}).json()
        sid = saved["scenario_id"]

        first = speak(client, "visit-1", scenario_id=sid)
        assert first.status_code == 200
        # The reply goes out before the review: nothing ticked yet on the first turn.
        assert first.json()["goals_achieved"] == []

        state = settled(client, "visit-1", scenario_id=sid)
        assert [(g["id"], g["achieved"]) for g in state["goals"]] == [("order", True)]
        assert state["goals"][0]["evidence_quote"] == "Quisiera un café, por favor."
        assert state["goals"][0]["label"] == "Order a drink"

        # The next turn reports what was ticked so far, and the judge is only asked
        # about goals still open — here none, so it is not called at all.
        second = speak(client, "visit-1", scenario_id=sid)
        assert second.json()["goals_achieved"] == ["order"]
        settled(client, "visit-1", scenario_id=sid)
        assert len(judge.calls) == 1


def test_director_only_asks_about_open_goals_and_ignores_unasked_ticks(tmp_path):
    class Overreaching(Judge):
        async def review(self, pending, transcript):
            self.calls.append(([g.id for g in pending], transcript))
            return Verdict(achieved=[GoalTick(id="order", evidence_quote="Un café."),
                                     GoalTick(id="not_a_goal", evidence_quote="x")])

    judge = Overreaching()
    with TestClient(app(tmp_path, scenarios=Scenarios(), speech=Speech(), director=judge)) as client:
        sid = client.post("/v1/scenarios", json={"prompt": "Order at a café"}).json()["scenario_id"]
        speak(client, "visit-2", scenario_id=sid)
        state = settled(client, "visit-2", scenario_id=sid)
        assert [g["id"] for g in state["goals"] if g["achieved"]] == ["order"]
        assert judge.calls[0][0] == ["order"]
        # The judge saw the run as recorded: the learner line, then the character.
        assert [t.role for t in judge.calls[0][1]] == ["learner", "npc"]


def test_a_failed_review_costs_nothing(tmp_path):
    with TestClient(app(tmp_path, scenarios=Scenarios(), speech=Speech(), director=Failing())) as client:
        sid = client.post("/v1/scenarios", json={"prompt": "Order at a café"}).json()["scenario_id"]
        assert speak(client, "visit-3", scenario_id=sid).status_code == 200
        state = settled(client, "visit-3", scenario_id=sid)
        assert all(not g["achieved"] for g in state["goals"])


def test_goal_status_without_a_director_or_rubric(tmp_path, monkeypatch):
    for key in ("OPENAI_API_KEY", "DIRECTOR_MODEL", "TUTOR_MODEL", "SCENARIO_MODEL"):
        monkeypatch.delenv(key, raising=False)
    with TestClient(app(tmp_path)) as client:
        assert client.get("/health").json()["director_ready"] is False
        assert client.get("/v1/runs/visit-1/goals").status_code == 503
    monkeypatch.setenv("SCENARIO_PACK_DIR", str(tmp_path / "absent"))
    with TestClient(app(tmp_path, director=Judge())) as client:
        assert client.get("/v1/runs/visit-1/goals").status_code == 422
        assert client.get("/v1/runs/bad.id/goals", params={"scenario_id": str(uuid4())}).status_code in {404, 422}


def test_goal_store_is_forgiving(tmp_path):
    store = GoalStore(tmp_path)
    assert store.get("fresh") == {}
    store.save("visit-1", {"order": GoalTick(id="order", evidence_quote="Un café, por favor.")})
    assert list(store.get("visit-1")) == ["order"]
    (tmp_path / "visit-1.json").write_text("{not json")
    assert store.get("visit-1") == {}
    with pytest.raises(ValueError):
        store.get("../secrets")
