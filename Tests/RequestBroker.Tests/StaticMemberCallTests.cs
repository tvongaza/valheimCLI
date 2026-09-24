using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CallFixtures;
using valheimCLI;
using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// cli_call end to end, minus the console: a line in, the output lines out,
/// against the fixture types in this assembly (CallFixtures.cs).
/// </summary>
public class StaticMemberCallTests
{
    private static readonly StaticMemberCall.TypeIndex Index =
        new StaticMemberCall.TypeIndex(new[] { typeof(Diagnostics).Assembly });

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

    // --------------------------------------------------------------- tokens

    [Fact]
    public void TokensSplitAtWhitespaceAndQuotesKeepSpaces()
    {
        Assert.True(StaticMemberCall.TryTokenize("a   \"b c\" \"x\\\"y\" \"C:\\dir\\file\" \"\" \"back\\\\slash\"",
            out List<StaticMemberCall.Token> tokens, out string error), error);
        Assert.Equal(new[] { "a", "b c", "x\"y", "C:\\dir\\file", "", "back\\slash" }, tokens.Select(t => t.Text));
        Assert.Equal(new[] { false, true, true, true, true, true }, tokens.Select(t => t.Quoted));
    }

    [Fact]
    public void AnUnterminatedQuoteOrTextGluedToAQuoteIsRefused()
    {
        Assert.False(StaticMemberCall.TryTokenize("a \"open", out _, out string unterminated));
        Assert.Contains("unterminated quote starting at column 3", unterminated);
        Assert.False(StaticMemberCall.TryTokenize("\"a\"b", out _, out string glued));
        Assert.Contains("after the closing quote", glued);
    }

    [Fact]
    public void LimitIsAnOptionOnlyBeforeTheMember()
    {
        Assert.True(StaticMemberCall.TryParseRequest("--limit 3 Diagnostics.Numbers 9", out StaticMemberCall.Request? limited, out _));
        Assert.Equal(3, limited!.ItemLimit);
        Assert.Equal("Diagnostics.Numbers", limited.Path);
        Assert.Equal("9", Assert.Single(limited.Arguments).Text);

        Assert.True(StaticMemberCall.TryParseRequest("Diagnostics.Kind --limit -3", out StaticMemberCall.Request? plain, out _));
        Assert.Equal(StaticMemberCall.DefaultItemLimit, plain!.ItemLimit);
        Assert.Equal(new[] { "--limit", "-3" }, plain.Arguments.Select(a => a.Text));
    }

    [Theory]
    [InlineData("--limit", "--limit takes a whole number")]
    [InlineData("--limit many X.Y", "--limit takes a whole number")]
    [InlineData("--limit -1 X.Y", "--limit takes a whole number")]
    [InlineData("--limit 10001 X.Y", "--limit takes a whole number")]
    [InlineData("--verbose X.Y", "unknown option --verbose")]
    public void BadOptionsAreRefusedWithTheUsage(string line, string message)
    {
        List<string> output = Run(line);
        Assert.Equal("bad_request", ErrorCode(output));
        Assert.Contains(message, output[0]);
        Assert.Equal(StaticMemberCall.Usage, output[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--limit 5")]
    [InlineData("\"Diagnostics.Count\"")]
    public void NoMemberPrintsOnlyTheUsage(string line)
    {
        Assert.Equal(new[] { StaticMemberCall.Usage }, Run(line));
    }

    // ----------------------------------------------------------- resolution

    [Fact]
    public void AnExactFullNameWinsOverNamespacedTypesEndingWithIt()
    {
        List<string> output = Run("CallProbe.Answer");
        Assert.Equal(new[] { "VALUE 42", "OK: CALL CallProbe.Answer kind=field type=int" }, output);
    }

    [Fact]
    public void ASuffixMatchIsUsedWhenTheExactTypeLacksTheMember()
    {
        Assert.Equal("VALUE 1", Run("CallProbe.OnlyNamespaced")[0]);
        Assert.Equal("VALUE 7", Run("Alpha.CallProbe.Answer")[0]);
        Assert.Equal("VALUE 7", Run("CallFixtures.Alpha.CallProbe.Answer")[0]);
    }

    [Fact]
    public void TwoTypesWithTheMemberAreAmbiguousAndBothAreListed()
    {
        List<string> output = Run("Probe.Shared");
        Assert.Equal("ambiguous_type", ErrorCode(output));
        Assert.Contains("2 loaded types named Probe have a static Shared; give more of the namespace", output[0]);
        string assembly = typeof(Diagnostics).Assembly.GetName().Name!;
        Assert.Contains("  candidate: CallFixtures.Alpha.Probe.Shared (" + assembly + ")", output);
        Assert.Contains("  candidate: CallFixtures.Beta.Probe.Shared (" + assembly + ")", output);
        Assert.DoesNotContain(output, l => l.StartsWith("OK:", StringComparison.Ordinal));
    }

    [Fact]
    public void TheMemberNameNarrowsSameNamedTypes()
    {
        Assert.Equal("VALUE \"alpha only\"", Run("Probe.OnlyAlpha")[0]);
        Assert.Equal("VALUE \"beta\"", Run("Beta.Probe.Shared")[0]);
    }

    [Fact]
    public void NestedTypesAreNamedWithDots()
    {
        Assert.Equal("VALUE \"nested\"", Run("Outer.Nested.Where")[0]);
        Assert.Equal("VALUE \"nested\"", Run("CallFixtures.Outer.Nested.Where")[0]);
    }

    [Fact]
    public void AnUnknownTypeSuggestsTheRightCase()
    {
        List<string> output = Run("diagnostics.count");
        Assert.Equal("no_type", ErrorCode(output));
        Assert.Contains("no loaded type named diagnostics", output[0]);
        Assert.Contains("  did you mean CallFixtures.Diagnostics.Count?", output);
    }

    [Fact]
    public void ATypeGivenWithoutAMemberIsPointedOut()
    {
        List<string> bare = Run("Diagnostics");
        Assert.Equal("bad_request", ErrorCode(bare));
        Assert.Contains("  'Diagnostics' is a type; add the member: Diagnostics.<Member>", bare);

        List<string> namespaced = Run("CallFixtures.Diagnostics");
        Assert.Equal("no_type", ErrorCode(namespaced));
        Assert.Contains("  'CallFixtures.Diagnostics' is a type; add the member: CallFixtures.Diagnostics.<Member>", namespaced);

        Assert.Equal("bad_request", ErrorCode(Run("Diagnostics.")));
        Assert.Equal("bad_request", ErrorCode(Run(".Count")));
    }

    [Fact]
    public void AMissingMemberListsTheStaticMembersAndANearMiss()
    {
        List<string> output = Run("Diagnostics.count");
        Assert.Equal("no_member", ErrorCode(output));
        Assert.Contains("CallFixtures.Diagnostics has no static field, property or method named count", output[0]);
        Assert.Contains("  did you mean CallFixtures.Diagnostics.Count?", output);
        string members = Assert.Single(output, l => l.StartsWith("  static members: ", StringComparison.Ordinal));
        Assert.EndsWith("more)", members); // Diagnostics has more than MaxListed members

        // Fields, properties and methods, private ones too; no accessors, nothing from object.
        List<string> small = Run("Small.E");
        Assert.Equal("no_member", ErrorCode(small));
        Assert.Contains("  static members: A C D b", small);
    }

    [Fact]
    public void AMemberMissingFromEverySameNamedTypeListsTheTypes()
    {
        List<string> output = Run("Probe.Nothing");
        Assert.Equal("no_member", ErrorCode(output));
        Assert.Contains("none of the 2 loaded types named Probe has a static member Nothing", output[0]);
        Assert.Contains(output, l => l.StartsWith("  type: CallFixtures.Alpha.Probe (", StringComparison.Ordinal));
    }

    [Fact]
    public void OpenGenericTypesAreNotIndexed()
    {
        Assert.Equal("no_type", ErrorCode(Run("Generic.Size")));
        Assert.Empty(Index.Find("Generic`1"));
    }

    [Fact]
    public void InheritedStaticsAndPrivateMembersAreReachable()
    {
        Assert.Equal("VALUE \"from base\"", Run("Derived.Inherited")[0]);
        Assert.Equal("VALUE \"private reached\"", Run("Diagnostics.Secret")[0]);
    }

    [Fact]
    public void TheIndexIsRebuiltOnlyWhenTheAssemblyCountChanges()
    {
        StaticMemberCall.TypeIndexCache cache = new StaticMemberCall.TypeIndexCache();
        Assembly[] one = { typeof(Diagnostics).Assembly };
        StaticMemberCall.TypeIndex first = cache.For(one);
        Assert.Same(first, cache.For(one));
        Assert.Equal(1, cache.Builds);
        StaticMemberCall.TypeIndex second = cache.For(new[] { typeof(Diagnostics).Assembly, typeof(object).Assembly });
        Assert.NotSame(first, second);
        Assert.Equal(2, cache.Builds);
        Assert.Equal(2, second.AssemblyCount);
        Assert.NotEmpty(second.Find("System.Math"));
    }

    // ------------------------------------- assemblies and types that break

    /// <summary>An assembly whose type list is whatever the test says, including a throw.</summary>
    private sealed class FakeAssembly : Assembly
    {
        private readonly Func<Type[]> _types;
        private readonly string _name;

        public FakeAssembly(Func<Type[]> types, string name = "Fake")
        {
            _types = types;
            _name = name;
        }

        public override Type[] GetTypes() => _types();
        public override AssemblyName GetName() => new AssemblyName(_name);
    }

    /// <summary>A type that throws when inspected, as one with a missing dependency does.</summary>
    private sealed class UninspectableType : TypeDelegator
    {
        public UninspectableType() : base(typeof(Diagnostics)) { }

        public override bool ContainsGenericParameters => throw new TypeLoadException("missing dependency");
    }

    /// <summary>A type that is listed but throws when its members are asked for.</summary>
    private sealed class MemberlessType : TypeDelegator
    {
        public MemberlessType(Type shape) : base(shape) { }

        public override MemberInfo[] GetMember(string name, MemberTypes type, BindingFlags bindingAttr) =>
            throw new TypeLoadException("missing dependency");

        public override MemberInfo[] GetMembers(BindingFlags bindingAttr) => throw new TypeLoadException("missing dependency");
    }

    [Fact]
    public void AnAssemblyThatCannotLoadEveryTypeContributesTheOnesItCan()
    {
        FakeAssembly partial = new FakeAssembly(() => throw new ReflectionTypeLoadException(
            new Type?[] { typeof(Diagnostics), null }!, new Exception?[] { null, new TypeLoadException() }!));
        FakeAssembly broken = new FakeAssembly(() => throw new NotSupportedException("dynamic"));
        FakeAssembly odd = new FakeAssembly(() => new Type[] { new UninspectableType(), typeof(Outer.Nested) });
        StaticMemberCall.TypeIndex index = new StaticMemberCall.TypeIndex(new Assembly[] { partial, broken, odd });
        Assert.Equal(3, index.AssemblyCount);
        Assert.Equal(typeof(Diagnostics), Assert.Single(index.Find("Diagnostics")));
        Assert.Equal(typeof(Outer.Nested), Assert.Single(index.Find("Nested")));
    }

    /// <summary>Alpha.Probe's members under another full name, to make many same-named types.</summary>
    private sealed class RenamedType : TypeDelegator
    {
        private readonly string _namespace;

        public RenamedType(string ns) : base(typeof(CallFixtures.Alpha.Probe)) => _namespace = ns;

        public override string FullName => _namespace + ".Probe";
        public override string Namespace => _namespace;
    }

    [Fact]
    public void LongCandidateListsAreCutAndCounted()
    {
        int total = StaticMemberCall.MaxListed + 3;
        Type[] many = Enumerable.Range(0, total).Select(i => (Type)new RenamedType("Mod" + i)).ToArray();
        StaticMemberCall.TypeIndex index = new StaticMemberCall.TypeIndex(new Assembly[] { new FakeAssembly(() => many) });

        List<string> ambiguous = new List<string>();
        StaticMemberCall.Run(index, "Probe.Shared", ambiguous.Add);
        Assert.Equal("ambiguous_type", ErrorCode(ambiguous));
        Assert.Equal(StaticMemberCall.MaxListed, ambiguous.Count(l => l.StartsWith("  candidate: ", StringComparison.Ordinal)));
        Assert.Equal("  ... 3 more", ambiguous[^1]);

        List<string> missing = new List<string>();
        StaticMemberCall.Run(index, "Probe.Nothing", missing.Add);
        Assert.Equal("no_member", ErrorCode(missing));
        Assert.Equal(StaticMemberCall.MaxListed, missing.Count(l => l.StartsWith("  type: ", StringComparison.Ordinal)));
        Assert.Equal("  ... 3 more", missing[^1]);
    }

    // ------------------------------------- a mod reloaded in place

    /// <summary>A fixture type as defined again by a reloaded copy of its assembly.</summary>
    private sealed class CopiedType : TypeDelegator
    {
        private readonly Assembly _assembly;

        public CopiedType(Type shape, Assembly assembly) : base(shape) => _assembly = assembly;

        public override Assembly Assembly => _assembly;
    }

    /// <summary>
    /// Assemblies "Mod-1".."Mod-n" in load order, each defining its own copy
    /// of the given types, as a script engine's reloads leave them.
    /// </summary>
    private static FakeAssembly[] Reloads(int count, params Type[] shapes)
    {
        FakeAssembly[] assemblies = new FakeAssembly[count];
        for (int i = 0; i < count; i++)
        {
            int n = i;
            assemblies[n] = new FakeAssembly(() => shapes.Select(t => (Type)new CopiedType(t, assemblies[n])).ToArray(), "Mod-" + (n + 1));
        }
        return assemblies;
    }

    private static List<string> RunOn(IEnumerable<Assembly> assemblies, string line, params Assembly[] live)
    {
        List<string> output = new List<string>();
        StaticMemberCall.Run(new StaticMemberCall.TypeIndex(assemblies), line, output.Add, null, () => live);
        return output;
    }

    [Fact]
    public void AReloadedTypeIsNotAmbiguousTheLastLoadedCopyIsCalled()
    {
        List<string> output = RunOn(Reloads(2, typeof(Diagnostics)), "Diagnostics.Label");
        Assert.Equal(new[]
        {
            "VALUE \"diagnostics\"",
            "OK: CALL CallFixtures.Diagnostics.Label kind=field type=string assembly=Mod-2 stale_copies=1 chosen=newest",
        }, output);
    }

    [Fact]
    public void TheCopyWhoseAssemblyHoldsARunningPluginWinsOverNewerOnes()
    {
        FakeAssembly[] reloads = Reloads(3, typeof(Diagnostics));
        Assert.EndsWith(" assembly=Mod-1 stale_copies=2 chosen=live", RunOn(reloads, "Diagnostics.Label", reloads[0])[^1]);
        // Several live copies: the newest of those.
        Assert.EndsWith(" assembly=Mod-2 stale_copies=2 chosen=live", RunOn(reloads, "Diagnostics.Label", reloads[0], reloads[1])[^1]);
    }

    [Fact]
    public void RunningPluginsAreLookedUpOnlyWhenATypeHasCopies()
    {
        int asked = 0;
        List<string> output = new List<string>();
        StaticMemberCall.Run(Index, "Diagnostics.Label", output.Add, null, () => { asked++; return new List<Assembly>(); });
        Assert.Equal(0, asked);
        Assert.Equal("OK: CALL CallFixtures.Diagnostics.Label kind=field type=string", output[^1]);

        StaticMemberCall.Run(new StaticMemberCall.TypeIndex(Reloads(2, typeof(Diagnostics))), "Diagnostics.Label", output.Add, null,
            () => { asked++; return new List<Assembly>(); });
        Assert.Equal(1, asked);
    }

    [Fact]
    public void DifferentFullNamesStayAmbiguousAndCopiesAreCountedNotListed()
    {
        List<string> output = RunOn(Reloads(2, typeof(CallFixtures.Alpha.Probe), typeof(CallFixtures.Beta.Probe)), "Probe.Shared");
        Assert.Equal("ambiguous_type", ErrorCode(output));
        Assert.Contains("2 loaded types named Probe have a static Shared", output[0]);
        Assert.Equal(new[]
        {
            "  candidate: CallFixtures.Alpha.Probe.Shared (Mod-2) and 1 older copies",
            "  candidate: CallFixtures.Beta.Probe.Shared (Mod-2) and 1 older copies",
        }, output.Skip(1));
    }

    [Fact]
    public void AMissingMemberOnAReloadedTypeListsItsMembersOnce()
    {
        List<string> output = RunOn(Reloads(2, typeof(Small)), "Small.E");
        Assert.Equal("no_member", ErrorCode(output));
        Assert.Contains("CallFixtures.Small has no static field, property or method named E", output[0]);
        Assert.Contains("  static members: A C D b", output);
    }

    [Fact]
    public void AssemblyChoosesACopyOrATypeByTheStartOfItsAssemblyName()
    {
        FakeAssembly[] reloads = Reloads(2, typeof(Diagnostics));
        Assert.Equal("OK: CALL CallFixtures.Diagnostics.Label kind=field type=string",
            RunOn(reloads, "--assembly mod-1 Diagnostics.Label")[^1]);

        List<string> none = RunOn(reloads, "--assembly Other Diagnostics.Label");
        Assert.Equal("no_type", ErrorCode(none));
        Assert.Contains("no loaded type named Diagnostics in an assembly whose name starts with Other", none[0]);
        Assert.Equal(new[] { "  found in: Mod-1", "  found in: Mod-2" }, none.Skip(1));

        List<string> assembly = Run("--assembly");
        Assert.Equal("bad_request", ErrorCode(assembly));
        Assert.Contains("--assembly takes the start of an assembly name", assembly[0]);
    }

    [Fact]
    public void TheIndexKnowsTheLoadOrder()
    {
        FakeAssembly[] reloads = Reloads(2, typeof(Diagnostics));
        StaticMemberCall.TypeIndex index = new StaticMemberCall.TypeIndex(reloads);
        Assert.Equal(0, index.LoadOrder(reloads[0]));
        Assert.Equal(1, index.LoadOrder(reloads[1]));
        Assert.Equal(-1, index.LoadOrder(typeof(Diagnostics).Assembly));
    }

    [Fact]
    public void ASameNamedTypeThatCannotListItsMembersDoesNotBlockTheLookup()
    {
        FakeAssembly withBroken = new FakeAssembly(() => new Type[]
        {
            typeof(CallFixtures.Alpha.Probe), new MemberlessType(typeof(CallFixtures.Beta.Probe)),
        });
        StaticMemberCall.TypeIndex index = new StaticMemberCall.TypeIndex(new Assembly[] { withBroken });
        List<string> output = new List<string>();
        StaticMemberCall.Run(index, "Probe.Shared", output.Add);
        Assert.Equal("VALUE \"alpha\"", output[0]);

        FakeAssembly onlyBroken = new FakeAssembly(() => new Type[] { new MemberlessType(typeof(CallFixtures.Beta.Probe)) });
        output.Clear();
        StaticMemberCall.Run(new StaticMemberCall.TypeIndex(new Assembly[] { onlyBroken }), "Probe.Shared", output.Add);
        Assert.Equal("no_member", ErrorCode(output));
        Assert.Contains("  CallFixtures.Beta.Probe has no static fields, properties or methods", output);
    }

    // ------------------------------------------------------------ overloads

    [Theory]
    [InlineData("5", "int")]
    [InlineData("-5", "int")]
    [InlineData("5000000000", "long")]
    [InlineData("1.5", "double")]
    [InlineData("1e3", "double")]
    [InlineData("abc", "string")]
    [InlineData("\"5\"", "string")]
    [InlineData("null", "string")]
    public void TheMostNaturalReadingOfAnArgumentChoosesTheOverload(string argument, string chosen)
    {
        List<string> output = Run("Diagnostics.Kind " + argument);
        Assert.Equal("VALUE \"" + chosen + "\"", output[0]);
        Assert.StartsWith("OK: CALL CallFixtures.Diagnostics.Kind kind=method overload=(" + chosen + ") type=string", output[^1]);
    }

    [Fact]
    public void EquallyGoodOverloadsAreReportedNotGuessed()
    {
        List<string> output = Run("Diagnostics.Narrow 5");
        Assert.Equal("ambiguous_overload", ErrorCode(output));
        Assert.Contains("2 overloads of CallFixtures.Diagnostics.Narrow fit these arguments equally well", output[0]);
        Assert.Contains("  overload: CallFixtures.Diagnostics.Narrow(short value)", output);
        Assert.Contains("  overload: CallFixtures.Diagnostics.Narrow(byte value)", output);
    }

    [Fact]
    public void AnOverloadThatUsesFewerDefaultsWins()
    {
        Assert.Equal("VALUE \"one\"", Run("Diagnostics.Pick 1")[0]);
        Assert.Equal("VALUE \"two b=2\"", Run("Diagnostics.Pick 1 2")[0]);
    }

    [Fact]
    public void OptionalParametersMayBeLeftOff()
    {
        Assert.Equal("VALUE \"x2!\"", Run("Diagnostics.Optional x")[0]);
        Assert.Equal("VALUE \"x3!\"", Run("Diagnostics.Optional x 3")[0]);
        Assert.Equal("VALUE \"x3?\"", Run("Diagnostics.Optional x 3 ?")[0]);
    }

    [Fact]
    public void AnOptionalParameterWithoutADefaultGetsTheTypeDefault()
    {
        Assert.Equal("VALUE \"count 0\"", Run("Diagnostics.Unset")[0]);
        Assert.Equal("VALUE \"count 4\"", Run("Diagnostics.Unset 4")[0]);
    }

    [Fact]
    public void AnInParameterIsGivenAndNotPrintedBack()
    {
        Assert.Equal(new[] { "VALUE 42", "OK: CALL CallFixtures.Diagnostics.PlusOne kind=method type=int" }, Run("Diagnostics.PlusOne 41"));
        MethodInfo plusOne = typeof(Diagnostics).GetMethod(nameof(Diagnostics.PlusOne))!;
        Assert.Equal("CallFixtures.Diagnostics.PlusOne(in int value)", StaticMemberCall.Signature(plusOne));
    }

    [Fact]
    public void TheOnlyOverloadOfThatLengthNamesTheBadArgumentAndListsTheOthers()
    {
        List<string> output = Run("Diagnostics.Scale big");
        Assert.Equal("bad_argument", ErrorCode(output));
        Assert.Contains("cannot read 'big' as float for parameter factor of CallFixtures.Diagnostics.Scale(float factor)", output[0]);
        Assert.Contains("  overload: CallFixtures.Diagnostics.Scale(float x, float y)", output);
        Assert.Equal("VALUE \"per axis\"", Run("Diagnostics.Scale 1 2")[0]);
    }

    [Fact]
    public void AWrongArgumentCountListsTheOverloads()
    {
        List<string> output = Run("Diagnostics.Optional");
        Assert.Equal("no_overload", ErrorCode(output));
        Assert.Contains("no overload of CallFixtures.Diagnostics.Optional takes 0 argument(s)", output[0]);
        Assert.Contains("  overload: CallFixtures.Diagnostics.Optional(string name, int times = 2, string suffix = \"!\")", output);
    }

    [Fact]
    public void VectorArgumentsChooseTheOverloadByTheirLength()
    {
        List<string> three = Run("Diagnostics.Distance 0,0,0 3,100,4");
        Assert.Equal(new[] { "VALUE 5", "OK: CALL CallFixtures.Diagnostics.Distance kind=method overload=(Vec3,Vec3) type=float" }, three);
        List<string> two = Run("Diagnostics.Distance 0,0 3,-7");
        Assert.Equal(new[] { "VALUE 7", "OK: CALL CallFixtures.Diagnostics.Distance kind=method overload=(Cell,Cell) type=int" }, two);
    }

    [Fact]
    public void ASingleFittingOverloadNamesTheArgumentItCannotRead()
    {
        List<string> output = Run("Diagnostics.Describe Lava true");
        Assert.Equal("bad_argument", ErrorCode(output));
        Assert.Contains("cannot read 'Lava' as Biome for parameter biome of CallFixtures.Diagnostics.Describe(Biome biome, bool loud)", output[0]);
    }

    [Fact]
    public void ArgumentsThatFitNoOverloadListEachFailure()
    {
        List<string> output = Run("Diagnostics.Distance 1,2 3,4,5");
        Assert.Equal("no_overload", ErrorCode(output));
        Assert.Contains("no overload of CallFixtures.Diagnostics.Distance accepts these arguments", output[0]);
        Assert.Contains(output, l => l.Contains("cannot read '1,2' as Vec3"));
        Assert.Contains(output, l => l.Contains("cannot read '3,4,5' as Cell"));
    }

    [Fact]
    public void EnumsReadByNameInAnyCaseOrByNumber()
    {
        Assert.Equal("VALUE \"SWAMP\"", Run("Diagnostics.Describe swamp true")[0]);
        Assert.Equal("VALUE \"Swamp\"", Run("Diagnostics.Describe 2 False")[0]);
        Assert.Equal("VALUE Ground, Water", Run("Diagnostics.Combine Ground,water")[0]);
    }

    [Fact]
    public void OutAndRefParametersArePrintedAfterTheCall()
    {
        Assert.Equal(new[]
        {
            "VALUE true",
            "OUT found=\"b c\"",
            "OUT at=1",
            "OK: CALL CallFixtures.Diagnostics.TryFind kind=method type=bool",
        }, Run("Diagnostics.TryFind \"a#b c\""));
        Assert.Equal(new[]
        {
            "OUT value=42",
            "OK: CALL CallFixtures.Diagnostics.Double kind=method type=void",
        }, Run("Diagnostics.Double 21"));
    }

    [Fact]
    public void NullIsUnquotedAndAQuotedNullIsText()
    {
        Assert.Equal("VALUE \"<null>\"", Run("Diagnostics.Echo null")[0]);
        Assert.Equal("VALUE \"[null]\"", Run("Diagnostics.Echo \"null\"")[0]);
        Assert.Equal("VALUE \"[]\"", Run("Diagnostics.Echo \"\"")[0]);
        Assert.Equal("VALUE \"none\"", Run("Diagnostics.Nullable null")[0]);
        Assert.Equal("VALUE \"has 4\"", Run("Diagnostics.Nullable 4")[0]);
        Assert.Equal("VALUE \"char a\"", Run("Diagnostics.Letter a")[0]);
        Assert.Equal("VALUE \"String:5\"", Run("Diagnostics.Anything 5")[0]);
    }

    [Fact]
    public void GenericAndPointerMethodsAreNotCallable()
    {
        List<string> generic = Run("Diagnostics.Make");
        Assert.Equal("not_callable", ErrorCode(generic));
        Assert.Contains("  overload: CallFixtures.Diagnostics.Make()", generic);
        Assert.Equal("not_callable", ErrorCode(Run("Diagnostics.Pointer 1")));
    }

    // ------------------------------------------------- fields and properties

    [Fact]
    public void FieldsConstantsAndPropertiesAreRead()
    {
        Assert.Equal(new[] { "VALUE 3", "OK: CALL CallFixtures.Diagnostics.Version kind=constant type=int" }, Run("Diagnostics.Version"));
        Assert.Equal(new[] { "VALUE \"diagnostics\"", "OK: CALL CallFixtures.Diagnostics.Label kind=field type=string" }, Run("Diagnostics.Label"));
        Assert.Equal(new[] { "VALUE Swamp", "OK: CALL CallFixtures.Diagnostics.Current kind=field type=Biome" }, Run("Diagnostics.Current"));
        Assert.Equal(new[] { "VALUE null", "OK: CALL CallFixtures.Diagnostics.Missing kind=field type=string" }, Run("Diagnostics.Missing"));
        Assert.Equal("OK: CALL CallFixtures.Diagnostics.Calls kind=property type=int", Run("Diagnostics.Calls")[1]);
    }

    [Fact]
    public void AFieldOrPropertyTakesNoArgumentsAndAWriteOnlyPropertyCannotBeRead()
    {
        List<string> field = Run("Diagnostics.Label x");
        Assert.Equal("bad_argument", ErrorCode(field));
        Assert.Contains("CallFixtures.Diagnostics.Label is a field; it takes no arguments", field[0]);
        Assert.Equal("bad_argument", ErrorCode(Run("Diagnostics.Calls 1")));
        List<string> writeOnly = Run("Diagnostics.WriteOnly");
        Assert.Equal("not_callable", ErrorCode(writeOnly));
        Assert.Contains("has no getter", writeOnly[0]);
    }

    [Fact]
    public void EachCallRunsTheMethodOnce()
    {
        Run("Diagnostics.Reset");
        Assert.Equal(new[] { "OK: CALL CallFixtures.Diagnostics.Reset kind=method type=void" }, Run("Diagnostics.Reset"));
        Assert.Equal("VALUE 1", Run("Diagnostics.Count")[0]);
        Assert.Equal("VALUE 2", Run("Diagnostics.Count")[0]);
        Assert.Equal("VALUE 2", Run("Diagnostics.Calls")[0]);
    }

    // ------------------------------------------------------------ sequences

    [Fact]
    public void ACollectionPrintsOneLinePerItemAndItsCount()
    {
        Assert.Equal(new[]
        {
            "ITEM 0 0",
            "ITEM 1 10",
            "ITEM 2 20",
            "OK: CALL CallFixtures.Diagnostics.Numbers kind=method type=List<int> items=3 shown=3",
        }, Run("Diagnostics.Numbers 3"));
    }

    [Fact]
    public void TheLimitSaysHowManyItemsItLeftOut()
    {
        Assert.Equal(new[]
        {
            "ITEM 0 0",
            "ITEM 1 10",
            "MORE 3 of 5 items not shown; raise --limit (now 2)",
            "OK: CALL CallFixtures.Diagnostics.Numbers kind=method type=List<int> items=5 shown=2",
        }, Run("--limit 2 Diagnostics.Numbers 5"));
        Assert.Equal(new[]
        {
            "MORE 5 of 5 items not shown; raise --limit (now 0)",
            "OK: CALL CallFixtures.Diagnostics.Numbers kind=method type=List<int> items=5 shown=0",
        }, Run("--limit 0 Diagnostics.Numbers 5"));
    }

    [Fact]
    public void AnEndlessSequenceStopsAtTheLimit()
    {
        List<string> output = Run("--limit 3 Diagnostics.Endless");
        Assert.Equal(new[] { "ITEM 0 0", "ITEM 1 1", "ITEM 2 2" }, output.Take(3));
        Assert.StartsWith("MORE items not shown (a sequence, not a collection: its length is unknown)", output[3]);
        Assert.EndsWith("items=? shown=3", output[4]);
        Assert.Equal(StaticMemberCall.DefaultItemLimit + 2, Run("Diagnostics.Endless").Count);
    }

    [Fact]
    public void ASequenceThatEndsAtTheLimitHasNoMoreLine()
    {
        Assert.Equal(new[]
        {
            "ITEM 0 \"a\"",
            "ITEM 1 \"b\"",
            "OK: CALL CallFixtures.Diagnostics.Few kind=method type=IEnumerable<string> items=2 shown=2",
        }, Run("--limit 2 Diagnostics.Few"));
    }

    [Fact]
    public void AnEmptyArrayAndADictionary()
    {
        Assert.Equal(new[] { "OK: CALL CallFixtures.Diagnostics.Empty kind=field type=int[] items=0 shown=0" }, Run("Diagnostics.Empty"));
        Assert.Equal(new[] { "ITEM 0 \"wood\" => 20", "ITEM 1 \"stone\" => 5" }, Run("Diagnostics.Table").Take(2));
    }

    // -------------------------------------------------------------- results

    [Fact]
    public void AnObjectWithoutItsOwnTextListsItsPublicMembersOneLevelDeep()
    {
        Assert.Equal(
            "VALUE Sample Count=3 Name=\"north gate\" Position=1.5,2,-3 Next=null Values=List<int>(count=2) Ratio=0.25 Broken=<threw InvalidOperationException>",
            Run("Diagnostics.MakeSample")[0]);
    }

    [Fact]
    public void VectorsAndStringsPrintAsTheyAreTyped()
    {
        Assert.Equal("VALUE 0.1,-2,1000000", Run("Diagnostics.Origin")[0]);
        Assert.Equal("VALUE \"first\\nsecond \\\"quoted\\\"\"", Run("Diagnostics.Multiline")[0]);
    }

    // ----------------------------------------------------------- exceptions

    [Fact]
    public void ATargetExceptionIsUnwrappedAndHandedToTheLog()
    {
        List<string> output = new List<string>();
        List<(string, Exception)> logged = new List<(string, Exception)>();
        StaticMemberCall.Run(Index, "Diagnostics.Fail \"no world\nloaded\"", output.Add, (target, ex) => logged.Add((target, ex)));
        Assert.Equal(new[]
        {
            "ERROR: code=call_threw message=CallFixtures.Diagnostics.Fail threw System.InvalidOperationException: no world loaded",
        }, output);
        (string target, Exception exception) = Assert.Single(logged);
        Assert.Equal("CallFixtures.Diagnostics.Fail", target);
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void AReflectionFailureIsReportedApartFromTheTargetThrowing()
    {
        List<string> output = new List<string>();
        List<string> logged = new List<string>();
        StaticMemberCall.Run(Index, "Diagnostics.Window", output.Add, (target, _) => logged.Add(target));
        Assert.Equal("call_failed", ErrorCode(output));
        Assert.StartsWith("ERROR: code=call_failed message=could not call CallFixtures.Diagnostics.Window: System.NotSupportedException", output[0]);
        Assert.Equal(new[] { "CallFixtures.Diagnostics.Window" }, logged);
    }

    [Fact]
    public void AFailedStaticConstructorNamesItsCause()
    {
        List<string> output = Run("BrokenInit.Value");
        Assert.Equal("call_threw", ErrorCode(output));
        Assert.Contains("threw System.TypeInitializationException", output[0]);
        Assert.EndsWith("(System.InvalidOperationException: static setup failed)", output[0]);
    }

    [Fact]
    public void AnExceptionWhileWalkingTheResultPrintsNoPartialItems()
    {
        List<string> output = Run("Diagnostics.BreaksWhileWalked");
        Assert.Equal(new[]
        {
            "ERROR: code=call_threw message=CallFixtures.Diagnostics.BreaksWhileWalked (walking the result) threw System.InvalidOperationException: collection was modified",
        }, output);
    }
}
