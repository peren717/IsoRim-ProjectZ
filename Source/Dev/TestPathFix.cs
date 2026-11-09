using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProjectZ.Patches;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode pathsave (pathfix, user save pathfindingBug.rws): -projectzsave=<save> [-projectzpspawn=Human212]
    // [-projectzpsthing=128,92] [-projectzpsdest=102,76] [-projectzsaveticks=N]. Loads the save (it needs the user's mod
    // list) and lets it run with real ticks. The watched pawn hauled a corpse back and forth for ever (option C took a
    // final stairs route dearer than the game's path, without a stair step); now the corpse must reach its cell. Every
    // pawn's path requests are counted per (pawn, start cell, destination): a pawn asking the same path again and again
    // from one cell is the loop's signature.
    public static partial class TestRunner
    {
        private static string psPawnId = "Human212";
        private static IntVec3 psThingAt = new IntVec3(128, 0, 92), psDestAt = new IntVec3(102, 0, 76);
        private static int psTicks = 6000, psStartTick, psDoneTick = -1, psRoutedAt, psFindsAt, psKeptAt, psWinsAt;
        private static double psMsAt;
        private static Pawn psPawn;
        private static Thing psThing;
        private static readonly Dictionary<(int pawn, IntVec3 at, IntVec3 dest), int> psRequests = new Dictionary<(int, IntVec3, IntVec3), int>();
        private static bool psOn;

        private static IntVec3 PsCell(string arg, IntVec3 def)
        {
            if (GenCommandLine.TryGetCommandLineArg(arg, out string s))
            {
                string[] p = s.Split(',');
                if (p.Length == 2 && int.TryParse(p[0], out int x) && int.TryParse(p[1], out int z))
                {
                    return new IntVec3(x, 0, z);
                }
            }
            return def;
        }

        private static bool StepPathSave(Map map)
        {
            switch (stage)
            {
                case "ps_load":
                    GenCommandLine.TryGetCommandLineArg("projectzsave", out saveName);
                    if (GenCommandLine.TryGetCommandLineArg("projectzpspawn", out string id))
                    {
                        psPawnId = id;
                    }
                    psThingAt = PsCell("projectzpsthing", psThingAt);
                    psDestAt = PsCell("projectzpsdest", psDestAt);
                    if (GenCommandLine.TryGetCommandLineArg("projectzsaveticks", out string st) && int.TryParse(st, out int n))
                    {
                        psTicks = n;
                    }
                    stage = "ps_loaded";
                    pendingLoad = saveName;
                    return true;
                case "ps_loaded":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    psPawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.ThingID == psPawnId);
                    psThing = psThingAt.GetThingList(map).FirstOrDefault(t => t is Corpse) ?? psThingAt.GetThingList(map).FirstOrDefault(t => t.def.EverHaulable);
                    Job j = psPawn?.CurJob;
                    Info($"loaded {saveName}: tick {Find.TickManager.TicksGame}, pawn {psPawn} at {psPawn?.Position} job {j?.def?.defName} A {Tgt(j?.targetA ?? LocalTargetInfo.Invalid)} B {Tgt(j?.targetB ?? LocalTargetInfo.Invalid)}; thing {psThing} at {psThingAt}");
                    if (psPawn == null || psThing == null)
                    {
                        Result("pathsave: the watched pawn and thing are in the save", false, $"pawn {psPawn}, thing {psThing}");
                        Finish();
                        return true;
                    }
                    Harmony h = new Harmony("Peren.IsoRimProjectZ.test.pathsave");
                    h.Patch(AccessTools.Method(typeof(Pawn_PathFollower), "GenerateNewPathRequest"), postfix: new HarmonyMethod(typeof(TestRunner), nameof(PsGenReqPostfix)) { priority = Priority.Last - 1 });
                    h.Patch(AccessTools.Method(typeof(StairRouter), nameof(StairRouter.Find)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(PsFindPrefix)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(PsFindPostfix)));
                    psOn = true;
                    psRequests.Clear();
                    psChecks.Clear();
                    psCheckMs = 0;
                    psStartTick = Find.TickManager.TicksGame;
                    psDoneTick = -1;
                    psRoutedAt = PZStats.sameFloorRouted;
                    psWinsAt = PZStats.sameFloorWins;
                    psFindsAt = PZStats.finds;
                    psMsAt = PZStats.findMsTotal;
                    psKeptAt = StairRouter.KeptDirect;
                    TickUntil(psTicks, () =>
                    {
                        if (psDoneTick < 0 && psThing.Spawned && psThing.Position == psDestAt)
                        {
                            psDoneTick = Find.TickManager.TicksGame;
                        }
                        return false;
                    }, "ps_end", 1);
                    return true;
                }
                case "ps_end":
                {
                    tickWatch = null;
                    psOn = false;
                    int elapsed = Find.TickManager.TicksGame - psStartTick;
                    var worst = psRequests.OrderByDescending(kv => kv.Value).Take(5).ToList();
                    int maxSame = worst.Count > 0 ? worst[0].Value : 0;
                    int pawnMax = psRequests.Where(kv => kv.Key.pawn == psPawn.thingIDNumber).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
                    string stats = $"same-floor checked over the stairs {PZStats.sameFloorRouted - psRoutedAt} (stairs taken {PZStats.sameFloorWins - psWinsAt}, game's path kept by the rule {StairRouter.KeptDirect - psKeptAt}), router finds {PZStats.finds - psFindsAt} ({PZStats.findMsTotal - psMsAt:0.0} ms, of them option C {psChecks.Count} finds {psCheckMs:0.0} ms), ticks {elapsed}";
                    Result($"pathsave: {psPawn.LabelShort} hauls {psThing.LabelShort} to {psDestAt} (no loop)", psDoneTick >= 0,
                        $"arrived at tick +{(psDoneTick < 0 ? -1 : psDoneTick - psStartTick)}; thing now {(psThing.Spawned ? psThing.Position.ToString() : "held by " + psThing.ParentHolder)}; pawn {psPawn.Position} job {psPawn.CurJob?.def?.defName}; {stats}");
                    Result("pathsave: no pawn asks the same path from the same cell more than 4 times", maxSame <= 4,
                        $"watched pawn's most {pawnMax}; top: {string.Join(" | ", worst.Select(kv => $"{map.mapPawns.AllPawns.FirstOrDefault(p => p.thingIDNumber == kv.Key.pawn)?.LabelShort ?? kv.Key.pawn.ToString()} {kv.Key.at}->{kv.Key.dest} x{kv.Value}"))}");
                    Info($"pathsave stats: {stats}; requests {psRequests.Values.Sum()} over {psRequests.Select(kv => kv.Key.pawn).Distinct().Count()} pawns");
                    foreach (string line in psChecks.Take(40))
                    {
                        Info("  option C check: " + line);
                    }
                    Finish();
                    return true;
                }
            }
            return false;
        }

        // option C checks (Find with the game's path): direct cost, the stairs' lower bound without and with cached legs, the answer
        private static readonly List<string> psChecks = new List<string>();
        private static string psPending;
        private static long psFindT0;
        private static double psCheckMs;

        private static void PsFindPrefix(Map map, IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, TraverseParms tp, PawnPath knownDirect)
        {
            psPending = null;
            if (!psOn || knownDirect == null || StairRouter.busy)
            {
                return;
            }
            StairRouter.TestBoundNoCache = true;
            float b0 = StairRouter.StairLowerBound(map, start, dest, peMode, tp);
            StairRouter.TestBoundNoCache = false;
            float b1 = StairRouter.StairLowerBound(map, start, dest, peMode, tp);
            psFindT0 = System.Diagnostics.Stopwatch.GetTimestamp();
            psPending = $"[{Find.TickManager.TicksGame - psStartTick}] {tp.pawn?.LabelShort} {start}->{Tgt(dest)} direct {knownDirect.TotalCost:0}, bound octile {b0:0} (x1.2 {(knownDirect.TotalCost > StairRouter.DetourFactor * b0 ? "<" : ">=")} direct), bound with cached legs {b1:0} (x1.2 {(knownDirect.TotalCost > StairRouter.DetourFactor * b1 ? "<" : ">=")} direct)";
        }

        private static void PsFindPostfix(PawnPath knownDirect, PawnPath __result)
        {
            if (psPending == null)
            {
                return;
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - psFindT0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            psCheckMs += ms;
            psChecks.Add($"{psPending}; {ms:0.0} ms; answer {(ReferenceEquals(__result, knownDirect) ? "game's path" : "stairs route")} (route cost {StairRouter.LastCost:0}, nodes {StairRouter.LastRoute.Count}, legs {StairRouter.LastLegs}, final {StairRouter.LastFinal})");
            psPending = null;
        }

        private static void PsGenReqPostfix(Pawn_PathFollower __instance)
        {
            if (!psOn)
            {
                return;
            }
            Pawn p = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            if (p == null)
            {
                return;
            }
            var k = (p.thingIDNumber, __instance.nextCell, __instance.Destination.Cell);
            psRequests.TryGetValue(k, out int n);
            psRequests[k] = n + 1;
        }
    }
}
