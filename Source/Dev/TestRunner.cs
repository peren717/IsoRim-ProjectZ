using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ProjectZ.Patches;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Headless harness test (harness 2, `run.sh <tag> - projectz pz=...`). Inert unless the game is started with
    // -quicktest -projectztest. Output: <savedatafolder>/renders/<tag>/ (log.txt, results.txt, *.png, done.txt).
    //   -projectztag=<tag>
    //   -projectzmode=main|plain|gen|raids|m1b|m1c   main: full M1a check; plain: make a no-annex save "pz_plain"
    //                                   (component removed, like a save from before the addon); gen: generation + finders
    //                                   only; raids: the M1a raid battery with a reason for each failure; m1b: stairs,
    //                                   upper floor, hauling, sleeping, a raid upstairs, save mid-climb, 3D shots (TestM1b)
    //                                   m1c: roofs under upper floors, heat between floors, shooting between floors
    //                                   (sight, hit chances, cover, real fighting, projectile shots), save/reload (TestM1c)
    //                                   m1c2: playtest findings (roofs, stacked walls, map edge, move order, facing, range
    //                                   ring, mortar under a floor) with screenshots (TestM1c2)
    //                                   stairs: the stairs batch (1x3 stairs, stairwell, rebuild from either floor, jumping
    //                                   down, slab height rule, wall-height lock, ornate door) with screenshots (TestStairs)
    //                                   floorui: building floor 2 through the UI pointer and designators, refusal messages,
    //                                   no-designator fall-through, language shots (TestFloorUI)
    //                                   save: load a player save (-projectzsave=<name>), describe its stairs and colonists,
    //                                   tick it (-projectzsaveticks=N) until everything planned upstairs is built (TestSave)
    //                                   deadend: two stairs, the nearer one up onto a one-cell floor; a bed upstairs
    //                                   is built over the other one (TestDeadEnd; -projectzgreedyroute shows the old router)
    //                                   staircolour: stairs of wood, steel and granite next to walls of the same stuff, day and
    //                                   night, floor 1 and 2 views, roofs on and off (TestStairColour)
    //                                   slabremove: taking up upper floors (peel order, carpet, blockers), stairs in an
    //                                   open field, a slab collapsing; run with -projectzfloors=3 (TestSlabRemove)
    //                                   bugs4: ground sun/moon shadows while floor 2 is viewed (-projectznoshadowfix: the
    //                                   old open-air shadow mask), floors laid on a stairs landing by colonists, stairs
    //                                   with a carpeted landing deconstructed (TestBugs4)
    //                                   slabstuff: slabs of any stuff (defs, dropdown, colonists building wood, steel,
    //                                   granite and marble slabs, a carpet on them, a stone slab taken up, steel stairs' landing,
    //                                   a collapse) with shots; slabold: a save from before it (-projectzsave=<name>) (TestSlabStuff)
    //                                   qol1: ground-only buildings, the floor scroll, the floor HUD (dots, clicks, UI scale
    //                                   1 and 1.5, Alt + drag, reset, hide); run with -projectzfloors=3 (TestQoL1)
    //                                   drops: drop pods and skyfallers with upper floors (finders, centre drops into a
    //                                   thin-roofed floor-2 room, never under a slab, a scattered raid onto a platform and
    //                                   its retreat); run with -projectzmapsize=120 (TestDrops)
    //                                   thick: thick rock roofs (no slab over, no stairs under them, the roof guard, stairs at
    //                                   a mountain edge, infestations) (TestThick)
    //                                   space: the orbital relay map of the gravship start (Odyssey): stairs, landing, upper
    //                                   floors, walking up, taking a slab up, vacuum under the floors; -projectzspacefloors
    //                                   applies IsoRim's proposed space-map fix at run time (TestSpace)
    //                                   power: power across floors (conduit risers over 3 floors, a battery that must
    //                                   not join, hidden conduits under walls, hookups at the band edge, a colonist
    //                                   deconstructing and rebuilding the riser's ground conduit, save/reload) with
    //                                   power-overlay shots; run with -projectzfloors=3 (TestPower)
    //                                   range: weapon range across floors (3D distance + 1 per floor down): shotguns vs
    //                                   floors 13, 2 and 5, a sniper on floor 5, a turret on floor 2, a raider walking in,
    //                                   a mortar, and the range preview shots; run with -projectzfloors=13 (TestRange)
    //   -projectzfloors=N  floors for maps generated in this run (overrides the setting)
    //   -projectzdays=D     in-game days to fast-forward in main mode (default 2)
    //   -projectzmapsize=N  quick-test ground size (default 150)
    // Tests drive the real game: incidents through their workers, then real ticks (TickManager.DoSingleTick).
    public static partial class TestRunner
    {
        public static bool Active;
        public static int FloorsOverride;
        // Test only (-projectznoexitfix): the stair-link test without the traveller's own check, to show the old freeze.
        public static bool NoExitFix;

        private static string mode = "main";
        private static string tag = "projectz";
        private static string outDir;
        private static string logPath;
        private static float days = 2f;
        private static readonly List<string> results = new List<string>();
        private static int fails;
        private static int errors;
        private static int warnings;

        // state machine (static: survives the save/load in the middle of the main test)
        internal static string stage = "start";
        internal static int waitFrames;
        private static int ticksLeft;
        private static int ticksTotal;
        private static string pendingShot;
        private static int pendingSince;
        private static string pendingLoad;
        private static Game loadFrom;
        private static readonly Stopwatch clock = new Stopwatch();

        public static void Init()
        {
            Active = GenCommandLine.CommandLineArgPassed("quicktest") && GenCommandLine.CommandLineArgPassed("projectztest");
            if (!Active)
            {
                return;
            }
            if (GenCommandLine.TryGetCommandLineArg("projectzmode", out string m))
            {
                mode = m;
            }
            if (GenCommandLine.TryGetCommandLineArg("projectztag", out string t))
            {
                tag = t;
            }
            if (GenCommandLine.TryGetCommandLineArg("projectzfloors", out string f) && int.TryParse(f, out int fl))
            {
                FloorsOverride = Mathf.Clamp(fl, 1, ProjectZSettings.MaxFloors);
            }
            if (GenCommandLine.TryGetCommandLineArg("projectzdays", out string d) && float.TryParse(d, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dd))
            {
                days = dd;
            }
            FloorJump.Disabled = GenCommandLine.CommandLineArgPassed("projectznojumpfix");
            NoExitFix = GenCommandLine.CommandLineArgPassed("projectznoexitfix");
            TerrainShadowMask_OpenAir_Patch.Disabled = GenCommandLine.CommandLineArgPassed("projectznoshadowfix");
            GreedyRoute = GenCommandLine.CommandLineArgPassed("projectzgreedyroute");
            if (mode == "plain")
            {
                FloorsOverride = 1;
            }
            outDir = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", tag);
            Directory.CreateDirectory(outDir);
            foreach (string name in new[] { "log.txt", "results.txt", "done.txt" })
            {
                string p = Path.Combine(outDir, name);
                if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
            logPath = Path.Combine(outDir, "log.txt");
            Harmony h = new Harmony("Peren.IsoRimProjectZ.test");
            foreach (string lm in new[] { "Message", "Warning", "Error" })
            {
                MethodInfo mi = AccessTools.Method(typeof(Log), lm, new[] { typeof(string) });
                if (mi != null)
                {
                    h.Patch(mi, postfix: new HarmonyMethod(typeof(TestRunner), nameof(LogPostfix)));
                }
            }
            h.Patch(AccessTools.Method(typeof(Root_Play), nameof(Root_Play.SetupForQuickTestPlay)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(SetupPostfix)));
            // Driven from the game loop without a GameComponent (nothing test-only goes into saves).
            h.Patch(AccessTools.Method(typeof(Game), nameof(Game.UpdatePlay)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(Update)));
            h.Patch(AccessTools.Method(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(OnGUI)));
            h.Patch(AccessTools.Method(typeof(GenExplosion), nameof(GenExplosion.DoExplosion)), prefix: new HarmonyMethod(typeof(TestExplosionWatch), nameof(TestExplosionWatch.Prefix)));
            DrawLog.Patch(h);
            if (mode == "floorui" || mode == "slabremove" || mode == "bugs4" || mode == "slabstuff" || mode == "qol1" || mode == "thick" || mode == "space" || mode == "power" || mode == "riser")
            {
                PatchMouse(h);
            }
            if (GenCommandLine.CommandLineArgPassed("projectzspacefloors"))
            {
                PatchIsoRimSpaceFloors(h);
            }
            h.Patch(AccessTools.Method(typeof(Frame), nameof(Frame.FailConstruction)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(BotchPrefix)));
            if (GenCommandLine.CommandLineArgPassed("cap17hide"))
            {
                try
                {
                    Traverse.Create(Waylith.EngineApplication.Instance).Property("Window").Property("Visible").SetValue(false);
                }
                catch (Exception e)
                {
                    Out("hide failed: " + e.Message);
                }
            }
            Out($"test active: mode {mode}, floors {FloorsOverride}, days {days}, out {outDir}");
        }

        // Construction botches (each loses part of the frame's materials): the m1b "upper floor built" flake.
        internal static int botches;
        internal static int botchedMaterials;

        private static void BotchPrefix(Frame __instance)
        {
            botches++;
            botchedMaterials += __instance.resourceContainer?.TotalStackCount ?? 0;
        }

        private static void LogPostfix(string text, MethodBase __originalMethod)
        {
            string kind = __originalMethod.Name.ToUpperInvariant();
            if (kind == "ERROR")
            {
                errors++;
                if (errors == 300 && stage != "done")
                {
                    Append("[PZTest] 300 errors: giving up");
                    Result("error flood", false, "300 errors logged; see log.txt");
                    Finish();
                }
            }
            else if (kind == "WARNING")
            {
                warnings++;
            }
            Append(kind + ": " + text);
        }

        private static void SetupPostfix()
        {
            Find.GameInitData.mapSize = GenCommandLine.TryGetCommandLineArg("projectzmapsize", out string ms) && int.TryParse(ms, out int n) ? n : 150;
            try
            {
                foreach (SurfaceTile tile in Find.WorldGrid.Tiles.InRandomOrder())
                {
                    // -projectzriver: a tile with a river and a road, so the river genSteps and the river flow map run
                    bool wantRiver = GenCommandLine.CommandLineArgPassed("projectzriver");
                    if (wantRiver && (tile.Rivers.NullOrEmpty() || tile.Roads.NullOrEmpty()))
                    {
                        continue;
                    }
                    // -projectzmutator=<TileMutatorDef>: a tile with that mutator (e.g. MixedBiome)
                    if (GenCommandLine.TryGetCommandLineArg("projectzmutator", out string mut) && !tile.Mutators.Any(m => m.defName == mut))
                    {
                        continue;
                    }
                    if (tile.PrimaryBiome == BiomeDefOf.TemperateForest && (wantRiver || tile.hilliness == Hilliness.SmallHills) && TileFinder.IsValidTileForNewSettlement(tile.tile))
                    {
                        Find.GameInitData.startingTile = tile.tile;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Out("tile pick failed: " + e.Message);
            }
        }

        public static void Out(string s)
        {
            Append("[PZTest] " + s);
        }

        private static void Append(string s)
        {
            try
            {
                File.AppendAllText(logPath ?? Path.Combine(GenFilePaths.SaveDataFolderPath, "projectz_test.log"), DateTime.Now.ToString("HH:mm:ss ") + s + "\n");
            }
            catch (Exception)
            {
            }
        }

        private static void Result(string name, bool ok, string detail)
        {
            string line = (ok ? "PASS " : "FAIL ") + name + ": " + detail;
            if (!ok)
            {
                fails++;
            }
            results.Add(line);
            Out(line);
        }

        private static void Info(string line)
        {
            results.Add("INFO " + line);
            Out(line);
        }

        // ------------------------------------------------------------------ driver

        public static void Update()
        {
            if (!Active || stage == "done" || pendingLoad != null || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || LongEventHandler.ShouldWaitForEvent)
            {
                return;
            }
            if (loadFrom != null)
            {
                if (ReferenceEquals(Current.Game, loadFrom))
                {
                    return;
                }
                loadFrom = null;
                waitFrames = 60;
                ticksAfterLoad = Find.TickManager.TicksGame;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                Out($"new game after load: map {Find.CurrentMap} size {Find.CurrentMap.Size}");
            }
            try
            {
                Step(Find.CurrentMap);
            }
            catch (Exception e)
            {
                Result("exception", false, e.ToString());
                Finish();
            }
        }

        public static void OnGUI()
        {
            if (Active && guiAction != null && Event.current.type == EventType.Repaint)
            {
                Action act = guiAction;
                guiAction = null;
                act();
            }
            if (!Active || pendingLoad == null || Event.current.type != EventType.Repaint)
            {
                return;
            }
            string name = pendingLoad;
            loadFrom = Current.Game;
            pendingLoad = null;
            Out("loading " + name);
            GameDataSaveLoader.LoadGame(name);
        }

        private static void Wait(int frames, string next)
        {
            waitFrames = frames;
            stage = next;
        }

        private static void Step(Map map)
        {
            if (pendingShot != null)
            {
                if (!File.Exists(pendingShot) || new FileInfo(pendingShot).Length == 0)
                {
                    if (Time.frameCount - pendingSince > 600)
                    {
                        Result("screenshot", false, "never written: " + pendingShot);
                        pendingShot = null;
                    }
                    return;
                }
                Info("screenshot " + pendingShot + " " + new FileInfo(pendingShot).Length + " bytes");
                pendingShot = null;
            }
            if (waitFrames > 0)
            {
                waitFrames--;
                return;
            }
            if (ticksLeft > 0)
            {
                DoTicks(map);
                return;
            }
            if (stage.StartsWith("b_"))
            {
                StepM1b(map);
                return;
            }
            if (stage.StartsWith("c_"))
            {
                StepM1c(map);
                return;
            }
            if (stage.StartsWith("d_"))
            {
                StepM1c2(map);
                return;
            }
            if (stage.StartsWith("w_") || stage.StartsWith("x_") || stage.StartsWith("r_"))
            {
                StepStairwell(map);
                return;
            }
            if (stage.StartsWith("u_"))
            {
                StepFloorUI(map);
                return;
            }
            if (stage.StartsWith("s_"))
            {
                StepStairs(map);
                return;
            }
            if (stage.StartsWith("l_"))
            {
                StepLight(map);
                return;
            }
            if (stage.StartsWith("v_"))
            {
                StepSave(map);
                return;
            }
            if (stage.StartsWith("k_"))
            {
                StepDeadEnd(map);
                return;
            }
            if (stage.StartsWith("z_"))
            {
                StepSlabRemove(map);
                return;
            }
            if (stage.StartsWith("q_"))
            {
                StepStairColour(map);
                return;
            }
            if (stage.StartsWith("p_"))
            {
                StepSlabStuff(map);
                return;
            }
            if (stage.StartsWith("t_"))
            {
                StepThick(map);
                return;
            }
            if (stage.StartsWith("a_"))
            {
                StepDrops(map);
                return;
            }
            if (stage.StartsWith("o_"))
            {
                StepQoL1(map);
                return;
            }
            if (stage.StartsWith("y_"))
            {
                StepBugs4(map);
                return;
            }
            if (stage.StartsWith("e_"))
            {
                StepSpace(map);
                return;
            }
            if (stage.StartsWith("n_"))
            {
                StepPower(map);
                return;
            }
            if (stage.StartsWith("j_"))
            {
                StepRiser(map);
                return;
            }
            if (stage.StartsWith("g_"))
            {
                StepRange(map);
                return;
            }
            switch (stage)
            {
                case "start":
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(30, mode == "plain" ? "plain_save" : mode == "save" ? "v_load" : mode == "slabold" ? "p_oldload" : "gencheck");
                    break;

                // ---- plain: a save made "before the addon": no annex, no AnnexMapComponent node
                case "plain_save":
                {
                    AnnexMapComponent comp = map.GetComponent<AnnexMapComponent>();
                    Result("plain map has no annex", !ProjectZ.HasAnnex(map) && comp != null && comp.floors <= 1, $"size {map.Size}");
                    map.components.Remove(comp);
                    ProjectZ.Invalidate();
                    StartTicks(600, "plain_save2");
                    break;
                }
                case "plain_save2":
                    GameDataSaveLoader.SaveGame("pz_plain");
                    Result("saved pz_plain", File.Exists(GenFilePaths.FilePathForSavedGame("pz_plain")), GenFilePaths.FilePathForSavedGame("pz_plain"));
                    Finish();
                    break;

                // ---- main / gen
                case "gencheck":
                    GenCheck(map, "after generation");
                    FinderStats(map);
                    if (mode == "m1b")
                    {
                        stage = "b_setup";
                    }
                    else if (mode == "m1c")
                    {
                        stage = "c_setup";
                    }
                    else if (mode == "m1c2")
                    {
                        stage = "d_setup";
                    }
                    else if (mode == "stairs" || mode == "floorui")
                    {
                        stage = "s_setup";
                    }
                    else if (mode == "deadend")
                    {
                        stage = "k_setup";
                    }
                    else if (mode == "slabremove")
                    {
                        stage = "z_setup";
                    }
                    else if (mode == "staircolour")
                    {
                        stage = "q_setup";
                    }
                    else if (mode == "slabstuff")
                    {
                        stage = "p_setup";
                    }
                    else if (mode == "thick")
                    {
                        stage = "t_setup";
                    }
                    else if (mode == "drops")
                    {
                        stage = "a_setup";
                    }
                    else if (mode == "qol1")
                    {
                        stage = "o_setup";
                    }
                    else if (mode == "bugs4")
                    {
                        stage = "y_setup";
                    }
                    else if (mode == "space")
                    {
                        stage = "e_setup";
                    }
                    else if (mode == "power")
                    {
                        stage = "n_setup";
                    }
                    else if (mode == "riser")
                    {
                        stage = "j_setup";
                    }
                    else if (mode == "range")
                    {
                        stage = "g_setup";
                    }
                    else if (mode == "light")
                    {
                        stage = "l_setup";
                    }
                    else if (mode == "raids")
                    {
                        FireIncidents(map, raidsOnly: true);
                        Finish();
                    }
                    else if (mode == "gen")
                    {
                        // D3: temporary maps get the annex too.
                        // D3: temporary maps get the annex too. Generate an enemy settlement's map (its own genSteps).
                        Settlement s = Find.WorldObjects.Settlements.Where(x => x.Faction != null && !x.Faction.IsPlayer && !x.HasMap && x.Tile.Layer == map.Tile.Layer).OrderBy(x => Find.WorldGrid.ApproxDistanceInTiles(x.Tile, map.Tile)).FirstOrDefault();
                        if (s == null)
                        {
                            Result("temporary map", false, "no settlement found");
                        }
                        else
                        {
                            Map temp = GetOrGenerateMapUtility.GetOrGenerateMap(s.Tile, null);
                            Info($"temporary map: {s.Label} ({s.Faction?.Name}), generator {temp.generatorDef?.defName}, size {temp.Size}");
                            GenCheck(temp, "temporary map");
                            FinderStats(temp);
                        }
                        StartTicks(2500, "shot");
                    }
                    else
                    {
                        stage = "incidents";
                    }
                    break;
                case "incidents":
                    AnnexWatch.Reset();
                    FireIncidents(map);
                    StartTicks(2500, "incidents_check");
                    break;
                case "incidents_check":
                    WatchCheck(map, "incidents (+2500 ticks)");
                    StartTicks(Mathf.RoundToInt(days * GenDate.TicksPerDay), "ff_check");
                    clock.Restart();
                    break;
                case "ff_check":
                    Info($"fast-forward {days} days: {ticksTotal} ticks in {clock.Elapsed.TotalSeconds:0.0}s");
                    WatchCheck(map, $"fast-forward {days} days");
                    GenCheck(map, $"after {days} days");
                    stage = "camera";
                    break;
                case "camera":
                    Find.CameraDriver.SetRootPosAndSize(new Vector3(map.Size.x / 2f, 0f, map.Size.z - 3f), 20f);
                    Wait(5, "camera_check");
                    break;
                case "camera_check":
                {
                    Vector3 pos = Traverse.Create(Find.CameraDriver).Field("rootPos").GetValue<Vector3>();
                    int gz = ProjectZ.GroundZ(map);
                    Result("camera clamp", !ProjectZ.HasAnnex(map) || pos.z <= gz - 2f + 0.01f, $"asked z {map.Size.z - 3}, camera z {pos.z:0.0}, ground rows {gz}");
                    stage = "shot";
                    break;
                }
                case "shot":
                {
                    CameraDriver_Update_Patch.AllowAnnexView = true;
                    Find.UIRoot.screenshotMode.Active = true;
                    Find.CameraDriver.config.sizeRange.max = 400f;
                    float size = Mathf.Max(map.Size.z / 2f + 4f, map.Size.x / 2f / Mathf.Max(0.5f, (float)UI.screenWidth / UI.screenHeight) + 4f);
                    Find.CameraDriver.SetRootPosAndSize(new Vector3(map.Size.x / 2f, 0f, map.Size.z / 2f), size);
                    Wait(20, "shot_take");
                    break;
                }
                case "shot_take":
                    Capture(mode == "gen" ? "gen_overview" : "m1a_overview");
                    stage = "shot_done";
                    break;
                case "shot_done":
                    CameraDriver_Update_Patch.AllowAnnexView = false;
                    Find.UIRoot.screenshotMode.Active = false;
                    if (mode == "gen")
                    {
                        WatchCheck(map, "2500 ticks");
                        Finish();
                    }
                    else
                    {
                        stage = "save";
                    }
                    break;
                case "save":
                    savedTicks = Find.TickManager.TicksGame;
                    GameDataSaveLoader.SaveGame("pz_annex");
                    Result("saved pz_annex", File.Exists(GenFilePaths.FilePathForSavedGame("pz_annex")), "");
                    stage = "load_annex";
                    pendingLoad = "pz_annex";
                    break;
                case "load_annex":
                    Info("reloaded pz_annex");
                    Result("reload: same game", Math.Abs(ticksAfterLoad - savedTicks) <= 10, $"ticks on load {ticksAfterLoad} vs saved {savedTicks}");
                    GenCheck(map, "after reload");
                    AnnexWatch.Reset();
                    StartTicks(2500, "load_annex2");
                    break;
                case "load_annex2":
                    WatchCheck(map, "reloaded save +2500 ticks");
                    if (File.Exists(GenFilePaths.FilePathForSavedGame("pz_plain")))
                    {
                        stage = "load_plain";
                        pendingLoad = "pz_plain";
                    }
                    else
                    {
                        Result("plain save present", false, "run mode plain first");
                        Finish();
                    }
                    break;
                case "load_plain":
                    PlainCheck(map);
                    Info($"plain save loaded at game tick {Find.TickManager.TicksGame}");
                    AnnexWatch.Reset();
                    StartTicks(2500, "load_plain2");
                    break;
                case "load_plain2":
                    Info($"plain save +2500 ticks: {AnnexWatch.Spawns} spawns, game tick now {Find.TickManager.TicksGame}, pawns {map.mapPawns.AllPawnsSpawnedCount}");
                    Result("plain save still has no annex", !ProjectZ.HasAnnex(map), $"size {map.Size}");
                    Finish();
                    break;
            }
        }

        private static int savedTicks;
        private static int ticksAfterLoad;

        private static void StartTicks(int n, string next)
        {
            ticksLeft = n;
            ticksTotal = n;
            stage = next;
        }

        private static void DoTicks(Map map)
        {
            Stopwatch sw = Stopwatch.StartNew();
            TickManager tm = Find.TickManager;
            while (ticksLeft > 0 && sw.ElapsedMilliseconds < 200)
            {
                tm.DoSingleTick();
                ticksLeft--;
                if (tickWatch != null && (ticksTotal - ticksLeft) % watchEvery == 0)
                {
                    TickHook(map);
                    if (tickWatch())
                    {
                        Out($"  condition met after {ticksTotal - ticksLeft} of {ticksTotal} ticks");
                        ticksTotal -= ticksLeft;
                        ticksLeft = 0;
                        break;
                    }
                }
                if ((ticksTotal - ticksLeft) % 15000 == 0)
                {
                    Out($"  tick {ticksTotal - ticksLeft}/{ticksTotal} (game day {GenDate.DaysPassedFloat:0.00}), pawns {map.mapPawns.AllPawnsSpawnedCount}, annex spawns so far {AnnexWatch.AnnexSpawns}");
                }
            }
        }

        private static void Capture(string name)
        {
            string path = Path.Combine(outDir, name + ".png");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            ScreenCapture.CaptureScreenshot(path);
            pendingShot = path;
            pendingSince = Time.frameCount;
        }

        private static void Finish()
        {
            stage = "done";
            results.Add($"SUMMARY fails={fails} errors={errors} warnings={warnings}");
            Out($"SUMMARY fails={fails} errors={errors} warnings={warnings}");
            try
            {
                File.WriteAllLines(Path.Combine(outDir, "results.txt"), results);
                File.WriteAllText(Path.Combine(outDir, "done.txt"), DateTime.Now.ToString("o") + $" fails={fails} errors={errors}");
            }
            catch (Exception e)
            {
                Out("write failed: " + e);
            }
            Root.Shutdown();
        }

        // ------------------------------------------------------------------ checks

        private static void GenCheck(Map map, string when)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int floors = FloorsOverride > 0 ? FloorsOverride : ProjectZ.FloorsForNewMaps;
            if (a == null)
            {
                Result("annex present " + when, false, $"map {map.Size} has no annex");
                return;
            }
            int gz = a.groundZ;
            int sx = map.Size.x;
            Result("map size " + when, map.Size.z == a.floors * a.stride && a.stride % 17 == 0 && a.stride > gz && a.floors == floors,
                $"size {map.Size}, ground {sx}x{gz}, stride H={a.stride}, floors {a.floors} (expected {floors}); gap rows per band {a.stride - gz}");
            int annexCells = 0, notAir = 0, things = 0, roofs = 0, fogged = 0, snow = 0, plants = 0, pawns = 0, filth = 0;
            Dictionary<string, int> thingDefs = new Dictionary<string, int>();
            for (int z = gz; z < map.Size.z; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    annexCells++;
                    if (c.GetTerrain(map) != PZDefOf.PZ_OpenAir)
                    {
                        notAir++;
                    }
                    List<Thing> list = map.thingGrid.ThingsListAtFast(c);
                    for (int i = 0; i < list.Count; i++)
                    {
                        things++;
                        string k = list[i].def.defName;
                        thingDefs[k] = thingDefs.TryGetValue(k, out int n) ? n + 1 : 1;
                        if (list[i] is Plant)
                        {
                            plants++;
                        }
                        if (list[i] is Pawn)
                        {
                            pawns++;
                        }
                        if (list[i] is Filth)
                        {
                            filth++;
                        }
                    }
                    if (map.roofGrid.Roofed(c))
                    {
                        roofs++;
                    }
                    if (map.fogGrid.IsFogged(c))
                    {
                        fogged++;
                    }
                    if (map.snowGrid.GetDepth(c) > 0f)
                    {
                        snow++;
                    }
                }
            }
            Result("annex empty " + when, notAir == 0 && things == 0 && roofs == 0 && fogged == 0 && snow == 0,
                $"{annexCells} annex cells: non-open-air {notAir}, things {things} (plants {plants}, pawns {pawns}, filth {filth}) {string.Join(",", thingDefs.Select(kv => kv.Key + "x" + kv.Value))}, roofed {roofs}, fogged {fogged}, snow {snow}");
            // The ground itself was generated normally, right up to its top row.
            int topAir = 0, river = 0, road = 0, water = 0;
            Dictionary<string, int> topTerrain = new Dictionary<string, int>();
            for (int x = 0; x < sx; x++)
            {
                TerrainDef t = new IntVec3(x, 0, gz - 1).GetTerrain(map);
                if (t == PZDefOf.PZ_OpenAir)
                {
                    topAir++;
                }
                topTerrain[t.defName] = topTerrain.TryGetValue(t.defName, out int n) ? n + 1 : 1;
            }
            int groundThings = 0;
            for (int z = 0; z < gz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    TerrainDef t = c.GetTerrain(map);
                    if (t.IsRiver)
                    {
                        river++;
                    }
                    if (t.IsRoad)
                    {
                        road++;
                    }
                    if (t.IsWater)
                    {
                        water++;
                    }
                    groundThings += map.thingGrid.ThingsListAtFast(c).Count;
                }
            }
            Result("ground generated to its top row " + when, topAir == 0, $"top row z={gz - 1}: {string.Join(",", topTerrain.Select(kv => kv.Key + "x" + kv.Value))}");
            Info($"ground {when}: river cells {river}, road cells {road}, water {water}, things {groundThings}, mutators {string.Join(",", map.TileInfo.Mutators.Select(m => m.defName))}, biome {map.Biome.defName}, generator {map.generatorDef?.defName}");
            Info($"floors: {string.Join("; ", Enumerable.Range(0, a.floors).Select(k => $"floor {k} rows {k * a.stride}..{k * a.stride + gz - 1}"))}");
            // predicate round trips
            IntVec3 g = new IntVec3(7, 0, gz - 1);
            IntVec3 f1 = ProjectZ.CellOnFloor(g, 1, map);
            IntVec3 gap = new IntVec3(7, 0, gz);
            bool rt = ProjectZ.FloorOf(g, map) == 0 && ProjectZ.FloorOf(f1, map) == 1 && ProjectZ.GroundCellOf(f1, map) == g && ProjectZ.FloorOf(gap, map) == -1 && !ProjectZ.GroundCellOf(gap, map).IsValid && ProjectZ.IsAnnex(gap, map) && !ProjectZ.IsAnnex(g, map)
                && (a.floors < 3 || ProjectZ.GroundCellOf(ProjectZ.CellOnFloor(g, a.floors - 1, map), map) == g) && !ProjectZ.CellOnFloor(g, a.floors, map).IsValid;
            Result("predicates", rt, $"ground {g} -> floor1 {f1}; gap {gap} floor {ProjectZ.FloorOf(gap, map)}");
            // rooms/regions: the outdoor ground region on the top row touches the map edge (counts as outdoors)
            Region topReg = null;
            for (int x = 0; x < sx && topReg == null; x++)
            {
                IntVec3 c = new IntVec3(x, 0, gz - 1);
                if (c.Standable(map))
                {
                    topReg = c.GetRegion(map);
                }
            }
            Result("top-row region touches the edge " + when, topReg == null || topReg.touchesMapEdge, topReg == null ? "no standable top-row cell" : $"{topReg} outdoors={topReg.Room?.PsychologicallyOutdoors}");
            IntVec3 voidCell = new IntVec3(sx / 2, 0, gz + 1);
            Region voidReg = voidCell.GetRegion(map, RegionType.Set_All);
            Info($"void region at {voidCell}: {voidReg?.type} touchesMapEdge={voidReg?.touchesMapEdge}");
        }

        private static void FinderStats(Map map)
        {
            const int N = 20000;
            Stat(map, "CellFinder.RandomCell", N, () => CellFinder.RandomCell(map));
            Stat(map, "CellFinderLoose.RandomCellWith(any)", N, () => CellFinderLoose.RandomCellWith(c => true, map));
            Stat(map, "CellFinder.TryFindRandomCell(any)", N, () => CellFinder.TryFindRandomCell(map, c => true, out IntVec3 r) ? r : IntVec3.Invalid);
            Stat(map, "CellFinder.RandomEdgeCell", N, () => CellFinder.RandomEdgeCell(map));
            foreach (Rot4 dir in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
            {
                Stat(map, "CellFinder.RandomEdgeCell(" + dir.ToStringHuman() + ")", N / 4, () => CellFinder.RandomEdgeCell(dir, map));
            }
            Stat(map, "CellFinder.TryFindRandomEdgeCellWith(any)", N, () => CellFinder.TryFindRandomEdgeCellWith(c => true, map, 0f, out IntVec3 r) ? r : IntVec3.Invalid);
            Stat(map, "TryFindRandomEdgeCellWith(top row only)", 200, () => CellFinder.TryFindRandomEdgeCellWith(c => c.z >= ProjectZ.GroundZ(map) - 1, map, 0f, out IntVec3 r) ? r : IntVec3.Invalid, mustHitTopRow: true);
            Stat(map, "CellFinder.RandomNotEdgeCell(10)", N, () => CellFinder.RandomNotEdgeCell(10, map));
            Stat(map, "RCellFinder.TryFindRandomPawnEntryCell", 2000, () => RCellFinder.TryFindRandomPawnEntryCell(out IntVec3 r, map, 0.2f) ? r : IntVec3.Invalid);
            Stat(map, "DropCellFinder.RandomDropSpot", 2000, () => DropCellFinder.RandomDropSpot(map));
            Stat(map, "DropCellFinder.TryFindRaidDropCenterClose", 500, () => DropCellFinder.TryFindRaidDropCenterClose(out IntVec3 r, map) ? r : IntVec3.Invalid);
            Stat(map, "DropCellFinder.FindRaidDropCenterDistant", 30, () => DropCellFinder.FindRaidDropCenterDistant(map));
            Stat(map, "lightning strike cell (RandomCellWith standable unroofed)", 2000, () => CellFinderLoose.RandomCellWith(sq => sq.Standable(map) && !map.roofGrid.Roofed(sq), map));
            Stat(map, "CellFinderLoose.TryFindSkyfallerCell(meteorite)", 300, () => CellFinderLoose.TryFindSkyfallerCell(ThingDefOf.MeteoriteIncoming, map, TerrainAffordanceDefOf.Walkable, out IntVec3 r, 10, default, -1, allowRoofedCells: true) ? r : IntVec3.Invalid);
            Stat(map, "map.cellsInRandomOrder (all cells)", 1, () => IntVec3.Invalid);
            int gz = ProjectZ.GroundZ(map);
            IntVec3 center = map.Center;
            Result("Map.Center on the ground", center.z < gz && Mathf.Abs(center.z - gz / 2) <= 1, $"center {center}, ground rows {gz}");
            bool edgeTop = new IntVec3(5, 0, gz - 1).OnEdge(map) && !new IntVec3(5, 0, gz - 2).OnEdge(map) && !new IntVec3(5, 0, gz).OnEdge(map) && !new IntVec3(0, 0, gz + 5).OnEdge(map) && new IntVec3(5, 0, gz - 1).OnEdge(map, Rot4.South) && new IntVec3(5, 0, 0).OnEdge(map, Rot4.North);
            Result("OnEdge: ground top row is the north edge, annex never", edgeTop, "");
            Result("DistanceToEdge top row", new IntVec3(50, 0, gz - 1).DistanceToEdge(map) == 0 && new IntVec3(50, 0, gz - 4).DistanceToEdge(map) == 3, $"{new IntVec3(50, 0, gz - 4).DistanceToEdge(map)}");
            int cellsInRandomOrder = map.cellsInRandomOrder.GetAll().Count;
            Result("cellsInRandomOrder covers the full map", cellsInRandomOrder == map.Area, $"{cellsInRandomOrder} of {map.Area}");
            int exitCells = 0, exitTop = 0, exitAnnex = 0;
            if (map.exitMapGrid.MapUsesExitGrid)
            {
                foreach (IntVec3 c in map.AllCells)
                {
                    if (map.exitMapGrid.IsExitCell(c))
                    {
                        exitCells++;
                        if (c.z >= gz)
                        {
                            exitAnnex++;
                        }
                        else if (c.z >= gz - 3)
                        {
                            exitTop++;
                        }
                    }
                }
                Result("exit grid on the ground only", exitAnnex == 0 && exitTop > 0, $"{exitCells} exit cells, {exitTop} in the top 3 ground rows, {exitAnnex} in the annex");
            }
            else
            {
                Info("exit grid not used on this map (home map)");
            }
        }

        private static void Stat(Map map, string name, int n, Func<IntVec3> pick, bool mustHitTopRow = false)
        {
            int gz = ProjectZ.GroundZ(map);
            int annex = 0, invalid = 0, topRow = 0, maxZ = -1;
            for (int i = 0; i < n; i++)
            {
                IntVec3 c = pick();
                if (!c.IsValid)
                {
                    invalid++;
                    continue;
                }
                if (ProjectZ.IsAnnex(c, map))
                {
                    annex++;
                }
                if (c.z == gz - 1)
                {
                    topRow++;
                }
                maxZ = Mathf.Max(maxZ, c.z);
            }
            bool ok = annex == 0 && (!mustHitTopRow || topRow > 0);
            if (name.StartsWith("map.cellsInRandomOrder"))
            {
                return;
            }
            Result(name, ok, $"{n} picks: annex {annex}, invalid {invalid}, on top ground row {topRow}, max z {maxZ} (ground rows 0..{gz - 1})");
        }

        private static void FireIncidents(Map map, bool raidsOnly = false)
        {
            int fired = 0, failed = 0;
            List<string> report = new List<string>();
            foreach (PawnsArrivalModeDef arrive in new[] { PawnsArrivalModeDefOf.EdgeWalkIn, PawnsArrivalModeDefOf.EdgeDrop, PawnsArrivalModeDefOf.CenterDrop, PawnsArrivalModeDefOf.RandomDrop, PawnsArrivalModeDefOf.EdgeWalkInGroups })
            {
                for (int i = 0; i < (raidsOnly ? 10 : 3); i++)
                {
                    IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
                    parms.faction = Find.FactionManager.RandomEnemyFaction(allowNonHumanlike: false);
                    parms.raidStrategy = RaidStrategyDefOf.ImmediateAttack;
                    parms.raidArrivalMode = arrive;
                    parms.points = 400f;
                    parms.forced = true;
                    Faction faction = parms.faction;
                    bool ok = IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);
                    if (ok)
                    {
                        fired++;
                    }
                    else
                    {
                        failed++;
                        // Why: the raid's points after the arrival mode's factor against the cheapest group the faction can
                        // field. Map-independent: decided before any cell is looked at.
                        float adjusted = IncidentWorker_Raid.AdjustedRaidPoints(400f, arrive, RaidStrategyDefOf.ImmediateAttack, faction, PawnGroupKindDefOf.Combat, map);
                        float min = faction?.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat) ?? -1f;
                        string why = $"RaidEnemy/{arrive.defName} failed: faction {faction?.Name} ({faction?.def.defName}, {faction?.def.techLevel}), points 400 -> {adjusted:0} after the arrival factor, cheapest combat group {min:0}, age restriction {parms.raidAgeRestriction?.defName ?? "none"}, arrival now {parms.raidArrivalMode?.defName}, spawn centre {parms.spawnCenter} (annex {ProjectZ.IsAnnex(parms.spawnCenter, map)})";
                        Out(why);
                        Info(why + (adjusted < min ? " => not enough points (vanilla, map-independent)" : " => points suffice: look further"));
                    }
                    report.Add("RaidEnemy/" + arrive.defName + (ok ? "" : "(failed)") + "[" + faction?.def.defName + "]");
                }
            }
            if (raidsOnly)
            {
                Info($"raids fired {fired}, failed {failed}: {string.Join(", ", report)}");
                return;
            }
            foreach (string defName in new[] { "ManhunterPack", "HerdMigration", "FarmAnimalsWanderIn", "ThrumboPasses", "ResourcePodCrash", "ShipChunkDrop", "MeteoriteImpact", "TraderCaravanArrival", "WildManWandersIn" })
            {
                IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail(defName);
                if (def == null)
                {
                    report.Add(defName + "(missing)");
                    continue;
                }
                for (int i = 0; i < 4; i++)
                {
                    IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, map);
                    parms.forced = true;
                    if (def.category == IncidentCategoryDefOf.ThreatBig || def.category == IncidentCategoryDefOf.ThreatSmall)
                    {
                        parms.points = 300f;
                    }
                    bool ok = false;
                    try
                    {
                        ok = def.Worker.TryExecute(parms);
                    }
                    catch (Exception e)
                    {
                        Out(defName + " threw " + e.Message);
                    }
                    if (ok)
                    {
                        fired++;
                    }
                    else
                    {
                        failed++;
                    }
                    report.Add(defName + (ok ? "" : "(failed)"));
                }
            }
            for (int i = 0; i < 40; i++)
            {
                map.weatherManager.eventHandler.AddEvent(new WeatherEvent_LightningStrike(map));
            }
            report.Add("LightningStrike x40");
            // wild animals: the spawner's own entry-cell search, the same call its tick makes
            int animals = 0;
            for (int i = 0; i < 20; i++)
            {
                if (RCellFinder.TryFindRandomPawnEntryCell(out IntVec3 c, map, CellFinder.EdgeRoadChance_Animal, allowFogged: true, cell => map.reachability.CanReachMapEdge(cell, TraverseParms.For(TraverseMode.NoPassClosedDoors).WithFenceblocked(forceFenceblocked: true)))
                    && map.wildAnimalSpawner.SpawnRandomWildAnimalAt(c, canFlyIn: false))
                {
                    animals++;
                }
            }
            report.Add("wild animal groups x" + animals);
            Info($"incidents fired {fired}, failed {failed}: {string.Join(", ", report)}");
        }

        private static void WatchCheck(Map map, string when)
        {
            int pawnsInAnnex = map.mapPawns.AllPawnsSpawned.Count(p => ProjectZ.IsAnnex(p.Position, map) && (ProjectZ.FloorOf(p.Position, map) < 1 || p.Position.GetTerrain(map) == PZDefOf.PZ_OpenAir));
            Result("nothing spawned in the annex: " + when, AnnexWatch.AnnexSpawns == 0 && pawnsInAnnex == 0,
                $"{AnnexWatch.Spawns} spawns ({string.Join(",", AnnexWatch.SpawnsByCategory.Select(kv => kv.Key + "x" + kv.Value))}), annex spawns {AnnexWatch.AnnexSpawns} (+{AnnexWatch.AnnexMotes} visual motes) {string.Join(",", AnnexWatch.AnnexByDef.Select(kv => kv.Key + "x" + kv.Value))}, pawns now in the annex off built floors {pawnsInAnnex}, pawns on map {map.mapPawns.AllPawnsSpawnedCount}");
            Info($"weather now {map.weatherManager.curWeather?.defName}, game day {GenDate.DaysPassedFloat:0.00}, errors so far {errors}, warnings {warnings}");
        }

        private static void PlainCheck(Map map)
        {
            Result("plain save: no annex", !ProjectZ.HasAnnex(map) && map.GetComponent<AnnexMapComponent>() != null, $"size {map.Size}, component floors {map.GetComponent<AnnexMapComponent>()?.floors}");
            int maxZ = 0;
            for (int i = 0; i < 20000; i++)
            {
                maxZ = Mathf.Max(maxZ, CellFinder.RandomCell(map).z);
            }
            IntVec3 center = map.Center;
            bool vanilla = maxZ >= map.Size.z - 3 && new IntVec3(5, 0, map.Size.z - 1).OnEdge(map) && center == new IntVec3(map.Size.x / 2, 0, map.Size.z / 2)
                && ProjectZ.GroundZ(map) == map.Size.z && !ProjectZ.IsAnnex(new IntVec3(1, 0, map.Size.z - 1), map);
            Result("plain save: vanilla behaviour", vanilla, $"RandomCell max z {maxZ} of {map.Size.z - 1}, centre {center}");
            Find.CameraDriver.SetRootPosAndSize(new Vector3(map.Size.x / 2f, 0f, map.Size.z - 3f), 20f);
        }
    }

}
