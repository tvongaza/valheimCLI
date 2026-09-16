using valheimCLI;
using Xunit;

namespace valheimCLI.Tests
{

public class CaptureCommandsTests
{
    private static ClutterSystem Start(params bool[] enabled)
    {
        ClutterSystem system = new ClutterSystem();
        foreach (bool value in enabled)
            system.m_clutter.Add(new ClutterSystem.Clutter { m_enabled = value });
        ClutterSystem.instance = system;
        CaptureCommands.Register();
        return system;
    }

    private static string Run(string value)
    {
        Terminal.ConsoleEventArgs args = new Terminal.ConsoleEventArgs("cli_clutter", value);
        Terminal.LastCommand!.Callback(args);
        return Assert.Single(args.Context.Output);
    }

    [Fact]
    public void OffDisablesTheEntriesOnTheRealCommandPath()
    {
        ClutterSystem system = Start(true, false, true);
        Assert.StartsWith("OK:", Run("off"));
        Assert.All(system.m_clutter, entry => Assert.False(entry.m_enabled));
        Assert.Equal(1, system.ClearCalls);
        Assert.True(Terminal.LastCommand!.IsCheat);
    }

    [Fact]
    public void RepeatedOffThenOnRestoresOriginallyDisabledEntriesToo()
    {
        ClutterSystem system = Start(true, false, true);
        Run("off");
        Run("off");
        Assert.StartsWith("OK:", Run("on"));
        Assert.Equal(new[] { true, false, true }, system.m_clutter.Select(entry => entry.m_enabled));
    }

    [Fact]
    public void OnWithoutOffDoesNotEnableDisabledContent()
    {
        ClutterSystem system = Start(false, true);
        Assert.StartsWith("OK:", Run("on"));
        Assert.Equal(new[] { false, true }, system.m_clutter.Select(entry => entry.m_enabled));
    }

    [Fact]
    public void EachOverrideTakesAFreshSnapshot()
    {
        ClutterSystem system = Start(true, false);
        Run("off");
        Run("on");
        system.m_clutter[0].m_enabled = false;
        system.m_clutter[1].m_enabled = true;
        Run("off");
        Run("on");
        Assert.Equal(new[] { false, true }, system.m_clutter.Select(entry => entry.m_enabled));
    }

    [Fact]
    public void SnapshotsFollowEntryIdentityAndDoNotCrossWorldInstances()
    {
        ClutterSystem oldSystem = Start(true, false);
        Run("off");
        oldSystem.m_clutter.Reverse();
        Run("on");
        Assert.Equal(new[] { false, true }, oldSystem.m_clutter.Select(entry => entry.m_enabled));
        Run("off");
        ClutterSystem newSystem = Start(false, true);
        Run("on");
        Assert.Equal(new[] { false, true }, newSystem.m_clutter.Select(entry => entry.m_enabled));
    }

    [Fact]
    public void InvalidArgumentsAndMissingSystemDoNotReportSuccess()
    {
        ClutterSystem system = Start(true);
        Assert.StartsWith("Usage:", Run("invalid"));
        Assert.Equal(0, system.ClearCalls);
        ClutterSystem.instance = null;
        Assert.StartsWith("ERROR:", Run("off"));
    }
}

}

// Minimal runtime seams; CaptureCommands itself is compiled unchanged into this
// suite. These fields match the shipped ClutterSystem and its nested entry type;
// the plugin build separately checks the real publicized game assembly.
public sealed class ClutterSystem
{
    public static ClutterSystem? instance;
    public sealed class Clutter { public bool m_enabled; }
    public readonly List<Clutter> m_clutter = new List<Clutter>();
    public int ClearCalls;
    public void ClearAll() => ClearCalls++;
}

public sealed class Terminal
{
    public delegate void ConsoleEvent(ConsoleEventArgs args);
    public static ConsoleCommand? LastCommand;
    public sealed class ConsoleCommand
    {
        public readonly ConsoleEvent Callback;
        public readonly bool IsCheat;
        public ConsoleCommand(string name, string help, ConsoleEvent callback, bool isCheat)
        { Callback = callback; IsCheat = isCheat; LastCommand = this; }
    }
    public sealed class ConsoleEventArgs
    {
        private readonly string[] arguments;
        public ConsoleEventArgs(params string[] values) { arguments = values; }
        public int Length => arguments.Length;
        public string this[int index] => arguments[index];
        public ConsoleContext Context = new ConsoleContext();
    }
    public sealed class ConsoleContext
    {
        public readonly List<string> Output = new List<string>();
        public void AddString(string value) => Output.Add(value);
    }
}
