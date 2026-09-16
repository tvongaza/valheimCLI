using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace valheimCLI
{
    public static class SessionControlCommands
    {
        public static void Register()
        {
            new Terminal.ConsoleCommand("cli_teleport_peer", "Server only: teleport a connected peer's character with the game's own teleport, which runs on that peer's client (nothing needs to be installed there): cli_teleport_peer <peer#> <x> <y> <z> [yaw]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                TeleportPeer(args, args.Context.AddString);
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

        public static void TeleportPeer(Terminal.ConsoleEventArgs args, Action<string> output)
        {
            if (!IsServer(output))
            {
                return;
            }
            if (args.Length < 5 || args.Length > 6 || !int.TryParse(args[1], out int index) ||
                !CommandArguments.TryFiniteFloat(args[2], out float x) || !CommandArguments.TryFiniteFloat(args[3], out float y) || !CommandArguments.TryFiniteFloat(args[4], out float z))
            {
                output("Usage: cli_teleport_peer <peer#> <x> <y> <z> [yaw]");
                return;
            }
            if (!SessionArguments.TryYaw(args.Length == 6 ? args[5] : null, out float yaw))
            {
                output("ERROR: yaw must be a finite number");
                return;
            }

            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            if (index < 1 || index > peers.Count)
            {
                output($"ERROR: no peer {index} ({peers.Count} connected)");
                return;
            }
            ZNetPeer peer = peers[index - 1];
            if (peer.m_characterID.IsNone() || ZDOMan.instance == null || ZDOMan.instance.GetZDO(peer.m_characterID) == null || ZRoutedRpc.instance == null)
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
    }
}
