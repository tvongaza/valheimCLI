#!/usr/bin/env bash
# Reach a game on another machine: open, check or close an SSH tunnel to its
# valheimCLI port.
#
#   remote-tunnel.sh open  <user@host> [local-port=5555] [remote-port=5555]
#   remote-tunnel.sh check <user@host> [local-port=5555]
#   remote-tunnel.sh close <user@host> [local-port=5555]
#
# The mod listens on 127.0.0.1 only, so a game on another machine (a gaming
# PC, a dedicated server, a CI box) is reached through SSH port forwarding.
# `open` returns once the forward is listening (ssh -f with
# ExitOnForwardFailure), and fails at once if the local port is taken.
# After that every valheim-cli call, and every script here, talks to the
# remote game:
#
#   remote-tunnel.sh open me@gamebox 5556
#   valheim-cli --port 5556 --remote --status
#   VALHEIM_CLI_PORT=5556 ./examples/<script>.sh ...
#
# Logs are files, not commands: follow a remote log with
# VALHEIM_SSH=me@gamebox VALHEIM_LOG=<path there> ./examples/wait-for-log.sh.
#
# Run a second game's tunnel on another local port (for example a client on
# 5556 and a dedicated server on 5557). The tunnel is an ssh control master
# whose socket lives in ${TMPDIR:-/tmp}, so `check` and `close` find it again.
set -euo pipefail

usage() { sed -n '2,8p' "$0" >&2; exit 4; }
[ $# -ge 2 ] || usage
action=$1 host=$2 local_port=${3:-5555} remote_port=${4:-5555}
socket="${TMPDIR:-/tmp}/valheim-cli-tunnel-${local_port}.sock"

case "$action" in
  open)
    ssh -f -N -M -S "$socket" \
      -o ExitOnForwardFailure=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=3 \
      -L "${local_port}:127.0.0.1:${remote_port}" "$host"
    echo "OK: 127.0.0.1:${local_port} -> ${host}:${remote_port} (close with: $0 close $host $local_port)"
    ;;
  check)
    ssh -S "$socket" -O check "$host" 2>&1
    ;;
  close)
    ssh -S "$socket" -O exit "$host" 2>&1
    ;;
  *) usage ;;
esac
