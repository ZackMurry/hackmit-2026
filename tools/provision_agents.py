"""Create or update the scenario's ElevenLabs agents, tools and greeting audio.

Idempotent: run it as often as you like. Everything is looked up by name, so the
second run updates in place instead of making duplicates, and the agent ids stay
stable. There is no local state file to drift out of date.

    uv run python tools/provision_agents.py            # show what would change
    uv run python tools/provision_agents.py --apply    # create/update for real
    uv run python tools/provision_agents.py --apply --write-env
    uv run python tools/provision_agents.py --apply --greetings   # also render audio

Creating agents and tools costs no conversation minutes. Rendering greetings costs
a few hundred TTS credits, once.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from dotenv import load_dotenv  # noqa: E402

from orchestrator.scene import ScenePack, default_pack_dir  # noqa: E402

load_dotenv(ROOT / "orchestrator" / ".env")
API = "https://api.elevenlabs.io"
KEY = os.environ.get("ELEVENLABS_API_KEY", "")

# The agent only emits event types that are switched on, and the default set is not
# documented. client_tool_call in particular must be present or no scene action ever
# reaches us; the others are what the adapter's reader loop consumes.
CLIENT_EVENTS = [
    "conversation_initiation_metadata", "ping", "audio", "interruption",
    "user_transcript", "agent_response", "agent_response_correction", "client_tool_call",
]
# Spanish agents cannot be CREATED with eleven_v3_conversational; the API rejects it
# with "Non-english Agents must use turbo or flash v2_5". Flash is also ~4x faster to
# first audio, which matters more here than expressiveness.
TTS_MODEL = "eleven_flash_v2_5"
# Tool calling must be reliable: a missed serve_order means no cup and no bill.
# gemini-2.5-flash skipped the call under this prompt; this one does not.
LLM = "gpt-5.6-luna"


def call(method: str, path: str, body: dict | None = None, raw: bool = False):
    data = json.dumps(body).encode() if body is not None else None
    headers = {"xi-api-key": KEY}
    if data:
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request(API + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request) as response:
            payload = response.read()
            return payload if raw else json.loads(payload or b"{}")
    except urllib.error.HTTPError as error:
        detail = error.read().decode()[:400]
        raise SystemExit(f"\n  {method} {path} -> HTTP {error.code}\n  {detail}\n") from None


def system_tool(name: str) -> dict:
    """built_in_tools is a map of name -> full config object, not a list of names."""
    return {"type": "system", "name": name, "description": "",
            "response_timeout_secs": 20, "params": {"system_tool_type": name}}


def ensure_tools(pack: ScenePack, apply: bool) -> dict[str, str]:
    existing = {t["tool_config"]["name"]: t["id"]
                for t in call("GET", "/v1/convai/tools").get("tools", [])}
    ids: dict[str, str] = {}
    for name, spec in pack.scenario.tools.items():
        config = {
            "type": "client",
            "name": name,
            "description": spec.description,
            "expects_response": spec.expects_response,
            "parameters": spec.parameters,
            # Filler like "one moment" before a call that returns instantly sounds wrong.
            "pre_tool_speech": "off",
            "execution_mode": "immediate",
            "interruption_mode": "allow",
        }
        if spec.expects_response:
            config["response_timeout_secs"] = spec.response_timeout_secs
        if name in existing:
            print(f"  tool  {name:14} update  {existing[name]}")
            if apply:
                call("PATCH", f"/v1/convai/tools/{existing[name]}", {"tool_config": config})
            ids[name] = existing[name]
        else:
            print(f"  tool  {name:14} create")
            ids[name] = call("POST", "/v1/convai/tools",
                             {"tool_config": config})["id"] if apply else "<new>"
    return ids


def agent_body(pack: ScenePack, npc, tool_ids: dict[str, str]) -> dict:
    return {
        "name": f"{pack.scenario.scenario_id} · {npc.npc_id}",
        "tags": [pack.scenario.scenario_id],
        "conversation_config": {
            "agent": {
                "language": pack.scenario.language,
                "first_message": npc.first_message,
                "prompt": {
                    "prompt": pack.prompts[npc.npc_id],
                    "llm": LLM,
                    "temperature": 0.7,
                    "tool_ids": [tool_ids[t] for t in npc.tools if t in tool_ids],
                    "built_in_tools": {name: system_tool(name)
                                       for name in ("end_call", "skip_turn")},
                },
            },
            "tts": {
                "voice_id": npc.voice_id,
                "model_id": TTS_MODEL,
                "agent_output_audio_format": "pcm_16000",
                "speed": npc.tts.speed,
                "stability": npc.tts.stability,
                "similarity_boost": npc.tts.similarity_boost,
            },
            "asr": {
                "user_input_audio_format": "pcm_16000",
                "keywords": pack.scenario.asr_keywords[:50],
            },
            "turn": {
                "turn_eagerness": npc.turn.eagerness,
                "turn_timeout": npc.turn.timeout,
            },
            "conversation": {
                "text_only": False,
                "max_duration_seconds": npc.max_duration_seconds,
                "client_events": CLIENT_EVENTS,
            },
        },
    }


def ensure_agents(pack: ScenePack, tool_ids: dict[str, str], apply: bool) -> dict[str, str]:
    existing = {a["name"]: a["agent_id"]
                for a in call("GET", "/v1/convai/agents?page_size=100").get("agents", [])}
    ids: dict[str, str] = {}
    for npc in pack.scenario.npcs:
        body = agent_body(pack, npc, tool_ids)
        tools = ", ".join(npc.tools) or "none"
        if body["name"] in existing:
            agent_id = existing[body["name"]]
            print(f"  agent {npc.npc_id:14} update  {agent_id}  [{tools}]")
            if apply:
                # PATCH merge depth is undocumented, so send the whole config.
                call("PATCH", f"/v1/convai/agents/{agent_id}", body)
            ids[npc.npc_id] = agent_id
        else:
            print(f"  agent {npc.npc_id:14} create            [{tools}]")
            ids[npc.npc_id] = call("POST", "/v1/convai/agents/create",
                                   body)["agent_id"] if apply else "<new>"
    return ids


async def _capture_greeting(agent_id: str) -> tuple[bytes, int]:
    """Record the character's opening line straight from the agent.

    The standalone text-to-speech endpoint refuses Voice Library voices on the free
    plan (HTTP 402 "Free users cannot use library voices via the API"), and it would
    anyway be a second rendering that could drift from the live voice. Opening one
    short conversation gives the real thing, with the agent's own voice settings.
    """
    import base64
    import wave as wavelib
    from io import BytesIO

    from websockets.asyncio.client import connect

    signed = call("GET", f"/v1/convai/conversation/get-signed-url?agent_id={agent_id}")
    pcm, rate, deadline = bytearray(), 16000, asyncio.get_running_loop().time() + 25
    async with connect(signed["signed_url"], max_size=16 * 1024 * 1024) as ws:
        # Always send every dynamic variable the prompts mention: a missing one makes
        # the whole conversation fail rather than degrade.
        await ws.send(json.dumps({
            "type": "conversation_initiation_client_data",
            "dynamic_variables": {"learner_name": "amigo", "user_order": "nada"}}))
        while True:
            remaining = deadline - asyncio.get_running_loop().time()
            if remaining <= 0:
                break
            try:
                message = json.loads(await asyncio.wait_for(ws.recv(), remaining))
            except (TimeoutError, Exception):
                break
            kind = message.get("type")
            if kind == "conversation_initiation_metadata":
                fmt = message["conversation_initiation_metadata_event"]["agent_output_audio_format"]
                rate = int(fmt.removeprefix("pcm_"))
            elif kind == "ping":
                await ws.send(json.dumps({"type": "pong",
                                          "event_id": message["ping_event"]["event_id"]}))
            elif kind == "audio":
                pcm.extend(base64.b64decode(message["audio_event"]["audio_base_64"]))
                deadline = asyncio.get_running_loop().time() + 1.5  # quiet = finished
    buffer = BytesIO()
    with wavelib.open(buffer, "wb") as out:
        out.setnchannels(1)
        out.setsampwidth(2)
        out.setframerate(rate)
        out.writeframes(bytes(pcm))
    return buffer.getvalue(), rate


def render_greetings(pack: ScenePack, agent_ids: dict[str, str], apply: bool) -> None:
    out = (Path(pack.root) if pack.root else default_pack_dir()).resolve() / "greetings"
    for npc in pack.scenario.npcs:
        target = out / f"{npc.npc_id}.wav"
        shown = target.relative_to(ROOT) if target.is_relative_to(ROOT) else target
        if not apply:
            print(f"  audio {npc.npc_id:14} {shown}")
            continue
        wav, rate = asyncio.run(_capture_greeting(agent_ids[npc.npc_id]))
        seconds = (len(wav) - 44) / 2 / rate
        if seconds < 0.3:
            print(f"  audio {npc.npc_id:14} FAILED — no greeting audio captured")
            continue
        out.mkdir(parents=True, exist_ok=True)
        target.write_bytes(wav)
        print(f"  audio {npc.npc_id:14} {shown}  ({seconds:.1f}s @ {rate} Hz)")


def write_env(ids: dict[str, str]) -> None:
    path = ROOT / "orchestrator" / ".env"
    text = path.read_text()
    for npc_id, agent_id in ids.items():
        line = f"AGENT_ID_{npc_id.upper()}={agent_id}"
        pattern = rf"(?m)^AGENT_ID_{re.escape(npc_id.upper())}=.*$"
        text = re.sub(pattern, line, text) if re.search(pattern, text) else \
            text.rstrip("\n") + f"\n{line}\n"
    path.write_text(text)
    print(f"  wrote agent ids into {path.relative_to(ROOT)}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--apply", action="store_true", help="actually create/update")
    parser.add_argument("--write-env", action="store_true", help="save ids to orchestrator/.env")
    parser.add_argument("--greetings", action="store_true", help="render greeting audio")
    parser.add_argument("--pack", default=str(default_pack_dir()))
    args = parser.parse_args()

    if not KEY:
        return print("ELEVENLABS_API_KEY is not set (orchestrator/.env)") or 1

    pack = ScenePack.load(args.pack)
    print(f"\n  scenario {pack.scenario.scenario_id}  ({len(pack.npcs)} characters)")
    print(f"  mode     {'APPLY' if args.apply else 'dry run — nothing will change'}\n")

    tool_ids = ensure_tools(pack, args.apply)
    agent_ids = ensure_agents(pack, tool_ids, args.apply)
    if args.greetings:
        render_greetings(pack, agent_ids, args.apply)

    if args.apply and args.write_env:
        write_env(agent_ids)
    elif args.apply:
        print("\n  add these to orchestrator/.env:")
        for npc_id, agent_id in agent_ids.items():
            print(f"    AGENT_ID_{npc_id.upper()}={agent_id}")
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
