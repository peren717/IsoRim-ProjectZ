using System.Collections.Generic;
using RimWorld;
using Verse;

namespace ProjectZ
{
    // Multi-level gravships, phase P5 (isorim-pz-multilevel-gravship.md v2.1 G, user answers v2.5): the ship stairs
    // PZ_ShipStairs. The same 1x3 ramp and class as the ordinary stairs (Building_Stairs: links, path, reach, rise, jumps
    // and the stairwell work unchanged; the rules that know stairs by def ask StairDefs). Stuffless gravtech, only on a
    // gravship:
    //   - Placement (PlaceWorker_ShipStairs): the whole ramp on the ship's deck (ground substructure or a ship upper deck),
    //     its exit past the top on a ship upper deck. Off the deck: "PZ_ShipStairsOnDeck" (also for the game's own
    //     affordance refusal, PlaceMessages).
    //   - The stairwell: the same logic as the ordinary stairs (user answer 2): a ship deck over the ramp is marked for
    //     removal (StairwellFloor.Mark, the game's remove-foundation job takes it up) and the stairs wait for it; built at
    //     once (god mode) they take it up themselves (ClearDeck). The ship stairs' exit deck can't be removed while they
    //     stand (ShipDeck.Blocker).
    //   - No landing is laid (their exit is always a deck).
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
            if (!ShipDeck.At(top, map))
            {
                return "PZ_ShipStairsOnDeck".Translate();
            }
            if (Stairwell.StairThingAt(FloorRoof.Below(top, map), map) || top.GetTerrain(map).passability != Traversability.Standable
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
