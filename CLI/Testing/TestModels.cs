using YamlDotNet.Serialization;

namespace valheim_cli.Testing;

public class TestPlan
{
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
}

public class GameSettings
{
    [YamlMember(Alias = "launch")]
    public bool Launch { get; set; } = false;

    [YamlMember(Alias = "launchTimeout")]
    public string LaunchTimeout { get; set; } = "120s";

    [YamlMember(Alias = "stopAfter")]
    public bool StopAfter { get; set; } = false;

    public TimeSpan GetLaunchTimeoutSpan()
    {
        return TestSettings.ParseDuration(LaunchTimeout);
    }
}

public class TestSettings
{
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
    Error
}

public class TestCaseResult
{
    public string Name { get; set; } = "";
    public TestResult Result { get; set; }
    public string Message { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public List<string> Output { get; set; } = new();
}

public class TestPlanResult
{
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string ArtifactDirectory { get; set; } = "";
    public List<TestCaseResult> TestResults { get; set; } = new();

    public int Passed => TestResults.Count(r => r.Result == TestResult.Passed);
    public int Failed => TestResults.Count(r => r.Result == TestResult.Failed);
    public int Skipped => TestResults.Count(r => r.Result == TestResult.Skipped);
    public int Errors => TestResults.Count(r => r.Result == TestResult.Error);
    public TimeSpan TotalDuration => EndTime - StartTime;
}
