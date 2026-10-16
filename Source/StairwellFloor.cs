using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // Stairs placed under a floor that is already laid (stairs design §1): the floor over the stairwell gets the
    // remove-upper-floor designation (the game's remove-foundation one, Slabs.cs), colonists take it up with the normal
    // jobs like any slab (peel order, materials back), and the stairs are not built before the stairwell is open. One
    // predicate, MustClear (a floor cell on the floor above a stairs thing), used by:
    //   - placement (PlaceWorker_Stairs): a stairwell cell with only a floor on it is fine (CanClear: nothing blocks
    //     its removal, Slab.Blocker);
    //   - GenConstruct.CanConstruct: the stairs' blueprint or frame waits ("waiting for the floor above to be
    //     removed"), also in its inspect string;
    //   - the designations follow the stairs site (Mark / Unmark from the stairs-thing spawn and despawn patches): added
    //     when a blueprint or frame spawns, and the ones it added removed again when it goes before the floor did.
    //   Built stairs (god mode places them at once) clear their stairwell when they spawn (Slab.Remove).
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

        // Placement: a stairwell cell with a floor on it can be cleared when nothing keeps that floor there (a building,
        // a blueprint or frame, the exit of other stairs, a floor above it).
        public static bool CanClear(IntVec3 up, Map map)
        {
            return Slab.At(up, map) && Slab.Blocker(up, map) == null;
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
                if (MustClear(up, map) && map.designationManager.DesignationAt(up, DesignationDefOf.RemoveFoundation) == null)
                {
                    map.designationManager.AddDesignation(new Designation(up, DesignationDefOf.RemoveFoundation));
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
                    map.designationManager.DesignationAt(up, DesignationDefOf.RemoveFoundation)?.Delete();
                }
            }
        }
    }

    // The cells whose removal designation a stairs site added (so cancelling it takes back only those).
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

        // Saves from before the slab removal: a stairwell marked with the remove-floor designation gets the slab's.
        public override void FinalizeInit()
        {
            base.FinalizeInit();
            int moved = 0;
            foreach (IntVec3 c in cells)
            {
                Designation old = c.InBounds(map) ? map.designationManager.DesignationAt(c, DesignationDefOf.RemoveFloor) : null;
                if (old != null && Slab.At(c, map))
                {
                    old.Delete();
                    if (map.designationManager.DesignationAt(c, DesignationDefOf.RemoveFoundation) == null)
                    {
                        map.designationManager.AddDesignation(new Designation(c, DesignationDefOf.RemoveFoundation));
                    }
                    moved++;
                }
            }
            if (moved > 0)
            {
                ProjectZ.DebugLog($"map {map}: {moved} stairwell designation(s) moved to the upper floor removal");
            }
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
