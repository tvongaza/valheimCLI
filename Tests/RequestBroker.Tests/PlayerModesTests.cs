using System.Collections.Generic;
using valheimCLI;
using Xunit;

namespace valheimCLI.Tests
{
    /// <summary>
    /// Teleport outcomes, cli_arrive's per-frame decision, cli_fly's argument
    /// and cli_set_player_safety's reply: the parts of those commands decided
    /// without the game.
    /// </summary>
    public class PlayerModesTests
    {
        [Fact]
        public void AnAcceptedTeleportIsAccepted()
        {
            Assert.Equal(TeleportAnswer.Accepted, PlayerModes.ClassifyTeleport(accepted: true, ownsCharacter: true, teleporting: true));
        }

        /// <summary>The same false return means three different things; the command has to say which.</summary>
        [Fact]
        public void ARefusalSaysWhy()
        {
            Assert.Equal(TeleportAnswer.InProgress, PlayerModes.ClassifyTeleport(false, true, teleporting: true));
            Assert.Equal(TeleportAnswer.Cooldown, PlayerModes.ClassifyTeleport(false, true, teleporting: false));
            Assert.Equal(TeleportAnswer.Forwarded, PlayerModes.ClassifyTeleport(false, ownsCharacter: false, teleporting: false));
            Assert.Contains("in progress", PlayerModes.DescribeRefusal(TeleportAnswer.InProgress));
            Assert.Contains("2 s", PlayerModes.DescribeRefusal(TeleportAnswer.Cooldown));
            Assert.Contains("forwarded", PlayerModes.DescribeRefusal(TeleportAnswer.Forwarded));
        }

        [Fact]
        public void OnlyATemporaryRefusalIsWorthOfferingAgain()
        {
            Assert.True(PlayerModes.WorthRetrying(TeleportAnswer.InProgress));
            Assert.True(PlayerModes.WorthRetrying(TeleportAnswer.Cooldown));
            Assert.False(PlayerModes.WorthRetrying(TeleportAnswer.Forwarded));
        }

        /// <summary>
        /// The failure this fixes: a refused teleport left the player where they
        /// were, which read as "bounced back", and the retries were spent in one
        /// frame. Until the game accepts, the only step is to offer again.
        /// </summary>
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void BeforeAcceptanceNothingCanBounce(bool teleporting, bool atTarget)
        {
            Assert.Equal(ArriveStep.Offer, PlayerModes.NextArriveStep(accepted: false, teleporting, atTarget));
        }

        [Fact]
        public void AfterAcceptanceTheTeleportRunsThenLandsOrBounces()
        {
            Assert.Equal(ArriveStep.InFlight, PlayerModes.NextArriveStep(true, teleporting: true, atTarget: false));
            Assert.Equal(ArriveStep.InFlight, PlayerModes.NextArriveStep(true, teleporting: true, atTarget: true));
            Assert.Equal(ArriveStep.Bounced, PlayerModes.NextArriveStep(true, teleporting: false, atTarget: false));
            Assert.Equal(ArriveStep.Landed, PlayerModes.NextArriveStep(true, teleporting: false, atTarget: true));
        }

        [Fact]
        public void ATimeoutSaysWhetherTheGameEverAcceptedTheTeleport()
        {
            Assert.Equal("teleport_refused", PlayerModes.ArriveTimeoutCode(everAccepted: false));
            Assert.Equal("arrive_timeout", PlayerModes.ArriveTimeoutCode(everAccepted: true));
        }

        private static List<string> Args(params string[] tokens) => new List<string>(tokens);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NoArgumentReportsWithoutChanging(bool current)
        {
            Assert.True(PlayerModes.TryFlyTarget(Args("cli_fly"), current, out bool target, out bool change, out string error));
            Assert.Equal(current, target);
            Assert.False(change);
            Assert.Equal("", error);
        }

        /// <summary>An explicit state is the same whatever the current one is, so running it twice is harmless.</summary>
        [Theory]
        [InlineData("on", true)]
        [InlineData("ON", true)]
        [InlineData("true", true)]
        [InlineData("off", false)]
        [InlineData("False", false)]
        public void AnExplicitStateIsIdempotent(string argument, bool expected)
        {
            foreach (bool current in new[] { false, true })
            {
                Assert.True(PlayerModes.TryFlyTarget(Args("cli_fly", argument), current, out bool target, out bool change, out _));
                Assert.Equal(expected, target);
                Assert.True(change);
            }
        }

        [Fact]
        public void ToggleMustBeAskedForByName()
        {
            Assert.True(PlayerModes.TryFlyTarget(Args("cli_fly", "toggle"), false, out bool target, out _, out _));
            Assert.True(target);
            Assert.True(PlayerModes.TryFlyTarget(Args("cli_fly", "toggle"), true, out target, out _, out _));
            Assert.False(target);
        }

        [Theory]
        [InlineData("yes")]
        [InlineData("1")]
        [InlineData("")]
        public void AnythingElseGivesTheUsage(string argument)
        {
            Assert.False(PlayerModes.TryFlyTarget(Args("cli_fly", argument), false, out bool target, out bool change, out string error));
            Assert.False(target);
            Assert.False(change);
            Assert.Equal(PlayerModes.FlyUsage, error);
            Assert.False(PlayerModes.TryFlyTarget(Args("cli_fly", "on", "off"), false, out _, out _, out error));
            Assert.Equal(PlayerModes.FlyUsage, error);
        }

        [Fact]
        public void TheFlyLineGivesTheStateReadBack()
        {
            Assert.Equal("OK: fly=True changed=True", PlayerModes.FlyLine(true, true));
            Assert.Equal("OK: fly=False changed=False", PlayerModes.FlyLine(false, false));
        }

        /// <summary>Every flag the command sets is on the line, in the True/False form this mod prints elsewhere.</summary>
        [Fact]
        public void TheSafetyLineNamesEveryFlagItSets()
        {
            Assert.Equal("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True",
                PlayerModes.SafetyLine(true, true, true, true, true));
        }

        /// <summary>Turning safety off leaves cheats as they were: a devcommands the user ran is not undone.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SafetyOffDoesNotRequireCheatsOff(bool cheats)
        {
            Assert.StartsWith("OK: ", PlayerModes.SafetyLine(false, false, false, false, cheats));
        }

        [Theory]
        [InlineData(false, true, true, true)]
        [InlineData(true, false, true, true)]
        [InlineData(true, true, false, true)]
        [InlineData(true, true, true, false)]
        public void AFlagThatDidNotTakeIsAnError(bool god, bool ghost, bool debugMode, bool cheats)
        {
            Assert.StartsWith("ERROR: code=safety_not_applied playerSafety enabled=True", PlayerModes.SafetyLine(true, god, ghost, debugMode, cheats));
            Assert.False(PlayerModes.SafetyApplied(true, god, ghost, debugMode, cheats));
        }
    }
}
