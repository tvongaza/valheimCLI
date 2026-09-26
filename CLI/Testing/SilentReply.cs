namespace valheim_cli.Testing;

/// <summary>
/// A reply in which the game printed nothing for a command. A valheimCLI plugin answers every command
/// it runs: with the command's output, with an ERROR line, or with "Executed: &lt;command&gt;" when the
/// command ran and printed nothing. A reply with no line at all is never that: a dedicated server
/// prints nothing for a command it does not have, and the client showed such a reply as an empty
/// success. It is reported instead, and for a cli_ command the plugin's own command list (LIST_COMMANDS)
/// says exactly whether the build has the command.
/// </summary>
public static class SilentReply
{
    public const string NoOutputCode = "no_output";
    public const string UnknownCommandCode = "unknown_command";

    /// <summary>The plugin's reply when a command it ran printed nothing.</summary>
    public const string ExecutedPrefix = "Executed: ";

    /// <summary>cli_ commands the plugin handles itself without registering them as console commands.</summary>
    private static readonly HashSet<string> Unlisted = new(StringComparer.OrdinalIgnoreCase) { "cli_run_trusted" };

    public static string CommandName(string command)
    {
        string trimmed = command.Trim();
        int space = trimmed.IndexOfAny(new[] { ' ', '\t' });
        return space < 0 ? trimmed : trimmed[..space];
    }

    public static bool IsOwnCommand(string name) => name.StartsWith("cli_", StringComparison.OrdinalIgnoreCase);

    /// <summary>The game printed no line at all (blank lines are nothing printed).</summary>
    public static bool IsEmpty(IReadOnlyList<string> output) => output.All(string.IsNullOrWhiteSpace);

    /// <summary>The reply is only the plugin's confirmation that this command ran and printed nothing.</summary>
    public static bool IsBareConfirmation(string command, IReadOnlyList<string> output)
    {
        List<string> lines = output.Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        return lines.Count == 1 &&
               lines[0].StartsWith(ExecutedPrefix, StringComparison.Ordinal) &&
               CommandName(lines[0][ExecutedPrefix.Length..]).Equals(CommandName(command), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The result for a reply in which the game printed nothing, or null when the reply stands as it is.
    /// listCommands is asked only for a cli_ command whose reply is empty or the bare confirmation: the
    /// command names the plugin has registered, or null when it did not list them. plugin says where the
    /// plugin answers (host:port).
    /// </summary>
    public static CommandResult? Judge(string command, List<string> output, Func<IReadOnlyCollection<string>?> listCommands, string plugin)
    {
        bool empty = IsEmpty(output);
        if (!empty && !IsBareConfirmation(command, output))
        {
            return null;
        }

        string name = CommandName(command);
        IReadOnlyCollection<string>? registered = IsOwnCommand(name) && !Unlisted.Contains(name) ? listCommands() : null;
        if (registered != null && !registered.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            int own = registered.Count(IsOwnCommand);
            return Failed(command, output, UnknownCommandCode,
                $"the valheimCLI plugin at {plugin} has no command '{name}' (its build registers {own} cli_ commands): " +
                "the plugin build is older than this client or does not have the command; update the plugin");
        }

        if (!empty)
        {
            return null;
        }

        string why = !IsOwnCommand(name) || Unlisted.Contains(name)
            ? "this plugin build may not have the command"
            : registered != null
                ? "the plugin lists the command, which ended without a reply line"
                : "the plugin did not list its commands afterwards (the connection may have closed); this plugin build may not have the command";
        return Failed(command, output, NoOutputCode, $"the game printed nothing for '{name}': {why} (valheimCLI plugin at {plugin})");
    }

    private static CommandResult Failed(string command, List<string> output, string code, string message)
    {
        string line = $"ERROR: code={code} message={message}";
        return new CommandResult
        {
            Command = command,
            Ok = false,
            ErrorCode = code,
            Message = line,
            Output = output.Where(existing => !string.IsNullOrWhiteSpace(existing)).Append(line).ToList()
        };
    }
}
