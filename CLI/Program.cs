using valheim_cli.Testing;

namespace valheim_cli;

class Program
{
    private static int _port = ConnectionDefaults.Port;
    private static string _host = ConnectionDefaults.Host;
    private static bool _verbose = false;
    private static string? _testFile = null;
    private static bool _launch = false;
    private static bool _stopAfter = false;
    private static bool _status = false;
    private static string? _gamePath = null;
    private static string? _connect = null;
    private static string? _password = null;
    private static string? _passwordFile = null;
    private static bool _json = false;
    private static TimeSpan _timeout = TimeSpan.FromSeconds(120);
    private static TimeSpan _interval = TimeSpan.FromSeconds(2);
    private static string? _artifactsDir = null;
    private static Dictionary<string, string> _variables = new();

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--help")
        {
            PrintHelp();
            return 0;
        }

        // Parse arguments
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "-p" || args[i] == "--port") && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out int port))
                {
                    _port = port;
                }
                i++; // Skip next arg
            }
            else if ((args[i] == "-t" || args[i] == "--test") && i + 1 < args.Length)
            {
                _testFile = args[i + 1];
                i++; // Skip next arg
            }
            else if (args[i] == "-v" || args[i] == "--verbose")
            {
                _verbose = true;
            }
            else if (args[i] == "--launch" || args[i] == "-l")
            {
                _launch = true;
            }
            else if (args[i] == "--stop-after")
            {
                _stopAfter = true;
            }
            else if (args[i] == "--status")
            {
                _status = true;
            }
            else if (args[i] == "--game-path" && i + 1 < args.Length)
            {
                _gamePath = args[i + 1];
                i++; // Skip next arg
            }
            else if (args[i] == "--connect" && i + 1 < args.Length)
            {
                _connect = args[i + 1];
                i++;
            }
            else if (args[i] == "--password" && i + 1 < args.Length)
            {
                _password = args[i + 1];
                i++;
            }
            else if (args[i] == "--password-file" && i + 1 < args.Length)
            {
                _passwordFile = args[i + 1];
                i++;
            }
            else if (args[i] == "--json")
            {
                _json = true;
            }
            else if (args[i] == "--timeout" && i + 1 < args.Length)
            {
                _timeout = ParseDuration(args[i + 1], TimeSpan.FromSeconds(120));
                i++;
            }
            else if (args[i] == "--interval" && i + 1 < args.Length)
            {
                _interval = ParseDuration(args[i + 1], TimeSpan.FromSeconds(2));
                i++;
            }
            else if (args[i] == "--artifacts" && i + 1 < args.Length)
            {
                _artifactsDir = args[i + 1];
                i++;
            }
            else if (args[i] == "--var" && i + 1 < args.Length)
            {
                string varArg = args[i + 1];
                int eqIndex = varArg.IndexOf('=');
                if (eqIndex > 0)
                {
                    string key = varArg.Substring(0, eqIndex);
                    string value = varArg.Substring(eqIndex + 1);
                    _variables[key] = value;
                }
                i++; // Skip next arg
            }
        }

        // Status mode - check game status
        if (_status)
        {
            return ShowStatus();
        }

        // Test mode
        if (_testFile != null)
        {
            return RunTestMode(_testFile).GetAwaiter().GetResult();
        }

        // Check if command was provided as argument (exclude flags and their values)
        List<string> commandArgs = new();
        bool inCommand = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (inCommand)
            {
                if (args[i] == "--json")
                {
                    continue;
                }

                if (args[i] == "--timeout" || args[i] == "--interval" || args[i] == "--artifacts")
                {
                    i++;
                    continue;
                }

                commandArgs.Add(args[i]);
                continue;
            }

            // Flags with values
            if (args[i] == "-p" || args[i] == "--port" ||
                args[i] == "-t" || args[i] == "--test" ||
                args[i] == "--game-path" || args[i] == "--var" ||
                args[i] == "--connect" || args[i] == "--password" ||
                args[i] == "--password-file" || args[i] == "--timeout" ||
                args[i] == "--interval" || args[i] == "--artifacts")
            {
                i++; // Skip the value too
                continue;
            }
            // Boolean flags
            if (args[i] == "-v" || args[i] == "--verbose" || args[i] == "--launch" || args[i] == "-l" ||
                args[i] == "--stop-after" || args[i] == "--status" || args[i] == "--json")
            {
                continue;
            }
            if (!args[i].StartsWith("-"))
            {
                inCommand = true;
                commandArgs.Add(args[i]);
            }
        }

        if (commandArgs.Count > 0)
        {
            string topLevelCommand = commandArgs[0].ToLowerInvariant();
            if (topLevelCommand == "wait")
            {
                return RunWaitCommand(commandArgs).GetAwaiter().GetResult();
            }

            if (topLevelCommand == "join")
            {
                return RunJoinCommand(commandArgs).GetAwaiter().GetResult();
            }

            if (topLevelCommand == "commands")
            {
                return RunCommandsCommand(commandArgs);
            }

            if (topLevelCommand == "help" && commandArgs.Count > 1)
            {
                return RunCommandHelp(commandArgs[1]);
            }

            // Single command mode
            string command = string.Join(" ", commandArgs);
            return ExecuteSingleCommand(command);
        }

        if (_launch)
        {
            return RunLaunchMode().GetAwaiter().GetResult();
        }

        // Interactive mode
        return InteractiveMode();
    }

    static int ShowStatus()
    {
        GameLauncher launcher = new GameLauncher(_gamePath, _host, _port, _connect, _password);
        GameStatus status = launcher.GetStatus();

        if (_json)
        {
            JsonOutput.Write(new
            {
                ok = status.IsConnected,
                command = "status",
                state = status.State,
                message = status.IsConnected ? "Connected to CLI server." : "CLI server is not connected.",
                errorCode = status.IsConnected ? "" : status.DiagnosticCode,
                gamePath = status.GamePath,
                host = status.Host,
                port = status.Port,
                readiness = new
                {
                    process = status.ProcessReady,
                    pluginServer = status.PluginServerReady,
                    terminal = status.TerminalReady,
                    mainMenu = status.MainMenuReady,
                    inWorld = status.InWorldReady,
                    localPlayer = status.LocalPlayerReady,
                    serverConnected = status.ServerConnected
                },
                connection = new
                {
                    status = status.ConnectionStatus,
                    server = status.ConnectedServer
                },
                load = new
                {
                    phase = status.LoadPhase,
                    locationsGenerated = status.LocationsGenerated,
                    locationProgress = status.LocationProgress,
                    estimatedLocationSeconds = status.EstimatedLocationSeconds,
                    locationCount = status.LocationCount,
                    activeAreaLoaded = status.ActiveAreaLoaded,
                    respawnWait = status.RespawnWait
                },
                diagnostics = new
                {
                    code = status.DiagnosticCode,
                    message = status.DiagnosticMessage,
                    pluginLog = status.PluginLog
                }
            });
            return status.IsConnected ? 0 : (int)CliExitCode.ConnectionFailure;
        }

        WriteCompactStatus(status);

        return status.IsConnected ? 0 : 1;
    }

    static void WriteCompactStatus(GameStatus status)
    {
        string diagnosticCode = status.IsConnected ? "ready" : status.DiagnosticCode;
        string connectionStatus = string.IsNullOrWhiteSpace(status.ConnectionStatus) ? "none" : status.ConnectionStatus;
        string connectedServer = string.IsNullOrWhiteSpace(status.ConnectedServer) ? "none" : status.ConnectedServer;

        Console.WriteLine($"valheim-cli status ok={ToBool(status.IsConnected)} code={diagnosticCode}");
        Console.WriteLine(
            "readiness " +
            $"process={ToBool(status.ProcessReady)} " +
            $"plugin={ToBool(status.PluginServerReady)} " +
            $"terminal={ToBool(status.TerminalReady)} " +
            $"mainMenu={ToBool(status.MainMenuReady)} " +
            $"inWorld={ToBool(status.InWorldReady)} " +
            $"localPlayer={ToBool(status.LocalPlayerReady)} " +
            $"serverConnected={ToBool(status.ServerConnected)}");
        Console.WriteLine(
            "context " +
            $"game={FormatState(status.IsRunning ? "running" : "not_running")} " +
            $"state={FormatState(status.State)} " +
            $"phase={FormatState(status.LoadPhase)} " +
            $"cli={status.Host}:{status.Port} " +
            $"connection={FormatState(connectionStatus)} " +
            $"server={FormatState(connectedServer)}");
        if (!string.IsNullOrWhiteSpace(status.LoadPhase))
        {
            Console.WriteLine(
                "load " +
                $"locationsGenerated={ToBool(status.LocationsGenerated)} " +
                $"locationProgress={status.LocationProgress.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"estimatedLocationSeconds={status.EstimatedLocationSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"locationCount={status.LocationCount} " +
                $"activeAreaLoaded={ToBool(status.ActiveAreaLoaded)} " +
                $"respawnWait={status.RespawnWait.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
        }
        Console.WriteLine($"diagnostic {diagnosticCode}: {status.DiagnosticMessage}");
        Console.WriteLine($"next {NextStatusAction(status)}");
        Console.WriteLine($"path {status.GamePath}");
    }

    static string NextStatusAction(GameStatus status)
    {
        if (status.ServerConnected)
        {
            return "Client is in-world and connected; run Valheim commands or validation steps.";
        }

        if (status.InWorldReady)
        {
            return "Client is in-world; run commands or join a server when needed.";
        }

        if (status.MainMenuReady)
        {
            return "Main menu is ready; run join --server HOST:2456 or cli_connect_direct.";
        }

        if (status.LoadPhase.Equals("generating_locations", StringComparison.OrdinalIgnoreCase))
        {
            return "World location generation is still running; wait for locationProgress to reach 1.000 before expecting a local player.";
        }

        if (status.LoadPhase.Equals("loading_active_area", StringComparison.OrdinalIgnoreCase))
        {
            return "World generation is done, but the active area is still loading; wait before running player commands.";
        }

        if (status.LoadPhase.Equals("respawning", StringComparison.OrdinalIgnoreCase))
        {
            return "World is loaded and the client is waiting for player respawn.";
        }

        if (status.IsConnected)
        {
            return "CLI server is reachable; wait for main-menu or run terminal-safe commands.";
        }

        switch (status.DiagnosticCode)
        {
            case "game_not_running":
                return "Start Valheim with the desired profile, then rerun valheim-cli --status.";
            case "wrong_port":
                return status.PluginLog.Port.HasValue
                    ? $"Rerun with -p {status.PluginLog.Port.Value} or update the plugin port config."
                    : "Check the valheimCLI port in the BepInEx log and rerun with -p PORT.";
            case "plugin_server_not_listening":
                return "Wait for startup to finish; if this persists, inspect the BepInEx log.";
            case "plugin_missing_or_not_loaded":
                return status.IsRunning
                    ? "Wait for startup to finish; if this persists, verify valheimCLI.dll is in the active BepInEx profile."
                    : "Install valheimCLI.dll in the active BepInEx profile and restart Valheim.";
            case "bepinex_log_missing":
                return "Check --game-path or launch Valheim with BepInEx enabled.";
            default:
                return "Wait for readiness or inspect the Valheim/BepInEx log.";
        }
    }

    static string ToBool(bool value)
    {
        return value ? "true" : "false";
    }

    static string FormatState(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "none" : value.Replace(' ', '_');
    }

    static async Task<int> RunTestMode(string testFile)
    {
        // Support glob patterns
        List<string> testFiles = new();

        if (testFile.Contains('*'))
        {
            string directory = Path.GetDirectoryName(testFile) ?? ".";
            string pattern = Path.GetFileName(testFile);
            testFiles.AddRange(Directory.GetFiles(directory, pattern));
        }
        else if (File.Exists(testFile))
        {
            testFiles.Add(testFile);
        }
        else
        {
            Console.Error.WriteLine($"Test file not found: {testFile}");
            return 1;
        }

        if (testFiles.Count == 0)
        {
            Console.Error.WriteLine($"No test files found matching: {testFile}");
            return 1;
        }

        Console.WriteLine("Valheim CLI Test Runner");
        Console.WriteLine("=======================");

        // Create game launcher
        GameLauncher launcher = new GameLauncher(_gamePath, _host, _port, _connect, _password);

        // Create runner with options (CLI flags will override YAML settings)
        TestRunnerOptions options = new TestRunnerOptions
        {
            Verbose = _verbose,
            Launch = _launch ? true : null,      // Only set if flag was provided
            StopAfter = _stopAfter ? true : null, // Only set if flag was provided
            Variables = _variables,
            ArtifactsDirectory = _artifactsDir,
            Json = _json
        };

        TestRunner runner = new TestRunner(launcher, options, _host, _port);

        int totalFailed = 0;
        List<TestPlanResult> results = new();
        foreach (string file in testFiles)
        {
            TestPlanResult result = await runner.RunTestFileAsync(file);
            results.Add(result);
            totalFailed += result.Failed + result.Errors;
        }

        if (_json)
        {
            JsonOutput.Write(new
            {
                ok = totalFailed == 0,
                command = "test",
                state = "",
                message = totalFailed == 0 ? "All test plans passed." : "One or more test plans failed.",
                errorCode = totalFailed == 0 ? "" : "test_failed",
                results
            });
        }

        return totalFailed > 0 ? 1 : 0;
    }

    static async Task<int> RunLaunchMode()
    {
        GameLauncher launcher = new GameLauncher(_gamePath, _host, _port, _connect, ReadPassword());
        List<LaunchPhase> phases = new();

        if (!launcher.IsGameRunning())
        {
            WriteHumanPhase($"Launching Valheim from: {launcher.GamePath}");
            bool started = launcher.LaunchGame();
            phases.Add(new LaunchPhase
            {
                Name = "process-started",
                Ok = started,
                Message = started ? "Launch command started Valheim." : "Launch command failed."
            });

            if (!started)
            {
                return FinishLaunch(phases, null, "process-started", "launch_failed", "Failed to start Valheim.");
            }
        }
        else
        {
            phases.Add(new LaunchPhase
            {
                Name = "process-started",
                Ok = true,
                Message = "Valheim process was already running."
            });
        }

        GameStatus processStatus = await launcher.WaitForTargetAsync(WaitTarget.Process, _timeout, _interval);
        phases.Add(new LaunchPhase
        {
            Name = "process-ready",
            Ok = processStatus.ProcessReady,
            Message = processStatus.ProcessReady ? "Valheim process is running." : processStatus.DiagnosticMessage
        });
        if (!processStatus.ProcessReady)
        {
            return FinishLaunch(phases, processStatus, "process-ready", "timeout", "Valheim process did not become visible before timeout.");
        }

        bool steamRunning = launcher.IsSteamRunning();
        phases.Add(new LaunchPhase
        {
            Name = "steam",
            Ok = steamRunning,
            Required = false,
            Message = steamRunning ? "Steam process observed." : "Steam process was not observed; continuing because Valheim is running."
        });

        GameStatus pluginStatus = await launcher.WaitForTargetAsync(WaitTarget.PluginServer, _timeout, _interval);
        bool pluginLoaded = pluginStatus.PluginLog.PluginLoaded || pluginStatus.PluginServerReady;
        phases.Add(new LaunchPhase
        {
            Name = "bepinex",
            Ok = pluginStatus.PluginLog.Exists || pluginStatus.PluginServerReady,
            Message = pluginStatus.PluginLog.Exists ? $"BepInEx log found at {pluginStatus.PluginLog.Path}." : pluginStatus.DiagnosticMessage
        });
        phases.Add(new LaunchPhase
        {
            Name = "plugin-loaded",
            Ok = pluginLoaded,
            Message = pluginLoaded ? "valheimCLI plugin loaded." : pluginStatus.DiagnosticMessage
        });
        phases.Add(new LaunchPhase
        {
            Name = "cli-server-listening",
            Ok = pluginStatus.PluginServerReady,
            Message = pluginStatus.PluginServerReady ? $"CLI server is listening at {_host}:{_port}." : pluginStatus.DiagnosticMessage
        });
        if (!pluginStatus.PluginServerReady)
        {
            string errorCode = pluginStatus.WaitTimedOut ? "timeout" : pluginStatus.DiagnosticCode;
            return FinishLaunch(phases, pluginStatus, "cli-server-listening", errorCode, "CLI server did not become reachable.");
        }

        GameStatus terminalStatus = await launcher.WaitForTargetAsync(WaitTarget.Terminal, _timeout, _interval);
        phases.Add(new LaunchPhase
        {
            Name = "terminal-ready",
            Ok = terminalStatus.TerminalReady,
            Message = terminalStatus.TerminalReady ? "Terminal commands are available." : terminalStatus.DiagnosticMessage
        });
        if (!terminalStatus.TerminalReady)
        {
            return FinishLaunch(phases, terminalStatus, "terminal-ready", "game_not_ready", "Terminal did not become ready.");
        }

        if (launcher.HasServerConnect)
        {
            if (!launcher.QueueServerConnect(out List<string> output))
            {
                if (!_json)
                {
                    foreach (string line in output)
                    {
                        Console.Error.WriteLine(line);
                    }
                }

                phases.Add(new LaunchPhase
                {
                    Name = "server-join-queued",
                    Ok = false,
                    Message = "Server join command failed."
                });
                return FinishLaunch(phases, terminalStatus, "server-join-queued", "command_failed", "Server join command failed.");
            }

            foreach (string line in output)
            {
                WriteHumanPhase(line);
            }

            phases.Add(new LaunchPhase
            {
                Name = "server-join-queued",
                Ok = true,
                Message = "Dedicated server join command was accepted."
            });

            using ValheimClient client = new ValheimClient(_host, _port) { CommandTimeout = _timeout };
            if (!client.Connect())
            {
                phases.Add(new LaunchPhase
                {
                    Name = "in-world",
                    Ok = false,
                    Message = "CLI server disconnected before InWorld wait could start."
                });
                return FinishLaunch(phases, launcher.GetStatus(), "in-world", "connection_failed", "CLI server disconnected before InWorld wait could start.");
            }

            WriteHumanPhase("Waiting for InWorld...");
            bool inWorld = await client.WaitForStateAsync("InWorld", _timeout, interval: _interval);
            GameStatus finalStatus = launcher.GetStatus();
            phases.Add(new LaunchPhase
            {
                Name = "in-world",
                Ok = inWorld,
                Message = inWorld ? "Connected and in world." : "Server join was queued, but InWorld was not reached before timeout."
            });
            if (!inWorld)
            {
                return FinishLaunch(phases, finalStatus, "in-world", "timeout", "Server join was queued, but InWorld was not reached before timeout.");
            }

            return FinishLaunch(phases, finalStatus, "", "", "Launch completed.");
        }

        return FinishLaunch(phases, terminalStatus, "", "", "Launch completed.");
    }

    private static int FinishLaunch(List<LaunchPhase> phases, GameStatus? status, string failurePhase, string errorCode, string message)
    {
        bool ok = string.IsNullOrWhiteSpace(errorCode);
        if (_json)
        {
            JsonOutput.Write(new
            {
                ok,
                command = "launch",
                state = status?.State ?? "",
                message,
                errorCode,
                failurePhase,
                phases,
                status
            });
            return ok ? 0 : (errorCode == "timeout" ? (int)CliExitCode.Timeout : (int)CliExitCode.CommandFailure);
        }

        foreach (LaunchPhase phase in phases)
        {
            string marker = phase.Ok ? "OK" : (phase.Required ? "FAIL" : "INFO");
            Console.WriteLine($"{marker}: {phase.Name}: {phase.Message}");
        }

        if (!ok)
        {
            Console.Error.WriteLine($"ERROR: {message} phase={failurePhase} code={errorCode}");
        }

        return ok ? 0 : 1;
    }

    private static void WriteHumanPhase(string message)
    {
        if (!_json)
        {
            Console.WriteLine(message);
        }
    }

    static void PrintHelp()
    {
        Console.WriteLine("Valheim CLI - Remote console for Valheim");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  valheim-cli [options] [command]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -p, --port <port>     Port to connect to (default: 5555)");
        Console.WriteLine("  -t, --test <file>     Run a YAML test file");
        Console.WriteLine("  -v, --verbose         Verbose output (for test mode)");
        Console.WriteLine("  -l, --launch          Launch Valheim before running tests");
        Console.WriteLine("  --stop-after          Stop game after tests (only if all pass)");
        Console.WriteLine("  --status              Check game and connection status");
        Console.WriteLine("  --game-path <path>    Path to Valheim installation");
        Console.WriteLine("  --connect <addr>      Auto-connect to server on launch (e.g. 178.156.172.16:2457)");
        Console.WriteLine("  --password <pass>     Server password for auto-connect");
        Console.WriteLine("  --password-file <path> Read server password from a file");
        Console.WriteLine("  --json                Print machine-readable JSON for supported commands");
        Console.WriteLine("  --timeout <duration>  Timeout for wait/join operations and for a command to complete in-game (default 120s), e.g. 120s or 3m");
        Console.WriteLine("  --interval <duration> Poll interval for wait/join operations");
        Console.WriteLine("  --artifacts <dir>     Test-run artifact directory");
        Console.WriteLine("  --var <key=value>     Set a test variable (can be used multiple times)");
        Console.WriteLine("  --help                Show this help");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  valheim-cli                              Interactive mode");
        Console.WriteLine("  valheim-cli help                         Run 'help' command");
        Console.WriteLine("  valheim-cli spawn Boar 5                 Run 'spawn Boar 5' command");
        Console.WriteLine("  valheim-cli -p 5556 pos                  Connect to port 5556, run 'pos'");
        Console.WriteLine("  valheim-cli --test tests/spawn.yaml      Run test file");
        Console.WriteLine("  valheim-cli -t tests/*.yaml -v           Run all tests verbosely");
        Console.WriteLine("  valheim-cli --status                     Check if game is running");
        Console.WriteLine("  valheim-cli -t tests/spawn.yaml --launch Launch game, then run tests");
        Console.WriteLine("  valheim-cli -t tests/road.yaml --var n=1 Pass variable to test");
        Console.WriteLine("  valheim-cli -t tests/spawn.yaml --launch --stop-after");
        Console.WriteLine("                                           Launch, test, stop if passed");
        Console.WriteLine("  valheim-cli --launch --connect 178.156.172.16:2457 --password mypass");
        Console.WriteLine("                                           Launch and auto-join server");
        Console.WriteLine("  valheim-cli wait --for terminal --timeout 120s");
        Console.WriteLine("  valheim-cli join --server 127.0.0.1:2456 --password-file ./password.txt --character Test");
        Console.WriteLine("  valheim-cli commands --group cli --json");
        Console.WriteLine();
        Console.WriteLine("Interactive commands:");
        Console.WriteLine("  exit, quit         Exit the CLI");
        Console.WriteLine("  commands           List all available Valheim commands");
        Console.WriteLine("  help               Show Valheim console help");
        Console.WriteLine();
        Console.WriteLine("Test File Format (YAML):");
        Console.WriteLine("  See documentation for full test file schema including");
        Console.WriteLine("  game: section (launch, launchTimeout, stopAfter),");
        Console.WriteLine("  waitFor conditions, expect assertions, and variables.");
        Console.WriteLine();
        Console.WriteLine("Game Path Detection (priority order):");
        Console.WriteLine("  1. --game-path argument");
        Console.WriteLine("  2. VALHEIM_PATH environment variable");
        Console.WriteLine("  3. Default: ~/Library/Application Support/Steam/steamapps/common/Valheim/");
    }

    static int ExecuteSingleCommand(string command)
    {
        try
        {
            using ValheimClient client = new ValheimClient(_host, _port) { CommandTimeout = _timeout };
            if (!client.Connect())
            {
                Console.Error.WriteLine($"Cannot connect to Valheim at {_host}:{_port}");
                Console.Error.WriteLine("Make sure Valheim is running with the valheimCLI mod loaded.");
                return 1;
            }

            CommandResult result = client.ExecuteCommand(command);
            if (_json)
            {
                JsonOutput.Write(new
                {
                    ok = result.Ok,
                    command = result.Command,
                    state = client.GetState(),
                    message = result.Message,
                    errorCode = result.ErrorCode,
                    output = result.Output
                });
                return result.Ok ? 0 : (int)CliExitCode.CommandFailure;
            }

            foreach (string line in result.Output)
            {
                Console.WriteLine(line);
            }
            return result.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            if (_json)
            {
                JsonOutput.Write(new CliResponse
                {
                    Ok = false,
                    Command = command,
                    Message = ex.Message,
                    ErrorCode = "unexpected_exception"
                });
                return (int)CliExitCode.CommandFailure;
            }

            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    static async Task<int> RunWaitCommand(List<string> args)
    {
        string targetRaw = GetOption(args, "--for") ?? (args.Count > 1 ? args[1] : "terminal");
        if (!WaitTargets.TryParse(targetRaw, out WaitTarget target))
        {
            return PrintBadInput($"Unknown wait target: {targetRaw}");
        }

        GameLauncher launcher = new GameLauncher(_gamePath, _host, _port, _connect, ReadPassword());
        GameStatus status = await launcher.WaitForTargetAsync(target, _timeout, _interval);
        bool ok = status.Satisfies(target);
        string targetName = WaitTargets.ToName(target);

        if (_json)
        {
            JsonOutput.Write(new
            {
                ok,
                command = "wait",
                state = status.State,
                message = ok ? $"Reached {targetName}." : $"Timed out waiting for {targetName}.",
                errorCode = ok ? "" : "timeout",
                target = targetName,
                timeoutSeconds = _timeout.TotalSeconds,
                status
            });
            return ok ? 0 : (int)CliExitCode.Timeout;
        }

        Console.WriteLine(ok
            ? $"OK: reached {targetName}; state={status.State}; connectionStatus={status.ConnectionStatus}"
            : $"TIMEOUT: waiting for {targetName}; state={status.State}; diagnostic={status.DiagnosticCode}");
        return ok ? 0 : (int)CliExitCode.Timeout;
    }

    static async Task<int> RunJoinCommand(List<string> args)
    {
        string? server = GetOption(args, "--server");
        if (string.IsNullOrWhiteSpace(server))
        {
            return PrintBadInput("join requires --server <host:port>");
        }

        string? character = GetOption(args, "--character");
        bool createCharacter = args.Any(arg => arg.Equals("--create-character", StringComparison.OrdinalIgnoreCase));
        GameLauncher launcher = new GameLauncher(_gamePath, _host, _port, _connect, ReadPassword());
        JoinResult result = await launcher.JoinDirectAsync(server, ReadPassword(), character, createCharacter, _timeout, _interval);

        if (_json)
        {
            JsonOutput.Write(new
            {
                ok = result.Ok,
                command = "join",
                state = result.FinalStatus?.State ?? "",
                message = result.Message,
                errorCode = result.ErrorCode,
                server = result.Server,
                steps = result.Steps,
                status = result.FinalStatus
            });
            return result.Ok ? 0 : (int)CliExitCode.CommandFailure;
        }

        Console.WriteLine(result.Ok ? $"OK: {result.Message}" : $"ERROR: {result.Message}");
        foreach (CommandResult step in result.Steps)
        {
            foreach (string line in step.Output)
            {
                Console.WriteLine(line);
            }
        }
        return result.Ok ? 0 : (int)CliExitCode.CommandFailure;
    }

    static int RunCommandsCommand(List<string> args)
    {
        string? group = GetOption(args, "--group");
        string? search = GetOption(args, "--search");
        using ValheimClient client = new ValheimClient(_host, _port) { CommandTimeout = _timeout };
        if (!client.Connect())
        {
            return PrintConnectionFailure();
        }

        List<CommandInfo> commands = client.ListCommands()
            .Where(command => string.IsNullOrWhiteSpace(group) ||
                              CommandMetadata.GetGroup(command).Equals(group, StringComparison.OrdinalIgnoreCase))
            .Where(command => string.IsNullOrWhiteSpace(search) ||
                              command.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                              command.Description.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(command => command.Name)
            .ToList();

        if (_json)
        {
            JsonOutput.Write(new
            {
                ok = true,
                command = "commands",
                state = client.GetState(),
                message = $"Found {commands.Count} command(s).",
                errorCode = "",
                commands = commands.Select(CommandMetadata.ToJson)
            });
            return 0;
        }

        Console.WriteLine($"Available commands ({commands.Count}):");
        foreach (CommandInfo command in commands)
        {
            Console.WriteLine($"  {command.Name,-28} [{CommandMetadata.GetGroup(command)}] requires={CommandMetadata.GetPrecondition(command)} {command.Description}");
        }
        return 0;
    }

    static int RunCommandHelp(string commandName)
    {
        using ValheimClient client = new ValheimClient(_host, _port) { CommandTimeout = _timeout };
        if (!client.Connect())
        {
            return PrintConnectionFailure();
        }

        List<CommandInfo> commands = client.ListCommands();
        CommandInfo? command = commands.FirstOrDefault(item => item.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase));
        if (command == null)
        {
            List<string> suggestions = commands
                .Select(item => new
                {
                    item.Name,
                    Distance = GetEditDistance(commandName.ToLowerInvariant(), item.Name.ToLowerInvariant())
                })
                .Where(item => item.Distance <= Math.Max(2, commandName.Length / 3))
                .OrderBy(item => item.Distance)
                .ThenBy(item => item.Name)
                .Select(item => item.Name)
                .Take(5)
                .ToList();

            if (_json)
            {
                JsonOutput.Write(new
                {
                    ok = false,
                    command = "help",
                    state = client.GetState(),
                    message = $"Unknown command: {commandName}",
                    errorCode = "unknown_command",
                    suggestions
                });
            }
            else
            {
                Console.Error.WriteLine($"Unknown command: {commandName}");
                if (suggestions.Count > 0)
                {
                    Console.Error.WriteLine($"Suggestions: {string.Join(", ", suggestions)}");
                }
            }

            return (int)CliExitCode.BadInput;
        }

        if (_json)
        {
            JsonOutput.Write(new
            {
                ok = true,
                command = "help",
                state = client.GetState(),
                message = command.Description,
                errorCode = "",
                info = CommandMetadata.ToJson(command)
            });
            return 0;
        }

        Console.WriteLine(command.Name);
        Console.WriteLine($"  {command.Description}");
        Console.WriteLine($"  Group: {CommandMetadata.GetGroup(command)}");
        Console.WriteLine($"  Requires: {CommandMetadata.GetPrecondition(command)}");
        Console.WriteLine($"  Usage: {CommandMetadata.ExtractUsage(command)}");
        return 0;
    }

    private static int GetEditDistance(string left, string right)
    {
        int[,] distance = new int[left.Length + 1, right.Length + 1];
        for (int i = 0; i <= left.Length; i++)
        {
            distance[i, 0] = i;
        }

        for (int j = 0; j <= right.Length; j++)
        {
            distance[0, j] = j;
        }

        for (int i = 1; i <= left.Length; i++)
        {
            for (int j = 1; j <= right.Length; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                distance[i, j] = Math.Min(
                    Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                    distance[i - 1, j - 1] + cost);
            }
        }

        return distance[left.Length, right.Length];
    }

    private static string? ReadPassword()
    {
        if (!string.IsNullOrWhiteSpace(_passwordFile))
        {
            return File.ReadAllText(_passwordFile).Trim();
        }

        return _password;
    }

    private static string? GetOption(List<string> args, string option)
    {
        int index = args.FindIndex(arg => arg.Equals(option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    private static TimeSpan ParseDuration(string raw, TimeSpan fallback)
    {
        TimeSpan parsed = TestSettings.ParseDuration(raw);
        return parsed <= TimeSpan.Zero ? fallback : parsed;
    }

    private static int PrintBadInput(string message)
    {
        if (_json)
        {
            JsonOutput.Write(new CliResponse
            {
                Ok = false,
                Command = "input",
                Message = message,
                ErrorCode = "bad_input"
            });
        }
        else
        {
            Console.Error.WriteLine($"ERROR: {message}");
        }

        return (int)CliExitCode.BadInput;
    }

    private static int PrintConnectionFailure()
    {
        if (_json)
        {
            JsonOutput.Write(new CliResponse
            {
                Ok = false,
                Command = "connect",
                Message = $"Cannot connect to Valheim at {_host}:{_port}.",
                ErrorCode = "connection_failed"
            });
        }
        else
        {
            Console.Error.WriteLine($"Cannot connect to Valheim at {_host}:{_port}");
            Console.Error.WriteLine("Make sure Valheim is running with the valheimCLI mod loaded.");
        }

        return (int)CliExitCode.ConnectionFailure;
    }

    static int InteractiveMode()
    {
        Console.WriteLine($"Valheim CLI - Connecting to {_host}:{_port}");
        Console.WriteLine("Type 'exit' to quit, 'commands' to list available commands");
        Console.WriteLine();

        ValheimClient? client = null;

        while (true)
        {
            Console.Write("valheim> ");
            string? input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input))
                continue;

            // Remove any non-printable characters and trim (fixes Warp terminal issues)
            input = new string(input.Where(c => !char.IsControl(c) && c != '\u200B' && c != '\uFEFF').ToArray()).Trim();

            if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (input.Equals("reconnect", StringComparison.OrdinalIgnoreCase))
            {
                client?.Dispose();
                client = null;
                Console.WriteLine("Reconnecting...");
                continue;
            }

            try
            {
                // Connect if not connected
                if (client == null || !client.IsConnected)
                {
                    client?.Dispose();
                    client = new ValheimClient(_host, _port) { CommandTimeout = _timeout };
                    if (!client.Connect())
                    {
                        Console.WriteLine("Failed to connect. Is Valheim running with the mod?");
                        client.Dispose();
                        client = null;
                        continue;
                    }
                }

                if (input.Equals("commands", StringComparison.OrdinalIgnoreCase))
                {
                    List<CommandInfo> commands = client.ListCommands();
                    Console.WriteLine($"\nAvailable commands ({commands.Count}):\n");
                    foreach (CommandInfo cmd in commands.OrderBy(c => c.Name))
                    {
                        string cheatMarker = cmd.IsCheat ? " [CHEAT]" : "";
                        Console.WriteLine($"  {cmd.Name,-20} {cmd.Description}{cheatMarker}");
                    }
                    Console.WriteLine();
                }
                else
                {
                    List<string> response = client.SendCommand(input);
                    foreach (string line in response)
                    {
                        Console.WriteLine(line);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                client?.Dispose();
                client = null;
            }
        }

        client?.Dispose();
        return 0;
    }

}
