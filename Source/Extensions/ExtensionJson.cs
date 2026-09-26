using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace valheimCLI.Extensions
{
    // Small, bounded wire format: adapters return JSON values, never Unity objects.
    public static class ExtensionJson
    {
        public static string Result(ExtensionResult result) => Write(new Dictionary<string, object?>
        {
            ["schemaVersion"] = result.ResultVersion, ["ok"] = result.Ok,
            ["extension"] = result.ExtensionId, ["instance"] = result.Instance,
            ["code"] = result.Code, ["message"] = result.Message, ["data"] = result.Data
        });
        public static string Write(object? value)
        {
            var output = new StringBuilder();
            Append(output, value, 0);
            return output.ToString();
        }
        private static void Append(StringBuilder output, object? value, int depth)
        {
            if (depth > 16 || output.Length > 262144) throw new ArgumentException("Extension result exceeds bounds.");
            if (value == null) output.Append("null");
            else if (value is string text) Quote(output, text);
            else if (value is bool flag) output.Append(flag ? "true" : "false");
            else if (value is byte || value is short || value is int || value is long || value is ushort || value is uint || value is ulong || value is sbyte || value is decimal)
                output.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            else if (value is float || value is double)
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number)) throw new ArgumentException("Non-finite result.");
                output.Append(number.ToString("R", CultureInfo.InvariantCulture));
            }
            else if (value is IEnumerable<KeyValuePair<string, object?>> fields)
            {
                output.Append('{'); bool first = true;
                foreach (var field in fields)
                { if (!first) output.Append(','); first = false; Quote(output, field.Key); output.Append(':'); Append(output, field.Value, depth + 1); }
                output.Append('}');
            }
            else if (value is IEnumerable items && !(value is IDictionary))
            {
                output.Append('['); bool first = true;
                foreach (object? item in items)
                { if (!first) output.Append(','); first = false; Append(output, item, depth + 1); }
                output.Append(']');
            }
            else throw new ArgumentException("Unsupported extension result type: " + value.GetType().FullName);
            if (output.Length > 262144) throw new ArgumentException("Extension result exceeds bounds.");
        }
        private static void Quote(StringBuilder output, string value)
        {
            if (value.Length > 262144) throw new ArgumentException("Extension string exceeds bounds.");
            output.Append('"');
            foreach (char c in value)
            {
                if (c == '"' || c == '\\') output.Append('\\').Append(c);
                else if (c < 32 || char.IsSurrogate(c)) output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else output.Append(c);
            }
            output.Append('"');
        }
    }
}
