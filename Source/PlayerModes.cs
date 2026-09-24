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
        Forwarded
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
        Landed
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
        public static ArriveStep NextArriveStep(bool accepted, bool teleporting, bool atTarget)
        {
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

        /// <summary>
        /// The error code when cli_arrive runs out of time. A teleport the game
        /// never accepted is a different failure from one that was accepted and
        /// never settled, and a script should be able to tell them apart.
        /// </summary>
        public static string ArriveTimeoutCode(bool everAccepted) => everAccepted ? "arrive_timeout" : "teleport_refused";

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
