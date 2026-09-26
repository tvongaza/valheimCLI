#!/usr/bin/env bash
# area-snapshot.sh: a "what is here" dump of one place, for a bug report.
#
# Waits until the game counts the area round (x, z) as ready (its zone loaded
# and every saved object there instantiated), then runs the read-only
# inspection commands at that point and writes their replies, one section per
# command, to a text file. Nothing in the world is changed.
#
# Usage:
#   examples/area-snapshot.sh <x> <z> [radius=30] [out=area-<x>-<z>.txt]
#
# Environment:
#   VALHEIM_CLI         the valheim-cli executable (default: valheim-cli on PATH)
#   VALHEIM_CLI_PORT    the valheimCLI port (default: 5555)
#   AREA_READY_TIMEOUT  seconds to wait for the area to be ready (default: 60)
#
# The wait uses cli_until, which is a cheat command: in single player run
# `devcommands` in the game console once first. The area must be near a
# player for the game to load it; move there first (e.g. with cli_arrive) if
# the wait times out. Server-only sections (cli_zdos_at, cli_containers_at)
# answer on the machine hosting the world and say so elsewhere.
#
# A game on another machine works through an SSH tunnel to its port:
#   ssh -N -L 5555:127.0.0.1:5555 user@game-host
set -euo pipefail

usage() {
  echo "usage: $0 <x> <z> [radius=30] [out=area-<x>-<z>.txt]" >&2
  exit 4
}

[[ $# -ge 2 && $# -le 4 ]] || usage
x=$1 z=$2 radius=${3:-30}
out=${4:-area-$x-$z.txt}
cli=${VALHEIM_CLI:-valheim-cli}
port=${VALHEIM_CLI_PORT:-5555}
timeout=${AREA_READY_TIMEOUT:-60}

for n in "$x" "$z" "$radius" "$timeout"; do
  [[ $n =~ ^-?[0-9]+(\.[0-9]+)?$ ]] || usage
done

# The game re-checks inside one bounded call; the CLI waits a little longer
# than the game does so it sees the game's own timeout reply.
echo "waiting up to ${timeout}s for the area at $x,$z to be ready" >&2
if ! ready=$("$cli" --port "$port" --timeout "$((${timeout%.*} + 10))s" \
    cli_until "$timeout" "ready=True" cli_area_ready "$x" "$z"); then
  echo "$ready" >&2
  echo "the area at $x,$z did not become ready (is a player near it, and devcommands on?)" >&2
  exit 2
fi

# One section per command. A command that fails (a server-only command on a
# client, an unreadable container) is recorded with its exit code and the
# snapshot carries on: a partial picture is still worth attaching.
section() {
  local status=0 reply
  reply=$("$cli" --port "$port" "$@" 2>&1) || status=$?
  printf '## %s\n%s\n## exit=%d\n\n' "$*" "$reply" "$status"
}

{
  printf '# area snapshot at x=%s z=%s radius=%s\n\n' "$x" "$z" "$radius"
  printf '## cli_until %s ready=True cli_area_ready %s %s\n%s\n## exit=0\n\n' "$timeout" "$x" "$z" "$ready"
  section cli_ground_height "$x" "$z"
  section cli_surface_at "$x" "$z"
  section cli_paint_at "$x" "$z"
  section cli_zdos_at "$x" "$z" "$radius"
  section cli_containers_at "$x" "$z" "$radius"
  section cli_rocks_at "$x" "$z" "$radius"
  section cli_rock_health "$x" "$z" "$radius"
  section cli_piece_support "$x" "$z" "$radius"
} >"$out"

echo "wrote $out" >&2
