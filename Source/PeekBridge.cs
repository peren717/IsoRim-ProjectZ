using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // The peek key (PZ_PeekUpper, no default key): while it is held, IsoRim draws the floors above the viewed one faded
    // (IsoRim API 7, Api.PeekUpperFloors; an older IsoRim: the key does nothing). Kept apart from IsoRimBridge like
    // MineXrayBridge: only IsoRim.Api, only behind Present. Read each frame; IsoRim's flag is set only when the key
    // goes down or up, and never while a text field has focus.
    [HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootUpdate))]
    public static class PeekBridge
    {
        private static bool held;

        public static void Postfix()
        {
            if (!IsoRimBridge.Present || IsoRimBridge.ApiVersion < 7 || PZKeyDefOf.PZ_PeekUpper == null)
            {
                return;
            }
            bool now = GUIUtility.keyboardControl == 0 && PZKeyDefOf.PZ_PeekUpper.IsDown;
            if (now != held)
            {
                held = now;
                Set(now);
            }
        }

        private static void Set(bool on) => IsoRim.Api.PeekUpperFloors = on;
    }
}
