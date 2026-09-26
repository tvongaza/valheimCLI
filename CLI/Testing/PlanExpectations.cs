using valheimCLI;

namespace valheim_cli.Testing;

/// <summary>An expectations file to check, and where the request for it came from.</summary>
public sealed class ExpectationSource
{
    public string Path { get; init; } = "";
    public bool Strict { get; init; }

    /// <summary>--expect, --expect-strict, or plan (the plan's game.expect key).</summary>
    public string From { get; init; } = "";
}

public enum ExpectationOutcome
{
    Held,

    /// <summary>The game does not match the file, or did not confirm that it does.</summary>
    Mismatch,

    /// <summary>The file is missing, malformed or empty; the game was not asked.</summary>
    BadFile
}

/// <summary>The result of checking a game against an expectations file, as a run records it.</summary>
public sealed class ExpectationCheck
{
    public string File { get; set; } = "";
    public bool Strict { get; set; }
    public string From { get; set; } = "";
    public ExpectationOutcome Outcome { get; set; }
    public string Message { get; set; } = "";

    /// <summary>The game's reply to cli_expect: OK, or the MISMATCH lines and the error.</summary>
    public List<string> Output { get; set; } = new();

    public bool Held => Outcome == ExpectationOutcome.Held;

    /// <summary>One line for a run's summary.</summary>
    public string Summary()
    {
        string mode = Strict ? "strict" : "not strict";
        return Outcome switch
        {
            ExpectationOutcome.Held => $"held ({File}, {mode}, from {From})",
            ExpectationOutcome.Mismatch => $"MISMATCH ({File}, {mode}, from {From}); no step ran",
            _ => $"UNREADABLE ({File}): {Message}; no step ran"
        };
    }
}

/// <summary>
/// Expectations for a test plan run. They are checked once, when the game first
/// answers and before the first step, so a run against the wrong mod builds or the
/// wrong world stops before it measures anything.
/// </summary>
public static class PlanExpectations
{
    /// <summary>
    /// The expectations a run checks, or null for none. The command line wins over
    /// the plan's game.expect and game.expectStrict keys, as --launch and --stop-after
    /// win over theirs: a caller can check a plan against another file, or in another
    /// mode, without editing it. A command-line path is relative to the working
    /// directory, as for single commands; the plan's is relative to the plan file, so
    /// a plan and its pins file move together. expectStrict without expect means nothing.
    /// </summary>
    public static ExpectationSource? Resolve(string? cliFile, bool cliStrict, string? planFile, bool planStrict, string planPath)
    {
        if (!string.IsNullOrWhiteSpace(cliFile))
        {
            return new ExpectationSource { Path = cliFile, Strict = cliStrict, From = cliStrict ? "--expect-strict" : "--expect" };
        }

        if (string.IsNullOrWhiteSpace(planFile))
        {
            return null;
        }

        string planDirectory = System.IO.Path.GetDirectoryName(planPath) ?? "";
        string path = System.IO.Path.IsPathRooted(planFile) ? planFile : System.IO.Path.Combine(planDirectory, planFile);
        return new ExpectationSource { Path = path, Strict = planStrict, From = "plan" };
    }

    /// <summary>
    /// Reads an expectations file into the cli_expect command that checks it. Parsing
    /// is the mod's own (Source/Expectations.cs), so a file the CLI accepts means the
    /// same in the game.
    /// </summary>
    public static bool TryLoad(string path, bool strict, out string command, out string error)
    {
        command = "";
        error = "";
        if (!File.Exists(path))
        {
            error = $"Expectations file not found: {path}";
            return false;
        }

        List<string> errors = new();
        List<Expectation> expectations = Expectations.ParseLines(File.ReadAllLines(path), errors);
        if (errors.Count > 0)
        {
            error = $"{path}: {string.Join("; ", errors)}";
            return false;
        }

        if (expectations.Count == 0)
        {
            error = $"{path} has no expectations";
            return false;
        }

        command = Expectations.ExpectCommand(expectations, strict);
        return true;
    }

    public static ExpectationCheck Unreadable(ExpectationSource source, string error)
    {
        return new ExpectationCheck
        {
            File = source.Path,
            Strict = source.Strict,
            From = source.From,
            Outcome = ExpectationOutcome.BadFile,
            Message = error
        };
    }

    /// <summary>
    /// Judges the game's reply to cli_expect. The expectations hold only when the game
    /// says so with an OK: EXPECT line and reports no mismatch or error. Anything else
    /// fails: an empty reply, or an older valheimCLI that does not know cli_expect,
    /// has not shown that the game matches.
    /// </summary>
    public static ExpectationCheck Judge(ExpectationSource source, IReadOnlyList<string> output)
    {
        bool confirmed = output.Any(line => line.StartsWith("OK: EXPECT", StringComparison.Ordinal));
        bool refuted = output.Any(line =>
            line.StartsWith("MISMATCH ", StringComparison.Ordinal) ||
            line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase));
        bool held = confirmed && !refuted;
        return new ExpectationCheck
        {
            File = source.Path,
            Strict = source.Strict,
            From = source.From,
            Outcome = held ? ExpectationOutcome.Held : ExpectationOutcome.Mismatch,
            Message = held
                ? $"{source.Path} holds."
                : output.Count == 0
                    ? $"The game did not answer cli_expect, so it is not known to match {source.Path}."
                    : $"The game does not match {source.Path}.",
            Output = output.ToList()
        };
    }

    /// <summary>
    /// The exit code of a test run. A game that does not match is reported as such
    /// (6) even when steps of another plan also failed, because it makes every step
    /// result suspect; a file that cannot be read is bad input (4); then failed steps (1).
    /// </summary>
    public static CliExitCode ExitCode(IEnumerable<ExpectationCheck?> checks, int failedSteps)
    {
        List<ExpectationCheck> made = checks.OfType<ExpectationCheck>().ToList();
        if (made.Any(check => check.Outcome == ExpectationOutcome.Mismatch))
        {
            return CliExitCode.ExpectationMismatch;
        }

        if (made.Any(check => check.Outcome == ExpectationOutcome.BadFile))
        {
            return CliExitCode.BadInput;
        }

        return failedSteps > 0 ? CliExitCode.CommandFailure : CliExitCode.Success;
    }
}
