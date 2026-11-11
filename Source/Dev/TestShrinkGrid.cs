using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode shrinkgrid (grids made while a map is shrunk for generation, MapGenPatches Grid_ClearAndResizeTo_Patch).
    // Args: -projectzsgcase=gen|t1|t2|t5, -projectzseed=N, -projectzfloors=N, optional -projectzscenario=<ScenarioDef>
    // (the quick test's scenario instead of Crashlanded; a forced map, e.g. an orbit start, keeps its own tile and map
    // generator) and -projectzgravship=<VGE StartingGravshipDef> (the scenario's chosen starting gravship). Every case is one
    // launch (its own fixture). Base vs head needs the harness's -isorimseed=N: IsoRim then makes Map.NextGenSeed and the
    // other generation seeds the same in every process (Api 72), so the generated maps match.
    //   gen  right after generation, before any tick: SNAP lines (map size, ground rows, things by def and position with a
    //        hash, things in the annex, VEF pipe nets and their grid sizes, the areas' grid sizes, GridFixer's count),
    //        compared base vs head by the runner. The full thing list goes to sg_things.txt.
    //   t1   gen, then: every PipeNet.networkGrid is the map's size. A platform beside the ship carries slabs on every upper
    //        floor (columns, stairs between the floors), a slab corridor on floor 2 reaches over the ship's east-most oxygen
    //        pipe. Colonists build an oxygen pipe from a blueprint (the game's designator) on every upper floor, floor 2's
    //        right above the ship's pipe. Then the pipe build overlay is drawn for 300 ticks on the top floor. Expect no
    //        error and no IndexOutOfRange. Save, load, and a colonist builds one more pipe on floor 3.
    //   t2   a colonist builds an oxygen pipe on the ground next to a ship oxygen pipe: it joins the ship's net, and the
    //        net's stored oxygen is the same right after as right before.
    //   t5   gen (tall mountains, a mountainous tile), plus hashes of the terrain, foundation and fog of the ground and of
    //        the annex.
    public static partial class TestRunner
    {
        private static string GxCase => GenCommandLine.TryGetCommandLineArg("projectzsgcase", out string c) ? c : "gen";

        private static string gxScenarioName;
        private static ScenarioDef gxScenarioDef;
        private static bool gxSwitched;
        private static int gxIoor;
        private static int gxErrors0;

        // t1 fixture
        private static CellRect gxPlat;
        private static IntVec3 gxShipPipe;
        private static readonly List<IntVec3> gxPipeCells = new List<IntVec3>();
        private static IntVec3 gxExtraCell;
        private static ThingDef gxPipeDef;
        private static Def gxNetDef;
        // t2 fixture
        private static IntVec3 gxT2Cell = IntVec3.Invalid, gxT2Ship = IntVec3.Invalid;
        private static int gxOverlayTicks;
        private static float gxT2Before = float.NaN, gxT2Prev = float.NaN;

        private static void GxInit(Harmony h)
        {
            GridFixer.Recorder = new List<string>();   // (what GridFixer resizes, read by the SNAP lines)
            if (GenCommandLine.TryGetCommandLineArg("projectzscenario", out string sn))
            {
                gxScenarioName = sn;
                h.Patch(AccessTools.Method(typeof(Scenario), nameof(Scenario.PreConfigure)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(GxPreConfigurePrefix)));
                h.Patch(AccessTools.Method(typeof(Root_Play), nameof(Root_Play.SetupForQuickTestPlay)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(GxSetupPostfix)) { priority = Priority.Low });
            }
            h.Patch(AccessTools.Method(typeof(Game), nameof(Game.InitNewGame)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(GxInitNewGamePostfix)));
            h.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }), postfix: new HarmonyMethod(typeof(TestRunner), nameof(GxErrorPostfix)));
        }

        private static void GxErrorPostfix(string text)
        {
            if (text != null && text.Contains("IndexOutOfRange"))
            {
                gxIoor++;
            }
        }

        // The quick test's Crashlanded is swapped for the chosen scenario before it configures (pawns, parts).
        private static bool GxPreConfigurePrefix(Scenario __instance)
        {
            // (the defs are loaded by now; GxInit runs in the mod's constructor, before them)
            if (gxScenarioName != null && gxScenarioDef == null && !gxSwitched)
            {
                gxScenarioDef = DefDatabase<ScenarioDef>.GetNamedSilentFail(gxScenarioName);
                Out($"shrinkgrid: scenario {gxScenarioName} -> {gxScenarioDef?.defName ?? "NOT FOUND"}");
                if (gxScenarioDef == null)
                {
                    gxSwitched = true;
                    Result("shrinkgrid: scenario", false, gxScenarioName + " not found");
                }
            }
            if (gxScenarioDef == null || gxSwitched || Current.ProgramState != ProgramState.Entry || Current.Game == null || !ReferenceEquals(Current.Game.Scenario, __instance))
            {
                return true;
            }
            gxSwitched = true;
            Scenario s = gxScenarioDef.scenario.CopyForEditing();
            if (GenCommandLine.TryGetCommandLineArg("projectzgravship", out string gs))
            {
                ScenPart part = s.AllParts.FirstOrDefault(p => p.GetType().Name == "ScenPart_ChooseStartingGravship");
                FieldInfo f = part == null ? null : AccessTools.Field(part.GetType(), "chosenDef");
                Def def = f == null ? null : GenDefDatabase.GetDefSilentFail(f.FieldType, gs, false);
                if (def != null)
                {
                    f.SetValue(part, def);
                }
                Out($"shrinkgrid: chosen gravship {gs} -> {(def != null ? "set" : "NOT SET (part " + (part != null) + ")")}");
            }
            Current.Game.Scenario = s;
            s.PreConfigure();
            return false;
        }

        // After the quick test's own tile pick (vanilla's random surface tile, TestRunner's forest tile): a forced map
        // (an orbit start) picks its tile and map generator again, under the run's seed.
        private static void GxSetupPostfix()
        {
            List<ScenPart_ForcedMap> forced = Find.Scenario.AllParts.OfType<ScenPart_ForcedMap>().ToList();
            if (forced.Count == 0)
            {
                return;
            }
            Rand.PushState(FixtureSeed("shrinkgrid_tile"));
            try
            {
                foreach (ScenPart_ForcedMap p in forced)
                {
                    p.PostWorldGenerate();
                }
            }
            finally
            {
                Rand.PopState();
            }
            Out($"shrinkgrid: forced map {Find.GameInitData.mapGeneratorDef?.defName} tile {Find.GameInitData.startingTile}, map size {Find.GameInitData.mapSize}");
        }

        private static void GxInitNewGamePostfix()
        {
            Map map = Find.CurrentMap ?? Find.Maps.FirstOrDefault();
            if (map == null)
            {
                Result("shrinkgrid: a map after InitNewGame", false, "none");
                return;
            }
            try
            {
                GxSnapshot(map);
            }
            catch (Exception e)
            {
                Result("shrinkgrid: snapshot", false, e.ToString());
            }
        }

        // ---------------------------------------------------------------- VEF pipe nets (by reflection: ProjectZ does not reference VEF)

        private static MapComponent GxPipeManager(Map map) => map.components.FirstOrDefault(c => c.GetType().FullName == "PipeSystem.PipeNetManager");

        private static IList GxNets(Map map)
        {
            MapComponent m = GxPipeManager(map);
            return m == null ? null : Traverse.Create(m).Field("pipeNets").GetValue() as IList;
        }

        private static BoolGrid GxNetGrid(object net) => Traverse.Create(net).Field("networkGrid").GetValue<BoolGrid>();

        private static Def GxNetDefOf(object net) => Traverse.Create(net).Field("def").GetValue<Def>();

        private static object GxNetAt(Map map, IntVec3 c)
        {
            MapComponent m = GxPipeManager(map);
            if (m == null || gxNetDef == null)
            {
                return null;
            }
            try
            {
                return AccessTools.Method(m.GetType(), "GetPipeNetAt").Invoke(m, new object[] { c, gxNetDef });
            }
            catch (TargetInvocationException e)
            {
                // (the bug itself: a ground-sized net grid indexed with an upper floor's cell)
                if (e.InnerException is IndexOutOfRangeException)
                {
                    gxIoor++;
                    gxNetAtThrew++;
                    return null;
                }
                throw;
            }
        }

        private static int gxNetAtThrew;
        private static bool gxT2Filled;

        // Fills every tank of a net (CompResourceStorage.AddResource with what it can still take); returns the amount added.
        private static float GxFill(Map map, object net)
        {
            float added = 0f;
            foreach (ThingComp c in GxStorages(map, net))
            {
                float can = Traverse.Create(c).Property("AmountCanAccept").GetValue<float>();
                if (can > 0f)
                {
                    Traverse.Create(c).Method("AddResource", new[] { typeof(float) }).GetValue(can);
                    added += can;
                }
            }
            return added;
        }

        private static IEnumerable<ThingComp> GxStorages(Map map, object net)
        {
            if (net == null)
            {
                yield break;
            }
            foreach (Thing t in map.listerThings.AllThings.ToList())
            {
                if (!(t is ThingWithComps tw))
                {
                    continue;
                }
                foreach (ThingComp c in tw.AllComps)
                {
                    for (Type ty = c.GetType(); ty != null; ty = ty.BaseType)
                    {
                        if (ty.FullName == "PipeSystem.CompResourceStorage")
                        {
                            if (ReferenceEquals(Traverse.Create(c).Property("PipeNet").GetValue(), net))
                            {
                                yield return c;
                            }
                            break;
                        }
                    }
                }
            }
        }

        // The oxygen stored in a net: every CompResourceStorage on the map whose PipeNet is that net.
        private static float GxStored(Map map, object net)
        {
            return net == null ? float.NaN : GxStorages(map, net).Sum(c => Traverse.Create(c).Property("AmountStored").GetValue<float>());
        }

        private static void GxFindOxygen()
        {
            Type netDefType = AccessTools.TypeByName("PipeSystem.PipeNetDef");
            if (netDefType == null)
            {
                return;
            }
            gxNetDef = GenDefDatabase.GetDefSilentFail(netDefType, "VGE_OxygenNet", false);
            gxPipeDef = DefDatabase<ThingDef>.GetNamedSilentFail("VGE_OxygenPipe");
        }

        private static bool GxIsOxygenPipe(Thing t)
        {
            if (gxNetDef == null)
            {
                return false;
            }
            List<ThingDef> pipes = Traverse.Create(gxNetDef).Field("pipeDefs").GetValue<List<ThingDef>>();
            return pipes != null && pipes.Contains(t.def);
        }

        // ---------------------------------------------------------------- snapshot (right after generation)

        private static void GxSnapshot(Map map)
        {
            GxFindOxygen();
            AnnexMapComponent an = ProjectZ.Annex(map);
            int groundZ = ProjectZ.GroundZ(map);
            Info($"SNAP map size {map.Size}, ground rows {groundZ}, floors {an?.floors ?? 1}, stride {an?.stride ?? 0}, generator {map.generatorDef?.defName}, tile {map.Tile}, scenario {Find.Scenario?.name}");
            List<string> things = map.listerThings.AllThings
                .Select(t => $"{t.def.defName}|{t.Stuff?.defName}|{t.Position.x},{t.Position.y},{t.Position.z}|{t.Rotation.AsInt}|{t.stackCount}")
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            uint hash = StableHash(string.Join("\n", things));
            int inAnnex = map.listerThings.AllThings.Count(t => t.Position.z >= groundZ);
            Info($"SNAP things {things.Count} hash {hash:x8}, in the annex (z >= {groundZ}): {inAnnex}");
            // (tall mountains raise rock into the upper floors on purpose: there it is only compared base vs head)
            Result("SNAP nothing in the annex after generation" + (GxCase == "t5" ? " (tall mountains: rock expected, compared base vs head)" : ""), inAnnex == 0 || GxCase == "t5", $"{inAnnex} thing(s) at z >= {groundZ}: " + string.Join(", ", map.listerThings.AllThings.Where(t => t.Position.z >= groundZ).Take(8).Select(t => $"{t.def.defName}@{t.Position}")));
            try
            {
                File.WriteAllLines(Path.Combine(outDir, "sg_things.txt"), things);
            }
            catch (Exception e)
            {
                Out("sg_things write failed: " + e.Message);
            }
            int pawns = map.mapPawns.AllPawnsSpawnedCount;
            Info($"SNAP pawns spawned {pawns}, colonists {map.mapPawns.FreeColonistsSpawnedCount}");
            // areas
            foreach (Area a in map.areaManager.AllAreas)
            {
                BoolGrid g = Traverse.Create(a).Field("innerGrid").GetValue<BoolGrid>();
                Info($"SNAP area {a.GetType().Name} {a.Label}: grid {g?.Width}x{g?.Height}, cells {g?.TrueCount}");
            }
            // VEF pipe nets
            IList nets = GxNets(map);
            if (nets == null)
            {
                Info("SNAP pipe nets: no PipeNetManager (VEF not loaded)");
            }
            else
            {
                MapComponent m = GxPipeManager(map);
                BoolGrid valve = Traverse.Create(m).Field("valveGrid").GetValue<BoolGrid>();
                Info($"SNAP pipe nets {nets.Count}, valve grid {valve?.Width}x{valve?.Height}");
                int bad = 0;
                List<string> lines = new List<string>();
                for (int i = 0; i < nets.Count; i++)
                {
                    BoolGrid g = GxNetGrid(nets[i]);
                    bool full = g != null && g.Width == map.Size.x && g.Height == map.Size.z;
                    if (!full)
                    {
                        bad++;
                    }
                    lines.Add($"{GxNetDefOf(nets[i])?.defName} {g?.Width}x{g?.Height} cells {g?.TrueCount}");
                }
                foreach (string l in lines.OrderBy(s => s, StringComparer.Ordinal))
                {
                    Info("SNAP pipe net " + l);
                }
                if (GxCase == "t1")
                {
                    Result("T1 every PipeNet.networkGrid is the map's size right after generation", nets.Count > 0 && bad == 0, $"{nets.Count} net(s), {bad} not {map.Size.x}x{map.Size.z}: " + string.Join("; ", lines));
                }
                if (gxNetDef != null)
                {
                    int shipPipes = map.listerThings.AllThings.Count(GxIsOxygenPipe);
                    Info($"SNAP oxygen pipes {shipPipes}");
                }
            }
            List<string> fixedFields = GridFixer.Recorder ?? new List<string>();
            int fixerGrids = fixedFields.Count(l => l.StartsWith("grid "));
            Info($"SNAP GridFixer count {fixedFields.Count} (grids and plain arrays it resized at FinalizeInit, every map so far)");
            foreach (string l in fixedFields)
            {
                Info("SNAP GridFixer resized: " + l);
            }
            Info($"SNAP GridFixer grids resized {fixerGrids} (backstop; 0 expected with the full-size rule), plain arrays {fixedFields.Count - fixerGrids}");
            if (GxCase == "t5")
            {
                GxTerrainHashes(map, groundZ);
            }
        }

        private static void GxTerrainHashes(Map map, int groundZ)
        {
            CellIndices ci = map.cellIndices;
            int split = ci.CellToIndex(new IntVec3(0, 0, groundZ));
            List<string> ground = new List<string>(), annex = new List<string>();
            List<string> gFog = new List<string>(), aFog = new List<string>();
            for (int i = 0; i < ci.NumGridCells; i++)
            {
                string t = map.terrainGrid.topGrid[i]?.defName + "/" + map.terrainGrid.FoundationAt(i)?.defName + "/" + map.roofGrid.RoofAt(i)?.defName;
                string f = map.fogGrid.IsFogged(i) ? "1" : "0";
                (i < split ? ground : annex).Add(t);
                (i < split ? gFog : aFog).Add(f);
            }
            Info($"SNAP T5 terrain hash ground {StableHash(string.Join(",", ground)):x8} ({ground.Count} cells), annex {StableHash(string.Join(",", annex)):x8} ({annex.Count} cells)");
            Info($"SNAP T5 fog hash ground {StableHash(string.Concat(gFog)):x8} fogged {gFog.Count(x => x == "1")}, annex {StableHash(string.Concat(aFog)):x8} fogged {aFog.Count(x => x == "1")}");
            Dictionary<string, int> kinds = annex.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            Info("SNAP T5 annex terrain/foundation/roof kinds: " + string.Join(", ", kinds.OrderByDescending(k => k.Value).Take(12).Select(k => $"{k.Key} {k.Value}")));
        }

        // ---------------------------------------------------------------- the driver

        private static bool StepShrinkGrid(Map map)
        {
            Rand.PushState(FixtureSeed("shrinkgrid_" + stage));
            try
            {
                return StepShrinkGridInner(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static bool StepShrinkGridInner(Map map)
        {
            switch (stage)
            {
                case "gx_start":
                    Info($"shrinkgrid case {GxCase}; errors so far {errors}, IndexOutOfRange so far {gxIoor}");
                    if (GxCase == "t1")
                    {
                        stage = "gx_t1_setup";
                    }
                    else if (GxCase == "t2")
                    {
                        stage = "gx_t2_setup";
                    }
                    else
                    {
                        Result("SNAP no IndexOutOfRange up to the first update", gxIoor == 0, $"{gxIoor}");
                        Finish();
                    }
                    return true;

                // ---- T1
                case "gx_t1_setup":
                    GxT1Setup(map);
                    return true;
                case "gx_t1_designate":
                    GxDesignate(map, gxPipeCells, "T1");
                    SetWork(map, true);
                    SeedTicks("shrinkgrid_t1_build");
                    TickUntil(40000, () =>
                    {
                        KeepFed(map);
                        return gxPipeCells.All(c => GxPipeAt(map, c));
                    }, "gx_t1_built", 30);
                    return true;
                case "gx_t1_built":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    List<string> per = gxPipeCells.Select(c => $"f{ProjectZ.FloorOf(c, map) + 1} {c} {(GxPipeAt(map, c) ? "built" : "NOT built")}, net {(GxNetAt(map, c) != null ? "yes" : "none")}").ToList();
                    Result("T1 colonists built an oxygen pipe on every upper floor (2, 3, 4, 5) from blueprints", gxPipeCells.All(c => GxPipeAt(map, c)), $"after {ticksTotal} ticks: " + string.Join("; ", per));
                    IntVec3 over = gxPipeCells[0];
                    object upNet = GxNetAt(map, over), shipNet = GxNetAt(map, gxShipPipe);
                    Result("T1 the floor-2 pipe right above the ship's pipe has its own net (not the ship's)", upNet != null && shipNet != null && !ReferenceEquals(upNet, shipNet), $"above {over}: {(upNet != null ? "net" : "none")}, ship pipe {gxShipPipe}: {(shipNet != null ? "net" : "none")}");
                    GxNetSizes(map, "T1 after building");
                    Info($"T1 errors while building: {errors - gxErrors0}, IndexOutOfRange {gxIoor}");
                    // the pipe build overlay, viewed from the top floor
                    int top = (ProjectZ.Annex(map)?.floors ?? 1) - 1;
                    ShotAt(map, top, new IntVec3(gxPlat.CenterCell.x, 0, gxPlat.CenterCell.z), 18f);
                    Find.DesignatorManager.Select(BuildCopyCommandUtility.FindAllowedDesignator(gxPipeDef) ?? new Designator_Build(gxPipeDef));
                    gxOverlayTicks = 0;
                    Wait(30, "gx_t1_overlay");
                    return true;
                }
                case "gx_t1_overlay":
                    // one seeded tick per drawn frame: the overlay is drawn between every two of the 300 ticks
                    if (gxOverlayTicks < 300)
                    {
                        SeedTicks("shrinkgrid_t1_overlay_" + gxOverlayTicks);
                        gxOverlayTicks++;
                        StartTicks(1, "gx_t1_overlay");
                        return true;
                    }
                    stage = "gx_t1_overlay2";
                    return true;
                case "gx_t1_overlay2":
                    Wait(30, "gx_t1_overlay3");
                    return true;
                case "gx_t1_overlay3":
                    Capture("sg_t1_overlay_top");
                    Wait(5, "gx_t1_overlay4");
                    return true;
                case "gx_t1_overlay4":
                    Find.DesignatorManager.Deselect();
                    Result("T1 300 ticks with the pipe overlay drawn on the top floor, from the start of the build: no error, no IndexOutOfRange", errors - gxErrors0 == 0 && gxIoor == 0, $"errors {errors - gxErrors0}, IndexOutOfRange {gxIoor} (of them thrown by GetPipeNetAt called by the test's checks: {gxNetAtThrew})");
                    savedTicks = Find.TickManager.TicksGame;
                    GameDataSaveLoader.SaveGame("pz_shrinkgrid");
                    Result("T1 saved pz_shrinkgrid", File.Exists(GenFilePaths.FilePathForSavedGame("pz_shrinkgrid")), "");
                    stage = "gx_t1_loaded";
                    pendingLoad = "pz_shrinkgrid";
                    return true;
                case "gx_t1_loaded":
                {
                    GxFindOxygen();
                    Info($"T1 reloaded at tick {ticksAfterLoad} (saved {savedTicks})");
                    GxNetSizes(map, "T1 after the load");
                    Result("T1 after the load: every pipe still there, each in a net", gxPipeCells.All(c => GxPipeAt(map, c) && GxNetAt(map, c) != null), string.Join("; ", gxPipeCells.Select(c => $"{c} {GxPipeAt(map, c)} {GxNetAt(map, c) != null}")));
                    gxErrors0 = errors;
                    gxIoor = gxNetAtThrew = 0;
                    Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    steel.stackCount = 10;
                    GenSpawn.Spawn(steel, new IntVec3(gxPlat.maxX - 1, 0, gxPlat.minZ + 1), map);
                    GxDesignate(map, new List<IntVec3> { gxExtraCell }, "T1 after the load");
                    SetWork(map, true);
                    SeedTicks("shrinkgrid_t1_extra");
                    TickUntil(20000, () =>
                    {
                        KeepFed(map);
                        return GxPipeAt(map, gxExtraCell);
                    }, "gx_t1_extra", 30);
                    return true;
                }
                case "gx_t1_extra":
                    tickWatch = null;
                    SetWork(map, false);
                    Result("T1 after the load a colonist builds one more pipe on floor 3", GxPipeAt(map, gxExtraCell) && GxNetAt(map, gxExtraCell) != null, $"{gxExtraCell} after {ticksTotal} ticks");
                    StartTicks(120, "gx_t1_end");
                    return true;
                case "gx_t1_end":
                    Result("T1 after the load and the extra pipe: no error, no IndexOutOfRange", errors - gxErrors0 == 0 && gxIoor == 0, $"errors {errors - gxErrors0}, IndexOutOfRange {gxIoor} (of them thrown by GetPipeNetAt called by the test's checks: {gxNetAtThrew})");
                    GxNetSizes(map, "T1 end");
                    Finish();
                    return true;

                // ---- T2
                case "gx_t2_setup":
                    GxT2Setup(map);
                    return true;
                case "gx_t2_designate":
                    GxDesignate(map, new List<IntVec3> { gxT2Cell }, "T2");
                    SetWork(map, true);
                    SeedTicks("shrinkgrid_t2_build");
                    TickUntil(20000, () =>
                    {
                        KeepFed(map);
                        if (GxPipeAt(map, gxT2Cell))
                        {
                            gxT2Before = gxT2Prev;
                            return true;
                        }
                        // The ship's net drains while the colonist works (its oxygen goes into the rooms): fill its tanks
                        // once, when the pipe's frame appears, so the join is measured with oxygen in the net.
                        if (!gxT2Filled && gxT2Cell.GetThingList(map).Any(t => t is Frame))
                        {
                            gxT2Filled = true;
                            Info($"T2 frame up at tick {ticksTotal - ticksLeft}: the ship net's tanks filled (+{GxFill(map, GxNetAt(map, gxT2Ship)):0.0000})");
                        }
                        gxT2Prev = GxStored(map, GxNetAt(map, gxT2Ship));
                        return false;
                    }, "gx_t2_built", 1);
                    return true;
                case "gx_t2_built":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    object a = GxNetAt(map, gxT2Cell), b = GxNetAt(map, gxT2Ship);
                    float after = GxStored(map, b);
                    Result("T2 a colonist built the ground pipe next to the ship's pipe", GxPipeAt(map, gxT2Cell), $"{gxT2Cell} after {ticksTotal} ticks");
                    Result("T2 the new ground pipe joined the ship's oxygen net", a != null && ReferenceEquals(a, b), $"new {gxT2Cell} net {(a != null ? "yes" : "none")}, ship {gxT2Ship} net {(b != null ? "yes" : "none")}, same {ReferenceEquals(a, b)}");
                    Result("T2 the net's stored oxygen is unchanged by the join", !float.IsNaN(gxT2Before) && gxT2Before > 0f && Mathf.Abs(after - gxT2Before) < 0.001f, $"(tanks filled at the frame: {gxT2Filled}) before {gxT2Before:0.0000}, after {after:0.0000}");
                    Info($"SNAP T2 stored oxygen before {gxT2Before:0.0000} after {after:0.0000}, nets {GxNets(map)?.Count}");
                    GxNetSizes(map, "T2 end");
                    Result("T2 no error, no IndexOutOfRange", errors - gxErrors0 == 0 && gxIoor == 0, $"errors {errors - gxErrors0}, IndexOutOfRange {gxIoor} (of them thrown by GetPipeNetAt called by the test's checks: {gxNetAtThrew})");
                    Finish();
                    return true;
                }
            }
            return false;
        }

        private static bool GxPipeAt(Map map, IntVec3 c) => c.IsValid && c.GetThingList(map).Any(t => t.def == gxPipeDef);

        private static void GxNetSizes(Map map, string when)
        {
            IList nets = GxNets(map);
            if (nets == null)
            {
                return;
            }
            List<string> lines = new List<string>();
            int bad = 0;
            foreach (object n in nets)
            {
                BoolGrid g = GxNetGrid(n);
                if (g == null || g.Width != map.Size.x || g.Height != map.Size.z)
                {
                    bad++;
                }
                lines.Add($"{GxNetDefOf(n)?.defName} {g?.Width}x{g?.Height}");
            }
            Info($"{when}: {nets.Count} pipe net(s), {bad} not the map's size {map.Size.x}x{map.Size.z}: " + string.Join("; ", lines.OrderBy(s => s, StringComparer.Ordinal)));
        }

        private static void GxDesignate(Map map, List<IntVec3> cells, string what)
        {
            Designator_Build d = BuildCopyCommandUtility.FindAllowedDesignator(gxPipeDef) as Designator_Build ?? new Designator_Build(gxPipeDef);
            foreach (IntVec3 c in cells)
            {
                FloorView.Set(ProjectZ.FloorOf(c, map) < 0 ? 0 : ProjectZ.FloorOf(c, map));
                AcceptanceReport ok = d.CanDesignateCell(c);
                if (ok.Accepted)
                {
                    d.DesignateMultiCell(new List<IntVec3> { c });
                }
                bool bp = c.GetThingList(map).Any(t => t is Blueprint && t.def.entityDefToBuild == gxPipeDef);
                Result($"{what} blueprint placed through the designator at {c} (floor {ProjectZ.FloorOf(c, map) + 1})", ok.Accepted && bp, $"accepted {ok.Accepted} '{ok.Reason}', blueprint {bp}");
            }
            FloorView.Set(0);
        }

        // Colonists ready to work in vacuum: injuries healed, a vacsuit and helmet if they lack vacuum resistance, good
        // builders; every other pawn, the raids and the scenario's enemy ship are kept away.
        private static void GxPrepareColonists(Map map, IntVec3 standAt)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            DebugSettings.enableRandomMentalStates = false;
            Find.PlaySettings.autoRebuild = false;
            Find.Storyteller.storytellerComps.Clear();
            Find.Storyteller.incidentQueue.Clear();
            foreach (ScenPart p in Find.Scenario.AllParts.Where(x => x.GetType().Name == "ScenPart_TheGravship"))
            {
                AccessTools.Method(p.GetType(), "NotifyPlayerEscaped")?.Invoke(p, null);
                Info("scenario's enemy gravship arrival cancelled (ScenPart_TheGravship.NotifyPlayerEscaped)");
            }
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (p.Faction != Faction.OfPlayer)
                {
                    p.Destroy();
                }
            }
            ThingDef suit = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_Vacsuit");
            ThingDef helmet = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_VacsuitHelmet");
            List<Pawn> cols = Colonists(map);
            for (int i = 0; i < cols.Count; i++)
            {
                Pawn p = cols[i];
                foreach (Hediff hd in p.health.hediffSet.hediffs.Where(x => x is Hediff_Injury || x is Hediff_MissingPart).ToList())
                {
                    p.health.RemoveHediff(hd);
                }
                if (p.GetStatValue(StatDefOf.VacuumResistance) < 1f && p.apparel != null)
                {
                    foreach (ThingDef ad in new[] { suit, helmet })
                    {
                        if (ad != null)
                        {
                            p.apparel.Wear((Apparel)ThingMaker.MakeThing(ad, ad.MadeFromStuff ? GenStuff.DefaultStuffFor(ad) : null), false);
                        }
                    }
                }
                SkillRecord sk = p.skills?.GetSkill(SkillDefOf.Construction);
                if (sk != null)
                {
                    sk.Level = 20;
                }
                Draft(p, false);
                if (standAt.IsValid)
                {
                    Teleport(p, standAt + new IntVec3(i % 4, 0, i / 4));
                }
            }
            SetWork(map, false);
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            Info("colonists: " + string.Join("; ", cols.Select(p => $"{p.LabelShort} at {p.Position} downed {p.Downed} vacuum resistance {p.GetStatValue(StatDefOf.VacuumResistance):0.00} construction {(p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) ? "disabled" : "ok")}")));
        }

        private static List<Thing> GxShipPipes(Map map) => map.listerThings.AllThings.Where(t => GxIsOxygenPipe(t) && t.Faction == Faction.OfPlayer && t.Position.z < ProjectZ.GroundZ(map)).ToList();

        private static void GxT1Setup(Map map)
        {
            GxFindOxygen();
            AnnexMapComponent an = ProjectZ.Annex(map);
            List<Thing> pipes = GxShipPipes(map);
            if (an == null || an.floors < 5 || gxPipeDef == null || pipes.Count == 0)
            {
                Result("T1 setup", false, $"needs 5 floors, VGE and a ship with oxygen pipes: floors {an?.floors}, pipe def {gxPipeDef?.defName}, ship oxygen pipes {pipes.Count}");
                Finish();
                return;
            }
            // The ship: the bounding rect of its substructure; the east-most oxygen pipe with nothing built on its cell.
            List<IntVec3> sub = map.AllCells.Where(c => c.z < an.groundZ && map.terrainGrid.FoundationAt(c) != null && map.terrainGrid.FoundationAt(c).IsSubstructure).ToList();
            CellRect ship = CellRect.FromLimits(sub.Min(c => c.x), sub.Min(c => c.z), sub.Max(c => c.x), sub.Max(c => c.z));
            Thing target = pipes.Where(t => t.Position.GetEdifice(map) == null).OrderByDescending(t => t.Position.x).ThenBy(t => t.Position.z).FirstOrDefault()
                ?? pipes.OrderByDescending(t => t.Position.x).ThenBy(t => t.Position.z).First();
            gxShipPipe = target.Position;
            TerrainDef top = gxShipPipe.GetTerrain(map);
            TerrainDef found = map.terrainGrid.FoundationAt(gxShipPipe);
            // Platform P (14 x 12) east of the ship, 3 cells of space between, its middle on the pipe's row.
            int x0 = ship.maxX + 4;
            int z0 = Mathf.Clamp(gxShipPipe.z - 6, 2, an.groundZ - 14);
            gxPlat = new CellRect(x0, z0, 14, 12);
            if (!gxPlat.InBounds(map) || gxPlat.maxX >= map.Size.x - 2)
            {
                Result("T1 setup", false, $"platform {gxPlat} out of bounds (ship {ship}, map {map.Size})");
                Finish();
                return;
            }
            foreach (IntVec3 c in gxPlat)
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (!(t is Pawn) && t.def.destroyable)
                    {
                        t.Destroy();
                    }
                }
                if (found != null)
                {
                    map.terrainGrid.SetFoundation(c, found);
                }
                map.terrainGrid.SetTerrain(c, top);
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
            }
            // Stairs k -> k+1 (k = 0..3), facing north, each in its own column: footprint z0+2..z0+5, exit above z0+5.
            List<IntVec3> stairs = Enumerable.Range(0, 4).Select(k => ProjectZ.CellOnFloor(new IntVec3(x0 + 2 + 3 * k, 0, z0 + 3), k, map)).ToList();
            HashSet<IntVec3> wells = new HashSet<IntVec3>();
            for (int k = 0; k < 4; k++)
            {
                foreach (IntVec3 r in StairShape.Ramp(new IntVec3(x0 + 2 + 3 * k, 0, z0 + 3), Rot4.North))
                {
                    wells.Add(ProjectZ.CellOnFloor(r, k + 1, map));
                }
            }
            // Slabs on floors 2-5 over the platform (not over a well); on floor 2 also a 3-wide corridor west to the ship's pipe.
            List<IntVec3> slab = new List<IntVec3>();
            for (int k = 1; k <= 4; k++)
            {
                foreach (IntVec3 g in gxPlat)
                {
                    slab.Add(ProjectZ.CellOnFloor(g, k, map));
                }
            }
            for (int x = gxShipPipe.x - 1; x < x0; x++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    slab.Add(ProjectZ.CellOnFloor(new IntVec3(x, 0, gxShipPipe.z + dz), 1, map));
                }
            }
            foreach (IntVec3 u in slab)
            {
                if (!wells.Contains(u))
                {
                    map.terrainGrid.SetTerrain(u, PZDefOf.PZ_UpperFloor);
                    map.fogGrid.Unfog(u);
                }
            }
            // Columns on floors 1-4 at the platform's edge (no slab rests more than 6 cells from one).
            ThingDef column = DefDatabase<ThingDef>.GetNamed("Column");
            (int, int)[] posts = { (0, 0), (6, 0), (13, 0), (0, 6), (13, 6), (0, 11), (6, 11), (13, 11) };
            for (int k = 0; k < 4; k++)
            {
                foreach ((int dx, int dz) in posts)
                {
                    Place(column, ThingDefOf.Steel, ProjectZ.CellOnFloor(new IntVec3(x0 + dx, 0, z0 + dz), k, map), map);
                }
            }
            for (int k = 0; k < 4; k++)
            {
                Place(PZDefOf.PZ_Stairs, ThingDefOf.Steel, stairs[k], map, Rot4.North);
            }
            // The pipes: floor 2 right above the ship's pipe; floors 3, 4 and 5 on the platform; after the load one more on floor 3.
            gxPipeCells.Clear();
            gxPipeCells.Add(ProjectZ.CellOnFloor(gxShipPipe, 1, map));
            for (int k = 2; k <= 4; k++)
            {
                gxPipeCells.Add(ProjectZ.CellOnFloor(new IntVec3(x0 + 12, 0, z0 + 9), k, map));
            }
            gxExtraCell = ProjectZ.CellOnFloor(new IntVec3(x0 + 11, 0, z0 + 9), 2, map);
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 20;
            GenSpawn.Spawn(steel, new IntVec3(x0 + 12, 0, z0 + 1), map);
            GxPrepareColonists(map, new IntVec3(x0 + 7, 0, z0 + 7));
            Info($"T1 fixture: ship {ship}, ship oxygen pipe {gxShipPipe} ({target.def.defName}, edifice {gxShipPipe.GetEdifice(map)?.def.defName ?? "none"}, terrain {top?.defName}, foundation {found?.defName}), platform {gxPlat}, stairs {string.Join(" ", stairs.Select(s => $"{s} exit {StairShape.Exit(s, Rot4.North, map)}"))}, pipe cells {string.Join(" ", gxPipeCells)}, extra {gxExtraCell}; ship oxygen pipes {pipes.Count}");
            GxNetSizes(map, "T1 before building");
            gxErrors0 = errors;
            gxIoor = gxNetAtThrew = 0;
            SeedTicks("shrinkgrid_t1_settle");
            StartTicks(60, "gx_t1_designate");
        }

        private static void GxT2Setup(Map map)
        {
            GxFindOxygen();
            List<Thing> pipes = GxShipPipes(map);
            if (gxPipeDef == null || pipes.Count == 0)
            {
                Result("T2 setup", false, $"needs VGE and a ship with oxygen pipes: pipe def {gxPipeDef?.defName}, ship oxygen pipes {pipes.Count}");
                Finish();
                return;
            }
            HashSet<IntVec3> pipeCells = new HashSet<IntVec3>(pipes.Select(t => t.Position));
            // A ground cell next to a ship oxygen pipe: substructure, standable, no building, no pipe of any net.
            foreach (Thing t in pipes.OrderBy(t => t.Position.x).ThenBy(t => t.Position.z))
            {
                foreach (IntVec3 c in GenAdj.CellsAdjacentCardinal(t))
                {
                    if (!c.InBounds(map) || pipeCells.Contains(c) || c.GetEdifice(map) != null || !c.Standable(map) || map.terrainGrid.FoundationAt(c) == null || !map.terrainGrid.FoundationAt(c).IsSubstructure)
                    {
                        continue;
                    }
                    if (c.GetThingList(map).Any(x => x is Building || x is Blueprint || x is Frame))
                    {
                        continue;
                    }
                    gxT2Cell = c;
                    gxT2Ship = t.Position;
                    break;
                }
                if (gxT2Cell.IsValid)
                {
                    break;
                }
            }
            if (!gxT2Cell.IsValid)
            {
                Result("T2 setup", false, "no free substructure cell next to a ship oxygen pipe");
                Finish();
                return;
            }
            IntVec3 stand = GenAdj.CellsAdjacent8Way(new TargetInfo(gxT2Cell, map)).FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && c != gxT2Cell);
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 5;
            GenPlace.TryPlaceThing(steel, stand, map, ThingPlaceMode.Near);
            GxPrepareColonists(map, IntVec3.Invalid);
            object net = GxNetAt(map, gxT2Ship);
            Info($"T2 fixture: new pipe {gxT2Cell}, next to ship pipe {gxT2Ship}; ship net stored {GxStored(map, net):0.0000}; nets {GxNets(map)?.Count}");
            GxNetSizes(map, "T2 before building");
            gxErrors0 = errors;
            gxIoor = gxNetAtThrew = 0;
            SeedTicks("shrinkgrid_t2_settle");
            StartTicks(60, "gx_t2_designate");
        }
    }
}
