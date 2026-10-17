using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Combat
{
    // Jumps between floors (design "Jump packs across floors", user 2026-10-16). Every jump in the game goes through
    // JumpUtility: Verb_Jump (Royalty's jump pack and locust armour, modded packs) and Verb_CastAbilityJump (Biotech's
    // longjump legs, the apocriton's and paramedic's mech longjumps, Anomaly's devourer leap, modded abilities) check a
    // target with JumpUtility.CanHitTargetFrom and ValidJumpTarget, and fly the pawn in a PawnFlyer. Between floors:
    //   - range is the 3D distance (Range3D.Distance: over the ground plus the height between the floors; no bonus,
    //     nothing is shot);
    //   - the landing needs open sky (no roof over the landing cell; the stairwell's counts as open);
    //   - the straight line from the take-off (eye height) to the landing (middle height) must not pass through a
    //     slab, a roof or a wall (Heights.Clear, the shooting line of sight without the shooters' slab-edge rule: a
    //     jump may land a little in from the edge, as far as the line clears the rim);
    //   - nobody lands in the void rows between floors, and the flight's mid-air re-targeting stays on the landing's floor;
    //   - the flight lasts as long as its 3D length, and is drawn in 3D between the real floors (FloorLayout.ThingOffset),
    //     its shadow on the floor under the pawn;
    //   - the landing does no harm (a jump pack's landing, unlike falling off a slab edge).
    // Jumps on one floor are left to the game. The game's own AI jumps (paramedic mech toward its rescue or fire,
    // apocriton escape) go through the same rules; no new AI.
    public static class JumpRules
    {
        // A pawn's jump from root to cell lands on another floor.
        public static bool Cross(IntVec3 root, IntVec3 cell, Map map)
        {
            return map != null && Heights.CrossFloor(root, cell, map);
        }

        // The cross-floor rule (both on floors of map): range, open sky, a clear line.
        public static bool CanJump(IntVec3 root, IntVec3 cell, float range, Map map)
        {
            return Range3D.Distance(root, cell, map) <= range && Column.OpenAbove(cell, map) && Sight(root, cell, map);
        }

        // The straight line from the take-off to the landing clears every slab, roof and wall between them.
        public static bool Sight(IntVec3 root, IntVec3 cell, Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            int ks = ProjectZ.FloorOf(root, map);
            int kt = ProjectZ.FloorOf(cell, map);
            IntVec3 gs = ProjectZ.GroundCellOf(root, map);
            IntVec3 gt = ProjectZ.GroundCellOf(cell, map);
            if (an == null || ks < 0 || kt < 0 || !gs.IsValid || !gt.IsValid || gs == gt)
            {
                return false;   // straight up or down: a slab is always between
            }
            return Heights.Clear(gs, gt, ks * Heights.Storey + Heights.Eye, kt * Heights.Storey + Heights.Middle, map, an.floors);
        }

        public static bool IsJumpVerb(Verb v) => v is Verb_Jump || v is Verb_CastAbilityJump;

        // ---- the game's AI jumps (JobGiver_AIJumpToJobTarget): its raw row distance and line of sight

        [ThreadStatic] private static Map aiMap;

        public static void SetAIMap(Map map) => aiMap = map;

        public static float AIDistance(IntVec3 a, IntVec3 b)
        {
            Map map = aiMap;
            return Cross(a, b, map) ? Range3D.Distance(a, b, map) : a.DistanceTo(b);
        }

        public static bool AISight(IntVec3 a, IntVec3 b, Map map)
        {
            return Cross(a, b, map) ? Sight(a, b, map) : GenSight.LineOfSight(a, b, map);
        }
    }

    [HarmonyPatch(typeof(JumpUtility), nameof(JumpUtility.CanHitTargetFrom))]
    public static class Jump_CanHitTargetFrom_Patch
    {
        public static bool Prepare() => CombatGate.Enabled;

        public static bool Prefix(Pawn pawn, IntVec3 root, LocalTargetInfo targ, float range, ref bool __result)
        {
            Map map = pawn?.Map;
            if (!targ.IsValid || !JumpRules.Cross(root, targ.Cell, map))
            {
                return true;
            }
            __result = JumpRules.CanJump(root, targ.Cell, range, map);
            return false;
        }
    }

    // No landing in the void rows between floors.
    [HarmonyPatch(typeof(JumpUtility), nameof(JumpUtility.ValidJumpTarget))]
    public static class Jump_ValidTarget_Patch
    {
        public static void Postfix(Map map, IntVec3 cell, ref bool __result)
        {
            if (__result && ProjectZ.Annex(map) != null && ProjectZ.FloorOf(cell, map) < 0)
            {
                __result = false;
            }
        }
    }

    // The warm-up's line (Verb.TryStartCastOn -> TryFindShootLineFromTo) for a jump between floors: the jump rule, not
    // the shooting one (which has the high-ground bonus and the slab-edge rule).
    [HarmonyPatch(typeof(Verb), nameof(Verb.TryFindShootLineFromTo))]
    public static class Jump_ShootLine_Patch
    {
        public static bool Prepare() => CombatGate.Enabled;

        public static bool Prefix(Verb __instance, IntVec3 root, LocalTargetInfo targ, ref ShootLine resultingLine, ref bool __result)
        {
            if (!JumpRules.IsJumpVerb(__instance) || !targ.IsValid)
            {
                return true;
            }
            Map map = __instance.caster?.Map;
            if (!JumpRules.Cross(root, targ.Cell, map))
            {
                return true;
            }
            resultingLine = new ShootLine(root, targ.Cell);
            __result = __instance.CanHitTargetFrom(root, targ);
            return false;
        }
    }

    // The game's AI jump toward its job's target (paramedic mechs: rescue, fire) measures and looks along the map rows.
    [HarmonyPatch(typeof(JobGiver_AIJumpToJobTarget), "TryGiveJob")]
    public static class AIJumpToJobTarget_Patch
    {
        private static readonly MethodInfo Dist = AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceTo), new[] { typeof(IntVec3), typeof(IntVec3) });
        private static readonly MethodInfo Sight = AccessTools.Method(typeof(GenSight), nameof(GenSight.LineOfSight), new[] { typeof(IntVec3), typeof(IntVec3), typeof(Map) });

        public static bool Prepare() => CombatGate.Enabled;

        public static void Prefix(Pawn pawn) => JumpRules.SetAIMap(pawn?.Map);

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int d = 0, s = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(Dist))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(JumpRules), nameof(JumpRules.AIDistance));
                    d++;
                }
                else if (ci.Calls(Sight))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(JumpRules), nameof(JumpRules.AISight));
                    s++;
                }
                yield return ci;
            }
            if (d != 1 || s != 1)
            {
                Log.Warning($"[ProjectZ] JobGiver_AIJumpToJobTarget changed (distance calls {d}, sight calls {s}): AI jumps between floors may be measured on the map rows.");
            }
        }
    }

    // ---- the flight

    public static class JumpFlights
    {
        private static readonly AccessTools.FieldRef<PawnFlyer, Vector3> startVec = AccessTools.FieldRefAccess<PawnFlyer, Vector3>("startVec");
        private static readonly AccessTools.FieldRef<PawnFlyer, IntVec3> destCell = AccessTools.FieldRefAccess<PawnFlyer, IntVec3>("destCell");
        private static readonly AccessTools.FieldRef<PawnFlyer, float> flightDistance = AccessTools.FieldRefAccess<PawnFlyer, float>("flightDistance");
        private static readonly AccessTools.FieldRef<PawnFlyer, int> ticksFlightTime = AccessTools.FieldRefAccess<PawnFlyer, int>("ticksFlightTime");
        private static readonly AccessTools.FieldRef<PawnFlyer, int> ticksFlying = AccessTools.FieldRefAccess<PawnFlyer, int>("ticksFlying");
        private static readonly AccessTools.FieldRef<PawnFlyer, Vector3> groundPos = AccessTools.FieldRefAccess<PawnFlyer, Vector3>("groundPos");
        private static readonly AccessTools.FieldRef<PawnFlyer, Vector3> effectivePos = AccessTools.FieldRefAccess<PawnFlyer, Vector3>("effectivePos");
        private static readonly AccessTools.FieldRef<PawnFlyer, float> effectiveHeight = AccessTools.FieldRefAccess<PawnFlyer, float>("effectiveHeight");

        // A flight between floors now: where it is drawn (its band and the rise over that band's floor, the floor its
        // shadow falls on).
        public struct Pose
        {
            public int band;       // the floor whose rows the flyer stands in now
            public float rise;     // true height over that band's floor (from the path between the floors; the arc is the game's)
            public float height;   // true height over the ground, arc included
            public int shadow;     // the floor its shadow falls on
            public IntVec3 ground; // ground cell under it
        }

        private static readonly Dictionary<int, Pose> poses = new Dictionary<int, Pose>();

        public static bool PoseOf(PawnFlyer f, out Pose p) => poses.TryGetValue(f.thingIDNumber, out p);

        public static void Forget(PawnFlyer f) => poses.Remove(f.thingIDNumber);

        // Floors of the flight's two ends; false for a flight on one floor (or a map without floors).
        public static bool Ends(PawnFlyer f, Map map, out int ks, out int kd)
        {
            ks = kd = 0;
            if (f == null || map == null || ProjectZ.Annex(map) == null)
            {
                return false;
            }
            ks = ProjectZ.FloorOf(startVec(f).ToIntVec3(), map);
            kd = ProjectZ.FloorOf(destCell(f), map);
            return ks >= 0 && kd >= 0 && ks != kd;
        }

        // PawnFlyer.MakeFlyer: the flight's length between floors in 3D (the game takes the row distance).
        public static void SetLength(PawnFlyer f, Map map, IntVec3 from, IntVec3 to)
        {
            if (f != null && JumpRules.Cross(from, to, map))
            {
                flightDistance(f) = Range3D.Distance(from, to, map);
            }
        }

        // After PawnFlyer.RecomputePosition: a flight between floors runs over the ground from its start's column to its
        // landing's, rising (or falling) between the two floors' heights along the game's progress, plus the game's arc.
        public static void Recompute(PawnFlyer f)
        {
            Map map = f.Map;
            if (!Ends(f, map, out int ks, out int kd))
            {
                return;
            }
            AnnexMapComponent an = ProjectZ.Annex(map);
            int total = Mathf.Max(1, ticksFlightTime(f));
            float t = Mathf.Clamp01((float)ticksFlying(f) / total);
            PawnFlyerProperties props = f.def.pawnFlyer;
            float t2 = props.Worker.AdjustedProgress(t);
            float eh = effectiveHeight(f);
            Vector3 s = startVec(f);
            Vector3 d = f.DestinationPos;
            Vector3 gs = new Vector3(s.x, s.y, s.z - ks * an.stride);
            Vector3 gd = new Vector3(d.x, d.y, d.z - kd * an.stride);
            Vector3 g = Vector3.Lerp(gs, gd, t2);
            float storey = IsoRimBridge.StoreyHeight;
            float path = Mathf.Lerp(ks * storey, kd * storey, t2);
            float lift = props.heightFactor * eh;
            float height = path + lift;
            int lo = Mathf.Min(ks, kd), hi = Mathf.Max(ks, kd);
            // the band whose floor it is above now (so a floor hidden over it hides it once it rises past that floor)
            int band = Mathf.Clamp(Mathf.FloorToInt(height / storey + 0.001f), lo, hi);
            IntVec3 gc = new IntVec3(Mathf.Clamp(Mathf.FloorToInt(g.x), 0, map.Size.x - 1), 0, Mathf.Clamp(Mathf.FloorToInt(g.z), 0, an.groundZ - 1));
            Vector3 onBand = new Vector3(g.x, g.y, g.z + band * an.stride);
            groundPos(f) = onBand;
            effectivePos(f) = onBand + Altitudes.AltIncVect * eh + Vector3.forward * lift;
            IntVec3 cell = ProjectZ.CellOnFloor(gc, band, map);
            if (cell.IsValid && cell.InBounds(map) && f.Position != cell)
            {
                f.Position = cell;
            }
            int top = Mathf.Min(Mathf.FloorToInt(height / storey + 0.001f), an.floors - 1);
            int shadow = 0;
            for (int j = top; j >= 1; j--)
            {
                if (ProjectZ.HasFloor(ProjectZ.CellOnFloor(gc, j, map), map))
                {
                    shadow = j;
                    break;
                }
            }
            poses[f.thingIDNumber] = new Pose { band = band, rise = path - band * storey, height = height, shadow = shadow, ground = gc };
        }

        // FloorLayout.ThingOffset: the flyer (and the pawn it draws) rises over its band's floor.
        public static Vector3 Offset(PawnFlyer f)
        {
            return f.Spawned && PoseOf(f, out Pose p) && p.rise != 0f ? new Vector3(0f, p.rise, 0f) : Vector3.zero;
        }

        // PawnFlyer.DrawShadow: on the floor under the pawn, not at its path's height. The flyer's own rise (applied to
        // everything it draws, Offset) is taken back off.
        public static void ShadowAt(PawnFlyer f, ref Vector3 drawLoc)
        {
            if (!f.Spawned || !PoseOf(f, out Pose p))
            {
                return;
            }
            AnnexMapComponent an = ProjectZ.Annex(f.Map);
            if (an == null)
            {
                return;
            }
            float squash = IsoRimBridge.Squash;
            float back = p.rise / (squash > 0.01f ? squash : 1f);
            // (moved onto the shadow floor's rows: IsoRim lifts it by that floor's height)
            drawLoc = new Vector3(drawLoc.x, drawLoc.y - back, drawLoc.z + (p.shadow - p.band) * an.stride);
        }
    }

    [HarmonyPatch(typeof(PawnFlyer), nameof(PawnFlyer.MakeFlyer))]
    public static class PawnFlyer_MakeFlyer_Patch
    {
        public static void Prefix(Pawn pawn, IntVec3 destCell, out KeyValuePair<Map, IntVec3> __state)
        {
            __state = new KeyValuePair<Map, IntVec3>(pawn?.Map, pawn != null ? pawn.Position : IntVec3.Invalid);
        }

        public static void Postfix(PawnFlyer __result, IntVec3 destCell, KeyValuePair<Map, IntVec3> __state)
        {
            if (__state.Key != null && __state.Value.IsValid)
            {
                JumpFlights.SetLength(__result, __state.Key, __state.Value, destCell);
            }
        }
    }

    [HarmonyPatch(typeof(PawnFlyer), "RecomputePosition")]
    public static class PawnFlyer_Recompute_Patch
    {
        public static void Postfix(PawnFlyer __instance)
        {
            if (__instance.Spawned)
            {
                JumpFlights.Recompute(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(PawnFlyer), "DrawShadow")]
    public static class PawnFlyer_Shadow_Patch
    {
        public static void Prefix(PawnFlyer __instance, ref Vector3 drawLoc)
        {
            JumpFlights.ShadowAt(__instance, ref drawLoc);
        }
    }

    [HarmonyPatch(typeof(PawnFlyer), nameof(PawnFlyer.Destroy))]
    public static class PawnFlyer_Destroy_Patch
    {
        public static void Postfix(PawnFlyer __instance) => JumpFlights.Forget(__instance);
    }

    // The flight's mid-air re-targeting (PawnFlyer.CheckDestination, every 15 ticks: the landing became invalid, try the
    // cells round it): only cells on the landing's own floor, and for a flight between floors only under open sky.
    [HarmonyPatch(typeof(PawnFlyer), "CheckDestination")]
    public static class PawnFlyer_CheckDestination_Patch
    {
        private static readonly AccessTools.FieldRef<PawnFlyer, IntVec3> destCell = AccessTools.FieldRefAccess<PawnFlyer, IntVec3>("destCell");

        public static bool Prefix(PawnFlyer __instance)
        {
            Map map = __instance.Map;
            if (map == null || ProjectZ.Annex(map) == null)
            {
                return true;
            }
            ThingOwner held = __instance.GetDirectlyHeldThings();
            if (held == null || held.Count == 0)
            {
                return true;
            }
            Thing flying = held[0];
            IntVec3 dest = destCell(__instance);
            int k = ProjectZ.FloorOf(dest, map);
            bool cross = JumpFlights.Ends(__instance, map, out _, out _);
            if (Ok(flying, map, dest, k, cross))
            {
                return false;
            }
            int num = GenRadial.NumCellsInRadius(3.9f);
            for (int i = 0; i < num; i++)
            {
                IntVec3 c = dest + GenRadial.RadialPattern[i];
                if (Ok(flying, map, c, k, cross))
                {
                    destCell(__instance) = c;
                    break;
                }
            }
            return false;
        }

        private static bool Ok(Thing flying, Map map, IntVec3 c, int k, bool cross)
        {
            return c.InBounds(map) && ProjectZ.FloorOf(c, map) == k && JumpUtility.ValidJumpTarget(flying, map, c) && (!cross || Column.OpenAbove(c, map));
        }
    }

    // The jump's range ring on the floor viewed (and the ground) when the jumper stands on another floor: the cells it
    // can land on there (the game draws its own ring on the jumper's floor).
    [HarmonyPatch]
    public static class Jump_DrawHighlight_Patch
    {
        public static bool Prepare() => CombatGate.Enabled;

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Verb_Jump), nameof(Verb_Jump.DrawHighlight));
            yield return AccessTools.Method(typeof(Verb_CastAbilityJump), nameof(Verb_CastAbilityJump.DrawHighlight));
        }

        public static void Postfix(Verb __instance)
        {
            Thing caster = __instance.caster;
            Map map = Find.CurrentMap;
            if (caster == null || !caster.Spawned || caster.Map != map || ProjectZ.Annex(map) == null)
            {
                return;
            }
            IntVec3 root = caster.Position;
            int ks = ProjectZ.FloorOf(root, map);
            IntVec3 g = ProjectZ.GroundCellOf(root, map);
            if (ks < 0 || !g.IsValid)
            {
                return;
            }
            float range = __instance.EffectiveRange;
            int view = FloorView.Current;
            Ring(__instance, root, g, ks, 0, range, map);
            if (view != 0)
            {
                Ring(__instance, root, g, ks, view, range, map);
            }
        }

        private static void Ring(Verb verb, IntVec3 root, IntVec3 g, int ks, int k, float range, Map map)
        {
            if (k == ks)
            {
                return;
            }
            float v = Range3D.Rise(ks, k);
            float s = range * range - v * v;
            if (s < 1f)
            {
                return;
            }
            float r = Mathf.Sqrt(s);
            IntVec3 c = ProjectZ.CellOnFloor(g, k, map);
            if (!c.IsValid)
            {
                return;
            }
            Thing caster = verb.caster;
            GenDraw.DrawRadiusRing(c, Mathf.Min(r, GenRadial.MaxRadialPatternRadius - 0.5f), Color.white,
                x => JumpUtility.ValidJumpTarget(caster, map, x) && JumpRules.CanJump(root, x, range, map));
        }
    }
}
