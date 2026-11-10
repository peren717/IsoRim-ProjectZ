using System;
using System.Collections.Generic;
using System.Linq;
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
            (typeof(Verb), "CanHitCellFromCellIgnoringRange", null, "ec7f9fd296dc2225", "ProjectZ, Verb_CanHitCell_Patch.Fallback (a shot's sight rule without the game's method)"),
            (typeof(PawnFlyer), "CheckDestination", null, "93007b4cff28e958", "ProjectZ, PawnFlyer_CheckDestination_Patch (the landing search radius 3.9)"),
            (typeof(ExitMapGrid), "IsGoodExitCell", null, "a3c1abbd8ae12c88", "ProjectZ, ExitMapGrid_Rebuild_Patch.IsGoodExitCell (the exit cell rule, radius 2)"),
            (typeof(PowerBeam), "StartRandomFireAndDoFlameDamage", null, "186513ce9c20613f", "ProjectZ, PowerBeam_Fire_Patch (the beam's fire and flame damage)"),
            (typeof(JobDriver_Mine), "ResetTicksToPickHit", null, "944e16f6ef2d0fe5", "ProjectZ, MineValues.PickHitTicks (ticks between pick hits)"),
            // (a lambda: the work toil's tick, the one MakeNewToils lambda taking an int; see Find)
            (typeof(JobDriver_AffectRoof), "<MakeNewToils>b__", new[] { typeof(int) }, "ccf76e120c31f25b", "ProjectZ, MineValues.RoofWorkPerTick (Remove roof's work a tick, 1.7)"),
        };

        // A method by name and parameters; a name starting with "<" is a compiler-made lambda: the method of the type or
        // its nested types whose name starts with it and whose parameters are args (exactly one, or none found).
        private static MethodBase Find(Type type, string method, Type[] args)
        {
            if (!method.StartsWith("<"))
            {
                return args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
            }
            MethodBase found = null;
            int n = 0;
            foreach (Type t in new[] { type }.Concat(AccessTools.InnerTypes(type)))
            {
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t))
                {
                    if (m.Name.StartsWith(method) && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(args ?? Type.EmptyTypes))
                    {
                        found = m;
                        n++;
                    }
                }
            }
            return n == 1 ? found : null;
        }

        // The copies watched (tests: all of them registered).
        public static int Count => Copies.Length;

        // (tests) registered
        public static int Registered;

        public static IEnumerable<MethodBase> Methods()
        {
            foreach (var c in Copies)
            {
                yield return Find(c.type, c.method, c.args);
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
                if (m == null)
                {
                    Log.Warning($"[ProjectZ] The game's {c.type.Name}.{c.method} was not found: the copy it backs ({c.note}) is not watched.");
                    continue;
                }
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
                string now = m == null ? "(not found)" : IsoRim.Api.CopyHash(m);
                parts.Add($"{c.type.Name}.{c.method} {now}{(now == c.hash ? "" : " (registered " + c.hash + ")")}");
            }
            return string.Join("; ", parts);
        }
    }
}
