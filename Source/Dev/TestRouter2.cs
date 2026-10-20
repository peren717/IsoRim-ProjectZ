using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // Mode router2 (isorim-pz-pathing-redesign.md: the two-level stair-end router, no jumps). Run with -projectzfloors=3
    // -projectzseed=7. Every check builds its own fixture in one cleared area (all levels wiped first: isolated), with
    // its own fixed random state, and runs real orders and jobs with real ticks:
    //   O   an oracle on 30 seeded random 3-level layouts (walls, doors, floors, stairs): for random start/destination pairs
    //       the route the game gets (FindPathNow: router between floors, the game's path + option C on one floor) is
    //       compared with every route over the stairs enumerated with real legs (StairRouter.OracleCost): between floors
    //       the cost must equal the cheapest (never more), on one floor be within option C's 20 %; a walk per layout;
    //   A   admissibility: every real leg cost >= its estimate (counted over the whole run);
    //   B   floor-bridge shortcut, below and above the 20 % threshold;  T  turn-around only at departure;
    //   W   wasted work in a busy two-floor colony (same-floor requests that ran the stairs route);
    //   U   an unreachable target (no freeze, no job loop); D  dead-end staircases; DR  a door forbidden and allowed;
    //   PB  a pawn on a stair end; F  burning stairs; FG  stairs in fog; M  melee across a stair step;
    //   RC  raiders crossing floors; SP  sappers; X  exit map from upstairs; K  taking up the last stairs (low side,
    //       top side, refused); L  a pawn on a ledge only a jump would leave stays put.
    public static partial class TestRunner
    {
        private static CellRect r2Area;
        private static Pawn r2P, r2Q;
        private static int r2Seed, r2SeedsLeft, r2Pairs, r2PairsOk, r2Mismatch, r2RouterCheaper, r2Walks, r2WalksOk;
        private static readonly List<string> r2Bad = new List<string>();
        private static IntVec3 r2Dest;
        private static int r2ErrAt, r2FindsAt, r2ClimbsAt, r2HostClimbsAt, r2SameReqAt, r2SameRoutedAt, r2LegsAt;
        private static Thing r2Thing, r2Thing2;
        private static Building_Stairs r2SA, r2SB;
        private static int r2Walked, r2Switches;
        private static IntVec3 r2LastPos;
        private static PawnPath r2LastPath;
        private static IntVec3 r2FirstH;
        private static bool r2Flag;
        private static readonly List<string> r2Seen = new List<string>();

        private static IntVec3 RG(int x, int z) => new IntVec3(r2Area.minX + x, 0, r2Area.minZ + z);

        private static IntVec3 RL(int x, int z, int L, Map map) => Levels.Cell(RG(x, z), L, map);

        private static bool StepRouter2(Map map)
        {
            switch (stage)
            {
                case "r2_setup":
                    R2Setup(map);
                    return true;
                case "r2_o_next":
                    R2OracleNext(map);
                    return true;
                case "r2_o_walked":
                    R2OracleWalked(map);
                    return true;
                case "r2_o_done":
                    Result($"O oracle: on {30} seeded layouts the route between floors costs exactly the cheapest of all stair routes (real legs), on one floor within option C's 20 %",
                        r2Pairs > 0 && r2Mismatch == 0 && r2PairsOk == r2Pairs,
                        $"pairs {r2Pairs}, ok {r2PairsOk}, mismatches {r2Mismatch}, router cheaper than the enumeration (legs chained through stair ends) {r2RouterCheaper}; first problems: {string.Join(" | ", r2Bad.Take(6))}");
                    Result("O walks: a pawn ordered across floors on each layout arrives", r2Walks > 0 && r2WalksOk == r2Walks, $"walks {r2Walks}, arrived {r2WalksOk}");
                    stage = "r2_b";
                    return true;
                case "r2_b":
                    R2Bridge(map, false);
                    return true;
                case "r2_b_check":
                    R2BridgeCheck(map, false);
                    return true;
                case "r2_b2":
                    R2Bridge(map, true);
                    return true;
                case "r2_b2_check":
                    R2BridgeCheck(map, true);
                    return true;
                case "r2_t":
                    R2Turn(map);
                    return true;
                case "r2_t_check":
                    R2TurnCheck(map);
                    return true;
                case "r2_w":
                    R2Busy(map);
                    return true;
                case "r2_w_check":
                    R2BusyCheck(map);
                    return true;
                case "r2_u":
                    R2Unreach(map);
                    return true;
                case "r2_u_check":
                    R2UnreachCheck(map);
                    return true;
                case "r2_u_order_check":
                    R2UnreachOrderCheck(map);
                    return true;
                case "r2_d":
                    R2DeadEnds(map);
                    return true;
                case "r2_d_check":
                    R2DeadEndsCheck(map);
                    return true;
                case "r2_dr":
                    R2Door(map);
                    return true;
                case "r2_dr_check":
                    R2DoorCheck(map);
                    return true;
                case "r2_pb":
                    R2Blocker(map);
                    return true;
                case "r2_pb_check":
                    R2BlockerCheck(map);
                    return true;
                case "r2_f":
                    R2Fire(map);
                    return true;
                case "r2_fg":
                    R2Fog(map);
                    return true;
                case "r2_m":
                    R2Melee(map);
                    return true;
                case "r2_m_check":
                    R2MeleeCheck(map);
                    return true;
                case "r2_rc":
                    R2Raid(map);
                    return true;
                case "r2_rc_check":
                    R2RaidCheck(map);
                    return true;
                case "r2_sp":
                    R2Sapper(map);
                    return true;
                case "r2_sp_check":
                    R2SapperCheck(map);
                    return true;
                case "r2_x":
                    R2Exit(map);
                    return true;
                case "r2_x_check":
                    R2ExitCheck(map);
                    return true;
                case "r2_k1":
                    R2Cut(map, 1);
                    return true;
                case "r2_k1_check":
                    R2CutCheck(map, 1);
                    return true;
                case "r2_k2":
                    R2Cut(map, 2);
                    return true;
                case "r2_k2_check":
                    R2CutCheck(map, 2);
                    return true;
                case "r2_k3":
                    R2Cut(map, 3);
                    return true;
                case "r2_k3_check":
                    R2CutCheck(map, 3);
                    return true;
                case "r2_l":
                    R2Ledge(map);
                    return true;
                case "r2_l_check":
                    R2LedgeCheck(map);
                    return true;
                case "r2_end":
                    Result("A admissibility: every real leg cost >= its estimate (whole run)", StairRouter.AdmissibilityViolations == 0, $"violations {StairRouter.AdmissibilityViolations}; last {StairRouter.LastViolation ?? "-"}; legs checked {PZStats.routerLegsChecked}");
                    Info($"router stats: finds {PZStats.finds} ({PZStats.findMsTotal:0.0} ms, slowest {PZStats.findMsMax:0.00} ms), routes {PZStats.routesBuilt}, legs {PZStats.routerLegsChecked} (refused by floor reach {PZStats.routerLegsRefused}, failed {PZStats.routerLegFails}), memo {PZStats.routeMemos}/{PZStats.routeMemoHits}, give-ups {PZStats.routeGiveUps}, deferred {PZStats.routerDeferred} (queue max {PZStats.routerQueueMax}, longest wait {PZStats.routerWaitTicksMax} ticks, budget waits {PZStats.routerBudgetWaits}); same-floor requests {PZStats.sameFloorRequests}, checked over stairs {PZStats.sameFloorRouted}; reach runs {PZStats.reachRuns} (fast no {PZStats.reachFastNo}, stairs {PZStats.stairReach}), floor questions {RouteGraph.FloorReachCalls} ({PZStats.floorReachMs:0.0} ms); time in real legs {PZStats.routerLegMs:0.0} ms; last leg failure {StairRouter.LastLegFail ?? "-"}");
                    Finish();
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------------ fixture

        private static void R2Setup(Map map)
        {
            DebugSettings.enableRandomMentalStates = false;
            NoRoofCollapse(map);
            Find.Storyteller.storytellerComps.Clear();   // no incidents during the test (test only)
            Find.PlaySettings.autoRebuild = false;
            foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
            {
                a.Destroy();
            }
            AnnexMapComponent annex = ProjectZ.Annex(map);
            if (annex == null || annex.MaxLevel < 2)
            {
                Result("router2: a map with 3 levels", false, $"levels {Levels.Min(map)}..{Levels.Max(map)} (run with -projectzfloors=3)");
                Finish();
                return;
            }
            r2Area = new CellRect(map.Center.x - 22, map.Center.z - 16, 44, 32);
            List<Pawn> cols = Colonists(map);
            r2P = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Cleaning) && !p.WorkTagIsDisabled(WorkTags.Violent)) ?? cols.First();
            r2Q = cols.FirstOrDefault(p => p != r2P);
            foreach (Pawn p in cols)
            {
                p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
            }
            R2Park(map);
            r2Seed = 0;
            r2SeedsLeft = 30;
            r2Pairs = r2PairsOk = r2Mismatch = r2RouterCheaper = r2Walks = r2WalksOk = 0;
            r2Bad.Clear();
            StairRouter.AdmissibilityViolations = 0;
            Info($"router2: area {r2Area}, walker {r2P}, second {r2Q}, levels {Levels.Min(map)}..{Levels.Max(map)}");
            stage = "r2_o_next";
        }

        // Every colonist but the fixture's pawns stands far off, drafted (no work), fed.
        private static void R2Park(Map map, params Pawn[] keep)
        {
            int i = 0;
            foreach (Pawn p in Colonists(map))
            {
                if (keep.Contains(p))
                {
                    continue;
                }
                IntVec3 c = R2ParkCell(map, i++);
                if (c.IsValid)
                {
                    Teleport(p, c);
                }
                Draft(p, true);
            }
        }

        private static IntVec3 R2ParkCell(Map map, int i)
        {
            return CellFinder.StandableCellNear(new IntVec3(8 + 2 * i, 0, 8), map, 6f);
        }

        // Raid fixtures: the colonists not in the fixture leave the map for the check (else the raiders go for them), and
        // come back after it.
        private static readonly List<Pawn> r2Hidden = new List<Pawn>();

        private static void R2Hide(Map map, params Pawn[] keep)
        {
            foreach (Pawn p in Colonists(map))
            {
                if (!keep.Contains(p))
                {
                    p.DeSpawn();
                    r2Hidden.Add(p);
                }
            }
        }

        private static void R2Unhide(Map map)
        {
            int i = 0;
            foreach (Pawn p in r2Hidden)
            {
                if (!p.Spawned && !p.Destroyed)
                {
                    GenSpawn.Spawn(p, R2ParkCell(map, i++), map);
                    Draft(p, true);
                }
            }
            r2Hidden.Clear();
        }

        private static void R2Feed(Map map)
        {
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.needs?.food != null) p.needs.food.CurLevel = p.needs.food.MaxLevel;
                if (p.needs?.joy != null) p.needs.joy.CurLevel = 1f;
                if (p.needs?.rest != null) p.needs.rest.CurLevel = 1f;
                if (p.InMentalState) p.MentalState.RecoverFromState();
            }
        }

        // Wipe the area (and a margin) on every level: things gone, plain soil on the ground, open air above, no roofs,
        // no fog. Fixture pawns are moved out first.
        private static void R2Clear(Map map)
        {
            // (a fixture pawn lost earlier, e.g. hurt in a fight check: another colonist takes its place)
            if (r2P == null || r2P.Dead || !r2P.Spawned || r2P.Downed)
            {
                Pawn was = r2P;
                r2P = Colonists(map).FirstOrDefault(p => p != r2Q && !p.Downed) ?? map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.Faction == Faction.OfPlayer && p.RaceProps.Humanlike && p != r2Q && !p.Downed);
                Info($"router2: walker {was} (dead {was?.Dead}, spawned {was?.Spawned}, downed {was?.Downed}, at {was?.PositionHeld}, held by {was?.ParentHolder}) is now {r2P}; colonists {string.Join(", ", Colonists(map))}");
            }
            if (r2Q == null || r2Q.Dead || !r2Q.Spawned || r2Q.Downed)
            {
                r2Q = Colonists(map).FirstOrDefault(p => p != r2P && !p.Downed);
            }
            // (nothing from outside the fixtures: wild animals that wandered in, a threat a quest brought)
            foreach (Pawn o in map.mapPawns.AllPawnsSpawned.Where(p => p.Faction != Faction.OfPlayer).ToList())
            {
                o.Destroy();
            }
            map.roofCollapseBuffer.Clear();
            CellRect big = r2Area.ExpandedBy(3).ClipInsideMap(map);
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
            {
                IntVec3 g = Levels.Ground(p.Position, map);
                if (g.IsValid && big.Contains(g))
                {
                    if (p.IsColonist)
                    {
                        Teleport(p, R2ParkCell(map, 0));
                    }
                    else
                    {
                        p.Destroy();
                    }
                }
            }
            foreach (Thing f in map.listerThings.ThingsInGroup(ThingRequestGroup.Fire).ToList())
            {
                f.Destroy();
            }
            for (int L = Levels.Max(map); L >= Levels.Min(map); L--)
            {
                foreach (IntVec3 g in big)
                {
                    IntVec3 c = Levels.Cell(g, L, map);
                    if (!c.IsValid || !c.InBounds(map))
                    {
                        continue;
                    }
                    foreach (Thing t in c.GetThingList(map).ToList())
                    {
                        if (!(t is Pawn) && !t.Destroyed)
                        {
                            t.Destroy();
                        }
                    }
                }
            }
            for (int L = Levels.Max(map); L >= Levels.Min(map); L--)
            {
                foreach (IntVec3 g in big)
                {
                    IntVec3 c = Levels.Cell(g, L, map);
                    if (!c.IsValid || !c.InBounds(map))
                    {
                        continue;
                    }
                    map.terrainGrid.SetTerrain(c, L == 0 ? TerrainDefOf.Soil : PZDefOf.PZ_OpenAir);
                    map.roofGrid.SetRoof(c, null);
                    if (map.fogGrid.IsFogged(c))
                    {
                        map.fogGrid.Unfog(c);
                    }
                }
            }
            map.roofCollapseBuffer.Clear();
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
        }

        // An upper floor over ground cells rect on level L (the roof under it laid first).
        private static void R2Floor(Map map, CellRect rect, int L)
        {
            foreach (IntVec3 g in rect)
            {
                if (!g.InBounds(map))
                {
                    continue;
                }
                IntVec3 below = Levels.Cell(g, L - 1, map);
                IntVec3 c = Levels.Cell(g, L, map);
                if (!c.IsValid || Stairwell.StairThingAt(below, map))
                {
                    continue;
                }
                map.roofGrid.SetRoof(below, RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(c, PZDefOf.PZ_UpperFloor);
            }
        }

        private static Thing R2Wall(Map map, IntVec3 c)
        {
            if (!c.InBounds(map) || c.GetEdifice(map) != null || !c.Standable(map))
            {
                return null;
            }
            return Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
        }

        private static Building_Stairs R2Stairs(Map map, IntVec3 center, Rot4 rot)
        {
            if (!GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, center, rot, map, stuffDef: ThingDefOf.WoodLog).Accepted)
            {
                return null;
            }
            return Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, center, map, rot) as Building_Stairs;
        }

        private static void R2Work(Pawn p, params WorkTypeDef[] types)
        {
            p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
            foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefs)
            {
                if (!p.WorkTypeIsDisabled(w))
                {
                    p.workSettings.SetPriority(w, types.Contains(w) ? 1 : 0);
                }
            }
        }

        private static void R2Mark()
        {
            r2ErrAt = errors;
            r2FindsAt = PZStats.finds;
            r2ClimbsAt = climbsColonists;
            r2HostClimbsAt = climbsHostiles;
            r2SameReqAt = PZStats.sameFloorRequests;
            r2SameRoutedAt = PZStats.sameFloorRouted;
            r2LegsAt = PZStats.routerLegsChecked;
            PZStats.findMsMax = 0;
        }

        private static string R2Stats() => $"router finds {PZStats.finds - r2FindsAt} (slowest {PZStats.findMsMax:0.00} ms), legs {PZStats.routerLegsChecked - r2LegsAt}, colonist steps {climbsColonists - r2ClimbsAt}, hostile steps {climbsHostiles - r2HostClimbsAt}, errors {errors - r2ErrAt}";

        // The cost of the path the game gets for this pawn (null path: MaxValue), and the path's stair steps (H cells).
        private static float R2Path(Map map, Pawn p, IntVec3 from, LocalTargetInfo to, PathEndMode mode, out List<IntVec3> steps, out bool valid)
        {
            steps = new List<IntVec3>();
            valid = true;
            PawnPath path = map.pathFinder.FindPathNow(from, to, TraverseParms.For(p), PathFinderCostTuning.For(p), mode);
            if (path == null || !path.Found)
            {
                path?.Dispose();
                return float.MaxValue;
            }
            List<IntVec3> n = path.NodesReversed;
            StairGrid g = StairGrid.For(map);
            for (int i = n.Count - 1; i > 0; i--)
            {
                IntVec3 a = n[i], b = n[i - 1];
                if (Math.Abs(a.x - b.x) <= 1 && Math.Abs(a.z - b.z) <= 1)
                {
                    continue;
                }
                if (g != null && g.IsStep(a, b))
                {
                    steps.Add(Levels.LevelOf(a, map) < Levels.LevelOf(b, map) ? a : b);
                }
                else
                {
                    valid = false;
                }
            }
            float cost = path.TotalCost;
            path.Dispose();
            return cost;
        }

        // ------------------------------------------------------------------------------------------------ O: oracle

        private static readonly List<Building_Stairs> r2Stairs = new List<Building_Stairs>();

        private static void R2Layout(Map map, int seed)
        {
            R2Clear(map);
            r2Stairs.Clear();
            Rand.PushState(seed);
            try
            {
                int w = r2Area.Width, h = r2Area.Height;
                List<CellRect> l1 = new List<CellRect>();
                int n1 = Rand.RangeInclusive(2, 3);
                for (int i = 0; i < n1; i++)
                {
                    int rw = Rand.RangeInclusive(7, 16), rh = Rand.RangeInclusive(6, 12);
                    CellRect r = new CellRect(r2Area.minX + Rand.RangeInclusive(1, w - rw - 1), r2Area.minZ + Rand.RangeInclusive(1, h - rh - 1), rw, rh);
                    l1.Add(r);
                    R2Floor(map, r, 1);
                }
                int n2 = Rand.RangeInclusive(1, 2);
                for (int i = 0; i < n2; i++)
                {
                    CellRect b = l1[Rand.Range(0, l1.Count)];
                    int rw = Rand.RangeInclusive(4, Math.Max(4, b.Width - 1)), rh = Rand.RangeInclusive(4, Math.Max(4, b.Height - 1));
                    CellRect r = new CellRect(b.minX + Rand.RangeInclusive(0, Math.Max(0, b.Width - rw)), b.minZ + Rand.RangeInclusive(0, Math.Max(0, b.Height - rh)), Math.Min(rw, b.Width), Math.Min(rh, b.Height));
                    R2Floor(map, r, 2);
                }
                // walls with a door now and then, on every level
                for (int L = 0; L <= 2; L++)
                {
                    int nw = Rand.RangeInclusive(2, 4);
                    for (int i = 0; i < nw; i++)
                    {
                        bool horiz = Rand.Bool;
                        int len = Rand.RangeInclusive(5, 16);
                        IntVec3 s = RG(Rand.Range(1, w - 1), Rand.Range(1, h - 1));
                        int doorAt = Rand.Chance(0.5f) ? Rand.Range(1, len - 1) : -1;
                        for (int k = 0; k < len; k++)
                        {
                            IntVec3 g = horiz ? s + new IntVec3(k, 0, 0) : s + new IntVec3(0, 0, k);
                            if (!r2Area.Contains(g))
                            {
                                break;
                            }
                            IntVec3 c = Levels.Cell(g, L, map);
                            if (!c.IsValid || L > 0 && !ProjectZ.HasFloor(c, map))
                            {
                                continue;
                            }
                            if (k == doorAt)
                            {
                                if (c.GetEdifice(map) == null && c.Standable(map))
                                {
                                    Place(ThingDefOf.Door, ThingDefOf.WoodLog, c, map);
                                }
                            }
                            else
                            {
                                R2Wall(map, c);
                            }
                        }
                    }
                }
                // stairs: up to 6 accepted placements, from the ground and from level 1
                for (int tries = 0; tries < 80 && r2Stairs.Count < 6; tries++)
                {
                    int L = Rand.Chance(0.6f) ? 0 : 1;
                    Rot4 rot = new Rot4(Rand.Range(0, 4));
                    IntVec3 c = Levels.Cell(RG(Rand.Range(3, w - 3), Rand.Range(3, h - 3)), L, map);
                    if (L == 1 && !ProjectZ.HasFloor(c, map))
                    {
                        continue;
                    }
                    Building_Stairs st = R2Stairs(map, c, rot);
                    if (st != null)
                    {
                        r2Stairs.Add(st);
                    }
                }
            }
            finally
            {
                Rand.PopState();
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
        }

        private static List<IntVec3> R2Cells(Map map, int L)
        {
            List<IntVec3> list = new List<IntVec3>();
            foreach (IntVec3 g in r2Area.ContractedBy(1))
            {
                IntVec3 c = Levels.Cell(g, L, map);
                if (c.IsValid && c.Standable(map) && c.GetEdifice(map) == null && !(L > 0 && !ProjectZ.HasFloor(c, map)))
                {
                    list.Add(c);
                }
            }
            return list;
        }

        private static void R2OracleNext(Map map)
        {
            tickWatch = null;
            if (r2SeedsLeft <= 0)
            {
                stage = "r2_o_done";
                return;
            }
            r2SeedsLeft--;
            int seed = (SeedArg ?? 7) * 1000 + r2Seed++;
            R2Layout(map, seed);
            R2Feed(map);
            Draft(r2P, true);
            Rand.PushState(seed + 1);
            List<(IntVec3 s, IntVec3 d)> pairs = new List<(IntVec3, IntVec3)>();
            try
            {
                List<IntVec3>[] cells = { R2Cells(map, 0), R2Cells(map, 1), R2Cells(map, 2) };
                for (int i = 0; i < 8; i++)
                {
                    int ls = Rand.Range(0, 3);
                    int ld = i < 5 ? (ls + Rand.RangeInclusive(1, 2)) % 3 : ls;
                    if (cells[ls].Count == 0 || cells[ld].Count == 0)
                    {
                        continue;
                    }
                    pairs.Add((cells[ls].RandomElement(), cells[ld].RandomElement()));
                }
            }
            finally
            {
                Rand.PopState();
            }
            IntVec3 walkFrom = IntVec3.Invalid, walkTo = IntVec3.Invalid;
            foreach ((IntVec3 s, IntVec3 d) in pairs)
            {
                Teleport(r2P, s);
                TraverseParms tp = TraverseParms.For(r2P);
                PathFinderCostTuning tune = PathFinderCostTuning.For(r2P);
                float got = R2Path(map, r2P, s, d, PathEndMode.OnCell, out List<IntVec3> steps, out bool valid);
                float best = StairRouter.OracleCost(map, s, d, PathEndMode.OnCell, tp, tune, out string bestName);
                bool same = Levels.LevelOf(s, map) == Levels.LevelOf(d, map);
                bool ok;
                if (best >= float.MaxValue)
                {
                    ok = got >= float.MaxValue;
                }
                else if (got >= float.MaxValue)
                {
                    ok = false;
                }
                else if (same)
                {
                    ok = got <= best * StairRouter.DetourFactor + 0.5f && valid;
                }
                else
                {
                    ok = got <= best + 0.5f && valid;
                    if (got < best - 0.5f)
                    {
                        r2RouterCheaper++;
                    }
                }
                r2Pairs++;
                if (ok)
                {
                    r2PairsOk++;
                    if (!same && got < float.MaxValue && !walkFrom.IsValid)
                    {
                        walkFrom = s;
                        walkTo = d;
                    }
                }
                else
                {
                    r2Mismatch++;
                    if (r2Bad.Count < 12)
                    {
                        r2Bad.Add($"seed {seed}: {s} L{Levels.LevelOf(s, map)} -> {d} L{Levels.LevelOf(d, map)}: got {(got >= float.MaxValue ? "none" : got.ToString("0"))} (steps {string.Join(",", steps)}, valid {valid}), oracle {(best >= float.MaxValue ? "none" : best.ToString("0"))} ({bestName}), router iterations {StairRouter.LastIterations} legs {StairRouter.LastLegs} capped {StairRouter.LastFinal}, last leg failure {StairRouter.LastLegFail ?? "-"}, stairs {string.Join(",", r2Stairs.Select(x => x.Position + "" + x.Rotation.ToStringHuman()[0]))}");
                    }
                }
            }
            if (!walkFrom.IsValid)
            {
                stage = "r2_o_next";
                return;
            }
            // a real walk over the route
            Teleport(r2P, walkFrom);
            r2Dest = walkTo;
            r2Walks++;
            R2Mark();
            GoTo(r2P, walkTo);
            TickUntil(4000, () => r2P.Position == r2Dest, "r2_o_walked", 15);
        }

        private static void R2OracleWalked(Map map)
        {
            tickWatch = null;
            if (r2P.Position == r2Dest)
            {
                r2WalksOk++;
            }
            else if (r2Bad.Count < 12)
            {
                r2Bad.Add($"walk to {r2Dest} ended at {r2P.Position} ({R2Stats()})");
            }
            stage = "r2_o_next";
        }

        // ------------------------------------------------------------------------------------------------ B: bridge

        // A wall across the area splits it (a gap at the west end); stairs up on the south side, a level-1 bridge over
        // the wall, stairs down on the north side. far = the gap far away (a long detour: above the threshold) or near
        // (below it).
        private static void R2Bridge(Map map, bool far)
        {
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            int wallZ = 16;
            int gapX = far ? 1 : 26;
            for (int x = 0; x < 44; x++)
            {
                if (x != gapX && x != gapX + 1)
                {
                    R2Wall(map, RG(x, wallZ));
                }
            }
            // for the far variant the wall runs on west beyond the area too, so the detour is long
            R2Floor(map, new CellRect(RG(30, 10).x, RG(30, 10).z, 3, 13), 1);
            Building_Stairs up = R2Stairs(map, RG(31, 8), Rot4.North);     // exit (31, 11) on the bridge
            Building_Stairs down = R2Stairs(map, RG(31, 25), Rot4.South);  // exit (31, 22) on the bridge
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            IntVec3 s = RG(31, 5), d = RG(31, 28);
            Teleport(r2P, s);
            Draft(r2P, true);
            TraverseParms tp = TraverseParms.For(r2P);
            StairRouter.busy = true;
            PawnPath v = map.pathFinder.FindPathNow(s, d, tp, PathFinderCostTuning.For(r2P), PathEndMode.OnCell);
            StairRouter.busy = false;
            float vc = v != null && v.Found ? v.TotalCost : float.MaxValue;
            v?.Dispose();
            float lb = StairRouter.StairLowerBound(map, s, d, PathEndMode.OnCell, tp);
            float got = R2Path(map, r2P, s, d, PathEndMode.OnCell, out List<IntVec3> steps, out bool valid);
            float best = StairRouter.OracleCost(map, s, d, PathEndMode.OnCell, tp, PathFinderCostTuning.For(r2P), out string bestName);
            bool above = vc > StairRouter.DetourFactor * lb;
            string tag = far ? "B above the threshold (a long detour round the wall)" : "B below the threshold (a short detour)";
            Result($"{tag}: fixture valid (stairs placed; the game's path {(far ? ">" : "<=")} 1.2 x the stairs' lower bound)", up != null && down != null && above == far,
                $"stairs {up?.Position} {down?.Position}; game path {vc:0}, lower bound {lb:0}, ratio {vc / lb:0.00}");
            if (far)
            {
                Result($"{tag}: the route goes over the bridge (two stair steps) and costs the cheapest route", steps.Count == 2 && valid && got <= best + 0.5f,
                    $"got {got:0} (steps {steps.Count}), oracle {best:0} ({bestName}), game path {vc:0}");
            }
            else
            {
                Result($"{tag}: the game's own path stays (no stairs), within 20 % of the cheapest", steps.Count == 0 && got <= best * StairRouter.DetourFactor + 0.5f,
                    $"got {got:0} (steps {steps.Count}), oracle {best:0} ({bestName}), game path {vc:0}, lower bound {lb:0}");
            }
            r2Dest = d;
            R2Mark();
            GoTo(r2P, d);
            TickUntil(3000, () => r2P.Position == r2Dest, far ? "r2_b2_check" : "r2_b_check", 15);
        }

        private static void R2BridgeCheck(Map map, bool far)
        {
            tickWatch = null;
            int steps = climbsColonists - r2ClimbsAt;
            int routed = PZStats.sameFloorRouted - r2SameRoutedAt;
            if (far)
            {
                Result("B above the threshold, walked: the pawn takes the bridge (2 stair steps) and arrives", r2P.Position == r2Dest && steps == 2 && routed >= 1, $"at {r2P.Position}, steps {steps}, same-floor checks over the stairs {routed}; {R2Stats()}");
                stage = "r2_t";
            }
            else
            {
                Result("B below the threshold, walked: the pawn walks round on the ground (no stairs, no stairs route worked out)", r2P.Position == r2Dest && steps == 0 && routed == 0, $"at {r2P.Position}, steps {steps}, same-floor checks over the stairs {routed}; {R2Stats()}");
                stage = "r2_b2";
            }
        }

        // ------------------------------------------------------------------------------------------------ T: turn-around

        // Two stairs about equally good; while the pawn walks, a new path is asked for every 30 ticks (as the game does for
        // blocked paths and moved targets). The stairs the route uses may only change in the first steps.
        private static void R2Turn(Map map)
        {
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            R2Floor(map, new CellRect(RG(2, 20).x, RG(2, 20).z, 40, 8), 1);
            r2SA = R2Stairs(map, RG(12, 17), Rot4.North);
            r2SB = R2Stairs(map, RG(30, 17), Rot4.North);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            IntVec3 s = RG(21, 2), d = RL(21, 25, 1, map);
            Teleport(r2P, s);
            Draft(r2P, true);
            r2Dest = d;
            r2Walked = 0;
            r2Switches = 0;
            r2LastPos = s;
            r2LastPath = null;
            r2FirstH = IntVec3.Invalid;
            r2Seen.Clear();
            R2Mark();
            GoTo(r2P, d);
            System.Reflection.MethodInfo again = AccessTools.Method(typeof(Pawn_PathFollower), "TrySetNewPathRequest");
            int tick = 0;
            TickUntil(3000, () =>
            {
                tick++;
                if (r2P.Position != r2LastPos)
                {
                    r2Walked++;
                    r2LastPos = r2P.Position;
                }
                PawnPath cur = r2P.pather?.curPath;
                if (cur != null && !ReferenceEquals(cur, r2LastPath))
                {
                    r2LastPath = cur;
                    IntVec3 h = IntVec3.Invalid;
                    List<IntVec3> n = cur.NodesReversed;
                    for (int i = n.Count - 1; i > 0; i--)
                    {
                        if (Math.Abs(n[i].x - n[i - 1].x) > 1 || Math.Abs(n[i].z - n[i - 1].z) > 1)
                        {
                            h = n[i];
                            break;
                        }
                    }
                    if (h.IsValid)
                    {
                        if (r2FirstH.IsValid && h != r2FirstH && r2Walked > 3)
                        {
                            r2Switches++;
                        }
                        if (r2Seen.Count < 20)
                        {
                            r2Seen.Add($"{r2Walked}:{h}");
                        }
                        r2FirstH = h;
                    }
                }
                if (tick % 2 == 0 && r2P.pather.Moving && Levels.LevelOf(r2P.Position, map) == 0)
                {
                    again.Invoke(r2P.pather, null);   // ask again (the follower keeps walking its path meanwhile)
                }
                return r2P.Position == r2Dest;
            }, "r2_t_check", 15);
        }

        private static void R2TurnCheck(Map map)
        {
            tickWatch = null;
            Result("T turn-around only at departure: with paths asked again all the way, the stairs chosen never change after the first 3 steps; the pawn arrives",
                r2P.Position == r2Dest && r2Switches == 0 && r2Seen.Count >= 3, $"at {r2P.Position}, switches after 3 steps {r2Switches}, paths seen (steps walked: stairs H) {string.Join(" ", r2Seen)}; {R2Stats()}");
            stage = "r2_w";
        }

        // ------------------------------------------------------------------------------------------------ W: busy colony

        private static void R2Busy(Map map)
        {
            R2Clear(map);
            R2Feed(map);
            // a two-storey house with two stairs, items to haul both ways, filth to clean, walls to build upstairs
            CellRect house = new CellRect(RG(6, 6).x, RG(6, 6).z, 30, 20);
            foreach (IntVec3 c in house.EdgeCells)
            {
                if (c == RG(20, 6) || c == RG(6, 15))
                {
                    Place(ThingDefOf.Door, ThingDefOf.WoodLog, c, map);
                }
                else
                {
                    R2Wall(map, c);
                }
            }
            R2Floor(map, house.ContractedBy(1), 1);
            R2Stairs(map, RG(10, 9), Rot4.North);
            R2Stairs(map, RG(30, 9), Rot4.North);
            foreach (IntVec3 g in house)
            {
                map.areaManager.Home[g] = true;
                map.areaManager.Home[Levels.Cell(g, 1, map)] = true;
            }
            Zone_Stockpile zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (IntVec3 g in new CellRect(RG(26, 18).x, RG(26, 18).z, 6, 4))
            {
                zone.AddCell(Levels.Cell(g, 1, map));
            }
            Rand.PushState((SeedArg ?? 7) + 501);
            try
            {
                for (int i = 0; i < 14; i++)
                {
                    Thing w = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                    w.stackCount = 20;
                    GenSpawn.Spawn(w, RG(Rand.Range(8, 34), Rand.Range(8, 13)), map);
                }
                for (int i = 0; i < 20; i++)
                {
                    int L = Rand.Range(0, 2);
                    IntVec3 c = Levels.Cell(RG(Rand.Range(8, 34), Rand.Range(14, 24)), L, map);
                    if (c.Standable(map))
                    {
                        GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Filth_Dirt), c, map);
                    }
                }
                for (int x = 12; x < 20; x++)
                {
                    IntVec3 c = RL(x, 22, 1, map);
                    if (GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, c, Rot4.North, map, stuffDef: ThingDefOf.WoodLog).Accepted)
                    {
                        GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, c, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
                    }
                }
            }
            finally
            {
                Rand.PopState();
            }
            int i2 = 0;
            foreach (Pawn p in Colonists(map))
            {
                Draft(p, false);
                Teleport(p, RG(10 + 4 * i2++, 16));
                R2Work(p, WorkTypeDefOf.Hauling, WorkTypeDefOf.Cleaning, WorkTypeDefOf.Construction);
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            R2Mark();
            r2Walked = PZStats.sameFloorWins;
            StartTicks(12000, "r2_w_check");
        }

        private static void R2BusyCheck(Map map)
        {
            int req = PZStats.sameFloorRequests - r2SameReqAt;
            int routed = PZStats.sameFloorRouted - r2SameRoutedAt;
            int wins = PZStats.sameFloorWins - r2Walked;
            int wasted = routed - wins;
            double share = req > 0 ? (double)wasted / req : 0;
            Result("W wasted work: in a busy two-floor colony at most 5 % of the same-floor path requests worked out a stairs route that was then not taken (option C)", req >= 50 && share <= 0.05,
                $"same-floor requests {req}, checked over the stairs {routed} (stairs taken {wins}, wasted {wasted} = {share:P1}); {R2Stats()}");
            foreach (Pawn p in Colonists(map))
            {
                R2Work(p);
            }
            R2Park(map, r2P);
            stage = "r2_u";
        }

        // ------------------------------------------------------------------------------------------------ U: unreachable

        private static void R2Unreach(Map map)
        {
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            R2Floor(map, new CellRect(RG(10, 10).x, RG(10, 10).z, 16, 10), 1);
            R2Stairs(map, RG(12, 7), Rot4.North);
            // a cell on the floor walled in all round
            IntVec3 t = RL(20, 15, 1, map);
            foreach (IntVec3 d in GenAdj.AdjacentCells)
            {
                R2Wall(map, t + d);
            }
            map.areaManager.Home[t] = true;
            r2Thing = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Filth_Dirt), t, map);
            foreach (Thing f in map.listerThings.ThingsInGroup(ThingRequestGroup.Filth).Where(f => f != r2Thing).ToList())
            {
                f.Destroy();
            }
            Teleport(r2P, RG(14, 3));
            Draft(r2P, false);
            R2Work(r2P, WorkTypeDefOf.Cleaning);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            r2Flag = false;
            R2Mark();
            TickUntil(1500, () => { r2Flag |= r2P.CurJob != null && r2P.CurJob.targetA.Thing == r2Thing; return false; }, "r2_u_check", 15);
        }

        private static void R2UnreachCheck(Map map)
        {
            tickWatch = null;
            bool reach = r2P.CanReach(r2Thing, PathEndMode.Touch, Danger.Deadly);
            Stopwatch sw = Stopwatch.StartNew();
            float cost = R2Path(map, r2P, r2P.Position, r2Thing, PathEndMode.Touch, out _, out _);
            double ms = sw.Elapsed.TotalMilliseconds;
            Result("U an unreachable target upstairs: not reachable, never given as a job, no path (answered at once), no search loop",
                !reach && !r2Flag && cost >= float.MaxValue && ms < 5 && PZStats.finds - r2FindsAt <= 2 && errors == r2ErrAt,
                $"reach {reach}, job seen {r2Flag}, path {(cost >= float.MaxValue ? "none" : cost.ToString())} in {ms:0.00} ms; {R2Stats()}");
            // a drafted order there: refused or failed once, no loop
            R2Work(r2P);
            R2Mark();
            GoTo(r2P, r2Thing.Position);
            StartTicks(1000, "r2_u_order_check");
        }

        private static void R2UnreachOrderCheck(Map map)
        {
            Result("U a drafted order to the unreachable cell: the pawn stays, a path is asked at most a few times (no job loop), no error",
                Levels.LevelOf(r2P.Position, map) == 0 && PZStats.finds - r2FindsAt <= 3 && errors == r2ErrAt, $"at {r2P.Position} job {r2P.CurJobDef?.defName}; {R2Stats()}");
            r2Thing.Destroy();
            stage = "r2_d";
        }

        // ------------------------------------------------------------------------------------------------ D: dead ends

        private static void R2DeadEnds(Map map)
        {
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            // the floor in the east; three stairs in the west lead onto one-cell islands, the good one is far east
            R2Floor(map, new CellRect(RG(28, 4).x, RG(28, 4).z, 12, 22), 1);
            r2Stairs.Clear();
            foreach (int z in new[] { 6, 12, 18 })
            {
                Building_Stairs st = R2Stairs(map, RG(8, z), Rot4.West);   // exit (5, z) on level 1: a landing island
                if (st != null)
                {
                    r2Stairs.Add(st);
                }
            }
            r2SA = R2Stairs(map, RG(34, 1), Rot4.North);   // exit (34, 4) on the floor
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            IntVec3 s = RG(12, 12), d = RL(30, 20, 1, map);
            Teleport(r2P, s);
            Draft(r2P, true);
            TraverseParms tp = TraverseParms.For(r2P);
            R2Path(map, r2P, s, d, PathEndMode.OnCell, out _, out _);   // (the first call after a fixture pays the game's own grid rebuild)
            Stopwatch sw = Stopwatch.StartNew();
            float got = R2Path(map, r2P, s, d, PathEndMode.OnCell, out List<IntVec3> steps, out bool valid);
            double ms = sw.Elapsed.TotalMilliseconds;
            float best = StairRouter.OracleCost(map, s, d, PathEndMode.OnCell, tp, PathFinderCostTuning.For(r2P), out string bestName);
            Result("D several dead-end staircases (3 nearer, onto islands): the route takes the good stairs, costs the cheapest, quickly",
                r2Stairs.Count == 3 && r2SA != null && steps.Count == 1 && steps[0] == r2SA.HighCell && got <= best + 0.5f && ms < 20,
                $"decoys {r2Stairs.Count}, good {r2SA?.Position}; got {got:0} over {string.Join(",", steps)} in {ms:0.00} ms, oracle {best:0} ({bestName})");
            r2Dest = d;
            R2Mark();
            GoTo(r2P, d);
            TickUntil(3000, () => r2P.Position == r2Dest, "r2_d_check", 15);
        }

        private static void R2DeadEndsCheck(Map map)
        {
            tickWatch = null;
            Result("D walked: the pawn arrives over the good stairs (one step up), no dead-end stairs tried", r2P.Position == r2Dest && climbsColonists - r2ClimbsAt == 1, $"at {r2P.Position}; {R2Stats()}");
            stage = "r2_dr";
        }

        // ------------------------------------------------------------------------------------------------ DR: door

        // Upstairs two rooms, a door between; stairs A into room 1 (near), stairs B into room 2 (far). The target in room 2:
        // over A and through the door; the door forbidden: over B; B gone too: not reachable; the door allowed: over A.
        private static void R2Door(Map map)
        {
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            CellRect up = new CellRect(RG(6, 10).x, RG(6, 10).z, 32, 10);
            R2Floor(map, up, 1);
            for (int z = 10; z < 20; z++)
            {
                IntVec3 c = RL(22, z, 1, map);
                if (z == 15)
                {
                    r2Thing = Place(ThingDefOf.Door, ThingDefOf.WoodLog, c, map);
                }
                else
                {
                    R2Wall(map, c);
                }
            }
            r2SA = R2Stairs(map, RG(18, 7), Rot4.North);   // exit (18, 10): room 1
            r2SB = R2Stairs(map, RG(40, 14), Rot4.West);   // exit (37, 14): room 2
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            IntVec3 s = RG(18, 2), d = RL(28, 15, 1, map);
            Teleport(r2P, s);
            Draft(r2P, true);
            R2Path(map, r2P, s, d, PathEndMode.OnCell, out List<IntVec3> s1, out _);
            ((Building_Door)r2Thing).SetForbidden(true, false);
            bool reach2 = r2P.CanReach(d, PathEndMode.OnCell, Danger.Deadly);
            R2Path(map, r2P, s, d, PathEndMode.OnCell, out List<IntVec3> s2, out _);
            Result("DR a door upstairs: open to the pawn, the route goes over the near stairs and through it; forbidden, over the far stairs",
                r2SA != null && r2SB != null && s1.Count == 1 && s1[0] == r2SA.HighCell && reach2 && s2.Count == 1 && s2[0] == r2SB.HighCell,
                $"door open: steps {string.Join(",", s1)}; forbidden: reach {reach2}, steps {string.Join(",", s2)}; A {r2SA?.HighCell}, B {r2SB?.HighCell}");
            r2Dest = d;
            R2Mark();
            GoTo(r2P, d);
            TickUntil(3000, () => r2P.Position == r2Dest, "r2_dr_check", 15);
        }

        private static void R2DoorCheck(Map map)
        {
            tickWatch = null;
            bool arrived = r2P.Position == r2Dest;
            Teleport(r2P, RG(18, 2));
            r2SB.Destroy();
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            bool reach3 = r2P.CanReach(r2Dest, PathEndMode.OnCell, Danger.Deadly);
            ((Building_Door)r2Thing).SetForbidden(false, false);
            bool reach4 = r2P.CanReach(r2Dest, PathEndMode.OnCell, Danger.Deadly);
            Result("DR walked over the far stairs with the door forbidden; with those stairs gone too not reachable; the door allowed again reachable",
                arrived && !reach3 && reach4, $"arrived {arrived}; far stairs gone: reach {reach3}; door allowed: reach {reach4}; {R2Stats()}");
            stage = "r2_pb";
        }

        // ------------------------------------------------------------------------------------------------ PB: blocker

        private static void R2Blocker(Map map)
        {
            R2Clear(map);
            R2Park(map, r2P, r2Q);
            R2Feed(map);
            R2Floor(map, new CellRect(RG(10, 10).x, RG(10, 10).z, 16, 10), 1);
            r2SA = R2Stairs(map, RG(14, 7), Rot4.North);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Info($"PB: walker {r2P} spawned {r2P?.Spawned}, second {r2Q} spawned {r2Q?.Spawned} downed {r2Q?.Downed}, stairs {r2SA?.Position}; colonists {string.Join(", ", Colonists(map).Select(p => p + (p.Downed ? " (downed)" : "")))}");
            if (r2Q == null || r2SA == null)
            {
                Result("PB fixture: a second colonist and the stairs", false, $"second {r2Q}, stairs {r2SA}");
                stage = "r2_f";
                return;
            }
            // the second colonist stands drafted on the exit
            Teleport(r2Q, r2SA.TopCell);
            Draft(r2Q, true);
            Teleport(r2P, RG(14, 2));
            r2Dest = RL(20, 16, 1, map);
            R2Mark();
            GoTo(r2P, r2Dest);
            TickUntil(3000, () => r2P.Position == r2Dest, "r2_pb_check", 15);
        }

        private static void R2BlockerCheck(Map map)
        {
            tickWatch = null;
            Result("PB a pawn standing on the stairs' exit: the walker still gets up and arrives, no error", r2P.Position == r2Dest && errors == r2ErrAt, $"at {r2P.Position}, blocker at {r2Q.Position}; {R2Stats()}");
            stage = "r2_f";
        }

        // ------------------------------------------------------------------------------------------------ F: fire

        private static void R2Fire(Map map)
        {
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            R2Floor(map, new CellRect(RG(6, 12).x, RG(6, 12).z, 32, 8), 1);
            r2SA = R2Stairs(map, RG(20, 9), Rot4.North);   // near
            r2SB = R2Stairs(map, RG(30, 9), Rot4.North);   // 10 cells farther
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            IntVec3 s = RG(20, 3), d = RL(20, 16, 1, map);
            Teleport(r2P, s);
            Draft(r2P, true);
            R2Path(map, r2P, s, d, PathEndMode.OnCell, out List<IntVec3> s0, out _);
            bool fire = FireUtility.TryStartFireIn(r2SA.Position, map, 1f, null);
            float got = R2Path(map, r2P, s, d, PathEndMode.OnCell, out List<IntVec3> s1, out _);
            float best = StairRouter.OracleCost(map, s, d, PathEndMode.OnCell, TraverseParms.For(r2P), PathFinderCostTuning.For(r2P), out string bestName);
            foreach (Thing f in map.listerThings.ThingsInGroup(ThingRequestGroup.Fire).ToList())
            {
                f.Destroy();
            }
            R2Path(map, r2P, s, d, PathEndMode.OnCell, out List<IntVec3> s2, out _);
            Result("F burning stairs: without fire over the near stairs; a fire on its ramp makes the far stairs cheaper (fire costs as on any cell); put out, the near stairs again",
                r2SA != null && r2SB != null && fire && s0.Count == 1 && s0[0] == r2SA.HighCell && s1.Count == 1 && s1[0] == r2SB.HighCell && got <= best + 0.5f && s2.Count == 1 && s2[0] == r2SA.HighCell,
                $"fire started {fire}; before {string.Join(",", s0)}, burning {string.Join(",", s1)} (cost {got:0}, oracle {best:0} {bestName}), after {string.Join(",", s2)}; near {r2SA?.HighCell}, far {r2SB?.HighCell}");
            stage = "r2_fg";
        }

        // ------------------------------------------------------------------------------------------------ FG: fog

        private static void R2Fog(Map map)
        {
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            R2Floor(map, new CellRect(RG(10, 10).x, RG(10, 10).z, 16, 10), 1);
            r2SA = R2Stairs(map, RG(14, 7), Rot4.North);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            foreach (IntVec3 g in new CellRect(RG(10, 10).x, RG(10, 10).z, 16, 10))
            {
                map.fogGrid.Refog(CellRect.SingleCell(Levels.Cell(g, 1, map)));
            }
            IntVec3 s = RG(14, 2), d = RL(20, 16, 1, map);
            Teleport(r2P, s);
            Draft(r2P, false);
            bool reach = r2P.CanReach(d, PathEndMode.OnCell, Danger.Deadly);
            float undrafted = R2Path(map, r2P, s, d, PathEndMode.OnCell, out List<IntVec3> s1, out _);
            Draft(r2P, true);
            float drafted = R2Path(map, r2P, s, d, PathEndMode.OnCell, out List<IntVec3> s2, out _);
            Result("FG stairs into fog: reachable (fog no obstacle); undrafted the path costs fog like the game's avoidFog, drafted not",
                reach && undrafted < float.MaxValue && drafted < float.MaxValue && undrafted > drafted + 500 && s1.Count == 1 && s2.Count == 1,
                $"exit fogged {r2SA?.TopCell.Fogged(map)}; reach {reach}; cost undrafted {undrafted:0}, drafted {drafted:0}");
            foreach (IntVec3 g in new CellRect(RG(10, 10).x, RG(10, 10).z, 16, 10))
            {
                IntVec3 c = Levels.Cell(g, 1, map);
                if (map.fogGrid.IsFogged(c))
                {
                    map.fogGrid.Unfog(c);
                }
            }
            stage = "r2_m";
        }

        // ------------------------------------------------------------------------------------------------ M: melee

        private static void R2Melee(Map map)
        {
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            R2Floor(map, new CellRect(RG(10, 10).x, RG(10, 10).z, 16, 10), 1);
            r2SA = R2Stairs(map, RG(14, 7), Rot4.North);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Pawn r = MakeRaider(map, r2SA.TopCell);
            r.equipment.DestroyAllEquipment();
            r.stances.stunner.StunFor(3000, null, addBattleLog: false);
            r2Thing = r;
            Teleport(r2P, r2SA.HighCell);
            Draft(r2P, true);
            bool fromH = ReachabilityImmediate.CanReachImmediate(r2SA.HighCell, r, map, PathEndMode.Touch, r2P);
            bool fromMid = ReachabilityImmediate.CanReachImmediate(r2SA.Position, r, map, PathEndMode.Touch, r2P);
            Result("M touch across a stair step: from H the pawn on E is touched, from the middle of the ramp not", fromH && !fromMid, $"from H {fromH}, from L1 {fromMid}");
            r2Flag = false;
            int hits0 = r.health.hediffSet.hediffs.OfType<Hediff_Injury>().Count();
            r2Seed = hits0;
            R2Mark();
            Job job = JobMaker.MakeJob(JobDefOf.AttackMelee, r);
            job.playerForced = true;
            r2P.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            TickUntil(600, () => r.Dead || r.health.hediffSet.hediffs.OfType<Hediff_Injury>().Count() >= hits0 + 2, "r2_m_check", 10);
        }

        private static void R2MeleeCheck(Map map)
        {
            tickWatch = null;
            Pawn r = r2Thing as Pawn;
            int hits = r == null ? 0 : r.health.hediffSet.hediffs.OfType<Hediff_Injury>().Count();
            Result("M melee across a stair step: the colonist on H hits the raider on E without leaving its floor",
                r != null && (r.Dead || hits > r2Seed) && Levels.LevelOf(r2P.Position, map) == 0, $"raider injuries {r2Seed} -> {hits} (dead {r?.Dead}), colonist at {r2P.Position}; {R2Stats()}");
            if (r != null && !r.Destroyed)
            {
                r.Destroy();
            }
            stage = "r2_rc";
        }

        // ------------------------------------------------------------------------------------------------ RC: raiders

        private static void R2Raid(Map map)
        {
            R2Clear(map);
            R2Park(map, r2P);
            R2Hide(map, r2P);
            R2Feed(map);
            R2Floor(map, new CellRect(RG(10, 14).x, RG(10, 14).z, 16, 10), 1);
            r2SA = R2Stairs(map, RG(14, 11), Rot4.North);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Teleport(r2P, RL(20, 20, 1, map));
            Draft(r2P, true);
            r2P.drafter.FireAtWill = false;
            List<Pawn> raiders = new List<Pawn>();
            for (int k = 0; k < 2; k++)
            {
                Pawn r = MakeRaider(map, RG(4 + 2 * k, 3));
                r.equipment.DestroyAllEquipment();
                r.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MeleeWeapon_Club"), ThingDefOf.WoodLog));
                raiders.Add(r);
            }
            LordMaker.MakeNewLord(raiders[0].Faction, new LordJob_AssaultColony(raiders[0].Faction, canKidnap: false, canTimeoutOrFlee: false, canSteal: false), map, raiders);
            R2Mark();
            TickUntil(3000, () => map.mapPawns.AllPawnsSpawned.Any(p => p.HostileTo(Faction.OfPlayer) && Levels.LevelOf(p.Position, map) == 1), "r2_rc_check", 15);
        }

        private static void R2RaidCheck(Map map)
        {
            tickWatch = null;
            List<Pawn> h = map.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer)).ToList();
            Result("RC melee raiders on the ground cross floors: up the stairs to the colonist upstairs", h.Any(p => Levels.LevelOf(p.Position, map) == 1) && climbsHostiles - r2HostClimbsAt >= 1,
                $"{string.Join("; ", h.Select(p => p + " " + p.Position + " " + p.CurJobDef?.defName))}; {R2Stats()}");
            foreach (Pawn p in h)
            {
                p.Destroy();
            }
            Heal(r2P);
            R2Unhide(map);
            stage = "r2_sp";
        }

        // ------------------------------------------------------------------------------------------------ SP: sappers

        // The colonist in a walled room upstairs (no door); stairs up outside it. Sappers: up the stairs and through the wall.
        private static void R2Sapper(Map map)
        {
            R2Clear(map);
            R2Park(map, r2P);
            R2Hide(map, r2P);
            R2Feed(map);
            R2Floor(map, new CellRect(RG(8, 12).x, RG(8, 12).z, 24, 12), 1);
            r2SA = R2Stairs(map, RG(10, 9), Rot4.North);   // exit (10, 12)
            CellRect room = new CellRect(RG(20, 14).x, RG(20, 14).z, 8, 8);
            foreach (IntVec3 g in room.EdgeCells)
            {
                R2Wall(map, Levels.Cell(g, 1, map));
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Teleport(r2P, RL(24, 18, 1, map));
            Draft(r2P, true);
            r2P.drafter.FireAtWill = false;
            Pawn r = MakeRaider(map, RG(4, 3));
            r.equipment.DestroyAllEquipment();
            TraverseParms tp = TraverseParms.For(r, Danger.Deadly, TraverseMode.PassAllDestroyableThings);
            bool reach = map.reachability.CanReach(r.Position, r2P, PathEndMode.Touch, tp);
            PawnPath p = map.pathFinder.FindPathNow(r.Position, r2P, tp, null, PathEndMode.Touch);
            bool found = p != null && p.Found;
            bool overStairs = found && p.NodesReversed.Contains(r2SA.TopCell);
            p?.Dispose();
            Result("SP a wall-breaking path between floors: reachable, found, over the stairs", reach && found && overStairs, $"reach {reach}, path {found}, over the stairs {overStairs}");
            r2Thing = r;
            // two raiders (the lord picks its sapper from them: SappersUtility), the sappers' destination set to the colonist (the
            // game picks a random raid destination: GenAI.RandomRaidDest)
            Pawn r2 = MakeRaider(map, RG(6, 3));
            Lord lord = LordMaker.MakeNewLord(r.Faction, new LordJob_AssaultColony(r.Faction, canKidnap: false, canTimeoutOrFlee: false, sappers: true, canSteal: false), map, new List<Pawn> { r, r2 });
            if (lord.CurLordToil is LordToil_AssaultColonySappers st && st.data is LordToilData_AssaultColonySappers sd)
            {
                sd.sapperDest = r2P.Position;
                st.UpdateAllDuties();
            }
            Info($"SP sappers: {r} duty {r.mindState.duty?.def?.defName}, {r2} duty {r2.mindState.duty?.def?.defName}, destination {r2P.Position}");
            r2Seed = room.EdgeCells.Count(g => Levels.Cell(g, 1, map).GetEdifice(map) != null);
            R2Mark();
            TickUntil(8000, () => room.EdgeCells.Count(g => Levels.Cell(g, 1, map).GetEdifice(map) != null) < r2Seed || map.mapPawns.AllPawnsSpawned.Any(h => h.HostileTo(Faction.OfPlayer) && Levels.LevelOf(h.Position, map) == 1 && room.Contains(Levels.Ground(h.Position, map))), "r2_sp_check", 30);
        }

        private static void R2SapperCheck(Map map)
        {
            tickWatch = null;
            Pawn r = r2Thing as Pawn;
            CellRect room = new CellRect(RG(20, 14).x, RG(20, 14).z, 8, 8);
            int walls = room.EdgeCells.Count(g => Levels.Cell(g, 1, map).GetEdifice(map) != null);
            Result("SP a sapper raider goes up the stairs and breaks into the walled room upstairs", (walls < r2Seed || map.mapPawns.AllPawnsSpawned.Any(h => h.HostileTo(Faction.OfPlayer) && Levels.LevelOf(h.Position, map) == 1 && room.Contains(Levels.Ground(h.Position, map)))) && climbsHostiles - r2HostClimbsAt >= 1,
                $"walls {r2Seed} -> {walls}; sapper at {r?.Position} job {r?.CurJobDef?.defName}; {R2Stats()}");
            foreach (Pawn h in map.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer)).ToList())
            {
                h.Destroy();
            }
            Heal(r2P);
            R2Unhide(map);
            stage = "r2_x";
        }

        // ------------------------------------------------------------------------------------------------ X: exit map

        private static void R2Exit(Map map)
        {
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            R2Floor(map, new CellRect(RG(10, 14).x, RG(10, 14).z, 16, 10), 1);
            r2SA = R2Stairs(map, RG(14, 11), Rot4.North);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Pawn r = MakeRaider(map, RL(20, 20, 1, map));
            r2Thing = r;
            LordMaker.MakeNewLord(r.Faction, new LordJob_ExitMapBest(LocomotionUrgency.Jog), map, new List<Pawn> { r });
            R2Mark();
            TickUntil(5000, () => !r.Spawned, "r2_x_check", 30);
        }

        private static void R2ExitCheck(Map map)
        {
            tickWatch = null;
            Pawn r = r2Thing as Pawn;
            Result("X a pawn upstairs told to leave the map goes down the stairs and leaves", r != null && !r.Spawned && !r.Dead && climbsHostiles - r2HostClimbsAt >= 1, $"{r} spawned {r?.Spawned} dead {r?.Dead}; {R2Stats()}");
            stage = "r2_k1";
        }

        // ------------------------------------------------------------------------------------------------ K: last stairs

        // 1: a platform reached only by these stairs, the worker on it: it works from the ground (the side that stays joined).
        // 2: the stairs stand in a walled ground pocket; the platform has other stairs down outside: the worker works from
        //    the platform (rim), never from the pocket.
        // 3: as 1, but walls on the platform close the stairs' exit off from the rest of it (the rim round the stairwell
        //    stays reachable): the worker could only work from the rim, which would cut it off: no job.
        private static void R2Cut(Map map, int which)
        {
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            // the platform over the stairs: the stairs' well (14, 10..12) is cut out of it, the exit E (14, 13) on it
            CellRect plat = new CellRect(RG(10, 6).x, RG(10, 6).z, 16, 14);
            R2Floor(map, plat, 1);
            r2SA = R2Stairs(map, RG(14, 11), Rot4.North);
            r2SB = null;
            if (which == 2)
            {
                // the pocket round the stairs on the ground (3 x 5 ring round the ramp), no door; the platform's other
                // stairs down outside it
                CellRect pocket = new CellRect(RG(13, 9).x, RG(13, 9).z, 3, 5);
                foreach (IntVec3 g in pocket.EdgeCells)
                {
                    R2Wall(map, g);
                }
                r2SB = R2Stairs(map, RG(22, 11), Rot4.North);
            }
            if (which == 3)
            {
                // E (14, 13) walled in on the platform (its rim neighbours and the row behind it)
                foreach (IntVec3 g in new[] { RG(13, 13), RG(15, 13), RG(13, 14), RG(14, 14), RG(15, 14) })
                {
                    R2Wall(map, Levels.Cell(g, 1, map));
                }
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            IntVec3 start = which == 2 ? RG(30, 2) : RL(20, 17, 1, map);
            Teleport(r2P, start);
            Draft(r2P, false);
            R2Work(r2P, WorkTypeDefOf.Construction);
            map.designationManager.AddDesignation(new Designation(r2SA, DesignationDefOf.Deconstruct));
            RouteGraph g2 = RouteGraph.For(map);
            int side = g2 == null ? 0 : g2.CutSide(g2.LinkOf(r2SA));
            Info($"K{which}: stairs {r2SA?.Position}, other stairs {r2SB?.Position}, cut side {side} (+1: top cut off, -1: low side cut off), worker at {start}");
            r2Seed = side;
            R2Mark();
            TickUntil(which == 3 ? 1500 : 4000, () => r2SA.Destroyed, "r2_k" + which + "_check", 15);
        }

        private static void R2CutCheck(Map map, int which)
        {
            tickWatch = null;
            int lv = Levels.LevelOf(r2P.Position, map);
            if (which == 1)
            {
                Result("K1 the last stairs to a platform taken up with the worker on it: it works from the ground and stays joined to the map",
                    r2Seed == 1 && r2SA.Destroyed && lv == 0, $"cut side {r2Seed}, stairs gone {r2SA.Destroyed}, worker at {r2P.Position} level {lv}; {R2Stats()}");
            }
            else if (which == 2)
            {
                CellRect pocket = new CellRect(RG(13, 9).x, RG(13, 9).z, 3, 5);
                bool inPocket = lv == 0 && pocket.Contains(r2P.Position);
                Result("K2 stairs standing in a walled ground pocket taken up: the worker works from the platform above (not from the pocket) and stays joined",
                    r2Seed == -1 && r2SA.Destroyed && !inPocket && lv == 1, $"cut side {r2Seed}, stairs gone {r2SA.Destroyed}, worker at {r2P.Position} level {lv}, in the pocket {inPocket}; {R2Stats()}");
            }
            else
            {
                IntVec3 rim = RL(13, 11, 1, map);
                bool rimReach = r2P.CanReach(rim, PathEndMode.OnCell, Danger.Deadly);
                Result("K3 the last stairs, the worker on the platform with the exit walled off but the rim reachable: refused, the stairs stay",
                    r2Seed == 1 && !r2SA.Destroyed && lv == 1 && rimReach, $"cut side {r2Seed}, stairs standing {!r2SA.Destroyed}, worker at {r2P.Position}, rim {rim} reachable {rimReach}, job {r2P.CurJobDef?.defName}; {R2Stats()}");
                map.designationManager.RemoveAllDesignationsOn(r2SA);
            }
            R2Work(r2P);
            stage = which == 1 ? "r2_k2" : which == 2 ? "r2_k3" : "r2_l";
        }

        // ------------------------------------------------------------------------------------------------ L: ledge

        private static void R2Ledge(Map map)
        {
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            R2Floor(map, new CellRect(RG(16, 12).x, RG(16, 12).z, 5, 5), 1);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Teleport(r2P, RL(18, 14, 1, map));
            Draft(r2P, true);
            r2Dest = RG(18, 4);
            bool reach = r2P.CanReach(r2Dest, PathEndMode.OnCell, Danger.Deadly);
            Result("L a pawn on a ledge only a jump would leave: the ground is not reachable", !reach, $"reach {reach}");
            R2Mark();
            GoTo(r2P, r2Dest);
            StartTicks(600, "r2_l_check");
        }

        private static void R2LedgeCheck(Map map)
        {
            Result("L ordered down it stays on the ledge (no jump), no error, no loop", Levels.LevelOf(r2P.Position, map) == 1 && errors == r2ErrAt && PZStats.finds - r2FindsAt <= 3 && PZStats.jumps == 0,
                $"at {r2P.Position}; {R2Stats()}");
            stage = "r2_end";
        }
    }
}
