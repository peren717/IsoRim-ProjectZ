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
            IsoRim.Api.Floors = new FloorLayout();
        }

        // Height of one storey, in cells: IsoRim's wall height (a little more, so a floor never meets the tops of the
        // walls below it in one plane).
        public static float StoreyHeight => Present ? WallHeight() + 0.02f : 1.2f;

        private static float WallHeight() => IsoRim.View3D.WallHeight;

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

        // A pawn on a stair step rises (or sinks) with the step.
        public Vector3 ThingOffset(Thing t)
        {
            if (!(t is Pawn p))
            {
                return Vector3.zero;
            }
            int dir = Climb.Direction(p);
            if (dir == 0)
            {
                return Vector3.zero;
            }
            float pct = Mathf.Clamp01(p.pather.MovePercentage);
            return new Vector3(0f, dir * pct * IsoRimBridge.StoreyHeight, 0f);
        }
    }
}
