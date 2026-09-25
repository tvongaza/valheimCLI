# Examples

Small scripts for the jobs a mod developer repeats: build and test a mod,
wait for the game to say something, read what went wrong, reach a game on
another machine, see a world. Each one is self-contained, with its usage
in its header; copy them into your own project and change what you need.

| Script | What it does |
| --- | --- |
| [dev-loop.sh](dev-loop.sh) | Build a mod, install it, launch Valheim, run a test plan, summarise the log |
| [wait-for-log.sh](wait-for-log.sh) | Return the moment a line matching a regex is written to the BepInEx log |
| [log-summary.sh](log-summary.sh) | Count warnings and errors by source and by message; optionally fail on them |
| [remote-tunnel.sh](remote-tunnel.sh) | Open, check or close an SSH tunnel to a game on another machine |
| [smoke-plan.yaml](smoke-plan.yaml) | A test plan for any mod: reach a fresh local world, check the player and one of your mod's commands, take a screenshot |
| [world-map.py](world-map.py) | Draw a `cli_world_dump` as an SVG map (biomes, water, contours, locations) with your own paths and points on top |

## Conventions

- **Bash or Python 3.** The shell scripts need only bash 3.2; `world-map.py`
  needs only Python 3.8+ and its standard library, and reads CSV files, so
  it runs anywhere the dump has been copied to.
- **Local first.** Every script works against the game on this machine
  with no configuration when Valheim is in the default Steam folder.
- **Environment**, the same names in every script:

  | Variable | Meaning | Default |
  | --- | --- | --- |
  | `VALHEIM_CLI` | the `valheim-cli` executable | `valheim-cli` on `PATH` |
  | `VALHEIM_CLI_PORT` | the mod's command port | `5555` |
  | `VALHEIM_PATH` | the game folder that contains `BepInEx` | the Steam folder for this OS |
  | `VALHEIM_LOG` | a log file to read instead of `BepInEx/LogOutput.log` | |
  | `VALHEIM_SSH` | `user@host` whose log to follow over ssh | |

- **Remote games.** The mod listens on `127.0.0.1` only. Open a tunnel with
  `remote-tunnel.sh open user@host 5556` and point any script at it with
  `VALHEIM_CLI_PORT=5556`. Log files are read on the machine that writes
  them: `VALHEIM_SSH=user@host VALHEIM_LOG=<path there>`.
- **Waits return on the event.** A script waits for the game to report
  what it is waiting for (an async `cli_*` command, `valheim-cli wait
  --for ...`, a log line), never by sleeping and asking again. Give a wait
  every outcome, failures included, so a failure returns as quickly as a
  success; only a timeout means nothing happened. Waits print a `WAIT:`
  heartbeat to stderr (`PROGRESS` sets the interval), and `valheim-cli
  wait` also ends early on a stall, a state that cannot reach its target,
  or a game that exits or stops answering.
- **Exit codes** follow `valheim-cli`: 0 success, 1 failure, 2 timeout
  or stall, 3 connection or missing file, 4 bad input, 5 game not in the
  needed state, 7 the game went away during a wait.

## What a wait looks like

Real output of `valheim-cli`, recorded against a stand-in for the game that
replays a world load, so the times are short; `...` marks lines left out.
Heartbeats go to stderr, the result line to stdout. A world loading, with a
heartbeat every 5 s:

```text
$ valheim-cli wait --for in-world --timeout 300s --progress 5s
...
WAIT: 12s/300s for in-world; state=InWorldNoPlayer phase=generating_locations connection=Connected locationProgress=0.515; changed: locationProgress 0.210 -> 0.515, locationCount 1138 -> 2785
...
WAIT: 24s/300s for in-world; state=InWorldNoPlayer phase=loading_active_area connection=Connected; changed: phase generating_locations -> loading_active_area, locationsGenerated false -> true, locationProgress 0.819 -> 1.000, locationCount 4431 -> 5412
OK: reached in-world; state=InWorld; connectionStatus=Connected
$ echo $?
0
```

A load that stops moving ends at the stall window, not the timeout (the
default window is 120s), then prints the last status:

```text
$ valheim-cli wait --for in-world --timeout 300s --stall 30s --progress 10s
...
WAIT: 20s/300s for in-world; state=InWorldNoPlayer phase=generating_locations connection=Connected locationProgress=0.310; unchanged for 12s
WAIT: 30s/300s for in-world; state=InWorldNoPlayer phase=generating_locations connection=Connected locationProgress=0.310; unchanged for 22s
ERROR: code=stalled waiting for in-world; state=InWorldNoPlayer; phase=generating_locations; connectionStatus=Connected; nothing the game reports changed for 30s (stall window 30s)
last status:
...
$ echo $?
2
```

Waiting for the main menu while the game sits in a world ends after 15 s:

```text
$ valheim-cli wait --for main-menu --timeout 300s --progress 10s
WAIT: 10s/300s for main-menu; state=InWorld phase=ready connection=Connected; unchanged for 10s
ERROR: code=unreachable waiting for main-menu; state=InWorld; phase=ready; connectionStatus=Connected; the game is in a world and nothing is leaving it; the main menu comes only after a logout (cli_logout_save) or a disconnect
last status:
...
$ echo $?
5
```

A test plan's `waitFor` step prints the same heartbeat under the step, and
a stalled or unreachable wait fails the step (`smoke-plan.yaml` started in a
world stops at its first step with `code=unreachable`).
