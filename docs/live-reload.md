# Live reload

Load a new build of a plugin into a running game, without a restart, and prove
from a script that the new build is the one running. This uses BepInEx
ScriptEngine for the reload and three valheimCLI commands to wait for it and
check it. It works for any BepInEx plugin that undoes its own effects when it
is destroyed (the checklist below), and for valheimCLI itself.

| Command | Does |
| --- | --- |
| `cli_await_plugin <guid\|file.dll> [md5-prefix\|-] [timeout=30]` | Waits until the plugin is reloaded, optionally proven to be the build with that md5 |
| `cli_build` | Which valheimCLI build is answering: assembly, source, md5 of its file when it loaded, load time |
| `cli_self_unload` | Unloads valheimCLI so a copy in `BepInEx/scripts` can load in its place |

`examples/reload-plugin.sh <path/to/Plugin.dll> [guid]` runs the whole flow.

## Install ScriptEngine

ScriptEngine is part of BepInEx.Debug:
<https://github.com/BepInEx/BepInEx.Debug/releases>. This page was checked
against release r11.1 (`ScriptEngine_r11.1.zip`). Unpack it into the game
folder so that `BepInEx/plugins/ScriptEngine.dll` exists, create the folder
`BepInEx/scripts`, and start the game once to write the config file
`BepInEx/config/com.bepis.bepinex.scriptengine.cfg`. Then set:

```ini
[General]
## Load the plugins in BepInEx/scripts when the game starts (default false).
LoadOnStart = true

[AutoReload]
## Reload when a DLL in BepInEx/scripts changes (default false).
EnableFileSystemWatcher = true
## Seconds without further changes before the reload (default 3).
AutoReloadDelay = 3
```

`ReloadKey` (default F6) reloads by hand. `IncludeSubdirectories` (default
false) also watches and loads subfolders of `BepInEx/scripts`.

## What a reload does

ScriptEngine r11.1, read from its source:

1. A `*.dll` in `BepInEx/scripts` is created, changed, renamed or deleted. The
   timer restarts on every change and counts game frames, so a reload happens
   `AutoReloadDelay` seconds after the last change while the game is running.
2. Every plugin it loaded before is unloaded together: its
   `Chainloader.PluginInfos` entry is removed and the hidden GameObject that
   carries all of them is destroyed at the end of that frame. Their
   `OnDestroy` runs then.
3. Every DLL in the folder is read again, renamed to `<name>-<ticks>` and
   loaded from memory. ScriptEngine asks for symbols, and with the Mono.Cecil
   that BepInEx ships a DLL without a `.pdb` next to it (or an embedded one)
   fails to load and stops the reload of the DLLs after it. Always copy the
   `.pdb`, or build with `<DebugType>embedded</DebugType>`.
4. A GUID that is still in `Chainloader.PluginInfos` is refused: "A plugin with
   GUID ... is already loaded!". That is a plugin the chainloader loaded from
   `BepInEx/plugins`. Do not keep a copy there while you reload it.
5. One frame later each plugin is registered in `Chainloader.PluginInfos` and
   added to a new hidden GameObject, which runs its `Awake`. `Info.Location`
   (the file) is set only after `Awake` returns; read it in `Start` or later.
   `[BepInDependency]` is ignored.

Copy the `.pdb` before the `.dll`, and write the DLL under another name and
rename it into place. The watcher then sees one complete file.

The old assembly stays loaded (.NET cannot unload it), with its own statics.
Everything the old instance put into shared state (the game, Unity, BepInEx,
other mods) stays too unless its `OnDestroy` takes it out.

## What a plugin must undo in OnDestroy

A plugin reloads cleanly when its `OnDestroy` leaves nothing of it running.
ScriptEngine destroys the old instance a frame before the new one's `Awake`,
so undoing by name or id does not touch the new instance.

- **Harmony patches.** `_harmony.UnpatchSelf()` removes every patch under the
  plugin's Harmony id. Keep the instance that `Harmony.CreateAndPatchAll`
  returns. A patch left behind runs the old code next to the new one.
- **Event subscriptions.** Remove every handler added to a static or long-lived
  event: `SceneManager.sceneLoaded`, game or library events, another plugin's
  `ConfigEntry.SettingChanged`.
- **Static state outside the plugin.** The plugin's own statics are new with
  each assembly; entries it added to the game's or another mod's collections
  and singletons are not.
- **File watchers.** Dispose any `FileSystemWatcher`, for example one that
  reloads the config file: it keeps calling the old code.
- **Sockets and threads.** Stop listeners, close accepted connections, and
  tell background threads to finish. A port the old instance still holds
  refuses the new one; retry the bind for a few seconds.
- **Log sources.** Remove sources made with `Logger.CreateLogSource` with
  `BepInEx.Logging.Logger.Sources.Remove(source)`. The plugin's own `Logger`
  is one too. A left-over source only costs memory.
- **GameObjects and components.** Destroy what the plugin created, above all
  objects marked `DontDestroyOnLoad`, and components it added to game objects
  or prefabs: they run the old assembly's code for as long as they live.
- **Console commands.** `new Terminal.ConsoleCommand(name, ...)` stores the
  command in the static `Terminal.commands` under `name.ToLower()`, replacing
  any command of that name. The new instance's registration therefore replaces
  the old one's. A command that the new build no longer registers stays, bound
  to the old code, unless `OnDestroy` removes it; remove only an entry that
  still holds the object you registered. Tab completion may list a removed
  command until the terminal rebuilds its list.
- **Coroutines** stop with the plugin; ones started on another MonoBehaviour do
  not.

Content the game has already taken in (prefabs, items, recipes, pieces,
localization, registered through a library or directly) usually cannot be taken
back. Live reload suits code; restart the game after changing content.

## Wait for a reload: cli_await_plugin

```bash
valheim-cli cli_await_plugin com.example.mymod d41d8cd98f00 60
valheim-cli cli_await_plugin MyMod.dll - 60
```

The plugin is named by its `BepInPlugin` GUID, or by the file name it loaded
from (anything ending in `.dll`). The answer comes the moment the plugin is
reloaded:

```text
OK: PLUGIN guid=com.example.mymod version=1.2.0 assembly=MyMod-639000000000000000 source=scripts md5=d41d8cd98f00b204e9800998ecf8427e ms=3120 location=/path/to/Valheim/BepInEx/scripts/MyMod.dll
```

`source` is `scripts` (ScriptEngine), `plugins` (chainloader), `other` or
`unknown`. `location` is the rest of the line and may contain spaces.

What counts as reloaded. The md5 of the file an instance was loaded from is
what must be proven, and the file's md5 now does not say that: the copy that
triggers a reload replaces the file under the old instance first. So:

- An instance that was not loaded when the call arrived counts. Its file is
  hashed once, when it is first seen, a frame or two after ScriptEngine read it.
- With an md5 prefix (6 to 32 hex digits), only an instance whose md5 starts
  with it counts. Pass the md5 of the DLL you copied; a reload of some other
  build then keeps waiting and the timeout names the md5 that did load.
- An instance loaded in the same ScriptEngine pass as the answering valheimCLI
  counts by the md5 taken at that load. This answers after a reload that also
  replaced valheimCLI (see below).
- Without an md5 (`-`) only a new instance counts: that proves a reload, not a
  build.

Issue the call right after the copy: `AutoReloadDelay` is the window in which
it must arrive. A reload that finished before the call is not seen as new and
the call times out, unless the md5 case above applies.

On timeout:

```text
ERROR: code=await_timeout message=com.example.mymod not reloaded within 60s: no new instance loaded; loaded now: MyMod-638999999999999999
```

The client's `--timeout` (default 120s) bounds the whole request; raise it for
a longer wait.

## Where to keep valheimCLI

Keep `valheimCLI.dll` in `BepInEx/plugins` while you reload other plugins.
ScriptEngine reloads everything in `BepInEx/scripts` together, so a copy of
valheimCLI there is replaced by every reload, and the connection that was
waiting closes. The client then exits with code 3 and
`ERROR: code=connection_closed`; reconnect and ask again with the md5, which
the same-pass rule answers at once.

## Reloading valheimCLI itself

The new instance destroys the one serving the request before that one could
answer, so the connection closes: `cli_await_plugin` cannot report its own
replacement. Reconnect and ask `cli_build`:

```text
OK: BUILD guid=valheimCLI.valheimCLI version=1.0.0 assembly=valheimCLI-639000000000000000 source=scripts md5=0cc175b9c0f1b6a831c399e269772661 loaded=2025-01-02T03:04:05Z location=/path/to/Valheim/BepInEx/scripts/valheimCLI.dll
```

The md5 is of the file as it was when that instance loaded, so it does not
change when the next build is copied over the file.

`cli_await_plugin valheimCLI.valheimCLI <md5>` answers only one way: the
instance answering is already that build (`self=true`). Otherwise it holds the
request until the reload closes the connection (the event a script waits for),
or times out when no reload came. Without an md5 it refuses at once.

The flow, which `examples/reload-plugin.sh` follows:

1. `cli_build`: if its md5 is the new build's, stop.
2. If `source=plugins`, `cli_self_unload`: ScriptEngine refuses the GUID while
   that copy is registered. The reply arrives, then the port closes.
3. Copy the `.pdb` and `.dll` into `BepInEx/scripts`.
4. `cli_await_plugin valheimCLI.valheimCLI <md5>`: exit 0 means the new build
   already answers; exit 3 means the old one is gone.
5. `valheim-cli wait --for plugin-server`, then `cli_build` and compare the md5.

During the reload the port is closed for a moment; the new command server
retries the bind for up to 5 seconds while the old one lets go.

If a copy in `BepInEx/plugins` and one in `BepInEx/scripts` both end up loaded
(for example with both files present when the game starts), the older would keep the
port while the newer one's commands answer, and async replies would be lost.
The newest instance therefore unloads any other before it opens the port, and
logs a warning. Remove the `BepInEx/plugins` copy after moving
valheimCLI to `BepInEx/scripts`.

A closed connection is reported by the client as
`ERROR: code=connection_closed` with exit code 3, and a failure to connect also
exits 3. The command is never resent: it may have run. The interactive client
reconnects on the next command.

## Remote game

The command server listens on 127.0.0.1 only. Forward its port
(`ssh -N -L 5555:127.0.0.1:5555 host`) and copy the files to the remote
`BepInEx/scripts` with `scp`. The example copies over SSH when `REMOTE_HOST`
and `REMOTE_VALHEIM_PATH` are set; the tunnel is yours to open.
