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
    //   K3 all marked again at strength 0.55                     minetint_55.png
    //   K4 the game's mark lies on the block's top: IsoRim's matrix for it (MineMarks.MarkMatrix) is at the block's top as
    //      built (within its rim's drop) and fitted inside the cell
    //   K5 the faint outline round the marked group: the walls layer of the block's section has more vertices with the
    //      outline on than off (MineMarks.OutlineOn; the section redrawn through IsoRim's own mark, each way)
    //   K6 fogged level-1 rock further in, marked: its mark lies over the fog lid (the whole cell, at the lid's height)
    //                                                            minetint_fog.png
    // Read from IsoRim by name (MineMarks.Marked, StrengthOverride, MarkMatrix, OutlineOn; SectionLayer_IsoRimWalls.Mark;
    // IsoCellMap.TopOf; View3D.Squash).
    public static partial class TestRunner
    {
        private static readonly List<IntVec3> mkCells = new List<IntVec3>();
        private static readonly List<IntVec3> mkNear = new List<IntVec3>();
        private static IntVec3 mkCentre;
        private static System.Type mkType;
        private static readonly List<IntVec3> mkFog = new List<IntVec3>();
        private static int mkVertsOn, mkVertsOff;

        // Vertices of IsoRim's walls layer in the section holding c.
        private static int MkVerts(IntVec3 c, Map map)
        {
            System.Type wl = HarmonyLib.AccessTools.TypeByName("IsoRim.SectionLayer_IsoRimWalls");
            Section s = map.mapDrawer.SectionAt(c);
            SectionLayer l = s != null && wl != null ? s.GetLayer(wl) : null;
            return l != null ? l.subMeshes.Sum(m => m.verts.Count) : -1;
        }

        private static void MkRedraw(IntVec3 c, Map map)
        {
            HarmonyLib.AccessTools.TypeByName("IsoRim.SectionLayer_IsoRimWalls").GetMethod("Mark").Invoke(null, new object[] { map, c });
        }

        private static Matrix4x4 MkMatrix(IntVec3 c, Map map)
        {
            return (Matrix4x4)mkType.GetMethod("MarkMatrix").Invoke(null, new object[] { map, c });
        }

        private static float MkTop(IntVec3 c, Map map)
        {
            return (float)HarmonyLib.AccessTools.TypeByName("IsoRim.IsoCellMap").GetMethod("TopOf").Invoke(null, new object[] { map, c });
        }

        private static float MkSquash()
        {
            return (float)HarmonyLib.AccessTools.TypeByName("IsoRim.View3D").GetProperty("Squash").GetValue(null);
        }

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
                    HarmonyLib.AccessTools.Field(mkType, "StrengthOverride").SetValue(null, 0.55f);
                    {
                        // all unmarked and marked again, so every block is redrawn at the new strength
                        Designator_Cancel cancel = new Designator_Cancel();
                        cancel.DesignateMultiCell(mkCells.Where(c => cancel.CanDesignateCell(c).Accepted).ToList());
                        Designator_Mine mine = new Designator_Mine();
                        mine.DesignateMultiCell(mkCells.Where(c => mine.CanDesignateCell(c).Accepted).ToList());
                    }
                    Wait(30, "mk_55");
                    return true;

                case "mk_55":
                {
                    Capture("minetint_55");
                    float squash = MkSquash();
                    List<string> bad = new List<string>();
                    foreach (IntVec3 c in mkCells.Where(c => !map.fogGrid.IsFogged(c)))
                    {
                        Matrix4x4 m = MkMatrix(c, map);
                        float y = m.m13 * squash - 0.03f;   // less the mark's lift over the top
                        float top = MkTop(c, map);
                        Vector3 centre = m.MultiplyPoint3x4(new Vector3(c.x + 0.5f, 0f, c.z + 0.5f));
                        bool inCell = centre.x > c.x && centre.x < c.x + 1 && centre.z > c.z && centre.z < c.z + 1
                            && m.m00 > 0.3f && m.m00 <= 1.001f && m.m22 > 0.3f && m.m22 <= 1.001f;
                        if (y > top + 0.01f || y < top - 0.2f || !inCell)
                        {
                            bad.Add($"{c} y {y:0.000} top {top:0.000} size {m.m00:0.00}x{m.m22:0.00} centre {centre.x:0.00},{centre.z:0.00}");
                        }
                    }
                    int unfogged = mkCells.Count(c => !map.fogGrid.IsFogged(c));
                    Result("minetint K4: the game's mark lies on the block's top, fitted to it", unfogged > 0 && bad.Count == 0,
                        bad.Count == 0 ? $"{unfogged} cells" : string.Join("; ", bad.Take(4)));
                    mkVertsOn = MkVerts(mkCells[0], map);
                    HarmonyLib.AccessTools.Field(mkType, "OutlineOn").SetValue(null, false);
                    MkRedraw(mkCells[0], map);
                    Wait(20, "mk_ol_off");
                    return true;
                }

                case "mk_ol_off":
                    mkVertsOff = MkVerts(mkCells[0], map);
                    HarmonyLib.AccessTools.Field(mkType, "OutlineOn").SetValue(null, true);
                    MkRedraw(mkCells[0], map);
                    Wait(20, "mk_ol_on");
                    return true;

                case "mk_ol_on":
                {
                    int again = MkVerts(mkCells[0], map);
                    Result("minetint K5: the outline round the marked group is drawn (walls layer vertices, outline on vs off)",
                        mkVertsOff > 0 && again > mkVertsOff && again == mkVertsOn, $"on {mkVertsOn}, off {mkVertsOff}, on again {again}");
                    // K6: fogged level-1 rock further in
                    IntVec3 d = mbCells[1] - mbCells[0];
                    IntVec3 side = d.RotatedBy(RotationDirection.Clockwise);
                    mkFog.Clear();
                    for (int i = 6; i <= 12; i++)
                    {
                        for (int j = -3; j <= 3; j++)
                        {
                            IntVec3 c = Levels.Cell(mbStand + d * i + side * j, 1, map);
                            if (c.IsValid && c.InBounds(map) && c.GetEdifice(map) is Mineable && map.fogGrid.IsFogged(c))
                            {
                                mkFog.Add(c);
                            }
                        }
                    }
                    Designator_Mine mine = new Designator_Mine();
                    List<IntVec3> ok = mkFog.Where(c => mine.CanDesignateCell(c).Accepted).ToList();
                    mine.DesignateMultiCell(ok);
                    Info($"minetint: fogged level-1 rock further in {mkFog.Count} cells, designated {ok.Count}");
                    Find.CameraDriver.SetRootPosAndSize(new Vector3(mkCentre.x + 0.5f + d.x * 4, 0f, mkCentre.z + 0.5f + d.z * 4), 12f);
                    Wait(30, "mk_fog");
                    return true;
                }

                case "mk_fog":
                {
                    Capture("minetint_fog");
                    float squash = MkSquash();
                    List<string> bad = new List<string>();
                    List<IntVec3> marked = mkFog.Where(c => map.designationManager.DesignationAt(c, DesignationDefOf.Mine) != null).ToList();
                    foreach (IntVec3 c in marked)
                    {
                        Matrix4x4 m = MkMatrix(c, map);
                        float y = m.m13 * squash - 0.03f;
                        float top = MkTop(c, map);
                        if (Mathf.Abs(y - top) > 0.002f || Mathf.Abs(m.m00 - 1f) > 0.001f || Mathf.Abs(m.m03) > 0.001f)
                        {
                            bad.Add($"{c} y {y:0.000} top {top:0.000} size {m.m00:0.00}");
                        }
                    }
                    Result("minetint K6: a fogged marked rock's mark lies over the fog lid (whole cell, the lid's height)", marked.Count > 0 && bad.Count == 0,
                        bad.Count == 0 ? $"{marked.Count} fogged cells marked" : string.Join("; ", bad.Take(4)));
                    Wait(5, "mk_end");
                    return true;
                }

                case "mk_end":
                    HarmonyLib.AccessTools.Field(mkType, "StrengthOverride").SetValue(null, -1f);
                    Finish();
                    return true;
            }
            return false;
        }
    }
}
