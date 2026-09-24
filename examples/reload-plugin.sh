#!/usr/bin/env bash
# Live-reload a BepInEx plugin into a running Valheim and wait until the new
# build is the one running (BepInEx ScriptEngine; see docs/live-reload.md).
#
# Usage: examples/reload-plugin.sh <path/to/Plugin.dll> [guid]
#
# Copies the DLL (and its .pdb, which ScriptEngine needs) into
# BepInEx/scripts, then waits with cli_await_plugin until an instance loaded
# from exactly that file (same md5) is running. guid defaults to the DLL's file
# name, which cli_await_plugin also accepts. For valheimCLI itself the waiting
# connection closes when the new build replaces the old one; the script then
# reconnects and checks cli_build instead. Exits 0 when the new build runs.
#
# Environment:
#   VALHEIM_CLI          valheim-cli executable (default: valheim-cli on PATH)
#   VALHEIM_CLI_PORT     command port (default 5555)
#   VALHEIM_PATH         game folder containing BepInEx (default: the Steam
#                        library on macOS or Linux)
#   RELOAD_TIMEOUT       seconds to wait for the reload (default 60)
#   REMOTE_HOST          copy to this SSH host instead of the local game
#   REMOTE_VALHEIM_PATH  game folder on REMOTE_HOST (required with REMOTE_HOST)
#
# A remote game: the command server listens on 127.0.0.1 only, so forward its
# port first (ssh -N -L 5555:127.0.0.1:5555 host) and set REMOTE_HOST and
# REMOTE_VALHEIM_PATH; the files are streamed over ssh and moved into place
# (a POSIX shell on the remote side).
set -euo pipefail

usage() {
    echo "Usage: $0 <path/to/Plugin.dll> [guid]" >&2
    exit 4
}

[[ $# -ge 1 && $# -le 2 ]] || usage
dll=$1
[[ -f $dll && $dll == *.dll ]] || { echo "ERROR: not a .dll file: $dll" >&2; exit 4; }
name=$(basename "$dll")
target=${2:-$name}
pdb="${dll%.dll}.pdb"

VALHEIM_CLI=${VALHEIM_CLI:-valheim-cli}
PORT=${VALHEIM_CLI_PORT:-5555}
TIMEOUT=${RELOAD_TIMEOUT:-60}
if [[ -z ${VALHEIM_PATH:-} ]]; then
    if [[ $(uname -s) == Darwin ]]; then
        VALHEIM_PATH="$HOME/Library/Application Support/Steam/steamapps/common/Valheim"
    else
        VALHEIM_PATH="$HOME/.local/share/Steam/steamapps/common/Valheim"
    fi
fi

# The client's own deadline outlasts the in-game wait so the answer can arrive.
cli() {
    "$VALHEIM_CLI" --port "$PORT" --timeout "$((TIMEOUT + 15))s" "$@"
}

md5_of() {
    if command -v md5sum >/dev/null 2>&1; then
        md5sum "$1" | cut -d' ' -f1
    else
        md5 -q "$1"
    fi
}

# One key=value field of an OK: line (location= is the rest of the line).
field() {
    local line=$1 key=$2
    if [[ $key == location ]]; then
        [[ $line == *" location="* ]] && printf '%s\n' "${line#* location=}"
        return 0
    fi
    [[ $line =~ (^|[[:space:]])$key=([^[:space:]]*) ]] && printf '%s\n' "${BASH_REMATCH[2]}"
    return 0
}

# Copy the .pdb first (the watcher reacts to *.dll only), then write the DLL
# under a temporary name and rename it into place, so ScriptEngine reads one
# complete file.
deploy() {
    if [[ -n ${REMOTE_HOST:-} ]]; then
        [[ -n ${REMOTE_VALHEIM_PATH:-} ]] || { echo "ERROR: REMOTE_VALHEIM_PATH is required with REMOTE_HOST" >&2; exit 4; }
        # Streamed through ssh so the remote shell alone interprets the paths.
        local dir="$REMOTE_VALHEIM_PATH/BepInEx/scripts" qdir qpdb qtmp qdll
        printf -v qdir '%q' "$dir"
        printf -v qpdb '%q' "$dir/$(basename "$pdb")"
        printf -v qtmp '%q' "$dir/.$name.tmp"
        printf -v qdll '%q' "$dir/$name"
        ssh "$REMOTE_HOST" "mkdir -p $qdir"
        [[ -f $pdb ]] && ssh "$REMOTE_HOST" "cat > $qpdb" < "$pdb"
        ssh "$REMOTE_HOST" "cat > $qtmp && mv -f $qtmp $qdll" < "$dll"
    else
        local dir="$VALHEIM_PATH/BepInEx/scripts"
        [[ -d "$VALHEIM_PATH/BepInEx" ]] || { echo "ERROR: no BepInEx folder under VALHEIM_PATH=$VALHEIM_PATH" >&2; exit 4; }
        mkdir -p "$dir"
        [[ -f $pdb ]] && cp "$pdb" "$dir/"
        cp "$dll" "$dir/.$name.tmp"
        mv -f "$dir/.$name.tmp" "$dir/$name"
    fi
    echo "copied $name ($new_md5) to BepInEx/scripts"
}

new_md5=$(md5_of "$dll")
[[ -f $pdb ]] || echo "warning: no $(basename "$pdb") next to the DLL; ScriptEngine fails to load a DLL without symbols unless they are embedded" >&2

build=$(cli cli_build) || { echo "ERROR: valheimCLI does not answer on port $PORT" >&2; exit 3; }
cli_guid=$(field "$build" guid)
cli_source=$(field "$build" source)
cli_md5=$(field "$build" md5)
cli_location=$(field "$build" location)

self=false
if [[ $target == "$cli_guid" || $target == "$(basename "$cli_location")" ]]; then
    self=true
fi

if ! $self; then
    if [[ $cli_source == scripts ]]; then
        echo "note: valheimCLI is loaded from BepInEx/scripts too; ScriptEngine reloads it with $name, so the first wait ends when its connection closes" >&2
    fi
    deploy
    set +e
    cli cli_await_plugin "$target" "$new_md5" "$TIMEOUT"
    rc=$?
    set -e
    if [[ $rc -eq 3 ]]; then
        # valheimCLI was reloaded with the plugin: reconnect and ask again. The
        # new valheimCLI knows the md5 of every plugin loaded in its own pass.
        cli --interval 500ms wait --for plugin-server
        cli cli_await_plugin "$target" "$new_md5" "$TIMEOUT"
        exit $?
    fi
    exit $rc
fi

if [[ $cli_md5 == "$new_md5" ]]; then
    echo "OK: valheimCLI $new_md5 is already running"
    exit 0
fi

if [[ $cli_source == plugins ]]; then
    # ScriptEngine refuses a GUID the chainloader still has registered.
    echo "valheimCLI was loaded from BepInEx/plugins; unloading it so the copy in BepInEx/scripts can load"
    cli cli_self_unload
fi
deploy

# The old instance holds this request until the new one replaces it and the
# connection closes (exit 3); exit 0 means the new build already answered.
set +e
cli cli_await_plugin "$cli_guid" "$new_md5" "$TIMEOUT"
rc=$?
set -e
[[ $rc -eq 0 ]] && exit 0
[[ $rc -eq 3 ]] || exit $rc

cli --interval 500ms wait --for plugin-server
after=$(cli cli_build)
echo "$after"
if [[ $(field "$after" md5) != "$new_md5" ]]; then
    echo "ERROR: valheimCLI answers with md5 $(field "$after" md5), not $new_md5" >&2
    exit 1
fi
if [[ $cli_source == plugins ]]; then
    echo "note: remove valheimCLI.dll from BepInEx/plugins, or both copies load at the next start (the newest wins)" >&2
fi
