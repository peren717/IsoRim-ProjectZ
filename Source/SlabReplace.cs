using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ
{
    // Replacing a slab (user 2026-11-09): a slab blueprint laid over a slab of another stuff swaps it in one step when the
    // frame is done. No toggle; ship decks and natural tops are not slabs, so never replaced this way.
    //   - Where: an upper-floor cell with a slab (bare, or under a floor laid on it) of a different def. A damaged slab
    //     takes any slab, its own stuff too (the repair). The same def is refused (the game's "terrain is already").
    //   - The slab is already there, so the open-air rules (PZ_FloorSupport, PlaceWorker_UpperFloor's cover and support)
    //     do not apply; building still waits for the game's support under it (CanConstruct_SlabSupport_Patch).
    //   - No refund: the old slab just goes, as on every removal path (Slab.Remove's rule); the removal code is not used.
    //   - The cell is never open air: no remove-floor-first job (vanilla would take up a floor laid on the slab first),
    //     and the frame swaps the def in place. A bare slab is set like any terrain (slab to slab: no roof, gravity or
    //     support change). Under a laid floor only the under layer is written: the floor on top stays.
    public static class SlabReplace
    {
        // Laying a slab through a finished frame or a god-mode placement (SetTerrain then keeps a floor laid on top).
        private static int laying;

        public static int Replaced;   // counters (tests)
        public static int UnderSwaps;

        // A slab of 'def' may replace the slab at c (any other slab def there; the same one: no).
        public static bool Over(BuildableDef def, IntVec3 c, Map map)
        {
            if (!(def is TerrainDef t) || !SlabDefs.Is(t) || SlabDefs.IsDamaged(t) || map == null || !c.IsValid || !c.InBounds(map)
                || ProjectZ.Annex(map) == null || !Slab.At(c, map) || map.terrainGrid.FoundationAt(c) != null)
            {
                return false;
            }
            TerrainDef now = SlabDamage.SlabAt(c, map);
            return now != null && now != t;
        }

        // A slab blueprint or frame standing on a slab (the swap to come).
        public static bool Pending(Thing t) => t != null && t.Spawned && SlabDefs.Is(t.def.entityDefToBuild) && Slab.At(t.Position, t.Map);

        public static void Enter(BuildableDef def, IntVec3 c, Map map)
        {
            laying++;
            if (Over(def, c, map))
            {
                Replaced++;
                ProjectZ.DebugLog($"slab at {c} replaced: {SlabDamage.SlabAt(c, map)?.defName} -> {def.defName} (no refund)");
            }
        }

        public static void Exit() => laying--;

        // SetTerrain with a slab on a cell whose slab is under a laid floor: only the under layer changes.
        public static bool SwapUnder(TerrainGrid tg, Map map, IntVec3 c, TerrainDef newTerr)
        {
            if (laying <= 0 || !SlabDefs.Is(newTerr) || map == null || !Slab.At(c, map))
            {
                return false;
            }
            TerrainDef top = tg.TopTerrainAt(c);
            if (SlabDefs.Is(top) || !SlabDefs.Is(tg.UnderTerrainAt(c)))
            {
                return false;
            }
            // (the game's own under-layer write: glow and the map's terrain event; path cost, water and fertility follow
            // the top layer, which stays; the roof below and the support stay, both slabs)
            tg.SetUnderTerrain(c, newTerr);
            map.mapDrawer.MapMeshDirty(c, MapMeshFlagDefOf.Terrain, regenAdjacentCells: true, regenAdjacentSections: false);
            UnderSwaps++;
            return true;
        }
    }

    // The slab is there: only its own def is refused (by the game: "terrain is already", or PlaceMessages for a slab
    // under a laid floor); the open-air affordance and the changeable flag of a damaged slab do not count.
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanBuildOnTerrain))]
    public static class CanBuildOnTerrain_SlabReplace_Patch
    {
        public static void Postfix(BuildableDef entDef, IntVec3 c, Map map, ref bool __result)
        {
            if (!__result && SlabReplace.Over(entDef, c, map))
            {
                __result = true;
            }
        }
    }

    // Never take the floor up first for a slab over a slab (vanilla: any terrain blueprint where the top layer can be
    // removed, i.e. a floor laid on the slab): the floor stays and the slab under it is swapped.
    [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResources), "ShouldRemoveExistingFloorFirst")]
    public static class ShouldRemoveExistingFloorFirst_SlabReplace_Patch
    {
        public static void Postfix(Blueprint blue, ref bool __result)
        {
            if (__result && SlabReplace.Pending(blue))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
    public static class CompleteConstruction_SlabReplace_Patch
    {
        public static void Prefix(Frame __instance, out bool __state)
        {
            __state = __instance.Spawned && SlabDefs.Is(__instance.def.entityDefToBuild);
            if (__state)
            {
                SlabReplace.Enter(__instance.def.entityDefToBuild, __instance.Position, __instance.Map);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                SlabReplace.Exit();
            }
        }
    }

    // God mode lays the slab at once (the same swap).
    [HarmonyPatch(typeof(Designator_Build), nameof(Designator_Build.DesignateSingleCell))]
    public static class DesignateSingleCell_SlabReplace_Patch
    {
        public static void Prefix(Designator_Build __instance, IntVec3 c, out bool __state)
        {
            // (only when it lays the terrain itself; otherwise it places a blueprint)
            __state = SlabDefs.Is(__instance.PlacingDef) && (DebugSettings.godMode || __instance.PlacingDef.GetStatValueAbstract(StatDefOf.WorkToBuild, __instance.StuffDef) == 0f);
            if (__state)
            {
                SlabReplace.Enter(__instance.PlacingDef, c, __instance.Map);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                SlabReplace.Exit();
            }
        }
    }

    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain))]
    public static class SetTerrain_SlabReplace_Patch
    {
        private static readonly AccessTools.FieldRef<TerrainGrid, Map> mapRef = AccessTools.FieldRefAccess<TerrainGrid, Map>("map");

        public static bool Prefix(TerrainGrid __instance, IntVec3 c, TerrainDef newTerr)
        {
            return !SlabReplace.SwapUnder(__instance, mapRef(__instance), c, newTerr);
        }
    }
}
