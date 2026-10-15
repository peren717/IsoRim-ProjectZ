using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace ProjectZ.Patches
{
    // Stairs in the region graph (design §2.4 B step 2, §8.4): at QUERY time the region of a stair's bottom cell and
    // the region of the cell above it count as neighbours. Nothing is written into region.links, so districts, rooms
    // and temperature stay per floor: the two floods that build districts (FloodAndSetDistricts,
    // FloodAndSetNewRegionIndex) see no stair links at all.
    internal static class StairRegions
    {
        // > 0 while districts are being built (no stair links then).
        [ThreadStatic]
        internal static int suppress;

        // The regions linked to 'region' by stairs, or null (most regions).
        internal static List<Region> Partners(Region region, StairGrid g)
        {
            List<Region> into = null;
            RegionGrid rg = g.map.regionGrid;
            List<StairLink> links = g.links;
            for (int i = 0; i < links.Count; i++)
            {
                StairLink l = links[i];
                Region b = rg.GetValidRegionAt_NoRebuild(l.bottom);
                Region t = rg.GetValidRegionAt_NoRebuild(l.top);
                if (b == null || t == null || b == t)
                {
                    continue;
                }
                if (b == region)
                {
                    (into ??= new List<Region>()).Add(t);
                }
                else if (t == region)
                {
                    (into ??= new List<Region>()).Add(b);
                }
            }
            return into;
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

    // Region breadth-first searches (GenClosest, job scans, CanReach's helpers ...): right after a region is taken from
    // the open queue, the regions linked to it by stairs are queued like linked neighbours (same entry test).
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
            StairGrid g = StairGrid.For(region.Map);
            if (g == null)
            {
                return;
            }
            List<Region> partners = StairRegions.Partners(region, g);
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

    // Reachability.CheckRegionBasedReachability with stair links (on maps with stairs; vanilla elsewhere). The same
    // search as the game's, plus each dequeued region's stair partners.
    [HarmonyPatch(typeof(Reachability), "CheckRegionBasedReachability")]
    public static class CheckRegionBasedReachability_Patch
    {
        private static readonly AccessTools.FieldRef<Reachability, Map> MapRef = AccessTools.FieldRefAccess<Reachability, Map>("map");
        private static readonly AccessTools.FieldRef<Reachability, Queue<Region>> OpenRef = AccessTools.FieldRefAccess<Reachability, Queue<Region>>("openQueue");
        private static readonly AccessTools.FieldRef<Reachability, List<Region>> StartRef = AccessTools.FieldRefAccess<Reachability, List<Region>>("startingRegions");
        private static readonly AccessTools.FieldRef<Reachability, List<Region>> DestRef = AccessTools.FieldRefAccess<Reachability, List<Region>>("destRegions");
        private static readonly AccessTools.FieldRef<Reachability, uint> ReachedRef = AccessTools.FieldRefAccess<Reachability, uint>("reachedIndex");
        private static readonly AccessTools.FieldRef<Reachability, ReachabilityCache> CacheRef = AccessTools.FieldRefAccess<Reachability, ReachabilityCache>("cache");

        public static bool Prefix(Reachability __instance, TraverseParms traverseParams, ref bool __result)
        {
            StairGrid g = StairGrid.For(MapRef(__instance));
            if (g == null)
            {
                return true;
            }
            Queue<Region> open = OpenRef(__instance);
            List<Region> starts = StartRef(__instance);
            List<Region> dests = DestRef(__instance);
            uint reached = ReachedRef(__instance);
            ReachabilityCache cache = CacheRef(__instance);
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
                List<Region> partners = StairRegions.Partners(region, g);
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
                    cache.AddCachedResult(starts[l].District, dests[m].District, traverseParams, reachable: false);
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
                    cache.AddCachedResult(starts[k].District, r.District, tp, reachable: true);
                }
                return true;
            }
            open.Enqueue(r);
            r.reachedIndex = reached;
            return false;
        }
    }
}
