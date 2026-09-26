using System.Diagnostics;
using valheim_cli.Testing;
using Xunit;

namespace valheimCLI.Tests;
public class OwnedLauncherTests
{
    [Fact] public void AttachedProcessCannotBeStoppedByOwnedCleanup()
    {
        var launcher = new GameLauncher(remote: true);
        Assert.False(launcher.TryStopOwnedGame(out var message)); Assert.Contains("untouched", message);
    }
    [Fact] public void UnlaunchedLocalProcessCannotBeStoppedByOwnedCleanup()
    {
        Assert.False(new GameLauncher(Path.GetTempPath()).TryStopOwnedGame(out var message)); Assert.Contains("untouched", message);
    }
    [TestRunnerFoundationTests.UnixFact] public void StopTargetsOnlyTheLaunchedProcessTree()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cli-owned-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        using var unrelated = Process.Start("/bin/sleep", "30")!;
        var launcher = new GameLauncher(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "run_bepinex.sh"), "exec /bin/sleep 30\n");
            Assert.True(launcher.LaunchGame()); Assert.True(launcher.TryStopOwnedGame(out var message), message);
            Assert.False(unrelated.HasExited); Assert.False(launcher.TryStopOwnedGame(out _));
        }
        finally { if (!unrelated.HasExited) unrelated.Kill(); unrelated.WaitForExit(); Directory.Delete(dir, true); }
    }
}
