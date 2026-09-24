using System.Text.Json;
using System.Text.Json.Serialization;

namespace valheim_cli.Testing;

public enum CliExitCode
{
    Success = 0,
    CommandFailure = 1,
    Timeout = 2,
    ConnectionFailure = 3,
    BadInput = 4,
    GameNotReady = 5
}

public sealed class CliResponse
{
    public bool Ok { get; set; }
    public string Command { get; set; } = "";
    public string State { get; set; } = "";
    public string Message { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public Dictionary<string, object?> Details { get; set; } = new();
}

public sealed class CommandResult
{
    public string Command { get; set; } = "";
    public bool Ok { get; set; }
    public string ErrorCode { get; set; } = "";
    public string Message { get; set; } = "";
    public List<string> Output { get; set; } = new();

    public static CommandResult FromOutput(string command, List<string> output)
    {
        string message = output.Count > 0 ? output[0] : "";
        string errorCode = DetectErrorCode(output);
        return new CommandResult
        {
            Command = command,
            Ok = string.IsNullOrEmpty(errorCode),
            ErrorCode = errorCode,
            Message = message,
            Output = output
        };
    }

    private static string DetectErrorCode(List<string> output)
    {
        // A command that ends its reply with its own OK: line has said that it
        // succeeded, and the words below may then be data: a string cli_call
        // returned, a location or item name. Only its explicit ERROR lines count.
        bool reportedOk = ReportedOk(output);
        foreach (string line in output)
        {
            string lower = line.ToLowerInvariant();
            if (reportedOk && !lower.StartsWith("error:") && !lower.StartsWith("error "))
            {
                continue;
            }

            if (lower.Contains("no local player found"))
            {
                return "player_not_loaded";
            }

            if (lower.Contains("not valid in the current context"))
            {
                return "wrong_game_context";
            }

            if (lower.Contains("usage:"))
            {
                return "bad_input";
            }

            if (lower.Contains("not a recognized command"))
            {
                return "unknown_command";
            }

            if (lower.StartsWith("error:") ||
                lower.StartsWith("error ") ||
                lower.Contains("error executing command"))
            {
                return "command_failed";
            }

            if (lower.Contains("timed out"))
            {
                return "command_timeout";
            }
        }

        return "";
    }

    private static bool ReportedOk(List<string> output)
    {
        for (int i = output.Count - 1; i >= 0; i--)
        {
            string line = output[i].Trim();
            if (line.Length > 0)
            {
                return line.StartsWith("OK:", StringComparison.Ordinal);
            }
        }

        return false;
    }
}

public sealed class LaunchPhase
{
    public string Name { get; set; } = "";
    public bool Ok { get; set; }
    public bool Required { get; set; } = true;
    public string Message { get; set; } = "";
}

public sealed class PluginLogInfo
{
    public string Path { get; set; } = "";
    public bool Exists { get; set; }
    public bool PluginLoaded { get; set; }
    public int? Port { get; set; }
}

public static class JsonOutput
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Write(object value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, Options));
    }
}

public enum WaitTarget
{
    Process,
    PluginServer,
    Terminal,
    MainMenu,
    InWorld,
    LocalPlayer,
    ServerConnected
}

public static class WaitTargets
{
    public static bool TryParse(string raw, out WaitTarget target)
    {
        string normalized = raw.Trim().Replace("-", "").Replace("_", "").ToLowerInvariant();
        target = normalized switch
        {
            "process" or "game" => WaitTarget.Process,
            "plugin" or "pluginserver" or "server" => WaitTarget.PluginServer,
            "terminal" or "cli" => WaitTarget.Terminal,
            "mainmenu" or "menu" => WaitTarget.MainMenu,
            "inworld" or "world" => WaitTarget.InWorld,
            "localplayer" or "player" => WaitTarget.LocalPlayer,
            "serverconnected" or "connected" or "connection" => WaitTarget.ServerConnected,
            _ => WaitTarget.Process
        };
        return normalized is "process" or "game" or "plugin" or "pluginserver" or "server" or "terminal" or "cli" or "mainmenu" or "menu" or "inworld" or "world" or "localplayer" or "player" or "serverconnected" or "connected" or "connection";
    }

    public static string ToName(WaitTarget target)
    {
        return target switch
        {
            WaitTarget.Process => "process",
            WaitTarget.PluginServer => "plugin-server",
            WaitTarget.Terminal => "terminal",
            WaitTarget.MainMenu => "main-menu",
            WaitTarget.InWorld => "in-world",
            WaitTarget.LocalPlayer => "local-player",
            WaitTarget.ServerConnected => "server-connected",
            _ => "process"
        };
    }
}
