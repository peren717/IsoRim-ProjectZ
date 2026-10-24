using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ
{
    // Api 4, RegisterDeckTerrain: other foundations that count as ship upper decks (ShipDeck.Is). On an upper floor such a
    // foundation is built like the deck: directly on the floor support over open air (PZ_FloorSupport stands in for the
    // ground affordance it needs, e.g. "Walkable"), only where a deck may go (ShipDeck.Check). Never over a deck or a floor:
    // those cells give no floor support. On the ground its own rules stay. Its terrain def is not changed, so nothing
    // else that needs the same affordance gains anything on upper floors.
    public static class DeckTerrain
    {
        private static TerrainAffordanceDef floorSupport;

        public static TerrainAffordanceDef FloorSupport => floorSupport ?? (floorSupport = DefDatabase<TerrainAffordanceDef>.GetNamedSilentFail("PZ_FloorSupport"));

        // Every cell of the footprint is on an upper floor and gives the floor support (open air, nothing laid on it).
        public static bool OnFloorSupport(BuildableDef def, IntVec3 c, Rot4 rot, Map map)
        {
            TerrainAffordanceDef fs = FloorSupport;
            if (fs == null || map == null || ProjectZ.Annex(map) == null)
            {
                return false;
            }
            foreach (IntVec3 x in GenAdj.OccupiedRect(c, rot, def.Size))
            {
                if (!x.InBounds(map) || ProjectZ.FloorOf(x, map) < 1 || !x.GetAffordances(map).Contains(fs))
                {
                    return false;
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanBuildOnTerrain))]
    public static class CanBuildOnTerrain_DeckTerrain_Patch
    {
        public static void Postfix(BuildableDef entDef, IntVec3 c, Map map, Rot4 rot, ref bool __result)
        {
            if (!__result && ShipDeck.IsExtra(entDef) && DeckTerrain.OnFloorSupport(entDef, c, rot, map))
            {
                __result = true;
            }
        }
    }

    // The deck's own placement rules for a registered deck foundation on an upper floor (it has no deck place worker).
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanPlaceBlueprintAt_NewTemp))]
    public static class CanPlaceBlueprintAt_DeckTerrain_Patch
    {
        public static void Postfix(BuildableDef entDef, IntVec3 center, Map map, ref AcceptanceReport __result)
        {
            if (!__result.Accepted || !ShipDeck.IsExtra(entDef) || map == null || ProjectZ.Annex(map) == null || ProjectZ.FloorOf(center, map) < 1)
            {
                return;
            }
            // never over a deck or another foundation (another mod may let it replace foundations on the ground)
            if (map.terrainGrid.FoundationAt(center) != null)
            {
                __result = "PZ_AlreadyFloor".Translate();
                return;
            }
            AcceptanceReport r = ShipDeck.Check(center, map);
            if (!r.Accepted)
            {
                __result = r;
            }
        }
    }
}
