using System;
using System.Linq;
using valheimCLI;
using valheim_cli.Testing;
using Xunit;

namespace valheimCLI.Tests
{
    public class SessionArgumentsTests
    {
        [Theory]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("not-a-yaw")]
        public void SuppliedBadYawIsNotSilentlyZero(string input)
        {
            Assert.False(SessionArguments.TryYaw(input, out _));
        }

        [Fact]
        public void OmittedYawHasAnExplicitDefault()
        {
            Assert.True(SessionArguments.TryYaw(null, out float yaw));
            Assert.Equal(0f, yaw);
            Assert.True(SessionArguments.TryYaw("-22.5", out yaw));
            Assert.Equal(-22.5f, yaw);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-1")]
        [InlineData("1.5")]
        [InlineData("10001")]
        [InlineData("2147483648")]
        public void CartLoadsHaveABoundedPositiveQuantity(string input)
        {
            Assert.False(SessionArguments.TryCartLoadCount(input, out _));
        }

        [Fact]
        public void CartLoadDoesNotLoseTheRemainderAboveOneStack()
        {
            Assert.Equal(new[] { 50, 50, 23 }, SessionArguments.StackBatches(123, 50));
            Assert.Equal(123, SessionArguments.StackBatches(123, 50).Sum());
            Assert.Equal(new[] { 1, 1, 1 }, SessionArguments.StackBatches(3, 1));
            Assert.Equal(new[] { 50 }, SessionArguments.StackBatches(50, 50));
        }

        [Fact]
        public void CartLoadSuccessRequiresTheRequestedInventoryIncrease()
        {
            Assert.True(SessionArguments.CartLoadComplete(123, 10, 133));
            Assert.False(SessionArguments.CartLoadComplete(123, 10, 60));
            Assert.False(SessionArguments.CartLoadComplete(123, 10, 10));
            Assert.False(SessionArguments.CartLoadComplete(123, 10, 200));
        }

        [Theory]
        [InlineData(0, 50)]
        [InlineData(1, 0)]
        public void BadStackInputsCannotLoopForever(int count, int stack)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SessionArguments.StackBatches(count, stack).ToArray());
        }

        [Theory]
        [InlineData("cli_teleport_peer", "server-world")]
        [InlineData("cli_terrain_edit", "loaded-world")]
        [InlineData("cli_spawn_piece", "loaded-world")]
        [InlineData("cli_cart", "local-player")]
        [InlineData("cli_build_rotate", "local-player")]
        [InlineData("cli_clutter", "loaded-world")]
        [InlineData("cli_build_snap_points", "loaded-world")]
        [InlineData("cli_build_place_at", "local-player")]
        [InlineData("cli_build_place_snapped", "local-player")]
        [InlineData("cli_fly", "local-player")]
        [InlineData("cli_set_player_safety", "local-player")]
        public void ActionsDeclareExecutionContext(string name, string context)
        {
            Assert.Equal(context, CommandMetadata.GetPrecondition(new CommandInfo { Name = name }));
        }
    }
}
