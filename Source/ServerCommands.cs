using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace valheimCLI
{
    /// <summary>
    /// Commands for testing on a dedicated server.
    ///
    /// Valheim 1.0 runs cheat commands only where ZNet.IsServer() is true
    /// (Terminal.IsCheatsEnabled), so a client joined to a dedicated server has
    /// every cheat-gated cli_ command refused, admin or not ("not valid in the
    /// current context"). Two ways round it, both opt-in:
    ///
    ///   - On the client, [Server] AllowOnServerClients lets valheimCLI's own
    ///     cli_ commands through again (ConsoleCommand_IsValid_Patch).
    ///   - On the server (valheimCLI installed there too, on another port),
    ///     cli_peers and cli_teleport_peer move a connected player through the
    ///     game's own teleport RPC, which runs on that player's client. That
    ///     client needs nothing installed, so an unmodded client can be put on
    ///     a test site.
    /// </summary>
    public static class ServerCommands
    {
        /// <summary>[Server] AllowOnServerClients.</summary>
        public static bool AllowCliOnServerClients;

        public static void Register()
        {
            new Terminal.ConsoleCommand("cli_peers", "Server only: list the connected peers and where their characters stand: cli_peers", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                PrintPeers(args.Context.AddString);
            });

            new Terminal.ConsoleCommand("cli_teleport_peer", "Server only: teleport a connected peer's character with the game's own teleport, which runs on that peer's client (nothing needs to be installed there): cli_teleport_peer <peer#> <x> <y> <z> [yaw]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                TeleportPeer(args, args.Context.AddString);
            });

            new Terminal.ConsoleCommand("cli_ground_height", "Terrain height under points, from this game's own terrain colliders (what a player here stands on): cli_ground_height <x> <z> [<x> <z> ...]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                GroundHeights(args, args.Context.AddString);
            });
        }

        /// <summary>
        /// ZoneSystem.GetGroundHeight: a ray down onto the terrain layer, so the
        /// answer is the collider this game built, terrain edits included, and
        /// only where that terrain is loaded.
        /// </summary>
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
                if (!TryFloat(args[i], out float x) || !TryFloat(args[i + 1], out float z))
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

        public static void TeleportPeer(Terminal.ConsoleEventArgs args, Action<string> output)
        {
            if (!IsServer(output))
            {
                return;
            }
            if (args.Length < 5 || !int.TryParse(args[1], out int index) ||
                !TryFloat(args[2], out float x) || !TryFloat(args[3], out float y) || !TryFloat(args[4], out float z))
            {
                output("Usage: cli_teleport_peer <peer#> <x> <y> <z> [yaw]");
                return;
            }
            float yaw = 0f;
            if (args.Length >= 6)
            {
                TryFloat(args[5], out yaw);
            }

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            if (index < 1 || index > peers.Count)
            {
                output($"ERROR: no peer {index} ({peers.Count} connected)");
                return;
            }
            ZNetPeer peer = peers[index - 1];
            if (peer.m_characterID.IsNone())
            {
                output($"ERROR: peer {index} has no character yet");
                return;
            }

            // Character.RPC_TeleportTo calls TeleportTo on whichever peer owns
            // the character, which is that player's own client. Distant: the
            // client shows its loading screen until the destination has loaded.
            Vector3 position = new Vector3(x, y, z);
            ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, peer.m_characterID, "RPC_TeleportTo",
                position, Quaternion.Euler(0f, yaw, 0f), true);
            output(string.Format(CultureInfo.InvariantCulture,
                "OK: asked peer {0} to teleport to {1:F1},{2:F1},{3:F1}", index, x, y, z));
        }

        private static bool TryFloat(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// [Server] AllowOnServerClients: valheimCLI's own cli_ commands stay usable
    /// on a client joined to a dedicated server, where Valheim 1.0 refuses every
    /// cheat command. Only cli_ commands, only on such a client.
    /// </summary>
    [HarmonyPatch(typeof(Terminal.ConsoleCommand), nameof(Terminal.ConsoleCommand.IsValid))]
    public static class ConsoleCommand_IsValid_Patch
    {
        public static void Postfix(Terminal.ConsoleCommand __instance, ref bool __result)
        {
            if (__result || !ServerCommands.AllowCliOnServerClients)
            {
                return;
            }
            if (ZNet.instance == null || ZNet.instance.IsServer())
            {
                return;
            }
            if (__instance.Command != null && __instance.Command.StartsWith("cli_", StringComparison.OrdinalIgnoreCase))
            {
                __result = true;
            }
        }
    }
}
