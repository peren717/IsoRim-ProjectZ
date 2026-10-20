using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Utility;
using Verse;

namespace ProjectZ.Patches
{
    // Ground spreaders (user decision 2026-10-19): every terrain or thing spreader that starts on the ground spreads into,
    // and writes onto, ground cells only. One predicate (GroundSpread.Ground: not an annex cell, the GroundFinders
    // convention), applied where each spreader decides on a cell; on the ground every check is vanilla's own.
    //   Odyssey floods            Flood.CanFloodPotentiallySpreadInto (CanFloodSpreadInto and every other caller build on it)
    //   Odyssey lava flow         GameCondition_LavaFlow.CanLavaPotentiallySpreadInto (CanLavaSpreadInto builds on it)
    //   Odyssey lava emergence    LavaEmergence.CanLavaSpreadInto (also LavaEmergenceImmediate)
    //   Anomaly fleshmass         CompGrowsFleshmassTendrils.CanPlaceFleshmass
    //   lump writers              VoidAwakeningUtility.SpawnVoidMetalAround, CompDeepScanner.DoFind: their
    //                             GridShapeMaker.IrregularLump call swapped for GroundSpread.GroundLump (vanilla's lump, its
    //                             cells above the ground dropped; the ground part is the same lump)
    // Without floors each check is vanilla's.
    public static class GroundSpread
    {
        // Test only (-projectzgroundspreadoff, or set by a check for a moment): the spreaders as vanilla, to show the old leaks.
        public static bool Off = GenCommandLine.CommandLineArgPassed("projectzgroundspreadoff");

        // Test only: how often each hook ran (0 after a real spread = the check was inlined into its caller).
        public static int FloodCalls, LavaFlowCalls, LavaEmergenceCalls, FleshmassCalls, LumpCalls;

        public static bool Ground(IntVec3 c, Map map) => Off || map == null || !ProjectZ.IsAnnex(c, map);

        // GridShapeMaker.IrregularLump for a writer that starts on the ground.
        public static List<IntVec3> GroundLump(IntVec3 center, Map map, int numCells, Predicate<IntVec3> validator)
        {
            List<IntVec3> lump = GridShapeMaker.IrregularLump(center, map, numCells, validator);
            LumpCalls++;
            if (!Off && ProjectZ.HasAnnex(map))
            {
                lump.RemoveAll(c => ProjectZ.IsAnnex(c, map));
            }
            return lump;
        }
    }

    [HarmonyPatch(typeof(Flood), "CanFloodPotentiallySpreadInto")]
    public static class Flood_GroundSpread_Patch
    {
        public static void Postfix(Flood __instance, IntVec3 cell, ref bool __result)
        {
            GroundSpread.FloodCalls++;
            if (__result && !GroundSpread.Ground(cell, __instance.Map))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(GameCondition_LavaFlow), "CanLavaPotentiallySpreadInto")]
    public static class LavaFlow_GroundSpread_Patch
    {
        public static void Postfix(GameCondition_LavaFlow __instance, IntVec3 c, ref bool __result)
        {
            GroundSpread.LavaFlowCalls++;
            if (__result && !GroundSpread.Ground(c, __instance.gameConditionManager?.ownerMap))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(LavaEmergence), "CanLavaSpreadInto")]
    public static class LavaEmergence_GroundSpread_Patch
    {
        public static void Postfix(LavaEmergence __instance, IntVec3 c, ref bool __result)
        {
            GroundSpread.LavaEmergenceCalls++;
            if (__result && !GroundSpread.Ground(c, __instance.Map))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(CompGrowsFleshmassTendrils), "CanPlaceFleshmass")]
    public static class Fleshmass_GroundSpread_Patch
    {
        public static void Postfix(CompGrowsFleshmassTendrils __instance, IntVec3 cell, ref bool __result)
        {
            GroundSpread.FleshmassCalls++;
            if (__result && !GroundSpread.Ground(cell, __instance.parent?.Map))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch]
    public static class GroundLump_Patch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(VoidAwakeningUtility), nameof(VoidAwakeningUtility.SpawnVoidMetalAround));
            yield return AccessTools.Method(typeof(CompDeepScanner), "DoFind");
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            MethodInfo lump = AccessTools.Method(typeof(GridShapeMaker), nameof(GridShapeMaker.IrregularLump));
            MethodInfo with = AccessTools.Method(typeof(GroundSpread), nameof(GroundSpread.GroundLump));
            int n = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(lump))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = with;
                    n++;
                }
                yield return ci;
            }
            if (n == 0)
            {
                Log.Warning("[ProjectZ] ground spread: no IrregularLump call in " + original.DeclaringType.Name + "." + original.Name + " (left as vanilla)");
            }
        }
    }
}
