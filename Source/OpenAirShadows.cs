using HarmonyLib;
using Verse;

namespace ProjectZ
{
    // Sun and moon shadows on the ground while an upper floor is viewed. The game lays a "shadow mask" quad on every
    // cell whose terrain is not drawn (SectionLayer_Terrain: dontRender -> MatBases.ShadowMask), so no shadow falls on
    // undrawn ground. That mask is a screen-space stencil (no depth test, queue 3165, bit 4) and every sun shadow
    // (SunShadow 3170, SunShadowFade 3175: buildings, trees, models, pawns) is drawn only where that bit is clear.
    // Open air upstairs (PZ_OpenAir) is such terrain, and IsoRim draws an upper floor's blocks over the ground they
    // stand on: the open air of the viewed floor masked the whole ground below it, so every ground shadow went.
    // One rule, at the source: open air on an upper floor makes no shadow mask. (The ground keeps its own masks:
    // rooms, fog, its undrawn cells.) Upper floors draw no building sun shadows (IsoRim leaves those out up there);
    // pawn and model shadows on a floor are cast as anywhere else.
    [HarmonyPatch(typeof(SectionLayer_Terrain), nameof(SectionLayer_Terrain.Regenerate))]
    public static class TerrainShadowMask_OpenAir_Patch
    {
        // Test only (-projectznoshadowfix): the old mask, to show the bug.
        public static bool Disabled;

        private static readonly AccessTools.FieldRef<SectionLayer, Section> sectionRef = AccessTools.FieldRefAccess<SectionLayer, Section>("section");

        public static void Postfix(SectionLayer_Terrain __instance)
        {
            Section s = sectionRef(__instance);
            Map map = s?.map;
            if (Disabled || map == null || !ProjectZ.IsAnnex(s.botLeft, map))
            {
                return;
            }
            foreach (LayerSubMesh sm in __instance.subMeshes)
            {
                if (sm.material == MatBases.ShadowMask)
                {
                    sm.disabled = true;
                }
            }
        }
    }
}
