using System;
using System.Collections.Generic;
using System.Globalization;

namespace valheimCLI
{
    /// <summary>Numeric bounds shared by inspection and test-action commands.</summary>
    internal static class CommandArguments
    {
        // A census is local work, not an unbounded scan of the whole world.
        internal const float MaximumRadius = 1024f;

        internal static bool TryFiniteFloat(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        internal static bool TryRadius(string text, out float value)
        {
            return TryFiniteFloat(text, out value) && value > 0f && value <= MaximumRadius;
        }

        internal static bool CanScan(float x, float z, float radius)
        {
            // Vector2s stores sector coordinates in shorts. Refuse wrapped sectors.
            const float limit = 32000f * 64f;
            return Math.Abs(x) + radius < limit && Math.Abs(z) + radius < limit;
        }

        internal static float CapsuleExtent(float radius, float height, int direction, int axis)
        {
            return direction == axis ? Math.Max(radius, height * 0.5f) : radius;
        }

        /// <summary>
        /// A run of points written as consecutive numbers from <paramref name="start"/>
        /// to the end of the arguments, <paramref name="dimensions"/> numbers per
        /// point. At least one point, no partial point, every number finite.
        /// </summary>
        internal static bool TryPoints(string[] args, int start, int dimensions, out List<float[]> points)
        {
            points = new List<float[]>();
            int count = args.Length - start;
            if (dimensions < 1 || start < 0 || count <= 0 || count % dimensions != 0)
            {
                return false;
            }
            for (int i = start; i < args.Length; i += dimensions)
            {
                float[] point = new float[dimensions];
                for (int d = 0; d < dimensions; d++)
                {
                    if (!TryFiniteFloat(args[i + d], out point[d]))
                    {
                        points.Clear();
                        return false;
                    }
                }
                points.Add(point);
            }
            return true;
        }

        /// <summary>A whole number from zero to <paramref name="maximum"/>, invariant culture.</summary>
        internal static bool TryCount(string text, int maximum, out int value)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                && value >= 0 && value <= maximum;
        }
    }
}
