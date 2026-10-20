using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ.Patches
{
    // Targeting ranges between floors (user decision 2026-10-16): a targeted permit's range around its caller, and an
    // ability's destination range around its first target, are measured like weapon range: over the ground plus the
    // height between the floors (Range3D.Distance; no shooting-down bonus, nothing is shot). Vanilla's conditions stay:
    // permits only have a range when the weather caps it (RangeClamped > 0).
    //
    // The game checks these with a raw IntVec3Utility.DistanceTo on the map's rows, where floor 2 is a whole band away. Inventory
    // (decompile 1.7.4979): the targeting validators of RoyalTitlePermitWorker_CallShuttle, _CallLaborers, _CallAid,
    // _DropResources and _OrbitalStrike (lambdas in their Begin* methods) and CompAbilityEffect_WithDest.CanHitTarget.
    // Every other targeted range goes through a verb (Verb.OutOfRange, RangePatches). One transpiler swaps that one call
    // for PermitRange.Distance in every method of those classes (and their subclasses and closures) that makes it.
    public static class PermitRange
    {
        // Distance between two cells of the current map (targeting is always on it); vanilla's without floors.
        public static float Distance(IntVec3 a, IntVec3 b)
        {
            Map map = Find.CurrentMap;
            if (ProjectZ.Annex(map) == null || ProjectZ.FloorOf(a, map) < 0 || ProjectZ.FloorOf(b, map) < 0)
            {
                return a.DistanceTo(b);
            }
            return Combat.Range3D.Distance(a, b, map);
        }

        // The same on an ability's own map (AI pawns cast on maps that are not the current one).
        public static float DistanceOn(IntVec3 a, IntVec3 b, CompAbilityEffect_WithDest comp)
        {
            Map map = comp?.parent?.pawn?.MapHeld;
            if (ProjectZ.Annex(map) == null || ProjectZ.FloorOf(a, map) < 0 || ProjectZ.FloorOf(b, map) < 0)
            {
                return a.DistanceTo(b);
            }
            return Combat.Range3D.Distance(a, b, map);
        }
    }

    [HarmonyPatch]
    public static class PermitRange_Patch
    {
        private static readonly MethodInfo Raw = AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceTo), new[] { typeof(IntVec3), typeof(IntVec3) });
        private static readonly MethodInfo Ours = AccessTools.Method(typeof(PermitRange), nameof(PermitRange.Distance));
        private static readonly MethodInfo OursOn = AccessTools.Method(typeof(PermitRange), nameof(PermitRange.DistanceOn));

        public static IEnumerable<MethodBase> TargetMethods()
        {
            Type[] bases = { typeof(RoyalTitlePermitWorker_Targeted), typeof(CompAbilityEffect_WithDest) };
            List<Type> types = typeof(Pawn).Assembly.GetTypes().Where(t => bases.Any(b => b.IsAssignableFrom(t))).ToList();
            HashSet<MethodBase> found = new HashSet<MethodBase>();
            foreach (Type t in types)
            {
                foreach (Type x in new[] { t }.Concat(t.GetNestedTypes(AccessTools.all)))
                {
                    foreach (MethodInfo m in x.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly))
                    {
                        if (!m.IsAbstract && !m.ContainsGenericParameters && m.Name != nameof(CompAbilityEffect_WithDest.GetDestination) && Calls(m))
                        {
                            found.Add(m);
                        }
                    }
                }
            }
            ProjectZ.DebugLog($"targeting ranges between floors: {found.Count} methods ({string.Join(", ", found.Select(m => m.DeclaringType.Name + "." + m.Name))})");
            return found;
        }

        private static bool Calls(MethodInfo m)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(m).Any(p => (p.Key == OpCodes.Call || p.Key == OpCodes.Callvirt) && Equals(p.Value, Raw));
            }
            catch (Exception)
            {
                return false;
            }
        }

        // (an ability comp's own method passes itself for its map; the permits' targeting closures use the current map)
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            return FailSoft.Transpile(nameof(PermitRange_Patch), original, instructions, code =>
            {
                bool comp = !original.IsStatic && typeof(CompAbilityEffect_WithDest).IsAssignableFrom(original.DeclaringType);
                int n = 0;
                for (int i = 0; i < code.Count; i++)
                {
                    CodeInstruction ci = code[i];
                    if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && Equals(ci.operand, Raw))
                    {
                        if (comp)
                        {
                            code.Insert(i, new CodeInstruction(OpCodes.Ldarg_0));
                            i++;
                            ci.operand = OursOn;
                        }
                        else
                        {
                            ci.operand = Ours;
                        }
                        ci.opcode = OpCodes.Call;
                        n++;
                    }
                }
                return n > 0 ? null : "no distance call found";
            });
        }
    }
}
