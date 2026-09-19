# HackMIT 2026

Generated world models + immersive language learning

## Layout

- `My project/` — Unity 6 project. Open `Assets/Scenes/SampleScene.unity`.
- `Assets/Worlds/` — WorldLabs Gaussian-splat worlds (`.spz`) and their collider meshes.
- `tools/spz_to_collider.py` — builds a walkable collider `.glb` from a `.spz` when WorldLabs didn't ship one.

## Talking to NPCs

Start the orchestrator (`python -m orchestrator`, see `docs/api.md`), press Play, walk up to
an NPC and **hold E** to speak; release to send. The client only records the mic and displays
what comes back — speech recognition, the character and the voice are the server's.

- `ConversationClient` (on the `Conversation` object; `serverUrl`, default `http://127.0.0.1:8765`)
  POSTs each recording as `audio/wav` to `/v1/speech?session_id=…&npc_id=…&response_format=json`
  and plays the returned audio through the NPC's lip-sync. `user_transcript` / `agent_transcript`
  are subtitled at the bottom of the screen. Each NPC keeps its own `session_id` (conversation
  memory) and closes it when the scene ends. WAV, raw PCM, MP3 and OGG replies are all decoded.
- `ScenarioClient` (same object) loads a saved scenario (`scenarioId`) or generates one from
  `prompt` / `language` / `level` via `/v1/scenarios` when the scene starts, then applies it:
  goals → the quest list, characters → NPC names, ids and opening lines (matched by id, else by
  order in `npcs.json`), and `scenario_id` is attached to every speech turn. The last generated
  id is remembered in PlayerPrefs so replaying doesn't regenerate.
- NPC `id`s in `npcs.json` are the server's `npc_id`s and need a configured agent
  (`AGENT_ID_LUIS`, `AGENT_ID_MARIANA`, …).

Lines without server audio — greetings, and every reply when `serverUrl` is empty — are voiced
with the prerecorded `tools/sample_es.mp3` (copied to `Assets/Resources/Audio/`), so the
interaction, audio positioning and lip-sync can be tested offline.
Goal completion isn't reported by the API yet, so quests only tick via `quests.json` /
`QuestManager.Complete`.

## Content is JSON

Everything the scene needs beyond the world itself lives in `My project/Assets/StreamingAssets/`
and is re-read while the game is running, so you can tune it without leaving Play mode.

### `quests.json`

```json
{
  "title": "Café",
  "quests": [
    { "id": "order", "text": "Order something in Spanish", "status": "todo" }
  ]
}
```

Flip `status` to `"done"` (from the conversation backend via `QuestManager.Instance.Complete(id)`,
or by editing the file) and the HUD ticks it off. Quest completion is also a trigger for NPC moves.

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
| `languageCode`, `greeting` | Language code sent to the server; line said when the player first walks up.                             |
| `moves[].trigger`          | `"start"` (scene load), `"quest"` (quest `after` completed), `"move"` (this NPC finished move `after`). |
| `moves[].delay`            | Seconds to wait after the trigger, e.g. walk over 4 s after `order` is done.                     |
| `moves[].path`             | World-space waypoints. Y is a hint; feet snap to the collider below.                                    |
| `moves[].speed`            | Override m/s for this move (0 = NPC default).                                                           |
| `moves[].endYaw`           | Heading to turn to on arrival; omit to keep the walking direction.                                      |
| `moves[].loop`             | Ping-pong the path forever (patrols).                                                                   |
| `moves[].once`             | Default `true`; set `false` to re-fire every time the trigger happens.                                  |

From code: `NpcManager.Instance.WalkTo("luis", new Vector3(1, -1.52f, -1.5f))` or
`npc.GetComponent<NpcSchedule>().Trigger("bring_coffee")`.

World coordinates: the splat is rendered with scale `(2, -2, 2)`, so `world = raw_spz * (2, -2, 2)`.
The ModernHouse floor is at world `y ≈ -1.52`.

## Collider generation

```sh
python -m venv .venv && .venv/bin/pip install -r tools/requirements.txt
.venv/bin/python tools/spz_to_collider.py Assets/Worlds/ModernHouse/modern_house_with_lush_landscaping_2m.spz \
    "My project/Assets/StreamingAssets/Worlds/modern_house_with_lush_landscaping_collider.glb"
```

## Python API

See [API setup and endpoint contracts](docs/api.md). Start with
`.venv/bin/python -m orchestrator` after installing the documented dependencies.
Unity remains separately owned. The API includes an ElevenLabs adapter based on
the teammate's voice demo scripts; see the API docs for keys and agent IDs.
