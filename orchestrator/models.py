from typing import Annotated, Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, model_validator

Text = Annotated[str, Field(min_length=1, max_length=4000)]
Identifier = Annotated[str, Field(pattern=r"^[a-z][a-z0-9_]{0,63}$")]


class Model(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)


class ScenarioRequest(Model):
    prompt: Text
    language: Annotated[str, Field(min_length=2, max_length=80)] = "Mexican Spanish"
    level: Literal["A1", "A2", "B1", "B2", "C1", "C2"] = "A2"


class Character(Model):
    id: Identifier
    name: Text
    role: Text
    instructions: Text
    opening_line: Text


class Goal(Model):
    id: Identifier
    description: Text
    evidence_required: Text
    npc_id: Identifier
    core: bool


class Scenario(Model):
    title: Text
    setting: Text
    language: Text
    level: Literal["A1", "A2", "B1", "B2", "C1", "C2"]
    characters: Annotated[list[Character], Field(min_length=1, max_length=6)]
    goals: Annotated[list[Goal], Field(min_length=1, max_length=12)]

    @model_validator(mode="after")
    def references(self):
        characters = [c.id for c in self.characters]
        goals = [g.id for g in self.goals]
        if len(set(characters)) != len(characters) or len(set(goals)) != len(goals):
            raise ValueError("Character and goal IDs must be unique")
        if any(g.npc_id not in characters for g in self.goals):
            raise ValueError("Every goal must reference an existing character")
        return self


class GeneratedScenario(Model):
    scenario: Scenario
    response: Text


class ScenarioResponse(GeneratedScenario):
    scenario_id: UUID


# --------------------------------------------------------------------------- grading

# Goal ids come from two places with different conventions: generated scenarios use
# snake_case, the authored pack uses "G1". The rubric accepts both.
GoalId = Annotated[str, Field(min_length=1, max_length=64, pattern=r"^[A-Za-z][A-Za-z0-9_-]*$")]
# "any" is a real npc_id in the authored pack: a goal that counts with any character.
RubricNpcId = Annotated[str, Field(pattern=r"^[a-z][a-z0-9_]{0,63}$")]


class RubricGoal(Model):
    """One goal as the grader sees it, whoever authored it.

    `Goal` (generated) and `scene.GoalSpec` (authored) describe the same thing with
    different field names. Normalising here means the grader has one input shape and
    neither existing contract has to change.
    """

    id: GoalId
    npc_id: RubricNpcId
    core: bool
    label: Text
    evidence_required: Text


class TranscriptTurn(Model):
    """One line of a run, in the order it happened.

    `event` lines are not speech: they are things the game did (a tool call the
    character triggered), recorded so the grader can cross-check a claim against what
    actually happened instead of trusting the words alone.
    """

    role: Literal["learner", "npc", "event"]
    npc_id: Identifier
    text: Text


class GradeRequest(Model):
    # The rubric: inline goals win, then a saved scenario, then the loaded pack.
    goals: Annotated[list[RubricGoal], Field(max_length=12)] | None = None
    scenario_id: UUID | None = None
    # The conversation: inline turns win, then whatever the server recorded for the run.
    transcript: Annotated[list[TranscriptTurn], Field(max_length=400)] | None = None
    run_id: Annotated[str, Field(max_length=64, pattern=r"^[A-Za-z0-9_-]+$")] | None = None


class GoalResult(Model):
    goal_id: GoalId
    achieved: bool
    # The learner's exact words that earned it. No quote, no goal.
    evidence_quote: Text | None
    note: Text


class Grade(Model):
    # How completely the whole goal set was hit: 10 is every goal, 1 is none.
    overall: Annotated[int, Field(ge=1, le=10)]
    goals: Annotated[list[GoalResult], Field(max_length=12)]
    summary: Text


class GradeResponse(Grade):
    scenario_id: UUID | None = None
    run_id: str | None = None
    goals_achieved: int
    goals_total: int
