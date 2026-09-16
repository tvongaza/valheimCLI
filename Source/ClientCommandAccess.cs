using HarmonyLib;

namespace valheimCLI
{
    public static class ClientCommandAccess
    {
        public static bool AllowOnServerClients;
    }

    [HarmonyPatch(typeof(Terminal.ConsoleCommand), nameof(Terminal.ConsoleCommand.IsValid))]
    public static class ConsoleCommand_IsValid_Patch
    {
        public static void Postfix(Terminal.ConsoleCommand __instance, Terminal context, bool skipAllowedCheck,
            ref bool __result)
        {
            if (__result || !ClientCommandAccess.AllowOnServerClients || __instance == null)
            {
                return;
            }

            ZNet? net = ZNet.instance;
            bool haveNetwork = net != null;
            bool isServer = net != null && net.IsServer();
            bool cheatsEnabled = context != null && context.IsCheatsEnabled();
            bool commandAllowed = skipAllowedCheck || (context != null && context.isAllowedCommand(__instance));

            // The command OBJECT, not its name: a name can belong to whoever
            // registered it last (see CliCommandValidity).
            if (CliCommandValidity.WaivesCheatRestriction(ClientCommandAccess.AllowOnServerClients,
                    __instance, __instance.IsCheat, cheatsEnabled, commandAllowed,
                    __instance.IsNetwork, haveNetwork, __instance.OnlyServer, isServer))
            {
                __result = true;
            }
        }
    }
}
