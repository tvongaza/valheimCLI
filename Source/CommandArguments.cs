using System;
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
    }
}
