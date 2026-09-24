#!/usr/bin/env bash
# A minimal scripted build: a straight row of hammer pieces, each snapped to the
# one before, placed from coordinates with no camera and no aiming.
#
#   build-row.sh <x> <y> <z> [count=5] [piece=wood_floor] [spacing=2] [yaw=0]
#
# The first piece goes exactly at x y z with cli_build_place_at. Each next one
# is asked for `spacing` metres further along the piece's own +x axis (turned by
# yaw) and placed with cli_build_place_snapped, which moves it so its snap point
# meets the previous piece's; the next request starts from where the piece
# actually went. `spacing` only has to land within the 0.5 m snap distance, so
# the piece's width is close enough (wood_floor is 2 m).
#
# The spot must be near the player (its terrain loaded), for example after
# goto-and-look.sh. The script equips a hammer, giving one only if the inventory
# has none, and builds in no-cost mode, which stays on afterwards
# (cli_build_nocost false). Set BUILD_ROW_COST=1 to pay for the pieces instead.
# A piece already standing where one would go stops the run with
# ERROR: code=occupied, so a second run does not stack a duplicate row.
#
# It ends with cli_piece_support_settle over the row, so the last line says how
# many of its pieces lack support now (unheld=0 when the row stands) instead of
# after the game's next support update.
#
# Prints every reply and a last line with the row's two ends. Exit code:
# valheim-cli's for the step that failed (1 command failure, 3 no connection),
# 1 if a snapped piece did not meet its neighbour.
#
# A game on another machine: forward its port first and run this locally,
#   ssh -N -L 5555:127.0.0.1:5555 <host>
#
# Environment:
#   VALHEIM_CLI       valheim-cli executable (default: valheim-cli on PATH)
#   VALHEIM_CLI_PORT  valheimCLI port (default 5555)
#   BUILD_ROW_COST=1  pay for the pieces from the inventory instead of no-cost mode
set -euo pipefail

[ $# -ge 3 ] && [ $# -le 7 ] || { sed -n '5p' "$0" >&2; exit 4; }
x=$1
y=$2
z=$3
count=${4:-5}
piece=${5:-wood_floor}
spacing=${6:-2}
yaw=${7:-0}
cli=${VALHEIM_CLI:-valheim-cli}
port=${VALHEIM_CLI_PORT:-5555}
case "$count" in ''|*[!0-9]*|0) echo "ERROR: count must be a whole number of at least 1" >&2; exit 4 ;; esac
nocost=nocost
[ "${BUILD_ROW_COST:-0}" = 1 ] && nocost=""

# Run one command, print its reply, keep it in $reply, and return
# valheim-cli's exit code (non-zero when the reply carries an ERROR line).
reply=""
run() {
  local code=0
  reply=$("$cli" --port "$port" "$@") || code=$?
  printf '%s\n' "$reply"
  return "$code"
}

# The placement commands take pieces from the equipped build tool's table.
# cli_equip_item answers OK (already=True) for a hammer already in hand, so a
# hammer is given only when the inventory has none; any other refusal stops.
code=0
equip=$("$cli" --port "$port" cli_equip_item Hammer 2>&1) || code=$?
if [ "$code" -ne 0 ]; then
  case "$equip" in
    *"No inventory item matching"*) run cli_give_item Hammer; run cli_equip_item Hammer ;;
    *) printf '%s\n' "$equip" >&2; exit "$code" ;;
  esac
else
  printf '%s\n' "$equip"
fi

placed_at() {
  printf '%s\n' "$reply" | sed -n 's/^OK: placed .* at=(\([^)]*\)).*/\1/p' | tr ',' ' '
}

# shellcheck disable=SC2086  # $nocost is deliberately empty or one word
run cli_build_place_at "$piece" "$x" "$y" "$z" "$yaw" $nocost
read -r px py pz <<<"$(placed_at)"
first="$px,$py,$pz"

i=1
while [ "$i" -lt "$count" ]; do
  read -r nx ny nz < <(awk -v x="$px" -v y="$py" -v z="$pz" -v s="$spacing" -v a="$yaw" \
    'BEGIN { r = a * atan2(0, -1) / 180; printf "%.3f %.3f %.3f\n", x + s * cos(r), y, z - s * sin(r) }')
  # shellcheck disable=SC2086
  run cli_build_place_snapped "$piece" "$nx" "$ny" "$nz" "$yaw" 0.5 $nocost
  gap=$(printf '%s\n' "$reply" | sed -n 's/^SNAP .*gapAfter=\([^ ]*\).*/\1/p')
  # gapAfter is measured on the placed piece: it must meet its neighbour.
  if ! awk -v g="$gap" 'BEGIN { exit !(g != "" && g != "unknown" && g + 0 < 0.01) }'; then
    echo "ERROR: piece $((i + 1)) did not meet its neighbour (gapAfter=${gap:-missing})" >&2
    exit 1
  fi
  read -r px py pz <<<"$(placed_at)"
  i=$((i + 1))
done

# Ask whether the row stands now rather than after the game's next support
# update: recompute support over the row and count pieces below their minimum.
IFS=, read -r fx _ fz <<<"$first"
read -r mx mz radius < <(awk -v ax="$fx" -v az="$fz" -v bx="$px" -v bz="$pz" -v s="$spacing" \
  'BEGIN { dx = bx - ax; dz = bz - az; printf "%.3f %.3f %.1f\n", (ax + bx) / 2, (az + bz) / 2, sqrt(dx * dx + dz * dz) / 2 + s + 1 }')
run cli_piece_support_settle "$mx" "$mz" "$radius" 3 "$piece"
unheld=$(printf '%s\n' "$reply" | sed -n 's/.*PIECE_SUPPORT_SETTLE .*unheld=\([0-9]*\).*/\1/p')

echo "OK: row of $count $piece from ($first) to ($px,$py,$pz) unheld=${unheld:-unknown}"
