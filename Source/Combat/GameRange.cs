using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace ProjectZ.Combat
{
    // THE source of the game's ranges for the rules ProjectZ carries between floors (no copied vanilla numbers: another
    // mod may change a range, and ProjectZ follows it on every floor, in its rings and in its shell).
    //
    // A rule says how the game itself answers "how far does this reach from this source":
    //   - read: the value the game uses, through the game's own getter (after mods: a verb's EffectiveRange, a stat, a
    //     def field read at use time); read live, never cached;
    //   - check: only a yes/no at a cell (the mechanitor's CanCommandTo): probed on the source's own floor, where
    //     ProjectZ passes to the game, so the answer is the game's and other mods'. Per direction (8, integer cells) a
    //     binary search finds the last cell taken; the cells between the farthest taken and the nearest refused are then
    //     asked in the most open quadrant. The flat reach is the circle between them (the rule's own value when that
    //     lies between them: an unmodified game keeps its exact number). A rule that is no circle round the source (a
    //     square, relays) gives the farthest cell taken. Cached per source, refreshed every RefreshTicks game ticks or
    //     RefreshSeconds real seconds (paused: settings changed) or when the source's hediffs or apparel change.
    //   - fallback: the game's own value, used only when the game cannot be asked (no map, the probe found nothing,
    //     asked again from inside a probe).
    // The flat reach turns into the 3D range here too: a plain sphere (sphere: the mechanitor) or the weapon rule
    // (Range3D: +1 per floor shooting down); per floor the cross-section (HorizontalReach), round the source the shell.
    public static class GameRange
    {
        public const int RefreshTicks = 250;
        public const float RefreshSeconds = 4f;
        private const int MaxProbe = 200;   // cells per direction (past any map's half width)

        public sealed class Rule
        {
            public string name;
            public float fallbackSquared;              // the game's own value (flat reach, cells), squared as the game compares it
            public Func<Thing, float> read;            // the game's getter
            public Func<Thing, IntVec3, bool> check;   // the game's yes/no at a cell on the source's floor
            public bool sphere = true;                 // 3D: a plain sphere; false: the weapon rule (Range3D)
        }

        private sealed class Entry
        {
            public float r2;
            public int tick;
            public float time;
            public int sig;
        }

        private static readonly Dictionary<(Rule, int), Entry> cache = new Dictionary<(Rule, int), Entry>();
        private static bool probing;

        // (tests) probes run, and the last probe's result
        public static int Probes;
        public static int LastProbeCalls;
        public static string LastProbe;

        // The flat reach of the rule from the source, squared.
        public static float FlatSquared(Rule rule, Thing source)
        {
            if (rule.read != null)
            {
                float v = source == null ? Mathf.Sqrt(rule.fallbackSquared) : rule.read(source);
                return v * v;
            }
            if (rule.check == null || source == null || probing)
            {
                return rule.fallbackSquared;
            }
            // the same source asked again in one frame (a ring asks per cell): the last answer
            if (ReferenceEquals(rule, lastRule) && source.thingIDNumber == lastId && RealTime.frameCount == lastFrame)
            {
                return lastR2;
            }
            int tick = Find.TickManager?.TicksGame ?? 0;
            float now = RealTime.LastRealTime;
            int sig = Signature(source);
            var key = (rule, source.thingIDNumber);
            if (cache.TryGetValue(key, out Entry e) && tick - e.tick < RefreshTicks && tick >= e.tick && now - e.time < RefreshSeconds && now >= e.time && e.sig == sig)
            {
                return Remember(rule, source, e.r2);
            }
            float r2 = Probe(rule, source);
            if (e == null)
            {
                if (cache.Count > 256)
                {
                    cache.Clear();
                }
                e = new Entry();
                cache[key] = e;
            }
            e.r2 = r2;
            e.tick = tick;
            e.time = now;
            e.sig = sig;
            return Remember(rule, source, r2);
        }

        private static Rule lastRule;
        private static int lastId = -1, lastFrame = -1;
        private static float lastR2;

        private static float Remember(Rule rule, Thing source, float r2)
        {
            lastRule = rule;
            lastId = source.thingIDNumber;
            lastFrame = RealTime.frameCount;
            lastR2 = r2;
            return r2;
        }

        public static float Flat(Rule rule, Thing source) => Mathf.Sqrt(FlatSquared(rule, source));

        // How far over the ground the rule reaches on floor k from a source on floor ks (negative: out of reach).
        public static float HorizontalReach(Rule rule, Thing source, int ks, int k)
        {
            if (!rule.sphere)
            {
                return Range3D.HorizontalReach(Flat(rule, source), ks, k);
            }
            float v = Range3D.Rise(ks, k);
            float s = FlatSquared(rule, source) - v * v;
            return s <= 0f ? -1f : Mathf.Sqrt(s);
        }

        // Within the rule's 3D range between two floors (a: the source's cell, b: the target's; strict, as the game's).
        public static bool Within3D(Rule rule, Thing source, IntVec3 a, IntVec3 b, Map map)
        {
            float d = Range3D.Distance(a, b, map);
            if (rule.sphere)
            {
                return d * d < FlatSquared(rule, source);
            }
            float reach = Range3D.Reach(Flat(rule, source), ProjectZ.FloorOf(a, map), ProjectZ.FloorOf(b, map));
            return d * d < reach * reach;
        }

        // The shell's radius round the source (the flat reach; the weapon rule's bulge downward is RangeShell's).
        public static float ShellRadius(Rule rule, Thing source) => Flat(rule, source);

        public static void Invalidate(Thing source)
        {
            lastRule = null;
            if (source == null)
            {
                cache.Clear();
                return;
            }
            List<(Rule, int)> drop = new List<(Rule, int)>();
            foreach (var k in cache.Keys)
            {
                if (k.Item2 == source.thingIDNumber)
                {
                    drop.Add(k);
                }
            }
            foreach (var k in drop)
            {
                cache.Remove(k);
            }
        }

        // What on the source can change a range cheaply seen: its hediffs (implants) and its apparel.
        private static int Signature(Thing source)
        {
            if (source is Pawn p)
            {
                return (p.health?.hediffSet?.hediffs?.Count ?? 0) * 397 ^ (p.apparel?.WornApparelCount ?? 0);
            }
            return 0;
        }

        // ---- the probe

        private static readonly IntVec3[] Dirs =
        {
            new IntVec3(1, 0, 0), new IntVec3(-1, 0, 0), new IntVec3(0, 0, 1), new IntVec3(0, 0, -1),
            new IntVec3(1, 0, 1), new IntVec3(1, 0, -1), new IntVec3(-1, 0, 1), new IntVec3(-1, 0, -1),
        };

        private static float Probe(Rule rule, Thing source)
        {
            Map map = source.MapHeld;
            IntVec3 a = source.PositionHeld;
            float fb = rule.fallbackSquared;
            int k = map == null || !a.IsValid ? -1 : ProjectZ.FloorOf(a, map);
            if (k < 0)
            {
                return fb;
            }
            int x0 = 0, x1 = map.Size.x - 1;
            int z0 = ProjectZ.Annex(map) == null ? 0 : Levels.RowMin(map, k), z1 = ProjectZ.Annex(map) == null ? map.Size.z - 1 : Levels.RowMax(map, k);
            bool Inside(IntVec3 c) => c.x >= x0 && c.x <= x1 && c.z >= z0 && c.z <= z1;
            int calls = 0;
            bool Ask(IntVec3 c)
            {
                calls++;
                return rule.check(source, c);
            }
            int maxIn = -1, minOut = int.MaxValue;
            probing = true;
            try
            {
                foreach (IntVec3 d in Dirs)
                {
                    int tmax = 0;
                    while (tmax < MaxProbe && Inside(a + d * (tmax + 1)))
                    {
                        tmax++;
                    }
                    if (tmax == 0)
                    {
                        continue;
                    }
                    int unit = d.x * d.x + d.z * d.z;
                    // largest t in [0, tmax] taken (t = 0: none), the game's answer monotone along the ray
                    int lo = 0, hi = tmax;
                    while (lo < hi)
                    {
                        int mid = (lo + hi + 1) / 2;
                        if (Ask(a + d * mid))
                        {
                            lo = mid;
                        }
                        else
                        {
                            hi = mid - 1;
                        }
                    }
                    if (lo > 0)
                    {
                        maxIn = Mathf.Max(maxIn, lo * lo * unit);
                    }
                    if (lo < tmax)
                    {
                        minOut = Mathf.Min(minOut, (lo + 1) * (lo + 1) * unit);
                    }
                }
                // the cells between the farthest taken and the nearest refused, in the most open quadrant
                if (minOut != int.MaxValue && maxIn >= 0 && maxIn < minOut)
                {
                    int sx = a.x - x0 >= x1 - a.x ? -1 : 1, sz = a.z - z0 >= z1 - a.z ? -1 : 1;
                    int lo2 = maxIn, hi2 = minOut;
                    int rmax = Mathf.CeilToInt(Mathf.Sqrt(hi2));
                    for (int dx = 0; dx <= rmax; dx++)
                    {
                        for (int dz = 0; dz <= rmax; dz++)
                        {
                            int s = dx * dx + dz * dz;
                            if (s <= lo2 || s >= hi2)
                            {
                                continue;
                            }
                            IntVec3 c = a + new IntVec3(sx * dx, 0, sz * dz);
                            if (!Inside(c))
                            {
                                continue;
                            }
                            if (Ask(c))
                            {
                                maxIn = Mathf.Max(maxIn, s);
                            }
                            else
                            {
                                minOut = Mathf.Min(minOut, s);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce($"[ProjectZ] Asking the game for the range of {rule.name} failed: {e}", 61190323 ^ rule.name.GetHashCode());
                return fb;
            }
            finally
            {
                probing = false;
            }
            float r2;
            string how;
            if (maxIn < 0 && minOut == int.MaxValue)
            {
                r2 = fb;
                how = "nothing asked (fallback)";
            }
            else if (minOut == int.MaxValue)
            {
                r2 = maxIn + 1f;   // taken up to the floor's edges: at least that far
                how = "no refusal on the floor";
            }
            else if (maxIn >= minOut)
            {
                r2 = maxIn + 1f;   // no circle round the source: the farthest cell taken
                how = "not a circle";
            }
            else if (fb > maxIn && fb <= minOut)
            {
                r2 = fb;           // the game's own number fits its answer: kept exactly
                how = "the rule's own value";
            }
            else
            {
                r2 = minOut;       // the circle through the nearest cell refused
                how = "derived";
            }
            Probes++;
            LastProbeCalls = calls;
            LastProbe = $"{rule.name} from {source}: taken up to {maxIn}, refused from {minOut} (squared cells), reach {Mathf.Sqrt(r2):0.00} ({how}), {calls} calls";
            return r2;
        }
    }
}
