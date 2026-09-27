# Integration fork and upstream publication

The fork is an interim delivery channel while owner PRs await review. It does not replace upstream contributions. Keep reviewable commits, refresh against the current integration head in a separate checkout, and never overwrite another agent's branch.

## Upstream PR groups

1. **Reliable test runner:** schema/assertion/error semantics, owned-launch cleanup, focused reproductions and migration guide. Existing commands stay compatible; ambiguous old plans receive actionable errors.
2. **Shared client/runner library:** executable consumes the same code C# tests use. No parallel protocol implementation.
3. **Versioned extension host:** lifecycle/gate tests, capability/result protocol, minimal bundled terrain observer and reload example. Submit after the real binding/reload check.
4. **Roads adoption (Roads owner's repo):** extracted terrain adapter and parity tests, optional observation plugin, two bounded system regressions. Toolkit packaging is its own project; do not add it to the shipped Roads DLL.

Draft bodies and test evidence belong with each change. The integration release manifest records included PR URLs and exact commits, including unmerged work. Once a PR lands, replace its fork commits with upstream commits and rerun the affected layers. Keep the fork's integration-only scripts out of unrelated owner PRs.

## Candidate packaging

Run local tests first and commit. Then:

```sh
python3 scripts/package-integration.py --output /absolute/new/candidate-directory
```

This creates a portable framework-dependent CLI (`dotnet cli/valheim-cli.dll`, targets .NET 9 and permits newer major runtimes; tested on .NET 10), the net48 plugin, three preview NuGet packages, license, source archive, build manifest and SHA256SUMS. It does not deploy or publish. Game/Unity/BepInEx/Roads assemblies and station configs are excluded. Source SDK requirements differ from runtime requirements: our local unit harness currently targets .NET 10.

The candidate is explicitly labelled **local-candidate-game-validation-pending**. Do not promote it based only on a successful package build. The in-game plugin preserves its current BepInEx identity for replacement; do not co-install upstream and fork copies. Release version and source hash distinguish the candidate; install pins must use the actual deployed file hashes, not the version string alone.

## Release gate

- Unit/synthetic/integration suites green; both Roads runtimes green.
- CLI executable and plugin compile; optional adapters compile against actual game references.
- Small game gate in `testing-toolkit.md` complete, with real loaded-build pins and cleanup evidence.
- Fresh install of published packages/examples, no dependency on private absolute paths.
- Cross-platform external CLI smoke; The Roads-local owned Windows dedicated launcher passed both persistence scenarios; its shared-library extraction is locally tested and awaits a repeat with the packaged binary. Client launch/account ownership remains outside this pilot. Attaching to a prepared game is supported by the transport.
- Confirm source and dependency licenses/notices; the CLI is MIT and the extracted Roads fixture has its source notice preserved.
- Review outgoing artifact contents and PR mapping; add release notes, dependency versions, rollback instructions and known limitations.
- Create owner PRs, then publish a clearly labelled integration prerelease to the fork. Do not wait for merge after validation. Attach every created PR to this task.

No public release or owner PR has been created by this implementation yet. This is deliberate until the remaining terrain/client boundary gate and publication review are complete. Extension reload and Roads dedicated persistence have separate recorded passes.
