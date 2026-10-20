using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode stepaside: pawns on a top that opens (Shaft.Open) step to a free neighbour on the same level; items fall; with no
    // free neighbour the pawn falls (user 2026-10-19). The same fixed-seed map and arguments as minedown; the setup finds the
    // plateau block P (MdSites), puts the miners on it and saves pz_stepaside; every check reloads it, uses the real Mine
    // below designator, work giver and job under its own fixed seed:
    //   S1 three colonists mine below five neighbouring cells (a plus) at once: all open, nobody falls
    //   S2 a drafted colonist walking across the top at the moment it opens: steps aside, no fall, reaches its goal
    //   S3 an animal on the top: steps aside
    //   S4 wood on the top falls to the pit floor
    //   S5 the top ringed by walls and a cell about to open: the colonist on it falls (as before)
    //   S6 neighbouring tops mined below, the middle one last: no work spot on a diagonal cell that cannot touch its top
    //      (both corner cells open air), no job loop ("started 10 jobs in one tick"), all open
    public static partial class TestRunner
    {
        private static readonly List<int> saCrewIds = new List<int>();
        private static int saStep0, saPhase;
        private static bool saFell, saOpened;
        private static IntVec3 saU, saGoal, saFrom, saAfter;
        private static Pawn saP;
        private static string saNote;
        private static string saBadSpot;
        private static int saErr0;

        private static readonly AccessTools.FieldRef<JobDriver_Mine, int> saTicks = AccessTools.FieldRefAccess<JobDriver_Mine, int>("ticksToPickHit");

        private static List<Pawn> SaCrew(Map map) => saCrewIds.Select(id => map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == id)).Where(p => p != null).ToList();

        private static Pawn SaOther(Map map) => Colonists(map).Where(p => !saCrewIds.Contains(p.thingIDNumber)).OrderBy(p => p.thingIDNumber).FirstOrDefault() ?? SaCrew(map).LastOrDefault();

        private static IntVec3 SaTop(int dx, int dz, Map map) => L1(mdG0 + new IntVec3(dx, 0, dz), map);

        private static bool SaOpen(IntVec3 top, Map map) => top.GetTerrain(map) == PZDefOf.PZ_OpenAir;

        // One miner (crew[0]) north of the top U, its rock at 1 hit point (fixture), Mine below on U.
        private static Pawn SaOneMiner(Map map, IntVec3 u, int minerDz)
        {
            Pawn m = SaCrew(map).First();
            Teleport(m, SaTop(0, minerDz, map));
            MdWork(map, new List<Pawn> { m }, WorkTypeDefOf.Mining);
            Thing rock = ProjectZ.GroundCellOf(u, map).GetFirstMineable(map);
            if (rock != null)
            {
                rock.HitPoints = 1;
            }
            return m;
        }

        // Every tick: a crew job from above whose work spot (C) cannot touch its top (B) by the game's touch rule.
        private static void SaSpotWatch()
        {
            Map map = Find.CurrentMap;
            foreach (Pawn p in SaCrew(map))
            {
                Job j = p.CurJob;
                if (saBadSpot == null && j != null && j.def == PZMineDefOf.PZ_MineFromAbove && j.targetC.IsValid
                    && !ReachabilityImmediate.CanReachImmediate(j.targetC.Cell, j.targetB, map, PathEndMode.Touch, p))
                {
                    saBadSpot = $"{p} spot {j.targetC.Cell} for top {j.targetB.Cell} tick {Find.TickManager.TicksGame}";
                }
            }
        }

        private static void SaMark(Map map, IEnumerable<IntVec3> tops)
        {
            new Designator_MineBelow().DesignateMultiCell(tops);
        }

        private static void SaStart(Map map)
        {
            MdStart(map, MuRect(new[] { mdG0 }, map, 5), false);
            saStep0 = Shaft.SteppedAside;
            saFell = saOpened = false;
            saPhase = 0;
            saNote = null;
            saFrom = saAfter = IntVec3.Invalid;
        }

        private static void SaEnd(Map map, string check, string next)
        {
            MuOutside(map, check);
            if (next == null)
            {
                Finish();
                return;
            }
            stage = next;
            pendingLoad = "pz_stepaside";
        }

        private static bool StepStepAside(Map map)
        {
            switch (stage)
            {
                case "sa_setup":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    if (ProjectZ.Annex(map) == null || !Mountains.On(map))
                    {
                        Result("stepaside: a tall-mountain map", false, "no annex or no tall mountains (-projectztallmountains)");
                        Finish();
                        return true;
                    }
                    MdSites(map);
                    if (!mdG0.IsValid)
                    {
                        Result("stepaside site: a 9x7 ground rock block under bare level-1 tops (P)", false, "none");
                        Finish();
                        return true;
                    }
                    List<Pawn> all = Colonists(map).Where(p => !p.Downed).OrderBy(p => p.thingIDNumber).ToList();
                    List<Pawn> crew = all.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining)).Take(3).ToList();
                    saCrewIds.Clear();
                    for (int i = 0; i < all.Count; i++)
                    {
                        Teleport(all[i], SaTop(-3 + i, -3, map));
                        if (crew.Contains(all[i]))
                        {
                            saCrewIds.Add(all[i].thingIDNumber);
                        }
                    }
                    ReachFog.Run(map);
                    Info($"setup: P {mdG0}; crew {string.Join(", ", crew.Select(p => $"{p} at {p.Position}"))}; others {string.Join(", ", all.Except(crew).Select(p => p.ToString()))}");
                    SetHour(map, 10);
                    FloorView.Set(0);
                    GameDataSaveLoader.SaveGame("pz_stepaside");
                    stage = "sa_s1";
                    pendingLoad = "pz_stepaside";
                    return true;
                }

                // ------------------------------------------------------------ S1: three miners, five neighbouring tops
                case "sa_s1":
                {
                    SaStart(map);
                    List<IntVec3> tops = new[] { (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1) }.Select(o => SaTop(o.Item1, o.Item2, map)).ToList();
                    SaMark(map, tops);
                    List<Pawn> crew = SaCrew(map);
                    MdWork(map, crew, WorkTypeDefOf.Mining);
                    int marked = tops.Count(t => MineSide.MarkAt(t, map, MineDir.FromAbove));
                    Info($"S1: {marked}/5 tops marked Mine below, crew {crew.Count}");
                    MuRun(400, 51000, () => tops.All(t => SaOpen(t, map)), "sa_s1_done", () =>
                    {
                        foreach (Pawn p in SaCrew(map))
                        {
                            if (!saFell && ProjectZ.FloorOf(p.Position, map) == 0)
                            {
                                saFell = true;
                                saNote = $"{p} at {p.Position} tick {Find.TickManager.TicksGame}";
                            }
                        }
                    });
                    return true;
                }

                case "sa_s1_done":
                {
                    List<IntVec3> tops = new[] { (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1) }.Select(o => SaTop(o.Item1, o.Item2, map)).ToList();
                    int open = tops.Count(t => SaOpen(t, map));
                    List<Pawn> crew = SaCrew(map);
                    Result("S1 three colonists mine below five neighbouring tops (a plus) at once: all open, nobody falls", open == 5 && !saFell && crew.All(p => ProjectZ.FloorOf(p.Position, map) == 1),
                        $"open {open}/5, fell {saNote ?? "none"}; crew {string.Join(", ", crew.Select(p => $"{p} L{ProjectZ.FloorOf(p.Position, map)}"))}; stepped aside {Shaft.SteppedAside - saStep0}");
                    SaEnd(map, "S1", "sa_s2");
                    return true;
                }

                // ------------------------------------------------------------ S2: a colonist walking across the top
                case "sa_s2":
                {
                    SaStart(map);
                    saU = SaTop(0, 0, map);
                    Pawn m = SaOneMiner(map, saU, 2);
                    saP = SaOther(map);
                    Teleport(saP, SaTop(-3, 0, map));
                    Draft(saP, true);
                    saGoal = SaTop(3, 0, map);
                    SaMark(map, new[] { saU });
                    // the miner's pick held until the walker is on U, then the hit that opens it (the timing, not the work)
                    MuRun(200, 52000, () => saOpened && saP.Position == saGoal, "sa_s2_done", () =>
                    {
                        if (ProjectZ.FloorOf(saP.Position, map) == 0)
                        {
                            saFell = true;
                        }
                        if (!saOpened && SaOpen(saU, map))
                        {
                            saOpened = true;
                            saAfter = saP.Position;
                            saNote = $"moving {saP.pather.Moving}, job {saP.CurJobDef?.defName}, dest {saP.pather.Destination}";
                            saGoal = SaTop(3, 0, map);
                            if (saP.CurJobDef != JobDefOf.Goto)
                            {
                                saP.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, saGoal), JobTag.Misc);
                            }
                            return;
                        }
                        if (saOpened)
                        {
                            return;
                        }
                        JobDriver_Mine d = m.jobs.curDriver as JobDriver_MineVertical;
                        if (d == null || d.CurToilIndex < 1)
                        {
                            return;
                        }
                        if (saPhase == 0 || !saP.pather.Moving && saP.CurJobDef != JobDefOf.Goto)
                        {
                            // walk across U, to and fro
                            saPhase = 1;
                            saGoal = saP.Position.x < saU.x ? SaTop(3, 0, map) : SaTop(-3, 0, map);
                            saP.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, saGoal), JobTag.Misc);
                        }
                        if (saP.Position == saU && saP.pather.Moving)
                        {
                            saTicks(d) = 1;
                            saFrom = saU;
                        }
                        else
                        {
                            saTicks(d) = 200;
                        }
                    });
                    return true;
                }

                case "sa_s2_done":
                {
                    bool adj = saAfter.IsValid && saAfter != saU && ProjectZ.FloorOf(saAfter, map) == 1 && saAfter.AdjacentTo8WayOrInside(saU);
                    Result("S2 a colonist walking across a top as it opens steps aside (same level), keeps walking and reaches its goal, no fall",
                        saOpened && adj && !saFell && saP.Position == saGoal && Shaft.SteppedAside - saStep0 == 1,
                        $"opened {saOpened}, on U then {saFrom == saU}, moved to {saAfter} (L{(saAfter.IsValid ? ProjectZ.FloorOf(saAfter, map) : -9)}) {saNote}; now {saP.Position} goal {saGoal}; fell {saFell}; stepped aside {Shaft.SteppedAside - saStep0}");
                    SaEnd(map, "S2", "sa_s3");
                    return true;
                }

                // ------------------------------------------------------------ S3: an animal on the top
                case "sa_s3":
                {
                    SaStart(map);
                    saU = SaTop(0, 0, map);
                    SaOneMiner(map, saU, 2);
                    Rand.PushState(53000);
                    try
                    {
                        saP = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDef.Named("Muffalo"), null));
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                    GenSpawn.Spawn(saP, saU, map);
                    SaMark(map, new[] { saU });
                    MuRun(200, 53100, () => saOpened, "sa_s3_done", () =>
                    {
                        if (!saP.Spawned)
                        {
                            return;
                        }
                        if (ProjectZ.FloorOf(saP.Position, map) == 0)
                        {
                            saFell = true;
                        }
                        if (SaOpen(saU, map))
                        {
                            saOpened = true;
                            saAfter = saP.Position;
                            return;
                        }
                        saFrom = saP.Position;
                        if (saP.Position == saU && saP.CurJobDef != JobDefOf.Wait)
                        {
                            saP.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 5000), JobCondition.InterruptForced);
                        }
                    });
                    return true;
                }

                case "sa_s3_done":
                {
                    bool adj = saAfter.IsValid && saAfter != saU && ProjectZ.FloorOf(saAfter, map) == 1 && saAfter.AdjacentTo8WayOrInside(saU);
                    Result("S3 an animal on a top as it opens steps aside (same level), no fall", saOpened && saFrom == saU && adj && !saFell && Shaft.SteppedAside - saStep0 == 1,
                        $"opened {saOpened}, {saP} on U before {saFrom == saU}, then at {saAfter}; fell {saFell}; stepped aside {Shaft.SteppedAside - saStep0}");
                    SaEnd(map, "S3", "sa_s4");
                    return true;
                }

                // ------------------------------------------------------------ S4: items on the top fall
                case "sa_s4":
                {
                    SaStart(map);
                    saU = SaTop(0, 0, map);
                    saP = SaOneMiner(map, saU, 2);
                    Thing wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                    wood.stackCount = 30;
                    GenSpawn.Spawn(wood, saU, map);
                    SaMark(map, new[] { saU });
                    MuRun(200, 54000, () => SaOpen(saU, map), "sa_s4_done", () =>
                    {
                        if (ProjectZ.FloorOf(saP.Position, map) == 0)
                        {
                            saFell = true;
                        }
                    });
                    return true;
                }

                case "sa_s4_done":
                {
                    IntVec3 g = ProjectZ.GroundCellOf(saU, map);
                    List<Thing> wood = map.listerThings.ThingsOfDef(ThingDefOf.WoodLog).Where(t => t.Spawned && ProjectZ.GroundCellOf(t.Position, map).InHorDistOf(g, 1.5f)).ToList();
                    Result("S4 wood on a top as it opens falls to the pit floor (the miner stays up)", SaOpen(saU, map) && wood.Sum(t => t.stackCount) == 30 && wood.All(t => ProjectZ.FloorOf(t.Position, map) == 0) && !saFell,
                        $"open {SaOpen(saU, map)}; wood {string.Join(", ", wood.Select(t => $"{t.stackCount} at {t.Position} L{ProjectZ.FloorOf(t.Position, map)}"))}; miner fell {saFell}");
                    SaEnd(map, "S4", "sa_s5");
                    return true;
                }

                // ------------------------------------------------------------ S5: no free neighbour: the pawn falls
                case "sa_s5":
                {
                    SaStart(map);
                    saU = SaTop(0, 0, map);
                    IntVec3 n = SaTop(0, 1, map);
                    // the ring: walls on seven neighbours; the eighth (north) is about to open (Mine below on it, its rock
                    // taken away first so nobody works it) and is where the miner stands
                    foreach (IntVec3 o in GenAdj.AdjacentCells)
                    {
                        IntVec3 c = saU + o;
                        if (c != n)
                        {
                            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
                        }
                    }
                    ProjectZ.GroundCellOf(n, map).GetFirstMineable(map)?.Destroy(DestroyMode.Vanish);
                    map.designationManager.AddDesignation(new Designation(n, PZMineDefOf.PZ_MineBelow));
                    SaOneMiner(map, saU, 3);
                    saP = SaOther(map);
                    Teleport(saP, saU);
                    Draft(saP, true);
                    SaMark(map, new[] { saU });
                    MuRun(200, 55000, () => SaOpen(saU, map) && ProjectZ.FloorOf(saP.Position, map) == 0, "sa_s5_done", () =>
                    {
                        if (!saOpened && SaOpen(saU, map))
                        {
                            saOpened = true;
                            saAfter = saP.Position;
                        }
                    });
                    return true;
                }

                case "sa_s5_done":
                {
                    Result("S5 a top ringed by walls and a cell about to open: the colonist on it falls into the pit (as before)",
                        SaOpen(saU, map) && ProjectZ.FloorOf(saP.Position, map) == 0 && ProjectZ.GroundCellOf(saP.Position, map).InHorDistOf(ProjectZ.GroundCellOf(saU, map), 1.5f) && Shaft.SteppedAside == saStep0,
                        $"open {SaOpen(saU, map)}; {saP} at {saP.Position} L{ProjectZ.FloorOf(saP.Position, map)}; stepped aside {Shaft.SteppedAside - saStep0}");
                    SaEnd(map, "S5", "sa_s6");
                    return true;
                }

                // ------------------------------------------------------------ S6: the middle top mined last, no diagonal spot
                // that cannot touch it. Phase 1: the west and south tops (-1,0), (0,-1) are mined below (real jobs); phase 2:
                // the middle top is marked, the crew south-west of it. The diagonal cell (-1,-1) is nearest but both its
                // corner cells are open air: it cannot touch the middle (vanilla's corner rule), so it is never the spot.
                case "sa_s6":
                {
                    SaStart(map);
                    saErr0 = errors;
                    saBadSpot = null;
                    List<IntVec3> arms = new[] { SaTop(-1, 0, map), SaTop(0, -1, map) }.ToList();
                    foreach (IntVec3 t in arms)
                    {
                        Thing rock = ProjectZ.GroundCellOf(t, map).GetFirstMineable(map);
                        if (rock != null)
                        {
                            rock.HitPoints = 1;
                        }
                    }
                    SaMark(map, arms);
                    List<Pawn> crew = SaCrew(map);
                    for (int i = 0; i < crew.Count; i++)
                    {
                        Teleport(crew[i], SaTop(-2 - i, -2, map));
                    }
                    MdWork(map, crew, WorkTypeDefOf.Mining);
                    Info($"S6 phase 1: {arms.Count(t => MineSide.MarkAt(t, map, MineDir.FromAbove))}/2 arm tops marked, crew {crew.Count}");
                    MuRun(200, 56000, () => arms.All(t => SaOpen(t, map)), "sa_s6_mid", SaSpotWatch);
                    return true;
                }

                case "sa_s6_mid":
                {
                    saU = SaTop(0, 0, map);
                    List<Pawn> crew = SaCrew(map);
                    for (int i = 0; i < crew.Count; i++)
                    {
                        crew[i].jobs.EndCurrentJob(JobCondition.InterruptForced);
                        Teleport(crew[i], SaTop(-2 - i, -2, map));
                    }
                    IntVec3 diag = SaTop(-1, -1, map);
                    Info($"S6 phase 2: arms open {SaOpen(SaTop(-1, 0, map), map)}, {SaOpen(SaTop(0, -1, map), map)}; diagonal {diag} touches the middle {ReachabilityImmediate.CanReachImmediate(diag, saU, map, PathEndMode.Touch, crew.FirstOrDefault())}");
                    SaMark(map, new[] { saU });
                    MuRun(300, 56500, () => SaOpen(saU, map), "sa_s6_done", SaSpotWatch);
                    return true;
                }

                case "sa_s6_done":
                {
                    List<IntVec3> tops = new[] { SaTop(-1, 0, map), SaTop(0, -1, map), SaTop(0, 0, map) }.ToList();
                    int open = tops.Count(t => SaOpen(t, map));
                    Result("S6 neighbouring tops mined below, the middle one last: no spot that cannot touch its top, no job loop, all open",
                        open == 3 && saBadSpot == null && errors == saErr0,
                        $"open {open}/3; bad spot {saBadSpot ?? "none"}; errors {errors - saErr0}");
                    SaEnd(map, "S6", null);
                    return true;
                }
            }
            return false;
        }
    }
}
