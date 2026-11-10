using System;
using System.Collections.Generic;
using System.Reflection;
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
            finally
            {
                PrefEnd();
            }
        }

        private static bool Find(CastPositionRequest req, out IntVec3 dest)
        {
            dest = IntVec3.Invalid;
            Pawn caster = req.caster;
            Map map = caster.Map;
            Verb verb = req.verb;
            if (GameRange.MeleeReach(verb))
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
            float reach = GameRange.HorizontalReach(GameRange.Weapon, verb, ks, kt);
            if (req.maxRangeFromTarget > 0.01f && req.maxRangeFromTarget < 500f)
            {
                reach = Mathf.Min(reach, GameRange.HorizontalReach(req.maxRangeFromTarget, GameRange.Weapon.sphere, ks, kt));
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
            PrefBegin(req, map);
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
                float pref = Preference(c, avoid);
                if (pref >= 0.001f)   // (the game's least: EvaluateCell keeps a spot from 0.001 up)
                {
                    spots.Add(new KeyValuePair<float, IntVec3>(pref, c));
                }
            }
            PrefEnd();
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

        // ---- the game's preference for a shooting spot (CastPositionFinder), asked with ProjectZ's 3D inputs
        //
        // A private copy of the game's own CastPositionPreference(c) is asked (Scope3DCore.ReverseCopy, a snapshot made at
        // first use: other mods' changes to its body come along; the live method is not patched). Its private search state
        // is set as TryFindCastPosition and EvaluateCell set it, with the distances between the caster, the target and the
        // cell in 3D (Range3D); in the copy, the two floor-blind measures are swapped (CallSwap): the cover against the
        // target (by height between floors, HeightCover) and the target-to-cell distance (3D between floors); on one floor
        // both are the game's own. Its weights and limits stay the game's and other mods'. The game's own search state is
        // saved and put back round every search (PrefBegin, PrefEnd). Not the 3D scope's table: the distance is a vector's
        // length (IntVec3.LengthHorizontalSquared, no cell pair to answer) and the cover is ProjectZ's height rule.
        // Without the copy, the game's live method (its flat measures between floors), never a copy of it.
        // Copies (no public method to ask; watched with IsoRim's WatchCopy, CopyWatches; parity with the game's own on one
        // floor: test mode gamerange, G3):
        //   - the state's optimal range, from TryFindCastPosition: EffectiveRange x 0.8 x range x 0.8;
        //   - the avoid grid's factor, from EvaluateCell: at least 0.1 of (37.5 - the mark) / 37.5.

        private static readonly Type Finder = typeof(CastPositionFinder);
        private static readonly AccessTools.FieldRef<CastPositionRequest> stReq = AccessTools.StaticFieldRefAccess<CastPositionRequest>(AccessTools.Field(Finder, "req"));
        private static readonly AccessTools.FieldRef<float> stRangeFromTarget = AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(Finder, "rangeFromTarget"));
        private static readonly AccessTools.FieldRef<float> stRangeFromTargetSq = AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(Finder, "rangeFromTargetSquared"));
        private static readonly AccessTools.FieldRef<float> stOptimalSq = AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(Finder, "optimalRangeSquared"));
        private static readonly AccessTools.FieldRef<float> stCasterToCellSq = AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(Finder, "rangeFromCasterToCellSquared"));
        private static readonly AccessTools.FieldRef<float> stTargetToCellSq = AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(Finder, "rangeFromTargetToCellSquared"));
        private static readonly MethodInfo GamePreferenceMethod = AccessTools.Method(Finder, "CastPositionPreference");
        private static readonly Func<IntVec3, float> livePreference = AccessTools.MethodDelegate<Func<IntVec3, float>>(GamePreferenceMethod);

        private static bool copyLooked, haveCopy;

        // the search asked (its map, the target's real cell, the cell asked)
        private static Map askMap;
        private static IntVec3 askTarget, askCell;
        private static CastPositionRequest askReq;
        private static float askRangeFromTarget, askRangeFromTargetSq, askOptimalSq;
        private static bool saved;
        private static CastPositionRequest savedReq;
        private static float s1, s2, s3, s4, s5;

        // (tests) the state the last search set; whether the game's method is asked through the copy
        public static float LastRangeFromTarget, LastRangeFromTargetSq, LastOptimalSq;
        public static bool UsesCopy => haveCopy;

        // (the copy's body, written by Scope3DCore.ReverseCopy)
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static float PreferenceCopy(IntVec3 c) => throw new NotImplementedException("reverse patch");

        private static void LookCopy()
        {
            if (copyLooked)
            {
                return;
            }
            copyLooked = true;
            Dictionary<MethodBase, MethodInfo> swaps = new Dictionary<MethodBase, MethodInfo>
            {
                [AccessTools.Method(typeof(CoverUtility), nameof(CoverUtility.CalculateOverallBlockChance), new[] { typeof(LocalTargetInfo), typeof(IntVec3), typeof(Map) })] = AccessTools.Method(typeof(CastSpots), nameof(BlockChance)),
                [AccessTools.PropertyGetter(typeof(IntVec3), nameof(IntVec3.LengthHorizontalSquared))] = AccessTools.Method(typeof(CastSpots), nameof(TargetToCellSquared)),
            };
            haveCopy = Scope3DCore.ReverseCopy(GamePreferenceMethod, AccessTools.Method(typeof(CastSpots), nameof(PreferenceCopy)), swaps, false, HarmonyReversePatchType.Snapshot) != null;
            if (!haveCopy)
            {
                Log.Warning("[ProjectZ] The game's shooting-spot preference could not be asked in 3D: between floors the game's own flat measures are used.");
            }
        }

        public static void PrefBegin(CastPositionRequest req, Map map)
        {
            PrefEnd();
            LookCopy();
            Verb verb = req.verb;
            IntVec3 t = req.target.Position;
            askMap = map;
            askTarget = t;
            askReq = req;
            bool cross = Heights.CrossFloor(req.caster.Position, t, map);
            float d2 = cross ? Range3D.DistSquared(req.caster.Position, CellRect.SingleCell(t), t, map, out _, out _) : 0f;
            // (on one floor exactly the game's: the flat distance, its square an integer)
            askRangeFromTargetSq = cross ? d2 : (req.caster.Position - t).LengthHorizontalSquared;
            askRangeFromTarget = cross ? Mathf.Sqrt(d2) : (req.caster.Position - t).LengthHorizontal;
            askOptimalSq = verb.EffectiveRange * 0.8f * (verb.verbProps.range * 0.8f);   // copied from vanilla TryFindCastPosition
            LastRangeFromTarget = askRangeFromTarget;
            LastRangeFromTargetSq = askRangeFromTargetSq;
            LastOptimalSq = askOptimalSq;
            savedReq = stReq();
            s1 = stRangeFromTarget();
            s2 = stRangeFromTargetSq();
            s3 = stOptimalSq();
            s4 = stCasterToCellSq();
            s5 = stTargetToCellSq();
            saved = true;
        }

        public static void PrefEnd()
        {
            if (!saved)
            {
                return;
            }
            saved = false;
            stReq() = savedReq;
            stRangeFromTarget() = s1;
            stRangeFromTargetSq() = s2;
            stOptimalSq() = s3;
            stCasterToCellSq() = s4;
            stTargetToCellSq() = s5;
            savedReq = default(CastPositionRequest);
            askMap = null;
        }

        // The game's preference for c (inside PrefBegin / PrefEnd), times the avoid grid's factor (copied from vanilla
        // CastPositionFinder.EvaluateCell). Under 0.001 the game never takes the spot (a fire: -1).
        public static float Preference(IntVec3 c, AvoidGrid avoid)
        {
            if (!saved)
            {
                return -1f;
            }
            // (the search state set again for every cell, as EvaluateCell does: nothing in between may have changed it)
            stReq() = askReq;
            stRangeFromTarget() = askRangeFromTarget;
            stRangeFromTargetSq() = askRangeFromTargetSq;
            stOptimalSq() = askOptimalSq;
            stCasterToCellSq() = (c - askReq.caster.Position).LengthHorizontalSquared;
            askCell = c;
            float pref = haveCopy ? PreferenceCopy(c) : livePreference(c);
            if (avoid != null)
            {
                pref *= Mathf.Max(0.1f, (37.5f - avoid[c]) / 37.5f);
            }
            return pref;
        }

        // (the copy) the cover c has against the target's fire: by height between floors (sandbags help against a floor
        // above only where the line passes below their top), the game's on one floor.
        public static float BlockChance(LocalTargetInfo c, IntVec3 shooterLoc, Map map)
        {
            if (askMap != null && Heights.CrossFloor(c.Cell, askTarget, map))
            {
                HeightCover.For(c, askTarget, map, out float block);
                return block;
            }
            return CoverUtility.CalculateOverallBlockChance(c, shooterLoc, map);
        }

        // (the copy) the target-to-cell distance squared: 3D between floors, the game's on one floor.
        public static float TargetToCellSquared(ref IntVec3 v)
        {
            if (askMap != null && Heights.CrossFloor(askCell, askTarget, askMap))
            {
                return Range3D.DistSquared(askCell, CellRect.SingleCell(askTarget), askTarget, askMap, out _, out _);
            }
            return v.LengthHorizontalSquared;
        }
    }

    // A turret ordered to attack a target on another floor (player turrets, mortars): the range rule, the game's messages.
    [HarmonyPatch(typeof(Building_TurretGun), nameof(Building_TurretGun.OrderAttack))]
    [PatchGroup(FailSoft.Combat)]
    [CombatPartOf(CombatPart.Range)]
    public static class TurretGun_OrderAttack_Patch
    {
        private static readonly AccessTools.FieldRef<Building_Turret, LocalTargetInfo> forced = AccessTools.FieldRefAccess<Building_Turret, LocalTargetInfo>("forcedTarget");
        private static readonly AccessTools.FieldRef<Building_TurretGun, int> cooldown = AccessTools.FieldRefAccess<Building_TurretGun, int>("burstCooldownTicksLeft");
        private static readonly AccessTools.FieldRef<Building_TurretGun, bool> holdFire = AccessTools.FieldRefAccess<Building_TurretGun, bool>("holdFire");

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
            if (Range3D.RangeDistance(__instance.Position, targ.Cell, map) > GameRange.Weapon.Flat(v))
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
    [CombatPartOf(CombatPart.Range)]
    public static class FloatMenu_RangedReason_Patch
    {
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
                if (Range3D.RangeDistance(pawn.Position, target.Cell, map) > GameRange.Weapon.Flat(v))
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
    // The cells of the other floors it reaches by the range rule and sees, too. The marks copy vanilla
    // AvoidGrid.PrintAvoidGridAroundTurret (private, nothing to ask): the range read from verbProps.range + 4 cells, past
    // the minimum range, walkable cells in sight, 45 each. Parity with the game's own on the turret's floor: test mode
    // gamerange (G4).
    [HarmonyPatch(typeof(AvoidGrid), "PrintAvoidGridAroundTurret")]
    [PatchGroup(FailSoft.Combat)]
    [CombatPartOf(CombatPart.Range)]
    public static class AvoidGrid_Turret_Patch
    {
        private static readonly Action<AvoidGrid, IntVec3, int> increment =
            AccessTools.MethodDelegate<Action<AvoidGrid, IntVec3, int>>(AccessTools.Method(typeof(AvoidGrid), "IncrementAvoidGrid"));

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
                int ks = ProjectZ.FloorOf(tur.Position, map);
                for (int k = a.MinLevel; k <= a.MaxLevel; k++)
                {
                    if (k != ks)
                    {
                        Mark(tur, k, map, (c, m) => increment(__instance, c, m));
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Marking a turret's fire on other floors failed: " + e, 61190312);
            }
        }

        // The turret's marks on floor k (on its own floor: what the game marks there, for the parity test).
        public static void Mark(Building_TurretGun tur, int k, Map map, Action<IntVec3, int> mark)
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
            float reach = GameRange.HorizontalReach(range + 4f, GameRange.Weapon.sphere, ks, k);
            if (reach < 1f)
            {
                return;
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
                    mark(c, 45);
                }
            }
        }
    }
}
