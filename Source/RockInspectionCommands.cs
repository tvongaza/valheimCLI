using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace valheimCLI
{
    /// <summary>
    /// What is left of the rocks near a point, asked of the rocks rather than
    /// inferred from rays or screenshots.
    ///
    /// A breakable boulder (MineRock5) is one object made of many pieces, its
    /// hit areas. Each piece keeps its own health, saved in the object's ZDO;
    /// a piece with no health left has its collider deactivated and is gone.
    /// cli_rocks_at reads the live rocks loaded here; cli_rock_health reads
    /// the saved health, which a dedicated server and a client can both
    /// answer, and compares it with the live rock where one is loaded.
    /// </summary>
    public static class RockInspectionCommands
    {
        private const string RocksAtUsage = "Usage: cli_rocks_at <x> <z> [radius=30]";

        public static void Register()
        {
            new Terminal.ConsoleCommand("cli_rocks_at", "Rocks loaded near a point and what is left of them: each breakable boulder's remaining pieces with their health and world bounds, other rocks' solid bounds, and the items lying on the ground: cli_rocks_at <x> <z> [radius=30]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                RocksAt(args, args.Context.AddString);
            });

            new Terminal.ConsoleCommand("cli_rock_health", "Which pieces of every breakable boulder near a point are destroyed, from its saved object (the same answer on a server and a client) and, where the rock is loaded, from the live rock too: cli_rock_health <x> <z> [radius=30]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                RockHealth(args, args.Context.AddString);
            });
        }

        public static void RockHealth(Terminal.ConsoleEventArgs args, Action<string> output)
        {
            if (args.Length < 3 || args.Length > 4 || !CommandArguments.TryFiniteFloat(args[1], out float x) || !CommandArguments.TryFiniteFloat(args[2], out float z))
            {
                output(MineRockHealth.Usage);
                return;
            }
            float radius = 30f;
            if (args.Length >= 4 && !CommandArguments.TryRadius(args[3], out radius))
            {
                output(MineRockHealth.Usage);
                return;
            }
            if (!CommandArguments.CanScan(x, z, radius))
            {
                output("ERROR: census coordinates exceed the supported sector range");
                return;
            }
            if (ZDOMan.instance == null || ZNetScene.instance == null)
            {
                output("ERROR: no world loaded");
                return;
            }

            // Every saved object near the point whose prefab is a MineRock5. On
            // a client that is every such object the server has sent.
            List<ZDO> rocks = new List<ZDO>();
            List<ZDO> scratch = new List<ZDO>();
            HashSet<ZoneSystem.SectorIndex> visited = new HashSet<ZoneSystem.SectorIndex>();
            Vector2s min = ZoneSystem.GetZone(new Vector3(x - radius, 0f, z - radius));
            Vector2s max = ZoneSystem.GetZone(new Vector3(x + radius, 0f, z + radius));
            for (int zx = min.x; zx <= max.x; zx++)
            {
                for (int zy = min.y; zy <= max.y; zy++)
                {
                    scratch.Clear();
                    ZDOMan.instance.FindObjects(new Vector2s(zx, zy), scratch, visited);
                    foreach (ZDO zdo in scratch)
                    {
                        Vector3 pos = zdo.GetPosition();
                        if ((pos.x - x) * (pos.x - x) + (pos.z - z) * (pos.z - z) > radius * radius)
                        {
                            continue;
                        }
                        GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                        if (prefab != null && prefab.GetComponent<MineRock5>() != null)
                        {
                            rocks.Add(zdo);
                        }
                    }
                }
            }
            rocks.Sort((a, b) =>
            {
                Vector3 pa = a.GetPosition(), pb = b.GetPosition();
                return SceneGeometry.CompareNameThenPosition(
                    ZNetScene.instance.GetPrefab(a.GetPrefab()).name, pa.x, pa.y, pa.z,
                    ZNetScene.instance.GetPrefab(b.GetPrefab()).name, pb.x, pb.y, pb.z);
            });

            int loaded = 0, mismatched = 0, unreadable = 0;
            foreach (ZDO zdo in rocks)
            {
                Vector3 pos = zdo.GetPosition();
                bool readable = MineRockHealth.TryDecode(zdo.GetString(ZDOVars.s_health), out float[]? saved);
                if (!readable)
                {
                    unreadable++;
                }

                float[]? live = null;
                ZNetView? view = ZNetScene.instance.FindInstance(zdo);
                MineRock5? rock = view != null ? view.GetComponent<MineRock5>() : null;
                if (rock != null && rock.m_hitAreas != null)
                {
                    loaded++;
                    live = new float[rock.m_hitAreas.Count];
                    for (int i = 0; i < live.Length; i++)
                    {
                        live[i] = rock.m_hitAreas[i].m_health;
                    }
                }

                output(MineRockHealth.Row(ZNetScene.instance.GetPrefab(zdo.GetPrefab()).name, zdo.m_uid.ToString(),
                    pos.x, pos.y, pos.z, readable, saved, live, out bool mismatch));
                if (mismatch)
                {
                    mismatched++;
                }
            }
            output(MineRockHealth.Summary(x, z, radius, rocks.Count, loaded, mismatched, unreadable));
        }

        public static void RocksAt(Terminal.ConsoleEventArgs args, Action<string> output)
        {
            if (args.Length < 3 || args.Length > 4 || !CommandArguments.TryFiniteFloat(args[1], out float x) || !CommandArguments.TryFiniteFloat(args[2], out float z))
            {
                output(RocksAtUsage);
                return;
            }
            float radius = 30f;
            if (args.Length >= 4 && !CommandArguments.TryRadius(args[3], out radius))
            {
                output(RocksAtUsage);
                return;
            }
            if (ZNetScene.instance == null)
            {
                output("ERROR: no world loaded");
                return;
            }

            // Collected first and printed in name-then-position order, so the
            // same place gives the same text on every visit.
            List<Found> rocks = new List<Found>();

            foreach (MineRock5 rock in UnityEngine.Object.FindObjectsByType<MineRock5>(FindObjectsSortMode.None))
            {
                if (rock.m_hitAreas == null)
                {
                    continue;
                }
                List<string> near = new List<string>();
                int left = 0;
                for (int i = 0; i < rock.m_hitAreas.Count; i++)
                {
                    MineRock5.HitArea area = rock.m_hitAreas[i];
                    if (area.m_health <= 0f)
                    {
                        continue;
                    }
                    left++;
                    if (area.m_collider == null)
                    {
                        continue;
                    }
                    Bounds b = area.m_collider.bounds;
                    if (SceneGeometry.HorizontalDistanceToBox(b.min.x, b.min.z, b.max.x, b.max.z, x, z) > radius)
                    {
                        continue;
                    }
                    near.Add(FormattableString.Invariant(
                        $"piece={i} health={area.m_health:F1} min={b.min.x:F2},{b.min.y:F2},{b.min.z:F2} max={b.max.x:F2},{b.max.y:F2},{b.max.z:F2}"));
                }
                if (near.Count == 0)
                {
                    continue;
                }
                Vector3 p = rock.transform.position;
                rocks.Add(new Found(PrefabName(rock.gameObject), p, FormattableString.Invariant(
                    $"kind=MineRock5 pos={p.x:F2},{p.y:F2},{p.z:F2} pieces={rock.m_hitAreas.Count} left={left} near={near.Count} {Zdo(rock.gameObject)}"), near));
            }

            // Rocks without per-piece health: a MineRock is always a rock; a
            // Destructible is one only by its name (the game has no rock type
            // for it), so this half is a heuristic and says which kind matched.
            foreach (Type kind in new[] { typeof(MineRock), typeof(Destructible) })
            {
                foreach (UnityEngine.Object found in UnityEngine.Object.FindObjectsByType(kind, FindObjectsSortMode.None))
                {
                    GameObject go = ((Component)found).gameObject;
                    string name = PrefabName(go);
                    if (kind == typeof(Destructible) && name.IndexOf("rock", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    bool any = false;
                    Bounds all = default;
                    foreach (Collider collider in go.GetComponentsInChildren<Collider>())
                    {
                        if (!collider.enabled || collider.isTrigger)
                        {
                            continue;
                        }
                        if (!any)
                        {
                            all = collider.bounds;
                            any = true;
                        }
                        else
                        {
                            all.Encapsulate(collider.bounds);
                        }
                    }
                    if (!any || SceneGeometry.HorizontalDistanceToBox(all.min.x, all.min.z, all.max.x, all.max.z, x, z) > radius)
                    {
                        continue;
                    }
                    Vector3 p = go.transform.position;
                    rocks.Add(new Found(name, p, FormattableString.Invariant(
                        $"kind={kind.Name} pos={p.x:F2},{p.y:F2},{p.z:F2} min={all.min.x:F2},{all.min.y:F2},{all.min.z:F2} max={all.max.x:F2},{all.max.y:F2},{all.max.z:F2} {Zdo(go)}"), null));
                }
            }

            List<Found> drops = new List<Found>();
            foreach (ItemDrop drop in UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None))
            {
                Vector3 p = drop.transform.position;
                if ((p.x - x) * (p.x - x) + (p.z - z) * (p.z - z) > radius * radius)
                {
                    continue;
                }
                drops.Add(new Found(PrefabName(drop.gameObject), p, FormattableString.Invariant(
                    $"stack={drop.m_itemData.m_stack} pos={p.x:F2},{p.y:F2},{p.z:F2} {Zdo(drop.gameObject)}"), null));
            }

            rocks.Sort(Found.Compare);
            drops.Sort(Found.Compare);
            int pieces = 0;
            for (int r = 0; r < rocks.Count; r++)
            {
                output($"ROCK {r + 1} name={rocks[r].Name} {rocks[r].Text}");
                if (rocks[r].Pieces == null)
                {
                    continue;
                }
                foreach (string line in rocks[r].Pieces!)
                {
                    pieces++;
                    output($"ROCKPIECE rock={r + 1} {line}");
                }
            }
            foreach (Found drop in drops)
            {
                output($"DROP name={drop.Name} {drop.Text}");
            }

            output(FormattableString.Invariant($"OK: ROCKS_AT {x:F1},{z:F1} r={radius:F1} rocks={rocks.Count} pieces_near={pieces} drops={drops.Count}"));
        }

        private sealed class Found
        {
            internal readonly string Name;
            internal readonly Vector3 Position;
            internal readonly string Text;
            internal readonly List<string>? Pieces;

            internal Found(string name, Vector3 position, string text, List<string>? pieces)
            {
                Name = name;
                Position = position;
                Text = text;
                Pieces = pieces;
            }

            internal static int Compare(Found a, Found b)
            {
                return SceneGeometry.CompareNameThenPosition(
                    a.Name, a.Position.x, a.Position.y, a.Position.z,
                    b.Name, b.Position.x, b.Position.y, b.Position.z);
            }
        }

        /// <summary>
        /// The prefab an object was made from, by its saved object. The
        /// GameObject name is not reliable: MineRock5 renames its own when it
        /// builds its combined mesh.
        /// </summary>
        internal static string PrefabName(GameObject go)
        {
            ZNetView view = go.GetComponentInParent<ZNetView>();
            ZDO? zdo = view != null ? view.GetZDO() : null;
            GameObject? prefab = zdo != null && ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            return prefab != null ? prefab.name : CustomCommands.CleanPrefabName((view != null ? view.gameObject : go).name).Trim().Replace(' ', '_');
        }

        /// <summary>The object's network identity, or zdo=none for a purely local object.</summary>
        internal static string Zdo(GameObject go)
        {
            ZNetView view = go.GetComponentInParent<ZNetView>();
            ZDO? zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            return zdo != null ? "zdo=" + zdo.m_uid.ToString() : "zdo=none";
        }
    }
}
