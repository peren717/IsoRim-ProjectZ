using HarmonyLib;
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
}
