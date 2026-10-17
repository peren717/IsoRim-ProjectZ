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
    //   - the flight's own arc must be clear (user decision 2026-10-16, JumpRules.Arc): the path the flyer is drawn on
    //     (over the ground between the two cells' centres, rising or falling between the floors, plus the game's arc for
    //     its flyer) is followed column by column, and the pawn's body (its feet up to Body over them) must not pass
    //     through a slab, a roof or a sight-blocking building (wall, closed door, rock) there. So how far in from a slab
    //     edge a jump may land follows from its length: a long jump comes down over the rim steeply enough to land
    //     further in, a short steep one doesn't; a high arc under a slab overhang is stopped;
    //   - nobody lands in the void rows between floors, and the flight's mid-air re-targeting stays on the landing's floor;
    //   - the flight lasts as long as its 3D length, and is drawn in 3D between the real floors (FloorLayout.ThingOffset),
    //     its shadow on the floor under the pawn;
    //   - the landing does no harm (a jump pack's landing, unlike falling off a slab edge).
    // Targeting (CanHitTargetFrom), the warm-up (TryFindShootLineFromTo), the game's AI jump, the mid-air re-targeting
    // and the range rings all ask Arc. Jumps on one floor are left to the game. The game's own AI jumps (paramedic mech
    // toward its rescue or fire, apocriton escape) go through the same rules; no new AI.
    public static class JumpRules
    {
        // The pawn's body over its feet for the arc check (about head height: a little under a wall's top).
        public static float Body => 0.9f * IsoRimBridge.WallHeight;

        private const float Eps = 0.01f;   // the feet stand on their floor at the take-off and the landing
        private const float Step = 0.1f;   // height samples within a column, in cells over the ground

        // A pawn's jump from root to cell lands on another floor.
        public static bool Cross(IntVec3 root, IntVec3 cell, Map map)
        {
            return map != null && Heights.CrossFloor(root, cell, map);
        }

        // The cross-floor rule (both on floors of map): range, open sky, a clear arc.
        public static bool CanJump(IntVec3 root, IntVec3 cell, float range, Map map, ThingDef flyer = null)
        {
            return Range3D.Distance(root, cell, map) <= range && Column.OpenAbove(cell, map) && Arc(root, cell, map, flyer ?? CurrentFlyer);
        }

        // The arc for the jump being checked now (its flyer, else the game's PawnFlyer).
        public static bool Sight(IntVec3 root, IntVec3 cell, Map map) => Arc(root, cell, map, CurrentFlyer);

        // ---- the flight's path, shared with its drawing (JumpFlights.Recompute)

        // Height of the path between floors ks and kd at progress u (0..1), without the arc.
        public static float Path(int ks, int kd, float u)
        {
            float storey = IsoRimBridge.StoreyHeight;
            return Mathf.Lerp(ks * storey, kd * storey, u);
        }

        // The game's arc over the path at progress u (PawnFlyer.RecomputePosition: heightFactor * Worker.GetHeight).
        public static float Lift(PawnFlyerProperties props, float u) => props.heightFactor * props.Worker.GetHeight(u);

        // The flyer's true height over the ground at progress u.
        public static float FlightHeight(PawnFlyerProperties props, int ks, int kd, float u) => Path(ks, kd, u) + Lift(props, u);

        public static PawnFlyerProperties Props(ThingDef flyer) => (flyer ?? ThingDefOf.PawnFlyer).pawnFlyer ?? ThingDefOf.PawnFlyer.pawnFlyer;

        // The flight from root to cell (on two floors) flown by flyer clears every slab, roof and wall on its way.
        public static bool Arc(IntVec3 root, IntVec3 cell, Map map, ThingDef flyer)
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
            PawnFlyerProperties props = Props(flyer);
            float dx = gt.x - gs.x, dz = gt.z - gs.z;
            float len = Mathf.Sqrt(dx * dx + dz * dz);
            // the columns the ground path (centre to centre) crosses, each with its share [u0, u1] of the flight
            int x = gs.x, z = gs.z;
            int sx = dx > 0 ? 1 : -1, sz = dz > 0 ? 1 : -1;
            float tdx = Mathf.Abs(dx) > 1e-6f ? 1f / Mathf.Abs(dx) : float.PositiveInfinity;
            float tdz = Mathf.Abs(dz) > 1e-6f ? 1f / Mathf.Abs(dz) : float.PositiveInfinity;
            float tmx = tdx * 0.5f, tmz = tdz * 0.5f;   // half a cell from the centre to the next column
            float u0 = 0f;
            for (int guard = 0; guard < 4096; guard++)
            {
                bool last = x == gt.x && z == gt.z;
                float u1 = last ? 1f : Mathf.Min(1f, Mathf.Min(tmx, tmz));
                if (u1 - u0 > 1e-4f && !ColumnClear(new IntVec3(x, 0, z), u0, u1, len, props, ks, kt, gs, gt, map))
                {
                    return false;
                }
                if (last || u1 >= 1f)
                {
                    break;
                }
                if (Mathf.Abs(tmx - tmz) < 1e-5f)
                {
                    // exactly through a corner
                    x += sx;
                    z += sz;
                    tmx += tdx;
                    tmz += tdz;
                }
                else if (tmx < tmz)
                {
                    x += sx;
                    tmx += tdx;
                }
                else
                {
                    z += sz;
                    tmz += tdz;
                }
                u0 = u1;
            }
            return true;
        }

        // Over ground column g the pawn's feet run between the lowest and highest point of the path in [u0, u1], its head
        // Body higher. Blocked by a solid plane (slab, roof) strictly inside that span, or a sight-blocking building it
        // overlaps (the take-off and landing cells themselves don't count).
        private static bool ColumnClear(IntVec3 g, float u0, float u1, float len, PawnFlyerProperties props, int ks, int kt, IntVec3 gs, IntVec3 gt, Map map)
        {
            int lowest = Levels.Min(map), top = Levels.Max(map);
            int m = Mathf.Max(1, Mathf.CeilToInt((u1 - u0) * len / Step));
            float lo = float.MaxValue, hi = float.MinValue;
            for (int j = 0; j <= m; j++)
            {
                float h = FlightHeight(props, ks, kt, Mathf.Lerp(u0, u1, (float)j / m));
                lo = Mathf.Min(lo, h);
                hi = Mathf.Max(hi, h);
            }
            float head = hi + Body;
            float storey = IsoRimBridge.StoreyHeight;
            float wall = IsoRimBridge.WallHeight;
            for (int k = lowest + 1; k <= top + 1; k++)
            {
                float level = k * storey;
                if (level > lo + Eps && level < head && Heights.Plane(g, k, map))
                {
                    return false;
                }
            }
            for (int k = lowest; k <= top; k++)
            {
                if (g == gs && k == ks || g == gt && k == kt)
                {
                    continue;
                }
                float bottom = k * storey;
                if (head <= bottom + Eps || lo >= bottom + wall)
                {
                    continue;
                }
                IntVec3 c = ProjectZ.CellOnFloor(g, k, map);
                if (c.IsValid && c.InBounds(map) && !c.CanBeSeenOverFast(map))
                {
                    return false;
                }
            }
            return true;
        }

        // ---- the flyer of the jump being checked (the arc's height): set around the jump verbs' CanHitTargetFrom and
        // JumpUtility.OrderJump (Jump_VerbFlyer_Patch); the game's PawnFlyer otherwise.

        [ThreadStatic] private static ThingDef flyerNow;

        public static ThingDef CurrentFlyer => flyerNow ?? ThingDefOf.PawnFlyer;

        public static ThingDef FlyerOf(Verb v) => v is Verb_CastAbilityJump j ? j.JumpFlyerDef : ThingDefOf.PawnFlyer;

        public static ThingDef Push(ThingDef d)
        {
            ThingDef old = flyerNow;
            flyerNow = d;
            return old;
        }

        public static void Pop(ThingDef old) => flyerNow = old;

        public static bool IsJumpVerb(Verb v) => v is Verb_Jump || v is Verb_CastAbilityJump;

        // ---- the game's AI jumps (JobGiver_AIJumpToJobTarget): its raw row distance and line of sight

        [ThreadStatic] private static Map aiMap;
        [ThreadStatic] private static ThingDef aiFlyer;

        public static void SetAI(Map map, ThingDef flyer)
        {
            aiMap = map;
            aiFlyer = flyer;
        }

        public static float AIDistance(IntVec3 a, IntVec3 b)
        {
            Map map = aiMap;
            return Cross(a, b, map) ? Range3D.Distance(a, b, map) : a.DistanceTo(b);
        }

        public static bool AISight(IntVec3 a, IntVec3 b, Map map)
        {
            return Cross(a, b, map) ? Arc(a, b, map, aiFlyer ?? ThingDefOf.PawnFlyer) : GenSight.LineOfSight(a, b, map);
        }
    }

    // The flyer of the jump checked: the jump verbs' own target checks (targeting, warm-up, AI validation) and the ordered
    // jump's cell search.
    [HarmonyPatch]
    public static class Jump_VerbFlyer_Patch
    {
        public static bool Prepare() => CombatGate.Enabled;

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Verb_Jump), nameof(Verb_Jump.CanHitTargetFrom));
            yield return AccessTools.Method(typeof(Verb_CastAbilityJump), nameof(Verb_CastAbilityJump.CanHitTargetFrom));
        }

        public static void Prefix(Verb __instance, out ThingDef __state) => __state = JumpRules.Push(JumpRules.FlyerOf(__instance));

        public static void Finalizer(ThingDef __state) => JumpRules.Pop(__state);
    }

    [HarmonyPatch(typeof(JumpUtility), nameof(JumpUtility.OrderJump))]
    public static class Jump_OrderFlyer_Patch
    {
        public static bool Prepare() => CombatGate.Enabled;

        public static void Prefix(Verb verb, out ThingDef __state) => __state = JumpRules.Push(JumpRules.FlyerOf(verb));

        public static void Finalizer(ThingDef __state) => JumpRules.Pop(__state);
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

        public static void Prefix(Pawn pawn, JobGiver_AIJumpToJobTarget __instance)
        {
            Verb v = pawn?.abilities?.GetAbility(__instance.ability)?.verb;
            JumpRules.SetAI(pawn?.Map, v != null ? JumpRules.FlyerOf(v) : null);
        }

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

        // The cell the flight took off from.
        public static IntVec3 StartCell(PawnFlyer f) => startVec(f).ToIntVec3();

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
            Vector3 gs = new Vector3(s.x, s.y, s.z - Levels.Shift(map, ks));
            Vector3 gd = new Vector3(d.x, d.y, d.z - Levels.Shift(map, kd));
            Vector3 g = Vector3.Lerp(gs, gd, t2);
            float storey = IsoRimBridge.StoreyHeight;
            float path = JumpRules.Path(ks, kd, t2);
            float lift = props.heightFactor * eh;
            float height = path + lift;
            int lo = Mathf.Min(ks, kd), hi = Mathf.Max(ks, kd);
            // the band whose floor it is above now (so a floor hidden over it hides it once it rises past that floor)
            int band = Mathf.Clamp(Mathf.FloorToInt(height / storey + 0.001f), lo, hi);
            IntVec3 gc = new IntVec3(Mathf.Clamp(Mathf.FloorToInt(g.x), 0, map.Size.x - 1), 0, Mathf.Clamp(Mathf.FloorToInt(g.z), 0, an.groundZ - 1));
            Vector3 onBand = new Vector3(g.x, g.y, g.z + Levels.Shift(map, band));
            groundPos(f) = onBand;
            effectivePos(f) = onBand + Altitudes.AltIncVect * eh + Vector3.forward * lift;
            IntVec3 cell = ProjectZ.CellOnFloor(gc, band, map);
            if (cell.IsValid && cell.InBounds(map) && f.Position != cell)
            {
                f.Position = cell;
            }
            int top = Mathf.Min(Mathf.FloorToInt(height / storey + 0.001f), an.MaxLevel);
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
            drawLoc = new Vector3(drawLoc.x, drawLoc.y - back, drawLoc.z + Levels.Shift(f.Map, p.shadow) - Levels.Shift(f.Map, p.band));
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
    // cells round it): only cells on the landing's own floor, and for a flight between floors only under open sky with a
    // clear arc from the take-off (the flight is redrawn toward the new cell).
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
            IntVec3 start = JumpFlights.StartCell(__instance);
            ThingDef def = __instance.def;
            if (Ok(flying, map, dest, k, cross, start, def))
            {
                return false;
            }
            int num = GenRadial.NumCellsInRadius(3.9f);
            for (int i = 0; i < num; i++)
            {
                IntVec3 c = dest + GenRadial.RadialPattern[i];
                if (Ok(flying, map, c, k, cross, start, def))
                {
                    destCell(__instance) = c;
                    break;
                }
            }
            return false;
        }

        private static bool Ok(Thing flying, Map map, IntVec3 c, int k, bool cross, IntVec3 start, ThingDef def)
        {
            return c.InBounds(map) && ProjectZ.FloorOf(c, map) == k && JumpUtility.ValidJumpTarget(flying, map, c) && (!cross || Column.OpenAbove(c, map) && JumpRules.Arc(start, c, map, def));
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
                x => JumpUtility.ValidJumpTarget(caster, map, x) && JumpRules.CanJump(root, x, range, map, JumpRules.FlyerOf(verb)));
        }
    }
}
