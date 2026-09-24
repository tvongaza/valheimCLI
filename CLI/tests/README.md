# valheimCLI Test Plans

`CLI/tests/` is for reusable sample plans that can run in a generic Valheim CLI setup.

Keep environment-specific validation plans out of this directory. Put local or private plans under `CLI/local-tests/`, and put run output under `CLI/runs/`. Those folders are ignored by git.

A `waitFor` step prints a heartbeat while it waits and fails early when the game stalls or sits in a state that cannot reach the target (waiting for `MainMenu` in a world). A step during which a person acts can set `stall: 0`; see "Readiness And Exit Codes" in the top-level README.

Generated evidence such as screenshots, logs, transcripts, and videos should not be committed. The test runner writes `summary.json` and `transcript.txt` under `CLI/runs/<timestamp>-<plan>/` by default.

Tracked examples:

- `example-spawn.yaml`: generic command execution example.
- `dedicated-connect.yaml`: reusable dedicated-server join plan using variables for server and password.
