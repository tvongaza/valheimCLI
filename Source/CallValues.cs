using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace valheimCLI
{
    /// <summary>
    /// cli_call's two conversions between console text and .NET values:
    /// an argument token into a parameter's type, and a returned value into
    /// one line of text. Plain .NET, no Unity: vectors are recognised by
    /// shape (a struct whose only public fields are x, y[, z[, w]]) rather
    /// than by type, so UnityEngine.Vector2/3/4, Quaternion and the game's
    /// Vector2i/Vector2s all read "x,y,z" and print the same way, and the
    /// tests can use a stand-in struct.
    ///
    /// Numbers are read and written with the invariant culture: a decimal
    /// point, never a comma, whatever the machine's locale.
    /// </summary>
    public static class CallValues
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly string[] Axes = { "x", "y", "z", "w" };

        /// <summary>Members listed for an object without a ToString of its own; the rest are counted.</summary>
        public const int MaxMembers = 24;

        /// <summary>
        /// Relative cost of each conversion, so that overload selection can
        /// prefer the most natural reading of a token: "5" is an int before
        /// it is a long, a double or a string; "1.5" is a double before a
        /// float; a quoted token is a string before anything else. Lower is
        /// better. Only the ordering matters.
        /// </summary>
        public static class Cost
        {
            public const int Exact = 0;
            public const int Near = 1;
            public const int Widened = 2;
            public const int IntegerAsReal = 3;
            public const int NumberAsEnum = 4;
            public const int TextAsString = 5;
            public const int TextAsObject = 6;
            public const int NullableWrap = 1;
            /// <summary>Added when a quoted token is read as anything but text.</summary>
            public const int QuotedNonText = 10;
        }

        // ------------------------------------------------------------------
        // Text -> value
        // ------------------------------------------------------------------

        /// <summary>
        /// Read <paramref name="text"/> as <paramref name="target"/>. Returns
        /// false when it cannot be read that way. A ByRef type is read as its
        /// element type. An unquoted <c>null</c> is null for a reference or
        /// nullable type; a quoted token is never null.
        /// </summary>
        public static bool TryConvert(string text, bool quoted, Type target, out object? value, out int cost)
        {
            value = null;
            cost = 0;
            if (target.IsByRef)
            {
                target = target.GetElementType()!;
            }

            if (!quoted && text == "null")
            {
                return !target.IsValueType || Nullable.GetUnderlyingType(target) != null;
            }

            Type? underlying = Nullable.GetUnderlyingType(target);
            if (underlying != null)
            {
                if (!TryConvert(text, quoted, underlying, out value, out cost))
                {
                    return false;
                }
                cost += Cost.NullableWrap;
                return true;
            }

            if (target == typeof(string))
            {
                value = text;
                cost = quoted ? Cost.Exact : Cost.TextAsString;
                return true;
            }
            if (target == typeof(object))
            {
                value = text;
                cost = quoted ? Cost.Near : Cost.TextAsObject;
                return true;
            }
            if (target == typeof(char))
            {
                if (text.Length != 1)
                {
                    return false;
                }
                value = text[0];
                cost = Cost.Near;
                return true;
            }

            if (!TryConvertNonText(text, target, out value, out cost))
            {
                return false;
            }
            if (quoted)
            {
                cost += Cost.QuotedNonText;
            }
            return true;
        }

        private static bool TryConvertNonText(string text, Type target, out object? value, out int cost)
        {
            value = null;
            cost = 0;
            if (target == typeof(bool))
            {
                if (!bool.TryParse(text, out bool b))
                {
                    return false;
                }
                value = b;
                return true;
            }
            if (target.IsEnum)
            {
                return TryConvertEnum(text, target, out value, out cost);
            }
            if (IsIntegral(target))
            {
                if (!TryIntegral(text, target, out value))
                {
                    return false;
                }
                cost = target == typeof(int) ? Cost.Exact : target == typeof(long) ? Cost.Near : Cost.Widened;
                return true;
            }
            if (target == typeof(double) || target == typeof(float) || target == typeof(decimal))
            {
                bool integerText = IsIntegerText(text);
                if (target == typeof(double))
                {
                    if (!double.TryParse(text, NumberStyles.Float, Inv, out double d))
                    {
                        return false;
                    }
                    value = d;
                    cost = integerText ? Cost.IntegerAsReal : Cost.Exact;
                    return true;
                }
                if (target == typeof(float))
                {
                    if (!float.TryParse(text, NumberStyles.Float, Inv, out float f))
                    {
                        return false;
                    }
                    value = f;
                    cost = integerText ? Cost.IntegerAsReal : Cost.Near;
                    return true;
                }
                if (!decimal.TryParse(text, NumberStyles.Float, Inv, out decimal m))
                {
                    return false;
                }
                value = m;
                cost = integerText ? Cost.IntegerAsReal : Cost.Widened;
                return true;
            }
            FieldInfo[]? axes = VectorAxes(target);
            if (axes != null)
            {
                return TryConvertVector(text, target, axes, out value, out cost);
            }
            return false;
        }

        private static bool TryConvertEnum(string text, Type target, out object? value, out int cost)
        {
            value = null;
            cost = 0;
            if (IsIntegerText(text))
            {
                if (!long.TryParse(text, NumberStyles.Integer, Inv, out long number))
                {
                    return false;
                }
                value = Enum.ToObject(target, number);
                cost = Cost.NumberAsEnum;
                return true;
            }
            // Names only, each one defined (Enum.Parse alone would also take
            // "3" or " 3 "); a [Flags] value may combine them with commas.
            string[] names = Enum.GetNames(target);
            foreach (string part in text.Split(','))
            {
                string name = part.Trim();
                if (name.Length == 0 || !names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
            value = Enum.Parse(target, text, true);
            cost = Cost.Exact;
            return true;
        }

        private static bool TryConvertVector(string text, Type target, FieldInfo[] axes, out object? value, out int cost)
        {
            value = null;
            cost = 0;
            string[] parts = text.Split(',');
            if (parts.Length != axes.Length)
            {
                return false;
            }
            object boxed = Activator.CreateInstance(target)!;
            for (int i = 0; i < axes.Length; i++)
            {
                if (!TryConvertNonText(parts[i].Trim(), axes[i].FieldType, out object? component, out int _))
                {
                    return false;
                }
                axes[i].SetValue(boxed, component);
            }
            value = boxed;
            return true;
        }

        private static bool IsIntegral(Type t) =>
            t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
            t == typeof(sbyte) || t == typeof(ushort) || t == typeof(uint) || t == typeof(ulong);

        private static bool IsIntegerText(string text) =>
            decimal.TryParse(text, NumberStyles.Integer, Inv, out decimal _);

        private static bool TryIntegral(string text, Type target, out object? value)
        {
            value = null;
            if (!decimal.TryParse(text, NumberStyles.Integer, Inv, out decimal whole))
            {
                return false;
            }
            try
            {
                value = Convert.ChangeType(whole, target, Inv);
                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        /// <summary>
        /// The x, y[, z[, w]] fields of a vector-shaped struct, in axis order,
        /// or null. Vector-shaped: a struct whose public instance fields are
        /// exactly the first two to four axes, all numeric. Color32 (r, g, b,
        /// a), Rect (properties) and tuples (Item1, Item2) are not vectors.
        /// </summary>
        public static FieldInfo[]? VectorAxes(Type t)
        {
            if (!t.IsValueType || t.IsPrimitive || t.IsEnum)
            {
                return null;
            }
            FieldInfo[] fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            if (fields.Length < 2 || fields.Length > Axes.Length)
            {
                return null;
            }
            FieldInfo[] ordered = new FieldInfo[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo? axis = fields.FirstOrDefault(f => f.Name == Axes[i]);
                if (axis == null || !IsNumber(axis.FieldType))
                {
                    return null;
                }
                ordered[i] = axis;
            }
            return ordered;
        }

        private static bool IsNumber(Type t) =>
            IsIntegral(t) || t == typeof(float) || t == typeof(double) || t == typeof(decimal);

        // ------------------------------------------------------------------
        // Value -> text
        // ------------------------------------------------------------------

        /// <summary>
        /// A value as one line. Strings are quoted and escaped (so "" and
        /// "null" differ from null), numbers are invariant and round-trip,
        /// vectors print as they are typed ("x,y,z"), a type with its own
        /// ToString uses it, and anything else lists its public fields and
        /// properties one level deep: <c>TypeName a=1 b="x"</c>. With
        /// <paramref name="expand"/> false (a value nested in another), an
        /// object without its own text prints only its type name, and a
        /// collection its type and count.
        /// </summary>
        public static string Format(object? value, bool expand = true)
        {
            if (value == null)
            {
                return "null";
            }
            switch (value)
            {
                case string s:
                    return Quote(s);
                case char c:
                    return Quote(c.ToString());
                case bool b:
                    return b ? "true" : "false";
                case float f:
                    return f.ToString("R", Inv);
                case double d:
                    return d.ToString("R", Inv);
                case DictionaryEntry entry:
                    return Format(entry.Key, false) + " => " + Format(entry.Value, false);
            }
            Type t = value.GetType();
            if (t.IsEnum)
            {
                return value.ToString() ?? "";
            }
            if (value is IFormattable formattable && (t.IsPrimitive || t == typeof(decimal)))
            {
                return formattable.ToString(null, Inv);
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                object? key = t.GetProperty("Key")!.GetValue(value, null);
                object? item = t.GetProperty("Value")!.GetValue(value, null);
                return Format(key, false) + " => " + Format(item, false);
            }
            FieldInfo[]? axes = VectorAxes(t);
            if (axes != null)
            {
                return string.Join(",", axes.Select(a => Format(a.GetValue(value), false)).ToArray());
            }
            if (HasOwnToString(t))
            {
                return SafeToString(value);
            }
            if (value is IEnumerable sequence)
            {
                // A lazy sequence is not walked here, and its compiler-made
                // type name (<Items>d__4) says nothing: call it a sequence.
                return TryCount(sequence, out int count)
                    ? FriendlyName(t) + "(count=" + count.ToString(Inv) + ")"
                    : t.Name.IndexOf('<') >= 0 ? "sequence" : FriendlyName(t);
            }
            return expand ? DescribeMembers(value, t) : FriendlyName(t);
        }

        /// <summary>
        /// Whether a returned value is shown item by item: a sequence that is
        /// not a string and has no text of its own. A Unity Transform is
        /// enumerable (its children) but has its own ToString, so it prints
        /// as itself.
        /// </summary>
        public static bool IsItemized(object? value) =>
            value is IEnumerable && !(value is string) && !HasOwnToString(value.GetType());

        /// <summary>A collection's count when it knows it without being walked.</summary>
        public static bool TryCount(IEnumerable sequence, out int count)
        {
            if (sequence is ICollection collection)
            {
                count = collection.Count;
                return true;
            }
            count = 0;
            return false;
        }

        private static string DescribeMembers(object value, Type t)
        {
            List<string> parts = new List<string>();
            int total = 0;
            foreach (FieldInfo field in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                total++;
                if (parts.Count < MaxMembers)
                {
                    parts.Add(field.Name + "=" + ReadMember(() => field.GetValue(value)));
                }
            }
            foreach (PropertyInfo property in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || property.GetGetMethod() == null)
                {
                    continue;
                }
                total++;
                if (parts.Count < MaxMembers)
                {
                    parts.Add(property.Name + "=" + ReadMember(() => property.GetValue(value, null)));
                }
            }
            StringBuilder text = new StringBuilder(FriendlyName(t));
            foreach (string part in parts)
            {
                text.Append(' ').Append(part);
            }
            if (total > parts.Count)
            {
                text.Append(" ...").Append((total - parts.Count).ToString(Inv)).Append(" more");
            }
            return text.ToString();
        }

        private static string ReadMember(Func<object?> read)
        {
            try
            {
                return Format(read(), false);
            }
            catch (Exception ex)
            {
                Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                return "<threw " + cause.GetType().Name + ">";
            }
        }

        private static bool HasOwnToString(Type t)
        {
            MethodInfo? toString = t.GetMethod("ToString", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            return toString != null && toString.DeclaringType != typeof(object) && toString.DeclaringType != typeof(ValueType);
        }

        private static string SafeToString(object value)
        {
            try
            {
                return EscapeControl(value.ToString() ?? "");
            }
            catch (Exception ex)
            {
                return "<ToString threw " + ex.GetType().Name + ">";
            }
        }

        /// <summary>A string in double quotes, with \ " and control characters escaped.</summary>
        public static string Quote(string s)
        {
            StringBuilder text = new StringBuilder(s.Length + 2);
            text.Append('"');
            foreach (char c in s)
            {
                if (c == '"' || c == '\\')
                {
                    text.Append('\\').Append(c);
                }
                else
                {
                    AppendEscaped(text, c);
                }
            }
            text.Append('"');
            return text.ToString();
        }

        /// <summary>Output is line based: a newline inside a value must not start a new line.</summary>
        private static string EscapeControl(string s)
        {
            if (!s.Any(char.IsControl))
            {
                return s;
            }
            StringBuilder text = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                AppendEscaped(text, c);
            }
            return text.ToString();
        }

        private static void AppendEscaped(StringBuilder text, char c)
        {
            switch (c)
            {
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        text.Append("\\u").Append(((int)c).ToString("x4", Inv));
                    }
                    else
                    {
                        text.Append(c);
                    }
                    break;
            }
        }

        // ------------------------------------------------------------------
        // Type names as a C# reader writes them
        // ------------------------------------------------------------------

        private static readonly Dictionary<Type, string> Aliases = new Dictionary<Type, string>
        {
            { typeof(void), "void" }, { typeof(object), "object" }, { typeof(string), "string" },
            { typeof(bool), "bool" }, { typeof(char), "char" }, { typeof(byte), "byte" },
            { typeof(sbyte), "sbyte" }, { typeof(short), "short" }, { typeof(ushort), "ushort" },
            { typeof(int), "int" }, { typeof(uint), "uint" }, { typeof(long), "long" },
            { typeof(ulong), "ulong" }, { typeof(float), "float" }, { typeof(double), "double" },
            { typeof(decimal), "decimal" },
        };

        /// <summary><c>List&lt;int&gt;</c>, <c>float?</c>, <c>string[]</c>: not <c>List`1</c>.</summary>
        public static string FriendlyName(Type t)
        {
            if (t.IsByRef)
            {
                return FriendlyName(t.GetElementType()!);
            }
            if (Aliases.TryGetValue(t, out string? alias))
            {
                return alias;
            }
            if (t.IsArray)
            {
                return FriendlyName(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
            }
            Type? underlying = Nullable.GetUnderlyingType(t);
            if (underlying != null)
            {
                return FriendlyName(underlying) + "?";
            }
            if (t.IsGenericType)
            {
                string name = t.Name;
                int tick = name.IndexOf('`');
                if (tick >= 0)
                {
                    name = name.Substring(0, tick);
                }
                return name + "<" + string.Join(", ", t.GetGenericArguments().Select(FriendlyName).ToArray()) + ">";
            }
            return t.Name;
        }
    }
}
