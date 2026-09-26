using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace valheimCLI
{
    /// <summary>The line-counted CMD/CMDT reply, shared by all command responses.</summary>
    public static class CommandResponse
    {
        public static void Write(TextWriter writer, IEnumerable<string> output)
        {
            // AddString/log entries can contain newlines. The receiver counts
            // physical lines, not calls to AddString; preserve blank lines too.
            List<string> lines = output.SelectMany(text =>
                text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')).ToList();
            writer.WriteLine($"OUTPUT:{lines.Count}");
            foreach (string line in lines)
                writer.WriteLine(line);
            writer.WriteLine("END_OUTPUT");
        }
    }
}
