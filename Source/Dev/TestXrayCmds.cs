using Verse;

namespace ProjectZ.Dev
{
    // Mode xray, after X5: IsoRim's x-ray with ProjectZ's vertical mining commands (MineXrayBridge), same map and tunnel:
    //   X6 Mine above, the ground viewed: only the floor above       xray_mineabove_l0.png
    //   X7 Mine above, level 1 viewed: only the floor above          xray_mineabove_l1.png
    //   X8 Mine below, level 1 viewed: only the floor below          xray_minebelow_l1.png
    //   X9 Mine above with mineXray and mineCrosshair off: nothing   xray_cmds_off.png
    public static partial class TestRunner
    {
        private static object xrOldXray, xrOldCross;

        private static bool StepXrayCmds(Map map)
        {
            switch (stage)
            {
                case "xr_cmds":
                    Find.DesignatorManager.Select(new Designator_MineAbove());
                    ShotAt(map, 0, xrCells[4], 9f);
                    Find.UIRoot.screenshotMode.Active = true;
                    Wait(40, "xr_c6");
                    return true;

                case "xr_c6":
                    Capture("xray_mineabove_l0");
                    XrOnly("X6: Mine above, the ground viewed: only the floor above", false, true);
                    ShotAt(map, 1, xrCells[4], 9f);
                    Find.UIRoot.screenshotMode.Active = true;
                    Wait(40, "xr_c7");
                    return true;

                case "xr_c7":
                    Capture("xray_mineabove_l1");
                    XrOnly("X7: Mine above, level 1 viewed: only the floor above", false, true);
                    Find.DesignatorManager.Select(new Designator_MineBelow());
                    Wait(40, "xr_c8");
                    return true;

                case "xr_c8":
                    Capture("xray_minebelow_l1");
                    XrOnly("X8: Mine below, level 1 viewed: only the floor below", true, false);
                    xrOldXray = IsoRimBridge.SetOption("mineXray", false);
                    xrOldCross = IsoRimBridge.SetOption("mineCrosshair", false);
                    Find.DesignatorManager.Select(new Designator_MineAbove());
                    Wait(40, "xr_c9");
                    return true;

                case "xr_c9":
                    Capture("xray_cmds_off");
                    Wait(5, "xr_c9_check");
                    return true;

                case "xr_c9_check":
                    Result("xray X9: Mine above with the x-ray options off: nothing", XrGet("Shown") is bool s9 && !s9, $"shown {XrGet("Shown")}");
                    IsoRimBridge.SetOption("mineXray", xrOldXray ?? true);
                    IsoRimBridge.SetOption("mineCrosshair", xrOldCross ?? false);
                    Find.DesignatorManager.Deselect();
                    Finish();
                    return true;
            }
            return false;
        }

        private static void XrOnly(string name, bool down, bool up)
        {
            Result("xray " + name, XrGet("ShownDown") is bool d && d == down && XrGet("ShownUp") is bool u && u == up,
                $"down {XrGet("ShownDown")}, up {XrGet("ShownUp")}, api {IsoRimBridge.ApiVersion}");
        }
    }
}
