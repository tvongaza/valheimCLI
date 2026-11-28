using System;
using valheimCLI.Extensions;

namespace valheimCLI
{
    internal sealed class WorldToolsPack : IDisposable
    {
        private ConsoleModule? _module;
        private ExtensionRegistration? _observations;
        public void Register(valheimCLIPlugin core)
        {
            try
            {
                _observations = WorldObservations.Register(core.Extensions!);
                _module = core.Modules.Register("cli.worldtools", "0.1.0", () =>
                { WorldInspectionCommands.Register(); TerrainInspectionCommands.Register(); RockInspectionCommands.Register(); RockActionCommands.Register(); TerrainActionCommands.Register(); });
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { _module?.Dispose(); _observations?.Dispose(); }
    }
}
