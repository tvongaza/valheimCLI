using System;

namespace valheimCLI
{
    /// <summary>
    /// Registers cli_call. Everything it does lives in StaticMemberCall and
    /// CallValues (plain .NET, unit-tested); this class only supplies the
    /// console line, the loaded assemblies and the log.
    ///
    /// The command is cheat-gated like the other cli_ commands: in a world
    /// this game hosts, turn on devcommands first. On a dedicated server the
    /// console is reached only through the CLI port, and devcommands sent
    /// there enables it the same way.
    /// </summary>
    public static class CallCommands
    {
        private static readonly StaticMemberCall.TypeIndexCache Types = new StaticMemberCall.TypeIndexCache();

        public static void Register()
        {
            new Terminal.ConsoleCommand("cli_call", "Call a static method, or read a static field or property, of the game or any loaded mod and print the result (non-public members included; Vector3 as x,y,z; strings with spaces in double quotes): cli_call [--limit N] <[Namespace.]Type.Member> [arg ...]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                StaticMemberCall.Run(Types.For(AppDomain.CurrentDomain.GetAssemblies()), args.ArgsAll, args.Context.AddString,
                    (target, ex) => valheimCLIPlugin.Log.LogWarning($"cli_call {target} threw: {ex}"));
            }, isCheat: true);
        }
    }
}
