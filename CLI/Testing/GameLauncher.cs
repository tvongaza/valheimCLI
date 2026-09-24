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
    private Process? _gameProcess;

    public GameLauncher(string? gamePath = null, string host = ConnectionDefaults.Host, int port = ConnectionDefaults.Port, string? connect = null, string? password = null)
    {
        _gamePath = ResolveGamePath(gamePath);
        _host = host;
        _port = port;
        _connect = connect;
        _password = password;
    }

    public string GamePath => _gamePath;
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
    /// Wait for the game to be ready (TCP server accepting connections)
    /// </summary>
    public async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        DateTime deadline = DateTime.Now.Add(timeout);
        TimeSpan pollInterval = TimeSpan.FromSeconds(2);

        while (DateTime.Now < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (TryConnect())
            {
                return true;
            }

            await Task.Delay(pollInterval, cancellationToken);
        }

        return false;
    }

    public async Task<GameStatus> WaitForTargetAsync(
        WaitTarget target,
        TimeSpan timeout,
        TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        DateTime deadline = DateTime.Now.Add(timeout);
        GameStatus last = GetStatus();

        while (DateTime.Now < deadline && !cancellationToken.IsCancellationRequested)
        {
            last = GetStatus();
            if (last.Satisfies(target))
            {
                return last;
            }

            if (target == WaitTarget.ServerConnected && last.HasUnrecoverableConnectionFailure)
            {
                return last;
            }

            await Task.Delay(interval, cancellationToken);
        }

        last.WaitTimedOut = true;
        last.WaitTargetName = WaitTargets.ToName(target);
        return last;
    }

    public async Task<JoinResult> JoinDirectAsync(
        string server,
        string? password,
        string? character,
        bool createCharacter,
        TimeSpan timeout,
        TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        JoinResult result = new() { Server = server };
        GameStatus terminalStatus = await WaitForTargetAsync(WaitTarget.Terminal, timeout, interval, cancellationToken);
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
            GameStatus menuStatus = await WaitForTargetAsync(WaitTarget.MainMenu, timeout, interval, cancellationToken);
            if (!menuStatus.Satisfies(WaitTarget.MainMenu))
            {
                result.ErrorCode = menuStatus.WaitTimedOut ? "timeout" : menuStatus.DiagnosticCode;
                result.Message = "Main menu was not ready for character selection.";
                result.FinalStatus = menuStatus;
                return result;
            }

            string command = createCharacter
                ? $"cli_create_character {character} --local"
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

        GameStatus connectedStatus = await WaitForTargetAsync(WaitTarget.ServerConnected, timeout, interval, cancellationToken);
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
            using (client)
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

                client.TryGetConnectionStatus(out connectionStatus, out server);
                return new GameStatus
                {
                    IsRunning = GameStatus.RunningFrom(running, connected),
                    ProcessSeenLocally = running,
                    IsConnected = connected,
                    GamePath = _gamePath,
                    Host = _host,
                    Port = _port,
                    State = state,
                    LoadPhase = GetDetail(statusDetails, "phase"),
                    LocationsGenerated = GetBoolDetail(statusDetails, "locationsGenerated"),
                    LocationProgress = GetFloatDetail(statusDetails, "locationProgress"),
                    EstimatedLocationSeconds = GetFloatDetail(statusDetails, "estimatedLocationSeconds"),
                    LocationCount = GetIntDetail(statusDetails, "locationCount"),
                    ActiveAreaLoaded = GetBoolDetail(statusDetails, "activeAreaLoaded"),
                    RespawnWait = GetFloatDetail(statusDetails, "respawnWait"),
                    ConnectionStatus = connectionStatus,
                    ConnectedServer = server,
                    PluginLog = pluginLog
                };
            }
        }

        return new GameStatus
        {
            IsRunning = running,
            ProcessSeenLocally = running,
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

    public static bool RunningFrom(bool processSeenLocally, bool pluginAnswered) => processSeenLocally || pluginAnswered;

    public bool IsConnected { get; set; }
    public string GamePath { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string State { get; set; } = "Unknown";
    public string LoadPhase { get; set; } = "";
    public bool LocationsGenerated { get; set; }
    public float LocationProgress { get; set; }
    public float EstimatedLocationSeconds { get; set; }
    public int LocationCount { get; set; }
    public bool ActiveAreaLoaded { get; set; }
    public float RespawnWait { get; set; }
    public string ConnectionStatus { get; set; } = "";
    public string ConnectedServer { get; set; } = "";
    public bool WaitTimedOut { get; set; }
    public string WaitTargetName { get; set; } = "";
    public PluginLogInfo PluginLog { get; set; } = new();

    public bool ProcessReady => IsRunning;
    public bool PluginServerReady => IsConnected;
    public bool TerminalReady => IsConnected;
    public bool MainMenuReady => State.Equals("MainMenu", StringComparison.OrdinalIgnoreCase);
    public bool InWorldReady => State.Equals("InWorld", StringComparison.OrdinalIgnoreCase);
    public bool LocalPlayerReady => InWorldReady;
    public bool ServerConnected => InWorldReady && ConnectionStatus.Equals("Connected", StringComparison.OrdinalIgnoreCase);
    public bool HasUnrecoverableConnectionFailure => ConnectionStatus.Contains("ErrorVersion", StringComparison.OrdinalIgnoreCase) ||
                                                     ConnectionStatus.Contains("ErrorPassword", StringComparison.OrdinalIgnoreCase);
    public string DiagnosticCode
    {
        get
        {
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

public class JoinResult
{
    public bool Ok { get; set; }
    public string Server { get; set; } = "";
    public string Message { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public List<CommandResult> Steps { get; set; } = new();
    public GameStatus? FinalStatus { get; set; }
}
