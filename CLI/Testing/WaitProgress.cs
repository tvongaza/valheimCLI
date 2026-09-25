using System.Globalization;

namespace valheim_cli.Testing;

/// <summary>How a wait ended, or that it goes on.</summary>
public enum WaitOutcome
{
    Waiting,
    Reached,
    TimedOut,
    Stalled,
    Unreachable,

    /// <summary>The game was there during the wait and is gone: it exited, or its plugin stopped answering.</summary>
    Lost
}

/// <summary>
/// The limits of one wait. The timeout bounds the whole wait; the stall window
/// ends it early when nothing the game reports has changed for that long; the
/// progress interval sets how often a heartbeat is printed.
/// </summary>
public sealed class WaitPolicy
{
    /// <summary>A heartbeat every 15 s: often enough to tell a wait from a hang.</summary>
    public static readonly TimeSpan DefaultProgress = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Two minutes without any change. Normal loads change something well inside that:
    /// location generation reports its progress, and the load phases follow one another.
    /// The longest quiet stretches are a modded game between the plugin answering and
    /// the main menu, loading the area around the player on a slow disk, and a world
    /// save that holds the game's main thread; each can sit on one value for a minute.
    /// </summary>
    public static readonly TimeSpan DefaultStall = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long a state that cannot reach the target must hold before the wait gives up.
    /// A logout or disconnect started just before (or just after) the wait changes the
    /// status within a few frames; the margin is for the request that starts it.
    /// </summary>
    public static readonly TimeSpan UnreachableGrace = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A game that was there is gone when this many polls in a row, spanning at least
    /// LostAfter, find neither its process nor its plugin. One failed poll between two
    /// answers (a tunnel hiccup) is not a loss; three over 4 s are (6 s at the default
    /// 2 s interval, counted from the last answer).
    /// </summary>
    public const int LostAfterPolls = 3;

    public static readonly TimeSpan LostAfter = TimeSpan.FromSeconds(4);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Heartbeat interval; zero prints none.</summary>
    public TimeSpan Progress { get; init; } = DefaultProgress;

    /// <summary>Stall window; zero never stalls.</summary>
    public TimeSpan Stall { get; init; } = DefaultStall;

    /// <summary>Keep waiting in a state that needs an action (someone else will take it).</summary>
    public bool AllowUnreachable { get; init; }

    /// <summary>Only the timeout ends the wait: the behaviour of the waits inside launch and join.</summary>
    public static WaitPolicy TimeoutOnly(TimeSpan timeout, TimeSpan progress)
    {
        return new WaitPolicy { Timeout = timeout, Progress = progress, Stall = TimeSpan.Zero, AllowUnreachable = true };
    }

    /// <summary>
    /// The policy of a test plan's waitFor step. The step's own stall wins (it knows
    /// whether a person acts during it), then the command line's --stall, then the default.
    /// </summary>
    public static bool TryForPlanStep(
        TimeSpan timeout,
        string stepStall,
        bool stepAllowUnreachable,
        TimeSpan? cliStall,
        bool cliAllowUnreachable,
        TimeSpan progress,
        out WaitPolicy policy,
        out string error)
    {
        policy = new WaitPolicy();
        error = "";
        TimeSpan stall = cliStall ?? DefaultStall;
        if (!string.IsNullOrWhiteSpace(stepStall))
        {
            if (!WaitDurations.TryParse(stepStall, out stall))
            {
                error = $"invalid stall duration '{stepStall}' (use e.g. 90s, 2m or 0 to disable)";
                return false;
            }
        }

        policy = new WaitPolicy
        {
            Timeout = timeout,
            Progress = progress,
            Stall = stall,
            AllowUnreachable = stepAllowUnreachable || cliAllowUnreachable
        };
        return true;
    }
}

public static class WaitDurations
{
    /// <summary>
    /// Strict duration: 500ms, 15s, 2m or bare seconds; zero is allowed (it disables).
    /// Unlike the lenient timeout parser, an unreadable value is an error, not a default.
    /// </summary>
    public static bool TryParse(string raw, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string text = raw.Trim().ToLowerInvariant();
        string number = text;
        double scale = 1000;
        if (text.EndsWith("ms"))
        {
            number = text[..^2];
            scale = 1;
        }
        else if (text.EndsWith("s"))
        {
            number = text[..^1];
        }
        else if (text.EndsWith("m"))
        {
            number = text[..^1];
            scale = 60000;
        }

        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
        {
            return false;
        }

        duration = TimeSpan.FromMilliseconds(value * scale);
        return true;
    }

    public static string Format(TimeSpan duration)
    {
        return duration.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s";
    }
}

/// <summary>
/// Where the game stands, in the order a launch passes through. Entering a world and
/// leaving one look alike from outside (no player, loading), so that stage is transitional.
/// </summary>
public enum WaitStage
{
    NotRunning,
    Process,
    Terminal,
    MainMenu,
    EnteringWorld,
    InWorld
}

/// <summary>
/// The part of a status that shows progress. Two snapshots are compared field by field;
/// any difference is progress. Timers (respawnWait, estimatedLocationSeconds) move on
/// their own and are left out, so a load stuck behind a running timer still stalls.
/// </summary>
public sealed class WaitSnapshot
{
    public bool Running { get; init; }
    public bool Answering { get; init; }
    public bool PluginLogExists { get; init; }
    public bool PluginLoaded { get; init; }

    /// <summary>
    /// Log size, compared only while the game reports nothing else (the CLI does not
    /// answer yet, or the state is still Unknown): during startup a growing log is the
    /// only sign of progress. Afterwards it is -1, as a game logs whether or not it moves.
    /// </summary>
    public long LogLength { get; init; } = -1;
    public string State { get; init; } = "";
    public string Phase { get; init; } = "";
    public bool ShuttingDown { get; init; }
    public bool LocationsGenerated { get; init; }
    public string LocationProgress { get; init; } = "";
    public int LocationCount { get; init; }
    public bool ActiveAreaLoaded { get; init; }
    public string ConnectionStatus { get; init; } = "";
    public string Server { get; init; } = "";
    public bool Listening { get; init; }

    public static WaitSnapshot From(GameStatus status)
    {
        bool startup = !status.IsConnected || status.State.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
        return new WaitSnapshot
        {
            Running = status.IsRunning,
            Answering = status.IsConnected,
            PluginLogExists = status.PluginLog.Exists,
            PluginLoaded = status.PluginLog.PluginLoaded,
            LogLength = startup ? status.PluginLog.Length : -1,
            State = status.State,
            Phase = status.LoadPhase,
            ShuttingDown = status.ShuttingDown,
            LocationsGenerated = status.LocationsGenerated,
            LocationProgress = status.LocationProgress.ToString("F3", CultureInfo.InvariantCulture),
            LocationCount = status.LocationCount,
            ActiveAreaLoaded = status.ActiveAreaLoaded,
            ConnectionStatus = status.ConnectionStatus,
            Server = status.ConnectedServer,
            Listening = status.Listening
        };
    }

    /// <summary>Each field that differs, as "name old -> new"; empty when nothing changed.</summary>
    public List<string> ChangesFrom(WaitSnapshot previous)
    {
        List<string> changes = new();
        Add(changes, "game", RunningName(previous.Running), RunningName(Running));
        Add(changes, "cli", AnswerName(previous.Answering), AnswerName(Answering));
        Add(changes, "bepinexLog", Bool(previous.PluginLogExists), Bool(PluginLogExists));
        Add(changes, "pluginLoaded", Bool(previous.PluginLoaded), Bool(PluginLoaded));
        if (previous.LogLength >= 0 && LogLength >= 0 && previous.LogLength != LogLength)
        {
            changes.Add(LogLength > previous.LogLength
                ? $"log +{LogLength - previous.LogLength} bytes"
                : $"log restarted ({LogLength} bytes)");
        }
        Add(changes, "state", previous.State, State);
        Add(changes, "phase", previous.Phase, Phase);
        Add(changes, "shuttingDown", Bool(previous.ShuttingDown), Bool(ShuttingDown));
        Add(changes, "locationsGenerated", Bool(previous.LocationsGenerated), Bool(LocationsGenerated));
        Add(changes, "locationProgress", previous.LocationProgress, LocationProgress);
        Add(changes, "locationCount", previous.LocationCount.ToString(CultureInfo.InvariantCulture), LocationCount.ToString(CultureInfo.InvariantCulture));
        Add(changes, "activeAreaLoaded", Bool(previous.ActiveAreaLoaded), Bool(ActiveAreaLoaded));
        Add(changes, "connection", previous.ConnectionStatus, ConnectionStatus);
        Add(changes, "server", previous.Server, Server);
        Add(changes, "listening", Bool(previous.Listening), Bool(Listening));
        return changes;
    }

    private static void Add(List<string> changes, string name, string before, string after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            changes.Add($"{name} {Show(before)} -> {Show(after)}");
        }
    }

    private static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "none" : value;
    private static string Bool(bool value) => value ? "true" : "false";
    private static string RunningName(bool value) => value ? "running" : "not_running";
    private static string AnswerName(bool value) => value ? "answering" : "not_answering";
}

/// <summary>What a wait has seen, printed every progress interval.</summary>
public sealed class WaitHeartbeat
{
    public string Event { get; init; } = "wait-progress";
    public double ElapsedSeconds { get; init; }
    public double TimeoutSeconds { get; init; }
    public string Target { get; init; } = "";

    /// <summary>not_running or starting (running, the plugin not answering yet), not_answering for a remote game; null once the plugin answers.</summary>
    public string? Game { get; init; }
    public string State { get; init; } = "";
    public string Phase { get; init; } = "";
    public string ConnectionStatus { get; init; } = "";

    /// <summary>Location generation progress, 0 to 1, while the world reports one.</summary>
    public float? LocationProgress { get; init; }

    /// <summary>What changed since the previous heartbeat (or the start of the wait).</summary>
    public List<string> Changes { get; init; } = new();

    /// <summary>How long the status has been as it is now.</summary>
    public double UnchangedSeconds { get; init; }

    public string ToLine()
    {
        string line = $"WAIT: {Seconds(ElapsedSeconds)}/{Seconds(TimeoutSeconds)} for {Target}; " +
                      (Game != null ? $"game={Game} " : "") +
                      $"state={Show(State)} phase={Show(Phase)} connection={Show(ConnectionStatus)}";
        if (LocationProgress.HasValue)
        {
            line += $" locationProgress={LocationProgress.Value.ToString("F3", CultureInfo.InvariantCulture)}";
        }

        line += Changes.Count > 0
            ? $"; changed: {string.Join(", ", Changes)}"
            : $"; unchanged for {Seconds(UnchangedSeconds)}";
        return line;
    }

    private static string Seconds(double seconds) => seconds.ToString("F0", CultureInfo.InvariantCulture) + "s";
    private static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Replace(' ', '_');
}

/// <summary>One observation's verdict, and the heartbeat to print if one is due.</summary>
public sealed class WaitStep
{
    public WaitOutcome Outcome { get; init; }
    public string Reason { get; init; } = "";

    /// <summary>The errorCode of an ending other than reached: timeout, stalled, unreachable, game_exited or plugin_lost.</summary>
    public string ErrorCode { get; init; } = "";
    public WaitHeartbeat? Heartbeat { get; init; }
}

public static class WaitReachability
{
    public static WaitStage StageOf(GameStatus status)
    {
        if (!status.IsRunning)
        {
            return WaitStage.NotRunning;
        }

        if (!status.IsConnected)
        {
            return WaitStage.Process;
        }

        string state = status.State;
        if (state.Equals("MainMenu", StringComparison.OrdinalIgnoreCase))
        {
            return WaitStage.MainMenu;
        }

        if (state.Equals("InWorld", StringComparison.OrdinalIgnoreCase))
        {
            return WaitStage.InWorld;
        }

        if (state.Equals("Loading", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("InWorldNoPlayer", StringComparison.OrdinalIgnoreCase))
        {
            return WaitStage.EnteringWorld;
        }

        return WaitStage.Terminal;
    }

    /// <summary>
    /// The server turned the connection down for good (wrong version or password), or the
    /// game is a dedicated server and the target is a client's: no amount of waiting
    /// reaches it. Definitive, so it ends the wait at once.
    /// </summary>
    public static string Rejected(WaitTarget target, GameStatus status)
    {
        if (target == WaitTarget.ServerConnected && status.HasUnrecoverableConnectionFailure)
        {
            return $"the server rejected the connection (connection={status.ConnectionStatus}); fix the version or password and join again";
        }

        // A dedicated server never has a main menu, a local player or a connection to
        // another server: its state stays InWorldNoPlayer however long the wait.
        if (status.Dedicated && target is WaitTarget.MainMenu or WaitTarget.InWorld or WaitTarget.LocalPlayer or WaitTarget.ServerConnected)
        {
            return $"this is a dedicated server, which never has a main menu, a local player or a server connection of its own ({WaitTargets.ToName(target)}); wait for server-ready";
        }

        return "";
    }

    /// <summary>
    /// Why the target cannot be reached from this status without an action, or "" when
    /// it can (or when the status cannot tell). Only settled states count: a game that
    /// is loading may be entering a world or leaving one, and a game that is at the main
    /// menu may have a join queued, so neither is ever called unreachable here.
    /// </summary>
    public static string Why(WaitTarget target, GameStatus status)
    {
        // A game that stops during the wait is not decided here: WaitLoss ends the
        // wait within seconds, with its own code, instead of after this grace.
        WaitStage stage = StageOf(status);
        if (target == WaitTarget.MainMenu && stage == WaitStage.InWorld && !IsLeavingWorld(status))
        {
            return "the game is in a world and nothing is leaving it; the main menu comes only after a logout (cli_logout_save) or a disconnect";
        }

        return "";
    }

    /// <summary>
    /// A logout or a lost connection is under way: the game is shutting down its world
    /// (reported by the plugin), or the connection status reports an error or a disconnect.
    /// </summary>
    public static bool IsLeavingWorld(GameStatus status)
    {
        return status.ShuttingDown ||
               status.ConnectionStatus.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
               status.ConnectionStatus.Contains("Disconnect", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Notices a game that was there during the wait and is gone. Only a loss counts: a
/// game that has not come up yet (a wait started ahead of a launch) never is one.
/// The local process was seen and has exited (game_exited), or the plugin answered and
/// has stopped answering (plugin_lost: a game through a tunnel or a dedicated server,
/// whose process is not visible here, or a local game still quitting). Each poll
/// that finds the game gone counts; one that finds its plugin answering resets the
/// count, so a single failed poll between answers is a blip, not a loss.
/// </summary>
public sealed class WaitLoss
{
    private bool _seenProcess;
    private bool _seenAnswer;
    private int _misses;
    private DateTime _firstMiss;

    /// <summary>game_exited or plugin_lost once the loss is decided, otherwise "".</summary>
    public string Code { get; private set; } = "";

    public string Reason { get; private set; } = "";

    public bool Observe(GameStatus status, DateTime at)
    {
        bool processGone = _seenProcess && !status.ProcessSeenLocally;
        bool answerGone = _seenAnswer && !status.IsConnected;
        _seenProcess |= status.ProcessSeenLocally;
        _seenAnswer |= status.IsConnected;
        if (status.IsConnected || (!processGone && !answerGone))
        {
            _misses = 0;
            return false;
        }

        if (_misses == 0)
        {
            _firstMiss = at;
        }

        _misses++;
        TimeSpan gone = at - _firstMiss;
        if (_misses < WaitPolicy.LostAfterPolls || gone < WaitPolicy.LostAfter)
        {
            return false;
        }

        string polls = $"{_misses} polls over {WaitDurations.Format(gone)}";
        if (processGone)
        {
            Code = "game_exited";
            Reason = $"the game process exited during the wait (not found for {polls}); start it again";
        }
        else
        {
            Code = "plugin_lost";
            Reason = $"the plugin answered earlier in this wait and has not answered for {polls}" +
                     (status.ProcessSeenLocally ? "; the game process is still here (it may be quitting or hung)" : "; the game (or the tunnel to it) is gone");
        }

        return true;
    }
}

/// <summary>
/// Decides, one status observation at a time, whether a wait has reached its target,
/// timed out, stalled, become unreachable or lost the game, and when to print a heartbeat. It keeps
/// no clock of its own: every call passes the time the status was read, which keeps
/// it testable and lets a caller stamp an observation with the moment the status was
/// asked for (a busy game can take seconds to answer; that time is not a stall).
/// </summary>
public sealed class WaitTracker
{
    private readonly WaitTarget _target;
    private readonly WaitPolicy _policy;
    private readonly DateTime _start;
    private WaitSnapshot? _current;
    private WaitSnapshot? _atLastBeat;
    private DateTime _changedAt;
    private DateTime _lastBeat;
    private bool _seenRunning;
    private readonly WaitLoss _loss = new();

    public WaitTracker(WaitTarget target, WaitPolicy policy, DateTime start)
    {
        _target = target;
        _policy = policy;
        _start = start;
        _changedAt = start;
        _lastBeat = start;
    }

    public List<WaitHeartbeat> Heartbeats { get; } = new();

    /// <summary>How long the status had been unchanged at the last observation.</summary>
    public TimeSpan Unchanged { get; private set; }

    public TimeSpan Elapsed { get; private set; }

    public WaitStep Observe(GameStatus status, DateTime at)
    {
        WaitSnapshot snapshot = WaitSnapshot.From(status);
        if (_current == null || snapshot.ChangesFrom(_current).Count > 0)
        {
            _changedAt = at;
        }

        _current = snapshot;
        _atLastBeat ??= snapshot;
        _seenRunning |= status.IsRunning;
        Elapsed = at - _start;
        Unchanged = at - _changedAt;

        if (status.Satisfies(_target))
        {
            return new WaitStep { Outcome = WaitOutcome.Reached };
        }

        string rejected = WaitReachability.Rejected(_target, status);
        if (rejected.Length > 0)
        {
            return Ended(WaitOutcome.Unreachable, rejected);
        }

        // A game that exits comes back only when someone starts it, so --allow-unreachable
        // (someone else acts) keeps waiting through it, as the launch and join waits do.
        bool lost = _loss.Observe(status, at);
        if (lost && !_policy.AllowUnreachable && _target != WaitTarget.Process)
        {
            return new WaitStep { Outcome = WaitOutcome.Lost, Reason = _loss.Reason, ErrorCode = _loss.Code };
        }

        if (!_policy.AllowUnreachable && Unchanged >= WaitPolicy.UnreachableGrace)
        {
            string why = WaitReachability.Why(_target, status);
            if (why.Length > 0)
            {
                return Ended(WaitOutcome.Unreachable, why);
            }
        }

        if (Elapsed >= _policy.Timeout)
        {
            return Ended(WaitOutcome.TimedOut, $"not reached within {WaitDurations.Format(_policy.Timeout)}");
        }

        // A game that has not been seen running has not started, which is not a stall:
        // the wait may have been started ahead of the launch. A remote game is never seen
        // until its plugin answers (no process or log here tells a start from a failed
        // one), so its silence stalls like any other unchanged status.
        if (_policy.Stall > TimeSpan.Zero && (_seenRunning || status.Remote) && Unchanged >= _policy.Stall)
        {
            string window = $"{WaitDurations.Format(Unchanged)} (stall window {WaitDurations.Format(_policy.Stall)})";
            return Ended(WaitOutcome.Stalled, _seenRunning
                ? $"nothing the game reports changed for {window}"
                : $"nothing answered on the port for {window}; this machine cannot see a remote game's process or log: check the tunnel, that the game started, and that valheimCLI loaded (the BepInEx log on the game's machine)");
        }

        WaitHeartbeat? beat = null;
        if (_policy.Progress > TimeSpan.Zero && at - _lastBeat >= _policy.Progress)
        {
            beat = new WaitHeartbeat
            {
                ElapsedSeconds = Math.Round(Elapsed.TotalSeconds),
                TimeoutSeconds = Math.Round(_policy.Timeout.TotalSeconds),
                Target = WaitTargets.ToName(_target),
                Game = status.Remote && !status.IsConnected ? "not_answering"
                    : !status.IsRunning ? "not_running" : !status.IsConnected ? "starting" : null,
                State = status.State,
                Phase = status.LoadPhase,
                ConnectionStatus = status.ConnectionStatus,
                LocationProgress = ShowsLocationProgress(status) ? status.LocationProgress : null,
                Changes = snapshot.ChangesFrom(_atLastBeat),
                UnchangedSeconds = Math.Round(Unchanged.TotalSeconds)
            };
            Heartbeats.Add(beat);
            _lastBeat = at;
            _atLastBeat = snapshot;
        }

        return new WaitStep { Outcome = WaitOutcome.Waiting, Heartbeat = beat };
    }

    private static WaitStep Ended(WaitOutcome outcome, string reason)
    {
        return new WaitStep { Outcome = outcome, Reason = reason, ErrorCode = ErrorCode(outcome) };
    }

    private static bool ShowsLocationProgress(GameStatus status)
    {
        return status.LoadPhase.Equals("generating_locations", StringComparison.OrdinalIgnoreCase) ||
               (status.LocationProgress > 0f && status.LocationProgress < 1f);
    }

    /// <summary>
    /// The errorCode for an outcome that ended the wait without reaching the target. A lost
    /// game has two (game_exited, plugin_lost); the step that decided it carries the one.
    /// </summary>
    public static string ErrorCode(WaitOutcome outcome)
    {
        return outcome switch
        {
            WaitOutcome.TimedOut => "timeout",
            WaitOutcome.Stalled => "stalled",
            WaitOutcome.Unreachable => "unreachable",
            WaitOutcome.Lost => "plugin_lost",
            _ => ""
        };
    }

    /// <summary>
    /// Exit code per outcome. A stall is a timeout that came early, so it keeps the
    /// timeout's code and a script that retries on 2 still does. An unreachable target
    /// is the game being in the wrong state for it, which is what 5 (game not ready) means.
    /// A game that went away during the wait has its own code: no state of it is left to
    /// be ready or not, and a script restarts the game rather than retrying the wait.
    /// </summary>
    public static CliExitCode ExitCode(WaitOutcome outcome)
    {
        return outcome switch
        {
            WaitOutcome.Reached => CliExitCode.Success,
            WaitOutcome.Unreachable => CliExitCode.GameNotReady,
            WaitOutcome.Lost => CliExitCode.GameLost,
            _ => CliExitCode.Timeout
        };
    }
}
