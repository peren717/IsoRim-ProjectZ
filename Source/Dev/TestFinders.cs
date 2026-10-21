using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using ProjectZ.Patches;

namespace ProjectZ.Dev
{
    // Mode finders (compat G1; run with -projectzfloors=3 -projectzmapsize=120; -projectzfindersoff: the finders as
    // vanilla, to show the old behaviour). Scene: platform P1 (30 x 30) on floor 2 over a roofed, columned ground area
    // (stairs up at its south), a room R on it (walls, a door), platform P2 on floor 3 over P1 (columns on P1, stairs up
    // from P1); a cleared strip from the scene to the ground's north edge. A fixed world seed (as mtn/tallmtn); every
    // check below starts with FdBegin (its own seed, colonists back home, pawns of earlier checks removed).
    //   C1 the finders, many calls each, centres on the ground, on floor 2 and on floor 3: the expanding search round a
    //      centre (TryFindRandomCellNearWith, TryFindRandomCellsNear, TryFindRandomClearCellsNear, near the map centre)
    //      stays on the centre's floor; outside the colony near the centre: the ground; exits (best, random) and the edge
    //      away from the colony: ground edges, the ground's north row used; two caravans' starting cells: the ground;
    //      Anomaly's large structures: the ground.
    //   C2 a visitor group, two of them sent up to floor 2 and floor 3, leaves the map (exit lord): every pawn leaves
    //      from a ground edge cell.
    //   C3 thrumbos pass: their target near the centre is on the ground; when their time is up they leave by ground
    //      edges.
    //   C4 an edge walk-in raid aimed at a wall on floor 2: it enters at a ground edge.
    //   C5 a pit gate incident (Anomaly): the gate's spawner covers ground cells only.
    public static partial class TestRunner
    {
        private static IntVec3 fdO, fdStairs1, fdStairs2;
        private static CellRect fdP1, fdP2, fdRoom, fdStrip;
        private static Thing fdTargetWall;
        private static readonly HashSet<string> fdTracked = new HashSet<string>();
        private static readonly List<string> fdExits = new List<string>();
        private static int fdExitBad, fdExitCount;

        private static IntVec3 FdA(int dx, int dz) => new IntVec3(fdO.x + dx, 0, fdO.z + dz);

        private static IntVec3 FdOn(IntVec3 g, int k, Map map) => k == 0 ? g : ProjectZ.CellOnFloor(g, k, map);

        private static string FdCell(IntVec3 c, Map map) => $"{c} lv {Levels.LevelOf(c, map)}";

        // A ground edge cell (the ground's north edge is row groundZ - 1).
        private static bool FdGroundEdge(IntVec3 c, Map map)
        {
            int gz = ProjectZ.GroundZ(map);
            return c.InBounds(map) && c.z < gz && (c.x == 0 || c.x == map.Size.x - 1 || c.z == 0 || c.z == gz - 1);
        }

        // Test hook (mode finders): where each tracked pawn leaves the map.
        private static void FdExitPrefix(Pawn __instance)
        {
            if (__instance?.Map == null || !fdTracked.Contains(__instance.ThingID))
            {
                return;
            }
            fdExitCount++;
            bool ok = FdGroundEdge(__instance.Position, __instance.Map);
            if (!ok)
            {
                fdExitBad++;
            }
            if (!ok || fdExits.Count < 6)
            {
                fdExits.Add(__instance.LabelShort + " " + FdCell(__instance.Position, __instance.Map) + (ok ? "" : " NOT A GROUND EDGE"));
            }
        }

        private static readonly HashSet<string> fdBaseline = new HashSet<string>();
        private static readonly Dictionary<Pawn, IntVec3> fdHome = new Dictionary<Pawn, IntVec3>();

        // Each check starts from the same state: its own fixed seed (for its synchronous part: the incident, the finder
        // calls; ticks after it run on the game's rand), the colonists back at their spots, every pawn an earlier check
        // brought (visitors, thrumbos, raiders, corpses) gone. The scene itself is never changed by a check.
        private static void FdBegin(Map map, int check)
        {
            foreach (Thing t in map.listerThings.AllThings.Where(t => t is Corpse || t is Pawn p && !fdBaseline.Contains(p.ThingID)).ToList())
            {
                t.Destroy();
            }
            foreach (KeyValuePair<Pawn, IntVec3> kv in fdHome)
            {
                if (kv.Key.Spawned && kv.Key.Position != kv.Value)
                {
                    Teleport(kv.Key, kv.Value);
                }
            }
            FdTrackReset();
            FdEnd();
            Rand.PushState(7170 + check);
            fdSeeded = true;
        }

        private static bool fdSeeded;

        private static void FdEnd()
        {
            if (fdSeeded)
            {
                Rand.PopState();
                fdSeeded = false;
            }
        }

        private static void FdTrackReset()
        {
            fdTracked.Clear();
            fdExits.Clear();
            fdExitBad = 0;
            fdExitCount = 0;
        }

        // A check's seed holds for its own (synchronous) step: the game wants the rand stack empty between frames.
        private static bool StepFinders(Map map)
        {
            try
            {
                return StepFindersInner(map);
            }
            finally
            {
                FdEnd();
            }
        }

        private static bool StepFindersInner(Map map)
        {
            switch (stage)
            {
                case "fd_setup":
                    SetupFinders(map);
                    return true;
                case "fd_finders":
                    FdFinders(map);
                    stage = "fd_visitors";
                    return true;

                // ---- C2 visitors leave
                case "fd_visitors":
                {
                    FdBegin(map, 2);
                    List<Pawn> before = map.mapPawns.AllPawnsSpawned.ToList();
                    IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail("VisitorGroup");
                    bool fired = false;
                    List<Pawn> visitors = new List<Pawn>();
                    // (a group can be a single pawn: fire again until there are two; one lord for all of them below)
                    for (int i = 0; i < 4 && def != null && visitors.Count < 2; i++)
                    {
                        IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.Misc, map);
                        parms.forced = true;
                        parms.points = 1500f;
                        fired |= def.Worker.TryExecute(parms);
                        visitors = map.mapPawns.AllPawnsSpawned.Except(before).Where(p => p.RaceProps.Humanlike && p.Faction != null && !p.Faction.IsPlayer && !p.Faction.HostileTo(Faction.OfPlayer)).ToList();
                    }
                    if (!fired || visitors.Count < 2)
                    {
                        Result("C2 a visitor group arrives", false, $"fired {fired}, {visitors.Count} visitors");
                        stage = "fd_thrumbo";
                        return true;
                    }
                    Teleport(visitors[0], FdOn(fdRoom.CenterCell, 1, map));
                    Teleport(visitors[1], FdOn(FdA(15, 15), 2, map));
                    foreach (Pawn p in visitors)
                    {
                        fdTracked.Add(p.ThingID);
                        p.GetLord()?.RemovePawn(p);
                    }
                    foreach (IGrouping<Faction, Pawn> g in visitors.GroupBy(p => p.Faction))
                    {
                        LordMaker.MakeNewLord(g.Key, new LordJob_ExitMapBest(LocomotionUrgency.Walk, canDig: false), map, g.ToList());
                    }
                    Info($"C2 {visitors.Count} visitors ({visitors[0].Faction}) leave; {FdCell(visitors[0].Position, map)} and {FdCell(visitors[1].Position, map)} upstairs");
                    TickUntil(30000, () => !map.mapPawns.AllPawnsSpawned.Any(p => fdTracked.Contains(p.ThingID) && !p.Dead && !p.Downed), "fd_visitors_check", 60);
                    return true;
                }
                case "fd_visitors_check":
                {
                    List<Pawn> left = map.mapPawns.AllPawnsSpawned.Where(p => fdTracked.Contains(p.ThingID) && !p.Dead && !p.Downed).ToList();
                    Result("C2 the visitors (two sent up to floors 2 and 3) leave the map, each from a ground edge cell", left.Count == 0 && fdExitCount == fdTracked.Count && fdExitBad == 0,
                        $"{fdExitCount} of {fdTracked.Count} left, {fdExitBad} not from a ground edge; still here {string.Join(", ", left.Select(p => $"{p.LabelShort} {FdCell(p.Position, map)} {p.CurJobDef?.defName}"))}; {string.Join("; ", fdExits)}");
                    stage = "fd_thrumbo";
                    return true;
                }

                // ---- C3 thrumbos pass
                case "fd_thrumbo":
                {
                    FdBegin(map, 3);
                    List<Pawn> before = map.mapPawns.AllPawnsSpawned.ToList();
                    IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail("ThrumboPasses");
                    IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.Misc, map);
                    parms.forced = true;
                    bool fired = def != null && def.Worker.TryExecute(parms);
                    List<Pawn> th = map.mapPawns.AllPawnsSpawned.Except(before).Where(p => p.def == ThingDefOf.Thrumbo || p.kindDef == PawnKindDefOf.Thrumbo).ToList();
                    if (!fired || th.Count == 0)
                    {
                        Result("C3 thrumbos pass", false, $"fired {fired}, {th.Count} thrumbos");
                        stage = "fd_raid";
                        return true;
                    }
                    int goUp = th.Count(p => p.mindState.forcedGotoPosition.IsValid && ProjectZ.IsAnnex(p.mindState.forcedGotoPosition, map));
                    Result("C3 the thrumbos' goal near the map centre (outside the colony) is on the ground", goUp == 0,
                        $"{th.Count} thrumbos, goals {string.Join(" ", th.Select(p => p.mindState.forcedGotoPosition.IsValid ? FdCell(p.mindState.forcedGotoPosition, map) : "none"))}");
                    foreach (Pawn p in th)
                    {
                        fdTracked.Add(p.ThingID);
                        p.mindState.forcedGotoPosition = IntVec3.Invalid;
                        p.mindState.exitMapAfterTick = Find.TickManager.TicksGame + 10;
                        p.jobs?.EndCurrentJob(JobCondition.InterruptForced);
                    }
                    TickUntil(30000, () => !map.mapPawns.AllPawnsSpawned.Any(p => fdTracked.Contains(p.ThingID) && !p.Dead && !p.Downed), "fd_thrumbo_check", 60);
                    return true;
                }
                case "fd_thrumbo_check":
                {
                    List<Pawn> left = map.mapPawns.AllPawnsSpawned.Where(p => fdTracked.Contains(p.ThingID) && !p.Dead && !p.Downed).ToList();
                    Result("C3 the thrumbos leave by ground edges when their time is up", left.Count == 0 && fdExitCount == fdTracked.Count && fdExitBad == 0,
                        $"{fdExitCount} of {fdTracked.Count} left, {fdExitBad} not from a ground edge; still here {string.Join(", ", left.Select(p => $"{FdCell(p.Position, map)} {p.CurJobDef?.defName}"))}; {string.Join("; ", fdExits)}");
                    foreach (Pawn p in left)
                    {
                        p.Destroy();
                    }
                    stage = "fd_raid";
                    return true;
                }

                // ---- C4 an edge walk-in raid aimed at a wall on floor 2
                case "fd_raid":
                {
                    FdBegin(map, 4);
                    List<Pawn> before = map.mapPawns.AllPawnsSpawned.ToList();
                    IncidentParms used = null;
                    foreach (Faction f in Find.FactionManager.AllFactions.Where(f => f.HostileTo(Faction.OfPlayer) && !f.def.hidden && f.def.humanlikeFaction && !f.temporary && f.def.pawnGroupMakers != null).InRandomOrder())
                    {
                        IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
                        parms.faction = f;
                        parms.raidStrategy = RaidStrategyDefOf.ImmediateAttack;
                        parms.raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn;
                        parms.attackTargets = new List<Thing> { fdTargetWall };
                        parms.points = 300f;
                        parms.forced = true;
                        if (IncidentDefOf.RaidEnemy.Worker.TryExecute(parms))
                        {
                            used = parms;
                            break;
                        }
                    }
                    List<Pawn> raiders = map.mapPawns.AllPawnsSpawned.Except(before).Where(p => p.Faction != null && p.Faction.HostileTo(Faction.OfPlayer)).ToList();
                    int up = raiders.Count(p => ProjectZ.IsAnnex(p.Position, map));
                    Result("C4 an edge walk-in raid aimed at a wall on floor 2 enters at a ground edge", used != null && FdGroundEdge(used.spawnCenter, map) && raiders.Count > 0 && up == 0,
                        $"fired {used != null}, spawn centre {(used != null ? FdCell(used.spawnCenter, map) : "-")}, {raiders.Count} raiders, {up} above the ground; target {FdCell(fdTargetWall.Position, map)}");
                    ClearRaiders(map);
                    stage = "fd_pitgate";
                    return true;
                }

                // ---- C5 a pit gate
                case "fd_pitgate":
                {
                    FdBegin(map, 5);
                    if (!ModsConfig.AnomalyActive)
                    {
                        Info("C5 Anomaly not active: pit gate skipped");
                        FdEnd();
                        Finish();
                        return true;
                    }
                    IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail("PitGate");
                    IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.Misc, map);
                    parms.forced = true;
                    bool fired = def != null && def.Worker.TryExecute(parms);
                    List<Thing> sp = map.listerThings.ThingsOfDef(ThingDefOf.PitGateSpawner).ToList();
                    int up = sp.Sum(t => t.OccupiedRect().Cells.Count(c => ProjectZ.IsAnnex(c, map)));
                    Result("C5 a pit gate incident: the gate's spawner covers ground cells only", fired && sp.Count == 1 && up == 0,
                        $"fired {fired}, {sp.Count} spawners at {string.Join(" ", sp.Select(t => FdCell(t.Position, map)))}, {up} cells above the ground");
                    FdEnd();
                    Finish();   // (the spawner can't be destroyed; the run ends here)
                    return true;
                }
            }
            return false;
        }

        private static void SetupFinders(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int gz = ProjectZ.GroundZ(map);
            CellRect found = new CellRect(Mathf.Max(4, map.Size.x / 2 - 20), Mathf.Max(4, gz / 2 - 20), 40, 40);
            if (a == null || Levels.Max(map) < 2 || !found.ExpandedBy(2).InBounds(map) || found.maxZ >= gz - 10)
            {
                Result("test spot", false, $"needs 3 floors and a ground of 60+ rows (run with -projectzfloors=3 -projectzmapsize=120); levels {Levels.Max(map) + 1}, ground {gz}");
                Finish();
                return;
            }
            fdO = new IntVec3(found.minX, 0, found.minZ);
            // The strip to the ground's north edge (5 wide, over the scene's middle).
            fdStrip = CellRect.FromLimits(fdO.x + 18, found.maxZ, fdO.x + 22, gz - 1);
            TerrainDef soil = DefDatabase<TerrainDef>.GetNamed("Soil");
            List<IntVec3> clear = found.ExpandedBy(2).Cells.Concat(fdStrip.ExpandedBy(1).ClipInsideRect(ProjectZ.GroundRect(map)).Cells).Distinct().ToList();
            foreach (IntVec3 c in clear)
            {
                map.roofGrid.SetRoof(c, null);
            }
            foreach (IntVec3 c in clear)
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable && !(t is Pawn))
                    {
                        t.Destroy();
                    }
                }
                if (c.GetTerrain(map).IsWater || c.GetTerrain(map).passability != Traversability.Standable)
                {
                    map.terrainGrid.SetTerrain(c, soil);
                }
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
            }
            map.roofCollapseBuffer.Clear();
            DebugSettings.enableRandomMentalStates = false;
            Find.Storyteller.storytellerComps.Clear();   // no other incidents during the test (test only)
            Find.PlaySettings.autoRebuild = false;
            // P1: 30 x 30 on floor 2, over a roofed ground area held by columns every 6 cells; stairs up at its south.
            fdP1 = new CellRect(fdO.x + 5, fdO.z + 5, 30, 30);
            fdP2 = fdP1;
            fdStairs1 = FdA(20, 7);
            fdStairs2 = FdA(10, 12);
            HashSet<IntVec3> well1 = new HashSet<IntVec3>(StairShape.Ramp(fdStairs1, Rot4.North));
            HashSet<IntVec3> well2 = new HashSet<IntVec3>(StairShape.Ramp(fdStairs2, Rot4.North));
            foreach (IntVec3 g in fdP1)
            {
                bool column = (g.x - fdP1.minX) % 6 == 3 && (g.z - fdP1.minZ) % 6 == 3 && !well1.Contains(g) && !well2.Contains(g);
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                if (column)
                {
                    Place(ThingDefOf.Wall, ThingDefOf.Steel, g, map);
                }
            }
            foreach (IntVec3 g in fdP1)
            {
                if (!well1.Contains(g))
                {
                    map.terrainGrid.SetTerrain(FdOn(g, 1, map), PZDefOf.PZ_UpperFloor);
                }
            }
            Place(PZDefOf.PZ_Stairs, ThingDefOf.Steel, fdStairs1, map, Rot4.North);
            // P2 over P1: P1 roofed with columns on it (the same grid), stairs up from P1.
            foreach (IntVec3 g in fdP1)
            {
                bool column = (g.x - fdP1.minX) % 6 == 3 && (g.z - fdP1.minZ) % 6 == 3 && !well1.Contains(g) && !well2.Contains(g);
                if (!well1.Contains(g))
                {
                    map.roofGrid.SetRoof(FdOn(g, 1, map), RoofDefOf.RoofConstructed);
                }
                if (column)
                {
                    Place(ThingDefOf.Wall, ThingDefOf.Steel, FdOn(g, 1, map), map);
                }
            }
            foreach (IntVec3 g in fdP2)
            {
                if (!well1.Contains(g) && !well2.Contains(g))
                {
                    map.terrainGrid.SetTerrain(FdOn(g, 2, map), PZDefOf.PZ_UpperFloor);
                }
            }
            Place(PZDefOf.PZ_Stairs, ThingDefOf.Steel, FdOn(fdStairs2, 1, map), map, Rot4.North);
            // Room R on P1 (walls, a door to the south).
            fdRoom = new CellRect(fdO.x + 24, fdO.z + 24, 7, 7);
            IntVec3 door = new IntVec3(fdRoom.minX + 3, 0, fdRoom.minZ);
            foreach (IntVec3 g in fdRoom.EdgeCells)
            {
                foreach (Thing t in FdOn(g, 1, map).GetThingList(map).Where(t => t.def == ThingDefOf.Wall).ToList())
                {
                    t.Destroy();
                }
                Thing w = Place(g == door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, FdOn(g, 1, map), map);
                if (g == new IntVec3(fdRoom.maxX, 0, fdRoom.maxZ - 3))
                {
                    fdTargetWall = w;
                }
            }
            fdTargetWall = fdTargetWall ?? FdOn(new IntVec3(fdRoom.maxX, 0, fdRoom.maxZ - 3), 1, map).GetFirstBuilding(map);
            // Colonists in the scene's south-west corner (the "colony" the edge finder avoids).
            List<Pawn> cols = Colonists(map);
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], FdA(2 + i, 2));
            }
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            fdBaseline.Clear();
            fdHome.Clear();
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                fdBaseline.Add(p.ThingID);
            }
            foreach (Pawn p in cols)
            {
                fdHome[p] = p.Position;
            }
            Info($"finders scene: ground {map.Size.x} x {gz}, levels {Levels.Min(map)}..{Levels.Max(map)}, P1 {fdP1} on floor 2 and floor 3, room {fdRoom}, strip {fdStrip}, target wall {(fdTargetWall != null ? FdCell(fdTargetWall.Position, map) : "none")}");
            Wait(30, "fd_finders");
        }

        private static void FdFinders(Map map)
        {
            int gz = ProjectZ.GroundZ(map);
            IntVec3[] centres = { FdA(15, 15), FdOn(FdA(15, 15), 1, map), FdOn(FdA(15, 15), 2, map) };
            FdBegin(map, 100);
            // TryFindRandomCellNearWith with a validator only far cells pass (the search has to grow): the centre's floor.
            for (int k = 0; k < 3; k++)
            {
                IntVec3 near = centres[k];
                int found = 0, off = 0;
                List<string> bad = new List<string>();
                for (int i = 0; i < 200; i++)
                {
                    if (RCellFinder.TryFindRandomCellNearWith(near, c => (c - near).LengthHorizontalSquared > 40 * 40, map, out IntVec3 r))
                    {
                        found++;
                        if (Levels.LevelOfRowLoose(map, r.z) != k)
                        {
                            off++;
                            if (bad.Count < 4)
                            {
                                bad.Add(FdCell(r, map));
                            }
                        }
                    }
                }
                Result($"C1 TryFindRandomCellNearWith from a centre on level {k} (far cells only, the search grows): every cell on level {k}", found == 200 && off == 0, $"{found} found, {off} off the level {string.Join(" ", bad)}");
                // nothing passes: it ends, false
                bool none = RCellFinder.TryFindRandomCellNearWith(near, c => false, map, out IntVec3 _);
                Result($"C1 TryFindRandomCellNearWith from level {k}, nothing passes: ends with false", !none, "");
            }
            FdBegin(map, 101);
            // TryFindRandomCellsNear / ClearCellsNear from floor 3 (P2): walkable cells on P2 only.
            {
                IntVec3 near = centres[2];
                bool ok1 = RCellFinder.TryFindRandomCellsNear(near, 40, map, c => c.Standable(map) && (c - near).LengthHorizontalSquared > 10 * 10, out List<IntVec3> cells1);
                bool ok2 = RCellFinder.TryFindRandomClearCellsNear(near, 40, map, out List<IntVec3> cells2);
                int off = cells1.Concat(cells2).Count(c => Levels.LevelOf(c, map) != 2);
                Result("C1 TryFindRandomCellsNear and TryFindRandomClearCellsNear from floor 3: all on floor 3", ok1 && ok2 && off == 0, $"{cells1.Count} + {cells2.Count} cells, {off} off floor 3");
            }
            FdBegin(map, 102);
            // Near the map centre (a centre on the ground): far cells only, so the search grows: the ground.
            {
                IntVec3 mc = map.Center;
                int found = 0, off = 0;
                for (int i = 0; i < 100; i++)
                {
                    if (RCellFinder.TryFindRandomCellNearTheCenterOfTheMapWith(c => (c - mc).LengthHorizontalSquared > (gz / 2 - 3) * (gz / 2 - 3), map, out IntVec3 r))
                    {
                        found++;
                        off += ProjectZ.IsAnnex(r, map) ? 1 : 0;
                    }
                }
                Result("C1 TryFindRandomCellNearTheCenterOfTheMapWith (far cells only): the ground", found > 0 && off == 0, $"{found} found, {off} above the ground");
            }
            FdBegin(map, 103);
            // Outside the colony near the centre (thrumbos, ...): the ground.
            {
                Pawn col = Colonists(map).FirstOrDefault();
                int found = 0, off = 0;
                for (int i = 0; i < 50 && col != null; i++)
                {
                    if (RCellFinder.TryFindRandomCellOutsideColonyNearTheCenterOfTheMap(col.Position, map, 30f, out IntVec3 r))
                    {
                        found++;
                        off += ProjectZ.IsAnnex(r, map) ? 1 : 0;
                    }
                }
                Result("C1 TryFindRandomCellOutsideColonyNearTheCenterOfTheMap: the ground", found > 0 && off == 0, $"{found} found, {off} above the ground");
            }
            FdBegin(map, 104);
            // Exits for a pawn on the ground near the north edge, in room R (floor 2) and on P2 (floor 3).
            Pawn walker = Colonists(map).Last();
            IntVec3 home = walker.Position;
            IntVec3[] starts = { new IntVec3(fdStrip.CenterCell.x, 0, gz - 4), FdOn(fdRoom.CenterCell, 1, map), FdOn(FdA(15, 15), 2, map) };
            string[] where = { "the ground near the north edge", "room R (floor 2)", "P2 (floor 3)" };
            for (int k = 0; k < 3; k++)
            {
                Teleport(walker, starts[k]);
                int best = 0, bestBad = 0, rnd = 0, rndBad = 0, rndNorth = 0;
                List<string> bad = new List<string>();
                IntVec3 firstBest = IntVec3.Invalid;
                for (int i = 0; i < 30; i++)
                {
                    if (RCellFinder.TryFindBestExitSpot(walker, out IntVec3 s))
                    {
                        best++;
                        firstBest = firstBest.IsValid ? firstBest : s;
                        if (!FdGroundEdge(s, map))
                        {
                            bestBad++;
                            bad.Add("best " + FdCell(s, map));
                        }
                    }
                }
                for (int i = 0; i < 100; i++)
                {
                    if (RCellFinder.TryFindRandomExitSpot(walker, out IntVec3 s))
                    {
                        rnd++;
                        rndNorth += s.z == gz - 1 ? 1 : 0;
                        if (!FdGroundEdge(s, map))
                        {
                            rndBad++;
                            if (bad.Count < 6)
                            {
                                bad.Add("random " + FdCell(s, map));
                            }
                        }
                    }
                }
                Result($"C1 exit spots (best, random) from {where[k]}: found, ground edges only", best == 30 && bestBad == 0 && rnd == 100 && rndBad == 0,
                    $"best {best}/30 (e.g. {FdCell(firstBest, map)}), random {rnd}/100 (north row {rndNorth}), not a ground edge {bestBad + rndBad} {string.Join(" ", bad.Take(6))}");
                if (k == 0)
                {
                    Result("C1 the best exit next to the ground's north edge is on its north row (z = groundZ - 1); random exits use that row too", firstBest.z == gz - 1 && rndNorth > 0,
                        $"best {FdCell(firstBest, map)}, random on the north row {rndNorth}/100");
                }
            }
            Teleport(walker, home);
            FdBegin(map, 105);
            // The edge away from the colony, from positions on the ground (north strip), in room R and on P2.
            for (int k = 0; k < 3; k++)
            {
                bool ok = RCellFinder.TryFindEdgeCellFromPositionAvoidingColony(starts[k], map, c => c.Standable(map), out IntVec3 r);
                Result($"C1 TryFindEdgeCellFromPositionAvoidingColony from {where[k]}: a ground edge", ok && FdGroundEdge(r, map), $"{(ok ? FdCell(r, map) : "none")} from {FdCell(starts[k], map)}");
            }
            {
                FdBegin(map, 106);
                // Its edge list is the ground's edges: the ground's north row is among them.
                bool ok = RCellFinder.TryFindEdgeCellFromPositionAvoidingColony(starts[0], map, c => c.z == gz - 1 && c.Standable(map), out IntVec3 r);
                Result("C1 TryFindEdgeCellFromPositionAvoidingColony: the ground's north row is one of its edges", ok && r.z == gz - 1, ok ? FdCell(r, map) : "none");
            }
            FdBegin(map, 107);
            // Two caravans' starting cells.
            {
                int off = 0;
                List<string> e = new List<string>();
                for (int i = 0; i < 10; i++)
                {
                    MultipleCaravansCellFinder.FindStartingCellsFor2Groups(map, out IntVec3 c1, out IntVec3 c2);
                    off += (ProjectZ.IsAnnex(c1, map) ? 1 : 0) + (ProjectZ.IsAnnex(c2, map) ? 1 : 0);
                    if (i < 3)
                    {
                        e.Add($"{c1}/{c2}");
                    }
                }
                Result("C1 MultipleCaravansCellFinder: both caravans start on the ground", off == 0, $"{off} of 20 above the ground; e.g. {string.Join(" ", e)}");
            }
            FdBegin(map, 108);
            // Anomaly's large structures.
            if (ModsConfig.AnomalyActive)
            {
                int n = 0, up = 0;
                foreach (ThingDef def in new[] { ThingDefOf.PitGate, ThingDefOf.FleshmassHeart, ThingDefOf.PitBurrow })
                {
                    for (int i = 0; i < 15; i++)
                    {
                        LargeBuildingSpawnParms parms = LargeBuildingSpawnParms.Default.ForThing(def);
                        parms.allowFogged = true;
                        parms.ignoreTerrainAffordance = true;
                        if (LargeBuildingCellFinder.TryFindCell(out IntVec3 c, map, parms, forceRecalculate: i == 0))
                        {
                            n++;
                            if (GenAdj.OccupiedRect(c, Rot4.North, def.Size).Cells.Any(x => ProjectZ.IsAnnex(x, map)))
                            {
                                up++;
                                Info($"C1 {def.defName} ({def.Size}) at {FdCell(c, map)} reaches above the ground");
                            }
                        }
                    }
                }
                Result("C1 LargeBuildingCellFinder (pit gate, fleshmass heart, burrow): the ground", n > 0 && up == 0, $"{n} cells found, {up} above the ground");
            }
            Info("ground finders swaps: " + string.Join(", ", GroundFinders_Patch.PerMethod.Select(kv => kv.Key + " " + kv.Value)));
        }
    }
}
