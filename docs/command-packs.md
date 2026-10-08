# Core and optional command packs

The game-side additions are **command packs** (extensions), not AI skills. Core
stays loaded once; ordinary BepInEx or ScriptEngine loads the optional packs.
There is no second DLL loader or runtime compiler.

| Install | Owns | Console commands |
|---|---|---:|
| `valheimCLI.dll` core 1.1 | Socket/broker, main-thread dispatch, permissions, expectations, build/reload diagnostics, async completion, shared operation gate and extension ownership | 9 |
| `Valheim.Cli.Standard.dll` | Characters/joins, player/item/actor actions, building/carts, routes, screenshots/map exports, generator point probes, async waits/save and existing general gameplay helpers | 112 |
| `Valheim.Cli.WorldTools.dll` | World/ZDO/container census, terrain/rock inspection and actions, structured terrain/collider/player-support observations | 15 |
| `Valheim.Cli.Reflection.dll` | Optional `cli_call` reflection over game and mod members | 1 |
| `Valheim.Cli.Capture.dll` | Reversible grass/clutter visibility override | 1 |
| `Valheim.Cli.Observe.dll` | Optional structured game observations for test runners: zones, content, config, Harmony, review state and frames | 0 |

Before the split, core registered 135 console commands. The seven `cli_mwl_*`
port commands left core (see below); the other 128 keep their names and are each
registered exactly once, in core or one pack. Core adds `cli_extension`, `cli_extensions` and `cli_access`, three of its nine; Standard adds the read-only
`cli_generator_at` point probe, `cli_terrain_modifiers_at` loaded-modifier census, explicit local-character cheat acknowledgement, the two one-hop teleport trace commands, bounded player readiness and test-only teleport timing, for 138 in total.
`docs/command-inventory-before.json` records the 135;
`CommandPackInventoryTests` in `Tests/RequestBroker.Tests` checks the difference
and the counts in the table above. Core alone
intentionally does not expose gameplay commands. The Standard pack is large
because session, actor and capture actions share existing gameplay helpers;
these remain one optional assembly to avoid cross-pack static dependencies.
Further splitting it should follow actual independent consumers, not duplicate
helpers or introduce dependencies between ScriptEngine-loaded assemblies.

The optional Observe pack registers structured `valheim.observe/*` extension
capabilities through `cli_extensions`; it adds no console aliases. Install it
with the same core build when a test needs zone and area readiness, content or
Harmony censuses, saved rooms, unresolved prefabs, live config, player custom
data, global keys, or a bounded visual review. `zones <x,z> ...` accepts zone
coordinates on a server or client and reports `areaReady` alongside loaded
terrain and saved-object counts. A test runner must pin the pack DLL and verify
the capability before issuing commands. Review commands alter only the owned
client’s temporary visual state and restore it on pack unload.

The older console commands remain for existing scripts. `cli_zone_ready` checks
a radius, and `cli_area_ready` checks one point; neither is the typed zone
census that `valheim.observe/zones` returns. `cli_check_global_key` checks one
key, while `valheim.observe/globalkeys` reports the whole set for comparison
between actors. `cli_mist` changes loaded mist volumes without a review-state
lease; new visual tests should use `review-begin`, `review-mist-off` and
`review-restore` so the original flags are restored. These aliases are not
required by the Observe pack and are not a substitute for its capabilities.

For a bounded teleport timing trace, run `cli_teleport_trace_arm` on the
client, request one teleport, then run `cli_teleport_trace_wait <id> [timeout]`
on that client. The wait returns one result after the game finishes the hop;
no remote position polling is needed. It reports the first observed frame for
the request, movement, area readiness, floor readiness and completion. Times
are milliseconds from arming, with frame precision. `-1` means a phase was not
observed; a completed teleport without `floorReadyMs` may have used the game's
fallback and is not proof of supported arrival. Check ground support separately.
These commands measure only; they do not shorten Valheim's waits.

`cli_wait_teleportable [timeout] [still-seconds] [grounded]` waits inside the
client for the game's cooldown and a quiet, usable player, then returns once.
`cli_extension valheim.world/player-support-wait <x> <ground-y> <z> <timeout>`
likewise waits for a supported landing and holds it for 250 ms. They replace
repeated remote support queries in multi-location test runs; neither moves the
player. A result is tied to the current world and strict pins as usual.

`cli_teleport_test_mode on` is separate and off by default. It requires both
cheat permission and `VALHEIMCLI_TEST_FAST_TELEPORT=1` in the client's launch
environment; `off` or pack unload stops it. While enabled, the game may finish
a distant teleport after its two-second move only when its existing area and
floor checks succeed; the two-second cooldown may end at half a second. A
test plan must record the option as enabled. Use it only on a disposable
client: this changes game timing and is unsuitable for ordinary acceptance
runs unless the timing change is the subject of the test.

MWL port commands stay in MWL's optional adapter; Roads test commands stay in
Roads' adapter. ValheimTesting is an external library/repository that uses the
ValheimCLI transport package. None is a core or ordinary production-mod dependency.

## Install or upgrade

Stop the test game. Keep one core DLL in `BepInEx/plugins`. To retain the complete
prior command set, install all four pack DLLs beside it. Copy only the named
DLLs, not build output directories containing references. Embedded symbols are
included. Packs require ValheimCLI core 1.1 or newer; do not mix them with the old
monolithic core, whose commands would collide.

For development, put a pack in an owned `BepInEx/scripts` directory instead of
plugins and let ScriptEngine replace it. Never put the same pack in both places.
ScriptEngine reloads **all** scripts in that directory when any file changes.
Keep the core outside scripts and restart to replace core/API itself. Replacing
core underneath active packs is unsupported; no managed assembly unloading or
memory reclamation is promised.

Update strict expectations to include the actual installed pack GUIDs and hashes:
`valheimCLI.standard`, `valheimCLI.worldtools`, `valheimCLI.capture`, `valheimCLI.reflection`. No expectations
are disabled automatically. Old scripts using a missing pack should fail setup
by checking its capability, rather than relying on an unknown command's text.

`cli_extensions` lists owners, fresh instance tokens, closing state, active work
and cleanup errors. `cli_extension cli.standard/commands` (likewise
`cli.worldtools/commands`, `cli.capture/commands` and `cli.reflection/commands`) lists compatible aliases.
`valheim.world/terrain`, `terrain-surface`, and `player-support` now require World
Tools. Their names and result schemas remain unchanged.

`cli_terrain_modifiers_at <x> <z> [radius=30]` reads the loaded
`TerrainModifier` list in the game's application order. Each row includes its
saved identity, position, player flag, sort order, creation time and edit flags.
This is a live-zone observation, not a census of unloaded ZDOs or the terrain
compiler; the command refuses more than 128 nearby rows rather than returning
an incomplete list. Its order and creation-time field are pinned to the tested
game build, so a changed field fails explicitly.

## Ownership and access

Console packs register through `ConsoleModuleHost` and the same
`ExtensionRegistry` as typed adapters. Registration is synchronous on the game
thread. A command collision or thrown registration rolls back the whole console
registration, preserving earlier owners. Unload removes only command objects
this pack owns; a later replacement under the same name is not removed or granted
its predecessor's client permission. Name prefixes never establish authority.

### Behaviour change: direct commands now need console permission

This is a deliberate tightening. Before the split, core's direct dispatcher
(`TryExecuteBuiltInCommand`) called the handlers of 37 commands in every game
state, in a world as well as at the main menu, without the console's validity
check, so cheat-marked ones ran without `devcommands`. Thirty of them are now in
Standard (the other seven were the `cli_mwl_*` commands). Standard owns that
dispatcher, and `ConsoleModuleHost` first applies the registered command's own
`IsValid`, the check the console uses (including the `AllowOnServerClients`
waiver on a joined client). With no console yet, cheat, network and server-only
commands are refused. A refusal is
`ERROR: code=command_not_allowed ...`. The dispatcher also cannot call a
retained delegate after an alias was replaced.

These 20 cheat-marked direct commands now need `devcommands` on a host or single
player game, or `AllowOnServerClients` on a client joined to a dedicated server:
`cli_aim_at`, `cli_aim_at_nearest_character`, `cli_apply_magic_effect`,
`cli_destroy_nearby_characters`, `cli_equip_item`, `cli_find_locations`,
`cli_fire_current_weapon`, `cli_freeze_nearest_character`, `cli_give_item`,
`cli_goto_location`, `cli_set_env`, `cli_set_nearby_character_health`,
`cli_set_player_safety`, `cli_set_tod`, `cli_setup_reload_on_kill_clip`,
`cli_spawn_at`, `cli_spawn_frozen`, `cli_spawn_near`, `cli_weapon_state` and
`cli_zdo_resend_destroyed`. The other ten direct commands (character selection
and creation, connection, host-world and `cli_logout_save`) are not cheat-marked,
so `devcommands` does not affect them.

Migration: enable `devcommands` once per game session before a script or plan
uses these commands (a plan can run it as a setup step), then check a command's
reply rather than toggling again. `cli_run_trusted` is unchanged and does not
waive the cheat check. Existing handlers retain their documented
role/argument/world checks. Native achievement/cheat confirmation remains a
separate game gate.

Retiring a pack removes aliases immediately. Tracked asynchronous work retains
its owner until it settles; old and new instances cannot overlap under that ID.
A request waiting to start sees cancellation, while an already-issued teleport or
screenshot can settle through the existing async logic before releasing the shared
gate. Route controls stop on retirement; Capture restores its original clutter
flags. A thrown cleanup blocks replacement and is visible in discovery. Arbitrary
spawn/save/terrain effects cannot be rolled back. Some existing fire/walk actions
may finish their bounded routines while draining; removal is not undo.

New structured adapters should use `ExtensionCommand` / `ExtensionContext`.
`ConsoleModuleHost` is the compatibility layer for established console handlers,
not a second transport or permission system. Pack coroutines must be started
through their owner, not on the plugin instance directly.

## Build and local checks

```sh
# Includes CommandPackInventoryTests (command inventory, pack boundaries and project settings):
dotnet test Tests/RequestBroker.Tests/RequestBroker.Tests.csproj -c Release
# These use YOUR existing game assembly references in Environment.props:
dotnet build Packs/Standard/Valheim.Cli.Standard.csproj -c Release
dotnet build Packs/WorldTools/Valheim.Cli.WorldTools.csproj -c Release
dotnet build Packs/Capture/Valheim.Cli.Capture.csproj -c Release
dotnet build Packs/Reflection/Valheim.Cli.Reflection.csproj -c Release
dotnet build Packs/Observe/Valheim.Cli.Observe.csproj -c Release
```

All packs retain `AllowUnsafeBlocks`, matching core: Mono needs the emitted verification attributes when running code compiled against publicized game references. Without it the assemblies load but private-member paths (for example character selection and save completion) fail only when invoked. The inventory check enforces this build setting. This does not change command permissions.

The portable executable and `Valheim.Cli.Testing` package remain
ValheimCLI-owned; ValheimTesting is not bundled back into core.

Reflection is independently optional: inspection/terrain consumers need not install
`cli_call`. Its existing cheat gate, overload selection and live-assembly lookup
are unchanged. World Tools still includes terrain mutations; it is not a read-only
permission boundary. Standard stays together because its actions share session
and gameplay helpers. Further subdivision should follow those dependencies.

## Paint and walking observations

`valheim.world/terrain-paint <integer x> <integer z>` returns the loaded native
one-metre paint texel, raw normalized `r/g/b/a`, texel coordinates and heightmap
origin. It never creates a compiler or regenerates/loads terrain. Missing maps,
unreadable textures and out-of-range texels return `complete=false`; the game's
black out-of-range sentinel is not a measurement. It measures the loaded mask,
not the renderer or compiler serialization. Keep alpha separately: its meaning
varies with biome/game material. Sampling uses the game's own WorldToVertexMask.

ValheimTesting's PaintCheck consumes this capability. WalkingReview uses the
existing read-only player-support observer while a human drives. Both require independent plans and strict world/plugin pins. A bounded native campaign has now exercised Reflection removal/reload in a loaded dedicated world: one persistent ValheimCLI connection, a fresh Reflection owner after reload, and unchanged identities for all other owners. The ValheimCLI-only client joined, enabled character protection and read native terrain, collision and paint; the server confirmed its save. These checks exposed and fixed the pack verification-metadata issue described above. Human walking and general rendered appearance are separate acceptance checks.


## Examples for consumers and extension authors

Start with the external [testing-framework setup guide](https://github.com/tvongaza/ValheimTesting/blob/main/docs/getting-started.md) and [example index](https://github.com/tvongaza/ValheimTesting/blob/main/examples/README.md). NoGameTerrain runs without ValheimCLI or Valheim; GameObserve adds strict pins and a read-only connection. TerrainCheck, ClientSurfaceCheck and PaintCheck require World Tools and an already prepared fixture. WalkingReview leaves movement and usability judgement to a person.

For a new game-side extension, read the [extension API guide](testing-toolkit.md) and the working [ReloadProbe](../examples/ReloadProbe). Its paired [ReloadCheck driver](https://github.com/tvongaza/ValheimTesting/blob/main/examples/ReloadCheck/README.md) demonstrates registration, cancellation, cleanup and command replacement. Use an owned scripts directory: ScriptEngine reloads every script there. Core replacement still requires a restart.

## Bounded terrain capture

World Tools exposes `valheim.world/terrain-grid <x> <z> <spacing> <countX> <countZ> <generator|loaded-ground>`. It accepts at most 256 samples, yields every 16 samples, and refuses a world/generator change during capture. Results include world UID, generator version, game version/assembly identity, timestamps, grid coordinates and a completeness flag. Coordinates use horizontal **x,z**, in metres, with x varying fastest.

Generator samples include height, biome and river facts. Loaded-ground samples read only an existing heightmap; biome and river fields are explicitly absent. No zones are generated and there is no fallback between layers. Capture is a sequence of observations, not an atomic snapshot of mutable ground. Cancellation never returns a partial grid as complete.

[TerrainCapture](https://github.com/tvongaza/ValheimTesting/blob/main/examples/TerrainCapture/README.md) validates and saves this input for exact replay. A captured result is useful input, not an independent expected answer. Local loop/import tests and compilation against game assemblies pass, and the [native follow-up](#native-follow-up--27-september-2026) exercised capture, replay and the unloaded-ground refusal in Valheim.

## Typed session lifecycle

Standard owns `valheim.session/state`, `join`, `leave` and `save`. No extra pack or dependency between packs is introduced. `state` is read-only and exposes world UID, native world/player readiness, connection status, saving and load errors. Native readiness does not establish mod completion or loaded terrain at a destination.

`join <host:port> <existing-character> [password-environment-variable]` requires the client menu and waits for a connected local player. The optional name resolves inside the game's process; the password itself never enters the command/result. Omission clears a stale password. A previous network instance's rejection is not attributed to the new join. `leave` saves the local character and waits for the menu; it does not confirm a remote server's world save. Both use the existing permission checks and shared mutation gate.

`save [timeout-seconds]` is server-only (1–600 seconds, default 120). It shares the `cli_save` loop, checks vanilla save refusals, waits for any earlier save, issues once, then requires the new save thread to finish and the save counter to advance in the same world. This is stronger than accepting a `Saving..` message. The timeout bounds only the wait for an earlier save: if that save is still writing, the reply is `save_timeout` and nothing was issued. Once issued, the reply, owner and gate are held until the write ends, so the reply reports the real outcome (`saved: true`, or `save_failed`), with `pastTimeout: true` when the write outlived the timeout. Give the client request a timeout longer than the save can take. Cancellation does not stop an issued native operation: its owner and gate remain until the operation settles. Read-only state remains available. Unprovable transitions can require a controlled restart.

The external [SessionControl example](https://github.com/tvongaza/ValheimTesting/blob/main/examples/SessionControl/README.md) demonstrates exact-once actions and mandatory repinning after a transition, including failed or ambiguous attempts. Local policy/lifecycle tests and compilation against real game references pass, and the [native follow-up](#native-follow-up--27-september-2026) exercised structured join, leave and save; older text-command smoke checks do not establish them. The later change that follows a save past its timeout (`pastTimeout`) has local tests only.

## Native follow-up — 27 September 2026

The current four-pack build passed the [bounded strict capability campaign](https://github.com/tvongaza/ValheimTesting/blob/main/docs/native-validation-20260927.md): strict A/B reload/removal, terrain capture/replay and unloaded refusal, structured join/leave/save, stale-password recovery, and controlled mutation draining during owner replacement. Per-command pins and the persistent dispatch guard stayed enabled. A wrong core hash blocked logout without leaving the world. The 12-second drain fixture proves native scheduling and owner/gate lifetime for a controlled effect; it is not a claim that every native save/join cancellation case was exercised. No production CLI change was needed in this campaign.

### Observe test access before using cheats

Core provides `cli_access`, a non-cheat, read-only command returning one `ACCESS` JSON line (schema 1).
It reports `devcommands`, `cheatsAcknowledged`, `allowOnServerClients`, `server`, `dedicated`,
`joinedClient`, `localPlayer` and `profileAvailable`. Strict expectations still apply.

Valheim 1.0.16 has separate devcommand and achievement-acknowledgement gates. A mod marking the game
modded can already satisfy the latter; a minimal test stack may not. On an owned disposable dedicated
server, enable devcommands and use `confirmcheats` if acknowledgement is missing. On a client, wait
until its disposable local character has loaded before `cli_acknowledge_local_cheats`; it cannot mark
an unloaded character at the menu. Re-read `cli_access` after the action. Joined-client mutations also
need the explicit `AllowOnServerClients` setting and remain subject to command-specific permissions.
The observation grants nothing, and acceptance never replaces assertions about the action's effect.
