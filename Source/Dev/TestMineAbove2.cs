using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode mineabove2: the Mine above command (PZ_MineAbove on a level-k cell C for the rock U right above it) with real
    // jobs, on the fixed-seed tall-mountain map of mode mineup (-projectztallmountains -projectzfloors=4 -projectzmapsize=250).
    // Setup: tunnel A (mode mineup's site: 6 ground cells under level-1 rock) dug with vanilla Mine, saved as pz_mineabove2;
    // every check reloads it, marks with the real designator, ticks under a fixed seed and checks after 600 more ticks: U
    // open air (not walkable, no pawn can stand there), C a covered shaft (stairwell roof), the lid above U kept, no mark
    // left, nothing outside the site changed:
    //   A1 one cell under plain rock (one miner)
    //   A2 a 4-cell drag (two miners); stairs placed in the shaft afterwards
    //   A3 a 4-cell drag whose rock is also reachable on level 1: a level-1 pocket beside it with a colonist in it (who
    //      also mines); stairs placed in the shaft afterwards
    //   A4 as A3, but the level-1 colonist alone can mine (the ground crew off work): the rock waits for the marks' side
    //   A5 as A4, the same rocks also marked with vanilla Mine on level 1: the level-1 colonist mines them with vanilla's
    //      job, and the boundary still opens (the user's bug on ad26742: U stayed a walkable floor)
    // Bare tops (Mine above with no rock above: the top opens by Remove roof's rule, Hole.CanOpen + Shaft.Open; tunnel B of
    // mode mineup: ground cells under a bare level-1 top; tunnel M: a line whose cells have rock or a bare top above; both
    // dug in the setup when the map has them):
    //   T1 one cell under a bare top: opens to the sky by the Mine above job (not Remove roof), the work time logged; then
    //      the two cells beside it: stairs placed under the three holes
    //   T2 one drag over cells with rock above and cells with a bare top above (tunnel M, else A and B together): each
    //      opens (rock: covered shaft; top: sky)
    //   T3 refusals: a top with something built on it ('Something is built above'), a top with a player floor on it
    //      ('Only bare natural ground opens'); a mark left under the built top opens nothing
    //   T4 a colonist standing on the top: steps aside on its level as the top opens (no fall)
    // With -projectzsave=<copy of a player's save>: every shaft mark and its U described, and every cell on level 1+ that is
    // a natural floor with no rock on it, a roof over it and an open (no rock) cell below listed (U left closed).
    public static partial class TestRunner
    {
        private static readonly List<IntVec3> maCells = new List<IntVec3>();
        private static readonly List<IntVec3> maPocket = new List<IntVec3>();
        private static readonly Dictionary<IntVec3, string> maMinedBy = new Dictionary<IntVec3, string>();
        private static int maMined0, maOpened0, maTops0, maHoles0, maStep0, maWork0, maWorkTicks;
        private static Pawn maUpper, maStander;
        private static readonly HashSet<IntVec3> maTops = new HashSet<IntVec3>();
        private static readonly List<IntVec3> maM = new List<IntVec3>();   // tunnel M (rock and bare tops above), empty: none
        private static bool maSiteB;
        private static IntVec3 maStandAt;
        private static string maWorker;

        private static string MaCell(IntVec3 c, Map map)
        {
            return $"{c} L{ProjectZ.FloorOf(c, map)} {c.GetTerrain(map)?.defName} roof {MuRoof(c, map)} edifice {c.GetEdifice(map)?.def.defName ?? "-"} walkable {c.Walkable(map)}";
        }

        // Every cell's rock under watch: who is working on it (job def, pawn level) at each tick, kept until it is gone.
        private static void MaWatch(Map map)
        {
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                Thing t = p.CurJob?.targetA.Thing;
                if (t is Mineable && maCells.Any(c => MuUp(c, map) == t.Position))
                {
                    maMinedBy[t.Position] = $"{p.LabelShort} {p.CurJobDef?.defName} from L{ProjectZ.FloorOf(p.Position, map)}";
                }
            }
        }

        private static void MaOrder(Map map, string check, IEnumerable<IntVec3> cells)
        {
            maCells.Clear();
            maCells.AddRange(cells);
            maMinedBy.Clear();
            maMined0 = MineUp.MinedFromBelow;
            maOpened0 = Shaft.Opened;
            maTops0 = MineUp.TopsOpened;
            maHoles0 = Hole.Opened;
            maWork0 = -1;
            maWorkTicks = -1;
            maTops.Clear();
            foreach (IntVec3 c in maCells.Where(c => MuUp(c, map).GetFirstMineable(map) == null))
            {
                maTops.Add(c);
            }
            Designator_MineAbove d = new Designator_MineAbove();
            List<string> why = new List<string>();
            foreach (IntVec3 c in maCells)
            {
                AcceptanceReport r = d.CanDesignateCell(c);
                why.Add($"{c}: {(r.Accepted ? "ok" : r.Reason)}");
            }
            d.DesignateMultiCell(maCells);
            int marked = maCells.Count(c => MineSide.MarkAt(c, map, MineDir.FromBelow));
            Result($"{check} Mine above accepted on every cell", marked == maCells.Count, $"marked {marked}/{maCells.Count}; {string.Join("; ", why)}");
        }

        private static bool MaAllGone(Map map) => maCells.All(c => maTops.Contains(c) ? MuUp(c, map).GetTerrain(map) == PZDefOf.PZ_OpenAir : MuUp(c, map).GetFirstMineable(map) == null);

        // Every tick (bare tops): the first tick a colonist works a top-opening job, and the ticks until a top is open.
        private static void MaTopWatch(Map map)
        {
            MaWatch(map);
            Pawn w = maWork0 < 0 ? map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.CurJobDef == PZMineDefOf.PZ_MineFromBelow && !p.CurJob.targetA.HasThing && p.jobs.curDriver.CurToilIndex >= 1) : null;
            if (w != null)
            {
                maWork0 = Find.TickManager.TicksGame;
                maWorker = $"{w.LabelShort}, mining skill {w.skills?.GetSkill(SkillDefOf.Mining)?.Level}, mining speed {w.GetStatValue(StatDefOf.MiningSpeed):0.00}";
            }
            if (maWork0 >= 0 && maWorkTicks < 0 && MineUp.TopsOpened > maTops0)
            {
                maWorkTicks = Find.TickManager.TicksGame - maWork0;
            }
        }

        // Rock above: a covered shaft, the lid kept; a bare top: C's roof by the shaft rule (U roofed: stairwell roof, else
        // sky), opened by the Mine above job, not by Remove roof.
        private static void MaCheck(Map map, string check)
        {
            List<string> bad = new List<string>();
            foreach (IntVec3 c in maCells)
            {
                IntVec3 u = MuUp(c, map), lid = MuUp(u, map);
                bool top = maTops.Contains(c);
                RoofDef want = map.roofGrid.Roofed(u) ? PZDefOf.PZ_StairwellRoof : null;
                bool ok = u.GetFirstMineable(map) == null && u.GetTerrain(map) == PZDefOf.PZ_OpenAir && !u.Walkable(map) && Shaft.Is(c, map) && map.roofGrid.RoofAt(c) == want
                    && (top || want == PZDefOf.PZ_StairwellRoof && lid.IsValid && (ProjectZ.HasFloor(lid, map) || lid.GetFirstMineable(map) != null))
                    && !MineSide.MarkAt(c, map, MineDir.FromBelow);
                if (!ok)
                {
                    bad.Add($"{(top ? "top" : "rock")} C {MaCell(c, map)} shaft {Shaft.Is(c, map)} mark {MineSide.MarkAt(c, map, MineDir.FromBelow)} | U {MaCell(u, map)} | lid {(lid.IsValid ? MaCell(lid, map) : "none")}");
                }
            }
            bool reach = maCells.Any(c => map.mapPawns.FreeColonistsSpawned.Any(p => p.CanReach(MuUp(c, map), PathEndMode.OnCell, Danger.Deadly)));
            int tops = maTops.Count, rocks = maCells.Count - tops;
            string what = tops == 0 ? "a covered shaft over C" : rocks == 0 ? "a hole over C" : "rock: a covered shaft, top: a hole";
            Result($"{check} U open air ({what}, not walkable){(rocks > 0 ? ", the lid kept" : "")}, no mark left", bad.Count == 0 && !reach,
                $"{maCells.Count - bad.Count}/{maCells.Count} ok ({rocks} rock, {tops} top), a colonist can stand on a U: {reach}; mined from below {MineUp.MinedFromBelow - maMined0}, tops opened {MineUp.TopsOpened - maTops0}, shafts opened {Shaft.Opened - maOpened0}; by {string.Join(", ", maMinedBy.Select(kv => $"{kv.Key}: {kv.Value}"))}{(bad.Count == 0 ? "" : "; BAD: " + string.Join(" || ", bad))}");
            if (tops > 0)
            {
                Result($"{check} every bare top opened by the Mine above job (not Remove roof)", MineUp.TopsOpened - maTops0 == tops && Hole.Opened == maHoles0,
                    $"tops opened {MineUp.TopsOpened - maTops0} of {tops}, Remove roof holes {Hole.Opened - maHoles0}");
            }
        }

        // Tunnel M: open ground s, then 5 rock cells (thick roof, rock side walls) with rock (natural top 2+) or a bare level-1
        // top (natural top 1) above, at least one of each, and a rock front wall; 20+ cells from tunnels A and B.
        private static bool MaSiteM(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            maM.Clear();
            foreach (IntVec3 s in Near(new IntVec3(map.Size.x / 2, 0, a.groundZ / 2), 110, map))   // (a fixed point: the same site every run)
            {
                if (s.x < 6 || s.z < 6 || s.x >= map.Size.x - 6 || s.z >= a.groundZ - 6 || !MuOpen(s, map) || s.InHorDistOf(muSA, 20f) || maSiteB && s.InHorDistOf(muSB, 20f)
                    || !map.reachability.CanReachMapEdge(s, TraverseParms.For(TraverseMode.PassDoors)))
                {
                    continue;
                }
                foreach (IntVec3 d in GenAdj.CardinalDirections)
                {
                    IntVec3 side = d.RotatedBy(RotationDirection.Clockwise);
                    bool ok = !map.fogGrid.IsFogged(s + d);
                    int tops = 0, rocks = 0;
                    for (int i = 1; i <= 6 && ok; i++)
                    {
                        IntVec3 t = s + d * i;
                        ok = MuRock(t, map) && MuThick(t, map);
                        if (!ok || i == 6)
                        {
                            continue;
                        }
                        ok = MuRock(t + side, map) && MuRock(t - side, map);
                        int nt = Column.NaturalTop(t, map);
                        IntVec3 u = MuUp(t, map);
                        if (ok && nt == 1 && TmTop(u, map))
                        {
                            tops++;
                        }
                        else if (ok && nt >= 2 && MuRock(u, map))
                        {
                            rocks++;
                        }
                        else
                        {
                            ok = false;
                        }
                    }
                    if (!ok || tops == 0 || rocks == 0)
                    {
                        continue;
                    }
                    for (int i = 1; i <= 5; i++)
                    {
                        maM.Add(s + d * i);
                    }
                    Info($"tunnel M: {s} dir {d}, above: {string.Join(" ", maM.Select(t => MuRock(MuUp(t, map), map) ? "rock" : "top"))}");
                    return true;
                }
            }
            return false;
        }

        private static void MaStairs(Map map, string check, IntVec3 center)
        {
            AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, center, muRotA, map, stuffDef: ThingDefOf.WoodLog);
            Result($"{check} stairs in the shaft: placement accepted", ok.Accepted, $"at {center} {muRotA}: '{(ok.Accepted ? "ok" : ok.Reason)}'");
        }

        // A level-1 pocket beside the rock above tunnel cells (side +1, +2), seen, a colonist in it (Mining only).
        private static bool MaPocket(Map map, IEnumerable<IntVec3> along)
        {
            maPocket.Clear();
            foreach (IntVec3 t in along)
            {
                maPocket.Add(MuUp(t + muSideA, map));
                maPocket.Add(MuUp(t + muSideA * 2, map));
            }
            if (!maPocket.All(c => c.IsValid && MuRock(c, map) && ProjectZ.HasFloor(c, map)))
            {
                return false;
            }
            HfCarve(map, maPocket, true);
            return true;
        }

        private static void MaEnd(Map map, string check, string next)
        {
            MuOutside(map, check);
            maUpper = null;
            FloorView.Set(0);
            Find.DesignatorManager.Deselect();
            if (next == null)
            {
                Finish();
                return;
            }
            stage = next;
            pendingLoad = "pz_mineabove2";
        }

        private static void MaUserSave(Map map, string name)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            Info($"loaded {name}: map {map.Size}, floors {a?.floors}, levels {a?.MinLevel}..{a?.MaxLevel}, tall mountains {Mountains.On(map)}");
            List<IntVec3> marks = ShaftMarks.For(map)?.cells.OrderBy(c => c.z).ThenBy(c => c.x).ToList() ?? new List<IntVec3>();
            foreach (IntVec3 c in marks)
            {
                IntVec3 u = MuUp(c, map);
                Info($"shaft C {MaCell(c, map)} is {Shaft.Is(c, map)} | U {(u.IsValid ? MaCell(u, map) : "none")}");
            }
            int vert = map.designationManager.AllDesignations.Count(d => d.def == PZMineDefOf.PZ_MineAbove || d.def == PZMineDefOf.PZ_MineBelow);
            Info($"Mine above / below marks in the save: {vert}");
            int closed = 0;
            foreach (IntVec3 c in map.AllCells)
            {
                if (ProjectZ.FloorOf(c, map) < 1 || !Floors.UnbuiltNatural(c, map) || c.GetFirstMineable(map) != null || !map.roofGrid.Roofed(c))
                {
                    continue;
                }
                IntVec3 b = FloorRoof.Below(c, map);
                if (!b.IsValid || b.GetFirstMineable(map) != null)
                {
                    continue;
                }
                closed++;
                if (closed <= 40)
                {
                    Info($"roofed natural floor with no rock on it over an open cell: U {MaCell(c, map)} | below {MaCell(b, map)}");
                }
            }
            Result("user save: no roofed natural floor without rock over an open cell (a U left closed)", closed == 0, $"{closed} cell(s)");
        }

        // The setup's tunnels: A, and B and M when the map has them.
        private static List<IntVec3> MaDig() => muA.Take(6).Concat(maSiteB ? muB.Take(4) : Enumerable.Empty<IntVec3>()).Concat(maM).ToList();

        private static bool StepMineAbove2(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            switch (stage)
            {
                case "ma_setup":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    if (GenCommandLine.TryGetCommandLineArg("projectzsave", out string save))
                    {
                        stage = "ma_user";
                        pendingLoad = save;
                        return true;
                    }
                    if (a == null || !Mountains.On(map))
                    {
                        Result("mineabove2: a tall-mountain map", false, "no annex or no tall mountains (-projectztallmountains)");
                        Finish();
                        return true;
                    }
                    bool siteA = MuSiteA(map);
                    Result("mineabove2 site: tunnel A (rock above, top at 2+)", siteA, siteA ? $"{muSA} dir {muDA} side {muSideA}" : "none");
                    if (!siteA)
                    {
                        Finish();
                        return true;
                    }
                    MuClear(map, Enumerable.Range(0, 5).Select(i => muSA - muDA * i));
                    muSB = IntVec3.Invalid;
                    maSiteB = MuSiteB(map);
                    Result("mineabove2 site: tunnel B (a bare level-1 top above)", maSiteB, maSiteB ? $"{muSB} dir {muDB}" : "none");
                    bool siteM = MaSiteM(map);
                    Info($"mineabove2 site: tunnel M (rock and bare tops above): {(siteM ? "found" : "none, T2 uses tunnels A and B")}");
                    if (maSiteB)
                    {
                        MuClear(map, new[] { muSB });
                    }
                    if (siteM)
                    {
                        MuClear(map, new[] { maM[0] * 2 - maM[1] });
                    }
                    MuStart(map, CellRect.Empty, false);
                    List<IntVec3> dig = MaDig();
                    foreach (IntVec3 c in dig)
                    {
                        map.designationManager.AddDesignation(new Designation(c, DesignationDefOf.Mine));
                    }
                    MuCrew(map, WorkTypeDefOf.Mining, 3);
                    MuRun(800, 61000, () => dig.All(c => c.GetEdifice(map) == null), "ma_dug");
                    return true;
                }

                case "ma_dug":
                {
                    List<IntVec3> dig = MaDig();
                    Result("setup: colonists dug the tunnels (A, B, M) with vanilla Mine", dig.All(c => c.GetEdifice(map) == null), $"{dig.Count(c => c.GetEdifice(map) == null)}/{dig.Count}, mined from below {MineUp.MinedFromBelow}");
                    foreach (Pawn p in Colonists(map))
                    {
                        Draft(p, false);
                        Teleport(p, muSA - muDA);
                    }
                    FgWork(map, new Pawn[0], WorkTypeDefOf.Mining);
                    FloorView.Set(0);
                    GameDataSaveLoader.SaveGame("pz_mineabove2");
                    stage = "ma_a1";
                    pendingLoad = "pz_mineabove2";
                    return true;
                }

                // ------------------------------------------------------------ A1: one cell, plain rock above
                case "ma_a1":
                    MuStart(map, MuRect(muA, map, 4), false);
                    MaOrder(map, "A1", new[] { muA[2] });
                    MuCrew(map, WorkTypeDefOf.Mining, 1);
                    MuRun(300, 62000, () => MaAllGone(map), "ma_a1_more", () => MaWatch(map));
                    return true;

                case "ma_a1_more":
                    MuRun(2, 62100, () => false, "ma_a1_done");
                    return true;

                case "ma_a1_done":
                    MaCheck(map, "A1");
                    MaEnd(map, "A1", "ma_a2");
                    return true;

                // ------------------------------------------------------------ A2: a 4-cell drag
                case "ma_a2":
                    MuStart(map, MuRect(muA, map, 4), false);
                    MaOrder(map, "A2", muA.Take(4));
                    MuCrew(map, WorkTypeDefOf.Mining, 2);
                    MuRun(500, 63000, () => MaAllGone(map), "ma_a2_more", () => MaWatch(map));
                    return true;

                case "ma_a2_more":
                    MuRun(2, 63100, () => false, "ma_a2_done");
                    return true;

                case "ma_a2_done":
                    MaCheck(map, "A2");
                    MaStairs(map, "A2", muA[1]);
                    MaEnd(map, "A2", "ma_a3");
                    return true;

                // ------------------------------------------------------------ A3/A4: the rock also reachable on level 1
                case "ma_a3":
                case "ma_a4":
                {
                    bool a4 = stage == "ma_a4";
                    string check = a4 ? "A4" : "A3";
                    MuStart(map, MuRect(muA, map, 4), false);
                    List<IntVec3> cells = muA.Skip(2).Take(4).ToList();
                    bool pocket = MaPocket(map, cells);
                    List<Pawn> crew = MuCrew(map, WorkTypeDefOf.Mining, 2);
                    maUpper = crew.LastOrDefault();
                    if (!pocket || maUpper == null)
                    {
                        Result($"{check} fixture: a level-1 pocket beside the rock above, a colonist in it", false, $"pocket {pocket}, crew {crew.Count}");
                        MaEnd(map, check, a4 ? "ma_a5" : "ma_a4");
                        return true;
                    }
                    Teleport(maUpper, maPocket[0]);
                    if (a4)
                    {
                        MdWork(map, new List<Pawn> { maUpper }, WorkTypeDefOf.Mining);   // (the ground crew off work: drafted pawns undraft by themselves)
                    }
                    ReachFog.Run(map);
                    MuStart(map, MuRect(muA, map, 4), false);
                    bool reach = cells.All(c => maUpper.CanReach(MuUp(c, map), PathEndMode.Touch, Danger.Deadly));
                    Info($"{check} pocket {string.Join(" ", maPocket)}; {maUpper} on {maUpper.Position} L{ProjectZ.FloorOf(maUpper.Position, map)} touches every U on level 1: {reach}");
                    MaOrder(map, check, cells);
                    MuRun(a4 ? 60 : 500, a4 ? 65000 : 64000, () => MaAllGone(map), a4 ? "ma_a4_more" : "ma_a3_more", () => MaWatch(map));
                    return true;
                }

                case "ma_a3_more":
                case "ma_a4_more":
                    MuRun(2, 64100, () => false, stage == "ma_a4_more" ? "ma_a4_done" : "ma_a3_done");
                    return true;

                case "ma_a3_done":
                case "ma_a4_done":
                {
                    bool a4 = stage == "ma_a4_done";
                    string check = a4 ? "A4" : "A3";
                    if (a4 && !MaAllGone(map))
                    {
                        // the level-1 colonist alone: it cannot reach the marked cells below, so nobody may take the rock
                        Result("A4 the level-1 colonist alone: the rock is not taken from level 1 (it waits for the marks' side)",
                            maCells.All(c => MuUp(c, map).GetFirstMineable(map) != null && MineSide.MarkAt(c, map, MineDir.FromBelow)),
                            $"left {maCells.Count(c => MuUp(c, map).GetFirstMineable(map) != null)}/{maCells.Count}; by {string.Join(", ", maMinedBy.Select(kv => $"{kv.Key}: {kv.Value}"))}");
                    }
                    else
                    {
                        MaCheck(map, check);
                        if (!a4)
                        {
                            MaStairs(map, check, muA[3]);
                        }
                    }
                    MaEnd(map, check, a4 ? "ma_a5" : "ma_a4");
                    return true;
                }

                // ------------------------------------------------------------ A5: the marked rock also carries vanilla Mine
                case "ma_a5":
                {
                    MuStart(map, MuRect(muA, map, 4), false);
                    List<IntVec3> cells = muA.Skip(2).Take(4).ToList();
                    bool pocket = MaPocket(map, cells);
                    List<Pawn> crew = MuCrew(map, WorkTypeDefOf.Mining, 2);
                    maUpper = crew.LastOrDefault();
                    if (!pocket || maUpper == null)
                    {
                        Result("A5 fixture: a level-1 pocket beside the rock above, a colonist in it", false, $"pocket {pocket}, crew {crew.Count}");
                        MaEnd(map, "A5", null);
                        return true;
                    }
                    Teleport(maUpper, maPocket[0]);
                    MdWork(map, new List<Pawn> { maUpper }, WorkTypeDefOf.Mining);
                    ReachFog.Run(map);
                    MuStart(map, MuRect(muA, map, 4), false);
                    MaOrder(map, "A5", cells);
                    // the same rocks marked with vanilla Mine on their own level too (an older order, a vein flood, ...)
                    FloorView.Set(1);
                    new Designator_Mine().DesignateMultiCell(cells.Select(c => MuUp(c, map)));
                    FloorView.Set(0);
                    int vanilla = cells.Count(c => map.designationManager.DesignationAt(MuUp(c, map), DesignationDefOf.Mine) != null);
                    Info($"A5 vanilla Mine on the rocks above too: {vanilla}/{cells.Count}; {maUpper} on {maUpper.Position} L{ProjectZ.FloorOf(maUpper.Position, map)} (the only miner)");
                    MuRun(300, 66000, () => MaAllGone(map), "ma_a5_more", () => MaWatch(map));
                    return true;
                }

                case "ma_a5_more":
                    MuRun(2, 66100, () => false, "ma_a5_done");
                    return true;

                case "ma_a5_done":
                    MaCheck(map, "A5");
                    MaEnd(map, "A5", maSiteB ? "ma_t1" : null);
                    return true;

                // ------------------------------------------------------------ T1: one cell under a bare top, then stairs
                case "ma_t1":
                    MuStart(map, MuRect(muB, map, 4), false);
                    MaOrder(map, "T1", new[] { muB[1] });
                    MuCrew(map, WorkTypeDefOf.Mining, 1);
                    MuRun(300, 67000, () => MaAllGone(map), "ma_t1_more", () => MaTopWatch(map));
                    return true;

                case "ma_t1_more":
                    MuRun(2, 67100, () => false, "ma_t1_done");
                    return true;

                case "ma_t1_done":
                {
                    MaCheck(map, "T1");
                    IntVec3 c = muB[1], u = MuUp(c, map);
                    Room room = c.GetRoom(map);
                    Result("T1 the bare top opened to the sky (no roof over C or U)", map.roofGrid.RoofAt(c) == null && !map.roofGrid.Roofed(u),
                        $"C roof {MuRoof(c, map)}, U roof {MuRoof(u, map)}, room open roofs {room?.OpenRoofCount}");
                    Info($"T1 work time: {maWorkTicks} ticks from the first work tick to the open top by {maWorker} (65 work at 1.7 x mining speed a tick; speed 1: about {JobDriver_MineVertical.TopWork / 1.7f:0} ticks, as vanilla Remove roof at construction speed 1)");
                    // the two cells beside it, then stairs under the three holes
                    MaOrder(map, "T1b", new[] { muB[0], muB[2] });
                    MuRun(300, 67200, () => MaAllGone(map), "ma_t1b_more", () => MaTopWatch(map));
                    return true;
                }

                case "ma_t1b_more":
                    MuRun(2, 67300, () => false, "ma_t1b_done");
                    return true;

                case "ma_t1b_done":
                {
                    MaCheck(map, "T1b");
                    AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, muB[1], muRotB, map, stuffDef: ThingDefOf.WoodLog);
                    Result("T1 stairs under the three holes: placement accepted", ok.Accepted, $"at {muB[1]} {muRotB}: '{(ok.Accepted ? "ok" : ok.Reason)}'");
                    MaEnd(map, "T1", "ma_t2");
                    return true;
                }

                // ------------------------------------------------------------ T2: one drag, rock and bare tops above
                case "ma_t2":
                {
                    List<IntVec3> cells = maM.Count > 0 ? maM.ToList() : muA.Take(2).Concat(muB.Take(3)).ToList();
                    MuStart(map, MuRect(cells, map, 4), false);
                    MaOrder(map, "T2", cells);
                    Result("T2 fixture: one drag over rock-above and top-above cells", maTops.Count > 0 && maTops.Count < maCells.Count,
                        $"{(maM.Count > 0 ? "tunnel M" : "tunnels A and B")}: {maCells.Count - maTops.Count} rock, {maTops.Count} top");
                    MuCrew(map, WorkTypeDefOf.Mining, 2);
                    MuRun(600, 68000, () => MaAllGone(map), "ma_t2_more", () => MaTopWatch(map));
                    return true;
                }

                case "ma_t2_more":
                    MuRun(2, 68100, () => false, "ma_t2_done");
                    return true;

                case "ma_t2_done":
                    MaCheck(map, "T2");
                    MaEnd(map, "T2", "ma_t3");
                    return true;

                // ------------------------------------------------------------ T3: refusals by Remove roof's rule
                case "ma_t3":
                {
                    MuStart(map, MuRect(muB, map, 4), false);
                    Designator_MineAbove d = new Designator_MineAbove();
                    IntVec3 c1 = muB[1], c2 = muB[2];
                    Thing stool = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Stool"), ThingDefOf.WoodLog);
                    stool.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(stool, MuUp(c1, map), map);
                    map.terrainGrid.SetTerrain(MuUp(c2, map), TerrainDefOf.WoodPlankFloor);
                    AcceptanceReport built = d.CanDesignateCell(c1), floor = d.CanDesignateCell(c2), bare = d.CanDesignateCell(muB[0]);
                    Result("T3 Mine above under a bare top: accepted (the control)", bare.Accepted, $"{muB[0]}: '{(bare.Accepted ? "ok" : bare.Reason)}'");
                    Result("T3 Mine above under a top with something built on it: 'Something is built above'", !built.Accepted && built.Reason == "PZ_BuiltAbove".Translate(), $"{c1}: '{built.Reason}'");
                    Result("T3 Mine above under a top with a player floor on it: 'Only bare natural ground opens'", !floor.Accepted && floor.Reason == "PZ_NotNaturalGround".Translate(),
                        $"{c2} ({MuUp(c2, map).GetTerrain(map)?.defName}): '{floor.Reason}'");
                    // a mark from before the stool was built: nothing opens
                    map.designationManager.AddDesignation(new Designation(c1, PZMineDefOf.PZ_MineAbove));
                    maTops0 = MineUp.TopsOpened;
                    MuCrew(map, WorkTypeDefOf.Mining, 1);
                    MuRun(10, 69000, () => false, "ma_t3_done");
                    return true;
                }

                case "ma_t3_done":
                {
                    IntVec3 u = MuUp(muB[1], map);
                    Result("T3 a mark left under the built top: nothing opens, no job on it",
                        u.GetTerrain(map) != PZDefOf.PZ_OpenAir && MineUp.TopsOpened == maTops0 && map.mapPawns.FreeColonistsSpawned.All(p => p.CurJobDef != PZMineDefOf.PZ_MineFromBelow),
                        $"U {MaCell(u, map)}, tops opened {MineUp.TopsOpened - maTops0}");
                    MaEnd(map, "T3", "ma_t4");
                    return true;
                }

                // ------------------------------------------------------------ T4: a colonist on the top steps aside
                case "ma_t4":
                {
                    MuStart(map, MuRect(muB, map, 4), false);
                    IntVec3 c = muB[1], u = MuUp(c, map);
                    List<Pawn> crew = MuCrew(map, WorkTypeDefOf.Mining, 1);
                    maStander = Colonists(map).Where(p => !crew.Contains(p) && !p.Downed).OrderBy(p => p.thingIDNumber).FirstOrDefault();
                    if (maStander == null)
                    {
                        Result("T4 fixture: a second colonist", false, "none");
                        MaEnd(map, "T4", null);
                        return true;
                    }
                    Teleport(maStander, u);
                    Draft(maStander, true);
                    maStandAt = u;
                    maStep0 = Shaft.SteppedAside;
                    MaOrder(map, "T4", new[] { c });
                    MuRun(300, 70000, () => MaAllGone(map), "ma_t4_more", () => MaTopWatch(map));
                    return true;
                }

                case "ma_t4_more":
                    MuRun(2, 70100, () => false, "ma_t4_done");
                    return true;

                case "ma_t4_done":
                {
                    MaCheck(map, "T4");
                    IntVec3 at = maStander.Position;
                    Result("T4 the colonist on the top stepped aside on its level (no fall)",
                        maStander.Spawned && !maStander.Downed && at != maStandAt && ProjectZ.FloorOf(at, map) == ProjectZ.FloorOf(maStandAt, map) && at.AdjacentTo8WayOrInside(maStandAt) && Shaft.SteppedAside - maStep0 == 1,
                        $"{maStander} {maStandAt} -> {at} L{ProjectZ.FloorOf(at, map)}, stepped aside {Shaft.SteppedAside - maStep0}");
                    maStander = null;
                    MaEnd(map, "T4", null);
                    return true;
                }

                // ------------------------------------------------------------ the player's save
                case "ma_user":
                    MaUserSave(map, GenCommandLine.TryGetCommandLineArg("projectzsave", out string s) ? s : "?");
                    Finish();
                    return true;
            }
            return false;
        }
    }
}
