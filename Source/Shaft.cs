using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ
{
    // Tall mountains phase 3 ("mine up", isorim-pz-phase3-mineup-plan.md §4.2): a shaft is a natural ceiling opened from
    // below. C is the cell whose ceiling opened (the miner's level L), U the cell right above it (L+1). One opening is one
    // boundary: U becomes open air; U keeps its own roof (thick under more rock or a top: a covered shaft; none: a hole to
    // the sky). C's roof follows one rule (ApplyRoof): U roofed -> the stairwell roof (an opening: indoors, invisible,
    // Column.OpenAbove), else none. The rule is applied again down a stack of shafts when a higher boundary opens.
    // C cells are remembered (ShaftMarks, saved); a mark counts while no floor lies over it (a slab cap or a stairs landing
    // over the hole takes over the ceiling, and the mark comes back into force if that floor goes).
    // The ways in (player orders): Mine above / Mine below on a cell, the rock over or under it mined from below or from above
    // (MineUp.cs, JobDriver_MineVertical), and vanilla Remove roof under a bare natural top (Hole.CanOpen, MineUp.cs).
    public static class Shaft
    {
        // counters (tests)
        public static int Opened, StackRoofs;
        public static double LastOpenMs, MaxOpenMs;

        // C is the bottom of a shaft: marked, and nothing laid over the hole.
        public static bool Is(IntVec3 c, Map map)
        {
            return ShaftMarks.Has(c, map) && !FloorRoof.FloorAbove(c, map);
        }

        // A shaft whose top is covered (U roofed): its bottom keeps the stairwell roof.
        public static bool Covered(IntVec3 c, Map map)
        {
            if (!Is(c, map))
            {
                return false;
            }
            IntVec3 up = FloorRoof.Above(c, map);
            return up.IsValid && map.roofGrid.Roofed(up);
        }

        // The roof a shaft bottom gets: the stairwell roof under a covered top, none under the sky. A roof the player built
        // over a hole to the sky stays (it closes the hole; plan R4 #8); so does a floor's roof (a cap: FloorAbove).
        public static void ApplyRoof(IntVec3 c, Map map)
        {
            if (!c.IsValid || !c.InBounds(map) || FloorRoof.FloorAbove(c, map))
            {
                return;
            }
            RoofDef cur = map.roofGrid.RoofAt(c);
            IntVec3 up = FloorRoof.Above(c, map);
            RoofDef want = up.IsValid && map.roofGrid.Roofed(up) ? PZDefOf.PZ_StairwellRoof : null;
            if (cur != null && !cur.isNatural && cur != PZDefOf.PZ_FloorRoof && cur != PZDefOf.PZ_StairwellRoof)
            {
                return;   // built by the player over the hole
            }
            if (cur != want)
            {
                map.roofGrid.SetRoof(c, want);
            }
        }

        // Opens the ceiling of C (§4.2): mark, U's leftovers, the boundary, C's roof, the stack below, falls, flying roofs,
        // the shaft walls' fog. 'cause' is the pawn who opened it (it keeps its job).
        public static void Open(IntVec3 c, Map map, Thing cause = null)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            IntVec3 u = FloorRoof.Above(c, map);
            if (!u.IsValid || !u.InBounds(map) || !ProjectZ.HasFloor(u, map))
            {
                return;
            }
            // 1. the mark (before the terrain: the roof rules ask it)
            ShaftMarks.For(map)?.Add(c);
            // 2. U's leftovers: the zone cell, designations, filth and plants
            Slab.DropZoneCell(u, map);
            List<Designation> des = new List<Designation>(map.designationManager.AllDesignationsAt(u));
            foreach (Designation d in des)
            {
                d.Delete();
            }
            List<Thing> list = u.GetThingList(map);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Thing t = list[i];
                if (t.def.category == ThingCategory.Filth || t.def.category == ThingCategory.Plant)
                {
                    t.Destroy(DestroyMode.Vanish);
                }
            }
            // 3. the boundary (TerrainChanged_FloorRoof_Patch: FloorRoof.Changed(U) puts C's roof by the shaft rule)
            map.terrainGrid.SetTerrain(u, PZDefOf.PZ_OpenAir);
            // 4. C's roof (again: also when the terrain hook found nothing to change)
            ApplyRoof(c, map);
            // 5. down a stack of shafts
            for (IntVec3 x = FloorRoof.Below(c, map); x.IsValid && Is(x, map); x = FloorRoof.Below(x, map))
            {
                StackRoofs++;
                ApplyRoof(x, map);
            }
            // 6. what stood on U falls (gravity) or steps aside
            Slab.MoveOff(u, map, cause);
            // 7. a hole to the sky: roofs that hung on C's roof (vanilla's no-roof job does the same)
            if (!map.roofGrid.Roofed(c))
            {
                RoofCollapseCellsFinder.CheckCollapseFlyingRoofs(new List<IntVec3> { c }, map, removalMode: true);
            }
            // 8. fog: both ends of the shaft (U, and C: a pit mined from above was rock in the fog) and the rock faces round
            // them (not the open cells beyond: those open when pawns can walk there)
            RevealEnd(u, map);
            RevealEnd(c, map);
            // 9. fog through the hole, like vanilla mining into a cave (HoleFog)
            HoleFog.Reveal(c, map, !map.generatorDef.ignoreAreaRevealedLetter);
            Opened++;
            LastOpenMs = sw.Elapsed.TotalMilliseconds;
            MaxOpenMs = System.Math.Max(MaxOpenMs, LastOpenMs);
            ProjectZ.DebugLog($"shaft opened over {c} (U {u}): roof {map.roofGrid.RoofAt(c)?.defName ?? "none"}, U roof {map.roofGrid.RoofAt(u)?.defName ?? "none"}, {LastOpenMs:0.00} ms");
        }

        private static void RevealEnd(IntVec3 x, Map map)
        {
            FogGrid fog = map.fogGrid;
            if (fog.IsFogged(x))
            {
                fog.Unfog(x);
            }
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = x + GenAdj.AdjacentCells[i];
                if (n.InBounds(map) && ProjectZ.SameBand(n, x, map) && fog.IsFogged(n))
                {
                    Building e = n.GetEdifice(map);
                    if (e != null && e.def.MakeFog)
                    {
                        fog.Unfog(n);
                    }
                }
            }
        }
    }

    // Fog through an opened hole (user 2026-10-19: what a pawn can get to by falling through it is revealed, like vanilla
    // mining into a cave). One rule for every opening (Shaft.Open: mine above, mine below, Remove roof under a bare top) and
    // for older saves (ShaftMarks, once per load):
    //   - down the column: where a pawn in U comes down is the fall rule (Column.Landing: the first floor below, a thick
    //     roof stops it, Invalid when nothing holds); every cell it passes is unfogged, and the landing gets vanilla's
    //     mined-cell reveal (FogGrid.FloodUnfogAdjacent: its open area floods, rock stops it; a rock cell there only shows
    //     its face);
    //   - the upper end U (the cell that became open air): under a roof (a covered shaft, inside the mountain) vanilla's
    //     mined-cell reveal too, so a hole mined up into a cave shows the cave; a hole to the sky shows only U and the rock
    //     round it (Shaft.Open's own reveal): the plateau round it opens where pawns walk (MountainFog), never through a
    //     hole nobody can climb out of (user 2026-10-19: Remove roof from a tunnel flooded the whole plateau).
    // Vanilla's flood stops at unfogged cells and the void rows between bands (at least one) are never fogged and hold
    // no rock, so it keeps to its band (as MountainFog's floods).
    public static class HoleFog
    {
        // counters (tests)
        public static int Reveals, LastPassed;

        // test only (-projectznoholefog): the rule does nothing, to show the old fog
        public static bool Off;

        private static readonly List<IntVec3> tmp = new List<IntVec3>();

        private static bool Blocker(IntVec3 c, Map map)
        {
            Building e = c.GetEdifice(map);
            return e != null && e.def.MakeFog;
        }

        // U lies inside the mountain (roofed): its open neighbours are a cave, not a plateau under the sky.
        private static bool Covered(IntVec3 u, Map map) => map.roofGrid.Roofed(u);

        // The landing of a fall into the hole over c, and the cells the fall passes (between U and the landing).
        private static IntVec3 Fall(IntVec3 c, Map map, out IntVec3 u, List<IntVec3> passed)
        {
            passed.Clear();
            u = FloorRoof.Above(c, map);
            if (!u.IsValid || !u.InBounds(map))
            {
                return IntVec3.Invalid;
            }
            IntVec3 land = Column.Landing(u, map, false, true, out _, out _);
            if (!land.IsValid || !land.InBounds(map) || land == u)
            {
                return IntVec3.Invalid;
            }
            IntVec3 g = Levels.Ground(u, map);
            int lo = Levels.LevelOf(land, map);
            for (int L = Levels.LevelOf(u, map) - 1; L > lo; L--)
            {
                IntVec3 x = Levels.Cell(g, L, map);
                if (x.IsValid)
                {
                    passed.Add(x);
                }
            }
            return land;
        }

        // The rule for the hole over shaft bottom c (c's ceiling open: U above it is open air).
        public static void Reveal(IntVec3 c, Map map, bool letters)
        {
            if (Off || map == null || !c.IsValid || !c.InBounds(map))
            {
                return;
            }
            IntVec3 land = Fall(c, map, out IntVec3 u, tmp);
            if (!u.IsValid || !u.InBounds(map))
            {
                tmp.Clear();
                return;
            }
            FogGrid fog = map.fogGrid;
            List<IntVec3> passed = new List<IntVec3>(tmp);
            tmp.Clear();
            if (Covered(u, map))
            {
                fog.FloodUnfogAdjacent(u, letters);
            }
            foreach (IntVec3 x in passed)
            {
                if (fog.IsFogged(x))
                {
                    fog.Unfog(x);
                }
            }
            LastPassed = passed.Count;
            if (land.IsValid)
            {
                if (!Blocker(land, map))
                {
                    fog.FloodUnfogAdjacent(land, letters);
                }
                else if (fog.IsFogged(land))
                {
                    fog.Unfog(land);
                }
            }
            Reveals++;
            ProjectZ.DebugLog($"hole fog over {c}: U {u}, landing {land}, {LastPassed} cell(s) passed");
        }

        // Something the rule reveals is still fogged (older saves): a passed cell, the landing, or open fog next to the
        // landing or to a covered U.
        public static bool Pending(IntVec3 c, Map map)
        {
            IntVec3 land = Fall(c, map, out IntVec3 u, tmp);
            try
            {
                if (!u.IsValid || !u.InBounds(map))
                {
                    return false;
                }
                FogGrid fog = map.fogGrid;
                foreach (IntVec3 x in tmp)
                {
                    if (fog.IsFogged(x))
                    {
                        return true;
                    }
                }
                if (Covered(u, map) && OpenFogNext(u, map))
                {
                    return true;
                }
                return land.IsValid && (fog.IsFogged(land) || !Blocker(land, map) && OpenFogNext(land, map));
            }
            finally
            {
                tmp.Clear();
            }
        }

        private static bool OpenFogNext(IntVec3 x, Map map)
        {
            for (int i = 0; i < 4; i++)
            {
                IntVec3 n = x + GenAdj.CardinalDirections[i];
                if (n.InBounds(map) && map.fogGrid.IsFogged(n) && !Blocker(n, map))
                {
                    return true;
                }
            }
            return false;
        }
    }

    // The bottoms of shafts (C cells), saved.
    public class ShaftMarks : MapComponent
    {
        public HashSet<IntVec3> cells = new HashSet<IntVec3>();

        public ShaftMarks(Map map) : base(map)
        {
        }

        private static Map lastMap;
        private static ShaftMarks last;

        public static ShaftMarks For(Map map)
        {
            if (map == null)
            {
                return null;
            }
            if (!ReferenceEquals(map, lastMap))
            {
                last = map.components == null ? null : map.GetComponent<ShaftMarks>();
                lastMap = map;
            }
            return last;
        }

        public static bool Has(IntVec3 c, Map map)
        {
            ShaftMarks m = For(map);
            return m != null && m.cells.Count > 0 && m.cells.Contains(c);
        }

        public void Add(IntVec3 c) => cells.Add(c);

        // (loading makes the map's components anew from the save: the instance cached before that is stale)
        private void Cache()
        {
            lastMap = map;
            last = this;
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            Cache();
            fogChecked = false;
        }

        // Saves from before HoleFog: once per load, on the first frame in play (fog and regions ready), every open hole
        // whose fall or landing area is still fogged gets the rule.
        private bool fogChecked;

        public override void MapComponentUpdate()
        {
            if (fogChecked || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            fogChecked = true;
            int fixedHoles = 0;
            foreach (IntVec3 c in new List<IntVec3>(cells))
            {
                if (Shaft.Is(c, map) && HoleFog.Pending(c, map))
                {
                    HoleFog.Reveal(c, map, false);
                    fixedHoles++;
                }
            }
            if (fixedHoles > 0)
            {
                ProjectZ.DebugLog($"hole fog: {fixedHoles} open hole(s) from an older save revealed below");
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref cells, "cells", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                Cache();
            }
            if (cells == null)
            {
                cells = new HashSet<IntVec3>();
            }
        }
    }
}

namespace ProjectZ
{
    // Mine up P5 (plan §8, Q7): a thick roof over open shaft air that caves in (vanilla's cave-in for an unsupported mined
    // area) drops its rocks at the shaft's bottom instead of into the air: the cell is taken off the game's collapse list
    // and vanilla's own first phase (the crush and the CollapsedRocks) runs on the landing cell (Column.Landing), with the
    // thick roof's def there for that one call. The thick roof itself stays (vanilla: it never vanishes).
    public static class ShaftCaveIn
    {
        public static int Landed;

        private static readonly AccessTools.FieldRef<RoofGrid, RoofDef[]> gridRef = AccessTools.FieldRefAccess<RoofGrid, RoofDef[]>("roofGrid");

        private delegate void PhaseOneFn(IntVec3 c, Map map, List<Thing> outCrushedThings);

        private static readonly PhaseOneFn phaseOne = AccessTools.MethodDelegate<PhaseOneFn>(AccessTools.Method(typeof(RoofCollapserImmediate), "DropRoofInCellPhaseOne"));

        private static bool Over(IntVec3 c, Map map)
        {
            return c.InBounds(map) && (map.roofGrid.RoofAt(c)?.isThickRoof ?? false) && Gravity.Air(c, map) && !ProjectZ.IsVoid(c, map);
        }

        public static List<IntVec3> Redirect(List<IntVec3> cells, Map map, List<Thing> outCrushedThings)
        {
            bool any = false;
            for (int i = 0; i < cells.Count && !any; i++)
            {
                any = Over(cells[i], map);
            }
            if (!any)
            {
                return cells;
            }
            List<IntVec3> keep = new List<IntVec3>(cells.Count);
            RoofDef[] grid = gridRef(map.roofGrid);
            foreach (IntVec3 c in cells)
            {
                if (!Over(c, map))
                {
                    keep.Add(c);
                    continue;
                }
                IntVec3 land = Column.Landing(c, map, false, false, out _, out _);
                if (!land.IsValid || !land.InBounds(map))
                {
                    continue;   // nothing below holds: no rocks in the air
                }
                int idx = map.cellIndices.CellToIndex(land);
                RoofDef was = grid[idx];
                grid[idx] = map.roofGrid.RoofAt(c);
                try
                {
                    phaseOne(land, map, outCrushedThings);
                }
                finally
                {
                    grid[idx] = was;
                }
                Landed++;
                ProjectZ.DebugLog($"cave-in over the shaft at {c}: rocks landed at {land}");
            }
            return keep;
        }
    }
}
