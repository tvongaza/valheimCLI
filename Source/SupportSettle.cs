using System;
using System.Collections.Generic;
using System.Globalization;

namespace valheimCLI
{
    /// <summary>
    /// cli_piece_support_settle's rules, in plain .NET so they can be tested
    /// without the game.
    ///
    /// The game recomputes a piece's structural support about once a second,
    /// and not at all for the first 30 s after a piece is created by anything
    /// other than the hammer. A test that builds and then asks "does this
    /// stand?" would have to wait. The command recomputes support now, with the
    /// game's own WearNTear.UpdateSupport, in an order that lets one pass carry
    /// support up a column.
    ///
    /// UpdateSupport reads each neighbour's STORED support. Asked top-first, a
    /// column's top piece reads its neighbour's stale value; asked bottom-up,
    /// every piece reads a neighbour settled in the same pass. Sideways paths
    /// still need more than one pass, so passes repeat until nothing changes or
    /// the limit is reached.
    /// </summary>
    public sealed class SupportSettleRequest
    {
        public const string Usage = "Usage: cli_piece_support_settle <x> <z> [radius=10] [passes=3] [nameFilter]";
        public const float DefaultRadius = 10f;
        public const int DefaultPasses = 3;
        public const int MaximumPasses = 20;

        public readonly float X;
        public readonly float Z;
        public readonly float Radius;
        public readonly int Passes;
        public readonly string NameFilter;

        private SupportSettleRequest(float x, float z, float radius, int passes, string nameFilter)
        {
            X = x;
            Z = z;
            Radius = radius;
            Passes = passes;
            NameFilter = nameFilter;
        }

        public static bool TryParse(IReadOnlyList<string>? args, out SupportSettleRequest? request, out string error)
        {
            request = null;
            if (args == null || args.Count < 3 || args.Count > 6)
            {
                error = Usage;
                return false;
            }

            if (!PlacementRequest.TryNumber(args[1], "x", out float x, out error) ||
                !PlacementRequest.TryNumber(args[2], "z", out float z, out error))
            {
                return false;
            }

            float radius = DefaultRadius;
            if (args.Count >= 4 && !CommandArguments.TryRadius(args[3], out radius))
            {
                error = "ERROR: radius must be finite, greater than zero and at most 1024";
                return false;
            }

            int passes = DefaultPasses;
            if (args.Count >= 5 &&
                (!int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out passes) || passes < 1 || passes > MaximumPasses))
            {
                error = $"ERROR: passes must be a whole number from 1 to {MaximumPasses}";
                return false;
            }

            request = new SupportSettleRequest(x, z, radius, passes, args.Count == 6 ? args[5] : string.Empty);
            error = string.Empty;
            return true;
        }
    }

    /// <summary>One piece as the settle order sees it.</summary>
    public readonly struct SettlePiece
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        /// <summary>A stable identity (the ZDO id's text), the last tie-breaker.</summary>
        public readonly string Id;

        public SettlePiece(float x, float y, float z, string id)
        {
            X = x;
            Y = y;
            Z = z;
            Id = id;
        }
    }

    public static class SupportSettle
    {
        /// <summary>
        /// The order to settle in: lowest first. Pieces at the same height are
        /// ordered by x, z and identity, so the scene's enumeration order never
        /// decides which one is asked first.
        /// </summary>
        public static int[] BottomUpOrder(IReadOnlyList<SettlePiece> pieces)
        {
            int[] order = new int[pieces.Count];
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }
            Array.Sort(order, (a, b) =>
            {
                SettlePiece pa = pieces[a], pb = pieces[b];
                int c = pa.Y.CompareTo(pb.Y);
                if (c == 0) c = pa.X.CompareTo(pb.X);
                if (c == 0) c = pa.Z.CompareTo(pb.Z);
                if (c == 0) c = string.CompareOrdinal(pa.Id, pb.Id);
                return c;
            });
            return order;
        }

        /// <summary>
        /// Run passes until one changes nothing or <paramref name="maxPasses"/>
        /// have run. <paramref name="pass"/> performs one pass and says whether
        /// any support value changed. A pass that changed nothing means the
        /// values are settled; the limit alone does not.
        /// </summary>
        public static int RunPasses(int maxPasses, Func<bool> pass, out bool converged)
        {
            converged = false;
            int run = 0;
            while (run < maxPasses)
            {
                run++;
                if (!pass())
                {
                    converged = true;
                    break;
                }
            }
            return run;
        }

        /// <summary>Whether two support readings differ enough to count as a change.</summary>
        public static bool Changed(float before, float after) => Math.Abs(before - after) > 1e-4f;

        public static string PieceLine(string prefab, string zdo, float x, float y, float z, float support, float max, float min, bool held, bool owned) =>
            string.Format(CultureInfo.InvariantCulture,
                "SUPPORT piece={0} zdo={1} pos=({2:F3},{3:F3},{4:F3}) support={5:F2} max={6:F2} min={7:F2} held={8} owner={9}",
                prefab, zdo, x, y, z, support, max, min, held, owned ? "local" : "remote");

        public static string SummaryLine(int reported, int settled, int skippedRemote, int held, int unheld, int passesRun, bool converged, float radius) =>
            string.Format(CultureInfo.InvariantCulture,
                "OK: PIECE_SUPPORT_SETTLE reported={0} settled={1} skippedRemote={2} held={3} unheld={4} passes={5} converged={6} radius={7:F1}",
                reported, settled, skippedRemote, held, unheld, passesRun, converged, radius);
    }
}
