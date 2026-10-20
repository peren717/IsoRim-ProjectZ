using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Patches
{
    // The Floors row on vanilla's Create World page (Page_CreateWorldParams). It is drawn as the last row of the
    // page's left column, 40 px under vanilla's last row (Advanced settings; Odyssey's landmark and Biotech's
    // pollution rows come before it), the floors warning right under its slider, then the Tall mountains row. The
    // right column (factions) is untouched.
    public static class WorldPage
    {
        // The value on the page. Reset() (first open and "Reset all") puts it back to the default.
        public static int PageFloors = 2;
        public static bool PageMountains;

        // Set by the transpiler when the row was inserted (the test checks it).
        public static bool RowInserted;

        // Called inside the left column's group, right before Widgets.EndGroup. lastRowY: vanilla's y of its last row.
        public static void DrawRow(Page_CreateWorldParams page, Rect rect, float lastRowY)
        {
            // As vanilla: column width (mainRect.width - Margin) / 2, labels 200 px wide, rows 40 px apart.
            float column = (rect.width - Window.StandardMargin) * 0.5f;
            float width = column - 200f;
            float y = lastRowY + 40f;
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, y, 200f, 30f), "PZ_WorldFloors".Translate());
            Rect slider = new Rect(200f, y, width, 30f);
            PageFloors = Mathf.RoundToInt(Widgets.HorizontalSlider(slider, PageFloors, 1f, ProjectZSettings.MaxFloors, middleAlignment: true, PageFloors.ToString(), null, null, 1f));
            TooltipHandler.TipRegion(new Rect(0f, y, slider.xMax, slider.height), "PZ_WorldFloorsTip".Translate());
            // The floors warning right under its slider; the Tall mountains row moves down by its height.
            float warning = FloorsUI.DrawWarning(new Rect(200f, y + 34f, width, 200f), PageFloors);
            // Tall mountains: a checkbox (greyed with one floor, nothing to rise into)
            float y2 = y + 40f + (warning > 0f ? warning + 4f : 0f);
            Widgets.Label(new Rect(0f, y2, 200f, 30f), "PZ_TallMountains".Translate());
            Widgets.Checkbox(new Vector2(200f, y2 + 3f), ref PageMountains, 24f, disabled: PageFloors <= 1);
            TooltipHandler.TipRegion(new Rect(0f, y2, 230f, 30f), "PZ_TallMountainsTip".Translate());
        }
    }

    [HarmonyPatch(typeof(Page_CreateWorldParams), nameof(Page_CreateWorldParams.DoWindowContents))]
    public static class Page_CreateWorldParams_DoWindowContents_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            return FailSoft.Transpile(nameof(Page_CreateWorldParams_DoWindowContents_Patch), original, instructions, Edit);
        }

        // (no row: new worlds use the mod setting)
        private static string Edit(List<CodeInstruction> code)
        {
            MethodInfo endGroup = AccessTools.Method(typeof(Widgets), nameof(Widgets.EndGroup));
            // vanilla's row y: the local of the first "y += 40f"
            CodeInstruction loadY = null;
            for (int i = 0; i + 3 < code.Count && loadY == null; i++)
            {
                if (code[i].IsLdloc() && code[i + 1].opcode == OpCodes.Ldc_R4 && code[i + 1].operand is float f && f == 40f
                    && code[i + 2].opcode == OpCodes.Add && code[i + 3].IsStloc() && code[i].LocalIndex() == code[i + 3].LocalIndex())
                {
                    loadY = new CodeInstruction(code[i].opcode, code[i].operand);
                }
            }
            int at = code.FindIndex(ci => ci.Calls(endGroup));
            if (loadY == null || at < 0)
            {
                return "layout not recognised";
            }
            CodeInstruction first = new CodeInstruction(OpCodes.Ldarg_0);
            code[at].MoveLabelsTo(first); // the tutorial-mode branch jumps to EndGroup: the row is drawn there too
            code.InsertRange(at, new[]
            {
                first,
                new CodeInstruction(OpCodes.Ldarg_1),
                loadY,
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(WorldPage), nameof(WorldPage.DrawRow))),
            });
            WorldPage.RowInserted = true;
            return null;
        }
    }

    // First open and "Reset all": back to the default (the mod setting).
    [HarmonyPatch(typeof(Page_CreateWorldParams), nameof(Page_CreateWorldParams.Reset))]
    public static class Page_CreateWorldParams_Reset_Patch
    {
        public static void Postfix()
        {
            WorldPage.PageFloors = WorldFloors.Default;
            WorldPage.PageMountains = WorldFloors.DefaultMountains;
            WorldFloors.ClearChosen();
        }
    }

    // "Generate": hand the page's value to the world about to be generated (WorldFloorsComponent.FinalizeInit).
    [HarmonyPatch(typeof(Page_CreateWorldParams), "CanDoNext")]
    public static class Page_CreateWorldParams_CanDoNext_Patch
    {
        public static void Prefix()
        {
            WorldFloors.SetChosen(WorldPage.PageFloors, WorldPage.PageMountains);
        }
    }
}
