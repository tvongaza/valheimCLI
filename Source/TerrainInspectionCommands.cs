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
