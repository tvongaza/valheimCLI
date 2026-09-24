using valheim_cli.Testing;
using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// A game is running when its process is on this machine or when its plugin
/// answers on the port: a game reached through a tunnel, or a dedicated server,
/// has no local process named valheim but is running all the same.
/// </summary>
public class GameStatusTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void RunningFromEitherALocalProcessOrAnAnsweringPlugin(bool local, bool answered, bool running)
    {
        Assert.Equal(running, GameStatus.RunningFrom(local, answered));
    }

    [Fact]
    public void ARemoteGameThatAnswersIsReadyAndNotReportedAsStopped()
    {
        GameStatus status = new GameStatus
        {
            IsRunning = GameStatus.RunningFrom(processSeenLocally: false, pluginAnswered: true),
            ProcessSeenLocally = false,
            IsConnected = true,
            State = "InWorld",
            ConnectionStatus = "Connected",
        };

        Assert.True(status.ProcessReady);
        Assert.True(status.PluginServerReady);
        Assert.True(status.Satisfies(WaitTarget.Process));
        Assert.True(status.Satisfies(WaitTarget.InWorld));
        Assert.NotEqual("game_not_running", status.DiagnosticCode);
    }

    [Fact]
    public void NoProcessAndNoAnswerIsNotRunning()
    {
        GameStatus status = new GameStatus
        {
            IsRunning = GameStatus.RunningFrom(processSeenLocally: false, pluginAnswered: false),
        };

        Assert.False(status.ProcessReady);
        Assert.False(status.Satisfies(WaitTarget.Process));
        Assert.Equal("game_not_running", status.DiagnosticCode);
    }
}
