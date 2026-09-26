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
    GameNotReady = 5,
    ExpectationMismatch = 6,

    /// <summary>
    /// The game exited, or its plugin stopped answering, during a wait. 7, not 6: an
    /// expectation mismatch (a game that is not the one a test expects) is being given 6.
    /// </summary>
    GameLost = 7
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

    /// <summary>
    /// The process exit code for this result: a connection that closed before
    /// the answer, or a server that unloaded with the command open (the server
    /// stopped, or a live reload replaced valheimCLI), is a connection failure,
    /// so a script can reconnect rather than give up.
    /// </summary>
    public int ExitCode => Ok ? (int)CliExitCode.Success
        : ConnectionLoss.IsConnectionLoss(ErrorCode) ? (int)CliExitCode.ConnectionFailure
        : (int)CliExitCode.CommandFailure;

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

            if (lower.StartsWith("error: code=" + ConnectionLoss.ErrorCode))
            {
                return ConnectionLoss.ErrorCode;
            }

            if (lower.StartsWith("error: code=" + ConnectionLoss.UnloadedCode + " "))
            {
                return ConnectionLoss.UnloadedCode;
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

            // A client's console says "'x' is not a recognized command"; a dedicated server's says
            // "Unknown command 'x'. Type 'help' ...". Both are a command the game does not have.
            if (lower.Contains("not a recognized command") || lower.StartsWith("unknown command '"))
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

    /// <summary>Log size in bytes when it was read; a growing log is a starting game's only sign of progress.</summary>
    public long Length { get; set; }
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

    private static readonly JsonSerializerOptions EventOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Write(object value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, Options));
    }

    /// <summary>
    /// One event as a single line on stderr, so stdout keeps exactly one JSON document
    /// (the final result) while a long operation still reports as it goes.
    /// </summary>
    public static void WriteEvent(object value)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(value, EventOptions));
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
    ServerConnected,

    /// <summary>A server's world is up for players: locations exist and it listens (a dedicated server, or a host that opened its world).</summary>
    ServerReady
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
            "serverready" or "dedicated" or "dedicatedserver" => WaitTarget.ServerReady,
            _ => WaitTarget.Process
        };
        return normalized is "process" or "game" or "plugin" or "pluginserver" or "server" or "terminal" or "cli" or "mainmenu" or "menu" or "inworld" or "world" or "localplayer" or "player" or "serverconnected" or "connected" or "connection" or "serverready" or "dedicated" or "dedicatedserver";
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
            WaitTarget.ServerReady => "server-ready",
            _ => "process"
        };
    }
}
