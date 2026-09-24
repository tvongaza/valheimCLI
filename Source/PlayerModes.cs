using System;
using System.Collections.Generic;

namespace valheimCLI
{
    /// <summary>What Player.TeleportTo said to a teleport request.</summary>
    public enum TeleportAnswer
    {
        Accepted,
        /// <summary>Refused: a teleport is still running.</summary>
        InProgress,
        /// <summary>Refused: the game allows a new teleport only 2 s after the last one finished.</summary>
        Cooldown,
        /// <summary>This peer does not own the character, so the game forwarded the request to the owner.</summary>
        Forwarded,
        /// <summary>Not offered: the first-spawn intro is running and the valkyrie carries the player (cli_skip_intro ends it).</summary>
        Intro,
        /// <summary>Not offered: the player is attached (seat, bed, ship's helm, saddle) and the attachment holds it in place.</summary>
        Attached,
        /// <summary>Not offered: the player is dead.</summary>
        Dead
    }

    /// <summary>
    /// A player state in which a teleport cannot stick, whatever TeleportTo
    /// answers. TeleportTo accepts in all of these, moves the player, and
    /// something else puts it back: the intro valkyrie sets the player's
    /// position every frame until it drops them, and an attachment snaps the
    /// player to its attach point every frame.
    /// </summary>
    public enum TeleportBlock
    {
        None,
        Intro,
        Attached,
        Dead
    }

    /// <summary>What cli_arrive does on a frame, given what the player is doing.</summary>
    public enum ArriveStep
    {
        /// <summary>No teleport has been accepted yet: offer it again.</summary>
        Offer,
        /// <summary>An accepted teleport is still running.</summary>
        InFlight,
        /// <summary>An accepted teleport finished away from the target (the game put the player back).</summary>
        Bounced,
        /// <summary>At the target; wait for the zones.</summary>
        Landed,
        /// <summary>The player is in a state no teleport survives (see TeleportBlock): give up with its reason.</summary>
        Blocked
    }

    /// <summary>
    /// The teleport and player-mode rules, in plain .NET so they can be tested
    /// without the game.
    ///
    /// Player.TeleportTo returns false instead of teleporting while a teleport
    /// is running and for 2 s after one finishes, and on a peer that does not
    /// own the character it forwards the request and also returns false. A
    /// command that ignores the return value reports a teleport that never
    /// happens; one that retries on "not at the target" mistakes a refusal for
    /// a bounce and spends its retries in a single frame.
    /// </summary>
    public static class PlayerModes
    {
        /// <summary>
        /// Seconds a landing must hold before cli_arrive reports it. Something
        /// that holds the player (an attachment, a cutscene) moves it back
        /// within a frame or two of the landing; one second also lets the
        /// set-down on the ground come to rest.
        /// </summary>
        public const float LandingSettleSeconds = 1f;

        /// <summary>How far a held landing may drift: horizontally, and vertically (settling onto the ground from the set-down height).</summary>
        public const float LandingHorizontalTolerance = 2f;
        public const float LandingVerticalTolerance = 1.5f;

        /// <summary>The first state that makes a teleport pointless, in order of what is reported.</summary>
        public static TeleportBlock Blocker(bool inIntro, bool valkyrieCarrying, bool attached, bool dead)
        {
            if (dead)
            {
                return TeleportBlock.Dead;
            }
            if (inIntro || valkyrieCarrying)
            {
                return TeleportBlock.Intro;
            }
            return attached ? TeleportBlock.Attached : TeleportBlock.None;
        }

        /// <summary>
        /// Ask for a teleport. A blocked player is refused before the game is
        /// asked: TeleportTo would accept, and the teleport would be undone.
        /// </summary>
        public static TeleportAnswer RequestTeleport(TeleportBlock block, Func<bool> teleportTo, Func<bool> ownsCharacter, Func<bool> teleporting)
        {
            if (block != TeleportBlock.None)
            {
                return BlockAnswer(block);
            }
            bool accepted = teleportTo();
            return ClassifyTeleport(accepted, ownsCharacter(), teleporting());
        }

        public static TeleportAnswer ClassifyTeleport(bool accepted, bool ownsCharacter, bool teleporting)
        {
            if (accepted)
            {
                return TeleportAnswer.Accepted;
            }
            if (!ownsCharacter)
            {
                return TeleportAnswer.Forwarded;
            }
            return teleporting ? TeleportAnswer.InProgress : TeleportAnswer.Cooldown;
        }

        /// <summary>The reason, for an ERROR line or a pending status.</summary>
        public static string DescribeRefusal(TeleportAnswer answer)
        {
            switch (answer)
            {
                case TeleportAnswer.InProgress:
                    return "a teleport is still in progress";
                case TeleportAnswer.Cooldown:
                    return "the game allows a teleport 2 s after the last one finished";
                case TeleportAnswer.Forwarded:
                    return "this peer does not own the character; the request was forwarded to its owner";
                case TeleportAnswer.Intro:
                    return "the first-spawn intro is in progress; the valkyrie holds the player until it drops them (cli_skip_intro ends it)";
                case TeleportAnswer.Attached:
                    return "the player is attached (seat, bed, helm or saddle) and would be held in place";
                case TeleportAnswer.Dead:
                    return "the player is dead";
                default:
                    return "accepted";
            }
        }

        /// <summary>Whether offering the same teleport again later can succeed. A forwarded request cannot: it will be forwarded again.</summary>
        public static bool WorthRetrying(TeleportAnswer answer) =>
            answer == TeleportAnswer.InProgress || answer == TeleportAnswer.Cooldown;

        /// <summary>
        /// One frame of cli_arrive. Only a teleport the game accepted can
        /// bounce; before that, the player not being at the target means
        /// nothing has happened yet.
        /// </summary>
        public static ArriveStep NextArriveStep(TeleportBlock block, bool accepted, bool teleporting, bool atTarget)
        {
            if (block != TeleportBlock.None)
            {
                return ArriveStep.Blocked;
            }
            if (!accepted)
            {
                return ArriveStep.Offer;
            }
            if (teleporting)
            {
                return ArriveStep.InFlight;
            }
            return atTarget ? ArriveStep.Landed : ArriveStep.Bounced;
        }

        /// <summary>The refusal a blocked arrival reports.</summary>
        public static TeleportAnswer BlockAnswer(TeleportBlock block)
        {
            switch (block)
            {
                case TeleportBlock.Intro:
                    return TeleportAnswer.Intro;
                case TeleportBlock.Attached:
                    return TeleportAnswer.Attached;
                case TeleportBlock.Dead:
                    return TeleportAnswer.Dead;
                default:
                    return TeleportAnswer.Accepted;
            }
        }

        /// <summary>
        /// Whether a declared landing still holds on a later frame: nothing
        /// blocks the player, no teleport runs, and the player is still where
        /// it landed.
        /// </summary>
        public static bool LandingHeld(TeleportBlock block, bool teleporting, float horizontalDrift, float verticalDrift) =>
            block == TeleportBlock.None && !teleporting &&
            horizontalDrift <= LandingHorizontalTolerance && Math.Abs(verticalDrift) <= LandingVerticalTolerance;

        /// <summary>
        /// The error code when cli_arrive runs out of time. A teleport the game
        /// never accepted is a different failure from one that was accepted and
        /// never settled, and a script should be able to tell them apart.
        /// </summary>
        public static string ArriveTimeoutCode(bool everAccepted) => everAccepted ? "arrive_timeout" : "teleport_refused";

        /// <summary>
        /// Whether any part of the first-spawn intro is still ahead or under
        /// way: queued (the game shows it once the start area is nearly
        /// generated), its text showing, the valkyrie carrying the player, or
        /// the player still flagged as in the intro.
        /// </summary>
        public static bool IntroActive(bool queued, bool showing, bool valkyrieCarrying, bool playerInIntro) =>
            queued || showing || valkyrieCarrying || playerInIntro;

        /// <summary>
        /// Whether cli_skip_intro can report the player free. Skipping asks the
        /// game to respawn the player, which happens on a later frame: until a
        /// new player stands in the world (the one from before the skip is
        /// destroyed), the old one still reads as spawned. A player that was
        /// never replaced because nothing was skipped counts as respawned.
        /// </summary>
        public static bool IntroSkipSettled(bool playerSpawned, bool respawnedSinceSkip, bool waitingForRespawn, bool introActive) =>
            playerSpawned && respawnedSinceSkip && !waitingForRespawn && !introActive;

        /// <summary>What cli_skip_intro is still waiting for, for its timeout line.</summary>
        public static string IntroSkipPending(bool playerSpawned, bool respawnedSinceSkip, bool waitingForRespawn, bool introActive)
        {
            if (introActive)
            {
                return "the intro is still active";
            }
            if (!playerSpawned || !respawnedSinceSkip || waitingForRespawn)
            {
                return "the player has not respawned";
            }
            return "nothing";
        }

        public const string FlyUsage = "Usage: cli_fly [on|off|toggle]";

        /// <summary>
        /// cli_fly's argument. No argument reports the state without changing
        /// it; "toggle" must be asked for by name, so a script that means "on"
        /// can never turn fly off by running twice.
        /// </summary>
        public static bool TryFlyTarget(IReadOnlyList<string>? args, bool current, out bool target, out bool changeRequested, out string error)
        {
            target = current;
            changeRequested = false;
            error = string.Empty;
            if (args == null || args.Count <= 1)
            {
                return true;
            }
            if (args.Count > 2)
            {
                error = FlyUsage;
                return false;
            }

            string value = args[1].Trim().ToLowerInvariant();
            switch (value)
            {
                case "on":
                case "true":
                    target = true;
                    break;
                case "off":
                case "false":
                    target = false;
                    break;
                case "toggle":
                    target = !current;
                    break;
                default:
                    error = FlyUsage;
                    return false;
            }
            changeRequested = true;
            return true;
        }

        public static string FlyLine(bool fly, bool changed) => $"OK: fly={fly} changed={changed}";

        /// <summary>
        /// cli_set_player_safety's reply. Every flag the command touches is on
        /// the line with the value read back from the game, so a script checks
        /// what is actually set instead of trusting what it asked for. The
        /// values print as True/False, like every other flag this mod reports.
        /// </summary>
        public static string SafetyLine(bool enabled, bool god, bool ghost, bool debugMode, bool cheats)
        {
            string prefix = SafetyApplied(enabled, god, ghost, debugMode, cheats) ? "OK:" : "ERROR: code=safety_not_applied";
            return $"{prefix} playerSafety enabled={enabled} god={god} ghost={ghost} debugMode={debugMode} cheats={cheats}";
        }

        /// <summary>Whether the modes read back are the ones cli_set_player_safety asked for. Cheats are only ever switched on, never off.</summary>
        public static bool SafetyApplied(bool enabled, bool god, bool ghost, bool debugMode, bool cheats) =>
            god == enabled && ghost == enabled && debugMode == enabled && (!enabled || cheats);
    }
}
