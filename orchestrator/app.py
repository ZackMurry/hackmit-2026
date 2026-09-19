import asyncio
import base64
import os
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Literal
from uuid import UUID, uuid4

from fastapi import FastAPI, HTTPException, Query, Request
from fastapi.responses import JSONResponse, Response

from .models import GeneratedScenario, ScenarioRequest, ScenarioResponse
from .scenarios import OpenAIScenarios, ScenarioProvider, ScenarioStore
from .speech import SpeechInputError, SpeechUnavailable, SUPPORTED_AUDIO_TYPES, SpeechInput, SpeechOutput, SpeechProvider, load_speech_provider

MAX_AUDIO_BYTES = 10 * 1024 * 1024
MAX_JSON_BYTES = 32 * 1024


async def read_limited(request: Request, limit: int) -> bytes:
    body = bytearray()
    async for chunk in request.stream():
        body.extend(chunk)
        if len(body) > limit:
            raise HTTPException(413, "Request body is too large")
    return bytes(body)


async def provider_call(awaitable, timeout: float):
    try:
        async with asyncio.timeout(timeout):
            return await awaitable
    except SpeechInputError as error:
        raise HTTPException(422, str(error)) from None
    except SpeechUnavailable as error:
        raise HTTPException(503, str(error)) from None
    except TimeoutError:
        raise HTTPException(504, "Provider timed out") from None
    except Exception:
        # Provider exceptions may contain credentials or transcripts.
        raise HTTPException(502, "Provider failed or returned invalid output") from None


def create_app(*, speech: SpeechProvider | None = None,
               scenarios: ScenarioProvider | None = None,
               data_dir: Path | None = None, timeout: float = 60) -> FastAPI:
    store = ScenarioStore(data_dir or Path(os.getenv("SCENARIO_DIR", "runs/scenarios")))

    @asynccontextmanager
    async def lifespan(app):
        configured = scenarios
        if configured is None and os.getenv("OPENAI_API_KEY") and os.getenv("SCENARIO_MODEL"):
            configured = OpenAIScenarios(os.environ["OPENAI_API_KEY"], os.environ["SCENARIO_MODEL"])
        app.state.scenarios = configured
        app.state.speech = speech
        if speech is None and os.getenv("SPEECH_ADAPTER"):
            app.state.speech = load_speech_provider(os.environ["SPEECH_ADAPTER"])
        elif speech is None and os.getenv("ELEVENLABS_API_KEY"):
            from .elevenlabs_adapter import create_provider
            app.state.speech = create_provider()
        try:
            yield
        finally:
            if isinstance(configured, OpenAIScenarios):
                await configured.aclose()
            if app.state.speech is not None and hasattr(app.state.speech, "aclose"):
                await app.state.speech.aclose()

    app = FastAPI(title="Scenar.io API", version="0.1.0", lifespan=lifespan)

    @app.get("/health")
    async def health():
        return {"status": "ok", "scenarios_ready": app.state.scenarios is not None,
                "speech_ready": app.state.speech is not None}

    @app.post("/v1/scenarios", response_model=ScenarioResponse, openapi_extra={
        "requestBody": {"required": True, "content": {"application/json": {
            "schema": ScenarioRequest.model_json_schema()}}}})
    async def generate(request: Request):
        if request.headers.get("content-type", "").split(";")[0].strip() != "application/json":
            raise HTTPException(415, "Expected application/json")
        from pydantic import ValidationError
        try:
            payload = ScenarioRequest.model_validate_json(await read_limited(request, MAX_JSON_BYTES))
        except ValidationError:
            raise HTTPException(422, "Expected a nonempty prompt, language, and valid CEFR level") from None
        if app.state.scenarios is None:
            raise HTTPException(503, "Configure OPENAI_API_KEY and SCENARIO_MODEL")

        async def validated():
            generated = await app.state.scenarios.generate(payload)
            result = GeneratedScenario.model_validate(generated)
            if result.scenario.level != payload.level or result.scenario.language != payload.language:
                raise ValueError("Provider changed requested language or level")
            return result

        result = await provider_call(validated(), timeout)
        saved = ScenarioResponse(scenario_id=uuid4(), **result.model_dump())
        try:
            await asyncio.to_thread(store.save, saved)
        except OSError:
            raise HTTPException(500, "Could not save scenario") from None
        return saved

    @app.get("/v1/scenarios/{scenario_id}", response_model=ScenarioResponse)
    async def get_scenario(scenario_id: UUID):
        try:
            return await asyncio.to_thread(store.get, scenario_id)
        except FileNotFoundError:
            raise HTTPException(404, "Scenario not found") from None

    @app.delete("/v1/speech/sessions/{session_id}", status_code=204)
    async def end_speech_session(session_id: UUID):
        provider = app.state.speech
        if provider is None:
            raise HTTPException(503, "Speech adapter is not configured")
        if not hasattr(provider, "end_session"):
            raise HTTPException(501, "Speech adapter does not support ending sessions")
        await provider_call(provider.end_session(session_id), timeout)
        return Response(status_code=204)

    @app.post("/v1/speech", openapi_extra={
        "requestBody": {"required": True, "content": {media: {
            "schema": {"type": "string", "format": "binary"}}
            for media in sorted(SUPPORTED_AUDIO_TYPES)}}})
    async def respond(request: Request, session_id: UUID,
                      npc_id: str = Query(pattern=r"^[a-z][a-z0-9_]{0,63}$"),
                      scenario_id: UUID | None = None,
                      sample_rate: int | None = Query(default=None, ge=8000, le=48000),
                      response_format: Literal["audio", "json"] = "audio"):
        media_type = request.headers.get("content-type", "").split(";")[0].strip().lower()
        if media_type not in SUPPORTED_AUDIO_TYPES:
            raise HTTPException(415, "Unsupported audio Content-Type")
        if media_type == "audio/pcm" and sample_rate is None:
            raise HTTPException(422, "Raw PCM requires sample_rate (PCM16 LE mono)")
        audio = await read_limited(request, MAX_AUDIO_BYTES)
        if not audio or (media_type == "audio/pcm" and len(audio) % 2):
            raise HTTPException(422, "Audio must be nonempty; PCM16 must contain whole samples")
        context = None
        if scenario_id:
            saved = await get_scenario(scenario_id)
            if npc_id not in {c.id for c in saved.scenario.characters}:
                raise HTTPException(422, "NPC does not belong to this scenario")
            context = saved.scenario.model_dump()
        if app.state.speech is None:
            raise HTTPException(503, "Speech adapter is not configured")
        turn = SpeechInput(audio, media_type, session_id, npc_id, scenario_id, context, sample_rate)

        async def validated():
            result = await app.state.speech.respond(turn)
            if (not isinstance(result, SpeechOutput) or not isinstance(result.audio, bytes)
                    or not result.audio or len(result.audio) > MAX_AUDIO_BYTES
                    or result.media_type not in SUPPORTED_AUDIO_TYPES):
                raise ValueError("Invalid speech output")
            if result.media_type == "audio/pcm" and (
                not isinstance(result.sample_rate, int) or not 8000 <= result.sample_rate <= 48000
                or len(result.audio) % 2
            ):
                raise ValueError("Invalid PCM metadata")
            return result

        output = await provider_call(validated(), timeout)
        headers = {"X-Session-ID": str(session_id), "Cache-Control": "no-store"}
        if output.sample_rate is not None:
            headers["X-Audio-Sample-Rate"] = str(output.sample_rate)
        if response_format == "json":
            return JSONResponse({"session_id": str(session_id), "npc_id": npc_id,
                "audio_base64": base64.b64encode(output.audio).decode("ascii"),
                "media_type": output.media_type, "sample_rate": output.sample_rate,
                "user_transcript": output.user_transcript, "agent_transcript": output.agent_transcript},
                headers=headers)
        return Response(output.audio, media_type=output.media_type, headers=headers)

    return app


app = create_app()
