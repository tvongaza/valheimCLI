using System.Text.RegularExpressions;

namespace valheim_cli.Testing;

/// <summary>What became of a command, as far as resending it is concerned.</summary>
public enum CommandFate
{
    /// <summary>The command ran and answered (with success or with its own error).</summary>
    Answered,

    /// <summary>It expired in the game's queue behind a busy main thread: it never ran, so it is safe to send again.</summary>
    NotStarted,

    /// <summary>It started and missed its timeout: it may still act, so it is never resent.</summary>
    TimedOutAfterStart,

    /// <summary>No answer reached the client: it may have run, so it is never resent.</summary>
    NoAnswer
}

/// <summary>
/// --retry-unstarted: resend a command only when the game said it never ran. The server
/// expires a request that waited out its timeout in the queue (the main thread was busy
/// with a save, a world generation or a long command) with "it had not started and will
/// not run"; every other timeout may have acted, and resending it could act twice.
/// </summary>
public static class CommandRetry
{
    public const string NotStartedText = "it had not started and will not run";

    private static readonly Regex RequestId = new Regex(@"Command #(?<id>\d+) did not complete in time", RegexOptions.Compiled);

    public static CommandFate Classify(IReadOnlyList<string> output)
    {
        foreach (string line in output)
        {
            if (line.StartsWith("ERROR: code=client_timeout", StringComparison.Ordinal))
            {
                return CommandFate.NoAnswer;
            }

            if (line.StartsWith("ERROR: code=command_timeout", StringComparison.Ordinal))
            {
                return line.Contains(NotStartedText, StringComparison.Ordinal)
                    ? CommandFate.NotStarted
                    : CommandFate.TimedOutAfterStart;
            }
        }

        return CommandFate.Answered;
    }

    /// <summary>
    /// Sends once, then again for as long as the answer says the command never ran, up to
    /// `retries` more times. onRetry gets one heartbeat line per resend.
    /// </summary>
    public static List<string> Send(Func<List<string>> send, int retries, Action<string>? onRetry)
    {
        List<string> output = send();
        for (int attempt = 1; attempt <= retries && Classify(output) == CommandFate.NotStarted; attempt++)
        {
            onRetry?.Invoke(RetryLine(output, attempt, retries));
            output = send();
        }

        return output;
    }

    public static string RetryLine(IReadOnlyList<string> output, int attempt, int retries)
    {
        string id = "";
        foreach (string line in output)
        {
            Match match = RequestId.Match(line);
            if (match.Success)
            {
                id = $" #{match.Groups["id"].Value}";
                break;
            }
        }

        return $"RETRY: {attempt}/{retries} command{id} had not started (the game's main thread was busy) and never ran; sending it again";
    }
}
