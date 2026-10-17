using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using ProjectZ.Patches;

namespace ProjectZ.Dev
{
    // Light on upper floors (mode light): each floor must be lit by its own cells (sky, roof, lamps), not by the ground
    // cells it stands over. Four 7x7 plots in a row, all on wood plank floor:
    //   T  ground room (walls, torch inside) with an open terrace on floor 2 over it (no walls, no roof up there)
    //   G  open ground, the terrace's reference (same sky, no roof)
    //   R2 ground room with a roofed room on floor 2 over it, torch upstairs only
    //   R1 roofed ground room with a torch, the upstairs room's reference
    // Shots at noon, dusk and night, viewing floor 2 and floor 1, plus skip variants (IsoRim lighting fix off, ground
    // shadows off). SAMPLE lines in results.txt give the screen pixel of each sample cell (top-left origin) for the
    // pixel measurement (tools: lightmeasure.ps1).
    public static partial class TestRunner
    {
        private static CellRect lightT, lightG, lightR2, lightR1;
        private static readonly List<KeyValuePair<string, Action<Map>>> lightShots = new List<KeyValuePair<string, Action<Map>>>();
        private static int lightIndex;
        private static string lightShotName;

        private static bool StepLight(Map map)
        {
            switch (stage)
            {
                case "l_setup":
                    SetupLight(map);
                    return true;
                case "l_shot":
                    if (lightIndex >= lightShots.Count)
                    {
                        stage = "l_end";
                        return true;
                    }
                    lightShotName = lightShots[lightIndex].Key;
                    if (IsoRimBridge.Present)
                    {
                        Info($"before {lightShotName} (frame {Time.frameCount}): glow corner R2 {GlowCorner(map, Above(lightR2.Min + new IntVec3(2, 0, 2), map))}");
                    }
                    lightShots[lightIndex].Value(map);
                    Wait(60, "l_take");
                    return true;
                case "l_take":
                    LogSamples(map, lightShotName);
                    Capture("light_" + lightShotName);
                    lightIndex++;
                    stage = "l_restore";
                    return true;
                case "l_restore":
                    IsoRimBridge.SetOption("lightingFix", true);
                    IsoRimBridge.SetOption("groundShadows", true);
                    stage = "l_shot";
                    return true;
                case "l_end":
                    IsoRimBridge.SetRoofs(false);
                    WatchCheck(map, "light end");
                    Finish();
                    return true;
            }
            return false;
        }

        private static void SetupLight(Map map)
        {
            List<Pawn> cols = Colonists(map);
            IntVec3 near = cols.Count > 0 ? new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z)) : map.Center;
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 79f, true))
            {
                CellRect r = new CellRect(c.x - 18, c.z - 4, 37, 7);
                CellRect m = r.ExpandedBy(3);
                if (!r.ExpandedBy(8).InBounds(map) || m.maxZ >= ProjectZ.GroundZ(map) - 12)
                {
                    continue;
                }
                if (m.Cells.All(x => !x.GetTerrain(map).IsWater && !map.roofGrid.Roofed(x) && !x.Fogged(map) && x.GetFirstBuilding(map) == null && x.GetTerrain(map).passability == Traversability.Standable))
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
                    if (t is Plant || t.def.category == ThingCategory.Item || t is Filth)
                    {
                        t.Destroy();
                    }
                }
            }
            DebugSettings.enableRandomMentalStates = false;
            int ox = found.minX, oz = found.minZ;
            lightT = new CellRect(ox, oz, 7, 7);
            lightG = new CellRect(ox + 10, oz, 7, 7);
            lightR2 = new CellRect(ox + 20, oz, 7, 7);
            lightR1 = new CellRect(ox + 30, oz, 7, 7);
            TerrainDef plank = DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor");
            ThingDef torch = DefDatabase<ThingDef>.GetNamed("TorchLamp");
            foreach (CellRect h in new[] { lightT, lightG, lightR2, lightR1 })
            {
                foreach (IntVec3 c in h)
                {
                    map.terrainGrid.SetTerrain(c, plank);
                }
            }
            // T: ground room under an open terrace
            foreach (IntVec3 c in lightT.EdgeCells)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            foreach (IntVec3 c in lightT)
            {
                map.terrainGrid.SetTerrain(Above(c, map), plank);
            }
            Lamp(torch, lightT.CenterCell, map);
            // R2: ground room, roofed room upstairs with the torch
            foreach (IntVec3 c in lightR2.EdgeCells)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            foreach (IntVec3 c in lightR2)
            {
                map.terrainGrid.SetTerrain(Above(c, map), plank);
            }
            foreach (IntVec3 c in lightR2.EdgeCells)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, Above(c, map), map);
            }
            foreach (IntVec3 c in lightR2.ContractedBy(1))
            {
                map.roofGrid.SetRoof(Above(c, map), RoofDefOf.RoofConstructed);
            }
            Lamp(torch, Above(lightR2.CenterCell, map), map);
            // R1: roofed ground room with the torch
            foreach (IntVec3 c in lightR1.EdgeCells)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            foreach (IntVec3 c in lightR1)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            Lamp(torch, lightR1.CenterCell, map);
            // colonists out of the way, game paused, clear sky
            IntVec3 park = new IntVec3(found.CenterCell.x, 0, Mathf.Max(2, found.minZ - 12));
            for (int i = 0; i < cols.Count; i++)
            {
                Pawn p = cols[i];
                p.drafter.Drafted = true;
                // one colonist on the terrace and one in the upstairs room (north-east corner, behind the sample cells)
                IntVec3 to = i == 0 ? Above(lightT.Min + new IntVec3(5, 0, 5), map) : i == 1 ? Above(lightR2.Min + new IntVec3(5, 0, 5), map) : CellFinder.StandableCellNear(park, map, 6f);
                Teleport(p, to);
            }
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Info($"light plots T {lightT}, G {lightG}, R2 {lightR2}, R1 {lightR1}; ground rows {ProjectZ.GroundZ(map)}, map {map.Size}; T ground roof {map.roofGrid.RoofAt(lightT.CenterCell)?.defName}, R2 ground roof {map.roofGrid.RoofAt(lightR2.CenterCell)?.defName}, R2 up roof {map.roofGrid.RoofAt(Above(lightR2.CenterCell, map))?.defName}");
            BuildLightShots(map);
            lightIndex = 0;
            Wait(60, "l_shot");
        }

        private static void Lamp(ThingDef def, IntVec3 c, Map map)
        {
            Thing t = Place(def, null, c, map);
            CompRefuelable fuel = t.TryGetComp<CompRefuelable>();
            fuel?.Refuel(fuel.Props.fuelCapacity);
            CompFlickable flick = t.TryGetComp<CompFlickable>();
            if (flick != null)
            {
                flick.SwitchIsOn = true;
            }
            t.TryGetComp<CompGlower>()?.UpdateLit(map);
        }

        private static void SetHour(Map map, int hour)
        {
            int now = GenLocalDate.HourOfDay(map);
            int add = ((hour - now) % 24 + 24) % 24;
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + add * GenDate.TicksPerHour);
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            foreach (Thing t in map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("TorchLamp")))
            {
                t.TryGetComp<CompGlower>()?.UpdateLit(map);
            }
        }

        private static void BuildLightShots(Map map)
        {
            lightShots.Clear();
            Vector3 at = new Vector3(lightT.minX + 18.5f, 0f, lightT.minZ + 3.5f);
            // the first view of floor 2 after building, shot once more below (does the light of new upstairs cells lag?)
            lightShots.Add(new KeyValuePair<string, Action<Map>>("h12_f2_first", m => { SetHour(m, 12); ShotAt(m, 1, IntVec3.FromVector3(at), 13f); }));
            foreach (int hour in new[] { 12, 18, 0 })
            {
                int h = hour;
                lightShots.Add(new KeyValuePair<string, Action<Map>>($"h{h:00}_f2", m => { SetHour(m, h); ShotAt(m, 1, IntVec3.FromVector3(at), 13f); }));
                lightShots.Add(new KeyValuePair<string, Action<Map>>($"h{h:00}_f1", m => { SetHour(m, h); ShotAt(m, 0, IntVec3.FromVector3(at), 13f); }));
                if (h != 18)
                {
                    lightShots.Add(new KeyValuePair<string, Action<Map>>($"h{h:00}_f2_nolightfix", m => { SetHour(m, h); IsoRimBridge.SetOption("lightingFix", false); ShotAt(m, 1, IntVec3.FromVector3(at), 13f); }));
                    lightShots.Add(new KeyValuePair<string, Action<Map>>($"h{h:00}_f2_noshadows", m => { SetHour(m, h); IsoRimBridge.SetOption("groundShadows", false); ShotAt(m, 1, IntVec3.FromVector3(at), 13f); }));
                }
            }
        }

        private static string GlowCorner(Map map, IntVec3 c)
        {
            return IsoRimBridge.GlowCorner(map, c);
        }

        // Screen pixel (top-left origin) of the centre of sample cells: floor 2 cells mapped by IsoRim (Camera hook).
        private static void LogSamples(Map map, string shot)
        {
            Camera cam = Find.Camera;
            List<string> parts = new List<string>();
            void S(string name, IntVec3 c)
            {
                Vector3 p = cam.WorldToScreenPoint(c.ToVector3Shifted());
                parts.Add($"{name}={Mathf.RoundToInt(p.x)},{Mathf.RoundToInt(Screen.height - p.y)}");
            }
            // three inner cells of each plot, away from walls and lamps (lamps are at the centres)
            int[][] offs = { new[] { 2, 2 }, new[] { 4, 2 }, new[] { 2, 4 } };
            for (int i = 0; i < offs.Length; i++)
            {
                IntVec3 d = new IntVec3(offs[i][0], 0, offs[i][1]);
                S($"T2_{i}", Above(lightT.Min + d, map));
                S($"T1_{i}", lightT.Min + d);
                S($"G_{i}", lightG.Min + d);
                S($"R2_{i}", Above(lightR2.Min + d, map));
                S($"R2g_{i}", lightR2.Min + d);
                S($"R1_{i}", lightR1.Min + d);
            }
            // what IsoRim's glow texture holds (copied from the game's lighting overlay) at the corner of two sample cells
            if (IsoRimBridge.Present)
            {
                Info($"glow texture corners ({shot}): T2 {GlowCorner(map, Above(lightT.Min + new IntVec3(2, 0, 2), map))} R2 {GlowCorner(map, Above(lightR2.Min + new IntVec3(2, 0, 2), map))} R1 {GlowCorner(map, lightR1.Min + new IntVec3(2, 0, 2))} T1 {GlowCorner(map, lightT.Min + new IntVec3(2, 0, 2))}");
            }
            results.Add($"SAMPLE {shot} screen={Screen.width}x{Screen.height} hour={GenLocalDate.HourOfDay(map)} skyglow={map.skyManager.CurSkyGlow:0.000} " + string.Join(" ", parts));
            Info($"glow at samples ({shot}): T2 {map.glowGrid.VisualGlowAt(Above(lightT.Min + new IntVec3(2, 0, 2), map))} T1 {map.glowGrid.VisualGlowAt(lightT.Min + new IntVec3(2, 0, 2))} G {map.glowGrid.VisualGlowAt(lightG.Min + new IntVec3(2, 0, 2))} R2 {map.glowGrid.VisualGlowAt(Above(lightR2.Min + new IntVec3(2, 0, 2), map))} R1 {map.glowGrid.VisualGlowAt(lightR1.Min + new IntVec3(2, 0, 2))}");
        }
    }
}
