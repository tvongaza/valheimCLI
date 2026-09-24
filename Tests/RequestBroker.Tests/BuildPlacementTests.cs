using System.Collections.Generic;
using valheimCLI;
using Xunit;

namespace valheimCLI.Tests
{
    /// <summary>
    /// The half of cli_build_place_at, cli_build_place_snapped and
    /// cli_build_snap_points that can be decided without the game: arguments,
    /// the snap geometry and the rules applied before the placement call.
    /// </summary>
    public class BuildPlacementTests
    {
        private static List<string> Args(params string[] tokens) => new List<string>(tokens);

        private static PlacementRequest Place(bool snapped, params string[] tokens)
        {
            Assert.True(PlacementRequest.TryParse(Args(tokens), snapped, out PlacementRequest? request, out string error), error);
            return request!;
        }

        private static string Refused(bool snapped, params string[] tokens)
        {
            Assert.False(PlacementRequest.TryParse(Args(tokens), snapped, out PlacementRequest? request, out string error));
            Assert.Null(request);
            return error;
        }

        [Fact]
        public void PlaceAtTakesAPieceAndAPositionWithDefaults()
        {
            PlacementRequest request = Place(false, "cli_build_place_at", "wood_floor", "10.5", "32", "-4.25");
            Assert.Equal("wood_floor", request.Piece);
            Assert.Equal(10.5f, request.Position.X);
            Assert.Equal(32f, request.Position.Y);
            Assert.Equal(-4.25f, request.Position.Z);
            Assert.Equal(0f, request.Yaw);
            Assert.False(request.NoCost);
        }

        [Fact]
        public void PlaceAtReadsYawAndATrailingNoCost()
        {
            PlacementRequest request = Place(false, "cli_build_place_at", "selected", "1", "2", "3", "45", "nocost");
            Assert.Equal("selected", request.Piece);
            Assert.Equal(45f, request.Yaw);
            Assert.True(request.NoCost);

            Assert.True(Place(false, "cli_build_place_at", "wood_floor", "1", "2", "3", "--nocost").NoCost);
            Assert.True(Place(false, "cli_build_place_at", "wood_floor", "1", "2", "3", "NOCOST").NoCost);
        }

        /// <summary>A yaw that fails to parse must not quietly become zero: the piece would face the wrong way and look placed.</summary>
        [Theory]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("ninety")]
        public void ABadYawIsRefusedNotDefaulted(string yaw)
        {
            Assert.Contains("yaw", Refused(false, "cli_build_place_at", "wood_floor", "1", "2", "3", yaw));
        }

        [Theory]
        [InlineData("x", "NaN", "2", "3")]
        [InlineData("y", "1", "Infinity", "3")]
        [InlineData("z", "1", "2", "north")]
        public void CoordinatesMustBeFinite(string which, string x, string y, string z)
        {
            Assert.StartsWith($"ERROR: {which} ", Refused(false, "cli_build_place_at", "wood_floor", x, y, z));
        }

        [Fact]
        public void CoordinatesAreReadInInvariantCulture()
        {
            Assert.Equal(1.5f, Place(false, "cli_build_place_at", "wood_floor", "1.5", "0", "0").Position.X);
            Assert.StartsWith("ERROR: x ", Refused(false, "cli_build_place_at", "wood_floor", "1,5", "0", "0"));
        }

        [Fact]
        public void MissingOrExtraArgumentsGiveTheUsage()
        {
            Assert.Equal(PlacementRequest.PlaceAtUsage, Refused(false, "cli_build_place_at", "wood_floor", "1", "2"));
            Assert.Equal(PlacementRequest.PlaceAtUsage, Refused(false, "cli_build_place_at", "wood_floor", "1", "2", "3", "0", "0.5"));
            Assert.Equal(PlacementRequest.PlaceAtUsage, Refused(false, "cli_build_place_at", " ", "1", "2", "3"));
            Assert.Equal(PlacementRequest.PlaceSnappedUsage, Refused(true, "cli_build_place_snapped"));
        }

        /// <summary>"nocost" is only a flag in last place, so it can never be read where a number was meant.</summary>
        [Fact]
        public void NoCostIsOnlyAFlagInLastPlace()
        {
            Assert.Contains("yaw", Refused(false, "cli_build_place_at", "wood_floor", "1", "2", "3", "nocost", "90"));
        }

        [Fact]
        public void SnappedPlacementDefaultsToTheHammersSnapDistance()
        {
            PlacementRequest request = Place(true, "cli_build_place_snapped", "wood_floor", "1", "2", "3");
            Assert.Equal(0.5f, request.SnapRadius);
            Assert.Equal(PlacementRequest.DefaultSnapRadius, request.SnapRadius);

            request = Place(true, "cli_build_place_snapped", "wood_floor", "1", "2", "3", "90", "1.25", "nocost");
            Assert.Equal(90f, request.Yaw);
            Assert.Equal(1.25f, request.SnapRadius);
            Assert.True(request.NoCost);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-1")]
        [InlineData("10.5")]
        [InlineData("NaN")]
        public void SnapRadiusIsPositiveAndWithinTheHammersReach(string radius)
        {
            Assert.Contains("snapRadius", Refused(true, "cli_build_place_snapped", "wood_floor", "1", "2", "3", "0", radius));
        }

        [Fact]
        public void PlaceAtHasNoSnapRadius()
        {
            Assert.Equal(PlacementRequest.PlaceAtUsage, Refused(false, "cli_build_place_at", "wood_floor", "1", "2", "3", "0", "1"));
        }

        [Fact]
        public void SnapPointListingHasARadiusAndAFilter()
        {
            Assert.True(SnapPointsRequest.TryParse(Args("cli_build_snap_points", "1", "2", "3"), out SnapPointsRequest? request, out _));
            Assert.Equal(SnapPointsRequest.DefaultRadius, request!.Radius);
            Assert.Equal("", request.NameFilter);

            Assert.True(SnapPointsRequest.TryParse(Args("cli_build_snap_points", "1", "2", "3", "8", "floor"), out request, out _));
            Assert.Equal(8f, request!.Radius);
            Assert.Equal("floor", request.NameFilter);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("2000")]
        [InlineData("wide")]
        public void SnapPointListingRefusesABadRadius(string radius)
        {
            Assert.False(SnapPointsRequest.TryParse(Args("cli_build_snap_points", "1", "2", "3", radius), out SnapPointsRequest? request, out string error));
            Assert.Null(request);
            Assert.Contains("radius", error);
        }

        [Fact]
        public void SnapPointListingNeedsThreeCoordinates()
        {
            Assert.False(SnapPointsRequest.TryParse(Args("cli_build_snap_points", "1", "2"), out _, out string error));
            Assert.Equal(SnapPointsRequest.Usage, error);
            Assert.False(SnapPointsRequest.TryParse(Args("cli_build_snap_points", "1", "2", "3", "4", "floor", "extra"), out _, out error));
            Assert.Equal(SnapPointsRequest.Usage, error);
        }

        private static void Near(Point3 expected, Point3 actual)
        {
            Assert.True(expected.DistanceTo(actual) < 1e-4f, $"expected {expected.Format()} got {actual.Format()}");
        }

        /// <summary>Unity's Quaternion.Euler(0, yaw, 0): +Z turns towards +X, and +X towards -Z.</summary>
        [Fact]
        public void YawTurnsTheWayUnityDoes()
        {
            Near(new Point3(1, 0, 0), SnapGeometry.RotateYaw(new Point3(0, 0, 1), 90f));
            Near(new Point3(0, 0, -1), SnapGeometry.RotateYaw(new Point3(1, 0, 0), 90f));
            Near(new Point3(0, 5, -1), SnapGeometry.RotateYaw(new Point3(0, 5, 1), 180f));
            Near(new Point3(2, 3, 4), SnapGeometry.RotateYaw(new Point3(2, 3, 4), 360f));
        }

        [Fact]
        public void ChildPositionsFollowTheRootsScaleThenItsYaw()
        {
            Point3 origin = new Point3(100, 20, -50);
            Near(new Point3(101, 20, -50), SnapGeometry.ChildToWorld(origin, 0f, new Point3(1, 1, 1), new Point3(1, 0, 0)));
            Near(new Point3(100, 20, -52), SnapGeometry.ChildToWorld(origin, 90f, new Point3(2, 1, 1), new Point3(1, 0, 0)));
            Near(new Point3(100, 21.5f, -50), SnapGeometry.ChildToWorld(origin, 45f, new Point3(1, 1.5f, 1), new Point3(0, 1, 0)));
        }

        /// <summary>
        /// Two 2x2 floors: the new one's corners, asked for half a metre short of
        /// the built one's edge, snap onto the shared edge -- one exact pair and
        /// an offset that moves the whole piece.
        /// </summary>
        [Fact]
        public void TheClosestPairWinsAndGivesTheOffset()
        {
            List<Point3> built = new List<Point3> { new Point3(1, 0, 1), new Point3(1, 0, -1), new Point3(-1, 0, 1), new Point3(-1, 0, -1) };
            List<Point3> mine = new List<Point3> { new Point3(1.4f, 0, 1), new Point3(1.4f, 0, -1), new Point3(3.4f, 0, 1), new Point3(3.4f, 0, -1) };
            Assert.True(SnapGeometry.TryClosestPair(mine, built, 0.5f, out SnapMatch match, out float nearest));
            Assert.Equal(0, match.Mine);
            Assert.Equal(0, match.Theirs);
            Assert.Equal(0.4f, match.Gap, 4);
            Assert.Equal(0.4f, nearest, 4);
            Point3 offset = built[match.Theirs] - mine[match.Mine];
            Near(new Point3(-0.4f, 0, 0), offset);
        }

        /// <summary>The game compares strictly, so of two equally close pairs the first found is kept.</summary>
        [Fact]
        public void TiesKeepTheFirstPair()
        {
            // Both pairs are exactly 0.25 m apart.
            List<Point3> mine = new List<Point3> { new Point3(0, 0, 0), new Point3(1, 0, 0) };
            List<Point3> theirs = new List<Point3> { new Point3(1.25f, 0, 0), new Point3(-0.25f, 0, 0) };
            Assert.True(SnapGeometry.TryClosestPair(mine, theirs, 0.5f, out SnapMatch match, out _));
            Assert.Equal(0, match.Mine);
            Assert.Equal(1, match.Theirs);
        }

        [Fact]
        public void ANearMissIsReportedAsADistance()
        {
            List<Point3> mine = new List<Point3> { new Point3(0, 0, 0) };
            List<Point3> theirs = new List<Point3> { new Point3(0.75f, 0, 0), new Point3(3, 0, 0) };
            Assert.False(SnapGeometry.TryClosestPair(mine, theirs, 0.5f, out _, out float nearest));
            Assert.Equal(0.75f, nearest, 4);

            Assert.False(SnapGeometry.TryClosestPair(mine, new List<Point3>(), 0.5f, out _, out nearest));
            Assert.True(float.IsPositiveInfinity(nearest));
        }

        [Fact]
        public void AGapExactlyAtTheRadiusStillSnaps()
        {
            List<Point3> mine = new List<Point3> { new Point3(0, 0, 0) };
            List<Point3> theirs = new List<Point3> { new Point3(0.5f, 0, 0) };
            Assert.True(SnapGeometry.TryClosestPair(mine, theirs, 0.5f, out _, out _));
        }

        [Fact]
        public void TheSamePieceInTheSamePlaceBlocksTheSnap()
        {
            Assert.True(SnapGeometry.OverlapsSamePiece(0.01f, 0f, allowRotatedOverlap: false));
            Assert.True(SnapGeometry.OverlapsSamePiece(0.01f, 90f, allowRotatedOverlap: false));
            Assert.False(SnapGeometry.OverlapsSamePiece(0.01f, 90f, allowRotatedOverlap: true));
            Assert.True(SnapGeometry.OverlapsSamePiece(0.01f, 5f, allowRotatedOverlap: true));
            Assert.False(SnapGeometry.OverlapsSamePiece(0.06f, 0f, allowRotatedOverlap: false));
        }

        [Theory]
        [InlineData(0f, 0)]
        [InlineData(22.5f, 1)]
        [InlineData(90f, 4)]
        [InlineData(337.5f, 15)]
        [InlineData(360f, 0)]
        [InlineData(-22.5f, 15)]
        [InlineData(-90f, 12)]
        public void AHammerHeadingHasAStep(float yaw, int step)
        {
            Assert.Equal(step, SnapGeometry.HammerStep(yaw));
        }

        [Theory]
        [InlineData(10f)]
        [InlineData(45.5f)]
        [InlineData(-1f)]
        public void AHeadingBetweenStepsIsNotOneAPlayerCanSet(float yaw)
        {
            Assert.Null(SnapGeometry.HammerStep(yaw));
        }

        [Fact]
        public void TheFirstFailingRuleIsReported()
        {
            Assert.Null(PlacementRules.Refusal(true, false, false, false, false));
            Assert.Equal("not_loaded", PlacementRules.Refusal(false, true, true, true, true));
            Assert.Equal("no_build_zone", PlacementRules.Refusal(true, true, true, true, true));
            Assert.Equal("private_zone", PlacementRules.Refusal(true, false, true, true, true));
            Assert.Equal("wrong_biome", PlacementRules.Refusal(true, false, false, true, true));
            Assert.Equal("missing_requirements", PlacementRules.Refusal(true, false, false, false, true));
        }
    }
}
