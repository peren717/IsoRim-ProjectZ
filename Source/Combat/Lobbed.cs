using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ.Combat
{
    // Lobbed shots (indirect fire) between floors, declared by other mods through ProjectZ.Api. A lobbed shot is treated
    // like a mortar shell by all three gates between floors:
    //   sight and target picking: no line of sight needed (AttackTargetFinder.CanSee, the target finder's sight filters);
    //   CanHitCell: no line of sight needed (the game's mustCastOnOpenGround still holds);
    //   the projectile: no free intercepts on the way (walls, pawns, slabs between don't stop it), it comes down on the
    //   top of the target's column (Column.Top: a slab or floor over the target stops it there), shields as for shells.
    // A shot declared direct is the opposite, also for a projectile def that flies overhead (flyOverhead): it needs a
    // line of sight and flies as a direct shot between floors (ProjectZ's flight, interceptions, no fall onto a slab top).
    // Nothing declared: as before (the verb's requireLineOfSight, the projectile def's flyOverhead).
    // Only shots between two floors are asked about; on one floor every mod and the game decide as before.
    public static class Lobbed
    {
        private static readonly List<Func<Verb, IntVec3, IntVec3, bool?>> rules = new List<Func<Verb, IntVec3, IntVec3, bool?>>();

        public static bool Any => rules.Count > 0;

        public static void Register(Func<Verb, IntVec3, IntVec3, bool?> rule)
        {
            if (rule != null && !rules.Contains(rule))
            {
                rules.Add(rule);
            }
        }

        public static void Unregister(Func<Verb, IntVec3, IntVec3, bool?> rule) => rules.Remove(rule);

        // The declared kind of a shot between floors from cell from to cell to: true lobbed, false direct, null nothing
        // declared. The first rule with an answer decides; a rule that throws is skipped (logged once).
        public static bool? Declared(Verb verb, IntVec3 from, IntVec3 to)
        {
            if (rules.Count == 0 || verb == null)
            {
                return null;
            }
            for (int i = 0; i < rules.Count; i++)
            {
                try
                {
                    bool? r = rules[i](verb, from, to);
                    if (r.HasValue)
                    {
                        return r;
                    }
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[ProjectZ] a lobbed-shot rule failed: " + e, 61190401 + i);
                }
            }
            return null;
        }

        // Declared lobbed (sight and CanHitCell need no line of sight).
        public static bool IsLobbed(Verb verb, IntVec3 from, IntVec3 to) => rules.Count > 0 && Declared(verb, from, to) == true;

        // ---- projectiles

        private sealed class Kind
        {
            public bool lobbed;
        }

        private static readonly ConditionalWeakTable<Projectile, Kind> marks = new ConditionalWeakTable<Projectile, Kind>();

        public static void Mark(Projectile p, bool lobbed)
        {
            if (p != null)
            {
                marks.AddOrUpdate(p, new Kind { lobbed = lobbed });
            }
        }

        // The projectile was declared (by its verb's rule at launch, or by Api.SetLobbed).
        public static bool Marked(Projectile p, out bool lobbed)
        {
            if (marks.TryGetValue(p, out Kind k))
            {
                lobbed = k.lobbed;
                return true;
            }
            lobbed = false;
            return false;
        }

        // Does this projectile fly as a lobbed shot (from the sky, no intercepts)? Its mark, else its def (flyOverhead).
        public static bool Of(Projectile p)
        {
            if (p == null)
            {
                return false;
            }
            return marks.TryGetValue(p, out Kind k) ? k.lobbed : p.def.projectile.flyOverhead;
        }

        // The verb of the shot being cast (Verb_LaunchProjectile.TryCastShot, where it launches its projectile).
        [ThreadStatic]
        internal static Verb casting;
    }

    // The verb launching: read by the launch (Projectile_Launch_Patch) to ask the rules about its projectile.
    [HarmonyPatch(typeof(Verb_LaunchProjectile), "TryCastShot")]
    public static class LobbedCast_Patch
    {
        public static bool Prepare() => CombatGate.Enabled;

        public static void Prefix(Verb_LaunchProjectile __instance, out Verb __state)
        {
            __state = Lobbed.casting;
            Lobbed.casting = __instance;
        }

        public static void Finalizer(Verb __state)
        {
            Lobbed.casting = __state;
        }
    }
}
