"""End-to-end check against a running orchestrator and the live ElevenLabs agents.

Walks the demo the way Unity will: meet Maria, order from her, ask what it costs,
then sit with Luis and confirm he knows what you ordered. Also exercises the error
paths a game client can trigger by accident.

    uv run python -m orchestrator &        # server must already be running
    uv run python tools/e2e_check.py
    uv run python tools/e2e_check.py --base http://127.0.0.1:8799   # another server

Learner speech is synthesised offline with the macOS `say` command, so the only
paid calls are the agent turns themselves (roughly five).

A check that cannot run because another lane has not wired its part yet is printed as
SKIP and does not count for or against the total; it is never reported as a pass.
"""

from __future__ import annotations

import argparse
import base64
import io
import json
import re
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
import uuid
import wave
from pathlib import Path

BASE = "http://127.0.0.1:8765"
ROOT = Path(__file__).resolve().parent.parent
PASS, FAIL = "  \033[32mPASS\033[0m", "  \033[31mFAIL\033[0m"
SKIP, INFO = "  \033[33mSKIP\033[0m", "  \033[36mINFO\033[0m"
results: list[tuple[bool, str]] = []
skipped: list[str] = []


def check(ok: bool, label: str, detail: str = "") -> bool:
    results.append((ok, label))
    print(f"{PASS if ok else FAIL}  {label}" + (f"\n        {detail}" if detail else ""))
    return ok


def skip(label: str, why: str) -> None:
    skipped.append(label)
    print(f"{SKIP}  {label}\n        {why}")


def pace(reply: dict) -> float | None:
    """Seconds of reply audio per spoken letter, or None if it cannot be measured.

    Letters rather than characters so that markup such as <despacio> and punctuation
    do not flatter the number.
    """
    raw = base64.b64decode(reply.get("audio_base64") or "")
    text = re.sub(r"</?\w+>|\[[a-z ]+\]", "", reply.get("agent_transcript") or "")
    letters = sum(ch.isalnum() for ch in text)
    if not raw or letters < 8:
        return None
    if raw[:4] == b"RIFF":
        with wave.open(io.BytesIO(raw)) as clip:
            seconds = clip.getnframes() / clip.getframerate()
    else:
        seconds = len(raw) / 2 / (reply.get("sample_rate") or 16000)
    return seconds / letters


def spanish_voice() -> str:
    listing = subprocess.run(["say", "-v", "?"], capture_output=True, text=True).stdout
    for want in ("Paulina", "Angelica", "Juan", "Eddy"):
        for line in listing.splitlines():
            if line.startswith(want) and "es_MX" in line:
                return want
    raise SystemExit("No Mexican Spanish voice available to `say`")


def say_wav(text: str, voice: str) -> bytes:
    """Synthesise a learner utterance as PCM16 16 kHz mono WAV, offline and free."""
    with tempfile.TemporaryDirectory() as tmp:
        aiff, wav = Path(tmp) / "a.aiff", Path(tmp) / "a.wav"
        subprocess.run(["say", "-v", voice, "-o", str(aiff), text], check=True)
        subprocess.run(["afconvert", "-f", "WAVE", "-d", "LEI16@16000", "-c", "1",
                        str(aiff), str(wav)], check=True, capture_output=True)
        return wav.read_bytes()


def request(method: str, path: str, body: bytes | None = None,
            content_type: str | None = None) -> tuple[int, bytes, dict]:
    req = urllib.request.Request(BASE + path, data=body, method=method)
    if content_type:
        req.add_header("Content-Type", content_type)
    try:
        with urllib.request.urlopen(req, timeout=120) as response:
            return response.status, response.read(), dict(response.headers)
    except urllib.error.HTTPError as error:
        return error.code, error.read(), dict(error.headers)
    except urllib.error.URLError as error:
        raise SystemExit(
            f"\n  Cannot reach {BASE} — is the server running?\n  {error}\n") from None


def speak(text: str, npc: str, run: str, voice: str, session: str) -> dict:
    audio = say_wav(text, voice)
    status, raw, _ = request(
        "POST",
        f"/v1/speech?session_id={session}&npc_id={npc}&run_id={run}"
        f"&learner_name=Vishesh&response_format=json",
        audio, "audio/wav")
    if status != 200:
        return {"_status": status, "_body": raw.decode()[:200]}
    return json.loads(raw)


def describe(reply: dict) -> str:
    if "_status" in reply:
        return f"HTTP {reply['_status']}: {reply['_body']}"
    actions = ", ".join(a.get("action", "?") for a in reply.get("actions", [])) or "none"
    return (f"heard  {reply.get('user_transcript')!r}\n"
            f"        said   {reply.get('agent_transcript')!r}\n"
            f"        actions {actions}")


def main() -> int:
    global BASE
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default=BASE, help="orchestrator URL")
    BASE = parser.parse_args().base.rstrip("/")
    voice = spanish_voice()
    run = uuid.uuid4().hex[:16]
    maria_session, luis_session = str(uuid.uuid4()), str(uuid.uuid4())
    print(f"\n  server {BASE}   learner voice '{voice}'   run {run}\n")

    # ---------------------------------------------------------------- discovery
    status, raw, _ = request("GET", "/health")
    health = json.loads(raw)
    check(health.get("speech_ready") is True, "health: speech provider configured")
    check(health.get("scenario_pack") == "cafe_cancun_v1", "health: scenario pack loaded")

    status, raw, _ = request("GET", "/v1/npcs")
    cast = json.loads(raw)
    by_id = {n["npc_id"]: n for n in cast.get("npcs", [])}
    check(set(by_id) == {"maria", "luis"}, "npcs: Maria and Luis are listed")
    check(all(n["ready"] for n in by_id.values()), "npcs: both have an agent configured",
          "" if all(n["ready"] for n in by_id.values())
          else "set AGENT_ID_MARIA / AGENT_ID_LUIS in orchestrator/.env")

    status, raw, headers = request("GET", "/v1/npcs/maria/greeting")
    seconds = 0.0
    if status == 200 and raw[:4] == b"RIFF":
        with wave.open(io.BytesIO(raw)) as clip:
            seconds = clip.getnframes() / clip.getframerate()
    check(seconds > 0.3, "greeting: Maria's opening line is served as playable audio",
          f"{seconds:.1f}s, {len(raw)} bytes, {headers.get('content-type')}")

    # ---------------------------------------------------------------- Maria
    # She asks one clarifying question before serving, so the order is settled on the
    # second turn, not the first. Actions are collected across both.
    print("\n  — Maria, the waitress —")
    served: list[dict] = []
    normal_paces: list[float] = []
    for utterance in ("Buenas tardes. Quiero un café de olla y una concha, por favor.",
                      "Para tomar aquí, por favor."):
        reply = speak(utterance, "maria", run, voice, maria_session)
        print(f"        {describe(reply)}")
        if "_status" in reply:
            check(False, "maria: turn completed", reply["_body"])
            break
        served += [a for a in reply.get("actions", []) if a.get("action") == "serve_order"]
        normal_paces += [p for p in [pace(reply)] if p]
    else:
        check(True, "maria: both turns completed")
        check(bool(reply.get("audio_base64")), "maria: replied with audio")

    check(bool(served), "maria: serve_order fired as a scene action")
    if served:
        check(served[-1].get("total_mxn") == 80,
              "maria: total priced by Python, not the model",
              f"got {served[-1].get('total_mxn')} MXN, expected 80 (50 + 30)")
        check(served[-1].get("total_words_es") == "ochenta",
              "maria: total supplied in Spanish words")

    # Asking her to slow down must never break the turn. Whether the audio really is
    # slower is reported, not asserted: it depends on how much she chooses to repeat.
    reply = speak("¿Puedes repetir más despacio, por favor?", "maria", run, voice,
                  maria_session)
    print(f"        {describe(reply)}")
    check("_status" not in reply and bool(reply.get("agent_transcript")),
          "maria: a request to slow down gets a reply")
    slow = pace(reply) if "_status" not in reply else None
    if slow and normal_paces:
        usual = sum(normal_paces) / len(normal_paces)
        print(f"{INFO}  slow reply {slow * 1000:.0f} ms/letter vs {usual * 1000:.0f} usual "
              f"({(slow / usual - 1) * 100:+.0f}%)"
              f"{'' if slow > usual * 1.08 else ' — NOT measurably slower'}")

    reply = speak("¿Cuánto es todo?", "maria", run, voice, maria_session)
    print(f"        {describe(reply)}")
    billed = [a for a in reply.get("actions", []) if a.get("action") == "show_bill"]
    said = (reply.get("agent_transcript") or "").lower()
    check("_status" not in reply, "maria: price question completed")
    check(bool(billed) or "ochenta" in said, "maria: told the learner the total",
          f"said: {said[:120]!r}")

    request("DELETE", f"/v1/speech/sessions/{maria_session}")

    # ---------------------------------------------------------------- Luis
    print("\n  — Luis, at the table —")
    reply = speak("Hola Luis, mucho gusto. ¿Cómo estás?", "luis", run, voice, luis_session)
    print(f"        {describe(reply)}")
    check("_status" not in reply, "luis: turn completed")
    check(bool(reply.get("audio_base64")), "luis: replied with audio")
    said = (reply.get("agent_transcript") or "").lower()
    check(any(word in said for word in ("olla", "concha", "café", "pediste", "ordenaste")),
          "luis: knows what you ordered from Maria (run_id handoff)",
          f"said: {said[:120]!r}")

    status, _, _ = request("DELETE", f"/v1/speech/sessions/{luis_session}")
    check(status == 204, "session teardown returns 204")

    # ---------------------------------------------------------------- errors
    print("\n  — error paths —")
    session = str(uuid.uuid4())
    for label, path, body, ctype, want in [
        ("wrong content type rejected", f"/v1/speech?session_id={session}&npc_id=maria",
         b"hi", "text/plain", 415),
        ("raw PCM without sample_rate rejected",
         f"/v1/speech?session_id={session}&npc_id=maria", b"\x00\x01", "audio/pcm", 422),
        ("unknown character rejected",
         f"/v1/speech?session_id={session}&npc_id=nobody", b"RIFF", "audio/wav", 503),
        ("bad npc_id pattern rejected",
         f"/v1/speech?session_id={session}&npc_id=NOT-VALID", b"RIFF", "audio/wav", 422),
    ]:
        status, _, _ = request("POST", path, body, ctype)
        check(status == want, f"{label} ({want})", f"got {status}")

    status, _, _ = request("GET", "/v1/npcs/nobody/greeting")
    check(status == 404, "unknown greeting returns 404")

    # ---------------------------------------------------------------- summary
    passed = sum(1 for ok, _ in results if ok)
    print(f"\n  {passed}/{len(results)} checks passed"
          + (f", {len(skipped)} skipped (not wired yet)" if skipped else "") + "\n")
    for ok, label in results:
        if not ok:
            print(f"    failed: {label}")
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
