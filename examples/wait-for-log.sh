#!/usr/bin/env bash
# Wait for a line in the BepInEx log and return the moment it is written.
#
#   wait-for-log.sh <regex> [timeout-seconds=600] [--from-start]
#
# Follows <game>/BepInEx/LogOutput.log and stops at the first line matching
# <regex> (extended regular expression). Only lines written after the call
# count, unless --from-start also matches lines already in the file. A log
# that does not exist yet is waited for, so the script can be started before
# the game.
#
# Put every outcome in the regex, failures as well as success, for example
#   wait-for-log.sh 'MyMod\] ready|MyMod\] failed|Exception'
# so a failure returns as quickly as a success. Only the timeout means that
# nothing happened.
#
# Prints the matching line and exits 0; exits 2 on timeout (the same code
# valheim-cli uses for a timeout).
#
# Environment:
#   VALHEIM_PATH  game folder that contains BepInEx (default: the Steam folder for this OS)
#   VALHEIM_LOG   log file to follow instead of <VALHEIM_PATH>/BepInEx/LogOutput.log
#   VALHEIM_SSH   user@host: follow the log on that machine over ssh instead
#                 (a macOS or Linux host; set VALHEIM_LOG to the path there).
#                 On a Windows host the same wait is PowerShell's
#                 Get-Content -Wait -Tail 0 <log> | Select-String <regex> | Select-Object -First 1
set -euo pipefail

usage() { echo "usage: wait-for-log.sh <regex> [timeout-seconds=600] [--from-start]" >&2; exit 4; }
[ $# -ge 1 ] || usage
regex=$1
limit=${2:-600}
case "$limit" in ''|*[!0-9]*) usage ;; esac
lines=0
[ "${3:-}" = --from-start ] && lines=+1

default_game_path() {
  case "$(uname -s)" in
    Darwin) echo "$HOME/Library/Application Support/Steam/steamapps/common/Valheim" ;;
    *)      echo "$HOME/.steam/debian-installation/steamapps/common/Valheim" ;;
  esac
}
log=${VALHEIM_LOG:-${VALHEIM_PATH:-$(default_game_path)}/BepInEx/LogOutput.log}

# tail -F keeps following across the file being created or replaced (BepInEx
# rewrites LogOutput.log at every game start). Its output goes through a FIFO
# so this shell can stop it the moment a line matches.
work=$(mktemp -d)
fifo=$work/log
mkfifo "$fifo"
tail_pid=
cleanup() {
  if [ -n "$tail_pid" ]; then
    { kill "$tail_pid" && wait "$tail_pid"; } 2>/dev/null || true
  fi
  rm -rf "$work"
}
trap cleanup EXIT

if [ -n "${VALHEIM_SSH:-}" ]; then
  printf -v remote 'tail -n %q -F %q' "$lines" "$log"
  ssh -n -o BatchMode=yes "$VALHEIM_SSH" "$remote" >"$fifo" 2>/dev/null &
else
  tail -n "$lines" -F "$log" >"$fifo" 2>/dev/null &
fi
tail_pid=$!
exec 3<"$fifo"

deadline=$((SECONDS + limit))
while [ "$SECONDS" -lt "$deadline" ]; do
  # read blocks until a line arrives or the time left runs out.
  if ! IFS= read -r -t $((deadline - SECONDS)) line <&3; then
    break
  fi
  line=${line%$'\r'}
  if [[ $line =~ $regex ]]; then
    printf '%s\n' "$line"
    exit 0
  fi
done

echo "TIMEOUT: no line matching /$regex/ in $log after ${limit}s" >&2
exit 2
