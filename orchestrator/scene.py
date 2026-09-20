"""Scenario data and the scene actions a character can trigger.

Pure logic: no network, no provider SDK, no event loop. The adapter calls
:func:`ScenePack.dispatch` when an agent asks for a tool, and gets back both the
sentence to hand the agent and the action to forward to the game client.

Two rules govern everything here, and both come from the build doc:

1. **Python owns arithmetic.** A character never invents a price. Totals come from
   ``menu.json`` so the bill is always right.
2. **A tool call never fails and never blocks.** Every dispatch returns a result the
   agent can read aloud its way out of, even when the parameters are nonsense. A
   blocking tool that gets no answer stalls the conversation for up to 20 billed
   seconds, so we always answer immediately.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Annotated, Any, Literal

from pydantic import BaseModel, ConfigDict, Field, model_validator

MAX_ITEMS_PER_ORDER = 12
Identifier = Annotated[str, Field(pattern=r"^[a-z][a-z0-9_]{0,63}$")]


# --------------------------------------------------------------------------- numbers

_UNITS = (
    "cero uno dos tres cuatro cinco seis siete ocho nueve diez once doce trece catorce "
    "quince dieciséis diecisiete dieciocho diecinueve veinte veintiuno veintidós "
    "veintitrés veinticuatro veinticinco veintiséis veintisiete veintiocho veintinueve"
).split()
_TENS = {3: "treinta", 4: "cuarenta", 5: "cincuenta", 6: "sesenta",
         7: "setenta", 8: "ochenta", 9: "noventa"}
_HUNDREDS = {1: "ciento", 2: "doscientos", 3: "trescientos", 4: "cuatrocientos",
             5: "quinientos", 6: "seiscientos", 7: "setecientos", 8: "ochocientos",
             9: "novecientos"}


def spanish_number(value: int) -> str:
    """Spell 0–9999 in Spanish, so a voice model never has to read digits.

    The prompts tell each character to say prices in words. Handing them the words
    removes the guesswork: "ochenta pesos", not "80".
    """
    if value < 0 or value > 9999:
        return str(value)
    if value < 30:
        return _UNITS[value]
    if value < 100:
        tens, rest = divmod(value, 10)
        return _TENS[tens] + (f" y {_UNITS[rest]}" if rest else "")
    if value == 100:
        return "cien"
    if value < 1000:
        hundreds, rest = divmod(value, 100)
        return _HUNDREDS[hundreds] + (f" {spanish_number(rest)}" if rest else "")
    thousands, rest = divmod(value, 1000)
    head = "mil" if thousands == 1 else f"{_UNITS[thousands]} mil"
    return head + (f" {spanish_number(rest)}" if rest else "")


# --------------------------------------------------------------------------- schema

class Model(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)


class MenuItem(Model):
    name_es: str
    price_mxn: Annotated[int, Field(ge=0, le=9999)]
    available: bool = True
    milk: bool = False
    sold_out_today: bool = False


class Menu(Model):
    currency: str = "MXN"
    note: str | None = None
    items: dict[Identifier, MenuItem]


class TtsSpec(Model):
    speed: Annotated[float, Field(ge=0.7, le=1.2)] = 1.0
    stability: Annotated[float, Field(ge=0.0, le=1.0)] = 0.45
    similarity_boost: Annotated[float, Field(ge=0.0, le=1.0)] = 0.8


class TurnSpec(Model):
    eagerness: Literal["eager", "normal", "patient"] = "patient"
    timeout: Annotated[float, Field(ge=1, le=30)] = 8


class NpcSpec(Model):
    npc_id: Identifier
    # Other ids a client may already be sending for this character. Lets a game
    # client that predates a rename keep working instead of getting a 503.
    aliases: list[Identifier] = []
    name: str
    role: str
    gender: Literal["female", "male", "other"]
    voice_id: str
    voice_name: str | None = None
    prompt_file: str
    first_message: str
    tools: list[Identifier] = []
    max_duration_seconds: Annotated[int, Field(ge=30, le=1800)] = 300
    tts: TtsSpec = TtsSpec()
    turn: TurnSpec = TurnSpec()


class GoalSpec(Model):
    id: str
    npc_id: str
    core: bool
    label: str
    evidence_required: str


class ToolSpec(Model):
    type: Literal["client"] = "client"
    description: str
    expects_response: bool = False
    response_timeout_secs: Annotated[int, Field(ge=1, le=120)] = 3
    parameters: dict[str, Any] = {}


class ScenarioFile(Model):
    scenario_id: Identifier
    title: str
    setting: str
    language: str
    language_label: str
    level: str
    currency: str = "MXN"
    npcs: Annotated[list[NpcSpec], Field(min_length=1)]
    asr_keywords: list[str] = []
    gestures: list[str] = []
    goals: list[GoalSpec] = []
    tools: dict[Identifier, ToolSpec] = {}

    @model_validator(mode="after")
    def _references(self):
        ids = [n.npc_id for n in self.npcs]
        if len(set(ids)) != len(ids):
            raise ValueError("Duplicate npc_id in scenario")
        names = list(ids) + [a for n in self.npcs for a in n.aliases]
        if len(set(names)) != len(names):
            raise ValueError("An alias collides with another character's id or alias")
        for npc in self.npcs:
            unknown = set(npc.tools) - set(self.tools)
            if unknown:
                raise ValueError(f"{npc.npc_id} references undefined tools: {sorted(unknown)}")
        known = set(ids) | {"any"}
        for goal in self.goals:
            if goal.npc_id not in known:
                raise ValueError(f"Goal {goal.id} references unknown npc {goal.npc_id}")
        return self


# --------------------------------------------------------------------------- run state

@dataclass
class RunState:
    """What the learner has ordered during one visit.

    Shared across characters: Maria takes the order, and Luis is told about it so he
    can comment on it. Keyed by run, not by conversation, because each character has
    its own conversation.
    """

    items: list[str] = field(default_factory=list)
    total_mxn: int = 0
    to_go: bool | None = None
    bill_shown: bool = False

    def order_summary_es(self, menu: Menu) -> str:
        """The ``{{user_order}}`` dynamic variable, in Spanish."""
        if not self.items:
            return "nada"
        counts: dict[str, int] = {}
        for item in self.items:
            counts[item] = counts.get(item, 0) + 1
        parts = []
        for item_id, count in counts.items():
            name = menu.items[item_id].name_es if item_id in menu.items else item_id
            parts.append(name if count == 1 else f"{spanish_number(count)} {name}")
        return ", ".join(parts)


@dataclass(frozen=True)
class ToolOutcome:
    """What a tool call produced.

    ``result`` goes back to the agent, ``action`` goes on to the game client. A tool
    that expects no response still yields an action.
    """

    result: str | None = None
    action: dict[str, Any] | None = None
    is_error: bool = False


# --------------------------------------------------------------------------- the pack

class ScenePack:
    """A loaded scenario: its cast, its menu and the actions its characters can take."""

    def __init__(self, scenario: ScenarioFile, menu: Menu, prompts: dict[str, str],
                 root: Path | None = None):
        self.scenario = scenario
        self.menu = menu
        self.prompts = prompts
        self.root = root
        self.npcs = {npc.npc_id: npc for npc in scenario.npcs}
        self.by_any_name = {**{npc.npc_id: npc.npc_id for npc in scenario.npcs},
                            **{alias: npc.npc_id
                               for npc in scenario.npcs for alias in npc.aliases}}

    def resolve(self, npc_id: str) -> str:
        """Map whatever the client called a character to this scenario's id."""
        return self.by_any_name.get(npc_id, npc_id)

    # -- loading ----------------------------------------------------------------

    @classmethod
    def load(cls, directory: str | Path) -> ScenePack:
        root = Path(directory)
        scenario = ScenarioFile.model_validate_json((root / "scenario.json").read_text())
        menu = Menu.model_validate_json((root / "menu.json").read_text())
        prompts: dict[str, str] = {}
        for npc in scenario.npcs:
            text = (root / npc.prompt_file).read_text()
            prompts[npc.npc_id] = text.replace("[[MENU]]", cls.menu_markdown(menu))
        return cls(scenario, menu, prompts, root)

    @staticmethod
    def menu_markdown(menu: Menu) -> str:
        """The menu as the characters see it: prices in words, sold-out marked."""
        lines = []
        for item in menu.items.values():
            price = f"{spanish_number(item.price_mxn)} pesos"
            suffix = " (HOY NO HAY, se acabó)" if not item.available else ""
            lines.append(f"- {item.name_es}: {price}{suffix}")
        return "\n".join(lines)

    # -- pricing ----------------------------------------------------------------

    def price(self, item_ids: list[str]) -> int:
        return sum(self.menu.items[i].price_mxn for i in item_ids if i in self.menu.items)

    def _valid_ids(self) -> str:
        return ", ".join(sorted(i for i, v in self.menu.items.items() if v.available))

    # -- dispatch ---------------------------------------------------------------

    def dispatch(self, npc_id: str, tool: str, params: Any, state: RunState) -> ToolOutcome:
        """Run one tool call. Never raises."""
        try:
            if not isinstance(params, dict):
                params = {}
            handler = {
                "serve_order": self._serve_order,
                "show_bill": self._show_bill,
                "play_gesture": self._play_gesture,
            }.get(tool)
            if handler is None:
                return ToolOutcome(result=f"Unknown tool '{tool}'. Continue the "
                                          f"conversation without it.", is_error=True)
            return handler(npc_id, params, state)
        except Exception:  # a tool must never take the conversation down with it
            return ToolOutcome(result="That did not work. Carry on naturally.", is_error=True)

    def _serve_order(self, npc_id: str, params: dict, state: RunState) -> ToolOutcome:
        raw = params.get("items")
        if isinstance(raw, str):  # some models send "concha,concha" instead of a list
            raw = [part.strip() for part in raw.split(",")]
        if not isinstance(raw, list) or not raw:
            return ToolOutcome(
                result=f"No items were given. Ask what they would like. Valid ids: "
                       f"{self._valid_ids()}.", is_error=True)

        items = [str(i).strip().lower() for i in raw][:MAX_ITEMS_PER_ORDER]
        unknown = [i for i in items if i not in self.menu.items]
        if unknown:
            return ToolOutcome(
                result=f"Unknown item {unknown[0]!r}. Valid ids: {self._valid_ids()}. "
                       f"Ask the customer to choose again.", is_error=True)

        unavailable = [i for i in items if not self.menu.items[i].available]
        if unavailable:
            name = self.menu.items[unavailable[0]].name_es
            return ToolOutcome(
                result=f"{name} is sold out today. Tell the customer and offer "
                       f"something else. Do not call serve_order again until they choose.",
                is_error=True)

        if isinstance(params.get("to_go"), bool):
            state.to_go = params["to_go"]

        # The call declares the WHOLE order, so it replaces rather than adds. Models
        # re-call this across consecutive turns while confirming; appending would
        # silently double the order and the bill. Replacing makes a repeat harmless
        # and still gets the total right when the customer adds something.
        repeat = sorted(items) == sorted(state.items)
        state.items = list(items)
        state.total_mxn = self.price(state.items)
        total_words = spanish_number(state.total_mxn)

        if repeat:  # already on the table: no second cup, no second charge
            return ToolOutcome(
                result=(f"That order is already placed. The total is still "
                        f"{total_words} pesos ({state.total_mxn} MXN). Do not call "
                        f"serve_order again for it."))

        served = ", ".join(self.menu.items[i].name_es for i in items)
        return ToolOutcome(
            result=(f"Served: {served}. Total: {total_words} pesos "
                    f"({state.total_mxn} MXN). Do not say the total unless the "
                    f"customer asks."),
            action={"action": "serve_order", "npc_id": npc_id, "items": items,
                    "to_go": state.to_go, "total_mxn": state.total_mxn,
                    "total_words_es": total_words})

    def _show_bill(self, npc_id: str, params: dict, state: RunState) -> ToolOutcome:
        if not state.items:
            return ToolOutcome(
                result="Nothing has been ordered yet, so there is no bill. Ask what "
                       "they would like.", is_error=True)
        state.bill_shown = True
        total_words = spanish_number(state.total_mxn)
        return ToolOutcome(
            result=f"The bill is {total_words} pesos ({state.total_mxn} MXN). Say it in words.",
            action={"action": "show_bill", "npc_id": npc_id, "total_mxn": state.total_mxn,
                    "total_words_es": total_words,
                    "items": list(state.items)})

    def _play_gesture(self, npc_id: str, params: dict, state: RunState) -> ToolOutcome:
        gesture = str(params.get("gesture", "")).strip().lower()
        allowed = self.scenario.gestures
        if allowed and gesture not in allowed:
            # Non-blocking tool: the agent is not waiting, so just drop it.
            return ToolOutcome(is_error=True)
        return ToolOutcome(action={"action": "play_gesture", "npc_id": npc_id,
                                   "gesture": gesture})


def default_pack_dir() -> Path:
    import os
    return Path(os.getenv("SCENARIO_PACK_DIR", "scenarios/cafe_cancun"))
