using HarmonyLib;
using UnityEngine;
using Verse;

namespace ProjectZ.Combat
{
    // The one measure of ProjectZ's interceptors between floors: how far an incoming thing is from an interceptor's centre
    // (user decisions 2026-10-17, void barrier Q4 "3D" and the orbital doc "Shields"; made one place 2026-11-11, Api 12).
    // Used by the shield bubbles (Shields3D: its dome check) and by other mods' interceptors (Api.InterceptorDistance).
    //  - Between floors means: the incoming thing's band (its row's, void rows counted with the band below; for anything
    //    that is not a projectile, the band of the cell it comes down on, not of the row it is drawn in) is not the
    //    centre's floor, or it is a projectile flying between floors (Flights.cross: its height runs between two floors
    //    even in one band). Otherwise (one band, no floors, the void rule off) the interceptor's own (the game's) check
    //    stands: TryDistance answers false.
    //  - Overhead: a lobbed projectile (Lobbed.Of: a shell, or a shot declared lobbed) and anything else that is not a
    //    projectile (a skyfaller, a drop pod: it comes down from the sky). An overhead thing is measured on the ground
    //    circle, whatever floor it comes down on: both points moved down onto the ground rows, heights ignored. (Shells and
    //    bombardments from the sky are stopped over the ground circle, as the cell measure of the bombardment checks,
    //    Heights.GroundDistance.)
    //  - Direct (any other projectile): in virtual 3D (x, z over the ground rows; y the height in cells: floor k at
    //    k * Range3D.Storey, a projectile between floors at its flight height (Flights), others at the middle of their
    //    floor). Under the centre's floor (more than UnderFloorMargin below it) it is never inside: the interceptor covers
    //    the space over its floor, a hemisphere for a shield.
    public static class Intercept3D
    {
        // How far under the centre's floor height a direct point may be and still count as over it (rounding of heights).
        public const float UnderFloorMargin = 0.05f;

        private static readonly AccessTools.FieldRef<Projectile, Vector3> originRef = AccessTools.FieldRefAccess<Projectile, Vector3>("origin");
        private static readonly AccessTools.FieldRef<Projectile, Vector3> destRef = AccessTools.FieldRefAccess<Projectile, Vector3>("destination");

        private static float Scale => Range3D.Storey / Heights.Storey;

        // Comes down from the sky (measured on the ground circle).
        public static bool Overhead(Thing incoming) => !(incoming is Projectile p) || Lobbed.Of(p);

        // The level of a raw position's band (its void rows counted with it), within the map's levels.
        public static int BandOf(Vector3 p, AnnexMapComponent an)
        {
            int z = Mathf.FloorToInt(p.z);
            if (z < an.groundZ)
            {
                return 0;
            }
            return Mathf.Clamp(Levels.LevelOfRowLoose(an.map, z), an.MinLevel, an.MaxLevel);
        }

        // The band an incoming thing at raw position pos is on: a projectile's is its position's band; anything else (a
        // skyfaller, a drop pod) is on the floor it comes down on, its cell (Position), whatever row it is drawn in (a falling
        // pod is drawn rows north of its cell, there maybe in the next floor's rows).
        public static int BandOf(Thing incoming, Vector3 pos, AnnexMapComponent an)
        {
            if (incoming is Projectile)
            {
                return BandOf(pos, an);
            }
            IntVec3 c = incoming.Position;
            return BandOf(new Vector3(c.x, 0f, c.z), an);
        }

        // The interceptor's map with floors (null: no floors, or the void rule off: the game's own checks).
        public static AnnexMapComponent Space(Map map) => VoidRule.Off ? null : ProjectZ.Annex(map);

        // Is incoming at pos measured between floors from an interceptor centred on cell centre (floor ks)? False: the
        // interceptor's own check (one band and no flight between floors; centre in a void row).
        public static bool Between(Thing incoming, Vector3 pos, IntVec3 centre, Map map, AnnexMapComponent an, out int ks)
        {
            ks = ProjectZ.FloorOf(centre, map);
            if (ks < 0 || incoming == null)
            {
                return false;
            }
            bool cross = incoming is Projectile p && Flights.cross.TryGetValue(p, out _);
            return cross || BandOf(incoming, pos, an) != ks;
        }

        // The centre in the measure's space (overhead: on the ground plane).
        public static Vector3 Centre(IntVec3 centre, int ks, Map map, bool overhead)
        {
            return new Vector3(centre.x + 0.5f, overhead ? 0f : ks * Range3D.Storey, centre.z + 0.5f - Levels.Shift(map, ks));
        }

        // The incoming thing's raw position in the measure's space (overhead: on the ground plane).
        public static Vector3 Point(Thing incoming, Vector3 pos, AnnexMapComponent an, bool overhead)
        {
            int k = BandOf(incoming, pos, an);
            Vector3 v = new Vector3(pos.x, HeightAt(incoming, pos, k) * Scale, pos.z - Levels.Shift(an.map, k));
            if (overhead)
            {
                v.y = 0f;
            }
            return v;
        }

        // A direct point (an entry point, a position) under the centre's floor: never inside.
        public static bool UnderFloor(Vector3 at, Vector3 centre, bool overhead) => !overhead && at.y < centre.y - UnderFloorMargin;

        // (Api 12) Where a shooter on its own floor aims at an incoming thing at raw position pos: that position in the
        // shooter's floor frame, so the flat angle from the shooter's drawn position to it is the true direction. The thing's
        // band is BandOf(Thing) (a thing that is not a projectile: the floor of its cell); its point is Point's (x, z over the
        // ground rows), moved into the shooter's floor's rows; y stays pos.y. On the shooter's band, without floors or with the
        // void rule off, a shooter in a void row: pos itself.
        public static Vector3 AimPoint(Thing shooter, Thing incoming, Vector3 pos)
        {
            Map map = shooter?.Map;
            AnnexMapComponent an = Space(map);
            if (an == null || incoming == null || !shooter.Spawned)
            {
                return pos;
            }
            int ks = ProjectZ.FloorOf(shooter.Position, map);
            if (ks < 0 || BandOf(incoming, pos, an) == ks)
            {
                return pos;
            }
            Vector3 g = Point(incoming, pos, an, true);
            return new Vector3(g.x, pos.y, g.z + Levels.Shift(map, ks));
        }

        // The distance between floors (Api 12, see above); false: one band or no floors, the interceptor's own measure.
        // PositiveInfinity for a direct point under the centre's floor.
        public static bool TryDistance(Thing incoming, Vector3 pos, IntVec3 centre, Map map, out float distance)
        {
            distance = 0f;
            AnnexMapComponent an = Space(map);
            if (an == null || !Between(incoming, pos, centre, map, an, out int ks))
            {
                return false;
            }
            bool overhead = Overhead(incoming);
            Vector3 s = Centre(centre, ks, map, overhead);
            Vector3 p = Point(incoming, pos, an, overhead);
            distance = UnderFloor(p, s, overhead) ? float.PositiveInfinity : (p - s).magnitude;
            return true;
        }

        // Height over the ground in wall units: a projectile between floors at its flight height along its path, others
        // (and anything not a projectile) at the middle of their floor.
        private static float HeightAt(Thing incoming, Vector3 pos, int k)
        {
            Flights.Flight f = incoming is Projectile p ? Flights.Of(p) : null;
            if (f == null)
            {
                return k * Heights.Storey + (Heights.Eye + Heights.Middle) / 2f;
            }
            Projectile pr = (Projectile)incoming;
            Vector3 o = originRef(pr);
            Vector3 d = destRef(pr);
            float len = new Vector2(d.x - o.x, d.z - o.z).magnitude;
            float t = len < 0.01f ? 1f : Mathf.Clamp01(new Vector2(pos.x - o.x, pos.z - o.z).magnitude / len);
            return Mathf.Lerp(f.hs, f.ht, t);
        }
    }
}
