using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;

namespace valheimCLI
{
    /// <summary>
    /// Which mod builds and which world this game is running, so a script can
    /// refuse to work on the wrong ones. A copy that failed because the game
    /// held the DLL open, a stale copy in another plugins folder, or a machine
    /// with a different build all look like a normal run until the results
    /// make no sense.
    ///
    ///   cli_manifest                  every loaded plugin: GUID, name, version, md5, file
    ///   cli_world                     name, seed, uid, world-gen version and, on a server
    ///                                 or host, a hash of the save files taken at load
    ///   cli_expect [--strict] k=v ... OK, or MISMATCH with each difference
    ///
    /// The md5 is of the plugin's file as it is on disk now, so a file replaced
    /// after the game loaded it would match: such a plugin reports
    /// changed_since_load=yes and fails an md5 expectation. A plugin a reloader
    /// loaded from bytes has no known load time and reports unknown.
    ///
    /// With [Expectations] File set, the same check runs before every command
    /// the CLI sends, and everything but the diagnostics is refused while it
    /// fails. The parsing and matching live in Expectations.cs.
    /// </summary>
    public static class ManifestCommands
    {
        private static readonly DateTime ProcessStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        /// <summary>When valheimCLI's plugin loaded (its Awake); later than the process start after a live reload.</summary>
        private static DateTime? _ownLoadUtc;
        private static readonly Dictionary<string, (DateTime written, string md5)> Hashes = new Dictionary<string, (DateTime, string)>();

        internal static ConfigEntry<string>? FileConfig;
        internal static ConfigEntry<bool>? StrictConfig;
        private static ConfigFile? _config;
        private static readonly FileRefresher ConfigRefresher = new FileRefresher();
        private static readonly StandingExpectations Standing = new StandingExpectations();

        /// <summary>
        /// Records when valheimCLI loaded. Called from the plugin's Awake: a
        /// static initialiser would run when this class is first used, and a
        /// reloader loads the assembly from bytes, so neither the assembly's
        /// location nor a field initialiser can say it.
        /// </summary>
        internal static void RecordOwnLoad(DateTime utc) => _ownLoadUtc = utc;

        /// <summary>
        /// The plugin's config, as loaded by Awake. The standing check re-reads
        /// it when the .cfg changed, instead of waiting for a file watcher.
        /// </summary>
        internal static void UseConfig(ConfigFile config)
        {
            _config = config;
            ConfigRefresher.Baseline(config.ConfigFilePath);
        }

        /// <summary>
        /// Reloads the config when its file changed since the last check, so an
        /// edit to [Expectations] applies to the very next command (one stat
        /// per check when nothing changed).
        /// </summary>
        private static void RefreshConfig()
        {
            ConfigFile? config = _config;
            if (config == null) return;
            try
            {
                if (ConfigRefresher.RefreshIfChanged(config.ConfigFilePath, config.Reload))
                    valheimCLIPlugin.Log.LogInfo($"Expectations: config changed, reloaded (strict={Strict()}, file={ConfiguredPath()})");
            }
            catch (Exception ex)
            {
                valheimCLIPlugin.Log.LogWarning($"Expectations: could not reload {config.ConfigFilePath}: {ex.Message}");
            }
        }

        private static bool Strict() => StrictConfig?.Value ?? false;

        public static void Register()
        {
            new Terminal.ConsoleCommand("cli_manifest", "List every loaded BepInEx plugin with its GUID, name, version, md5, file and whether the file changed after it was loaded: cli_manifest", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                List<PluginFacts> plugins = Plugins();
                foreach (PluginFacts p in plugins) args.Context.AddString(Expectations.FormatPlugin(p));
                args.Context.AddString($"OK: MANIFEST plugins={plugins.Count}");
            });

            new Terminal.ConsoleCommand("cli_world", "Report the loaded world's name, seed, uid, world-gen version and, on a server or host, a hash of its save files as they were at load: cli_world", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                WorldFacts w = World();
                if (!w.Loaded)
                {
                    args.Context.AddString("ERROR: code=no_world message=No world is loaded.");
                    return;
                }
                args.Context.AddString(Expectations.FormatWorld(w));
                args.Context.AddString("OK: WORLD");
            });

            new Terminal.ConsoleCommand("cli_expect", "Check the loaded plugins and world against expectations (plugin=md5|any|absent, world=, seed=, worlduid=, worldfiles=); with no pairs, check the configured expectations file: cli_expect [--strict] [key=value ...]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                Expect(args.Args.Skip(1).ToList(), args.Context.AddString);
            });
        }

        private static void Expect(List<string> words, Action<string> output)
        {
            bool strict = words.RemoveAll(w => w.Equals("--strict", StringComparison.OrdinalIgnoreCase)) > 0;
            List<string> problems;
            int count;
            string path = "";
            if (words.Count == 0)
            {
                // The configured file, as the next command would be checked
                // against it; the reply names the file and strict value used.
                RefreshConfig();
                path = ConfiguredPath();
                strict |= Strict();
                if (path.Length == 0)
                {
                    output($"OK: EXPECT off (no [Expectations] File; pass key=value pairs to check them) strict={Lower(strict)} file=");
                    return;
                }
                problems = Standing.Problems(path, strict, Plugins, World);
                count = -1;
            }
            else
            {
                List<Expectation> expectations = new List<Expectation>();
                for (int i = 0; i < words.Count; i++)
                {
                    if (!Expectations.TryParse(words[i], i + 1, out Expectation e, out string error))
                    {
                        output($"ERROR: code=bad_input message={error}");
                        return;
                    }
                    expectations.Add(e);
                }
                problems = Expectations.Check(expectations, Plugins(), World(), strict, waitForWorld: false);
                count = expectations.Count;
            }

            string used = count >= 0 ? "" : $" strict={Lower(strict)} file={path}";
            if (problems.Count == 0)
            {
                output(count >= 0 ? $"OK: EXPECT {count} expectation(s) met{(strict ? " (strict)" : "")}" : $"OK: EXPECT holds{used}");
                return;
            }
            foreach (string p in problems) output("MISMATCH " + p);
            output($"ERROR: code=expectation_mismatch mismatches={problems.Count}{used}");
        }

        private static string Lower(bool value) => value ? "true" : "false";

        // ---- standing expectations ----

        private static string ConfiguredPath() => Expectations.ResolvePath(FileConfig?.Value ?? "", Paths.ConfigPath);

        /// <summary>
        /// The standing expectations that do not hold (empty when all do or no
        /// file is set). Logs a warning when the result changes, not per call.
        /// </summary>
        internal static List<string> StandingProblems()
        {
            RefreshConfig();
            List<string> problems = Standing.Problems(ConfiguredPath(), Strict(), Plugins, World);
            string? change = Standing.StateChange(problems, out bool isWarning);
            if (change != null)
            {
                if (isWarning) valheimCLIPlugin.Log.LogWarning(change);
                else valheimCLIPlugin.Log.LogInfo(change);
            }
            return problems;
        }

        /// <summary>
        /// Refuses a CLI command, with the mismatch list, while the standing
        /// expectations fail. Diagnostics always run so the mismatch can be
        /// looked into.
        /// </summary>
        internal static bool Refuse(string command, Action<string> output)
        {
            if (StandingExpectations.IsAllowedWhileMismatched(command)) return false;
            List<string> problems = StandingProblems();
            if (problems.Count == 0) return false;
            foreach (string p in problems) output("MISMATCH " + p);
            output($"ERROR: code=expectation_mismatch message=Refused: this game does not match {ConfiguredPath()}; cli_manifest, cli_world and cli_expect still answer.");
            return true;
        }

        // ---- facts ----

        /// <summary>Every loaded plugin, by GUID. A file is hashed again only when its write time changes.</summary>
        private static List<PluginFacts> Plugins()
        {
            List<PluginFacts> list = new List<PluginFacts>();
            foreach (KeyValuePair<string, PluginInfo> kv in Chainloader.PluginInfos.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                PluginInfo info = kv.Value;
                string file = info.Location ?? "";
                PluginFacts p = new PluginFacts
                {
                    Guid = kv.Key,
                    Name = info.Metadata?.Name ?? "",
                    Version = info.Metadata?.Version?.ToString() ?? "",
                    File = file
                };
                if (file.Length > 0 && File.Exists(file))
                {
                    DateTime written = File.GetLastWriteTimeUtc(file);
                    if (!Hashes.TryGetValue(file, out (DateTime written, string md5) cached) || cached.written != written)
                    {
                        cached = (written, Expectations.Md5OfFile(file));
                        Hashes[file] = cached;
                    }
                    p.Md5 = cached.md5;
                    bool isOwn = kv.Key == valheimCLIPlugin.ModGUID;
                    // An assembly loaded from bytes has no location: a reloader loaded it, at an unknown time.
                    bool loadedFromBytes = info.Instance != null && string.IsNullOrEmpty(info.Instance.GetType().Assembly.Location);
                    p.ChangedSinceLoad = Expectations.ChangedSinceLoad(isOwn, loadedFromBytes, written, _ownLoadUtc, ProcessStartUtc);
                }
                list.Add(p);
            }
            return list;
        }

        private static string? _loadedFiles;
        private static string _loadedDir = "";

        /// <summary>
        /// A server or host is about to load its world (ZNet.Awake loads it):
        /// hash the save files before the game reads or writes them.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class ZNetAwakePatch
        {
            private static void Prefix()
            {
                _loadedFiles = null;
                _loadedDir = "";
                try
                {
                    World? w = ZNet.World;
                    if (!ZNet.m_isServer || w == null) return;
                    _loadedDir = w.GetSaveDirectory(w.m_fileSource);
                    _loadedFiles = Expectations.HashDirectory(_loadedDir);
                }
                catch (Exception ex)
                {
                    valheimCLIPlugin.Log.LogWarning($"cli_world: could not hash the world's save files at load: {ex.Message}");
                }
            }
        }

        private static WorldFacts World()
        {
            WorldFacts f = new WorldFacts();
            World? w = ZNet.World;
            if (w == null || ZNet.instance == null) return f;
            f.Name = w.m_name ?? "";
            f.Seed = w.m_seedName ?? "";
            f.Uid = w.m_uid.ToString(CultureInfo.InvariantCulture);
            f.WorldGen = w.m_worldGenVersion.ToString(CultureInfo.InvariantCulture);
            if (!ZNet.instance.IsServer())
            {
                f.FilesHashed = WorldFilesHashed.ClientHasNoFiles;
                return f;
            }
            string dir = w.GetSaveDirectory(w.m_fileSource);
            f.Dir = dir;
            // Hashed at load only when this is the world that loaded; any other
            // case (valheimCLI loaded after the world) cannot know the load-time files.
            if (_loadedFiles != null && _loadedDir == dir)
            {
                f.Files = _loadedFiles;
                f.FilesHashed = f.Files.Length > 0 ? WorldFilesHashed.AtLoad : WorldFilesHashed.NoDirectory;
            }
            else
            {
                f.FilesHashed = WorldFilesHashed.NotAtLoad;
            }
            return f;
        }
    }
}
