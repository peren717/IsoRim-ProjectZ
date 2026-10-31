using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode hiddenshadow (run with -projectzfloors=3 -projectzseed=N): IsoRim stops early on a block of a hidden floor that
    // has nothing to draw (Floors.DrawHiddenShadow: no sun shadow sub-mesh, nothing to rebuild), and every frame looks the
    // same as before. The scene: house H with stairs, a floor-1 slab over it (walls, a door, a bed, steel and a meal
    // upstairs, a colonist parked there) and a floor-2 slab over its west half (walls, steel); hut E with stairs 22 cells
    // east, a floor-1 slab over it and nothing on it (its blocks on floor 1 cast no shadow). Shots at 9 and 15 h viewing
    // the ground (floors 1 and 2 hidden) and at 12 h viewing floor 1 (floor 2 hidden). Then colonists build walls on E's
    // slab while the upper floors are hidden (blueprints, wood, ticks) and the shots are taken again: E's walls now cast
    // their shadow on the ground. With IsoRim's switch present every shot is taken three times in the same frame state:
    // early stop on (<name>.png), off (<name>_noskip.png), on again (<name>_again.png, the noise of two frames drawn the
    // same way): all three must be identical. Plants, items and dirt on the ground in view are taken out (they differ from
    // run to run), so the site also compares across builds (IsoRim before the change against after; -projectzhzsingle:
    // one shot each, the same steps as without the switch). Last, 4 x 300 frames each way at the ground view: frame ms and
    // the time in MapDrawer.DrawMapMesh (with -projectzfloors=10 -projectzmapsize=250: the cost of many hidden floors).
    public static partial class TestRunner
    {
        private static IntVec3 hzO;
        private static CellRect hzSite, hzHouse, hzHut, hzUp2;
        private static int hzBase, hzShot, hzBuiltWalls, hzPlanned;
        private static string hzPhase;
        private static FieldInfo hzSwitch;
        private static readonly List<(string name, int hour, int floor)> hzShots = new List<(string, int, int)>
        {
            ("g09", 9, 0), ("g15", 15, 0), ("f1_12", 12, 1),
        };
        private static readonly Type hzSunType = AccessTools.TypeByName("Verse.SectionLayer_SunShadows");
        private static readonly List<double> hzFrameMs = new List<double>();
        private static readonly List<double> hzOnFrame = new List<double>(), hzOffFrame = new List<double>(), hzOnMesh = new List<double>(), hzOffMesh = new List<double>();
        private static int hzBlock;
        private static double hzMeshMs;
        private static long hzMeshT0;
        private static bool hzMeshPatched;

        private static void HzMeshWatch()
        {
            if (hzMeshPatched)
            {
                return;
            }
            hzMeshPatched = true;
            Harmony h = new Harmony("Peren.IsoRimProjectZ.test.hzmesh");
            h.Patch(AccessTools.Method(typeof(MapDrawer), nameof(MapDrawer.DrawMapMesh)),
                prefix: new HarmonyMethod(typeof(TestRunner), nameof(HzMeshPre)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(TestRunner), nameof(HzMeshPost)) { priority = Priority.Last });
        }

        private static void HzMeshPre() => hzMeshT0 = Stopwatch.GetTimestamp();

        private static void HzMeshPost() => hzMeshMs = (Stopwatch.GetTimestamp() - hzMeshT0) * 1000.0 / Stopwatch.Frequency;
        private static long hzLastTs;
        private static int hzFrames;

        private static IntVec3 HzO(int dx, int dz) => new IntVec3(hzO.x + dx, 0, hzO.z + dz);

        private static bool StepHiddenShadow(Map map)
        {
            switch (stage)
            {
                case "hz_setup":
                    HzSetup(map);
                    return true;
                case "hz_settle":
                    hzPhase = "a";
                    hzShot = 0;
                    HzPark(map);
                    Wait(600, "hz_shot");
                    return true;
                case "hz_shot":
                {
                    if (hzShot >= hzShots.Count)
                    {
                        Wait(2, hzPhase == "a" ? "hz_build" : "hz_perf");
                        return true;
                    }
                    var s = hzShots[hzShot];
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Find.TickManager.DebugSetTicksGame(hzBase + s.hour * GenDate.TicksPerHour);
                    map.weatherManager.curWeather = WeatherDefOf.Clear;
                    map.weatherManager.lastWeather = WeatherDefOf.Clear;
                    foreach (IntVec3 c in hzSite)
                    {
                        if (map.snowGrid.GetDepth(c) > 0f)
                        {
                            map.snowGrid.SetDepth(c, 0f);
                        }
                    }
                    ShotAt(map, s.floor, hzO, 20f);
                    Find.UIRoot.screenshotMode.Active = true;   // no interface (its alerts and clock move between frames)
                    HzSwitch(false);
                    Wait(45, "hz_take");
                    return true;
                }
                case "hz_take":
                {
                    var s = hzShots[hzShot];
                    foreach (Window w in Find.WindowStack.Windows.Where(w => w.layer == WindowLayer.Dialog).ToList())
                    {
                        Find.WindowStack.TryRemove(w, doCloseSound: false);
                    }
                    HzSections(map, $"{hzPhase}_{s.name}");
                    ZCapture($"{hzPhase}_{s.name}");
                    stage = hzSwitch != null ? "hz_take_off" : "hz_next";
                    return true;
                }
                case "hz_take_off":
                    HzSwitch(true);
                    Wait(5, "hz_take_off2");
                    return true;
                case "hz_take_off2":
                {
                    var s = hzShots[hzShot];
                    ZCapture($"{hzPhase}_{s.name}_noskip");
                    stage = "hz_take_on2";
                    return true;
                }
                case "hz_take_on2":
                    HzSwitch(false);
                    Wait(5, "hz_take_on2b");
                    return true;
                case "hz_take_on2b":
                {
                    var s = hzShots[hzShot];
                    ZCapture($"{hzPhase}_{s.name}_again");
                    stage = "hz_next";
                    return true;
                }
                case "hz_next":
                    HzSwitch(false);
                    Find.UIRoot.screenshotMode.Active = false;
                    hzShot++;
                    Wait(2, "hz_shot");
                    return true;
                case "hz_build":
                    HzBuild(map);
                    return true;
                case "hz_built":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    int walls = HzWalls(map);
                    Result("hiddenshadow: colonists built the walls on E's slab (floor 1 hidden)", walls == hzPlanned && hzPlanned > 0, $"{walls} of {hzPlanned} walls after {ticksTotal} ticks");
                    hzBuiltWalls = walls;
                    foreach (IntVec3 g in hzHut.EdgeCells)
                    {
                        IntVec3 c = Levels.Cell(g, 1, map);
                        if (c.IsValid && c.GetThingList(map).Any(x => x is Blueprint || x is Frame))
                        {
                            Info($"hiddenshadow: unfinished at {c}: {string.Join(",", c.GetThingList(map).Select(x => x.def.defName))}");
                        }
                    }
                    SeedTicks("hiddenshadow_after");
                    StartTicks(1200, "hz_built2");
                    return true;
                }
                case "hz_built2":
                {
                    HzCleanup(map);
                    int finished = 0, total = 0;
                    foreach (Section sec in HzHutSections(map))
                    {
                        total++;
                        if (HzSunFinished(sec))
                        {
                            finished++;
                        }
                    }
                    Result("hiddenshadow: E's floor-1 blocks have a sun shadow now", finished > 0, $"{finished} of {total} blocks with a finished sun shadow mesh");
                    hzPhase = "b";
                    hzShot = 0;
                    HzPark(map);
                    Wait(600, "hz_shot");
                    return true;
                }
                case "hz_perf":
                    // 4 x 300 frames each way (early stop on, off, on, off, ...) at the ground view, upper floors hidden (window
                    // hidden): frame ms and the time in MapDrawer.DrawMapMesh
                    ShotAt(map, 0, hzO, 20f);
                    HzMeshWatch();
                    hzBlock = 0;
                    HzSwitch(false);
                    hzFrames = -30;
                    stage = "hz_perf_run";
                    return true;
                case "hz_perf_run":
                {
                    long now = Stopwatch.GetTimestamp();
                    bool off = hzSwitch != null && (bool)hzSwitch.GetValue(null);
                    if (hzFrames > 0)
                    {
                        hzFrameMs.Add((now - hzLastTs) * 1000.0 / Stopwatch.Frequency);
                        (off ? hzOffFrame : hzOnFrame).Add((now - hzLastTs) * 1000.0 / Stopwatch.Frequency);
                        (off ? hzOffMesh : hzOnMesh).Add(hzMeshMs);
                    }
                    hzLastTs = now;
                    hzFrames++;
                    if (hzFrames <= 300)
                    {
                        return true;
                    }
                    hzBlock++;
                    if (hzSwitch != null && hzBlock < 8)
                    {
                        HzSwitch(!off);
                        hzFrames = -30;
                        return true;
                    }
                    string Line(List<double> l) => l.Count == 0 ? "-" : $"mean {l.Average():0.000}, median {l.OrderBy(x => x).ElementAt(l.Count / 2):0.000}";
                    Info($"hiddenshadow frame ms (ground view, upper floors hidden, {hzOnFrame.Count} frames each way): early stop on {Line(hzOnFrame)}; off {Line(hzOffFrame)}");
                    Info($"hiddenshadow DrawMapMesh ms per frame: early stop on {Line(hzOnMesh)}; off {Line(hzOffMesh)}");
                    HzSwitch(false);
                    Wait(2, "hz_end");
                    return true;
                }
                case "hz_end":
                    Finish();
                    return true;
            }
            return false;
        }

        private static void HzSwitch(bool off)
        {
            if (hzSwitch != null)
            {
                hzSwitch.SetValue(null, off);
            }
        }

        private static void HzSetup(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            DebugSettings.enableRandomMentalStates = false;
            Find.Storyteller.storytellerComps.Clear();
            Find.PlaySettings.autoRebuild = false;
            NoAmbushes(map);
            NoRoofCollapse(map);
            Type floors = AccessTools.TypeByName("IsoRim.Floors");
            hzSwitch = floors != null ? AccessTools.Field(floors, "SkipEmptyOff") : null;
            if (GenCommandLine.CommandLineArgPassed("projectzhzsingle"))
            {
                hzSwitch = null;   // one shot each, the same steps as with IsoRim before the change (cross-build comparison)
            }
            Info("hiddenshadow: IsoRim's early stop switch " + (hzSwitch != null ? "present (each shot on and off)" : "absent (IsoRim before the change: one shot each)"));
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || a.MaxLevel < 2)
            {
                Result("hiddenshadow: a map with two upper floors", false, "run with -projectzfloors=3");
                Finish();
                return;
            }
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
            {
                p.Destroy();
            }
            int gz = a.groundZ;
            List<Pawn> cols = Colonists(map);
            IntVec3 near = new IntVec3(map.Size.x / 2, 0, gz / 2);   // (not the colonists: they wander before the test starts)
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 60f, true))
            {
                CellRect r = new CellRect(c.x - 28, c.z - 17, 56, 34);
                if (r.InBounds(map) && r.maxZ < gz - 3 && r.Cells.All(x => !x.GetTerrain(map).IsWater && x.GetThingList(map).All(t => t is Pawn || t.def.destroyable)))
                {
                    found = r;
                    break;
                }
            }
            if (found.Area == 0)
            {
                Result("hiddenshadow: test spot", false, "no 56 x 34 spot near the colonists");
                Finish();
                return;
            }
            hzSite = found;
            hzO = new IntVec3(found.minX + 28, 0, found.minZ + 17);
            foreach (IntVec3 g in hzSite)
            {
                for (int k = 0; k <= 2; k++)
                {
                    IntVec3 c = k == 0 ? g : Levels.Cell(g, k, map);
                    if (!c.IsValid)
                    {
                        continue;
                    }
                    foreach (Thing t in c.GetThingList(map).ToList())
                    {
                        if (!(t is Pawn) && t.def.destroyable)
                        {
                            t.Destroy();
                        }
                    }
                }
                map.terrainGrid.SetTerrain(g, TerrainDefOf.Concrete);
                map.roofGrid.SetRoof(g, null);
                if (map.fogGrid.IsFogged(g))
                {
                    map.fogGrid.Unfog(g);
                }
            }
            HzNoPlants(map);
            // nothing forbidden (its mark pulses in real time, also while paused)
            foreach (Thing t in map.listerThings.AllThings.ToList())
            {
                if (t.def.HasComp(typeof(CompForbiddable)))
                {
                    t.SetForbidden(false, false);
                }
            }
            // house H: walls, a door, a floor-1 slab over it, stairs inside
            hzHouse = new CellRect(hzO.x - 26, hzO.z - 6, 12, 10);
            foreach (IntVec3 c in hzHouse.EdgeCells)
            {
                bool door = c.z == hzHouse.minZ && c.x == hzHouse.minX + 6;
                Place(door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            RpFloor(map, hzHouse, 1);
            bool stairsH = HzStairs(map, new[] { new IntVec3(hzHouse.minX + 8, 0, hzHouse.minZ + 2), new IntVec3(hzHouse.minX + 9, 0, hzHouse.minZ + 2), new IntVec3(hzHouse.minX + 8, 0, hzHouse.minZ + 4) });
            // floor 1 over H: walls with a door, a bed, steel, a meal
            foreach (IntVec3 g in hzHouse.EdgeCells)
            {
                IntVec3 c = Levels.Cell(g, 1, map);
                bool door = g.x == hzHouse.maxX && g.z == hzHouse.minZ + 6;
                if (c.IsValid && ProjectZ.HasFloor(c, map) && c.GetEdifice(map) == null)
                {
                    Place(door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
                }
            }
            Place(ThingDefOf.Bed, ThingDefOf.WoodLog, Levels.Cell(new IntVec3(hzHouse.minX + 2, 0, hzHouse.maxZ - 3), 1, map), map, Rot4.South);
            D3Spawn(ThingDefOf.Steel, 40, Levels.Cell(new IntVec3(hzHouse.minX + 4, 0, hzHouse.minZ + 2), 1, map), map);
            D3Spawn(ThingDefOf.MealSimple, 5, Levels.Cell(new IntVec3(hzHouse.minX + 5, 0, hzHouse.minZ + 2), 1, map), map);
            // floor 2 over H's west half: walls, steel
            hzUp2 = new CellRect(hzHouse.minX, hzHouse.minZ, 6, 10);
            RpFloor(map, hzUp2, 2);
            foreach (IntVec3 g in hzUp2.EdgeCells)
            {
                IntVec3 c = Levels.Cell(g, 2, map);
                if (c.IsValid && ProjectZ.HasFloor(c, map) && c.GetEdifice(map) == null)
                {
                    Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
                }
            }
            D3Spawn(ThingDefOf.Steel, 30, Levels.Cell(new IntVec3(hzUp2.minX + 2, 0, hzUp2.minZ + 4), 2, map), map);
            // hut E: walls, a door, a floor-1 slab over it, stairs inside; nothing upstairs
            hzHut = new CellRect(hzO.x + 8, hzO.z - 5, 9, 9);
            foreach (IntVec3 c in hzHut.EdgeCells)
            {
                bool door = c.z == hzHut.minZ && c.x == hzHut.minX + 4;
                Place(door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            RpFloor(map, hzHut, 1);
            bool stairsE = HzStairs(map, new[] { new IntVec3(hzHut.minX + 4, 0, hzHut.minZ + 2), new IntVec3(hzHut.minX + 3, 0, hzHut.minZ + 2), new IntVec3(hzHut.minX + 5, 0, hzHut.minZ + 2) });
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            // E's floor-1 blocks: no sun shadow yet (the case the early stop is for)
            int hutSections = 0, hutFinished = 0;
            foreach (Section sec in HzHutSections(map))
            {
                hutSections++;
                if (HzSunFinished(sec))
                {
                    hutFinished++;
                }
            }
            float hf = GenLocalDate.HourFloat(map);
            hzBase = Find.TickManager.TicksGame + Mathf.RoundToInt((24f - hf) * GenDate.TicksPerHour);
            Info($"hiddenshadow scene: site {hzSite}, house {hzHouse} (stairs {stairsH}), floor 2 over {hzUp2}, hut {hzHut} (stairs {stairsE}), colonists {cols.Count}, E's floor-1 blocks {hutSections} ({hutFinished} with a sun shadow), shot day starts at tick {hzBase}");
            Result("hiddenshadow: scene built", stairsH && stairsE && hutSections > 0 && hutFinished == 0, $"stairs H {stairsH}, E {stairsE}; E blocks {hutSections}, with shadow {hutFinished}");
            SeedTicks("hiddenshadow_settle");
            StartTicks(120, "hz_settle");
        }

        private static bool HzStairs(Map map, IntVec3[] tries)
        {
            foreach (IntVec3 st in tries)
            {
                RpOpenWell(map, st, Rot4.North);
                if (GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, st, Rot4.North, map, stuffDef: ThingDefOf.WoodLog).Accepted)
                {
                    Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, st, map, Rot4.North);
                    return true;
                }
                foreach (IntVec3 u in rpOpened)
                {
                    map.terrainGrid.SetTerrain(u, PZDefOf.PZ_UpperFloor);
                }
            }
            return false;
        }

        // Colonists parked (drafted) where they stand in every shot: one upstairs in H, the rest in a row south of the site.
        private static void HzPark(Map map)
        {
            List<Pawn> cols = Colonists(map).OrderBy(p => p.thingIDNumber).ToList();
            for (int i = 0; i < cols.Count; i++)
            {
                IntVec3 at = i == 0 ? Levels.Cell(new IntVec3(hzHouse.minX + 8, 0, hzHouse.maxZ - 2), 1, map) : HzO(-10 + i * 3, -14);
                Teleport(cols[i], at);
                Draft(cols[i], true);
                cols[i].Rotation = Rot4.South;
            }
        }

        private static void HzBuild(Map map)
        {
            FloorView.Set(0);
            foreach (Pawn p in Colonists(map))
            {
                Draft(p, false);
            }
            hzPlanned = 0;
            foreach (IntVec3 g in hzHut.EdgeCells)
            {
                IntVec3 c = Levels.Cell(g, 1, map);
                if (g.z == hzHut.minZ && g.x == hzHut.minX + 4)
                {
                    continue;   // a gap over the door
                }
                if (c.IsValid && GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, c, Rot4.North, map, stuffDef: ThingDefOf.WoodLog).Accepted)
                {
                    GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, c, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
                    hzPlanned++;
                }
            }
            for (int i = 0; i < 4; i++)
            {
                D3Spawn(ThingDefOf.WoodLog, 75, HzO(10 + i, -8), map);
            }
            SetWork(map, true);
            foreach (Pawn p in Colonists(map))
            {
                SkillRecord sk = p.skills?.GetSkill(SkillDefOf.Construction);
                if (sk != null && !sk.TotallyDisabled)
                {
                    sk.Level = 20;   // no botches (their text marks linger on the hidden floor and fade while shown)
                }
                if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                {
                    p.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2);
                }
            }
            Info($"hiddenshadow: {hzPlanned} wall blueprints on E's floor-1 slab; floors 1-2 hidden while the colonists work");
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            SeedTicks("hiddenshadow_build");
            TickUntil(60000, () =>
            {
                KeepFed(map);
                return HzWalls(map) == hzPlanned;
            }, "hz_built", 250);
        }

        private static int HzWalls(Map map)
        {
            int n = 0;
            foreach (IntVec3 g in hzHut.EdgeCells)
            {
                IntVec3 c = Levels.Cell(g, 1, map);
                if (c.IsValid && c.GetEdifice(map) is Building b && b.def == ThingDefOf.Wall)
                {
                    n++;
                }
            }
            return n;
        }

        // Leftover wood and dirt after the work (so the scene is the same apart from E's walls).
        private static void HzCleanup(Map map)
        {
            int gone = 0;
            foreach (IntVec3 g in hzSite.ExpandedBy(4).ClipInsideMap(map))
            {
                for (int k = 0; k <= 2; k++)
                {
                    IntVec3 c = k == 0 ? g : Levels.Cell(g, k, map);
                    if (!c.IsValid)
                    {
                        continue;
                    }
                    foreach (Thing t in c.GetThingList(map).ToList())
                    {
                        if (t is Filth || t.def == ThingDefOf.WoodLog || t is Blueprint || t is Frame)
                        {
                            t.Destroy();
                            gone++;
                        }
                    }
                }
            }
            Info($"hiddenshadow: cleaned {gone} leftovers (wood, dirt)");
            HzNoPlants(map);
        }

        // No plants, items or dirt on the ground in view: they differ from run to run (plants grow and spread, the map's
        // chunks and scattered items are not seeded), the rest of the scene is the same, so the shots also compare across runs
        // (IsoRim before and after the change). The test's own items are upstairs.
        private static void HzNoPlants(Map map)
        {
            int n = 0;
            foreach (IntVec3 c in hzSite.ExpandedBy(45).ClipInsideMap(map))
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if ((t is Plant || t is Filth || t.def.category == ThingCategory.Item) && t.def.destroyable)
                    {
                        t.Destroy();
                        n++;
                    }
                }
            }
            Info($"hiddenshadow: {n} plants, items and dirt taken out of view");
        }

        private static IEnumerable<Section> HzHutSections(Map map)
        {
            HashSet<Section> seen = new HashSet<Section>();
            foreach (IntVec3 g in hzHut)
            {
                IntVec3 c = Levels.Cell(g, 1, map);
                if (c.IsValid)
                {
                    Section s = map.mapDrawer.SectionAt(c);
                    if (s != null && seen.Add(s))
                    {
                        yield return s;
                    }
                }
            }
        }

        private static SectionLayer HzSunLayer(Section s) => hzSunType != null ? s.GetLayer(hzSunType) : null;

        private static bool HzSunFinished(Section s)
        {
            SectionLayer l = HzSunLayer(s);
            return l != null && l.subMeshes.Any(m => m.finalized && !m.disabled);
        }

        // How many blocks of the hidden floors the early stop passes over in this view.
        private static void HzSections(Map map, string shot)
        {
            int hidden = 0, empty = 0, dirty = 0;
            Section[,] secs = AccessTools.Field(typeof(MapDrawer), "sections").GetValue(map.mapDrawer) as Section[,];
            if (secs == null)
            {
                return;
            }
            int view = FloorView.Current;
            foreach (Section s in secs)
            {
                int k = ProjectZ.FloorOf(s.botLeft, map);
                if (k <= view)
                {
                    continue;
                }
                hidden++;
                SectionLayer l = HzSunLayer(s);
                if (l == null)
                {
                    continue;
                }
                if (l.Dirty)
                {
                    dirty++;
                }
                else if (!l.subMeshes.Any(m => m.finalized && !m.disabled))
                {
                    empty++;
                }
            }
            Info($"hiddenshadow {shot}: hidden blocks {hidden}, with nothing to draw {empty}, waiting for a rebuild {dirty}");
        }
    }
}
