using valheim_cli.Testing;
using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// A wait reports progress, ends early when nothing changes for the stall window,
/// and ends at once (after a short grace) when the game sits in a state that cannot
/// reach the target without an action. A timeout alone waited out every one of these.
/// </summary>
public class WaitProgressTests
{
    private static readonly DateTime T0 = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Run
    {
        public WaitOutcome Outcome { get; init; }
        public double At { get; init; }
        public string Reason { get; init; } = "";
        public List<WaitHeartbeat> Heartbeats { get; init; } = new();
    }

    /// <summary>Observes statusAt(t) every `every` seconds until the wait ends or `until` passes.</summary>
    private static Run Observe(WaitTarget target, WaitPolicy policy, Func<double, GameStatus> statusAt, double until, double every = 2)
    {
        WaitTracker tracker = new WaitTracker(target, policy, T0);
        for (double t = 0; t <= until; t += every)
        {
            WaitStep step = tracker.Observe(statusAt(t), T0.AddSeconds(t));
            if (step.Outcome != WaitOutcome.Waiting)
            {
                return new Run { Outcome = step.Outcome, At = t, Reason = step.Reason, Heartbeats = tracker.Heartbeats };
            }
        }

        return new Run { Outcome = WaitOutcome.Waiting, At = until, Heartbeats = tracker.Heartbeats };
    }

    private static WaitPolicy Policy(double timeout, double stall = 120, double progress = 15, bool allowUnreachable = false)
    {
        return new WaitPolicy
        {
            Timeout = TimeSpan.FromSeconds(timeout),
            Stall = TimeSpan.FromSeconds(stall),
            Progress = TimeSpan.FromSeconds(progress),
            AllowUnreachable = allowUnreachable
        };
    }

    private static GameStatus Status(string state, string phase, string connection = "Connected", bool shuttingDown = false)
    {
        return new GameStatus
        {
            IsRunning = true,
            IsConnected = true,
            State = state,
            LoadPhase = phase,
            ConnectionStatus = connection,
            ShuttingDown = shuttingDown
        };
    }

    private static GameStatus InWorld(string connection = "Connected") => Status("InWorld", "ready", connection);
    private static GameStatus MainMenu() => Status("MainMenu", "main_menu", "");

    // --- unreachable -------------------------------------------------------------

    [Fact]
    public void InWorldWhileWaitingForMainMenuIsUnreachableWithinTheGrace()
    {
        // wait --for main-menu --timeout 180s, issued while the game sits in a world.
        Run run = Observe(WaitTarget.MainMenu, Policy(180), _ => InWorld(), until: 180);

        Assert.Equal(WaitOutcome.Unreachable, run.Outcome);
        Assert.InRange(run.At, WaitPolicy.UnreachableGrace.TotalSeconds, WaitPolicy.UnreachableGrace.TotalSeconds + 2);
        Assert.Contains("logout", run.Reason);
    }

    [Fact]
    public void APlanStepWaitingForMainMenuInAWorldIsUnreachableToo()
    {
        // waitFor: {state: MainMenu, timeout: 300s}
        Assert.True(WaitTargets.TryParse("MainMenu", out WaitTarget target));
        Assert.True(WaitPolicy.TryForPlanStep(TimeSpan.FromSeconds(300), "", false, null, false, WaitPolicy.DefaultProgress, out WaitPolicy policy, out string error), error);

        Run run = Observe(target, policy, _ => InWorld(), until: 300);

        Assert.Equal(WaitOutcome.Unreachable, run.Outcome);
        Assert.True(run.At < 20, $"unreachable only at {run.At}s");
    }

    [Fact]
    public void ALogoutReportedByThePluginKeepsTheWaitThroughALongSave()
    {
        // The logout starts at 10 s; the save holds the world (and the player) until 40 s.
        Run run = Observe(WaitTarget.MainMenu, Policy(180), t =>
            t < 10 ? InWorld()
            : t < 40 ? Status("InWorld", "ready", "Connected", shuttingDown: true)
            : t < 46 ? Status("Unknown", "unknown", "")
            : MainMenu(), until: 180);

        Assert.Equal(WaitOutcome.Reached, run.Outcome);
        Assert.Equal(46, run.At);
    }

    [Fact]
    public void ALogoutThatMovesTheStateWithinTheGraceReachesTheMainMenu()
    {
        // A plugin that does not report shuttingDown: the state itself moves in time.
        Run run = Observe(WaitTarget.MainMenu, Policy(180), t =>
            t < 12 ? InWorld()
            : t < 20 ? Status("InWorldNoPlayer", "respawning", "")
            : t < 26 ? Status("Unknown", "unknown", "")
            : MainMenu(), until: 180);

        Assert.Equal(WaitOutcome.Reached, run.Outcome);
    }

    [Theory]
    [InlineData("ErrorDisconnected")]
    [InlineData("Disconnected")]
    public void AWorldLosingItsConnectionIsOnItsWayToTheMenu(string connection)
    {
        Run run = Observe(WaitTarget.MainMenu, Policy(180), _ => InWorld(connection), until: 100);

        Assert.Equal(WaitOutcome.Waiting, run.Outcome);
    }

    [Fact]
    public void AllowUnreachableWaitsForSomeoneElseToLogOut()
    {
        Run run = Observe(WaitTarget.MainMenu, Policy(180, stall: 0, allowUnreachable: true), _ => InWorld(), until: 200);

        Assert.Equal(WaitOutcome.TimedOut, run.Outcome);
        Assert.Equal(180, run.At);
    }

    [Fact]
    public void MainMenuWhileWaitingForAWorldIsNeverUnreachable()
    {
        // A join may be queued; only the stall window ends this wait.
        Run run = Observe(WaitTarget.InWorld, Policy(300, stall: 120), _ => MainMenu(), until: 300);

        Assert.Equal(WaitOutcome.Stalled, run.Outcome);
        Assert.Equal(120, run.At);
    }

    [Theory]
    [InlineData("Loading", "connecting_screen")]
    [InlineData("InWorldNoPlayer", "loading_active_area")]
    public void ATransitionalStateIsNeverUnreachable(string state, string phase)
    {
        Run run = Observe(WaitTarget.MainMenu, Policy(180, stall: 0), _ => Status(state, phase), until: 100);

        Assert.Equal(WaitOutcome.Waiting, run.Outcome);
    }

    [Fact]
    public void TheGameStoppingDuringTheWaitIsUnreachable()
    {
        Run run = Observe(WaitTarget.InWorld, Policy(300), t =>
            t < 10 ? Status("InWorldNoPlayer", "loading_active_area") : new GameStatus { IsRunning = false }, until: 300);

        Assert.Equal(WaitOutcome.Unreachable, run.Outcome);
        Assert.InRange(run.At, 10 + WaitPolicy.UnreachableGrace.TotalSeconds, 10 + WaitPolicy.UnreachableGrace.TotalSeconds + 2);
        Assert.Contains("stopped running", run.Reason);
    }

    [Fact]
    public void AStoppedGameCanStillBeWaitedForAsAProcess()
    {
        // `wait --for process` is how a script waits for a relaunch.
        GameStatus stopped = new GameStatus { IsRunning = false };

        Assert.Equal("", WaitReachability.Why(WaitTarget.Process, stopped, seenRunning: true));
        Assert.NotEqual("", WaitReachability.Why(WaitTarget.Terminal, stopped, seenRunning: true));
        Assert.Equal("", WaitReachability.Why(WaitTarget.Terminal, stopped, seenRunning: false));
    }

    [Fact]
    public void AServerRejectionEndsTheWaitAtOnceEvenWhenUnreachableIsAllowed()
    {
        Run run = Observe(WaitTarget.ServerConnected, Policy(180, allowUnreachable: true), _ => Status("Loading", "connecting_screen", "ErrorPassword"), until: 180);

        Assert.Equal(WaitOutcome.Unreachable, run.Outcome);
        Assert.Equal(0, run.At);
        Assert.Contains("ErrorPassword", run.Reason);
    }

    [Fact]
    public void TimeoutOnlyPolicyNeverStallsNorGivesUp()
    {
        Run run = Observe(WaitTarget.MainMenu, WaitPolicy.TimeoutOnly(TimeSpan.FromSeconds(180), TimeSpan.Zero), _ => InWorld(), until: 200);

        Assert.Equal(WaitOutcome.TimedOut, run.Outcome);
        Assert.Empty(run.Heartbeats);
    }

    // --- stall -------------------------------------------------------------------

    [Fact]
    public void ALoadThatChangesPhaseEvery20SecondsNeverStallsAndReportsEachChange()
    {
        string[] phases = { "connecting_screen", "generating_locations", "loading_active_area", "respawning" };
        Run run = Observe(WaitTarget.InWorld, Policy(600, stall: 60), t =>
            t >= 300 ? InWorld() : Status("InWorldNoPlayer", phases[(int)(t / 20) % phases.Length]), until: 600);

        Assert.Equal(WaitOutcome.Reached, run.Outcome);
        Assert.Equal(300, run.At);
        Assert.True(run.Heartbeats.Count >= 18, $"{run.Heartbeats.Count} heartbeats");
        Assert.Contains(run.Heartbeats, beat => beat.Changes.Any(change => change.StartsWith("phase ")));
    }

    [Fact]
    public void AStatusFrozenPastTheStallWindowStalls()
    {
        Run run = Observe(WaitTarget.InWorld, Policy(600, stall: 60), _ => Status("InWorldNoPlayer", "loading_active_area"), until: 600);

        Assert.Equal(WaitOutcome.Stalled, run.Outcome);
        Assert.Equal(60, run.At);
        Assert.Contains("60s", run.Reason);
    }

    [Fact]
    public void TheStallWindowCountsFromTheLastChange()
    {
        Run run = Observe(WaitTarget.InWorld, Policy(600, stall: 60), t =>
            t < 50 ? Status("Loading", "connecting_screen") : Status("InWorldNoPlayer", "loading_active_area"), until: 600);

        Assert.Equal(WaitOutcome.Stalled, run.Outcome);
        Assert.Equal(110, run.At);
    }

    [Fact]
    public void TimersAreNotProgress()
    {
        Run run = Observe(WaitTarget.InWorld, Policy(600, stall: 60), t =>
        {
            GameStatus status = Status("InWorldNoPlayer", "respawning");
            status.RespawnWait = (float)t;
            status.EstimatedLocationSeconds = (float)(600 - t);
            return status;
        }, until: 600);

        Assert.Equal(WaitOutcome.Stalled, run.Outcome);
        Assert.Equal(60, run.At);
    }

    [Fact]
    public void SlowLocationProgressIsProgress()
    {
        Run run = Observe(WaitTarget.InWorld, Policy(900, stall: 60), t =>
        {
            GameStatus status = Status("InWorldNoPlayer", "generating_locations");
            status.LocationProgress = (float)Math.Floor(t / 40) / 100f;
            return t >= 800 ? InWorld() : status;
        }, until: 900);

        Assert.Equal(WaitOutcome.Reached, run.Outcome);
    }

    [Fact]
    public void AGameNotYetStartedDoesNotStall()
    {
        Run run = Observe(WaitTarget.Terminal, Policy(300, stall: 60), _ => new GameStatus { IsRunning = false }, until: 300);

        Assert.Equal(WaitOutcome.TimedOut, run.Outcome);
        Assert.Equal(300, run.At);
    }

    [Fact]
    public void AGrowingLogIsProgressWhileTheGameStarts()
    {
        Run run = Observe(WaitTarget.Terminal, Policy(600, stall: 60), t =>
        {
            GameStatus status = new GameStatus { IsRunning = true, IsConnected = false };
            status.PluginLog.Exists = true;
            status.PluginLog.Length = t < 200 ? 1000 + (long)(t / 30) * 500 : 5000;
            return status;
        }, until: 600);

        Assert.Equal(WaitOutcome.Stalled, run.Outcome);
        Assert.InRange(run.At, 250, 262);
    }

    [Fact]
    public void AGrowingLogIsNotProgressInAWorld()
    {
        Run run = Observe(WaitTarget.MainMenu, Policy(600, stall: 60, allowUnreachable: true), t =>
        {
            GameStatus status = InWorld();
            status.PluginLog.Exists = true;
            status.PluginLog.Length = 1000 + (long)t * 100;
            return status;
        }, until: 600);

        Assert.Equal(WaitOutcome.Stalled, run.Outcome);
        Assert.Equal(60, run.At);
    }

    [Fact]
    public void AStallWindowAsLongAsTheTimeoutIsATimeout()
    {
        Run run = Observe(WaitTarget.InWorld, Policy(120, stall: 120), _ => MainMenu(), until: 200);

        Assert.Equal(WaitOutcome.TimedOut, run.Outcome);
        Assert.Equal(120, run.At);
    }

    [Fact]
    public void ZeroStallNeverStalls()
    {
        Run run = Observe(WaitTarget.InWorld, Policy(300, stall: 0), _ => Status("InWorldNoPlayer", "loading_active_area"), until: 300);

        Assert.Equal(WaitOutcome.TimedOut, run.Outcome);
    }

    // --- heartbeat ---------------------------------------------------------------

    [Fact]
    public void AHeartbeatEveryProgressInterval()
    {
        Run run = Observe(WaitTarget.InWorld, Policy(60, stall: 0, progress: 15), _ => MainMenu(), until: 60);

        Assert.Equal(new double[] { 16, 32, 48 }, run.Heartbeats.Select(beat => beat.ElapsedSeconds).ToArray());
        string line = run.Heartbeats[1].ToLine();
        Assert.StartsWith("WAIT: 32s/60s for in-world; state=MainMenu phase=main_menu", line);
        Assert.EndsWith("unchanged for 32s", line);
    }

    [Fact]
    public void AHeartbeatSaysWhenTheGameIsNotUpYet()
    {
        Run run = Observe(WaitTarget.Terminal, Policy(60, progress: 15), t =>
            t < 20 ? new GameStatus { IsRunning = false } : new GameStatus { IsRunning = true }, until: 40);

        Assert.StartsWith("WAIT: 16s/60s for terminal; game=not_running state=Unknown", run.Heartbeats[0].ToLine());
        Assert.StartsWith("WAIT: 32s/60s for terminal; game=starting", run.Heartbeats[1].ToLine());
        Assert.Contains("game not_running -> running", run.Heartbeats[1].ToLine());

        Run answering = Observe(WaitTarget.InWorld, Policy(60, progress: 15), _ => MainMenu(), until: 20);
        Assert.DoesNotContain("game=", answering.Heartbeats[0].ToLine());
    }

    [Fact]
    public void ZeroProgressPrintsNoHeartbeat()
    {
        Run run = Observe(WaitTarget.InWorld, Policy(60, stall: 0, progress: 0), _ => MainMenu(), until: 60);

        Assert.Empty(run.Heartbeats);
    }

    [Fact]
    public void AHeartbeatShowsLocationProgressAndWhatChanged()
    {
        Run run = Observe(WaitTarget.InWorld, Policy(60, stall: 0, progress: 15), t =>
        {
            if (t < 10)
            {
                return Status("Loading", "connecting_screen");
            }

            GameStatus status = Status("InWorldNoPlayer", "generating_locations");
            status.LocationProgress = 0.412f;
            return status;
        }, until: 20);

        WaitHeartbeat beat = Assert.Single(run.Heartbeats);
        Assert.Equal(0.412f, beat.LocationProgress);
        string line = beat.ToLine();
        Assert.Contains("locationProgress=0.412", line);
        Assert.Contains("state Loading -> InWorldNoPlayer", line);
        Assert.Contains("phase connecting_screen -> generating_locations", line);
        Assert.Contains("locationProgress 0.000 -> 0.412", line);
    }

    [Fact]
    public void TheHeartbeatBeforeAnOutcomeIsNotPrinted()
    {
        // The result line says it all; a heartbeat in the same observation would repeat it.
        Run run = Observe(WaitTarget.MainMenu, Policy(180, progress: 15), _ => InWorld(), until: 180);

        Assert.Equal(WaitOutcome.Unreachable, run.Outcome);
        Assert.Empty(run.Heartbeats);
    }

    // --- stages, codes, parsing ----------------------------------------------------

    [Fact]
    public void StagesFollowTheLaunchOrder()
    {
        Assert.Equal(WaitStage.NotRunning, WaitReachability.StageOf(new GameStatus { IsRunning = false }));
        Assert.Equal(WaitStage.Process, WaitReachability.StageOf(new GameStatus { IsRunning = true }));
        Assert.Equal(WaitStage.Terminal, WaitReachability.StageOf(Status("Unknown", "unknown")));
        Assert.Equal(WaitStage.MainMenu, WaitReachability.StageOf(MainMenu()));
        Assert.Equal(WaitStage.EnteringWorld, WaitReachability.StageOf(Status("Loading", "loading")));
        Assert.Equal(WaitStage.EnteringWorld, WaitReachability.StageOf(Status("InWorldNoPlayer", "respawning")));
        Assert.Equal(WaitStage.InWorld, WaitReachability.StageOf(InWorld()));
    }

    [Theory]
    [InlineData(WaitOutcome.Reached, CliExitCode.Success, "")]
    [InlineData(WaitOutcome.TimedOut, CliExitCode.Timeout, "timeout")]
    [InlineData(WaitOutcome.Stalled, CliExitCode.Timeout, "stalled")]
    [InlineData(WaitOutcome.Unreachable, CliExitCode.GameNotReady, "unreachable")]
    public void EachOutcomeHasItsCodes(WaitOutcome outcome, CliExitCode exitCode, string errorCode)
    {
        Assert.Equal(exitCode, WaitTracker.ExitCode(outcome));
        Assert.Equal(errorCode, WaitTracker.ErrorCode(outcome));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("0s", 0)]
    [InlineData("15s", 15000)]
    [InlineData("90", 90000)]
    [InlineData("2m", 120000)]
    [InlineData("500ms", 500)]
    [InlineData(" 3M ", 180000)]
    public void DurationsParse(string raw, double milliseconds)
    {
        Assert.True(WaitDurations.TryParse(raw, out TimeSpan duration));
        Assert.Equal(milliseconds, duration.TotalMilliseconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5s")]
    [InlineData("1.5m")]
    [InlineData("10h")]
    public void BadDurationsAreRejected(string raw)
    {
        Assert.False(WaitDurations.TryParse(raw, out TimeSpan _));
    }

    [Fact]
    public void APlanStepStallWinsOverTheCommandLine()
    {
        Assert.True(WaitPolicy.TryForPlanStep(TimeSpan.FromSeconds(300), "0", false, TimeSpan.FromSeconds(30), false, TimeSpan.FromSeconds(15), out WaitPolicy policy, out string _));
        Assert.Equal(TimeSpan.Zero, policy.Stall);
        Assert.Equal(TimeSpan.FromSeconds(300), policy.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(15), policy.Progress);
    }

    [Fact]
    public void TheCommandLineStallWinsOverTheDefault()
    {
        Assert.True(WaitPolicy.TryForPlanStep(TimeSpan.FromSeconds(300), "", false, TimeSpan.FromSeconds(30), true, TimeSpan.FromSeconds(15), out WaitPolicy policy, out string _));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.Stall);
        Assert.True(policy.AllowUnreachable);

        Assert.True(WaitPolicy.TryForPlanStep(TimeSpan.FromSeconds(300), "", true, null, false, TimeSpan.FromSeconds(15), out WaitPolicy defaults, out string _));
        Assert.Equal(WaitPolicy.DefaultStall, defaults.Stall);
        Assert.True(defaults.AllowUnreachable);
    }

    [Fact]
    public void ABadPlanStepStallIsAnError()
    {
        Assert.False(WaitPolicy.TryForPlanStep(TimeSpan.FromSeconds(300), "soon", false, null, false, TimeSpan.FromSeconds(15), out WaitPolicy _, out string error));
        Assert.Contains("soon", error);
    }
}
