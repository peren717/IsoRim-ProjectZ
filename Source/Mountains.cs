using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // Tall mountains, phase 1 (isorim-pz-mountains-design.md §2.1, option A; world setting "Tall mountains", off by
    // default, new worlds only). On a map generated with it, the overhead-mountain (thick-roof) areas rise into the bands:
    //   - height H per ground column from the elevation captured at generation (NaturalElevation): 1 + one storey per
    //     Step of elevation over the thick-roof threshold (Step 0.10, user 2026-10-17), cut flat at the top level (the top level stays free, so
    //     every top is standable); every other ground natural rock (the edge lip with a thin roof or none, ore, lone
    //     outcrops) gets height 1: a walkable level-1 top (rock-top floors, RockTops); a tall thing under a rise gets
    //     solid rock over it (a small level-2 plateau);
    //   - per level a cleanup like vanilla's: features narrower than 3 cells and level groups under 20 cells go, and
    //     ledges narrower than Terrace cells are filled up (one sheer cliff instead of 1-2 cell steps);
    //   - levels 1..H-1 of a column hold the column's ground rock (the nearest non-ore natural rock) on its rough
    //     floor, level H only the rough floor (the mountain top); the cell under every band rock gets the thick rock
    //     roof; ground roofs stay vanilla's (thick inside, thin at the edge), a ground rock without one gets the floor
    //     roof (the support roof, as under a slab: Floors.CeilingFor);
    //   - fog as vanilla's flood from the open: open air is unfogged, tops reached from it (4-way) too, rock next to
    //     an unfogged non-rock cell (8-way) is a revealed face; the rest is fogged;
    //   - ore lumps per level: the ground's ore share of its rock, the ground's ore mix, vanilla lump sizes.
    // Everything else (mining, collapse, temperature, darkness, regions) is vanilla on the Things and roofs made here.
    public static class Mountains
    {
        // Elevation per storey (tests: -projectzmtnstep=X).
        public static float Step = 0.10f;
        public const int MinGroup = 20;
        public const int Terrace = 3;

        // last fill, for the test
        public static int LastRock, LastTops, LastOre, LastFogged, LastMaxH, LastLips, LastLipRoofs, LastTallRaised;
        public static double LastSeconds;
        public static byte[] LastHeights;
        public static string LastTimes;

        // The map has tall mountains (generated with them).
        public static bool On(Map map) => ProjectZ.Annex(map)?.tallMountains ?? false;

        // ---------------------------------------------------------------- heights

        // H per ground cell (index z * sx + x), 0 = no rise; cap = the highest level a top may have.
        public static byte[] Heights(Map map, AnnexMapComponent a, int cap)
        {
            int sx = map.Size.x, gz = a.groundZ, n = sx * gz;
            byte[] h = new byte[n];
            if (cap < 1 || a.elevation == null || a.elevation.Length != n)
            {
                return h;
            }
            for (int i = 0; i < n; i++)
            {
                if (map.roofGrid.RoofAt(new IntVec3(i % sx, 0, i / sx))?.isThickRoof ?? false)
                {
                    float over = Mathf.Max(0f, NaturalElevation.Dequantise(a.elevation[i]) - NaturalElevation.ThickRoof + 0.0001f);
                    h[i] = (byte)Mathf.Clamp(1 + Mathf.FloorToInt(over / Step), 1, cap);
                }
            }
            bool[] up = new bool[n], bas = new bool[n];
            for (int L = 2; L <= cap; L++)
            {
                for (int i = 0; i < n; i++)
                {
                    up[i] = h[i] >= L;
                    bas[i] = h[i] >= L - 1;
                }
                // thin parts and small groups go, narrow ledges fill up, then notches and small pits close
                Open(up, sx, gz);
                RemoveSmall(up, sx, gz, MinGroup);
                FillLedges(up, bas, sx, gz, Terrace);
                Close(up, bas, sx, gz);
                FillPockets(up, bas, sx, gz, MinGroup);
                for (int i = 0; i < n; i++)
                {
                    if (up[i])
                    {
                        if (h[i] < L)
                        {
                            h[i] = (byte)L;
                        }
                    }
                    else if (h[i] >= L)
                    {
                        h[i] = (byte)(L - 1);
                    }
                }
            }
            RockTops(map, h, sx, gz);
            return h;
        }

        // Rock-top floors (isorim-pz-rocktop-floors-plan.md §3.2), after the cleanup (its level-2 ledge fill builds on
        // level 1; lips raised before it would be pulled up into level 2):
        //   - every ground natural rock (ore too, a lone outcrop too: user Q1) has at least a level-1 top: the edge lip
        //     with a thin roof or none is walkable on top, like the rest of the mountain;
        //   - a tall thing under a rise (ancient dangers; HeightRule.TallThingAt) gets solid rock on level 1 over it, raised
        //     as a small plateau at least 3 cells wide (user Q2: the 3x3 round it, where the mountain rises): H 2, a top on
        //     level 2. In a 2-storey world (cap 1) that H is cap + 1: level 1 is solid rock with its thick roof and no top
        //     (decided 2026-10-18).
        private static void RockTops(Map map, byte[] h, int sx, int gz)
        {
            int n = sx * gz;
            LastLips = LastTallRaised = 0;
            for (int i = 0; i < n; i++)
            {
                if (h[i] == 0 && (new IntVec3(i % sx, 0, i / sx).GetEdifice(map)?.def.building?.isNaturalRock ?? false))
                {
                    h[i] = 1;
                    LastLips++;
                }
            }
            List<int> tall = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (h[i] >= 1 && HeightRule.TallThingAt(new IntVec3(i % sx, 0, i / sx), map) != null)
                {
                    tall.Add(i);
                }
            }
            foreach (int i in tall)
            {
                int x = i % sx, z = i / sx;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, nz = z + dz;
                        if (nx < 0 || nz < 0 || nx >= sx || nz >= gz)
                        {
                            continue;
                        }
                        int j = nz * sx + nx;
                        if (h[j] == 1)
                        {
                            h[j] = 2;
                            LastTallRaised++;
                        }
                    }
                }
            }
        }

        // 3x3 opening (erode, then dilate inside the set): parts narrower than 3 cells go. Off the map counts as set.
        private static void Open(bool[] s, int sx, int gz)
        {
            int n = sx * gz;
            bool[] e = new bool[n];
            for (int z = 0; z < gz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    bool all = s[z * sx + x];
                    for (int dz = -1; dz <= 1 && all; dz++)
                    {
                        for (int dx = -1; dx <= 1 && all; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            if (nx >= 0 && nz >= 0 && nx < sx && nz < gz && !s[nz * sx + nx])
                            {
                                all = false;
                            }
                        }
                    }
                    e[z * sx + x] = all;
                }
            }
            for (int z = 0; z < gz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    int i = z * sx + x;
                    if (!s[i])
                    {
                        continue;
                    }
                    bool any = false;
                    for (int dz = -1; dz <= 1 && !any; dz++)
                    {
                        for (int dx = -1; dx <= 1 && !any; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            any = nx >= 0 && nz >= 0 && nx < sx && nz < gz && e[nz * sx + nx];
                        }
                    }
                    s[i] = any;
                }
            }
        }

        // 3x3 closing inside bas (dilate into bas, then erode): notches and holes narrower than 3 cells fill up.
        private static void Close(bool[] s, bool[] bas, int sx, int gz)
        {
            int n = sx * gz;
            bool[] d = new bool[n];
            for (int z = 0; z < gz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    int i = z * sx + x;
                    bool any = s[i];
                    for (int dz = -1; dz <= 1 && !any; dz++)
                    {
                        for (int dx = -1; dx <= 1 && !any; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            any = nx >= 0 && nz >= 0 && nx < sx && nz < gz && s[nz * sx + nx];
                        }
                    }
                    d[i] = any && bas[i];
                }
            }
            for (int z = 0; z < gz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    int i = z * sx + x;
                    if (s[i] || !d[i])
                    {
                        continue;
                    }
                    bool all = true;
                    for (int dz = -1; dz <= 1 && all; dz++)
                    {
                        for (int dx = -1; dx <= 1 && all; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            if (nx >= 0 && nz >= 0 && nx < sx && nz < gz && !d[nz * sx + nx])
                            {
                                all = false;
                            }
                        }
                    }
                    s[i] = all;
                }
            }
        }

        // Pockets of the level below enclosed by the level (bas - up groups that touch no cell outside bas) under min
        // cells fill up: a small pit in a plateau is a vanilla-like "small group" of the lower level.
        private static void FillPockets(bool[] up, bool[] bas, int sx, int gz, int min)
        {
            int n = sx * gz;
            bool[] seen = new bool[n];
            List<int> group = new List<int>();
            Queue<int> q = new Queue<int>();
            for (int i0 = 0; i0 < n; i0++)
            {
                if (!bas[i0] || up[i0] || seen[i0])
                {
                    continue;
                }
                group.Clear();
                bool open = false;
                seen[i0] = true;
                q.Enqueue(i0);
                while (q.Count > 0)
                {
                    int i = q.Dequeue();
                    group.Add(i);
                    int x = i % sx, z = i / sx;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), nz = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        if (nx < 0 || nz < 0 || nx >= sx || nz >= gz)
                        {
                            continue;
                        }
                        int j = nz * sx + nx;
                        if (!bas[j])
                        {
                            open = true;
                        }
                        else if (!up[j] && !seen[j])
                        {
                            seen[j] = true;
                            q.Enqueue(j);
                        }
                    }
                }
                if (!open && group.Count < min)
                {
                    foreach (int i in group)
                    {
                        up[i] = true;
                    }
                }
            }
        }

        // 8-connected groups under min cells go.
        private static void RemoveSmall(bool[] s, int sx, int gz, int min)
        {
            int n = sx * gz;
            bool[] seen = new bool[n];
            List<int> group = new List<int>();
            Queue<int> q = new Queue<int>();
            for (int i0 = 0; i0 < n; i0++)
            {
                if (!s[i0] || seen[i0])
                {
                    continue;
                }
                group.Clear();
                seen[i0] = true;
                q.Enqueue(i0);
                while (q.Count > 0)
                {
                    int i = q.Dequeue();
                    group.Add(i);
                    int x = i % sx, z = i / sx;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            if (nx < 0 || nz < 0 || nx >= sx || nz >= gz)
                            {
                                continue;
                            }
                            int j = nz * sx + nx;
                            if (s[j] && !seen[j])
                            {
                                seen[j] = true;
                                q.Enqueue(j);
                            }
                        }
                    }
                }
                if (group.Count < min)
                {
                    foreach (int i in group)
                    {
                        s[i] = false;
                    }
                }
            }
        }

        // Ledges of the level below (bas - up) narrower than width cells are raised into up (a sheer cliff there).
        private static void FillLedges(bool[] up, bool[] bas, int sx, int gz, int width)
        {
            int n = sx * gz;
            int[] dOut = Dist(i => !bas[i], i => bas[i], sx, gz);
            int[] dIn = Dist(i => up[i], i => bas[i] && !up[i], sx, gz);
            for (int i = 0; i < n; i++)
            {
                if (bas[i] && !up[i] && dIn[i] < int.MaxValue && dOut[i] < int.MaxValue && dOut[i] + dIn[i] - 1 < width)
                {
                    up[i] = true;
                }
            }
        }

        // 8-way steps from the nearest source cell over pass cells (sources 0; unreached int.MaxValue).
        private static int[] Dist(System.Func<int, bool> source, System.Func<int, bool> pass, int sx, int gz)
        {
            int n = sx * gz;
            int[] d = new int[n];
            Queue<int> q = new Queue<int>();
            for (int i = 0; i < n; i++)
            {
                if (source(i))
                {
                    d[i] = 0;
                    q.Enqueue(i);
                }
                else
                {
                    d[i] = int.MaxValue;
                }
            }
            while (q.Count > 0)
            {
                int i = q.Dequeue();
                int x = i % sx, z = i / sx;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, nz = z + dz;
                        if (nx < 0 || nz < 0 || nx >= sx || nz >= gz)
                        {
                            continue;
                        }
                        int j = nz * sx + nx;
                        if (d[j] == int.MaxValue && pass(j))
                        {
                            d[j] = d[i] + 1;
                            q.Enqueue(j);
                        }
                    }
                }
            }
            return d;
        }

        // ---------------------------------------------------------------- fill (map generation)

        private static bool IsPlainRock(ThingDef d) => d?.building != null && d.building.isNaturalRock && !d.building.isResourceRock && d.building.naturalTerrain != null;

        // Called from the FinalizeInit prefix (full size restored, annex filled with open air, elevation handed over).
        internal static void Generate(Map map, AnnexMapComponent a)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            int sx = map.Size.x, gz = a.groundZ, n = sx * gz;
            int cap = a.MaxLevel;
            byte[] h = Heights(map, a, cap);
            LastHeights = h;
            int maxH = 0;
            for (int i = 0; i < n; i++)
            {
                maxH = Mathf.Max(maxH, h[i]);
            }
            a.tallMountains = true;
            LastRock = LastTops = LastOre = LastFogged = LastLipRoofs = 0;
            LastMaxH = Mathf.Min(maxH, cap);
            if (maxH == 0)
            {
                ProjectZ.DebugLog($"map {map}: tall mountains, nothing to rise (no overhead mountain)");
                return;
            }
            // rock def per column: the nearest plain natural rock on the ground (4-way)
            ThingDef[] rock = new ThingDef[n];
            Queue<int> q = new Queue<int>();
            for (int i = 0; i < n; i++)
            {
                Building b = new IntVec3(i % sx, 0, i / sx).GetEdifice(map);
                if (b != null && IsPlainRock(b.def))
                {
                    rock[i] = b.def;
                    q.Enqueue(i);
                }
            }
            ThingDef fallback = Find.World?.NaturalRockTypesIn(map.Tile)?.FirstOrDefault(IsPlainRock) ?? DefDatabase<ThingDef>.AllDefsListForReading.First(IsPlainRock);
            if (q.Count == 0)
            {
                for (int i = 0; i < n; i++)
                {
                    rock[i] = fallback;
                }
            }
            while (q.Count > 0)
            {
                int i = q.Dequeue();
                int x = i % sx, z = i / sx;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), nz = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (nx < 0 || nz < 0 || nx >= sx || nz >= gz)
                    {
                        continue;
                    }
                    int j = nz * sx + nx;
                    if (rock[j] == null)
                    {
                        rock[j] = rock[i];
                        q.Enqueue(j);
                    }
                }
            }
            // fog per level (index over ground cells, per level 1..cap)
            bool[][] fog = new bool[cap + 1][];
            for (int L = 1; L <= cap; L++)
            {
                fog[L] = BandFog(h, L, sx, gz);
            }
            Patches.AnnexGen.FillRule rule = (c, m) =>
            {
                int L = Levels.LevelOf(c, m);
                if (L == Levels.None || L < 1 || L > cap)
                {
                    return Patches.AnnexGen.OpenAir(c, m);
                }
                IntVec3 g = Levels.Ground(c, m);
                int i = g.z * sx + g.x;
                if (h[i] < L)
                {
                    return Patches.AnnexGen.OpenAir(c, m);
                }
                return new Patches.AnnexGen.AnnexCell { terrain = rock[i].building.naturalTerrain, fogged = fog[L][i] };
            };
            double tPrep = sw.Elapsed.TotalSeconds;
            Patches.AnnexGen.FillAnnex(map, gz, withPathCosts: false, rule);
            double tFill = sw.Elapsed.TotalSeconds;
            double tSpawn = 0, tRoof = 0;
            System.Diagnostics.Stopwatch part = new System.Diagnostics.Stopwatch();
            // ore lumps planned first (spawned in place of the rock: no rock is spawned and then wiped)
            int seed = Gen.HashCombineInt(Find.World?.info?.Seed ?? 0, map.Tile.tileId);
            ThingDef[][] ore;
            Rand.PushState(seed);
            try
            {
                ore = PlanOre(map, a, h, cap);
            }
            finally
            {
                Rand.PopState();
            }
            double tOre = sw.Elapsed.TotalSeconds - tFill;
            // rock Things and the thick roof under every rock floor
            RoofDef thick = RoofDefOf.RoofRockThick;
            for (int L = 1; L <= cap; L++)
            {
                for (int i = 0; i < n; i++)
                {
                    if (h[i] < L)
                    {
                        continue;
                    }
                    IntVec3 c = Levels.Cell(new IntVec3(i % sx, 0, i / sx), L, map);
                    if (fog[L][i])
                    {
                        LastFogged++;
                    }
                    if (h[i] == L)
                    {
                        LastTops++;
                        continue;
                    }
                    part.Restart();
                    ThingDef def = ore[L]?[i] ?? rock[i];
                    if (def != rock[i])
                    {
                        LastOre++;
                    }
                    GenSpawn.Spawn(ThingMaker.MakeThing(def), c, map, WipeMode.Vanish);
                    tSpawn += part.Elapsed.TotalSeconds;
                    part.Restart();
                    map.roofGrid.SetRoof(c, thick);
                    tRoof += part.Elapsed.TotalSeconds;
                    LastRock++;
                }
            }
            // the ground cell under a new top without a natural rock roof gets the floor roof (the support roof, as under a
            // slab); vanilla's thin and thick rock roofs stay
            for (int i = 0; i < n; i++)
            {
                IntVec3 g = new IntVec3(i % sx, 0, i / sx);
                if (h[i] >= 1 && !(map.roofGrid.RoofAt(g)?.isNatural ?? false))
                {
                    map.roofGrid.SetRoof(g, PZDefOf.PZ_FloorRoof);
                    LastLipRoofs++;
                }
            }
            LastSeconds = sw.Elapsed.TotalSeconds;
            LastTimes = $"heights+fog {tPrep:0.00}, terrain {tFill - tPrep:0.00}, ore plan {tOre:0.00}, rock (spawn {tSpawn:0.00}, roof {tRoof:0.00})";
            ProjectZ.DebugLog($"map {map}: tall mountains up to level {maxH} (cap {cap}, step {Step}): {LastRock} rock, {LastTops} tops, {LastOre} ore cells, {LastFogged} fogged band cells, lips {LastLips} ({LastLipRoofs} given the floor roof), {LastTallRaised} cells raised over tall things, {LastSeconds:0.00} s");
        }

        // Fog of level L's band (index over ground cells): vanilla's flood from the open (FloodFillerFog: 4-way through
        // non-blocking cells, then 8-way onto the blockers next to them).
        private static bool[] BandFog(byte[] h, int L, int sx, int gz)
        {
            int n = sx * gz;
            bool[] vis = new bool[n];
            Queue<int> q = new Queue<int>();
            for (int i = 0; i < n; i++)
            {
                if (h[i] < L)
                {
                    vis[i] = true;   // open air (the sky)
                    q.Enqueue(i);
                }
            }
            while (q.Count > 0)
            {
                int i = q.Dequeue();
                int x = i % sx, z = i / sx;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), nz = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (nx < 0 || nz < 0 || nx >= sx || nz >= gz)
                    {
                        continue;
                    }
                    int j = nz * sx + nx;
                    if (!vis[j] && h[j] == L)
                    {
                        vis[j] = true;   // a top reached from the open
                        q.Enqueue(j);
                    }
                }
            }
            bool[] fogged = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (h[i] < L)
                {
                    continue;
                }
                if (h[i] == L)
                {
                    fogged[i] = !vis[i];
                    continue;
                }
                bool face = false;
                int x = i % sx, z = i / sx;
                for (int dz = -1; dz <= 1 && !face; dz++)
                {
                    for (int dx = -1; dx <= 1 && !face; dx++)
                    {
                        int nx = x + dx, nz = z + dz;
                        if ((dx != 0 || dz != 0) && nx >= 0 && nz >= 0 && nx < sx && nz < gz)
                        {
                            int j = nz * sx + nx;
                            face = h[j] <= L && vis[j];
                        }
                    }
                }
                fogged[i] = !face;
            }
            return fogged;
        }

        // Ore lumps planned on the band rock, per level (index over ground cells; null: none on that level): the ground's
        // share of ore in its natural rock, the ground's ore mix (cells per def), vanilla lump sizes, at least 5 cells between
        // lump centres (as vanilla).
        private static ThingDef[][] PlanOre(Map map, AnnexMapComponent a, byte[] h, int cap)
        {
            int sx = map.Size.x, gz = a.groundZ, n = sx * gz;
            ThingDef[][] ore = new ThingDef[cap + 1][];
            int groundRock = 0, groundOre = 0;
            Dictionary<ThingDef, int> mix = new Dictionary<ThingDef, int>();
            for (int i = 0; i < n; i++)
            {
                Building b = new IntVec3(i % sx, 0, i / sx).GetEdifice(map);
                if (b == null || b.def.building == null || !b.def.building.isNaturalRock)
                {
                    continue;
                }
                groundRock++;
                if (b.def.building.isResourceRock)
                {
                    groundOre++;
                    mix[b.def] = mix.TryGetValue(b.def, out int k) ? k + 1 : 1;
                }
            }
            if (groundRock == 0 || groundOre == 0)
            {
                return ore;
            }
            float share = groundOre / (float)groundRock;
            List<ThingDef> defs = mix.Keys.ToList();
            List<int> cand = new List<int>();
            List<IntVec3> centres = new List<IntVec3>();
            for (int L = 1; L < cap; L++)
            {
                cand.Clear();
                centres.Clear();
                for (int i = 0; i < n; i++)
                {
                    if (h[i] > L)
                    {
                        cand.Add(i);
                    }
                }
                if (cand.Count == 0)
                {
                    continue;
                }
                ThingDef[] lv = ore[L] = new ThingDef[n];
                int level = L;
                int target = Mathf.RoundToInt(share * cand.Count);
                int placed = 0;
                for (int tries = 0; placed < target && tries < 200 + target; tries++)
                {
                    int ci = cand.RandomElement();
                    IntVec3 g = new IntVec3(ci % sx, 0, ci / sx);
                    if (lv[ci] != null || centres.Any(o => o.DistanceToSquared(g) < 25))
                    {
                        continue;
                    }
                    ThingDef def = defs.RandomElementByWeight(d => mix[d] / Mathf.Max(1f, d.building.mineableScatterLumpSizeRange.Average));
                    int size = def.building.mineableScatterLumpSizeRange.RandomInRange;
                    // (on the ground rows: the same shape as vanilla's, kept to this level's rock)
                    foreach (IntVec3 x in GridShapeMaker.IrregularLump(g, map, size, v => v.z >= 0 && v.z < gz && h[v.z * sx + v.x] > level && lv[v.z * sx + v.x] == null))
                    {
                        lv[x.z * sx + x.x] = def;
                        placed++;
                    }
                    centres.Add(g);
                }
            }
            return ore;
        }
    }
}
