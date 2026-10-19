using System.Collections.Generic;
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
    // The two ways in (both player orders): vanilla Mine on rock above that can't be dug on its own level (MineUp.cs,
    // JobDriver_MineFromBelow), and vanilla Remove roof under a bare natural top (Hole.CanOpen, MineUp.cs).
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
            // 8. fog: U and the rock faces round it (not the open cells beyond: those open when pawns can walk there)
            FogGrid fog = map.fogGrid;
            if (fog.IsFogged(u))
            {
                fog.Unfog(u);
            }
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = u + GenAdj.AdjacentCells[i];
                if (n.InBounds(map) && ProjectZ.SameBand(n, u, map) && fog.IsFogged(n))
                {
                    Building e = n.GetEdifice(map);
                    if (e != null && e.def.MakeFog)
                    {
                        fog.Unfog(n);
                    }
                }
            }
            Opened++;
            LastOpenMs = sw.Elapsed.TotalMilliseconds;
            MaxOpenMs = System.Math.Max(MaxOpenMs, LastOpenMs);
            ProjectZ.DebugLog($"shaft opened over {c} (U {u}): roof {map.roofGrid.RoofAt(c)?.defName ?? "none"}, U roof {map.roofGrid.RoofAt(u)?.defName ?? "none"}, {LastOpenMs:0.00} ms");
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

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref cells, "cells", LookMode.Value);
            if (cells == null)
            {
                cells = new HashSet<IntVec3>();
            }
        }
    }
}
