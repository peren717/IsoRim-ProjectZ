using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ
{
    // XML for other mods (Api 2): this building fits under a floor whatever its model height (ProjectZ's height rule
    // never counts it as too tall): <modExtensions><li Class="ProjectZ.FitsUnderFloor" /></modExtensions>
    public class FitsUnderFloor : DefModExtension
    {
    }

    // The slab height rule (user decision 2026-10-15, both directions): a building drawn taller than a storey's clearance
    // can't have a floor over any of its cells, and a floor over a cell keeps such a building from being placed under
    // it. Models are not squashed. "Drawn taller" is IsoRim's own model height (OBJ top x scale x footprint fit,
    // IsoRim.Api.ModelHeight); walls, doors and other shapes follow the wall height and never count. Blueprints and
    // frames count as what they will become.
    // Other mods (Api 2): a def with the FitsUnderFloor extension never counts; rules registered through
    // Api.RegisterHeightRule are asked next (first answer wins); then the model height. Every use of the rule (placement
    // both ways, ship decks, ship stairs, tall mountains over tall things) asks TooTall, so this is the one place.
    public static class HeightRule
    {
        // Head room under a floor, in cells: the wall height less a little (everyday buildings up to 1.60 at walls of 1.65).
        public static float Clearance => IsoRimBridge.WallHeight - 0.05f + 0.005f;

        private static readonly List<Func<BuildableDef, bool?>> rules = new List<Func<BuildableDef, bool?>>();

        public static void Register(Func<BuildableDef, bool?> rule)
        {
            if (rule != null && !rules.Contains(rule))
            {
                rules.Add(rule);
            }
        }

        public static void Unregister(Func<BuildableDef, bool?> rule) => rules.Remove(rule);

        public static bool TooTall(BuildableDef def)
        {
            ThingDef t = def as ThingDef;
            // (the elevator's base model is a storey tall by design: its shaft and cap rules decide what goes over it)
            if (t == null || t.category != ThingCategory.Building || StairDefs.Is(t) || t == ElevatorDefOf.PZ_Elevator || typeof(Building_Door).IsAssignableFrom(t.thingClass))
            {
                return false;
            }
            if (t.HasModExtension<FitsUnderFloor>())
            {
                return false;
            }
            for (int i = 0; i < rules.Count; i++)
            {
                bool? r;
                try
                {
                    r = rules[i](t);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce($"[ProjectZ] height rule {rules[i].Method.DeclaringType}.{rules[i].Method.Name} threw: {e}", rules[i].Method.GetHashCode() ^ 0x5a2004);
                    continue;
                }
                if (r.HasValue)
                {
                    return r.Value;
                }
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
        // Under a natural rock floor (tall mountains, user decision Q7) only an open one counts (a top, or dug out):
        // where the cell above is still solid rock, a tall thing fits as under vanilla's overhead mountain.
        public static bool FloorOver(IntVec3 c, Map map)
        {
            if (FloorRoof.FloorAbove(c, map))
            {
                IntVec3 above = FloorRoof.Above(c, map);
                if (Floors.KindAt(above, map) != FloorKind.NaturalRock)
                {
                    return true;
                }
                Building e = above.GetEdifice(map);
                if (e == null || !(e.def.building?.isNaturalRock ?? false))
                {
                    return true;
                }
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
