using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode firefloors (fire between floors, FloorFire; run with -projectzfloors=3 -projectzseed=7). Real Fire things, real
    // ticks (seeded per stage), real firefoam poppers, rain, save/load. The colonists are drafted far away; every fixture
    // stands on its own concrete patch, far from the others. -projectzffpart=walls|slabs|clear|perf runs one part (default
    // all, one after another on the same map).
    //   walls  T1 a burning wood wall lights the wood wall right above (12 columns: ticks and spread attempts to the climb);
    //          T3 a burning wood wall upstairs lights the one below (12 columns, lower rate); H reference: a burning wood
    //          wall lights its one wood neighbour (vanilla, 12); T2 a stone wall above never lights (4); T4 a low wall
    //          (test def: the wall at fill 0.65, an embrasure), a wood column and a wood door above a burning wood wall, and
    //          a full wall above a burning low wall: never lit (3 each)
    //   slabs  T5 a room fire under a wood slab (stone walls, wood floor, stools): nothing on floor 2; T6 a wood storage
    //          room (shelves of logs) under a wood slab: the slab burns through and collapses (stool on it destroyed, steel
    //          falls, letter); T7 the same under a steel and a granite slab: never; T8 a carpet on a steel slab upstairs:
    //          burned carpet, slab intact; T9 a carpet on a wood slab over a burning shelf: carpet burns, the slab still
    //          breaks (sooner than T6: fire on both sides)
    //   clear  T10 a shelf fire under a wood slab put out by a firefoam popper, a fire on a bare wood slab upstairs put out
    //          by rain: exposure kept for an hour, then gone; a second short fire then does not break the slab; T11 save
    //          and load mid-fire (exposure kept, a storage room burns through on time)
    //   perf   T13 a big fire on three floors of wood: the hooks' cost
    // Mode firefloors1 (-projectzfloors=1; -projectznofirefloors: the hooks unpatched, as master): T12 a seeded fire on a
    // map without floors, its fingerprint (compare runs) and no hook call past the map check.
    public static partial class TestRunner
    {
        private sealed class FfPair
        {
            public string kind;
            public IntVec3 src, dst;
            public int attempts;
            public int litAfter = -1;     // ticks from the start to the target's first fire
            public int attemptsAtLit = -1;
            public bool srcLit;
        }

        private static readonly List<FfPair> ffPairs = new List<FfPair>();
        private static readonly Dictionary<IntVec3, List<FfPair>> ffBySrc = new Dictionary<IntVec3, List<FfPair>>();
        private static readonly Dictionary<IntVec3, int> ffFirstFire = new Dictionary<IntVec3, int>();
        private static readonly List<string> ffParts = new List<string>();
        private static int ffT0, ffLetters, ffBroken0, ffTOut, ffTOut2;
        private static WeatherDef ffWeather;
        private static string ffShotName, ffShotNext;
        private static ThingDef ffLowWall;
        private static bool ffPatched;

        // slabs part
        private static CellRect ffT5, ffT6, ffT7s, ffT7g, ffT8, ffT9;
        private static int ffT6Break = -1, ffT9Break = -1, ffT5Fires, ffT5Up = 0, ffT8Fires;
        private static float ffT9TopMax;
        private static bool ffT5Broke, ffT7Burning;
        private static IntVec3 ffT6Stool, ffT6Steel;
        private static readonly Dictionary<IntVec3, int> ffT6Breaks = new Dictionary<IntVec3, int>();
        // clear part
        private static CellRect ffP, ffR, ffK;
        private static float ffPExp1, ffRExp1;
        private static readonly Dictionary<int, float> ffKExp = new Dictionary<int, float>();
        private static int ffKBreak = -1, ffCountBeforeSave, ffSaveTick;
        // perf
        private static readonly Stopwatch ffClock = new Stopwatch();
        private static int ffPeakFires;
        // single-floor
        private static readonly List<string> ffPrints = new List<string>();

        private static IntVec3 FfUp(IntVec3 g, int k, Map map) => Levels.Cell(g, k, map);

        private static ThingDef FfWood => ThingDefOf.WoodLog;

        private static ThingDef FfGranite => DefDatabase<ThingDef>.GetNamed("BlocksGranite");

        // the carpet (1.7: one def per colour; any that burns into the burned carpet)
        private static TerrainDef FfCarpet => DefDatabase<TerrainDef>.AllDefs.Where(t => t.layerable && t.burnedDef?.defName == "BurnedCarpet").OrderBy(t => t.defName).FirstOrDefault();

        private static int FfNow => Find.TickManager.TicksGame;

        private static FloorFireMap FfMap(Map map) => FloorFireMap.Of(map);

        // ------------------------------------------------------------------ test patches

        private static void FfPatch(Harmony h)
        {
            if (ffPatched)
            {
                return;
            }
            ffPatched = true;
            h.Patch(AccessTools.Method(typeof(Fire), nameof(Fire.SpawnSetup)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(FfSpawnPostfix)));
            // (after ProjectZ's own postfix: it was registered at startup)
            h.Patch(AccessTools.Method(typeof(Fire), "TrySpread"), postfix: new HarmonyMethod(typeof(TestRunner), nameof(FfSpreadPostfix)));
        }

        private static void FfSpawnPostfix(Fire __instance, bool respawningAfterLoad)
        {
            if (respawningAfterLoad || !stage.StartsWith("fw"))
            {
                return;
            }
            IntVec3 c = __instance.Position;
            if (!ffFirstFire.ContainsKey(c))
            {
                ffFirstFire[c] = FfNow;
            }
        }

        private static void FfSpreadPostfix(Fire __instance)
        {
            if (__instance.parent != null || !__instance.Spawned || !ffBySrc.TryGetValue(__instance.Position, out List<FfPair> list))
            {
                return;
            }
            foreach (FfPair p in list)
            {
                if (p.litAfter >= 0)
                {
                    continue;
                }
                p.attempts++;
                if (p.dst.ContainsStaticFire(__instance.Map))
                {
                    p.litAfter = FfNow - ffT0;
                    p.attemptsAtLit = p.attempts;
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        private static void FfKeep(Map map)
        {
            WeatherManager wm = map.weatherManager;
            wm.curWeather = ffWeather;
            wm.lastWeather = ffWeather;
            KeepFed(map);
        }

        private static void FfTick(string name, int max, Func<bool> cond, string next, int every = 30)
        {
            SeedTicks("firefloors_" + name);
            Map map = Find.CurrentMap;
            TickUntil(max, () =>
            {
                FfKeep(map);
                return cond();
            }, next, every);
        }

        private static void FfShot(Map map, string name, int floor, IntVec3 g, float size, string next)
        {
            ShotAt(map, floor, Levels.Ground(g, map), size);
            ffShotName = name;
            ffShotNext = next;
            Wait(40, "fw_shot");
        }

        private static Thing FfPlace(Map map, ThingDef def, ThingDef stuff, IntVec3 c)
        {
            map.fogGrid.Unfog(c);
            return Place(def, stuff, c, map);
        }

        private static void FfSlab(Map map, IntVec3 g, TerrainDef slab, TerrainDef floor = null)
        {
            map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
            IntVec3 u = FfUp(g, 1, map);
            map.terrainGrid.SetTerrain(u, slab);
            if (floor != null)
            {
                map.terrainGrid.SetTerrain(u, floor);
            }
            map.fogGrid.Unfog(u);
        }

        // A room: walls of wallStuff on the edge of outer, floor inside, a slab over all of it (optional).
        private static void FfRoom(Map map, CellRect outer, ThingDef wallStuff, TerrainDef floor, TerrainDef slab)
        {
            foreach (IntVec3 c in outer)
            {
                if (outer.IsOnEdge(c))
                {
                    FfPlace(map, ThingDefOf.Wall, wallStuff, c);
                }
                else if (floor != null)
                {
                    map.terrainGrid.SetTerrain(c, floor);
                }
                if (slab != null)
                {
                    FfSlab(map, c, slab);
                }
            }
        }

        // Small wood shelves with three full stacks of logs each on the cells of r.
        private static void FfShelves(Map map, CellRect r)
        {
            ThingDef shelf = DefDatabase<ThingDef>.GetNamed("ShelfSmall");
            foreach (IntVec3 c in r)
            {
                FfPlace(map, shelf, FfWood, c);
                for (int i = 0; i < 3; i++)
                {
                    Thing logs = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                    logs.stackCount = logs.def.stackLimit;
                    GenSpawn.Spawn(logs, c, map);
                }
            }
        }

        private static bool FfLight(Map map, IntVec3 c) => FireUtility.TryStartFireIn(c, map, 0.1f, null);

        private static int FfFiresIn(Map map, CellRect r, int level) => r.Cells.Count(g => FfUp(g, level, map).IsValid && FfUp(g, level, map).ContainsStaticFire(map));

        private static int FfFiresEverIn(CellRect r, int level, Map map, int since) => r.Cells.Count(g => ffFirstFire.TryGetValue(FfUp(g, level, map), out int t) && t >= since);

        private static string FfSlabState(IntVec3 u, Map map)
        {
            TerrainDef s = SlabDamage.SlabAt(u, map);
            return s == null ? "gone" : s.defName;
        }

        private static int FfGone(Map map, CellRect r) => r.Cells.Count(g => SlabDamage.SlabAt(FfUp(g, 1, map), map) == null);

        private static float FfMaxExposure(Map map, CellRect r) => r.Cells.Select(g => FfMap(map)?.ExposureAt(FfUp(g, 1, map)) ?? 0f).DefaultIfEmpty(0f).Max();

        private static int FfExposed(Map map, CellRect r) => r.Cells.Count(g => (FfMap(map)?.ExposureAt(FfUp(g, 1, map)) ?? 0f) > 0f);

        private static ThingDef FfMakeLowWall()
        {
            if (ffLowWall != null)
            {
                return ffLowWall;
            }
            // test def: the vanilla wall at fill 0.65 (as an embrasure / IsoRim low wall); not in the def database, never saved
            ThingDef d = (ThingDef)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(ThingDefOf.Wall, null);
            d.defName = "PZTest_LowWall";
            d.ResolveDefNameHash();
            d.label = "test low wall";
            d.fillPercent = 0.65f;
            ffLowWall = d;
            return d;
        }

        private static void FfNextPart()
        {
            if (ffParts.Count == 0)
            {
                Finish();
                return;
            }
            string p = ffParts[0];
            ffParts.RemoveAt(0);
            Wait(5, "fw_" + p + "_setup");
        }

        // ------------------------------------------------------------------ driver

        private static bool StepFireFloors(Map map)
        {
            Rand.PushState((int)(StableHash("firefloors_" + stage) ^ (uint)(SeedArg ?? 717)));
            try
            {
                return StepFireFloorsInner(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static bool StepFireFloorsInner(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            switch (stage)
            {
                case "fw_shot":
                    Capture(ffShotName);
                    Wait(5, ffShotNext);
                    return true;

                case "fw_setup":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    DebugSettings.enableRandomMentalStates = false;
                    Find.PlaySettings.autoRebuild = false;
                    Find.Storyteller.storytellerComps.Clear();
                    Find.Storyteller.incidentQueue.Clear();
                    ffWeather = WeatherDefOf.Clear;
                    FfKeep(map);
                    if (mode == "firefloors1")
                    {
                        stage = "fw_one_setup";
                        return true;
                    }
                    if (an == null || an.floors < 2 || an.groundZ < 140)
                    {
                        Result("firefloors setup", false, $"needs 2+ floors and 140+ ground rows (-projectzfloors=3); floors {an?.floors}, ground {an?.groundZ}");
                        Finish();
                        return true;
                    }
                    ExClear(map, new CellRect(3, 3, 144, 142));
                    foreach (IntVec3 c in new CellRect(3, 3, 144, 142))
                    {
                        map.terrainGrid.SetTerrain(c, TerrainDefOf.Concrete);
                    }
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
                    {
                        if (p.Faction != Faction.OfPlayer)
                        {
                            p.Destroy();
                        }
                    }
                    List<Pawn> cols = Colonists(map);
                    for (int i = 0; i < cols.Count; i++)
                    {
                        Draft(cols[i], true);
                        Teleport(cols[i], new IntVec3(148, 0, 3 + i));
                    }
                    TerrainDef wood = PZDefOf.PZ_UpperFloor;
                    Info($"map {map.Size}, floors {an.floors}, ground {an.groundZ}; wood slab {wood.defName} flammability {wood.GetStatValueAbstract(StatDefOf.Flammability)}, burns {SlabDefs.Burns(wood)}, threshold {FloorFire.Threshold(wood)}; " +
                        $"steel slab flammability {SlabDefs.For(ThingDefOf.Steel).GetStatValueAbstract(StatDefOf.Flammability)}, burns {SlabDefs.Burns(SlabDefs.For(ThingDefOf.Steel))}; granite slab burns {SlabDefs.Burns(SlabDefs.For(FfGranite))}; " +
                        $"wood wall flammability {ThingDefOf.Wall.GetStatValueAbstract(StatDefOf.Flammability, FfWood)}, HP {ThingDefOf.Wall.GetStatValueAbstract(StatDefOf.MaxHitPoints, FfWood)}");
                    List<string> woody = SlabDefs.All.Where(SlabDefs.Burns).Select(t => t.defName).ToList();
                    List<string> wrong = SlabDefs.All.Where(t => SlabDefs.Burns(t) != SlabDefs.Woody(SlabDefs.StuffOf(t)) || (SlabDefs.Burns(t) ? Mathf.Abs(t.GetStatValueAbstract(StatDefOf.Flammability) - 0.22f) > 0.001f : t.GetStatValueAbstract(StatDefOf.Flammability) != 0f))
                        .Concat(SlabDefs.AllDamaged.Where(t => SlabDefs.Burns(t) || t.GetStatValueAbstract(StatDefOf.Flammability) != 0f)).Select(t => t.defName).ToList();
                    Result("defs: every Woody slab burns at 0.22, every other slab and every damaged slab never (0)", wrong.Count == 0 && woody.Count >= 1,
                        $"burning: {string.Join(", ", woody)}; wrong: {(wrong.Count == 0 ? "none" : string.Join(", ", wrong))}");
                    if (GenCommandLine.TryGetCommandLineArg("projectzffpart", out string part) && part != "all")
                    {
                        ffParts.Add(part);
                    }
                    else
                    {
                        ffParts.AddRange(new[] { "walls", "slabs", "clear", "perf" });
                    }
                    FfNextPart();
                    return true;
                }

                // ================================================================== walls (T1, T2, T3, T4)
                case "fw_walls_setup":
                {
                    ffPairs.Clear();
                    ffBySrc.Clear();
                    ThingDef low = FfMakeLowWall();
                    TerrainDef steel = SlabDefs.For(ThingDefOf.Steel);
                    // kind, src level, dst: (level, x offset, def, stuff)
                    void Column(string kind, int x, int z, ThingDef srcDef, ThingDef dstDef, ThingDef dstStuff, int srcLevel, int dstLevel, int dstDx)
                    {
                        IntVec3 gs = new IntVec3(x + 1, 0, z), gd = new IntVec3(x + 1 + dstDx, 0, z);
                        // support: a granite wall, and a steel slab over the pair
                        FfPlace(map, ThingDefOf.Wall, FfGranite, new IntVec3(x, 0, z));
                        foreach (IntVec3 g in new[] { new IntVec3(x, 0, z), gs, gd }.Distinct())
                        {
                            FfSlab(map, g, steel);
                        }
                        IntVec3 src = FfUp(gs, srcLevel, map), dst = FfUp(gd, dstLevel, map);
                        if (srcLevel == 1 || dstLevel == 1)
                        {
                            // (a wall upstairs that is not part of the pair still holds the granite support over it)
                        }
                        FfPlace(map, srcDef, FfWood, src);
                        if (srcLevel == 1 && dstLevel == 0)
                        {
                            // the ground wall below it
                        }
                        FfPlace(map, dstDef, dstStuff, dst);
                        FfPair p = new FfPair { kind = kind, src = src, dst = dst };
                        ffPairs.Add(p);
                        if (!ffBySrc.TryGetValue(src, out List<FfPair> l))
                        {
                            ffBySrc[src] = l = new List<FfPair>();
                        }
                        l.Add(p);
                    }
                    for (int i = 0; i < 12; i++)
                    {
                        Column("up", 8 + 11 * i, 10, ThingDefOf.Wall, ThingDefOf.Wall, FfWood, 0, 1, 0);
                        Column("down", 8 + 11 * i, 20, ThingDefOf.Wall, ThingDefOf.Wall, FfWood, 1, 0, 0);
                        Column("side", 8 + 11 * i, 30, ThingDefOf.Wall, ThingDefOf.Wall, FfWood, 0, 0, 1);
                    }
                    for (int i = 0; i < 4; i++)
                    {
                        Column("stone", 8 + 11 * i, 40, ThingDefOf.Wall, ThingDefOf.Wall, FfGranite, 0, 1, 0);
                    }
                    for (int i = 0; i < 3; i++)
                    {
                        Column("lowAbove", 8 + 11 * i, 50, ThingDefOf.Wall, low, FfWood, 0, 1, 0);
                        Column("lowBelow", 8 + 11 * (i + 4), 50, low, ThingDefOf.Wall, FfWood, 0, 1, 0);
                        Column("column", 8 + 11 * i, 60, ThingDefOf.Wall, DefDatabase<ThingDef>.GetNamed("Column"), FfWood, 0, 1, 0);
                        Column("door", 8 + 11 * (i + 4), 60, ThingDefOf.Wall, ThingDefOf.Door, FfWood, 0, 1, 0);
                    }
                    List<string> bad = new List<string>();
                    foreach (FfPair p in ffPairs)
                    {
                        Building sb = p.src.GetEdifice(map), db = p.dst.GetEdifice(map);
                        if (sb == null || db == null)
                        {
                            bad.Add($"{p.kind} {p.src}->{p.dst}: {sb?.def.defName ?? "no source"} / {db?.def.defName ?? "no target"}");
                        }
                    }
                    Result("walls: fixtures built (source and target standing)", bad.Count == 0, bad.Count == 0 ? $"{ffPairs.Count} pairs" : string.Join("; ", bad.Take(6)));
                    Building lw = ffPairs.First(p => p.kind == "lowAbove").dst.GetEdifice(map);
                    Info($"low wall test def: fill {lw?.def.fillPercent}, holdsRoof {lw?.def.holdsRoof}, full wall {FloorFire.FullWall(lw)}; wood wall full wall {FloorFire.FullWall(ffPairs[0].src.GetEdifice(map))}; column {FloorFire.FullWall(ffPairs.First(p => p.kind == "column").dst.GetEdifice(map))}, door {FloorFire.FullWall(ffPairs.First(p => p.kind == "door").dst.GetEdifice(map))}");
                    ffFirstFire.Clear();
                    ffT0 = FfNow;
                    FloorFire.Climbs = FloorFire.Drops = 0;
                    int lit = 0;
                    foreach (FfPair p in ffPairs)
                    {
                        if (FfLight(map, p.src))
                        {
                            p.srcLit = true;
                            lit++;
                        }
                    }
                    Result("walls: every source wall set on fire (a real Fire, size 0.1)", lit == ffPairs.Count, $"{lit}/{ffPairs.Count}");
                    FfTick("walls1", 20000, () => ffPairs.Any(p => p.kind == "up" && p.litAfter >= 0), "fw_walls_shot1");
                    return true;
                }
                case "fw_walls_shot1":
                {
                    FfPair p = ffPairs.FirstOrDefault(q => q.kind == "up" && q.litAfter >= 0);
                    if (p == null)
                    {
                        stage = "fw_walls_tick2";
                        return true;
                    }
                    Info($"first climb after {p.litAfter} ticks at {p.dst}");
                    FfShot(map, "fw_wallclimb_f2", 1, p.dst, 9f, "fw_walls_shot2");
                    return true;
                }
                case "fw_walls_shot2":
                {
                    FfPair p = ffPairs.First(q => q.kind == "up" && q.litAfter >= 0);
                    FfShot(map, "fw_wallclimb_f1", 0, p.src, 9f, "fw_walls_tick2");
                    return true;
                }
                case "fw_walls_tick2":
                    FfTick("walls2", 20000 - (FfNow - ffT0), () => FfNow - ffT0 > 2000 && ffPairs.All(p => !p.src.ContainsStaticFire(map)), "fw_walls_res");
                    return true;
                case "fw_walls_res":
                {
                    foreach (string kind in new[] { "up", "down", "side" })
                    {
                        List<FfPair> l = ffPairs.Where(p => p.kind == kind).ToList();
                        List<FfPair> hit = l.Where(p => p.litAfter >= 0).ToList();
                        int att = l.Sum(p => p.litAfter >= 0 ? p.attemptsAtLit : p.attempts);
                        float perAttempt = att == 0 ? 0f : hit.Count / (float)att;
                        List<int> times = hit.Select(p => p.litAfter).OrderBy(t => t).ToList();
                        string detail = $"{hit.Count}/{l.Count} lit; ticks to the light: median {(times.Count == 0 ? -1 : times[times.Count / 2])}, mean {(times.Count == 0 ? -1 : (int)times.Average())}, min {(times.Count == 0 ? -1 : times[0])}, max {(times.Count == 0 ? -1 : times.Last())}; spread attempts {att} -> rate per attempt {perAttempt:0.000} " +
                            $"(design {(kind == "down" ? FloorFire.DownPick : FloorFire.UpPick):0.000} x wall flammability); attempts per column [{string.Join(" ", l.Select(p => p.litAfter >= 0 ? p.attemptsAtLit.ToString() : "-" + p.attempts))}]";
                        if (kind == "up")
                        {
                            Result("T1 a burning wood wall lights the wood wall right above (10+ of 12 within the wall's life)", hit.Count >= 10, detail);
                        }
                        else if (kind == "down")
                        {
                            Result("T3 a burning wood wall upstairs lights the wood wall right below, at a lower rate (some lit, rate per attempt under half the climb's)", hit.Count >= 3, detail);
                        }
                        else
                        {
                            Info("H reference (vanilla): a burning wood wall lights its one wood neighbour: " + detail);
                        }
                    }
                    {
                        List<FfPair> up = ffPairs.Where(p => p.kind == "up").ToList(), dn = ffPairs.Where(p => p.kind == "down").ToList();
                        float ru = up.Count(p => p.litAfter >= 0) / (float)Math.Max(1, up.Sum(p => p.litAfter >= 0 ? p.attemptsAtLit : p.attempts));
                        float rd = dn.Count(p => p.litAfter >= 0) / (float)Math.Max(1, dn.Sum(p => p.litAfter >= 0 ? p.attemptsAtLit : p.attempts));
                        Result("T3 downward rate per spread attempt clearly lower than upward", rd < ru * 0.6f, $"up {ru:0.000}, down {rd:0.000} (ratio {(ru > 0 ? rd / ru : 0):0.00}); counters climbs {FloorFire.Climbs}, drops {FloorFire.Drops}");
                    }
                    foreach (string kind in new[] { "stone", "lowAbove", "lowBelow", "column", "door" })
                    {
                        List<FfPair> l = ffPairs.Where(p => p.kind == kind).ToList();
                        int fires = l.Count(p => ffFirstFire.ContainsKey(p.dst));
                        string detail = $"{fires}/{l.Count} targets ever had a fire; source spread attempts [{string.Join(" ", l.Select(p => p.attempts))}]; targets now [{string.Join(", ", l.Select(p => p.dst.GetEdifice(map)?.def.defName ?? "gone"))}]";
                        string name = kind == "stone" ? "T2 a granite wall above a burning wood wall never lights"
                            : kind == "lowAbove" ? "T4 a low wall (fill 0.65) above a burning wood wall never lights"
                            : kind == "lowBelow" ? "T4 a burning low wall never lights the full wall above"
                            : kind == "column" ? "T4 a wood column above a burning wood wall never lights"
                            : "T4 a wood door above a burning wood wall never lights";
                        Result(name, fires == 0 && l.All(p => p.attempts > 0), detail);
                    }
                    SxFireOut(map);
                    // the test low walls are not in the def database: never into a save (part clear saves the map)
                    foreach (Thing t in map.listerThings.AllThings.Where(t => t.def == ffLowWall).ToList())
                    {
                        t.Destroy();
                    }
                    FfNextPart();
                    return true;
                }

                // ================================================================== slabs (T5 - T9)
                case "fw_slabs_setup":
                {
                    TerrainDef woodSlab = PZDefOf.PZ_UpperFloor, steel = SlabDefs.For(ThingDefOf.Steel), granite = SlabDefs.For(FfGranite);
                    TerrainDef plank = DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor"), carpet = FfCarpet;
                    ffT5 = new CellRect(8, 75, 9, 9);
                    ffT6 = new CellRect(30, 75, 7, 7);
                    ffT7s = new CellRect(50, 75, 7, 7);
                    ffT7g = new CellRect(70, 75, 7, 7);
                    ffT8 = new CellRect(90, 75, 5, 5);
                    ffT9 = new CellRect(110, 75, 7, 7);
                    // T5: granite walls, wood floor, wood stools, wood slab; nothing that climbs
                    FfRoom(map, ffT5, FfGranite, plank, woodSlab);
                    ThingDef stool = DefDatabase<ThingDef>.GetNamed("Stool");
                    foreach (IntVec3 c in new[] { new IntVec3(10, 0, 77), new IntVec3(13, 0, 79), new IntVec3(14, 0, 81), new IntVec3(11, 0, 81) })
                    {
                        FfPlace(map, stool, FfWood, c);
                    }
                    // T6 / T7: storage rooms, shelves of logs in the middle 3x3
                    foreach (KeyValuePair<CellRect, TerrainDef> kv in new[] { new KeyValuePair<CellRect, TerrainDef>(ffT6, woodSlab), new KeyValuePair<CellRect, TerrainDef>(ffT7s, steel), new KeyValuePair<CellRect, TerrainDef>(ffT7g, granite) })
                    {
                        FfRoom(map, kv.Key, FfGranite, plank, kv.Value);
                        FfShelves(map, kv.Key.ContractedBy(2));
                    }
                    // on the T6 slab: a wood stool and a stack of steel over the shelves
                    ffT6Stool = FfUp(ffT6.CenterCell + IntVec3.East, 1, map);
                    ffT6Steel = FfUp(ffT6.CenterCell, 1, map);
                    FfPlace(map, stool, FfWood, ffT6Stool);
                    Thing st = ThingMaker.MakeThing(ThingDefOf.Steel);
                    st.stackCount = 50;
                    GenSpawn.Spawn(st, ffT6Steel, map);
                    // T8: a carpet on a steel slab upstairs (granite wall ring under it)
                    foreach (IntVec3 c in ffT8)
                    {
                        if (ffT8.IsOnEdge(c))
                        {
                            FfPlace(map, ThingDefOf.Wall, FfGranite, c);
                        }
                        FfSlab(map, c, steel, ffT8.ContractedBy(1).Contains(c) ? carpet : null);
                    }
                    // T9: a carpet on a wood slab over a burning shelf
                    foreach (IntVec3 c in ffT9)
                    {
                        if (ffT9.IsOnEdge(c))
                        {
                            FfPlace(map, ThingDefOf.Wall, FfGranite, c);
                        }
                        FfSlab(map, c, woodSlab, ffT9.ContractedBy(1).Contains(c) ? carpet : null);
                    }
                    FfShelves(map, CellRect.SingleCell(ffT9.CenterCell));
                    Info($"T8 cell {FfUp(ffT8.CenterCell, 1, map)}: top {map.terrainGrid.TopTerrainAt(FfUp(ffT8.CenterCell, 1, map)).defName}, slab {FfSlabState(FfUp(ffT8.CenterCell, 1, map), map)}; T9 cell: top {map.terrainGrid.TopTerrainAt(FfUp(ffT9.CenterCell, 1, map)).defName}, slab {FfSlabState(FfUp(ffT9.CenterCell, 1, map), map)}");
                    ffFirstFire.Clear();
                    ffLetters = Find.LetterStack.LettersListForReading.Count;
                    ffBroken0 = FloorFire.BurnedThrough;
                    ffT6Break = ffT9Break = -1;
                    ffT6Breaks.Clear();
                    ffT5Up = 0;
                    ffT9TopMax = 0f;
                    FfShot(map, "fw_carpet_steel_before", 1, ffT8.CenterCell, 6f, "fw_slabs_light");
                    return true;
                }
                case "fw_slabs_light":
                {
                    ffT0 = FfNow;
                    int ok = 0;
                    foreach (IntVec3 c in new[] { new IntVec3(10, 0, 78), new IntVec3(14, 0, 80), new IntVec3(12, 0, 77), new IntVec3(9, 0, 82), new IntVec3(15, 0, 76), new IntVec3(12, 0, 80) })
                    {
                        ok += FfLight(map, c) ? 1 : 0;
                    }
                    foreach (CellRect r in new[] { ffT6, ffT7s, ffT7g })
                    {
                        foreach (IntVec3 c in r.ContractedBy(2))
                        {
                            ok += FfLight(map, c) ? 1 : 0;
                        }
                    }
                    ok += FfLight(map, FfUp(ffT8.CenterCell, 1, map)) ? 1 : 0;
                    ok += FfLight(map, FfUp(ffT9.CenterCell, 1, map)) ? 1 : 0;
                    ok += FfLight(map, FfUp(new IntVec3(ffT9.minX + 1, 0, ffT9.minZ + 1), 1, map)) ? 1 : 0;
                    ok += FfLight(map, FfUp(new IntVec3(ffT9.maxX - 1, 0, ffT9.maxZ - 1), 1, map)) ? 1 : 0;
                    ok += FfLight(map, ffT9.CenterCell) ? 1 : 0;
                    Result("slabs: fires started", ok == 6 + 27 + 5, $"{ok}/38");
                    FfTick("slabs1", 3000, () => { FfSlabsWatch(map); return false; }, "fw_slabs_shot1");
                    return true;
                }
                case "fw_slabs_shot1":
                    Info($"T5 after {FfNow - ffT0} ticks: {FfFiresIn(map, ffT5, 0)} fires on floor 1, {FfFiresIn(map, ffT5, 1)} on floor 2");
                    FfShot(map, "fw_roomfire_f1", 0, ffT5.CenterCell, 9f, "fw_slabs_shot2");
                    return true;
                case "fw_slabs_shot2":
                    FfShot(map, "fw_roomfire_f2", 1, ffT5.CenterCell, 9f, "fw_slabs_shot3");
                    return true;
                case "fw_slabs_shot3":
                    FfShot(map, "fw_slab_before_f2", 1, ffT6.CenterCell, 8f, "fw_slabs_tick2");
                    return true;
                case "fw_slabs_tick2":
                    FfTick("slabs2", 30000, () => { FfSlabsWatch(map); return SlabDamage.SlabAt(ffT6Steel, map) == null && SlabDamage.SlabAt(ffT6Stool, map) == null; }, "fw_slabs_brk", 15);
                    return true;
                case "fw_slabs_brk":
                    // the collapse buffer resolves at the end of the tick: a few more ticks
                    FfTick("slabs3", 20, () => false, "fw_slabs_shot4");
                    return true;
                case "fw_slabs_shot4":
                    // debris pieces don't move while the harness game is paused: run them out before the shots
                    IsoRimBridge.DebrisHold = true;
                    IsoRimBridge.DebrisAdvance(15f);
                    FfShot(map, "fw_slab_after_f2", 1, ffT6.CenterCell, 8f, "fw_slabs_shot5");
                    return true;
                case "fw_slabs_shot5":
                    FfShot(map, "fw_slab_after_f1", 0, ffT6.CenterCell, 8f, "fw_slabs_late");
                    return true;
                case "fw_slabs_late":
                    IsoRimBridge.DebrisHold = false;
                    FfTick("slabslate", 600, () => false, "fw_slabs_late2");
                    return true;
                case "fw_slabs_late2":
                    Info($"T6 600 ticks later: slab cells gone {FfGone(map, ffT6)}; centre top terrain {map.terrainGrid.TopTerrainAt(FfUp(ffT6.CenterCell, 1, map)).defName}");
                    FfShot(map, "fw_slab_late_f2", 1, ffT6.CenterCell, 8f, "fw_slabs_tick4");
                    return true;
                case "fw_slabs_tick4":
                {
                    // T6 result now (things on it fell / were destroyed)
                    int lastT6 = ffT6Breaks.Count == 0 ? ffT6Break : ffT6Breaks.Values.Max();
                    List<string> letters = Find.LetterStack.LettersListForReading.Where(l => l.arrivalTick >= ffT0 + ffT6Break - 20 && l.arrivalTick <= ffT0 + lastT6 + 40).Select(l => l.Label.ToString() + "@" + (l.arrivalTick - ffT0)).ToList();
                    Info("T6 cells burned through (ticks after the start): " + string.Join(", ", ffT6Breaks.OrderBy(kv => kv.Value).Select(kv => $"{Levels.Ground(kv.Key, map)} {kv.Value}")));
                    Info("steel stacks now: " + string.Join(", ", map.listerThings.ThingsOfDef(ThingDefOf.Steel).Where(t => t.Spawned).Select(t => $"{t.stackCount} at {t.Position} (L{Levels.LevelOf(t.Position, map)})")));
                    Thing stoolNow = ffT6Stool.GetFirstThing(map, DefDatabase<ThingDef>.GetNamed("Stool"));
                    List<Thing> steelDown = map.listerThings.ThingsOfDef(ThingDefOf.Steel).Where(t => t.Spawned && Levels.LevelOf(t.Position, map) == 0 && ffT6.ExpandedBy(3).Contains(t.Position)).ToList();
                    Result("T6 a long fire under a wood slab: it burns through and collapses (the stool on it destroyed, the steel falls, a letter)",
                        ffT6Break >= 0 && FfGone(map, ffT6) > 0 && stoolNow == null && steelDown.Count > 0 && letters.Count > 0,
                        $"first cell burned through {ffT6Break} ticks after the fire started; slab cells gone {FfGone(map, ffT6)}/{ffT6.Area}; stool {(stoolNow == null ? "gone" : "still there")}; steel fallen to floor 1 at the room: {steelDown.Sum(t => t.stackCount)}; letters [{string.Join(" | ", letters)}]; burned through {FloorFire.BurnedThrough - ffBroken0}");
                    FfTick("slabs4", 30000, () => { FfSlabsWatch(map); return FfNow - ffT0 > 20000 && ffT9Break >= 0 && FfFiresIn(map, ffT8, 1) == 0 && FfFiresIn(map, ffT9, 1) == 0; }, "fw_slabs_shot6");
                    return true;
                }
                case "fw_slabs_shot6":
                    FfShot(map, "fw_carpet_steel_after", 1, ffT8.CenterCell, 6f, "fw_slabs_res");
                    return true;
                case "fw_slabs_res":
                {
                    TerrainDef steel = SlabDefs.For(ThingDefOf.Steel), granite = SlabDefs.For(FfGranite), burned = DefDatabase<TerrainDef>.GetNamed("BurnedCarpet");
                    int t5Ever = FfFiresEverIn(ffT5, 0, map, ffT0);
                    Result("T5 a room fire under a wood slab (no wall burning) puts no fire on floor 2", ffT5Up == 0 && t5Ever >= 5,
                        $"cells that burned on floor 1: {t5Ever}; fires seen on floor 2 over the room while its slab was whole: {ffT5Up}; slab cells gone {FfGone(map, ffT5)} (broke: {ffT5Broke}); max exposure now {FfMaxExposure(map, ffT5):0}");
                    foreach (KeyValuePair<CellRect, TerrainDef> kv in new[] { new KeyValuePair<CellRect, TerrainDef>(ffT7s, steel), new KeyValuePair<CellRect, TerrainDef>(ffT7g, granite) })
                    {
                        int intact = kv.Key.Cells.Count(g => SlabDamage.SlabAt(FfUp(g, 1, map), map) == kv.Value);
                        Result($"T7 the same storage fire under a {kv.Value.label}: never breaks, no exposure kept",
                            intact == kv.Key.Area && FfExposed(map, kv.Key) == 0 && FfFiresEverIn(kv.Key.ContractedBy(2), 0, map, ffT0) == 9,
                            $"intact {intact}/{kv.Key.Area}; exposed cells {FfExposed(map, kv.Key)}; shelves still burning when T6 broke: {ffT7Burning}; burning now {FfFiresIn(map, kv.Key, 0)}; elapsed {FfNow - ffT0} ticks");
                    }
                    {
                        List<IntVec3> cells = ffT8.ContractedBy(1).Cells.Select(g => FfUp(g, 1, map)).ToList();
                        int burnt = cells.Count(u => map.terrainGrid.TopTerrainAt(u) == burned);
                        int carpetLeft = cells.Count(u => map.terrainGrid.TopTerrainAt(u) == FfCarpet);
                        int intact = ffT8.Cells.Count(g => SlabDamage.SlabAt(FfUp(g, 1, map), map) == steel);
                        Result("T8 a carpet on a steel slab upstairs burns as vanilla (burned carpet), the steel slab stays", burnt >= 1 && burnt + carpetLeft == cells.Count && intact == ffT8.Area && FfExposed(map, ffT8) == 0,
                            $"carpet cells burned {burnt}/{cells.Count} (carpet left {carpetLeft}); fires there at most {ffT8Fires}; steel slab cells {intact}/{ffT8.Area}; exposed cells {FfExposed(map, ffT8)}; cells [{string.Join(", ", cells.Select(u => map.terrainGrid.TopTerrainAt(u).defName))}]; top at the lit cell {map.terrainGrid.TopTerrainAt(FfUp(ffT8.CenterCell, 1, map)).defName} over {FfSlabState(FfUp(ffT8.CenterCell, 1, map), map)}");
                    }
                    {
                        List<IntVec3> cells = ffT9.ContractedBy(1).Cells.Where(g => g != ffT9.CenterCell).Select(g => FfUp(g, 1, map)).ToList();
                        int burnt = cells.Count(u => map.terrainGrid.TopTerrainAt(u) == burned);
                        int woodLeft = cells.Count(u => SlabDamage.SlabAt(u, map) == PZDefOf.PZ_UpperFloor);
                        IntVec3 c1 = FfUp(new IntVec3(ffT9.minX + 1, 0, ffT9.minZ + 1), 1, map), c2 = FfUp(new IntVec3(ffT9.maxX - 1, 0, ffT9.maxZ - 1), 1, map);
                        bool cornersOk = new[] { c1, c2 }.All(u => map.terrainGrid.TopTerrainAt(u) == burned && SlabDamage.SlabAt(u, map) == PZDefOf.PZ_UpperFloor);
                        Result("T9 a carpet on a wood slab burns as vanilla (burned carpet over the intact wood slab where only the carpet burned) and the wood slab over the burning shelf still breaks, sooner than T6 (fire on both sides)",
                            ffT9Break >= 0 && SlabDamage.SlabAt(FfUp(ffT9.CenterCell, 1, map), map) == null && cornersOk && (ffT6Break < 0 || ffT9Break < ffT6Break),
                            $"centre burned through {ffT9Break} ticks after the start (T6 {ffT6Break}); the two lit corner cells: {map.terrainGrid.TopTerrainAt(c1).defName} over {FfSlabState(c1, map)}, {map.terrainGrid.TopTerrainAt(c2).defName} over {FfSlabState(c2, map)}; other carpet cells burned {burnt}/{cells.Count}, wood slab intact {woodLeft}/{cells.Count} (cells next to the centre also get the spilled shelf's fire below); largest exposure seen on a carpet cell outside the centre: {ffT9TopMax:0}");
                    }
                    SxFireOut(map);
                    FfNextPart();
                    return true;
                }

                // ================================================================== clear (T10, T11)
                case "fw_clear_setup":
                {
                    TerrainDef woodSlab = PZDefOf.PZ_UpperFloor, steel = SlabDefs.For(ThingDefOf.Steel);
                    TerrainDef plank = DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor");
                    ffP = new CellRect(10, 100, 5, 5);
                    ffR = new CellRect(40, 100, 7, 7);
                    ffK = new CellRect(70, 100, 7, 7);
                    FfRoom(map, ffP, FfGranite, null, woodSlab);
                    FfShelves(map, CellRect.SingleCell(ffP.CenterCell));
                    foreach (IntVec3 c in ffR)
                    {
                        if (ffR.IsOnEdge(c))
                        {
                            FfPlace(map, ThingDefOf.Wall, FfGranite, c);
                        }
                        FfSlab(map, c, ffR.ContractedBy(2).Contains(c) ? woodSlab : steel);
                    }
                    FfRoom(map, ffK, FfGranite, plank, woodSlab);
                    FfShelves(map, ffK.ContractedBy(2));
                    ffFirstFire.Clear();
                    ffBroken0 = FloorFire.BurnedThrough;
                    ffKBreak = -1;
                    ffT0 = FfNow;
                    int ok = (FfLight(map, ffP.CenterCell) ? 1 : 0) + (FfLight(map, FfUp(ffR.CenterCell, 1, map)) ? 1 : 0);
                    foreach (IntVec3 c in ffK.ContractedBy(2))
                    {
                        ok += FfLight(map, c) ? 1 : 0;
                    }
                    Result("clear: fires started", ok == 11, $"{ok}/11");
                    FfTick("clear1", 6500, () => { FfClearWatch(map); return false; }, "fw_clear_out1");
                    return true;
                }
                case "fw_clear_out1":
                {
                    Info($"after {FfNow - ffT0} ticks: popper room exposure {FfMaxExposure(map, ffP):0}, rain patch max {FfMaxExposure(map, ffR):0} ({FfExposed(map, ffR)} cells, {FfFiresIn(map, ffR, 1)} fires), storage room max {FfMaxExposure(map, ffK):0}");
                    FfPop(map, ffP);
                    ffWeather = DefDatabase<WeatherDef>.GetNamed("Rain");
                    FfTick("clear2", 8000, () => { FfClearWatch(map); return FfFiresIn(map, ffP, 0) == 0 && FfFiresIn(map, ffR, 1) == 0; }, "fw_clear_outres");
                    return true;
                }
                case "fw_clear_outres":
                {
                    ffWeather = WeatherDefOf.Clear;
                    ffTOut = FfNow;
                    ffPExp1 = FfMaxExposure(map, ffP);
                    ffRExp1 = FfMaxExposure(map, ffR);
                    Result("T10 firefoam and rain put the fires out; the exposure is kept for now", FfFiresIn(map, ffP, 0) == 0 && FfFiresIn(map, ffR, 1) == 0 && ffPExp1 > 0f && ffRExp1 > 0f,
                        $"out {FfNow - ffT0} ticks after the start; popper room exposure {ffPExp1:0}, rain patch max {ffRExp1:0} ({FfExposed(map, ffR)} cells); threshold {FloorFire.Threshold(PZDefOf.PZ_UpperFloor):0}");
                    // T11: save and load mid-fire
                    ffKExp.Clear();
                    foreach (IntVec3 g in ffK)
                    {
                        IntVec3 u = FfUp(g, 1, map);
                        float e = FfMap(map).ExposureAt(u);
                        if (e > 0f)
                        {
                            ffKExp[map.cellIndices.CellToIndex(u)] = e;
                        }
                    }
                    ffCountBeforeSave = FfMap(map).Count;
                    ffSaveTick = FfNow;
                    Info($"saving with {ffCountBeforeSave} exposure entries, {FfFiresIn(map, ffK, 0)} fires in the storage room");
                    GameDataSaveLoader.SaveGame("pz_firefloors");
                    stage = "fw_clear_loaded";
                    pendingLoad = "pz_firefloors";
                    return true;
                }
                case "fw_clear_loaded":
                {
                    FloorFireMap fm = FfMap(map);
                    List<string> diff = new List<string>();
                    foreach (KeyValuePair<int, float> kv in ffKExp)
                    {
                        float now = fm.ExposureAt(map.cellIndices.IndexToCell(kv.Key));
                        if (now < kv.Value - 0.01f || now > kv.Value + FloorFire.DamagePerInterval(1.75f) * (1 + (FfNow - ffSaveTick) / 150))
                        {
                            diff.Add($"{map.cellIndices.IndexToCell(kv.Key)} {kv.Value:0.0}->{now:0.0}");
                        }
                    }
                    Result("T11 save/load mid-fire keeps every exposure entry (as saved, plus at most the fire damage of the ticks run since)", fm.Count == ffCountBeforeSave && diff.Count == 0 && ffKExp.Count > 0 && FfFiresIn(map, ffK, 0) > 0,
                        $"entries {ffCountBeforeSave} -> {fm.Count}; storage room cells {ffKExp.Count}, {FfNow - ffSaveTick} ticks since the save; out of range [{string.Join(", ", diff.Take(5))}]; fires in the room after load {FfFiresIn(map, ffK, 0)}");
                    FfTick("clear3", 1000, () => { FfClearWatch(map); return false; }, "fw_clear_hold");
                    return true;
                }
                case "fw_clear_hold":
                    Result("T10 the exposure is still kept after 1000 ticks without fire", FfMaxExposure(map, ffP) == ffPExp1 && FfMaxExposure(map, ffR) == ffRExp1,
                        $"popper room {FfMaxExposure(map, ffP):0}, rain patch {FfMaxExposure(map, ffR):0}, {FfNow - ffTOut} ticks after the fires went out");
                    FfTick("clear4", 4000, () => { FfClearWatch(map); return FfNow - ffTOut >= FloorFire.ClearAfter + 260; }, "fw_clear_gone");
                    return true;
                case "fw_clear_gone":
                {
                    Result("T10 one hour without fire: the exposure is gone", FfExposed(map, ffP) == 0 && FfExposed(map, ffR) == 0,
                        $"{FfNow - ffTOut} ticks after: popper room exposed cells {FfExposed(map, ffP)}, rain patch {FfExposed(map, ffR)}");
                    ffTOut2 = FfNow;
                    // the firefoam left on the popper room floor would refuse any fire (vanilla: foam filth does not allow fire); cleaned as a colonist would
                    int foam = 0;
                    foreach (IntVec3 c in ffP)
                    {
                        foreach (Thing t in c.GetThingList(map).ToList())
                        {
                            if (t is Filth && !t.def.filth.allowsFire)
                            {
                                t.Destroy();
                                foam++;
                            }
                        }
                    }
                    Info($"cleaned {foam} firefoam filth in the popper room");
                    int ok = (FfLight(map, ffP.CenterCell) ? 1 : 0) + (FfLight(map, FfUp(ffR.CenterCell, 1, map)) ? 1 : 0);
                    Result("T10 second fires started", ok == 2, $"{ok}/2");
                    FfTick("clear5", 6500, () => { FfClearWatch(map); return false; }, "fw_clear_second");
                    return true;
                }
                case "fw_clear_second":
                {
                    float p2 = FfMaxExposure(map, ffP), r2 = FfMaxExposure(map, ffR);
                    float thr = FloorFire.Threshold(PZDefOf.PZ_UpperFloor);
                    Result("T10 a second short fire does not break the slab (the first one's exposure was forgotten)",
                        FfGone(map, ffP) == 0 && FfGone(map, ffR) == 0 && p2 < thr && r2 < thr && p2 + ffPExp1 >= thr * 0.9f && r2 + ffRExp1 >= thr * 0.9f,
                        $"after {FfNow - ffTOut2} ticks: popper room {p2:0} (first fire {ffPExp1:0}, sum {p2 + ffPExp1:0} vs threshold {thr:0}), rain patch {r2:0} (first {ffRExp1:0}); gone: {FfGone(map, ffP)}, {FfGone(map, ffR)}");
                    FfPop(map, ffP);
                    ffWeather = DefDatabase<WeatherDef>.GetNamed("Rain");
                    FfTick("clear6", 8000, () => { FfClearWatch(map); return FfFiresIn(map, ffP, 0) == 0 && FfFiresIn(map, ffR, 1) == 0 && ffKBreak >= 0; }, "fw_clear_res");
                    return true;
                }
                case "fw_clear_res":
                    ffWeather = WeatherDefOf.Clear;
                    Result("T11 the storage room saved mid-fire burns through on time (its exposure went on after the load)", ffKBreak >= 0 && ffKBreak < 15000,
                        $"first cell burned through {ffKBreak} ticks after the start (saved at {ffTOut - ffT0}); cells gone {FfGone(map, ffK)}/{ffK.Area}; popper room gone {FfGone(map, ffP)}, rain patch gone {FfGone(map, ffR)}");
                    SxFireOut(map);
                    FfNextPart();
                    return true;

                // ================================================================== perf (T13)
                case "fw_perf_setup":
                {
                    TerrainDef woodSlab = PZDefOf.PZ_UpperFloor, plank = DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor"), carpet = FfCarpet;
                    CellRect block = new CellRect(10, 115, 49, 25);
                    int top = Math.Min(2, Levels.Max(map));
                    foreach (IntVec3 g in block)
                    {
                        bool wall = (g.x - block.minX) % 6 == 0 || (g.z - block.minZ) % 6 == 0;
                        bool door = wall && ((g.x - block.minX) % 6 == 3 || (g.z - block.minZ) % 6 == 3);
                        map.terrainGrid.SetTerrain(g, plank);
                        map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                        for (int k = 0; k <= top; k++)
                        {
                            IntVec3 c = FfUp(g, k, map);
                            if (k >= 1)
                            {
                                map.terrainGrid.SetTerrain(c, woodSlab);
                                if (k == 1)
                                {
                                    map.terrainGrid.SetTerrain(c, carpet);
                                }
                                map.fogGrid.Unfog(c);
                            }
                            if (k < top && wall)
                            {
                                FfPlace(map, door ? ThingDefOf.Door : ThingDefOf.Wall, FfWood, c);
                            }
                        }
                    }
                    ffFirstFire.Clear();
                    ffT0 = FfNow;
                    int ok = 0;
                    // fires on the floors of every room on floors 1 and 2, and on a wall of each room on floor 1 (they climb)
                    for (int rx = 0; rx < 8; rx++)
                    {
                        for (int rz = 0; rz < 4; rz++)
                        {
                            IntVec3 g = new IntVec3(block.minX + 2 + 6 * rx, 0, block.minZ + 2 + 6 * rz);
                            ok += FfLight(map, g) ? 1 : 0;
                            ok += FfLight(map, FfUp(g + IntVec3.North, 1, map)) ? 1 : 0;
                            ok += FfLight(map, new IntVec3(block.minX + 6 * rx, 0, block.minZ + 1 + 6 * rz)) ? 1 : 0;
                        }
                    }
                    Info($"perf: block {block} on {top + 1} levels, wood floors, walls and slabs; fires started {ok}");
                    FloorFire.HookTicks = 0;
                    FloorFire.HookCalls = 0;
                    FloorFire.Timing = true;
                    ffPeakFires = 0;
                    ffClock.Restart();
                    FfTick("perfwarm", 300, () => false, "fw_perf_run");
                    return true;
                }
                case "fw_perf_run":
                {
                    // (after a warm-up: the hooks JIT-compiled)
                    ffT0 = FfNow;
                    FloorFire.HookTicks = 0;
                    FloorFire.BreakTicks = 0;
                    FloorFire.HookCalls = 0;
                    ffClock.Restart();
                    FfTick("perf", 20000, () => { ffPeakFires = Math.Max(ffPeakFires, map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count); return false; }, "fw_perf_res", 50);
                    return true;
                }
                case "fw_perf_res":
                {
                    ffClock.Stop();
                    FloorFire.Timing = false;
                    double ms = FloorFire.HookTicks * 1000.0 / Stopwatch.Frequency, brk = FloorFire.BreakTicks * 1000.0 / Stopwatch.Frequency, steady = ms - brk;
                    int ticks = FfNow - ffT0;
                    Result("T13 perf: the hooks' cost in a big fire on three floors", ticks > 0 && ms / ticks < 0.05,
                        $"{ticks} ticks, peak {ffPeakFires} fires, {ffFirstFire.Count} cells burned; hook calls past the map check {FloorFire.HookCalls}, total {ms:0.00} ms = {ms / ticks * 1000:0.0} us per tick; of which the breaks themselves (collapse + debug log lines) {brk:0.00} ms; without them {steady:0.00} ms = {steady / ticks * 1000:0.0} us per tick, {(FloorFire.HookCalls == 0 ? 0 : steady * 1000 / FloorFire.HookCalls):0.000} us per call; wall time {ffClock.Elapsed.TotalSeconds:0.0} s ({ffClock.Elapsed.TotalMilliseconds / ticks:0.000} ms per tick incl. frames); burned through {FloorFire.BurnedThrough - ffBroken0}, climbs {FloorFire.Climbs}, drops {FloorFire.Drops}");
                    FfShot(map, "fw_perf_f1", 0, new IntVec3(34, 0, 127), 18f, "fw_perf_done");
                    return true;
                }
                case "fw_perf_done":
                    FfNextPart();
                    return true;

                // ================================================================== one floor (T12)
                case "fw_one_setup":
                {
                    bool off = GenCommandLine.CommandLineArgPassed("projectznofirefloors");
                    if (off)
                    {
                        Harmony pz = new Harmony("Peren.IsoRimProjectZ");
                        pz.Unpatch(AccessTools.Method(typeof(Fire), "TrySpread"), HarmonyPatchType.Postfix, "Peren.IsoRimProjectZ");
                        pz.Unpatch(AccessTools.Method(typeof(Fire), "DoComplexCalcs"), HarmonyPatchType.Postfix, "Peren.IsoRimProjectZ");
                    }
                    var info1 = Harmony.GetPatchInfo(AccessTools.Method(typeof(Fire), "TrySpread"));
                    var info2 = Harmony.GetPatchInfo(AccessTools.Method(typeof(Fire), "DoComplexCalcs"));
                    Info($"T12 single floor: annex {ProjectZ.Annex(map) != null}; hooks {(off ? "UNPATCHED (as master)" : "on")}: TrySpread postfixes [{string.Join(", ", info1?.Postfixes.Select(p => p.owner) ?? new string[0])}], DoComplexCalcs postfixes [{string.Join(", ", info2?.Postfixes.Select(p => p.owner) ?? new string[0])}]");
                    if (ProjectZ.Annex(map) != null)
                    {
                        Result("T12 needs a map without floors (-projectzfloors=1)", false, "");
                        Finish();
                        return true;
                    }
                    CellRect area = new CellRect(40, 40, 30, 30);
                    ExClear(map, area);
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
                    {
                        if (p.Faction != Faction.OfPlayer)
                        {
                            p.Destroy();
                        }
                    }
                    List<Pawn> cols = Colonists(map);
                    for (int i = 0; i < cols.Count; i++)
                    {
                        Draft(cols[i], true);
                        Teleport(cols[i], new IntVec3(2, 0, 2 + i));
                    }
                    TerrainDef plank = DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor");
                    CellRect house = new CellRect(45, 45, 11, 9);
                    foreach (IntVec3 c in house)
                    {
                        map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                        if (house.IsOnEdge(c))
                        {
                            FfPlace(map, c.x == 50 && c.z == 45 ? ThingDefOf.Door : ThingDefOf.Wall, FfWood, c);
                        }
                        else
                        {
                            map.terrainGrid.SetTerrain(c, plank);
                        }
                    }
                    FfShelves(map, new CellRect(47, 47, 2, 2));
                    FfPlace(map, DefDatabase<ThingDef>.GetNamed("Stool"), FfWood, new IntVec3(52, 0, 50));
                    ffFirstFire.Clear();
                    FloorFire.HookTicks = 0;
                    FloorFire.HookCalls = 0;
                    FloorFire.Timing = true;
                    ffPrints.Clear();
                    ffT0 = FfNow;
                    ffClock.Restart();
                    Result("T12 fires started", FfLight(map, new IntVec3(47, 0, 47)) & FfLight(map, new IntVec3(53, 0, 51)), "");
                    FfTick("one", 12000, () =>
                    {
                        if ((FfNow - ffT0) % 1500 == 0)
                        {
                            List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
                            ffPrints.Add($"{FfNow - ffT0}:{fires.Count}:{StableHash(string.Join(";", fires.Select(f => f.Position.ToString() + ((Fire)f).fireSize.ToString("0.000")).OrderBy(s => s))) % 100000}");
                        }
                        return false;
                    }, "fw_one_res", 1);
                    return true;
                }
                case "fw_one_res":
                {
                    ffClock.Stop();
                    FloorFire.Timing = false;
                    CellRect area = new CellRect(30, 30, 50, 50);
                    int burned = area.Cells.Count(c => map.terrainGrid.TopTerrainAt(c).defName.StartsWith("Burned"));
                    string things = string.Join(";", area.Cells.SelectMany(c => c.GetThingList(map)).Where(t => !(t is Mote) && !(t is Pawn)).Select(t => t.def.defName + "@" + t.Position + ":" + t.HitPoints).OrderBy(s => s));
                    string print = $"fires [{string.Join(" ", ffPrints)}]; burned floors {burned}; cells that burned {ffFirstFire.Count}; things hash {StableHash(things)}";
                    Info("T12 fingerprint: " + print);
                    Result("T12 a map without floors: no hook call past the map check, no exposure kept", FloorFire.HookCalls == 0 && (FfMap(map)?.Count ?? 0) == 0 && ffFirstFire.Count > 5,
                        $"hook calls past the map check {FloorFire.HookCalls}; exposure entries {FfMap(map)?.Count}; {FfNow - ffT0} ticks in {ffClock.Elapsed.TotalSeconds:0.0} s");
                    try
                    {
                        File.WriteAllText(Path.Combine(outDir, "fingerprint.txt"), print);
                    }
                    catch (Exception)
                    {
                    }
                    Finish();
                    return true;
                }
            }
            return false;
        }

        private static void FfSlabsWatch(Map map)
        {
            if ((FfNow - ffT0) % 750 < 15)
            {
                // trace: per cell exposure and the fire there (T5 floor 1, T9 floor 2), for the report
                List<string> tr = new List<string>();
                foreach (IntVec3 c in ffT5.ContractedBy(1).Cells.Concat(ffT9.ContractedBy(1).Cells.Select(g => FfUp(g, 1, map))))
                {
                    Fire f = c.GetFirstThing<Fire>(map);
                    IntVec3 u = Levels.LevelOf(c, map) == 0 ? FfUp(c, 1, map) : c;
                    float e = FfMap(map)?.ExposureAt(u) ?? 0f;
                    if (f != null || e > 0f)
                    {
                        tr.Add($"{Levels.Ground(c, map)}L{Levels.LevelOf(c, map)} e{e:0} {(f == null ? "-" : $"fire t{f.TicksSinceSpawn} s{f.fireSize:0.00}")} {map.terrainGrid.TopTerrainAt(c).defName}");
                    }
                }
                Out($"  trace {FfNow - ffT0}: " + string.Join("; ", tr));
            }
            if (ffT6Break < 0 && FfGone(map, ffT6) > 0)
            {
                ffT6Break = FfNow - ffT0;
                ffT7Burning = FfFiresIn(map, ffT7s, 0) > 0 && FfFiresIn(map, ffT7g, 0) > 0;
            }
            foreach (IntVec3 g in ffT6)
            {
                IntVec3 u = FfUp(g, 1, map);
                if (!ffT6Breaks.ContainsKey(u) && SlabDamage.SlabAt(u, map) == null)
                {
                    ffT6Breaks[u] = FfNow - ffT0;
                }
            }
            if (ffT9Break < 0 && SlabDamage.SlabAt(FfUp(ffT9.CenterCell, 1, map), map) == null)
            {
                ffT9Break = FfNow - ffT0;
            }
            if (FfGone(map, ffT5) == 0)
            {
                ffT5Up = Math.Max(ffT5Up, FfFiresEverIn(ffT5, 1, map, ffT0));
            }
            else
            {
                ffT5Broke = true;
            }
            ffT8Fires = Math.Max(ffT8Fires, FfFiresIn(map, ffT8, 1));
            foreach (IntVec3 g in ffT9.ContractedBy(1))
            {
                if (g != ffT9.CenterCell)
                {
                    ffT9TopMax = Math.Max(ffT9TopMax, FfMap(map)?.ExposureAt(FfUp(g, 1, map)) ?? 0f);
                }
            }
        }

        private static void FfClearWatch(Map map)
        {
            if (ffKBreak < 0 && FfGone(map, ffK) > 0)
            {
                ffKBreak = FfNow - ffT0;
            }
        }

        // A firefoam popper in the room, set off as the game does when it is burned (CompExplosive's wick on flame damage).
        private static void FfPop(Map map, CellRect room)
        {
            IntVec3 c = room.CenterCell + IntVec3.North;
            Thing popper = FfPlace(map, DefDatabase<ThingDef>.GetNamed("FirefoamPopper"), null, c);
            popper.TakeDamage(new DamageInfo(DamageDefOf.Flame, 1f));
            Info($"firefoam popper at {c}: {(popper.Destroyed ? "popped" : "wick lit")}");
        }
    }
}
