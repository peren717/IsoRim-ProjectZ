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
    // pollution rows come before it), with the floors warning under it. The right column (factions) is untouched.
    public static class WorldPage
    {
        // The value on the page. Reset() (first open and "Reset all") puts it back to the default.
        public static int PageFloors = 2;

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
            FloorsUI.DrawWarning(new Rect(200f, y + 34f, width, 200f), PageFloors);
        }
    }

    [HarmonyPatch(typeof(Page_CreateWorldParams), nameof(Page_CreateWorldParams.DoWindowContents))]
    public static class Page_CreateWorldParams_DoWindowContents_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
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
                Log.Warning("[ProjectZ] Create World page: layout not recognised, no Floors row (new worlds use the mod setting).");
                return code;
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
            return code;
        }
    }

    // First open and "Reset all": back to the default (the mod setting).
    [HarmonyPatch(typeof(Page_CreateWorldParams), nameof(Page_CreateWorldParams.Reset))]
    public static class Page_CreateWorldParams_Reset_Patch
    {
        public static void Postfix()
        {
            WorldPage.PageFloors = WorldFloors.Default;
            WorldFloors.ClearChosen();
        }
    }

    // "Generate": hand the page's value to the world about to be generated (WorldFloorsComponent.FinalizeInit).
    [HarmonyPatch(typeof(Page_CreateWorldParams), "CanDoNext")]
    public static class Page_CreateWorldParams_CanDoNext_Patch
    {
        public static void Prefix()
        {
            WorldFloors.SetChosen(WorldPage.PageFloors);
        }
    }
}
