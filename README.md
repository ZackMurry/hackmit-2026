# HackMIT 2026

Generated world models + immersive language learning

## Layout

- `My project/` — Unity 6 project. Open `Assets/Scenes/SampleScene.unity`.
- `Assets/Worlds/` — WorldLabs Gaussian-splat worlds (`.spz`) and their collider meshes.
- `tools/spz_to_collider.py` — builds a walkable collider `.glb` from a `.spz` when WorldLabs didn't ship one.

## Content is JSON

Everything the scene needs beyond the world itself lives in `My project/Assets/StreamingAssets/`
and is re-read while the game is running, so you can tune it without leaving Play mode.

### `quests.json`

```json
{ "title": "Café", "quests": [ { "id": "order_coffee", "text": "Order a coffee", "status": "todo" } ] }
```

Flip `status` to `"done"` (from the conversation backend via `QuestManager.Instance.Complete(id)`,
or by editing the file) and the HUD ticks it off. Quest completion is also a trigger for NPC moves.

### `npcs.json`

`NpcManager` spawns one NPC per entry with the full stack (avatar, idle mocap, look-at,
lip-synced speech, E-to-talk, walking, schedule).

```json
{
  "npcs": [{
    "id": "barista",
    "displayName": "Sofía",
    "avatar": "Avatars/Female_Adult_08/Export/Female_Adult_08_facial",
    "position": { "x": 0.85, "y": -1.52, "z": 2.992 },
    "yaw": 180,
    "scale": 0.7,
    "walkSpeed": 0.8,
    "walkClip": "",
    "moves": [{
      "id": "bring_coffee",
      "trigger": "quest", "after": "order_coffee", "delay": 4,
      "path": [ { "x": 1.0, "y": -1.52, "z": 2.0 }, { "x": 1.0, "y": -1.52, "z": -1.5 } ],
      "endYaw": 180
    }]
  }]
}
```

| Field | Meaning |
| --- | --- |
| `position`, `yaw`, `scale` | Where the feet start (world units), heading in degrees (0 = +Z), avatar scale. |
| `walkSpeed`, `turnSpeed` | m/s and deg/s defaults for this NPC. |
| `idleClips`, `walkClip` | Resources paths of mocap clips. No walk clip → procedural gait. |
| `moves[].trigger` | `"start"` (scene load), `"quest"` (quest `after` completed), `"move"` (this NPC finished move `after`). |
| `moves[].delay` | Seconds to wait after the trigger, e.g. walk over 4 s after `order_coffee` is done. |
| `moves[].path` | World-space waypoints. Y is a hint; feet snap to the collider below. |
| `moves[].speed` | Override m/s for this move (0 = NPC default). |
| `moves[].endYaw` | Heading to turn to on arrival; omit to keep the walking direction. |
| `moves[].loop` | Ping-pong the path forever (patrols). |
| `moves[].once` | Default `true`; set `false` to re-fire every time the trigger happens. |

From code: `NpcManager.Instance.WalkTo("barista", new Vector3(1, -1.52f, -1.5f))` or
`npc.GetComponent<NpcSchedule>().Trigger("bring_coffee")`.

World coordinates: the splat is rendered with scale `(2, -2, 2)`, so `world = raw_spz * (2, -2, 2)`.
The ModernHouse floor is at world `y ≈ -1.52`.

## Collider generation

```sh
python -m venv .venv && .venv/bin/pip install -r tools/requirements.txt
.venv/bin/python tools/spz_to_collider.py Assets/Worlds/ModernHouse/modern_house_with_lush_landscaping_2m.spz \
    "My project/Assets/StreamingAssets/Worlds/modern_house_with_lush_landscaping_collider.glb"
```
