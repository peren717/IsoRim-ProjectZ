using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Combat
{
    // Shield bubbles across floors (user decisions 2026-10-17: void barrier Q4 "3D", orbital doc "Shields"). A shield is a
    // hemisphere of its radius over its centre, on its floor. A projectile is stopped where its 3D path enters the
    // hemisphere from outside (vanilla: where its flat path crosses the circle; outgoing shots pass). Shells flying
    // overhead are stopped over the ground circle, whatever floor they come down on. Positions are the virtual 3D ones
    // (over the ground, plus Range3D.Storey per floor): bullets between floors at their flight height (Flights), others
    // at the middle of their floor. On one band with a plain shot this is the game's own check, unchanged.
    // The rest (active, hostility, hit points, EMP, effects) is the game's: when the 3D path enters, the game's check
    // is run with the two positions placed just outside and inside its circle on the shield's band, in the direction
    // of the 3D entry point (the flash shows there).
    [HarmonyPatch(typeof(CompProjectileInterceptor), nameof(CompProjectileInterceptor.CheckIntercept))]
    public static class ProjectileInterceptor_3D_Patch
    {
        public static int Checked;   // counters (tests)
        public static int Entered;

        private static readonly AccessTools.FieldRef<Projectile, Vector3> originRef = AccessTools.FieldRefAccess<Projectile, Vector3>("origin");
        private static readonly AccessTools.FieldRef<Projectile, Vector3> destRef = AccessTools.FieldRefAccess<Projectile, Vector3>("destination");

        private static float Scale => Range3D.Storey / Heights.Storey;

        // The level of a raw position's band (its void rows counted with it), within the map's levels.
        private static int BandOf(Vector3 p, AnnexMapComponent an)
        {
            int z = Mathf.FloorToInt(p.z);
            if (z < an.groundZ)
            {
                return 0;
            }
            return Mathf.Clamp(Levels.LevelOfRowLoose(an.map, z), an.MinLevel, an.MaxLevel);
        }

        // A raw position on level k to virtual 3D (y in cells).
        private static Vector3 V(Vector3 p, int k, float h, AnnexMapComponent an)
        {
            return new Vector3(p.x, h * Scale, p.z - Levels.Shift(an.map, k));
        }

        private static float HeightAt(Projectile p, Vector3 pos, int k)
        {
            Flights.Flight f = Flights.Of(p);
            if (f == null)
            {
                return k * Heights.Storey + (Heights.Eye + Heights.Middle) / 2f;
            }
            Vector3 o = originRef(p);
            Vector3 d = destRef(p);
            float len = new Vector2(d.x - o.x, d.z - o.z).magnitude;
            float t = len < 0.01f ? 1f : Mathf.Clamp01(new Vector2(pos.x - o.x, pos.z - o.z).magnitude / len);
            return Mathf.Lerp(f.hs, f.ht, t);
        }

        // Where the segment p0 -> p1 crosses the sphere (s, r): entering from outside (or leaving, if outgoing counts).
        private static bool Crosses(Vector3 p0, Vector3 p1, Vector3 s, float r, bool outgoing, out Vector3 at)
        {
            at = p1;
            Vector3 d = p1 - p0;
            Vector3 m = p0 - s;
            float a = Vector3.Dot(d, d);
            if (a < 1e-6f)
            {
                return false;
            }
            float b = 2f * Vector3.Dot(d, m);
            float c = Vector3.Dot(m, m) - r * r;
            float disc = b * b - 4f * a * c;
            if (disc < 0f)
            {
                return false;
            }
            float q = Mathf.Sqrt(disc);
            float t = c > 0f ? (-b - q) / (2f * a) : (outgoing ? (-b + q) / (2f * a) : -1f);
            if (t < 0f || t > 1f)
            {
                return false;
            }
            at = p0 + d * t;
            return true;
        }

        public static bool Prefix(CompProjectileInterceptor __instance, Projectile projectile, ref Vector3 lastExactPos, ref Vector3 newExactPos, ref bool __result, out byte __state)
        {
            __state = FinalSay.Pass;
            return Check(__instance, projectile, ref lastExactPos, ref newExactPos, ref __result, ref __state);
        }

        // Final say between floors (FinalSay): a projectile this check let pass stays passed. When another mod's prefix
        // skipped ours, checked here (a pass stands; an entry into the dome is left to what ran).
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(CompProjectileInterceptor __instance, Projectile projectile, Vector3 lastExactPos, Vector3 newExactPos, ref bool __result, byte __state)
        {
            if (__state == FinalSay.NotRun)
            {
                FinalSay.PrefixSkipped++;
                bool mine = false;
                byte s = FinalSay.Pass;
                Check(__instance, projectile, ref lastExactPos, ref newExactPos, ref mine, ref s);
                __state = s;
            }
            FinalSay.Bool(__state, ref __result);
        }

        private static bool Check(CompProjectileInterceptor __instance, Projectile projectile, ref Vector3 lastExactPos, ref Vector3 newExactPos, ref bool __result, ref byte __state)
        {
            Map map = __instance.parent?.Map;
            AnnexMapComponent an = VoidRule.Off ? null : ProjectZ.Annex(map);
            if (an == null || projectile == null)
            {
                return true;
            }
            IntVec3 sc = __instance.parent.Position;
            int ks = ProjectZ.FloorOf(sc, map);
            int kp = BandOf(newExactPos, an);
            bool cross = Flights.cross.TryGetValue(projectile, out _);
            if (ks < 0 || !cross && kp == ks)
            {
                return true;   // the game's own check on one band
            }
            Checked++;
            CompProperties_ProjectileInterceptor props = __instance.Props;
            float r = props.radius;
            bool overhead = Lobbed.Of(projectile);   // a shell, or a shot declared lobbed
            Vector3 s = new Vector3(sc.x + 0.5f, ks * Range3D.Storey, sc.z + 0.5f - Levels.Shift(map, ks));
            int k0 = BandOf(lastExactPos, an);
            Vector3 p0 = V(lastExactPos, k0, HeightAt(projectile, lastExactPos, k0), an);
            Vector3 p1 = V(newExactPos, kp, HeightAt(projectile, newExactPos, kp), an);
            if (overhead)
            {
                s.y = p0.y = p1.y = 0f;
            }
            __result = false;
            __state = FinalSay.No;
            if ((p1 - s).magnitude > r + projectile.def.projectile.SpeedTilesPerTick + 0.1f)
            {
                return false;
            }
            if (!Crosses(p0, p1, s, r, props.interceptOutgoingProjectiles, out Vector3 at))
            {
                return false;
            }
            if (!overhead && at.y < s.y - 0.05f)
            {
                return false;   // under the dome's floor
            }
            Entered++;
            Vector2 dir = new Vector2(at.x - s.x, at.z - s.z);
            dir = dir.sqrMagnitude < 1e-4f ? Vector2.up : dir.normalized;
            Vector3 c = sc.ToVector3Shifted();
            Vector3 d3 = new Vector3(dir.x, 0f, dir.y);
            lastExactPos = c + d3 * (r + 0.5f);
            newExactPos = c + d3 * Mathf.Max(0f, r - 0.3f);
            __state = FinalSay.Pass;   // the game decides, with the positions moved
            return true;
        }
    }
}
