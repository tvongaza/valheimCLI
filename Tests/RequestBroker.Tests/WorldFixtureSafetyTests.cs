using Xunit;

namespace valheimCLI.Tests
{
    public class WorldFixtureSafetyTests
    {
        [Theory]
        [InlineData("NaN,0,1")]
        [InlineData("0,Infinity,1")]
        [InlineData("0,0,NaN")]
        [InlineData("0,0,Infinity")]
        [InlineData("1e30,0,1")]
        [InlineData("0,0,20001")]
        public void AWindowMustBeFiniteAndBounded(string text)
        {
            Assert.False(WorldDumpGrid.TryParseWindow(text, out _, out _, out _));
        }

        [Fact]
        public void HugeExportsAreRejectedBeforeAnyWork()
        {
            Assert.False(WorldDumpGrid.TrySampleCount(0, 4000, 0, 4000, 5, out _));
            Assert.False(WorldDumpGrid.TrySampleCount(int.MinValue, int.MaxValue, 0, 0, 50, out _));
            Assert.False(WorldDumpGrid.TrySampleCount(0, 1, 0, 1, 0, out _));
            Assert.False(WorldDumpGrid.TrySampleCount(2, 1, 0, 1, 50, out _));
            Assert.True(WorldDumpGrid.TrySampleCount(0, 400, 0, 400, 50, out long count));
            Assert.Equal(160801, count);
            Assert.True(WorldDumpGrid.TrySampleCount(10, 18, 20, 28, 8, out count));
            Assert.Equal(81, count);
        }

        [Theory]
        [InlineData(false, false, false, true, false)]
        [InlineData(true, true, false, true, false)]
        [InlineData(true, true, true, false, false)]
        [InlineData(true, true, true, true, true)]
        [InlineData(true, false, false, true, true)]
        public void FixturesCannotSilentlyReplaceAnExistingOrCloudWorld(bool menu, bool exists, bool overwrite, bool local, bool allowed)
        {
            Assert.Equal(allowed, WorldFixturePolicy.CanCreate(menu, exists, overwrite, local, out string reason));
            Assert.Equal(allowed, string.IsNullOrEmpty(reason));
        }
    }
}
