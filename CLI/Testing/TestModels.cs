using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace valheim_cli.Testing;

public class TestPlan
{
    [YamlMember(Alias = "schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    [YamlMember(Alias = "game")]
    public GameSettings Game { get; set; } = new();

    [YamlMember(Alias = "settings")]
    public TestSettings Settings { get; set; } = new();

    [YamlMember(Alias = "variables")]
    public Dictionary<string, string> Variables { get; set; } = new();

    [YamlMember(Alias = "tests")]
    public List<TestCase> Tests { get; set; } = new();

    [YamlMember(Alias = "cleanup")]
    public List<string> Cleanup { get; set; } = new();

    public static TestPlan Parse(string yaml)
    {
        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();

        TestPlan plan = deserializer.Deserialize<TestPlan>(yaml)
            ?? throw new FormatException("The test plan is empty.");
        plan.Validate();
        return plan;
    }

    private void Validate()
    {
        if (SchemaVersion != 1 && SchemaVersion != 2)
            throw new FormatException("Supported test plan schemaVersion values are 1 and 2.");
        if (Game == null || Settings == null || Variables == null || Tests == null || Cleanup == null)
            throw new FormatException("game, settings, variables, tests and cleanup cannot be null.");
        TestSettings.ValidateDuration(Game.LaunchTimeout, "game.launchTimeout");
        TestSettings.ValidateDuration(Settings.Timeout, "settings.timeout");
        if (Variables.Any(v => string.IsNullOrWhiteSpace(v.Key) || v.Value == null))
            throw new FormatException("Variables need a name and a non-null value.");
        ValidateCommands(Cleanup, "cleanup");
        foreach (TestCase test in Tests)
        {
            if (test == null || test.Commands == null)
                throw new FormatException("Tests and their command lists cannot be null.");
            ValidateCommands(test.Commands, $"test '{test.Name}'");
            if (test.Repeat < 1) throw new FormatException("repeat must be at least 1.");
            if (!string.IsNullOrEmpty(test.Wait))
                TestSettings.ValidateDuration(test.Wait, "wait", allowZero: true);
            if (test.WaitFor != null)
            {
                if (!string.IsNullOrEmpty(test.WaitFor.Event))
                    throw new FormatException("waitFor.event is not implemented; use an observed state or a condition command.");
                if (string.IsNullOrWhiteSpace(test.WaitFor.State))
                    throw new FormatException("waitFor requires a state.");
                TestSettings.ValidateDuration(test.WaitFor.Timeout, "waitFor.timeout");
                if (!string.IsNullOrEmpty(test.WaitFor.Stall))
                    TestSettings.ValidateDuration(test.WaitFor.Stall, "waitFor.stall", allowZero: true);
            }
            if (test.Expect != null)
            {
                if (test.Commands.Count == 0)
                    throw new FormatException("expect requires a command whose result can be checked.");
                if (string.IsNullOrWhiteSpace(test.Expect.Output) && string.IsNullOrWhiteSpace(test.Expect.ErrorCode))
                    throw new FormatException("expect requires output or errorCode.");
                if (test.Expect.Scope != null && test.Expect.Scope != "lastCommand" && test.Expect.Scope != "allCommands")
                    throw new FormatException("expect.scope must be lastCommand or allCommands.");
                if (SchemaVersion == 1 && (test.Commands.Count > 1 || test.Repeat > 1) && test.Expect.Scope == null)
                    throw new FormatException("Multi-command/repeated expectations must choose expect.scope or set schemaVersion: 2 (last command, each repetition).");
                if (!string.IsNullOrWhiteSpace(test.Expect.Output) && string.IsNullOrWhiteSpace(test.Expect.GetPattern()))
                    throw new FormatException("expect.output requires a nonempty pattern.");
            }
        }
    }

    private static void ValidateCommands(List<string> commands, string context)
    {
        if (commands.Any(string.IsNullOrWhiteSpace))
            throw new FormatException($"{context} contains an empty command.");
    }
}

public class GameSettings
{
    [YamlMember(Alias = "launch")]
    public bool Launch { get; set; } = false;

    [YamlMember(Alias = "launchTimeout")]
    public string LaunchTimeout { get; set; } = "120s";

    [YamlMember(Alias = "stopAfter")]
    public bool StopAfter { get; set; } = false;

    /// <summary>
    /// An expectations file (docs/expectations.md) the game must match before the
    /// first step, relative to the plan file. --expect and --expect-strict override it.
    /// </summary>
    [YamlMember(Alias = "expect")]
    public string Expect { get; set; } = "";

    /// <summary>Check <see cref="Expect"/> in strict mode.</summary>
    [YamlMember(Alias = "expectStrict")]
    public bool ExpectStrict { get; set; } = false;

    public TimeSpan GetLaunchTimeoutSpan()
    {
        return TestSettings.ParseDuration(LaunchTimeout);
    }
}

public class TestSettings
{
    /// <summary>Plan preflight is strict; the older CLI flag parser keeps its existing fallback behavior.</summary>
    internal static void ValidateDuration(string? duration, string field, bool allowZero = false)
    {
        string value = duration?.Trim().ToLowerInvariant() ?? "";
        string number = value.EndsWith("ms") ? value[..^2]
            : value.EndsWith("s") || value.EndsWith("m") ? value[..^1] : value;
        if (!int.TryParse(number, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int amount) ||
            amount < (allowZero ? 0 : 1) || ParseDuration(value).TotalMilliseconds > int.MaxValue)
            throw new FormatException($"{field} must be a {(allowZero ? "nonnegative" : "positive")} duration in ms, s or m (maximum {int.MaxValue} ms).");
    }
    [YamlMember(Alias = "timeout")]
    public string Timeout { get; set; } = "30s";

    [YamlMember(Alias = "stopOnFailure")]
    public bool StopOnFailure { get; set; } = true;

    [YamlMember(Alias = "logLevel")]
    public string LogLevel { get; set; } = "normal";

    public TimeSpan GetTimeoutSpan()
    {
        return ParseDuration(Timeout);
    }

    public static TimeSpan ParseDuration(string duration)
    {
        if (string.IsNullOrEmpty(duration))
            return TimeSpan.FromSeconds(30);

        duration = duration.Trim().ToLowerInvariant();

        if (duration.EndsWith("ms"))
        {
            if (int.TryParse(duration[..^2], out int ms))
                return TimeSpan.FromMilliseconds(ms);
        }
        else if (duration.EndsWith("s"))
        {
            if (int.TryParse(duration[..^1], out int s))
                return TimeSpan.FromSeconds(s);
        }
        else if (duration.EndsWith("m"))
        {
            if (int.TryParse(duration[..^1], out int m))
                return TimeSpan.FromMinutes(m);
        }
        else if (int.TryParse(duration, out int defaultSeconds))
        {
            return TimeSpan.FromSeconds(defaultSeconds);
        }

        return TimeSpan.FromSeconds(30);
    }
}

public class TestCase
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "description")]
    public string Description { get; set; } = "";

    [YamlMember(Alias = "waitFor")]
    public WaitCondition? WaitFor { get; set; }

    [YamlMember(Alias = "commands")]
    public List<string> Commands { get; set; } = new();

    [YamlMember(Alias = "expect")]
    public ExpectCondition? Expect { get; set; }

    [YamlMember(Alias = "wait")]
    public string Wait { get; set; } = "";

    [YamlMember(Alias = "repeat")]
    public int Repeat { get; set; } = 1;

    [YamlMember(Alias = "skip")]
    public bool Skip { get; set; } = false;

    public TimeSpan GetWaitDuration()
    {
        if (string.IsNullOrEmpty(Wait))
            return TimeSpan.Zero;
        return TestSettings.ParseDuration(Wait);
    }
}

public class WaitCondition
{
    [YamlMember(Alias = "state")]
    public string State { get; set; } = "";

    [YamlMember(Alias = "timeout")]
    public string Timeout { get; set; } = "60s";

    [YamlMember(Alias = "message")]
    public string Message { get; set; } = "";

    [YamlMember(Alias = "event")]
    public string Event { get; set; } = "";

    /// <summary>End the wait when nothing the game reports changes for this long ("0" never); empty uses --stall or the default.</summary>
    [YamlMember(Alias = "stall")]
    public string Stall { get; set; } = "";

    /// <summary>Keep waiting in a state that needs an action, for a step where someone takes it.</summary>
    [YamlMember(Alias = "allowUnreachable")]
    public bool AllowUnreachable { get; set; } = false;

    public TimeSpan GetTimeoutSpan()
    {
        return TestSettings.ParseDuration(Timeout);
    }
}

public class ExpectCondition
{
    /// <summary>By default only the final command in each repetition can satisfy the assertion.</summary>
    [YamlMember(Alias = "scope")]
    public string? Scope { get; set; }

    /// <summary>Explicit expected CLI result code, or local_exit_N for a local command.</summary>
    [YamlMember(Alias = "errorCode")]
    public string ErrorCode { get; set; } = "";

    [YamlMember(Alias = "output")]
    public string Output { get; set; } = "";

    public bool IsContains => Output.StartsWith("contains ", StringComparison.OrdinalIgnoreCase);
    public bool IsMatches => Output.StartsWith("matches ", StringComparison.OrdinalIgnoreCase);

    public string GetPattern()
    {
        if (IsContains)
            return Output.Substring(9).Trim().Trim('"');
        if (IsMatches)
            return Output.Substring(8).Trim().Trim('"');
        return Output;
    }
}

public enum TestResult
{
    Passed,
    Failed,
    Skipped,
    Error,
    Cancelled
}

public class TestCaseResult
{
    public string Name { get; set; } = "";
    public TestResult Result { get; set; }
    public string Message { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public List<string> Output { get; set; } = new();
    public List<CommandResult> Commands { get; set; } = new();
}

public class TestPlanResult
{
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string ArtifactDirectory { get; set; } = "";
    public List<TestCaseResult> TestResults { get; set; } = new();
    public List<TestCaseResult> CleanupResults { get; set; } = new();

    /// <summary>The expectations check made before the first step; null when none was asked for.</summary>
    public ExpectationCheck? Expectations { get; set; }

    public int Passed => TestResults.Count(r => r.Result == TestResult.Passed);
    public int Failed => TestResults.Count(r => r.Result == TestResult.Failed);
    public int Skipped => TestResults.Count(r => r.Result == TestResult.Skipped);
    public int Errors => TestResults.Concat(CleanupResults).Count(r => r.Result == TestResult.Error || r.Result == TestResult.Cancelled);
    public TimeSpan TotalDuration => EndTime - StartTime;
}
