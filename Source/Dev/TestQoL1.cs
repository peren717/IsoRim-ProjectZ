using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode qol1 (run with -projectzfloors=3): ground-only buildings, the floor scroll and the floor HUD.
    //   O1 the ground-only list (XML) resolves; each listed building is refused on floor 2 with "Must be built on the
    //      ground" through the build designator and the UI pointer, and not refused for that on the ground; gravship
    //      parts (need substructure) are refused upstairs; a mortar is allowed upstairs.
    //   O2 colonists build a grave on the ground (designated through the UI pointer, real ticks); the mortar designated on
    //      floor 2 leaves its blueprint there (not built: 225 steel, 6 components, construction 5).
    //   O3 the HUD: dots per floor (colonists on 1 and 2, a raider on 1, a fire on 3), switching by clicking its bars
    //      (a mouse-down event through the game's GlobalControls), shots at floors 1/2/3 at UI scale 1 and 1.5; the
    //      floor scroll (a wheel event with the hold key down switches floors and doesn't zoom; without it, zoom);
    //      Alt + drag moves it, reset puts it back in its slot, hide; the mod settings shot.
    public static partial class TestRunner
    {
        private static IntVec3 oO, oStairs, oGrave, oMortar, oDrill;
        private static CellRect oHouse;
        private static bool oPatched;
        private static bool oHold;
        private static int oShot;
        private static readonly string[] OWant = { "DeepDrill", "GroundPenetratingScanner", "MoisturePump", "Grave", "Bridge", "HeavyBridge", "Substructure" };
        private static readonly Dictionary<string, (AcceptanceReport r, IntVec3 c)> oUp = new Dictionary<string, (AcceptanceReport r, IntVec3 c)>();

        private static IntVec3 O(int dx, int dz) => new IntVec3(oO.x + dx, 0, oO.z + dz);

        private static BuildableDef ODef(string n) => (BuildableDef)DefDatabase<ThingDef>.GetNamedSilentFail(n) ?? DefDatabase<TerrainDef>.GetNamedSilentFail(n);

        private static string OScale() => $"UI x{Prefs.UIScale} (data {Traverse.Create(typeof(Prefs)).Field("data").Field("uiScale").GetValue<float>()}), screen {Screen.width}x{Screen.height}, UI {UI.screenWidth}x{UI.screenHeight}";

        private static bool OHoldPrefix(KeyBindingDef __instance, ref bool __result)
        {
            if (oHold && __instance == PZKeyDefOf.PZ_FloorScrollHold)
            {
                __result = true;
                return false;
            }
            return true;
        }

        // A synthetic event through the game's bottom-right controls (where the HUD draws and reads input), as one GUI
        // pass of the frame would deliver it. Runs inside OnGUI (the UI scale already applied).
        private static void OEvent(EventType type, Vector2 pos, bool alt = false, Vector2 delta = default(Vector2))
        {
            Event saved = Event.current;
            Event ev = new Event { type = type, mousePosition = pos, button = 0, alt = alt, delta = delta, clickCount = 1 };
            if (alt)
            {
                ev.modifiers = EventModifiers.Alt;
            }
            Event.current = ev;
            try
            {
                Find.MapUI.globalControls.GlobalControlsOnGUI();
            }
            finally
            {
                Event.current = saved;
            }
        }

        private static Vector2 RowCenter(int k)
        {
            int n = ProjectZ.Annex(Find.CurrentMap).floors;
            return FloorHud.RowRect(FloorHud.LastRect, n, k).center;
        }

        private static string R(Rect r) => $"({r.x:0.#},{r.y:0.#} {r.width:0.#}x{r.height:0.#})";

        private static bool StepQoL1(Map map)
        {
            switch (stage)
            {
                case "o_setup":
                    SetupQoL1(map);
                    return true;

                // ---- O1: ground-only refusals through the designators and the UI pointer
                case "o_view":
                    ShotAt(map, 1, oHouse.CenterCell, 12f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(10, "o_refuse");
                    return true;
                case "o_refuse":
                {
                    // floor 2 viewed (o_view): every listed building over a slab cell, gravship parts, the mortar
                    GroundOnlyDef list = DefDatabase<GroundOnlyDef>.GetNamedSilentFail("PZ_GroundOnly");
                    List<string> have = list == null ? new List<string>() : list.things.Select(t => t.defName).Concat(list.terrains.Select(t => t.defName)).ToList();
                    Result("ground-only list (XML) resolves", OWant.All(have.Contains) && OWant.All(n => GroundOnly.Is(ODef(n))), string.Join(", ", have));
                    oUp.Clear();
                    foreach (string n in OWant)
                    {
                        BuildableDef def = ODef(n);
                        if (def == null)
                        {
                            Result($"{n}: def exists", false, "missing");
                            continue;
                        }
                        Designator_Build d = new Designator_Build(def);
                        Find.DesignatorManager.Select(d);
                        AcceptanceReport up = RefusalAt(d, ScreenOf(O(9, 5), 1), out IntVec3 upCell);
                        Find.DesignatorManager.Deselect();
                        oUp[n] = (up, upCell);
                    }
                    // gravship parts follow (they need substructure, which can't be upstairs)
                    List<ThingDef> grav = DefDatabase<ThingDef>.AllDefs.Where(t => t.BuildableByPlayer && t.terrainAffordanceNeeded?.defName == "Substructure").ToList();
                    List<string> allowed = new List<string>();
                    List<string> reasons = new List<string>();
                    foreach (ThingDef t in grav)
                    {
                        Designator_Build d = new Designator_Build(t);
                        Find.DesignatorManager.Select(d);
                        AcceptanceReport r = RefusalAt(d, ScreenOf(O(9, 5), 1), out IntVec3 c);
                        if (r.Accepted || ProjectZ.FloorOf(c, map) != 1)
                        {
                            allowed.Add(t.defName + "@" + c);
                        }
                        reasons.Add(r.Reason);
                        Find.DesignatorManager.Deselect();
                    }
                    Result("gravship parts needing substructure are refused on floor 2", grav.Count > 0 && allowed.Count == 0,
                        $"{grav.Count} parts ({string.Join(",", grav.Select(t => t.defName))}); reasons: {string.Join(" | ", reasons.Distinct())}; allowed: {string.Join(",", allowed)}");
                    // not on the list: mortar, geothermal, watermill (vanilla's own requirements decide those)
                    ThingDef mortar = DefDatabase<ThingDef>.GetNamed("Turret_Mortar");
                    Designator_Build dm = new Designator_Build(mortar);
                    Find.DesignatorManager.Select(dm);
                    AcceptanceReport mr = RefusalAt(dm, ScreenOf(oMortar, 1), out IntVec3 mc);
                    Find.DesignatorManager.Deselect();
                    Result("mortar allowed on floor 2", mr.Accepted && ProjectZ.FloorOf(mc, map) == 1, $"{mc}: {(mr.Accepted ? "accepted" : mr.Reason)}");
                    foreach (string n in new[] { "GeothermalGenerator", "WatermillGenerator" })
                    {
                        ThingDef t = DefDatabase<ThingDef>.GetNamedSilentFail(n);
                        Result($"{n} not on the list", t != null && !GroundOnly.Is(t), t == null ? "missing" : "ok");
                    }
                    FloorView.Set(0);
                    Wait(10, "o_refuse_ground");
                    return true;
                }
                case "o_refuse_ground":
                {
                    string msg = "PZ_GroundOnly".Translate();
                    foreach (string n in OWant)
                    {
                        BuildableDef def = ODef(n);
                        if (def == null || !oUp.TryGetValue(n, out (AcceptanceReport r, IntVec3 c) up))
                        {
                            continue;
                        }
                        Designator_Build d = new Designator_Build(def);
                        Find.DesignatorManager.Select(d);
                        AcceptanceReport down = RefusalAt(d, ScreenOf(n == "DeepDrill" ? oDrill : O(17, 5), 0), out IntVec3 downCell);
                        Find.DesignatorManager.Deselect();
                        // (bridges need water: on dry ground the game refuses them for that, not for the ground rule)
                        bool landBridge = n == "Bridge" || n == "HeavyBridge";
                        Result($"{n}: refused on floor 2 with the ground-only message, {(landBridge ? "not for that reason" : "allowed")} on the ground",
                            ProjectZ.FloorOf(up.c, map) == 1 && !up.r.Accepted && up.r.Reason == msg && ProjectZ.FloorOf(downCell, map) == 0 && (landBridge ? !down.Accepted && down.Reason != msg : down.Accepted),
                            $"floor 2 {up.c}: '{up.r.Reason}'; ground {downCell}: {(down.Accepted ? "accepted" : "'" + down.Reason + "'")}");
                    }
                    FloorView.Set(1);
                    Wait(10, "o_refuse_shot");
                    return true;
                }
                case "o_refuse_shot":
                {
                    // the refusal as the player sees it: the grave designator over floor 2, the message after a click
                    Designator_Build d = new Designator_Build(ThingDefOf.Grave);
                    Find.DesignatorManager.Select(d);
                    AcceptanceReport r = UiDesignate(ScreenOf(O(9, 5), 1), ScreenOf(O(9, 5), 1));
                    // (what the designator manager shows for a refused click)
                    Messages.Message(r.Reason, MessageTypeDefOf.RejectInput, false);
                    fakeMouse = ScreenOf(O(9, 5), 1);
                    Wait(20, "o_refuse_shot2");
                    return true;
                }
                case "o_refuse_shot2":
                    Capture("qol_grave_refused");
                    Wait(2, "o_build");
                    return true;

                // ---- O2: colonists build a grave on the ground; a mortar blueprint on floor 2
                case "o_build":
                    fakeMouse = null;
                    Find.DesignatorManager.Deselect();
                    FloorView.Set(0);
                    Wait(10, "o_build_grave");
                    return true;
                case "o_build_grave":
                {
                    Find.DesignatorManager.Select(new Designator_Build(ThingDefOf.Grave));
                    AcceptanceReport g = UiDesignate(ScreenOf(oGrave, 0), ScreenOf(oGrave, 0));
                    Find.DesignatorManager.Deselect();
                    Info($"designated: grave at {oGrave} '{g.Reason}' accepted {g.Accepted}");
                    FloorView.Set(1);
                    Wait(10, "o_build_mortar");
                    return true;
                }
                case "o_build_mortar":
                {
                    Find.DesignatorManager.Select(new Designator_Build(DefDatabase<ThingDef>.GetNamed("Turret_Mortar")));
                    AcceptanceReport m = UiDesignate(ScreenOf(oMortar, 1), ScreenOf(oMortar, 1));
                    Find.DesignatorManager.Deselect();
                    Info($"designated: mortar at {UpS(oMortar, map)} '{m.Reason}' accepted {m.Accepted}; planned {map.listerThings.AllThings.Count(t => t is Blueprint && t.Spawned)}");
                    SetWork(map, true);
                    foreach (Pawn p in Colonists(map))
                    {
                        if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                        {
                            p.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2);
                        }
                    }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(30000, () => OGrave(map) != null, "o_built", 120);
                    return true;
                }
                case "o_built":
                {
                    Thing grave = OGrave(map), mortar = OMortar(map);
                    Result("colonists built the grave on the ground", grave != null && ProjectZ.FloorOf(grave.Position, map) == 0, $"{grave} at {grave?.Position}");
                    Result("the mortar planned on floor 2 (blueprint or frame there)", mortar != null && ProjectZ.FloorOf(mortar.Position, map) == 1, $"{mortar} at {mortar?.Position}");
                    SetWork(map, false);
                    stage = "o_hud";
                    return true;
                }

                // ---- O3: the HUD
                case "o_hud":
                {
                    List<Pawn> cols = Colonists(map);
                    // colonists: one on the ground, one on floor 2, none on floor 3; danger: a raider on the ground, fire on floor 3
                    Teleport(cols[0], O(6, -3));
                    Teleport(cols[1], UpS(O(4, 5), map));
                    for (int i = 2; i < cols.Count; i++)
                    {
                        Teleport(cols[i], O(8 + i, -3));
                    }
                    foreach (Pawn p in cols)
                    {
                        Draft(p, true);
                    }
                    MakeRaider(map, O(30, -2));
                    IntVec3 f3 = ProjectZ.CellOnFloor(O(3, 5), 2, map);
                    Fire fire = (Fire)ThingMaker.MakeThing(ThingDefOf.Fire);
                    fire.fireSize = 0.6f;
                    GenSpawn.Spawn(fire, f3, map);
                    ShotAt(map, 0, oHouse.CenterCell, 12f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(30, "o_dots");
                    return true;
                }
                case "o_dots":
                {
                    FloorHud.Refresh(map, 3, Time.realtimeSinceStartup, force: true);
                    Result("HUD dots: colonists on floors 1 and 2, danger on 1 (raider) and 3 (fire)",
                        FloorHud.ColonistOn(0) && FloorHud.ColonistOn(1) && !FloorHud.ColonistOn(2) && FloorHud.DangerOn(0) && !FloorHud.DangerOn(1) && FloorHud.DangerOn(2),
                        $"colonist {FloorHud.ColonistOn(0)},{FloorHud.ColonistOn(1)},{FloorHud.ColonistOn(2)}; danger {FloorHud.DangerOn(0)},{FloorHud.DangerOn(1)},{FloorHud.DangerOn(2)}");
                    Rect r = FloorHud.LastRect;
                    Result("HUD in its slot at the bottom right (right edge 3 px in, 3 floors = 42 px tall)",
                        Mathf.Abs(r.xMax - (UI.screenWidth - 3f)) < 0.01f && Mathf.Abs(r.height - FloorHud.StackHeight(3)) < 0.01f && r.yMax < UI.screenHeight - 30f,
                        $"HUD {R(r)}, screen {UI.screenWidth}x{UI.screenHeight}");
                    for (int k = 0; k < 3; k++)
                    {
                        Info("tooltip floor " + (k + 1) + ": " + FloorHudTip(k).Replace("\n", " | "));
                    }
                    oShot = 0;
                    stage = "o_click";
                    return true;
                }
                // floors 2, 3, then 1 by clicking their bars; a shot of each (UI scale 1, then 1.5)
                case "o_click":
                {
                    int[] order = { 1, 2, 0 };
                    int k = order[oShot % 3];
                    guiAction = () =>
                    {
                        Vector2 at = RowCenter(k);
                        OEvent(EventType.MouseDown, at);
                        Result($"UI x{Prefs.UIScale}: clicking the HUD bar of floor {k + 1} views it", FloorView.Current == k, $"click at {at}, viewing floor {FloorView.Current + 1}");
                    };
                    Wait(25, "o_click_shot");
                    return true;
                }
                case "o_click_shot":
                {
                    int[] order = { 1, 2, 0 };
                    int k = order[oShot % 3];
                    Capture($"qol_hud_f{k + 1}_ui{(oShot >= 3 ? "15" : "1")}");
                    Info($"HUD rect {R(FloorHud.LastRect)} (UI px); {OScale()}");
                    oShot++;
                    // (the next change waits for the shot: a change in this update would show in it)
                    Wait(2, oShot == 3 ? "o_resize" : oShot == 6 ? "o_unscale" : "o_click");
                    return true;
                }
                case "o_resize":
                    // UI scale 1.5 needs at least 768 UI px of height: a 1920 x 1152 window
                    oScreen = new Vector2Int(Screen.width, Screen.height);
                    Screen.SetResolution(1920, 1152, false);
                    Wait(30, "o_scale");
                    return true;
                case "o_unscale":
                    Prefs.UIScale = 1f;
                    Screen.SetResolution(oScreen.x, oScreen.y, false);
                    Wait(30, "o_mid");
                    return true;
                case "o_scale":
                    Info("resized: " + OScale());
                    Prefs.UIScale = 1.5f;
                    Wait(30, "o_scale2");
                    return true;
                case "o_scale2":
                    Info("UI scale set to 1.5: " + OScale());
                    stage = "o_click";
                    return true;
                // a mid-slide frame (animation), view 1 -> 2
                case "o_mid":
                    guiAction = () => OEvent(EventType.MouseDown, RowCenter(1));
                    Wait(3, "o_mid_shot");
                    return true;
                case "o_mid_shot":
                    Capture("qol_hud_slide");
                    Wait(20, "o_scroll");
                    return true;
                // the floor scroll
                case "o_scroll":
                {
                    if (!oPatched)
                    {
                        oPatched = true;
                        new Harmony("Peren.IsoRimProjectZ.test.qol1").Patch(AccessTools.PropertyGetter(typeof(KeyBindingDef), nameof(KeyBindingDef.IsDown)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(OHoldPrefix)));
                    }
                    FloorView.Set(0);
                    guiAction = () =>
                    {
                        Traverse size = Traverse.Create(Find.CameraDriver).Field("desiredSize");
                        float before = size.GetValue<float>();
                        oHold = true;
                        string log = "";
                        foreach (float dy in new[] { -3f, -3f, 3f })
                        {
                            Event saved = Event.current;
                            Event ev = new Event { type = EventType.ScrollWheel, delta = new Vector2(0f, dy), mousePosition = new Vector2(UI.screenWidth / 2f, UI.screenHeight / 2f) };
                            Event.current = ev;
                            try
                            {
                                Patches_FloorKeys();
                                Find.CameraDriver.CameraDriverOnGUI();
                            }
                            finally
                            {
                                Event.current = saved;
                            }
                            log += $" wheel {dy}: floor {FloorView.Current + 1}, size {size.GetValue<float>():0.00};";
                        }
                        oHold = false;
                        float held = size.GetValue<float>();
                        Result("shift + wheel: up, up, down = floors 2, 3, 2, and no zoom", FloorView.Current == 1 && Mathf.Abs(held - before) < 0.001f, $"size before {before:0.00};{log}");
                        Event saved2 = Event.current;
                        Event ev2 = new Event { type = EventType.ScrollWheel, delta = new Vector2(0f, -3f), mousePosition = new Vector2(UI.screenWidth / 2f, UI.screenHeight / 2f) };
                        Event.current = ev2;
                        try
                        {
                            Patches_FloorKeys();
                            Find.CameraDriver.CameraDriverOnGUI();
                        }
                        finally
                        {
                            Event.current = saved2;
                        }
                        float plain = size.GetValue<float>();
                        Result("plain wheel: zooms, floor unchanged", FloorView.Current == 1 && Mathf.Abs(plain - held) > 0.001f, $"floor {FloorView.Current + 1}, size {held:0.00} -> {plain:0.00}");
                        size.SetValue(before);
                    };
                    Wait(10, "o_drag");
                    return true;
                }
                // Alt + drag moves it; reset; hide
                case "o_drag":
                {
                    guiAction = () =>
                    {
                        Rect before = FloorHud.LastRect;
                        Vector2 at = RowCenter(0);
                        OEvent(EventType.MouseDown, at, alt: true);
                        OEvent(EventType.MouseDrag, at + new Vector2(-300f, -200f), alt: true);
                        OEvent(EventType.MouseDrag, at + new Vector2(-500f, -300f), alt: true);
                        OEvent(EventType.MouseUp, at + new Vector2(-500f, -300f), alt: true);
                        Info($"drag from {R(before)}: settings moved {ProjectZMod.Settings.floorHudMoved}, right {ProjectZMod.Settings.floorHudRight:0.#}, bottom {ProjectZMod.Settings.floorHudBottom:0.#}");
                        oDragFrom = before;
                    };
                    Wait(5, "o_drag_check");
                    return true;
                }
                case "o_drag_check":
                {
                    Rect r = FloorHud.LastRect;
                    Result("Alt + drag moves the HUD (500 left, 300 up), the view stays", ProjectZMod.Settings.floorHudMoved && Mathf.Abs(r.x - (oDragFrom.x - 500f)) < 0.5f && Mathf.Abs(r.y - (oDragFrom.y - 300f)) < 0.5f && FloorView.Current == 1,
                        $"{R(oDragFrom)} -> {R(r)}, viewing floor {FloorView.Current + 1}");
                    Capture("qol_hud_moved");
                    Wait(3, "o_reset");
                    return true;
                }
                case "o_reset":
                    FloorHud.ResetPosition();
                    Wait(5, "o_reset_check");
                    return true;
                case "o_reset_check":
                {
                    Rect r = FloorHud.LastRect;
                    Result("reset puts the HUD back in its slot", !ProjectZMod.Settings.floorHudMoved && Mathf.Abs(r.x - oDragFrom.x) < 0.5f && Mathf.Abs(r.y - oDragFrom.y) < 0.5f, $"{R(r)}, slot {R(oDragFrom)}");
                    ProjectZMod.Settings.hideFloorHud = true;
                    FloorHud.LastRect = Rect.zero;
                    Wait(5, "o_hide_check");
                    return true;
                }
                case "o_hide_check":
                    Result("hide floor HUD: not drawn", FloorHud.LastRect == Rect.zero, R(FloorHud.LastRect));
                    Capture("qol_hud_hidden");
                    Wait(3, "o_settings");
                    return true;
                case "o_settings":
                    ProjectZMod.Settings.hideFloorHud = false;
                    Find.WindowStack.Add(new Dialog_ModSettings(LoadedModManager.GetMod<ProjectZMod>()));
                    Wait(30, "o_settings_shot");
                    return true;
                case "o_settings_shot":
                    Capture("qol_settings");
                    Wait(3, "o_end");
                    return true;
                case "o_end":
                    Find.WindowStack.TryRemove(typeof(Dialog_ModSettings), false);
                    Finish();
                    return true;
            }
            return false;
        }

        private static Rect oDragFrom;
        private static Vector2Int oScreen;

        private static void Patches_FloorKeys() => FloorKeys_Patch.Postfix();

        private static string FloorHudTip(int k) => "PZ_FloorHudTip".Translate(FloorView.Label(k), FloorView.KeyName(PZKeyDefOf.PZ_FloorUp),
            FloorView.KeyName(PZKeyDefOf.PZ_FloorDown), FloorView.KeyName(PZKeyDefOf.PZ_FloorScrollHold));

        private static Thing OGrave(Map map) => map.listerThings.ThingsOfDef(ThingDefOf.Grave).FirstOrDefault(t => t.Position == oGrave);

        private static Thing OMortar(Map map) => UpS(oMortar, map).GetThingList(map).FirstOrDefault(t => (t is Blueprint || t is Frame) && t.def.entityDefToBuild?.defName == "Turret_Mortar" || t.def.defName == "Turret_Mortar");

        private static void SetupQoL1(Map map)
        {
            List<Pawn> cols = Colonists(map);
            IntVec3 near = cols.Count > 0 ? new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z)) : map.Center;
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 79f, true))
            {
                CellRect r = new CellRect(c.x - 12, c.z - 8, 36, 17);
                CellRect m = r.ExpandedBy(3);
                if (!m.InBounds(map) || m.maxZ >= ProjectZ.GroundZ(map) - 12)
                {
                    continue;
                }
                if (m.Cells.All(x => !x.GetTerrain(map).IsWater && !map.roofGrid.Roofed(x) && !x.Fogged(map) && !(x.GetEdifice(map)?.def.building?.isNaturalRock ?? false)
                    && x.GetTerrain(map).passability == Traversability.Standable && x.GetTerrain(map).affordances.Any(a => a.defName == "Diggable")))
                {
                    found = r;
                    break;
                }
            }
            if (found.Area == 0)
            {
                Result("test spot", false, "no open diggable ground near the colonists");
                Finish();
                return;
            }
            foreach (IntVec3 c in found.ExpandedBy(3))
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable && (t is Plant || t.def.category == ThingCategory.Item || t is Filth || t is Building))
                    {
                        t.Destroy();
                    }
                }
            }
            DebugSettings.enableRandomMentalStates = false;
            Find.PlaySettings.autoRebuild = false;
            oO = new IntVec3(found.minX, 0, found.minZ + 4);
            // House: walls, door south, roof, a floor-2 slab over the inside except the stairwell; stairs up to the north;
            // a floor-3 slab over the west half (x <= 5) only, so the mortar at the east end has nothing above it.
            oHouse = new CellRect(oO.x, oO.z, 13, 10);
            IntVec3 door = O(6, 0);
            foreach (IntVec3 c in oHouse.EdgeCells)
            {
                Place(c == door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            foreach (IntVec3 c in oHouse)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                map.areaManager.Home[c] = true;
            }
            oStairs = O(2, 3);
            HashSet<IntVec3> well = new HashSet<IntVec3>(StairShape.Footprint(oStairs, Rot4.North).Cells);
            foreach (IntVec3 g in oHouse.ContractedBy(1))
            {
                if (!well.Contains(g))
                {
                    map.terrainGrid.SetTerrain(UpS(g, map), PZDefOf.PZ_UpperFloor);
                    IntVec3 f3 = ProjectZ.CellOnFloor(g, 2, map);
                    if (f3.IsValid && g.x <= oO.x + 5 && !well.Contains(g))
                    {
                        map.terrainGrid.SetTerrain(f3, PZDefOf.PZ_UpperFloor);
                    }
                }
            }
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, oStairs, map, Rot4.North);
            oMortar = O(10, 6);
            oGrave = O(20, 9);
            oDrill = O(22, 3);
            map.deepResourceGrid.SetAt(oDrill, ThingDefOf.Steel, 300);
            // gravship substructure on the ground needs an inspected grav engine nearby
            ThingDef engine = DefDatabase<ThingDef>.GetNamedSilentFail("GravEngine");
            if (engine != null)
            {
                Find.ResearchManager.gravEngineInspected = true;
                Place(engine, null, O(26, 12), map);
            }
            foreach ((ThingDef def, int count) in new[] { (ThingDefOf.Steel, 75), (ThingDefOf.Steel, 75), (ThingDefOf.ComponentIndustrial, 6) })
            {
                Thing t = ThingMaker.MakeThing(def);
                t.stackCount = count;
                GenPlace.TryPlaceThing(t, O(6, -2), map, ThingPlaceMode.Near);
            }
            SetWork(map, false);
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], O(4 + i, -2));
            }
            Info($"qol1: house {oHouse}, stairs {oStairs}, mortar at {UpS(oMortar, map)}, grave at {oGrave}, drill at {oDrill}, floors {ProjectZ.Annex(map).floors}, colonists {cols.Count}");
            AnnexWatch.Reset();
            Wait(5, "o_view");
        }
    }
}
