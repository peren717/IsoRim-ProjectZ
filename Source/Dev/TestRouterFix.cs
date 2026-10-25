using System;
using System.Linq;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode routerfix (2026-10-25): the router's failure handling (RouterErrors). Run with -projectzfloors=2 (or more)
    // -projectzseed=7; -projectzexpectpatcher=<packageId> names a test mod whose dummy Harmony patch on Reachability must
    // show in the message. A forced failure (RouterErrors.TestThrow) for one pawn only, real jobs and ticks:
    //   RX1 a hauling job up the stairs fails: the request is answered (the pawn drops the job, never waits), one full
    //       message naming the pawn, its job (HaulToCell, its work giver, the targets) and the mods; the memo stops the job
    //       from being handed out again at once;
    //   RX2 another pawn still gets its path over the stairs (a drafted order up);
    //   RX3 more failures within the minute: handled, no further message (no log spam);
    //   RX4 after more than a real-time minute a bill job up the stairs fails: a second full message, with the bill and
    //       its bench.
    public static partial class TestRunner
    {
        private static Thing rxItem, rxBench;
        private static int rxHandledAt, rxLoggedAt;
        private static float rxFirstLogTime;
        private static bool rxSawJob;
        private static int rxLongestWait;
        private static int rxWaitStart = -1;

        private static bool StepRouterFix(Map map)
        {
            switch (stage)
            {
                case "rx_setup":
                    RxSetup(map);
                    return true;
                case "rx_haul_check":
                    RxHaulCheck(map);
                    return true;
                case "rx_other_check":
                    RxOtherCheck(map);
                    return true;
                case "rx_quiet_check":
                    RxQuietCheck(map);
                    return true;
                case "rx_bill":
                    RxBill(map);
                    return true;
                case "rx_bill_check":
                    RxBillCheck(map);
                    return true;
            }
            return false;
        }

        // Watches the failing pawn: how long a path request stays unanswered (it must never wait for long).
        private static void RxWatchWait()
        {
            Pawn p = r2P;
            if (p?.pather == null)
            {
                return;
            }
            bool waiting = p.pather.curPathRequest != null && !p.pather.curPathRequest.ResultIsReady;
            int now = Find.TickManager.TicksGame;
            if (waiting && rxWaitStart < 0)
            {
                rxWaitStart = now;
            }
            else if (!waiting && rxWaitStart >= 0)
            {
                rxLongestWait = Mathf.Max(rxLongestWait, now - rxWaitStart);
                rxWaitStart = -1;
            }
            if (rxWaitStart >= 0)
            {
                rxLongestWait = Mathf.Max(rxLongestWait, now - rxWaitStart);
            }
        }

        private static void RxSetup(Map map)
        {
            DebugSettings.enableRandomMentalStates = false;
            NoRoofCollapse(map);
            Find.Storyteller.storytellerComps.Clear();   // no incidents during the test (test only)
            NoAmbushes(map);
            foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
            {
                a.Destroy();
            }
            AnnexMapComponent annex = ProjectZ.Annex(map);
            if (annex == null || annex.MaxLevel < 1)
            {
                Result("routerfix: a map with floors", false, "no floors (run with -projectzfloors=2)");
                Finish();
                return;
            }
            r2Area = new CellRect(map.Center.x - 22, map.Center.z - 16, 44, 32);
            var cols = Colonists(map);
            r2P = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Crafting)) ?? cols.First();
            r2Q = cols.FirstOrDefault(p => p != r2P);
            R2Clear(map);
            R2Park(map, r2P, r2Q);
            R2Feed(map);
            // an upper floor over x 10..29, z 10..21, stairs up from the ground at (12, 7) (exit (12, 10))
            R2Floor(map, new CellRect(RG(10, 10).x, RG(10, 10).z, 20, 12), 1);
            R2Stairs(map, RG(12, 7), Rot4.North);
            // wood upstairs, a stockpile on the ground
            rxItem = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.WoodLog), RL(22, 16, 1, map), map);
            rxItem.stackCount = 20;
            Zone_Stockpile z = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(z);
            foreach (IntVec3 c in new CellRect(RG(30, 2).x, RG(30, 2).z, 3, 3))
            {
                z.AddCell(c);
            }
            Teleport(r2P, RG(16, 3));
            Draft(r2P, false);
            R2Work(r2P, WorkTypeDefOf.Hauling);
            Teleport(r2Q, RG(20, 3));
            Draft(r2Q, true);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Pawn failing = r2P;
            RouterErrors.TestThrow = p => p == failing;
            rxHandledAt = RouterErrors.Handled;
            rxLoggedAt = RouterErrors.Logged;
            rxSawJob = false;
            rxLongestWait = 0;
            rxWaitStart = -1;
            Info($"routerfix: failing pawn {r2P}, other pawn {r2Q}, wood {rxItem} at {rxItem.Position}, levels {Levels.Min(map)}..{Levels.Max(map)}");
            TickUntil(3000, () =>
            {
                RxWatchWait();
                rxSawJob |= r2P.CurJobDef == JobDefOf.HaulToCell;
                return RouterErrors.Handled > rxHandledAt && RouterErrors.Logged > rxLoggedAt;
            }, "rx_haul_check", 5);
        }

        private static void RxHaulCheck(Map map)
        {
            tickWatch = null;
            rxFirstLogTime = Time.realtimeSinceStartup;
            string msg = RouterErrors.LastMessage ?? "";
            string expect = GenCommandLine.TryGetCommandLineArg("projectzexpectpatcher", out string pid) ? pid : null;
            bool content = msg.Contains(r2P.LabelShort) && msg.Contains("HaulToCell") && msg.Contains("work giver") && msg.Contains("WoodLog")
                && msg.Contains("floor 1") && msg.Contains("may be involved") && msg.Contains("forced router failure");
            bool patcher = expect == null || msg.IndexOf(expect, StringComparison.OrdinalIgnoreCase) >= 0;
            Result("RX1 a hauling job up the stairs fails: one full message (pawn, HaulToCell + work giver, targets, mods, exception)",
                rxSawJob && RouterErrors.Logged == rxLoggedAt + 1 && content && patcher,
                $"job seen {rxSawJob}, messages {RouterErrors.Logged - rxLoggedAt}, content {content}, expected patcher {expect ?? "-"} listed {patcher}");
            Info("routerfix message 1:\n" + msg);
            // the pawn must not wait: the request answered, the job dropped, and not handed out again while the memo lasts
            rxHandledAt = RouterErrors.Handled;
            TickUntil(200, () => { RxWatchWait(); return false; }, "rx_other_check", 5);
        }

        private static void RxOtherCheck(Map map)
        {
            tickWatch = null;
            bool dropped = r2P.CurJobDef != JobDefOf.HaulToCell || r2P.pather.curPathRequest == null || r2P.pather.curPathRequest.ResultIsReady;
            Result("RX1b the failing pawn never waits for an answer: requests answered within a few ticks, the job dropped",
                rxLongestWait <= 30 && dropped, $"longest unanswered request {rxLongestWait} ticks, job now {r2P.CurJobDef?.defName}");
            // RX2: the other pawn goes up the stairs
            GoTo(r2Q, RL(25, 18, 1, map));
            TickUntil(2500, () => { RxWatchWait(); return r2Q.Position == RL(25, 18, 1, map); }, "rx_quiet_check", 10);
        }

        private static void RxQuietCheck(Map map)
        {
            tickWatch = null;
            Result("RX2 another pawn still gets its path over the stairs", r2Q.Position == RL(25, 18, 1, map), $"at {r2Q.Position} (floor {Levels.LevelOf(r2Q.Position, map)})");
            // RX3: more failures (the memo runs out, the job comes back, fails again) stay silent within the minute
            int handled = RouterErrors.Handled, logged = RouterErrors.Logged;
            rxHandledAt = handled;
            TickUntil(1500, () => { RxWatchWait(); return false; }, "rx_bill", 10);
            rxLoggedAt = logged;
        }

        private static void RxBill(Map map)
        {
            tickWatch = null;
            float since = Time.realtimeSinceStartup - rxFirstLogTime;
            Result("RX3 failures within the minute are handled silently (no further message), the pawn still never waits",
                RouterErrors.Handled > rxHandledAt && RouterErrors.Logged == rxLoggedAt && since < RouterErrors.LogIntervalSeconds && rxLongestWait <= 30,
                $"handled {RouterErrors.Handled - rxHandledAt} more, messages {RouterErrors.Logged - rxLoggedAt} more, {since:0.0} s since the first, longest wait {rxLongestWait} ticks");
            // RX4: a bill job up the stairs after the minute
            rxItem.Destroy();
            rxBench = Place(DefDatabase<ThingDef>.GetNamed("TableStonecutter"), ThingDefOf.WoodLog, RL(20, 15, 1, map), map, Rot4.South);
            ((Building_WorkTable)rxBench).BillStack.AddBill(DefDatabase<RecipeDef>.GetNamed("Make_StoneBlocksAny").MakeNewBill());
            Thing chunk = GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("ChunkGranite")), RG(18, 3), map);
            chunk.SetForbidden(false, false);
            R2Work(r2P, WorkTypeDefOf.Crafting);
            Teleport(r2P, RG(16, 3));
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            rxLoggedAt = RouterErrors.Logged;
            rxSawJob = false;
            Info($"routerfix: waiting for a real-time minute since the first message ({since:0.0} s so far); bench {rxBench} at {rxBench.Position}, chunk {chunk} at {chunk.Position}");
            TickUntil(400000, () =>
            {
                RxWatchWait();
                rxSawJob |= r2P.CurJobDef == JobDefOf.DoBill;
                return RouterErrors.Logged > rxLoggedAt;
            }, "rx_bill_check", 10);
        }

        private static void RxBillCheck(Map map)
        {
            tickWatch = null;
            RouterErrors.TestThrow = null;
            float since = Time.realtimeSinceStartup - rxFirstLogTime;
            string msg = RouterErrors.LastMessage ?? "";
            bool content = msg.Contains("DoBill") && msg.Contains("bill:") && msg.Contains("bench") && msg.Contains("TableStonecutter") && msg.Contains(r2P.LabelShort);
            Result("RX4 after more than a real-time minute a bill job up the stairs fails: a second full message with the bill and its bench",
                RouterErrors.Logged == rxLoggedAt + 1 && since >= RouterErrors.LogIntervalSeconds && content && rxLongestWait <= 30,
                $"bill job seen {rxSawJob}, messages {RouterErrors.Logged - rxLoggedAt}, {since:0.0} s after the first, content {content}, longest wait {rxLongestWait} ticks");
            Info("routerfix message 2:\n" + msg);
            Finish();
        }
    }
}
