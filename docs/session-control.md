# Control a test session

These commands operate through the existing localhost CLI listener. Use your
existing SSH tunnel for remote machines. There is no new public admin endpoint.

## Dedicated server and client

`cli_teleport_peer <peer#> <x> <y> <z> [yaw]` runs on the server. Get the current
1-based peer index from `cli_peers`; it is not a permanent player identifier.
The reply confirms a teleport request, not arrival. Verify the character's
position on a later `cli_peers` or client state read. The target client needs
no CLI installation for this vanilla RPC.

Valheim refuses cheat-marked commands on a client connected to a dedicated
server. To use the CLI test actions there, set this **on that client**, then
restart it:

```ini
[Server]
AllowOnServerClients = true
```

The default is false. This waives only the cheat restriction on command objects
registered by this plugin. Terminal allow-list, network and server-only checks
still apply; a foreign command with a `cli_` name gets no waiver. This is a local
testing option, not server-granted administrator rights. Single-player/host
sessions use vanilla cheat-command access.

## Actions

| Command | Behaviour |
| --- | --- |
| `cli_terrain_ops` | List registered vanilla terrain-operation names/settings |
| `cli_terrain_edit <op> <x> <y> <z>` | Request the actual vanilla operation on loaded terrain; not a synthetic compiler write |
| `cli_build_rotate <step>` | Set the hammer ghost's rotation step using the game's rotation increment |
| `cli_spawn_piece <prefab> <x> <y> <z> <yaw>` | Instantiate a registered prefab and report its ZDO/persistence, if any |
| `cli_cart status [radius]` | Observe the nearest loaded cart and its attachment/inventory |
| `cli_cart attach\|detach [radius]` | Request vanilla interaction; check status again to confirm |
| `cli_cart load <prefab> <count> [radius]` | Add 1–10000 items to an owned cart, using multiple stacks when needed |
| `cli_clutter off\|on` | Disable decorative ground clutter, then restore each entry's previous state; does not remove world vegetation |

Terrain edit, hammer rotation, prefab spawn, cart and clutter commands are
cheat-marked. Prefab spawning is an explicit test action: it bypasses hammer
placement and does not establish that a player can repair that piece normally.
A spawned prefab is not necessarily a persistent network object; inspect the
reported `zdo` and `persistent` fields.

Terrain height reads in the edit reply are taken in the same frame. A zero
delta does not establish that the operation did nothing. Observe again after
the compiler/collider updates. Unloaded terrain is refused before instantiation.

Cart loading refuses invalid items, amounts and radii. If the cart is remotely
owned it requests ownership and returns an error asking you to retry after the
inventory updates; it does not write through a stale inventory immediately.
Insufficient capacity is refused. The final reply reports requested versus
actually added quantities; partial addition returns an error. Nothing promises
that a queued attachment or ownership request has already completed.

Use disposable worlds/characters for mutation tests and restore their files
and plugin configuration after the session. Re-enable clutter before capture
work ends if it was enabled at the start.

## Place pieces from coordinates

`cli_build_try_place_at` aims the camera and builds where the ray lands, so it
cannot put a piece where no surface answers the ray: in mid-air, on top of
another piece, behind a wall. These commands take the transform directly.

| Command | Behaviour |
| --- | --- |
| `cli_build_place_at <piece> <x> <y> <z> [yaw] [nocost]` | Place one piece at exactly this position and heading |
| `cli_build_snap_points <x> <y> <z> [radius=4] [nameFilter]` | List built pieces' snap points near a point, nearest first; places nothing |
| `cli_build_place_snapped <piece> <x> <y> <z> [yaw] [snapRadius=0.5] [nocost]` | Place a piece near this position, moved so its closest snap point meets a built piece's |

`<piece>` is a prefab or display name from the equipped build tool's table
(`cli_build_list`), or `selected` for the piece chosen with `cli_build_select`.
Equip a hammer first (`cli_give_item Hammer`, `cli_equip_item Hammer`); without
one the reply is an `ERROR` naming what is equipped. `cli_equip_item` replies
`OK: equipped item … already=True` for an item already in hand (the game's own
equip call refuses that, which used to read as `Equip failed`), prefers the
equipped copy when the inventory holds two, and is an `ERROR` only when the game
refuses a real equip. Give a hammer only when it answers `No inventory item`. `nocost` switches the
player's no-cost mode on, as `cli_build_nocost true` does, and it stays on.

Both call `Player.PlacePiece`, the call the hammer makes once its own checks
pass. The piece therefore gets the local player as creator, runs
`WearNTear.OnPlaced` and every `IPlaced` hook, plays its placement effect and is
marked cheated on the hammer's terms. The resources are then taken as the hammer
takes them: unless the world's free-build key is set, even in no-cost mode.
This is the difference from `cli_spawn_piece`, which instantiates a prefab and
nothing else.

Before placing, the command refuses with `ERROR: code=<reason>`:

| Code | Reason |
| --- | --- |
| `occupied` | The same piece already stands within 5 cm at the same heading, so a script that runs twice does not stack duplicates (the hammer's snapping rule) |
| `not_loaded` | No terrain is loaded at the position; move a player there first |
| `no_build_zone` | Inside a location that forbids building |
| `private_zone` | A ward denies this player access |
| `wrong_biome` | The piece is restricted to other biomes |
| `missing_requirements` | Not in no-cost mode and the player lacks the resources or a crafting station |

After the placement call the new piece's support is computed with the game's
own `WearNTear.UpdateSupport`. The game breaks a piece below its minimum
support at its next wear update (`WearNTear.UpdateWear` applies 100% damage),
which for a placed piece is within about a second. That is what happens to a
piece buried in the ground or left in mid-air with nothing under it: terrain
counts as support only where the piece's bounds cross its surface. Such a
piece is removed again at once, not charged for, and refused:

```
ERROR: code=unsupported prefab=wood_floor at=(…) support=0.00 min=10.00 groundY=38.43 belowGround=0.82: …
```

Pieces that do not wear from lack of support, may not be removed, or stand in a
world with the `NoBuildingFall` key are not refused. The OK line reports the
`support` computed at placement; neighbours can change it later, so use
`cli_piece_support_settle` to ask whether a finished structure stands.

The hammer's other checks are made on its camera-driven ghost and are not
applied: clipping, a player in the way, room to stand, ground type, dungeon and
snow rules. Stamina, tool durability, skill gain and build statistics are not
touched.

Success reads:

```
OK: placed prefab=wood_floor zdo=<id> at=(100.000,32.000,200.000) yaw=90.0 hammerStep=4 cheated=True noCost=True freeBuild=False support=100.00
```

`zdo` and `at` are read from the new piece, found by identity after the call, not
from the request. `hammerStep` is the scroll-wheel step (22.5° each) that gives
this heading, or `none`: a level piece at any other yaw is one no player can
reproduce with the hammer.

`cli_build_place_snapped` applies the rule in `Player.FindClosestSnapPoints`.
The new piece's snap points are worked out at the requested transform; for each,
the closest built snap point within `snapRadius` is found; the closest pair wins
and the piece moves by that pair's offset. Neighbouring points come from built
pieces (ones with a network object) whose origin is within `snapRadius + 10` m;
the hammer's own placement ghost, which it snaps onto whatever the player looks
at, is never a neighbour. A second line reports the pair:

```
SNAP snappedTo=wood_floor snappedToZdo=<id> theirPoint=(...) myPoint=(...) requested=(...) offset=(...) gapBefore=0.400 gapAfter=0.000 candidates=8
```

`gapAfter` is measured on the placed piece, not predicted; anything above a few
millimetres means the piece did not meet its neighbour. When several pieces
share a snap point, `snappedTo` names one of them; the offset is the same.
No point within the radius gives `ERROR: code=no_snap_point` with the nearest
gap found; `cli_build_snap_points` shows what is actually there. A piece without
snap points gives `ERROR: code=no_snap_points`; place it with
`cli_build_place_at`. The snap radius must be greater than zero and at most 10 m.

Numbers must be finite and use a decimal point. A yaw or radius that does not
parse is refused, never read as zero. `nocost` is only recognised last.

## Settle structural support now

The game recomputes a piece's support about once a second, and not at all for
the first 30 s after a piece appears by any route other than the hammer
(including `cli_spawn_piece` and pieces in a zone that has just loaded). A test
that builds something and asks whether it stands would have to wait.

`cli_piece_support_settle <x> <z> [radius=10] [passes=3] [nameFilter]` runs the
game's own `WearNTear.UpdateSupport` now on every piece this peer owns within a
horizontal radius of `x z`, lowest first. The rule reads each neighbour's
stored support, so asking a column top-first reads stale values; bottom-up, one
pass carries support up a column. Pieces at the same height are ordered by
position and ZDO id, never by the scene's listing order. Passes repeat until one
changes nothing (`converged=True`) or `passes` (1-20) have run. The filter
chooses what is reported, not what is settled.

```
SUPPORT piece=wood_pole2 zdo=<id> pos=(x,y,z) support=55.00 max=100.00 min=10.00 held=True owner=local
OK: PIECE_SUPPORT_SETTLE reported=6 settled=6 skippedRemote=0 held=5 unheld=1 passes=2 converged=True radius=10.0
```

This is a write, and cheat-marked: it stores each owned piece's support in its
ZDO, as the game's own update does, and the game may ask other peers to clear
their cached support. It applies no damage. `held=False` means support is below
the piece's minimum; the game breaks that piece when it next updates its wear.
Remote-owned pieces are not recomputed (`skippedRemote`); their lines show the
support their owner last stored. `converged=False` means the limit ran out, not
that the structure settled.

## Teleports the game refuses

`Player.TeleportTo` does nothing while a teleport is running and for 2 s after
one finishes, and says so only through its return value. On a peer that does not
own the character it forwards the request instead.

`cli_teleport` now reports a refusal instead of success:

```
ERROR: code=teleport_refused reason=a teleport is still in progress inIntro=False …
ERROR: code=teleport_refused reason=the game allows a teleport 2 s after the last one finished inIntro=False …
```

Some states undo any teleport the game accepts, so neither command asks it:
during the first-spawn intro the valkyrie sets the player's position every
frame until it drops them, an attachment (seat, bed, ship's helm, saddle) holds
the player at its attach point, and a dead player is not going anywhere. These
are refused at once, with the player's state on the line:

```
ERROR: code=teleport_refused reason=the first-spawn intro is in progress; the valkyrie holds the player until it drops them inIntro=True valkyrieCarrying=True attached=False dead=False teleporting=False position=…
```

`cli_arrive` refuses these rather than waiting them out: the intro moves on only
when a person dismisses its text, and an attachment ends only when someone
leaves it. Finish or skip the intro (`cli_check_intro_complete` says whether it
is over) and stand up before arriving.

Otherwise `cli_arrive` offers its teleport every frame until the game accepts
it, then waits for landing and loaded zones as before. A landing then has to
hold for 1 s: the player must stay within 2 m horizontally and 1.5 m vertically
of where it landed, with no teleport running and nothing blocking. A landing
that is undone in that time is not reported: if a blocking state caused it, the
reply is `ERROR: code=teleport_refused reason=landing undone: …`; otherwise the
teleport is offered again, like a bounce. The reply adds `acceptedMs`, the time
until the game accepted, and `heldMs`; `retries` counts bounced or undone
landings of accepted teleports. A timeout before any acceptance is
`ERROR: code=teleport_refused`; after one, `ERROR: code=arrive_timeout`. Every
failure line carries `inIntro`, `valkyrieCarrying` (a valkyrie that has not yet
dropped the player), `attached`, `dead`, `teleporting` and `position`. A
forwarded request fails at once.

## Player safety and fly

`cli_set_player_safety true|false` sets god mode, ghost mode and debug mode to
the value given. `true` also switches cheats on, because debug mode's keys (fly
on Z, no-cost building on B) do nothing without them; `false` leaves cheats as
they were. Every flag is read back from the game onto the reply:

```
OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True
```

A flag that did not take gives `ERROR: code=safety_not_applied` with the same
fields. Check the line rather than running `debugmode` or `devcommands`, which
toggle: run blind, they are as likely to switch a mode off as on.

A client joined to a dedicated server never has cheats in effect, whatever the
`cheats` field says, so the Z key does nothing there. `cli_fly` sets debug fly
directly:

| Command | Behaviour |
| --- | --- |
| `cli_fly` | Report the current state; change nothing |
| `cli_fly on` / `cli_fly off` | Set it; running either twice is harmless |
| `cli_fly toggle` | Flip it |

The reply is `OK: fly=True changed=True`, read back from the player. `cli_fly`,
`cli_build_place_at`, `cli_build_place_snapped` and `cli_piece_support_settle`
are cheat-marked; `cli_build_snap_points` only reads. On a dedicated-server
client they need `AllowOnServerClients` like the other test actions; they are
covered by it because this plugin registers them.
