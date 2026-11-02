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
            Rand.PushState((int)(StableHash("slabforce_" + stage) ^ (uint)(SeedArg ?? 717)));
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
    }
}
