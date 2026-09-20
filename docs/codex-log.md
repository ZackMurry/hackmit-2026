# Agent-assisted work

## 2026-09-19 — API endpoints

Request: implement speech-in/speech-out and prompt-to-structured-scenario endpoints,
leaving ElevenLabs implementation and Unity to teammates. User approved FastAPI,
Uvicorn, Pydantic, OpenAI SDK, pytest, and httpx dependencies.

Result: added a local HTTP API, validated scenario generation and persistence,
a pluggable speech adapter contract, configuration examples, endpoint documentation,
and provider-independent tests. Installed dependencies in the repository `.venv`.
No Unity changes or ElevenLabs provider implementation. Human changes: none known.

Validation: 17 endpoint/validation tests passed initially; final checks recorded in
the task response. No live provider calls were made. The original director eval
script does not exist and scoring is outside the requested endpoint scope.

## 2026-09-19 — ElevenLabs integration

Request: integrate the latest teammate commit with the existing API. Fast-forwarded
main to `71b8d80` (Add ElevenLabs voice test scripts), preserving the local API work
and combining `.env.example` settings. Kept both teammate scripts and Unity unchanged.
User approved websockets and python-dotenv; installed them in `.venv`.

Adapted the signed-URL/text-message/PCM flow into a server adapter, preceded by
Scribe upload transcription. Added automatic provider selection, dotenv loading
in the module entry point, session reuse/expiry/close, error cleanup, and WAV output.
Documented the quiet-period completion limitation and scenario context semantics.
Validation: 24 tests pass, including mocked HTTP and WebSocket integration tests.
No live ElevenLabs requests or remote agent mutations were performed. The older
plan's director eval script is absent. Human changes: none known.

## 2026-09-19 — Goal tracking and grading

Request: keep track of goals, and add an endpoint that grades a conversation against
them from 1 to 10, where the number rates how completely the whole goal set was hit.
Asked whether tracking belonged inside `POST /v1/scenarios`; answered no, because a
scenario is a reusable template and progress belongs to a run, and built it on the
existing `run_id` instead.

Result: `orchestrator/grading.py` with a `GraderProvider` protocol, an `OpenAIGrader`
using Responses structured outputs, and a JSON Lines `TranscriptStore`. `/v1/speech`
now appends each turn's transcripts and scene actions to the run, best effort, so
recording can never fail a paid speech turn. Added `GET /v1/runs/{run_id}` and
`POST /v1/grade`, which resolves its rubric from inline goals, a saved scenario or
the loaded pack, and its conversation from an inline transcript or a recorded run.
Generated and authored goal shapes are normalised by `rubric_goals` rather than
migrating either existing contract. A grade that awards a goal without quoting the
learner, or that judges the wrong goal set, is rejected as a provider failure.

Not done: the build doc's per-turn director, live goal ticks, actor steering, and
any Unity end card. Human changes: none known.

Validation: 83 tests pass, including grade rejection, run recording, path-traversal
refusal in the store, and both goal shapes normalising. No live provider calls were
made; the grader is exercised through doubles.
