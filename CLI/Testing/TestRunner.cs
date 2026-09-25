using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace valheim_cli.Testing;

public class TestRunner
{
    private readonly GameLauncher _launcher;
    private readonly TestRunnerOptions _options;
    private readonly string _host;
    private readonly int _port;
    private ValheimClient? _client;
    private Dictionary<string, string> _variables = new();

    public TestRunner(GameLauncher launcher, TestRunnerOptions? options = null, string host = ConnectionDefaults.Host, int port = ConnectionDefaults.Port)
    {
        _launcher = launcher;
        _options = options ?? new TestRunnerOptions();
        _host = host;
        _port = port;
    }

    /// <summary>
    /// Legacy constructor for backwards compatibility
    /// </summary>
    public TestRunner(ValheimClient client, TestRunnerOptions? options = null)
    {
        _client = client;
        _launcher = new GameLauncher();
        _options = options ?? new TestRunnerOptions();
        _host = ConnectionDefaults.Host;
        _port = ConnectionDefaults.Port;
    }

    public async Task<TestPlanResult> RunTestFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        TestPlanResult planResult = new()
        {
            FilePath = filePath,
            StartTime = DateTime.Now,
            ArtifactDirectory = CreateArtifactDirectory(filePath)
        };

        bool launchedGame = false;

        try
        {
            // Parse the test file
            string yaml = await File.ReadAllTextAsync(filePath, cancellationToken);
            TestPlan plan = ParseTestPlan(yaml);
            planResult.Name = plan.Name;

            // Initialize variables (YAML first, then CLI overrides)
            _variables = new Dictionary<string, string>(plan.Variables);
            foreach (KeyValuePair<string, string> cliVar in _options.Variables)
            {
                _variables[cliVar.Key] = cliVar.Value;
            }

            Log($"Running test plan: {plan.Name}", ConsoleColor.Cyan);
            if (!string.IsNullOrEmpty(plan.Description))
                Log($"  {plan.Description}", ConsoleColor.Gray);
            Log($"  Tests: {plan.Tests.Count}", ConsoleColor.Gray);
            Log("");

            // Determine launch settings (CLI overrides YAML)
            bool shouldLaunch = _options.Launch ?? plan.Game.Launch;
            bool shouldStopAfter = _options.StopAfter ?? plan.Game.StopAfter;
            TimeSpan launchTimeout = _options.LaunchTimeout ?? plan.Game.GetLaunchTimeoutSpan();

            // Handle game launching if configured
            if (_client == null)
            {
                if (shouldLaunch)
                {
                    launchedGame = await HandleGameLaunchAsync(launchTimeout, cancellationToken);
                    if (!launchedGame && !_launcher.TryConnect())
                    {
                        throw new InvalidOperationException("Failed to launch game or connect to server");
                    }
                }

                // Create and connect client
                _client = new ValheimClient(_host, _port);
                if (!_client.Connect())
                {
                    throw new InvalidOperationException("Failed to connect to Valheim. Is the game running with the mod?");
                }
                Log("Connected to Valheim", ConsoleColor.Green);
                Log("");
            }

            // Run each test case
            foreach (TestCase testCase in plan.Tests)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                TestCaseResult result = await RunTestCaseAsync(testCase, plan.Settings, cancellationToken);
                planResult.TestResults.Add(result);

                // Stop on failure if configured
                if (result.Result == TestResult.Failed && plan.Settings.StopOnFailure)
                {
                    Log("Stopping due to test failure (stopOnFailure=true)", ConsoleColor.Yellow);
                    break;
                }
            }

            // Run cleanup
            if (plan.Cleanup.Count > 0)
            {
                Log("\nRunning cleanup...", ConsoleColor.Gray);
                foreach (string command in plan.Cleanup)
                {
                    try
                    {
                        string expandedCommand = ExpandVariables(command);
                        _client!.SendCommand(expandedCommand);
                    }
                    catch (Exception ex)
                    {
                        Log($"  Cleanup error: {ex.Message}", ConsoleColor.Yellow);
                    }
                }
            }

            // Handle stop-after if all tests passed
            if (shouldStopAfter && planResult.Failed == 0 && planResult.Errors == 0)
            {
                Log("\nStopping game (all tests passed)...", ConsoleColor.Gray);
                _client?.Dispose();
                _client = null;
                _launcher.StopGame();
            }
            else if (shouldStopAfter && (planResult.Failed > 0 || planResult.Errors > 0))
            {
                Log("\nKeeping game running for debugging (tests failed)", ConsoleColor.Yellow);
            }
        }
        catch (Exception ex)
        {
            Log($"Error running test file: {ex.Message}", ConsoleColor.Red);
            planResult.TestResults.Add(new TestCaseResult
            {
                Name = "Test Plan Error",
                Result = TestResult.Error,
                Message = ex.Message
            });
        }

        planResult.EndTime = DateTime.Now;
        WriteArtifacts(planResult);
        PrintSummary(planResult);
        return planResult;
    }

    private async Task<bool> HandleGameLaunchAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Check if already connected
        if (_launcher.TryConnect())
        {
            Log("Game already running and connected, skipping launch", ConsoleColor.Yellow);
            return QueueServerConnectIfRequested();
        }

        // Check if game is running but not connected yet
        if (_launcher.IsGameRunning())
        {
            Log("Game is running, waiting for server...", ConsoleColor.Yellow);
            bool ready = await WaitForPluginServerAsync(timeout, cancellationToken);
            if (ready)
            {
                Log("Server ready", ConsoleColor.Green);
                return QueueServerConnectIfRequested();
            }
            Log("Server did not become ready within timeout", ConsoleColor.Red);
            return false;
        }

        // Launch the game
        Log($"Launching Valheim from: {_launcher.GamePath}", ConsoleColor.Cyan);
        if (!_launcher.LaunchGame())
        {
            Log("Failed to launch game", ConsoleColor.Red);
            return false;
        }

        Log($"Waiting for game to be ready (timeout: {timeout.TotalSeconds}s)...", ConsoleColor.Gray);
        bool isReady = await WaitForPluginServerAsync(timeout, cancellationToken);

        if (isReady)
        {
            Log("Game is ready", ConsoleColor.Green);
            return QueueServerConnectIfRequested();
        }

        Log("Game did not become ready within timeout", ConsoleColor.Red);
        return false;
    }

    /// <summary>Waits for the plugin's port to answer, printing a heartbeat while the game starts.</summary>
    private async Task<bool> WaitForPluginServerAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        GameStatus status = await _launcher.WaitForTargetAsync(
            WaitTarget.PluginServer,
            timeout,
            _options.Interval,
            cancellationToken,
            _options.Progress,
            LogHeartbeat);
        return status.PluginServerReady;
    }

    private void LogHeartbeat(WaitHeartbeat beat)
    {
        Log($"    {beat.ToLine()}", ConsoleColor.DarkGray);
    }

    private bool QueueServerConnectIfRequested()
    {
        if (!_launcher.HasServerConnect)
        {
            return true;
        }

        Log("Queueing server join...", ConsoleColor.Cyan);
        if (_launcher.QueueServerConnect(out List<string> output))
        {
            foreach (string line in output)
            {
                Log($"  {line}", ConsoleColor.Gray);
            }
            return true;
        }

        foreach (string line in output)
        {
            Log($"  {line}", ConsoleColor.Red);
        }
        return false;
    }

    private async Task<TestCaseResult> RunTestCaseAsync(TestCase testCase, TestSettings settings, CancellationToken cancellationToken)
    {
        TestCaseResult result = new()
        {
            Name = testCase.Name
        };

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            // Handle skipped tests
            if (testCase.Skip)
            {
                result.Result = TestResult.Skipped;
                result.Message = "Test marked as skipped";
                LogTestResult(result);
                return result;
            }

            Log($"  [{testCase.Name}]", ConsoleColor.White);

            // Handle wait condition
            if (testCase.WaitFor != null)
            {
                string waitFailure = await HandleWaitConditionAsync(testCase.WaitFor, cancellationToken);
                if (waitFailure.Length > 0)
                {
                    result.Result = TestResult.Failed;
                    result.Message = waitFailure;
                    stopwatch.Stop();
                    result.Duration = stopwatch.Elapsed;
                    LogTestResult(result);
                    return result;
                }
            }

            // Execute commands (with repeat support)
            int repeatCount = Math.Max(1, testCase.Repeat);
            for (int i = 0; i < repeatCount; i++)
            {
                foreach (string command in testCase.Commands)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    string expandedCommand = ExpandVariables(command);
                    LogVerbose($"    > {expandedCommand}");

                    List<string> output = IsLocalCommand(expandedCommand)
                        ? await ExecuteLocalCommandAsync(GetLocalCommand(expandedCommand), settings.GetTimeoutSpan(), cancellationToken)
                        : _client!.SendCommand(expandedCommand);
                    result.Output.AddRange(output);

                    foreach (string line in output)
                    {
                        LogVerbose($"      {line}");
                    }
                }

                // Wait between iterations if specified
                if (testCase.GetWaitDuration() > TimeSpan.Zero)
                {
                    await Task.Delay(testCase.GetWaitDuration(), cancellationToken);
                }
            }

            // Check expectations
            if (testCase.Expect != null)
            {
                bool expectationMet = CheckExpectation(testCase.Expect, result.Output);
                if (!expectationMet)
                {
                    result.Result = TestResult.Failed;
                    result.Message = $"Expectation not met: {testCase.Expect.Output}";
                    stopwatch.Stop();
                    result.Duration = stopwatch.Elapsed;
                    LogTestResult(result);
                    return result;
                }
            }

            result.Result = TestResult.Passed;
            result.Message = "OK";
        }
        catch (Exception ex)
        {
            result.Result = TestResult.Error;
            result.Message = ex.Message;
        }

        stopwatch.Stop();
        result.Duration = stopwatch.Elapsed;
        LogTestResult(result);
        return result;
    }

    /// <summary>Returns "" when the wait condition is met, otherwise why it is not.</summary>
    private async Task<string> HandleWaitConditionAsync(WaitCondition condition, CancellationToken cancellationToken)
    {
        // Display message if provided
        if (!string.IsNullOrEmpty(condition.Message))
        {
            Log($"    Waiting: {condition.Message}", ConsoleColor.Yellow);
        }

        // A readiness target (MainMenu, InWorld, ...) is watched through the full status:
        // heartbeats, stall and unreachable detection, as with `valheim-cli wait`.
        if (!string.IsNullOrEmpty(condition.State) && WaitTargets.TryParse(condition.State, out WaitTarget target))
        {
            if (!WaitPolicy.TryForPlanStep(
                    condition.GetTimeoutSpan(),
                    condition.Stall,
                    condition.AllowUnreachable,
                    _options.Stall,
                    _options.AllowUnreachable,
                    _options.Progress,
                    out WaitPolicy policy,
                    out string error))
            {
                return $"waitFor: {error}";
            }

            LogVerbose($"    Waiting for {WaitTargets.ToName(target)} (timeout {WaitDurations.Format(policy.Timeout)}, stall {WaitDurations.Format(policy.Stall)})");
            WaitResult waited = await _launcher.WaitAsync(target, policy, _options.Interval, LogHeartbeat, cancellationToken);
            if (waited.Outcome == WaitOutcome.Reached)
            {
                return "";
            }

            GameStatus status = waited.Status;
            return $"Wait for {condition.State} failed: code={waited.ErrorCode}; " +
                   $"state={status.State} phase={status.LoadPhase} connection={status.ConnectionStatus}; " +
                   (waited.Reason.Length > 0 ? waited.Reason : "not reached");
        }

        // Any other game state name (Loading, InWorldNoPlayer) is matched by name only.
        if (!string.IsNullOrEmpty(condition.State))
        {
            LogVerbose($"    Waiting for state: {condition.State}");
            bool reached = await _client!.WaitForStateAsync(condition.State, condition.GetTimeoutSpan(), cancellationToken);
            return reached ? "" : $"Wait condition not met: state={condition.State}";
        }

        // Handle custom event wait (extensible for future)
        if (!string.IsNullOrEmpty(condition.Event))
        {
            LogVerbose($"    Waiting for event: {condition.Event}");
            // For now, just wait the timeout duration
            // This can be extended to support custom events
            await Task.Delay(condition.GetTimeoutSpan(), cancellationToken);
            return "";
        }

        return "";
    }

    private static bool IsLocalCommand(string command)
    {
        string trimmed = command.TrimStart();
        return trimmed.StartsWith("local:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetLocalCommand(string command)
    {
        string trimmed = command.TrimStart();
        int colonIndex = trimmed.IndexOf(':');
        return colonIndex >= 0 ? trimmed[(colonIndex + 1)..].TrimStart() : trimmed;
    }

    private async Task<List<string>> ExecuteLocalCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        List<string> output = new();
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        ProcessStartInfo startInfo = new()
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/C");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(command);
        }

        using Process process = new() { StartInfo = startInfo };
        process.Start();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            AddProcessOutput(output, stdout);
            AddProcessOutput(output, stderr);
            output.Add($"localExitCode={process.ExitCode}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            output.Add($"ERROR: Local command timed out after {timeout.TotalSeconds:F0}s");
        }

        return output;
    }

    private static void AddProcessOutput(List<string> output, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
                output.Add(line);
        }
    }

    private bool CheckExpectation(ExpectCondition expect, List<string> output)
    {
        string combinedOutput = string.Join("\n", output);
        string expectedOutput = ExpandVariables(expect.Output);

        if (expect.IsContains)
        {
            string pattern = GetExpectationPattern(expectedOutput, "contains");
            return combinedOutput.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        if (expect.IsMatches)
        {
            string pattern = GetExpectationPattern(expectedOutput, "matches");
            try
            {
                return Regex.IsMatch(combinedOutput, pattern, RegexOptions.IgnoreCase);
            }
            catch (RegexParseException)
            {
                Log($"    Invalid regex pattern: {pattern}", ConsoleColor.Red);
                return false;
            }
        }

        // Default: exact match
        return combinedOutput.Contains(expectedOutput, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetExpectationPattern(string output, string prefix)
    {
        return output.Substring(prefix.Length).Trim().Trim('"');
    }

    private string ExpandVariables(string input)
    {
        string result = input;
        foreach (KeyValuePair<string, string> variable in _variables)
        {
            result = result.Replace($"${{{variable.Key}}}", variable.Value);
            result = result.Replace($"${variable.Key}", variable.Value);
        }
        return result;
    }

    private void LogTestResult(TestCaseResult result)
    {
        ConsoleColor color = result.Result switch
        {
            TestResult.Passed => ConsoleColor.Green,
            TestResult.Failed => ConsoleColor.Red,
            TestResult.Skipped => ConsoleColor.Yellow,
            TestResult.Error => ConsoleColor.Magenta,
            _ => ConsoleColor.White
        };

        string status = result.Result switch
        {
            TestResult.Passed => "PASS",
            TestResult.Failed => "FAIL",
            TestResult.Skipped => "SKIP",
            TestResult.Error => "ERROR",
            _ => "????"
        };

        string duration = result.Duration.TotalMilliseconds > 1000
            ? $"{result.Duration.TotalSeconds:F1}s"
            : $"{result.Duration.TotalMilliseconds:F0}ms";

        Log($"    [{status}] {result.Name} ({duration})", color);

        if (result.Result != TestResult.Passed && !string.IsNullOrEmpty(result.Message))
        {
            Log($"           {result.Message}", ConsoleColor.Gray);
        }
    }

    private void PrintSummary(TestPlanResult result)
    {
        Log("");
        Log("═══════════════════════════════════════════════════════", ConsoleColor.Cyan);
        Log($"Test Results: {result.Name}", ConsoleColor.Cyan);
        Log("═══════════════════════════════════════════════════════", ConsoleColor.Cyan);

        ConsoleColor summaryColor = result.Failed > 0 || result.Errors > 0 ? ConsoleColor.Red : ConsoleColor.Green;
        Log($"  Passed:  {result.Passed}", result.Passed > 0 ? ConsoleColor.Green : ConsoleColor.Gray);
        Log($"  Failed:  {result.Failed}", result.Failed > 0 ? ConsoleColor.Red : ConsoleColor.Gray);
        Log($"  Skipped: {result.Skipped}", result.Skipped > 0 ? ConsoleColor.Yellow : ConsoleColor.Gray);
        Log($"  Errors:  {result.Errors}", result.Errors > 0 ? ConsoleColor.Magenta : ConsoleColor.Gray);
        Log($"  Total:   {result.TestResults.Count}", ConsoleColor.White);
        Log($"  Duration: {result.TotalDuration.TotalSeconds:F2}s", ConsoleColor.Gray);
        Log($"  Artifacts: {result.ArtifactDirectory}", ConsoleColor.Gray);
        Log("═══════════════════════════════════════════════════════", ConsoleColor.Cyan);
    }

    private string CreateArtifactDirectory(string filePath)
    {
        string root = !string.IsNullOrWhiteSpace(_options.ArtifactsDirectory)
            ? _options.ArtifactsDirectory
            : Path.Combine("CLI", "runs");
        string planName = Path.GetFileNameWithoutExtension(filePath);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string directory = Path.Combine(root, $"{stamp}-{planName}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteArtifacts(TestPlanResult result)
    {
        Directory.CreateDirectory(result.ArtifactDirectory);
        string transcriptPath = Path.Combine(result.ArtifactDirectory, "transcript.txt");
        List<string> transcript = new();
        foreach (TestCaseResult test in result.TestResults)
        {
            transcript.Add($"[{test.Result}] {test.Name}: {test.Message}");
            transcript.AddRange(test.Output.Select(line => "  " + line));
        }
        File.WriteAllLines(transcriptPath, transcript);

        string summaryPath = Path.Combine(result.ArtifactDirectory, "summary.json");
        JsonSerializerOptions options = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(result, options));
    }

    private void Log(string message, ConsoleColor color = ConsoleColor.White)
    {
        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }

    private void LogVerbose(string message)
    {
        if (_options.Verbose)
        {
            Log(message, ConsoleColor.DarkGray);
        }
    }

    public static TestPlan ParseTestPlan(string yaml)
    {
        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<TestPlan>(yaml);
    }
}

public class TestRunnerOptions
{
    public bool Verbose { get; set; } = false;
    public bool StopOnFirstFailure { get; set; } = false;

    // Game launch options (CLI flags override YAML settings)
    public bool? Launch { get; set; } = null;
    public bool? StopAfter { get; set; } = null;
    public TimeSpan? LaunchTimeout { get; set; } = null;
    public string? ArtifactsDirectory { get; set; } = null;
    public bool Json { get; set; }

    // Waits: poll interval, heartbeat interval, and the defaults a waitFor step can override
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan Progress { get; set; } = WaitPolicy.DefaultProgress;
    public TimeSpan? Stall { get; set; } = null;
    public bool AllowUnreachable { get; set; } = false;

    // CLI variables (override YAML variables)
    public Dictionary<string, string> Variables { get; set; } = new();
}
