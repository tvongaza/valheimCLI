# valheimCLI Test Plans

`CLI/tests/` is for reusable sample plans that can run in a generic Valheim CLI setup.

Keep environment-specific validation plans out of this directory. Put local or private plans under `CLI/local-tests/`, and put run output under `CLI/runs/`. Those folders are ignored by git.

A `waitFor` step prints a heartbeat while it waits and fails early when the game stalls or sits in a state that cannot reach the target (waiting for `MainMenu` in a world). A step during which a person acts can set `stall: 0`; see "Readiness And Exit Codes" in the top-level README.

Generated evidence such as screenshots, logs, transcripts, and videos should not be committed. The test runner writes `summary.json` and `transcript.txt` under `CLI/runs/<timestamp>-<plan>/` by default.

Tracked examples:

- `example-spawn.yaml`: generic command execution example.
- `dedicated-connect.yaml`: reusable dedicated-server join plan using variables for server and password.

## Reliable assertions (schema version 2)

Use `schemaVersion: 2` for new plans. Existing simple plans without a version
remain valid, but multi-command or repeated assertions must either opt into
version 2 or explicitly choose `expect.scope`. This prevents silently changing
what an older combined-output assertion meant.

```yaml
schemaVersion: 2
name: Inspect a road fixture
settings:
  timeout: 30s
  stopOnFailure: true
tests:
  - name: Query the loaded world
    commands:
      - cli_world
    expect:
      output: 'contains world='
```

Version 2 checks `expect.output` against the **last command of each repetition**.
Output from an earlier command or repetition cannot satisfy it. Prefer one
asserted action per case. `expect.scope: allCommands` deliberately checks all
commands in the current repetition; it never suppresses command failures.

Unknown or duplicate YAML fields, empty assertions, malformed/negative durations,
zero timeouts and unsupported `waitFor.event` are rejected before commands run.
Zero remains valid for `wait` and `waitFor.stall`.

Command errors, connection loss, missing output and local-process failures now
fail a case even without `expect`. The runner uses the same command-result
classification as ordinary CLI execution. An explicit expected error can test a
negative case:

```yaml
  - name: Removed member is unavailable
    commands:
      - cli_call Example.Diagnostics.RemovedAction
    expect:
      errorCode: command_failed
      output: 'contains code=no_member'
```

`errorCode` is the current CLI **result** code, not necessarily the detailed code
inside a plugin's reply. Many plugin errors currently map to `command_failed`, so
also check the specific reply as above. Only the final command may produce the
expected error; setup failures still fail. A command succeeding when an error is
expected also fails. Local exits use `local_exit_N`; local timeouts use
`local_timeout`. Local stdout containing error-like text is just data when the
process exits successfully.

`settings.timeout` now also sets the game command timeout (the transport retains
its response allowance). Cancellation is checked between synchronous game
commands; it does not interrupt a game command in flight or undo its effects.
Local shell processes are terminated on timeout or cancellation.

Plan cleanup is attempted once after errors or cancellation, with its own bounded
command execution. A wrong-build/world expectations check still runs **neither
cases nor cleanup**. Cleanup failures are recorded separately in `CleanupResults`
and count toward the run's errors and nonzero exit. Remaining cases after a stop
are recorded as skipped, with a reason; cancelled cases are reported explicitly.
Cleanup must be safe for a partially completed plan.

Artifacts use unique directories even for rapid repetitions of the same plan.
Each case records individual command results as well as its output. Recorded
command names omit arguments, which may contain credentials; command output and
verbose console logging may still contain sensitive data and need review before
sharing.

This is the runner foundation, not full session isolation. The existing game
launch/`stopAfter` behavior is unchanged: failed runs leave the game open and a
successful stop can use process-name lookup. Do not use `stopAfter` on a shared
machine/session. Owned-process fixture management, extension loading and typed
game observations are separate follow-up work.
