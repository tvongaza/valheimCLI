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
# request ends when the new build unloads the old one (exit 3); the script then
# reconnects and checks cli_build instead. Exits 0 when the new build runs.
#
# Environment:
#   VALHEIM_CLI          valheim-cli executable (default: valheim-cli on PATH)
#   VALHEIM_CLI_PORT     command port (default 5555)
#   VALHEIM_PATH         game folder containing BepInEx (default: the Steam
#                        library on macOS or Linux)
#   RELOAD_TIMEOUT       seconds to wait for the reload (default 60)
#   REMOTE_HOST          copy to this SSH host instead of the local game
#   REMOTE_VALHEIM_PATH  game folder on REMOTE_HOST (required with REMOTE_HOST),
#                        e.g. "C:/Program Files (x86)/Steam/steamapps/common/Valheim"
#   REMOTE_OS            posix, windows or auto (default auto: a host where
#                        `uname -s` runs and is not MINGW/MSYS/Cygwin is posix)
#
# A remote game: the command server listens on 127.0.0.1 only, so forward its
# port first (ssh -N -L 5555:127.0.0.1:5555 host) and set REMOTE_HOST and
# REMOTE_VALHEIM_PATH. A POSIX host gets the files streamed over ssh. A
# Windows host (OpenSSH server, any default shell) gets them by scp into the
# SSH user's home folder, and PowerShell moves them into place; the script is
# sent with -EncodedCommand, so no shell quoting is involved.
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

# Every deploy copies the .pdb first (the watcher reacts to *.dll only), then
# writes the DLL under a name that is not *.dll and renames it into place in
# the same folder, so ScriptEngine reads one complete file.
deploy_local() {
    local dir="$VALHEIM_PATH/BepInEx/scripts"
    [[ -d "$VALHEIM_PATH/BepInEx" ]] || { echo "ERROR: no BepInEx folder under VALHEIM_PATH=$VALHEIM_PATH" >&2; exit 4; }
    mkdir -p "$dir"
    if [[ -f $pdb ]]; then
        cp "$pdb" "$dir/"
    fi
    cp "$dll" "$dir/.$name.tmp"
    mv -f "$dir/.$name.tmp" "$dir/$name"
}

# Streamed through ssh so the remote shell alone interprets the paths.
deploy_posix() {
    local dir="$REMOTE_VALHEIM_PATH/BepInEx/scripts" qdir qpdb qtmp qdll
    printf -v qdir '%q' "$dir"
    printf -v qpdb '%q' "$dir/$(basename "$pdb")"
    printf -v qtmp '%q' "$dir/.$name.tmp"
    printf -v qdll '%q' "$dir/$name"
    ssh "$REMOTE_HOST" "mkdir -p $qdir"
    if [[ -f $pdb ]]; then
        ssh "$REMOTE_HOST" "cat > $qpdb" < "$pdb"
    fi
    ssh "$REMOTE_HOST" "cat > $qtmp && mv -f $qtmp $qdll" < "$dll"
}

# A PowerShell single-quoted literal: only ' needs doubling.
ps_literal() {
    printf "'%s'" "$(printf '%s' "$1" | sed "s/'/''/g")"
}

# The files go by scp to fixed names in the SSH user's home folder (the
# starting folder of both scp and ssh sessions), so no remote path needs
# quoting. PowerShell then copies them next to the target under a name that
# is not *.dll and swaps the DLL in with one rename. The PowerShell text
# defines no functions: an alias (md, gc, h, ...) would win over a function
# of the same name.
deploy_windows() {
    local up_dll="valheim-reload-upload.dll.part" up_pdb="valheim-reload-upload.pdb.part" has_pdb='$false'
    if [[ -f $pdb ]]; then
        scp -q "$pdb" "$REMOTE_HOST:$up_pdb"
        has_pdb='$true'
    fi
    scp -q "$dll" "$REMOTE_HOST:$up_dll"
    local ps
    ps="\$ProgressPreference = 'SilentlyContinue'
\$game = $(ps_literal "$REMOTE_VALHEIM_PATH")
\$name = $(ps_literal "$name")
\$pdbName = $(ps_literal "$(basename "$pdb")")
\$hasPdb = $has_pdb
\$upDll = '$up_dll'
\$upPdb = '$up_pdb'
"
    local body
    # read -d '' ends at the end of input with status 1; the text is complete.
    IFS= read -r -d '' body <<'PS' || true
$ErrorActionPreference = 'Stop'
$home0 = [Environment]::CurrentDirectory
$dir = [IO.Path]::Combine($game, 'BepInEx', 'scripts')
[void][IO.Directory]::CreateDirectory($dir)
if ($hasPdb) {
    [IO.File]::Copy([IO.Path]::Combine($home0, $upPdb), [IO.Path]::Combine($dir, $pdbName), $true)
    [IO.File]::Delete([IO.Path]::Combine($home0, $upPdb))
}
$part = [IO.Path]::Combine($dir, $name + '.part')
$dest = [IO.Path]::Combine($dir, $name)
[IO.File]::Copy([IO.Path]::Combine($home0, $upDll), $part, $true)
[IO.File]::Delete([IO.Path]::Combine($home0, $upDll))
if ([IO.File]::Exists($dest)) {
    [IO.File]::Replace($part, $dest, [NullString]::Value)
} else {
    [IO.File]::Move($part, $dest)
}
PS
    ps+=$body
    local encoded
    encoded=$(printf '%s' "$ps" | iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\r\n')
    local err status=0
    err=$(mktemp)
    ssh "$REMOTE_HOST" "powershell -NoProfile -NonInteractive -EncodedCommand $encoded" 2>"$err" || status=$?
    clixml_to_text <"$err" >&2
    rm -f "$err"
    return "$status"
}

# PowerShell run over ssh writes its error and progress streams to stderr as
# CLIXML ("#< CLIXML" then one "<Objs ...>" line). Keep the error records as
# plain text and drop the rest; any other line passes through unchanged.
clixml_to_text() {
    local line
    while IFS= read -r line || [[ -n $line ]]; do
        case $line in
            "#< CLIXML"*) ;;
            "<Objs"*)
                printf '%s\n' "$line" | grep -o '<S S="Error">[^<]*</S>' |
                    sed -e 's/<S S="Error">//' -e 's/<\/S>$//' -e 's/_x000D_//g' -e 's/_x000A_//g' \
                        -e 's/&lt;/</g' -e 's/&gt;/>/g' -e 's/&quot;/"/g' -e "s/&apos;/'/g" -e 's/&amp;/\&/g' || true
                ;;
            *) printf '%s\n' "$line" ;;
        esac
    done
}

remote_os() {
    local os uname_out
    os=$(printf '%s' "${REMOTE_OS:-auto}" | tr '[:upper:]' '[:lower:]')
    case $os in
        posix | windows)
            echo "$os"
            return 0
            ;;
        auto) ;;
        *)
            echo "ERROR: REMOTE_OS must be posix, windows or auto (got $os)" >&2
            exit 4
            ;;
    esac
    if uname_out=$(ssh "$REMOTE_HOST" uname -s 2>/dev/null) && [[ -n $uname_out ]] &&
        ! [[ $uname_out =~ MINGW|MSYS|CYGWIN|Windows ]]; then
        echo posix
    else
        echo windows
    fi
}

deploy() {
    if [[ -n ${REMOTE_HOST:-} ]]; then
        [[ -n ${REMOTE_VALHEIM_PATH:-} ]] || { echo "ERROR: REMOTE_VALHEIM_PATH is required with REMOTE_HOST" >&2; exit 4; }
        local os
        os=$(remote_os)
        if [[ $os == windows ]]; then
            deploy_windows
        else
            deploy_posix
        fi
        echo "copied $name ($new_md5) to $REMOTE_HOST ($os): BepInEx/scripts"
    else
        deploy_local
        echo "copied $name ($new_md5) to BepInEx/scripts"
    fi
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
        echo "note: valheimCLI is loaded from BepInEx/scripts too; ScriptEngine reloads it with $name, so the first wait ends when it is unloaded (exit 3)" >&2
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

# The old instance holds this request until the new one unloads it (exit 3:
# code=unloaded or a closed connection); exit 0 means the new build already
# answered.
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
