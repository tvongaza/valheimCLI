using System;
using System.Collections.Generic;
using valheimCLI;
using Xunit;

namespace valheimCLI.Tests
{
    /// <summary>
    /// [Server] AllowOnServerClients waives Valheim 1.0's cheat restriction for
    /// valheimCLI's own commands on a dedicated-server client -- and waives
    /// nothing else.
    ///
    /// Ownership is the command OBJECT. Vanilla's constructor does
    /// commands[name.ToLower()] = this, so a name belongs to whoever registered
    /// it last: a plugin loading after us can take a name we recorded, and a
    /// name another plugin holds becomes ours the moment we replace it. The
    /// stand-in below plays the part of Terminal.ConsoleCommand, which this
    /// Unity-free test project cannot reference.
    /// </summary>
    public class CliCommandValidityTests : IDisposable
    {
        private sealed class Command
        {
            public readonly string Name;
            public Command(string name) => Name = name;
        }

        private readonly Command _ours = new Command("cli_teleport_peer");

        public CliCommandValidityTests()
        {
            CliCommandValidity.ForgetOwnCommands();
            CliCommandValidity.RecordOwnCommands(new object[] { _ours });
        }

        public void Dispose() => CliCommandValidity.ForgetOwnCommands();

        /// <summary>The case the option exists for: our cheat command on a server client.</summary>
        private bool Waives(bool allow = true, object? command = null, bool isCheat = true,
            bool cheatsEnabled = false, bool commandAllowed = true, bool isNetwork = false,
            bool haveNetwork = true, bool onlyServer = false, bool isServer = false) =>
            CliCommandValidity.WaivesCheatRestriction(allow, command ?? _ours, isCheat, cheatsEnabled,
                commandAllowed, isNetwork, haveNetwork, onlyServer, isServer);

        [Fact]
        public void OurCheatCommandOnAServerClientIsWaived()
        {
            Assert.True(Waives());
        }

        [Fact]
        public void TheOptionMustBeOn()
        {
            Assert.False(Waives(allow: false));
        }

        [Fact]
        public void AnotherPluginsCommandIsNotOursToRescue()
        {
            Assert.False(Waives(command: new Command("cli_something_elses")));
            // Straight to the rule: the helper's default would put one of ours
            // back in place of null and prove nothing.
            Assert.False(CliCommandValidity.WaivesCheatRestriction(true, null, true, false, true, false, true, false, false));
            Assert.False(CliCommandValidity.IsOwnCommand(new Command("cli_teleport_peer")));
        }

        /// <summary>
        /// The name is not the command. Another plugin registering OUR name
        /// replaces the object behind it; that object was never ours.
        /// </summary>
        [Fact]
        public void AForeignReplacementOfOneOfOurNamesIsRefused()
        {
            var replacement = new Command("cli_teleport_peer");
            Assert.True(Waives(command: _ours));
            Assert.False(Waives(command: replacement));
        }

        [Fact]
        public void OurReplacementOfAnExistingNameIsOurs()
        {
            var foreign = new Command("cli_teleport_peer");
            var before = new Dictionary<string, object> { ["cli_teleport_peer"] = foreign };
            var mine = new Command("cli_teleport_peer");
            var after = new Dictionary<string, object> { ["cli_teleport_peer"] = mine };

            List<object> ours = CliCommandValidity.NewlyRegistered(before, after);
            Assert.Equal(new object[] { mine }, ours);

            CliCommandValidity.ForgetOwnCommands();
            CliCommandValidity.RecordOwnCommands(ours);
            Assert.True(Waives(command: mine));
            Assert.False(Waives(command: foreign));
        }

        [Fact]
        public void AnUntouchedCommandIsNotClaimedAsOurs()
        {
            var foreign = new Command("someone_elses");
            var mine = new Command("cli_new");
            var before = new Dictionary<string, object> { ["someone_elses"] = foreign };
            var after = new Dictionary<string, object> { ["someone_elses"] = foreign, ["cli_new"] = mine };

            // Only the entry our registration actually added.
            Assert.Equal(new object[] { mine }, CliCommandValidity.NewlyRegistered(before, after));
        }

        [Fact]
        public void ACommandTheTerminalDisallowsStaysInvalid()
        {
            Assert.False(Waives(commandAllowed: false));
        }

        [Fact]
        public void AServerOnlyCommandStaysInvalidOnAClient()
        {
            Assert.False(Waives(onlyServer: true));
        }

        [Fact]
        public void ANetworkCommandWithoutAZNetStaysInvalid()
        {
            Assert.False(Waives(isNetwork: true, haveNetwork: false));
        }

        [Fact]
        public void NothingIsWaivedWhereCheatsAlreadyWork()
        {
            Assert.False(Waives(isServer: true));
            Assert.False(Waives(cheatsEnabled: true));
        }

        [Fact]
        public void ACommandThatIsNotACheatNeedsNoWaiver()
        {
            Assert.False(Waives(isCheat: false));
        }
    }
}
