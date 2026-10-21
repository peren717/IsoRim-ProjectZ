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
    // Explosions across floors (design "Explosions across floors", user decision 2026-10-16). An explosion is a sphere:
    //   - its centre is its cell's ground x, z at its floor's height + Middle; it hits every stacked cell (the ground and
    //     each floor band) within its radius in 3D (over the ground plus the height between floors, Range3D), never a
    //     void row, and never a cell of another band by the raw row overlap of the map (the old leak);
    //   - on its own floor the game's own cells stand (line of sight, walls next to open cells); on another floor a cell
    //     is hit where the height line from the centre to it is clear (Heights.Clear: slabs, roofs and walls block), so a
    //     grenade on a slab misses the room under it but reaches the ground past the slab's edge; on an upper floor only
    //     cells with a floor are hit (open air holds nothing);
    //   - every distance the explosion measures to a cell (damage and armour falloff, fire and gas chances, spread
    //     timing, cell fleck colour, snow melt, the hit's direction, a dormant thing waking, animals fleeing) is that 3D
    //     distance: ExplosionDistance_Patch swaps the game's flat measure for Distance below in those methods.
    // Everything on one floor, and every map without floors, is the game's own.
    public static class Explosions3D
    {
        // Hook for later rules (orbital strikes through floors, pending design): while true, other floors' cells are
        // picked by 3D distance alone, without the height line.
        public static bool IgnoreBlocking;

        // Test only (-projectznoexplfix): the game's flat explosions, to show the old leak between bands.
        public static bool Off;

        // Height over its floor of the lowest part of a cell above the centre that the blast can reach.
        private const float NearBottom = 0.1f;

        // Cells of the explosion at center: the game's cells kept on the centre's floor, plus the other floors' cells.
        public static List<IntVec3> CellsToHit(IEnumerable<IntVec3> vanilla, IntVec3 center, Map map, float radius, FloatRange? angle, AnnexMapComponent an)
        {
            List<IntVec3> res = new List<IntVec3>();
            int kc = ProjectZ.FloorOf(center, map);
            if (kc < 0)
            {
                return res;   // a void row: nothing there to hit
            }
            foreach (IntVec3 c in vanilla)
            {
                if (ProjectZ.FloorOf(c, map) == kc)
                {
                    res.Add(c);
                }
            }
            if (AimFloor.IsVirtualCentre(center, map))
            {
                return res;   // a directional spew aimed at another floor: its cone on the target's floor only (AimFloor)
            }
            IntVec3 gc = ProjectZ.GroundCellOf(center, map);
            float hc = kc * Heights.Storey + Heights.Middle;
            for (int k = an.MinLevel; k <= an.MaxLevel; k++)
            {
                if (k == kc)
                {
                    continue;
                }
                float v = (k - kc) * Range3D.Storey;
                float h2 = radius * radius - v * v;
                if (h2 <= 0f)
                {
                    continue;
                }
                // the cell is reached where the line to its middle, or to the part of it nearest the centre (the top of
                // a cell below, the bottom of one above), is clear: a blast on a slab's edge cell reaches the ground cell
                // just past it (the line to that cell's middle grazes the slab's rim)
                float ht = k * Heights.Storey + Heights.Middle;
                float hn = k * Heights.Storey + (k < kc ? Heights.Wall : NearBottom);
                int n = GenRadial.NumCellsInRadius(Mathf.Min(Mathf.Sqrt(h2), GenRadial.MaxRadialPatternRadius - 1f));
                for (int i = 0; i < n; i++)
                {
                    IntVec3 g = gc + GenRadial.RadialPattern[i];
                    if (g.x < 0 || g.z < 0 || g.x >= map.Size.x || g.z >= an.groundZ)
                    {
                        continue;
                    }
                    IntVec3 c = ProjectZ.CellOnFloor(g, k, map);
                    if (!c.IsValid || !c.InBounds(map) || !Column.FloorAt(g, k, map))
                    {
                        continue;
                    }
                    if (angle.HasValue && !InAngle(g, gc, Mathf.Sqrt(h2 + v * v), angle.Value))
                    {
                        continue;
                    }
                    // (and the exact point where each line crosses a level's plane: the walk's windows can step over it on
                    // a diagonal, which let a blast on a mountain top or a slab reach the cell diagonally under it)
                    if (!IgnoreBlocking && !(Heights.Clear(gc, g, hc, ht, map) && PlanesClear(gc, g, hc, ht, map)) && !(Heights.Clear(gc, g, hc, hn, map) && PlanesClear(gc, g, hc, hn, map)))
                    {
                        continue;
                    }
                    res.Add(c);
                }
            }
            return res;
        }

        public static int PlaneLeaksStopped;

        // The line from height hs over ground cell gs to ht over gt crosses no solid plane between two levels (Column.
        // PlaneSolid) at the exact points where it passes those levels' heights (the column under each point).
        public static bool PlanesClear(IntVec3 gs, IntVec3 gt, float hs, float ht, Map map)
        {
            float lo = Mathf.Min(hs, ht), hi = Mathf.Max(hs, ht);
            if (hi - lo < 0.0001f)
            {
                return true;
            }
            for (int k = Levels.Min(map) + 1; k <= Levels.Max(map) + 1; k++)
            {
                float level = k * Heights.Storey;
                if (level < lo || level > hi)
                {
                    continue;
                }
                float t = (level - hs) / (ht - hs);
                IntVec3 g = new IntVec3(Mathf.FloorToInt(gs.x + t * (gt.x - gs.x) + 0.5f), 0, Mathf.FloorToInt(gs.z + t * (gt.z - gs.z) + 0.5f));
                if (g.x >= 0 && g.z >= 0 && g.x < map.Size.x && g.z < ProjectZ.Annex(map).groundZ && Column.PlaneSolid(g, k, map))
                {
                    PlaneLeaksStopped++;
                    return false;
                }
            }
            return true;
        }

        // The game's angle filter (directional explosions) over the ground.
        private static bool InAngle(IntVec3 g, IntVec3 gc, float radius, FloatRange angle)
        {
            float len = (g - gc).LengthHorizontal;
            if (len <= 0.5f)
            {
                return false;
            }
            float f = len / radius;
            float a = Mathf.Atan2(-(g.z - gc.z), g.x - gc.x) * 57.29578f;
            return !(a - angle.min < -0.5f * f || a - angle.max > 0.5f * f);
        }

        // The explosion's distance between two cells: 3D between floors, the game's flat one otherwise.
        public static float Distance(IntVec3 a, IntVec3 b, Explosion e)
        {
            Map map = e?.Map;
            if (!Off && map != null && Heights.CrossFloor(a, b, map))
            {
                return Range3D.Distance(a, b, map);
            }
            return a.DistanceTo(b);
        }

        // Replacements for the game's calls (same arguments, the explosion appended).
        public static float DistanceTo(IntVec3 a, IntVec3 b, Explosion e) => Distance(a, b, e);

        public static int DistanceToSquared(IntVec3 a, IntVec3 b, Explosion e)
        {
            Map map = e?.Map;
            if (!Off && map != null && Heights.CrossFloor(a, b, map))
            {
                float d = Range3D.Distance(a, b, map);
                return Mathf.RoundToInt(d * d);
            }
            return a.DistanceToSquared(b);
        }

        public static bool InHorDistOf(ref IntVec3 a, IntVec3 b, float maxDist, Explosion e)
        {
            Map map = e?.Map;
            if (!Off && map != null && Heights.CrossFloor(a, b, map))
            {
                return Range3D.Distance(a, b, map) <= maxDist;
            }
            return a.InHorDistOf(b, maxDist);
        }

        // (a - b).LengthHorizontal
        public static float SubLength(IntVec3 a, IntVec3 b, Explosion e) => Distance(a, b, e);

        // (a - b).AngleFlat: over the ground between floors.
        public static float SubAngle(IntVec3 a, IntVec3 b, Explosion e)
        {
            Map map = e?.Map;
            if (!Off && map != null && Heights.CrossFloor(a, b, map))
            {
                return (ProjectZ.GroundCellOf(a, map) - ProjectZ.GroundCellOf(b, map)).AngleFlat;
            }
            return (a - b).AngleFlat;
        }
    }

    // THE cell picker of explosions (Explosion.StartExplosion through ExplosionCellsToHit(Explosion), the targeting
    // preview GenExplosion.RenderPredictedAreaOfEffect, Verb_LaunchProjectileStaticPsychic's highlight). No DamageWorker
    // overrides it in the game.
    [HarmonyPatch(typeof(DamageWorker), nameof(DamageWorker.ExplosionCellsToHit), new[] { typeof(IntVec3), typeof(Map), typeof(float), typeof(IntVec3?), typeof(IntVec3?), typeof(FloatRange?) })]
    public static class DamageWorker_ExplosionCellsToHit_Patch
    {
        [HarmonyPriority(Priority.Last)]   // final say between floors (FinalSay)
        public static void Postfix(IntVec3 center, Map map, float radius, FloatRange? affectedAngle, ref IEnumerable<IntVec3> __result)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || __result == null || Explosions3D.Off)
            {
                return;
            }
            __result = Explosions3D.CellsToHit(__result, center, map, radius, affectedAngle, an);
        }
    }

    // Cells handed to an explosion directly (overrideCells: the fire spew's cone) and the neighbours some explosions add
    // (applyDamageToExplosionCellsNeighbors) never reach a void row, nor another band by the raw row overlap.
    [HarmonyPatch(typeof(Explosion), "AffectCell")]
    public static class Explosion_AffectCell_Patch
    {
        public static bool Prefix(Explosion __instance, IntVec3 c)
        {
            Map map = __instance.Map;
            if (map == null || Explosions3D.Off || !ProjectZ.HasAnnex(map))
            {
                return true;
            }
            int k = ProjectZ.FloorOf(c, map);
            if (k < 0)
            {
                return false;
            }
            return __instance.overrideCells.NullOrEmpty() || k == ProjectZ.FloorOf(__instance.Position, map);
        }
    }

    // Every distance an explosion measures from its centre to a cell or thing, in 3D between floors (see Explosions3D).
    [HarmonyPatch]
    public static class ExplosionDistance_Patch
    {
        // method -> argument holding the explosion
        private static readonly Dictionary<MethodBase, int> targets = new Dictionary<MethodBase, int>();

        private static readonly MethodInfo distTo = AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceTo), new[] { typeof(IntVec3), typeof(IntVec3) });
        private static readonly MethodInfo distSq = AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceToSquared), new[] { typeof(IntVec3), typeof(IntVec3) });
        private static readonly MethodInfo inHor = AccessTools.Method(typeof(IntVec3), nameof(IntVec3.InHorDistOf), new[] { typeof(IntVec3), typeof(float) });
        private static readonly MethodInfo sub = AccessTools.Method(typeof(IntVec3), "op_Subtraction", new[] { typeof(IntVec3), typeof(IntVec3) });
        private static readonly MethodInfo lenH = AccessTools.PropertyGetter(typeof(IntVec3), nameof(IntVec3.LengthHorizontal));
        private static readonly MethodInfo angleFlat = AccessTools.PropertyGetter(typeof(IntVec3), nameof(IntVec3.AngleFlat));

        public static int Replaced;   // calls swapped over all targets (logged; the test checks it)

        // calls swapped per method (the test reports it)
        public static readonly Dictionary<string, int> PerMethod = new Dictionary<string, int>();

        public static IEnumerable<MethodBase> TargetMethods()
        {
            void Add(System.Type t, string name, int arg)
            {
                MethodBase m = AccessTools.Method(t, name);
                if (m != null)
                {
                    targets[m] = arg;
                }
                else
                {
                    Log.Warning($"[ProjectZ] explosions across floors: {t.Name}.{name} not found");
                }
            }
            Add(typeof(Explosion), nameof(Explosion.GetDamageAmountAt), 0);
            Add(typeof(Explosion), nameof(Explosion.GetArmorPenetrationAt), 0);
            Add(typeof(Explosion), "GetCellAffectTick", 0);
            Add(typeof(Explosion), "AffectCell", 0);
            Add(typeof(DamageWorker), nameof(DamageWorker.ExplosionAffectCell), 1);
            Add(typeof(DamageWorker), "ExplosionDamageThing", 1);
            Add(typeof(DamageWorker_Vaporize), nameof(DamageWorker_Vaporize.ExplosionAffectCell), 1);
            Add(typeof(DamageWorker_Vaporize), "ExplosionDamageThing", 1);
            Add(typeof(ThingWithComps), nameof(ThingWithComps.Notify_Explosion), 1);
            Add(typeof(Pawn_MindState), nameof(Pawn_MindState.Notify_Explosion), 1);
            return targets.Keys;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            return FailSoft.Transpile(nameof(ExplosionDistance_Patch), original, instructions, code => Edit(code, original));
        }

        private static string Edit(List<CodeInstruction> code, MethodBase original)
        {
            int arg = targets.TryGetValue(original, out int a) ? a : original.DeclaringType == typeof(Explosion) ? 0 : 1;
            int n = 0;
            for (int i = 0; i < code.Count; i++)
            {
                CodeInstruction ci = code[i];
                MethodInfo with = null;
                if (ci.Calls(distTo))
                {
                    with = AccessTools.Method(typeof(Explosions3D), nameof(Explosions3D.DistanceTo));
                }
                else if (ci.Calls(distSq))
                {
                    with = AccessTools.Method(typeof(Explosions3D), nameof(Explosions3D.DistanceToSquared));
                }
                else if (ci.Calls(inHor))
                {
                    with = AccessTools.Method(typeof(Explosions3D), nameof(Explosions3D.InHorDistOf));
                }
                else if (ci.Calls(sub) && i + 3 < code.Count && code[i + 1].IsStloc() && code[i + 2].opcode.Name.StartsWith("ldloca")
                    && (code[i + 3].Calls(lenH) || code[i + 3].Calls(angleFlat)))
                {
                    // (a - b).LengthHorizontal / .AngleFlat -> one call on a, b
                    with = AccessTools.Method(typeof(Explosions3D), code[i + 3].Calls(lenH) ? nameof(Explosions3D.SubLength) : nameof(Explosions3D.SubAngle));
                    code[i + 1].opcode = OpCodes.Nop;
                    code[i + 1].operand = null;
                    code[i + 2].opcode = OpCodes.Nop;
                    code[i + 2].operand = null;
                    code[i + 3].opcode = OpCodes.Nop;
                    code[i + 3].operand = null;
                }
                if (with == null)
                {
                    continue;
                }
                code.Insert(i, LoadArg(arg).WithLabels(ci.ExtractLabels()));
                i++;
                ci.opcode = OpCodes.Call;
                ci.operand = with;
                n++;
            }
            Replaced += n;
            PerMethod[original.DeclaringType?.Name + "." + original.Name] = n;
            ProjectZ.DebugLog($"explosions across floors: {original.DeclaringType?.Name}.{original.Name}: {n} distance calls in 3D");
            return n > 0 ? null : "no distance call found";
        }

        private static CodeInstruction LoadArg(int i)
        {
            switch (i)
            {
                case 0: return new CodeInstruction(OpCodes.Ldarg_0);
                case 1: return new CodeInstruction(OpCodes.Ldarg_1);
                case 2: return new CodeInstruction(OpCodes.Ldarg_2);
                default: return new CodeInstruction(OpCodes.Ldarg_S, (byte)i);
            }
        }
    }
}

namespace ProjectZ.Combat
{
    // The doomsday rocket's 3 extra flame blasts pick their centres in an 11x11 rect around the impact clipped to the map:
    // by raw rows they could land in a void row or on another band. Clipped to the impact's band instead.
    [HarmonyPatch(typeof(Projectile_DoomsdayRocket), "Impact")]
    public static class DoomsdayRocket_Impact_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            return FailSoft.Transpile(nameof(DoomsdayRocket_Impact_Patch), original, instructions, code =>
            {
                MethodInfo clip = AccessTools.Method(typeof(CellRect), nameof(CellRect.ClipInsideMap), new[] { typeof(Map) });
                int n = 0;
                foreach (CodeInstruction ci in code)
                {
                    if (ci.Calls(clip))
                    {
                        ci.opcode = OpCodes.Call;
                        ci.operand = AccessTools.Method(typeof(DoomsdayRocket_Impact_Patch), nameof(ClipInsideBand));
                        n++;
                    }
                }
                return n == 1 ? null : $"{n} map clips found (expected 1)";
            });
        }

        public static CellRect ClipInsideBand(ref CellRect r, Map map)
        {
            IntVec3 centre = r.CenterCell;
            r.ClipInsideMap(map);
            AnnexMapComponent an = ProjectZ.Annex(map);
            int k = ProjectZ.FloorOf(centre, map);
            if (an == null || k < 0 || Explosions3D.Off)
            {
                return r;
            }
            r = CellRect.FromLimits(r.minX, Mathf.Max(r.minZ, Levels.RowMin(map, k)), r.maxX, Mathf.Min(r.maxZ, Levels.RowMax(map, k)));
            return r;
        }
    }
}
