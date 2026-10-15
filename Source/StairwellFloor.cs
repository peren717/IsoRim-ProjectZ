using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // Stairs placed under a floor that is already laid (stairs design §1): the floor over the stairwell gets the game's
    // own remove-floor designation, colonists take it up with the normal job (materials back as usual), and the stairs
    // are not built before the stairwell is open. One predicate, MustClear (a floor cell on the floor above a stairs
    // thing), used by:
    //   - placement (PlaceWorker_Stairs): a stairwell cell with only a floor on it is fine (CanClear);
    //   - TerrainGrid.CanRemoveTopLayerAt: such a floor can be removed (an upper floor can't anywhere else), and
    //     RemoveTopLayer takes it down to open air at once (also a floor laid on the upper floor), with the leavings,
    //     and moves pawns and items off (MoveOff) and drops the zone cell;
    //   - the remove-floor checks for a building in the way: any building on it blocks;
    //   - GenConstruct.CanConstruct: the stairs' blueprint or frame waits ("waiting for the floor above to be
    //     removed"), also in its inspect string;
    //   - the designations follow the stairs site (Mark / Unmark from the stairs-thing spawn and despawn patches): added
    //     when a blueprint or frame spawns, and the ones it added removed again when it goes before the floor did.
    //   Built stairs (god mode places them at once) clear their stairwell when they spawn.
    public static class StairwellFloor
    {
        // A stairs blueprint or frame (not the built stairs).
        public static bool IsSite(Thing t) => t != null && !(t is Building_Stairs) && t.def.entityDefToBuild == PZDefOf.PZ_Stairs;

        // A floor on the floor above a stairs thing: it has to go.
        public static bool MustClear(IntVec3 c, Map map)
        {
            if (map == null || !c.IsValid || !c.InBounds(map) || ProjectZ.Annex(map) == null || !ProjectZ.HasFloor(c, map))
            {
                return false;
            }
            return Stairwell.StairThingAt(FloorRoof.Below(c, map), map);
        }

        // Placement: a stairwell cell with a floor on it can be cleared when nothing else is there that must stay (a
        // building, a blueprint or frame, the exit of other stairs).
        public static bool CanClear(IntVec3 up, Map map)
        {
            if (!ProjectZ.HasFloor(up, map))
            {
                return false;
            }
            List<Thing> list = map.thingGrid.ThingsListAtFast(up);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is Building || list[i] is Blueprint || list[i] is Frame)
                {
                    return false;
                }
            }
            return !IsStairsExit(up, map);
        }

        // The exit of any stairs thing (built, blueprint or frame).
        public static bool IsStairsExit(IntVec3 c, Map map)
        {
            foreach (Thing t in map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs))
            {
                if (StairShape.Exit(t.Position, t.Rotation, map) == c)
                {
                    return true;
                }
            }
            foreach (ThingRequestGroup g in new[] { ThingRequestGroup.Blueprint, ThingRequestGroup.BuildingFrame })
            {
                foreach (Thing t in map.listerThings.ThingsInGroup(g))
                {
                    if (IsSite(t) && StairShape.Exit(t.Position, t.Rotation, map) == c)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // A stairs blueprint or frame with floor still over its stairwell.
        public static bool Waiting(Thing t)
        {
            if (!IsSite(t) || !t.Spawned || ProjectZ.Annex(t.Map) == null)
            {
                return false;
            }
            foreach (IntVec3 c in t.OccupiedRect())
            {
                if (ProjectZ.HasFloor(StairShape.Above(c, t.Map), t.Map))
                {
                    return true;
                }
            }
            return false;
        }

        public static string WaitingLine => "PZ_WaitingFloorAbove".Translate();

        // A stairs site spawned: its stairwell floor is designated for removal (where it isn't already).
        public static void Mark(Thing site)
        {
            Map map = site.Map;
            StairwellMarks marks = map.GetComponent<StairwellMarks>();
            foreach (IntVec3 c in site.OccupiedRect())
            {
                IntVec3 up = StairShape.Above(c, map);
                if (MustClear(up, map) && map.designationManager.DesignationAt(up, DesignationDefOf.RemoveFloor) == null)
                {
                    map.designationManager.AddDesignation(new Designation(up, DesignationDefOf.RemoveFloor));
                    marks?.cells.Add(up);
                }
            }
        }

        // A stairs thing gone: the designations a site added over cells with no stairs thing under them any more go too.
        public static void Unmark(Map map, CellRect rect)
        {
            StairwellMarks marks = map.GetComponent<StairwellMarks>();
            if (marks == null || marks.cells.Count == 0)
            {
                return;
            }
            foreach (IntVec3 c in rect)
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                IntVec3 up = StairShape.Above(c, map);
                if (up.IsValid && marks.cells.Contains(up) && !Stairwell.StairThingAt(c, map))
                {
                    marks.cells.Remove(up);
                    map.designationManager.DesignationAt(up, DesignationDefOf.RemoveFloor)?.Delete();
                }
            }
        }

        // Takes the floor (and any floor laid on it) down to open air: the room below keeps its roof (the stairwell's,
        // FloorRoof / Stairwell.RoofFor), materials come back as from any removed floor, pawns and items move off.
        public static void Clear(IntVec3 c, Map map, bool doLeavings)
        {
            TerrainGrid tg = map.terrainGrid;
            TerrainDef top = tg.TopTerrainAt(c);
            TerrainDef under = tg.UnderTerrainAt(c);
            Zone zone = map.zoneManager.ZoneAt(c);
            if (zone != null)
            {
                zone.RemoveCell(c);
                if (zone.cells.Count > 0)
                {
                    zone.CheckContiguous();
                }
            }
            tg.SetTerrain(c, PZDefOf.PZ_OpenAir);
            if (doLeavings)
            {
                // (dropped next to the cell: nothing can be placed on open air)
                GenLeaving.DoLeavingsFor(top, c, map);
                if (under != null && under != PZDefOf.PZ_OpenAir)
                {
                    GenLeaving.DoLeavingsFor(under, c, map);
                }
            }
            MoveOff(c, map);
            ProjectZ.DebugLog($"stairwell floor at {c} removed ({top.defName}{(under != null ? " on " + under.defName : "")})");
        }

        // Pawns and items on a cell that just became open air step to the nearest cell on the same floor that can hold
        // them (as the game's own recovery from an unwalkable cell, Pawn_PathFollower.TryRecoverFromUnwalkablePosition).
        private static void MoveOff(IntVec3 c, Map map)
        {
            int k = ProjectZ.FloorOf(c, map);
            List<Thing> list = c.GetThingList(map);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Thing t = list[i];
                if (t is Pawn p)
                {
                    IntVec3 to = IntVec3.Invalid;
                    for (int r = 1; r < GenRadial.NumCellsInRadius(6f); r++)
                    {
                        IntVec3 x = c + GenRadial.RadialPattern[r];
                        if (x.InBounds(map) && ProjectZ.FloorOf(x, map) == k && x.Standable(map))
                        {
                            to = x;
                            break;
                        }
                    }
                    if (to.IsValid)
                    {
                        // The pawn taking the floor up keeps its job (it is finishing); anyone else starts again.
                        bool remover = p.CurJob != null && p.CurJob.def == JobDefOf.RemoveFloor && p.CurJob.targetA.Cell == c;
                        p.Position = to;
                        p.Notify_Teleported(endCurrentJob: !remover, resetTweenedPos: false);
                    }
                }
                else if (t.def.category == ThingCategory.Item)
                {
                    t.DeSpawn();
                    if (!GenPlace.TryPlaceThing(t, c, map, ThingPlaceMode.Near, null, x => ProjectZ.FloorOf(x, map) == k))
                    {
                        GenSpawn.Spawn(t, c, map);
                    }
                }
            }
        }
    }

    // The cells whose remove-floor designation a stairs site added (so cancelling it takes back only those).
    public class StairwellMarks : MapComponent
    {
        public HashSet<IntVec3> cells = new HashSet<IntVec3>();

        public StairwellMarks(Map map) : base(map)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref cells, "cells", LookMode.Value);
            if (cells == null)
            {
                cells = new HashSet<IntVec3>();
            }
        }
    }

    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.CanRemoveTopLayerAt))]
    public static class CanRemoveTopLayer_Stairwell_Patch
    {
        private static readonly AccessTools.FieldRef<TerrainGrid, Map> mapRef = AccessTools.FieldRefAccess<TerrainGrid, Map>("map");

        public static void Postfix(TerrainGrid __instance, IntVec3 c, ref bool __result)
        {
            if (!__result && StairwellFloor.MustClear(c, mapRef(__instance)))
            {
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.RemoveTopLayer))]
    public static class RemoveTopLayer_Stairwell_Patch
    {
        private static readonly AccessTools.FieldRef<TerrainGrid, Map> mapRef = AccessTools.FieldRefAccess<TerrainGrid, Map>("map");

        public static bool Prefix(TerrainGrid __instance, IntVec3 c, bool doLeavings)
        {
            Map map = mapRef(__instance);
            if (!StairwellFloor.MustClear(c, map))
            {
                return true;
            }
            StairwellFloor.Clear(c, map, doLeavings);
            return false;
        }
    }

    // Any building on a stairwell floor keeps it there (it would be left on open air).
    [HarmonyPatch(typeof(WorkGiver_ConstructRemoveFloor), nameof(WorkGiver_ConstructRemoveFloor.AnyBuildingBlockingFloorRemoval))]
    public static class FloorRemovalBlocked_Stairwell_Patch
    {
        public static bool Prefix(IntVec3 c, Map map, ref bool __result)
        {
            if (!StairwellFloor.MustClear(c, map))
            {
                return true;
            }
            __result = c.GetFirstBuilding(map) != null;
            return false;
        }
    }

    // Every construction work giver and the finish-frame job ask this: the stairs wait for their stairwell.
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanConstruct), new[] { typeof(Thing), typeof(Pawn), typeof(bool), typeof(bool), typeof(JobDef) })]
    public static class CanConstruct_Stairwell_Patch
    {
        // (first, so the reason shown is this one)
        public static bool Prefix(Thing t, ref bool __result)
        {
            if (StairwellFloor.Waiting(t))
            {
                JobFailReason.Is(StairwellFloor.WaitingLine);
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Blueprint_Build), nameof(Blueprint_Build.GetInspectString))]
    public static class BlueprintInspect_Stairwell_Patch
    {
        public static void Postfix(Blueprint_Build __instance, ref string __result)
        {
            if (StairwellFloor.Waiting(__instance))
            {
                __result = __result.NullOrEmpty() ? StairwellFloor.WaitingLine : __result + "\n" + StairwellFloor.WaitingLine;
            }
        }
    }

    [HarmonyPatch(typeof(Frame), nameof(Frame.GetInspectString))]
    public static class FrameInspect_Stairwell_Patch
    {
        public static void Postfix(Frame __instance, ref string __result)
        {
            if (StairwellFloor.Waiting(__instance))
            {
                __result = __result.NullOrEmpty() ? StairwellFloor.WaitingLine : __result + "\n" + StairwellFloor.WaitingLine;
            }
        }
    }
}
