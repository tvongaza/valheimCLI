# Valheim CLI

Run Valheim console commands from your terminal.

## Setup

```bash
# Build
dotnet build
cd CLI && dotnet build

# Install mod
cp bin/Debug/valheimCLI.dll ~/Library/Application\ Support/Steam/steamapps/common/Valheim/BepInEx/plugins/
```

## Usage

The CLI targets net9.0. On a machine with only a newer runtime installed
(e.g. .NET 10), run it with `DOTNET_ROLL_FORWARD=Major` set, or build with
`dotnet build -p:TargetFramework=net10.0`.


```bash
# Interactive
./CLI/bin/Debug/net9.0/valheim-cli
valheim> help
valheim> tod 0.5
valheim> spawn Boar 5

# Single command
./CLI/bin/Debug/net9.0/valheim-cli tod 0.5

# Structured status for scripts
./CLI/bin/Debug/net9.0/valheim-cli --status --json

# Compact agent-readable status
./CLI/bin/Debug/net9.0/valheim-cli --status

# Launch with phase diagnostics
./CLI/bin/Debug/net9.0/valheim-cli --launch --timeout 180s --json

# Wait for a specific readiness state
./CLI/bin/Debug/net9.0/valheim-cli wait --for terminal --timeout 120s
./CLI/bin/Debug/net9.0/valheim-cli wait --for server-connected --timeout 180s --json
./CLI/bin/Debug/net9.0/valheim-cli wait --for in-world --timeout 10m --stall 3m --progress 30s

# Safe direct dedicated-server join. Prefer password files so secrets are not
# printed by the shell or stored in command history.
./CLI/bin/Debug/net9.0/valheim-cli join \
  --server HOST:2456 \
  --password-file ./server-password.txt \
  --character TestCharacter \
  --timeout 180s

# Discover commands and their automation metadata
./CLI/bin/Debug/net9.0/valheim-cli commands --group cli
./CLI/bin/Debug/net9.0/valheim-cli commands --search screenshot --json
```

## Command Completion And Capture Helpers

A response carries the whole output of its own command: the server waits for
the game thread to complete the command (or for an async command's coroutine
to complete it) before answering, up to `--timeout` (default 120s; the wire
form is `CMDT:<seconds>:<command>`, the older `CMD:<command>` keeps a 30s
wait). A command that misses its timeout is abandoned: the response says so
and any output it produces later is dropped, with a `NOTE:` line on the next
response. Scripts no longer need to ask twice for a slow command's output,
which used to run it twice.

On a timeout the response says what became of the command: one that had
not started is expired and never runs; a synchronous one keeps running on
the game thread and later requests queue behind it; an async one issues no
further actions, lets an effect it already started settle (the teleport
lands, the screenshot file finishes) and only then frees the player and
camera for the next command. Arrive, env, capture and clear share the
player and camera and run one at a time; a second one waits its turn.
The client bounds its own socket wait (`--timeout` plus 5 s) and never
resends a command that may have executed; it detects an older server
(no capability line after the greeting) and falls back to `CMD:` with a
warning.

Async helpers replace fixed sleeps in capture scripts with one bounded call
each; the answer names the condition still pending when a deadline passes:

```bash
valheim-cli cli_env 0.45 Clear                 # debug time/weather, waits for the 2 s transition
valheim-cli cli_arrive 331 60 -516 64 30       # teleport, wait for landing + loaded zones (re-teleports once if dropped)
valheim-cli cli_clear_view 331 -516 45         # destroy clutter, recount next frame, repeat up to 3 passes
valheim-cli cli_until 30 ready=true road_zone_state 331 -516 64   # poll any command until a line matches
valheim-cli cli_capture E-side 347.5 57.5 -522.5 331.1 51.5 -515.6  # pose, wait for zones / heightmap rebuilds / weather, render 2 frames, save, wait for the file
```

## Readiness And Exit Codes

`--status` prints a compact agent-readable summary:

```text
valheim-cli status ok=false code=game_not_running
readiness process=false plugin=false terminal=false mainMenu=false inWorld=false localPlayer=false serverConnected=false
context game=not_running state=Unknown cli=127.0.0.1:5555 connection=none server=none
diagnostic game_not_running: Valheim process is not running.
next Start Valheim with the desired profile, then rerun valheim-cli --status.
path /path/to/Valheim
```

The readiness line reports separate fields for the game process, plugin TCP server, terminal command bridge, main menu, in-world player, local player, and dedicated-server connection. Use `--json` for stable automation output.

Status diagnostics distinguish these connection failures when the facts are available:

- `game_not_running`: no Valheim process was detected.
- `wrong_port`: the BepInEx log shows valheimCLI loaded on a different port.
- `plugin_server_not_listening`: the plugin loaded marker exists, but the requested port is not accepting connections.
- `plugin_missing_or_not_loaded`: BepInEx wrote a log, but valheimCLI did not report loading.
- `bepinex_log_missing`: no BepInEx log was found at the resolved game path.

`--launch` reports named phases in human output and JSON:

- `process-started`
- `process-ready`
- `steam`
- `bepinex`
- `plugin-loaded`
- `cli-server-listening`
- `terminal-ready`
- `server-join-queued` when `--connect` is used
- `in-world` when `--connect` is used

The JSON launch response includes `phases`, `failurePhase`, `errorCode`, and the final `status` object.

`wait --for <target>` supports:

- `process`
- `plugin-server`
- `terminal`
- `main-menu`
- `in-world`
- `local-player`
- `server-connected`

A wait watches the whole status (process, plugin, state, load phase, location progress, connection) and ends in one of four ways:

- **reached**: `OK: reached <target>; ...`, exit 0.
- **timeout**: `--timeout` passed, `TIMEOUT: waiting for <target>; ...`, exit 2.
- **stalled**: nothing in the status changed for `--stall` (default 120s; `0` disables), `ERROR: code=stalled ...`, exit 2. A stall is a timeout that came early, so it keeps the timeout's exit code.
- **unreachable**: the game is in a state from which the target never comes without an action, `ERROR: code=unreachable ...`, exit 5. Waiting for `main-menu` while the game is in a world is the common case: the menu comes only after a logout (`cli_logout_save`) or a disconnect.

While it waits, it prints a heartbeat every `--progress` (default 15s; `0` disables) to stderr, so stdout keeps only the result:

```text
WAIT: 45s/300s for in-world; state=InWorldNoPlayer phase=generating_locations connection=Connected locationProgress=0.412; changed: phase connecting_screen -> generating_locations, locationProgress 0.000 -> 0.412
WAIT: 60s/300s for in-world; state=InWorldNoPlayer phase=loading_active_area connection=Connected; changed: phase generating_locations -> loading_active_area
```

A stalled or unreachable wait prints the last status to stderr after its result line. With `--json` each heartbeat is a one-line JSON event on stderr (`{"event":"wait-progress",...}`) and the final document on stdout carries `errorCode`, `elapsedSeconds`, `unchangedSeconds`, the `heartbeats` and the last `status`.

How the decisions are made:

- **Progress** is any change in state, load phase, location progress or count, locations generated, active area loaded, connection status or server, whether the game runs, whether the plugin answers, and (only while the game is starting and reports nothing else) the size of the BepInEx log. Timers such as `respawnWait` and `estimatedLocationSeconds` move on their own and do not count.
- **The stall window** starts at the last change and is armed only once the game has been seen running, so a wait started ahead of a launch waits for the launch. 120s is conservative: location generation reports its progress and the load phases follow one another, but a heavily modded game between the plugin loading and the main menu, loading the area around the player on a slow disk, or a world save that holds the main thread can each sit on one value for about a minute. A stall window no shorter than `--timeout` never fires. Raise it, or pass `--stall 0`, for a wait during which a person acts in a menu (nothing in the status changes while a character is picked).
- **Unreachable** is decided only from settled states, and only after the status has held for 15 s, which covers a logout or disconnect requested just before or just after the wait starts. Waiting for `main-menu` is unreachable when the game is in a world with its player and nothing is leaving it: the plugin does not report the world shutting down and the connection status shows no error or disconnect. A game that was running during the wait and stops is unreachable for every target but `process`. A server that rejects the connection (wrong version or password) ends a `server-connected` wait at once. Loading states (`Loading`, `InWorldNoPlayer`) are never unreachable, as entering and leaving a world pass through the same ones, and waiting for a world from the main menu is never unreachable, as a join may be queued; the stall window covers both. `--allow-unreachable` keeps waiting in any state, for a wait where someone else logs out; add `--stall 0` if that person may take longer than the stall window.

The waits inside `--launch` and `join` print the same heartbeat but end only on their timeout (or a rejected connection), as before.

A test plan's `waitFor` step behaves like `wait` for any target name (`MainMenu`, `InWorld`, ...) and prints the heartbeat under the step. It takes two optional keys: `stall` (a duration, `0` to disable; it overrides `--stall`) and `allowUnreachable: true`. `--progress`, `--stall` and `--allow-unreachable` on the command line apply to every `waitFor` step. A state that is not a wait target (`Loading`, `InWorldNoPlayer`) is still matched by name only.

```yaml
  - name: Load the world by hand
    waitFor:
      state: InWorld
      timeout: 10m
      stall: 0            # a person picks the character; nothing changes meanwhile
      message: Load into a world to begin testing
```

Exit codes:

- `0`: success
- `1`: command or test failure
- `2`: timeout, or a wait that stalled
- `3`: connection failure
- `4`: bad input
- `5`: game not ready, or a wait whose target cannot be reached from the game's state

## Test Layout And Artifacts

`CLI/tests/` contains generic, reusable sample plans only. Put local/private plans in `CLI/local-tests/`; that folder is ignored by git.

Test runs write generated evidence under `CLI/runs/<timestamp>-<plan>/` by default:

- `summary.json`
- `transcript.txt`

Screenshots, videos, logs, and generated evidence should stay out of source control.

## Remote Helpers

Generic wrappers live under `scripts/`:

- `scripts/valheim-cli-local`
- `scripts/valheim-cli-ssh-linux`
- `scripts/valheim-cli-ssh-windows.ps1`

Configure hosts, executable paths, and game paths with environment variables or script parameters. Do not put real passwords in commands; use `--password-file`.

## Examples

[`examples/`](examples/README.md) holds small scripts for a mod's development
loop: build, install, launch and run a plan in one step; wait for a log line;
summarise a run's warnings and errors; tunnel to a game on another machine.
They run against the local game by default and against a remote one through
the tunnel.

## Config

`BepInEx/config/valheimCLI.valheimCLI.cfg`:
- `Server.Port` - default 5555
- `Server.Enabled` - toggle on/off

## Requirements

- .NET SDK 8.0+
- BepInEx installed in Valheim
- Publicized assemblies in `Managed/publicized_assemblies/`
