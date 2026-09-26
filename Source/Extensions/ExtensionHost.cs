using System;
using System.Collections.Generic;
using System.Linq;

namespace valheimCLI.Extensions
{
    internal static class ExtensionHost
    {
        internal static string? Precondition(ExtensionCommand command)
        {
            bool server = ZNet.instance != null && ZNet.instance.IsServer();
            if (command.NeedsWorld && (ZoneSystem.instance == null || ZDOMan.instance == null)) return "A loaded world is required.";
            if (command.Role == ExtensionRole.Server && !server) return "Server role required.";
            if (command.Role == ExtensionRole.Client && (ZNet.instance == null || server && ZNet.instance.IsDedicated())) return "Client role required.";
            if (!command.ReadOnly)
            {
                if (Console.instance == null || !Console.instance.IsCheatsEnabled()) return "Enable devcommands for mutating extension commands.";
                if (ZNet.instance != null && !server && !ClientCommandAccess.AllowOnServerClients)
                    return "Client mutation requires AllowOnServerClients on this test client.";
            }
            return null;
        }
        internal static void Register(ExtensionRegistry registry)
        {
            new Terminal.ConsoleCommand("cli_extensions", "List active extension capabilities as JSON", args =>
            {
                var owners = registry.Registrations.Select(owner => new Dictionary<string, object?>
                {
                    ["id"] = owner.Id, ["version"] = owner.Version, ["instance"] = owner.Instance,
                    ["closing"] = registry.IsClosing(owner), ["cleanupError"] = owner.CleanupError,
                    ["commands"] = registry.Commands(owner).Select(command => new Dictionary<string, object?>
                    {
                        ["name"] = command.Name, ["help"] = command.Help, ["readOnly"] = command.ReadOnly,
                        ["role"] = command.Role.ToString(), ["needsWorld"] = command.NeedsWorld,
                        ["resultVersion"] = command.ResultVersion
                    }).ToArray()
                }).ToArray();
                args.Context.AddString("EXTENSIONS " + ExtensionJson.Write(new Dictionary<string, object?> { ["apiVersion"] = ExtensionRegistry.ApiVersion, ["extensions"] = owners }));
                args.Context.AddString("OK: EXTENSIONS");
            });
            new Terminal.ConsoleCommand("cli_extension", "Run <extension-id/command> [arguments]", args =>
            {
                if (args.Length < 2) { args.Context.AddString("ERROR: code=usage message=cli_extension <extension-id/command> [arguments]"); return; }
                AsyncHandle? handle = valheimCLIPlugin.BeginAsync();
                if (handle == null) { args.Context.AddString("ERROR: code=transport_required message=Use the CLI connection."); return; }
                string[] arguments = Enumerable.Range(2, args.Length - 2).Select(i => args[i]).ToArray();
                registry.Begin(args[1], arguments, handle.Id, () => handle.Abandoned, result =>
                {
                    try
                    {
                        handle.Output("EXTENSION_RESULT " + ExtensionJson.Result(result));
                        handle.Output(result.Ok ? "OK: EXTENSION" : "ERROR: code=" + result.Code + " message=Extension failed; see structured result.");
                    }
                    catch (Exception) { handle.Output("ERROR: code=result_serialization message=Extension returned unsupported or excessive data."); }
                    finally { handle.Complete(); }
                });
            });
        }
    }
}
