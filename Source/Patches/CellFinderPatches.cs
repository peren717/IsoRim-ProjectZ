using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
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
            IntVec3 g = Levels.Ground(v, map);
            if (!g.IsValid)
            {
                __result = 0; // void gap
                return false;
            }
            int z = g.z;
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

    // The game's finders that size their search by the whole map (compat scan §4, G1): inside them the map means the
    // ground, so exits and edges are the ground's edges (north = row groundZ - 1) and searches round the map centre stay
    // on the ground. One swap set for all of them: map.Size -> the ground's size, CellRect.WholeMap -> the ground rect,
    // ClipInsideMap -> the ground; the expanding search round a centre (TryFindRandomCellNearWith and every finder built
    // on it) clips to the centre's band instead. Without floors every swap does what vanilla's call does.
    //   TryFindBestExitSpot: its random cell near the pawn is taken down to the ground under it (a pawn upstairs leaves
    //   by the ground edge nearest the column it stands over).
    //   TryFindEdgeCellFromPositionAvoidingColony: an upper-floor start counts as the ground cell under it (prefix below).
    //   MultipleCaravansCellFinder (two caravans meeting): its opposite spots near the ground's edges.
    //   LargeBuildingCellFinder's candidate scan: the ground's rows (its cell rule: LargeBuildingGroundOnly_Patch).
    [HarmonyPatch]
    public static class GroundFinders_Patch
    {
        public static readonly Dictionary<string, int> PerMethod = new Dictionary<string, int>();

        private static MethodInfo M(Type t, string name) => AccessTools.Method(t, name);

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return M(typeof(RCellFinder), nameof(RCellFinder.TryFindRandomCellNearWith));
            yield return M(typeof(RCellFinder), nameof(RCellFinder.TryFindRandomCellOutsideColonyNearTheCenterOfTheMap));
            yield return M(typeof(RCellFinder), nameof(RCellFinder.TryFindBestExitSpot));
            yield return M(typeof(RCellFinder), nameof(RCellFinder.TryFindRandomExitSpot));
            yield return M(typeof(RCellFinder), nameof(RCellFinder.TryFindEdgeCellFromPositionAvoidingColony));
            yield return M(typeof(MultipleCaravansCellFinder), nameof(MultipleCaravansCellFinder.FindStartingCellsFor2Groups));
            yield return M(typeof(MultipleCaravansCellFinder), "TryFindOppositeSpots");
            yield return M(typeof(LargeBuildingCellFinder), "CalculateLocationCandidates");
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            MethodInfo size = AccessTools.PropertyGetter(typeof(Map), nameof(Map.Size));
            MethodInfo wholeMap = M(typeof(CellRect), nameof(CellRect.WholeMap));
            MethodInfo clip = M(typeof(CellRect), nameof(CellRect.ClipInsideMap));
            MethodInfo near = M(typeof(CellFinder), nameof(CellFinder.TryFindRandomCellNear));
            MethodInfo spotNearEdge = M(typeof(MultipleCaravansCellFinder), "RandomSpotNearEdge");
            MethodInfo opposite = M(typeof(MultipleCaravansCellFinder), "OppositeSpot");
            bool byCentre = original.Name == nameof(RCellFinder.TryFindRandomCellNearWith);
            int n = 0;
            foreach (CodeInstruction ci in instructions)
            {
                string with = null;
                if (ci.Calls(size))
                {
                    with = nameof(GroundFinders.Size);
                }
                else if (ci.Calls(wholeMap))
                {
                    with = nameof(GroundFinders.GroundRect);
                }
                else if (ci.Calls(clip))
                {
                    if (byCentre && !GroundFinders.Off)
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0); // near
                    }
                    with = byCentre ? nameof(GroundFinders.ClipInsideBand) : nameof(GroundFinders.ClipInsideGround);
                }
                else if (ci.Calls(near))
                {
                    with = nameof(GroundFinders.RandomCellNearOnGround);
                }
                else if (ci.Calls(spotNearEdge))
                {
                    with = nameof(GroundFinders.RandomSpotNearEdge);
                }
                else if (ci.Calls(opposite))
                {
                    with = nameof(GroundFinders.OppositeSpot);
                }
                if (with != null && !GroundFinders.Off)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = M(typeof(GroundFinders), with);
                    n++;
                }
                yield return ci;
            }
            PerMethod[original.DeclaringType.Name + "." + original.Name] = n;
            if (n == 0)
            {
                Log.Warning("[ProjectZ] ground finders: nothing swapped in " + original.DeclaringType.Name + "." + original.Name + " (left as vanilla)");
            }
        }
    }

    // TryFindEdgeCellFromPositionAvoidingColony walks from the position to the ground's edge: start on the ground.
    [HarmonyPatch(typeof(RCellFinder), nameof(RCellFinder.TryFindEdgeCellFromPositionAvoidingColony))]
    public static class EdgeCellFromPosition_Ground_Patch
    {
        public static void Prefix(ref IntVec3 position, Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a != null && position.z >= a.groundZ && !GroundFinders.Off)
            {
                position = GroundFinders.OnGround(position, map, a);
            }
        }
    }

    // The swaps (GroundFinders_Patch). Each is the vanilla call when the map has no floors.
    public static class GroundFinders
    {
        // Test only (-projectzfindersoff): the finders as vanilla (no swaps, no ground-only large structures), to show
        // the old behaviour.
        public static readonly bool Off = GenCommandLine.CommandLineArgPassed("projectzfindersoff");

        public static IntVec3 Size(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            IntVec3 s = map.Size;
            return a == null ? s : new IntVec3(s.x, s.y, a.groundZ);
        }

        public static CellRect GroundRect(Map map) => ProjectZ.GroundRect(map);

        // (static stand-ins for the instance method: the rect comes by reference, as its 'this')
        public static CellRect ClipInsideGround(ref CellRect rect, Map map)
        {
            return rect.ClipInsideRect(ProjectZ.GroundRect(map));
        }

        // The band of the search centre (a void row counts with the band below it).
        public static CellRect ClipInsideBand(ref CellRect rect, Map map, IntVec3 centre)
        {
            if (ProjectZ.Annex(map) == null)
            {
                return rect.ClipInsideMap(map);
            }
            int L = Levels.LevelOfRowLoose(map, centre.z);
            return rect.ClipInsideRect(CellRect.FromLimits(0, Levels.RowMin(map, L), map.Size.x - 1, Levels.RowMax(map, L)));
        }

        // A cell (any row) taken down to the ground under it; a void row to the ground's top row.
        public static IntVec3 OnGround(IntVec3 c, Map map, AnnexMapComponent a)
        {
            IntVec3 g = Levels.Ground(c, map);
            return g.IsValid ? g : new IntVec3(c.x, c.y, Mathf.Clamp(Mathf.FloorToInt(Levels.GroundZLoose(map, c.z)), 0, a.groundZ - 1));
        }

        public static bool RandomCellNearOnGround(IntVec3 root, Map map, int squareRadius, Predicate<IntVec3> validator, out IntVec3 result, int maxTries)
        {
            bool found = CellFinder.TryFindRandomCellNear(root, map, squareRadius, validator, out result, maxTries);
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (found && a != null && result.z >= a.groundZ)
            {
                result = OnGround(result, map, a);
            }
            return found;
        }

        // MultipleCaravansCellFinder's own two helpers, its lines with the ground's size.
        public static IntVec3 RandomSpotNearEdge(Map map)
        {
            IntVec3 s = Size(map);
            CellRect r = ProjectZ.GroundRect(map);
            r.minX += Mathf.RoundToInt(s.x * 0.2f);
            r.minZ += Mathf.RoundToInt(s.z * 0.2f);
            r.maxX -= Mathf.RoundToInt(s.x * 0.2f);
            r.maxZ -= Mathf.RoundToInt(s.z * 0.2f);
            return r.EdgeCells.RandomElement();
        }

        public static IntVec3 OppositeSpot(IntVec3 spot, Map map)
        {
            IntVec3 s = Size(map);
            return new IntVec3(s.x - spot.x, spot.y, s.z - spot.z);
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
