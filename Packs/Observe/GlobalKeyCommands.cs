using System;
using System.Collections;
using System.Collections.Generic;
using valheimCLI.Extensions;

namespace valheimCLI.Observe
{
    /// <summary>A read-only list of global keys held by this server or client.</summary>
    public static class GlobalKeyCommands
    {
        public const string Source = "global-keys";
        public static ExtensionCommand List(string name = "globalkeys") =>
            new ExtensionCommand(name, "List this process's global keys", ListKeys, readOnly: true, needsWorld: true);

        public static List<string> Keys()
        {
            var system = ZoneSystem.instance ?? throw new InvalidOperationException("No ZoneSystem: load a world first.");
            var keys = system.GetGlobalKeys();
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        internal static IEnumerator ListKeys(ExtensionContext context)
        {
            if (context.Arguments.Count != 0) { context.Fail("usage", "takes no arguments"); yield break; }
            context.Succeed(new Dictionary<string, object?>
            {
                ["source"] = Source, ["complete"] = true,
                ["server"] = ZNet.instance != null && ZNet.instance.IsServer(), ["keys"] = Keys(),
            });
        }
    }
}
