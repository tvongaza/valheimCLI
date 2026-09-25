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
# While it waits it prints a heartbeat to stderr every PROGRESS seconds, like
# valheim-cli wait: elapsed time, how many lines arrived since the last one
# and the latest of them, so a busy game and a silent one look different.
# There is no stall detection here: a game in a world can log nothing for
# minutes. To wait for a game state rather than a line, use
# `valheim-cli wait --for ...`, which also fails early on a stall. A
# dedicated server's world is up at `valheim-cli wait --for server-ready`;
# do not key that to a log line: the generation line is written only when a
# new world generates its locations, never when an existing world loads.
#
# Environment:
#   VALHEIM_PATH  game folder that contains BepInEx (default: the Steam folder for this OS)
#   VALHEIM_LOG   log file to follow instead of <VALHEIM_PATH>/BepInEx/LogOutput.log
#   PROGRESS      heartbeat interval in seconds (default 15; 0 disables)
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
progress=${PROGRESS:-15}
case "$progress" in ''|*[!0-9]*) usage ;; esac

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

start=$SECONDS
deadline=$((start + limit))
next_beat=$((start + progress))
arrived=0
latest=
while [ "$SECONDS" -lt "$deadline" ]; do
  # read blocks until a line arrives, the next heartbeat is due or the time
  # left runs out. bash 3.2 returns the same status for a timeout as for the
  # end of the stream, so a live tail is what tells them apart.
  wait_for=$((deadline - SECONDS))
  if [ "$progress" -gt 0 ] && [ $((next_beat - SECONDS)) -lt "$wait_for" ]; then
    wait_for=$((next_beat - SECONDS))
  fi
  [ "$wait_for" -ge 1 ] || wait_for=1
  if IFS= read -r -t "$wait_for" line <&3; then
    line=${line%$'\r'}
    if [[ $line =~ $regex ]]; then
      printf '%s\n' "$line"
      exit 0
    fi
    arrived=$((arrived + 1))
    latest=$line
  elif ! kill -0 "$tail_pid" 2>/dev/null; then
    break
  fi
  if [ "$progress" -gt 0 ] && [ "$SECONDS" -ge "$next_beat" ]; then
    if [ "$arrived" -gt 0 ]; then
      echo "WAIT: $((SECONDS - start))s/${limit}s for /$regex/; $arrived new lines, latest: ${latest:0:120}" >&2
    else
      echo "WAIT: $((SECONDS - start))s/${limit}s for /$regex/; no new lines" >&2
    fi
    arrived=0
    next_beat=$((SECONDS + progress))
  fi
done

echo "TIMEOUT: no line matching /$regex/ in $log after ${limit}s" >&2
exit 2
