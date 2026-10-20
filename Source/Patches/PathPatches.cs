using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Patches
{
    // The hooks of the path follower and the path finder (the router itself: Router.cs; its graph: RouteGraph.cs).
    //
    // The path follower's request (GenerateNewPathRequest: a new job, a blocked path, a moved target): when the router is
    // needed (StairRouter.Needed: another floor, or not reachable on this floor alone) the game's request is cancelled
    // at once and an unanswered one is handed to the follower; the router answers it in RouteGraph's tick within a
    // per-tick budget (drafted pawns and forced jobs first). The follower waits for it as for the game's own requests.
    [HarmonyPatch(typeof(Pawn_PathFollower), "GenerateNewPathRequest")]
    public static class GenerateNewPathRequest_Patch
    {
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> PawnRef = AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, PathEndMode> ModeRef = AccessTools.FieldRefAccess<Pawn_PathFollower, PathEndMode>("peMode");

        [HarmonyPriority(Priority.Last)]   // final say between floors (FinalSay)
        public static void Postfix(Pawn_PathFollower __instance, ref PathRequest __result)
        {
            if (__result == null)
            {
                return;
            }
            Pawn pawn = PawnRef(__instance);
            Map map = pawn?.Map;
            LocalTargetInfo dest = __instance.Destination;
            PathEndMode mode = ModeRef(__instance);
            RouteGraph g = RouteGraph.For(map);
            if (g == null || !dest.IsValid)
            {
                return;
            }
            if (!StairRouter.Needed(map, __instance.nextCell, dest, mode, __result.TraverseParms))
            {
                // option C: a same-floor path is checked against the stairs' lower bound when it comes back
                Dev.PZStats.sameFloorRequests++;
                PatherTick_Detour_Patch.Watch(__instance, __result, map, __instance.nextCell, dest, mode);
                return;
            }
            PathRequest pushed = __result;
            PathRequest answered = map.pathFinder.CreateRequest(__instance.nextCell, dest, null, pawn, PathFinderCostTuning.For(pawn), mode);
            pushed.Dispose();
            g.Enqueue(new RouteGraph.Pending { pawn = pawn, req = answered, start = __instance.nextCell, dest = dest, mode = mode, tp = answered.TraverseParms, tuning = answered.Tuning });
            __result = answered;
            Dev.PZStats.splices++;
            if (ProjectZ.DebugOn)
            {
                ProjectZ.DebugLog($"{pawn} path {__instance.nextCell} -> {dest.Cell} over the stairs: queued");
            }
        }
    }

    // Option C (user 2026-10-19): a same-floor path the game found is compared, when it comes back, with the lower bound of
    // any route over the stairs (estimates only: no leg is run). Only when it costs more than DetourFactor x that bound is
    // the stairs route worked out (deferred, within the budget), and the cheaper of the two is walked. The common case
    // costs one small A* over estimates at request time and nothing else.
    [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.PatherTick))]
    public static class PatherTick_Detour_Patch
    {
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> PawnRef = AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");

        private sealed class Watched
        {
            public PathRequest req;
            public float bound;
            public IntVec3 start;
            public LocalTargetInfo dest;
            public PathEndMode mode;
        }

        private static readonly Dictionary<Pawn_PathFollower, Watched> watched = new Dictionary<Pawn_PathFollower, Watched>();

        internal static void Watch(Pawn_PathFollower pather, PathRequest req, Map map, IntVec3 start, LocalTargetInfo dest, PathEndMode mode)
        {
            float bound = StairRouter.StairLowerBound(map, start, dest, mode, req.TraverseParms);
            if (bound >= float.MaxValue)
            {
                watched.Remove(pather);
                return;
            }
            if (watched.Count > 2000)
            {
                watched.Clear();   // (followers of pawns long gone)
            }
            watched[pather] = new Watched { req = req, bound = bound, start = start, dest = dest, mode = mode };
        }

        public static void Prefix(Pawn_PathFollower __instance)
        {
            if (watched.Count == 0 || !watched.TryGetValue(__instance, out Watched w))
            {
                return;
            }
            PathRequest req = __instance.curPathRequest;
            if (req != w.req)
            {
                if (req == null || w.req.Cancelled || w.req.ResultIsReady)
                {
                    watched.Remove(__instance);
                }
                return;
            }
            if (!req.TryGetPath(out PawnPath path))
            {
                return;
            }
            watched.Remove(__instance);
            if (path == null || !path.Found || path.TotalCost <= StairRouter.DetourFactor * w.bound)
            {
                return;
            }
            Pawn pawn = PawnRef(__instance);
            Map map = pawn?.Map;
            RouteGraph g = RouteGraph.For(map);
            if (g == null || pawn == null)
            {
                return;
            }
            // the game's path is kept for the comparison; the follower waits for the answer
            req.ClaimCalculatedPath();
            PathRequest answered = map.pathFinder.CreateRequest(w.start, w.dest, null, pawn, PathFinderCostTuning.For(pawn), w.mode);
            __instance.curPathRequest = answered;
            req.Dispose();
            g.Enqueue(new RouteGraph.Pending { pawn = pawn, req = answered, start = w.start, dest = w.dest, mode = w.mode, tp = answered.TraverseParms, tuning = answered.Tuning, vanilla = path });
            Dev.PZStats.sameFloorRouted++;
        }

        internal static void Clear() => watched.Clear();
    }

    // Everything else that asks for a path at once (AI sappers, breachers, manhunters, exit-map finders ...): routed at
    // once (no budget: the caller needs the answer), or not found, instead of a full-map search that must fail. A
    // same-floor path the game found gets option C's check too.
    [HarmonyPatch(typeof(PathFinder), nameof(PathFinder.FindPathNow), new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathFinderCostTuning?), typeof(PathEndMode), typeof(PathRequest.IPathGridCustomizer) })]
    public static class FindPathNow_Stairs_Patch
    {
        private static readonly AccessTools.FieldRef<PathFinder, Map> MapRef = AccessTools.FieldRefAccess<PathFinder, Map>("map");

        // The routed path; null: the game's search.
        private static PawnPath Decide(PathFinder finder, IntVec3 start, LocalTargetInfo target, TraverseParms traverseParms, PathFinderCostTuning? tuning, PathEndMode peMode)
        {
            if (StairRouter.busy || !target.IsValid)
            {
                return null;
            }
            Map map = MapRef(finder);
            if (map == null || !StairRouter.Needed(map, start, target, peMode, traverseParms))
            {
                return null;
            }
            Dev.PZStats.findNow++;
            return StairRouter.Find(map, start, target, peMode, traverseParms, tuning) ?? PawnPath.NotFound;
        }

        // __state: ran = the prefix ran; path = the path it joined (null: the game's search).
        [HarmonyPriority(Priority.High)]
        public static bool Prefix(PathFinder __instance, IntVec3 start, LocalTargetInfo target, TraverseParms traverseParms, PathFinderCostTuning? tuning, PathEndMode peMode, ref PawnPath __result, out (bool ran, PawnPath path) __state)
        {
            PawnPath p = Decide(__instance, start, target, traverseParms, tuning, peMode);
            __state = (true, p);
            if (p == null)
            {
                return true;
            }
            __result = p;
            return false;
        }

        // Final say between floors (FinalSay): the routed path stands (routed here when another mod's prefix skipped ours).
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(PathFinder __instance, IntVec3 start, LocalTargetInfo target, TraverseParms traverseParms, PathFinderCostTuning? tuning, PathEndMode peMode, ref PawnPath __result, (bool ran, PawnPath path) __state)
        {
            if (!__state.ran)
            {
                FinalSay.PrefixSkipped++;
                __state = (true, Decide(__instance, start, target, traverseParms, tuning, peMode));
            }
            if (__state.path != null)
            {
                FinalSay.Value(true, __state.path, ref __result);
                return;
            }
            // option C for the paths found at once
            if (StairRouter.busy || __result == null || !__result.Found || !target.IsValid)
            {
                return;
            }
            Map map = MapRef(__instance);
            if (RouteGraph.For(map) == null || StairGrid.For(map) == null)
            {
                return;
            }
            float bound = StairRouter.StairLowerBound(map, start, target, peMode, traverseParms);
            if (bound >= float.MaxValue || __result.TotalCost <= StairRouter.DetourFactor * bound)
            {
                return;
            }
            Dev.PZStats.sameFloorRouted++;
            PawnPath vanilla = __result;
            PawnPath p = StairRouter.Find(map, start, target, peMode, traverseParms, tuning, vanilla);
            if (p != null && !ReferenceEquals(p, vanilla))
            {
                vanilla.Dispose();
                __result = p;
            }
        }
    }

    // A stair step takes its own time instead of a cell's walking cost (J5).
    [HarmonyPatch(typeof(Pawn_PathFollower), "SetupMoveIntoNextCell")]
    public static class SetupMoveIntoNextCell_Patch
    {
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> PawnRef = AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");

        public static void Postfix(Pawn_PathFollower __instance)
        {
            Pawn pawn = PawnRef(__instance);
            if (pawn == null || Links.Kind(pawn) == StepKind.None)
            {
                return;
            }
            __instance.nextCellCostTotal = Climb.StepTicks;
            __instance.nextCellCostLeft = Climb.StepTicks;
        }
    }

    // After the step the pawn is drawn at its new cell at once (no slide across the map between the floors' rows). Not
    // pawn.Notify_Teleported: that ends the job.
    [HarmonyPatch(typeof(Pawn_PathFollower), "TryEnterNextPathCell")]
    public static class TryEnterNextPathCell_Patch
    {
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> PawnRef = AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");

        public static void Prefix(Pawn_PathFollower __instance, out IntVec3 __state)
        {
            __state = IntVec3.Invalid;
            Pawn pawn = PawnRef(__instance);
            if (pawn != null && Links.Kind(pawn) != StepKind.None)
            {
                __state = pawn.Position;
            }
        }

        public static void Postfix(Pawn_PathFollower __instance, IntVec3 __state)
        {
            if (!__state.IsValid)
            {
                return;
            }
            Pawn pawn = PawnRef(__instance);
            if (pawn == null || pawn.Position == __state)
            {
                return;
            }
            pawn.Drawer?.tweener?.Notify_Teleported();
            Dev.TestRunner.CountClimb(pawn);
            ProjectZ.DebugLog($"{pawn} took the stairs {__state} -> {pawn.Position}");
        }
    }

    // During a stair step the pawn moves across toward the next cell's column on its own floor's rows (IsoRim raises or
    // lowers it, Links.Rise).
    [HarmonyPatch(typeof(PawnTweener), "TweenedPosRoot")]
    public static class TweenedPosRoot_Patch
    {
        private static readonly AccessTools.FieldRef<PawnTweener, Pawn> PawnRef = AccessTools.FieldRefAccess<PawnTweener, Pawn>("pawn");

        public static void Postfix(PawnTweener __instance, ref Vector3 __result)
        {
            Pawn pawn = PawnRef(__instance);
            if (pawn == null || !Links.StepTween(pawn, out Vector3 at))
            {
                return;
            }
            __result = new Vector3(at.x, __result.y, at.z);
        }
    }

    // A pawn saved in the middle of a stair step (or of a jump, in saves made before jumps were removed): its next cell
    // is not next to it. Start again from where it stands; the path is joined again when it resumes.
    // Patched on Pawn.PostMapInit, which calls it: the one-line TryResumePathingAfterLoading can be inlined.
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.PostMapInit))]
    public static class TryResumePathingAfterLoading_Patch
    {
        public static void Prefix(Pawn __instance)
        {
            Pawn pawn = __instance;
            Pawn_PathFollower pather = pawn.pather;
            if (pather == null || !pawn.Spawned)
            {
                return;
            }
            IntVec3 next = pather.nextCell;
            IntVec3 pos = pawn.Position;
            if (next.IsValid && (Math.Abs(next.x - pos.x) > 1 || Math.Abs(next.z - pos.z) > 1))
            {
                ProjectZ.DebugLog($"{pawn} was saved on a stair step or jump ({pos} -> {next}): starting again from {pos}");
                pather.nextCell = pos;
                pather.nextCellCostLeft = 0f;
                pather.nextCellCostTotal = 1f;
                Dev.PZStats.climbResets++;
            }
        }
    }
}
