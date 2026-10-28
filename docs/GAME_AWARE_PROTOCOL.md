# CloudSSD Game-Aware telemetry protocol

Game-Aware Mode lets an **authorized** integration (a game's official modding API, a plugin, or the game's own
telemetry) tell CloudSSD where the player is, so data for the current and nearby world regions can be cached
before it is needed. Without such an integration CloudSSD uses Universal Mode only and never pretends to know the
player's location.

**Status:** the receiving side (pipe server, region learning, prediction, scheduling, cancellation) is implemented
and unit-tested with synthetic telemetry. **No game plugin exists yet**; nothing has been validated with a real game.

## Transport

- Windows named-pipe endpoint derived from the active game ID; resolve it from the application integration service.
- Created with `PipeOptions.CurrentUserOnly`: only processes of the same Windows user can connect.
- The pipe exists only while the game is running from CloudSSD with Game-Aware enabled for that title.
- One client at a time; reconnecting is allowed.

## Messages

UTF-8 JSON, one object per line (max 4096 bytes per line). Unknown fields are ignored; malformed lines are counted
and dropped.

```json
{"type":"position","x":1520.5,"y":12.0,"z":-340.25,"region":"downtown"}
{"type":"region","region":"harbor"}
{"type":"fasttravel","region":"airport"}
{"type":"loading","state":"begin"}
```

| type | meaning |
|---|---|
| `position` | Player position (world units) plus the game's own region identifier. Send at 1–10 Hz. |
| `region` | Region change without coordinates. |
| `fasttravel` | Teleport/fast travel to a region (prefetch for neighbours is cancelled). |
| `loading` | Optional loading-screen markers (currently logged only). |

Region identifiers must come from the game (zone names, cell IDs, map chunks). CloudSSD does not infer regions
from coordinates on its own.

## How the data is used

1. **Learning (validated, never guessed):** while the player is in region R, every block the game reads is counted
   for R. A block is associated with R only after it was read during **≥ 2 separate visits** to R and **≥ 70 %** of
   its reads happened in R. Shared data (UI, common textures, global archives) is therefore never attributed to a
   single region, and no assumption is made about archive layout.
2. **Region graph:** transitions between regions are counted; movement heading toward a region's observed centroid
   raises its probability.
3. **Prediction:** on entering R, R's validated blocks are scheduled at P1; regions reached from R with probability
   ≥ 0.3 are scheduled at P2. All region prefetches are tagged and cancelled when the player moves elsewhere; fast
   travel cancels neighbour predictions and prioritizes the destination.
4. All normal safeguards apply: game reads (P0) always win, prefetch respects the cache budget, disk reserve and
   bandwidth share, and accuracy is tracked per predictor ("region").

## Rules for plugin authors

- Use only the game's supported modding/plugin interfaces. Do not inject into games protected by anti-cheat, and do
  not modify executables or bypass DRM. CloudSSD will not help circumvent such protections.
- Send coarse data only (position and region). No personal data.
