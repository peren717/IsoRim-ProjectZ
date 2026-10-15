using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Building floor 2 through the UI (mode floorui; the playtest bug "requires supporting terrain: upper floor space").
    // Scene: the stairs test's houses (SetupStairs), but house A's floor only over its east part (stairs and exit), its
    // west part open air over the roof. Everything runs through the game's own pointer and designator paths: the mouse is
    // a screen point (UI.MousePositionOnUI / MouseCell / MouseMapPosition answer it), designators are selected in the
    // DesignatorManager, clicks and drags go through CanDesignateCell / DesignateSingleCell / the DesignationDragger, and
    // colonists build the blueprints with real ticks.
    //   1. no designator: the pointer over open air on floor 2 falls through to the ground (the move-order feature);
    //   2. every designator in the architect menu keeps the pointer on the viewed floor;
    //   3. refusals: an upper floor while viewing the ground, over a cell with no roof below, over the stairwell; a wall
    //      on open air;
    //   4. dragging the upper floor over house A's open part on floor 2: blueprints on floor 2 (none past the roof),
    //      colonists build them;
    //   5. a wall line and a bed on the new floor, a stockpile and the home area on floor 2;
    //   6. no designator again: a drafted move order on open air goes to the ground;
    //   7. screenshots of the floor button, the inspect pane, the architect menu and the mod settings (language check).
    public static partial class TestRunner
    {
        // The pointer as a screen point (pixels), when set: the game's mouse position answers it.
        internal static Vector2? fakeMouse;
        private static Designator uFloor, uWall, uBed;
        private static List<IntVec3> uFloorCells = new List<IntVec3>();
        private static List<IntVec3> uWallCells = new List<IntVec3>();
        private static IntVec3 uBedCell;
        private static Pawn uMover;

        internal static void PatchMouse(Harmony h)
        {
            h.Patch(AccessTools.PropertyGetter(typeof(UI), nameof(UI.MousePositionOnUI)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(MousePosPrefix)));
            h.Patch(AccessTools.Method(typeof(UI), nameof(UI.MouseCell)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(MouseCellPrefix)));
            h.Patch(AccessTools.Method(typeof(UI), nameof(UI.MouseMapPosition)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(MouseMapPrefix)));
        }

        private static bool MousePosPrefix(ref Vector2 __result)
        {
            if (fakeMouse == null)
            {
                return true;
            }
            __result = fakeMouse.Value / Prefs.UIScale;
            return false;
        }

        private static bool MouseCellPrefix(ref IntVec3 __result)
        {
            if (fakeMouse == null)
            {
                return true;
            }
            __result = UI.UIToMapPosition(fakeMouse.Value / Prefs.UIScale).ToIntVec3();
            return false;
        }

        private static bool MouseMapPrefix(ref Vector3 __result)
        {
            if (fakeMouse == null)
            {
                return true;
            }
            __result = UI.UIToMapPosition(fakeMouse.Value / Prefs.UIScale);
            return false;
        }

        // The screen point over ground cell g raised to floor `floor` (as the camera sees it).
        private static Vector2 ScreenOf(IntVec3 g, int floor) => IsoRim.View3D.WorldToScreen(new Vector3(g.x + 0.5f, floor * IsoRimBridge.StoreyHeight, g.z + 0.5f));

        private static IntVec3 OnFloor(IntVec3 g, int floor, Map map) => ProjectZ.CellOnFloor(new IntVec3(g.x, 0, g.z), floor, map);

        private static IEnumerable<Designator> AllDesignators()
        {
            foreach (DesignationCategoryDef cat in DefDatabase<DesignationCategoryDef>.AllDefs)
            {
                foreach (Designator d in cat.AllResolvedDesignators)
                {
                    if (d is Designator_Dropdown dd)
                    {
                        foreach (Designator e in dd.Elements)
                        {
                            yield return e;
                        }
                    }
                    else
                    {
                        yield return d;
                    }
                }
            }
        }

        // A left click (or a drag from `from` to `to`) with the selected designator, as DesignatorManager.ProcessInputEvents
        // does it: designators without a draw style designate the clicked cell, the others drag.
        private static AcceptanceReport UiDesignate(Vector2 from, Vector2 to)
        {
            DesignatorManager dm = Find.DesignatorManager;
            Designator d = dm.SelectedDesignator;
            fakeMouse = from;
            AcceptanceReport r = AcceptanceReport.WasAccepted;
            if (dm.SelectedStyle == null)
            {
                IntVec3 c = UI.MouseCell();
                r = d.CanDesignateCell(c);
                if (r.Accepted)
                {
                    d.DesignateSingleCell(c);
                    d.Finalize(true);
                }
                else
                {
                    d.Finalize(false);
                }
            }
            else
            {
                dm.Dragger.StartDrag();
                fakeMouse = to;
                // (the dragger keeps its cells for the frame: a test may drag twice in one frame, a player never does)
                Traverse.Create(dm.Dragger).Field("lastUpdateFrame").SetValue(-1);
                List<IntVec3> cells = dm.Dragger.DragCells.ToList();
                Info($"  {d.GetType().Name} drag {cells.Count} cells, {cells.FirstOrDefault()}..{cells.LastOrDefault()}");
                d.DesignateMultiCell(cells);
                dm.Dragger.EndDrag();
            }
            fakeMouse = null;
            return r;
        }

        private static AcceptanceReport RefusalAt(Designator d, Vector2 px, out IntVec3 cell)
        {
            fakeMouse = px;
            cell = UI.MouseCell();
            AcceptanceReport r = d.CanDesignateCell(cell);
            fakeMouse = null;
            return r;
        }

        private static List<Thing> PlannedOn(Map map, IEnumerable<IntVec3> cells, BuildableDef def)
        {
            List<Thing> found = new List<Thing>();
            foreach (IntVec3 c in cells)
            {
                found.AddRange(c.GetThingList(map).Where(t => (t is Blueprint || t is Frame) && t.def.entityDefToBuild == def));
            }
            return found;
        }

        private static void ViewUp(Map map, IntVec3 g, float size)
        {
            ShotAt(map, 1, g, size);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        }

        private static bool StepFloorUI(Map map)
        {
            switch (stage)
            {
                case "u_start":
                {
                    Info($"language {LanguageDatabase.activeLanguage?.folderName}: upper floor '{PZDefOf.PZ_UpperFloor.label}', stairs '{PZDefOf.PZ_Stairs.label}', affordance '{DefDatabase<TerrainAffordanceDef>.GetNamed("PZ_FloorSupport").label}', floor up key '{PZKeyDefOf.PZ_FloorUp.label}'");
                    foreach (Pawn p in Colonists(map))
                    {
                        if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Construction))
                        {
                            p.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                        }
                        if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                        {
                            p.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2);
                        }
                    }
                    for (int i = 0; i < 4; i++)
                    {
                        Thing t = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                        t.stackCount = t.def.stackLimit;
                        GenPlace.TryPlaceThing(t, farGround, map, ThingPlaceMode.Near);
                    }
                    Find.DesignatorManager.Deselect();
                    ViewUp(map, sHouseA.CenterCell, 9f);
                    Wait(30, "u_fall1");
                    return true;
                }
                case "u_fall1":
                {
                    // 1. no designator: open air on floor 2 lets the pointer through to the ground
                    IntVec3 outside = new IntVec3(sHouseA.minX - 3, 0, sHouseA.minZ + 3);
                    IntVec3 inside = new IntVec3(sHouseA.minX + 1, 0, sHouseA.minZ + 3);
                    IntVec3 slab = new IntVec3(sHouseA.maxX - 2, 0, sHouseA.minZ + 1);
                    IntVec3 a = UI.UIToMapPosition(ScreenOf(outside, 1) / Prefs.UIScale).ToIntVec3();
                    IntVec3 b = UI.UIToMapPosition(ScreenOf(inside, 1) / Prefs.UIScale).ToIntVec3();
                    IntVec3 s = UI.UIToMapPosition(ScreenOf(slab, 1) / Prefs.UIScale).ToIntVec3();
                    Result("no designator: open air on floor 2 falls through to the ground, a floor catches the pointer",
                        ProjectZ.FloorOf(a, map) == 0 && ProjectZ.FloorOf(b, map) == 0 && s == OnFloor(slab, 1, map),
                        $"open air outside -> {a} (floor {ProjectZ.FloorOf(a, map) + 1}), open air over the room -> {b} (floor {ProjectZ.FloorOf(b, map) + 1}), slab -> {s} (want {OnFloor(slab, 1, map)})");
                    stage = "u_inventory";
                    return true;
                }
                case "u_inventory":
                {
                    // 2. every designator: the pointer stays on the viewed floor, open air included
                    IntVec3 inside = new IntVec3(sHouseA.minX + 1, 0, sHouseA.minZ + 3);
                    IntVec3 want = OnFloor(inside, 1, map);
                    Vector2 px = ScreenOf(inside, 1);
                    int n = 0;
                    List<string> bad = new List<string>();
                    Dictionary<string, int> kinds = new Dictionary<string, int>();
                    foreach (Designator d in AllDesignators())
                    {
                        try
                        {
                            Find.DesignatorManager.Select(d);
                            IntVec3 got = UI.UIToMapPosition(px / Prefs.UIScale).ToIntVec3();
                            string kind = d is Designator_Build b ? (b.PlacingDef is TerrainDef ? "build terrain" : "build thing") : d is Designator_Zone ? "zone" : d is Designator_AreaHome || d is Designator_AreaAllowed || d is Designator_AreaBuildRoof || d is Designator_AreaNoRoof || d is Designator_AreaSnowClear ? "area" : d.GetType().Name;
                            kinds[kind] = kinds.TryGetValue(kind, out int k) ? k + 1 : 1;
                            n++;
                            if (got != want)
                            {
                                bad.Add($"{d.GetType().Name} {d.Label} -> {got}");
                            }
                        }
                        catch (Exception e)
                        {
                            bad.Add($"{d.GetType().Name}: {e.Message}");
                        }
                        Find.DesignatorManager.Deselect();
                    }
                    Result($"every designator ({n}) keeps the pointer on the viewed floor over open air", n > 50 && bad.Count == 0,
                        (bad.Count == 0 ? "all on " + want : string.Join("; ", bad.Take(20))) + " | kinds: " + string.Join(", ", kinds.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}")));
                    IntVec3 after = UI.UIToMapPosition(px / Prefs.UIScale).ToIntVec3();
                    Result("deselected: the pointer falls through again", ProjectZ.FloorOf(after, map) == 0, $"{after}");
                    // 3. refusals: the upper floor while viewing the ground
                    uFloor = BuildCopyCommandUtility.FindAllowedDesignator(PZDefOf.PZ_UpperFloor);
                    Designator_Build w = BuildCopyCommandUtility.FindAllowedDesignator(ThingDefOf.Wall);
                    Designator_Build bed = BuildCopyCommandUtility.FindAllowedDesignator(ThingDefOf.Bed);
                    w?.SetStuffDef(ThingDefOf.WoodLog);
                    bed?.SetStuffDef(ThingDefOf.WoodLog);
                    uWall = w;
                    uBed = bed;
                    Result("architect designators found (upper floor, wall, bed)", uFloor != null && uWall != null && uBed != null, $"{uFloor?.Label}, {uWall?.Label}, {uBed?.Label}");
                    if (uFloor == null || uWall == null || uBed == null)
                    {
                        Finish();
                        return true;
                    }
                    Find.DesignatorManager.Select(uFloor);
                    FloorView.Set(0);
                    Wait(5, "u_refuse");
                    return true;
                }
                case "u_refuse":
                {
                    IntVec3 inside = new IntVec3(sHouseA.minX + 1, 0, sHouseA.minZ + 3);
                    AcceptanceReport g = RefusalAt(uFloor, ScreenOf(inside, 0), out IntVec3 gc);
                    Result("upper floor while viewing the ground: 'switch to the floor above'", !g.Accepted && g.Reason == PlaceWorker_UpperFloor.ViewFloorAbove() && ProjectZ.FloorOf(gc, map) == 0, $"{gc}: '{g.Reason}'");
                    Messages.Message(g.Reason, MessageTypeDefOf.RejectInput, false);
                    FloorView.Set(1);
                    Find.DesignatorManager.Select(uFloor);
                    stage = "u_refuse2";
                    waitFrames = 5;
                    return true;
                }
                case "u_refuse2":
                {
                    IntVec3 outside = new IntVec3(sHouseA.minX - 3, 0, sHouseA.minZ + 3);
                    AcceptanceReport o = RefusalAt(uFloor, ScreenOf(outside, 1), out IntVec3 oc);
                    Result("upper floor with no roof below: 'the room below needs a roof'", !o.Accepted && o.Reason == "PZ_NeedsRoofBelow".Translate() && oc == OnFloor(outside, 1, map), $"{oc}: '{o.Reason}'");
                    AcceptanceReport s = RefusalAt(uFloor, ScreenOf(stairPos, 1), out IntVec3 sc);
                    Result("upper floor over the stairs: 'keep the space over the stairs open'", !s.Accepted && s.Reason == "PZ_StairwellBelow".Translate() && sc == OnFloor(stairPos, 1, map), $"{sc}: '{s.Reason}'");
                    AcceptanceReport e = RefusalAt(uFloor, ScreenOf(new IntVec3(sHouseA.maxX - 2, 0, sHouseA.minZ + 1), 1), out IntVec3 ec);
                    Info($"upper floor on a built floor: {ec}: '{e.Reason}'");
                    Find.DesignatorManager.Select(uWall);
                    AcceptanceReport wr = RefusalAt(uWall, ScreenOf(outside, 1), out IntVec3 wc);
                    Result("a wall on open air upstairs: 'lay an upper floor here first'", !wr.Accepted && wr.Reason == "PZ_NeedsFloorFirst".Translate() && wc == OnFloor(outside, 1, map), $"{wc}: '{wr.Reason}'");
                    foreach (AcceptanceReport r in new[] { o, s, wr })
                    {
                        Messages.Message(r.Reason, MessageTypeDefOf.RejectInput, false);
                    }
                    Find.DesignatorManager.Deselect();
                    Wait(20, "u_refuse_shot");
                    return true;
                }
                case "u_refuse_shot":
                    Capture("floorui_messages");
                    stage = "u_drag";
                    return true;
                case "u_drag":
                {
                    // 4. drag the upper floor over the open part, one column past the house (no roof there)
                    Find.DesignatorManager.Select(uFloor);
                    IntVec3 from = new IntVec3(sHouseA.minX - 1, 0, sHouseA.minZ);
                    IntVec3 to = new IntVec3(sHouseA.maxX - 4, 0, sHouseA.maxZ);
                    fakeMouse = ScreenOf(from, 1);
                    IntVec3 start = UI.MouseCell();
                    Result("drag start with the floor designator lands on floor 2's open air", start == OnFloor(from, 1, map), $"{start}, want {OnFloor(from, 1, map)}");
                    Find.DesignatorManager.Dragger.StartDrag();
                    fakeMouse = ScreenOf(to, 1);
                    Wait(20, "u_drag_shot");
                    return true;
                }
                case "u_drag_shot":
                    Capture("floorui_drag");
                    stage = "u_drag_end";
                    return true;
                case "u_drag_end":
                {
                    DesignatorManager dm = Find.DesignatorManager;
                    Traverse.Create(dm.Dragger).Field("lastUpdateFrame").SetValue(-1);
                    List<IntVec3> drag = dm.Dragger.DragCells.ToList();
                    dm.SelectedDesignator.DesignateMultiCell(drag);
                    dm.Dragger.EndDrag();
                    fakeMouse = null;
                    dm.Deselect();
                    uFloorCells = new CellRect(sHouseA.minX, sHouseA.minZ, sHouseA.maxX - 4 - sHouseA.minX + 1, sHouseA.Height).Cells.Select(g => OnFloor(g, 1, map)).ToList();
                    List<IntVec3> past = Enumerable.Range(sHouseA.minZ, sHouseA.Height).Select(z => OnFloor(new IntVec3(sHouseA.minX - 1, 0, z), 1, map)).ToList();
                    List<Thing> placed = PlannedOn(map, uFloorCells, PZDefOf.PZ_UpperFloor);
                    List<Thing> wrong = PlannedOn(map, past, PZDefOf.PZ_UpperFloor);
                    List<Thing> ground = PlannedOn(map, new CellRect(sHouseA.minX - 1, sHouseA.minZ, sHouseA.Width + 1, sHouseA.Height).Cells, PZDefOf.PZ_UpperFloor);
                    Result("dragged upper floor: blueprints on every open cell over the room on floor 2, none past the roof, none on the ground",
                        placed.Count == uFloorCells.Count && wrong.Count == 0 && ground.Count == 0,
                        $"{placed.Count}/{uFloorCells.Count} placed, {wrong.Count} past the roof, {ground.Count} on the ground; drag had {drag.Count} cells");
                    Wait(20, "u_bp_shot");
                    return true;
                }
                case "u_bp_shot":
                    Capture("floorui_blueprints");
                    TickUntil(60000, () => uFloorCells.All(c => c.GetTerrain(map) == PZDefOf.PZ_UpperFloor), "u_built", 250);
                    return true;
                case "u_built":
                {
                    tickWatch = null;
                    int built = uFloorCells.Count(c => c.GetTerrain(map) == PZDefOf.PZ_UpperFloor);
                    Result("colonists build the dragged upper floor", built == uFloorCells.Count, $"{built}/{uFloorCells.Count} built after {ticksTotal} ticks");
                    ViewUp(map, sHouseA.CenterCell, 9f);
                    Wait(30, "u_built_shot");
                    return true;
                }
                case "u_built_shot":
                {
                    Capture("floorui_built");
                    // (the next change waits for the shot: a change in this update would show in it)
                    Wait(2, "u_built_next");
                    return true;
                }
                case "u_built_next":
                {
                    // 5. a wall line and a bed on the new floor
                    Find.DesignatorManager.Select(uWall);
                    IntVec3 w0 = new IntVec3(sHouseA.minX + 1, 0, sHouseA.minZ + 1);
                    IntVec3 w1 = new IntVec3(sHouseA.minX + 3, 0, sHouseA.minZ + 1);
                    UiDesignate(ScreenOf(w0, 1), ScreenOf(w1, 1));
                    uWallCells = Enumerable.Range(w0.x, 3).Select(x => OnFloor(new IntVec3(x, 0, w0.z), 1, map)).ToList();
                    Find.DesignatorManager.Select(uBed);
                    IntVec3 b = new IntVec3(sHouseA.minX + 2, 0, sHouseA.minZ + 4);
                    uBedCell = OnFloor(b, 1, map);
                    AcceptanceReport br = UiDesignate(ScreenOf(b, 1), ScreenOf(b, 1));
                    Find.DesignatorManager.Deselect();
                    List<Thing> walls = PlannedOn(map, uWallCells, ThingDefOf.Wall);
                    List<Thing> beds = PlannedOn(map, GenAdj.OccupiedRect(uBedCell, Rot4.North, ThingDefOf.Bed.size).Cells.Concat(GenAdj.OccupiedRect(uBedCell, Rot4.South, ThingDefOf.Bed.size).Cells), ThingDefOf.Bed).Distinct().ToList();
                    Result("viewing floor 2: a wall drag and a bed click on the new floor place their blueprints there",
                        walls.Count == 3 && beds.Count == 1 && beds.All(t => ProjectZ.FloorOf(t.Position, map) == 1),
                        $"walls {walls.Count}/3, bed {beds.Count} at {beds.FirstOrDefault()?.Position} (clicked {uBedCell}, '{br.Reason}')");
                    TickUntil(40000, () => uWallCells.All(c => c.GetEdifice(map)?.def == ThingDefOf.Wall) && map.listerThings.ThingsOfDef(ThingDefOf.Bed).Any(t => ProjectZ.FloorOf(t.Position, map) == 1 && sHouseA.Contains(ProjectZ.GroundCellOf(t.Position, map))), "u_furn_built", 250);
                    return true;
                }
                case "u_furn_built":
                {
                    tickWatch = null;
                    int walls = uWallCells.Count(c => c.GetEdifice(map)?.def == ThingDefOf.Wall);
                    Thing bedT = map.listerThings.ThingsOfDef(ThingDefOf.Bed).FirstOrDefault(t => ProjectZ.FloorOf(t.Position, map) == 1 && sHouseA.Contains(ProjectZ.GroundCellOf(t.Position, map)));
                    Result("colonists build the wall and the bed upstairs", walls == 3 && bedT != null, $"walls {walls}/3, bed {bedT?.Position}, after {ticksTotal} ticks");
                    // zone and home area on floor 2
                    Designator zone = AllDesignators().OfType<Designator_ZoneAddStockpile_Resources>().FirstOrDefault();
                    Designator home = AllDesignators().OfType<Designator_AreaHomeExpand>().FirstOrDefault();
                    IntVec3 z0 = new IntVec3(sHouseA.minX + 3, 0, sHouseA.minZ + 3);
                    IntVec3 z1 = new IntVec3(sHouseA.minX + 4, 0, sHouseA.minZ + 4);
                    List<IntVec3> zoneCells = new CellRect(z0.x, z0.z, 2, 2).Cells.Select(g => OnFloor(g, 1, map)).ToList();
                    CellRect homeRect = new CellRect(sHouseA.minX, sHouseA.minZ, sHouseA.Width, sHouseA.Height);
                    List<IntVec3> homeCells = homeRect.Cells.Select(g => OnFloor(g, 1, map)).ToList();
                    if (zone != null)
                    {
                        Find.DesignatorManager.Select(zone);
                        UiDesignate(ScreenOf(z0, 1), ScreenOf(z1, 1));
                    }
                    // (building and zoning set the home area round them: clear it first, with the home-clear designator)
                    Designator homeClear = AllDesignators().OfType<Designator_AreaHomeClear>().FirstOrDefault();
                    Vector2 hFrom = ScreenOf(new IntVec3(homeRect.minX, 0, homeRect.minZ), 1), hTo = ScreenOf(new IntVec3(homeRect.maxX, 0, homeRect.maxZ), 1);
                    if (homeClear != null)
                    {
                        Find.DesignatorManager.Select(homeClear);
                        UiDesignate(hFrom, hTo);
                    }
                    int homeBefore = homeCells.Count(c => map.areaManager.Home[c]);
                    int groundHome = homeRect.Cells.Count(c => map.areaManager.Home[c]);
                    Info($"  home on floor 2 before the expand drag: {homeCells.Count(c => map.areaManager.Home[c])}; expand accepts the first cell: {home?.CanDesignateCell(homeCells[0]).Accepted}");
                    if (home != null)
                    {
                        Find.DesignatorManager.Select(home);
                        UiDesignate(hFrom, hTo);
                    }
                    Find.DesignatorManager.Deselect();
                    int zoned = zoneCells.Count(c => map.zoneManager.ZoneAt(c) is Zone_Stockpile);
                    int homed = homeCells.Count(c => map.areaManager.Home[c]);
                    Result("viewing floor 2: stockpile and home-area drags (clear, then expand) work on floor 2's cells only", zone != null && home != null && zoned == 4 && homeClear != null && homeBefore == 0 && groundHome == homeRect.Area && homed == homeCells.Count,
                        $"stockpile {zoned}/4; home cleared on floor 2 to {homeBefore} (ground below kept {groundHome}/{homeRect.Area}), then set {homed}/{homeCells.Count}");
                    ViewUp(map, sHouseA.CenterCell, 9f);
                    Wait(30, "u_furn_shot");
                    return true;
                }
                case "u_furn_shot":
                {
                    Capture("floorui_furniture");
                    // (the next change waits for the shot: a change in this update would show in it)
                    Wait(2, "u_furniture_next");
                    return true;
                }
                case "u_furniture_next":
                {
                    // 6. no designator: a drafted move order on open air goes to the ground
                    uMover = Colonists(map).FirstOrDefault(p => !p.Downed);
                    IntVec3 g = new IntVec3(sHouseA.minX - 3, 0, sHouseA.minZ + 3);
                    Vector3 pos = UI.UIToMapPosition(ScreenOf(g, 1) / Prefs.UIScale);
                    IntVec3 cell = pos.ToIntVec3();
                    Draft(uMover, true);
                    List<FloatMenuOption> opts = FloatMenuMakerMap.GetOptions(new List<Pawn> { uMover }, pos, out FloatMenuContext ctx);
                    FloatMenuOption go = opts.FirstOrDefault(o => o.isGoto && !o.Disabled);
                    go?.Chosen(true, null);
                    clickWant = uMover.CurJob != null && uMover.CurJob.def == JobDefOf.Goto ? uMover.CurJob.targetA.Cell : cell;
                    Result("no designator: a drafted move order on open air upstairs goes to the ground", go != null && ProjectZ.FloorOf(clickWant, map) == 0,
                        $"pointer {cell} (floor {ProjectZ.FloorOf(cell, map) + 1}), job -> {clickWant}; options {string.Join(", ", opts.Select(o => o.Label))}");
                    TickUntil(4000, () => uMover.Position == clickWant, "u_moved", 10);
                    return true;
                }
                case "u_moved":
                {
                    tickWatch = null;
                    Result("the drafted colonist gets to that ground cell", uMover.Position == clickWant, $"{uMover} at {uMover.Position}, wanted {clickWant}");
                    Draft(uMover, false);
                    // 7. language shots: the inspect pane and floor button, the architect menu, the mod settings
                    ViewUp(map, sHouseA.CenterCell, 9f);
                    Building_Stairs st = StairsA(map);
                    if (st != null)
                    {
                        Find.Selector.Select(st);
                    }
                    Wait(30, "u_inspect_shot");
                    return true;
                }
                case "u_inspect_shot":
                {
                    Capture("floorui_inspect");
                    // (the next change waits for the shot: a change in this update would show in it)
                    Wait(2, "u_inspect_next");
                    return true;
                }
                case "u_inspect_next":
                {
                    Find.Selector.ClearSelection();
                    try
                    {
                        Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Architect, false);
                        MainTabWindow_Architect arch = (MainTabWindow_Architect)MainButtonDefOf.Architect.TabWindow;
                        List<ArchitectCategoryTab> tabs = Traverse.Create(arch).Field("desPanelsCached").GetValue<List<ArchitectCategoryTab>>();
                        arch.selectedDesPanel = tabs?.FirstOrDefault(t => t.def == PZDefOf.PZ_UpperFloor.designationCategory);
                    }
                    catch (Exception e)
                    {
                        Info("architect menu: " + e.Message);
                    }
                    Wait(30, "u_arch_shot");
                    return true;
                }
                case "u_arch_shot":
                    Capture("floorui_architect");
                    Wait(2, "u_architect_next");
                    return true;
                case "u_architect_next":
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    Find.WindowStack.Add(new Dialog_ModSettings(LoadedModManager.GetMod<ProjectZMod>()));
                    Wait(30, "u_settings_shot");
                    return true;
                case "u_settings_shot":
                    Capture("floorui_settings");
                    stage = "u_jump";
                    return true;
                // 8. camera jumps to a colonist upstairs (playtest bug: the camera went to the raw cell north of the map)
                case "u_jump":
                {
                    Find.WindowStack.TryRemove(typeof(Dialog_ModSettings), false);
                    IntVec3 up = UpInner(map, sHouseA).OrderBy(c => map.zoneManager.ZoneAt(c) == null ? 0 : 1).ThenBy(c => c.x).First();
                    Teleport(uMover, up);
                    Draft(uMover, true);
                    Info($"jump target: {uMover} at {uMover.Position} (floor {ProjectZ.FloorOf(uMover.Position, map) + 1}); smooth camera jumps {Prefs.SmoothCameraJumps}");
                    JumpAway(map);
                    Wait(30, "u_jump_before");
                    return true;
                }
                case "u_jump_before":
                {
                    Capture("floorui_jump_before");
                    // (the next change waits for the shot: a change in this update would show in it)
                    Wait(2, "u_jump_before_next");
                    return true;
                }
                case "u_jump_before_next":
                {
                    // the colonist bar's double-click, through the bar's own click handling (a double-click event over
                    // the colonist's portrait)
                    guiAction = () =>
                    {
                        Event old = Event.current;
                        try
                        {
                            ColonistBar bar = Find.ColonistBar;
                            int i = bar.Entries.FindIndex(e => e.pawn == uMover);
                            Rect r = new Rect(bar.DrawLocs[i].x, bar.DrawLocs[i].y, bar.Size.x, bar.Size.y);
                            Event e2 = new Event { type = EventType.MouseDown, button = 0, clickCount = 2, mousePosition = r.center };
                            Event.current = e2;
                            bar.ColonistBarOnGUI();
                            Info($"colonist bar double-click at {r.center} (portrait {i}), event used {e2.type == EventType.Used}");
                        }
                        catch (Exception ex)
                        {
                            Result("colonist bar double-click", false, ex.ToString());
                        }
                        finally
                        {
                            Event.current = old;
                        }
                    };
                    Wait(90, "u_jump_bar");
                    return true;
                }
                case "u_jump_bar":
                    JumpCheck(map, "colonist bar double-click");
                    Capture("floorui_jump_bar");
                    stage = "u_jump_letter";
                    return true;
                case "u_jump_letter":
                {
                    JumpAway(map);
                    ChoiceLetter letter = LetterMaker.MakeLetter("Test", "Test", LetterDefOf.NeutralEvent, new LookTargets(uMover));
                    Find.LetterStack.ReceiveLetter(letter);
                    DiaOption jump = letter.Choices.FirstOrDefault(o => Traverse.Create(o).Field("text").GetValue<string>() == "JumpToLocation".Translate());
                    Result("letter has its 'jump to' option", jump != null && !jump.disabled, string.Join(", ", letter.Choices.Select(o => Traverse.Create(o).Field("text").GetValue<string>())));
                    smoothWas = Prefs.SmoothCameraJumps;
                    Prefs.SmoothCameraJumps = true;
                    jump?.action?.Invoke();
                    Info($"letter jump with smooth camera jumps on: panning {Traverse.Create(Find.CameraDriver).Field("panner").Field("Moving").GetValue<bool>()}");
                    Wait(90, "u_jump_letter_check");
                    return true;
                }
                case "u_jump_letter_check":
                    JumpCheck(map, "letter 'jump to' (pan)");
                    Prefs.SmoothCameraJumps = smoothWas;
                    Capture("floorui_jump_letter");
                    stage = "u_jump_select";
                    return true;
                case "u_jump_select":
                    // select-and-jump (alerts, the colonist bar's group frame, search results): cut, as with smooth jumps off
                    JumpAway(map);
                    Find.Selector.ClearSelection();
                    CameraJumper.TryJumpAndSelect(uMover, CameraJumper.MovementMode.Cut);
                    Wait(30, "u_jump_select_check");
                    return true;
                case "u_jump_select_check":
                {
                    JumpCheck(map, "select and jump (cut)");
                    Result("select and jump: the colonist is selected", Find.Selector.IsSelected(uMover), $"{Find.Selector.SelectedObjects.Count} selected");
                    Capture("floorui_jump_select");
                    // (the next change waits for the shot: a change in this update would show in it)
                    Wait(2, "u_jump_select_next");
                    return true;
                }
                case "u_jump_select_next":
                {
                    // and back to a target on the ground: the ground is shown
                    uGround = Colonists(map).FirstOrDefault(p => p != uMover && ProjectZ.FloorOf(p.Position, map) == 0);
                    Find.Selector.ClearSelection();
                    if (uGround != null)
                    {
                        CameraJumper.TryJump(uGround, CameraJumper.MovementMode.Cut);
                    }
                    Wait(30, "u_jump_ground");
                    return true;
                }
                case "u_jump_ground":
                    if (uGround != null)
                    {
                        JumpCheck(map, "jump to a colonist on the ground (cut)", uGround);
                    }
                    Draft(uMover, false);
                    stage = "u_end";
                    return true;
                case "u_end":
                    Find.WindowStack.TryRemove(typeof(Dialog_ModSettings), false);
                    Finish();
                    return true;
            }
            return false;
        }

        internal static Action guiAction;
        private static bool smoothWas;
        private static Pawn uGround;

        // View the ground, the camera far from the target.
        private static void JumpAway(Map map)
        {
            FloorView.Set(0);
            Find.Selector.ClearSelection();
            Find.CameraDriver.SetRootPosAndSize(new Vector3(farGround.x - 25.5f, 0f, farGround.z - 15.5f), 9f);
        }

        // After a jump to uMover upstairs: floor 2 shown, the camera on the ground rows, and the colonist drawn near the
        // middle of the screen.
        private static void JumpCheck(Map map, string what, Pawn target = null)
        {
            target = target ?? uMover;
            IntVec3 c = target.Position;
            int k = ProjectZ.FloorOf(c, map);
            IntVec3 g = ProjectZ.GroundCellOf(c, map);
            Vector2 px = IsoRim.View3D.WorldToScreen(new Vector3(c.x + 0.5f, k * IsoRimBridge.StoreyHeight, g.z + 0.5f));
            Vector2 mid = new Vector2(Screen.width / 2f, Screen.height / 2f);
            float off = (px - mid).magnitude / Screen.height;
            IntVec3 cam = Find.CameraDriver.MapPosition;
            Result($"{what}: floor {k + 1} shown, the colonist drawn mid-screen, camera over the ground",
                FloorView.Current == k && off < 0.08f && ProjectZ.FloorOf(cam, map) == 0,
                $"view floor {FloorView.Current + 1}, colonist at {c} drawn at {px} (screen middle {mid}, off {off:0.000} of the height), camera {cam}");
        }
    }
}
