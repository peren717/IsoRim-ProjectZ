using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // Multi-level gravships, phase P1 (isorim-pz-multilevel-gravship.md, v2.0 - v2.4): the ship upper deck PZ_ShipDeck.
    // It IS vanilla gravship substructure (a copy of the Substructure def with its tag and affordances), laid as the
    // foundation over open air on an upper floor, only right above ground substructure or another deck (no overhang).
    // Because of the tag, vanilla treats deck cells as substructure by itself (ship parts placed on them, the
    // substructure overlay, "remove foundation"); ordinary slabs and stairs over it are refused by SlabSupport.ColumnBlocked.
    //   - Placement: PlaceWorker_ShipDeck (Check); a planned deck waits for the deck or substructure below (CanConstruct).
    //   - Support: only the substructure or deck below holds it, never walls. The roof under a deck (PZ_FloorRoof) counts
    //     as held while that cell has a substructure foundation (the game's support checks, postfixed here).
    //   - Removal: the vanilla remove-foundation tool; refused under a deck ("remove the deck above first"). What stands on
    //     a removed deck moves off (Slab.MoveOff), what was laid on it goes.
    //   - Falling: a deck whose cell below lost its substructure falls through the slab collapse (SlabCollapse): its roof
    //     cell is marked to collapse, the game's collapse buffer drops it with the deck (one hook there, S1). Decks above it
    //     follow. This runs on any foundation removal, after a gravship takes off (its ground cells go, P1 decks stay
    //     behind) and once when a map loads.
    // Footprint: until the 3D ship set (P2), the engine and extender footprint is tested on the ground projection
    // (GroundCellOf of both ends), so a deck over the ship's substructure is inside it like the cell below.
    // Without Odyssey the def is not loaded (MayRequire) and nothing here does anything.
    public static class ShipDeck
    {
        public static bool Is(TerrainDef t) => t != null && t == PZDefOf.PZ_ShipDeck;

        public static bool Is(BuildableDef d) => d is TerrainDef t && Is(t);

        // An upper floor cell with a ship deck (bare or under a floor laid on it).
        public static bool At(IntVec3 c, Map map) => c.IsValid && c.InBounds(map) && Is(map.terrainGrid.FoundationAt(c));

        // A blueprint or frame of gravship substructure (vanilla's or a deck) on c.
        public static bool Planned(IntVec3 c, Map map)
        {
            if (!c.IsValid || !c.InBounds(map))
            {
                return false;
            }
            List<Thing> list = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < list.Count; i++)
            {
                if ((list[i] is Blueprint || list[i] is Frame) && list[i].def.entityDefToBuild is TerrainDef t && t.IsSubstructure)
                {
                    return true;
                }
            }
            return false;
        }

        // Where a deck may go: an upper floor cell (open air: the game's affordance check) right above substructure or a
        // deck, built or planned; not over a stairwell or anything too tall for the storey (as slabs).
        public static AcceptanceReport Check(IntVec3 loc, Map map)
        {
            if (ProjectZ.FloorOf(loc, map) < 1)
            {
                return PlaceWorker_UpperFloor.ViewFloorAbove();
            }
            IntVec3 below = FloorRoof.Below(loc, map);
            if (!SlabSupport.Gravship(below, map) && !Planned(below, map))
            {
                return "PZ_DeckNeedsShipBelow".Translate();
            }
            if (SlabSupport.ThickRoof(below, map))
            {
                return "PZ_ThickRoof".Translate();
            }
            if (Stairwell.StairThingAt(below, map))
            {
                return "PZ_StairwellBelow".Translate();
            }
            Thing tall = HeightRule.TallThingAt(below, map);
            if (tall != null)
            {
                return "PZ_TallBelow".Translate(tall.LabelShort);
            }
            return AcceptanceReport.WasAccepted;
        }

        // Why the substructure (or deck) at c can't be removed now: a deck built or planned right above it. Null: no reason.
        public static string Blocker(IntVec3 c, Map map)
        {
            if (!SlabSupport.Gravship(c, map))
            {
                return null;
            }
            IntVec3 up = StairShape.Above(c, map);
            if (up.IsValid && (At(up, map) || Planned(up, map)))
            {
                return "PZ_DeckAboveFirst".Translate();
            }
            return null;
        }

        // The roof at c is the underside of a deck that stands on substructure: held (decks hold themselves up by what is
        // below them, not by walls).
        public static bool Held(IntVec3 c, Map map)
        {
            return ProjectZ.Annex(map) != null && SlabSupport.Gravship(c, map) && At(StairShape.Above(c, map), map);
        }

        // The cell 'below' lost its substructure: a deck right above it falls (the slab collapse, through the game's
        // collapse buffer: the roof cell under the deck drops with it next tick). Without that roof (a gravship took it
        // with the ground cells) the deck falls at once.
        public static void SupportLost(IntVec3 below, Map map)
        {
            IntVec3 up = StairShape.Above(below, map);
            if (!At(up, map) || SlabSupport.Gravship(below, map))
            {
                return;
            }
            if (map.roofGrid.RoofAt(below) == PZDefOf.PZ_FloorRoof)
            {
                map.roofCollapseBuffer.MarkToCollapse(below);
            }
            else
            {
                SlabCollapse.BufferCollapse(below, map, null);
            }
            ProjectZ.DebugLog($"ship deck at {up} lost its support at {below}: falls");
        }

        // Every deck over a cell without substructure falls (after a take-off, at load). Returns how many were found.
        public static int CheckAll(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || PZDefOf.PZ_ShipDeck == null)
            {
                return 0;
            }
            int n = 0;
            for (int k = 1; k < a.floors; k++)
            {
                for (int z = 0; z < a.groundZ; z++)
                {
                    for (int x = 0; x < map.Size.x; x++)
                    {
                        IntVec3 u = ProjectZ.CellOnFloor(new IntVec3(x, 0, z), k, map);
                        if (At(u, map) && !SlabSupport.Gravship(FloorRoof.Below(u, map), map))
                        {
                            SupportLost(FloorRoof.Below(u, map), map);
                            n++;
                        }
                    }
                }
            }
            if (n > 0)
            {
                ProjectZ.DebugLog($"map {map}: {n} ship deck cell(s) without support");
            }
            return n;
        }

        // At load (after everything is spawned).
        public static void CheckAllLater(Map map)
        {
            if (PZDefOf.PZ_ShipDeck != null)
            {
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    if (map != null && Find.Maps.Contains(map))
                    {
                        CheckAll(map);
                    }
                });
            }
        }
    }

    public class PlaceWorker_ShipDeck : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            return ShipDeck.Check(loc, map);
        }
    }

    // A floor laid on a deck over open air keeps open air as its under layer (the game would put its default under
    // terrain there, sand, which a removed floor would leave behind on an upper floor).
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain))]
    public static class SetTerrain_ShipDeck_Patch
    {
        private static readonly AccessTools.FieldRef<TerrainGrid, Map> mapRef = AccessTools.FieldRefAccess<TerrainGrid, Map>("map");
        private static readonly AccessTools.FieldRef<TerrainGrid, TerrainDef[]> underRef = AccessTools.FieldRefAccess<TerrainGrid, TerrainDef[]>("underGrid");

        public static void Prefix(TerrainGrid __instance, IntVec3 c, TerrainDef newTerr, out bool __state)
        {
            __state = false;
            if (newTerr == null || !newTerr.layerable || newTerr.isFoundation || newTerr.temporary)
            {
                return;
            }
            Map map = mapRef(__instance);
            if (map != null && c.InBounds(map) && ProjectZ.IsAnnex(c, map) && __instance.FoundationAt(c) != null
                && __instance.UnderTerrainAt(c) == null && __instance.TopTerrainAt(c) == PZDefOf.PZ_OpenAir)
            {
                __state = true;
            }
        }

        public static void Postfix(TerrainGrid __instance, IntVec3 c, bool __state)
        {
            if (__state)
            {
                underRef(__instance)[mapRef(__instance).cellIndices.CellToIndex(c)] = PZDefOf.PZ_OpenAir;
            }
        }
    }

    // A substructure foundation removed (the remove tool, god mode, a destroyed foundation, a falling deck): a deck above
    // it falls; a deck taken away leaves open air and moves off what stood on it.
    [HarmonyPatch(typeof(TerrainGrid), "DoTerrainChangedEffects")]
    public static class TerrainChanged_ShipDeck_Patch
    {
        private static readonly AccessTools.FieldRef<TerrainGrid, Map> mapRef = AccessTools.FieldRefAccess<TerrainGrid, Map>("map");

        public static void Postfix(TerrainGrid __instance, IntVec3 c, TerrainDef oldFoundation)
        {
            if (oldFoundation == null || !oldFoundation.IsSubstructure || __instance.FoundationAt(c) != null)
            {
                return;
            }
            Map map = mapRef(__instance);
            if (map == null || ProjectZ.Annex(map) == null)
            {
                return;
            }
            if (ShipDeck.Is(oldFoundation) && ProjectZ.FloorOf(c, map) >= 1)
            {
                if (__instance.TopTerrainAt(c) != PZDefOf.PZ_OpenAir)
                {
                    // (a floor still laid on it: only god mode removes a foundation before the floor)
                    __instance.SetTerrain(c, PZDefOf.PZ_OpenAir);
                }
                Slab.DropZoneCell(c, map);
                Slab.MoveOff(c, map);
            }
            ShipDeck.SupportLost(c, map);
        }
    }

    // ---- support: the roof under a deck is held by the substructure below it

    [HarmonyPatch(typeof(RoofCollapseUtility), nameof(RoofCollapseUtility.WithinRangeOfRoofHolder))]
    public static class WithinRangeOfRoofHolder_ShipDeck_Patch
    {
        public static void Postfix(IntVec3 c, Map map, ref bool __result)
        {
            if (!__result && ShipDeck.Held(c, map))
            {
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(RoofCollapseUtility), nameof(RoofCollapseUtility.ConnectedToRoofHolder))]
    public static class ConnectedToRoofHolder_ShipDeck_Patch
    {
        public static void Postfix(IntVec3 c, Map map, ref bool __result)
        {
            if (!__result && ShipDeck.Held(c, map))
            {
                __result = true;
            }
        }
    }

    // The game's "flying roof" test: a roofed area with no holder in it all falls. A held deck underside counts as one,
    // so the roofs joined to it stay unless they are too far from a real holder (WithinRangeOfRoofHolder).
    [HarmonyPatch(typeof(RoofCollapseCellsFinder), nameof(RoofCollapseCellsFinder.ConnectsToRoofHolder))]
    public static class ConnectsToRoofHolder_ShipDeck_Patch
    {
        public static void Postfix(IntVec3 c, Map map, ref bool __result)
        {
            if (__result || PZDefOf.PZ_ShipDeck == null || ProjectZ.Annex(map) == null)
            {
                return;
            }
            bool held = false;
            map.floodFiller.FloodFill(c, x => x.Roofed(map), x =>
            {
                if (ShipDeck.Held(x, map))
                {
                    held = true;
                    return true;
                }
                return false;
            });
            if (held)
            {
                __result = true;
            }
        }
    }

    // ---- removal: not under a deck

    // Under a deck: blocked. On an upper floor deck itself, as for slabs: only built things block (vanilla would also count
    // items and pawns, whose affordance open air lacks; they are moved off instead).
    [HarmonyPatch(typeof(WorkGiver_ConstructRemoveFoundation), nameof(WorkGiver_ConstructRemoveFoundation.AnyBuildingBlockingFoundationRemoval))]
    public static class FoundationRemovalBlocked_ShipDeck_Patch
    {
        public static bool Prefix(IntVec3 c, Map map, ref bool __result)
        {
            if (!ShipDeck.At(c, map) || ProjectZ.FloorOf(c, map) < 1)
            {
                return true;
            }
            __result = ShipDeck.Blocker(c, map) != null;
            List<Thing> list = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < list.Count && !__result; i++)
            {
                __result = list[i] is Building || list[i] is Blueprint || list[i] is Frame;
            }
            return false;
        }

        public static void Postfix(IntVec3 c, Map map, ref bool __result)
        {
            if (!__result && ProjectZ.Annex(map) != null && ShipDeck.Blocker(c, map) != null)
            {
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(Designator_RemoveFoundation), nameof(Designator_RemoveFoundation.CanDesignateCell))]
    public static class RemoveFoundationDesignate_ShipDeck_Patch
    {
        public static void Postfix(Designator_RemoveFoundation __instance, IntVec3 c, ref AcceptanceReport __result)
        {
            if (!__result.Accepted && !__result.Reason.NullOrEmpty() && ProjectZ.Annex(__instance.Map) != null)
            {
                string why = ShipDeck.Blocker(c, __instance.Map);
                if (why != null)
                {
                    __result = why;
                }
            }
        }
    }

    // A planned deck is built after the deck or substructure below it.
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanConstruct), new[] { typeof(Thing), typeof(Pawn), typeof(bool), typeof(bool), typeof(JobDef) })]
    public static class CanConstruct_ShipDeck_Patch
    {
        public static bool Prefix(Thing t, ref bool __result)
        {
            if (t == null || !t.Spawned || !ShipDeck.Is(t.def.entityDefToBuild) || ProjectZ.Annex(t.Map) == null)
            {
                return true;
            }
            if (!SlabSupport.Gravship(FloorRoof.Below(t.Position, t.Map), t.Map))
            {
                JobFailReason.Is("PZ_DeckBelowFirst".Translate());
                __result = false;
                return false;
            }
            return true;
        }
    }

    // The engine and extender footprint on the ground projection (P1; P2 makes it a 3D sphere): a cell on any floor is in
    // range when its ground cell is in range of the maker's ground cell. Identical to vanilla for ground cells.
    [HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.InsideFootprint))]
    public static class InsideFootprint_ShipDeck_Patch
    {
        public static bool Prefix(IntVec3 loc, Map map, List<Thing> footprintMakers, Thing thingToExclude, ref bool __result)
        {
            if (!ModsConfig.OdysseyActive || ProjectZ.Annex(map) == null)
            {
                return true;
            }
            __result = false;
            IntVec3 g = ProjectZ.GroundCellOf(loc, map);
            if (!g.IsValid)
            {
                return false;
            }
            if (footprintMakers == null)
            {
                footprintMakers = map.listerThings.ThingsInGroup(ThingRequestGroup.SubstructureFootprint);
            }
            foreach (Thing maker in footprintMakers)
            {
                if (maker.Spawned && maker.TryGetComp(out CompSubstructureFootprint comp) && comp.Valid && (thingToExclude == null || maker != thingToExclude))
                {
                    IntVec3 mg = ProjectZ.GroundCellOf(maker.Position, map);
                    if (mg.IsValid && g.InHorDistOf(mg, comp.Props.radius))
                    {
                        __result = true;
                        return false;
                    }
                }
            }
            return false;
        }
    }

    // After a take-off the ship's ground cells are gone (vanilla, without terrain events): the decks over them fall.
    [HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.GenerateGravship))]
    public static class GenerateGravship_ShipDeck_Patch
    {
        public static void Prefix(Building_GravEngine engine, out Map __state)
        {
            __state = engine?.Map;
        }

        public static void Postfix(Map __state)
        {
            if (__state != null)
            {
                ShipDeck.CheckAll(__state);
            }
        }
    }
}
