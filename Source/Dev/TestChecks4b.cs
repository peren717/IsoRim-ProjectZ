using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode checks4b (report only; run with -projectzfloors=3 -projectzseed=N, with or without -projectztallmountains).
    // Three more vanilla spreaders started at the ground's north edge, under floors, each in its own fixture area and
    // with its own random state (Seeded), each run on the game's own tick until it stops or a tick budget:
    //   L  Odyssey lava flow (GameCondition_LavaFlow: CanLavaSpreadInto / CanLavaPotentiallySpreadInto) on a volcanic
    //      rock fixture at the north edge;
    //   E  Odyssey lava emergence (the LavaEmergence incident from a deep-lava pool at the north edge, LavaEmergence.
    //      CanLavaSpreadInto), then a LavaEmergenceImmediate (as a falling lava rock makes) on the north row;
    //   T  Anomaly fleshmass heart (CompGrowsFleshmassTendrils.CanPlaceFleshmass) emerging near the north edge.
    // For each: cells reaching the void rows or a floor band, the first leak (ticks), whether the spreader reached the
    // ground's north row, and a probe of the spread check over every annex cell (which cells it would accept).
    public static partial class TestRunner
    {
        private static CellRect k4bL, k4bE, k4bPool, k4bStrip;
        private static IntVec3 k4bHeartAt;
        private static GameCondition_LavaFlow k4bFlow;
        private static LavaEmergence k4bEm;
        private static Thing k4bHeart;
        private static int k4bFirstLeak, k4bWatch;
        private static List<StorytellerComp> k4bComps;

        private static bool K4bLava(IntVec3 c, Map map) => map.terrainGrid.TempTerrainAt(c) == TerrainDefOf.LavaShallow;

        private static bool K4bFlesh(Thing t) => t.def == ThingDefOf.Fleshmass_Active || t.def == ThingDefOf.Fleshmass || t.def == ThingDefOf.FleshmassSpitter || t.def == ThingDefOf.NerveBundle || t.def == ThingDefOf.FleshmassHeart;

        // Lava cells above the ground: total, void, band (by base terrain), northmost row.
        private static int K4bLavaAbove(Map map, out string detail)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int n = 0, voidN = 0, maxRow = -1;
            Dictionary<string, int> byTerrain = new Dictionary<string, int>();
            for (int z = a.groundZ; z < map.Size.z; z++)
            {
                for (int x = 0; x < map.Size.x; x++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    if (!K4bLava(c, map))
                    {
                        continue;
                    }
                    n++;
                    maxRow = Mathf.Max(maxRow, z);
                    if (ProjectZ.IsVoid(c, map))
                    {
                        voidN++;
                    }
                    else
                    {
                        string k = map.terrainGrid.BaseTerrainAt(c)?.defName ?? "null";
                        byTerrain[k] = byTerrain.TryGetValue(k, out int v) ? v + 1 : 1;
                    }
                }
            }
            detail = $"{n} lava cells above the ground: {voidN} void, {n - voidN} on a band [{string.Join(", ", byTerrain.Select(kv => kv.Key + " " + kv.Value))}], northmost row {maxRow} (ground ends at {a.groundZ - 1})";
            return n;
        }

        private static int K4bFleshAbove(Map map, out string detail)
        {
            List<Thing> up = map.listerThings.AllThings.Where(t => t.Spawned && K4bFlesh(t) && ProjectZ.IsAnnex(t.Position, map)).ToList();
            int voidN = up.Count(t => ProjectZ.IsVoid(t.Position, map));
            List<Pawn> beasts = map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == Faction.OfEntities && ProjectZ.IsAnnex(p.Position, map)).ToList();
            detail = $"{up.Count} fleshmass things above the ground: {voidN} void, {up.Count - voidN} on a band [{string.Join(" ", up.Take(8).Select(t => t.def.defName + "@" + K4Cell(t.Position, map)))}]; entity pawns above the ground {beasts.Count}";
            return up.Count;
        }

        // The spread check over every annex cell: how many it would accept, void and band cells (band by terrain).
        private static string K4bGate(Map map, Func<IntVec3, bool> accepts)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int voidAll = 0, voidYes = 0, bandAll = 0, bandYes = 0;
            Dictionary<string, int> yesBy = new Dictionary<string, int>();
            for (int z = a.groundZ; z < map.Size.z; z++)
            {
                for (int x = 0; x < map.Size.x; x++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    bool v = ProjectZ.IsVoid(c, map);
                    bool yes;
                    try
                    {
                        yes = accepts(c);
                    }
                    catch (Exception)
                    {
                        yes = false;
                    }
                    if (v)
                    {
                        voidAll++;
                        voidYes += yes ? 1 : 0;
                    }
                    else
                    {
                        bandAll++;
                        if (yes)
                        {
                            bandYes++;
                            string k = c.GetTerrain(map)?.defName ?? "null";
                            yesBy[k] = yesBy.TryGetValue(k, out int n) ? n + 1 : 1;
                        }
                    }
                }
            }
            return $"accepts {voidYes}/{voidAll} void cells, {bandYes}/{bandAll} band cells [{string.Join(", ", yesBy.OrderByDescending(kv => kv.Value).Take(6).Select(kv => kv.Key + " " + kv.Value))}]";
        }

        private static void K4bPark(Map map)
        {
            int i = 0;
            foreach (Pawn p in Colonists(map))
            {
                Draft(p, false);
                Teleport(p, new IntVec3(10 + i++ * 2, 0, 12));
            }
        }

        private static void K4bStartWatch()
        {
            k4bFirstLeak = -1;
            k4bWatch = 0;
        }

        private static bool StepChecks4b(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            int gz = an?.groundZ ?? map.Size.z;
            switch (stage)
            {
                case "k4b_setup":
                    if (an == null || Levels.Max(map) < 2 || map.Size.x < 140)
                    {
                        Result("test spot", false, $"needs 3 floors and a 140+ wide ground (run with -projectzfloors=3); levels {Levels.Max(map) + 1}");
                        Finish();
                        return true;
                    }
                    Info($"map {map.Size}, ground {map.Size.x} x {gz}, stride {an.stride}, floors {an.floors}, tall mountains {an.tallMountains}, biome {map.Biome.defName}, outdoor temp {map.mapTemperature.OutdoorTemp:0.0}");
                    SetWork(map, false);
                    k4bComps = Find.Storyteller.storytellerComps.ToList();
                    Find.Storyteller.storytellerComps.Clear();
                    K4Clear(map, new CellRect(8, gz - 14, 136, 14));
                    K4Clear(map, new CellRect(8, 0, 136, 4));
                    StartTicks(600, "k4b_fix");
                    return true;
                case "k4b_fix":
                {
                    k4bStrip = new CellRect(8, 0, 136, 3);
                    k4bL = new CellRect(10, gz - 12, 36, 12);
                    k4bE = new CellRect(56, gz - 12, 36, 12);
                    k4bPool = new CellRect(70, gz - 8, 6, 4);
                    k4bHeartAt = new IntVec3(120, 0, gz - 7);
                    int slabs = 0;
                    Seeded("k4b_fix", () =>
                    {
                        foreach (IntVec3 g in k4bStrip)
                        {
                            IntVec3 u = K4Up(g, map);
                            if (u.GetTerrain(map) == PZDefOf.PZ_OpenAir)
                            {
                                map.terrainGrid.SetTerrain(u, PZDefOf.PZ_UpperFloor);
                                slabs++;
                            }
                        }
                    });
                    K4bPark(map);
                    Info($"fixtures: lava flow {k4bL} (volcanic rock), lava emergence {k4bE} with a deep-lava pool {k4bPool}, fleshmass heart at {k4bHeartAt}; slab strip on floor 2 over {k4bStrip}: {slabs} slab cells laid (row {K4Up(k4bStrip.Cells.First(), map).z}, right past the void)");
                    stage = "k4b_l_setup";
                    return true;
                }

                // ================================================================ L: lava flow
                case "k4b_l_setup":
                {
                    List<IntVec3> mine = new List<IntVec3>();
                    Seeded("k4b_l", () =>
                    {
                        foreach (IntVec3 c in k4bL)
                        {
                            map.terrainGrid.SetTerrain(c, TerrainDefOf.VolcanicRock);
                        }
                        // the edge-cell seed finder alone (Rot4.Random edge): where it puts seeds on this map
                        int picks = 0, up = 0, inFix = 0;
                        GameCondition_LavaFlow probe = (GameCondition_LavaFlow)GameConditionMaker.MakeConditionPermanent(GameConditionDefOf.LavaFlow);
                        System.Reflection.MethodInfo gi = AccessTools.Method(typeof(GameCondition_LavaFlow), "GetInitialCells");
                        for (int i = 0; i < 40; i++)
                        {
                            foreach (IntVec3 c in (IEnumerable<IntVec3>)gi.Invoke(probe, new object[] { map }))
                            {
                                picks++;
                                up += ProjectZ.IsAnnex(c, map) ? 1 : 0;
                                inFix += k4bL.Contains(c) ? 1 : 0;
                            }
                        }
                        Info($"L0 lava flow's own seed finder (GetInitialCells, a random map edge, volcanic rock only), 40 calls: {picks} seeds, {inFix} in the fixture (north row), {up} above the ground");
                        Result("L0 lava flow seeds (GetInitialCells) are never above the ground", up == 0, $"{picks} seeds, {up} above the ground");
                        // the incident's own condition (IncidentWorker_LavaFlow: a permanent LavaFlow condition), with three
                        // seed cells in the fixture besides its own edge seeds
                        k4bFlow = (GameCondition_LavaFlow)GameConditionMaker.MakeConditionPermanent(GameConditionDefOf.LavaFlow);
                        HashSet<IntVec3> open = Traverse.Create(k4bFlow).Field("openCells").GetValue<HashSet<IntVec3>>();
                        foreach (int x in new[] { k4bL.minX + 6, k4bL.CenterCell.x, k4bL.maxX - 6 })
                        {
                            IntVec3 c = new IntVec3(x, 0, k4bL.minZ + 2);
                            map.terrainGrid.SetTempTerrain(c, TerrainDefOf.LavaShallow);
                            open.Add(c);
                            mine.Add(c);
                        }
                        map.gameConditionManager.RegisterCondition(k4bFlow);
                    });
                    HashSet<IntVec3> all = Traverse.Create(k4bFlow).Field("openCells").GetValue<HashSet<IntVec3>>();
                    List<IntVec3> own = all.Where(c => !mine.Contains(c)).ToList();
                    Info($"L lava flow started: active {map.gameConditionManager.ActiveConditions.Contains(k4bFlow)}, our seeds {string.Join(" ", mine)}, its own edge seeds {own.Count} [{string.Join(" ", own.Take(6).Select(c => K4Cell(c, map)))}]");
                    System.Reflection.MethodInfo pot = AccessTools.Method(typeof(GameCondition_LavaFlow), "CanLavaPotentiallySpreadInto");
                    System.Reflection.MethodInfo can = AccessTools.Method(typeof(GameCondition_LavaFlow), "CanLavaSpreadInto");
                    Info($"L gate probe, CanLavaPotentiallySpreadInto: {K4bGate(map, c => (bool)pot.Invoke(k4bFlow, new object[] { c }))}; CanLavaSpreadInto: {K4bGate(map, c => (bool)can.Invoke(k4bFlow, new object[] { c }))}");
                    K4bStartWatch();
                    TickUntil(120000, () =>
                    {
                        k4bWatch += 250;
                        if (k4bFirstLeak < 0 && K4bLavaAbove(map, out string _) > 0)
                        {
                            k4bFirstLeak = k4bWatch;
                        }
                        return !map.gameConditionManager.ActiveConditions.Contains(k4bFlow);
                    }, "k4b_l_check", 250);
                    return true;
                }
                case "k4b_l_check":
                {
                    int ground = ProjectZ.GroundRect(map).Cells.Count(c => K4bLava(c, map));
                    int north = Enumerable.Range(0, map.Size.x).Count(x => K4bLava(new IntVec3(x, 0, gz - 1), map));
                    int n = K4bLavaAbove(map, out string detail);
                    Info($"L lava flow: after {k4bWatch} ticks (condition {(map.gameConditionManager.ActiveConditions.Contains(k4bFlow) ? "running" : "ended")}): {ground} ground lava cells (fixture {k4bL.Area}), {north} in the ground's north row; first lava above the ground after {(k4bFirstLeak < 0 ? "never" : k4bFirstLeak + " ticks")}");
                    Result("L lava flow reaches the ground's north row (the test sees the edge)", north > 0, $"{north} north-row cells");
                    Result("L lava flow: no lava above the ground (void rows, slabs, rock tops)", n == 0, detail);
                    ShotAt(map, 0, new IntVec3(k4bL.CenterCell.x, 0, gz - 6), 14f);
                    Wait(30, "k4b_l_shot1");
                    return true;
                }
                case "k4b_l_shot1":
                    ZCapture("k4b_l_lavaflow_ground_north");
                    ShotAt(map, 1, new IntVec3(k4bL.CenterCell.x, 0, 4), 14f);
                    Wait(30, "k4b_l_shot2");
                    return true;
                case "k4b_l_shot2":
                {
                    ZCapture("k4b_l_lavaflow_f2_south");
                    if (map.gameConditionManager.ActiveConditions.Contains(k4bFlow))
                    {
                        k4bFlow.End();
                    }
                    Info($"L removed, {K4ClearFlood(map)} temporary-terrain cells cleared");
                    stage = "k4b_e_setup";
                    return true;
                }

                // ================================================================ E: lava emergence
                case "k4b_e_setup":
                {
                    Seeded("k4b_e", () =>
                    {
                        foreach (IntVec3 c in k4bPool)
                        {
                            map.terrainGrid.SetTerrain(c, TerrainDefOf.LavaDeep);
                        }
                    });
                    IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail("LavaEmergence");
                    IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.Misc, map);
                    parms.forced = true;
                    bool fired = false;
                    Seeded("k4b_e_fire", () => fired = def != null && def.Worker.TryExecute(parms));
                    k4bEm = map.listerThings.ThingsOfDef(ThingDefOf.LavaEmergence).OfType<LavaEmergence>().FirstOrDefault();
                    if (k4bEm == null)
                    {
                        Result("E lava emergence spawned", false, $"incident {(def == null ? "missing" : "fired " + fired)}; no LavaEmergence on the map");
                        stage = "k4b_e2_setup";
                        return true;
                    }
                    HashSet<IntVec3> open = Traverse.Create(k4bEm).Field("openCells").GetValue<HashSet<IntVec3>>();
                    int pool = Traverse.Create(k4bEm).Field("poolSize").GetValue<int>();
                    Info($"E lava emergence (incident fired {fired}) at {K4Cell(k4bEm.Position, map)}: pool size {pool}, emergence points [{string.Join(" ", open.Select(c => K4Cell(c, map)))}]");
                    Result("E lava emergence points are ground cells", open.Count > 0 && open.All(c => !ProjectZ.IsAnnex(c, map)), $"{open.Count} points, {open.Count(c => ProjectZ.IsAnnex(c, map))} above the ground");
                    System.Reflection.MethodInfo can = AccessTools.Method(typeof(LavaEmergence), "CanLavaSpreadInto");
                    Info($"E gate probe, LavaEmergence.CanLavaSpreadInto: {K4bGate(map, c => (bool)can.Invoke(k4bEm, new object[] { c }))}");
                    K4bStartWatch();
                    TickUntil(400000, () =>
                    {
                        k4bWatch += 480;
                        if (k4bFirstLeak < 0 && K4bLavaAbove(map, out string _) > 0)
                        {
                            k4bFirstLeak = k4bWatch;
                        }
                        return k4bEm.Destroyed;
                    }, "k4b_e_check", 480);
                    return true;
                }
                case "k4b_e_check":
                {
                    int ground = ProjectZ.GroundRect(map).Cells.Count(c => K4bLava(c, map));
                    int north = Enumerable.Range(0, map.Size.x).Count(x => K4bLava(new IntVec3(x, 0, gz - 1), map));
                    int n = K4bLavaAbove(map, out string detail);
                    Info($"E lava emergence: after {k4bWatch} ticks ({(k4bEm.Destroyed ? "cooling begun, emergence gone" : "still spreading")}): {ground} ground lava cells, {north} in the ground's north row; first lava above the ground after {(k4bFirstLeak < 0 ? "never" : k4bFirstLeak + " ticks")}");
                    Result("E lava emergence reaches the ground's north row (the test sees the edge)", north > 0, $"{north} north-row cells");
                    Result("E lava emergence: no lava above the ground (void rows, slabs, rock tops)", n == 0, detail);
                    ShotAt(map, 0, new IntVec3(k4bPool.CenterCell.x, 0, gz - 6), 14f);
                    Wait(30, "k4b_e_shot1");
                    return true;
                }
                case "k4b_e_shot1":
                    ZCapture("k4b_e_emergence_ground_north");
                    Info($"E removed, {K4ClearFlood(map)} temporary-terrain cells cleared");
                    stage = "k4b_e2_setup";
                    return true;
                case "k4b_e2_setup":
                {
                    // LavaEmergenceImmediate (what a falling lava rock leaves, LavaRockIncoming): spreads 100-150 cells in one tick
                    IntVec3 at = new IntVec3(k4bE.maxX - 6, 0, gz - 1);
                    Seeded("k4b_e2", () => GenSpawn.Spawn(ThingDefOf.LavaEmergenceImmediate, at, map));
                    Info($"E2 lava emergence (immediate) spawned on the ground's north row at {K4Cell(at, map)}");
                    K4bStartWatch();
                    TickUntil(2000, () =>
                    {
                        k4bWatch += 10;
                        if (k4bFirstLeak < 0 && K4bLavaAbove(map, out string _) > 0)
                        {
                            k4bFirstLeak = k4bWatch;
                        }
                        return !map.listerThings.ThingsOfDef(ThingDefOf.LavaEmergenceImmediate).Any() && k4bWatch >= 60;
                    }, "k4b_e2_check", 10);
                    return true;
                }
                case "k4b_e2_check":
                {
                    int ground = ProjectZ.GroundRect(map).Cells.Count(c => K4bLava(c, map));
                    int north = Enumerable.Range(0, map.Size.x).Count(x => K4bLava(new IntVec3(x, 0, gz - 1), map));
                    int n = K4bLavaAbove(map, out string detail);
                    Info($"E2 immediate emergence: after {k4bWatch} ticks: {ground} ground lava cells, {north} in the north row; first lava above the ground after {(k4bFirstLeak < 0 ? "never" : k4bFirstLeak + " ticks")}");
                    Result("E2 immediate lava emergence on the north row: no lava above the ground", n == 0, detail);
                    ShotAt(map, 0, new IntVec3(k4bE.maxX - 6, 0, gz - 4), 12f);
                    Wait(30, "k4b_e2_shot");
                    return true;
                }
                case "k4b_e2_shot":
                    ZCapture("k4b_e2_immediate_ground_north");
                    Info($"E2 removed, {K4ClearFlood(map)} temporary-terrain cells cleared");
                    stage = "k4b_t_setup";
                    return true;

                // ================================================================ T: fleshmass heart
                case "k4b_t_setup":
                {
                    if (!ModsConfig.AnomalyActive)
                    {
                        Result("T fleshmass heart", false, "Anomaly not active");
                        stage = "k4b_finish";
                        return true;
                    }
                    // where the incident itself would put the heart (info)
                    IntVec3 inc = IntVec3.Invalid;
                    Seeded("k4b_t_find", () => LargeBuildingCellFinder.TryFindCell(out inc, map, IncidentWorker_FleshmassHeart.HeartSpawnParms.ForThing(ThingDefOf.FleshmassHeart)));
                    Info($"T the incident's own heart cell finder (LargeBuildingCellFinder) picks {K4Cell(inc, map)}");
                    Seeded("k4b_t", () =>
                    {
                        // the incident's spawner (IncidentWorker_FleshmassHeart.TryExecuteWorker), at our cell
                        BuildingGroundSpawner sp = (BuildingGroundSpawner)ThingMaker.MakeThing(ThingDefOf.FleshmassHeartSpawner);
                        Building_FleshmassHeart heart = sp.ThingToSpawn as Building_FleshmassHeart;
                        heart.Comp.threatPoints = StorytellerUtility.DefaultThreatPointsNow(map);
                        heart.SetFaction(Faction.OfEntities);
                        GenSpawn.Spawn(sp, k4bHeartAt, map);
                        k4bHeart = heart;
                    });
                    Info($"T heart spawner placed at {K4Cell(k4bHeartAt, map)} (heart rect{GenAdj.OccupiedRect(k4bHeartAt, Rot4.North, ThingDefOf.FleshmassHeart.size)})");
                    TickUntil(30000, () => k4bHeart.Spawned, "k4b_t_grow", 60);
                    return true;
                }
                case "k4b_t_grow":
                {
                    if (!k4bHeart.Spawned)
                    {
                        Result("T fleshmass heart emerged", false, $"not spawned after {ticksTotal} ticks");
                        stage = "k4b_finish";
                        return true;
                    }
                    CompGrowsFleshmassTendrils comp = k4bHeart.TryGetComp<CompGrowsFleshmassTendrils>();
                    System.Reflection.MethodInfo can = AccessTools.Method(typeof(CompGrowsFleshmassTendrils), "CanPlaceFleshmass");
                    Info($"T heart emerged at {K4Cell(k4bHeart.Position, map)}, growth points {comp.GrowthPoints}; gate probe, CanPlaceFleshmass: {K4bGate(map, c => (bool)can.Invoke(comp, new object[] { c }))}");
                    K4bStartWatch();
                    TickUntil(120000, () =>
                    {
                        k4bWatch += 250;
                        if (k4bFirstLeak < 0 && K4bFleshAbove(map, out string _) > 0)
                        {
                            k4bFirstLeak = k4bWatch;
                        }
                        return false;
                    }, "k4b_t_check", 250);
                    return true;
                }
                case "k4b_t_check":
                {
                    CompGrowsFleshmassTendrils comp = k4bHeart.TryGetComp<CompGrowsFleshmassTendrils>();
                    int ground = map.listerThings.AllThings.Count(t => t.Spawned && K4bFlesh(t) && !ProjectZ.IsAnnex(t.Position, map));
                    int north = map.listerThings.AllThings.Count(t => t.Spawned && K4bFlesh(t) && t.Position.z == gz - 1);
                    int n = K4bFleshAbove(map, out string detail);
                    Info($"T fleshmass: after {k4bWatch} ticks (growth points left {comp?.GrowthPoints}, contiguous {comp?.ContiguousFleshmass.Count()}): {ground} fleshmass things on the ground, {north} in the ground's north row; first fleshmass above the ground after {(k4bFirstLeak < 0 ? "never" : k4bFirstLeak + " ticks")}");
                    Result("T fleshmass reaches the ground's north row (the test sees the edge)", north > 0, $"{north} north-row cells");
                    Result("T fleshmass: nothing above the ground (void rows, slabs, rock tops)", n == 0, detail);
                    ShotAt(map, 0, new IntVec3(k4bHeartAt.x, 0, gz - 10), 18f);
                    Wait(30, "k4b_t_shot1");
                    return true;
                }
                case "k4b_t_shot1":
                    ZCapture("k4b_t_fleshmass_ground_north");
                    ShotAt(map, 1, new IntVec3(k4bHeartAt.x, 0, 4), 14f);
                    Wait(30, "k4b_t_shot2");
                    return true;
                case "k4b_t_shot2":
                    ZCapture("k4b_t_fleshmass_f2_south");
                    stage = "k4b_finish";
                    return true;
                case "k4b_finish":
                    Info($"slab strip cells left on floor 2: {k4bStrip.Cells.Count(g => K4Up(g, map).GetTerrain(map) == PZDefOf.PZ_UpperFloor)}/{k4bStrip.Area}");
                    if (k4bComps != null)
                    {
                        Find.Storyteller.storytellerComps.AddRange(k4bComps);
                        k4bComps = null;
                    }
                    Wait(2, "k4b_end");
                    return true;
                case "k4b_end":
                    Finish();
                    return true;
            }
            return false;
        }
    }
}
