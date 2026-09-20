from uuid import uuid4

import pytest
from fastapi.testclient import TestClient

from orchestrator.app import create_app
from orchestrator.grading import TranscriptStore, event_text, rubric_goals
from orchestrator.models import Goal, Grade, GoalResult, TranscriptTurn
from orchestrator.scene import GoalSpec
from orchestrator.speech import SpeechOutput
from orchestrator.tests.test_api import Scenarios


RUBRIC = [{"id": "G1", "npc_id": "maria", "core": True, "label": "Order something",
           "evidence_required": "The learner names a menu item in a request form."},
          {"id": "G2", "npc_id": "maria", "core": False, "label": "Ask the price",
           "evidence_required": "The learner asks what it costs."}]
TRANSCRIPT = [{"role": "learner", "npc_id": "maria", "text": "Quisiera un café, por favor."},
              {"role": "npc", "npc_id": "maria", "text": "Claro que sí."},
              {"role": "event", "npc_id": "maria", "text": "serve_order items=cafe_olla total_mxn=45"}]


class Grader:
    """A grader double: awards every core goal, and records what it was asked."""

    def __init__(self, overall=8):
        self.calls = []
        self.overall = overall

    async def grade(self, rubric, transcript):
        self.calls.append((rubric, transcript))
        return Grade(overall=self.overall, summary="Good ordering; ask prices next time.",
                     goals=[GoalResult(goal_id=goal.id, achieved=goal.core,
                                       evidence_quote="Quisiera un café, por favor."
                                       if goal.core else None,
                                       note="Judged from the transcript.")
                            for goal in rubric])


class Broken:
    def __init__(self, make):
        self.make = make

    async def grade(self, rubric, transcript):
        return self.make(rubric)


class Speech:
    def __init__(self, actions=()):
        self.actions = actions

    async def respond(self, request):
        return SpeechOutput(b"\x00\x00", "audio/pcm", 16000, "Quisiera un café",
                            "Claro que sí", tuple(self.actions))


def app(tmp_path, **kwargs):
    return create_app(data_dir=tmp_path, runs_dir=tmp_path / "runs", **kwargs)


def test_grades_an_inline_transcript(tmp_path):
    grader = Grader()
    with TestClient(app(tmp_path, grader=grader)) as client:
        result = client.post("/v1/grade", json={"goals": RUBRIC, "transcript": TRANSCRIPT})
        assert result.status_code == 200
        body = result.json()
        assert body["overall"] == 8
        assert body["goals_achieved"] == 1 and body["goals_total"] == 2
        assert [g["goal_id"] for g in body["goals"]] == ["G1", "G2"]
        assert body["goals"][1]["evidence_quote"] is None
        # The grader sees the event line, so it can check a claim against what happened.
        rubric, transcript = grader.calls[0]
        assert [t.role for t in transcript] == ["learner", "npc", "event"]
        assert rubric[0].label == "Order something"


def test_grades_a_recorded_run_against_a_saved_scenario(tmp_path):
    """The two halves together: speech records the run, grade reads it back."""
    grader = Grader(overall=10)
    speech = Speech(actions=[{"action": "serve_order", "npc_id": "luis",
                              "items": ["cafe_olla"], "total_mxn": 45}])
    with TestClient(app(tmp_path, scenarios=Scenarios(), speech=speech, grader=grader)) as client:
        saved = client.post("/v1/scenarios", json={"prompt": "Order at a café"}).json()
        turn = client.post("/v1/speech", params={"session_id": str(uuid4()), "npc_id": "luis",
            "run_id": "visit-1"}, content=b"aa", headers={"Content-Type": "audio/wav"})
        assert turn.status_code == 200

        run = client.get("/v1/runs/visit-1").json()
        assert run["turn_count"] == 3
        assert [t["role"] for t in run["transcript"]] == ["learner", "npc", "event"]
        assert run["transcript"][2]["text"] == "serve_order items=cafe_olla total_mxn=45"

        result = client.post("/v1/grade", json={"scenario_id": saved["scenario_id"],
                                                "run_id": "visit-1"})
        assert result.status_code == 200
        body = result.json()
        assert body["overall"] == 10 and body["run_id"] == "visit-1"
        assert body["scenario_id"] == saved["scenario_id"]
        # The rubric came from the saved scenario, normalised from its goal shape.
        rubric, transcript = grader.calls[0]
        assert [goal.id for goal in rubric] == ["order"]
        assert rubric[0].label == "Order a drink"
        assert len(transcript) == 3


def test_grade_falls_back_to_the_loaded_pack(tmp_path):
    grader = Grader()
    with TestClient(app(tmp_path, grader=grader)) as client:
        result = client.post("/v1/grade", json={"transcript": TRANSCRIPT})
        if result.status_code == 422:
            pytest.skip("No scenario pack is loaded in this environment")
        assert result.status_code == 200
        assert [goal.id for goal in grader.calls[0][0]][:1] == ["G1"]


@pytest.mark.parametrize("payload", [
    {"transcript": TRANSCRIPT},                      # no rubric
    {"goals": RUBRIC},                               # no conversation
    {"goals": RUBRIC, "transcript": []},
    {"goals": RUBRIC, "transcript": TRANSCRIPT, "unknown": 1},
    {"goals": RUBRIC, "transcript": [{"role": "narrator", "npc_id": "maria", "text": "x"}]},
    {"goals": RUBRIC, "transcript": TRANSCRIPT, "run_id": "../escape"},
])
def test_invalid_grade_request(payload, tmp_path, monkeypatch):
    monkeypatch.setenv("SCENARIO_PACK_DIR", str(tmp_path / "absent"))
    with TestClient(app(tmp_path, grader=Grader())) as client:
        assert client.post("/v1/grade", json=payload).status_code == 422


def test_grade_requires_json(tmp_path):
    with TestClient(app(tmp_path, grader=Grader())) as client:
        assert client.post("/v1/grade", content=b"{}",
                           headers={"Content-Type": "text/plain"}).status_code == 415


def test_grade_unconfigured(monkeypatch, tmp_path):
    for key in ("OPENAI_API_KEY", "TUTOR_MODEL", "SCENARIO_MODEL"):
        monkeypatch.delenv(key, raising=False)
    with TestClient(app(tmp_path)) as client:
        assert client.get("/health").json()["grader_ready"] is False
        assert client.post("/v1/grade", json={"goals": RUBRIC,
                                              "transcript": TRANSCRIPT}).status_code == 503


def award_without_evidence(rubric):
    return Grade(overall=9, summary="Nice work.",
                 goals=[GoalResult(goal_id=goal.id, achieved=True, evidence_quote=None,
                                   note="Trust me.") for goal in rubric])


def judge_the_wrong_goals(rubric):
    return Grade(overall=9, summary="Nice work.",
                 goals=[GoalResult(goal_id="G9", achieved=False, evidence_quote=None,
                                   note="Not a goal that was asked about.")])


@pytest.mark.parametrize("make", [award_without_evidence, judge_the_wrong_goals])
def test_untrustworthy_grade_is_rejected(make, tmp_path):
    with TestClient(app(tmp_path, grader=Broken(make))) as client:
        assert client.post("/v1/grade", json={"goals": RUBRIC,
                                              "transcript": TRANSCRIPT}).status_code == 502


@pytest.mark.parametrize("run_id,status", [("never-happened", 404), ("bad.id", 422)])
def test_unknown_run(run_id, status, tmp_path):
    with TestClient(app(tmp_path, grader=Grader())) as client:
        assert client.get(f"/v1/runs/{run_id}").status_code == status
        assert client.post("/v1/grade", json={"goals": RUBRIC,
                                              "run_id": run_id}).status_code in {404, 422}


def test_rubric_normalises_both_goal_shapes():
    generated = Goal(id="order", description="Order a drink", evidence_required="Asks for one",
                     npc_id="luis", core=True)
    authored = GoalSpec(id="G1", npc_id="any", core=False, label="Recover in Spanish",
                        evidence_required="Uses a repair phrase")
    assert [g.label for g in rubric_goals([generated, authored])] == \
        ["Order a drink", "Recover in Spanish"]
    assert [g.npc_id for g in rubric_goals([generated, authored])] == ["luis", "any"]


def test_event_text_is_readable():
    assert event_text({"action": "serve_order", "npc_id": "maria", "items": ["a", "b"],
                       "to_go": None, "total_mxn": 80}) == "serve_order items=a,b total_mxn=80"
    assert event_text({}) == "action"


def test_transcript_store_survives_a_truncated_line(tmp_path):
    store = TranscriptStore(tmp_path)
    store.append("visit-1", [TranscriptTurn(role="learner", npc_id="maria", text="Hola")])
    (tmp_path / "visit-1.jsonl").open("a").write('{"role": "npc", "npc_')
    assert [t.text for t in store.get("visit-1")] == ["Hola"]
    with pytest.raises(ValueError):
        store.get("../secrets")
