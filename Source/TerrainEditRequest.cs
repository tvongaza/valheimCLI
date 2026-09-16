using System;
using System.Collections.Generic;
using System.Globalization;

namespace valheimCLI
{
    /// <summary>
    /// cli_terrain_edit's arguments, parsed and checked here in plain .NET so
    /// the rule can be tested without Unity -- the same split CliCommandValidity
    /// uses, where the game-side code only gathers facts.
    ///
    /// The command asks the game to run ONE OF ITS OWN terrain operations, the
    /// ones a hoe or pickaxe uses, at a point. It deliberately does not invent an
    /// edit or write heightmap data itself: a test built on a home-made edit
    /// would only show that something preserves home-made edits.
    ///
    /// Coordinates must be finite. A NaN or infinity here would be handed
    /// straight to the terrain system, and "it did something strange" is the
    /// worst possible outcome for a test whose whole job is to say whether an
    /// edit survived.
    /// </summary>
    public sealed class TerrainEditRequest
    {
        /// <summary>Name of a terrain op the game has registered (cli_terrain_ops lists them).</summary>
        public readonly string Op;
        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        private TerrainEditRequest(string op, float x, float y, float z)
        {
            Op = op;
            X = x;
            Y = y;
            Z = z;
        }

        public const string Usage = "Usage: cli_terrain_edit <op> <x> <y> <z>";

        /// <summary>
        /// Parse the console argument list, which arrives with the command name
        /// at index 0 exactly as Terminal hands it over.
        /// </summary>
        public static bool TryParse(IReadOnlyList<string>? args, out TerrainEditRequest? request, out string error)
        {
            request = null;
            if (args == null || args.Count != 5)
            {
                error = Usage;
                return false;
            }

            string op = args[1];
            if (string.IsNullOrWhiteSpace(op))
            {
                error = "ERROR: no terrain op named: " + Usage;
                return false;
            }

            if (!TryCoordinate(args[2], "x", out float x, out error) ||
                !TryCoordinate(args[3], "y", out float y, out error) ||
                !TryCoordinate(args[4], "z", out float z, out error))
            {
                return false;
            }

            request = new TerrainEditRequest(op.Trim(), x, y, z);
            error = string.Empty;
            return true;
        }

        private static bool TryCoordinate(string text, string which, out float value, out string error)
        {
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                value = 0f;
                error = $"ERROR: {which} is not a number: '{text}'";
                return false;
            }
            // Rejected rather than passed on: the terrain system would take it.
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                value = 0f;
                error = $"ERROR: {which} must be a finite number, got '{text}'";
                return false;
            }
            error = string.Empty;
            return true;
        }

        public string Describe() =>
            string.Format(CultureInfo.InvariantCulture, "{0} at {1:F1},{2:F1},{3:F1}", Op, X, Y, Z);
    }
}
