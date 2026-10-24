using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ.Patches
{
    // Plain refusals for building between floors. The game refuses a terrain it can't build on with its own affordance
    // message ("requires <affordance>") before any place worker runs, which says nothing useful here:
    //   - an upper floor on the ground (working on the ground) or on a cell that already has a floor: say what to do;
    //     elsewhere the upper floor's own rules (PlaceWorker_UpperFloor.Check: roof below, stairwell, tall building);
    //   - anything else on open air upstairs: lay a floor first.
    // Ground-only buildings (GroundOnly, an XML list) anywhere above the ground are refused first, whatever else the game
    // says about them.
    // Every placement goes through GenConstruct.CanPlaceBlueprintAt_NewTemp (designators, copies, rebuilds, installs).
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanPlaceBlueprintAt_NewTemp))]
    public static class CanPlaceBlueprintAt_Messages_Patch
    {
        public static void Postfix(BuildableDef entDef, IntVec3 center, Rot4 rot, Map map, Thing thing, ThingDef stuffDef, ref AcceptanceReport __result)
        {
            if (entDef != null && map != null && GroundOnly.Is(entDef) && GroundOnly.Upstairs(entDef, center, rot, map))
            {
                __result = "PZ_GroundOnly".Translate();
                return;
            }
            // (a damaged slab is not changeable: no floor goes on it, SlabDamage)
            if (!__result.Accepted && entDef is TerrainDef && !SlabDefs.Is(entDef) && map != null && center.InBounds(map) && SlabDefs.IsDamaged(center.GetTerrain(map)))
            {
                __result = "PZ_DamagedSlabNoFloor".Translate();
                return;
            }
            if (__result.Accepted || entDef == null || map == null || ProjectZ.Annex(map) == null || !IsTerrainRefusal(entDef, stuffDef ?? thing?.Stuff, __result.Reason))
            {
                return;
            }
            // (a ship deck as a slab, with its own rules: ShipDeck.Check)
            // (a registered deck foundation only upstairs: on the ground its own rules and messages stay)
            if (SlabDefs.Is(entDef) || ShipDeck.Is(entDef) && (!ShipDeck.IsExtra(entDef) || ProjectZ.FloorOf(center, map) >= 1))
            {
                if (ProjectZ.FloorOf(center, map) < 1)
                {
                    __result = PlaceWorker_UpperFloor.ViewFloorAbove();
                    return;
                }
                if (center.GetTerrain(map) != PZDefOf.PZ_OpenAir)
                {
                    __result = "PZ_AlreadyFloor".Translate();
                    return;
                }
                AcceptanceReport r = ShipDeck.Is(entDef) ? ShipDeck.Check(center, map) : PlaceWorker_UpperFloor.Check(center, map);
                if (!r.Accepted)
                {
                    __result = r;
                }
                return;
            }
            // (ship stairs off the ship's deck: their own short reason, ShipStairs)
            if (PZDefOf.PZ_ShipStairs != null && entDef == PZDefOf.PZ_ShipStairs)
            {
                __result = "PZ_ShipStairsOnDeck".Translate();
                return;
            }
            foreach (IntVec3 c in GenAdj.OccupiedRect(center, rot, entDef.Size))
            {
                if (c.InBounds(map) && c.GetTerrain(map) == PZDefOf.PZ_OpenAir)
                {
                    __result = "PZ_NeedsFloorFirst".Translate();
                    return;
                }
            }
        }

        // The game's own "terrain can't support" refusal (GenConstruct.CanPlaceBlueprintAt_NewTemp, same keys and order).
        private static bool IsTerrainRefusal(BuildableDef entDef, ThingDef stuffDef, string reason)
        {
            if (reason.NullOrEmpty())
            {
                return false;
            }
            TerrainAffordanceDef need = entDef.GetTerrainAffordanceNeed(stuffDef);
            if (need == null)
            {
                return reason == "TerrainCannotSupport".Translate(entDef).CapitalizeFirst();
            }
            if (entDef.useStuffTerrainAffordance && stuffDef != null)
            {
                return reason == "TerrainCannotSupport_TerrainAffordanceFromStuff".Translate(entDef, need, stuffDef).CapitalizeFirst();
            }
            return reason == "TerrainCannotSupport_TerrainAffordance".Translate(entDef, need).CapitalizeFirst();
        }
    }
}
