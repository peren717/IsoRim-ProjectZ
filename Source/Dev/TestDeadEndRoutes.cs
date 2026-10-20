using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ProjectZ.Patches;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode deadend, part 2 (branch stairroute: the router's search is bounded and agrees with reachability). A second
    // house H2 (13 x 10, roofed, door south) beside the first, its upper floor D over the whole inside. Three "decoy"
    // stairs face west with their exits over the west wall (one-cell floor islands, as the dead-end stairs above); the
    // good stairs face north at the east end onto D. One colonist does cleaning only:
    //   r_a   a filth on D walled in on its floor (no way there at all): not reachable, no job, the router answers fast;
    //   r_fog a filth on the ground in a walled pocket under a hole in D, its landing cell fogged: before 2026-10-19 only a
    //         jump got in; with no jumps nothing does: not reachable, no job, the router answers fast, fogged or revealed;
    //         (rewritten for the jump removal: it used to expect the jump in once the landing was revealed)
    //   r_cap a filth on D, the colonist outside H2's west wall: the three decoy stairs come first in straight-line order
    //         and all lead nowhere, the good one ranks 4th: the router (A* over the stairs' ends) takes the good stairs.
    public static partial class TestRunner
    {
        private static int cOx, cOz;
        private static Pawn cPawn;
        private static Thing cFilth;
        private static IntVec3 cUa, cA, cB, cUb, cWest;
        private static bool cJobSeen;
        private static int cFindsAt, cJumpsAt, cRegionAt, cFailsAt;
        private static readonly IntVec3[] cDecoys = new IntVec3[3];
        private static IntVec3 cGood;

        private static IntVec3 CG(int x, int z) => new IntVec3(cOx + x, 0, cOz + z);

        private static bool CJobOn(Thing t) => cPawn?.CurJob != null && (cPawn.CurJob.targetA.Thing == t || cPawn.CurJob.targetQueueA != null && cPawn.CurJob.targetQueueA.Any(q => q.Thing == t));

        private static Thing CSpawnFilth(IntVec3 c, Map map)
        {
            Thing f = ThingMaker.MakeThing(ThingDefOf.Filth_Dirt);
            GenSpawn.Spawn(f, c, map);
            map.areaManager.Home[c] = true;
            return f;
        }

        // A timed path request from the colonist to the filth, as the game makes it (FindPathNow -> StairRouter.Find; the
        // other router calls of the phase, e.g. the colonist wandering, are reported but not judged):
        // ms and found.
        private static (double ms, bool found) CTimedPath(Map map, Thing t)
        {
            Stopwatch sw = Stopwatch.StartNew();
            PawnPath p = map.pathFinder.FindPathNow(cPawn.Position, t, TraverseParms.For(cPawn), PathFinderCostTuning.For(cPawn), PathEndMode.Touch);
            sw.Stop();
            bool found = p != null && p.Found;
            p?.Dispose();
            return (sw.Elapsed.TotalMilliseconds, found);
        }

        private static void CMark()
        {
            cJobSeen = false;
            cFindsAt = PZStats.finds;
            cJumpsAt = PZStats.jumps;
            cRegionAt = PZStats.regionRoutes;
            cFailsAt = kPatherFails;
            PZStats.findMsMax = 0;
        }

        private static string CStats() => $"router finds {PZStats.finds - cFindsAt} (slowest {PZStats.findMsMax:0.00} ms), region-graph routes {PZStats.regionRoutes - cRegionAt}, jumps {PZStats.jumps - cJumpsAt}, jobs ended for want of a path {kPatherFails - cFailsAt}";

        private static bool StepRouteCases(Map map)
        {
            switch (stage)
            {
                case "k_r_setup":
                    SetupRouteCases(map);
                    return true;
                case "k_r_a":
                {
                    tickWatch = null;
                    bool reach = cPawn.CanReach(cFilth, PathEndMode.Touch, Danger.Deadly);
                    (double ms, bool found) = CTimedPath(map, cFilth);
                    Result("routes: a filth walled in on the upper floor is not reachable, never given as a job, and the router says no at once",
                        !reach && !found && !cJobSeen && !cFilth.Destroyed && ms < 5,
                        $"reach {reach}, path {found} in {ms:0.00} ms, job seen {cJobSeen}, filth left {!cFilth.Destroyed}; {CStats()}");
                    cFilth.Destroy();
                    // r_fog: the pocket's landing fogged
                    map.fogGrid.Refog(CellRect.SingleCell(cA));
                    cFilth = CSpawnFilth(cB, map);
                    CMark();
                    Info($"routes: fog case: landing {cA} fogged {cA.Fogged(map)}, filth {cB} fogged {cB.Fogged(map)}, colonist {cPawn} at {cPawn.Position}");
                    TickUntil(2500, () => cJobSeen |= CJobOn(cFilth), "k_r_fog", 20);
                    return true;
                }
                case "k_r_fog":
                {
                    tickWatch = null;
                    bool reach = cPawn.CanReach(cFilth, PathEndMode.Touch, Danger.Deadly);
                    (double ms, bool found) = CTimedPath(map, cFilth);
                    Result("routes: a pocket under a hole (only a jump would get in: no jumps since 2026-10-19), landing fogged: not reachable, never given as a job, the router says no at once",
                        !reach && !found && !cJobSeen && !cFilth.Destroyed && ms < 5,
                        $"reach {reach}, path {found} in {ms:0.00} ms, job seen {cJobSeen}, filth left {!cFilth.Destroyed}; {CStats()}");
                    map.fogGrid.Unfog(cA);
                    bool reach2 = cPawn.CanReach(cFilth, PathEndMode.Touch, Danger.Deadly);
                    Result("routes: the landing revealed, the pocket is still not reachable (fog never decided it)", !reach2 && !cA.Fogged(map), $"reach {reach2}, landing fogged {cA.Fogged(map)}");
                    CMark();
                    TickUntil(1500, () => (cJobSeen |= CJobOn(cFilth)) && false, "k_r_fog_clean", 30);
                    return true;
                }
                case "k_r_fog_clean":
                {
                    tickWatch = null;
                    Result("routes: revealed, still no job for the pocket's filth, nobody went in (no jump), no job ended for want of a path", !cFilth.Destroyed && !cJobSeen && PZStats.jumps == cJumpsAt && kPatherFails == cFailsAt,
                        $"filth left {!cFilth.Destroyed} after {ticksTotal} ticks, job seen {cJobSeen}, colonist at {cPawn.Position}; {CStats()}");
                    // r_cap: out of the pocket, west of H2 (the pocket filth gone either way, so it does not draw the cleaner)
                    if (!cFilth.Destroyed)
                    {
                        cFilth.Destroy();
                    }
                    // the hole is closed again (a fixture for the next case only), and out with anything that is in the pocket
                    map.terrainGrid.SetTerrain(UpS(cA, map), PZDefOf.PZ_UpperFloor);
                    map.roofGrid.SetRoof(cA, RoofDefOf.RoofConstructed);
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(p => p.Position == cA || p.Position == cB).ToList())
                    {
                        if (p != cPawn)
                        {
                            p.Destroy();
                        }
                    }
                    Teleport(cPawn, cWest);
                    Info($"routes: hole closed, cleaner {cPawn} now at {cPawn.Position} (west of H2: {cWest})");
                    cFilth = CSpawnFilth(cUb, map);
                    StairGrid g = StairGrid.For(map);
                    IntVec3 fromG = cWest, destG = ProjectZ.GroundCellOf(cUb, map);
                    List<StairLink> ups = g.links.Where(l => l.floor == 0 && g.Passable(l, TraverseParms.For(cPawn))).OrderBy(l => COctile(fromG, ProjectZ.GroundCellOf(l.bottom, map)) + COctile(ProjectZ.GroundCellOf(l.bottom, map), destG)).ToList();
                    int goodRank = ups.FindIndex(l => l.stairs.Position == cGood) + 1;
                    int decoysFirst = ups.Take(3).Count(l => cDecoys.Contains(l.stairs.Position));
                    Result("routes: set up so the three decoy stairs are the router's three best candidates and the good stairs come later", decoysFirst == 3 && goodRank > 3,
                        $"up stairs in straight-line order: {string.Join(", ", ups.Select(l => l.stairs.Position + (l.stairs.Position == cGood ? " (good)" : cDecoys.Contains(l.stairs.Position) ? " (decoy)" : "")))}; good rank {goodRank}");
                    CMark();
                    TickUntil(10000, () => cFilth.Destroyed, "k_r_cap", 60);
                    return true;
                }
                case "k_r_cap":
                {
                    tickWatch = null;
                    Result("routes: the colonist takes the 4th-best stairs up and cleans the filth, no job ended for want of a path, no slow search",
                        cFilth.Destroyed && kPatherFails == cFailsAt && PZStats.findMsMax < 50,
                        $"cleaned {cFilth.Destroyed} after {ticksTotal} ticks, colonist at {cPawn.Position}; {CStats()}; old router {OldRoute}");
                    stage = "k_end";
                    return true;
                }
            }
            return false;
        }

        private static int COctile(IntVec3 a, IntVec3 b)
        {
            int dx = Math.Abs(a.x - b.x), dz = Math.Abs(a.z - b.z);
            return 10 * Math.Max(dx, dz) + 4 * Math.Min(dx, dz);
        }

        private static void SetupRouteCases(Map map)
        {
            // H2: the first open 19 x 16 spot east or west of the first house (cleared field, no fog, no rock)
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(kHouse.CenterCell, 75f, true))
            {
                CellRect r = new CellRect(c.x, c.z, 13, 10);
                CellRect m = r.ExpandedBy(6);
                if (!m.InBounds(map) || m.maxZ >= ProjectZ.GroundZ(map) - 12 || c.DistanceTo(kHouse.CenterCell) < 30f)
                {
                    continue;
                }
                if (m.Cells.All(x => !x.GetTerrain(map).IsWater && !map.roofGrid.Roofed(x) && !x.Fogged(map) && !(x.GetEdifice(map)?.def.building?.isNaturalRock ?? false) && x.GetTerrain(map).passability == Traversability.Standable && x.GetFirstBuilding(map) == null))
                {
                    found = r;
                    break;
                }
            }
            if (found.Area == 0)
            {
                Result("routes: test spot", false, "no open ground near the first house");
                stage = "k_end";
                return;
            }
            foreach (IntVec3 c in found.ExpandedBy(6))
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable && (t is Plant || t.def.category == ThingCategory.Item || t is Filth))
                    {
                        t.Destroy();
                    }
                }
            }
            cOx = found.minX;
            cOz = found.minZ;
            CellRect h2 = found;
            IntVec3 door = CG(4, 0);
            foreach (IntVec3 c in h2.EdgeCells)
            {
                Place(c == door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            foreach (IntVec3 c in h2)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            cDecoys[0] = CG(2, 1);
            cDecoys[1] = CG(2, 4);
            cDecoys[2] = CG(2, 7);
            cGood = CG(10, 2);
            // the pocket: ground cells A (under a hole in D, roofless) and B (the filth) walled in
            cA = CG(7, 2);
            cB = CG(7, 1);
            foreach (IntVec3 w in new[] { CG(6, 1), CG(6, 2), CG(6, 3), CG(8, 1), CG(8, 2), CG(8, 3), CG(7, 3) })
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, w, map);
            }
            map.roofGrid.SetRoof(cA, null);
            HashSet<IntVec3> wells = new HashSet<IntVec3>(cDecoys.SelectMany(p => StairShape.Footprint(p, Rot4.West).Cells).Concat(StairShape.Footprint(cGood, Rot4.North).Cells));
            foreach (IntVec3 g in h2.ContractedBy(1))
            {
                if (!wells.Contains(g))
                {
                    map.terrainGrid.SetTerrain(UpS(g, map), g == cA ? PZDefOf.PZ_OpenAir : PZDefOf.PZ_UpperFloor);
                }
            }
            foreach (IntVec3 p in cDecoys)
            {
                AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, p, Rot4.West, map, stuffDef: ThingDefOf.WoodLog);
                Info($"routes: decoy stairs {p} placement {(ok.Accepted ? "accepted" : "refused: " + ok.Reason)}");
                Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, p, map, Rot4.West);
            }
            AcceptanceReport okG = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, cGood, Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
            Info($"routes: good stairs {cGood} placement {(okG.Accepted ? "accepted" : "refused: " + okG.Reason)}");
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, cGood, map, Rot4.North);
            // r_a: a filth on D walled in on its floor
            cUa = UpS(CG(4, 7), map);
            foreach (IntVec3 g in GenAdj.CellsAdjacent8Way(new TargetInfo(CG(4, 7), map)))
            {
                if (h2.ContractedBy(1).Contains(g) && !wells.Contains(g))
                {
                    Place(ThingDefOf.Wall, ThingDefOf.WoodLog, UpS(g, map), map);
                }
            }
            cUb = UpS(CG(4, 4), map);
            cWest = CG(-4, 4);
            foreach (IntVec3 g in h2)
            {
                map.areaManager.Home[g] = true;
                map.areaManager.Home[UpS(g, map)] = true;
            }
            map.areaManager.Home[cB] = true;
            // one cleaner; the others do nothing and stand far off
            List<Pawn> cols = Colonists(map);
            cPawn = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Cleaning));
            if (cPawn == null)
            {
                Result("routes: a colonist who can clean", false, "none");
                stage = "k_end";
                return;
            }
            foreach (Pawn p in cols)
            {
                p.jobs?.EndCurrentJob(JobCondition.InterruptForced);
                foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefs)
                {
                    if (!p.WorkTypeIsDisabled(w))
                    {
                        p.workSettings.SetPriority(w, p == cPawn && w == WorkTypeDefOf.Cleaning ? 1 : 0);
                    }
                }
            }
            // no other filth anywhere near (the colonist must clean ours)
            foreach (Thing f in map.listerThings.ThingsInGroup(ThingRequestGroup.Filth).ToList())
            {
                f.Destroy();
            }
            Teleport(cPawn, UpS(CG(10, 6), map));
            Region deadExit = map.regionGrid.GetValidRegionAt(UpS(CG(0, 4), map));
            Region aRegion = map.regionGrid.GetValidRegionAt(cA);
            Result("routes: set up (4 stairs in H2, decoy exits one-cell islands, the pocket a 2-cell region under a hole)",
                StairGrid.For(map)?.Count == 6 && deadExit != null && deadExit.CellCount == 1 && UpS(cA, map).GetTerrain(map) == PZDefOf.PZ_OpenAir && aRegion != null,
                $"H2 {h2}, links {StairGrid.For(map)?.Count}, decoy exit region cells {deadExit?.CellCount}, hole {UpS(cA, map).GetTerrain(map)?.defName}, pocket region cells {aRegion?.CellCount}, cleaner {cPawn}");
            cFilth = CSpawnFilth(cUa, map);
            CMark();
            TickUntil(2500, () => cJobSeen |= CJobOn(cFilth), "k_r_a", 20);
        }
    }
}
