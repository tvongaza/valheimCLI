using System;
using System.Collections.Generic;
using UnityEngine;

namespace valheimCLI
{
    /// <summary>
    /// cli_rock_hit: break a rock the way an area hit breaks it, through the
    /// rock's own damage path. A breakable boulder (MineRock5) is hit with the
    /// game's MineRock5.Damage, given a point and a radius: every piece of the
    /// rock inside the sphere takes the damage, and the rock's own support check
    /// (MineRock5.CheckSupport) then drops the pieces left holding on nothing.
    /// A whole rock that fractures when hit (a Destructible whose
    /// m_spawnWhenDestroyed is a MineRock5, as cliff_mistlands2) is hit with
    /// Destructible.Damage, which swaps it for its fractured copy; hit again to
    /// break pieces of that copy. Only rocks are hit: no piece, creature or
    /// other object takes damage.
    /// </summary>
    public static class RockActionCommands
    {
        public static void Register()
        {
            new Terminal.ConsoleCommand("cli_rock_hit", "Hit the rocks inside a sphere with the game's own area damage, as a tool or blast does: a breakable boulder loses every piece inside the sphere, then the pieces its support check finds holding on nothing; a whole rock that fractures (cliff_mistlands2) is swapped for its fractured copy (hit again to break pieces). Rocks only: cli_rock_hit <x> <y> <z> <radius> [damage=1000]. Run it where the rock is owned (the host).", (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args)
            {
                RockHit(args, args.Context.AddString);
            }, isCheat: true);
        }

        public static void RockHit(Terminal.ConsoleEventArgs args, Action<string> output)
        {
            if (!RockHitRequest.TryParse(args.Args, out RockHitRequest request, out string error))
            {
                output(error);
                return;
            }
            float x = request.X;
            float y = request.Y;
            float z = request.Z;
            float radius = request.Radius;
            float damage = request.Damage;
            if (ZNetScene.instance == null)
            {
                output("ERROR: no world loaded");
                return;
            }
            Vector3 point = new Vector3(x, y, z);
            Dictionary<MineRock5, List<Collider>> rocks = new Dictionary<MineRock5, List<Collider>>();
            HashSet<Destructible> whole = new HashSet<Destructible>();
            foreach (Collider collider in Physics.OverlapSphere(point, radius, ~0, QueryTriggerInteraction.Ignore))
            {
                MineRock5 rock = collider.GetComponentInParent<MineRock5>();
                if (rock != null)
                {
                    if (!rocks.TryGetValue(rock, out List<Collider> pieces))
                    {
                        pieces = new List<Collider>();
                        rocks.Add(rock, pieces);
                    }
                    pieces.Add(collider);
                    continue;
                }
                Destructible destructible = collider.GetComponentInParent<Destructible>();
                if (destructible != null && destructible.m_spawnWhenDestroyed != null && destructible.m_spawnWhenDestroyed.GetComponent<MineRock5>() != null)
                {
                    whole.Add(destructible);
                }
            }
            int hit = 0;
            foreach (Destructible destructible in whole)
            {
                ZNetView view = destructible.GetComponent<ZNetView>();
                if (view == null || !view.IsValid())
                {
                    continue;
                }
                Vector3 p = destructible.transform.position;
                string name = CommandArguments.CleanPrefabName(destructible.gameObject.name);
                float scale = destructible.transform.localScale.x;
                bool owner = view.IsOwner();
                string fractured = CommandArguments.CleanPrefabName(destructible.m_spawnWhenDestroyed.name);
                destructible.Damage(Hit(point, 0f, damage));
                output(RockHitRequest.SwapRow(name, p.x, p.y, p.z, scale, owner, fractured, destructible.m_destroyed));
                hit++;
            }
            foreach (KeyValuePair<MineRock5, List<Collider>> entry in rocks)
            {
                MineRock5 rock = entry.Key;
                if (rock.m_nview == null || !rock.m_nview.IsValid() || rock.m_hitAreas == null)
                {
                    continue;
                }
                // Owner is read first: a rock hit to nothing is destroyed and loses its view.
                bool owner = rock.m_nview.IsOwner();
                int before = Left(rock);
                Vector3 p = rock.transform.position;
                string name = CommandArguments.CleanPrefabName(rock.gameObject.name);
                float scale = rock.transform.localScale.x;
                int pieceCount = rock.m_hitAreas.Count;
                // Each piece found by the unbounded overlap above is hit by its own collider: the game's
                // sphere search shares a 128-collider buffer with everything else in the sphere and can skip pieces.
                foreach (Collider piece in entry.Value)
                {
                    if (piece == null || rock.m_nview == null || !rock.m_nview.IsValid())
                    {
                        continue;
                    }
                    rock.Damage(Hit(point, piece, damage));
                }
                int after = rock.m_nview != null && rock.m_nview.IsValid() ? Left(rock) : 0;
                output(RockHitRequest.PiecesRow(name, p.x, p.y, p.z, scale, owner, pieceCount, before, after));
                hit++;
            }
            output(request.Summary(hit));
        }

        private static HitData Hit(Vector3 point, Collider piece, float damage)
        {
            HitData hitData = Hit(point, 0f, damage);
            hitData.m_hitCollider = piece;
            return hitData;
        }

        private static HitData Hit(Vector3 point, float radius, float damage)
        {
            HitData hit = new HitData();
            hit.m_point = point;
            hit.m_radius = radius;
            hit.m_damage.m_damage = damage;
            hit.m_toolTier = 100;
            hit.m_hitType = HitData.HitType.Structural;
            return hit;
        }

        private static int Left(MineRock5 rock)
        {
            int left = 0;
            foreach (MineRock5.HitArea area in rock.m_hitAreas)
            {
                if (area.m_health > 0f)
                {
                    left++;
                }
            }
            return left;
        }

    }
}
