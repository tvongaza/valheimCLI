using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace valheimCLI
{
    /// <summary>One loaded BepInEx plugin, as cli_manifest reports it.</summary>
    public sealed class PluginFacts
    {
        public string Guid = "";
        public string Name = "";
        public string Version = "";
        /// <summary>Full path of the plugin's DLL; empty when the plugin has no file on disk.</summary>
        public string File = "";
        /// <summary>Lower-case hex md5 of the file; empty when there is no file.</summary>
        public string Md5 = "";
        /// <summary>Whether the file was written after the plugin loaded: if so, the running code may be an older build.</summary>
        public FileChange ChangedSinceLoad;
    }

    /// <summary>Whether a plugin's file was written after the plugin loaded.</summary>
    public enum FileChange
    {
        No,
        Yes,
        /// <summary>The plugin's load time is not known (a reloader loaded it from bytes after startup).</summary>
        Unknown
    }

    /// <summary>The world the game is on, as cli_world reports it.</summary>
    public sealed class WorldFacts
    {
        public string Name = "";
        public string Seed = "";
        public string Uid = "";
        public string WorldGen = "";
        /// <summary>Hash of the save directory (see Expectations.HashDirectory); empty when unknown.</summary>
        public string Files = "";
        /// <summary>One of the WorldFilesHashed values: when, or why not, <see cref="Files"/> was taken.</summary>
        public string FilesHashed = "";
        /// <summary>The save directory that was hashed; empty on a client.</summary>
        public string Dir = "";

        public bool Loaded => Name.Length > 0;
    }

    /// <summary>When the world's save files were hashed.</summary>
    public static class WorldFilesHashed
    {
        /// <summary>By the server or host as it loaded the world, before anything could save it.</summary>
        public const string AtLoad = "at_load";
        /// <summary>A client joined to a server has no copy of the world's files.</summary>
        public const string ClientHasNoFiles = "client_has_no_files";
        /// <summary>The world loaded before valheimCLI did, so its load-time files are unknown.</summary>
        public const string NotAtLoad = "not_at_load";
        /// <summary>There was no save directory at load: a new world, or a save in the older single-file format.</summary>
        public const string NoDirectory = "no_directory";
    }

    /// <summary>One key=value expectation, with the line (or argument position) it came from.</summary>
    public sealed class Expectation
    {
        public string Key = "";
        public string Value = "";
        public int Line;

        public override string ToString() => Key + "=" + Value;
    }

    /// <summary>
    /// What a game is expected to run, written as key=value lines, and the
    /// check of a running game against them. Plain .NET with no game types, so
    /// the mod (cli_expect and the standing expectations file), the CLI
    /// (manifest --write, --expect) and the unit tests share one parser and
    /// one matcher.
    ///
    /// A plugin key is a plugin's GUID, its name or its DLL file name without
    /// .dll (case-insensitive; a space may be written as _). Its value is
    ///   an md5 of the DLL, or a prefix of at least 8 hex characters
    ///   any      the plugin must be loaded, any build
    ///   absent   the plugin must not be loaded
    /// World keys are world (name, or any), seed, worlduid and worldfiles (a
    /// prefix of the save-files hash taken at load). In a file, # starts a
    /// comment, at the start of a line or after whitespace.
    /// </summary>
    public static class Expectations
    {
        public const string Any = "any";
        public const string Absent = "absent";
        /// <summary>Shortest hash prefix accepted: shorter ones match too many builds by chance.</summary>
        public const int MinHashPrefix = 8;

        public static readonly string[] WorldKeys = { "world", "seed", "worlduid", "worldfiles" };

        public static bool IsWorldKey(string key) => WorldKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

        // ---- parsing ----

        /// <summary>
        /// One key=value token. The value is validated for its key: a hash is
        /// 8 to 32 hex characters, worlduid is an integer, world and seed are
        /// any non-empty text. Hashes and keywords are returned lower case.
        /// </summary>
        public static bool TryParse(string token, int line, out Expectation expectation, out string error)
        {
            expectation = new Expectation();
            error = "";
            int eq = token.IndexOf('=');
            if (eq <= 0 || eq == token.Length - 1 || token.Any(char.IsWhiteSpace))
            {
                error = $"'{token}' is not key=value";
                return false;
            }

            string key = token.Substring(0, eq);
            string value = token.Substring(eq + 1);
            string lower = value.ToLowerInvariant();
            if (IsWorldKey(key))
            {
                key = key.ToLowerInvariant();
                if (key == "worlduid" && !long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                {
                    error = $"'{token}': worlduid is an integer (cli_world prints it)";
                    return false;
                }
                if (key == "worldfiles" && !IsHashPrefix(lower))
                {
                    error = $"'{token}': worldfiles is a hash of {MinHashPrefix} to 32 hex characters";
                    return false;
                }
                if (key == "worldfiles" || (key == "world" && lower == Any)) value = lower;
            }
            else if (lower == Any || lower == Absent || IsHashPrefix(lower))
            {
                value = lower;
            }
            else
            {
                error = $"'{token}': a plugin's value is an md5 ({MinHashPrefix} to 32 hex characters), '{Any}' or '{Absent}'";
                return false;
            }

            expectation = new Expectation { Key = key, Value = value, Line = line };
            return true;
        }

        /// <summary>
        /// Every expectation in a file's lines. Blank lines and comments are
        /// skipped; a malformed line or a key given twice adds an error naming
        /// its line and is left out.
        /// </summary>
        public static List<Expectation> ParseLines(IEnumerable<string> lines, List<string> errors)
        {
            List<Expectation> result = new List<Expectation>();
            int number = 0;
            foreach (string raw in lines)
            {
                number++;
                string text = StripComment(raw).Trim();
                if (text.Length == 0) continue;
                if (!TryParse(text, number, out Expectation e, out string error))
                {
                    errors.Add($"line {number}: {error}");
                    continue;
                }
                Expectation? first = result.FirstOrDefault(r => string.Equals(r.Key, e.Key, StringComparison.OrdinalIgnoreCase));
                if (first != null)
                {
                    errors.Add($"line {number}: {e.Key} is already expected on line {first.Line}");
                    continue;
                }
                result.Add(e);
            }
            return result;
        }

        /// <summary>The cli_expect command that checks <paramref name="expectations"/> in the game.</summary>
        public static string ExpectCommand(IEnumerable<Expectation> expectations, bool strict) =>
            string.Join(" ", new[] { "cli_expect" }.Concat(strict ? new[] { "--strict" } : Array.Empty<string>()).Concat(expectations.Select(e => e.ToString())));

        private static string StripComment(string line)
        {
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
                    return line.Substring(0, i);
            }
            return line;
        }

        private static bool IsHashPrefix(string value) =>
            value.Length >= MinHashPrefix && value.Length <= 32 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

        // ---- matching ----

        /// <summary>
        /// Every expectation that does not hold, one line each; empty when all hold.
        /// <paramref name="waitForWorld"/>: world keys are skipped while no world
        /// is loaded (a standing file is checked at the main menu too) instead
        /// of failing. <paramref name="strict"/>: every loaded plugin must be
        /// named by some plugin key, and a loaded world by world or worlduid.
        /// </summary>
        public static List<string> Check(IReadOnlyList<Expectation> expectations, IReadOnlyList<PluginFacts> plugins, WorldFacts world, bool strict, bool waitForWorld)
        {
            List<string> problems = new List<string>();
            List<PluginFacts> named = new List<PluginFacts>();
            foreach (Expectation e in expectations)
            {
                if (IsWorldKey(e.Key))
                {
                    CheckWorldKey(e, world, waitForWorld, problems);
                    continue;
                }

                List<PluginFacts> match = plugins.Where(p => Names(p, e.Key)).ToList();
                named.AddRange(match);
                if (e.Value == Absent)
                {
                    if (match.Count > 0) problems.Add($"{e.Key}: loaded ({FileName(match[0])}), expected absent");
                    continue;
                }
                if (match.Count == 0)
                {
                    problems.Add($"{e.Key}: not loaded, expected {e.Value}");
                    continue;
                }
                if (match.Count > 1)
                {
                    problems.Add($"{e.Key}: names {match.Count} loaded plugins ({string.Join(", ", match.Select(m => m.Guid))}); use a GUID");
                    continue;
                }
                if (e.Value == Any) continue;

                PluginFacts p = match[0];
                if (p.Md5.Length == 0) problems.Add($"{e.Key}: has no file on disk to hash, expected {e.Value}");
                else if (!p.Md5.StartsWith(e.Value, StringComparison.Ordinal)) problems.Add($"{e.Key}: md5 {Short(p.Md5)}, expected {e.Value} ({FileName(p)})");
                else if (p.ChangedSinceLoad == FileChange.Yes) problems.Add($"{e.Key}: file matches but was written after the game loaded it; the running code may be the previous build");
            }

            if (strict)
            {
                foreach (PluginFacts p in plugins)
                {
                    if (!named.Contains(p))
                        problems.Add($"{Normalize(p.Guid)}: loaded but not listed (strict), md5 {(p.Md5.Length > 0 ? Short(p.Md5) : "-")} ({FileName(p)})");
                }
                if (world.Loaded && !expectations.Any(e => e.Key == "world" || e.Key == "worlduid"))
                    problems.Add($"world: {world.Name} (uid {world.Uid}) is loaded but not listed (strict); add world=, worlduid= or world=any");
            }
            return problems;
        }

        private static void CheckWorldKey(Expectation e, WorldFacts world, bool waitForWorld, List<string> problems)
        {
            if (!world.Loaded)
            {
                if (!waitForWorld) problems.Add($"{e.Key}: no world loaded, expected {e.Value}");
                return;
            }
            switch (e.Key)
            {
                case "world":
                    if (e.Value != Any && !string.Equals(Normalize(world.Name), Normalize(e.Value), StringComparison.OrdinalIgnoreCase))
                        problems.Add($"world: {world.Name}, expected {e.Value}");
                    break;
                case "seed":
                    if (!string.Equals(Normalize(world.Seed), Normalize(e.Value), StringComparison.Ordinal))
                        problems.Add($"seed: {world.Seed}, expected {e.Value}");
                    break;
                case "worlduid":
                    if (!string.Equals(world.Uid, e.Value, StringComparison.Ordinal))
                        problems.Add($"worlduid: {world.Uid}, expected {e.Value}");
                    break;
                case "worldfiles":
                    if (world.FilesHashed == WorldFilesHashed.ClientHasNoFiles) problems.Add("worldfiles: a client has no world files; check this on the server or host");
                    else if (world.FilesHashed == WorldFilesHashed.NoDirectory) problems.Add("worldfiles: there was no save directory at load (a new world, or a save in the older single-file format)");
                    else if (world.Files.Length == 0) problems.Add("worldfiles: the world loaded before valheimCLI, so its files at load are unknown");
                    else if (!world.Files.StartsWith(e.Value, StringComparison.Ordinal)) problems.Add($"worldfiles: {Short(world.Files)}, expected {e.Value}");
                    break;
            }
        }

        /// <summary>A plugin key names a plugin by GUID, name or file name without extension.</summary>
        public static bool Names(PluginFacts plugin, string key)
        {
            string k = Normalize(key);
            return string.Equals(Normalize(plugin.Guid), k, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Normalize(plugin.Name), k, StringComparison.OrdinalIgnoreCase)
                || (plugin.File.Length > 0 && string.Equals(Normalize(Path.GetFileNameWithoutExtension(plugin.File)), k, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Report lines are space-separated key=value fields, so a space inside a value is written as _.</summary>
        public static string Normalize(string value) => value.Replace(' ', '_');

        private static string FileName(PluginFacts p) => p.File.Length > 0 ? Normalize(Path.GetFileName(p.File)) : "no file";

        private static string Short(string md5) => md5.Length > MinHashPrefix ? md5.Substring(0, MinHashPrefix) : md5;

        /// <summary>
        /// Whether a plugin's file was written after the plugin loaded.
        /// valheimCLI knows its own load time (recorded in its Awake, however
        /// it was loaded; null when not recorded). Another plugin loaded from
        /// its file by the chainloader loaded at process start; one whose
        /// assembly was loaded from bytes (a reloader such as ScriptEngine,
        /// after startup) has no known load time, so the answer is Unknown
        /// rather than a guess from the process start.
        /// </summary>
        public static FileChange ChangedSinceLoad(bool isOwn, bool loadedFromBytes, DateTime writtenUtc, DateTime? ownLoadUtc, DateTime processStartUtc)
        {
            if (isOwn)
            {
                if (ownLoadUtc == null) return FileChange.Unknown;
                return writtenUtc > ownLoadUtc.Value ? FileChange.Yes : FileChange.No;
            }
            if (loadedFromBytes) return FileChange.Unknown;
            return writtenUtc > processStartUtc ? FileChange.Yes : FileChange.No;
        }

        // ---- report lines (the mod writes them, the CLI reads them) ----

        /// <summary>
        /// PLUGIN guid=... name=... version=... md5=... changed_since_load=yes|no|unknown file=...
        /// Every field but file has spaces written as _; file is last and verbatim.
        /// </summary>
        public static string FormatPlugin(PluginFacts p) =>
            $"PLUGIN guid={Normalize(p.Guid)} name={Normalize(p.Name)} version={Normalize(p.Version)} md5={(p.Md5.Length > 0 ? p.Md5 : "-")} changed_since_load={FileChangeName(p.ChangedSinceLoad)} file={p.File}";

        private static string FileChangeName(FileChange change) =>
            change == FileChange.Yes ? "yes" : change == FileChange.Unknown ? "unknown" : "no";

        /// <summary>The fields of a FormatPlugin line, or null when the line is not one.</summary>
        public static PluginFacts? ParsePlugin(string line)
        {
            Dictionary<string, string>? f = Fields(line, "PLUGIN ", "file");
            if (f == null || !f.ContainsKey("guid")) return null;
            string md5 = Get(f, "md5");
            return new PluginFacts
            {
                Guid = Get(f, "guid"),
                Name = Get(f, "name"),
                Version = Get(f, "version"),
                File = Get(f, "file"),
                Md5 = md5 == "-" ? "" : md5,
                ChangedSinceLoad = Get(f, "changed_since_load") switch
                {
                    "yes" => FileChange.Yes,
                    "unknown" => FileChange.Unknown,
                    _ => FileChange.No
                }
            };
        }

        /// <summary>
        /// WORLD name=... seed=... uid=... worldgen=... files=... files_hashed=... dir=...
        /// files is - when unknown; dir is last and verbatim.
        /// </summary>
        public static string FormatWorld(WorldFacts w) =>
            $"WORLD name={Normalize(w.Name)} seed={Normalize(w.Seed)} uid={w.Uid} worldgen={w.WorldGen} files={(w.Files.Length > 0 ? w.Files : "-")} files_hashed={w.FilesHashed} dir={w.Dir}";

        /// <summary>The fields of a FormatWorld line, or null when the line is not one.</summary>
        public static WorldFacts? ParseWorld(string line)
        {
            Dictionary<string, string>? f = Fields(line, "WORLD ", "dir");
            if (f == null || Get(f, "name").Length == 0) return null;
            string files = Get(f, "files");
            return new WorldFacts
            {
                Name = Get(f, "name"),
                Seed = Get(f, "seed"),
                Uid = Get(f, "uid"),
                WorldGen = Get(f, "worldgen"),
                Files = files == "-" ? "" : files,
                FilesHashed = Get(f, "files_hashed"),
                Dir = Get(f, "dir")
            };
        }

        private static Dictionary<string, string>? Fields(string line, string prefix, string lastKey)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
            string body = line.Substring(prefix.Length);
            string rest = "";
            int last = body.IndexOf(" " + lastKey + "=", StringComparison.Ordinal);
            if (last >= 0)
            {
                rest = body.Substring(last + lastKey.Length + 2);
                body = body.Substring(0, last);
            }
            Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string token in body.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = token.IndexOf('=');
                if (eq > 0) fields[token.Substring(0, eq)] = token.Substring(eq + 1);
            }
            if (last >= 0) fields[lastKey] = rest;
            return fields;
        }

        private static string Get(Dictionary<string, string> fields, string key) => fields.TryGetValue(key, out string? v) ? v : "";

        // ---- snapshot ----

        /// <summary>
        /// An expectations file that the given game satisfies, strict mode
        /// included: one GUID=md5 line per plugin (any for one with no file),
        /// and with a world, world/worlduid/seed lines plus the load-time files
        /// hash as a commented line to uncomment for a world restored before
        /// each run. <paramref name="header"/> lines become comments at the top.
        /// </summary>
        public static string Snapshot(IEnumerable<PluginFacts> plugins, WorldFacts? world, IEnumerable<string> header)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string h in header) sb.Append("# ").Append(h).Append('\n');
            sb.Append("# One key=value per line. A plugin key is its GUID, name or DLL file name;\n");
            sb.Append($"# its value an md5 (or its first {MinHashPrefix}+ hex characters), '{Any}' (loaded, any build)\n");
            sb.Append($"# or '{Absent}' (must not be loaded). See docs/expectations.md.\n");
            sb.Append('\n');
            foreach (PluginFacts p in plugins.OrderBy(p => p.Guid, StringComparer.Ordinal))
            {
                string value = p.Md5.Length > 0 ? p.Md5 : Any;
                string file = p.File.Length > 0 ? Path.GetFileName(p.File) : "no file on disk";
                sb.Append($"{Normalize(p.Guid)}={value}  # {p.Name} {p.Version} ({file})\n");
            }
            if (world != null && world.Loaded)
            {
                sb.Append('\n');
                sb.Append($"world={Normalize(world.Name)}\n");
                sb.Append($"worlduid={world.Uid}\n");
                sb.Append($"seed={Normalize(world.Seed)}\n");
                if (world.Files.Length > 0)
                {
                    sb.Append("# The save files as they were when the world loaded. Uncomment for a world\n");
                    sb.Append("# that is restored before each run; any save changes it.\n");
                    sb.Append($"# worldfiles={world.Files}\n");
                }
            }
            return sb.ToString();
        }

        // ---- hashing ----

        /// <summary>Lower-case hex md5 of a file's bytes.</summary>
        public static string Md5OfFile(string path)
        {
            using (MD5 md5 = MD5.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return Hex(md5.ComputeHash(stream));
            }
        }

        /// <summary>
        /// The hash of a directory tree: for every file under it, the line
        /// "relative/path:md5" (forward slashes), sorted ordinally, joined by
        /// \n with no trailing newline, and the md5 of those bytes as UTF-8.
        /// Empty when the directory does not exist. docs/expectations.md shows
        /// the same recipe as a shell one-liner.
        /// </summary>
        public static string HashDirectory(string dir)
        {
            if (!Directory.Exists(dir)) return "";
            string root = Path.GetFullPath(dir).TrimEnd('/', '\\');
            List<string> lines = new List<string>();
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(root.Length + 1).Replace('\\', '/');
                lines.Add(rel + ":" + Md5OfFile(file));
            }
            lines.Sort(StringComparer.Ordinal);
            using (MD5 all = MD5.Create())
            {
                return Hex(all.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
            }
        }

        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        /// <summary>A configured path: absolute as given, relative to <paramref name="baseDir"/> otherwise, empty for none.</summary>
        public static string ResolvePath(string configured, string baseDir)
        {
            string p = configured.Trim();
            if (p.Length == 0) return "";
            return Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p);
        }
    }

    /// <summary>
    /// The standing expectations file: re-read whenever it changes, checked
    /// before every command sent through the CLI except the diagnostics, and
    /// a single log line each time the result changes (not one per command).
    /// </summary>
    public sealed class StandingExpectations
    {
        private string _path = "";
        private DateTime _written;
        private long _length = -1;
        private List<Expectation> _entries = new List<Expectation>();
        private List<string> _errors = new List<string>();
        private string? _lastState;

        private static readonly HashSet<string> Diagnostics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "cli_manifest", "cli_world", "cli_expect", "cli_connection_status", "help" };

        /// <summary>
        /// Let a command run while expectations fail. For commands that only
        /// report on the game (and so help find the mismatch); a feature that
        /// adds one calls this when it registers.
        /// </summary>
        public static void AllowWhileMismatched(string commandName) => Diagnostics.Add(commandName);

        /// <summary>The command line's first word is an allowed diagnostic.</summary>
        public static bool IsAllowedWhileMismatched(string commandLine)
        {
            string[] words = commandLine.Trim().Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            return words.Length > 0 && Diagnostics.Contains(words[0]);
        }

        /// <summary>
        /// What does not hold of the file at <paramref name="path"/>; empty
        /// when everything does or no file is configured. A configured file
        /// that is missing or unreadable is itself a problem, so a typo in the
        /// path cannot silently switch the check off. World keys wait until a
        /// world is loaded.
        /// </summary>
        public List<string> Problems(string path, bool strict, Func<IReadOnlyList<PluginFacts>> plugins, Func<WorldFacts> world)
        {
            if (path.Length == 0)
            {
                _path = "";
                return new List<string>();
            }
            if (!File.Exists(path))
            {
                _length = -1;
                return new List<string> { $"expectations file not found: {path}" };
            }

            FileInfo info = new FileInfo(path);
            if (path != _path || info.LastWriteTimeUtc != _written || info.Length != _length)
            {
                try
                {
                    List<string> errors = new List<string>();
                    _entries = Expectations.ParseLines(File.ReadAllLines(path), errors);
                    _errors = errors.Select(e => $"{path} {e}").ToList();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _length = -1;
                    return new List<string> { $"expectations file could not be read: {ex.Message}" };
                }
                _path = path;
                _written = info.LastWriteTimeUtc;
                _length = info.Length;
            }

            List<string> problems = new List<string>(_errors);
            problems.AddRange(Expectations.Check(_entries, plugins(), world(), strict, waitForWorld: true));
            return problems;
        }

        /// <summary>
        /// A log message when <paramref name="problems"/> differ from the last
        /// call's: a warning listing them, or a note that they are all met
        /// again. Null when nothing changed, and on a first call that finds
        /// nothing wrong.
        /// </summary>
        public string? StateChange(List<string> problems, out bool isWarning)
        {
            string state = string.Join("\n", problems);
            isWarning = problems.Count > 0;
            bool changed = state != (_lastState ?? "");
            _lastState = state;
            if (!changed)
            {
                return null;
            }
            if (!isWarning) return "Expectations are met again; commands are no longer refused.";
            return $"Expectations do not hold; every CLI command but the diagnostics is refused until they do ({problems.Count}):\n  " + string.Join("\n  ", problems);
        }
    }
}
