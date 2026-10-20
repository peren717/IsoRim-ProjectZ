using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // Mode routerperf: router timings on a big synthetic scene (run with -projectzfloors=9 -projectzmapsize=250
    // -projectzseed=7). Written against public game calls only, so the same file builds into the old router (1088f9d)
    // for the comparison. Scene (fixed seed): six towers of 4 to 8 storeys on a 200 x 200 field, rooms with doors on
    // every storey, stairs chaining the storeys (one or two per storey), bridges between neighbouring towers, and dead-end
    // stairs out onto landing islands.
    //   P1 per-request time: 200 fixed start/destination pairs between floors, FindPathNow timed, three rounds 300 ticks
    //      apart (the first cold): mean, p99, max;
    //   P2 a cross-floor fight (12 gunners on the ground, 6 colonists on a tower top), 2500 ticks: the time in path and
    //      reach calls (FindPathNow, the follower's path requests, CanReach, the router's tick) as a share of the ticks.
    public static partial class TestRunner
    {
        private static readonly List<CellRect> rpTowers = new List<CellRect>();
        private static readonly List<int> rpHeights = new List<int>();
        private static readonly List<(IntVec3, IntVec3)> rpPairs = new List<(IntVec3, IntVec3)>();
        private static readonly List<double> rpTimes = new List<double>();
        private static readonly List<string> rpRounds = new List<string>();
        private static int rpRound, rpStairs, rpDeadEnds, rpTicksLeft, rpFindsAt;
        private static double rpTickMs, rpPathMs, rpFindMaxAt;
        private static bool rpPatched;
        private static int rpDepth;
        private static long rpStart;
        private static Pawn rpPawn;
        private static readonly Dictionary<string, int> rpWhy = new Dictionary<string, int>();

        private static bool StepRouterPerf(Map map)
        {
            switch (stage)
            {
                case "rp_setup":
                    RpSetup(map);
                    return true;
                case "rp_round":
                    RpRound(map);
                    return true;
                case "rp_fight":
                    RpFightStart(map);
                    return true;
                case "rp_fighting":
                    RpFighting(map);
                    return true;
            }
            return false;
        }

        // ---------------------------------------------------------------------------------------------- timing hooks

        private static void RpPrefix()
        {
            if (rpDepth++ == 0)
            {
                rpStart = Stopwatch.GetTimestamp();
            }
        }

        private static void RpFinalizer()
        {
            if (--rpDepth == 0)
            {
                rpPathMs += (Stopwatch.GetTimestamp() - rpStart) * 1000.0 / Stopwatch.Frequency;
            }
        }

        // Test only (modes router2, routerperf): the synthetic floors are laid without columns, so the game's roof-support
        // checks (run when a wall or column goes, e.g. a fixture cleared) would drop them. Those checks are switched off
        // for these modes; the floors under test are never meant to fall.
        private static bool noCollapsePatched;

        private static bool NoCollapse() => false;

        private static void NoRoofCollapse(Map map)
        {
            map.roofCollapseBuffer.Clear();
            if (noCollapsePatched)
            {
                return;
            }
            noCollapsePatched = true;
            Harmony h = new Harmony("Peren.IsoRimProjectZ.test.nocollapse");
            HarmonyMethod skip = new HarmonyMethod(typeof(TestRunner), nameof(NoCollapse));
            foreach (System.Reflection.MethodBase m in AccessTools.GetDeclaredMethods(typeof(RoofCollapseCellsFinder)).Where(m => m.Name == "Notify_RoofHolderDespawned" || m.Name == "CheckAndRemoveCollpsingRoofs" || m.Name == "CheckCollapseFlyingRoofs"))
            {
                h.Patch(m, prefix: skip);
            }
        }

        private static void RpPatch()
        {
            if (rpPatched)
            {
                return;
            }
            rpPatched = true;
            Harmony h = new Harmony("Peren.IsoRimProjectZ.test.routerperf");
            HarmonyMethod pre = new HarmonyMethod(typeof(TestRunner), nameof(RpPrefix));
            HarmonyMethod fin = new HarmonyMethod(typeof(TestRunner), nameof(RpFinalizer));
            List<System.Reflection.MethodBase> targets = new List<System.Reflection.MethodBase>
            {
                AccessTools.Method(typeof(PathFinder), nameof(PathFinder.FindPathNow), new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathFinderCostTuning?), typeof(PathEndMode), typeof(PathRequest.IPathGridCustomizer) }),
                AccessTools.Method(typeof(Pawn_PathFollower), "GenerateNewPathRequest"),
                AccessTools.Method(typeof(Reachability), nameof(Reachability.CanReach), new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) }),
            };
            Type rg = AccessTools.TypeByName("ProjectZ.Patches.RouteGraph");
            if (rg != null)
            {
                targets.Add(AccessTools.Method(rg, "MapComponentTick"));
            }
            foreach (System.Reflection.MethodBase m in targets)
            {
                if (m != null)
                {
                    h.Patch(m, prefix: pre, finalizer: fin);
                }
            }
            Info($"routerperf: timing hooks on {string.Join(", ", targets.Where(m => m != null).Select(m => m.DeclaringType.Name + "." + m.Name))}");
        }

        // ---------------------------------------------------------------------------------------------- scene

        private static void RpFloor(Map map, CellRect rect, int L)
        {
            foreach (IntVec3 g in rect)
            {
                IntVec3 below = Levels.Cell(g, L - 1, map);
                IntVec3 c = Levels.Cell(g, L, map);
                if (!c.IsValid || !below.IsValid)
                {
                    continue;
                }
                map.roofGrid.SetRoof(below, RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(c, PZDefOf.PZ_UpperFloor);
            }
        }

        private static void RpSetup(Map map)
        {
            DebugSettings.enableRandomMentalStates = false;
            NoRoofCollapse(map);
            Find.Storyteller.storytellerComps.Clear();
            Find.PlaySettings.autoRebuild = false;
            AnnexMapComponent annex = ProjectZ.Annex(map);
            if (annex == null || annex.MaxLevel < 8 || map.Size.x < 250)
            {
                Result("routerperf: a 9-level map 250 wide", false, $"levels {Levels.Min(map)}..{Levels.Max(map)}, size {map.Size} (run with -projectzfloors=9 -projectzmapsize=250)");
                Finish();
                return;
            }
            Stopwatch build = Stopwatch.StartNew();
            foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
            {
                a.Destroy();
            }
            CellRect field = new CellRect(25, 25, 200, 200);
            // the field: plain soil, nothing standing, no roofs, no fog (all levels)
            for (int L = Levels.Max(map); L >= 0; L--)
            {
                foreach (IntVec3 g in field)
                {
                    IntVec3 c = Levels.Cell(g, L, map);
                    if (!c.IsValid)
                    {
                        continue;
                    }
                    List<Thing> list = c.GetThingList(map);
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (!(list[i] is Pawn) && list[i].def.destroyable)
                        {
                            list[i].Destroy();
                        }
                    }
                    if (L == 0)
                    {
                        map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                    }
                    map.roofGrid.SetRoof(c, null);
                    if (map.fogGrid.IsFogged(c))
                    {
                        map.fogGrid.Unfog(c);
                    }
                }
            }
            Rand.PushState((SeedArg ?? 7) * 7919);
            try
            {
                rpTowers.Clear();
                rpHeights.Clear();
                for (int ty = 0; ty < 2; ty++)
                {
                    for (int tx = 0; tx < 3; tx++)
                    {
                        CellRect t = new CellRect(35 + tx * 62, 50 + ty * 90, 34, 34);
                        rpTowers.Add(t);
                        rpHeights.Add(Rand.RangeInclusive(4, 8));
                    }
                }
                for (int i = 0; i < rpTowers.Count; i++)
                {
                    CellRect t = rpTowers[i];
                    for (int L = 1; L <= rpHeights[i]; L++)
                    {
                        RpFloor(map, t, L);
                    }
                    // bridges to the east neighbour on two storeys
                    if (i % 3 != 2)
                    {
                        for (int b = 0; b < 2; b++)
                        {
                            int L = Rand.RangeInclusive(1, Math.Min(rpHeights[i], rpHeights[i + 1]));
                            int z = t.minZ + Rand.Range(4, t.Height - 6);
                            RpFloor(map, new CellRect(t.maxX + 1, z, rpTowers[i + 1].minX - t.maxX - 1, 3), L);
                        }
                    }
                }
                // rooms: walls with doors on every storey of every tower (and on the ground inside them)
                for (int i = 0; i < rpTowers.Count; i++)
                {
                    CellRect t = rpTowers[i];
                    for (int L = 0; L <= rpHeights[i]; L++)
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            bool horiz = Rand.Bool;
                            int at = horiz ? t.minZ + Rand.Range(6, t.Height - 6) : t.minX + Rand.Range(6, t.Width - 6);
                            int door1 = Rand.Range(2, (horiz ? t.Width : t.Height) - 2);
                            int door2 = Rand.Range(2, (horiz ? t.Width : t.Height) - 2);
                            for (int s = 1; s < (horiz ? t.Width : t.Height) - 1; s++)
                            {
                                IntVec3 g = horiz ? new IntVec3(t.minX + s, 0, at) : new IntVec3(at, 0, t.minZ + s);
                                IntVec3 c = Levels.Cell(g, L, map);
                                if (!c.IsValid || c.GetEdifice(map) != null || !c.Standable(map))
                                {
                                    continue;
                                }
                                Place(s == door1 || s == door2 ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
                            }
                        }
                    }
                }
                // stairs chaining the storeys, and dead ends out over the tower's edge
                rpStairs = rpDeadEnds = 0;
                rpWhy.Clear();
                for (int i = 0; i < rpTowers.Count; i++)
                {
                    CellRect t = rpTowers[i];
                    for (int L = 0; L < rpHeights[i]; L++)
                    {
                        int want = Rand.RangeInclusive(1, 2), got = 0;
                        for (int tries = 0; tries < 60 && got < want; tries++)
                        {
                            IntVec3 c = Levels.Cell(new IntVec3(t.minX + Rand.Range(3, t.Width - 3), 0, t.minZ + Rand.Range(3, t.Height - 3)), L, map);
                            Rot4 rot = new Rot4(Rand.Range(0, 4));
                            // (the stairwell: the floor over the footprint is opened first when nothing stands on it, as a player
                            // would before building stairs under a storey that has another storey over it)
                            RpOpenWell(map, c, rot);
                            AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, c, rot, map, stuffDef: ThingDefOf.WoodLog);
                            if (!ok.Accepted)
                            {
                                rpWhy[ok.Reason ?? "?"] = rpWhy.TryGetValue(ok.Reason ?? "?", out int nw) ? nw + 1 : 1;
                                foreach (IntVec3 u in rpOpened)
                                {
                                    map.terrainGrid.SetTerrain(u, PZDefOf.PZ_UpperFloor);   // (put back: no stairs here)
                                }
                            }
                            if (ok.Accepted)
                            {
                                Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, c, map, rot);
                                got++;
                                rpStairs++;
                            }
                        }
                        // a dead end: stairs at the tower's west or east edge facing out (the exit a landing island)
                        if (L < rpHeights[i] && Rand.Chance(0.6f))
                        {
                            for (int tries = 0; tries < 20; tries++)
                            {
                                bool west = Rand.Bool;
                                IntVec3 g = new IntVec3(west ? t.minX + 1 : t.maxX - 1, 0, t.minZ + Rand.Range(3, t.Height - 3));
                                IntVec3 c = Levels.Cell(g, L, map);
                                Rot4 rot = west ? Rot4.West : Rot4.East;
                                RpOpenWell(map, c, rot);
                                if (!GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, c, rot, map, stuffDef: ThingDefOf.WoodLog).Accepted)
                                {
                                    foreach (IntVec3 u in rpOpened)
                                    {
                                        map.terrainGrid.SetTerrain(u, PZDefOf.PZ_UpperFloor);
                                    }
                                }
                                else
                                {
                                    Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, c, map, rot);
                                    rpDeadEnds++;
                                    break;
                                }
                            }
                        }
                    }
                }
                // the pairs: walkable cells on random storeys of random towers (and the ground between them)
                rpPairs.Clear();
                for (int n = 0; n < 200; n++)
                {
                    IntVec3 a = RpCell(map), b = RpCell(map);
                    if (a.IsValid && b.IsValid && Levels.LevelOf(a, map) != Levels.LevelOf(b, map))
                    {
                        rpPairs.Add((a, b));
                    }
                }
            }
            finally
            {
                Rand.PopState();
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            rpPawn = Colonists(map).First();
            foreach (Pawn p in Colonists(map))
            {
                Draft(p, true);
            }
            RpPatch();
            Info($"routerperf: {rpTowers.Count} towers (storeys {string.Join(",", rpHeights)}), stairs {rpStairs} + dead ends {rpDeadEnds}, pairs {rpPairs.Count}; stairs refused: {string.Join("; ", rpWhy.OrderByDescending(kv => kv.Value).Take(4).Select(kv => kv.Value + "x " + kv.Key))}; built in {build.ElapsedMilliseconds} ms; regions {map.regionGrid.AllRegions.Count()}");
            rpRound = 0;
            rpTimes.Clear();
            rpRounds.Clear();
            stage = "rp_round";
        }

        private static readonly List<IntVec3> rpOpened = new List<IntVec3>();

        private static void RpOpenWell(Map map, IntVec3 c, Rot4 rot)
        {
            rpOpened.Clear();
            int L = Levels.LevelOf(c, map);
            foreach (IntVec3 f in StairShape.Footprint(c, rot))
            {
                if (!f.InBounds(map) || Levels.LevelOf(f, map) != L)
                {
                    return;
                }
            }
            foreach (IntVec3 f in StairShape.Footprint(c, rot))
            {
                IntVec3 up = Levels.Cell(Levels.Ground(f, map), L + 1, map);
                if (up.IsValid && ProjectZ.HasFloor(up, map) && up.GetEdifice(map) == null && up.GetThingList(map).Count == 0)
                {
                    map.terrainGrid.SetTerrain(up, PZDefOf.PZ_OpenAir);
                    rpOpened.Add(up);
                }
            }
        }

        // (router2 only, read by name so this file builds into the old router too)
        private static string RpBreakdown()
        {
            string s = "";
            foreach (string n in new[] { "findMsTotal", "routerLegMs", "floorReachMs", "routerLegsChecked", "routerDeferred", "routerBudgetWaits", "routerWaitTicksMax", "reachRuns", "reachFastNo", "stairReach" })
            {
                System.Reflection.FieldInfo fi = typeof(PZStats).GetField(n);
                if (fi != null)
                {
                    object v = fi.GetValue(null);
                    s += $"{n} {(v is double d ? d.ToString("0.0") : v)}, ";
                }
            }
            return s.Length == 0 ? "(no router2 counters)" : "totals so far: " + s.TrimEnd(' ', ',');
        }

        private static IntVec3 RpCell(Map map)
        {
            for (int tries = 0; tries < 50; tries++)
            {
                int i = Rand.Range(0, rpTowers.Count);
                CellRect t = rpTowers[i];
                int L = Rand.RangeInclusive(0, rpHeights[i]);
                IntVec3 c = Levels.Cell(new IntVec3(t.minX + Rand.Range(1, t.Width - 1), 0, t.minZ + Rand.Range(1, t.Height - 1)), L, map);
                if (c.IsValid && c.Standable(map) && c.GetEdifice(map) == null)
                {
                    return c;
                }
            }
            return IntVec3.Invalid;
        }

        // ---------------------------------------------------------------------------------------------- P1

        private static void RpRound(Map map)
        {
            if (rpRound >= 3)
            {
                List<double> all = rpTimes.OrderBy(x => x).ToList();
                double mean = all.Count > 0 ? all.Average() : 0;
                double p99 = all.Count > 0 ? all[Math.Min(all.Count - 1, (int)(all.Count * 0.99))] : 0;
                double max = all.Count > 0 ? all[all.Count - 1] : 0;
                Info($"PERF P1 per request (FindPathNow between floors, {all.Count} calls over 3 rounds): mean {mean:0.000} ms, p99 {p99:0.000} ms, max {max:0.000} ms; rounds: {string.Join(" | ", rpRounds)}; {RpBreakdown()}");
                Result("P1 per-request timings measured", all.Count > 0, $"mean {mean:0.000} ms, p99 {p99:0.000} ms, max {max:0.000} ms");
                stage = "rp_fight";
                return;
            }
            List<double> round = new List<double>();
            int found = 0;
            TraverseParms tp = TraverseParms.For(rpPawn);
            foreach ((IntVec3 a, IntVec3 b) in rpPairs)
            {
                rpPawn.Position = a;
                rpPawn.Notify_Teleported();
                Stopwatch sw = Stopwatch.StartNew();
                PawnPath p = map.pathFinder.FindPathNow(a, b, tp, PathFinderCostTuning.For(rpPawn), PathEndMode.OnCell);
                double ms = sw.Elapsed.TotalMilliseconds;
                if (p != null && p.Found)
                {
                    found++;
                }
                p?.Dispose();
                round.Add(ms);
            }
            rpTimes.AddRange(round);
            List<double> s = round.OrderBy(x => x).ToList();
            rpRounds.Add($"round {rpRound + 1}: found {found}/{round.Count}, mean {round.Average():0.000}, p99 {s[Math.Min(s.Count - 1, (int)(s.Count * 0.99))]:0.000}, max {s[s.Count - 1]:0.000} ms");
            rpRound++;
            StartTicks(300, "rp_round");
        }

        // ---------------------------------------------------------------------------------------------- P2

        private static void RpFightStart(Map map)
        {
            // six colonists on the top of the tallest tower, drafted, firing at will; twelve gunners on the ground
            int top = rpHeights.IndexOf(rpHeights.Max());
            CellRect t = rpTowers[top];
            List<Pawn> cols = Colonists(map);
            int i = 0;
            foreach (Pawn p in cols)
            {
                IntVec3 c = Levels.Cell(new IntVec3(t.CenterCell.x + i++ % 4, 0, t.CenterCell.z + i / 4), rpHeights[top], map);
                if (!c.Standable(map))
                {
                    c = CellFinder.StandableCellNear(c, map, 4f, x => Levels.LevelOf(x, map) == rpHeights[top]);
                }
                if (c.IsValid)
                {
                    Teleport(p, c);
                }
                Draft(p, true);
                if (p.drafter != null)
                {
                    p.drafter.FireAtWill = true;
                }
            }
            List<Pawn> raiders = new List<Pawn>();
            for (int k = 0; k < 12; k++)
            {
                IntVec3 c = CellFinder.StandableCellNear(new IntVec3(30 + k * 3, 0, 30), map, 5f);
                raiders.Add(MakeRaider(map, c));
            }
            LordMaker.MakeNewLord(raiders[0].Faction, new LordJob_AssaultColony(raiders[0].Faction, canKidnap: false, canTimeoutOrFlee: false, canSteal: false), map, raiders);
            rpTicksLeft = 2500;
            rpTickMs = 0;
            rpPathMs = 0;
            rpFindsAt = PZStats.finds;
            rpFindMaxAt = PZStats.findMsMax;
            PZStats.findMsMax = 0;
            stage = "rp_fighting";
        }

        private static void RpFighting(Map map)
        {
            TickManager tm = Find.TickManager;
            Stopwatch frame = Stopwatch.StartNew();
            while (rpTicksLeft > 0 && frame.ElapsedMilliseconds < 400)
            {
                long t0 = Stopwatch.GetTimestamp();
                tm.DoSingleTick();
                rpTickMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                rpTicksLeft--;
            }
            if (rpTicksLeft > 0)
            {
                return;
            }
            int hostilesUp = map.mapPawns.AllPawnsSpawned.Count(p => p.HostileTo(Faction.OfPlayer) && Levels.LevelOf(p.Position, map) >= 1);
            int hostiles = map.mapPawns.AllPawnsSpawned.Count(p => p.HostileTo(Faction.OfPlayer) && !p.Downed);
            double share = rpTickMs > 0 ? rpPathMs / rpTickMs : 0;
            Info($"PERF P2 fight 2500 ticks: ticks {rpTickMs:0} ms ({rpTickMs / 2500:0.000} ms/tick), path+reach calls {rpPathMs:0} ms = {share:P1} of the tick time; router searches {PZStats.finds - rpFindsAt}, slowest {PZStats.findMsMax:0.00} ms; hostiles standing {hostiles}, upstairs {hostilesUp}; {RpBreakdown()}");
            Result("P2 fight timings measured", rpTickMs > 0, $"path+reach share {share:P1}, {rpTickMs / 2500:0.000} ms/tick");
            Finish();
        }
    }
}
