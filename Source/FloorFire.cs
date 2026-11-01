using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // Fire between floors (user design 2026-11-01). Only on maps with floors; a map without them runs none of this (one
    // cached map lookup per hook, nothing else), so its fires are the game's own.
    //
    // 1. Fire climbs walls only. A fire standing on a full wall (FullWall: a 1x1 edifice that holds the roof, fills the
    //    cell (fillPercent 0.9+) and blocks movement; doors and low walls such as embrasures do not count) gets, at each of
    //    its spread attempts (vanilla Fire.TrySpread: every 150..75 ticks once the fire is bigger than 1), one more try at the
    //    full wall right above it (next level, same column) and one at the full wall right below it:
    //      chance = pick * FireUtility.ChanceToStartFireIn(target)   (the game's own: the target wall's Flammability)
    //    with pick = UpPick 0.1 = the game's chance to pick any one given neighbour (0.8 for the 8 neighbours / 8), so a
    //    wall fire climbs as fast as it walks along the wall; and DownPick 0.025 = a quarter of it (heat rises: the floor
    //    above takes the flames, the one below only drips and embers). A stone wall (flammability 0) never ignites (no
    //    random number is drawn for it). Slabs, open air, stairwells and holes pass nothing (the game's spread stays on its
    //    band, BandRadial).
    // 2. Wood slabs burn through. A slab of a Woody stuff (SlabDefs.Burns, decided by the slab under any floor laid on it)
    //    takes, while a fire burns in the cell right below it or on its own cell, the fire damage the game would deal there
    //    (Fire.DoFireDamage's amount, every 150 ticks, its mean: no random number drawn). When the sum reaches the hit points
    //    of a wall of the slab's stuff (ThingDefOf.Wall, wood 195), the slab breaks (SlabDamage.Break: the support-loss
    //    collapse, letter). No damaged state: a cell no fire has touched for ClearAfter ticks (one in-game hour) starts
    //    from zero again. A wood slab burns as the game's wood plank floor (flammability 0.22, SlabDefs), never into a
    //    burned floor (its top layer is not removable: the game's TryBurnFloor does nothing to it), so a fire on a bare wood
    //    slab lasts until the slab burns through. A floor laid on any slab burns as vanilla (into its burnedDef).
    // Rain and firefoam are the game's: they put the fires out, and the exposure then clears.
    public static class FloorFire
    {
        public const float UpPick = 0.1f;
        public const float DownPick = 0.025f;
        public const int ClearAfter = 2500;   // ticks without fire before a slab's exposure is forgotten (1 in-game hour)

        public static int Climbs;      // counters (tests): walls lit by a climb up / down, slabs burned through
        public static int Drops;
        public static int BurnedThrough;
        public static long HookTicks;  // stopwatch ticks spent in the hooks on maps with floors (tests, perf)
        public static long BreakTicks; // of which in the break itself (SlabDamage.Break and its debug lines; tests, perf)
        public static int HookCalls;
        public static bool Timing;     // tests only: measure HookTicks

        // A full wall: the game's walls and every modded wall like them; not a door, a low wall (embrasure), a column or a
        // multi-cell building.
        public static bool FullWall(Building b)
        {
            if (b == null)
            {
                return false;
            }
            ThingDef d = b.def;
            return d.holdsRoof && d.fillPercent >= 0.9f && d.passability == Traversability.Impassable && d.size.x == 1 && d.size.z == 1 && !(b is Building_Door);
        }

        // The wall fire's extra tries (Fire.TrySpread postfix).
        public static void Spread(Fire fire)
        {
            if (fire.parent != null || !fire.Spawned)
            {
                return;
            }
            Map map = fire.Map;
            if (ProjectZ.Annex(map) == null)
            {
                return;
            }
            long t0 = Timing ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            IntVec3 c = fire.Position;
            if (FullWall(c.GetEdifice(map)))
            {
                int L = Levels.LevelOf(c, map);
                IntVec3 g = Levels.Ground(c, map);
                if (L != Levels.None && g.IsValid)
                {
                    if (TryLight(Levels.Cell(g, L + 1, map), map, UpPick, fire))
                    {
                        Climbs++;
                    }
                    if (TryLight(Levels.Cell(g, L - 1, map), map, DownPick, fire))
                    {
                        Drops++;
                    }
                }
            }
            if (Timing)
            {
                HookTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                HookCalls++;
            }
        }

        private static bool TryLight(IntVec3 t, Map map, float pick, Fire from)
        {
            if (!t.IsValid || !t.InBounds(map) || !FullWall(t.GetEdifice(map)))
            {
                return false;
            }
            float chance = FireUtility.ChanceToStartFireIn(t, map);
            if (chance <= 0f || !Rand.Chance(pick * chance))
            {
                return false;
            }
            return FireUtility.TryStartFireIn(t, map, 0.1f, from.instigator);
        }

        // The game's fire damage per complex-calc interval (Fire.DoFireDamage), its mean.
        public static float DamagePerInterval(float fireSize) => Mathf.Max(1f, Mathf.Clamp(0.0125f + 0.0036f * fireSize, 0.0125f, 0.05f) * 150f);

        // A burning fire's exposure for the slabs over and under it (Fire.DoComplexCalcs postfix, every 150 ticks).
        public static void Expose(Fire fire)
        {
            if (fire.parent != null || !fire.Spawned)
            {
                return;
            }
            Map map = fire.Map;
            if (ProjectZ.Annex(map) == null)
            {
                return;
            }
            long t0 = Timing ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            IntVec3 c = fire.Position;
            float dmg = DamagePerInterval(fire.fireSize);
            // the slab under the fire (a fire on the floor laid on it, or on a bare wood slab)
            TerrainDef slab = BurningSlab(c, map);
            if (slab != null)
            {
                FloorFireMap.Of(map)?.Add(c, slab, dmg);
            }
            // the slab over it
            IntVec3 up = FloorRoof.Above(c, map);
            slab = up.IsValid ? BurningSlab(up, map) : null;
            if (slab != null)
            {
                FloorFireMap.Of(map)?.Add(up, slab, dmg);
            }
            if (Timing)
            {
                HookTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                HookCalls++;
            }
        }

        // A slab at u that fire can burn through (a Woody slab; a stairs landing is part of the stairs: never broken, Add).
        public static TerrainDef BurningSlab(IntVec3 u, Map map)
        {
            TerrainDef s = SlabDamage.SlabAt(u, map);
            return SlabDefs.Burns(s) ? s : null;
        }

        // The exposure a wood slab burns through at: the hit points of a wall of its stuff.
        private static readonly Dictionary<TerrainDef, float> thresholds = new Dictionary<TerrainDef, float>();

        public static float Threshold(TerrainDef slab)
        {
            if (!thresholds.TryGetValue(slab, out float v))
            {
                ThingDef stuff = SlabDefs.StuffOf(slab);
                v = ThingDefOf.Wall.GetStatValueAbstract(StatDefOf.MaxHitPoints, stuff);
                if (v <= 0f)
                {
                    v = 195f;
                }
                thresholds[slab] = v;
            }
            return v;
        }
    }

    // The exposure of wood slabs to fire on one map: only cells a fire touched in the last hour (empty without fires).
    public class FloorFireMap : MapComponent
    {
        private sealed class Entry
        {
            public float damage;
            public int last;
        }

        private readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();

        private static Map cachedMap;
        private static FloorFireMap cached;

        public FloorFireMap(Map map) : base(map)
        {
        }

        public static FloorFireMap Of(Map map)
        {
            if (ReferenceEquals(cachedMap, map) && cached != null)
            {
                return cached;
            }
            FloorFireMap c = map.GetComponent<FloorFireMap>();
            if (c != null)
            {
                cachedMap = map;
                cached = c;
            }
            return c;
        }

        public int Count => entries.Count;

        // Exposure of the slab at u so far (0: none).
        public float ExposureAt(IntVec3 u) => u.InBounds(map) && entries.TryGetValue(map.cellIndices.CellToIndex(u), out Entry e) ? e.damage : 0f;

        // A burning wood slab at u (the caller checked) takes dmg.
        public void Add(IntVec3 u, TerrainDef slab, float dmg)
        {
            int i = map.cellIndices.CellToIndex(u);
            int now = Find.TickManager.TicksGame;
            if (!entries.TryGetValue(i, out Entry e))
            {
                e = new Entry();
                entries[i] = e;
            }
            else if (now - e.last > FloorFire.ClearAfter)
            {
                e.damage = 0f;
            }
            e.damage += dmg;
            e.last = now;
            float threshold = FloorFire.Threshold(slab);
            if (e.damage >= threshold)
            {
                if (StairwellFloor.IsStairsExit(u, map))
                {
                    // the landing of stairs is part of the stairs (as for heavy hits): never broken
                    e.damage = threshold;
                    return;
                }
                entries.Remove(i);
                long b0 = FloorFire.Timing ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                FloorFire.BurnedThrough++;
                ProjectZ.DebugLog($"wood slab at {u} ({slab.defName}) burned through: {e.damage:0} fire damage");
                SlabDamage.Break(u, map);
                if (FloorFire.Timing)
                {
                    FloorFire.BreakTicks += System.Diagnostics.Stopwatch.GetTimestamp() - b0;
                }
            }
        }

        public override void MapComponentTick()
        {
            if (entries.Count == 0 || Find.TickManager.TicksGame % 250 != 0)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            List<int> old = null;
            foreach (KeyValuePair<int, Entry> kv in entries)
            {
                if (now - kv.Value.last > FloorFire.ClearAfter)
                {
                    (old ?? (old = new List<int>())).Add(kv.Key);
                }
            }
            if (old != null)
            {
                foreach (int i in old)
                {
                    entries.Remove(i);
                }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            List<int> cells = null, last = null;
            List<float> damage = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                if (entries.Count == 0)
                {
                    return;   // nothing written without fires
                }
                cells = new List<int>();
                last = new List<int>();
                damage = new List<float>();
                foreach (KeyValuePair<int, Entry> kv in entries)
                {
                    cells.Add(kv.Key);
                    damage.Add(kv.Value.damage);
                    last.Add(kv.Value.last);
                }
            }
            Scribe_Collections.Look(ref cells, "fireCells", LookMode.Value);
            Scribe_Collections.Look(ref damage, "fireDamage", LookMode.Value);
            Scribe_Collections.Look(ref last, "fireLast", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                entries.Clear();
                if (cells != null && damage != null && last != null && cells.Count == damage.Count && cells.Count == last.Count)
                {
                    for (int k = 0; k < cells.Count; k++)
                    {
                        entries[cells[k]] = new Entry { damage = damage[k], last = last[k] };
                    }
                }
                cachedMap = null;
                cached = null;
            }
        }
    }

    [HarmonyPatch(typeof(Fire), "TrySpread")]
    public static class Fire_TrySpread_Floors_Patch
    {
        public static void Postfix(Fire __instance)
        {
            FloorFire.Spread(__instance);
        }
    }

    [HarmonyPatch(typeof(Fire), "DoComplexCalcs")]
    public static class Fire_DoComplexCalcs_Floors_Patch
    {
        public static void Postfix(Fire __instance)
        {
            FloorFire.Expose(__instance);
        }
    }
}
