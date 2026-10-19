using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode xray: IsoRim's x-ray of the floor below while the Mine tool is in hand upstairs (IsoRim MineXray), in a tall
    // mountain. The same fixed-seed map as minebody (Mountainous, -projectztallmountains, -projectzfloors=4,
    // -projectzmapsize=250), saved once as pz_xray. The fastest miner digs a tunnel into a mountain face with the real job
    // (6 cells straight, then 3 to the side where there is rock), the game ticking under fixed seeds; a second colonist is
    // put in the tunnel. Then level 1 is viewed with the pointer held (IsoRim's designator cell and cut-away pointer) on the
    // rock above the tunnel's fifth cell, and shots are taken:
    //   X1 Mine tool in hand: the x-ray is drawn (outline, pawns, crosshair)      xray_tool.png, xray_tool_wide.png
    //   X2 no tool: nothing drawn                                                  xray_none.png
    //   X3 tool in hand, the ground viewed: only the floor above (two-way x-ray)  xray_ground.png
    // Read from IsoRim by name (MineXray.Shown, OutlineQuads, BuildMs).
    public static partial class TestRunner
    {
        private static readonly List<IntVec3> xrCells = new List<IntVec3>();
        private static IntVec3 xrUp;
        private static Pawn xrMiner;
        private static int xrFrame;
        private static bool xrPatched;
        private static System.Type xrType;

        private static object XrGet(string field) => HarmonyLib.AccessTools.Field(xrType, field)?.GetValue(null);

        private static bool StepXray(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            switch (stage)
            {
                case "xr_setup":
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    xrType = HarmonyLib.AccessTools.TypeByName("IsoRim.MineXray");
                    if (a == null || !Mountains.On(map) || xrType == null)
                    {
                        Result("xray: a tall-mountain map and IsoRim's MineXray", false, $"annex {a != null}, tall mountains {Mountains.On(map)}, MineXray {xrType != null}");
                        Finish();
                        return true;
                    }
                    GameDataSaveLoader.SaveGame("pz_xray");
                    stage = "xr_place";
                    pendingLoad = "pz_xray";
                    return true;

                case "xr_place":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    DebugSettings.enableRandomMentalStates = false;
                    Find.Storyteller.storytellerComps.Clear();
                    Find.Storyteller.incidentQueue.Clear();
                    xrMiner = Colonists(map).Where(p => !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining)).OrderByDescending(p => p.GetStatValue(StatDefOf.MiningSpeed)).ThenBy(p => p.thingIDNumber).FirstOrDefault();
                    mbPawn = xrMiner;
                    if (xrMiner == null || !MbSite(map, 0))
                    {
                        Result("xray: a miner and a mountain face", false, "none");
                        Finish();
                        return true;
                    }
                    xrCells.Clear();
                    xrCells.AddRange(mbCells);
                    IntVec3 d = mbCells[1] - mbCells[0];
                    IntVec3 side = d.RotatedBy(RotationDirection.Clockwise);
                    for (int i = 1; i <= 3; i++)
                    {
                        IntVec3 c = mbCells[5] + side * i;
                        if (!(c.InBounds(map) && c.GetEdifice(map) is Mineable m && m.def.building.isNaturalRock))
                        {
                            break;
                        }
                        xrCells.Add(c);
                    }
                    foreach (Pawn p in Colonists(map))
                    {
                        if (p != xrMiner && p.drafter != null)
                        {
                            p.drafter.Drafted = true;
                        }
                    }
                    Teleport(xrMiner, mbStand);
                    if (xrMiner.drafter != null)
                    {
                        xrMiner.drafter.Drafted = false;
                    }
                    foreach (IntVec3 c in xrCells)
                    {
                        map.designationManager.AddDesignation(new Designation(c, DesignationDefOf.Mine));
                    }
                    FgWork(map, new[] { xrMiner }, WorkTypeDefOf.Mining);
                    Info($"xray: miner {xrMiner} at {mbStand}, tunnel {string.Join(" ", xrCells)}");
                    xrFrame = 0;
                    stage = "xr_dig";
                    return true;
                }

                case "xr_dig":
                {
                    System.Func<bool> dug = () => xrCells.All(c => c.GetFirstMineable(map) == null);
                    Rand.PushState(7100 + xrFrame);
                    try
                    {
                        for (int t = 0; t < 300 && !dug(); t++)
                        {
                            if (xrMiner.needs?.food != null) xrMiner.needs.food.CurLevel = xrMiner.needs.food.MaxLevel;
                            if (xrMiner.needs?.rest != null) xrMiner.needs.rest.CurLevel = xrMiner.needs.rest.MaxLevel;
                            Find.TickManager.DoSingleTick();
                        }
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                    if (!dug() && ++xrFrame < 200)
                    {
                        return true;
                    }
                    Result("xray: the tunnel is dug by the real job", dug(), $"{xrCells.Count(c => c.GetFirstMineable(map) == null)}/{xrCells.Count} cells after {xrFrame} frames of 300 ticks");
                    // a second colonist in the tunnel, drafted (still)
                    Pawn other = Colonists(map).Where(p => p != xrMiner && !p.Downed).OrderBy(p => p.thingIDNumber).FirstOrDefault();
                    if (other != null)
                    {
                        Teleport(other, xrCells[2]);
                    }
                    xrUp = Levels.Cell(xrCells[4], 1, map);
                    Info($"xray: pointer cell above the tunnel {xrUp} ({xrUp.GetFirstMineable(map)?.def.defName ?? "no rock"}, fogged {map.fogGrid.IsFogged(xrUp)}); miner on {xrMiner.Position}, other on {other?.Position}");
                    XrPointer();
                    ShotAt(map, 1, xrCells[4], 9f);
                    Find.DesignatorManager.Select(new Designator_Mine());
                    Find.UIRoot.screenshotMode.Active = true;
                    Wait(40, "xr_tool");
                    return true;
                }

                case "xr_tool":
                    Capture("xray_tool");
                    Wait(5, "xr_tool_check");
                    return true;

                case "xr_tool_check":
                    Result("xray X1: drawn with the Mine tool in hand upstairs", (bool)XrGet("Shown") && (int)XrGet("OutlineQuads") > 0,
                        $"shown {XrGet("Shown")}, outline quads {XrGet("OutlineQuads")}, last rebuild {(double)XrGet("BuildMs"):0.00} ms, rebuilds {XrGet("Builds")}");
                    Find.CameraDriver.SetRootPosAndSize(new Vector3(xrCells[4].x + 0.5f, 0f, xrCells[4].z + 0.5f), 18f);
                    Wait(30, "xr_wide");
                    return true;

                case "xr_wide":
                    Capture("xray_tool_wide");
                    Wait(5, "xr_none_set");
                    return true;

                case "xr_none_set":
                    Find.DesignatorManager.Deselect();
                    Find.CameraDriver.SetRootPosAndSize(new Vector3(xrCells[4].x + 0.5f, 0f, xrCells[4].z + 0.5f), 9f);
                    Wait(30, "xr_none");
                    return true;

                case "xr_none":
                    Capture("xray_none");
                    Wait(5, "xr_none_check");
                    return true;

                case "xr_none_check":
                    Result("xray X2: nothing without the tool", !(bool)XrGet("Shown"), $"shown {XrGet("Shown")}");
                    Find.DesignatorManager.Select(new Designator_Mine());
                    FloorView.Set(0);
                    Wait(30, "xr_ground");
                    return true;

                case "xr_ground":
                    Capture("xray_ground");
                    Wait(5, "xr_ground_check");
                    return true;

                case "xr_ground_check":
                    Result("xray X3: the ground viewed: no floor below, the floor above shown (two-way x-ray)", XrGet("ShownDown") is bool dn && !dn && XrGet("ShownUp") is bool upx && upx, $"shown {XrGet("Shown")}, down {XrGet("ShownDown")}, up {XrGet("ShownUp")}");
                    Find.DesignatorManager.Deselect();
                    Finish();
                    return true;
            }
            return false;
        }

        // The pointer held on the rock above the tunnel: IsoRim's designator cell and its cut-away pointer.
        private static void XrPointer()
        {
            if (xrPatched)
            {
                return;
            }
            xrPatched = true;
            HarmonyLib.Harmony h = new HarmonyLib.Harmony("Peren.IsoRimProjectZ.test.xray");
            h.Patch(HarmonyLib.AccessTools.Method(HarmonyLib.AccessTools.TypeByName("IsoRim.Interaction"), "DesignatorMouseCell"),
                prefix: new HarmonyLib.HarmonyMethod(typeof(TestRunner), nameof(XrCellPrefix)));
            h.Patch(HarmonyLib.AccessTools.Method(HarmonyLib.AccessTools.TypeByName("IsoRim.Cutaway"), "Pointer"),
                prefix: new HarmonyLib.HarmonyMethod(typeof(TestRunner), nameof(XrPointerPrefix)));
        }

        private static bool XrCellPrefix(ref IntVec3 __result)
        {
            __result = xrUp;
            return false;
        }

        private static bool XrPointerPrefix(ref Vector3 ground, ref bool __result)
        {
            ground = xrUp.ToVector3Shifted();
            __result = true;
            return false;
        }
    }
}
