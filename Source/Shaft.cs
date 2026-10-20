using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

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

        // Opens the ceiling of C (§4.2): mark, U's leftovers, the boundary, C's roof, the stack below, falls, flying roofs.
        // 'cause' is the pawn who opened it (it keeps its job). (Fog: ReachFog, on the region change.)
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
            // 2b. pawns on U step to a neighbour on the same level (user 2026-10-19); with none free they fall (step 6)
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (i < list.Count && list[i] is Pawn p)
                {
                    StepAside(p, u, map);
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
            // 8. fog: the regions change with the opening (ReachFog)
            Opened++;
            LastOpenMs = sw.Elapsed.TotalMilliseconds;
            MaxOpenMs = System.Math.Max(MaxOpenMs, LastOpenMs);
            ProjectZ.DebugLog($"shaft opened over {c} (U {u}): roof {map.roofGrid.RoofAt(c)?.defName ?? "none"}, U roof {map.roofGrid.RoofAt(u)?.defName ?? "none"}, {LastOpenMs:0.00} ms");
        }

        public static int SteppedAside;

        private static readonly AccessTools.FieldRef<Pawn_PathFollower, PathEndMode> peModeRef = AccessTools.FieldRefAccess<Pawn_PathFollower, PathEndMode>("peMode");

        // A pawn on a top that opens steps to the free neighbour (8-way, same level, not open air or about to open) nearest
        // where it was heading (its path, else its job's cell on this level), else nearest its spot. Its job goes on.
        private static void StepAside(Pawn p, IntVec3 u, Map map)
        {
            IntVec3 goal = IntVec3.Invalid;
            if (p.pather != null && p.pather.Moving && p.pather.Destination.IsValid)
            {
                goal = p.pather.Destination.Cell;
            }
            else if (p.CurJob != null)
            {
                foreach (TargetIndex ti in new[] { TargetIndex.C, TargetIndex.B, TargetIndex.A })
                {
                    LocalTargetInfo t = p.CurJob.GetTarget(ti);
                    if (t.IsValid && t.Cell != u && t.Cell.InBounds(map) && ProjectZ.SameBand(t.Cell, u, map))
                    {
                        goal = t.Cell;
                        break;
                    }
                }
            }
            IntVec3 best = IntVec3.Invalid;
            int bestScore = int.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = u + GenAdj.AdjacentCells[i];
                if (!n.InBounds(map) || !ProjectZ.SameBand(n, u, map) || Gravity.Air(n, map) || !n.Standable(map) || AboutToOpen(n, map))
                {
                    continue;
                }
                int score = (n - u).LengthHorizontalSquared + (goal.IsValid && goal.InBounds(map) && ProjectZ.SameBand(goal, u, map) ? 10 * (n - goal).LengthHorizontalSquared : 0);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = n;
                }
            }
            if (!best.IsValid)
            {
                return;
            }
            bool moving = p.pather != null && p.pather.Moving;
            LocalTargetInfo dest = moving ? p.pather.Destination : LocalTargetInfo.Invalid;
            PathEndMode pe = moving ? peModeRef(p.pather) : PathEndMode.None;
            p.Position = best;
            p.Notify_Teleported(endCurrentJob: false, resetTweenedPos: false);
            if (moving && dest.IsValid && p.Spawned)
            {
                p.pather.StartPath(dest, pe);   // (the teleport stops the pather: on its way again)
            }
            SteppedAside++;
            ProjectZ.DebugLog($"{p} stepped aside from opening top {u} to {best}");
        }

        // A cell whose own top is set to open: marked Mine below, or a Remove roof hole under it.
        private static bool AboutToOpen(IntVec3 n, Map map)
        {
            if (MineSide.MarkAt(n, map, MineDir.FromAbove))
            {
                return true;
            }
            IntVec3 b = FloorRoof.Below(n, map);
            return b.IsValid && b.InBounds(map) && map.areaManager.NoRoof[b] && Hole.CanOpen(b, map);
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
