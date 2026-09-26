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

## Solids, readiness, structural support and rocks

These read the scene loaded on the machine that runs them, except
`cli_rock_health`, which reads saved objects and so also answers on a server
for zones nobody has loaded. None of them changes the world.

| Command | Reads |
| --- | --- |
| `cli_solids_over 0.5 0.2 2 100 31 200 101 31 200` | Every solid collider in a column over each point, by the game's own overlap test; a breakable boulder's piece by index and health |
| `cli_area_ready 100 200` | The game's area-ready answer for the point's zone, and the saved objects there still without an instance |
| `cli_piece_support 100 200 30 wood` | Each build piece's support as the game holds it, the material's limits, and whether that number has been computed yet |
| `cli_rocks_at 100 200 30` | Loaded rocks: each breakable boulder's remaining pieces with health and world bounds, other rocks' bounds, items on the ground |
| `cli_rock_health 100 200 30` | Each breakable boulder's destroyed pieces from its saved object, compared with the live rock where one is loaded |

`cli_solids_over <half> <from> <to> <x> <y> <z> ...` tests an upright box
`2*half` metres square from `y+from` to `y+to` over each point, so `y` is
the point's own height (from `cli_ground_height`, for example). It tests the
collider shapes, not their bounding boxes: a large boulder whose box spans a
point but whose rock does not is not listed. A non-convex mesh collider has
no inside for the physics engine, only its surface, so a column lying wholly
inside one touches nothing and is not listed; make the column tall enough to
cross the surface you are asking about. Layers tested are `Default`,
`static_solid`, `Default_small`, `piece` and `vehicle`; terrain, water,
characters and triggers are not. Each collider is listed once, with how many
points' columns it touches and the first such point. Object names come from
the saved object's prefab, because a breakable boulder renames its own
GameObject when it builds its combined mesh.

`cli_area_ready` prints `ready=` exactly as `ZNetScene.IsAreaReady` answers
it: the point's zone is loaded and every saved object of a known prefab in it
and its eight neighbours has an instance. `MISSING_INSTANCE` lines (up to the
`list` argument, default 10; `0` prints only the counts) name the objects
holding it back. `cli_until 60 ready=True cli_area_ready 100 200` waits for
it in one bounded call.

`cli_piece_support` lists pieces bottom-up with `support`, `max`, `min`,
`held` (support at or above the minimum; a piece that is not held takes
damage on each of the owner's wear passes) and `state`, which says where the
number came from:

- `computed`: this peer owns the piece and recomputes its support on every
  wear pass, about once a second.
- `pending`: this peer owns it, but it is inside the 30 s after its instance
  appeared during which the game does not recompute it; `pending_s` is the
  time left. Its support reads full capacity, which is an initial value and
  not a measurement. Pieces placed with the hammer skip this wait; pieces
  created with `spawn`, and pieces that just loaded with their zone, do not.
- `exempt`: the piece takes no support wear, so the game never computes it.
- `remote`: another peer owns it; the value is what that peer last saved,
  full capacity if it never saved one.
- `unowned`: nobody simulates it; the game reports full capacity.

The command does not recompute support: the game's recompute writes the result
into the piece's saved object and can tell other owners to drop what they
cached, which would change the world being inspected. A wear pass visits
pieces in the game's own order and each reads its neighbours' current values,
so support can take several passes to travel up a tall stack; read twice a
few seconds apart and compare. The name filter chooses
what is listed, not what holds it up.

`cli_rocks_at` reads live objects. A breakable boulder (`kind=MineRock5`) is
listed with each piece within the radius that still has health, as a
`ROCKPIECE` line with world bounds; a destroyed piece has no collider and is
gone. `kind=MineRock` is a single-piece mineable rock. `kind=Destructible` is
a destructible whose prefab name contains `rock`; the game has no rock type
for these, so that half is a name match. `DROP` lines are items lying on the
ground, such as what mining leaves behind.

`cli_rock_health` decodes each breakable boulder's saved per-piece health.
`saved=none` means the rock was never hit. Where the rock is loaded, the live
pieces are compared with the saved ones (`match=`); a rock whose saved health
cannot be decoded is reported `saved=unreadable` and makes the reply end in
`ERROR`, as an unreadable container does. On a client, only objects the
server has sent are seen.

`examples/terrain-transect.sh` samples `cli_ground_height` along a line in
one call and writes CSV; `examples/area-snapshot.sh` waits for
`cli_area_ready` and writes every read-only inspection of one place to a text
file for a bug report.
