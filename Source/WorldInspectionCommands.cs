using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace valheimCLI
{
    public static class WorldInspectionCommands
    {
        public static void Register()
        {
            new Terminal.ConsoleCommand("cli_peers", "Server only: list the connected peers and where their characters stand: cli_peers", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                PrintPeers(args.Context.AddString);
            });

            new Terminal.ConsoleCommand("cli_zdos_at", "Server only: every saved object (ZDO) near a world point, by prefab name and transform, whether or not the zone is loaded here: cli_zdos_at <x> <z> [radius=40]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                ZdosAt(args, args.Context.AddString);
            });

            new Terminal.ConsoleCommand("cli_containers_at", "Server only: every container near a world point with what its ZDO says is inside, read from saved data rather than from a live object: cli_containers_at <x> <z> [radius=40]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                ContainersAt(args, args.Context.AddString);
            });
        }

        private static bool IsServer(Action<string> output)
        {
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                return true;
            }
            output("ERROR: server only: run it through the server's own valheimCLI");
            return false;
        }

        public static void PrintPeers(Action<string> output)
        {
            if (!IsServer(output))
            {
                return;
            }
            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            output($"OK: {peers.Count} peer(s)");
            for (int i = 0; i < peers.Count; i++)
            {
                ZNetPeer peer = peers[i];
                ZDO? character = peer.m_characterID.IsNone() ? null : ZDOMan.instance.GetZDO(peer.m_characterID);
                Vector3 position = character != null ? character.GetPosition() : peer.m_refPos;
                Vector2s zone = ZoneSystem.GetZone(position);
                output(string.Format(CultureInfo.InvariantCulture,
                    "PEER {0} {1} position={2:F1},{3:F2},{4:F1} zone={5},{6}",
                    i + 1, character != null ? "character" : "reference", position.x, position.y, position.z, zone.x, zone.y));
            }
        }

        public static void ZdosAt(Terminal.ConsoleEventArgs args, Action<string> output)
        {
            if (args.Length < 3 || args.Length > 4 || !CommandArguments.TryFiniteFloat(args[1], out float x) || !CommandArguments.TryFiniteFloat(args[2], out float z))
            {
                output("Usage: cli_zdos_at <x> <z> [radius=40]");
                return;
            }
            float radius = 40f;
            if (args.Length >= 4 && !CommandArguments.TryRadius(args[3], out radius))
            {
                output("Usage: cli_zdos_at <x> <z> [radius=40]");
                return;
            }
            if (!CommandArguments.CanScan(x, z, radius))
            {
                output("ERROR: census coordinates exceed the supported sector range");
                return;
            }
            if (!IsServer(output))
            {
                return;
            }
            if (ZDOMan.instance == null || ZoneSystem.instance == null || ZNetScene.instance == null)
            {
                output("ERROR: no world loaded");
                return;
            }

            Vector3 centre = new Vector3(x, 0f, z);
            Vector2s min = ZoneSystem.GetZone(new Vector3(x - radius, 0f, z - radius));
            Vector2s max = ZoneSystem.GetZone(new Vector3(x + radius, 0f, z + radius));

            List<ZDO> found = new List<ZDO>();
            List<ZDO> scratch = new List<ZDO>();
            // FindObjects is per zone and 1.0 asks the caller to carry the
            // sectors already visited; one set across the whole sweep is what
            // keeps an object on a zone boundary from being listed twice.
            HashSet<ZoneSystem.SectorIndex> visited = new HashSet<ZoneSystem.SectorIndex>();
            for (int zx = min.x; zx <= max.x; zx++)
            {
                for (int zy = min.y; zy <= max.y; zy++)
                {
                    scratch.Clear();
                    ZDOMan.instance.FindObjects(new Vector2s(zx, zy), scratch, visited);
                    foreach (ZDO zdo in scratch)
                    {
                        Vector3 pos = zdo.GetPosition();
                        float dx = pos.x - centre.x;
                        float dz = pos.z - centre.z;
                        if (dx * dx + dz * dz <= radius * radius)
                            found.Add(zdo);
                    }
                }
            }

            // Ordered by name then position so two visits produce the same text
            // and a diff is a real difference, not a reshuffled dictionary.
            found.Sort((a, b) =>
            {
                int byName = string.CompareOrdinal(PrefabName(a.GetPrefab()), PrefabName(b.GetPrefab()));
                if (byName != 0) return byName;
                Vector3 pa = a.GetPosition(), pb = b.GetPosition();
                if (pa.x != pb.x) return pa.x.CompareTo(pb.x);
                if (pa.y != pb.y) return pa.y.CompareTo(pb.y);
                return pa.z.CompareTo(pb.z);
            });

            foreach (ZDO zdo in found)
            {
                Vector3 pos = zdo.GetPosition();
                Quaternion q = zdo.GetRotation();
                Vector3 rot = q.eulerAngles;
                // The quaternion as well as the euler: eulerAngles is one of
                // several spellings of the same rotation, so a gate that compares
                // it component-wise against a differently-spelled expectation is
                // comparing conventions, not orientations. The euler stays for
                // reading.
                // Scale: a ZDO carries one only when the prefab syncs it. "-"
                // means the ZDO records none, which is not the same as 1 and is
                // left for the reader to decide about.
                string scale = "-";
                Vector3 vec = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
                if (vec != Vector3.zero)
                    scale = string.Format(CultureInfo.InvariantCulture, "{0:F4},{1:F4},{2:F4}", vec.x, vec.y, vec.z);
                else
                {
                    float scalar = zdo.GetFloat(ZDOVars.s_scaleScalarHash, 0f);
                    if (scalar != 0f)
                        scale = string.Format(CultureInfo.InvariantCulture, "{0:F4},{0:F4},{0:F4}", scalar);
                }
                // A LocationProxy says WHICH location it stands for. Without it
                // the only way to attribute a proxy to a site is by proximity,
                // and two sites can be closer together than either is wide.
                string location = "";
                int locationHash = zdo.GetInt(ZDOVars.s_location, 0);
                if (locationHash != 0)
                    location = string.Format(CultureInfo.InvariantCulture,
                        " location={0} seed={1}", locationHash, zdo.GetInt(ZDOVars.s_seed, 0));
                output(string.Format(CultureInfo.InvariantCulture,
                    "ZDO {0} id={1} pos={2:F3},{3:F3},{4:F3} rot={5:F2},{6:F2},{7:F2} " +
                    "quat={8:F6},{9:F6},{10:F6},{11:F6} scale={12} persistent={13} owner={14}{15}",
                    PrefabName(zdo.GetPrefab()), zdo.m_uid, pos.x, pos.y, pos.z, rot.x, rot.y, rot.z,
                    q.x, q.y, q.z, q.w, scale, zdo.Persistent, zdo.GetOwner(), location));
            }
            output(string.Format(CultureInfo.InvariantCulture,
                "OK: ZDOS_AT {0:F1},{1:F1} r={2:F1} zones={3} objects={4}",
                x, z, radius, (max.x - min.x + 1) * (max.y - min.y + 1), found.Count));
        }

        public static void ContainersAt(Terminal.ConsoleEventArgs args, Action<string> output)
        {
            if (args.Length < 3 || args.Length > 4 || !CommandArguments.TryFiniteFloat(args[1], out float x) || !CommandArguments.TryFiniteFloat(args[2], out float z))
            {
                output("Usage: cli_containers_at <x> <z> [radius=40]");
                return;
            }
            float radius = 40f;
            if (args.Length >= 4 && !CommandArguments.TryRadius(args[3], out radius))
            {
                output("Usage: cli_containers_at <x> <z> [radius=40]");
                return;
            }
            if (!CommandArguments.CanScan(x, z, radius))
            {
                output("ERROR: census coordinates exceed the supported sector range");
                return;
            }
            if (!IsServer(output))
            {
                return;
            }
            if (ZDOMan.instance == null || ZoneSystem.instance == null || ZNetScene.instance == null)
            {
                output("ERROR: no world loaded");
                return;
            }

            Vector2s min = ZoneSystem.GetZone(new Vector3(x - radius, 0f, z - radius));
            Vector2s max = ZoneSystem.GetZone(new Vector3(x + radius, 0f, z + radius));
            List<ZDO> scratch = new List<ZDO>();
            HashSet<ZoneSystem.SectorIndex> visited = new HashSet<ZoneSystem.SectorIndex>();
            int containers = 0;
            int unreadable = 0;
            for (int zx = min.x; zx <= max.x; zx++)
            {
                for (int zy = min.y; zy <= max.y; zy++)
                {
                    scratch.Clear();
                    ZDOMan.instance.FindObjects(new Vector2s(zx, zy), scratch, visited);
                    foreach (ZDO zdo in scratch)
                    {
                        Vector3 pos = zdo.GetPosition();
                        float dx = pos.x - x, dz = pos.z - z;
                        if (dx * dx + dz * dz > radius * radius)
                            continue;
                        // Whether this is a container is a question about the
                        // PREFAB, not about what the ZDO happens to carry: a
                        // chest that has never been opened has no s_items and no
                        // s_addedDefaultItems, and that state -- an empty chest
                        // waiting for its first owner to roll it -- is the one
                        // most worth seeing.
                        GameObject? prefab = ZNetScene.instance != null
                            ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
                        if (prefab == null || prefab.GetComponent<Container>() == null)
                            continue;
                        byte[] items = zdo.GetByteArray(ZDOVars.s_items);
                        bool rolled = zdo.GetBool(ZDOVars.s_addedDefaultItems);
                        containers++;
                        output(string.Format(CultureInfo.InvariantCulture,
                            "CONTAINER {0} pos={1:F3},{2:F3},{3:F3} defaultItemsRolled={4} bytes={5} id={6}",
                            PrefabName(zdo.GetPrefab()), pos.x, pos.y, pos.z, rolled,
                            items == null ? -1 : items.Length, zdo.m_uid));
                        foreach (string line in DescribeInventory(items))
                        {
                            if (line.StartsWith("ERROR:", StringComparison.Ordinal))
                            {
                                unreadable++;
                                output(line + " container=" + zdo.m_uid);
                            }
                            else
                            {
                                output("  ITEM " + line);
                            }
                        }
                    }
                }
            }
            output(string.Format(CultureInfo.InvariantCulture,
                (unreadable == 0 ? "OK:" : "ERROR:") + " CONTAINERS_AT {0:F1},{1:F1} r={2:F1} containers={3} unreadable={4}", x, z, radius, containers, unreadable));
        }

        private static List<string> DescribeInventory(byte[]? items)
        {
            List<string> lines = new List<string>();
            if (items == null || items.Length == 0)
                return lines;
            try
            {
                // Vanilla's temporary inventory does not instantiate ItemDrops or
                // discard saved slots outside an arbitrary chest-size rectangle.
                ZPackage header = new ZPackage(items);
                global::Version.Item version = (global::Version.Item)header.ReadInt();
                int expected = version >= global::Version.Item.Smaller ? header.ReadUShort() : header.ReadInt();
                Inventory inventory = new Inventory(true);
                inventory.Load(new ZPackage(items));
                if (expected < 0 || inventory.NrOfItems() != expected)
                {
                    lines.Add($"ERROR: inventory incomplete: saved={expected} decoded={inventory.NrOfItems()}");
                }
                foreach (ItemDrop.ItemData item in inventory.GetAllItems())
                {
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0} x{1} quality={2} variant={3} durability={4:F1} slot={5},{6}",
                        item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name,
                        item.m_stack, item.m_quality, item.m_variant, item.m_durability,
                        item.m_gridPos.x, item.m_gridPos.y));
                }
            }
            catch (Exception ex)
            {
                lines.Add("ERROR: inventory unreadable: " + ex.GetType().Name + " " + ex.Message);
            }
            return lines;
        }

        private static string PrefabName(int hash)
        {
            GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
            return prefab != null ? prefab.name : "hash:" + hash.ToString(CultureInfo.InvariantCulture);
        }
    }
}
