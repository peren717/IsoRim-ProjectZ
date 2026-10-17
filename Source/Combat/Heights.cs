using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace ProjectZ.Combat
{
    // The height model for shots between floors (design §1.1 and §2.2, user decision A of 2026-10-15).
    //
    // Every cell is a column over its ground cell; heights are in wall units (a wall is 1 high). Floor k stands at
    // k * Storey. Between floor k-1 and floor k lies one horizontal plane per column, solid where the cell below has a
    // roof (but not a stairwell's, Column.OpenAbove) or the cell above has a floor (the slab): the two always go together (an upper floor needs a roof under it,
    // FloorRoof.cs). A line of sight runs from the shooter's eye (floor + Eye) to the target's middle (floor + Middle):
    //   - it is blocked where it crosses a solid plane (slabs and roofs: no shooting through floors or into roofed rooms
    //     from above), and where it passes a cell whose building blocks sight (walls, closed doors, rock) below that
    //     building's top on its floor (walls stand Wall high);
    //   - decision A: the end on the higher floor must stand at the edge of its slab toward the other end: the next cell
    //     along the line on its floor is open air, or a low parapet (sandbags, barricade) with open air right after it.
    //     So a pawn in the middle of a floor neither sees down nor is seen from below.
    // Same-floor shots are left to the game.
    public static class Heights
    {
        public const float Storey = 1.02f;  // slab of floor k at k * Storey (a floor sits just over the walls below)
        public const float Wall = 1f;       // top of a sight-blocking building over its floor
        public const float Eye = 0.8f;      // a shooter's eye over its floor
        public const float Middle = 0.5f;   // where a target is aimed at, over its floor

        // Cells a and b on the same map with floors, on two different floors (no void rows).
        public static bool CrossFloor(IntVec3 a, IntVec3 b, Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || a.z < an.groundZ && b.z < an.groundZ)
            {
                return false;
            }
            int ka = ProjectZ.FloorOf(a, map);
            int kb = ProjectZ.FloorOf(b, map);
            return ka != kb && ka >= 0 && kb >= 0;
        }

        // Cell c moved onto the floor of cell like (same ground cell); c itself without floors or for void rows.
        public static IntVec3 OnFloorOf(IntVec3 c, IntVec3 like, Map map)
        {
            int k = ProjectZ.FloorOf(like, map);
            IntVec3 g = ProjectZ.GroundCellOf(c, map);
            if (k < 0 || !g.IsValid)
            {
                return c;
            }
            IntVec3 r = ProjectZ.CellOnFloor(g, k, map);
            return r.IsValid ? r : c;
        }

        // Horizontal distance between two cells, each over its ground cell.
        public static float GroundDistance(IntVec3 a, IntVec3 b, Map map)
        {
            return ProjectZ.VirtualFlatDistance(a.ToVector3Shifted(), b.ToVector3Shifted(), map);
        }

        // An upper-floor cell with a floor on it (any terrain but open air).
        public static bool Slab(IntVec3 c, Map map)
        {
            return ProjectZ.HasFloor(c, map);
        }

        // The plane between level k-1 and level k over ground cell g is solid (k = the top level + 1 is the top floor's roof).
        public static bool Plane(IntVec3 g, int k, Map map)
        {
            IntVec3 below = ProjectZ.CellOnFloor(g, k - 1, map);
            if (below.IsValid && !Column.OpenAbove(below, map))
            {
                return true;
            }
            return k <= Levels.Max(map) && Slab(ProjectZ.CellOnFloor(g, k, map), map);
        }

        // Height of a building's top over its floor, for cover (walls Wall, others their fill).
        public static float CoverTop(Thing t)
        {
            return t.def.Fillage == FillCategory.Full ? Wall : t.def.fillPercent;
        }

        private static readonly List<IntVec3> line = new List<IntVec3>();

        // Ground cells from a to b, both included (Bresenham, as the game's line of sight walks).
        public static List<IntVec3> Line(IntVec3 a, IntVec3 b)
        {
            line.Clear();
            int x0 = a.x, z0 = a.z, x1 = b.x, z1 = b.z;
            int dx = Mathf.Abs(x1 - x0), dz = Mathf.Abs(z1 - z0);
            int sx = x0 < x1 ? 1 : -1, sz = z0 < z1 ? 1 : -1;
            int err = dx - dz;
            while (true)
            {
                line.Add(new IntVec3(x0, 0, z0));
                if (x0 == x1 && z0 == z1)
                {
                    break;
                }
                int e2 = 2 * err;
                if (e2 > -dz)
                {
                    err -= dz;
                    x0 += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    z0 += sz;
                }
            }
            return line;
        }

        // Line of sight from a shooter's cell to a target's cell on another floor.
        public static bool CanSee(IntVec3 from, IntVec3 to, Map map, float fromHeight = Eye, float toHeight = Middle)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null)
            {
                return false;
            }
            int ks = ProjectZ.FloorOf(from, map);
            int kt = ProjectZ.FloorOf(to, map);
            IntVec3 gs = ProjectZ.GroundCellOf(from, map);
            IntVec3 gt = ProjectZ.GroundCellOf(to, map);
            if (ks < 0 || kt < 0 || !gs.IsValid || !gt.IsValid || gs == gt)
            {
                return false;   // straight above or below: a slab is always between
            }
            if (ks != kt && !AtEdge(ks > kt ? gs : gt, ks > kt ? gt : gs, Mathf.Max(ks, kt), map))
            {
                return false;
            }
            float hs = ks * Storey + fromHeight;
            float ht = kt * Storey + toHeight;
            return Clear(gs, gt, hs, ht, map);
        }

        private static readonly List<IntVec3> seeDests = new List<IntVec3>();
        private static readonly List<IntVec3> seeSources = new List<IntVec3>();

        // A seer sees a thing on another floor: the game's AttackTargetFinder.CanSee (from its cell, then from its lean
        // cells, to the target's cell and its lean cells) with the height line of sight for each pair.
        public static bool CanSeeLeaning(Thing seer, Thing target)
        {
            Map map = seer.Map;
            IntVec3 from = seer.Position;
            try
            {
                ShootLeanUtility.CalcShootableCellsOf(seeDests, target, from);
                for (int i = 0; i < seeDests.Count; i++)
                {
                    if (CanSee(from, seeDests[i], map))
                    {
                        return true;
                    }
                }
                ShootLeanUtility.LeanShootingSourcesFromTo(from, target.Position, map, seeSources);
                for (int j = 0; j < seeSources.Count; j++)
                {
                    if (!seeSources[j].CanBeSeenOver(map))
                    {
                        continue;
                    }
                    for (int i = 0; i < seeDests.Count; i++)
                    {
                        if (CanSee(seeSources[j], seeDests[i], map))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
            finally
            {
                seeDests.Clear();
                seeSources.Clear();
            }
        }

        // Decision A: the end on floor k (ground cell g) looks over the edge of its slab toward the ground cell other.
        public static bool AtEdge(IntVec3 g, IntVec3 other, int k, Map map)
        {
            List<IntVec3> l = Line(g, other);
            if (l.Count < 2)
            {
                return false;
            }
            IntVec3 c1 = ProjectZ.CellOnFloor(l[1], k, map);
            if (!c1.IsValid)
            {
                return false;
            }
            if (!Slab(c1, map))
            {
                return true;
            }
            // A low parapet on the last cell of the floor: open air right behind it.
            Building b = c1.GetEdifice(map);
            if (b == null || b.def.Fillage != FillCategory.Partial || l.Count < 3)
            {
                return false;
            }
            IntVec3 c2 = ProjectZ.CellOnFloor(l[2], k, map);
            return c2.IsValid && !Slab(c2, map);
        }

        // Walks the line between two ground cells with its heights at each end: no solid plane crossed, no sight-blocking
        // building passed below its top (the end cells themselves don't block).
        public static bool Clear(IntVec3 gs, IntVec3 gt, float hs, float ht, Map map)
        {
            int lowest = Levels.Min(map), top = Levels.Max(map);
            List<IntVec3> l = Line(gs, gt);
            int n = l.Count - 1;
            float len = Mathf.Max((gt - gs).LengthHorizontal, 0.5f);
            float half = 0.6f / len;
            for (int i = 0; i <= n; i++)
            {
                IntVec3 g = l[i];
                float t = i == 0 ? 0f : (i == n ? 1f : (g - gs).LengthHorizontal / len);
                float t0 = Mathf.Clamp01(t - half);
                float t1 = Mathf.Clamp01(t + half);
                float a = Mathf.Lerp(hs, ht, t0);
                float b = Mathf.Lerp(hs, ht, t1);
                float lo = Mathf.Min(a, b);
                float hi = Mathf.Max(a, b);
                for (int k = lowest + 1; k <= top + 1; k++)
                {
                    float level = k * Storey;
                    if (level >= lo && level <= hi && Plane(g, k, map))
                    {
                        return false;
                    }
                }
                if (i == 0 || i == n)
                {
                    continue;
                }
                for (int k = lowest; k <= top; k++)
                {
                    float bottom = k * Storey;
                    if (hi < bottom || lo > bottom + Wall)
                    {
                        continue;
                    }
                    IntVec3 c = ProjectZ.CellOnFloor(g, k, map);
                    if (c.IsValid && c.InBounds(map) && !c.CanBeSeenOverFast(map))
                    {
                        // Blocked only where the line is below the top of what stands there.
                        if (lo <= bottom + Wall)
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        // Height of the line from (gs, hs) to (gt, ht) over ground cell g.
        public static float HeightAt(IntVec3 gs, IntVec3 gt, float hs, float ht, IntVec3 g)
        {
            float len = (gt - gs).LengthHorizontal;
            if (len < 0.01f)
            {
                return ht;
            }
            float t = Mathf.Clamp01((g - gs).LengthHorizontal / len);
            return Mathf.Lerp(hs, ht, t);
        }
    }
}
