using System;
using System.Collections.Generic;
using valheimCLI;
using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// The rules behind live reload: where a plugin came from, what counts as a
/// proven reload for cli_await_plugin, its arguments, the port retry, and
/// which console commands an unloading instance may remove.
/// </summary>
public class LiveReloadTests
{
    private const string Ticks = "639000000000000000";

    [Theory]
    [InlineData("/games/Valheim/BepInEx/scripts/MyMod.dll", "MyMod-" + Ticks, "scripts")]
    [InlineData("/games/Valheim/BepInEx/scripts/sub/MyMod.dll", "MyMod-" + Ticks, "scripts")]
    [InlineData(@"C:\Steam\Valheim\BepInEx\scripts\MyMod.dll", "MyMod-" + Ticks, "scripts")]
    [InlineData("/games/Valheim/bepinex/Scripts/MyMod.dll", "MyMod", "scripts")]
    [InlineData("/games/Valheim/BepInEx/plugins/MyMod.dll", "MyMod", "plugins")]
    [InlineData(@"C:\Steam\Valheim\BepInEx\plugins\Author-MyMod\MyMod.dll", "MyMod", "plugins")]
    [InlineData("BepInEx/plugins/MyMod.dll", "MyMod", "plugins")]
    [InlineData("/elsewhere/MyMod.dll", "MyMod", "other")]
    [InlineData("", "MyMod-" + Ticks, "scripts")]
    [InlineData("", "MyMod", "unknown")]
    [InlineData(null, null, "unknown")]
    public void SourceComesFromThePathThenTheReloadName(string? location, string? assembly, string expected)
    {
        Assert.Equal(expected, LiveReload.ClassifySource(location, assembly));
    }

    [Fact]
    public void AFolderMerelyNamedScriptsIsNotTheScriptEngineFolder()
    {
        Assert.Equal("plugins", LiveReload.ClassifySource("/games/Valheim/BepInEx/plugins/scripts/MyMod.dll", "MyMod"));
        Assert.Equal("other", LiveReload.ClassifySource("/home/me/scripts/MyMod.dll", "MyMod"));
        Assert.Equal("other", LiveReload.ClassifySource("/games/NotBepInEx/scripts/MyMod.dll", "MyMod"));
    }

    [Fact]
    public void ReloadedNameSplitsIntoNameAndLoadTime()
    {
        DateTime when = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Local);
        Assert.True(LiveReload.TryParseReloadedName("My-Mod-" + when.Ticks, out string baseName, out DateTime loaded));
        Assert.Equal("My-Mod", baseName);
        Assert.Equal(when, loaded);
        Assert.Equal(DateTimeKind.Local, loaded.Kind);
    }

    [Theory]
    [InlineData("MyMod")]
    [InlineData("MyMod-")]
    [InlineData("-" + Ticks)]
    [InlineData("MyMod-1.2.3")]
    [InlineData("MyMod-beta")]
    [InlineData("MyMod-12345")]
    [InlineData("MyMod-63900000000000000x")]
    [InlineData("MyMod-+39000000000000000")]
    [InlineData("")]
    [InlineData(null)]
    public void NamesWithoutATickSuffixAreNotReloads(string? name)
    {
        Assert.False(LiveReload.TryParseReloadedName(name, out string baseName, out _));
        Assert.Equal(name ?? "", baseName);
    }

    [Theory]
    [InlineData("abcdef", true)]
    [InlineData("ABCDEF0123", true)]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdef0", false)]
    [InlineData("abcdeg", false)]
    [InlineData("-", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Md5PrefixIsSixToThirtyTwoHexDigits(string? text, bool valid)
    {
        Assert.Equal(valid, LiveReload.IsMd5Prefix(text));
    }

    [Theory]
    [InlineData("d41d8cd98f00b204e9800998ecf8427e", "d41d8c", true)]
    [InlineData("d41d8cd98f00b204e9800998ecf8427e", "D41D8C", true)]
    [InlineData("d41d8cd98f00b204e9800998ecf8427e", "d41d8cd98f00b204e9800998ecf8427e", true)]
    [InlineData("d41d8cd98f00b204e9800998ecf8427e", "e41d8c", false)]
    [InlineData("d41d8cd98f00b204e9800998ecf8427e", null, true)]
    [InlineData("", "d41d8c", false)]
    [InlineData(null, "d41d8c", false)]
    [InlineData(null, null, true)]
    public void Md5MatchesItsPrefix(string? md5, string? prefix, bool matches)
    {
        Assert.Equal(matches, LiveReload.Md5Matches(md5, prefix));
    }

    [Fact]
    public void HexIsLowerCaseWithoutSeparators()
    {
        Assert.Equal("00ff10ab", LiveReload.Hex(new byte[] { 0x00, 0xFF, 0x10, 0xAB }));
    }

    [Fact]
    public void PortBindRetriesOnlyWhileRunningAndWithinTheAttempts()
    {
        Assert.True(LiveReload.ShouldRetryPortBind(1, running: true));
        Assert.True(LiveReload.ShouldRetryPortBind(LiveReload.PortBindAttempts - 1, running: true));
        Assert.False(LiveReload.ShouldRetryPortBind(LiveReload.PortBindAttempts, running: true));
        Assert.False(LiveReload.ShouldRetryPortBind(1, running: false));
        Assert.Equal(5000, LiveReload.PortBindAttempts * LiveReload.PortBindRetryMs);
    }

    // ---- cli_await_plugin arguments ----

    private static AwaitPluginRequest Parse(params string[] args)
    {
        Assert.True(AwaitPluginRequest.TryParse(args, out AwaitPluginRequest request, out string error), error);
        return request;
    }

    private static string ParseError(params string[] args)
    {
        Assert.False(AwaitPluginRequest.TryParse(args, out _, out string error));
        return error;
    }

    [Fact]
    public void GuidAloneWaitsForAnyBuildWithTheDefaultTimeout()
    {
        AwaitPluginRequest request = Parse("com.example.mymod");
        Assert.Equal("com.example.mymod", request.Target);
        Assert.False(request.TargetsFile);
        Assert.Null(request.Md5Prefix);
        Assert.Equal(AwaitPluginRequest.DefaultTimeoutSeconds, request.TimeoutSeconds);
    }

    [Fact]
    public void Md5PrefixIsStoredLowerCase()
    {
        AwaitPluginRequest request = Parse("com.example.mymod", "D41D8CD9", "12.5");
        Assert.Equal("d41d8cd9", request.Md5Prefix);
        Assert.Equal(12.5, request.TimeoutSeconds);
    }

    [Fact]
    public void DashSkipsTheMd5()
    {
        AwaitPluginRequest request = Parse("com.example.mymod", "-", "60");
        Assert.Null(request.Md5Prefix);
        Assert.Equal(60, request.TimeoutSeconds);
    }

    [Fact]
    public void BadArgumentsSayWhatIsWrong()
    {
        Assert.StartsWith("Usage:", ParseError());
        Assert.StartsWith("Usage:", ParseError(" "));
        Assert.StartsWith("Usage:", ParseError("a", "-", "30", "extra"));
        Assert.Contains("md5-prefix", ParseError("com.example.mymod", "abc"));
        Assert.Contains("md5-prefix", ParseError("com.example.mymod", "60"));
        Assert.Contains("timeout", ParseError("com.example.mymod", "-", "0"));
        Assert.Contains("timeout", ParseError("com.example.mymod", "-", "-5"));
        Assert.Contains("timeout", ParseError("com.example.mymod", "-", "601"));
        Assert.Contains("timeout", ParseError("com.example.mymod", "-", "NaN"));
        Assert.Contains("timeout", ParseError("com.example.mymod", "-", "soon"));
        Assert.Contains("code=bad_input", ParseError("com.example.mymod", "-", "soon"));
    }

    [Fact]
    public void AGuidNamesThePluginExactly()
    {
        AwaitPluginRequest request = Parse("com.example.mymod");
        Assert.True(request.Matches("com.example.mymod", "/any/where/Other.dll"));
        Assert.False(request.Matches("com.example.MyMod", null));
        Assert.False(request.Matches("com.example.mymod.extra", null));
        Assert.False(request.Matches(null, null));
    }

    [Fact]
    public void AFileNameNamesThePluginByTheFileItLoadedFrom()
    {
        AwaitPluginRequest request = Parse("MyMod.dll", "-", "10");
        Assert.True(request.TargetsFile);
        Assert.True(request.Matches("com.example.mymod", "/games/Valheim/BepInEx/scripts/MyMod.dll"));
        Assert.True(request.Matches("anything", @"C:\Valheim\BepInEx\scripts\mymod.DLL"));
        Assert.True(request.Matches("anything", "MyMod.dll"));
        Assert.False(request.Matches("MyMod.dll", "/games/Valheim/BepInEx/scripts/NotMyMod.dll"));
        Assert.False(request.Matches("com.example.mymod", ""));
        Assert.False(request.Matches("com.example.mymod", null));
    }

    // ---- what proves a reload ----

    private static AwaitPluginRequest Request(string? md5Prefix) =>
        new AwaitPluginRequest { Target = "com.example.mymod", Md5Prefix = md5Prefix };

    [Fact]
    public void AnInstanceAliveAtTheCallWithoutALoadTimeMd5NeverCountsEvenWhenItsFileNowMatches()
    {
        // The copy that triggers the reload replaces the old instance's file first.
        PluginAwaiter awaiter = new PluginAwaiter(Request("aaaaaa"), new[] { 1 });
        int reads = 0;
        Assert.False(awaiter.Observe(1, () => { reads++; return "aaaaaa00"; }, out _));
        Assert.Equal(0, reads);
        Assert.Equal(0, awaiter.NewInstancesSeen);
    }

    [Fact]
    public void ANewInstanceCountsWhenNoMd5IsAsked()
    {
        PluginAwaiter awaiter = new PluginAwaiter(Request(null), new[] { 1 });
        Assert.True(awaiter.Observe(2, () => "bbbbbb00", out string md5));
        Assert.Equal("bbbbbb00", md5);
    }

    [Fact]
    public void ANewInstanceOfAnotherBuildDoesNotCountAndIsReported()
    {
        PluginAwaiter awaiter = new PluginAwaiter(Request("aaaaaa"), new[] { 1 });
        Assert.False(awaiter.Observe(2, () => "bbbbbb00", out _));
        Assert.Equal("bbbbbb00", awaiter.LastMismatchMd5);
        Assert.Equal(1, awaiter.NewInstancesSeen);
        Assert.True(awaiter.Observe(3, () => "aaaaaa00", out string md5));
        Assert.Equal("aaaaaa00", md5);
    }

    [Fact]
    public void TheMd5IsReadOnceWhenTheInstanceIsFirstSeen()
    {
        // A later copy of the awaited build must not turn an instance of the
        // previous build into a match.
        PluginAwaiter awaiter = new PluginAwaiter(Request("aaaaaa"), Array.Empty<int>());
        string file = "bbbbbb00";
        int reads = 0;
        Func<string?> read = () => { reads++; return file; };
        Assert.False(awaiter.Observe(2, read, out _));
        file = "aaaaaa00";
        Assert.False(awaiter.Observe(2, read, out string md5));
        Assert.Equal("bbbbbb00", md5);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void AnUnknownLocationIsAskedAgainOnTheNextScan()
    {
        PluginAwaiter awaiter = new PluginAwaiter(Request("aaaaaa"), Array.Empty<int>());
        Assert.False(awaiter.Observe(2, () => null, out _));
        Assert.Equal(0, awaiter.NewInstancesSeen);
        Assert.True(awaiter.Observe(2, () => "aaaaaa00", out _));
    }

    [Fact]
    public void AnInstanceLoadedWithTheAnsweringOneCountsByItsLoadTimeMd5()
    {
        // Both reloaded in one ScriptEngine pass: the caller reconnects and asks.
        Dictionary<int, string> loadMd5 = new() { [1] = "aaaaaa00" };
        PluginAwaiter awaiter = new PluginAwaiter(Request("aaaaaa"), new[] { 1 }, loadMd5);
        Assert.True(awaiter.Observe(1, () => throw new InvalidOperationException("the file now is never read"), out string md5));
        Assert.Equal("aaaaaa00", md5);
    }

    [Fact]
    public void ALoadTimeMd5OfAnotherBuildDoesNotCount()
    {
        Dictionary<int, string> loadMd5 = new() { [1] = "bbbbbb00" };
        PluginAwaiter awaiter = new PluginAwaiter(Request("aaaaaa"), new[] { 1 }, loadMd5);
        Assert.False(awaiter.Observe(1, () => "aaaaaa00", out string md5));
        Assert.Equal("bbbbbb00", md5);
        Assert.Equal("bbbbbb00", awaiter.LastMismatchMd5);
    }

    [Fact]
    public void WithoutAnMd5OnlyANewInstanceProvesAReload()
    {
        Dictionary<int, string> loadMd5 = new() { [1] = "aaaaaa00" };
        PluginAwaiter awaiter = new PluginAwaiter(Request(null), new[] { 1 }, loadMd5);
        Assert.False(awaiter.Observe(1, () => "aaaaaa00", out _));
        Assert.True(awaiter.Observe(2, () => "aaaaaa00", out _));
    }

    // ---- console commands an unloading instance owns ----

    private sealed class Cmd
    {
        public string Name = "";
    }

    [Fact]
    public void RegisteredIsWhatWasAddedOrReplaced()
    {
        Cmd vanilla = new Cmd { Name = "help" };
        Cmd oldMine = new Cmd { Name = "cli_build" };
        Cmd newMine = new Cmd { Name = "cli_build" };
        Cmd added = new Cmd { Name = "cli_new" };
        Dictionary<string, Cmd> before = new() { ["help"] = vanilla, ["cli_build"] = oldMine };
        Dictionary<string, Cmd> after = new() { ["help"] = vanilla, ["cli_build"] = newMine, ["cli_new"] = added };

        List<KeyValuePair<string, Cmd>> owned = LiveReload.Registered(before, after);

        Assert.Equal(2, owned.Count);
        Assert.Contains(owned, e => e.Key == "cli_build" && ReferenceEquals(e.Value, newMine));
        Assert.Contains(owned, e => e.Key == "cli_new" && ReferenceEquals(e.Value, added));
    }

    [Fact]
    public void UnloadRemovesOnlyEntriesStillHoldingItsOwnObjects()
    {
        Cmd mine = new Cmd { Name = "cli_build" };
        Cmd alsoMine = new Cmd { Name = "cli_other" };
        Cmd newer = new Cmd { Name = "cli_build" };
        Cmd vanilla = new Cmd { Name = "help" };
        List<KeyValuePair<string, Cmd>> owned = new()
        {
            new("cli_build", mine),
            new("cli_other", alsoMine),
            new("cli_gone", new Cmd())
        };
        Dictionary<string, Cmd> registry = new() { ["help"] = vanilla, ["cli_build"] = newer, ["cli_other"] = alsoMine };

        int removed = LiveReload.RemoveOwned(registry, owned);

        Assert.Equal(1, removed);
        Assert.Same(newer, registry["cli_build"]);
        Assert.Same(vanilla, registry["help"]);
        Assert.False(registry.ContainsKey("cli_other"));
    }
}
