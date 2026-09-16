using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace valheimCLI
{
    public static class TerrainActionCommands
    {
        public static void Register()
        {
            new Terminal.ConsoleCommand("cli_terrain_ops", "List the terrain operations this game has registered -- the ones a hoe or pickaxe places: cli_terrain_ops", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                TerrainOps(args.Context.AddString);
            });

            new Terminal.ConsoleCommand("cli_terrain_edit", "Apply one of the game's OWN terrain operations at a point, the way a tool does: cli_terrain_edit <op> <x> <y> <z> (cli_terrain_ops lists the names). Run it on the CLIENT whose edit you want -- the game routes the change to whoever owns that zone's terrain compiler.", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                TerrainEdit(args, args.Context.AddString);
            }, isCheat: true);
        }

        public static void TerrainOps(Action<string> output)
        {
            if (ObjectDB.instance == null)
            {
                output("ERROR: no ObjectDB yet (no world loaded)");
                return;
            }
            List<TerrainOp> ops = ObjectDB.instance.m_terrainOps;
            if (ops == null || ops.Count == 0)
            {
                output("OK: 0 terrain op(s) registered");
                return;
            }
            output($"OK: {ops.Count} terrain op(s)");
            foreach (TerrainOp op in ops)
            {
                if (op == null)
                {
                    continue;
                }
                output("  TERRAINOP " + Utils.GetPrefabName(op.gameObject) + " " + DescribeOp(op));
            }
        }

        private static string DescribeOp(TerrainOp op)
        {
            TerrainOp.Settings settings = op.m_settings;
            if (settings == null)
            {
                return "(no settings)";
            }
            List<string> parts = new List<string>();
            if (settings.m_raise)
            {
                parts.Add(string.Format(CultureInfo.InvariantCulture, "raise r={0:F1} delta={1:F2}",
                    settings.m_raiseRadius, settings.m_raiseDelta));
            }
            if (settings.m_level)
            {
                parts.Add(string.Format(CultureInfo.InvariantCulture, "level r={0:F1} offset={1:F2}",
                    settings.m_levelRadius, settings.m_levelOffset));
            }
            if (settings.m_smooth)
            {
                parts.Add(string.Format(CultureInfo.InvariantCulture, "smooth r={0:F1}", settings.m_smoothRadius));
            }
            if (settings.m_paintCleared)
            {
                parts.Add(string.Format(CultureInfo.InvariantCulture, "paint {0} r={1:F1}",
                    settings.m_paintType, settings.m_paintRadius));
            }
            return parts.Count == 0 ? "(does nothing)" : string.Join(", ", parts.ToArray());
        }

        public static void TerrainEdit(Terminal.ConsoleEventArgs args, Action<string> output)
        {
            if (ObjectDB.instance == null)
            {
                output("ERROR: no ObjectDB yet (no world loaded)");
                return;
            }
            List<string> tokens = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                tokens.Add(args[i]);
            }
            if (!TerrainEditRequest.TryParse(tokens, out TerrainEditRequest? request, out string error) || request == null)
            {
                output(error);
                return;
            }
            if (!ObjectDB.instance.TryGetTerrainOp(request.Op, out TerrainOp prefab) || prefab == null)
            {
                output($"ERROR: no terrain op named '{request.Op}' -- cli_terrain_ops lists them");
                return;
            }
            // Awake checks this first and applies nothing when it is set, which
            // would leave the command looking like it worked.
            if (TerrainOp.m_forceDisableTerrainOps)
            {
                output("ERROR: TerrainOp.m_forceDisableTerrainOps is set, so the game applies no terrain ops here");
                return;
            }

            Vector3 position = new Vector3(request.X, request.Y, request.Z);
            float heightBefore = 0f;
            bool hadBefore = ZoneSystem.instance != null &&
                ZoneSystem.instance.GetGroundHeight(position, out heightBefore);

            if (!hadBefore)
            {
                output("ERROR: no terrain loaded at the edit point; move a player there first");
                return;
            }
            UnityEngine.Object.Instantiate(prefab.gameObject, position, Quaternion.identity);

            // Report what CHANGED, not merely that something was asked for: an
            // edit that landed on no loaded terrain is otherwise silent, and
            // this command exists to produce evidence.
            float heightAfter = 0f;
            bool hasAfter = ZoneSystem.instance != null &&
                ZoneSystem.instance.GetGroundHeight(position, out heightAfter);

            if (!hadBefore && !hasAfter)
            {
                output(string.Format(CultureInfo.InvariantCulture,
                    "OK: requested {0}, but no terrain is loaded there to measure -- run it where the terrain is live",
                    request.Describe()));
                return;
            }
            // Both readings are taken in the SAME frame as the Instantiate, so
            // the terrain collider has usually not rebuilt yet and this delta
            // is commonly 0.000 for an edit that did land -- a run measured
            // 53.101 -> 53.101 here and 52.675 a few seconds later. Say so,
            // rather than letting a zero read as "the edit did nothing": the
            // authoritative evidence is the zone's compiler, not this number.
            output(string.Format(CultureInfo.InvariantCulture,
                "OK: requested {0}; ground {1:F3} -> {2:F3} (delta {3:F3}, same-frame reading -- the collider may not have rebuilt, so 0.000 here is NOT evidence the edit failed; check the zone's compiler)",
                request.Describe(), heightBefore, heightAfter, heightAfter - heightBefore));
        }
    }
}
