# Ask a mod a question with cli_call

`cli_call` calls a static method, or reads a static field or property, of the
game or of any loaded mod, and prints what it returns. A mod exposes its state
through a static diagnostic method once; scripts then ask for it by name,
without a console command for every question.

```text
cli_call [--limit N] [--assembly NAME] <[Namespace.]Type.Member> [arg ...]
```

Options come before the member: `--limit N` bounds how many items of a
collection are printed (default 50), `--assembly NAME` looks only in
assemblies whose name starts with `NAME` (any case).

```bash
valheim-cli devcommands                                  # cli_call is cheat-gated
valheim-cli cli_call Version.GetVersionString
valheim-cli cli_call Utils.DistanceXZ 0,0,0 3,100,4
valheim-cli cli_call ZoneSystem.GetZone 100,0,-200
valheim-cli cli_call ZNet.ContainsValidIPv4 '"join 10.1.2.3:2456"'
valheim-cli cli_call --limit 3 Heightmap.GetAllHeightmaps
valheim-cli cli_call MyMod.Diagnostics.QueueLength       # your own mod
```

```text
VALUE "1.0.12"
OK: CALL Version.GetVersionString kind=method type=string

VALUE 5
OK: CALL Utils.DistanceXZ kind=method type=float

VALUE 2,-3
OK: CALL ZoneSystem.GetZone kind=method type=Vector2s

VALUE true
OUT ipAddress="10.1.2.3"
OK: CALL ZNet.ContainsValidIPv4 kind=method type=bool
```

## Access

The command is cheat-gated like the other `cli_` commands. In a world this
game hosts (single player or a hosted server) run `devcommands` first. On a
dedicated server the console is reached only through the CLI port; send
`devcommands` there the same way. On a client joined to a dedicated server
Valheim refuses every cheat command, `cli_call` included.

Non-public members are reachable. The command is a debugging tool and the
state worth looking at is usually private. Whatever it calls runs on the game
thread: a slow method stalls the frame, and a method that changes state
changes it.

## Naming the member

The last dot separates the member from the type. The type is its full name
(`MyMod.Diagnostics`) or any trailing part of it (`Diagnostics`). Nested types
use dots too (`Outer.Inner.Member`). Names are case-sensitive; a near miss in
case is suggested.

- A type whose full name is exactly what you typed wins over types that only
  end with it: `ZNet.x` is the game's `ZNet` even if a mod has `MyMod.ZNet`.
- Otherwise only types that have a static member of that name count. If more
  than one does, the reply is `ambiguous_type` with every candidate and its
  assembly; type more of the namespace.
- Static members inherited from a base class are found. Open generic types
  and generic methods are not callable (there is no way to give the type
  argument).
- The same full name defined in several assemblies is one type loaded more
  than once, not an ambiguity; see below.

### A mod reloaded in place

A script engine or hot-reload tool loads a new copy of a mod's assembly on
every reload, under a new name (`MyMod-<ticks>`), and the runtime never
unloads the old copies. Every type of the mod then exists once per reload,
with the same full name. `cli_call` calls one copy:

1. the copy whose assembly holds a running plugin (an entry in BepInEx's
   plugin table whose instance still exists, or a plugin component in the
   scene; a reload destroys the old instance), the newest of them if several
   do;
2. otherwise the copy loaded last.

The `OK:` line then names the copy and how many were passed over:

```text
VALUE "MyMod"
OK: CALL MyMod.Plugin.ModName kind=constant type=string assembly=MyMod-639258367862848272 stale_copies=1 chosen=live
```

`chosen=newest` means no copy holds a running plugin (a helper assembly with
no plugin in it) and load order decided. To read an older copy on purpose,
name its assembly: `cli_call --assembly MyMod-639258316676507905 MyMod.Plugin.ModName`.
Two types with different full names still make `ambiguous_type`; each
candidate is listed once, with the number of older copies. Unrelated mods
that define the very same full name are treated as copies too; the
`assembly=` field shows which one answered, and `--assembly` picks the other.

## Arguments

Arguments are separated by spaces. Put a string that contains spaces in
double quotes; inside quotes, `\"` is a quote and `\\` a backslash, and any
other backslash is kept as typed. Through `valheim-cli` the quotes must reach
the game, so protect them from your shell: `'"two words"'`.

| Parameter type | Text |
| --- | --- |
| `string` | anything; `""` is the empty string |
| `int`, `long`, other integers | `42`, `-7` (must fit the type) |
| `float`, `double`, `decimal` | `1.5`, `-2`, `1e3`: always a decimal point, whatever the locale |
| `bool` | `true`, `false` (any case) |
| enum | a name in any case (`swamp`), names joined by commas for a flags enum, or a number |
| vector | `x,y` / `x,y,z` / `x,y,z,w` without spaces: `Vector2`, `Vector3`, `Vector4`, `Quaternion`, `Vector2i`, `Vector2s` |
| `char` | a single character |
| nullable, class, interface | `null` (unquoted); `"null"` is the four-letter string |
| `object` | the text, as a string |

A vector is any struct whose public fields are exactly `x, y[, z[, w]]`, all
numbers. `out` parameters are not given; they are printed after the call, as
are `ref` parameters (which are given). Optional parameters may be left off.
Other parameter types (a `GameObject`, a list) accept only `null`.

### Overloads

An overload fits when the argument count lies between its required and its
total parameters and every argument converts. Of the fits, the most natural
reading wins: `5` is an `int` before a `long`, before a smaller integer type,
before a `double`, an enum or a string; `1.5` is a `double` before a `float`;
a quoted argument is a string first. On a tie the one that leaves fewest
optional parameters to their defaults wins. A remaining tie is reported as
`ambiguous_overload` with the tied signatures; quote a string or add a
decimal point to choose. When overloads exist the `OK:` line names the one
used: `overload=(Vector3,Vector3)`.

## Output

| Line | Meaning |
| --- | --- |
| `VALUE <text>` | the result; not printed for a `void` method |
| `ITEM <i> <text>` | one per item, when the result is a collection or sequence |
| `MORE ...` | how many items `--limit` (default 50, at most 10000) left out |
| `OUT <name>=<text>` | an `out` or `ref` parameter after the call |
| `OK: CALL <Type.Member> kind=<method\|field\|property\|constant> type=<type>` | always last on success; `items=<n> shown=<m>` for a collection; `assembly=<name> stale_copies=<n> chosen=<live\|newest>` when the type is loaded more than once |
| `ERROR: code=<code> message=<text>` | the failure; detail lines follow, indented |

Values are printed on one line:

- `null`; strings in double quotes with `\"`, `\\`, `\n`, `\r`, `\t` escaped,
  so `""`, `"null"` and `null` differ.
- Numbers with the invariant culture, round-trip precision; `true`/`false`;
  enums by name.
- Vectors as they are typed: `1.5,-2,3`. One call's output can be the next
  call's argument.
- A type with its own `ToString` uses it (newlines escaped). Unity objects
  print as `name (Type)`.
- Anything else lists its public fields and properties one level deep:
  `SpawnStats count=3 name="north gate" origin=1.5,2,-3 cells=List<int>(count=2)`.
  Nested objects print only their type name, nested collections their count,
  a property getter that throws prints `<threw ExceptionType>`, and at most 24
  members are shown.
- A collection prints one `ITEM` line per entry, dictionaries as
  `key => value`. A collection reports its full count. A lazy sequence is
  read only up to the limit plus one item (it may be endless), so its count
  is `items=?` when more remain.

A reply that ends with `OK: CALL` is a success for `valheim-cli` whatever
the returned value says: text such as `usage:` or `timed out` inside a
`VALUE` line is data. Only an `ERROR:` line fails it.

## Errors

| Code | Cause |
| --- | --- |
| `bad_request` | no `Type.Member`, an unterminated quote, a bad `--limit` or `--assembly` |
| `no_type` | no loaded type has that name (near misses in case are suggested), or none in the assemblies `--assembly` names (the assemblies that do have it are listed) |
| `ambiguous_type` | several types of that name have the member; candidates listed |
| `no_member` | the type has no static member of that name; its static members are listed |
| `no_overload` | no overload takes that many arguments, or none accepts them; overloads listed |
| `ambiguous_overload` | several overloads fit equally well; the tied ones listed |
| `bad_argument` | an argument cannot be read as its parameter, or a field/property was given arguments |
| `not_callable` | a generic method, a pointer parameter, a write-only property |
| `call_threw` | the member threw: the exception type and message; a failing static constructor adds its cause. The full stack trace goes to the BepInEx log |
| `call_failed` | reflection could not make the call |

## What it does not do

- It reads fields and properties; it does not assign them. A property setter
  is a method named `set_Name` and can be called like one; a field cannot be
  written.
- It reaches static members only. To look at an instance, give your mod a
  static method that finds it and returns what you want to see.
- A `params` array parameter is an ordinary array parameter: it accepts only
  `null`.

## Sampling a value over time

`examples/sample-value.sh` calls one member at a fixed interval and writes a
CSV with a UTC timestamp per sample, for a time series of a mod's state:

```bash
examples/sample-value.sh EnvMan.IsDay 10 5 > day.csv
examples/sample-value.sh MyMod.Diagnostics.QueueLength 120 0.5 > queue.csv
```
