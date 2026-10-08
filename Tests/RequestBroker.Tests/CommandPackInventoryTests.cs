using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;

namespace valheimCLI.Tests;

/// <summary>
/// Command preservation and source/dependency boundaries between core and the
/// command packs, read from the source tree; no game or game build needed.
/// </summary>
public class CommandPackInventoryTests
{
    // docs/command-inventory-before.json is every console command core registered
    // before the split (base 9765190). These left core entirely: MWL's optional
    // adapter owns them now.
    private static readonly string[] Removed =
    {
        "cli_mwl_port_status", "cli_mwl_goto_port", "cli_mwl_clear_shipments",
        "cli_mwl_port_payment_regression", "cli_mwl_port_delivery_regression",
        "cli_mwl_port_ownership_seed", "cli_mwl_port_ownership_check",
    };

    // New in core with the extension API.
    private static readonly string[] Added = { "cli_extension", "cli_extensions", "cli_access" };
    // New commands in optional packs, beyond the preserved pre-split inventory.
    private static readonly string[] NewPackCommands = { "cli_generator_at", "cli_terrain_modifiers_at", "cli_acknowledge_local_cheats", "cli_teleport_trace_arm", "cli_teleport_trace_wait", "cli_teleport_test_mode", "cli_wait_teleportable" };

    private static readonly string[] Packs = { "Standard", "WorldTools", "Capture", "Reflection", "Observe" };

    // Pack implementation types core must not name.
    private static readonly string[] PackTypes =
    {
        "CustomCommands", "WorldInspectionCommands", "TerrainInspectionCommands", "CaptureCommands",
        "BuildCommands", "AsyncCommands", "WorldObservations", "CallCommands", "StaticMemberCall", "CallValues",
    };

    // Reflection pack types World Tools must not name.
    private static readonly string[] ReflectionTypes = { "CallCommands", "StaticMemberCall", "CallValues" };

    // Where each install row of docs/command-packs.md registers its commands.
    private static readonly Dictionary<string, string> FolderOfDll = new(StringComparer.Ordinal)
    {
        ["valheimCLI.dll"] = "Source",
        ["Valheim.Cli.Standard.dll"] = "Packs/Standard",
        ["Valheim.Cli.WorldTools.dll"] = "Packs/WorldTools",
        ["Valheim.Cli.Reflection.dll"] = "Packs/Reflection",
        ["Valheim.Cli.Capture.dll"] = "Packs/Capture",
        ["Valheim.Cli.Observe.dll"] = "Packs/Observe",
    };

    private static readonly Regex Registration = new(@"new Terminal.ConsoleCommand\(""([^""]+)""");

    private readonly ITestOutputHelper _output;

    public CommandPackInventoryTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void InventoryIsThePreSplitState()
    {
        HashSet<string> before = InventoryBeforeTheSplit();
        Assert.Empty(Removed.Where(name => !before.Contains(name)));
        Assert.Empty(Added.Where(name => before.Contains(name)));
    }

    [Fact]
    public void EveryCommandIsKeptExactlyOnceExceptTheRemovedAndAddedOnes()
    {
        HashSet<string> before = InventoryBeforeTheSplit();
        Dictionary<string, string> found = FindRegistrations();
        var expected = new HashSet<string>(before, StringComparer.Ordinal);
        expected.ExceptWith(Removed);
        expected.UnionWith(Added);
        expected.UnionWith(NewPackCommands);
        var difference = new HashSet<string>(expected, StringComparer.Ordinal);
        difference.SymmetricExceptWith(found.Keys);
        Assert.True(difference.Count == 0,
            "Lost/added commands: " + string.Join(", ", difference.OrderBy(name => name, StringComparer.Ordinal)));

        _output.WriteLine($"PASS: {before.Count} commands before the split; {before.Count - Removed.Length} kept exactly once, " +
                          $"{Removed.Length} cli_mwl_* removed, {Added.Length + NewPackCommands.Length} added ({found.Count} now).");
        foreach (string folder in FolderOfDll.Values)
            _output.WriteLine($"{folder} {CountIn(found, folder)}");
    }

    [Fact]
    public void DocumentedCountsMatchTheRegistrations()
    {
        HashSet<string> before = InventoryBeforeTheSplit();
        Dictionary<string, string> found = FindRegistrations();
        string docs = File.ReadAllText(RepoPaths.Of("docs/command-packs.md"));

        var documented = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match row in Regex.Matches(docs, @"^\|\s*`(?<dll>[^`]+\.dll)`[^\n]*\|\s*(?<count>\d+)\s*\|\s*$", RegexOptions.Multiline))
        {
            string dll = row.Groups["dll"].Value;
            Assert.True(FolderOfDll.ContainsKey(dll), $"docs/command-packs.md lists {dll}, which this test does not know");
            documented.Add(FolderOfDll[dll], int.Parse(row.Groups["count"].Value));
        }
        Assert.Equal(FolderOfDll.Values.OrderBy(f => f, StringComparer.Ordinal).ToArray(),
                     documented.Keys.OrderBy(f => f, StringComparer.Ordinal).ToArray());
        foreach ((string folder, int count) in documented)
        {
            Assert.True(count == CountIn(found, folder),
                $"docs/command-packs.md says {count} commands for {folder}; the source registers {CountIn(found, folder)}");
            _output.WriteLine($"{folder} {count} (documented and registered)");
        }

        Assert.Equal(before.Count, DocumentedNumber(docs, @"core\s+registered\s+(\d+)\s+console\s+commands"));
        Assert.Equal(before.Count - Removed.Length, DocumentedNumber(docs, @"the\s+other\s+(\d+)\s+keep\s+their\s+names"));
        Assert.Equal(found.Count, DocumentedNumber(docs, @"for\s+(\d+)\s+in\s+total"));
    }

    [Fact]
    public void CoreHasNoPackImplementationReferences()
    {
        string core = string.Join("\n", SourceFiles("Source").Select(path => File.ReadAllText(path)));
        foreach (string name in PackTypes)
            Assert.False(Regex.IsMatch(core, $@"\b{name}\b"), $"Core depends on {name}");
    }

    [Fact]
    public void CoreProjectExcludesThePackSources()
    {
        Assert.Contains(@"<Compile Remove=""Packs\**"" />", File.ReadAllText(RepoPaths.Of("valheimCLI.csproj")));
    }

    [Fact]
    public void EachPackProjectReferencesOnlyCoreWithoutCopyingIt()
    {
        foreach (string pack in Packs)
        {
            string path = RepoPaths.Of($"Packs/{pack}/Valheim.Cli.{pack}.csproj");
            string project = File.ReadAllText(path);
            Assert.True(project.Contains("<AllowUnsafeBlocks>true</AllowUnsafeBlocks>"), $"{pack} lost Mono publicized-member access");
            Assert.True(project.Contains("<Private>false</Private>"), $"{pack} copies core into its output");

            XElement[] references = XDocument.Load(path).Descendants("ProjectReference").ToArray();
            Assert.True(references.Length == 1, $"{pack} has {references.Length} project references; core only is expected");
            string include = (string?)references[0].Attribute("Include") ?? "";
            Assert.True(include.Replace('\\', '/').EndsWith("/valheimCLI.csproj", StringComparison.Ordinal), $"{pack} references {include}, not core");
            Assert.Equal("false", (string?)references[0].Element("Private"));
        }
    }

    [Fact]
    public void WorldToolsDoesNotDependOnReflection()
    {
        foreach (string path in SourceFiles("Packs/WorldTools", SearchOption.TopDirectoryOnly))
        {
            string text = File.ReadAllText(path);
            foreach (string name in ReflectionTypes)
                Assert.False(Regex.IsMatch(text, $@"\b{name}\b"), $"World Tools depends on {name} in {RepoPaths.Relative(path)}");
        }
    }

    /// <summary>The .cs files under a repository folder, leaving out build output (obj/ and bin/).</summary>
    private static IEnumerable<string> SourceFiles(string folder, SearchOption option = SearchOption.AllDirectories) =>
        Directory.EnumerateFiles(RepoPaths.Of(folder), "*.cs", option)
            .Where(path => !RepoPaths.Relative(path).Split('/').Any(part => part is "obj" or "bin"))
            .OrderBy(path => path, StringComparer.Ordinal);

    private static HashSet<string> InventoryBeforeTheSplit()
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(RepoPaths.Of("docs/command-inventory-before.json")));
        return json.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Every console command registered in Source/ and Packs/, with the repository-relative file that registers it.</summary>
    private static Dictionary<string, string> FindRegistrations()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string folder in new[] { "Source", "Packs" })
            foreach (string path in SourceFiles(folder))
                foreach (Match match in Registration.Matches(File.ReadAllText(path)))
                {
                    string name = match.Groups[1].Value;
                    Assert.False(found.TryGetValue(name, out string? first), $"Duplicate registration: {name} in {first} and {RepoPaths.Relative(path)}");
                    found[name] = RepoPaths.Relative(path);
                }
        return found;
    }

    private static int CountIn(Dictionary<string, string> found, string folder) =>
        found.Values.Count(path => path.StartsWith(folder + "/", StringComparison.Ordinal));

    private static int DocumentedNumber(string docs, string pattern)
    {
        Match match = Regex.Match(docs, pattern);
        Assert.True(match.Success, $"docs/command-packs.md no longer states /{pattern}/");
        return int.Parse(match.Groups[1].Value);
    }
}
