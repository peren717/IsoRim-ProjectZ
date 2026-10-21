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
    // Mode ladders (isorim-pz-siege-ladders-design.md, stage 1: the generic vertical link and the temporary ladder, no raid
    // AI). Run with -projectzfloors=5 -projectzseed=7. Every check builds its own fixture in one cleared area (all levels
    // wiped, router2's R2Clear), under its own random state, and runs real orders and jobs with real ticks:
    //   C   climbs of 1, 2 and 4 floors, up and down: 180 ticks per floor (a Goto with a short expiry on the 4-floor climb:
    //       the climb is not cut short; a job override asked mid-climb is held off)
    //   Q   two pawns ordered up one ladder: one on it at a time, the second waits at the foot
    //   CA  a downed pawn carried up: no slower
    //   B   the ladder destroyed mid-climb: up at 2.5 floors (falls 2, bruised), down at about 1.3 floors (falls 1, stagger)
    //   DN  downed mid-climb (falls 2, bruised); DD a raider killed mid-descent (the corpse comes down)
    //   P   the place-ladder job: a colonist (player ladder), a raider (raider ladder, progress bar shown), a mechanoid (lift)
    //   CL  claim refused while a raider is on the map, allowed after; deconstruct refused during the raid; the countdown
    //       paused during the raid, running after it and after the claim
    //   DC  deconstructing a ladder (5 cloth) and a lift (1 component) by a colonist
    //   H   a colonist hauls steel up a ladder to a stockpile; the fogged platform is revealed (ReachFog) over the ladder
    //   K   the last way down: a ladder taken up is worked from the side that stays joined
    //   S   screenshots of a climb (3D), up and down
    //   SV  saved and loaded mid-climb: the climb starts again and ends on top
    //   E   the player's emergency ladder: the blueprint on the top cell, built from the top by a stuck colonist (who then
    //       climbs down) and from the foot, counting down from completion (no raid gate), a raider climbs it, deconstructing
    //       it gives nothing
    //   CD  the countdown ends: the raiders' ladder falls apart one day after the raid (60000 ticks); ECD the emergency
    //       ladder disappears one day after it was built
    public static partial class TestRunner
    {
        private static readonly int[] ldSpans = { 1, 2, 4 };
        private static int ldIdx;
        private static Building_SiegeLadder ldLadder;
        private static IntVec3 ldFoot, ldTop, ldDest;
        private static int ldN;
        private static int ldT0, ldT1, ldT0b, ldT1b, ldMaxHolders, ldWaitsAt, ldFallsAt, ldFallFloorsAt, ldHurtAt, ldStaggerAt, ldTicks, ldClimbsAt;
        private static float ldHeightAt;
        private static bool ldFlag, ldFlag2, ldOverrideHeld;
        private static Pawn ldR;
        private static string ldPId, ldQId;
        private static bool ldMoteSeen;
        private static int ldCountdownAt;

        private static IntVec3 LdG(int x, int z) => RG(x, z);

        private static IntVec3 LdL(int x, int z, int L, Map map) => RL(x, z, L, map);

        private static bool StepLadders(Map map)
        {
            Rand.PushState((int)(StableHash("ladders_" + stage) ^ (uint)(SeedArg ?? 717)));
            try
            {
                return StepLaddersInner(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static bool StepLaddersInner(Map map)
        {
            switch (stage)
            {
                case "ld_setup":
                    LdSetup(map);
                    return true;
                case "ld_c":
                    LdClimb(map, true);
                    return true;
                case "ld_c_up":
                    LdClimbCheck(map, true);
                    return true;
                case "ld_c_down":
                    LdClimbCheck(map, false);
                    return true;
                case "ld_q":
                    LdQueue(map);
                    return true;
                case "ld_q_check":
                    LdQueueCheck(map);
                    return true;
                case "ld_ca":
                    LdCarry(map);
                    return true;
                case "ld_ca_go":
                    LdCarryGo(map);
                    return true;
                case "ld_ca_check":
                    LdCarryCheck(map);
                    return true;
                case "ld_b":
                    LdBreak(map, true);
                    return true;
                case "ld_b_check":
                    LdBreakCheck(map, true);
                    return true;
                case "ld_b2":
                    LdBreak(map, false);
                    return true;
                case "ld_b2_check":
                    LdBreakCheck(map, false);
                    return true;
                case "ld_dn":
                    LdDowned(map);
                    return true;
                case "ld_dn_check":
                    LdDownedCheck(map);
                    return true;
                case "ld_dd":
                    LdDead(map);
                    return true;
                case "ld_dd_check":
                    LdDeadCheck(map);
                    return true;
                case "ld_p":
                    LdPlace(map, 0);
                    return true;
                case "ld_p_check":
                    LdPlaceCheck(map, 0);
                    return true;
                case "ld_p2":
                    LdPlace(map, 1);
                    return true;
                case "ld_p2_check":
                    LdPlaceCheck(map, 1);
                    return true;
                case "ld_p3":
                    LdPlace(map, 2);
                    return true;
                case "ld_p3_check":
                    LdPlaceCheck(map, 2);
                    return true;
                case "ld_cl":
                    LdClaim(map);
                    return true;
                case "ld_cl_raid":
                    LdClaimRaid(map);
                    return true;
                case "ld_cl_after":
                    LdClaimAfter(map);
                    return true;
                case "ld_cl_claimed":
                    LdClaimClaimed(map);
                    return true;
                case "ld_dc":
                    LdDecon(map, false);
                    return true;
                case "ld_dc_check":
                    LdDeconCheck(map, false);
                    return true;
                case "ld_dc2":
                    LdDecon(map, true);
                    return true;
                case "ld_dc2_check":
                    LdDeconCheck(map, true);
                    return true;
                case "ld_h":
                    LdHaul(map);
                    return true;
                case "ld_h_fog":
                    LdHaulFog(map);
                    return true;
                case "ld_h_check":
                    LdHaulCheck(map);
                    return true;
                case "ld_k":
                    LdK2(map);
                    return true;
                case "ld_k_check":
                    LdK2Check(map);
                    return true;
                case "ld_s":
                    LdShots(map);
                    return true;
                case "ld_s1":
                    LdShot(map, "ladder_climb_up_40", "ld_s1_take");
                    return true;
                case "ld_s1_take":
                    Capture("ladder_climb_up_40");
                    TickUntil(2000, () => LdPct(map) >= 0.75f, "ld_s2", 1);
                    return true;
                case "ld_s2":
                    LdShot(map, "ladder_climb_up_75", "ld_s2_take");
                    return true;
                case "ld_s2_take":
                    Capture("ladder_climb_up_75");
                    TickUntil(2000, () => r2P.Position == ldDest, "ld_s3", 5);
                    return true;
                case "ld_s3":
                    tickWatch = null;
                    ldDest = LdG(15, 13);
                    GoTo(r2P, ldDest);
                    TickUntil(2000, () => LdPct(map) >= 0.5f && Links.Kind(r2P) == StepKind.LadderDown, "ld_s4", 1);
                    return true;
                case "ld_s4":
                    LdShot(map, "ladder_climb_down_50", "ld_s4_take");
                    return true;
                case "ld_s4_take":
                    Capture("ladder_climb_down_50");
                    TickUntil(2000, () => r2P.Position == ldDest, "ld_sv", 5);
                    return true;
                case "ld_sv":
                    LdSave(map);
                    return true;
                case "ld_sv_save":
                    LdSaveNow(map);
                    return true;
                case "ld_sv_loaded":
                    LdLoaded(map);
                    return true;
                case "ld_sv_check":
                    LdLoadedCheck(map);
                    return true;
                case "ld_cd":
                    LdCountdown(map);
                    return true;
                case "ld_cd_check":
                    LdCountdownCheck(map);
                    return true;
                case "ld_e1":
                    LdEmergency(map, true, "ld_e1_check");
                    return true;
                case "ld_e1_check":
                    LdEmergencyCheck(map, true);
                    return true;
                case "ld_e1_go":
                    LdEmergencyGone(map, true);
                    return true;
                case "ld_e2":
                    LdEmergency(map, false, "ld_e2_check");
                    return true;
                case "ld_e2_check":
                    LdEmergencyCheck(map, false);
                    return true;
                case "ld_e2_go":
                    LdEmergencyGone(map, false);
                    return true;
                case "ld_e_cd":
                    LdEmergencyCountdown(map);
                    return true;
                case "ld_e_raider":
                    LdEmergencyRaider(map);
                    return true;
                case "ld_e_dc":
                    LdEmergencyDecon(map);
                    return true;
                case "ld_ecd":
                    LdEmergencyEnd(map);
                    return true;
                case "ld_ecd_built":
                    LdEmergencyEndBuilt(map);
                    return true;
                case "ld_ecd_check":
                    LdEmergencyEndCheck(map);
                    return true;
            }
            return StepLadders2Inner(map);
        }

        // ------------------------------------------------------------------------------------------------ fixtures

        private static void LdSetup(Map map)
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
                Result("ladders: a map with 5 levels", false, $"levels {Levels.Min(map)}..{Levels.Max(map)} (run with -projectzfloors=5)");
                Finish();
                return;
            }
            r2Area = new CellRect(map.Center.x - 22, map.Center.z - 16, 44, 32);
            List<Pawn> cols = Colonists(map);
            r2P = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)) ?? cols.First();
            r2Q = cols.FirstOrDefault(p => p != r2P);
            foreach (Pawn p in cols)
            {
                p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
            }
            R2Park(map);
            Info($"ladders: area {r2Area}, walker {r2P}, second {r2Q}, levels {Levels.Min(map)}..{Levels.Max(map)}");
            ldIdx = 0;
            stage = "ld_c";
        }

        // A clear area, a platform on level n (ground x 20..29, z 8..19), its west edge cell (20, 13) the top; the column at
        // (19, 13) runs down to the foot on the ground. spawn: a ladder of 'faction' (null: none spawned).
        private static void LdFixture(Map map, int n, Faction faction, bool lift = false, params Pawn[] keep)
        {
            R2Clear(map);
            R2Park(map, keep);
            R2Feed(map);
            LinkSteps.For(map)?.steps.Clear();
            ldN = n;
            R2Floor(map, new CellRect(LdG(20, 8).x, LdG(20, 8).z, 10, 12), n);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            ldTop = LdL(20, 13, n, map);
            ldFoot = LdG(19, 13);
            ldLadder = null;
            if (faction != null)
            {
                ldLadder = LadderSite.Spawn(ldFoot, ldTop, faction, map, lift);
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            }
        }

        private static Faction LdRaiders()
        {
            return Find.FactionManager.AllFactions.Where(x => x.HostileTo(Faction.OfPlayer) && x.def.humanlikeFaction && !x.def.hidden && !x.temporary && x.def.pawnGroupMakers != null).OrderBy(x => x.loadID).First();
        }

        private static float LdPct(Map map) => Links.Kind(r2P) == StepKind.None ? 0f : r2P.pather.MovePercentage;

        private static LinkSteps.Step LdStep(Map map, Pawn p) => LinkSteps.For(map)?.Of(p);

        private static string LdLink(Map map)
        {
            StairGrid g = StairGrid.For(map);
            int i = g == null || ldLadder == null ? -1 : g.IndexOf(ldLadder);
            if (i < 0)
            {
                return "no link";
            }
            VLink l = g.links[i];
            return $"{l.kind} foot {l.bottom} top {l.top} span {l.span} step {l.stepTicks}";
        }

        private static void LdMarkFalls()
        {
            ldFallsAt = LinkSteps.Falls;
            ldFallFloorsAt = LinkSteps.FallFloors;
            ldHurtAt = JumpGrid.HurtLandings;
            ldStaggerAt = JumpGrid.Staggers;
            ldWaitsAt = LinkSteps.Waits;
        }

        // ------------------------------------------------------------------------------------------------ C: climbs

        private static void LdClimb(Map map, bool up)
        {
            int n = ldSpans[ldIdx];
            LdFixture(map, n, LdRaiders(), false, r2P);
            Teleport(r2P, LdG(15, 13));
            Draft(r2P, true);
            Result($"C{n} fixture: a ladder of {n} floor(s) at the column's foot", ldLadder != null && StairGrid.For(map)?.IndexOf(ldLadder) >= 0, LdLink(map));
            ldT0 = ldT1 = -1;
            ldOverrideHeld = false;
            ldFlag = false;
            ldDest = LdL(24, 13, n, map);
            Job job = JobMaker.MakeJob(JobDefOf.Goto, ldDest);
            if (n == 4)
            {
                // a short expiry: without the guard the job would end in the middle of the climb
                job.expiryInterval = 200;
                job.checkOverrideOnExpire = false;
            }
            r2P.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder);
            int held0 = PZStats.climbOverridesHeld;
            SeedTicks("ld_c_up" + n);
            TickUntil(3000, () =>
            {
                LdTrack(map, r2P, n);
                if (n == 4 && !ldFlag && LinkSteps.Climbing(r2P) && LdStep(map, r2P).height > 1.5f)
                {
                    ldFlag = true;
                    r2P.jobs.CheckForJobOverride();
                    ldOverrideHeld = LinkSteps.Climbing(r2P) && PZStats.climbOverridesHeld > held0;
                }
                return Levels.LevelOf(r2P.Position, map) == n && Links.Kind(r2P) == StepKind.None;
            }, "ld_c_up", 1);
        }

        private static void LdTrack(Map map, Pawn p, int n)
        {
            int now = Find.TickManager.TicksGame;
            LinkSteps.Step st = LdStep(map, p);
            if (ldT0 < 0 && st != null && st.climbing && st.link.IsLadder)
            {
                ldT0 = now;
            }
            if (ldT0 >= 0 && ldT1 < 0 && Links.Kind(p) == StepKind.None)
            {
                ldT1 = now;
            }
        }

        private static void LdClimbCheck(Map map, bool up)
        {
            tickWatch = null;
            int n = ldN;
            int took = ldT1 - ldT0;
            int want = VLink.LadderTicksPerFloor * n;
            bool onTop = up ? Levels.LevelOf(r2P.Position, map) == n : r2P.Position == ldDest;
            Result($"C{n} {(up ? "up" : "down")}: the climb takes {want} ticks (3 s per floor) and ends {(up ? "on top" : "on the ground")}", onTop && Math.Abs(took - want) <= 3,
                $"took {took} ticks (from {ldT0} to {ldT1}), at {r2P.Position} level {Levels.LevelOf(r2P.Position, map)}, job {r2P.CurJobDef?.defName}");
            if (up && n == 4)
            {
                Result("C4 up: a Goto with a 200-tick expiry is not cut short mid-climb, and a job override asked mid-climb is held off", onTop && ldFlag && ldOverrideHeld,
                    $"override asked {ldFlag}, held {ldOverrideHeld} (held so far {PZStats.climbOverridesHeld})");
            }
            if (up)
            {
                ldT0 = ldT1 = -1;
                ldDest = LdG(15, 13);
                GoTo(r2P, ldDest);
                SeedTicks("ld_c_down" + n);
                TickUntil(3000, () =>
                {
                    LdTrack(map, r2P, n);
                    return r2P.Position == ldDest;
                }, "ld_c_down", 1);
                return;
            }
            ldIdx++;
            stage = ldIdx < ldSpans.Length ? "ld_c" : "ld_q";
        }

        // ------------------------------------------------------------------------------------------------ Q: queue

        private static void LdQueue(Map map)
        {
            LdFixture(map, 2, LdRaiders(), false, r2P, r2Q);
            Teleport(r2P, LdG(16, 12));
            Teleport(r2Q, LdG(16, 14));
            Draft(r2P, true);
            Draft(r2Q, true);
            LdMarkFalls();
            ldMaxHolders = 0;
            ldT1 = ldT1b = -1;
            GoTo(r2P, LdL(24, 12, 2, map));
            GoTo(r2Q, LdL(24, 14, 2, map));
            SeedTicks("ld_q");
            TickUntil(4000, () =>
            {
                LinkSteps s = LinkSteps.For(map);
                int holders = s.steps.Count(x => x.climbing && x.link.thing == ldLadder && x.pawn.pather.MovePercentage > 0f);
                ldMaxHolders = Math.Max(ldMaxHolders, holders);
                int now = Find.TickManager.TicksGame;
                if (ldT1 < 0 && Levels.LevelOf(r2P.Position, map) == 2)
                {
                    ldT1 = now;
                }
                if (ldT1b < 0 && Levels.LevelOf(r2Q.Position, map) == 2)
                {
                    ldT1b = now;
                }
                return ldT1 >= 0 && ldT1b >= 0;
            }, "ld_q_check", 1);
        }

        private static void LdQueueCheck(Map map)
        {
            tickWatch = null;
            int gap = Math.Abs(ldT1b - ldT1);
            Result("Q two pawns, one ladder: never two on it, the second waits at its end, both arrive", ldT1 >= 0 && ldT1b >= 0 && ldMaxHolders == 1 && gap >= VLink.LadderTicksPerFloor * 2 - 3 && LinkSteps.Waits > ldWaitsAt,
                $"most on the ladder at once {ldMaxHolders}, arrivals {ldT1} and {ldT1b} (gap {gap}), wait ticks {LinkSteps.Waits - ldWaitsAt}; vanilla pawn collision alone would not queue them (colonists pass through each other unless enemies are near)");
            stage = "ld_ca";
        }

        // ------------------------------------------------------------------------------------------------ CA: carry

        private static Hediff ldAnaesthetic;

        private static void LdCarry(Map map)
        {
            LdFixture(map, 2, LdRaiders(), false, r2P, r2Q);
            Teleport(r2P, LdG(15, 13));
            Teleport(r2Q, LdG(16, 15));
            Draft(r2P, true);
            Draft(r2Q, false);
            HealthUtility.TryAnesthetize(r2Q);
            ldAnaesthetic = r2Q.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Anesthetic);
            Job job = JobMaker.MakeJob(JobDefOf.CarryDownedPawnDrafted, r2Q);
            job.count = 1;
            r2P.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder);
            SeedTicks("ld_ca");
            TickUntil(1500, () => r2P.carryTracker.CarriedThing == r2Q, "ld_ca_go", 5);
        }

        private static void LdCarryGo(Map map)
        {
            tickWatch = null;
            Result("CA fixture: the downed pawn is picked up", r2P.carryTracker.CarriedThing == r2Q, $"downed {r2Q.Downed}, carried {r2P.carryTracker.CarriedThing}");
            ldT0 = ldT1 = -1;
            ldDest = LdL(24, 13, 2, map);
            GoTo(r2P, ldDest);
            SeedTicks("ld_ca_go");
            TickUntil(3000, () =>
            {
                LdTrack(map, r2P, 2);
                return Levels.LevelOf(r2P.Position, map) == 2 && Links.Kind(r2P) == StepKind.None;
            }, "ld_ca_check", 1);
        }

        private static void LdCarryCheck(Map map)
        {
            tickWatch = null;
            int took = ldT1 - ldT0;
            Result("CA carrying a downed pawn up 2 floors: no slower (360 ticks), the pawn still carried on top", Levels.LevelOf(r2P.Position, map) == 2 && Math.Abs(took - 360) <= 3 && r2P.carryTracker.CarriedThing == r2Q,
                $"took {took}, carrier at {r2P.Position}, carrying {r2P.carryTracker.CarriedThing}");
            r2P.carryTracker.TryDropCarriedThing(r2P.Position, ThingPlaceMode.Near, out Thing _);
            LdWake(r2Q);
            stage = "ld_b";
        }

        private static void LdWake(Pawn p)
        {
            if (p == null)
            {
                return;
            }
            foreach (Hediff h in p.health.hediffSet.hediffs.Where(h => h.def == HediffDefOf.Anesthetic).ToList())
            {
                p.health.RemoveHediff(h);
            }
            Heal(p);
        }

        // ------------------------------------------------------------------------------------------------ B: broken

        private static void LdBreak(Map map, bool up)
        {
            LdFixture(map, 4, LdRaiders(), false, r2P);
            Heal(r2P);
            Teleport(r2P, up ? LdG(15, 13) : LdL(22, 13, 4, map));
            Draft(r2P, true);
            LdMarkFalls();
            ldFlag = false;
            ldTicks = 0;
            ldHeightAt = -1f;
            GoTo(r2P, up ? LdL(24, 13, 4, map) : LdG(15, 13));
            SeedTicks("ld_b" + up);
            TickUntil(3000, () =>
            {
                LinkSteps.Step st = LdStep(map, r2P);
                if (!ldFlag && LinkSteps.Climbing(r2P) && (up ? st.height >= 2.5f : st.height <= 1.6f && st.height >= 1.05f))
                {
                    ldFlag = true;
                    ldHeightAt = st.height;
                    ldLadder.Destroy(DestroyMode.KillFinalize);
                }
                if (ldFlag)
                {
                    ldTicks++;
                }
                return ldFlag && ldTicks > 90;
            }, up ? "ld_b_check" : "ld_b2_check", 1);
        }

        private static void LdBreakCheck(Map map, bool up)
        {
            tickWatch = null;
            int fell = LinkSteps.FallFloors - ldFallFloorsAt;
            int want = up ? 2 : 1;
            bool bruised = HasBruise(r2P);
            Result(up ? "B ladder destroyed mid-climb up at 2.5 floors: the climber falls to the foot, 2 floors, bruised (hurt from 2 floors)" : "B ladder destroyed mid-climb down at about 1.3 floors: falls 1 floor, staggers, no bruise",
                ldFlag && r2P.Position == ldFoot && LinkSteps.Falls - ldFallsAt == 1 && fell == want && (up ? JumpGrid.HurtLandings - ldHurtAt == 1 && bruised : JumpGrid.HurtLandings == ldHurtAt && !bruised && JumpGrid.Staggers > ldStaggerAt),
                $"height when broken {ldHeightAt:0.00}, at {r2P.Position} (foot {ldFoot}), falls {LinkSteps.Falls - ldFallsAt}, floors {fell}, hurt landings {JumpGrid.HurtLandings - ldHurtAt}, staggers {JumpGrid.Staggers - ldStaggerAt}, bruised {bruised}, ladder destroyed {ldLadder?.Destroyed}, link gone {StairGrid.For(map)?.IndexOf(ldLadder) ?? -1}");
            Heal(r2P);
            stage = up ? "ld_b2" : "ld_dn";
        }

        // ------------------------------------------------------------------------------------------------ DN / DD

        private static void LdDowned(Map map)
        {
            LdFixture(map, 4, LdRaiders(), false, r2P);
            Heal(r2P);
            Teleport(r2P, LdG(15, 13));
            Draft(r2P, true);
            LdMarkFalls();
            ldFlag = false;
            ldTicks = 0;
            GoTo(r2P, LdL(24, 13, 4, map));
            SeedTicks("ld_dn");
            TickUntil(3000, () =>
            {
                LinkSteps.Step st = LdStep(map, r2P);
                if (!ldFlag && LinkSteps.Climbing(r2P) && st.height >= 2.5f)
                {
                    ldFlag = true;
                    ldHeightAt = st.height;
                    HealthUtility.TryAnesthetize(r2P);
                }
                if (ldFlag)
                {
                    ldTicks++;
                }
                return ldFlag && ldTicks > 90;
            }, "ld_dn_check", 1);
        }

        private static void LdDownedCheck(Map map)
        {
            tickWatch = null;
            Result("DN downed mid-climb at 2.5 floors: falls to the foot (2 floors, bruised); the ladder stays",
                ldFlag && r2P.Downed && r2P.Position == ldFoot && LinkSteps.FallFloors - ldFallFloorsAt == 2 && HasBruise(r2P) && ldLadder.Spawned,
                $"height {ldHeightAt:0.00}, downed {r2P.Downed}, at {r2P.Position}, floors fallen {LinkSteps.FallFloors - ldFallFloorsAt}, bruised {HasBruise(r2P)}, ladder spawned {ldLadder.Spawned}");
            LdWake(r2P);
            stage = "ld_dd";
        }

        private static void LdDead(Map map)
        {
            LdFixture(map, 4, LdRaiders(), false, r2P);
            R2Hide(map);
            ldR = MakeRaider(map, LdL(22, 13, 4, map));
            ldR.equipment.DestroyAllEquipment();
            ldFlag = false;
            ldTicks = 0;
            Job job = JobMaker.MakeJob(JobDefOf.Goto, LdG(12, 13));
            job.locomotionUrgency = LocomotionUrgency.Jog;
            ldR.jobs.StartJob(job, JobCondition.InterruptForced);
            SeedTicks("ld_dd");
            TickUntil(3000, () =>
            {
                LinkSteps.Step st = LdStep(map, ldR);
                if (!ldFlag && !ldR.Dead && LinkSteps.Climbing(ldR) && st.height <= 2.5f)
                {
                    ldFlag = true;
                    ldHeightAt = st.height;
                    ldR.Kill(null);
                }
                if (ldFlag)
                {
                    ldTicks++;
                }
                return ldFlag && ldTicks > 30 || ldR.Destroyed && !ldR.Dead;
            }, "ld_dd_check", 1);
        }

        private static void LdDeadCheck(Map map)
        {
            tickWatch = null;
            Corpse c = ldR?.Corpse;
            Result("DD a raider killed mid-descent: the corpse comes down to the foot", ldFlag && c != null && c.Spawned && c.Position == ldFoot,
                $"height {ldHeightAt:0.00}, corpse at {c?.Position} (foot {ldFoot}), lowered so far {LinkSteps.CorpsesLowered}");
            c?.Destroy();
            R2Unhide(map);
            stage = "ld_p";
        }

        // ------------------------------------------------------------------------------------------------ P: place

        private static void LdPlace(Map map, int who)
        {
            LdFixture(map, 2, null, false, r2P);
            ldMoteSeen = false;
            ldWaitsAt = SiegeLadderToils.BarTicks;
            ldT0 = Find.TickManager.TicksGame;
            Pawn p;
            if (who == 0)
            {
                p = r2P;
                Teleport(p, LdG(12, 13));
                Draft(p, true);
                Job job = JobDriver_PlaceSiegeLadder.Make(ldFoot, ldTop);
                job.playerForced = true;
                p.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }
            else
            {
                R2Hide(map);
                if (who == 1)
                {
                    p = MakeRaider(map, LdG(12, 13));
                    p.equipment.DestroyAllEquipment();
                }
                else
                {
                    p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Mech_Scyther, Faction.OfMechanoids, forceGenerateNewPawn: true));
                    GenSpawn.Spawn(p, LdG(12, 13), map);
                }
                p.jobs.StartJob(JobDriver_PlaceSiegeLadder.Make(ldFoot, ldTop), JobCondition.InterruptForced);
            }
            ldR = p;
            SeedTicks("ld_p" + who);
            TickUntil(1500, () =>
            {
                if (SiegeLadderToils.BarTicks > ldWaitsAt && SiegeLadderToils.LastBarFaction == ldR.Faction)
                {
                    ldMoteSeen = true;
                }
                return ldFoot.GetFirstThing<Building_SiegeLadder>(map) != null;
            }, who == 0 ? "ld_p_check" : who == 1 ? "ld_p2_check" : "ld_p3_check", 1);
        }

        private static void LdPlaceCheck(Map map, int who)
        {
            tickWatch = null;
            Building_SiegeLadder b = ldFoot.GetFirstThing<Building_SiegeLadder>(map);
            ldLadder = b;
            string name = who == 0 ? "a colonist (forced order)" : who == 1 ? "a raider" : "a mechanoid";
            ThingDef want = who == 2 ? PZDefOf.PZ_SiegeLift : PZDefOf.PZ_SiegeLadder;
            Result($"P {name} places a {(who == 2 ? "lift" : "ladder")} at the foot after a 60-tick job with a progress bar shown for any faction; it is the placer's, its link registered",
                b != null && b.def == want && b.Faction == ldR.Faction && ldMoteSeen && StairGrid.For(map)?.IndexOf(b) >= 0,
                $"{b?.def.defName} faction {b?.Faction} (placer {ldR} {ldR.Faction}), progress bar seen {ldMoteSeen} (bar ticks {SiegeLadderToils.BarTicks - ldWaitsAt}, last by {SiegeLadderToils.LastBarFaction}), {LdLink(map)}, after {Find.TickManager.TicksGame - ldT0} ticks");
            if (who != 0)
            {
                if (!ldR.Destroyed)
                {
                    ldR.Destroy();
                }
                R2Unhide(map);
            }
            stage = who == 0 ? "ld_p2" : who == 1 ? "ld_p3" : "ld_cl";
        }

        // ------------------------------------------------------------------------------------------------ CL: claim

        private static void LdClaim(Map map)
        {
            LdFixture(map, 2, LdRaiders());
            R2Hide(map);
            ldR = MakeRaider(map, LdG(3, 3));
            ldR.equipment.DestroyAllEquipment();
            ldR.stances.stunner.StunFor(30000, null, addBattleLog: false);
            ldCountdownAt = ldLadder.GetComp<CompSiegeLadder>().ticksLeft;
            SeedTicks("ld_cl");
            StartTicks(1000, "ld_cl_raid");
        }

        private static void LdClaimRaid(Map map)
        {
            CompSiegeLadder comp = ldLadder.GetComp<CompSiegeLadder>();
            AcceptanceReport claim = new Designator_Claim().CanDesignateThing(ldLadder);
            AcceptanceReport decon = new Designator_Deconstruct().CanDesignateThing(ldLadder);
            Result("CL during the raid (a raider on the map): claim refused, deconstruct refused, the countdown paused",
                !claim.Accepted && !decon.Accepted && comp.ticksLeft == ldCountdownAt && !comp.Running,
                $"claim {claim.Accepted} ({claim.Reason}), deconstruct {decon.Accepted} ({decon.Reason}), ticks left {comp.ticksLeft} (was {ldCountdownAt}), inspect \"{comp.CompInspectStringExtra()}\"");
            ldR.Destroy();
            ldCountdownAt = comp.ticksLeft;
            SeedTicks("ld_cl2");
            StartTicks(1000, "ld_cl_after");
        }

        private static void LdClaimAfter(Map map)
        {
            CompSiegeLadder comp = ldLadder.GetComp<CompSiegeLadder>();
            Designator_Claim d = new Designator_Claim();
            AcceptanceReport claim = d.CanDesignateThing(ldLadder);
            int ran = ldCountdownAt - comp.ticksLeft;
            Result("CL after the raid: claim allowed, the countdown runs", claim.Accepted && ran >= 750 && ran <= 1000 && comp.Running,
                $"claim {claim.Accepted}, countdown ran {ran} of 1000 ticks, inspect \"{comp.CompInspectStringExtra()}\"");
            if (claim.Accepted)
            {
                d.DesignateThing(ldLadder);
            }
            ldCountdownAt = comp.ticksLeft;
            SeedTicks("ld_cl3");
            StartTicks(1000, "ld_cl_claimed");
        }

        private static void LdClaimClaimed(Map map)
        {
            CompSiegeLadder comp = ldLadder.GetComp<CompSiegeLadder>();
            int ran = ldCountdownAt - comp.ticksLeft;
            Result("CL claimed: the ladder is the player's and its countdown keeps running", ldLadder.Faction == Faction.OfPlayer && ran >= 750 && ran <= 1000,
                $"faction {ldLadder.Faction}, countdown ran {ran} of 1000 ticks (left {comp.ticksLeft})");
            R2Unhide(map);
            stage = "ld_dc";
        }

        // ------------------------------------------------------------------------------------------------ DC: deconstruct

        private static void LdDecon(Map map, bool lift)
        {
            LdFixture(map, 2, lift ? Faction.OfMechanoids : LdRaiders(), lift, r2P);
            new Designator_Claim().DesignateThing(ldLadder);
            Teleport(r2P, LdG(12, 13));
            Draft(r2P, false);
            r2P.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
            new Designator_Deconstruct().DesignateThing(ldLadder);
            SeedTicks("ld_dc" + lift);
            TickUntil(3000, () => ldLadder.Destroyed, lift ? "ld_dc2_check" : "ld_dc_check", 10);
        }

        private static void LdDeconCheck(Map map, bool lift)
        {
            tickWatch = null;
            ThingDef res = lift ? ThingDefOf.ComponentIndustrial : ThingDefOf.Cloth;
            CellRect big = r2Area.ExpandedBy(3).ClipInsideMap(map);
            int got = map.listerThings.ThingsOfDef(res).Where(t => t.Spawned && big.Contains(Levels.Ground(t.Position, map))).Sum(t => t.stackCount);
            int want = lift ? 1 : 5;
            Result($"DC a colonist deconstructs the claimed {(lift ? "lift" : "ladder")}: {want} {res.label}", ldLadder.Destroyed && got == want,
                $"destroyed {ldLadder.Destroyed}, {res.defName} left {got}, worker job {r2P.CurJobDef?.defName}");
            foreach (Thing t in map.listerThings.ThingsOfDef(res).Where(t => t.Spawned && big.Contains(Levels.Ground(t.Position, map))).ToList())
            {
                t.Destroy();
            }
            Draft(r2P, true);
            stage = lift ? "ld_h" : "ld_dc2";
        }

        // ------------------------------------------------------------------------------------------------ H: haul

        private static void LdHaul(Map map)
        {
            LdFixture(map, 2, null, false, r2P);
            // the platform fogged before the ladder comes: revealed over the ladder (ReachFog)
            foreach (IntVec3 g in new CellRect(LdG(20, 8).x, LdG(20, 8).z, 10, 12))
            {
                IntVec3 c = Levels.Cell(g, 2, map);
                map.fogGrid.Refog(CellRect.SingleCell(c));
            }
            Teleport(r2P, LdG(12, 13));
            Draft(r2P, false);
            ldFlag = ldTop.Fogged(map);
            ldLadder = LadderSite.Spawn(ldFoot, ldTop, LdRaiders(), map, false);
            SeedTicks("ld_h_fog");
            StartTicks(300, "ld_h_fog");
        }

        private static void LdHaulFog(Map map)
        {
            bool revealed = !LdL(25, 14, 2, map).Fogged(map) && !ldTop.Fogged(map);
            Result("H the fogged platform is revealed over the new ladder (ReachFog treats it as a link)", ldFlag && revealed, $"top fogged before {ldFlag}, platform revealed {revealed}");
            Zone_Stockpile zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (IntVec3 g in new CellRect(LdG(24, 11).x, LdG(24, 11).z, 3, 3))
            {
                zone.AddCell(Levels.Cell(g, 2, map));
            }
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 30;
            GenPlace.TryPlaceThing(steel, LdG(10, 13), map, ThingPlaceMode.Direct);
            foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefs)
            {
                if (!r2P.WorkTypeIsDisabled(w))
                {
                    r2P.workSettings.SetPriority(w, w == WorkTypeDefOf.Hauling ? 1 : 0);
                }
            }
            ldClimbsAt = climbsColonists;
            SeedTicks("ld_h");
            TickUntil(8000, () => LdSteelUp(map) >= 30, "ld_h_check", 30);
        }

        private static int LdSteelUp(Map map) => map.listerThings.ThingsOfDef(ThingDefOf.Steel).Where(t => t.Spawned && Levels.LevelOf(t.Position, map) == 2).Sum(t => t.stackCount);

        private static void LdHaulCheck(Map map)
        {
            tickWatch = null;
            int up = LdSteelUp(map);
            Result("H a colonist hauls 30 steel up the ladder to the stockpile on floor 3", up >= 30 && climbsColonists > ldClimbsAt, $"steel upstairs {up}, colonist link steps {climbsColonists - ldClimbsAt}, hauler at {r2P.Position}");
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
            stage = "ld_k";
        }

        // ------------------------------------------------------------------------------------------------ K: last way down

        private static void LdK2(Map map)
        {
            LdFixture(map, 2, LdRaiders(), false, r2P);
            new Designator_Claim().DesignateThing(ldLadder);
            Teleport(r2P, LdL(25, 13, 2, map));
            Draft(r2P, false);
            new Designator_Deconstruct().DesignateThing(ldLadder);
            int side = StairTouch.RemovalSide(ldLadder);
            Result("K the ladder is the platform's only way: taking it up would cut the top off (worked from the foot side only)", side == 1, $"removal side {side}");
            SeedTicks("ld_k");
            TickUntil(4000, () => ldLadder.Destroyed, "ld_k_check", 10);
        }

        private static void LdK2Check(Map map)
        {
            tickWatch = null;
            Result("K the colonist on the platform climbs down and takes the ladder up from the ground (never left on top)", ldLadder.Destroyed && Levels.LevelOf(r2P.Position, map) == 0,
                $"destroyed {ldLadder.Destroyed}, worker at {r2P.Position} level {Levels.LevelOf(r2P.Position, map)}");
            foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Cloth).ToList())
            {
                t.Destroy();
            }
            Draft(r2P, true);
            stage = "ld_e1";
        }

        // ------------------------------------------------------------------------------------------------ E: emergency ladder

        private static int ldBuiltAt, ldBuilderLevel;

        private static Building_SiegeLadder LdEmergencyAt(Map map) => ldTop.GetFirstThing<Building_SiegeLadder>(map);

        // The player's emergency ladder: a blueprint on the top cell turned toward the open column (west), built by a colonist
        // standing on the platform (fromTop) or on the ground.
        private static void LdEmergency(Map map, bool fromTop, string next)
        {
            LdFixture(map, 2, null, false, r2P);
            Heal(r2P);
            Teleport(r2P, fromTop ? LdL(25, 13, 2, map) : LdG(12, 13));
            Draft(r2P, false);
            r2P.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
            AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_EmergencyLadder, ldTop, Rot4.West, map);
            AcceptanceReport bad = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_EmergencyLadder, ldTop, Rot4.East, map);
            Result($"E{(fromTop ? 1 : 2)} the blueprint goes on the top cell turned toward the open side (refused toward the floor)", ok.Accepted && !bad.Accepted, $"west {ok.Accepted} ({ok.Reason}), east {bad.Accepted} ({bad.Reason})");
            if (ok.Accepted)
            {
                GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_EmergencyLadder, ldTop, map, Rot4.West, Faction.OfPlayer, null);
            }
            ldFlag2 = false;
            ldBuiltAt = -1;
            ldBuilderLevel = -9;
            SeedTicks("ld_e" + fromTop);
            TickUntil(4000, () =>
            {
                if (Levels.LevelOf(r2P.Position, map) != (fromTop ? 2 : 0))
                {
                    ldFlag2 = true;
                }
                if (LdEmergencyAt(map) != null)
                {
                    ldBuiltAt = Find.TickManager.TicksGame;
                    ldBuilderLevel = Levels.LevelOf(r2P.Position, map);
                    return true;
                }
                return false;
            }, next, 1);
        }

        private static void LdEmergencyCheck(Map map, bool fromTop)
        {
            tickWatch = null;
            Building_SiegeLadder b = LdEmergencyAt(map);
            ldLadder = b;
            StairGrid g = StairGrid.For(map);
            int i = g == null || b == null ? -1 : g.IndexOf(b);
            VLink l = i < 0 ? default : g.links[i];
            Result($"E{(fromTop ? 1 : 2)} a colonist builds the emergency ladder from {(fromTop ? "the top (stuck on the platform)" : "the foot")}: the player's, standing on the top cell, its link down to the foot",
                b != null && b.Faction == Faction.OfPlayer && i >= 0 && l.bottom == ldFoot && l.top == ldTop && l.span == 2 && ldBuilderLevel == (fromTop ? 2 : 0) && !ldFlag2,
                $"{b?.def.defName} at {b?.Position} faction {b?.Faction}, link {(i < 0 ? "none" : l.bottom + " -> " + l.top + " span " + l.span)}, builder level {ldBuilderLevel} (left its floor {ldFlag2}), built at {ldBuiltAt}");
            if (b == null)
            {
                LdEmergencyDiag(map);
                stage = fromTop ? "ld_e2" : "ld_s";
                return;
            }
            ldDest = fromTop ? LdG(14, 13) : LdL(25, 13, 2, map);
            GoTo(r2P, ldDest);
            SeedTicks("ld_e_go" + fromTop);
            TickUntil(3000, () => r2P.Position == ldDest, fromTop ? "ld_e1_go" : "ld_e2_go", 5);
        }

        private static void LdEmergencyDiag(Map map)
        {
            Thing bp = ldTop.GetThingList(map).FirstOrDefault(x => EmergencyLadder.IsPending(x));
            string s = bp == null ? "no blueprint or frame" : $"{bp.def.defName} at {bp.Position}";
            if (bp != null)
            {
                bool foot = EmergencyLadder.PendingFoot(bp, out IntVec3 f);
                bool spot = RCellFinder.TryFindGoodAdjacentSpotToTouch(r2P, bp, out IntVec3 sc);
                s += $", foot {foot} {f}, reach touch {r2P.CanReach(bp, PathEndMode.Touch, Danger.Deadly)}, spot {spot} {sc}, can construct {GenConstruct.CanConstruct(bp, r2P, false)}, immediate from foot {ReachabilityImmediate.CanReachImmediate(f, bp, map, PathEndMode.Touch, r2P)}";
                PawnPath pp = map.pathFinder.FindPathNow(r2P.Position, bp, TraverseParms.For(r2P), null, PathEndMode.Touch);
                s += $", path {(pp != null && pp.Found ? pp.NodesReversed.Count + " nodes, ends " + pp.NodesReversed[0] : "none")}, last leg fail {StairRouter.LastLegFail}, needed {StairRouter.Needed(map, r2P.Position, bp, PathEndMode.Touch, TraverseParms.For(r2P))}";
                pp?.Dispose();
                foreach (WorkGiverDef wg in DefDatabase<WorkGiverDef>.AllDefs.Where(w => w.Worker is WorkGiver_Scanner && w.workType == WorkTypeDefOf.Construction))
                {
                    WorkGiver_Scanner sc2 = (WorkGiver_Scanner)wg.Worker;
                    Job j = null;
                    try { j = sc2.HasJobOnThing(r2P, bp) ? sc2.JobOnThing(r2P, bp) : null; } catch (Exception e) { s += " ex " + e.Message; }
                    if (j != null) s += $", {wg.defName} -> {j.def.defName}";
                }
            }
            Info($"emergency diag: {s}; builder {r2P} at {r2P.Position} job {r2P.CurJobDef?.defName} drafted {r2P.Drafted} construction active {r2P.workSettings.WorkIsActive(WorkTypeDefOf.Construction)}");
        }

        private static void LdEmergencyGone(Map map, bool fromTop)
        {
            tickWatch = null;
            Result(fromTop ? "E1 the stuck colonist climbs down its own ladder" : "E2 the colonist climbs up the ladder it built", r2P.Position == ldDest, $"at {r2P.Position}");
            if (fromTop)
            {
                stage = "ld_e2";
                return;
            }
            // no raid gate: counting from completion even with a raider on the map
            ldR = MakeRaider(map, LdG(3, 3));
            ldR.equipment.DestroyAllEquipment();
            ldR.stances.stunner.StunFor(30000, null, addBattleLog: false);
            ldCountdownAt = ldLadder.GetComp<CompSiegeLadder>().ticksLeft;
            SeedTicks("ld_e_cd");
            StartTicks(1000, "ld_e_cd");
        }

        private static void LdEmergencyCountdown(Map map)
        {
            CompSiegeLadder comp = ldLadder.GetComp<CompSiegeLadder>();
            int ran = ldCountdownAt - comp.ticksLeft;
            int since = Find.TickManager.TicksGame - ldBuiltAt;
            Result("E the emergency ladder counts down from completion, a raider on the map or not (no raid gate)", comp.Running && ran >= 750 && ran <= 1000 && Math.Abs(60000 - comp.ticksLeft - since) <= 260,
                $"ran {ran} of 1000 ticks with a raider here, left {comp.ticksLeft}, ticks since built {since}, inspect \"{comp.CompInspectStringExtra()}\"");
            ldR.Destroy();
            // a raider climbs it like any ladder
            ldPId = r2P.ThingID;
            R2Hide(map);
            ldR = MakeRaider(map, LdG(12, 13));
            ldR.equipment.DestroyAllEquipment();
            ldDest = LdL(26, 13, 2, map);
            Job job = JobMaker.MakeJob(JobDefOf.Goto, ldDest);
            ldR.jobs.StartJob(job, JobCondition.InterruptForced);
            ldClimbsAt = climbsHostiles;
            SeedTicks("ld_e_raider");
            TickUntil(3000, () => Levels.LevelOf(ldR.Position, map) == 2, "ld_e_raider", 5);
        }

        private static void LdEmergencyRaider(Map map)
        {
            tickWatch = null;
            Result("E a raider climbs the player's emergency ladder like any ladder", Levels.LevelOf(ldR.Position, map) == 2 && climbsHostiles > ldClimbsAt, $"raider at {ldR.Position}, hostile link steps {climbsHostiles - ldClimbsAt}");
            ldR.Destroy();
            R2Unhide(map);
            r2P = ById(map, ldPId) ?? r2P;
            Teleport(r2P, LdG(12, 13));
            Draft(r2P, false);
            r2P.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
            new Designator_Deconstruct().DesignateThing(ldLadder);
            SeedTicks("ld_e_dc");
            TickUntil(3000, () => ldLadder.Destroyed, "ld_e_dc", 10);
        }

        private static void LdEmergencyDecon(Map map)
        {
            tickWatch = null;
            CellRect big = r2Area.ExpandedBy(3).ClipInsideMap(map);
            List<Thing> left = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableAlways).Where(t => t.Spawned && big.Contains(Levels.Ground(t.Position, map))).ToList();
            Result("E deconstructing the emergency ladder gives nothing back", ldLadder.Destroyed && left.Count == 0, $"destroyed {ldLadder.Destroyed}, items left {string.Join(", ", left.Select(t => t.LabelCap))}");
            Draft(r2P, true);
            stage = "ld_s";
        }

        // The last check: an emergency ladder built from the foot disappears one day after it was finished.
        private static void LdEmergencyEnd(Map map)
        {
            LdEmergency(map, false, "ld_ecd_built");
        }

        private static void LdEmergencyEndBuilt(Map map)
        {
            tickWatch = null;
            ldLadder = LdEmergencyAt(map);
            Draft(r2P, true);
            Teleport(r2P, LdG(12, 10));
            if (ldLadder == null)
            {
                Result("ECD the emergency ladder disappears a day after it was built", false, "not built");
                Finish();
                return;
            }
            SeedTicks("ld_ecd");
            TickUntil(62000, () => ldLadder.Destroyed, "ld_ecd_check", 250);
        }

        private static void LdEmergencyEndCheck(Map map)
        {
            tickWatch = null;
            int took = Find.TickManager.TicksGame - ldBuiltAt;
            Result("ECD the emergency ladder disappears a day after it was built (60000 ticks), its link gone", ldLadder.Destroyed && took >= 59750 && took <= 60500 && StairGrid.For(map)?.IndexOf(ldLadder) is null or < 0,
                $"destroyed {ldLadder.Destroyed} {took} ticks after completion");
            Info($"ladder counters: falls {LinkSteps.Falls} ({LinkSteps.FallFloors} floors), wait ticks {LinkSteps.Waits}, corpses lowered {LinkSteps.CorpsesLowered}, paths stopped {LinkSteps.Stopped}, overrides held {PZStats.climbOverridesHeld}");
            stage = "ld_col";   // (the follow-ups: TestLadders2)
        }

        // ------------------------------------------------------------------------------------------------ S: shots

        private static void LdShots(Map map)
        {
            LdFixture(map, 2, LdRaiders(), false, r2P);
            Teleport(r2P, LdG(16, 13));
            Draft(r2P, true);
            ldDest = LdL(24, 13, 2, map);
            GoTo(r2P, ldDest);
            SeedTicks("ld_s");
            TickUntil(2000, () => LdPct(map) >= 0.4f && Links.Kind(r2P) == StepKind.LadderUp, "ld_s1", 1);
        }

        private static void LdShot(Map map, string name, string next)
        {
            tickWatch = null;
            Info($"{name}: {r2P} at {r2P.Position} -> {r2P.pather.nextCell} {r2P.pather.MovePercentage:P0}, kind {Links.Kind(r2P)}, rise {Links.Rise(r2P):0.00}, draw pos {r2P.DrawPos}");
            ShotAt(map, 2, ldFoot + IntVec3.East, 5f);
            Wait(3, next);
        }

        // ------------------------------------------------------------------------------------------------ SV: save

        private static void LdSave(Map map)
        {
            tickWatch = null;
            LdFixture(map, 2, LdRaiders(), false, r2P);
            Teleport(r2P, LdG(16, 13));
            Draft(r2P, true);
            ldDest = LdL(24, 13, 2, map);
            GoTo(r2P, ldDest);
            SeedTicks("ld_sv");
            TickUntil(2000, () => LdPct(map) >= 0.5f && Links.Kind(r2P) == StepKind.LadderUp, "ld_sv_save", 1);
        }

        private static void LdSaveNow(Map map)
        {
            tickWatch = null;
            ldPId = r2P.ThingID;
            ldQId = r2Q?.ThingID;
            Info($"saving with {r2P} mid-climb at {r2P.pather.MovePercentage:P0}");
            climbsAtSave = PZStats.climbResets;
            GameDataSaveLoader.SaveGame("pz_ladder");
            Result("SV saved pz_ladder mid-climb", File.Exists(GenFilePaths.FilePathForSavedGame("pz_ladder")), "");
            stage = "ld_sv_loaded";
            pendingLoad = "pz_ladder";
        }

        private static void LdLoaded(Map map)
        {
            r2P = ById(map, ldPId);
            r2Q = ById(map, ldQId);
            ldLadder = ldFoot.GetFirstThing<Building_SiegeLadder>(map);
            bool reset = r2P != null && PZStats.climbResets > climbsAtSave;
            Result("SV after loading: the ladder and its link are back, the climber starts its climb again from the foot", r2P != null && ldLadder != null && StairGrid.For(map)?.IndexOf(ldLadder) >= 0 && reset && (r2P.Position == ldFoot || Levels.LevelOf(r2P.Position, map) == 2),
                $"climber {r2P} at {r2P?.Position}, next {r2P?.pather.nextCell}, resets {PZStats.climbResets - climbsAtSave}, {LdLink(map)}, job {r2P?.CurJobDef?.defName}");
            if (r2P != null && r2P.CurJobDef != JobDefOf.Goto)
            {
                GoTo(r2P, ldDest);
            }
            SeedTicks("ld_sv_after");
            TickUntil(2000, () => r2P == null || r2P.Position == ldDest, "ld_sv_check", 5);
        }

        private static void LdLoadedCheck(Map map)
        {
            tickWatch = null;
            Result("SV the reloaded climber gets to the top", r2P != null && r2P.Position == ldDest, $"at {r2P?.Position}");
            stage = "ld_cd";
        }

        // ------------------------------------------------------------------------------------------------ CD: countdown

        private static void LdCountdown(Map map)
        {
            LdFixture(map, 2, LdRaiders());
            ldT0 = Find.TickManager.TicksGame;
            SeedTicks("ld_cd");
            TickUntil(62000, () => ldLadder.Destroyed, "ld_cd_check", 250);
        }

        private static void LdCountdownCheck(Map map)
        {
            tickWatch = null;
            int took = Find.TickManager.TicksGame - ldT0;
            Result("CD with no raiders left the ladder falls apart after one day (60000 ticks), its link gone", ldLadder.Destroyed && took >= 59750 && took <= 60500 && StairGrid.For(map)?.IndexOf(ldLadder) is null or < 0,
                $"destroyed {ldLadder.Destroyed} after {took} ticks");
            stage = "ld_ecd";
        }
    }
}
