using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using CallFixtures;
using valheimCLI;
using Xunit;

namespace valheimCLI.Tests;

/// <summary>
/// cli_call's text-to-value and value-to-text conversions (CallValues).
/// Vectors use the Vec3/Cell stand-ins, which have the shape of
/// UnityEngine.Vector3 and the game's Vector2s.
/// </summary>
public class CallValuesTests
{
    private static object? Convert(string text, Type target, bool quoted = false)
    {
        Assert.True(CallValues.TryConvert(text, quoted, target, out object? value, out int _), text + " as " + target.Name);
        return value;
    }

    private static int CostOf(string text, Type target, bool quoted = false)
    {
        Assert.True(CallValues.TryConvert(text, quoted, target, out object? _, out int cost), text + " as " + target.Name);
        return cost;
    }

    private static bool Fails(string text, Type target, bool quoted = false) =>
        !CallValues.TryConvert(text, quoted, target, out object? _, out int _);

    // ------------------------------------------------------------- reading

    [Fact]
    public void NumbersAndBooleansReadInvariantly()
    {
        CultureInfo previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE"); // decimal comma
            Assert.Equal(1.5f, Convert("1.5", typeof(float)));
            Assert.Equal(-2.25, Convert("-2.25", typeof(double)));
            Assert.Equal(1.5m, Convert("1.5", typeof(decimal)));
            Assert.True(Fails("1,5", typeof(float)));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
        Assert.Equal(42, Convert("42", typeof(int)));
        Assert.Equal(5000000000L, Convert("5000000000", typeof(long)));
        Assert.Equal((byte)255, Convert("255", typeof(byte)));
        Assert.Equal((ulong)7, Convert("7", typeof(ulong)));
        Assert.Equal(true, Convert("True", typeof(bool)));
        Assert.Equal(false, Convert("false", typeof(bool)));
        Assert.Equal('x', Convert("x", typeof(char)));
        Assert.Equal(double.NaN, Convert("NaN", typeof(double)));
    }

    [Theory]
    [InlineData("300", typeof(byte))]
    [InlineData("-1", typeof(uint))]
    [InlineData("5000000000", typeof(int))]
    [InlineData("1.5", typeof(int))]
    [InlineData("yes", typeof(bool))]
    [InlineData("ab", typeof(char))]
    [InlineData("abc", typeof(double))]
    [InlineData("Lava", typeof(Biome))]
    [InlineData("Swamp,Lava", typeof(Biome))]
    [InlineData("1,2", typeof(Vec3))]
    [InlineData("1,2,3,4", typeof(Vec3))]
    [InlineData("1.5,2", typeof(Cell))]
    [InlineData("1,2,3,4", typeof(Rgba))]
    [InlineData("null", typeof(int))]
    [InlineData("abc", typeof(int?))]
    [InlineData("abc", typeof(float))]
    [InlineData("abc", typeof(decimal))]
    [InlineData("99999999999999999999", typeof(Biome))]
    [InlineData("", typeof(Biome))]
    [InlineData("something", typeof(Sample))]
    public void TextThatDoesNotFitTheTypeIsRefused(string text, Type target)
    {
        Assert.True(Fails(text, target));
    }

    [Fact]
    public void EnumsReadByNameFlagsOrNumber()
    {
        Assert.Equal(Biome.Swamp, Convert("swamp", typeof(Biome)));
        Assert.Equal(Biome.Mountain, Convert("4", typeof(Biome)));
        Assert.Equal(Layers.Ground | Layers.Air, Convert("Ground, air", typeof(Layers)));
        Assert.Equal(CallValues.Cost.Exact, CostOf("Swamp", typeof(Biome)));
        Assert.Equal(CallValues.Cost.NumberAsEnum, CostOf("2", typeof(Biome)));
    }

    [Fact]
    public void VectorsReadAsCommaSeparatedAxes()
    {
        Vec3 v = (Vec3)Convert("1.5,-2,3", typeof(Vec3))!;
        Assert.Equal((1.5f, -2f, 3f), (v.x, v.y, v.z));
        Vec3 spaced = (Vec3)Convert("1, 2, 3", typeof(Vec3), quoted: true)!;
        Assert.Equal(3f, spaced.z);
        Cell c = (Cell)Convert("4,-7", typeof(Cell))!;
        Assert.Equal((4, -7), (c.x, c.y));
        Vec3 byRef = (Vec3)Convert("0,0,1", typeof(Vec3).MakeByRefType())!;
        Assert.Equal(1f, byRef.z);
    }

    [Fact]
    public void NullIsOnlyUnquotedAndOnlyForTypesThatHoldIt()
    {
        Assert.Null(Convert("null", typeof(string)));
        Assert.Null(Convert("null", typeof(Sample)));
        Assert.Null(Convert("null", typeof(int?)));
        Assert.Equal("null", Convert("null", typeof(string), quoted: true));
        Assert.Equal(3, Convert("3", typeof(int?)));
        Assert.Equal(CallValues.Cost.Exact + CallValues.Cost.NullableWrap, CostOf("3", typeof(int?)));
    }

    [Fact]
    public void CostsOrderTheReadingsOfOneToken()
    {
        // "5": int, then long, then a narrower or unsigned integer, then a real, an enum, text.
        Assert.True(CostOf("5", typeof(int)) < CostOf("5", typeof(long)));
        Assert.True(CostOf("5", typeof(long)) < CostOf("5", typeof(short)));
        Assert.True(CostOf("5", typeof(short)) < CostOf("5", typeof(double)));
        Assert.Equal(CostOf("5", typeof(double)), CostOf("5", typeof(float)));
        Assert.True(CostOf("5", typeof(double)) < CostOf("5", typeof(Biome)));
        Assert.True(CostOf("5", typeof(Biome)) < CostOf("5", typeof(string)));
        Assert.True(CostOf("5", typeof(string)) < CostOf("5", typeof(object)));
        // "1.5": double, then float, then decimal.
        Assert.True(CostOf("1.5", typeof(double)) < CostOf("1.5", typeof(float)));
        Assert.True(CostOf("1.5", typeof(float)) < CostOf("1.5", typeof(decimal)));
        // A quoted token is text first.
        Assert.Equal(CallValues.Cost.Exact, CostOf("5", typeof(string), quoted: true));
        Assert.True(CostOf("5", typeof(object), quoted: true) < CostOf("5", typeof(int), quoted: true));
        Assert.Equal(CallValues.Cost.QuotedNonText, CostOf("5", typeof(int), quoted: true));
        Assert.True(CostOf("a", typeof(char)) < CostOf("a", typeof(string)));
    }

    [Fact]
    public void TextReachesAnObjectParameterAsAString()
    {
        Assert.Equal("12", Convert("12", typeof(object)));
    }

    [Fact]
    public void AnObjectInHandPassesToItsTypeABaseTypeOrAWiderNumber()
    {
        Generator gen = new Generator(1);
        Assert.True(CallValues.TryConvertValue(gen, typeof(Generator), out object? same, out int exact));
        Assert.Same(gen, same);
        Assert.Equal(CallValues.Cost.Exact, exact);
        Assert.True(CallValues.TryConvertValue(new DerivedGenerator(), typeof(Generator), out object? _, out int derived));
        Assert.Equal(CallValues.Cost.Near, derived);
        Assert.True(CallValues.TryConvertValue(gen, typeof(object), out object? _, out int boxed));
        Assert.Equal(CallValues.Cost.Widened, boxed);
        Assert.True(CallValues.TryConvertValue(3, typeof(double), out object? wider, out int _));
        Assert.Equal(3.0, wider);
        Assert.True(CallValues.TryConvertValue(3, typeof(int?), out object? wrapped, out int nullableCost));
        Assert.Equal(3, wrapped);
        Assert.Equal(CallValues.Cost.Exact + CallValues.Cost.NullableWrap, nullableCost);
        Assert.True(CallValues.TryConvertValue(null, typeof(Generator), out object? none, out int _));
        Assert.Null(none);
        Assert.True(CallValues.TryConvertValue(gen, typeof(Generator).MakeByRefType(), out object? _, out int _));

        Assert.False(CallValues.TryConvertValue(null, typeof(int), out object? _, out int _));
        Assert.False(CallValues.TryConvertValue(300L, typeof(byte), out object? _, out int _));
        Assert.False(CallValues.TryConvertValue("text", typeof(Generator), out object? _, out int _));
        Assert.False(CallValues.TryConvertValue(gen, typeof(int?), out object? _, out int _));
        Assert.False(CallValues.TryConvertValue(true, typeof(int), out object? _, out int _));
    }

    // ------------------------------------------------------------- writing

    [Fact]
    public void PrimitivesPrintInvariantlyAndRoundTrip()
    {
        CultureInfo previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("0.1", CallValues.Format(0.1f));
            Assert.Equal("1.5", CallValues.Format(1.5));
            Assert.Equal("2.75", CallValues.Format(2.75m));
            Assert.Equal("-3", CallValues.Format(-3));
            Assert.Equal("123456789012", CallValues.Format(123456789012L));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
        Assert.Equal("null", CallValues.Format(null));
        Assert.Equal("true", CallValues.Format(true));
        Assert.Equal("\"x\"", CallValues.Format('x'));
        Assert.Equal("Swamp", CallValues.Format(Biome.Swamp));
        Assert.Equal("Ground, Water", CallValues.Format(Layers.Ground | Layers.Water));
    }

    [Fact]
    public void StringsAreQuotedAndEscapedOntoOneLine()
    {
        Assert.Equal("\"\"", CallValues.Format(""));
        Assert.Equal("\"null\"", CallValues.Format("null"));
        Assert.Equal("\"a \\\"b\\\" c:\\\\d\"", CallValues.Format("a \"b\" c:\\d"));
        Assert.Equal("\"one\\ntwo\\r\\tthree\\u0001\"", CallValues.Format("one\ntwo\r\tthree\u0001"));
    }

    [Fact]
    public void VectorsPrintAsTheyAreTypedEvenWithTheirOwnToString()
    {
        Assert.Equal("1.5,-2,3", CallValues.Format(new Vec3(1.5f, -2f, 3f)));
        Assert.Equal("4,-7", CallValues.Format(new Cell { x = 4, y = -7 }));
        Assert.Equal("Rgba r=1 g=2 b=3 a=4", CallValues.Format(new Rgba { r = 1, g = 2, b = 3, a = 4 }));
    }

    [Fact]
    public void AnOwnToStringIsUsedWithNewlinesEscaped()
    {
        Assert.Equal("labelled\\nsecond line", CallValues.Format(new Labelled()));
        Assert.Equal("<ToString threw NotSupportedException>", CallValues.Format(new ThrowsOnToString()));
        Assert.Equal("00:01:30", CallValues.Format(TimeSpan.FromSeconds(90)));
    }

    [Fact]
    public void ANestedObjectPrintsOnlyItsTypeAndACollectionItsCount()
    {
        Assert.Equal("Sample", CallValues.Format(new Sample(), expand: false));
        Assert.Equal("List<int>(count=2)", CallValues.Format(new List<int> { 1, 2 }, expand: false));
        Assert.Equal("List<int>(count=2)", CallValues.Format(new List<int> { 1, 2 }));
        Assert.Equal("sequence", CallValues.Format(Diagnostics.Few(), expand: false));
        Assert.Equal("IEnumerable<int>", CallValues.FriendlyName(typeof(IEnumerable<int>)));
    }

    [Fact]
    public void AWideObjectListsItsFirstMembersAndCountsTheRest()
    {
        string text = CallValues.Format(new Wide());
        Assert.StartsWith("Wide F0=0 F1=0 ", text);
        Assert.EndsWith(" F23=0 ...2 more", text);
    }

    [Fact]
    public void PairsPrintAsKeyAndValue()
    {
        Assert.Equal("\"wood\" => 20", CallValues.Format(new KeyValuePair<string, int>("wood", 20)));
        Assert.Equal("1 => null", CallValues.Format(new DictionaryEntry(1, null)));
        Assert.Equal("\"k\" => Sample", CallValues.Format(new KeyValuePair<string, Sample>("k", new Sample())));
    }

    [Fact]
    public void OnlySequencesWithoutTheirOwnTextAreItemized()
    {
        Assert.True(CallValues.IsItemized(new List<int>()));
        Assert.True(CallValues.IsItemized(new int[0]));
        Assert.True(CallValues.IsItemized(new Dictionary<string, int>()));
        Assert.False(CallValues.IsItemized("text"));
        Assert.False(CallValues.IsItemized(null));
        Assert.False(CallValues.IsItemized(new Sample()));
        Assert.False(CallValues.IsItemized(new LabelledSequence()));
    }

    private sealed class LabelledSequence : IEnumerable
    {
        public IEnumerator GetEnumerator() => new int[] { 1 }.GetEnumerator();
        public override string ToString() => "labelled sequence";
    }

    [Fact]
    public void VectorShapeIsExactlyTheLeadingAxes()
    {
        Assert.Equal(new[] { "x", "y", "z" }, Array.ConvertAll(CallValues.VectorAxes(typeof(Vec3))!, f => f.Name));
        Assert.Equal(2, CallValues.VectorAxes(typeof(Cell))!.Length);
        Assert.Null(CallValues.VectorAxes(typeof(Rgba)));
        Assert.Null(CallValues.VectorAxes(typeof(Scalar)));
        Assert.Null(CallValues.VectorAxes(typeof(int)));
        Assert.Null(CallValues.VectorAxes(typeof(Biome)));
        Assert.Null(CallValues.VectorAxes(typeof(Sample)));
        Assert.Null(CallValues.VectorAxes(typeof(ValueTuple<int, int>)));
    }

    [Theory]
    [InlineData(typeof(int), "int")]
    [InlineData(typeof(void), "void")]
    [InlineData(typeof(int?), "int?")]
    [InlineData(typeof(string[]), "string[]")]
    [InlineData(typeof(int[,]), "int[,]")]
    [InlineData(typeof(List<int>), "List<int>")]
    [InlineData(typeof(Dictionary<string, List<float>>), "Dictionary<string, List<float>>")]
    [InlineData(typeof(Sample), "Sample")]
    public void TypeNamesReadAsCSharp(Type type, string name)
    {
        Assert.Equal(name, CallValues.FriendlyName(type));
        Assert.Equal(name, CallValues.FriendlyName(type == typeof(void) ? type : type.MakeByRefType()));
    }

    [Fact]
    public void ASignatureShowsModifiersAndDefaults()
    {
        MethodInfo find = typeof(Diagnostics).GetMethod(nameof(Diagnostics.TryFind))!;
        Assert.Equal("CallFixtures.Diagnostics.TryFind(string text, out string found, out int at)", StaticMemberCall.Signature(find));
        MethodInfo twice = typeof(Diagnostics).GetMethod(nameof(Diagnostics.Double))!;
        Assert.Equal("CallFixtures.Diagnostics.Double(ref int value)", StaticMemberCall.Signature(twice));
    }
}
