using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// Where a dump takes its samples. The point of the lattice is that a dumped
/// position is one the game itself asks about, so a reader offline can return
/// the value unchanged; a window that drifted off it would turn every sample
/// into an interpolation and quietly weaken every measurement made from it.
/// </summary>
public class WorldDumpGridTests
{
    [Theory]
    [InlineData(128)]  // the island grid: i*128 - 10000
    [InlineData(8)]    // road pathfinding cells: multiples of 8
    [InlineData(50)]   // the map dumps used for pictures
    public void TheFullMapRunsFromTheOriginToTheFarEdge(int step)
    {
        Assert.Equal(0, WorldDumpGrid.From(-10000f, step));
        Assert.Equal(-10000f, WorldDumpGrid.At(0, step));
        Assert.Equal(20000 / step, WorldDumpGrid.To(10000f, step));
    }

    [Fact]
    public void EightMetreSamplesLandOnThePathfindersOwnCells()
    {
        // The pathfinder puts cell centres at multiples of 8, and the origin is
        // one, so every sample of an 8 m dump is a cell centre - anywhere in
        // the world, and in a window as much as in the full map.
        for (int index = 0; index < 5; index++)
        {
            Assert.Equal(0f, WorldDumpGrid.At(index, 8) % 8f);
        }

        int from = WorldDumpGrid.From(1234f, 8);
        Assert.True(WorldDumpGrid.At(from, 8) >= 1234f);
        Assert.True(WorldDumpGrid.At(from - 1, 8) < 1234f);
        Assert.Equal(0f, WorldDumpGrid.At(from, 8) % 8f);
    }

    [Fact]
    public void A128MetreWindowKeepsTheIslandGridsOwnOffset()
    {
        // The island grid is not a multiple of 128: it is i*128 - 10000, and
        // -10000 is 112 short of a multiple. A window has to keep that offset
        // or island detection offline would read between the game's samples.
        int from = WorldDumpGrid.From(0f, 128);
        float sample = WorldDumpGrid.At(from, 128);
        Assert.Equal(112f, ((sample % 128f) + 128f) % 128f);
        Assert.True(sample >= 0f && sample < 128f);
    }

    [Fact]
    public void AWindowHoldsOnlySamplesInsideIt()
    {
        // 8 m samples between 1000 and 1064: the ends are on the lattice, so
        // both are included and nothing outside is.
        int from = WorldDumpGrid.From(1000f, 8);
        int to = WorldDumpGrid.To(1064f, 8);
        Assert.Equal(1000f, WorldDumpGrid.At(from, 8));
        Assert.Equal(1064f, WorldDumpGrid.At(to, 8));
        Assert.Equal(9, to - from + 1);

        // A window narrower than one step holds no sample at all, and the
        // caller is told rather than handed an empty file.
        Assert.True(WorldDumpGrid.To(1006f, 8) < WorldDumpGrid.From(1001f, 8));
    }

    [Theory]
    [InlineData("100,200,300", 100f, 200f, 300f)]
    [InlineData("-1234.5,0,64", -1234.5f, 0f, 64f)]
    public void AWindowIsThreeNumbers(string spec, float x, float z, float half)
    {
        Assert.True(WorldDumpGrid.TryParseWindow(spec, out float cx, out float cz, out float parsedHalf));
        Assert.Equal(x, cx);
        Assert.Equal(z, cz);
        Assert.Equal(half, parsedHalf);
    }

    [Theory]
    [InlineData("")]
    [InlineData("100,200")]
    [InlineData("100,200,300,400")]
    [InlineData("100,200,0")]
    [InlineData("100,200,-5")]
    [InlineData("a,b,c")]
    public void AnythingElseIsRefusedRatherThanGuessedAt(string spec)
    {
        Assert.False(WorldDumpGrid.TryParseWindow(spec, out float _, out float _, out float _));
    }
}
