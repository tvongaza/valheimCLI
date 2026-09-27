# Valheim testing toolkit preview

Assistant-written implementation notes. Preview prototype. The Roads dedicated save/restart and bridge-respawn pilot passed its bounded Valheim checks on 27 September. Shared-lifecycle extraction and terrain comparison additions are locally tested; paired terrain/client collision remains pending.

## Test pyramid

1. **Broad base: unit and synthetic tests.** Real mod code runs against small explicit doubles and reusable terrain inputs. Fast default CI, no Valheim installation. Test decisions, persistence formats, queues, ownership and failures here.
2. **Smaller integration layer.** Exercise the CLI protocol/runner, adapter registry, fixture handling and scenario orchestration together with controlled transports. No game required for most of these.
3. **Small system-test cap.** Real dedicated server for save/restart and bridge respawn; one instrumented client for terrain replication/collision; one actual ScriptEngine reload to verify assembly binding. Prepared worlds, a few zones, bounded commands. Record unexecuted cases as pending.
4. **Human judgement.** A short look/walk where appearance or usability matters. Do not turn every visual issue into an exact-coordinate assertion.

A successful fake transport run is an orchestration test, not an in-game pass. An input replay round-trip proves capture plumbing, not the game's terrain implementation. Broader fake physics or copied routing algorithms would weaken this pyramid.

## Components

| Component | Runtime and purpose |
|---|---|
| `valheimCLI.dll` | net48, stable BepInEx plugin: existing commands, transport, broker, extension host |
| `Valheim.Cli.Testing` | net9, the same client and YAML runner used by the executable; source lives in `CLI/Testing` and compiles once for the executable |
| `Valheim.Testing` | netstandard2.0, synthetic plane/island/ridge/river and exact captured-sample replay; works with net48/Mono and modern .NET |
| `Valheim.Testing.Game` | net9, named actors, typed observations, bounded observation waits, fixture copies, comparisons and JSON/JUnit reports |
| Roads pilot | Separate Roads checkout: test-only world adapter, game observation plugin and system scenarios |

The toolkit currently incubates in this branch to keep changes testable together. Its engine-free projects can move to the dedicated toolkit repository before first publication; package boundaries are already separate. The upstream CLI PR should include the client-library split and extension API, not demand ownership of Roads tests. No Unity/game DLL is a toolkit dependency. In-game adapters must not load the net9 test-side packages.

## Run locally

```sh
dotnet test Tests/RequestBroker.Tests/RequestBroker.Tests.csproj
dotnet test Toolkit/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj
dotnet build CLI/valheim-cli.csproj -c Release
# The next two require YOUR game/BepInEx assembly references in Environment.props:
dotnet build valheimCLI.csproj -c Release
dotnet build examples/ReloadProbe/ReloadProbe.csproj
```

The Roads pilot checkout has its own existing net10.0/net48 suite, the four extracted-fixture parity tests, and a separate `ProceduralRoads.SystemTests.Tests` suite. Existing test fixtures retain exact synthetic outputs; this requirement is about changing the input model, not demanding identical real-game road shapes.

## Extension API v1

Keep the core in normal `BepInEx/plugins`. Put only the optional test adapter in `BepInEx/scripts` for ScriptEngine reload. Do not load a second CLI/API assembly with the adapter. Core hot replacement during adapter work is not supported: finish work and restart the process. Assembly unload/managed-memory reclamation is not promised.

```csharp
registration = valheimCLIPlugin.Instance.Extensions.Register("my.mod.tests", "0.1.0", 1,
    new ExtensionCommand("snapshot", "Read a complete observation", Snapshot,
        readOnly: true, role: ExtensionRole.Server, needsWorld: true));
// OnDestroy: registration?.Dispose();
```

Registration validates every command before publishing any. Names are namespaced by owner. Each instance has a new token. Dispose removes capabilities immediately and cancels queued/active handlers. Cleanup runs in reverse order after work settles; cleanup failure prevents silent replacement. Handlers run on the main thread, yield `null` to wait a frame, and must return `context.Succeed(data)` or `context.Fail(code, message)`. Arbitrary Unity yield instructions are not supported in v1.

Mutation is the safe default (`readOnly: false`), requires devcommands, and uses the existing async operation gate. Joined-client mutation also requires the existing opt-in client setting. Trusted adapters can of course mislabel or ignore these rules; this is an API contract, not a sandbox. Tests must serialize other synchronous console mutations themselves.

If an effect continues after cancellation, install a `WaitForQuiescence` probe *before* issuing it. The core holds the gate and retiring owner until the probe confirms completion. A throwing/stuck probe leaves the owner blocked rather than claiming successful cleanup; diagnose and restart. The API cannot roll back arbitrary terrain/spawn/save effects.

Use `cli_extensions` to discover commands, instance tokens and result versions. Use `cli_extension owner/command args` over the normal CLI connection. JSON values are bounded to 256 KiB/16 levels, strings/finite numbers/bools/arrays/string-keyed objects. Unsupported values fail explicitly. The actor rejects stale instance tokens and incomplete measurements. Arguments are single tokens in this preview; adapters validate their own grammar.

The bundled `valheim.world/terrain x z generator|loaded-ground` observation demonstrates reuse. It distinguishes raw generator height from actual loaded heightmap ground. Missing heightmap returns `complete: false` and null height; it does not invent a zero.

## Actors, fixtures and evidence

A `GameActor` wraps the existing transport; it never owns/stops the attached process. Verify `cli_expect` pins before using an actor. Require a capability/version, issue a mutation once, then poll its read-only observation. Unknown outcomes are failures, not permission to resend. Keep the command timeout below the overall scenario allowance: a synchronous read cannot be forcibly interrupted by an observation deadline.

`WorldFixture.Copy` requires a hash manifest, refuses links and unexpected files, verifies copied bytes and creates its own new directory. It deletes only that copy; `Preserve=true` keeps it for debugging. Stop the owned game BEFORE disposing its world fixture. It does not own accounts, characters, ports or remote station locks. Those remain explicit session-operator responsibilities in preview 1.

`ScenarioReport` records outcomes and teardown failure in JSON/JUnit. Use a unique output directory per run; include fixture/build/config hashes in `Provenance`. Output may contain player/world data; inspect before publishing. Do not include credentials in fixture configuration or logs.

## Small game validation gate

- Reload probe A→B under ScriptEngine (**passed on Mac Valheim at the main menu, 26 September 2026; see [the repeatable check](../examples/ReloadCheck/README.md)**), with CLI core stable: change/remove a command, verify discovery and instance changed, retained command answers B, removed command is absent, connection remains usable.
- Roads empty-save scenario on a disposable old-network fixture: confirmed save, owned restart, zero cells/points/crossings, loaded-from-save true.
- Roads pending bridge append→respawn: prove pending before action; compare frozen independent expected pieces with marked ZDOs by full transform and multiplicity; save/restart and compare again.
- One matched-input replay/game boundary-road case and one Roads-absent instrumented-client observation: actual height, area readiness and collision. Preserve unknown/missing observations as incomplete. Use ordinary vanilla terrain for server-only acceptance.

Do not expand this into a full-world matrix per commit. These checks gate the preview release and relevant engine/adapter changes, not every local test iteration.

## Mod-owned adapters and compatibility commands

MWL port/shipment probes now belong to `MoreWorldLocations.TestAdapter` in MWL's
repository, with external assertions in `MoreWorldLocations.SystemTests`. The CLI
core no longer registers `cli_mwl_*` commands or resolves MWL types. Install the
optional adapter to retain those command names. An older core that still owns
the names is refused by the adapter rather than silently overwritten.

An adapter may register compatibility console commands calling
`ExtensionHost.Execute(registry, path, arguments, output)`. This is the same
dispatcher as `cli_extension`; it does not bypass role, world, devcommands, client
opt-in, cancellation or the mutation gate. The adapter must remove only its own
command instances when disposed. Successful results can include a bounded
`legacyLines` string array to retain established output alongside structured JSON.

Extension mutation checks read the raw devcommands flag. Valheim's
`IsCheatsEnabled()` also requires being the server, and would otherwise reject
every opted-in joined client. Ten pure policy tests cover the distinction and
ensure the opt-in cannot waive world or role restrictions. This does not expand
the waiver for ordinary console commands owned by other plugins.

Client-side follow-up: run an opted-in joined-client mutation and its refused
controls in a disposable fixture. Server Devcommands is a user-suggested reference
for enabling client devcommands if that check exposes a missing game-level step.
No dependency on that mod or general admin-command bypass has been added.


## Shared owned-session lifecycle (preview 2)

`Valheim.Testing.Game` now owns `OwnedServerSession`, `DirectServerProcess` and
`RecordingTransport`. Roads consumes these types instead of copying them. Other
mods provide their own namespaced session capability, for example
`other.mod.tests/session`; the shared package has no Roads dependency.

The launch callback receives a fresh unpredictable token for every boot. Pass it
to your prepared child process, and have your adapter report the exact token,
PID, actual save root, `dedicated` and `complete`, under source
`owned-test-session`. The session requires a valid extension result envelope from
the configured owner, verifies process/save identity, then strict environment
pins. Define `complete` to include your mod's readiness. It retries bounded
readiness observations, not mutations. Stop/restart owns only that process and
refuses to start a replacement after failed cleanup. Per-boot logs preserve prior
startup evidence. It does not own accounts, station locks or external processes.

The previous Roads-local implementation passed dedicated-server checks. The
extraction preserves its behavior with fast lifecycle/scenario tests and a second
mod owner fixture; do not present this as a new in-game run of the extracted DLL.

## Terrain checks and clean package examples

`TerrainProbe` validates coordinate identity, metres, completeness and explicit
generator versus loaded-ground layers. It compares up to 256 declared independent
expectations and reports every residual. See `examples/TerrainCheck` for a
read-only game check and `examples/NoGameTerrain` for a package-only, no-game
example with hand-derived plane expectations and explicit unknown replay data.
Neither a replay of its own capture nor a synthetic plane alone establishes
Valheim terrain conversion or client physics. That paired system gate remains.

The package examples accept `-p:ToolkitPackageVersion=0.1.0-preview.2` instead of
project references. Copy just an example directory outside this repository, add
the packaged `packages/` directory as a NuGet source alongside nuget.org (for
YamlDotNet), restore and run/build with that property. No game, Unity, BepInEx or
Roads assemblies belong in these NuGet packages. They are external test-driver
libraries; do not install them as game plugins.
