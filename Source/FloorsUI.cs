using UnityEngine;
using Verse;

namespace ProjectZ
{
    // Shared by the mod settings slider and the Create World page's Floors row.
    public static class FloorsUI
    {
        // Above two storeys a warning under the slider, yellow then red: every storey is another whole map of cells,
        // and more than 5 are untested. False (no warning) for 1 or 2.
        public static bool Warning(int floors, out string text, out Color color)
        {
            string key;
            if (floors >= 13)
            {
                key = "PZ_FloorsWarn13";
                color = ColorLibrary.RedReadable;
            }
            else if (floors >= 10)
            {
                key = "PZ_FloorsWarn10";
                color = ColorLibrary.RedReadable;
            }
            else if (floors >= 6)
            {
                key = "PZ_FloorsWarn6";
                color = new Color(1f, 0.75f, 0.2f);
            }
            else if (floors >= 3)
            {
                key = "PZ_FloorsWarn3";
                color = new Color(1f, 0.92f, 0.45f);
            }
            else
            {
                text = null;
                color = Color.white;
                return false;
            }
            text = key.Translate();
            return true;
        }

        public static void DrawWarning(Listing_Standard list, int floors)
        {
            if (!Warning(floors, out string text, out Color color))
            {
                return;
            }
            GUI.color = color;
            list.Label(text);
            GUI.color = Color.white;
        }

        // At the top of rect, wrapped to its width; returns the height used.
        public static float DrawWarning(Rect rect, int floors)
        {
            if (!Warning(floors, out string text, out Color color))
            {
                return 0f;
            }
            float h = Text.CalcHeight(text, rect.width);
            GUI.color = color;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, h), text);
            GUI.color = Color.white;
            return h;
        }
    }
}
