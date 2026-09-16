using System.Collections.Generic;
using valheimCLI;
using Xunit;

namespace valheimCLI.Tests
{
    /// <summary>
    /// cli_terrain_edit's argument rule, which is all of the command that can be
    /// decided without Unity. The game-side half only looks the op up in
    /// ObjectDB and instantiates it, because instantiating a terrain op IS the
    /// edit -- TerrainOp.Awake applies it and destroys itself.
    ///
    /// The point of the command is that the edit is the GAME's, not ours, so the
    /// only thing worth guarding here is that a caller cannot hand the terrain
    /// system something it should never see.
    /// </summary>
    public class TerrainEditRequestTests
    {
        private static List<string> Args(params string[] tokens) => new List<string>(tokens);

        private static bool Parse(List<string>? args, out TerrainEditRequest? request, out string error) =>
            TerrainEditRequest.TryParse(args, out request, out error);

        [Fact]
        public void AWellFormedEditIsAccepted()
        {
            Assert.True(Parse(Args("cli_terrain_edit", "raise", "100.5", "32", "-64.25"),
                out TerrainEditRequest? request, out string error));
            Assert.NotNull(request);
            Assert.Equal(string.Empty, error);
            Assert.Equal("raise", request!.Op);
            Assert.Equal(100.5f, request.X);
            Assert.Equal(32f, request.Y);
            Assert.Equal(-64.25f, request.Z);
        }

        /// <summary>Coordinates are read the same way everywhere else in this mod: invariant culture.</summary>
        [Fact]
        public void CoordinatesAreReadInInvariantCulture()
        {
            Assert.True(Parse(Args("cli_terrain_edit", "digg", "1.5", "0.25", "2.75"),
                out TerrainEditRequest? request, out _));
            Assert.Equal(1.5f, request!.X);
            Assert.Equal(0.25f, request.Y);
            Assert.Equal(2.75f, request.Z);
        }

        [Theory]
        [InlineData(3)]  // op and one coordinate
        [InlineData(4)]  // a coordinate short
        [InlineData(6)]  // one too many
        public void TheWrongNumberOfArgumentsIsRefused(int count)
        {
            List<string> args = new List<string>();
            string[] all = { "cli_terrain_edit", "raise", "1", "2", "3", "4" };
            for (int i = 0; i < count; i++)
            {
                args.Add(all[i]);
            }
            Assert.False(Parse(args, out TerrainEditRequest? request, out string error));
            Assert.Null(request);
            Assert.Equal(TerrainEditRequest.Usage, error);
        }

        [Fact]
        public void NoArgumentsAtAllIsRefusedRatherThanThrowing()
        {
            Assert.False(Parse(null, out TerrainEditRequest? request, out string error));
            Assert.Null(request);
            Assert.Equal(TerrainEditRequest.Usage, error);
        }

        [Fact]
        public void AnEmptyOpNameIsRefused()
        {
            Assert.False(Parse(Args("cli_terrain_edit", "   ", "1", "2", "3"),
                out TerrainEditRequest? request, out string error));
            Assert.Null(request);
            Assert.Contains("no terrain op named", error);
        }

        [Theory]
        [InlineData("x", 2)]
        [InlineData("y", 3)]
        [InlineData("z", 4)]
        public void ACoordinateThatIsNotANumberIsRefusedAndNamed(string which, int index)
        {
            string[] tokens = { "cli_terrain_edit", "raise", "1", "2", "3" };
            tokens[index] = "over-there";
            Assert.False(Parse(Args(tokens), out TerrainEditRequest? request, out string error));
            Assert.Null(request);
            Assert.Contains(which + " is not a number", error);
            Assert.Contains("over-there", error);
        }

        /// <summary>
        /// The one that matters. float.TryParse accepts "NaN" and "Infinity", and
        /// a non-finite position would be handed straight to the terrain system.
        /// A test whose job is to say whether an edit survived cannot afford
        /// "something strange happened" as an outcome.
        /// </summary>
        [Theory]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("-Infinity")]
        public void ANonFiniteCoordinateIsRefused(string value)
        {
            Assert.False(Parse(Args("cli_terrain_edit", "raise", value, "2", "3"),
                out TerrainEditRequest? request, out string error));
            Assert.Null(request);
            Assert.Contains("finite", error);
        }

        [Fact]
        public void TheOpNameIsTrimmed()
        {
            Assert.True(Parse(Args("cli_terrain_edit", "  raise  ", "1", "2", "3"),
                out TerrainEditRequest? request, out _));
            Assert.Equal("raise", request!.Op);
        }

        [Fact]
        public void DescribeNamesTheOpAndThePoint()
        {
            Assert.True(Parse(Args("cli_terrain_edit", "raise", "10", "20", "30"),
                out TerrainEditRequest? request, out _));
            Assert.Equal("raise at 10.0,20.0,30.0", request!.Describe());
        }
    }
}
