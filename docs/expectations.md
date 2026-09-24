# Know which mods and which world you are testing

A test that ran against the wrong build looks like any other test until its
results stop making sense. The DLL copy failed because the game held the file
open, an old copy sat in another plugins folder, a teammate's machine has a
different release of a dependency, or the world is the one from yesterday's
run. These commands say what the game is actually running, and let a script
or the game itself refuse to go on when that is not what you expected.

| Command | Answers |
| --- | --- |
| `cli_manifest` | Every loaded BepInEx plugin: GUID, name, version, md5 of its DLL, whether the DLL changed after it was loaded, and its path |
| `cli_world` | The loaded world: name, seed, uid, world-gen version and, on a server or host, a hash of its save files as they were at load |
| `cli_expect [--strict] key=value ...` | `OK`, or one `MISMATCH` line per difference and `ERROR: code=expectation_mismatch` |

All three are ordinary console commands (not cheats), so they also answer on a
client joined to a dedicated server.

```text
$ valheim-cli cli_manifest
PLUGIN guid=com.example.mymod name=My_Mod version=1.4.0 md5=5f2b0c6e9a0d4c1e8b7a6f5e4d3c2b1a changed_since_load=no file=/path/to/Valheim/BepInEx/plugins/MyMod.dll
PLUGIN guid=valheimCLI.valheimCLI name=valheimCLI version=1.0.0 md5=0e1d2c3b4a5968778695a4b3c2d1e0f9 changed_since_load=no file=/path/to/Valheim/BepInEx/plugins/valheimCLI.dll
OK: MANIFEST plugins=2

$ valheim-cli cli_world
WORLD name=Dev seed=Qx7bT2mLpa uid=1234567890 worldgen=2 files=9c1f0a4b7e3d2c5a8b6f4e1d0c9b8a7f files_hashed=at_load dir=/path/to/worlds_local/Dev/
OK: WORLD

$ valheim-cli cli_expect MyMod=5f2b0c6e world=Dev
OK: EXPECT 2 expectation(s) met

$ valheim-cli cli_expect MyMod=11111111
MISMATCH MyMod: md5 5f2b0c6e, expected 11111111 (MyMod.dll)
ERROR: code=expectation_mismatch mismatches=1
```

Fields are space separated, so a space inside a name is printed as `_`. The
`file=` and `dir=` fields come last and are printed as they are, spaces
included.

## Expectations

An expectation is `key=value`. A plugin key is the plugin's GUID, its name, or
its DLL file name without `.dll`; case does not matter and `_` stands for a
space. If a name matches more than one loaded plugin, the expectation fails
and asks for the GUID.

| Value | Means |
| --- | --- |
| an md5 | The plugin's DLL has this md5. A prefix of 8 or more hex characters is enough; shorter prefixes are rejected because they match other builds by chance. |
| `any` | The plugin is loaded; any build. |
| `absent` | The plugin must not be loaded (for example a client-only mod on a server). |

The md5 is of the DLL as it is on disk now. If the file was written after the
game loaded it, the game may be running the previous build, so the plugin
reports `changed_since_load=yes` and fails an md5 expectation (`any` still
passes). A plugin loaded at startup is compared with the time the game
started; valheimCLI compares with the time it loaded itself, so a live reload
of valheimCLI is judged correctly. Another plugin that a reloader such as
ScriptEngine loaded from bytes after startup has no known load time: it
reports `changed_since_load=unknown`, and an md5 expectation checks only its
md5. A live-reload feature that loads the plugin can know more.

World keys:

| Key | Value |
| --- | --- |
| `world` | The world's name (case does not matter), or `any` |
| `seed` | The seed, exactly |
| `worlduid` | The world's uid as `cli_world` prints it |
| `worldfiles` | A prefix (8+ hex characters) of the save-files hash taken at load |

`worldfiles` is the strongest check: it proves the world's saved state, not
just its name. It only makes sense for a world you restore before each run,
because any save changes it. Only a server or host has the files; a client
joined to a server reports `files_hashed=client_has_no_files`, so check it on
the server. When valheimCLI loaded after the world (a plugin reloader), or the
world had no save directory yet (a new world), the load-time hash is unknown
and a `worldfiles` expectation fails with that reason.

## Expectations files

A file holds one expectation per line. `#` starts a comment at the start of a
line or after whitespace; blank lines are ignored. A malformed line, or a key
given twice, is an error that names the line.

```text
# The mod pack's known-good builds.
com.example.mymod=5f2b0c6e9a0d4c1e8b7a6f5e4d3c2b1a   # My Mod 1.4.0
com.example.library=any                               # any release will do
com.example.clientonly=absent

world=Dev
```

Write one from a running game instead of by hand:

```bash
valheim-cli manifest --write pins.txt               # every loaded plugin by GUID and md5
valheim-cli manifest --write pins.txt --with-world  # plus world, worlduid and seed
valheim-cli manifest                                # the same text on stdout
```

The snapshot lists the world's `worldfiles` hash as a commented line;
uncomment it when the world is restored from a copy before every run.

Then check a game against it:

```bash
valheim-cli --expect pins.txt                 # only the check: exit 0, or 6 with the mismatches
valheim-cli --expect pins.txt spawn Boar 5    # run the command only if the check passes
valheim-cli --expect-strict pins.txt          # strict mode (below)
```

Exit code 6 means the game does not match the file; 4 means the file itself
is missing or malformed. `--expect` applies to single commands and to a bare
`--expect` check; use a `cli_expect` step inside YAML test plans.

## Strict mode

By default a file only says what must hold of the plugins and world it names.
In strict mode it is a lock file:

- every loaded plugin must be named by the file (list a plugin as `any` when
  its build does not matter); a plugin the file does not name is a mismatch,
  reported with its md5 so it can be added;
- once a world is loaded, the file must name it with `world=` or `worlduid=`
  (`world=any` accepts any world, and says so). `seed` alone does not name a
  world: many worlds share a seed.

A snapshot from `manifest --write` always passes strict mode on the game it
came from.

## Standing expectations

Set a file in `BepInEx/config/valheimCLI.valheimCLI.cfg` and the game checks
it before every command sent through the CLI:

```ini
[Expectations]
## Relative paths are relative to BepInEx/config.
File = pins.txt
Strict = false
```

While any expectation fails, every command except the diagnostics is refused
with the mismatch list:

```text
MISMATCH com.example.mymod: md5 5f2b0c6e, expected 9d8c7b6a (MyMod.dll)
ERROR: code=expectation_mismatch message=Refused: this game does not match /path/to/BepInEx/config/pins.txt; cli_manifest, cli_world and cli_expect still answer.
```

- The diagnostics `cli_manifest`, `cli_world`, `cli_expect`,
  `cli_connection_status` and `help` always run, so the mismatch can be looked
  into. `cli_expect` with no pairs checks the configured file.
- The file is read again whenever it changes: a script that restores another
  world, or installs a build and restarts the game, rewrites it in place and
  the next command is checked against the new lines. Plugin hashes are cached
  by file write time.
- World keys wait until a world is loaded, so the main menu is not refused for
  a file that names a world.
- A configured file that does not exist is a mismatch, not "off": a typo in the
  path cannot silently disable the check. Set `File` empty to turn it off.
- The BepInEx log gets one warning, with the list, each time the result
  changes (and a note when everything holds again), including once at startup.
  It does not repeat per command.
- Commands typed into the in-game console are not checked.

A feature that adds another read-only diagnostic can let it run while the
check fails by calling `StandingExpectations.AllowWhileMismatched("cli_my_command")`
when it registers its commands.

## Recipes

A mod pack author pins the known-good set and ships it next to the pack:

```bash
valheim-cli manifest --write modpack.pins     # on a game that works
```

A tester who installs the pack sets `[Expectations] File` to it. A missing or
stale dependency now stops every scripted command with the plugin's name,
instead of a crash report that points somewhere else.

A CI job or test script refuses to measure a drifted install:

```bash
valheim-cli --expect-strict ci.pins || exit 1
valheim-cli -t tests/my-feature.yaml
```

A bug report says exactly what ran: ask for the output of
`valheim-cli cli_manifest` and `valheim-cli cli_world`, or for
`valheim-cli manifest --with-world`, which is also a file the maintainer can
check a local game against.

See `examples/pin-mods.sh` for snapshot-and-check in one script.

## World files hash

`files=` in `cli_world` and the `worldfiles` key use this recipe, over the
world's save directory (`worlds_local/<world name>/`):

1. For every file under the directory, recursively, take the line
   `relative/path:md5`, with `/` as the separator and the md5 in lower-case hex.
2. Sort the lines ordinally (byte order; `LC_ALL=C sort`).
3. Join them with `\n`, with no trailing newline.
4. The hash is the md5 of those bytes (UTF-8), in lower-case hex.

The game takes it just before it loads the world, so it is the saved state the
world was loaded from. Backups the game keeps elsewhere in `worlds_local` are
not included.

The same in Python:

```bash
python3 -c 'import hashlib,os,sys;r=sys.argv[1];print(hashlib.md5("\n".join(sorted(os.path.relpath(os.path.join(d,f),r).replace(os.sep,"/")+":"+hashlib.md5(open(os.path.join(d,f),"rb").read()).hexdigest() for d,_,fs in os.walk(r) for f in fs)).encode()).hexdigest())' "/path/to/worlds_local/Dev"
```

And in a shell (Linux `md5sum`; on macOS replace `md5sum | cut -d" " -f1` with
`md5 -q`):

```bash
cd "/path/to/worlds_local/Dev" && printf %s "$(find . -type f | while IFS= read -r f; do printf '%s:%s\n' "${f#./}" "$(md5sum < "$f" | cut -d" " -f1)"; done | LC_ALL=C sort)" | md5sum | cut -d" " -f1
```

`examples/world-hash.sh` wraps this for either system and can compare the
result with a running game's `cli_world`.
