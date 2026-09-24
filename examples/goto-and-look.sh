#!/usr/bin/env bash
# Put the player at a spot and take a clean screenshot of it: player safety on
# (and checked), a teleport that waits until the player has landed and the
# zones around are loaded, clear weather, Mistlands mist off, then a capture
# from above and behind that waits until the view is ready.
#
#   goto-and-look.sh <x> <z> [y]
#
# y is the height to teleport to (default 200, above any terrain). cli_arrive
# sets the player down on the ground once it has loaded; god mode covers the
# drop in the meantime.
#
# Leaves behind: god, ghost and debug modes and cheats on
# (cli_set_player_safety false turns the modes off), the debug time of day and
# weather (cli_env -1 reset), mist off in the zones loaded now (cli_mist on).
# The free-fly camera is handed back to the player.
#
# Prints every reply; the last is the capture's OK line naming the PNG, which
# the game writes under its own save-data folder. Exit code: valheim-cli's for
# the step that failed (1 command failure, 3 no connection, 4 bad input).
#
# A game on another machine: forward its port first and run this locally,
#   ssh -N -L 5555:127.0.0.1:5555 <host>
#
# Environment:
#   VALHEIM_CLI       valheim-cli executable (default: valheim-cli on PATH)
#   VALHEIM_CLI_PORT  valheimCLI port (default 5555)
#   LOOK_TOD          time of day 0-1 for the shot (default 0.5, midday; "keep" leaves it)
#   LOOK_ENV          weather for the shot (default Clear; "keep" leaves it)
#   LOOK_DISTANCE     horizontal distance from the camera to the spot, metres (default 25)
#   LOOK_HEIGHT       camera height above the spot, metres (default 15)
#   LOOK_NAME         screenshot name (default look_<x>_<z>)
set -euo pipefail

[ $# -ge 2 ] && [ $# -le 3 ] || { sed -n '7p' "$0" >&2; exit 4; }
x=$1
z=$2
y=${3:-200}
cli=${VALHEIM_CLI:-valheim-cli}
port=${VALHEIM_CLI_PORT:-5555}
tod=${LOOK_TOD:-0.5}
env=${LOOK_ENV:-Clear}
distance=${LOOK_DISTANCE:-25}
height=${LOOK_HEIGHT:-15}
name=${LOOK_NAME:-look_${x}_${z}}

# Run one command, print its reply, keep it in $reply, and return
# valheim-cli's exit code (non-zero when the reply carries an ERROR line).
reply=""
run() {
  local code=0
  reply=$("$cli" --port "$port" "$@") || code=$?
  printf '%s\n' "$reply"
  return "$code"
}

run cli_set_player_safety true
# The exit code already fails on an ERROR reply; the fields are checked too so
# the script never moves a player it has not seen protected.
case "$reply" in
  *"god=True ghost=True"*) ;;
  *) echo "ERROR: player safety is not on; not teleporting" >&2; exit 1 ;;
esac

# cli_arrive waits for the game to accept the teleport, for the landing and for
# every zone within 64 m to load, then puts the player on the ground.
run cli_arrive "$x" "$y" "$z"
position=$(printf '%s\n' "$reply" | sed -n 's/.*OK: ARRIVE position=\([^ ]*\).*/\1/p')
[ -n "$position" ] || { echo "ERROR: cli_arrive gave no position" >&2; exit 1; }
IFS=, read -r px py pz <<<"$position"

run cli_env "$tod" "$env"
run cli_mist off

# Camera: back along -x and -z from the spot, looking at a point a metre above it.
read -r cx cy cz lx ly lz < <(awk -v x="$px" -v y="$py" -v z="$pz" -v d="$distance" -v h="$height" \
  'BEGIN { s = d / sqrt(2); printf "%.2f %.2f %.2f %.2f %.2f %.2f\n", x - s, y + h, z - s, x, y + 1, z }')
code=0
run cli_capture "$name" "$cx" "$cy" "$cz" "$lx" "$ly" "$lz" || code=$?
run cli_freefly_release >/dev/null || true
exit "$code"
