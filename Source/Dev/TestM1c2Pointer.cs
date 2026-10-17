using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // M1c-2 pointer through floors (mode m1c2, after the move order): viewing floor 2 with the colonist upstairs, an order
    // given with the game's own drafted-move float menu at a screen point (UI.UIToMapPosition, as a right-click does):
    //   1. on the slab inside the house: the order lands on floor 2 (destination mark there, not hidden behind a wall);
    //   2. on the ground outside the house, where the floor-2 plane is open air: the pointer falls through to the ground and
    //      the colonist walks down the stairs to that cell.
    public static partial class TestRunner
    {
        private static IntVec3 clickWant;

        private static bool StepPointer(Map map)
        {
            switch (stage)
            {
                case "d_click":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    FloorView.Set(1);
                    IsoRimBridge.SetRoofs(false);
                    Find.PlaySettings.showRoofOverlay = false;
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(mover);
                    Find.CameraDriver.SetRootPosAndSize(new Vector3(twoStorey.CenterCell.x + 0.5f, 0f, twoStorey.CenterCell.z + 0.5f), 12f);
                    Wait(30, "d_click_slab");
                    return true;
                }
                case "d_click_slab":
                    ClickOrder(map, new IntVec3(twoStorey.minX + 4, 0, twoStorey.minZ + 3), 1, "on the slab inside the house");
                    StartTicks(3, "d_click_slab_shot");
                    return true;
                case "d_click_slab_shot":
                    tickWatch = null;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Capture("m1c2_click_slab");
                    stage = "d_click_slab_go";
                    return true;
                case "d_click_slab_go":
                    TickUntil(2000, () => mover.Position == clickWant, "d_click_slab_check", 5);
                    return true;
                case "d_click_slab_check":
                    tickWatch = null;
                    Result("ordered on the slab: colonist gets there on floor 2", mover.Position == clickWant, $"{mover} at {mover.Position}, wanted {clickWant}");
                    Wait(5, "d_click_ground");
                    return true;
                case "d_click_ground":
                    ClickOrder(map, new IntVec3(twoStorey.minX - 2, 0, twoStorey.minZ + 3), 0, "on the ground outside the house");
                    StartTicks(8, "d_click_ground_shot");
                    return true;
                case "d_click_ground_shot":
                    tickWatch = null;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Info($"ground order: {mover} at {mover.Position}, job {mover.CurJob?.def?.defName} -> {mover.CurJob?.targetA}, path nodes {mover.pather.curPath?.NodesLeftCount}");
                    Capture("m1c2_click_ground");
                    stage = "d_click_ground_go";
                    return true;
                case "d_click_ground_go":
                    TickUntil(4000, () => mover.Position == clickWant, "d_click_ground_check", 10);
                    return true;
                case "d_click_ground_check":
                    tickWatch = null;
                    Result("ordered on the ground outside: colonist walks down to that cell", mover.Position == clickWant,
                        $"{mover} at {mover.Position} (floor {ProjectZ.FloorOf(mover.Position, map) + 1}), wanted {clickWant}");
                    Find.Selector.ClearSelection();
                    stage = "d_face";
                    return true;
            }
            return false;
        }

        // A right-click order at the screen point over ground cell g raised to floor `floor` (seen from the camera): the pointer must
        // land on that floor's cell, and the game's drafted-move option is taken (its goto mark, its job).
        private static void ClickOrder(Map map, IntVec3 g, int floor, string what)
        {
            IntVec3 cell = ProjectZ.CellOnFloor(new IntVec3(g.x, 0, g.z), floor, map);
            float h = floor * IsoRimBridge.StoreyHeight;
            Vector2 px = IsoRimBridge.ScreenOf(new Vector3(g.x + 0.5f, h, g.z + 0.5f));
            Vector3 clickPos = Verse.UI.UIToMapPosition(px / Prefs.UIScale);
            IntVec3 got = clickPos.ToIntVec3();
            Result($"pointer {what} picks floor {floor + 1}'s cell", got == cell, $"screen {px} -> {got}, expected {cell}");
            List<FloatMenuOption> opts = FloatMenuMakerMap.GetOptions(new List<Pawn> { mover }, clickPos, out FloatMenuContext ctx);
            FloatMenuOption go = opts.FirstOrDefault(o => o.isGoto && !o.Disabled);
            Result($"drafted-move option {what}", go != null, string.Join(", ", opts.Select(o => o.Label)));
            clickWant = cell;
            if (go != null)
            {
                go.Chosen(true, null);
                clickWant = mover.CurJob != null && mover.CurJob.def == JobDefOf.Goto ? mover.CurJob.targetA.Cell : cell;
                Result($"order {what} goes to floor {floor + 1}", ProjectZ.FloorOf(clickWant, map) == floor && clickWant.DistanceTo(cell) <= 3f,
                    $"job {mover.CurJob?.def?.defName} -> {clickWant} (clicked {cell})");
            }
        }
    }
}
