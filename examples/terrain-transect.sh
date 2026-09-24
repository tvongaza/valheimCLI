#!/usr/bin/env bash
# terrain-transect.sh: the ground height along a straight line, as CSV.
#
# Samples the loaded terrain colliders (what a player standing there would
# stand on) every <step> metres from (x1, z1) to (x2, z2), both ends
# included, in ONE cli_ground_height call, and prints
#
#   x,z,distance,height
#
# to stdout. distance is metres from the start. height is empty where the
# game has no terrain loaded, so keep the line within the area loaded around
# the player (or move the player there first, e.g. with cli_arrive).
#
# Usage:
#   examples/terrain-transect.sh <x1> <z1> <x2> <z2> [step=1] > transect.csv
#
# Environment:
#   VALHEIM_CLI       the valheim-cli executable (default: valheim-cli on PATH)
#   VALHEIM_CLI_PORT  the valheimCLI port (default: 5555)
#
# A game on another machine works through an SSH tunnel to its port:
#   ssh -N -L 5555:127.0.0.1:5555 user@game-host
set -euo pipefail

usage() {
  echo "usage: $0 <x1> <z1> <x2> <z2> [step=1]" >&2
  exit 4
}

[[ $# -eq 4 || $# -eq 5 ]] || usage
x1=$1 z1=$2 x2=$3 z2=$4 step=${5:-1}
cli=${VALHEIM_CLI:-valheim-cli}
port=${VALHEIM_CLI_PORT:-5555}
max_points=10000

# One line per sample: x z distance. awk does the arithmetic so the numbers
# are plain decimals whatever the shell's locale.
if ! samples=$(LC_ALL=C awk -v x1="$x1" -v z1="$z1" -v x2="$x2" -v z2="$z2" -v step="$step" -v max="$max_points" '
  function isnum(v) { return v ~ /^-?[0-9]+(\.[0-9]+)?$/ }
  BEGIN {
    if (!isnum(x1) || !isnum(z1) || !isnum(x2) || !isnum(z2) || !isnum(step) || step <= 0) {
      print "coordinates and step must be decimal numbers, step greater than zero" > "/dev/stderr"; exit 4
    }
    length_m = sqrt((x2 - x1) ^ 2 + (z2 - z1) ^ 2)
    n = int(length_m / step)
    if (n * step < length_m) n++
    if (n + 1 > max) {
      printf "%d samples is more than %d: use a larger step\n", n + 1, max > "/dev/stderr"; exit 4
    }
    for (i = 0; i <= n; i++) {
      d = (i == n) ? length_m : i * step
      t = (length_m > 0) ? d / length_m : 0
      printf "%.3f %.3f %.3f\n", x1 + (x2 - x1) * t, z1 + (z2 - z1) * t, d
      if (length_m == 0) break
    }
  }'); then
  exit 4
fi

points=()
while read -r x z _; do
  points+=("$x" "$z")
done <<<"$samples"

if ! reply=$("$cli" --port "$port" cli_ground_height "${points[@]}"); then
  echo "$reply" >&2
  echo "cli_ground_height failed (is a world loaded?)" >&2
  exit 1
fi

# The reply has one GROUND line per point, in the order the points were sent.
echo "x,z,distance,height"
LC_ALL=C awk '
  NR == FNR { x[NR] = $1; z[NR] = $2; d[NR] = $3; n = NR; next }
  /^GROUND / {
    i++
    h = ""
    if (match($0, / h=-?[0-9.]+/)) h = substr($0, RSTART + 3, RLENGTH - 3)
    printf "%s,%s,%s,%s\n", x[i], z[i], d[i], h
  }
  END {
    if (i != n) { printf "expected %d GROUND lines, got %d\n", n, i > "/dev/stderr"; exit 1 }
  }' <(printf '%s\n' "$samples") <(printf '%s\n' "$reply")
