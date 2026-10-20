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
        }

        private static void Register()
        {
            IsoRim.Api.XrayDesignator(typeof(Designator_MineAbove), false, true);
            IsoRim.Api.XrayDesignator(typeof(Designator_MineBelow), true, false);
        }
    }
}
