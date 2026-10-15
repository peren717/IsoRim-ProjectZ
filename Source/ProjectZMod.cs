using HarmonyLib;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    public class ProjectZSettings : ModSettings
    {
        public const int MaxFloors = 5;

        // Total storeys including the ground (user decision D1: default 2, at most 5). Read when a map is generated.
        public int floors = 2;
        // Dev/debug: extra logging, the annex spawn watch and the dev-mode camera bypass.
        public bool debug = false;
        // Decision D7: shooting down at a lower floor adds this to the shooter's accuracy factor (hit report "shooter").
        public float heightAccuracy = 0.2f;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref floors, "floors", 2);
            Scribe_Values.Look(ref debug, "debug", false);
            Scribe_Values.Look(ref heightAccuracy, "heightAccuracy", 0.2f);
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

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard list = new Listing_Standard();
            list.Begin(inRect);
            list.Label("PZ_Floors".Translate(Settings.floors), tooltip: "PZ_FloorsTip".Translate());
            Settings.floors = Mathf.RoundToInt(list.Slider(Settings.floors, 1f, ProjectZSettings.MaxFloors));
            list.Gap();
            list.Label("PZ_HeightAccuracy".Translate(Settings.heightAccuracy.ToStringPercent()), tooltip: "PZ_HeightAccuracyTip".Translate());
            Settings.heightAccuracy = UnityEngine.Mathf.Round(list.Slider(Settings.heightAccuracy, 0f, 0.5f) * 20f) / 20f;
            list.Gap();
            list.CheckboxLabeled("PZ_Debug".Translate(), ref Settings.debug, "PZ_DebugTip".Translate());
            list.End();
        }
    }
}
