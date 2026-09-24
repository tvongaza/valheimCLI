using System;
using System.Collections.Generic;
using System.Globalization;

namespace valheimCLI
{
    /// <summary>A position in world or piece-local space, without Unity, so placement maths can be tested.</summary>
    public readonly struct Point3
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        public Point3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static Point3 operator +(Point3 a, Point3 b) => new Point3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Point3 operator -(Point3 a, Point3 b) => new Point3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public float DistanceTo(Point3 other)
        {
            float dx = X - other.X, dy = Y - other.Y, dz = Z - other.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public string Format() => string.Format(CultureInfo.InvariantCulture, "({0:F3},{1:F3},{2:F3})", X, Y, Z);
    }

    /// <summary>
    /// Arguments of cli_build_place_at and cli_build_place_snapped, parsed and
    /// checked in plain .NET (see TerrainEditRequest for the same split).
    ///
    /// Both commands place a piece from coordinates rather than from the camera
    /// ray, so a bad number cannot be allowed to become a default: a yaw that
    /// failed to parse and silently became 0 would build a structure that looks
    /// right in a screenshot and is not what the script asked for.
    /// </summary>
    public sealed class PlacementRequest
    {
        public const string PlaceAtUsage = "Usage: cli_build_place_at <prefab-or-name|selected> <x> <y> <z> [yaw] [nocost]";
        public const string PlaceSnappedUsage = "Usage: cli_build_place_snapped <prefab-or-name|selected> <x> <y> <z> [yaw] [snapRadius] [nocost]";

        /// <summary>The hammer's own snap distance (Player.FindClosestSnapPoints is called with 0.5 m).</summary>
        public const float DefaultSnapRadius = 0.5f;

        /// <summary>The hammer looks for neighbouring snap points within 10 m of the ghost; a wider snap would reach pieces it never considers.</summary>
        public const float MaximumSnapRadius = 10f;

        public readonly string Piece;
        public readonly Point3 Position;
        public readonly float Yaw;
        public readonly float SnapRadius;
        public readonly bool NoCost;

        private PlacementRequest(string piece, Point3 position, float yaw, float snapRadius, bool noCost)
        {
            Piece = piece;
            Position = position;
            Yaw = yaw;
            SnapRadius = snapRadius;
            NoCost = noCost;
        }

        /// <summary>
        /// Parse the console argument list (command name at index 0). Optional
        /// arguments are positional; "nocost" may only come last, so a yaw can
        /// never be mistaken for a flag or the other way round.
        /// </summary>
        public static bool TryParse(IReadOnlyList<string>? args, bool snapped, out PlacementRequest? request, out string error)
        {
            request = null;
            string usage = snapped ? PlaceSnappedUsage : PlaceAtUsage;
            int optionalNumbers = snapped ? 2 : 1;
            if (args == null || args.Count < 5)
            {
                error = usage;
                return false;
            }

            int end = args.Count;
            bool noCost = false;
            if (IsNoCostToken(args[end - 1]))
            {
                noCost = true;
                end--;
            }

            if (end < 5 || end > 5 + optionalNumbers || string.IsNullOrWhiteSpace(args[1]))
            {
                error = usage;
                return false;
            }

            if (!TryNumber(args[2], "x", out float x, out error) ||
                !TryNumber(args[3], "y", out float y, out error) ||
                !TryNumber(args[4], "z", out float z, out error))
            {
                return false;
            }

            float yaw = 0f;
            if (end > 5 && !TryNumber(args[5], "yaw", out yaw, out error))
            {
                return false;
            }

            float snapRadius = DefaultSnapRadius;
            if (snapped && end > 6)
            {
                if (!TryNumber(args[6], "snapRadius", out snapRadius, out error))
                {
                    return false;
                }
                if (snapRadius <= 0f || snapRadius > MaximumSnapRadius)
                {
                    error = string.Format(CultureInfo.InvariantCulture, "ERROR: snapRadius must be greater than zero and at most {0:F0}", MaximumSnapRadius);
                    return false;
                }
            }

            request = new PlacementRequest(args[1], new Point3(x, y, z), yaw, snapRadius, noCost);
            error = string.Empty;
            return true;
        }

        public static bool IsNoCostToken(string value)
        {
            return value.Equals("nocost", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("--nocost", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryNumber(string text, string which, out float value, out string error)
        {
            if (!CommandArguments.TryFiniteFloat(text, out value))
            {
                value = 0f;
                error = $"ERROR: {which} must be a finite number, got '{text}'";
                return false;
            }
            error = string.Empty;
            return true;
        }
    }

    /// <summary>Arguments of cli_build_snap_points.</summary>
    public sealed class SnapPointsRequest
    {
        public const string Usage = "Usage: cli_build_snap_points <x> <y> <z> [radius=4] [nameFilter]";
        public const float DefaultRadius = 4f;

        public readonly Point3 Centre;
        public readonly float Radius;
        public readonly string NameFilter;

        private SnapPointsRequest(Point3 centre, float radius, string nameFilter)
        {
            Centre = centre;
            Radius = radius;
            NameFilter = nameFilter;
        }

        public static bool TryParse(IReadOnlyList<string>? args, out SnapPointsRequest? request, out string error)
        {
            request = null;
            if (args == null || args.Count < 4 || args.Count > 6)
            {
                error = Usage;
                return false;
            }

            if (!PlacementRequest.TryNumber(args[1], "x", out float x, out error) ||
                !PlacementRequest.TryNumber(args[2], "y", out float y, out error) ||
                !PlacementRequest.TryNumber(args[3], "z", out float z, out error))
            {
                return false;
            }

            float radius = DefaultRadius;
            if (args.Count >= 5 && !CommandArguments.TryRadius(args[4], out radius))
            {
                error = "ERROR: radius must be finite, greater than zero and at most 1024";
                return false;
            }

            request = new SnapPointsRequest(new Point3(x, y, z), radius, args.Count == 6 ? args[5] : string.Empty);
            error = string.Empty;
            return true;
        }
    }

    /// <summary>The closest pair between a new piece's snap points and its neighbours'.</summary>
    public readonly struct SnapMatch
    {
        public readonly int Mine;
        public readonly int Theirs;
        public readonly float Gap;

        public SnapMatch(int mine, int theirs, float gap)
        {
            Mine = mine;
            Theirs = theirs;
            Gap = gap;
        }
    }

    /// <summary>
    /// The geometry the hammer uses to snap, reimplemented over plain points so
    /// the rule is visible and tested rather than inferred from a private method.
    /// </summary>
    public static class SnapGeometry
    {
        /// <summary>The hammer's rotation increment: the scroll wheel turns a piece in steps of this many degrees.</summary>
        public const float HammerRotationDegrees = 22.5f;

        /// <summary>
        /// Rotate a vector about +Y by a yaw in degrees, as Unity's
        /// Quaternion.Euler(0, yaw, 0) does: +Z turns towards +X.
        /// </summary>
        public static Point3 RotateYaw(Point3 v, float yawDegrees)
        {
            double radians = yawDegrees * Math.PI / 180.0;
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);
            return new Point3(v.X * cos + v.Z * sin, v.Y, -v.X * sin + v.Z * cos);
        }

        /// <summary>
        /// Where a direct child of a piece ends up in the world when the piece
        /// stands at <paramref name="origin"/> with <paramref name="yawDegrees"/>.
        /// The prefab root's own scale applies: a stock prefab is not always at
        /// scale one, and its snap points move with it.
        /// </summary>
        public static Point3 ChildToWorld(Point3 origin, float yawDegrees, Point3 rootScale, Point3 localPosition)
        {
            Point3 scaled = new Point3(localPosition.X * rootScale.X, localPosition.Y * rootScale.Y, localPosition.Z * rootScale.Z);
            return origin + RotateYaw(scaled, yawDegrees);
        }

        /// <summary>
        /// Player.FindClosestSnapPoints' rule: for each of the new piece's snap
        /// points, the closest neighbouring point within <paramref name="maxGap"/>;
        /// of those, the closest pair wins. Ties keep the earlier point, as the
        /// game's strict comparisons do. <paramref name="nearestGap"/> is the
        /// closest pair at any distance, so a near miss can be reported as a number.
        /// </summary>
        public static bool TryClosestPair(IReadOnlyList<Point3> mine, IReadOnlyList<Point3> theirs, float maxGap,
            out SnapMatch match, out float nearestGap)
        {
            match = default;
            nearestGap = float.PositiveInfinity;
            bool found = false;
            float best = float.PositiveInfinity;
            for (int i = 0; i < mine.Count; i++)
            {
                for (int j = 0; j < theirs.Count; j++)
                {
                    float gap = mine[i].DistanceTo(theirs[j]);
                    if (gap < nearestGap)
                    {
                        nearestGap = gap;
                    }
                    if (gap <= maxGap && gap < best)
                    {
                        best = gap;
                        match = new SnapMatch(i, j, gap);
                        found = true;
                    }
                }
            }
            return found;
        }

        /// <summary>
        /// The hammer refuses to snap onto a spot where the same piece already
        /// stands (within 5 cm), unless the piece allows rotated overlap and the
        /// two differ by more than 10 degrees.
        /// </summary>
        public static bool OverlapsSamePiece(float distance, float angleDegrees, bool allowRotatedOverlap)
        {
            return distance < 0.05f && (!allowRotatedOverlap || angleDegrees <= 10f);
        }

        /// <summary>
        /// The scroll-wheel step that gives this yaw, or null when no step does.
        /// A player can only set a level piece to one of these headings, so a
        /// yaw between them is a placement no player can reproduce.
        /// </summary>
        public static int? HammerStep(float yawDegrees)
        {
            double steps = yawDegrees / HammerRotationDegrees;
            double rounded = Math.Round(steps);
            if (Math.Abs(steps - rounded) > 1e-3)
            {
                return null;
            }
            int step = (int)(rounded % 16);
            return step < 0 ? step + 16 : step;
        }
    }

    /// <summary>
    /// The rules a coordinate placement applies before the game's placement
    /// call: the ones that depend only on where the piece goes and what the
    /// player carries. The hammer's other checks (clipping, blocked by a
    /// player, space, ground type) are decided on its camera-driven ghost and
    /// are not applied here.
    /// </summary>
    public static class PlacementRules
    {
        /// <summary>The first rule that refuses the placement, as an error code, or null when none does.</summary>
        public static string? Refusal(bool terrainLoaded, bool insideNoBuildLocation, bool wardDeniesAccess, bool wrongBiome, bool missingRequirements)
        {
            // A piece created where no zone is loaded is not built into a live
            // world: nothing around it is there to support it or be checked.
            if (!terrainLoaded)
            {
                return "not_loaded";
            }
            if (insideNoBuildLocation)
            {
                return "no_build_zone";
            }
            if (wardDeniesAccess)
            {
                return "private_zone";
            }
            if (wrongBiome)
            {
                return "wrong_biome";
            }
            if (missingRequirements)
            {
                return "missing_requirements";
            }
            return null;
        }
    }
}
