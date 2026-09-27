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
            new ExtensionCommand("terrain-surface", "Read a loaded terrain vertex and its own collider: <x> <z> (grid vertices)", Surface, readOnly: true, needsWorld: true),
            new ExtensionCommand("player-support", "Read local player position, motion and grounded state", Support, readOnly: true, role: ExtensionRole.Client, needsWorld: true),
            new ExtensionCommand("terrain", "terrain <x> <z> <generator|loaded-ground>; metres, x/z horizontal", Terrain, readOnly: true, needsWorld: true));
        private static IEnumerator Surface(ExtensionContext context)
        {
            if(context.Arguments.Count!=2 || !CommandArguments.TryFiniteFloat(context.Arguments[0],out float x) ||
                !CommandArguments.TryFiniteFloat(context.Arguments[1],out float z) || Math.Abs(x)>20000 || Math.Abs(z)>20000 || x!=Mathf.Round(x) || z!=Mathf.Round(z))
            { context.Fail("usage","terrain-surface <integer x> <integer z>; native 1m grid vertices within +/-20000"); yield break; }
            var point=new Vector3(x,0,z); var hm=Heightmap.FindHeightmap(point);
            float height=0; RaycastHit hit=default;
            var collider=hm != null ? hm.GetComponent<MeshCollider>() : null;
            bool complete=hm!=null && hm.m_scale==1 && hm.GetWorldHeight(point,out height) && collider!=null &&
                collider.Raycast(new Ray(new Vector3(x,height+20,z),Vector3.down),out hit,40);
            context.Succeed(new Dictionary<string,object?>{["source"]="loaded-terrain-surface",["complete"]=complete,["x"]=x,["z"]=z,
                ["height"]=complete?(object)height:null,["colliderHeight"]=complete?(object)hit.point.y:null,["units"]="metres"});
            yield break;
        }
        private static IEnumerator Support(ExtensionContext context)
        {
            if(context.Arguments.Count!=0){context.Fail("usage","player-support takes no arguments");yield break;}
            var player=Player.m_localPlayer;
            if(player==null){context.Succeed(new Dictionary<string,object?>{["source"]="local-player-support",["complete"]=false});yield break;}
            var p=player.transform.position; var velocity=player.GetVelocity();
            context.Succeed(new Dictionary<string,object?>{["source"]="local-player-support",["complete"]=true,
                ["x"]=p.x,["y"]=p.y,["z"]=p.z,["speed"]=velocity.magnitude,["grounded"]=player.IsOnGround(),
                ["flying"]=player.IsDebugFlying(),["attached"]=player.IsAttached(),["dead"]=player.IsDead(),["teleporting"]=player.IsTeleporting(),["units"]="metres"});
            yield break;
        }
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
                if (complete) height = WorldGenerator.instance!.GetHeight(x, z);
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
