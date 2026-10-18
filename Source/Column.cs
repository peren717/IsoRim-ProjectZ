using RimWorld;
using Verse;

namespace ProjectZ
{
    // Mountains foundation F2 (isorim-pz-mountains-design.md §3): the one set of column queries every 3D system asks
    // (sight and shots: Heights, CombatPatches; explosions; jumps and their arcs; falls; strikes from the sky). A column
    // is a ground cell g with its cells on every level (Levels). Phase 1's rock (and a possible virtual rock later) only
    // has to answer these.
    public static class Column
    {
        // The column above a cell is open (nothing stops a fall, a shot or a look): no roof, or the stairwell's.
        public static bool OpenAbove(IntVec3 c, Map map)
        {
            RoofDef r = map.roofGrid.RoofAt(c);
            return r == null || r == PZDefOf.PZ_StairwellRoof;
        }

        // There is a floor to stand on at level L over g: the ground (level 0) always; on an upper band a floor built (or
        // laid) there, any terrain but open air.
        public static bool FloorAt(IntVec3 g, int L, Map map)
        {
            return L == 0 || ProjectZ.HasFloor(Levels.Cell(g, L, map), map);
        }

        // The horizontal plane between level L-1 and level L over g is solid: the cell below has a roof (not the
        // stairwell's opening) or level L has a floor there (the two go together: FloorRoof). L = Max + 1 is the top
        // level's roof.
        public static bool PlaneSolid(IntVec3 g, int L, Map map)
        {
            IntVec3 below = Levels.Cell(g, L - 1, map);
            if (below.IsValid && !OpenAbove(below, map))
            {
                return true;
            }
            return L <= Levels.Max(map) && L != 0 && FloorAt(g, L, map);
        }

        // A sight-blocking building (wall, closed door, rock: the game's CanBeSeenOverFast) at level L over g. Stands
        // Heights.Wall high over its level's floor.
        public static bool BlockAt(IntVec3 g, int L, Map map)
        {
            IntVec3 c = Levels.Cell(g, L, map);
            return c.IsValid && c.InBounds(map) && !c.CanBeSeenOverFast(map);
        }

        // The top of the column over c: the highest cell with a floor over c's ground cell, else the ground cell (c
        // itself on a map without floors; Invalid for a void row). Whatever falls from the sky lands here (mortar shells,
        // orbital strikes, lightning: SkyStrikes); read it again for each hit, floors can be gone by then.
        public static IntVec3 Top(IntVec3 c, Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return c;
            }
            IntVec3 g = Levels.Ground(c, map);
            if (!g.IsValid)
            {
                return IntVec3.Invalid;
            }
            for (int L = a.MaxLevel; L >= 1; L--)
            {
                if (FloorAt(g, L, map))
                {
                    return Levels.Cell(g, L, map);
                }
            }
            return g;
        }

        // Compat G2 (Gravity.cs): where something at cell c comes down when c has no floor (open air on an upper band, or a
        // void row). Void rows belong to the band below them: the walk starts on that band's edge row (no fall when that
        // row has a floor there). Then down the column: the roof of each cell passed (the ceiling of the room below) is met
        // first (roofs: a thick roof stops the fall, Invalid; any other roof but the stairwell opening is listed in
        // 'broken' for the caller to break), then that cell's floor (the ground always counts). The landing is that cell
        // (standable: or the nearest standable cell on that floor); Invalid when nothing below holds. 'origin' is the cell
        // on c's own floor the walk started from (c, or the edge row cell of a void row), for the caller's fallback.
        // c with a floor (or a ground cell, or a map without floors) is returned as it is.
        public static IntVec3 Landing(IntVec3 c, Map map, bool standable, bool roofs, out int fallen, out IntVec3 origin, System.Collections.Generic.List<IntVec3> broken = null)
        {
            fallen = 0;
            origin = c;
            broken?.Clear();
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || !c.InBounds(map) || c.z < a.groundZ || ProjectZ.HasFloor(c, map))
            {
                return c;
            }
            int b = a.BandOfRow(c.z);
            IntVec3 g;
            int start;
            if (b < 0)
            {
                // a void row: the edge row of the band below it
                start = a.LevelOfBand(a.BandOfRowLoose(c.z));
                g = new IntVec3(c.x, 0, a.groundZ - 1);
                origin = Levels.Cell(g, start, map);
                if (!origin.IsValid)
                {
                    return IntVec3.Invalid;
                }
                if (FloorAt(g, start, map))
                {
                    return Settle(origin, map, start, standable, broken);
                }
            }
            else
            {
                start = a.LevelOfBand(b);
                g = Levels.Ground(c, map);
            }
            int lo = a.MinLevel;
            for (int L = start; L > lo; L--)
            {
                IntVec3 below = Levels.Cell(g, L - 1, map);
                if (!below.IsValid)
                {
                    break;
                }
                if (roofs)
                {
                    RoofDef r = map.roofGrid.RoofAt(below);
                    if (r != null && r != PZDefOf.PZ_StairwellRoof)
                    {
                        if (r.isThickRoof)
                        {
                            broken?.Clear();
                            return IntVec3.Invalid;
                        }
                        broken?.Add(below);
                    }
                }
                if (FloorAt(g, L - 1, map))
                {
                    fallen = start - (L - 1);
                    IntVec3 land = Settle(below, map, L - 1, standable, broken);
                    if (!land.IsValid)
                    {
                        fallen = 0;
                    }
                    return land;
                }
            }
            broken?.Clear();
            return IntVec3.Invalid;
        }

        private static IntVec3 Settle(IntVec3 cj, Map map, int level, bool standable, System.Collections.Generic.List<IntVec3> broken)
        {
            if (!standable || cj.Standable(map))
            {
                return cj;
            }
            IntVec3 near = Slab.NearestStandable(cj, map, level, false);
            if (!near.IsValid)
            {
                broken?.Clear();
            }
            return near;
        }

        // The level of the natural top over ground cell g:the highest level with a natural rock floor (a mountain top,
        // Floors.KindAt), else 0 (the ground). A walk over at most the map's levels of terrain lookups, so not cached.
        public static int NaturalTop(IntVec3 g, Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return 0;
            }
            for (int L = a.MaxLevel; L >= 1; L--)
            {
                IntVec3 c = Levels.Cell(g, L, map);
                if (c.IsValid && Floors.KindAt(c, map) == FloorKind.NaturalRock)
                {
                    return L;
                }
            }
            return 0;
        }

        // Height of level L's floor over the ground: in wall units (Heights, a wall = 1) and in cells (Range3D, as drawn).
        public static float WallHeight(int L) => L * Combat.Heights.Storey;

        public static float CellHeight(int L) => L * Combat.Range3D.Storey;
    }
}
