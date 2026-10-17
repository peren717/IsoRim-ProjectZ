using HarmonyLib;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // Everything that touches IsoRim's types lives here, so ProjectZ also loads (headless tests) without IsoRim. It uses
    // only IsoRim's public API (IsoRim.Api, IsoRim.Api.Dev for tests, IsoRim.IFloorLayout, IsoRim.ProjectileCardHook),
    // never IsoRim's internals, so IsoRim can change those freely.
    public static class IsoRimBridge
    {
        public static bool Present;

        // The IsoRim API version this ProjectZ needs (IsoRim.Api.Version).
        private const int NeedApi = 2;

        public static void Init()
        {
            Present = AccessTools.TypeByName("IsoRim.Api") != null && AccessTools.TypeByName("IsoRim.IFloorLayout") != null;
            if (!Present)
            {
                Log.Message("[ProjectZ] IsoRim with the floor API was not found: floors work, but are not drawn stacked.");
                return;
            }
            System.Reflection.MethodInfo version = AccessTools.PropertyGetter("IsoRim.Api:Version");
            int have = version == null ? 1 : (int)version.Invoke(null, null);
            if (have < NeedApi)
            {
                Present = false;
                Log.Warning($"[ProjectZ] This IsoRim is too old for this ProjectZ (API {have}, needs {NeedApi}): update IsoRim. Floors work, but are not drawn stacked.");
                return;
            }
            Hook();
        }

        private static void Hook()
        {
            // Decisions D10/D11: the 3D view is always on with ProjectZ, on every map.
            // Storeys stand on the walls: the wall height is fixed (user 2026-10-15), so stairs, models and the slab
            // height rule all work to one storey.
            IsoRim.Api.LockView("IsoRim ProjectZ", LockedWall);
            IsoRim.Api.HiddenRoof = r => r == PZDefOf.PZ_StairwellRoof;
            IsoRim.Api.Floors = new FloorLayout();
            if (Combat.CombatGate.Enabled)
            {
                IsoRim.Api.ProjectileCard = Combat.ProjectilePitch.Card;   // shots between floors pitched (ProjectilePitch)
            }
        }

        // The wall height under ProjectZ (IsoRim's option is locked to it).
        public static readonly float LockedWall = DevWallHeight(1.65f);

        // Dev only: -projectzwallheight=X starts the game with another locked wall height (wall-height comparison shots).
        private static float DevWallHeight(float normal)
        {
            if (GenCommandLine.TryGetCommandLineArg("projectzwallheight", out string s) && float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float h) && h >= 0.5f && h <= 4f)
            {
                Log.Warning("[ProjectZ] dev: wall height " + h + " (-projectzwallheight)");
                return h;
            }
            return normal;
        }

        // Height of one storey, in cells: the wall height and a little more, so a floor never meets the tops of the
        // walls below it in one plane.
        public static float StoreyHeight => WallHeight + 0.02f;

        public static float WallHeight => Present ? WallHeightInner() : LockedWall;

        private static float WallHeightInner() => IsoRim.Api.WallHeight;

        // How tall IsoRim draws a building from its model (0 without one, or without IsoRim).
        public static float ModelHeight(ThingDef def) => Present ? ModelHeightInner(def) : 0f;

        private static float ModelHeightInner(ThingDef def) => IsoRim.Api.ModelHeight(def);

        public static float Pitch => Present ? PitchInner() : 90f;

        private static float PitchInner() => IsoRim.Api.ViewPitch;

        // IsoRim's roof switch (tests).
        public static void SetRoofs(bool on)
        {
            if (Present)
            {
                SetRoofsInner(on);
            }
        }

        private static void SetRoofsInner(bool on) => IsoRim.Api.ShowRoofs(on);

        // The ground point the camera shows at the same place on screen as a point raised to a floor (x and z in ground
        // rows, real height as y): centring the camera on it centres the raised point. Without the 3D view, the point
        // itself.
        public static Vector3 DrawnGroundPoint(Vector3 p) => Present ? DrawnGroundPointInner(p) : new Vector3(p.x, 0f, p.z);

        private static Vector3 DrawnGroundPointInner(Vector3 p)
        {
            Vector3 flat = new Vector3(p.x, 0f, p.z);
            if (!IsoRim.Api.ViewReady)
            {
                return flat;
            }
            Vector3 g = IsoRim.Api.ScreenToGround(IsoRim.Api.WorldToScreen(p));
            if (float.IsNaN(g.x) || float.IsNaN(g.z) || float.IsInfinity(g.x) || float.IsInfinity(g.z))
            {
                return flat;
            }
            return new Vector3(g.x, 0f, g.z);
        }

        // An IsoRim option by the name it is saved under (tests); the old value, or null without IsoRim.
        public static object SetOption(string field, object value)
        {
            return Present ? SetOptionInner(field, value) : null;
        }

        private static object SetOptionInner(string field, object value) => IsoRim.Api.SetOption(field, value);

        // The wall height IsoRim is locked to and who locked the view (tests); 0 and null without IsoRim.
        public static float LockedWallHeight => Present ? LockedWallHeightInner() : 0f;

        private static float LockedWallHeightInner() => IsoRim.Api.LockedWallHeight;

        public static string ViewLockedBy => Present ? ViewLockedByInner() : null;

        private static string ViewLockedByInner() => IsoRim.Api.ViewLockedBy;

        // What the cut-away works on now (tests): the pointer (screen and map) and the cells it lowers in a rectangle.
        public static string CutState(Map map, CellRect rect)
        {
            return Present ? CutStateInner(map, rect) : "no IsoRim";
        }

        private static string CutStateInner(Map map, CellRect rect)
        {
            Vector3 mouse = UnityEngine.Input.mousePosition;
            Vector3 at = IsoRim.Api.ScreenToMap(new Vector2(mouse.x, mouse.y));
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (IntVec3 c in rect)
            {
                if (c.InBounds(map) && IsoRim.Api.WallCutAway(map, c))
                {
                    sb.Append(c).Append(' ');
                }
            }
            return $"mouse {mouse} -> map {at}; walls lowered: {sb}";
        }

        // Screen pixel to the cell under it on a floor (tests), via IsoRim's camera maths.
        public static void ScreenToGround(Vector2 px, float height, out Vector3 p)
        {
            p = IsoRim.Api.ScreenToHeight(px, height);
        }

        // ---- the range preview's shell (Combat.RangeShell)

        // The 3D view draws now.
        public static bool View3DOn => Present && View3DOnInner();

        private static bool View3DOnInner() => IsoRim.Api.ViewReady;

        // How the 3D view squashes heights (drawn y = true height / Squash).
        public static float Squash => Present ? SquashInner() : 1f;

        private static float SquashInner() => IsoRim.Api.HeightSquash;

        // Unit direction toward the camera in true space.
        public static Vector3 TowardCamera() => Present ? TowardCameraInner() : Vector3.up;

        private static Vector3 TowardCameraInner()
        {
            return -(Quaternion.Euler(IsoRim.Api.ViewPitch, IsoRim.Api.ViewYaw, 0f) * Vector3.forward);
        }

        // How high a shooter fires from over its feet, in cells (IsoRim's muzzle: where its bullets start).
        public static float MuzzleHeight(Thing t) => Present ? MuzzleHeightInner(t) : 0.5f;

        private static float MuzzleHeightInner(Thing t) => IsoRim.Api.MuzzleHeight(t);

        // The height (cells over the ground, true) IsoRim draws a projectile at when the game has it at point at of its
        // flat path, and the height of what it is aimed at over its floor (tests).
        public static float ProjectileHeight(Projectile p, Vector3 at) => Present ? ProjectileHeightInner(p, at) : 0f;

        private static float ProjectileHeightInner(Projectile p, Vector3 at) => IsoRim.Api.ProjectileHeight(p, at);

        // Where a point (ground x and z, true height in cells as y) shows on screen, pixels from the bottom left (tests).
        public static Vector2 ScreenOf(Vector3 p) => Present ? ScreenOfInner(p) : Vector2.zero;

        private static Vector2 ScreenOfInner(Vector3 p) => IsoRim.Api.WorldToScreen(p);

        // Tells IsoRim where a shot really left from, when its launch point was moved onto another floor's rows.
        public static void SetLaunchPoint(Projectile p, Vector3 from)
        {
            if (Present)
            {
                SetLaunchPointInner(p, from);
            }
        }

        private static void SetLaunchPointInner(Projectile p, Vector3 from) => IsoRim.Api.SetLaunchPoint(p, from);

        // Where IsoRim's drawn flight starts (x, z in the game's rows; y the height in cells), or null without IsoRim.
        public static Vector3? DrawnStart(Projectile p) => Present ? DrawnStartInner(p) : (Vector3?)null;

        private static Vector3 DrawnStartInner(Projectile p) => IsoRim.Api.DrawnStart(p);

        // IsoRim's muzzle point (tests): x, z on the map, y the height over the owner's feet.
        public static Vector3 Muzzle(Thing owner, ThingDef gun, Vector3 from, Vector3 dir) => Present ? MuzzleInner(owner, gun, from, dir) : from;

        private static Vector3 MuzzleInner(Thing owner, ThingDef gun, Vector3 from, Vector3 dir) => IsoRim.Api.Muzzle(owner, gun, from, dir);

        // Where IsoRim draws a skyfaller now (tests): its ground point (ground-band x, z), height of its bottom over the
        // ground and its landing floor's height; null without IsoRim, or when its floor is hidden.
        public static Vector3[] SkyPose(RimWorld.Skyfaller s) => Present ? SkyPoseInner(s) : null;

        private static Vector3[] SkyPoseInner(RimWorld.Skyfaller s)
        {
            if (!IsoRim.Api.SkyfallerPose(s, out Vector3 ground, out float height, out float floorHeight, out float rowShift))
            {
                return null;
            }
            return new[] { ground, new Vector3(height, floorHeight, rowShift) };
        }

        public static float AimHeight(Projectile p) => Present ? AimHeightInner(p) : 0f;

        private static float AimHeightInner(Projectile p) => IsoRim.Api.AimHeight(p);

        // (made on first use, on the main thread; the holder keeps the game's static-asset check quiet)
        [StaticConstructorOnStartup]
        private static class ShellHolder
        {
            public static Material mat;
        }

        private static bool shellTried;

        // See-through, vertex-coloured, depth-tested without writing depth, both sides, unlit; queued after the game's
        // light overlay (3100) so night never darkens it. Null without IsoRim.
        public static Material ShellMaterial()
        {
            if (!shellTried)
            {
                shellTried = true;
                ShellHolder.mat = Present ? ShellMaterialInner() : null;
            }
            return ShellHolder.mat;
        }

        public const int ShellQueue = 3110;

        private static Material ShellMaterialInner() => IsoRim.Api.MakeBlendMaterial("PZ_RangeShell", ShellQueue);

        // ---- solid pieces (RiserView): IsoRim's lit vertex-coloured model material, drawn with its light mark as
        // IsoRim draws its own models (sun shading per face, night and lamp light per cell). Does nothing without IsoRim.
        public static void DrawLitSolid(Mesh mesh, Matrix4x4 matrix)
        {
            if (Present)
            {
                DrawLitSolidInner(mesh, matrix);
            }
        }

        private static void DrawLitSolidInner(Mesh mesh, Matrix4x4 matrix) => IsoRim.Api.DrawLit(mesh, matrix);

        // ---- dev tests only (IsoRim.Api.Dev)

        // IsoRim draws stacked floors this frame.
        public static bool FloorsDrawn => Present && FloorsDrawnInner();

        private static bool FloorsDrawnInner() => IsoRim.Api.Dev.FloorsDrawn;

        // IsoRim's 3D view switch set directly (the caller regenerates the map meshes).
        public static void SetView3DActive(bool on)
        {
            if (Present)
            {
                SetView3DActiveInner(on);
            }
        }

        private static void SetView3DActiveInner(bool on) => IsoRim.Api.Dev.View3DActive = on;

        // IsoRim's upper-floor sun shadow gap fill (A/B switch).
        public static void SetShadowGapFill(bool on)
        {
            if (Present)
            {
                SetShadowGapFillInner(on);
            }
        }

        private static void SetShadowGapFillInner(bool on) => IsoRim.Api.Dev.ShadowGapFill = on;

        // IsoRim sees a slab (the floor above) over this cell.
        public static bool SlabOver(Map map, IntVec3 c) => Present && SlabOverInner(map, c);

        private static bool SlabOverInner(Map map, IntVec3 c) => IsoRim.Api.Dev.SlabOver(map, c);

        // The light in IsoRim's glow texture at a cell's south-west corner, as text ("?" when unknown).
        public static string GlowCorner(Map map, IntVec3 c) => Present ? GlowCornerInner(map, c) : "?";

        private static string GlowCornerInner(Map map, IntVec3 c)
        {
            Color32? v = IsoRim.Api.Dev.GlowCorner(map, c);
            return v.HasValue ? v.Value.ToString() : "?";
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

        // The gravship cutscene shows all floors of a ship with upper decks, then puts the player's view back.
        public int ViewFloor(Map map, int k) => FloorView.Swap(map, k);

        // The stairwell of ship stairs is part of the ship: a roof over it is drawn as the ship's roof (ShipStairs.Opening).
        public bool ShipCell(Map map, IntVec3 c) => ShipStairs.Opening(c, map);

        // A pawn on the ramp, on the step to or from its top, or in the middle of a jump (Links.Rise).
        public Vector3 ThingOffset(Thing t)
        {
            if (t is RimWorld.PawnFlyer f)
            {
                return Combat.JumpFlights.Offset(f);   // a jump between floors (JumpFlights)
            }
            if (!(t is Pawn p))
            {
                return Vector3.zero;
            }
            float rise = Links.Rise(p);
            return rise == 0f ? Vector3.zero : new Vector3(0f, rise, 0f);
        }
    }
}
