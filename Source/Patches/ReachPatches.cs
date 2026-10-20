using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace ProjectZ.Patches
{
    // Links between floors in the region graph (design §2.4 B step 2, §8.4; stairs-jump design §3): at QUERY time the
    // region of a stair's high end and the region of its exit count as neighbours (both ways), and the region of a cell a
    // pawn can jump down from leads to the landing's region (one way only, JumpGrid). Nothing is written into
    // region.links, so districts, rooms and temperature stay per floor: the two floods that build districts
    // (FloodAndSetDistricts, FloodAndSetNewRegionIndex) see no links at all.
    internal static class StairRegions
    {
        // > 0 while districts are being built, or while a search must stay on one floor (no links then).
        [ThreadStatic]
        internal static int suppress;

        // The regions one step away from 'region' over stairs (either way) and jumps (down only); null for most regions.
        // fogBlocked: no link into a fogged cell (FogBlocks).
        internal static List<Region> Partners(Region region, Map map, bool fogBlocked)
        {
            List<FloorLink> found = scratch ??= new List<FloorLink>();
            found.Clear();
            Links(region, map, withJumps: true, fogBlocked, found);
            List<Region> into = null;
            for (int i = 0; i < found.Count; i++)
            {
                Region to = found[i].to;
                if (found[i].stair >= 0 || into == null || !into.Contains(to))
                {
                    (into ??= new List<Region>()).Add(to);
                }
            }
            found.Clear();
            return into;
        }

        [ThreadStatic]
        private static List<FloorLink> scratch;

        // One link out of a region to another floor: a stairs link (index into StairGrid.links; jump -1) or a jump
        // (index into JumpGrid.Down(from region); stair -1).
        internal struct FloorLink
        {
            public Region to;
            public int stair;
            public int jump;
        }

        // Fog: the player's pawns never go into fog over a floor link (vanilla never lets them be sent into fog: work
        // givers skip fogged things, orders can't target fog, and on the ground fog sits behind rock; a stairs exit or a
        // jump landing would be a way in). Others (raiders, animals, visitors) go as in vanilla, where fog is no
        // obstacle. A search with a pawn: that pawn's faction (fixed per pawn, so the reachability cache, keyed by pawn,
        // stays right whether it is drafted or not); without a pawn: the query's own fog flag (TraverseParms.avoidFog,
        // vanilla's fogBlocked); a region search whose traveller is unknown (the BFS hook): blocked.
        internal static bool FogBlocks(TraverseParms tp)
        {
            return tp.pawn != null ? tp.pawn.Faction != null && tp.pawn.Faction.IsPlayer : tp.avoidFog;
        }

        // The stairs link, taken up (onto its exit) or down (onto its high end), enters fog.
        internal static bool IntoFog(StairLink l, bool up, Map map) => (up ? l.top : l.bottom).Fogged(map);

        internal static bool IntoFog(Jump j, Map map) => j.land.Fogged(map);

        // THE rule for which links join floors, shared by reachability (Partners: stairs and jumps) and the path router
        // (StairRouter: its candidates and RegionRoute, stairs only in the first pass): each usable stairs link touching
        // 'region' (either way), then, withJumps, each jump down out of it; with fogBlocked none that enters fog. The
        // traveller's own test (Region.Allows: doors, fences, danger) is the caller's entry test, as in the game's
        // region searches.
        internal static void Links(Region region, Map map, bool withJumps, bool fogBlocked, List<FloorLink> into)
        {
            StairGrid g = StairGrid.For(map);
            if (g != null)
            {
                RegionGrid rg = map.regionGrid;
                List<StairLink> links = g.links;
                for (int i = 0; i < links.Count; i++)
                {
                    StairLink l = links[i];
                    if (!Dev.TestRunner.NoExitFix && !g.Usable(l))
                    {
                        continue;   // (the traveller's own test, Region.Allows, is the search's entry test: StairGrid.Passable)
                    }
                    Region b = rg.GetValidRegionAt_NoRebuild(l.bottom);
                    Region t = rg.GetValidRegionAt_NoRebuild(l.top);
                    if (b == null || t == null || b == t)
                    {
                        continue;
                    }
                    if (b == region && !(fogBlocked && IntoFog(l, true, map)))
                    {
                        into.Add(new FloorLink { to = t, stair = i, jump = -1 });
                    }
                    else if (t == region && !(fogBlocked && IntoFog(l, false, map)))
                    {
                        into.Add(new FloorLink { to = b, stair = i, jump = -1 });
                    }
                }
            }
            JumpGrid jg = withJumps ? JumpGrid.For(map) : null;
            if (jg != null)
            {
                List<Jump> jumps = jg.Down(region);
                for (int i = 0; i < jumps.Count; i++)
                {
                    if (jumps[i].to.valid && !(fogBlocked && IntoFog(jumps[i], map)))
                    {
                        into.Add(new FloorLink { to = jumps[i].to, stair = -1, jump = i });
                    }
                }
            }
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
    // a region is taken from the open queue, its stair and jump partners are queued like linked neighbours (same entry
    // test, J2). A search starting upstairs so reaches the ground over a jump; one starting on the ground never goes up
    // without stairs. These searches do not know their traveller, so no partner here enters fog (StairRegions.FogBlocks:
    // the stricter rule; reachability itself applies it per traveller).
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
                Log.Error("[ProjectZ] RegionTraverser.BFSWorker.BreadthFirstTraverseWork is not in the expected form: stairs and jumps are left out of region searches.");
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
            List<Region> partners = StairRegions.Partners(region, map, fogBlocked: true);
            if (partners == null)
            {
                return;
            }
            int pos = (int)closedArrayPosF.GetValue(worker);
            uint closed = (uint)closedIndexF.GetValue(worker);
            for (int i = 0; i < partners.Count; i++)
            {
                Region r = partners[i];
                if (r.closedIndex[pos] != closed && (r.type & types) != RegionType.None && (entryCondition == null || entryCondition(region, r)))
                {
                    queueM.Invoke(worker, new object[] { r });
                }
            }
        }
    }

    // The reachability cache, one way (J3). The game's cache stores (district A, district B) sorted, so a result for
    // A -> B also answers B -> A; with one-way jumps that is wrong (upstairs -> ground can be true while ground ->
    // upstairs is false). On maps with floors the results of our own search go into the game's own dictionary under
    // directed keys (from, to | 1 << 30: never equal to a sorted vanilla key), and only those are read. Every way the
    // game clears its cache (all, per pawn, per hostile) clears these too.
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
            return Ok ? get(c, from.ID, to.ID | Directed, tp) : -1;
        }

        internal static void Add(ReachabilityCache c, District from, District to, TraverseParms tp, bool reachable)
        {
            if (Ok)
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

    // The cache read on maps with floors: the directed entries only.
    [HarmonyPatch(typeof(Reachability), "GetCachedResult")]
    public static class GetCachedResult_Patch
    {
        // __state: 0 the prefix did not run, 1 not ours (no floors, one floor), 2 + the answer between floors.
        public static bool Prefix(Reachability __instance, TraverseParms traverseParams, ref BoolUnknown __result, out byte __state)
        {
            __state = FinalSay.Pass;
            if (ProjectZ.Annex(ReachRefs.MapRef(__instance)) == null)
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
            if (__state == FinalSay.NotRun && ProjectZ.Annex(ReachRefs.MapRef(__instance)) != null && ReachFinal.CrossFloor(__instance))
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
            if (ProjectZ.Annex(ReachRefs.MapRef(__instance)) == null)
            {
                return true;
            }
            DirCache.Init();
            List<Region> starts = ReachRefs.StartRef(__instance);
            List<Region> dests = ReachRefs.DestRef(__instance);
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

    // Reachability.CheckRegionBasedReachability on maps with floors: the game's search plus each dequeued region's stair
    // and jump partners, writing directed cache entries (J1).
    [HarmonyPatch(typeof(Reachability), "CheckRegionBasedReachability")]
    public static class CheckRegionBasedReachability_Patch
    {
        public static bool Prefix(Reachability __instance, TraverseParms traverseParams, ref bool __result, out byte __state)
        {
            __state = FinalSay.Pass;
            if (ProjectZ.Annex(ReachRefs.MapRef(__instance)) == null)
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
            if (__state == FinalSay.NotRun && ProjectZ.Annex(ReachRefs.MapRef(__instance)) != null && ReachFinal.CrossFloor(__instance))
            {
                FinalSay.PrefixSkipped++;
                bool mine = false;
                Run(__instance, traverseParams, ref mine);
                __state = FinalSay.Of(mine);
            }
            FinalSay.Bool(__state, ref __result);
        }

        private static bool Run(Reachability __instance, TraverseParms traverseParams, ref bool __result)
        {
            Map map = ReachRefs.MapRef(__instance);
            if (ProjectZ.Annex(map) == null)
            {
                return true;
            }
            DirCache.Init();
            Queue<Region> open = ReachRefs.OpenRef(__instance);
            List<Region> starts = ReachRefs.StartRef(__instance);
            List<Region> dests = ReachRefs.DestRef(__instance);
            uint reached = ReachRefs.ReachedRef(__instance);
            ReachabilityCache cache = ReachRefs.CacheRef(__instance);
            while (open.Count > 0)
            {
                Region region = open.Dequeue();
                for (int i = 0; i < region.links.Count; i++)
                {
                    RegionLink link = region.links[i];
                    for (int j = 0; j < 2; j++)
                    {
                        if (Visit(link.regions[j], traverseParams, open, starts, dests, reached, cache))
                        {
                            __result = true;
                            return false;
                        }
                    }
                }
                List<Region> partners = StairRegions.Partners(region, map, StairRegions.FogBlocks(traverseParams));
                for (int i = 0; partners != null && i < partners.Count; i++)
                {
                    if (Visit(partners[i], traverseParams, open, starts, dests, reached, cache))
                    {
                        __result = true;
                        return false;
                    }
                }
            }
            for (int l = 0; l < starts.Count; l++)
            {
                for (int m = 0; m < dests.Count; m++)
                {
                    DirCache.Add(cache, starts[l].District, dests[m].District, traverseParams, false);
                }
            }
            __result = false;
            return false;
        }

        private static bool Visit(Region r, TraverseParms tp, Queue<Region> open, List<Region> starts, List<Region> dests, uint reached, ReachabilityCache cache)
        {
            if (r == null || r.reachedIndex == reached || !r.type.Passable() || !r.Allows(tp, isDestination: false))
            {
                return false;
            }
            if (dests.Contains(r))
            {
                for (int k = 0; k < starts.Count; k++)
                {
                    DirCache.Add(cache, starts[k].District, r.District, tp, true);
                }
                return true;
            }
            open.Enqueue(r);
            r.reachedIndex = reached;
            return false;
        }
    }
}
