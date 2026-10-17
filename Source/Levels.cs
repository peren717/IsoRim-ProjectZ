using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // Mountains foundation F1 (isorim-pz-mountains-design.md §3): levels and bands.
    //
    // A BAND is a block of map rows: band 0 = the ground rows [0, groundZ), band b >= 1 = rows [b*stride, b*stride +
    // groundZ), followed by its void rows. A LEVEL is a height: 0 = the ground, 1 = the storey above it, -1 one below
    // (basements, later). Each band holds one level (AnnexMapComponent.bandLevel, saved; missing = identity, which is all
    // maps today). Assigned levels always form one run Min..Max that contains 0, and band 0 is always level 0.
    //
    // This class is the only place (with AnnexMapComponent and the layout code in MapGenPatches) that turns rows into
    // bands or bands into rows: everything else asks for levels and cells. ProjectZ.FloorOf / CellOnFloor / GroundCellOf
    // are thin wrappers (FloorOf: -1 for a void row, valid while no level is below the ground; use LevelOf where
    // negative levels must work).
    //
    // Off the assigned range (cells past the map's north edge, levels over the top) the mapping goes on 1:1 from the
    // nearest end, so arithmetic on such cells stays what it was; Cell() and Has() never accept them.
    public static class Levels
    {
        // LevelOf for a void row: no level.
        public const int None = int.MinValue;

        // Lowest and highest level of the map (0 and 0 without floors).
        public static int Min(Map map) => ProjectZ.Annex(map)?.MinLevel ?? 0;

        public static int Max(Map map) => ProjectZ.Annex(map)?.MaxLevel ?? 0;

        // Number of levels (1 without floors).
        public static int Count(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            return a == null ? 1 : a.MaxLevel - a.MinLevel + 1;
        }

        // Level L exists on the map.
        public static bool Has(Map map, int L)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            return a == null ? L == 0 : L >= a.MinLevel && L <= a.MaxLevel;
        }

        // The map's levels, lowest first (cold paths; hot loops run Min..Max).
        public static IEnumerable<int> All(Map map)
        {
            int lo = Min(map), hi = Max(map);
            for (int L = lo; L <= hi; L++)
            {
                yield return L;
            }
        }

        // The band holding level L (-1: none; 0 for level 0 without floors).
        public static int BandOf(Map map, int L)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return L == 0 ? 0 : -1;
            }
            return Has(map, L) ? a.BandOfLevel(L) : -1;
        }

        // The level band b holds (None: not a band of this map).
        public static int LevelOfBand(Map map, int b)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return b == 0 ? 0 : None;
            }
            return IsAssigned(map, b) ? a.LevelOfBand(b) : None;
        }

        // Band b exists and holds a level.
        public static bool IsAssigned(Map map, int b)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            return a == null ? b == 0 : b >= 0 && b < a.floors;
        }

        // The band of a cell: 0 for the ground (and every cell without floors), -1 for a void row.
        public static int BandOfCell(IntVec3 c, Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            return a == null ? 0 : a.BandOfRow(c.z);
        }

        // The level of a cell (None for a void row; 0 for every cell without floors).
        public static int LevelOf(IntVec3 c, Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return 0;
            }
            int b = a.BandOfRow(c.z);
            return b < 0 ? None : a.LevelOfBand(b);
        }

        // Ground cell g at level L (L = 0 returns g). Invalid when the map has no such level or g is not a ground cell.
        public static IntVec3 Cell(IntVec3 g, int L, Map map)
        {
            if (L == 0)
            {
                return g;
            }
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || L < a.MinLevel || L > a.MaxLevel || g.z < 0 || g.z >= a.groundZ)
            {
                return IntVec3.Invalid;
            }
            return new IntVec3(g.x, g.y, g.z + a.BandOfLevel(L) * a.stride);
        }

        // The ground cell under (or at) c; Invalid for a void row.
        public static IntVec3 Ground(IntVec3 c, Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || c.z < a.groundZ)
            {
                return c;
            }
            int b = a.BandOfRow(c.z);
            return b < 0 ? IntVec3.Invalid : new IntVec3(c.x, c.y, c.z - b * a.stride);
        }

        // Rows from the ground to level L's band: a ground row z is row z + Shift(L) on level L (0 without floors).
        public static int Shift(Map map, int L)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            return a == null ? 0 : a.BandOfLevel(L) * a.stride;
        }

        // The level whose band a world row belongs to, its void rows counted with it (drawn and flying positions, misses
        // between bands); 0 without floors.
        public static int LevelOfRowLoose(Map map, int row)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            return a == null ? 0 : a.LevelOfBand(a.BandOfRowLoose(row));
        }

        // The same as a ground z: a world z (any row, void rows with their band) moved down onto the ground rows.
        public static float GroundZLoose(Map map, float z)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            return a == null ? z : z - a.BandOfRowLoose(Mathf.FloorToInt(z)) * a.stride;
        }

        // The rows of level L's band, lowest and highest (inclusive). Without floors: the whole map for level 0.
        public static int RowMin(Map map, int L)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            return a == null ? 0 : a.BandOfLevel(L) * a.stride;
        }

        public static int RowMax(Map map, int L)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            return a == null ? map.Size.z - 1 : a.BandOfLevel(L) * a.stride + a.groundZ - 1;
        }
    }
}
