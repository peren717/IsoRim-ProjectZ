using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Combat
{
    // The other range decisions between floors (Range3D is the rule; Verb.OutOfRange and the target finder are in
    // CombatPatches.cs).

    // AI shooting positions against a target on another floor, on the shooter's own floor: the game's search
    // (CastPositionFinder) looks round the target's cell, i.e. on the target's floor, so it never offers the ground
    // under a floor's edge to a raider below. Cells round the target's column on the shooter's floor out to the range
    // rule's reach there; the game's limits (validator, ranges from the caster and the locus, region count, allowed
    // area, reachability, known danger, reservations) and its preference (cover from the target, near the caster, the
    // avoid grid) in short; the best spot that can hit the target (Verb.CanHitTargetFrom: sight and the range rule).
    public static class CastSpots
    {
        private const int MaxHitChecks = 250;   // the expensive tests (reach, sight), best spots first

        private static readonly List<KeyValuePair<float, IntVec3>> spots = new List<KeyValuePair<float, IntVec3>>();

        public static bool OwnFloor(CastPositionRequest req, out IntVec3 dest)
        {
            dest = IntVec3.Invalid;
            try
            {
                return Find(req, out dest);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Finding a shooting spot for a target on another floor failed: " + e, 61190311);
                return false;
            }
        }

        private static bool Find(CastPositionRequest req, out IntVec3 dest)
        {
            dest = IntVec3.Invalid;
            Pawn caster = req.caster;
            Map map = caster.Map;
            Verb verb = req.verb;
            if (verb.verbProps.IsMeleeAttack || verb.EffectiveRange <= 1.42f)
            {
                return false;
            }
            int ks = ProjectZ.FloorOf(caster.Position, map);
            int kt = ProjectZ.FloorOf(req.target.Position, map);
            IntVec3 tg = ProjectZ.GroundCellOf(req.target.Position, map);
            IntVec3 centre = ProjectZ.CellOnFloor(tg, ks, map);
            if (ks < 0 || kt < 0 || !centre.IsValid)
            {
                return false;
            }
            float reach = Range3D.HorizontalReach(verb.EffectiveRange, ks, kt);
            if (req.maxRangeFromTarget > 0.01f && req.maxRangeFromTarget < 500f)
            {
                reach = Mathf.Min(reach, Range3D.HorizontalReach(req.maxRangeFromTarget, ks, kt));
            }
            if (reach < 1f)
            {
                return false;
            }
            reach = Mathf.Min(reach, GenRadial.MaxRadialPatternRadius);
            int mark = 0;
            if (req.maxRegions > 0)
            {
                Region region = caster.Position.GetRegion(map);
                if (region == null)
                {
                    return false;
                }
                mark = Rand.Int;
                RegionTraverser.MarkRegionsBFS(region, null, req.maxRegions, mark);
            }
            AvoidGrid avoid = null;
            if (caster.TryGetAvoidGrid(out AvoidGrid grid, onlyIfLordAllows: false) && grid.Grid.Length == map.cellIndices.NumGridCells)
            {
                avoid = grid;   // (reading Grid regenerates it when dirty)
            }
            Vector3 locusV = req.maxRangeFromLocus > 0.01f && req.locus.IsValid ? ProjectZ.Virtual(req.locus, map) : Vector3.zero;
            spots.Clear();
            int n = GenRadial.NumCellsInRadius(reach);
            for (int i = 0; i < n; i++)
            {
                IntVec3 c = centre + GenRadial.RadialPattern[i];
                if (!c.InBounds(map) || ProjectZ.FloorOf(c, map) != ks)
                {
                    continue;
                }
                if (req.maxRangeFromCaster > 0.01f && (c - caster.Position).LengthHorizontalSquared > req.maxRangeFromCaster * req.maxRangeFromCaster)
                {
                    continue;
                }
                if (req.maxRangeFromLocus > 0.01f && req.locus.IsValid && ProjectZ.VirtualFlatDistance(locusV, c.ToVector3Shifted(), map) > req.maxRangeFromLocus)
                {
                    continue;
                }
                if (req.validator != null && !req.validator(c))
                {
                    continue;
                }
                if (!c.WalkableBy(map, caster) || c != caster.Position && !c.InAllowedArea(caster))
                {
                    continue;
                }
                if (mark != 0 && c.GetRegion(map)?.mark != mark)
                {
                    continue;
                }
                float pref = 1f;
                if (req.wantCoverFromTarget)
                {
                    // the cover c has against the target's fire, by height (sandbags help against a floor above only
                    // where the line passes below their top)
                    HeightCover.For(new LocalTargetInfo(c), req.target.Position, map, out float block);
                    pref += block * 0.55f;
                }
                pref *= Mathf.Pow(0.967f, (caster.Position - c).LengthHorizontal);
                if (avoid != null)
                {
                    pref *= Mathf.Max(0.1f, (37.5f - avoid[c]) / 37.5f);
                }
                spots.Add(new KeyValuePair<float, IntVec3>(pref, c));
            }
            spots.Sort((a, b) => b.Key.CompareTo(a.Key));
            int checks = 0;
            for (int i = 0; i < spots.Count && checks < MaxHitChecks; i++)
            {
                IntVec3 c = spots[i].Value;
                if (!map.reachability.CanReach(caster.Position, c, PathEndMode.OnCell, TraverseParms.For(caster, Danger.Some)))
                {
                    continue;
                }
                checks++;
                if (!verb.CanHitTargetFrom(c, req.target) || !map.pawnDestinationReservationManager.CanReserve(c, caster) || PawnUtility.KnownDangerAt(c, map, caster))
                {
                    continue;
                }
                dest = c;
                break;
            }
            spots.Clear();
            if (dest.IsValid)
            {
                ProjectZ.DebugLog($"cast spot for {caster} at {caster.Position} (floor {ks + 1}) -> {req.target} (floor {kt + 1}): {dest}, 3D distance {Range3D.Distance(dest, req.target.Position, map):0.0}, range {verb.EffectiveRange:0.0} + {Range3D.Reach(verb.EffectiveRange, ks, kt) - verb.EffectiveRange:0}");
            }
            return dest.IsValid;
        }

    }

    // A turret ordered to attack a target on another floor (player turrets, mortars): the range rule, the game's messages.
    [HarmonyPatch(typeof(Building_TurretGun), nameof(Building_TurretGun.OrderAttack))]
    [PatchGroup(FailSoft.Combat)]
    public static class TurretGun_OrderAttack_Patch
    {
        private static readonly AccessTools.FieldRef<Building_Turret, LocalTargetInfo> forced = AccessTools.FieldRefAccess<Building_Turret, LocalTargetInfo>("forcedTarget");
        private static readonly AccessTools.FieldRef<Building_TurretGun, int> cooldown = AccessTools.FieldRefAccess<Building_TurretGun, int>("burstCooldownTicksLeft");
        private static readonly AccessTools.FieldRef<Building_TurretGun, bool> holdFire = AccessTools.FieldRefAccess<Building_TurretGun, bool>("holdFire");

        public static bool Prepare() => CombatGate.Enabled;

        public static bool Prefix(Building_TurretGun __instance, LocalTargetInfo targ)
        {
            Map map = __instance.Map;
            if (!targ.IsValid || map == null || !Heights.CrossFloor(__instance.Position, targ.Cell, map))
            {
                return true;
            }
            Verb v = __instance.AttackVerb;
            float d = Range3D.Distance(__instance.Position, targ.Cell, map);
            if (d < v.verbProps.EffectiveMinRange(targ, __instance))
            {
                Messages.Message("MessageTargetBelowMinimumRange".Translate(), __instance, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            if (Range3D.RangeDistance(__instance.Position, targ.Cell, map) > v.EffectiveRange)
            {
                Messages.Message("MessageTargetBeyondMaximumRange".Translate(), __instance, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            if (forced(__instance) != targ)
            {
                forced(__instance) = targ;
                if (cooldown(__instance) <= 0)
                {
                    __instance.TryStartShootSomething(canBeginBurstImmediately: false);
                }
            }
            if (holdFire(__instance))
            {
                Messages.Message("MessageTurretWontFireBecauseHoldFire".Translate(__instance.def.label), __instance, MessageTypeDefOf.RejectInput, historical: false);
            }
            return false;
        }
    }

    // The reason a drafted pawn can't shoot. Between floors the game tells range from sight by the flat distance
    // (InHorDistOf), which is a whole floor's rows away: the range rule's reason instead. For every shot that is in range
    // but has no line of sight (the game's own result on one floor, Los3D's between floors): "no line of sight".
    [HarmonyPatch(typeof(FloatMenuUtility), nameof(FloatMenuUtility.GetRangedAttackAction))]
    [PatchGroup(FailSoft.Combat)]
    public static class FloatMenu_RangedReason_Patch
    {
        public static bool Prepare() => CombatGate.Enabled;

        public static void Postfix(Pawn pawn, LocalTargetInfo target, ref string failStr)
        {
            if (string.IsNullOrEmpty(failStr) || pawn?.Map == null || !target.IsValid)
            {
                return;
            }
            Verb v = pawn.equipment?.PrimaryEq?.PrimaryVerb;
            if (v == null)
            {
                return;
            }
            // the game's reasons as it writes them (1.7 capitalizes the keyed text: "Out of range"); compared ignoring case
            string outOfRange = "OutOfRange".Translate().CapitalizeFirst();
            string tooClose = "TooClose".Translate().CapitalizeFirst();
            string cannotHit = "CannotHitTarget".Translate().CapitalizeFirst();
            Map map = pawn.Map;
            if (Heights.CrossFloor(pawn.Position, target.Cell, map) && (Same(failStr, outOfRange) || Same(failStr, tooClose)))
            {
                if (Range3D.RangeDistance(pawn.Position, target.Cell, map) > v.EffectiveRange)
                {
                    failStr = outOfRange;
                }
                else if (Range3D.Distance(pawn.Position, target.Cell, map) < v.verbProps.EffectiveMinRange(target, pawn))
                {
                    failStr = tooClose;
                }
                else
                {
                    failStr = cannotHit;
                }
            }
            if (Same(failStr, cannotHit) && NoSight.Blocked(v, target))
            {
                failStr = "PZ_CannotHitNoLineOfSight".Translate();
            }
        }

        private static bool Same(string a, string b) => string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);
    }

    // The AI's avoid grid round a turret (raiders keep out of its fire): the game marks only the turret's own floor.
    // The cells of the other floors it reaches by the range rule and sees, too.
    [HarmonyPatch(typeof(AvoidGrid), "PrintAvoidGridAroundTurret")]
    [PatchGroup(FailSoft.Combat)]
    public static class AvoidGrid_Turret_Patch
    {
        private static readonly Action<AvoidGrid, IntVec3, int> increment =
            AccessTools.MethodDelegate<Action<AvoidGrid, IntVec3, int>>(AccessTools.Method(typeof(AvoidGrid), "IncrementAvoidGrid"));

        public static bool Prepare() => CombatGate.Enabled;

        public static void Postfix(AvoidGrid __instance, Building_TurretGun tur, bool __runOriginal)
        {
            Map map = tur?.Map;
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || !__runOriginal)   // another mod left this turret's avoid grid out: none upstairs either
            {
                return;
            }
            try
            {
                Verb v = tur.GunCompEq?.PrimaryVerb;
                if (v == null)
                {
                    return;
                }
                int ks = ProjectZ.FloorOf(tur.Position, map);
                IntVec3 g = ProjectZ.GroundCellOf(tur.Position, map);
                if (ks < 0 || !g.IsValid)
                {
                    return;
                }
                float range = v.verbProps.range;
                float min = v.verbProps.EffectiveMinRange(allowAdjacentShot: true);
                for (int k = a.MinLevel; k <= a.MaxLevel; k++)
                {
                    if (k == ks)
                    {
                        continue;
                    }
                    float reach = Range3D.HorizontalReach(range + 4f, ks, k);
                    if (reach < 1f)
                    {
                        continue;
                    }
                    IntVec3 centre = ProjectZ.CellOnFloor(g, k, map);
                    int n = GenRadial.NumCellsInRadius(Mathf.Min(reach, GenRadial.MaxRadialPatternRadius));
                    for (int i = 0; i < n; i++)
                    {
                        IntVec3 c = centre + GenRadial.RadialPattern[i];
                        if (!c.InBounds(map) || ProjectZ.FloorOf(c, map) != k || !c.WalkableByNormal(map))
                        {
                            continue;
                        }
                        if (min >= 1f && Range3D.Distance(tur.Position, c, map) < min)
                        {
                            continue;
                        }
                        if (Los3D.CanSee(tur.Position, c, map))
                        {
                            increment(__instance, c, 45);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Marking a turret's fire on other floors failed: " + e, 61190312);
            }
        }
    }
}
