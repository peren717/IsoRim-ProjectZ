using HarmonyLib;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // Everything that touches IsoRim's types lives here, so ProjectZ also loads (headless tests) without IsoRim.
    public static class IsoRimBridge
    {
        public static bool Present;

        public static void Init()
        {
            Present = AccessTools.TypeByName("IsoRim.Api") != null && AccessTools.TypeByName("IsoRim.IFloorLayout") != null;
            if (!Present)
            {
                Log.Message("[ProjectZ] IsoRim with the floor API was not found: floors work, but are not drawn stacked.");
                return;
            }
            Hook();
        }

        private static void Hook()
        {
            // Decisions D10/D11: the 3D view is always on with ProjectZ, on every map.
            IsoRim.View3D.LockedOn = true;
            IsoRim.View3D.LockedBy = "IsoRim ProjectZ";
            // Storeys stand on the walls: the wall height is fixed (user 2026-10-15), so stairs, models and the slab
            // height rule all work to one storey.
            IsoRim.View3D.LockedWallHeight = LockedWall;
            IsoRim.Api.HiddenRoof = r => r == PZDefOf.PZ_StairwellRoof;
            IsoRim.Api.Floors = new FloorLayout();
        }

        // The wall height under ProjectZ (IsoRim's option is locked to it).
        public const float LockedWall = 1.65f;

        // Height of one storey, in cells: the wall height and a little more, so a floor never meets the tops of the
        // walls below it in one plane.
        public static float StoreyHeight => WallHeight + 0.02f;

        public static float WallHeight => Present ? WallHeightInner() : LockedWall;

        private static float WallHeightInner() => IsoRim.View3D.WallHeight;

        // How tall IsoRim draws a building from its model (0 without one, or without IsoRim).
        public static float ModelHeight(ThingDef def) => Present ? ModelHeightInner(def) : 0f;

        private static float ModelHeightInner(ThingDef def) => IsoRim.Api.ModelHeight(def);

        public static float Pitch => Present ? PitchInner() : 90f;

        private static float PitchInner() => IsoRim.View3D.Pitch;

        // IsoRim's roof switch (tests).
        public static void SetRoofs(bool on)
        {
            if (Present)
            {
                SetRoofsInner(on);
            }
        }

        private static void SetRoofsInner(bool on) => IsoRim.Roofs.Set(on);

        // An IsoRim option by its field name (tests); the old value, or null without IsoRim.
        public static object SetOption(string field, object value)
        {
            return Present ? SetOptionInner(field, value) : null;
        }

        private static object SetOptionInner(string field, object value)
        {
            object st = IsoRim.IsoRimMod.Settings;
            System.Reflection.FieldInfo f = AccessTools.Field(st.GetType(), field);
            object old = f.GetValue(st);
            f.SetValue(st, value);
            return old;
        }

        // What the cut-away works on now (tests): the pointer (screen and map) and the cells it lowers in a rectangle.
        public static string CutState(Map map, CellRect rect)
        {
            return Present ? CutStateInner(map, rect) : "no IsoRim";
        }

        private static string CutStateInner(Map map, CellRect rect)
        {
            Vector3 mouse = UnityEngine.Input.mousePosition;
            Vector3 at = IsoRim.Floors.ScreenToMap(new Vector2(mouse.x, mouse.y));
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (IntVec3 c in rect)
            {
                if (c.InBounds(map) && IsoRim.Cutaway.Hovered(map, c))
                {
                    sb.Append(c).Append(' ');
                }
            }
            return $"mouse {mouse} -> map {at}; walls lowered: {sb}";
        }

        // Screen pixel to the cell under it on a floor (tests), via IsoRim's camera maths.
        public static void ScreenToGround(Vector2 px, float height, out Vector3 p)
        {
            p = IsoRim.View3D.ScreenToHeight(px, height);
        }
    }

    public class FloorLayout : IsoRim.IFloorLayout
    {
        public int GroundRows(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            return a == null ? 0 : a.groundZ;
        }

        public int FloorCount(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            return a == null ? 0 : a.floors - 1;
        }

        public bool Floor(Map map, int k, out float height, out int zShift)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            height = k * IsoRimBridge.StoreyHeight;
            zShift = a == null ? 0 : k * a.stride;
            return k <= FloorView.Current;
        }

        public int PointerFloor(Map map) => FloorView.Current;

        // A pawn on the ramp, on the step to or from its top, or in the middle of a jump (Links.Rise).
        public Vector3 ThingOffset(Thing t)
        {
            if (!(t is Pawn p))
            {
                return Vector3.zero;
            }
            float rise = Links.Rise(p);
            return rise == 0f ? Vector3.zero : new Vector3(0f, rise, 0f);
        }
    }
}
