using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using ProjectZ.Patches;
using RimWorld.Utility;
using Verse;

namespace ProjectZ.Dev
{
    // Mode checks4b (run with -projectzfloors=3 -projectzseed=N, with or without -projectztallmountains). Vanilla
    // spreaders and lump writers started at the ground's north edge, under floors, each in its own fixture area and with
    // its own random state (Seeded); the spreaders run on the game's own tick until they stop or a tick budget. The ground
    // spread rule (GroundSpreadPatches): nothing of theirs ever lands above the ground.
    //   L  Odyssey lava flow (GameCondition_LavaFlow: CanLavaSpreadInto / CanLavaPotentiallySpreadInto) on a volcanic
    //      rock fixture at the north edge;
    //   E  Odyssey lava emergence (the LavaEmergence incident from a deep-lava pool at the north edge, LavaEmergence.
    //      CanLavaSpreadInto), then a LavaEmergenceImmediate (as a falling lava rock makes) on the north row;
    //   V  Anomaly void metal (VoidAwakeningUtility.SpawnVoidMetalAround, a 300-cell lump) centred 3 rows below the edge;
    //   D  deep scanner finds (CompDeepScanner.DoFind): D1 centred on the ground's north row, D2 just inside the no-build
    //      edge margin (ProjectZ's CloseToEdge already keeps the find's cells 10 rows off the north edge);
    //   T  Anomaly fleshmass heart (CompGrowsFleshmassTendrils.CanPlaceFleshmass) emerging near the north edge.
    // For each spreader: nothing above the ground (void rows, slabs, rock tops), the spreader reached the ground's north
    // row, its check over every annex cell accepts none, the same terrain is accepted on a ground cell and refused on a
    // band and a void cell (vanilla's own check accepts all three), and the hook ran during the real spread (not inlined).
    // For each lump: nothing above the ground, the raw lump did reach above it, and the ground part is vanilla's lump
    // clipped to the ground.
    public static partial class TestRunner
    {
        private static CellRect k4bL, k4bE, k4bPool, k4bStrip;
        private static IntVec3 k4bHeartAt;
        private static GameCondition_LavaFlow k4bFlow;
        private static LavaEmergence k4bEm;
        private static Thing k4bHeart;
        private static int k4bFirstLeak, k4bWatch;
        private static List<StorytellerComp> k4bComps;
        private static List<IntVec3> k4bRawLump;
        private static IntVec3 k4bForceCenter = IntVec3.Invalid;
        private static int k4bCalls;

        internal static void K4bPatch(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(GridShapeMaker), nameof(GridShapeMaker.IrregularLump)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(K4bLumpPostfix)));
            h.Patch(AccessTools.Method(typeof(CellFinderLoose), nameof(CellFinderLoose.TryFindRandomNotEdgeCellWith)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(K4bCenterPrefix)));
        }

        // The raw lump (vanilla's, before the ground rule drops its cells above the ground).
        private static void K4bLumpPostfix(List<IntVec3> __result)
        {
            k4bRawLump = __result?.ToList();
        }

        // The deep scanner's centre finder, held at the test's cell (no random draw).
        private static bool K4bCenterPrefix(ref IntVec3 result, ref bool __result)
        {
            if (!k4bForceCenter.IsValid)
            {
                return true;
            }
            result = k4bForceCenter;
            __result = true;
            return false;
        }

        // The hooked check on the same terrain at a ground cell, a band cell and a void cell (written straight into the
        // terrain grid, no change effects, restored right after), with the ground rule on and off: on, it accepts the
        // ground cell and refuses the other two; off (vanilla's own check) it accepts all three, so the refusal is the rule's.
        internal static void GsGate(Map map, string name, TerrainDef terrain, IntVec3 g, IntVec3 b, IntVec3 v, Func<IntVec3, bool> accepts)
        {
            TerrainDef[] top = map.terrainGrid.topGrid;
            IntVec3[] cells = { g, b, v };
            TerrainDef[] old = cells.Select(c => top[map.cellIndices.CellToIndex(c)]).ToArray();
            bool[] on = new bool[3], off = new bool[3];
            bool was = GroundSpread.Off;
            string err = "";
            try
            {
                foreach (IntVec3 c in cells)
                {
                    top[map.cellIndices.CellToIndex(c)] = terrain;
                }
                for (int i = 0; i < 3; i++)
                {
                    on[i] = accepts(cells[i]);
                }
                GroundSpread.Off = true;
                for (int i = 0; i < 3; i++)
                {
                    off[i] = accepts(cells[i]);
                }
            }
            catch (Exception e)
            {
                err = " EXCEPTION " + (e.InnerException ?? e).Message;
            }
            finally
            {
                GroundSpread.Off = was;
                for (int i = 0; i < 3; i++)
                {
                    top[map.cellIndices.CellToIndex(cells[i])] = old[i];
                }
            }
            string d = $"{terrain.defName} on ground {K4Cell(g, map)}, band {K4Cell(b, map)}, void {K4Cell(v, map)}: rule on {on[0]}/{on[1]}/{on[2]}, vanilla {off[0]}/{off[1]}/{off[2]}{err}";
            Result($"{name}: accepts the ground cell, refuses the same terrain on a band and a void cell", err == "" && on[0] && !on[1] && !on[2], d);
            Result($"{name}: vanilla's own check accepts all three (the refusal is the ground rule's)", err == "" && off[0] && off[1] && off[2], d);
        }

        // The hook ran during the real spread (a check inlined into its caller would never reach the hook).
        internal static void GsRan(string name, int calls)
        {
            Result($"{name}: the ground hook ran during the real spread (not inlined)", calls > 0, $"{calls} calls");
        }

        // The ground cells of vanilla's raw lump (in bounds).
        private static HashSet<IntVec3> K4bClipped(Map map, IEnumerable<IntVec3> raw) => new HashSet<IntVec3>(raw.Where(c => c.InBounds(map) && !ProjectZ.IsAnnex(c, map)));

        private static Dictionary<IntVec3, string> K4bDeep(Map map)
        {
            Dictionary<IntVec3, string> d = new Dictionary<IntVec3, string>();
            foreach (IntVec3 c in map.AllCells)
            {
                ThingDef t = map.deepResourceGrid.ThingDefAt(c);
                if (t != null)
                {
                    d[c] = t.defName + " " + map.deepResourceGrid.CountAt(c);
                }
            }
            return d;
        }

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
        private static string K4bGate(Map map, Func<IntVec3, bool> accepts) => K4bGate(map, accepts, out int _);

        private static string K4bGate(Map map, Func<IntVec3, bool> accepts, out int yesAll)
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
            yesAll = voidYes + bandYes;
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
                    string gp = K4bGate(map, c => (bool)pot.Invoke(k4bFlow, new object[] { c }), out int yp);
                    string gc = K4bGate(map, c => (bool)can.Invoke(k4bFlow, new object[] { c }), out int yc);
                    Result("L the lava flow's checks accept no cell above the ground", yp == 0 && yc == 0, $"CanLavaPotentiallySpreadInto: {gp}; CanLavaSpreadInto: {gc}");
                    IntVec3 lg = new IntVec3(50, 0, gz - 3);
                    GsGate(map, "L CanLavaPotentiallySpreadInto", TerrainDefOf.VolcanicRock, lg, K4Up(lg, map), new IntVec3(50, 0, gz + 1), c => (bool)pot.Invoke(k4bFlow, new object[] { c }));
                    GsGate(map, "L CanLavaSpreadInto", TerrainDefOf.VolcanicRock, lg, K4Up(lg, map), new IntVec3(50, 0, gz + 1), c => (bool)can.Invoke(k4bFlow, new object[] { c }));
                    k4bCalls = GroundSpread.LavaFlowCalls;
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
                    GsRan("L lava flow", GroundSpread.LavaFlowCalls - k4bCalls);
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
                    string ge = K4bGate(map, c => (bool)can.Invoke(k4bEm, new object[] { c }), out int ye);
                    Result("E LavaEmergence.CanLavaSpreadInto accepts no cell above the ground", ye == 0, ge);
                    IntVec3 eg = new IntVec3(50, 0, gz - 3);
                    GsGate(map, "E LavaEmergence.CanLavaSpreadInto", DefDatabase<TerrainDef>.GetNamed("Soil"), eg, K4Up(eg, map), new IntVec3(50, 0, gz + 1), c => (bool)can.Invoke(k4bEm, new object[] { c }));
                    k4bCalls = GroundSpread.LavaEmergenceCalls;
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
                    GsRan("E lava emergence", GroundSpread.LavaEmergenceCalls - k4bCalls);
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
                    k4bCalls = GroundSpread.LavaEmergenceCalls;
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
                    GsRan("E2 immediate lava emergence", GroundSpread.LavaEmergenceCalls - k4bCalls);
                    ShotAt(map, 0, new IntVec3(k4bE.maxX - 6, 0, gz - 4), 12f);
                    Wait(30, "k4b_e2_shot");
                    return true;
                }
                case "k4b_e2_shot":
                    ZCapture("k4b_e2_immediate_ground_north");
                    Info($"E2 removed, {K4ClearFlood(map)} temporary-terrain cells cleared");
                    stage = "k4b_v_setup";
                    return true;

                // ================================================================ V: void metal lump
                case "k4b_v_setup":
                {
                    if (!ModsConfig.AnomalyActive)
                    {
                        Result("V void metal", false, "Anomaly not active");
                        stage = "k4b_d_setup";
                        return true;
                    }
                    IntVec3 at = new IntVec3(28, 0, gz - 3);
                    HashSet<IntVec3> before = new HashSet<IntVec3>(map.AllCells.Where(c => map.terrainGrid.TopTerrainAt(c) == TerrainDefOf.Voidmetal));
                    HashSet<Thing> massesBefore = new HashSet<Thing>(map.listerThings.ThingsOfDef(ThingDefOf.VoidmetalMassSmall));
                    k4bRawLump = null;
                    int calls = GroundSpread.LumpCalls;
                    Seeded("k4b_v", () =>
                    {
                        // the anchor (the monolith in the quest part): a void metal mass at the centre
                        Thing anchor = GenSpawn.Spawn(ThingDefOf.VoidmetalMassSmall, at, map);
                        massesBefore.Add(anchor);
                        k4bRawLump = null;
                        VoidAwakeningUtility.SpawnVoidMetalAround(anchor, 300, 4);
                    });
                    List<IntVec3> raw = k4bRawLump ?? new List<IntVec3>();
                    HashSet<IntVec3> written = new HashSet<IntVec3>(map.AllCells.Where(c => map.terrainGrid.TopTerrainAt(c) == TerrainDefOf.Voidmetal && !before.Contains(c)));
                    HashSet<IntVec3> clipped = K4bClipped(map, raw);
                    List<Thing> masses = map.listerThings.ThingsOfDef(ThingDefOf.VoidmetalMassSmall).Where(t => !massesBefore.Contains(t)).ToList();
                    int up = written.Count(c => ProjectZ.IsAnnex(c, map));
                    int massesUp = masses.Count(t => ProjectZ.IsAnnex(t.Position, map));
                    int rawUp = raw.Count(c => c.InBounds(map) && ProjectZ.IsAnnex(c, map));
                    Info($"V void metal lump at {K4Cell(at, map)}: vanilla's raw lump {raw.Count} cells ({rawUp} above the ground: {raw.Count(c => c.InBounds(map) && ProjectZ.IsVoid(c, map))} void, northmost row {(raw.Count > 0 ? raw.Max(c => c.z) : -1)}), clipped to the ground {clipped.Count}; void metal written on {written.Count} cells, {masses.Count} new masses");
                    Result("V the lump writer went through the ground lump (transpiler took)", GroundSpread.LumpCalls - calls == 1, $"{GroundSpread.LumpCalls - calls} ground lump calls");
                    Result("V vanilla's raw lump reaches above the ground (the test sees the edge)", rawUp > 0, $"{rawUp} raw cells above the ground");
                    Result("V void metal: nothing above the ground (void rows, slabs, rock tops)", up == 0 && massesUp == 0, $"{up} void metal cells and {massesUp} masses above the ground");
                    Result("V the ground part is vanilla's lump clipped to the ground (same cells)", written.SetEquals(clipped), $"written {written.Count}, clipped {clipped.Count}, missing {clipped.Count(c => !written.Contains(c))}, extra {written.Count(c => !clipped.Contains(c))}");
                    ShotAt(map, 0, new IntVec3(at.x, 0, gz - 6), 14f);
                    Wait(30, "k4b_v_shot1");
                    return true;
                }
                case "k4b_v_shot1":
                    ZCapture("k4b_v_voidmetal_ground_north");
                    ShotAt(map, 1, new IntVec3(28, 0, 4), 14f);
                    Wait(30, "k4b_v_shot2");
                    return true;
                case "k4b_v_shot2":
                    ZCapture("k4b_v_voidmetal_f2_south");
                    stage = "k4b_d_setup";
                    return true;

                // ================================================================ D: deep scanner find
                case "k4b_d_setup":
                {
                    IntVec3 at = new IntVec3(100, 0, gz - 1);
                    ThingDef scannerDef = DefDatabase<ThingDef>.GetNamedSilentFail("GroundPenetratingScanner");
                    // (the worker only names the finder in the letter: any of the player's people)
                    Pawn worker = map.mapPawns.PawnsInFaction(Faction.OfPlayer).Where(p => p.RaceProps.Humanlike).OrderBy(p => p.thingIDNumber).FirstOrDefault();
                    Info($"D colonists: free spawned {Colonists(map).Count}, player humanlike pawns {map.mapPawns.PawnsInFaction(Faction.OfPlayer).Count(p => p.RaceProps.Humanlike)} [{string.Join(" ", map.mapPawns.PawnsInFaction(Faction.OfPlayer).Where(p => p.RaceProps.Humanlike).Select(p => p.LabelShort + (p.Dead ? " dead" : p.Downed ? " downed" : "") + (p.Spawned ? "@" + p.Position : " unspawned")))}]");
                    if (worker == null)
                    {
                        // (the colonists can be gone by now on some seeds; an unspawned one is enough for the letter)
                        worker = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                    }
                    if (scannerDef == null)
                    {
                        Result("D deep scanner", false, "no GroundPenetratingScanner def");
                        stage = "k4b_t_setup";
                        return true;
                    }
                    Thing scanner = GenSpawn.Spawn(ThingMaker.MakeThing(scannerDef), new IntVec3(100, 0, 1), map);
                    CompDeepScanner comp = scanner.TryGetComp<CompDeepScanner>();
                    System.Reflection.MethodInfo scatter = AccessTools.Method(typeof(CompDeepScanner), "CanScatterAt");
                    System.Reflection.MethodInfo doFind = AccessTools.Method(typeof(CompDeepScanner), "DoFind");
                    // a find whose lump is big enough to cross the edge: the first seed name whose draws (lump kind, then
                    // size, as DoFind draws them) give 15+ cells
                    string seedName = null;
                    ThingDef kind = null;
                    int size = 0;
                    for (int i = 0; i < 40 && seedName == null; i++)
                    {
                        string nm = "k4b_d" + i;
                        Seeded(nm, () =>
                        {
                            kind = DefDatabase<ThingDef>.AllDefs.RandomElementByWeight(d => d.deepCommonality);
                            size = Mathf.CeilToInt(kind.deepLumpSizeRange.RandomInRange);
                        });
                        if (size >= 15)
                        {
                            seedName = nm;
                        }
                    }
                    // D1 centred on the north row (its raw lump crosses the edge); D2 just inside the no-build edge margin, where
                    // ProjectZ's CloseToEdge lets the find write on the ground (its ground part is checked against vanilla's lump).
                    foreach ((string label, IntVec3 c0) in new[] { ("D1", at), ("D2", new IntVec3(130, 0, gz - 11)) })
                    {
                        at = c0;
                        // the find's own cell rule before the find (CanScatterAt and not in the no-build edge area)
                        HashSet<IntVec3> eligible = new HashSet<IntVec3>(CellRect.CenteredOn(at, 12).ClipInsideMap(map).Cells.Where(c => (bool)scatter.Invoke(comp, new object[] { c, map }) && !c.InNoBuildEdgeArea(map)));
                        Dictionary<IntVec3, string> before = K4bDeep(map);
                        int calls = GroundSpread.LumpCalls;
                        k4bRawLump = null;
                        k4bForceCenter = at;
                        try
                        {
                            Seeded(seedName ?? "k4b_d0", () => doFind.Invoke(comp, new object[] { worker }));
                        }
                        finally
                        {
                            k4bForceCenter = IntVec3.Invalid;
                        }
                        Dictionary<IntVec3, string> after = K4bDeep(map);
                        HashSet<IntVec3> written = new HashSet<IntVec3>(after.Where(kv => !before.TryGetValue(kv.Key, out string o) || o != kv.Value).Select(kv => kv.Key));
                        List<IntVec3> raw = k4bRawLump ?? new List<IntVec3>();
                        HashSet<IntVec3> expect = new HashSet<IntVec3>(K4bClipped(map, raw).Where(c => eligible.Contains(c)));
                        int rawUp = raw.Count(c => c.InBounds(map) && ProjectZ.IsAnnex(c, map));
                        int rawUpOk = raw.Count(c => c.InBounds(map) && ProjectZ.IsAnnex(c, map) && eligible.Contains(c));
                        int up = written.Count(c => ProjectZ.IsAnnex(c, map));
                        Info($"{label} deep scanner find (seed {seedName}, {kind?.defName} lump of {size}) centred on {K4Cell(at, map)}: raw lump {raw.Count} cells, {rawUp} above the ground ({rawUpOk} of them pass vanilla's own cell rule), ground cells passing the rule {expect.Count}; deep ore written on {written.Count} cells [{string.Join(" ", written.Take(6).Select(c => K4Cell(c, map) + " " + after[c]))}]");
                        Result($"{label} the find went through the ground lump (transpiler took)", GroundSpread.LumpCalls - calls == 1, $"{GroundSpread.LumpCalls - calls} ground lump calls");
                        if (label == "D1")
                        {
                            Result("D1 vanilla's raw lump reaches above the ground (the test sees the edge)", rawUp > 0, $"{rawUp} raw cells above the ground, {rawUpOk} of them pass vanilla's own cell rule (ProjectZ's no-build edge margin)");
                        }
                        Result($"{label} deep scanner: no deep ore above the ground", up == 0, $"{up} cells above the ground");
                        Result($"{label} the ground part is vanilla's lump clipped to the ground and to its own cell rule (same cells)", written.SetEquals(expect) && (label == "D1" || written.Count > 0), $"written {written.Count}, expected {expect.Count}, missing {expect.Count(c => !written.Contains(c))}, extra {written.Count(c => !expect.Contains(c))}");
                    }
                    scanner.Destroy();
                    stage = "k4b_t_setup";
                    return true;
                }

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
                    string gt = K4bGate(map, c => (bool)can.Invoke(comp, new object[] { c }), out int yt);
                    Info($"T heart emerged at {K4Cell(k4bHeart.Position, map)}, growth points {comp.GrowthPoints}");
                    Result("T CanPlaceFleshmass accepts no cell above the ground", yt == 0, gt);
                    IntVec3 tg = new IntVec3(100, 0, gz - 10);
                    GsGate(map, "T CanPlaceFleshmass", DefDatabase<TerrainDef>.GetNamed("Soil"), tg, K4Up(tg, map), new IntVec3(100, 0, gz + 1), c => (bool)can.Invoke(comp, new object[] { c }));
                    k4bCalls = GroundSpread.FleshmassCalls;
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
                    GsRan("T fleshmass", GroundSpread.FleshmassCalls - k4bCalls);
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
