extern alias FixtureAssembly;
using System.Text.Json;
using valheim_cli.Testing;
using Xunit;

namespace valheimCLI.Tests
{
public sealed class CliBuildManifestTests
{
    [Fact]
    public void ReadsPluginAndLiteralCommandFromBuildWithoutLoadingIt()
    {
        string file = typeof(FixtureAssembly::ManifestFixture.ManifestPlugin).Assembly.Location;
        var entry = Assert.Single(CliBuildManifest.Generate([file]));
        Assert.Contains("valheimCLI.manifest-test", entry.Plugins);
        Assert.Equal(1, entry.Extensions["valheim.observe"]["manifest-probe"]);
        Assert.Equal(64, entry.Sha256.Length);

        string output = Path.Combine(Path.GetTempPath(), "cli-manifest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            CliBuildManifest.Write("test-build", [file], output);
            using var json = JsonDocument.Parse(File.ReadAllText(output));
            Assert.Equal(1, json.RootElement.GetProperty("schema").GetInt32());
            Assert.Equal("test-build", json.RootElement.GetProperty("build").GetString());
            Assert.Equal("manifest-probe", Assert.Single(json.RootElement.GetProperty("files")[0]
                .GetProperty("extensions").GetProperty("valheim.observe").EnumerateObject()).Name);
        }
        finally { File.Delete(output); }
    }

    [Fact]
    public void RefusesFilesThatCannotBeAModBuild()
    {
        Assert.Throws<FileNotFoundException>(() => CliBuildManifest.Generate([Path.GetFullPath("missing-valheim-cli.dll")]));
        string file = Path.GetTempFileName();
        try { Assert.Throws<InvalidDataException>(() => CliBuildManifest.Generate([file])); }
        finally { File.Delete(file); }
    }
}
}
