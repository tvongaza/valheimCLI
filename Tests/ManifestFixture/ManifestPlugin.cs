using System;

namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class BepInPlugin(string guid, string name, string version) : Attribute
    {
        public string Guid { get; } = guid;
        public string Name { get; } = name;
        public string Version { get; } = version;
    }
}

namespace valheimCLI.Extensions
{
    public sealed class ExtensionCommand
    {
        public string Name { get; }
        public int Version { get; }
        public ExtensionCommand(string name, object? a, object? b, object? c, object? d, object? e, int version)
        { Name = name; Version = version; _ = (a, b, c, d, e); }
    }
    public sealed class ExtensionRegistry
    {
        public void Register(string id, string version, int apiVersion, ExtensionCommand[] commands) { }
    }
}

namespace ManifestFixture
{
    [BepInEx.BepInPlugin("valheimCLI.manifest-test", "Manifest test", "1.0.0")]
    public sealed class ManifestPlugin
    {
        public static void Register(valheimCLI.Extensions.ExtensionRegistry registry) =>
            registry.Register("valheim.observe", "0.1.0", 1,
                new valheimCLI.Extensions.ExtensionCommand[]
                {
                    new("manifest-probe", null, null, null, null, null, 1)
                });
    }
}
