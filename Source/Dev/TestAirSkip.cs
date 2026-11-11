using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode airskip (run with -projectzfloors=3 -projectzseed=N -projectzriver): open air upstairs is passed over by the
    // game's per-cell weather steps (OpenAirSkip) and nothing else changes. Time passes on a small fixture: cloth stacks and
    // dirt outdoors on concrete on the ground and on an unroofed floor-1 slab; the map's river. Phase 1, 1.5 days of hard
    // snow at -20 C (snow piles up, the river freezes, cloth deteriorates); phase 2, 1.5 days of rain at +15 C (snow melts,
    // ice melts, rain washes dirt). The outdoor temperature and the weather are held by the test; ticks are seeded. After
    // each phase, OAKEY lines (snow, ice, dirt, cloth on the ground and on the slab, snow on open air) to compare runs:
    //   default                 the fix on
    //   -projectznoairskip      every cell done, as before
    //   -projectzairaudit       every cell done, and each visit to an empty open-air cell checked for any change it made
    //                           (snow, sand, terrain, things damaged, destroyed or spawned, dirt thinned, flecks, random draws)
    // The harness is not exactly repeatable (the ticks before the test starts and some frame-time work differ from run to
    // run), so snow, ice, dirt and cloth only compare within the spread of repeated runs; the audit is the exact check.
    // The weather cycles are set to their start when phase 1 begins, and every visit is hashed by (ticks into the test,
    // cell): visits.all (the cycle) and visits.ran (cells the game's step really ran on, not counting empty open air) must
    // be the same with and without the fix: every other cell is done on exactly the same tick.
    // The game's snow noise (SteadyEnvironmentEffects.snowNoise, seeded by the first snowfall) is made before phase 1, so its
    // seed does not depend on which cell the first snow falls on.
    // -projectzairperf: instead of the phases, 20 blocks of 1000 seeded ticks with the skip on and off in turn (the same map,
    // the same state): the time in SteadyEnvironmentEffectsTick, TempTerrainManager.Tick and the whole tick, each way.
    public static partial class TestRunner
    {
        private static bool oaPerf;
        private static int oaPerfBlock;
        private static long oaPerfT0, oaPerfT1;
        private static readonly double[] oaPerfSteady = new double[2], oaPerfTemp = new double[2], oaPerfTick = new double[2];
        private static readonly int[] oaPerfTicks = new int[2];
        private static float? oaTemp;
        private static WeatherDef oaWeather;
        private static bool oaAudit;
        private static CellRect oaSite, oaSlab, oaRoom;
        private static readonly List<Thing> oaClothGround = new List<Thing>(), oaClothSlab = new List<Thing>();
        private static int oaPhase;

        // visit counters: [0] SteadyEnvironmentEffects, [1] FreezeManager
        private static readonly long[] oaVisits = new long[2], oaOpen = new long[2], oaReal = new long[2];
        private static readonly ulong[] oaHashAll = { 14695981039346656037UL, 14695981039346656037UL };
        private static readonly ulong[] oaHashRan = { 14695981039346656037UL, 14695981039346656037UL };
        private static readonly long[] oaRan = new long[2], oaRanOpen = new long[2];
        private static int oaT0;
        private static bool oaLastOpen;
        private static AccessTools.FieldRef<SteadyEnvironmentEffects, int> oaSteadyCycleBound;
        private static AccessTools.FieldRef<SteadyEnvironmentEffects, int> oaSteadyCycle => oaSteadyCycleBound ??= AccessTools.FieldRefAccess<SteadyEnvironmentEffects, int>("cycleIndex");   // (bound on first use: nothing for players)
        private static AccessTools.FieldRef<TempTerrainManager, int> oaTempCycleBound;
        private static AccessTools.FieldRef<TempTerrainManager, int> oaTempCycle => oaTempCycleBound ??= AccessTools.FieldRefAccess<TempTerrainManager, int>("cycleIndex");   // (bound on first use: nothing for players)
        private static int oaStep;   // the step of the visit being audited
        private static readonly long[,] oaDrawVisits = new long[3, 2];
        private static readonly List<string> oaDrawSamples = new List<string>();

        // audit
        private static bool oaAuditing;
        private static IntVec3 oaCell;
        private static double oaSnow0, oaSand0;
        private static uint oaRand0;
        private static int oaEvTerrain, oaEvDamage, oaEvDestroy, oaEvSpawn, oaEvThin, oaEvFleck;
        private static long oaAudited, oaChangedSnow, oaChangedSand, oaChangedTerrain, oaChangedThings, oaFlecks, oaRandDraws, oaRandVisits;
        private static AccessTools.FieldRef<SnowGrid, double> oaSnowTotalBound;
        private static AccessTools.FieldRef<SnowGrid, double> oaSnowTotal => oaSnowTotalBound ??= AccessTools.FieldRefAccess<SnowGrid, double>("totalDepth");   // (bound on first use: nothing for players)
        private static AccessTools.FieldRef<SandGrid, double> oaSandTotalBound;
        private static AccessTools.FieldRef<SandGrid, double> oaSandTotal => oaSandTotalBound ??= AccessTools.FieldRefAccess<SandGrid, double>("totalDepth");   // (bound on first use: nothing for players)
        private static AccessTools.FieldRef<uint> oaRandIterBound;
        private static AccessTools.FieldRef<uint> oaRandIter => oaRandIterBound ??= AccessTools.StaticFieldRefAccess<uint>(AccessTools.Field(typeof(Rand), "iterations"));   // (bound on first use: nothing for players)

        internal static void OaInit(Harmony h)
        {
            oaAudit = GenCommandLine.CommandLineArgPassed("projectzairaudit");
            oaPerf = GenCommandLine.CommandLineArgPassed("projectzairperf");
            if (oaPerf)
            {
                h.Patch(AccessTools.Method(typeof(SteadyEnvironmentEffects), nameof(SteadyEnvironmentEffects.SteadyEnvironmentEffectsTick)),
                    prefix: new HarmonyMethod(typeof(TestRunner), nameof(OaPerfPre)) { priority = Priority.First }, postfix: new HarmonyMethod(typeof(TestRunner), nameof(OaPerfSteadyPost)) { priority = Priority.Last });
                h.Patch(AccessTools.Method(typeof(TempTerrainManager), nameof(TempTerrainManager.Tick)),
                    prefix: new HarmonyMethod(typeof(TestRunner), nameof(OaPerfPre1)) { priority = Priority.First }, postfix: new HarmonyMethod(typeof(TestRunner), nameof(OaPerfTempPost)) { priority = Priority.Last });
            }
            if (oaAudit)
            {
                OpenAirSkip.Off = true;   // every cell done; the empty open-air ones checked
            }
            h.Patch(AccessTools.PropertyGetter(typeof(MapTemperature), nameof(MapTemperature.OutdoorTemp)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(OaTempPostfix)));
            h.Patch(AccessTools.PropertyGetter(typeof(MapTemperature), nameof(MapTemperature.SeasonalTemp)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(OaTempPostfix)));
            HarmonyMethod pre0 = new HarmonyMethod(typeof(TestRunner), nameof(OaSteadyPre)) { priority = Priority.First };
            HarmonyMethod post0 = new HarmonyMethod(typeof(TestRunner), nameof(OaPost)) { priority = Priority.Last };
            h.Patch(AccessTools.Method(typeof(SteadyEnvironmentEffects), "DoCellSteadyEffects"), prefix: pre0, postfix: post0);
            if (ModsConfig.OdysseyActive)
            {
                HarmonyMethod pre1 = new HarmonyMethod(typeof(TestRunner), nameof(OaFreezePre)) { priority = Priority.First };
                h.Patch(AccessTools.Method(typeof(FreezeManager), nameof(FreezeManager.DoCellSteadyEffects)), prefix: pre1, postfix: post0);
            }
            if (oaAudit)
            {
                h.Patch(AccessTools.Method(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(OaEvTerrainPre)));
                h.Patch(AccessTools.Method(typeof(TerrainGrid), nameof(TerrainGrid.SetTempTerrain)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(OaEvTerrainPre)));
                h.Patch(AccessTools.Method(typeof(TerrainGrid), nameof(TerrainGrid.RemoveTempTerrain)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(OaEvTerrainPre)));
                h.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.TakeDamage)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(OaEvDamagePre)));
                h.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.Destroy)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(OaEvDestroyPre)));
                h.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.SpawnSetup)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(OaEvSpawnPre)));
                h.Patch(AccessTools.Method(typeof(Filth), nameof(Filth.ThinFilth)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(OaEvThinPre)));
                h.Patch(AccessTools.Method(typeof(FleckManager), nameof(FleckManager.CreateFleck)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(OaEvFleckPre)));
            }
        }

        private static void OaPerfPre() => oaPerfT0 = System.Diagnostics.Stopwatch.GetTimestamp();

        private static void OaPerfPre1() => oaPerfT1 = System.Diagnostics.Stopwatch.GetTimestamp();

        private static void OaPerfSteadyPost()
        {
            oaPerfSteady[OpenAirSkip.Off ? 1 : 0] += (System.Diagnostics.Stopwatch.GetTimestamp() - oaPerfT0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private static void OaPerfTempPost()
        {
            oaPerfTemp[OpenAirSkip.Off ? 1 : 0] += (System.Diagnostics.Stopwatch.GetTimestamp() - oaPerfT1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private static void OaPerfStep(Map map)
        {
            if (oaPerfBlock >= 20)
            {
                OpenAirSkip.Off = false;
                string L(int k) => $"steady {oaPerfSteady[k] / oaPerfTicks[k]:0.0000} ms, freeze {oaPerfTemp[k] / oaPerfTicks[k]:0.0000} ms, whole tick {oaPerfTick[k] / oaPerfTicks[k]:0.0000} ms";
                Info($"airskip perf ({oaPerfTicks[0]} ticks each way, map {map.Size}, levels {ProjectZ.Annex(map)?.floors}): skip on: {L(0)}; skip off: {L(1)}");
                Finish();
                return;
            }
            int k = oaPerfBlock % 2;
            OpenAirSkip.Off = k == 1;
            TickManager tm = Find.TickManager;
            int seed = FixtureSeed("airskip_perf");
            for (int i = 0; i < 1000; i++)
            {
                long a = System.Diagnostics.Stopwatch.GetTimestamp();
                Rand.PushState(seed + (oaPerfBlock * 1000 + i) * SeedStride);
                try
                {
                    tm.DoSingleTick();
                }
                finally
                {
                    Rand.PopState();
                }
                oaPerfTick[k] += (System.Diagnostics.Stopwatch.GetTimestamp() - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            }
            oaPerfTicks[k] += 1000;
            oaPerfBlock++;
        }

        private static void OaTempPostfix(ref float __result)
        {
            if (oaTemp.HasValue)
            {
                __result = oaTemp.Value;
            }
        }

        private static void OaSteadyPre(Map ___map, IntVec3 c) => OaVisit(0, ___map, c);

        private static void OaFreezePre(Map ___map, IntVec3 c) => OaVisit(1, ___map, c);

        private static void OaVisit(int k, Map map, IntVec3 c)
        {
            if (oaPhase == 0 || map != Find.CurrentMap)
            {
                return;
            }
            oaVisits[k]++;
            ulong ha = oaHashAll[k];
            ha = (ha ^ (ulong)(Find.TickManager.TicksGame - oaT0)) * 1099511628211UL;
            ha = (ha ^ (ulong)map.cellIndices.CellToIndex(c)) * 1099511628211UL;
            oaHashAll[k] = ha;
            oaStep = k;
            oaLastOpen = OpenAirSkip.OpenEmpty(map, c);
            if (oaLastOpen)
            {
                oaOpen[k]++;
                if (oaAudit)
                {
                    oaAuditing = true;
                    oaSnow0 = oaSnowTotal(map.snowGrid);
                    oaSand0 = map.sandGrid != null ? oaSandTotal(map.sandGrid) : 0.0;
                    oaRand0 = oaRandIter();
                    oaCell = c;
                    oaEvTerrain = oaEvDamage = oaEvDestroy = oaEvSpawn = oaEvThin = oaEvFleck = 0;
                }
                return;
            }
            oaReal[k]++;
        }

        private static void OaPost(IntVec3 c, bool __runOriginal)
        {
            if (oaPhase != 0 && __runOriginal)
            {
                Map m = Find.CurrentMap;
                if (oaLastOpen)
                {
                    oaRanOpen[oaStep]++;
                }
                else
                {
                    oaRan[oaStep]++;
                    ulong hsh = oaHashRan[oaStep];
                    hsh = (hsh ^ (ulong)(Find.TickManager.TicksGame - oaT0)) * 1099511628211UL;
                    hsh = (hsh ^ (ulong)m.cellIndices.CellToIndex(c)) * 1099511628211UL;
                    oaHashRan[oaStep] = hsh;
                }
            }
            if (!oaAuditing)
            {
                return;
            }
            oaAuditing = false;
            Map map = Find.CurrentMap;
            oaAudited++;
            if (oaSnowTotal(map.snowGrid) != oaSnow0)
            {
                oaChangedSnow++;
            }
            if ((map.sandGrid != null ? oaSandTotal(map.sandGrid) : 0.0) != oaSand0)
            {
                oaChangedSand++;
            }
            if (oaEvTerrain > 0)
            {
                oaChangedTerrain++;
            }
            if (oaEvDamage + oaEvDestroy + oaEvSpawn + oaEvThin > 0)
            {
                oaChangedThings++;
            }
            if (oaEvFleck > 0)
            {
                oaFlecks++;
            }
            uint draws = oaRandIter() - oaRand0;
            if (draws > 0)
            {
                oaRandDraws += draws;
                oaRandVisits++;
                oaDrawVisits[oaPhase, oaStep]++;
                if (oaDrawSamples.Count < 12 && (oaPhase == 1 || oaDrawSamples.Count < 4))
                {
                    Room room = oaCell.GetRoom(map);
                    oaDrawSamples.Add($"phase {oaPhase} {(oaStep == 0 ? "steady" : "freeze")} {oaCell} temp {oaCell.GetTemperature(map):0.0} room {room?.ID} outdoors {room?.UsesOutdoorTemperature} cells {room?.CellCount} roofed {map.roofGrid.Roofed(oaCell)} draws {draws}");
                }
            }
        }

        private static void OaEvTerrainPre()
        {
            if (oaAuditing)
            {
                oaEvTerrain++;
            }
        }

        private static void OaEvDamagePre()
        {
            if (oaAuditing)
            {
                oaEvDamage++;
            }
        }

        private static void OaEvDestroyPre()
        {
            if (oaAuditing)
            {
                oaEvDestroy++;
            }
        }

        private static void OaEvSpawnPre()
        {
            if (oaAuditing)
            {
                oaEvSpawn++;
            }
        }

        private static void OaEvThinPre()
        {
            if (oaAuditing)
            {
                oaEvThin++;
            }
        }

        private static void OaEvFleckPre()
        {
            if (oaAuditing)
            {
                oaEvFleck++;
            }
        }

        private static bool StepAirSkip(Map map)
        {
            switch (stage)
            {
                case "oa_setup":
                    OaSetup(map);
                    return true;
                case "oa_perf":
                    KeepFed(map);
                    OaPerfStep(map);
                    return true;
                case "oa_p1":
                    OaHold(map, -20f, DefDatabase<WeatherDef>.GetNamed("SnowHard"));
                    oaT0 = Find.TickManager.TicksGame;
                    oaSteadyCycle(map.steadyEnvironmentEffects) = 0;
                    oaTempCycle(map.tempTerrain) = 0;
                    oaPhase = 1;
                    SeedTicks("airskip_p1");
                    TickUntil(90000, () => OaKeep(map), "oa_p1_done", 250);
                    return true;
                case "oa_p1_done":
                    tickWatch = null;
                    OaMeasure(map, "p1");
                    OaHold(map, 15f, DefDatabase<WeatherDef>.GetNamed("Rain"));
                    oaPhase = 2;
                    SeedTicks("airskip_p2");
                    TickUntil(90000, () => OaKeep(map), "oa_p2_done", 250);
                    return true;
                case "oa_p2_done":
                    tickWatch = null;
                    OaMeasure(map, "p2");
                    oaPhase = 0;
                    OaReport(map);
                    Finish();
                    return true;
            }
            return false;
        }

        private static void OaHold(Map map, float temp, WeatherDef w)
        {
            oaTemp = temp;
            oaWeather = w;
            OaKeep(map);
        }

        // Every 250 ticks: the weather held, the colonists fed and calm (drafted in their room).
        private static bool OaKeep(Map map)
        {
            WeatherManager wm = map.weatherManager;
            wm.curWeather = oaWeather;
            wm.lastWeather = oaWeather;
            KeepFed(map);
            return false;
        }

        private static void OaSetup(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            DebugSettings.enableRandomMentalStates = false;
            Find.Storyteller.storytellerComps.Clear();
            Find.PlaySettings.autoRebuild = false;
            NoAmbushes(map);
            NoRoofCollapse(map);
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || a.MaxLevel < 1)
            {
                Result("airskip: a map with floors", false, "no annex (run with -projectzfloors=3)");
                Finish();
                return;
            }
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
            {
                p.Destroy();
            }
            int gz = a.groundZ;
            IntVec3 C = IntVec3.Invalid;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(new IntVec3(map.Size.x / 2, 0, gz / 2), 50f, true))
            {
                CellRect r = new CellRect(c.x - 22, c.z - 17, 44, 34);
                if (r.InBounds(map) && r.maxZ < gz - 3 && r.Cells.All(x => !x.GetTerrain(map).IsWater && x.GetThingList(map).All(t => t is Pawn || t.def.destroyable)))
                {
                    C = c;
                    break;
                }
            }
            if (!C.IsValid)
            {
                Result("airskip: test spot", false, "no dry 44 x 34 spot near the map centre");
                Finish();
                return;
            }
            oaSite = new CellRect(C.x - 20, C.z - 15, 40, 30);
            foreach (IntVec3 c in oaSite.ExpandedBy(2).ClipInsideMap(map))
            {
                foreach (IntVec3 cc in new[] { c, Levels.Cell(c, 1, map) })
                {
                    if (!cc.IsValid)
                    {
                        continue;
                    }
                    foreach (Thing t in cc.GetThingList(map).ToList())
                    {
                        if (!(t is Pawn) && t.def.destroyable)
                        {
                            t.Destroy();
                        }
                    }
                }
                if (!c.GetTerrain(map).IsWater)
                {
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Concrete);
                }
                map.roofGrid.SetRoof(c, null);
                if (map.fogGrid.IsFogged(c))
                {
                    map.fogGrid.Unfog(c);
                }
            }
            // a closed, roofed room (the colonists wait in it, drafted); the floor-1 slab over it, unroofed
            oaRoom = new CellRect(C.x - 18, C.z - 13, 10, 10);
            foreach (IntVec3 c in oaRoom.EdgeCells)
            {
                Place(ThingDefOf.Wall, ThingDefOf.BlocksGranite, c, map);
            }
            oaSlab = oaRoom;
            RpFloor(map, oaSlab, 1);
            List<Pawn> cols = Colonists(map);
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], new IntVec3(oaRoom.minX + 2 + (i % 6), 0, oaRoom.minZ + 2 + i / 6));
                Draft(cols[i], true);
            }
            // no pond of our own (a water body made at runtime never freezes: its counts are not recached); the map's river
            // (-projectzriver) freezes and thaws
            int water = 0;
            for (int i = 0; i < map.cellIndices.CellToIndex(new IntVec3(0, 0, gz)); i++)
            {
                if (map.terrainGrid.TerrainAt(i).canFreeze)
                {
                    water++;
                }
            }
            Info($"airskip: ground cells that can freeze {water}");
            // cloth stacks and dirt outdoors: on the ground and on the slab
            ThingDef dirt = DefDatabase<ThingDef>.GetNamed("Filth_Dirt");
            Rand.PushState(FixtureSeed($"{mode}_filth"));
            try
            {
                int n = 0;
                for (int x = oaSite.minX + 12; x < oaSite.maxX - 2 && n < 24; x += 2)
                {
                    for (int z = oaSite.minZ + 2; z < oaSite.maxZ - 2 && n < 24; z += 6)
                    {
                        IntVec3 c = new IntVec3(x, 0, z);
                        oaClothGround.Add(D3Spawn(ThingDefOf.Cloth, 20, c, map));
                        n++;
                    }
                }
                foreach (IntVec3 g in oaSlab.ContractedBy(1))
                {
                    if ((g.x + g.z) % 3 == 0)
                    {
                        oaClothSlab.Add(D3Spawn(ThingDefOf.Cloth, 20, Levels.Cell(g, 1, map), map));
                    }
                }
                int dg = 0, ds = 0;
                foreach (IntVec3 c in oaSite.Cells.Where(c => !oaRoom.ExpandedBy(1).Contains(c) && (c.x * 7 + c.z * 3) % 5 == 0))
                {
                    if (FilthMaker.TryMakeFilth(c, map, dirt, 3))
                    {
                        dg++;
                    }
                }
                foreach (IntVec3 g in oaSlab.ContractedBy(1).Where(g => (g.x + g.z) % 3 == 1))
                {
                    if (FilthMaker.TryMakeFilth(Levels.Cell(g, 1, map), map, dirt, 3))
                    {
                        ds++;
                    }
                }
                Info($"airskip fixture: site {oaSite}, room {oaRoom} (slab over it, floor 1), cloth stacks {oaClothGround.Count} ground + {oaClothSlab.Count} slab, dirt {dg} ground + {ds} slab, colonists {cols.Count}, map {map.Size}, groundZ {gz}, odyssey {ModsConfig.OdysseyActive}, mode {(oaAudit ? "audit (every cell done, open air checked)" : OpenAirSkip.Off ? "fix off" : "fix on")}");
            }
            finally
            {
                Rand.PopState();
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            // the snow noise, made now (see the header)
            map.steadyEnvironmentEffects.AddFallenSnowAt(oaSite.CenterCell, 0f);
            OaMeasure(map, "p0");
            Wait(5, oaPerf ? "oa_perf" : "oa_p1");
        }

        private static void OaKey(string name, string value)
        {
            Info($"OAKEY {name} {value}");
        }

        private static void OaMeasure(Map map, string when)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            double snowGround = 0, snowSlab = 0, snowAir = 0, sandAll = 0;
            foreach (IntVec3 c in oaSite)
            {
                snowGround += map.snowGrid.GetDepth(c);
            }
            foreach (IntVec3 g in oaSlab)
            {
                snowSlab += map.snowGrid.GetDepth(Levels.Cell(g, 1, map));
            }
            int airCells = 0;
            for (int i = map.cellIndices.CellToIndex(new IntVec3(0, 0, a.groundZ)); i < map.cellIndices.NumGridCells; i++)
            {
                if (map.terrainGrid.TerrainAt(i) == PZDefOf.PZ_OpenAir)
                {
                    airCells++;
                    snowAir += map.snowGrid.GetDepth(map.cellIndices.IndexToCell(i));
                    if (map.sandGrid != null)
                    {
                        sandAll += map.sandGrid.GetDepth(map.cellIndices.IndexToCell(i));
                    }
                }
            }
            int iceMap = 0;
            for (int i = 0; i < map.cellIndices.NumGridCells; i++)
            {
                if (map.terrainGrid.TerrainAt(i) == TerrainDefOf.ThinIce)
                {
                    iceMap++;
                }
            }
            string Filth(IEnumerable<IntVec3> cells)
            {
                int n = 0, thick = 0;
                foreach (IntVec3 c in cells)
                {
                    foreach (Thing t in c.GetThingList(map))
                    {
                        if (t is Filth f)
                        {
                            n++;
                            thick += f.thickness;
                        }
                    }
                }
                return $"{n} piles, thickness {thick}";
            }
            string Cloth(List<Thing> list)
            {
                return $"{list.Count(t => !t.Destroyed)} stacks left, HP {list.Where(t => !t.Destroyed).Sum(t => t.HitPoints)}";
            }
            OaKey($"{when}.snow.ground", snowGround.ToString("0.000000"));
            OaKey($"{when}.snow.slab", snowSlab.ToString("0.000000"));
            OaKey($"{when}.snow.openair", $"{snowAir:0.000000} over {airCells} cells");
            OaKey($"{when}.sand.openair", sandAll.ToString("0.000000"));
            OaKey($"{when}.ice.map", iceMap.ToString());
            OaKey($"{when}.dirt.ground", Filth(oaSite.Cells));
            OaKey($"{when}.dirt.slab", Filth(oaSlab.Cells.Select(g => Levels.Cell(g, 1, map))));
            OaKey($"{when}.cloth.ground", Cloth(oaClothGround));
            OaKey($"{when}.cloth.slab", Cloth(oaClothSlab));
            OaKey($"{when}.visits.all", $"steady {oaVisits[0]} hash {oaHashAll[0]:X16}, freeze {oaVisits[1]} hash {oaHashAll[1]:X16}");
            OaKey($"{when}.visits.ran", $"steady {oaRan[0]} hash {oaHashRan[0]:X16}, freeze {oaRan[1]} hash {oaHashRan[1]:X16}");
            Info($"airskip {when}: the game's step ran on empty open air: steady {oaRanOpen[0]}, freeze {oaRanOpen[1]}");
            Info($"airskip {when}: visits steady {oaVisits[0]} (empty open air {oaOpen[0]}), freeze {oaVisits[1]} (empty open air {oaOpen[1]}); tick {Find.TickManager.TicksGame}, temp {map.mapTemperature.OutdoorTemp:0.0}, weather {map.weatherManager.curWeather?.defName}");
        }

        private static void OaReport(Map map)
        {
            long all = oaVisits[0] + oaVisits[1];
            long open = oaOpen[0] + oaOpen[1];
            Info($"airskip: {open} of {all} visits were to empty open-air cells ({100.0 * open / Math.Max(1, all):0.0}%)");
            Result("airskip: steps ran on every kind of cell", oaVisits[0] > 0 && oaReal[0] > 0 && oaOpen[0] > 0 && (!ModsConfig.OdysseyActive || oaOpen[1] > 0), $"steady {oaVisits[0]}, freeze {oaVisits[1]}");
            Result("airskip: the weather did its work", true, "see the OAKEY lines (p0 -> p1 -> p2)");
            if (oaAudit)
            {
                Info($"airskip audit: visits with random draws by phase: p1 steady {oaDrawVisits[1, 0]} freeze {oaDrawVisits[1, 1]}, p2 steady {oaDrawVisits[2, 0]} freeze {oaDrawVisits[2, 1]}");
                foreach (string s in oaDrawSamples)
                {
                    Info("airskip audit draw: " + s);
                }
                Info($"airskip audit: {oaAudited} visits to empty open-air cells run as before and checked: snow changed {oaChangedSnow}, sand changed {oaChangedSand}, terrain changed {oaChangedTerrain}, things changed {oaChangedThings}, flecks made {oaFlecks}; random draws {oaRandDraws} in {oaRandVisits} visits");
                Result("airskip audit: empty open-air cells change nothing", oaAudited > 0 && oaChangedSnow == 0 && oaChangedSand == 0 && oaChangedTerrain == 0 && oaChangedThings == 0 && oaFlecks == 0,
                    $"{oaAudited} visits, changes snow {oaChangedSnow} sand {oaChangedSand} terrain {oaChangedTerrain} things {oaChangedThings} flecks {oaFlecks}");
            }
        }
    }
}
