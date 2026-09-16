using System.Reflection;

namespace valheimCLI
{
    public static class CaptureCommands
    {
        public static void Register()
        {
            new Terminal.ConsoleCommand("cli_clutter", "Switch ground clutter (grass, small plants) off or back on for clean terrain shots: cli_clutter <off|on>", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (args.Length != 2 || (args[1] != "off" && args[1] != "on"))
                {
                    args.Context.AddString("Usage: cli_clutter <off|on>");
                    return;
                }

                ClutterSystem clutter = ClutterSystem.instance;
                if (clutter == null)
                {
                    args.Context.AddString("ERROR: ClutterSystem not available");
                    return;
                }

                FieldInfo? enabledField = typeof(ClutterSystem).GetField("m_enabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (enabledField == null)
                {
                    args.Context.AddString("ERROR: ClutterSystem.m_enabled not found");
                    return;
                }

                bool on = args[1] == "on";
                enabledField.SetValue(clutter, on);
                clutter.ClearAll();
                args.Context.AddString($"OK: clutter {(on ? "on" : "off")} (cleared, rebuilds as zones refresh)");
            }, isCheat: true);
        }
    }
}
