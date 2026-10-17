using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // Mountains foundation F5 (isorim-pz-mountains-design.md §3): vanilla's elevation, kept past map generation.
    //
    // The game's elevation grid (GenStep_ElevationFertility) lives only while a map is generated (MapGenerator.Elevation,
    // cleared at the end). On maps with floors it is copied when the rocks are placed (GenStep_RocksFromGrid, the last
    // genStep that reads it), one byte per ground cell, into the annex component and saved with the map. Nothing uses
    // it yet: the mountain fill (phase 1) turns it into rock heights. Maps made before this have no copy; At() then
    // gives a stand-in from the roofs (distance to the mountain's edge plus seeded noise, user decision 2026-10-16).
    public static class NaturalElevation
    {
        // byte = elevation * Scale, rounded and clamped to 0..255: elevations 0..1.59 in steps of 0.00625 (the thick-roof
        // threshold 0.798 is byte 128; one storey of ~0.05 is 8 steps). Below 0 (water, low ground) reads as 0.
        public const float Scale = 160f;

        // vanilla's thresholds (GenStep_RocksFromGrid: 0.7 rock, 0.7 * 1.04 thin roof, 0.7 * 1.14 thick roof)
        public const float Rock = 0.7f;
        public const float ThinRoof = 0.7f * 1.04f;
        public const float ThickRoof = 0.7f * 1.14f;

        public static byte Quantise(float e) => (byte)Mathf.Clamp(Mathf.RoundToInt(e * Scale), 0, 255);

        public static float Dequantise(byte b) => b / Scale;

        // The map's elevation was captured at generation (else At() is the stand-in).
        public static bool Captured(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            return a != null && a.elevation != null && a.elevation.Length == map.Size.x * a.groundZ;
        }

        // Elevation of ground cell g (quantised): the captured value, or the stand-in for maps made before F5.
        // 0 on maps without floors and outside the ground.
        public static float At(IntVec3 g, Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || !g.IsValid || g.x < 0 || g.z < 0 || g.x >= map.Size.x || g.z >= a.groundZ)
            {
                return 0f;
            }
            int i = g.z * map.Size.x + g.x;
            if (Captured(map))
            {
                return Dequantise(a.elevation[i]);
            }
            return Dequantise(StandIn(map, a)[i]);
        }

        // ---------------------------------------------------------------- capture (generation)

        // Into the pending generation record (AnnexGen.Pending); the FinalizeInit prefix hands it to the component.
        internal static void Capture(Map map, MapGenFloatGrid elev, Patches.AnnexGen.Pending p)
        {
            if (elev == null)
            {
                return;
            }
            int sx = p.groundSize.x, gz = p.groundSize.z;
            byte[] arr = new byte[sx * gz];
            for (int z = 0; z < gz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    arr[z * sx + x] = Quantise(elev[new IntVec3(x, 0, z)]);
                }
            }
            p.elevation = arr;
            ProjectZ.DebugLog($"map {map}: elevation captured ({arr.Length} cells)");
        }

        // ---------------------------------------------------------------- stand-in (maps without a capture)

        private static readonly ConditionalWeakTable<Map, byte[]> standIns = new ConditionalWeakTable<Map, byte[]>();

        // Rock under a thick roof: the thick-roof threshold plus 0.012 per cell from the nearest cell without a thick
        // roof (4-neighbour steps over the ground) plus seeded noise of +-0.02; rock under a thin roof: between the two
        // roof thresholds; other natural rock: just over the rock threshold; anything else 0. Deterministic per map,
        // not saved (built once per map when first asked, from the roofs and rocks as they are then).
        private static byte[] StandIn(Map map, AnnexMapComponent a)
        {
            if (standIns.TryGetValue(map, out byte[] got))
            {
                return got;
            }
            int sx = map.Size.x, gz = a.groundZ, n = sx * gz;
            int[] dist = new int[n];
            Queue<int> open = new Queue<int>();
            for (int i = 0; i < n; i++)
            {
                IntVec3 c = new IntVec3(i % sx, 0, i / sx);
                bool thick = map.roofGrid.RoofAt(c)?.isThickRoof ?? false;
                dist[i] = thick ? -1 : 0;
                if (!thick)
                {
                    open.Enqueue(i);
                }
            }
            while (open.Count > 0)
            {
                int i = open.Dequeue();
                int x = i % sx, z = i / sx;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), nz = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (nx < 0 || nz < 0 || nx >= sx || nz >= gz)
                    {
                        continue;
                    }
                    int j = nz * sx + nx;
                    if (dist[j] < 0)
                    {
                        dist[j] = dist[i] + 1;
                        open.Enqueue(j);
                    }
                }
            }
            int worldSeed = Find.World?.info?.Seed ?? 0;
            float seed = Mathf.Abs(worldSeed % 1000) + Mathf.Abs(map.Tile.tileId % 1000) * 0.37f;
            byte[] arr = new byte[n];
            for (int i = 0; i < n; i++)
            {
                IntVec3 c = new IntVec3(i % sx, 0, i / sx);
                RoofDef roof = map.roofGrid.RoofAt(c);
                float e;
                if (dist[i] > 0 || dist[i] < 0)
                {
                    // (dist < 0: a thick-roofed pocket with no edge in reach on the ground; treat as deep)
                    int d = dist[i] < 0 ? 64 : dist[i];
                    float noise = (Mathf.PerlinNoise(c.x * 0.07f + seed, c.z * 0.07f + seed * 0.5f) - 0.5f) * 0.04f;
                    e = ThickRoof + 0.012f * d + noise;
                }
                else if (roof != null && roof.isNatural)
                {
                    e = (ThinRoof + ThickRoof) * 0.5f;
                }
                else
                {
                    Building b = c.GetEdifice(map);
                    e = b != null && b.def.building != null && b.def.building.isNaturalRock ? (Rock + ThinRoof) * 0.5f : 0f;
                }
                arr[i] = Quantise(e);
            }
            standIns.Add(map, arr);
            return arr;
        }
    }

    // F5: the elevation grid is still alive when the rocks are placed (it and the cave grid are cleared only at the end of
    // generation). Only maps generated with floors (AnnexGen.pending) keep a copy. It waits in the pending record until
    // the FinalizeInit prefix: GridFixer resizes every ground-sized array it finds on the map's components.
    [HarmonyPatch(typeof(GenStep_RocksFromGrid), nameof(GenStep_RocksFromGrid.Generate))]
    public static class RocksFromGrid_CaptureElevation_Patch
    {
        public static void Postfix(Map map)
        {
            Patches.AnnexGen.Pending p = Patches.AnnexGen.pending;
            if (p == null || !ReferenceEquals(p.map, map))
            {
                return;
            }
            NaturalElevation.Capture(map, MapGenerator.Elevation, p);
        }
    }
}
