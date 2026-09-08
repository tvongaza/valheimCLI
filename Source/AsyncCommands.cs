using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace valheimCLI
{
    /// <summary>
    /// Commands that finish later than their handler: each starts a coroutine,
    /// waits for a specific in-game condition with a deadline, and completes
    /// its request (see valheimCLIPlugin.BeginAsync) with one line that says
    /// what happened, or which condition was still pending at the deadline.
    /// They replace fixed sleeps in capture scripts: a caller issues one
    /// command and gets one bounded answer.
    ///
    ///   cli_arrive  <x> <y> <z> [radius=64] [timeout=30]     teleport, wait for landing and loaded zones (re-teleports once if dropped)
    ///   cli_env     <tod|keep> <env|keep> [timeout=15]       set debug time/weather, wait for the transition to finish
    ///   cli_capture <name> <cx> <cy> <cz> <lx> <ly> <lz> [supersize=1] [timeout=20]
    ///                                                        pose the free-fly camera, wait for zones, heightmap rebuilds and the
    ///                                                        env transition, render two frames, capture, wait for the file
    ///   cli_until   <timeout> <needle> <command...>          re-run a console command until a line contains needle
    ///   cli_clear_view is the verified clear (CustomCommands): destroy, recount next frame, repeat up to three passes
    /// </summary>
    public static class AsyncCommands
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void Register()
        {
            new Terminal.ConsoleCommand("cli_arrive", "Teleport and wait until the player has landed and every zone within radius is loaded: cli_arrive <x> <y> <z> [radius=64] [timeout=30]. A dropped teleport is re-issued once.", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (args.Length < 4 || !TryF(args[1], out float x) || !TryF(args[2], out float y) || !TryF(args[3], out float z))
                {
                    args.Context.AddString("Usage: cli_arrive <x> <y> <z> [radius=64] [timeout=30]");
                    return;
                }
                float radius = args.Length >= 5 && TryF(args[4], out float r) ? Mathf.Clamp(r, 1f, 200f) : 64f;
                float timeout = args.Length >= 6 && TryF(args[5], out float t) ? t : 30f;
                Start(args.Context.AddString, handle => Arrive(handle, new Vector3(x, y, z), radius, timeout));
            }, isCheat: true);

            new Terminal.ConsoleCommand("cli_env", "Set the local debug time of day and weather and wait for the transition: cli_env <tod 0-1|keep> <env name|keep> [timeout=15]. Both persist for the session; set once per capture run.", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (args.Length < 3)
                {
                    args.Context.AddString("Usage: cli_env <tod 0-1|keep> <env|keep> [timeout=15]");
                    return;
                }
                float timeout = args.Length >= 4 && TryF(args[3], out float t) ? t : 15f;
                Start(args.Context.AddString, handle => Env(handle, args[1], args[2], timeout));
            }, isCheat: true);

            new Terminal.ConsoleCommand("cli_capture", "Pose the free-fly camera, wait until the view is ready (zones loaded, no heightmap rebuild queued, weather transition done), render two frames, save a PNG and wait for the file: cli_capture <name> <cx> <cy> <cz> <lookX> <lookY> <lookZ> [supersize=1] [timeout=20]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (args.Length < 8 || !TryF(args[2], out float cx) || !TryF(args[3], out float cy) || !TryF(args[4], out float cz) ||
                    !TryF(args[5], out float lx) || !TryF(args[6], out float ly) || !TryF(args[7], out float lz))
                {
                    args.Context.AddString("Usage: cli_capture <name> <cx> <cy> <cz> <lookX> <lookY> <lookZ> [supersize=1] [timeout=20]");
                    return;
                }
                int supersize = 1;
                if (args.Length >= 9 && (!int.TryParse(args[8], out supersize) || supersize < 1 || supersize > 4))
                {
                    args.Context.AddString("Usage: supersize is 1..4");
                    return;
                }
                float timeout = args.Length >= 10 && TryF(args[9], out float t) ? t : 20f;
                Start(args.Context.AddString, handle => Capture(handle, args[1], new Vector3(cx, cy, cz), new Vector3(lx, ly, lz), supersize, timeout));
            }, isCheat: true);

            new Terminal.ConsoleCommand("cli_until", "Re-run a console command every 250 ms until one of its output lines contains the needle, then return that output: cli_until <timeout> <needle> <command...>", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (args.Length < 4 || !TryF(args[1], out float timeout))
                {
                    args.Context.AddString("Usage: cli_until <timeout seconds> <needle> <command...>");
                    return;
                }
                string needle = args[2];
                string command = string.Join(" ", args.Args.Skip(3));
                Start(args.Context.AddString, handle => Until(handle, timeout, needle, command));
            }, isCheat: true);
        }

        /// <summary>cli_clear_view: destroy the clutter, recount on the next frame, repeat while anything remains (up to three passes).</summary>
        public static void StartClearView(float x, float z, float radius, Action<string> console)
        {
            Start(console, handle => ClearView(handle, x, z, radius));
        }

        private static IEnumerator ClearView(Action<string> output, float x, float z, float radius)
        {
            if (ZNetScene.instance == null)
            {
                output("ERROR: world not loaded");
                yield break;
            }
            Dictionary<string, int> byKind = new Dictionary<string, int>();
            int removed = 0, passes = 0, remaining = 0;
            for (passes = 1; passes <= 3; passes++)
            {
                List<ZNetView> victims = CustomCommands.CollectClearViewTargets(x, z, radius, byKind);
                if (victims.Count == 0 && passes > 1)
                {
                    passes--;
                    break;
                }
                removed += victims.Count;
                CustomCommands.DestroyViews(victims);
                yield return null;
                remaining = CustomCommands.CollectClearViewTargets(x, z, radius, new Dictionary<string, int>()).Count;
                if (remaining == 0)
                    break;
            }
            string summary = string.Join(" ", byKind.Select(kv => $"{kv.Key}={kv.Value}"));
            output($"OK: CLEAR_VIEW removed={removed} passes={Math.Min(passes, 3)} remaining={remaining} radius={radius:F0} at={x:F0},{z:F0} {summary}".TrimEnd());
        }

        private static bool TryF(string s, out float value) => float.TryParse(s, NumberStyles.Float, Inv, out value);

        /// <summary>Open the async request and run the coroutine; without a CLI request (typed in the F5 console) run it anyway and print to the console.</summary>
        private static void Start(Action<string> console, Func<Action<string>, IEnumerator> body)
        {
            AsyncHandle? handle = valheimCLIPlugin.BeginAsync();
            valheimCLIPlugin? plugin = valheimCLIPlugin.Instance;
            if (plugin == null)
            {
                console("ERROR: plugin not available");
                handle?.Complete();
                return;
            }
            Action<string> output = handle != null ? handle.Output : console;
            plugin.StartCoroutine(Run(body(output), handle, output));
        }

        private static IEnumerator Run(IEnumerator body, AsyncHandle? handle, Action<string> output)
        {
            try
            {
                while (true)
                {
                    object? current;
                    try
                    {
                        if (!body.MoveNext()) break;
                        current = body.Current;
                    }
                    catch (Exception ex)
                    {
                        output($"ERROR: code=unexpected_exception message={ex.Message}");
                        break;
                    }
                    yield return current;
                }
            }
            finally
            {
                handle?.Complete();
            }
        }

        // ---- cli_arrive ----
        private static IEnumerator Arrive(Action<string> output, Vector3 target, float radius, float timeout)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                output("ERROR: No local player found");
                yield break;
            }
            Stopwatch clock = Stopwatch.StartNew();
            int retries = 0;
            CustomCommands.TeleportPlayer(target, _ => { }, distant: false);
            string pending = "";
            while (clock.Elapsed.TotalSeconds < timeout)
            {
                yield return null;
                Vector3 p = player.transform.position;
                float dx = p.x - target.x, dz = p.z - target.z;
                bool atTarget = dx * dx + dz * dz <= 4f;
                if (player.IsTeleporting())
                {
                    pending = $"teleporting, player at {p.x:F1},{p.y:F1},{p.z:F1}";
                    continue;
                }
                if (!atTarget)
                {
                    // A non-distant teleport lands 2 s in, and if no floor answers the
                    // raycast yet (the zone's terrain collider is a frame behind its
                    // spawn) the game bounces the player back ("portal blocked"). The
                    // zones are loaded by then, so the next attempt lands.
                    pending = $"bounced back to {p.x:F1},{p.y:F1},{p.z:F1}";
                    if (retries >= 3)
                        break;
                    retries++;
                    CustomCommands.TeleportPlayer(target, _ => { }, distant: false);
                    continue;
                }
                string zoneLine = "";
                CustomCommands.ZoneReady(target.x, target.z, radius, line => zoneLine = line);
                if (zoneLine.Contains("ready=true"))
                {
                    output($"OK: ARRIVE position={p.x:F1},{p.y:F1},{p.z:F1} retries={retries} ms={clock.ElapsedMilliseconds} {zoneLine}");
                    yield break;
                }
                pending = zoneLine;
            }
            output($"ERROR: code=arrive_timeout retries={retries} ms={clock.ElapsedMilliseconds} pending={pending}");
        }

        // ---- cli_env ----
        private static IEnumerator Env(Action<string> output, string tod, string env, float timeout)
        {
            EnvMan envMan = EnvMan.instance;
            if (envMan == null)
            {
                output("ERROR: EnvMan is not ready");
                yield break;
            }
            string setLines = "";
            if (!tod.Equals("keep", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryF(tod, out float fraction))
                {
                    output("ERROR: tod must be 0-1, -1 (reset) or keep");
                    yield break;
                }
                CustomCommands.SetDebugTimeOfDay(fraction, line => setLines += line + "; ");
            }
            string? wantEnv = null;
            if (!env.Equals("keep", StringComparison.OrdinalIgnoreCase))
            {
                string line = "";
                CustomCommands.SetDebugEnvironment(env, l => line = l);
                setLines += line + "; ";
                if (line.StartsWith("ERROR"))
                {
                    output(line);
                    yield break;
                }
                wantEnv = envMan.m_debugEnv;
            }
            Stopwatch clock = Stopwatch.StartNew();
            // EnvMan picks the debug env up on its next update, queues it and interpolates for
            // m_transitionDuration (2 s in the shipped game); m_nextEnv stays set until done.
            while (clock.Elapsed.TotalSeconds < timeout)
            {
                yield return null;
                bool transition = envMan.m_nextEnv != null;
                string current = envMan.m_currentEnv?.m_name ?? "";
                bool envOk = wantEnv == null || (!string.IsNullOrEmpty(wantEnv) && current == wantEnv) || (wantEnv == "" && !transition);
                if (!transition && envOk)
                {
                    output($"OK: ENV current={current} tod={(envMan.m_debugTimeOfDay ? envMan.m_debugTime.ToString("F3", Inv) : "game")} transition_ms={clock.ElapsedMilliseconds} {setLines.TrimEnd(' ', ';')}");
                    yield break;
                }
            }
            output($"ERROR: code=env_timeout current={envMan.m_currentEnv?.m_name} next={envMan.m_nextEnv?.m_name} wanted={wantEnv}");
        }

        // ---- cli_capture ----
        private static IEnumerator Capture(Action<string> output, string name, Vector3 camera, Vector3 lookAt, int supersize, float timeout)
        {
            string poseLine = "";
            CustomCommands.FreeFlyPose(camera, lookAt, line => poseLine = line);
            if (poseLine.StartsWith("ERROR"))
            {
                output(poseLine);
                yield break;
            }
            Stopwatch clock = Stopwatch.StartNew();
            string pending = "";
            int frames = 0;
            bool ready = false;
            while (clock.Elapsed.TotalSeconds < timeout)
            {
                yield return null;
                frames++;
                pending = ViewPending(lookAt, 64f);
                if (pending.Length == 0)
                {
                    ready = true;
                    break;
                }
            }
            if (!ready)
            {
                output($"ERROR: code=capture_timeout name={name} pending={pending}");
                yield break;
            }
            long readyMs = clock.ElapsedMilliseconds;
            // Two completed renders with the new pose, terrain and weather before the capture.
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();

            string path = CustomCommands.ResolveScreenshotPathPublic(name);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            try { File.Delete(path); } catch { /* a stale file of the same name must not pass as this capture */ }
            ScreenCapture.CaptureScreenshot(path, supersize);

            // The PNG is written asynchronously: done when it exists and its size stops changing.
            long lastSize = -1;
            int stable = 0;
            while (clock.Elapsed.TotalSeconds < timeout)
            {
                yield return new WaitForSecondsRealtime(0.1f);
                long size = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (size > 0 && size == lastSize)
                {
                    stable++;
                    if (stable >= 2)
                    {
                        output($"OK: CAPTURE name={name} path={path} bytes={size} size={Screen.width * supersize}x{Screen.height * supersize} ready_ms={readyMs} frames_waited={frames} total_ms={clock.ElapsedMilliseconds}");
                        yield break;
                    }
                }
                else
                {
                    stable = 0;
                }
                lastSize = size;
            }
            output($"ERROR: code=capture_timeout name={name} pending=file path={path} bytes={lastSize}");
        }

        /// <summary>Empty when the view around a point is ready; otherwise what is still pending.</summary>
        public static string ViewPending(Vector3 around, float radius)
        {
            List<string> reasons = new List<string>();
            string zoneLine = "";
            CustomCommands.ZoneReady(around.x, around.z, radius, line => zoneLine = line);
            if (!zoneLine.Contains("ready=true"))
                reasons.Add(zoneLine.Replace("ZONE_READY ", "zones:"));

            int queued = 0;
            List<Heightmap> heightmaps = Heightmap.GetAllHeightmaps();
            if (heightmaps != null)
                foreach (Heightmap hm in heightmaps)
                    if (hm != null && hm.HaveQueuedRebuild())
                        queued++;
            if (queued > 0)
                reasons.Add($"heightmap_rebuilds_queued:{queued}");

            EnvMan envMan = EnvMan.instance;
            if (envMan != null && envMan.m_nextEnv != null)
                reasons.Add($"env_transition:{envMan.m_currentEnv?.m_name}->{envMan.m_nextEnv.m_name}");

            return string.Join(";", reasons);
        }

        // ---- cli_until ----
        private static IEnumerator Until(Action<string> output, float timeout, string needle, string command)
        {
            valheimCLIPlugin? plugin = valheimCLIPlugin.Instance;
            if (plugin == null)
            {
                output("ERROR: plugin not available");
                yield break;
            }
            Stopwatch clock = Stopwatch.StartNew();
            int polls = 0;
            List<string> last = new List<string>();
            while (true)
            {
                polls++;
                last = plugin.RunCapturing(command);
                if (last.Any(l => l.Contains(needle)))
                {
                    foreach (string line in last) output(line);
                    output($"OK: UNTIL matched after {clock.ElapsedMilliseconds} ms polls={polls}");
                    yield break;
                }
                if (clock.Elapsed.TotalSeconds >= timeout)
                    break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            foreach (string line in last) output(line);
            output($"ERROR: code=until_timeout polls={polls} needle={needle}");
        }
    }
}
