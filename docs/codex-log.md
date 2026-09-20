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

## 2026-09-19 — Director eval, richer verdict, hints and the feedback report

Honesty note first: this entry records work done by a Claude Code agent (Anthropic), not
by Codex. The master doc (§18.5 C12) plans for Codex to write the eval and produce the
before-and-after table; that has not happened. Nothing below should be presented as
Codex's work. The eval and its numbers are real and reproducible, so a Codex job can be
baselined against them.

Request: build the "brain" lane of §18.5: C5 (director fast, richer, steering), C9/C10
(pronunciation impressions and the written feedback report), a hints endpoint, and the
C11 eval with its zero-false-award gate.

Result: `orchestrator/director.py` now sends a cache-stable request (instructions and
full rubric, one message per turn, open goals last), runs at `reasoning.effort=none`
with a 4 s timeout, logs mistakes, learner state, counters and a moderation flag beside
the run, enforces in code that every quote is the learner's own words, and delivers at
most one `[DIRECTOR]` note per three learner turns through `on_note`. New:
`orchestrator/coach.py` and `POST /v1/hint`, `orchestrator/feedback.py` and
`POST /v1/feedback` (JSON plus a self-contained "What we heard" page, with
`gpt-audio-1.5` pronunciation impressions that can never fail the report), both routed
from `orchestrator/brain_routes.py`. `eval/` holds 40 hand-written cases (22
adversarial) and a runner that drives the real `OpenAIDirector.review` path.

Eval, 40 cases x 3, live (full table and method in `eval/README.md`):

| Model / effort | False awards | Recall | p50 | p95 |
| --- | --- | --- | --- | --- |
| gpt-5.6-luna / none (default) | 0 | 0.96 | 1.30 s | 2.14 s |
| gpt-5.6-luna / low | 0 | 0.91 | 1.79 s | 2.90 s |
| gpt-5.4-nano / none | 2 | 0.75 | 1.12 s | 1.70 s |
| gpt-5.4-nano / low | 0 | 0.96 | 1.41 s | 2.15 s |

What the eval changed: the first luna/none run had one reproducible false award (price
asked after Maria had already given it). Prompt wording alone did not fix it; adding a
`check` and a `holds` field to each tick in the wire schema did, and a follow-up wording
fix recovered the recall that change cost. About 98% of input tokens are served from the
prompt cache. There is no "before" latency figure for the old director: it was never
measured, so no before-and-after speed claim is made.

Not done or not met: director p50 is 1.3 s, not the planned under 1 s. The labels in the
eval were written and checked by the same agent, not by a second person. The ElevenLabs
second-opinion analysis and latency figures in the report (C10) were not built. One
existing director test was changed on purpose: the director now keeps reviewing after
every goal is met (mistakes and steering outlive the checklist), and the test doubles'
quotes were changed to words the learner double actually says, because the new substring
rule rightly drops anything else.

Validation: `uv run pytest` passes (162 tests across all lanes at the time of writing), `uvx ruff check orchestrator
eval` is clean. Live checks made once each: a five-turn scripted run through `Director`
(ticks, states, a delivered note, mistakes), `POST /v1/hint` through the real app
(1.1 to 1.4 s), `POST /v1/feedback` including one `gpt-audio-1.5` clip (3 to 6 s).
