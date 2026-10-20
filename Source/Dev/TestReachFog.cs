using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode reachfog: THE fog rule (ReachFog: every place the player's pawns can reach is revealed). A fixed-seed
    // tall-mountain map (the arguments of rocktop: -projectztallmountains -projectzfloors=4 -projectzmapsize=250) saved
    // once as pz_reachfog; every check reloads it, builds its own fixture and runs real flows with ticks:
    //   R1 a colonist arrives on a level-2 ledge (teleport): the terrace one level below (only a jump gets there) stays
    //      fogged; the colonist digs a hole (Mine below) from the ledge beside the terrace: it opens through the hole
    //   R2 a colonist arrives on a level-3 top: the area two or more floors below (only a jump gets there) stays fogged
    //   R3 colonists build stairs from the ground onto a fogged plateau's rim: it opens on completion
    //   R4 a plateau nobody can reach (no stairs, nothing higher next to it) stays fogged after runs
    //   R5 a cave sealed by rock stays fogged while colonists mine towards it; it opens when they break through
    //   R6 a room behind a closed door stays fogged; a drafted colonist ordered into the doorway opens it and the room
    //   R7 cost: the full run (every region scanned, as after a load), the walk-only run, the run after a wall is built
    // Every check also verifies that every walkable cell it revealed is reachable (RfAllReached: an independent
    // reachability query that ignores fog), and the checks that must not open anything compare the whole fog grid.
    // With -projectzsave=<copy of a player's save>: the load-time pass (loaded once with the rule off, once on): how many
    // cells it reveals, all reachable, its cost, shots per level with the fog lid on.
    public static partial class TestRunner
    {
        private const string RfSave = "pz_reachfog";
        private static IntVec3 rfT2 = IntVec3.Invalid, rfLand1 = IntVec3.Invalid, rfGoal1 = IntVec3.Invalid, rfStand1 = IntVec3.Invalid;
        private static IntVec3 rfT3 = IntVec3.Invalid, rfLand2 = IntVec3.Invalid;
        private static IntVec3 rfStairs = IntVec3.Invalid, rfExit = IntVec3.Invalid;
        private static Rot4 rfRot;
        private static IntVec3 rfLone = IntVec3.Invalid;
        private static IntVec3 rfCaveE = IntVec3.Invalid, rfCaveD = IntVec3.Invalid;
        private static IntVec3 rfRoomE = IntVec3.Invalid, rfRoomD = IntVec3.Invalid;
        private static readonly List<IntVec3> rfArea = new List<IntVec3>();
        private static bool[] rfBefore = new bool[0];
        private static Pawn rfPawn;
        private static int rfShot, rfFog0;
        private static string rfUserSave;

        private static bool[] RfSnap(Map map)
        {
            int n = map.cellIndices.NumGridCells;
            bool[] f = new bool[n];
            for (int i = 0; i < n; i++)
            {
                f[i] = map.fogGrid.IsFogged(i);
            }
            return f;
        }

        private static int RfChanged(Map map, bool[] before)
        {
            int n = 0;
            for (int i = 0; i < before.Length && i < map.cellIndices.NumGridCells; i++)
            {
                n += before[i] != map.fogGrid.IsFogged(i) ? 1 : 0;
            }
            return n;
        }

        // Independent reachability (the game's query with ProjectZ's floor links, no pawn: fog is no obstacle, doors pass)
        // from any of the start cells.
        private static bool RfReach(Map map, IntVec3 c, List<IntVec3> from)
        {
            TraverseParms tp = TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly);
            for (int i = 0; i < from.Count; i++)
            {
                if (map.reachability.CanReach(from[i], c, PathEndMode.OnCell, tp))
                {
                    return true;
                }
            }
            return false;
        }

        private static List<Pawn> RfPlayers(Map map) => map.mapPawns.AllPawnsSpawned.Where(ReachFog.PlayerPawn).ToList();

        // The start cells of the oracle: the player's pawns, then the far end of every open hole whose near end they
        // reach (round the top -> the landing; back up only through a covered top), read from the map (Shaft, Column),
        // twice for holes over holes.
        private static List<IntVec3> RfStarts(Map map)
        {
            List<IntVec3> s = RfPlayers(map).Select(p => p.Position).ToList();
            List<IntVec3> marks = ShaftMarks.For(map)?.cells.Where(c => Shaft.Is(c, map)).ToList() ?? new List<IntVec3>();
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (IntVec3 c in marks)
                {
                    IntVec3 u = FloorRoof.Above(c, map);
                    IntVec3 land = u.IsValid ? Column.Landing(u, map, false, true, out _, out _) : IntVec3.Invalid;
                    if (!land.IsValid || !land.Walkable(map))
                    {
                        continue;
                    }
                    List<IntVec3> top = GenAdj.CardinalDirections.Select(d => u + d).Where(n => n.InBounds(map) && ProjectZ.SameBand(n, u, map) && n.Walkable(map)).ToList();
                    if (!s.Contains(land) && top.Any(n => RfReach(map, n, s)))
                    {
                        s.Add(land);
                    }
                    if (map.roofGrid.Roofed(u) && RfReach(map, land, s))
                    {
                        s.AddRange(top.Where(n => !s.Contains(n)));
                    }
                }
            }
            return s;
        }

        // Everything unfogged since 'before': every walkable cell reachable by a player pawn; the rest (faces, ceilings,
        // the open fog vanilla's flood takes along) counted.
        private static bool RfAllReached(Map map, bool[] before, out string why)
        {
            List<IntVec3> from = RfStarts(map);
            Dictionary<Region, bool> byRegion = new Dictionary<Region, bool>();
            int newly = 0, walk = 0, bad = 0, faces = 0, ceil = 0, other = 0, topCeil = 0;
            string first = null;
            bool Reached(IntVec3 c)
            {
                Region r = map.regionGrid.GetValidRegionAt(c);
                if (r == null)
                {
                    return false;
                }
                if (!byRegion.TryGetValue(r, out bool ok))
                {
                    ok = RfReach(map, c, from);
                    byRegion[r] = ok;
                }
                return ok;
            }
            bool CeilingOfReached(IntVec3 c)
            {
                IntVec3 b = FloorRoof.Below(c, map);
                while (b.IsValid && b.InBounds(map) && b.GetTerrain(map) == PZDefOf.PZ_OpenAir && b.GetEdifice(map) == null)
                {
                    b = FloorRoof.Below(b, map);
                }
                return b.IsValid && b.InBounds(map) && b.Walkable(map) && Reached(b);
            }
            for (int i = 0; i < before.Length && i < map.cellIndices.NumGridCells; i++)
            {
                if (!before[i] || map.fogGrid.IsFogged(i))
                {
                    continue;
                }
                newly++;
                IntVec3 c = map.cellIndices.IndexToCell(i);
                if (c.Walkable(map))
                {
                    walk++;
                    if (!Reached(c))
                    {
                        if (CeilingOfReached(c))
                        {
                            topCeil++;   // (a top that is the ceiling of a reached cave: seen from below, not walked on)
                        }
                        else
                        {
                            bad++;
                            first ??= $"{c} L{Levels.LevelOf(c, map)} {c.GetTerrain(map).defName}";
                        }
                    }
                    continue;
                }
                bool face = false;
                for (int d = 0; d < 8 && !face; d++)
                {
                    IntVec3 n = c + GenAdj.AdjacentCells[d];
                    face = n.InBounds(map) && n.Walkable(map) && !map.fogGrid.IsFogged(n) && Reached(n);
                }
                if (face)
                {
                    faces++;
                    continue;
                }
                IntVec3 b = FloorRoof.Below(c, map);
                while (b.IsValid && b.InBounds(map) && b.GetTerrain(map) == PZDefOf.PZ_OpenAir && b.GetEdifice(map) == null)
                {
                    b = FloorRoof.Below(b, map);
                }
                if (b.IsValid && b.InBounds(map) && b.Walkable(map) && Reached(b))
                {
                    ceil++;
                }
                else
                {
                    other++;
                }
            }
            why = $"{newly} cell(s) revealed: {walk} walkable ({bad} NOT reachable{(first == null ? "" : ", first " + first)}, {topCeil} of them tops seen from below as the ceiling of a reached cell), {faces} faces, {ceil} ceilings, {other} other (vanilla's flood through open fog, faces of a shaft)";
            return bad == 0;
        }

        private static void RfFixture(Map map)
        {
            DebugSettings.enableRandomMentalStates = false;
            Find.PlaySettings.autoRebuild = false;
            Find.Storyteller.storytellerComps.Clear();
            Find.Storyteller.incidentQueue.Clear();
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            IsoRimBridge.SetOption("fogLid", true);
        }

        private static void RfNext(string next)
        {
            Find.UIRoot.screenshotMode.Active = false;
            muPointer = IntVec3.Invalid;
            FloorView.Set(0);
            Find.DesignatorManager.Deselect();
            if (next == null)
            {
                Finish();
                return;
            }
            stage = next;
            pendingLoad = rfUserSave ?? RfSave;
        }

        // The cells 4-connected to 'from' on its band through walkable cells (cap).
        private static List<IntVec3> RfFlat(IntVec3 from, Map map, int cap = 4000)
        {
            List<IntVec3> l = new List<IntVec3>();
            if (!from.IsValid || !from.InBounds(map) || !from.Walkable(map))
            {
                return l;
            }
            int band = Levels.BandOfCell(from, map);
            HashSet<IntVec3> seen = new HashSet<IntVec3> { from };
            Queue<IntVec3> q = new Queue<IntVec3>();
            q.Enqueue(from);
            while (q.Count > 0 && l.Count < cap)
            {
                IntVec3 c = q.Dequeue();
                l.Add(c);
                for (int d = 0; d < 4; d++)
                {
                    IntVec3 n = c + GenAdj.CardinalDirections[d];
                    if (n.InBounds(map) && Levels.BandOfCell(n, map) == band && n.Walkable(map) && seen.Add(n))
                    {
                        q.Enqueue(n);
                    }
                }
            }
            return l;
        }

        private static int RfFogged(IEnumerable<IntVec3> cells, Map map) => cells.Count(c => map.fogGrid.IsFogged(c));

        // A top at level L over g whose jump over its neighbour in a direction lands at least 'fall' floors down on a
        // fogged cell with a fogged area of at least minFog cells round it (and a fogged goal cell 2+ cells inside it).
        private static bool RfLedge(IntVec3 g, int L, int fall, int minFog, Map map, out IntVec3 top, out IntVec3 land, out IntVec3 goal, out IntVec3 stand)
        {
            top = land = goal = stand = IntVec3.Invalid;
            if (g.x < 4 || g.z < 4 || g.x >= map.Size.x - 4 || g.z >= ProjectZ.GroundZ(map) - 4 || Column.NaturalTop(g, map) != L)
            {
                return false;
            }
            IntVec3 t = Levels.Cell(g, L, map);
            if (!t.IsValid || !TmTop(t, map) || !t.Standable(map))
            {
                return false;
            }
            for (int d = 0; d < 4; d++)
            {
                IntVec3 dir = GenAdj.CardinalDirections[d];
                if (!JumpGrid.LandingFrom(t, dir, map, out IntVec3 l, out int fallen) || fallen < fall || !map.fogGrid.IsFogged(l) || !l.Standable(map))
                {
                    continue;
                }
                List<IntVec3> area = RfFlat(l, map, 400);
                if (RfFogged(area, map) < minFog)
                {
                    continue;
                }
                IntVec3 gl = area.Where(c => map.fogGrid.IsFogged(c) && c.Standable(map) && c.DistanceTo(l) >= 2f && c.DistanceTo(l) <= 5f).OrderBy(c => c.DistanceToSquared(l)).ThenBy(c => c.x).ThenBy(c => c.z).FirstOrDefault();
                IntVec3 st = t - dir;
                if (!gl.IsValid || gl == IntVec3.Zero || !st.InBounds(map) || !TmTop(st, map) || !st.Standable(map))
                {
                    continue;
                }
                top = t;
                land = l;
                goal = gl;
                stand = st;
                return true;
            }
            return false;
        }

        // A ground cell e (open, seen, reachable) and a direction: rock at e+d, then a 5x5 block of fogged natural rock
        // centred at e+k*d (k = 3: room, 4: cave). Returns the centre.
        private static bool RfPocket(IntVec3 e, int k, Map map, Pawn any, out IntVec3 dir)
        {
            dir = IntVec3.Invalid;
            if (!e.InBounds(map) || e.x < 8 || e.z < 8 || e.x >= map.Size.x - 8 || e.z >= ProjectZ.GroundZ(map) - 8 || !e.Standable(map) || map.fogGrid.IsFogged(e) || e.GetEdifice(map) != null)
            {
                return false;
            }
            for (int d = 0; d < 4; d++)
            {
                IntVec3 dd = GenAdj.CardinalDirections[d];
                IntVec3 c0 = e + dd * k;
                if (!MuRock(e + dd, map) || !HfBlock(c0, 2, map, x => MuRock(x, map) && ((x - e).x * dd.x + (x - e).z * dd.z == 1 || map.fogGrid.IsFogged(x))))
                {
                    continue;
                }
                if (any == null || !map.reachability.CanReach(any.Position, e, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors)))
                {
                    continue;
                }
                dir = dd;
                return true;
            }
            return false;
        }

        private static bool RfSites(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            List<Pawn> cols = Colonists(map);
            IntVec3 home = new IntVec3(map.Size.x / 2, 0, a.groundZ / 2);   // (fixed: the colonists wander before the setup)
            List<IntVec3> near = Near(home, 110, map).ToList();
            IntVec3 t, l, gl;
            // R1: a level-2 ledge over a fogged level-1 terrace (one floor down, no hole)
            foreach (IntVec3 g in near)
            {
                if (RfLedge(g, 2, 1, 12, map, out t, out l, out gl, out IntVec3 st1) && Levels.LevelOf(l, map) == 1)
                {
                    rfT2 = t;
                    rfLand1 = l;
                    rfGoal1 = gl;
                    rfStand1 = st1;
                    break;
                }
            }
            // R2: a level-3 top two or more floors over a fogged landing area, away from R1
            foreach (IntVec3 g in near)
            {
                if ((!rfT2.IsValid || !g.InHorDistOf(ProjectZ.GroundCellOf(rfT2, map), 30f)) && RfLedge(g, 3, 2, 6, map, out t, out l, out gl, out _))
                {
                    rfT3 = t;
                    rfLand2 = gl;
                    break;
                }
            }
            // R3: stairs on the ground whose exit is a rim top with a fogged plateau behind it (as tallfog G2)
            Pawn any = cols.FirstOrDefault();
            foreach (IntVec3 p in near)
            {
                if (rfStairs.IsValid || p.z >= a.groundZ - 3 || p.z < 3 || p.x < 3 || p.x >= map.Size.x - 3)
                {
                    continue;
                }
                for (int r = 0; r < 4 && !rfStairs.IsValid; r++)
                {
                    Rot4 rot = new Rot4(r);
                    IntVec3 exit = StairShape.Exit(p, rot, map);
                    if (!exit.IsValid || !TmTop(exit, map) || map.fogGrid.IsFogged(exit) || Levels.LevelOf(exit, map) != 1)
                    {
                        continue;
                    }
                    if (StairShape.Footprint(p, rot).Cells.Any(c => !c.InBounds(map) || map.fogGrid.IsFogged(c) || c.Roofed(map) || c.GetEdifice(map) != null || !c.Standable(map))
                        || any == null || !map.reachability.CanReach(any.Position, p, PathEndMode.Touch, TraverseParms.For(TraverseMode.PassDoors))
                        || !GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, p, rot, map, stuffDef: ThingDefOf.WoodLog).Accepted)
                    {
                        continue;
                    }
                    if (RfFogged(RfFlat(exit, map, 2000), map) < 30)
                    {
                        continue;
                    }
                    rfStairs = p;
                    rfRot = rot;
                    rfExit = exit;
                }
            }
            // R4: a fogged level-1 top with 20+ fogged cells round it, the nearest to home
            foreach (IntVec3 g in near)
            {
                IntVec3 c = Levels.Cell(g, 1, map);
                if (c.IsValid && Column.NaturalTop(g, map) == 1 && TmTop(c, map) && map.fogGrid.IsFogged(c) && RfFogged(RfFlat(c, map, 200), map) >= 20)
                {
                    rfLone = c;
                    break;
                }
            }
            // R5 (cave, k 4) and R6 (room, k 3): pockets of fogged rock beside reachable ground, apart from each other
            foreach (IntVec3 e in near)
            {
                if (!rfCaveE.IsValid && RfPocket(e, 4, map, any, out IntVec3 d))
                {
                    rfCaveE = e;
                    rfCaveD = d;
                }
                else if (!rfRoomE.IsValid && (!rfCaveE.IsValid || !e.InHorDistOf(rfCaveE, 15f)) && RfPocket(e, 3, map, any, out IntVec3 d2))
                {
                    rfRoomE = e;
                    rfRoomD = d2;
                }
                if (rfCaveE.IsValid && rfRoomE.IsValid)
                {
                    break;
                }
            }
            Info($"sites: R1 ledge {rfT2} -> terrace {rfLand1} goal {rfGoal1}; R2 top {rfT3} -> {rfLand2}; R3 stairs {rfStairs} {rfRot} exit {rfExit}; R4 plateau {rfLone}; R5 cave beside {rfCaveE} dir {rfCaveD}; R6 room beside {rfRoomE} dir {rfRoomD}");
            return rfT2.IsValid && rfT3.IsValid && rfStairs.IsValid && rfLone.IsValid && rfCaveE.IsValid && rfRoomE.IsValid;
        }

        private static bool StepReachFog(Map map)
        {
            switch (stage)
            {
                case "rf_setup":
                {
                    RfFixture(map);
                    if (GenCommandLine.TryGetCommandLineArg("projectzsave", out rfUserSave))
                    {
                        ReachFog.Off = true;
                        stage = "rf_u_off";
                        pendingLoad = rfUserSave;
                        return true;
                    }
                    rfUserSave = null;
                    if (ProjectZ.Annex(map) == null || !Mountains.On(map))
                    {
                        Result("reachfog: a tall-mountain map", false, "no annex or no tall mountains (-projectztallmountains)");
                        Finish();
                        return true;
                    }
                    bool sites = RfSites(map);
                    Result("R sites: a level-2 ledge over a fogged terrace, a level-3 top over a 2+ floor drop, a stairs spot onto a fogged plateau, a fogged plateau, two rock pockets beside reachable ground", sites, "see sites");
                    if (!sites)
                    {
                        Finish();
                        return true;
                    }
                    FloorView.Set(0);
                    GameDataSaveLoader.SaveGame(RfSave);
                    RfNext("rf_r1");
                    return true;
                }

                // ------------------------------------------------------------ R1 a terrace one level below a ledge
                case "rf_r1":
                {
                    RfFixture(map);
                    ReachFog.Run(map);
                    rfArea.Clear();
                    rfArea.AddRange(RfFlat(rfLand1, map, 400));
                    rfFog0 = RfFogged(rfArea, map);
                    Pawn p = Colonists(map).OrderBy(x => x.thingIDNumber).First(x => !x.Downed && !x.WorkTypeIsDisabled(WorkTypeDefOf.Mining));
                    MdWork(map, new List<Pawn> { p }, WorkTypeDefOf.Mining);
                    Info($"R1 ledge {rfT2} (L2), terrace {rfArea.Count} cells round {rfLand1} (L1), fogged {rfFog0}; {p} arrives on the ledge at {rfStand1} (teleport)");
                    Teleport(p, rfStand1);
                    rfPawn = p;
                    Wait(3, "rf_r1_arrived");
                    return true;
                }

                case "rf_r1_arrived":
                {
                    int fog = RfFogged(rfArea, map);
                    Result("R1 a colonist arrived on a level-2 ledge: the terrace one level below (only a jump gets there) stays fogged", fog == rfFog0,
                        $"terrace cells fogged {fog} of {rfArea.Count} (before {rfFog0}); run {ReachFog.LastMs:0.00} ms (walk {ReachFog.LastWalkMs:0.00} ms), {ReachFog.LastRegions} regions reached, {ReachFog.LastScanned} scanned");
                    MuShot(map, "reachfog_r1_terrace_fogged_L1", 1, rfLand1, 14f, "rf_r1_dig");
                    return true;
                }

                case "rf_r1_dig":
                {
                    Find.UIRoot.screenshotMode.Active = false;
                    rfBefore = RfSnap(map);
                    bool ok = HfOrder(new Designator_MineBelow(), rfT2, out string why);
                    Info($"R1 {rfPawn} marks Mine below on the ledge cell {rfT2} (the rock under it borders the terrace): {why}");
                    if (!ok)
                    {
                        Result("R1 Mine below on the ledge", false, why);
                        RfNext("rf_r2");
                        return true;
                    }
                    IntVec3 c1 = FloorRoof.Below(rfT2, map);
                    MuRun(400, 71710, () => c1.GetFirstMineable(map) == null && Shaft.Is(c1, map), "rf_r1_dug");
                    return true;
                }

                case "rf_r1_dug":
                    Wait(2, "rf_r1_check");
                    return true;

                case "rf_r1_check":
                {
                    IntVec3 c1 = FloorRoof.Below(rfT2, map);
                    int fog = RfFogged(rfArea, map);
                    Result("R1 the colonist dug a hole from the ledge down beside the terrace: the terrace, connected through the hole, is revealed",
                        Shaft.Is(c1, map) && fog == 0, $"hole bottom {c1} open {Shaft.Is(c1, map)}; terrace cells still fogged {fog} of {rfArea.Count}");
                    bool ok = RfAllReached(map, rfBefore, out string why);
                    Result("R1 everything revealed is reached (walking, stairs, through a hole)", ok, why);
                    MuShot(map, "reachfog_r1_terrace_hole_L1", 1, rfLand1, 14f, "rf_r1_end");
                    return true;
                }

                case "rf_r1_end":
                    RfNext("rf_r2");
                    return true;

                // ------------------------------------------------------------ R2 a drop of two floors or more
                case "rf_r2":
                {
                    RfFixture(map);
                    ReachFog.Run(map);
                    rfArea.Clear();
                    rfArea.AddRange(RfFlat(rfLand2, map, 400));
                    rfFog0 = RfFogged(rfArea, map);
                    Pawn p = Colonists(map).OrderBy(x => x.thingIDNumber).First(x => !x.Downed);
                    Info($"R2 top {rfT3} (L3), area {rfArea.Count} cells round {rfLand2} (L{Levels.LevelOf(rfLand2, map)}) two or more floors down, fogged {rfFog0}; {p} arrives on the top");
                    Teleport(p, rfT3);
                    Draft(p, true);
                    Wait(3, "rf_r2_arrived");
                    return true;
                }

                case "rf_r2_arrived":
                {
                    int fog = RfFogged(rfArea, map);
                    Result("R2 a colonist arrived on a level-3 top: the area two or more floors below it (only a jump gets there) stays fogged", fog == rfFog0, $"area cells fogged {fog} of {rfArea.Count} (before {rfFog0})");
                    MuShot(map, $"reachfog_r2_drop_L{Levels.LevelOf(rfLand2, map)}", Levels.LevelOf(rfLand2, map), ProjectZ.GroundCellOf(rfLand2, map), 16f, "rf_r2_end");
                    return true;
                }

                case "rf_r2_end":
                    RfNext("rf_r3");
                    return true;

                // ------------------------------------------------------------ R3 stairs onto a fogged plateau
                case "rf_r3":
                {
                    RfFixture(map);
                    ReachFog.Run(map);
                    rfArea.Clear();
                    rfArea.AddRange(RfFlat(rfExit, map, 4000));
                    rfBefore = RfSnap(map);
                    MuWood(map, rfStairs - rfRot.FacingCell * 3);
                    GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, rfStairs, map, rfRot, Faction.OfPlayer, ThingDefOf.WoodLog);
                    MuCrew(map, WorkTypeDefOf.Construction, 3);
                    Info($"R3 stairs {rfStairs} facing {rfRot}, exit {rfExit}; plateau {rfArea.Count} cells, fogged {RfFogged(rfArea, map)}");
                    MuRun(400, 71730, () => MuStairs(map, rfStairs) != null, "rf_r3_built");
                    return true;
                }

                case "rf_r3_built":
                {
                    bool built = MuStairs(map, rfStairs) != null;
                    int up = Colonists(map).Count(p => ProjectZ.FloorOf(p.Position, map) >= 1);
                    Wait(2, "rf_r3_check");
                    Info($"R3 built {built}, colonists upstairs {up}");
                    return true;
                }

                case "rf_r3_check":
                {
                    bool built = MuStairs(map, rfStairs) != null;
                    int fog = RfFogged(rfArea, map);
                    Result("R3 colonists built stairs onto a fogged plateau's rim: on completion the plateau is revealed", built && fog == 0, $"built {built}; plateau cells still fogged {fog} of {rfArea.Count}");
                    bool ok = RfAllReached(map, rfBefore, out string why);
                    Result("R3 everything revealed is reachable", ok, why);
                    MuShot(map, "reachfog_r3_stairs_L1", 1, rfExit, 18f, "rf_r3_end");
                    return true;
                }

                case "rf_r3_end":
                    RfNext("rf_r4");
                    return true;

                // ------------------------------------------------------------ R4 a plateau nobody can reach
                case "rf_r4":
                {
                    RfFixture(map);
                    ReachFog.Run(map);
                    rfArea.Clear();
                    rfArea.AddRange(RfFlat(rfLone, map, 2000));
                    rfFog0 = RfFogged(rfArea, map);
                    rfBefore = RfSnap(map);
                    // real triggers: colonists arrive (teleports on the ground), a wall is built; and a full rescan
                    List<Pawn> cols = Colonists(map).OrderBy(x => x.thingIDNumber).ToList();
                    foreach (Pawn p in cols)
                    {
                        IntVec3 to = CellFinder.StandableCellNear(p.Position, map, 3f);
                        if (to.IsValid)
                        {
                            Teleport(p, to);
                        }
                    }
                    ReachFogMap.For(map)?.Forget();
                    MuRun(10, 71740, () => false, "rf_r4_check");
                    return true;
                }

                case "rf_r4_check":
                {
                    int fog = RfFogged(rfArea, map);
                    bool reach = RfReach(map, rfLone, RfStarts(map));
                    int changed = RfChanged(map, rfBefore);
                    Result("R4 a plateau nobody can reach (no stairs, no jump onto it) stays fogged after runs", fog == rfFog0 && changed == 0 && !reach,
                        $"plateau round {rfLone}: {fog} of {rfArea.Count} fogged (before {rfFog0}: its rim seen since generation), reachable {reach}; fog cells changed anywhere {changed}; runs {ReachFog.Runs}");
                    MuShot(map, "reachfog_r4_unreached_L1", 1, rfLone, 18f, "rf_r4_end");
                    return true;
                }

                case "rf_r4_end":
                    RfNext("rf_r5");
                    return true;

                // ------------------------------------------------------------ R5 a cave sealed by rock
                case "rf_r5":
                {
                    RfFixture(map);
                    IntVec3 c0 = rfCaveE + rfCaveD * 4;
                    rfArea.Clear();
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            rfArea.Add(c0 + new IntVec3(dx, 0, dz));
                        }
                    }
                    HfCarve(map, rfArea, false);
                    ReachFog.Run(map);
                    rfBefore = RfSnap(map);
                    List<Pawn> crew = Colonists(map).OrderBy(x => x.thingIDNumber).Where(x => !x.Downed && !x.WorkTypeIsDisabled(WorkTypeDefOf.Mining)).Take(1).ToList();
                    Teleport(crew[0], rfCaveE);
                    MdWork(map, crew, WorkTypeDefOf.Mining);
                    map.designationManager.AddDesignation(new Designation(rfCaveE + rfCaveD, DesignationDefOf.Mine));
                    Info($"R5 cave of {rfArea.Count} cells round {c0} (fogged {RfFogged(rfArea, map)}), sealed by rock; {crew[0]} mines {rfCaveE + rfCaveD}");
                    MuRun(200, 71750, () => (rfCaveE + rfCaveD).GetEdifice(map) == null, "rf_r5_first");
                    return true;
                }

                case "rf_r5_first":
                {
                    int fog = RfFogged(rfArea, map);
                    Wait(2, "rf_r5_first2");
                    Info($"R5 first rock mined {(rfCaveE + rfCaveD).GetEdifice(map) == null}; cave fogged {fog}");
                    return true;
                }

                case "rf_r5_first2":
                {
                    int fog = RfFogged(rfArea, map);
                    Result("R5 colonists mined towards a cave sealed by rock: it stays fogged while rock still seals it", (rfCaveE + rfCaveD).GetEdifice(map) == null && fog == rfArea.Count,
                        $"cave cells fogged {fog} of {rfArea.Count}; runs {ReachFog.Runs}");
                    map.designationManager.AddDesignation(new Designation(rfCaveE + rfCaveD * 2, DesignationDefOf.Mine));
                    MuRun(200, 71751, () => (rfCaveE + rfCaveD * 2).GetEdifice(map) == null, "rf_r5_second");
                    return true;
                }

                case "rf_r5_second":
                    Wait(2, "rf_r5_check");
                    return true;

                case "rf_r5_check":
                {
                    int fog = RfFogged(rfArea, map);
                    int ceil = rfArea.Count(c => { IntVec3 u = FloorRoof.Above(c, map); return u.IsValid && map.fogGrid.IsFogged(u); });
                    Result("R5 they broke through: the cave is revealed, its ceiling too", fog == 0 && ceil == 0, $"cave cells fogged {fog}, ceilings fogged {ceil} of {rfArea.Count}");
                    bool ok = RfAllReached(map, rfBefore, out string why);
                    Result("R5 everything revealed is reachable", ok, why);
                    MuShot(map, "reachfog_r5_cave_L0", 0, rfCaveE + rfCaveD * 3, 12f, "rf_r5_end");
                    return true;
                }

                case "rf_r5_end":
                    RfNext("rf_r6");
                    return true;

                // ------------------------------------------------------------ R6 a room behind a closed door
                case "rf_r6":
                {
                    RfFixture(map);
                    IntVec3 c0 = rfRoomE + rfRoomD * 3;
                    rfArea.Clear();
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            rfArea.Add(c0 + new IntVec3(dx, 0, dz));
                        }
                    }
                    IntVec3 door = rfRoomE + rfRoomD;
                    HfCarve(map, new[] { door }, true);
                    Thing d = ThingMaker.MakeThing(ThingDefOf.Door, ThingDefOf.WoodLog);
                    d.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(d, door, map, Rot4.North);
                    HfCarve(map, rfArea, false);
                    // the room: an ancient sealed pocket; a door in its only wall gap
                    Wait(3, "rf_r6_closed");
                    return true;
                }

                case "rf_r6_closed":
                {
                    ReachFogMap.For(map)?.Forget();
                    ReachFog.Run(map);
                    int fog = RfFogged(rfArea, map);
                    Building_Door door = (rfRoomE + rfRoomD).GetEdifice(map) as Building_Door;
                    Result("R6 a room behind a closed door stays fogged (the walk stops at the door)", door != null && !door.Open && fog == rfArea.Count,
                        $"door {door} open {door?.Open}, room cells fogged {fog} of {rfArea.Count}");
                    rfBefore = RfSnap(map);
                    Pawn p = Colonists(map).OrderBy(x => x.thingIDNumber).First(x => !x.Downed);
                    Teleport(p, rfRoomE);
                    Draft(p, true);
                    rfPawn = p;
                    p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, rfRoomE + rfRoomD), JobTag.DraftedOrder);
                    MuRun(60, 71760, () => rfPawn.Position == rfRoomE + rfRoomD, "rf_r6_opened");
                    return true;
                }

                case "rf_r6_opened":
                    Wait(2, "rf_r6_check");
                    return true;

                case "rf_r6_check":
                {
                    int fog = RfFogged(rfArea, map);
                    Result("R6 a drafted colonist ordered into the doorway opened the door: the room is revealed", rfPawn.Position == rfRoomE + rfRoomD && fog == 0,
                        $"{rfPawn} at {rfPawn.Position}; room cells fogged {fog} of {rfArea.Count}");
                    bool ok = RfAllReached(map, rfBefore, out string why);
                    Result("R6 everything revealed is reachable", ok, why);
                    MuShot(map, "reachfog_r6_room_L0", 0, rfRoomE + rfRoomD * 2, 12f, "rf_r6_end");
                    return true;
                }

                case "rf_r6_end":
                    RfNext("rf_r7");
                    return true;

                // ------------------------------------------------------------ R7 cost
                case "rf_r7":
                {
                    RfFixture(map);
                    ReachFog.Run(map);
                    double full = 0, walk = 0, wall = 0;
                    int regions = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        ReachFogMap.For(map)?.Forget();
                        ReachFog.Run(map);
                        full += ReachFog.LastMs;
                        regions = ReachFog.LastRegions;
                    }
                    for (int k = 0; k < 10; k++)
                    {
                        ReachFog.Run(map);
                        walk += ReachFog.LastMs;
                    }
                    // a wall built on the ground (regions rebuilt, the jump index dirty): the run after it
                    List<Pawn> cols = Colonists(map);
                    IntVec3 home = new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z));
                    int walls = 0;
                    foreach (IntVec3 c in GenRadial.RadialCellsAround(home, 20f, false).Where(c => c.InBounds(map) && c.Standable(map) && c.GetFirstPawn(map) == null && c.GetFirstItem(map) == null && !c.Roofed(map)).Take(3).ToList())
                    {
                        GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog), c, map);
                        map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                        ReachFog.Run(map);
                        wall += ReachFog.LastMs;
                        walls++;
                    }
                    full /= 3;
                    walk /= 10;
                    wall /= Math.Max(1, walls);
                    Info($"PERF reach fog on {map.Size.x}x{map.Size.z} ({map.cellIndices.NumGridCells} cells, {regions} regions reached): full run {full:0.00} ms, walk only {walk:0.00} ms, after a wall {wall:0.00} ms; max of all runs {ReachFog.MaxMs:0.00} ms");
                    Result("R7 cost: a full run (as after a load) under 100 ms, a walk-only run and the run after a wall under 5 ms", full < 100 && walk < 5 && wall < 5,
                        $"full {full:0.00} ms, walk {walk:0.00} ms, after a wall {wall:0.00} ms");
                    RfNext(null);
                    return true;
                }

                // ------------------------------------------------------------ the player's save: the load-time pass
                case "rf_u_off":
                {
                    RfFixture(map);
                    rfBefore = RfSnap(map);
                    AnnexMapComponent a = ProjectZ.Annex(map);
                    Info($"loaded {rfUserSave} WITHOUT the rule: map {map.Size}, levels {a?.MinLevel}..{a?.MaxLevel}, tall mountains {Mountains.On(map)}; player pawns {string.Join(", ", RfPlayers(map).Select(p => $"{p} {p.Position} L{Levels.LevelOf(p.Position, map)}"))}");
                    HfFogStats(map, "save, rule off");
                    ReachFog.Off = false;
                    ReachFog.MaxMs = 0;
                    stage = "rf_u_on";
                    pendingLoad = rfUserSave;
                    return true;
                }

                case "rf_u_on":
                {
                    RfFixture(map);
                    if (ReachFog.Runs == 0)
                    {
                        return true;   // (the load-time run comes on the first frames)
                    }
                    int newly = 0;
                    for (int i = 0; i < rfBefore.Length && i < map.cellIndices.NumGridCells; i++)
                    {
                        newly += rfBefore[i] && !map.fogGrid.IsFogged(i) ? 1 : 0;
                    }
                    Info($"loaded {rfUserSave} WITH the rule: load-time run {ReachFog.MaxMs:0.0} ms ({ReachFog.LastRegions} regions reached), {newly} cell(s) newly revealed");
                    HfFogStats(map, "save, rule on");
                    bool ok = RfAllReached(map, rfBefore, out string why);
                    Result("U the load-time pass on the player's save: every walkable cell it revealed is reachable", ok, why);
                    // cost on the player's map
                    double full = 0, walk = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        ReachFogMap.For(map)?.Forget();
                        ReachFog.Run(map);
                        full += ReachFog.LastMs;
                    }
                    for (int k = 0; k < 10; k++)
                    {
                        ReachFog.Run(map);
                        walk += ReachFog.LastMs;
                    }
                    Info($"PERF on {rfUserSave} ({map.Size.x}x{map.Size.z}, {ReachFog.LastRegions} regions reached): full run {full / 3:0.00} ms, walk only {walk / 10:0.00} ms");
                    SetHour(map, 12);
                    rfShot = 0;
                    stage = "rf_u_shots";
                    return true;
                }

                case "rf_u_shots":
                {
                    AnnexMapComponent a = ProjectZ.Annex(map);
                    if (rfShot > a.MaxLevel)
                    {
                        RfNext(null);
                        return true;
                    }
                    List<Pawn> cols = Colonists(map);
                    IntVec3 mid = cols.Count == 0 ? map.Center : new IntVec3((int)cols.Average(p => ProjectZ.GroundCellOf(p.Position, map).x), 0, (int)cols.Average(p => ProjectZ.GroundCellOf(p.Position, map).z));
                    int f = rfShot++;
                    MuShot(map, $"reachfog_user_L{f}", f, mid, 30f, "rf_u_shots");
                    return true;
                }
            }
            return false;
        }
    }
}
