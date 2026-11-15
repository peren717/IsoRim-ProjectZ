using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode floorstall (user 2026-11-14): the alert "Marked floor out of reach" (FloorStall, Alert_FloorStalled). Run with
    // -projectzfloors=3 -projectzseed=7. Every check builds its own fixture (slabforce's: FoFixture, the L-strip from
    // the stairs exit; DkFixture, the ship). The alert is read where the game keeps it: the alerts readout's own list of
    // active alerts (the game asks the alert every 24 frames; FtFrames frames are waited for it). The colonists work by
    // themselves with ticks; nothing is forced. Order of the run: S3 S1 S2 S6 S7 S8 S9 S5 S4 (the ship last).
    //   S3 nothing marked: the alert is not active
    //   S1 the L-strip all marked, its farthest cell outside the only worker's allowed area: no alert before StallTicks;
    //      after them the alert is active, its culprit that cell, no marked cell worked; the float menu on that cell
    //      refuses the order, on another marked cell it offers it; a jump to the culprit shows its floor. The area
    //      opened: the alert goes at once; the worker takes the whole strip up by its own jobs, the far end first
    //   S2 the same strip and worker, plus an able colonist busy elsewhere (an ordered wait) and an able one asleep: no
    //      alert, though no marked cell is worked
    //   S4 a ship deck block marked, its farthest cell outside the worker's allowed area: the alert is active (Odyssey)
    //   S6 the stall showing: a colonist who does no construction drafted changes nothing; the restricted worker itself
    //      drafted: the alert goes while it is drafted (the game applies no allowed area to drafted pawns) and is back
    //      StallTicks after it is undrafted
    //   S7 a floor that is not stalled, its only able colonist drafted for a while: no alert
    //   S8 the stall showing, the game's Cancel tool on the stuck cell: the alert goes at the next look; the worker takes
    //      up the rest by its own jobs
    //   S9 a marked foundation (a bridge) on the ground floor outside the only worker's allowed area: no alert
    //   S5 cost: 199 marked cells, 20 colonists who do construction; one look timed (nothing marked, stalled, not stalled)
    public static partial class TestRunner
    {
        private const int FtFrames = 40;                                        // more than the readout's 24-frame turn
        private const int FtTicks = Alert_FloorStalled.StallTicks + 100;
        private static Pawn ftR;
        private static Area_Allowed ftArea;
        private static readonly List<IntVec3> ftOut = new List<IntVec3>();      // the cells left out of the allowed area
        private static readonly List<Pawn> ftExtra = new List<Pawn>();
        private static bool ftEarly, ftShown, ftBy, ftDrafted, ftDrafted2, ftBack0, ftHands;
        private static IntVec3 ftGround;
        private static string ftNote2 = "";
        private static int ftForced;
        private static string ftNote = "";

        private static bool StepFloorStall(Map map)
        {
            Rand.PushState(FixtureSeed("floorstall_" + stage));
            try
            {
                return StepFloorStallInner(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        // The game's own alert object and whether its readout lists it as active now.
        private static Alert_FloorStalled FtAlert(out bool active)
        {
            AlertsReadout r = Find.Alerts;
            List<Alert> all = AccessTools.Field(typeof(AlertsReadout), "AllAlerts").GetValue(r) as List<Alert>;
            List<Alert> on = AccessTools.Field(typeof(AlertsReadout), "activeAlerts").GetValue(r) as List<Alert>;
            Alert_FloorStalled a = all?.OfType<Alert_FloorStalled>().FirstOrDefault();
            active = a != null && on != null && on.Contains(a);
            return a;
        }

        private static string FtState(Map map)
        {
            Alert_FloorStalled a = FtAlert(out bool active);
            List<IntVec3> stuck = new List<IntVec3>();
            FloorStall.Stuck(map, stuck);
            List<GlobalTargetInfo> culprits = a?.GetReport().AllCulprits?.ToList() ?? new List<GlobalTargetInfo>();
            return $"alert in the readout {a != null}, active {active}, culprits [{string.Join(" ", culprits.Select(c => c.Cell))}]; stuck now [{string.Join(" ", stuck)}]; game tick {Find.TickManager.TicksGame}";
        }

        private static bool FtActive(Map map, IEnumerable<IntVec3> want)
        {
            Alert_FloorStalled a = FtAlert(out bool active);
            if (a == null || !active)
            {
                return false;
            }
            List<GlobalTargetInfo> culprits = a.GetReport().AllCulprits?.ToList() ?? new List<GlobalTargetInfo>();
            HashSet<IntVec3> w = new HashSet<IntVec3>(want);
            return culprits.Count == w.Count && culprits.All(c => c.Map == map && w.Contains(c.Cell));
        }

        // Only 'workers' do construction (nothing else); every other colonist does no work at all.
        private static void FtWork(Map map, params Pawn[] workers)
        {
            foreach (Pawn p in Colonists(map))
            {
                if (workers.Contains(p))
                {
                    R2Work(p, WorkTypeDefOf.Construction);
                }
                else
                {
                    R2Work(p);
                }
            }
        }

        // An allowed area of the whole map but 'without', for 'pawns'.
        private static void FtRestrict(Map map, IEnumerable<IntVec3> without, params Pawn[] pawns)
        {
            FtFree(map);
            ftOut.Clear();
            ftOut.AddRange(without);
            if (!map.areaManager.TryMakeNewAllowed(out ftArea))
            {
                Info("floorstall: no allowed area could be made");
                return;
            }
            foreach (IntVec3 c in map.AllCells)
            {
                ftArea[c] = true;
            }
            foreach (IntVec3 c in ftOut)
            {
                ftArea[c] = false;
            }
            foreach (Pawn p in pawns)
            {
                p.playerSettings.AreaRestrictionInPawnCurrentMap = ftArea;
            }
        }

        private static void FtFree(Map map)
        {
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.playerSettings != null && p.playerSettings.AreaRestrictionInPawnCurrentMap != null)
                {
                    p.playerSettings.AreaRestrictionInPawnCurrentMap = null;
                }
            }
            if (ftArea != null)
            {
                ftArea.Delete();
                ftArea = null;
            }
        }

        private static IntVec3 FtFar(Map map) => FoL(12, 19, 1, map);

        // The stall of S1: the L-strip all marked, its far cell outside the allowed area of the only worker; the second
        // colonist does no work and is not drafted.
        private static void FtStall(Map map)
        {
            FoFixture(map, FoLStrip(), true, FoG(26, 13), g => true);
            FtWork(map, r2P);
            FtRestrict(map, new[] { FtFar(map) }, r2P);
            if (r2Q != null)
            {
                Draft(r2Q, false);
            }
            ftForced = 0;
        }

        private static bool FtWatch(Map map, Func<bool> done)
        {
            Job j = r2P.CurJob;
            if (j != null && (j.def == JobDefOf.RemoveFoundation || j.def == JobDefOf.RemoveFloor) && j.playerForced)
            {
                ftForced++;
            }
            return FoWatch(map, done);
        }

        private static bool StepFloorStallInner(Map map)
        {
            switch (stage)
            {
                case "ft_setup":
                {
                    DebugSettings.enableRandomMentalStates = false;
                    Find.Storyteller.storytellerComps.Clear();
                    Find.PlaySettings.autoRebuild = false;
                    NoAmbushes(map);
                    foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
                    {
                        a.Destroy();
                    }
                    if (Levels.Max(map) < 2)
                    {
                        Result("floorstall: a map with 3 levels", false, $"levels {Levels.Min(map)}..{Levels.Max(map)} (run with -projectzfloors=3)");
                        Finish();
                        return true;
                    }
                    r2Area = new CellRect(map.Center.x - 22, map.Center.z - 16, 44, 32);
                    List<Pawn> cols = Colonists(map).OrderBy(p => p.thingIDNumber).ToList();
                    foreach (Pawn p in cols)
                    {
                        p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
                    }
                    List<Pawn> able = cols.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)).ToList();
                    r2P = able.FirstOrDefault() ?? cols.First();
                    r2Q = able.FirstOrDefault(p => p != r2P);
                    ftR = able.FirstOrDefault(p => p != r2P && p != r2Q);
                    Info($"floorstall: area {r2Area}, worker {r2P}, able colonists {r2Q} and {ftR}; StallTicks {Alert_FloorStalled.StallTicks}, RecheckFrames {Alert_FloorStalled.RecheckFrames}");
                    Info("copy watches: registered " + CopyWatches.Registered + "/" + CopyWatches.Count + "; hashes " + CopyWatches.Hashes());
                    // (the game's alerts start after tick 600)
                    SeedTicks("ft_setup");
                    StartTicks(700, "ft_3");
                    return true;
                }

                // ---- S3: nothing marked
                case "ft_3":
                {
                    FoFixture(map, FoLStrip(), true, FoG(26, 13), g => false);
                    FtWork(map, r2P);
                    FtFree(map);
                    Wait(FtFrames, "ft_3_a");
                    return true;
                }
                case "ft_3_a":
                {
                    SeedTicks("ft_3");
                    StartTicks(FtTicks, "ft_3_b");
                    return true;
                }
                case "ft_3_b":
                    Wait(FtFrames, "ft_3_done");
                    return true;
                case "ft_3_done":
                {
                    Alert_FloorStalled a = FtAlert(out bool active);
                    List<IntVec3> stuck = new List<IntVec3>();
                    FloorStall.Stuck(map, stuck);
                    Result("S3 nothing marked: the alert is in the game's readout and not active",
                        a != null && !active && stuck.Count == 0 && foCells.Count == 0 && !map.designationManager.AnySpawnedDesignationOfDef(DesignationDefOf.RemoveFoundation) && errors == foErrAt,
                        $"{FtState(map)}; marked {foCells.Count}; label '{"PZ_AlertFloorStalled".Translate()}'; errors {errors - foErrAt}");
                    stage = "ft_1";
                    return true;
                }

                // ---- S1: the far cell outside the only worker's allowed area
                case "ft_1":
                {
                    FoFixture(map, FoLStrip(), true, FoG(26, 13), g => true);
                    FtWork(map, r2P);
                    FtRestrict(map, new[] { FtFar(map) }, r2P);
                    ftForced = 0;
                    ftNote = $"marked {foCells.Count}, {FoDist(map, FtFar(map))}; worker {r2P} area '{r2P.playerSettings.EffectiveAreaRestrictionInPawnCurrentMap?.Label}' (the far cell in it {FtFar(map).InAllowedArea(r2P)}), forbidden for it {FtFar(map).IsForbidden(r2P)}";
                    Wait(FtFrames, "ft_1_a");
                    return true;
                }
                case "ft_1_a":
                {
                    // (looked at once, no tick since: stuck, but not yet for StallTicks)
                    FtAlert(out ftEarly);
                    ftNote += $"; before the ticks: {FtState(map)}";
                    SeedTicks("ft_1");
                    TickUntil(FtTicks, () => FtWatch(map, () => false), "ft_1_b", 1);
                    return true;
                }
                case "ft_1_b":
                    tickWatch = null;
                    Wait(FtFrames, "ft_1_c");
                    return true;
                case "ft_1_c":
                {
                    IntVec3 far = FtFar(map);
                    Alert_FloorStalled a = FtAlert(out bool active);
                    Job j = r2P.CurJob;
                    bool working = j != null && (j.def == JobDefOf.RemoveFoundation || j.def == JobDefOf.RemoveFloor);
                    Result("S1 the far cell outside the only worker's allowed area: not shown at the first look, active after StallTicks, its only culprit that cell",
                        !ftEarly && FtActive(map, new[] { far }),
                        $"{ftNote}; after {foTick} ticks: {FtState(map)}");
                    Result("S1 no marked cell is worked while it is stalled (the order holds)",
                        FoLeft(map) == foCells.Count && !foFirst.IsValid && !working && errors == foErrAt,
                        $"marked left {FoLeft(map)}/{foCells.Count}, first taken up {foFirst}, worker at {r2P.Position} job {r2P.CurJobDef?.defName}; errors {errors - foErrAt}");
                    string label = a?.Label ?? "", text = a != null ? a.GetExplanation().ToString() : "";
                    Result("S1 the alert's texts are there (label and explanation)",
                        label == "PZ_AlertFloorStalled".Translate().ToString() && label.Length > 0 && !label.Contains("PZ_") && text.Length > 0 && !text.Contains("PZ_"),
                        $"label '{label}'; explanation '{text.Replace("\n", " / ")}'; priority {a?.Priority}");
                    // (what the right-click order really offers: on the stuck cell and on another marked cell)
                    FloatMenuOption onStuck = FoOption(r2P, far, out string shownStuck);
                    IntVec3 near = FoL(19, 13, 1, map);
                    FloatMenuOption onNear = FoOption(r2P, near, out string shownNear);
                    Result("S1 the right-click order: refused on the stuck cell (outside the allowed area), offered on another marked cell",
                        (onStuck == null || onStuck.Disabled) && onNear != null && !onNear.Disabled,
                        $"stuck cell {far}: {shownStuck}; marked cell {near}: {shownNear}");
                    // (the alert's click: the game jumps to the culprit; the floor view follows)
                    FloorView.Set(0);
                    GlobalTargetInfo culprit = a?.GetReport().AllCulprits?.FirstOrDefault() ?? GlobalTargetInfo.Invalid;
                    if (culprit.IsValid)
                    {
                        CameraJumper.TryJumpAndSelect(culprit);
                    }
                    int shownFloor = FloorView.Current;
                    Result("S1 a jump to the culprit shows its floor",
                        culprit.IsValid && shownFloor == ProjectZ.FloorOf(far, map) && shownFloor >= 1,
                        $"culprit {culprit}; floor view {shownFloor} (the cell's floor {ProjectZ.FloorOf(far, map)})");
                    FloorView.Set(0);
                    // the area opened
                    FtFree(map);
                    Wait(FtFrames, "ft_1_d");
                    return true;
                }
                case "ft_1_d":
                {
                    FtAlert(out bool active);
                    Result("S1 the area opened: the alert goes at the next look (no tick needed)", !active, FtState(map));
                    SeedTicks("ft_1_open");
                    TickUntil(30000, () => FtWatch(map, () => FoLeft(map) == 0), "ft_1_e", 1);
                    return true;
                }
                case "ft_1_e":
                    tickWatch = null;
                    Wait(FtFrames, "ft_1_done");
                    return true;
                case "ft_1_done":
                {
                    FtAlert(out bool active);
                    Result("S1 then the worker takes the whole strip up by its own jobs (none forced), the far end first; the alert stays off",
                        FoLeft(map) == 0 && foFirst == FtFar(map) && ftForced == 0 && foOnCell == 0 && !active && errors == foErrAt,
                        $"marked left {FoLeft(map)}/{foCells.Count} after {foTick} ticks, first {foFirst} (the far cell {FtFar(map)}) at tick {foFirstTick}, forced job ticks {ftForced}, ticks on its cell {foOnCell}; {FtState(map)}; errors {errors - foErrAt}");
                    stage = "ft_2";
                    return true;
                }

                // ---- S2: able colonists who are busy or asleep
                case "ft_2":
                {
                    FoFixture(map, FoLStrip(), true, FoG(26, 13), g => true);
                    FtWork(map, new[] { r2P, r2Q, ftR }.Where(p => p != null).ToArray());
                    FtRestrict(map, new[] { FtFar(map) }, r2P);
                    ftForced = 0;
                    ftNote = "";
                    if (r2Q != null)
                    {
                        // (busy elsewhere: an ordered wait, far from the stairs, longer than the check)
                        Teleport(r2Q, FoG(38, 26));
                        Draft(r2Q, false);
                        Job wait = JobMaker.MakeJob(JobDefOf.Wait);
                        wait.expiryInterval = 20000;
                        r2Q.jobs.TryTakeOrderedJob(wait, JobTag.Misc);
                    }
                    if (ftR != null)
                    {
                        // (exhausted: it lies down by itself and sleeps through the check)
                        Teleport(ftR, FoG(38, 4));
                        Draft(ftR, false);
                        if (ftR.needs?.rest != null)
                        {
                            ftR.needs.rest.CurLevel = 0.01f;
                        }
                    }
                    Wait(FtFrames, "ft_2_a");
                    return true;
                }
                case "ft_2_a":
                {
                    FtAlert(out ftEarly);
                    SeedTicks("ft_2");
                    TickUntil(FtTicks + 400, () => FtWatch(map, () => false), "ft_2_b", 1);
                    return true;
                }
                case "ft_2_b":
                    tickWatch = null;
                    Wait(FtFrames, "ft_2_done");
                    return true;
                case "ft_2_done":
                {
                    FtAlert(out bool active);
                    List<IntVec3> stuck = new List<IntVec3>();
                    FloorStall.Stuck(map, stuck);
                    bool busy = r2Q != null && r2Q.CurJobDef == JobDefOf.Wait;
                    bool asleep = ftR != null && !ftR.Awake();
                    Result("S2 the restricted worker plus an able colonist busy elsewhere and an able one asleep: no alert, though no marked cell is worked",
                        !ftEarly && !active && stuck.Count == 0 && busy && asleep && FoLeft(map) == foCells.Count && errors == foErrAt,
                        $"{FtState(map)}; after {foTick} ticks marked left {FoLeft(map)}/{foCells.Count}; busy {r2Q} job {r2Q?.CurJobDef?.defName} at {r2Q?.Position}; asleep {ftR} awake {ftR?.Awake()} job {ftR?.CurJobDef?.defName} at {ftR?.Position}; restricted {r2P} job {r2P.CurJobDef?.defName}; errors {errors - foErrAt}");
                    if (r2Q != null)
                    {
                        r2Q.jobs.StopAll();
                    }
                    if (ftR?.needs?.rest != null)
                    {
                        ftR.needs.rest.CurLevel = 1f;
                        ftR.jobs.StopAll();
                    }
                    FtFree(map);
                    stage = "ft_6";
                    return true;
                }

                // ---- S4: a ship deck (the last check: the ship's decks stay in the foundation grid, R2Clear keeps them)
                case "ft_4":
                {
                    if (!DkFixture(map, 1, DkBlockA(), false))
                    {
                        stage = "ft_end";
                        return true;
                    }
                    FtWork(map, r2P);
                    int far = dkDist.Values.DefaultIfEmpty(-1).Max();
                    FtRestrict(map, foCells.Where(c => dkDist[c] == far).ToList(), r2P);
                    ftNote = $"deck cells marked {foCells.Count}, distances {string.Join(" ", foCells.Select(c => dkDist[c]))}; outside the worker's area [{string.Join(" ", ftOut)}]";
                    Wait(FtFrames, "ft_4_a");
                    return true;
                }
                case "ft_4_a":
                {
                    FtAlert(out ftEarly);
                    SeedTicks("ft_4");
                    TickUntil(FtTicks, () => { DkWatch(map); return false; }, "ft_4_b", 1);
                    return true;
                }
                case "ft_4_b":
                    tickWatch = null;
                    Wait(FtFrames, "ft_4_done");
                    return true;
                case "ft_4_done":
                {
                    Result("S4 a ship deck block, its farthest cell outside the worker's allowed area: the alert is active, its culprit that cell, no deck taken up",
                        !ftEarly && ftOut.Count > 0 && FtActive(map, ftOut) && DkLeft(map) == foCells.Count && ftOut.All(c => ShipDeck.Upper(c, map)) && errors == foErrAt,
                        $"{ftNote}; after {foTick} ticks: {FtState(map)}; decks left {DkLeft(map)}/{foCells.Count}; worker at {r2P.Position} job {r2P.CurJobDef?.defName}; errors {errors - foErrAt}");
                    FtFree(map);
                    stage = "ft_end";
                    return true;
                }

                // ---- S6: drafting while the stall shows
                case "ft_6":
                    FtStall(map);
                    Wait(FtFrames, "ft_6_a");
                    return true;
                case "ft_6_a":
                    SeedTicks("ft_6");
                    TickUntil(FtTicks, () => FtWatch(map, () => false), "ft_6_b", 1);
                    return true;
                case "ft_6_b":
                    tickWatch = null;
                    Wait(FtFrames, "ft_6_c");
                    return true;
                case "ft_6_c":
                {
                    ftShown = FtActive(map, new[] { FtFar(map) });
                    ftNote = $"stall showing {ftShown} ({FtState(map)})";
                    // (a colonist who does no construction is drafted)
                    if (r2Q != null)
                    {
                        Draft(r2Q, true);
                    }
                    Wait(FtFrames, "ft_6_d");
                    return true;
                }
                case "ft_6_d":
                {
                    ftBy = FtActive(map, new[] { FtFar(map) });
                    ftNote += $"; {r2Q} (no construction) drafted {r2Q?.Drafted}: {FtState(map)}";
                    // (the restricted worker itself is drafted)
                    Draft(r2P, true);
                    Wait(FtFrames, "ft_6_e");
                    return true;
                }
                case "ft_6_e":
                {
                    FtAlert(out ftDrafted);
                    ftNote2 = $"worker {r2P} drafted {r2P.Drafted} (the far cell forbidden for it now {FtFar(map).IsForbidden(r2P)}): {FtState(map)}";
                    SeedTicks("ft_6_drafted");
                    TickUntil(FtTicks, () => FtWatch(map, () => false), "ft_6_f", 1);
                    return true;
                }
                case "ft_6_f":
                    tickWatch = null;
                    Wait(FtFrames, "ft_6_g");
                    return true;
                case "ft_6_g":
                {
                    FtAlert(out ftDrafted2);
                    ftNote2 += $"; still drafted after {FtTicks} ticks: {FtState(map)}";
                    Draft(r2P, false);
                    Wait(FtFrames, "ft_6_h");
                    return true;
                }
                case "ft_6_h":
                {
                    FtAlert(out ftBack0);
                    ftNote2 += $"; undrafted, first look: {FtState(map)}";
                    SeedTicks("ft_6_undrafted");
                    TickUntil(FtTicks, () => FtWatch(map, () => false), "ft_6_i", 1);
                    return true;
                }
                case "ft_6_i":
                    tickWatch = null;
                    Wait(FtFrames, "ft_6_done");
                    return true;
                case "ft_6_done":
                {
                    bool back = FtActive(map, new[] { FtFar(map) });
                    Result("S6 the stall showing, a colonist who does no construction is drafted: nothing changes (the alert stays, the same culprit)",
                        ftShown && ftBy && r2Q != null, ftNote);
                    Result("S6 the restricted worker itself drafted: the alert goes while it is drafted (the game applies no allowed area to a drafted pawn), and is back StallTicks after it is undrafted; no marked cell worked",
                        ftShown && !ftDrafted && !ftDrafted2 && !ftBack0 && back && FoLeft(map) == foCells.Count && errors == foErrAt,
                        $"{ftNote2}; {FtTicks} ticks later: {FtState(map)}; marked left {FoLeft(map)}/{foCells.Count}; errors {errors - foErrAt}");
                    if (r2Q != null)
                    {
                        Draft(r2Q, false);
                    }
                    FtFree(map);
                    stage = "ft_7";
                    return true;
                }

                // ---- S7: a floor that is not stalled, its able colonist drafted for a while
                case "ft_7":
                {
                    FoFixture(map, FoLStrip(), true, FoG(26, 13), g => true);
                    FtWork(map, r2P);
                    FtFree(map);
                    Draft(r2P, true);
                    ftForced = 0;
                    Wait(FtFrames, "ft_7_a");
                    return true;
                }
                case "ft_7_a":
                {
                    FtAlert(out ftEarly);
                    SeedTicks("ft_7");
                    TickUntil(FtTicks + 400, () => FtWatch(map, () => false), "ft_7_b", 1);
                    return true;
                }
                case "ft_7_b":
                    tickWatch = null;
                    Wait(FtFrames, "ft_7_done");
                    return true;
                case "ft_7_done":
                {
                    FtAlert(out bool active);
                    List<IntVec3> stuck = new List<IntVec3>();
                    FloorStall.Stuck(map, stuck);
                    Result("S7 a floor that is not stalled, its only able colonist drafted for a while: no alert, though no marked cell is worked",
                        !ftEarly && !active && stuck.Count == 0 && r2P.Drafted && FoLeft(map) == foCells.Count && errors == foErrAt,
                        $"{FtState(map)}; after {foTick} ticks marked left {FoLeft(map)}/{foCells.Count}; worker {r2P} drafted {r2P.Drafted} at {r2P.Position}; errors {errors - foErrAt}");
                    Draft(r2P, false);
                    stage = "ft_8";
                    return true;
                }

                // ---- S8: the stuck cell's mark cancelled
                case "ft_8":
                    FtStall(map);
                    Wait(FtFrames, "ft_8_a");
                    return true;
                case "ft_8_a":
                    SeedTicks("ft_8");
                    TickUntil(FtTicks, () => FtWatch(map, () => false), "ft_8_b", 1);
                    return true;
                case "ft_8_b":
                    tickWatch = null;
                    Wait(FtFrames, "ft_8_c");
                    return true;
                case "ft_8_c":
                {
                    IntVec3 far = FtFar(map);
                    ftShown = FtActive(map, new[] { far });
                    ftNote = $"stall showing {ftShown} ({FtState(map)})";
                    // (the game's Cancel tool on the stuck cell)
                    Designator_Cancel cancel = new Designator_Cancel();
                    AcceptanceReport can = cancel.CanDesignateCell(far);
                    if (can.Accepted)
                    {
                        cancel.DesignateSingleCell(far);
                    }
                    ftNote += $"; Cancel on {far}: accepted {can.Accepted}, still marked {map.designationManager.DesignationAt(far, DesignationDefOf.RemoveFoundation) != null}";
                    Wait(FtFrames, "ft_8_d");
                    return true;
                }
                case "ft_8_d":
                {
                    FtAlert(out ftEarly);
                    ftNote += $"; next look: {FtState(map)}";
                    SeedTicks("ft_8_rest");
                    TickUntil(30000, () => FtWatch(map, () => FoLeft(map) <= 1), "ft_8_e", 1);
                    return true;
                }
                case "ft_8_e":
                    tickWatch = null;
                    Wait(FtFrames, "ft_8_done");
                    return true;
                case "ft_8_done":
                {
                    IntVec3 far = FtFar(map), second = FoL(12, 18, 1, map);
                    FtAlert(out bool active);
                    Result("S8 the stall showing, the game's Cancel tool on the stuck cell: the alert goes at the next look (no tick needed)",
                        ftShown && !ftEarly && map.designationManager.DesignationAt(far, DesignationDefOf.RemoveFoundation) == null, ftNote);
                    Result("S8 then the worker takes up the rest by its own jobs (none forced), the next cell first; the cancelled cell stays; the alert stays off",
                        FoLeft(map) == 1 && ProjectZ.HasFloor(far, map) && foFirst == second && ftForced == 0 && foOnCell == 0 && !active && errors == foErrAt,
                        $"marked left {FoLeft(map)}/{foCells.Count} after {foTick} ticks (the cancelled cell {far} still a floor {ProjectZ.HasFloor(far, map)}), first taken up {foFirst} (the next cell {second}), forced job ticks {ftForced}, ticks on its cell {foOnCell}; {FtState(map)}; errors {errors - foErrAt}");
                    FtFree(map);
                    stage = "ft_9";
                    return true;
                }

                // ---- S9: a marked foundation on the ground floor nobody can work on: not ProjectZ's cells
                case "ft_9":
                {
                    FoFixture(map, FoLStrip(), true, FoG(26, 13), g => false);
                    FtWork(map, r2P);
                    ftGround = FoG(34, 22);
                    map.terrainGrid.SetFoundation(ftGround, TerrainDefOf.Bridge);
                    Designator_RemoveFoundation tool = new Designator_RemoveFoundation();
                    AcceptanceReport can = tool.CanDesignateCell(ftGround);
                    if (can.Accepted)
                    {
                        tool.DesignateSingleCell(ftGround);
                    }
                    FtRestrict(map, new[] { ftGround }, r2P);
                    WorkGiver_Scanner giver = DefDatabase<WorkGiverDef>.GetNamed("ConstructRemoveFoundations").Worker as WorkGiver_Scanner;
                    ftHands = FloorStall.Hands(r2P, giver, ftGround);
                    ftNote = $"bridge on the ground at {ftGround} (floor {ProjectZ.FloorOf(ftGround, map)}, foundation {map.terrainGrid.FoundationAt(ftGround)?.defName}), the remove tool accepted {can.Accepted}, in the peel order's cells {SlabPeel.Peeled(ftGround, map)}; automatic work hands it to the worker {ftHands} (forbidden for it {ftGround.IsForbidden(r2P)}, the work giver's answer {giver.HasJobOnCell(r2P, ftGround)})";
                    Wait(FtFrames, "ft_9_a");
                    return true;
                }
                case "ft_9_a":
                {
                    FtAlert(out ftEarly);
                    SeedTicks("ft_9");
                    StartTicks(FtTicks + 400, "ft_9_b");
                    return true;
                }
                case "ft_9_b":
                    Wait(FtFrames, "ft_9_done");
                    return true;
                case "ft_9_done":
                {
                    FtAlert(out bool active);
                    List<IntVec3> stuck = new List<IntVec3>();
                    FloorStall.Stuck(map, stuck);
                    bool marked = map.designationManager.DesignationAt(ftGround, DesignationDefOf.RemoveFoundation) != null;
                    bool there = map.terrainGrid.FoundationAt(ftGround) == TerrainDefOf.Bridge;
                    Result("S9 a marked foundation on the ground floor that the only worker cannot work on (outside its allowed area): no alert",
                        marked && there && !ftHands && !ftEarly && !active && stuck.Count == 0 && errors == foErrAt,
                        $"{ftNote}; after {FtTicks + 400} ticks still marked {marked}, still there {there}; {FtState(map)}; errors {errors - foErrAt}");
                    foreach (Designation d in map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.RemoveFoundation).ToList())
                    {
                        d.Delete();
                    }
                    if (there)
                    {
                        map.terrainGrid.RemoveFoundation(ftGround, false);
                    }
                    FtFree(map);
                    stage = "ft_5";
                    return true;
                }

                // ---- S5: the cost of one look
                case "ft_5":
                {
                    // (x 1..20, z 9..18 on level 1: 200 cells, the stairs exit (20, 13) among them is not markable)
                    FoFixture(map, new[] { new CellRect(FoG(1, 9).x, FoG(1, 9).z, 20, 10) }, true, FoG(26, 13), g => true);
                    ftExtra.Clear();
                    List<Pawn> cols = Colonists(map);
                    for (int i = cols.Count; i < 20; i++)
                    {
                        Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true));
                        GenSpawn.Spawn(p, CellFinder.StandableCellNear(FoG(30, 6 + i), map, 4f), map);
                        p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
                        ftExtra.Add(p);
                    }
                    cols = Colonists(map);
                    foreach (Pawn p in cols)
                    {
                        Draft(p, false);
                    }
                    FtWork(map, cols.ToArray());
                    int doers = cols.Count(p => p.workSettings != null && p.workSettings.WorkIsActive(WorkTypeDefOf.Construction));
                    SlabPeel peel = SlabPeel.For(map);
                    List<IntVec3> next = new List<IntVec3>();
                    peel.Next(next);
                    string head = $"{foCells.Count} marked cells, next in the order [{string.Join(" ", next)}], {cols.Count} colonists ({doers} do construction)";
                    List<IntVec3> stuck = new List<IntVec3>();
                    // stalled: everyone's area leaves the next cells out
                    FtRestrict(map, next, cols.ToArray());
                    string stalled = FtTime(map, stuck);
                    int stuckCount = stuck.Count;
                    // not stalled: the area opened
                    FtFree(map);
                    string free = FtTime(map, stuck);
                    int freeCount = stuck.Count;
                    // every colonist asked in full about one next cell (all three tests run and say yes): the most a stalled
                    // look can cost per next cell
                    WorkGiver_Scanner giver = DefDatabase<WorkGiverDef>.GetNamed("ConstructRemoveFoundations").Worker as WorkGiver_Scanner;
                    int yes = 0;
                    map.reachability.ClearCache();
                    Stopwatch sw = Stopwatch.StartNew();
                    foreach (Pawn p in cols)
                    {
                        yes += FloorStall.Hands(p, giver, next[0]) ? 1 : 0;
                    }
                    double fullFirst = sw.Elapsed.TotalMilliseconds * 1000.0;
                    sw.Restart();
                    for (int i = 0; i < 200; i++)
                    {
                        foreach (Pawn p in cols)
                        {
                            FloorStall.Hands(p, giver, next[0]);
                        }
                    }
                    string full = $"{cols.Count} colonists asked in full about one cell ({yes} could take it): first {fullFirst:0} us, then {sw.Elapsed.TotalMilliseconds * 1000.0 / 200:0.0} us";
                    // nothing marked
                    foreach (Designation d in map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.RemoveFoundation).ToList())
                    {
                        d.Delete();
                    }
                    string none = FtTime(map, stuck);
                    Result("S5 cost of one look (the game asks at most once in 24 frames), 199 marked cells and 20 colonists: stalled, not stalled, nothing marked",
                        foCells.Count >= 190 && cols.Count == 20 && doers >= 15 && stuckCount == next.Count && next.Count > 0 && freeCount == 0 && stuck.Count == 0 && errors == foErrAt,
                        $"{head}; stalled (stuck {stuckCount}): {stalled}; not stalled (stuck {freeCount}): {free}; {full}; nothing marked: {none}; errors {errors - foErrAt}");
                    foreach (Pawn p in ftExtra)
                    {
                        if (!p.Destroyed)
                        {
                            p.Destroy();
                        }
                    }
                    ftExtra.Clear();
                    stage = "ft_4";
                    return true;
                }
                case "ft_end":
                    FtFree(map);
                    Finish();
                    return true;
            }
            return false;
        }

        // One look timed: the first one with the reach cache emptied (as after any change of the map's regions), then the
        // mean of 200 more.
        private static string FtTime(Map map, List<IntVec3> stuck)
        {
            map.reachability.ClearCache();
            stuck.Clear();
            Stopwatch sw = Stopwatch.StartNew();
            FloorStall.Stuck(map, stuck);
            double first = sw.Elapsed.TotalMilliseconds * 1000.0;
            int found = stuck.Count;
            const int runs = 200;
            List<IntVec3> again = new List<IntVec3>();
            sw.Restart();
            for (int i = 0; i < runs; i++)
            {
                again.Clear();
                FloorStall.Stuck(map, again);
            }
            double mean = sw.Elapsed.TotalMilliseconds * 1000.0 / runs;
            return $"first look {first:0} us, then {mean:0.0} us a look ({found} stuck)";
        }
    }
}
