using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Patches
{
    // D9: drop pods, skyfallers and raids never land in the annex. (Hostiles may later walk up stairs; that is M1b.)

    [HarmonyPatch(typeof(DropCellFinder), nameof(DropCellFinder.IsGoodDropSpot))]
    public static class DropCellFinder_IsGoodDropSpot_Patch
    {
        public static bool Prefix(IntVec3 c, Map map, ref bool __result)
        {
            if (ProjectZ.IsAnnex(c, map))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(DropCellFinder), nameof(DropCellFinder.CanPhysicallyDropInto))]
    public static class DropCellFinder_CanPhysicallyDropInto_Patch
    {
        public static bool Prefix(IntVec3 c, Map map, ref bool __result)
        {
            if (ProjectZ.IsAnnex(c, map))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(DropCellFinder), nameof(DropCellFinder.SkyfallerCanLandAt))]
    public static class DropCellFinder_SkyfallerCanLandAt_Patch
    {
        public static bool Prefix(IntVec3 c, Map map, IntVec2 size, ref bool __result)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a != null && GenAdj.OccupiedRect(c, Rot4.North, size).maxZ >= a.groundZ)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    // Its last resort is map.AllCells.RandomElement().
    [HarmonyPatch(typeof(DropCellFinder), nameof(DropCellFinder.FindRaidDropCenterDistant))]
    public static class DropCellFinder_FindRaidDropCenterDistant_Patch
    {
        public static void Postfix(Map map, ref IntVec3 __result)
        {
            if (ProjectZ.IsAnnex(__result, map))
            {
                __result = CellFinder.RandomCell(map);
            }
        }
    }

    // Exit cells are the cells within 2 of an edge; rebuilt over the ground only, with the ground's top row as
    // the north edge (vanilla walks CellRect.WholeMap and would put the north exit band in the annex).
    [HarmonyPatch(typeof(ExitMapGrid), "Rebuild")]
    public static class ExitMapGrid_Rebuild_Patch
    {
        private static readonly AccessTools.FieldRef<ExitMapGrid, Map> MapRef = AccessTools.FieldRefAccess<ExitMapGrid, Map>("map");
        private static readonly AccessTools.FieldRef<ExitMapGrid, bool> DirtyRef = AccessTools.FieldRefAccess<ExitMapGrid, bool>("dirty");
        private static readonly AccessTools.FieldRef<ExitMapGrid, BoolGrid> GridRef = AccessTools.FieldRefAccess<ExitMapGrid, BoolGrid>("exitMapGrid");
        private static readonly AccessTools.FieldRef<ExitMapGrid, CellBoolDrawer> DrawerRef = AccessTools.FieldRefAccess<ExitMapGrid, CellBoolDrawer>("drawerInt");

        public static bool Prefix(ExitMapGrid __instance)
        {
            Map map = MapRef(__instance);
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return true;
            }
            DirtyRef(__instance) = false;
            BoolGrid grid = GridRef(__instance);
            if (grid == null || !grid.MapSizeMatches(map))
            {
                grid = new BoolGrid(map);
                GridRef(__instance) = grid;
            }
            else
            {
                grid.Clear();
            }
            int maxX = map.Size.x - 1;
            int maxZ = a.groundZ - 1;
            for (int z = 0; z <= maxZ; z++)
            {
                for (int x = 0; x <= maxX; x++)
                {
                    if (z > 1 && z < maxZ - 1 && x > 1 && x < maxX - 1)
                    {
                        x = maxX - 1;
                    }
                    IntVec3 c = new IntVec3(x, 0, z);
                    if (IsGoodExitCell(c, map))
                    {
                        grid[c] = true;
                    }
                }
            }
            DrawerRef(__instance)?.SetDirty();
            return false;
        }

        private static bool IsGoodExitCell(IntVec3 cell, Map map)
        {
            if (!cell.CanBeSeenOver(map))
            {
                return false;
            }
            int num = GenRadial.NumCellsInRadius(2f);
            for (int i = 0; i < num; i++)
            {
                IntVec3 c = cell + GenRadial.RadialPattern[i];
                if (c.InBounds(map) && c.OnEdge(map) && c.CanBeSeenOverFast(map) && GenSight.LineOfSight(cell, c, map))
                {
                    return true;
                }
            }
            return false;
        }
    }

    // A region touches the map edge at the ground's edges (outdoors / CanReachMapEdge). Vanilla checks the full
    // map size, so the ground's top row would not count and the annex's outer rows would.
    [HarmonyPatch(typeof(RegionMaker), "AddCell")]
    public static class RegionMaker_AddCell_Patch
    {
        private static readonly AccessTools.FieldRef<RegionMaker, Map> MapRef = AccessTools.FieldRefAccess<RegionMaker, Map>("map");
        private static readonly AccessTools.FieldRef<RegionMaker, Region> RegRef = AccessTools.FieldRefAccess<RegionMaker, Region>("newReg");

        public static void Postfix(RegionMaker __instance, IntVec3 c)
        {
            Map map = MapRef(__instance);
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return;
            }
            Region reg = RegRef(__instance);
            if (c.z < a.groundZ)
            {
                if (c.z == a.groundZ - 1)
                {
                    reg.touchesMapEdge = true;
                }
            }
            else if (reg.extentsClose.minZ >= a.groundZ)
            {
                // A region made only of annex cells (void or floor) never touches the outside world.
                reg.touchesMapEdge = false;
            }
        }
    }

    // The 2D camera stays over the ground. A dev/test switch lets the overview shot see the whole map.
    [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.Update))]
    public static class CameraDriver_Update_Patch
    {
        private static readonly AccessTools.FieldRef<CameraDriver, Vector3> RootPosRef = AccessTools.FieldRefAccess<CameraDriver, Vector3>("rootPos");

        public static bool AllowAnnexView;

        public static void Postfix(CameraDriver __instance)
        {
            if (AllowAnnexView || LongEventHandler.ShouldWaitForEvent)
            {
                return;
            }
            Map map = Find.CurrentMap;
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return;
            }
            ref Vector3 pos = ref RootPosRef(__instance);
            float maxZ = a.groundZ - 2f;
            if (pos.z > maxZ)
            {
                pos.z = maxZ;
                Traverse.Create(__instance).Method("ApplyPositionToGameObject").GetValue();
            }
        }
    }
}
