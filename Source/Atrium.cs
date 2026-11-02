using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // Atriums (player request 2026-11-02: open multi-storey halls, warehouses, arenas, caves). A cell whose roof is
    // taken away while the column above it is still covered (the cell right above it, one floor up, has any roof: a
    // floor's, a built or rock roof, or itself an atrium's) does not open to the sky: it gets the invisible stairwell
    // roof (PZ_StairwellRoof) as stairwells do. Indoors, no rain or snow, indoor temperature, no sun for plants; drawn
    // invisible (IsoRim Api.HiddenRoof), so the floors above look down into it; shots, sight and falls pass
    // (Column.OpenAbove). The two storeys stay separate rooms (each its own temperature).
    //
    // One rule (Stairwell.RoofFor, applied by FloorRoof's SetRoof prefix to every roof written outside a floor's): no roof
    // under a covered column is the stairwell roof; the stairwell roof outside an opening stays while the column is
    // covered. The column changing is a roof changing in it (a floor's roof included), so the SetRoof postfix asks the
    // cell below again (Recheck), down the column: cover gone -> open sky, cover come -> atrium. Saves are put right at
    // load (Normalize). A roof built over an atrium cell (Build roof area) closes it with a real roof (patches below);
    // Remove roof refuses the stairwell roof (RemoveRoof_FloorRoof_Patch), so a no-roof area left there doesn't loop.
    public static class Atrium
    {
        // (tests) cells that turned into an atrium / back to the sky by Recheck
        public static int Covers, Uncovers;

        // The column over c is covered: the cell right above it (one floor up) has a roof.
        public static bool Covered(IntVec3 c, Map map)
        {
            if (map == null || ProjectZ.Annex(map) == null)
            {
                return false;
            }
            IntVec3 up = FloorRoof.Above(c, map);
            return up.IsValid && up.InBounds(map) && map.roofGrid.Roofed(up);
        }

        // c is an atrium cell: the stairwell roof that is no opening (and no floor over it).
        public static bool Is(IntVec3 c, Map map)
        {
            return map != null && c.InBounds(map) && ProjectZ.Annex(map) != null && map.roofGrid.RoofAt(c) == PZDefOf.PZ_StairwellRoof
                && !FloorRoof.FloorAbove(c, map) && !Stairwell.Well(c, map);
        }

        // A roof changed at c: the cell under it follows the rule (and so on down, through SetRoof).
        public static void Recheck(IntVec3 c, Map map)
        {
            IntVec3 b = FloorRoof.Below(c, map);
            if (!b.IsValid || !b.InBounds(map) || FloorRoof.FloorAbove(b, map))
            {
                return;
            }
            RoofDef cur = map.roofGrid.RoofAt(b);
            if (cur == null)
            {
                if (Covered(b, map))
                {
                    Covers++;
                    map.roofGrid.SetRoof(b, null);   // the SetRoof prefix makes it the atrium's
                }
            }
            else if (cur == PZDefOf.PZ_StairwellRoof && !Covered(b, map) && !Stairwell.Well(b, map))
            {
                Uncovers++;
                map.roofGrid.SetRoof(b, null);       // open sky
            }
        }

        // (tests) cells put right by the last Normalize: made atriums / opened to the sky
        public static int LoadCovered, LoadOpened;

        // Load: the column rule over the whole map, top down (saves from before atriums, or anything that slipped
        // through): no roof under a covered column is the atrium's; an atrium's roof under no cover is open sky again.
        // Openings (stairs, shafts, elevator and ladder columns: Stairwell.Well) and cells under a floor are left alone.
        // Run when loading has finished (LongEventHandler), so the stairs and elevator links are known.
        public static void NormalizeLater(Map map)
        {
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                if (map != null && Find.Maps.Contains(map))
                {
                    Normalize(map);
                }
            });
        }

        public static int Normalize(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || PZDefOf.PZ_StairwellRoof == null)
            {
                return 0;
            }
            int covered = 0, opened = 0;
            for (int k = a.MaxLevel; k >= a.MinLevel; k--)
            {
                for (int z = 0; z < a.groundZ; z++)
                {
                    for (int x = 0; x < map.Size.x; x++)
                    {
                        IntVec3 c = ProjectZ.CellOnFloor(new IntVec3(x, 0, z), k, map);
                        if (!c.IsValid || !c.InBounds(map) || FloorRoof.FloorAbove(c, map))
                        {
                            continue;
                        }
                        RoofDef r = map.roofGrid.RoofAt(c);
                        if (r == null && Covered(c, map))
                        {
                            map.roofGrid.SetRoof(c, null);   // (the SetRoof prefix: the atrium's roof)
                            covered++;
                        }
                        else if (r == PZDefOf.PZ_StairwellRoof && !Covered(c, map) && !Stairwell.Well(c, map))
                        {
                            map.roofGrid.SetRoof(c, null);   // open sky
                            opened++;
                        }
                    }
                }
            }
            LoadCovered = covered;
            LoadOpened = opened;
            if (covered + opened > 0)
            {
                ProjectZ.DebugLog($"map {map}: atrium rule at load: {covered} cell(s) under a covered column made atriums, {opened} opened to the sky");
            }
            return covered + opened;
        }
    }

    // Build roof over an atrium cell: the stairwell roof there counts as none (vanilla asks c.Roofed), so the player can
    // close it with a real roof.
    [HarmonyPatch(typeof(WorkGiver_BuildRoof), nameof(WorkGiver_BuildRoof.HasJobOnCell))]
    public static class BuildRoof_Atrium_Patch
    {
        private static readonly Func<WorkGiver_BuildRoof, IntVec3, Pawn, Building> toTouch =
            AccessTools.MethodDelegate<Func<WorkGiver_BuildRoof, IntVec3, Pawn, Building>>(AccessTools.Method(typeof(WorkGiver_BuildRoof), "BuildingToTouchToBeAbleToBuildRoof"));

        public static void Postfix(WorkGiver_BuildRoof __instance, Pawn pawn, IntVec3 c, bool forced, ref bool __result)
        {
            Map map = pawn?.Map;
            if (__result || map == null || !map.areaManager.BuildRoof[c] || !Atrium.Is(c, map))
            {
                return;
            }
            if (!pawn.CanReserve(c, 1, -1, ReservationLayerDefOf.Ceiling, forced))
            {
                return;
            }
            if (!pawn.CanReach(c, PathEndMode.Touch, pawn.NormalMaxDanger()) && toTouch(__instance, c, pawn) == null)
            {
                return;
            }
            if (!RoofCollapseUtility.WithinRangeOfRoofHolder(c, map) || !RoofCollapseUtility.ConnectedToRoofHolder(c, map, assumeRoofAtRoot: true))
            {
                return;
            }
            Thing thing = RoofUtility.FirstBlockingThing(c, map);
            __result = thing == null || RoofUtility.CanHandleBlockingThing(thing, pawn, forced);
        }
    }

    [HarmonyPatch(typeof(JobDriver_BuildRoof), "DoWorkFailOn")]
    public static class BuildRoofFail_Atrium_Patch
    {
        public static void Postfix(JobDriver_BuildRoof __instance, ref bool __result)
        {
            if (__result && Atrium.Is(__instance.job.targetA.Cell, __instance.pawn?.Map))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(JobDriver_BuildRoof), "DoEffect")]
    public static class BuildRoofEffect_Atrium_Patch
    {
        public static int Built;

        public static void Postfix(JobDriver_BuildRoof __instance)
        {
            Map map = __instance.pawn?.Map;
            if (map == null || ProjectZ.Annex(map) == null)
            {
                return;
            }
            IntVec3 cell = __instance.job.targetA.Cell;
            for (int i = 0; i < 9; i++)
            {
                IntVec3 c = cell + GenAdj.AdjacentCellsAndInside[i];
                if (!c.InBounds(map) || !map.areaManager.BuildRoof[c] || !Atrium.Is(c, map) || !RoofCollapseUtility.WithinRangeOfRoofHolder(c, map)
                    || RoofUtility.FirstBlockingThing(c, map) != null)
                {
                    continue;
                }
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                MoteMaker.PlaceTempRoof(c, map);
                Built++;
            }
        }
    }
}
