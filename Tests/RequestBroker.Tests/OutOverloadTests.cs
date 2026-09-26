using System.Reflection;
using valheimCLI;
using Xunit;

namespace valheimCLI.Tests;

public class OutOverloadTests
{
    [Theory]
    [InlineData("10 20", false)]
    [InlineData("10.0 20.0", false)]
    [InlineData("10 20", true)]
    [InlineData("10.0 20.0", true)]
    public void HeightChoosesTwoInputsRegardlessOfReflectionOrder(string arguments, bool reverse)
    {
        var methods = Methods(nameof(HeightOverloadFixture.GetHeight));
        if (reverse) methods.Reverse();
        Assert.True(StaticMemberCall.TryTokenize(arguments, out var tokens, out _));
        Assert.True(StaticMemberCall.TrySelectOverload(methods, tokens, out var choice, out var failure), failure?.Message);
        Assert.Equal(2, choice!.Method.GetParameters().Length);
    }

    [Fact]
    public void SingletonCallInvokesHeightWithoutImplicitOutput()
    {
        var output = Run("HeightOverloadFixture.instance.GetHeight 10 20");
        Assert.Contains("VALUE 30", output);
        Assert.Contains(output, line => line.StartsWith("OK: CALL "));
        Assert.DoesNotContain(output, line => line.StartsWith("OUT "));
    }

    [Fact]
    public void OutOnlyMethodStillReturnsItsOutput()
    {
        var output = Run("HeightOverloadFixture.instance.Sample 10");
        Assert.Contains("VALUE 10", output);
        Assert.Contains("OUT colour=\"green\"", output);
        Assert.Contains(output, line => line.StartsWith("OK: CALL "));
    }

    [Fact]
    public void EquallyGoodInputsAndOutputCountsRemainAmbiguous()
    {
        Assert.True(StaticMemberCall.TryTokenize("10", out var tokens, out _));
        Assert.False(StaticMemberCall.TrySelectOverload(Methods(nameof(HeightOverloadFixture.Ambiguous)), tokens, out _, out var failure));
        Assert.Equal("ambiguous_overload", failure!.Code);
    }

    [Fact]
    public void BetterInputConversionStillWinsOverFewerOutputs()
    {
        var output = Run("HeightOverloadFixture.instance.Numeric 10");
        Assert.Contains("VALUE \"integer\"", output);
        Assert.Contains("OUT result=10", output);
    }

    private static List<MethodInfo> Methods(string name) => typeof(HeightOverloadFixture)
        .GetMethods().Where(method => method.Name == name).ToList();

    private static List<string> Run(string command)
    {
        var index = new StaticMemberCall.TypeIndex(new[] { typeof(HeightOverloadFixture).Assembly });
        var output = new List<string>();
        StaticMemberCall.Run(index, command, output.Add);
        return output;
    }
}

public class HeightOverloadFixture
{
    public static HeightOverloadFixture instance = new();
    public struct Tint { public float r, g, b, a; }
    public float GetHeight(float x, float z) => x + z;
    public float GetHeight(float x, float z, out Tint colour) { colour = default; return -1; }
    public float Sample(float x, out string colour) { colour = "green"; return x; }
    public void Ambiguous(float x, out int value) { value = 1; }
    public void Ambiguous(float x, out string value) { value = "one"; }
    public string Numeric(int x, out int result) { result = x; return "integer"; }
    public string Numeric(double x) => "double";
}
