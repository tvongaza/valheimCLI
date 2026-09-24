using System.Collections.Generic;
using valheimCLI;
using Xunit;

namespace valheimCLI.Tests
{
    /// <summary>
    /// cli_piece_support_settle without the game: its arguments, the order it
    /// settles in, when it stops, and what it prints.
    /// </summary>
    public class SupportSettleTests
    {
        private static List<string> Args(params string[] tokens) => new List<string>(tokens);

        [Fact]
        public void AColumnNeedsOnlyItsHorizontalPosition()
        {
            Assert.True(SupportSettleRequest.TryParse(Args("cli_piece_support_settle", "100", "-20.5"), out SupportSettleRequest? request, out _));
            Assert.Equal(100f, request!.X);
            Assert.Equal(-20.5f, request.Z);
            Assert.Equal(SupportSettleRequest.DefaultRadius, request.Radius);
            Assert.Equal(SupportSettleRequest.DefaultPasses, request.Passes);
            Assert.Equal("", request.NameFilter);

            Assert.True(SupportSettleRequest.TryParse(Args("cli_piece_support_settle", "1", "2", "4", "7", "pole"), out request, out _));
            Assert.Equal(4f, request!.Radius);
            Assert.Equal(7, request.Passes);
            Assert.Equal("pole", request.NameFilter);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("21")]
        [InlineData("2.5")]
        [InlineData("many")]
        public void PassesAreAWholeNumberWithALimit(string passes)
        {
            Assert.False(SupportSettleRequest.TryParse(Args("cli_piece_support_settle", "1", "2", "10", passes), out SupportSettleRequest? request, out string error));
            Assert.Null(request);
            Assert.Contains("passes", error);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("1025")]
        [InlineData("NaN")]
        public void TheRadiusIsBounded(string radius)
        {
            Assert.False(SupportSettleRequest.TryParse(Args("cli_piece_support_settle", "1", "2", radius), out _, out string error));
            Assert.Contains("radius", error);
        }

        [Fact]
        public void BadCoordinatesOrCountsAreRefused()
        {
            Assert.False(SupportSettleRequest.TryParse(Args("cli_piece_support_settle", "1"), out _, out string error));
            Assert.Equal(SupportSettleRequest.Usage, error);
            Assert.False(SupportSettleRequest.TryParse(Args("cli_piece_support_settle", "1", "2", "3", "3", "pole", "extra"), out _, out error));
            Assert.Equal(SupportSettleRequest.Usage, error);
            Assert.False(SupportSettleRequest.TryParse(Args("cli_piece_support_settle", "Infinity", "2"), out _, out error));
            Assert.StartsWith("ERROR: x ", error);
        }

        /// <summary>The lowest piece is settled first, so each piece above reads a neighbour settled in the same pass.</summary>
        [Fact]
        public void TheColumnSettlesFromTheBottomUp()
        {
            List<SettlePiece> pieces = new List<SettlePiece>
            {
                new SettlePiece(0, 6, 0, "c"),
                new SettlePiece(0, 0, 0, "a"),
                new SettlePiece(0, 2, 0, "b"),
            };
            Assert.Equal(new[] { 1, 2, 0 }, SupportSettle.BottomUpOrder(pieces));
        }

        /// <summary>At one height the order is fixed by position and identity, never by the order the scene listed them in.</summary>
        [Fact]
        public void TiesAtOneHeightDoNotDependOnListOrder()
        {
            SettlePiece west = new SettlePiece(-1, 2, 5, "9");
            SettlePiece eastSouth = new SettlePiece(1, 2, -5, "8");
            SettlePiece eastNorth = new SettlePiece(1, 2, 5, "7");
            SettlePiece eastNorthTwin = new SettlePiece(1, 2, 5, "6");
            List<SettlePiece> one = new List<SettlePiece> { west, eastSouth, eastNorth, eastNorthTwin };
            List<SettlePiece> other = new List<SettlePiece> { eastNorthTwin, eastNorth, eastSouth, west };

            string[] Ids(List<SettlePiece> list)
            {
                int[] order = SupportSettle.BottomUpOrder(list);
                string[] ids = new string[order.Length];
                for (int i = 0; i < order.Length; i++) ids[i] = list[order[i]].Id;
                return ids;
            }

            Assert.Equal(new[] { "9", "8", "6", "7" }, Ids(one));
            Assert.Equal(Ids(one), Ids(other));
        }

        [Fact]
        public void PassesStopOnceNothingChanges()
        {
            int calls = 0;
            int run = SupportSettle.RunPasses(10, () => ++calls < 3, out bool converged);
            Assert.Equal(3, run);
            Assert.Equal(3, calls);
            Assert.True(converged);
        }

        /// <summary>Reaching the limit is not settling: the reply must not claim convergence.</summary>
        [Fact]
        public void TheLimitIsNotConvergence()
        {
            int calls = 0;
            int run = SupportSettle.RunPasses(4, () => { calls++; return true; }, out bool converged);
            Assert.Equal(4, run);
            Assert.Equal(4, calls);
            Assert.False(converged);
        }

        [Fact]
        public void AnAlreadySettledColumnTakesOnePass()
        {
            Assert.Equal(1, SupportSettle.RunPasses(3, () => false, out bool converged));
            Assert.True(converged);
        }

        [Fact]
        public void RoundingNoiseIsNotAChange()
        {
            Assert.False(SupportSettle.Changed(52.5f, 52.50001f));
            Assert.True(SupportSettle.Changed(52.5f, 52.4f));
        }

        [Fact]
        public void TheLinesCarryEveryReading()
        {
            Assert.Equal("SUPPORT piece=wood_pole2 zdo=1:2 pos=(1.000,2.500,-3.000) support=55.00 max=100.00 min=10.00 held=True owner=local",
                SupportSettle.PieceLine("wood_pole2", "1:2", 1f, 2.5f, -3f, 55f, 100f, 10f, true, true));
            Assert.EndsWith("held=False owner=remote", SupportSettle.PieceLine("wood_pole2", "1:3", 0, 0, 0, 5f, 100f, 10f, false, false));
            Assert.Equal("OK: PIECE_SUPPORT_SETTLE reported=4 settled=5 skippedRemote=1 held=3 unheld=1 passes=2 converged=True radius=10.0",
                SupportSettle.SummaryLine(4, 5, 1, 3, 1, 2, true, 10f));
        }
    }
}
