using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace valheimCLI
{
    public static class CaptureCommands
    {
        // Weak keys keep this temporary override from retaining a departed world.
        private static readonly ConditionalWeakTable<ClutterSystem, Dictionary<ClutterSystem.Clutter, bool>> Originals =
            new ConditionalWeakTable<ClutterSystem, Dictionary<ClutterSystem.Clutter, bool>>();

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
                    Dictionary<ClutterSystem.Clutter, bool> saved = Originals.GetOrCreateValue(clutter);
                    foreach (ClutterSystem.Clutter entry in clutter.m_clutter)
                    {
                        if (!saved.ContainsKey(entry)) saved.Add(entry, entry.m_enabled);
                        entry.m_enabled = false;
                    }
                }
                else if (Originals.TryGetValue(clutter, out Dictionary<ClutterSystem.Clutter, bool> saved))
                {
                    foreach (ClutterSystem.Clutter entry in clutter.m_clutter)
                    {
                        if (saved.TryGetValue(entry, out bool enabled)) entry.m_enabled = enabled;
                    }
                    Originals.Remove(clutter);
                }

                clutter.ClearAll();
                args.Context.AddString($"OK: clutter {(on ? "restored" : "off")} (cleared, rebuilds as zones refresh)");
            }, isCheat: true);
        }
    }
}
