using System.Diagnostics;
using System.Text.RegularExpressions;

namespace valheim_cli.Testing;

public class GameLauncher
{
    private readonly string _gamePath;
    private readonly string _host;
    private readonly int _port;
    private readonly string? _connect;
    private readonly string? _password;
    private readonly bool _remote;
    private Process? _gameProcess;

    /// <param name="remote">
    /// The game is on another machine (reached through a tunnel on this machine's port).
    /// Its process and its BepInEx log are not here, so none of this machine's are read:
    /// a local Valheim (someone playing) would otherwise stand in for the remote game.
    /// </param>
    public GameLauncher(string? gamePath = null, string host = ConnectionDefaults.Host, int port = ConnectionDefaults.Port, string? connect = null, string? password = null, bool remote = false)
    {
        _gamePath = ResolveGamePath(gamePath);
        _host = host;
        _port = port;
        _connect = connect;
        _password = password;
        _remote = remote;
    }

    public string GamePath => _gamePath;
    public bool Remote => _remote;
    public bool HasServerConnect => !string.IsNullOrWhiteSpace(_connect);

    /// <summary>
    /// Resolves game path with priority: explicit path > VALHEIM_PATH env > default
    /// </summary>
    private static string ResolveGamePath(string? explicitPath)
    {
        // Priority 1: Explicit path argument
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return explicitPath;
        }

        // Priority 2: Environment variable
        string? envPath = Environment.GetEnvironmentVariable("VALHEIM_PATH");
        if (!string.IsNullOrEmpty(envPath))
        {
            return envPath;
        }

        // Priority 3: Default Steam path for the current OS
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsLinux())
        {
            return Path.Combine(homeDir, ".steam", "debian-installation", "steamapps", "common", "Valheim");
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam",
                "steamapps",
                "common",
                "Valheim");
        }

        return Path.Combine(homeDir, "Library", "Application Support", "Steam", "steamapps", "common", "Valheim");
    }

    /// <summary>
    /// Check if Valheim process is currently running
    /// </summary>
    public bool IsGameRunning()
    {
        if (_remote)
        {
            return false;
        }

        return ProcessExists("valheim") || ProcessExists("Valheim") || ProcessExists("valheim.x86_64");
    }

    public bool IsSteamRunning()
    {
        return ProcessExists("steam") || ProcessExists("Steam") || ProcessExists("steamwebhelper");
    }

    /// <summary>
    /// Check if a process exists by name, optionally performing an action on each process
    /// </summary>
    private static bool ProcessExists(string name, Action<Process>? action = null)
    {
        Process[] processes = Process.GetProcessesByName(name);
        bool found = processes.Length > 0;
        foreach (Process p in processes)
        {
            try
            {
                action?.Invoke(p);
            }
            catch
            {
                // Process may have already exited
            }
            finally
            {
                p.Dispose();
            }
        }
        return found;
    }

    /// <summary>
    /// Try to connect to the game's TCP server
    /// </summary>
    public bool TryConnect()
    {
        using ValheimClient client = new ValheimClient(_host, _port);
        return client.Connect();
    }

    public bool TryOpenClient(out ValheimClient client)
    {
        client = new ValheimClient(_host, _port);
        if (client.Connect())
        {
            return true;
        }

        client.Dispose();
        return false;
    }

    /// <summary>
    /// Launch Valheim using run_bepinex.sh
    /// </summary>
    public bool LaunchGame()
    {
        if (_remote)
        {
            Console.Error.WriteLine("Cannot launch a remote game (--remote): start it on its own machine.");
            return false;
        }

        string scriptPath = Path.Combine(_gamePath, "run_bepinex.sh");

        if (!File.Exists(scriptPath))
        {
            Console.Error.WriteLine($"Launch script not found: {scriptPath}");
            return false;
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"\"{scriptPath}\"",
                WorkingDirectory = _gamePath,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

            _gameProcess = Process.Start(startInfo);
            return _gameProcess != null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to launch game: {ex.Message}");
            return false;
        }
    }

    public bool QueueServerConnect(out List<string> output)
    {
        output = new List<string>();
        if (!HasServerConnect)
        {
            return true;
        }

        using ValheimClient client = new ValheimClient(_host, _port);
        if (!client.Connect())
        {
            output.Add($"ERROR: Cannot connect to Valheim CLI server at {_host}:{_port}");
            return false;
        }

        output = client.SendCommand(BuildConnectCommand());
        return output.Any(line => line.StartsWith("OK:", StringComparison.OrdinalIgnoreCase));
    }

    private string BuildConnectCommand()
    {
        string command = $"cli_connect_direct {_connect}";
        if (!string.IsNullOrWhiteSpace(_password))
        {
            command += $" {_password}";
        }

        return command;
    }

    /// <summary>
    /// Waits for a readiness target under a timeout only (a rejected server connection
    /// also ends it). Launch and join use this; heartbeats are printed when onHeartbeat is set.
    /// </summary>
    public async Task<GameStatus> WaitForTargetAsync(
        WaitTarget target,
        TimeSpan timeout,
        TimeSpan interval,
        CancellationToken cancellationToken = default,
        TimeSpan? progress = null,
        Action<WaitHeartbeat>? onHeartbeat = null,
        bool endAtPasswordPrompt = false)
    {
        WaitPolicy policy = WaitPolicy.TimeoutOnly(timeout, onHeartbeat != null ? progress ?? WaitPolicy.DefaultProgress : TimeSpan.Zero);
        policy = new WaitPolicy
        {
            Timeout = policy.Timeout,
            Progress = policy.Progress,
            Stall = policy.Stall,
            AllowUnreachable = policy.AllowUnreachable,
            EndAtPasswordPrompt = endAtPasswordPrompt
        };
        WaitResult result = await WaitAsync(target, policy, interval, onHeartbeat, cancellationToken);
        return result.Status;
    }

    /// <summary>
    /// Polls the status every interval and lets a WaitTracker decide: reached, timed out,
    /// stalled or unreachable. Each observation is stamped with the moment the status was
    /// asked for, because a game whose main thread is busy answers late.
    /// </summary>
    public async Task<WaitResult> WaitAsync(
        WaitTarget target,
        WaitPolicy policy,
        TimeSpan interval,
        Action<WaitHeartbeat>? onHeartbeat = null,
        CancellationToken cancellationToken = default)
    {
        DateTime start = DateTime.Now;
        WaitTracker tracker = new WaitTracker(target, policy, start);
        GameStatus status;
        WaitStep step;
        while (true)
        {
            DateTime observedAt = DateTime.Now;
            status = GetStatus();
            step = tracker.Observe(status, observedAt);
            if (step.Heartbeat != null)
            {
                onHeartbeat?.Invoke(step.Heartbeat);
            }

            if (step.Outcome != WaitOutcome.Waiting || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            TimeSpan left = start + policy.Timeout - DateTime.Now;
            TimeSpan delay = left < interval ? left : interval;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }

        if (step.Outcome != WaitOutcome.Reached)
        {
            status.WaitTimedOut = step.Outcome == WaitOutcome.TimedOut || step.Outcome == WaitOutcome.Waiting;
            status.WaitTargetName = WaitTargets.ToName(target);
        }

        WaitOutcome outcome = step.Outcome == WaitOutcome.Waiting ? WaitOutcome.TimedOut : step.Outcome;
        return new WaitResult
        {
            Outcome = outcome,
            ErrorCode = step.ErrorCode.Length > 0 ? step.ErrorCode : WaitTracker.ErrorCode(outcome),
            Reason = step.Reason,
            Status = status,
            Elapsed = tracker.Elapsed,
            Unchanged = tracker.Unchanged,
            Heartbeats = tracker.Heartbeats
        };
    }

    public async Task<JoinResult> JoinDirectAsync(
        string server,
        string? password,
        string? character,
        bool createCharacter,
        TimeSpan timeout,
        TimeSpan interval,
        CancellationToken cancellationToken = default,
        bool skipIntro = false,
        TimeSpan? progress = null,
        Action<WaitHeartbeat>? onHeartbeat = null)
    {
        JoinResult result = new() { Server = server };
        GameStatus terminalStatus = await WaitForTargetAsync(WaitTarget.Terminal, timeout, interval, cancellationToken, progress, onHeartbeat);
        if (!terminalStatus.Satisfies(WaitTarget.Terminal))
        {
            result.ErrorCode = terminalStatus.WaitTimedOut ? "timeout" : terminalStatus.DiagnosticCode;
            result.Message = "Valheim CLI server was not ready for commands.";
            result.FinalStatus = terminalStatus;
            return result;
        }

        using ValheimClient client = new ValheimClient(_host, _port);
        if (!client.Connect())
        {
            result.ErrorCode = "connection_failed";
            result.Message = $"Cannot connect to Valheim CLI server at {_host}:{_port}.";
            result.FinalStatus = GetStatus();
            return result;
        }

        if (!string.IsNullOrWhiteSpace(character))
        {
            GameStatus menuStatus = await WaitForTargetAsync(WaitTarget.MainMenu, timeout, interval, cancellationToken, progress, onHeartbeat);
            if (!menuStatus.Satisfies(WaitTarget.MainMenu))
            {
                result.ErrorCode = menuStatus.WaitTimedOut ? "timeout" : menuStatus.DiagnosticCode;
                result.Message = "Main menu was not ready for character selection.";
                result.FinalStatus = menuStatus;
                return result;
            }

            string command = createCharacter
                ? $"cli_create_character {character} --local" + (skipIntro ? " --skip-intro" : "")
                : $"cli_select_character {character}";
            CommandResult characterResult = client.ExecuteCommand(command);
            result.Steps.Add(characterResult);
            if (!characterResult.Ok)
            {
                result.ErrorCode = characterResult.ErrorCode;
                result.Message = characterResult.Message;
                result.FinalStatus = GetStatus();
                return result;
            }
        }

        string joinCommand = $"cli_connect_direct {server}";
        if (!string.IsNullOrWhiteSpace(password))
        {
            joinCommand += $" {password}";
        }

        CommandResult joinCommandResult = client.ExecuteCommand(joinCommand);
        result.Steps.Add(new CommandResult
        {
            Command = "cli_connect_direct <server> <password>",
            Ok = joinCommandResult.Ok,
            ErrorCode = joinCommandResult.ErrorCode,
            Message = joinCommandResult.Message,
            Output = joinCommandResult.Output
        });

        if (!joinCommandResult.Ok)
        {
            result.ErrorCode = joinCommandResult.ErrorCode;
            result.Message = joinCommandResult.Message;
            result.FinalStatus = GetStatus();
            return result;
        }

        // The join passes its password with the connect, so a prompt that stays open means the server asked for a
        // password the join did not give: the join ends there instead of at its timeout.
        GameStatus connectedStatus = await WaitForTargetAsync(WaitTarget.ServerConnected, timeout, interval, cancellationToken, progress, onHeartbeat, endAtPasswordPrompt: true);
        result.FinalStatus = connectedStatus;
        if (connectedStatus.Satisfies(WaitTarget.ServerConnected))
        {
            result.Ok = true;
            result.Message = "Connected to dedicated server.";
            return result;
        }

        result.ErrorCode = connectedStatus.WaitTimedOut ? "timeout" : connectedStatus.DiagnosticCode;
        result.Message = ClassifyJoinFailure(connectedStatus);
        return result;
    }

    private static string ClassifyJoinFailure(GameStatus status)
    {
        string connectionStatus = status.ConnectionStatus.ToLowerInvariant();
        if (connectionStatus.Contains("errorversion"))
        {
            return "Wrong game or mod version.";
        }

        if (connectionStatus.Contains("errorpassword"))
        {
            return "Server password was rejected.";
        }

        if (status.PasswordPrompt)
        {
            return "The server asks for a password and the join gave none; pass --password-file.";
        }

        if (connectionStatus.Contains("errorbanned"))
        {
            return "Refused: banned from this server, or not on its permitted list.";
        }

        if (connectionStatus.Contains("errorfull"))
        {
            return "Refused: the server is full.";
        }

        if (connectionStatus.Contains("erroralreadyconnected"))
        {
            return "Refused: this player is already connected to the server.";
        }

        if (connectionStatus.Contains("errorkicked"))
        {
            return "Kicked by the server.";
        }

        if (connectionStatus.Contains("errorplatformexcluded") || connectionStatus.Contains("errorcrossplayprivilege"))
        {
            return "Refused: the server does not accept this platform (crossplay).";
        }

        if (connectionStatus.Contains("errorconnectfailed"))
        {
            return "The connection to the server could not be made.";
        }

        if (connectionStatus.Contains("disconnected"))
        {
            return "Disconnected before server connection completed.";
        }

        return "Server connection was not established before timeout.";
    }

    /// <summary>
    /// Stop the game process gracefully
    /// </summary>
    public void StopGame()
    {
        // A remote game is not ours to stop, and killing by name would stop a local one.
        if (_remote)
        {
            Console.Error.WriteLine("Not stopping a remote game (--remote): stop it on its own machine.");
            return;
        }

        // First try to kill the tracked process
        if (_gameProcess != null && !_gameProcess.HasExited)
        {
            try
            {
                _gameProcess.Kill(entireProcessTree: true);
                _gameProcess.Dispose();
                _gameProcess = null;
                return;
            }
            catch
            {
                // Fall through to find by name
            }
        }

        // Find and kill any Valheim processes.
        Action<Process> killAction = p => p.Kill(entireProcessTree: true);
        ProcessExists("valheim", killAction);
        ProcessExists("Valheim", killAction);
        ProcessExists("valheim.x86_64", killAction);
    }

    /// <summary>
    /// Get the current status of the game
    /// </summary>
    public GameStatus GetStatus()
    {
        bool running = IsGameRunning();
        bool connected = false;
        string state = "Unknown";
        string connectionStatus = "";
        string server = "";
        PluginLogInfo pluginLog = GetPluginLogInfo();
        if (TryOpenClient(out ValheimClient client))
        {
            try
            {
                connected = true;
                Dictionary<string, string> statusDetails = client.GetStatusDetails();
                if (statusDetails.TryGetValue("state", out string? detailedState) && !string.IsNullOrWhiteSpace(detailedState))
                {
                    state = detailedState;
                }
                else
                {
                    state = client.GetState();
                }

                bool statusLineRead = client.StatusLineRead;
                client.TryGetConnectionStatus(out connectionStatus, out server);
                GameStatus answered = new GameStatus
                {
                    IsRunning = GameStatus.RunningFrom(running, connected),
                    ProcessSeenLocally = running,
                    Remote = _remote,
                    IsConnected = connected,
                    GamePath = _gamePath,
                    Host = _host,
                    Port = _port,
                    State = state,
                    ConnectionStatus = connectionStatus,
                    ConnectedServer = server,
                    PluginLog = pluginLog
                };
                ApplyStatusDetails(answered, statusDetails, statusLineRead);
                return answered;
            }
            catch (Exception ex) when (ex is IOException || ex is System.Net.Sockets.SocketException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                // The game went away between the greeting and the answer (it exited, or the
                // tunnel to it closed): this poll found no plugin, which a wait counts as such.
                connected = false;
                state = "Unknown";
                connectionStatus = "";
                server = "";
            }
            finally
            {
                client.Dispose();
            }
        }

        return new GameStatus
        {
            IsRunning = running,
            ProcessSeenLocally = running,
            Remote = _remote,
            IsConnected = connected,
            GamePath = _gamePath,
            Host = _host,
            Port = _port,
            State = state,
            ConnectionStatus = connectionStatus,
            ConnectedServer = server,
            PluginLog = pluginLog
        };
    }

    /// <summary>
    /// Copies the plugin's status details into a status. From a STATUS line (fromStatusLine) the
    /// fields it carried are kept too, so a field the plugin build does not report is told apart
    /// from one it reports as false; from the state-only fallback nothing is known about them.
    /// </summary>
    internal static void ApplyStatusDetails(GameStatus status, Dictionary<string, string> details, bool fromStatusLine)
    {
        status.LoadPhase = GetDetail(details, "phase");
        status.ShuttingDown = GetBoolDetail(details, "shuttingDown");
        status.LocationsGenerated = GetBoolDetail(details, "locationsGenerated");
        status.LocationProgress = GetFloatDetail(details, "locationProgress");
        status.EstimatedLocationSeconds = GetFloatDetail(details, "estimatedLocationSeconds");
        status.LocationCount = GetIntDetail(details, "locationCount");
        status.ActiveAreaLoaded = GetBoolDetail(details, "activeAreaLoaded");
        status.RespawnWait = GetFloatDetail(details, "respawnWait");
        status.Dedicated = GetBoolDetail(details, "dedicated");
        status.Listening = GetBoolDetail(details, "listening");
        status.PasswordPrompt = GetBoolDetail(details, "passwordPrompt");
        status.ReportedFields = fromStatusLine ? new HashSet<string>(details.Keys, StringComparer.OrdinalIgnoreCase) : null;
    }

    private static string GetDetail(Dictionary<string, string> details, string key)
    {
        return details.TryGetValue(key, out string? value) ? value : "";
    }

    private static bool GetBoolDetail(Dictionary<string, string> details, string key)
    {
        return details.TryGetValue(key, out string? value) && value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static float GetFloatDetail(Dictionary<string, string> details, string key)
    {
        if (!details.TryGetValue(key, out string? value))
        {
            return 0f;
        }

        return float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : 0f;
    }

    private static int GetIntDetail(Dictionary<string, string> details, string key)
    {
        if (!details.TryGetValue(key, out string? value))
        {
            return 0;
        }

        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;
    }

    public PluginLogInfo GetPluginLogInfo()
    {
        if (_remote)
        {
            return new PluginLogInfo();
        }

        string logPath = Path.Combine(_gamePath, "BepInEx", "LogOutput.log");
        PluginLogInfo info = new() { Path = logPath };
        if (!File.Exists(logPath))
        {
            return info;
        }

        info.Exists = true;
        try
        {
            string text;
            using (FileStream stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream))
            {
                text = reader.ReadToEnd();
                info.Length = stream.Length;
            }
            MatchCollection matches = Regex.Matches(text, @"valheimCLI loaded\. CLI server on port (?<port>\d+)", RegexOptions.IgnoreCase);
            if (matches.Count == 0)
            {
                return info;
            }

            Match last = matches[^1];
            info.PluginLoaded = true;
            if (int.TryParse(last.Groups["port"].Value, out int detectedPort))
            {
                info.Port = detectedPort;
            }
        }
        catch
        {
            info.Exists = false;
        }

        return info;
    }
}

public class GameStatus
{
    /// <summary>
    /// The game is running: its process is on this machine, or its plugin answers on
    /// the port. A game reached through a tunnel, or a dedicated server (a process
    /// under another name), has no local process named valheim but answers.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>A Valheim client process was found on this machine.</summary>
    public bool ProcessSeenLocally { get; set; }

    /// <summary>
    /// The game is on another machine (--remote): only its plugin's answers describe it,
    /// so a game that does not answer is not known to be starting or stopped.
    /// </summary>
    public bool Remote { get; set; }

    public static bool RunningFrom(bool processSeenLocally, bool pluginAnswered) => processSeenLocally || pluginAnswered;

    public bool IsConnected { get; set; }
    public string GamePath { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string State { get; set; } = "Unknown";
    public string LoadPhase { get; set; } = "";

    /// <summary>The world is being shut down (a logout is under way); false from a plugin that does not report it.</summary>
    public bool ShuttingDown { get; set; }
    public bool LocationsGenerated { get; set; }
    public float LocationProgress { get; set; }
    public float EstimatedLocationSeconds { get; set; }
    public int LocationCount { get; set; }
    public bool ActiveAreaLoaded { get; set; }
    public float RespawnWait { get; set; }

    /// <summary>The game is a dedicated server (no local player, ever); false from a plugin that does not report it.</summary>
    public bool Dedicated { get; set; }

    /// <summary>The game is a server with its network socket open for players; false from a plugin that does not report it.</summary>
    public bool Listening { get; set; }
    public string ConnectionStatus { get; set; } = "";
    public string ConnectedServer { get; set; } = "";

    /// <summary>The game waits at the server's password prompt (the join gave no password); false from an older plugin.</summary>
    public bool PasswordPrompt { get; set; }
    public bool WaitTimedOut { get; set; }
    public string WaitTargetName { get; set; } = "";

    /// <summary>
    /// The fields of the plugin's status line, or null when none was read (the plugin did not answer,
    /// or answered with its state alone). A field the plugin build does not report reads as false above;
    /// this tells the two apart.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public HashSet<string>? ReportedFields { get; set; }

    /// <summary>Which of these fields the plugin's status line did not carry; empty when it carried all or none was read.</summary>
    public List<string> MissingFields(IEnumerable<string> fields)
    {
        return ReportedFields == null ? new List<string>() : fields.Where(field => !ReportedFields.Contains(field)).ToList();
    }
    public PluginLogInfo PluginLog { get; set; } = new();

    public bool ProcessReady => IsRunning;
    public bool PluginServerReady => IsConnected;
    public bool TerminalReady => IsConnected;
    public bool MainMenuReady => State.Equals("MainMenu", StringComparison.OrdinalIgnoreCase);
    public bool InWorldReady => State.Equals("InWorld", StringComparison.OrdinalIgnoreCase);
    public bool LocalPlayerReady => InWorldReady;
    public bool ServerConnected => InWorldReady && ConnectionStatus.Equals("Connected", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A server's world is up for players: its locations exist, it listens, and it is not
    /// shutting down. The same for a new world (after generating its locations) and an
    /// existing one (straight after loading), unlike the log line written only when locations are generated.
    /// </summary>
    public bool ServerReady => IsConnected && LocationsGenerated && Listening && !ShuttingDown;
    /// <summary>
    /// The connection attempt is over: every ZNet.ConnectionStatus that starts with Error (wrong version or
    /// password, banned or not permitted, full, kicked, already connected, platform or crossplay refused, failed
    /// or dropped) ends that attempt, and only a new join can connect. A banned player used to wait out the whole
    /// join timeout on ErrorBanned (25 Sep 2026).
    /// </summary>
    public bool HasUnrecoverableConnectionFailure => ConnectionStatus.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
    public string DiagnosticCode
    {
        get
        {
            if (Remote && !IsConnected)
            {
                return "remote_not_answering";
            }

            if (!IsRunning)
            {
                return "game_not_running";
            }

            if (!IsConnected)
            {
                if (PluginLog.PluginLoaded && PluginLog.Port.HasValue && PluginLog.Port.Value != Port)
                {
                    return "wrong_port";
                }

                if (PluginLog.PluginLoaded)
                {
                    return "plugin_server_not_listening";
                }

                if (PluginLog.Exists)
                {
                    return "plugin_missing_or_not_loaded";
                }

                return "bepinex_log_missing";
            }

            return "not_ready";
        }
    }

    public string DiagnosticMessage
    {
        get
        {
            string code = DiagnosticCode;
            switch (code)
            {
                case "remote_not_answering":
                    return $"Nothing answers on {Host}:{Port}, and this machine cannot see a remote game's process or log.";
                case "game_not_running":
                    return "Valheim process is not running.";
                case "wrong_port":
                    return $"valheimCLI loaded on port {PluginLog.Port}, but the CLI is checking {Port}.";
                case "plugin_server_not_listening":
                    return "valheimCLI appears in the BepInEx log, but the requested CLI port is not accepting connections.";
                case "plugin_missing_or_not_loaded":
                    return "BepInEx log exists, but no valheimCLI loaded marker was found.";
                case "bepinex_log_missing":
                    return "BepInEx log was not found at the resolved game path.";
                case "not_ready":
                    return IsConnected ? "CLI server is reachable." : "Game is running, but the requested readiness target has not been reached.";
                default:
                    return "Game is running, but the requested readiness target has not been reached.";
            }
        }
    }

    public bool Satisfies(WaitTarget target)
    {
        return target switch
        {
            WaitTarget.Process => ProcessReady,
            WaitTarget.PluginServer => PluginServerReady,
            WaitTarget.Terminal => TerminalReady,
            WaitTarget.MainMenu => MainMenuReady,
            WaitTarget.InWorld => InWorldReady,
            WaitTarget.LocalPlayer => LocalPlayerReady,
            WaitTarget.ServerConnected => ServerConnected,
            WaitTarget.ServerReady => ServerReady,
            _ => false
        };
    }

    public override string ToString()
    {
        string runningStatus = IsRunning ? "Running" : "Not running";
        string connectionStatus = IsConnected ? "Connected" : "Not connected";
        return $"Game: {runningStatus}, Server: {connectionStatus} ({Host}:{Port}), State: {State}";
    }
}

public class WaitResult
{
    public WaitOutcome Outcome { get; set; }

    /// <summary>"" when reached; otherwise timeout, stalled, unreachable, game_exited or plugin_lost.</summary>
    public string ErrorCode { get; set; } = "";
    public string Reason { get; set; } = "";
    public GameStatus Status { get; set; } = new();
    public TimeSpan Elapsed { get; set; }
    public TimeSpan Unchanged { get; set; }
    public List<WaitHeartbeat> Heartbeats { get; set; } = new();
}

public class JoinResult
{
    public bool Ok { get; set; }
    public string Server { get; set; } = "";
    public string Message { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public List<CommandResult> Steps { get; set; } = new();
    public GameStatus? FinalStatus { get; set; }
}
