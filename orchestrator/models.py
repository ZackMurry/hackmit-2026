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

# Goal ids are snake_case quest ids in both the authored pack and generated scenarios
# (the client prints each score on the quest line with the same id), but the rubric
# accepts anything identifier-shaped so a hand-written "G1" still grades.
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


class SceneNote(Model):
    """Something the game tells one character about the scene, outside any turn: who
    has just walked up, whose turn it is to speak. Keyed so a later note on the same
    subject replaces the earlier one; an empty text forgets the subject."""

    npc_id: Identifier
    key: Identifier
    text: Annotated[str, Field(max_length=2000)] = ""


class GoalTick(Model):
    """A goal met during the run, as the director saw it. No quote, no tick."""

    id: GoalId
    evidence_quote: Text


class Verdict(Model):
    """What the director returns after a turn: the open goals now met, if any."""

    achieved: Annotated[list[GoalTick], Field(max_length=12)]


# The scale the client prints. Ordered worst to best; grading.py turns it into points.
LetterGrade = Literal["F", "D-", "D", "D+", "C-", "C", "C+", "B-", "B", "B+", "A-", "A", "A+"]


class GoalResult(Model):
    """One line of the receipt: a goal, its grade, and a word from the tutor."""

    id: GoalId
    # None means the goal never came up (never spoke to that character, the situation
    # never arose), which is not the same as failing it.
    grade: LetterGrade | None
    # One sentence for the learner: what earned the grade, or what would have.
    comment: Text
    # The learner's exact words behind a passing grade. No quote, no pass.
    evidence_quote: Text | None


class Grade(Model):
    """What the grader returns; the overall grade is derived, not asked for."""

    scores: Annotated[list[GoalResult], Field(max_length=12)]
    summary: Text


class GradeResponse(Grade):
    # Weighted mean of the graded goals (core goals count double). An unattempted core
    # goal counts as an F; an unattempted optional one is left out.
    overall: LetterGrade
    scenario_id: UUID | None = None
    run_id: str | None = None
    goals_passed: int
    goals_total: int
