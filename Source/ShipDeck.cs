using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // Multi-level gravships, phase P1 (isorim-pz-multilevel-gravship.md, v2.0 - v2.4): the ship upper deck PZ_ShipDeck.
    // It IS vanilla gravship substructure (a copy of the Substructure def with its tag and affordances), laid as the
    // foundation over open air on an upper floor, only right above the ship's column: ground substructure, another deck
    // or the opening of ship stairs (ShipColumn; no overhang).
    // Because of the tag, vanilla treats deck cells as substructure by itself (ship parts placed on them, the
    // substructure overlay, "remove foundation"); ordinary slabs and stairs over it are refused by SlabSupport.BuildBlocked.
    //   - Placement: PlaceWorker_ShipDeck (Check); a planned deck waits for the deck or substructure below (CanConstruct).
    //   - Support (user decision 2026-10-17): the game's roof rules, as for any upper floor: the roof under a deck
    //     (PZ_FloorRoof) needs a wall, column or stairs within range below, at placement (planned decks next to it count)
    //     and when built; without one it collapses with the deck. Separately, a deck needs a ship column cell right
    //     below it (placement, and the fall below).
    //   - Removal: the vanilla remove-foundation tool; refused under a deck ("remove the deck above first"). What stands on
    //     a removed deck falls to the floor below (Slab.MoveOff, the gravity rule; the worker stands on a next cell,
    //     WorkSpot), what was laid on it goes.
    //   - Falling: a deck whose cell below lost its substructure falls through the slab collapse (SlabCollapse): its roof
    //     cell is marked to collapse, the game's collapse buffer drops it with the deck (one hook there, S1). Decks above it
    //     follow. This runs on any foundation removal, after a gravship takes off (decks left out of the ship set stay
    //     behind and fall) and once when a map loads.
    // Ship set, footprint and launch (P2): ShipSet.cs. Decks over the ship are ship cells; the engine and extender
    // footprint is a 3D sphere.
    // Without Odyssey the def is not loaded (MayRequire) and nothing here does anything.

    // The ship's column (fix 2026-10-17, user rule "a staircase governs only its own next floor"): a cell that counts as
    // part of the ship for anything above it. Gravship substructure or a ship upper deck; with countPlanned also a
    // blueprint or frame of one; and the stairwell opening of ship stairs (open air over a ramp, built or planned,
    // ShipStairs.Opening): it flies with the ship, so a deck may go over it as over any deck. Every "is there ship right
    // below" question asks this: deck placement and construction, the ship stairs' exit, the deck fall (runtime and at
    // load), the ship set's column walk, and the ordinary-slab and ordinary-stairs refusal over a ship.
    public static class ShipColumn
    {
        public static bool Cell(IntVec3 c, Map map, bool countPlanned = false)
        {
            return SlabSupport.Gravship(c, map) || ShipStairs.Opening(c, map) || countPlanned && ShipDeck.Planned(c, map);
        }
    }

    public static class ShipDeck
    {
        public static bool Is(TerrainDef t) => t != null && t == PZDefOf.PZ_ShipDeck;

        public static bool Is(BuildableDef d) => d is TerrainDef t && Is(t);

        // An upper floor cell with a ship deck (bare or under a floor laid on it).
        public static bool At(IntVec3 c, Map map) => c.IsValid && c.InBounds(map) && Is(map.terrainGrid.FoundationAt(c));

        // A deck on an upper floor (the ground's substructure is vanilla's).
        public static bool Upper(IntVec3 c, Map map) => ProjectZ.Annex(map) != null && At(c, map) && ProjectZ.FloorOf(c, map) >= 1;

        // Where a worker stands to take deck c up (compat G2: what stands on a removed deck falls, so never on c itself):
        // a cell next to it (4 ways) on its floor that the pawn can stand on and reach; one not marked for removal itself
        // first, then the nearest to the pawn. Invalid: none.
        public static IntVec3 WorkSpot(Pawn pawn, IntVec3 c)
        {
            Map map = pawn.Map;
            IntVec3 best = IntVec3.Invalid;
            int bestScore = int.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                IntVec3 x = c + GenAdj.CardinalDirections[i];
                if (!x.InBounds(map) || !ProjectZ.SameBand(x, c, map) || !x.Standable(map) || !pawn.CanReach(x, PathEndMode.OnCell, Danger.Deadly))
                {
                    continue;
                }
                int score = (map.designationManager.DesignationAt(x, DesignationDefOf.RemoveFoundation) != null ? 1000000 : 0) + pawn.Position.DistanceToSquared(x);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = x;
                }
            }
            return best;
        }

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

        // Where a deck may go: an upper floor cell (open air: the game's affordance check) right above the ship's column
        // (substructure or a deck, built or planned, or a ship stairs opening: ShipColumn); not over a stairwell or
        // anything too tall for the storey (as slabs).
        public static AcceptanceReport Check(IntVec3 loc, Map map)
        {
            if (ProjectZ.FloorOf(loc, map) < 1)
            {
                return PlaceWorker_UpperFloor.ViewFloorAbove();
            }
            IntVec3 below = FloorRoof.Below(loc, map);
            if (!ShipColumn.Cell(below, map, countPlanned: true))
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
            // roof support as for any upper floor (the game's roof rules; decks planned next to it count)
            if (!SlabSupport.Supported(below, map, countPlanned: true))
            {
                return "PZ_NoSupportBelow".Translate();
            }
            return AcceptanceReport.WasAccepted;
        }

        // Why the substructure (or deck) at c can't be removed now: a deck built or planned right above it. Null: no reason.
        // A deck over a ship stairs ramp (forWell: about to be one) may go though: it becomes the stairs' opening, still
        // the ship's column for the deck above (ShipColumn).
        public static string Blocker(IntVec3 c, Map map, bool forWell = false)
        {
            if (!SlabSupport.Gravship(c, map))
            {
                return null;
            }
            IntVec3 up = StairShape.Above(c, map);
            if (up.IsValid && (At(up, map) || Planned(up, map)) && !forWell && !ShipStairs.OverRamp(c, map))
            {
                return "PZ_DeckAboveFirst".Translate();
            }
            // (P5: the exit of ship stairs stays while they stand, as a slab landing does)
            if (ProjectZ.FloorOf(c, map) >= 1 && StairwellFloor.IsStairsExit(c, map))
            {
                return "PZ_StairsLandHere".Translate();
            }
            return null;
        }

        // The cell 'below' lost its substructure (it is no ship column cell now): a deck right above it falls (the slab
        // collapse, through the game's collapse buffer: the roof cell under the deck drops with it next tick). Without that roof (a gravship took it
        // with the ground cells) the deck falls at once.
        public static void SupportLost(IntVec3 below, Map map)
        {
            IntVec3 up = StairShape.Above(below, map);
            if (!At(up, map) || ShipColumn.Cell(below, map))
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

        // Every deck over a cell that is no ship column cell falls (after a take-off, at load). Returns how many were found.
        public static int CheckAll(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || PZDefOf.PZ_ShipDeck == null)
            {
                return 0;
            }
            int n = 0;
            for (int k = 1; k <= a.MaxLevel; k++)
            {
                for (int z = 0; z < a.groundZ; z++)
                {
                    for (int x = 0; x < map.Size.x; x++)
                    {
                        IntVec3 u = ProjectZ.CellOnFloor(new IntVec3(x, 0, z), k, map);
                        if (At(u, map) && !ShipColumn.Cell(FloorRoof.Below(u, map), map))
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
    // it falls; a deck taken away leaves open air and what stood on it falls (Slab.MoveOff).
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

    // An upper deck is taken up from a cell next to it (WorkSpot; the job's target B, Slabs.cs): none, no job.
    [HarmonyPatch(typeof(WorkGiver_ConstructRemoveFoundation), nameof(WorkGiver_ConstructRemoveFoundation.HasJobOnCell))]
    public static class RemoveFoundationHasJob_ShipDeck_Patch
    {
        public static void Postfix(Pawn pawn, IntVec3 c, ref bool __result)
        {
            if (__result && ShipDeck.Upper(c, pawn.Map) && !ShipDeck.WorkSpot(pawn, c).IsValid)
            {
                JobFailReason.Is("PZ_NoWorkSpot".Translate());
                __result = false;
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
            if (!ShipColumn.Cell(FloorRoof.Below(t.Position, t.Map), t.Map))
            {
                JobFailReason.Is("PZ_DeckBelowFirst".Translate());
                __result = false;
                return false;
            }
            if (!SlabSupport.Supported(FloorRoof.Below(t.Position, t.Map), t.Map))
            {
                JobFailReason.Is("PZ_NoSupportBelow".Translate());
                __result = false;
                return false;
            }
            return true;
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
