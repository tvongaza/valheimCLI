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
    ///   cli_arrive  <x> <y> <z> [radius=64] [timeout=30]     teleport, wait for landing and loaded zones (re-teleports when the game bounces the landing)
    ///   cli_env     <tod|keep> <env|keep> [timeout=15]       set debug time/weather, wait for the transition to finish
    ///   cli_capture <name> <cx> <cy> <cz> <lx> <ly> <lz> [supersize=1] [timeout=20]
    ///                                                        pose the free-fly camera, wait for zones, heightmap rebuilds and the
    ///                                                        env transition, render two frames, capture to a request-specific file,
    ///                                                        verify the PNG, move it into place
    ///   cli_until   <timeout> <needle> <command...>          re-run a console command until a line contains needle
    ///   cli_clear_view is the verified clear (CustomCommands): destroy, recount next frame, repeat up to three passes
    ///
    /// Arrive, env, capture and clear share the player and the camera, so they
    /// run one at a time through an OperationGate: a second one waits its turn.
    /// When a request times out the server abandons it; the coroutine then
    /// issues no further actions, lets an effect it already started settle
    /// (the teleport lands or bounces, the screenshot file finishes), and only
    /// then releases the gate. cli_until is not gated: it only re-runs the
    /// command it was given.
    /// </summary>
    public static class AsyncCommands
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static readonly OperationGate Gate = new OperationGate();
        private static long s_consoleIds = -1;

        /// <summary>What a coroutine body gets: where to write, whether its request was abandoned.</summary>
        public sealed class Context
        {
            public long Id;
            public string Name = "";
            public AsyncHandle? Handle;
            public Action<string> Output = _ => { };
            public bool Cancelled => Handle != null && Handle.Abandoned;
        }

        public static void Register()
        {
            new Terminal.ConsoleCommand("cli_arrive", "Teleport and wait until the player has landed and every zone within radius is loaded: cli_arrive <x> <y> <z> [radius=64] [timeout=30]. A bounced landing is re-issued (up to three times).", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (args.Length < 4 || !TryF(args[1], out float x) || !TryF(args[2], out float y) || !TryF(args[3], out float z))
                {
                    args.Context.AddString("Usage: cli_arrive <x> <y> <z> [radius=64] [timeout=30]");
                    return;
                }
                float radius = args.Length >= 5 && TryF(args[4], out float r) ? Mathf.Clamp(r, 1f, 200f) : 64f;
                float timeout = args.Length >= 6 && TryF(args[5], out float t) ? t : 30f;
                Start("arrive", args.Context.AddString, ctx => Arrive(ctx, new Vector3(x, y, z), radius, timeout), gated: true);
            }, isCheat: true);

            new Terminal.ConsoleCommand("cli_env", "Set the local debug time of day and weather and wait for the transition: cli_env <tod 0-1|keep> <env name|keep> [timeout=15]. Both persist for the session; set once per capture run.", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (args.Length < 3)
                {
                    args.Context.AddString("Usage: cli_env <tod 0-1|keep> <env|keep> [timeout=15]");
                    return;
                }
                float timeout = args.Length >= 4 && TryF(args[3], out float t) ? t : 15f;
                Start("env", args.Context.AddString, ctx => Env(ctx, args[1], args[2], timeout), gated: true);
            }, isCheat: true);

            new Terminal.ConsoleCommand("cli_capture", "Pose the free-fly camera, wait until the view is ready (zones loaded, no heightmap rebuild queued, weather transition done), render two frames, save a PNG to a request-specific file, verify it and move it into place: cli_capture <name> <cx> <cy> <cz> <lookX> <lookY> <lookZ> [supersize=1] [timeout=20]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
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
                Start("capture", args.Context.AddString, ctx => Capture(ctx, args[1], new Vector3(cx, cy, cz), new Vector3(lx, ly, lz), supersize, timeout), gated: true);
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
                Start("until", args.Context.AddString, ctx => Until(ctx, timeout, needle, command), gated: false);
            }, isCheat: true);
        }

        /// <summary>cli_clear_view: destroy the clutter, recount on the next frame, repeat while anything remains (up to three passes).</summary>
        public static void StartClearView(float x, float z, float radius, Action<string> console)
        {
            Start("clear_view", console, ctx => ClearView(ctx, x, z, radius), gated: true);
        }

        private static bool TryF(string s, out float value) => float.TryParse(s, NumberStyles.Float, Inv, out value);

        /// <summary>Open the async request and run the coroutine; typed in the F5 console (no request) it runs anyway and prints there.</summary>
        private static void Start(string name, Action<string> console, Func<Context, IEnumerator> body, bool gated)
        {
            AsyncHandle? handle = valheimCLIPlugin.BeginAsync();
            valheimCLIPlugin? plugin = valheimCLIPlugin.Instance;
            if (plugin == null)
            {
                console("ERROR: plugin not available");
                handle?.Complete();
                return;
            }
            Context ctx = new Context
            {
                Id = handle?.Id ?? s_consoleIds--,
                Name = name,
                Handle = handle,
                Output = handle != null ? handle.Output : console
            };
            plugin.StartCoroutine(Run(ctx, body, gated));
        }

        private static IEnumerator Run(Context ctx, Func<Context, IEnumerator> bodyFactory, bool gated)
        {
            bool owns = false;
            try
            {
                if (gated)
                {
                    // Wait for the player/camera to be free. A request that times out while
                    // waiting is abandoned by the server and gives up here without acting.
                    Stopwatch waited = Stopwatch.StartNew();
                    while (!Gate.TryAcquire(ctx.Id, ctx.Name))
                    {
                        if (ctx.Cancelled || waited.Elapsed.TotalSeconds > 60)
                        {
                            ctx.Output($"ERROR: code=busy message={ctx.Name} waited {waited.ElapsedMilliseconds} ms for {Gate.OwnerName} (request #{Gate.Owner}) and gave up");
                            yield break;
                        }
                        yield return null;
                    }
                    owns = true;
                }

                IEnumerator body = bodyFactory(ctx);
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
                        ctx.Output($"ERROR: code=unexpected_exception message={ex.Message}");
                        valheimCLIPlugin.Log.LogError($"Async {ctx.Name} #{ctx.Id}: {ex}");
                        break;
                    }
                    yield return current;
                }
            }
            finally
            {
                if (owns)
                    Gate.Release(ctx.Id);
                ctx.Handle?.Complete();
            }
        }

        private static void Cancelled(Context ctx, string settled)
        {
            // The request was abandoned: this line is dropped by the broker, the log keeps it.
            valheimCLIPlugin.Log.LogWarning($"Async {ctx.Name} #{ctx.Id} cancelled after its request timed out; {settled}");
            ctx.Output($"CANCELLED: {ctx.Name} {settled}");
        }

        // ---- cli_arrive ----
        private static IEnumerator Arrive(Context ctx, Vector3 target, float radius, float timeout)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                ctx.Output("ERROR: No local player found");
                yield break;
            }
            Stopwatch clock = Stopwatch.StartNew();
            int retries = 0;
            CustomCommands.TeleportPlayer(target, _ => { }, distant: false);
            string pending = "";
            while (clock.Elapsed.TotalSeconds < timeout && !ctx.Cancelled)
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
                    // A target given well above the ground (site files use y=60) would drop the
                    // player: fall damage, a red flash over the next capture, seconds of falling.
                    // Set the player down once the ground is there.
                    bool grounded = false;
                    if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(p, out float ground) && p.y - ground > 1f)
                    {
                        player.transform.position = new Vector3(p.x, ground + 0.3f, p.z);
                        if (player.m_body != null)
                            player.m_body.linearVelocity = Vector3.zero;
                        p = player.transform.position;
                        grounded = true;
                    }
                    ctx.Output($"OK: ARRIVE position={p.x:F1},{p.y:F1},{p.z:F1} grounded={grounded} retries={retries} ms={clock.ElapsedMilliseconds} {zoneLine}");
                    yield break;
                }
                pending = zoneLine;
            }
            if (ctx.Cancelled)
            {
                // No further teleports; the one in flight lands or bounces on its own
                // (the game gives a non-distant teleport up to 15 s). Hold the gate until then.
                Stopwatch settle = Stopwatch.StartNew();
                while (player.IsTeleporting() && settle.Elapsed.TotalSeconds < 16)
                    yield return null;
                Vector3 p = player.transform.position;
                Cancelled(ctx, $"teleport settled after {settle.ElapsedMilliseconds} ms, player at {p.x:F1},{p.y:F1},{p.z:F1}");
                yield break;
            }
            ctx.Output($"ERROR: code=arrive_timeout retries={retries} ms={clock.ElapsedMilliseconds} pending={pending}");
        }

        // ---- cli_env ----
        private static IEnumerator Env(Context ctx, string tod, string env, float timeout)
        {
            EnvMan envMan = EnvMan.instance;
            if (envMan == null)
            {
                ctx.Output("ERROR: EnvMan is not ready");
                yield break;
            }
            string setLines = "";
            if (!tod.Equals("keep", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryF(tod, out float fraction))
                {
                    ctx.Output("ERROR: tod must be 0-1, -1 (reset) or keep");
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
                    ctx.Output(line);
                    yield break;
                }
                wantEnv = envMan.m_debugEnv;
            }
            Stopwatch clock = Stopwatch.StartNew();
            // EnvMan picks the debug env up on its next update, queues it and interpolates for
            // m_transitionDuration (2 s in the shipped game); m_nextEnv stays set until done.
            while (clock.Elapsed.TotalSeconds < timeout && !ctx.Cancelled)
            {
                yield return null;
                bool transition = envMan.m_nextEnv != null;
                string current = envMan.m_currentEnv?.m_name ?? "";
                bool envOk = wantEnv == null || (!string.IsNullOrEmpty(wantEnv) && current == wantEnv) || (wantEnv == "" && !transition);
                if (!transition && envOk)
                {
                    ctx.Output($"OK: ENV current={current} tod={(envMan.m_debugTimeOfDay ? envMan.m_debugTime.ToString("F3", Inv) : "game")} transition_ms={clock.ElapsedMilliseconds} {setLines.TrimEnd(' ', ';')}");
                    yield break;
                }
            }
            if (ctx.Cancelled)
            {
                // The settings are already applied; the transition finishes on its own.
                Cancelled(ctx, $"settings applied ({setLines.TrimEnd(' ', ';')}), transition left to finish");
                yield break;
            }
            ctx.Output($"ERROR: code=env_timeout current={envMan.m_currentEnv?.m_name} next={envMan.m_nextEnv?.m_name} wanted={wantEnv}");
        }

        // ---- cli_capture ----
        private static IEnumerator Capture(Context ctx, string name, Vector3 camera, Vector3 lookAt, int supersize, float timeout)
        {
            string poseLine = "";
            CustomCommands.FreeFlyPose(camera, lookAt, line => poseLine = line);
            if (poseLine.StartsWith("ERROR"))
            {
                ctx.Output(poseLine);
                yield break;
            }
            Stopwatch clock = Stopwatch.StartNew();
            string pending = "";
            int frames = 0;
            bool ready = false;
            while (clock.Elapsed.TotalSeconds < timeout && !ctx.Cancelled)
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
            if (ctx.Cancelled)
            {
                Cancelled(ctx, "before the screenshot was requested; nothing written");
                yield break;
            }
            if (!ready)
            {
                ctx.Output($"ERROR: code=capture_timeout name={name} pending={pending}");
                yield break;
            }
            // Grass: the clutter system places one patch per frame around the camera after
            // it moves, and a patch with nothing to place is never recorded, so "done" is
            // the patch count standing still for three frames (bounded to 3 s).
            int grassFrames = 0;
            int lastPatches = -1;
            int quiet = 0;
            Stopwatch grass = Stopwatch.StartNew();
            while (grass.Elapsed.TotalSeconds < 3 && !ctx.Cancelled)
            {
                int patches = GrassPatchCount();
                if (patches == lastPatches)
                {
                    if (++quiet >= 3) break;
                }
                else
                {
                    quiet = 0;
                }
                lastPatches = patches;
                grassFrames++;
                yield return null;
            }
            long readyMs = clock.ElapsedMilliseconds;
            // Two completed renders with the new pose, terrain, grass and weather before the capture.
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();

            // Capture to a file only this request writes; the final name is replaced
            // only by a verified, complete PNG, so a stale image can never pass.
            string path = CustomCommands.ResolveScreenshotPathPublic(name);
            string partPath = path + $".req{Math.Abs(ctx.Id)}.part";
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            try { if (File.Exists(partPath)) File.Delete(partPath); }
            catch (Exception ex)
            {
                ctx.Output($"ERROR: code=capture_part_exists message={ex.Message} path={partPath}");
                yield break;
            }
            ScreenCapture.CaptureScreenshot(partPath, supersize);

            // The PNG is written asynchronously: wait until it exists, stops growing and
            // carries the PNG signature and the IEND trailer. A cancelled request still waits
            // for its own write (bounded) so the file is not left half-written under the gate.
            long lastSize = -1;
            int stable = 0;
            bool complete = false;
            Stopwatch write = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < timeout || (ctx.Cancelled && write.Elapsed.TotalSeconds < 5))
            {
                yield return new WaitForSecondsRealtime(0.1f);
                long size = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                if (size > 0 && size == lastSize)
                {
                    stable++;
                    if (stable >= 2 && IsCompletePng(partPath))
                    {
                        complete = true;
                        break;
                    }
                }
                else
                {
                    stable = 0;
                }
                lastSize = size;
                if (ctx.Cancelled && write.Elapsed.TotalSeconds >= 5)
                    break;
            }
            if (ctx.Cancelled)
            {
                try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
                Cancelled(ctx, complete ? "screenshot written and discarded" : "screenshot write abandoned, partial file removed");
                yield break;
            }
            if (!complete)
            {
                ctx.Output($"ERROR: code=capture_timeout name={name} pending=file path={partPath} bytes={lastSize}");
                yield break;
            }
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(partPath, path);
            }
            catch (Exception ex)
            {
                ctx.Output($"ERROR: code=capture_replace_failed message={ex.Message} path={path} part={partPath}");
                yield break;
            }
            long finalSize = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (finalSize != lastSize || !IsCompletePng(path))
            {
                ctx.Output($"ERROR: code=capture_verify_failed path={path} bytes={finalSize} expected={lastSize}");
                yield break;
            }
            ctx.Output($"OK: CAPTURE name={name} path={path} bytes={finalSize} size={Screen.width * supersize}x{Screen.height * supersize} ready_ms={readyMs} frames_waited={frames} grass_frames={grassFrames} total_ms={clock.ElapsedMilliseconds}");
        }

        /// <summary>Grass patches the clutter system currently holds (0 when it is off or absent).</summary>
        public static int GrassPatchCount()
        {
            ClutterSystem clutter = ClutterSystem.instance;
            if (clutter == null || clutter.m_patches == null)
                return 0;
            return clutter.m_patches.Count;
        }

        /// <summary>PNG signature at the start and the IEND chunk at the end: the writer has finished.</summary>
        public static bool IsCompletePng(string path)
        {
            try
            {
                using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < 8 + 12) return false;
                byte[] head = new byte[8];
                if (fs.Read(head, 0, 8) != 8) return false;
                if (head[0] != 0x89 || head[1] != 0x50 || head[2] != 0x4E || head[3] != 0x47 || head[4] != 0x0D || head[5] != 0x0A || head[6] != 0x1A || head[7] != 0x0A)
                    return false;
                fs.Seek(-8, SeekOrigin.End);
                byte[] tail = new byte[8];
                if (fs.Read(tail, 0, 8) != 8) return false;
                return tail[0] == (byte)'I' && tail[1] == (byte)'E' && tail[2] == (byte)'N' && tail[3] == (byte)'D';
            }
            catch
            {
                return false;
            }
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

            // The HUD's black loading screen fades in over a teleport and out afterwards;
            // a frame captured through it is dark.
            Hud hud = Hud.instance;
            if (hud != null && hud.m_loadingScreen != null && hud.m_loadingScreen.gameObject.activeSelf && hud.m_loadingScreen.alpha > 0f)
                reasons.Add($"hud_fade:{hud.m_loadingScreen.alpha:F2}");
            if (hud != null && hud.m_damageScreen != null && hud.m_damageScreen.gameObject.activeSelf && hud.m_damageScreen.color.a > 0f)
                reasons.Add($"hud_damage_flash:{hud.m_damageScreen.color.a:F2}");
            Player player = Player.m_localPlayer;
            if (player != null && player.IsTeleporting())
                reasons.Add("player_teleporting");

            return string.Join(";", reasons);
        }

        // ---- cli_clear_view ----
        private static IEnumerator ClearView(Context ctx, float x, float z, float radius)
        {
            if (ZNetScene.instance == null)
            {
                ctx.Output("ERROR: world not loaded");
                yield break;
            }
            Dictionary<string, int> byKind = new Dictionary<string, int>();
            int removed = 0, passes = 0, remaining = 0;
            for (passes = 1; passes <= 3 && !ctx.Cancelled; passes++)
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
            if (ctx.Cancelled)
            {
                Cancelled(ctx, $"after {removed} removals");
                yield break;
            }
            string summary = string.Join(" ", byKind.Select(kv => $"{kv.Key}={kv.Value}"));
            ctx.Output($"OK: CLEAR_VIEW removed={removed} passes={Math.Min(passes, 3)} remaining={remaining} radius={radius:F0} at={x:F0},{z:F0} {summary}".TrimEnd());
        }

        // ---- cli_until ----
        private static IEnumerator Until(Context ctx, float timeout, string needle, string command)
        {
            valheimCLIPlugin? plugin = valheimCLIPlugin.Instance;
            if (plugin == null)
            {
                ctx.Output("ERROR: plugin not available");
                yield break;
            }
            Stopwatch clock = Stopwatch.StartNew();
            int polls = 0;
            List<string> last = new List<string>();
            while (!ctx.Cancelled)
            {
                polls++;
                last = plugin.RunCapturing(command);
                if (last.Any(l => l.Contains(needle)))
                {
                    foreach (string line in last) ctx.Output(line);
                    ctx.Output($"OK: UNTIL matched after {clock.ElapsedMilliseconds} ms polls={polls}");
                    yield break;
                }
                if (clock.Elapsed.TotalSeconds >= timeout)
                    break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            if (ctx.Cancelled)
            {
                Cancelled(ctx, $"after {polls} polls");
                yield break;
            }
            foreach (string line in last) ctx.Output(line);
            ctx.Output($"ERROR: code=until_timeout polls={polls} needle={needle}");
        }
    }
}
