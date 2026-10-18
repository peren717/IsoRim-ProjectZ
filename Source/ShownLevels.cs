using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // The levels the player is offered (user decision 2026-10-17): every control that picks a level (the floor HUD's
    // bars and clicks, the floor keys and the floor scroll) works on Range; gameplay, tests, camera jumps to a target
    // and IsoRim's gravship cutscene keep all of the map's levels (Levels, FloorView.Set).
    //
    // Range = one level below the lowest USED level to one above the highest, clamped to the map's levels, and widened
    // to the ground, the viewed level (never yank the view away) and any level with a HUD dot. Levels below the ground
    // (basements, later) need nothing more: the ground counts as used, so one level below it shows once such levels exist.
    // A level is used where it has a floor (any terrain but open air: slab, ship deck, natural rock incl. mountain tops;
    // ProjectZ.HasFloor), a blueprint or frame (a slab blueprint on open air too), a pawn or an item.
    //
    // Cost, per map (State, kept on the AnnexMapComponent, not saved):
    //   - floors: one flag per level. The first ask scans each level's band until the first floor cell (a level without
    //     floors is read through once: ~groundZ x width terrain lookups). After that only TerrainChanged (FloorRoof.cs)
    //     touches it: a floor laid sets the flag at once, a floor taken away marks that level for a rescan, read at the
    //     next refresh (early exit on the first floor cell).
    //   - things: at most every RefreshTime (real time), one pass over the spawned pawns, blueprints, frames, items
    //     (HaulableEver) and fires, only reading their positions.
    public static class ShownLevels
    {
        public const float RefreshTime = 0.25f;

        public class State
        {
            public int min, max;            // the map's levels when built
            public bool[] floor, dirty;     // index L - min
            public bool anyDirty;
            public int thingLo, thingHi;    // lowest / highest level with a pawn, item, blueprint, frame or fire
            public float at = -100f;        // real time of the last refresh
        }

        // Lowest and highest level to offer on this map (0 and 0 without floors).
        public static void Range(Map map, out int lo, out int hi)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                lo = hi = 0;
                return;
            }
            State s = Get(a, map, false);
            int usedLo = Mathf.Min(0, s.thingLo), usedHi = Mathf.Max(0, s.thingHi);
            for (int L = s.min; L <= s.max; L++)
            {
                if (s.floor[L - s.min])
                {
                    usedLo = Mathf.Min(usedLo, L);
                    usedHi = Mathf.Max(usedHi, L);
                }
            }
            lo = Mathf.Max(s.min, usedLo - 1);
            hi = Mathf.Min(s.max, usedHi + 1);
            if (map == Find.CurrentMap)
            {
                int view = FloorView.Current;
                lo = Mathf.Min(lo, view);
                hi = Mathf.Max(hi, view);
            }
            lo = Mathf.Min(lo, 0);
        }

        public static bool Shown(Map map, int L)
        {
            Range(map, out int lo, out int hi);
            return L >= lo && L <= hi;
        }

        // The level a step of dir (+1 up, -1 down) from `from` reaches: the next shown one, or `from` at the end.
        public static int Step(Map map, int from, int dir)
        {
            Range(map, out int lo, out int hi);
            return Mathf.Clamp(from + dir, lo, hi);
        }

        // TerrainChanged (FloorRoof.cs): a floor laid on (added) or taken from cell c of an upper band.
        public static void FloorChanged(IntVec3 c, Map map, bool added)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            State s = a?.shownLevels;
            if (s == null || s.floor == null)
            {
                return;   // (not built yet: the first ask scans)
            }
            int L = Levels.LevelOf(c, map);
            if (L == Levels.None || L < s.min || L > s.max)
            {
                return;
            }
            if (added)
            {
                s.floor[L - s.min] = true;
            }
            else
            {
                s.dirty[L - s.min] = true;
                s.anyDirty = true;
            }
        }

        // Test hooks: refresh now; what the cache holds.
        public static void ForceRefresh(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a != null)
            {
                Get(a, map, true);
            }
        }

        public static string Describe(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return "no floors";
            }
            State s = Get(a, map, false);
            Range(map, out int lo, out int hi);
            List<int> floors = new List<int>();
            for (int L = s.min; L <= s.max; L++)
            {
                if (s.floor[L - s.min])
                {
                    floors.Add(L);
                }
            }
            return $"shown {lo}..{hi}; floors on [{string.Join(",", floors)}], things {s.thingLo}..{s.thingHi}, view {FloorView.Current}";
        }

        private static State Get(AnnexMapComponent a, Map map, bool force)
        {
            State s = a.shownLevels;
            int min = a.MinLevel, max = a.MaxLevel;
            if (s == null || s.floor == null || s.min != min || s.max != max)
            {
                s = a.shownLevels = new State { min = min, max = max, floor = new bool[max - min + 1], dirty = new bool[max - min + 1] };
                for (int L = min; L <= max; L++)
                {
                    s.floor[L - min] = L == 0 || ScanFloor(map, L);
                }
                force = true;
            }
            float now = Time.realtimeSinceStartup;
            if (!force && now - s.at < RefreshTime && now >= s.at)
            {
                return s;
            }
            s.at = now;
            if (s.anyDirty)
            {
                s.anyDirty = false;
                for (int i = 0; i < s.dirty.Length; i++)
                {
                    if (s.dirty[i])
                    {
                        s.dirty[i] = false;
                        s.floor[i] = s.min + i == 0 || ScanFloor(map, s.min + i);
                    }
                }
            }
            s.thingLo = s.thingHi = 0;
            AddThings(s, map, map.mapPawns.AllPawnsSpawned, a);
            AddThings(s, map, map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint), a);
            AddThings(s, map, map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame), a);
            AddThings(s, map, map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver), a);
            AddThings(s, map, map.listerThings.ThingsOfDef(ThingDefOf.Fire), a);
            return s;
        }

        private static void AddThings<T>(State s, Map map, IReadOnlyList<T> list, AnnexMapComponent a) where T : Thing
        {
            for (int i = 0; i < list.Count; i++)
            {
                IntVec3 c = list[i].Position;
                if (c.z < a.groundZ)
                {
                    continue;   // the ground (level 0 is always in)
                }
                int L = Levels.LevelOf(c, map);
                if (L == Levels.None)
                {
                    continue;
                }
                if (L < s.thingLo)
                {
                    s.thingLo = L;
                }
                if (L > s.thingHi)
                {
                    s.thingHi = L;
                }
            }
        }

        // Level L has a floor cell anywhere (its band read row by row, stops at the first).
        private static bool ScanFloor(Map map, int L)
        {
            int from = Levels.RowMin(map, L), to = Levels.RowMax(map, L);
            if (from < 0 || to >= map.Size.z || from > to)
            {
                return false;
            }
            TerrainGrid tg = map.terrainGrid;
            TerrainDef air = PZDefOf.PZ_OpenAir;
            int w = map.Size.x;
            int end = (to + 1) * w;
            for (int i = from * w; i < end; i++)
            {
                if (tg.TerrainAt(i) != air)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
