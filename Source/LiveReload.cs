using System;
using System.Collections.Generic;
using System.Globalization;

namespace valheimCLI
{
    /// <summary>
    /// The rules behind live reload, kept free of Unity and BepInEx so the
    /// tests exercise them. BepInEx ScriptEngine loads plugins from
    /// BepInEx/scripts and reloads them when a file there changes, each time
    /// as a new assembly named "&lt;name&gt;-&lt;DateTime.Now.Ticks&gt;"; the
    /// chainloader loads BepInEx/plugins once at startup. See docs/live-reload.md.
    /// </summary>
    public static class LiveReload
    {
        public const string SourcePlugins = "plugins";
        public const string SourceScripts = "scripts";
        public const string SourceOther = "other";
        public const string SourceUnknown = "unknown";

        /// <summary>
        /// Which loader a plugin came from: "scripts" when its file is under
        /// BepInEx/scripts or its assembly carries the ScriptEngine rename,
        /// "plugins" when its file is under BepInEx/plugins, "other" for any
        /// other file, "unknown" when neither the file nor the name says.
        /// </summary>
        public static string ClassifySource(string? location, string? assemblyName)
        {
            string path = (location ?? "").Replace('\\', '/');
            if (ContainsSegment(path, "BepInEx/scripts/"))
                return SourceScripts;
            if (ContainsSegment(path, "BepInEx/plugins/"))
                return SourcePlugins;
            if (TryParseReloadedName(assemblyName, out _, out _))
                return SourceScripts;
            return path.Length > 0 ? SourceOther : SourceUnknown;
        }

        private static bool ContainsSegment(string path, string segment)
        {
            return path.IndexOf("/" + segment, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.StartsWith(segment, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Splits a ScriptEngine assembly name "&lt;name&gt;-&lt;ticks&gt;" into the
        /// original name and the local time it was loaded. A name whose suffix
        /// is not a plausible tick count (a version, "-beta") is not a reload.
        /// </summary>
        public static bool TryParseReloadedName(string? assemblyName, out string baseName, out DateTime loadedLocal)
        {
            baseName = assemblyName ?? "";
            loadedLocal = default;
            if (string.IsNullOrEmpty(assemblyName))
                return false;
            int dash = assemblyName!.LastIndexOf('-');
            if (dash <= 0 || dash == assemblyName.Length - 1)
                return false;
            string suffix = assemblyName.Substring(dash + 1);
            // Ticks since year 1: 18 digits for any date from 0317 to 3169.
            if (suffix.Length != 18 || !long.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
                return false;
            baseName = assemblyName.Substring(0, dash);
            loadedLocal = new DateTime(ticks, DateTimeKind.Local);
            return true;
        }

        /// <summary>An md5 prefix a caller may pass: 6 to 32 hex digits.</summary>
        public static bool IsMd5Prefix(string? text)
        {
            if (text == null || text.Length < 6 || text.Length > 32)
                return false;
            foreach (char c in text)
            {
                if (!Uri.IsHexDigit(c))
                    return false;
            }
            return true;
        }

        /// <summary>True when no prefix is asked for, or the md5 starts with it (either case).</summary>
        public static bool Md5Matches(string? md5, string? prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return true;
            return !string.IsNullOrEmpty(md5) && md5!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Lower-case hex of a hash, the form md5sum prints.</summary>
        public static string Hex(byte[] hash)
        {
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Binding the command port: after a live reload the new server starts a
        /// frame after the old one closed its listener, and the operating system
        /// may still hold the port for a moment. Retry while the server is still
        /// meant to run, up to <see cref="PortBindAttempts"/> attempts
        /// <see cref="PortBindRetryMs"/> apart (five seconds in all).
        /// </summary>
        public const int PortBindAttempts = 20;
        public const int PortBindRetryMs = 250;

        public static bool ShouldRetryPortBind(int attempt, bool running)
        {
            return running && attempt < PortBindAttempts;
        }

        /// <summary>
        /// The entries a registration added or replaced: every key whose value in
        /// <paramref name="after"/> is not the very object it was in
        /// <paramref name="before"/>. Ownership is the registered object, never its
        /// name: a newer instance re-registering the same name owns it from then on.
        /// </summary>
        public static List<KeyValuePair<TKey, TValue>> Registered<TKey, TValue>(
            IEnumerable<KeyValuePair<TKey, TValue>> before, IEnumerable<KeyValuePair<TKey, TValue>> after)
            where TKey : notnull
            where TValue : class
        {
            Dictionary<TKey, TValue> previous = new Dictionary<TKey, TValue>();
            foreach (KeyValuePair<TKey, TValue> entry in before)
                previous[entry.Key] = entry.Value;
            List<KeyValuePair<TKey, TValue>> added = new List<KeyValuePair<TKey, TValue>>();
            foreach (KeyValuePair<TKey, TValue> entry in after)
            {
                if (!previous.TryGetValue(entry.Key, out TValue? old) || !ReferenceEquals(old, entry.Value))
                    added.Add(entry);
            }
            return added;
        }

        /// <summary>
        /// Unload: remove each owned entry that still holds the object this
        /// instance registered; an entry another instance has since replaced is
        /// left alone. Returns how many were removed.
        /// </summary>
        public static int RemoveOwned<TKey, TValue>(IDictionary<TKey, TValue> registry, IEnumerable<KeyValuePair<TKey, TValue>> owned)
            where TKey : notnull
            where TValue : class
        {
            int removed = 0;
            foreach (KeyValuePair<TKey, TValue> entry in owned)
            {
                if (registry.TryGetValue(entry.Key, out TValue? current) && ReferenceEquals(current, entry.Value))
                {
                    registry.Remove(entry.Key);
                    removed++;
                }
            }
            return removed;
        }
    }

    /// <summary>
    /// The arguments of cli_await_plugin: &lt;guid|file.dll&gt; [md5-prefix|-] [timeout=30].
    /// The plugin is named by its BepInPlugin GUID, or by the file name it was
    /// loaded from (anything ending in .dll), for a caller that has the DLL but
    /// not the GUID.
    /// </summary>
    public sealed class AwaitPluginRequest
    {
        public const string Usage = "Usage: cli_await_plugin <guid|file.dll> [md5-prefix|-] [timeout=30]";
        public const double DefaultTimeoutSeconds = 30;
        public const double MaxTimeoutSeconds = 600;

        /// <summary>A GUID, or a file name when it ends in .dll.</summary>
        public string Target = "";
        /// <summary>Lower-case md5 prefix the new instance's file must match, or null for any build.</summary>
        public string? Md5Prefix;
        public double TimeoutSeconds = DefaultTimeoutSeconds;

        /// <summary>Parses the arguments after the command name.</summary>
        public static bool TryParse(IReadOnlyList<string> args, out AwaitPluginRequest request, out string error)
        {
            request = new AwaitPluginRequest();
            error = "";
            if (args.Count < 1 || args.Count > 3 || string.IsNullOrWhiteSpace(args[0]))
            {
                error = Usage;
                return false;
            }
            request.Target = args[0];
            if (args.Count >= 2 && args[1] != "-")
            {
                if (!LiveReload.IsMd5Prefix(args[1]))
                {
                    error = $"ERROR: code=bad_input message=md5-prefix must be 6 to 32 hex digits or - (got {args[1]}). {Usage}";
                    return false;
                }
                request.Md5Prefix = args[1].ToLowerInvariant();
            }
            if (args.Count >= 3)
            {
                if (!double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double timeout) ||
                    double.IsNaN(timeout) || timeout <= 0 || timeout > MaxTimeoutSeconds)
                {
                    error = $"ERROR: code=bad_input message=timeout must be a number of seconds above 0 and at most {MaxTimeoutSeconds:F0} (got {args[2]}). {Usage}";
                    return false;
                }
                request.TimeoutSeconds = timeout;
            }
            return true;
        }

        public bool TargetsFile => Target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether a plugin with this GUID, loaded from this file, is the one named.</summary>
        public bool Matches(string? guid, string? location)
        {
            if (!TargetsFile)
                return string.Equals(guid, Target, StringComparison.Ordinal);
            string path = location ?? "";
            int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            return string.Equals(path.Substring(slash + 1), Target, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Decides when cli_await_plugin is satisfied. What must be proven is the
    /// md5 of the bytes an instance was loaded from, and a file's md5 now does
    /// not say that: the copy that triggers a reload replaces the file under
    /// the old instance first, so "the file matches" would pass before anything
    /// reloaded. An instance's md5 counts only when it was read at load time:
    ///  - a new instance (not alive when the call arrived) is hashed once, when
    ///    first seen (ScriptEngine read the same file a frame or two earlier);
    ///  - an instance loaded in the same pass as the answering valheimCLI has
    ///    its md5 from that load (<paramref name="knownLoadMd5"/> in the
    ///    constructor): ScriptEngine reloads every plugin in BepInEx/scripts
    ///    together, so a reload of both answers after reconnecting.
    /// Without an md5 prefix only a new instance counts: that proves a reload.
    /// </summary>
    public sealed class PluginAwaiter
    {
        private readonly AwaitPluginRequest _request;
        private readonly HashSet<int> _aliveAtCall;
        private readonly Dictionary<int, string> _knownLoadMd5;
        private readonly Dictionary<int, string> _md5AtFirstSight = new Dictionary<int, string>();

        public PluginAwaiter(AwaitPluginRequest request, IEnumerable<int> instancesAliveAtCall, IDictionary<int, string>? knownLoadMd5 = null)
        {
            _request = request;
            _aliveAtCall = new HashSet<int>(instancesAliveAtCall);
            _knownLoadMd5 = knownLoadMd5 != null ? new Dictionary<int, string>(knownLoadMd5) : new Dictionary<int, string>();
        }

        /// <summary>The md5 of the last instance that did not match, for the timeout message.</summary>
        public string LastMismatchMd5 { get; private set; } = "";

        /// <summary>New instances seen so far (matching or not).</summary>
        public int NewInstancesSeen => _md5AtFirstSight.Count;

        /// <summary>
        /// Consider one live instance with the awaited GUID. <paramref name="readMd5"/>
        /// returns the md5 of its file, or null while its location is not known
        /// yet (it is then asked again on the next scan). Returns true when this
        /// instance satisfies the request; <paramref name="md5"/> is its load-time
        /// md5, or "" when it has none.
        /// </summary>
        public bool Observe(int instanceId, Func<string?> readMd5, out string md5)
        {
            md5 = "";
            if (_aliveAtCall.Contains(instanceId))
            {
                if (_request.Md5Prefix == null || !_knownLoadMd5.TryGetValue(instanceId, out string? loaded))
                    return false;
                md5 = loaded;
                if (LiveReload.Md5Matches(loaded, _request.Md5Prefix))
                    return true;
                LastMismatchMd5 = loaded;
                return false;
            }
            if (!_md5AtFirstSight.TryGetValue(instanceId, out string? seen))
            {
                seen = readMd5();
                if (seen == null)
                    return false;
                _md5AtFirstSight[instanceId] = seen;
            }
            md5 = seen;
            if (LiveReload.Md5Matches(seen, _request.Md5Prefix))
                return true;
            LastMismatchMd5 = seen;
            return false;
        }
    }
}
