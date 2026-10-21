using System;
using System.Collections.Generic;
using ProjectZ.Combat;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // Marker on a jump ability (an AbilityDef whose verb is a jump verb): a pawn that has it jumps to a fight on another
    // floor (JobGiver_AIJumpAcross). The locust's PZ_LocustJump carries it; another jump ability joins by adding it.
    public class JumpAcrossFloors : DefModExtension
    {
    }

    // A jump to a fight on another floor (design "locust jump", user 2026-10-21). Locusts fly in the game, but nothing flies
    // between floors; they jump instead, through the game's jump (JumpUtility, PawnFlyer) and ProjectZ's rules for jumps
    // between floors (Combat/JumpFlights.cs: 3D range, open sky over the landing, a clear arc, no void rows).
    // Sits right where the fight finds no enemy it can walk to: after the assault duties' JobGiver_AIFightEnemies (before
    // their building trashing, which a real colony almost always offers, the sapper and ProjectZ's ladder climb), after the
    // manhunter's own search, after the hive defence (Patches/PZ_LocustJump.xml). It acts only when:
    //   - the map has floors, and the pawn has a ready (not cooling down) jump ability marked JumpAcrossFloors;
    //   - a hostile pawn it would fight (threat, auto-targetable) is on another floor within targetAcquireRadius (3D),
    //     its current enemy target first, then the nearest, and it can't walk there (stairs, ladders, doors it could bash);
    //   - a landing next to that pawn, on its floor, passes the jump's own target check from where the pawn stands (the
    //     rules above), and leads to the target on foot.
    // Then it casts the jump; after landing the duty's own fighting takes over. Never a jump on one floor, never while
    // wandering. Otherwise nothing: the chain after it runs as before. A miss is remembered for MemoTicks and a given jump
    // for RetryTicks (a cast that never took off isn't given again every think); the ability's cooldown stops chain jumps.
    public class JobGiver_AIJumpAcross : ThinkNode_JobGiver
    {
        public float targetAcquireRadius = 50f;

        public override ThinkNode DeepCopy(bool resolve = true)
        {
            JobGiver_AIJumpAcross o = (JobGiver_AIJumpAcross)base.DeepCopy(resolve);
            o.targetAcquireRadius = targetAcquireRadius;
            return o;
        }

        protected override Job TryGiveJob(Pawn pawn)
        {
            try
            {
                return JumpAcross.TryGive(pawn, targetAcquireRadius);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Jump to another floor failed: " + e, 81512391);
                return null;
            }
        }
    }

    public static class JumpAcross
    {
        // test switch -projectznojumpacross: the job giver gives nothing (the game's behaviour)
        public static bool Off;

        public const int MemoTicks = 250;        // nothing to jump to: asked again after this (as the climb planner's memo)
        public const int RetryTicks = 120;       // a jump given: not given again before this
        public const float LandingRadius = 2.9f; // landings searched round the target (nearest first)
        public const int MaxTargets = 6;         // unreachable targets tried per think

        // counters (tests)
        public static int Asked, Cooling, MemoHits, NoTarget, Reachable, NoLanding, Given;

        private static readonly Dictionary<int, int> memo = new Dictionary<int, int>();

        // The pawn's jump ability marked JumpAcrossFloors, else null.
        public static Ability AbilityOf(Pawn pawn)
        {
            List<Ability> list = pawn?.abilities?.AllAbilitiesForReading;
            if (list == null)
            {
                return null;
            }
            for (int i = 0; i < list.Count; i++)
            {
                Ability a = list[i];
                if (a?.def != null && a.verb != null && a.def.HasModExtension<JumpAcrossFloors>() && JumpRules.IsJumpVerb(a.verb))
                {
                    return a;
                }
            }
            return null;
        }

        public static Job TryGive(Pawn pawn, float radius)
        {
            if (Off || pawn == null || !pawn.Spawned || pawn.Downed || pawn.abilities == null)
            {
                return null;
            }
            Map map = pawn.Map;
            if (ProjectZ.Annex(map) == null || !CombatGate.Enabled || !FailSoft.IsApplied(nameof(Jump_CanHitTargetFrom_Patch)))
            {
                return null;
            }
            Ability ab = AbilityOf(pawn);
            if (ab == null)
            {
                return null;
            }
            Asked++;
            if (!ab.CanCast)
            {
                Cooling++;
                return null;
            }
            int now = Find.TickManager.TicksGame;
            int id = pawn.thingIDNumber;
            // (a memo from another game: further ahead than it can be, ignored)
            if (memo.TryGetValue(id, out int until) && now < until && until - now <= MemoTicks)
            {
                MemoHits++;
                return null;
            }
            if (memo.Count > 512)
            {
                memo.Clear();
            }
            IntVec3 landing = FindLanding(pawn, ab, radius, map);
            if (!landing.IsValid)
            {
                memo[id] = now + MemoTicks;
                return null;
            }
            memo[id] = now + RetryTicks;
            Given++;
            return ab.GetJob(landing, landing);
        }

        // A landing for a jump to the nearest hostile on another floor that can't be walked to; Invalid when none.
        private static IntVec3 FindLanding(Pawn pawn, Ability ab, float radius, Map map)
        {
            IntVec3 root = pawn.Position;
            float range = ab.verb.EffectiveRange;
            // (a landing is within LandingRadius of the target: a target further than that past the range has none)
            float reach = Math.Min(radius, range + LandingRadius);
            List<KeyValuePair<float, Pawn>> found = new List<KeyValuePair<float, Pawn>>();
            Pawn current = pawn.mindState?.enemyTarget as Pawn;
            IReadOnlyList<Pawn> all = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
            {
                Pawn t = all[i];
                if (t == pawn || t.Dead || !JumpRules.Cross(root, t.Position, map))
                {
                    continue;
                }
                float d = Range3D.Distance(root, t.Position, map);
                if (d > reach || !pawn.HostileTo(t) || t.ThreatDisabled(pawn) || !AttackTargetFinder.IsAutoTargetable(t))
                {
                    continue;
                }
                found.Add(new KeyValuePair<float, Pawn>(t == current ? -1f : d, t));
            }
            if (found.Count == 0)
            {
                NoTarget++;
                return IntVec3.Invalid;
            }
            found.Sort((a, b) => a.Key.CompareTo(b.Key));
            TraverseParms walk = TraverseParms.For(pawn, Danger.Deadly, TraverseMode.PassDoors);
            int tried = 0;
            for (int i = 0; i < found.Count && tried < MaxTargets; i++)
            {
                Pawn t = found[i].Value;
                if (pawn.CanReach(t, PathEndMode.Touch, Danger.Deadly, canBashDoors: true, canBashFences: true, TraverseMode.PassDoors))
                {
                    Reachable++;
                    continue;   // it can walk there: the game's way (the duty's givers)
                }
                tried++;
                IntVec3 c = Landing(pawn, ab, t, walk, map);
                if (c.IsValid)
                {
                    return c;
                }
            }
            NoLanding++;
            return IntVec3.Invalid;
        }

        // The nearest cell round target, on its floor, the jump may land on from where the pawn stands and from which the
        // target is reached on foot.
        private static IntVec3 Landing(Pawn pawn, Ability ab, Pawn target, TraverseParms walk, Map map)
        {
            IntVec3 tc = target.Position;
            IntVec3 root = pawn.Position;
            int n = GenRadial.NumCellsInRadius(LandingRadius);
            for (int i = 1; i < n; i++)
            {
                IntVec3 c = tc + GenRadial.RadialPattern[i];
                if (!c.InBounds(map) || !ProjectZ.SameBand(c, tc, map) || !JumpRules.Cross(root, c, map) || c.GetFirstPawn(map) != null)
                {
                    continue;
                }
                if (!map.pawnDestinationReservationManager.CanReserve(c, pawn) || !ab.verb.ValidateTarget(c, false))
                {
                    continue;
                }
                if (map.reachability.CanReach(c, target, PathEndMode.Touch, walk))
                {
                    return c;
                }
            }
            return IntVec3.Invalid;
        }
    }
}
