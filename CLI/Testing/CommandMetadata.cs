namespace valheim_cli.Testing;

public static class CommandMetadata
{
    public static string GetGroup(CommandInfo command)
    {
        string name = command.Name.ToLowerInvariant();
        if (name.StartsWith("cli_"))
        {
            return "cli";
        }

        if (name.StartsWith("ct_") || name.Contains("screenshot") || name.Contains("camera"))
        {
            return "camera";
        }

        if (name.Contains("creative"))
        {
            return "creative";
        }

        return command.IsCheat ? "cheat" : "valheim";
    }

    public static string GetPrecondition(CommandInfo command)
    {
        string name = command.Name.ToLowerInvariant();
        if (name is "cli_create_character" or "cli_select_character" or "cli_connect_direct" or "cli_create_world")
        {
            return "main-menu";
        }

        if (name is "cli_connection_status")
        {
            return "plugin-server";
        }

        if (name == "cli_world_dump")
        {
            return "loaded-world";
        }

        if (name.StartsWith("ct_"))
        {
            return "terminal";
        }

        if (name.Contains("player") || name.Contains("nearest") || name.Contains("spawn") || name.Contains("walk") || name.Contains("goto"))
        {
            return "local-player";
        }

        return "terminal";
    }

    public static object ToJson(CommandInfo command)
    {
        return new
        {
            command.Name,
            command.Description,
            command.IsCheat,
            Group = GetGroup(command),
            Precondition = GetPrecondition(command),
            Usage = ExtractUsage(command)
        };
    }

    public static string ExtractUsage(CommandInfo command)
    {
        int index = command.Description.IndexOf(':');
        return index >= 0 ? command.Description[(index + 1)..].Trim() : command.Name;
    }
}
