using System;
using System.Globalization;

namespace valheimCLI
{
    /// <summary>
    /// Where a world dump takes its samples, and how a window is asked for.
    ///
    /// The game samples its island grid at i*128 - 10000 and a road pathfinder
    /// walks cells at multiples of 8, and 10000 is a whole number of 8s, so one
    /// origin serves both: a sample at -10000 + i*step is a position the game
    /// itself asks about. A reader offline can then return that value unchanged
    /// instead of interpolating toward it, which is the difference between a
    /// dump that reproduces the world and one that approximates it.
    ///
    /// Plain .NET on purpose: this is the part of the dump that has to be right,
    /// and it is tested without the game.
    /// </summary>
    internal static class WorldDumpGrid
    {
        public const float Origin = -10000f;

        /// <summary>First lattice index at or above a coordinate.</summary>
        public static int From(float coordinate, int step) =>
            (int)Math.Ceiling((coordinate - Origin) / step);

        /// <summary>Last lattice index at or below a coordinate.</summary>
        public static int To(float coordinate, int step) =>
            (int)Math.Floor((coordinate - Origin) / step);

        /// <summary>The world coordinate of a lattice index.</summary>
        public static float At(int index, int step) => Origin + index * step;

        /// <summary>
        /// A window as "cx,cz,half". Rejected rather than guessed at when it is
        /// not three numbers with a positive half-size.
        /// </summary>
        public static bool TryParseWindow(string spec, out float centerX, out float centerZ, out float half)
        {
            centerX = 0f;
            centerZ = 0f;
            half = 0f;

            if (string.IsNullOrWhiteSpace(spec))
            {
                return false;
            }

            string[] parts = spec.Split(',');
            if (parts.Length != 3)
            {
                return false;
            }

            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out centerX)
                || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out centerZ)
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out half)
                || half <= 0f)
            {
                return false;
            }

            return true;
        }
    }
}
