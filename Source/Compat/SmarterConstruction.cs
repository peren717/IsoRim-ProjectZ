using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace ProjectZ.Compat
{
    // Smarter Construction (dhultgren.smarterconstruction). Before a wall frame is finished, that mod flood-fills the
    // path grid's walkable cells round the frame; a pocket under 100 cells counts as closed, and when it holds another
    // blueprint/frame or a player pawn the job is refused. Floors are joined by stairs, ladders and lifts (vertical links)
    // in our reachability and router, not in the path grid, so a small upper floor, or a ground room left only by stairs,
    // looked closed: walls there were never built (user report: a constructoid would not build upper-floor walls).
    // No reference to the mod: every member is found by name, and when one is missing nothing is patched (one warning;
    // the game then behaves as with the mod alone).
    //   A. its flood fill (ClosedRegionDetector.FloodFill, behind every enclose check, the finish toil's check and its
    //      safe-spot search too): a pocket holding an end of a vertical link (stairs, a ladder, a lift) is not closed
    //      (returned empty, which the mod reads as "open"). Pockets without a link end, on the ground or upstairs, keep the
    //      mod's protection (user 2026-11-07: a wall sealing a colonist into a small upstairs room is still refused).
    //   B. its own closest-frame choice (CustomGenClosest, which it swaps in for GenClosest's in JobGiver_Work) measures
    //      the frame distance with our 3D distance (Dist3D, the rule GenClosest gets): a frame right upstairs is near.
    //      On one band, and on maps without floors, the value is the mod's own bit for bit.
    [StaticConstructorOnStartup]
    public static class SmarterConstruction
    {
        public const string PackageId = "dhultgren.smarterconstruction";
        private const string DetectorName = "SmarterConstruction.Core.ClosedRegionDetector";
        private const string ClosestName = "SmarterConstruction.Patches.CustomGenClosest";
        private const string ClosestMethod = "ClosestThing_Global_Reachable_Custom";
        private const string HarmonyId = "Peren.IsoRimProjectZ.smarterconstruction";

        public enum Status { Absent, Skipped, Active }

        public static Status State { get; private set; } = Status.Absent;
        public static string SkipReason { get; private set; }
        // distance expressions swapped in the mod's closest-frame choice (1 expected)
        public static int DistSwaps { get; private set; }
        // pockets the mod saw as closed that we opened: holding a link end (tests)
        public static int OpenedLink;
        // Test only: the patches stay in but act as the mod alone (the control).
        public static bool Off;
        // Test only (-projectzsmartconbreak=<member>): that member of the mod counts as missing.
        internal static string SimulateMissing => Dev.TestRunner.SmartConBreak;

        private static readonly MethodInfo Sub = AccessTools.Method(typeof(IntVec3), "op_Subtraction", new[] { typeof(IntVec3), typeof(IntVec3) });
        private static readonly MethodInfo LenSq = AccessTools.PropertyGetter(typeof(IntVec3), nameof(IntVec3.LengthHorizontalSquared));

        static SmarterConstruction()
        {
            if (!ModsConfig.ActiveModsInLoadOrder.Any(m => m.PackageIdNonUnique == PackageId))
            {
                return;
            }
            try
            {
                string missing = Patch();
                if (missing != null)
                {
                    State = Status.Skipped;
                    SkipReason = missing;
                    DistSwaps = 0;
                    Log.Warning("[ProjectZ] Smarter Construction compat off (" + missing + "): its enclose check ignores stairs between floors.");
                    return;
                }
                State = Status.Active;
                Log.Message($"[ProjectZ] Smarter Construction compat on: enclose check across floors, {DistSwaps} frame distance");
            }
            catch (Exception e)
            {
                State = Status.Skipped;
                SkipReason = e.Message;
                DistSwaps = 0;
                new Harmony(HarmonyId).UnpatchAll(HarmonyId);
                Log.Warning("[ProjectZ] Smarter Construction compat off (" + e.Message + "): its enclose check ignores stairs between floors.");
            }
        }

        // Resolves every member first and patches only when all of them are there; returns what is missing, else null.
        private static string Patch()
        {
            Type detector = SimulateMissing == "detector" ? null : AccessTools.TypeByName(DetectorName);
            Type closest = SimulateMissing == "closest" ? null : AccessTools.TypeByName(ClosestName);
            Type walk = AccessTools.TypeByName("SmarterConstruction.Core.IWalkabilityHandler");
            if (detector == null || closest == null || walk == null)
            {
                return "types not found";
            }
            MethodInfo flood = SimulateMissing == "FloodFill" ? null : AccessTools.Method(detector, "FloodFill", new[] { walk, typeof(IntVec3), typeof(HashSet<IntVec3>) });
            if (flood == null || flood.ReturnType != typeof(HashSet<IntVec3>))
            {
                return "no FloodFill";
            }
            // the lambda that scores each frame: the one of the method's lambdas that measures (center - t.Position)
            List<MethodInfo> scorers = SimulateMissing == "scorer" ? new List<MethodInfo>() : Lambdas(closest, ClosestMethod).Where(m => Swaps(PatchProcessor.GetOriginalInstructions(m)) > 0).ToList();
            if (scorers.Count != 1)
            {
                return scorers.Count + " frame distance lambdas";
            }
            DistSwaps = Swaps(PatchProcessor.GetOriginalInstructions(scorers[0]));
            Harmony h = new Harmony(HarmonyId);
            h.Patch(flood, postfix: new HarmonyMethod(typeof(SmarterConstruction), nameof(FloodFillPostfix)));
            h.Patch(scorers[0], transpiler: new HarmonyMethod(typeof(SmarterConstruction), nameof(Transpiler)));
            return null;
        }

        // The compiler's lambdas of method 'outer' of type t (in t and its nested closure classes).
        private static IEnumerable<MethodInfo> Lambdas(Type t, string outer)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            string prefix = "<" + outer + ">b__";
            foreach (Type n in new[] { t }.Concat(t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)))
            {
                foreach (MethodInfo m in n.GetMethods(all))
                {
                    if (m.Name.StartsWith(prefix) && m.GetMethodBody() != null)
                    {
                        yield return m;
                    }
                }
            }
        }

        // (a - b) stored in a local and read back as .LengthHorizontalSquared: the shape Dist3D swaps in the game's methods.
        private static bool IsDistance(List<CodeInstruction> code, int i)
        {
            return code[i].Calls(Sub) && i + 3 < code.Count && code[i + 1].IsStloc() && code[i + 2].opcode.Name.StartsWith("ldloca") && code[i + 3].Calls(LenSq)
                && code[i + 1].labels.Count == 0 && code[i + 2].labels.Count == 0 && code[i + 3].labels.Count == 0;
        }

        private static int Swaps(List<CodeInstruction> code)
        {
            int n = 0;
            for (int i = 0; i < code.Count; i++)
            {
                if (IsDistance(code, i))
                {
                    n++;
                }
            }
            return n;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = instructions.ToList();
            MethodInfo with = AccessTools.Method(typeof(SmarterConstruction), nameof(FrameDistSq));
            for (int i = 0; i < code.Count; i++)
            {
                if (!IsDistance(code, i))
                {
                    continue;
                }
                for (int k = 1; k <= 3; k++)
                {
                    code[i + k].opcode = OpCodes.Nop;
                    code[i + k].operand = null;
                }
                code[i].operand = with;
            }
            return code;
        }

        // (center - frame).LengthHorizontalSquared in 3D. The lambda has no map: Dist3D's context map, set for the whole of
        // JobGiver_Work.TryIssueJobPackage (Dist3D_WorkContext_Patch), the method the mod calls its choice from.
        public static int FrameDistSq(IntVec3 a, IntVec3 b)
        {
            return Off ? (a - b).LengthHorizontalSquared : Patches.Dist3D.SubLenSq(a, b, null);
        }

        // A closed pocket the mod found is open when it holds an end of a vertical link: floors are joined outside the path
        // grid, so a small upper floor reached by stairs, or a ground room left by stairs, is no pocket. One without a link
        // end is closed on any floor. Empty = "not a closed pocket" to the mod.
        private static void FloodFillPostfix(object walkabilityHandler, ref HashSet<IntVec3> __result)
        {
            if (Off || __result == null || __result.Count == 0 || !(walkabilityHandler is MapComponent mc))
            {
                return;
            }
            Map map = mc.map;
            if (ProjectZ.Annex(map) == null)
            {
                return;
            }
            StairGrid g = StairGrid.For(map);
            if (g == null)
            {
                return;
            }
            foreach (VLink l in g.links)
            {
                if (__result.Contains(l.bottom) || __result.Contains(l.top) || __result.Contains(l.low) || __result.Contains(l.mid))
                {
                    OpenedLink++;
                    __result = new HashSet<IntVec3>();
                    return;
                }
            }
        }
    }
}
