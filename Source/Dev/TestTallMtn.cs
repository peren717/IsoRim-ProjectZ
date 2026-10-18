using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode tallmtn (tall mountains, phase 1; isorim-pz-mountains-design.md "Phase 1 plan"). A fixed world seed (as mtn), a
    // Mountainous tile (-projectzhilliness=Impassable for the other), -projectztallmountains on, -projectzfloors=N,
    // -projectzmtnstep=X to try a step. This first part: the fill's numbers (heights, rock, tops, ore, fog), checks of
    // the fill against its rules, performance (as mtn's PERF lines), and screenshots of the generated mountains at
    // play zoom (overview, the highest area viewed level by level, a sheer cliff, a terrace, the edge lip).
    public static partial class TestRunner
    {
        private struct TmShot
        {
            public string name;
            public int floor;
            public IntVec3 g;
            public float size;
        }

        private static readonly List<TmShot> tmShots = new List<TmShot>();
        private static int tmShot;
        private static int tmNoFogAt = -1;

        private static bool StepTallMtn(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (stage.StartsWith("tm_f_"))
            {
                return StepTallMtnFlows(map);
            }
            switch (stage)
            {
                case "tm_setup":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    DebugSettings.enableRandomMentalStates = false;
                    Info($"tallmtn: map {map.Size}, tile {map.Tile} {map.TileInfo?.PrimaryBiome?.defName} {map.TileInfo?.hilliness}, floors {a?.floors ?? 1}, ground rows {a?.groundZ ?? map.Size.z}, step {Mountains.Step}");
                    if (a == null)
                    {
                        Result("tallmtn: map has floors", false, "no annex");
                        Finish();
                        return true;
                    }
                    Info($"PERF fill: {Mountains.LastSeconds:0.00} s; rock {Mountains.LastRock}, tops {Mountains.LastTops}, ore cells {Mountains.LastOre}, fogged band cells {Mountains.LastFogged}, highest top level {Mountains.LastMaxH}");
                    Info("PERF fill parts: " + Mountains.LastTimes);
                    mtPerf.Add($"fill s {Mountains.LastSeconds:0.00}, rock {Mountains.LastRock}, ore {Mountains.LastOre}, tops {Mountains.LastTops}");
                    TmChecks(map, a);
                    FogLidCheck("after generation");
                    MtPerfMap(map, "after generation");
                    MtTicks(map);
                    mtFrames = 0;
                    mtFrameSum = 0f;
                    stage = "tm_frames";
                    return true;
                }
                case "tm_frames":
                    if (mtFrames > 0)
                    {
                        mtFrameSum += Time.unscaledDeltaTime;
                    }
                    if (++mtFrames <= 180)
                    {
                        return true;
                    }
                    mtPerf.Add($"frame ms {mtFrameSum / 180f * 1000f:0.00}");
                    Info($"PERF frame ms (paused, 180 frames, window hidden): {mtFrameSum / 180f * 1000f:0.00}");
                    stage = GenCommandLine.CommandLineArgPassed("projectztmnoflows") ? "tm_shots" : "tm_f_setup";
                    return true;
                case "tm_shots":
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    TmPlanShots(map, a);
                    tmShot = 0;
                    // shots at noon (the quick test starts at dawn)
                    int hour = GenLocalDate.HourOfDay(map);
                    Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + ((12 - hour + 24) % 24) * GenDate.TicksPerHour);
                    Find.TickManager.DoSingleTick();
                    map.skyManager.ForceSetCurSkyGlow(1f);
                    stage = "tm_shot";
                    return true;
                case "tm_shot":
                    if (tmShot == tmNoFogAt)
                    {
                        // diagnosis only: the same spots with every band cell unfogged (what the fog hides)
                        foreach (IntVec3 c in map.AllCells)
                        {
                            if (ProjectZ.FloorOf(c, map) >= 1 && map.fogGrid.IsFogged(c))
                            {
                                map.fogGrid.Unfog(c);
                            }
                        }
                    }
                    if (tmShot >= tmShots.Count)
                    {
                        FloorView.Set(0);
                        stage = "tm_lv0";
                        return true;
                    }
                    ShotAt(map, tmShots[tmShot].floor, tmShots[tmShot].g, tmShots[tmShot].size);
                    Wait(25, "tm_take");
                    return true;
                // the floor HUD offers the levels with mountain tops plus one (ShownLevels)
                case "tm_lv0":
                    LvWait(0.6f, "tm_lv");
                    return true;
                case "tm_lv":
                {
                    int top = 0;
                    for (int x = 0; x < map.Size.x; x++)
                    {
                        for (int z = 0; z < ProjectZ.GroundZ(map); z++)
                        {
                            top = Mathf.Max(top, Column.NaturalTop(new IntVec3(x, 0, z), map));
                        }
                    }
                    ShownExpected(map, out int elo, out int ehi);
                    Info($"tall mountains: highest top level {top} of {Levels.Max(map)}");
                    LvCheck(map, $"floor HUD on a tall-mountain map: the levels in use (tops up to level {top}) plus one", elo, ehi);
                    Result("floor HUD shows every level with mountain tops and one more", ehi >= Mathf.Min(Levels.Max(map), top + 1), $"top {top}, shown up to {ehi}");
                    // cost: the cache built from nothing, a refresh (the things pass), a refresh with one empty level to rescan
                    AnnexMapComponent pa = ProjectZ.Annex(map);
                    pa.shownLevels = null;
                    Stopwatch psw = Stopwatch.StartNew();
                    ShownLevels.ForceRefresh(map);
                    double buildMs = psw.Elapsed.TotalMilliseconds;
                    psw.Restart();
                    for (int i = 0; i < 10; i++)
                    {
                        ShownLevels.ForceRefresh(map);
                    }
                    double refreshMs = psw.Elapsed.TotalMilliseconds / 10;
                    ShownLevels.FloorChanged(Levels.Cell(new IntVec3(1, 0, 1), Levels.Max(map), map), map, false);
                    psw.Restart();
                    ShownLevels.ForceRefresh(map);
                    double rescanMs = psw.Elapsed.TotalMilliseconds;
                    Info($"PERF shown levels: build {buildMs:0.000} ms, refresh {refreshMs:0.000} ms (pawns {map.mapPawns.AllPawnsSpawnedCount}, items {map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Count}, blueprints+frames {map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count + map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count}), refresh with an empty level rescanned {rescanMs:0.000} ms; map {map.Size.x}x{ProjectZ.GroundZ(map)} x {Levels.Count(map)} levels");
                    Capture("tm_hud");
                    Wait(5, "tm_save");
                    return true;
                }
                case "tm_take":
                    Capture(tmShots[tmShot].name);
                    tmShot++;
                    Wait(5, "tm_shot");
                    return true;
                case "tm_save":
                {
                    mtMine = map.listerThings.AllThings.Count(t => t.def.mineable);
                    Stopwatch sw = Stopwatch.StartNew();
                    GameDataSaveLoader.SaveGame("pz_tallmtn");
                    mtSaveS = sw.Elapsed.TotalSeconds;
                    string path = GenFilePaths.FilePathForSavedGame("pz_tallmtn");
                    mtSaveBytes = File.Exists(path) ? new FileInfo(path).Length : -1;
                    Info($"PERF save s {mtSaveS:0.00}, save KB {mtSaveBytes / 1024}");
                    mtPerf.Add($"save s {mtSaveS:0.00}, save KB {mtSaveBytes / 1024}");
                    stage = "tm_loaded";
                    pendingLoad = "pz_tallmtn";
                    return true;
                }
                case "tm_loaded":
                {
                    Info($"PERF load s {LastLoadSeconds:0.00}");
                    mtPerf.Add($"load s {LastLoadSeconds:0.00}");
                    MtPerfMap(map, "after load");
                    int mineAfter = map.listerThings.AllThings.Count(t => t.def.mineable);
                    Result("save + load: same mineables, tall mountains flag kept", mineAfter == mtMine && Mountains.On(map), $"mineables {mtMine} -> {mineAfter}, flag {Mountains.On(map)}");
                    FogLidCheck("after load");
                    Info("PERF SUMMARY | " + string.Join(" | ", mtPerf));
                    Finish();
                    return true;
                }
            }
            return false;
        }

        // IsoRim's fog lid follows the world (user decision 2026-10-17): locked off with tall mountains, not locked
        // without; the player's own option is never written (the harness checks the settings file on disk too).
        private static void FogLidCheck(string when)
        {
            if (!IsoRimBridge.CanLockFogLid)
            {
                Info($"fog lid lock ({when}): skipped, IsoRim API {IsoRimBridge.ApiVersion}");
                return;
            }
            bool tall = WorldFloors.TallMountainsThisGame;
            string by = IsoRimBridge.FogLidLockedBy;
            object own = IsoRimBridge.SetOption("fogLid", true);
            IsoRimBridge.SetOption("fogLid", own);   // read back unchanged
            // (user 2026-10-18: no lock with tall mountains either; the lid is the player's option in every game)
            bool ok = by == null && IsoRimBridge.FogLidOn == (own is bool b && b);
            Result($"fog lid not locked, the player's option (tall mountains {tall}) ({when})", ok,
                $"tall mountains {tall}, locked by '{by ?? "null"}', lid drawn {IsoRimBridge.FogLidOn}, player's option {own}");
        }

        // Natural top level per ground cell.
        private static int[] TmHeights(Map map, AnnexMapComponent a)
        {
            int sx = map.Size.x, gz = a.groundZ;
            int[] h = new int[sx * gz];
            for (int z = 0; z < gz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    h[z * sx + x] = Column.NaturalTop(new IntVec3(x, 0, z), map);
                }
            }
            return h;
        }

        private static void TmChecks(Map map, AnnexMapComponent a)
        {
            int sx = map.Size.x, gz = a.groundZ, cap = a.MaxLevel;
            int[] h = TmHeights(map, a);
            int[] hist = new int[cap + 1];
            int badRoof = 0, badRock = 0, badDef = 0, badThick = 0, narrow = 0, topsFogged = 0, rockFogged = 0, rockOpen = 0;
            string first = null;
            for (int z = 0; z < gz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    IntVec3 g = new IntVec3(x, 0, z);
                    int H = h[z * sx + x];
                    hist[H]++;
                    bool thick = map.roofGrid.RoofAt(g)?.isThickRoof ?? false;
                    if (H > 0 != thick)
                    {
                        badThick++;
                        first = first ?? $"thick {g} H{H}";
                    }
                    Building gr = g.GetEdifice(map);
                    for (int L = 1; L <= cap; L++)
                    {
                        IntVec3 c = Levels.Cell(g, L, map);
                        Building e = c.GetEdifice(map);
                        RoofDef r = map.roofGrid.RoofAt(c);
                        bool wantRock = L < H;
                        if (L == H && map.fogGrid.IsFogged(c))
                        {
                            topsFogged++;
                        }
                        if (L < H)
                        {
                            if (map.fogGrid.IsFogged(c))
                            {
                                rockFogged++;
                            }
                            else
                            {
                                rockOpen++;
                            }
                        }
                        if (wantRock != (e != null && e.def.building.isNaturalRock))
                        {
                            badRock++;
                            first = first ?? $"rock {c} L{L} H{H}";
                        }
                        if ((L < H) != (r == RoofDefOf.RoofRockThick) || L == H && r != null)
                        {
                            badRoof++;
                            first = first ?? $"roof {c} L{L} H{H} {r?.defName}";
                        }
                        if (L <= H && gr != null && gr.def.building.isNaturalRock && !gr.def.building.isResourceRock && c.GetTerrain(map) != gr.def.building.naturalTerrain)
                        {
                            badDef++;
                            first = first ?? $"terrain {c} {c.GetTerrain(map).defName} over {gr.def.defName}";
                        }
                    }
                    // a ledge 1-2 cells wide: H differs by one on both sides within 3 cells in a straight line
                    for (int d = 0; d < 2; d++)
                    {
                        int dx = d == 0 ? 1 : 0, dz = d == 0 ? 0 : 1;
                        for (int w = 1; w <= 2; w++)
                        {
                            int x0 = x - dx, z0 = z - dz, x1 = x + dx * w, z1 = z + dz * w;
                            if (x0 < 0 || z0 < 0 || x1 >= sx || z1 >= gz || H < 2)
                            {
                                continue;
                            }
                            int lo = h[z0 * sx + x0], hi = h[z1 * sx + x1];
                            bool flat = true;
                            for (int k = 0; k < w; k++)
                            {
                                flat &= h[(z + dz * k) * sx + x + dx * k] == H - 1;
                            }
                            if (flat && lo < H - 1 && hi >= H && w <= 2 && h[z * sx + x] == H - 1)
                            {
                                narrow++;
                            }
                        }
                    }
                }
            }
            if (Mountains.LastHeights != null && Mountains.LastHeights.Length == h.Length)
            {
                int mism = 0;
                string firstM = null;
                for (int i = 0; i < h.Length; i++)
                {
                    if (h[i] != Mountains.LastHeights[i])
                    {
                        mism++;
                        if (firstM == null)
                        {
                            IntVec3 g = new IntVec3(i % sx, 0, i / sx);
                            firstM = $"{g}: fill {Mountains.LastHeights[i]}, now {h[i]}; terrain by level " + string.Join(",", Enumerable.Range(1, cap).Select(L => Levels.Cell(g, L, map).GetTerrain(map).defName + "/" + (Levels.Cell(g, L, map).GetEdifice(map)?.def.defName ?? "-")));
                        }
                    }
                }
                Result("fill heights are what the map shows (NaturalTop)", mism == 0, $"{mism} differ{(firstM == null ? "" : ", first " + firstM)}");
            }
            Info("heights (cells per top level): " + string.Join(", ", Enumerable.Range(0, cap + 1).Select(L => $"L{L} {hist[L]}")));
            Result("fill: a top only over thick roof, rock below every top, thick roof under every rock floor and none on tops, rough floor of the column's rock",
                badThick == 0 && badRock == 0 && badRoof == 0 && badDef == 0 && hist.Skip(1).Sum() > 0,
                $"thick {badThick}, rock {badRock}, roof {badRoof}, terrain {badDef}{(first == null ? "" : ", first " + first)}");
            Info($"narrow ledges seen in straight lines (approximate): {narrow}");
            Info($"fog: tops fogged {topsFogged} of {hist.Skip(1).Sum()}, band rock fogged {rockFogged}, revealed faces {rockOpen}");
            // ore share: band vs ground
            int gRock = 0, gOre = 0, bRock = 0, bOre = 0;
            foreach (Thing t in map.listerThings.AllThings)
            {
                if (t.def.building == null || !t.def.building.isNaturalRock)
                {
                    continue;
                }
                bool band = ProjectZ.FloorOf(t.Position, map) >= 1;
                if (band)
                {
                    bRock++;
                    bOre += t.def.building.isResourceRock ? 1 : 0;
                }
                else
                {
                    gRock++;
                    gOre += t.def.building.isResourceRock ? 1 : 0;
                }
            }
            float gs = gRock == 0 ? 0 : gOre / (float)gRock, bs = bRock == 0 ? 0 : bOre / (float)bRock;
            Info($"ore share: ground {gOre}/{gRock} = {gs:P1}, bands {bOre}/{bRock} = {bs:P1}");
        }

        // Around g (rows north first): per cell the top level, then flags of the top cell: f fogged, r roofed, d glow < 0.5,
        // and g if the ground cell is fogged.
        private static void TmDump(Map map, AnnexMapComponent a, IntVec3 g)
        {
            List<string> rows = new List<string>();
            for (int dz = 8; dz >= -8; dz--)
            {
                string row = "";
                for (int dx = -12; dx <= 12; dx++)
                {
                    IntVec3 c = new IntVec3(g.x + dx, 0, g.z + dz);
                    if (c.x < 0 || c.z < 0 || c.x >= map.Size.x || c.z >= a.groundZ)
                    {
                        row += " ....";
                        continue;
                    }
                    int H = Column.NaturalTop(c, map);
                    IntVec3 t = H > 0 ? Levels.Cell(c, H, map) : c;
                    row += " " + H + (map.fogGrid.IsFogged(t) ? "f" : "-") + (map.roofGrid.Roofed(t) ? "r" : "-") + (map.glowGrid.GroundGlowAt(t) < 0.5f ? "d" : "-") + (map.fogGrid.IsFogged(c) ? "g" : "-");
                }
                rows.Add(row);
            }
            Info($"dump around {g} (x {g.x - 12}..{g.x + 12}, z {g.z + 8} down to {g.z - 8}):\n" + string.Join("\n", rows));
        }

        private static void TmPlanShots(Map map, AnnexMapComponent a)
        {
            tmShots.Clear();
            int sx = map.Size.x, gz = a.groundZ, cap = a.MaxLevel;
            int[] h = TmHeights(map, a);
            int H(int x, int z) => x < 0 || z < 0 || x >= sx || z >= gz ? -1 : h[z * sx + x];
            // the highest area: the most height in a 9x9 block
            IntVec3 peak = IntVec3.Invalid, cliff = IntVec3.Invalid, terrace = IntVec3.Invalid, lip = IntVec3.Invalid;
            int best = -1, bestCliff = 0, bestTer = -1;
            for (int z = 6; z < gz - 6; z += 2)
            {
                for (int x = 6; x < sx - 6; x += 2)
                {
                    int s = 0;
                    for (int dz = -4; dz <= 4; dz++)
                    {
                        for (int dx = -4; dx <= 4; dx++)
                        {
                            s += H(x + dx, z + dz);
                        }
                    }
                    if (s > best)
                    {
                        best = s;
                        peak = new IntVec3(x, 0, z);
                    }
                    // sheer cliff: a drop of 2+ levels to a neighbour
                    for (int d = 0; d < 4; d++)
                    {
                        IntVec3 dir = GenAdj.CardinalDirections[d];
                        int drop = H(x, z) - H(x + dir.x, z + dir.z);
                        if (drop > bestCliff && H(x + dir.x, z + dir.z) >= 0)
                        {
                            bestCliff = drop;
                            cliff = new IntVec3(x + dir.x * 3, 0, z + dir.z * 3);
                        }
                        // terrace: level L here, L+1 3-5 cells one way, L-1 3-5 the other way
                        int L = H(x, z);
                        if (L >= 1 && H(x + dir.x * 4, z + dir.z * 4) > L && H(x - dir.x * 4, z - dir.z * 4) < L && H(x + dir.x, z + dir.z) == L && H(x - dir.x, z - dir.z) == L && L > bestTer)
                        {
                            bestTer = L;
                            terrace = new IntVec3(x, 0, z);
                        }
                    }
                    // the edge lip: a level-1 top next to ground rock with no top
                    if (!lip.IsValid && H(x, z) == 1)
                    {
                        for (int d = 0; d < 4; d++)
                        {
                            IntVec3 n = new IntVec3(x, 0, z) + GenAdj.CardinalDirections[d] * 2;
                            Building b = n.InBounds(map) ? n.GetEdifice(map) : null;
                            if (H(n.x, n.z) == 0 && b != null && b.def.building.isNaturalRock)
                            {
                                lip = new IntVec3(x, 0, z);
                            }
                        }
                    }
                }
            }
            int top = peak.IsValid ? H(peak.x, peak.z) : 0;
            Info($"shots: peak {peak} (level {top}), cliff {cliff} (drop {bestCliff}), terrace {terrace} (level {bestTer}), lip {lip}");
            IntVec3 mid = new IntVec3(sx / 2, 0, gz / 2);
            if (tfStairs.IsValid && TfStairsBuilt(map))
            {
                tmShots.Add(new TmShot { name = "tm_stairs_top", floor = cap, g = tfStairs, size = 10f });
                tmShots.Add(new TmShot { name = "tm_stairs_view1", floor = 1, g = tfStairs, size = 10f });
            }
            if (tfMine1.IsValid)
            {
                tmShots.Add(new TmShot { name = "tm_mined_view1", floor = 1, g = ProjectZ.GroundCellOf(tfMine1, map), size = 10f });
            }
            tmShots.Add(new TmShot { name = "tm_overview", floor = cap, g = peak.IsValid ? peak : mid, size = 45f });
            tmShots.Add(new TmShot { name = "tm_overview_wide", floor = cap, g = mid, size = 80f });
            if (peak.IsValid)
            {
                tmShots.Add(new TmShot { name = "tm_peak_play", floor = cap, g = peak, size = 18f });
                for (int L = 0; L <= cap; L++)
                {
                    tmShots.Add(new TmShot { name = $"tm_peak_view{L}", floor = L, g = peak, size = 18f });
                }
            }
            if (cliff.IsValid)
            {
                tmShots.Add(new TmShot { name = "tm_cliff", floor = cap, g = cliff, size = 12f });
            }
            if (terrace.IsValid)
            {
                tmShots.Add(new TmShot { name = "tm_terrace", floor = cap, g = terrace, size = 12f });
            }
            if (lip.IsValid)
            {
                tmShots.Add(new TmShot { name = "tm_lip", floor = cap, g = lip, size = 10f });
                tmShots.Add(new TmShot { name = "tm_lip_view1", floor = 1, g = lip, size = 10f });
            }
            tmNoFogAt = tmShots.Count;
            if (peak.IsValid)
            {
                tmShots.Add(new TmShot { name = "tm_nofog_peak_play", floor = cap, g = peak, size = 18f });
            }
            if (terrace.IsValid)
            {
                tmShots.Add(new TmShot { name = "tm_nofog_terrace", floor = cap, g = terrace, size = 12f });
                TmDump(map, a, terrace);
            }
        }
    }
}
