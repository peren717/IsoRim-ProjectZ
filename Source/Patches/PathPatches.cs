using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Patches
{
    // Paths between floors (design §2.4 B step 3, §8 "path splicing"; idea credited to MigCorp - Skiptech, written here
    // from the 1.7 API only). The game's path finder only knows the 8 neighbours of a cell, so a path to another floor
    // is made of legs on single floors joined by stair steps:
    //   start -> stair bottom (floor k), [stair step], top (floor k+1) -> ... -> destination.
    // Each leg is a normal path from the game's path finder (FindPathNow, so its own costs, doors and avoid grid); the
    // legs are joined into one PawnPath. A stair step is just two path nodes that are not neighbours: the path
    // follower sets pawn.Position = nextCell without checking adjacency, so the step needs no special job.
    // The destination and end mode are never changed, so arriving, failing, jobs and saves all see the real target.
    internal static class StairRouter
    {
        private static readonly AccessTools.FieldRef<PawnPath, float> CostRef = AccessTools.FieldRefAccess<PawnPath, float>("totalCostInt");
        private static readonly AccessTools.FieldRef<PawnPath, int> IndexRef = AccessTools.FieldRefAccess<PawnPath, int>("curNodeIndex");

        public const int StepCost = 120;   // path cost of one stair step
        private const int CandidatesPerStep = 3;

        // Set while legs are worked out (the legs never cross floors, but be safe against re-entry).
        [ThreadStatic]
        internal static bool busy;

        // Both ends on real floors of an annex map, on different floors.
        public static bool CrossFloor(Map map, IntVec3 start, IntVec3 dest)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || !start.IsValid || !dest.IsValid || start.z < a.groundZ && dest.z < a.groundZ)
            {
                return false;
            }
            int fs = ProjectZ.FloorOf(start, map);
            int fd = ProjectZ.FloorOf(dest, map);
            return fs >= 0 && fd >= 0 && fs != fd;
        }

        // The joined path, or null when there is none. tuning/traverseParms as the caller's request.
        public static PawnPath Find(Map map, IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, TraverseParms tp, PathFinderCostTuning? tuning)
        {
            StairGrid g = StairGrid.For(map);
            if (g == null || busy)
            {
                return null;
            }
            busy = true;
            List<PawnPath> legs = new List<PawnPath>();
            try
            {
                int fs = ProjectZ.FloorOf(start, map);
                int fd = ProjectZ.FloorOf(dest.Cell, map);
                int dir = fd > fs ? 1 : -1;
                IntVec3 from = start;
                IntVec3 destGround = ProjectZ.GroundCellOf(dest.Cell, map);
                int cost = 0;
                for (int f = fs; f != fd; f += dir)
                {
                    // Stairs between floor f and f + dir, best first by straight-line distance (8-way) via them.
                    List<StairLink> cands = new List<StairLink>();
                    for (int i = 0; i < g.links.Count; i++)
                    {
                        StairLink l = g.links[i];
                        if ((dir > 0 ? l.floor == f : l.floor == f - 1) && g.Usable(l))
                        {
                            cands.Add(l);
                        }
                    }
                    IntVec3 fromGround = ProjectZ.GroundCellOf(from, map);
                    cands.SortBy(l => Octile(fromGround, ProjectZ.GroundCellOf(l.bottom, map)) + Octile(ProjectZ.GroundCellOf(l.bottom, map), destGround));
                    bool stepped = false;
                    for (int i = 0; i < cands.Count && i < CandidatesPerStep; i++)
                    {
                        IntVec3 entry = dir > 0 ? cands[i].bottom : cands[i].top;
                        IntVec3 exit = dir > 0 ? cands[i].top : cands[i].bottom;
                        PawnPath leg = Leg(map, from, entry, PathEndMode.OnCell, tp, tuning);
                        if (leg == null)
                        {
                            continue;
                        }
                        legs.Add(leg);
                        cost += (int)leg.TotalCost + StepCost;
                        from = exit;
                        stepped = true;
                        break;
                    }
                    if (!stepped)
                    {
                        return null;
                    }
                }
                PawnPath last = Leg(map, from, dest, peMode, tp, tuning);
                if (last == null)
                {
                    return null;
                }
                legs.Add(last);
                cost += (int)last.TotalCost;
                return Join(legs, cost);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Finding a path over the stairs failed: " + e, 81512301);
                return null;
            }
            finally
            {
                for (int i = 0; i < legs.Count; i++)
                {
                    legs[i].Dispose();
                }
                busy = false;
            }
        }

        private static int Octile(IntVec3 a, IntVec3 b)
        {
            int dx = Math.Abs(a.x - b.x);
            int dz = Math.Abs(a.z - b.z);
            return 10 * Math.Max(dx, dz) + 4 * Math.Min(dx, dz);
        }

        // One leg on a single floor; a one-node path when already there.
        private static PawnPath Leg(Map map, IntVec3 from, LocalTargetInfo to, PathEndMode peMode, TraverseParms tp, PathFinderCostTuning? tuning)
        {
            if (peMode == PathEndMode.OnCell && to.Cell == from)
            {
                PawnPath one = new PawnPath();
                one.AddNode(from);
                IndexRef(one) = 0;
                CostRef(one) = 0f;
                return one;
            }
            PawnPath p = map.pathFinder.FindPathNow(from, to, tp, tuning, peMode);
            if (p == null || !p.Found || p.NodesReversed.Count == 0)
            {
                p?.Dispose();
                return null;
            }
            return p;
        }

        // Legs in walking order -> one path: nodes are stored last first, so the last leg's nodes come first.
        private static PawnPath Join(List<PawnPath> legs, int cost)
        {
            PawnPath joined = new PawnPath();
            for (int i = legs.Count - 1; i >= 0; i--)
            {
                List<IntVec3> nodes = legs[i].NodesReversed;
                for (int n = 0; n < nodes.Count; n++)
                {
                    joined.AddNode(nodes[n]);
                }
            }
            IndexRef(joined) = joined.NodesReversed.Count - 1;
            CostRef(joined) = cost;
            return joined;
        }
    }

    // The path follower's request: a path to another floor is joined from legs right away, and the follower gets an
    // already answered request. The game's own request (pushed just before) is cancelled at once, so no full-map
    // search is run for a target it cannot reach on its own. Every new request (NeedNewPath every 30 ticks, a blocked
    // path, a moved target) comes through here again, so a path is joined again whenever the game would search again.
    [HarmonyPatch(typeof(Pawn_PathFollower), "GenerateNewPathRequest")]
    public static class GenerateNewPathRequest_Patch
    {
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> PawnRef = AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, PathEndMode> ModeRef = AccessTools.FieldRefAccess<Pawn_PathFollower, PathEndMode>("peMode");

        public static void Postfix(Pawn_PathFollower __instance, ref PathRequest __result)
        {
            if (__result == null)
            {
                return;
            }
            Pawn pawn = PawnRef(__instance);
            Map map = pawn?.Map;
            LocalTargetInfo dest = __instance.Destination;
            if (map == null || !dest.IsValid || !StairRouter.CrossFloor(map, __instance.nextCell, dest.Cell))
            {
                return;
            }
            PathRequest pushed = __result;
            PathEndMode mode = ModeRef(__instance);
            PathRequest answered = map.pathFinder.CreateRequest(__instance.nextCell, dest, null, pawn, PathFinderCostTuning.For(pawn), mode);
            pushed.Dispose();
            PawnPath path = StairRouter.Find(map, __instance.nextCell, dest, mode, answered.TraverseParms, answered.Tuning);
            answered.Resolve(path);
            __result = answered;
            Dev.PZStats.splices++;
            if (path == null)
            {
                Dev.PZStats.spliceFails++;
            }
            if (ProjectZ.DebugOn)
            {
                ProjectZ.DebugLog($"{pawn} path {__instance.nextCell} -> {dest.Cell} over the stairs: {(path != null ? path.NodesReversed.Count + " nodes, cost " + path.TotalCost : "none")}");
            }
        }
    }

    // Everything else that asks for a path at once (AI sappers, breachers, manhunters, exit-map finders ...): joined
    // the same way, or not found, instead of a full-map search that must fail.
    [HarmonyPatch(typeof(PathFinder), nameof(PathFinder.FindPathNow), new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathFinderCostTuning?), typeof(PathEndMode), typeof(PathRequest.IPathGridCustomizer) })]
    public static class FindPathNow_Stairs_Patch
    {
        private static readonly AccessTools.FieldRef<PathFinder, Map> MapRef = AccessTools.FieldRefAccess<PathFinder, Map>("map");

        [HarmonyPriority(Priority.High)]
        public static bool Prefix(PathFinder __instance, IntVec3 start, LocalTargetInfo target, TraverseParms traverseParms, PathFinderCostTuning? tuning, PathEndMode peMode, ref PawnPath __result)
        {
            if (StairRouter.busy || !target.IsValid)
            {
                return true;
            }
            Map map = MapRef(__instance);
            if (map == null || !StairRouter.CrossFloor(map, start, target.Cell))
            {
                return true;
            }
            __result = StairRouter.Find(map, start, target, peMode, traverseParms, tuning) ?? PawnPath.NotFound;
            Dev.PZStats.findNow++;
            return false;
        }
    }

    // The stair step takes Climb.StepTicks instead of a cell's walking cost.
    [HarmonyPatch(typeof(Pawn_PathFollower), "SetupMoveIntoNextCell")]
    public static class SetupMoveIntoNextCell_Patch
    {
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> PawnRef = AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");

        public static void Postfix(Pawn_PathFollower __instance)
        {
            Pawn pawn = PawnRef(__instance);
            if (pawn == null || Climb.Direction(pawn) == 0)
            {
                return;
            }
            __instance.nextCellCostTotal = Climb.StepTicks;
            __instance.nextCellCostLeft = Climb.StepTicks;
        }
    }

    // After the step the pawn is drawn at its new cell at once (no slide across the map between the floors' rows).
    // Not pawn.Notify_Teleported: that ends the job.
    [HarmonyPatch(typeof(Pawn_PathFollower), "TryEnterNextPathCell")]
    public static class TryEnterNextPathCell_Patch
    {
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> PawnRef = AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");

        public static void Prefix(Pawn_PathFollower __instance, out IntVec3 __state)
        {
            Pawn pawn = PawnRef(__instance);
            __state = pawn != null && Climb.Direction(pawn) != 0 ? pawn.Position : IntVec3.Invalid;
        }

        public static void Postfix(Pawn_PathFollower __instance, IntVec3 __state)
        {
            if (!__state.IsValid)
            {
                return;
            }
            Pawn pawn = PawnRef(__instance);
            if (pawn != null && pawn.Position != __state)
            {
                pawn.Drawer?.tweener?.Notify_Teleported();
                Dev.TestRunner.CountClimb(pawn);
                ProjectZ.DebugLog($"{pawn} took the stairs {__state} -> {pawn.Position}");
            }
        }
    }

    // During the step the pawn stays over its stair cell (IsoRim raises it as it climbs, IsoRimBridge.ThingOffset).
    [HarmonyPatch(typeof(PawnTweener), "TweenedPosRoot")]
    public static class TweenedPosRoot_Patch
    {
        private static readonly AccessTools.FieldRef<PawnTweener, Pawn> PawnRef = AccessTools.FieldRefAccess<PawnTweener, Pawn>("pawn");

        public static void Postfix(PawnTweener __instance, ref Vector3 __result)
        {
            Pawn pawn = PawnRef(__instance);
            if (pawn == null || Climb.Direction(pawn) == 0)
            {
                return;
            }
            Vector3 p = pawn.Position.ToVector3Shifted();
            __result = new Vector3(p.x, __result.y, p.z);
        }
    }

    // A pawn saved in the middle of a stair step: its next cell (the other end) is not next to it. Start again from
    // where it stands; the path is joined again when it resumes.
    // Patched on Pawn.PostMapInit, which calls it: the one-line TryResumePathingAfterLoading can be inlined.
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.PostMapInit))]
    public static class TryResumePathingAfterLoading_Patch
    {
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> PawnRef = AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");

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
                ProjectZ.DebugLog($"{pawn} was saved on a stair step ({pos} -> {next}): starting again from {pos}");
                pather.nextCell = pos;
                pather.nextCellCostLeft = 0f;
                pather.nextCellCostTotal = 1f;
                Dev.PZStats.climbResets++;
            }
        }
    }
}
