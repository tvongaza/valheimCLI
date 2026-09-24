using valheimCLI;
using Xunit;

namespace valheimCLI.Tests
{
    /// <summary>cli_equip_item's choice of item and its reply, without the game.</summary>
    public class ItemSelectionTests
    {
        private static int Choose(ItemMatch[] matches, bool[] equipped) => ItemSelection.Choose(matches, equipped);

        /// <summary>
        /// The in-game failure: with the hammer in hand, "equip the hammer"
        /// replied "Equip failed" (the game refuses to equip what is already
        /// equipped) and a build script stopped. It is the state asked for.
        /// </summary>
        [Fact]
        public void AnItemAlreadyInHandIsOk()
        {
            Assert.StartsWith("OK:", ItemSelection.ReplyPrefix(alreadyEquipped: true, equipAccepted: false));
        }

        [Fact]
        public void OnlyARealRefusalIsAnError()
        {
            Assert.StartsWith("OK:", ItemSelection.ReplyPrefix(false, equipAccepted: true));
            Assert.StartsWith("ERROR:", ItemSelection.ReplyPrefix(false, equipAccepted: false));
        }

        /// <summary>
        /// Two hammers, the second in hand: the old lookup took the first in
        /// inventory order and swapped the held one out. The equipped copy wins.
        /// </summary>
        [Fact]
        public void TheEquippedCopyOfTwoWins()
        {
            Assert.Equal(1, Choose(new[] { ItemMatch.Exact, ItemMatch.Exact }, new[] { false, true }));
            Assert.Equal(0, Choose(new[] { ItemMatch.Exact, ItemMatch.Exact }, new[] { false, false }));
        }

        [Fact]
        public void AWholeNameBeatsAPartOfOneEvenInHand()
        {
            Assert.Equal(1, Choose(new[] { ItemMatch.Partial, ItemMatch.Exact }, new[] { true, false }));
            Assert.Equal(2, Choose(new[] { ItemMatch.None, ItemMatch.Partial, ItemMatch.Partial }, new[] { true, false, true }));
        }

        [Fact]
        public void NoMatchIsNoItem()
        {
            Assert.Equal(-1, Choose(new[] { ItemMatch.None, ItemMatch.None }, new[] { true, false }));
            Assert.Equal(-1, Choose(new ItemMatch[0], new bool[0]));
        }
    }
}
