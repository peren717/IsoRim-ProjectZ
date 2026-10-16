using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using RimWorld.Planet;

namespace ProjectZ
{
    [DefOf]
    public static class PZKeyDefOf
    {
        public static KeyBindingDef PZ_FloorUp;
        public static KeyBindingDef PZ_FloorDown;
        public static KeyBindingDef PZ_FloorScrollHold;

        static PZKeyDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(PZKeyDefOf));
        }
    }

    // The floor the player works on: the pointer picks its cells (build, zone, order, select there) and the floors
    // above it are hidden. 0 = the ground. Not saved (starts on the ground). Shown and switched by the floor HUD
    // (FloorHud.cs), the floor keys and the floor scroll (FloorKeys_Patch).
    public static class FloorView
    {
        private static int current;

        public static int Current
        {
            get
            {
                AnnexMapComponent a = ProjectZ.Annex(Find.CurrentMap);
                return a == null ? 0 : Mathf.Clamp(current, 0, a.floors - 1);
            }
        }

        public static void Set(int floor)
        {
            AnnexMapComponent a = ProjectZ.Annex(Find.CurrentMap);
            if (a == null)
            {
                current = 0;
                return;
            }
            int k = Mathf.Clamp(floor, 0, a.floors - 1);
            if (k != Current)
            {
                current = k;
                SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
            }
            current = k;
        }

        public static string Label(int floor) => "PZ_FloorLabel".Translate(floor + 1);

        // A key as players read it: = and - rather than the game's "Equals" and "Minus".
        public static string KeyName(KeyBindingDef k)
        {
            switch (k.MainKey)
            {
                case KeyCode.Equals: return "=";
                case KeyCode.Minus: return "-";
                case KeyCode.KeypadPlus: return "Num +";
                case KeyCode.KeypadMinus: return "Num -";
                case KeyCode.LeftShift:
                case KeyCode.RightShift: return "Shift";
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt: return "Alt";
                case KeyCode.LeftControl:
                case KeyCode.RightControl: return "Ctrl";
                default: return k.MainKeyLabel;
            }
        }
    }

    // Floor keys (= and - by default) and the floor scroll: the mouse wheel while the hold key (Shift by default) is down
    // changes the floor, wheel up = floor up. Runs after the windows had the event (a scrolled list keeps it) and before
    // the camera (Root.OnGUI calls CameraDriverOnGUI after UIRootOnGUI): the used event no longer zooms.
    [HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
    public static class FloorKeys_Patch
    {
        // Wheel notches give whole steps; touchpads send small deltas, added up to a step.
        private static float scrollSum;
        private static float scrollAt;

        public static void Postfix()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || WorldRendererUtility.WorldSelected)
            {
                return;
            }
            EventType type = Event.current.type;
            if (type != EventType.KeyDown && type != EventType.ScrollWheel)
            {
                return;
            }
            if (!ProjectZ.HasAnnex(Find.CurrentMap))
            {
                return;
            }
            if (type == EventType.ScrollWheel)
            {
                if (PZKeyDefOf.PZ_FloorScrollHold != null && PZKeyDefOf.PZ_FloorScrollHold.IsDown)
                {
                    Scroll(Event.current.delta);
                    Event.current.Use();
                }
                return;
            }
            if (PZKeyDefOf.PZ_FloorUp != null && PZKeyDefOf.PZ_FloorUp.KeyDownEvent)
            {
                FloorView.Set(FloorView.Current + 1);
                Event.current.Use();
            }
            else if (PZKeyDefOf.PZ_FloorDown != null && PZKeyDefOf.PZ_FloorDown.KeyDownEvent)
            {
                FloorView.Set(FloorView.Current - 1);
                Event.current.Use();
            }
        }

        private static void Scroll(Vector2 delta)
        {
            // (with Shift held some systems turn the wheel sideways)
            float d = delta.y != 0f ? delta.y : delta.x;
            if (d == 0f)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (now - scrollAt > 0.5f || Mathf.Sign(d) != Mathf.Sign(scrollSum))
            {
                scrollSum = 0f;
            }
            scrollAt = now;
            scrollSum += d;
            if (Mathf.Abs(scrollSum) < 1f)
            {
                return;
            }
            // wheel up (negative delta) = floor up
            FloorView.Set(FloorView.Current + (scrollSum < 0f ? 1 : -1));
            scrollSum = 0f;
        }
    }
}
