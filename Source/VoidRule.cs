using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ
{
    // The void rows as a virtual wall (isorim-pz-void-barrier.md, option F; user decisions 2026-10-17). The void rows are
    // already PZ_OpenAir (impassable, never drawn, no snow, filth, pollution or fire). The game stops gas, light and sight
    // only with buildings, and makes impassable cells without a wall into "free air exchange" regions that join the rooms
    // next to them (so every band's open air was one room with the ground through the void). A void cell gets what a
    // full, light-blocking wall gives, with no Thing spawned or saved:
    //   1. no region (GetExpectedRegionType -> None): each band's open air is its own outdoor room;
    //   2. the glow grid's light-blocker bit, set on every load (Apply);
    //   3. no gas (GasCanMoveTo false; gas already there on load is cleared);
    //   4. no flat line of sight between two bands (GenSight.LineOfSight false): sight across floors only through
    //      ProjectZ's own 3D rule (Los3D), also with the combat module off;
    //   5. no wall temperature exchange with another band (a one-row gap made the equalize cell two rows out land there).
    // Radius effects (tornado, auras, shields, ...) are a separate class: BandScope.cs.
    public static class VoidRule
    {
        // Test only (-projectznovoidrule): none of the rules (also BandScope and the 3D shields/auras), to show the old leaks.
        public static bool Off;

        public static int LastCells;
        public static int LastGasCleared;
        public static double LastApplyMs;

        // Void cell on a map with floors (cheap: the cached annex and two compares).
        public static bool Blocks(IntVec3 c, Map map) => !Off && ProjectZ.IsVoid(c, map);

        // On every load and after generation (AnnexMapComponent.FinalizeInit): light blockers on, gas out.
        public static void Apply(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (Off || a == null)
            {
                return;
            }
            Stopwatch sw = Stopwatch.StartNew();
            int cells = 0, gas = 0;
            int sx = map.Size.x;
            // every void row (the rows after each band, IsVoid)
            for (int z = a.groundZ; z < map.Size.z; z++)
            {
                if (!ProjectZ.IsVoid(new IntVec3(0, 0, z), map))
                {
                    continue;
                }
                {
                    for (int x = 0; x < sx; x++)
                    {
                        IntVec3 c = new IntVec3(x, 0, z);
                        // (before the first glow pass this only sets the bit; later it also re-lights around the cell)
                        map.glowGrid.LightBlockerAdded(c);
                        if (map.gasGrid.AnyGasAt(c))
                        {
                            map.gasGrid.ClearCellUnsafe(c);
                            map.mapDrawer.MapMeshDirty(c, MapMeshFlagDefOf.Gas);
                            gas++;
                        }
                        cells++;
                    }
                }
            }
            LastCells = cells;
            LastGasCleared = gas;
            LastApplyMs = sw.Elapsed.TotalMilliseconds;
            if (gas > 0)
            {
                Log.Message($"[ProjectZ] {gas} void cell(s) held gas (a save from before the void rule): cleared.");
            }
            ProjectZ.DebugLog($"void rule: {cells} void cells marked as light blockers, {gas} gas cell(s) cleared, {LastApplyMs:0.0} ms");
        }

        private static readonly AccessTools.FieldRef<GasGrid, Map> gasMap = AccessTools.FieldRefAccess<GasGrid, Map>("map");

        public static Map MapOf(GasGrid g) => gasMap(g);

        // GasGrid's own calls to GasCanMoveTo go through here (a small method: it may be inlined into them).
        public static bool GasMove(GasGrid g, IntVec3 c)
        {
            if (Blocks(c, gasMap(g)))
            {
                return false;
            }
            return g.GasCanMoveTo(c);
        }
    }

    // 1. No region in the void.
    [HarmonyPatch(typeof(RegionTypeUtility), nameof(RegionTypeUtility.GetExpectedRegionType))]
    public static class GetExpectedRegionType_Void_Patch
    {
        public static void Postfix(IntVec3 c, Map map, ref RegionType __result)
        {
            if (__result != RegionType.None && VoidRule.Blocks(c, map))
            {
                __result = RegionType.None;
            }
        }
    }

    // 3. No gas in the void (every caller outside GasGrid, and GasGrid's flood fills, which take it as a delegate).
    [HarmonyPatch(typeof(GasGrid), nameof(GasGrid.GasCanMoveTo))]
    public static class GasCanMoveTo_Void_Patch
    {
        public static void Postfix(GasGrid __instance, IntVec3 cell, ref bool __result)
        {
            if (__result && VoidRule.Blocks(cell, VoidRule.MapOf(__instance)))
            {
                __result = false;
            }
        }
    }

    // ... and GasGrid's direct calls (AddGas, diffusion, the building equalizer, debug fill), in case it is inlined there.
    [HarmonyPatch]
    public static class GasGridCalls_Void_Patch
    {
        private static readonly MethodInfo canMove = AccessTools.Method(typeof(GasGrid), nameof(GasGrid.GasCanMoveTo));
        private static readonly MethodInfo with = AccessTools.Method(typeof(VoidRule), nameof(VoidRule.GasMove));

        public static readonly Dictionary<string, int> PerMethod = new Dictionary<string, int>();

        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(typeof(GasGrid)))
            {
                if (m != canMove && !m.IsAbstract && BandScope.CallsAny(m, canMove))
                {
                    yield return m;
                }
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            int n = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(canMove))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = with;
                    n++;
                }
                yield return ci;
            }
            PerMethod[original.Name] = n;
        }
    }

    // 4. No flat line of sight between two bands (both full overloads; the 3-argument one forwards to the first).
    [HarmonyPatch]
    public static class LineOfSight_Void_Patch
    {
        public static int Refused;   // counter (tests)

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GenSight), nameof(GenSight.LineOfSight),
                new[] { typeof(IntVec3), typeof(IntVec3), typeof(Map), typeof(bool), typeof(System.Func<IntVec3, bool>), typeof(int), typeof(int) });
            yield return AccessTools.Method(typeof(GenSight), nameof(GenSight.LineOfSight),
                new[] { typeof(IntVec3), typeof(IntVec3), typeof(Map), typeof(CellRect), typeof(CellRect), typeof(System.Func<IntVec3, bool>), typeof(bool) });
        }

        public static bool Prefix(IntVec3 start, IntVec3 end, Map map, ref bool __result)
        {
            if (VoidRule.Off || ProjectZ.SameBand(start, end, map))
            {
                return true;
            }
            Refused++;
            __result = false;
            return false;
        }
    }

    // 5. A room's wall-exchange cells (two cells out past a wall) stay in its own band, as at the map edge.
    [HarmonyPatch(typeof(RoomTempTracker), "RegenerateEqualizationData")]
    public static class RegenerateEqualization_Void_Patch
    {
        private static readonly AccessTools.FieldRef<RoomTempTracker, Room> roomRef = AccessTools.FieldRefAccess<RoomTempTracker, Room>("room");

        public static int Dropped;   // counter (tests)

        public static void Postfix(RoomTempTracker __instance)
        {
            if (VoidRule.Off)
            {
                return;
            }
            Room room = roomRef(__instance);
            Map map = room?.Map;
            List<IntVec3> cells = __instance.EqualizeCellsForReading;
            if (map == null || cells.Count == 0 || ProjectZ.Annex(map) == null || room.CellCount == 0)
            {
                return;
            }
            IntVec3 any = room.Cells.First();
            Dropped += cells.RemoveAll(c => !ProjectZ.SameBand(c, any, map));
        }
    }
}
