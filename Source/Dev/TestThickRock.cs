using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode thickrock (thick-roof columns keep rock above the ground, Mountains.Heights; a player's report: every mined
    // cell broke the overhead mountain). Run with -projectzfloors=2/3/5 -projectzmapsize=250 -projectztallmountains; the
    // fixed seed and Mountainous tile of tallmtn. -projectztkat=x,z pins the mountain picked for the shots (the same spot
    // on two builds). Setup: the fill's checks (TmChecks), the rule's own recount, the numbers (PERF lines), shots at the
    // ground view, level 1 and the top, with the game's fog and with the bands unfogged. The game is saved (pz_thickrock);
    // every check reloads it and runs vanilla's mining job under fixed seeds, one miner:
    //   T   a tunnel into the mountain from open ground, through the edge rock and 6 thick-roof cells: the thick-roof cells
    //       keep their roof, the overlay and the rock above; MA Mine above at its end: the rock above goes, a shaft opens
    //   O   a lone ore cell, E a thin-roof edge rock, K a roofless outcrop cell, each under a bare level-1 top: they open
    //   U   a tunnel on level 1 from a walkable top: under rock the roof stays; under a bare top or on the top level the sky
    //       opens; the cell under each mined rock loses its thick roof and stays roofed
    //   MB  Mine below from a top: the rock under the top goes, the top opens
    // After every check the whole map follows the rule (TkRule: a thick roof if and only if rock right above).
    // Each check ends with terrain, roofs and edifices outside its site unchanged. Shots: tk_*.png, tkn_*.png (unfogged).
    public static partial class TestRunner
    {
        private static IntVec3 tkMtn = IntVec3.Invalid, tkEdge = IntVec3.Invalid;
        private static readonly List<IntVec3> tkTunnel = new List<IntVec3>(), tkUp = new List<IntVec3>();
        private static readonly List<string> tkUpAbove = new List<string>();
        private static IntVec3 tkS, tkOre, tkOreS, tkThin, tkThinS, tkLone, tkLoneS, tkUpS, tkMbTop, tkMbS;
        private static readonly List<TmShot> tkShots = new List<TmShot>();
        private static int tkShot;

        private static bool TkThick(IntVec3 g, Map map) => map.roofGrid.RoofAt(g)?.isThickRoof ?? false;

        private static bool TkRock(IntVec3 c, Map map) => c.IsValid && c.InBounds(map) && (c.GetEdifice(map)?.def.building?.isNaturalRock ?? false);

        // The biggest overhead mountain: the most thick roofs in a 41x41 block.
        private static void TkPickMountain(Map map, AnnexMapComponent a)
        {
            int sx = map.Size.x, gz = a.groundZ;
            if (GenCommandLine.TryGetCommandLineArg("projectztkat", out string at))
            {
                string[] p = at.Split(',');
                tkMtn = new IntVec3(int.Parse(p[0]), 0, int.Parse(p[1]));
            }
            else
            {
                int best = -1;
                for (int z = 25; z < gz - 25; z += 5)
                {
                    for (int x = 25; x < sx - 25; x += 5)
                    {
                        int s = 0;
                        for (int dz = -20; dz <= 20; dz += 2)
                        {
                            for (int dx = -20; dx <= 20; dx += 2)
                            {
                                s += TkThick(new IntVec3(x + dx, 0, z + dz), map) ? 1 : 0;
                            }
                        }
                        if (s > best)
                        {
                            best = s;
                            tkMtn = new IntVec3(x, 0, z);
                        }
                    }
                }
            }
            // the mountain's south edge seen from the camera: straight south to the first ground cell without a thick roof
            IntVec3 c = tkMtn;
            while (c.z > 0 && TkThick(c, map))
            {
                c += IntVec3.South;
            }
            tkEdge = c;
            Info($"thickrock shots: mountain {tkMtn}, south edge {tkEdge}");
        }

        private static void TkChecks(Map map, AnnexMapComponent a)
        {
            int cap = a.MaxLevel, sx = map.Size.x, gz = a.groundZ;
            int thick = 0, caves = 0, noRock = 0, topLow = 0, solid = 0, lips = 0, lipsRaised = 0, oreBand = 0;
            string first = null;
            for (int z = 0; z < gz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    IntVec3 g = new IntVec3(x, 0, z);
                    IntVec3 u = L1(g, map);
                    int top = Column.NaturalTop(g, map);
                    if (!TkThick(g, map))
                    {
                        if (TkRock(g, map))
                        {
                            lips++;
                            lipsRaised += top >= 2 || TkRock(u, map) ? 1 : 0;
                        }
                        continue;
                    }
                    thick++;
                    caves += TkRock(g, map) ? 0 : 1;
                    if (!TkRock(u, map))
                    {
                        noRock++;
                        first = first ?? $"{g}: nothing but {u.GetTerrain(map).defName} on level 1, top {top}";
                    }
                    if (cap >= 2 && top < 2)
                    {
                        topLow++;
                    }
                    if (TkRock(Levels.Cell(g, cap, map), map))
                    {
                        solid++;
                    }
                }
            }
            foreach (Thing t in map.listerThings.AllThings)
            {
                if ((t.def.building?.isResourceRock ?? false) && ProjectZ.FloorOf(t.Position, map) >= 1)
                {
                    oreBand++;
                }
            }
            Info($"thickrock columns: thick-roof {thick} ({caves} over open ground), solid to the top level {solid}; other ground rock {lips} ({lipsRaised} raised above H 1, over tall things); ore cells in the bands {oreBand}; cap {cap}, map {map.Size}, floors {a.floors}");
            Result("every thick-roof column has rock on level 1", thick > 0 && noRock == 0, $"{thick} columns, {noRock} without rock{(first == null ? "" : ", first " + first)}");
            TkRule(map, "after generation");
            if (cap >= 2)
            {
                Result("the top of a thick-roof column stands on level 2 or higher", topLow == 0, $"{topLow} lower; solid to the top level {solid}");
            }
            else
            {
                Result("2 storeys: every thick-roof column is solid to level 1, no top; ore there too", solid == thick && oreBand > 0, $"solid {solid} of {thick}; ore cells {oreBand}");
            }
            Result("rock without a thick roof keeps H 1 (but over tall things)", lips > 0 && lipsRaised <= Mountains.LastTallRaised, $"{lipsRaised} raised, {Mountains.LastTallRaised} cells raised over tall things");
        }

        private static bool TkSites(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int cap = a.MaxLevel;
            IntVec3 home = new IntVec3(map.Size.x / 2, 0, a.groundZ / 2);
            List<IntVec3> near = Near(home, 120, map).Where(g => g.x >= 8 && g.z >= 8 && g.x < map.Size.x - 8 && g.z < a.groundZ - 8).ToList();
            // T: from open ground straight through the edge rock and 6 thick-roof cells
            tkTunnel.Clear();
            tkS = IntVec3.Invalid;
            foreach (IntVec3 s in near)
            {
                if (!OtStand(s, map))
                {
                    continue;
                }
                foreach (IntVec3 d in GenAdj.CardinalDirections)
                {
                    List<IntVec3> line = new List<IntVec3>();
                    int thick = 0;
                    for (int k = 1; k <= 11 && thick < 6; k++)
                    {
                        IntVec3 c = s + d * k;
                        if (!OtRock(c, map) || thick > 0 && !TkThick(c, map))
                        {
                            break;
                        }
                        line.Add(c);
                        thick += TkThick(c, map) ? 1 : 0;
                    }
                    if (thick == 6 && !map.fogGrid.IsFogged(line[0]))
                    {
                        tkTunnel.AddRange(line);
                        tkS = s;
                        break;
                    }
                }
                if (tkS.IsValid)
                {
                    break;
                }
            }
            List<IntVec3> taken = new List<IntVec3>(tkTunnel);
            IntVec3 Pick(System.Func<IntVec3, bool> want, out IntVec3 side)
            {
                side = IntVec3.Invalid;
                foreach (IntVec3 g in near)
                {
                    if (!OtRock(g, map) || TkThick(g, map) || !OtBareTop(g, map) || !OtFar(g, taken) || !want(g))
                    {
                        continue;
                    }
                    IntVec3 s = OtSide(g, map);
                    if (s.IsValid)
                    {
                        side = s;
                        taken.Add(g);
                        return g;
                    }
                }
                return IntVec3.Invalid;
            }
            tkOre = Pick(g => g.GetFirstMineable(map).def.building.isResourceRock, out tkOreS);
            tkThin = Pick(g => map.roofGrid.RoofAt(g) == RoofDefOf.RoofRockThin && !g.GetFirstMineable(map).def.building.isResourceRock, out tkThinS);
            tkLone = Pick(g => map.roofGrid.RoofAt(g) == PZDefOf.PZ_FloorRoof && !g.GetFirstMineable(map).def.building.isResourceRock, out tkLoneS);
            // U: a walkable level-1 top, then 6 level-1 rock cells straight on; the longest stretch of thick-roof columns first
            tkUp.Clear();
            tkUpAbove.Clear();
            tkUpS = IntVec3.Invalid;
            foreach (IntVec3 g in near)
            {
                IntVec3 s = L1(g, map);
                if (!TmTop(s, map) || !OtFar(g, taken) || !Mountains.WalkableRaw(s, map))
                {
                    continue;
                }
                foreach (IntVec3 d in GenAdj.CardinalDirections)
                {
                    List<IntVec3> line = Enumerable.Range(1, 6).Select(k => L1(g + d * k, map)).ToList();
                    if (line.All(c => c.IsValid && OtRock(c, map)))
                    {
                        tkUp.AddRange(line);
                        tkUpS = s;
                        break;
                    }
                }
                if (tkUpS.IsValid)
                {
                    taken.Add(g);
                    break;
                }
            }
            foreach (IntVec3 c in tkUp)
            {
                IntVec3 up = FloorRoof.Above(c, map);
                tkUpAbove.Add(!up.IsValid ? "none" : TkRock(up, map) ? "rock" : TmTop(up, map) ? "top" : "other");
            }
            // MB: a bare top over rock with a walkable top beside it, on the highest level it is found near the middle
            tkMbTop = tkMbS = IntVec3.Invalid;
            foreach (IntVec3 g in near)
            {
                if (!OtFar(g, taken))
                {
                    continue;
                }
                int L = Column.NaturalTop(g, map);
                IntVec3 t = L >= 1 ? Levels.Cell(g, L, map) : IntVec3.Invalid;
                if (!t.IsValid || !TmTop(t, map) || !OtRock(FloorRoof.Below(t, map), map) || !t.GetThingList(map).All(x => x is Plant || x.def.category == ThingCategory.Filth))
                {
                    continue;
                }
                IntVec3 s = GenAdj.CardinalDirections.Select(d => t + d).FirstOrDefault(n => n.InBounds(map) && ProjectZ.SameBand(n, t, map) && TmTop(n, map));
                if (s.IsValid && s != IntVec3.Zero)
                {
                    tkMbTop = t;
                    tkMbS = s;
                    break;
                }
            }
            Info($"thickrock sites: T {string.Join(" ", tkTunnel)} from {tkS}; O {tkOre} from {tkOreS}; E {tkThin} from {tkThinS}; K {tkLone} from {tkLoneS}; U {string.Join(" ", tkUp)} from {tkUpS} above [{string.Join(",", tkUpAbove)}]; MB {tkMbTop} from {tkMbS}");
            return tkS.IsValid && tkOre.IsValid && tkThin.IsValid;
        }

        private static void TkPlanShots(Map map, string pre)
        {
            int cap = ProjectZ.Annex(map).MaxLevel;
            tkShots.Clear();
            tkShots.Add(new TmShot { name = pre + "_wide", floor = cap, g = tkMtn, size = 60f });
            foreach (int L in new[] { 0, 1, cap }.Distinct())
            {
                string lv = L == cap ? "top" : "v" + L;
                tkShots.Add(new TmShot { name = $"{pre}_mtn_{lv}", floor = L, g = tkMtn, size = 26f });
                tkShots.Add(new TmShot { name = $"{pre}_edge_{lv}", floor = L, g = tkEdge, size = 13f });
            }
            tkShot = 0;
        }

        // Shots at noon, whenever the mining ended.
        private static void TkNoon(Map map)
        {
            SetHour(map, 12);
            map.skyManager.ForceSetCurSkyGlow(1f);
        }

        private static string TkCell(IntVec3 c, Map map)
        {
            IntVec3 u = FloorRoof.Above(c, map);
            string above = !u.IsValid ? "no level" : TkRock(u, map) ? "rock" : u.GetTerrain(map)?.defName;
            return $"{c}: rock {(c.GetFirstMineable(map)?.def.defName ?? "-")}, roof {MuRoof(c, map)}, above {above}";
        }

        // The rule over the whole map: a cell has a thick roof if and only if rock stands right above it.
        private static void TkRule(Map map, string when)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int thick = 0, under = 0, thickNoRock = 0, rockNoThick = 0;
            string first = null;
            for (int L = 0; L <= a.MaxLevel; L++)
            {
                for (int z = 0; z < a.groundZ; z++)
                {
                    for (int x = 0; x < map.Size.x; x++)
                    {
                        IntVec3 c = Levels.Cell(new IntVec3(x, 0, z), L, map);
                        bool t = TkThick(c, map), r = OtRock(FloorRoof.Above(c, map), map);
                        thick += t ? 1 : 0;
                        under += r ? 1 : 0;
                        if (t != r)
                        {
                            thickNoRock += t ? 1 : 0;
                            rockNoThick += r ? 1 : 0;
                            first = first ?? $"{TkCell(c, map)} L{L}";
                        }
                    }
                }
            }
            Result($"the rule {when}: a thick roof if and only if rock right above", thick > 0 && thickNoRock == 0 && rockNoThick == 0, $"thick {thick}, under rock {under}; thick without rock {thickNoRock}, rock without thick {rockNoThick}{(first == null ? "" : ", first " + first)}");
        }

        private static void TkEnd(Map map, string check, string next)
        {
            BareTop.Off = false;
            Find.PlaySettings.showRoofOverlay = false;
            MuOutside(map, check);
            TkRule(map, "after " + check);
            muPointer = IntVec3.Invalid;
            Find.UIRoot.screenshotMode.Active = false;
            FloorView.Set(0);
            Find.DesignatorManager.Deselect();
            if (next == null)
            {
                Finish();
                return;
            }
            stage = next;
            pendingLoad = "pz_thickrock";
        }

        // A site that opens: the rock gone, its top open air, the cell under the sky, the rule ran once.
        private static void TkOpens(Map map, string name, IntVec3 c)
        {
            IntVec3 u = L1(c, map);
            Result(name, !OtRock(c, map) && u.GetTerrain(map) == PZDefOf.PZ_OpenAir && !map.roofGrid.Roofed(c) && BareTop.Opened - otOpened0 == 1, $"{TkCell(c, map)}; tops opened {BareTop.Opened - otOpened0}");
        }

        private static bool StepThickRock(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (stage.StartsWith("tk_s_") || stage.StartsWith("tk_l_") || stage.StartsWith("tk_old_"))
            {
                return StepThickRock2(map);
            }
            switch (stage)
            {
                case "tk_setup":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    DebugSettings.enableRandomMentalStates = false;
                    if (a == null || !Mountains.On(map))
                    {
                        Result("thickrock: a tall-mountain map", false, "no annex or no tall mountains (-projectztallmountains)");
                        Finish();
                        return true;
                    }
                    Info($"thickrock: map {map.Size}, tile {map.Tile} {map.TileInfo?.PrimaryBiome?.defName} {map.TileInfo?.hilliness}, floors {a.floors}, ground rows {a.groundZ}, step {Mountains.Step}");
                    Info($"PERF fill: {Mountains.LastSeconds:0.00} s ({Mountains.LastTimes}); rock {Mountains.LastRock}, tops {Mountains.LastTops}, ore cells {Mountains.LastOre}, fogged band cells {Mountains.LastFogged}, rim {Mountains.LastRim}, highest top level {Mountains.LastMaxH}");
                    mtPerf.Add($"fill s {Mountains.LastSeconds:0.00}, rock {Mountains.LastRock}, ore {Mountains.LastOre}, tops {Mountains.LastTops}");
                    TmChecks(map, a);
                    TkChecks(map, a);
                    if (GenCommandLine.CommandLineArgPassed("projectztkmakeold"))
                    {
                        stage = "tk_old_make";
                        return true;
                    }
                    if (GenCommandLine.CommandLineArgPassed("projectztkfillonly"))   // (a seed scan: the fill's checks only)
                    {
                        Finish();
                        return true;
                    }
                    MtPerfMap(map, "after generation");
                    MtTicks(map);
                    mtFrames = 0;
                    mtFrameSum = 0f;
                    stage = "tk_frames";
                    return true;
                }
                case "tk_frames":
                    if (mtFrames > 0)
                    {
                        mtFrameSum += Time.unscaledDeltaTime;
                    }
                    if (++mtFrames <= 180)
                    {
                        return true;
                    }
                    mtPerf.Add($"frame ms {mtFrameSum / 180f * 1000f:0.00}");
                    stage = "tk_save";
                    return true;
                case "tk_save":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    bool sites = TkSites(map);
                    Result("thickrock sites: T, O, E", sites, "see the sites line");
                    TkPickMountain(map, a);
                    List<Pawn> all = Colonists(map).Where(p => !p.Downed).OrderBy(p => p.thingIDNumber).ToList();
                    Pawn miner = all.First(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining));
                    otMinerId = miner.thingIDNumber;
                    SetHour(map, 11);
                    FloorView.Set(0);
                    Stopwatch sw = Stopwatch.StartNew();
                    GameDataSaveLoader.SaveGame("pz_thickrock");
                    mtSaveS = sw.Elapsed.TotalSeconds;
                    string path = GenFilePaths.FilePathForSavedGame("pz_thickrock");
                    mtSaveBytes = File.Exists(path) ? new FileInfo(path).Length : -1;
                    mtPerf.Add($"save s {mtSaveS:0.00}, save KB {mtSaveBytes / 1024}");
                    stage = sites ? "tk_loaded" : "tk_loaded_end";
                    pendingLoad = "pz_thickrock";
                    return true;
                }
                case "tk_loaded":
                case "tk_loaded_end":
                {
                    bool end = stage == "tk_loaded_end";
                    mtPerf.Add($"load s {LastLoadSeconds:0.00}");
                    MtPerfMap(map, "after load");
                    MtTicks(map);
                    System.GC.Collect();
                    System.GC.WaitForPendingFinalizers();
                    mtPerf.Add($"managed MB after GC {System.GC.GetTotalMemory(true) / (1024 * 1024)}, working set MB {Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)}");
                    Info("PERF SUMMARY | " + string.Join(" | ", mtPerf));
                    TkPlanShots(map, "tk");
                    map.skyManager.ForceSetCurSkyGlow(1f);
                    stage = "tk_shot";
                    tkAfterShots = end ? "tk_finish" : "tk_unfog";
                    return true;
                }
                case "tk_shot":
                    if (tkShot >= tkShots.Count)
                    {
                        stage = tkAfterShots;
                        return true;
                    }
                    tkShot++;
                    MuShot(map, tkShots[tkShot - 1].name, tkShots[tkShot - 1].floor, tkShots[tkShot - 1].g, tkShots[tkShot - 1].size, "tk_shot");
                    return true;
                case "tk_unfog":
                    foreach (IntVec3 c in map.AllCells)
                    {
                        if (ProjectZ.FloorOf(c, map) >= 1 && map.fogGrid.IsFogged(c))
                        {
                            map.fogGrid.Unfog(c);
                        }
                    }
                    TkPlanShots(map, "tkn");
                    stage = "tk_shot";
                    tkAfterShots = "tk_reload";
                    return true;
                case "tk_reload":
                    Find.UIRoot.screenshotMode.Active = false;
                    stage = "tk_t";
                    pendingLoad = "pz_thickrock";
                    return true;
                case "tk_finish":
                    Finish();
                    return true;

                // ------------------------------------------------------------ T: the tunnel
                case "tk_t":
                    OtMine(map, tkTunnel, tkS, false);
                    MuRun(2500, 71000, () => tkTunnel.All(c => !OtRock(c, map)), "tk_t_ticks");
                    return true;

                case "tk_t_ticks":
                    StartTicks(300, "tk_t_check");
                    return true;

                case "tk_t_check":
                {
                    List<IntVec3> inner = tkTunnel.Where(c => !OtRock(c, map) && FloorRoof.Above(c, map).IsValid && TkThick(c, map)).ToList();
                    List<IntVec3> lip = tkTunnel.Except(inner).ToList();
                    int roofed = inner.Count(c => map.roofGrid.Roofed(c)), overlay = inner.Count(c => map.roofGrid.GetCellBool(map.cellIndices.CellToIndex(c))), rockAbove = inner.Count(c => TkRock(FloorRoof.Above(c, map), map));
                    Info($"T cells: {string.Join("; ", tkTunnel.Select(c => TkCell(c, map)))}");
                    Result("T a tunnel under a thick roof (6 cells): no sky, the roof overlay stays, rock above; the tops opened only over the edge rock",
                        tkTunnel.All(c => !OtRock(c, map)) && inner.Count == 6 && roofed == 6 && overlay == 6 && rockAbove == 6 && BareTop.Opened - otOpened0 <= lip.Count,
                        $"mined {tkTunnel.Count(c => !OtRock(c, map))}/{tkTunnel.Count}; still thick-roofed {inner.Count}, of them roofed {roofed}, overlay {overlay}, rock above {rockAbove}; edge cells {lip.Count}, tops opened {BareTop.Opened - otOpened0}");
                    TkNoon(map);
                    Find.PlaySettings.showRoofOverlay = true;
                    MuShot(map, "tk_t_ground", 0, tkTunnel[tkTunnel.Count / 2], 9f, "tk_t_shot2");
                    return true;
                }

                case "tk_t_shot2":
                    Find.PlaySettings.showRoofOverlay = false;
                    MuShot(map, "tk_t_top", a.MaxLevel, tkTunnel[tkTunnel.Count / 2], 9f, "tk_ma");
                    return true;

                // ------------------------------------------------------------ MA: Mine above at the end of the tunnel
                case "tk_ma":
                {
                    Find.UIRoot.screenshotMode.Active = false;
                    FloorView.Set(0);
                    IntVec3 w = tkTunnel[tkTunnel.Count - 1];
                    Designator_MineAbove d = new Designator_MineAbove();
                    AcceptanceReport r = d.CanDesignateCell(w);
                    Info($"MA at {w}: {TkCell(w, map)}; designator {r.Accepted} {r.Reason}");
                    if (!r.Accepted)
                    {
                        Result("MA Mine above at the tunnel's end", false, $"refused: {r.Reason}");
                        TkEnd(map, "T", "tk_o");
                        return true;
                    }
                    d.DesignateSingleCell(w);
                    MuRun(800, 71100, () => !OtRock(FloorRoof.Above(w, map), map), "tk_ma_check");
                    return true;
                }

                case "tk_ma_check":
                {
                    IntVec3 w = tkTunnel[tkTunnel.Count - 1], u = FloorRoof.Above(w, map);
                    Result("MA Mine above at the tunnel's end: the rock above goes, its floor opens (a shaft)", !OtRock(u, map) && u.GetTerrain(map) == PZDefOf.PZ_OpenAir,
                        $"{TkCell(w, map)}; level 1: {TkCell(u, map)}, terrain {u.GetTerrain(map).defName}");
                    TkEnd(map, "T + MA", "tk_o");
                    return true;
                }

                // ------------------------------------------------------------ O, E, K: rock that opens
                case "tk_o":
                    OtMine(map, new[] { tkOre }, tkOreS, false);
                    MuRun(600, 71200, () => !OtRock(tkOre, map), "tk_o_check");
                    return true;

                case "tk_o_check":
                    TkOpens(map, "O a lone ore cell under a bare top opens", tkOre);
                    TkNoon(map);
                    MuShot(map, "tk_o_ground", 0, tkOre, 7f, "tk_o_end");
                    return true;

                case "tk_o_end":
                    TkEnd(map, "O", "tk_e");
                    return true;

                case "tk_e":
                    OtMine(map, new[] { tkThin }, tkThinS, false);
                    MuRun(600, 71300, () => !OtRock(tkThin, map), "tk_e_check");
                    return true;

                case "tk_e_check":
                    TkOpens(map, "E a thin-roof edge rock under a bare top opens", tkThin);
                    TkNoon(map);
                    MuShot(map, "tk_e_ground", 0, tkThin, 7f, "tk_e_end");
                    return true;

                case "tk_e_end":
                    TkEnd(map, "E", tkLone.IsValid ? "tk_k" : tkUp.Count > 0 ? "tk_u" : "tk_mb");
                    return true;

                case "tk_k":
                    OtMine(map, new[] { tkLone }, tkLoneS, false);
                    MuRun(600, 71400, () => !OtRock(tkLone, map), "tk_k_check");
                    return true;

                case "tk_k_check":
                    TkOpens(map, "K a roofless rock under a bare top opens", tkLone);
                    TkEnd(map, "K", tkUp.Count > 0 ? "tk_u" : "tk_mb");
                    return true;

                // ------------------------------------------------------------ U: a tunnel on level 1
                case "tk_u":
                    OtMine(map, tkUp, tkUpS, false);
                    MuRun(2500, 71500, () => tkUp.All(c => !OtRock(c, map)), "tk_u_ticks");
                    return true;

                case "tk_u_ticks":
                    StartTicks(300, "tk_u_check");
                    return true;

                case "tk_u_check":
                {
                    int keep = 0, kept = 0, bare = 0, opened = 0;
                    for (int i = 0; i < tkUp.Count; i++)
                    {
                        bool roofed = map.roofGrid.Roofed(tkUp[i]);
                        if (tkUpAbove[i] == "top" || tkUpAbove[i] == "none")
                        {
                            bare++;
                            opened += roofed ? 0 : 1;
                        }
                        else
                        {
                            keep++;
                            kept += roofed ? 1 : 0;
                        }
                    }
                    Info($"U cells: {string.Join("; ", tkUp.Select(c => TkCell(c, map)))}");
                    Result("U a tunnel on level 1: cells under rock keep their roof, cells under a bare top or on the top level open to the sky", tkUp.All(c => !OtRock(c, map)) && kept == keep && opened == bare, $"mined {tkUp.Count(c => !OtRock(c, map))}/{tkUp.Count}; under rock {kept} of {keep} kept; else {opened} of {bare} opened");
                    List<IntVec3> belowU = tkUp.Select(c => FloorRoof.Below(c, map)).ToList();
                    Info($"U below: {string.Join("; ", belowU.Select(c => TkCell(c, map)))}");
                    Result("U the cell under each mined rock has no thick roof any more, its roof stays", belowU.All(c => !TkThick(c, map) && map.roofGrid.Roofed(c)), $"thin {belowU.Count(c => map.roofGrid.RoofAt(c) == RoofDefOf.RoofRockThin)}, floor roof {belowU.Count(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_FloorRoof)}, thick {belowU.Count(c => TkThick(c, map))}, none {belowU.Count(c => !map.roofGrid.Roofed(c))}");
                    TkNoon(map);
                    MuShot(map, "tk_u_v1", 1, tkUp[tkUp.Count / 2], 9f, "tk_u_end");
                    return true;
                }

                case "tk_u_end":
                    TkEnd(map, "U", "tk_mb");
                    return true;

                // ------------------------------------------------------------ MB: Mine below from a top
                case "tk_mb":
                {
                    if (!tkMbTop.IsValid)
                    {
                        Result("MB a site: a bare top over rock", false, "none found");
                        Finish();
                        return true;
                    }
                    Pawn miner = OtPawn(map, otMinerId);
                    MdWork(map, new List<Pawn> { miner }, WorkTypeDefOf.Mining);
                    Draft(miner, false);
                    Teleport(miner, tkMbS);
                    otOpened0 = BareTop.Opened;
                    MuStart(map, MuRect(new[] { tkMbTop, tkMbS }, map, 2), false);
                    IntVec3 t = tkMbTop;
                    bool marked = false;
                    MuRun(1000, 71600, () => !OtRock(FloorRoof.Below(t, map), map), "tk_mb_check", () =>
                    {
                        Designator_MineBelow d = new Designator_MineBelow();
                        if (!marked && d.CanDesignateCell(t).Accepted)
                        {
                            d.DesignateSingleCell(t);
                            marked = true;
                        }
                    });
                    return true;
                }

                case "tk_mb_check":
                {
                    IntVec3 b = FloorRoof.Below(tkMbTop, map);
                    Result("MB Mine below from a top: the rock under it goes, the top opens", !OtRock(b, map) && tkMbTop.GetTerrain(map) == PZDefOf.PZ_OpenAir,
                        $"top {tkMbTop} terrain {tkMbTop.GetTerrain(map).defName}; {TkCell(b, map)}");
                    TkEnd(map, "MB", a.MaxLevel == 1 ? "tk_s_mortar" : "tk_l_site");
                    return true;
                }
            }
            return false;
        }

        private static string tkAfterShots;
    }
}
