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
