# Scenar.io API

Python 3.11+, managed with [uv](https://docs.astral.sh/uv/). Run from the repository root:

```sh
uv sync                      # creates .venv and installs the locked dependencies
cp orchestrator/.env.example orchestrator/.env   # then fill in the values
uv run python -m orchestrator
```

`uv sync` reads `pyproject.toml` and `uv.lock`, so everyone gets the same versions.
`uv run` re-syncs automatically before executing, and needs no activated virtualenv.

Without uv, the generated exports still work:

```sh
python3 -m venv .venv
.venv/bin/python -m pip install -r orchestrator/requirements-dev.txt
.venv/bin/python -m orchestrator
```

Configuration comes from `orchestrator/.env`, or from the environment, which takes
precedence:

```sh
export OPENAI_API_KEY='your-key'
export SCENARIO_MODEL='your-structured-output-capable-model'
export ELEVENLABS_API_KEY='your-key'
export AGENT_ID_LUIS='your-teammates-agent-id'
# Optional: export AGENT_ID_MARIANA='another-agent-id'
```

The server listens on `127.0.0.1:8765`. Interactive documentation: `/docs`;
OpenAPI schema: `/openapi.json`; readiness flags: `/health`. Running `python -m orchestrator` loads `orchestrator/.env`; exported environment
variables take precedence. Never commit credentials. Direct Uvicorn startup requires exported variables. This is a local development API, without
public authentication or cross-origin browser access configured.

## Prompt → structured scenario and response

```sh
curl http://127.0.0.1:8765/v1/scenarios \
  -H 'Content-Type: application/json' \
  -d '{"prompt":"Order coffee in Cancún, then chat with a friend","language":"Mexican Spanish","level":"A2"}'
```

Returns `{scenario_id, scenario, response}`. The scenario includes title, setting,
language, CEFR level, characters (id, name, role, instructions, opening_line), and
goals (id, description, evidence_required, npc_id, core). `response` introduces the
exercise to the learner; it is not generated speech. Fetch the saved result using
`GET /v1/scenarios/{scenario_id}`. Local JSON files live in `runs/scenarios` or
`SCENARIO_DIR`. IDs are UUIDs; persistence survives restarts.

Generation uses the OpenAI Responses API with Pydantic structured output, following
[official documentation](https://developers.openai.com/api/docs/guides/structured-outputs).
The server rejects invalid references, changed language/level, incomplete outputs,
and refusals rather than returning fabricated scenarios. It does not create Unity
assets or provision ElevenLabs agents.

## Speech → speech

```sh
curl -X POST 'http://127.0.0.1:8765/v1/speech?session_id=de305d54-75b4-431b-adb2-eb6b9e546014&npc_id=luis' \
  -H 'Content-Type: audio/wav' --data-binary @input.wav --output reply.audio
```

Use the returned Content-Type to decode the audio. Reuse a client-generated
`session_id` UUID for conversation continuity. Supply `scenario_id` to load a
saved scenario and validate the NPC. Add `response_format=json` to receive
`audio_base64`, `media_type`, `sample_rate`, `user_transcript`, `agent_transcript`,
`session_id`, and `npc_id`. Transcripts may be null.

Supported media types: audio/wav, audio/x-wav, audio/mpeg, audio/webm, audio/ogg,
audio/mp4, audio/pcm. Raw PCM is signed PCM16 little-endian mono and requires the
`sample_rate` query parameter (8000–48000). Containers are decoded/validated by
the speech adapter. Each request/response is at most 10 MiB; scenario requests
are at most 32 KiB. Provider calls time out after 60 seconds. These are complete
HTTP turns, not realtime streaming or interruption support.

### Built-in ElevenLabs integration

The adapter is selected automatically when `ELEVENLABS_API_KEY` is set. Configure
`AGENT_ID_LUIS`, `AGENT_ID_MARIANA`, or `AGENT_ID_<NPC_ID>` for each NPC used by the
client. For beginner Luis, set `AGENT_ID_LUIS_BEGINNER` and use `npc_id=luis_beginner`.
No agent IDs are hardcoded. `SMOKE_AGENT_ID` is supported as a fallback for Luis.
A generated scenario must use character IDs that have configured agents.

The flow adapts the scripts from commit `71b8d80`: uploaded audio is transcribed
using ElevenLabs Scribe (`ELEVENLABS_STT_MODEL`, default `scribe_v2`), then the
transcript is sent as `user_message` through the signed-URL voice-agent socket.
The built-in adapter returns PCM wrapped in WAV (`audio/wav`), so save it as
`reply.wav`. This introduces a separate transcription request and is not realtime
microphone streaming. See the official [transcription API](https://elevenlabs.io/docs/api-reference/speech-to-text/convert)
and [agent WebSocket protocol](https://elevenlabs.io/docs/eleven-agents/libraries/web-sockets).

Agent output must be PCM; its sample rate is read from session metadata. Enable
agent_response and audio client events. The initial greeting is drained before
submitting the user's turn. Reply completion follows the teammate's quiet-period
approach: both text and audio must arrive, then audio must go quiet for 1.4 seconds.
Unusually long gaps between audio chunks can still truncate a reply; a live check
with the configured agent is required. Scene tool calls receive explicit error
results because scene actions are outside this API.

Connections retain conversation memory per session UUID and are closed when the
NPC/scenario changes, on error/cancellation, after 120 seconds idle, or on shutdown.
Overlapping turns for one session are rejected with 422. Up to 64 sessions are
retained. Use one server worker; session memory is process-local and is lost on
restart or expiry. Close a session immediately when the user finishes:

```sh
curl -X DELETE http://127.0.0.1:8765/v1/speech/sessions/YOUR_SESSION_UUID
```

Saved scenario content is sent as a contextual update to the configured agent;
it does not override that agent's system prompt, voice, or language configuration.
The standalone teammate scripts remain unchanged. The beginner demo modifies the
remote agent speed when invoked; the API adapter does not modify agent settings.

### Optional custom speech adapter

Implement a zero-argument synchronous factory returning an object with:

```python
from orchestrator.speech import SpeechInput, SpeechOutput

class VoiceProvider:
    async def respond(self, request: SpeechInput) -> SpeechOutput:
        # Implement transcription/conversation/voice generation here.
        ...

    async def aclose(self):
        # Optional: clean up sessions/connections at server shutdown.
        ...

def create_provider():
    return VoiceProvider()
```

Set `SPEECH_ADAPTER=package.module:create_provider`. Input contains audio bytes,
media type, session UUID, NPC ID, optional scenario UUID and validated scenario
dictionary, and optional PCM sample rate. Return SpeechOutput containing bytes,
media type, sample rate when PCM, and optional transcripts. The provider owns
session memory, concurrency/ordering per session, expiration, decoding, provider
credentials, and cancellation cleanup. It must not block the event loop.

The API does not import the ElevenLabs SDK. Without ElevenLabs credentials or a
custom adapter, speech returns 503. Unknown/unconfigured NPC agents also return
503. `SPEECH_ADAPTER` overrides the built-in implementation when set. Tests mock
provider HTTP/WebSocket responses; they do not represent live voice validation.

## Errors and checks

Errors use `{"detail": ...}`. Statuses: 404 unknown scenario; 413 oversized body;
415 unsupported content type; 422 invalid inputs; 503 missing provider configuration;
502 provider failure/refusal/invalid output; 504 provider deadline exceeded.
Provider exception details are not returned to clients.

```sh
uv run pytest
```

Tests require no credentials and cover persistence, scenario-to-speech handoff,
validation, missing configuration, audio responses, timeouts, and provider errors.
Live provider checks require an ElevenLabs key and configured agent IDs.
The older plan's `eval/run_eval.py` director evaluator is not implemented: scoring,
tutoring, the Unity WebSocket bridge, and realtime voice are outside these two
HTTP endpoints.
