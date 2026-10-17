using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Combat
{
    // Shooting between floors (design §1.2 patch list, §2.2). Only shots whose two ends are on different floors change;
    // everything on one floor is left to the game. Off when Combat Extended is loaded (it has its own heights).
    public static class CombatGate
    {
        private static bool? enabled;

        public static bool Enabled
        {
            get
            {
                if (enabled == null)
                {
                    bool ce = ModsConfig.ActiveModsInLoadOrder.Any(m => m.PackageIdPlayerFacing != null && m.PackageIdPlayerFacing.ToLowerInvariant().Contains("combatextended"));
                    enabled = !ce;
                    if (ce)
                    {
                        Log.Message("[ProjectZ] Combat Extended is loaded: shooting between floors is left to it (ProjectZ's combat patches are off).");
                    }
                }
                return enabled.Value;
            }
        }

        // Accuracy bonus for shooting down at a lower floor (decision D7, a setting).
        public static float HeightBonus => ProjectZMod.Settings == null ? 0.2f : ProjectZMod.Settings.heightAccuracy;
    }

    // Cover between floors: the game's cover givers next to the target, seen from the shooter's spot moved onto the
    // target's floor, kept only where the shot passes below the cover's top (sandbags don't cover from a floor above;
    // a parapet covers a pawn on an upper floor from below).
    public static class HeightCover
    {
        public static List<CoverInfo> For(LocalTargetInfo target, IntVec3 shooter, Map map, out float block)
        {
            IntVec3 tc = target.Cell;
            IntVec3 vs = Heights.OnFloorOf(shooter, tc, map);
            List<CoverInfo> all = CoverUtility.CalculateCoverGiverSet(target, vs, map);
            int ks = ProjectZ.FloorOf(shooter, map);
            int kt = ProjectZ.FloorOf(tc, map);
            IntVec3 gs = ProjectZ.GroundCellOf(shooter, map);
            IntVec3 gt = ProjectZ.GroundCellOf(tc, map);
            float hs = ks * Heights.Storey + Heights.Eye;
            float ht = kt * Heights.Storey + Heights.Middle;
            List<CoverInfo> kept = new List<CoverInfo>(all.Count);
            block = 0f;
            foreach (CoverInfo ci in all)
            {
                if (ci.Thing == null)
                {
                    continue;
                }
                float lineHeight = Heights.HeightAt(gs, gt, hs, ht, ProjectZ.GroundCellOf(ci.Thing.Position, map));
                float top = kt * Heights.Storey + Heights.CoverTop(ci.Thing);
                if (lineHeight > top)
                {
                    continue;
                }
                kept.Add(ci);
                if (ci.Thing.Position != vs)
                {
                    block += (1f - block) * ci.BlockChance;
                }
            }
            return kept;
        }
    }

    // The one line-of-sight check behind every shot: AI target picking, CanHitTarget, TryFindShootLineFromTo.
    [HarmonyPatch(typeof(Verb), "CanHitCellFromCellIgnoringRange")]
    public static class Verb_CanHitCell_Patch
    {
        public static bool Prepare() => CombatGate.Enabled;

        public static bool Prefix(Verb __instance, IntVec3 __0, IntVec3 __1, ref bool __result)
        {
            Map map = __instance.caster?.Map;
            if (map == null || !Heights.CrossFloor(__0, __1, map))
            {
                return true;
            }
            VerbProperties vp = __instance.verbProps;
            if (vp.mustCastOnOpenGround && (!__1.Standable(map) || map.thingGrid.CellContains(__1, ThingCategory.Pawn)))
            {
                __result = false;
                return false;
            }
            __result = !vp.requireLineOfSight || Heights.CanSee(__0, __1, map);
            return false;
        }
    }

    // Leaning out around a wall corner to shoot (the game's ShootLeanUtility, behind TryFindShootLineFromTo, the target's
    // own lean cells and AttackTargetFinder.CanSee). The game picks the lean cells from the direction to the target in
    // map rows, where another floor is a whole band of rows away (always "north" or "south"); between floors it gets
    // the direction over the ground instead (the target's column on the shooter's floor). Lean cells off the shooter's
    // floor (the void rows past a band's edge) are dropped. Each lean cell then goes through the height line of sight
    // (Verb_CanHitCell_Patch, Heights.CanSee): an upper end must lean out at its slab's edge.
    [HarmonyPatch(typeof(ShootLeanUtility), nameof(ShootLeanUtility.LeanShootingSourcesFromTo))]
    public static class ShootLean_Patch
    {
        public static bool Off;   // test only: the game's own directions, to show the gap

        public static bool Prepare() => CombatGate.Enabled;

        public static void Prefix(IntVec3 shooterLoc, ref IntVec3 targetPos, Map map)
        {
            if (!Off && map != null && Heights.CrossFloor(shooterLoc, targetPos, map))
            {
                targetPos = Heights.OnFloorOf(targetPos, shooterLoc, map);
            }
        }

        public static void Postfix(IntVec3 shooterLoc, Map map, List<IntVec3> listToFill)
        {
            if (Off || map == null || listToFill.Count == 0 || ProjectZ.Annex(map) == null)
            {
                return;
            }
            int k = ProjectZ.FloorOf(shooterLoc, map);
            if (k < 0)
            {
                return;
            }
            for (int i = listToFill.Count - 1; i >= 0; i--)
            {
                if (ProjectZ.FloorOf(listToFill[i], map) != k)
                {
                    listToFill.RemoveAt(i);
                }
            }
        }
    }

    // Range between floors: the 3D rule with the high-ground bonus (Range3D). Behind CanHitTarget/CanHitTargetFrom,
    // TryFindShootLineFromTo, abilities (Verb_CastAbility) and drafted attack orders.
    [HarmonyPatch(typeof(Verb), nameof(Verb.OutOfRange))]
    public static class Verb_OutOfRange_Patch
    {
        public static bool Prepare() => CombatGate.Enabled;

        public static bool Prefix(Verb __instance, IntVec3 root, LocalTargetInfo targ, CellRect occupiedRect, ref bool __result)
        {
            Map map = __instance.caster?.Map;
            if (map == null || !targ.IsValid || !Heights.CrossFloor(root, targ.Cell, map))
            {
                return true;
            }
            float min = __instance.verbProps.EffectiveMinRange(targ, __instance.caster);
            __result = Range3D.OutOfRange(root, occupiedRect, targ.Cell, __instance.EffectiveRange, min, map);
            return false;
        }
    }

    // The hit report between floors: distance over the ground, the height accuracy bonus when shooting down, cover by
    // height. Verb_LaunchProjectile.TryCastShot uses this report, so the tooltip and the real shot agree.
    [HarmonyPatch(typeof(ShotReport), nameof(ShotReport.HitReportFor))]
    public static class ShotReport_Patch
    {
        private static readonly AccessTools.StructFieldRef<ShotReport, float> distance = AccessTools.StructFieldRefAccess<ShotReport, float>("distance");
        private static readonly AccessTools.StructFieldRef<ShotReport, float> shooterFactor = AccessTools.StructFieldRefAccess<ShotReport, float>("factorFromShooterAndDist");
        private static readonly AccessTools.StructFieldRef<ShotReport, float> equipmentFactor = AccessTools.StructFieldRefAccess<ShotReport, float>("factorFromEquipment");
        private static readonly AccessTools.StructFieldRef<ShotReport, List<CoverInfo>> covers = AccessTools.StructFieldRefAccess<ShotReport, List<CoverInfo>>("covers");
        private static readonly AccessTools.StructFieldRef<ShotReport, float> coversBlock = AccessTools.StructFieldRefAccess<ShotReport, float>("coversOverallBlockChance");

        public static bool Prepare() => CombatGate.Enabled;

        public static void Postfix(Thing caster, Verb verb, LocalTargetInfo target, ref ShotReport __result)
        {
            Map map = caster?.Map;
            if (map == null || verb == null || !target.IsValid || !Heights.CrossFloor(caster.Position, target.Cell, map))
            {
                return;
            }
            float d = Heights.GroundDistance(caster.Position, target.Cell, map);
            distance(ref __result) = d;
            float shooter = 1f;
            if (verb.verbProps.canGoWild)
            {
                shooter = ShotReport.HitFactorFromShooter(caster, d);
                if (ProjectZ.FloorOf(caster.Position, map) > ProjectZ.FloorOf(target.Cell, map))
                {
                    shooter = Mathf.Min(1f, shooter + CombatGate.HeightBonus);
                }
            }
            shooterFactor(ref __result) = shooter;
            equipmentFactor(ref __result) = verb.verbProps.GetHitChanceFactor(verb.EquipmentSource, d);
            if (target.HasThing && !target.Thing.def.CanBenefitFromCover)
            {
                covers(ref __result) = new List<CoverInfo>();
                coversBlock(ref __result) = 0f;
                return;
            }
            covers(ref __result) = HeightCover.For(target, caster.Position, map, out float block);
            coversBlock(ref __result) = block;
        }
    }

    // A shot that goes wild between floors scatters around the target as seen from the shooter's spot on its floor.
    [HarmonyPatch(typeof(ShootLine), nameof(ShootLine.ChangeDestToMissWild))]
    public static class ShootLine_MissWild_Patch
    {
        private static readonly AccessTools.StructFieldRef<ShootLine, IntVec3> source = AccessTools.StructFieldRefAccess<ShootLine, IntVec3>("source");

        public static bool Prepare() => CombatGate.Enabled;

        public static void Prefix(ref ShootLine __instance, Map map, out IntVec3 __state)
        {
            __state = IntVec3.Invalid;
            if (map != null && Heights.CrossFloor(__instance.Source, __instance.Dest, map))
            {
                __state = __instance.Source;
                source(ref __instance) = Heights.OnFloorOf(__instance.Source, __instance.Dest, map);
            }
        }

        public static void Postfix(ref ShootLine __instance, IntVec3 __state)
        {
            if (__state.IsValid)
            {
                source(ref __instance) = __state;
            }
        }
    }

    // Seeing a target on another floor (target finder validators): the same height line of sight.
    [HarmonyPatch(typeof(AttackTargetFinder), nameof(AttackTargetFinder.CanSee))]
    public static class AttackTargetFinder_CanSee_Patch
    {
        public static bool Prepare() => CombatGate.Enabled;

        public static bool Prefix(Thing seer, Thing target, ref bool __result)
        {
            Map map = seer?.Map;
            if (map == null || target == null || target.Map != map || !Heights.CrossFloor(seer.Position, target.Position, map))
            {
                return true;
            }
            __result = Heights.CanSeeLeaning(seer, target);
            return false;
        }
    }

    // Picking a target to shoot: the game measures its range filters and scores flat, which puts every pawn on another
    // floor a whole floor's worth of rows away. Targets on other floors that can be shot from here are scored with their
    // real distance and height cover and win when they score better than the game's pick (or the game found none it
    // can shoot from here).
    [HarmonyPatch(typeof(AttackTargetFinder), nameof(AttackTargetFinder.BestAttackTarget))]
    public static class AttackTargetFinder_Best_Patch
    {
        private static readonly Func<Thing, IAttackTarget, TargetScanFlags, bool> ignoreNoncombatant =
            AccessTools.MethodDelegate<Func<Thing, IAttackTarget, TargetScanFlags, bool>>(AccessTools.Method(typeof(AttackTargetFinder), "ShouldIgnoreNoncombatant"));
        private static readonly Func<Thing, Thing, bool, bool, bool> canReach =
            AccessTools.MethodDelegate<Func<Thing, Thing, bool, bool, bool>>(AccessTools.Method(typeof(AttackTargetFinder), "CanReach"));
        private static readonly Func<IAttackTarget, IAttackTargetSearcher, Verb, float> score =
            AccessTools.MethodDelegate<Func<IAttackTarget, IAttackTargetSearcher, Verb, float>>(AccessTools.Method(typeof(AttackTargetFinder), "GetShootingTargetScore"));
        private static readonly Func<IAttackTargetSearcher, bool> hasRanged =
            AccessTools.MethodDelegate<Func<IAttackTargetSearcher, bool>>(AccessTools.Method(typeof(AttackTargetFinder), "HasRangedAttack"));

        private static readonly List<IAttackTarget> candidates = new List<IAttackTarget>();

        public static bool Prepare() => CombatGate.Enabled;

        public static void Postfix(IAttackTargetSearcher searcher, TargetScanFlags flags, Predicate<Thing> validator, float minDist, float maxDist,
            bool canBashDoors, bool canTakeTargetsCloserThanEffectiveMinRange, bool canBashFences, bool onlyRanged, ref IAttackTarget __result)
        {
            try
            {
                Pick(searcher, flags, validator, minDist, maxDist, canBashDoors, canTakeTargetsCloserThanEffectiveMinRange, canBashFences, onlyRanged, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Picking a target on another floor failed: " + e, 61190301);
            }
        }

        private static void Pick(IAttackTargetSearcher searcher, TargetScanFlags flags, Predicate<Thing> validator, float minDist, float maxDist,
            bool canBashDoors, bool canTakeTargetsCloserThanEffectiveMinRange, bool canBashFences, bool onlyRanged, ref IAttackTarget __result)
        {
            Thing s = searcher?.Thing;
            Map map = s?.Map;
            if (map == null || !s.Spawned || ProjectZ.Annex(map) == null)
            {
                return;
            }
            Verb verb = searcher.CurrentEffectiveVerb;
            Pawn sp = s as Pawn;
            if (verb == null || verb.verbProps.IsMeleeAttack || !(hasRanged(searcher) || onlyRanged) || sp != null && sp.InAggroMentalState)
            {
                return;
            }
            int ks = ProjectZ.FloorOf(s.Position, map);
            if (ks < 0)
            {
                return;
            }
            candidates.Clear();
            candidates.AddRange(map.attackTargetsCache.GetPotentialTargetsFor(searcher));
            IAttackTarget best = null;
            float bestScore = float.MinValue;
            IAttackTarget bestNoHit = null;
            float bestNoHitScore = float.MinValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                IAttackTarget t = candidates[i];
                Thing th = t.Thing;
                if (t == searcher || th == null || !th.Spawned || th.Map != map || !Heights.CrossFloor(s.Position, th.Position, map))
                {
                    continue;
                }
                // Range filters by the range rule (maxDist is the weapon's range or a search radius); the score by the
                // real 3D distance.
                float d = Range3D.Distance(s.Position, th.Position, map);
                if (Range3D.RangeDistance(s.Position, th.Position, map) > maxDist || d < minDist)
                {
                    continue;
                }
                if (!canTakeTargetsCloserThanEffectiveMinRange)
                {
                    float mr = verb.verbProps.EffectiveMinRange(th, s);
                    if (mr > 0f && d < mr)
                    {
                        continue;
                    }
                }
                if (!Valid(t, th, s, sp, searcher, flags, validator, verb, canBashDoors, canBashFences, map))
                {
                    continue;
                }
                if (!verb.CanHitTargetFrom(s.Position, th))
                {
                    // The game's NeedReachableIfCantHitFromMyPos between floors: a target this pawn sees but can't hit
                    // from here counts when it can walk under it (to the target's column on its own floor), where the
                    // cast position search (CastSpots) finds it a spot within range. Only when the game found nothing.
                    if (__result == null && sp != null && (flags & TargetScanFlags.NeedReachableIfCantHitFromMyPos) != 0
                        && ((flags & TargetScanFlags.NeedLOSToPawns) == 0 && (flags & TargetScanFlags.NeedLOSToNonPawns) == 0 || Heights.CanSeeLeaning(s, th))
                        && CanWalkUnder(sp, th, ks, map))
                    {
                        float sn = Score(t, searcher, verb, s, th, d, map);
                        if (sn > bestNoHitScore)
                        {
                            bestNoHitScore = sn;
                            bestNoHit = t;
                        }
                    }
                    continue;
                }
                float sc = Score(t, searcher, verb, s, th, d, map);
                if (sc > bestScore)
                {
                    bestScore = sc;
                    best = t;
                }
            }
            candidates.Clear();
            if (best == null)
            {
                if (bestNoHit != null && __result == null)
                {
                    __result = bestNoHit;
                }
                return;
            }
            Thing r = __result?.Thing;
            if (r != null && r.Spawned && r.Map == map && !Heights.CrossFloor(s.Position, r.Position, map) && verb.CanHitTargetFrom(s.Position, r)
                && score(__result, searcher, verb) >= bestScore)
            {
                return;   // the game's pick on this floor scores at least as well
            }
            __result = best;
        }

        // The pawn can walk to (touch) the target's column on its own floor k.
        private static bool CanWalkUnder(Pawn p, Thing th, int k, Map map)
        {
            IntVec3 under = ProjectZ.CellOnFloor(ProjectZ.GroundCellOf(th.Position, map), k, map);
            return under.IsValid && under.InBounds(map) && map.reachability.CanReach(p.Position, under, PathEndMode.Touch, TraverseParms.For(p, Danger.Deadly));
        }

        // The game's target filters (AttackTargetFinder.BestAttackTarget's validator) that don't depend on distance.
        private static bool Valid(IAttackTarget t, Thing th, Thing s, Pawn sp, IAttackTargetSearcher searcher, TargetScanFlags flags, Predicate<Thing> validator,
            Verb verb, bool canBashDoors, bool canBashFences, Map map)
        {
            if (!s.HostileTo(th) || validator != null && !validator(th))
            {
                return false;
            }
            if (ignoreNoncombatant(s, t, flags))
            {
                return false;
            }
            if (sp != null)
            {
                Lord lord = sp.GetLord();
                if (lord != null && !lord.LordJob.ValidateAttackTarget(sp, th))
                {
                    return false;
                }
            }
            if ((flags & TargetScanFlags.NeedNotUnderThickRoof) != 0)
            {
                RoofDef roof = th.Position.GetRoof(map);
                if (roof != null && roof.isThickRoof)
                {
                    return false;
                }
            }
            if (((flags & TargetScanFlags.NeedThreat) != 0 || (flags & TargetScanFlags.NeedAutoTargetable) != 0) && t.ThreatDisabled(searcher))
            {
                return false;
            }
            if ((flags & TargetScanFlags.NeedAutoTargetable) != 0 && !AttackTargetFinder.IsAutoTargetable(t))
            {
                return false;
            }
            if ((flags & TargetScanFlags.NeedActiveThreat) != 0 && !GenHostility.IsActiveThreatTo(t, s.Faction))
            {
                return false;
            }
            if (verb.IsEMP() && th is Pawn tp && tp.RaceProps.IsFlesh)
            {
                return false;
            }
            if ((flags & TargetScanFlags.NeedNonBurning) != 0 && th.IsBurning())
            {
                return false;
            }
            if ((sp != null && sp.IsColonist || s.Faction == Faction.OfPlayer) && th.Position.Fogged(map))
            {
                return false;
            }
            if ((flags & TargetScanFlags.NeedReachable) != 0 && !canReach(s, th, canBashDoors, canBashFences))
            {
                return false;
            }
            return true;
        }

        // The game's shooting score with the real distance and the height cover put in place of the flat ones.
        private static float Score(IAttackTarget t, IAttackTargetSearcher searcher, Verb verb, Thing s, Thing th, float d, Map map)
        {
            float sc = score(t, searcher, verb);
            sc += Mathf.Min((th.Position - s.Position).LengthHorizontal, 40f) - Mathf.Min(d, 40f);
            sc += CoverUtility.CalculateOverallBlockChance(th.Position, s.Position, map) * 10f;
            HeightCover.For(th, s.Position, map, out float block);
            sc -= block * 10f;
            return sc;
        }
    }

    // A shooter that can already hit its target on another floor from where it stands stays there. Otherwise a spot on its
    // own floor within the range rule (CastSpots.OwnFloor); only then the game's search, which looks round the target's
    // cell, on the target's floor.
    [HarmonyPatch(typeof(CastPositionFinder), nameof(CastPositionFinder.TryFindCastPosition))]
    public static class CastPositionFinder_Patch
    {
        public static bool Prepare() => CombatGate.Enabled;

        public static bool Prefix(CastPositionRequest newReq, out IntVec3 dest, ref bool __result)
        {
            dest = IntVec3.Invalid;
            Pawn caster = newReq.caster;
            Thing target = newReq.target;
            if (caster == null || target == null || newReq.verb == null || !caster.Spawned || target.Map != caster.Map
                || !Heights.CrossFloor(caster.Position, target.Position, caster.Map))
            {
                return true;
            }
            if (newReq.verb.CanHitTargetFrom(caster.Position, target) && (newReq.validator == null || newReq.validator(caster.Position)))
            {
                dest = caster.Position;
                __result = true;
                return false;
            }
            if (CastSpots.OwnFloor(newReq, out dest))
            {
                __result = true;
                return false;
            }
            dest = IntVec3.Invalid;
            return true;
        }
    }

    // Projectiles between floors fly in the target's floor rows (so they hit and explode there), from the shooter's
    // ground cell moved onto that floor; their height runs from the shooter's eye to the target's middle. Along the way
    // they pass over anything lower than their height at that point (on the floor that height is on), and stop on a
    // slab or roof they would cross. A shot that would come down on open air (a miss off an upper floor's edge) falls to
    // the floor below, and one past a floor's north edge lands on its top row, never in the void between floors. Not
    // saved: a projectile still in flight on load flies on as a plain one.
    public static class Flights
    {
        public sealed class Flight
        {
            public float hs;
            public float ht;
            public float last;
        }

        public static readonly ConditionalWeakTable<Projectile, Flight> table = new ConditionalWeakTable<Projectile, Flight>();

        // Every projectile between floors (shells too, which have no Flight): from and to floor, for its drawn pitch
        // (ProjectilePitch).
        public sealed class Cross
        {
            public int from;
            public int to;
            public bool tiltKnown;
            public bool tilted;
            public Quaternion tilt;
        }

        public static readonly ConditionalWeakTable<Projectile, Cross> cross = new ConditionalWeakTable<Projectile, Cross>();

        public static Flight Of(Projectile p)
        {
            return table.TryGetValue(p, out Flight f) ? f : null;
        }

        // A thing's top over its floor, for projectiles passing over it.
        public static float ThingTop(Thing t)
        {
            if (t is Pawn p)
            {
                return p.GetPosture() == PawnPosture.Standing ? 1f : 0.2f;
            }
            return t.def.Fillage == FillCategory.Full ? Heights.Wall : t.def.fillPercent;
        }
    }

    [HarmonyPatch]
    public static class Projectile_Launch_Patch
    {
        public static bool Prepare() => CombatGate.Enabled;

        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Projectile), nameof(Projectile.Launch), new[]
            {
                typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef)
            });
        }

        public static void Postfix(Projectile __instance, ref Vector3 ___origin, ref Vector3 ___destination, ref int ___ticksToImpact, ref int ___lifetime)
        {
            Map map = __instance.Map;
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return;
            }
            IntVec3 oc = ___origin.ToIntVec3();
            IntVec3 dc = ___destination.ToIntVec3();
            int ko = ProjectZ.FloorOf(oc, map);
            int kd = ProjectZ.FloorOf(dc, map);
            if (ko < 0)
            {
                return;
            }
            bool moved = false;
            // A miss past the north edge of a floor lands on its top row, not in the void between floors.
            if (kd < 0 && dc.z >= a.groundZ)
            {
                kd = Mathf.Clamp(Levels.LevelOfRowLoose(map, dc.z), a.MinLevel, a.MaxLevel);
                ___destination.z = Mathf.Min(___destination.z, Levels.RowMax(map, kd) + 1 - 0.01f);
                dc = ___destination.ToIntVec3();
                moved = true;
            }
            // A shot that comes down on open air (a miss off the edge of an upper floor) falls to the floor below.
            int land = kd;
            IntVec3 g = ProjectZ.GroundCellOf(dc, map);
            while (land > 0 && g.IsValid && !Heights.Slab(ProjectZ.CellOnFloor(g, land, map), map))
            {
                land--;
            }
            if (land != kd)
            {
                ___destination.z += Levels.Shift(map, land) - Levels.Shift(map, kd);
                moved = true;
            }
            if (land != ko)
            {
                // IsoRim draws the shot leaving from the real shooter's muzzle (a manned mortar's barrel), not from
                // the moved launch point
                IsoRimBridge.SetLaunchPoint(__instance, ___origin);
                ___origin.z += Levels.Shift(map, land) - Levels.Shift(map, ko);
                moved = true;
            }
            if (!moved)
            {
                return;
            }
            float speed = __instance.def.projectile.SpeedTilesPerTick;
            int ticks = Mathf.Max(1, Mathf.CeilToInt((___origin - ___destination).magnitude / Mathf.Max(speed, 0.001f)));
            ___ticksToImpact = ticks;
            ___lifetime = ticks;
            IntVec3 at = ___origin.ToIntVec3();
            if (at.InBounds(map))
            {
                __instance.Position = at;
            }
            if (land == ko)
            {
                return;   // only clamped onto its own floor
            }
            Flights.cross.AddOrUpdate(__instance, new Flights.Cross { from = ko, to = land });
            if (!__instance.def.projectile.flyOverhead)
            {
                bool aimed = land == kd && (__instance.usedTarget.HasThing || __instance.intendedTarget.HasThing);
                float hs = ko * Heights.Storey + Heights.Eye;
                Flights.table.AddOrUpdate(__instance, new Flights.Flight
                {
                    hs = hs,
                    ht = land * Heights.Storey + (aimed ? Heights.Middle : 0.1f),
                    last = hs
                });
            }
            PZCombatStats.crossFloorShots++;
            if (PZCombatStats.shots.Count < 500)
            {
                PZCombatStats.shots.Add((__instance.Launcher?.ToString(), __instance.intendedTarget.ToString(), ko, land));
            }
        }
    }

    [HarmonyPatch(typeof(Projectile), "CheckForFreeIntercept")]
    public static class Projectile_Intercept_Patch
    {
        private static readonly Func<Projectile, Thing, bool> canHit = AccessTools.MethodDelegate<Func<Projectile, Thing, bool>>(AccessTools.Method(typeof(Projectile), "CanHit"));
        private static readonly Action<Projectile, Thing, bool> impact = AccessTools.MethodDelegate<Action<Projectile, Thing, bool>>(AccessTools.Method(typeof(Projectile), "Impact"));

        public static bool Prepare() => CombatGate.Enabled;

        public static bool Prefix(Projectile __instance, IntVec3 c, ref bool __result, Vector3 ___origin, Vector3 ___destination, Thing ___launcher, bool ___preventFriendlyFire)
        {
            Flights.Flight f = Flights.Of(__instance);
            if (f == null)
            {
                return true;
            }
            __result = Intercept(__instance, f, c, ___origin, ___destination, ___launcher, ___preventFriendlyFire);
            return false;
        }

        private static bool Intercept(Projectile p, Flights.Flight f, IntVec3 c, Vector3 origin, Vector3 destination, Thing launcher, bool preventFriendlyFire)
        {
            Map map = p.Map;
            AnnexMapComponent a = ProjectZ.Annex(map);
            IntVec3 dest = destination.ToIntVec3();
            if (a == null || dest == c)
            {
                return false;
            }
            IntVec3 g = ProjectZ.GroundCellOf(c, map);
            IntVec3 og = ProjectZ.GroundCellOf(origin.ToIntVec3(), map);
            IntVec3 dg = ProjectZ.GroundCellOf(dest, map);
            if (!g.IsValid || !og.IsValid || !dg.IsValid)
            {
                return false;
            }
            float h = Heights.HeightAt(og, dg, f.hs, f.ht, g);
            // A slab or roof crossed on the way into this cell stops the shot there.
            float lo = Mathf.Min(f.last, h);
            float hi = Mathf.Max(f.last, h);
            bool down = f.last > h;
            f.last = h;
            for (int k = a.MinLevel + 1; k <= a.MaxLevel + 1; k++)
            {
                float level = k * Heights.Storey;
                if (level >= lo && level <= hi && Heights.Plane(g, k, map))
                {
                    IntVec3 hitCell = ProjectZ.CellOnFloor(g, Mathf.Min(down ? k : k - 1, a.MaxLevel), map);
                    if (hitCell.IsValid)
                    {
                        p.Position = hitCell;
                    }
                    PZCombatStats.slabStops++;
                    impact(p, null, false);
                    return true;
                }
            }
            float chance = VerbUtility.InterceptChanceFactorFromDistance(origin, c);
            if (chance <= 0f)
            {
                return false;
            }
            int floor = Mathf.Clamp(Mathf.FloorToInt(h / Heights.Storey), a.MinLevel, a.MaxLevel);
            IntVec3 ck = ProjectZ.CellOnFloor(g, floor, map);
            if (!ck.IsValid || !ck.InBounds(map))
            {
                return false;
            }
            List<Thing> things = ck.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (!canHit(p, thing))
                {
                    continue;
                }
                if (h > floor * Heights.Storey + Flights.ThingTop(thing))
                {
                    PZCombatStats.passedOver++;
                    continue;   // flies over it
                }
                bool openDoor = false;
                if (thing.def.Fillage == FillCategory.Full)
                {
                    if (!(thing is Building_Door door && door.Open))
                    {
                        Hit(p, ck, thing);
                        return true;
                    }
                    openDoor = true;
                }
                float n = 0f;
                if (thing is Pawn pawn)
                {
                    n = 0.4f * Mathf.Clamp(pawn.BodySize, 0.1f, 2f);
                    if (pawn.GetPosture() != PawnPosture.Standing)
                    {
                        n *= 0.1f;
                    }
                    if (launcher != null && pawn.Faction != null && launcher.Faction != null && !pawn.Faction.HostileTo(launcher.Faction))
                    {
                        n = preventFriendlyFire ? 0f : n * Find.Storyteller.difficulty.friendlyFireChanceFactor;
                    }
                }
                else if (thing.def.fillPercent > 0.2f)
                {
                    n = openDoor ? 0.05f : (!dest.AdjacentTo8Way(c) ? thing.def.fillPercent * 0.15f : thing.def.fillPercent);
                }
                n *= chance;
                if (n > 1E-05f && Rand.Chance(n))
                {
                    Hit(p, ck, thing);
                    return true;
                }
            }
            return false;
        }

        private static void Hit(Projectile p, IntVec3 cell, Thing thing)
        {
            if (p.Position != cell)
            {
                p.Position = cell;
            }
            PZCombatStats.intercepts++;
            impact(p, thing, false);
        }
    }

    // Counters for the tests (cheap, always on).
    public static class PZCombatStats
    {
        public static int crossFloorShots;
        public static int intercepts;
        public static int passedOver;
        public static int slabStops;
        public static readonly List<(string launcher, string target, int fromFloor, int toFloor)> shots = new List<(string, string, int, int)>();
    }
}
