using HarmonyLib;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    public class ProjectZSettings : ModSettings
    {
        public const int MaxFloors = 13;

        // Total storeys including the ground (user decision D1: default 2, at most 5). Read when a map is generated.
        public int floors = 2;
        // Dev/debug: extra logging, the annex spawn watch and the dev-mode camera bypass.
        public bool debug = false;
        // Decision D7: shooting down at a lower floor adds this to the shooter's accuracy factor (hit report "shooter").
        public float heightAccuracy = 0.2f;
        // Floor HUD (FloorHud.cs): hidden, and where it was moved with Alt + drag (distances of its right and bottom edges
        // from the screen's, in UI px); not moved = its own slot in the bottom-right controls.
        public bool hideFloorHud = false;
        public bool floorHudMoved = false;
        public float floorHudRight;
        public float floorHudBottom;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref floors, "floors", 2);
            Scribe_Values.Look(ref debug, "debug", false);
            Scribe_Values.Look(ref heightAccuracy, "heightAccuracy", 0.2f);
            Scribe_Values.Look(ref hideFloorHud, "hideFloorHud", false);
            Scribe_Values.Look(ref floorHudMoved, "floorHudMoved", false);
            Scribe_Values.Look(ref floorHudRight, "floorHudRight", 0f);
            Scribe_Values.Look(ref floorHudBottom, "floorHudBottom", 0f);
            floors = Mathf.Clamp(floors, 1, MaxFloors);
        }
    }

    public class ProjectZMod : Mod
    {
        public static ProjectZSettings Settings;

        public ProjectZMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<ProjectZSettings>();
            // Never the argument-less PatchAll(): on 1.7 CoreCLR it can patch nothing.
            new Harmony("Peren.IsoRimProjectZ").PatchAll(typeof(ProjectZMod).Assembly);
            IsoRimBridge.Init();
            Dev.TestRunner.Init();
            Log.Message("[ProjectZ] loaded, floors=" + Settings.floors + " (effective " + ProjectZ.FloorsForNewMaps + ")");
        }

        public override string SettingsCategory() => "IsoRim ProjectZ";

        // Above two storeys a warning under the slider, yellow then red: every storey is another whole map of cells, and
        // more than 5 are untested.
        private static void FloorsWarning(Listing_Standard list, int floors)
        {
            string key;
            Color color;
            if (floors >= 13)
            {
                key = "PZ_FloorsWarn13";
                color = ColorLibrary.RedReadable;
            }
            else if (floors >= 10)
            {
                key = "PZ_FloorsWarn10";
                color = ColorLibrary.RedReadable;
            }
            else if (floors >= 6)
            {
                key = "PZ_FloorsWarn6";
                color = new Color(1f, 0.75f, 0.2f);
            }
            else if (floors >= 3)
            {
                key = "PZ_FloorsWarn3";
                color = new Color(1f, 0.92f, 0.45f);
            }
            else
            {
                return;
            }
            GUI.color = color;
            list.Label(key.Translate());
            GUI.color = Color.white;
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard list = new Listing_Standard();
            list.Begin(inRect);
            list.Label("PZ_Floors".Translate(Settings.floors), tooltip: "PZ_FloorsTip".Translate());
            Settings.floors = Mathf.RoundToInt(list.Slider(Settings.floors, 1f, ProjectZSettings.MaxFloors));
            FloorsWarning(list, Settings.floors);
            list.Gap();
            list.Label("PZ_HeightAccuracy".Translate(Settings.heightAccuracy.ToStringPercent()), tooltip: "PZ_HeightAccuracyTip".Translate());
            Settings.heightAccuracy = UnityEngine.Mathf.Round(list.Slider(Settings.heightAccuracy, 0f, 0.5f) * 20f) / 20f;
            list.Gap();
            list.CheckboxLabeled("PZ_HideFloorHud".Translate(), ref Settings.hideFloorHud);
            if (list.ButtonText("PZ_ResetFloorHud".Translate(), widthPct: 0.4f))
            {
                Settings.floorHudMoved = false;
            }
            list.Gap();
            list.CheckboxLabeled("PZ_Debug".Translate(), ref Settings.debug, "PZ_DebugTip".Translate());
            list.End();
        }
    }
}
