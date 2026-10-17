using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace ProjectZ
{
    // The floor HUD (user decisions "Floor HUD", 2026-10-16; mockup model-review\projectz\floor_hud, variant B): one
    // bar per floor stacked bottom-up (top = highest), no numbers. The viewed floor is lighter with a bright border and
    // sticks out toward the map; floors above it (hidden) are outlines only; the ground bar is wider and darker. A white
    // dot = a colonist on that floor, a red dot = danger there (an active hostile pawn or a fire, not under fog).
    // Switching slides the bars (150 ms, ease-out, real time so it also plays while paused). Click a bar to view that
    // floor; Alt + drag moves the HUD anywhere (saved in the mod settings, "reset" puts it back).
    // By default it takes its own slot in the game's bottom-right controls column, right above the play settings
    // (GlobalControlsUtility.DoPlaySettings moves the column's base up), so the time controls, readouts and alerts move
    // up with it. Only on maps with floors. Sizes are UI px, so it scales with the UI scale like the rest of the column.
    public static class FloorHud
    {
        public const float BarW = 60f, BarH = 12f, Gap = 3f, GroundExtra = 10f, Shift = 6f, EdgeGap = 3f;
        private const float DotSize = 8f;   // 6 px dot in a 1 px dark ring
        private const float SlideTime = 0.15f, CacheTime = 0.25f;

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

        // slide
        private static Map animMap;
        private static int animFloors, shown, from;
        private static float switchedAt = -1f;

        // dots, per floor (index = level - the map's lowest level), refreshed a few times a second
        private static Map cacheMap;
        private static int cacheLowest;
        private static float cacheAt = -100f;
        private static bool[] colonist = new bool[0], danger = new bool[0];

        // Alt + drag
        private static bool dragging;
        private static Vector2 grab;

        // Test hook: the last rect the HUD was drawn in (UI px).
        public static Rect LastRect;

        public static float Width => BarW + GroundExtra + Shift;

        public static float StackHeight(int floors) => floors * BarH + (floors - 1) * Gap;

        private static ProjectZSettings S => ProjectZMod.Settings;

        // The row (bar plus half the gaps, full HUD width) of row index k (level - the lowest level, 0 = bottom bar) in
        // the HUD rect r: the click and tooltip target.
        public static Rect RowRect(Rect r, int floors, int k)
        {
            int i = floors - 1 - k;
            return new Rect(r.x, r.y + i * (BarH + Gap) - Gap / 2f, r.width, BarH + Gap);
        }

        // From GlobalControls: draw in our own slot (moving the column base up), or where the player put it.
        public static void DoSlot(ref float curBaseY)
        {
            Map map = Find.CurrentMap;
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || S == null || S.hideFloorHud)
            {
                dragging = false;
                return;
            }
            float h = StackHeight(Levels.Count(map));
            Rect r;
            if (S.floorHudMoved)
            {
                r = MovedRect(h);
            }
            else
            {
                curBaseY -= 4f;
                r = new Rect(UI.screenWidth - EdgeGap - Width, curBaseY - h, Width, h);
                curBaseY -= h;
            }
            OnGUI(r, map, Levels.Count(map));
        }

        private static Rect MovedRect(float h)
        {
            float right = Mathf.Clamp(S.floorHudRight, 0f, Mathf.Max(0f, UI.screenWidth - Width));
            float bottom = Mathf.Clamp(S.floorHudBottom, 0f, Mathf.Max(0f, UI.screenHeight - h));
            return new Rect(UI.screenWidth - right - Width, UI.screenHeight - bottom - h, Width, h);
        }

        private static void OnGUI(Rect r, Map map, int floors)
        {
            LastRect = r;
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
            // (rows by index i, level = lowest + i)
            int lowest = Levels.Min(map);
            int cur = FloorView.Current;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
            {
                for (int i = 0; i < floors; i++)
                {
                    int k = lowest + i;
                    if (RowRect(r, floors, i).Contains(e.mousePosition))
                    {
                        if (e.alt)
                        {
                            dragging = true;
                            grab = e.mousePosition - new Vector2(r.xMax, r.yMax);
                            S.floorHudMoved = true;
                            S.floorHudRight = UI.screenWidth - r.xMax;
                            S.floorHudBottom = UI.screenHeight - r.yMax;
                        }
                        else if (k != cur)
                        {
                            FloorView.Set(k);
                        }
                        e.Use();
                        break;
                    }
                }
            }
            for (int i = 0; i < floors; i++)
            {
                Rect row = RowRect(r, floors, i);
                if (Mouse.IsOver(row))
                {
                    MouseoverSounds.DoRegion(row);
                    int floor = lowest + i;
                    TooltipHandler.TipRegion(row, new TipSignal(() => Tip(floor), 0x5A10F + i));
                }
            }
            if (e.type == EventType.Repaint)
            {
                Draw(r, map, floors, FloorView.Current - lowest, -lowest);
            }
        }

        private static string Tip(int k) => "PZ_FloorHudTip".Translate(FloorView.Label(k), FloorView.KeyName(PZKeyDefOf.PZ_FloorUp),
            FloorView.KeyName(PZKeyDefOf.PZ_FloorDown), FloorView.KeyName(PZKeyDefOf.PZ_FloorScrollHold));

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

        // (k, cur, ground: row indices; ground = the ground's row)
        private static void Draw(Rect r, Map map, int floors, int cur, int ground)
        {
            float now = Time.realtimeSinceStartup;
            if (animMap != map || animFloors != floors)
            {
                animMap = map;
                animFloors = floors;
                shown = from = cur;
                switchedAt = -1f;
            }
            else if (cur != shown)
            {
                from = shown;
                shown = cur;
                switchedAt = now;
            }
            float t = switchedAt < 0f ? 1f : Mathf.Clamp01((now - switchedAt) / SlideTime);
            float ease = 1f - (1f - t) * (1f - t);
            Refresh(map, floors, now);
            for (int k = 0; k < floors; k++)
            {
                float v = Mathf.Lerp(k == from ? 1f : 0f, k == shown ? 1f : 0f, ease);
                float f = Mathf.Lerp(k <= from ? 1f : 0f, k <= shown ? 1f : 0f, ease);
                float w = BarW + (k == ground ? GroundExtra : 0f);
                float y = r.y + (floors - 1 - k) * (BarH + Gap);
                Rect bar = new Rect(r.xMax - v * Shift - w, y, w, BarH);
                DrawBar(bar, k == ground, v, f);
                if (!dragging && Mouse.IsOver(RowRect(r, floors, k)))
                {
                    Widgets.DrawHighlight(bar);
                }
                float dx = bar.xMax - 3f - DotSize;
                if (colonist[k])
                {
                    DrawDot(new Rect(dx, y + 2f, DotSize, DotSize), ColonistDot);
                    dx -= 3f + DotSize;
                }
                if (danger[k])
                {
                    DrawDot(new Rect(dx, y + 2f, DotSize, DotSize), DangerDot);
                }
            }
        }

        // v: how much it is the viewed floor (0..1, mid-slide in between); f: how much it is filled (0 = above the view).
        private static void DrawBar(Rect bar, bool ground, float v, float f)
        {
            Color fill = Color.Lerp(ground ? GroundFill : Fill, ViewFill, v);
            Color light = Color.Lerp(ground ? GroundLight : Light, ViewLight, v);
            Color dark = Color.Lerp(ground ? GroundDark : Dark, ViewDark, v);
            Color border = Color.Lerp(Color.Lerp(HiddenOutline, Outline, f), ViewBorder, v);
            Rect inner = bar.ContractedBy(1f);
            if (f >= 1f)
            {
                Widgets.DrawBoxSolid(bar, border);
            }
            else
            {
                Frame(bar, border);
                Frame(inner, Fade(HiddenRing, 1f - f));
            }
            if (f > 0f)
            {
                Widgets.DrawBoxSolid(inner, Fade(fill, f));
                Widgets.DrawBoxSolid(new Rect(inner.x, inner.y, inner.width, 1f), Fade(light, f));
                Widgets.DrawBoxSolid(new Rect(inner.xMax - 1f, inner.y + 1f, 1f, inner.height - 1f), Fade(light, f));
                Widgets.DrawBoxSolid(new Rect(inner.x, inner.yMax - 1f, inner.width - 1f, 1f), Fade(dark, f));
                Widgets.DrawBoxSolid(new Rect(inner.x, inner.y + 1f, 1f, inner.height - 2f), Fade(dark, f));
            }
        }

        private static void DrawDot(Rect r, Color c)
        {
            Widgets.DrawBoxSolid(r, Outline);
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
            if (worldView || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null)
            {
                return;
            }
            FloorHud.DoSlot(ref curBaseY);
        }
    }
}
