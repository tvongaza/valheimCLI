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
