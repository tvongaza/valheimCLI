# Reproduce a world and export a small region

At the main menu, with no world running:

```
cli_create_world CliFixture reproducibleSeed
cli_start_local_world CliFixture
```

Creation writes local world metadata through Valheim's save API and re-reads it
to verify the name, seed and identity. It does not generate the world. Existing
names are refused unless `--overwrite` is explicitly supplied. That option
replaces a **local** world, including its old save; it is for disposable fixtures.
A cloud world with that name is always refused. Prefer a new name.

Successful creation replies with `OK: WORLD_CREATED` and the saved world's
name, seed, identity and generation version. Log messages alone are not a
success acknowledgement. Update both the plugin and terminal client: replies
preserve multiline log output, and malformed or incomplete responses now fail
explicitly and disconnect. A response error does not mean creation was undone;
inspect the save before deciding whether to try again. The CLI does not resend
the command automatically.

In a loaded world:

```
cli_world_dump 8 --window 1000,1000,64
```

The optional output directory goes after the step and before `--window`.
Without a directory, files go below the game's local save directory in
`valheimCLI/world-dumps/<world-name>`. Use different directories to retain runs;
repeating the same export replaces its CSV.

Samples share the full-world lattice `-10000 + index * step`; clipping a window
does not shift it. Coordinates are metres. Step must be 5–1000 metres. Windows
are clipped to -10000..10000. Exports exceeding 1,000,000 samples are refused
before files are written: use a smaller window or a coarser step. Export remains
synchronous and can pause gameplay; use small windows on a disposable world.

`world.csv` (or the window-named CSV) preserves the first five columns and adds
`river_width` and `base_height`:

```
x,z,height,biome,river,river_width,base_height
```

`height` is WorldGenerator.GetHeight. `base_height` is the raw
WorldGenerator.GetBaseHeight value, not metres of final terrain or a client's
edited collider height. These are generator data, not baked terrain edits.
Full exports also write `locations.csv`; windowed exports deliberately do not.
The reply includes the actual sample count, extent and output path. Generated
CSVs, world files and game assemblies do not belong in the repository.
