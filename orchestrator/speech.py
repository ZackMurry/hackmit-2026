"""Stable boundary for the separately owned ElevenLabs implementation."""

from dataclasses import dataclass
from importlib import import_module
from typing import Any, Protocol
from uuid import UUID


SUPPORTED_AUDIO_TYPES = frozenset({
    "audio/wav", "audio/x-wav", "audio/mpeg", "audio/webm", "audio/ogg",
    "audio/mp4", "audio/pcm",
})


@dataclass(frozen=True)
class SpeechInput:
    audio: bytes
    media_type: str
    session_id: UUID
    npc_id: str
    scenario_id: UUID | None = None
    scenario: dict[str, Any] | None = None
    # Required for raw signed PCM16 little-endian mono; absent for containers.
    sample_rate: int | None = None


@dataclass(frozen=True)
class SpeechOutput:
    audio: bytes
    media_type: str
    sample_rate: int | None = None
    user_transcript: str | None = None
    agent_transcript: str | None = None


class SpeechProvider(Protocol):
    async def respond(self, request: SpeechInput) -> SpeechOutput:
        """Return one complete spoken reply; propagate cancellation on timeout.

        Own session memory keyed by session_id, NPC switching, audio decoding,
        transcription, provider calls, and resource/session cleanup. Never block
        the event loop. The API supplies the validated scenario if one is selected.
        """
        ...


def load_speech_provider(path: str) -> SpeechProvider:
    """Load an operator-configured, zero-argument factory: package.module:create."""
    module, separator, factory = path.partition(":")
    if not separator or not module or not factory:
        raise ValueError("SPEECH_ADAPTER must be package.module:factory")
    provider = getattr(import_module(module), factory)()
    if not callable(getattr(provider, "respond", None)):
        raise TypeError("Speech adapter must provide async respond(request)")
    return provider


class SpeechInputError(ValueError):
    """Audio or session input cannot be processed (safe client-facing message)."""


class SpeechUnavailable(RuntimeError):
    """Requested voice configuration is unavailable (safe client-facing message)."""
