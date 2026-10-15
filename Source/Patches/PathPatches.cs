using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Patches
{
    // Paths between floors (design §2.4 B step 3, §8 "path splicing"; idea credited to MigCorp - Skiptech, written here
    // from the 1.7 API only). The game's path finder only knows the 8 neighbours of a cell, so a path to another floor
    // is made of legs on single floors joined by stair steps (and, when no stairs lead there, jumps down):
    //   start -> stair high end (floor k), [stair step], exit (floor k+1) -> ... -> destination.
    // Each leg is a normal path from the game's path finder (FindPathNow, so its own costs, doors and avoid grid); the
    // legs are joined into one PawnPath. A stair step or jump is just two path nodes that are not neighbours: the path
    // follower sets pawn.Position = nextCell without checking adjacency, so the step needs no special job.
    // The destination and end mode are never changed, so arriving, failing, jobs and saves all see the real target.
    internal static class StairRouter
    {
        private static readonly AccessTools.FieldRef<PawnPath, float> CostRef = AccessTools.FieldRefAccess<PawnPath, float>("totalCostInt");
        private static readonly AccessTools.FieldRef<PawnPath, int> IndexRef = AccessTools.FieldRefAccess<PawnPath, int>("curNodeIndex");

        public const int StepCost = 120;    // path cost of one stair step
        public const int JumpCost = 2000;   // path cost of a jump: only when nothing else leads there
        private const int CandidatesPerStep = 3;
        private const int JumpSearchRegions = 400;

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
            if (busy || ProjectZ.Annex(map) == null)
            {
                return null;
            }
            busy = true;
            List<PawnPath> legs = new List<PawnPath>();
            try
            {
                // T5: a stairs thing touched from the floor above: the path ends on the stairwell's rim, on this floor.
                if (peMode == PathEndMode.Touch && dest.HasThing && StairTouch.Is(dest.Thing) && ProjectZ.FloorOf(start, map) == StairTouch.FloorOf(dest.Thing) + 1)
                {
                    PawnPath rim = RimPath(map, start, dest.Thing, tp, tuning);
                    if (rim != null)
                    {
                        return rim;
                    }
                }
                int cost = Route(map, start, dest, peMode, tp, tuning, legs, jumps: false);
                if (cost < 0 && ProjectZ.FloorOf(start, map) > ProjectZ.FloorOf(dest.Cell, map))
                {
                    // Pass 2 (J4): no stair route; going down, jumps are allowed.
                    DisposeAll(legs);
                    cost = Route(map, start, dest, peMode, tp, tuning, legs, jumps: true);
                }
                return cost < 0 ? null : Join(legs, cost);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Finding a path over the stairs failed: " + e, 81512301);
                return null;
            }
            finally
            {
                DisposeAll(legs);
                busy = false;
            }
        }

        private static void DisposeAll(List<PawnPath> legs)
        {
            for (int i = 0; i < legs.Count; i++)
            {
                legs[i].Dispose();
            }
            legs.Clear();
        }

        // Legs from start to dest, floor by floor; the total cost, or -1.
        private static int Route(Map map, IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, TraverseParms tp, PathFinderCostTuning? tuning, List<PawnPath> legs, bool jumps)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int fd = ProjectZ.FloorOf(dest.Cell, map);
            IntVec3 from = start;
            int cost = 0;
            for (int guard = 0; guard < 2 * a.floors + 2; guard++)
            {
                int f = ProjectZ.FloorOf(from, map);
                if (f == fd)
                {
                    PawnPath last = Leg(map, from, dest, peMode, tp, tuning);
                    if (last == null)
                    {
                        ProjectZ.DebugLog($"route {start} -> {dest.Cell} (jumps {jumps}): no last leg {from} -> {dest.Cell}");
                        return -1;
                    }
                    legs.Add(last);
                    return cost + (int)last.TotalCost;
                }
                int dir = fd > f ? 1 : -1;
                if (!StairStep(map, ref from, dir, dest, tp, tuning, legs, ref cost) && !(jumps && dir < 0 && JumpStep(map, ref from, dest, peMode, tp, tuning, legs, ref cost)))
                {
                    ProjectZ.DebugLog($"route {start} -> {dest.Cell} (jumps {jumps}): no step from {from} (floor {f}, dir {dir})");
                    return -1;
                }
            }
            return -1;
        }

        // To the floor above (dir 1) or below (-1) over stairs, best first by straight-line distance (8-way) via them.
        private static bool StairStep(Map map, ref IntVec3 from, int dir, LocalTargetInfo dest, TraverseParms tp, PathFinderCostTuning? tuning, List<PawnPath> legs, ref int cost)
        {
            StairGrid g = StairGrid.For(map);
            if (g == null)
            {
                return false;
            }
            int f = ProjectZ.FloorOf(from, map);
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
            IntVec3 destGround = ProjectZ.GroundCellOf(dest.Cell, map);
            cands.SortBy(l => Octile(fromGround, ProjectZ.GroundCellOf(l.bottom, map)) + Octile(ProjectZ.GroundCellOf(l.bottom, map), destGround));
            for (int i = 0; i < cands.Count && i < CandidatesPerStep; i++)
            {
                IntVec3 entry = dir > 0 ? cands[i].bottom : cands[i].top;
                IntVec3 exit = dir > 0 ? cands[i].top : cands[i].bottom;
                PawnPath leg = Leg(map, from, entry, PathEndMode.OnCell, tp, tuning);
                if (leg == null)
                {
                    ProjectZ.DebugLog($"stair step: no leg {from} -> {entry}");
                    continue;
                }
                legs.Add(leg);
                cost += (int)leg.TotalCost + StepCost;
                from = exit;
                return true;
            }
            return false;
        }

        // Down by a jump (pass 2): the jumps out of the regions reachable on this floor whose landing reaches the
        // destination, best first by straight-line distance via them; no landing on fire.
        private static bool JumpStep(Map map, ref IntVec3 from, LocalTargetInfo dest, PathEndMode peMode, TraverseParms tp, PathFinderCostTuning? tuning, List<PawnPath> legs, ref int cost)
        {
            JumpGrid jg = JumpGrid.For(map);
            Region start = from.GetRegion(map);
            if (jg == null || start == null)
            {
                return false;
            }
            List<Jump> cands = new List<Jump>();
            StairRegions.suppress++;
            try
            {
                RegionTraverser.BreadthFirstTraverse(start, (Region _, Region r) => r.Allows(tp, isDestination: false), delegate(Region r)
                {
                    cands.AddRange(jg.Down(r));
                    return false;
                }, JumpSearchRegions);
            }
            finally
            {
                StairRegions.suppress--;
            }
            if (cands.Count == 0)
            {
                return false;
            }
            IntVec3 fromGround = ProjectZ.GroundCellOf(from, map);
            IntVec3 destGround = ProjectZ.GroundCellOf(dest.Cell, map);
            cands.SortBy(j => Octile(fromGround, ProjectZ.GroundCellOf(j.from, map)) + Octile(ProjectZ.GroundCellOf(j.land, map), destGround));
            int tried = 0;
            for (int i = 0; i < cands.Count && tried < CandidatesPerStep; i++)
            {
                Jump j = cands[i];
                if (j.land.ContainsStaticFire(map) || !map.reachability.CanReach(j.land, dest, peMode, tp))
                {
                    continue;
                }
                tried++;
                PawnPath leg = Leg(map, from, j.from, PathEndMode.OnCell, tp, tuning);
                if (leg == null)
                {
                    continue;
                }
                legs.Add(leg);
                cost += (int)leg.TotalCost + JumpCost;
                from = j.land;
                Dev.PZStats.jumpRoutes++;
                return true;
            }
            return false;
        }

        // T5: to the nearest rim cell of a stairs thing that a leg reaches.
        private static PawnPath RimPath(Map map, IntVec3 start, Thing stairs, TraverseParms tp, PathFinderCostTuning? tuning)
        {
            List<IntVec3> cells = new List<IntVec3>();
            StairTouch.UpperCells(stairs, cells);
            cells.SortBy(c => Octile(start, c));
            for (int i = 0; i < cells.Count && i < CandidatesPerStep; i++)
            {
                PawnPath leg = Leg(map, start, cells[i], PathEndMode.OnCell, tp, tuning);
                if (leg != null)
                {
                    Dev.PZStats.rimRoutes++;
                    return leg;
                }
            }
            return null;
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

    // A stair step or jump takes its own time instead of a cell's walking cost (J5).
    [HarmonyPatch(typeof(Pawn_PathFollower), "SetupMoveIntoNextCell")]
    public static class SetupMoveIntoNextCell_Patch
    {
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> PawnRef = AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");

        public static void Postfix(Pawn_PathFollower __instance)
        {
            Pawn pawn = PawnRef(__instance);
            if (pawn == null)
            {
                return;
            }
            StepKind k = Links.Kind(pawn);
            if (k == StepKind.None)
            {
                return;
            }
            int ticks = k == StepKind.Jump ? JumpGrid.JumpTicks : Climb.StepTicks;
            __instance.nextCellCostTotal = ticks;
            __instance.nextCellCostLeft = ticks;
        }
    }

    // After the step the pawn is drawn at its new cell at once (no slide across the map between the floors' rows); after
    // a jump it lands (bruises, stagger: JumpGrid). Not pawn.Notify_Teleported: that ends the job.
    [HarmonyPatch(typeof(Pawn_PathFollower), "TryEnterNextPathCell")]
    public static class TryEnterNextPathCell_Patch
    {
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> PawnRef = AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");

        public struct Step
        {
            public IntVec3 from;
            public StepKind kind;
            public int fallen;
        }

        public static void Prefix(Pawn_PathFollower __instance, out Step __state)
        {
            __state = new Step { from = IntVec3.Invalid };
            Pawn pawn = PawnRef(__instance);
            if (pawn == null)
            {
                return;
            }
            StepKind k = Links.Kind(pawn, out int fallen);
            if (k != StepKind.None)
            {
                __state = new Step { from = pawn.Position, kind = k, fallen = fallen };
            }
        }

        public static void Postfix(Pawn_PathFollower __instance, Step __state)
        {
            if (!__state.from.IsValid)
            {
                return;
            }
            Pawn pawn = PawnRef(__instance);
            if (pawn == null || pawn.Position == __state.from)
            {
                return;
            }
            pawn.Drawer?.tweener?.Notify_Teleported();
            if (__state.kind == StepKind.Jump)
            {
                JumpGrid.For(pawn.Map)?.Landed(pawn, __state.fallen);
                Dev.TestRunner.CountJump(pawn);
                ProjectZ.DebugLog($"{pawn} jumped {__state.from} -> {pawn.Position} ({__state.fallen} floor(s))");
            }
            else
            {
                Dev.TestRunner.CountClimb(pawn);
                ProjectZ.DebugLog($"{pawn} took the stairs {__state.from} -> {pawn.Position}");
            }
        }
    }

    // During a stair step or jump the pawn moves across toward the next cell's column on its own floor's rows (IsoRim
    // raises or lowers it, Links.Rise).
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

    // A pawn saved in the middle of a stair step or jump: its next cell is not next to it. Start again from where it
    // stands; the path is joined again when it resumes.
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
