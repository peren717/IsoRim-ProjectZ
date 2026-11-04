using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ
{
    // Tall mountains: THE rule for thick roofs (user 2026-11-04): a cell has a thick roof if and only if the cell right
    // above it holds rock. Rock under rock keeps the overhead mountain: a tunnel there stays roofed. The top rock layer, edge
    // rock and lone rock have no rock above: no thick roof, and mining them opens the sky (BareTop).
    //   - no rock above, a floor above: the thin rock roof (a natural floor's ceiling, Floors.CeilingFor). It needs the game's
    //     roof support and falls with its floor (DropRoofCellList_Patch), as vanilla's thin roofs; rock holds roofs, so solid
    //     rock never starts that;
    //   - no rock above, nothing above (the top level): no roof, as vanilla's outermost rock;
    //   - CollapsedRocks are debris, not rock.
    // One place answers it (For). Everything goes through it: generation (Mountains.Generate), every roof written
    // (SetRoof_FloorRoof_Patch), rock going (DesignationManager_MineMarks_Patch: mined from any side, blasted, dropped) and
    // rock coming (Building_SpawnRock_Patch). Maps from before the rule keep the roofs they have until rock above them goes:
    // no load-time pass, a thick roof over a player's hall must not turn into one that needs support by itself.
    public static class RockRoof
    {
        // (tests: the rule off, with Mountains.ThickMin 1 the heights and roofs of a map from before it)
        public static bool Off;

        public static bool IsRock(IntVec3 c, Map map)
        {
            return c.IsValid && c.InBounds(map) && c.GetEdifice(map) is Mineable m && m.def.building.isNaturalRock && m.def != ThingDefOf.CollapsedRocks;
        }

        // The roof cell c has by the rule, given the roof 'def' it would have otherwise.
        public static RoofDef For(IntVec3 c, Map map, RoofDef def)
        {
            if (Off)
            {
                return def;
            }
            IntVec3 up = FloorRoof.Above(c, map);
            if (IsRock(up, map))
            {
                return RoofDefOf.RoofRockThick;
            }
            if (def == null || !def.isThickRoof)
            {
                return def;
            }
            return up.IsValid && ProjectZ.HasFloor(up, map) ? RoofDefOf.RoofRockThin : null;
        }

        // Cell c made to follow the rule.
        public static void Apply(IntVec3 c, Map map)
        {
            RoofDef cur = map.roofGrid.RoofAt(c);
            RoofDef want = For(c, map, cur);
            if (cur != want)
            {
                map.roofGrid.SetRoof(c, want);
            }
        }

        // The rock at c came or went: the cell under it follows.
        public static void RockChanged(IntVec3 c, Map map)
        {
            IntVec3 below = FloorRoof.Below(c, map);
            if (below.IsValid)
            {
                Apply(below, map);
            }
        }
    }

    // Rock that appears on a mountain map after generation (a mod, a dev tool). (Generation writes every roof itself.)
    [HarmonyPatch(typeof(Building), nameof(Building.SpawnSetup))]
    public static class Building_SpawnRock_Patch
    {
        public static void Postfix(Building __instance, Map map, bool respawningAfterLoad)
        {
            if (!respawningAfterLoad && __instance is Mineable && !Mountains.Filling && Current.ProgramState == ProgramState.Playing && Mountains.On(map))
            {
                RockRoof.RockChanged(__instance.Position, map);
            }
        }
    }
}
