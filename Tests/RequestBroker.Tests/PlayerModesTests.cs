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
            Assert.Equal(ArriveStep.Offer, PlayerModes.NextArriveStep(TeleportBlock.None, accepted: false, teleporting, atTarget));
        }

        [Fact]
        public void AfterAcceptanceTheTeleportRunsThenLandsOrBounces()
        {
            Assert.Equal(ArriveStep.InFlight, PlayerModes.NextArriveStep(TeleportBlock.None, true, teleporting: true, atTarget: false));
            Assert.Equal(ArriveStep.InFlight, PlayerModes.NextArriveStep(TeleportBlock.None, true, teleporting: true, atTarget: true));
            Assert.Equal(ArriveStep.Bounced, PlayerModes.NextArriveStep(TeleportBlock.None, true, teleporting: false, atTarget: false));
            Assert.Equal(ArriveStep.Landed, PlayerModes.NextArriveStep(TeleportBlock.None, true, teleporting: false, atTarget: true));
        }

        /// <summary>
        /// The failure this fixes: during the first-spawn intro TeleportTo
        /// accepts, the player is moved, and the valkyrie carries it straight
        /// back. The old request asked the game and reported Accepted; a blocked
        /// player must be refused without asking.
        /// </summary>
        [Fact]
        public void AnIntroRefusesTheTeleportWithoutAskingTheGame()
        {
            bool asked = false;
            TeleportAnswer answer = PlayerModes.RequestTeleport(TeleportBlock.Intro, () => { asked = true; return true; }, () => true, () => true);
            Assert.Equal(TeleportAnswer.Intro, answer);
            Assert.False(asked);
            Assert.Contains("intro", PlayerModes.DescribeRefusal(answer));
            Assert.Contains("cli_skip_intro", PlayerModes.DescribeRefusal(answer));
            Assert.False(PlayerModes.WorthRetrying(answer));
        }

        /// <summary>Each stage of the intro counts: queued before it shows, the text, the ride, the player's own flag.</summary>
        [Theory]
        [InlineData(true, false, false, false)]
        [InlineData(false, true, false, false)]
        [InlineData(false, false, true, false)]
        [InlineData(false, false, false, true)]
        public void AnyStageOfTheIntroIsActive(bool queued, bool showing, bool carrying, bool playerInIntro)
        {
            Assert.True(PlayerModes.IntroActive(queued, showing, carrying, playerInIntro));
        }

        [Fact]
        public void NoStageMeansNoIntro()
        {
            Assert.False(PlayerModes.IntroActive(false, false, false, false));
        }

        /// <summary>
        /// The failure this guards against: the skip asks for a respawn on a
        /// later frame, and the player from before the skip still stands in
        /// the world meanwhile. Reporting it would hand the caller a player
        /// that is about to be destroyed.
        /// </summary>
        [Fact]
        public void TheOldPlayerIsNotTheRespawnedOne()
        {
            Assert.False(PlayerModes.IntroSkipSettled(playerSpawned: true, respawnedSinceSkip: false, waitingForRespawn: false, introActive: false));
            Assert.Equal("the player has not respawned", PlayerModes.IntroSkipPending(true, false, false, false));
        }

        [Theory]
        [InlineData(false, true, false, false, "the player has not respawned")]
        [InlineData(true, true, true, false, "the player has not respawned")]
        [InlineData(true, true, false, true, "the intro is still active")]
        [InlineData(false, false, true, true, "the intro is still active")]
        public void ASkipWaitsForTheRespawnAndTheIntroToEnd(bool spawned, bool respawned, bool waiting, bool introActive, string pending)
        {
            Assert.False(PlayerModes.IntroSkipSettled(spawned, respawned, waiting, introActive));
            Assert.Equal(pending, PlayerModes.IntroSkipPending(spawned, respawned, waiting, introActive));
        }

        [Fact]
        public void ANewPlayerOnTheGroundWithNoIntroIsSettled()
        {
            Assert.True(PlayerModes.IntroSkipSettled(playerSpawned: true, respawnedSinceSkip: true, waitingForRespawn: false, introActive: false));
        }

        [Fact]
        public void AnUnblockedRequestStillAsksTheGame()
        {
            bool asked = false;
            Assert.Equal(TeleportAnswer.Accepted, PlayerModes.RequestTeleport(TeleportBlock.None, () => { asked = true; return true; }, () => true, () => true));
            Assert.True(asked);
            Assert.Equal(TeleportAnswer.Cooldown, PlayerModes.RequestTeleport(TeleportBlock.None, () => false, () => true, () => false));
        }

        [Theory]
        [InlineData(TeleportBlock.Attached, TeleportAnswer.Attached, "attached")]
        [InlineData(TeleportBlock.Dead, TeleportAnswer.Dead, "dead")]
        public void OtherStatesThatUndoATeleportAreRefusedToo(TeleportBlock block, TeleportAnswer expected, string reason)
        {
            TeleportAnswer answer = PlayerModes.RequestTeleport(block, () => true, () => true, () => false);
            Assert.Equal(expected, answer);
            Assert.Contains(reason, PlayerModes.DescribeRefusal(answer));
            Assert.False(PlayerModes.WorthRetrying(answer));
            Assert.Equal(expected, PlayerModes.BlockAnswer(block));
        }

        [Fact]
        public void TheBlockerIsReadFromThePlayersState()
        {
            Assert.Equal(TeleportBlock.None, PlayerModes.Blocker(false, false, false, false));
            Assert.Equal(TeleportBlock.Intro, PlayerModes.Blocker(inIntro: true, false, false, false));
            Assert.Equal(TeleportBlock.Intro, PlayerModes.Blocker(false, valkyrieCarrying: true, false, false));
            Assert.Equal(TeleportBlock.Attached, PlayerModes.Blocker(false, false, attached: true, false));
            Assert.Equal(TeleportBlock.Dead, PlayerModes.Blocker(true, true, true, dead: true));
            Assert.Equal(TeleportAnswer.Accepted, PlayerModes.BlockAnswer(TeleportBlock.None));
        }

        /// <summary>
        /// The in-game failure: an intro player at the target with the zones
        /// loaded read as Landed, and the valkyrie carried it away a moment
        /// later. Whatever else is true, a blocked player has not landed.
        /// </summary>
        [Theory]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        public void ABlockedPlayerNeverLands(bool accepted, bool teleporting, bool atTarget)
        {
            Assert.Equal(ArriveStep.Blocked, PlayerModes.NextArriveStep(TeleportBlock.Intro, accepted, teleporting, atTarget));
            Assert.Equal(ArriveStep.Blocked, PlayerModes.NextArriveStep(TeleportBlock.Attached, accepted, teleporting, atTarget));
        }

        [Fact]
        public void ALandingHoldsOnlyWhereItLanded()
        {
            Assert.True(PlayerModes.LandingHeld(TeleportBlock.None, false, 0.1f, -0.3f));
            Assert.True(PlayerModes.LandingHeld(TeleportBlock.None, false, 2f, 1.5f));
            // Carried 170 m up by the valkyrie, as observed in game.
            Assert.False(PlayerModes.LandingHeld(TeleportBlock.None, false, 0.2f, 170.4f));
            Assert.False(PlayerModes.LandingHeld(TeleportBlock.None, false, 2.5f, 0f));
            Assert.False(PlayerModes.LandingHeld(TeleportBlock.None, false, 0f, -1.6f));
            Assert.False(PlayerModes.LandingHeld(TeleportBlock.None, teleporting: true, 0f, 0f));
            Assert.False(PlayerModes.LandingHeld(TeleportBlock.Intro, false, 0f, 0f));
            Assert.Equal(1f, PlayerModes.LandingSettleSeconds);
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
