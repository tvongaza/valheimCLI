using System;
using System.Collections.Generic;
using System.Globalization;

namespace valheimCLI
{
    /// <summary>Validation for actions that must reject bad input before touching a world.</summary>
    internal static class SessionArguments
    {
        internal static bool TryYaw(string? text, out float yaw)
        {
            yaw = 0f;
            return text == null || CommandArguments.TryFiniteFloat(text, out yaw);
        }

        internal static bool TryCartLoadCount(string text, out int count)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out count)
                && count > 0 && count <= 10000;
        }

        internal static IEnumerable<int> StackBatches(int count, int maximumStack)
        {
            if (count <= 0 || maximumStack <= 0)
            {
                throw new ArgumentOutOfRangeException();
            }
            while (count > 0)
            {
                int batch = Math.Min(count, maximumStack);
                yield return batch;
                count -= batch;
            }
        }

        internal static bool CartLoadComplete(int requested, int before, int after)
        {
            return requested > 0 && after - before == requested;
        }
    }
}
