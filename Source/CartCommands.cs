using System;
using System.Globalization;
using System.Collections.Generic;
using UnityEngine;

namespace valheimCLI
{
    /// <summary>
    /// The vanilla Cart (Vagon) from the console: where it is, whether it is
    /// hitched, and what it carries. Attaching goes through Vagon.Interact --
    /// the same request a player's Use key makes -- so the cart's own distance
    /// rule decides whether the player is standing at the handle.
    /// </summary>
    public static class CartCommands
    {
        internal static void Register()
        {
            _ = new Terminal.ConsoleCommand("cli_cart", "Nearest vanilla cart: cli_cart status|attach|detach|load <prefab> <count> [radius=30]", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (args.Length < 2)
                {
                    args.Context.AddString("Usage: cli_cart status|attach|detach|load <prefab> <count> [radius=30]");
                    return;
                }
                Run(args, args.Context.AddString);
            }, isCheat: true);

            _ = new Terminal.ConsoleCommand("cli_spawn_piece", "Spawn one prefab at an exact transform, reporting its actual network persistence: cli_spawn_piece <prefab> <x> <y> <z> <yaw>", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                if (args.Length != 6 || !TryF(args[2], out float x) || !TryF(args[3], out float y) || !TryF(args[4], out float z) || !TryF(args[5], out float yaw))
                {
                    args.Context.AddString("Usage: cli_spawn_piece <prefab> <x> <y> <z> <yaw>");
                    return;
                }
                GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(args[1]) : null;
                if (prefab == null)
                {
                    args.Context.AddString($"ERROR: no prefab named '{args[1]}'");
                    return;
                }
                GameObject spawned = UnityEngine.Object.Instantiate(prefab, new Vector3(x, y, z), Quaternion.Euler(0f, yaw, 0f));
                ZNetView? nview = spawned.GetComponent<ZNetView>();
                string zdo = nview != null && nview.GetZDO() != null ? nview.GetZDO().m_uid.ToString() : "none";
                bool persistent = nview != null && nview.IsValid() && nview.GetZDO().Persistent;
                args.Context.AddString($"OK: spawned {args[1]} at {V(spawned.transform.position)} yaw={F(spawned.transform.eulerAngles.y)} zdo={zdo} persistent={persistent}");
            }, isCheat: true);
        }

        private static bool TryF(string s, out float v) => CommandArguments.TryFiniteFloat(s, out v);

        private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
        private static string V(Vector3 v) => $"{F(v.x)},{F(v.y)},{F(v.z)}";

        private static void Run(Terminal.ConsoleEventArgs args, Action<string> addOutput)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                addOutput("ERROR: No local player found");
                return;
            }
            string op = args[1].ToLowerInvariant();
            float radius = 30f;
            int radiusIndex = op == "load" ? 4 : 2;
            if (args.Length > radiusIndex)
            {
                if (!CommandArguments.TryRadius(args[radiusIndex], out radius))
                {
                    addOutput("ERROR: radius must be finite, greater than zero and at most 1024");
                    return;
                }
            }

            if (args.Length > radiusIndex + 1)
            {
                addOutput("ERROR: too many cart arguments");
                return;
            }
            Vagon? cart = null;
            float best = float.MaxValue;
            foreach (Vagon candidate in UnityEngine.Object.FindObjectsByType<Vagon>(FindObjectsSortMode.None))
            {
                if (candidate == null || candidate.m_nview == null || !candidate.m_nview.IsValid())
                {
                    continue;
                }
                float d = Vector3.Distance(player.transform.position, candidate.transform.position);
                if (d < best && d <= radius)
                {
                    best = d;
                    cart = candidate;
                }
            }
            if (cart == null)
            {
                addOutput($"ERROR: no cart within {F(radius)} m");
                return;
            }

            switch (op)
            {
                case "status":
                    addOutput("OK: " + Describe(cart, player));
                    return;
                case "attach":
                case "detach":
                {
                    bool attached = cart.IsAttached(player);
                    if ((op == "attach") == attached)
                    {
                        addOutput("OK: already " + (attached ? "attached; " : "detached; ") + Describe(cart, player));
                        return;
                    }
                    bool canAttach = cart.CanAttach(player.gameObject);
                    cart.Interact(player, false, false);
                    addOutput($"OK: requested {op}; canAttachNow={canAttach}; the cart answers on its next update. " + Describe(cart, player));
                    return;
                }
                case "load":
                {
                    if (args.Length < 4 || !SessionArguments.TryCartLoadCount(args[3], out int count))
                    {
                        addOutput("Usage: cli_cart load <prefab> <count=1..10000> [radius]");
                        return;
                    }
                    if (cart.m_container == null)
                    {
                        addOutput("ERROR: the cart has no container");
                        return;
                    }
                    GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(args[2]) : null;
                    if (prefab == null)
                    {
                        addOutput($"ERROR: no prefab named '{args[2]}'");
                        return;
                    }
                    ItemDrop? item = prefab.GetComponent<ItemDrop>();
                    if (item == null || item.m_itemData.m_shared.m_maxStackSize <= 0)
                    {
                        addOutput("ERROR: prefab is not a valid inventory item");
                        return;
                    }
                    if (!cart.m_container.m_nview.IsOwner())
                    {
                        cart.m_container.m_nview.ClaimOwnership();
                        addOutput("ERROR: requested container ownership; retry after its inventory updates");
                        return;
                    }
                    Inventory inv = cart.m_container.GetInventory();
                    if (!inv.CanAddItem(prefab, count))
                    {
                        addOutput("ERROR: insufficient cart capacity; nothing added");
                        return;
                    }
                    int before = CountPrefab(inv, prefab.name);
                    bool added = true;
                    foreach (int batch in SessionArguments.StackBatches(count, item.m_itemData.m_shared.m_maxStackSize))
                    {
                        if (!inv.AddItem(prefab, batch))
                        {
                            added = false;
                            break;
                        }
                    }
                    int after = CountPrefab(inv, prefab.name);
                    cart.m_container.Save();
                    bool complete = added && SessionArguments.CartLoadComplete(count, before, after);
                    addOutput($"{(complete ? "OK:" : "ERROR:")} cart load requested={count} added={after - before} prefab={args[2]}; " + Describe(cart, player));
                    return;
                }
                default:
                    addOutput("Usage: cli_cart status|attach|detach|load <prefab> <count> [radius=30]");
                    return;
            }
        }

        private static int CountPrefab(Inventory inventory, string name)
        {
            int count = 0;
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
            {
                if (item.m_dropPrefab != null && item.m_dropPrefab.name == name)
                {
                    count += item.m_stack;
                }
            }
            return count;
        }

        private static string Describe(Vagon cart, Player player)
        {
            Vector3 attach = cart.m_attachPoint != null ? cart.m_attachPoint.position : cart.transform.position;
            float handleDistance = Vector3.Distance(player.transform.position + cart.m_attachOffset, attach);
            Inventory? inv = cart.m_container != null ? cart.m_container.GetInventory() : null;
            string load = inv != null ? $"items={inv.NrOfItems()} weight={F(inv.GetTotalWeight())}" : "no-container";
            return $"cart zdo={cart.m_nview.GetZDO().m_uid} pos={V(cart.transform.position)} yaw={F(cart.transform.eulerAngles.y)} " +
                   $"attachPoint={V(attach)} handleDistance={F(handleDistance)} detachDistance={F(cart.m_detachDistance)} " +
                   $"attachedToPlayer={cart.IsAttached(player)} attached={cart.IsAttached()} owner={cart.m_nview.IsOwner()} {load}";
        }
    }
}
