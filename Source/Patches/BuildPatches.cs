using HarmonyLib;
using UnityEngine;
using Verse;

namespace ProjectZ.Patches
{
    // "Close to the edge" (no-build and no-zone margins) is measured on the ground: an upper floor cell is as close to
    // the edge as the ground cell below it, and the ground's top row is the north edge. Cells between floors are
    // always "close to the edge".
    [HarmonyPatch(typeof(GenGrid), nameof(GenGrid.CloseToEdge))]
    public static class CloseToEdge_Patch
    {
        public static bool Prefix(IntVec3 c, Map map, int edgeDist, ref bool __result)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return true;
            }
            IntVec3 g = ProjectZ.GroundCellOf(c, map);
            if (!g.IsValid)
            {
                __result = true;
                return false;
            }
            __result = g.x < edgeDist || g.z < edgeDist || g.x >= map.Size.x - edgeDist || g.z >= a.groundZ - edgeDist;
            return false;
        }
    }

    // The stairs' icon (ProjectZ/Stairs_MenuIcon, build menu and anywhere else the game draws a thing's icon) is a square
    // picture, but the game fits every thing icon to the thing's sprite size (1 x 3 for the stairs), which squeezes it.
    // For the stairs it is drawn square.
    [HarmonyPatch(typeof(Widgets), "ThingIconWorker")]
    public static class ThingIcon_StairsSquare_Patch
    {
        public static bool Prefix(Rect rect, ThingDef thingDef, Texture resolvedIcon, float resolvedIconAngle, float scale, Material mat, float alpha)
        {
            if (thingDef != PZDefOf.PZ_Stairs || thingDef.uiIconPath.NullOrEmpty())
            {
                return true;
            }
            Widgets.DrawTextureFitted(rect, resolvedIcon, scale, Vector2.one, new Rect(0f, 0f, 1f, 1f), resolvedIconAngle, mat, alpha);
            return false;
        }
    }
}
