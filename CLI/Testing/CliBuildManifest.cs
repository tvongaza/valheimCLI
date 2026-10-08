using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace valheim_cli.Testing;

/// <summary>Reads a coherent ValheimCLI core and pack build without loading any game assembly.</summary>
public static class CliBuildManifest
{
    private const string ExtensionsNamespace = "valheimCLI.Extensions";

    /// <summary>One DLL's file identity, BepInEx plugin GUIDs, and literal extension registrations.</summary>
    public sealed record FileEntry(string File, string Sha256, IReadOnlyList<string> Plugins,
        SortedDictionary<string, SortedDictionary<string, int>> Extensions);

    /// <summary>Read each DLL in build order. A missing plugin or unrecognised registration fails rather than guessing.</summary>
    public static IReadOnlyList<FileEntry> Generate(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var entries = new List<FileEntry>();
        foreach (string path in files)
        {
            if (!Path.IsPathFullyQualified(path) || !System.IO.File.Exists(path))
                throw new FileNotFoundException($"Give each DLL of the set by its full path; {path} is not a file.", path);
            var plugins = Plugins(path) ?? throw new InvalidDataException($"{path} is not a .NET assembly.");
            if (plugins.Count == 0)
                throw new InvalidDataException($"{path} declares no [BepInPlugin]; list only the ValheimCLI core and its packs.");
            using var stream = System.IO.File.OpenRead(path);
            string sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            entries.Add(new FileEntry(Path.GetFileName(path), sha256, plugins, Extensions(path)));
        }
        return entries;
    }

    /// <summary>Write the schema-1 build manifest from the same metadata read by <see cref="Generate"/>.</summary>
    public static void Write(string build, IEnumerable<string> files, string output)
    {
        if (string.IsNullOrWhiteSpace(build)) throw new ArgumentException("Name the build.", nameof(build));
        var entries = Generate(files);
        if (entries.Count == 0) throw new ArgumentException("List the ValheimCLI core and at least one pack.", nameof(files));
        System.IO.File.WriteAllText(output, JsonSerializer.Serialize(new { schema = 1, build, files = entries.Select(entry =>
            new { file = entry.File, sha256 = entry.Sha256, plugins = entry.Plugins, extensions = entry.Extensions }) },
            new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>Whether a plugin GUID belongs to the ValheimCLI core or a command pack.</summary>
    public static bool IsCliPlugin(string guid) => guid.StartsWith("valheimCLI.", StringComparison.Ordinal);

    /// <summary>The GUIDs from an assembly's BepInPlugin attributes, or null if it is not a .NET assembly.</summary>
    public static IReadOnlyList<string>? Plugins(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();
            var guids = new List<string>();
            foreach (var type in md.TypeDefinitions)
                foreach (var handle in md.GetTypeDefinition(type).GetCustomAttributes())
                {
                    var attribute = md.GetCustomAttribute(handle);
                    if (Owner(md, attribute.Constructor) != ("BepInEx", "BepInPlugin")) continue;
                    var value = md.GetBlobReader(attribute.Value);
                    if (value.ReadUInt16() != 1) continue; // The custom attribute blob's prolog.
                    if (value.ReadSerializedString() is { Length: > 0 } guid) guids.Add(guid);
                }
            return guids;
        }
        catch (Exception error) when (error is BadImageFormatException or InvalidOperationException) { return null; } // Not a .NET assembly.
    }

    /// <summary>The extensions the assembly's IL registers with literal names: owner, then command and result version.</summary>
    public static SortedDictionary<string, SortedDictionary<string, int>> Extensions(string path)
    {
        var found = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        foreach (var handle in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0) continue;
            var code = Decode(md, pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader());
            for (int i = 0; i < code.Count; i++)
            {
                // A literal registration: ldstr id, ldstr version, ldc apiVersion, ldc count, newarr ExtensionCommand. Anything
                // else (the core's own module host passes its id as an argument) is not claimed.
                if (code[i].Op != Newarr || TypeOf(md, code[i].Token) != (ExtensionsNamespace, "ExtensionCommand")) continue;
                if (i < 4 || code[i - 4].String is not { } owner || code[i - 3].String == null || code[i - 2].Int == null || code[i - 1].Int is not { } count) continue;
                string where = $"{Path.GetFileName(path)} ({md.GetString(md.GetTypeDefinition(method.GetDeclaringType()).Name)}.{md.GetString(method.Name)}, {owner})";
                var commands = new SortedDictionary<string, int>(StringComparer.Ordinal);
                int boundary = i, j = i + 1;
                for (; j < code.Count; j++)
                {
                    var (op, token) = (code[j].Op, code[j].Token);
                    if (op == Newarr && TypeOf(md, token) == (ExtensionsNamespace, "ExtensionCommand")) throw Unknown(where, "a second command array before Register");
                    var called = op is Newobj or Call or Callvirt ? Method(md, token) : null;
                    if (op == Newobj && called is { } constructor && constructor.Owner == (ExtensionsNamespace, "ExtensionCommand") && constructor.Name == ".ctor")
                    {
                        if (constructor.Parameters != 7) throw Unknown(where, $"an ExtensionCommand constructor with {constructor.Parameters} parameters");
                        string? name = code.Skip(boundary + 1).Take(j - boundary - 1).Select(instruction => instruction.String).FirstOrDefault(text => text != null);
                        if (name == null || code[j - 1].Int is not { } version) throw Unknown(where, "a command without a literal name and result version");
                        if (!commands.TryAdd(name, version)) throw Unknown(where, $"the command {name} twice");
                        boundary = j;
                    }
                    else if (op is Call or Callvirt && called is { } register && register.Owner == (ExtensionsNamespace, "ExtensionRegistry") && register.Name == "Register") break;
                }
                if (j == code.Count) throw Unknown(where, "a command array that is never registered");
                if (commands.Count != count) throw Unknown(where, $"{count} array elements but {commands.Count} commands read");
                if (!found.TryAdd(owner, commands)) throw Unknown(where, "the owner registered twice");
                i = j;
            }
        }
        return found;
    }

    private static InvalidDataException Unknown(string where, string what) =>
        new($"Cannot read the extension registration in {where} statically: {what}. Write its manifest entry from the build's own record instead.");

    private const ushort Call = 0x28, Callvirt = 0x6F, Newobj = 0x73, Newarr = 0x8D, Ldstr = 0x72;
    private readonly record struct Instruction(ushort Op, EntityHandle Token, string? String, int? Int);

    // The method body's instructions, with string literals, int constants and member tokens; other operands skipped by size.
    private static List<Instruction> Decode(MetadataReader md, BlobReader il)
    {
        var code = new List<Instruction>();
        while (il.RemainingBytes > 0)
        {
            ushort op = il.ReadByte();
            if (op == 0xFE) op = (ushort)(0xFE00 | il.ReadByte());
            switch (op)
            {
                case >= 0x15 and <= 0x1E: code.Add(new(op, default, null, op - 0x16)); break; // ldc.i4.m1 .. ldc.i4.8
                case 0x1F: code.Add(new(op, default, null, il.ReadSByte())); break; // ldc.i4.s
                case 0x20: code.Add(new(op, default, null, il.ReadInt32())); break; // ldc.i4
                case Ldstr: code.Add(new(op, default, md.GetUserString(MetadataTokens.UserStringHandle(il.ReadInt32() & 0xFFFFFF)), null)); break;
                case Call or Callvirt or Newobj or Newarr or 0x27 or 0x29 or 0x70 or 0x71 or 0x74 or 0x75 or 0x79 or (>= 0x7B and <= 0x81) or 0x8C or 0x8F or 0xA3 or 0xA4 or 0xA5 or 0xC2 or 0xC6 or 0xD0
                    or 0xFE06 or 0xFE07 or 0xFE15 or 0xFE16 or 0xFE1C:
                    int token = il.ReadInt32();
                    code.Add(new(op, (token >> 24) is 0x70 or 0x11 ? default : MetadataTokens.EntityHandle(token), null, null)); break;
                case 0x45: int targets = il.ReadInt32(); il.Offset += 4 * targets; code.Add(new(op, default, null, null)); break; // switch
                default:
                    il.Offset += op switch
                    {
                        >= 0x0E and <= 0x13 or (>= 0x2B and <= 0x37) or 0xDE or 0xFE12 or 0xFE19 => 1,
                        0x22 or (>= 0x38 and <= 0x44) or 0xDD => 4,
                        0x21 or 0x23 => 8,
                        >= 0xFE09 and <= 0xFE0E => 2,
                        _ => 0,
                    };
                    code.Add(new(op, default, null, null)); break;
            }
        }
        return code;
    }

    private static (string Namespace, string Name)? TypeOf(MetadataReader md, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeReference => (md.GetString(md.GetTypeReference((TypeReferenceHandle)handle).Namespace), md.GetString(md.GetTypeReference((TypeReferenceHandle)handle).Name)),
        HandleKind.TypeDefinition => (md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)handle).Namespace), md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)handle).Name)),
        _ => null,
    };

    private static (string, string)? Owner(MetadataReader md, EntityHandle constructor) => constructor.Kind switch
    {
        HandleKind.MemberReference => TypeOf(md, md.GetMemberReference((MemberReferenceHandle)constructor).Parent),
        HandleKind.MethodDefinition => TypeOf(md, md.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()),
        _ => null,
    };

    private static ((string, string)? Owner, string Name, int Parameters)? Method(MetadataReader md, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
                var reference = md.GetMemberReference((MemberReferenceHandle)handle);
                return (TypeOf(md, reference.Parent), md.GetString(reference.Name), Parameters(md, reference.Signature));
            case HandleKind.MethodDefinition:
                var definition = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                return (TypeOf(md, definition.GetDeclaringType()), md.GetString(definition.Name), Parameters(md, definition.Signature));
            case HandleKind.MethodSpecification:
                return Method(md, md.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
            default: return null;
        }
    }

    private static int Parameters(MetadataReader md, BlobHandle signature)
    {
        var blob = md.GetBlobReader(signature);
        var header = blob.ReadSignatureHeader();
        if (header.Kind != SignatureKind.Method) return -1;
        if (header.IsGeneric) blob.ReadCompressedInteger();
        return blob.ReadCompressedInteger();
    }
}
