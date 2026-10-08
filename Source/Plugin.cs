using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace valheimCLI
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class valheimCLIPlugin : BaseUnityPlugin
    {
        private const string ModName = "valheimCLI";
        internal const string ModVersion = "1.1.0";
        private const string Author = "valheimCLI";
        internal const string ModGUID = Author + "." + ModName;
        private static string ConfigFileName = ModGUID + ".cfg";
        private static string ConfigFileFullPath = BepInEx.Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;

        private readonly Harmony HarmonyInstance = new(ModGUID);
        public static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource(ModName);

        private CommandServer? _commandServer;
        private readonly MainThreadHeartbeat _heartbeat = new();
        private GameStateTracker? _stateTracker;
        private ConfigEntry<int>? _portConfig;
        private ConfigEntry<bool>? _enabledConfig;
        private ConfigEntry<bool>? _autoStartQueuedJoinConfig;
        private ConfigEntry<bool>? _allowOnServerClientsConfig;

        private readonly List<string> _capturedOutput = new();
        private bool _capturingOutput;

        public Extensions.ExtensionRegistry? Extensions { get; private set; }
        public Extensions.ConsoleModuleHost Modules { get; private set; } = null!;
        public bool AutoStartQueuedJoin => _autoStartQueuedJoinConfig?.Value == true;

        public void Awake()
        {
            ManifestCommands.RecordOwnLoad(DateTime.UtcNow);
            UnloadOtherInstances();
            Instance = this;
            _loadedUtc = DateTime.UtcNow;

            _enabledConfig = Config.Bind("Server", "Enabled", true, "Enable the command server");
            _portConfig = Config.Bind("Server", "Port", 5555, "Port for the command server (localhost only)");
            _autoStartQueuedJoinConfig = Config.Bind("ClientLaunch", "AutoStartQueuedJoin", true, "Automatically start the selected character when Valheim has a queued startup/server join.");
            _allowOnServerClientsConfig = Config.Bind("Server", "AllowOnServerClients", false, "Let valheimCLI's own cli_ commands run while this client is joined to a dedicated server. Valheim 1.0 refuses every cheat command on such a client, admin or not. For test stations: the server cannot see or stop it.");
            ClientCommandAccess.AllowOnServerClients = _allowOnServerClientsConfig.Value;
            ManifestCommands.FileConfig = Config.Bind("Expectations", "File", "", "A file of key=value lines naming the plugin builds (and optionally the world) this game must run; see docs/expectations.md. While it does not hold, every command sent through the CLI except the diagnostics (cli_manifest, cli_world, cli_expect) is refused. A relative path is relative to the BepInEx config folder. Empty = off.");
            ManifestCommands.StrictConfig = Config.Bind("Expectations", "Strict", false, "Also require the expectations file to name every loaded plugin (a plugin not listed is a mismatch; list it as name=any if its build does not matter) and, once a world is loaded, to name the world (world= or worlduid=; world=any accepts any).");
            ManifestCommands.UseConfig(Config);

            Assembly assembly = Assembly.GetExecutingAssembly();
            HarmonyInstance.PatchAll(assembly);

            // Which commands are ours is the difference our registration makes
            // to Terminal.commands -- the "cli_" prefix is not proof of
            // ownership, and AllowOnServerClients must not rescue another
            // plugin's command (see CliCommandValidity). Keep the OBJECTS, not
            // the names: the vanilla constructor does commands[name] = this, so
            // a name can be taken over by a plugin loading after us, and a name
            // someone else registered first is still ours once we replace it.
            Dictionary<string, object> beforeRegister = SnapshotCommands();
            List<KeyValuePair<string, Terminal.ConsoleCommand>> before = new(Terminal.commands);
            Extensions = new valheimCLI.Extensions.ExtensionRegistry(AsyncExecution.Gate, valheimCLI.Extensions.ExtensionHost.Precondition);
            valheimCLI.Extensions.ExtensionHost.Register(Extensions);
            Modules = new Extensions.ConsoleModuleHost(this, Extensions);
            ManifestCommands.Register();
            AccessCommands.Register();
            ReloadCommands.Register();
            foreach (string name in new[] { "cli_build", "cli_self_unload", "cli_await_plugin" })
                StandingExpectations.AllowWhileMismatched(name);
            List<object> registeredHere = CliCommandValidity.NewlyRegistered(beforeRegister, SnapshotCommands());
            CliCommandValidity.RecordOwnCommands(registeredHere);
            Log.LogInfo($"Registered {registeredHere.Count} valheimCLI commands");
            _ownCommands = LiveReload.Registered(before, Terminal.commands);

            // Initialize state tracker
            _stateTracker = new GameStateTracker(Log);

            if (_enabledConfig.Value)
            {
                _commandServer = new CommandServer(Log, _portConfig.Value);
                _commandServer.SetStateTracker(_stateTracker);
                _commandServer.SetHeartbeat(_heartbeat);
                _commandServer.Start();
            }

            SetupWatcher();
            Log.LogInfo($"{ModName} loaded. CLI server on port {_portConfig.Value}");
        }

        /// <summary>Terminal.commands as it stands now: each name against the object behind it.</summary>
        private static Dictionary<string, object> SnapshotCommands()
        {
            Dictionary<string, object> snapshot = new Dictionary<string, object>();
            foreach (KeyValuePair<string, Terminal.ConsoleCommand> entry in Terminal.commands)
            {
                snapshot[entry.Key] = entry.Value;
            }
            return snapshot;
        }

        /// <summary>
        /// Every plugin has loaded by the first frame: check the standing
        /// expectations once so a mismatch is in the log before any command.
        /// </summary>
        private void Start()
        {
            ManifestCommands.StandingProblems();
            ReloadCommands.RecordLoadedBuild(this, _loadedUtc);
        }

        private void Update()
        {
            _heartbeat.Stamp();
            _stateTracker?.Update();
            ProcessPendingCommands();
            Extensions?.Tick();
            Modules?.Tick();
        }

        /// <summary>Publish a bounded reason before intentionally blocking the game thread; STATUS reports it off-thread.</summary>
        public static void SetBusy(string reason) => Instance?._heartbeat.SetBusy(reason);

        /// <summary>Remove the reason once the long game-thread operation finishes.</summary>
        public static void ClearBusy() => Instance?._heartbeat.ClearBusy();

        private void ProcessPendingCommands()
        {
            if (_commandServer == null) return;

            while (_commandServer.TryGetPendingRequest(out RequestBroker.Request request))
            {
                string command = request.Text;
                RequestBroker broker = _commandServer.Broker;
                broker.CurrentRequestId = request.Id;
                try
                {
                    if (string.IsNullOrEmpty(command))
                    {
                        _commandServer.SendOutput("ERROR: code=empty_command message=Empty command.");
                        continue;
                    }
                    if (ManifestCommands.Refuse(command, line => _commandServer.SendOutput(line)))
                    {
                        continue;
                    }
                    Log.LogInfo($"Executing CLI command #{request.Id}: {command}");
                    if (!TryExecuteBuiltInCommand(command))
                    {
                        ExecuteCommand(command);
                    }
                }
                catch (Exception ex)
                {
                    _commandServer.SendOutput($"ERROR: code=unexpected_exception message={ex.Message}");
                    Log.LogError($"Command execution error: {ex}");
                }
                finally
                {
                    // An async handler (BeginAsync) completes its request itself,
                    // possibly already inside the handler.
                    broker.EndHandler(request.Id);
                    broker.CurrentRequestId = 0;
                }
            }
        }

        private bool TryExecuteBuiltInCommand(string command)
        {
            const string prefix = "cli_run_trusted";
            if (command.Equals(prefix, StringComparison.OrdinalIgnoreCase) || command.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
            {
                string inner = command.Substring(prefix.Length).Trim();
                if (inner.Length == 0) _commandServer?.SendOutput("Usage: cli_run_trusted <console command>");
                else ExecuteCommand(inner, skipAllowedCheck: true);
                return true;
            }
            return Modules.TryDispatch(command, line => _commandServer?.SendOutput(line));
        }

        /// <summary>
        /// A handler that keeps working after it returns (a coroutine) calls this
        /// while executing: its request stays open until the handle completes it,
        /// and output sent through the handle belongs to that request however
        /// late it arrives (until the request's timeout abandons it).
        /// </summary>
        public static AsyncHandle? BeginAsync()
        {
            CommandServer? server = Instance?._commandServer;
            if (server == null) return null;
            long id = server.Broker.CurrentRequestId;
            if (id == 0) return null;
            server.Broker.MarkAsync(id);
            return new AsyncHandle(server.Broker, id);
        }

        /// <summary>
        /// Run a console command now and return its AddString lines instead of
        /// sending them to the client (cli_until polls with this).
        /// </summary>
        public List<string> RunCapturing(string command)
        {
            List<string> lines = new List<string>();
            if (Console.instance == null)
            {
                lines.Add("Error: Console not available (game not fully loaded)");
                return lines;
            }
            _capturedOutput.Clear();
            _capturingOutput = true;
            try
            {
                Console.instance.TryRunCommand(command, silentFail: false, skipAllowedCheck: false);
                lines.AddRange(_capturedOutput);
            }
            finally
            {
                _capturingOutput = false;
                _capturedOutput.Clear();
            }
            return lines;
        }

        private void ExecuteCommand(string command, bool skipAllowedCheck = false)
        {
            if (Console.instance == null)
            {
                _commandServer?.SendOutput("Error: Console not available (game not fully loaded)");
                return;
            }

            _capturedOutput.Clear();
            _capturingOutput = true;

            try
            {
                // Execute the command; its AddString output is captured synchronously.
                // (An earlier 100 ms sleep here stalled the game thread once per command.)
                Console.instance.TryRunCommand(command, silentFail: false, skipAllowedCheck: skipAllowedCheck);

                // Send captured output or confirmation
                if (_capturedOutput.Count > 0)
                {
                    foreach (string line in _capturedOutput)
                    {
                        _commandServer?.SendOutput(line);
                    }
                }
                else if (_commandServer == null || !_commandServer.Broker.BegunAsync(_commandServer.Broker.CurrentRequestId))
                {
                    // An async command answers through its handle (later, or already
                    // inside its handler); nothing to confirm here.
                    _commandServer?.SendOutput($"Executed: {command}");
                }
            }
            finally
            {
                _capturingOutput = false;
            }
        }

        public void CaptureOutput(string text)
        {
            if (_capturingOutput)
            {
                _capturedOutput.Add(text);
            }
        }

        public static valheimCLIPlugin? Instance { get; private set; }

        private void OnEnable()
        {
            Instance = this;
        }

        /// <summary>
        /// The newest instance wins. A copy in BepInEx/plugins (chainloader) and
        /// one in BepInEx/scripts (ScriptEngine) both load when both files are
        /// there; the older would keep the command port while the newer one's
        /// commands answer, and every async reply would go to a socket nobody
        /// reads. Any other live instance with this GUID is destroyed before
        /// this one patches, registers commands or opens the port. Both loaders
        /// hide the objects that carry plugins, so the search includes hidden
        /// objects. Patches still registered under this Harmony id (a build
        /// without the clean unload below) are removed as well.
        /// </summary>
        private void UnloadOtherInstances()
        {
            foreach (BaseUnityPlugin other in UnityEngine.Resources.FindObjectsOfTypeAll<BaseUnityPlugin>())
            {
                if (other == null || ReferenceEquals(other, this)) continue;
                BepInPlugin? meta = MetadataHelper.GetMetadata(other);
                if (meta == null || meta.GUID != ModGUID) continue;
                Log.LogWarning($"Another {ModName} instance is loaded ({other.GetType().Assembly.GetName().Name}); unloading it: the newest wins");
                try { DestroyImmediate(other); }
                catch (Exception ex) { Log.LogError($"Unloading the other {ModName} instance failed: {ex}"); }
            }
            Harmony.UnpatchID(ModGUID);
        }

        /// <summary>
        /// Leaves nothing of this instance running, so a live reload (the next
        /// build loads as a separate assembly next to this one) or
        /// cli_self_unload is clean: the port and its threads, the config
        /// watcher, the Harmony patches, the console commands this instance
        /// registered (unless a newer instance has replaced them), the log sources.
        /// UnpatchSelf removes every patch under this Harmony id; both loaders
        /// destroy the old instance before the new one patches, so it never
        /// takes the new instance's patches.
        /// </summary>
        private void OnDestroy()
        {
            Modules?.Dispose();
            Extensions?.Dispose();
            _commandServer?.Dispose();
            _commandServer = null;
            _configWatcher?.Dispose();
            _configWatcher = null;
            HarmonyInstance.UnpatchSelf();
            LiveReload.RemoveOwned(Terminal.commands, _ownCommands);
            Config.Save();
            if (ReferenceEquals(Instance, this))
                Instance = null;
            BepInEx.Logging.Logger.Sources.Remove(Log);
            BepInEx.Logging.Logger.Sources.Remove(Logger);
        }

        private DateTime _loadedUtc;
        private List<KeyValuePair<string, Terminal.ConsoleCommand>> _ownCommands = new();
        private FileSystemWatcher? _configWatcher;

        private DateTime _lastReloadTime;
        private const long RELOAD_DELAY = 10000000; // One second


        /// <summary>
        /// Reloads the config some time after its file changes. Best effort:
        /// events arrive on another thread, one within a second of the last
        /// reload is dropped, and a reload that races the writer fails. Settings
        /// that must apply to the next command (the [Expectations] entries) are
        /// re-read by the command path itself; see ManifestCommands.RefreshConfig.
        /// </summary>
        private void SetupWatcher()
        {
            _lastReloadTime = DateTime.Now;
            _configWatcher = new(BepInEx.Paths.ConfigPath, ConfigFileName);
            _configWatcher.Changed += ReadConfigValues;
            _configWatcher.Created += ReadConfigValues;
            _configWatcher.Renamed += ReadConfigValues;
            _configWatcher.IncludeSubdirectories = true;
            _configWatcher.EnableRaisingEvents = true;
        }

        private void ReadConfigValues(object sender, FileSystemEventArgs e)
        {
            var now = DateTime.Now;
            var time = now.Ticks - _lastReloadTime.Ticks;
            if (!File.Exists(ConfigFileFullPath) || time < RELOAD_DELAY) return;

            try
            {
                Log.LogInfo("Attempting to reload configuration...");
                Config.Reload();
                Log.LogInfo("Configuration reloaded successfully!");
            }
            catch
            {
                Log.LogError($"There was an issue loading {ConfigFileName}");
                return;
            }

            _lastReloadTime = now;
        }
    }

    /// <summary>Completes an async request; see valheimCLIPlugin.BeginAsync.</summary>
    public sealed class AsyncHandle
    {
        private readonly RequestBroker _broker;
        public long Id { get; }

        internal AsyncHandle(RequestBroker broker, long id)
        {
            _broker = broker;
            Id = id;
        }

        public bool Abandoned => _broker.IsAbandoned(Id);
        /// <summary>After Complete: true until the socket thread has taken the response to send.</summary>
        public bool AwaitingCollection => _broker.IsComplete(Id);
        public void Output(string line) => _broker.Output(Id, line);
        public void Complete() => _broker.Complete(Id);
    }

    // Harmony patch to capture console output
    [HarmonyPatch(typeof(Terminal), nameof(Terminal.AddString), new Type[] { typeof(string) })]
    public static class Terminal_AddString_Patch
    {
        public static void Postfix(string text)
        {
            valheimCLIPlugin.Instance?.CaptureOutput(text);
        }
    }
}
