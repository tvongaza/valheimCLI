using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace valheimCLI.Tests;

/// <summary>Proves that the default plugin keeps the commands from before the pack split.</summary>
public class CommandPackInventoryTests
{
    private static readonly string[] Removed =
    {
        "cli_mwl_port_status", "cli_mwl_goto_port", "cli_mwl_clear_shipments",
        "cli_mwl_port_payment_regression", "cli_mwl_port_delivery_regression",
        "cli_mwl_port_ownership_seed", "cli_mwl_port_ownership_check",
    };

    private static readonly string[] Added =
    {
        "cli_extension", "cli_extensions", "cli_access", "cli_generator_at",
        "cli_terrain_modifiers_at", "cli_acknowledge_local_cheats",
        "cli_teleport_trace_arm", "cli_teleport_trace_wait",
        "cli_teleport_test_mode", "cli_wait_teleportable", "cli_screenshot_path64",
        "cli_camera_arc", "cli_camera_path", "cli_camera_stop", "cli_camera_status", "cli_camera_clean", "cli_hud",
        "cli_destroy_prefabs_at", "cli_rock_hit",
    };

    private static readonly Regex Registration = new(@"new Terminal.ConsoleCommand\(""([^""]+)""");

    [Fact]
    public void EveryPreviousDefaultCommandIsRegisteredExactlyOnce()
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(RepoPaths.Of("docs/command-inventory-before.json")));
        HashSet<string> expected = json.RootElement.EnumerateObject()
            .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(Removed, name => Assert.Contains(name, expected));
        expected.ExceptWith(Removed);
        expected.UnionWith(Added);

        Dictionary<string, string> found = new(StringComparer.Ordinal);
        foreach (string folder in new[] { "Source", "Packs" })
            foreach (string path in SourceFiles(folder))
                foreach (Match match in Registration.Matches(File.ReadAllText(path)))
                {
                    string name = match.Groups[1].Value;
                    Assert.False(found.TryGetValue(name, out string? first),
                        $"Duplicate registration: {name} in {first} and {RepoPaths.Relative(path)}");
                    found.Add(name, RepoPaths.Relative(path));
                }

        Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal),
            found.Keys.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(147, found.Count);
    }

    [Fact]
    public void BuiltInSourcesProduceOneBepInExPlugin()
    {
        string project = File.ReadAllText(RepoPaths.Of("valheimCLI.csproj"));
        Assert.Contains("<Compile Include=\"Packs/**/*.cs\"", project);
        Assert.Contains("<AllowUnsafeBlocks>true</AllowUnsafeBlocks>", project);
        Assert.Empty(Directory.EnumerateFiles(RepoPaths.Of("Packs"), "*.csproj", SearchOption.AllDirectories));

        string[] plugins = SourceFiles("Source").Concat(SourceFiles("Packs"))
            .Where(path => File.ReadAllText(path).Contains("[BepInPlugin(", StringComparison.Ordinal))
            .Select(RepoPaths.Relative).ToArray();
        Assert.Equal(new[] { "Source/Plugin.cs" }, plugins);

        string builtIns = File.ReadAllText(RepoPaths.Of("Source/BuiltInCommands.cs"));
        foreach (string group in new[] { "standard", "worldTools", "reflection", "capture", "observe" })
            Assert.Contains($"_{group}.Register(core)", builtIns);
    }

    [Fact]
    public void ObserveCapabilitiesRemainInTheDefaultPlugin()
    {
        string observe = File.ReadAllText(RepoPaths.Of("Packs/Observe/ObservePack.cs"));
        string[] names = Regex.Matches(observe, "new ExtensionCommand\\(\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(13, names.Length);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("_observe.Register(core)", File.ReadAllText(RepoPaths.Of("Source/BuiltInCommands.cs")));
    }

    private static IEnumerable<string> SourceFiles(string folder) =>
        Directory.EnumerateFiles(RepoPaths.Of(folder), "*.cs", SearchOption.AllDirectories)
            .Where(path => !RepoPaths.Relative(path).Split('/').Any(part => part is "obj" or "bin"))
            .OrderBy(path => path, StringComparer.Ordinal);
}
