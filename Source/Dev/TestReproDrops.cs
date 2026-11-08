using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using ProjectZ.Patches;

namespace ProjectZ.Dev
{
    // Mode reprodrops (bug report: "ship chunks fall onto the 2nd floor while the 3rd floor slab above is still intact").
    // REPORT ONLY: no checks of a design here, a sweep that records every skyfaller landing. Run with -projectzfloors=3
    // -projectzmapsize=120 -projectzseed=717 (optional -projectzxdruns=<factor%>, -projectzxdonly=<source,...>,
    // -projectzxdnodiscover).
    // Scene (real construction state as the other tests: PZ_UpperFloor terrain, walls, roofs):
    //   L  a large ground room (36 x 30) with a floor-2 slab, a walled floor-2 room on it and a floor-3 slab over that
    //      (ground and floor 2 both under a slab; floor 3 an open roof area), columns every 6 cells on both levels;
    //   AT an atrium strip east of L: the floor-3 slab runs on over open air on floor 2 (ground under it: the stairwell
    //      roof, floor 2 there: the floor's roof over open air);
    //   P  a house: ground room under a floor-2 slab, on floor 2 a walled room with a normal thin roof (no slab above);
    //   G  a plain roofed ground room; open ground all round; an orbital trade beacon on open ground.
    // Every skyfaller- or pod-making source is fired through the game's own incident workers (or the trade deal), with a
    // fixed seed per run, and ticked until every skyfaller of the run has landed. A test-only Harmony patch on
    // Skyfaller.SpawnSetup records each one (cell, floor, roof, slab above, DropRules verdict, stack trace) and on
    // Skyfaller.SpawnThings where its contents went; DropCellFinder's two drop checks are counted per run (were they asked
    // at all?). After a run the scene is restored (new things removed, terrain, roofs and walls as built).
    // Output: results.txt, landings.tsv, traces.txt and screenshots of the first violations (3D landing floor, 3D top
    // floor, 2D).
    public static partial class TestRunner
    {
        private sealed class XdSource
        {
            public string name;
            public int runs;
            public int maxTicks = 1500;      // after firing: tick until no skyfaller of the run is in the air (and this many at most)
            public int minTicks = 0;         // tick at least this long (late skyfallers: game conditions, quests, spawners)
            public Func<Map, bool> fire;
            public int fired, failed, skyfallers, violations;
            public string note = "";
        }

        private sealed class XdLanding
        {
            public string source;
            public int run;
            public Skyfaller sky;
            public string def;
            public IntVec3 cell;
            public int floor;
            public string roofAtSpawn;
            public bool blocked, kept;
            public string slabAbove;      // floors above with a slab in this column ("" none)
            public string footprintBlocked;
            public string trace;
            public string roofAtImpact = "?";
            public string roofAfter = "?";
            public bool slabStill = true;
            public bool impacted;
            public List<Thing> contents = new List<Thing>();
            public string contentsAtImpact = "";
            public string contentsAfter = "";
            public int physChecks, landChecks;
        }

        private static readonly List<XdSource> xdSources = new List<XdSource>();
        private static readonly List<XdLanding> xdLandings = new List<XdLanding>();
        private static readonly Dictionary<Skyfaller, XdLanding> xdBySky = new Dictionary<Skyfaller, XdLanding>();
        private static int xdSrc, xdRun, xdTicked;
        private static string xdCurSource = "(setup)";
        private static bool xdRecording;
        private static int xdPhys, xdPhysFalse, xdLand, xdLandFalse;
        private static HashSet<int> xdBeforeIds = new HashSet<int>();
        private static HashSet<int> xdQuestsBefore = new HashSet<int>();
        private static IntVec3 xdO;
        private static CellRect xdArea, xdL, xdAt, xdP, xdG;
        private static RoofDef[] xdRoofs;
        private static TerrainDef[] xdTerrain;
        private static readonly List<(IntVec3 c, ThingDef def, ThingDef stuff, Rot4 rot)> xdEdifices = new List<(IntVec3, ThingDef, ThingDef, Rot4)>();
        private static int xdSlabs, xdKept;
        private static readonly List<(int floor, IntVec3 g, float size, string name, bool view3d)> xdShots = new List<(int, IntVec3, float, string, bool)>();
        private static readonly HashSet<string> xdShotKinds = new HashSet<string>();
        private static bool xdPatched;
        private static Pawn xdNegotiator;
        private static Building xdBeacon;

        private static IntVec3 XA(int dx, int dz) => new IntVec3(xdO.x + dx, 0, xdO.z + dz);

        private static IntVec3 XF(int dx, int dz, int k, Map map) => ProjectZ.CellOnFloor(XA(dx, dz), k, map);

        private static string XRoof(IntVec3 c, Map map) => c.InBounds(map) ? map.roofGrid.RoofAt(c)?.defName ?? "none" : "oob";

        private static string XSlabAbove(IntVec3 c, Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int k = ProjectZ.FloorOf(c, map);
            if (a == null || k < 0)
            {
                return "";
            }
            IntVec3 g = ProjectZ.GroundCellOf(c, map);
            List<string> l = new List<string>();
            for (int j = k + 1; j <= a.MaxLevel; j++)
            {
                IntVec3 u = ProjectZ.CellOnFloor(g, j, map);
                if (u.IsValid && ProjectZ.HasFloor(u, map))
                {
                    l.Add((j + 1).ToString());
                }
            }
            return string.Join("+", l);
        }

        private static string XWhere(IntVec3 c, Map map)
        {
            IntVec3 g = ProjectZ.GroundCellOf(c, map);
            string zone = !g.IsValid ? "?" : xdL.Contains(g) ? "L" : xdAt.Contains(g) ? "AT" : xdP.Contains(g) ? "P" : xdG.Contains(g) ? "G" : xdArea.Contains(g) ? "scene-open" : "outside";
            return $"{c} floor {ProjectZ.FloorOf(c, map) + 1} [{zone}] roof {XRoof(c, map)}";
        }

        // ---------------------------------------------------------------- recording (test-only patches)

        private static void XdPatch()
        {
            if (xdPatched)
            {
                return;
            }
            xdPatched = true;
            Harmony h = new Harmony("Peren.IsoRimProjectZ.test.reprodrops");
            h.Patch(AccessTools.Method(typeof(Skyfaller), nameof(Skyfaller.SpawnSetup)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(XdSpawnPostfix)));
            foreach (Type t in new[] { typeof(Skyfaller) }.Concat(typeof(Skyfaller).AllSubclassesNonAbstract()))
            {
                System.Reflection.MethodInfo m = AccessTools.DeclaredMethod(t, "SpawnThings");
                if (m != null)
                {
                    h.Patch(m, prefix: new HarmonyMethod(typeof(TestRunner), nameof(XdSpawnThingsPrefix)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(XdSpawnThingsPostfix)));
                }
            }
            h.Patch(AccessTools.Method(typeof(CellFinderLoose), nameof(CellFinderLoose.TryFindSkyfallerCell)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(XdSkyCellPostfix)));
            h.Patch(AccessTools.Method(typeof(CellFinder), nameof(CellFinder.TryFindRandomCellNear)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(XdNearPrefix)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(XdNearPostfix)));
            h.Patch(AccessTools.Method(typeof(DropCellFinder), nameof(DropCellFinder.CanPhysicallyDropInto)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(XdPhysPostfix)));
            h.Patch(AccessTools.Method(typeof(DropCellFinder), nameof(DropCellFinder.SkyfallerCanLandAt)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(XdLandPostfix)));
        }

        // The finders' own answers this run (CellFinderLoose.TryFindSkyfallerCell and the TryFindRandomCellNear it uses):
        // the rect searched and the cell chosen, for the trace.
        private static readonly List<string> xdFinds = new List<string>();


        public static void XdSkyCellPostfix(ThingDef skyfaller, Map map, IntVec3 cell, IntVec3 nearLoc, int nearLocMaxDist, bool allowRoofedCells, bool __result)
        {
            if (xdRecording && map != null)
            {
                xdFinds.Add($"TryFindSkyfallerCell({skyfaller?.defName}, nearLoc {nearLoc}, nearLocMaxDist {nearLocMaxDist}, allowRoofedCells {allowRoofedCells}) -> {__result} {(cell.IsValid && cell.InBounds(map) ? XWhere(cell, map) + " DropRules.Blocked " + DropRules.Blocked(cell, map) : cell.ToString())}");
            }
        }

        public static void XdNearPrefix(IntVec3 root, Map map, int squareRadius)
        {
            if (xdRecording && map != null)
            {
                int x0 = Math.Max(0, root.x - squareRadius), z0 = Math.Max(0, root.z - squareRadius);
                long x1 = Math.Min((long)map.Size.x, (long)root.x + squareRadius), z1 = Math.Min((long)map.Size.z, (long)root.z + squareRadius);
                xdFinds.Add($"  TryFindRandomCellNear(root {root}, squareRadius {squareRadius}): samples x {x0}..{x1}, z {z0}..{z1} (map {map.Size}, ground rows 0..{ProjectZ.GroundZ(map) - 1})");
            }
        }

        public static void XdNearPostfix(IntVec3 result, Map map, bool __result)
        {
            if (xdRecording && map != null)
            {
                xdFinds.Add($"  TryFindRandomCellNear -> {__result} {(result.IsValid && result.InBounds(map) ? XWhere(result, map) : result.ToString())}");
            }
        }

        private static void XdPhysPostfix(bool __result)
        {
            xdPhys++;
            xdPhysFalse += __result ? 0 : 1;
        }

        private static void XdLandPostfix(bool __result)
        {
            xdLand++;
            xdLandFalse += __result ? 0 : 1;
        }

        public static void XdSpawnPostfix(Skyfaller __instance, Map map, bool respawningAfterLoad)
        {
            if (!xdRecording || respawningAfterLoad || map == null)
            {
                return;
            }
            try
            {
                IntVec3 c = __instance.Position;
                List<string> fb = GenAdj.OccupiedRect(c, __instance.Rotation, __instance.def.size).Cells.Where(x => DropRules.Blocked(x, map)).Select(x => x.ToString()).ToList();
                XdLanding l = new XdLanding
                {
                    source = xdCurSource,
                    run = xdRun,
                    sky = __instance,
                    def = __instance.def.defName + "(" + string.Join(",", __instance.innerContainer.Select(t => t.def.defName).Take(3)) + ")",
                    cell = c,
                    floor = ProjectZ.FloorOf(c, map),
                    roofAtSpawn = XRoof(c, map),
                    blocked = DropRules.Blocked(c, map),
                    kept = FloorRoof.Kept(c, map),
                    slabAbove = XSlabAbove(c, map),
                    footprintBlocked = string.Join(" ", fb),
                    trace = new StackTrace(1, false).ToString(),
                    physChecks = xdPhys,
                    landChecks = xdLand,
                };
                xdLandings.Add(l);
                xdBySky[__instance] = l;
            }
            catch (Exception e)
            {
                Out("xd spawn record failed: " + e);
            }
        }

        public static void XdSpawnThingsPrefix(Skyfaller __instance)
        {
            if (!xdBySky.TryGetValue(__instance, out XdLanding l) || l.impacted)
            {
                return;
            }
            l.impacted = true;
            l.roofAtImpact = XRoof(__instance.Position, __instance.Map);
            List<Thing> all = new List<Thing>();
            ThingOwnerUtility.GetAllThingsRecursively(__instance, all, false);
            l.contents = all.Where(t => t != __instance).ToList();
        }

        public static void XdSpawnThingsPostfix(Skyfaller __instance)
        {
            if (!xdBySky.TryGetValue(__instance, out XdLanding l))
            {
                return;
            }
            Map map = __instance.Map;
            l.contentsAtImpact = string.Join("; ", l.contents.Where(t => t.Spawned && t.Map == map).Take(4).Select(t => t.def.defName + "@" + XWhere(t.Position, map)));
        }

        // ---------------------------------------------------------------- sources

        private static void XdSources(Map map)
        {
            xdSources.Clear();
            int pct = 100;
            if (GenCommandLine.TryGetCommandLineArg("projectzxdruns", out string rs) && int.TryParse(rs, out int r) && r > 0)
            {
                pct = r;
            }
            int N(int n) => Mathf.Max(1, n * pct / 100);
            HashSet<IncidentDef> covered = new HashSet<IncidentDef>();
            void Inc(string defName, int runs, int minTicks = 0, int maxTicks = 1500)
            {
                IncidentDef d = DefDatabase<IncidentDef>.GetNamedSilentFail(defName);
                if (d == null)
                {
                    xdSources.Add(new XdSource { name = defName, runs = 0, note = "def missing (DLC off?)" });
                    return;
                }
                covered.Add(d);
                xdSources.Add(new XdSource { name = defName, runs = N(runs), minTicks = minTicks, maxTicks = maxTicks, fire = m => XFireIncident(d, m) });
            }
            Inc("ShipChunkDrop", 150);
            Inc("MeteoriteImpact", 60);
            foreach (IncidentDef d in DefDatabase<IncidentDef>.AllDefs.Where(d => d.workerClass != null && typeof(IncidentWorker_CrashedShipPart).IsAssignableFrom(d.workerClass)).OrderBy(d => d.defName))
            {
                Inc(d.defName, 30);
            }
            foreach (IncidentDef d in DefDatabase<IncidentDef>.AllDefs.Where(d => d.workerClass != null && d.workerClass.Name == "IncidentWorker_MechCluster").OrderBy(d => d.defName))
            {
                Inc(d.defName, 25);
            }
            Inc("ResourcePodCrash", 60);
            foreach (IncidentDef d in DefDatabase<IncidentDef>.AllDefs.Where(d => (d.defName.Contains("PodCrash") || d.defName.Contains("ShuttleCrash")) && d.defName != "ResourcePodCrash").OrderBy(d => d.defName))
            {
                Inc(d.defName, 30, 120);
            }
            foreach (PawnsArrivalModeDef arrive in DefDatabase<PawnsArrivalModeDef>.AllDefs.Where(a => a.defName.Contains("Drop")).OrderBy(a => a.defName))
            {
                PawnsArrivalModeDef ar = arrive;
                xdSources.Add(new XdSource { name = "RaidEnemy/" + ar.defName, runs = N(25), fire = m => XFireRaid(m, ar) });
            }
            xdSources.Add(new XdSource { name = "OrbitalTrade(buy)", runs = N(20), fire = XFireTrade });
            foreach (string n in new[] { "OrbitalDebris", "ObeliskMutator", "ObeliskDuplicator", "ObeliskAbductor", "GoldenCubeArrival", "VoidCuriosity", "UnnaturalCorpseArrival" })
            {
                IncidentDef d = DefDatabase<IncidentDef>.AllDefs.FirstOrDefault(x => x.defName == n || x.workerClass != null && x.workerClass.Name == "IncidentWorker_" + n);
                if (d != null && !covered.Contains(d))
                {
                    Inc(d.defName, n == "OrbitalDebris" ? 6 : 15, n == "OrbitalDebris" ? 12000 : 200, n == "OrbitalDebris" ? 15000 : 1500);
                }
            }
            // everything else once (discovery: does any other incident make a skyfaller here?)
            if (!GenCommandLine.CommandLineArgPassed("projectzxdnodiscover"))
            {
                string[] skip = { "GameEndedWanderersJoin", "RaidEnemy", "RaidFriendly" };
                foreach (IncidentDef d in DefDatabase<IncidentDef>.AllDefs.Where(d => !covered.Contains(d) && d.TargetAllowed(map) && !skip.Contains(d.defName)).OrderBy(d => d.defName))
                {
                    IncidentDef dd = d;
                    xdSources.Add(new XdSource { name = "discover:" + d.defName, runs = 1, minTicks = 300, fire = m => XFireIncident(dd, m) });
                }
            }
            if (GenCommandLine.TryGetCommandLineArg("projectzxdonly", out string only))
            {
                HashSet<string> keep = new HashSet<string>(only.Split(','));
                xdSources.RemoveAll(s => !keep.Contains(s.name));
            }
            Info($"sources ({xdSources.Count}): {string.Join(", ", xdSources.Select(s => s.name + "x" + s.runs + (s.note != "" ? "(" + s.note + ")" : "")))}");
        }

        private static bool XFireIncident(IncidentDef d, Map map)
        {
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(d.category, map);
            parms.forced = true;
            if (parms.points < 400f)
            {
                parms.points = 400f;
            }
            return d.Worker.TryExecute(parms);
        }

        private static bool XFireRaid(Map map, PawnsArrivalModeDef arrive)
        {
            List<Faction> enemies = Find.FactionManager.AllFactions.Where(f => f.HostileTo(Faction.OfPlayer) && !f.def.hidden && f.def.humanlikeFaction && !f.temporary && f.def.pawnGroupMakers != null && f.def.techLevel >= TechLevel.Industrial).OrderBy(f => f.loadID).ToList();
            foreach (Faction f in enemies.InRandomOrder().ToList())
            {
                IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
                parms.faction = f;
                parms.raidStrategy = RaidStrategyDefOf.ImmediateAttack;
                parms.raidArrivalMode = arrive;
                parms.points = 500f;
                parms.forced = true;
                if (IncidentDefOf.RaidEnemy.Worker.TryExecute(parms))
                {
                    return true;
                }
            }
            return false;
        }

        // An orbital trader in orbit, the colonist on the comms console's job skipped: the trade deal itself (buy one of
        // each of up to three goods), which drops what was bought (TradeUtility.SpawnDropPod at DropCellFinder.TradeDropSpot).
        private static bool XFireTrade(Map map)
        {
            TraderKindDef kind = DefDatabase<TraderKindDef>.AllDefs.Where(t => t.orbital).OrderBy(t => t.defName).RandomElementWithFallback();
            if (kind == null || xdNegotiator == null)
            {
                return false;
            }
            TradeShip ship = new TradeShip(kind, Find.FactionManager.RandomNonHostileFaction(minTechLevel: TechLevel.Industrial));
            map.passingShipManager.AddShip(ship);
            ship.GenerateThings();
            TradeSession.SetupWith(ship, xdNegotiator, false);
            int bought = 0;
            foreach (Tradeable t in TradeSession.deal.AllTradeables.Where(t => !t.IsCurrency && t.CountHeldBy(Transactor.Trader) > 0 && t.ThingDef != null && t.ThingDef.category == ThingCategory.Item).OrderBy(t => t.ThingDef.defName).Take(3).ToList())
            {
                t.AdjustTo(1);
                bought++;
            }
            bool ok = bought > 0 && TradeSession.deal.TryExecute(out bool actually) && actually;
            TradeSession.Close();
            map.passingShipManager.RemoveShip(ship);
            return ok;
        }

        // ---------------------------------------------------------------- the stages

        private static bool StepReproDrops(Map map)
        {
            switch (stage)
            {
                case "xd_setup":
                    XdSetup(map);
                    return true;
                case "xd_next":
                    XdNext(map);
                    return true;
                case "xd_ticked":
                {
                    // the skyfallers of this run that are still in the air: more ticks
                    xdTicked += ticksTotal;
                    XdSource s = xdSources[xdSrc];
                    bool inAir = xdLandings.Any(l => l.source == s.name && l.run == xdRun && l.sky.Spawned);
                    if (inAir && xdTicked < s.maxTicks)
                    {
                        SeedTicks($"xd_more_{s.name}_{xdRun}_{xdTicked}");
                        TickUntil(s.maxTicks - xdTicked, () => !xdLandings.Any(l => l.source == s.name && l.run == xdRun && l.sky.Spawned), "xd_ticked", 5);
                        return true;
                    }
                    SeedTicks($"xd_after_{s.name}_{xdRun}");
                    StartTicks(200, "xd_post");
                    return true;
                }
                case "xd_post":
                    XdPost(map);
                    return true;
                case "xd_shot":
                {
                    if (xdShots.Count == 0)
                    {
                        XdView3D(true, map);
                        XdCleanup(map);
                        Wait(2, "xd_next");
                        return true;
                    }
                    var s = xdShots[0];
                    XdView3D(s.view3d, map);
                    ShotAt(map, s.floor, s.g, s.size);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(30, "xd_take");
                    return true;
                }
                case "xd_take":
                {
                    var s = xdShots[0];
                    xdShots.RemoveAt(0);
                    ZCapture(s.name);
                    Wait(3, "xd_shot");
                    return true;
                }
                case "xd_report":
                    XdReport(map);
                    Finish();
                    return true;
            }
            return false;
        }

        private static void XdView3D(bool on, Map map)
        {
            if (IsoRimBridge.Present)
            {
                IsoRimBridge.SetView3DActive(on);
                map.mapDrawer.RegenerateEverythingNow();
            }
        }

        private static void XdNext(Map map)
        {
            while (xdSrc < xdSources.Count && (xdSources[xdSrc].fire == null || xdRun >= xdSources[xdSrc].runs))
            {
                if (xdSources[xdSrc].fire != null)
                {
                    XdSource done = xdSources[xdSrc];
                    Out($"source {done.name}: fired {done.fired}/{done.runs}, skyfallers {done.skyfallers}, violations {done.violations}");
                }
                xdSrc++;
                xdRun = 0;
            }
            if (xdSrc >= xdSources.Count)
            {
                stage = "xd_report";
                return;
            }
            XdSource s = xdSources[xdSrc];
            xdCurSource = s.name;
            xdBeforeIds = new HashSet<int>(map.listerThings.AllThings.Select(t => t.thingIDNumber));
            xdQuestsBefore = new HashSet<int>(Find.QuestManager.QuestsListForReading.Select(q => q.id));
            xdPhys = xdPhysFalse = xdLand = xdLandFalse = 0;
            xdFinds.Clear();
            xdRecording = true;
            bool ok = false;
            Rand.PushState((int)(StableHash($"xd_{s.name}_{xdRun}") ^ (uint)(SeedArg ?? 717)));
            try
            {
                ok = s.fire(map);
            }
            catch (Exception e)
            {
                Out($"{s.name} run {xdRun}: fire threw {e.GetType().Name}: {e.Message}");
                s.note = "threw " + e.GetType().Name;
            }
            finally
            {
                Rand.PopState();
            }
            if (ok)
            {
                s.fired++;
            }
            else
            {
                s.failed++;
            }
            xdTicked = 0;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            SeedTicks($"xd_ticks_{s.name}_{xdRun}");
            int min = Math.Max(s.minTicks, 1);
            if (s.minTicks > 0)
            {
                // late skyfallers (spawners, quests): tick the minimum, then on while any is in the air
                StartTicks(min, "xd_ticked");
            }
            else
            {
                TickUntil(s.maxTicks, () => !xdLandings.Any(l => l.source == s.name && l.run == xdRun && l.sky.Spawned), "xd_ticked", 5);
            }
        }

        private static void XdPost(Map map)
        {
            tickWatch = null;
            XdSource s = xdSources[xdSrc];
            List<XdLanding> mine = xdLandings.Where(l => l.source == s.name && l.run == xdRun).ToList();
            s.skyfallers += mine.Count;
            int slabs = SlabCount(map), kept = KeptCount(map);
            bool shoot = false;
            foreach (XdLanding l in mine)
            {
                l.roofAfter = XRoof(l.cell, map);
                // the slab(s) that were above it at spawn: still there?
                IntVec3 g = ProjectZ.GroundCellOf(l.cell, map);
                foreach (string f in l.slabAbove.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    IntVec3 u = ProjectZ.CellOnFloor(g, int.Parse(f) - 1, map);
                    if (!u.IsValid || !ProjectZ.HasFloor(u, map))
                    {
                        l.slabStill = false;
                    }
                }
                l.contentsAfter = string.Join("; ", l.contents.Where(t => t.Spawned && t.Map == map).Take(6).Select(t => t.def.defName + "@" + XWhere(t.Position, map)));
                bool viol = l.blocked || l.footprintBlocked != "";
                if (viol)
                {
                    s.violations++;
                    string kind = $"{(s.name.StartsWith("discover:") ? "discover" : s.name)}_f{l.floor + 1}_{(l.slabAbove != "" ? "slab" : "noslab")}_{l.roofAtSpawn}";
                    if (xdShotKinds.Count < 8 && xdShotKinds.Add(kind) && l.impacted)
                    {
                        AnnexMapComponent a = ProjectZ.Annex(map);
                        string tag = $"reprodrops_{xdShotKinds.Count}_{XSafe(s.name)}_r{xdRun}_f{l.floor + 1}";
                        xdShots.Add((l.floor, g, 9f, tag + "_3d_viewfloor" + (l.floor + 1), true));
                        xdShots.Add((a.MaxLevel, g, 9f, tag + "_3d_viewtop", true));
                        xdShots.Add((l.floor, g, 9f, tag + "_2d_viewfloor" + (l.floor + 1), false));
                        Info($"screenshots {tag}: {s.name} run {xdRun} {l.def} at {XWhere(l.cell, map)}");
                        shoot = true;
                    }
                }
                else if (l.floor == ProjectZ.Annex(map).MaxLevel && l.impacted && xdShotKinds.Add("top_ok") && xdShotKinds.Count <= 9)
                {
                    // one legal landing on the top floor (floor 3), for comparing how it is drawn
                    string tag = $"reprodrops_ok_{XSafe(s.name)}_r{xdRun}_f{l.floor + 1}";
                    xdShots.Add((l.floor, g, 9f, tag + "_3d_viewfloor" + (l.floor + 1), true));
                    xdShots.Add((0, g, 9f, tag + "_3d_viewground", true));
                    xdShots.Add((l.floor, g, 9f, tag + "_2d_viewfloor" + (l.floor + 1), false));
                    Info($"screenshots {tag} (legal top landing for comparison): {l.def} at {XWhere(l.cell, map)}");
                    shoot = true;
                }
                string line = $"{l.source}\t{l.run}\t{l.def}\t{l.cell}\t{ProjectZ.GroundCellOf(l.cell, map)}\t{l.floor + 1}\t{XZone(l.cell, map)}\t{l.roofAtSpawn}\t{l.roofAtImpact}\t{l.roofAfter}\t{(l.blocked ? 1 : 0)}\t{(l.kept ? 1 : 0)}\t{l.footprintBlocked}\t{l.slabAbove}\t{(l.slabStill ? 1 : 0)}\t{xdPhys}/{xdPhysFalse}\t{xdLand}/{xdLandFalse}\t{l.contentsAtImpact}\t{l.contentsAfter}";
                XdTsv(line);
                if (viol)
                {
                    XdTrace($"=== VIOLATION {l.source} run {l.run} {l.def} at {XWhere(l.cell, map)}; slab above at spawn: floors {l.slabAbove}; DropRules.Blocked {l.blocked}, Kept {l.kept}; footprint blocked: {l.footprintBlocked}; CanPhysicallyDropInto calls this run {xdPhys} (false {xdPhysFalse}), SkyfallerCanLandAt calls {xdLand} (false {xdLandFalse}); roof at impact {l.roofAtImpact}, after {l.roofAfter}; slab still there {l.slabStill}; contents at impact: {l.contentsAtImpact}; contents after: {l.contentsAfter}\nfinders this run:\n{string.Join("\n", xdFinds.Take(16))}\nspawn stack:\n{l.trace}");
                }
            }
            Out($"{s.name} run {xdRun}: {mine.Count} skyfaller(s) {string.Join(" | ", mine.Select(l => l.def + " " + XWhere(l.cell, map) + (l.blocked || l.footprintBlocked != "" ? " VIOLATION" : "")))}; slabs {xdSlabs}->{slabs}, floor roofs {xdKept}->{kept}");
            if (slabs != xdSlabs || kept != xdKept)
            {
                Info($"{s.name} run {xdRun}: slab cells {xdSlabs} -> {slabs}, floor roofs {xdKept} -> {kept} (restored)");
            }
            xdRun++;
            if (shoot)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                stage = "xd_shot";
                return;
            }
            XdCleanup(map);
            Wait(1, "xd_next");
        }

        private static string XSafe(string s) => new string(s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

        private static string XZone(IntVec3 c, Map map)
        {
            IntVec3 g = ProjectZ.GroundCellOf(c, map);
            return !g.IsValid ? "?" : xdL.Contains(g) ? "L" : xdAt.Contains(g) ? "AT" : xdP.Contains(g) ? "P" : xdG.Contains(g) ? "G" : xdArea.Contains(g) ? "scene-open" : "outside";
        }

        private static void XdTsv(string line)
        {
            File.AppendAllText(Path.Combine(outDir, "landings.tsv"), line + "\n");
        }

        private static void XdTrace(string text)
        {
            File.AppendAllText(Path.Combine(outDir, "traces.txt"), text + "\n");
        }

        // New things gone, quests and conditions ended, the scene as built.
        private static void XdCleanup(Map map)
        {
            xdRecording = false;
            List<Thing> fresh = map.listerThings.AllThings.Where(t => !xdBeforeIds.Contains(t.thingIDNumber) && !(t is Pawn p && p.IsColonist)).ToList();
            foreach (Thing t in fresh.Where(t => t is Skyfaller || t is ActiveTransporter).ToList())
            {
                if (!t.Destroyed)
                {
                    t.Destroy();
                }
            }
            foreach (Thing t in map.listerThings.AllThings.Where(t => !xdBeforeIds.Contains(t.thingIDNumber) && !(t is Pawn p && p.IsColonist)).ToList())
            {
                if (!t.Destroyed && t.def.destroyable)
                {
                    t.Destroy();
                }
            }
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist && p.Faction != Faction.OfPlayer).ToList())
            {
                p.Destroy();
            }
            foreach (Quest q in Find.QuestManager.QuestsListForReading.Where(q => !xdQuestsBefore.Contains(q.id) && q.State == QuestState.Ongoing).ToList())
            {
                q.End(QuestEndOutcome.Unknown, false);
            }
            foreach (GameCondition gc in map.gameConditionManager.ActiveConditions.ToList())
            {
                gc.End();
            }
            foreach (GameCondition gc in Find.World.gameConditionManager.ActiveConditions.ToList())
            {
                gc.End();
            }
            map.roofCollapseBuffer.Clear();
            XdRestore(map);
            foreach (Pawn c in Colonists(map))
            {
                Heal(c);
                if (c.Downed || c.InMentalState)
                {
                    c.mindState?.mentalStateHandler?.CurState?.RecoverFromState();
                }
            }
            Find.LetterStack.LettersListForReading.ToList().ForEach(l => Find.LetterStack.RemoveLetter(l));
        }

        private static void XdSnapshot(Map map)
        {
            int n = map.cellIndices.NumGridCells;
            xdRoofs = new RoofDef[n];
            xdTerrain = new TerrainDef[n];
            for (int i = 0; i < n; i++)
            {
                IntVec3 c = map.cellIndices.IndexToCell(i);
                xdRoofs[i] = map.roofGrid.RoofAt(c);
                xdTerrain[i] = map.terrainGrid.TerrainAt(c);
            }
            xdEdifices.Clear();
            AnnexMapComponent a = ProjectZ.Annex(map);
            for (int k = a.MinLevel; k <= a.MaxLevel; k++)
            {
                foreach (IntVec3 g in xdArea)
                {
                    IntVec3 c = ProjectZ.CellOnFloor(g, k, map);
                    Building b = c.IsValid ? c.GetEdifice(map) : null;
                    if (b != null && b.Position == c)
                    {
                        xdEdifices.Add((c, b.def, b.Stuff, b.Rotation));
                    }
                }
            }
            xdSlabs = SlabCount(map);
            xdKept = KeptCount(map);
        }

        private static void XdRestore(Map map)
        {
            int n = map.cellIndices.NumGridCells, terr = 0, roofs = 0, walls = 0;
            for (int i = 0; i < n; i++)
            {
                IntVec3 c = map.cellIndices.IndexToCell(i);
                if (map.terrainGrid.TerrainAt(c) != xdTerrain[i])
                {
                    map.terrainGrid.SetTerrain(c, xdTerrain[i]);
                    terr++;
                }
            }
            foreach (var e in xdEdifices)
            {
                Building b = e.c.GetEdifice(map);
                if (b == null)
                {
                    Place(e.def, e.stuff, e.c, map, e.rot);
                    walls++;
                }
                else if (b.HitPoints < b.MaxHitPoints)
                {
                    b.HitPoints = b.MaxHitPoints;
                }
            }
            for (int i = 0; i < n; i++)
            {
                IntVec3 c = map.cellIndices.IndexToCell(i);
                if (map.roofGrid.RoofAt(c) != xdRoofs[i])
                {
                    map.roofGrid.SetRoof(c, xdRoofs[i]);
                    roofs++;
                }
            }
            map.roofCollapseBuffer.Clear();
            foreach (IntVec3 g in xdArea.ExpandedBy(2))
            {
                for (int k = 0; k <= ProjectZ.Annex(map).MaxLevel; k++)
                {
                    IntVec3 c = ProjectZ.CellOnFloor(g, k, map);
                    if (c.IsValid && c.InBounds(map))
                    {
                        map.fogGrid.Unfog(c);
                    }
                }
            }
            if (terr + roofs + walls > 0)
            {
                Out($"  restored: terrain {terr}, roofs {roofs}, buildings {walls}; slabs {SlabCount(map)} (built {xdSlabs}), floor roofs {KeptCount(map)} (built {xdKept})");
            }
        }

        private static void XdSetup(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || an.MaxLevel < 2)
            {
                Result("reprodrops setup", false, $"needs 3 floors (-projectzfloors=3); max level {an?.MaxLevel}");
                Finish();
                return;
            }
            FQuiet(map, true);
            XdPatch();
            File.WriteAllText(Path.Combine(outDir, "landings.tsv"), "source\trun\tskyfaller(contents)\tcell\tground cell\tfloor\tzone\troof at spawn\troof at impact\troof after\tDropRules.Blocked\tKept\tfootprint blocked\tslab above (floors)\tslab still\tCanPhysicallyDropInto calls/false\tSkyfallerCanLandAt calls/false\tcontents at impact\tcontents after\n");
            File.WriteAllText(Path.Combine(outDir, "traces.txt"), "");
            int gz = ProjectZ.GroundZ(map);
            xdArea = new CellRect(Mathf.Max(12, map.Size.x / 2 - 36), Mathf.Max(12, gz / 2 - 24), 72, 48);
            if (!xdArea.ExpandedBy(2).InBounds(map) || xdArea.maxZ >= gz - 12)
            {
                Result("test spot", false, "map too small (run with -projectzmapsize=120)");
                Finish();
                return;
            }
            xdO = new IntVec3(xdArea.minX, 0, xdArea.minZ);
            TerrainDef soil = DefDatabase<TerrainDef>.GetNamed("Soil");
            foreach (IntVec3 c in xdArea.ExpandedBy(2))
            {
                map.roofGrid.SetRoof(c, null);
            }
            foreach (IntVec3 c in xdArea.ExpandedBy(2))
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable && !(t is Pawn))
                    {
                        t.Destroy();
                    }
                }
                if (c.GetTerrain(map).IsWater || c.GetTerrain(map).passability != Traversability.Standable || !c.GetTerrain(map).affordances.Any(a => a.defName == "Heavy"))
                {
                    map.terrainGrid.SetTerrain(c, soil);
                }
                map.roofGrid.SetRoof(c, null);
            }
            map.roofCollapseBuffer.Clear();
            DebugSettings.enableRandomMentalStates = false;
            Find.PlaySettings.autoRebuild = false;

            // L: ground room 36 x 30 under a floor-2 slab; a walled room on floor 2 under a floor-3 slab; floor 3 open.
            xdL = new CellRect(xdO.x, xdO.z, 36, 30);
            foreach (IntVec3 g in xdL)
            {
                bool edge = g.x == xdL.minX || g.x == xdL.maxX || g.z == xdL.minZ || g.z == xdL.maxZ;
                bool column = (g.x - xdO.x) % 6 == 3 && (g.z - xdO.z) % 6 == 3;
                if (edge || column)
                {
                    Place(g == XA(18, 0) ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.Steel, g, map);
                }
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
            }
            foreach (IntVec3 g in xdL)
            {
                map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(g, 1, map), PZDefOf.PZ_UpperFloor);
            }
            foreach (IntVec3 g in xdL)
            {
                IntVec3 u = ProjectZ.CellOnFloor(g, 1, map);
                bool edge = g.x == xdL.minX || g.x == xdL.maxX || g.z == xdL.minZ || g.z == xdL.maxZ;
                bool column = (g.x - xdO.x) % 6 == 3 && (g.z - xdO.z) % 6 == 3;
                if ((edge && g.x != xdL.maxX) || column)
                {
                    Place(ThingDefOf.Wall, ThingDefOf.Steel, u, map);
                }
                map.roofGrid.SetRoof(u, RoofDefOf.RoofConstructed);
            }
            // AT: the floor-3 slab runs 5 cells on east of L over open air on floor 2 (the east wall of L on floor 2 left
            // open, so floor 2's room looks into the atrium; the ground under it is open, no roof of its own).
            xdAt = new CellRect(xdL.maxX + 1, xdO.z, 5, 30);
            foreach (IntVec3 g in xdL.Cells.Concat(xdAt.Cells))
            {
                map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(g, 2, map), PZDefOf.PZ_UpperFloor);
            }
            // P: house 13 x 10, ground room under a floor-2 slab, floor-2 room with a normal thin roof.
            xdP = new CellRect(xdO.x + 48, xdO.z, 13, 10);
            foreach (IntVec3 c in xdP.EdgeCells)
            {
                Place(c == XA(54, 0) ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            foreach (IntVec3 c in xdP)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(c, 1, map), PZDefOf.PZ_UpperFloor);
            }
            foreach (IntVec3 g in xdP.EdgeCells)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, ProjectZ.CellOnFloor(g, 1, map), map);
            }
            foreach (IntVec3 g in xdP)
            {
                map.roofGrid.SetRoof(ProjectZ.CellOnFloor(g, 1, map), RoofDefOf.RoofConstructed);
            }
            // G: plain roofed ground room 9 x 7.
            xdG = new CellRect(xdO.x + 48, xdO.z + 16, 9, 7);
            foreach (IntVec3 c in xdG.EdgeCells)
            {
                Place(c == XA(52, 16) ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            foreach (IntVec3 c in xdG)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            // an orbital trade beacon on open ground, silver beside it
            xdBeacon = (Building)Place(ThingDefOf.OrbitalTradeBeacon, null, XA(64, 40), map);
            Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
            silver.stackCount = 500;
            GenSpawn.Spawn(silver, XA(65, 41), map);
            for (int i = 0; i < 6; i++)
            {
                Thing more = ThingMaker.MakeThing(ThingDefOf.Silver);
                more.stackCount = 500;
                GenSpawn.Spawn(more, XA(66 + i % 3, 41 + i / 3), map);
            }
            foreach (IntVec3 g in xdArea.ExpandedBy(2))
            {
                for (int k = 0; k <= an.MaxLevel; k++)
                {
                    IntVec3 c = ProjectZ.CellOnFloor(g, k, map);
                    if (c.IsValid && c.InBounds(map))
                    {
                        map.fogGrid.Unfog(c);
                    }
                }
            }
            // colonists out of the scene, drafted, by the beacon's silver (negotiator for the trade)
            List<Pawn> cols = Colonists(map);
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], CellFinder.RandomClosewalkCellNear(new IntVec3(xdArea.maxX + 6, 0, xdArea.maxZ - 2), map, 3));
                Draft(cols[i], true);
            }
            xdNegotiator = cols.FirstOrDefault();
            map.roofCollapseBuffer.Clear();
            Info($"scene {xdArea}: L {xdL} (ground roof {XRoof(XA(10, 10), map)}, floor 2 roof {XRoof(XF(10, 10, 1, map), map)}, floor 3 {(ProjectZ.HasFloor(XF(10, 10, 2, map), map) ? "slab" : "none")}); atrium {xdAt} (ground roof {XRoof(XA(38, 10), map)}, floor 2 {XF(38, 10, 1, map).GetTerrain(map)?.defName} roof {XRoof(XF(38, 10, 1, map), map)}); P {xdP} (ground roof {XRoof(XA(52, 5), map)}, floor 2 roof {XRoof(XF(52, 5, 1, map), map)}); G {xdG} (roof {XRoof(XA(52, 19), map)}); groundZ {gz}, map {map.Size}, levels {an.MinLevel}..{an.MaxLevel}");
            int keptL1 = xdL.Cells.Count(g => FloorRoof.Kept(g, map)), keptL2 = xdL.Cells.Count(g => FloorRoof.Kept(ProjectZ.CellOnFloor(g, 1, map), map)), keptAt = xdAt.Cells.Count(g => FloorRoof.Kept(g, map));
            Info($"kept roofs: L ground {keptL1}/{xdL.Area}, L floor 2 {keptL2}/{xdL.Area}, atrium ground {keptAt}/{xdAt.Area}");
            XdSnapshot(map);
            XdSources(map);
            xdSrc = 0;
            xdRun = 0;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            // the scene, before anything falls
            XdView3D(true, map);
            ShotAt(map, 2, xdL.CenterCell, 26f);
            Wait(30, "xd_scene_take");
        }

        private static bool StepReproDropsScene(Map map)
        {
            switch (stage)
            {
                case "xd_scene_take":
                    ZCapture("reprodrops_scene_3d_viewtop");
                    Wait(3, "xd_scene2");
                    return true;
                case "xd_scene2":
                    ShotAt(map, 1, xdL.CenterCell, 26f);
                    Wait(30, "xd_scene2_take");
                    return true;
                case "xd_scene2_take":
                    ZCapture("reprodrops_scene_3d_viewfloor2");
                    Wait(3, "xd_next");
                    return true;
            }
            return false;
        }

        private static void XdReport(Map map)
        {
            List<XdLanding> viol = xdLandings.Where(l => l.blocked || l.footprintBlocked != "").ToList();
            foreach (XdSource s in xdSources)
            {
                List<XdLanding> ls = xdLandings.Where(l => l.source == s.name).ToList();
                string perFloor = string.Join(" ", ls.GroupBy(l => l.floor + 1).OrderBy(g => g.Key).Select(g => $"f{g.Key}:{g.Count()}"));
                string perZone = string.Join(" ", ls.GroupBy(l => XZone(l.cell, map)).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"));
                if (s.fire == null)
                {
                    Info($"SOURCE {s.name}: not run ({s.note})");
                    continue;
                }
                if (s.name.StartsWith("discover:") && ls.Count == 0)
                {
                    continue;
                }
                Info($"SOURCE {s.name}: runs {s.runs}, fired {s.fired}, failed {s.failed}{(s.note != "" ? " (" + s.note + ")" : "")}; skyfallers {ls.Count} ({perFloor}; zones {perZone}); violations {ls.Count(l => l.blocked || l.footprintBlocked != "")}");
            }
            List<string> silent = xdSources.Where(s => s.name.StartsWith("discover:") && !xdLandings.Any(l => l.source == s.name)).Select(s => s.name.Substring(9) + (s.fired > 0 ? "" : "(not fired)")).ToList();
            Info($"discovery, no skyfaller: {string.Join(", ", silent)}");
            foreach (var g in viol.GroupBy(l => l.source))
            {
                Info($"VIOLATIONS {g.Key}: {g.Count()}: {string.Join("; ", g.Take(12).Select(l => $"run {l.run} {XWhere(l.cell, map)} slab above {l.slabAbove}, roof at impact {l.roofAtImpact}, after {l.roofAfter}, slab still {l.slabStill}, contents after {l.contentsAfter}"))}");
            }
            Result("no skyfaller lands where DropRules.Blocked (under a kept roof or off the floors)", viol.Count == 0, $"{xdLandings.Count} skyfallers, {viol.Count} violations");
        }
    }
}
