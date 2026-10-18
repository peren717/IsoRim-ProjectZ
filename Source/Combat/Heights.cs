using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace ProjectZ.Combat
{
    // Heights between floors in wall units (a wall is 1 high, floor k at k * Storey), for cover by height (HeightCover),
    // projectile flights (Flights), explosions (Explosions3D with Clear) and shields. Line of sight between floors is
    // Los3D's (cells, the game's own walk); CanSee and CanSeeLeaning here are the entry points kept for callers.
    // Between floor k-1 and floor k lies one horizontal plane per column, solid where the cell below has a roof (but not
    // a stairwell's, Column.OpenAbove) or the cell above has a floor (the slab): the two always go together (an upper
    // floor needs a roof under it, FloorRoof.cs). Same-floor shots are left to the game.
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

        // The plane between floor k-1 and floor k over ground cell g is solid (k >= 1; k = floors is the top floor's roof).
        public static bool Plane(IntVec3 g, int k, Map map, int floors)
        {
            IntVec3 below = ProjectZ.CellOnFloor(g, k - 1, map);
            if (below.IsValid && !Column.OpenAbove(below, map))
            {
                return true;
            }
            return k < floors && Slab(ProjectZ.CellOnFloor(g, k, map), map);
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

        // Line of sight from a shooter's cell to a target's cell on another floor (Los3D).
        public static bool CanSee(IntVec3 from, IntVec3 to, Map map)
        {
            return Los3D.CanSee(from, to, map);
        }

        private static readonly List<IntVec3> seeDests = new List<IntVec3>();
        private static readonly List<IntVec3> seeSources = new List<IntVec3>();

        // A seer sees a thing on another floor: the game's AttackTargetFinder.CanSee (from its cell, then from its lean
        // cells, to the target's cell and its lean cells) with Los3D for each pair; the validator as the game's (blind smoke).
        public static bool CanSeeLeaning(Thing seer, Thing target, Func<IntVec3, bool> validator = null)
        {
            Map map = seer.Map;
            IntVec3 from = seer.Position;
            try
            {
                ShootLeanUtility.CalcShootableCellsOf(seeDests, target, from);
                for (int i = 0; i < seeDests.Count; i++)
                {
                    if (Los3D.CanSee(from, seeDests[i], map, validator: validator))
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
                        if (Los3D.CanSee(seeSources[j], seeDests[i], map, validator: validator))
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

        // Walks the line between two ground cells with its heights at each end: no solid plane crossed, no sight-blocking
        // building passed below its top (the end cells themselves don't block). Explosions' height line (Explosions3D).
        public static bool Clear(IntVec3 gs, IntVec3 gt, float hs, float ht, Map map, int floors)
        {
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
                for (int k = 1; k <= floors; k++)
                {
                    float level = k * Storey;
                    if (level >= lo && level <= hi && Plane(g, k, map, floors))
                    {
                        return false;
                    }
                }
                if (i == 0 || i == n)
                {
                    continue;
                }
                for (int k = 0; k < floors; k++)
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
