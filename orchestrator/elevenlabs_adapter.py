"""HTTP-turn adapter based on the signed-URL flow in tools/demo_beginner.py.

Uploads are transcribed with ElevenLabs Scribe, then sent as user_message to the
existing voice agent. The scripts remain standalone; importing them would load
credentials and (for the beginner demo) mutate shared agent configuration.
"""
from __future__ import annotations

import asyncio
import base64
import io
import json
import os
import time
import wave
from contextlib import suppress
from dataclasses import dataclass, field
from uuid import UUID

import httpx

from .speech import SpeechInput, SpeechOutput, SpeechInputError, SpeechUnavailable

MAX_REPLY = 10 * 1024 * 1024 - 44  # leave room for a WAV header


def wav_bytes(pcm: bytes, rate: int) -> bytes:
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(rate)
        output.writeframes(pcm)
    return buffer.getvalue()


@dataclass
class Session:
    lock: asyncio.Lock = field(default_factory=asyncio.Lock)
    queue: asyncio.Queue = field(default_factory=lambda: asyncio.Queue(maxsize=256))
    ws: object | None = None
    reader: asyncio.Task | None = None
    context: tuple | None = None
    rate: int | None = None
    touched: float = field(default_factory=time.monotonic)


class ElevenLabsSpeech:
    def __init__(self, key: str, agents: dict[str, str], *, stt_model="scribe_v2",
                 client=None, connect=None, quiet=1.4, idle_seconds=120):
        self.agents = agents
        self.stt_model = stt_model
        self.http = client or httpx.AsyncClient(
            base_url="https://api.elevenlabs.io", headers={"xi-api-key": key}, timeout=30)
        self.connect = connect
        self.quiet = quiet
        self.idle_seconds = idle_seconds
        self.sessions: dict[UUID, Session] = {}
        self.reaper = None

    async def _transcribe(self, request: SpeechInput) -> str:
        data, media = request.audio, request.media_type
        extensions = {"audio/wav": "wav", "audio/x-wav": "wav", "audio/mpeg": "mp3",
                      "audio/webm": "webm", "audio/ogg": "ogg", "audio/mp4": "m4a"}
        if media == "audio/pcm":
            if not request.sample_rate or len(data) % 2:
                raise SpeechInputError("PCM requires whole samples and a sample rate")
            data, media = wav_bytes(data, request.sample_rate), "audio/wav"
        if media in {"audio/wav", "audio/x-wav"}:
            try:
                with wave.open(io.BytesIO(data), "rb") as audio:
                    if audio.getnframes() == 0:
                        raise ValueError("Empty WAV")
            except (wave.Error, EOFError, ValueError):
                raise SpeechInputError("Upload a valid nonempty PCM WAV file") from None
        result = await self.http.post("/v1/speech-to-text", data={
            "model_id": self.stt_model, "tag_audio_events": "false", "diarize": "false"},
            files={"file": ("input." + extensions[media], data, media)})
        if result.status_code in {400, 422}:
            raise SpeechInputError("ElevenLabs could not transcribe this audio")
        result.raise_for_status()
        text = result.json().get("text", "").strip()
        if not text:
            raise SpeechInputError("No speech was detected")
        return text

    async def _pump(self, session: Session):
        try:
            async for raw in session.ws:
                message = json.loads(raw)
                kind = message.get("type")
                if kind == "ping":
                    # Answer promptly; don't let ping delays block audio consumption.
                    await session.ws.send(json.dumps({"type": "pong",
                        "event_id": message["ping_event"]["event_id"]}))
                elif kind == "client_tool_call":
                    event = message["client_tool_call"]
                    await session.ws.send(json.dumps({"type": "client_tool_result",
                        "tool_call_id": event["tool_call_id"], "is_error": True,
                        "result": "Scene actions are not implemented by this HTTP API."}))
                else:
                    session.queue.put_nowait(message)
        except asyncio.CancelledError:
            raise
        except Exception:
            pass  # Closed/invalid sockets are surfaced by the sentinel below.
        finally:
            if session.queue.full():
                session.queue.get_nowait()
            session.queue.put_nowait(None)

    async def _turn(self, session: Session, *, greeting=False):
        audio, text = bytearray(), ""
        deadline = time.monotonic() + (10 if greeting else 40)
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                if greeting and not audio and not text:
                    return b"", ""
                raise TimeoutError("No complete agent turn")
            # Only declare a reply complete after both text and audio arrive.
            wait = min(self.quiet, remaining) if audio and text else remaining
            try:
                message = await asyncio.wait_for(session.queue.get(), wait)
            except TimeoutError:
                if audio and text:
                    break
                if greeting and not audio and not text:
                    return b"", ""
                raise
            if message is None:
                if audio and text:
                    break
                raise RuntimeError("Agent connection closed before reply")
            kind = message.get("type")
            if kind == "conversation_initiation_metadata":
                fmt = message["conversation_initiation_metadata_event"]["agent_output_audio_format"]
                if not fmt.startswith("pcm_") or not fmt[4:].isdigit():
                    raise ValueError("Agent must use PCM output")
                session.rate = int(fmt[4:])
                if not 8000 <= session.rate <= 48000:
                    raise ValueError("Unsupported agent sample rate")
            elif kind == "agent_response":
                text = message["agent_response_event"]["agent_response"]
            elif kind == "agent_response_correction":
                text = message["agent_response_correction_event"]["corrected_agent_response"]
            elif kind == "audio":
                audio.extend(base64.b64decode(message["audio_event"]["audio_base_64"], validate=True))
                if len(audio) > MAX_REPLY:
                    raise ValueError("Agent reply exceeds audio limit")
            elif kind == "interruption":
                audio.clear()
                text = ""
            elif kind in {"error", "conversation_error"}:
                raise RuntimeError("Agent reported an error")
        if session.rate is None or len(audio) % 2:
            raise ValueError("Missing or invalid PCM metadata")
        return bytes(audio), text

    async def _close(self, session: Session):
        if session.reader:
            session.reader.cancel()
            with suppress(asyncio.CancelledError):
                await session.reader
        if session.ws:
            with suppress(Exception):
                await session.ws.close()
        session.ws = session.reader = None
        session.queue = asyncio.Queue(maxsize=256)
        session.rate = None

    async def _open(self, session: Session, agent: str, request: SpeechInput):
        result = await self.http.get("/v1/convai/conversation/get-signed-url", params={"agent_id": agent})
        result.raise_for_status()
        connect = self.connect
        if connect is None:
            from websockets.asyncio.client import connect
        session.ws = await connect(result.json()["signed_url"], max_size=16 * 1024 * 1024)
        session.reader = asyncio.create_task(self._pump(session))
        await session.ws.send(json.dumps({"type": "conversation_initiation_client_data",
            "dynamic_variables": {"learner_name": "amigo", "user_order": "nada"}}))
        # The teammate's agents greet first; exclude that greeting from the reply.
        await self._turn(session, greeting=True)
        if session.rate is None:
            raise ValueError("Agent did not negotiate its output format")
        if request.scenario:
            await session.ws.send(json.dumps({"type": "contextual_update", "text":
                "Current practice scenario and active character " + request.npc_id + ": " +
                json.dumps(request.scenario, ensure_ascii=False)}))

    async def respond(self, request: SpeechInput) -> SpeechOutput:
        agent = self.agents.get(request.npc_id)
        if not agent:
            raise SpeechUnavailable("No ElevenLabs agent configured for this NPC")
        if self.reaper is None:
            self.reaper = asyncio.create_task(self._reap())
        if request.session_id not in self.sessions:
            if len(self.sessions) >= 64:
                raise SpeechUnavailable("Voice session capacity reached; close an idle session")
            self.sessions[request.session_id] = Session()
        session = self.sessions[request.session_id]
        # Reject overlaps instead of queuing turns that may arrive out of order.
        if session.lock.locked():
            raise SpeechInputError("A speech turn is already running for this session")
        async with session.lock:
            try:
                text = await self._transcribe(request)
                context = (request.npc_id, request.scenario_id)
                if session.ws is None or session.context != context or session.reader.done():
                    await self._close(session)
                    await self._open(session, agent, request)
                    session.context = context
                # Discard unsolicited idle messages before sending the next turn.
                while not session.queue.empty():
                    session.queue.get_nowait()
                await session.ws.send(json.dumps({"type": "user_message", "text": text}))
                audio, response = await self._turn(session)
                return SpeechOutput(wav_bytes(audio, session.rate), "audio/wav", session.rate, text, response)
            except BaseException:
                # Also close on request cancellation/timeout to avoid stale replies.
                await self._close(session)
                self.sessions.pop(request.session_id, None)
                raise
            finally:
                session.touched = time.monotonic()

    async def end_session(self, session_id: UUID):
        session = self.sessions.get(session_id)
        if session:
            if session.lock.locked():
                raise SpeechInputError("A speech turn is already running for this session")
            self.sessions.pop(session_id, None)
            await self._close(session)

    async def _reap(self):
        while True:
            await asyncio.sleep(min(10, self.idle_seconds))
            for key, session in list(self.sessions.items()):
                if not session.lock.locked() and time.monotonic() - session.touched > self.idle_seconds:
                    self.sessions.pop(key, None)
                    await self._close(session)

    async def aclose(self):
        if self.reaper:
            self.reaper.cancel()
            with suppress(asyncio.CancelledError):
                await self.reaper
        for session in list(self.sessions.values()):
            await self._close(session)
        self.sessions.clear()
        await self.http.aclose()


def create_provider() -> ElevenLabsSpeech:
    agents = {key[len("AGENT_ID_"):].lower(): value for key, value in os.environ.items()
              if key.startswith("AGENT_ID_") and value}
    if "luis" not in agents and os.getenv("SMOKE_AGENT_ID"):
        agents["luis"] = os.environ["SMOKE_AGENT_ID"]
    return ElevenLabsSpeech(os.environ["ELEVENLABS_API_KEY"], agents,
                           stt_model=os.getenv("ELEVENLABS_STT_MODEL", "scribe_v2"))
