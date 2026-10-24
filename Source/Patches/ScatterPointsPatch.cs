using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ProjectZ.Patches
{
    // Terrain scatter (pebbles, bones ...) picks its spots once per map with a blue-noise pass over every map cell: each
    // candidate is checked against every spot kept so far, which grows with the square of the map's size. A map with
    // floors has floors x the cells, so this alone took ~7 s at 13 floors (and ran again on every load and for every
    // temporary map, e.g. a gravship photo map). Same pass with a grid of buckets: same random draws, same order, same
    // spots, only the "any kept spot this close?" check looks at the neighbouring buckets instead of every spot.
    [HarmonyPatch(typeof(SectionLayer_TerrainScatter), "GenerateBlueNoisePoints")]
    public static class SectionLayer_TerrainScatter_BlueNoise_Patch
    {
        public static bool Prefix(List<IntVec3> candidatePoints, float minimumSpacing, ref List<IntVec3> __result)
        {
            IntVec3[] array = new IntVec3[candidatePoints.Count];
            int n = 0;
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach (IntVec3 c in candidatePoints.InRandomOrder())
            {
                array[n++] = c;
                minX = Mathf.Min(minX, c.x);
                minZ = Mathf.Min(minZ, c.z);
                maxX = Mathf.Max(maxX, c.x);
                maxZ = Mathf.Max(maxZ, c.z);
            }
            List<IntVec3> kept = new List<IntVec3>();
            if (n == 0)
            {
                __result = kept;
                return false;
            }
            // bucket side >= the spacing: every kept spot close enough is in the 3x3 buckets around the candidate
            int side = Mathf.Max(1, Mathf.CeilToInt(minimumSpacing));
            int bw = (maxX - minX) / side + 1;
            int bh = (maxZ - minZ) / side + 1;
            List<IntVec3>[] buckets = new List<IntVec3>[bw * bh];
            for (int i = n - 1; i >= 0; i--)
            {
                IntVec3 c = array[i];
                int bx = (c.x - minX) / side;
                int bz = (c.z - minZ) / side;
                bool free = true;
                for (int z = Mathf.Max(0, bz - 1); free && z <= Mathf.Min(bh - 1, bz + 1); z++)
                {
                    for (int x = Mathf.Max(0, bx - 1); x <= Mathf.Min(bw - 1, bx + 1); x++)
                    {
                        List<IntVec3> b = buckets[z * bw + x];
                        if (b == null)
                        {
                            continue;
                        }
                        for (int k = 0; k < b.Count; k++)
                        {
                            if (c.InHorDistOf(b[k], minimumSpacing))
                            {
                                free = false;
                                break;
                            }
                        }
                        if (!free)
                        {
                            break;
                        }
                    }
                }
                if (free)
                {
                    kept.Add(c);
                    (buckets[bz * bw + bx] ?? (buckets[bz * bw + bx] = new List<IntVec3>(2))).Add(c);
                }
            }
            __result = kept;
            return false;
        }
    }
}
