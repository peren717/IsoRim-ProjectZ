using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ
{
    // Decision D6, as changed on 2026-10-16 (removal design §7): an upper floor is built where a roof could be held (the
    // game's support rules, SlabSupport), and from then on the roof under it IS the floor above. The cell below gets the
    // roof PZ_FloorRoof ("upper floor", thin), so rooms, weather, temperature, light and "indoors" treat it as roofed
    // through the game's own roof grid.
    //
    // While the floor is there its roof can't be taken away: the no-roof area, drop pods, mortar shells and tornadoes
    // leave it alone, and any other roof written to that cell becomes the floor's roof again. It does collapse like a
    // built roof when its support goes, and the floor falls with it (SlabCollapse). Removing the floor turns it back into
    // an ordinary constructed roof. Saves from before M1c (and anything that slipped through) are put right when the
    // map loads: every cell under a floor gets the floor's roof, also where it had none.
    public static class FloorRoof
    {
        // The cell right above c on the next floor has a floor on it.
        public static bool FloorAbove(IntVec3 c, Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || !c.InBounds(map))
            {
                return false;
            }
            int k = ProjectZ.FloorOf(c, map);
            if (k < 0 || k + 1 >= a.floors)
            {
                return false;
            }
            IntVec3 up = ProjectZ.CellOnFloor(ProjectZ.GroundCellOf(c, map), k + 1, map);
            return up.IsValid && ProjectZ.HasFloor(up, map);
        }

        // A roof that never drops: a floor's (the floor itself) or the stairwell's (an opening).
        public static bool Kept(IntVec3 c, Map map)
        {
            return c.InBounds(map) && (FloorAbove(c, map) || ProjectZ.Annex(map) != null && map.roofGrid.RoofAt(c) == PZDefOf.PZ_StairwellRoof);
        }

        // The cell under c on the floor below (c on an upper floor), else Invalid.
        public static IntVec3 Below(IntVec3 c, Map map)
        {
            int k = ProjectZ.FloorOf(c, map);
            if (k < 1)
            {
                return IntVec3.Invalid;
            }
            return ProjectZ.CellOnFloor(ProjectZ.GroundCellOf(c, map), k - 1, map);
        }

        // After a terrain change on an upper-floor cell: the roof under it follows.
        public static void Changed(IntVec3 c, Map map)
        {
            IntVec3 below = Below(c, map);
            if (!below.IsValid)
            {
                return;
            }
            RoofDef cur = map.roofGrid.RoofAt(below);
            if (ProjectZ.HasFloor(c, map))
            {
                if (cur != PZDefOf.PZ_FloorRoof)
                {
                    map.roofGrid.SetRoof(below, PZDefOf.PZ_FloorRoof);
                }
            }
            else if (cur == PZDefOf.PZ_FloorRoof)
            {
                map.roofGrid.SetRoof(below, RoofDefOf.RoofConstructed);
            }
        }

        // Every cell under a floor gets the floor's roof; floor roofs without a floor above become ordinary roofs.
        public static int Normalize(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return 0;
            }
            int fixedCells = 0;
            for (int k = 0; k < a.floors; k++)
            {
                for (int z = 0; z < a.groundZ; z++)
                {
                    for (int x = 0; x < map.Size.x; x++)
                    {
                        IntVec3 c = ProjectZ.CellOnFloor(new IntVec3(x, 0, z), k, map);
                        RoofDef cur = map.roofGrid.RoofAt(c);
                        bool above = FloorAbove(c, map);
                        if (above && cur != PZDefOf.PZ_FloorRoof)
                        {
                            map.roofGrid.SetRoof(c, PZDefOf.PZ_FloorRoof);
                            fixedCells++;
                        }
                        else if (!above && cur == PZDefOf.PZ_FloorRoof)
                        {
                            map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                            fixedCells++;
                        }
                    }
                }
            }
            if (fixedCells > 0)
            {
                ProjectZ.DebugLog($"map {map}: {fixedCells} roof cell(s) under upper floors put right");
            }
            return fixedCells;
        }
    }

    // Terrain changes on upper floors (built, removed, the stairs' landing): the roof below follows.
    [HarmonyPatch(typeof(TerrainGrid), "DoTerrainChangedEffects")]
    public static class TerrainChanged_FloorRoof_Patch
    {
        private static readonly AccessTools.FieldRef<TerrainGrid, Map> mapRef = AccessTools.FieldRefAccess<TerrainGrid, Map>("map");

        public static void Postfix(TerrainGrid __instance, IntVec3 c, TerrainDef oldTerr, TerrainDef newTerr)
        {
            Map map = mapRef(__instance);
            if (oldTerr == newTerr || map == null || !ProjectZ.IsAnnex(c, map))
            {
                return;
            }
            if (oldTerr == PZDefOf.PZ_OpenAir || newTerr == PZDefOf.PZ_OpenAir)
            {
                FloorRoof.Changed(c, map);
                FloorHeat.Dirty();
                SlabPeel.Dirty(map);
            }
        }
    }

    // Any roof written under a floor is the floor's roof (also "no roof": the floor stays the ceiling), except the game
    // dropping an unsupported one (SlabCollapse: the floor falls).
    [HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.SetRoof))]
    public static class SetRoof_FloorRoof_Patch
    {
        private static readonly AccessTools.FieldRef<RoofGrid, Map> mapRef = AccessTools.FieldRefAccess<RoofGrid, Map>("map");

        public static void Prefix(RoofGrid __instance, IntVec3 c, ref RoofDef def)
        {
            if (def == PZDefOf.PZ_FloorRoof)
            {
                FloorHeat.Dirty();
                return;
            }
            Map map = mapRef(__instance);
            if (map == null || ProjectZ.Annex(map) == null)
            {
                return;
            }
            FloorHeat.Dirty();
            if (FloorRoof.FloorAbove(c, map))
            {
                if (def == null && SlabCollapse.LetRoofGo(c, map))
                {
                    return;
                }
                def = PZDefOf.PZ_FloorRoof;
            }
            else
            {
                def = Stairwell.RoofFor(c, map, def);
            }
        }

        // A roof changed under open air: where pawns can land from above changed (JumpGrid, reachability).
        public static void Postfix(RoofGrid __instance, IntVec3 c)
        {
            Map map = mapRef(__instance);
            if (map != null && ProjectZ.Annex(map) != null)
            {
                JumpGrid.ColumnChanged(c, map);
            }
        }
    }

    // No roof-removal jobs under a floor.
    [HarmonyPatch(typeof(WorkGiver_RemoveRoof), nameof(WorkGiver_RemoveRoof.HasJobOnCell))]
    public static class RemoveRoof_FloorRoof_Patch
    {
        public static void Postfix(Pawn pawn, IntVec3 c, ref bool __result)
        {
            if (__result && FloorRoof.FloorAbove(c, pawn.Map))
            {
                __result = false;
            }
        }
    }

    // Drop pods, shells and tornadoes don't drop a floor's roof, nor the stairwell's (it is an opening: there is nothing
    // up there to fall, Column.OpenAbove). A collapse for want of support (the game's collapse buffer, the list
    // overload) drops a floor's roof with its floor (SlabCollapse).
    [HarmonyPatch(typeof(RoofCollapserImmediate), nameof(RoofCollapserImmediate.DropRoofInCells), new[] { typeof(IntVec3), typeof(Map), typeof(List<Thing>) })]
    public static class DropRoofCell_Patch
    {
        public static bool Prefix(IntVec3 c, Map map)
        {
            return !FloorRoof.Kept(c, map);
        }
    }

    [HarmonyPatch(typeof(RoofCollapserImmediate), nameof(RoofCollapserImmediate.DropRoofInCells), new[] { typeof(IEnumerable<IntVec3>), typeof(Map), typeof(List<Thing>) })]
    public static class DropRoofCells_Patch
    {
        public static void Prefix(ref IEnumerable<IntVec3> cells, Map map)
        {
            if (cells != null && ProjectZ.Annex(map) != null)
            {
                cells = cells.Where(c => !FloorRoof.Kept(c, map)).ToList();
            }
        }
    }

    [HarmonyPatch(typeof(RoofCollapserImmediate), nameof(RoofCollapserImmediate.DropRoofInCells), new[] { typeof(List<IntVec3>), typeof(Map), typeof(List<Thing>) })]
    public static class DropRoofCellList_Patch
    {
        public static void Prefix(ref List<IntVec3> cells, Map map, List<Thing> outCrushedThings)
        {
            if (cells == null || ProjectZ.Annex(map) == null)
            {
                return;
            }
            if (SlabCollapse.Resolving)
            {
                // (by index: walls falling with a slab add the roofs they held to this same list, the buffer's own)
                for (int i = 0; i < cells.Count; i++)
                {
                    IntVec3 c = cells[i];
                    if (c.InBounds(map) && map.roofGrid.RoofAt(c) == PZDefOf.PZ_FloorRoof && FloorRoof.FloorAbove(c, map))
                    {
                        SlabCollapse.BufferCollapse(c, map, outCrushedThings);
                    }
                }
                if (cells.Any(c => c.InBounds(map) && map.roofGrid.RoofAt(c) == PZDefOf.PZ_StairwellRoof))
                {
                    cells = cells.Where(c => !c.InBounds(map) || map.roofGrid.RoofAt(c) != PZDefOf.PZ_StairwellRoof).ToList();
                }
                return;
            }
            if (cells.Any(c => c.InBounds(map) && FloorRoof.Kept(c, map)))
            {
                cells = cells.Where(c => !c.InBounds(map) || !FloorRoof.Kept(c, map)).ToList();
            }
        }
    }
}
