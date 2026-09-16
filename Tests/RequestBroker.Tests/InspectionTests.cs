using System;
using System.Collections.Generic;
using System.Globalization;
using valheimCLI;
using valheim_cli.Testing;
using Xunit;

namespace valheimCLI.Tests
{
    public class InspectionTests
    {
        [Theory]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("-Infinity")]
        [InlineData("1e100")]
        [InlineData("garbage")]
        public void CoordinatesMustBeFinite(string input)
        {
            Assert.False(CommandArguments.TryFiniteFloat(input, out _));
        }

        [Fact]
        public void NumbersUseInvariantCulture()
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                Assert.True(CommandArguments.TryFiniteFloat("-12.5", out float value));
                Assert.Equal(-12.5f, value);
                Assert.False(CommandArguments.TryFiniteFloat("12,5", out _));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-1")]
        [InlineData("1025")]
        [InlineData("NaN")]
        public void CensusRadiusCannotBeUnbounded(string input)
        {
            Assert.False(CommandArguments.TryRadius(input, out _));
        }

        [Fact]
        public void CensusRejectsSectorWrapAndAcceptsNegativeWorldCoordinates()
        {
            Assert.True(CommandArguments.CanScan(-10000, -10000, 40));
            Assert.False(CommandArguments.CanScan(2047990, 0, 40));
            Assert.False(CommandArguments.CanScan(0, -2047990, 40));
            Assert.True(CommandArguments.TryRadius("1024", out _));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void CapsuleExtentUsesItsActualAxis(int direction)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                Assert.Equal(axis == direction ? 3f : 1f,
                    CommandArguments.CapsuleExtent(1, 6, direction, axis));
            }
            Assert.Equal(2f, CommandArguments.CapsuleExtent(2, 1, direction, direction));
        }

        [Theory]
        [InlineData("cli_peers", "server-world")]
        [InlineData("cli_zdos_at", "server-world")]
        [InlineData("cli_containers_at", "server-world")]
        [InlineData("cli_ground_height", "loaded-world")]
        [InlineData("cli_paint_at", "loaded-world")]
        [InlineData("cli_piece_geometry", "loaded-world")]
        [InlineData("cli_surface_at", "loaded-world")]
        [InlineData("cli_prefabs_at", "loaded-world")]
        [InlineData("cli_nearby_prefabs", "local-player")]
        public void CommandsDeclareTheirActualContext(string name, string context)
        {
            Assert.Equal(context, CommandMetadata.GetPrecondition(new CommandInfo { Name = name }));
        }

        [Fact]
        public void AnUnreadableInventoryFailsEvenWithRowsAndATerminator()
        {
            CommandResult result = CommandResult.FromOutput("cli_containers_at 0 0", new List<string>
            {
                "CONTAINER Chest id=1:2 bytes=10",
                "ERROR: inventory incomplete: saved=2 decoded=1 container=1:2",
                "OK: CONTAINERS_AT 0,0 r=40 containers=1"
            });
            Assert.False(result.Ok);
            Assert.Equal("command_failed", result.ErrorCode);
        }
    }
}
