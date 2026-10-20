using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ProjectZ.Combat;
using ProjectZ.Patches;
using RimWorld;
using Verse;

namespace ProjectZ.Dev
{
    // A patch class whose target does not exist: applied only when forced (-projectzfailpatch=FailSoftProbe_Patch), to run
    // Harmony's own failure for a missing target through FailSoft (test only; otherwise its Prepare says no).
    [HarmonyPatch(typeof(Pawn), "ProjectZFailSoftNoSuchMethod")]
    public static class FailSoftProbe_Patch
    {
        public static bool Prepare() => FailSoft.Forced(nameof(FailSoftProbe_Patch));

        public static void Postfix()
        {
        }
    }

    // Mode failsoft (FailSoft.cs), with -projectzfailpatch=<names> (the patches forced to fail) and optional
    // -projectzfailexpect=<names> (what else must be off, e.g. a closed group's members). No map work: the patch state only.
    //   S1 every forced patch is not applied and none of its patch methods is left on any method
    //   S2 one warning per failure (FailSoft.Warnings = forced classes + lists with forced entries + closed groups)
    //   S3 the rest is applied (counts; combat and stair routing on unless a member of theirs was forced)
    //   S4 a transpiler that loses its anchor AFTER loading (another mod's transpiler, Priority.First, takes the doomsday
    //      rocket's map clip away): the IL is handed back (no ClipInsideBand call in the method), one warning only
    //      (Harmony re-runs every transpiler twice more here)
    public static partial class TestRunner
    {
        private static readonly MethodInfo fsClipVanilla = AccessTools.Method(typeof(CellRect), nameof(CellRect.ClipInsideMap), new[] { typeof(Map) });

        private static void StepFailSoftChecks()
        {
            const string id = "Peren.IsoRimProjectZ";
            HashSet<string> forced = Names("projectzfailpatch");
            HashSet<string> expectOff = Names("projectzfailexpect");
            Info($"failsoft: {FailSoft.Applied} applied, failed [{string.Join(", ", FailSoft.Failed)}], undone with a group [{string.Join(", ", FailSoft.Undone)}], warnings {FailSoft.Warnings}");

            // S1
            List<string> bad = new List<string>();
            foreach (string name in forced.Concat(expectOff))
            {
                if (FailSoft.IsApplied(name))
                {
                    bad.Add(name + " still applied");
                }
                List<string> left = LeftOn(name, id);
                if (left.Count > 0)
                {
                    bad.Add(name + " left on " + string.Join(", ", left));
                }
                if (!FailSoft.Failed.Contains(name) && !FailSoft.Undone.Contains(name) && name != nameof(FailSoftProbe_Patch))
                {
                    bad.Add(name + " not reported");
                }
            }
            if (forced.Contains(nameof(FailSoftProbe_Patch)) && !FailSoft.Failed.Contains(nameof(FailSoftProbe_Patch)))
            {
                bad.Add("FailSoftProbe_Patch (missing target) not reported");
            }
            Result("failsoft S1: every forced patch is off, nothing of it left on any method", forced.Count > 0 && bad.Count == 0, bad.Count == 0 ? string.Join(", ", forced) : string.Join("; ", bad));

            // S2: one warning per failed class, per list with failures, per closed group
            int lists = new[] { "BandRadial:", "AimFloor:", "Dist3D:" }.Count(p => FailSoft.Failed.Any(f => f.StartsWith(p)));
            int classes = FailSoft.Failed.Count(f => !f.Contains(":"));
            int groups = (CombatGate.Enabled ? 0 : 1) + (FailSoft.Undone.Any(u => u.Contains("RouteGraph") || u.Contains("Reach")) ? 1 : 0);
            Result("failsoft S2: one warning per failure (classes + lists + closed groups)", FailSoft.Warnings == classes + lists + groups,
                $"warnings {FailSoft.Warnings}, expected {classes} classes + {lists} lists + {groups} groups");

            // S3
            bool combatForced = forced.Concat(expectOff).Any(n => typeof(TestRunner).Assembly.GetType("ProjectZ.Combat." + n)?.GetCustomAttribute<PatchGroupAttribute>()?.group == FailSoft.Combat);
            bool routeForced = forced.Concat(expectOff).Any(n => AllTypes().FirstOrDefault(t => t.Name == n)?.GetCustomAttribute<PatchGroupAttribute>()?.group == FailSoft.StairRouting);
            bool combatOk = combatForced ? !CombatGate.Enabled && !FailSoft.IsApplied(nameof(Verb_CanHitCell_Patch)) && AimFloor.Rows.All(r => !FailSoft.IsApplied(r.Entry))
                : CombatGate.Enabled && FailSoft.IsApplied(nameof(Verb_CanHitCell_Patch)) && FailSoft.IsApplied(nameof(Projectile_Launch_Patch));
            bool routeOk = routeForced ? !FailSoft.IsApplied(nameof(FindPathNow_Stairs_Patch)) && !FailSoft.IsApplied(nameof(BFSWorker_Patch))
                : FailSoft.IsApplied(nameof(FindPathNow_Stairs_Patch)) && FailSoft.IsApplied(nameof(BFSWorker_Patch)) && FailSoft.IsApplied(nameof(GetCachedResult_Patch));
            int rows = BandRadial.Rows.Count(r => FailSoft.IsApplied(r.entry)), d3 = Dist3D.Rows.Count(r => FailSoft.IsApplied(Dist3D_Patch.EntryName(r)));
            Result("failsoft S3: everything else applied (combat and stair routing as expected)", combatOk && routeOk && FailSoft.Applied > 300 && rows >= BandRadial.Rows.Count - forced.Count && d3 >= Dist3D.Rows.Length - forced.Count,
                $"combat {(CombatGate.Enabled ? "on" : "off")} (forced member {combatForced}), stair routing {(FailSoft.IsApplied(nameof(FindPathNow_Stairs_Patch)) ? "on" : "off")} (forced member {routeForced}), radius rows {rows}/{BandRadial.Rows.Count}, 3D distance rows {d3}/{Dist3D.Rows.Length}, applied {FailSoft.Applied}");

            // S4
            MethodBase impact = AccessTools.Method(typeof(Projectile_DoomsdayRocket), "Impact");
            MethodInfo ours = AccessTools.Method(typeof(DoomsdayRocket_Impact_Patch), nameof(DoomsdayRocket_Impact_Patch.ClipInsideBand));
            if (!FailSoft.IsApplied(nameof(DoomsdayRocket_Impact_Patch)))
            {
                Info("failsoft S4 skipped: DoomsdayRocket_Impact_Patch not applied");
                return;
            }
            bool before = PatchProcessor.GetCurrentInstructions(impact).Any(c => c.Calls(ours));
            int w0 = FailSoft.Warnings;
            Harmony hostile = new Harmony("Peren.IsoRimProjectZ.test.failsoft");
            hostile.Patch(impact, transpiler: new HarmonyMethod(typeof(TestRunner), nameof(FsHostileClip)) { priority = Priority.First });
            hostile.Patch(impact, prefix: new HarmonyMethod(typeof(TestRunner), nameof(FsNoop)));   // one more re-run
            List<CodeInstruction> now = PatchProcessor.GetCurrentInstructions(impact);
            bool handedBack = !now.Any(c => c.Calls(ours)) && now.Any(c => c.Calls(AccessTools.Method(typeof(TestRunner), nameof(FsClip))));
            int w1 = FailSoft.Warnings;
            hostile.UnpatchAll(hostile.Id);
            Result("failsoft S4: an anchor lost after loading (another mod's transpiler): IL handed back, one warning", before && handedBack && w1 - w0 == 1,
                $"swapped before {before}, handed back {handedBack}, warnings +{w1 - w0}");
        }

        private static HashSet<string> Names(string arg)
        {
            HashSet<string> s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (GenCommandLine.TryGetCommandLineArg(arg, out string v) && !string.IsNullOrEmpty(v))
            {
                foreach (string n in v.Split(','))
                {
                    if (n.Trim().Length > 0)
                    {
                        s.Add(n.Trim());
                    }
                }
            }
            return s;
        }

        private static IEnumerable<Type> AllTypes() => AccessTools.GetTypesFromAssembly(typeof(TestRunner).Assembly);

        // Our patch methods of 'name' still on a method: a class's methods anywhere; a list entry's on its targets.
        private static List<string> LeftOn(string name, string id)
        {
            List<string> left = new List<string>();
            Type cls = AllTypes().FirstOrDefault(t => t.Name == name && t.HasHarmonyAttribute());
            IEnumerable<MethodBase> where;
            Func<MethodInfo, bool> mine;
            if (cls != null)
            {
                where = Harmony.GetAllPatchedMethods();
                mine = pm => pm.DeclaringType == cls;
            }
            else if (name.StartsWith("BandRadial:"))
            {
                where = name.EndsWith("ProcessEquidistantCells") ? new[] { (MethodBase)AccessTools.Method(typeof(GenRadial), nameof(GenRadial.ProcessEquidistantCells)) }
                    : BandRadial.Rows.Where(r => r.entry == name && r.target != null).Select(r => r.target);
                mine = pm => pm.DeclaringType == typeof(BandRadial);
            }
            else if (name.StartsWith("AimFloor:"))
            {
                where = AimFloor.Rows.Where(r => r.Entry == name && r.found != null).Select(r => r.found);
                mine = pm => pm.DeclaringType == typeof(AimFloor);
            }
            else if (name.StartsWith("Dist3D:"))
            {
                where = Dist3D.Rows.Where(r => Dist3D_Patch.EntryName(r) == name).SelectMany(r => r.targets);
                mine = pm => pm.DeclaringType == typeof(Dist3D_Patch);
            }
            else
            {
                left.Add("(unknown name)");
                return left;
            }
            foreach (MethodBase m in where.ToList())
            {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(m);
                if (info != null && info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Any(p => p.owner == id && mine(p.PatchMethod)))
                {
                    left.Add(m.DeclaringType?.Name + "." + m.Name);
                }
            }
            return left;
        }

        // The hostile transpiler (S4): the map clip through a wrapper of its own (same result), so ProjectZ's anchor is gone.
        public static IEnumerable<CodeInstruction> FsHostileClip(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(fsClipVanilla))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(TestRunner), nameof(FsClip));
                }
                yield return ci;
            }
        }

        public static CellRect FsClip(ref CellRect r, Map map) => r.ClipInsideMap(map);

        public static void FsNoop()
        {
        }
    }
}
