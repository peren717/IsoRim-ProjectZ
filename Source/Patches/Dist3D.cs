using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Patches
{
    // 3D distance for the game's "nearest" choices (isorim-pz-pathing-redesign.md, "AI 选择"; user 2026-10-20: "otherwise
    // it isn't real multi-level"). Floors are bands of rows on one map, so the game's flat distances put a thing on the
    // floor right above "a band of rows away" and a far ground thing "near". Rule, for two cells on different levels:
    //   distance = the game's own ground distance between their ground cells (Euclidean or Manhattan, as the call site
    //              measures) + |level difference| x StoreyCells,
    // StoreyCells = one storey's climb in walking cells: the ramp (3 cells) + the stair step's time (Climb.StepTicks) in
    // cells of a baseline walker (60 / 4.6 ticks per cell) = 5.3. On one level (one band) every helper returns the game's
    // own value bit for bit; nothing changes on maps without floors.
    //
    // One mechanism: a transpiler swaps the distance expressions inside the listed vanilla methods (Rows, the inventory)
    // for these helpers, the method's own map passed along (first Thing/Region/Map/IAttackTarget parameter, the path
    // follower's pawn, or the context map an entry prefix sets). Kinds swapped (each row lists the kinds it may swap):
    //   (a - b).LengthHorizontalSquared / .LengthHorizontal / .LengthManhattan, a.DistanceToSquared(b), a.DistanceTo(b),
    //   a.InHorDistOf(b, d), rect.ClosestDistSquaredTo(c) (region extents: the region search's distance limit).
    // Not here (handled elsewhere): radius loops (BandRadial), fleeing (FleePatches), ShouldLoseTarget (VirtualPatches),
    // weapon ranges and ranged target picks between floors (Combat: Range3D, AttackTargetFinder_Best_Patch).
    // Fast path: both rows below the lowest ground height of any map with floors seen (MinGroundZ, int.MaxValue until
    // one is seen): the game's value, one compare.
    // Test flag -projectznodist3d: helpers return the game's values (the control).
    public static class Dist3D
    {
        public static bool Off;

        // The lowest groundZ of the maps with floors seen this session (ProjectZ.Annex); int.MaxValue: none.
        public static int MinGroundZ = int.MaxValue;

        public const float RampCells = 3f;
        public const float BaselineTicksPerCell = 60f / 4.6f;

        // One storey in walking cells.
        public static readonly float StoreyCells = RampCells + Climb.StepTicks / BaselineTicksPerCell;

        // Map for helpers in methods without a map of their own (set by the entry prefix, Dist3D_WorkContext_Patch).
        [ThreadStatic]
        internal static Map ctx;

        // Calls that went the 3D way; all helper calls (tests).
        public static int CrossCalls;
        public static long Calls;

        private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> PatherPawn = AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");

        internal static void Seen(AnnexMapComponent a)
        {
            if (a.groundZ < MinGroundZ)
            {
                MinGroundZ = a.groundZ;
            }
        }

        private static Map MapOf(object o)
        {
            switch (o)
            {
                case Thing t: return t.MapHeld ?? ctx;
                case Region r: return r.Map;
                case Map m: return m;
                case IAttackTarget at: return at.Thing?.MapHeld ?? ctx;
                case Pawn_PathFollower pf: return PatherPawn(pf)?.Map ?? ctx;
                default: return ctx;
            }
        }

        // a and b on different levels of a map with floors: their ground offsets and the level difference.
        private static bool Cross(IntVec3 a, IntVec3 b, object o, out int dx, out int dz, out int dL)
        {
            dx = dz = dL = 0;
            AnnexMapComponent an = Off ? null : ProjectZ.Annex(MapOf(o));
            if (an == null)
            {
                return false;
            }
            int ba = an.BandOfRowLoose(a.z), bb = an.BandOfRowLoose(b.z);
            if (ba == bb)
            {
                return false;
            }
            dx = a.x - b.x;
            dz = (a.z - ba * an.stride) - (b.z - bb * an.stride);
            dL = Math.Abs(an.LevelOfBand(ba) - an.LevelOfBand(bb));
            CrossCalls++;
            return true;
        }

        private static bool Fast(IntVec3 a, IntVec3 b) => a.z < MinGroundZ && b.z < MinGroundZ;

        // 3D distance (Euclidean ground part).
        public static float Distance(IntVec3 a, IntVec3 b, Map map)
        {
            if (Fast(a, b) || !Cross(a, b, map, out int dx, out int dz, out int dL))
            {
                return (a - b).LengthHorizontal;
            }
            return Mathf.Sqrt(dx * dx + dz * dz) + dL * StoreyCells;
        }

        // ---- the replacements (same arguments as the game's, the map source appended)

        // (a - b).LengthHorizontalSquared
        public static int SubLenSq(IntVec3 a, IntVec3 b, object o)
        {
            Calls++;
            if (Fast(a, b) || !Cross(a, b, o, out int dx, out int dz, out int dL))
            {
                return (a - b).LengthHorizontalSquared;
            }
            float d = Mathf.Sqrt(dx * dx + dz * dz) + dL * StoreyCells;
            return Mathf.RoundToInt(d * d);
        }

        // (a - b).LengthHorizontal
        public static float SubLen(IntVec3 a, IntVec3 b, object o)
        {
            Calls++;
            if (Fast(a, b) || !Cross(a, b, o, out int dx, out int dz, out int dL))
            {
                return (a - b).LengthHorizontal;
            }
            return Mathf.Sqrt(dx * dx + dz * dz) + dL * StoreyCells;
        }

        // (a - b).LengthManhattan
        public static int SubManhattan(IntVec3 a, IntVec3 b, object o)
        {
            Calls++;
            if (Fast(a, b) || !Cross(a, b, o, out int dx, out int dz, out int dL))
            {
                return (a - b).LengthManhattan;
            }
            return Math.Abs(dx) + Math.Abs(dz) + Mathf.RoundToInt(dL * StoreyCells);
        }

        // a.DistanceToSquared(b)
        public static int DistSq(IntVec3 a, IntVec3 b, object o) => SubLenSq(a, b, o);

        // a.DistanceTo(b)
        public static float Dist(IntVec3 a, IntVec3 b, object o) => SubLen(a, b, o);

        // a.InHorDistOf(b, maxDist)
        public static bool InHorDistOf(ref IntVec3 a, IntVec3 b, float maxDist, object o)
        {
            Calls++;
            if (Fast(a, b) || !Cross(a, b, o, out int dx, out int dz, out int dL))
            {
                return a.InHorDistOf(b, maxDist);
            }
            return Mathf.Sqrt(dx * dx + dz * dz) + dL * StoreyCells <= maxDist;
        }

        // rect.ClosestDistSquaredTo(c) (a region's extents: all on one band)
        public static float RectDistSq(ref CellRect r, IntVec3 c, object o)
        {
            Calls++;
            IntVec3 rc = new IntVec3(r.minX, 0, r.minZ);
            if (Fast(rc, c) || !Cross(rc, c, o, out _, out _, out int dL))
            {
                return r.ClosestDistSquaredTo(c);
            }
            AnnexMapComponent an = ProjectZ.Annex(MapOf(o));
            int shift = (an.BandOfRowLoose(r.minZ) - an.BandOfRowLoose(c.z)) * an.stride;
            float g = Mathf.Sqrt(r.ClosestDistSquaredTo(new IntVec3(c.x, c.y, c.z + shift))) + dL * StoreyCells;
            return g * g;
        }

        // ---- the inventory

        [Flags]
        public enum Kind
        {
            SubLenSq = 1,
            SubLen = 2,
            SubManhattan = 4,
            DistSq = 8,
            Dist = 16,
            InHor = 32,
            RectDist = 64,
        }

        public sealed class Row
        {
            public readonly string name;
            public readonly Type type;
            public readonly string outer;      // the method (or the method holding the local function / lambdas)
            public readonly string inner;      // local function name; "*" = every lambda of outer; null = outer itself
            public readonly Kind kinds;
            public readonly int expected;      // swaps expected over the row's methods
            public readonly List<MethodBase> targets = new List<MethodBase>();
            public readonly Dictionary<MethodBase, int> swapsOf = new Dictionary<MethodBase, int>();   // the last transpiler run per method (Harmony re-runs it whenever another patch lands on the method)

            public int swaps => swapsOf.Values.Sum();

            public Row(string name, Type type, string outer, string inner, Kind kinds, int expected)
            {
                this.name = name;
                this.type = type;
                this.outer = outer;
                this.inner = inner;
                this.kinds = kinds;
                this.expected = expected;
            }
        }

        // Ranked by gameplay impact (the design note's inventory; ranged targets, radius loops, flee and target keeping are
        // handled elsewhere, see above).
        public static readonly Row[] Rows =
        {
            // GenClosest: food, medicine, beds, weapons, apparel, haulables, every WorkGiver thing scan, melee targets
            new Row("closest regionwise: pick", typeof(RegionProcessorClosestThingReachable), "ProcessThing", null, Kind.SubLenSq, 1),
            new Row("closest regionwise: distance limit", typeof(RegionProcessorClosestThingReachable), "RegionEntryPredicate", null, Kind.RectDist, 1),
            new Row("closest global", typeof(GenClosest), "ClosestThing_Global", "Process", Kind.SubLenSq, 1),
            new Row("closest global reachable", typeof(GenClosest), "ClosestThing_Global_Reachable", "Process", Kind.SubLenSq, 1),
            // work on cells (plants, mining, construction cells, cleaning ...)
            new Row("work cells", typeof(JobGiver_Work), "TryIssueJobPackage", "ProcessCell", Kind.SubLenSq, 1),
            // meals (food optimality counts the distance)
            new Row("food scan", typeof(FoodUtility), "SpawnedFoodSearchInnerScan", null, Kind.SubManhattan, 1),
            // (its pack-animal comparison sits in a closure: left, pack animals only)
            new Row("food: map vs inventory", typeof(FoodUtility), nameof(FoodUtility.TryFindBestFoodSourceFor), null, Kind.SubManhattan, 2),
            // storage
            new Row("store cell", typeof(StoreUtility), "TryFindBestBetterStoreCellForWorker", null, Kind.SubLenSq, 1),
            new Row("store building", typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterNonSlotGroupStorageFor), null, Kind.DistSq, 1),
            // bill ingredients: nearest first
            new Row("ingredients (no mix)", typeof(WorkGiver_DoBill), "TryFindBestIngredientsInSet_NoMixHelper", "*", Kind.SubLenSq, 2),
            new Row("ingredients (mix)", typeof(WorkGiver_DoBill), "TryFindBestBillIngredientsInSet_AllowMix", "*", Kind.SubLenSq, 1),
            // sappers / breachers: the 50-cell target radius and "reached the duty spot"
            new Row("sapper: reached", typeof(JobGiver_AISapper), "TryGiveJob", null, Kind.DistSq, 1),
            new Row("sapper: 50-cell targets", typeof(JobGiver_AISapper), "TryGiveJob", "*", Kind.DistSq, 1),
            new Row("breacher: reached", typeof(JobGiver_AIBreaching), "TryGiveJob", null, Kind.DistSq, 1),
            // melee target: region pick vs flood pick
            new Row("melee target choice", typeof(AttackTargetFinder), nameof(AttackTargetFinder.BestAttackTarget), null, Kind.SubLen, 2),
            // raiders with nothing in sight: walk to the nearest hostile (sappers' and assaulters' last resort)
            new Row("goto nearest hostile", typeof(JobGiver_AIGotoNearestHostile), "TryGiveJob", null, Kind.DistSq, 1),
            // predators: prey score
            new Row("prey score", typeof(FoodUtility), nameof(FoodUtility.GetPreyScoreFor), null, Kind.SubLen, 1),
            // path follower: moving-target tolerance, stale path, rope stretch
            new Row("new path: moving target", typeof(Pawn_PathFollower), "NeedNewPath", null, Kind.InHor | Kind.SubLenSq, 4),
            new Row("rope stretch", typeof(Pawn_PathFollower), "RopeeWithStretchedRopeAtNextPathCell", null, Kind.InHor, 1),
        };

        internal static IEnumerable<MethodBase> Resolve(Row row)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            if (row.inner == null)
            {
                foreach (MethodInfo m in row.type.GetMethods(all).Where(m => m.Name == row.outer && m.GetMethodBody() != null))
                {
                    yield return m;
                }
                yield break;
            }
            string prefix = "<" + row.outer + ">";
            IEnumerable<Type> types = new[] { row.type }.Concat(AllNested(row.type));
            foreach (Type t in types)
            {
                foreach (MethodInfo m in t.GetMethods(all))
                {
                    if (!m.Name.StartsWith(prefix) || m.GetMethodBody() == null)
                    {
                        continue;
                    }
                    if (row.inner == "*" ? m.Name.StartsWith(prefix + "b__") : m.Name.StartsWith(prefix + "g__" + row.inner + "|"))
                    {
                        yield return m;
                    }
                }
            }
        }

        private static IEnumerable<Type> AllNested(Type t)
        {
            foreach (Type n in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                yield return n;
                foreach (Type nn in AllNested(n))
                {
                    yield return nn;
                }
            }
        }
    }

    // The transpiler over the inventory (Dist3D.Rows).
    [HarmonyPatch]
    public static class Dist3D_Patch
    {
        private static readonly Dictionary<MethodBase, Dist3D.Row> rowOf = new Dictionary<MethodBase, Dist3D.Row>();

        private static readonly MethodInfo sub = AccessTools.Method(typeof(IntVec3), "op_Subtraction", new[] { typeof(IntVec3), typeof(IntVec3) });
        private static readonly MethodInfo lenSq = AccessTools.PropertyGetter(typeof(IntVec3), nameof(IntVec3.LengthHorizontalSquared));
        private static readonly MethodInfo len = AccessTools.PropertyGetter(typeof(IntVec3), nameof(IntVec3.LengthHorizontal));
        private static readonly MethodInfo man = AccessTools.PropertyGetter(typeof(IntVec3), nameof(IntVec3.LengthManhattan));
        private static readonly MethodInfo distSq = AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceToSquared), new[] { typeof(IntVec3), typeof(IntVec3) });
        private static readonly MethodInfo dist = AccessTools.Method(typeof(IntVec3Utility), nameof(IntVec3Utility.DistanceTo), new[] { typeof(IntVec3), typeof(IntVec3) });
        private static readonly MethodInfo inHor = AccessTools.Method(typeof(IntVec3), nameof(IntVec3.InHorDistOf), new[] { typeof(IntVec3), typeof(float) });
        private static readonly MethodInfo rectDist = AccessTools.Method(typeof(CellRect), nameof(CellRect.ClosestDistSquaredTo), new[] { typeof(IntVec3) });

        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (Dist3D.Row row in Dist3D.Rows)
            {
                foreach (MethodBase m in Dist3D.Resolve(row))
                {
                    if (!rowOf.ContainsKey(m))
                    {
                        rowOf[m] = row;
                        row.targets.Add(m);
                        yield return m;
                    }
                }
                if (row.targets.Count == 0)
                {
                    Log.Warning($"[ProjectZ] 3D distance: {row.type.Name}.{row.outer} {row.inner} not found");
                }
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            if (!rowOf.TryGetValue(original, out Dist3D.Row row))
            {
                return code;
            }
            CodeInstruction MapLoad() => MapSource(original);
            int n = 0;
            for (int i = 0; i < code.Count; i++)
            {
                CodeInstruction ci = code[i];
                string with = null;
                if (ci.Calls(distSq) && (row.kinds & Dist3D.Kind.DistSq) != 0)
                {
                    with = nameof(Dist3D.DistSq);
                }
                else if (ci.Calls(dist) && (row.kinds & Dist3D.Kind.Dist) != 0)
                {
                    with = nameof(Dist3D.Dist);
                }
                else if (ci.Calls(inHor) && (row.kinds & Dist3D.Kind.InHor) != 0)
                {
                    with = nameof(Dist3D.InHorDistOf);
                }
                else if (ci.Calls(rectDist) && (row.kinds & Dist3D.Kind.RectDist) != 0)
                {
                    with = nameof(Dist3D.RectDistSq);
                }
                else if (ci.Calls(sub) && i + 3 < code.Count && code[i + 1].IsStloc() && code[i + 2].opcode.Name.StartsWith("ldloca"))
                {
                    CodeInstruction get = code[i + 3];
                    string w = get.Calls(lenSq) && (row.kinds & Dist3D.Kind.SubLenSq) != 0 ? nameof(Dist3D.SubLenSq)
                        : get.Calls(len) && (row.kinds & Dist3D.Kind.SubLen) != 0 ? nameof(Dist3D.SubLen)
                        : get.Calls(man) && (row.kinds & Dist3D.Kind.SubManhattan) != 0 ? nameof(Dist3D.SubManhattan) : null;
                    if (w != null && code[i + 1].labels.Count == 0 && code[i + 2].labels.Count == 0 && get.labels.Count == 0)
                    {
                        with = w;
                        for (int k = 1; k <= 3; k++)
                        {
                            code[i + k].opcode = OpCodes.Nop;
                            code[i + k].operand = null;
                        }
                    }
                }
                if (Dev.TestRunner.Active && with == null && ci.Calls(sub) && code.Skip(i + 1).Take(6).Any(c => c.Calls(lenSq) || c.Calls(len) || c.Calls(man)))
                {
                    ProjectZ.DebugLog($"3D distance IL {original.Name} @{i} NOT SWAPPED (another shape): " + string.Join(" | ", code.Skip(Math.Max(0, i - 6)).Take(12).Select(c => c.ToString())));
                }
                if (with == null)
                {
                    continue;
                }
                code.Insert(i, MapLoad().WithLabels(ci.ExtractLabels()));
                i++;
                ci.opcode = OpCodes.Call;
                ci.operand = AccessTools.Method(typeof(Dist3D), with);
                n++;
            }
            row.swapsOf[original] = n;
            ProjectZ.DebugLog($"3D distance: {original.DeclaringType?.Name}.{original.Name}: {n} swapped ({row.name})");
            return code;
        }

        // The object the helpers take their map from: the first Thing/Region/Map/IAttackTarget parameter; 'this' for the
        // path follower; else null (the context map).
        private static CodeInstruction MapSource(MethodBase m)
        {
            ParameterInfo[] ps = m.GetParameters();
            int off = m.IsStatic ? 0 : 1;
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType == typeof(Map))
                {
                    return Ldarg(i + off);
                }
            }
            for (int i = 0; i < ps.Length; i++)
            {
                Type pt = ps[i].ParameterType;
                if (!pt.IsByRef && (typeof(Thing).IsAssignableFrom(pt) || pt == typeof(Region) || pt == typeof(Map) || pt == typeof(IAttackTarget)))
                {
                    return Ldarg(i + off);
                }
            }
            if (!m.IsStatic && typeof(Pawn_PathFollower).IsAssignableFrom(m.DeclaringType))
            {
                return new CodeInstruction(OpCodes.Ldarg_0);
            }
            return new CodeInstruction(OpCodes.Ldnull);
        }

        private static CodeInstruction Ldarg(int i)
        {
            switch (i)
            {
                case 0: return new CodeInstruction(OpCodes.Ldarg_0);
                case 1: return new CodeInstruction(OpCodes.Ldarg_1);
                case 2: return new CodeInstruction(OpCodes.Ldarg_2);
                case 3: return new CodeInstruction(OpCodes.Ldarg_3);
                default: return new CodeInstruction(OpCodes.Ldarg_S, (byte)i);
            }
        }
    }

    // The context map for rows whose methods carry no map (JobGiver_Work's cell scan).
    [HarmonyPatch(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage))]
    public static class Dist3D_WorkContext_Patch
    {
        public static void Prefix(Pawn pawn, out Map __state)
        {
            __state = Dist3D.ctx;
            Dist3D.ctx = pawn?.Map;
        }

        public static void Finalizer(Map __state)
        {
            Dist3D.ctx = __state;
        }
    }
}
