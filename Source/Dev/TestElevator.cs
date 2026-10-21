using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // Mode elevator (the player elevator, Elevator.cs; user 2026-10-20). Run with -projectzfloors=5 -projectzseed=7.
    // Every check builds its own fixture in one cleared area (all levels wiped, router2's R2Clear) under its own random
    // state, and runs real orders and jobs with real ticks. The fixture: platforms on levels 1-4 over ground x 10..18,
    // z 8..19; the elevator at (19, 13) turned west (its stops: (18, 13) on every level), powered by a charged battery
    // under the platforms.
    //   C   rides of 1, 2 and 4 floors, up and down: 30 + 39 ticks per floor; a job override asked mid-ride is held off
    //   R   route choice: elevator near, stairs far (the elevator); stairs near, elevator far (the stairs); the router's
    //       pick matches the oracle's cheapest route
    //   M   three colonists ordered up 4 floors at once: no queue, all ride together, all arrive
    //   H   a colonist hauls steel up the elevator to a stockpile on level 4
    //   CA  a downed colonist carried up 2 floors: no slower
    //   PW  the power cut mid-ride (the battery destroyed): the rider finishes as it began; then an emergency ladder:
    //       3 s per floor, one at a time (two colonists); power back (a new battery): an elevator again
    //   AN  a tame animal follows its drafted master up
    //   VI  a friendly visitor group goes to its chill spot on level 3 by the elevator
    //   PR  a prisoner and a raider can't reach upstairs over it (a colonist can); a prisoner breaking out can
    //   CO  the shaft kept open: floor, wall, roof area, roof work, blueprints refused (the elevator's message);
    //       placement refused where the shaft is blocked (a floor, a roof, a blueprint); the safety net (a floor spawned
    //       in the shaft: links only below it, a rider on a link past it falls; opened again: linked again)
    //   RF  the shaft roofed (second round): its cells carry the stairwell roof, a room beside it on level 1 is indoors and
    //       roofed; a colonist lays a floor in the shaft on level 3 (the cap): the elevator serves levels 0-2; the colonist
    //       takes it up again: no ordinary roof left, the elevator serves 0-4 again
    //   PU  placed under a floor: the floor in the shaft is marked for removal, the elevator waits, colonists take the
    //       floor up and build the elevator
    //   K2  the last way out: an elevator whose own floor would be cut off without it is not taken down from there
    //   XB  a stop whose exit is blocked is skipped (the ride goes past it), its floor unreachable
    //   S   screenshots of a ride (3D), up and down
    //   SV  saved and loaded mid-ride: the rider starts its ride again and arrives
    //   B   the elevator destroyed mid-ride: the rider falls down the shaft; D a colonist deconstructs it while another
    //       shuttles up and down: the rider in the shaft falls
    //   RD  real raids: with stairs to the platform the raiders take the stairs, never the elevator; without stairs they
    //       place siege ladders, never ride the elevator
    public static partial class TestRunner
    {
        private static Building_Elevator elE;
        private static Thing elBattery;
        private static readonly int[] elSpans = { 1, 2, 4 };
        private static int elIdx;
        private static IntVec3 elDest, elDest2, elDest3;
        private static Pawn elR, elA;
        private static readonly List<Pawn> elPawns = new List<Pawn>();
        private static int elMaxRiders, elWaitsAt, elFallsAt, elFallFloorsAt, elHurtAt, elT0, elTicks, elPlacedAt, elCarAt, elRelinksAt;
        private static bool elFlag, elFlag2, elOverrideHeld;
        private static float elHeightAt;
        private static string elPId, elQId, elNote;
        private static Building_Stairs elStairs;
        private static readonly List<Pawn> elGuests = new List<Pawn>();

        private sealed class ElRide
        {
            public Pawn p;
            public int t0, t1 = -1;
            public VLink link;
            public IntVec3 from, to;
            public bool arrived;
        }

        private static readonly List<ElRide> elRides = new List<ElRide>();
        private static readonly Dictionary<Pawn, ElRide> elCur = new Dictionary<Pawn, ElRide>();
        private static readonly Dictionary<Pawn, int> elStairSteps = new Dictionary<Pawn, int>();
        private static readonly HashSet<Pawn> elOnStair = new HashSet<Pawn>();

        private static IntVec3 ElG(int x, int z) => RG(x, z);

        private static IntVec3 ElL(int x, int z, int L, Map map) => RL(x, z, L, map);

        private static bool StepElevator(Map map)
        {
            Rand.PushState((int)(StableHash("elevator_" + stage) ^ (uint)(SeedArg ?? 717)));
            try
            {
                return StepElevatorInner(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static bool StepElevatorInner(Map map)
        {
            switch (stage)
            {
                case "el_setup":
                    ElSetup(map);
                    return true;
                case "el_c":
                    ElClimb(map);
                    return true;
                case "el_c_go":
                    ElClimbGo(map);
                    return true;
                case "el_c_up":
                    ElClimbCheck(map, true);
                    return true;
                case "el_c_down":
                    ElClimbCheck(map, false);
                    return true;
                case "el_r":
                    ElRoute(map, true);
                    return true;
                case "el_r_go":
                    ElRouteGo(map, true);
                    return true;
                case "el_r_check":
                    ElRouteCheck(map, true);
                    return true;
                case "el_rb":
                    ElRoute(map, false);
                    return true;
                case "el_rb_go":
                    ElRouteGo(map, false);
                    return true;
                case "el_rb_check":
                    ElRouteCheck(map, false);
                    return true;
                case "el_m":
                    ElMany(map);
                    return true;
                case "el_m_go":
                    ElManyGo(map);
                    return true;
                case "el_m_check":
                    ElManyCheck(map);
                    return true;
                case "el_h":
                    ElHaul(map);
                    return true;
                case "el_h_go":
                    ElHaulGo(map);
                    return true;
                case "el_h_check":
                    ElHaulCheck(map);
                    return true;
                case "el_ca":
                    ElCarry(map);
                    return true;
                case "el_ca_pick":
                    ElCarryPick(map);
                    return true;
                case "el_ca_go":
                    ElCarryGo(map);
                    return true;
                case "el_ca_check":
                    ElCarryCheck(map);
                    return true;
                case "el_pw":
                    ElPower(map);
                    return true;
                case "el_pw_go":
                    ElPowerGo(map);
                    return true;
                case "el_pw_check":
                    ElPowerCheck(map);
                    return true;
                case "el_pw_ladder":
                    ElLadderCheck(map);
                    return true;
                case "el_pw_queue":
                    ElQueueCheck(map);
                    return true;
                case "el_pw_back":
                    ElBack(map);
                    return true;
                case "el_pw_back_check":
                    ElBackCheck(map);
                    return true;
                case "el_an":
                    ElAnimal(map);
                    return true;
                case "el_an_go":
                    ElAnimalGo(map);
                    return true;
                case "el_an_check":
                    ElAnimalCheck(map);
                    return true;
                case "el_vi":
                    ElVisitors(map);
                    return true;
                case "el_vi_go":
                    ElVisitorsGo(map);
                    return true;
                case "el_vi_check":
                    ElVisitorsCheck(map);
                    return true;
                case "el_pr":
                    ElPrisoner(map);
                    return true;
                case "el_pr_go":
                    ElPrisonerGo(map);
                    return true;
                case "el_pr_break":
                    ElPrisonBreakCheck(map);
                    return true;
                case "el_co":
                    ElColumn(map);
                    return true;
                case "el_co_go":
                    ElColumnGo(map);
                    return true;
                case "el_co_net":
                    ElNetCheck(map);
                    return true;
                case "el_co_open":
                    ElNetOpenCheck(map);
                    return true;
                case "el_co_ride":
                    ElNetRide(map);
                    return true;
                case "el_co_ride_go":
                    ElNetRideGo(map);
                    return true;
                case "el_co_ride_check":
                    ElNetRideCheck(map);
                    return true;
                case "el_rf":
                    ElRoof(map);
                    return true;
                case "el_rf_go":
                    ElRoofGo(map);
                    return true;
                case "el_rf_capped":
                    ElCapped(map);
                    return true;
                case "el_rf_uncapped":
                    ElUncapped(map);
                    return true;
                case "el_pu":
                    ElUnder(map);
                    return true;
                case "el_pu_go":
                    ElUnderGo(map);
                    return true;
                case "el_pu_check":
                    ElUnderCheck(map);
                    return true;
                case "el_k2":
                    ElK2(map);
                    return true;
                case "el_k2_go":
                    ElK2Go(map);
                    return true;
                case "el_k2_check":
                    ElK2Check(map);
                    return true;
                case "el_xb":
                    ElExitBlocked(map);
                    return true;
                case "el_xb_go":
                    ElExitBlockedGo(map);
                    return true;
                case "el_xb_check":
                    ElExitBlockedCheck(map);
                    return true;
                case "el_s":
                    ElShots(map);
                    return true;
                case "el_s_go":
                    ElShotsGo(map);
                    return true;
                case "el_s0":
                    ElShot(map, "elevator_board_up", 1, "el_s0_take");
                    return true;
                case "el_s0_take":
                    Capture("elevator_board_up");
                    TickUntil(2000, () => Links.Kind(r2P) == StepKind.ElevatorUp && r2P.pather.MovePercentage >= 0.5f, "el_s1", 1);
                    return true;
                case "el_s1":
                    ElShot(map, "elevator_ride_up_50", 2, "el_s1_take");
                    return true;
                case "el_s1_take":
                    Capture("elevator_ride_up_50");
                    Result("S5 a platform is drawn under the powered rider (IsoRim Api 8)", IsoRimBridge.ApiVersion < 8 || ElevatorDraw.Drawn > 0, $"IsoRim Api {IsoRimBridge.ApiVersion}, platforms drawn {ElevatorDraw.Drawn}");
                    TickUntil(2000, () => r2P.Position == elDest, "el_s2", 5);
                    return true;
                case "el_s2":
                    tickWatch = null;
                    elDest = ElG(22, 13);
                    GoTo(r2P, elDest);
                    TickUntil(2000, () => Links.Kind(r2P) == StepKind.ElevatorDown && r2P.pather.MovePercentage >= 0.5f, "el_s3", 1);
                    return true;
                case "el_s3":
                    ElShot(map, "elevator_ride_down_50", 2, "el_s3_take");
                    return true;
                case "el_s3_take":
                    Capture("elevator_ride_down_50");
                    TickUntil(2000, () => r2P.Position == elDest, "el_s4", 5);
                    return true;
                case "el_s4":
                    ElShot(map, "elevator_idle", 0, "el_s4_take");
                    return true;
                case "el_s4_take":
                    Capture("elevator_idle");
                    stage = "el_sv";
                    return true;
                case "el_sv":
                    ElSave(map);
                    return true;
                case "el_sv_go":
                    ElSaveGo(map);
                    return true;
                case "el_sv_save":
                    ElSaveNow(map);
                    return true;
                case "el_sv_loaded":
                    ElLoaded(map);
                    return true;
                case "el_sv_check":
                    ElLoadedCheck(map);
                    return true;
                case "el_b":
                    ElBreak(map);
                    return true;
                case "el_b_go":
                    ElBreakGo(map);
                    return true;
                case "el_b_check":
                    ElBreakCheck(map);
                    return true;
                case "el_d":
                    ElDecon(map);
                    return true;
                case "el_d_go":
                    ElDeconGo(map);
                    return true;
                case "el_d_check":
                    ElDeconCheck(map);
                    return true;
                case "el_rd":
                    ElRaid(map, true);
                    return true;
                case "el_rd_go":
                    ElRaidGo(map, true);
                    return true;
                case "el_rd_check":
                    ElRaidCheck(map, true);
                    return true;
                case "el_rd2":
                    ElRaid(map, false);
                    return true;
                case "el_rd2_go":
                    ElRaidGo(map, false);
                    return true;
                case "el_rd2_check":
                    ElRaidCheck(map, false);
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------------ fixtures

        private static void ElSetup(Map map)
        {
            DebugSettings.enableRandomMentalStates = false;
            NoRoofCollapse(map);
            Find.Storyteller.storytellerComps.Clear();
            Find.PlaySettings.autoRebuild = false;
            NoAmbushes(map);
            foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
            {
                a.Destroy();
            }
            if (Levels.Max(map) < 4)
            {
                Result("elevator: a map with 5 levels", false, $"levels {Levels.Min(map)}..{Levels.Max(map)} (run with -projectzfloors=5)");
                Finish();
                return;
            }
            r2Area = new CellRect(map.Center.x - 22, map.Center.z - 16, 44, 32);
            List<Pawn> cols = Colonists(map);
            r2P = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)) ?? cols.First();
            r2Q = cols.FirstOrDefault(p => p != r2P && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) ?? cols.FirstOrDefault(p => p != r2P);
            foreach (Pawn p in cols)
            {
                p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
            }
            R2Park(map);
            Result("elevator: the def loads (Machining, 100 steel + 4 components, 150 W, rotatable)", ElevatorDefOf.PZ_Elevator != null && ElevatorDefOf.PZ_Elevator.researchPrerequisites?.Any(r => r.defName == "Machining") == true
                && ElevatorDefOf.PZ_Elevator.CostList.Any(c => c.thingDef == ThingDefOf.Steel && c.count == 100) && ElevatorDefOf.PZ_Elevator.CostList.Any(c => c.thingDef == ThingDefOf.ComponentIndustrial && c.count == 4)
                && Mathf.Approximately(ElevatorDefOf.PZ_Elevator.GetCompProperties<CompProperties_Power>()?.PowerConsumption ?? 0f, 150f) && ElevatorDefOf.PZ_Elevator.rotatable,
                $"def {ElevatorDefOf.PZ_Elevator?.defName}, research {string.Join(",", ElevatorDefOf.PZ_Elevator?.researchPrerequisites?.Select(r => r.defName) ?? new string[0])}, costs {string.Join(", ", ElevatorDefOf.PZ_Elevator?.CostList?.Select(c => c.thingDef.defName + " " + c.count) ?? new string[0])}, power {ElevatorDefOf.PZ_Elevator?.GetCompProperties<CompProperties_Power>()?.PowerConsumption}");
            Info($"elevator: area {r2Area}, walker {r2P}, second {r2Q}, levels {Levels.Min(map)}..{Levels.Max(map)}");
            elIdx = 0;
            stage = "el_c";
        }

        // Platforms on levels 1..top over ground x 10..18, z 8..19; the elevator at (19, 13) turned west; a charged battery
        // under the platforms. Then ticks until the elevator is powered and linked so (next).
        private static void ElFixture(Map map, string next, bool power = true, int top = 4, params Pawn[] keep) => ElFixture(map, next, false, power, top, keep);

        // mirror: the platforms east of the shaft (x 20..28), the elevator turned east (its stops (20, 13)): the camera's
        // side for the shots (the game's view looks north-east: a shaft west of the platforms is in front of them).
        private static void ElFixture(Map map, string next, bool mirror, bool power, int top, params Pawn[] keep)
        {
            R2Clear(map);
            R2Park(map, keep);
            R2Feed(map);
            LinkSteps.For(map)?.steps.Clear();
            for (int L = 1; L <= top; L++)
            {
                R2Floor(map, new CellRect(ElG(mirror ? 20 : 10, 8).x, ElG(10, 8).z, 9, 12), L);
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            elE = Place(ElevatorDefOf.PZ_Elevator, null, ElG(19, 13), map, mirror ? Rot4.East : Rot4.West) as Building_Elevator;
            elBattery = null;
            if (power)
            {
                ElNewBattery(map, mirror);
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            elPawns.Clear();
            ElResetRides();
            SeedTicks("el_power_" + next);
            TickUntil(400, () => elE != null && elE.Spawned && (!power || elE.Powered && elE.LinkedPowered), next, 1);
        }

        private static void ElNewBattery(Map map, bool mirror = false)
        {
            elBattery = Place(ThingDefOf.Battery, null, ElG(mirror ? 22 : 16, 9), map, Rot4.North);
            elBattery.TryGetComp<CompPowerBattery>()?.AddEnergy(10000f);
        }

        private static void ElResetRides()
        {
            elRides.Clear();
            elCur.Clear();
            elStairSteps.Clear();
            elOnStair.Clear();
            elMaxRiders = 0;
        }

        // Every tick: the rides of the watched pawns (start when the pawn's step is an elevator step, end when it is not).
        private static void ElWatch(Map map)
        {
            int now = Find.TickManager.TicksGame;
            foreach (Pawn p in elPawns)
            {
                if (p == null || !p.Spawned)
                {
                    if (p != null && elCur.TryGetValue(p, out ElRide gone))
                    {
                        gone.t1 = now;
                        elCur.Remove(p);
                    }
                    continue;
                }
                StepKind k = Links.Kind(p, out VLink l);
                bool el = k == StepKind.ElevatorUp || k == StepKind.ElevatorDown;
                bool st = k == StepKind.StairUp || k == StepKind.StairDown;
                if (st && elOnStair.Add(p))
                {
                    elStairSteps[p] = (elStairSteps.TryGetValue(p, out int n) ? n : 0) + 1;
                }
                else if (!st)
                {
                    elOnStair.Remove(p);
                }
                if (elCur.TryGetValue(p, out ElRide cur) && (!el || cur.from != p.Position || cur.to != p.pather.nextCell))
                {
                    cur.t1 = now;
                    cur.arrived = p.Position == cur.to;
                    elCur.Remove(p);
                }
                if (el && !elCur.ContainsKey(p))
                {
                    ElRide r = new ElRide { p = p, t0 = now, link = l, from = p.Position, to = p.pather.nextCell };
                    elRides.Add(r);
                    elCur[p] = r;
                }
            }
            elMaxRiders = Math.Max(elMaxRiders, elCur.Count);
        }

        private static List<ElRide> RidesOf(Pawn p) => elRides.Where(r => r.p == p).ToList();

        private static string RideStr(ElRide r) => r == null ? "no ride" : $"{r.p.LabelShort} {r.link.kind} span {r.link.span} {r.from} -> {r.to} ticks {(r.t1 < 0 ? -1 : r.t1 - r.t0)} arrived {r.arrived}";

        private static int ElWant(int span) => VLink.ElevatorBoardTicks + VLink.ElevatorTicksPerFloor * span;

        private static string ElLinks(Map map)
        {
            StairGrid g = StairGrid.For(map);
            if (g == null || elE == null)
            {
                return "no links";
            }
            List<VLink> ls = g.links.Where(l => l.thing == elE).ToList();
            return $"{ls.Count} link(s), kinds {string.Join("/", ls.Select(l => l.kind).Distinct())}, top level {elE.LinkedTop}, powered {elE.Powered}";
        }

        private static LinkSteps.Step ElStep(Map map, Pawn p) => LinkSteps.For(map)?.Of(p);

        private static void ElMarkFalls()
        {
            elFallsAt = LinkSteps.Falls;
            elFallFloorsAt = LinkSteps.FallFloors;
            elHurtAt = JumpGrid.HurtLandings;
            elWaitsAt = LinkSteps.Waits;
            elCarAt = LinkSteps.CarExits;
        }

        // ------------------------------------------------------------------------------------------------ C: rides

        private static void ElClimb(Map map)
        {
            ElFixture(map, "el_c_go", true, 4, r2P);
            Teleport(r2P, ElG(15, 13));
            Draft(r2P, true);
        }

        private static void ElClimbGo(Map map)
        {
            tickWatch = null;
            int n = elSpans[elIdx];
            int stops = StairGrid.For(map)?.links.Count(l => l.thing == elE) ?? 0;
            if (elIdx == 0)
            {
                Result("C fixture: the elevator links every pair of its 5 floors (10 links), powered", elE != null && stops == 10 && elE.LinkedPowered && elE.LinkedTop == 4, ElLinks(map));
            }
            elPawns.Add(r2P);
            ElResetRides();
            elFlag = elOverrideHeld = false;
            elDest = ElL(14, 13, n, map);
            Job job = JobMaker.MakeJob(JobDefOf.Goto, elDest);
            if (n == 4)
            {
                job.expiryInterval = 100;
                job.checkOverrideOnExpire = false;
            }
            r2P.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder);
            int held0 = PZStats.climbOverridesHeld;
            SeedTicks("el_c_up" + n);
            TickUntil(3000, () =>
            {
                ElWatch(map);
                if (n == 4 && !elFlag && LinkSteps.Climbing(r2P) && r2P.pather.MovePercentage > 0.5f)
                {
                    elFlag = true;
                    r2P.jobs.CheckForJobOverride();
                    elOverrideHeld = LinkSteps.Climbing(r2P) && PZStats.climbOverridesHeld > held0;
                }
                return r2P.Position == elDest;
            }, "el_c_up", 1);
        }

        private static void ElClimbCheck(Map map, bool up)
        {
            tickWatch = null;
            int n = elSpans[elIdx];
            ElRide r = RidesOf(r2P).LastOrDefault();
            int took = r == null || r.t1 < 0 ? -1 : r.t1 - r.t0;
            bool there = up ? r2P.Position == elDest : r2P.Position == ElG(15, 13);
            Result($"C{n} {(up ? "up" : "down")}: one ride of {n} floor(s), {ElWant(n)} ticks (0.5 s + 0.65 s per floor), arrives", there && r != null && r.link.kind == LinkKind.Elevator && r.link.span == n && r.arrived && Math.Abs(took - ElWant(n)) <= 3 && RidesOf(r2P).Count == 1,
                $"{RideStr(r)}, rides {RidesOf(r2P).Count}, at {r2P.Position} level {Levels.LevelOf(r2P.Position, map)}");
            if (up && n == 4)
            {
                Result("C4 up: a Goto with a 100-tick expiry is not cut short mid-ride, and a job override asked mid-ride is held off", there && elFlag && elOverrideHeld, $"asked {elFlag}, held {elOverrideHeld}");
            }
            if (up)
            {
                ElResetRides();
                GoTo(r2P, ElG(15, 13));
                SeedTicks("el_c_down" + n);
                TickUntil(3000, () =>
                {
                    ElWatch(map);
                    return r2P.Position == ElG(15, 13);
                }, "el_c_down", 1);
                return;
            }
            elIdx++;
            if (elIdx < elSpans.Length)
            {
                stage = "el_c_go";
                return;
            }
            stage = "el_r";
        }

        // ------------------------------------------------------------------------------------------------ R: route choice

        private static void ElRoute(Map map, bool nearElevator)
        {
            ElFixture(map, nearElevator ? "el_r_go" : "el_rb_go", true, 4, r2P);
            // stairs up to level 1 on the platform's west side: ramp (7..9, 10), exit (10, 10)
            elStairs = R2Stairs(map, ElG(8, 10), Rot4.East);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Teleport(r2P, nearElevator ? ElG(21, 13) : ElG(4, 10));
            Draft(r2P, true);
        }

        private static void ElRouteGo(Map map, bool nearElevator)
        {
            tickWatch = null;
            elDest = nearElevator ? ElL(16, 13, 1, map) : ElL(12, 10, 1, map);
            float oc = StairRouter.OracleCost(map, r2P.Position, elDest, PathEndMode.OnCell, TraverseParms.For(r2P), null, out string best);
            RouteGraph g = RouteGraph.For(map);
            string bestKind = "?";
            if (best.StartsWith("up") && int.TryParse(new string(best.Substring(2).TakeWhile(char.IsDigit).ToArray()), out int li) && g != null && li < g.links.Count)
            {
                bestKind = g.links[li].kind.ToString();
            }
            elNote = $"oracle best {best} ({bestKind}) cost {oc:0}";
            elPawns.Add(r2P);
            ElResetRides();
            GoTo(r2P, elDest);
            SeedTicks(nearElevator ? "el_r" : "el_rb");
            TickUntil(3000, () =>
            {
                ElWatch(map);
                return r2P.Position == elDest;
            }, nearElevator ? "el_r_check" : "el_rb_check", 1);
        }

        private static void ElRouteCheck(Map map, bool nearElevator)
        {
            tickWatch = null;
            bool rode = RidesOf(r2P).Count > 0;
            int stairSteps = elStairSteps.TryGetValue(r2P, out int s) ? s : 0;
            bool oracleOk = nearElevator ? elNote.Contains("(Elevator)") : elNote.Contains("(Stairs)");
            Result(nearElevator ? "R elevator near, stairs far: the cheaper elevator is taken (the oracle agrees)" : "R stairs near, elevator far: the cheaper stairs are taken (the oracle agrees)",
                elStairs != null && r2P.Position == elDest && (nearElevator ? rode && stairSteps == 0 : !rode && stairSteps > 0) && oracleOk,
                $"stairs {elStairs?.Position}, rides {RidesOf(r2P).Count} ({RideStr(RidesOf(r2P).FirstOrDefault())}), stair steps {stairSteps}, at {r2P.Position}; {elNote}; last route cost {StairRouter.LastCost:0}");
            stage = nearElevator ? "el_rb" : "el_m";
        }

        // ------------------------------------------------------------------------------------------------ M: many riders

        private static void ElMany(Map map)
        {
            List<Pawn> cols = Colonists(map);
            elR = cols.FirstOrDefault(p => p != r2P && p != r2Q && !p.Downed);
            ElFixture(map, "el_m_go", true, 4, r2P, r2Q, elR);
            Teleport(r2P, ElG(16, 12));
            Teleport(r2Q, ElG(16, 14));
            if (elR != null)
            {
                Teleport(elR, ElG(15, 13));
            }
        }

        private static void ElManyGo(Map map)
        {
            tickWatch = null;
            elPawns.AddRange(new[] { r2P, r2Q, elR }.Where(p => p != null));
            ElResetRides();
            ElMarkFalls();
            GoTo(r2P, ElL(13, 12, 4, map));
            GoTo(r2Q, ElL(13, 14, 4, map));
            if (elR != null)
            {
                GoTo(elR, ElL(14, 13, 4, map));
            }
            SeedTicks("el_m");
            TickUntil(3000, () =>
            {
                ElWatch(map);
                return elPawns.All(p => Levels.LevelOf(p.Position, map) == 4 && Links.Kind(p) == StepKind.None);
            }, "el_m_check", 1);
        }

        private static void ElManyCheck(Map map)
        {
            tickWatch = null;
            List<ElRide> rides = elRides.Where(r => r.t1 >= 0).ToList();
            bool timed = rides.Count == elPawns.Count && rides.All(r => r.arrived && r.link.span == 4 && Math.Abs(r.t1 - r.t0 - ElWant(4)) <= 3);
            int gap = rides.Count == 0 ? -1 : rides.Max(r => r.t1) - rides.Min(r => r.t1);
            Result($"M {elPawns.Count} colonists ordered up 4 floors at once: they ride together (no queue, no waits), each in {ElWant(4)} ticks, all arrive",
                elPawns.All(p => Levels.LevelOf(p.Position, map) == 4) && timed && elMaxRiders == elPawns.Count && LinkSteps.Waits == elWaitsAt && gap < ElWant(4),
                $"most riding at once {elMaxRiders}, waits {LinkSteps.Waits - elWaitsAt}, arrivals spread {gap} ticks; {string.Join("; ", rides.Select(RideStr))}");
            stage = "el_h";
        }

        // ------------------------------------------------------------------------------------------------ H: haul

        private static void ElHaul(Map map)
        {
            ElFixture(map, "el_h_go", true, 4, r2P);
            Teleport(r2P, ElG(12, 13));
        }

        private static void ElHaulGo(Map map)
        {
            tickWatch = null;
            Zone_Stockpile zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (IntVec3 g in new CellRect(ElG(12, 16).x, ElG(12, 16).z, 3, 3))
            {
                zone.AddCell(Levels.Cell(g, 4, map));
            }
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 30;
            GenPlace.TryPlaceThing(steel, ElG(14, 10), map, ThingPlaceMode.Direct);
            Draft(r2P, false);
            foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefs)
            {
                if (!r2P.WorkTypeIsDisabled(w))
                {
                    r2P.workSettings.SetPriority(w, w == WorkTypeDefOf.Hauling ? 1 : 0);
                }
            }
            elPawns.Add(r2P);
            ElResetRides();
            SeedTicks("el_h");
            TickUntil(8000, () =>
            {
                ElWatch(map);
                return ElSteelOn(map, 4) >= 30;
            }, "el_h_check", 1);
        }

        private static int ElSteelOn(Map map, int L) => map.listerThings.ThingsOfDef(ThingDefOf.Steel).Where(t => t.Spawned && Levels.LevelOf(t.Position, map) == L).Sum(t => t.stackCount);

        private static void ElHaulCheck(Map map)
        {
            tickWatch = null;
            int up = ElSteelOn(map, 4);
            Result("H a colonist hauls 30 steel up the elevator to the stockpile on level 4", up >= 30 && RidesOf(r2P).Any(r => r.link.kind == LinkKind.Elevator && r.arrived),
                $"steel on level 4: {up}, rides {string.Join("; ", RidesOf(r2P).Select(RideStr))}, hauler at {r2P.Position}, job {r2P.CurJobDef?.defName}");
            foreach (Zone z in map.zoneManager.AllZones.ToList())
            {
                z.Delete();
            }
            foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefs)
            {
                if (!r2P.WorkTypeIsDisabled(w))
                {
                    r2P.workSettings.SetPriority(w, 3);
                }
            }
            Draft(r2P, true);
            stage = "el_ca";
        }

        // ------------------------------------------------------------------------------------------------ CA: carry

        private static void ElCarry(Map map)
        {
            ElFixture(map, "el_ca_pick", true, 4, r2P, r2Q);
            Teleport(r2P, ElG(15, 13));
            Teleport(r2Q, ElG(15, 15));
            Draft(r2P, true);
            Draft(r2Q, false);
            HealthUtility.TryAnesthetize(r2Q);
        }

        private static void ElCarryPick(Map map)
        {
            tickWatch = null;
            Job job = JobMaker.MakeJob(JobDefOf.CarryDownedPawnDrafted, r2Q);
            job.count = 1;
            r2P.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder);
            SeedTicks("el_ca");
            TickUntil(1500, () => r2P.carryTracker.CarriedThing == r2Q, "el_ca_go", 5);
        }

        private static void ElCarryGo(Map map)
        {
            tickWatch = null;
            Result("CA fixture: the downed colonist is picked up", r2P.carryTracker.CarriedThing == r2Q, $"downed {r2Q.Downed}, carried {r2P.carryTracker.CarriedThing}");
            elPawns.Add(r2P);
            ElResetRides();
            elDest = ElL(14, 13, 2, map);
            GoTo(r2P, elDest);
            SeedTicks("el_ca_go");
            TickUntil(3000, () =>
            {
                ElWatch(map);
                return r2P.Position == elDest;
            }, "el_ca_check", 1);
        }

        private static void ElCarryCheck(Map map)
        {
            tickWatch = null;
            ElRide r = RidesOf(r2P).LastOrDefault();
            int took = r == null || r.t1 < 0 ? -1 : r.t1 - r.t0;
            Result($"CA carrying a downed colonist up 2 floors: no slower ({ElWant(2)} ticks), still carried on top", r2P.Position == elDest && Math.Abs(took - ElWant(2)) <= 3 && r2P.carryTracker.CarriedThing == r2Q,
                $"{RideStr(r)}, carrying {r2P.carryTracker.CarriedThing}");
            r2P.carryTracker.TryDropCarriedThing(r2P.Position, ThingPlaceMode.Near, out Thing _);
            LdWake(r2Q);
            stage = "el_pw";
        }

        // ------------------------------------------------------------------------------------------------ PW: power

        private static void ElPower(Map map)
        {
            ElFixture(map, "el_pw_go", true, 4, r2P, r2Q);
            Heal(r2P);
            Heal(r2Q);
            Teleport(r2P, ElG(15, 13));
            Teleport(r2Q, ElG(15, 15));
            Draft(r2P, true);
            Draft(r2Q, true);
        }

        private static void ElPowerGo(Map map)
        {
            tickWatch = null;
            elPawns.Add(r2P);
            elPawns.Add(r2Q);
            ElResetRides();
            elFlag = false;
            elNote = "";
            elDest = ElL(14, 13, 4, map);
            GoTo(r2P, elDest);
            SeedTicks("el_pw");
            TickUntil(3000, () =>
            {
                ElWatch(map);
                if (!elFlag && Links.Kind(r2P) == StepKind.ElevatorUp && r2P.pather.MovePercentage >= 0.5f)
                {
                    elFlag = true;
                    elHeightAt = ElStep(map, r2P)?.height ?? -1f;
                    elBattery.Destroy(DestroyMode.Vanish);
                    elT0 = Find.TickManager.TicksGame;
                }
                if (elFlag && elNote == "" && !elE.LinkedPowered)
                {
                    elNote = $"links a ladder {Find.TickManager.TicksGame - elT0} ticks after the cut, the rider still on its way {Links.Kind(r2P) != StepKind.None}";
                }
                return r2P.Position == elDest;
            }, "el_pw_check", 1);
        }

        private static void ElPowerCheck(Map map)
        {
            tickWatch = null;
            ElRide r = RidesOf(r2P).LastOrDefault();
            int took = r == null || r.t1 < 0 ? -1 : r.t1 - r.t0;
            Result($"PW power cut mid-ride (at {elHeightAt:0.00} floors): the rider finishes as it began ({ElWant(4)} ticks), the links turn into a ladder",
                elFlag && r2P.Position == elDest && r != null && r.link.kind == LinkKind.Elevator && Math.Abs(took - ElWant(4)) <= 3 && !elE.Powered && StairGrid.For(map).links.Where(l => l.thing == elE).All(l => l.kind == LinkKind.ElevatorLadder),
                $"{RideStr(r)}; {elNote}; {ElLinks(map)}");
            // the emergency ladder: down 2 floors alone
            ElResetRides();
            elDest = ElL(14, 13, 2, map);
            GoTo(r2P, elDest);
            SeedTicks("el_pw_ladder");
            TickUntil(3000, () =>
            {
                ElWatch(map);
                return r2P.Position == elDest;
            }, "el_pw_ladder", 1);
        }

        private static void ElLadderCheck(Map map)
        {
            tickWatch = null;
            // (as ladders, one climb of 2 floors or two of 1 cost the same: either may be taken)
            List<ElRide> lr = RidesOf(r2P);
            int took = lr.Count == 0 || lr.Last().t1 < 0 ? -1 : lr.Last().t1 - lr.First().t0;
            Result($"PW without power the elevator is an emergency ladder: down 2 floors in {VLink.LadderTicksPerFloor * 2} ticks (3 s per floor)",
                r2P.Position == elDest && lr.Count > 0 && lr.All(x => x.link.kind == LinkKind.ElevatorLadder && x.arrived) && lr.Sum(x => x.link.span) == 2 && Math.Abs(took - VLink.LadderTicksPerFloor * 2) <= 3,
                $"{took} ticks in all: {string.Join("; ", lr.Select(RideStr))}");
            // two at once: one at a time
            Teleport(r2Q, ElL(13, 14, 2, map));
            ElResetRides();
            ElMarkFalls();
            GoTo(r2P, ElG(14, 12));
            GoTo(r2Q, ElG(14, 14));
            elTicks = 0;
            SeedTicks("el_pw_queue");
            TickUntil(4000, () =>
            {
                ElWatch(map);
                int holders = LinkSteps.For(map).steps.Count(x => x.climbing && x.link.thing == elE && x.pawn.pather.MovePercentage > 0f);
                elTicks = Math.Max(elTicks, holders);
                return r2P.Position == ElG(14, 12) && r2Q.Position == ElG(14, 14);
            }, "el_pw_queue", 1);
        }

        private static void ElQueueCheck(Map map)
        {
            tickWatch = null;
            List<ElRide> rides = elRides.Where(x => x.t1 >= 0).ToList();
            int gap = rides.Count < 2 ? -1 : Math.Abs(rides[0].t1 - rides[1].t1);
            Result("PW the emergency ladder takes one at a time: the second colonist waits, both get down",
                r2P.Position == ElG(14, 12) && r2Q.Position == ElG(14, 14) && elTicks == 1 && LinkSteps.Waits > elWaitsAt && gap >= VLink.LadderTicksPerFloor * 2 - 3,
                $"most climbing at once {elTicks}, wait ticks {LinkSteps.Waits - elWaitsAt}, arrivals gap {gap}; {string.Join("; ", rides.Select(RideStr))}");
            // power back
            ElNewBattery(map);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            elT0 = Find.TickManager.TicksGame;
            SeedTicks("el_pw_back");
            TickUntil(400, () => elE.Powered && elE.LinkedPowered, "el_pw_back", 1);
        }

        private static void ElBack(Map map)
        {
            tickWatch = null;
            Result("PW power back (a new battery): an elevator again", elE.Powered && elE.LinkedPowered && StairGrid.For(map).links.Where(l => l.thing == elE).All(l => l.kind == LinkKind.Elevator),
                $"after {Find.TickManager.TicksGame - elT0} ticks; {ElLinks(map)}");
            ElResetRides();
            elDest = ElL(14, 13, 1, map);
            GoTo(r2P, elDest);
            SeedTicks("el_pw_back_go");
            TickUntil(3000, () =>
            {
                ElWatch(map);
                return r2P.Position == elDest;
            }, "el_pw_back_check", 1);
        }

        private static void ElBackCheck(Map map)
        {
            tickWatch = null;
            ElRide r = RidesOf(r2P).LastOrDefault();
            int took = r == null || r.t1 < 0 ? -1 : r.t1 - r.t0;
            Result($"PW powered again: up 1 floor in {ElWant(1)} ticks", r2P.Position == elDest && r != null && r.link.kind == LinkKind.Elevator && Math.Abs(took - ElWant(1)) <= 3, RideStr(r));
            stage = "el_an";
        }

        // ------------------------------------------------------------------------------------------------ AN: animal

        private static void ElAnimal(Map map)
        {
            ElFixture(map, "el_an_go", true, 4, r2P);
            Teleport(r2P, ElG(15, 13));
            Draft(r2P, false);
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Husky") ?? DefDatabase<PawnKindDef>.GetNamed("YorkshireTerrier");
            elA = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfPlayer, forceGenerateNewPawn: true));
            GenSpawn.Spawn(elA, ElG(14, 14), map);
            elA.training.Train(TrainableDefOf.Obedience, r2P, complete: true);
            elA.playerSettings.Master = r2P;
            elA.playerSettings.followDrafted = true;
        }

        private static void ElAnimalGo(Map map)
        {
            tickWatch = null;
            elPawns.Add(r2P);
            elPawns.Add(elA);
            ElResetRides();
            elDest = ElL(13, 12, 3, map);
            GoTo(r2P, elDest);
            SeedTicks("el_an");
            TickUntil(4000, () =>
            {
                ElWatch(map);
                return r2P.Position == elDest && Levels.LevelOf(elA.Position, map) == 3 && Links.Kind(elA) == StepKind.None;
            }, "el_an_check", 1);
        }

        private static void ElAnimalCheck(Map map)
        {
            tickWatch = null;
            Result("AN a tame animal (obedience, follows drafted) follows its master up 3 floors by the elevator",
                Levels.LevelOf(elA.Position, map) == 3 && RidesOf(elA).Any(r => r.arrived && r.link.span == 3),
                $"{elA.LabelShort} at {elA.Position} level {Levels.LevelOf(elA.Position, map)}, job {elA.CurJobDef?.defName}, may use {ElevatorRule.MayUse(elE, elA)}; rides {string.Join("; ", RidesOf(elA).Select(RideStr))}; master at {r2P.Position}");
            elA.Destroy();
            elA = null;
            stage = "el_vi";
        }

        // ------------------------------------------------------------------------------------------------ VI: visitors

        private static void ElVisitors(Map map)
        {
            ElFixture(map, "el_vi_go", true, 4, r2P);
        }

        private static void ElVisitorsGo(Map map)
        {
            tickWatch = null;
            Faction f = Find.FactionManager.AllFactions.Where(x => !x.IsPlayer && !x.HostileTo(Faction.OfPlayer) && x.def.humanlikeFaction && !x.def.hidden && !x.Hidden && !x.temporary && !x.defeated && x.def.pawnGroupMakers != null).OrderBy(x => x.loadID).FirstOrDefault();
            elGuests.Clear();
            elPawns.Clear();
            if (f == null)
            {
                Result("VI a friendly faction to visit", false, "none in this world");
                stage = "el_pr";
                return;
            }
            for (int i = 0; i < 2; i++)
            {
                Pawn v = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Villager, f, forceGenerateNewPawn: true));
                GenSpawn.Spawn(v, ElG(4 + i, 24), map);
                elGuests.Add(v);
                elPawns.Add(v);
            }
            elDest = ElL(13, 15, 3, map);
            Lord lord = LordMaker.MakeNewLord(f, new LordJob_VisitColony(f, elDest, 60000), map, elGuests);
            elNote = $"{f.Name} ({f.def.defName}), lord {lord.LordJob.GetType().Name}, may use {ElevatorRule.MayUse(elE, elGuests[0])}";
            ElResetRides();
            SeedTicks("el_vi");
            TickUntil(6000, () =>
            {
                ElWatch(map);
                return elGuests.All(v => v.Spawned && Levels.LevelOf(v.Position, map) == 3 && Links.Kind(v) == StepKind.None);
            }, "el_vi_check", 1);
        }

        private static void ElVisitorsCheck(Map map)
        {
            tickWatch = null;
            Result("VI a friendly visitor group goes to its chill spot on level 3 by the elevator",
                elGuests.All(v => v.Spawned && Levels.LevelOf(v.Position, map) == 3 && RidesOf(v).Any(r => r.arrived)),
                $"{elNote}; {string.Join("; ", elGuests.Select(v => $"{v.LabelShort} at {v.Position} level {Levels.LevelOf(v.Position, map)} toil {v.GetLord()?.CurLordToil?.GetType().Name} rides {RidesOf(v).Count}"))}");
            foreach (Pawn v in elGuests)
            {
                if (!v.Destroyed)
                {
                    v.Destroy();
                }
            }
            elGuests.Clear();
            stage = "el_pr";
        }

        // ------------------------------------------------------------------------------------------------ PR: prisoner, raider

        private static void ElPrisoner(Map map)
        {
            ElFixture(map, "el_pr_go", true, 4, r2P);
            Teleport(r2P, ElG(15, 13));
        }

        private static void ElPrisonerGo(Map map)
        {
            tickWatch = null;
            IntVec3 up = ElL(14, 13, 2, map);
            // a one-cell cell (walls round (15, 17), a door on its south side): a prison break starts in a room
            foreach (IntVec3 g in new CellRect(ElG(14, 16).x, ElG(14, 16).z, 3, 3).EdgeCells)
            {
                if (g != ElG(15, 16))
                {
                    R2Wall(map, g);
                }
            }
            Place(ThingDefOf.Door, ThingDefOf.WoodLog, ElG(15, 16), map);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Pawn pr = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Villager, LdRaiders(), forceGenerateNewPawn: true));
            GenSpawn.Spawn(pr, ElG(15, 17), map);
            pr.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
            elR = pr;
            Pawn raider = MakeRaider(map, ElG(15, 11));
            elA = raider;
            bool colOk = r2P.CanReach(up, PathEndMode.OnCell, Danger.Deadly);
            bool prOk = pr.CanReach(up, PathEndMode.OnCell, Danger.Deadly);
            bool raOk = raider.CanReach(up, PathEndMode.OnCell, Danger.Deadly);
            PawnPath pp = map.pathFinder.FindPathNow(pr.Position, up, TraverseParms.For(pr), null, PathEndMode.OnCell);
            bool prPath = pp != null && pp.Found;
            pp?.Dispose();
            PawnPath rp = map.pathFinder.FindPathNow(raider.Position, up, TraverseParms.For(raider, Danger.Deadly, TraverseMode.PassAllDestroyableThings), null, PathEndMode.OnCell);
            bool raPath = rp != null && rp.Found;
            rp?.Dispose();
            Result("PR a colonist can reach level 2 over the elevator; a prisoner and a raider can't (no path either, the raider not even breaking walls)",
                colOk && !prOk && !raOk && !prPath && !raPath && !ElevatorRule.MayUse(elE, pr) && !ElevatorRule.MayUse(elE, raider) && ElevatorRule.MayUse(elE, r2P),
                $"colonist {colOk}, prisoner {prOk} (path {prPath}), raider {raOk} (sapper path {raPath}); may use: colonist {ElevatorRule.MayUse(elE, r2P)}, prisoner {ElevatorRule.MayUse(elE, pr)}, raider {ElevatorRule.MayUse(elE, raider)}");
            raider.Destroy();
            elA = null;
            PrisonBreakUtility.StartPrisonBreak(pr);
            SeedTicks("el_pr_break");
            StartTicks(5, "el_pr_break");
        }

        private static void ElPrisonBreakCheck(Map map)
        {
            IntVec3 up = ElL(14, 13, 2, map);
            bool breaking = PrisonBreakUtility.IsPrisonBreaking(elR);
            bool ok = elR.CanReach(up, PathEndMode.OnCell, Danger.Deadly);
            Result("PR a prisoner breaking out may use it (the door rule: escapees open any door)", breaking && ok && ElevatorRule.MayUse(elE, elR),
                $"breaking {breaking}, lord {elR.GetLord()?.LordJob?.GetType().Name}, reach {ok}, may use {ElevatorRule.MayUse(elE, elR)}");
            elR.Destroy();
            elR = null;
            stage = "el_co";
        }

        // ------------------------------------------------------------------------------------------------ CO: column

        private static void ElColumn(Map map)
        {
            ElFixture(map, "el_co_go", true, 4, r2P);
        }

        private static void ElColumnGo(Map map)
        {
            tickWatch = null;
            string msg = "PZ_ElevatorShaftOpen".Translate();
            IntVec3 s2 = ElL(19, 13, 2, map);
            IntVec3 s1 = ElL(19, 13, 1, map);
            IntVec3 s0 = ElG(19, 13);
            AcceptanceReport floor = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_UpperFloor, s2, Rot4.North, map);
            AcceptanceReport wall = GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, s2, Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
            AcceptanceReport lamp = GenConstruct.CanPlaceBlueprintAt(ThingDefOf.StandingLamp, s2, Rot4.North, map);
            AcceptanceReport roofArea = new Designator_AreaBuildRoof().CanDesignateCell(s1);
            map.areaManager.BuildRoof[s1] = true;
            bool roofWork = new WorkGiver_BuildRoof().HasJobOnCell(r2P, s1);
            map.areaManager.BuildRoof[s1] = false;
            AcceptanceReport roofOver = new Designator_AreaBuildRoof().CanDesignateCell(s0);
            Result("CO the shaft is kept open: a wall and a lamp in it are refused with the elevator's message; a floor is not refused by the shaft (it caps it; the slab support rule still applies); a roof area may be drawn but no roof work comes (the shaft has its own roof)",
                (floor.Accepted || floor.Reason != msg) && !wall.Accepted && wall.Reason == msg && !lamp.Accepted && roofArea.Accepted && !roofWork && map.roofGrid.RoofAt(s1) == PZDefOf.PZ_StairwellRoof,
                $"floor {floor.Accepted} ({floor.Reason}), wall {wall.Accepted} ({wall.Reason}), lamp {lamp.Accepted} ({lamp.Reason}), roof area {roofArea.Accepted} ({roofArea.Reason}), roof over the elevator {roofOver.Accepted} ({roofOver.Reason}), roof work {roofWork}");
            // placement where the shaft is not open: a floor on level 3 and a roof over the cell (allowed: the floor is taken
            // up, the roof becomes the shaft's), a blueprint on level 2 and a wall on a floor on level 2 (refused)
            IntVec3 a = ElG(25, 6), b = ElG(27, 6), c = ElG(29, 6), d = ElG(31, 6), w = ElG(33, 6);
            map.terrainGrid.SetTerrain(ElL(33, 6, 2, map), PZDefOf.PZ_UpperFloor);
            R2Wall(map, ElL(33, 6, 2, map));
            map.terrainGrid.SetTerrain(ElL(25, 6, 3, map), PZDefOf.PZ_UpperFloor);
            map.roofGrid.SetRoof(b, RoofDefOf.RoofConstructed);
            // (a floor blueprint put there as it stands: the player may have planned it before)
            Blueprint bpFloor = GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_UpperFloor, ElL(29, 6, 2, map), map, Rot4.North, Faction.OfPlayer, null);
            string blocked = "PZ_ElevatorShaftBlocked".Translate();
            AcceptanceReport pa = GenConstruct.CanPlaceBlueprintAt(ElevatorDefOf.PZ_Elevator, a, Rot4.West, map);
            AcceptanceReport pb = GenConstruct.CanPlaceBlueprintAt(ElevatorDefOf.PZ_Elevator, b, Rot4.West, map);
            AcceptanceReport pc = GenConstruct.CanPlaceBlueprintAt(ElevatorDefOf.PZ_Elevator, c, Rot4.West, map);
            AcceptanceReport pd = GenConstruct.CanPlaceBlueprintAt(ElevatorDefOf.PZ_Elevator, d, Rot4.West, map);
            AcceptanceReport pw = GenConstruct.CanPlaceBlueprintAt(ElevatorDefOf.PZ_Elevator, w, Rot4.West, map);
            AcceptanceReport top = GenConstruct.CanPlaceBlueprintAt(ElevatorDefOf.PZ_Elevator, ElL(31, 13, 4, map), Rot4.West, map);
            Result("CO placement: under a floor (level 3) and under a built roof it is allowed; a floor blueprint (level 2) or a wall (level 2) in the shaft refuse it; open: allowed; the top level: refused",
                pa.Accepted && pb.Accepted && bpFloor != null && bpFloor.Spawned && !pc.Accepted && pc.Reason == blocked && !pw.Accepted && pw.Reason == blocked && pd.Accepted && !top.Accepted,
                $"floor {pa.Accepted} ({pa.Reason}), roof {pb.Accepted} ({pb.Reason}), blueprint {bpFloor?.def.defName} at {bpFloor?.Position} -> {pc.Accepted} ({pc.Reason}), wall {pw.Accepted} ({pw.Reason}), open {pd.Accepted} ({pd.Reason}), top level {top.Accepted} ({top.Reason})");
            // an elevator blueprint keeps its shaft open too
            GenConstruct.PlaceBlueprintForBuild(ElevatorDefOf.PZ_Elevator, d, map, Rot4.West, Faction.OfPlayer, null);
            AcceptanceReport inBp = GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, ElL(31, 6, 3, map), Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
            AcceptanceReport floorBp = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_UpperFloor, ElL(31, 6, 1, map), Rot4.North, map);
            Result("CO an elevator blueprint keeps its shaft open already (a wall, a floor in it refused)", !inBp.Accepted && !floorBp.Accepted,
                $"wall {inBp.Accepted} ({inBp.Reason}), floor {floorBp.Accepted} ({floorBp.Reason})");
            // the safety net: a floor spawned in the shaft on level 3
            elRelinksAt = Building_Elevator.Relinks;
            map.terrainGrid.SetTerrain(ElL(19, 13, 3, map), PZDefOf.PZ_UpperFloor);
            SeedTicks("el_co_net");
            StartTicks(3, "el_co_net");
        }

        private static void ElNetCheck(Map map)
        {
            List<VLink> ls = StairGrid.For(map)?.links.Where(l => l.thing == elE).ToList() ?? new List<VLink>();
            bool reach4 = r2P.CanReach(ElL(14, 13, 4, map), PathEndMode.OnCell, Danger.Deadly);
            bool reach2 = r2P.CanReach(ElL(14, 13, 2, map), PathEndMode.OnCell, Danger.Deadly);
            Result("CO safety net: a floor spawned in the shaft on level 3: the elevator stands and links only levels 0-2 (3 links); level 4 unreachable, level 2 still reachable",
                elE.Spawned && elE.LinkedTop == 2 && ls.Count == 3 && !reach4 && reach2, $"{ElLinks(map)}, reach level 4 {reach4}, level 2 {reach2}, relinks {Building_Elevator.Relinks - elRelinksAt}");
            // the floor taken away again: the roof it leaves under it is the shaft's own (no ordinary roof to take away)
            map.terrainGrid.SetTerrain(ElL(19, 13, 3, map), PZDefOf.PZ_OpenAir);
            elNote = $"roof left under it: {map.roofGrid.RoofAt(ElL(19, 13, 2, map))?.defName ?? "none"}";
            SeedTicks("el_co_open");
            TickUntil(600, () => elE.LinkedTop == 4, "el_co_open", 1);
        }

        private static void ElNetOpenCheck(Map map)
        {
            tickWatch = null;
            Result("CO the floor taken out of the shaft: it leaves the shaft's own roof (not an ordinary one), linked up to level 4 again (within a rare tick)",
                elE.LinkedTop == 4 && StairGrid.For(map).links.Count(l => l.thing == elE) == 10 && elNote.Contains("PZ_StairwellRoof"), $"{elNote}; {ElLinks(map)}");
            stage = "el_co_ride";
        }

        // A floor spawned in the shaft above the rider mid-ride: its link goes, it falls down the shaft.
        private static void ElNetRide(Map map)
        {
            ElFixture(map, "el_co_ride_go", true, 4, r2P);
            Heal(r2P);
            Teleport(r2P, ElG(15, 13));
            Draft(r2P, true);
        }

        private static void ElNetRideGo(Map map)
        {
            tickWatch = null;
            elPawns.Add(r2P);
            ElResetRides();
            ElMarkFalls();
            elFlag = false;
            elTicks = 0;
            GoTo(r2P, ElL(14, 13, 4, map));
            SeedTicks("el_co_ride");
            TickUntil(3000, () =>
            {
                ElWatch(map);
                LinkSteps.Step st = ElStep(map, r2P);
                if (!elFlag && st != null && st.link.IsElevator && st.height >= 2.5f)
                {
                    elFlag = true;
                    elHeightAt = st.height;
                    map.terrainGrid.SetTerrain(ElL(19, 13, 3, map), PZDefOf.PZ_UpperFloor);
                }
                if (elFlag)
                {
                    elTicks++;
                }
                return elFlag && elTicks > 60;
            }, "el_co_ride_check", 1);
        }

        private static void ElNetRideCheck(Map map)
        {
            tickWatch = null;
            int fell = LinkSteps.FallFloors - elFallFloorsAt;
            Result("CO safety net mid-ride: a floor spawned in the shaft over a rider at 2.5 floors: its link goes, it falls down the shaft to the ground stop (2 floors, bruised)",
                elFlag && r2P.Position == ElG(18, 13) && LinkSteps.Falls - elFallsAt == 1 && fell == 2 && HasBruise(r2P) && elE.LinkedTop == 2,
                $"height {elHeightAt:0.00}, at {r2P.Position}, falls {LinkSteps.Falls - elFallsAt}, floors {fell}, bruised {HasBruise(r2P)}; {ElLinks(map)}");
            map.terrainGrid.SetTerrain(ElL(19, 13, 3, map), PZDefOf.PZ_OpenAir);
            Heal(r2P);
            stage = "el_rf";
        }

        // ------------------------------------------------------------------------------------------------ RF: roof and cap

        private static void ElRoof(Map map)
        {
            ElFixture(map, "el_rf_go", true, 4, r2P);
            Heal(r2P);
            Teleport(r2P, ElG(15, 13));
            // a room on level 1 beside the shaft: interior (16..18, 12..14) with the stop (18, 13) and the shaft cell
            // (19, 13); walls round it, round the shaft too (floors laid beside the shaft for them)
            R2Floor(map, CellRect.SingleCell(ElG(19, 12)), 1);
            R2Floor(map, CellRect.SingleCell(ElG(19, 14)), 1);
            R2Floor(map, new CellRect(ElG(20, 12).x, ElG(20, 12).z, 1, 3), 1);
            foreach (IntVec3 g in new CellRect(ElG(15, 11).x, ElG(15, 11).z, 5, 5).EdgeCells)
            {
                if (g.x != ElG(19, 0).x)
                {
                    R2Wall(map, Levels.Cell(g, 1, map));
                }
            }
            R2Wall(map, ElL(19, 12, 1, map));
            R2Wall(map, ElL(19, 14, 1, map));
            R2Wall(map, ElL(20, 13, 1, map));
            // stairs up to level 3 (the floors above the cap are reached without the elevator), on ledges outside the stack
            // (nothing over their stairwells): ground -> 1 at (8, 10); a level-1 ledge x 5..9, z 14..18 with stairs on to
            // level 2 at (7, 16); a level-2 ledge x 10..18, z 3..7 with stairs on to level 3 at (14, 5)
            R2Floor(map, new CellRect(ElG(5, 14).x, ElG(5, 14).z, 5, 5), 1);
            R2Floor(map, new CellRect(ElG(10, 3).x, ElG(10, 3).z, 9, 5), 2);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            elStairs = R2Stairs(map, ElG(8, 10), Rot4.East);
            Building_Stairs s12 = R2Stairs(map, ElL(7, 16, 1, map), Rot4.East);
            Building_Stairs s23 = R2Stairs(map, ElL(14, 5, 2, map), Rot4.North);
            elNote = $"stairs {elStairs?.Position}, {s12?.Position}, {s23?.Position}";
            // a wall on level 2 holds the cap's floor (the slab support rule), wood for it on level 3
            R2Wall(map, ElL(17, 10, 2, map));
            Thing wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
            wood.stackCount = 20;
            GenPlace.TryPlaceThing(wood, ElL(16, 16, 3, map), map, ThingPlaceMode.Direct);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
        }

        private static void ElRoofGo(Map map)
        {
            tickWatch = null;
            bool allRoofed = true;
            string roofs = "";
            for (int L = 0; L <= 4; L++)
            {
                IntVec3 c = ElL(19, 13, L, map);
                RoofDef r = map.roofGrid.RoofAt(c);
                roofs += $"L{L} {r?.defName ?? "none"} open {Column.OpenAbove(c, map)}; ";
                allRoofed &= r == PZDefOf.PZ_StairwellRoof && Column.OpenAbove(c, map);
            }
            Room room = ElL(17, 13, 1, map).GetRoom(map);
            bool indoors = room != null && !room.PsychologicallyOutdoors && !room.UsesOutdoorTemperature && room.OpenRoofCount == 0 && !room.TouchesMapEdge;
            Result("RF every shaft cell (levels 0-4) has the invisible stairwell roof (no rain or snow in, the column stays open); a room beside it on level 1 is indoors and roofed",
                allRoofed && indoors, $"{roofs}room {room?.ID} cells {room?.CellCount}, outdoors {room?.PsychologicallyOutdoors}, outdoor temperature {room?.UsesOutdoorTemperature}, open roof {room?.OpenRoofCount}, edge {room?.TouchesMapEdge}");
            // the cap: a wood floor planned in the shaft on level 3 (allowed, with the cap message), laid by a colonist
            IntVec3 cap = ElL(19, 13, 3, map);
            int shown = ElevatorCap.Shown;
            AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_UpperFloor, cap, Rot4.North, map);
            if (ok.Accepted)
            {
                GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_UpperFloor, cap, map, Rot4.North, Faction.OfPlayer, null);
            }
            elNote += $"; cap placement {ok.Accepted} ({ok.Reason}), message shown {ElevatorCap.Shown > shown}";
            elFlag = ok.Accepted && ElevatorCap.Shown > shown;
            Draft(r2P, false);
            foreach (WorkTypeDef wt in DefDatabase<WorkTypeDef>.AllDefs)
            {
                if (!r2P.WorkTypeIsDisabled(wt))
                {
                    r2P.workSettings.SetPriority(wt, wt == WorkTypeDefOf.Construction || wt == WorkTypeDefOf.Hauling ? 1 : 0);
                }
            }
            SeedTicks("el_rf_cap");
            TickUntil(10000, () => ProjectZ.HasFloor(cap, map) && elE.LinkedTop == 2, "el_rf_capped", 10);
        }

        private static void ElCapped(Map map)
        {
            tickWatch = null;
            IntVec3 cap = ElL(19, 13, 3, map);
            List<VLink> ls = StairGrid.For(map).links.Where(l => l.thing == elE).ToList();
            bool roofsAbove = map.roofGrid.RoofAt(cap) == null && map.roofGrid.RoofAt(ElL(19, 13, 4, map)) == null;
            Result("RF a colonist lays a floor in the shaft on level 3 (placement allowed, the cap message shown): the elevator serves levels 0-2 only (3 links), no shaft roof above the cap",
                elFlag && ProjectZ.HasFloor(cap, map) && elE.LinkedTop == 2 && ls.Count == 3 && ls.All(l => Levels.LevelOf(l.top, map) <= 2) && roofsAbove,
                $"{elNote}; floor {ProjectZ.HasFloor(cap, map)}, {ElLinks(map)}, roof under the cap {map.roofGrid.RoofAt(ElL(19, 13, 2, map))?.defName}, over it {map.roofGrid.RoofAt(cap)?.defName ?? "none"}, inspect '{elE.GetInspectString().Replace("\n", " | ")}'");
            map.designationManager.AddDesignation(new Designation(cap, DesignationDefOf.RemoveFoundation));
            SeedTicks("el_rf_uncap");
            TickUntil(10000, () => !ProjectZ.HasFloor(cap, map) && elE.LinkedTop == 4, "el_rf_uncapped", 10);
        }

        private static void ElUncapped(Map map)
        {
            tickWatch = null;
            IntVec3 cap = ElL(19, 13, 3, map);
            Result("RF the colonist takes the cap up again: no ordinary roof left under it (the shaft's own), the elevator serves 0-4 again (10 links), the shaft roofed up to the top",
                !ProjectZ.HasFloor(cap, map) && elE.LinkedTop == 4 && StairGrid.For(map).links.Count(l => l.thing == elE) == 10 && map.roofGrid.RoofAt(ElL(19, 13, 2, map)) == PZDefOf.PZ_StairwellRoof && map.roofGrid.RoofAt(ElL(19, 13, 4, map)) == PZDefOf.PZ_StairwellRoof,
                $"floor {ProjectZ.HasFloor(cap, map)}, {ElLinks(map)}, roofs L2 {map.roofGrid.RoofAt(ElL(19, 13, 2, map))?.defName}, L3 {map.roofGrid.RoofAt(cap)?.defName}, L4 {map.roofGrid.RoofAt(ElL(19, 13, 4, map))?.defName}, worker at {r2P.Position}");
            foreach (WorkTypeDef wt in DefDatabase<WorkTypeDef>.AllDefs)
            {
                if (!r2P.WorkTypeIsDisabled(wt))
                {
                    r2P.workSettings.SetPriority(wt, 3);
                }
            }
            Draft(r2P, true);
            stage = "el_pu";
        }

        // ------------------------------------------------------------------------------------------------ PU: under a floor

        // Level 1 platform over x 10..20 (the shaft cell included), stairs up to it; the elevator planned at (19, 13)
        // under it, materials on the ground.
        private static void ElUnder(Map map)
        {
            R2Clear(map);
            R2Park(map, r2P, r2Q);
            R2Feed(map);
            LinkSteps.For(map)?.steps.Clear();
            R2Floor(map, new CellRect(ElG(10, 8).x, ElG(10, 8).z, 10, 12), 1);
            ResearchProjectDef machining = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("Machining");
            if (machining != null && !machining.IsFinished)
            {
                Find.ResearchManager.FinishProject(machining);
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            elStairs = R2Stairs(map, ElG(8, 10), Rot4.East);
            ElNewBattery(map);
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 120;
            GenPlace.TryPlaceThing(steel, ElG(22, 10), map, ThingPlaceMode.Near);
            Thing comp = ThingMaker.MakeThing(ThingDefOf.ComponentIndustrial);
            comp.stackCount = 5;
            GenPlace.TryPlaceThing(comp, ElG(22, 12), map, ThingPlaceMode.Near);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Teleport(r2P, ElG(22, 14));
            Teleport(r2Q, ElG(23, 14));
            SeedTicks("el_pu_wait");
            StartTicks(5, "el_pu_go");
        }

        private static void ElUnderGo(Map map)
        {
            IntVec3 up = ElL(19, 13, 1, map);
            AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(ElevatorDefOf.PZ_Elevator, ElG(19, 13), Rot4.West, map);
            Blueprint bp = ok.Accepted ? GenConstruct.PlaceBlueprintForBuild(ElevatorDefOf.PZ_Elevator, ElG(19, 13), map, Rot4.West, Faction.OfPlayer, null) : null;
            bool marked = map.designationManager.DesignationAt(up, DesignationDefOf.RemoveFoundation) != null;
            bool waiting = bp != null && ElevatorSite.Waiting(bp) && !GenConstruct.CanConstruct(bp, r2P, false);
            elNote = $"placement {ok.Accepted} ({ok.Reason}), floor over it marked {marked}, waiting {waiting}, inspect '{bp?.GetInspectString().Replace("\n", " | ")}'";
            elFlag = ok.Accepted && marked && waiting;
            foreach (Pawn p in new[] { r2P, r2Q })
            {
                Draft(p, false);
                foreach (WorkTypeDef wt in DefDatabase<WorkTypeDef>.AllDefs)
                {
                    if (!p.WorkTypeIsDisabled(wt))
                    {
                        p.workSettings.SetPriority(wt, wt == WorkTypeDefOf.Construction || wt == WorkTypeDefOf.Hauling ? 1 : 0);
                    }
                }
            }
            elE = null;
            SeedTicks("el_pu");
            TickUntil(30000, () =>
            {
                elE = map.listerThings.ThingsOfDef(ElevatorDefOf.PZ_Elevator).OfType<Building_Elevator>().FirstOrDefault();
                return elE != null;
            }, "el_pu_check", 30);
        }

        private static void ElUnderCheck(Map map)
        {
            tickWatch = null;
            IntVec3 up = ElL(19, 13, 1, map);
            if (elE == null)
            {
                Thing site = ElG(19, 13).GetThingList(map).FirstOrDefault(x => ElevatorSite.Is(x));
                string s = site == null ? "no site" : $"{site.def.defName}, waiting {ElevatorSite.Waiting(site)}";
                foreach (Pawn p in new[] { r2P, r2Q })
                {
                    bool can = site != null && GenConstruct.CanConstruct(site, p, false);
                    s += $"; {p.LabelShort} at {p.Position} job {p.CurJobDef?.defName} drafted {p.Drafted}, can construct {can} ({JobFailReason.Reason})";
                    foreach (WorkGiverDef wg in DefDatabase<WorkGiverDef>.AllDefs.Where(w => w.Worker is WorkGiver_Scanner && (w.workType == WorkTypeDefOf.Construction || w.workType == WorkTypeDefOf.Hauling)))
                    {
                        Job j = null;
                        try { j = site != null && ((WorkGiver_Scanner)wg.Worker).HasJobOnThing(p, site) ? ((WorkGiver_Scanner)wg.Worker).JobOnThing(p, site) : null; } catch (Exception e) { s += " ex " + e.Message; }
                        if (j != null) s += $", {wg.defName} -> {j.def.defName}";
                    }
                }
                s += "; things on the cell: " + string.Join(", ", ElG(19, 13).GetThingList(map).Select(x => x.def.defName)) + "; steel left " + map.listerThings.ThingsOfDef(ThingDefOf.Steel).Sum(x => x.stackCount);
                Info("PU diag: " + s);
            }
            Result("PU placed under a floor: allowed, the floor in the shaft marked for removal, the elevator waits for it; colonists take it up and build the elevator, which links up to level 4 with its shaft roofed",
                elFlag && elE != null && !ProjectZ.HasFloor(up, map) && elE.LinkedTop == 4 && map.roofGrid.RoofAt(ElG(19, 13)) == PZDefOf.PZ_StairwellRoof && map.roofGrid.RoofAt(up) == PZDefOf.PZ_StairwellRoof,
                $"{elNote}; built {elE != null} at tick {Find.TickManager.TicksGame}, floor left {ProjectZ.HasFloor(up, map)}, {ElLinks(map)}, roof over it {map.roofGrid.RoofAt(ElG(19, 13))?.defName ?? "none"}, L1 {map.roofGrid.RoofAt(up)?.defName ?? "none"}");
            foreach (Pawn p in new[] { r2P, r2Q })
            {
                foreach (WorkTypeDef wt in DefDatabase<WorkTypeDef>.AllDefs)
                {
                    if (!p.WorkTypeIsDisabled(wt))
                    {
                        p.workSettings.SetPriority(wt, 3);
                    }
                }
                Draft(p, true);
            }
            stage = "el_k2";
        }

        // ------------------------------------------------------------------------------------------------ K2: last way

        // A: level 1 at x 4..12, z 8..12 (stairs up from the ground), stairs on to C: level 2 at x 4..24, z 12..19 (with a
        // hole for the shaft at (20, 16)); B: a level 1 island at x 20..24, z 14..18 holding the elevator at (20, 16)
        // turned east (stops (21, 16) on levels 1-4). B is reached only by the elevator from C.
        private static void ElK2(Map map)
        {
            R2Clear(map);
            R2Park(map, r2P, r2Q);
            R2Feed(map);
            LinkSteps.For(map)?.steps.Clear();
            R2Floor(map, new CellRect(ElG(4, 8).x, ElG(4, 8).z, 9, 5), 1);
            R2Floor(map, new CellRect(ElG(20, 14).x, ElG(20, 14).z, 5, 5), 1);
            R2Floor(map, new CellRect(ElG(4, 12).x, ElG(4, 12).z, 21, 8), 2);
            IntVec3 hole = ElL(20, 16, 2, map);
            map.terrainGrid.SetTerrain(hole, PZDefOf.PZ_OpenAir);
            map.roofGrid.SetRoof(ElL(20, 16, 1, map), null);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            elStairs = R2Stairs(map, ElG(2, 10), Rot4.East);
            Building_Stairs s2 = R2Stairs(map, ElL(8, 10, 1, map), Rot4.North);
            elE = Place(ElevatorDefOf.PZ_Elevator, null, ElL(20, 16, 1, map), map, Rot4.East) as Building_Elevator;
            elBattery = Place(ThingDefOf.Battery, null, ElL(23, 15, 1, map), map, Rot4.North);
            elBattery.TryGetComp<CompPowerBattery>()?.AddEnergy(10000f);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Teleport(r2Q, ElL(22, 17, 1, map));
            elNote = $"stairs {elStairs?.Position} and {s2?.Position}, elevator {elE?.Position}";
            SeedTicks("el_k2_power");
            TickUntil(400, () => elE != null && elE.LinkedPowered, "el_k2_go", 1);
        }

        private static void ElK2Go(Map map)
        {
            tickWatch = null;
            bool cReach = r2Q.CanReach(ElL(10, 15, 2, map), PathEndMode.OnCell, Danger.Deadly);
            bool cuts = elE.CutsOffItsFloor();
            new Designator_Deconstruct().DesignateThing(elE);
            Draft(r2Q, false);
            foreach (WorkTypeDef wt in DefDatabase<WorkTypeDef>.AllDefs)
            {
                if (!r2Q.WorkTypeIsDisabled(wt))
                {
                    r2Q.workSettings.SetPriority(wt, wt == WorkTypeDefOf.Construction ? 1 : 0);
                }
            }
            bool job = new WorkGiver_Deconstruct { def = DefDatabase<WorkGiverDef>.AllDefs.FirstOrDefault(d => d.giverClass == typeof(WorkGiver_Deconstruct)) }.HasJobOnThing(r2Q, elE);
            elNote += $"; island reaches level 2 by it {cReach}, cuts off its floor {cuts}, deconstruct job {job} ({JobFailReason.Reason})";
            elFlag = cReach && cuts && !job;
            SeedTicks("el_k2");
            StartTicks(2500, "el_k2_check");
        }

        private static void ElK2Check(Map map)
        {
            Result("K2 the last way out: an elevator on an island it alone joins to the rest is not taken down from the island (the worker would be stranded); it stands after 2500 ticks with the designation on",
                elFlag && elE.Spawned && map.designationManager.DesignationOn(elE, DesignationDefOf.Deconstruct) != null,
                $"{elNote}; spawned {elE.Spawned}, worker {r2Q} at {r2Q.Position} job {r2Q.CurJobDef?.defName}");
            map.designationManager.DesignationOn(elE, DesignationDefOf.Deconstruct)?.Delete();
            foreach (WorkTypeDef wt in DefDatabase<WorkTypeDef>.AllDefs)
            {
                if (!r2Q.WorkTypeIsDisabled(wt))
                {
                    r2Q.workSettings.SetPriority(wt, 3);
                }
            }
            Draft(r2Q, true);
            stage = "el_xb";
        }

        // ------------------------------------------------------------------------------------------------ XB: exit blocked

        private static void ElExitBlocked(Map map)
        {
            ElFixture(map, "el_xb_go", true, 4, r2P);
            Teleport(r2P, ElG(15, 13));
            Draft(r2P, true);
            R2Wall(map, ElL(18, 13, 2, map));
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
        }

        private static void ElExitBlockedGo(Map map)
        {
            tickWatch = null;
            bool reach2 = r2P.CanReach(ElL(14, 13, 2, map), PathEndMode.OnCell, Danger.Deadly);
            elNote = $"level 2 reachable {reach2}, inspect '{elE.GetInspectString().Replace("\n", " | ")}'";
            elPawns.Add(r2P);
            ElResetRides();
            elDest = ElL(14, 13, 3, map);
            GoTo(r2P, elDest);
            SeedTicks("el_xb");
            TickUntil(3000, () =>
            {
                ElWatch(map);
                return r2P.Position == elDest;
            }, "el_xb_check", 1);
            elFlag = reach2;
        }

        private static void ElExitBlockedCheck(Map map)
        {
            tickWatch = null;
            List<ElRide> rides = RidesOf(r2P);
            Result("XB a stop whose exit is walled is skipped: one ride from the ground straight to level 3, level 2 unreachable",
                r2P.Position == elDest && rides.Count == 1 && rides[0].link.span == 3 && rides[0].arrived && !elFlag,
                $"{string.Join("; ", rides.Select(RideStr))}; {elNote}");
            stage = "el_s";
        }

        // ------------------------------------------------------------------------------------------------ S: shots

        private static void ElShots(Map map)
        {
            ElFixture(map, "el_s_go", true, true, 4, r2P);
            Teleport(r2P, ElG(22, 13));
            Draft(r2P, true);
        }

        private static void ElShotsGo(Map map)
        {
            tickWatch = null;
            elDest = ElL(24, 13, 2, map);
            GoTo(r2P, elDest);
            SeedTicks("el_s");
            TickUntil(2000, () => Links.Kind(r2P) == StepKind.ElevatorUp && r2P.pather.MovePercentage >= 0.06f, "el_s0", 1);
        }

        private static void ElShot(Map map, string name, int floor, string next)
        {
            tickWatch = null;
            Info($"{name}: {r2P} at {r2P.Position} -> {r2P.pather.nextCell} {r2P.pather.MovePercentage:P0}, kind {Links.Kind(r2P)}, rise {Links.Rise(r2P):0.00}, draw pos {r2P.DrawPos}");
            ShotAt(map, floor, ElG(19, 13), 6f);
            Wait(3, next);
        }

        // ------------------------------------------------------------------------------------------------ SV: save

        private static void ElSave(Map map)
        {
            ElFixture(map, "el_sv_go", true, 4, r2P);
            Teleport(r2P, ElG(16, 13));
            Draft(r2P, true);
        }

        private static void ElSaveGo(Map map)
        {
            tickWatch = null;
            elDest = ElL(14, 13, 2, map);
            GoTo(r2P, elDest);
            SeedTicks("el_sv");
            TickUntil(2000, () => Links.Kind(r2P) == StepKind.ElevatorUp && r2P.pather.MovePercentage >= 0.5f, "el_sv_save", 1);
        }

        private static void ElSaveNow(Map map)
        {
            tickWatch = null;
            elPId = r2P.ThingID;
            elQId = r2Q?.ThingID;
            Info($"saving with {r2P} mid-ride at {r2P.pather.MovePercentage:P0}");
            climbsAtSave = PZStats.climbResets;
            GameDataSaveLoader.SaveGame("pz_elevator");
            Result("SV saved pz_elevator mid-ride", File.Exists(GenFilePaths.FilePathForSavedGame("pz_elevator")), "");
            stage = "el_sv_loaded";
            pendingLoad = "pz_elevator";
        }

        private static void ElLoaded(Map map)
        {
            r2P = ById(map, elPId);
            r2Q = ById(map, elQId);
            elE = map.listerThings.ThingsOfDef(ElevatorDefOf.PZ_Elevator).OfType<Building_Elevator>().FirstOrDefault();
            elBattery = map.listerThings.ThingsOfDef(ThingDefOf.Battery).FirstOrDefault();
            bool reset = r2P != null && PZStats.climbResets > climbsAtSave;
            Result("SV after loading: the elevator and its links are back (powered as saved), the rider starts its ride again",
                r2P != null && elE != null && StairGrid.For(map)?.links.Count(l => l.thing == elE) == 10 && elE.LinkedPowered && reset && (r2P.Position == ElG(18, 13) || Levels.LevelOf(r2P.Position, map) == 2),
                $"rider {r2P} at {r2P?.Position}, next {r2P?.pather.nextCell}, resets {PZStats.climbResets - climbsAtSave}, {ElLinks(map)}, job {r2P?.CurJobDef?.defName}");
            if (r2P != null && r2P.CurJobDef != JobDefOf.Goto)
            {
                GoTo(r2P, elDest);
            }
            elPawns.Clear();
            if (r2P != null)
            {
                elPawns.Add(r2P);
            }
            ElResetRides();
            SeedTicks("el_sv_after");
            TickUntil(2000, () =>
            {
                ElWatch(map);
                return r2P == null || r2P.Position == elDest;
            }, "el_sv_check", 1);
        }

        private static void ElLoadedCheck(Map map)
        {
            tickWatch = null;
            Result("SV the reloaded rider gets to level 2", r2P != null && r2P.Position == elDest, $"at {r2P?.Position}; rides {string.Join("; ", RidesOf(r2P).Select(RideStr))}; {ElLinks(map)}");
            stage = "el_b";
        }

        // ------------------------------------------------------------------------------------------------ B / D: gone mid-ride

        private static void ElBreak(Map map)
        {
            ElFixture(map, "el_b_go", true, 4, r2P);
            Heal(r2P);
            Teleport(r2P, ElG(15, 13));
            Draft(r2P, true);
        }

        private static void ElBreakGo(Map map)
        {
            tickWatch = null;
            elPawns.Add(r2P);
            ElResetRides();
            ElMarkFalls();
            elFlag = false;
            elTicks = 0;
            GoTo(r2P, ElL(14, 13, 4, map));
            SeedTicks("el_b");
            TickUntil(3000, () =>
            {
                ElWatch(map);
                LinkSteps.Step st = ElStep(map, r2P);
                if (!elFlag && st != null && st.link.IsElevator && st.height >= 3.2f)
                {
                    elFlag = true;
                    elHeightAt = st.height;
                    elE.Destroy(DestroyMode.KillFinalize);
                }
                if (elFlag)
                {
                    elTicks++;
                }
                return elFlag && elTicks > 60;
            }, "el_b_check", 1);
        }

        private static void ElBreakCheck(Map map)
        {
            tickWatch = null;
            int fell = LinkSteps.FallFloors - elFallFloorsAt;
            Result("B the elevator destroyed mid-ride at 3.2 floors: the rider falls down the shaft to the ground stop (3 floors, bruised), no links left",
                elFlag && r2P.Position == ElG(18, 13) && LinkSteps.Falls - elFallsAt == 1 && fell == 3 && HasBruise(r2P) && StairGrid.For(map)?.links.Count(l => l.thing == elE) is null or 0,
                $"height {elHeightAt:0.00}, at {r2P.Position}, falls {LinkSteps.Falls - elFallsAt}, floors {fell}, bruised {HasBruise(r2P)}, destroyed {elE.Destroyed}");
            Heal(r2P);
            stage = "el_d";
        }

        // A colonist deconstructs the elevator while another shuttles between the ground and level 4.
        private static void ElDecon(Map map)
        {
            ElFixture(map, "el_d_go", true, 4, r2P, r2Q);
            Heal(r2P);
            Heal(r2Q);
            Teleport(r2P, ElG(18, 13));
            Teleport(r2Q, ElG(20, 14));
            Draft(r2P, true);
            Draft(r2Q, false);
            foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefs)
            {
                if (!r2Q.WorkTypeIsDisabled(w))
                {
                    r2Q.workSettings.SetPriority(w, w == WorkTypeDefOf.Construction ? 1 : 0);
                }
            }
        }

        private static void ElDeconGo(Map map)
        {
            tickWatch = null;
            elPawns.Add(r2P);
            ElResetRides();
            ElMarkFalls();
            elFlag = elFlag2 = false;
            elHeightAt = -1f;
            new Designator_Deconstruct().DesignateThing(elE);
            IntVec3 g0 = ElG(18, 13), g4 = ElL(18, 13, 4, map);
            GoTo(r2P, g4);
            SeedTicks("el_d");
            TickUntil(20000, () =>
            {
                ElWatch(map);
                if (!elFlag && !elE.Spawned)
                {
                    elFlag = true;
                    elFlag2 = LinkSteps.Falls > elFallsAt;
                    elTicks = 0;
                }
                if (!elFlag && Links.Kind(r2P) != StepKind.None)
                {
                    elHeightAt = ElStep(map, r2P)?.height ?? -1f;
                }
                if (!elFlag && Links.Kind(r2P) == StepKind.None && (r2P.Position == g4 || r2P.Position == g0) && !r2P.pather.Moving)
                {
                    GoTo(r2P, r2P.Position == g4 ? g0 : g4);
                }
                if (elFlag)
                {
                    elTicks++;
                }
                return elFlag && elTicks > 30;
            }, "el_d_check", 1);
        }

        private static void ElDeconCheck(Map map)
        {
            tickWatch = null;
            int fell = LinkSteps.FallFloors - elFallFloorsAt;
            int steel = map.listerThings.ThingsOfDef(ThingDefOf.Steel).Where(t => t.Spawned && r2Area.ExpandedBy(3).Contains(Levels.Ground(t.Position, map))).Sum(t => t.stackCount);
            Result("D a colonist deconstructs the elevator while another shuttles in it: the rider in the shaft falls down to the ground stop; materials come back",
                elFlag && elFlag2 && r2P.Position == ElG(18, 13) && Math.Abs(fell - Mathf.FloorToInt(elHeightAt + 0.001f)) <= 1 && steel > 0,
                $"deconstructed {elE.Destroyed}, rider was at {elHeightAt:0.00} floors, fell {LinkSteps.Falls - elFallsAt} time(s), {fell} floor(s), at {r2P.Position}, bruised {HasBruise(r2P)}, steel back {steel}, worker {r2Q} job {r2Q.CurJobDef?.defName}");
            foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefs)
            {
                if (!r2Q.WorkTypeIsDisabled(w))
                {
                    r2Q.workSettings.SetPriority(w, 3);
                }
            }
            Draft(r2Q, true);
            Heal(r2P);
            stage = "el_rd";
        }

        // ------------------------------------------------------------------------------------------------ RD: raids

        private static readonly List<Pawn> elRaiders = new List<Pawn>();

        // Level 2 holds a colonist (the raid's target); the elevator on the ground beside the platform; withStairs: stairs
        // up to level 1 at the far side and a slab ramp up... (level 1 to 2 joined by stairs too).
        private static void ElRaid(Map map, bool withStairs)
        {
            ElFixture(map, withStairs ? "el_rd_go" : "el_rd2_go", true, 2, r2Q);
            Heal(r2Q);
            elStairs = null;
            if (withStairs)
            {
                // ground -> level 1 at the platform's south-west, level 1 -> 2 on the platform (exit on its north part)
                elStairs = R2Stairs(map, ElG(8, 10), Rot4.East);
                foreach (IntVec3 g in new CellRect(ElG(11, 15).x, ElG(11, 15).z, 1, 3))
                {
                    IntVec3 c2 = Levels.Cell(g, 2, map);
                    map.terrainGrid.SetTerrain(c2, PZDefOf.PZ_OpenAir);
                    map.roofGrid.SetRoof(Levels.Cell(g, 1, map), null);
                }
                R2Stairs(map, Levels.Cell(ElG(11, 16), 1, map), Rot4.North);
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            R2Hide(map, r2Q);
            Teleport(r2Q, ElL(13, 13, 2, map));
            Draft(r2Q, true);
        }

        private static void ElRaidGo(Map map, bool withStairs)
        {
            tickWatch = null;
            elRaiders.Clear();
            List<Pawn> before = map.mapPawns.AllPawnsSpawned.ToList();
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
            parms.faction = LdRaiders();
            parms.raidStrategy = DefDatabase<RaidStrategyDef>.GetNamedSilentFail("ImmediateAttack");
            parms.raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn;
            parms.spawnCenter = CellFinder.StandableCellNear(new IntVec3(r2Area.minX - 15, 0, r2Area.CenterCell.z), map, 8f);
            parms.points = 120f;
            parms.forced = true;
            bool fired = IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);
            elRaiders.AddRange(map.mapPawns.AllPawnsSpawned.Except(before).Where(p => p.Faction == parms.faction));
            elPawns.Clear();
            elPawns.AddRange(elRaiders);
            ElResetRides();
            elPlacedAt = LadderSite.Placed;
            elStairSteps.Clear();
            bool reach = elRaiders.Count > 0 && elRaiders[0].CanReach(ElL(13, 13, 2, map), PathEndMode.OnCell, Danger.Deadly);
            elNote = $"raid fired {fired}, {elRaiders.Count} raider(s) at {parms.spawnCenter}, first can reach level 2 {reach}, stairs {elStairs?.Position}";
            SeedTicks(withStairs ? "el_rd" : "el_rd2");
            TickUntil(12000, () =>
            {
                ElWatch(map);
                bool up = elRaiders.Any(p => p.Spawned && Levels.LevelOf(p.Position, map) >= 1);
                return withStairs ? elRaiders.Any(p => elStairSteps.ContainsKey(p)) && up : LadderSite.Placed > elPlacedAt && up;
            }, withStairs ? "el_rd_check" : "el_rd2_check", 1);
        }

        private static void ElRaidCheck(Map map, bool withStairs)
        {
            tickWatch = null;
            int rides = elRides.Count;
            int stairs = elStairSteps.Values.Sum();
            int ladders = LadderSite.Placed - elPlacedAt;
            int upNow = elRaiders.Count(p => p.Spawned && Levels.LevelOf(p.Position, map) >= 1);
            if (withStairs)
            {
                Result("RD a raid on a platform reachable by stairs and the elevator: the raiders take the stairs, never the elevator",
                    elRaiders.Count > 0 && rides == 0 && stairs > 0 && upNow > 0, $"{elNote}; elevator rides {rides}, stair steps {stairs}, ladders {ladders}, raiders up {upNow}, elevator spawned {elE.Spawned}");
            }
            else
            {
                Result("RD a raid on a platform reachable only by the elevator: the raiders place siege ladders and climb, never ride the elevator",
                    elRaiders.Count > 0 && rides == 0 && ladders > 0 && upNow > 0, $"{elNote}; elevator rides {rides}, ladders {ladders}, raiders up {upNow}, elevator spawned {elE.Spawned} hp {elE.HitPoints}");
            }
            foreach (Pawn p in elRaiders)
            {
                if (!p.Destroyed)
                {
                    p.Destroy();
                }
            }
            foreach (Lord l in map.lordManager.lords.Where(l => l.faction == LdRaiders()).ToList())
            {
                map.lordManager.RemoveLord(l);
            }
            elRaiders.Clear();
            R2Unhide(map);
            if (r2Q != null && !r2Q.Dead)
            {
                Heal(r2Q);
            }
            if (withStairs)
            {
                stage = "el_rd2";
                return;
            }
            Finish();
        }
    }
}
