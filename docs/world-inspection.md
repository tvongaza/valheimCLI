# Inspect a world without changing it

Run these through the existing CLI connection. Saved-data commands belong on the
server; collision and paint commands belong on the machine whose scene you want
to observe. A dedicated server need not have a distant player's terrain loaded.

| Command | Reads |
| --- | --- |
| `cli_peers` | Server peers; `character` is a resolved character, `reference` is only a reference position |
| `cli_zdos_at 100 200 40` | Server ZDOs in a horizontal radius, including unloaded zones |
| `cli_containers_at 100 200 40` | Saved container contents and whether default loot was rolled; does not open or fill them |
| `cli_ground_height 100 200` | Loaded terrain collider height |
| `cli_surface_at 100 200 200` | Downward ray hits, ordered by distance, with layer, trigger flag and object identity |
| `cli_piece_geometry wood_floor wood_stair` | Prefab-local collider bounds and snap points without placing anything |
| `cli_paint_at 100 200` | Loaded terrain's raw paint channels and a descriptive label |
| `cli_prefabs_at 100 30 200 40` | Existing scene census, now with precise positions, rotations and network identities |
| `cli_nearby_prefabs 40` | The same scene census around the local player |

Coordinates are metres, rotations degrees, numbers use a decimal point. Census
radii must be greater than zero and at most 1024 m. ZDO/container radii are
horizontal; the existing scene census uses a sphere and retains its effective
30 m nearby / 60 m coordinate-query caps, printed in the reply. Prefab geometry includes
inactive children and reports unsupported collider bounds explicitly.

`ZDOS_AT` and `CONTAINERS_AT` end with counts; preserve the entire reply when
checking completeness. Unknown prefab hashes remain in the ZDO census. Missing
saved scale (`scale=-`) is not a unit-scale measurement: reconstruction depends
on the receiving prefab. ZDO IDs identify network objects within a running
session; compare persisted content separately across reloads.

Missing local terrain is reported as unavailable, not height zero. Ray hits may
include triggers, characters and non-walkable layers; inspect the layer and
trigger fields before calling a hit a floor. Observing a collider is not a walk.
An inventory that cannot be fully decoded returns `ERROR`, so the existing CLI
failure path sees it even if other containers were readable. Temporary inventory
loading uses the game's data-only reader and checks the decoded item count.

The existing scene-census commands retain their cheat flag. This change adds no
client permission bypass and does not grant server administrator rights.
