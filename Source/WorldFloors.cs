using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // Floors per game (user decision 2026-10-16). The number of floors is picked once, on the Create World page
    // (Patches/WorldPagePatches.cs), and saved with the world. It never changes for that game: every map generated in
    // it gets these floors. The mod setting is only the page's starting value.
    public class WorldFloorsComponent : WorldComponent
    {
        // Total storeys including the ground. 0 = not decided: a save from before this component, decided by
        // WorldFloors.Migrate once its maps are loaded.
        public int floors;
        internal bool needsMigration;

        public WorldFloorsComponent(World world) : base(world)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref floors, "floors", 0);
        }

        public override void FinalizeInit(bool fromLoad)
        {
            base.FinalizeInit(fromLoad);
            if (floors > 0)
            {
                return;
            }
            if (fromLoad)
            {
                // Missing from the save (vanilla made a fresh component) or never set. The maps aren't loaded yet.
                needsMigration = true;
            }
            else
            {
                // A new world: WorldGenerator.GenerateWorld calls this while the Create World page is still open.
                floors = WorldFloors.TakeChosen();
                ProjectZ.DebugLog($"new world: {floors} floor(s)");
            }
        }
    }

    // THE decision of how many floors a new map gets. Readers: MapGenPatches (map generation) only, through
    // ProjectZ.FloorsForNewMaps. Nothing else reads ProjectZSettings.floors during play.
    public static class WorldFloors
    {
        // Set by the Create World page when it generates (Page_CreateWorldParams.CanDoNext), taken by the new world.
        private static int chosen;

        // The page's starting value: the mod setting (a test run's -projectzfloors wins).
        public static int Default
        {
            get
            {
                if (Dev.TestRunner.FloorsOverride > 0)
                {
                    return Dev.TestRunner.FloorsOverride;
                }
                return ProjectZMod.Settings == null ? 1 : Mathf.Clamp(ProjectZMod.Settings.floors, 1, ProjectZSettings.MaxFloors);
            }
        }

        internal static void SetChosen(int floors)
        {
            chosen = Mathf.Clamp(floors, 1, ProjectZSettings.MaxFloors);
        }

        internal static void ClearChosen()
        {
            chosen = 0;
        }

        // A world made without the page (dev quick test, mods that skip it) gets the default.
        internal static int TakeChosen()
        {
            int f = chosen > 0 ? chosen : Default;
            chosen = 0;
            return f;
        }

        public static WorldFloorsComponent Comp => Find.World?.GetComponent<WorldFloorsComponent>();

        // Floors of the current game.
        public static int ForThisGame
        {
            get
            {
                WorldFloorsComponent c = Comp;
                if (c == null)
                {
                    return Default; // no world (not during play)
                }
                if (c.floors <= 0)
                {
                    if (c.needsMigration)
                    {
                        Migrate(c);
                    }
                    else
                    {
                        // A world that skipped FinalizeInit(fromLoad: false): decide now, once.
                        c.floors = TakeChosen();
                        Log.Warning($"[ProjectZ] world had no floors set; using {c.floors}.");
                    }
                }
                return Mathf.Clamp(c.floors, 1, ProjectZSettings.MaxFloors);
            }
        }

        // Last migration note, for the test.
        internal static string lastMigration;

        // A save without the value: the most floors any of its maps already has; 1 when none has upper floors
        // (ProjectZ added to a game that didn't start with it: no upper floors there, user decision).
        internal static void Migrate(WorldFloorsComponent c)
        {
            c.needsMigration = false;
            int most = 1;
            if (Current.Game != null)
            {
                foreach (Map m in Find.Maps)
                {
                    AnnexMapComponent a = m.GetComponent<AnnexMapComponent>();
                    if (a != null && a.HasAnnex)
                    {
                        most = Mathf.Max(most, a.floors);
                    }
                }
            }
            c.floors = Mathf.Clamp(most, 1, ProjectZSettings.MaxFloors);
            lastMigration = most > 1
                ? $"[ProjectZ] save had no world floors: set to {c.floors}, the most floors of its maps."
                : "[ProjectZ] save had no world floors and no map with upper floors: set to 1, new maps get no upper floors.";
            Log.Message(lastMigration);
        }
    }

    // After a load, once every map is loaded: decide the value of a save that has none.
    [HarmonyPatch(typeof(Game), nameof(Game.FinalizeInit))]
    public static class Game_FinalizeInit_WorldFloors_Patch
    {
        public static void Postfix()
        {
            WorldFloorsComponent c = WorldFloors.Comp;
            if (c != null && c.needsMigration)
            {
                WorldFloors.Migrate(c);
            }
        }
    }
}
