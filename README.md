# HackMIT 2026

Generated world models + immersive language learning

## Layout

- `My project/` — Unity 6 project. Scenes: `Assets/Scenes/SampleScene.unity` (ModernHouse) and
  `Assets/Scenes/CancunCafe.unity` (Café Nader; NPCs from `npcs_cancun.json`).
- `Assets/Worlds/` — WorldLabs Gaussian-splat worlds (`.spz`) and their collider meshes.
- `orchestrator/` — the Python API: scenario generation, speech turns, run recording and grading.
- `scenarios/cafe_cancun/` — the authored café: cast, prompts, menu, goals and greeting audio.
- `tools/spz_to_collider.py` — builds a walkable collider `.glb` from a `.spz` when WorldLabs didn't ship one.

## Talking to NPCs

Start the orchestrator (`uv run python -m orchestrator`, see `docs/api.md`), press Play, walk up to
an NPC and **hold E** to speak; release to send. The client only records the mic and displays
what comes back — speech recognition, the character and the voice are the server's.

- `ConversationClient` (on the `Conversation` object; `serverUrl`, default `http://127.0.0.1:8765`)
  POSTs each recording as `audio/wav` to `/v1/speech?session_id=…&npc_id=…&response_format=json`
  and plays the returned audio through the NPC's lip-sync. `user_transcript` / `agent_transcript`
  are subtitled at the bottom of the screen. Each NPC keeps its own `session_id` (conversation
  memory) and closes it when the scene ends. WAV, raw PCM, MP3 and OGG replies are all decoded.
  One `run_id` per Play session is sent to every NPC, so Luis knows what you ordered from Maria;
  `learnerName` (optional) is how the characters address you.
- `CastClient` (same object) fetches the authored cast from `/v1/npcs` when the scene starts and,
  for every spawned NPC whose id (or server alias) matches a character, swaps in the server's
  opening line as the greeting together with its pre-recorded audio (`/v1/npcs/{id}/greeting`,
  same voice as the live agent, no API spend). Characters without a configured agent are
  flagged in the console.
- **Scene actions**: a reply may carry `actions` the character performed mid-conversation
  (`serve_order`, `show_bill`, `play_gesture`; see `docs/api.md`). The client mirrors them in the
  world: quests whose `action` matches tick off, and NPC moves with `"trigger": "action"` start
  (e.g. Maria heads back to the counter once she has taken the order). Gestures are logged only —
  there are no gesture animations yet.
- `ScenarioClient` (same object) loads a saved scenario (`scenarioId`) or generates one from
  `prompt` / `language` / `level` via `/v1/scenarios` when the scene starts, then applies it:
  goals → the quest list, characters → NPC names, ids and opening lines (matched by id, else by
  order in `npcs.json`), and `scenario_id` is attached to every speech turn. The last generated
  id is remembered in PlayerPrefs so replaying doesn't regenerate.
- NPC `id`s in `npcs.json` are the server's `npc_id`s (`maria`, `luis`; `mariana` is still
  accepted as an alias) and need a configured agent (`AGENT_ID_MARIA`, `AGENT_ID_LUIS`, …).

Lines without server audio — greetings when the server is down, and every reply when `serverUrl`
is empty — are voiced with the prerecorded `tools/sample_es.mp3` (copied to
`Assets/Resources/Audio/`), so the interaction, audio positioning and lip-sync can be tested
offline.

## Goals and grading

A scenario carries goals — "order something in Spanish", "ask a follow-up about something Luis
said". Two separate things happen with them, and the characters know about neither: an actor who
is also grading you talks like an examiner, so Maria and Luis are never told.

**During the run** the HUD quest list ticks off scene actions, and that is the client's own
bookkeeping: a quest whose `action` matches what the server reported completes (`serve_order` →
the `order` quest), as do `quests.json` edits and `QuestManager.Complete`. The server does not
judge goals mid-conversation.

**After the run** the server grades what was actually said. Every `/v1/speech` turn is appended
to the run it belongs to — the learner's transcript, the character's reply, and one line per
scene action the character triggered — so tracking needs nothing new from the client beyond the
`run_id` `ConversationClient` already sends once per Play session:

```sh
curl http://127.0.0.1:8765/v1/runs/<run_id>          # what was said, and what was done
curl http://127.0.0.1:8765/v1/grade \
  -H 'Content-Type: application/json' -d '{"run_id":"<run_id>"}'
```

`POST /v1/grade` returns `overall` — **1–10 for how completely the whole goal set was hit**,
weighting the goals marked `core` above the rest — plus a per-goal verdict quoting the learner's
own words as evidence, and a short summary addressed to them. A goal is never awarded without a
quote. Add `scenario_id` to grade against a generated scenario's goals instead of the café's, or
post `goals` and `transcript` inline to grade a conversation the server never saw. Recordings are
JSON Lines under `runs/transcripts/`. Details in `docs/api.md`.

Unity does not call `/v1/grade` yet — there is no end card, so a finished run is graded with
curl. The build doc's live director, which would tick goals from speech during the run rather
than from scene actions, is also not built.

## Content is JSON

Everything the scene needs beyond the world itself lives in `My project/Assets/StreamingAssets/`
and is re-read while the game is running, so you can tune it without leaving Play mode.

### `quests.json`

```json
{
  "title": "Café",
  "quests": [
    { "id": "order", "text": "Order something in Spanish", "status": "todo", "action": "serve_order" }
  ]
}
```

Flip `status` to `"done"` (from code via `QuestManager.Instance.Complete(id)`, or by editing the
file) and the HUD ticks it off. `action` names the server scene action that completes the quest
automatically. Quest completion is also a trigger for NPC moves.

### `npcs.json`

`NpcManager` spawns one NPC per entry with the full stack (avatar, idle mocap, look-at,
lip-synced speech, E-to-talk, walking, schedule).

```json
{
  "npcs": [
    {
      "id": "luis",
      "displayName": "Luis",
      "avatar": "Avatars/Female_Adult_08/Export/Female_Adult_08_facial",
      "position": { "x": 0.85, "y": -1.52, "z": 2.992 },
      "yaw": 180,
      "scale": 0.7,
      "walkSpeed": 0.8,
      "walkClip": "",
      "moves": [
        {
          "id": "bring_coffee",
          "trigger": "quest",
          "after": "order",
          "delay": 4,
          "path": [
            { "x": 1.0, "y": -1.52, "z": 2.0 },
            { "x": 1.0, "y": -1.52, "z": -1.5 }
          ],
          "endYaw": 180
        }
      ]
    }
  ]
}
```

| Field                      | Meaning                                                                                                 |
| -------------------------- | ------------------------------------------------------------------------------------------------------- |
| `position`, `yaw`, `scale` | Where the feet start (world units), heading in degrees (0 = +Z), avatar scale.                          |
| `walkSpeed`, `turnSpeed`   | m/s and deg/s defaults for this NPC.                                                                    |
| `idleClips`, `walkClip`    | Resources paths of mocap clips. No walk clip → procedural gait.                                         |
| `greeting`                 | Line said when the player first walks up (offline fallback; `CastClient` replaces it with the server's recorded opening line). |
| `seat`                     | Seat id from `seats.json` to start the scene sitting in.                                                |
| `moves[].trigger`          | `"start"` (scene load), `"quest"` (quest `after` completed), `"move"` (this NPC finished move `after`), `"greet"` (this NPC finished its greeting), `"action"` (server reported scene action `after` for this NPC). |
| `moves[].delay`            | Seconds to wait after the trigger, e.g. walk over 4 s after `order` is done.                     |
| `moves[].path`             | World-space waypoints. Y is a hint; feet snap to the collider below.                                    |
| `moves[].speed`            | Override m/s for this move (0 = NPC default).                                                           |
| `moves[].endYaw`           | Heading to turn to on arrival; omit to keep the walking direction.                                      |
| `moves[].loop`             | Ping-pong the path forever (patrols).                                                                   |
| `moves[].once`             | Default `true`; set `false` to re-fire every time the trigger happens.                                  |
| `moves[].sit`              | Seat id to sit down in on arrival.                                                                      |

From code: `NpcManager.Instance.WalkTo("luis", new Vector3(1, -1.52f, -1.5f))`,
`npc.GetComponent<NpcSchedule>().Trigger("bring_coffee")`, `npc.GetComponent<NpcSitter>().Sit("table_a")`
/ `.Stand()`. Any walk stands the NPC up first.

### `seats.json`

The scanned world has no chair objects, so chairs are anchors. `SeatManager` (on the `Seats`
object; `CancunCafe` uses `seats_cancun.json`) places one `Seat` per entry:

```json
{
  "seats": [
    { "id": "table_a", "position": { "x": 1.1, "y": -0.9, "z": 8.7 }, "yaw": 90, "playerCanSit": true }
  ]
}
```

`position` is a point on the cushion (world units), `yaw` the direction the sitter faces. NPCs
switch to the Rocketbox seated idles (`f_sit_chair_*`) and are shifted so the mocap pelvis lands
on the cushion. The player walks up to a free seat and presses **F** to sit (camera parks
`eyeAboveSeat` over the cushion, facing `yaw`; look still works, WASD doesn't); **F** or any
movement key stands back up. Holding **E** to talk works while seated.

World coordinates: the splat is rendered with scale `(2, -2, 2)`, so `world = raw_spz * (2, -2, 2)`.
The ModernHouse floor is at world `y ≈ -1.52`, the CancunCafe floor at `y ≈ -1.50` (the café runs
along +z; it opens onto the sand along its +x side: a doorway at the south-east corner around `(4, 0.7)` and a wide opening between `z ≈ 5` and `z ≈ 10`; counter at `z ≈ 14`).

### CancunCafe flow

The player spawns on the sand outside the south-east doorway facing the café. Maria (waitress)
waits just inside; when the player walks up she greets them ("Tu amigo ya está en la mesa. Ven
conmigo"), then walks up the aisle to the table (`"trigger": "greet"`) and waits there. Luis is
already seated at `table_a` (`"seat"`); the free chair opposite is `table_b` — press **F** to sit.
Look at Maria and hold **E** to order; when the server reports `serve_order` the `order` quest
ticks and she heads back to the counter (`"trigger": "action"`). Then hold **E** facing Luis to
chat — he knows what you ordered. The characters, prompts, menu and greeting audio live in
`scenarios/cafe_cancun/` (see `docs/api.md`). Avatars: `Avatars/Female_Adult_08` and
`Avatars/Male_Adult_08` (Microsoft Rocketbox, MIT).

## Worlds

Each `.spz` in `Assets/Worlds/<Name>/` is converted to a `GaussianSplatAsset` by
`Assets/Editor/WorldSplatImporter.cs` the first time the editor loads without it (or via
**Tools ▸ Worlds ▸ Use …**), which also points the matching scene's `GaussianSplatRenderer` and
`WorldColliderLoader` at the asset and collider. To add a world: drop the `.spz` in
`Assets/Worlds/`, generate its collider (below), copy a scene, and add a `World` entry to the importer.

## Collider generation

```sh
uv sync --group mesh   # numpy/scipy/scikit-image/trimesh; or pip install -r tools/requirements.txt
uv run tools/spz_to_collider.py Assets/Worlds/ModernHouse/modern_house_with_lush_landscaping_2m.spz \
    "My project/Assets/StreamingAssets/Worlds/modern_house_with_lush_landscaping_collider.glb"
uv run tools/spz_to_collider.py Assets/Worlds/CancunCafe/cancun_cafe_model.spz \
    "My project/Assets/StreamingAssets/Worlds/cancun_cafe_collider.glb"
```

## Python API

See [API setup and endpoint contracts](docs/api.md). `uv sync`, put `ELEVENLABS_API_KEY`,
`AGENT_ID_MARIA` and `AGENT_ID_LUIS` in `orchestrator/.env` or the repo-root `.env` (both are
git-ignored; `tools/provision_agents.py --apply` creates the agents and prints the ids), then
`uv run python -m orchestrator`. It serves the cast (`/v1/npcs`), scenario generation
(`/v1/scenarios`), speech turns (`/v1/speech`), the recorded run (`/v1/runs/{run_id}`) and
grading (`/v1/grade`); `/health` reports which of those are configured, and `/docs` is browsable.
Grading additionally needs `OPENAI_API_KEY` and `TUTOR_MODEL`.
Unity remains separately owned. The API includes an ElevenLabs adapter based on
the teammate's voice demo scripts; see the API docs for keys and agent IDs.
