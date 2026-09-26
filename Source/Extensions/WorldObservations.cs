using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace valheimCLI.Extensions
{
    // First bundled module. Existing text inspection commands remain compatible.
    internal static class WorldObservations
    {
        internal static void Register(ExtensionRegistry registry) => registry.Register("valheim.world", "0.1.0", 1,
            new ExtensionCommand("terrain", "terrain <x> <z> <generator|loaded-ground>; metres, x/z horizontal", Terrain, readOnly: true, needsWorld: true));
        private static IEnumerator Terrain(ExtensionContext context)
        {
            var args = context.Arguments;
            if (args.Count != 3 || !CommandArguments.TryFiniteFloat(args[0], out float x) || !CommandArguments.TryFiniteFloat(args[1], out float z) ||
                Math.Abs(x) > 20000 || Math.Abs(z) > 20000 || (args[2] != "generator" && args[2] != "loaded-ground"))
            { context.Fail("usage", "terrain <x> <z> <generator|loaded-ground>; within +/-20000 m"); yield break; }
            bool complete; float height = 0;
            if (args[2] == "generator")
            {
                complete = WorldGenerator.instance != null;
                if (complete) height = WorldGenerator.instance.GetHeight(x, z);
            }
            else
            {
                Vector3 point = new Vector3(x, 0, z);
                complete = Heightmap.FindHeightmap(point) != null && ZoneSystem.instance.GetGroundHeight(point, out height);
            }
            context.Succeed(new Dictionary<string, object?>
            {
                ["source"] = args[2], ["complete"] = complete, ["x"] = x, ["z"] = z,
                ["height"] = complete ? (object)height : null, ["units"] = "metres"
            });
        }
    }
}
