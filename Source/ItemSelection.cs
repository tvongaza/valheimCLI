using System.Collections.Generic;

namespace valheimCLI
{
    /// <summary>How well an inventory item's names match what was asked for.</summary>
    public enum ItemMatch
    {
        None,
        /// <summary>The request is part of the prefab, token or display name.</summary>
        Partial,
        /// <summary>The request is the whole prefab, token or display name.</summary>
        Exact
    }

    /// <summary>
    /// cli_equip_item's choice and reply, in plain .NET so they can be tested
    /// without the game.
    ///
    /// Humanoid.EquipItem returns false for an item that is already equipped,
    /// the same false it returns for a real refusal (mid-attack, swimming,
    /// broken, missing DLC). Asked to equip the hammer the player holds, the
    /// command used to report "Equip failed", and with two hammers in the
    /// inventory it could pick the unequipped copy and swap them. A script that
    /// runs "equip the hammer" before building must get OK when the hammer is
    /// already in hand.
    /// </summary>
    public static class ItemSelection
    {
        /// <summary>
        /// The item to equip, or -1: the best match kind wins (a whole-name
        /// match over a partial one), and among equally good matches an item
        /// that is already equipped wins, then inventory order.
        /// </summary>
        public static int Choose(IReadOnlyList<ItemMatch> matches, IReadOnlyList<bool> equipped)
        {
            int best = -1;
            for (int i = 0; i < matches.Count; i++)
            {
                if (matches[i] == ItemMatch.None)
                {
                    continue;
                }
                if (best < 0 || matches[i] > matches[best] ||
                    (matches[i] == matches[best] && equipped[i] && !equipped[best]))
                {
                    best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// The reply's prefix: an item already equipped is the state asked
        /// for, not a failure; only a refused equip of an unequipped item is
        /// an error.
        /// </summary>
        public static string ReplyPrefix(bool alreadyEquipped, bool equipAccepted) =>
            alreadyEquipped || equipAccepted ? "OK: equipped item" : "ERROR: Equip failed";
    }
}
