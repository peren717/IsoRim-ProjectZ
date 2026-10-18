using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode radial (compat G3a, isorim-pz-compat-g3-plan.md section 5): radius effects stay on their centre's floor
    // (BandRadial). Run with -projectzfloors=3 -projectzmapsize=101 (a 1-row void gap, the worst case).
    // -projectznoradialclip: nothing patched (the game's cells); a check that the clip alone decides is then named
    // "LEAK ..." and expects the old leak; one that the void rule's rooms and sight also cover keeps its expectation.
    // -projectzradialref=<tag>: compare this run's fingerprints (N1, N2) with that run's (renders/<tag>/fingerprints.txt).
    // Every check: its own fixed seed for its whole (synchronous) part, ticks included (Rand.PushState(7300 + check));
    // its own fixture in an x-strip (all rows of 11 columns), everything it spawned destroyed and the strip's terrain,
    // roofs, pollution and snow put back at its end; counters reset. Real flows: incidents through their workers, pawn
    // jobs, verbs and abilities cast by pawns, comps ticking through TickManager.DoSingleTick.
    //   R0 the list: every listed method found and patched, its producers swapped as listed; opt-outs not patched.
    //   N2 (first, so both runs start alike) the checks below centred well inside a band: fingerprints equal with and
    //      without the clip (compared by the -projectznoradialclip run).
    //   R1 an IED shot by a colonist on the ground's north row: a ground pawn is warned, one on floor 2's south row isn't;
    //      the blast (Explosions3D) is fingerprinted (unchanged without the clip).
    //   R2 a short circuit (the incident) by grass on the ground's north rows, wood and hay on floor 2's south row: no fire
    //      there after 2,500 ticks.
    //   R3 a toxifier on floor 2's south rows pollutes only floor 2; a pollution pump there unpollutes only floor 2 (the
    //      BandScope rows G3 retires are off for R3 and R4: BandRadial alone).
    //   R4 a tornado driving north on the ground: nothing on floor 2's south rows damaged, no roof lost.
    //   R5 a colonist on floor 2's south row walled in on its cell: it recovers onto floor 2, not onto the ground.
    //   R6 a colonist on floor 2's south row drops its weapon, every cell next to it taken: it lands on floor 2 (the
    //      same-floor placement patch off).
    //   R7 Waterskip (Royalty) cast on floor 2's south row: floor 2's fires out, the ground's north row still burning.
    //   R8 a pyromaniac on floor 2's south row, a fire on the ground 2 raw rows away: no thought; control: a fire on floor 2.
    //   P1 startup cost of the list (resolve, patch) and P3 per-method counters (a clip seen as needed, 0 calls = inlined).
    // Mode radialn1 (-projectzfloors=1): N1, the ground-only variants of R1, R2, R5, R6, R7 fingerprinted (compared
    //   with and without the clip: identical). Mode radialperf (P2): ms per tick with 30 colonists (run with and without
    //   the clip, -projectzfloors=4 and 1, -projectzmapsize=250).
    public static partial class TestRunner
    {
        private static int rdGz, rdStride;
        private static bool rdFloors;
        private static bool rdSeeded;
        private static RdFix rdFix;
        private static readonly List<string> rdFp = new List<string>();
        private static readonly List<string> rdFpDetail = new List<string>();
        private static Dictionary<string, string> rdRef;
        private static readonly List<Pawn> rdWarned = new List<Pawn>();
        private static readonly List<string> rdP3 = new List<string>();

        private static bool RdOff => BandRadial.Off;

        private sealed class RdFix
        {
            public string name;
            public int x0, x1;
            public HashSet<Thing> before;
            public Dictionary<IntVec3, TerrainDef> terrain = new Dictionary<IntVec3, TerrainDef>();
            public Dictionary<IntVec3, RoofDef> roofs = new Dictionary<IntVec3, RoofDef>();
            public HashSet<IntVec3> polluted = new HashSet<IntVec3>();
            public Dictionary<IntVec3, float> snow = new Dictionary<IntVec3, float>();
            public List<Pawn> pawns = new List<Pawn>();
        }

        internal static void PatchRadialProbe(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Game), nameof(Game.LoadGame)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(RdLoadedPostfix)));
            h.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.TakeDamage)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(RdDamagePrefix)));
            h.Patch(AccessTools.Method(typeof(Pawn_MindState), "Notify_DangerousExploderAboutToExplode"), postfix: new HarmonyMethod(typeof(TestRunner), nameof(RdWarnPostfix)));
        }

        // The compared runs load one save: the game's rand fixed once it is loaded (the load itself draws from the
        // start-up seed: chunk rotations), and the game paused after its one tick (Prefs pauseOnLoad, not saved).
        private static void RdLoadedPostfix()
        {
            AccessTools.Field(typeof(Rand), "seed").SetValue(null, 7300u);
            AccessTools.Field(typeof(Rand), "iterations").SetValue(null, 0u);
        }

        // fire damage taken by things off the ground (R2)
        private static int rdFlameUp;
        private static readonly List<string> rdFlameUpWhat = new List<string>();

        private static void RdDamagePrefix(Thing __instance, DamageInfo dinfo)
        {
            if (dinfo.Def == DamageDefOf.Flame && __instance.Spawned && ProjectZ.IsAnnex(__instance.Position, __instance.Map))
            {
                rdFlameUp++;
                if (rdFlameUpWhat.Count < 6)
                {
                    rdFlameUpWhat.Add(__instance.def.defName + " " + RdCell(__instance.Position, __instance.Map));
                }
            }
        }

        private static void RdWarnPostfix(Pawn_MindState __instance)
        {
            if (__instance?.pawn != null && !rdWarned.Contains(__instance.pawn))
            {
                rdWarned.Add(__instance.pawn);
            }
        }

        // ---- cells: ground (x, z); floor k over ground (x, z)
        private static IntVec3 RdG(int x, int z) => new IntVec3(x, 0, z);

        private static IntVec3 RdOn(int x, int z, int k, Map map) => k == 0 ? new IntVec3(x, 0, z) : ProjectZ.CellOnFloor(new IntVec3(x, 0, z), k, map);

        private static string RdCell(IntVec3 c, Map map) => $"{c} lv {Levels.LevelOf(c, map)}";

        private static IEnumerable<IntVec3> RdStrip(Map map) => new CellRect(rdFix.x0, 0, rdFix.x1 - rdFix.x0 + 1, map.Size.z).Cells;

        // A check that the clip alone decides (leakable) or that the void rule also covers.
        private static void RdExpect(string name, bool ok, bool leakable, string detail)
        {
            if (!RdOff)
            {
                Result(name, ok, detail);
            }
            else if (leakable)
            {
                Result("LEAK " + name, !ok, detail);
            }
            else
            {
                Result(name + " (also without the clip: the void rule's rooms and sight)", ok, detail);
            }
        }

        // The check's fixture: an x-strip of 11 columns (slot 0..7), recorded so RdEnd puts it back.
        private static void RdBegin(Map map, int check, string name, int slot)
        {
            RdEndSeed();
            rdFix = new RdFix { name = name, x0 = 3 + 12 * (slot % 8) };
            rdFix.x1 = rdFix.x0 + 10;
            rdFix.before = new HashSet<Thing>();
            foreach (IntVec3 c in RdStrip(map))
            {
                foreach (Thing t in map.thingGrid.ThingsListAtFast(c))
                {
                    rdFix.before.Add(t);
                }
                rdFix.terrain[c] = c.GetTerrain(map);
                rdFix.roofs[c] = map.roofGrid.RoofAt(c);
                if (map.pollutionGrid.IsPolluted(c))
                {
                    rdFix.polluted.Add(c);
                }
                float d = map.snowGrid.GetDepth(c);
                if (d > 0f)
                {
                    rdFix.snow[c] = d;
                }
            }
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            BandRadial.ResetCounters();
            BandRadial.Counting = true;
            rdWarned.Clear();
            Rand.PushState(7300 + check);
            rdSeeded = true;
        }

        private static void RdEndSeed()
        {
            if (rdSeeded)
            {
                Rand.PopState();
                rdSeeded = false;
            }
        }

        // Everything the check spawned destroyed, the strip's terrain, roofs, pollution and snow put back.
        private static void RdEnd(Map map)
        {
            if (rdFix == null)
            {
                return;
            }
            int removed = 0;
            foreach (Pawn p in rdFix.pawns)
            {
                if (!p.Destroyed)
                {
                    p.jobs?.StopAll();
                    p.Destroy();
                    removed++;
                }
            }
            foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Fire).ToList())
            {
                t.Destroy();
            }
            foreach (IntVec3 c in RdStrip(map))
            {
                foreach (Thing t in map.thingGrid.ThingsListAtFast(c).ToList())
                {
                    if (!rdFix.before.Contains(t) && !t.Destroyed && t.def.destroyable)
                    {
                        t.Destroy();
                        removed++;
                    }
                }
            }
            foreach (IntVec3 c in RdStrip(map))
            {
                if (c.GetTerrain(map) != rdFix.terrain[c])
                {
                    map.terrainGrid.SetTerrain(c, rdFix.terrain[c]);
                }
                if (map.roofGrid.RoofAt(c) != rdFix.roofs[c])
                {
                    map.roofGrid.SetRoof(c, rdFix.roofs[c]);
                }
                bool pol = rdFix.polluted.Contains(c);
                if (map.pollutionGrid.IsPolluted(c) != pol)
                {
                    map.pollutionGrid.SetPolluted(c, pol);
                }
                float d = rdFix.snow.TryGetValue(c, out float sd) ? sd : 0f;
                if (Mathf.Abs(map.snowGrid.GetDepth(c) - d) > 0.0001f)
                {
                    map.snowGrid.SetDepth(c, d);
                }
            }
            map.roofCollapseBuffer.Clear();
            BandScope_Patch.RetirableOff = false;
            Patches_TestOff(false);
            BandRadial.Counting = false;
            Out($"  {rdFix.name}: cleaned up ({removed} things removed, strip x {rdFix.x0}..{rdFix.x1} restored)");
            rdFix = null;
            RdEndSeed();
        }

        private static void Patches_TestOff(bool on)
        {
            TryPlaceThing_SameFloor_Patch.TestOff = on;
        }

        // A fixed number of real ticks inside the check's own seed (one frame); stops early when cond holds.
        private static int RdTicks(int max, Func<bool> cond = null, int every = 10)
        {
            TickManager tm = Find.TickManager;
            for (int i = 1; i <= max; i++)
            {
                tm.DoSingleTick();
                if (i % 15 == 0)
                {
                    RdFrame(Find.CurrentMap);
                }
                if (cond != null && i % every == 0 && cond())
                {
                    return i;
                }
            }
            return max;
        }

        // What the map does each frame that ticks rely on (Map.MapUpdate): power nets connected, dirty regions and rooms
        // rebuilt, the glow grid updated. (The checks tick inside one frame, under their own seed.)
        private static void RdFrame(Map map)
        {
            map.powerNetManager.UpdatePowerNetsAndConnections_First();
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            map.glowGrid.GlowGridUpdate_First();
        }

        private static Pawn RdPawn(Map map, IntVec3 at, string weapon = null, bool draft = true)
        {
            Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true, mustBeCapableOfViolence: true));
            GenSpawn.Spawn(p, at, map);
            if (draft)
            {
                Draft(p, true);
            }
            if (weapon != null)
            {
                p.skills.GetSkill(SkillDefOf.Shooting).Level = 20;
                p.equipment.DestroyAllEquipment();
                p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(weapon)));
            }
            rdFix.pawns.Add(p);
            return p;
        }

        // A floor-k slab over ground rect g (roof under it, as the floor's own).
        private static void RdSlab(Map map, CellRect g, int k = 1)
        {
            foreach (IntVec3 c in g)
            {
                IntVec3 below = RdOn(c.x, c.z, k - 1, map);
                map.roofGrid.SetRoof(below, PZDefOf.PZ_FloorRoof);
                IntVec3 u = RdOn(c.x, c.z, k, map);
                map.terrainGrid.SetTerrain(u, PZDefOf.PZ_UpperFloor);
                map.fogGrid.Unfog(u);
            }
        }

        // Ground cells cleared (things, water, rock, roofs) and unfogged.
        private static void RdClear(Map map, CellRect r)
        {
            foreach (IntVec3 c in r)
            {
                if (!c.InBounds(map))
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
                TerrainDef td = c.GetTerrain(map);
                if (td.IsWater || td.passability == Traversability.Impassable && td != PZDefOf.PZ_OpenAir)
                {
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
            }
        }

        private static Thing RdWood(Map map, IntVec3 c, int n = 75)
        {
            Thing w = ThingMaker.MakeThing(ThingDefOf.WoodLog);
            w.stackCount = n;
            return GenSpawn.Spawn(w, c, map);
        }

        private static void RdGrass(Map map, IEnumerable<IntVec3> cells)
        {
            ThingDef grass = DefDatabase<ThingDef>.GetNamed("Plant_Grass");
            foreach (IntVec3 c in cells)
            {
                if (c.GetPlant(map) == null && c.GetEdifice(map) == null)
                {
                    Plant pl = (Plant)ThingMaker.MakeThing(grass);
                    pl.Growth = 1f;
                    GenSpawn.Spawn(pl, c, map);
                }
            }
        }

        private static int FiresIn(Map map, IEnumerable<IntVec3> cells) => cells.Sum(c => c.InBounds(map) ? FireUtility.NumFiresAt(c, map) : 0);

        // ---- fingerprints (N1, N2, R1's blast)
        private static void RdFingerprint(Map map, string name, IEnumerable<Pawn> pawns)
        {
            StringBuilder sb = new StringBuilder();
            int things = 0, fires = 0, polluted = 0;
            float snow = 0f;
            foreach (IntVec3 c in RdStrip(map))
            {
                foreach (Thing t in map.thingGrid.ThingsListAtFast(c).OrderBy(t => t.def.defName).ThenBy(t => t.HitPoints))
                {
                    if (t is Pawn)
                    {
                        continue;
                    }
                    if (t is Plant)
                    {
                        sb.Append(t.def.defName).Append('@').Append(c.x - rdFix.x0).Append(',').Append(c.z).Append(';');
                        things++;
                        continue;
                    }
                    sb.Append(t.def.defName).Append('@').Append(c.x - rdFix.x0).Append(',').Append(c.z).Append(' ').Append(t.HitPoints).Append(' ').Append(t.stackCount).Append(';');
                    things++;
                    if (t is Fire)
                    {
                        fires++;
                    }
                }
                if (map.pollutionGrid.IsPolluted(c))
                {
                    sb.Append("P").Append(c.x - rdFix.x0).Append(',').Append(c.z).Append(';');
                    polluted++;
                }
                float d = map.snowGrid.GetDepth(c);
                if (d > 0f)
                {
                    snow += d;
                    sb.Append("S").Append(c.x - rdFix.x0).Append(',').Append(c.z).Append('=').Append(d.ToString("0.000")).Append(';');
                }
            }
            List<Thought> thoughts = new List<Thought>();
            foreach (Pawn p in pawns)
            {
                sb.Append("pawn ").Append(p.Dead ? "dead" : (p.PositionHeld.x - rdFix.x0) + "," + p.PositionHeld.z).Append(' ').Append(p.CurJobDef?.defName).Append(' ');
                foreach (Hediff h in p.health.hediffSet.hediffs.OrderBy(h => h.def.defName).ThenBy(h => h.Severity))
                {
                    sb.Append(h.def.defName).Append(':').Append(h.Severity.ToString("0.00")).Append(',');
                }
                if (!p.Dead && p.needs?.mood != null)
                {
                    thoughts.Clear();
                    p.needs.mood.thoughts.GetAllMoodThoughts(thoughts);
                    foreach (string td in thoughts.Select(t => t.def.defName).OrderBy(s => s))
                    {
                        sb.Append(td).Append(',');
                    }
                }
                sb.Append(';');
            }
            uint hash = 2166136261;
            foreach (char ch in sb.ToString())
            {
                hash = (hash ^ ch) * 16777619;
            }
            string line = $"{name}\t{hash:x8}\t{things} things, {fires} fires, {polluted} polluted, snow {snow:0.00}, {pawns.Count()} pawns";
            rdFp.Add(line);
            rdFpDetail.Add(name + "\t" + sb.ToString().Replace(";", "\n  "));
            Info("fingerprint " + line.Replace('\t', ' '));
            if (rdRef != null)
            {
                if (rdRef.TryGetValue(name, out string other))
                {
                    string mine = line.Split('\t')[1];
                    Result($"{name}: identical with and without the clip", other.Split('\t')[0] == mine, $"this run {mine} ({line.Split('\t')[2]}), reference {other.Replace('\t', ' ')}");
                }
                else
                {
                    Result($"{name}: in the reference run", false, "missing there");
                }
            }
        }

        private static void RdFinish()
        {
            try
            {
                File.WriteAllLines(Path.Combine(outDir, "fingerprints.txt"), rdFp);
                File.WriteAllLines(Path.Combine(outDir, "fingerprints_detail.txt"), rdFpDetail);
            }
            catch (Exception e)
            {
                Out("fingerprints not written: " + e.Message);
            }
            Finish();
        }

        private static string RdCounters(params string[] names)
        {
            return string.Join("; ", names.Select(n =>
            {
                BandRadial.Row r = BandRadial.Rows.FirstOrDefault(x => x.Name == n);
                return r == null ? n + " (not listed)" : $"{n} calls {r.calls} cells {r.cells} folded {r.folded} skipped {r.skipped}";
            }));
        }

        // P3: a method the check needed clipped was reached through the clip (0 calls with a clip expected: inlined).
        private static void RdInlineWatch(string check, params string[] names)
        {
            if (RdOff)
            {
                return;
            }
            foreach (string n in names)
            {
                BandRadial.Row r = BandRadial.Rows.FirstOrDefault(x => x.Name == n);
                bool ok = r != null && r.target != null && r.calls > 0;
                rdP3.Add($"{check} {n}: calls {r?.calls} cells {r?.cells} folded {r?.folded} skipped {r?.skipped} ({r?.target?.GetMethodBody()?.GetILAsByteArray()?.Length} B)");
                Result($"P3 {check}: {n} ran through the clip", ok, ok ? $"calls {r.calls}, cells {r.cells}, folded {r.folded}, skipped {r.skipped}" : r == null ? "not listed" : "0 calls: inlined into its caller? needs a call-site swap");
            }
        }

        // ==================================================================== driver

        private static bool StepRadial(Map map)
        {
            try
            {
                return StepRadialInner(map);
            }
            finally
            {
                RdEndSeed();
            }
        }

        private static bool StepRadialInner(Map map)
        {
            switch (stage)
            {
                case "rd_start":
                    // the compared runs start from one saved map (generation is not deterministic between runs)
                    if (GenCommandLine.TryGetCommandLineArg("projectzsave", out string save) && mode != "radialsave")
                    {
                        stage = "rd_setup";
                        Traverse.Create(typeof(Prefs)).Field("data").Field("pauseOnLoad").SetValue(true);
                        pendingLoad = save;
                        Out("radial: loading " + save);
                    }
                    else
                    {
                        stage = "rd_setup";
                    }
                    return true;
                case "rd_setup":
                    RdSetup(map);
                    return true;
                case "rd_r0":
                    RdR0(map);
                    Wait(5, mode == "radialperf" ? "rd_perf" : "rd_n");
                    return true;
                case "rd_n":
                    RdNChecks(map);
                    Wait(5, mode == "radialn1" ? "rd_end" : "rd_r1");
                    return true;
                case "rd_r1":
                    RdR1(map);
                    Wait(5, "rd_r2");
                    return true;
                case "rd_r2":
                    RdR2(map);
                    Wait(5, "rd_r3");
                    return true;
                case "rd_r3":
                    RdR3(map);
                    Wait(5, "rd_r3b");
                    return true;
                case "rd_r3b":
                    RdR3b(map);
                    Wait(5, "rd_r4");
                    return true;
                case "rd_r4":
                    RdR4(map);
                    Wait(5, "rd_r5");
                    return true;
                case "rd_r5":
                    RdR5(map);
                    Wait(5, "rd_r6");
                    return true;
                case "rd_r6":
                    RdR6(map);
                    Wait(5, "rd_r7");
                    return true;
                case "rd_r7":
                    RdR7(map);
                    Wait(5, "rd_r8");
                    return true;
                case "rd_r8":
                    RdR8(map);
                    Wait(5, "rd_end");
                    return true;
                case "rd_perf":
                    RdPerf(map);
                    return true;
                case "rd_end":
                    foreach (string l in rdP3)
                    {
                        Info("P3 " + l);
                    }
                    RdFinish();
                    return true;
            }
            return false;
        }

        private static void RdSetup(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            rdFloors = a != null;
            rdGz = ProjectZ.GroundZ(map);
            rdStride = a?.stride ?? map.Size.z;
            if (mode == "radial" && (a == null || a.floors < 3 || a.stride - a.groundZ != 1))
            {
                Result("radial setup", false, $"needs 3 floors and a 1-row gap (-projectzfloors=3 -projectzmapsize=101); floors {a?.floors}, ground {rdGz}, stride {rdStride}");
                RdFinish();
                return;
            }
            if (mode == "radialn1" && a != null)
            {
                Result("radialn1 setup", false, "needs a map without floors (-projectzfloors=1)");
                RdFinish();
                return;
            }
            if (GenCommandLine.TryGetCommandLineArg("projectzradialref", out string refTag))
            {
                string p = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", refTag, "fingerprints.txt");
                if (File.Exists(p))
                {
                    rdRef = File.ReadAllLines(p).Select(l => l.Split(new[] { '\t' }, 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
                }
                else
                {
                    Result("reference fingerprints", false, "missing: " + p);
                }
            }
            DebugSettings.enableRandomMentalStates = false;
            Find.Storyteller.storytellerComps.Clear();   // no other incidents during the test (test only)
            Find.PlaySettings.autoRebuild = false;
            // only our pawns act: wild animals and visitors gone, the colonists drafted in the west column (outside every strip)
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(p => p.Faction != Faction.OfPlayer).ToList())
            {
                p.Destroy();
            }
            List<Pawn> cols = Colonists(map);
            for (int i = 0; i < cols.Count; i++)
            {
                RdClear(map, CellRect.SingleCell(RdG(1, rdGz / 3 + i)));
                Teleport(cols[i], RdG(1, rdGz / 3 + i));
                Draft(cols[i], true);
            }
            // the strips' working rows: the ground's north rows, the middle, the ground's south rows (floor supports)
            if (mode != "radialperf")
            {
                RdClear(map, new CellRect(2, rdGz - 14, map.Size.x - 3, 14));
                RdClear(map, new CellRect(2, rdGz / 2 - 9, map.Size.x - 3, 18));
                RdClear(map, new CellRect(2, 0, map.Size.x - 3, 13));
            }
            Info("radial: map at setup: " + map.listerThings.AllThings.Count + " things, hash " + map.listerThings.AllThings.Aggregate(17, (h, t) => h * 31 + t.def.shortHash + t.Position.x * 7 + t.Position.z * 131) + ", tick " + Find.TickManager.TicksGame + ", tile " + map.Tile + ", world seed " + Find.World.info.seedString);
            if (mode == "radialsave")
            {
                string name = GenCommandLine.TryGetCommandLineArg("projectzsave", out string sv) ? sv : "pz_radial";
                GameDataSaveLoader.SaveGame(name);
                Result("radialsave: saved " + name, File.Exists(GenFilePaths.FilePathForSavedGame(name)), GenFilePaths.FilePathForSavedGame(name));
                RdFinish();
                return;
            }
            Info($"radial: ground {map.Size.x} x {rdGz}, stride {rdStride}, floors {a?.floors ?? 1}; clip {(RdOff ? "OFF (-projectznoradialclip)" : "on")}; reference {(rdRef != null ? refTag : "none")}");
            Wait(30, "rd_r0");
        }

        // ---- R0 the list, P1 the startup cost
        private static void RdR0(Map map)
        {
            Info($"P1 radius clip startup: {BandRadial.Listed} listed, {BandRadial.Patched} patched, {BandRadial.SitesSwapped} producers swapped, resolve {BandRadial.ResolveMs:0.0} ms, patch {BandRadial.PatchMs:0} ms");
            Dictionary<string, int> groups = BandRadial.Rows.GroupBy(r => r.group).ToDictionary(g => g.Key, g => g.Count());
            Info("R0 list by group: " + string.Join(", ", groups.OrderBy(kv => kv.Key).Select(kv => kv.Key + " " + kv.Value)));
            if (RdOff)
            {
                Result("R0 clip off: nothing patched", BandRadial.Patched == 0, $"{BandRadial.Patched} patched");
                return;
            }
            List<string> bad = BandRadial.Rows.Where(r => r.target == null || r.swaps != r.expected || r.swaps < 1).Select(r => $"{r.Name} {(r.target == null ? "not found" : r.swaps + "/" + r.expected)}").ToList();
            Result("R0 every listed method found, patched and its producers swapped as listed", BandRadial.Listed > 100 && BandRadial.Patched == BandRadial.Listed && BandRadial.NotFound == 0 && bad.Count == 0,
                $"{BandRadial.Patched} of {BandRadial.Listed} patched, {BandRadial.SitesSwapped} producers swapped; off: {string.Join(", ", bad)}");
            int ours = BandRadial.Rows.Count(r => r.target != null && Harmony.GetPatchInfo(r.target)?.Transpilers.Any(t => t.PatchMethod.DeclaringType == typeof(BandRadial)) == true);
            Result("R0 Harmony holds the clip on every listed method", ours == BandRadial.Patched, $"{ours} of {BandRadial.Patched}");
            bool forced = BandRadial.Rows.Any(r => r.method == "GetForcedMissTarget" || r.method == "ExplosionCellsToHit" || r.type.Contains("GenStep") || r.type.Contains("SymbolResolver") || r.type.StartsWith("Verse.Debug") || r.type.Contains("DebugAction"));
            Result("R0 opt-outs, explosions, map generation and debug tools not listed", !forced, "");
            Result("R0 ProcessEquidistantCells' InBounds swapped", BandRadial.PecSwapped == 1, $"{BandRadial.PecSwapped}");
            Info($"R0 a map with floors seen: {BandRadial.AnyAnnex}");
        }

        // ==================================================================== N1 / N2
        // The same checks centred well inside a band (more than 10 cells from its edges): fingerprinted.
        private static void RdNChecks(Map map)
        {
            int zc = rdGz / 2;
            int up = rdFloors ? 1 : 0;
            string tag = rdFloors ? "N2" : "N1";
            // IED, shot (ground)
            RdBegin(map, 50, tag + " IED", 0);
            {
                Thing ied = Place(DefDatabase<ThingDef>.GetNamed("TrapIED_HighExplosive"), null, RdG(rdFix.x0 + 5, zc + 2), map);
                Pawn a = RdPawn(map, RdG(rdFix.x0 + 2, zc));
                Pawn s = RdPawn(map, RdG(rdFix.x0 + 9, zc - 5), "Gun_BoltActionRifle");
                Job j = JobMaker.MakeJob(JobDefOf.AttackStatic, ied);
                j.playerForced = true;
                s.jobs.TryTakeOrderedJob(j, JobTag.DraftedOrder);
                int t = RdTicks(1500, () => ied.Destroyed);
                Out($"  {tag} IED: exploded {ied.Destroyed} after {t} ticks, warned {string.Join(", ", rdWarned.Select(p => p.LabelShort))}");
                RdFingerprint(map, tag + " IED", new[] { a, s });
                RdEnd(map);
            }
            // fire in grass (ground)
            RdBegin(map, 51, tag + " fire", 1);
            {
                RdGrass(map, new CellRect(rdFix.x0, zc - 6, 11, 12).Cells);
                for (int x = rdFix.x0 + 3; x <= rdFix.x0 + 7; x += 2)
                {
                    FireUtility.TryStartFireIn(RdG(x, zc), map, 1f, null);
                }
                RdTicks(1000);
                RdFingerprint(map, tag + " fire", new Pawn[0]);
                RdEnd(map);
            }
            // stuck pawn (floor 2 with floors)
            RdBegin(map, 52, tag + " stuck pawn", 2);
            {
                if (rdFloors)
                {
                    RdSlab(map, new CellRect(rdFix.x0, zc - 6, 11, 12));
                }
                IntVec3 c = RdOn(rdFix.x0 + 5, zc, up, map);
                Pawn p = RdPawn(map, c);
                RdWallsAround(map, c, 2f);
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
                p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, RdOn(rdFix.x0 + 5, zc + 5, up, map)), JobTag.DraftedOrder);
                RdTicks(200);
                RdFingerprint(map, tag + " stuck pawn", new[] { p });
                RdEnd(map);
            }
            // a weapon dropped next to taken cells (ground)
            RdBegin(map, 53, tag + " drop", 3);
            {
                IntVec3 c = RdG(rdFix.x0 + 5, zc);
                Pawn p = RdPawn(map, c, "Gun_Revolver");
                RdItemsAround(map, c);
                p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.DropEquipment, p.equipment.Primary), JobTag.DraftedOrder);
                RdTicks(120);
                RdFingerprint(map, tag + " drop", new[] { p });
                RdEnd(map);
            }
            // Waterskip (floor 2 with floors)
            if (ModsConfig.RoyaltyActive)
            {
                RdBegin(map, 54, tag + " waterskip", 4);
                if (rdFloors)
                {
                    RdSlab(map, new CellRect(rdFix.x0, zc - 6, 11, 12));
                }
                IntVec3 target = RdOn(rdFix.x0 + 5, zc, up, map);
                for (int dx = -2; dx <= 2; dx++)
                {
                    for (int dz = -2; dz <= 2; dz++)
                    {
                        IntVec3 f = RdOn(rdFix.x0 + 5 + dx, zc + dz, up, map);
                        RdWood(map, f, 20);
                        FireUtility.TryStartFireIn(f, map, 0.5f, null);
                    }
                }
                Pawn caster = RdCaster(map, RdOn(rdFix.x0 + 5, zc + 4, up, map));
                bool cast = RdCast(map, caster, target);
                RdFingerprint(map, tag + " waterskip", new[] { caster });
                Out($"  {tag} waterskip cast {cast}");
                RdEnd(map);
            }
            Info($"{tag} clip counters were reset per check; calls through the clip on this map: {(rdFloors ? "counted" : "none (no map with floors: " + (!BandRadial.AnyAnnex) + ")")}");
            if (!rdFloors && !RdOff)
            {
                int calls = BandRadial.Rows.Sum(r => r.calls + r.cells + r.folded + r.skipped);
                Result("N1 no map with floors: the clip never engages (counters 0)", !BandRadial.AnyAnnex && calls == 0, $"any annex {BandRadial.AnyAnnex}, counters {calls}");
            }
        }

        private static void RdWallsAround(Map map, IntVec3 c, float r)
        {
            foreach (IntVec3 n in GenRadial.RadialCellsAround(c, r, false))
            {
                if (n.InBounds(map) && ProjectZ.SameBand(n, c, map) && n.Standable(map))
                {
                    Place(ThingDefOf.Wall, ThingDefOf.WoodLog, n, map);
                }
            }
        }

        // The cell and its 8 neighbours on its band hold one steel stack each (no room for a dropped weapon there).
        private static void RdItemsAround(Map map, IntVec3 c)
        {
            foreach (IntVec3 n in GenAdj.CellsAdjacent8Way(new TargetInfo(c, map)).Concat(new[] { c }))
            {
                if (n.InBounds(map) && ProjectZ.SameBand(n, c, map) && n.Standable(map))
                {
                    Thing s = ThingMaker.MakeThing(ThingDefOf.Steel);
                    s.stackCount = 10;
                    GenSpawn.Spawn(s, n, map);
                }
            }
        }

        private static Pawn RdCaster(Map map, IntVec3 at)
        {
            Pawn p = null;
            for (int i = 0; i < 6; i++)
            {
                p = RdPawn(map, at);
                if (p.GetStatValue(StatDefOf.PsychicSensitivity) > 0.5f)
                {
                    break;
                }
                rdFix.pawns.Remove(p);
                p.Destroy();
                p = null;
            }
            if (p == null)
            {
                return null;
            }
            p.ChangePsylinkLevel(1, sendLetter: false);   // (the first call implants level 1)
            p.ChangePsylinkLevel(2, sendLetter: false);
            p.abilities.GainAbility(DefDatabase<AbilityDef>.GetNamed("Waterskip"));
            p.psychicEntropy.OffsetPsyfocusDirectly(1f);
            return p;
        }

        private static bool RdCast(Map map, Pawn p, IntVec3 target)
        {
            Ability ab = p?.abilities.GetAbility(DefDatabase<AbilityDef>.GetNamed("Waterskip"));
            if (ab == null)
            {
                return false;
            }
            // (the ability gizmo's own job, as QueueCastingJob gives it once its confirmation is passed)
            bool canCast = ab.CanCast, canApply = ab.CanApplyOn(new LocalTargetInfo(target));
            bool disabled = ab.GizmoDisabled(out string why);
            p.jobs.TryTakeOrderedJob(ab.GetJob(new LocalTargetInfo(target), new LocalTargetInfo(target)), JobTag.Misc);
            string job = p.CurJobDef?.defName;
            int from = Find.TickManager.TicksGame;
            RdTicks(600, () => ab.lastCastTick >= from, 5);
            Out($"  waterskip by {p.LabelShort} {RdCell(p.Position, map)} at {RdCell(target, map)}: can cast {canCast}, can apply {canApply}, gizmo disabled {disabled} {why}, job {job}, cast at tick {ab.lastCastTick} (from {from})");
            return ab.lastCastTick >= from;
        }

        // ==================================================================== R1..R8 (the band edge)

        // ground north row z, floor 2's south row (over ground row 0)
        private static int RdNorth => rdGz - 1;

        private static IntVec3 RdF2(int x, int gz) => RdOn(x, gz, 1, rdMap);

        private static Map rdMap => Find.CurrentMap;

        private static void RdR1(Map map)
        {
            RdBegin(map, 1, "R1 IED", 0);
            RdSlab(map, new CellRect(rdFix.x0, 0, 11, 6));
            Thing ied = Place(DefDatabase<ThingDef>.GetNamed("TrapIED_HighExplosive"), null, RdG(rdFix.x0 + 5, RdNorth), map);
            Pawn a = RdPawn(map, RdG(rdFix.x0 + 2, RdNorth - 2));
            Pawn b = RdPawn(map, RdF2(rdFix.x0 + 5, 0));
            Pawn s = RdPawn(map, RdG(rdFix.x0 + 9, RdNorth - 8), "Gun_BoltActionRifle");
            Job j = JobMaker.MakeJob(JobDefOf.AttackStatic, ied);
            j.playerForced = true;
            s.jobs.TryTakeOrderedJob(j, JobTag.DraftedOrder);
            bool wick = false;
            int t = RdTicks(1500, () =>
            {
                wick |= rdWarned.Count > 0;
                return ied.Destroyed;
            });
            Result("R1 the IED's wick started and it went off", ied.Destroyed, $"after {t} ticks; warned {string.Join(", ", rdWarned.Select(p => RdCell(p.PositionHeld, map)))}");
            Result("R1 the ground pawn 3 cells away was warned", rdWarned.Contains(a), $"{a.LabelShort} {RdCell(a.PositionHeld, map)}");
            RdExpect("R1 the pawn on floor 2's south row (2 raw rows away) was not warned", !rdWarned.Contains(b), false, $"{b.LabelShort} {RdCell(b.PositionHeld, map)}");
            RdFingerprint(map, "R1 blast", new[] { a, b, s });
            RdInlineWatch("R1", "GenExplosion.NotifyNearbyPawnsOfDangerousExplosive");
            RdEnd(map);
        }

        private static void RdR2(Map map)
        {
            RdBegin(map, 2, "R2 fire", 1);
            RdSlab(map, new CellRect(rdFix.x0, 0, 11, 6));
            // a wood-fired generator powering a conduit line to the ground's north row
            Thing gen = Place(DefDatabase<ThingDef>.GetNamed("WoodFiredGenerator"), null, RdG(rdFix.x0 + 2, RdNorth - 9), map);
            CompRefuelable fuel = gen.TryGetComp<CompRefuelable>();
            fuel?.Refuel(fuel.Props.fuelCapacity);
            CellRect gr = gen.OccupiedRect();
            for (int x = gr.maxX + 1; x <= rdFix.x0 + 5; x++)
            {
                Place(ThingDefOf.PowerConduit, null, RdG(x, gr.minZ), map);
            }
            for (int z = gr.minZ + 1; z <= RdNorth; z++)
            {
                Place(ThingDefOf.PowerConduit, null, RdG(rdFix.x0 + 5, z), map);
            }
            RdGrass(map, new CellRect(rdFix.x0, RdNorth - 11, 11, 12).Cells);
            List<Thing> wood = new List<Thing>();
            for (int x = rdFix.x0; x <= rdFix.x1; x++)
            {
                wood.Add(RdWood(map, RdF2(x, 0)));
                Thing hay = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Hay"));
                hay.stackCount = 75;
                wood.Add(GenSpawn.Spawn(hay, RdF2(x, 1), map));
                // (roofed: no weathering, only fire could hurt them)
                map.roofGrid.SetRoof(RdF2(x, 0), RoofDefOf.RoofConstructed);
                map.roofGrid.SetRoof(RdF2(x, 1), RoofDefOf.RoofConstructed);
            }
            RdFrame(map);
            int conduits = ShortCircuitUtility.GetShortCircuitablePowerConduits(map).Count();
            IncidentDef def = DefDatabase<IncidentDef>.GetNamed("ShortCircuit");
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, map);
            parms.forced = true;
            bool fired = def.Worker.TryExecute(parms);
            int started = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count;
            if (started == 0)
            {
                // (the culprit picked was far from the grass: light the north rows as the circuit would)
                FireUtility.TryStartFireIn(RdG(rdFix.x0 + 4, RdNorth - 1), map, 1f, null);
            }
            // (and the grass on the north row lit, as the spreading fire would: the check is the fire there next to floor 2)
            for (int x = rdFix.x0 + 1; x <= rdFix.x1; x += 3)
            {
                FireUtility.TryStartFireIn(RdG(x, RdNorth), map, 1f, null);
            }
            int maxFire = 0, upFire = 0, northZ = 0;
            rdFlameUp = 0;
            rdFlameUpWhat.Clear();
            RdTicks(2500, () =>
            {
                List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
                maxFire = Math.Max(maxFire, fires.Count);
                upFire = Math.Max(upFire, fires.Count(f => ProjectZ.FloorOf(f.Position, map) != 0));
                northZ = Math.Max(northZ, fires.Where(f => ProjectZ.FloorOf(f.Position, map) == 0).Select(f => f.Position.z).DefaultIfEmpty(0).Max());
                return false;
            });
            int hurt = wood.Count(w => w.Destroyed || w.HitPoints < w.MaxHitPoints);
            int grassLeft = new CellRect(rdFix.x0, RdNorth - 11, 11, 12).Cells.Count(c => c.GetPlant(map) != null);
            Result("R2 the short circuit fired, the grass burned up to the ground's north row", fired && maxFire > 3 && northZ == RdNorth, $"incident {fired} (of {conduits} conduits), first fires {started}, up to {maxFire} fires, northmost fire row {northZ}, grass cells left {grassLeft} of 132");
            RdExpect("R2 no fire on floor 2's south row (wood and hay 2 raw rows away), no fire damage there", upFire == 0 && rdFlameUp == 0 && wood.All(w => !w.Destroyed), false, $"fires on floor 2 at most {upFire}, flame damage taken above the ground {rdFlameUp} {string.Join(", ", rdFlameUpWhat)}; stacks below full HP {hurt} of {wood.Count} {string.Join(" ", wood.Where(w => w.Destroyed || w.HitPoints < w.MaxHitPoints).Select(w => w.def.defName + (w.Destroyed ? " destroyed" : " " + w.HitPoints + "/" + w.MaxHitPoints + " " + RdCell(w.Position, map) + " roofed " + map.roofGrid.Roofed(w.Position))))}");
            RdInlineWatch("R2", "ShortCircuitUtility.TryStartFireNear", "Fire.TrySpread");
            RdEnd(map);
        }

        private static int RdPolluted(Map map, IEnumerable<IntVec3> cells) => cells.Count(c => c.InBounds(map) && map.pollutionGrid.IsPolluted(c));

        // the cells round (x, z) on every band: ground, void, floor 2, floor 3
        private static IEnumerable<IntVec3> RdColumn(Map map, int x0, int x1) => new CellRect(x0, 0, x1 - x0 + 1, map.Size.z).ClipInsideMap(map).Cells;

        private static void RdR3(Map map)
        {
            RdBegin(map, 3, "R3 toxifier", 2);
            BandScope_Patch.RetirableOff = true;
            if (!ModsConfig.BiotechActive)
            {
                Info("R3 Biotech not active: skipped");
                RdEnd(map);
                return;
            }
            RdSlab(map, new CellRect(rdFix.x0, 0, 11, 9));
            ThingDef def = DefDatabase<ThingDef>.GetNamed("ToxifierGenerator");
            CompProperties_Toxifier props = def.GetCompProperties<CompProperties_Toxifier>();
            int keep = props.pollutionIntervalTicks;
            props.pollutionIntervalTicks = 250;   // (test only: 6 cells every 250 ticks instead of 3 days; put back below)
            Thing tox;
            try
            {
                tox = Place(def, null, RdF2(rdFix.x0 + 5, 1), map);
                int t = RdTicks(2000, () => RdPolluted(map, RdColumn(map, rdFix.x0 - 27, rdFix.x1 + 27)) >= 30);
                Out($"  R3 toxifier at {RdCell(tox.Position, map)} ({tox.OccupiedRect()}) after {t} ticks");
            }
            finally
            {
                props.pollutionIntervalTicks = keep;
            }
            List<IntVec3> all = RdColumn(map, rdFix.x0 - 27, rdFix.x1 + 27).Where(c => map.pollutionGrid.IsPolluted(c)).ToList();
            int onF2 = all.Count(c => Levels.LevelOf(c, map) == 1);
            int off = all.Count - onF2;
            RdExpect("R3 a toxifier on floor 2's south rows (BandScope's rows off) pollutes only floor 2", all.Count >= 30 && off == 0, true,
                $"{all.Count} polluted, {onF2} on floor 2, {off} elsewhere {string.Join(" ", all.Where(c => Levels.LevelOf(c, map) != 1).Take(6).Select(c => RdCell(c, map)))}");
            RdInlineWatch("R3", "CompToxifier.UpdateNextPolluteCell", "CompToxifier.PolluteNextCell");
            RdEnd(map);
        }

        private static void RdR3b(Map map)
        {
            RdBegin(map, 4, "R3 pollution pump", 3);
            BandScope_Patch.RetirableOff = true;
            if (!ModsConfig.BiotechActive)
            {
                Info("R3 Biotech not active: pump skipped");
                RdEnd(map);
                return;
            }
            RdSlab(map, new CellRect(rdFix.x0, 0, 11, 9));
            Thing gen = Place(DefDatabase<ThingDef>.GetNamed("WoodFiredGenerator"), null, RdF2(rdFix.x0 + 8, 6), map);
            CompRefuelable fuel = gen.TryGetComp<CompRefuelable>();
            fuel?.Refuel(fuel.Props.fuelCapacity);
            CellRect gr = gen.OccupiedRect();
            for (int x = rdFix.x0 + 5; x < gr.minX; x++)
            {
                Place(ThingDefOf.PowerConduit, null, new IntVec3(x, 0, gr.minZ), map);
            }
            for (int z = rdStride; z < gr.minZ; z++)
            {
                Place(ThingDefOf.PowerConduit, null, new IntVec3(rdFix.x0 + 5, 0, z), map);
            }
            RdFrame(map);
            // polluted: ground cells 2..4 raw rows from the pump, floor-2 cells 4+ cells from it
            List<IntVec3> ground = new CellRect(rdFix.x0 + 2, RdNorth - 2, 7, 3).Cells.ToList();
            List<IntVec3> f2 = new CellRect(rdFix.x0, 4, 11, 3).Cells.Select(g => RdF2(g.x, g.z)).ToList();
            foreach (IntVec3 c in ground.Concat(f2))
            {
                map.pollutionGrid.SetPolluted(c, true);
            }
            ThingDef def = DefDatabase<ThingDef>.GetNamed("PollutionPump");
            CompProperties_PollutionPump props = def.GetCompProperties<CompProperties_PollutionPump>();
            int keep = props.intervalTicks;
            props.intervalTicks = 120;   // (test only; put back below)
            try
            {
                Thing pump = Place(def, null, RdF2(rdFix.x0 + 5, 0), map);
                RdTicks(1500, () => f2.Count(c => !map.pollutionGrid.IsPolluted(c)) >= 8);
                Out($"  R3 pump at {RdCell(pump.Position, map)}, powered {pump.TryGetComp<CompPowerTrader>()?.PowerOn}");
            }
            finally
            {
                props.intervalTicks = keep;
            }
            int groundClean = ground.Count(c => !map.pollutionGrid.IsPolluted(c));
            int f2Clean = f2.Count(c => !map.pollutionGrid.IsPolluted(c));
            RdExpect("R3 a pollution pump on floor 2's south row unpollutes only floor 2", f2Clean >= 4 && groundClean == 0, true, $"floor-2 cells cleaned {f2Clean} of {f2.Count}, ground cells cleaned {groundClean} of {ground.Count}");
            RdInlineWatch("R3", "CompPollutionPump.GetCellToUnpollute");
            RdEnd(map);
        }

        private static void RdR4(Map map)
        {
            RdBegin(map, 5, "R4 tornado", 4);
            BandScope_Patch.RetirableOff = true;
            RdSlab(map, new CellRect(rdFix.x0, 0, 11, 4));
            Dictionary<Thing, int> hp = new Dictionary<Thing, int>();
            for (int x = rdFix.x0 + 1; x <= rdFix.x1 - 1; x += 2)
            {
                Thing w = Place(ThingDefOf.Wall, ThingDefOf.WoodLog, RdF2(x, 0), map);
                hp[w] = w.HitPoints;
            }
            List<IntVec3> roofCells = new CellRect(rdFix.x0, 1, 11, 3).Cells.Select(g => RdF2(g.x, g.z)).Where(c => c.GetEdifice(map) == null).ToList();
            foreach (IntVec3 c in roofCells)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            Pawn p = RdPawn(map, RdF2(rdFix.x0 + 6, 0));
            int inj = Injuries(p);
            // a tornado on the ground's north row moving along it (west), then one driving north into the void
            Thing tornado = GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Tornado")), RdG(rdFix.x0 + 5, RdNorth), map);
            Traverse.Create(tornado).Field("direction").SetValue(180f);
            BandScope_Patch.AllOff = true;   // (BandRadial alone; Tornado.Tick's scope would cover the damage too)
            int t1;
            try
            {
                t1 = RdTicks(150, () => tornado.Destroyed || ProjectZ.IsAnnex(tornado.Position, map), 5);
            }
            finally
            {
                BandScope_Patch.AllOff = false;
            }
            Out($"  R4 first tornado after {t1} ticks: {(tornado.Spawned ? RdCell(tornado.Position, map) : "gone")}");
            if (!tornado.Destroyed)
            {
                tornado.Destroy();
            }
            tornado = GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Tornado")), RdG(rdFix.x0 + 5, RdNorth - 3), map);
            Traverse.Create(tornado).Field("direction").SetValue(270f);
            int maxZ = 0;
            int t = RdTicks(2500, () =>
            {
                if (tornado.Spawned)
                {
                    maxZ = Math.Max(maxZ, tornado.Position.z);
                }
                return tornado.Destroyed;
            }, 5);
            int damaged = hp.Count(kv => kv.Key.Destroyed || kv.Key.HitPoints < kv.Value);
            int roofs = roofCells.Count(c => map.roofGrid.Roofed(c));
            Result("R4 the tornado ends at the void (Tornado.Tick's row, kept)", tornado.Destroyed && maxZ < rdGz, $"destroyed {tornado.Destroyed} after {t} ticks, furthest row {maxZ}");
            RdExpect("R4 a tornado along the ground's north row (BandScope off): nothing on floor 2's south rows damaged, no roof lost", damaged == 0 && roofs == roofCells.Count && !p.Dead && Injuries(p) == inj, true,
                $"walls damaged {damaged} of {hp.Count} (on floor 2's south row), roofs {roofCells.Count} -> {roofs} (rows 2-4), colonist injuries {inj} -> {Injuries(p)}");
            RdInlineWatch("R4", "Tornado.DamageCloseThings", "Tornado.DamageFarThings", "Tornado.DestroyRoofs");
            if (!tornado.Destroyed)
            {
                tornado.Destroy();
            }
            RdEnd(map);
        }

        private static void RdR5(Map map)
        {
            RdBegin(map, 6, "R5 stuck pawn", 5);
            RdSlab(map, new CellRect(rdFix.x0, 0, 11, 8));
            IntVec3 c = RdF2(rdFix.x0 + 5, 0);
            Pawn p = RdPawn(map, c);
            RdWallsAround(map, c, 2f);
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, RdF2(rdFix.x0 + 5, 6)), JobTag.DraftedOrder);
            RdTicks(120);
            RdExpect("R5 walled in on floor 2's south row, the colonist recovers onto floor 2 (not the ground 2 raw rows down)", !p.Dead && Levels.LevelOf(p.PositionHeld, map) == 1, true, $"{p.LabelShort} now {RdCell(p.PositionHeld, map)}, dead {p.Dead}");
            RdInlineWatch("R5", "Pawn_PathFollower.TryRecoverFromUnwalkablePosition");
            RdEnd(map);
        }

        private static void RdR6(Map map)
        {
            RdBegin(map, 7, "R6 drop", 6);
            Patches_TestOff(true);
            RdSlab(map, new CellRect(rdFix.x0, 0, 11, 8));
            IntVec3 c = RdF2(rdFix.x0 + 5, 0);
            Pawn p = RdPawn(map, c, "Gun_Revolver");
            ThingWithComps gun = p.equipment.Primary;
            RdItemsAround(map, c);
            p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.DropEquipment, gun), JobTag.DraftedOrder);
            RdTicks(120, () => gun.Spawned);
            RdExpect("R6 a weapon dropped on floor 2's south row, its cell and neighbours taken (same-floor patch off): it lands on floor 2", gun.Spawned && Levels.LevelOf(gun.Position, map) == 1, false,
                $"spawned {gun.Spawned} at {(gun.Spawned ? RdCell(gun.Position, map) : "-")}");
            RdInlineWatch("R6", "GenPlace.TryFindPlaceSpotNear");
            RdEnd(map);
        }

        private static void RdR7(Map map)
        {
            if (!ModsConfig.RoyaltyActive)
            {
                Info("R7 Royalty not active: Waterskip skipped");
                return;
            }
            RdBegin(map, 8, "R7 waterskip", 7);
            RdSlab(map, new CellRect(rdFix.x0, 0, 11, 9));
            IntVec3 target = RdF2(rdFix.x0 + 5, 0);
            List<IntVec3> up = new List<IntVec3>(), down = new List<IntVec3>();
            for (int dx = -1; dx <= 1; dx++)
            {
                up.Add(RdF2(rdFix.x0 + 5 + dx, 0));
                up.Add(RdF2(rdFix.x0 + 5 + dx, 1));
                down.Add(RdG(rdFix.x0 + 5 + dx, RdNorth));
            }
            foreach (IntVec3 f in up.Concat(down))
            {
                RdWood(map, f, 20);
                FireUtility.TryStartFireIn(f, map, 0.5f, null);
            }
            int upBefore = FiresIn(map, up), downBefore = FiresIn(map, down);
            Pawn caster = RdCaster(map, RdF2(rdFix.x0 + 5, 7));
            bool cast = RdCast(map, caster, target);
            int upAfter = FiresIn(map, up), downAfter = FiresIn(map, down);
            Result("R7 Waterskip cast on floor 2's south row puts out floor 2's fires", cast && upBefore > 0 && upAfter == 0, $"cast {cast}, floor-2 fires {upBefore} -> {upAfter}");
            RdExpect("R7 the ground's north row (2 raw rows from the target) still burns", downBefore > 0 && downAfter > 0, false, $"ground fires {downBefore} -> {downAfter}");
            RdInlineWatch("R7", "CompAbilityEffect_Waterskip+<AffectedCells>d__1.MoveNext");
            RdEnd(map);
        }

        private static bool RdPyroThought(Pawn p)
        {
            List<Thought> th = new List<Thought>();
            p.needs.mood.thoughts.GetAllMoodThoughts(th);
            return th.Any(t => t.def.defName == "PyromaniacNearFire");
        }

        private static void RdR8(Map map)
        {
            RdBegin(map, 9, "R8 pyromaniac", 0);
            RdSlab(map, new CellRect(rdFix.x0, 0, 11, 8));
            Pawn p = RdPawn(map, RdF2(rdFix.x0 + 5, 0));
            p.story.traits.GainTrait(new Trait(TraitDefOf.Pyromaniac));
            RdWood(map, RdG(rdFix.x0 + 5, RdNorth), 75);
            FireUtility.TryStartFireIn(RdG(rdFix.x0 + 5, RdNorth), map, 1f, null);
            RdTicks(300);
            bool groundFire = FiresIn(map, new[] { RdG(rdFix.x0 + 5, RdNorth) }) > 0;
            bool thought = RdPyroThought(p);
            RdExpect("R8 a pyromaniac on floor 2's south row: no thought from a fire on the ground 2 raw rows away", groundFire && !thought, false, $"ground fire burning {groundFire}, thought {thought}");
            RdWood(map, RdF2(rdFix.x0 + 8, 2), 75);
            FireUtility.TryStartFireIn(RdF2(rdFix.x0 + 8, 2), map, 1f, null);
            RdTicks(300);
            bool control = RdPyroThought(p);
            Result("R8 control: a fire on floor 2 near the pyromaniac gives the thought", control, $"thought {control}");
            RdInlineWatch("R8", "ThoughtWorker_PyromaniacNearFlames.NumFiresNear");
            RdEnd(map);
        }

        // ==================================================================== P2 (mode radialperf)
        private static readonly List<double> rdTickMs = new List<double>();

        private static void RdPerf(Map map)
        {
            RdBegin(map, 90, "P2 perf", 0);
            AnnexMapComponent a = ProjectZ.Annex(map);
            int n = 0;
            CellRect area = new CellRect(map.Size.x / 2 - 20, rdGz / 2 - 20, 40, 40).ClipInsideMap(map);
            if (a != null)
            {
                RdSlab(map, area);
            }
            foreach (Pawn c in Colonists(map))
            {
                Draft(c, false);
            }
            for (int i = 0; i < 30; i++)
            {
                int k = a != null ? i % Math.Min(2, a.floors) : 0;
                IntVec3 g = new IntVec3(area.minX + 2 + i % 6 * 6, 0, area.minZ + 2 + i / 6 * 6);
                IntVec3 at = RdOn(g.x, g.z, k, map);
                if (!at.Standable(map))
                {
                    at = CellFinder.StandableCellNear(at, map, 5f);
                }
                if (at.IsValid)
                {
                    RdPawn(map, at, draft: false);
                    n++;
                }
            }
            BandRadial.Counting = false;
            RdTicks(300);
            rdTickMs.Clear();
            Stopwatch sw = new Stopwatch();
            TickManager tm = Find.TickManager;
            for (int i = 0; i < 2500; i++)
            {
                sw.Restart();
                tm.DoSingleTick();
                rdTickMs.Add(sw.Elapsed.TotalMilliseconds);
            }
            List<double> s = rdTickMs.OrderBy(x => x).ToList();
            Info($"P2 {n} colonists, floors {a?.floors ?? 1}, clip {(RdOff ? "off" : "on")}: median {s[s.Count / 2]:0.000} ms per tick, mean {s.Average():0.000}, p90 {s[(int)(s.Count * 0.9)]:0.000}");
            BandRadial.ResetCounters();
            BandRadial.Counting = true;
            RdTicks(1000);
            Info("P2 hottest clipped methods (1000 ticks): " + string.Join("; ", BandRadial.Rows.Where(r => r.calls > 0).OrderByDescending(r => r.cells).Take(10).Select(r => $"{r.Name} calls {r.calls} cells {r.cells} folded {r.folded} skipped {r.skipped}")));
            Result("P2 measured", true, $"{rdTickMs.Count} ticks");
            RdEnd(map);
            Wait(5, "rd_end");
        }
    }
}
