using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace valheimCLI
{
    public static class CaptureCommands
    {
        // Weak keys keep this temporary override from retaining a departed world.
        private static readonly ConditionalWeakTable<ClutterSystem, Dictionary<ClutterSystem.Clutter, bool>> Originals =
            new ConditionalWeakTable<ClutterSystem, Dictionary<ClutterSystem.Clutter, bool>>();
        private static readonly List<WeakReference<ClutterSystem>> Owners = new List<WeakReference<ClutterSystem>>();

        private static void Restore(ClutterSystem clutter)
        {
            if (!Originals.TryGetValue(clutter, out Dictionary<ClutterSystem.Clutter, bool> saved)) return;
            foreach (var entry in saved) entry.Key.m_enabled = entry.Value;
            Originals.Remove(clutter);
            Owners.RemoveAll(owner => !owner.TryGetTarget(out var target) || ReferenceEquals(target, clutter));
        }

        /// <summary>Release temporary overrides before a replacement assembly loses the snapshots.</summary>
        public static void RestoreAll()
        {
            foreach (var owner in Owners.ToArray())
            {
                if (!owner.TryGetTarget(out var clutter) || clutter == null) continue;
                Restore(clutter);
                clutter.ClearAll();
            }
            Owners.Clear();
        }

        public static void Register()
        {
            new Terminal.ConsoleCommand("cli_clutter", "Switch ground clutter (grass, small plants) off or back on for clean terrain shots: cli_clutter <off|on>", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (args.Length != 2 || (args[1] != "off" && args[1] != "on"))
                {
                    args.Context.AddString("Usage: cli_clutter <off|on>");
                    return;
                }

                ClutterSystem? clutter = ClutterSystem.instance;
                if (clutter == null)
                {
                    args.Context.AddString("ERROR: ClutterSystem not available");
                    return;
                }

                bool on = args[1] == "on";
                if (!on)
                {
                    if (!Originals.TryGetValue(clutter, out Dictionary<ClutterSystem.Clutter, bool> saved))
                    {
                        saved = Originals.GetOrCreateValue(clutter);
                        Owners.RemoveAll(owner => !owner.TryGetTarget(out var target) || target == null);
                        Owners.Add(new WeakReference<ClutterSystem>(clutter));
                    }
                    foreach (ClutterSystem.Clutter entry in clutter.m_clutter)
                    {
                        if (!saved.ContainsKey(entry)) saved.Add(entry, entry.m_enabled);
                        entry.m_enabled = false;
                    }
                }
                else
                {
                    Restore(clutter);
                }

                clutter.ClearAll();
                args.Context.AddString($"OK: clutter {(on ? "restored" : "off")} (cleared, rebuilds as zones refresh)");
            }, isCheat: true);
        }
    }
}
