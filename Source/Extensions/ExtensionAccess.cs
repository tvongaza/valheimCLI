namespace valheimCLI.Extensions
{
    /// <summary>Facts are gathered by the Unity host; policy is exercised without a game.</summary>
    public static class ExtensionAccess
    {
        public static string? Refusal(ExtensionCommand command, bool worldLoaded, bool haveNetwork,
            bool isServer, bool isDedicated, bool haveConsole, bool devcommands, bool allowOnServerClients)
        {
            if (command.NeedsWorld && !worldLoaded) return "A loaded world is required.";
            if (command.Role == ExtensionRole.Server && !isServer) return "Server role required.";
            if (command.Role == ExtensionRole.Client && (!haveNetwork || isDedicated)) return "Client role required.";
            if (!command.ReadOnly)
            {
                if (!haveConsole || !devcommands) return "Enable devcommands for mutating extension commands.";
                if (haveNetwork && !isServer && !allowOnServerClients)
                    return "Client mutation requires AllowOnServerClients on this test client.";
            }
            return null;
        }
    }
}
