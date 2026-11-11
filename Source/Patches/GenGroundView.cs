using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ.Patches
{
    // Map generation sees only the ground.
    //
    // While the genSteps run, a map with floors is shrunk to its ground (MapGenPatches: map.info.Size = the ground), but its
    // grids and map.cellIndices keep the full size (ground + annex). Code that sizes a cell walk by map.Size / AllCells /
    // BoundsRect sees the ground. Code that walks the grids through map.cellIndices sees the annex rows too. To it they are
    // flat, empty land: elevation 0, never written by a genStep.
    //
    // THE RULE: a generation-time walk over the grids uses GroundIndices(map) (the map's current size), never map.cellIndices.
    // The row stride (x) is the same, so a full-size grid is indexed the same for every ground cell. Outside generation, and
    // on maps without floors, the two are equal and vanilla is unchanged. cellIndices and the grids themselves are not touched.
    //
    // Inventory (decompiled 1.7 Assembly-CSharp, 2026-11-08), every walk by cellIndices / NumGridCells / IndexToCell that runs
    // while a map is generated:
    //   MapGenUtility.GetClearRects            walks cellIndices over MapGenerator.Elevation: clear rects in the annex and across
    //                                          the ground's north edge (no top padding at the ground). Behind it:
    //                                          TryGetRandomClearRect, TryGetLargestClearRect, TryGetClosestClearRectTo,
    //                                          GetLargestClearRects, TryGetStructureRect. Users: GenStep_LargeRuins (ancient
    //                                          reactor, no bounds check: ruins spawned in the annex, thousands of out-of-bounds
    //                                          errors), Outpost, AncientStockpile_Gravcore, GravshipWreckage, SurveySite, Asteroid,
    //                                          TileMutator AncientStructure/AncientUplink/Stockpile/Harbor (most only safe by an
    //                                          affordance or bounds check of their own).  -> clipped here
    //   TileMutatorWorker_MixedBiome.Init      array of map.Size (the ground) indexed by cellIndices: same stride, ground only;
    //                                          GridFixer resizes it at FinalizeInit. Safe.
    //   WaterBodyTracker.ConstructBodies       walks NumGridCells at FinalizeInit / load, after the size is restored (annex is
    //                                          open air, never water). Safe.
    //   PathFinder, PathFinderMapData,         run inside the full-size window (MapGenPatches BeginFull/EndFull). Handled.
    //   UsedRectPathGridCustomizer
    //   FloodFiller                            indices only for its visited list; neighbours checked by InBounds (map.Size). Safe.
    //   MapGenFloatGrid, map component grids   allocation by NumGridCells only, no walk. Safe.
    //   BoolGrid, ByteGrid, IntGrid, CellGrid  sized by map.Size in ClearAndResizeTo(Map) (their Map constructors and every
    //     made or resized from the map        resize by a map): run inside the full-size window (MapGenPatches
    //                                          Grid_ClearAndResizeTo_Patch), so a grid made from the shrunk map has the full
    //                                          size: areas (AddStartingAreas), zones, plans, genStep scratch grids (Asteroid,
    //                                          RocksFromGrid, Caves, LargestAreaFinder), FloodFiller.parentGrid, other mods'
    //                                          grids (VEF PipeNet.networkGrid from a gravship start's pipes). Indexed by the
    //                                          same stride, so ground cells read the same. Handled. GridFixer (FinalizeInit) is
    //                                          only a backstop for grids in the fields of the map, of its fields' objects and
    //                                          of areas, zones, plans and map components; it does not reach grids nested
    //                                          deeper or in lists (VEF's pipe nets). It warns for every grid it still resizes.
    //   CellFinder random cells / edges        CellFinderPatches (ground only). Handled.
    public static class GenGroundView
    {
        // The cells generation may use: the map's current size (the ground while shrunk).
        public static CellIndices GroundIndices(Map map) => new CellIndices(map.Size.x, map.Size.z);
    }

    [HarmonyPatch(typeof(MapGenUtility), nameof(MapGenUtility.GetClearRects))]
    public static class MapGenUtility_GetClearRects_Patch
    {
        private static readonly FieldInfo CellIndicesField = AccessTools.Field(typeof(Map), nameof(Map.cellIndices));
        private static readonly MethodInfo Ground = AccessTools.Method(typeof(GenGroundView), nameof(GenGroundView.GroundIndices));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int swapped = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.LoadsField(CellIndicesField))
                {
                    swapped++;
                    yield return new CodeInstruction(OpCodes.Call, Ground).WithLabels(ci.labels).WithBlocks(ci.blocks);
                    continue;
                }
                yield return ci;
            }
            if (swapped != 1)
            {
                throw new InvalidOperationException($"MapGenUtility.GetClearRects: expected one read of Map.cellIndices, found {swapped}");
            }
        }
    }
}
