using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ.Patches
{
    // Touching between floors (design §2 StairTouch; H-c, user 2026-10-19). Two rules, hooked into the shared places
    // touching goes through:
    //   ACROSS A STEP: the two ends of a stair step (H on floor k, E on floor k + 1) are neighbours for touching, like the
    //     game's neighbouring cells: anything on one end is touched (worked on, picked up, hit in melee) from the other.
    //     Only that pair of cells; fire, sight and social interaction are not touched by this.
    //   THE RIM: a stairs thing on floor k (built, blueprint or frame) is touched from its own 8-ring as usual and also
    //     from the floor above, from the walkable floor k + 1 cells next to its stairwell (rebuild, repair, deconstruct
    //     from either floor).
    // And K2 (user 2026-10-19): stairs being taken up (deconstruct, uninstall) whose loss cuts one side off from the map
    // edge are touched only from the side that stays joined (RemovalSide), so the worker is never left cut off; a worker
    // that can't reach that side gets no job (reachability's destination regions leave the other side out).
    // The hooks:
    //   T1 Reachability's destination regions (CanReach Touch: CanConstruct, reservations, work givers, paths, the router's goal);
    //   T2 the immediate touch test (arriving, FailOnCannotTouch, NeedNewPath, melee range, ...);
    //   T3 the region thing lists (closest-thing searches find the frame from upstairs);
    //   T4 the work spot finder (CanTouchTargetFromValidCell, GotoBuild);
    //   T5 the router's goal legs from upstairs end on the rim (Router.cs).
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

        // The other end of a stair step at c (H <-> E), or Invalid.
        public static IntVec3 PairCell(IntVec3 c, Map map)
        {
            if (StairGrid.For(map) == null)
            {
                return IntVec3.Invalid;
            }
            RouteGraph g = RouteGraph.For(map);
            return g == null ? IntVec3.Invalid : g.PairCell(c);
        }

        // root is the other end of a stair step from a cell of the target.
        public static bool AcrossStep(IntVec3 root, LocalTargetInfo target, Map map)
        {
            if (map == null || !target.IsValid || StairGrid.For(map) == null)
            {
                return false;
            }
            IntVec3 other = PairCell(root, map);
            if (!other.IsValid)
            {
                return false;
            }
            if (target.HasThing && target.Thing.Spawned)
            {
                return target.Thing.OccupiedRect().Contains(other);
            }
            return target.Cell == other;
        }

        // K2: stairs being taken up (deconstruct or uninstall designated): +1 when their top side would be cut off from
        // the map edge (touch them only from their own floor), -1 when their low side would be (only from the floor above,
        // the rim), 0 otherwise.
        public static int RemovalSide(Thing t)
        {
            if (!(t is Building_Stairs s) || !s.Spawned || ProjectZ.Annex(s.Map) == null)
            {
                return 0;
            }
            DesignationManager dm = s.Map.designationManager;
            if (dm.DesignationOn(s, DesignationDefOf.Deconstruct) == null && dm.DesignationOn(s, DesignationDefOf.Uninstall) == null)
            {
                return 0;
            }
            RouteGraph g = RouteGraph.For(s.Map);
            if (g == null)
            {
                return 0;
            }
            int link = g.LinkOf(s);
            return link < 0 ? 0 : g.CutSide(link);
        }

        // May the target be touched from root (K2)? Only stairs being taken up restrict it.
        public static bool TouchAllowedFrom(IntVec3 root, Thing t)
        {
            int side = RemovalSide(t);
            if (side == 0)
            {
                return true;
            }
            int k = Levels.LevelOf(t.Position, t.Map);
            int r = Levels.LevelOf(root, t.Map);
            return side > 0 ? r == k : r == k + 1;
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
                if (!TouchAllowedFrom(c, t))
                {
                    continue;
                }
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
    // filth) could be reached to touch. Its own region, entered over the stair link, is a goal too. And the region of
    // the other end of a stair step from a target's cell (across a step), the rim of a stairs thing, and K2.
    [HarmonyPatch(typeof(TouchPathEndModeUtility), nameof(TouchPathEndModeUtility.AddAllowedAdjacentRegions))]
    [PatchGroup(FailSoft.StairRouting)]
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
            bool stairs = StairGrid.For(map) != null;
            foreach (IntVec3 c in own)
            {
                Region r = c.InBounds(map) ? c.GetRegion(map) : null;
                if (r != null && r.Allows(traverseParams, isDestination: true) && !regions.Contains(r))
                {
                    regions.Add(r);
                }
                if (stairs)
                {
                    IntVec3 o = StairTouch.PairCell(c, map);
                    Region ro = o.IsValid && o.InBounds(map) ? o.GetRegion(map) : null;
                    if (ro != null && ro.Allows(traverseParams, isDestination: true) && !regions.Contains(ro))
                    {
                        regions.Add(ro);
                    }
                }
            }
            if (!dest.HasThing || !StairTouch.Is(dest.Thing))
            {
                return;
            }
            int side = StairTouch.RemovalSide(dest.Thing);
            if (side <= 0)
            {
                StairTouch.UpperCells(dest.Thing, cells);
                for (int i = 0; i < cells.Count; i++)
                {
                    Region r = cells[i].GetRegion(map);
                    if (r != null && r.Allows(traverseParams, isDestination: true) && !regions.Contains(r))
                    {
                        regions.Add(r);
                    }
                }
            }
            if (side != 0)
            {
                // K2: only the side that stays joined
                int k = Levels.LevelOf(dest.Thing.Position, map);
                int keep = side > 0 ? k : k + 1;
                regions.RemoveAll(r => RouteGraph.LevelOfRegion(r, map) != keep);
            }
        }
    }

    // T2
    [HarmonyPatch(typeof(TouchPathEndModeUtility), nameof(TouchPathEndModeUtility.IsAdjacentOrInsideAndAllowedToTouch))]
    [PatchGroup(FailSoft.StairRouting)]
    public static class IsAdjacentOrInsideAndAllowedToTouch_Patch
    {
        [HarmonyPriority(Priority.Last)]   // final say between floors (FinalSay)
        public static void Postfix(IntVec3 root, LocalTargetInfo target, PathingContext pc, ref bool __result)
        {
            if (!__result)
            {
                if (target.HasThing && StairTouch.Is(target.Thing) && StairTouch.IsUpperToucher(root, target.Thing))
                {
                    __result = true;
                }
                else if (StairTouch.AcrossStep(root, target, pc?.map ?? target.Thing?.Map))
                {
                    __result = true;
                }
            }
            if (__result && target.HasThing && target.Thing is Building_Stairs && !StairTouch.TouchAllowedFrom(root, target.Thing))
            {
                __result = false;
            }
        }
    }

    // T3: listed in the rim's regions too.
    [HarmonyPatch(typeof(RegionListersUpdater), nameof(RegionListersUpdater.GetTouchableRegions))]
    [PatchGroup(FailSoft.StairRouting)]
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
    [PatchGroup(FailSoft.StairRouting)]
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
    // alone): the game's builders, haulers and cleaners stand on it. K2: stairs being taken up, from the side that stays
    // joined only.
    [HarmonyPatch(typeof(RCellFinder), nameof(RCellFinder.TryFindGoodAdjacentSpotToTouch))]
    [PatchGroup(FailSoft.StairRouting)]
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
            int side = StairTouch.RemovalSide(touchee);
            if (__result && !StairTouch.TouchAllowedFrom(result, touchee))
            {
                __result = false;   // K2: that side would be cut off
            }
            bool upstairs = ProjectZ.FloorOf(toucher.Position, toucher.Map) == StairTouch.FloorOf(touchee) + 1;
            if (__result && !upstairs || side > 0)
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
