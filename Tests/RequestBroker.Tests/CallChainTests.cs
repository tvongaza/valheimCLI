using System;
using System.Collections.Generic;
using CallFixtures;
using valheimCLI;
using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// Instance members of a static member's value (Type.Member.Member) and
/// @Type.Member arguments, against the Generator singleton fixture.
/// </summary>
public class CallChainTests
{
    private static readonly StaticMemberCall.TypeIndex Index =
        new StaticMemberCall.TypeIndex(new[] { typeof(Generator).Assembly });

    private static List<string> Run(string line)
    {
        List<string> output = new List<string>();
        StaticMemberCall.Run(Index, line, output.Add);
        return output;
    }

    private static string ErrorCode(List<string> output)
    {
        string error = Assert.Single(output, l => l.StartsWith("ERROR: ", StringComparison.Ordinal));
        return error.Substring("ERROR: code=".Length).Split(' ')[0];
    }

    // --------------------------------------------------------------- chains

    [Fact]
    public void AnInstanceMethodOfASingletonIsCalledWithOverloadsChosenAsForStatics()
    {
        Assert.Equal(new[]
        {
            "VALUE Meadows",
            "OK: CALL CallFixtures.Generator.GetBiome kind=method overload=(float,float,float,bool) type=Biome via=CallFixtures.Generator.instance",
        }, Run("Generator.instance.GetBiome 531 -134"));
        Assert.Equal("VALUE Swamp", Run("Generator.instance.GetBiome 531,0,-134")[0]);
        Assert.Equal("VALUE Mountain", Run("Generator.instance.GetBiome 8,-2")[0]);
        Assert.Equal("VALUE Swamp", Run("Generator.instance.GetBiome -1 0 0.5 true")[0]);
    }

    [Fact]
    public void InstanceFieldsPropertiesAndPrivateMethodsAreReached()
    {
        Assert.Equal(new[] { "VALUE 7", "OK: CALL CallFixtures.Generator.Seed kind=property type=int via=CallFixtures.Generator.instance" },
            Run("Generator.instance.Seed"));
        Assert.Equal("OK: CALL CallFixtures.Generator.Scale kind=field type=float via=CallFixtures.Generator.instance",
            Run("Generator.instance.Scale")[1]);
        Assert.Equal("VALUE 70", Run("Generator.instance.Secret")[0]);
        Assert.Equal("VALUE \"derived\"", Run("DerivedGenerator.Special.Kind")[0]);
        Assert.Equal("VALUE \"generator 5\"", Run("DerivedGenerator.Special.Name")[0]);
        Assert.Equal("VALUE 50", Run("DerivedGenerator.Special.Secret")[0]); // private, declared on the base class
        Assert.Equal("no_member", ErrorCode(Run("Diagnostics.MakeSample.Item"))); // an indexer is not a member to read
        Assert.Equal("VALUE 3", Run("Diagnostics.MakeSample.Count")[0]);
    }

    [Fact]
    public void ChainsGoDeeperAndMayStartAtAParameterlessStaticMethod()
    {
        Assert.Equal(new[] { "VALUE 64", "OK: CALL CallFixtures.Settings.Radius kind=field type=int via=CallFixtures.Generator.instance.Config" },
            Run("Generator.instance.Config.Radius"));
        Assert.Equal(new[] { "VALUE \"generator 3\"", "OK: CALL CallFixtures.Generator.Name kind=method type=string via=CallFixtures.Generator.Make" },
            Run("Generator.Make.Name"));
    }

    [Fact]
    public void AStaticMemberWithTheWholePathWinsOverAChain()
    {
        Assert.Equal("VALUE \"static\"", Run("Holder.Slot.Item")[0]);
        Assert.Equal("VALUE \"chain\"", Run("Other.Holder.Slot.Item")[0]);
    }

    [Fact]
    public void ANullValueHasNothingToCallOn()
    {
        List<string> output = Run("Generator.Missing.Name");
        Assert.Equal(new[]
        {
            "ERROR: code=null_target message=CallFixtures.Generator.Missing is null; there is no object to use Name on",
        }, output);
    }

    [Fact]
    public void AMissingInstanceMemberListsTheInstanceMembers()
    {
        List<string> output = Run("Generator.instance.name");
        Assert.Equal("no_member", ErrorCode(output));
        Assert.Contains("CallFixtures.Generator.instance is a CallFixtures.Generator, which has no instance field, property or method named name", output[0]);
        Assert.Contains("  did you mean CallFixtures.Generator.instance.Name?", output);
        string members = Assert.Single(output, l => l.StartsWith("  instance members: ", StringComparison.Ordinal));
        Assert.Contains(" GetBiome ", members);
        Assert.Contains(" Secret ", members);
        Assert.DoesNotContain("get_Seed", members);
        Assert.DoesNotContain("GetHashCode", members);
    }

    [Fact]
    public void TheErrorComesFromTheReadingThatGotFurthest()
    {
        // The head is a type with no such static member: say so, with its members.
        List<string> typo = Run("Generator.instanc.Name");
        Assert.Equal("no_member", ErrorCode(typo));
        Assert.Contains("CallFixtures.Generator has no static field, property or method named instanc", typo[0]);
        Assert.Contains(typo, l => l.StartsWith("  static members: ", StringComparison.Ordinal) && l.Contains(" instance"));
        Assert.Contains("  did you mean CallFixtures.Generator.instance?", Run("Generator.Instance.Name"));
        // The head is a type name: the static reading's error stands.
        List<string> nested = Run("Outer.Nested.Nope");
        Assert.Equal("no_member", ErrorCode(nested));
        Assert.Contains("CallFixtures.Outer.Nested has no static field, property or method named Nope", nested[0]);
        // Nothing of it exists.
        List<string> nothing = Run("Nothing.At.All");
        Assert.Equal("no_type", ErrorCode(nothing));
        Assert.Contains("no loaded type named Nothing.At", nothing[0]);
    }

    [Fact]
    public void AChainHeadMustBeReadableWithoutArguments()
    {
        List<string> output = Run("Generator.Seeded.Name");
        Assert.Equal("not_callable", ErrorCode(output));
        Assert.Contains("CallFixtures.Generator.Seeded takes arguments", output[0]);
        Assert.Contains("  overload: CallFixtures.Generator.Seeded(int seed)", output);
    }

    [Fact]
    public void ExceptionsNameThePathThatThrew()
    {
        Assert.Equal(new[]
        {
            "ERROR: code=call_threw message=CallFixtures.Generator.instance.Fail threw System.InvalidOperationException: not ready",
        }, Run("Generator.instance.Fail"));
        Assert.Equal(new[]
        {
            "ERROR: code=call_threw message=CallFixtures.Generator.Exploding threw System.InvalidOperationException: no world loaded",
        }, Run("Generator.Exploding.Name"));
        List<string> field = Run("Generator.instance.Scale 3");
        Assert.Equal("bad_argument", ErrorCode(field));
        Assert.Contains("CallFixtures.Generator.instance.Scale is a field", field[0]);
    }

    // ----------------------------------------------------------- references

    [Fact]
    public void AReferencePassesTheMembersValueAsTheArgument()
    {
        Assert.Equal(new[]
        {
            "VALUE 404",
            "OK: CALL CallFixtures.Blend.Height kind=method type=float ref3=CallFixtures.Generator.instance",
        }, Run("Blend.Height 531 -134 @Generator.instance"));
        Assert.Equal("VALUE Info Seed=7 At=1.5,0,2 Biome=Meadows", Run("Blend.Debug 1.5 2 @Generator.instance")[0]);
    }

    [Fact]
    public void AReferencesTypeChoosesTheOverload()
    {
        Assert.Equal("VALUE \"generator 7\"", Run("Blend.Describe @Generator.instance")[0]);
        Assert.Equal("VALUE \"generator 5\"", Run("Blend.Describe @DerivedGenerator.Special")[0]);
        Assert.Equal("VALUE \"object 42\"", Run("Blend.Describe @CallProbe.Answer")[0]);
    }

    [Fact]
    public void ReferencesMayBeNumbersNullsOrChains()
    {
        Assert.Equal("VALUE 6", Run("Blend.Twice @Diagnostics.Version")[0]);
        Assert.Equal("VALUE \"none\"", Run("Blend.Maybe @Generator.Missing")[0]);
        List<string> chained = Run("Blend.Twice @Generator.instance.Scale");
        Assert.Equal(new[] { "VALUE 4", "OK: CALL CallFixtures.Blend.Twice kind=method type=double ref1=CallFixtures.Generator.instance.Scale" }, chained);
        Assert.Equal("VALUE \"some\"", Run("Blend.Maybe @Generator.Make")[0]);
    }

    [Fact]
    public void AReferenceOfTheWrongTypeIsRefusedLikeAnyArgument()
    {
        List<string> output = Run("Blend.Height 1 2 @Diagnostics.Label");
        Assert.Equal("bad_argument", ErrorCode(output));
        Assert.Contains("cannot pass @Diagnostics.Label (string) as Generator for parameter gen of CallFixtures.Blend.Height(float wx, float wy, Generator gen)", output[0]);
    }

    [Fact]
    public void DoubleAtIsALiteralAt()
    {
        Assert.Equal("VALUE \"@home\"", Run("Blend.Echo @@home")[0]);
        Assert.Equal("VALUE \"@home\"", Run("Blend.Echo \"@home\"")[0]);
        Assert.Equal("VALUE \"@@x\"", Run("Blend.Echo @@@x")[0]);
    }

    [Fact]
    public void AReferenceThatCannotBeReadNamesItsArgument()
    {
        List<string> unknown = Run("Blend.Height 1 2 @Nope.instance");
        Assert.Equal("no_type", ErrorCode(unknown));
        Assert.StartsWith("ERROR: code=no_type message=argument 3 (@Nope.instance): no loaded type named Nope", unknown[0]);

        List<string> ambiguous = Run("Blend.Echo @Probe.Shared");
        Assert.Equal("ambiguous_type", ErrorCode(ambiguous));
        Assert.Contains("argument 1 (@Probe.Shared): 2 loaded types named Probe", ambiguous[0]);
        Assert.Contains(ambiguous, l => l.StartsWith("  candidate: CallFixtures.Alpha.Probe.Shared", StringComparison.Ordinal));

        List<string> needsArguments = Run("Blend.Describe @Generator.Seeded");
        Assert.Equal("not_callable", ErrorCode(needsArguments));
        Assert.Contains("argument 1 (@Generator.Seeded): CallFixtures.Generator.Seeded takes arguments", needsArguments[0]);

        List<string> threw = Run("Blend.Describe @Generator.Exploding");
        Assert.Equal(new[]
        {
            "ERROR: code=call_threw message=argument 1 (@Generator.Exploding): CallFixtures.Generator.Exploding threw System.InvalidOperationException: no world loaded",
        }, threw);

        List<string> writeOnly = Run("Blend.Echo @Diagnostics.WriteOnly");
        Assert.Equal(new[]
        {
            "ERROR: code=not_callable message=argument 1 (@Diagnostics.WriteOnly): CallFixtures.Diagnostics.WriteOnly has no getter",
        }, writeOnly);

        Assert.Equal("bad_request", ErrorCode(Run("Blend.Echo @")));
    }
}
