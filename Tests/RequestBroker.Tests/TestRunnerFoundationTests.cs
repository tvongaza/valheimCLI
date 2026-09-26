using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using valheim_cli.Testing;
using Xunit;

namespace valheimCLI.Tests;

public class TestRunnerFoundationTests
{
    [Theory]
    [InlineData("name: p\ntests: []\nsettngs: {}")]
    [InlineData("name: p\ntests:\n  - name: t\n    commands: [help]\n    expectt: {output: absent}")]
    [InlineData("name: p\ntests:\n  - name: t\n    commands: [help]\n    expect: {outpt: absent}")]
    public void UnknownFieldsCannotRemoveAnAssertion(string yaml) =>
        Assert.ThrowsAny<Exception>(() => TestPlan.Parse(yaml));

    [Theory]
    [InlineData("potato")]
    [InlineData("-1s")]
    [InlineData("0s")]
    public void InvalidTimeoutIsRefusedBeforeExecution(string duration) =>
        Assert.ThrowsAny<Exception>(() => TestPlan.Parse($"name: p\nsettings: {{timeout: {duration}}}\ntests: []"));

    [Fact]
    public void UnimplementedEventWaitCannotPassBySleeping() =>
        Assert.ThrowsAny<Exception>(() => TestPlan.Parse("name: p\ntests:\n  - name: t\n    waitFor: {event: saved, timeout: 1ms}"));

    [Theory]
    [InlineData("name: p\nsettings: {timeout: 1s, timeout: 2s}\ntests: []")]
    [InlineData("name: p\ntests: null")]
    [InlineData("name: p\ntests: [null]")]
    [InlineData("name: p\ntests:\n  - name: t\n    repeat: 0")]
    [InlineData("name: p\ntests:\n  - name: t\n    commands: [help]\n    expect: {}")]
    [InlineData("name: p\ntests:\n  - name: t\n    commands: [help]\n    expect: {output: 'contains', scope: typo}")]
    public void MalformedPlansAreRefused(string yaml) =>
        Assert.ThrowsAny<Exception>(() => TestPlan.Parse(yaml));

    [Fact]
    public void ZeroIsValidForDelaysAndStallButNotTimeouts()
    {
        TestPlan plan = TestPlan.Parse("name: p\ntests:\n  - name: t\n    wait: 0\n    waitFor: {state: InWorld, stall: 0}\n");
        Assert.Equal(TimeSpan.Zero, plan.Tests[0].GetWaitDuration());
    }

    [Fact]
    public void AmbiguousLegacyAssertionGetsAMigrationError()
    {
        FormatException error = Assert.Throws<FormatException>(() => TestPlan.Parse(
            "name: p\ntests:\n  - name: t\n    commands: [first, second]\n    expect: {output: marker}\n"));
        Assert.Contains("schemaVersion: 2", error.Message);
    }

    [Fact]
    public void TrackedSamplePlansRemainValid()
    {
        string[] paths = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "SamplePlans"), "*.yaml");
        Assert.NotEmpty(paths);
        foreach (string path in paths) Assert.NotEmpty(TestPlan.Parse(File.ReadAllText(path)).Tests);
    }

    [Theory]
    [InlineData("ERROR: code=no_member message=missing")]
    [InlineData("Unknown command 'missing'. Type 'help' for commands.")]
    public async Task FailedCommandWithoutAssertionFailsTheCase(string reply)
    {
        await using var fixture = new RunnerFixture(_ => new[] { reply });
        TestPlanResult result = await fixture.Run(Case("probe"));
        Assert.Equal(1, result.Failed + result.Errors);
        Assert.Contains(reply, result.TestResults[0].Output);
    }

    [Fact]
    public async Task SilentCommandIsNotSuccessful()
    {
        await using var fixture = new RunnerFixture(_ => Array.Empty<string>());
        Assert.Equal(1, (await fixture.Run(Case("probe"))).Errors);
    }

    [Fact]
    public async Task AssertionCannotMatchAnEarlierCommand()
    {
        await using var fixture = new RunnerFixture(c => new[] { c == "first" ? "OK: marker" : "OK: other" });
        TestPlanResult result = await fixture.Run("  - name: t\n    commands: [first, second]\n    expect: {output: 'contains marker'}\n");
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task CombinedOutputRequiresAnExplicitScope()
    {
        await using var fixture = new RunnerFixture(c => new[] { c == "first" ? "OK: marker" : "OK: other" });
        TestPlanResult result = await fixture.Run("  - name: t\n    commands: [first, second]\n    expect: {output: 'contains marker', scope: allCommands}\n");
        Assert.Equal(1, result.Passed);
    }

    [Fact]
    public async Task EachRepetitionMustMeetTheAssertion()
    {
        int attempt = 0;
        await using var fixture = new RunnerFixture(_ => new[] { ++attempt == 1 ? "OK: marker" : "OK: other" });
        TestPlanResult result = await fixture.Run("  - name: t\n    commands: [probe]\n    repeat: 2\n    expect: {output: 'contains marker'}\n");
        Assert.Equal(1, result.Failed);
        Assert.Contains("repetition 2", result.TestResults[0].Message);
    }

    [Fact]
    public async Task ErrorsStopLaterCasesAndRemainVisibleAsNotRun()
    {
        await using var fixture = new RunnerFixture(_ => new[] { "ERROR: code=bad message=broken" });
        TestPlanResult result = await fixture.Run(Case("first") + Case("second"), "cleanup: [restore]\n");
        Assert.Equal(TestResult.Error, result.TestResults[0].Result);
        Assert.Equal(TestResult.Skipped, result.TestResults[1].Result);
        Assert.Equal(new[] { "first", "restore" }, fixture.Commands);
    }

    [Fact]
    public async Task ExplicitContinueStillReportsTheFailure()
    {
        await using var fixture = new RunnerFixture(c => new[] { c == "first" ? "ERROR: broken" : "OK: done" });
        TestPlanResult result = await fixture.Run(Case("first") + Case("second"), "settings: {stopOnFailure: false}\n");
        Assert.Equal(1, result.Errors);
        Assert.Equal(1, result.Passed);
        Assert.Equal(new[] { "first", "second" }, fixture.Commands);
    }

    [Fact]
    public async Task PlanTimeoutReachesTheProtocolAndDoesNotLeakIntoNextPlan()
    {
        await using var fixture = new RunnerFixture(_ => new[] { "OK: done" });
        TimeSpan original = fixture.CommandTimeout;
        await fixture.Run(Case("probe"), "settings: {timeout: 2s}\n");
        Assert.Equal("CMDT:2:probe", Assert.Single(fixture.Requests));
        Assert.Equal(original, fixture.CommandTimeout);
    }

    [Fact]
    public async Task NewResultRecordsDoNotPersistCommandArguments()
    {
        await using var fixture = new RunnerFixture(_ => new[] { "OK: done" });
        TestPlanResult result = await fixture.Run(Case("probe secret-argument"));
        Assert.Equal("probe", result.TestResults[0].Commands[0].Command);
        Assert.DoesNotContain("secret-argument", File.ReadAllText(Path.Combine(result.ArtifactDirectory, "summary.json")));
    }

    [Theory]
    [InlineData("ERROR: code=no_member message=missing", 1, 0)]
    [InlineData("OK: member exists", 0, 1)]
    public async Task NegativeTestsMustExplicitlyNameAnError(string reply, int passed, int failed)
    {
        await using var fixture = new RunnerFixture(_ => new[] { reply });
        TestPlanResult result = await fixture.Run("  - name: t\n    commands: [probe]\n    expect: {errorCode: command_failed, output: 'contains code=no_member'}\n");
        Assert.Equal(passed, result.Passed);
        Assert.Equal(failed, result.Failed);
    }

    [Fact]
    public async Task ExpectedFinalErrorCannotExcuseAnEarlierError()
    {
        await using var fixture = new RunnerFixture(_ => new[] { "ERROR: code=no_member message=missing" });
        TestPlanResult result = await fixture.Run("  - name: t\n    commands: [first, second]\n    expect: {errorCode: command_failed}\n");
        Assert.Equal(1, result.Errors);
        Assert.Equal(new[] { "first" }, fixture.Commands);
    }

    [Fact]
    public async Task WrongEnvironmentRunsNeitherCasesNorCleanup()
    {
        await using var fixture = new RunnerFixture(_ => new[] { "MISMATCH plugin", "ERROR: mismatch" });
        File.WriteAllText(fixture.FilePath("pins.txt"), "com.example.plugin=any\n");
        TestPlanResult result = await fixture.Run(Case("probe"), "game: {expect: pins.txt}\ncleanup: [restore]\n");
        Assert.False(result.Expectations!.Held);
        Assert.Empty(result.TestResults);
        Assert.Empty(result.CleanupResults);
        Assert.StartsWith("cli_expect", Assert.Single(fixture.Commands));
    }

    [Fact]
    public async Task BadPlanDoesNotSendEvenCleanup()
    {
        await using var fixture = new RunnerFixture(_ => new[] { "OK: done" });
        TestPlanResult result = await fixture.Run(Case("probe"), "settings: {timeout: potato}\ncleanup: [restore]\n");
        Assert.Equal(1, result.Errors);
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public async Task FailedCleanupDoesNotSkipTheRemainingCleanup()
    {
        await using var fixture = new RunnerFixture(c => new[] { c == "restore1" ? "ERROR: failed" : "OK: done" });
        TestPlanResult result = await fixture.Run(Case("probe"), "cleanup: [restore1, restore2]\n");
        Assert.Equal(1, result.Passed);
        Assert.Equal(1, result.Errors);
        Assert.Equal(new[] { "probe", "restore1", "restore2" }, fixture.Commands);
        Assert.Equal(2, result.CleanupResults.Count);
    }

    [Fact]
    public async Task FailedCleanupMakesTheRunFailAndKeepsTheOriginalFailure()
    {
        await using var fixture = new RunnerFixture(c => new[] { "ERROR: code=bad message=" + c });
        TestPlanResult result = await fixture.Run(Case("probe"), "cleanup: [restore]\n");
        Assert.True(result.Errors >= 2);
        Assert.Contains("probe", fixture.Commands);
        Assert.Contains("restore", fixture.Commands);
        Assert.NotEqual(CliExitCode.Success, PlanExpectations.ExitCode(new[] { result.Expectations }, result.Failed + result.Errors));
    }

    [Fact]
    public async Task CancellationBetweenCommandsCannotReportSuccess()
    {
        using var cancel = new CancellationTokenSource();
        await using var fixture = new RunnerFixture(c =>
        {
            if (c == "first") cancel.Cancel();
            return new[] { "OK: done" };
        });
        TestPlanResult result = await fixture.Run("  - name: t\n    commands: [first, second]\n", "cleanup: [restore]\n", cancel.Token);
        Assert.True(result.Errors > 0);
        Assert.DoesNotContain("second", fixture.Commands);
        Assert.Contains("restore", fixture.Commands);
    }

    [Fact]
    public async Task LocalNonzeroExitIsNotJustOutput()
    {
        await using var fixture = new RunnerFixture(_ => new[] { "OK: done" });
        TestPlanResult result = await fixture.Run(Case("local: exit 7"));
        Assert.Equal(1, result.Errors);
        Assert.Contains("localExitCode=7", result.TestResults[0].Output);
    }

    [UnixFact]
    public async Task CancellationKillsTheLocalProcessItStarted()
    {
        await using var fixture = new RunnerFixture(_ => new[] { "OK: done" });
        using var cancel = new CancellationTokenSource();
        string pidFile = fixture.FilePath("child.pid");
        Task<TestPlanResult> run = fixture.Run(Case($"local: echo $$ > '{pidFile}'; sleep 30"),
            "cleanup: [restore]\n", cancel.Token);
        int pid = 0;
        try
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out pid)) break;
                await Task.Delay(10);
            }
            Assert.True(pid > 0, "local process did not start");
            cancel.Cancel();
            TestPlanResult result = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(TestResult.Cancelled, result.TestResults[0].Result);
            Assert.Contains("restore", fixture.Commands);
            Assert.False(IsAlive(pid));
        }
        finally
        {
            cancel.Cancel();
            if (pid > 0 && IsAlive(pid))
            {
                using Process process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
            }
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static bool IsAlive(int pid)
    {
        try { using Process process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    [UnixFact]
    public async Task TimeoutKillsTheLocalCommandAndReportsFailure()
    {
        await using var fixture = new RunnerFixture(_ => new[] { "OK: done" });
        TestPlanResult result = await fixture.Run(Case("local: sleep 30"), "settings: {timeout: 100ms}\n");
        Assert.Equal(1, result.Errors);
        Assert.Equal("local_timeout", result.TestResults[0].Commands[0].ErrorCode);
    }

    [Fact]
    public async Task RepeatedRunsNeverOverwriteTheirEvidence()
    {
        await using var fixture = new RunnerFixture(_ => new[] { "OK: done" });
        TestPlanResult first = await fixture.Run(Case("probe"));
        TestPlanResult second = await fixture.Run(Case("probe"));
        Assert.NotEqual(first.ArtifactDirectory, second.ArtifactDirectory);
        Assert.True(File.Exists(Path.Combine(first.ArtifactDirectory, "summary.json")));
    }

    [Fact]
    public async Task ValidPlanStillPassesAndWritesArtifacts()
    {
        await using var fixture = new RunnerFixture(_ => new[] { "OK: marker" });
        TestPlanResult result = await fixture.Run("  - name: t\n    commands: [probe]\n    expect: {output: 'contains marker'}\n");
        Assert.Equal(1, result.Passed);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.ArtifactDirectory, "summary.json")));
        Assert.Equal(1, json.RootElement.GetProperty("Passed").GetInt32());
    }

    private static string Case(string command) => $"  - name: t\n    commands: [{JsonSerializer.Serialize(command)}]\n";

    public sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute()
        {
            if (OperatingSystem.IsWindows()) Skip = "Process fixture uses bash; Windows process cancellation needs its own fixture.";
        }
    }

    /// <summary>Exercises the shipped runner and client over the real protocol, never a game.</summary>
    private sealed class RunnerFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        private readonly ValheimClient _client;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cli-runner-" + Guid.NewGuid().ToString("N"));
        public List<string> Commands { get; } = new();
        public List<string> Requests { get; } = new();
        public TimeSpan CommandTimeout => _client.CommandTimeout;
        public string FilePath(string name) => Path.Combine(_root, name);

        public RunnerFixture(Func<string, string[]> reply)
        {
            Directory.CreateDirectory(_root);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _server = Task.Run(async () =>
            {
                using TcpClient peer = await _listener.AcceptTcpClientAsync(_stop.Token);
                using NetworkStream stream = peer.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                await writer.WriteLineAsync("VALHEIM_CLI_READY");
                await writer.WriteLineAsync("VALHEIM_CLI_CAPS completion");
                while (await reader.ReadLineAsync(_stop.Token) is string line)
                {
                    Requests.Add(line);
                    string command = line.StartsWith("CMDT:") ? line[(line.IndexOf(':', 5) + 1)..] : line[4..];
                    Commands.Add(command);
                    string[] output = reply(command);
                    await writer.WriteLineAsync("OUTPUT:" + output.Length);
                    foreach (string item in output) await writer.WriteLineAsync(item);
                    await writer.WriteLineAsync("END_OUTPUT");
                }
            });
            _client = new ValheimClient("127.0.0.1", port);
            Assert.True(_client.Connect());
        }

        public async Task<TestPlanResult> Run(string cases, string extra = "", CancellationToken cancellation = default)
        {
            string path = Path.Combine(_root, "plan.yaml");
            await File.WriteAllTextAsync(path, "schemaVersion: 2\nname: p\ntests:\n" + cases + extra);
            var runner = new TestRunner(_client, new TestRunnerOptions { ArtifactsDirectory = _root });
            return await runner.RunTestFileAsync(path, cancellation);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            _stop.Cancel();
            _listener.Stop();
            try { await _server.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            finally
            {
                _stop.Dispose();
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
