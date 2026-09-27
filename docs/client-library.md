# Valheim.Cli.Testing

The transport and YAML test-plan runner shared by the valheim-cli executable and external tools. Targets .NET 9; no game assemblies. YamlDotNet 16.2.1 is an external MIT-licensed dependency.

```xml
<PackageReference Include="Valheim.Cli.Testing" Version="[0.1.0-preview.4]" />
```

Until upstream merges the extension/transport work, build the pinned package from the tvongaza/valheimCLI fork. The ValheimTesting repository records its exact source revision and provides a bootstrap script. No public NuGet release is claimed.

Synthetic terrain, test fixtures, game assertions and session ownership live in [ValheimTesting](https://github.com/tvongaza/ValheimTesting). CLI does not depend on that library.
