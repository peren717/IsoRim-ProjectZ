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
        // Not used and hidden in the settings while the HeightAccuracy combat part is off (Api 6); kept for compatibility.
        public float heightAccuracy = 0.2f;
        // Floor HUD (FloorHud.cs): hidden, and where it was moved with Alt + drag (distances of its right and bottom edges
        // from the screen's, in UI px); not moved = its own slot in the bottom-right controls.
        public bool hideFloorHud = false;
        public bool floorHudMoved = false;
        public float floorHudRight;
        public float floorHudBottom;
        // The one-time floor scroll hint was shown (the first click on a floor HUD bar, per install).
        public bool floorScrollHintShown = false;

        // Storey height (user 2026-11-16): the wall height ProjectZ locks IsoRim's view to (IsoRimBridge.LockedWall), one
        // of WallHeights, global and changeable at any time (IsoRimBridge.ApplyWallHeight). The default is the wall height
        // ProjectZ was built at, so nothing changes unless the player picks another.
        public const float DefaultWallHeight = 1.65f;
        public static readonly float[] WallHeights = { DefaultWallHeight, 2.0f, 2.4f };
        public float wallHeight = DefaultWallHeight;

        // The choice nearest to h (a hand-edited or outdated saved value lands on a listed one).
        public static float NearestWallHeight(float h)
        {
            float best = DefaultWallHeight;
            for (int i = 0; i < WallHeights.Length; i++)
            {
                if (Mathf.Abs(WallHeights[i] - h) < Mathf.Abs(best - h))
                {
                    best = WallHeights[i];
                }
            }
            return best;
        }

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
            Scribe_Values.Look(ref wallHeight, "storeyWallHeight", DefaultWallHeight);
            floors = Mathf.Clamp(floors, 1, MaxFloors);
            wallHeight = NearestWallHeight(wallHeight);
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
            Combat.CombatGate.OnSettle(() => Combat.AimFloor.Patch(harmony));
            // the game's own sight rule for shots between floors, run in the 3D scope (built now; logged once if it fails)
            Combat.Verb_CanHitCell_Patch.GameReady();
            IsoRimBridge.Init();
            CopyWatches.Register();
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
            list.Label("PZ_StoreyHeight".Translate(), tooltip: "PZ_StoreyHeightTip".Translate());
            for (int i = 0; i < ProjectZSettings.WallHeights.Length; i++)
            {
                float h = ProjectZSettings.WallHeights[i];
                if (list.RadioButton(h.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture), Settings.wallHeight == h, 12f, "PZ_StoreyHeightTip".Translate()) && Settings.wallHeight != h)
                {
                    Settings.wallHeight = h;
                    IsoRimBridge.ApplyWallHeight();
                }
            }
            list.Gap();
            // hidden (field and key kept) while the HeightAccuracy combat part is off (Api 6: Combat Extended, or released)
            if (Combat.CombatGate.On(CombatPart.HeightAccuracy))
            {
                list.Label("PZ_HeightAccuracy".Translate(Settings.heightAccuracy.ToStringPercent()), tooltip: "PZ_HeightAccuracyTip".Translate());
                Settings.heightAccuracy = UnityEngine.Mathf.Round(list.Slider(Settings.heightAccuracy, 0f, 0.5f) * 20f) / 20f;
                list.Gap();
            }
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
