using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ
{
    // The slab height rule (user decision 2026-10-15, both directions): a building drawn taller than a storey's clearance
    // can't have a floor over any of its cells, and a floor over a cell keeps such a building from being placed under
    // it. Models are not squashed. "Drawn taller" is IsoRim's own model height (OBJ top x scale x footprint fit,
    // IsoRim.Api.ModelHeight); walls, doors and other shapes follow the wall height and never count. Blueprints and
    // frames count as what they will become.
    public static class HeightRule
    {
        // Head room under a floor, in cells: the wall height less a little (everyday buildings up to 1.60 at walls of 1.65).
        public static float Clearance => IsoRimBridge.WallHeight - 0.05f + 0.005f;

        public static bool TooTall(BuildableDef def)
        {
            ThingDef t = def as ThingDef;
            if (t == null || t.category != ThingCategory.Building || StairDefs.Is(t) || typeof(Building_Door).IsAssignableFrom(t.thingClass))
            {
                return false;
            }
            return IsoRimBridge.ModelHeight(t) > Clearance;
        }

        // The building (or blueprint, or frame) in a cell that is too tall for a floor over it; null if none.
        public static Thing TallThingAt(IntVec3 c, Map map)
        {
            if (!c.IsValid || !c.InBounds(map))
            {
                return null;
            }
            List<Thing> list = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < list.Count; i++)
            {
                Thing t = list[i];
                BuildableDef def = t is Blueprint || t is Frame ? t.def.entityDefToBuild : t.def;
                if (TooTall(def))
                {
                    return t;
                }
            }
            return null;
        }

        // A floor (built, or a blueprint or frame of one, or the landing of stairs still to be built) right over a cell.
        public static bool FloorOver(IntVec3 c, Map map)
        {
            if (FloorRoof.FloorAbove(c, map))
            {
                return true;
            }
            IntVec3 up = StairShape.Above(c, map);
            return up.IsValid && up.InBounds(map) && (PlaceWorker_Stairs.FloorPlanned(up, map) || StairwellFloor.IsStairsExit(up, map));
        }
    }

    // Every placement (designators, blueprints, auto-rebuild, install) goes through here: a tall building under a floor
    // is refused. (The other direction is PlaceWorker_UpperFloor.)
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanPlaceBlueprintAt_NewTemp))]
    public static class CanPlaceBlueprintAt_HeightRule_Patch
    {
        public static void Postfix(BuildableDef entDef, IntVec3 center, Rot4 rot, Map map, ref AcceptanceReport __result)
        {
            if (!__result.Accepted || map == null || ProjectZ.Annex(map) == null || !HeightRule.TooTall(entDef))
            {
                return;
            }
            foreach (IntVec3 c in GenAdj.OccupiedRect(center, rot, entDef.Size))
            {
                if (HeightRule.FloorOver(c, map))
                {
                    __result = "PZ_TooTallUnderFloor".Translate();
                    return;
                }
            }
        }
    }
}
