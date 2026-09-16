namespace valheim_cli.Testing;

public record CommandInfo
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool IsCheat { get; init; }
}
