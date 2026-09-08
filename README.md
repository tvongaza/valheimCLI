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

Exit codes:

- `0`: success
- `1`: command or test failure
- `2`: timeout
- `3`: connection failure
- `4`: bad input
- `5`: game not ready

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

## Config

`BepInEx/config/valheimCLI.valheimCLI.cfg`:
- `Server.Port` - default 5555
- `Server.Enabled` - toggle on/off

## Requirements

- .NET SDK 8.0+
- BepInEx installed in Valheim
- Publicized assemblies in `Managed/publicized_assemblies/`
