using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode elevtop (the elevator's top floor setting, Elevator.cs; user 2026-11-01). Run with -projectzfloors=5
    // -projectzseed=7. Fixtures as mode elevator's (ElFixture: platforms on levels 1-4 over ground x 10..18, z 8..19, the
    // elevator at (19, 13) turned west, stops (18, 13)); each check builds its own under its own random state, real
    // orders and jobs with real ticks.
    //   LO  the gizmo (label, menu of floors 2..5) lowers a built elevator to level 2: 3 links, the shaft's reservation and
    //       roof end there; a colonist lays a floor in the freed shaft cell on level 3 (a construction job, no cap message),
    //       an item can lie on it; level 4 unreachable, level 3 reached over stairs only (rides end at level 2); shots
    //       (3D, gizmo, menu)
    //   RB  raising it to the top while that floor is there: refused with the message, setting unchanged (shot)
    //   RA  a colonist takes the floor up; raised to the top: 10 links, the shaft roofed again, a ride to level 4
    //   PW  lowered to level 2, power cut: the emergency ladder links only up to level 2; a colonist climbs to level 2
    //   BP  a blueprint under a 4-floor building: all floors in the shaft marked, its gizmo set to Floor 2 takes the marks
    //       on levels 2-4 back; a second blueprint under a wall on level 3 gets top level 2 by itself (cancelled: marks
    //       gone); colonists take the level-1 floor up and build it (the frame carries the setting and its gizmo): it
    //       serves levels 0-1, the floors above stay, a colonist rides it
    //   SV  saved and loaded: the elevator's setting and a blueprint's kept; OLD the same save without the new field and
    //       component (as from before): all the way up
    public static partial class TestRunner
    {
        private static Thing etBp;
        private static int etInt, etInt2, etAuto;
        private static bool etFrameSeen, etFrameGizmo;
        private static int etFrameTop = -99;
        private static FloatMenu etMenu;

        private static bool StepElevTop(Map map)
        {
            Rand.PushState((int)(StableHash("elevtop_" + stage) ^ (uint)(SeedArg ?? 717)));
            try
            {
                return StepElevTopInner(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static string EtTopLabel(int level) => "PZ_ElevatorTop".Translate(FloorView.Label(level));

        private static Command_Action EtGizmo(Thing t) => t.GetGizmos().OfType<Command_Action>().FirstOrDefault(c => c.defaultLabel != null && c.defaultLabel.StartsWith("PZ_ElevatorTop".Translate("").ToString().Trim()));

        private static void EtWork(Pawn p, bool on)
        {
            Draft(p, !on);
            foreach (WorkTypeDef wt in DefDatabase<WorkTypeDef>.AllDefs)
            {
                if (!p.WorkTypeIsDisabled(wt))
                {
                    p.workSettings.SetPriority(wt, on ? (wt == WorkTypeDefOf.Construction || wt == WorkTypeDefOf.Hauling ? 1 : 0) : 3);
                }
            }
        }

        private static List<string> EtMessages()
        {
            List<Message> live = AccessTools.Field(typeof(Messages), "liveMessages").GetValue(null) as List<Message>;
            return live?.Select(m => m.text).ToList() ?? new List<string>();
        }

        private static bool EtMarked(Map map, IntVec3 c) => map.designationManager.DesignationAt(c, DesignationDefOf.RemoveFoundation) != null;

        private static bool StepElevTopInner(Map map)
        {
            switch (stage)
            {
                case "et_setup":
                    ElSetup(map);
                    if (stage != "done")
                    {
                        stage = "et_lo";
                    }
                    return true;

                // ------------------------------------------------------------------------------------ LO: lowered
                case "et_lo":
                    ElFixture(map, "et_lo_go", true, 4, r2P);
                    Heal(r2P);
                    Teleport(r2P, ElG(15, 13));
                    Draft(r2P, true);
                    // a wall on level 2 holds a floor laid in the shaft cell on level 3 (the slab support rule); wood there
                    R2Wall(map, ElL(17, 10, 2, map));
                    Thing wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                    wood.stackCount = 20;
                    GenPlace.TryPlaceThing(wood, ElL(16, 16, 3, map), map, ThingPlaceMode.Direct);
                    // stairs up to level 3 on ledges outside the stack (as mode elevator's RF): ground -> 1 at (8, 10); a
                    // level-1 ledge x 5..9, z 14..18 with stairs to level 2 at (7, 16); a level-2 ledge x 10..18, z 3..7 with
                    // stairs to level 3 at (14, 5). Level 4 has none.
                    R2Floor(map, new CellRect(ElG(5, 14).x, ElG(5, 14).z, 5, 5), 1);
                    R2Floor(map, new CellRect(ElG(10, 3).x, ElG(10, 3).z, 9, 5), 2);
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    elStairs = R2Stairs(map, ElG(8, 10), Rot4.East);
                    Building_Stairs s12 = R2Stairs(map, ElL(7, 16, 1, map), Rot4.East);
                    Building_Stairs s23 = R2Stairs(map, ElL(14, 5, 2, map), Rot4.North);
                    Info($"LO stairs {elStairs?.Position}, {s12?.Position}, {s23?.Position}");
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    return true;
                case "et_lo_go":
                {
                    tickWatch = null;
                    Command_Action g = EtGizmo(elE);
                    List<FloatMenuOption> opts = ElevatorRule.TopOptions(elE, 0, 4);
                    Result("LO the elevator has the top floor button, 'Top floor: Floor 5' by default; its menu lists floors 2 to 5",
                        g != null && g.defaultLabel == EtTopLabel(4) && opts.Count == 4 && opts[0].Label == FloorView.Label(1) && opts[3].Label == FloorView.Label(4) && elE.TopSetting == Building_Elevator.TopAll && elE.LinkedTop == 4,
                        $"gizmo '{g?.defaultLabel}', menu [{string.Join(", ", opts.Select(o => o.Label))}], setting {elE.TopSetting}, {ElLinks(map)}");
                    etInt = ElevatorRule.TopChanged;
                    opts[1].action();   // Floor 3 = level 2
                    IntVec3 s3 = ElL(19, 13, 3, map), s4 = ElL(19, 13, 4, map), s2 = ElL(19, 13, 2, map);
                    List<VLink> ls = StairGrid.For(map).links.Where(l => l.thing == elE).ToList();
                    Command_Action g2 = EtGizmo(elE);
                    Result("LO picked Floor 3: the elevator serves levels 0-2 (3 links), the shaft's reservation and roof end at level 2 (lid roof on level 2, none above), the button says Floor 3",
                        ElevatorRule.TopChanged == etInt + 1 && elE.TopSetting == 2 && elE.LinkedTop == 2 && ls.Count == 3 && ls.All(l => Levels.LevelOf(l.top, map) <= 2)
                        && !LadderColumn.Reserved(s3, map) && !LadderColumn.Reserved(s4, map) && LadderColumn.Reserved(s2, map)
                        && map.roofGrid.RoofAt(s2) == PZDefOf.PZ_StairwellRoof && map.roofGrid.RoofAt(s3) == null && map.roofGrid.RoofAt(s4) == null && g2?.defaultLabel == EtTopLabel(2),
                        $"{ElLinks(map)}, reserved L2 {LadderColumn.Reserved(s2, map)} L3 {LadderColumn.Reserved(s3, map)} L4 {LadderColumn.Reserved(s4, map)}, roofs L2 {map.roofGrid.RoofAt(s2)?.defName} L3 {map.roofGrid.RoofAt(s3)?.defName ?? "none"} L4 {map.roofGrid.RoofAt(s4)?.defName ?? "none"}, gizmo '{g2?.defaultLabel}'");
                    // a floor planned in the freed cell on level 3: allowed, no cap message (not in the shaft); laid by a colonist
                    etInt2 = ElevatorCap.Shown;
                    AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_UpperFloor, s3, Rot4.North, map);
                    Blueprint bp = ok.Accepted ? GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_UpperFloor, s3, map, Rot4.North, Faction.OfPlayer, null) : null;
                    elNote = $"floor placement {ok.Accepted} ({ok.Reason}), blueprint {bp != null}, cap message {ElevatorCap.Shown > etInt2}";
                    elFlag = bp != null && ElevatorCap.Shown == etInt2;
                    Teleport(r2P, ElL(14, 13, 3, map));
                    EtWork(r2P, true);
                    SeedTicks("et_lo_build");
                    TickUntil(10000, () => ProjectZ.HasFloor(s3, map), "et_lo_built", 10);
                    return true;
                }
                case "et_lo_built":
                {
                    tickWatch = null;
                    IntVec3 s3 = ElL(19, 13, 3, map);
                    Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    steel.stackCount = 5;
                    bool placed = GenPlace.TryPlaceThing(steel, s3, map, ThingPlaceMode.Direct) && steel.Spawned && steel.Position == s3;
                    Result("LO a colonist lays a floor in the freed shaft cell on level 3 (a construction job; no cap message), an item can lie on it; the elevator still serves 0-2",
                        elFlag && ProjectZ.HasFloor(s3, map) && placed && elE.LinkedTop == 2 && StairGrid.For(map).links.Count(l => l.thing == elE) == 3,
                        $"{elNote}; floor {ProjectZ.HasFloor(s3, map)}, item placed {placed}, roof under it {map.roofGrid.RoofAt(ElL(19, 13, 2, map))?.defName}, {ElLinks(map)}");
                    steel.Destroy();
                    EtWork(r2P, false);
                    Teleport(r2P, ElG(15, 13));
                    Draft(r2P, true);
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    bool r2 = r2P.CanReach(ElL(14, 13, 2, map), PathEndMode.OnCell, Danger.Deadly);
                    bool r3 = r2P.CanReach(ElL(14, 13, 3, map), PathEndMode.OnCell, Danger.Deadly);
                    bool r4 = r2P.CanReach(ElL(14, 13, 4, map), PathEndMode.OnCell, Danger.Deadly);
                    elNote = $"reach L2 {r2}, L3 {r3}, L4 {r4}";
                    elFlag = r2 && r3 && !r4;
                    elPawns.Clear();
                    elPawns.Add(r2P);
                    ElResetRides();
                    elDest = ElL(14, 13, 3, map);
                    GoTo(r2P, elDest);
                    SeedTicks("et_lo_ride");
                    TickUntil(3000, () =>
                    {
                        ElWatch(map);
                        return r2P.Position == elDest;
                    }, "et_lo_ride", 1);
                    return true;
                }
                case "et_lo_ride":
                {
                    tickWatch = null;
                    List<ElRide> rides = RidesOf(r2P);
                    int steps = elStairSteps.TryGetValue(r2P, out int st) ? st : 0;
                    Result("LO no elevator route above level 2 any more: level 4 (no stairs) unreachable; a colonist sent to level 3 gets there over stairs, any ride ends at level 2 or below",
                        elFlag && r2P.Position == elDest && steps > 0 && rides.All(r => Levels.LevelOf(r.from, map) <= 2 && Levels.LevelOf(r.to, map) <= 2),
                        $"{elNote}; stair steps {steps}, rides {string.Join("; ", rides.Select(RideStr))}, at {r2P.Position}");
                    Teleport(r2P, ElG(22, 13));
                    ShotAt(map, 3, ElG(19, 13), 7f);
                    Wait(5, "et_shot3d");
                    return true;
                }
                case "et_shot3d":
                    ShotAt(map, 2, ElG(19, 13), 9f);
                    Wait(3, "et_shotgizmo_sel");
                    return true;
                case "et_shotgizmo_sel":
                    Find.Selector.Select(elE, playSound: false, forceDesignatorDeselect: false);
                    Wait(20, "et_shotgizmo");
                    return true;
                case "et_shotgizmo":
                    Capture("elevtop_gizmo");
                    stage = "et_shotmenu";
                    return true;
                case "et_shotmenu":
                    Find.Selector.Select(elE, playSound: false, forceDesignatorDeselect: false);
                    etMenu = new FloatMenu(ElevatorRule.TopOptions(elE, 0, 4)) { vanishIfMouseDistant = false };
                    Find.WindowStack.Add(etMenu);
                    etMenu.windowRect.x = UI.screenWidth * 0.42f;
                    etMenu.windowRect.y = UI.screenHeight - 120f - etMenu.windowRect.height;
                    Wait(20, "et_shotmenu_take");
                    return true;
                case "et_shotmenu_take":
                    Capture("elevtop_menu");
                    stage = "et_rb";
                    return true;

                // ------------------------------------------------------------------------------------ RB: raise refused
                case "et_rb":
                {
                    Find.WindowStack.TryRemove(etMenu, doCloseSound: false);
                    etMenu = null;
                    Find.Selector.ClearSelection();
                    etInt = ElevatorRule.TopRefused;
                    int changed = ElevatorRule.TopChanged;
                    ShotAt(map, 3, ElG(19, 13), 9f);
                    Find.Selector.Select(elE, playSound: false, forceDesignatorDeselect: false);
                    bool done = ElevatorRule.TrySetTop(elE, 4);
                    string want = "PZ_ElevatorTopBlocked".Translate(FloorView.Label(4));
                    List<string> msgs = EtMessages();
                    Result("RB raising to Floor 5 with a floor in the shaft on level 3: refused with 'clear the shaft first', the setting unchanged (Floor 3), no removal marked",
                        !done && ElevatorRule.TopRefused == etInt + 1 && ElevatorRule.TopChanged == changed && elE.TopSetting == 2 && elE.LinkedTop == 2 && msgs.Contains(want) && !EtMarked(map, ElL(19, 13, 3, map)),
                        $"result {done}, refused {ElevatorRule.TopRefused - etInt}, setting {elE.TopSetting}, {ElLinks(map)}, messages [{string.Join(" | ", msgs)}]");
                    Wait(20, "et_rb_shot");
                    return true;
                }
                case "et_rb_shot":
                    Capture("elevtop_refused");
                    stage = "et_ra";
                    return true;

                // ------------------------------------------------------------------------------------ RA: raised again
                case "et_ra":
                {
                    Find.Selector.ClearSelection();
                    IntVec3 s3 = ElL(19, 13, 3, map);
                    map.designationManager.AddDesignation(new Designation(s3, DesignationDefOf.RemoveFoundation));
                    Teleport(r2P, ElL(14, 13, 3, map));
                    EtWork(r2P, true);
                    SeedTicks("et_ra_take");
                    TickUntil(10000, () => !ProjectZ.HasFloor(s3, map), "et_ra_go", 10);
                    return true;
                }
                case "et_ra_go":
                {
                    tickWatch = null;
                    EtWork(r2P, false);
                    bool done = ElevatorRule.TrySetTop(elE, 4);
                    IntVec3 s3 = ElL(19, 13, 3, map);
                    elNote = $"raised {done}, floor left {ProjectZ.HasFloor(s3, map)}, roofs L2 {map.roofGrid.RoofAt(ElL(19, 13, 2, map))?.defName} L3 {map.roofGrid.RoofAt(s3)?.defName ?? "none"} L4 {map.roofGrid.RoofAt(ElL(19, 13, 4, map))?.defName ?? "none"}, {ElLinks(map)}";
                    elFlag = done && elE.TopSetting == Building_Elevator.TopAll && elE.LinkedTop == 4 && StairGrid.For(map).links.Count(l => l.thing == elE) == 10
                        && map.roofGrid.RoofAt(s3) == PZDefOf.PZ_StairwellRoof && map.roofGrid.RoofAt(ElL(19, 13, 4, map)) == PZDefOf.PZ_StairwellRoof && LadderColumn.Reserved(s3, map);
                    Teleport(r2P, ElG(15, 13));
                    Draft(r2P, true);
                    elPawns.Clear();
                    elPawns.Add(r2P);
                    ElResetRides();
                    elDest = ElL(14, 13, 4, map);
                    GoTo(r2P, elDest);
                    SeedTicks("et_ra_ride");
                    TickUntil(3000, () =>
                    {
                        ElWatch(map);
                        return r2P.Position == elDest;
                    }, "et_ra_check", 1);
                    return true;
                }
                case "et_ra_check":
                {
                    tickWatch = null;
                    List<ElRide> rides = RidesOf(r2P);
                    Result("RA the colonist takes the floor up; raised to Floor 5: 10 links, the shaft reserved and roofed up to the top again, a ride of 4 floors",
                        elFlag && r2P.Position == elDest && rides.Count == 1 && rides[0].link.span == 4 && rides[0].arrived,
                        $"{elNote}; {string.Join("; ", rides.Select(RideStr))}, at {r2P.Position}");
                    stage = "et_pw";
                    return true;
                }

                // ------------------------------------------------------------------------------------ PW: power off
                case "et_pw":
                    ElFixture(map, "et_pw_go", true, 4, r2P);
                    Heal(r2P);
                    Teleport(r2P, ElG(15, 13));
                    Draft(r2P, true);
                    return true;
                case "et_pw_go":
                    tickWatch = null;
                    ElevatorRule.TrySetTop(elE, 2);
                    elBattery.Destroy();
                    SeedTicks("et_pw_off");
                    TickUntil(600, () => !elE.LinkedPowered, "et_pw_off", 1);
                    return true;
                case "et_pw_off":
                {
                    tickWatch = null;
                    List<VLink> ls = StairGrid.For(map).links.Where(l => l.thing == elE).ToList();
                    bool r4 = r2P.CanReach(ElL(14, 13, 4, map), PathEndMode.OnCell, Danger.Deadly);
                    elNote = $"{ElLinks(map)}, kinds {string.Join("/", ls.Select(l => l.kind).Distinct())}, reach L4 {r4}";
                    elFlag = !elE.LinkedPowered && ls.Count == 3 && ls.All(l => l.kind == LinkKind.ElevatorLadder && Levels.LevelOf(l.top, map) <= 2) && elE.LinkedTop == 2 && !r4;
                    elPawns.Clear();
                    elPawns.Add(r2P);
                    ElResetRides();
                    elDest = ElL(14, 13, 2, map);
                    GoTo(r2P, elDest);
                    SeedTicks("et_pw_climb");
                    TickUntil(4000, () =>
                    {
                        ElWatch(map);
                        return r2P.Position == elDest;
                    }, "et_pw_check", 1);
                    return true;
                }
                case "et_pw_check":
                {
                    tickWatch = null;
                    Result("PW top Floor 3, power cut: the emergency ladder links only levels 0-2 (3 ladder links), level 4 unreachable; a colonist climbs to level 2",
                        elFlag && r2P.Position == elDest, $"{elNote}; at {r2P.Position}, job {r2P.CurJobDef?.defName}");
                    stage = "et_bp";
                    return true;
                }

                // ------------------------------------------------------------------------------------ BP: blueprint under a building
                case "et_bp":
                {
                    R2Clear(map);
                    R2Park(map, r2P, r2Q);
                    R2Feed(map);
                    LinkSteps.For(map)?.steps.Clear();
                    foreach (Designation des in map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.RemoveFoundation).ToList())
                    {
                        des.Delete();
                    }
                    for (int L = 1; L <= 4; L++)
                    {
                        R2Floor(map, new CellRect(ElG(10, 8).x, ElG(10, 8).z, 11, 12), L);
                    }
                    ResearchProjectDef machining = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("Machining");
                    if (machining != null && !machining.IsFinished)
                    {
                        Find.ResearchManager.FinishProject(machining);
                    }
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    elStairs = R2Stairs(map, ElG(8, 10), Rot4.East);
                    // a wall on level 3 over a second spot (12, 13)
                    R2Wall(map, ElL(12, 13, 3, map));
                    ElNewBattery(map);
                    Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    steel.stackCount = 250;   // (spare for a failed construction)
                    GenPlace.TryPlaceThing(steel, ElG(22, 10), map, ThingPlaceMode.Near);
                    Thing comp = ThingMaker.MakeThing(ThingDefOf.ComponentIndustrial);
                    comp.stackCount = 10;
                    GenPlace.TryPlaceThing(comp, ElG(22, 12), map, ThingPlaceMode.Near);
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    Teleport(r2P, ElG(22, 14));
                    Teleport(r2Q, ElG(23, 14));
                    Draft(r2P, true);
                    Draft(r2Q, true);
                    SeedTicks("et_bp_wait");
                    StartTicks(5, "et_bp_go");
                    return true;
                }
                case "et_bp_go":
                {
                    IntVec3 at = ElG(19, 13);
                    AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(ElevatorDefOf.PZ_Elevator, at, Rot4.West, map);
                    etBp = ok.Accepted ? GenConstruct.PlaceBlueprintForBuild(ElevatorDefOf.PZ_Elevator, at, map, Rot4.West, Faction.OfPlayer, null) : null;
                    string marks0 = string.Join("", Enumerable.Range(1, 4).Select(L => EtMarked(map, ElL(19, 13, L, map)) ? "M" : "-"));
                    Command_Action g = etBp == null ? null : EtGizmo(etBp);
                    if (etBp != null)
                    {
                        ElevatorRule.TopOptions(etBp, 0, 4)[0].action();   // Floor 2 = level 1
                    }
                    string marks1 = string.Join("", Enumerable.Range(1, 4).Select(L => EtMarked(map, ElL(19, 13, L, map)) ? "M" : "-"));
                    Result("BP a blueprint under a 4-floor building: allowed, all 4 floors in its shaft marked; its button ('Top floor: Floor 5') set to Floor 2 takes the marks on levels 2-4 back, the shaft above level 1 is free",
                        etBp != null && marks0 == "MMMM" && g?.defaultLabel == EtTopLabel(4) && marks1 == "M---" && ElevatorRule.SetTopOf(etBp) == 1 && !LadderColumn.Reserved(ElL(19, 13, 2, map), map) && LadderColumn.Reserved(ElL(19, 13, 1, map), map),
                        $"placement {ok.Accepted} ({ok.Reason}), marks L1-4 before '{marks0}' after '{marks1}', gizmo '{g?.defaultLabel}', set top {(etBp == null ? -1 : ElevatorRule.SetTopOf(etBp))}");
                    // a second spot under a wall on level 3: placed with its top at level 2 by itself (said), cancelled
                    IntVec3 at2 = ElG(12, 13);
                    etAuto = ElevatorSite_Spawn_Patch.AutoTops;
                    AcceptanceReport ok2 = GenConstruct.CanPlaceBlueprintAt(ElevatorDefOf.PZ_Elevator, at2, Rot4.West, map);
                    Thing bp2 = ok2.Accepted ? GenConstruct.PlaceBlueprintForBuild(ElevatorDefOf.PZ_Elevator, at2, map, Rot4.West, Faction.OfPlayer, null) : null;
                    string marks2 = string.Join("", Enumerable.Range(1, 4).Select(L => EtMarked(map, ElL(12, 13, L, map)) ? "M" : "-"));
                    int top2 = bp2 == null ? -1 : ElevatorRule.SetTopOf(bp2);
                    bool msg = EtMessages().Contains("PZ_ElevatorTopAuto".Translate(FloorView.Label(2)));
                    bp2?.Destroy(DestroyMode.Cancel);
                    string marks3 = string.Join("", Enumerable.Range(1, 4).Select(L => EtMarked(map, ElL(12, 13, L, map)) ? "M" : "-"));
                    Result("BP placed under a wall on level 3: allowed with its top set to level 2 by itself (message), only levels 1-2 marked; cancelled: the marks go",
                        bp2 != null && top2 == 2 && ElevatorSite_Spawn_Patch.AutoTops == etAuto + 1 && msg && marks2 == "MM--" && marks3 == "----",
                        $"placement {ok2.Accepted} ({ok2.Reason}), top {top2}, auto {ElevatorSite_Spawn_Patch.AutoTops - etAuto}, message {msg}, marks '{marks2}', after cancel '{marks3}'");
                    EtWork(r2P, true);
                    EtWork(r2Q, true);
                    elE = null;
                    etFrameSeen = etFrameGizmo = false;
                    etFrameTop = -99;
                    SeedTicks("et_bp_build");
                    TickUntil(30000, () =>
                    {
                        if (!etFrameSeen && ElG(19, 13).GetThingList(map).FirstOrDefault(x => x is Frame && ElevatorSite.Is(x)) is Frame f)
                        {
                            etFrameSeen = true;
                            etFrameTop = ElevatorRule.SetTopOf(f);
                            etFrameGizmo = EtGizmo(f)?.defaultLabel == EtTopLabel(1);
                        }
                        elE = map.listerThings.ThingsOfDef(ElevatorDefOf.PZ_Elevator).OfType<Building_Elevator>().FirstOrDefault();
                        return elE != null;
                    }, "et_bp_check", 10);
                    return true;
                }
                case "et_bp_check":
                {
                    tickWatch = null;
                    EtWork(r2P, false);
                    EtWork(r2Q, false);
                    string floors = string.Join("", Enumerable.Range(1, 4).Select(L => ProjectZ.HasFloor(ElL(19, 13, L, map), map) ? "F" : "-"));
                    string marks = string.Join("", Enumerable.Range(2, 3).Select(L => EtMarked(map, ElL(19, 13, L, map)) ? "M" : "-"));
                    Result("BP colonists take the level-1 floor up and build it; the frame carried the setting (and its button); the elevator serves levels 0-1 (1 link), the floors on levels 2-4 stay, unmarked",
                        elE != null && etFrameSeen && etFrameTop == 1 && etFrameGizmo && elE.TopSetting == 1 && elE.LinkedTop == 1 && StairGrid.For(map).links.Count(l => l.thing == elE) == 1 && floors == "-FFF" && marks == "---",
                        $"built {elE != null}, frame seen {etFrameSeen} top {etFrameTop} gizmo {etFrameGizmo}, setting {elE?.TopSetting}, {ElLinks(map)}, floors L1-4 '{floors}', marks L2-4 '{marks}', roof under L2 {map.roofGrid.RoofAt(ElL(19, 13, 1, map))?.defName}");
                    if (elE == null)
                    {
                        stage = "et_sv";
                        return true;
                    }
                    SeedTicks("et_bp_power");
                    TickUntil(600, () => elE.LinkedPowered, "et_bp_ridego", 1);
                    return true;
                }
                case "et_bp_ridego":
                {
                    tickWatch = null;
                    Teleport(r2P, ElG(17, 13));
                    Draft(r2P, true);
                    elPawns.Clear();
                    elPawns.Add(r2P);
                    ElResetRides();
                    elDest = ElL(17, 14, 1, map);
                    GoTo(r2P, elDest);
                    SeedTicks("et_bp_ride");
                    TickUntil(3000, () =>
                    {
                        ElWatch(map);
                        return r2P.Position == elDest;
                    }, "et_bp_ride", 1);
                    return true;
                }
                case "et_bp_ride":
                {
                    tickWatch = null;
                    List<ElRide> rides = RidesOf(r2P);
                    Result("BP the elevator works to Floor 2: powered, a colonist beside it rides up one floor",
                        elE.LinkedPowered && r2P.Position == elDest && rides.Count == 1 && rides[0].link.kind == LinkKind.Elevator && rides[0].link.span == 1 && rides[0].arrived, $"{ElLinks(map)}; {string.Join("; ", rides.Select(RideStr))}, at {r2P.Position}");
                    stage = "et_s3";
                    return true;
                }

                // ------------------------------------------------------------------------------------ S3: 3D shots
                // (mirror fixture: the shaft in front of the platforms) lowered to level 2 with a floor laid over it on
                // level 3: floor 4 viewed (level 3) and floor 3 viewed (level 2)
                case "et_s3":
                    ElFixture(map, "et_s3_go", true, true, 4, r2P);
                    Teleport(r2P, ElG(24, 13));
                    Draft(r2P, true);
                    return true;
                case "et_s3_go":
                    tickWatch = null;
                    ElevatorRule.TrySetTop(elE, 2);
                    map.terrainGrid.SetTerrain(ElL(19, 13, 3, map), PZDefOf.PZ_UpperFloor);
                    SeedTicks("et_s3_settle");
                    StartTicks(30, "et_s3_a");
                    return true;
                case "et_s3_a":
                    Info($"S3 {ElLinks(map)}, floor over the shaft on level 3 {ProjectZ.HasFloor(ElL(19, 13, 3, map), map)}");
                    ShotAt(map, 3, ElG(19, 13), 6f);
                    Wait(5, "et_s3_a_take");
                    return true;
                case "et_s3_a_take":
                    Capture("elevtop_lowered_3d");
                    stage = "et_s3_b";
                    return true;
                case "et_s3_b":
                    ShotAt(map, 2, ElG(19, 13), 6f);
                    Wait(5, "et_s3_b_take");
                    return true;
                case "et_s3_b_take":
                    Capture("elevtop_lowered_3d_floor3");
                    stage = "et_sv";
                    return true;

                // ------------------------------------------------------------------------------------ SV / OLD: saves
                case "et_sv":
                    ElFixture(map, "et_sv_go", true, 4, r2P);
                    return true;
                case "et_sv_go":
                {
                    tickWatch = null;
                    ElevatorRule.TrySetTop(elE, 2);
                    etBp = GenConstruct.PlaceBlueprintForBuild(ElevatorDefOf.PZ_Elevator, ElG(26, 13), map, Rot4.West, Faction.OfPlayer, null);
                    ElevatorRule.TrySetTop(etBp, 1);
                    Info($"SV before saving: elevator setting {elE.TopSetting}, {ElLinks(map)}; blueprint at {etBp?.Position} set top {(etBp == null ? -1 : ElevatorRule.SetTopOf(etBp))}");
                    GameDataSaveLoader.SaveGame("pz_elevtop");
                    stage = "et_sv_loaded";
                    pendingLoad = "pz_elevtop";
                    return true;
                }
                case "et_sv_loaded":
                {
                    elE = map.listerThings.ThingsOfDef(ElevatorDefOf.PZ_Elevator).OfType<Building_Elevator>().FirstOrDefault();
                    etBp = ElG(26, 13).GetThingList(map).FirstOrDefault(x => ElevatorSite.Is(x));
                    Result("SV after loading: the elevator keeps Floor 3 (3 links), the blueprint keeps Floor 2",
                        elE != null && elE.TopSetting == 2 && elE.LinkedTop == 2 && StairGrid.For(map).links.Count(l => l.thing == elE) == 3 && etBp != null && ElevatorRule.SetTopOf(etBp) == 1,
                        $"elevator setting {elE?.TopSetting}, {ElLinks(map)}; blueprint {etBp?.def.defName} set top {(etBp == null ? -1 : ElevatorRule.SetTopOf(etBp))}");
                    // the same save as from before the setting: no topSetting field, no ElevatorTops component
                    GameDataSaveLoader.SaveGame("pz_elevtop_old");
                    string path = GenFilePaths.FilePathForSavedGame("pz_elevtop_old");
                    string text = File.ReadAllText(path);
                    int fields = Regex.Matches(text, "<topSetting>").Count;
                    text = Regex.Replace(text, @"\s*<topSetting>[^<]*</topSetting>", "");
                    bool comp = EtCut(ref text, "<li Class=\"ProjectZ.ElevatorTops\">");
                    File.WriteAllText(path, text);
                    Info($"OLD: stripped {fields} topSetting field(s) and the ElevatorTops component ({comp}) from {path}");
                    elFlag = fields >= 1 && comp;
                    stage = "et_old_loaded";
                    pendingLoad = "pz_elevtop_old";
                    return true;
                }
                case "et_old_loaded":
                {
                    elE = map.listerThings.ThingsOfDef(ElevatorDefOf.PZ_Elevator).OfType<Building_Elevator>().FirstOrDefault();
                    etBp = ElG(26, 13).GetThingList(map).FirstOrDefault(x => ElevatorSite.Is(x));
                    Result("OLD a save from before (no field, no component): the elevator goes all the way up (10 links), the blueprint too, the component is there again",
                        elFlag && elE != null && elE.TopSetting == Building_Elevator.TopAll && elE.LinkedTop == 4 && StairGrid.For(map).links.Count(l => l.thing == elE) == 10 && etBp != null && ElevatorRule.SetTopOf(etBp) == 4 && map.GetComponent<ElevatorTops>() != null,
                        $"stripped {elFlag}, elevator setting {elE?.TopSetting}, {ElLinks(map)}; blueprint set top {(etBp == null ? -1 : ElevatorRule.SetTopOf(etBp))}, component {map.GetComponent<ElevatorTops>() != null}");
                    Finish();
                    return true;
                }
            }
            return false;
        }

        // Cuts the element that starts with 'open' (an <li ...>) up to its matching </li>.
        private static bool EtCut(ref string text, string open)
        {
            int a = text.IndexOf(open, StringComparison.Ordinal);
            if (a < 0)
            {
                return false;
            }
            int depth = 0, i = a;
            while (i < text.Length)
            {
                int o = text.IndexOf("<li", i, StringComparison.Ordinal);
                int c = text.IndexOf("</li>", i, StringComparison.Ordinal);
                if (c < 0)
                {
                    return false;
                }
                if (o >= 0 && o < c)
                {
                    int end = text.IndexOf('>', o);
                    if (end > 0 && text[end - 1] != '/')
                    {
                        depth++;
                    }
                    i = end + 1;
                    continue;
                }
                depth--;
                i = c + 5;
                if (depth == 0)
                {
                    text = text.Remove(a, i - a);
                    return true;
                }
            }
            return false;
        }
    }
}
