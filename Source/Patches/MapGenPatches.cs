using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse.AI;
using Verse;

namespace ProjectZ.Patches
{
    // Design §2.3 A, "shrink during generation":
    //  1. GenerateMap prefix: the map is allocated with z = floors * H (all floors up front in this prototype).
    //  2. ConstructComponents postfix: every grid now has the full size; map.info.Size is set back to the ground size,
    //     so all genSteps (rivers, roads, ruins, mods) and Scenario.PostMapGenerate see a normal map.
    //  3. FinalizeInit prefix: the full size is restored and the annex is filled with open air, before regions,
    //     path costs and drawing are built.
    internal static class AnnexGen
    {
        internal sealed class Pending
        {
            public IntVec3 groundSize;
            public int stride;
            public int floors;
            public Map map;      // set once ConstructComponents ran
            public bool shrunk;
        }

        internal static Pending pending;

        // Annex cells: open air, unfogged, nothing else (no roof, no snow, no things). Written straight into the
        // terrain grid (SetTerrain's per-cell notifications aren't needed). Map.Size must be the full size.
        // Returns how many cells were not open air or were fogged before.
        internal static int FillAnnex(Map map, int groundZ, bool withPathCosts)
        {
            TerrainDef[] top = map.terrainGrid.topGrid;
            TerrainDef air = PZDefOf.PZ_OpenAir;
            CellIndices ci = map.cellIndices;
            int changed = 0;
            for (int i = ci.CellToIndex(new IntVec3(0, 0, groundZ)); i < ci.NumGridCells; i++)
            {
                bool fogged = map.fogGrid.IsFogged(i);
                if (top[i] != air || fogged)
                {
                    changed++;
                }
                top[i] = air;
                IntVec3 c = ci.IndexToCell(i);
                if (fogged)
                {
                    map.fogGrid.Unfog(c);
                }
                if (withPathCosts)
                {
                    map.pathing.RecalculatePerceivedPathCostAt(c);
                }
            }
            return changed;
        }
    }

    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
    public static class MapGenerator_GenerateMap_Patch
    {
        [HarmonyPriority(Priority.Last)]
        public static void Prefix(ref IntVec3 mapSize, MapGeneratorDef mapGenerator, bool isPocketMap, bool stepDebugger)
        {
            AnnexGen.pending = null;
            int floors = ProjectZ.FloorsForNewMaps;
            if (floors <= 1 || isPocketMap || stepDebugger || mapSize.z <= 0)
            {
                return;
            }
            int stride = ProjectZ.StrideFor(mapSize.z);
            AnnexGen.pending = new AnnexGen.Pending { groundSize = mapSize, stride = stride, floors = floors };
            ProjectZ.DebugLog($"generating {mapGenerator?.defName} map: ground {mapSize.x}x{mapSize.z}, stride {stride}, floors {floors} -> {mapSize.x}x{floors * stride}");
            mapSize = new IntVec3(mapSize.x, mapSize.y, floors * stride);
        }

        public static void Finalizer(Exception __exception)
        {
            AnnexGen.Pending p = AnnexGen.pending;
            AnnexGen.pending = null;
            if (p?.map != null && p.shrunk)
            {
                // Generation threw before FinalizeInit: put the real size back so the grids and Size agree.
                p.map.info.Size = new IntVec3(p.groundSize.x, p.groundSize.y, p.floors * p.stride);
                Log.Warning("[ProjectZ] map generation ended early; restored the full map size.");
            }
        }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.ConstructComponents))]
    public static class Map_ConstructComponents_Patch
    {
        public static void Postfix(Map __instance)
        {
            AnnexGen.Pending p = AnnexGen.pending;
            if (p == null || p.map != null || !ReferenceEquals(MapGenerator.mapBeingGenerated, __instance))
            {
                return;
            }
            p.map = __instance;
            AnnexMapComponent comp = __instance.GetComponent<AnnexMapComponent>();
            if (comp == null)
            {
                Log.Error("[ProjectZ] AnnexMapComponent missing; map generated without annex rows in use.");
                AnnexGen.pending = null;
                return;
            }
            comp.Set(p.groundSize.z, p.stride, p.floors);
            // Annex terrain and path costs are set now, at full size: genSteps that pathfind (roads) gather path
            // data over every grid cell, and the annex must already read as impassable open air there.
            Map map = __instance;
            AnnexGen.FillAnnex(map, p.groundSize.z, withPathCosts: true);
            map.info.Size = p.groundSize;
            p.shrunk = true;
        }
    }

    // Pathfinding during generation (GenStep_Roads ...) works on the full grids: run it at the full size.
    [HarmonyPatch(typeof(PathFinder), nameof(PathFinder.FindPathNow), new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathFinderCostTuning?), typeof(PathEndMode), typeof(PathRequest.IPathGridCustomizer) })]
    public static class PathFinder_FindPathNow_Patch
    {
        private static readonly AccessTools.FieldRef<PathFinder, Map> MapRef = AccessTools.FieldRefAccess<PathFinder, Map>("map");

        public static void Prefix(PathFinder __instance, out bool __state)
        {
            __state = false;
            AnnexGen.Pending p = AnnexGen.pending;
            if (p != null && p.shrunk && ReferenceEquals(p.map, MapRef(__instance)))
            {
                p.map.info.Size = new IntVec3(p.groundSize.x, p.groundSize.y, p.floors * p.stride);
                __state = true;
            }
        }

        public static void Finalizer(bool __state)
        {
            AnnexGen.Pending p = AnnexGen.pending;
            if (__state && p?.map != null)
            {
                p.map.info.Size = p.groundSize;
            }
        }
    }

    // Areas made while the map is shrunk (AddStartingAreas, genSteps) get full-size grids at once.
    [HarmonyPatch(typeof(Area), MethodType.Constructor, new[] { typeof(AreaManager) })]
    public static class Area_Ctor_Patch
    {
        private static readonly AccessTools.FieldRef<Area, BoolGrid> GridRef = AccessTools.FieldRefAccess<Area, BoolGrid>("innerGrid");

        public static void Postfix(Area __instance, AreaManager areaManager)
        {
            AnnexGen.Pending p = AnnexGen.pending;
            if (p != null && p.shrunk && ReferenceEquals(p.map, areaManager?.map))
            {
                GridRef(__instance)?.ClearAndResizeTo(p.groundSize.x, p.floors * p.stride);
            }
        }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.FinalizeInit))]
    public static class Map_FinalizeInit_Patch
    {
        // WaterInfo.riverFlowMap is laid out x-major (index x * Size.z + z, two floats per cell), so unlike the
        // z-major cell grids its layout depends on the height: copy the ground's columns into a full-height map.
        private static void RemapRiverFlow(Map map, int groundZ)
        {
            List<float> flow = map.waterInfo?.riverFlowMap;
            int sx = map.Size.x;
            int sz = map.Size.z;
            if (flow == null || flow.Count == 0)
            {
                return;
            }
            if (flow.Count != sx * groundZ * 2)
            {
                Log.Warning($"[ProjectZ] river flow map has {flow.Count} values, expected {sx * groundZ * 2}; left as is.");
                return;
            }
            float[] full = new float[sx * sz * 2];
            for (int x = 0; x < sx; x++)
            {
                for (int z = 0; z < groundZ; z++)
                {
                    int from = (x * groundZ + z) * 2;
                    int to = (x * sz + z) * 2;
                    full[to] = flow[from];
                    full[to + 1] = flow[from + 1];
                }
            }
            map.waterInfo.riverFlowMap = new List<float>(full);
            ProjectZ.DebugLog($"river flow map remapped to {sx}x{sz}");
        }

        [HarmonyPriority(Priority.First)]
        public static void Prefix(Map __instance)
        {
            AnnexGen.Pending p = AnnexGen.pending;
            if (p == null || !p.shrunk || !ReferenceEquals(p.map, __instance))
            {
                return;
            }
            p.shrunk = false;
            Map map = __instance;
            map.info.Size = new IntVec3(p.groundSize.x, p.groundSize.y, p.floors * p.stride);
            int groundZ = p.groundSize.z;
            ProjectZ.Invalidate();
            // Lazily built during generation from the ground size: rebuild over the full map, before anything
            // can index an annex cell.
            map.cellsInRandomOrder = new MapCellsInRandomOrder(map);
            int fixedGrids = GridFixer.Fix(map, p.groundSize.x * groundZ);
            RemapRiverFlow(map, groundZ);
            // Again after generation (nothing should have touched it): path costs, regions and section meshes are
            // all rebuilt right after this in FinalizeInit.
            int changed = AnnexGen.FillAnnex(map, groundZ, withPathCosts: false);
            ProjectZ.DebugLog($"annex checked: {changed} cell(s) changed during generation, {fixedGrids} ground-sized grid(s) resized, map size now {map.Size}");
        }
    }
}
