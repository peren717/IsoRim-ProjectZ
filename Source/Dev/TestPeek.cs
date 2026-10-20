using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode peek (run with -projectzfloors=3): the peek key (PZ_PeekUpper, PeekBridge) sets IsoRim's Api.PeekUpperFloors
    // (IsoRim API 7) while held. A placed 7 x 7 three-storey building (walls on each floor, slabs on floors 2 and 3, a roof).
    // The key is held by a prefix on KeyBindingDef.IsDown (as the game would report a held key); PeekBridge runs as usual.
    //   P1 ground viewed, key up: nothing faded                                               peek_off.png
    //   P2 key held: the flag is on, floors 2 and 3 drawn faded (IsoRim Floors.FadeOf)        peek_on.png
    //   P3 key held: screen points over the faded floors pick the same cells as with the key up (the viewed floor)
    //   P4 key held with a text field focused: no peek
    //   P5 key released: flag off, nothing faded                                              peek_released.png
    //   P6 floor 2 viewed, key held: only floor 3 faded; points as with the key up            peek_on_l1.png
    //   P7 IsoRim lists no peek key (no R3D_PeekUpper def); IsoRim's settings shot             peek_isorim_settings.png
    public static partial class TestRunner
    {
        private static IntVec3 pkO;
        private static CellRect pkHouse;
        private static bool pkHold, pkPatched;
        private static System.Type pkFloors;
        private static readonly List<IntVec3> pkPicksUp = new List<IntVec3>();
        private static PkTypeWindow pkTyping;

        // A small window whose text field takes the keyboard focus when it opens; focused: the focused control seen in its
        // last GUI pass.
        private class PkTypeWindow : Window
        {
            private string text = "name";
            public int focused;

            public override Vector2 InitialSize => new Vector2(300f, 120f);

            public override void DoWindowContents(Rect inRect)
            {
                GUI.SetNextControlName("pkType");
                text = Widgets.TextField(new Rect(0f, 0f, inRect.width, 30f), text);
                if (GUI.GetNameOfFocusedControl() != "pkType")
                {
                    GUI.FocusControl("pkType");
                }
                focused = GUIUtility.keyboardControl;
            }
        }

        private static bool PkHoldPrefix(KeyBindingDef __instance, ref bool __result)
        {
            if (pkHold && __instance == PZKeyDefOf.PZ_PeekUpper)
            {
                __result = true;
                return false;
            }
            return true;
        }

        private static int PkFade(int k) => (int)AccessTools.Method(pkFloors, "FadeOf").Invoke(null, new object[] { k });

        private static bool PkFlag() => IsoRim.Api.PeekUpperFloors;

        private static string PkState() => $"flag {PkFlag()}, fade slots floor 1/2/3: {PkFade(0)}/{PkFade(1)}/{PkFade(2)}, viewed level {FloorView.Current}";

        // Screen points over the building's cells raised to each storey (the faded floors' slabs and walls): the cells the
        // game's pointer picks there.
        private static List<IntVec3> PkPicks(Map map)
        {
            List<IntVec3> got = new List<IntVec3>();
            foreach (IntVec3 g in new[] { pkHouse.CenterCell, new IntVec3(pkHouse.minX + 1, 0, pkHouse.minZ + 1), new IntVec3(pkHouse.maxX, 0, pkHouse.maxZ) })
            {
                for (int k = 0; k <= 2; k++)
                {
                    Vector2 px = IsoRimBridge.ScreenOf(new Vector3(g.x + 0.5f, k * IsoRimBridge.StoreyHeight, g.z + 0.5f));
                    got.Add(Verse.UI.UIToMapPosition(px / Prefs.UIScale).ToIntVec3());
                }
            }
            return got;
        }

        private static string PkLevels(Map map, List<IntVec3> cells) => string.Join(",", cells.Select(c => Levels.LevelOf(c, map)));

        private static bool StepPeek(Map map)
        {
            switch (stage)
            {
                case "pk_setup":
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    pkFloors = AccessTools.TypeByName("IsoRim.Floors");
                    if (!IsoRimBridge.Present || IsoRimBridge.ApiVersion < 7 || pkFloors == null || ProjectZ.Annex(map) == null)
                    {
                        Result("peek: IsoRim API 7 and a map with floors", false, $"present {IsoRimBridge.Present}, api {IsoRimBridge.ApiVersion}, Floors {pkFloors != null}, annex {ProjectZ.Annex(map) != null}");
                        Finish();
                        return true;
                    }
                    SetupPeek(map);
                    if (!pkPatched)
                    {
                        pkPatched = true;
                        new Harmony("Peren.IsoRimProjectZ.test.peek").Patch(AccessTools.PropertyGetter(typeof(KeyBindingDef), nameof(KeyBindingDef.IsDown)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(PkHoldPrefix)));
                    }
                    ShotAt(map, 0, pkHouse.CenterCell, 11f);
                    Find.UIRoot.screenshotMode.Active = true;
                    Wait(40, "pk_off");
                    return true;

                case "pk_off":
                    Result("peek P1: ground viewed, key up: nothing faded", !PkFlag() && PkFade(1) == 0 && PkFade(2) == 0, PkState());
                    pkPicksUp.Clear();
                    pkPicksUp.AddRange(PkPicks(map));
                    Capture("peek_off");
                    Wait(10, "pk_hold");
                    return true;

                case "pk_hold":
                    pkHold = true;
                    Wait(20, "pk_on");
                    return true;

                case "pk_on":
                {
                    Result("peek P2: key held: floors 2 and 3 faded", PkFlag() && PkFade(1) > 0 && PkFade(2) > 0, PkState());
                    List<IntVec3> picks = PkPicks(map);
                    Result("peek P3: key held: the pointer picks the same cells as with the key up", picks.SequenceEqual(pkPicksUp),
                        $"held [{string.Join(" ", picks)}] levels {PkLevels(map, picks)}; key up [{string.Join(" ", pkPicksUp)}] levels {PkLevels(map, pkPicksUp)}");
                    Capture("peek_on");
                    Wait(10, "pk_focus");
                    return true;
                }

                case "pk_focus":
                    // A window with a focused text field (the game's own text field), as when the player types a name.
                    pkTyping = new PkTypeWindow();
                    Find.WindowStack.Add(pkTyping);
                    Wait(20, "pk_focus_check");
                    return true;

                case "pk_focus_check":
                {
                    bool flag = PkFlag();
                    Result("peek P4: key held with a text field focused: no peek", pkTyping.focused != 0 && !flag, $"text field control {pkTyping.focused}, keyboardControl now {GUIUtility.keyboardControl}, {PkState()}");
                    pkTyping.Close(false);
                    GUIUtility.keyboardControl = 0;
                    Wait(10, "pk_release");
                    return true;
                }

                case "pk_release":
                    Result("peek P4b: focus gone, key still held: peek again", PkFlag(), PkState());
                    pkHold = false;
                    Wait(20, "pk_released");
                    return true;

                case "pk_released":
                    Result("peek P5: key released: nothing faded", !PkFlag() && PkFade(1) == 0 && PkFade(2) == 0, PkState());
                    Capture("peek_released");
                    Wait(10, "pk_l1");
                    return true;

                case "pk_l1":
                    ShotAt(map, 1, pkHouse.CenterCell, 11f);
                    Find.UIRoot.screenshotMode.Active = true;
                    Wait(30, "pk_l1_up");
                    return true;

                case "pk_l1_up":
                    pkPicksUp.Clear();
                    pkPicksUp.AddRange(PkPicks(map));
                    pkHold = true;
                    Wait(20, "pk_l1_on");
                    return true;

                case "pk_l1_on":
                {
                    List<IntVec3> picks = PkPicks(map);
                    Result("peek P6: floor 2 viewed, key held: only floor 3 faded, pointer unchanged",
                        PkFlag() && PkFade(1) == 0 && PkFade(2) > 0 && picks.SequenceEqual(pkPicksUp),
                        $"{PkState()}; held [{string.Join(" ", picks)}] levels {PkLevels(map, picks)}; key up [{string.Join(" ", pkPicksUp)}] levels {PkLevels(map, pkPicksUp)}");
                    Capture("peek_on_l1");
                    Wait(10, "pk_l1_release");
                    return true;
                }

                case "pk_l1_release":
                    pkHold = false;
                    Wait(20, "pk_settings");
                    return true;

                case "pk_settings":
                {
                    Result("peek P6b: released on floor 2: nothing faded", !PkFlag() && PkFade(2) == 0, PkState());
                    bool isoKey = DefDatabase<KeyBindingDef>.GetNamedSilentFail("R3D_PeekUpper") != null;
                    KeyBindingDef pz = DefDatabase<KeyBindingDef>.GetNamedSilentFail("PZ_PeekUpper");
                    Result("peek P7: IsoRim has no peek key, ProjectZ has it (no default key)", !isoKey && pz != null && pz.defaultKeyCodeA == KeyCode.None,
                        $"R3D_PeekUpper {isoKey}; PZ_PeekUpper '{pz?.label}' category {pz?.category?.defName} default {pz?.defaultKeyCodeA}");
                    Mod iso = LoadedModManager.ModHandles.FirstOrDefault(m => m.GetType().FullName == "IsoRim.IsoRimMod");
                    AccessTools.Field(AccessTools.TypeByName("IsoRim.SettingsWindow"), "tab")?.SetValue(null, 0);
                    Find.UIRoot.screenshotMode.Active = false;
                    if (iso != null)
                    {
                        Find.WindowStack.Add(new Dialog_ModSettings(iso));
                    }
                    Wait(30, "pk_settings_shot");
                    return true;
                }

                case "pk_settings_shot":
                    Capture("peek_isorim_settings");
                    Wait(5, "pk_end");
                    return true;

                case "pk_end":
                    Find.WindowStack.TryRemove(typeof(Dialog_ModSettings), false);
                    Finish();
                    return true;
            }
            return false;
        }

        private static void SetupPeek(Map map)
        {
            List<Pawn> cols = Colonists(map);
            IntVec3 near = cols.Count > 0 ? new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z)) : map.Center;
            int groundZ = ProjectZ.GroundZ(map);
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 60f, true))
            {
                CellRect r = new CellRect(c.x - 8, c.z - 8, 17, 17);
                if (!r.InBounds(map) || r.maxZ >= groundZ - 3)
                {
                    continue;
                }
                if (r.Cells.All(x => !x.Fogged(map) && !map.roofGrid.Roofed(x) && !x.GetTerrain(map).IsWater && x.GetTerrain(map).passability != Traversability.Impassable && x.GetThingList(map).All(t => t is Pawn || t.def.destroyable)))
                {
                    found = r;
                    break;
                }
            }
            if (found.Area == 0)
            {
                found = CellRect.CenteredOn(map.Center, 8);
            }
            pkO = found.CenterCell;
            foreach (IntVec3 c in found)
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable)
                    {
                        t.Destroy();
                    }
                }
                map.terrainGrid.SetTerrain(c, TerrainDefOf.Concrete);
                map.snowGrid.SetDepth(c, 0f);
            }
            Find.Storyteller.storytellerComps.Clear();
            // 7 x 7, three storeys: ground walls (a door south) and roof; slab + walls on floor 2; slab + walls on floor 3; roof.
            pkHouse = new CellRect(pkO.x - 3, pkO.z - 3, 7, 7);
            IntVec3 door = new IntVec3(pkO.x, 0, pkHouse.minZ);
            for (int k = 0; k <= 2; k++)
            {
                foreach (IntVec3 g in pkHouse)
                {
                    IntVec3 c = k == 0 ? g : ProjectZ.CellOnFloor(g, k, map);
                    if (k > 0)
                    {
                        map.terrainGrid.SetTerrain(c, PZDefOf.PZ_UpperFloor);
                    }
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                }
                foreach (IntVec3 g in pkHouse.EdgeCells)
                {
                    IntVec3 c = k == 0 ? g : ProjectZ.CellOnFloor(g, k, map);
                    Place(k == 0 && g == door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
                }
            }
            Info($"peek: house {pkHouse} (3 storeys), api {IsoRimBridge.ApiVersion}, key {PZKeyDefOf.PZ_PeekUpper?.defName}");
        }
    }
}
