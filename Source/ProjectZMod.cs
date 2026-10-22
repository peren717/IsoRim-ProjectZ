using HarmonyLib;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    public class ProjectZSettings : ModSettings
    {
        public const int MaxFloors = 13;

        // Total storeys including the ground (user decision D1: default 2). Only the starting value of the Create World
        // page's Floors row (WorldFloors.Default): each game keeps the floors its world was created with.
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
        // The one-time floor scroll hint was shown (the first click on a floor HUD bar, per install).
        public bool floorScrollHintShown = false;

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
            Scribe_Values.Look(ref floorScrollHintShown, "floorScrollHintShown", false);
            floors = Mathf.Clamp(floors, 1, MaxFloors);
        }
    }

    public class ProjectZMod : Mod
    {
        public static ProjectZSettings Settings;

        public ProjectZMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<ProjectZSettings>();
            // Fail-soft (FailSoft.cs): class by class and entry by entry, a patch that fails turns off only its feature.
            // Our own assembly explicitly: the argument-less PatchAll() can patch nothing on 1.7 CoreCLR.
            Harmony harmony = new Harmony("Peren.IsoRimProjectZ");
            FailSoft.PatchAll(harmony, typeof(ProjectZMod).Assembly);
            // the game's "nearest" choices measure the 3D distance between floors (an explicit list of vanilla methods)
            Patches.Dist3D_Patch.Patch();
            // radius effects stay on their centre's floor (an explicit list of vanilla methods)
            BandRadial.Patch(harmony);
            // directional effects (flame spews, sprays, beams) aimed at another floor: laid out on the target's floor
            Combat.AimFloor.Patch(harmony);
            IsoRimBridge.Init();
            Dev.TestRunner.Init();
            Log.Message("[ProjectZ] loaded, default floors for new worlds=" + Settings.floors + " (effective " + WorldFloors.Default + ")");
        }

        public override string SettingsCategory() => "IsoRim ProjectZ";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard list = new Listing_Standard();
            list.Begin(inRect);
            list.Label("PZ_Floors".Translate(Settings.floors), tooltip: "PZ_FloorsTip".Translate());
            Settings.floors = Mathf.RoundToInt(list.Slider(Settings.floors, 1f, ProjectZSettings.MaxFloors));
            FloorsUI.DrawWarning(list, Settings.floors);
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
