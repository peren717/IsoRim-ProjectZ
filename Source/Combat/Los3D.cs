using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace ProjectZ.Combat
{
    // Line of sight between floors (user decisions 2026-10-18, isorim-pz-los3d-plan.md): the game's own sight walk
    // (GenSight.LineOfSight, its exact cell sequence and corner rule) over the ground columns, with a height.
    //   - The line runs from the source cell's centre at its floor + Eye to the target cell's centre at its floor + an aim
    //     height (head, chest, waist); any clear aim height = can see.
    //   - Per walked column the line's height range is the segment clipped to that cell.
    //   - Blocks: a cell whose building blocks sight (walls, closed doors, rock: the game's CanBeSeenOverFast) on a floor
    //     whose storey (floor to wall top) the height range reaches. The first and the last cell are exempt, as in the game.
    //   - Planes: the line may not cross the plane between two floors where it is solid (a roof over the cell below but not
    //     a stairwell's, or a floor on the cell above: slabs, ship decks), in any walked cell, ends included.
    //   - Nothing else blocks: sandbags, trees, furniture, pawns and open doors give cover as in the game.
    // Same-floor pairs never get here: the game's own code runs for them (Verb_CanHitCell_Patch, AttackTargetFinder_CanSee_Patch).
    public static class Los3D
    {
        // ---- the heights, in cells over the floor (the one place for them; IsoRim's human is 1.25 cells tall)
        public const float Eye = 1.10f;        // every shooter (pawns, turrets, animals), at its cell centre
        public const float AimHead = 1.10f;    // aim points at the target cell's centre, tried in this order
        public const float AimChest = 0.80f;
        public const float AimWaist = 0.50f;

        private static readonly float[] Aims = { AimHead, AimChest, AimWaist };
        private const int AllAims = 7;

        public static int Calls;   // test counter (cheap, always on)

        public static bool CanSee(IntVec3 src, IntVec3 dst, Map map, bool includeCorners = false, Func<IntVec3, bool> validator = null)
        {
            return CanSee(src, dst, map, out _, includeCorners, validator);
        }

        // As above; aim = the first clear aim height (cells over the target's floor).
        public static bool CanSee(IntVec3 src, IntVec3 dst, Map map, out float aim, bool includeCorners = false, Func<IntVec3, bool> validator = null)
        {
            aim = 0f;
            Calls++;
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return false;
            }
            int ks = ProjectZ.FloorOf(src, map);
            int kt = ProjectZ.FloorOf(dst, map);
            IntVec3 gs = ProjectZ.GroundCellOf(src, map);
            IntVec3 gt = ProjectZ.GroundCellOf(dst, map);
            if (ks < 0 || kt < 0 || !gs.IsValid || !gt.IsValid)
            {
                return false;
            }
            if (Trace(map, a, gs, ks, gt, kt, 0, 0, validator, out aim))
            {
                return true;
            }
            if (!includeCorners)
            {
                return false;
            }
            // GenSight.LineOfSightToEdges: the walks toward the target's edges that face the source
            int whole = (gs * 2).DistanceToSquared(gt * 2);
            for (int i = 0; i < 4; i++)
            {
                IntVec3 d = GenAdj.CardinalDirections[i];
                if ((gs * 2).DistanceToSquared(gt * 2 + d) <= whole && Trace(map, a, gs, ks, gt, kt, d.x, d.z, validator, out aim))
                {
                    return true;
                }
            }
            return false;
        }

        // The ground columns the walk visits from a to b (both included), the game's sequence (tests).
        public static void Walk(IntVec3 a, IntVec3 b, int halfX, int halfZ, List<IntVec3> into)
        {
            into.Clear();
            Walker w = new Walker(a, b, halfX, halfZ);
            while (true)
            {
                into.Add(new IntVec3(w.x, 0, w.z));
                if (w.left <= 1)
                {
                    break;
                }
                w.Step();
            }
        }

        // The cells the line is in (per walked column, the cell on the floor the line is on there), source to target,
        // with the given aim height (the shot report's smoke check).
        public static void LineCells(IntVec3 src, IntVec3 dst, Map map, float aim, List<IntVec3> into)
        {
            into.Clear();
            AnnexMapComponent a = ProjectZ.Annex(map);
            int ks = ProjectZ.FloorOf(src, map);
            int kt = ProjectZ.FloorOf(dst, map);
            IntVec3 gs = ProjectZ.GroundCellOf(src, map);
            IntVec3 gt = ProjectZ.GroundCellOf(dst, map);
            if (a == null || ks < 0 || kt < 0 || !gs.IsValid || !gt.IsValid)
            {
                return;
            }
            float storey = IsoRimBridge.StoreyHeight;
            float hs = ks * storey + Eye;
            float ht = kt * storey + aim;
            Walker w = new Walker(gs, gt, 0, 0);
            while (true)
            {
                Clip(gs.x, gs.z, gt.x, gt.z, w.x, w.z, out float t0, out float t1);
                float mid = hs + (ht - hs) * (t0 + t1) * 0.5f;
                int k = Mathf.Clamp(Mathf.FloorToInt(mid / storey), 0, a.floors - 1);
                into.Add(LosColumn.At(new IntVec3(w.x, 0, w.z), k, a));
                if (w.left <= 1)
                {
                    break;
                }
                w.Step();
            }
        }

        // One walk for all aim heights (a bitmask of the ones still clear).
        private static bool Trace(Map map, AnnexMapComponent a, IntVec3 gs, int ks, IntVec3 gt, int kt, int halfX, int halfZ, Func<IntVec3, bool> validator, out float aim)
        {
            aim = 0f;
            float storey = IsoRimBridge.StoreyHeight;
            float wall = IsoRimBridge.WallHeight;
            int floors = a.floors;
            float hs = ks * storey + Eye;
            float ht0 = kt * storey + Aims[0], ht1 = kt * storey + Aims[1], ht2 = kt * storey + Aims[2];
            // the segment over the ground (cell centres at integer coordinates); toward an edge for the half-offset walks
            float x1 = gt.x + halfX * 0.5f;
            float z1 = gt.z + halfZ * 0.5f;
            int clear = AllAims;
            Walker w = new Walker(gs, gt, halfX, halfZ);
            bool first = true;
            while (true)
            {
                bool last = w.left <= 1;
                IntVec3 g = new IntVec3(w.x, 0, w.z);
                Clip(gs.x, gs.z, x1, z1, w.x, w.z, out float t0, out float t1);
                for (int j = 0; j < 3; j++)
                {
                    int bit = 1 << j;
                    if ((clear & bit) == 0)
                    {
                        continue;
                    }
                    float ht = j == 0 ? ht0 : (j == 1 ? ht1 : ht2);
                    float h0 = hs + (ht - hs) * t0;
                    float h1 = hs + (ht - hs) * t1;
                    float lo = h0 < h1 ? h0 : h1;
                    float hi = h0 < h1 ? h1 : h0;
                    if (Blocked(map, a, g, lo, hi, storey, wall, floors, !first && !last, validator))
                    {
                        clear &= ~bit;
                    }
                }
                if (clear == 0)
                {
                    return false;
                }
                if (last)
                {
                    break;
                }
                w.Step();
                first = false;
            }
            aim = (clear & 1) != 0 ? Aims[0] : ((clear & 2) != 0 ? Aims[1] : Aims[2]);
            return true;
        }

        private static bool Blocked(Map map, AnnexMapComponent a, IntVec3 g, float lo, float hi, float storey, float wall, int floors, bool inner, Func<IntVec3, bool> validator)
        {
            int kLo = Mathf.FloorToInt(lo / storey);
            int kHi = Mathf.FloorToInt(hi / storey);
            // planes crossed (k * storey strictly inside the height range), the ends included
            for (int k = Mathf.Max(1, kLo); k <= kHi && k <= floors; k++)
            {
                float level = k * storey;
                if (lo < level && level < hi && LosColumn.Plane(g, k, map, a))
                {
                    return true;
                }
            }
            if (!inner)
            {
                return false;
            }
            // sight-blocking buildings of the storeys the range reaches
            for (int k = Mathf.Max(0, kLo); k <= kHi && k < floors; k++)
            {
                float bottom = k * storey;
                if (hi >= bottom && lo <= bottom + wall && LosColumn.Block(g, k, map, a))
                {
                    return true;
                }
            }
            if (validator != null)
            {
                int kf = Mathf.Clamp(Mathf.FloorToInt((lo + hi) * 0.5f / storey), 0, floors - 1);
                if (!validator(LosColumn.At(g, kf, a)))
                {
                    return true;
                }
            }
            return false;
        }

        // Liang-Barsky: the part [t0, t1] of the segment (x0,z0)-(x1,z1) inside cell (cx, cz); a cell the walk takes that
        // the segment misses (or only touches at a corner): its nearest point.
        private static void Clip(float x0, float z0, float x1, float z1, int cx, int cz, out float t0, out float t1)
        {
            t0 = 0f;
            t1 = 1f;
            float dx = x1 - x0, dz = z1 - z0;
            if (ClipEdge(-dx, x0 - (cx - 0.5f), ref t0, ref t1) && ClipEdge(dx, (cx + 0.5f) - x0, ref t0, ref t1)
                && ClipEdge(-dz, z0 - (cz - 0.5f), ref t0, ref t1) && ClipEdge(dz, (cz + 0.5f) - z0, ref t0, ref t1))
            {
                return;
            }
            float len2 = dx * dx + dz * dz;
            float t = len2 < 1e-6f ? 1f : Mathf.Clamp01(((cx - x0) * dx + (cz - z0) * dz) / len2);
            t0 = t1 = t;
        }

        private static bool ClipEdge(float p, float q, ref float t0, ref float t1)
        {
            if (Mathf.Abs(p) < 1e-7f)
            {
                return q >= 0f;
            }
            float r = q / p;
            if (p < 0f)
            {
                if (r > t1)
                {
                    return false;
                }
                if (r > t0)
                {
                    t0 = r;
                }
            }
            else
            {
                if (r < t0)
                {
                    return false;
                }
                if (r < t1)
                {
                    t1 = r;
                }
            }
            return true;
        }

        // GenSight.LineOfSight's walk (1.7, the overload with half offsets), step by step: 1 + dx + dz cells.
        private struct Walker
        {
            public int x, z, left;
            private readonly int xi, zi, ex, ez;
            private readonly bool sideOnEqual;
            private int err;

            public Walker(IntVec3 a, IntVec3 b, int halfX, int halfZ)
            {
                sideOnEqual = a.x != b.x ? a.x < b.x : a.z < b.z;
                int dx = Mathf.Abs(b.x - a.x);
                int dz = Mathf.Abs(b.z - a.z);
                x = a.x;
                z = a.z;
                left = 1 + dx + dz;
                xi = b.x > a.x ? 1 : -1;
                zi = b.z > a.z ? 1 : -1;
                ex = dx * 4 + halfX * 2;
                ez = dz * 4 + halfZ * 2;
                err = ex / 2 - ez / 2;
            }

            public void Step()
            {
                if (err > 0 || err == 0 && sideOnEqual)
                {
                    x += xi;
                    err -= ez;
                }
                else
                {
                    z += zi;
                    err += ex;
                }
                left--;
            }
        }
    }

    // Where Los3D reads the columns (the one adapter to the floor model): floor k's cell over ground cell g, the plane
    // between floor k-1 and floor k, and a sight-blocking building on floor k.
    internal static class LosColumn
    {
        public static IntVec3 At(IntVec3 g, int k, AnnexMapComponent a)
        {
            return k == 0 ? g : new IntVec3(g.x, 0, g.z + k * a.stride);
        }

        // Solid where the cell below has a roof (not a stairwell's) or floor k has a floor on it (k = floors: the top
        // floor's roof).
        public static bool Plane(IntVec3 g, int k, Map map, AnnexMapComponent a)
        {
            if (!Column.OpenAbove(At(g, k - 1, a), map))
            {
                return true;
            }
            return k < a.floors && ProjectZ.HasFloor(At(g, k, a), map);
        }

        public static bool Block(IntVec3 g, int k, Map map, AnnexMapComponent a)
        {
            return !At(g, k, a).CanBeSeenOverFast(map);
        }
    }
}
