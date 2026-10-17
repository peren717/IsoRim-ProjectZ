using HarmonyLib;
using RimWorld;
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

    // The stairs' icons (ProjectZ/Stairs_MenuIcon, ShipStairs_MenuIcon) are square pictures, but the game fits every thing
    // icon to the thing's sprite size (1 x 3 for stairs), which squeezes them. For every stairs def (StairDefs) they are
    // drawn square wherever the game draws a thing's icon, in the three places the proportions come from:
    //   - Widgets.ThingIconWorker (build menu, Widgets.ThingIcon / DefIcon: the designators' own drawing, info cards, lists);
    //   - Designator_Build's iconProportions (set from the sprite size in its constructor): Command.DrawIcon uses them for
    //     the copy gizmo (BuildCopyCommandUtility copies them onto its Command_Action) and dropdowns copy them too;
    //   - Widgets.GetIconFor(Thing): the proportions handed to commands and the cell inspector (carried things, selection).
    public static class StairIcons
    {
        public static bool Square(BuildableDef def) => global::ProjectZ.StairDefs.Is(def) && !def.uiIconPath.NullOrEmpty();
    }

    [HarmonyPatch(typeof(Widgets), "ThingIconWorker")]
    public static class ThingIcon_StairsSquare_Patch
    {
        public static bool Prefix(Rect rect, ThingDef thingDef, Texture resolvedIcon, float resolvedIconAngle, float scale, Material mat, float alpha)
        {
            if (!StairIcons.Square(thingDef))
            {
                return true;
            }
            Widgets.DrawTextureFitted(rect, resolvedIcon, scale, Vector2.one, new Rect(0f, 0f, 1f, 1f), resolvedIconAngle, mat, alpha);
            return false;
        }
    }

    [HarmonyPatch(typeof(Designator_Build), MethodType.Constructor, new[] { typeof(BuildableDef) })]
    public static class DesignatorBuild_StairsSquare_Patch
    {
        public static void Postfix(Designator_Build __instance, BuildableDef entDef)
        {
            if (StairIcons.Square(entDef))
            {
                __instance.iconProportions = Vector2.one;
            }
        }
    }

    [HarmonyPatch(typeof(Widgets), nameof(Widgets.GetIconFor), new[] { typeof(Thing), typeof(Vector2), typeof(Rot4?), typeof(bool), typeof(float), typeof(float), typeof(Vector2), typeof(Color), typeof(Material) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out, ArgumentType.Out, ArgumentType.Out, ArgumentType.Out })]
    public static class GetIconFor_StairsSquare_Patch
    {
        public static void Postfix(Thing thing, ref Vector2 iconProportions)
        {
            Thing t = thing?.GetInnerIfMinified();
            if (t != null && (StairIcons.Square(t.def) || (t is Blueprint || t is Frame) && StairIcons.Square(t.def.entityDefToBuild)))
            {
                iconProportions = Vector2.one;
            }
        }
    }
}
