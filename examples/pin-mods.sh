#!/usr/bin/env bash
# Pin the mod builds a game runs, then refuse to work on a game that drifted.
#
#   pin-mods.sh snapshot FILE [--with-world]   write FILE from the running game
#   pin-mods.sh check FILE [--strict]          exit 0 if the game matches FILE,
#                                              6 with the mismatches if not
#   pin-mods.sh run FILE COMMAND...            run a console command only if the
#                                              game matches FILE
#
# snapshot records every loaded BepInEx plugin by GUID and md5 (and with
# --with-world the loaded world's name, uid and seed). The file is plain text:
# edit a line to `any` when that plugin's build does not matter, or add
# `name=absent` for a plugin that must not be loaded. See docs/expectations.md.
#
# Typical use: snapshot once on a game that works, commit the file next to your
# tests, and start every test script with `pin-mods.sh check FILE --strict`.
# To make the game itself refuse drifted runs, point [Expectations] File in
# BepInEx/config/valheimCLI.valheimCLI.cfg at the same file.
#
# Environment:
#   VALHEIM_CLI       path to the valheim-cli executable (default: valheim-cli on PATH)
#   VALHEIM_CLI_PORT  the game's CLI port (default 5555)
#
# The game must be running with valheimCLI loaded. For a game on another
# machine, forward its port first: ssh -N -L 5555:127.0.0.1:5555 host
set -euo pipefail

cli="${VALHEIM_CLI:-valheim-cli}"
port="${VALHEIM_CLI_PORT:-5555}"

usage() {
  sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//' >&2
  exit 4
}

[[ $# -ge 2 ]] || usage
action="$1"
file="$2"
shift 2

case "$action" in
  snapshot)
    world_flag=()
    if [[ "${1:-}" == "--with-world" ]]; then
      world_flag=(--with-world)
    fi
    "$cli" --port "$port" manifest --write "$file" "${world_flag[@]+"${world_flag[@]}"}"
    ;;
  check)
    if [[ "${1:-}" == "--strict" ]]; then
      "$cli" --port "$port" --expect-strict "$file"
    else
      "$cli" --port "$port" --expect "$file"
    fi
    ;;
  run)
    [[ $# -ge 1 ]] || usage
    "$cli" --port "$port" --expect "$file" "$@"
    ;;
  *)
    usage
    ;;
esac
