using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace ProjectZ
{
    // Fail-soft patching. A patch that cannot be applied (its target missing or renamed, an overload changed, another
    // mod's transpiler changed the IL, a transpiler's anchor not found) turns off only the feature behind it; ProjectZ
    // never throws at startup. One mechanism, used by every patch:
    //   - attribute patch classes are applied one class at a time (PatchAll -> Class); a class that fails is undone whole
    //     (the parts that did land are removed), so a prefix/postfix pair (FinalSay) never stays half applied;
    //   - list patches (BandRadial, AimFloor, Dist3D) and manual patches are applied entry by entry (Entry), each with an
    //     optional check run after it landed ("did the transpiler swap anything");
    //   - every transpiler runs its edit through Transpile: an edit that reports a missing anchor (or throws) hands the
    //     method's IL back as it came, and its patch counts as failed;
    //   - groups: a failed KEY patch closes its group, every applied member is undone. Combat ("combat": the cross-floor
    //     sight/shooting core, CombatGate off as with Combat Extended loaded) and stair routing ("stair routing": reach,
    //     paths and the route graph; each floor then stands on its own).
    //   - combat parts (Api 6, CombatGate): a class tagged [CombatPartOf] or an entry with a part is applied only while
    //     its part is on. With a foreign combat system the parts it turns off are not known before every Mod constructor
    //     has run: their classes are listed here and applied later (ApplyCombat, from CombatGate.Settle: the combat
    //     group patched later); a failed key patch there still closes its group. A part released after its patches
    //     landed has them undone (UndoParts; listed in Released, not as failed).
    // Log: one warning per failed patch (a list's failures in one line), and once loading is done one summary line
    // "[ProjectZ] N patches applied, M skipped: <names>".
    // Later: when another mod patches a method after ProjectZ, Harmony runs every transpiler on it again; one that lost
    // its anchor then hands the IL back at once, and its patch is undone when loading is done (after that: a warning only).
    // Test switch -projectzfailpatch=<name>,<name>: those patches fail on purpose. A class: undone right after it landed
    // (Dev FailSoftProbe_Patch: a real missing target); a transpiler: its anchor reported missing; a list entry or a
    // manual patch: its target missing. Names: the class name, or "<list>:<entry>" as in the summary.
    public static class FailSoft
    {
        public const string Combat = "combat";
        public const string StairRouting = "stair routing";

        private sealed class Unit
        {
            public string name, group;
            public bool key;
            public CombatPart part;
            public Action undo;
            public Func<string> check;
        }

        private static Harmony harmony;
        private static readonly Dictionary<string, Unit> applied = new Dictionary<string, Unit>();
        private static readonly List<(string name, string reason)> pending = new List<(string, string)>();
        private static readonly Dictionary<string, string> failedKeys = new Dictionary<string, string>();   // group -> its first failed key patch
        private static readonly HashSet<string> closed = new HashSet<string>();
        private static readonly HashSet<string> warned = new HashSet<string>();
        private static HashSet<string> forced;
        private static string current;        // the patch being applied now
        private static List<string> batch;    // a list's failures, logged as one line
        private static string batchName;
        private static bool finished;

        // For the summary and the tests: patches that failed, and the members a closed group undid.
        public static readonly List<string> Failed = new List<string>();
        public static readonly List<string> Undone = new List<string>();
        public static readonly List<string> Released = new List<string>();

        // The classes of the combat parts a foreign combat system turns off (CombatGate), in assembly order: applied by
        // ApplyCombat once the parts are settled.
        private static readonly List<Type> combatClasses = new List<Type>();
        public static int Warnings;

        public static int Applied => applied.Count;

        public static bool IsApplied(string name) => applied.ContainsKey(name);

        public static bool Forced(string name)
        {
            if (forced == null)
            {
                forced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (GenCommandLine.TryGetCommandLineArg("projectzfailpatch", out string v) && !string.IsNullOrEmpty(v))
                {
                    foreach (string s in v.Split(','))
                    {
                        if (s.Trim().Length > 0)
                        {
                            forced.Add(s.Trim());
                        }
                    }
                }
            }
            return forced.Count > 0 && name != null && forced.Contains(name);
        }

        // ---- attribute patch classes

        // Every [HarmonyPatch] class of the assembly, one at a time (instead of Harmony's PatchAll, which stops at the first
        // class that throws and leaves every later class unpatched).
        public static void PatchAll(Harmony h, Assembly assembly)
        {
            harmony = h;
            foreach (Type t in AccessTools.GetTypesFromAssembly(assembly))
            {
                if (t.HasHarmonyAttribute())
                {
                    CombatPartOfAttribute cp = t.GetCustomAttribute<CombatPartOfAttribute>();
                    if (cp != null && (cp.part & ~global::ProjectZ.Combat.CombatGate.ForeignDefault) != 0)
                    {
                        combatClasses.Add(t);
                    }
                    Class(t);
                }
            }
            CloseGroups();
        }

        private static void Class(Type t)
        {
            string name = t.Name;
            CombatPartOfAttribute cp = t.GetCustomAttribute<CombatPartOfAttribute>();
            if (cp != null && !global::ProjectZ.Combat.CombatGate.On(cp.part))
            {
                return;   // its combat part is off (or not settled yet: ApplyCombat)
            }
            PatchGroupAttribute g = t.GetCustomAttribute<PatchGroupAttribute>();
            string error = null;
            List<MethodInfo> done = null;
            current = name;
            try
            {
                done = harmony.CreateClassProcessor(t).Patch();
                if (done != null && done.Count > 0 && Forced(name) && !pending.Any(p => p.name == name))
                {
                    throw new Exception("failed on purpose (-projectzfailpatch)");
                }
            }
            catch (Exception e)
            {
                error = Reason(e);
            }
            current = null;
            error = TakeOwn(name) ?? error;
            if (error != null)
            {
                Remove(t);
                Fail(name, error, g?.group, g != null && g.key);
            }
            else if (done != null && done.Count > 0)
            {
                applied[name] = new Unit { name = name, group = g?.group, key = g != null && g.key, part = cp?.part ?? CombatPart.None, undo = () => Remove(t) };
            }
            Settle();
        }

        // ---- list entries and manual patches

        // A list's entries: their failures in one warning line.
        public static void BeginList(string list)
        {
            batch = new List<string>();
            batchName = list;
        }

        public static void EndList()
        {
            if (batch != null && batch.Count > 0)
            {
                Warn($"{batchName}: {batch.Count} entr{(batch.Count == 1 ? "y" : "ies")} not applied, their part is off: " + string.Join("; ", batch));
            }
            batch = null;
            batchName = null;
        }

        // One entry: the same patch methods on its targets (all or none), then its check (null = fine, else what is
        // missing). A null or missing target fails the entry.
        public static bool Entry(string name, IEnumerable<MethodBase> targets, HarmonyMethod prefix = null, HarmonyMethod postfix = null,
            HarmonyMethod transpiler = null, HarmonyMethod finalizer = null, Func<string> check = null, string group = null, CombatPart part = CombatPart.None)
        {
            if (part != CombatPart.None && !global::ProjectZ.Combat.CombatGate.On(part))
            {
                return false;   // its combat part is off
            }
            List<MethodBase> list = targets == null ? new List<MethodBase>() : targets.ToList();
            if (Forced(name))
            {
                list = new List<MethodBase> { null };   // its target pointed at a method that does not exist
            }
            HashSet<MethodInfo> mine = new HashSet<MethodInfo>(new[] { prefix, postfix, transpiler, finalizer }.Where(m => m?.method != null).Select(m => m.method));
            void Undo()
            {
                foreach (MethodBase m in list.Where(x => x != null))
                {
                    Remove(m, pm => mine.Contains(pm));
                }
            }
            string error = null;
            current = name;
            try
            {
                if (list.Count == 0 || list.Contains(null))
                {
                    throw new MissingMethodException("target method not found");
                }
                foreach (MethodBase m in list)
                {
                    harmony.Patch(m, prefix, postfix, transpiler, finalizer);
                }
                error = check?.Invoke();
            }
            catch (Exception e)
            {
                error = Reason(e);
            }
            current = null;
            error = TakeOwn(name) ?? error;
            if (error != null)
            {
                Undo();
                Fail(name, error, group, false);
                Settle();
                return false;
            }
            applied[name] = new Unit { name = name, group = group, part = part, undo = Undo, check = check };
            Settle();
            return true;
        }

        public static bool Entry(string name, MethodBase target, HarmonyMethod prefix = null, HarmonyMethod postfix = null,
            HarmonyMethod transpiler = null, HarmonyMethod finalizer = null, Func<string> check = null, string group = null, CombatPart part = CombatPart.None)
        {
            return Entry(name, new[] { target }, prefix, postfix, transpiler, finalizer, check, group, part);
        }

        // ---- combat parts (CombatGate)

        // The listed combat classes whose part is on now and that are not applied yet (none failed), then the combat setup
        // steps (CombatGate.OnSettle: AimFloor, IsoRim's shot drawing). From CombatGate.Settle and a late KeepCombatParts.
        public static void ApplyCombat()
        {
            if (harmony == null)
            {
                return;
            }
            foreach (Type t in combatClasses)
            {
                if (!applied.ContainsKey(t.Name) && !Failed.Contains(t.Name) && !Undone.Contains(t.Name))
                {
                    Class(t);
                }
            }
            CloseGroups();
            global::ProjectZ.Combat.CombatGate.RunSettleSteps();
        }

        // Undoes every applied patch of the given combat parts (a part released); returns how many.
        public static int UndoParts(CombatPart parts)
        {
            List<Unit> mine = applied.Values.Where(u => (u.part & parts) != 0).ToList();
            foreach (Unit u in mine)
            {
                applied.Remove(u.name);
                Try(u.undo);
                Released.Add(u.name);
            }
            return mine.Count;
        }

        // ---- transpilers

        // Every transpiler's body: 'edit' changes the list and returns null when it found every anchor it needs, else what
        // is missing. Missing (or the edit threw): the IL as it came, and the patch 'name' counts as failed.
        public static List<CodeInstruction> Transpile(string name, MethodBase original, IEnumerable<CodeInstruction> instructions, Func<List<CodeInstruction>, string> edit)
        {
            List<CodeInstruction> code = instructions.ToList();
            List<CodeInstruction> before = code.Select(c => new CodeInstruction(c)).ToList();
            string miss;
            try
            {
                miss = edit(code);
            }
            catch (Exception e)
            {
                miss = "transpiler error " + Reason(e);
            }
            if (miss == null && Forced(name))
            {
                miss = "anchor reported missing on purpose (-projectzfailpatch)";
            }
            if (miss == null)
            {
                return code;
            }
            Miss(name, (original == null ? "" : original.DeclaringType?.Name + "." + original.Name + ": ") + miss);
            return before;
        }

        private static void Miss(string name, string reason)
        {
            if (name == null || pending.Any(p => p.name == name))
            {
                return;
            }
            pending.Add((name, reason));
            if (finished && warned.Add(name))
            {
                // after loading: nothing is undone any more, the method keeps the game's IL
                Warn($"patch {name} lost its anchor after another mod patched the same method, left as the game's there: {reason}");
            }
        }

        // ---- the end of loading

        // Re-checks every entry (another mod may have patched one of its methods after ProjectZ), undoes the patches whose
        // transpiler lost its anchor meanwhile, and writes the summary line.
        public static void Finish()
        {
            if (finished || harmony == null)
            {
                return;
            }
            try
            {
                foreach (Unit u in applied.Values.Where(u => u.check != null).ToList())
                {
                    string r = u.check();
                    if (r != null)
                    {
                        Miss(u.name, r);
                    }
                }
                Settle();
                CloseGroups();
            }
            catch (Exception e)
            {
                Warn("fail-soft end check: " + Reason(e));
            }
            finished = true;
            pending.Clear();
            string skipped = Failed.Count + Undone.Count == 0 ? "" : ": " + string.Join(", ", Failed)
                + (Undone.Count > 0 ? $"{(Failed.Count > 0 ? ", " : "")}{Undone.Count} undone with their group" : "");
            Log.Message($"[ProjectZ] {Applied} patches applied, {Failed.Count + Undone.Count} skipped{skipped}");
        }

        // ---- internals

        // Misses reported while other patches were applied (Harmony runs every transpiler of a method again whenever a
        // patch lands on it): the patch they belong to is undone now.
        private static void Settle()
        {
            if (pending.Count == 0 || current != null)
            {
                return;
            }
            List<(string name, string reason)> now = pending.ToList();
            pending.Clear();
            foreach ((string name, string reason) in now)
            {
                if (applied.TryGetValue(name, out Unit u))
                {
                    applied.Remove(name);
                    Try(u.undo);
                    Fail(name, reason, u.group, u.key);
                }
            }
        }

        private static string TakeOwn(string name)
        {
            int i = pending.FindIndex(p => p.name == name);
            if (i < 0)
            {
                return null;
            }
            string r = pending[i].reason;
            pending.RemoveAt(i);
            return r;
        }

        private static void Fail(string name, string reason, string group, bool key)
        {
            Failed.Add(name);
            if (key && group != null && !failedKeys.ContainsKey(group))
            {
                failedKeys[group] = name;
            }
            if (batch != null)
            {
                batch.Add(name + " (" + reason + ")");
                return;
            }
            Warn($"patch {name} not applied, its feature is off: {reason}");
        }

        private static void CloseGroups()
        {
            foreach (KeyValuePair<string, string> kv in failedKeys.ToList())
            {
                if (!closed.Add(kv.Key))
                {
                    continue;
                }
                if (kv.Key == Combat)
                {
                    global::ProjectZ.Combat.CombatGate.Disable();
                }
                List<Unit> members = applied.Values.Where(u => u.group == kv.Key).ToList();
                foreach (Unit u in members)
                {
                    applied.Remove(u.name);
                    Try(u.undo);
                    Undone.Add(u.name);
                }
                Warn($"{kv.Key} off because {kv.Value} failed: {members.Count} related patches undone");
            }
        }

        // The patches of class t on every method they landed on.
        private static void Remove(Type t)
        {
            foreach (MethodBase m in Harmony.GetAllPatchedMethods().ToList())
            {
                Remove(m, pm => pm.DeclaringType == t);
            }
        }

        // Our patches on 'original' whose method passes 'mine'.
        private static void Remove(MethodBase original, Func<MethodInfo, bool> mine)
        {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
            if (info == null)
            {
                return;
            }
            foreach (Patch p in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).ToList())
            {
                if (p.owner == harmony.Id && p.PatchMethod != null && mine(p.PatchMethod))
                {
                    Try(() => harmony.Unpatch(original, p.PatchMethod));
                }
            }
        }

        private static void Try(Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Log.Warning("[ProjectZ] undoing a failed patch: " + Reason(e));
            }
        }

        private static void Warn(string text)
        {
            Warnings++;
            Log.Warning("[ProjectZ] " + text);
        }

        private static string Reason(Exception e)
        {
            string where = e is HarmonyException && e.InnerException != null && !e.Message.EndsWith(" null") ? e.Message + ": " : "";
            Exception root = e;
            while (root.InnerException != null)
            {
                root = root.InnerException;
            }
            string msg = root.Message ?? "";
            int nl = msg.IndexOf('\n');
            return where + root.GetType().Name + " " + (nl >= 0 ? msg.Substring(0, nl).TrimEnd() : msg);
        }
    }

    // A patch class in a feature group (FailSoft): key = its failure turns the whole group off.
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class PatchGroupAttribute : Attribute
    {
        public readonly string group;
        public readonly bool key;

        public PatchGroupAttribute(string group, bool key = false)
        {
            this.group = group;
            this.key = key;
        }
    }

    // The fail-soft summary once every mod has loaded (after all static constructors: patches other mods add there are in).
    [StaticConstructorOnStartup]
    public static class FailSoftFinish
    {
        static FailSoftFinish()
        {
            LongEventHandler.ExecuteWhenFinished(FailSoft.Finish);
        }
    }
}
