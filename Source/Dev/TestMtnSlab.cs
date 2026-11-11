using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode mtnslab (player report 2026-11-10, repro only): inside a mountain, levels 0 and 1 both mined out (a two-storey
    // firing hall); the natural rock floor between them (level 1's floor = level 0's ceiling) can't be removed. Run with
    // -projectztallmountains -projectzfloors=4 -projectzmapsize=250 (the map from the harness seed -isorimseed, the tallmtn tile). Setup: a 3x3 block
    // with rock on levels 0..2 and a rock ring, carved by the mining end path (Mineable.DestroyMined) level 0 first, then
    // level 1 (the player's order); one worker in each room; a built-slab control over open ground (slab on level 1 and
    // level 2). Saved (pz_mtnslab); every path reloads it, designates with the game's designator on the viewed level and
    // ticks the worker until the floor is gone or time runs out. Records: accepted / reason, job seen, outcome.
    public static partial class TestRunner
    {
        private static CellRect msRoom, msCtl;
        private static IntVec3 msG, msCg;
        private static int msUpId, msDownId, msCtlId;
        private static readonly List<string> msTable = new List<string>();
        private static readonly HashSet<string> msJobs = new HashSet<string>();
        private static int msPath;
        private static string msPathName;
        private static int msLookId = -1, msOnTop;
        private static IntVec3 msLook;

        private static bool MsRock(IntVec3 c, Map map) => c.IsValid && c.InBounds(map) && c.GetEdifice(map) is Mineable m && m.def.building.isNaturalRock && !m.def.building.isResourceRock && m.def != ThingDefOf.CollapsedRocks;

        private static Pawn MsPawn(Map map, int id) => map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == id);

        private static string MsCell(IntVec3 c, Map map)
        {
            if (!c.IsValid)
            {
                return "invalid";
            }
            TerrainGrid tg = map.terrainGrid;
            return $"{c} L{ProjectZ.FloorOf(c, map)} top {tg.TopTerrainAt(c)?.defName} under {tg.UnderTerrainAt(c)?.defName ?? "-"} kind {Floors.KindAt(c, map)} roof {map.roofGrid.RoofAt(c)?.defName ?? "none"} edifice {c.GetEdifice(map)?.def.defName ?? "-"} fog {map.fogGrid.IsFogged(c)}";
        }

        private static bool MsSite(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            foreach (IntVec3 g in Near(new IntVec3(map.Size.x / 2, 0, a.groundZ / 2), 110, map))
            {
                CellRect r = new CellRect(g.x - 1, g.z - 1, 3, 3);
                CellRect ring = r.ExpandedBy(1);
                if (ring.minX < 2 || ring.minZ < 2 || ring.maxX >= map.Size.x - 2 || ring.maxZ >= a.groundZ - 2)
                {
                    continue;
                }
                bool ok = true;
                foreach (IntVec3 c in ring)
                {
                    ok = MsRock(c, map) && MsRock(Levels.Cell(c, 1, map), map) && MsRock(Levels.Cell(c, 2, map), map) && (map.roofGrid.RoofAt(c)?.isThickRoof ?? false);
                    if (!ok)
                    {
                        break;
                    }
                }
                if (ok)
                {
                    msRoom = r;
                    msG = g;
                    return true;
                }
            }
            return false;
        }

        // Open roofless ground, open air on levels 1 and 2, 3x3 plus a ring, at least 15 cells from the room.
        private static bool MsCtlSite(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            foreach (IntVec3 g in Near(msG, 100, map))
            {
                if (g.InHorDistOf(msG, 15f))
                {
                    continue;
                }
                CellRect r = new CellRect(g.x - 1, g.z - 1, 3, 3);
                CellRect ring = r.ExpandedBy(1);
                if (ring.minX < 2 || ring.minZ < 2 || ring.maxX >= map.Size.x - 2 || ring.maxZ >= a.groundZ - 2)
                {
                    continue;
                }
                bool ok = true;
                foreach (IntVec3 c in ring)
                {
                    IntVec3 u1 = Levels.Cell(c, 1, map), u2 = Levels.Cell(c, 2, map);
                    ok = Mountains.WalkableRaw(c, map) && !c.Roofed(map) && c.GetEdifice(map) == null && !map.fogGrid.IsFogged(c)
                        && u1.IsValid && u1.GetTerrain(map) == PZDefOf.PZ_OpenAir && u1.GetEdifice(map) == null && u2.IsValid && u2.GetTerrain(map) == PZDefOf.PZ_OpenAir;
                    if (!ok)
                    {
                        break;
                    }
                }
                if (ok)
                {
                    msCtl = r;
                    msCg = g;
                    return true;
                }
            }
            return false;
        }

        private static void MsCarve(Map map, IEnumerable<IntVec3> cells)
        {
            foreach (IntVec3 c in cells)
            {
                if (c.GetEdifice(map) is Mineable m)
                {
                    m.DestroyMined(null);
                }
            }
            // (the yield goes: nothing on the floors)
            foreach (IntVec3 c in cells)
            {
                foreach (Thing t in c.GetThingList(map).Where(t => t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Filth).ToList())
                {
                    t.Destroy();
                }
            }
        }

        private static string MsAr(AcceptanceReport r) => r.Accepted ? "ACCEPTED" : $"refused \"{(r.Reason.NullOrEmpty() ? "(no message)" : r.Reason)}\"";

        // The right-click menu on c for p: every option label (disabled marked).
        private static string MsMenu(Pawn p, IntVec3 c)
        {
            try
            {
                List<FloatMenuOption> opts = FloatMenuMakerMap.GetOptions(new List<Pawn> { p }, c.ToVector3Shifted(), out FloatMenuContext ctx);
                return $"[{string.Join(" | ", opts.Select(x => x.Label + (x.Disabled ? " (disabled)" : "")))}]";
            }
            catch (Exception e)
            {
                return "menu failed: " + e.Message;
            }
        }

        // The paths: name, the level viewed, the target cell (level 1 floor X1 from above, the level 0 cell X0 under it from below),
        // the designator. Control paths ("C ...") act on the built slab.
        private static readonly string[] msPaths =
        {
            "A1 above: Remove upper floor (拆除楼板)",
            "A2 above: Remove floor",
            "A3 above: Mine below",
            "A4 above: Mine",
            "A5 above: Deconstruct",
            "A6 above: lay a slab over it (replace)",
            "B1 below: Remove roof (no-roof area)",
            "B2 below: Mine above",
            "B3 below: Remove upper floor on the ceiling cell (level-0 cell)",
            "C1 control: Remove upper floor on the built level-1 slab (under the level-2 slab)",
            "C2 control: Remove roof under the built slab",
            "R1 above: Remove upper floor on a top over unmined rock",
            "R2 above: Remove upper floor on a top with a wood floor laid on it",
            "R3 above: Remove upper floor on a top with a wall on it",
        };

        private static bool MsCtl(int i) => i == 9 || i == 10;

        private static bool MsAbove(int i) => i <= 5 || i >= 11;

        // The ground cell of path i's column (target: its level-1 cell from above, the ground cell from below).
        private static IntVec3 MsColumn(int i) => MsCtl(i) ? msCg : i == 11 ? new IntVec3(msRoom.maxX + 1, 0, msG.z) : i == 12 ? new IntVec3(msRoom.maxX, 0, msRoom.maxZ) : i == 13 ? new IntVec3(msRoom.minX, 0, msRoom.maxZ) : msG;

        private static Designator MsDesignator(int i)
        {
            switch (i)
            {
                case 0: return new Designator_RemoveFoundation();
                case 1: return new Designator_RemoveFloor();
                case 2: return new Designator_MineBelow();
                case 3: return new Designator_Mine();
                case 4: return new Designator_Deconstruct();
                case 5: return new Designator_Build(SlabDefs.All.FirstOrDefault(t => t != PZDefOf.PZ_UpperFloor) ?? PZDefOf.PZ_UpperFloor);
                case 6: return new Designator_AreaNoRoof();
                case 7: return new Designator_MineAbove();
                case 8: return new Designator_RemoveFoundation();
                case 9: return new Designator_RemoveFoundation();
                case 10: return new Designator_AreaNoRoof();
                case 11:
                case 12:
                case 13: return new Designator_RemoveFoundation();
            }
            return null;
        }

        private static bool StepMtnSlab(Map map)
        {
            Rand.PushState(FixtureSeed("mtnslab_" + stage));
            try
            {
                return StepMtnSlabInner(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static bool StepMtnSlabInner(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            switch (stage)
            {
                case "ms_setup":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    DebugSettings.enableRandomMentalStates = false;
                    Find.PlaySettings.autoRebuild = false;
                    Find.Storyteller.storytellerComps.Clear();
                    Find.Storyteller.incidentQueue.Clear();
                    if (a == null || !Mountains.On(map) || a.MaxLevel < 3)
                    {
                        Result("mtnslab: a tall-mountain map with 4 floors", false, $"annex {a != null}, mountains {Mountains.On(map)}, max level {a?.MaxLevel}");
                        Finish();
                        return true;
                    }
                    Info($"mtnslab: map {map.Size}, tile {map.Tile} {map.TileInfo?.hilliness}, floors {a.floors}, max level {a.MaxLevel}; BareTop.Off {BareTop.Off}, RockRoof.Off {RockRoof.Off}, ThickMin {Mountains.ThickMin}");
                    bool site = MsSite(map), ctl = site && MsCtlSite(map);
                    Result("mtnslab sites: a 3x3 room block with rock on levels 0-2 and a rock ring; an open control block", site && ctl, $"room {msRoom} centre {msG}; control {msCtl} centre {msCg}");
                    if (!site || !ctl)
                    {
                        Finish();
                        return true;
                    }
                    // the player's order: level 0 mined out, then level 1
                    MsCarve(map, msRoom.Cells);
                    List<IntVec3> l1 = msRoom.Cells.Select(g => Levels.Cell(g, 1, map)).ToList();
                    MsCarve(map, l1);
                    // R1: a level-1 cell dug over unmined rock (the ring east of the room); R2: a wood floor laid on a top; R3: a wall on a top
                    MsCarve(map, new[] { Levels.Cell(MsColumn(11), 1, map) });
                    map.terrainGrid.SetTerrain(Levels.Cell(MsColumn(12), 1, map), TerrainDefOf.WoodPlankFloor);
                    Place(ThingDefOf.Wall, ThingDefOf.WoodLog, Levels.Cell(MsColumn(13), 1, map), map);
                    foreach (IntVec3 g in msRoom.ExpandedBy(1))
                    {
                        for (int L = 0; L <= 2; L++)
                        {
                            IntVec3 c = Levels.Cell(g, L, map);
                            if (c.IsValid && map.fogGrid.IsFogged(c))
                            {
                                map.fogGrid.Unfog(c);
                            }
                        }
                    }
                    // the control: built slabs on levels 1 and 2 over open ground
                    R2Floor(map, msCtl, 1);
                    R2Floor(map, msCtl, 2);
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    // the workers: one per room (Mining + Construction), one on the control's level-1 slab; the rest drafted
                    List<Pawn> all = Colonists(map).Where(p => !p.Downed).OrderBy(p => p.thingIDNumber).ToList();
                    List<Pawn> able = all.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)).Take(3).ToList();
                    if (able.Count < 3)
                    {
                        Result("mtnslab: three colonists who can mine and build", false, $"{able.Count}");
                        Finish();
                        return true;
                    }
                    foreach (Pawn p in all)
                    {
                        Draft(p, !able.Contains(p));
                        p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
                        foreach (WorkTypeDef d in DefDatabase<WorkTypeDef>.AllDefs)
                        {
                            if (!p.WorkTypeIsDisabled(d))
                            {
                                p.workSettings.SetPriority(d, able.Contains(p) && (d == WorkTypeDefOf.Mining || d == WorkTypeDefOf.Construction) ? 1 : 0);
                            }
                        }
                    }
                    Teleport(able[0], Levels.Cell(msRoom.Cells.First(), 1, map));
                    Teleport(able[1], msRoom.Cells.First());
                    Teleport(able[2], Levels.Cell(msCtl.Cells.First(), 1, map));
                    msUpId = able[0].thingIDNumber;
                    msDownId = able[1].thingIDNumber;
                    msCtlId = able[2].thingIDNumber;
                    msLookId = able[2].thingIDNumber;   // (the control worker, drafted onto the top in A1)
                    IntVec3 x1 = Levels.Cell(msG, 1, map);
                    Info($"setup X1 (the floor between): {MsCell(x1, map)}");
                    Info($"setup X0 (the room below):    {MsCell(msG, map)}");
                    Info($"setup above X1 (level 2):     {MsCell(Levels.Cell(msG, 2, map), map)}");
                    Info($"setup control slab L1:        {MsCell(Levels.Cell(msCg, 1, map), map)}");
                    Info($"setup control L0:             {MsCell(msCg, map)}");
                    Info($"setup Hole.Blocked(X0) = {(Hole.Blocked(msG, map) == null ? "null (can open)" : "\"" + Hole.Blocked(msG, map) + "\"")}; UnbuiltNatural(X1) {Floors.UnbuiltNatural(x1, map)}; Slab.At(X1) {Slab.At(x1, map)}; CanRemoveFoundationAt(X1) {map.terrainGrid.CanRemoveFoundationAt(x1)}");
                    Info($"setup workers: up {able[0].LabelShort} at {able[0].Position} L{ProjectZ.FloorOf(able[0].Position, map)}, down {able[1].LabelShort} at {able[1].Position}, control {able[2].LabelShort} at {able[2].Position}");
                    SetHour(map, 11);
                    FloorView.Set(0);
                    GameDataSaveLoader.SaveGame("pz_mtnslab");
                    msPath = 0;
                    stage = "ms_shot0";
                    pendingLoad = "pz_mtnslab";
                    return true;
                }

                case "ms_shot0":
                    map.skyManager.ForceSetCurSkyGlow(1f);
                    MuShot(map, "ms_setup_L1", 1, msG, 9f, "ms_shot1");
                    return true;
                case "ms_shot1":
                    MuShot(map, "ms_setup_L0", 0, msG, 9f, "ms_shot2");
                    return true;
                case "ms_shot2":
                    MuShot(map, "ms_control_L1", 1, msCg, 9f, "ms_next");
                    return true;

                case "ms_next":
                    Find.UIRoot.screenshotMode.Active = false;
                    if (msPath >= msPaths.Length)
                    {
                        Info("TABLE path | view | target | designator | designated | jobs seen | outcome");
                        foreach (string s in msTable)
                        {
                            Info("TABLE " + s);
                        }
                        Finish();
                        return true;
                    }
                    stage = "ms_path";
                    pendingLoad = "pz_mtnslab";
                    return true;

                case "ms_path":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    int i = msPath;
                    msPathName = msPaths[i];
                    bool ctl = MsCtl(i);
                    bool above = MsAbove(i);
                    IntVec3 g = MsColumn(i);
                    IntVec3 x1 = Levels.Cell(g, 1, map);
                    IntVec3 target = ctl ? (i == 9 ? x1 : g) : above ? x1 : (i == 8 ? g : g);
                    int view = ProjectZ.FloorOf(target, map);
                    FloorView.Set(view);
                    Designator d = MsDesignator(i);
                    AcceptanceReport r;
                    try
                    {
                        r = d.CanDesignateCell(target);
                    }
                    catch (Exception e)
                    {
                        r = "EXCEPTION " + e.GetType().Name + ": " + e.Message;
                    }
                    Pawn worker = MsPawn(map, ctl ? msCtlId : above ? msUpId : msDownId);
                    string menu = worker != null ? MsMenu(worker, target) : "no worker";
                    Info($"{msPathName}: view L{view}, target {MsCell(target, map)}; {d.GetType().Name}.CanDesignateCell -> {MsAr(r)}");
                    Info($"  right-click menu of {worker?.LabelShort} on the target: {menu}");
                    if (!r.Accepted)
                    {
                        if (i >= 11)
                        {
                            string want = (i == 11 ? "PZ_RockBelowMineBelow" : i == 12 ? "PZ_RemoveLaidFloorFirst" : "PZ_BuiltHere").Translate();
                            Result($"{msPathName}: refused with its reason", r.Reason == want, $"got \"{r.Reason}\", want \"{want}\"");
                        }
                        msTable.Add($"{msPathName} | L{view} | {target} | {MsAr(r)} | no | - | floor between {(ProjectZ.HasFloor(x1, map) ? "STAYS" : "gone")}");
                        msPath++;
                        stage = "ms_next";
                        return true;
                    }
                    if (d is Designator_Build)
                    {
                        // (laying a slab of other stuff over it: a blueprint; record what it becomes, no materials given)
                        d.DesignateSingleCell(target);
                    }
                    else
                    {
                        d.DesignateSingleCell(target);
                    }
                    string des = string.Join(",", map.designationManager.AllDesignationsAt(target).Select(x => x.def.defName))
                        + (d is Designator_AreaNoRoof ? (map.areaManager.NoRoof[target] ? " NoRoof area set" : " NoRoof area NOT set") : "")
                        + (d is Designator_Build ? " things: " + string.Join(",", target.GetThingList(map).Select(t => t.def.defName)) : "");
                    Info($"  designated: {des}");
                    if (i == 9 && worker != null)
                    {
                        // (no stairs at the control: automatic work has no peel order there; the player's right-click order)
                        bool forced = FoForce(worker, x1, out string shown);
                        des += forced ? " + right-click order" : " (right-click order not offered)";
                        Info($"  forced order: {forced}; {shown}");
                    }
                    msOnTop = 0;
                    msLook = IntVec3.Invalid;
                    if (i == 0)
                    {
                        // (a drafted colonist stands on the top: it steps aside as for Remove roof)
                        Pawn look = MsPawn(map, msLookId);
                        if (look != null)
                        {
                            Draft(look, true);
                            Teleport(look, x1);
                            msLook = x1;
                        }
                    }
                    msJobs.Clear();
                    Pawn wk = worker;
                    MuRun(60, $"91000_{i}", () => !ProjectZ.HasFloor(x1, map), "ms_path_done", () =>
                    {
                        Job j = wk?.CurJob;
                        if (j != null && j.def == JobDefOf.RemoveFoundation && wk.Position == x1 && ProjectZ.HasFloor(x1, map))
                        {
                            msOnTop++;
                        }
                        if (j != null && (j.targetA.Cell == target || j.targetA.Cell == x1 || j.targetA.Cell == g || j.targetB.Cell == target))
                        {
                            msJobs.Add(j.def.defName);
                        }
                    });
                    msTable.Add($"{msPathName} | L{view} | {target} | ACCEPTED | {des} | ");
                    return true;
                }

                case "ms_path_done":
                {
                    int i = msPath;
                    IntVec3 g = MsColumn(i);
                    IntVec3 x1 = Levels.Cell(g, 1, map);
                    Pawn worker = MsPawn(map, MsCtl(i) ? msCtlId : MsAbove(i) ? msUpId : msDownId);
                    string why = "";
                    if (ProjectZ.HasFloor(x1, map) && worker != null)
                    {
                        // why no job: the work givers' own answer for the target, unforced and forced
                        foreach (WorkGiverDef wgd in DefDatabase<WorkGiverDef>.AllDefs.Where(w => w.Worker is WorkGiver_Scanner && (w.defName.Contains("RemoveRoof") || w.defName.Contains("RemoveFoundation") || w.defName.Contains("MineFrom") || w.defName.Contains("RemoveFloor") || w.defName == "Mine")))
                        {
                            WorkGiver_Scanner sc = (WorkGiver_Scanner)wgd.Worker;
                            IntVec3 t = i == 6 || i == 7 || i == 10 ? g : x1;
                            JobFailReason.Clear();
                            bool hj = false, hf = false;
                            try
                            {
                                hj = sc.HasJobOnCell(worker, t, false);
                                hf = sc.HasJobOnCell(worker, t, true);
                            }
                            catch (Exception e)
                            {
                                why += $" {wgd.defName}: exception {e.Message};";
                                continue;
                            }
                            why += $" {wgd.defName}({t}) job {hj}/forced {hf}{(JobFailReason.HaveReason ? " reason \"" + JobFailReason.Reason + "\"" : "")};";
                        }
                    }
                    bool gone = !ProjectZ.HasFloor(x1, map);
                    msTable[msTable.Count - 1] += $"{(msJobs.Count == 0 ? "none" : string.Join(",", msJobs))} | floor between {(gone ? "GONE" : "STAYS")}; after: {MsCell(x1, map)}; below roof {map.roofGrid.RoofAt(g)?.defName ?? "none"}{(why.Length > 0 ? "; work givers:" + why : "")}";
                    Info($"  outcome: {msTable[msTable.Count - 1]}");
                    if (i == 0)
                    {
                        Pawn look = MsPawn(map, msLookId);
                        bool mark = ShaftMarks.Has(g, map);
                        Result("A1 Remove upper floor from above opens the rock floor between two dug levels (a real job), as Remove roof from below: open air, shaft mark, the stairwell roof below, the worker never on the top, a colonist on it stepped aside on level 1",
                            gone && msJobs.Contains("RemoveFoundation") && mark && map.roofGrid.RoofAt(g) == PZDefOf.PZ_StairwellRoof && msOnTop == 0 && (look == null || ProjectZ.FloorOf(look.Position, map) == 1 && look.Position != x1),
                            $"gone {gone}, jobs {string.Join(",", msJobs)}, shaft mark {mark}, roof below {map.roofGrid.RoofAt(g)?.defName ?? "none"}, worker ticks on the top {msOnTop}, onlooker {look?.LabelShort} at {look?.Position} L{(look == null ? -1 : ProjectZ.FloorOf(look.Position, map))}, TopRemoval.Opened {TopRemoval.Opened}, SteppedAside {Shaft.SteppedAside}");
                    }
                    msPath++;
                    if (gone && (i == 0 || i == 6 || i == 9))
                    {
                        MuShot(map, i == 0 ? "ms_after_A1_L1" : i == 6 ? "ms_after_B1_L0" : "ms_after_C1_L1", i == 6 ? 0 : 1, g, 9f, "ms_next");
                        return true;
                    }
                    stage = "ms_next";
                    return true;
                }
            }
            return true;
        }
    }
}
