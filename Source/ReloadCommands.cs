using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;

namespace valheimCLI
{
    /// <summary>
    /// Live reload support (BepInEx ScriptEngine; see docs/live-reload.md).
    /// ScriptEngine loads plugins from BepInEx/scripts and reloads them when a
    /// file there changes, each time as a new assembly named
    /// "&lt;name&gt;-&lt;ticks&gt;". It cannot say when a reload has finished,
    /// or prove which build is now running, and it refuses a GUID the
    /// chainloader already loaded from BepInEx/plugins. These commands fill
    /// those gaps:
    ///
    ///   cli_build                                   which valheimCLI build answers: assembly, source, md5 of the file it loaded
    ///   cli_await_plugin &lt;guid|file.dll&gt; [md5|-] [timeout=30]  wait until any plugin is reloaded, optionally proven to be a given build
    ///   cli_self_unload                             unload this valheimCLI so a copy in BepInEx/scripts can load in its place
    ///
    /// cli_await_plugin cannot report valheimCLI's own replacement: the new
    /// instance destroys the one serving the request, and the connection
    /// closes. A client reconnects and runs cli_build instead.
    /// </summary>
    public static class ReloadCommands
    {
        private static DateTime s_loadedUtc;
        private static string s_md5 = "";
        private static string s_location = "";
        /// <summary>Load-time md5 of every plugin loaded in the same pass as this instance, by instance id.</summary>
        private static readonly Dictionary<int, string> s_passMd5 = new Dictionary<int, string>();

        public static void Register()
        {
            new Terminal.ConsoleCommand("cli_build", "Which valheimCLI build is answering, with its assembly (a ScriptEngine reload names it valheimCLI-<ticks>), source (plugins|scripts), the md5 of its file when it loaded, and the load time: cli_build", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                Build(args.Context.AddString);
            });

            new Terminal.ConsoleCommand("cli_await_plugin", "Wait until a plugin, named by GUID or by the DLL file it loads from, is reloaded (an instance not loaded when the call arrived), optionally proven to be the build whose md5 starts with the given prefix: cli_await_plugin <guid|file.dll> [md5-prefix|-] [timeout=30]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (!AwaitPluginRequest.TryParse(args.Args.Skip(1).Where(a => a.Length > 0).ToList(), out AwaitPluginRequest request, out string error))
                {
                    args.Context.AddString(error);
                    return;
                }
                if (request.Matches(valheimCLIPlugin.ModGUID, s_location))
                {
                    AwaitSelf(request, args.Context.AddString);
                    return;
                }
                AsyncCommands.Start("await_plugin", args.Context.AddString, ctx => AwaitPlugin(ctx, request), gated: false);
            });

            new Terminal.ConsoleCommand("cli_self_unload", "Unload this valheimCLI instance so a copy in BepInEx/scripts can load in its place on ScriptEngine's next reload; the command port closes once the reply is sent: cli_self_unload", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                SelfUnload(args.Context.AddString);
            });
        }

        /// <summary>
        /// Called from the plugin's Start. ScriptEngine sets a plugin's location
        /// only after its Awake returns, so Start is the first moment it is
        /// known; ScriptEngine read the file a frame or two earlier. The md5 is
        /// taken once, here: the file can be replaced by the next build while
        /// this one still runs, and hashing it later would describe that build.
        /// When this instance came from BepInEx/scripts, the plugins on the same
        /// GameObject were loaded in the same ScriptEngine pass, so their files
        /// are hashed now too: cli_await_plugin can then prove their build after
        /// a reload that also replaced this instance. (The chainloader's
        /// GameObject holds every startup plugin; those never reload, and
        /// hashing them all would slow startup for nothing.)
        /// </summary>
        internal static void RecordLoadedBuild(BaseUnityPlugin plugin, DateTime loadedUtc)
        {
            s_loadedUtc = loadedUtc;
            s_location = LocationOf(plugin);
            s_md5 = Md5OfFile(s_location) ?? "";
            s_passMd5.Clear();
            if (LiveReload.ClassifySource(s_location, Assembly.GetExecutingAssembly().GetName().Name) != LiveReload.SourceScripts)
                return;
            foreach (BaseUnityPlugin sibling in plugin.GetComponents<BaseUnityPlugin>())
            {
                string? md5 = ReferenceEquals(sibling, plugin) ? s_md5 : Md5OfFile(LocationOf(sibling));
                if (!string.IsNullOrEmpty(md5))
                    s_passMd5[sibling.GetInstanceID()] = md5!;
            }
        }

        private static void Build(Action<string> output)
        {
            string assembly = Assembly.GetExecutingAssembly().GetName().Name;
            string source = LiveReload.ClassifySource(s_location, assembly);
            output($"OK: BUILD guid={valheimCLIPlugin.ModGUID} version={valheimCLIPlugin.ModVersion} assembly={assembly} source={source} md5={(s_md5.Length > 0 ? s_md5 : "unknown")} loaded={s_loadedUtc:yyyy-MM-ddTHH:mm:ssZ} location={s_location}");
        }

        /// <summary>
        /// Awaiting valheimCLI itself. A newer instance destroys this one before
        /// it could answer, so only one outcome can be reported: this instance
        /// is already the build asked for (it loaded from a file with that md5).
        /// Otherwise the request stays open until the reload closes the
        /// connection, or until its timeout says no reload happened.
        /// </summary>
        private static void AwaitSelf(AwaitPluginRequest request, Action<string> console)
        {
            if (request.Md5Prefix == null)
            {
                console("ERROR: code=self_await message=valheimCLI cannot report its own replacement (it is unloaded before it could answer); pass the new build's md5 so the instance answering can say whether it is that build, or reconnect after the reload and run cli_build");
                return;
            }
            if (LiveReload.Md5Matches(s_md5, request.Md5Prefix))
            {
                string assembly = Assembly.GetExecutingAssembly().GetName().Name;
                console($"OK: PLUGIN guid={valheimCLIPlugin.ModGUID} version={valheimCLIPlugin.ModVersion} assembly={assembly} source={LiveReload.ClassifySource(s_location, assembly)} md5={s_md5} ms=0 self=true location={s_location}");
                return;
            }
            AsyncCommands.Start("await_plugin", console, ctx => AwaitSelfReplaced(ctx, request), gated: false);
        }

        private static IEnumerator AwaitSelfReplaced(AsyncCommands.Context ctx, AwaitPluginRequest request)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < request.TimeoutSeconds && !ctx.Cancelled)
                yield return null;
            if (!ctx.Cancelled)
                ctx.Output($"ERROR: code=await_timeout message=valheimCLI was not replaced within {request.TimeoutSeconds:F0}s; still running md5={s_md5} (wanted {request.Md5Prefix}); is ScriptEngine's file watcher on, and is a copy loaded from BepInEx/plugins blocking it (cli_self_unload)?");
        }

        /// <summary>
        /// Scans the loaded plugins every 100 ms (unscaled): every BaseUnityPlugin
        /// object, hidden ones included, since both loaders hide the objects that
        /// carry plugins. ScriptEngine also registers each instance in
        /// Chainloader.PluginInfos, but that holds one entry per GUID and is
        /// overwritten or removed by whichever loader ran last, so the objects
        /// themselves are the record.
        /// </summary>
        private static IEnumerator AwaitPlugin(AsyncCommands.Context ctx, AwaitPluginRequest request)
        {
            PluginAwaiter awaiter = new PluginAwaiter(request, FindInstances(request).Select(p => p.GetInstanceID()).ToList(), s_passMd5);
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < request.TimeoutSeconds && !ctx.Cancelled)
            {
                foreach (BaseUnityPlugin plugin in FindInstances(request))
                {
                    if (awaiter.Observe(plugin.GetInstanceID(), () => Md5OfFile(LocationOf(plugin)), out string md5))
                    {
                        ctx.Output(DescribeInstance(plugin, md5, clock.ElapsedMilliseconds));
                        yield break;
                    }
                }
                float next = Time.realtimeSinceStartup + 0.1f;
                while (Time.realtimeSinceStartup < next)
                    yield return null;
            }
            if (ctx.Cancelled)
                yield break;
            string loaded = string.Join(",", FindInstances(request).Select(p => p.GetType().Assembly.GetName().Name));
            string why = awaiter.NewInstancesSeen == 0
                ? "no new instance loaded"
                : $"{awaiter.NewInstancesSeen} new instance(s) loaded";
            if (awaiter.LastMismatchMd5.Length > 0)
                why += $", running md5={awaiter.LastMismatchMd5}";
            ctx.Output($"ERROR: code=await_timeout message={request.Target} not reloaded within {request.TimeoutSeconds:F0}s: {why}; loaded now: {(loaded.Length > 0 ? loaded : "none")}");
        }

        private static List<BaseUnityPlugin> FindInstances(AwaitPluginRequest request)
        {
            List<BaseUnityPlugin> found = new List<BaseUnityPlugin>();
            foreach (BaseUnityPlugin plugin in Resources.FindObjectsOfTypeAll<BaseUnityPlugin>())
            {
                if (plugin == null) continue;
                BepInPlugin? meta = MetadataHelper.GetMetadata(plugin);
                if (meta != null && request.Matches(meta.GUID, request.TargetsFile ? LocationOf(plugin) : null))
                    found.Add(plugin);
            }
            return found;
        }

        private static string DescribeInstance(BaseUnityPlugin plugin, string md5, long ms)
        {
            BepInPlugin? meta = MetadataHelper.GetMetadata(plugin);
            string assembly = plugin.GetType().Assembly.GetName().Name;
            string location = LocationOf(plugin);
            return $"OK: PLUGIN guid={meta?.GUID} version={meta?.Version} assembly={assembly} source={LiveReload.ClassifySource(location, assembly)} md5={md5} ms={ms} location={location}";
        }

        /// <summary>
        /// The file a plugin was loaded from. Both loaders record it on the
        /// plugin's PluginInfo (ScriptEngine loads from bytes, so the assembly
        /// itself has no location).
        /// </summary>
        private static string LocationOf(BaseUnityPlugin plugin)
        {
            string? location = plugin.Info?.Location;
            if (string.IsNullOrEmpty(location))
            {
                BepInPlugin? meta = MetadataHelper.GetMetadata(plugin);
                if (meta != null && Chainloader.PluginInfos.TryGetValue(meta.GUID, out PluginInfo info) && ReferenceEquals(info.Instance, plugin))
                    location = info.Location;
            }
            if (string.IsNullOrEmpty(location))
                location = plugin.GetType().Assembly.Location;
            return location ?? "";
        }

        private static string? Md5OfFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;
            try
            {
                using MD5 hash = MD5.Create();
                using FileStream stream = File.OpenRead(path);
                return LiveReload.Hex(hash.ComputeHash(stream));
            }
            catch (IOException)
            {
                // Being written by the copy that triggers the reload: ask again on the next scan.
                return null;
            }
        }

        /// <summary>
        /// Destroys this instance after its reply has been collected by the
        /// socket thread (the destroy closes the port). Its PluginInfos entry is
        /// removed first, and only if it is this instance's: ScriptEngine
        /// refuses a GUID that is registered there.
        /// </summary>
        private static void SelfUnload(Action<string> console)
        {
            valheimCLIPlugin? plugin = valheimCLIPlugin.Instance;
            if (plugin == null)
            {
                console("ERROR: code=no_instance message=valheimCLI has no live instance");
                return;
            }
            // Marked async so the request completes in the coroutine, which can
            // then see when the socket thread has collected the reply.
            AsyncHandle? handle = valheimCLIPlugin.BeginAsync();
            string line = $"OK: UNLOADING assembly={Assembly.GetExecutingAssembly().GetName().Name}; the command port closes after this reply";
            if (handle != null)
                handle.Output(line);
            else
                console(line);
            if (Chainloader.PluginInfos.TryGetValue(valheimCLIPlugin.ModGUID, out PluginInfo info) && ReferenceEquals(info.Instance, plugin))
                Chainloader.PluginInfos.Remove(valheimCLIPlugin.ModGUID);
            plugin.StartCoroutine(DestroyAfterReply(plugin, handle));
        }

        private static IEnumerator DestroyAfterReply(valheimCLIPlugin plugin, AsyncHandle? handle)
        {
            // A completed request stays complete until the socket thread takes
            // its response; it writes the reply right after. Five seconds bounds
            // a client that disconnected before reading.
            handle?.Complete();
            Stopwatch clock = Stopwatch.StartNew();
            while (handle != null && handle.AwaitingCollection && clock.Elapsed.TotalSeconds < 5)
                yield return null;
            yield return null;
            UnityEngine.Object.Destroy(plugin);
        }
    }
}
