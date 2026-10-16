using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ
{
    // Stairs (design isorim-pz-stairs-jump-design.md §1): a 1x3 ramp on floor k, turned by its rotation. Low end L0,
    // middle L1, high end H = centre + rot.FacingCell. The exit E is the cell past the high end on floor k + 1. Over the
    // ramp, floor k + 1 stays open (the stairwell). A pawn walks up the ramp as on any standable building and steps from
    // H to E (and back) as one path step (Patches/PathPatches.cs); reachability links the regions of H and E
    // (Patches/ReachPatches.cs). Nothing is saved: the stairs register again when the map loads.
    public struct StairLink
    {
        public IntVec3 bottom;   // H, floor k
        public IntVec3 top;      // E, floor k + 1
        public int floor;        // k
        public Building_Stairs stairs;
    }

    // Where the parts of a ramp are, for a stairs thing or a placement (centre + rotation).
    public static class StairShape
    {
        public static CellRect Footprint(IntVec3 center, Rot4 rot) => GenAdj.OccupiedRect(center, rot, new IntVec2(1, 3));

        public static IntVec3 High(IntVec3 center, Rot4 rot) => center + rot.FacingCell;

        // The exit: the cell past the high end, on the floor above. Invalid where there is none.
        public static IntVec3 Exit(IntVec3 center, Rot4 rot, Map map)
        {
            int k = ProjectZ.FloorOf(center, map);
            IntVec3 past = High(center, rot) + rot.FacingCell;
            if (k < 0 || ProjectZ.FloorOf(past, map) != k)
            {
                return IntVec3.Invalid;
            }
            return ProjectZ.CellOnFloor(ProjectZ.GroundCellOf(past, map), k + 1, map);
        }

        // A cell lifted onto the floor above (for a footprint cell: a stairwell cell).
        public static IntVec3 Above(IntVec3 c, Map map)
        {
            int k = ProjectZ.FloorOf(c, map);
            return k < 0 ? IntVec3.Invalid : ProjectZ.CellOnFloor(ProjectZ.GroundCellOf(c, map), k + 1, map);
        }

        // How far up the ramp a footprint cell is: 0 (low end) .. 2 (high end).
        public static int Index(IntVec3 c, IntVec3 center, Rot4 rot)
        {
            IntVec3 d = c - center;
            IntVec3 f = rot.FacingCell;
            return 1 + d.x * f.x + d.z * f.z;
        }
    }

    public class StairGrid : MapComponent
    {
        public readonly List<StairLink> links = new List<StairLink>();

        public StairGrid(Map map) : base(map)
        {
        }

        public int Count => links.Count;

        private static Map lastMap;
        private static StairGrid last;

        // The stairs of a map, or null when it has none (cheap: last map cached).
        public static StairGrid For(Map map)
        {
            if (map == null)
            {
                return null;
            }
            StairGrid g;
            if (ReferenceEquals(map, lastMap))
            {
                g = last;
            }
            else
            {
                g = map.components == null ? null : map.GetComponent<StairGrid>();
                lastMap = map;
                last = g;
            }
            return g != null && g.links.Count > 0 ? g : null;
        }

        public void Register(Building_Stairs s, IntVec3 bottom, IntVec3 top)
        {
            Deregister(s, false);
            links.Add(new StairLink { bottom = bottom, top = top, floor = ProjectZ.FloorOf(bottom, map), stairs = s });
            Changed();
        }

        public void Deregister(Building_Stairs s, bool notify = true)
        {
            int removed = links.RemoveAll(l => l.stairs == s);
            if (removed > 0 && notify)
            {
                Changed();
            }
        }

        private void Changed()
        {
            map.reachability?.ClearCache();
            SlabPeel.Dirty(map);
            ProjectZ.DebugLog($"stairs on {map}: {links.Count} link(s)");
        }

        // True when a step from 'from' to 'to' is a stair step (either way).
        public bool IsStep(IntVec3 from, IntVec3 to)
        {
            for (int i = 0; i < links.Count; i++)
            {
                StairLink l = links[i];
                if (l.bottom == from && l.top == to || l.top == from && l.bottom == to)
                {
                    return true;
                }
            }
            return false;
        }

        // A link is usable when both ends can be stood on.
        public bool Usable(StairLink l)
        {
            return l.stairs != null && l.stairs.Spawned && l.top.InBounds(map) && l.top.Walkable(map) && l.bottom.Walkable(map);
        }

        // THE link test for a traveller: usable, and both ends let it in as the game's region search would (a door it
        // can't or may not open, a fence, danger: Region.Allows). Reachability (StairRegions.Partners + its region
        // entry test) and the path splicer (StairRouter.StairStep) agree through this, so a blocked exit means "can't
        // reach" and a re-path, never a path the follower can't walk.
        public bool Passable(StairLink l, TraverseParms tp)
        {
            if (!Usable(l))
            {
                return false;
            }
            Region t = map.regionGrid.GetValidRegionAt_NoRebuild(l.top);
            Region b = map.regionGrid.GetValidRegionAt_NoRebuild(l.bottom);
            return t != null && b != null && t.Allows(tp, isDestination: false) && b.Allows(tp, isDestination: false);
        }
    }

    public class Building_Stairs : Building
    {
        public IntVec3 HighCell => StairShape.High(Position, Rotation);

        // The exit on the floor above (Invalid without one).
        public IntVec3 TopCell => Map == null ? IntVec3.Invalid : StairShape.Exit(Position, Rotation, Map);

        // The landing (§7) is part of the stairs: laid with them on open air, the roof under it with it where there was
        // none (the stairs hold it, holdsRoof), and taken away with them. (False for stairs from older saves and for
        // stairs that came out onto a floor already there: that floor stays.)
        private bool landingLaid;
        private bool landingRoofLaid;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref landingLaid, "landingLaid", false);
            Scribe_Values.Look(ref landingRoofLaid, "landingRoofLaid", false);
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            IntVec3 top = TopCell;
            if (!top.IsValid)
            {
                return;
            }
            if (!respawningAfterLoad && top.GetTerrain(map) == PZDefOf.PZ_OpenAir)
            {
                // Q2: the landing is made with the stairs (the placement checked the cell under it).
                IntVec3 below = FloorRoof.Below(top, map);
                landingRoofLaid = below.IsValid && !map.roofGrid.Roofed(below);
                map.terrainGrid.SetTerrain(top, PZDefOf.PZ_UpperFloor);
                landingLaid = true;
            }
            if (!respawningAfterLoad)
            {
                // Built at once (god mode) under a floor: the stairwell opens now (StairwellFloor).
                foreach (IntVec3 c in this.OccupiedRect())
                {
                    IntVec3 up = StairShape.Above(c, map);
                    if (StairwellFloor.MustClear(up, map))
                    {
                        Slab.Remove(up, map, doLeavings: true);
                    }
                }
            }
            map.GetComponent<StairGrid>()?.Register(this, HighCell, top);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            map?.GetComponent<StairGrid>()?.Deregister(this);
            if (map != null && mode != DestroyMode.WillReplace)
            {
                TakeLanding(map);
            }
            base.DeSpawn(mode);
        }

        // The stairs go: so does the landing they laid (nothing comes back for it, it came with them), unless something
        // has been built on it since (then it stays a floor like any other, held up or not by what is left). The roof
        // they brought under it goes too unless something else holds it now.
        private void TakeLanding(Map map)
        {
            IntVec3 top = TopCell;
            if (!landingLaid || !top.IsValid || !Slab.At(top, map))
            {
                return;
            }
            List<Thing> list = map.thingGrid.ThingsListAtFast(top);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is Building || list[i] is Blueprint || list[i] is Frame)
                {
                    return;
                }
            }
            landingLaid = false;
            Slab.Remove(top, map, doLeavings: false, cause: this);
            IntVec3 below = FloorRoof.Below(top, map);
            if (landingRoofLaid && below.IsValid && map.roofGrid.Roofed(below) && !SlabSupport.Supported(below, map, ignore: this))
            {
                map.roofGrid.SetRoof(below, null);
            }
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            IntVec3 top = TopCell;
            if (top.IsValid)
            {
                string line = "PZ_StairsLeadTo".Translate(ProjectZ.FloorOf(top, Map) + 1);
                s = s.NullOrEmpty() ? line : s + "\n" + line;
            }
            return s;
        }
    }

    // The stairwell (Q1): the cells under a stairs thing (built, blueprint or frame) keep their roof, but as the
    // invisible PZ_StairwellRoof: the room below stays indoors, nothing is drawn there, and the column above stays
    // open (Column.OpenAbove). Without a stairs thing there any more, it is an ordinary built roof again. One rule
    // (RoofFor), applied whenever a roof is written (FloorRoof's SetRoof prefix) and when a stairs thing spawns or goes.
    public static class Stairwell
    {
        public static bool IsStairThing(Thing t)
        {
            return t != null && (t.def == PZDefOf.PZ_Stairs || t.def.entityDefToBuild == PZDefOf.PZ_Stairs);
        }

        public static bool StairThingAt(IntVec3 c, Map map)
        {
            if (!c.IsValid || !c.InBounds(map))
            {
                return false;
            }
            List<Thing> list = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < list.Count; i++)
            {
                if (IsStairThing(list[i]))
                {
                    return true;
                }
            }
            return false;
        }

        // The roof a cell really gets when 'def' is written to it.
        public static RoofDef RoofFor(IntVec3 c, Map map, RoofDef def)
        {
            if (def == null || def == PZDefOf.PZ_FloorRoof)
            {
                return def;
            }
            bool well = StairThingAt(c, map);
            if (well && def != PZDefOf.PZ_StairwellRoof)
            {
                return PZDefOf.PZ_StairwellRoof;
            }
            if (!well && def == PZDefOf.PZ_StairwellRoof)
            {
                return RoofDefOf.RoofConstructed;
            }
            return def;
        }

        public static void Refresh(Map map, CellRect rect)
        {
            if (map == null || ProjectZ.Annex(map) == null)
            {
                return;
            }
            // (a stairs building site is no landing for jumps: those changed too)
            JumpGrid.For(map)?.Dirty();
            map.reachability?.ClearCache();
            foreach (IntVec3 c in rect)
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                RoofDef cur = map.roofGrid.RoofAt(c);
                if (cur != null && RoofFor(c, map, cur) != cur)
                {
                    map.roofGrid.SetRoof(c, cur);   // the SetRoof prefix makes it the right one
                }
            }
        }
    }

    // A stairs thing spawned or gone: its cells' roofs follow the stairwell rule, and a floor over a new stairs site is
    // designated for removal (StairwellFloor; the designations it added go again with it).
    [HarmonyPatch(typeof(Thing), nameof(Thing.SpawnSetup))]
    public static class StairThing_Spawn_Patch
    {
        public static void Postfix(Thing __instance, bool respawningAfterLoad)
        {
            if (Stairwell.IsStairThing(__instance))
            {
                Stairwell.Refresh(__instance.Map, __instance.OccupiedRect());
                if (!respawningAfterLoad && StairwellFloor.IsSite(__instance) && ProjectZ.Annex(__instance.Map) != null)
                {
                    StairwellFloor.Mark(__instance);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.DeSpawn))]
    public static class StairThing_DeSpawn_Patch
    {
        public static void Prefix(Thing __instance, out Map __state)
        {
            __state = Stairwell.IsStairThing(__instance) ? __instance.Map : null;
        }

        public static void Postfix(Thing __instance, Map __state)
        {
            if (__state != null)
            {
                Stairwell.Refresh(__state, __instance.OccupiedRect());
                StairwellFloor.Unmark(__state, __instance.OccupiedRect());
            }
        }
    }

    // The column above a cell is open (nothing stops a fall, a shot or a look): no roof, or the stairwell's.
    public static class Column
    {
        public static bool OpenAbove(IntVec3 c, Map map)
        {
            RoofDef r = map.roofGrid.RoofAt(c);
            return r == null || r == PZDefOf.PZ_StairwellRoof;
        }
    }

    // Stairs (§1 placement, §7): the ramp on one floor with a floor above it; the stairwell open; the exit a floor (or
    // open air anywhere a roof could cover the cell under it: the landing is made with the stairs) with nothing
    // impassable on it.
    public class PlaceWorker_Stairs : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int k = ProjectZ.FloorOf(loc, map);
            if (a == null || k < 0 || k + 1 >= a.floors)
            {
                return "PZ_NoFloorAbove".Translate();
            }
            foreach (IntVec3 c in StairShape.Footprint(loc, rot))
            {
                if (ProjectZ.FloorOf(c, map) != k)
                {
                    return "PZ_NoFloorAbove".Translate();
                }
                IntVec3 up = StairShape.Above(c, map);
                if (!up.IsValid)
                {
                    return "PZ_WellNotOpen".Translate();
                }
                if (up.GetTerrain(map) == PZDefOf.PZ_OpenAir)
                {
                    if (up.GetEdifice(map) != null || FloorPlanned(up, map))
                    {
                        return "PZ_WellNotOpen".Translate();
                    }
                }
                else if (!StairwellFloor.CanClear(up, map))
                {
                    // (a floor alone is taken up for the stairs, StairwellFloor)
                    return "PZ_WellNotOpen".Translate();
                }
            }
            IntVec3 top = StairShape.Exit(loc, rot, map);
            if (!top.IsValid)
            {
                return "PZ_NoExitCell".Translate();
            }
            if (Stairwell.StairThingAt(FloorRoof.Below(top, map), map))
            {
                // the exit over another stairwell (its floor is about to go, or already open air)
                return "PZ_LandingBlocked".Translate();
            }
            if (top.GetTerrain(map) == PZDefOf.PZ_OpenAir)
            {
                // The landing is made with the stairs and held up by them (§7: stairs build anywhere): the cell under it
                // only has to be one a roof could cover.
                if (FloorPlanned(top, map))
                {
                    return AcceptanceReport.WasAccepted;
                }
                AcceptanceReport r = SlabSupport.CoverOk(FloorRoof.Below(top, map), map);
                if (!r.Accepted)
                {
                    return r;
                }
            }
            else if (top.GetTerrain(map).passability != Traversability.Standable)
            {
                return "PZ_LandingBlocked".Translate();
            }
            Building e = top.GetEdifice(map);
            if (e != null && e.def.passability == Traversability.Impassable)
            {
                return "PZ_LandingBlocked".Translate();
            }
            return AcceptanceReport.WasAccepted;
        }

        // An upper floor blueprint or frame on this cell.
        public static bool FloorPlanned(IntVec3 c, Map map)
        {
            if (!c.IsValid || !c.InBounds(map))
            {
                return false;
            }
            List<Thing> list = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < list.Count; i++)
            {
                if ((list[i] is Blueprint || list[i] is Frame) && list[i].def.entityDefToBuild == PZDefOf.PZ_UpperFloor)
                {
                    return true;
                }
            }
            return false;
        }
    }

    // An upper floor cell (§7): a roof could be held under it (the game's support rules: a wall, column or stairs within
    // 6.9 cells over roofed cells, the slabs planned next to it counting as roofs), the cell below not a stairwell (§1),
    // and nothing there too tall for the storey (the slab height rule, HeightRule). The slab brings the roof under it.
    public class PlaceWorker_UpperFloor : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            return Check(loc, map);
        }

        public static AcceptanceReport Check(IntVec3 loc, Map map)
        {
            if (ProjectZ.FloorOf(loc, map) < 1)
            {
                return ViewFloorAbove();
            }
            IntVec3 below = FloorRoof.Below(loc, map);
            AcceptanceReport r = SlabSupport.CoverOk(below, map);
            if (!r.Accepted)
            {
                return r;
            }
            if (!SlabSupport.Supported(below, map, countPlanned: true))
            {
                return "PZ_NoSupportBelow".Translate();
            }
            return AcceptanceReport.WasAccepted;
        }

        // Laying a floor while working on the ground: it goes on the floor above.
        public static string ViewFloorAbove() => "PZ_ViewFloorAbove".Translate(FloorView.KeyName(PZKeyDefOf.PZ_FloorUp));
    }
}
