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
    // With -projectzsave=<copy of a player's save>: every shaft mark and its U described, and every cell on level 1+ that is
    // a natural floor with no rock on it, a roof over it and an open (no rock) cell below listed (U left closed).
    public static partial class TestRunner
    {
        private static readonly List<IntVec3> maCells = new List<IntVec3>();
        private static readonly List<IntVec3> maPocket = new List<IntVec3>();
        private static readonly Dictionary<IntVec3, string> maMinedBy = new Dictionary<IntVec3, string>();
        private static int maMined0, maOpened0;
        private static Pawn maUpper;

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

        private static bool MaAllGone(Map map) => maCells.All(c => MuUp(c, map).GetFirstMineable(map) == null);

        private static void MaCheck(Map map, string check)
        {
            List<string> bad = new List<string>();
            foreach (IntVec3 c in maCells)
            {
                IntVec3 u = MuUp(c, map), lid = MuUp(u, map);
                bool ok = u.GetFirstMineable(map) == null && u.GetTerrain(map) == PZDefOf.PZ_OpenAir && !u.Walkable(map) && Shaft.Is(c, map)
                    && map.roofGrid.RoofAt(c) == PZDefOf.PZ_StairwellRoof && lid.IsValid && (ProjectZ.HasFloor(lid, map) || lid.GetFirstMineable(map) != null)
                    && !MineSide.MarkAt(c, map, MineDir.FromBelow);
                if (!ok)
                {
                    bad.Add($"C {MaCell(c, map)} shaft {Shaft.Is(c, map)} mark {MineSide.MarkAt(c, map, MineDir.FromBelow)} | U {MaCell(u, map)} | lid {(lid.IsValid ? MaCell(lid, map) : "none")}");
                }
            }
            bool reach = maCells.Any(c => map.mapPawns.FreeColonistsSpawned.Any(p => p.CanReach(MuUp(c, map), PathEndMode.OnCell, Danger.Deadly)));
            Result($"{check} U open air (a covered shaft over C, not walkable), the lid kept, no mark left", bad.Count == 0 && !reach,
                $"{maCells.Count - bad.Count}/{maCells.Count} ok, a colonist can stand on a U: {reach}; mined from below {MineUp.MinedFromBelow - maMined0}, shafts opened {Shaft.Opened - maOpened0}; by {string.Join(", ", maMinedBy.Select(kv => $"{kv.Key}: {kv.Value}"))}{(bad.Count == 0 ? "" : "; BAD: " + string.Join(" || ", bad))}");
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
                    MuStart(map, CellRect.Empty, false);
                    List<IntVec3> dig = muA.Take(6).ToList();
                    foreach (IntVec3 c in dig)
                    {
                        map.designationManager.AddDesignation(new Designation(c, DesignationDefOf.Mine));
                    }
                    MuCrew(map, WorkTypeDefOf.Mining, 3);
                    MuRun(400, 61000, () => dig.All(c => c.GetEdifice(map) == null), "ma_dug");
                    return true;
                }

                case "ma_dug":
                {
                    List<IntVec3> dig = muA.Take(6).ToList();
                    Result("setup: colonists dug tunnel A with vanilla Mine", dig.All(c => c.GetEdifice(map) == null), $"{dig.Count(c => c.GetEdifice(map) == null)}/{dig.Count}, mined from below {MineUp.MinedFromBelow}");
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
                    MaEnd(map, "A5", null);
                    return true;

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
