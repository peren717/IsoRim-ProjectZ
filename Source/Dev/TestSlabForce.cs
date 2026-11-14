using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode slabforce (GitHub issue, user 2026-11-02: a forced order skips the peel order and the K2 cut-off check; automatic
    // work keeps both; the physical rules stand). Run with -projectzfloors=3 -projectzseed=7. Every check builds its own
    // fixture (R2Clear) in the cleared area; the stairs ground -> level 1 at (22, 13) facing west, exit (20, 13). The
    // forced order is the float menu's: FloatMenuMakerMap.GetOptions on the cell for the undrafted worker (Construction
    // only), the remove-foundation option chosen (its action: TryTakeOrderedJobPrioritizedWork with the job the work giver
    // gives under forced=true); then the worker does the job with ticks.
    //   F1 a solid 11 x 7 floor, all designated: a forced order on an inner cell (not the farthest) is taken up first, the
    //      worker never on it
    //   F2 an L-strip from the stairs, all designated: automatic work takes the far end first (as before); a forced order
    //      on the near end takes it up first; the cut-off rest is left alone, no errors
    //   F3 a strip to a block, all designated, the worker and a drafted colonist on the far block, steel there: a forced
    //      order in the strip's middle cuts the block off; it is taken up, the game runs on without errors
    //   F4 a platform reached only by a ladder, its top cell designated: automatic work refuses it (K2, peel); a forced
    //      order takes it up (the worker stays on the platform)
    //   F5 physical rules: a table on the cell: forced refused; a slab with a slab above (Q6 (b)): forced taken up, the
    //      floor above stays; a slab no one can stand next to: forced refused
    //   F6 ship upper decks follow the same peel order (user 2026-11-14; Odyssey). A two-level gravship
    //      (TestGravLand.BuildShip, 13 x 11) with ship decks over all of it on level 1 but the stairwell, ship stairs inside
    //      it up to the deck; the workers start on the ship's ground floor and walk up. Automatic work:
    //      F6a two 3 x 3 blocks designated (the deck's far corner and an inner block), three workers: every cell taken up
    //          within the time bound, none left stranded with no cell to stand on, farthest from the stairs first;
    //      F6b the inner block, a forced order on its middle cell: taken up first (out of order); the rest after it;
    //      F6c the inner block with a level-2 deck kept over its middle (Q6 (b)): all nine taken up, the level-2 deck stays
    //          over the hole (ShipColumn.Hole, the floor roof under it), nothing collapses;
    //      F6d a one-wide dead-end deck corridor, all designated, a chunk on its second-to-last cell (a cell a pawn walks
    //          over but may not stand on; the chunk stays: hauled only when marked, and the workers only construct): the
    //          end cell is taken up first, from the chunk's cell as a slab's would be, then the rest.
    public static partial class TestRunner
    {
        private static IntVec3 foTarget;
        private static List<IntVec3> foCells = new List<IntVec3>();
        private static IntVec3 foFirst;
        private static int foFirstTick, foTick, foOnCell, foErrAt;
        private static string foNote = "";
        private static bool foOk;
        private static Building_SiegeLadder foLadder;
        private static Thing foSteel;

        private static IntVec3 FoG(int x, int z) => RG(x, z);

        private static IntVec3 FoL(int x, int z, int L, Map map) => RL(x, z, L, map);

        private static bool StepSlabForce(Map map)
        {
            Rand.PushState(FixtureSeed("slabforce_" + stage));
            try
            {
                return StepSlabForceInner(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        // The fixture: the area cleared, the stairs, the floor cells (level L) given, the worker on ground cell 'at' with
        // Construction only, undrafted. The floor cells in 'designate' are designated with the vanilla tool (cells it
        // refuses are left out: the stairs exit).
        private static void FoFixture(Map map, IEnumerable<CellRect> floors, bool stairs, IntVec3 at, Func<IntVec3, bool> designate)
        {
            R2Clear(map);
            // (designations outlive the terrain R2Clear resets: the last fixture's go)
            foreach (Designation d in map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.RemoveFoundation).ToList())
            {
                d.Delete();
            }
            R2Park(map, r2P);
            R2Feed(map);
            LinkSteps.For(map)?.steps.Clear();
            foreach (CellRect r in floors)
            {
                R2Floor(map, r, 1);
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            if (stairs)
            {
                Building_Stairs st = R2Stairs(map, FoG(22, 13), Rot4.West);
                if (st == null)
                {
                    Info("slabforce: stairs not placed");
                }
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            }
            foCells.Clear();
            Designator_RemoveFoundation tool = new Designator_RemoveFoundation();
            foreach (CellRect r in floors)
            {
                foreach (IntVec3 g in r)
                {
                    IntVec3 c = Levels.Cell(g, 1, map);
                    if (c.IsValid && designate(g) && tool.CanDesignateCell(c).Accepted)
                    {
                        tool.DesignateSingleCell(c);
                        foCells.Add(c);
                    }
                }
            }
            Teleport(r2P, at);
            Draft(r2P, false);
            R2Work(r2P, WorkTypeDefOf.Construction);
            foFirst = IntVec3.Invalid;
            foFirstTick = -1;
            foTick = 0;
            foOnCell = 0;
            foErrAt = errors;
            foNote = "";
        }

        // The float menu's remove-foundation option on cell c for pawn p (null: none), and what the menu showed.
        private static FloatMenuOption FoOption(Pawn p, IntVec3 c, out string shown)
        {
            WorkGiverDef wg = DefDatabase<WorkGiverDef>.GetNamed("ConstructRemoveFoundations");
            List<FloatMenuOption> opts = FloatMenuMakerMap.GetOptions(new List<Pawn> { p }, c.ToVector3Shifted(), out FloatMenuContext ctx);
            FloatMenuOption o = opts.FirstOrDefault(x => x.Label.IndexOf(wg.gerund, StringComparison.OrdinalIgnoreCase) >= 0 || x.Label.IndexOf(wg.verb, StringComparison.OrdinalIgnoreCase) >= 0);
            shown = $"clicked {ctx?.ClickedCell} (want {c}); option '{o?.Label}'{(o != null && o.Disabled ? " (disabled)" : "")}; all: {string.Join(" | ", opts.Select(x => x.Label + (x.Disabled ? " (disabled)" : "")))}";
            return o;
        }

        // The player's right-click order: the option chosen when enabled. True: ordered.
        private static bool FoForce(Pawn p, IntVec3 c, out string shown)
        {
            FloatMenuOption o = FoOption(p, c, out shown);
            if (o == null || o.Disabled)
            {
                return false;
            }
            o.Chosen(true, null);
            shown += $"; job {p.CurJobDef?.defName} -> {p.CurJob?.targetA} (spot {p.CurJob?.targetB}), forced {p.CurJob?.playerForced}";
            return true;
        }

        // Per tick: the first designated cell taken up, and the worker never on the cell it is taking up.
        private static bool FoWatch(Map map, Func<bool> done)
        {
            foTick++;
            if (!foFirst.IsValid)
            {
                foreach (IntVec3 c in foCells)
                {
                    if (!ProjectZ.HasFloor(c, map))
                    {
                        foFirst = c;
                        foFirstTick = foTick;
                        break;
                    }
                }
            }
            Job j = r2P.CurJob;
            if (j != null && (j.def == JobDefOf.RemoveFoundation || j.def == JobDefOf.RemoveFloor) && !r2P.pather.Moving && r2P.Position == j.targetA.Cell)
            {
                foOnCell++;
            }
            return done();
        }

        private static string FoDist(Map map, IntVec3 c) => $"{c} dist {SlabPeel.For(map)?.Distance(c)}, farthest designated {foCells.Select(x => SlabPeel.For(map)?.Distance(x) ?? -1).DefaultIfEmpty(-1).Max()}, workable (automatic) {SlabPeel.For(map)?.Workable(c)}";

        private static int FoLeft(Map map) => foCells.Count(c => ProjectZ.HasFloor(c, map));

        private static bool StepSlabForceInner(Map map)
        {
            switch (stage)
            {
                case "fo_setup":
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
                        Result("slabforce: a map with 3 levels", false, $"levels {Levels.Min(map)}..{Levels.Max(map)} (run with -projectzfloors=3)");
                        Finish();
                        return true;
                    }
                    r2Area = new CellRect(map.Center.x - 22, map.Center.z - 16, 44, 32);
                    List<Pawn> cols = Colonists(map);
                    r2P = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) ?? cols.First();
                    r2Q = cols.FirstOrDefault(p => p != r2P);
                    foreach (Pawn p in cols)
                    {
                        p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
                    }
                    Info($"slabforce: area {r2Area}, worker {r2P}, second colonist {r2Q}");
                    stage = "fo_1";
                    return true;
                }

                // ---- F1: a solid floor, a forced order on an inner cell
                case "fo_1":
                {
                    FoFixture(map, new[] { new CellRect(FoG(10, 10).x, FoG(10, 10).z, 11, 7) }, true, FoG(26, 13), g => true);
                    foTarget = FoL(15, 13, 1, map);
                    foNote = FoDist(map, foTarget);
                    foOk = FoForce(r2P, foTarget, out string shown);
                    foNote += "; " + shown;
                    SeedTicks("fo_1");
                    TickUntil(6000, () => FoWatch(map, () => !ProjectZ.HasFloor(foTarget, map)), "fo_1_done", 1);
                    return true;
                }
                case "fo_1_done":
                {
                    tickWatch = null;
                    Result("F1 solid floor: the menu offers the forced order on an inner cell (not the farthest) and the worker takes that cell up first, standing next to it",
                        foOk && !ProjectZ.HasFloor(foTarget, map) && foFirst == foTarget && FoLeft(map) == foCells.Count - 1 && foOnCell == 0 && r2P.Position.AdjacentToCardinal(foTarget),
                        $"{foNote}; after {foTick} ticks taken up {!ProjectZ.HasFloor(foTarget, map)}, first {foFirst}, designated left {FoLeft(map)}/{foCells.Count}, ticks on the cell {foOnCell}, worker at {r2P.Position}; errors {errors - foErrAt}");
                    stage = "fo_2a";
                    return true;
                }

                // ---- F2: the L-strip of the issue
                case "fo_2a":
                {
                    FoFixture(map, FoLStrip(), true, FoG(26, 13), g => true);
                    foNote = $"near {FoDist(map, FoL(19, 13, 1, map))}; far {FoDist(map, FoL(12, 19, 1, map))}";
                    SeedTicks("fo_2a");
                    TickUntil(8000, () => FoWatch(map, () => foFirst.IsValid), "fo_2a_done", 1);
                    return true;
                }
                case "fo_2a_done":
                {
                    tickWatch = null;
                    Result("F2 L-strip, automatic work (no order): the far end is taken up first, as before",
                        foFirst == FoL(12, 19, 1, map) && FoLeft(map) == foCells.Count - 1,
                        $"{foNote}; first {foFirst} after {foFirstTick} ticks, designated left {FoLeft(map)}/{foCells.Count}");
                    stage = "fo_2b";
                    return true;
                }
                case "fo_2b":
                {
                    FoFixture(map, FoLStrip(), true, FoG(26, 13), g => true);
                    foTarget = FoL(19, 13, 1, map);
                    foNote = FoDist(map, foTarget);
                    foOk = FoForce(r2P, foTarget, out string shown);
                    foNote += "; " + shown;
                    SeedTicks("fo_2b");
                    TickUntil(6000, () => FoWatch(map, () => !ProjectZ.HasFloor(foTarget, map)), "fo_2b_done", 1);
                    return true;
                }
                case "fo_2b_done":
                {
                    tickWatch = null;
                    Result("F2 L-strip, a forced order on the near end: offered (no 'farther first'), taken up first",
                        foOk && foFirst == foTarget && FoLeft(map) == foCells.Count - 1 && foOnCell == 0,
                        $"{foNote}; first {foFirst} after {foFirstTick} ticks, designated left {FoLeft(map)}/{foCells.Count}, ticks on the cell {foOnCell}, worker at {r2P.Position}");
                    SeedTicks("fo_2b_after");
                    StartTicks(1500, "fo_2b_after");
                    return true;
                }
                case "fo_2b_after":
                {
                    int marked = foCells.Count(c => map.designationManager.DesignationAt(c, DesignationDefOf.RemoveFoundation) != null);
                    Result("F2 the strip cut off by it is left alone by automatic work (no stairs reach it), 1500 ticks, no errors",
                        FoLeft(map) == foCells.Count - 1 && marked == foCells.Count - 1 && errors == foErrAt && r2P.Spawned,
                        $"designated left {FoLeft(map)}/{foCells.Count} (marked {marked}), errors {errors - foErrAt}, worker at {r2P.Position} job {r2P.CurJobDef?.defName}");
                    stage = "fo_3";
                    return true;
                }

                // ---- F3: a forced order that cuts the floor off
                case "fo_3":
                {
                    FoFixture(map, new[] { new CellRect(FoG(11, 13).x, FoG(11, 13).z, 10, 1), new CellRect(FoG(5, 10).x, FoG(5, 10).z, 6, 7) }, true, FoG(26, 13), g => true);
                    // (the worker and a drafted colonist on the far block, steel there)
                    Teleport(r2P, FoL(7, 13, 1, map));
                    Draft(r2P, false);
                    if (r2Q != null)
                    {
                        Teleport(r2Q, FoL(6, 11, 1, map));
                        Draft(r2Q, true);
                    }
                    foSteel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    foSteel.stackCount = 20;
                    GenSpawn.Spawn(foSteel, FoL(8, 15, 1, map), map);
                    foTarget = FoL(15, 13, 1, map);
                    foNote = FoDist(map, foTarget);
                    foOk = FoForce(r2P, foTarget, out string shown);
                    foNote += "; " + shown;
                    SeedTicks("fo_3");
                    TickUntil(6000, () => FoWatch(map, () => !ProjectZ.HasFloor(foTarget, map)), "fo_3_done", 1);
                    return true;
                }
                case "fo_3_done":
                {
                    tickWatch = null;
                    Result("F3 a forced order in the strip's middle (cuts the block off, the worker starting on it): offered and taken up first",
                        foOk && foFirst == foTarget && FoLeft(map) == foCells.Count - 1 && foOnCell == 0,
                        $"{foNote}; first {foFirst} after {foFirstTick} ticks, designated left {FoLeft(map)}/{foCells.Count}, ticks on the cell {foOnCell}, worker at {r2P.Position}");
                    SeedTicks("fo_3_after");
                    StartTicks(2000, "fo_3_after");
                    return true;
                }
                case "fo_3_after":
                {
                    IntVec3 q = r2Q?.Position ?? IntVec3.Invalid;
                    // (the cut-off part: west of the cell taken up; the strip on the stairs side is still worked by automatic work)
                    List<IntVec3> cut = foCells.Where(c => Levels.Ground(c, map).x < FoG(15, 13).x).ToList();
                    int cutLeft = cut.Count(c => ProjectZ.HasFloor(c, map) && map.designationManager.DesignationAt(c, DesignationDefOf.RemoveFoundation) != null);
                    Result("F3 the game runs on 2000 ticks without errors: the cut-off part stays (designated, unworked), the colonist and the steel on it stay, the worker on a floor",
                        cutLeft == cut.Count && errors == foErrAt && (r2Q == null || r2Q.Spawned && Levels.LevelOf(q, map) == 1 && q.Standable(map)) && foSteel.Spawned && Levels.LevelOf(foSteel.Position, map) == 1
                        && r2P.Spawned && (ProjectZ.HasFloor(r2P.Position, map) || Levels.LevelOf(r2P.Position, map) == 0),
                        $"cut-off part left {cutLeft}/{cut.Count} (all designated {FoLeft(map)}/{foCells.Count}), errors {errors - foErrAt}, colonist {r2Q} at {q} (level {(q.IsValid ? Levels.LevelOf(q, map) : -1)}), steel at {foSteel.PositionHeld}, worker at {r2P.Position} (level {Levels.LevelOf(r2P.Position, map)}) job {r2P.CurJobDef?.defName}");
                    if (r2Q != null)
                    {
                        Draft(r2Q, false);
                    }
                    stage = "fo_4a";
                    return true;
                }

                // ---- F4: the top of a ladder, the platform's only way (K2)
                case "fo_4a":
                {
                    FoLadderFixture(map);
                    IntVec3 top = FoL(20, 13, 1, map);
                    Patches.RouteGraph g = Patches.RouteGraph.For(map);
                    int link = g?.LinkOfTop(top) ?? -1;
                    foNote = $"ladder {foLadder} top {foLadder?.top}; designated {foCells.Contains(top)}; K2 cut side {(link >= 0 ? g.CutSide(link) : 0)}; {FoDist(map, top)}";
                    foTarget = top;
                    SeedTicks("fo_4a");
                    StartTicks(2500, "fo_4a_done");
                    return true;
                }
                case "fo_4a_done":
                {
                    Result("F4 a platform's only ladder lands on a designated slab: automatic work refuses it (K2 / peel), 2500 ticks",
                        foLadder != null && foCells.Contains(foTarget) && ProjectZ.HasFloor(foTarget, map) && errors == foErrAt,
                        $"{foNote}; still there {ProjectZ.HasFloor(foTarget, map)}, worker at {r2P.Position} job {r2P.CurJobDef?.defName}, errors {errors - foErrAt}");
                    stage = "fo_4b";
                    return true;
                }
                case "fo_4b":
                {
                    FoLadderFixture(map);
                    foTarget = FoL(20, 13, 1, map);
                    foNote = $"ladder {foLadder}; designated {foCells.Contains(foTarget)}; {FoDist(map, foTarget)}";
                    foOk = FoForce(r2P, foTarget, out string shown);
                    foNote += "; " + shown;
                    SeedTicks("fo_4b");
                    TickUntil(8000, () => FoWatch(map, () => !ProjectZ.HasFloor(foTarget, map)), "fo_4b_done", 1);
                    return true;
                }
                case "fo_4b_done":
                {
                    tickWatch = null;
                    Result("F4 a forced order on the ladder's top cell: offered and taken up (the worker stands next to it on the platform)",
                        foOk && !ProjectZ.HasFloor(foTarget, map) && foOnCell == 0,
                        $"{foNote}; after {foTick} ticks taken up {!ProjectZ.HasFloor(foTarget, map)}, ticks on the cell {foOnCell}, worker at {r2P.Position} (level {Levels.LevelOf(r2P.Position, map)})");
                    SeedTicks("fo_4b_after");
                    StartTicks(1000, "fo_4b_after");
                    return true;
                }
                case "fo_4b_after":
                {
                    Result("F4 the game runs on 1000 ticks without errors; the worker is left on the platform (the player's choice)",
                        errors == foErrAt && r2P.Spawned && r2P.Position.Standable(map) && Levels.LevelOf(r2P.Position, map) == 1,
                        $"errors {errors - foErrAt}, worker at {r2P.Position} (level {Levels.LevelOf(r2P.Position, map)}) job {r2P.CurJobDef?.defName}; ladder spawned {foLadder?.Spawned}");
                    stage = "fo_5a";
                    return true;
                }

                // ---- F5: the physical rules stand
                case "fo_5a":
                {
                    foTarget = FoL(15, 13, 1, map);
                    FoFixture(map, new[] { new CellRect(FoG(10, 10).x, FoG(10, 10).z, 11, 7) }, true, FoG(26, 13), g => g == FoG(15, 13));
                    Thing table = Place(DefDatabase<ThingDef>.GetNamed("EndTable"), ThingDefOf.WoodLog, foTarget, map);
                    FloatMenuOption o = FoOption(r2P, foTarget, out string shown);
                    foOk = (o == null || o.Disabled) && foCells.Contains(foTarget);
                    foNote = $"table {table}; blocker '{Slab.Blocker(foTarget, map)}'; {shown}";
                    if (o != null && !o.Disabled)
                    {
                        o.Chosen(true, null);
                    }
                    SeedTicks("fo_5a");
                    StartTicks(1500, "fo_5a_done");
                    return true;
                }
                case "fo_5a_done":
                {
                    Result("F5 a table on the designated slab: no forced order (the physical rule), the slab stays after 1500 ticks",
                        foOk && ProjectZ.HasFloor(foTarget, map) && errors == foErrAt,
                        $"{foNote}; still there {ProjectZ.HasFloor(foTarget, map)}, errors {errors - foErrAt}");
                    stage = "fo_5b";
                    return true;
                }
                case "fo_5b":
                {
                    FoFixture(map, new[] { new CellRect(FoG(10, 10).x, FoG(10, 10).z, 11, 7) }, true, FoG(26, 13), g => true);
                    R2Floor(map, new CellRect(FoG(13, 11).x, FoG(13, 11).z, 5, 5), 2);
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    foTarget = FoL(15, 13, 1, map);
                    foNote = FoDist(map, foTarget) + $"; floor above {ProjectZ.HasFloor(FoL(15, 13, 2, map), map)}";
                    foOk = FoForce(r2P, foTarget, out string shown);
                    foNote += "; " + shown;
                    SeedTicks("fo_5b");
                    TickUntil(6000, () => FoWatch(map, () => !ProjectZ.HasFloor(foTarget, map)), "fo_5b_done", 1);
                    return true;
                }
                case "fo_5b_done":
                {
                    tickWatch = null;
                    IntVec3 up = FoL(15, 13, 2, map);
                    Result("F5 a slab with a slab above (allowed since Q6 (b)): the forced order takes it up, the floor above stays on its roof",
                        foOk && foFirst == foTarget && ProjectZ.HasFloor(up, map) && map.roofGrid.RoofAt(foTarget) == PZDefOf.PZ_FloorRoof && errors == foErrAt,
                        $"{foNote}; first {foFirst} after {foFirstTick} ticks, floor above {ProjectZ.HasFloor(up, map)}, roof at the hole {map.roofGrid.RoofAt(foTarget)?.defName ?? "none"}, errors {errors - foErrAt}");
                    stage = "fo_5c";
                    return true;
                }
                case "fo_5c":
                {
                    // (one slab cell with open air all round it, next to the floor reached from the stairs)
                    foTarget = FoL(15, 20, 1, map);
                    FoFixture(map, new[] { new CellRect(FoG(10, 10).x, FoG(10, 10).z, 11, 7), new CellRect(FoG(15, 20).x, FoG(15, 20).z, 1, 1) }, true, FoG(26, 13), g => g == FoG(15, 20));
                    FloatMenuOption o = FoOption(r2P, foTarget, out string shown);
                    foOk = (o == null || o.Disabled) && foCells.Contains(foTarget);
                    foNote = $"{FoDist(map, foTarget)}; work spot {SlabPeel.WorkSpot(r2P, foTarget)}; {shown}";
                    if (o != null && !o.Disabled)
                    {
                        o.Chosen(true, null);
                    }
                    SeedTicks("fo_5c");
                    StartTicks(1500, "fo_5c_done");
                    return true;
                }
                case "fo_5c_done":
                {
                    Result("F5 a designated slab no one can stand next to: no forced order, it stays after 1500 ticks",
                        foOk && ProjectZ.HasFloor(foTarget, map) && errors == foErrAt,
                        $"{foNote}; still there {ProjectZ.HasFloor(foTarget, map)}, errors {errors - foErrAt}");
                    stage = "fo_6a";
                    return true;
                }

                // ---- F6: ship upper decks in the same peel order
                case "fo_6a":
                {
                    if (!DkFixture(map, 3, DkBlockA().Concat(DkBlockB()), false))
                    {
                        stage = "fo_end";
                        return true;
                    }
                    SeedTicks("fo_6a");
                    TickUntil(DkMaxTicks, () => DkWatch(map), "fo_6a_done", 1);
                    return true;
                }
                case "fo_6a_done":
                {
                    tickWatch = null;
                    Result("F6a two 3 x 3 deck blocks designated (far corner, inner block): colonists take every cell up within the bound, none left stranded",
                        DkLeft(map) == 0 && foOnCell == 0 && errors == foErrAt,
                        $"{DkReport(map)}; block A left {DkBlockA().Count(g => ShipDeck.At(Levels.Cell(g, 1, map), map))}/9, block B left {DkBlockB().Count(g => ShipDeck.At(Levels.Cell(g, 1, map), map))}/9; errors {errors - foErrAt}");
                    Result("F6a the decks were taken up farthest from the stairs first (the slabs' order)",
                        dkOrderBad == 0 && DkLeft(map) == 0,
                        $"{dkOrderBad} cells taken while a farther designated deck waited; order {dkOrder}");
                    stage = "fo_6b";
                    return true;
                }
                case "fo_6b":
                {
                    if (!DkFixture(map, 1, DkBlockB(), false))
                    {
                        stage = "fo_end";
                        return true;
                    }
                    foTarget = FoL(12, 13, 1, map);
                    dkForced = foTarget;
                    foNote = FoDist(map, foTarget);
                    foOk = FoForce(r2P, foTarget, out string shown);
                    foNote += "; " + shown;
                    SeedTicks("fo_6b");
                    TickUntil(DkMaxTicks, () => DkWatch(map), "fo_6b_done", 1);
                    return true;
                }
                case "fo_6b_done":
                {
                    tickWatch = null;
                    Result("F6b inner deck block, a forced order on its middle cell (not the farthest): offered and taken up first, the worker never on it",
                        foOk && foFirst == foTarget && foOnCell == 0,
                        $"{foNote}; first {foFirst} after {foFirstTick} ticks, ticks on the cell {foOnCell}, worker at {r2P.Position}");
                    Result("F6b then automatic work takes the other eight up, none left stranded",
                        DkLeft(map) == 0 && errors == foErrAt,
                        $"{DkReport(map)}; errors {errors - foErrAt}");
                    stage = "fo_6c";
                    return true;
                }
                case "fo_6c":
                {
                    if (!DkFixture(map, 3, DkBlockB(), true))
                    {
                        stage = "fo_end";
                        return true;
                    }
                    dkCollapsesAt = PZStats.slabCollapses;
                    SeedTicks("fo_6c");
                    TickUntil(DkMaxTicks, () => DkWatch(map), "fo_6c_done", 1);
                    return true;
                }
                case "fo_6c_done":
                {
                    tickWatch = null;
                    IntVec3 hole = FoL(12, 13, 1, map), up = FoL(12, 13, 2, map);
                    Result("F6c inner deck block under a kept level-2 deck (Q6 (b)): colonists take all nine up, none left stranded",
                        DkLeft(map) == 0 && foOnCell == 0 && errors == foErrAt,
                        $"{DkReport(map)}; errors {errors - foErrAt}");
                    Result("F6c the level-2 deck stays over the hole: a ship column hole with the floor roof under the deck, nothing collapsed",
                        !ShipDeck.At(hole, map) && ShipDeck.At(up, map) && HullCompat.Hole(hole, map) && map.roofGrid.RoofAt(hole) == PZDefOf.PZ_FloorRoof && PZStats.slabCollapses == dkCollapsesAt,
                        $"hole {hole}: deck {ShipDeck.At(hole, map)}, hole {HullCompat.Hole(hole, map)}, roof {map.roofGrid.RoofAt(hole)?.defName ?? "none"}; deck above {ShipDeck.At(up, map)}; collapses {PZStats.slabCollapses - dkCollapsesAt}");
                    stage = "fo_6d";
                    return true;
                }
                case "fo_6d":
                {
                    if (!DkFixture(map, 2, DkCorridor(), false, corridor: true))
                    {
                        stage = "fo_end";
                        return true;
                    }
                    SeedTicks("fo_6d");
                    TickUntil(DkMaxTicks, () => DkWatch(map), "fo_6d_done", 1);
                    return true;
                }
                case "fo_6d_done":
                {
                    tickWatch = null;
                    IntVec3 end = FoL(18, 13, 1, map);
                    Result("F6d one-wide dead-end deck corridor, a chunk on its second-to-last cell (walked over, not stood on): the end cell is taken up first, then the rest, as slabs are",
                        dkChunkOk && DkLeft(map) == 0 && foFirst == end && dkOrderBad == 0 && foOnCell == 0 && errors == foErrAt,
                        $"{dkNote}; first {foFirst} (the end {end}) after {foFirstTick} ticks, order{dkOrder} ({dkOrderBad} out of order); {DkReport(map)}; chunk now at {dkChunk?.PositionHeld} (spawned {dkChunk?.Spawned}); errors {errors - foErrAt}");
                    stage = "fo_end";
                    return true;
                }
                case "fo_end":
                    Finish();
                    return true;
            }
            return false;
        }

        // The L-strip: (12..20, 13) from the stairs exit west, then (12, 14..19) north.
        private static CellRect[] FoLStrip() => new[] { new CellRect(FoG(12, 13).x, FoG(12, 13).z, 9, 1), new CellRect(FoG(12, 14).x, FoG(12, 14).z, 1, 6) };

        // A level-1 platform (20..29, 8..19) reached only by a player ladder from (19, 13) on the ground to its top (20, 13),
        // the top cell designated; the worker on the ground.
        private static void FoLadderFixture(Map map)
        {
            FoFixture(map, new[] { new CellRect(FoG(20, 8).x, FoG(20, 8).z, 10, 12) }, false, FoG(15, 13), g => g == FoG(20, 13));
            foLadder = LadderSite.Spawn(FoG(19, 13), FoL(20, 13, 1, map), Faction.OfPlayer, map, false);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            IntVec3 top = FoL(20, 13, 1, map);
            Info($"slabforce: ladder {foLadder} at {foLadder?.Position} top {foLadder?.top}; top designated {foCells.Contains(top)}, blocker '{Slab.Blocker(top, map)}'");
        }

        // ---- F6 fixture: the ship. Origin (6, 8) (ground cells 6..18 x 8..18), ship stairs at (7, 11) facing north (ramp
        // 7 x 10..12, solid end (7, 13) under the exit, level 1), decks on level 1 over the whole ship but the stairwell.
        // Block A: the deck's far corner (16..18, 16..18); block B: an inner block (11..13, 12..14), its middle (12, 13).
        private const int DkMaxTicks = 40000;    // the bound for a block's removal (the probe saw cells stuck after 40000)
        private const int DkIdleTicks = 3000;    // no worker on a removal job this long: the rest is stuck, stop early
        private static readonly List<Pawn> dkWorkers = new List<Pawn>();
        private static readonly Dictionary<IntVec3, int> dkDist = new Dictionary<IntVec3, int>();
        private static readonly HashSet<IntVec3> dkGone = new HashSet<IntVec3>();
        private static IntVec3 dkForced = IntVec3.Invalid;
        private static int dkOrderBad, dkLastBusy, dkCollapsesAt;
        private static string dkOrder = "", dkNote = "";
        private static Thing dkChunk;
        private static bool dkChunkOk;

        private static IEnumerable<IntVec3> DkBlock(int x0, int z0) => new CellRect(FoG(x0, z0).x, FoG(x0, z0).z, 3, 3).Cells;

        private static IEnumerable<IntVec3> DkBlockA() => DkBlock(16, 16);

        private static IEnumerable<IntVec3> DkBlockB() => DkBlock(11, 12);

        // The corridor (F6d): level 1 has no deck east of x 13 but the row (14..18, 13), a dead end one cell wide; the
        // chunk lies on (17, 13).
        private static IEnumerable<IntVec3> DkCorridor() => new CellRect(FoG(14, 13).x, FoG(14, 13).z, 5, 1).Cells;

        private static int DkLeft(Map map) => foCells.Count(c => ShipDeck.At(c, map));

        // The fixture: the area cleared (foundations too: R2Clear keeps them), the ship built, the designated ground
        // cells' level-1 decks marked with the vanilla tool, n workers (Construction only) on the ship's ground floor.
        // upperDeck: a level-2 deck over block B's middle, its roof held by a wall on the level-1 deck. corridor: the deck
        // east of x 13 is only the corridor row, a chunk on its second-to-last cell (not marked for hauling: it stays).
        private static bool DkFixture(Map map, int n, IEnumerable<IntVec3> designate, bool upperDeck, bool corridor = false)
        {
            if (!ModsConfig.OdysseyActive || PZDefOf.PZ_ShipDeck == null || PZDefOf.PZ_ShipStairs == null)
            {
                Result("F6 ship decks: Odyssey loaded", false, $"Odyssey {ModsConfig.OdysseyActive}, deck def {PZDefOf.PZ_ShipDeck?.defName}");
                return false;
            }
            R2Clear(map);
            CellRect big = r2Area.ExpandedBy(3).ClipInsideMap(map);
            for (int L = Levels.Max(map); L >= Levels.Min(map); L--)
            {
                foreach (IntVec3 g in big)
                {
                    IntVec3 c = Levels.Cell(g, L, map);
                    if (c.IsValid && c.InBounds(map) && map.terrainGrid.FoundationAt(c) != null)
                    {
                        map.terrainGrid.RemoveFoundation(c, false);
                    }
                }
            }
            for (int L = Levels.Max(map); L >= Levels.Min(map); L--)
            {
                foreach (IntVec3 g in big)
                {
                    IntVec3 c = Levels.Cell(g, L, map);
                    if (c.IsValid && c.InBounds(map))
                    {
                        map.roofGrid.SetRoof(c, null);
                    }
                }
            }
            map.roofCollapseBuffer.Clear();
            foreach (Designation d in map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.RemoveFoundation).ToList())
            {
                d.Delete();
            }
            dkWorkers.Clear();
            dkWorkers.AddRange(Colonists(map).Where(p => !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)).OrderBy(p => p == r2P ? 0 : 1).ThenBy(p => p.thingIDNumber).Take(n));
            if (dkWorkers.Count == 0 || dkWorkers[0] != r2P)
            {
                Result("F6 ship decks: workers", false, $"{dkWorkers.Count} (the forced order's worker {r2P} first)");
                return false;
            }
            R2Park(map, dkWorkers.ToArray());
            R2Feed(map);
            LinkSteps.For(map)?.steps.Clear();
            Find.ResearchManager.gravEngineInspected = true;
            IntVec3 o = FoG(6, 8);
            BuildShip(map, o, 13, 11);
            Building_GravEngine e = GlEngine(map);
            if (e != null)
            {
                e.nameHidden = false;
            }
            IntVec3 st = FoG(7, 11);
            HashSet<IntVec3> well = new HashSet<IntVec3>(StairShape.Ramp(st, Rot4.North));
            for (int x = 0; x < 13; x++)
            {
                for (int z = 0; z < 11; z++)
                {
                    IntVec3 g = o + new IntVec3(x, 0, z);
                    if (corridor && x >= 8 && z != 5)
                    {
                        continue;
                    }
                    if (!well.Contains(g))
                    {
                        map.terrainGrid.SetTerrain(Levels.Cell(g, 1, map), PZDefOf.PZ_ShipDeck);
                    }
                }
            }
            foreach (IntVec3 g in well)
            {
                map.roofGrid.SetRoof(g, null);
            }
            AcceptanceReport stOk = ShipStairs.Check(st, Rot4.North, map);
            Building_Stairs stairs = Place(PZDefOf.PZ_ShipStairs, null, st, map, Rot4.North) as Building_Stairs;
            if (upperDeck)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, FoL(12, 16, 1, map), map);
                map.terrainGrid.SetTerrain(FoL(12, 13, 2, map), PZDefOf.PZ_ShipDeck);
            }
            dkChunk = null;
            dkChunkOk = false;
            dkNote = "";
            if (corridor)
            {
                IntVec3 at = FoL(17, 13, 1, map);
                dkChunk = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.ChunkSlagSteel), at, map);
                dkChunkOk = dkChunk.Position == at && at.Walkable(map) && !at.Standable(map);
                dkNote = $"chunk {dkChunk} at {dkChunk.Position} (walkable {at.Walkable(map)}, standable {at.Standable(map)}, marked for hauling {map.designationManager.DesignationOn(dkChunk, DesignationDefOf.Haul) != null})";
            }
            e?.ForceSubstructureDirty();
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            StairGrid sg = StairGrid.For(map);
            bool usable = stairs != null && sg != null && sg.links.Any(l => l.stairs == stairs && sg.Usable(l));
            foCells.Clear();
            Designator_RemoveFoundation tool = new Designator_RemoveFoundation();
            List<string> refused = new List<string>();
            FloorView.Set(1);
            foreach (IntVec3 g in designate)
            {
                IntVec3 c = Levels.Cell(g, 1, map);
                AcceptanceReport r = tool.CanDesignateCell(c);
                if (r.Accepted)
                {
                    tool.DesignateSingleCell(c);
                    foCells.Add(c);
                }
                else
                {
                    refused.Add($"{c} '{r.Reason}'");
                }
            }
            FloorView.Set(0);
            dkDist.Clear();
            SlabPeel peel = SlabPeel.For(map);
            foreach (IntVec3 c in foCells)
            {
                dkDist[c] = peel?.Distance(c) ?? -1;
            }
            for (int i = 0; i < dkWorkers.Count; i++)
            {
                Pawn w = dkWorkers[i];
                IntVec3 at = o + new IntVec3(2 + i, 0, 1);
                Teleport(w, at);
                Draft(w, false);
                R2Work(w, WorkTypeDefOf.Construction);
            }
            dkGone.Clear();
            dkForced = IntVec3.Invalid;
            dkOrderBad = 0;
            dkOrder = "";
            dkLastBusy = 0;
            foFirst = IntVec3.Invalid;
            foFirstTick = -1;
            foTick = 0;
            foOnCell = 0;
            foErrAt = errors;
            foNote = "";
            bool ok = stairs != null && usable && refused.Count == 0 && foCells.Count == designate.Count();
            Info($"F6 fixture: ship {o} engine {e?.Position}, ship stairs {stairs?.Position} (check '{(stOk.Accepted ? "accepted" : stOk.Reason)}', link usable {usable}, exit {stairs?.TopCell}), designated {foCells.Count} (refused {string.Join(", ", refused)}), distances {string.Join(" ", foCells.Select(c => dkDist[c]))}, workers {string.Join(", ", dkWorkers)}, level-2 deck {upperDeck}");
            if (!ok)
            {
                Result("F6 ship decks: fixture (ship stairs up to the deck, link usable, every block cell designated)", false, $"stairs {stairs}, usable {usable}, refused {string.Join(", ", refused)}");
            }
            return ok;
        }

        // Per tick: the workers never on the deck they take up; each cell gone, in order (none while a farther designated
        // deck waited, the forced cell aside); done when all are gone or no one has worked for DkIdleTicks.
        private static bool DkWatch(Map map)
        {
            foTick++;
            foreach (Pawn w in dkWorkers)
            {
                Job j = w.CurJob;
                if (j != null && (j.def == JobDefOf.RemoveFoundation || j.def == JobDefOf.RemoveFloor))
                {
                    dkLastBusy = foTick;
                    if (!w.pather.Moving && w.Position == j.targetA.Cell)
                    {
                        foOnCell++;
                    }
                }
            }
            foreach (IntVec3 c in foCells)
            {
                if (dkGone.Contains(c) || ShipDeck.At(c, map))
                {
                    continue;
                }
                dkGone.Add(c);
                if (!foFirst.IsValid)
                {
                    foFirst = c;
                    foFirstTick = foTick;
                }
                int far = foCells.Where(x => !dkGone.Contains(x) && ShipDeck.At(x, map)).Select(x => dkDist[x]).DefaultIfEmpty(-1).Max();
                if (c != dkForced && dkDist[c] < far)
                {
                    dkOrderBad++;
                }
                dkOrder += $" {dkDist[c]}";
            }
            return DkLeft(map) == 0 || foTick - dkLastBusy > DkIdleTicks;
        }

        // What is left: the cells still designated and whether anyone could stand next to them (stranded: no cell to stand
        // on, ShipDeck.WorkSpot).
        private static string DkReport(Map map)
        {
            List<IntVec3> left = foCells.Where(c => ShipDeck.At(c, map)).ToList();
            List<IntVec3> stranded = left.Where(c => !ShipDeck.WorkSpot(dkWorkers[0], c).IsValid).ToList();
            return $"{foCells.Count - left.Count}/{foCells.Count} taken up after {foTick} ticks (last work at {dkLastBusy}); left {string.Join(" ", left)}; stranded (no cell to stand on) {stranded.Count}: {string.Join(" ", stranded)}; ticks a worker stood on its cell {foOnCell}";
        }
    }
}
