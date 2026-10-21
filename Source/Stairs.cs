using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ
{
    // Stairs (design isorim-pz-stairs-jump-design.md §1; 1x4 since 2026-10-20): a 1x3 ramp on floor k, turned by its
    // rotation, and a solid end past it (StairShape). Low end L0, middle L1, high end H = centre + rot.FacingCell. The exit
    // E is the cell past the high end on floor k + 1, right above the solid end. Over the ramp, floor k + 1 stays open
    // (the stairwell). A pawn walks up the ramp as on any standable building and steps from
    // H to E (and back) as one path step (Patches/PathPatches.cs); reachability links the regions of H and E
    // (Patches/ReachPatches.cs). Nothing is saved: the stairs register again when the map loads.
    //
    // A vertical link (raider vertical moves, isorim-pz-siege-ladders-design.md: the stair link made generic): one step
    // of a path between floors that is not a walk to a neighbouring cell. Stairs, ladders and lifts share it; the router,
    // reachability, the touch rule, the reveal walk and the path follower read only this.
    //   Stairs: bottom = the ramp's high end H (floor k), top = the exit E (floor k + 1), low/mid = the ramp's low end L0
    //     and middle L1, span 1, step Climb.StepTicks.
    //   Ladder / lift (SiegeLadder.cs): bottom = low = mid = the foot (no ramp), top = the cell beside the open column,
    //     span floors up, step LadderTicksPerFloor x span.
    //   Elevator (Elevator.cs): bottom = low = mid = the door-side exit on a lower floor, top = the exit on a higher floor
    //     (one link per pair of floors along the shaft), step ElevatorBoardTicks + ElevatorTicksPerFloor x span; without
    //     power the same links as ElevatorLadder (a ladder: LadderTicksPerFloor x span, one at a time, falls).
    // topBlocker: the top is a wall or rock to clear after the climb (later stages; stage 1 never registers one).
    public enum LinkKind
    {
        Stairs,
        Ladder,
        Lift,
        Elevator,
        ElevatorLadder
    }

    public struct VLink
    {
        public LinkKind kind;
        public IntVec3 low;      // L0 (ladders: the foot)
        public IntVec3 mid;      // L1 (ladders: the foot)
        public IntVec3 bottom;   // H, floor k (ladders: the foot)
        public IntVec3 top;      // E, floor k + span
        public int floor;        // k
        public int span;         // floors between bottom and top
        public int stepTicks;    // the whole step's time (= its path cost)
        public bool topBlocker;
        public Building thing;

        public Building_Stairs stairs => thing as Building_Stairs;

        public bool HasRamp => kind == LinkKind.Stairs;

        // ladder rules: one climber at a time, falls (an elevator without power too)
        public bool IsLadder => kind == LinkKind.Ladder || kind == LinkKind.Lift || kind == LinkKind.ElevatorLadder;

        // a player elevator, powered or not (its links join its door-side exits; no column of their own, no touch across)
        public bool IsElevator => kind == LinkKind.Elevator || kind == LinkKind.ElevatorLadder;

        public const int LadderTicksPerFloor = 180;   // 3 s per floor (design)
        public const int ElevatorBoardTicks = 30;      // 0.5 s boarding (user 2026-10-20)
        public const int ElevatorTicksPerFloor = 39;   // 0.65 s per floor

        public static VLink Stairs(Building_Stairs s, IntVec3 bottom, IntVec3 top, Map map)
        {
            IntVec3 f = s.Rotation.FacingCell;
            return new VLink { kind = LinkKind.Stairs, low = s.Position - f, mid = s.Position, bottom = bottom, top = top, floor = ProjectZ.FloorOf(bottom, map), span = 1, stepTicks = Climb.StepTicks, thing = s };
        }

        public static VLink Ladder(LinkKind kind, Building b, IntVec3 foot, IntVec3 top, int span, bool topBlocker, Map map)
        {
            return new VLink { kind = kind, low = foot, mid = foot, bottom = foot, top = top, floor = ProjectZ.FloorOf(foot, map), span = span, stepTicks = LadderTicksPerFloor * span, topBlocker = topBlocker, thing = b };
        }

        public static VLink Elevator(Building b, IntVec3 lower, IntVec3 upper, int span, bool powered, Map map)
        {
            return new VLink { kind = powered ? LinkKind.Elevator : LinkKind.ElevatorLadder, low = lower, mid = lower, bottom = lower, top = upper, floor = ProjectZ.FloorOf(lower, map), span = span, stepTicks = powered ? ElevatorBoardTicks + ElevatorTicksPerFloor * span : LadderTicksPerFloor * span, thing = b };
        }
    }

    // Where the parts of a stairs thing are, for a stairs thing or a placement (centre + rotation). The footprint is 1x4
    // (user 2026-10-20): the 3 ramp cells (low end L0 = centre - facing, middle L1 = centre, high end H = centre + facing)
    // and the solid end S = centre + 2 x facing, right under the exit E. S is part of the stairs: not walkable
    // (StairSolid_PathCost_Patch), nothing else is placed on it (CanPlaceBlueprintAt_StairSolid_Patch). The game's
    // OccupiedRect for a 1x4 thing shifts with the rotation in world terms, but along the facing it always runs from
    // centre - facing to centre + 2 x facing (GenAdj.AdjustForRotation), so these four cells are the footprint for every
    // rotation. The stairwell (the floor above kept open) is over the ramp only; over S is the exit.
    public static class StairShape
    {
        public static readonly IntVec2 Size = new IntVec2(1, 4);

        public static CellRect Footprint(IntVec3 center, Rot4 rot) => GenAdj.OccupiedRect(center, rot, Size);

        // The 3 ramp cells (L0, L1, H): the stairwell's floor.
        public static IEnumerable<IntVec3> Ramp(IntVec3 center, Rot4 rot)
        {
            IntVec3 f = rot.FacingCell;
            yield return center - f;
            yield return center;
            yield return center + f;
        }

        public static CellRect RampRect(IntVec3 center, Rot4 rot) => CellRect.FromLimits(center - rot.FacingCell, center + rot.FacingCell);

        // The solid end past the high end, under the exit.
        public static IntVec3 Solid(IntVec3 center, Rot4 rot) => center + rot.FacingCell * 2;

        // c is one of the ramp cells of stairs at centre + rotation.
        public static bool IsRamp(IntVec3 c, IntVec3 center, Rot4 rot)
        {
            IntVec3 d = c - center;
            IntVec3 f = rot.FacingCell;
            if (d.x * f.z - d.z * f.x != 0 || d.y != 0)
            {
                return false;
            }
            int i = d.x * f.x + d.z * f.z;
            return i >= -1 && i <= 1;
        }

        // The solid end of a stairs thing (built, blueprint or frame) at c, or null.
        public static Thing SolidAt(IntVec3 c, Map map)
        {
            if (map == null || !c.IsValid || !c.InBounds(map))
            {
                return null;
            }
            List<Thing> list = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < list.Count; i++)
            {
                Thing t = list[i];
                if (Stairwell.IsStairThing(t) && Solid(t.Position, t.Rotation) == c)
                {
                    return t;
                }
            }
            return null;
        }

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

        // How far up the ramp a footprint cell is: 0 (low end) .. 2 (high end); 3 is the solid end.
        public static int Index(IntVec3 c, IntVec3 center, Rot4 rot)
        {
            IntVec3 d = c - center;
            IntVec3 f = rot.FacingCell;
            return 1 + d.x * f.x + d.z * f.z;
        }
    }

    // Every stairs def (multi-level gravships S2): the ordinary stairs and the ship stairs (Odyssey only, ShipStairs.cs).
    // Both are Building_Stairs; the rules that know the stairs by def (stairwell, exits, building sites, jump landings,
    // the height rule, the touch from above, the square icon) ask this. Ordinary-stairs-only rules (thick roof, not on a
    // gravship, a slab landing) keep testing PZDefOf.PZ_Stairs; the ship stairs lay a ship upper deck (ShipStairs.LayLanding).
    public static class StairDefs
    {
        public static bool Is(BuildableDef d) => d != null && (d == PZDefOf.PZ_Stairs || d == PZDefOf.PZ_ShipStairs);

        public static IEnumerable<ThingDef> All
        {
            get
            {
                yield return PZDefOf.PZ_Stairs;
                if (PZDefOf.PZ_ShipStairs != null)
                {
                    yield return PZDefOf.PZ_ShipStairs;
                }
            }
        }
    }

    public class StairGrid : MapComponent
    {
        public readonly List<VLink> links = new List<VLink>();

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
            Register(VLink.Stairs(s, bottom, top, map));
        }

        // Any vertical link (stairs, a ladder, a lift): one per thing.
        public void Register(VLink l)
        {
            Deregister(l.thing, false);
            links.Add(l);
            Changed();
        }

        // A thing with several links (an elevator: one per pair of floors) takes a new set at once. Pawns on a link that
        // stays (the same two ends) finish their step as it began; a link that goes drops its climbers (LinkSteps).
        public void Replace(Building s, List<VLink> now)
        {
            List<VLink> was = links.FindAll(l => l.thing == s);
            links.RemoveAll(l => l.thing == s);
            links.AddRange(now);
            Changed();
            foreach (VLink o in was)
            {
                if (!now.Exists(n => n.bottom == o.bottom && n.top == o.top))
                {
                    LinkSteps.LinkGone(map, s, o.bottom, o.top);
                }
            }
        }

        public void Deregister(Building s, bool notify = true)
        {
            int removed = links.RemoveAll(l => l.thing == s);
            if (removed > 0 && notify)
            {
                Changed();
            }
            if (removed > 0)
            {
                // pawns in the middle of its step stop, and climbers fall from the height they had reached (LinkSteps)
                LinkSteps.LinkGone(map, s);
            }
        }

        private void Changed()
        {
            map.reachability?.ClearCache();
            Patches.RouteGraph.For(map)?.StairsChanged();
            SlabPeel.Dirty(map);
            ReachFog.Request(map);
            ProjectZ.DebugLog($"stairs on {map}: {links.Count} link(s)");
        }

        // True when a step from 'from' to 'to' is a link step (either way).
        public bool IsStep(IntVec3 from, IntVec3 to) => StepLink(from, to) >= 0;

        // The index of the link whose step goes from 'from' to 'to' (either way), or -1.
        public int StepLink(IntVec3 from, IntVec3 to)
        {
            for (int i = 0; i < links.Count; i++)
            {
                VLink l = links[i];
                if (l.bottom == from && l.top == to || l.top == from && l.bottom == to)
                {
                    return i;
                }
            }
            return -1;
        }

        // The link of a thing, or -1.
        public int IndexOf(Thing t)
        {
            for (int i = 0; i < links.Count; i++)
            {
                if (ReferenceEquals(links[i].thing, t))
                {
                    return i;
                }
            }
            return -1;
        }

        // A link is usable when its thing stands and the ramp (stairs) and both ends can be stood on (RouteGraph.Usable:
        // the same).
        public bool Usable(VLink l) => UsableIn(l, map);

        public static bool UsableIn(VLink l, Map map)
        {
            if (l.thing == null || !l.thing.Spawned || !l.top.InBounds(map) || !l.bottom.InBounds(map) || !l.top.Walkable(map) || !l.bottom.Walkable(map))
            {
                return false;
            }
            return !l.HasRamp || l.low.InBounds(map) && l.low.Walkable(map) && l.mid.Walkable(map);
        }

        // The link test for a traveller (RouteGraph.Passable: the same): usable, and its cells' regions let it in as the
        // game's region search would (a door it can't or may not open, a fence, danger: Region.Allows).
        public bool Passable(VLink l, TraverseParms tp) => PassableIn(l, tp, map);

        // Usable, and an elevator only for whom it lets in (the router's test on dig parts, where regions are not asked).
        public static bool UsableFor(VLink l, TraverseParms tp, Map map) => UsableIn(l, map) && (!l.IsElevator || ElevatorRule.MayUse(l.thing, tp.pawn));

        public static bool PassableIn(VLink l, TraverseParms tp, Map map)
        {
            if (!UsableIn(l, map) || l.IsElevator && !ElevatorRule.MayUse(l.thing, tp.pawn))
            {
                return false;
            }
            Region t = map.regionGrid.GetValidRegionAt_NoRebuild(l.top);
            Region b = map.regionGrid.GetValidRegionAt_NoRebuild(l.bottom);
            Region lo = l.low == l.bottom ? b : map.regionGrid.GetValidRegionAt_NoRebuild(l.low);
            return t != null && b != null && lo != null && t.Allows(tp, isDestination: false) && b.Allows(tp, isDestination: false) && lo.Allows(tp, isDestination: false);
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
            // (ship stairs lay a ship upper deck as their landing, ShipStairs.LayLanding; a landing ship keeps the one it
            // brought: decks are placed before the things)
            if (!respawningAfterLoad && ShipStairs.Is(def) && top.GetTerrain(map) == PZDefOf.PZ_OpenAir && map.terrainGrid.FoundationAt(top) == null)
            {
                landingRoofLaid = ShipStairs.LayLanding(top, map);
                landingLaid = true;
            }
            if (!respawningAfterLoad && def == PZDefOf.PZ_Stairs && top.GetTerrain(map) == PZDefOf.PZ_OpenAir)
            {
                // Q2: the landing is made with the stairs (the placement checked the cell under it), of their stuff.
                IntVec3 below = FloorRoof.Below(top, map);
                landingRoofLaid = below.IsValid && !map.roofGrid.Roofed(below);
                map.terrainGrid.SetTerrain(top, SlabDefs.For(Stuff));
                landingLaid = true;
            }
            if (!respawningAfterLoad)
            {
                // Built at once (god mode) under a floor: the stairwell opens now (StairwellFloor).
                foreach (IntVec3 c in StairShape.Ramp(Position, Rotation))
                {
                    IntVec3 up = StairShape.Above(c, map);
                    if (StairwellFloor.MustClear(up, map))
                    {
                        if (ShipDeck.At(up, map))
                        {
                            ShipStairs.ClearDeck(up, map);
                        }
                        else
                        {
                            Slab.Remove(up, map, doLeavings: true);
                        }
                    }
                }
            }
            if (!respawningAfterLoad)
            {
                // nothing lies on the solid end (built at once, god mode: as the game moves items off an impassable building)
                IntVec3 solid = StairShape.Solid(Position, Rotation);
                CellRect rect = this.OccupiedRect();
                foreach (Thing t in map.thingGrid.ThingsListAtFast(solid).ToList())
                {
                    if (t.def.category == ThingCategory.Item)
                    {
                        t.DeSpawn();
                        if (!GenPlace.TryPlaceThing(t, solid, map, ThingPlaceMode.Near, null, x => !rect.Contains(x)))
                        {
                            t.Destroy();
                        }
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
        // has been built on it since (then it stays a floor like any other, held up or not by what is left). A floor laid
        // on it goes with it, returning nothing (user 2026-10-18). The roof they brought under it goes too unless something
        // else holds it now.
        private void TakeLanding(Map map)
        {
            IntVec3 top = TopCell;
            if (landingLaid && top.IsValid && ShipStairs.Is(def))
            {
                if (ShipStairs.TakeLanding(this, top, map))
                {
                    landingLaid = false;
                    TakeLandingRoof(top, map);
                }
                return;
            }
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
            // (no materials back, a floor laid on it included: user 2026-10-18, Slab.Remove)
            Slab.Remove(top, map, doLeavings: false, cause: this);
            TakeLandingRoof(top, map);
        }

        private void TakeLandingRoof(IntVec3 top, Map map)
        {
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
            return t != null && (StairDefs.Is(t.def) || StairDefs.Is(t.def.entityDefToBuild));
        }

        // c is a ramp cell of a stairs thing (the floor of a stairwell: the floor above it stays open). The solid end is
        // not: the exit is over it (StairCellAt asks for any cell of the footprint).
        public static bool StairThingAt(IntVec3 c, Map map)
        {
            if (!c.IsValid || !c.InBounds(map))
            {
                return false;
            }
            List<Thing> list = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < list.Count; i++)
            {
                if (IsStairThing(list[i]) && StairShape.IsRamp(c, list[i].Position, list[i].Rotation))
                {
                    return true;
                }
            }
            return false;
        }

        // Any cell of a stairs thing's footprint (ramp or solid end).
        public static bool StairCellAt(IntVec3 c, Map map)
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
            // (one rule: an opening is a stairs thing or a mined shaft under a covered top; a hole to the sky keeps a roof
            // built over it, Shaft.ApplyRoof)
            bool well = StairThingAt(c, map) || Shaft.Covered(c, map);
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
                // the roof over the opening above is drawn again (on a ship it is the ship's roof: ShipStairs.Opening)
                IntVec3 up = StairShape.Above(c, map);
                if (up.IsValid && up.InBounds(map) && map.roofGrid.Roofed(up))
                {
                    map.mapDrawer.MapMeshDirty(up, MapMeshFlagDefOf.Roofs);
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
                ShipStairs.ColumnChanged(__instance, __instance.Map);
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

        public static void Postfix(Thing __instance, Map __state, DestroyMode mode)
        {
            if (__state != null)
            {
                Stairwell.Refresh(__state, __instance.OccupiedRect());
                StairwellFloor.Unmark(__state, StairShape.RampRect(__instance.Position, __instance.Rotation));
                if (ShipStairs.Is(__instance.def) || ShipStairs.Is(__instance.def.entityDefToBuild))
                {
                    // (their opening is no ship column cell now: a deck over it falls)
                    ShipStairs.Gone(__instance, __state, mode);
                }
                ShipStairs.ColumnChanged(__instance, __state);
            }
        }
    }

    // (the column queries, Column.OpenAbove among them: Column.cs)

    // Stairs (§1 placement, §7): the ramp on one floor with a floor above it; the stairwell open; the exit a floor (or
    // open air anywhere a roof could cover the cell under it: the landing is made with the stairs) with nothing
    // impassable on it.
    public class PlaceWorker_Stairs : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int k = ProjectZ.FloorOf(loc, map);
            if (a == null || k < 0 || k + 1 > a.MaxLevel)
            {
                return "PZ_NoFloorAbove".Translate();
            }
            // no landing in fog (vanilla: nothing is placed in undiscovered cells; the game checks only the ramp's
            // centre); asked first, so no other reason tells what the fog hides
            IntVec3 exit = StairShape.Exit(loc, rot, map);
            if (exit.IsValid && exit.Fogged(map))
            {
                return "CannotPlaceInUndiscovered".Translate();
            }
            AcceptanceReport thick = ThickRoofCheck(loc, rot, map);
            if (!thick.Accepted)
            {
                return thick;
            }
            foreach (IntVec3 c in StairShape.Footprint(loc, rot))
            {
                if (ProjectZ.FloorOf(c, map) != k)
                {
                    return "PZ_NoFloorAbove".Translate();
                }
            }
            foreach (IntVec3 c in StairShape.Ramp(loc, rot))
            {
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
            if (LadderColumn.Reserved(top, map))
            {
                // (no landing laid in a ladder's open column or an elevator's shaft)
                return LadderColumn.Refusal(top, map, false);
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

        // No stairs where the stairwell (the ramp, whose roof becomes the stairwell's) or the cell under the landing
        // has a thick roof (SlabSupport.ThickRoof), and none on a gravship or with its landing over one
        // (SlabSupport.Gravship). The cell under the landing is a "use" (F4, SlabSupport.LandingBlocked): a natural rock
        // floor there is landed on as it is. Only roofs the stairs would replace count: the landing's own roof is never
        // written by them (mine up §7: stairs may end in a dug shaft or tunnel under rock). Placement and construction
        // both ask. Under a natural ceiling in a tall mountain the refusal says how to open it (mine it, or remove roof).
        public static AcceptanceReport ThickRoofCheck(IntVec3 loc, Rot4 rot, Map map)
        {
            // (the solid end's roof is the landing's: LandingBlocked below)
            foreach (IntVec3 c in StairShape.Ramp(loc, rot))
            {
                string blocked = SlabSupport.StairsColumnBlocked(c, map);
                if (blocked == "PZ_ThickRoof" && Mountains.On(map) && Floors.UnbuiltNatural(FloorRoof.Above(c, map), map))
                {
                    blocked = "PZ_CeilingIsRock";
                }
                if (blocked != null)
                {
                    return blocked.Translate();
                }
            }
            IntVec3 top = StairShape.Exit(loc, rot, map);
            if (top.IsValid)
            {
                string blocked = SlabSupport.LandingBlocked(top, map);
                if (blocked != null)
                {
                    return blocked.Translate();
                }
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
                if ((list[i] is Blueprint || list[i] is Frame) && SlabDefs.Is(list[i].def.entityDefToBuild))
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

    // An item on the solid end of a stairs site blocks its construction (the builders haul it off first, as for an
    // impassable building; the stairs are standable on their ramp, so the game would leave it under the solid end).
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.BlocksConstruction))]
    public static class BlocksConstruction_StairSolid_Patch
    {
        public static void Postfix(Thing constructible, Thing t, ref bool __result)
        {
            if (!__result && t != null && constructible != null && t.def.category == ThingCategory.Item && t.Spawned && StairwellFloor.IsSite(constructible)
                && t.Position == StairShape.Solid(constructible.Position, constructible.Rotation))
            {
                __result = true;
            }
        }
    }

    // The solid end of built stairs is not walkable (StairShape): the stairs are a standable building on their ramp only.
    // Every path grid asks its cell cost here (also the fence-blocked one); flying pawns too: it is a solid block.
    [HarmonyPatch(typeof(Verse.AI.PathGrid), nameof(Verse.AI.PathGrid.CalculatedCostAt))]
    public static class StairSolid_PathCost_Patch
    {
        public static void Postfix(Verse.AI.PathGrid __instance, IntVec3 c, ref int __result)
        {
            if (__result >= 10000)
            {
                return;
            }
            Map map = __instance.map;
            if (map == null || !c.InBounds(map) || !(c.GetEdifice(map) is Building_Stairs s))
            {
                return;
            }
            if (StairShape.Solid(s.Position, s.Rotation) == c)
            {
                __result = 10000;
            }
        }
    }

    // Placement over the stairs' cells (every placement goes through CanPlaceBlueprintAt_NewTemp):
    //   - nothing else goes on the solid end of stairs (built, blueprint or frame); floors under it are terrain, as under
    //     any building;
    //   - stairs refused by the game for rock in their footprint (its "space already occupied": mine it first, no
    //     exceptions) say which of their cells has rock.
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanPlaceBlueprintAt_NewTemp))]
    public static class CanPlaceBlueprintAt_StairSolid_Patch
    {
        public static void Postfix(BuildableDef entDef, IntVec3 center, Rot4 rot, Map map, Thing thingToIgnore, ref AcceptanceReport __result)
        {
            if (entDef == null || map == null || ProjectZ.Annex(map) == null)
            {
                return;
            }
            if (__result.Accepted)
            {
                if (entDef is ThingDef && !StairDefs.Is(entDef))
                {
                    foreach (IntVec3 c in GenAdj.OccupiedRect(center, rot, entDef.Size))
                    {
                        Thing s = StairShape.SolidAt(c, map);
                        if (s != null && s != thingToIgnore)
                        {
                            __result = "PZ_StairsSolid".Translate();
                            return;
                        }
                    }
                }
                return;
            }
            if (!StairDefs.Is(entDef) || __result.Reason != "SpaceAlreadyOccupied".Translate())
            {
                return;
            }
            List<string> parts = new List<string>();
            foreach (IntVec3 c in StairShape.Footprint(center, rot))
            {
                if (c.InBounds(map) && c.GetFirstMineable(map) != null)
                {
                    parts.Add(("PZ_StairsPart" + StairShape.Index(c, center, rot)).Translate());
                }
            }
            if (parts.Count > 0)
            {
                __result = "PZ_StairsMineFirst".Translate(string.Join(", ", parts));
            }
        }
    }
}
