using System;
using System.Collections.Generic;
using System.Globalization;

namespace valheimCLI
{
    /// <summary>
    /// A parsed <c>cli_rock_hit</c> request and the reply lines it writes. Plain C# (no Unity), so the
    /// argument rules and the reply text are tested without a game.
    /// </summary>
    public sealed class RockHitRequest
    {
        public const string Usage = "Usage: cli_rock_hit <x> <y> <z> <radius> [damage=1000]";
        public const float MaximumRadius = 64f;
        public const float DefaultDamage = 1000f;

        public float X { get; private set; }
        public float Y { get; private set; }
        public float Z { get; private set; }
        public float Radius { get; private set; }
        public float Damage { get; private set; } = DefaultDamage;

        /// <summary>args[0] is the command name. A radius above 0 up to 64, a damage above 0.</summary>
        public static bool TryParse(IReadOnlyList<string> args, out RockHitRequest request, out string error)
        {
            request = new RockHitRequest();
            error = Usage;
            if (args.Count < 5 || args.Count > 6)
            {
                return false;
            }
            if (!CommandArguments.TryFiniteFloat(args[1], out float x)
                || !CommandArguments.TryFiniteFloat(args[2], out float y)
                || !CommandArguments.TryFiniteFloat(args[3], out float z))
            {
                error = "ERROR: x y z must be numbers. " + Usage;
                return false;
            }
            if (!CommandArguments.TryRadius(args[4], out float radius) || radius > MaximumRadius)
            {
                error = "ERROR: radius must be above 0 and at most " + F(MaximumRadius) + ". " + Usage;
                return false;
            }
            float damage = DefaultDamage;
            if (args.Count == 6 && (!CommandArguments.TryFiniteFloat(args[5], out damage) || damage <= 0f))
            {
                error = "ERROR: damage must be a number above 0. " + Usage;
                return false;
            }
            request.X = x;
            request.Y = y;
            request.Z = z;
            request.Radius = radius;
            request.Damage = damage;
            error = string.Empty;
            return true;
        }

        /// <summary>The last reply line: how many rocks the sphere held and was hit.</summary>
        public string Summary(int rocksHit)
        {
            return rocksHit == 0
                ? "OK: 0 rock(s) inside the sphere"
                : $"OK: {rocksHit} rock(s) hit at {F(X)},{F(Y)},{F(Z)} r={F(Radius)} damage={F(Damage)}";
        }

        /// <summary>The row for a whole rock swapped for its fractured copy.</summary>
        public static string SwapRow(string name, float x, float y, float z, float scale, bool owner, string fracturedName)
        {
            return $"  ROCKHIT swap {name} pos={F(x)},{F(y)},{F(z)} scale={F(scale)} owner={owner} -> {fracturedName}";
        }

        /// <summary>The row for a breakable boulder hit: pieces with health before and after.</summary>
        public static string PiecesRow(string name, float x, float y, float z, float scale, bool owner, int pieces, int leftBefore, int leftAfter)
        {
            return $"  ROCKHIT pieces {name} pos={F(x)},{F(y)},{F(z)} scale={F(scale)} owner={owner} pieces={pieces} left_before={leftBefore} left_after={leftAfter}";
        }

        private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
    }
}
