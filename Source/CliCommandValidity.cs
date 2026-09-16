using System;
using System.Collections.Generic;

namespace valheimCLI
{
    /// <summary>
    /// Which commands [Server] AllowOnServerClients may rescue, and on what
    /// grounds. Plain .NET, no Unity: the rule is decided here and the Harmony
    /// patch only gathers the facts.
    ///
    /// Valheim 1.0's Terminal.ConsoleCommand.IsValid can refuse a command for
    /// four different reasons, in this order:
    ///
    ///   1. it is a cheat command and cheats are not enabled here,
    ///   2. the terminal does not allow this command (isAllowedCommand),
    ///   3. it is a network command and there is no ZNet,
    ///   4. it is server-only and this peer is not the server.
    ///
    /// Only (1) is what this option advertises waiving: on a client joined to a
    /// dedicated server IsCheatsEnabled is m_cheat and ZNet.IsServer(), so
    /// every cheat command is refused there, admin or not. A postfix that
    /// simply turned false into true waived all four, for any command whose
    /// name began with "cli_" -- including one registered by another plugin.
    /// So the waiver is granted only when the cheat gate is the sole reason,
    /// and only for commands valheimCLI itself registered.
    ///
    /// Ownership is the COMMAND OBJECT, never its name. The vanilla
    /// ConsoleCommand constructor does `Terminal.commands[name.ToLower()] =
    /// this`, which replaces whatever was registered under that name. Holding
    /// names would mean a plugin loading after us could take a name we recorded
    /// and inherit its waiver, and a name another plugin registered first would
    /// never be recorded as ours when we replaced it.
    /// </summary>
    public static class CliCommandValidity
    {
        /// <summary>Identity, not equality: two distinct command objects are never the same command.</summary>
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private static readonly HashSet<object> Own = new HashSet<object>(ReferenceComparer.Instance);

        /// <summary>
        /// The command objects valheimCLI registered. The plugin works them out
        /// by comparing Terminal.commands before and after its registration:
        /// an entry is ours when its name is new OR its object has been
        /// replaced by a different one.
        /// </summary>
        public static void RecordOwnCommands(IEnumerable<object> commands)
        {
            if (commands == null)
            {
                return;
            }
            foreach (object command in commands)
            {
                if (command != null)
                {
                    Own.Add(command);
                }
            }
        }

        public static void ForgetOwnCommands() => Own.Clear();

        public static int OwnCommandCount => Own.Count;

        public static bool IsOwnCommand(object? command) => command != null && Own.Contains(command);

        /// <summary>
        /// The command objects that OUR registration put into the table: every
        /// entry whose name was not there before, plus every entry whose object
        /// has been replaced by a different one. The second half is the point --
        /// registering a name another plugin already holds replaces their
        /// object with ours, and that command is ours however old the name is.
        /// Conversely a name we recorded earlier that now holds somebody else's
        /// object is simply not in this list.
        /// </summary>
        public static List<object> NewlyRegistered(IDictionary<string, object> before, IDictionary<string, object> after)
        {
            List<object> ours = new List<object>();
            if (after == null)
            {
                return ours;
            }
            foreach (KeyValuePair<string, object> entry in after)
            {
                if (entry.Value == null)
                {
                    continue;
                }
                if (before == null || !before.TryGetValue(entry.Key, out object? previous) ||
                    !ReferenceEquals(previous, entry.Value))
                {
                    ours.Add(entry.Value);
                }
            }
            return ours;
        }

        /// <summary>
        /// Whether the cheat restriction alone refused this command, so waiving
        /// it grants exactly what the option offers and nothing else. Every
        /// other condition vanilla checks is still required to pass.
        /// </summary>
        /// <param name="allowOnServerClients">the [Server] AllowOnServerClients option</param>
        /// <param name="command">the ConsoleCommand object being validated</param>
        /// <param name="isCheat">ConsoleCommand.IsCheat</param>
        /// <param name="cheatsEnabled">Terminal.IsCheatsEnabled() for this terminal</param>
        /// <param name="commandAllowed">isAllowedCommand, or the caller's skipAllowedCheck</param>
        /// <param name="isNetwork">ConsoleCommand.IsNetwork</param>
        /// <param name="haveNetwork">whether a ZNet exists</param>
        /// <param name="onlyServer">ConsoleCommand.OnlyServer</param>
        /// <param name="isServer">whether this peer is the server</param>
        public static bool WaivesCheatRestriction(bool allowOnServerClients, object? command, bool isCheat,
            bool cheatsEnabled, bool commandAllowed, bool isNetwork, bool haveNetwork, bool onlyServer, bool isServer)
        {
            if (!allowOnServerClients || !IsOwnCommand(command))
            {
                return false;
            }
            // Only on a client joined to a server: in a world this peer hosts,
            // cheats already work and nothing needs waiving.
            if (!haveNetwork || isServer)
            {
                return false;
            }
            // (1) The cheat gate has to be what refused it.
            if (!isCheat || cheatsEnabled)
            {
                return false;
            }
            // (2) (3) (4) still stand, as vanilla wrote them.
            if (!commandAllowed)
            {
                return false;
            }
            if (isNetwork && !haveNetwork)
            {
                return false;
            }
            if (onlyServer && !isServer)
            {
                return false;
            }
            return true;
        }
    }
}
