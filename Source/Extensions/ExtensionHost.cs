using System;
using System.Collections.Generic;
using System.Linq;

namespace valheimCLI.Extensions
{
    public static class ExtensionHost
    {
        internal static string? Precondition(ExtensionCommand command)
        {
            ZNet? net = ZNet.instance;
            // IsCheatsEnabled() includes IsServer(), so it can never admit an opted-in joined client.
            return ExtensionAccess.Refusal(command, ZoneSystem.instance != null && ZDOMan.instance != null,
                net != null, net != null && net.IsServer(), net != null && net.IsDedicated(),
                Console.instance != null, Terminal.m_cheat, ClientCommandAccess.AllowOnServerClients);
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
                string[] arguments = Enumerable.Range(2, args.Length - 2).Select(i => args[i]).ToArray();
                Execute(registry, args[1], arguments, args.Context.AddString);
            });
        }

        /// <summary>Compatibility commands use the same dispatch, cancellation and permissions as cli_extension.</summary>
        public static void Execute(ExtensionRegistry registry, string path, string[] arguments, Action<string> output)
        {
            AsyncHandle? handle = valheimCLIPlugin.BeginAsync();
            if (handle == null) { output("ERROR: code=transport_required message=Use the CLI connection."); return; }
            registry.Begin(path, arguments, handle.Id, () => handle.Abandoned, result =>
            {
                try
                {
                    handle.Output("EXTENSION_RESULT " + ExtensionJson.Result(result));
                    // Adapters can retain established console output during a migration.
                    if (result.Ok && result.Data.TryGetValue("legacyLines", out object? lines) && lines is string[] text)
                        foreach (string line in text) handle.Output(line);
                    handle.Output(result.Ok ? "OK: EXTENSION" : "ERROR: code=" + result.Code + " message=Extension failed; see structured result.");
                }
                catch (Exception) { handle.Output("ERROR: code=result_serialization message=Extension returned unsupported or excessive data."); }
                finally { handle.Complete(); }
            });
        }
    }
}
