using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace valheimCLI
{
    public static class TerrainInspectionCommands
    {
        public static void Register()
        {
            new Terminal.ConsoleCommand("cli_ground_height", "Terrain height under points, from this game's own terrain colliders (what a player here stands on): cli_ground_height <x> <z> [<x> <z> ...]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                GroundHeights(args, args.Context.AddString);
            });

            new Terminal.ConsoleCommand("cli_piece_geometry", "Measure a build piece without placing one: its collider extent in the prefab's OWN local space and every snap point vanilla would snap to: cli_piece_geometry <prefab> [<prefab> ...]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                PieceGeometry(args, args.Context.AddString);
            });

            new Terminal.ConsoleCommand("cli_surface_at", "What a player would stand on at a point: every collider a ray straight down hits, nearest first, with its ZDO: cli_surface_at <x> <z> [fromY=200]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                SurfaceAt(args, args.Context.AddString);
            });

            new Terminal.ConsoleCommand("cli_paint_at", "The terrain paint a client has at a point, from its own heightmap: cli_paint_at <x> <z> [<x> <z> ...]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                PaintAt(args, args.Context.AddString);
            });

            new Terminal.ConsoleCommand("cli_solids_over", "Every solid object standing over a set of points, by the game's own overlap test against the real collider shapes (not their bounding boxes): a column <half> metres to each side of every point, from <from> to <to> metres above it. A breakable boulder's piece is named by its index: cli_solids_over <half> <from> <to> <x1> <y1> <z1> [<x2> <y2> <z2> ...]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                SolidsOver(args, args.Context.AddString);
            });

            new Terminal.ConsoleCommand("cli_area_ready", "Whether the game counts the area round a point as ready (its zone loaded and every saved object in it and its neighbours instantiated), and which objects are still without an instance: cli_area_ready <x> <z> [list=10]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                AreaReady(args, args.Context.AddString);
            });

            new Terminal.ConsoleCommand("cli_piece_support", "What holds each build piece near a point up, as the game currently has it: the support it has, the most and least it can have, and whether the game has computed it yet: cli_piece_support <x> <z> [radius=30] [nameFilter]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                PieceSupport(args, args.Context.AddString);
            });
        }

        /// <summary>
        /// What can stand in the way of a player or a placed piece: static
        /// objects, pieces, rocks and trees. Not terrain, water or characters.
        /// </summary>
        private static int SolidMask => LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "vehicle");

        public static void SolidsOver(Terminal.ConsoleEventArgs args, Action<string> output)
        {
            if (!SolidsOverRequest.TryParse(args.Args, out SolidsOverRequest request))
            {
                output(SolidsOverRequest.Usage);
                return;
            }
            if (ZNetScene.instance == null)
            {
                output("ERROR: no world loaded");
                return;
            }

            // One box per point. Physics.OverlapBox tests the collider shapes
            // themselves, so a boulder's box that spans the point but whose
            // rock does not is not reported.
            Collider[] hits = new Collider[256];
            Dictionary<Collider, int> firstPoint = new Dictionary<Collider, int>();
            Dictionary<Collider, int> points = new Dictionary<Collider, int>();
            Vector3 extents = new Vector3(request.Half, request.HalfHeight, request.Half);
            for (int i = 0; i < request.Points.Count; i++)
            {
                float[] c = request.Centre(i);
                Vector3 centre = new Vector3(c[0], c[1], c[2]);
                int n = Physics.OverlapBoxNonAlloc(centre, extents, hits, Quaternion.identity, SolidMask, QueryTriggerInteraction.Ignore);
                // A full buffer may have dropped colliders: grow it and ask again.
                while (n == hits.Length)
                {
                    hits = new Collider[hits.Length * 2];
                    n = Physics.OverlapBoxNonAlloc(centre, extents, hits, Quaternion.identity, SolidMask, QueryTriggerInteraction.Ignore);
                }
                for (int h = 0; h < n; h++)
                {
                    Collider collider = hits[h];
                    if (!firstPoint.ContainsKey(collider))
                    {
                        firstPoint[collider] = i;
                        points[collider] = 0;
                    }
                    points[collider]++;
                }
            }

            List<KeyValuePair<Collider, int>> ordered = new List<KeyValuePair<Collider, int>>(firstPoint);
            ordered.Sort((a, b) =>
            {
                if (a.Value != b.Value) return a.Value.CompareTo(b.Value);
                int byName = string.CompareOrdinal(RockInspectionCommands.PrefabName(a.Key.gameObject), RockInspectionCommands.PrefabName(b.Key.gameObject));
                return byName != 0 ? byName : string.CompareOrdinal(a.Key.name, b.Key.name);
            });
            foreach (KeyValuePair<Collider, int> entry in ordered)
            {
                Collider collider = entry.Key;
                string piece = "";
                MineRock5 rock = collider.GetComponentInParent<MineRock5>();
                if (rock != null && rock.m_hitAreas != null)
                {
                    for (int i = 0; i < rock.m_hitAreas.Count; i++)
                    {
                        if (rock.m_hitAreas[i].m_collider == collider)
                        {
                            piece = FormattableString.Invariant($" piece={i} health={rock.m_hitAreas[i].m_health:F1}");
                            break;
                        }
                    }
                }
                float[] first = request.Points[entry.Value];
                output(FormattableString.Invariant(
                    $"SOLID name={RockInspectionCommands.PrefabName(collider.gameObject)}{piece} collider={collider.name.Replace(' ', '_')} layer={LayerMask.LayerToName(collider.gameObject.layer)} points={points[collider]} first={first[0]:F1},{first[2]:F1} top={collider.bounds.max.y:F2} {RockInspectionCommands.Zdo(collider.gameObject)}"));
            }
            output(FormattableString.Invariant($"OK: SOLIDS_OVER points={request.Points.Count} half={request.Half:F2} from={request.From:F2} to={request.To:F2} solids={firstPoint.Count}"));
        }

        public static void AreaReady(Terminal.ConsoleEventArgs args, Action<string> output)
        {
            const string usage = "Usage: cli_area_ready <x> <z> [list=10]";
            if (args.Length < 3 || args.Length > 4 || !CommandArguments.TryFiniteFloat(args[1], out float x) || !CommandArguments.TryFiniteFloat(args[2], out float z))
            {
                output(usage);
                return;
            }
            int list = 10;
            if (args.Length >= 4 && !CommandArguments.TryCount(args[3], 1000, out list))
            {
                output(usage);
                return;
            }
            if (!CommandArguments.CanScan(x, z, 0f))
            {
                output("ERROR: census coordinates exceed the supported sector range");
                return;
            }
            if (ZNetScene.instance == null || ZoneSystem.instance == null || ZDOMan.instance == null)
            {
                output("ERROR: no world loaded");
                return;
            }

            Vector3 point = new Vector3(x, 0f, z);
            Vector2s zone = ZoneSystem.GetZone(point);
            bool loaded = ZoneSystem.instance.IsZoneLoaded(zone);
            // The same sweep ZNetScene.IsAreaReady makes: the zone and its eight
            // neighbours, counting only objects whose prefab this game knows.
            List<ZDO> zdos = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(1, 0), zdos);
            int known = 0, missing = 0;
            foreach (ZDO zdo in zdos)
            {
                GameObject? prefab = zdo.GetPrefab() != 0 ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
                if (prefab == null) continue;
                known++;
                if (ZNetScene.instance.FindInstance(zdo)) continue;
                missing++;
                if (missing <= list)
                {
                    Vector3 p = zdo.GetPosition();
                    Vector2s at = ZoneSystem.GetZone(p);
                    output(FormattableString.Invariant($"MISSING_INSTANCE name={prefab.name} zdo={zdo.m_uid} pos={p.x:F1},{p.y:F1},{p.z:F1} zone={at.x},{at.y} distant={zdo.Distant} owner={zdo.GetOwner()}"));
                }
            }
            output(FormattableString.Invariant($"OK: AREA_READY {x:F1},{z:F1} ready={ZNetScene.instance.IsAreaReady(point)} zone={zone.x},{zone.y} loaded={loaded} objects={known} without_instance={missing}"));
        }

        /// <summary>
        /// Report the structural support the game holds for each build piece
        /// near a point, without recomputing it.
        ///
        /// Support decides whether a structure stands, and without this it can
        /// only be inferred by building, waiting and looking at what fell, which
        /// is slow and confounded by falling debris damaging what is beneath.
        /// The reply is the number the game will act on next: WearNTear.GetSupport
        /// and the material's limits, with a state that says where the number
        /// came from (see PieceSupportReading.State). Nothing is recomputed:
        /// UpdateSupport writes the result to the piece's ZDO and can ask other
        /// owners to drop cached support, so calling it would change the world
        /// being inspected. The game recomputes every owned piece about once a
        /// second, in its own order, each piece from its neighbours' current
        /// values; support can take several passes to travel up a tall stack,
        /// so reading twice a few seconds apart shows whether it has settled.
        /// </summary>
        public static void PieceSupport(Terminal.ConsoleEventArgs args, Action<string> output)
        {
            if (!PieceSupportReading.TryParse(args.Args, out float x, out float z, out float radius, out string? filter))
            {
                output(PieceSupportReading.Usage);
                return;
            }
            if (!CommandArguments.CanScan(x, z, radius))
            {
                output("ERROR: census coordinates exceed the supported sector range");
                return;
            }
            if (ZNetScene.instance == null)
            {
                output("ERROR: no world loaded");
                return;
            }

            List<WearNTear> found = new List<WearNTear>();
            foreach (WearNTear wear in WearNTear.GetAllInstances())
            {
                if (wear == null || wear.m_nview == null)
                {
                    continue;
                }
                Vector3 p = wear.transform.position;
                if ((p.x - x) * (p.x - x) + (p.z - z) * (p.z - z) > radius * radius)
                {
                    continue;
                }
                string name = RockInspectionCommands.PrefabName(wear.gameObject);
                if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                found.Add(wear);
            }
            found.Sort((a, b) =>
            {
                Vector3 pa = a.transform.position, pb = b.transform.position;
                return SceneGeometry.CompareBottomUp(pa.x, pa.y, pa.z, pb.x, pb.y, pb.z);
            });

            int held = 0, pending = 0;
            float now = Time.time;
            foreach (WearNTear wear in found)
            {
                ZNetView view = wear.m_nview;
                bool valid = view.IsValid();
                string state = PieceSupportReading.State(valid, valid && view.HasOwner(), valid && view.IsOwner(),
                    wear.m_noSupportWear, wear.m_createTime, now, wear.m_addPreSnow);
                float support = wear.GetSupport();
                float minimum = wear.GetMinSupport();
                if (PieceSupportReading.Held(support, minimum)) held++;
                if (state == "pending") pending++;
                Vector3 p = wear.transform.position;
                float health = valid ? view.GetZDO().GetFloat(ZDOVars.s_health, wear.m_health) : wear.m_health;
                output(PieceSupportReading.Row(RockInspectionCommands.PrefabName(wear.gameObject),
                    valid ? view.GetZDO().m_uid.ToString() : "none", p.x, p.y, p.z,
                    support, wear.GetMaxSupport(), minimum, state,
                    PieceSupportReading.PendingSeconds(wear.m_createTime, now, wear.m_addPreSnow), health));
            }
            output(PieceSupportReading.Summary(x, z, radius, found.Count, held, pending));
        }

        public static void GroundHeights(Terminal.ConsoleEventArgs args, Action<string> output)
        {
            if (ZoneSystem.instance == null)
            {
                output("ERROR: no world loaded");
                return;
            }
            if (args.Length < 3 || (args.Length - 1) % 2 != 0)
            {
                output("Usage: cli_ground_height <x> <z> [<x> <z> ...]");
                return;
            }
            for (int i = 1; i + 1 < args.Length; i += 2)
            {
                if (!CommandArguments.TryFiniteFloat(args[i], out float x) || !CommandArguments.TryFiniteFloat(args[i + 1], out float z))
                {
                    output($"ERROR: not a point: {args[i]} {args[i + 1]}");
                    continue;
                }
                if (ZoneSystem.instance.GetGroundHeight(new Vector3(x, 0f, z), out float height))
                {
                    output(string.Format(CultureInfo.InvariantCulture, "GROUND {0:F1},{1:F1} h={2:F3}", x, z, height));
                }
                else
                {
                    output(string.Format(CultureInfo.InvariantCulture, "GROUND {0:F1},{1:F1} none (no terrain loaded there)", x, z));
                }
            }
        }

        public static void SurfaceAt(Terminal.ConsoleEventArgs args, Action<string> output)
        {
            if (args.Length < 3 || args.Length > 4 || !CommandArguments.TryFiniteFloat(args[1], out float x) || !CommandArguments.TryFiniteFloat(args[2], out float z))
            {
                output("Usage: cli_surface_at <x> <z> [fromY=200]");
                return;
            }

            float fromY = 200f;
            if (args.Length >= 4 && !CommandArguments.TryFiniteFloat(args[3], out fromY))
            {
                output($"ERROR: not a height: {args[3]}");
                return;
            }

            if (fromY <= -500f || fromY > 10000f)
            {
                output("ERROR: fromY must be greater than -500 and at most 10000");
                return;
            }
            if (ZoneSystem.instance == null)
            {
                output("ERROR: no world loaded");
                return;
            }
            RaycastHit[] hits = Physics.RaycastAll(new Vector3(x, fromY, z), Vector3.down, fromY + 500f);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            int n = 0;
            foreach (RaycastHit hit in hits)
            {
                Collider collider = hit.collider;
                if (collider == null)
                {
                    continue;
                }

                ZNetView nview = collider.GetComponentInParent<ZNetView>();
                string zdo = nview != null && nview.IsValid() ? nview.GetZDO().m_uid.ToString() : "local";
                string name = collider.GetComponentInParent<Heightmap>() != null
                    ? "terrain"
                    : CustomCommands.CleanPrefabName(nview != null ? nview.gameObject.name : collider.gameObject.name);
                n++;
                output(string.Format(CultureInfo.InvariantCulture,
                    "SURFACE {0:F1},{1:F1} hit={2} name={3} y={4:F3} layer={5} zdo={6} trigger={7}",
                    x, z, n, name, hit.point.y, LayerMask.LayerToName(collider.gameObject.layer), zdo, collider.isTrigger));
            }

            output(string.Format(CultureInfo.InvariantCulture, "OK: SURFACE_AT {0:F1},{1:F1} hits={2}", x, z, n));
        }

        public static void PieceGeometry(Terminal.ConsoleEventArgs args, Action<string> output)
        {
            if (args.Length < 2)
            {
                output("Usage: cli_piece_geometry <prefab> [<prefab> ...]");
                return;
            }
            if (ZNetScene.instance == null)
            {
                output("ERROR: no ZNetScene: load a world first");
                return;
            }

            for (int i = 1; i < args.Length; i++)
            {
                GameObject prefab = ZNetScene.instance.GetPrefab(args[i]);
                if (prefab == null)
                {
                    output($"ERROR: no prefab named {args[i]}");
                    continue;
                }

                Transform root = prefab.transform;
                bool any = false;
                Vector3 min = Vector3.zero, max = Vector3.zero;
                foreach (Collider collider in prefab.GetComponentsInChildren<Collider>(true))
                {
                    if (collider.isTrigger)
                        continue;
                    if (!TryLocalBounds(collider, root, out Vector3 lo, out Vector3 hi))
                    {
                        output($"PIECE {args[i]} collider unreadable type={collider.GetType().Name} child={collider.name}");
                        continue;
                    }
                    if (!any) { min = lo; max = hi; any = true; }
                    else { min = Vector3.Min(min, lo); max = Vector3.Max(max, hi); }
                }

                if (any)
                {
                    Vector3 size = max - min;
                    output(string.Format(CultureInfo.InvariantCulture,
                        "PIECE {0} collider min={1:F3},{2:F3},{3:F3} max={4:F3},{5:F3},{6:F3} size={7:F3},{8:F3},{9:F3}",
                        args[i], min.x, min.y, min.z, max.x, max.y, max.z, size.x, size.y, size.z));
                }
                else
                {
                    output($"PIECE {args[i]} collider none (no non-trigger collider with readable bounds)");
                }

                int snaps = 0;
                foreach (Transform t in prefab.GetComponentsInChildren<Transform>(true))
                {
                    if (t == root || !IsSnapPoint(t))
                        continue;
                    Vector3 local = root.InverseTransformPoint(t.position);
                    snaps++;
                    output(string.Format(CultureInfo.InvariantCulture,
                        "PIECE {0} snap {1} at {2:F3},{3:F3},{4:F3}", args[i], snaps, local.x, local.y, local.z));
                }
                output($"OK: PIECE_GEOMETRY {args[i]} snaps={snaps}");
            }
        }

        private static bool IsSnapPoint(Transform t)
        {
            try
            {
                return t.CompareTag("snappoint");
            }
            catch (UnityException)
            {
                // The tag is not defined in this build; fall back to the name
                // the vanilla prefabs use.
                return t.name == "_snappoint";
            }
        }

        private static bool TryLocalBounds(Collider collider, Transform root, out Vector3 min, out Vector3 max)
        {
            min = max = Vector3.zero;
            Vector3 centre;
            Vector3 extents;
            switch (collider)
            {
                case BoxCollider box:
                    centre = box.center; extents = box.size * 0.5f; break;
                case SphereCollider sphere:
                    centre = sphere.center; extents = Vector3.one * sphere.radius; break;
                case CapsuleCollider capsule:
                    centre = capsule.center;
                    extents = new Vector3(
                        CommandArguments.CapsuleExtent(capsule.radius, capsule.height, capsule.direction, 0),
                        CommandArguments.CapsuleExtent(capsule.radius, capsule.height, capsule.direction, 1),
                        CommandArguments.CapsuleExtent(capsule.radius, capsule.height, capsule.direction, 2));
                    break;
                case MeshCollider mesh when mesh.sharedMesh != null:
                    centre = mesh.sharedMesh.bounds.center; extents = mesh.sharedMesh.bounds.extents; break;
                default:
                    return false;
            }

            bool first = true;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = centre + new Vector3(
                    (c & 1) == 0 ? -extents.x : extents.x,
                    (c & 2) == 0 ? -extents.y : extents.y,
                    (c & 4) == 0 ? -extents.z : extents.z);
                Vector3 local = root.InverseTransformPoint(collider.transform.TransformPoint(corner));
                if (first) { min = max = local; first = false; }
                else { min = Vector3.Min(min, local); max = Vector3.Max(max, local); }
            }
            return true;
        }

        public static void PaintAt(Terminal.ConsoleEventArgs args, Action<string> output)
        {
            if (args.Length < 3 || (args.Length - 1) % 2 != 0)
            {
                output("Usage: cli_paint_at <x> <z> [<x> <z> ...]");
                return;
            }
            for (int i = 1; i + 1 < args.Length; i += 2)
            {
                if (!CommandArguments.TryFiniteFloat(args[i], out float x) || !CommandArguments.TryFiniteFloat(args[i + 1], out float z))
                {
                    output($"ERROR: not a point: {args[i]} {args[i + 1]}");
                    continue;
                }
                Vector3 at = new Vector3(x, 0f, z);
                Heightmap hmap = Heightmap.FindHeightmap(at);
                if (hmap == null)
                {
                    output(string.Format(CultureInfo.InvariantCulture,
                        "PAINT {0:F1},{1:F1} none (no heightmap loaded there)", x, z));
                    continue;
                }
                Color mask = hmap.GetPaintMask(at);
                output(string.Format(CultureInfo.InvariantCulture,
                    "PAINT {0:F1},{1:F1} dirt={2:F3} cultivated={3:F3} paved={4:F3} clearveg={5:F3} -> {6}",
                    x, z, mask.r, mask.g, mask.b, mask.a, Name(mask)));
            }
        }

        private static string Name(Color mask)
        {
            if (mask.r >= 0.5f) return "Dirt";
            if (mask.g >= 0.5f) return "Cultivated";
            if (mask.b >= 0.5f) return "Paved";
            if (mask.r + mask.g + mask.b < 0.01f) return "unpainted";
            return "a blend";
        }
    }
}
