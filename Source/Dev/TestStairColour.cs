using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using ProjectZ.Patches;

namespace ProjectZ.Dev
{
    // Stair colour (mode staircolour): do stairs made of a stuff read like the walls made of it? Three 5x7 houses in a row,
    // wood, steel and granite blocks: walls of that stuff, stairs of that stuff inside along the east wall (up to the north),
    // a floor over the rest of the house, a torch inside, and a wooden dresser and shelf (IsoRim's own stuff-tinted models)
    // beside the stairs for reference; north of them the same three as open yards (walls and stairs, no roof, no floor
    // above), where stairs and walls stand in the same daylight. Shots at noon and midnight, floor 1 and floor 2 views,
    // roofs drawn and hidden.
    // Glow lines in results.txt give the game's light at the stair cells, the floor next to them and the walls.
    public static partial class TestRunner
    {
        private static readonly List<CellRect> qHouses = new List<CellRect>();
        private static readonly List<CellRect> qYards = new List<CellRect>();
        private static readonly List<KeyValuePair<string, Action<Map>>> qShots = new List<KeyValuePair<string, Action<Map>>>();
        private static int qIndex;
        private static string qShotName;

        private static bool StepStairColour(Map map)
        {
            switch (stage)
            {
                case "q_setup":
                    SetupStairColour(map);
                    return true;
                case "q_shot":
                    if (qIndex >= qShots.Count)
                    {
                        stage = "q_end";
                        return true;
                    }
                    qShotName = qShots[qIndex].Key;
                    qShots[qIndex].Value(map);
                    Wait(60, "q_take");
                    return true;
                case "q_take":
                    LogStairGlow(map, qShotName);
                    Capture("colour_" + qShotName);
                    qIndex++;
                    stage = "q_shot";
                    return true;
                case "q_end":
                    IsoRimBridge.SetRoofs(false);
                    Finish();
                    return true;
            }
            return false;
        }

        private static void SetupStairColour(Map map)
        {
            List<Pawn> cols = Colonists(map);
            IntVec3 near = cols.Count > 0 ? new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z)) : map.Center;
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 79f, true))
            {
                CellRect r = new CellRect(c.x - 10, c.z - 8, 21, 17);
                CellRect m = r.ExpandedBy(3);
                if (!r.ExpandedBy(8).InBounds(map) || m.maxZ >= ProjectZ.GroundZ(map) - 12)
                {
                    continue;
                }
                if (m.Cells.All(x => !x.GetTerrain(map).IsWater && !map.roofGrid.Roofed(x) && !x.Fogged(map) && !(x.GetEdifice(map)?.def.building?.isNaturalRock ?? false) && x.GetTerrain(map).passability == Traversability.Standable))
                {
                    found = r;
                    break;
                }
            }
            if (found.Area == 0)
            {
                Result("test spot", false, "no open ground near the colonists");
                Finish();
                return;
            }
            foreach (IntVec3 c in found.ExpandedBy(3))
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable && (t is Plant || t.def.category == ThingCategory.Item || t is Filth || t is Building))
                    {
                        t.Destroy();
                    }
                }
            }
            DebugSettings.enableRandomMentalStates = false;
            TerrainDef plank = DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor");
            ThingDef torch = DefDatabase<ThingDef>.GetNamed("TorchLamp");
            ThingDef[] stuffs = { ThingDefOf.WoodLog, ThingDefOf.Steel, DefDatabase<ThingDef>.GetNamed("BlocksGranite") };
            qHouses.Clear();
            qYards.Clear();
            for (int i = 0; i < stuffs.Length; i++)
            {
                CellRect h = new CellRect(found.minX + i * 8, found.minZ, 5, 7);
                qHouses.Add(h);
                foreach (IntVec3 c in h)
                {
                    map.terrainGrid.SetTerrain(c, plank);
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                }
                foreach (IntVec3 c in h.EdgeCells)
                {
                    Place(ThingDefOf.Wall, stuffs[i], c, map);
                }
                IntVec3 sp = new IntVec3(h.maxX - 1, 0, h.minZ + 2);
                HashSet<IntVec3> well = new HashSet<IntVec3>(StairShape.Footprint(sp, Rot4.North).Cells);
                foreach (IntVec3 g in h)
                {
                    if (!well.Contains(g))
                    {
                        map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(g, 1, map), PZDefOf.PZ_UpperFloor);
                    }
                }
                Place(PZDefOf.PZ_Stairs, stuffs[i], sp, map, Rot4.North);
                Lamp(torch, new IntVec3(h.minX + 1, 0, h.minZ + 1), map);
                Place(DefDatabase<ThingDef>.GetNamed("Dresser"), ThingDefOf.WoodLog, new IntVec3(h.minX + 1, 0, h.minZ + 3), map, Rot4.East);
                Place(DefDatabase<ThingDef>.GetNamed("Shelf"), ThingDefOf.WoodLog, new IntVec3(h.minX + 1, 0, h.minZ + 5), map, Rot4.North);
                Info($"house {i} {stuffs[i].defName} {h}: stairs at {sp}, well roofs {string.Join(",", well.Select(c => map.roofGrid.RoofAt(c)?.defName ?? "none"))}");

                CellRect y = new CellRect(found.minX + i * 8, found.minZ + 10, 5, 7);
                qYards.Add(y);
                foreach (IntVec3 c in y)
                {
                    map.terrainGrid.SetTerrain(c, plank);
                }
                foreach (IntVec3 c in y.EdgeCells)
                {
                    Place(ThingDefOf.Wall, stuffs[i], c, map);
                }
                IntVec3 yp = new IntVec3(y.maxX - 1, 0, y.minZ + 2);
                Place(PZDefOf.PZ_Stairs, stuffs[i], yp, map, Rot4.North);
                Place(DefDatabase<ThingDef>.GetNamed("Dresser"), ThingDefOf.WoodLog, new IntVec3(y.minX + 1, 0, y.minZ + 3), map, Rot4.East);
                Place(DefDatabase<ThingDef>.GetNamed("Column"), stuffs[i], new IntVec3(y.minX + 1, 0, y.minZ + 5), map);
                Info($"yard {i} {stuffs[i].defName} {y}: stairs at {yp}, roofs {string.Join(",", StairShape.Footprint(yp, Rot4.North).Cells.Select(c => map.roofGrid.RoofAt(c)?.defName ?? "none"))}");
            }
            IntVec3 park = new IntVec3(found.CenterCell.x, 0, Mathf.Max(2, found.minZ - 12));
            foreach (Pawn p in cols)
            {
                p.drafter.Drafted = true;
                Teleport(p, CellFinder.StandableCellNear(park, map, 6f));
            }
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            qShots.Clear();
            IntVec3 at = new IntVec3(found.minX + 10, 0, found.minZ + 3);
            IntVec3 atYard = new IntVec3(found.minX + 10, 0, found.minZ + 13);
            foreach (int hour in new[] { 12, 0 })
            {
                int h = hour;
                qShots.Add(new KeyValuePair<string, Action<Map>>($"yard_h{h:00}", m => { SetHour(m, h); ShotAt(m, 0, atYard, 8f); }));
            }
            foreach (int hour in new[] { 12, 0 })
            {
                foreach (int floor in new[] { 0, 1 })
                {
                    foreach (bool roofs in new[] { false, true })
                    {
                        int h = hour, f = floor;
                        bool r = roofs;
                        qShots.Add(new KeyValuePair<string, Action<Map>>($"h{h:00}_f{f + 1}_roof{(r ? 1 : 0)}", m => { SetHour(m, h); ShotAt(m, f, at, 8f, r); }));
                    }
                }
            }
            qIndex = 0;
            Wait(60, "q_shot");
        }

        private static void LogStairGlow(Map map, string shot)
        {
            List<string> parts = new List<string>();
            foreach (CellRect h in qHouses)
            {
                IntVec3 sp = new IntVec3(h.maxX - 1, 0, h.minZ + 2);
                parts.Add($"[{h.minX}] corners stairs {GlowCorner(map, sp + IntVec3.North)} floor {GlowCorner(map, sp + IntVec3.North + IntVec3.West)} east {GlowCorner(map, sp + IntVec3.North + IntVec3.East)}; stairs {map.glowGrid.VisualGlowAt(sp + IntVec3.North)} floor {map.glowGrid.VisualGlowAt(sp + IntVec3.West)} wall-east {map.glowGrid.VisualGlowAt(sp + IntVec3.East)} outside {map.glowGrid.VisualGlowAt(new IntVec3(h.maxX + 1, 0, h.minZ + 3))} roof {map.roofGrid.RoofAt(sp)?.defName ?? "none"}");
            }
            Info($"glow ({shot}, hour {GenLocalDate.HourOfDay(map)}): " + string.Join(" | ", parts));
        }
    }
}
