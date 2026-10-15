using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace ProjectZ
{
    // Decision D8: heat passes between floors. A room exchanges temperature with the rooms right above and below it
    // through the shared floor, cell by cell, the way the game exchanges it through a wall:
    //
    //   change per equalize interval (120 ticks) = sum over shared floor cells of (T_other - T)
    //                                              * 120 * 0.00017 (RoomTempTracker.WallEqualizeFactor) / room cells
    //
    // i.e. each cell of floor between two rooms counts as one wall cell between them. A ceiling that is an upper floor
    // (PZ_FloorRoof) no longer leaks to the outdoors as a thin roof does: that share of the thin-roof exchange
    // (0.00005 * 120 per interval, by the share of the room's cells) is taken off again. Cells with a wall or nothing
    // walkable on the other side exchange with nothing. Patched in RoomTempTracker.EqualizeTemperature (postfix; the
    // game calls it for every room every 120 ticks); the shared cells of each room are cached and rebuilt after any
    // roof, upper-floor terrain or room change.
    public static class FloorHeat
    {
        private sealed class Shared
        {
            public int version = -1;
            public readonly List<IntVec3> others = new List<IntVec3>();   // the cell across the floor, per shared cell
            public int ceilingFloors;                                    // own cells under a PZ_FloorRoof
        }

        private static int version;
        private static readonly ConditionalWeakTable<RoomTempTracker, Shared> cache = new ConditionalWeakTable<RoomTempTracker, Shared>();

        private const float WallFactor = 0.00017f;     // RoomTempTracker.WallEqualizeFactor
        private const float ThinRoofRate = 5E-05f;     // RoomTempTracker.ThinRoofEqualizeRate
        private const float Interval = 120f;           // ticks between equalizations

        public static void Dirty()
        {
            version++;
        }

        public static float Change(RoomTempTracker tracker, Room room)
        {
            Map map = room.Map;
            Shared s = cache.GetValue(tracker, _ => new Shared());
            if (s.version != version)
            {
                Rebuild(s, room, map);
                s.version = version;
            }
            if (s.others.Count == 0 && s.ceilingFloors == 0)
            {
                return 0f;
            }
            float t = tracker.Temperature;
            float sum = 0f;
            for (int i = 0; i < s.others.Count; i++)
            {
                Room other = s.others[i].GetRoom(map);
                if (other != null && other != room)
                {
                    sum += other.Temperature - t;
                }
            }
            float change = sum * Interval * WallFactor / room.CellCount;
            if (s.ceilingFloors > 0)
            {
                float diff = map.mapTemperature.OutdoorTemp - t;
                if (Math.Abs(diff) >= 100f)
                {
                    diff = Math.Sign(diff) * 100f + 5f * (diff - Math.Sign(diff) * 100f);   // as TempDiffFromOutdoorsAdjusted
                }
                change -= diff * ((float)s.ceilingFloors / room.CellCount) * ThinRoofRate * Interval;
            }
            return change;
        }

        private static void Rebuild(Shared s, Room room, Map map)
        {
            s.others.Clear();
            s.ceilingFloors = 0;
            foreach (IntVec3 c in room.Cells)
            {
                if (FloorRoof.FloorAbove(c, map))
                {
                    s.ceilingFloors++;
                    IntVec3 up = ProjectZ.CellOnFloor(ProjectZ.GroundCellOf(c, map), ProjectZ.FloorOf(c, map) + 1, map);
                    if (up.IsValid)
                    {
                        s.others.Add(up);
                    }
                }
                if (ProjectZ.HasFloor(c, map))
                {
                    IntVec3 below = FloorRoof.Below(c, map);
                    if (below.IsValid && map.roofGrid.RoofAt(below) == PZDefOf.PZ_FloorRoof)
                    {
                        s.others.Add(below);
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(RoomTempTracker), nameof(RoomTempTracker.EqualizeTemperature))]
    public static class EqualizeTemperature_FloorHeat_Patch
    {
        private static readonly AccessTools.FieldRef<RoomTempTracker, Room> roomRef = AccessTools.FieldRefAccess<RoomTempTracker, Room>("room");

        public static void Postfix(RoomTempTracker __instance)
        {
            Room room = roomRef(__instance);
            if (room == null || room.Map == null || ProjectZ.Annex(room.Map) == null || room.UsesOutdoorTemperature || room.IsDoorway)
            {
                return;
            }
            try
            {
                float change = FloorHeat.Change(__instance, room);
                if (change != 0f)
                {
                    __instance.Temperature += change;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Heat between floors failed: " + e, 61190201);
            }
        }
    }

    // Rooms rebuilt or re-roofed: the shared cells are found again.
    [HarmonyPatch(typeof(RoomTempTracker), "RegenerateEqualizationData")]
    public static class RegenerateEqualization_FloorHeat_Patch
    {
        public static void Postfix()
        {
            FloorHeat.Dirty();
        }
    }
}
