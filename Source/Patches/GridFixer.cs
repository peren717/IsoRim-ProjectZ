using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace ProjectZ.Patches
{
    // Grids made from map.Size while the map was shrunk for generation (areas from AddStartingAreas, lazily made
    // scratch grids such as FloodFiller.parentGrid ...) have only the ground rows. After the size is restored they are
    // resized to the full map, keeping their ground contents (indices are unchanged because the width is unchanged).
    // Searched: the map's fields, the fields of those objects (two levels), areas, zones, plans and map components.
    internal static class GridFixer
    {
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static int Fix(Map map, int groundCells)
        {
            int fixedCount = 0;
            HashSet<object> seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            List<object> roots = new List<object> { map };
            foreach (FieldInfo f in typeof(Map).GetFields(Inst))
            {
                if (!f.FieldType.IsValueType && f.FieldType != typeof(string))
                {
                    object v = f.GetValue(map);
                    if (v != null)
                    {
                        roots.Add(v);
                    }
                }
            }
            if (map.areaManager?.AllAreas != null)
            {
                roots.AddRange(map.areaManager.AllAreas);
            }
            if (map.zoneManager?.AllZones != null)
            {
                roots.AddRange(map.zoneManager.AllZones);
            }
            if (map.planManager?.AllPlans != null)
            {
                roots.AddRange(map.planManager.AllPlans);
            }
            if (map.components != null)
            {
                roots.AddRange(map.components);
            }
            foreach (object root in roots)
            {
                if (root == null || !seen.Add(root) || root is IEnumerable && !(root is Area))
                {
                    continue;
                }
                for (Type t = root.GetType(); t != null && t != typeof(object); t = t.BaseType)
                {
                    foreach (FieldInfo f in t.GetFields(Inst | BindingFlags.DeclaredOnly))
                    {
                        if (f.FieldType.IsValueType)
                        {
                            continue;
                        }
                        object v;
                        try
                        {
                            v = f.GetValue(root);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        if (v != null && TryFix(v, map, groundCells))
                        {
                            fixedCount++;
                            ProjectZ.DebugLog($"resized {t.Name}.{f.Name} ({v.GetType().Name}) to the full map");
                        }
                        // Plain per-cell arrays indexed by cellIndices (z-major, e.g. MixedBiomeMapComponent.biomeGrid):
                        // the ground part is a prefix of the full array.
                        else if (v is Array arr && arr.Rank == 1 && arr.Length == groundCells && arr.GetType().GetElementType().IsValueType)
                        {
                            Array full = Array.CreateInstance(arr.GetType().GetElementType(), map.cellIndices.NumGridCells);
                            Array.Copy(arr, full, groundCells);
                            f.SetValue(root, full);
                            fixedCount++;
                            ProjectZ.DebugLog($"resized {t.Name}.{f.Name} ({arr.GetType().Name}[{groundCells}]) to the full map");
                        }
                    }
                }
            }
            return fixedCount;
        }

        // ByteGrid / IntGrid / CellGrid keep their size in private mapSizeX / mapSizeZ.
        private static bool IsGround(object grid, Map map, int groundCells)
        {
            Type t = grid.GetType();
            FieldInfo fx = t.GetField("mapSizeX", Inst);
            FieldInfo fz = t.GetField("mapSizeZ", Inst);
            if (fx == null || fz == null)
            {
                return false;
            }
            int x = (int)fx.GetValue(grid);
            int z = (int)fz.GetValue(grid);
            return x == map.Size.x && x > 0 && z == groundCells / x;
        }

        private static bool TryFix(object grid, Map map, int groundCells)
        {
            switch (grid)
            {
                case BoolGrid b when b.Width == map.Size.x && b.Height == groundCells / map.Size.x:
                {
                    List<IntVec3> on = new List<IntVec3>(b.ActiveCells);
                    b.ClearAndResizeTo(map);
                    foreach (IntVec3 c in on)
                    {
                        b[c] = true;
                    }
                    return true;
                }
                case ByteGrid g when IsGround(g, map, groundCells):
                {
                    byte[] old = new byte[groundCells];
                    for (int i = 0; i < groundCells; i++)
                    {
                        old[i] = g[i];
                    }
                    g.ClearAndResizeTo(map);
                    for (int i = 0; i < groundCells; i++)
                    {
                        g[i] = old[i];
                    }
                    return true;
                }
                case IntGrid g when IsGround(g, map, groundCells):
                {
                    int[] old = new int[groundCells];
                    for (int i = 0; i < groundCells; i++)
                    {
                        old[i] = g[i];
                    }
                    g.ClearAndResizeTo(map);
                    for (int i = 0; i < groundCells; i++)
                    {
                        g[i] = old[i];
                    }
                    return true;
                }
                case CellGrid g when IsGround(g, map, groundCells):
                {
                    IntVec3[] old = new IntVec3[groundCells];
                    for (int i = 0; i < groundCells; i++)
                    {
                        old[i] = g[i];
                    }
                    g.ClearAndResizeTo(map);
                    for (int i = 0; i < groundCells; i++)
                    {
                        g[i] = old[i];
                    }
                    return true;
                }
            }
            return false;
        }
    }
}
