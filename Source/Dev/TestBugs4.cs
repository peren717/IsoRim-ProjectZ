using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode bugs4 (two playtest bugs):
    //   Y1 shadows: a ground scene (a 3 x 3 wall block, a table, a tree, a drafted colonist) beside a house H whose
    //      upper floor carries a colonist at its south edge, a table and a short wall. Shots at 9h, 12h, 17h and 0h (moon)
    //      viewing the ground and floor 2, and from the other side (yaw 180). SAMPLE lines give the screen pixels of a
    //      ground cell in the block's shadow and of a lit one on its other side (tools: shadowmeasure.ps1). Run once with
    //      -projectznoshadowfix for the old open-air shadow mask.
    //   Y2 floors on a stairs landing: two open-field stairs (landings laid with them); a carpet and a wood floor are
    //      placed on the landings with the real designators through the UI pointer on floor 2, colonists build them
    //      (ticks); the landing stays a slab that can't be taken up alone; shots of the carpeted landing; then the
    //      carpeted stairs are deconstructed by colonists: what happens to the landing, the carpet and its cloth.
    public static partial class TestRunner
    {
        private static IntVec3 yO, yBlock, yTable, yTree, yStairsS, yT1, yT2, yL1, yL2;
        private static CellRect yHouse;
        private static Pawn yEdge, yUp, yGround;
        private static TerrainDef yCarpet, yWood;
        private static int yShot, yClothBefore0, yClothBefore1, yClothBefore2;
        private static float yOldYaw;
        private static Pawn yClimbG, yClimbU;
        private static Building_Stairs yS1;
        private static readonly List<KeyValuePair<string, Action<Map>>> yShots = new List<KeyValuePair<string, Action<Map>>>();

        private static IntVec3 Y(int dx, int dz) => new IntVec3(yO.x + dx, 0, yO.z + dz);

        private static bool StepBugs4(Map map)
        {
            switch (stage)
            {
                case "y_setup":
                    SetupBugs4(map);
                    return true;
                case "y_shot":
                    if (yShot == 0)
                    {
                        yOldYaw = (float)IsoRimBridge.SetOption("freeYaw", 0f);
                    }
                    if (yShot >= yShots.Count)
                    {
                        IsoRimBridge.SetOption("freeYaw", yOldYaw);
                        Wait(5, "y_place");
                        return true;
                    }
                    yShots[yShot].Value(map);
                    Wait(60, "y_shot_take");
                    return true;
                case "y_shot_take":
                    YSamples(map, yShots[yShot].Key);
                    ZCapture(yShots[yShot].Key);
                    yShot++;
                    Wait(2, "y_shot");
                    return true;

                // ---- Y2: floors on the landings, placed through the UI on floor 2
                case "y_place":
                {
                    SetHour(map, 12);
                    ShotAt(map, 1, yT1, 9f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(10, "y_place2");
                    return true;
                }
                case "y_place2":
                {
                    foreach (var kv in new[] { new KeyValuePair<TerrainDef, IntVec3>(yCarpet, yL1), new KeyValuePair<TerrainDef, IntVec3>(yWood, yL2) })
                    {
                        Designator_Build d = BuildCopyCommandUtility.FindAllowedDesignator(kv.Key) ?? new Designator_Build(kv.Key);
                        Find.DesignatorManager.Select(d);
                        IntVec3 g = ProjectZ.GroundCellOf(kv.Value, map);
                        AcceptanceReport r = RefusalAt(d, ScreenOf(g, 1), out IntVec3 at);
                        AcceptanceReport api = GenConstruct.CanPlaceBlueprintAt(kv.Key, kv.Value, Rot4.North, map);
                        UiDesignate(ScreenOf(g, 1), ScreenOf(g, 1));
                        Find.DesignatorManager.Deselect();
                        bool planned = kv.Value.GetThingList(map).Any(t => t is Blueprint && t.def.entityDefToBuild == kv.Key);
                        Result($"{kv.Key.label} on a stairs landing: the designator on floor 2 places it", r.Accepted && at == kv.Value && planned,
                            $"pointer cell {at} (landing {kv.Value}), designator '{r.Reason}' accepted {r.Accepted}, CanPlaceBlueprintAt '{api.Reason}' {api.Accepted}, blueprint {planned}");
                    }
                    foreach (Pawn p in Colonists(map))
                    {
                        Draft(p, false);
                    }
                    Thing c = ThingMaker.MakeThing(ThingDefOf.Cloth);
                    c.stackCount = 75;
                    GenSpawn.Spawn(c, Y(22, 0), map);
                    yClothBefore0 = CountOn(map, ThingDefOf.Cloth, 0);
                    SetWork(map, true);
                    TickUntil(40000, () => yL1.GetTerrain(map) == yCarpet && yL2.GetTerrain(map) == yWood, "y_built", 50);
                    return true;
                }
                case "y_built":
                {
                    tickWatch = null;
                    YWhyNot(map, yL1);
                    SetWork(map, false);
                    Result("colonists lay the carpet and the wood floor on the landings", yL1.GetTerrain(map) == yCarpet && yL2.GetTerrain(map) == yWood,
                        $"after {ticksTotal} ticks: {yL1} {yL1.GetTerrain(map).defName} (under {map.terrainGrid.UnderTerrainAt(yL1)?.defName}), {yL2} {yL2.GetTerrain(map).defName}; things {string.Join(",", yL1.GetThingList(map).Concat(yL2.GetThingList(map)).Select(t => t.def.defName))}");
                    Result("the landing stays the stairs' slab: walkable, still can't be taken up alone", Slab.At(yL1, map) && yL1.Walkable(map) && Slab.Blocker(yL1, map) == "PZ_StairsLandHere".Translate(),
                        $"slab {Slab.At(yL1, map)}, walkable {yL1.Walkable(map)}, blocker '{Slab.Blocker(yL1, map)}'");
                    AcceptanceReport rf = new Designator_RemoveFloor().CanDesignateCell(yL1);
                    Result("the carpet on the landing can be removed like any floor", rf.Accepted, $"'{rf.Reason}'");
                    // one colonist from the ground, one from the house's upper floor (down its stairs, across, up T1)
                    List<Pawn> cl = Colonists(map);
                    yClimbG = cl[0];
                    yClimbU = cl.Count > 1 ? cl[1] : null;
                    Teleport(yClimbG, Y(22, 1));
                    GoTo(yClimbG, yL1);
                    if (yClimbU != null)
                    {
                        Teleport(yClimbU, UpS(new IntVec3(yHouse.minX + 5, 0, yHouse.minZ + 5), map));
                        GoTo(yClimbU, yL2);
                    }
                    TickUntil(4000, () => yClimbG.Position == yL1 && (yClimbU == null || yClimbU.Position == yL2), "y_climbed", 1);
                    return true;
                }
                case "y_climbed":
                {
                    tickWatch = null;
                    Result("a colonist from the ground walks up onto the carpeted landing", yClimbG.Position == yL1, $"{yClimbG} at {yClimbG.Position} after {ticksTotal} ticks");
                    if (yClimbU != null)
                    {
                        Result("a colonist from the house's upper floor walks down, across and up onto the other landing", yClimbU.Position == yL2, $"{yClimbU} at {yClimbU.Position} (floor {ProjectZ.FloorOf(yClimbU.Position, map) + 1})");
                    }
                    yShot = 0;
                    Wait(2, "y_lshot");
                    return true;
                }
                case "y_lshot":
                {
                    if (yShot >= YLandingShots.Length)
                    {
                        IsoRimBridge.SetOption("freeYaw", yOldYaw);
                        Find.CameraDriver.config.sizeRange.min = zOldMin;
                        Wait(5, "y_decon");
                        return true;
                    }
                    if (yShot == 0)
                    {
                        zOldMin = Find.CameraDriver.config.sizeRange.min;
                        Find.CameraDriver.config.sizeRange.min = 3f;
                    }
                    var s = YLandingShots[yShot];
                    IsoRimBridge.SetOption("freeYaw", s.yaw);
                    ShotAt(map, s.floor, StairShape.High(yT1, Rot4.North), 4.5f);
                    Wait(40, "y_lshot_take");
                    return true;
                }
                case "y_lshot_take":
                    ZCapture(YLandingShots[yShot].name);
                    yShot++;
                    Wait(2, "y_lshot");
                    return true;

                // ---- the carpeted stairs deconstructed by colonists
                case "y_decon":
                {
                    yS1 = yT1.GetFirstThing<Building_Stairs>(map);
                    Pawn climber = yClimbG;
                    Draft(climber, false);
                    Teleport(climber, Y(20, 0));
                    Designator_Deconstruct dd = new Designator_Deconstruct();
                    AcceptanceReport ok = dd.CanDesignateThing(yS1);
                    if (ok.Accepted)
                    {
                        dd.DesignateThing(yS1);
                    }
                    Info($"deconstruct designated on {yS1}: {ok.Accepted} '{ok.Reason}'");
                    yClothBefore0 = CountOn(map, ThingDefOf.Cloth, 0);
                    yClothBefore1 = CountOn(map, ThingDefOf.Cloth, 1);
                    yClothBefore2 = map.listerThings.ThingsOfDef(ThingDefOf.Cloth).Sum(t => t.stackCount);
                    SetWork(map, true);
                    TickUntil(20000, () => !yS1.Spawned, "y_gone", 20);
                    return true;
                }
                case "y_gone":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    IntVec3 below = FloorRoof.Below(yL1, map);
                    int cloth = map.listerThings.ThingsOfDef(ThingDefOf.Cloth).Sum(t => t.stackCount);
                    Info($"REPORT stairs with a carpeted landing deconstructed after {ticksTotal} ticks: landing {yL1} now {yL1.GetTerrain(map).defName} (under {map.terrainGrid.UnderTerrainAt(yL1)?.defName ?? "-"}), roof under it {map.roofGrid.RoofAt(below)?.defName ?? "none"}; cloth on the map {yClothBefore2} -> {cloth} (ground {yClothBefore0} -> {CountOn(map, ThingDefOf.Cloth, 0)}, floor 2 {yClothBefore1} -> {CountOn(map, ThingDefOf.Cloth, 1)})");
                    Result("deconstructed: the stairs are gone, nobody and nothing left on open air", !yS1.Spawned && !OnOpenAir(map), $"spawned {yS1.Spawned}");
                    ShotAt(map, 1, yT1, 6f);
                    Wait(40, "y_gone_shot");
                    return true;
                }
                case "y_gone_shot":
                    ZCapture("landing_carpet_after_decon_f2");
                    Wait(2, "y_end");
                    return true;
                case "y_end":
                    WatchCheck(map, "bugs4 end");
                    Finish();
                    return true;
            }
            return false;
        }

        private static readonly (string name, int floor, float yaw)[] YLandingShots =
        {
            ("landing_carpet_yaw0", 1, 0f), ("landing_carpet_yaw180", 1, 180f), ("landing_carpet_yaw90", 1, 90f), ("landing_carpet_groundview", 0, 0f),
        };

        // Why nobody works on the floor blueprint or frame on a landing: reach and each construction work giver's answer.
        private static void YWhyNot(Map map, IntVec3 c)
        {
            Thing site = c.GetThingList(map).FirstOrDefault(t => t is Blueprint || t is Frame);
            if (site == null)
            {
                return;
            }
            foreach (Pawn p in Colonists(map))
            {
                List<string> parts = new List<string>();
                parts.Add($"reach touch {p.CanReach(site, PathEndMode.Touch, Danger.Deadly)} oncell {p.CanReach(c, PathEndMode.OnCell, Danger.Deadly)} region {c.GetRegion(map)?.id} ({c.GetRegion(map)?.CellCount} cells)");
                foreach (WorkGiverDef wg in DefDatabase<WorkGiverDef>.AllDefs.Where(w => w.workType == WorkTypeDefOf.Construction && w.Worker is WorkGiver_Scanner))
                {
                    WorkGiver_Scanner s = (WorkGiver_Scanner)wg.Worker;
                    if (!s.PotentialWorkThingRequest.Accepts(site))
                    {
                        continue;
                    }
                    JobFailReason.Clear();
                    bool has = s.HasJobOnThing(p, site, forced: true);
                    parts.Add($"{wg.defName} {has} '{JobFailReason.Reason}'");
                }
                Info($"why not ({site} at {c}) for {p} at {p.Position}: {string.Join("; ", parts)}");
            }
        }

        // Screen pixels (top-left origin) of a ground cell in the wall block's shadow and of a lit one on its other side.
        private static void YSamples(Map map, string shot)
        {
            Vector2 v = GenCelestial.GetLightSourceInfo(map, GenCelestial.LightType.Shadow).vector;
            Vector2 dir = v.sqrMagnitude > 1e-6f ? v.normalized : new Vector2(0f, 1f);
            Vector3 c = yBlock.ToVector3Shifted();
            Vector3 inShadow = c + new Vector3(dir.x, 0f, dir.y) * 2.0f;
            Vector3 lit = c - new Vector3(dir.x, 0f, dir.y) * 2.6f;
            string P(Vector3 w)
            {
                Vector2 s = IsoRim.View3D.WorldToScreen(new Vector3(w.x, 0f, w.z));
                return $"{Mathf.RoundToInt(s.x)},{Mathf.RoundToInt(s.y)}";
            }
            results.Add($"SAMPLE {shot} screen={Screen.width}x{Screen.height} hour={GenLocalDate.HourOfDay(map)} cast={v.x:0.00},{v.y:0.00} strength={GenCelestial.CurShadowStrength(map):0.00} shadow={P(inShadow)} lit={P(lit)}");
        }

        private static void SetupBugs4(Map map)
        {
            List<Pawn> cols = Colonists(map);
            IntVec3 near = cols.Count > 0 ? new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z)) : map.Center;
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 79f, true))
            {
                CellRect r = new CellRect(c.x - 17, c.z - 6, 36, 13);
                CellRect m = r.ExpandedBy(3);
                if (!m.InBounds(map) || m.maxZ >= ProjectZ.GroundZ(map) - 12)
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
            Find.PlaySettings.autoRebuild = false;
            yO = new IntVec3(found.minX, 0, found.minZ);
            // House H (9 x 8): walls, door, roof, slab over the inside but the stairwell; stairs S up to it.
            yHouse = new CellRect(yO.x, yO.z + 3, 9, 8);
            IntVec3 door = new IntVec3(yHouse.minX + 4, 0, yHouse.minZ);
            foreach (IntVec3 c in yHouse.EdgeCells)
            {
                Place(c == door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            foreach (IntVec3 c in yHouse)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            yStairsS = new IntVec3(yHouse.minX + 2, 0, yHouse.minZ + 3);
            HashSet<IntVec3> well = new HashSet<IntVec3>(StairShape.Footprint(yStairsS, Rot4.North).Cells);
            foreach (IntVec3 g in yHouse)
            {
                if (!well.Contains(g))
                {
                    map.terrainGrid.SetTerrain(UpS(g, map), PZDefOf.PZ_UpperFloor);
                }
            }
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, yStairsS, map, Rot4.North);
            // Upstairs: a short wall, a table, a colonist at the south edge (shadow over the edge), one inside.
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, UpS(new IntVec3(yHouse.minX + 6, 0, yHouse.minZ + 6), map), map);
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, UpS(new IntVec3(yHouse.minX + 7, 0, yHouse.minZ + 6), map), map);
            Place(DefDatabase<ThingDef>.GetNamed("EndTable"), ThingDefOf.WoodLog, UpS(new IntVec3(yHouse.minX + 6, 0, yHouse.minZ + 3), map), map);
            // Ground: a 3 x 3 wall block, a table, a grown tree, a colonist.
            yBlock = Y(14, 6);
            foreach (IntVec3 c in CellRect.CenteredOn(yBlock, 1))
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            yTable = Y(14, 1);
            Place(DefDatabase<ThingDef>.GetNamed("EndTable"), ThingDefOf.WoodLog, yTable, map);
            yTree = Y(18, 4);
            ThingDef treeDef = DefDatabase<ThingDef>.GetNamedSilentFail("Plant_TreeOak") ?? DefDatabase<ThingDef>.AllDefs.First(d => d.plant != null && d.plant.IsTree);
            Plant tree = (Plant)ThingMaker.MakeThing(treeDef);
            tree.Growth = 1f;
            GenSpawn.Spawn(tree, yTree, map);
            // Open-field stairs T1, T2 (landings laid with them).
            yT1 = Y(24, 4);
            yT2 = Y(30, 4);
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, yT1, map, Rot4.North);
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, yT2, map, Rot4.North);
            yL1 = StairShape.Exit(yT1, Rot4.North, map);
            yL2 = StairShape.Exit(yT2, Rot4.North, map);
            yCarpet = DefDatabase<TerrainDef>.AllDefs.Where(t => t.layerable && t.costList != null && t.costList.Any(c => c.thingDef == ThingDefOf.Cloth) && t.constructionSkillPrerequisite == 0 && t.defName.StartsWith("Carpet") && !t.defName.Contains("_")).OrderBy(t => t.defName).FirstOrDefault();
            yWood = DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor");
            for (int i = 0; i < 2; i++)
            {
                Thing w = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                w.stackCount = 75;
                GenSpawn.Spawn(w, Y(26 + i, 0), map);
            }
            // Colonists: one on the upper floor's south edge, one inside upstairs, one on the ground; all drafted.
            yEdge = cols[0];
            yUp = cols.Count > 1 ? cols[1] : null;
            yGround = cols.Count > 2 ? cols[2] : null;
            Teleport(yEdge, UpS(new IntVec3(yHouse.minX + 5, 0, yHouse.minZ), map));
            Draft(yEdge, true);
            if (yUp != null)
            {
                Teleport(yUp, UpS(new IntVec3(yHouse.minX + 4, 0, yHouse.minZ + 5), map));
                Draft(yUp, true);
            }
            if (yGround != null)
            {
                Teleport(yGround, Y(11, 1));
                Draft(yGround, true);
            }
            SetWork(map, false);
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            Info($"bugs4: house {yHouse}, stairs S {yStairsS}, wall block {yBlock}, table {yTable}, tree {yTree} ({treeDef.defName}), stairs T1 {yT1} landing {yL1} ({yL1.GetTerrain(map).defName}), T2 {yT2} landing {yL2}; carpet {yCarpet?.defName}; wall sun shadow height {ThingDefOf.Wall.staticSunShadowHeight}; shadow fix {(TerrainShadowMask_OpenAir_Patch.Disabled ? "OFF" : "on")}");
            // the shots: hours x views x two sides
            yShots.Clear();
            IntVec3 at = Y(11, 4);
            foreach (int hour in new[] { 9, 12, 17, 0 })
            {
                foreach (int floor in new[] { 0, 1 })
                {
                    foreach (float yaw in new[] { 0f, 180f })
                    {
                        int h = hour, f = floor;
                        float y = yaw;
                        yShots.Add(new KeyValuePair<string, Action<Map>>($"shadow_h{h:00}_f{f + 1}_yaw{y:0}", m =>
                        {
                            SetHour(m, h);
                            IsoRimBridge.SetOption("freeYaw", y);
                            ShotAt(m, f, at, 11f);
                            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                        }));
                    }
                }
            }
            if (GenCommandLine.CommandLineArgPassed("projectzskipshots"))
            {
                yShots.Clear();
            }
            yShot = 0;
            AnnexWatch.Reset();
            Wait(30, "y_shot");
        }
    }
}
