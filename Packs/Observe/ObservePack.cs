using System;
using System.Collections;
using System.Linq;
using BepInEx;
using valheimCLI.Extensions;

namespace valheimCLI.Observe
{
    [BepInPlugin("valheimCLI.observe", "CLI Observations", "0.1.0")]
    [BepInDependency("valheimCLI.valheimCLI", "1.1.0")]
    public sealed class ObservePack : BaseUnityPlugin
    {
        private ExtensionRegistration? _registration;

        private IEnumerator Start()
        {
            float deadline = UnityEngine.Time.realtimeSinceStartup + 30f;
            while (valheimCLIPlugin.Instance?.Extensions == null)
            {
                if (UnityEngine.Time.realtimeSinceStartup > deadline)
                { Logger.LogError("CLI core 1.1 is not ready."); yield break; }
                yield return null;
            }
            var registry = valheimCLIPlugin.Instance!.Extensions!;
            while (registry.Registrations.Any(r => r.Id == "valheim.observe"))
            {
                if (UnityEngine.Time.realtimeSinceStartup > deadline)
                { Logger.LogError("Previous Observe pack is active or draining; replacement refused."); yield break; }
                yield return null;
            }
            _registration = registry.Register("valheim.observe", "0.1.0", ExtensionRegistry.ApiVersion,
                ContentCensus.Command(), HarmonyCensus.Command(), DungeonRooms.Command(),
                UnresolvedPrefabs.Command(), ZonePresence.Command(), GlobalKeyCommands.List(),
                ConfigEntryCommand.Command(), PlayerCustomData.Command(),
                ReviewState.BeginCommand(), ReviewState.RestoreCommand(), ReviewState.MistOffCommand(),
                ReviewState.ClutterOffCommand(), ReviewClipFrames.Command());
        }

        private void OnDestroy()
        {
            ReviewClipFrames.AbortOnUnload();
            ReviewState.RestoreOnUnload();
            _registration?.Dispose();
        }
    }
}
