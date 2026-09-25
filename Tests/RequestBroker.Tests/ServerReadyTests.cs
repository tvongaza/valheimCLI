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
}
