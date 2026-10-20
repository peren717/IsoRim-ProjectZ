using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode holefog: the hole fog rule (HoleFog). With -projectzsave=<name> (a copy of a player's save): every open hole
    // of the save described (column, landing, fog, jumps, reach) loaded once without the rule (-> the old fog, shots) and
    // once with it, then a drafted colonist ordered down a hole for real. Without a save: the fixtures (see below).
    public static partial class TestRunner
    {
        private static string hfSave;
        private static readonly List<IntVec3> hfHoles = new List<IntVec3>();
        private static int hfShotIdx, hfFogShot;
        private static bool[] hfFogBefore = new bool[0];
        private static Pawn hfPawn;
        private static IntVec3 hfTarget = IntVec3.Invalid;
        private static IntVec3 hfFrom = IntVec3.Invalid;
        private static bool hfJumped;

        private static string HfCell(IntVec3 c, Map map)
        {
            if (!c.IsValid || !c.InBounds(map))
            {
                return "invalid";
            }
            Building e = c.GetEdifice(map);
            return $"{c} L{Levels.LevelOf(c, map)} {c.GetTerrain(map).defName} roof {MuRoof(c, map)} {(e != null ? e.def.defName : "-")} {(map.fogGrid.IsFogged(c) ? "FOG" : "seen")}{(ShaftMarks.Has(c, map) ? " mark" : "")}";
        }

        // The walkable cells 4-connected to 'from' on its band (cap), and how many of them (and of their open
        // non-walkable neighbours) are fogged.
        private static int HfArea(IntVec3 from, Map map, out int fogged, out IntVec3 firstFog, int cap = 40000)
        {
            fogged = 0;
            firstFog = IntVec3.Invalid;
            if (!from.IsValid || !from.InBounds(map))
            {
                return 0;
            }
            int band = Levels.BandOfCell(from, map);
            HashSet<IntVec3> seen = new HashSet<IntVec3> { from };
            Queue<IntVec3> q = new Queue<IntVec3>();
            q.Enqueue(from);
            int n = 0;
            while (q.Count > 0 && n < cap)
            {
                IntVec3 c = q.Dequeue();
                n++;
                if (map.fogGrid.IsFogged(c))
                {
                    fogged++;
                    if (!firstFog.IsValid)
                    {
                        firstFog = c;
                    }
                }
                for (int d = 0; d < 4; d++)
                {
                    IntVec3 nb = c + GenAdj.CardinalDirections[d];
                    if (nb.InBounds(map) && Levels.BandOfCell(nb, map) == band && nb.Walkable(map) && seen.Add(nb))
                    {
                        q.Enqueue(nb);
                    }
                }
            }
            return n;
        }

        // The jumps over the open-air cell u (from its four walkable neighbours on u's level).
        private static List<(IntVec3 from, IntVec3 land, int fallen)> HfJumps(IntVec3 u, Map map)
        {
            List<(IntVec3, IntVec3, int)> list = new List<(IntVec3, IntVec3, int)>();
            for (int d = 0; d < 4; d++)
            {
                IntVec3 from = u - GenAdj.CardinalDirections[d];
                if (from.InBounds(map) && from.Walkable(map) && JumpGrid.LandingFrom(from, GenAdj.CardinalDirections[d], map, out IntVec3 land, out int fallen))
                {
                    list.Add((from, land, fallen));
                }
            }
            return list;
        }

        // One hole: the column, the fall's landing, the landing area's fog, the jumps and whether a colonist can get down.
        private static string HfDescribe(IntVec3 c, Map map, out bool ok)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            IntVec3 u = FloorRoof.Above(c, map);
            IntVec3 g = Levels.Ground(c, map);
            List<string> col = new List<string>();
            for (int L = a.MaxLevel; L >= a.MinLevel; L--)
            {
                IntVec3 x = Levels.Cell(g, L, map);
                col.Add($"L{L}: {HfCell(x, map)}");
            }
            IntVec3 land = u.IsValid ? Column.Landing(u, map, false, true, out int fallen, out _) : IntVec3.Invalid;
            int fallenN = land.IsValid ? Levels.LevelOf(u, map) - Levels.LevelOf(land, map) : 0;
            int area = HfArea(land, map, out int fogArea, out IntVec3 firstFog);
            bool passedFog = false;
            for (int L = Levels.LevelOf(u, map) - 1; land.IsValid && L > Levels.LevelOf(land, map); L--)
            {
                passedFog |= map.fogGrid.IsFogged(Levels.Cell(g, L, map));
            }
            List<(IntVec3 from, IntVec3 land, int fallen)> jumps = HfJumps(u, map);
            Pawn p = Colonists(map).FirstOrDefault();
            string reach = "";
            bool canReach = false;
            if (p != null && land.IsValid)
            {
                canReach = p.CanReach(land, PathEndMode.OnCell, Danger.Deadly);
                reach = $"{p} (at {p.Position} L{Levels.LevelOf(p.Position, map)}) can reach the landing: {canReach}";
                foreach ((IntVec3 from, IntVec3 l, int f) in jumps)
                {
                    reach += $"; reach jump-from {from}: {p.CanReach(from, PathEndMode.OnCell, Danger.Deadly)}";
                }
            }
            ok = land.IsValid && !passedFog && !map.fogGrid.IsFogged(land) && fogArea == 0 && (jumps.Count > 0 || land.GetThingList(map).Any(t => t is Building_Stairs)) && jumps.All(j => !j.land.Fogged(map));
            return $"hole C {c} (Is {Shaft.Is(c, map)}), U {HfCell(u, map)}; landing {HfCell(land, map)} fallen {fallenN}; passed cells fogged {passedFog}; landing area {area} walkable cells, {fogArea} fogged (first {firstFog}); "
                + $"jumps: {(jumps.Count == 0 ? "none" : string.Join(", ", jumps.Select(j => $"{j.from} -> {j.land} ({j.fallen} fl, {(j.land.Fogged(map) ? "into FOG" : "seen")})")))}; {reach}\n      column: {string.Join(" | ", col)}";
        }

        // Every open-air cell on an upper level with a seen walkable cell next to it (a place to jump from): where the fall
        // rule lands, and whether the landing or a passed cell is fogged.
        private static List<string> HfScan(Map map, out int intoFog)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            List<string> lines = new List<string>();
            HashSet<IntVec3> lands = new HashSet<IntVec3>();
            List<IntVec3> home = Colonists(map).Select(p => ProjectZ.GroundCellOf(p.Position, map)).ToList();
            intoFog = 0;
            for (int L = 1; L <= a.MaxLevel; L++)
            {
                for (int z = 0; z < a.groundZ; z++)
                {
                    for (int x = 0; x < map.Size.x; x++)
                    {
                        IntVec3 n = Levels.Cell(new IntVec3(x, 0, z), L, map);
                        if (!n.IsValid || n.GetTerrain(map) != PZDefOf.PZ_OpenAir || !home.Any(h => h.InHorDistOf(new IntVec3(x, 0, z), 45f)))
                        {
                            continue;
                        }
                        IntVec3 from = IntVec3.Invalid;
                        for (int d = 0; d < 4 && !from.IsValid; d++)
                        {
                            IntVec3 c = n + GenAdj.CardinalDirections[d];
                            if (c.InBounds(map) && ProjectZ.SameBand(c, n, map) && c.Walkable(map) && !map.fogGrid.IsFogged(c))
                            {
                                from = c;
                            }
                        }
                        if (!from.IsValid)
                        {
                            continue;
                        }
                        IntVec3 land = Column.Landing(n, map, false, true, out _, out _);
                        if (!land.IsValid || !land.InBounds(map))
                        {
                            continue;
                        }
                        bool fog = map.fogGrid.IsFogged(land);
                        IntVec3 g = new IntVec3(x, 0, z);
                        int lo = Levels.LevelOf(land, map);
                        for (int k = L - 1; k > lo; k--)
                        {
                            fog |= map.fogGrid.IsFogged(Levels.Cell(g, k, map));
                        }
                        if (fog)
                        {
                            intoFog++;
                            if (lands.Add(land) && lines.Count < 40)
                            {
                                bool jump = JumpGrid.LandingFrom(from, n - from, map, out IntVec3 jl, out int jf);
                                lines.Add($"open air {n} L{L} (jump from {from}) falls to {HfCell(land, map)}; jump link {(jump ? $"{jl} {jf} fl" : "none")}; marks in column: {Enumerable.Range(a.MinLevel, a.MaxLevel - a.MinLevel + 1).Count(k => ShaftMarks.Has(Levels.Cell(g, k, map), map))}");
                            }
                        }
                    }
                }
            }
            return lines;
        }

        // Per upper level: natural rock (and inner rock: all 8 neighbours rock too), bare tops and open air, with how many
        // of each are fogged. Returns the fogged fraction of inner rock over all upper levels.
        private static float HfFogStats(Map map, string when, List<string> into = null)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int allInner = 0, allInnerFog = 0;
            for (int L = 1; L <= a.MaxLevel; L++)
            {
                int rock = 0, rockFog = 0, inner = 0, innerFog = 0, tops = 0, topsFog = 0, air = 0, airFog = 0;
                for (int z = 0; z < a.groundZ; z++)
                {
                    for (int x = 0; x < map.Size.x; x++)
                    {
                        IntVec3 c = Levels.Cell(new IntVec3(x, 0, z), L, map);
                        if (!c.IsValid)
                        {
                            continue;
                        }
                        bool fog = map.fogGrid.IsFogged(c);
                        Building e = c.GetEdifice(map);
                        if (e is Mineable && e.def.building.isNaturalRock)
                        {
                            rock++;
                            rockFog += fog ? 1 : 0;
                            bool all = true;
                            for (int i = 0; i < 8 && all; i++)
                            {
                                IntVec3 n = c + GenAdj.AdjacentCells[i];
                                all = n.InBounds(map) && ProjectZ.SameBand(n, c, map) && n.GetEdifice(map) is Mineable;
                            }
                            if (all)
                            {
                                inner++;
                                innerFog += fog ? 1 : 0;
                            }
                        }
                        else if (c.GetTerrain(map) == PZDefOf.PZ_OpenAir)
                        {
                            air++;
                            airFog += fog ? 1 : 0;
                        }
                        else if (e == null && Floors.KindAt(c, map) == FloorKind.NaturalRock)
                        {
                            tops++;
                            topsFog += fog ? 1 : 0;
                        }
                    }
                }
                allInner += inner;
                allInnerFog += innerFog;
                if (rock + tops > 0)
                {
                    string line = $"fog {when} L{L}: rock {rockFog}/{rock} fogged, inner rock {innerFog}/{inner}, bare tops {topsFog}/{tops}, open air {airFog}/{air}";
                    Info(line);
                    into?.Add(line);
                }
            }
            return allInner == 0 ? 1f : (float)allInnerFog / allInner;
        }

        // Holes grouped: one per connected group of marks (shots, the walk check).
        private static List<IntVec3> HfGroups(Map map)
        {
            List<IntVec3> marks = ShaftMarks.For(map)?.cells.Where(c => Shaft.Is(c, map)).OrderBy(c => c.z).ThenBy(c => c.x).ToList() ?? new List<IntVec3>();
            List<IntVec3> heads = new List<IntVec3>();
            foreach (IntVec3 c in marks)
            {
                if (!heads.Any(h => h.InHorDistOf(c, 6f)))
                {
                    heads.Add(c);
                }
            }
            return heads;
        }

        private static bool StepHoleFog(Map map)
        {
            switch (stage)
            {
                case "hf_setup":
                    if (GenCommandLine.TryGetCommandLineArg("projectzsave", out hfSave))
                    {
                        HoleFog.Off = true;
                        stage = "hf_u_before";
                        pendingLoad = hfSave;
                        return true;
                    }
                    stage = "hf_f_setup";
                    return true;

                // ------------------------------------------------------------ the player's save
                case "hf_u_before":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    AnnexMapComponent a = ProjectZ.Annex(map);
                    Info($"loaded {hfSave} WITHOUT the rule: map {map.Size}, floors {a?.floors}, levels {a?.MinLevel}..{a?.MaxLevel}, tall mountains {Mountains.On(map)}; colonists {string.Join(", ", Colonists(map).Select(p => $"{p} {p.Position} L{Levels.LevelOf(p.Position, map)}"))}");
                    List<IntVec3> marks = ShaftMarks.For(map)?.cells.OrderBy(c => c.z).ThenBy(c => c.x).ToList() ?? new List<IntVec3>();
                    Info($"{marks.Count} shaft mark(s)");
                    HfFogStats(map, "save, rule off");
                    hfFogBefore = new bool[map.cellIndices.NumGridCells];
                    for (int i = 0; i < hfFogBefore.Length; i++)
                    {
                        hfFogBefore[i] = map.fogGrid.IsFogged(i);
                    }
                    foreach (IntVec3 c in marks)
                    {
                        Info("BEFORE " + HfDescribe(c, map, out _));
                    }
                    List<string> scan = HfScan(map, out int intoFog);
                    Info($"BEFORE scan: {intoFog} open-air cell(s) next to a seen walkable cell fall into fog");
                    foreach (string s in scan)
                    {
                        Info("  " + s);
                    }
                    hfHoles.Clear();
                    hfHoles.AddRange(HfGroups(map));
                    hfShotIdx = 0;
                    stage = "hf_u_shots_before";
                    return true;
                }

                case "hf_u_shots_before":
                case "hf_u_shots_after":
                {
                    bool before = stage == "hf_u_shots_before";
                    int hole = hfShotIdx / 2;
                    if (hole >= hfHoles.Count)
                    {
                        Find.UIRoot.screenshotMode.Active = false;
                        muPointer = IntVec3.Invalid;
                        if (before)
                        {
                            HoleFog.Off = false;
                            stage = "hf_u_after";
                            pendingLoad = hfSave;
                        }
                        else
                        {
                            SetHour(map, 12);
                            hfFogShot = 0;
                            stage = "hf_u_fogshots";
                        }
                        return true;
                    }
                    IntVec3 c = hfHoles[hole];
                    IntVec3 u = FloorRoof.Above(c, map);
                    IntVec3 land = Column.Landing(u, map, false, true, out _, out _);
                    bool top = hfShotIdx % 2 == 0;
                    int floor = top ? Levels.LevelOf(u, map) : Levels.LevelOf(land.IsValid ? land : c, map);
                    hfShotIdx++;
                    MuShot(map, $"hf_user{hole}_{(before ? "before" : "after")}_{(top ? "top" : "landing")}_L{floor}", floor, c, 12f, stage);
                    return true;
                }

                case "hf_u_after":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Info($"loaded {hfSave} WITH the rule (load-time pass): reveals {HoleFog.Reveals}");
                    HfFogStats(map, "save, rule on");
                    {
                        int changed = 0, far = 0;
                        List<IntVec3> holes = ShaftMarks.For(map)?.cells.Select(h => ProjectZ.GroundCellOf(h, map)).ToList() ?? new List<IntVec3>();
                        for (int i = 0; i < hfFogBefore.Length && i < map.cellIndices.NumGridCells; i++)
                        {
                            if (hfFogBefore[i] && !map.fogGrid.IsFogged(i))
                            {
                                changed++;
                                IntVec3 g = ProjectZ.GroundCellOf(map.cellIndices.IndexToCell(i), map);
                                far += holes.Any(h => h.InHorDistOf(g, 20f)) ? 0 : 1;
                            }
                        }
                        Result("U0 save: the load-time pass reveals only round the holes (nothing 20+ cells from a hole)", far == 0, $"{changed} cell(s) revealed by the load-time pass, {far} of them far from every hole");
                    }
                    List<IntVec3> marks = ShaftMarks.For(map)?.cells.OrderBy(c => c.z).ThenBy(c => c.x).ToList() ?? new List<IntVec3>();
                    foreach (IntVec3 c in marks.Where(c => Shaft.Is(c, map)))
                    {
                        string d = HfDescribe(c, map, out bool ok);
                        Result($"U1 save: the hole over {c} is revealed down to its landing (column, landing area, jumps seen)", ok, d);
                    }
                    hfHoles.Clear();
                    hfHoles.AddRange(HfGroups(map));
                    hfShotIdx = 0;
                    stage = "hf_u_shots_after";
                    return true;
                }

                case "hf_u_fogshots":
                {
                    int[] floors = { 0, 2, 4, 5, 6 };
                    if (hfFogShot >= floors.Length * 2)
                    {
                        IsoRimBridge.SetOption("fogLid", true);
                        Find.UIRoot.screenshotMode.Active = false;
                        muPointer = IntVec3.Invalid;
                        stage = "hf_u_walk";
                        return true;
                    }
                    bool lid = hfFogShot < floors.Length;
                    // (the second pass with IsoRim's fog lid off: the user's own option)
                    IsoRimBridge.SetOption("fogLid", lid);
                    IntVec3 mid = ProjectZ.GroundCellOf(hfHoles.FirstOrDefault(), map);
                    int f = floors[hfFogShot++ % floors.Length];
                    MuShot(map, $"hf_user_fog_L{f}{(lid ? "" : "_nolid")}", f, mid, 30f, "hf_u_fogshots");
                    return true;
                }

                case "hf_u_walk":
                {
                    // a drafted colonist next to the first hole is ordered onto its landing: it must jump down for real
                    hfShotIdx = 0;
                    IntVec3 c = hfHoles.FirstOrDefault();
                    if (!c.IsValid || hfHoles.Count == 0)
                    {
                        Finish();
                        return true;
                    }
                    IntVec3 u = FloorRoof.Above(c, map);
                    List<(IntVec3 from, IntVec3 land, int fallen)> jumps = HfJumps(u, map);
                    hfPawn = Colonists(map).OrderBy(p => p.thingIDNumber).FirstOrDefault(p => !p.Downed);
                    if (jumps.Count == 0 || hfPawn == null)
                    {
                        Result("U2 save: a drafted colonist ordered onto the landing below the hole jumps down", false, $"jumps {jumps.Count}, pawn {hfPawn}");
                        Finish();
                        return true;
                    }
                    hfFrom = jumps[0].from;
                    hfTarget = jumps[0].land;
                    MuStart(map, MuRect(new[] { c }, map, 3), false);
                    Teleport(hfPawn, hfFrom);
                    Draft(hfPawn, true);
                    bool canOrder = hfPawn.CanReach(hfTarget, PathEndMode.OnCell, Danger.Deadly);
                    Info($"U2 {hfPawn} at {hfFrom} ordered to {hfTarget}: reachable {canOrder}");
                    hfPawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, hfTarget), JobTag.DraftedOrder);
                    MuRun(60, 46000, () => hfPawn.Position == hfTarget, "hf_u_walked");
                    return true;
                }

                case "hf_u_walked":
                {
                    bool there = hfPawn.Position == hfTarget;
                    Result("U2 save: a drafted colonist ordered onto the landing below the hole jumps down", there, $"{hfPawn} at {hfPawn.Position} (L{Levels.LevelOf(hfPawn.Position, map)}), target {hfTarget}, job {hfPawn.CurJobDef?.defName}");
                    MuShot(map, "hf_user_walked", Levels.LevelOf(hfPawn.Position, map), hfPawn.Position, 10f, "hf_u_end");
                    return true;
                }

                // ------------------------------------------------------------ fixtures (fresh tall-mountain map)
                case "hf_f_setup":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    if (ProjectZ.Annex(map) == null || !Mountains.On(map))
                    {
                        Result("holefog: a tall-mountain map", false, "no annex or no tall mountains (-projectztallmountains)");
                        Finish();
                        return true;
                    }
                    float gen = HfFogStats(map, "after generation");
                    Result("F0 fog after generation: inner rock of the upper levels stays fogged", gen > 0.99f, $"{gen:P1} of inner upper-level rock fogged");
                    bool sites = HfSites(map);
                    Result("F sites: a level-2 top over rock with ground rock beside it (F1), ground rock under two levels of rock (F2), ground rock under a fogged level-1 plateau (F3)", sites, $"F1 {hfG1}, F2 {hfG2}, F3 {hfG3}");
                    if (!sites)
                    {
                        Finish();
                        return true;
                    }
                    FloorView.Set(0);
                    GameDataSaveLoader.SaveGame("pz_holefog");
                    stage = "hf_f_loaded";
                    pendingLoad = "pz_holefog";
                    return true;
                }

                case "hf_f_loaded":
                {
                    float load = HfFogStats(map, "after save + load");
                    Result("F0 fog after save + load: inner rock of the upper levels stays fogged", load > 0.99f, $"{load:P1} of inner upper-level rock fogged");
                    SetHour(map, 12);
                    hfShotIdx = 0;
                    stage = "hf_f_fogshots";
                    return true;
                }

                case "hf_f_fogshots":
                {
                    AnnexMapComponent a = ProjectZ.Annex(map);
                    if (hfShotIdx > a.MaxLevel)
                    {
                        Find.UIRoot.screenshotMode.Active = false;
                        muPointer = IntVec3.Invalid;
                        stage = "hf_f_next";
                        return true;
                    }
                    List<Pawn> cols = Colonists(map);
                    IntVec3 mid = new IntVec3((int)cols.Average(p => ProjectZ.GroundCellOf(p.Position, map).x), 0, (int)cols.Average(p => ProjectZ.GroundCellOf(p.Position, map).z));
                    int f = hfShotIdx++;
                    MuShot(map, $"hf_fog_gen_L{f}", f, mid, 30f, "hf_f_fogshots");
                    return true;
                }

                case "hf_f_next":
                    stage = "hf_f1";
                    pendingLoad = "pz_holefog";
                    return true;

                case "hf_u_end":
                    Find.UIRoot.screenshotMode.Active = false;
                    Finish();
                    return true;
            }
            return StepHoleFogFix(map);
        }
    }
}
