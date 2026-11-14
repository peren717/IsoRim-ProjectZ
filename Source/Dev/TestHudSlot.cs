using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode hudslot (run with -projectzfloors=3): the floor HUD's slot in the game's controls column, and its fallback
    // place when nothing runs the play settings there (a UI mod that draws the column its own way). The same steps run
    // with and without such a mod; what the run must show comes from the command line:
    //   -projectzhudexpect=slot       the HUD in its slot above the play settings, drawn once a pass, letters not moved
    //   -projectzhudexpect=fallback   no play settings in the column: the HUD on top of the column (its bottom at the
    //                                 base the column's drawer gives the letters), drawn once a pass, letters and alerts
    //                                 lifted over it
    //   -projectzhudexpect=none       the HUD missing (a build from before the fallback next to such a mod)
    //   -projectzhudclicks=x,y;x,y    UI points clicked one by one through the main buttons (mouse down + up: popups a
    //                                 UI mod opens from its bar); each is shot with the windows that cover the HUD, then
    //                                 clicked again to close
    //   H1 the plain corner (after 700 ticks: the game's own alerts are up); H2 four letters: above the HUD, no overlap,
    //   then 25 (the stack bundles from the lifted base: it and the alerts stay on screen); H3 a game condition: its
    //   readout and every other column item drawn on screen (time controls, date, weather) clear of the HUD; H4 the
    //   popups; H5 a click on the HUD's level-1 bar views it, and a click on the game's time controls (when they are on
    //   screen) sets the speed; H6 the world view: no HUD, letters not lifted; H7 Alt + drag moves the HUD, it stays
    //   there, letters back down. Counts are per GUI pass (test-only patches: FloorHud.Draw, DoPlaySettings,
    //   LettersOnGUI's base before and after every other prefix, the rects of DoConditionsUI, the time controls, the date
    //   and the weather readout).
    //   The real pointer is kept out: every play UI pass starts with the pointer at the screen's centre (it may sit
    //   anywhere over the hidden test window, and then a tooltip or a hover highlight gets into a shot).
    //   -projectzhudtrace: logs what the HUD gets of every synthetic event, and who uses one.
    //   -projectzhudnoletters: the letter stack is never drawn (a first prefix skips LetterStack.LettersOnGUI, as a mod
    //   with its own letter stack would): the fallback HUD then keeps its place right above the main buttons; the
    //   letter checks are left out.
    //   -projectzhudbackdrop: a flat grey sheet under the whole play UI (drawn before the map's UI), so two runs' shots
    //   compare pixel by pixel in the UI alone (the 3D view differs a little from run to run).
    public static partial class TestRunner
    {
        private static string hsExpect = "slot";
        private static readonly List<Vector2> hsClicks = new List<Vector2>();
        private static int hsClick;
        // this pass so far / the last finished Repaint pass
        private static int hsDrawsNow, hsPlayNow, hsDraws, hsPlay;
        private static bool hsFallback;
        private static float hsBaseRaw = float.NaN, hsBase = float.NaN;
        // the column's items drawn on screen: in this pass so far / in the last finished Repaint pass
        private static Rect hsCondNow, hsTimeNow, hsDateNow, hsWeatherNow, hsCond, hsTime, hsDate, hsWeather;
        // the UI size and the GUI origin (screen px) where the HUD was last drawn: a UI mod may draw the controls column
        // inside its own group with its own UI size (a bar on another edge); the pass's size outside it otherwise
        private static int hsUiW, hsUiH;
        private static Vector2 hsOrigin;
        private static Rect hsBefore;
        private static GameCondition hsCondition;
        private static readonly PropertyInfo hsFallbackDrew = AccessTools.Property(typeof(FloorHud), "FallbackDrew");   // (none on a build before the fallback)

        internal static void PatchHudSlot(Harmony h)
        {
            if (GenCommandLine.TryGetCommandLineArg("projectzhudexpect", out string e))
            {
                hsExpect = e;
            }
            if (GenCommandLine.TryGetCommandLineArg("projectzhudclicks", out string c))
            {
                foreach (string p in c.Split(';'))
                {
                    string[] xy = p.Split(',');
                    hsClicks.Add(new Vector2(float.Parse(xy[0], CultureInfo.InvariantCulture), float.Parse(xy[1], CultureInfo.InvariantCulture)));
                }
            }
            h.Patch(AccessTools.Method(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(HsPinPointerPrefix)) { priority = Priority.First });
            h.Patch(AccessTools.Method(typeof(FloorHud), "Draw"), prefix: new HarmonyMethod(typeof(TestRunner), nameof(HsDrawPrefix)));
            h.Patch(AccessTools.Method(typeof(GlobalControlsUtility), nameof(GlobalControlsUtility.DoPlaySettings)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(HsPlayPrefix)));
            MethodInfo letters = AccessTools.Method(typeof(LetterStack), nameof(LetterStack.LettersOnGUI));
            h.Patch(letters, prefix: new HarmonyMethod(typeof(TestRunner), nameof(HsLettersRawPrefix)) { priority = Priority.First });
            h.Patch(letters, prefix: new HarmonyMethod(typeof(TestRunner), nameof(HsLettersPrefix)) { priority = Priority.Last });
            h.Patch(AccessTools.Method(typeof(GameConditionManager), nameof(GameConditionManager.DoConditionsUI)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(HsCondPrefix)));
            h.Patch(AccessTools.Method(typeof(TimeControls), nameof(TimeControls.DoTimeControlsGUI)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(HsTimePrefix)));
            h.Patch(AccessTools.Method(typeof(DateReadout), nameof(DateReadout.DateOnGUI)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(HsDatePrefix)));
            h.Patch(AccessTools.Method(typeof(WeatherManager), nameof(WeatherManager.DoWeatherGUI)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(HsWeatherPrefix)));
            hsNoLetters = GenCommandLine.CommandLineArgPassed("projectzhudnoletters");
            if (GenCommandLine.CommandLineArgPassed("projectzhudbackdrop"))
            {
                h.Patch(AccessTools.Method(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_BeforeMainTabs)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(HsBackdropPrefix)));
            }
            if (GenCommandLine.CommandLineArgPassed("projectzhudtrace"))
            {
                // (-projectzhudtrace: who uses a synthetic event)
                h.Patch(AccessTools.Method(typeof(Event), nameof(Event.Use)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(HsUsePrefix)));
                h.Patch(AccessTools.Method(typeof(FloorHud), "OnGUI"), prefix: new HarmonyMethod(typeof(TestRunner), nameof(HsHudGuiPrefix)));
            }
        }

        private static void HsHudGuiPrefix(Rect r)
        {
            if (hsSynthetic)
            {
                Out($"HUDTRACE the HUD gets {Event.current.type} at {Event.current.mousePosition} button {Event.current.button}, rect {R(r)}, UI {UI.screenWidth}x{UI.screenHeight}");
            }
        }

        private static readonly Color HsBackdrop = new Color32(96, 96, 96, 255);

        private static void HsBackdropPrefix()
        {
            if (Event.current.type == EventType.Repaint && !hsSynthetic)
            {
                Widgets.DrawBoxSolid(new Rect(0f, 0f, UI.screenWidth, UI.screenHeight + MainButtonDef.ButtonHeight), HsBackdrop);
            }
        }

        private static bool hsSynthetic;

        private static void HsUsePrefix(Event __instance)
        {
            if (hsSynthetic)
            {
                Out($"HUDTRACE {__instance.type} at {__instance.mousePosition} used by:\n{System.Environment.StackTrace}");
            }
        }

        private static void HsDrawPrefix()
        {
            hsDrawsNow++;
            hsUiW = UI.screenWidth;
            hsUiH = UI.screenHeight;
            hsOrigin = GUIUtility.GUIToScreenPoint(Vector2.zero);
        }

        // A point of the HUD's last layout as the pass outside its group sees it (where a synthetic event starts).
        private static Vector2 HsAt(Vector2 hudPoint) => hudPoint + hsOrigin;

        private static void HsPlayPrefix() => hsPlayNow++;

        private static bool hsNoLetters;

        private static bool HsLettersRawPrefix(float baseY)
        {
            if (hsNoLetters)
            {
                return false;
            }
            hsBaseRaw = baseY;
            return true;
        }

        private static void HsLettersPrefix(float baseY) => hsBase = baseY;

        // (on screen only: a UI mod may run the time controls off screen for their hotkeys)
        private static bool HsOnScreen(Rect r) => r.height > 0f && r.xMax > 0f && r.yMax > 0f;

        private static void HsTimePrefix(Rect timerRect)
        {
            if (HsOnScreen(timerRect))
            {
                hsTimeNow = timerRect;
            }
        }

        private static void HsDatePrefix(Rect dateRect)
        {
            if (HsOnScreen(dateRect))
            {
                hsDateNow = dateRect;
            }
        }

        private static void HsWeatherPrefix(Rect rect)
        {
            if (HsOnScreen(rect))
            {
                hsWeatherNow = rect;
            }
        }

        private static void HsCondPrefix(Rect rect)
        {
            if (HsOnScreen(rect))
            {
                hsCondNow = rect;
            }
        }

        // (TestRunner.OnGUI, the end of every play UI pass) the counts of a finished Repaint pass
        internal static void HsGui()
        {
            if (mode != "hudslot")
            {
                return;
            }
            if (Event.current.type == EventType.Repaint)
            {
                hsDraws = hsDrawsNow;
                hsPlay = hsPlayNow;
                hsFallback = hsFallbackDrew != null && (bool)hsFallbackDrew.GetValue(null);
                if (hsDraws == 0)
                {
                    hsUiW = UI.screenWidth;
                    hsUiH = UI.screenHeight;
                    hsOrigin = Vector2.zero;
                }
            }
            if (Event.current.type == EventType.Repaint)
            {
                hsCond = hsCondNow;
                hsTime = hsTimeNow;
                hsDate = hsDateNow;
                hsWeather = hsWeatherNow;
            }
            hsDrawsNow = hsPlayNow = 0;
            hsCondNow = hsTimeNow = hsDateNow = hsWeatherNow = Rect.zero;
        }

        // The engine's GUI backend writes the real pointer into the current event at every group and matrix change
        // (ImGuiGuiBackend.UpdateEventMouse), so a synthetic event that passes a group needs the backend's pointer at
        // its position too. Test only, by reflection; returns what puts the real pointer back.
        private static System.Action HsPointer(Vector2 guiPos)
        {
            object backend = UnityStubs.Gui.GuiBackend.Current;
            FieldInfo f = backend == null ? null : AccessTools.Field(backend.GetType(), "physMouse");
            MethodInfo toDevice = backend == null ? null : AccessTools.Method(backend.GetType(), "GuiToDevicePoint");
            if (f == null || toDevice == null)
            {
                if (!hsNoPointer)
                {
                    hsNoPointer = true;
                    Info("no backend pointer to set (synthetic events keep their own position only outside groups; the real pointer is not kept out)");
                }
                return () => { };
            }
            object old = f.GetValue(backend);
            Vector2 dev = (Vector2)toDevice.Invoke(backend, new object[] { guiPos });
            f.SetValue(backend, System.Activator.CreateInstance(f.FieldType, dev.x, dev.y));
            return () => f.SetValue(backend, old);
        }

        private static bool hsNoPointer;

        // (before every play UI pass) the pointer at the screen's centre, over the map; the backend reads the real one
        // again next frame, so nothing is put back
        private static void HsPinPointerPrefix()
        {
            Vector2 at = new Vector2(UI.screenWidth / 2f, UI.screenHeight / 2f);
            HsPointer(at);
            if (Event.current != null)
            {
                Event.current.mousePosition = at;
            }
        }

        // A synthetic event through the game's controls column (as TestQoL1.OEvent) or through the main buttons, kept
        // out of the pass counts.
        private static void HsEvent(EventType type, Vector2 pos, bool alt = false, bool mainButtons = false)
        {
            int d = hsDrawsNow, p = hsPlayNow;
            System.Action pointerBack = HsPointer(pos);
            Event saved = Event.current;
            Event ev = new Event { type = type, mousePosition = pos, button = 0, alt = alt, clickCount = 1 };
            if (alt)
            {
                ev.modifiers = EventModifiers.Alt;
            }
            Event.current = ev;
            hsSynthetic = true;
            if (GenCommandLine.CommandLineArgPassed("projectzhudtrace"))
            {
                Out($"HUDTRACE sending {type} at {pos} through {(mainButtons ? "the main buttons" : "the controls column")}, UI {UI.screenWidth}x{UI.screenHeight}");
            }
            try
            {
                if (mainButtons)
                {
                    Find.MainButtonsRoot.MainButtonsOnGUI();
                }
                else
                {
                    Find.MapUI.globalControls.GlobalControlsOnGUI();
                }
            }
            finally
            {
                hsSynthetic = false;
                pointerBack();
                Event.current = saved;
                hsDrawsNow = d;
                hsPlayNow = p;
            }
        }

        private static List<Alert> HsAlerts() => Traverse.Create(Find.Alerts).Field("activeAlerts").GetValue<List<Alert>>() ?? new List<Alert>();

        private static float HsLift => float.IsNaN(hsBaseRaw) ? 0f : hsBaseRaw - hsBase;

        private static bool HsShown => hsDraws > 0;

        private static void HsFacts(string what)
        {
            Info($"{what}: HUD {(HsShown ? R(FloorHud.LastRect) : "not drawn")}, draws a pass {hsDraws}, play settings runs a pass {hsPlay}, fallback {hsFallback}, moved {ProjectZMod.Settings.floorHudMoved}; "
                + $"letters {Find.LetterStack.LettersListForReading.Count}, base {hsBaseRaw:0.#} -> {hsBase:0.#} (lift {HsLift:0.#}), top {Find.LetterStack.LastTopY:0.#}; "
                + $"alerts {HsAlerts().Count}, height {Find.Alerts.AlertsHeight:0.#}; UI {hsUiW}x{hsUiH} at the HUD (origin {hsOrigin.x:0.#},{hsOrigin.y:0.#}), screen {Screen.width}x{Screen.height}, viewing floor {FloorView.Current + 1}, world {WorldRendererUtility.WorldSelected}");
        }

        // The run's expectation for a HUD in its default place.
        // Where the fallback HUD's bottom belongs: the base the letters' caller gave (before any lift), never lower
        // than right above the main buttons.
        private static float HsFallbackBottom => float.IsNaN(hsBaseRaw) ? hsUiH - MainButtonDef.ButtonHeight - 4f : Mathf.Min(hsUiH - MainButtonDef.ButtonHeight - 4f, hsBaseRaw);

        // The column's items drawn on screen in the last pass, against the HUD.
        private static void HsColumnClear(string what)
        {
            if (!HsShown)
            {
                return;
            }
            Rect r = FloorHud.LastRect;
            (string name, Rect rect)[] items = { ("time controls", hsTime), ("date", hsDate), ("weather", hsWeather), ("conditions", hsCond) };
            string on = string.Join(", ", items.Where(i => i.rect != Rect.zero).Select(i => i.name + " " + R(i.rect)));
            string over = string.Join(", ", items.Where(i => i.rect != Rect.zero && i.rect.Overlaps(r)).Select(i => i.name));
            Result($"{what}: nothing of the controls column under the HUD", over.Length == 0, $"HUD {R(r)}; on screen: {(on.Length > 0 ? on : "none")}{(over.Length > 0 ? "; OVERLAP: " + over : "")}");
        }

        private static void HsVerdict(string what)
        {
            Rect r = FloorHud.LastRect;
            switch (hsExpect)
            {
                case "slot":
                    Result($"{what}: the HUD in its slot (drawn once a pass, the play settings ran in the column, no fallback, letters not lifted)",
                        hsDraws == 1 && hsPlay >= 1 && !hsFallback && HsLift == 0f && r.yMax < hsUiH - MainButtonDef.ButtonHeight - 4f - 1f,
                        $"HUD {R(r)}, draws {hsDraws}, play settings {hsPlay}, fallback {hsFallback}, lift {HsLift:0.#}");
                    break;
                case "fallback":
                    float h = FloorHud.StackHeight(2);
                    Result($"{what}: the HUD on top of the column, its bottom at the letters' base (drawn once a pass, no play settings in the column, fallback)",
                        hsDraws == 1 && hsPlay == 0 && hsFallback && Mathf.Abs(r.yMax - HsFallbackBottom) < 0.01f && Mathf.Abs(r.xMax - (hsUiW - FloorHud.EdgeGap)) < 0.01f && Mathf.Abs(r.height - h) < 0.01f,
                        $"HUD {R(r)}, want bottom {HsFallbackBottom:0.#} (letters' base {hsBaseRaw:0.#}), right {hsUiW - FloorHud.EdgeGap:0.#}, height {h:0.#}; draws {hsDraws}, play settings {hsPlay}, fallback {hsFallback}");
                    break;
                default:
                    Result($"{what}: the HUD is missing (the reproduction)", hsDraws == 0 && hsPlay == 0, $"draws {hsDraws}, play settings {hsPlay}");
                    break;
            }
        }

        private static bool StepHudSlot(Map map)
        {
            switch (stage)
            {
                case "hs_setup":
                    DebugSettings.enableRandomMentalStates = false;
                    Find.Storyteller.storytellerComps.Clear();   // no incidents (test only)
                    FloorHud.ResetPosition();
                    StartTicks(700, "hs_ready");   // (the game shows no alerts in a colony's first 600 ticks)
                    return true;
                case "hs_ready":
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    foreach (Letter l in Find.LetterStack.LettersListForReading.ToList())
                    {
                        Find.LetterStack.RemoveLetter(l);
                    }
                    ShotAt(map, 0, map.Center, 14f);
                    fakeMouse = new Vector2(Screen.width / 2f, Screen.height / 2f);   // (the temperature readout reads the pointer's cell)
                    Info($"expect {hsExpect}; clicks {string.Join(" ", hsClicks.Select(c => $"({c.x:0},{c.y:0})"))}; levels {Levels.Count(map)}, HUD bars [{FloorHud.Slots()}]");
                    WaitRealTime(3f, "hs_plain");
                    return true;

                // ---- H1: the plain corner
                case "hs_plain":
                    HsFacts("H1 plain");
                    HsVerdict("H1 plain");
                    Info("alerts: " + string.Join(", ", HsAlerts().Select(a => a.Label)));
                    HsColumnClear("H1 plain");
                    Capture("hs_1_plain");
                    Wait(3, "hs_letters");
                    return true;

                // ---- H2: letters and alerts
                case "hs_letters":
                {
                    LetterDef[] defs = { LetterDefOf.NeutralEvent, LetterDefOf.PositiveEvent, LetterDefOf.NeutralEvent, LetterDefOf.PositiveEvent };
                    for (int i = 0; i < defs.Length; i++)
                    {
                        Find.LetterStack.ReceiveLetter("Test letter " + (i + 1), "A letter of the hudslot test.", defs[i], debugInfo: null, delayTicks: 0, playSound: false);
                    }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    WaitRealTime(2.5f, "hs_letters_check");   // (a new letter slides in for a second)
                    return true;
                }
                case "hs_letters_check":
                {
                    HsFacts("H2 letters");
                    Rect r = FloorHud.LastRect;
                    int letters = Find.LetterStack.LettersListForReading.Count, alerts = HsAlerts().Count;
                    Result("H2 fixture: 3 or more letters and an alert", letters >= 3 && alerts >= 1, $"{letters} letters, {alerts} alerts");
                    HsVerdict("H2 letters");
                    if (hsExpect == "fallback" && !hsNoLetters)
                    {
                        // the stack starts at its base and goes up to its top; the alerts end at that top
                        Result("H2 letters start above the fallback HUD: the base is lifted by the HUD's height + 4, to 4 above the HUD's top",
                            Mathf.Abs(HsLift - (r.height + 4f)) < 0.01f && Mathf.Abs(hsBase - (r.y - 4f)) < 0.01f && Find.LetterStack.LastTopY < hsBase,
                            $"base {hsBaseRaw:0.#} -> {hsBase:0.#}, HUD top {r.y:0.#}, height {r.height:0.#}; letters' top {Find.LetterStack.LastTopY:0.#}, alerts above it ({Find.Alerts.AlertsHeight:0.#} tall)");
                    }
                    Capture("hs_2_letters");
                    Wait(3, "hs_many");
                    return true;
                }
                // 25 letters: more than fit; the stack bundles the rest, counting from the base it is given
                case "hs_many":
                    for (int i = 0; i < 21; i++)
                    {
                        Find.LetterStack.ReceiveLetter("Many letter " + (i + 1), "A letter of the hudslot test.", LetterDefOf.NeutralEvent, debugInfo: null, delayTicks: 0, playSound: false);
                    }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    WaitRealTime(2.5f, "hs_many_check");
                    return true;
                case "hs_many_check":
                {
                    HsFacts("H2 many letters");
                    Rect r = FloorHud.LastRect;
                    float top = Find.LetterStack.LastTopY - Find.Alerts.AlertsHeight;
                    if (hsNoLetters)
                    {
                        Info("H2 many letters: the letter stack is off (-projectzhudnoletters)");
                    }
                    else
                    {
                        Result("H2 25 letters: the stack bundles from its (lifted) base: letters start above the HUD, and the stack with the alerts stays on screen",
                            top >= -0.01f && (hsExpect == "fallback" ? Mathf.Abs(hsBase - (r.y - 4f)) < 0.01f : HsLift == 0f),
                            $"{Find.LetterStack.LettersListForReading.Count} letters, base {hsBaseRaw:0.#} -> {hsBase:0.#}, HUD {(HsShown ? R(r) : "not drawn")}; letters' top {Find.LetterStack.LastTopY:0.#}, alerts' top {top:0.#}");
                    }
                    Capture("hs_2b_many");
                    Wait(3, "hs_many_end");
                    return true;
                }
                case "hs_many_end":
                    foreach (Letter l in Find.LetterStack.LettersListForReading.Where(l => l.Label.ToString().StartsWith("Many letter")).ToList())
                    {
                        Find.LetterStack.RemoveLetter(l);
                    }
                    Wait(20, "hs_cond");
                    return true;

                // ---- H3: a game condition's readout (recorded)
                case "hs_cond":
                    hsCondition = GameConditionMaker.MakeConditionPermanent(GameConditionDefOf.HeatWave);
                    map.gameConditionManager.RegisterCondition(hsCondition);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    WaitRealTime(2.5f, "hs_cond_check");
                    return true;
                case "hs_cond_check":
                    HsFacts("H3 condition");
                    Result("H3 fixture: the condition's readout is on screen", hsCond != Rect.zero, R(hsCond));
                    HsVerdict("H3 condition");
                    HsColumnClear("H3 condition");
                    Capture("hs_3_condition");
                    Wait(3, "hs_cond_end");
                    return true;
                case "hs_cond_end":
                    hsCondition.End();
                    map.gameConditionManager.ActiveConditions.Remove(hsCondition);
                    foreach (Letter l in Find.LetterStack.LettersListForReading.Where(l => !l.Label.ToString().StartsWith("Test letter")).ToList())
                    {
                        Find.LetterStack.RemoveLetter(l);
                    }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    hsClick = 0;
                    Wait(20, hsClicks.Count > 0 ? "hs_pop" : "hs_click");
                    return true;

                // ---- H4: popups opened from the main buttons bar
                case "hs_pop":
                {
                    Vector2 at = hsClicks[hsClick];
                    guiAction = () =>
                    {
                        HsEvent(EventType.MouseDown, at, mainButtons: true);
                        HsEvent(EventType.MouseUp, at, mainButtons: true);
                    };
                    WaitRealTime(1.5f, "hs_pop_check");
                    return true;
                }
                case "hs_pop_check":
                {
                    HsFacts($"H4 popup {hsClick + 1} (click at {hsClicks[hsClick]})");
                    Rect r = FloorHud.LastRect;
                    foreach (Window w in Find.WindowStack.Windows)
                    {
                        Info($"H4 popup {hsClick + 1}: window {w.GetType().Name} id {w.ID} layer {w.layer} {R(w.windowRect)}{(HsShown && w.windowRect.Overlaps(r) ? " COVERS the HUD " + R(r) : "")}");
                    }
                    Capture($"hs_4_popup{hsClick + 1}");
                    Wait(3, "hs_pop_close");
                    return true;
                }
                case "hs_pop_close":
                {
                    Vector2 at = hsClicks[hsClick];
                    guiAction = () =>
                    {
                        HsEvent(EventType.MouseDown, at, mainButtons: true);
                        HsEvent(EventType.MouseUp, at, mainButtons: true);
                    };
                    hsClick++;
                    WaitRealTime(1f, hsClick < hsClicks.Count ? "hs_pop" : "hs_click");
                    return true;
                }

                // ---- H5: a click on a HUD bar
                case "hs_click":
                    if (hsExpect == "none")
                    {
                        stage = "hs_world";
                        return true;
                    }
                    guiAction = () =>
                    {
                        Vector2 at = HsAt(RowCenter(1));
                        HsEvent(EventType.MouseDown, at);
                        Result("H5 a click on the HUD's level-1 bar views that level", FloorView.Current == 1, $"click at {at}, viewing floor {FloorView.Current + 1}");
                    };
                    WaitRealTime(0.6f, "hs_click_shot");
                    return true;
                case "hs_click_shot":
                    HsFacts("H5 click");
                    HsVerdict("H5 click");
                    Capture("hs_5_floor2");
                    Wait(3, "hs_time");
                    return true;
                // the game's time controls, when they are on screen: the normal-speed button (the second) still takes
                // its click (no game ticks in between: read and paused again in the same GUI pass)
                case "hs_time":
                    if (hsTime == Rect.zero)
                    {
                        Info("H5 time controls: none on screen");
                        stage = "hs_world";
                        return true;
                    }
                    guiAction = () =>
                    {
                        Vector2 at = HsAt(new Vector2(hsTime.x + TimeControls.TimeButSize.x * 1.5f, hsTime.y + TimeControls.TimeButSize.y / 2f));
                        HsEvent(EventType.MouseDown, at);
                        HsEvent(EventType.MouseUp, at);
                        TimeSpeed got = Find.TickManager.CurTimeSpeed;
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                        Result("H5 a click on the game's time controls (normal speed) sets the speed", got == TimeSpeed.Normal, $"click at {at}, speed {got}; time controls {R(hsTime)}, HUD {R(FloorHud.LastRect)}");
                    };
                    Wait(5, "hs_world");
                    return true;

                // ---- H6: the world view
                case "hs_world":
                    FloorView.Set(0);
                    CameraJumper.TryShowWorld();
                    WaitRealTime(2f, "hs_world_check");
                    return true;
                case "hs_world_check":
                    HsFacts("H6 world");
                    Result("H6 world view: no HUD, letters not lifted", WorldRendererUtility.WorldSelected && hsDraws == 0 && HsLift == 0f, $"world {WorldRendererUtility.WorldSelected}, draws {hsDraws}, lift {HsLift:0.#}");
                    Capture("hs_6_world");
                    Wait(3, "hs_world_end");
                    return true;
                case "hs_world_end":
                    CameraJumper.TryHideWorld();
                    WaitRealTime(2f, hsExpect == "none" ? "hs_end" : "hs_back");
                    return true;
                case "hs_back":
                    HsFacts("H6 back on the map");
                    HsVerdict("H6 back on the map");
                    stage = "hs_drag";
                    return true;

                // ---- H7: Alt + drag
                case "hs_drag":
                    guiAction = () =>
                    {
                        hsBefore = FloorHud.LastRect;
                        Vector2 at = HsAt(RowCenter(0));
                        HsEvent(EventType.MouseDown, at, alt: true);
                        HsEvent(EventType.MouseDrag, at + new Vector2(-300f, -200f), alt: true);
                        HsEvent(EventType.MouseDrag, at + new Vector2(-500f, -300f), alt: true);
                        HsEvent(EventType.MouseUp, at + new Vector2(-500f, -300f), alt: true);
                    };
                    WaitRealTime(1f, "hs_drag_check");
                    return true;
                case "hs_drag_check":
                {
                    HsFacts("H7 moved");
                    Rect r = FloorHud.LastRect;
                    ProjectZSettings s = ProjectZMod.Settings;
                    Result("H7 Alt + drag moves the HUD (500 left, 300 up); it stays there, drawn once a pass; letters not lifted",
                        s.floorHudMoved && Mathf.Abs(r.x - (hsBefore.x - 500f)) < 0.5f && Mathf.Abs(r.y - (hsBefore.y - 300f)) < 0.5f && hsDraws == 1 && HsLift == 0f
                        && Mathf.Abs(s.floorHudRight - (hsUiW - r.xMax)) < 0.5f && Mathf.Abs(s.floorHudBottom - (hsUiH - r.yMax)) < 0.5f,
                        $"{R(hsBefore)} -> {R(r)}; saved right {s.floorHudRight:0.#}, bottom {s.floorHudBottom:0.#}; draws {hsDraws}, lift {HsLift:0.#}, fallback {hsFallback}");
                    Capture("hs_7_moved");
                    Wait(3, "hs_end");
                    return true;
                }
                case "hs_end":
                    FloorHud.ResetPosition();
                    foreach (Letter l in Find.LetterStack.LettersListForReading.ToList())
                    {
                        Find.LetterStack.RemoveLetter(l);
                    }
                    fakeMouse = null;
                    Finish();
                    return true;
            }
            return false;
        }
    }
}
