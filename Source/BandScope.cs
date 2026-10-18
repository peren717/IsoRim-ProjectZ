using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ProjectZ.Combat;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // Effects across the void that are not radius loops (isorim-pz-void-barrier.md §3). The game's radius loops (radial
    // cells round a centre: tornado damage, triggers, auras, pollution, terraforming, plant damage, ...) are clipped
    // centrally by BandRadial.cs (compat G3a; the rows this table had for them were retired in G3b after the parity
    // checks of mode radial). What stays here is a cell check outside those loops, one row per method:
    //   - a prefix records the source's band (the instance's thing: a Thing, a comp's parent, a hediff's pawn);
    //   - a transpiler swaps the method's own InBounds / Filled calls for band-aware ones (false / true for a cell in
    //     another band or the void); its compiler-generated lambdas are swapped too (they run inside its scope).
    //   Rows:
    //   - Tornado.Tick: the tornado's own move (InBounds of its next cell): it ends at the void like at the map edge.
    //     Only Tick's own IL is swapped; the damage it calls (DamageCloseThings, DamageFarThings, DestroyRoofs) is not
    //     in this table and is BandRadial's alone (the scope does nothing there).
    //   - CompSnowExpand.TryExpandSnow: its flood fill (Filled, in a lambda) stays on the band. BandRadial keeps
    //     the snow cells themselves on the band, but without this row the flood walks along the void row and brings snow
    //     into a room of the band that is open only to the void (mode radial, R16b). (No vanilla def has the comp.)
    // Separate, per effect:
    //   - GenRadial.RadialDistinctThingsAround (meditation and artificial-building listers, abilities' areas, Farskip,
    //     broadshield pack, overseer, holding platform, ...): its iterator keeps things on the centre's band;
    //   - auras (CompCauseHediff_AoE) and the mechanitor's command range: the true 3D distance between floors;
    //   - shield bubbles: Combat/Shields3D.cs; flashstorm strikes: SkyStrikes (the centre's band);
    //   - infestations per band (InfestationBand_Patch below).
    public static class BandScope
    {
        private struct Frame
        {
            public Map map;
            public int band;
        }

        [ThreadStatic]
        private static List<Frame> stack;

        public static void Push(Map map, IntVec3 src)
        {
            (stack ??= new List<Frame>()).Add(new Frame { map = map, band = map != null && src.IsValid ? ProjectZ.FloorOf(src, map) : -1 });
        }

        public static void Pop()
        {
            if (stack != null && stack.Count > 0)
            {
                stack.RemoveAt(stack.Count - 1);
            }
        }

        // A cell in another band than the current scope's source (false outside any scope or on a map without floors).
        public static bool OffBand(IntVec3 c, Map map)
        {
            if (VoidRule.Off || stack == null || stack.Count == 0)
            {
                return false;
            }
            Frame f = stack[stack.Count - 1];
            if (f.band < 0 || !ReferenceEquals(f.map, map) || ProjectZ.Annex(map) == null)
            {
                return false;
            }
            return ProjectZ.FloorOf(c, map) != f.band;
        }

        // ---- the swapped calls (same arguments as the game's)

        public static bool InBounds(IntVec3 c, Map map) => c.InBounds(map) && !OffBand(c, map);

        public static bool Filled(IntVec3 c, Map map) => OffBand(c, map) || c.Filled(map);

        // RadialDistinctThingsAround's iterator: the centre is its own source.
        public static bool InBoundsNear(IntVec3 c, Map map, IntVec3 centre) => c.InBounds(map) && (VoidRule.Off || ProjectZ.SameBand(c, centre, map));

        internal static readonly Dictionary<MethodInfo, MethodInfo> Swaps = new Dictionary<MethodInfo, MethodInfo>
        {
            { AccessTools.Method(typeof(GenGrid), nameof(GenGrid.InBounds), new[] { typeof(IntVec3), typeof(Map) }), AccessTools.Method(typeof(BandScope), nameof(InBounds)) },
            { AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.Filled)), AccessTools.Method(typeof(BandScope), nameof(Filled)) },
        };

        // The method's IL calls one of these.
        public static bool CallsAny(MethodBase m, params MethodInfo[] callees)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(m).Any(p => (p.Key == OpCodes.Call || p.Key == OpCodes.Callvirt) && p.Value is MethodInfo mi && callees.Contains(mi));
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The source of an instance: a thing, a comp's parent, a hediff's or hediff comp's pawn.
        public static bool SourceOf(object o, out Map map, out IntVec3 cell)
        {
            Thing t = o switch
            {
                Thing th => th,
                ThingComp tc => tc.parent,
                HediffComp hc => hc.Pawn,
                Hediff h => h.pawn,
                _ => null
            };
            map = t?.MapHeld;
            cell = t == null ? IntVec3.Invalid : t.PositionHeld;
            return map != null;
        }
    }

    // The table (one row per effect; "scope" rows record the source, the others only run inside a scope).
    [HarmonyPatch]
    public static class BandScope_Patch
    {
        private static readonly HashSet<MethodBase> scoped = new HashSet<MethodBase>();

        // Test only (mode radial, R16): no scope pushed (the snow row off, to show why it stays).
        public static bool TestOff;

        // calls swapped per method (logged; the void test checks them)
        public static readonly Dictionary<string, int> PerMethod = new Dictionary<string, int>();
        public static readonly List<string> Missing = new List<string>();

        private static IEnumerable<MethodBase> Row(Type t, string name, bool withLambdas = true)
        {
            MethodInfo m = AccessTools.Method(t, name);
            if (m == null)
            {
                Missing.Add(t.Name + "." + name);
                yield break;
            }
            scoped.Add(m);
            yield return m;
            if (!withLambdas)
            {
                yield break;
            }
            string tag = "<" + name + ">";
            foreach (Type nt in new[] { t }.Concat(t.GetNestedTypes(AccessTools.all)))
            {
                foreach (MethodInfo l in nt.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly))
                {
                    if (l.Name.Contains(tag) && !l.IsAbstract && l.GetMethodBody() != null)
                    {
                        yield return l;
                    }
                }
            }
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            IEnumerable<MethodBase> all = Enumerable.Empty<MethodBase>()
                .Concat(Row(typeof(Tornado), "Tick"))
                .Concat(Row(typeof(CompSnowExpand), "TryExpandSnow"));
            MethodInfo[] swaps = BandScope.Swaps.Keys.ToArray();
            MethodBase row = null;
            bool rowHit = true;
            foreach (MethodBase m in all.Distinct())
            {
                if (scoped.Contains(m))
                {
                    if (!rowHit)
                    {
                        Missing.Add(row.DeclaringType.Name + "." + row.Name + " (no cell check)");
                    }
                    row = m;
                    rowHit = false;
                }
                if (BandScope.CallsAny(m, swaps))
                {
                    rowHit = true;
                    yield return m;
                }
            }
            if (!rowHit)
            {
                Missing.Add(row.DeclaringType.Name + "." + row.Name + " (no cell check)");
            }
            if (Missing.Count > 0)
            {
                Log.Warning("[ProjectZ] band scope: not found: " + string.Join(", ", Missing));
            }
        }

        public static void Prefix(object __instance, MethodBase __originalMethod, out bool __state)
        {
            __state = false;
            if (TestOff)
            {
                return;
            }
            if (__instance != null && scoped.Contains(__originalMethod) && BandScope.SourceOf(__instance, out Map map, out IntVec3 c) && ProjectZ.Annex(map) != null)
            {
                BandScope.Push(map, c);
                __state = true;
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                BandScope.Pop();
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            int n = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo mi && BandScope.Swaps.TryGetValue(mi, out MethodInfo with))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = with;
                    n++;
                }
                yield return ci;
            }
            PerMethod[original.DeclaringType.Name + "." + original.Name] = n;
        }
    }

    // RadialDistinctThingsAround keeps things on the centre's band (its iterator: InBounds with the centre).
    [HarmonyPatch]
    public static class RadialDistinctThings_Band_Patch
    {
        public static int Swapped;

        public static MethodBase TargetMethod()
        {
            return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(GenRadial), nameof(GenRadial.RadialDistinctThingsAround)));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            MethodInfo inBounds = AccessTools.Method(typeof(GenGrid), nameof(GenGrid.InBounds), new[] { typeof(IntVec3), typeof(Map) });
            FieldInfo centre = AccessTools.Field(original.DeclaringType, "center");
            if (centre == null)
            {
                Log.Warning("[ProjectZ] band scope: RadialDistinctThingsAround's iterator has no centre field; left as is.");
                foreach (CodeInstruction ci in instructions)
                {
                    yield return ci;
                }
                yield break;
            }
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(inBounds))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldfld, centre);
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(BandScope), nameof(BandScope.InBoundsNear));
                    Swapped++;
                }
                yield return ci;
            }
            if (Swapped != 1)
            {
                Log.Warning($"[ProjectZ] band scope: RadialDistinctThingsAround: {Swapped} InBounds calls swapped (expected 1)");
            }
        }
    }

    // Auras (CompCauseHediff_AoE: the mech booster, ...): the true 3D distance between floors (as facilities, explosions).
    [HarmonyPatch(typeof(CompCauseHediff_AoE), "IsPawnAffected")]
    public static class CauseHediffAoE_3D_Patch
    {
        public static int Swapped;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo dist = AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceTo), new[] { typeof(IntVec3), typeof(IntVec3) });
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(dist))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(CauseHediffAoE_3D_Patch), nameof(Distance));
                    Swapped++;
                }
                yield return ci;
            }
            if (Swapped != 1)
            {
                Log.Warning($"[ProjectZ] auras across floors: {Swapped} distance calls swapped in CompCauseHediff_AoE.IsPawnAffected (expected 1)");
            }
        }

        public static float Distance(IntVec3 a, IntVec3 b, ThingComp comp)
        {
            Map map = comp?.parent?.MapHeld;
            if (!VoidRule.Off && map != null && (Heights.CrossFloor(a, b, map) || ProjectZ.IsVoid(a, map) || ProjectZ.IsVoid(b, map)))
            {
                return Heights.CrossFloor(a, b, map) ? Range3D.Distance(a, b, map) : float.MaxValue;
            }
            return a.DistanceTo(b);
        }
    }

    // The mechanitor's command range: the true 3D distance between floors (vanilla: flat squared distance < 620.01).
    [HarmonyPatch(typeof(Pawn_MechanitorTracker), nameof(Pawn_MechanitorTracker.CanCommandTo))]
    public static class MechanitorRange_3D_Patch
    {
        public const float RangeSquared = 620.01f;

        public static bool Prefix(Pawn_MechanitorTracker __instance, LocalTargetInfo target, ref bool __result)
        {
            Pawn p = __instance.Pawn;
            Map map = p?.MapHeld;
            if (VoidRule.Off || map == null || ProjectZ.Annex(map) == null || !target.IsValid)
            {
                return true;
            }
            IntVec3 a = p.PositionHeld;
            IntVec3 b = target.Cell;
            if (!b.InBounds(map))
            {
                __result = false;
                return false;
            }
            if (ProjectZ.SameBand(a, b, map))
            {
                return true;
            }
            if (!Heights.CrossFloor(a, b, map))
            {
                __result = false;   // a void row
                return false;
            }
            float d = Range3D.Distance(a, b, map);
            __result = d * d < RangeSquared;
            return false;
        }
    }
}

namespace ProjectZ
{
    // Infestations per band (tall mountains only, Mountains.On; isorim-pz-mountains-design.md §2.1): a dug room in an
    // upper band is scored like one on the ground, from its own band. Rows:
    //   - InfestationCellFinder's mountainousness radial count, its straight lines to the open and its distance to a
    //     blocker: cells of another band are off the map (BandScope, the cell argument is the source). The radial count
    //     is also BandRadial's, which folds a cell past the band onto the void; the void is in bounds and would count as
    //     a sample without rock, so the score at a band edge would drop. This row keeps the map-edge rule: the void and
    //     other bands are not sampled (tallmtn F11);
    //   - the 30-cell distance to colony buildings (CellFinderUtility's Dijkstra): neighbours stay in their band.
    [HarmonyPatch]
    public static class InfestationBand_Patch
    {
        public static int Swapped;

        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in new[] { "GetMountainousnessScoreAt", "StraightLineDistToUnroofed", "DistToBlocker" })
            {
                MethodInfo m = AccessTools.Method(typeof(InfestationCellFinder), name);
                if (m == null)
                {
                    Log.Warning("[ProjectZ] infestations per band: InfestationCellFinder." + name + " not found");
                    continue;
                }
                yield return m;
            }
        }

        public static void Prefix(IntVec3 cell, Map map, out bool __state)
        {
            __state = Mountains.On(map);
            if (__state)
            {
                BandScope.Push(map, cell);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                BandScope.Pop();
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo inBounds = AccessTools.Method(typeof(GenGrid), nameof(GenGrid.InBounds), new[] { typeof(IntVec3), typeof(Map) });
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(inBounds))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(BandScope), nameof(BandScope.InBounds));
                    Swapped++;
                }
                yield return ci;
            }
        }
    }

    [HarmonyPatch]
    public static class ColonyDistanceBand_Patch
    {
        public static int Swapped;

        public static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo getter = AccessTools.Method(typeof(DijkstraUtility), nameof(DijkstraUtility.AdjacentCellsNeighborsGetter));
            string tag = "<" + nameof(CellFinderUtility.CalculateDistanceToColonyBuildingGrid) + ">";
            foreach (Type nt in new[] { typeof(CellFinderUtility) }.Concat(typeof(CellFinderUtility).GetNestedTypes(AccessTools.all)))
            {
                foreach (MethodInfo l in nt.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly))
                {
                    if (l.Name.Contains(tag) && l.GetMethodBody() != null && BandScope.CallsAny(l, getter))
                    {
                        yield return l;
                    }
                }
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo getter = AccessTools.Method(typeof(DijkstraUtility), nameof(DijkstraUtility.AdjacentCellsNeighborsGetter));
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(getter))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(ColonyDistanceBand_Patch), nameof(Neighbours));
                    Swapped++;
                }
                yield return ci;
            }
        }

        private static readonly List<IntVec3> same = new List<IntVec3>();

        public static IEnumerable<IntVec3> Neighbours(IntVec3 cell, Map map)
        {
            IEnumerable<IntVec3> all = DijkstraUtility.AdjacentCellsNeighborsGetter(cell, map);
            if (!Mountains.On(map))
            {
                return all;
            }
            same.Clear();
            foreach (IntVec3 c in all)
            {
                if (ProjectZ.SameBand(c, cell, map))
                {
                    same.Add(c);
                }
            }
            return same;
        }
    }
}

namespace ProjectZ
{
    // R23: a child hive stays on its parent's band (CompSpawnerHives.FindChildHiveLocation's three searches all ask
    // CanSpawnHiveAt; the radius reaches across the void into a dug room of the next band). Tall mountains only.
    [HarmonyPatch(typeof(CompSpawnerHives), "CanSpawnHiveAt")]
    public static class ChildHiveBand_Patch
    {
        public static void Postfix(IntVec3 c, Map map, IntVec3 parentPos, ref bool __result)
        {
            if (__result && Mountains.On(map) && !ProjectZ.SameBand(c, parentPos, map))
            {
                __result = false;
            }
        }
    }
}
