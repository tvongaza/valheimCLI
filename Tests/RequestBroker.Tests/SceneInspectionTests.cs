using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using valheimCLI;
using valheim_cli.Testing;
using Xunit;

namespace valheimCLI.Tests
{
    public class SceneInspectionTests
    {
        private static string[] Args(string line) => line.Split(' ');

        // ---- point lists and counts ----

        [Fact]
        public void PointsAreReadInWholeGroups()
        {
            Assert.True(CommandArguments.TryPoints(Args("cmd 1 2 3 -4.5 5 6"), 1, 3, out List<float[]> points));
            Assert.Equal(2, points.Count);
            Assert.Equal(new[] { -4.5f, 5f, 6f }, points[1]);

            Assert.True(CommandArguments.TryPoints(Args("cmd 1 2"), 1, 2, out points));
            Assert.Single(points);
        }

        [Theory]
        [InlineData("cmd", 1, 3)]
        [InlineData("cmd 1 2", 1, 3)]
        [InlineData("cmd 1 2 3 4", 1, 3)]
        [InlineData("cmd 1 NaN 3", 1, 3)]
        [InlineData("cmd 1 2,5 3", 1, 3)]
        [InlineData("cmd 1 2 3", 1, 0)]
        public void APartialOrUnreadablePointRejectsTheList(string line, int start, int dimensions)
        {
            Assert.False(CommandArguments.TryPoints(Args(line), start, dimensions, out List<float[]> points));
            Assert.Empty(points);
        }

        [Theory]
        [InlineData("0", true)]
        [InlineData("1000", true)]
        [InlineData("1001", false)]
        [InlineData("-1", false)]
        [InlineData("2.5", false)]
        [InlineData("ten", false)]
        public void CountsAreWholeAndBounded(string text, bool accepted)
        {
            Assert.Equal(accepted, CommandArguments.TryCount(text, 1000, out _));
        }

        // ---- geometry and order ----

        [Theory]
        [InlineData(5, 5, 0)]
        [InlineData(0, 5, 0)]
        [InlineData(-2, 5, 2)]
        [InlineData(13, 5, 3)]
        [InlineData(-3, -4, 5)]
        [InlineData(13, 14, 5)]
        public void HorizontalDistanceIsToTheNearestFaceOrCorner(float x, float z, float expected)
        {
            Assert.Equal(expected, SceneGeometry.HorizontalDistanceToBox(0, 0, 10, 10, x, z), 4);
        }

        [Fact]
        public void RepliesAreOrderedByNameThenPosition()
        {
            Assert.True(SceneGeometry.CompareNameThenPosition("Rock_3", 0, 0, 0, "rock4", 0, 0, 0) < 0);
            Assert.True(SceneGeometry.CompareNameThenPosition("a", 1, 0, 0, "a", 2, -5, -5) < 0);
            Assert.True(SceneGeometry.CompareNameThenPosition("a", 1, 2, 0, "a", 1, 1, 9) > 0);
            Assert.True(SceneGeometry.CompareNameThenPosition("a", 1, 1, 3, "a", 1, 1, 2) > 0);
            Assert.Equal(0, SceneGeometry.CompareNameThenPosition("a", 1, 1, 1, "a", 1, 1, 1));
        }

        [Fact]
        public void SupportIsListedFromTheFootingUp()
        {
            List<float[]> pieces = new List<float[]>
            {
                new[] { 0f, 4f, 0f }, new[] { 1f, 0f, 0f }, new[] { 0f, 0f, 2f }, new[] { 0f, 0f, 1f }
            };
            pieces.Sort((a, b) => SceneGeometry.CompareBottomUp(a[0], a[1], a[2], b[0], b[1], b[2]));
            Assert.Equal(new[] { 0f, 0f, 1f }, pieces[0]);
            Assert.Equal(new[] { 0f, 0f, 2f }, pieces[1]);
            Assert.Equal(new[] { 1f, 0f, 0f }, pieces[2]);
            Assert.Equal(new[] { 0f, 4f, 0f }, pieces[3]);
        }

        // ---- cli_solids_over ----

        [Fact]
        public void SolidsOverBuildsAColumnAboveEachPoint()
        {
            Assert.True(SolidsOverRequest.TryParse(Args("cli_solids_over 0.5 1 3 10 20 30 -1 -2 -3"), out SolidsOverRequest request));
            Assert.Equal(2, request.Points.Count);
            Assert.Equal(1f, request.HalfHeight);
            // The column runs from y+1 to y+3, so its centre is y+2.
            Assert.Equal(new[] { 10f, 22f, 30f }, request.Centre(0));
            Assert.Equal(new[] { -1f, 0f, -3f }, request.Centre(1));
        }

        [Fact]
        public void SolidsOverAcceptsAColumnThatStartsBelowThePoint()
        {
            Assert.True(SolidsOverRequest.TryParse(Args("cli_solids_over 1 -2 2 0 5 0"), out SolidsOverRequest request));
            Assert.Equal(new[] { 0f, 5f, 0f }, request.Centre(0));
            Assert.Equal(2f, request.HalfHeight);
        }

        [Theory]
        [InlineData("cli_solids_over 0.5 1 3")]
        [InlineData("cli_solids_over 0.5 1 3 10 20")]
        [InlineData("cli_solids_over 0.5 1 3 10 20 30 40")]
        [InlineData("cli_solids_over 0 1 3 10 20 30")]
        [InlineData("cli_solids_over -1 1 3 10 20 30")]
        [InlineData("cli_solids_over 1025 1 3 10 20 30")]
        [InlineData("cli_solids_over 0.5 3 3 10 20 30")]
        [InlineData("cli_solids_over 0.5 3 1 10 20 30")]
        [InlineData("cli_solids_over 0.5 -2000 2000 10 20 30")]
        [InlineData("cli_solids_over 0.5 1 Infinity 10 20 30")]
        [InlineData("cli_solids_over 0.5 1 3 10 x 30")]
        public void SolidsOverRejectsAnUnboundedOrMalformedColumn(string line)
        {
            Assert.False(SolidsOverRequest.TryParse(Args(line), out _));
        }

        // ---- cli_piece_support ----

        [Fact]
        public void PieceSupportDefaultsAndOptionalArguments()
        {
            Assert.True(PieceSupportReading.TryParse(Args("cli_piece_support 10 -20"), out float x, out float z, out float radius, out string? filter));
            Assert.Equal((10f, -20f, 30f), (x, z, radius));
            Assert.Null(filter);

            Assert.True(PieceSupportReading.TryParse(Args("cli_piece_support 10 -20 5 pole"), out _, out _, out radius, out filter));
            Assert.Equal(5f, radius);
            Assert.Equal("pole", filter);
        }

        [Theory]
        [InlineData("cli_piece_support")]
        [InlineData("cli_piece_support 10")]
        [InlineData("cli_piece_support 10 x")]
        [InlineData("cli_piece_support 10 20 0")]
        [InlineData("cli_piece_support 10 20 2000")]
        [InlineData("cli_piece_support 10 20 5 pole extra")]
        public void PieceSupportRejectsBadArguments(string line)
        {
            Assert.False(PieceSupportReading.TryParse(Args(line), out _, out _, out _, out _));
        }

        [Fact]
        public void APieceThatTakesNoSupportWearIsExemptWhoeverOwnsIt()
        {
            Assert.Equal("exempt", PieceSupportReading.State(true, true, true, false, -1f, 100f, false));
            Assert.Equal("exempt", PieceSupportReading.State(false, false, false, false, 90f, 100f, false));
        }

        [Fact]
        public void SupportComesFromTheOwner()
        {
            Assert.Equal("unowned", PieceSupportReading.State(false, false, false, true, -1f, 100f, false));
            Assert.Equal("unowned", PieceSupportReading.State(true, false, false, true, -1f, 100f, false));
            Assert.Equal("remote", PieceSupportReading.State(true, true, false, true, 99f, 100f, false));
            Assert.Equal("computed", PieceSupportReading.State(true, true, true, true, -1f, 100f, false));
        }

        [Theory]
        // Placed through the build system: OnPlaced clears the creation time.
        [InlineData(-1f, 100f, false, "computed", 0f)]
        // Spawned or just loaded: inside the 30 s grace the value is full capacity.
        [InlineData(90f, 100f, false, "pending", 20f)]
        [InlineData(70f, 100f, false, "pending", 0f)]
        [InlineData(69.5f, 100f, false, "computed", 0f)]
        // Pre-placed snow is updated from the start.
        [InlineData(90f, 100f, true, "computed", 0f)]
        public void TheCreationGraceMirrorsTheGame(float created, float now, bool preSnow, string state, float remaining)
        {
            Assert.Equal(state, PieceSupportReading.State(true, true, true, true, created, now, preSnow));
            Assert.Equal(remaining, PieceSupportReading.PendingSeconds(created, now, preSnow), 3);
        }

        [Fact]
        public void APieceIsHeldAtOrAboveItsMinimum()
        {
            Assert.True(PieceSupportReading.Held(10f, 10f));
            Assert.False(PieceSupportReading.Held(9.99f, 10f));
        }

        [Fact]
        public void ASupportRowSaysWhetherTheNumberIsAMeasurement()
        {
            Assert.Equal(
                "SUPPORT wood_pole2 zdo=1:2 pos=1.000,2.500,-3.000 support=100.00 max=100.00 min=10.00 held=yes state=pending pending_s=12.3 health=100.0",
                PieceSupportReading.Row("wood_pole2", "1:2", 1f, 2.5f, -3f, 100f, 100f, 10f, "pending", 12.34f, 100f));
            Assert.Equal(
                "SUPPORT wood_pole2 zdo=1:3 pos=0.000,20.000,0.000 support=8.00 max=100.00 min=10.00 held=no state=computed health=40.0",
                PieceSupportReading.Row("wood_pole2", "1:3", 0f, 20f, 0f, 8f, 100f, 10f, "computed", 0f, 40f));
        }

        [Fact]
        public void TheSupportSummaryCountsWhatIsNotHeld()
        {
            Assert.Equal("OK: PIECE_SUPPORT 1.0,2.0 r=30.0 pieces=5 held=3 unheld=2 pending=1",
                PieceSupportReading.Summary(1f, 2f, 30f, 5, 3, 1));
        }

        // ---- cli_rock_health ----

        /// <summary>What MineRock5.SaveHealth stores: ZPackage writes through a BinaryWriter.</summary>
        private static string Saved(params float[] healths)
        {
            using MemoryStream stream = new MemoryStream();
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(healths.Length);
                foreach (float h in healths) writer.Write(h);
            }
            return Convert.ToBase64String(stream.ToArray());
        }

        [Fact]
        public void NoSavedHealthMeansTheRockWasNeverHit()
        {
            Assert.True(MineRockHealth.TryDecode("", out float[]? healths));
            Assert.Null(healths);
            Assert.True(MineRockHealth.TryDecode(null, out healths));
            Assert.Null(healths);
            Assert.Empty(MineRockHealth.Destroyed(healths));
        }

        [Fact]
        public void SavedHealthDecodesPiecePerPiece()
        {
            Assert.True(MineRockHealth.TryDecode(Saved(30f, 0f, -5f, 0.01f), out float[]? healths));
            Assert.Equal(new[] { 30f, 0f, -5f, 0.01f }, healths);
            Assert.Equal(new List<int> { 1, 2 }, MineRockHealth.Destroyed(healths));
        }

        [Fact]
        public void TrailingBytesAreIgnoredAsTheGameIgnoresThem()
        {
            byte[] bytes = Convert.FromBase64String(Saved(1f, 2f));
            Array.Resize(ref bytes, bytes.Length + 3);
            Assert.True(MineRockHealth.TryDecode(Convert.ToBase64String(bytes), out float[]? healths));
            Assert.Equal(new[] { 1f, 2f }, healths);
        }

        [Fact]
        public void AStringTheGameDidNotWriteIsUnreadable()
        {
            Assert.False(MineRockHealth.TryDecode("not base64!", out _));
            // Claims three pieces, carries two.
            byte[] bytes = Convert.FromBase64String(Saved(1f, 2f));
            bytes[0] = 3;
            Assert.False(MineRockHealth.TryDecode(Convert.ToBase64String(bytes), out _));
            // Negative count.
            Assert.False(MineRockHealth.TryDecode(Convert.ToBase64String(BitConverter.GetBytes(-1)), out _));
            // Shorter than the count itself.
            Assert.False(MineRockHealth.TryDecode(Convert.ToBase64String(new byte[] { 1, 0 }), out _));
        }

        [Fact]
        public void ARockNotLoadedHereIsReportedFromItsSaveAlone()
        {
            string row = MineRockHealth.Row("rock1", "5:6", 1f, 2f, 3f, true, null, null, out bool mismatch);
            Assert.Equal("ROCKHEALTH name=rock1 zdo=5:6 pos=1.00,2.00,3.00 saved=none destroyed=- live=none", row);
            Assert.False(mismatch);
        }

        [Fact]
        public void LiveAndSavedMatchWhenTheSamePiecesAreGone()
        {
            string row = MineRockHealth.Row("rock1", "5:6", 0f, 0f, 0f, true, new[] { 0f, 5f, 0f }, new[] { -1f, 60f, 0f }, out bool mismatch);
            Assert.EndsWith("saved=3 destroyed=0,2 live=3 live_destroyed=0,2 match=yes", row);
            Assert.False(mismatch);

            // Never hit, and the live rock agrees.
            MineRockHealth.Row("rock1", "5:6", 0f, 0f, 0f, true, null, new[] { 60f, 60f }, out mismatch);
            Assert.False(mismatch);
        }

        [Fact]
        public void LiveAndSavedDisagreeWhenDifferentPiecesAreGone()
        {
            string row = MineRockHealth.Row("rock1", "5:6", 0f, 0f, 0f, true, null, new[] { 60f, 0f }, out bool mismatch);
            Assert.EndsWith("saved=none destroyed=- live=2 live_destroyed=1 match=no", row);
            Assert.True(mismatch);
        }

        [Fact]
        public void AnUnreadableSaveIsNeitherAMatchNorAMismatch()
        {
            string row = MineRockHealth.Row("rock1", "5:6", 0f, 0f, 0f, false, null, new[] { 60f, 0f }, out bool mismatch);
            Assert.EndsWith("saved=unreadable destroyed=? live=2 live_destroyed=1 match=?", row);
            Assert.False(mismatch);
        }

        [Fact]
        public void AnUnreadableRockFailsTheWholeReply()
        {
            Assert.Equal("OK: ROCK_HEALTH 1.0,2.0 r=30.0 rocks=4 loaded=2 mismatched=1 unreadable=0",
                MineRockHealth.Summary(1f, 2f, 30f, 4, 2, 1, 0));
            string failed = MineRockHealth.Summary(1f, 2f, 30f, 4, 2, 0, 1);
            Assert.StartsWith("ERROR: ROCK_HEALTH", failed);
            CommandResult result = CommandResult.FromOutput("cli_rock_health 1 2", new List<string> { "ROCKHEALTH name=rock1", failed });
            Assert.False(result.Ok);
        }

        [Fact]
        public void UsageLinesAreRecognisedAsBadInput()
        {
            foreach (string usage in new[] { SolidsOverRequest.Usage, PieceSupportReading.Usage, MineRockHealth.Usage })
            {
                Assert.Equal("bad_input", CommandResult.FromOutput("x", new List<string> { usage }).ErrorCode);
            }
        }
    }
}
