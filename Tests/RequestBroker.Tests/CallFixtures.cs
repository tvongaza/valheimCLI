using System;
using System.Collections.Generic;

// Stand-ins for the game's and a mod's types, for StaticMemberCallTests and
// CallValuesTests. Block-scoped namespaces so one file can hold a type in the
// global namespace and same-named types in two namespaces.

/// <summary>A global-namespace type, as the game's own types are (ZNet, Utils).</summary>
public static class CallProbe
{
    public static int Answer = 42;
}

namespace CallFixtures.Alpha
{
    /// <summary>Same simple name as Beta.Probe: "Probe.Shared" is ambiguous.</summary>
    public static class Probe
    {
        public static string Shared() => "alpha";
        public static string OnlyAlpha() => "alpha only";
    }

    /// <summary>Same simple name as the global CallProbe: "CallProbe.Answer" means the global one.</summary>
    public static class CallProbe
    {
        public static int Answer = 7;
        public static int OnlyNamespaced = 1;
    }
}

namespace CallFixtures.Beta
{
    public static class Probe
    {
        public static string Shared() => "beta";
    }
}

namespace CallFixtures
{
    /// <summary>Vector-shaped like UnityEngine.Vector3: public fields x, y, z and nothing else public.</summary>
    public struct Vec3
    {
        public float x;
        public float y;
        public float z;

        public static Vec3 zero = new Vec3();

        public Vec3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public override string ToString() => "(" + x + ", " + y + ")";
    }

    /// <summary>Vector-shaped with integer axes, like the game's Vector2i.</summary>
    public struct Cell
    {
        public short x;
        public short y;
    }

    /// <summary>One axis is a number in a box, not a vector.</summary>
    public struct Scalar
    {
        public float x;
    }

    /// <summary>Four numeric fields that are not axes: not a vector.</summary>
    public struct Rgba
    {
        public byte r;
        public byte g;
        public byte b;
        public byte a;
    }

    public enum Biome
    {
        None = 0,
        Meadows = 1,
        Swamp = 2,
        Mountain = 4,
    }

    [Flags]
    public enum Layers
    {
        None = 0,
        Ground = 1,
        Water = 2,
        Air = 4,
    }

    /// <summary>A class without a ToString of its own: printed as its public members.</summary>
    public class Sample
    {
        public int Count = 3;
        public string Name = "north gate";
        public Vec3 Position = new Vec3(1.5f, 2f, -3f);
        public Sample? Next;
        public List<int> Values = new List<int> { 1, 2 };
        private int _hidden = 9;

        public float Ratio => 0.25f;
        public int SetOnly { set { } }
        public int PrivateGet { private get; set; }
        public int Broken => throw new InvalidOperationException("no ratio yet");
        public int this[int i] => i;
        public int Hidden() => _hidden;
    }

    public class Labelled
    {
        public override string ToString() => "labelled\nsecond line";
    }

    public class ThrowsOnToString
    {
        public override string ToString() => throw new NotSupportedException();
    }

    public class Wide
    {
        public int F0, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24, F25;
    }

    public static class Outer
    {
        public static class Nested
        {
            public static string Where() => "nested";
        }
    }

    public static class Generic<T>
    {
        public static int Size = 1;
    }

    public static class Diagnostics
    {
        public const int Version = 3;
        public static readonly string Label = "diagnostics";
        public static Biome Current = Biome.Swamp;
        private static int _calls;
        public static int Calls => _calls;
        public static string? Missing;
        public static int WriteOnly { set { _calls = value; } }
        public static int[] Empty = new int[0];

        public static void Reset() => _calls = 0;

        public static int Count() => ++_calls;

        private static string Secret() => "private reached";

        public static string Kind(int value) => "int";
        public static string Kind(long value) => "long";
        public static string Kind(double value) => "double";
        public static string Kind(string value) => "string";

        public static string Narrow(short value) => "short";
        public static string Narrow(byte value) => "byte";

        // Declared longest first, so declaration order cannot be what picks the shorter one.
        public static string Pick(int a, int b = 5) => "two b=" + b;
        public static string Pick(int a) => "one";

        public static string Optional(string name, int times = 2, string suffix = "!") => name + times + suffix;

        public static string Unset([System.Runtime.InteropServices.Optional] int count) => "count " + count;

        public static int PlusOne(in int value) => value + 1;

        public static string Scale(float factor) => "uniform";
        public static string Scale(float x, float y) => "per axis";

        public static float Distance(Vec3 a, Vec3 b)
        {
            float dx = b.x - a.x;
            float dz = b.z - a.z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        public static int Distance(Cell a, Cell b) => Math.Max(Math.Abs(a.x - b.x), Math.Abs(a.y - b.y));

        public static string Describe(Biome biome, bool loud) => loud ? biome.ToString().ToUpperInvariant() : biome.ToString();

        public static Layers Combine(Layers layers) => layers;

        public static bool TryFind(string text, out string found, out int at)
        {
            at = text.IndexOf('#');
            found = at >= 0 ? text.Substring(at + 1) : "";
            return at >= 0;
        }

        public static void Double(ref int value) => value *= 2;

        public static string Echo(string? text) => text == null ? "<null>" : "[" + text + "]";

        public static string Nullable(int? value) => value.HasValue ? "has " + value.Value : "none";

        public static string Letter(char c) => "char " + c;

        public static string Anything(object value) => value.GetType().Name + ":" + value;

        public static T Make<T>() where T : new() => new T();

        public static string Fail(string message) => throw new InvalidOperationException(message);

        public static List<int> Numbers(int count)
        {
            List<int> list = new List<int>();
            for (int i = 0; i < count; i++)
            {
                list.Add(i * 10);
            }
            return list;
        }

        public static IEnumerable<int> Endless()
        {
            int i = 0;
            while (true)
            {
                yield return i++;
            }
        }

        public static IEnumerable<string> Few()
        {
            yield return "a";
            yield return "b";
        }

        public static IEnumerable<int> BreaksWhileWalked()
        {
            yield return 1;
            throw new InvalidOperationException("collection was modified");
        }

        public static Dictionary<string, int> Table() => new Dictionary<string, int> { { "wood", 20 }, { "stone", 5 } };

        public static Sample MakeSample() => new Sample();

        public static Vec3 Origin() => new Vec3(0.1f, -2f, 1e6f);

        public static string Multiline() => "first\nsecond \"quoted\"";

        public static unsafe int Pointer(int* p) => *p;

        /// <summary>Reflection cannot box a ref struct, so invoking this fails before the method runs.</summary>
        public static Span<int> Window() => new Span<int>(new int[3]);
    }

    /// <summary>Few members, public and private, to check the listing under no_member.</summary>
    public static class Small
    {
        public static int A;
        private static int b = 2;
        public static int C => b;
        public static void D() { }
    }

    public static class BrokenInit
    {
        public static int Value = Explode();

        private static int Explode() => throw new InvalidOperationException("static setup failed");
    }

    public class Base
    {
        public static string Inherited() => "from base";
    }

    public class Derived : Base
    {
    }
}

namespace CallFixtures
{
    /// <summary>A singleton reached through a static field, like the game's WorldGenerator.instance.</summary>
    public class Generator
    {
        public static Generator? instance = new Generator(7);
        public static Generator? Missing;
        public static Generator Exploding => throw new InvalidOperationException("no world loaded");

        public static Generator Make() => new Generator(3);
        public static Generator Seeded(int seed) => new Generator(seed);

        public Generator(int seed)
        {
            Seed = seed;
        }

        public int Seed { get; }
        public float Scale = 2f;
        public Settings Config = new Settings();

        public Biome GetBiome(Vec3 point) => Biome.Swamp;
        public Biome GetBiome(Cell zone) => Biome.Mountain;
        public Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            wx > 0 ? Biome.Meadows : Biome.Swamp;

        public string Name() => "generator " + Seed;
        public virtual string Kind() => "base";
        private int Secret() => Seed * 10;
        public string Fail() => throw new InvalidOperationException("not ready");
    }

    public class DerivedGenerator : Generator
    {
        public static DerivedGenerator Special = new DerivedGenerator();

        public DerivedGenerator() : base(5) { }

        public override string Kind() => "derived";
    }

    public class Settings
    {
        public int Radius = 64;
    }

    /// <summary>A mod's static helper that takes the singleton as an argument.</summary>
    public static class Blend
    {
        public static float Height(float wx, float wy, Generator gen) => wx + wy + gen.Seed;

        public static Info Debug(float wx, float wy, Generator gen) =>
            new Info { Seed = gen.Seed, At = new Vec3(wx, 0f, wy), Biome = gen.GetBiome(wx, wy) };

        public struct Info
        {
            public int Seed;
            public Vec3 At;
            public Biome Biome;
        }

        public static string Describe(object value) => "object " + value;
        public static string Describe(Generator gen) => "generator " + gen.Seed;
        public static double Twice(double x) => x * 2;
        public static string Maybe(Generator? gen) => gen == null ? "none" : "some";
        public static string Echo(string text) => text;
    }
}

// "Holder.Slot.Item" reads two ways: the static field Item of Shade.Holder.Slot,
// or the instance field Item of the value of Other.Holder.Slot. The static
// reading must win.
namespace CallFixtures.Shade.Holder
{
    public static class Slot
    {
        public static string Item = "static";
    }
}

namespace CallFixtures.Other
{
    public static class Holder
    {
        public static SlotValue Slot = new SlotValue();
    }

    public class SlotValue
    {
        public string Item = "chain";
    }
}
