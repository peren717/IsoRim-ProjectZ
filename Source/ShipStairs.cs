using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace ProjectZ
{
    // Multi-level gravships, phase P5 (isorim-pz-multilevel-gravship.md v2.1 G, user answers v2.5): the ship stairs
    // PZ_ShipStairs. The same 1x3 ramp and class as the ordinary stairs (Building_Stairs: links, path, reach, rise, jumps
    // and the stairwell work unchanged; the rules that know stairs by def ask StairDefs). Stuffless gravtech, only on a
    // gravship:
    //   - Placement (PlaceWorker_ShipStairs): the whole ramp on the ship's deck (ground substructure or a ship upper deck),
    //     its exit past the top on a ship upper deck or over the ship's deck (open air above substructure or a deck, where
    //     a deck may go). Off the deck: "PZ_ShipStairsOnDeck" (also for the game's own affordance refusal, PlaceMessages).
    //   - The stairwell: the same logic as the ordinary stairs (user answer 2): a ship deck over the ramp is marked for
    //     removal (StairwellFloor.Mark, the game's remove-foundation job takes it up) and the stairs wait for it; built at
    //     once (god mode) they take it up themselves (ClearDeck). The ship stairs' exit deck can't be removed while they
    //     stand (ShipDeck.Blocker).
    //   - The landing (fix 2026-10-17, as ordinary stairs lay their slab): over open air they lay a ship upper deck at the
    //     exit when built (LayLanding; a planned deck there is cancelled), so the first stairs onto a fresh ship's next floor
    //     need no deck up there. It is an ordinary deck from then on (ship set, deck rules; the stairs hold its roof), can't
    //     be removed while the stairs stand, and goes with them unless it is in use (TakeLanding).
    //   - Part of the ship: the ramp stands on ship cells, so vanilla's OnValidSubstructure takes it along (P4 capture,
    //     floor-tagged offsets), and the link registers again when it spawns at the landing site.
    // Ordinary stairs stay refused on a gravship (PlaceWorker_Stairs.ThickRoofCheck, CanConstruct_SlabSupport_Patch).
    public static class ShipStairs
    {
        public static bool Is(BuildableDef d) => d != null && d == PZDefOf.PZ_ShipStairs;

        public static AcceptanceReport Check(IntVec3 loc, Rot4 rot, Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int k = ProjectZ.FloorOf(loc, map);
            if (a == null || k < 0 || k + 1 >= a.floors)
            {
                return "PZ_NoFloorAbove".Translate();
            }
            foreach (IntVec3 c in StairShape.Footprint(loc, rot))
            {
                if (!c.InBounds(map) || ProjectZ.FloorOf(c, map) != k)
                {
                    return "PZ_NoFloorAbove".Translate();
                }
                if (!SlabSupport.Gravship(c, map))
                {
                    return "PZ_ShipStairsOnDeck".Translate();
                }
                if (SlabSupport.ThickRoof(c, map))
                {
                    return "PZ_ThickRoof".Translate();
                }
                IntVec3 up = StairShape.Above(c, map);
                if (!up.IsValid)
                {
                    return "PZ_WellNotOpen".Translate();
                }
                if (up.GetTerrain(map) == PZDefOf.PZ_OpenAir)
                {
                    if (up.GetEdifice(map) != null || PlaceWorker_Stairs.FloorPlanned(up, map) || ShipDeck.Planned(up, map))
                    {
                        return "PZ_WellNotOpen".Translate();
                    }
                }
                else if (!CanClearDeck(up, map))
                {
                    // (a bare deck, or one with only a floor laid on it, is taken up for the stairs)
                    return "PZ_WellNotOpen".Translate();
                }
            }
            IntVec3 top = StairShape.Exit(loc, rot, map);
            if (!top.IsValid)
            {
                return "PZ_NoExitCell".Translate();
            }
            IntVec3 below = FloorRoof.Below(top, map);
            if (Stairwell.StairThingAt(below, map))
            {
                // the exit over another stairwell
                return "PZ_LandingBlocked".Translate();
            }
            if (!ShipDeck.At(top, map))
            {
                // The landing (as ordinary stairs): a ship upper deck laid with the stairs (LayLanding; or one planned
                // there), so the exit only has to be where a deck may go: over the ship's deck, nothing too tall under
                // it. The stairs hold the roof under it.
                if (top.GetTerrain(map) != PZDefOf.PZ_OpenAir || map.terrainGrid.FoundationAt(top) != null || !SlabSupport.Gravship(below, map))
                {
                    return "PZ_ShipStairsOnDeck".Translate();
                }
                if (SlabSupport.ThickRoof(below, map))
                {
                    return "PZ_ThickRoof".Translate();
                }
                Thing tall = HeightRule.TallThingAt(below, map);
                if (tall != null)
                {
                    return "PZ_TallBelow".Translate(tall.LabelShort);
                }
                if (top.GetEdifice(map) != null || PlaceWorker_Stairs.FloorPlanned(top, map))
                {
                    return "PZ_LandingBlocked".Translate();
                }
                // the deck's own vanilla rules (in the engine's footprint, ...); its support is the stairs
                foreach (PlaceWorker pw in PZDefOf.PZ_ShipDeck.PlaceWorkers)
                {
                    if (!(pw is PlaceWorker_ShipDeck))
                    {
                        AcceptanceReport r = pw.AllowsPlacing(PZDefOf.PZ_ShipDeck, top, Rot4.North, map);
                        if (!r.Accepted)
                        {
                            return r;
                        }
                    }
                }
                return AcceptanceReport.WasAccepted;
            }
            if (top.GetTerrain(map).passability != Traversability.Standable
                || map.designationManager.DesignationAt(top, DesignationDefOf.RemoveFoundation) != null)
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

        // A stairwell cell with a ship deck that can be taken up: nothing built or planned on it, no deck over it, not the
        // exit of other stairs (ShipDeck.Blocker).
        public static bool CanClearDeck(IntVec3 up, Map map)
        {
            if (!ShipDeck.At(up, map) || ShipDeck.Blocker(up, map) != null)
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
            return true;
        }

        // The landing, as ordinary stairs lay theirs (Building_Stairs.SpawnSetup): ship stairs whose exit is open air lay a
        // ship upper deck there (part of the ship from then on: the deck rules, the ship set); a deck planned there is
        // cancelled (its materials back). Returns whether a roof was laid with it (none was under it).
        public static bool LayLanding(IntVec3 top, Map map)
        {
            foreach (Thing t in map.thingGrid.ThingsListAtFast(top).ToList())
            {
                if ((t is Blueprint || t is Frame) && ShipDeck.Is(t.def.entityDefToBuild))
                {
                    t.Destroy(DestroyMode.Cancel);
                }
            }
            IntVec3 below = FloorRoof.Below(top, map);
            bool roofLaid = below.IsValid && !map.roofGrid.Roofed(below);
            map.terrainGrid.SetTerrain(top, PZDefOf.PZ_ShipDeck);
            ProjectZ.DebugLog($"ship stairs landing deck laid at {top}");
            return roofLaid;
        }

        // The ship stairs go: their landing deck goes too (TakeLanding as ordinary stairs), unless it is in use now: built
        // things on it, a deck above it, other stairs landing there.
        public static bool TakeLanding(Building_Stairs s, IntVec3 top, Map map)
        {
            if (!ShipDeck.At(top, map))
            {
                return false;
            }
            List<Thing> list = map.thingGrid.ThingsListAtFast(top);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is Building || list[i] is Blueprint || list[i] is Frame)
                {
                    return false;
                }
            }
            IntVec3 up = StairShape.Above(top, map);
            if (up.IsValid && (ShipDeck.At(up, map) || ShipDeck.Planned(up, map)))
            {
                return false;
            }
            foreach (Thing t in map.listerThings.ThingsOfDef(PZDefOf.PZ_ShipStairs))
            {
                if (t != s && StairShape.Exit(t.Position, t.Rotation, map) == top)
                {
                    return false;
                }
            }
            TerrainDef covering = map.terrainGrid.TopTerrainAt(top);
            if (covering != null && covering != PZDefOf.PZ_OpenAir)
            {
                GenLeaving.DoLeavingsFor(covering, top, map);
            }
            Slab.DropZoneCell(top, map);
            Slab.MoveOff(top, map, s);
            map.designationManager.DesignationAt(top, DesignationDefOf.RemoveFoundation)?.Delete();
            map.terrainGrid.RemoveFoundation(top, doLeavings: false);
            ProjectZ.DebugLog($"ship stairs landing deck at {top} taken with the stairs");
            return true;
        }

        // Ship stairs built at once (god mode) under a deck: the deck goes now (the game's foundation removal; a floor laid
        // on it goes with it, what stood there moves off: ShipDeck's terrain-change hook).
        public static void ClearDeck(IntVec3 up, Map map)
        {
            map.terrainGrid.RemoveFoundation(up, doLeavings: true);
            map.designationManager.DesignationAt(up, DesignationDefOf.RemoveFoundation)?.Delete();
            ProjectZ.DebugLog($"ship deck at {up} taken up for a stairwell");
        }
    }

    public class PlaceWorker_ShipStairs : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            return ShipStairs.Check(loc, rot, map);
        }
    }
}
