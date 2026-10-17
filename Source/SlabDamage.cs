using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ
{
    // Heavy hits on slabs (user decisions 2026-10-16, isorim-orbital-strike-3d.md "REVISED slab damage model"). A slab has
    // two states, no hit points:
    //   - a heavy hit is an explosion doing at least the slab's destroyOnBombDamageThreshold (35, the vanilla bridge value)
    //     at the cell; frag grenades, mortar shells and bombardment shells do 50 with no falloff, so their whole blast radius
    //     is one. Through the game's own terrain damage
    //     (DamageWorker.ExplosionDamageTerrain: Bomb damage, nothing wall-like standing on the cell), or a sky strike's hit
    //     (SkyStrikes: the power beam);
    //   - a wood slab breaks; a stone or metal slab becomes its damaged slab (SlabDefs), and a floor laid on it (carpet,
    //     tiles) is destroyed with no refund; a damaged slab breaks;
    //   - breaking is the slab collapse (SlabCollapse.Collapse: built things on it destroyed, pawns and items fall), and the
    //     roof under it goes too (it was the slab), so the floor below is open to whatever is above;
    //   - nothing is laid on a damaged slab (its def is not changeable: the game's own refusal; PlaceMessages says why) and
    //     it is never repaired: it is taken up with the remove tool (Slab.At counts it) and laid again;
    //   - the landing of stairs is part of the stairs: hits leave it alone.
    public static class SlabDamage
    {
        public static int Damaged;   // counters (tests)
        public static int Broken;

        // The slab at an upper-floor cell (also under a floor laid on it), else null.
        public static TerrainDef SlabAt(IntVec3 c, Map map)
        {
            if (!Slab.At(c, map))
            {
                return null;
            }
            TerrainDef top = map.terrainGrid.TopTerrainAt(c);
            return SlabDefs.Is(top) ? top : map.terrainGrid.UnderTerrainAt(c);
        }

        // Damage at the cell enough for a heavy hit on this slab.
        public static bool Heavy(TerrainDef slab, float damage) => slab != null && slab.destroyOnBombDamageThreshold >= 0f && damage >= slab.destroyOnBombDamageThreshold;

        // One heavy hit on the slab at c (nothing without a slab there).
        public static void HeavyHit(IntVec3 c, Map map)
        {
            TerrainDef slab = SlabAt(c, map);
            if (slab == null || StairwellFloor.IsStairsExit(c, map))
            {
                return;
            }
            TerrainDef hurt = SlabDefs.DamagedOf(slab);
            if (hurt != null)
            {
                // (not layerable: the floor laid on it goes, and no leavings)
                TerrainDef top = map.terrainGrid.TopTerrainAt(c);
                map.terrainGrid.SetTerrain(c, hurt);
                FleckMaker.ThrowDustPuff(c.ToVector3Shifted(), map, 1.2f);
                Damaged++;
                ProjectZ.DebugLog($"slab at {c} damaged: {top.defName} -> {hurt.defName}");
                return;
            }
            Break(c, map);
        }

        // The slab at u is gone: the collapse path, then the roof under it.
        public static void Break(IntVec3 u, Map map)
        {
            IntVec3 below = FloorRoof.Below(u, map);
            if (!below.IsValid)
            {
                return;
            }
            TerrainDef was = SlabAt(u, map);
            SlabCollapse.Collapse(below, map, null);
            RoofDef roof = map.roofGrid.RoofAt(below);
            if (roof != null && !roof.isThickRoof && !FloorRoof.FloorAbove(below, map))
            {
                map.roofGrid.SetRoof(below, null);
            }
            Broken++;
            ProjectZ.DebugLog($"slab at {u} broken ({was?.defName}); roof under it {roof?.defName ?? "none"} -> {map.roofGrid.RoofAt(below)?.defName ?? "none"}");
        }
    }

    // The game's terrain damage from explosions, for slabs (vanilla only knows foundations and top layers it can remove).
    [HarmonyPatch(typeof(DamageWorker), "ExplosionDamageTerrain")]
    public static class ExplosionDamageTerrain_Slab_Patch
    {
        public static bool Prefix(DamageWorker __instance, Explosion explosion, IntVec3 c)
        {
            Map map = explosion?.Map;
            if (__instance.def != DamageDefOf.Bomb || map == null || ProjectZ.Annex(map) == null)
            {
                return true;
            }
            TerrainDef slab = SlabDamage.SlabAt(c, map);
            if (slab == null)
            {
                return true;
            }
            if (SlabDamage.Heavy(slab, explosion.GetDamageAmountAt(c)))
            {
                SlabDamage.HeavyHit(c, map);
            }
            return false;
        }
    }
}
