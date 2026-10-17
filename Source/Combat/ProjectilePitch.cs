using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ProjectZ.Combat
{
    // Projectiles between floors drawn along their real 3D flight, pitched down or up. IsoRim draws a projectile
    // (Projectiles.DrawAt) as the game's flat card turned only about the vertical (the game's ExactRotation, or a
    // thrown thing's spin), at a height that blends from the muzzle to the target: between floors that is a slope of
    // up to several cells, but the card stays flat. Here IsoRim's one draw of the card (its Graphics.DrawMesh call) is
    // routed through Draw, which for a shot between floors (Flights.cross, set at launch) tilts it from the flat flight
    // direction onto the drawn 3D one: from where IsoRim's drawn flight starts (Projectiles.DrawnStart: the real
    // shooter's muzzle, a manned mortar's barrel, which the launch patch tells IsoRim with SetLaunchPoint) to the
    // destination, plus the change of IsoRim's own drawn height over it (Projectiles.Height, true cells). The card is turned in true space and then squashed as IsoRim
    // draws everything (drawn y = true / Squash), so it shows the true angle and length. Bullets, arrows, rockets and
    // thrown things alike (a spinning thing's spin plane tilts with it); a lobbed shell's or grenade's arc stays as
    // IsoRim draws it (height, no pitch, as on one floor), only the floor-to-floor slope pitches it. Shots on one
    // floor are drawn exactly as before (the same DrawMesh call). Not saved: a shot in flight on load flies on flat.
    [HarmonyPatch]
    public static class ProjectilePitch
    {
        private static MethodBase target;

        private static readonly MethodInfo drawMesh = AccessTools.Method(typeof(Graphics), nameof(Graphics.DrawMesh), new[] { typeof(Mesh), typeof(Vector3), typeof(Quaternion), typeof(Material), typeof(int) });

        private static readonly AccessTools.FieldRef<Projectile, Vector3> originRef = AccessTools.FieldRefAccess<Projectile, Vector3>("origin");
        private static readonly AccessTools.FieldRef<Projectile, Vector3> destinationRef = AccessTools.FieldRefAccess<Projectile, Vector3>("destination");

        // Tests: the pitch (degrees, + up) each projectile was last drawn with, by thingIDNumber; only while a test runs.
        public static readonly Dictionary<int, float> drawnPitch = new Dictionary<int, float>();
        public static int tiltedDraws;
        public static bool Disabled;   // tests: drawn the old way (flat), for before/after shots
        public static int flatDraws;

        public static bool Prepare()
        {
            target = AccessTools.Method(AccessTools.TypeByName("IsoRim.Projectiles"), "DrawAt");
            return CombatGate.Enabled && target != null && drawMesh != null;
        }

        public static MethodBase TargetMethod() => target;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            MethodInfo hook = AccessTools.Method(typeof(ProjectilePitch), nameof(Draw));
            int n = 0;
            foreach (CodeInstruction ci in code)
            {
                if (ci.Calls(drawMesh))
                {
                    // (the projectile, argument 0, after DrawMesh's own arguments)
                    yield return new CodeInstruction(OpCodes.Ldarg_0).WithLabels(ci.ExtractLabels());
                    yield return new CodeInstruction(OpCodes.Call, hook);
                    n++;
                    continue;
                }
                yield return ci;
            }
            if (n != 1)
            {
                Log.Warning($"[ProjectZ] IsoRim's projectile draw has {n} card draws (expected 1): shots between floors may be drawn flat.");
            }
        }

        // In place of IsoRim's Graphics.DrawMesh(mesh, at, rotation, mat, layer) for projectile p.
        public static void Draw(Mesh mesh, Vector3 at, Quaternion rotation, Material mat, int layer, Projectile p)
        {
            if (p != null && !Disabled && Tilt(p, out Quaternion tilt))
            {
                Quaternion r = tilt * rotation;
                Matrix4x4 m = Matrix4x4.Translate(at) * Matrix4x4.Scale(new Vector3(1f, 1f / Mathf.Max(IsoRimBridge.Squash, 0.01f), 1f)) * Matrix4x4.Rotate(r);
                Graphics.DrawMesh(mesh, m, mat, layer);
                if (Dev.TestRunner.Active)
                {
                    tiltedDraws++;
                    Vector3 fwd = r * Vector3.forward;
                    drawnPitch[p.thingIDNumber] = Mathf.Atan2(fwd.y, new Vector2(fwd.x, fwd.z).magnitude) * Mathf.Rad2Deg;
                }
                return;
            }
            Graphics.DrawMesh(mesh, at, rotation, mat, layer);
            if (Dev.TestRunner.Active && p != null)
            {
                flatDraws++;
                drawnPitch[p.thingIDNumber] = 0f;
            }
        }

        // The turn from the flat flight direction to the drawn 3D one, for a shot between floors (worked out once).
        public static bool Tilt(Projectile p, out Quaternion tilt)
        {
            tilt = Quaternion.identity;
            if (!Flights.cross.TryGetValue(p, out Flights.Cross c))
            {
                return false;
            }
            if (!c.tiltKnown)
            {
                c.tiltKnown = true;
                // from where IsoRim's drawn flight starts (the muzzle: the launch point plus its lead) to the destination
                Vector3 o = originRef(p);
                Vector3 d = destinationRef(p);
                Vector3? start = IsoRimBridge.DrawnStart(p);
                float h0 = start.HasValue ? start.Value.y : IsoRimBridge.ProjectileHeight(p, o);
                if (start.HasValue)
                {
                    o = start.Value;
                }
                Vector3 flat = new Vector3(d.x - o.x, 0f, d.z - o.z);
                if (flat.sqrMagnitude > 0.0025f)
                {
                    float rise = IsoRimBridge.ProjectileHeight(p, d) - h0;
                    c.tilt = Quaternion.FromToRotation(flat, flat + new Vector3(0f, rise, 0f));
                    c.tilted = true;
                }
            }
            tilt = c.tilt;
            return c.tilted;
        }
    }
}
