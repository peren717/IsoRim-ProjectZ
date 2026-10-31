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
    //   - Per walked column the line's height range is the segment clipped to that cell, stretched back to where the
    //     previous column's ended (PartEnd), so no height between the ends falls between two columns at a corner.
    //   - Blocks (Column.BlockAt): a cell whose building blocks sight (walls, closed doors, natural rock and ore on any
    //     level: the game's CanBeSeenOverFast) on a level whose storey (floor to wall top) the height range reaches. The
    //     first and the last cell are exempt, as in the game.
    //   - Planes (Column.PlaneSolid): the line may not cross the plane between two levels where it is solid (a roof over
    //     the cell below but not a stairwell's, thick rock roofs included, or a floor on the cell above: slabs, ship decks,
    //     natural rock floors of mountain tops), in any walked cell, ends included.
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
            int ks = Levels.LevelOf(src, map);
            int kt = Levels.LevelOf(dst, map);
            IntVec3 gs = ProjectZ.GroundCellOf(src, map);
            IntVec3 gt = ProjectZ.GroundCellOf(dst, map);
            if (ks == Levels.None || kt == Levels.None || !gs.IsValid || !gt.IsValid)
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
            int ks = Levels.LevelOf(src, map);
            int kt = Levels.LevelOf(dst, map);
            IntVec3 gs = ProjectZ.GroundCellOf(src, map);
            IntVec3 gt = ProjectZ.GroundCellOf(dst, map);
            if (a == null || ks == Levels.None || kt == Levels.None || !gs.IsValid || !gt.IsValid)
            {
                return;
            }
            float storey = IsoRimBridge.StoreyHeight;
            float hs = ks * storey + Eye;
            float ht = kt * storey + aim;
            Walker w = new Walker(gs, gt, 0, 0);
            float t1 = 0f;
            while (true)
            {
                float t0 = t1;
                t1 = PartEnd(gs.x, gs.z, gt.x, gt.z, w.x, w.z, w.left <= 1, t0);
                float mid = hs + (ht - hs) * (t0 + t1) * 0.5f;
                int k = Mathf.Clamp(Mathf.FloorToInt(mid / storey), a.MinLevel, a.MaxLevel);
                into.Add(Levels.Cell(new IntVec3(w.x, 0, w.z), k, map));
                if (w.left <= 1)
                {
                    break;
                }
                w.Step();
            }
        }

        // ---- Api 6: the same walk with arbitrary heights, and the walk itself as steps

        // Line of sight from src at hs to dst at ht (cells over each end's floor): the blocks of CanSee (one aim, no
        // corner walks). Works on a map without floors too (one level: walls, and roofs above the storey).
        public static bool Clear(IntVec3 src, float hs, IntVec3 dst, float ht, Map map, Func<IntVec3, bool> validator = null)
        {
            Calls++;
            int ks = Levels.LevelOf(src, map);
            int kt = Levels.LevelOf(dst, map);
            IntVec3 gs = ProjectZ.GroundCellOf(src, map);
            IntVec3 gt = ProjectZ.GroundCellOf(dst, map);
            if (ks == Levels.None || kt == Levels.None || !gs.IsValid || !gt.IsValid)
            {
                return false;
            }
            float storey = IsoRimBridge.StoreyHeight;
            float wall = IsoRimBridge.WallHeight;
            int lowest = Levels.Min(map), top = Levels.Max(map);
            float h0 = ks * storey + hs;
            float h1 = kt * storey + ht;
            Walker w = new Walker(gs, gt, 0, 0);
            bool first = true;
            float b = h0;
            float t1 = 0f;
            while (true)
            {
                bool last = w.left <= 1;
                t1 = PartEnd(gs.x, gs.z, gt.x, gt.z, w.x, w.z, last, t1);
                float a = b;
                b = last ? h1 : h0 + (h1 - h0) * t1;
                if (Blocked(map, new IntVec3(w.x, 0, w.z), a, b, first, last, storey, wall, lowest, top, !first && !last, validator))
                {
                    return false;
                }
                if (last)
                {
                    return true;
                }
                w.Step();
                first = false;
            }
        }

        // The walk from virtual point 'from' to 'to' (x, z over the ground rows, a cell spanning [x, x + 1); y the absolute
        // height in cells, level k's floor at k * StoreyHeight) over the ground columns, the game's cell sequence between
        // the two end cells. Per column (Api.LineStep): the cell on the level the line's middle is on there (clamped to the
        // map's levels), the line's height range over that level's floor, the first solid plane (Column.PlaneSolid) the
        // line crosses in that column in its direction of travel (int.MinValue: none). 'into' is cleared first; returns
        // its count; 0 when an end is off the ground rows. stopAtSolidPlane: ends with the column of the first solid plane.
        public static int Walk(Vector3 from, Vector3 to, Map map, List<Api.LineStep> into, bool stopAtSolidPlane)
        {
            into.Clear();
            int gz = ProjectZ.GroundRect(map).maxZ + 1;
            IntVec3 gs = new IntVec3(Mathf.FloorToInt(from.x), 0, Mathf.FloorToInt(from.z));
            IntVec3 gt = new IntVec3(Mathf.FloorToInt(to.x), 0, Mathf.FloorToInt(to.z));
            if (gs.x < 0 || gs.z < 0 || gs.x >= map.Size.x || gs.z >= gz || gt.x < 0 || gt.z < 0 || gt.x >= map.Size.x || gt.z >= gz)
            {
                return 0;
            }
            float storey = IsoRimBridge.StoreyHeight;
            int lowest = Levels.Min(map), top = Levels.Max(map);
            // the segment in the walk's frame (cell centres at integer coordinates)
            float x0 = from.x - 0.5f, z0 = from.z - 0.5f, x1 = to.x - 0.5f, z1 = to.z - 0.5f;
            Walker w = new Walker(gs, gt, 0, 0);
            float b = from.y;
            float t1 = 0f;
            while (true)
            {
                IntVec3 g = new IntVec3(w.x, 0, w.z);
                bool last = w.left <= 1;
                t1 = PartEnd(x0, z0, x1, z1, w.x, w.z, last, t1);
                float a = b;
                b = last ? to.y : from.y + (to.y - from.y) * t1;
                float lo = a < b ? a : b, hi = a < b ? b : a;
                int k = Mathf.Clamp(Mathf.FloorToInt((lo + hi) * 0.5f / storey), lowest, top);
                int plane = FirstPlane(map, g, a, b, into.Count == 0, last, storey, lowest, top);
                into.Add(new Api.LineStep { cell = Levels.Cell(g, k, map), floor = k, lo = lo - k * storey, hi = hi - k * storey, solidPlane = plane });
                if (last || stopAtSolidPlane && plane != int.MinValue)
                {
                    break;
                }
                w.Step();
            }
            return into.Count;
        }

        // One walk for all aim heights (a bitmask of the ones still clear).
        private static bool Trace(Map map, AnnexMapComponent a, IntVec3 gs, int ks, IntVec3 gt, int kt, int halfX, int halfZ, Func<IntVec3, bool> validator, out float aim)
        {
            aim = 0f;
            float storey = IsoRimBridge.StoreyHeight;
            float wall = IsoRimBridge.WallHeight;
            int lowest = a.MinLevel, top = a.MaxLevel;
            float hs = ks * storey + Eye;
            float ht0 = kt * storey + Aims[0], ht1 = kt * storey + Aims[1], ht2 = kt * storey + Aims[2];
            // the segment over the ground (cell centres at integer coordinates); toward an edge for the half-offset walks
            float x1 = gt.x + halfX * 0.5f;
            float z1 = gt.z + halfZ * 0.5f;
            int clear = AllAims;
            Walker w = new Walker(gs, gt, halfX, halfZ);
            bool first = true;
            float t1 = 0f;
            while (true)
            {
                bool last = w.left <= 1;
                IntVec3 g = new IntVec3(w.x, 0, w.z);
                float t0 = t1;
                t1 = PartEnd(gs.x, gs.z, x1, z1, w.x, w.z, last, t0);
                for (int j = 0; j < 3; j++)
                {
                    int bit = 1 << j;
                    if ((clear & bit) == 0)
                    {
                        continue;
                    }
                    float ht = j == 0 ? ht0 : (j == 1 ? ht1 : ht2);
                    float h0 = hs + (ht - hs) * t0;
                    float h1 = last ? ht : hs + (ht - hs) * t1;
                    if (Blocked(map, g, h0, h1, first, last, storey, wall, lowest, top, !first && !last, validator))
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

        // Column g's part of the line (PartEnd), from height a to b; end: the line ends in this part. Levels lowest..top
        // of the map; level k's floor at k * storey (negative below the ground).
        private static bool Blocked(Map map, IntVec3 g, float a, float b, bool start, bool end, float storey, float wall, int lowest, int top, bool inner, Func<IntVec3, bool> validator)
        {
            // planes crossed, the end columns included
            if (FirstPlane(map, g, a, b, start, end, storey, lowest, top) != int.MinValue)
            {
                return true;
            }
            float lo = a < b ? a : b, hi = a < b ? b : a;
            int kLo = Mathf.FloorToInt(lo / storey);
            int kHi = Mathf.FloorToInt(hi / storey);
            if (!inner)
            {
                return false;
            }
            // sight-blocking buildings of the storeys the range reaches
            for (int k = Mathf.Max(lowest, kLo); k <= kHi && k <= top; k++)
            {
                float bottom = k * storey;
                if (hi >= bottom && lo <= bottom + wall && Column.BlockAt(g, k, map))
                {
                    return true;
                }
            }
            if (validator != null)
            {
                int kf = Mathf.Clamp(Mathf.FloorToInt((lo + hi) * 0.5f / storey), lowest, top);
                if (!validator(Levels.Cell(g, kf, map)))
                {
                    return true;
                }
            }
            return false;
        }

        // The walk's columns split the line: a column's part runs from where the previous column's part ended (start; 0
        // for the first) to where the line leaves the cell (Clip's t1, never back), the last column's to 1. The parts meet
        // end to start, so every height between the line's ends lies in some column's part: the stretch the line spends
        // in a cell the walk skips (beside a corner it passes through or near) goes to the walk's columns there.
        private static float PartEnd(float x0, float z0, float x1, float z1, int cx, int cz, bool last, float start)
        {
            if (last)
            {
                return 1f;
            }
            Clip(x0, z0, x1, z1, cx, cz, out _, out float t1);
            return t1 < start ? start : (t1 > 1f ? 1f : t1);
        }

        // The first solid plane (Column.PlaneSolid's upper level; top + 1 = the top level's roof) crossed over column g by
        // the line's part from height a to b, in the direction of travel; int.MinValue: none. A level met exactly where a
        // part ends is crossed in that part (the next part starts there and doesn't count it again). One met exactly at
        // the line's own end (end: this part ends the line) is crossed when the line comes up to it (it ends on that
        // level's floor from below, at the plane's underside), not when it comes down onto it; one met exactly at the
        // line's own start (start: the first part) likewise when the line leaves it downward, not upward.
        private static int FirstPlane(Map map, IntVec3 g, float a, float b, bool start, bool end, float storey, int lowest, int top)
        {
            if (a == b)
            {
                return int.MinValue;
            }
            bool up = b > a;
            float lo = up ? a : b, hi = up ? b : a;
            int pLo = Mathf.Max(lowest + 1, Mathf.FloorToInt(lo / storey));
            int pHi = Mathf.Min(top + 1, Mathf.FloorToInt(hi / storey) + 1);
            for (int i = 0; i <= pHi - pLo; i++)
            {
                int p = up ? pLo + i : pHi - i;
                float level = p * storey;
                if ((level > lo && level < hi || level == b && (!end || up) || level == a && start && !up) && Column.PlaneSolid(g, p, map))
                {
                    return p;
                }
            }
            return int.MinValue;
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
}
