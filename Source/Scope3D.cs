using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ProjectZ.Combat;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // The 3D scope (Api 10, Api.Scope3D / Api.Scope3DMethod; single-source plan B). Another mod's own method (or the game's)
    // runs unchanged, and the few vanilla questions it asks are answered in 3D between floors while a scope is open, so a
    // rule is called, not copied for the cross-floor case. Two parts:
    //   - registration (Scope3DMethod): a transpiler swaps the method's calls of the primitives below (and those of its
    //     compiler lambdas, local functions and iterators) for the versions here. Only registered methods change; nothing
    //     else in the game is patched. A swapped call with no scope open is the game's own call after one static int test
    //     (depth == 0), so outside a scope behaviour and cost are unchanged;
    //   - the scope (Api.Scope3D): a source and a target cell on a map, pushed on a stack (scopes nest; the innermost
    //     answers) while the caller's own code runs, popped by the handle's Dispose. Main thread only: opened off the main
    //     thread it does nothing, and a swapped call on another thread always gets the game's answer.
    // The answers reuse ProjectZ's one 3D model (no second one): Los3D (sight), Range3D (distance), Api.OpenSkyOver
    // (roofs and floors over a cell), the band rows (floors). Inside a scope:
    //   - a pair of cells on two floors (Heights.CrossFloor): GenSight.LineOfSight (all three overloads) and
    //     LineOfSightToEdges -> Los3D (the game's walk over the ground with a height; ends exempt, the validator asked per
    //     column; the CellRect overload's rects are not used); IntVec3Utility.DistanceTo / DistanceToSquared and
    //     IntVec3.InHorDistOf -> the true 3D distance (Range3D.Distance = Api.Distance3D; squared rounded to an int: the
    //     measure of rules and ranges; Dist3D's walking measure is for "nearest" choices, and its methods are refused). A
    //     pair on one floor, or with a void row cell: the game's own answer;
    //   - a scope whose source and target are on two floors: GridsUtility.Roofed and RoofGrid.Roofed(IntVec3) -> covered
    //     in 3D (not Api.OpenSkyOver: a roof, or a floor built anywhere above; a stairwell's opening is open). In a scope
    //     on one floor: the game's own;
    //   - any scope: searches round a centre stay on the centre's floor (GenRadial.RadialCellsAround, both overloads:
    //     cells on another floor's rows or a void row are left out; CellFinder.TryFindRandomCellNear: its validator also
    //     asks for the root's floor). A centre in a void row: the game's own.
    //   - not swapped, already right per cell: Standable, Walkable, InBounds (every cell is a real cell on its floor;
    //     open air and void rows are impassable).
    public static class Scope3DCore
    {
        private struct Frame
        {
            public Map map;          // null: an inert frame (no floors, no map): the game's answers, also shadowing outer frames
            public IntVec3 source, target;
            public bool cross;       // source and target on two floors
            public bool sightClear;  // (internal) cross-floor sight is clear: a lobbed shot's own check
            public int serial;       // which opening (a stale handle closes nothing)
        }

        // > 0 while a scope is open on the main thread (the only state a swapped call reads first)
        internal static int depth;
        private static Frame[] stack = new Frame[8];
        private static int serials;

        // tests: scopes opened, swapped calls answered in 3D
        public static int Opened, Answered3D;

        public static bool Active => depth > 0 && UnityData.IsInMainThread && stack[depth - 1].map != null;

        // ---- the scope

        public static Scope3DHandle Open(Map map, IntVec3 source, IntVec3 target, bool sightClear = false)
        {
            if (!UnityData.IsInMainThread)
            {
                return default;
            }
            if (depth == stack.Length)
            {
                Array.Resize(ref stack, stack.Length * 2);
            }
            bool on = map != null && ProjectZ.HasAnnex(map);
            stack[depth] = new Frame
            {
                map = on ? map : null,
                source = source,
                target = target,
                cross = on && Heights.CrossFloor(source, target, map),
                sightClear = sightClear,
                serial = ++serials,
            };
            depth++;
            Opened++;
            return new Scope3DHandle(depth, serials);
        }

        // Closes the scope opened at 'level' and any left open inside it.
        internal static void Close(int level, int serial)
        {
            if (level <= 0 || !UnityData.IsInMainThread || depth < level || stack[level - 1].serial != serial)
            {
                return;
            }
            while (depth >= level)
            {
                depth--;
                stack[depth] = default;
            }
        }

        internal static bool IsOpen(int level, int serial) => level > 0 && depth >= level && stack[level - 1].serial == serial;

        // The innermost frame, when a scope is open on this thread for this map (null map: the frame's own map).
        private static bool Live(Map map, out Frame f)
        {
            if (depth == 0 || !UnityData.IsInMainThread)
            {
                f = default;
                return false;
            }
            f = stack[depth - 1];
            return f.map != null && (map == null || ReferenceEquals(f.map, map));
        }

        // ---- the swapped calls (same arguments as the game's; the game's call when no scope answers)

        public static bool LineOfSight(IntVec3 start, IntVec3 end, Map map, bool skipFirstCell, Func<IntVec3, bool> validator, int halfXOffset, int halfZOffset)
        {
            if (depth == 0 || !Live(map, out Frame f) || !Heights.CrossFloor(start, end, map))
            {
                return GenSight.LineOfSight(start, end, map, skipFirstCell, validator, halfXOffset, halfZOffset);
            }
            Answered3D++;
            return f.sightClear || Los3D.CanSeeTowards(start, end, map, halfXOffset, halfZOffset, validator);
        }

        public static bool LineOfSight3(IntVec3 start, IntVec3 end, Map map)
        {
            if (depth == 0 || !Live(map, out Frame f) || !Heights.CrossFloor(start, end, map))
            {
                return GenSight.LineOfSight(start, end, map);
            }
            Answered3D++;
            return f.sightClear || Los3D.CanSee(start, end, map);
        }

        public static bool LineOfSightRects(IntVec3 start, IntVec3 end, Map map, CellRect startRect, CellRect endRect, Func<IntVec3, bool> validator, bool forLeaning)
        {
            if (depth == 0 || !Live(map, out Frame f) || !Heights.CrossFloor(start, end, map))
            {
                return GenSight.LineOfSight(start, end, map, startRect, endRect, validator, forLeaning);
            }
            Answered3D++;
            return f.sightClear || Los3D.CanSee(start, end, map, false, validator);
        }

        public static bool LineOfSightToEdges(IntVec3 start, IntVec3 end, Map map, bool skipFirstCell, Func<IntVec3, bool> validator)
        {
            if (depth == 0 || !Live(map, out Frame f) || !Heights.CrossFloor(start, end, map))
            {
                return GenSight.LineOfSightToEdges(start, end, map, skipFirstCell, validator);
            }
            Answered3D++;
            return f.sightClear || Los3D.CanSee(start, end, map, true, validator);
        }

        public static bool Roofed(IntVec3 c, Map map)
        {
            if (depth == 0 || !Live(map, out Frame f) || !f.cross)
            {
                return c.Roofed(map);
            }
            Answered3D++;
            return !Api.OpenSkyOver(c, map);
        }

        private static readonly AccessTools.FieldRef<RoofGrid, Map> roofMap = AccessTools.FieldRefAccess<RoofGrid, Map>("map");

        public static bool RoofGridRoofed(RoofGrid grid, IntVec3 c)
        {
            if (depth == 0 || grid == null || !Live(roofMap(grid), out Frame f) || !f.cross)
            {
                return grid.Roofed(c);
            }
            Answered3D++;
            return !Api.OpenSkyOver(c, f.map);
        }

        public static float DistanceTo(IntVec3 a, IntVec3 b)
        {
            if (depth == 0 || !Live(null, out Frame f) || !Heights.CrossFloor(a, b, f.map))
            {
                return a.DistanceTo(b);
            }
            Answered3D++;
            return Range3D.Distance(a, b, f.map);
        }

        public static int DistanceToSquared(IntVec3 a, IntVec3 b)
        {
            if (depth == 0 || !Live(null, out Frame f) || !Heights.CrossFloor(a, b, f.map))
            {
                return a.DistanceToSquared(b);
            }
            Answered3D++;
            float d = Range3D.Distance(a, b, f.map);
            return Mathf.RoundToInt(d * d);
        }

        // (the instance call's receiver comes by reference)
        public static bool InHorDistOf(ref IntVec3 a, IntVec3 b, float maxDist)
        {
            if (depth == 0 || !Live(null, out Frame f) || !Heights.CrossFloor(a, b, f.map))
            {
                return a.InHorDistOf(b, maxDist);
            }
            Answered3D++;
            return Range3D.Distance(a, b, f.map) <= maxDist;
        }

        public static IEnumerable<IntVec3> RadialCellsAround(IntVec3 center, float radius, bool useCenter)
        {
            IEnumerable<IntVec3> cells = GenRadial.RadialCellsAround(center, radius, useCenter);
            return depth == 0 || !Live(null, out Frame f) || ProjectZ.FloorOf(center, f.map) < 0 ? cells : OnFloorOf(cells, center, f.map);
        }

        public static IEnumerable<IntVec3> RadialCellsAroundMinMax(IntVec3 center, float minRadius, float maxRadius)
        {
            IEnumerable<IntVec3> cells = GenRadial.RadialCellsAround(center, minRadius, maxRadius);
            return depth == 0 || !Live(null, out Frame f) || ProjectZ.FloorOf(center, f.map) < 0 ? cells : OnFloorOf(cells, center, f.map);
        }

        private static IEnumerable<IntVec3> OnFloorOf(IEnumerable<IntVec3> cells, IntVec3 center, Map map)
        {
            Answered3D++;
            foreach (IntVec3 c in cells)
            {
                if (ProjectZ.SameBand(c, center, map))
                {
                    yield return c;
                }
            }
        }

        public static bool TryFindRandomCellNear(IntVec3 root, Map map, int squareRadius, Predicate<IntVec3> validator, out IntVec3 result, int maxTries)
        {
            if (depth == 0 || !Live(map, out _) || ProjectZ.FloorOf(root, map) < 0)
            {
                return CellFinder.TryFindRandomCellNear(root, map, squareRadius, validator, out result, maxTries);
            }
            Answered3D++;
            Predicate<IntVec3> inner = validator;
            return CellFinder.TryFindRandomCellNear(root, map, squareRadius, c => ProjectZ.SameBand(c, root, map) && (inner == null || inner(c)), out result, maxTries);
        }

        // ---- registration

        private static Dictionary<MethodBase, MethodInfo> swaps;

        internal static Dictionary<MethodBase, MethodInfo> Swaps => swaps ??= BuildSwaps();

        private static Dictionary<MethodBase, MethodInfo> BuildSwaps()
        {
            Type t = typeof(Scope3DCore);
            Dictionary<MethodBase, MethodInfo> d = new Dictionary<MethodBase, MethodInfo>();
            void Add(MethodBase game, string mine)
            {
                MethodInfo m = AccessTools.Method(t, mine);
                if (game == null || m == null)
                {
                    Log.Warning("[ProjectZ] 3D scope: a swapped call is missing (" + mine + "): it stays the game's own in registered methods.");
                    return;
                }
                d[game] = m;
            }
            Add(AccessTools.Method(typeof(GenSight), nameof(GenSight.LineOfSight), new[] { typeof(IntVec3), typeof(IntVec3), typeof(Map), typeof(bool), typeof(Func<IntVec3, bool>), typeof(int), typeof(int) }), nameof(LineOfSight));
            Add(AccessTools.Method(typeof(GenSight), nameof(GenSight.LineOfSight), new[] { typeof(IntVec3), typeof(IntVec3), typeof(Map) }), nameof(LineOfSight3));
            Add(AccessTools.Method(typeof(GenSight), nameof(GenSight.LineOfSight), new[] { typeof(IntVec3), typeof(IntVec3), typeof(Map), typeof(CellRect), typeof(CellRect), typeof(Func<IntVec3, bool>), typeof(bool) }), nameof(LineOfSightRects));
            Add(AccessTools.Method(typeof(GenSight), nameof(GenSight.LineOfSightToEdges), new[] { typeof(IntVec3), typeof(IntVec3), typeof(Map), typeof(bool), typeof(Func<IntVec3, bool>) }), nameof(LineOfSightToEdges));
            Add(AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.Roofed), new[] { typeof(IntVec3), typeof(Map) }), nameof(Roofed));
            Add(AccessTools.Method(typeof(RoofGrid), nameof(RoofGrid.Roofed), new[] { typeof(IntVec3) }), nameof(RoofGridRoofed));
            Add(AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceTo), new[] { typeof(IntVec3), typeof(IntVec3) }), nameof(DistanceTo));
            Add(AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceToSquared), new[] { typeof(IntVec3), typeof(IntVec3) }), nameof(DistanceToSquared));
            Add(AccessTools.Method(typeof(IntVec3), nameof(IntVec3.InHorDistOf), new[] { typeof(IntVec3), typeof(float) }), nameof(InHorDistOf));
            Add(AccessTools.Method(typeof(GenRadial), nameof(GenRadial.RadialCellsAround), new[] { typeof(IntVec3), typeof(float), typeof(bool) }), nameof(RadialCellsAround));
            Add(AccessTools.Method(typeof(GenRadial), nameof(GenRadial.RadialCellsAround), new[] { typeof(IntVec3), typeof(float), typeof(float) }), nameof(RadialCellsAroundMinMax));
            Add(AccessTools.Method(typeof(CellFinder), nameof(CellFinder.TryFindRandomCellNear)), nameof(TryFindRandomCellNear));
            return d;
        }

        private static Harmony harmony;
        private static readonly Dictionary<MethodBase, int> registered = new Dictionary<MethodBase, int>();
        private static int lastSwapped;

        // Registered methods and their swapped calls (tests).
        public static IReadOnlyDictionary<MethodBase, int> Registered => registered;

        // Swaps the primitives' calls in 'method' and in its compiler lambdas, local functions and iterators. The number of
        // calls swapped (0: none found, no body, or the patch failed: logged once). A method registered before: its count.
        public static int Register(MethodBase method)
        {
            if (method == null)
            {
                return 0;
            }
            if (registered.TryGetValue(method, out int known))
            {
                return known;
            }
            // one meaning per method: a method whose distances Dist3D already measures (the walking measure, for "nearest"
            // choices) is not given the scope's geometric one
            MethodBase taken = WithInner(method).FirstOrDefault(m => Dist3DMethods.Contains(m));
            if (taken != null)
            {
                Log.Warning("[ProjectZ] 3D scope: " + method.DeclaringType?.FullName + "." + method.Name + " is not registered: ProjectZ already measures its distances between floors (" + taken.DeclaringType?.Name + "." + taken.Name + ", the walking distance for nearest choices).");
                registered[method] = 0;
                return 0;
            }
            int n = 0;
            try
            {
                harmony ??= new Harmony("Peren.IsoRimProjectZ.scope3d");
                foreach (MethodBase m in WithInner(method))
                {
                    if (!CallsAny(m))
                    {
                        continue;
                    }
                    lastSwapped = 0;
                    harmony.Patch(m, transpiler: new HarmonyMethod(typeof(Scope3DCore), nameof(Transpiler)));
                    n += lastSwapped;
                }
            }
            catch (Exception e)
            {
                Log.Warning("[ProjectZ] 3D scope: " + method.DeclaringType?.FullName + "." + method.Name + " could not be registered (" + e.GetType().Name + ": " + e.Message + "); it answers as on one floor.");
            }
            if (n == 0)
            {
                Log.Warning("[ProjectZ] 3D scope: " + method.DeclaringType?.FullName + "." + method.Name + " has no call the scope answers (or no body): nothing registered.");
            }
            registered[method] = n;
            return n;
        }

        private static HashSet<MethodBase> dist3DMethods;

        // The methods of Dist3D's rows (resolved as Dist3D does, also when its patch is off).
        internal static HashSet<MethodBase> Dist3DMethods => dist3DMethods ??= new HashSet<MethodBase>(Patches.Dist3D.Rows.SelectMany(r => Patches.Dist3D.Resolve(r)));

        // The method and the compiler's code it owns: lambdas and local functions ("<Name>b__", "<Name>g__") in its type and
        // nested types, and its iterator or async state machine's MoveNext ("<Name>d__"). The names carry the method's name
        // only, so with overloads of that name the code is also checked to be reached from this method's own IL (Owned).
        private static IEnumerable<MethodBase> WithInner(MethodBase method)
        {
            yield return method;
            Type t = method.DeclaringType;
            if (t == null)
            {
                yield break;
            }
            HashSet<MethodBase> owned = t.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly).Count(x => x.Name == method.Name) > 1 ? Owned(method) : null;
            string tag = "<" + method.Name + ">";
            foreach (Type x in new[] { t }.Concat(AllNested(t)))
            {
                bool machine = x != t && x.Name.StartsWith(tag + "d__");
                foreach (MethodInfo m in x.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null)
                    {
                        continue;
                    }
                    if (machine ? m.Name == "MoveNext" : m.Name.Contains(tag))
                    {
                        if (owned == null || owned.Contains(m))
                        {
                            yield return m;
                        }
                    }
                }
            }
        }

        // The compiler's code reached from method's own IL, transitively: lambdas and local functions it names (ldftn,
        // call), and the MoveNext of the state machine it creates (newobj).
        private static HashSet<MethodBase> Owned(MethodBase method)
        {
            HashSet<MethodBase> seen = new HashSet<MethodBase>();
            Queue<MethodBase> todo = new Queue<MethodBase>();
            todo.Enqueue(method);
            while (todo.Count > 0)
            {
                MethodBase m = todo.Dequeue();
                List<KeyValuePair<OpCode, object>> body;
                try
                {
                    body = PatchProcessor.ReadMethodBody(m).ToList();
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (KeyValuePair<OpCode, object> p in body)
                {
                    if (!(p.Value is MethodBase target) || target.DeclaringType == null)
                    {
                        continue;
                    }
                    MethodBase found = null;
                    if (target is ConstructorInfo && target.DeclaringType.Name.StartsWith("<") && target.DeclaringType.Name.Contains(">d__"))
                    {
                        found = AccessTools.Method(target.DeclaringType, "MoveNext");
                    }
                    else if (target is MethodInfo && target.Name.StartsWith("<"))
                    {
                        found = target;
                    }
                    if (found != null && seen.Add(found))
                    {
                        todo.Enqueue(found);
                    }
                }
            }
            return seen;
        }

        private static IEnumerable<Type> AllNested(Type t)
        {
            foreach (Type n in t.GetNestedTypes(AccessTools.all))
            {
                yield return n;
                foreach (Type nn in AllNested(n))
                {
                    yield return nn;
                }
            }
        }

        private static bool CallsAny(MethodBase m)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(m).Any(p => (p.Key == OpCodes.Call || p.Key == OpCodes.Callvirt) && p.Value is MethodBase mb && Swaps.ContainsKey(mb));
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = instructions.ToList();
            lastSwapped = CallSwap.SwapAll(code, Swaps);
            return code;
        }

        // ---- ProjectZ's own use: the game's original body of a method it patches, with the swaps (a reverse patch: none of
        // the patches on it, ProjectZ's own included). The stand-in's body is replaced; null when that failed or nothing was
        // swapped (logged).
        internal static MethodInfo OriginalWithScope(MethodBase original, MethodInfo standin)
        {
            return ReverseCopy(original, standin, null, true, HarmonyReversePatchType.Original);
        }

        private static IReadOnlyDictionary<MethodBase, MethodInfo> copyExtra;
        private static bool copyScope;

        // A private copy of the game's (or another mod's) method as the stand-in's body (a reverse patch), so a rule is asked
        // as it is without patching it live: the calls in 'extra' swapped (CallSwap), and with 'scope' the 3D scope's too.
        // Snapshot: the method with the transpilers on it when the copy is made (other mods' changes to its body come along;
        // their prefixes and postfixes don't), so make it at first use, after every mod's startup patches; Original: its
        // compiled body only. Null when that failed or nothing was swapped (logged).
        internal static MethodInfo ReverseCopy(MethodBase original, MethodInfo standin, IReadOnlyDictionary<MethodBase, MethodInfo> extra, bool scope, HarmonyReversePatchType type)
        {
            string name = original == null ? "(missing)" : original.DeclaringType?.Name + "." + original.Name;
            if (original == null || standin == null)
            {
                Log.Warning("[ProjectZ] no copy of the game's " + name + " (method not found).");
                return null;
            }
            if (type == HarmonyReversePatchType.Snapshot && Harmony.GetPatchInfo(original) == null)
            {
                type = HarmonyReversePatchType.Original;   // (nothing patched on it: the snapshot is its body; Harmony's snapshot needs patches)
            }
            copyExtra = extra;
            copyScope = scope;
            try
            {
                lastSwapped = 0;
                MethodInfo m = Harmony.ReversePatch(original, new HarmonyMethod(standin) { reversePatchType = type },
                    AccessTools.Method(typeof(Scope3DCore), nameof(CopyTranspiler)));
                if (lastSwapped == 0)
                {
                    Log.Warning("[ProjectZ] the game's " + name + " has no call to swap: no copy made.");
                    return null;
                }
                return m;
            }
            catch (Exception e)
            {
                Log.Warning("[ProjectZ] no copy of the game's " + name + " (" + e.GetType().Name + ": " + e.Message + ").");
                return null;
            }
            finally
            {
                copyExtra = null;
                copyScope = false;
            }
        }

        public static IEnumerable<CodeInstruction> CopyTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = instructions.ToList();
            int n = copyScope ? CallSwap.SwapAll(code, Swaps) : 0;
            if (copyExtra != null)
            {
                n += CallSwap.SwapAll(code, copyExtra);
            }
            lastSwapped = n;
            return code;
        }
    }

    // THE call-swap helper: every ProjectZ transpiler that replaces the calls of one method by another goes through here
    // (the 3D scope's table, the private copies of the game's rules (Scope3DCore.ReverseCopy), the power risers and
    // hookups, the hoses).
    public static class CallSwap
    {
        // Replace calls to 'from' by calls to 'to'; 'extraArgs' are pushed right before the call. Null when exactly
        // 'expected' were swapped; else what is missing (FailSoft.Transpile then hands the IL back as it came).
        internal static string Swap(List<CodeInstruction> list, MethodInfo from, MethodInfo to, int expected, params CodeInstruction[] extraArgs)
        {
            int done = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if ((list[i].opcode == OpCodes.Call || list[i].opcode == OpCodes.Callvirt) && list[i].operand as MethodInfo == from)
                {
                    list[i].opcode = OpCodes.Call;
                    list[i].operand = to;
                    for (int j = 0; j < extraArgs.Length; j++)
                    {
                        list.Insert(i, extraArgs[extraArgs.Length - 1 - j].Clone());
                    }
                    i += extraArgs.Length;
                    done++;
                }
            }
            return done == expected ? null : $"{done} of {expected} {from?.Name} calls found";
        }

        // Every call of a key of 'table' becomes a call of its value; the number swapped.
        internal static int SwapAll(List<CodeInstruction> list, IReadOnlyDictionary<MethodBase, MethodInfo> table)
        {
            int n = 0;
            foreach (CodeInstruction ci in list)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodBase mb && table.TryGetValue(mb, out MethodInfo mine))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = mine;
                    n++;
                }
            }
            return n;
        }
    }

    // What Api.Scope3D returns: Dispose closes the scope (and any opened inside it and left open). A struct: opening a
    // scope allocates nothing. Disposing it twice, or off the main thread, does nothing.
    public readonly struct Scope3DHandle : IDisposable
    {
        private readonly int level, serial;

        internal Scope3DHandle(int level, int serial)
        {
            this.level = level;
            this.serial = serial;
        }

        // The scope is open (false for a scope opened off the main thread).
        public bool Open => Scope3DCore.IsOpen(level, serial);

        public void Dispose() => Scope3DCore.Close(level, serial);
    }
}
