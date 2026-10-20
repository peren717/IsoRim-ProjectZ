using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ.Patches
{
    // Rebuild (and repair, deconstruct) stairs from either floor (design §2, StairTouch): a stairs thing on floor k
    // (built, blueprint or frame) can be touched from its own 8-ring as usual and also from the floor above: from the
    // walkable floor k + 1 cells next to the stairwell (its rim, the exit included). One rule (UpperCells), hooked into
    // the shared places touching goes through:
    //   T1 Reachability's destination regions (CanReach Touch: CanConstruct, reservations, work givers, paths);
    //   T2 the immediate touch test (arriving, FailOnCannotTouch, NeedNewPath, ...);
    //   T3 the region thing lists (closest-thing searches find the frame from upstairs);
    //   T4 the work spot finder (CanTouchTargetFromValidCell, GotoBuild);
    //   T5 paths from upstairs end on the rim (StairRouter).
    public static class StairTouch
    {
        public static bool Is(Thing t)
        {
            return t != null && t.Spawned && Stairwell.IsStairThing(t) && ProjectZ.Annex(t.Map) != null;
        }

        public static int FloorOf(Thing t) => ProjectZ.FloorOf(t.Position, t.Map);

        // The rim: walkable cells on the floor above that touch the stairwell (8 ways).
        public static void UpperCells(Thing t, List<IntVec3> into)
        {
            into.Clear();
            Map map = t.Map;
            int k = FloorOf(t);
            if (k < 0)
            {
                return;
            }
            foreach (IntVec3 c in t.OccupiedRect().ExpandedBy(1))
            {
                if (!c.InBounds(map) || ProjectZ.FloorOf(c, map) != k)
                {
                    continue;
                }
                IntVec3 u = ProjectZ.CellOnFloor(ProjectZ.GroundCellOf(c, map), k + 1, map);
                if (u.IsValid && u.InBounds(map) && u.Walkable(map))
                {
                    into.Add(u);
                }
            }
        }

        private static readonly List<IntVec3> tmp = new List<IntVec3>();

        public static bool IsUpperToucher(IntVec3 c, Thing t)
        {
            if (ProjectZ.FloorOf(c, t.Map) != FloorOf(t) + 1)
            {
                return false;
            }
            UpperCells(t, tmp);
            return tmp.Contains(c);
        }

        // The rim cell a pawn upstairs should work from: nearest it that it can reach; Invalid if none.
        public static IntVec3 BestUpperCell(Pawn p, Thing t)
        {
            UpperCells(t, tmp);
            IntVec3 best = IntVec3.Invalid;
            int bestD = int.MaxValue;
            for (int i = 0; i < tmp.Count; i++)
            {
                IntVec3 c = tmp[i];
                if (c == p.Position)
                {
                    return c;
                }
                int d = (c - p.Position).LengthHorizontalSquared;
                if (d < bestD && p.CanReach(c, PathEndMode.OnCell, Danger.Deadly))
                {
                    bestD = d;
                    best = c;
                }
            }
            return best;
        }
    }

    // T1. Also, on maps with floors: the target's own cells. The game reaches a touch target through the regions of the
    // cells around it only (standing on it counts as touching, IsAdjacentOrInsideAndAllowedToTouch, but is never needed
    // as a goal: a cell one can stand on always has a walkable neighbour). Up here a cell can be reached by a stair step
    // alone, a stairs landing in the open with open air all round: nothing on it (a floor blueprint or frame, an item,
    // filth) could be reached to touch. Its own region, entered over the stair link, is a goal too.
    [HarmonyPatch(typeof(TouchPathEndModeUtility), nameof(TouchPathEndModeUtility.AddAllowedAdjacentRegions))]
    public static class AddAllowedAdjacentRegions_Patch
    {
        private static readonly List<IntVec3> cells = new List<IntVec3>();

        [HarmonyPriority(Priority.Last)]   // final say between floors (FinalSay)
        public static void Postfix(LocalTargetInfo dest, TraverseParms traverseParams, Map map, List<Region> regions)
        {
            if (ProjectZ.Annex(map) == null || !dest.IsValid)
            {
                return;
            }
            CellRect own = dest.HasThing ? dest.Thing.OccupiedRect() : CellRect.SingleCell(dest.Cell);
            foreach (IntVec3 c in own)
            {
                Region r = c.InBounds(map) ? c.GetRegion(map) : null;
                if (r != null && r.Allows(traverseParams, isDestination: true) && !regions.Contains(r))
                {
                    regions.Add(r);
                }
            }
            if (!dest.HasThing || !StairTouch.Is(dest.Thing))
            {
                return;
            }
            StairTouch.UpperCells(dest.Thing, cells);
            for (int i = 0; i < cells.Count; i++)
            {
                Region r = cells[i].GetRegion(map);
                if (r != null && r.Allows(traverseParams, isDestination: true))
                {
                    regions.Add(r);
                }
            }
        }
    }

    // T2
    [HarmonyPatch(typeof(TouchPathEndModeUtility), nameof(TouchPathEndModeUtility.IsAdjacentOrInsideAndAllowedToTouch))]
    public static class IsAdjacentOrInsideAndAllowedToTouch_Patch
    {
        [HarmonyPriority(Priority.Last)]   // final say between floors (FinalSay)
        public static void Postfix(IntVec3 root, LocalTargetInfo target, ref bool __result)
        {
            if (!__result && target.HasThing && StairTouch.Is(target.Thing) && StairTouch.IsUpperToucher(root, target.Thing))
            {
                __result = true;
            }
        }
    }

    // T3: listed in the rim's regions too.
    [HarmonyPatch(typeof(RegionListersUpdater), nameof(RegionListersUpdater.GetTouchableRegions))]
    public static class GetTouchableRegions_Patch
    {
        private static readonly List<IntVec3> cells = new List<IntVec3>();

        [HarmonyPriority(Priority.Last)]   // final say between floors (FinalSay)
        public static void Postfix(Thing thing, Map map, List<Region> outRegions)
        {
            if (!Stairwell.IsStairThing(thing) || ProjectZ.Annex(map) == null || !thing.Spawned)
            {
                return;
            }
            StairTouch.UpperCells(thing, cells);
            for (int i = 0; i < cells.Count; i++)
            {
                Region r = map.regionGrid.GetValidRegionAt_NoRebuild(cells[i]);
                if (r != null && !outRegions.Contains(r))
                {
                    outRegions.Add(r);
                }
            }
        }
    }

    // T3: a region rebuilt on an upper floor lists the stairs things below whose rim it holds (the game only lists things
    // standing in or next to the new region).
    [HarmonyPatch(typeof(RegionMaker), "RegisterThingsInRegionListers")]
    public static class RegisterThingsInRegionListers_Patch
    {
        private static readonly AccessTools.FieldRef<RegionMaker, Region> NewRegRef = AccessTools.FieldRefAccess<RegionMaker, Region>("newReg");
        private static readonly AccessTools.FieldRef<RegionMaker, Map> MapRef = AccessTools.FieldRefAccess<RegionMaker, Map>("map");
        private static readonly List<IntVec3> cells = new List<IntVec3>();

        public static void Postfix(RegionMaker __instance)
        {
            Map map = MapRef(__instance);
            Region reg = NewRegRef(__instance);
            if (reg == null || map == null || ProjectZ.Annex(map) == null)
            {
                return;
            }
            int k = ProjectZ.FloorOf(reg.extentsClose.CenterCell, map);
            if (k < 1)
            {
                return;
            }
            foreach (ThingDef def in StairDefs())
            {
                List<Thing> list = map.listerThings.ThingsOfDef(def);
                for (int i = 0; i < list.Count; i++)
                {
                    Thing t = list[i];
                    if (StairTouch.FloorOf(t) != k - 1 || reg.ListerThings.Contains(t) || !ListerThings.EverListable(t.def, ListerThingsUse.Region))
                    {
                        continue;
                    }
                    StairTouch.UpperCells(t, cells);
                    for (int c = 0; c < cells.Count; c++)
                    {
                        if (map.regionGrid.GetValidRegionAt_NoRebuild(cells[c]) == reg)
                        {
                            reg.ListerThings.Add(t);
                            break;
                        }
                    }
                }
            }
        }

        private static IEnumerable<ThingDef> StairDefs()
        {
            foreach (ThingDef d in global::ProjectZ.StairDefs.All)
            {
                yield return d;
                if (d.blueprintDef != null)
                {
                    yield return d.blueprintDef;
                }
                if (d.frameDef != null)
                {
                    yield return d.frameDef;
                }
            }
        }
    }

    // T4: a pawn upstairs works from the rim; one on the ground that finds no spot down there tries the rim too. And, as in
    // T1, with no spot round a target on a map with floors, the target's own cell (a landing reached by its stair step
    // alone): the game's builders, haulers and cleaners stand on it.
    [HarmonyPatch(typeof(RCellFinder), nameof(RCellFinder.TryFindGoodAdjacentSpotToTouch))]
    public static class TryFindGoodAdjacentSpotToTouch_Patch
    {
        [HarmonyPriority(Priority.Last)]   // final say between floors (FinalSay)
        public static void Postfix(Pawn toucher, Thing touchee, ref IntVec3 result, ref bool __result)
        {
            if (!__result && toucher != null && touchee != null && touchee.Spawned && ProjectZ.Annex(touchee.Map) != null && !StairTouch.Is(touchee))
            {
                foreach (IntVec3 own in touchee.OccupiedRect())
                {
                    if (own.WalkableBy(touchee.Map, toucher) && toucher.CanReach(own, PathEndMode.OnCell, Danger.Deadly))
                    {
                        result = own;
                        __result = true;
                        return;
                    }
                }
            }
            if (toucher == null || !StairTouch.Is(touchee))
            {
                return;
            }
            bool upstairs = ProjectZ.FloorOf(toucher.Position, toucher.Map) == StairTouch.FloorOf(touchee) + 1;
            if (__result && !upstairs)
            {
                return;
            }
            IntVec3 c = StairTouch.BestUpperCell(toucher, touchee);
            if (c.IsValid)
            {
                result = c;
                __result = true;
            }
        }
    }
}
