using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// cli_rock_hit: the argument rules and the reply lines. A hit on a rock names the rock and the
/// pieces left before and after; a sphere with no rock answers a plain zero; bad input is refused
/// before anything is damaged. The game's damage itself (MineRock5.Damage) needs a running game.
/// </summary>
public class RockHitRequestTests
{
    [Fact]
    public void ParsesPointRadiusAndTheDefaultDamage()
    {
        Assert.True(RockHitRequest.TryParse(Args("cli_rock_hit -8918.5 71 2640 2.5"), out RockHitRequest r, out string error));
        Assert.Equal(string.Empty, error);
        Assert.Equal(-8918.5f, r.X);
        Assert.Equal(71f, r.Y);
        Assert.Equal(2640f, r.Z);
        Assert.Equal(2.5f, r.Radius);
        Assert.Equal(RockHitRequest.DefaultDamage, r.Damage);
    }

    [Fact]
    public void ParsesAGivenDamage()
    {
        Assert.True(RockHitRequest.TryParse(Args("cli_rock_hit 1 2 3 64 25.5"), out RockHitRequest r, out _));
        Assert.Equal(64f, r.Radius); // the largest radius is allowed
        Assert.Equal(25.5f, r.Damage);
    }

    [Theory]
    [InlineData("cli_rock_hit")]
    [InlineData("cli_rock_hit 1 2 3")]
    [InlineData("cli_rock_hit 1 2 3 4 5 6")]
    [InlineData("cli_rock_hit x 2 3 4")]
    [InlineData("cli_rock_hit 1 NaN 3 4")]
    [InlineData("cli_rock_hit 1 2 Infinity 4")]
    [InlineData("cli_rock_hit 1 2 3 0")]
    [InlineData("cli_rock_hit 1 2 3 -1")]
    [InlineData("cli_rock_hit 1 2 3 64.1")]
    [InlineData("cli_rock_hit 1 2 3 wide")]
    [InlineData("cli_rock_hit 1 2 3 4 0")]
    [InlineData("cli_rock_hit 1 2 3 4 -5")]
    [InlineData("cli_rock_hit 1 2 3 4 heavy")]
    [InlineData("cli_rock_hit 1 2 3 4 NaN")]
    public void RefusesBadInputWithTheUsage(string line)
    {
        Assert.False(RockHitRequest.TryParse(Args(line), out _, out string error));
        Assert.Contains(RockHitRequest.Usage, error);
    }

    [Fact]
    public void ARadiusErrorSaysTheLimits()
    {
        Assert.False(RockHitRequest.TryParse(Args("cli_rock_hit 1 2 3 100"), out _, out string error));
        Assert.Contains("above 0 and at most 64.00", error);
    }

    [Fact]
    public void AMissAnswersZeroRocks()
    {
        Assert.True(RockHitRequest.TryParse(Args("cli_rock_hit 1 2 3 4"), out RockHitRequest r, out _));
        Assert.Equal("OK: 0 rock(s) inside the sphere", r.Summary(0));
    }

    [Fact]
    public void AHitNamesTheSphereAndTheDamage()
    {
        Assert.True(RockHitRequest.TryParse(Args("cli_rock_hit -8918.5 71 2640 2.5"), out RockHitRequest r, out _));
        Assert.Equal("OK: 2 rock(s) hit at -8918.50,71.00,2640.00 r=2.50 damage=1000.00", r.Summary(2));
    }

    [Fact]
    public void ABreakableBoulderRowShowsThePiecesLeftBeforeAndAfter()
    {
        Assert.Equal(
            "  ROCKHIT pieces cliff_mistlands2_frac pos=-8916.25,68.22,2639.75 scale=0.50 owner=True pieces=103 left_before=103 left_after=82",
            RockHitRequest.PiecesRow("cliff_mistlands2_frac", -8916.25f, 68.22f, 2639.75f, 0.5f, true, 103, 103, 82));
    }

    [Fact]
    public void AWholeRockRowShowsTheSwapForItsFracturedCopy()
    {
        Assert.Equal(
            "  ROCKHIT swap cliff_mistlands2 pos=-8880.00,52.90,2700.00 scale=1.00 owner=False -> cliff_mistlands2_frac",
            RockHitRequest.SwapRow("cliff_mistlands2", -8880f, 52.9f, 2700f, 1f, false, "cliff_mistlands2_frac"));
    }

    private static string[] Args(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
