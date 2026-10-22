using Verse;

namespace ProjectZ
{
    // IsoRim's mining x-ray for the vertical mining commands (IsoRim API 5; an older IsoRim: nothing): Mine above shows
    // the level above (the rock being dug into), Mine below the level below. Kept apart from IsoRimBridge like it: only
    // IsoRim.Api, only behind Present.
    [StaticConstructorOnStartup]
    public static class MineXrayBridge
    {
        static MineXrayBridge()
        {
            if (IsoRimBridge.Present && IsoRimBridge.ApiVersion >= 5)
            {
                Register();
            }
            if (IsoRimBridge.Present && IsoRimBridge.ApiVersion >= 20)
            {
                RegisterPlacing();
            }
        }

        private static void Register()
        {
            IsoRim.Api.XrayDesignator(typeof(Designator_MineAbove), false, true);
            IsoRim.Api.XrayDesignator(typeof(Designator_MineBelow), true, false);
        }

        // IsoRim Api 20 (only called when IsoRim has it): placing stairs shows the floor above, where the exit lands.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void RegisterPlacing()
        {
            System.Action<BuildableDef, bool, bool> placing = new System.Action<BuildableDef, bool, bool>(IsoRim.Api.XrayPlacing);
            if (PZDefOf.PZ_Stairs != null)
            {
                placing(PZDefOf.PZ_Stairs, false, true);
            }
            if (PZDefOf.PZ_ShipStairs != null)
            {
                placing(PZDefOf.PZ_ShipStairs, false, true);
            }
        }
    }
}
