# Valheim.Cli.Testing

The transport and YAML test-plan runner shared by the valheim-cli executable and external tools. Targets .NET 9; no game assemblies. YamlDotNet 16.2.1 is an external MIT-licensed dependency.

```xml
<PackageReference Include="Valheim.Cli.Testing" Version="[0.1.0-preview.4]" />
```

No public NuGet release exists yet: build the pinned package from this repository's source. The ValheimTesting repository records the exact source revision it uses and provides a bootstrap script.

Synthetic terrain, test fixtures, game assertions and session ownership live in [ValheimTesting](https://github.com/tvongaza/ValheimTesting). ValheimCLI does not depend on that library.

For a build that ships core and command packs, generate its static capability manifest from the exact built DLLs:

```csharp
CliBuildManifest.Write("fork commit or release", new[] {
    coreDll, standardDll, worldToolsDll, observeDll
}, outputJson);
```

`CliBuildManifest` reads plugin GUIDs and literal extension registrations from metadata and IL without loading Valheim. It writes each file's SHA-256 and the schema-1 command list. An unknown registration shape fails instead of claiming a capability. Consumers can pin that manifest and still check the live `cli_extensions` reply after launch; a static manifest is not evidence that a plugin actually loaded.
