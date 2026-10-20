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

    // The one line-of-sight check behind every shot: AI target picking, CanHitTarget, TryFindShootLineFromTo (targeting,
    // warm-up, cast spots, the shot report). Between floors Los3D (with the game's corner walks for full-fill targets),
    // none for a shot declared lobbed (Lobbed); on one floor the game's own code. Final say between floors (FinalSay).
    [HarmonyPatch(typeof(Verb), "CanHitCellFromCellIgnoringRange")]
    public static class Verb_CanHitCell_Patch
    {
        public static bool Prepare() => CombatGate.Enabled;

        public static byte Decide(Verb verb, IntVec3 from, IntVec3 to, bool corners)
        {
            Map map = verb.caster?.Map;
            if (map == null || !Heights.CrossFloor(from, to, map))
            {
                return FinalSay.Pass;
            }
            VerbProperties vp = verb.verbProps;
            if (vp.mustCastOnOpenGround && (!to.Standable(map) || map.thingGrid.CellContains(to, ThingCategory.Pawn)))
            {
                return FinalSay.No;
            }
            // declared lobbed: no line needed; declared direct: a line needed; nothing declared: the verb's own rule
            bool? lobbed = Lobbed.Declared(verb, from, to);
            if (lobbed == true || lobbed == null && !vp.requireLineOfSight)
            {
                return FinalSay.Yes;
            }
            return FinalSay.Of(Los3D.CanSee(from, to, map, corners));
        }

        public static bool Prefix(Verb __instance, IntVec3 __0, IntVec3 __1, bool __2, ref bool __result, out byte __state)
        {
            __state = Decide(__instance, __0, __1, __2);
            if (__state == FinalSay.Pass)
            {
                return true;
            }
            __result = __state == FinalSay.Yes;
            return false;
        }

        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Verb __instance, IntVec3 __0, IntVec3 __1, bool __2, ref bool __result, byte __state)
        {
            if (__state == FinalSay.NotRun)
            {
                FinalSay.PrefixSkipped++;
                __state = Decide(__instance, __0, __1, __2);
            }
            FinalSay.Bool(__state, ref __result);
        }
    }

    // Leaning out around a wall corner to shoot (the game's ShootLeanUtility, behind TryFindShootLineFromTo, the target's
    // own lean cells and AttackTargetFinder.CanSee). The game picks the lean cells from the direction to the target in
    // map rows, where another floor is a whole band of rows away (always "north" or "south"); between floors it gets
    // the direction over the ground instead (the target's column on the shooter's floor). Lean cells off the shooter's
    // floor (the void rows past a band's edge) are dropped. Each lean cell then goes through Los3D (Verb_CanHitCell_Patch,
    // Heights.CanSeeLeaning).
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

        [HarmonyPriority(Priority.Last)]
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

        private static byte Decide(Verb verb, IntVec3 root, LocalTargetInfo targ, CellRect occupiedRect)
        {
            Map map = verb.caster?.Map;
            if (map == null || !targ.IsValid || !Heights.CrossFloor(root, targ.Cell, map))
            {
                return FinalSay.Pass;
            }
            float min = verb.verbProps.EffectiveMinRange(targ, verb.caster);
            return FinalSay.Of(Range3D.OutOfRange(root, occupiedRect, targ.Cell, verb.EffectiveRange, min, map));
        }

        public static bool Prefix(Verb __instance, IntVec3 root, LocalTargetInfo targ, CellRect occupiedRect, ref bool __result, out byte __state)
        {
            __state = Decide(__instance, root, targ, occupiedRect);
            if (__state == FinalSay.Pass)
            {
                return true;
            }
            __result = __state == FinalSay.Yes;
            return false;
        }

        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Verb __instance, IntVec3 root, LocalTargetInfo targ, CellRect occupiedRect, ref bool __result, byte __state)
        {
            if (__state == FinalSay.NotRun)
            {
                __state = Decide(__instance, root, targ, occupiedRect);
            }
            FinalSay.Bool(__state, ref __result);
        }
    }

    // The hit report between floors: distance over the ground, the height accuracy bonus when shooting down, cover by
    // height, blind smoke on the cells the line is in. Verb_LaunchProjectile.TryCastShot uses this report, so the tooltip
    // and the real shot agree. Last of all postfixes: between floors these fields are ProjectZ's (FinalSay).
    [HarmonyPatch(typeof(ShotReport), nameof(ShotReport.HitReportFor))]
    public static class ShotReport_Patch
    {
        private static readonly AccessTools.StructFieldRef<ShotReport, float> distance = AccessTools.StructFieldRefAccess<ShotReport, float>("distance");
        private static readonly AccessTools.StructFieldRef<ShotReport, float> shooterFactor = AccessTools.StructFieldRefAccess<ShotReport, float>("factorFromShooterAndDist");
        private static readonly AccessTools.StructFieldRef<ShotReport, float> equipmentFactor = AccessTools.StructFieldRefAccess<ShotReport, float>("factorFromEquipment");
        private static readonly AccessTools.StructFieldRef<ShotReport, List<CoverInfo>> covers = AccessTools.StructFieldRefAccess<ShotReport, List<CoverInfo>>("covers");
        private static readonly AccessTools.StructFieldRef<ShotReport, float> coversBlock = AccessTools.StructFieldRefAccess<ShotReport, float>("coversOverallBlockChance");
        private static readonly AccessTools.StructFieldRef<ShotReport, float> gasFactor = AccessTools.StructFieldRefAccess<ShotReport, float>("factorFromCoveringGas");
        private static readonly AccessTools.StructFieldRef<ShotReport, ShootLine> line = AccessTools.StructFieldRefAccess<ShotReport, ShootLine>("shootLine");

        public static bool Prepare() => CombatGate.Enabled;

        [HarmonyPriority(Priority.Last)]
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
            gasFactor(ref __result) = SmokeFactor(verb, line(ref __result), map);
            if (target.HasThing && !target.Thing.def.CanBenefitFromCover)
            {
                covers(ref __result) = new List<CoverInfo>();
                coversBlock(ref __result) = 0f;
                return;
            }
            covers(ref __result) = HeightCover.For(target, caster.Position, map, out float block);
            coversBlock(ref __result) = block;
        }

        private static readonly List<IntVec3> lineCells = new List<IntVec3>();

        // The game's blind smoke factor (0.7 when smoke is on the shoot line), with the cells the line is in between
        // floors (the game's own walk would cross the map rows between the two bands).
        private static float SmokeFactor(Verb verb, ShootLine sl, Map map)
        {
            if (!sl.Source.IsValid || !sl.Dest.IsValid || verb.EquipmentSource != null && verb.EquipmentSource.TryGetComp<CompUniqueWeapon>(out CompUniqueWeapon u) && u.IgnoreAccuracyMaluses)
            {
                return 1f;
            }
            if (!Los3D.CanSee(sl.Source, sl.Dest, map, out float aim))
            {
                aim = Los3D.AimChest;
            }
            Los3D.LineCells(sl.Source, sl.Dest, map, aim, lineCells);
            try
            {
                for (int i = 0; i < lineCells.Count; i++)
                {
                    if (lineCells[i].InBounds(map) && lineCells[i].AnyGas(map, GasType.BlindSmoke))
                    {
                        return 0.7f;
                    }
                }
                return 1f;
            }
            finally
            {
                lineCells.Clear();
            }
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

    // Seeing a target on another floor (target finder validators): Los3D with the game's leaning and its validator
    // (blind smoke for turrets). A seer whose shot at the target is declared lobbed needs no line (Lobbed.Sees).
    // Final say between floors (FinalSay).
    [HarmonyPatch(typeof(AttackTargetFinder), nameof(AttackTargetFinder.CanSee))]
    public static class AttackTargetFinder_CanSee_Patch
    {
        public static bool Prepare() => CombatGate.Enabled;

        private static byte Decide(Thing seer, Thing target, Func<IntVec3, bool> validator)
        {
            Map map = seer?.Map;
            if (map == null || target == null || target.Map != map || !Heights.CrossFloor(seer.Position, target.Position, map))
            {
                return FinalSay.Pass;
            }
            return FinalSay.Of(Sight.Sees(seer, target, (seer as IAttackTargetSearcher)?.CurrentEffectiveVerb, validator));
        }

        public static bool Prefix(Thing seer, Thing target, Func<IntVec3, bool> validator, ref bool __result, out byte __state)
        {
            __state = Decide(seer, target, validator);
            if (__state == FinalSay.Pass)
            {
                return true;
            }
            __result = __state == FinalSay.Yes;
            return false;
        }

        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Thing seer, Thing target, Func<IntVec3, bool> validator, ref bool __result, byte __state)
        {
            if (__state == FinalSay.NotRun)
            {
                __state = Decide(seer, target, validator);
            }
            FinalSay.Bool(__state, ref __result);
        }
    }

    // Seeing a thing on another floor to shoot it: a shot declared lobbed (Lobbed) needs no line of sight; else the
    // game's leaning sight with Los3D (Heights.CanSeeLeaning).
    public static class Sight
    {
        public static bool Sees(Thing seer, Thing target, Verb verb, Func<IntVec3, bool> validator)
        {
            if (verb != null && Lobbed.Any && Lobbed.IsLobbed(verb, seer.Position, target.Position))
            {
                return true;
            }
            return Heights.CanSeeLeaning(seer, target, validator);
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

        [HarmonyPriority(Priority.Last)]
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
            // the game's blind-smoke rule for sight (LOSBlockableByGas: turrets)
            Func<IntVec3, bool> gas = null;
            if ((flags & TargetScanFlags.LOSBlockableByGas) != 0 && !(verb.EquipmentSource != null && verb.EquipmentSource.TryGetComp<CompUniqueWeapon>(out CompUniqueWeapon uw) && uw.IgnoreAccuracyMaluses))
            {
                gas = c => !c.AnyGas(map, GasType.BlindSmoke);
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
                if (!Valid(t, th, s, sp, searcher, flags, validator, verb, canBashDoors, canBashFences, gas, map))
                {
                    continue;
                }
                if (!verb.CanHitTargetFrom(s.Position, th))
                {
                    // The game's NeedReachableIfCantHitFromMyPos between floors: a target this pawn sees but can't hit
                    // from here counts when it can walk under it (to the target's column on its own floor), where the
                    // cast position search (CastSpots) finds it a spot within range. Only when the game found nothing.
                    if (__result == null && sp != null && (flags & TargetScanFlags.NeedReachableIfCantHitFromMyPos) != 0
                        && ((flags & TargetScanFlags.NeedLOSToPawns) == 0 && (flags & TargetScanFlags.NeedLOSToNonPawns) == 0 || Sight.Sees(s, th, verb, gas))
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
            Verb verb, bool canBashDoors, bool canBashFences, Func<IntVec3, bool> gas, Map map)
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
            if (gas != null && (flags & TargetScanFlags.NeedLOSToAll) != 0)
            {
                if (!gas(s.Position) || !gas(th.Position))
                {
                    return false;
                }
                if (!Sight.Sees(s, th, verb, gas) && (th is Pawn ? (flags & TargetScanFlags.NeedLOSToPawns) != 0 : (flags & TargetScanFlags.NeedLOSToNonPawns) != 0))
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

        // The spot ProjectZ finds between floors; Invalid: none (the game's search runs).
        private static IntVec3 Decide(CastPositionRequest newReq)
        {
            Pawn caster = newReq.caster;
            Thing target = newReq.target;
            if (caster == null || target == null || newReq.verb == null || !caster.Spawned || target.Map != caster.Map
                || !Heights.CrossFloor(caster.Position, target.Position, caster.Map))
            {
                return IntVec3.Invalid;
            }
            if (newReq.verb.CanHitTargetFrom(caster.Position, target) && (newReq.validator == null || newReq.validator(caster.Position)))
            {
                return caster.Position;
            }
            return CastSpots.OwnFloor(newReq, out IntVec3 dest) ? dest : IntVec3.Invalid;
        }

        // __state: ran = the prefix ran; spot = the spot it found (Invalid: none).
        public static bool Prefix(CastPositionRequest newReq, out IntVec3 dest, ref bool __result, out (bool ran, IntVec3 spot) __state)
        {
            dest = Decide(newReq);
            __state = (true, dest);
            if (!dest.IsValid)
            {
                return true;
            }
            __result = true;
            return false;
        }

        // Final say between floors (FinalSay): the spot found here stands (found here when another mod's prefix skipped ours).
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(CastPositionRequest newReq, ref IntVec3 dest, ref bool __result, (bool ran, IntVec3 spot) __state)
        {
            if (!__state.ran)
            {
                FinalSay.PrefixSkipped++;
                __state = (true, Decide(newReq));
            }
            if (!__state.spot.IsValid)
            {
                return;
            }
            bool r = __result;
            FinalSay.Value(true, true, ref r);
            __result = r;
            FinalSay.Value(true, __state.spot, ref dest);
        }
    }

    // Projectiles between floors fly in the target's floor rows (so they hit and explode there), from the shooter's
    // ground cell moved onto that floor; their height runs from the shooter's eye (Los3D.Eye) to the clear aim height of
    // its line of sight (aimed shots) or near the ground (misses). Along the way they pass over anything lower than their
    // height at that point (on the floor that height is on). A miss stops on a slab or roof it would cross; an aimed shot
    // with a clear line never does (Los3D found no plane on it). A shot that would come down on open air (a miss off an upper floor's edge) falls to
    // the floor below, and one past a floor's north edge lands on its top row, never in the void between floors. Not
    // saved: a projectile still in flight on load flies on as a plain one.
    public static class Flights
    {
        public sealed class Flight
        {
            public float hs;
            public float ht;
            public float last;
            public bool clear;   // aimed along a clear line of sight
        }

        private static readonly List<IntVec3> dests = new List<IntVec3>();

        // A clear line of sight from the shoot line's source to the target's cell, or to one of its lean cells (the game's
        // shoot line may end on one); aim = its clear aim height (cells over the target's floor).
        public static bool ClearLine(IntVec3 source, IntVec3 dest, LocalTargetInfo target, Map map, out float aim)
        {
            bool corners = target.HasThing && target.Thing.def.Fillage == FillCategory.Full;
            if (Los3D.CanSee(source, dest, map, out aim, corners))
            {
                return true;
            }
            if (!target.HasThing || !target.Thing.Spawned)
            {
                return false;
            }
            try
            {
                ShootLeanUtility.CalcShootableCellsOf(dests, target.Thing, source);
                for (int i = 0; i < dests.Count; i++)
                {
                    if (dests[i] != dest && Los3D.CanSee(source, dests[i], map, out aim, corners))
                    {
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                dests.Clear();
            }
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
            IntVec3 spawn = __instance.Position;   // the shoot line's source
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
            while (land > 0 && g.IsValid && !Column.FloorAt(g, land, map))
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
            // Lobbed (from the sky, no flight) or direct (a flight): as declared (Api.SetLobbed, else the casting verb's
            // rule, Lobbed), else as its def flies (flyOverhead).
            if (!Lobbed.Marked(__instance, out bool lobbed))
            {
                lobbed = __instance.def.projectile.flyOverhead;
                Verb v = Lobbed.casting;
                if (Lobbed.Any && v != null)
                {
                    IntVec3 to = __instance.intendedTarget.IsValid ? __instance.intendedTarget.Cell : dc;
                    bool? declared = Heights.CrossFloor(spawn, to, map) ? Lobbed.Declared(v, spawn, to) : null;
                    if (declared.HasValue)
                    {
                        lobbed = declared.Value;
                        Lobbed.Mark(__instance, lobbed);
                    }
                }
            }
            if (!lobbed)
            {
                bool aimed = land == kd && (__instance.usedTarget.HasThing || __instance.intendedTarget.HasThing);
                // An aimed shot along a clear line of sight (Los3D from the shoot line's source, the spawn cell) flies at
                // its heights (eye to the clear aim height) and never stops on a slab or roof: the line crossed none.
                float aim = Los3D.AimChest;
                bool clear = aimed && Flights.ClearLine(spawn, dc, __instance.usedTarget, map, out aim);
                float wall = IsoRimBridge.WallHeight;
                float hs = ko * Heights.Storey + Los3D.Eye / wall;
                Flights.table.AddOrUpdate(__instance, new Flights.Flight
                {
                    hs = hs,
                    ht = land * Heights.Storey + (aimed ? (clear ? aim : Los3D.AimChest) / wall : 0.1f),
                    last = hs,
                    clear = clear
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

        // A direct shot between floors: its flight decides. A lobbed one between floors (declared, Lobbed; a def flying
        // overhead never gets here with a hit) is never intercepted on the way.
        private static byte Decide(Projectile p, IntVec3 c, Vector3 origin, Vector3 destination, Thing launcher, bool preventFriendlyFire)
        {
            Flights.Flight f = Flights.Of(p);
            if (f == null)
            {
                return Lobbed.Marked(p, out bool lobbed) && lobbed && Flights.cross.TryGetValue(p, out _) ? FinalSay.No : FinalSay.Pass;
            }
            return FinalSay.Of(Intercept(p, f, c, origin, destination, launcher, preventFriendlyFire));
        }

        public static bool Prefix(Projectile __instance, IntVec3 c, ref bool __result, Vector3 ___origin, Vector3 ___destination, Thing ___launcher, bool ___preventFriendlyFire, out byte __state)
        {
            __state = Decide(__instance, c, ___origin, ___destination, ___launcher, ___preventFriendlyFire);
            if (__state == FinalSay.Pass)
            {
                return true;
            }
            __result = __state == FinalSay.Yes;
            return false;
        }

        // Final say between floors (FinalSay). When another mod's prefix skipped ours (and the game's check), decided here
        // (the flight's interception impacts here then), unless that mod already ended the projectile.
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Projectile __instance, IntVec3 c, ref bool __result, Vector3 ___origin, Vector3 ___destination, Thing ___launcher, bool ___preventFriendlyFire, byte __state)
        {
            if (__state == FinalSay.NotRun)
            {
                if (!__instance.Spawned || __instance.Destroyed)
                {
                    return;
                }
                FinalSay.PrefixSkipped++;
                __state = Decide(__instance, c, ___origin, ___destination, ___launcher, ___preventFriendlyFire);
            }
            FinalSay.Bool(__state, ref __result);
        }

        private static readonly AccessTools.FieldRef<Projectile, ProjectileHitFlags> desiredFlags = AccessTools.FieldRefAccess<Projectile, ProjectileHitFlags>("desiredHitFlags");

        // The game's Projectile.CanHit; for a projectile def flying overhead that was declared direct (Lobbed) with the
        // hit flags it was launched with (the game gives a shell flying overhead none).
        private static bool CanHit(Projectile p, Thing thing)
        {
            if (!p.def.projectile.flyOverhead || p.def.projectile.alwaysFreeIntercept)
            {
                return canHit(p, thing);
            }
            if (!thing.Spawned || thing == p.Launcher || thing.Map != p.Map || CoverUtility.ThingCovered(thing, p.Map))
            {
                return false;
            }
            ProjectileHitFlags flags = desiredFlags(p);
            if (flags == ProjectileHitFlags.None)
            {
                return false;
            }
            if (thing == p.intendedTarget.Thing)
            {
                return (flags & ProjectileHitFlags.IntendedTarget) != 0 || thing.def.Fillage == FillCategory.Full;
            }
            return thing is Pawn ? (flags & ProjectileHitFlags.NonTargetPawns) != 0 : (flags & ProjectileHitFlags.NonTargetWorld) != 0;
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
            for (int k = a.MinLevel + 1; k <= a.MaxLevel + 1 && !f.clear; k++)
            {
                float level = k * Heights.Storey;
                if (level >= lo && level <= hi && Column.PlaneSolid(g, k, map))
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
                if (!CanHit(p, thing))
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
