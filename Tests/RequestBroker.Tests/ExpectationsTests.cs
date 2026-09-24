using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using valheimCLI;
using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// The expectations file format, the check of a game against it (plugins,
/// world, strict mode), the report lines the CLI reads back, the snapshot
/// writer and the world-files hash recipe.
/// </summary>
public class ExpectationsTests
{
    private const string JotunnMd5 = "0123456789abcdef0123456789abcdef";
    private const string CliMd5 = "fedcba9876543210fedcba9876543210";

    private static List<PluginFacts> Game() => new List<PluginFacts>
    {
        new PluginFacts { Guid = "com.jotunn.jotunn", Name = "Jotunn", Version = "2.20.0", File = "/game/BepInEx/plugins/Jotunn.dll", Md5 = JotunnMd5 },
        new PluginFacts { Guid = "valheimCLI.valheimCLI", Name = "valheim CLI", Version = "1.0.0", File = "/game/BepInEx/plugins/valheimCLI.dll", Md5 = CliMd5 },
    };

    private static WorldFacts NoWorld() => new WorldFacts();

    private static WorldFacts Dedicated() => new WorldFacts
    {
        Name = "Test World", Seed = "AbCdEfGh12", Uid = "-42", WorldGen = "2",
        Files = "aaaabbbbccccdddd0000111122223333", FilesHashed = WorldFilesHashed.AtLoad, Dir = "/saves/worlds_local/Test World/"
    };

    private static List<Expectation> Parse(params string[] lines)
    {
        List<string> errors = new List<string>();
        List<Expectation> result = Expectations.ParseLines(lines, errors);
        Assert.Empty(errors);
        return result;
    }

    private static List<string> Check(List<Expectation> e, WorldFacts? world = null, bool strict = false, bool waitForWorld = false, List<PluginFacts>? plugins = null) =>
        Expectations.Check(e, plugins ?? Game(), world ?? NoWorld(), strict, waitForWorld);

    // ---- file parsing ----

    [Fact]
    public void CommentsBlankLinesAndTrailingCommentsAreSkipped()
    {
        List<Expectation> e = Parse(
            "# pinned builds",
            "",
            "   ",
            "com.jotunn.jotunn=" + JotunnMd5.ToUpperInvariant() + "   # Jotunn 2.20.0",
            "\tvalheimCLI=ANY\t",
            "world=My#World");
        Assert.Equal(new[] { "com.jotunn.jotunn=" + JotunnMd5, "valheimCLI=any", "world=My#World" }, e.Select(x => x.ToString()));
        Assert.Equal(new[] { 4, 5, 6 }, e.Select(x => x.Line));
    }

    [Fact]
    public void MalformedLinesAreReportedWithTheirLineNumbersAndLeftOut()
    {
        List<string> errors = new List<string>();
        List<Expectation> e = Expectations.ParseLines(new[]
        {
            "no-equals-sign",
            "=0123456789abcdef",
            "Jotunn=",
            "Jotunn=0123 4567",
            "Jotunn=0123456",          // shorter than 8
            "Jotunn=0123456789abcdef0", // 17 is fine
            "Jotunn=0123456789abcdef0123456789abcdef0", // 33 is not
            "Other=zzzzzzzz",
            "worlduid=twelve",
            "worldfiles=any",
            "seed=Seed",
            "SEED=Other",
        }, errors);
        Assert.Equal(new[] { "Jotunn=0123456789abcdef0", "seed=Seed" }, e.Select(x => x.ToString()));
        Assert.Equal(10, errors.Count);
        Assert.StartsWith("line 1: ", errors[0]);
        Assert.StartsWith("line 4: ", errors[3]);
        Assert.Contains("already expected on line 11", errors[9]);
        Assert.StartsWith("line 12: ", errors[9]);
    }

    [Fact]
    public void WorldKeysAreCaseInsensitiveAndKeepTheirValueCase()
    {
        List<Expectation> e = Parse("World=Test_World", "WorldUID=-42", "WORLDFILES=AAAABBBB", "world2=any");
        Assert.Equal(new[] { "world=Test_World", "worlduid=-42", "worldfiles=aaaabbbb", "world2=any" }, e.Select(x => x.ToString()));
    }

    [Fact]
    public void AFileBecomesOneCliExpectCommand()
    {
        List<Expectation> e = Parse("# pins", "Jotunn=01234567  # a comment", "world=any");
        Assert.Equal("cli_expect Jotunn=01234567 world=any", Expectations.ExpectCommand(e, strict: false));
        Assert.Equal("cli_expect --strict Jotunn=01234567 world=any", Expectations.ExpectCommand(e, strict: true));
    }

    // ---- plugins ----

    [Fact]
    public void APluginIsNamedByGuidNameOrFileNameWithSpacesAsUnderscores()
    {
        Assert.Empty(Check(Parse("COM.JOTUNN.JOTUNN=" + JotunnMd5)));
        Assert.Empty(Check(Parse("jotunn=" + JotunnMd5)));
        Assert.Empty(Check(Parse("valheim_CLI=" + CliMd5)));
        Assert.Empty(Check(Parse("valheimcli=" + CliMd5)));
    }

    [Fact]
    public void AnMd5PrefixOfEightOrMoreCharactersMatchesFromTheStart()
    {
        Assert.Empty(Check(Parse("Jotunn=01234567")));
        Assert.Empty(Check(Parse("Jotunn=0123456789ABCDEF")));
        string problem = Assert.Single(Check(Parse("Jotunn=12345678")));
        Assert.Equal("Jotunn: md5 01234567, expected 12345678 (Jotunn.dll)", problem);
        Assert.Single(Check(Parse("Jotunn=89abcdef")));   // a substring that is not the start
    }

    [Fact]
    public void MissingAmbiguousAndFilelessPluginsFail()
    {
        Assert.Equal("Missing: not loaded, expected any", Assert.Single(Check(Parse("Missing=any"))));

        List<PluginFacts> twins = Game();
        twins.Add(new PluginFacts { Guid = "other.jotunn", Name = "Jotunn", File = "/x/Other.dll", Md5 = JotunnMd5 });
        Assert.Contains("names 2 loaded plugins", Assert.Single(Check(Parse("Jotunn=any"), plugins: twins)));
        Assert.Empty(Check(Parse("com.jotunn.jotunn=" + JotunnMd5), plugins: twins));

        List<PluginFacts> fileless = new List<PluginFacts> { new PluginFacts { Guid = "mem.plugin", Name = "Mem" } };
        Assert.Contains("no file on disk", Assert.Single(Check(Parse("Mem=01234567"), plugins: fileless)));
        Assert.Empty(Check(Parse("Mem=any"), plugins: fileless));
    }

    [Fact]
    public void AbsentFailsOnlyWhenThePluginIsLoaded()
    {
        Assert.Empty(Check(Parse("SomeClientMod=absent")));
        Assert.Equal("Jotunn: loaded (Jotunn.dll), expected absent", Assert.Single(Check(Parse("Jotunn=absent"))));
    }

    [Fact]
    public void AFileWrittenAfterLoadFailsAnMd5ButNotAny()
    {
        List<PluginFacts> plugins = Game();
        plugins[0].ChangedSinceLoad = FileChange.Yes;
        Assert.Contains("written after the game loaded it", Assert.Single(Check(Parse("Jotunn=" + JotunnMd5), plugins: plugins)));
        Assert.Empty(Check(Parse("Jotunn=any"), plugins: plugins));
    }

    [Fact]
    public void AnUnknownLoadTimeStillChecksTheMd5ButDoesNotFailOnIt()
    {
        List<PluginFacts> plugins = Game();
        plugins[0].ChangedSinceLoad = FileChange.Unknown;
        Assert.Empty(Check(Parse("Jotunn=" + JotunnMd5), plugins: plugins));
        Assert.Equal("Jotunn: md5 01234567, expected 11111111 (Jotunn.dll)", Assert.Single(Check(Parse("Jotunn=11111111"), plugins: plugins)));
    }

    private static readonly DateTime ProcessStart = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ReloadedAt = ProcessStart.AddHours(1);

    [Fact]
    public void OwnPluginLiveReloadedFromBytesComparesWithItsOwnLoadTime()
    {
        // The file was written a second before the reload loaded it: unchanged,
        // even though it is an hour newer than the process.
        Assert.Equal(FileChange.No, Expectations.ChangedSinceLoad(isOwn: true, loadedFromBytes: true, ReloadedAt.AddSeconds(-1), ReloadedAt, ProcessStart));
        Assert.Equal(FileChange.Yes, Expectations.ChangedSinceLoad(isOwn: true, loadedFromBytes: true, ReloadedAt.AddSeconds(1), ReloadedAt, ProcessStart));
        Assert.Equal(FileChange.No, Expectations.ChangedSinceLoad(isOwn: true, loadedFromBytes: false, ProcessStart.AddSeconds(-5), ProcessStart.AddSeconds(2), ProcessStart));
        Assert.Equal(FileChange.Yes, Expectations.ChangedSinceLoad(isOwn: true, loadedFromBytes: false, ProcessStart.AddSeconds(3), ProcessStart.AddSeconds(2), ProcessStart));
        Assert.Equal(FileChange.Unknown, Expectations.ChangedSinceLoad(isOwn: true, loadedFromBytes: true, ReloadedAt, null, ProcessStart));
    }

    [Fact]
    public void AnotherPluginComparesWithProcessStartUnlessLoadedFromBytes()
    {
        Assert.Equal(FileChange.No, Expectations.ChangedSinceLoad(isOwn: false, loadedFromBytes: false, ProcessStart.AddSeconds(-1), ReloadedAt, ProcessStart));
        Assert.Equal(FileChange.Yes, Expectations.ChangedSinceLoad(isOwn: false, loadedFromBytes: false, ProcessStart.AddSeconds(1), ReloadedAt, ProcessStart));
        Assert.Equal(FileChange.Unknown, Expectations.ChangedSinceLoad(isOwn: false, loadedFromBytes: true, ProcessStart.AddSeconds(-1), ReloadedAt, ProcessStart));
        Assert.Equal(FileChange.Unknown, Expectations.ChangedSinceLoad(isOwn: false, loadedFromBytes: true, ReloadedAt.AddSeconds(1), ReloadedAt, ProcessStart));
    }

    // ---- world ----

    [Fact]
    public void WorldKeysFailWithoutAWorldUnlessTheyWaitForOne()
    {
        List<Expectation> e = Parse("world=Test_World", "seed=AbCdEfGh12");
        Assert.Equal(2, Check(e).Count);
        Assert.Empty(Check(e, waitForWorld: true));
        Assert.Empty(Check(e, Dedicated(), waitForWorld: true));
    }

    [Fact]
    public void WorldNameIgnoresCaseButSeedAndUidAreExact()
    {
        Assert.Empty(Check(Parse("world=test_world", "seed=AbCdEfGh12", "worlduid=-42"), Dedicated()));
        Assert.Empty(Check(Parse("world=any"), Dedicated()));
        Assert.Equal("seed: AbCdEfGh12, expected abcdefgh12", Assert.Single(Check(Parse("seed=abcdefgh12"), Dedicated())));
        Assert.Equal("worlduid: -42, expected 42", Assert.Single(Check(Parse("worlduid=42"), Dedicated())));
        Assert.Equal("world: Test World, expected Other", Assert.Single(Check(Parse("world=Other"), Dedicated())));
    }

    [Fact]
    public void WorldFilesNeedTheHashTakenAtLoad()
    {
        Assert.Empty(Check(Parse("worldfiles=aaaabbbb"), Dedicated()));
        Assert.Equal("worldfiles: aaaabbbb, expected aaaabbbc", Assert.Single(Check(Parse("worldfiles=aaaabbbc"), Dedicated())));

        WorldFacts client = Dedicated();
        client.Files = "";
        client.FilesHashed = WorldFilesHashed.ClientHasNoFiles;
        Assert.Contains("a client has no world files", Assert.Single(Check(Parse("worldfiles=aaaabbbb"), client)));

        WorldFacts late = Dedicated();
        late.Files = "";
        late.FilesHashed = WorldFilesHashed.NotAtLoad;
        Assert.Contains("loaded before valheimCLI", Assert.Single(Check(Parse("worldfiles=aaaabbbb"), late)));

        WorldFacts fresh = Dedicated();
        fresh.Files = "";
        fresh.FilesHashed = WorldFilesHashed.NoDirectory;
        Assert.Contains("no save directory at load", Assert.Single(Check(Parse("worldfiles=aaaabbbb"), fresh)));
    }

    // ---- strict ----

    [Fact]
    public void StrictFailsAPluginTheFileDoesNotList()
    {
        List<Expectation> e = Parse("Jotunn=" + JotunnMd5);
        Assert.Empty(Check(e));
        string problem = Assert.Single(Check(e, strict: true));
        Assert.Equal("valheimCLI.valheimCLI: loaded but not listed (strict), md5 fedcba98 (valheimCLI.dll)", problem);
    }

    [Fact]
    public void StrictAcceptsAnyAndDoesNotReportAnAbsentPluginTwice()
    {
        Assert.Empty(Check(Parse("Jotunn=" + JotunnMd5, "valheimCLI=any"), strict: true));
        List<string> problems = Check(Parse("Jotunn=absent", "valheimCLI=any"), strict: true);
        Assert.Equal("Jotunn: loaded (Jotunn.dll), expected absent", Assert.Single(problems));
    }

    [Fact]
    public void StrictNeedsTheLoadedWorldNamed()
    {
        List<Expectation> plugins = Parse("Jotunn=any", "valheimCLI=any");
        Assert.Empty(Check(plugins, strict: true, waitForWorld: true));
        Assert.Contains("is loaded but not listed (strict)", Assert.Single(Check(plugins, Dedicated(), strict: true)));

        Assert.Empty(Check(Parse("Jotunn=any", "valheimCLI=any", "world=Test_World"), Dedicated(), strict: true));
        Assert.Empty(Check(Parse("Jotunn=any", "valheimCLI=any", "worlduid=-42"), Dedicated(), strict: true));
        Assert.Empty(Check(Parse("Jotunn=any", "valheimCLI=any", "world=any"), Dedicated(), strict: true));
        // seed and worldfiles do not name a world: many worlds share a seed.
        Assert.Single(Check(Parse("Jotunn=any", "valheimCLI=any", "seed=AbCdEfGh12"), Dedicated(), strict: true));
    }

    // ---- report lines ----

    [Fact]
    public void PluginLinesRoundTripWithSpacesInTheFileAndName()
    {
        PluginFacts p = new PluginFacts { Guid = "a.b", Name = "My Mod", Version = "1.2.3", File = "/Steam Library/plugins/My Mod.dll", Md5 = JotunnMd5, ChangedSinceLoad = FileChange.Yes };
        string line = Expectations.FormatPlugin(p);
        Assert.Equal($"PLUGIN guid=a.b name=My_Mod version=1.2.3 md5={JotunnMd5} changed_since_load=yes file=/Steam Library/plugins/My Mod.dll", line);
        PluginFacts back = Expectations.ParsePlugin(line)!;
        Assert.Equal(("a.b", "My_Mod", "1.2.3", "/Steam Library/plugins/My Mod.dll", JotunnMd5, true),
            (back.Guid, back.Name, back.Version, back.File, back.Md5, back.ChangedSinceLoad == FileChange.Yes));
        p.ChangedSinceLoad = FileChange.Unknown;
        Assert.Contains(" changed_since_load=unknown ", Expectations.FormatPlugin(p));
        Assert.Equal(FileChange.Unknown, Expectations.ParsePlugin(Expectations.FormatPlugin(p))!.ChangedSinceLoad);

        PluginFacts none = Expectations.ParsePlugin(Expectations.FormatPlugin(new PluginFacts { Guid = "mem" }))!;
        Assert.Equal(("", "", FileChange.No), (none.Md5, none.File, none.ChangedSinceLoad));
        Assert.Null(Expectations.ParsePlugin("OK: MANIFEST plugins=2"));
    }

    [Fact]
    public void WorldLinesRoundTrip()
    {
        string line = Expectations.FormatWorld(Dedicated());
        Assert.Equal("WORLD name=Test_World seed=AbCdEfGh12 uid=-42 worldgen=2 files=aaaabbbbccccdddd0000111122223333 files_hashed=at_load dir=/saves/worlds_local/Test World/", line);
        WorldFacts back = Expectations.ParseWorld(line)!;
        Assert.Equal(("Test_World", "AbCdEfGh12", "-42", "2", "aaaabbbbccccdddd0000111122223333", "at_load", "/saves/worlds_local/Test World/"),
            (back.Name, back.Seed, back.Uid, back.WorldGen, back.Files, back.FilesHashed, back.Dir));

        WorldFacts client = Expectations.ParseWorld("WORLD name=W seed=S uid=1 worldgen=2 files=- files_hashed=client_has_no_files dir=")!;
        Assert.Equal(("", "client_has_no_files", ""), (client.Files, client.FilesHashed, client.Dir));
        Assert.Null(Expectations.ParseWorld("ERROR: code=no_world message=No world is loaded."));
    }

    // ---- snapshot ----

    [Fact]
    public void ASnapshotIsSatisfiedStrictlyByTheGameItWasTakenFrom()
    {
        List<PluginFacts> plugins = Game();
        plugins.Add(new PluginFacts { Guid = "mem plugin", Name = "Mem", Version = "0.1" });
        string text = Expectations.Snapshot(plugins, Dedicated(), new[] { "written by a test" });

        Assert.StartsWith("# written by a test\n", text);
        Assert.Contains($"com.jotunn.jotunn={JotunnMd5}  # Jotunn 2.20.0 (Jotunn.dll)\n", text);
        Assert.Contains("mem_plugin=any  # Mem 0.1 (no file on disk)\n", text);
        Assert.Contains("world=Test_World\nworlduid=-42\nseed=AbCdEfGh12\n", text);
        Assert.Contains("# worldfiles=aaaabbbbccccdddd0000111122223333\n", text);

        List<Expectation> e = Parse(text.Split('\n'));
        Assert.DoesNotContain(e, x => x.Key == "worldfiles");   // commented out: opt in by uncommenting
        Assert.Empty(Expectations.Check(e, plugins, Dedicated(), strict: true, waitForWorld: false));

        // Another build of one plugin is caught.
        plugins[0].Md5 = "11111111111111111111111111111111";
        Assert.Single(Expectations.Check(e, plugins, Dedicated(), strict: true, waitForWorld: false));
    }

    [Fact]
    public void ASnapshotWithoutAWorldHasNoWorldLines()
    {
        string text = Expectations.Snapshot(Game(), null, Array.Empty<string>());
        Assert.DoesNotContain("world=", text);
        Assert.Equal(2, Parse(text.Split('\n')).Count);
    }

    // ---- world files hash ----

    [Fact]
    public void DirectoryHashIsMd5OfSortedPathAndMd5Lines()
    {
        string dir = Path.Combine(Path.GetTempPath(), "expectations-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            Directory.CreateDirectory(Path.Combine(dir, "B"));
            File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(dir, "B.txt2"), "world");
            File.WriteAllText(Path.Combine(dir, "sub", "c.db"), "x\n");
            File.WriteAllText(Path.Combine(dir, "B", "empty"), "");

            // Ordinal order: upper case before lower, '.' (0x2E) before '/' (0x2F).
            string lines = "B.txt2:7d793037a0760186574b0282f2f435e7\n" +
                           "B/empty:d41d8cd98f00b204e9800998ecf8427e\n" +
                           "a.txt:5d41402abc4b2a76b9719d911017c592\n" +
                           "sub/c.db:401b30e3b8b5d629635a5c613cdb7919";
            string expected = Md5Hex(Encoding.UTF8.GetBytes(lines));
            Assert.Equal(expected, Expectations.HashDirectory(dir));
            // The value docs/expectations.md's shell and python recipes print for this tree.
            Assert.Equal("b2ecc81cc2260e9b5c128fdc8d0fd142", Expectations.HashDirectory(dir + Path.DirectorySeparatorChar));

            File.WriteAllText(Path.Combine(dir, "sub", "c.db"), "y\n");
            Assert.NotEqual(expected, Expectations.HashDirectory(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
        Assert.Equal("", Expectations.HashDirectory(dir));
    }

    private static string Md5Hex(byte[] bytes)
    {
        using System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create();
        return Convert.ToHexString(md5.ComputeHash(bytes)).ToLowerInvariant();
    }

    [Fact]
    public void RelativePathsResolveAgainstTheBaseFolder()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "config");
        Assert.Equal("", Expectations.ResolvePath("  ", baseDir));
        Assert.Equal(Path.Combine(baseDir, "pins.txt"), Expectations.ResolvePath("pins.txt", baseDir));
        string rooted = Path.Combine(Path.GetTempPath(), "pins.txt");
        Assert.Equal(rooted, Expectations.ResolvePath(rooted, baseDir));
    }

    // ---- standing file ----

    [Fact]
    public void StandingFileIsReReadWhenItChangesAndAMissingFileFails()
    {
        string path = Path.Combine(Path.GetTempPath(), "expect-" + Guid.NewGuid().ToString("N") + ".txt");
        StandingExpectations standing = new StandingExpectations();
        try
        {
            Assert.Empty(standing.Problems("", false, Game, NoWorld));
            Assert.Contains("not found", Assert.Single(standing.Problems(path, false, Game, NoWorld)));

            File.WriteAllText(path, "Jotunn=" + JotunnMd5 + "\nworld=Elsewhere\n");
            Assert.Empty(standing.Problems(path, false, Game, NoWorld));               // world keys wait for a world
            Assert.Equal("world: Test World, expected Elsewhere", Assert.Single(standing.Problems(path, false, Game, Dedicated)));
            Assert.Single(standing.Problems(path, true, Game, NoWorld));                // strict: valheimCLI is not listed

            File.WriteAllText(path, "Jotunn=11111111\nnot a line\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
            List<string> problems = standing.Problems(path, false, Game, NoWorld);
            Assert.Equal(2, problems.Count);
            Assert.Equal($"{path} line 2: 'not a line' is not key=value", problems[0]);
            Assert.StartsWith("Jotunn: md5 01234567, expected 11111111", problems[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StateChangeLogsOncePerChange()
    {
        StandingExpectations standing = new StandingExpectations();
        Assert.Null(standing.StateChange(new List<string>(), out _));
        string? first = standing.StateChange(new List<string> { "Jotunn: not loaded, expected any" }, out bool warning);
        Assert.True(warning);
        Assert.Contains("Jotunn: not loaded", first);
        Assert.Null(standing.StateChange(new List<string> { "Jotunn: not loaded, expected any" }, out _));
        Assert.NotNull(standing.StateChange(new List<string> { "Jotunn: not loaded, expected any", "world: A, expected B" }, out _));
        Assert.Contains("met again", standing.StateChange(new List<string>(), out warning));
        Assert.False(warning);
        Assert.Null(standing.StateChange(new List<string>(), out _));
    }

    [Fact]
    public void DiagnosticsRunWhileExpectationsFailAndFeaturesCanAddTheirs()
    {
        Assert.True(StandingExpectations.IsAllowedWhileMismatched("cli_manifest"));
        Assert.True(StandingExpectations.IsAllowedWhileMismatched("  CLI_EXPECT Jotunn=any"));
        Assert.True(StandingExpectations.IsAllowedWhileMismatched("cli_connection_status"));
        Assert.False(StandingExpectations.IsAllowedWhileMismatched("spawn Boar 5"));
        Assert.False(StandingExpectations.IsAllowedWhileMismatched("cli_run_trusted cli_manifest"));
        Assert.False(StandingExpectations.IsAllowedWhileMismatched(""));

        Assert.False(StandingExpectations.IsAllowedWhileMismatched("cli_test_only_diagnostic"));
        StandingExpectations.AllowWhileMismatched("cli_test_only_diagnostic");
        Assert.True(StandingExpectations.IsAllowedWhileMismatched("cli_test_only_diagnostic --verbose"));
    }
}
