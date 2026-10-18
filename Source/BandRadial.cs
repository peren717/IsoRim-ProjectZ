using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ
{
    // Radius effects stay on their centre's floor (compat G3a, isorim-pz-compat-g3-plan.md). One mechanism for the
    // game's radius loops: in every listed vanilla method (BandRadialList.cs, an offline list made and reviewed with
    // tools\pz-radial-scan) a transpiler swaps only the cell producers for band-aware ones and passes the method's own map:
    //   - GenRadial.RadialCellsAround(c, r, useCenter) and (c, min, max) -> CellsAround: cells off the centre's band skipped;
    //   - RadialPattern[i] + c, ManualRadialPattern[i] + c (and c + p, c + p * k) -> AddCP / AddPC: a cell past the band's
    //     edge becomes the void-row cell next to the band in the same column (the "void wall": the void is inert);
    //   - GenRadial.ProcessEquidistantCells (blight): its own InBounds also asks for the centre's band.
    // The map is read once at the method's entry (a local) from the row's path. The area follows its centre cell's
    // floor (an effect aimed from the ground at floor 2 covers floor 2 only). Explosions keep Explosions3D's cells.
    // This also keeps "near" placements on their floor (drops, leavings: GenPlace.TryFindPlaceSpotNear) and the radius
    // loops BandScope had rows for (G3b retired those rows and the same-floor placement patch; mode radial R10-R17).
    // Without floors nothing changes: MapOf returns null (one static flag test) and the helpers give the game's own value
    // or iterator. Test flag -projectznoradialclip: nothing is patched (the game's cells, to show the old leaks).
    public static class BandRadial
    {
        // a map with floors has been seen in this session (set by ProjectZ.Annex); false: MapOf returns null at once
        public static bool AnyAnnex;
        // -projectznoradialclip (test only): nothing patched
        public static bool Off;
        // tests: per-row counters on maps with floors (calls, cells, folded, skipped)
        public static bool Counting;

        public sealed class Row
        {
            public readonly string group, type, method, args, mapPath;
            public readonly int expected;
            public MethodBase target;
            public int swaps;
            public int calls, cells, folded, skipped;

            public Row(string group, string type, string method, string args, string mapPath, int expected)
            {
                this.group = group;
                this.type = type;
                this.method = method;
                this.args = args;
                this.mapPath = mapPath;
                this.expected = expected;
            }

            public string Name => type.Substring(type.LastIndexOf('.') + 1) + "." + method;
        }

        internal static Row[] rows = BandRadialList.Rows;
        private static readonly Dictionary<MethodBase, int> idOf = new Dictionary<MethodBase, int>();
        public static IReadOnlyList<Row> Rows => rows;

        // startup report (P1)
        public static int Listed, Patched, Unmatched, NotFound, SitesSwapped;
        public static double ResolveMs, PatchMs;
        public static int PecSwapped, PecSkipped;

        public static void ResetCounters()
        {
            foreach (Row r in rows)
            {
                r.calls = r.cells = r.folded = r.skipped = 0;
            }
            PecSkipped = 0;
        }

        public static Row RowOf(Type t, string method) => rows.FirstOrDefault(r => r.target != null && r.target.DeclaringType == t && r.target.Name == method);

        // ---- the map of a method (its row's path, at entry)

        public static Map MapOf(object o, int id)
        {
            if (!AnyAnnex || o == null)
            {
                return null;
            }
            Map m;
            switch (o)
            {
                case Map mm: m = mm; break;
                case Thing t: m = t.MapHeld; break;
                case ThingComp c: m = c.parent?.MapHeld; break;
                case HediffComp hc: m = hc.Pawn?.MapHeld; break;
                case Hediff h: m = h.pawn?.MapHeld; break;
                case Verb v: m = v.caster?.MapHeld; break;
                case Ability a: m = a.pawn?.MapHeld; break;
                case AbilityComp ac: m = ac.parent?.pawn?.MapHeld; break;
                case JobDriver jd: m = jd.pawn?.MapHeld; break;
                case LordToil lt: m = lt.lord?.Map; break;
                case LordJob lj: m = lj.lord?.Map; break;
                case Lord l: m = l.Map; break;
                case MapComponent mc: m = mc.map; break;
                case IncidentParms ip: m = ip.target as Map; break;
                case IAttackTargetSearcher s: m = s.Thing?.MapHeld; break;
                case IAttackTarget at: m = at.Thing?.MapHeld; break;
                default: m = null; break;
            }
            return Keep(m, id);
        }

        public static Map MapOfTarget(TargetInfo t, int id) => AnyAnnex ? Keep(t.Map, id) : null;

        public static Map MapOfCurrent(int id) => AnyAnnex ? Keep(Find.CurrentMap, id) : null;

        private static Map Keep(Map m, int id)
        {
            if (m == null || ProjectZ.Annex(m) == null)
            {
                return null;
            }
            if (Counting)
            {
                rows[id].calls++;
            }
            return m;
        }

        // ---- the helpers (map == null: the game's own value / iterator)

        public static IntVec3 AddCP(IntVec3 centre, IntVec3 p, Map map, int id)
        {
            IntVec3 r = new IntVec3(centre.x + p.x, centre.y + p.y, centre.z + p.z);
            return map == null ? r : Fold(centre, r, map, id);
        }

        public static IntVec3 AddPC(IntVec3 p, IntVec3 centre, Map map, int id)
        {
            IntVec3 r = new IntVec3(p.x + centre.x, p.y + centre.y, p.z + centre.z);
            return map == null ? r : Fold(centre, r, map, id);
        }

        // A cell past the centre's band (on the map) -> the void row next to the band in that column. The centre, a
        // centre off the map and a cell off the map are left alone. A centre in a void row counts as its band below.
        private static IntVec3 Fold(IntVec3 centre, IntVec3 r, Map map, int id)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return r;
            }
            IntVec3 size = map.Size;
            if (Counting)
            {
                rows[id].cells++;
            }
            if ((uint)centre.x >= (uint)size.x || (uint)centre.z >= (uint)size.z || (uint)r.x >= (uint)size.x || (uint)r.z >= (uint)size.z)
            {
                return r;
            }
            int min = a.BandOfRowLoose(centre.z) * a.stride;
            int max = min + a.groundZ - 1;
            if (r.z > max)
            {
                r.z = max + 1;
            }
            else if (r.z < min)
            {
                r.z = min - 1;
            }
            else
            {
                return r;
            }
            if (Counting)
            {
                rows[id].folded++;
            }
            return r;
        }

        public static IEnumerable<IntVec3> CellsAround(IntVec3 center, float radius, bool useCenter, Map map, int id)
        {
            IEnumerable<IntVec3> cells = GenRadial.RadialCellsAround(center, radius, useCenter);
            AnnexMapComponent a = map == null ? null : ProjectZ.Annex(map);
            return a == null ? cells : Clip(cells, center, map, a, id);
        }

        public static IEnumerable<IntVec3> CellsAroundMinMax(IntVec3 center, float minRadius, float maxRadius, Map map, int id)
        {
            IEnumerable<IntVec3> cells = GenRadial.RadialCellsAround(center, minRadius, maxRadius);
            AnnexMapComponent a = map == null ? null : ProjectZ.Annex(map);
            return a == null ? cells : Clip(cells, center, map, a, id);
        }

        // The cells on the centre's band (and the centre itself); everything as the game's iterator for a centre off the map.
        private static IEnumerable<IntVec3> Clip(IEnumerable<IntVec3> cells, IntVec3 center, Map map, AnnexMapComponent a, int id)
        {
            IntVec3 size = map.Size;
            bool on = (uint)center.x < (uint)size.x && (uint)center.z < (uint)size.z;
            int min = a.BandOfRowLoose(center.z) * a.stride;
            int max = min + a.groundZ - 1;
            Row row = rows[id];
            foreach (IntVec3 c in cells)
            {
                if (Counting)
                {
                    row.cells++;
                }
                if (!on || c.z >= min && c.z <= max || c == center)
                {
                    yield return c;
                }
                else if (Counting)
                {
                    row.skipped++;
                }
            }
        }

        // ProcessEquidistantCells' InBounds (with a map): the centre's band only.
        public static bool InBoundsNear(IntVec3 c, Map map, IntVec3 center)
        {
            if (!c.InBounds(map))
            {
                return false;
            }
            if (!AnyAnnex || ProjectZ.Annex(map) == null || ProjectZ.SameBand(c, center, map))
            {
                return true;
            }
            PecSkipped++;
            return false;
        }

        // ---- patching (from the mod's constructor, after PatchAll)

        private static readonly MethodInfo rcaR = AccessTools.Method(typeof(GenRadial), nameof(GenRadial.RadialCellsAround), new[] { typeof(IntVec3), typeof(float), typeof(bool) });
        private static readonly MethodInfo rcaMM = AccessTools.Method(typeof(GenRadial), nameof(GenRadial.RadialCellsAround), new[] { typeof(IntVec3), typeof(float), typeof(float) });
        private static readonly FieldInfo pattern = AccessTools.Field(typeof(GenRadial), nameof(GenRadial.RadialPattern));
        private static readonly FieldInfo manual = AccessTools.Field(typeof(GenRadial), nameof(GenRadial.ManualRadialPattern));
        private static readonly MethodInfo add = AccessTools.Method(typeof(IntVec3), "op_Addition", new[] { typeof(IntVec3), typeof(IntVec3) });
        private static readonly MethodInfo mul = AccessTools.Method(typeof(IntVec3), "op_Multiply", new[] { typeof(IntVec3), typeof(int) });

        public static void Patch(Harmony h)
        {
            Off = GenCommandLine.CommandLineArgPassed("projectznoradialclip");
            Listed = rows.Length;
            if (Off)
            {
                Log.Message($"[ProjectZ] radius clip off (-projectznoradialclip): {rows.Length} listed methods left as the game's");
                return;
            }
            Stopwatch sw = Stopwatch.StartNew();
            Assembly game = typeof(Pawn).Assembly;
            List<string> missing = new List<string>();
            for (int i = 0; i < rows.Length; i++)
            {
                Row r = rows[i];
                Type t = game.GetType(r.type, false);
                MethodBase m = null;
                if (t != null)
                {
                    foreach (MethodBase c in t.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly).Cast<MethodBase>().Concat(t.GetConstructors(AccessTools.all)))
                    {
                        if (c.Name == r.method && string.Join(",", c.GetParameters().Select(p => p.ParameterType.Name)) == r.args)
                        {
                            m = c;
                            break;
                        }
                    }
                }
                if (m == null || m.GetMethodBody() == null)
                {
                    missing.Add(r.Name + "(" + r.args + ")");
                    continue;
                }
                r.target = m;
                idOf[m] = i;
            }
            ResolveMs = sw.Elapsed.TotalMilliseconds;
            NotFound = missing.Count;
            sw.Restart();
            HarmonyMethod tr = new HarmonyMethod(typeof(BandRadial), nameof(Transpiler)) { priority = Priority.Last };
            List<string> failed = new List<string>();
            foreach (Row r in rows)
            {
                if (r.target == null)
                {
                    continue;
                }
                try
                {
                    h.Patch(r.target, transpiler: tr);
                    Patched++;
                }
                catch (Exception e)
                {
                    failed.Add(r.Name + ": " + e.GetType().Name + " " + e.Message);
                }
            }
            try
            {
                h.Patch(AccessTools.Method(typeof(GenRadial), nameof(GenRadial.ProcessEquidistantCells)), transpiler: new HarmonyMethod(typeof(BandRadial), nameof(PecTranspiler)));
            }
            catch (Exception e)
            {
                failed.Add("GenRadial.ProcessEquidistantCells: " + e.Message);
            }
            PatchMs = sw.Elapsed.TotalMilliseconds;
            List<string> odd = rows.Where(r => r.target != null && r.swaps != r.expected).Select(r => $"{r.Name} {r.swaps}/{r.expected}").ToList();
            Unmatched = rows.Count(r => r.target != null && r.swaps == 0);
            SitesSwapped = rows.Sum(r => r.swaps);
            if (missing.Count > 0)
            {
                Log.Warning("[ProjectZ] radius clip: listed method(s) not found (a game update? regenerate the list with tools\\pz-radial-scan): " + string.Join(", ", missing));
            }
            if (odd.Count > 0)
            {
                Log.Warning("[ProjectZ] radius clip: cell producers swapped / expected differ in: " + string.Join(", ", odd));
            }
            if (failed.Count > 0)
            {
                Log.Warning("[ProjectZ] radius clip: patch failed: " + string.Join("; ", failed));
            }
            if (PecSwapped != 1)
            {
                Log.Warning($"[ProjectZ] radius clip: GenRadial.ProcessEquidistantCells: {PecSwapped} InBounds calls swapped (expected 1)");
            }
            Log.Message($"[ProjectZ] radius clip: {Patched} of {rows.Length} listed methods patched, {SitesSwapped} cell producers swapped, {Unmatched} with none; resolve {ResolveMs:0.0} ms, patch {PatchMs:0} ms");
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original, ILGenerator gen)
        {
            List<CodeInstruction> code = instructions.ToList();
            if (!idOf.TryGetValue(original, out int id))
            {
                return code;
            }
            Row row = rows[id];
            List<CodeInstruction> prologue = MapGetter(row, original, id, gen, out LocalBuilder mapLocal);
            if (prologue == null)
            {
                Log.Warning($"[ProjectZ] radius clip: {row.Name}: map path '{row.mapPath}' not usable; left as the game's");
                row.swaps = 0;
                return code;
            }
            int n = 0;
            MethodInfo cp = AccessTools.Method(typeof(BandRadial), nameof(AddCP));
            MethodInfo pc = AccessTools.Method(typeof(BandRadial), nameof(AddPC));
            for (int i = 0; i < code.Count; i++)
            {
                CodeInstruction ci = code[i];
                if (ci.Calls(rcaR) || ci.Calls(rcaMM))
                {
                    MethodInfo with = ci.Calls(rcaR) ? AccessTools.Method(typeof(BandRadial), nameof(CellsAround)) : AccessTools.Method(typeof(BandRadial), nameof(CellsAroundMinMax));
                    CodeInstruction ld = new CodeInstruction(OpCodes.Ldloc, mapLocal);
                    ld.MoveLabelsFrom(ci);
                    code.InsertRange(i, new[] { ld, new CodeInstruction(OpCodes.Ldc_I4, id) });
                    i += 2;
                    code[i].opcode = OpCodes.Call;
                    code[i].operand = with;
                    n++;
                    continue;
                }
                if (ci.opcode == OpCodes.Ldsfld && (ReferenceEquals(ci.operand, pattern) || ReferenceEquals(ci.operand, manual)))
                {
                    int at = AddSite(code, i, out bool centreFirst);
                    if (at < 0)
                    {
                        continue;
                    }
                    CodeInstruction call = code[at];
                    CodeInstruction ld = new CodeInstruction(OpCodes.Ldloc, mapLocal);
                    ld.MoveLabelsFrom(call);
                    code.InsertRange(at, new[] { ld, new CodeInstruction(OpCodes.Ldc_I4, id) });
                    call.opcode = OpCodes.Call;
                    call.operand = centreFirst ? cp : pc;
                    n++;
                }
            }
            row.swaps = n;
            if (n == 0)
            {
                return code;
            }
            code.InsertRange(0, prologue);
            return code;
        }

        // The op_Addition that combines the pattern element read at code[k] (ldsfld) with a centre (the scanner's shapes):
        //   ... ldelem [ldc.i4 k; call op_Multiply] call op_Addition         -> centre + p   (centreFirst)
        //   ... ldelem <ldarg|ldloc|ldsfld> {ldfld | getter} call op_Addition  -> p + centre
        // Anything else (the pattern kept in a local, .Length, ldelema): -1.
        private static int AddSite(List<CodeInstruction> code, int k, out bool centreFirst)
        {
            centreFirst = false;
            int j = k + 1, lim = Math.Min(code.Count, k + 8);
            while (j < lim && code[j].opcode != OpCodes.Ldelem && code[j].opcode != OpCodes.Ldelema)
            {
                FlowControl f = code[j].opcode.FlowControl;
                if (f != FlowControl.Next && f != FlowControl.Call || code[j].labels.Count > 0)
                {
                    return -1;
                }
                j++;
            }
            if (j >= lim || code[j].opcode != OpCodes.Ldelem || !(code[j].operand is Type et) || et != typeof(IntVec3))
            {
                return -1;
            }
            int m = j + 1;
            if (m + 1 < code.Count && code[m].opcode.Name.StartsWith("ldc.i4") && code[m + 1].Calls(mul))
            {
                m += 2;
            }
            if (m < code.Count && code[m].Calls(add))
            {
                centreFirst = true;
                return m;
            }
            if (m != j + 1 || m >= code.Count || !RootLoad(code[m].opcode))
            {
                return -1;
            }
            m++;
            while (m < code.Count && (code[m].opcode == OpCodes.Ldfld || IsGetter(code[m])))
            {
                m++;
            }
            return m < code.Count && code[m].Calls(add) ? m : -1;
        }

        private static bool RootLoad(OpCode o) => o.Name.StartsWith("ldarg") || o.Name.StartsWith("ldloc") || o == OpCodes.Ldsfld;

        private static bool IsGetter(CodeInstruction ci) => (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo mi && !mi.IsStatic && mi.GetParameters().Length == 0 && mi.ReturnType != typeof(void);

        // IL for "Map local = BandRadial.MapOf(<path>, id)" (null-safe along the path); null when the path doesn't resolve.
        private static List<CodeInstruction> MapGetter(Row row, MethodBase m, int id, ILGenerator gen, out LocalBuilder local)
        {
            local = gen.DeclareLocal(typeof(Map));
            List<CodeInstruction> il = new List<CodeInstruction>();
            if (row.mapPath == "current")
            {
                il.Add(new CodeInstruction(OpCodes.Ldc_I4, id));
                il.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BandRadial), nameof(MapOfCurrent))));
                il.Add(new CodeInstruction(OpCodes.Stloc, local));
                return il;
            }
            string[] parts = row.mapPath.Split('.');
            Type cur;
            if (parts[0] == "this")
            {
                if (m.IsStatic || m.DeclaringType.IsValueType)
                {
                    return null;
                }
                il.Add(new CodeInstruction(OpCodes.Ldarg_0));
                cur = m.DeclaringType;
            }
            else if (parts[0].StartsWith("arg:"))
            {
                ParameterInfo[] ps = m.GetParameters();
                int idx = Array.FindIndex(ps, p => p.Name == parts[0].Substring(4));
                if (idx < 0 || ps[idx].ParameterType.IsByRef)
                {
                    return null;
                }
                il.Add(CodeInstruction.LoadArgument(idx + (m.IsStatic ? 0 : 1)));
                cur = ps[idx].ParameterType;
            }
            else
            {
                return null;
            }
            Label isNull = gen.DefineLabel();
            bool anyNullCheck = false;
            for (int i = 1; i < parts.Length; i++)
            {
                if (cur.IsValueType)
                {
                    return null;
                }
                FieldInfo f = AccessTools.Field(cur, parts[i]);
                MethodInfo g = f == null ? AccessTools.PropertyGetter(cur, parts[i]) : null;
                if (f == null && g == null)
                {
                    return null;
                }
                il.Add(new CodeInstruction(OpCodes.Dup));
                il.Add(new CodeInstruction(OpCodes.Brfalse, isNull));
                anyNullCheck = true;
                if (f != null)
                {
                    il.Add(new CodeInstruction(OpCodes.Ldfld, f));
                    cur = f.FieldType;
                }
                else
                {
                    il.Add(new CodeInstruction(g.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, g));
                    cur = g.ReturnType;
                }
            }
            if (cur == typeof(TargetInfo))
            {
                if (anyNullCheck)
                {
                    return null;
                }
                il.Add(new CodeInstruction(OpCodes.Ldc_I4, id));
                il.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BandRadial), nameof(MapOfTarget))));
                il.Add(new CodeInstruction(OpCodes.Stloc, local));
                return il;
            }
            if (cur.IsValueType)
            {
                return null;
            }
            CodeInstruction ldId = new CodeInstruction(OpCodes.Ldc_I4, id);
            ldId.labels.Add(isNull);
            il.Add(ldId);
            il.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BandRadial), nameof(MapOf))));
            il.Add(new CodeInstruction(OpCodes.Stloc, local));
            return il;
        }

        public static IEnumerable<CodeInstruction> PecTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo inBounds = AccessTools.Method(typeof(GenGrid), nameof(GenGrid.InBounds), new[] { typeof(IntVec3), typeof(Map) });
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(inBounds))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);   // center
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(BandRadial), nameof(InBoundsNear));
                    PecSwapped++;
                }
                yield return ci;
            }
        }
    }
}
