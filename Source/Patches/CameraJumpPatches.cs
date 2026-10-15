using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ProjectZ.Patches
{
    // Camera jumps to a target (playtest bug: double-clicking a colonist upstairs in the colonist bar sent the camera to
    // the raw annex cell, north of the map). A jump shows the target's floor and centres the camera on where that floor
    // draws the target, not on its map cell.
    // Call sites in the 1.7 source that move the camera to a target cell:
    //   - CameraJumper.TryJumpInternal(IntVec3, Map, MovementMode): every TryJump / TryJumpAndSelect (colonist bar
    //     double-click, letters' "jump to", alerts, messages, quests, search, history, world -> map, inspect links,
    //     GlobalTargetInfo jumps) ends here, as a pan or a cut;
    //   - CameraJumper.TrySelectInternal(Thing): when selecting also leaves the world view or changes map (cut);
    //   - Selector.Select: selecting a thing on another map (cut).
    // Not targets: Game start (the start spot, on the ground), MapInterface (a remembered camera position), the gravship
    // landing pan (on the ground), IsoRim's camera drag (a camera position, not a target).
    public static class FloorJump
    {
        private static readonly AccessTools.FieldRef<CameraDriver, Vector3> RootPos = AccessTools.FieldRefAccess<CameraDriver, Vector3>("rootPos");

        // Tests: off (-projectznojumpfix) to see the game's own jump.
        public static bool Disabled;

        // Show the floor of `cell` and move the camera to it: a cut, or a pan when `pan` and the game is panning.
        public static void Apply(IntVec3 cell, Map map, bool pan)
        {
            if (Disabled || Current.ProgramState != ProgramState.Playing || map == null || map != Find.CurrentMap || !cell.IsValid || !cell.InBounds(map) || WorldRendererUtility.WorldSelected)
            {
                return;
            }
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return;
            }
            int k = ProjectZ.FloorOf(cell, map);
            if (k < 0)
            {
                return;
            }
            FloorView.Set(k);
            if (k == 0)
            {
                return;
            }
            IntVec3 g = ProjectZ.GroundCellOf(cell, map);
            Vector3 loc = IsoRimBridge.DrawnGroundPoint(new Vector3(cell.x + 0.5f, k * IsoRimBridge.StoreyHeight, g.z + 0.5f));
            CameraDriver driver = Find.CameraDriver;
            if (pan)
            {
                driver.PanToMapLocAndSize(loc, driver.RootSize);
            }
            else
            {
                driver.JumpToCurrentMapLoc(loc);
            }
        }

        // The cut the game made to the raw cell (rootPos there), or a pan under way.
        public static bool Panned(IntVec3 cell)
        {
            Vector3 r = RootPos(Find.CameraDriver);
            return Mathf.Abs(r.x - (cell.x + 0.5f)) > 0.01f || Mathf.Abs(r.z - (cell.z + 0.5f)) > 0.01f;
        }
    }

    [HarmonyPatch]
    public static class CameraJumper_TryJumpInternal_Patch
    {
        public static MethodBase TargetMethod() => AccessTools.Method(typeof(CameraJumper), "TryJumpInternal", new[] { typeof(IntVec3), typeof(Map), typeof(CameraJumper.MovementMode) });

        public static void Postfix(IntVec3 cell, Map map)
        {
            if (map != null && ProjectZ.Annex(map) != null && cell.IsValid && map == Find.CurrentMap)
            {
                FloorJump.Apply(cell, map, FloorJump.Panned(cell));
            }
        }
    }

    [HarmonyPatch(typeof(CameraJumper), "TrySelectInternal", new[] { typeof(Thing) })]
    public static class CameraJumper_TrySelectInternal_Patch
    {
        public static void Prefix(Thing thing, out bool __state)
        {
            __state = thing != null && thing.Spawned && (WorldRendererUtility.WorldSelected || thing.Map != Find.CurrentMap);
        }

        public static void Postfix(Thing thing, bool __state)
        {
            if (__state && thing.Spawned)
            {
                FloorJump.Apply(thing.Position, thing.Map, false);
            }
        }
    }

    [HarmonyPatch(typeof(Selector), nameof(Selector.Select))]
    public static class Selector_Select_Patch
    {
        public static void Prefix(object obj, out Map __state)
        {
            __state = obj is Thing t && t.MapHeld != null && t.MapHeld != Find.CurrentMap ? t.MapHeld : null;
        }

        public static void Postfix(object obj, Map __state)
        {
            if (__state != null && __state == Find.CurrentMap && obj is Thing t)
            {
                FloorJump.Apply(t.PositionHeld, __state, false);
            }
        }
    }
}
