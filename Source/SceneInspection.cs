using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace valheimCLI
{
    /// <summary>
    /// The plain-value half of the scene inspection commands (cli_solids_over,
    /// cli_piece_support, cli_rock_health, cli_rocks_at): argument parsing,
    /// geometry, the game's own rules restated over numbers, and the reply
    /// lines. Nothing here touches Unity, so all of it is unit-tested; the
    /// commands only gather the numbers and print what comes back.
    /// </summary>
    internal static class SceneGeometry
    {
        /// <summary>Horizontal distance from a point to an axis-aligned box; zero inside it.</summary>
        internal static float HorizontalDistanceToBox(float minX, float minZ, float maxX, float maxZ, float x, float z)
        {
            float dx = Math.Max(Math.Max(minX - x, 0f), x - maxX);
            float dz = Math.Max(Math.Max(minZ - z, 0f), z - maxZ);
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// Name, then position: two visits to an unchanged place print the same
        /// text, so a diff between two replies is a real difference and not the
        /// scene enumerating in another order.
        /// </summary>
        internal static int CompareNameThenPosition(string nameA, float ax, float ay, float az, string nameB, float bx, float by, float bz)
        {
            int byName = string.CompareOrdinal(nameA, nameB);
            if (byName != 0) return byName;
            if (ax != bx) return ax.CompareTo(bx);
            if (ay != by) return ay.CompareTo(by);
            return az.CompareTo(bz);
        }

        /// <summary>Lowest first, then by x and z: a structure reads from its footing up.</summary>
        internal static int CompareBottomUp(float ax, float ay, float az, float bx, float by, float bz)
        {
            if (ay != by) return ay.CompareTo(by);
            if (ax != bx) return ax.CompareTo(bx);
            return az.CompareTo(bz);
        }
    }

    /// <summary>
    /// cli_solids_over: an upright square column over each point, from
    /// <c>From</c> to <c>To</c> metres above the point's own height and
    /// <c>Half</c> metres to each side.
    /// </summary>
    internal sealed class SolidsOverRequest
    {
        internal const string Usage = "Usage: cli_solids_over <half> <from> <to> <x1> <y1> <z1> [<x2> <y2> <z2> ...]";

        internal float Half { get; private set; }
        internal float From { get; private set; }
        internal float To { get; private set; }
        internal List<float[]> Points { get; private set; } = new List<float[]>();

        /// <summary>Half the column's height; the box extent on the vertical axis.</summary>
        internal float HalfHeight => (To - From) * 0.5f;

        /// <summary>The column centre over point <paramref name="index"/>.</summary>
        internal float[] Centre(int index)
        {
            float[] p = Points[index];
            return new[] { p[0], p[1] + (From + To) * 0.5f, p[2] };
        }

        internal static bool TryParse(string[] args, out SolidsOverRequest request)
        {
            request = new SolidsOverRequest();
            if (args.Length < 7
                || !CommandArguments.TryFiniteFloat(args[1], out float half)
                || !CommandArguments.TryFiniteFloat(args[2], out float from)
                || !CommandArguments.TryFiniteFloat(args[3], out float to))
            {
                return false;
            }
            // The same bound as a census radius: a column is local work.
            if (half <= 0f || half > CommandArguments.MaximumRadius || to <= from || to - from > 2f * CommandArguments.MaximumRadius)
            {
                return false;
            }
            if (!CommandArguments.TryPoints(args, 4, 3, out List<float[]> points))
            {
                return false;
            }
            request.Half = half;
            request.From = from;
            request.To = to;
            request.Points = points;
            return true;
        }
    }

    /// <summary>
    /// Where a build piece's support number comes from and whether the game
    /// has computed it yet, restated from WearNTear so it can be tested.
    ///
    /// WearNTear.GetSupport answers with the full capacity when the object is
    /// not networked or nobody owns it, with the owner's live value on the
    /// owner, and with the owner's last saved value everywhere else. The owner
    /// recomputes support about once a second, but not for the first 30
    /// seconds after an instance appears unless it came through the build
    /// system (OnPlaced): a spawned piece, or one that just loaded with its
    /// zone, reads full capacity in that window. That number looks like a
    /// measurement and is an initial value, so the reply names it.
    /// </summary>
    internal static class PieceSupportReading
    {
        internal const string Usage = "Usage: cli_piece_support <x> <z> [radius=30] [nameFilter]";

        /// <summary>WearNTear.ShouldUpdate's grace after an instance is created.</summary>
        internal const float CreationGraceSeconds = 30f;

        internal static bool TryParse(string[] args, out float x, out float z, out float radius, out string? filter)
        {
            radius = 30f;
            filter = null;
            z = 0f;
            if (args.Length < 3 || args.Length > 5
                || !CommandArguments.TryFiniteFloat(args[1], out x)
                || !CommandArguments.TryFiniteFloat(args[2], out z))
            {
                x = 0f;
                return false;
            }
            if (args.Length >= 4 && !CommandArguments.TryRadius(args[3], out radius))
            {
                return false;
            }
            if (args.Length >= 5 && args[4].Length > 0)
            {
                filter = args[4];
            }
            return true;
        }

        /// <summary>
        /// computed: the owner is here and past the grace; it recomputes on every wear pass.
        /// pending: the owner is here but the instance is inside its creation grace.
        /// exempt: the piece takes no support wear, so the game never computes it.
        /// remote: another peer owns it; the value is what that peer last saved.
        /// unowned: nobody simulates it; the game reports full capacity.
        /// </summary>
        internal static string State(bool valid, bool hasOwner, bool isOwner, bool computesSupport, float createTime, float now, bool preSnow)
        {
            if (!computesSupport) return "exempt";
            if (!valid || !hasOwner) return "unowned";
            if (!isOwner) return "remote";
            return InGrace(createTime, now, preSnow) ? "pending" : "computed";
        }

        /// <summary>
        /// Whether the owner still skips this instance. Mirrors ShouldUpdate:
        /// OnPlaced sets the creation time to -1, a piece carrying pre-placed
        /// snow is updated from the start, and the grace ends strictly after
        /// 30 seconds.
        /// </summary>
        internal static bool InGrace(float createTime, float now, bool preSnow)
        {
            return createTime >= 0f && !preSnow && !(now - createTime > CreationGraceSeconds);
        }

        /// <summary>Seconds left in the creation grace; zero outside it.</summary>
        internal static float PendingSeconds(float createTime, float now, bool preSnow)
        {
            return InGrace(createTime, now, preSnow) ? CreationGraceSeconds - (now - createTime) : 0f;
        }

        /// <summary>WearNTear.HaveSupport: at or above the material's minimum.</summary>
        internal static bool Held(float support, float minimum) => support >= minimum;

        internal static string Row(string name, string zdo, float x, float y, float z,
            float support, float maximum, float minimum, string state, float pendingSeconds, float health)
        {
            string pending = state == "pending"
                ? string.Format(CultureInfo.InvariantCulture, " pending_s={0:F1}", pendingSeconds)
                : "";
            return string.Format(CultureInfo.InvariantCulture,
                "SUPPORT {0} zdo={1} pos={2:F3},{3:F3},{4:F3} support={5:F2} max={6:F2} min={7:F2} held={8} state={9}{10} health={11:F1}",
                name, zdo, x, y, z, support, maximum, minimum, Held(support, minimum) ? "yes" : "no", state, pending, health);
        }

        internal static string Summary(float x, float z, float radius, int pieces, int held, int pending)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "OK: PIECE_SUPPORT {0:F1},{1:F1} r={2:F1} pieces={3} held={4} unheld={5} pending={6}",
                x, z, radius, pieces, held, pieces - held, pending);
        }
    }

    /// <summary>
    /// A breakable boulder's (MineRock5) per-piece health as the game saves it:
    /// MineRock5.SaveHealth writes a ZPackage of the piece count followed by
    /// each piece's health (little-endian int32, then float32s), base64-encoded
    /// into the object's health string. No string means the rock was never hit.
    /// A piece at or below zero health is destroyed.
    /// </summary>
    internal static class MineRockHealth
    {
        internal const string Usage = "Usage: cli_rock_health <x> <z> [radius=30]";

        /// <summary>
        /// True with <paramref name="healths"/> null when nothing is saved; false
        /// when the string is not what SaveHealth writes. Trailing bytes are
        /// ignored, as MineRock5.LoadHealth ignores them.
        /// </summary>
        internal static bool TryDecode(string? text, out float[]? healths)
        {
            healths = null;
            if (string.IsNullOrEmpty(text))
            {
                return true;
            }
            try
            {
                byte[] bytes = Convert.FromBase64String(text);
                using BinaryReader reader = new BinaryReader(new MemoryStream(bytes));
                int count = reader.ReadInt32();
                if (count < 0 || (long)count * 4 > bytes.Length - 4)
                {
                    return false;
                }
                float[] values = new float[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = reader.ReadSingle();
                }
                healths = values;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (EndOfStreamException)
            {
                return false;
            }
        }

        /// <summary>Indices of the pieces with no health left; none for a rock never hit.</summary>
        internal static List<int> Destroyed(IReadOnlyList<float>? healths)
        {
            List<int> destroyed = new List<int>();
            if (healths == null) return destroyed;
            for (int i = 0; i < healths.Count; i++)
            {
                if (healths[i] <= 0f) destroyed.Add(i);
            }
            return destroyed;
        }

        internal static string Indices(List<int> values) => values.Count == 0 ? "-" : string.Join(",", values);

        /// <summary>
        /// One rock. <paramref name="live"/> is the loaded rock's piece health,
        /// null when the rock has no instance here; the live and saved answers
        /// match when the same pieces are destroyed in both.
        /// </summary>
        internal static string Row(string name, string zdo, float x, float y, float z,
            bool readable, float[]? saved, float[]? live, out bool mismatch)
        {
            mismatch = false;
            List<int> savedDestroyed = Destroyed(saved);
            string savedText = !readable ? "unreadable" : saved == null ? "none" : saved.Length.ToString(CultureInfo.InvariantCulture);
            string line = string.Format(CultureInfo.InvariantCulture,
                "ROCKHEALTH name={0} zdo={1} pos={2:F2},{3:F2},{4:F2} saved={5} destroyed={6}",
                name, zdo, x, y, z, savedText, readable ? Indices(savedDestroyed) : "?");
            if (live == null)
            {
                return line + " live=none";
            }
            List<int> liveDestroyed = Destroyed(live);
            string match = "?";
            if (readable)
            {
                mismatch = Indices(liveDestroyed) != Indices(savedDestroyed);
                match = mismatch ? "no" : "yes";
            }
            return line + string.Format(CultureInfo.InvariantCulture,
                " live={0} live_destroyed={1} match={2}", live.Length, Indices(liveDestroyed), match);
        }

        internal static string Summary(float x, float z, float radius, int rocks, int loaded, int mismatched, int unreadable)
        {
            return string.Format(CultureInfo.InvariantCulture,
                (unreadable == 0 ? "OK:" : "ERROR:") + " ROCK_HEALTH {0:F1},{1:F1} r={2:F1} rocks={3} loaded={4} mismatched={5} unreadable={6}",
                x, z, radius, rocks, loaded, mismatched, unreadable);
        }
    }
}
