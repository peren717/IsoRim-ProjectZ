using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace ProjectZ.Patches
{
    // Reachability between floors (isorim-pz-pathing-redesign.md: "the same graph and the same rules as the paths").
    // Floors are joined by stairs only, both ways (no jumps: user 2026-10-19). Nothing is written into region.links, so
    // districts, rooms and temperature stay per floor: the two floods that build districts see no stairs at all.
    //   - Reachability.CheckRegionBasedReachability on a map with floors (the game has already picked its start and
    //     destination regions and asked its cache): the parts' components first (different components: no, at once);
    //     then the game's own search on the floor when start and destination share a part; then the stairs' ends
    //     (RouteGraph.StairReach), each floor question asked of the floor-only instance (Reach2).
    //   - The game's region searches (GenClosest, job scans, flee and exit searches ...): each dequeued region's stair
    //     partners are queued like linked neighbours (BFSWorker_Patch).
    //   - Traversal modes that break walls, between floors: their dig parts' components (CheckCellBasedReachability).
    // Fog is no obstacle to reach (vanilla: fog only adds path cost, avoidFog).
    internal static class StairRegions
    {
        // > 0 while districts are being built, or while a search must stay on one floor (no stairs then).
        [ThreadStatic]
        internal static int suppress;

        // The regions one stair step away from 'region' (either way); null for most regions.
        internal static List<Region> Partners(Region region, Map map)
        {
            return RouteGraph.For(map)?.PartnersOf(region);
        }

        // The regions a usable stairs link joins (bottom b = H's, top t = E's; false when unusable or one region), read by
        // the reveal walk (ReachFog), so both see the same stairs.
        internal static bool LinkRegions(StairGrid g, StairLink l, RegionGrid rg, out Region b, out Region t)
        {
            b = t = null;
            if (!Dev.TestRunner.NoExitFix && !g.Usable(l))
            {
                return false;
            }
            b = rg.GetValidRegionAt_NoRebuild(l.bottom);
            t = rg.GetValidRegionAt_NoRebuild(l.top);
            return b != null && t != null && b != t;
        }
    }

    [HarmonyPatch(typeof(RegionTraverser), nameof(RegionTraverser.FloodAndSetDistricts))]
    public static class FloodAndSetDistricts_Patch
    {
        public static void Prefix()
        {
            StairRegions.suppress++;
        }

        public static void Finalizer()
        {
            StairRegions.suppress--;
        }
    }

    [HarmonyPatch(typeof(RegionTraverser), nameof(RegionTraverser.FloodAndSetNewRegionIndex))]
    public static class FloodAndSetNewRegionIndex_Patch
    {
        public static void Prefix()
        {
            StairRegions.suppress++;
        }

        public static void Finalizer()
        {
            StairRegions.suppress--;
        }
    }

    // Region breadth-first searches (GenClosest, job scans, flee and exit searches, CanReach's helpers ...): right after
    // a region is taken from the open queue, its stair partners are queued like linked neighbours (same entry test, J2).
    [HarmonyPatch]
    public static class BFSWorker_Patch
    {
        private static Type workerType;
        private static FieldInfo closedArrayPosF;
        private static FieldInfo closedIndexF;
        private static MethodInfo queueM;

        public static MethodBase TargetMethod()
        {
            workerType = AccessTools.Inner(typeof(RegionTraverser), "BFSWorker");
            closedArrayPosF = AccessTools.Field(workerType, "closedArrayPos");
            closedIndexF = AccessTools.Field(workerType, "closedIndex");
            queueM = AccessTools.Method(workerType, "QueueNewOpenRegion");
            return AccessTools.Method(workerType, "BreadthFirstTraverseWork");
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo dequeue = AccessTools.Method(typeof(Queue<Region>), nameof(Queue<Region>.Dequeue));
            MethodInfo hook = AccessTools.Method(typeof(BFSWorker_Patch), nameof(AfterDequeue));
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int done = 0;
            for (int i = 0; i + 1 < list.Count; i++)
            {
                if ((list[i].opcode == OpCodes.Callvirt || list[i].opcode == OpCodes.Call) && list[i].operand as MethodInfo == dequeue && list[i + 1].IsStloc())
                {
                    int local = list[i + 1].LocalIndex();
                    list.InsertRange(i + 2, new[]
                    {
                        new CodeInstruction(OpCodes.Ldarg_0),
                        CodeInstruction.LoadLocal(local),
                        new CodeInstruction(OpCodes.Ldarg_2),
                        new CodeInstruction(OpCodes.Ldarg_S, (byte)5),
                        new CodeInstruction(OpCodes.Call, hook),
                    });
                    done++;
                    break;
                }
            }
            if (done != 1)
            {
                Log.Error("[ProjectZ] RegionTraverser.BFSWorker.BreadthFirstTraverseWork is not in the expected form: stairs are left out of region searches.");
            }
            return list;
        }

        public static void AfterDequeue(object worker, Region region, RegionEntryPredicate entryCondition, RegionType types)
        {
            if (StairRegions.suppress > 0 || region == null)
            {
                return;
            }
            Map map = region.Map;
            if (ProjectZ.Annex(map) == null)
            {
                return;
            }
            List<Region> partners = StairRegions.Partners(region, map);
            if (partners == null)
            {
                return;
            }
            int pos = (int)closedArrayPosF.GetValue(worker);
            uint closed = (uint)closedIndexF.GetValue(worker);
            for (int i = 0; i < partners.Count; i++)
            {
                Region r = partners[i];
                if (r.valid && r.closedIndex[pos] != closed && (r.type & types) != RegionType.None && (entryCondition == null || entryCondition(region, r)))
                {
                    queueM.Invoke(worker, new object[] { r });
                }
            }
        }
    }

    // ProjectZ's own entries in the game's reachability cache, kept apart from the game's (directed keys: from, to |
    // 1 << 30, never equal to a sorted vanilla key), so another mod's or the game's entries made without the stairs are
    // never read for a question that needed them. Every way the game clears its cache clears these too.
    internal static class DirCache
    {
        private const int Directed = 1 << 30;
        private static Func<ReachabilityCache, int, int, TraverseParms, int> get;
        private static Action<ReachabilityCache, int, int, TraverseParms, bool> add;
        private static bool tried;
        internal static bool Ok;

        internal static void Init()
        {
            if (tried)
            {
                return;
            }
            tried = true;
            try
            {
                Type entryT = AccessTools.Inner(typeof(ReachabilityCache), "CachedEntry");
                FieldInfo dictF = AccessTools.Field(typeof(ReachabilityCache), "cacheDict");
                ConstructorInfo ctor = AccessTools.Constructor(entryT, new[] { typeof(int), typeof(int), typeof(TraverseParms) });
                Type dictT = dictF.FieldType;
                ParameterExpression cache = Expression.Parameter(typeof(ReachabilityCache), "cache");
                ParameterExpression a = Expression.Parameter(typeof(int), "a");
                ParameterExpression b = Expression.Parameter(typeof(int), "b");
                ParameterExpression tp = Expression.Parameter(typeof(TraverseParms), "tp");
                ParameterExpression r = Expression.Parameter(typeof(bool), "r");
                ParameterExpression v = Expression.Variable(typeof(bool), "v");
                Expression dict = Expression.Field(cache, dictF);
                Expression key = Expression.New(ctor, a, b, tp);
                MethodInfo tryGet = dictT.GetMethod("TryGetValue");
                Expression body = Expression.Block(typeof(int), new[] { v },
                    Expression.Condition(Expression.Call(dict, tryGet, key, v),
                        Expression.Condition(v, Expression.Constant(1), Expression.Constant(0)),
                        Expression.Constant(-1)));
                get = Expression.Lambda<Func<ReachabilityCache, int, int, TraverseParms, int>>(body, cache, a, b, tp).Compile();
                MethodInfo tryAdd = dictT.GetMethod("TryAdd");
                add = Expression.Lambda<Action<ReachabilityCache, int, int, TraverseParms, bool>>(Expression.Call(dict, tryAdd, key, r), cache, a, b, tp, r).Compile();
                Ok = true;
            }
            catch (Exception e)
            {
                Log.Error("[ProjectZ] The reachability cache is not in the expected form: reachability on maps with floors is not cached. " + e);
            }
        }

        // 1 true, 0 false, -1 unknown.
        internal static int Get(ReachabilityCache c, District from, District to, TraverseParms tp)
        {
            return Ok && from != null && to != null ? get(c, from.ID, to.ID | Directed, tp) : -1;
        }

        internal static void Add(ReachabilityCache c, District from, District to, TraverseParms tp, bool reachable)
        {
            if (Ok && from != null && to != null)
            {
                add(c, from.ID, to.ID | Directed, tp, reachable);
            }
        }
    }

    internal static class ReachRefs
    {
        internal static readonly AccessTools.FieldRef<Reachability, Map> MapRef = AccessTools.FieldRefAccess<Reachability, Map>("map");
        internal static readonly AccessTools.FieldRef<Reachability, Queue<Region>> OpenRef = AccessTools.FieldRefAccess<Reachability, Queue<Region>>("openQueue");
        internal static readonly AccessTools.FieldRef<Reachability, List<Region>> StartRef = AccessTools.FieldRefAccess<Reachability, List<Region>>("startingRegions");
        internal static readonly AccessTools.FieldRef<Reachability, List<Region>> DestRef = AccessTools.FieldRefAccess<Reachability, List<Region>>("destRegions");
        internal static readonly AccessTools.FieldRef<Reachability, uint> ReachedRef = AccessTools.FieldRefAccess<Reachability, uint>("reachedIndex");
        internal static readonly AccessTools.FieldRef<Reachability, ReachabilityCache> CacheRef = AccessTools.FieldRefAccess<Reachability, ReachabilityCache>("cache");
        internal static readonly AccessTools.FieldRef<Reachability, bool> WorkingRef = AccessTools.FieldRefAccess<Reachability, bool>("working");
    }

    // The question being answered (CanReach's own arguments): the stairs search asks the floor-only instance the same
    // question from each stairs end. Read at the start of the search (nested CanReach calls overwrite it).
    internal static class ReachCtx
    {
        internal static IntVec3 start;
        internal static LocalTargetInfo dest;
        internal static PathEndMode mode;
        internal static bool valid;
    }

    [HarmonyPatch(typeof(Reachability), nameof(Reachability.CanReach), new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) })]
    public static class CanReach_Ctx_Patch
    {
        public static void Prefix(IntVec3 start, LocalTargetInfo dest, PathEndMode peMode)
        {
            ReachCtx.start = start;
            ReachCtx.dest = dest;
            ReachCtx.mode = peMode;
            ReachCtx.valid = true;
        }
    }

    // Final say between floors (FinalSay) for the two reachability hooks: a check whose start and destination regions are
    // on different floors keeps ProjectZ's answer.
    internal static class ReachFinal
    {
        public static bool CrossFloor(Reachability r)
        {
            Map map = ReachRefs.MapRef(r);
            List<Region> starts = ReachRefs.StartRef(r);
            List<Region> dests = ReachRefs.DestRef(r);
            if (starts.Count == 0 || dests.Count == 0)
            {
                return false;
            }
            int ks = FloorOf(starts[0], map);
            for (int i = 0; i < dests.Count; i++)
            {
                if (FloorOf(dests[i], map) != ks)
                {
                    return true;
                }
            }
            return false;
        }

        private static int FloorOf(Region r, Map map) => ProjectZ.FloorOf(new IntVec3(r.extentsClose.minX, 0, r.extentsClose.minZ), map);
    }

    // The cache read on maps with floors: ProjectZ's entries only; and "no route" remembered by the router (RouteGraph
    // memo) answers no for that traveller meanwhile.
    [HarmonyPatch(typeof(Reachability), "GetCachedResult")]
    public static class GetCachedResult_Patch
    {
        // __state: 0 the prefix did not run, 1 not ours (no floors, one floor), 2 + the answer between floors.
        public static bool Prefix(Reachability __instance, TraverseParms traverseParams, ref BoolUnknown __result, out byte __state)
        {
            __state = FinalSay.Pass;
            if (RouteGraph.IsFloorOnly(__instance) || ProjectZ.Annex(ReachRefs.MapRef(__instance)) == null)
            {
                return true;
            }
            bool cross = ReachFinal.CrossFloor(__instance);
            bool run = Run(__instance, traverseParams, ref __result);
            if (cross)
            {
                __state = (byte)(2 + (int)__result);
            }
            return run;
        }

        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Reachability __instance, TraverseParms traverseParams, ref BoolUnknown __result, byte __state)
        {
            if (__state == FinalSay.NotRun && !RouteGraph.IsFloorOnly(__instance) && ProjectZ.Annex(ReachRefs.MapRef(__instance)) != null && ReachFinal.CrossFloor(__instance))
            {
                // another mod's prefix skipped ours: read the cache here
                FinalSay.PrefixSkipped++;
                BoolUnknown mine = BoolUnknown.Unknown;
                Run(__instance, traverseParams, ref mine);
                __state = (byte)(2 + (int)mine);
            }
            if (__state >= 2)
            {
                FinalSay.Value(true, (BoolUnknown)(__state - 2), ref __result);
            }
        }

        private static bool Run(Reachability __instance, TraverseParms traverseParams, ref BoolUnknown __result)
        {
            Map map = ReachRefs.MapRef(__instance);
            RouteGraph g = RouteGraph.For(map);
            if (g == null)
            {
                return true;
            }
            DirCache.Init();
            List<Region> starts = ReachRefs.StartRef(__instance);
            List<Region> dests = ReachRefs.DestRef(__instance);
            if (traverseParams.pawn != null && ReachCtx.valid && starts.Count > 0 && g.Memo(traverseParams.pawn, ReachCtx.dest.Cell, g.PartOf(starts[0])))
            {
                Dev.PZStats.routeMemoHits++;
                __result = BoolUnknown.False;
                return false;
            }
            ReachabilityCache cache = ReachRefs.CacheRef(__instance);
            bool unknown = false;
            for (int i = 0; i < starts.Count; i++)
            {
                for (int j = 0; j < dests.Count; j++)
                {
                    if (dests[j] == starts[i])
                    {
                        __result = BoolUnknown.True;
                        return false;
                    }
                    int r = DirCache.Get(cache, starts[i].District, dests[j].District, traverseParams);
                    if (r == 1)
                    {
                        __result = BoolUnknown.True;
                        return false;
                    }
                    if (r < 0)
                    {
                        unknown = true;
                    }
                }
            }
            __result = unknown ? BoolUnknown.Unknown : BoolUnknown.False;
            return false;
        }
    }

    // Reachability.CheckRegionBasedReachability on maps with floors (see StairRegions).
    [HarmonyPatch(typeof(Reachability), "CheckRegionBasedReachability")]
    public static class CheckRegionBasedReachability_Patch
    {
        public static bool Prefix(Reachability __instance, TraverseParms traverseParams, ref bool __result, out byte __state)
        {
            __state = FinalSay.Pass;
            if (RouteGraph.IsFloorOnly(__instance) || ProjectZ.Annex(ReachRefs.MapRef(__instance)) == null)
            {
                return true;
            }
            bool cross = ReachFinal.CrossFloor(__instance);
            bool run = Run(__instance, traverseParams, ref __result);
            if (cross && !run)
            {
                __state = FinalSay.Of(__result);
            }
            return run;
        }

        // Final say between floors (FinalSay); searched here when another mod's prefix skipped ours (the game's search
        // was skipped too: the open queue still holds the start regions).
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Reachability __instance, TraverseParms traverseParams, ref bool __result, byte __state)
        {
            if (__state == FinalSay.NotRun && !RouteGraph.IsFloorOnly(__instance) && ProjectZ.Annex(ReachRefs.MapRef(__instance)) != null && ReachFinal.CrossFloor(__instance))
            {
                FinalSay.PrefixSkipped++;
                bool mine = false;
                Run(__instance, traverseParams, ref mine);
                __state = FinalSay.Of(mine);
            }
            FinalSay.Bool(__state, ref __result);
        }

        private static readonly HashSet<int> sParts = new HashSet<int>();
        private static readonly HashSet<int> dParts = new HashSet<int>();

        private static bool Run(Reachability __instance, TraverseParms tp, ref bool __result)
        {
            Map map = ReachRefs.MapRef(__instance);
            RouteGraph g = RouteGraph.For(map);
            if (g == null || StairGrid.For(map) == null)
            {
                return true;   // no stairs: the game's search
            }
            DirCache.Init();
            IntVec3 cStart = ReachCtx.start;
            LocalTargetInfo cDest = ReachCtx.dest;
            PathEndMode cMode = ReachCtx.mode;
            bool haveCtx = ReachCtx.valid;
            Queue<Region> open = ReachRefs.OpenRef(__instance);
            List<Region> starts = ReachRefs.StartRef(__instance);
            List<Region> dests = ReachRefs.DestRef(__instance);
            uint reached = ReachRefs.ReachedRef(__instance);
            ReachabilityCache cache = ReachRefs.CacheRef(__instance);
            Dev.PZStats.reachRuns++;
            g.EnsureComps();
            sParts.Clear();
            dParts.Clear();
            for (int i = 0; i < starts.Count; i++)
            {
                int p = g.PartOf(starts[i]);
                if (p >= 0)
                {
                    sParts.Add(p);
                }
            }
            for (int i = 0; i < dests.Count; i++)
            {
                int p = g.PartOf(dests[i]);
                if (p >= 0)
                {
                    dParts.Add(p);
                }
            }
            // 1. parts in different components: never joined
            bool joined = false, samePart = false;
            foreach (int sp in sParts)
            {
                int cs = g.CompOf(sp);
                foreach (int dp in dParts)
                {
                    samePart |= sp == dp;
                    joined |= cs == g.CompOf(dp);
                }
            }
            if (!joined)
            {
                Dev.PZStats.reachFastNo++;
                AddAll(cache, starts, dests, tp, false);
                __result = false;
                return false;
            }
            // 2. the game's own search on the floor
            if (samePart)
            {
                while (open.Count > 0)
                {
                    Region region = open.Dequeue();
                    for (int i = 0; i < region.links.Count; i++)
                    {
                        RegionLink link = region.links[i];
                        for (int j = 0; j < 2; j++)
                        {
                            Region r = link.regions[j];
                            if (r == null || r.reachedIndex == reached || !r.type.Passable() || !r.Allows(tp, isDestination: false))
                            {
                                continue;
                            }
                            if (dests.Contains(r))
                            {
                                for (int k = 0; k < starts.Count; k++)
                                {
                                    DirCache.Add(cache, starts[k].District, r.District, tp, true);
                                }
                                __result = true;
                                return false;
                            }
                            open.Enqueue(r);
                            r.reachedIndex = reached;
                        }
                    }
                }
            }
            open.Clear();
            // 3. over the stairs
            bool ok = haveCtx && g.StairReach(cStart, sParts, cDest, cMode, dParts, tp);
            if (ok)
            {
                District d = dests.Count > 0 ? dests[0].District : null;
                bool one = true;
                for (int i = 1; i < dests.Count; i++)
                {
                    one &= dests[i].District == d;
                }
                if (one)
                {
                    for (int k = 0; k < starts.Count; k++)
                    {
                        DirCache.Add(cache, starts[k].District, d, tp, true);
                    }
                }
            }
            else
            {
                AddAll(cache, starts, dests, tp, false);
            }
            __result = ok;
            return false;
        }

        private static void AddAll(ReachabilityCache cache, List<Region> starts, List<Region> dests, TraverseParms tp, bool v)
        {
            for (int l = 0; l < starts.Count; l++)
            {
                for (int m = 0; m < dests.Count; m++)
                {
                    DirCache.Add(cache, starts[l].District, dests[m].District, tp, v);
                }
            }
        }
    }

    // Traversal modes the game answers by a cell flood (breaking walls, or keeping out of water), between floors: the
    // dig parts' components over the stairs (wall breakers), or the stairs' ends with the floor-only instance (water).
    [HarmonyPatch(typeof(Reachability), "CheckCellBasedReachability")]
    public static class CheckCellBasedReachability_Patch
    {
        private static readonly HashSet<int> sParts = new HashSet<int>();
        private static readonly HashSet<int> dParts = new HashSet<int>();

        public static bool Prefix(Reachability __instance, IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, TraverseParms traverseParams, ref bool __result)
        {
            if (RouteGraph.IsFloorOnly(__instance))
            {
                return true;
            }
            Map map = ReachRefs.MapRef(__instance);
            RouteGraph g = RouteGraph.For(map);
            if (g == null || StairGrid.For(map) == null || !dest.IsValid)
            {
                return true;
            }
            int ls = Levels.LevelOf(start, map), ld = Levels.LevelOf(dest.Cell, map);
            if (ls == ld || ls == Levels.None || ld == Levels.None)
            {
                return true;   // one floor: the game's flood
            }
            if (RouteGraph.DigMode(traverseParams.mode))
            {
                int key = RouteGraph.DigKey(traverseParams.mode);
                int sp = g.DigPartAt(start, key);
                for (int i = 0; i < 8 && sp <= 0; i++)
                {
                    sp = g.DigPartAt(start + GenAdj.AdjacentCells[i], key);
                }
                bool ok = false;
                if (sp > 0)
                {
                    int cs = g.DigCompOf(sp, key);
                    CellRect r = dest.HasThing ? dest.Thing.OccupiedRect() : CellRect.SingleCell(dest.Cell);
                    if (peMode != PathEndMode.OnCell)
                    {
                        r = r.ExpandedBy(1);
                    }
                    foreach (IntVec3 c in r)
                    {
                        if (c.InBounds(map) && ProjectZ.SameBand(c, dest.Cell, map))
                        {
                            int dp = g.DigPartAt(c, key);
                            if (dp > 0 && g.DigCompOf(dp, key) == cs)
                            {
                                ok = true;
                                break;
                            }
                        }
                    }
                }
                __result = ok;
                return false;
            }
            // (no closed doors, no water: the stairs' ends, each floor flooded the game's way)
            sParts.Clear();
            dParts.Clear();
            g.EnsureComps();
            int s0 = g.PartAt(start);
            if (s0 >= 0)
            {
                sParts.Add(s0);
            }
            CellRect dr = dest.HasThing ? dest.Thing.OccupiedRect() : CellRect.SingleCell(dest.Cell);
            if (peMode != PathEndMode.OnCell)
            {
                dr = dr.ExpandedBy(1);
            }
            foreach (IntVec3 c in dr)
            {
                int p = g.PartAt(c);
                if (p >= 0)
                {
                    dParts.Add(p);
                }
            }
            __result = sParts.Count > 0 && g.StairReach(start, sParts, dest, peMode, dParts, traverseParams);
            return false;
        }
    }
}
