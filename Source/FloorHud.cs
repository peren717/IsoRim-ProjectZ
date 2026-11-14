using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace ProjectZ
{
    // The floor HUD (user decisions "Floor HUD", 2026-10-16; mockup model-review\projectz\floor_hud, variant B): one
    // bar per shown level stacked bottom-up (top = highest), no numbers. The viewed floor is lighter with a bright border
    // and sticks out toward the map; floors above it (hidden) are outlines only; the ground bar is wider and darker. A
    // white dot = a colonist on that floor, a red dot = danger there (an active hostile pawn or a fire, not under fog).
    // Only the levels in use plus one above (and below) show (ShownLevels, user decision 2026-10-17); a bar coming or
    // going slides in or out of the stack (it grows or shrinks its slot), so the column above moves smoothly.
    // Switching slides the bars (150 ms, ease-out, real time so it also plays while paused). Click a bar to view that
    // floor; Alt + drag moves the HUD anywhere (saved in the mod settings, "reset" puts it back).
    // By default it takes its own slot in the game's bottom-right controls column, right above the play settings
    // (GlobalControlsUtility.DoPlaySettings moves the column's base up), so the time controls, readouts and alerts move
    // up with it. Only on maps with floors. Sizes are UI px, so it scales with the UI scale like the rest of the column.
    // When nothing runs the play settings in the map's controls column (a UI mod that draws the column its own way), the
    // HUD has no slot: it then draws itself on top of that column, where the column's drawer starts the letters, and
    // lifts the letters and alerts over it (DoFallback, BeforeLetters).
    public static class FloorHud
    {
        public const float BarW = 60f, BarH = 12f, Gap = 3f, GroundExtra = 10f, Shift = 6f, EdgeGap = 3f;
        private const float DotSize = 8f;   // 6 px dot in a 1 px dark ring
        private const float SlideTime = 0.15f, CacheTime = 0.25f;
        // the gap the HUD keeps to what is under it in the controls column; without a slot, also the letters' gap to it
        private const float ColumnGap = 4f;

        private static readonly Color Outline = new Color32(14, 11, 7, 255);
        private static readonly Color HiddenOutline = new Color32(14, 11, 7, 204);
        private static readonly Color HiddenRing = new Color32(150, 120, 72, 255);
        private static readonly Color Fill = new Color32(106, 81, 46, 255);
        private static readonly Color Light = new Color32(127, 99, 53, 255);
        private static readonly Color Dark = new Color32(79, 61, 35, 255);
        private static readonly Color GroundFill = new Color32(84, 64, 37, 255);
        private static readonly Color GroundLight = new Color32(100, 78, 44, 255);
        private static readonly Color GroundDark = new Color32(62, 47, 27, 255);
        private static readonly Color ViewFill = new Color32(158, 124, 74, 255);
        private static readonly Color ViewLight = new Color32(182, 146, 90, 255);
        private static readonly Color ViewDark = new Color32(128, 99, 58, 255);
        private static readonly Color ViewBorder = new Color32(240, 232, 212, 255);
        private static readonly Color ColonistDot = new Color32(238, 238, 238, 255);
        private static readonly Color DangerDot = new Color32(224, 44, 32, 255);

        // the view slide (levels)
        private static Map animMap;
        private static int viewShown, viewFrom;
        private static float switchedAt = -1f;

        // the slots, per level (index = level - the map's lowest level): how much of its slot a bar takes, 0 (not shown)
        // to 1, eased from `slotFrom` toward `slotTo` since `slotAt` (real time; -1 = settled)
        private static Map slotMap;
        private static int slotMin, slotCount;
        private static float[] slotFrom = new float[0], slotTo = new float[0], slotAt = new float[0];
        // the last layout: each bar's top (UI px, NaN = not drawn)
        private static float[] barTop = new float[0];

        // dots, per floor (index = level - the map's lowest level), refreshed a few times a second
        private static Map cacheMap;
        private static int cacheLowest;
        private static float cacheAt = -100f;
        private static bool[] colonist = new bool[0], danger = new bool[0];

        // Alt + drag
        private static bool dragging;
        private static Vector2 grab;

        // One play UI pass (BeginPass): the slot drew the HUD; the fallback drew it, and how far the letters have to
        // move up for it (0 where the player put it); the letters were started on the map view, at lettersBase (as their
        // caller gave it). letterLift: the lift of the pass before.
        private static bool slotServed, fallbackDrew, lettersSeen;
        private static float fallbackLift, letterLift, lettersBase;

        // Test hook: the last rect the HUD was drawn in (UI px).
        public static Rect LastRect;

        // Test hook: the fallback drew the HUD in this pass.
        public static bool FallbackDrew => fallbackDrew;

        public static float Width => BarW + GroundExtra + Shift;

        public static float StackHeight(int floors) => floors * BarH + (floors - 1) * Gap;

        private static ProjectZSettings S => ProjectZMod.Settings;

        private static float Ease(float t) => 1f - (1f - t) * (1f - t);

        // The row (bar plus half the gaps, full HUD width) of level L in the last layout: the click and tooltip target
        // (Rect.zero when that level has no bar).
        public static Rect RowRectOf(int L)
        {
            int i = L - slotMin;
            if (i < 0 || i >= barTop.Length || float.IsNaN(barTop[i]))
            {
                return Rect.zero;
            }
            return new Rect(LastRect.x, barTop[i] - Gap / 2f, LastRect.width, BarH + Gap);
        }

        // Test hook: the levels with a bar now (slot > 0) and fully in place.
        public static string Slots()
        {
            string s = "";
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < slotCount; i++)
            {
                float v = SlotValue(i, now);
                if (v > 0f)
                {
                    s += (s.Length > 0 ? "," : "") + (slotMin + i) + (v < 1f ? $"({v:0.00})" : "");
                }
            }
            return s;
        }

        // Where the HUD shows at all: playing, on a map, not in the world view (floors and the hide setting: Place).
        private static bool OnMapView(bool worldView) => !worldView && Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null;

        // The start of a play UI pass (FloorKeys_Patch, on UIRoot_Play.UIRootOnGUI). Layout passes don't count: the
        // game draws the controls column in none of them, so the state of the last drawing pass stays.
        public static void BeginPass()
        {
            if (Event.current.type == EventType.Layout)
            {
                return;
            }
            letterLift = fallbackLift;
            fallbackLift = 0f;
            slotServed = fallbackDrew = lettersSeen = false;
        }

        // From the play settings (GlobalControlsUtility.DoPlaySettings): the HUD's own slot in the controls column.
        public static void DoSlot(bool worldView, ref float curBaseY)
        {
            // (fallbackDrew: something ran the play settings after the column in this pass; the HUD is drawn already)
            if (!fallbackDrew && OnMapView(worldView) && Place(ref curBaseY))
            {
                slotServed = true;
            }
        }

        // After the map's controls column (GlobalControls.GlobalControlsOnGUI): nothing ran the play settings in this
        // pass (a UI mod draws the column its own way), so the HUD has no slot. It goes on top of whatever column was
        // drawn: its bottom where that column's drawer started the letters in this pass (BeforeLetters), so it covers
        // nothing of the column, whatever is in it. Never lower than its slot's place in an empty column, right above
        // the main buttons; that place too when nobody started the letters before this (something else draws them).
        // Not in Layout, as the game's column.
        public static void DoFallback()
        {
            if (slotServed || Event.current.type == EventType.Layout || !OnMapView(WorldRendererUtility.WorldSelected))
            {
                return;
            }
            float bottom = UI.screenHeight - MainButtonDef.ButtonHeight - ColumnGap;
            if (lettersSeen)
            {
                bottom = Mathf.Min(bottom, lettersBase);
            }
            float y = bottom + ColumnGap;   // (Place keeps the gap under the HUD)
            if (Place(ref y))
            {
                fallbackDrew = true;
                // the HUD's height plus the gap; 0 where the player put it (Place then leaves y alone)
                fallbackLift = bottom + ColumnGap - y;
            }
        }

        // Before the letter stack is drawn (LetterStack.LettersOnGUI; the alerts sit on the stack's top, and the stack
        // counts how many letters fit from the same base): on the map view, the base its caller gives is kept for this
        // pass's fallback HUD, and the letters start above the fallback HUD of the last pass. That pass, because a
        // column draws its letters before the fallback runs: in the first pass of a fallback HUD the letters are not
        // lifted yet. No lift with the HUD in its slot, moved by the player, or not shown.
        public static void BeforeLetters(ref float baseY)
        {
            if (!OnMapView(WorldRendererUtility.WorldSelected))
            {
                return;
            }
            lettersSeen = true;
            lettersBase = baseY;
            if (letterLift > 0f)
            {
                baseY -= letterLift;
            }
        }

        // Draws the HUD on top of a controls column (moving the column's base up), or where the player put it.
        // False: no HUD here (a map without floors, or hidden).
        private static bool Place(ref float curBaseY)
        {
            Map map = Find.CurrentMap;
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || S == null || S.hideFloorHud)
            {
                dragging = false;
                return false;
            }
            float now = Time.realtimeSinceStartup;
            UpdateSlots(map, now);
            float h = -Gap;
            for (int i = 0; i < slotCount; i++)
            {
                h += SlotValue(i, now) * (BarH + Gap);
            }
            h = Mathf.Max(h, BarH);
            Rect r;
            if (S.floorHudMoved)
            {
                r = MovedRect(h);
            }
            else
            {
                curBaseY -= ColumnGap;
                r = new Rect(UI.screenWidth - EdgeGap - Width, curBaseY - h, Width, h);
                curBaseY -= h;
            }
            OnGUI(r, map, now);
            return true;
        }

        // The slots follow the shown levels (ShownLevels.Range); a new map (or a new level layout) starts settled.
        private static void UpdateSlots(Map map, float now)
        {
            int min = Levels.Min(map), n = Levels.Count(map);
            ShownLevels.Range(map, out int lo, out int hi);
            bool reset = slotMap != map || slotMin != min || slotCount != n;
            if (reset)
            {
                slotMap = map;
                slotMin = min;
                slotCount = n;
                slotFrom = new float[n];
                slotTo = new float[n];
                slotAt = new float[n];
                barTop = new float[n];
            }
            for (int i = 0; i < n; i++)
            {
                float want = min + i >= lo && min + i <= hi ? 1f : 0f;
                if (reset)
                {
                    slotFrom[i] = slotTo[i] = want;
                    slotAt[i] = -1f;
                }
                else if (want != slotTo[i])
                {
                    slotFrom[i] = SlotValue(i, now);
                    slotTo[i] = want;
                    slotAt[i] = now;
                }
            }
        }

        private static float SlotValue(int i, float now)
        {
            if (i < 0 || i >= slotCount)
            {
                return 0f;
            }
            if (slotAt[i] < 0f || now < slotAt[i])
            {
                return slotTo[i];
            }
            float t = (now - slotAt[i]) / SlideTime;
            if (t >= 1f)
            {
                slotAt[i] = -1f;
                return slotTo[i];
            }
            return Mathf.Lerp(slotFrom[i], slotTo[i], Ease(t));
        }

        private static Rect MovedRect(float h)
        {
            float right = Mathf.Clamp(S.floorHudRight, 0f, Mathf.Max(0f, UI.screenWidth - Width));
            float bottom = Mathf.Clamp(S.floorHudBottom, 0f, Mathf.Max(0f, UI.screenHeight - h));
            return new Rect(UI.screenWidth - right - Width, UI.screenHeight - bottom - h, Width, h);
        }

        private static void OnGUI(Rect r, Map map, float now)
        {
            LastRect = r;
            // the layout: bars bottom-up, each taking its slot's share of a bar plus a gap (a bar coming in rises out
            // from under the one below it; the stack is clipped to the HUD rect)
            float cum = 0f;
            for (int i = 0; i < slotCount; i++)
            {
                float v = SlotValue(i, now);
                cum += v;
                barTop[i] = v > 0f ? r.yMax - cum * (BarH + Gap) + Gap : float.NaN;
            }
            Event e = Event.current;
            if (dragging)
            {
                if (e.type == EventType.MouseDrag)
                {
                    Vector2 corner = e.mousePosition - grab;
                    S.floorHudRight = Mathf.Clamp(UI.screenWidth - corner.x, 0f, Mathf.Max(0f, UI.screenWidth - Width));
                    S.floorHudBottom = Mathf.Clamp(UI.screenHeight - corner.y, 0f, Mathf.Max(0f, UI.screenHeight - r.height));
                    e.Use();
                }
                else if (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp || (e.type == EventType.Repaint && !Input.GetMouseButton(0) && !Dev.TestRunner.Active))
                {
                    EndDrag();
                    if (e.type == EventType.MouseUp)
                    {
                        e.Use();
                    }
                }
            }
            int cur = FloorView.Current;
            // (only bars of shown levels take clicks and tooltips, not one sliding out)
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
            {
                for (int i = 0; i < slotCount; i++)
                {
                    int k = slotMin + i;
                    if (slotTo[i] > 0f && RowRectOf(k).Contains(e.mousePosition))
                    {
                        if (e.alt)
                        {
                            dragging = true;
                            grab = e.mousePosition - new Vector2(r.xMax, r.yMax);
                            S.floorHudMoved = true;
                            S.floorHudRight = UI.screenWidth - r.xMax;
                            S.floorHudBottom = UI.screenHeight - r.yMax;
                        }
                        else
                        {
                            if (k != cur)
                            {
                                FloorView.Set(k);
                            }
                            ScrollHintOnce();
                        }
                        e.Use();
                        break;
                    }
                }
            }
            for (int i = 0; i < slotCount; i++)
            {
                if (slotTo[i] <= 0f)
                {
                    continue;
                }
                Rect row = RowRectOf(slotMin + i);
                if (Mouse.IsOver(row))
                {
                    MouseoverSounds.DoRegion(row);
                    int floor = slotMin + i;
                    TooltipHandler.TipRegion(row, new TipSignal(() => Tip(floor), 0x5A10F + i));
                }
            }
            if (e.type == EventType.Repaint)
            {
                Draw(r, map, FloorView.Current, now);
            }
        }

        private static string Tip(int k) => "PZ_FloorHudTip".Translate(FloorView.Label(k), FloorView.KeyName(PZKeyDefOf.PZ_FloorUp),
            FloorView.KeyName(PZKeyDefOf.PZ_FloorDown), FloorView.KeyName(PZKeyDefOf.PZ_FloorScrollHold));

        // The first click on a floor bar ever (per install, kept in the mod settings): one message about Shift + wheel.
        private static void ScrollHintOnce()
        {
            if (S.floorScrollHintShown)
            {
                return;
            }
            S.floorScrollHintShown = true;
            LoadedModManager.GetMod<ProjectZMod>()?.WriteSettings();
            Messages.Message("PZ_FloorScrollHint".Translate(), MessageTypeDefOf.NeutralEvent, historical: false);
        }

        private static void EndDrag()
        {
            dragging = false;
            if (!Dev.TestRunner.Active)
            {
                LoadedModManager.GetMod<ProjectZMod>()?.WriteSettings();
            }
        }

        // Test hook: the settings button.
        public static void ResetPosition()
        {
            dragging = false;
            S.floorHudMoved = false;
        }

        // (cur: the viewed level)
        private static void Draw(Rect r, Map map, int cur, float now)
        {
            if (animMap != map)
            {
                animMap = map;
                viewShown = viewFrom = cur;
                switchedAt = -1f;
            }
            else if (cur != viewShown)
            {
                viewFrom = viewShown;
                viewShown = cur;
                switchedAt = now;
            }
            float t = switchedAt < 0f ? 1f : Mathf.Clamp01((now - switchedAt) / SlideTime);
            float ease = Ease(t);
            Refresh(map, Levels.Count(map), now);
            Widgets.BeginGroup(r);
            try
            {
                // top down: a bar rising into its slot comes out from under the one below it
                for (int i = slotCount - 1; i >= 0; i--)
                {
                    if (float.IsNaN(barTop[i]))
                    {
                        continue;
                    }
                    int k = slotMin + i;
                    float alpha = SlotValue(i, now);
                    float v = Mathf.Lerp(k == viewFrom ? 1f : 0f, k == viewShown ? 1f : 0f, ease);
                    float f = Mathf.Lerp(k <= viewFrom ? 1f : 0f, k <= viewShown ? 1f : 0f, ease);
                    float w = BarW + (k == 0 ? GroundExtra : 0f);
                    float y = barTop[i] - r.y;
                    Rect bar = new Rect(r.width - v * Shift - w, y, w, BarH);
                    DrawBar(bar, k == 0, v, f, alpha);
                    if (!dragging && slotTo[i] > 0f && Mouse.IsOver(new Rect(0f, y - Gap / 2f, r.width, BarH + Gap)))
                    {
                        Widgets.DrawHighlight(bar);
                    }
                    int d = k - cacheLowest;
                    if (d < 0 || d >= colonist.Length)
                    {
                        continue;
                    }
                    float dx = bar.xMax - 3f - DotSize;
                    if (colonist[d])
                    {
                        DrawDot(new Rect(dx, y + 2f, DotSize, DotSize), Fade(ColonistDot, alpha));
                        dx -= 3f + DotSize;
                    }
                    if (danger[d])
                    {
                        DrawDot(new Rect(dx, y + 2f, DotSize, DotSize), Fade(DangerDot, alpha));
                    }
                }
            }
            finally
            {
                Widgets.EndGroup();
            }
        }

        // v: how much it is the viewed floor (0..1, mid-slide in between); f: how much it is filled (0 = above the view);
        // a: its opacity (a bar sliding in or out).
        private static void DrawBar(Rect bar, bool ground, float v, float f, float a)
        {
            Color fill = Color.Lerp(ground ? GroundFill : Fill, ViewFill, v);
            Color light = Color.Lerp(ground ? GroundLight : Light, ViewLight, v);
            Color dark = Color.Lerp(ground ? GroundDark : Dark, ViewDark, v);
            Color border = Fade(Color.Lerp(Color.Lerp(HiddenOutline, Outline, f), ViewBorder, v), a);
            Rect inner = bar.ContractedBy(1f);
            if (f >= 1f && a >= 1f)
            {
                Widgets.DrawBoxSolid(bar, border);
            }
            else
            {
                Frame(bar, border);
                Frame(inner, Fade(HiddenRing, (1f - f) * a));
            }
            if (f > 0f)
            {
                Widgets.DrawBoxSolid(inner, Fade(fill, f * a));
                Widgets.DrawBoxSolid(new Rect(inner.x, inner.y, inner.width, 1f), Fade(light, f * a));
                Widgets.DrawBoxSolid(new Rect(inner.xMax - 1f, inner.y + 1f, 1f, inner.height - 1f), Fade(light, f * a));
                Widgets.DrawBoxSolid(new Rect(inner.x, inner.yMax - 1f, inner.width - 1f, 1f), Fade(dark, f * a));
                Widgets.DrawBoxSolid(new Rect(inner.x, inner.y + 1f, 1f, inner.height - 2f), Fade(dark, f * a));
            }
        }

        private static void DrawDot(Rect r, Color c)
        {
            Widgets.DrawBoxSolid(r, Fade(Outline, c.a));
            Widgets.DrawBoxSolid(r.ContractedBy(1f), c);
        }

        private static void Frame(Rect r, Color c)
        {
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 1f), c);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), c);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y + 1f, 1f, r.height - 2f), c);
            Widgets.DrawBoxSolid(new Rect(r.xMax - 1f, r.y + 1f, 1f, r.height - 2f), c);
        }

        private static Color Fade(Color c, float a) => new Color(c.r, c.g, c.b, c.a * a);

        // Colonists and danger per floor (cell level; the void rows between floors count for none). floors = the
        // number of levels, indexed from the lowest.
        public static void Refresh(Map map, int floors, float now, bool force = false)
        {
            int lowest = Levels.Min(map);
            if (!force && cacheMap == map && colonist.Length == floors && cacheLowest == lowest && now - cacheAt < CacheTime && now >= cacheAt)
            {
                return;
            }
            cacheMap = map;
            cacheAt = now;
            cacheLowest = lowest;
            if (colonist.Length != floors)
            {
                colonist = new bool[floors];
                danger = new bool[floors];
            }
            for (int k = 0; k < floors; k++)
            {
                colonist[k] = danger[k] = false;
            }
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                int k = Row(p.Position, map, lowest);
                if (k >= 0 && k < floors)
                {
                    colonist[k] = true;
                }
            }
            foreach (IAttackTarget t in map.attackTargetsCache.TargetsHostileToColony)
            {
                if (t.Thing is Pawn p && p.Spawned && GenHostility.IsActiveThreatToPlayer(t))
                {
                    int k = Row(p.Position, map, lowest);
                    if (k >= 0 && k < floors)
                    {
                        danger[k] = true;
                    }
                }
            }
            foreach (Thing fire in map.listerThings.ThingsOfDef(ThingDefOf.Fire))
            {
                if (fire.Spawned && !fire.Position.Fogged(map))
                {
                    int k = Row(fire.Position, map, lowest);
                    if (k >= 0 && k < floors)
                    {
                        danger[k] = true;
                    }
                }
            }
        }

        // A cell's row index (level - lowest); -1 for a void row.
        private static int Row(IntVec3 c, Map map, int lowest)
        {
            int L = Levels.LevelOf(c, map);
            return L == Levels.None ? -1 : L - lowest;
        }

        // Test hooks (k = a level).
        public static bool ColonistOn(int k) => k - cacheLowest >= 0 && k - cacheLowest < colonist.Length && colonist[k - cacheLowest];
        public static bool DangerOn(int k) => k - cacheLowest >= 0 && k - cacheLowest < danger.Length && danger[k - cacheLowest];
    }

    // The HUD's slot: right above the play settings (the icon rows at the bottom right), below the time controls.
    [HarmonyPatch(typeof(GlobalControlsUtility), nameof(GlobalControlsUtility.DoPlaySettings))]
    public static class GlobalControls_FloorHud_Patch
    {
        public static void Postfix(bool worldView, ref float curBaseY)
        {
            FloorHud.DoSlot(worldView, ref curBaseY);
        }
    }

    // The HUD without its slot: when the play settings didn't run in the map's controls column (a postfix also runs
    // when another mod's prefix draws the column itself and skips the game's).
    [HarmonyPatch(typeof(GlobalControls), nameof(GlobalControls.GlobalControlsOnGUI))]
    public static class GlobalControls_FloorHudFallback_Patch
    {
        public static void Postfix()
        {
            FloorHud.DoFallback();
        }
    }

    // Letters (and the alerts on top of them) start above the fallback HUD, whoever draws them; where they would have
    // started is the fallback HUD's place.
    [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.LettersOnGUI))]
    public static class LetterStack_FloorHudFallback_Patch
    {
        public static void Prefix(ref float baseY)
        {
            FloorHud.BeforeLetters(ref baseY);
        }
    }
}
