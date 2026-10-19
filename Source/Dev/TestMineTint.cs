using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode minetint: IsoRim's tint of rock marked for mining (IsoRim MineMarks) on an upper floor of a tall mountain. The
    // same fixed-seed map as minebody/xray (Mountainous, -projectztallmountains, -projectzfloors=4, -projectzmapsize=250),
    // saved once as pz_minetint. At a mountain face, a block of level-1 rock (5 deep, 7 wide, over the face) is marked with
    // the game's Mine designator, as a player's drag, and level 1 is viewed:
    //   K1 marked, default strength                              minetint_marked.png, minetint_marked_wide.png
    //   K2 the near half unmarked with the cancel designator     minetint_half.png
    //   K3 all marked again at strength 0.6                      minetint_60.png
    // Read from IsoRim by name (MineMarks.Marked, StrengthOverride).
    public static partial class TestRunner
    {
        private static readonly List<IntVec3> mkCells = new List<IntVec3>();
        private static readonly List<IntVec3> mkNear = new List<IntVec3>();
        private static IntVec3 mkCentre;
        private static System.Type mkType;

        private static bool MkMarked(IntVec3 c, Map map)
        {
            Thing t = c.GetEdifice(map);
            return t != null && (bool)mkType.GetMethod("Marked").Invoke(null, new object[] { t });
        }

        private static bool StepMineTint(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            switch (stage)
            {
                case "mk_setup":
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    mkType = HarmonyLib.AccessTools.TypeByName("IsoRim.MineMarks");
                    if (a == null || !Mountains.On(map) || mkType == null)
                    {
                        Result("minetint: a tall-mountain map and IsoRim's MineMarks", false, $"annex {a != null}, tall mountains {Mountains.On(map)}, MineMarks {mkType != null}");
                        Finish();
                        return true;
                    }
                    GameDataSaveLoader.SaveGame("pz_minetint");
                    stage = "mk_place";
                    pendingLoad = "pz_minetint";
                    return true;

                case "mk_place":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Find.Storyteller.storytellerComps.Clear();
                    Find.Storyteller.incidentQueue.Clear();
                    if (!MbSite(map, 0))
                    {
                        Result("minetint: a mountain face", false, "none");
                        Finish();
                        return true;
                    }
                    IntVec3 d = mbCells[1] - mbCells[0];
                    IntVec3 side = d.RotatedBy(RotationDirection.Clockwise);
                    mkCells.Clear();
                    mkNear.Clear();
                    for (int i = 1; i <= 5; i++)
                    {
                        for (int j = -3; j <= 3; j++)
                        {
                            IntVec3 c = Levels.Cell(mbStand + d * i + side * j, 1, map);
                            if (c.IsValid && c.InBounds(map) && c.GetEdifice(map) is Mineable)
                            {
                                mkCells.Add(c);
                                if (i <= 2)
                                {
                                    mkNear.Add(c);
                                }
                            }
                        }
                    }
                    mkCentre = mbStand + d * 3;   // (ground cell: the camera is placed by floor)
                    Designator_Mine mine = new Designator_Mine();
                    List<IntVec3> ok = mkCells.Where(c => mine.CanDesignateCell(c).Accepted).ToList();
                    mine.DesignateMultiCell(ok);
                    Info($"minetint: face at {mbStand}, level-1 rock {mkCells.Count} cells, designated {ok.Count}, fogged {mkCells.Count(c => map.fogGrid.IsFogged(c))}, centre {mkCentre}");
                    ShotAt(map, 1, mkCentre, 9f);
                    Find.UIRoot.screenshotMode.Active = true;
                    Wait(40, "mk_marked");
                    return true;
                }

                case "mk_marked":
                    Capture("minetint_marked");
                    Result("minetint K1: the marked level-1 rock is tinted", mkCells.Count > 0 && mkCells.All(c => MkMarked(c, map)), $"{mkCells.Count(c => MkMarked(c, map))}/{mkCells.Count} marked");
                    Find.CameraDriver.SetRootPosAndSize(new Vector3(mkCentre.x + 0.5f, 0f, mkCentre.z + 0.5f), 16f);
                    Wait(30, "mk_wide");
                    return true;

                case "mk_wide":
                    Capture("minetint_marked_wide");
                    {
                        Designator_Cancel cancel = new Designator_Cancel();
                        cancel.DesignateMultiCell(mkNear.Where(c => cancel.CanDesignateCell(c).Accepted).ToList());
                    }
                    Find.CameraDriver.SetRootPosAndSize(new Vector3(mkCentre.x + 0.5f, 0f, mkCentre.z + 0.5f), 9f);
                    Wait(30, "mk_half");
                    return true;

                case "mk_half":
                    Capture("minetint_half");
                    Result("minetint K2: the near half is unmarked, the rest still marked", mkNear.All(c => !MkMarked(c, map)) && mkCells.Except(mkNear).All(c => MkMarked(c, map)),
                        $"near marked {mkNear.Count(c => MkMarked(c, map))}/{mkNear.Count}, far marked {mkCells.Except(mkNear).Count(c => MkMarked(c, map))}/{mkCells.Count - mkNear.Count}");
                    HarmonyLib.AccessTools.Field(mkType, "StrengthOverride").SetValue(null, 0.6f);
                    {
                        // all unmarked and marked again, so every block is redrawn at the new strength
                        Designator_Cancel cancel = new Designator_Cancel();
                        cancel.DesignateMultiCell(mkCells.Where(c => cancel.CanDesignateCell(c).Accepted).ToList());
                        Designator_Mine mine = new Designator_Mine();
                        mine.DesignateMultiCell(mkCells.Where(c => mine.CanDesignateCell(c).Accepted).ToList());
                    }
                    Wait(30, "mk_60");
                    return true;

                case "mk_60":
                    Capture("minetint_60");
                    Wait(5, "mk_end");
                    return true;

                case "mk_end":
                    HarmonyLib.AccessTools.Field(mkType, "StrengthOverride").SetValue(null, -1f);
                    Finish();
                    return true;
            }
            return false;
        }
    }
}
