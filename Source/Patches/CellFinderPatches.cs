using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ProjectZ.Patches
{
    // Design §2.3 B: random cells, map edges and the map centre only ever mean the GROUND. The ground's top row
    // (z = groundZ - 1) is the north edge. On maps without an annex every prefix returns true at once (vanilla runs).
    // Annex (upper floor / void) cells are never "on the edge": nobody enters or leaves the map from an upper floor.

    [HarmonyPatch(typeof(CellFinder), nameof(CellFinder.RandomCell))]
    public static class CellFinder_RandomCell_Patch
    {
        public static bool Prefix(Map map, ref IntVec3 __result)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return true;
            }
            __result = new IntVec3(Rand.Range(0, map.Size.x), 0, Rand.Range(0, a.groundZ));
            return false;
        }
    }

    // Its fallback scans the whole map; the first loop calls RandomCell, but replace it whole so an inlined
    // RandomCell can't leak annex cells either.
    [HarmonyPatch(typeof(CellFinder), nameof(CellFinder.TryFindRandomCell))]
    public static class CellFinder_TryFindRandomCell_Patch
    {
        public static bool Prefix(Map map, Predicate<IntVec3> validator, ref IntVec3 result, ref bool __result)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return true;
            }
            int sx = map.Size.x;
            int gz = a.groundZ;
            for (int i = 0; i < 1000; i++)
            {
                IntVec3 c = new IntVec3(Rand.Range(0, sx), 0, Rand.Range(0, gz));
                if (validator == null || validator(c))
                {
                    result = c;
                    __result = true;
                    return false;
                }
            }
            List<IntVec3> cells = new List<IntVec3>();
            for (int x = 0; x < sx; x++)
            {
                for (int z = 0; z < gz; z++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    if (validator(c))
                    {
                        cells.Add(c);
                    }
                }
            }
            if (cells.Count == 0)
            {
                result = new IntVec3(Rand.Range(0, sx), 0, Rand.Range(0, gz));
                __result = false;
                return false;
            }
            result = cells.RandomElement();
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(CellFinderLoose), nameof(CellFinderLoose.TryGetRandomCellWith))]
    public static class CellFinderLoose_TryGetRandomCellWith_Patch
    {
        public static bool Prefix(Predicate<IntVec3> validator, Map map, int maxTries, ref IntVec3 result, ref bool __result)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return true;
            }
            for (int i = 0; i < maxTries; i++)
            {
                IntVec3 c = new IntVec3(Rand.Range(0, map.Size.x), 0, Rand.Range(0, a.groundZ));
                if (validator(c))
                {
                    result = c;
                    __result = true;
                    return false;
                }
            }
            result = IntVec3.Invalid;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(CellFinder), nameof(CellFinder.RandomNotEdgeCell))]
    public static class CellFinder_RandomNotEdgeCell_Patch
    {
        public static bool Prefix(int minEdgeDistance, Map map, ref IntVec3 __result)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return true;
            }
            if (minEdgeDistance > map.Size.x / 2 || minEdgeDistance > a.groundZ / 2)
            {
                __result = IntVec3.Invalid;
                return false;
            }
            __result = new IntVec3(Rand.Range(minEdgeDistance, map.Size.x - minEdgeDistance), 0, Rand.Range(minEdgeDistance, a.groundZ - minEdgeDistance));
            return false;
        }
    }

    [HarmonyPatch(typeof(CellFinder), nameof(CellFinder.RandomEdgeCell), new[] { typeof(Map) })]
    public static class CellFinder_RandomEdgeCell_Patch
    {
        public static bool Prefix(Map map, ref IntVec3 __result)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return true;
            }
            __result = EdgeCells.RandomGroundEdgeCell(map.Size.x, a.groundZ);
            return false;
        }
    }

    [HarmonyPatch(typeof(CellFinder), nameof(CellFinder.RandomEdgeCell), new[] { typeof(Rot4), typeof(Map) })]
    public static class CellFinder_RandomEdgeCellDir_Patch
    {
        public static bool Prefix(Rot4 dir, Map map, ref IntVec3 __result)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return true;
            }
            __result = EdgeCells.RandomGroundEdgeCell(dir, map.Size.x, a.groundZ);
            return false;
        }
    }

    [HarmonyPatch(typeof(CellFinder), nameof(CellFinder.TryFindRandomEdgeCellWith), new[] { typeof(Predicate<IntVec3>), typeof(Map), typeof(float), typeof(IntVec3) }, new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out })]
    public static class CellFinder_TryFindRandomEdgeCellWith_Patch
    {
        public static bool Prefix(Predicate<IntVec3> validator, Map map, float roadChance, ref IntVec3 result, ref bool __result)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return true;
            }
            if (Rand.Chance(roadChance) && map.roadInfo.roadEdgeTiles.Where(c => validator(c)).TryRandomElement(out result))
            {
                __result = true;
                return false;
            }
            for (int i = 0; i < 100; i++)
            {
                IntVec3 c = EdgeCells.RandomGroundEdgeCell(map.Size.x, a.groundZ);
                if (validator(c))
                {
                    result = c;
                    __result = true;
                    return false;
                }
            }
            __result = EdgeCells.TryAll(EdgeCells.GroundEdges(map, a.groundZ, null), validator, out result);
            return false;
        }
    }

    [HarmonyPatch(typeof(CellFinder), nameof(CellFinder.TryFindRandomEdgeCellWith), new[] { typeof(Predicate<IntVec3>), typeof(Map), typeof(Rot4), typeof(float), typeof(IntVec3) }, new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out })]
    public static class CellFinder_TryFindRandomEdgeCellWithDir_Patch
    {
        public static bool Prefix(Predicate<IntVec3> validator, Map map, Rot4 dir, float roadChance, ref IntVec3 result, ref bool __result)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return true;
            }
            if (Rand.Value < roadChance && map.roadInfo.roadEdgeTiles.Where(c => validator(c) && c.OnEdge(map, dir)).TryRandomElement(out result))
            {
                __result = true;
                return false;
            }
            for (int i = 0; i < 100; i++)
            {
                IntVec3 c = EdgeCells.RandomGroundEdgeCell(dir, map.Size.x, a.groundZ);
                if (validator(c))
                {
                    result = c;
                    __result = true;
                    return false;
                }
            }
            __result = EdgeCells.TryAll(EdgeCells.GroundEdges(map, a.groundZ, dir), validator, out result);
            return false;
        }
    }

    [HarmonyPatch(typeof(CellFinder), nameof(CellFinder.TryFindRandomEdgeCellNearWith))]
    public static class CellFinder_TryFindRandomEdgeCellNearWith_Patch
    {
        public static bool Prefix(IntVec3 near, float radius, Map map, Predicate<IntVec3> validator, ref IntVec3 spot, ref bool __result)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return true;
            }
            CellRect ground = new CellRect(0, 0, map.Size.x, a.groundZ);
            CellRect rect = CellRect.CenteredOn(near, Mathf.CeilToInt(radius)).ClipInsideRect(ground);
            if (rect.ExpandedBy(1).Area == rect.ExpandedBy(1).ClipInsideRect(ground).Area)
            {
                spot = IntVec3.Invalid;
                __result = false;
                return false;
            }
            Predicate<IntVec3> predicate = x => x.InHorDistOf(near, radius) && x.OnEdge(map) && validator(x);
            if (ground.EdgeCellsCount < rect.Area)
            {
                __result = CellFinder.TryFindRandomEdgeCellWith(predicate, map, CellFinder.EdgeRoadChance_Ignore, out spot);
            }
            else
            {
                __result = CellFinder.TryFindRandomCellInsideWith(rect, predicate, out spot);
            }
            return false;
        }
    }

    // OnEdge(map): x at either side, or z on the ground's bottom / top row. Annex cells: never.
    [HarmonyPatch(typeof(GenGrid), nameof(GenGrid.OnEdge), new[] { typeof(IntVec3), typeof(Map) })]
    public static class GenGrid_OnEdge_Patch
    {
        public static bool Prefix(IntVec3 c, Map map, ref bool __result)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return true;
            }
            __result = c.z < a.groundZ && (c.x == 0 || c.x == map.Size.x - 1 || c.z == 0 || c.z == a.groundZ - 1);
            return false;
        }
    }

    // OnEdge(map, dir) keeps vanilla's own North/South mapping (North checks z == 0); only the top row moves.
    [HarmonyPatch(typeof(GenGrid), nameof(GenGrid.OnEdge), new[] { typeof(IntVec3), typeof(Map), typeof(Rot4) })]
    public static class GenGrid_OnEdgeDir_Patch
    {
        public static bool Prefix(IntVec3 c, Map map, Rot4 dir, ref bool __result)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return true;
            }
            if (c.z >= a.groundZ)
            {
                __result = false;
                return false;
            }
            if (dir == Rot4.South)
            {
                __result = c.z == a.groundZ - 1;
                return false;
            }
            return true; // North (z == 0), West, East don't depend on the height
        }
    }

    // Distance to the ground edge; an upper-floor cell measures in its own band like the ground cell under it.
    [HarmonyPatch(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceToEdge))]
    public static class IntVec3Utility_DistanceToEdge_Patch
    {
        public static bool Prefix(IntVec3 v, Map map, ref int __result)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return true;
            }
            int z = v.z < a.groundZ ? v.z : v.z % a.stride;
            if (z >= a.groundZ)
            {
                __result = 0; // void gap
                return false;
            }
            __result = Mathf.Max(Mathf.Min(Mathf.Min(Mathf.Min(v.x, z), map.Size.x - v.x - 1), a.groundZ - z - 1), 0);
            return false;
        }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.Center), MethodType.Getter)]
    public static class Map_Center_Patch
    {
        public static void Postfix(Map __instance, ref IntVec3 __result)
        {
            AnnexMapComponent a = ProjectZ.Annex(__instance);
            if (a != null)
            {
                __result = new IntVec3(__instance.Size.x / 2, 0, a.groundZ / 2);
            }
        }
    }

    internal static class EdgeCells
    {
        private static Map cachedMap;
        private static int cachedGroundZ;
        private static readonly List<IntVec3>[] cached = new List<IntVec3>[5]; // 0..3 = Rot4, 4 = all

        public static IntVec3 RandomGroundEdgeCell(int sx, int gz)
        {
            IntVec3 r = default;
            if (Rand.Value < 0.5f)
            {
                r.x = Rand.Value < 0.5f ? 0 : sx - 1;
                r.z = Rand.Range(0, gz);
            }
            else
            {
                r.z = Rand.Value < 0.5f ? 0 : gz - 1;
                r.x = Rand.Range(0, sx);
            }
            return r;
        }

        // Same direction mapping as vanilla CellFinder.RandomEdgeCell(Rot4, Map): North = top row.
        public static IntVec3 RandomGroundEdgeCell(Rot4 dir, int sx, int gz)
        {
            if (dir == Rot4.North)
            {
                return new IntVec3(Rand.Range(0, sx), 0, gz - 1);
            }
            if (dir == Rot4.South)
            {
                return new IntVec3(Rand.Range(0, sx), 0, 0);
            }
            if (dir == Rot4.West)
            {
                return new IntVec3(0, 0, Rand.Range(0, gz));
            }
            if (dir == Rot4.East)
            {
                return new IntVec3(sx - 1, 0, Rand.Range(0, gz));
            }
            return IntVec3.Invalid;
        }

        public static List<IntVec3> GroundEdges(Map map, int gz, Rot4? dir)
        {
            if (!ReferenceEquals(map, cachedMap) || gz != cachedGroundZ)
            {
                cachedMap = map;
                cachedGroundZ = gz;
                Array.Clear(cached, 0, cached.Length);
            }
            int slot = dir.HasValue ? dir.Value.AsInt : 4;
            if (cached[slot] == null)
            {
                CellRect ground = new CellRect(0, 0, map.Size.x, gz);
                cached[slot] = (dir.HasValue ? ground.GetEdgeCells(dir.Value) : ground.EdgeCells).ToList();
            }
            return cached[slot];
        }

        public static bool TryAll(List<IntVec3> cells, Predicate<IntVec3> validator, out IntVec3 result)
        {
            cells.Shuffle();
            for (int i = 0; i < cells.Count; i++)
            {
                try
                {
                    if (validator(cells[i]))
                    {
                        result = cells[i];
                        return true;
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[ProjectZ] TryFindRandomEdgeCellWith exception validating " + cells[i] + ": " + e);
                }
            }
            result = IntVec3.Invalid;
            return false;
        }
    }
}
