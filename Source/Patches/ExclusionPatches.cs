using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Patches
{
    // D9 (what is left of it): exit cells, the map edge and regions touching it, and the 2D camera stay on the ground.

    // Drop pods and skyfallers (user decision 2026-10-16, replaces D9's "never in the annex"): upper floors count like
    // the ground. A pod may come down on a walkable floor cell and punch a normal thin roof over an upper room, as on the
    // ground; it never comes down off the floors (void rows, open air) and never through a roof that never drops (a
    // floor's PZ_FloorRoof or the stairwell's, FloorRoof.Kept): no pod lands under a slab or falls from floor 2 into
    // floor 1. Those roofs stay thin otherwise (they collapse with their support). One rule, DropRules.Blocked, at the
    // game's own drop checks: CanPhysicallyDropInto (every drop finder and IsGoodDropSpot go through it),
    // SkyfallerCanLandAt (big skyfallers) and ShuttleCanLandHere (every shuttle landing cell: the call-shuttle and
    // call-laborers permits, a player shuttle's landing spot, shuttles arriving at a cell; it checks cells itself and only
    // refuses natural and thick roofs, so a shuttle could come down under a slab). The roof-punch itself already skips
    // kept roofs (FloorRoof.cs, DropRoofCells_Patch).
    public static class DropRules
    {
        // Test only (-projectznoshuttlefix): ShuttleCanLandHere without the rule, to show the old behaviour.
        public static bool ShuttleFixOff;

        public static bool Blocked(IntVec3 c, Map map)
        {
            if (ProjectZ.Annex(map) == null || !c.InBounds(map))
            {
                return false;
            }
            int k = ProjectZ.FloorOf(c, map);
            if (k < 0 || k >= 1 && !ProjectZ.HasFloor(c, map))
            {
                return true;
            }
            return FloorRoof.Kept(c, map);
        }
    }

    [HarmonyPatch(typeof(DropCellFinder), nameof(DropCellFinder.CanPhysicallyDropInto))]
    public static class DropCellFinder_CanPhysicallyDropInto_Patch
    {
        public static bool Prefix(IntVec3 c, Map map, ref bool __result)
        {
            if (DropRules.Blocked(c, map))
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
            if (ProjectZ.Annex(map) == null)
            {
                return true;
            }
            foreach (IntVec3 x in GenAdj.OccupiedRect(c, Rot4.North, size))
            {
                if (DropRules.Blocked(x, map))
                {
                    __result = false;
                    return false;
                }
            }
            return true;
        }
    }

    // The shuttle's own landing check, over the same cells it checks (footprint and interaction cell).
    [HarmonyPatch(typeof(RoyalTitlePermitWorker_CallShuttle), nameof(RoyalTitlePermitWorker_CallShuttle.ShuttleCanLandHere))]
    public static class ShuttleCanLandHere_Patch
    {
        public static void Postfix(LocalTargetInfo target, Map map, ThingDef shuttleDef, Rot4? rot, ref AcceptanceReport __result)
        {
            if (!__result.Accepted || DropRules.ShuttleFixOff || !target.IsValid || ProjectZ.Annex(map) == null)
            {
                return;
            }
            ThingDef def = shuttleDef ?? ThingDefOf.Shuttle;
            Rot4 r = rot ?? def.defaultPlacingRot;
            foreach (IntVec3 c in GenAdj.OccupiedRect(target.Cell, r, def.size))
            {
                if (DropRules.Blocked(c, map))
                {
                    __result = Refusal(c, map);
                    return;
                }
            }
            if (def.hasInteractionCell)
            {
                IntVec3 ic = ThingUtility.InteractionCellWhenAt(def, target.Cell, r, map);
                if (DropRules.Blocked(ic, map))
                {
                    __result = Refusal(ic, map);
                }
            }
        }

        private static AcceptanceReport Refusal(IntVec3 c, Map map)
        {
            string why = FloorRoof.Kept(c, map) ? "BlockedByRoof".Translate().CapitalizeFirst() : "ShuttleCannotLand_Unwalkable".Translate().CapitalizeFirst();
            return new AcceptanceReport("ShuttleCannotLand".Translate() + ": " + why);
        }
    }

    // Gravships land only on the ground (user decision 2026-10-16): no ship cell on an upper floor or a void row. Every
    // landing spot is checked cell by cell in the move designator's IsValidCell (the placement UI, which also covers
    // landing back on a map with a grav anchor); a new map puts the marker on its start spot, generated on the ground.
    // Ground cells under a slab are already refused by vanilla (its kept roof).
    [HarmonyPatch(typeof(Designator_MoveGravship), "IsValidCell")]
    public static class Designator_MoveGravship_IsValidCell_Patch
    {
        public static void Postfix(IntVec3 cell, Map map, ref AcceptanceReport __result)
        {
            if (cell.InBounds(map) && ProjectZ.IsAnnex(cell, map))
            {
                __result = "PZ_GravshipGroundOnly".Translate();
            }
        }
    }

    // Scattered drops (RandomDrop raids, random drop spots) pick any cell of the map like the ground does: the game's
    // random-cell finder is kept on the ground (M1a), so on maps with floors the spot is drawn over every floor's cells
    // (uniform, so a floor gets pods by its walkable open area) with the game's own conditions.
    [HarmonyPatch(typeof(DropCellFinder), nameof(DropCellFinder.RandomDropSpot))]
    public static class DropCellFinder_RandomDropSpot_Patch
    {
        public static bool Prefix(Map map, bool standableOnly, ref IntVec3 __result)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return true;
            }
            foreach (bool roofOk in new[] { false, true })
            {
                for (int i = 0; i < 3000; i++)
                {
                    IntVec3 g = new IntVec3(Rand.Range(0, map.Size.x), 0, Rand.Range(0, a.groundZ));
                    IntVec3 c = ProjectZ.CellOnFloor(g, Rand.Range(a.MinLevel, a.MaxLevel + 1), map);
                    if (c.IsValid && !DropRules.Blocked(c, map) && (!standableOnly || c.Standable(map)) && !c.GetTerrain(map).IsWater && (roofOk || !c.Roofed(map)) && !c.Fogged(map))
                    {
                        __result = c;
                        return false;
                    }
                }
            }
            return true;
        }
    }

    // Its last resort is map.AllCells.RandomElement(), which can be off the floors.
    [HarmonyPatch(typeof(DropCellFinder), nameof(DropCellFinder.FindRaidDropCenterDistant))]
    public static class DropCellFinder_FindRaidDropCenterDistant_Patch
    {
        public static void Postfix(Map map, ref IntVec3 __result)
        {
            if (DropRules.Blocked(__result, map))
            {
                for (int i = 0; i < 100 && DropRules.Blocked(__result, map); i++)
                {
                    __result = CellFinder.RandomCell(map);
                }
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
