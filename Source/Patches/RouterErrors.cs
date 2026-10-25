using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Patches
{
    // When working out a path over the stairs fails with an exception (routerfix, 2026-10-25; a player saw colonists wait
    // forever): the request is answered "not found" and the traveller's "no route" memo is set, so the pawn drops the job
    // instead of waiting for an answer that never comes, and the router goes on with the other requests. One full message
    // at most once per real-time minute (all maps): what failed, the pawn and its job (giver, targets, bill), the mods that
    // may be involved (in the exception's stack; other mods patching reachability and pathing) and the exception. Errors
    // within the minute are handled silently. Nothing here may throw: every step is guarded.
    public static class RouterErrors
    {
        public const float LogIntervalSeconds = 60f;
        private static float lastLog = float.NegativeInfinity;

        // Counters (tests): every error handled, and the full messages written.
        public static int Handled;
        public static int Logged;
        public static string LastMessage;

        // Test only (Dev.TestRunner mode routerfix): when it answers true for a pawn, Find throws for its requests.
        public static Func<Pawn, bool> TestThrow;

        // The request is answered by the caller; this sets the memo and writes the message (throttled). Never throws.
        public static void Handle(Exception e, Map map, Pawn pawn, IntVec3 start, LocalTargetInfo dest, PathEndMode mode, string where)
        {
            try
            {
                Handled++;
                Memo(map, pawn, start, dest);
                float now = Time.realtimeSinceStartup;
                if (now - lastLog < LogIntervalSeconds)
                {
                    return;
                }
                lastLog = now;
                string msg = Build(e, map, pawn, start, dest, mode, where);
                LastMessage = msg;
                Logged++;
                Log.Error(msg);
            }
            catch (Exception inner)
            {
                try
                {
                    Log.ErrorOnce("[ProjectZ] router error report failed: " + inner.Message + "; the original error: " + e, 81512302);
                }
                catch
                {
                }
            }
        }

        // The traveller's "no route" memo for this destination from its start (RouteGraph.Memo): reachability says no
        // meanwhile, so the work givers stop handing out the job.
        public static void Memo(Map map, Pawn pawn, IntVec3 start, LocalTargetInfo dest)
        {
            try
            {
                if (map == null || pawn == null || !dest.IsValid || !start.IsValid || !start.InBounds(map))
                {
                    return;
                }
                RouteGraph g = RouteGraph.For(map);
                Region r = map.regionGrid.GetValidRegionAt_NoRebuild(start);
                if (g == null || r == null)
                {
                    return;
                }
                g.AddMemo(pawn, dest.Cell, g.PartOf(r));
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------------------------------------------ the message

        private static string Build(Exception e, Map map, Pawn pawn, IntVec3 start, LocalTargetInfo dest, PathEndMode mode, string where)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[ProjectZ] Finding a path over the stairs failed (").Append(where).Append("); the request was answered \"no path\", so the pawn drops its job. ");
            sb.Append("At most one such message per minute; later ones are handled silently.\n");
            Safe(sb, "  pawn: ", () => PawnLine(pawn, map));
            Safe(sb, "  path: ", () => $"from {Cell(start, map)} to {Target(dest, map)} ({mode})");
            Safe(sb, "  job: ", () => JobLine(pawn, map));
            List<MethodBase> traced = new List<MethodBase>();
            Safe(sb, "  mods in the exception's stack (may be involved): ", () => List(StackMods(e, traced)));
            Safe(sb, "  other mods patching reachability / pathing (may be involved): ", () => List(PatchingMods(traced)));
            sb.Append("  exception: ").Append(e);
            return sb.ToString();
        }

        private static void Safe(StringBuilder sb, string label, Func<string> part)
        {
            string s;
            try
            {
                s = part();
            }
            catch (Exception x)
            {
                s = "(could not be read: " + x.GetType().Name + ")";
            }
            sb.Append(label).Append(string.IsNullOrEmpty(s) ? "none" : s).Append('\n');
        }

        private static string Cell(IntVec3 c, Map map)
        {
            if (!c.IsValid)
            {
                return "invalid cell";
            }
            int level = map != null && c.InBounds(map) ? Levels.LevelOf(c, map) : Levels.None;
            return level == Levels.None ? c.ToString() : $"{c} (floor {level})";
        }

        private static string Target(LocalTargetInfo t, Map map)
        {
            if (!t.IsValid)
            {
                return "none";
            }
            if (t.HasThing)
            {
                Thing th = t.Thing;
                return $"{th.LabelShort} [{th.def?.defName}] at {Cell(th.PositionHeld, map)}{(th.Spawned ? "" : " (not spawned)")}";
            }
            return "cell " + Cell(t.Cell, map);
        }

        private static string PawnLine(Pawn p, Map map)
        {
            if (p == null)
            {
                return "none";
            }
            return $"{p.LabelShort} [{p.def?.defName}], faction {p.Faction?.Name ?? "none"}, at {Cell(p.Position, map)}{(p.Drafted ? ", drafted" : "")}";
        }

        private static string JobLine(Pawn p, Map map)
        {
            Job j = p?.CurJob;
            if (j == null)
            {
                return "none";
            }
            StringBuilder sb = new StringBuilder();
            sb.Append(j.def?.defName ?? "?");
            sb.Append(", given by ").Append(j.workGiverDef != null ? "work giver " + j.workGiverDef.defName : j.jobGiver != null ? "think node " + j.jobGiver.GetType().Name : "none recorded");
            if (j.playerForced)
            {
                sb.Append(" (player forced)");
            }
            sb.Append("; A: ").Append(Target(j.targetA, map));
            sb.Append("; B: ").Append(Target(j.targetB, map));
            sb.Append("; C: ").Append(Target(j.targetC, map));
            Queue(sb, "queue A", j.targetQueueA, map);
            Queue(sb, "queue B", j.targetQueueB, map);
            if (j.bill != null)
            {
                Bill b = j.bill;
                Thing bench = b.billStack?.billGiver as Thing;
                sb.Append("; bill: ").Append(b.LabelCap.ToString());
                sb.Append(", bench ").Append(bench != null ? Target(bench, map) : "none");
                if (j.targetQueueB != null && j.targetQueueB.Count > 0)
                {
                    sb.Append(", ingredients queued ").Append(j.targetQueueB.Count);
                }
            }
            return sb.ToString();
        }

        private static void Queue(StringBuilder sb, string label, List<LocalTargetInfo> q, Map map)
        {
            if (q == null || q.Count == 0)
            {
                return;
            }
            sb.Append("; ").Append(label).Append(" (").Append(q.Count).Append("): ");
            for (int i = 0; i < q.Count && i < 3; i++)
            {
                sb.Append(i > 0 ? ", " : "").Append(Target(q[i], map));
            }
            if (q.Count > 3)
            {
                sb.Append(", ...");
            }
        }

        // ------------------------------------------------------------------------------------------------ mods

        private static Dictionary<Assembly, ModContentPack> byAssembly;

        private static ModContentPack ModOf(Assembly a)
        {
            if (a == null)
            {
                return null;
            }
            if (byAssembly == null)
            {
                Dictionary<Assembly, ModContentPack> d = new Dictionary<Assembly, ModContentPack>();
                foreach (ModContentPack m in LoadedModManager.RunningMods)
                {
                    List<Assembly> list = m?.assemblies?.loadedAssemblies;
                    if (list == null)
                    {
                        continue;
                    }
                    foreach (Assembly x in list)
                    {
                        if (x != null && !d.ContainsKey(x))
                        {
                            d[x] = m;
                        }
                    }
                }
                byAssembly = d;
            }
            return byAssembly.TryGetValue(a, out ModContentPack mod) ? mod : null;
        }

        private static ModContentPack ModOfHarmonyId(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            foreach (ModContentPack m in LoadedModManager.RunningMods)
            {
                if (m?.PackageId != null && string.Equals(m.PackageId, id, StringComparison.OrdinalIgnoreCase))
                {
                    return m;
                }
            }
            return null;
        }

        // Left out of the lists: the game, Harmony itself and ProjectZ (this mod).
        private static bool Skip(ModContentPack m)
        {
            if (m == null || m.IsOfficialMod)
            {
                return true;
            }
            string id = m.PackageId ?? "";
            return id.Equals("brrainz.harmony", StringComparison.OrdinalIgnoreCase) || m == ProjectZMod;
        }

        private static ModContentPack projectZMod;

        private static ModContentPack ProjectZMod => projectZMod ?? (projectZMod = ModOf(typeof(RouterErrors).Assembly));

        private static IEnumerable<ModContentPack> StackMods(Exception e, List<MethodBase> traced)
        {
            List<ModContentPack> mods = new List<ModContentPack>();
            for (Exception x = e; x != null; x = x.InnerException)
            {
                StackTrace st = new StackTrace(x, false);
                StackFrame[] frames = st.GetFrames();
                if (frames == null)
                {
                    continue;
                }
                foreach (StackFrame f in frames)
                {
                    MethodBase m = null;
                    try
                    {
                        m = Harmony.GetMethodFromStackframe(f) ?? f.GetMethod();   // a patched method's frame: its original
                    }
                    catch
                    {
                        m = f.GetMethod();
                    }
                    if (m == null)
                    {
                        continue;
                    }
                    if (!traced.Contains(m))
                    {
                        traced.Add(m);
                    }
                    ModContentPack mod = ModOf(m.DeclaringType?.Assembly);
                    if (!Skip(mod) && !mods.Contains(mod))
                    {
                        mods.Add(mod);
                    }
                }
            }
            return mods;
        }

        private static IEnumerable<MethodBase> KeyMethods()
        {
            Type[] reachArgs = { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) };
            yield return AccessTools.Method(typeof(Reachability), nameof(Reachability.CanReach), reachArgs);
            yield return AccessTools.Method(typeof(Reachability), "CheckRegionBasedReachability");
            yield return AccessTools.Method(typeof(Reachability), "GetCachedResult");
            foreach (MethodInfo m in typeof(PathFinder).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name == "FindPath" || m.Name == nameof(PathFinder.FindPathNow))
                {
                    yield return m;
                }
            }
            yield return AccessTools.Method(typeof(Pawn_PathFollower), "GenerateNewPathRequest");
            yield return AccessTools.Method(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.PatherTick));
            yield return AccessTools.Method(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.StartPath));
        }

        private static IEnumerable<ModContentPack> PatchingMods(List<MethodBase> traced)
        {
            List<ModContentPack> mods = new List<ModContentPack>();
            foreach (MethodBase m in KeyMethods().Concat(traced))
            {
                if (m == null)
                {
                    continue;
                }
                HarmonyLib.Patches info;
                try
                {
                    info = Harmony.GetPatchInfo(m);
                }
                catch
                {
                    continue;
                }
                if (info == null)
                {
                    continue;
                }
                foreach (Patch p in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
                {
                    ModContentPack mod = ModOf(p.PatchMethod?.DeclaringType?.Assembly) ?? ModOfHarmonyId(p.owner);
                    if (!Skip(mod) && !mods.Contains(mod))
                    {
                        mods.Add(mod);
                    }
                }
            }
            return mods;
        }

        private static string List(IEnumerable<ModContentPack> mods)
        {
            return string.Join(", ", mods.Select(m => $"{m.Name} ({m.PackageIdPlayerFacing})"));
        }
    }
}
