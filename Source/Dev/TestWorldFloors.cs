using System.IO;
using System.Linq;
using ProjectZ.Patches;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ProjectZ.Dev
{
    // Mode worldfloors (-projectzmode=worldfloors; run mode plain first for the plain-save case): floors per game.
    //  1. New world: before the quick test generates its world, the Create World page's own steps run (Reset with our
    //     postfix, the slider's value set to 4, the Generate prefix); vanilla then generates the world and the start map.
    //     (The real page can't generate inside the quick test's long event, so its Generate button isn't pressed.)
    //  2. The mod setting is changed to 7 mid-game: the game keeps 4. The real page is opened in game and drawn (shots
    //     of its Floors row at 13 and 3, "Reset all" back to the setting), then closed.
    //  3. A colonist leaves as a caravan and settles a new colony (vanilla Settle): the second map gets 4 floors.
    //  4. Save and reload: 4 kept, no migration.
    //  5. Old ProjectZ save: the world component removed, saved, reloaded: migrated to 4 (the most floors of its maps).
    //  6. Plain save (pz_plain from mode plain: no annex, no world component): migrated to 1; a colony settled there
    //     with the setting at 7 gets no upper floors.
    public static partial class TestRunner
    {
        private const int WfChosen = 4;
        private const int WfSettingAtStart = 2;
        private const int WfSettingMidGame = 7;
        private static int wfPageDefault = -1;
        private static Page_CreateWorldParams wfPage;
        private static int wfMapsBefore;

        // Root_Play.SetupForQuickTestPlay prefix (mode worldfloors): what the Create World page does before vanilla
        // generates the world.
        internal static void WorldFloorsSetupPrefix()
        {
            ProjectZMod.Settings.floors = WfSettingAtStart; // in memory only, never written
            Page_CreateWorldParams page = new Page_CreateWorldParams();
            page.Reset();                                   // vanilla Reset + our postfix: the page shows the default
            wfPageDefault = WorldPage.PageFloors;
            WorldPage.PageFloors = WfChosen;                // what the slider writes
            Page_CreateWorldParams_CanDoNext_Patch.Prefix(); // what "Generate" does before vanilla's world generation
            Out($"worldfloors: page default {wfPageDefault}, chosen {WfChosen}");
        }

        private static string WfMaps() => string.Join(", ", Find.Maps.Select(m => $"{m} {m.Size} floors {m.GetComponent<AnnexMapComponent>()?.floors} v{m.GetComponent<AnnexMapComponent>()?.layoutVersion}"));

        private static bool WfSettle(Map map, string next)
        {
            Pawn pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            PlanetTile home = map.Tile;
            PlanetTile target = PlanetTile.Invalid;
            float best = float.MaxValue;
            foreach (SurfaceTile t in Find.WorldGrid.Tiles)
            {
                if (!TileFinder.IsValidTileForNewSettlement(t.tile))
                {
                    continue;
                }
                float d = Find.WorldGrid.ApproxDistanceInTiles(home, t.tile);
                if (d >= 3f && d < best)
                {
                    best = d;
                    target = t.tile;
                }
            }
            if (pawn == null || !target.Valid)
            {
                Result("settle setup", false, $"colonist {pawn}, tile {target}");
                Finish();
                return false;
            }
            wfMapsBefore = Find.Maps.Count;
            Caravan caravan = CaravanExitMapUtility.ExitMapAndCreateCaravan(new[] { pawn }, Faction.OfPlayer, home, target, target, sendMessage: false);
            caravan.Tile = target;
            caravan.pather.StopDead();
            Info($"settling with {pawn} at tile {target} ({best:0.#} tiles away), setting now {ProjectZMod.Settings.floors}");
            SettleInEmptyTileUtility.Settle(caravan);
            Wait(60, next);
            return true;
        }

        private static void StepWorldFloors(Map map)
        {
            WorldFloorsComponent wc = WorldFloors.Comp;
            switch (stage)
            {
                case "f_start":
                {
                    AnnexMapComponent a = map.GetComponent<AnnexMapComponent>();
                    Result("page opens at the mod setting", wfPageDefault == WfSettingAtStart, $"page default {wfPageDefault}, setting {WfSettingAtStart}");
                    Result("new world keeps the page's value", wc != null && wc.floors == WfChosen && !wc.needsMigration, $"world floors {wc?.floors}");
                    Result("start map has the world's floors", a != null && a.HasAnnex && a.floors == WfChosen && a.layoutVersion == AnnexMapComponent.CurrentLayoutVersion, WfMaps());
                    GenCheck(map, "start map");
                    ProjectZMod.Settings.floors = WfSettingMidGame;
                    Result("mod setting changed mid-game is ignored", ProjectZ.FloorsForNewMaps == WfChosen, $"setting {ProjectZMod.Settings.floors}, floors for new maps {ProjectZ.FloorsForNewMaps}");
                    StartTicks(600, "f_page");
                    break;
                }
                case "f_page":
                    Result("Floors row inserted into the Create World page", WorldPage.RowInserted, "");
                    wfPage = new Page_CreateWorldParams();
                    Find.WindowStack.Add(wfPage); // PreOpen -> Reset: the page shows the current setting
                    Result("page opens at the current setting", WorldPage.PageFloors == WfSettingMidGame, $"page {WorldPage.PageFloors}");
                    WorldPage.PageFloors = 13;
                    Wait(30, "f_page13");
                    break;
                case "f_page13":
                    Capture("wf_page_13");
                    stage = "f_page_reset";
                    break;
                case "f_page_reset":
                    wfPage.Reset(); // "Reset all"
                    Result("Reset all restores the default", WorldPage.PageFloors == WfSettingMidGame, $"page {WorldPage.PageFloors}");
                    WorldPage.PageFloors = 3;
                    Wait(30, "f_page3");
                    break;
                case "f_page3":
                    Capture("wf_page_3");
                    stage = "f_page_close";
                    break;
                case "f_page_close":
                    wfPage.Close(doCloseSound: false);
                    wfPage = null;
                    Result("opening the page doesn't change the game", wc.floors == WfChosen && ProjectZ.FloorsForNewMaps == WfChosen, $"world floors {wc.floors}");
                    WfSettle(map, "f_settled");
                    break;
                case "f_settled":
                {
                    Map m2 = Find.Maps.Count > wfMapsBefore ? Find.Maps.Last() : null;
                    AnnexMapComponent a2 = m2?.GetComponent<AnnexMapComponent>();
                    Result("second map (settled) has the world's floors, not the setting", a2 != null && a2.HasAnnex && a2.floors == WfChosen, $"setting {ProjectZMod.Settings.floors}; {WfMaps()}");
                    if (m2 != null)
                    {
                        GenCheck(m2, "settled map");
                    }
                    StartTicks(600, "f_save");
                    break;
                }
                case "f_save":
                    savedTicks = Find.TickManager.TicksGame;
                    GameDataSaveLoader.SaveGame("pz_worldfloors");
                    Result("saved pz_worldfloors", File.Exists(GenFilePaths.FilePathForSavedGame("pz_worldfloors")), "");
                    WorldFloors.lastMigration = null;
                    stage = "f_load";
                    pendingLoad = "pz_worldfloors";
                    break;
                case "f_load":
                {
                    Result("reload: same game", System.Math.Abs(ticksAfterLoad - savedTicks) <= 10, $"ticks on load {ticksAfterLoad} vs saved {savedTicks}");
                    Result("reload keeps the world's floors", wc != null && wc.floors == WfChosen && !wc.needsMigration && WorldFloors.lastMigration == null, $"world floors {wc?.floors}, migration {WorldFloors.lastMigration ?? "none"}");
                    Result("reload: both maps keep their floors", Find.Maps.Count >= 2 && Find.Maps.All(m => m.GetComponent<AnnexMapComponent>()?.floors == WfChosen), WfMaps());
                    Result("reload: mod setting still ignored", ProjectZ.FloorsForNewMaps == WfChosen, $"setting {ProjectZMod.Settings.floors}");
                    // An old ProjectZ save: no world component.
                    Find.World.components.Remove(wc);
                    GameDataSaveLoader.SaveGame("pz_wf_old");
                    Result("saved pz_wf_old (no world floors)", File.Exists(GenFilePaths.FilePathForSavedGame("pz_wf_old")), "");
                    WorldFloors.lastMigration = null;
                    stage = "f_oldload";
                    pendingLoad = "pz_wf_old";
                    break;
                }
                case "f_oldload":
                    Result("old ProjectZ save: migrated to its maps' floors", wc != null && wc.floors == WfChosen && !wc.needsMigration && WorldFloors.lastMigration != null, $"world floors {wc?.floors}; log: {WorldFloors.lastMigration ?? "none"}; {WfMaps()}");
                    if (!File.Exists(GenFilePaths.FilePathForSavedGame("pz_plain")))
                    {
                        Result("plain save present", false, "run mode plain first");
                        Finish();
                        break;
                    }
                    WorldFloors.lastMigration = null;
                    stage = "f_plain";
                    pendingLoad = "pz_plain";
                    break;
                case "f_plain":
                    Result("plain save: migrated to 1", wc != null && wc.floors == 1 && WorldFloors.lastMigration != null && !ProjectZ.HasAnnex(map), $"world floors {wc?.floors}; log: {WorldFloors.lastMigration ?? "none"}; {WfMaps()}");
                    Result("plain save: setting 7 still ignored", ProjectZ.FloorsForNewMaps == 1, $"setting {ProjectZMod.Settings.floors}, floors for new maps {ProjectZ.FloorsForNewMaps}");
                    StartTicks(300, "f_plain_settle");
                    break;
                case "f_plain_settle":
                    WfSettle(map, "f_plain_settled");
                    break;
                case "f_plain_settled":
                {
                    Map m2 = Find.Maps.Count > wfMapsBefore ? Find.Maps.Last() : null;
                    Result("plain save: a new colony gets no upper floors", m2 != null && !ProjectZ.HasAnnex(m2), WfMaps());
                    StartTicks(600, "f_end");
                    break;
                }
                case "f_end":
                    Info($"end: {WfMaps()}");
                    Finish();
                    break;
            }
        }
    }
}
