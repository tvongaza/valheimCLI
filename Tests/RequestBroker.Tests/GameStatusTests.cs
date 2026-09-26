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

    // --- --remote: the game is on another machine ---------------------------------

    [Fact]
    public void ARemoteGameIsNotStoodInForByThisMachinesGameOrLog()
    {
        // Someone plays Valheim here (valheimCLI on 5555) while the CLI waits on a
        // server through a tunnel on 5558: this machine's game is not that server.
        string game = Path.Combine(Path.GetTempPath(), "vcli-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(game, "BepInEx"));
        File.WriteAllText(Path.Combine(game, "BepInEx", "LogOutput.log"), "[Info   :valheimCLI] valheimCLI loaded. CLI server on port 5555\n");
        try
        {
            GameLauncher local = new GameLauncher(game, port: 5558);
            Assert.True(local.GetPluginLogInfo().PluginLoaded);

            GameLauncher remote = new GameLauncher(game, port: 5558, remote: true);
            Assert.False(remote.GetPluginLogInfo().Exists);
            Assert.False(remote.GetPluginLogInfo().PluginLoaded);
            Assert.False(remote.IsGameRunning());
        }
        finally
        {
            Directory.Delete(game, recursive: true);
        }
    }

    [Fact]
    public void ARemoteGameThatDoesNotAnswerIsNotAWrongPortNorStopped()
    {
        GameStatus status = new GameStatus
        {
            Remote = true,
            IsRunning = false,
            IsConnected = false,
            Port = 5558,
            PluginLog = new PluginLogInfo { Exists = true, PluginLoaded = true, Port = 5555 }
        };

        Assert.Equal("remote_not_answering", status.DiagnosticCode);
        Assert.Contains("cannot see a remote game", status.DiagnosticMessage);
    }

    [Fact]
    public void ARemoteGameIsNeitherLaunchedNorStoppedFromHere()
    {
        GameLauncher remote = new GameLauncher(Path.GetTempPath(), port: 5558, remote: true);
        Assert.False(remote.LaunchGame());
        remote.StopGame(); // returns without looking for local processes to kill
    }
}
