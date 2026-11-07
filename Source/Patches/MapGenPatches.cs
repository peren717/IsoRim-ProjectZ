using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld.Planet;
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
            public byte[] elevation;   // F5, captured during the genSteps; given to the component after GridFixer
            public bool shrunk;
            public bool mountains;     // tall mountains (Mountains.cs), filled in the FinalizeInit prefix
            public bool keepUpper;     // a plan's keepUpperContent: what generation put on the annex stays (no second fill)
            public int fullDepth;      // how many full-size windows are open while shrunk (BeginFull, EndFull)
            public IntVec3 FullSize => new IntVec3(groundSize.x, groundSize.y, floors * stride);
        }

        internal static Pending pending;

        // A moment at the full size on a map shrunk for generation. Every caller goes through these two, so that the windows
        // nest (a pathfinder call inside an addon's window must not shrink the map under it). False: the map isn't being
        // generated shrunk, there is nothing to end. Ending is always safe: after the size was restored (FinalizeInit, the
        // GenerateMap finalizer), or for another map, it does nothing.
        internal static bool BeginFull(Map map)
        {
            Pending p = pending;
            if (p == null || !p.shrunk || map == null || !ReferenceEquals(p.map, map))
            {
                return false;
            }
            p.fullDepth++;
            map.info.Size = p.FullSize;
            return true;
        }

        internal static void EndFull(Map map)
        {
            Pending p = pending;
            if (p == null || !p.shrunk || map == null || !ReferenceEquals(p.map, map) || p.fullDepth <= 0)
            {
                return;
            }
            if (--p.fullDepth == 0)
            {
                map.info.Size = p.groundSize;
            }
        }

        // Mountains foundation F6: what one annex cell (any upper band or void row) gets at generation.
        internal struct AnnexCell
        {
            public TerrainDef terrain;
            public bool fogged;
        }

        internal delegate AnnexCell FillRule(IntVec3 c, Map map);

        // Today's rule for every annex cell: open air, unfogged. (Phase 1 plugs the mountain fill in here: rock floors,
        // fogged interior rock; basements likewise.)
        internal static AnnexCell OpenAir(IntVec3 c, Map map) => new AnnexCell { terrain = PZDefOf.PZ_OpenAir, fogged = false };

        // The rule both FillAnnex calls use.
        internal static FillRule Rule = OpenAir;

        // Annex cells: the rule's terrain and fog, nothing else (no roof, no snow, no things). Written straight into the
        // terrain grid (SetTerrain's per-cell notifications aren't needed). Map.Size must be the full size.
        // Returns how many cells differed from the rule before.
        internal static int FillAnnex(Map map, int groundZ, bool withPathCosts, FillRule rule = null)
        {
            rule = rule ?? Rule;
            TerrainDef[] top = map.terrainGrid.topGrid;
            CellIndices ci = map.cellIndices;
            int changed = 0;
            for (int i = ci.CellToIndex(new IntVec3(0, 0, groundZ)); i < ci.NumGridCells; i++)
            {
                IntVec3 c = ci.IndexToCell(i);
                AnnexCell want = rule(c, map);
                bool fogged = map.fogGrid.IsFogged(i);
                if (top[i] != want.terrain || fogged != want.fogged)
                {
                    changed++;
                }
                top[i] = want.terrain;
                if (fogged && !want.fogged)
                {
                    map.fogGrid.Unfog(c);
                }
                else if (!fogged && want.fogged)
                {
                    map.fogGrid.Refog(CellRect.SingleCell(c));
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
        public static void Prefix(ref IntVec3 mapSize, MapParent parent, MapGeneratorDef mapGenerator, bool isPocketMap, bool stepDebugger, out object __state)
        {
            // (a map generated during another's generation, an addon's pocket map: the finalizer gives the outer one back)
            __state = AnnexGen.pending;
            AnnexGen.pending = null;
            int floors = ProjectZ.FloorsForNewMaps;
            // A plan (MapPlans.cs) overrides the game's floors and the size for this one map. Never for a pocket map.
            MapFloorPlan? plan = isPocketMap || stepDebugger ? null : MapPlans.For(parent);
            if (plan != null)
            {
                floors = plan.Value.floors;
                if (plan.Value.groundSize.x > 0 && plan.Value.groundSize.z > 0)
                {
                    mapSize = new IntVec3(plan.Value.groundSize.x, mapSize.y, plan.Value.groundSize.z);
                }
            }
            if (floors <= 1 || isPocketMap || stepDebugger || mapSize.z <= 0)
            {
                return;
            }
            int stride = ProjectZ.StrideFor(mapSize.z);
            AnnexGen.pending = new AnnexGen.Pending
            {
                groundSize = mapSize,
                stride = stride,
                floors = floors,
                mountains = plan == null && ProjectZ.TallMountainsForNewMaps,   // (a plan's map: its own layout, no rock rising through it)
                keepUpper = plan != null && plan.Value.keepUpperContent,
            };
            ProjectZ.DebugLog($"generating {mapGenerator?.defName} map: ground {mapSize.x}x{mapSize.z}, stride {stride}, floors {floors} -> {mapSize.x}x{floors * stride}");
            mapSize = new IntVec3(mapSize.x, mapSize.y, floors * stride);
        }

        public static void Finalizer(Exception __exception, object __state)
        {
            AnnexGen.Pending p = AnnexGen.pending;
            AnnexGen.pending = __state as AnnexGen.Pending;
            if (p?.map != null && p.shrunk)
            {
                // Generation threw before FinalizeInit: put the real size back so the grids and Size agree.
                p.map.info.Size = p.FullSize;
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
            __state = AnnexGen.BeginFull(MapRef(__instance));
        }

        public static void Finalizer(PathFinder __instance, bool __state)
        {
            if (__state)
            {
                AnnexGen.EndFull(MapRef(__instance));
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
            map.info.Size = p.FullSize;
            int groundZ = p.groundSize.z;
            ProjectZ.Invalidate();
            // Lazily built during generation from the ground size: rebuild over the full map, before anything
            // can index an annex cell.
            map.cellsInRandomOrder = new MapCellsInRandomOrder(map);
            int fixedGrids = GridFixer.Fix(map, p.groundSize.x * groundZ);
            // (after GridFixer, which would take a ground-sized array for one of its grids)
            AnnexMapComponent comp = map.GetComponent<AnnexMapComponent>();
            if (comp != null && p.elevation != null)
            {
                comp.elevation = p.elevation;
            }
            RemapRiverFlow(map, groundZ);
            // Again after generation (nothing should have touched it): path costs, regions and section meshes are
            // all rebuilt right after this in FinalizeInit.
            int changed = p.keepUpper ? 0 : AnnexGen.FillAnnex(map, groundZ, withPathCosts: false);
            ProjectZ.DebugLog($"annex {(p.keepUpper ? "kept (plan)" : "checked")}: {changed} cell(s) changed during generation, {fixedGrids} ground-sized grid(s) resized, map size now {map.Size}");
            if (p.mountains && comp != null && comp.elevation != null)
            {
                Mountains.Generate(map, comp);
            }
        }
    }
}
