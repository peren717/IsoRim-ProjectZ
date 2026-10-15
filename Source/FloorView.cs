using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using RimWorld.Planet;

namespace ProjectZ
{
    [DefOf]
    public static class PZKeyDefOf
    {
        public static KeyBindingDef PZ_FloorUp;
        public static KeyBindingDef PZ_FloorDown;

        static PZKeyDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(PZKeyDefOf));
        }
    }

    // The floor the player works on: the pointer picks its cells (build, zone, order, select there) and the floors
    // above it are hidden. 0 = the ground. Not saved (starts on the ground).
    public static class FloorView
    {
        private static int current;

        public static int Current
        {
            get
            {
                AnnexMapComponent a = ProjectZ.Annex(Find.CurrentMap);
                return a == null ? 0 : Mathf.Clamp(current, 0, a.floors - 1);
            }
        }

        public static void Set(int floor)
        {
            AnnexMapComponent a = ProjectZ.Annex(Find.CurrentMap);
            if (a == null)
            {
                current = 0;
                return;
            }
            int k = Mathf.Clamp(floor, 0, a.floors - 1);
            if (k != Current)
            {
                current = k;
                SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
            }
            current = k;
        }

        public static string Label(int floor) => "PZ_FloorLabel".Translate(floor + 1);
    }

    // Keys (= and - by default) and a button in the bottom-right row.
    [HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
    public static class PlaySettings_FloorButton_Patch
    {
        public static void Postfix(WidgetRow row, bool worldView)
        {
            if (worldView || row == null || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            AnnexMapComponent a = ProjectZ.Annex(Find.CurrentMap);
            if (a == null)
            {
                return;
            }
            int k = FloorView.Current;
            string tip = "PZ_FloorButtonTip".Translate(FloorView.Label(k), PZKeyDefOf.PZ_FloorUp.MainKeyLabel, PZKeyDefOf.PZ_FloorDown.MainKeyLabel);
            if (row.ButtonText(FloorView.Label(k), tip))
            {
                FloorView.Set(k + 1 >= a.floors ? 0 : k + 1);
            }
        }
    }

    [HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
    public static class FloorKeys_Patch
    {
        public static void Postfix()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || WorldRendererUtility.WorldSelected || Event.current.type != EventType.KeyDown)
            {
                return;
            }
            if (!ProjectZ.HasAnnex(Find.CurrentMap))
            {
                return;
            }
            if (PZKeyDefOf.PZ_FloorUp != null && PZKeyDefOf.PZ_FloorUp.KeyDownEvent)
            {
                FloorView.Set(FloorView.Current + 1);
                Event.current.Use();
            }
            else if (PZKeyDefOf.PZ_FloorDown != null && PZKeyDefOf.PZ_FloorDown.KeyDownEvent)
            {
                FloorView.Set(FloorView.Current - 1);
                Event.current.Use();
            }
        }
    }
}
