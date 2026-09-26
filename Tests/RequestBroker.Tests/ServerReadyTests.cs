using valheim_cli.Testing;
using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// A dedicated server has a world but never a local player, so in-world never came
/// and a wait for it ran into its stall window. server-ready is reached when the world's
/// locations exist and the server listens for players: after generating them for a new
/// world, and straight after loading for an existing one, which writes no generation line.
/// </summary>
public class ServerReadyTests
{
    private static readonly DateTime T0 = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A dedicated server reached through a tunnel: no local process, the plugin answers.</summary>
    private static GameStatus Server(string state, string phase, bool locationsGenerated, bool listening, float progress = 0f, bool shuttingDown = false)
    {
        return new GameStatus
        {
            IsRunning = true,
            IsConnected = true,
            Dedicated = state != "MainMenu",
            State = state,
            LoadPhase = phase,
            LocationsGenerated = locationsGenerated,
            LocationProgress = progress,
            Listening = listening,
            ShuttingDown = shuttingDown,
            ConnectionStatus = "Connected"
        };
    }

    /// <summary>Observes statusAt(t) every 2 s; returns the outcome, the time and the error code.</summary>
    private static (WaitOutcome Outcome, double At, string ErrorCode, string Reason) Observe(WaitTarget target, Func<double, GameStatus> statusAt, double until)
    {
        WaitPolicy policy = new WaitPolicy { Timeout = TimeSpan.FromSeconds(until), Stall = TimeSpan.FromSeconds(120), Progress = TimeSpan.Zero };
        WaitTracker tracker = new WaitTracker(target, policy, T0);
        for (double t = 0; t <= until; t += 2)
        {
            WaitStep step = tracker.Observe(statusAt(t), T0.AddSeconds(t));
            if (step.Outcome != WaitOutcome.Waiting)
            {
                return (step.Outcome, t, step.ErrorCode, step.Reason);
            }
        }

        return (WaitOutcome.Waiting, until, "", "");
    }

    /// <summary>A new world: boot, then a long location generation, then the server opens.</summary>
    private static GameStatus NewWorld(double t)
    {
        if (t < 4)
        {
            return Server("MainMenu", "main_menu", false, false);
        }

        if (t < 300)
        {
            return Server("InWorldNoPlayer", "generating_locations", false, false, (float)(t / 300));
        }

        if (t < 304)
        {
            return Server("InWorldNoPlayer", "opening_server", true, false);
        }

        return Server("InWorldNoPlayer", "server_ready", true, true);
    }

    /// <summary>An existing world: boot, load (the main thread is busy; the status holds), then open at once.</summary>
    private static GameStatus ExistingWorld(double t)
    {
        if (t < 4)
        {
            return Server("MainMenu", "main_menu", false, false);
        }

        if (t < 40)
        {
            return Server("InWorldNoPlayer", "opening_server", true, false);
        }

        return Server("InWorldNoPlayer", "server_ready", true, true);
    }

    [Fact]
    public void ANewWorldIsReadyWhenItsServerOpensAfterGenerating()
    {
        (WaitOutcome outcome, double at, _, _) = Observe(WaitTarget.ServerReady, NewWorld, 1800);

        Assert.Equal(WaitOutcome.Reached, outcome);
        Assert.Equal(304, at);
    }

    [Fact]
    public void AnExistingWorldIsReadyWithoutEverGenerating()
    {
        (WaitOutcome outcome, double at, _, _) = Observe(WaitTarget.ServerReady, ExistingWorld, 1800);

        Assert.Equal(WaitOutcome.Reached, outcome);
        Assert.Equal(40, at);
    }

    [Theory]
    [InlineData(WaitTarget.InWorld)]
    [InlineData(WaitTarget.LocalPlayer)]
    [InlineData(WaitTarget.MainMenu)]
    [InlineData(WaitTarget.ServerConnected)]
    public void AClientTargetOnADedicatedServerEndsAtOnceAndNamesServerReady(WaitTarget target)
    {
        // Before, in-world on a server that loaded an existing world stalled after 120 s.
        (WaitOutcome outcome, double at, string errorCode, string reason) = Observe(target, t => ExistingWorld(t + 60), 1800);

        Assert.Equal(WaitOutcome.Unreachable, outcome);
        Assert.Equal(0, at);
        Assert.Equal("unreachable", errorCode);
        Assert.Contains("server-ready", reason);
    }

    [Fact]
    public void TheBootMenuOfADedicatedServerDoesNotEndAServerReadyWait()
    {
        (WaitOutcome outcome, _, _, _) = Observe(WaitTarget.ServerReady, NewWorld, 3);

        Assert.Equal(WaitOutcome.Waiting, outcome);
    }

    [Fact]
    public void ListeningAloneIsNotReady()
    {
        Assert.False(Server("InWorldNoPlayer", "generating_locations", false, true).ServerReady);
        Assert.False(Server("InWorldNoPlayer", "server_ready", true, true, shuttingDown: true).ServerReady);
        Assert.False(Server("InWorldNoPlayer", "opening_server", true, false).ServerReady);
        Assert.True(Server("InWorldNoPlayer", "server_ready", true, true).ServerReady);
    }

    [Fact]
    public void AHostThatOpenedItsWorldIsServerReadyToo()
    {
        GameStatus host = new GameStatus
        {
            IsRunning = true,
            IsConnected = true,
            State = "InWorld",
            LoadPhase = "ready",
            LocationsGenerated = true,
            Listening = true
        };

        Assert.True(host.Satisfies(WaitTarget.ServerReady));
        Assert.True(host.Satisfies(WaitTarget.InWorld));
    }

    [Fact]
    public void OpeningTheServerIsProgress()
    {
        WaitSnapshot before = WaitSnapshot.From(Server("InWorldNoPlayer", "opening_server", true, false));
        WaitSnapshot after = WaitSnapshot.From(Server("InWorldNoPlayer", "opening_server", true, true));

        Assert.Contains("listening false -> true", after.ChangesFrom(before));
    }

    [Theory]
    [InlineData("server-ready")]
    [InlineData("ServerReady")]
    [InlineData("server_ready")]
    [InlineData("dedicated")]
    public void TheTargetParses(string raw)
    {
        Assert.True(WaitTargets.TryParse(raw, out WaitTarget target));
        Assert.Equal(WaitTarget.ServerReady, target);
        Assert.Equal("server-ready", WaitTargets.ToName(target));
    }

    // --- A plugin build that does not report a field the target needs ------------------

    /// <summary>
    /// A dedicated server's status line from a plugin build before listening= (and dedicated=,
    /// passwordPrompt=) were added: it respawns nobody and never says it listens.
    /// </summary>
    private const string LineWithoutListening =
        "state=InWorldNoPlayer phase=respawning game=true shuttingDown=false mainMenu=false localPlayer=false " +
        "znet=true znetConnecting=false znetScene=true zoneSystem=true locationsGenerated=true locationProgress=1.000 " +
        "estimatedLocationSeconds=0.0 locationCount=6012 activeAreaLoaded=false respawnWait=0.0";

    /// <summary>The status the client builds from a plugin's STATUS line (fromStatusLine) or from its state-only fallback.</summary>
    private static GameStatus FromLine(string line, bool fromStatusLine = true)
    {
        GameStatus status = new GameStatus { IsRunning = true, IsConnected = true, Remote = true };
        Dictionary<string, string> details = ValheimClient.ParseKeyValueStatus(line);
        status.State = details.TryGetValue("state", out string? state) ? state : "Unknown";
        GameLauncher.ApplyStatusDetails(status, details, fromStatusLine);
        return status;
    }

    [Fact]
    public void AStatusLineWithoutListeningEndsAServerReadyWaitAtOnceNamingThePlugin()
    {
        // 25 Sep 2026: this line made a wait for server-ready run into its stall window ("nothing the
        // game reports changed for 121s"), which sent the reader to the game instead of the plugin.
        var run = Observe(WaitTarget.ServerReady, _ => FromLine(LineWithoutListening), until: 300);

        Assert.Equal(WaitOutcome.Unsupported, run.Outcome);
        Assert.Equal(0, run.At);
        Assert.Equal("plugin_lacks_field", run.ErrorCode);
        Assert.Contains("no listening field", run.Reason);
        Assert.Contains("server-ready", run.Reason);
        Assert.Contains("update the plugin", run.Reason);
        Assert.DoesNotContain("shuttingDown field", run.Reason);
        Assert.Equal(CliExitCode.CommandFailure, WaitTracker.ExitCode(run.Outcome));
    }

    [Fact]
    public void EveryMissingFieldIsNamed()
    {
        // A plugin build before shuttingDown= as well.
        string line = LineWithoutListening.Replace(" shuttingDown=false", "");
        var run = Observe(WaitTarget.ServerReady, _ => FromLine(line), until: 10);

        Assert.Equal("plugin_lacks_field", run.ErrorCode);
        Assert.Contains("no listening, shuttingDown field", run.Reason);
    }

    [Fact]
    public void ListeningFalseStillWaitsAsBefore()
    {
        string line = LineWithoutListening + " dedicated=true listening=false passwordPrompt=false";
        GameStatus status = FromLine(line);
        Assert.Empty(WaitStatusFields.Missing(WaitTarget.ServerReady, status));

        // Within the stall window the wait goes on to its timeout.
        var early = Observe(WaitTarget.ServerReady, _ => FromLine(line), until: 60);
        Assert.Equal(WaitOutcome.TimedOut, early.Outcome);
        Assert.Equal(60, early.At);

        // Unchanged for the stall window: the stall it always was, not the new code.
        var run = Observe(WaitTarget.ServerReady, _ => FromLine(line), until: 300);
        Assert.Equal(WaitOutcome.Stalled, run.Outcome);
        Assert.Equal("stalled", run.ErrorCode);
    }

    [Fact]
    public void FieldsPresentAndTrueReachServerReady()
    {
        string line = LineWithoutListening.Replace("phase=respawning", "phase=server_ready") + " dedicated=true listening=true passwordPrompt=false";
        var run = Observe(WaitTarget.ServerReady, _ => FromLine(line), until: 10);

        Assert.Equal(WaitOutcome.Reached, run.Outcome);
        Assert.Equal(0, run.At);
    }

    [Fact]
    public void AStateOnlyFallbackIsNotTakenForAnOldPlugin()
    {
        // No STATUS line in time (a busy game): the client falls back to the state alone, which
        // says nothing about the fields, so the wait goes on as before.
        GameStatus status = FromLine("state=InWorldNoPlayer phase=unknown", fromStatusLine: false);

        Assert.Empty(WaitStatusFields.Missing(WaitTarget.ServerReady, status));
        var run = Observe(WaitTarget.ServerReady, _ => status, until: 60);
        Assert.Equal(WaitOutcome.TimedOut, run.Outcome);
        Assert.Equal(60, run.At);
    }

    [Theory]
    [InlineData(WaitTarget.Process)]
    [InlineData(WaitTarget.PluginServer)]
    [InlineData(WaitTarget.Terminal)]
    [InlineData(WaitTarget.MainMenu)]
    [InlineData(WaitTarget.InWorld)]
    [InlineData(WaitTarget.LocalPlayer)]
    [InlineData(WaitTarget.ServerConnected)]
    public void OtherTargetsNeedNoFieldAnOldPluginLacks(WaitTarget target)
    {
        // A client's targets are judged by state and connection status, which every plugin build reports.
        Assert.Empty(WaitStatusFields.Required(target));
        Assert.Empty(WaitStatusFields.Missing(target, FromLine(LineWithoutListening.Replace(" shuttingDown=false", ""))));
    }
}
