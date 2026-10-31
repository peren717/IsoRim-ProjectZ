using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ
{
    // The game's per-cell weather steps walk the whole map, a fixed share of its cells every tick in one cycle
    // (SteadyEnvironmentEffects: snow and sand fall and melt, rain washing filth, things deteriorating, hot rooms;
    // Odyssey's FreezeManager through TempTerrainManager: water freezing, thin ice melting). On a map with floors most
    // cells are open air upstairs, where those steps change nothing: PZ_OpenAir holds no snow or sand
    // (holdSnowOrSand false), takes no filth, can't freeze, has no flammability, fleck, glow or heat; and with no thing
    // on the cell there is nothing to wash, deteriorate, burn or melt. Such a cell is passed over.
    // The cycle itself is untouched (the skip is inside the step for one cell): every other cell is visited on exactly
    // the tick it was before. A cell with anything on it (a building, a flier, an item) or any other terrain is done as
    // before. Maps without floors: nothing changes.
    internal static class OpenAirSkip
    {
        // Test switch (-projectznoairskip): every cell is done, as before.
        public static bool Off;

        internal static bool Skip(Map map, IntVec3 c)
        {
            return !Off && OpenEmpty(map, c);
        }

        // An empty open-air cell upstairs (with or without the switch; the airskip test checks these).
        internal static bool OpenEmpty(Map map, IntVec3 c)
        {
            if (map == null)
            {
                return false;
            }
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || c.z < a.groundZ)
            {
                return false;
            }
            int i = map.cellIndices.CellToIndex(c);
            return map.terrainGrid.TerrainAt(i) == PZDefOf.PZ_OpenAir && map.thingGrid.ThingsListAtFast(i).Count == 0;
        }
    }

    [HarmonyPatch(typeof(SteadyEnvironmentEffects), "DoCellSteadyEffects")]
    public static class SteadyEffects_OpenAir_Patch
    {
        public static bool Prefix(Map ___map, IntVec3 c)
        {
            return !OpenAirSkip.Skip(___map, c);
        }
    }

    [HarmonyPatch(typeof(FreezeManager), nameof(FreezeManager.DoCellSteadyEffects))]
    public static class Freeze_OpenAir_Patch
    {
        public static bool Prepare()
        {
            return ModsConfig.OdysseyActive;
        }

        public static bool Prefix(Map ___map, IntVec3 c)
        {
            return !OpenAirSkip.Skip(___map, c);
        }
    }
}
