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

        // The level of the natural top over ground cell g: the highest level with a natural rock floor (a mountain top,
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
