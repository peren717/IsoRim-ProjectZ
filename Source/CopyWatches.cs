using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // The game's methods ProjectZ still copies (each copy labelled in its code; no public method gives the value), watched
    // with IsoRim's copy watch (IsoRim Api 70, WatchCopy): when a game update changes one of them, IsoRim logs one line
    // naming the copy here, so the copy is checked instead of going wrong silently (Harmony patches on the method do not
    // count). Fail-soft: registered only with an IsoRim of Api 70 or later (ProjectZ still loads on an older one). The
    // hashes are IsoRim's Api.CopyHash of the game's build these were copied from (the debug action "Log copied method
    // hashes" prints the current ones). From the Mod constructor: checked when loading finishes.
    public static class CopyWatches
    {
        public const int NeedApi = 70;

        private static readonly (Type type, string method, Type[] args, string hash, string note)[] Copies =
        {
            (typeof(Projectile), "CheckForFreeIntercept", new[] { typeof(IntVec3) }, "c6b8236cf69a9b4c", "ProjectZ, Projectile_Intercept_Patch.InterceptChance (intercept chances)"),
            (typeof(ShotReport), nameof(ShotReport.HitReportFor), null, "e4356e075214a959", "ProjectZ, ShotReport_Patch.SmokeFactor (blind smoke 0.7)"),
            (typeof(CastPositionFinder), nameof(CastPositionFinder.TryFindCastPosition), null, "bc5b6ff2d2b0bb55", "ProjectZ, CastSpots.PrefBegin (the optimal range)"),
            (typeof(CastPositionFinder), "EvaluateCell", null, "73dd932fb9233b42", "ProjectZ, CastSpots.Preference (the avoid grid's factor)"),
            (typeof(AvoidGrid), "PrintAvoidGridAroundTurret", null, "e4c63f1905e25ab9", "ProjectZ, AvoidGrid_Turret_Patch.Mark (a turret's avoid marks)"),
            (typeof(JobGiver_AnimalFlee), "TryGiveJob", null, "4e2790d4e9c85999", "ProjectZ, FloorUnstrand.CalmRadius (the flee distance 24)"),
        };

        // (tests) registered
        public static int Registered;

        public static IEnumerable<MethodBase> Methods()
        {
            foreach (var c in Copies)
            {
                yield return c.args == null ? AccessTools.Method(c.type, c.method) : AccessTools.Method(c.type, c.method, c.args);
            }
        }

        public static void Register()
        {
            if (!IsoRimBridge.Present || IsoRimBridge.ApiVersion < NeedApi)
            {
                return;
            }
            try
            {
                RegisterInner();
            }
            catch (Exception e)
            {
                Log.Warning("[ProjectZ] Registering the copied methods with IsoRim's copy watch failed: " + e.Message);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterInner()
        {
            int i = 0;
            foreach (MethodBase m in Methods())
            {
                var c = Copies[i++];
                IsoRim.Api.WatchCopy(m, c.hash, c.note);
                Registered++;
            }
        }

        // (tests, IsoRim Api 70) the current hash of every watched method: "Type.Method hash"
        public static string Hashes()
        {
            if (!IsoRimBridge.Present || IsoRimBridge.ApiVersion < NeedApi)
            {
                return "(IsoRim Api " + IsoRimBridge.ApiVersion + ": no copy watch)";
            }
            return HashesInner();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string HashesInner()
        {
            List<string> parts = new List<string>();
            int i = 0;
            foreach (MethodBase m in Methods())
            {
                var c = Copies[i++];
                string now = IsoRim.Api.CopyHash(m);
                parts.Add($"{c.type.Name}.{c.method} {now}{(now == c.hash ? "" : " (registered " + c.hash + ")")}");
            }
            return string.Join("; ", parts);
        }
    }
}
