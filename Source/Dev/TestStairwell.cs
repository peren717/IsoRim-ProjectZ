using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Stairs under a laid floor (mode floorui, after the furniture step; StairwellFloor) and a blocked stairs exit
    // (StairGrid.Passable). House A has a floor over all of it by now (west part dragged and built by the colonists).
    //   W1 refusals through the stairs designator: a bed upstairs over the stairwell; a stairwell over another stairs'
    //      exit;
    //   W2 stairs W placed under the floor (designator click): remove-upper-floor designations on its 3 stairwell cells, the
    //      blueprint waits (inspect line, CanConstruct reason); a drafted colonist and an item on the stairwell;
    //   W3 stairs C placed in house B (turned east) and cancelled with the cancel designator: its designations go, the
    //      floor stays;
    //   W4 colonists take the floor up (materials back, nobody and nothing left on open air, stairwell roof below), then
    //      build W; a colonist walks up W;
    //   X1 a wall appears on W's exit while a colonist is on W's ramp heading up: it re-paths over the old stairs;
    //   X2 forbidden doors on both exits: "can't reach" (no enabled goto option, CanReach false); unforbidden, the
    //      colonist sets off, the doors are forbidden again mid-way: the goto ends (no freeze); unforbidden again: it
    //      gets there.
    public static partial class TestRunner
    {
        private static Designator_Build wStairs;
        private static IntVec3 wCenter, cCenter, wExit, wTarget;
        private static Thing wSite, cSite;
        private static Pawn wSitter, wClimber;
        private static int wWoodBefore;
        private static bool wFrameEarly, wOnRamp, wOnOldRamp;
        private static int wStill, wStillMax, wSplicesAt;
        private static IntVec3 wLastPos;
        private static Thing wWall, wDoorW, wDoorOld;

        private static IntVec3 G(int dx, int dz) => new IntVec3(sHouseA.minX + dx, 0, sHouseA.minZ + dz);

        private static List<IntVec3> WellUp(Map map, IntVec3 center, Rot4 rot) => StairShape.Footprint(center, rot).Cells.Select(c => StairShape.Above(c, map)).ToList();

        private static int WoodTotal(Map map) => map.listerThings.ThingsOfDef(ThingDefOf.WoodLog).Sum(t => t.stackCount)
            + map.mapPawns.AllPawnsSpawned.Sum(p => p.carryTracker?.CarriedThing?.def == ThingDefOf.WoodLog ? p.carryTracker.CarriedThing.stackCount : 0);

        private static void SetRot(Designator d, Rot4 rot) => Traverse.Create(d).Field("placingRot").SetValue(rot);

        private static Thing SiteAt(Map map, IntVec3 c) => c.GetThingList(map).FirstOrDefault(t => StairwellFloor.IsSite(t));

        private static bool OnOpenAir(Map map) => map.listerThings.AllThings.Any(t => t.Spawned && (t is Pawn || t.def.category == ThingCategory.Item) && ProjectZ.FloorOf(t.Position, map) >= 1 && t.Position.GetTerrain(map) == PZDefOf.PZ_OpenAir);

        // Freeze watch: ticks the pawn stood on one cell while a goto job is on.
        private static void Watch(Pawn p)
        {
            if (p.CurJobDef == JobDefOf.Goto && p.Position == wLastPos)
            {
                wStill++;
                wStillMax = Mathf.Max(wStillMax, wStill);
            }
            else
            {
                wStill = 0;
            }
            wLastPos = p.Position;
        }

        private static bool rUp;
        private static int rShot;
        private static float rLast, rMaxJump, rBackwards, rMax;
        private static List<IntVec3> rCells = new List<IntVec3>();

        // Height over the ground floor of the climber as drawn (the tween put on its root: the test runs many ticks a frame).
        private static float Absolute(Map map)
        {
            wClimber.Drawer.tweener.ResetTweenedPosToRoot();
            return Links.Rise(wClimber) + ProjectZ.FloorOf(wClimber.Position, map) * IsoRimBridge.StoreyHeight;
        }

        // The climber's drawn position along W's axis (cells from its centre; -9 when on another floor's rows).
        private static float Along(Map map)
        {
            wClimber.Drawer.tweener.ResetTweenedPosToRoot();
            Vector3 d = wClimber.Drawer.tweener.TweenedPos;
            if (ProjectZ.FloorOf(wClimber.Position, map) != 0)
            {
                return -9f;
            }
            return d.z - (wCenter.z + 0.5f);
        }

        private static void RampWatchStart(Map map, bool up)
        {
            rUp = up;
            rCells.Clear();
            rCells.Add(wClimber.Position);
            rLast = Absolute(map);
            rMaxJump = rBackwards = 0f;
            rMax = rLast;
        }

        private static void RampWatch(Map map)
        {
            if (rCells[rCells.Count - 1] != wClimber.Position)
            {
                rCells.Add(wClimber.Position);
            }
            float h = Absolute(map);
            float d = h - rLast;
            rMaxJump = Mathf.Max(rMaxJump, Mathf.Abs(d));
            rBackwards = Mathf.Max(rBackwards, rUp ? -d : d);
            rMax = Mathf.Max(rMax, h);
            rLast = h;
        }

        private static readonly string[] XCases = { "sandbags", "shelf", "pawn", "wall", "wallbp" };
        private static int xCase;
        private static bool xReach;
        private static Pawn xOther;
        private static int xStuckMax;
        private static readonly Dictionary<string, KeyValuePair<string, int>> xStuck = new Dictionary<string, KeyValuePair<string, int>>();
        private static readonly List<Thing> xThings = new List<Thing>();

        private static bool StepStairwell(Map map)
        {
            switch (stage)
            {
                case "w_start":
                {
                    for (int i = 0; i < 3; i++)
                    {
                        Thing t = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                        t.stackCount = t.def.stackLimit;
                        GenPlace.TryPlaceThing(t, farGround, map, ThingPlaceMode.Near);
                    }
                    // (the upstairs wall line from step 5 would hide stairs W from the camera)
                    foreach (IntVec3 c in uWallCells)
                    {
                        c.GetEdifice(map)?.Destroy();
                    }
                    wStairs = BuildCopyCommandUtility.FindAllowedDesignator(PZDefOf.PZ_Stairs);
                    wStairs?.SetStuffDef(ThingDefOf.WoodLog);
                    if (wStairs == null)
                    {
                        Result("stairs designator found", false, "none");
                        stage = "u_furniture_next";
                        return true;
                    }
                    ShotAt(map, 0, sHouseA.CenterCell, 9f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Find.DesignatorManager.Select(wStairs);
                    Wait(5, "w_refuse");
                    return true;
                }
                case "w_refuse":
                {
                    // W1: under the bed upstairs (its cells x = minX + 2, z = minZ + 4, 5)
                    SetRot(wStairs, Rot4.North);
                    AcceptanceReport bed = RefusalAt(wStairs, ScreenOf(G(2, 4), 0), out IntVec3 bc);
                    Result("stairs under a bed upstairs: refused, 'no building or stairs exit over the stairs'", !bed.Accepted && bed.Reason == "PZ_WellNotOpen".Translate() && bc == G(2, 4),
                        $"{bc}: '{bed.Reason}'; stairwell {string.Join(", ", WellUp(map, G(2, 4), Rot4.North).Select(c => c.GetTerrain(map).defName + "/" + (c.GetFirstBuilding(map)?.def.defName ?? "-")))}");
                    Messages.Message(bed.Reason, MessageTypeDefOf.RejectInput, false);
                    // W2: stairs W under the floor, up to the north, x = minX + 5
                    wCenter = G(5, 2);
                    fakeMouse = ScreenOf(wCenter, 0);
                    IntVec3 at = UI.MouseCell();
                    AcceptanceReport ok = wStairs.CanDesignateCell(at);
                    fakeMouse = null;
                    List<IntVec3> well = WellUp(map, wCenter, Rot4.North);
                    Info($"stairs W at {at} (want {wCenter}): '{ok.Reason}' accepted {ok.Accepted}; stairwell {string.Join(", ", well.Select(c => c + " " + c.GetTerrain(map).defName))}");
                    UiDesignate(ScreenOf(wCenter, 0), ScreenOf(wCenter, 0));
                    wSite = SiteAt(map, wCenter);
                    wExit = StairShape.Exit(wCenter, Rot4.North, map);
                    int des = well.Count(c => map.designationManager.DesignationAt(c, DesignationDefOf.RemoveFoundation) != null);
                    StairwellMarks marks = map.GetComponent<StairwellMarks>();
                    Result("stairs placed under a laid floor: blueprint, and the 3 floor cells over it designated for removal (marked as the site's)",
                        ok.Accepted && wSite is Blueprint && des == 3 && well.All(c => marks.cells.Contains(c)) && well.All(c => c.GetTerrain(map) == PZDefOf.PZ_UpperFloor),
                        $"site {wSite} at {wSite?.Position}, designations {des}/3, marks {marks.cells.Count}, exit {wExit}");
                    // W1b: a stairwell over the stairs exits (W's, a blueprint; the old stairs', built): turned west at (minX + 6, minZ + 4)
                    SetRot(wStairs, Rot4.West);
                    AcceptanceReport ex = RefusalAt(wStairs, ScreenOf(G(6, 4), 0), out IntVec3 ec);
                    Result("a stairwell over other stairs' exits: refused the same way", !ex.Accepted && ex.Reason == "PZ_WellNotOpen".Translate(), $"{ec}: '{ex.Reason}'");
                    SetRot(wStairs, Rot4.North);
                    Find.DesignatorManager.Deselect();
                    // waiting: inspect line and the construction check's reason
                    Pawn any = Colonists(map).First(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction));
                    JobFailReason.Clear();
                    bool can = GenConstruct.CanConstruct(wSite, any, WorkTypeDefOf.Construction, forced: true);
                    string insp = wSite?.GetInspectString() ?? "";
                    Result("the blueprint waits: can't be constructed, says why, inspect line", !can && JobFailReason.Reason == StairwellFloor.WaitingLine && insp.Contains(StairwellFloor.WaitingLine),
                        $"CanConstruct {can}, reason '{JobFailReason.Reason}', inspect '{insp.Replace("\n", " | ")}'");
                    // a drafted colonist standing on the stairwell, and an item on it
                    wSitter = Colonists(map).FirstOrDefault(p => p != any) ?? any;
                    Teleport(wSitter, well[0]);
                    Draft(wSitter, true);
                    Thing item = ThingMaker.MakeThing(ThingDefOf.Steel);
                    item.stackCount = 20;
                    GenSpawn.Spawn(item, well[1], map);
                    Info($"{wSitter} drafted on {well[0]}, steel x20 on {well[1]}");
                    // W3: stairs C in house B, turned east, then cancelled
                    ShotAt(map, 0, sHouseB.CenterCell, 9f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Find.DesignatorManager.Select(wStairs);
                    Wait(5, "w_cancel");
                    return true;
                }
                case "w_cancel":
                {
                    cCenter = new IntVec3(sHouseB.minX + 2, 0, sHouseB.minZ + 3);
                    SetRot(wStairs, Rot4.East);
                    UiDesignate(ScreenOf(cCenter, 0), ScreenOf(cCenter, 0));
                    SetRot(wStairs, Rot4.North);
                    cSite = SiteAt(map, cCenter);
                    List<IntVec3> well = WellUp(map, cCenter, Rot4.East);
                    int des = well.Count(c => map.designationManager.DesignationAt(c, DesignationDefOf.RemoveFoundation) != null);
                    Result("stairs turned east under house B's floor: blueprint, 3 designations", cSite != null && cSite.Rotation == Rot4.East && des == 3, $"site {cSite} at {cSite?.Position} rot {cSite?.Rotation}, designations {des}/3 on {string.Join(", ", well)}");
                    Designator cancel = AllDesignators().OfType<Designator_Cancel>().FirstOrDefault();
                    if (cancel != null)
                    {
                        Find.DesignatorManager.Select(cancel);
                        UiDesignate(ScreenOf(cCenter, 0), ScreenOf(cCenter, 0));
                    }
                    Find.DesignatorManager.Deselect();
                    int left = well.Count(c => map.designationManager.DesignationAt(c, DesignationDefOf.RemoveFoundation) != null);
                    StairwellMarks marks = map.GetComponent<StairwellMarks>();
                    Result("cancelled with the cancel designator: its designations go, the floor stays, W's stay",
                        cancel != null && (cSite == null || !cSite.Spawned) && left == 0 && well.All(c => c.GetTerrain(map) == PZDefOf.PZ_UpperFloor && !marks.cells.Contains(c)) && WellUp(map, wCenter, Rot4.North).All(c => map.designationManager.DesignationAt(c, DesignationDefOf.RemoveFoundation) != null),
                        $"cancel {cancel != null}, site spawned {cSite?.Spawned}, designations left {left}, marks {marks.cells.Count}");
                    ViewUp(map, wCenter, 5f);
                    Find.Selector.Select(wSite);
                    Wait(30, "w_designated_shot");
                    return true;
                }
                case "w_designated_shot":
                {
                    CellRect view = Find.CameraDriver.CurrentViewRect;
                    Info($"view rect {view}; stairwell cells in it (expanded by 3): {string.Join(", ", WellUp(map, wCenter, Rot4.North).Select(c => c + " " + view.ExpandedBy(3).Contains(c)))}; designations RemoveFoundation on the map {map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.RemoveFoundation).Count()}");
                    Capture("stairwell_designated");
                }
                    Wait(2, "w_remove");
                    return true;
                case "w_remove":
                {
                    Find.Selector.ClearSelection();
                    wWoodBefore = WoodTotal(map);
                    wFrameEarly = false;
                    List<IntVec3> well = WellUp(map, wCenter, Rot4.North);
                    TickUntil(30000, () =>
                    {
                        bool open = well.All(c => c.GetTerrain(map) == PZDefOf.PZ_OpenAir);
                        if (!open && wCenter.GetThingList(map).Any(t => t is Frame || t is Building_Stairs))
                        {
                            wFrameEarly = true;
                        }
                        return open;
                    }, "w_removed", 5);
                    return true;
                }
                case "w_removed":
                {
                    tickWatch = null;
                    List<IntVec3> well = WellUp(map, wCenter, Rot4.North);
                    int wood = WoodTotal(map) - wWoodBefore;
                    bool roofs = StairShape.Footprint(wCenter, Rot4.North).Cells.All(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_StairwellRoof);
                    Result("colonists take the floor up: open air over the stairs, stairwell roof below, materials back, no frame before",
                        well.All(c => c.GetTerrain(map) == PZDefOf.PZ_OpenAir) && roofs && wood > 0 && !wFrameEarly,
                        $"after {ticksTotal} ticks; terrain {string.Join(",", well.Select(c => c.GetTerrain(map).defName))}; roofs below {string.Join(",", StairShape.Footprint(wCenter, Rot4.North).Cells.Select(c => map.roofGrid.RoofAt(c)?.defName ?? "none"))}; wood +{wood}; frame early {wFrameEarly}");
                    Result("nobody and nothing left on open air; the drafted colonist stepped to a floor next to it",
                        !OnOpenAir(map) && wSitter.Position.Standable(map) && ProjectZ.FloorOf(wSitter.Position, map) == 1 && map.listerThings.ThingsOfDef(ThingDefOf.Steel).Any(t => t.stackCount >= 20 && ProjectZ.FloorOf(t.Position, map) == 1),
                        $"{wSitter} at {wSitter.Position} ({wSitter.Position.GetTerrain(map).defName}); steel {string.Join(", ", map.listerThings.ThingsOfDef(ThingDefOf.Steel).Where(t => ProjectZ.FloorOf(t.Position, map) == 1).Select(t => t.Position + " x" + t.stackCount))}");
                    Draft(wSitter, false);
                    ViewUp(map, wCenter, 5f);
                    Wait(30, "w_removed_shot");
                    return true;
                }
                case "w_removed_shot":
                    Capture("stairwell_removed");
                    Wait(2, "w_build");
                    return true;
                case "w_build":
                    TickUntil(40000, () => wCenter.GetFirstThing<Building_Stairs>(map) != null, "w_built", 50);
                    return true;
                case "w_built":
                {
                    tickWatch = null;
                    Building_Stairs st = wCenter.GetFirstThing<Building_Stairs>(map);
                    StairGrid g = StairGrid.For(map);
                    Result("then the stairs get built: link registered, stairwell roofs kept, marks cleared",
                        st != null && g != null && g.links.Any(l => l.stairs == st && l.top == wExit) && StairShape.Footprint(wCenter, Rot4.North).Cells.All(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_StairwellRoof) && map.GetComponent<StairwellMarks>().cells.Count == 0,
                        $"{st} after {ticksTotal} ticks; links {g?.Count}; marks {map.GetComponent<StairwellMarks>().cells.Count}");
                    // a colonist walks up W: from next to its low end to its exit
                    wClimber = Colonists(map).First(p => !p.Downed);
                    Teleport(wClimber, G(4, 1));
                    wOnRamp = false;
                    GoTo(wClimber, wExit);
                    TickUntil(3000, () =>
                    {
                        if (wClimber.Position == st.HighCell)
                        {
                            wOnRamp = true;
                        }
                        return wClimber.Position == wExit;
                    }, "w_climbed", 1);
                    return true;
                }
                case "w_climbed":
                {
                    tickWatch = null;
                    Result("a colonist climbs the new stairs", wClimber.Position == wExit && wOnRamp, $"{wClimber} at {wClimber.Position} (exit {wExit}), over W's top step {wOnRamp}, after {ticksTotal} ticks");
                    ViewUp(map, wCenter, 5f);
                    Wait(30, "w_built_shot");
                    return true;
                }
                case "w_built_shot":
                    Capture("stairwell_built");
                    Wait(2, "x_start");
                    return true;

                // ---- X1: a wall on W's exit while a colonist is on W's ramp heading up
                case "x_start":
                {
                    Draft(wClimber, false);
                    wTarget = OnFloor(G(5, 5), 1, map);
                    Teleport(wClimber, G(4, 1));
                    GoTo(wClimber, wTarget);
                    wOnOldRamp = false;
                    TickUntil(2000, () => StairShape.Footprint(wCenter, Rot4.North).Contains(wClimber.Position), "x_wall", 1);
                    return true;
                }
                case "x_wall":
                {
                    tickWatch = null;
                    Info($"{wClimber} on W's ramp at {wClimber.Position}, job {wClimber.CurJobDef?.defName}, heading to {wTarget}: a wall appears on W's exit {wExit}");
                    wWall = Place(ThingDefOf.Wall, ThingDefOf.WoodLog, wExit, map);
                    wStill = wStillMax = 0;
                    wLastPos = wClimber.Position;
                    wSplicesAt = PZStats.splices;
                    Building_Stairs old = StairsA(map);
                    TickUntil(3000, () =>
                    {
                        Watch(wClimber);
                        if (old != null && wClimber.Position == old.HighCell)
                        {
                            wOnOldRamp = true;
                        }
                        return wClimber.Position == wTarget;
                    }, "x_walled", 1);
                    return true;
                }
                case "x_walled":
                {
                    tickWatch = null;
                    Result("X1 wall on the exit mid-way: the colonist re-paths over the other stairs and gets there (no freeze)",
                        wClimber.Position == wTarget && wOnOldRamp && wStillMax < 120,
                        $"{wClimber} at {wClimber.Position} (target {wTarget}) after {ticksTotal} ticks, over the old stairs {wOnOldRamp}, longest stand {wStillMax} ticks, path requests {PZStats.splices - wSplicesAt}");
                    // X2: forbidden doors on both exits
                    wWall.Destroy();
                    Building_Stairs old = StairsA(map);
                    wDoorW = Place(ThingDefOf.Door, ThingDefOf.WoodLog, wExit, map);
                    wDoorOld = Place(ThingDefOf.Door, ThingDefOf.WoodLog, old.TopCell, map);
                    wDoorW.SetForbidden(true, false);
                    wDoorOld.SetForbidden(true, false);
                    Draft(wClimber, false);
                    Teleport(wClimber, G(4, 1));
                    Draft(wClimber, true);
                    Wait(5, "x_doors");
                    return true;
                }
                case "x_doors":
                {
                    Vector3 pos = wTarget.ToVector3Shifted();
                    List<FloatMenuOption> opts = FloatMenuMakerMap.GetOptions(new List<Pawn> { wClimber }, pos, out FloatMenuContext ctx);
                    bool goOk = opts.Any(o => o.isGoto && !o.Disabled);
                    bool reach = wClimber.CanReach(wTarget, PathEndMode.OnCell, Danger.Deadly);
                    Result("X2 forbidden doors on both exits: can't reach upstairs (no enabled move option)", !goOk && !reach,
                        $"CanReach {reach}; options {string.Join(", ", opts.Select(o => o.Label + (o.Disabled ? " (disabled)" : "")))}");
                    wDoorW.SetForbidden(false, false);
                    wDoorOld.SetForbidden(false, false);
                    GoTo(wClimber, wTarget);
                    TickUntil(2000, () => StairShape.Footprint(wCenter, Rot4.North).Contains(wClimber.Position) || StairShape.Footprint(StairsA(map).Position, StairsA(map).Rotation).Contains(wClimber.Position), "x_forbid", 1);
                    return true;
                }
                case "x_forbid":
                {
                    tickWatch = null;
                    Info($"{wClimber} on a ramp at {wClimber.Position} heading to {wTarget}: both exit doors forbidden again");
                    wDoorW.SetForbidden(true, false);
                    wDoorOld.SetForbidden(true, false);
                    wStill = wStillMax = 0;
                    wLastPos = wClimber.Position;
                    wSplicesAt = PZStats.splices;
                    StartTicks(600, "x_forbidden");
                    tickWatch = () =>
                    {
                        Watch(wClimber);
                        return false;
                    };
                    watchEvery = 1;
                    return true;
                }
                case "x_forbidden":
                {
                    tickWatch = null;
                    int req = PZStats.splices - wSplicesAt;
                    Result("X2 doors forbidden mid-way: the move ends at the stairs instead of a stuck walk (no re-path loop)",
                        wClimber.CurJobDef != JobDefOf.Goto && ProjectZ.FloorOf(wClimber.Position, map) == 0 && req < 30,
                        $"{wClimber} at {wClimber.Position}, job {wClimber.CurJobDef?.defName ?? "none"}, moving {wClimber.pather.Moving}, path requests in 600 ticks {req}, longest stand in a move {wStillMax}");
                    wDoorW.SetForbidden(false, false);
                    wDoorOld.SetForbidden(false, false);
                    GoTo(wClimber, wTarget);
                    TickUntil(3000, () => wClimber.Position == wTarget, "x_recovered", 5);
                    return true;
                }
                case "x_recovered":
                {
                    tickWatch = null;
                    Result("X2 doors allowed again: the colonist gets upstairs", wClimber.Position == wTarget, $"{wClimber} at {wClimber.Position} after {ticksTotal} ticks");
                    wDoorW.Destroy();
                    // X3: other things on W's exit with the old stairs shut (forbidden door): W is the only way up
                    wDoorOld.SetForbidden(true, false);
                    xCase = 0;
                    Wait(5, "x_case");
                    return true;
                }
                case "x_case":
                {
                    if (xCase >= XCases.Length)
                    {
                        wDoorOld.Destroy();
                        Draft(wClimber, false);
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                        Wait(5, "r_start");
                        return true;
                    }
                    xThings.Clear();
                    string what = XCases[xCase];
                    if (what == "sandbags")
                    {
                        xThings.Add(Place(DefDatabase<ThingDef>.GetNamed("Sandbags"), ThingDefOf.Cloth, wExit, map));
                    }
                    else if (what == "shelf")
                    {
                        Thing sh = Place(DefDatabase<ThingDef>.GetNamed("ShelfSmall"), ThingDefOf.WoodLog, wExit, map);
                        xThings.Add(sh);
                        Thing st = ThingMaker.MakeThing(ThingDefOf.Steel);
                        st.stackCount = 30;
                        xThings.Add(GenSpawn.Spawn(st, wExit, map));
                    }
                    else if (what == "pawn")
                    {
                        Pawn o = Colonists(map).First(p => p != wClimber && !p.Downed);
                        Teleport(o, wExit);
                        Draft(o, true);
                        xOther = o;
                    }
                    else if (what == "wall")
                    {
                        xThings.Add(Place(ThingDefOf.Wall, ThingDefOf.WoodLog, wExit, map));
                    }
                    else if (what == "wallbp")
                    {
                        // a wall to be built on the exit (placed without the placement checks): the colonists try
                        Draft(wClimber, false);
                        Teleport(wClimber, G(4, 1));
                        xThings.Add(GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, wExit, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog));
                        xStuck.Clear();
                        TickUntil(5000, () =>
                        {
                            foreach (Pawn p in Colonists(map))
                            {
                                string key = p.ThingID;
                                string now = p.Position + " " + p.CurJobDef?.defName;
                                if (!xStuck.TryGetValue(key, out var v) || v.Key != now)
                                {
                                    xStuck[key] = new KeyValuePair<string, int>(now, 0);
                                }
                                else
                                {
                                    xStuck[key] = new KeyValuePair<string, int>(now, v.Value + 10);
                                    xStuckMax = Mathf.Max(xStuckMax, v.Value + 10);
                                    if (v.Value + 10 == 600)
                                    {
                                        Info($"  stuck 600 ticks: {p} at {p.Position} job {p.CurJobDef?.defName} -> {p.CurJob?.targetA}, path to {p.pather.Destination} moving {p.pather.Moving}");
                                    }
                                }
                            }
                            return wExit.GetEdifice(map)?.def == ThingDefOf.Wall;
                        }, "x_wallbp_end", 10);
                        return true;
                    }
                    Draft(wClimber, false);
                    Teleport(wClimber, G(4, 1));
                    Draft(wClimber, true);
                    bool reach = wClimber.CanReach(wTarget, PathEndMode.OnCell, Danger.Deadly);
                    Info($"X3 {what} on W's exit {wExit}: CanReach {reach}, exit walkable {wExit.Walkable(map)} standable {wExit.Standable(map)}");
                    GoTo(wClimber, wTarget);
                    wStill = wStillMax = 0;
                    wLastPos = wClimber.Position;
                    wSplicesAt = PZStats.splices;
                    xReach = reach;
                    TickUntil(1500, () =>
                    {
                        Watch(wClimber);
                        return wClimber.Position == wTarget || wClimber.CurJobDef != JobDefOf.Goto;
                    }, "x_case_end", 1);
                    return true;
                }
                case "x_wallbp_end":
                {
                    tickWatch = null;
                    Info($"X3 wall blueprint on the exit: built {wExit.GetEdifice(map)?.def == ThingDefOf.Wall} after {ticksTotal} ticks, longest any colonist kept cell and job {xStuckMax}; colonists {string.Join("; ", Colonists(map).Select(p => p + " " + p.Position + " " + p.CurJobDef?.defName))}");
                    foreach (Thing t in xThings.Concat(new[] { wExit.GetEdifice(map) }).Where(t => t != null && t.Spawned).ToList())
                    {
                        t.Destroy();
                    }
                    foreach (Thing t in wExit.GetThingList(map).Where(t => t is Frame || t is Blueprint).ToList())
                    {
                        t.Destroy();
                    }
                    xCase++;
                    Wait(2, "x_case");
                    return true;
                }
                case "x_case_end":
                {
                    tickWatch = null;
                    string what = XCases[xCase];
                    bool there = wClimber.Position == wTarget;
                    int req = PZStats.splices - wSplicesAt;
                    Result($"X3 {what} on the exit: {(xReach ? "gets there" : "can't reach, and doesn't try")}, no freeze",
                        (xReach ? there : wClimber.CurJobDef != JobDefOf.Goto) && wStillMax < 120 && req < 30,
                        $"{wClimber} at {wClimber.Position}, job {wClimber.CurJobDef?.defName ?? "none"}, reach {xReach}, longest stand in a move {wStillMax}, path requests {req}, after {ticksTotal} ticks");
                    foreach (Thing t in xThings)
                    {
                        if (t.Spawned)
                        {
                            t.Destroy();
                        }
                    }
                    if (xOther != null)
                    {
                        Draft(xOther, false);
                        xOther = null;
                    }
                    xCase++;
                    Wait(2, "x_case");
                    return true;
                }

                // ---- R: up and down W along the ramp, the height following the drawn position (Links.Rise)
                case "r_start":
                {
                    Teleport(wClimber, G(4, 1));
                    RampWatchStart(map, up: true);
                    GoTo(wClimber, wExit);
                    rShot = 0;
                    Wait(2, "r_go");
                    return true;
                }
                case "r_go":
                {
                    // to the next shot point (drawn on step 1, 2, 3) or the end of the climb
                    float[] at = rUp ? new[] { -1f, 0f, 1f } : new[] { 1f, 0f, -1f };
                    IntVec3 end = rUp ? wExit : G(4, 1);
                    if (rShot < 3)
                    {
                        float want = at[rShot];
                        TickUntil(2000, () =>
                        {
                            RampWatch(map);
                            float a2 = Along(map);
                            return StairShape.Footprint(wCenter, Rot4.North).Contains(wClimber.Position) && (rUp ? a2 >= want : a2 <= want && a2 > -9f);
                        }, "r_shot", 1);
                    }
                    else
                    {
                        TickUntil(2000, () =>
                        {
                            RampWatch(map);
                            return wClimber.Position == end;
                        }, "r_end", 1);
                    }
                    return true;
                }
                case "r_shot":
                {
                    tickWatch = null;
                    ShotAt(map, 1, ProjectZ.GroundCellOf(wCenter, map), 5f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    wClimber.Drawer.tweener.ResetTweenedPosToRoot();
                    Info($"ramp {(rUp ? "up" : "down")} shot {rShot + 1}: {wClimber} at {wClimber.Position} -> {wClimber.pather.nextCell} {wClimber.pather.MovePercentage:P0}, along {Along(map):0.00}, rise {Links.Rise(wClimber):0.000}");
                    Wait(30, "r_shot_take");
                    return true;
                }
                case "r_shot_take":
                    Capture($"stairwell_ramp_{(rUp ? "up" : "down")}{rShot + 1}");
                    rShot++;
                    Wait(2, "r_go");
                    return true;
                case "r_end":
                {
                    tickWatch = null;
                    IntVec3 low = wCenter - Rot4.North.FacingCell, high = wCenter + Rot4.North.FacingCell;
                    List<IntVec3> want = rUp ? new List<IntVec3> { low, wCenter, high, wExit } : new List<IntVec3> { wExit, high, wCenter, low };
                    int idx = 0;
                    foreach (IntVec3 c in rCells)
                    {
                        if (idx < want.Count && c == want[idx])
                        {
                            idx++;
                        }
                    }
                    float storey = IsoRimBridge.StoreyHeight;
                    Result($"R ramp {(rUp ? "up" : "down")}: walks {(rUp ? "L0, L1, H, then the exit" : "the exit, H, L1, L0")}, height {(rUp ? "rising" : "falling")} smoothly with the drawn position",
                        wClimber.Position == (rUp ? wExit : G(4, 1)) && idx == want.Count && rMaxJump < 0.1f * storey && rBackwards < 0.02f * storey && rMax > 0.95f * storey,
                        $"cells {string.Join(" ", rCells)}; largest change in one tick {rMaxJump / storey:0.000} storey, against the way {rBackwards / storey:0.000}, top {rMax / storey:0.00} storey, after {ticksTotal} ticks");
                    if (rUp)
                    {
                        RampWatchStart(map, up: false);
                        GoTo(wClimber, G(4, 1));
                        rShot = 0;
                        Wait(2, "r_go");
                        return true;
                    }
                    Draft(wClimber, false);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    // back to the floor UI flow
                    Wait(5, "u_furniture_next");
                    return true;
                }
            }
            return false;
        }
    }
}
