using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProjectZ.Compat;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode smartcon: Smarter Construction compat (Compat/SmarterConstruction.cs). Run with -projectzfloors=2
    // -projectzseed=7 and the mod loaded (Core, Biotech, Harmony, IsoRim, ProjectZ, Smarter Construction); with
    // -projectzfloors=1 only the ground checks run (C3, C4b). Router2's fixture tools: every check wipes the area on all
    // levels (isolated) and sets its own fixed random state; real blueprints, deliveries, jobs and ticks. The control is
    // SmarterConstruction.Off (our patches in, acting as the mod alone).
    //   S   state: the mod loaded, the compat on, one frame distance swapped (or off with a missing member, nothing patched);
    //   C0  control: on a small upper floor (8x8) with 4 wall blueprints, a colonist (a) and a constructoid (b) build none;
    //   C1  the compat on: the colonist (a) and the constructoid (b) build all 4;
    //   C2  a ground room left only by stairs (up one stairs, down another into it): its 3 pillars get built (control: not);
    //   C3  the mod's protection kept: a ground wall that would seal another colonist in a 3x3 room is not finished;
    //       C3b the same room empty: the wall is built (the fixture is buildable);
    //       C3u the same upstairs: a wall sealing a colonist into a 3x3 room on the slab (no stair end in it) is not
    //       finished; C3ub the same room empty: the wall is built;
    //   C4  build order: a ready frame upstairs (14.5 cells in 3D) and one 30 cells off on the ground: the builder's first
    //       frame is the upstairs one (control: the far one); C4b frames on one level (5 seeded layouts): the same first
    //       frame with the compat on and off (on -projectzfloors=1: the map without floors);
    //   C5  a builder walking up the stairs to a frame upstairs is not failed by the mod's 300-tick stuck rule.
    public static partial class TestRunner
    {
        // Test only (-projectzsmartconbreak=<member>): that Smarter Construction member counts as missing (Compat/SmarterConstruction).
        public static string SmartConBreak;

        private static Pawn scP, scQ, scM, scMech;
        private static readonly Queue<KeyValuePair<string, Action<Map>>> scQueue = new Queue<KeyValuePair<string, Action<Map>>>();
        private static Action<Map> scCheck;
        private static List<IntVec3> scWalls = new List<IntVec3>();
        private static Thing scUp, scFar;
        private static int scLinkAt, scIncompletable;
        private static readonly List<string> scPicks = new List<string>();
        private static bool scHooked;

        private const int ScFix = 7700;   // the base of this mode's fixture ids (FixtureSeed of the mode and the id)

        private static bool StepSmartCon(Map map)
        {
            switch (stage)
            {
                case "sc_setup":
                    ScSetup(map);
                    return true;
                case "sc_next":
                    if (scQueue.Count == 0)
                    {
                        SmarterConstruction.Off = false;
                        Result("no errors", errors == 0, $"{errors} errors, {warnings} warnings");
                        Finish();
                        return true;
                    }
                    KeyValuePair<string, Action<Map>> next = scQueue.Dequeue();
                    Out("---- " + next.Key);
                    next.Value(map);
                    return true;
                case "sc_check":
                    tickWatch = null;
                    Action<Map> c = scCheck;
                    scCheck = null;
                    stage = "sc_next";
                    c?.Invoke(map);
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------------ fixture

        private static void ScSetup(Map map)
        {
            DebugSettings.enableRandomMentalStates = false;
            NoRoofCollapse(map);
            Find.Storyteller.storytellerComps.Clear();
            Find.PlaySettings.autoRebuild = false;
            NoAmbushes(map);
            bool loaded = ModsConfig.ActiveModsInLoadOrder.Any(m => m.PackageIdNonUnique == SmarterConstruction.PackageId);
            bool ours = Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)?.Owners.Contains("Peren.IsoRimProjectZ.smarterconstruction") ?? false);
            Info($"smarter construction loaded {loaded}; compat {SmarterConstruction.State} ({SmarterConstruction.SkipReason}), {SmarterConstruction.DistSwaps} distance swaps, our patches {ours}; simulate missing '{SmarterConstruction.SimulateMissing}'");
            if (!loaded)
            {
                Result("S without the mod: compat off, nothing patched", SmarterConstruction.State == SmarterConstruction.Status.Absent && !ours, $"state {SmarterConstruction.State}, our patches {ours}");
                Result("no errors", errors == 0, $"{errors} errors");
                Finish();
                return;
            }
            if (SmarterConstruction.SimulateMissing != null)
            {
                Result("S a missing member: compat off (warning), nothing patched", SmarterConstruction.State == SmarterConstruction.Status.Skipped && !ours, $"state {SmarterConstruction.State} ({SmarterConstruction.SkipReason}), our patches {ours}, warnings {warnings}");
                Result("no errors", errors == 0, $"{errors} errors");
                Finish();
                return;
            }
            Result("S compat on: flood fill and one frame distance patched", SmarterConstruction.State == SmarterConstruction.Status.Active && ours && SmarterConstruction.DistSwaps == 1, $"state {SmarterConstruction.State}, {SmarterConstruction.DistSwaps} distance swaps");
            if (!scHooked)
            {
                scHooked = true;
                new Harmony("Peren.IsoRimProjectZ.test.smartcon").Patch(AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(ScEndJobPrefix)));
            }
            r2Area = new CellRect(map.Center.x - 22, map.Center.z - 16, 44, 32);
            List<Pawn> cols = Colonists(map);
            scP = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) ?? cols.First();
            scQ = cols.FirstOrDefault(p => p != scP);
            scM = cols.FirstOrDefault(p => p != scP && p != scQ) ?? scQ ?? scP;
            r2P = scP;
            r2Q = scQ;
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)).ToList())
            {
                t.Destroy();
            }
            foreach (Zone z in map.zoneManager.AllZones.ToList())
            {
                z.Delete(false);
            }
            bool floors = ProjectZ.Annex(map) != null;
            Info($"smartcon: area {r2Area}, builder {scP} (construction {scP.skills?.GetSkill(SkillDefOf.Construction)?.Level}), second {scQ}, mechanitor {scM}; floors {floors} (levels {Levels.Min(map)}..{Levels.Max(map)})");
            if (floors)
            {
                // a constructoid overseen by the mechanitor (work mode Work: the control group's default)
                PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Mech_Constructoid");
                if (kind != null && ModsConfig.BiotechActive)
                {
                    Rand.PushState(FixtureSeed($"{mode}_{ScFix}"));
                    try
                    {
                        if (scM.mechanitor == null)
                        {
                            scM.health.AddHediff(HediffDefOf.MechlinkImplant, scM.health.hediffSet.GetBrain());
                        }
                        scMech = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfPlayer));
                        GenSpawn.Spawn(scMech, R2ParkCell(map, 5), map);
                        scM.relations.AddDirectRelation(PawnRelationDefOf.Overseer, scMech);
                        scMech.DeSpawn();
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                }
                Info($"constructoid {scMech}, overseer {scMech?.GetOverseer()}, work mode {(scM.mechanitor != null && scMech != null ? scM.mechanitor.GetControlGroup(scMech)?.WorkMode?.defName : "none")}");
                ScAdd("C0a control: colonist, small upper floor", m => ScSlabWalls(m, "C0a", true, false));
                ScAdd("C0b control: constructoid, small upper floor", m => ScSlabWalls(m, "C0b", true, true));
                ScAdd("C1a colonist, small upper floor", m => ScSlabWalls(m, "C1a", false, false));
                ScAdd("C1b constructoid, small upper floor", m => ScSlabWalls(m, "C1b", false, true));
                ScAdd("C2 control: ground room left by stairs", m => ScRoom(m, true));
                ScAdd("C2 ground room left by stairs", m => ScRoom(m, false));
            }
            ScAdd("C3 a wall sealing a colonist in", m => ScSeal(m, true));
            ScAdd("C3b the same wall, room empty", m => ScSeal(m, false));
            if (floors)
            {
                ScAdd("C3u an upstairs wall sealing a colonist in", m => ScSealUp(m, true));
                ScAdd("C3ub the same upstairs wall, room empty", m => ScSealUp(m, false));
            }
            if (floors)
            {
                ScAdd("C4 control: build order", m => ScOrder(m, true));
                ScAdd("C4 build order", m => ScOrder(m, false));
            }
            for (int i = 0; i < 5; i++)
            {
                int k = i;
                ScAdd($"C4b layout {k} control", m => ScSame(m, k, true));
                ScAdd($"C4b layout {k}", m => ScSame(m, k, false));
            }
            if (floors)
            {
                ScAdd("C5 stuck rule on the stairs", ScStuck);
            }
            stage = "sc_next";
        }

        private static void ScAdd(string name, Action<Map> start) => scQueue.Enqueue(new KeyValuePair<string, Action<Map>>(name, start));

        // Counts the finish-frame jobs that ended Incompletable (the mod's stuck rule ends them so).
        private static void ScEndJobPrefix(Pawn_JobTracker __instance, JobCondition condition)
        {
            if (condition == JobCondition.Incompletable && __instance.curJob?.def == JobDefOf.FinishFrame)
            {
                scIncompletable++;
            }
        }

        // A clean area: the mech away, every colonist parked but the builders, the builders' work set to construction only.
        private static void ScBase(Map map, int seed, Action build, params Pawn[] builders)
        {
            if (scMech != null && scMech.Spawned)
            {
                scMech.jobs?.StopAll();
                scMech.DeSpawn();
            }
            R2Clear(map);
            R2Park(map, builders);
            R2Feed(map);
            Rand.PushState(FixtureSeed($"{mode}_{seed}"));
            try
            {
                build();
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            }
            finally
            {
                Rand.PopState();
            }
            foreach (Pawn b in builders)
            {
                Draft(b, false);
                R2Work(b, WorkTypeDefOf.Construction);
            }
            R2Mark();
            scLinkAt = SmarterConstruction.OpenedLink;
            scIncompletable = 0;
        }

        private static Pawn ScMech(Map map, IntVec3 at)
        {
            if (scMech == null)
            {
                return null;
            }
            GenSpawn.Spawn(scMech, at, map);
            if (scMech.needs?.energy != null)
            {
                scMech.needs.energy.CurLevel = scMech.needs.energy.MaxLevel;
            }
            return scMech;
        }

        private static Thing ScWood(Map map, IntVec3 c, int n)
        {
            Thing t = ThingMaker.MakeThing(ThingDefOf.WoodLog);
            t.stackCount = n;
            t = GenSpawn.Spawn(t, c, map);
            t.SetForbidden(false, false);
            return t;
        }

        private static void ScBlueprint(Map map, IntVec3 c)
        {
            AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, c, Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
            if (!ok.Accepted)
            {
                Info($"  wall blueprint at {c}: CanPlaceBlueprintAt '{ok.Reason}' (placed anyway)");
            }
            GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, c, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
            scWalls.Add(c);
        }

        // A wall frame with all its materials in (ready to finish).
        private static Thing ScFrame(Map map, IntVec3 c)
        {
            Frame f = (Frame)ThingMaker.MakeThing(ThingDefOf.Wall.frameDef, ThingDefOf.WoodLog);
            f.SetFactionDirect(Faction.OfPlayer);
            GenSpawn.Spawn(f, c, map);
            foreach (ThingDefCountClass need in f.TotalMaterialCost())
            {
                Thing r = ThingMaker.MakeThing(need.thingDef);
                r.stackCount = need.count;
                f.resourceContainer.TryAdd(r);
            }
            return f;
        }

        private static bool ScBuilt(Map map, IntVec3 c) => c.GetEdifice(map)?.def == ThingDefOf.Wall;

        private static string ScWallState(Map map)
        {
            int built = scWalls.Count(c => ScBuilt(map, c));
            int frames = scWalls.Count(c => c.GetThingList(map).Any(t => t is Frame));
            int prints = scWalls.Count(c => c.GetThingList(map).Any(t => t is Blueprint));
            return $"walls {built}/{scWalls.Count} built, {frames} frames, {prints} blueprints";
        }

        private static string ScCounters() => $"pockets opened (link end) {SmarterConstruction.OpenedLink - scLinkAt}; finish jobs ended incompletable {scIncompletable}; {R2Stats()}";

        private static void ScRun(string name, int maxTicks, Func<bool> done, Action<Map> check)
        {
            scCheck = check;
            SeedTicks("smartcon_" + name);
            TickUntil(maxTicks, done, "sc_check", 60);
        }

        // The small upper floor: an 8x8 slab on level 1 over ground (8..15, 12..19), stairs up at (10, 9).
        private static void ScSlab(Map map)
        {
            R2Floor(map, new CellRect(RG(8, 12).x, RG(8, 12).z, 8, 8), 1);
            r2SA = R2Stairs(map, RG(10, 9), Rot4.North);
            if (r2SA == null)
            {
                Info("  stairs at " + RG(10, 9) + " refused: " + GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, RG(10, 9), Rot4.North, map, stuffDef: ThingDefOf.WoodLog).Reason);
            }
        }

        // ------------------------------------------------------------------------------------------------ C0, C1

        private static void ScSlabWalls(Map map, string id, bool off, bool mech)
        {
            SmarterConstruction.Off = off;
            scWalls = new List<IntVec3>();
            Pawn b = null;
            ScBase(map, ScFix + (mech ? 2 : 1), () =>
            {
                ScSlab(map);
                foreach (IntVec2 w in new[] { new IntVec2(12, 15), new IntVec2(13, 15), new IntVec2(14, 15), new IntVec2(11, 18) })
                {
                    ScBlueprint(map, RL(w.x, w.z, 1, map));
                }
                ScWood(map, RL(9, 17, 1, map), 75);
                if (mech)
                {
                    b = ScMech(map, RG(12, 5));
                }
                else
                {
                    b = scP;
                    Teleport(scP, RG(10, 5));
                }
            }, mech ? new Pawn[0] : new[] { scP });
            if (b == null)
            {
                Result(id + " builder", false, "no constructoid (Biotech?)");
                stage = "sc_next";
                return;
            }
            Pawn builder = b;
            Info($"  {id}: builder {builder}, compat {(off ? "OFF (control)" : "on")}, stairs {r2SA != null}, {ScWallState(map)}");
            ScRun(id, off ? 5000 : 15000, () => scWalls.All(c => ScBuilt(map, c)), m =>
            {
                int built = scWalls.Count(c => ScBuilt(m, c));
                string ev = $"{ScWallState(m)} after {ticksTotal} ticks; builder {builder} at {builder.Position} (level {Levels.LevelOf(builder.Position, m)}), job {builder.CurJobDef?.defName}; {ScCounters()}";
                if (off)
                {
                    Result(id + " control (mod alone): the walls on the small upper floor are not finished", built == 0, ev);
                }
                else
                {
                    Result(id + " compat on: every wall on the small upper floor is built", built == scWalls.Count, ev);
                }
                SmarterConstruction.Off = false;
            });
        }

        // ------------------------------------------------------------------------------------------------ C2

        // A walled ground room (interior 25..29 x 7..11) with stairs A inside up to a slab (24..36 x 12..16, level 1);
        // stairs B outside up to the same slab. The builder goes up B and down A into the room; 3 pillar blueprints inside.
        private static void ScRoom(Map map, bool off)
        {
            SmarterConstruction.Off = off;
            scWalls = new List<IntVec3>();
            Building_Stairs a = null, bStairs = null;
            ScBase(map, ScFix + 3, () =>
            {
                for (int x = 24; x <= 30; x++)
                {
                    for (int z = 6; z <= 12; z++)
                    {
                        if (x == 24 || x == 30 || z == 6 || z == 12)
                        {
                            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, RG(x, z), map);
                        }
                    }
                }
                R2Floor(map, new CellRect(RG(24, 12).x, RG(24, 12).z, 13, 5), 1);
                a = R2Stairs(map, RG(27, 9), Rot4.North);
                bStairs = R2Stairs(map, RG(34, 9), Rot4.North);
                foreach (IntVec2 w in new[] { new IntVec2(25, 7), new IntVec2(29, 7), new IntVec2(29, 11) })
                {
                    ScBlueprint(map, RG(w.x, w.z));
                }
                ScWood(map, RG(25, 10), 40);
                Teleport(scP, RG(34, 4));
            }, scP);
            if (a == null || bStairs == null)
            {
                Info($"  C2 stairs refused: A {GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, RG(27, 9), Rot4.North, map, stuffDef: ThingDefOf.WoodLog).Reason}, B {GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, RG(34, 9), Rot4.North, map, stuffDef: ThingDefOf.WoodLog).Reason}");
            }
            bool reach = map.reachability.CanReach(scP.Position, RG(26, 8), PathEndMode.OnCell, TraverseParms.For(scP));
            Info($"  C2: compat {(off ? "OFF (control)" : "on")}, stairs A {a != null}, B {bStairs != null}, room reachable {reach}; {ScWallState(map)}");
            ScRun("C2" + (off ? "off" : ""), off ? 5000 : 15000, () => scWalls.All(c => ScBuilt(map, c)), m =>
            {
                int built = scWalls.Count(c => ScBuilt(m, c));
                string ev = $"{ScWallState(m)} after {ticksTotal} ticks; builder at {scP.Position} (level {Levels.LevelOf(scP.Position, m)}), in room {CellRect.FromLimits(RG(25, 7), RG(29, 11)).Contains(scP.Position)}; {ScCounters()}";
                if (off)
                {
                    Result("C2 control (mod alone): the pillars in the room left by stairs are not finished", built == 0 && reach, ev);
                }
                else
                {
                    Result("C2 compat on: the pillars in the room left by stairs are built", built == scWalls.Count && reach, ev);
                }
                SmarterConstruction.Off = false;
            });
        }

        // ------------------------------------------------------------------------------------------------ C3

        // A walled 3x3 ground room (interior 30..32 x 20..22), its gap at (31, 19) planned as a wall; Q drafted inside
        // (sealed) or parked far (empty). The builder works from outside.
        private static void ScSeal(Map map, bool sealIn)
        {
            SmarterConstruction.Off = false;
            scWalls = new List<IntVec3>();
            ScBase(map, ScFix + 4, () =>
            {
                for (int x = 29; x <= 33; x++)
                {
                    for (int z = 19; z <= 23; z++)
                    {
                        if ((x == 29 || x == 33 || z == 19 || z == 23) && !(x == 31 && z == 19))
                        {
                            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, RG(x, z), map);
                        }
                    }
                }
                ScBlueprint(map, RG(31, 19));
                ScWood(map, RG(34, 16), 30);
                Teleport(scP, RG(31, 15));
            }, scP);
            if (sealIn && scQ != null)
            {
                Teleport(scQ, RG(31, 21));
                Draft(scQ, true);
            }
            ScRun(sealIn ? "C3" : "C3b", sealIn ? 4000 : 8000, () => sealIn ? false : ScBuilt(map, RG(31, 19)), m =>
            {
                bool built = ScBuilt(m, RG(31, 19));
                bool frame = RG(31, 19).GetThingList(m).Any(t => t is Frame);
                string ev = $"{ScWallState(m)} after {ticksTotal} ticks; second colonist {scQ} at {scQ?.Position} (inside {CellRect.FromLimits(RG(30, 20), RG(32, 22)).Contains(scQ?.Position ?? IntVec3.Invalid)}); builder at {scP.Position}, job {scP.CurJobDef?.defName}; {ScCounters()}";
                if (sealIn)
                {
                    Result("C3 the mod's protection kept: the wall that would seal a colonist in is not finished (materials delivered)", !built && frame, ev);
                }
                else
                {
                    Result("C3b the same wall with the room empty is built", built, ev);
                }
            });
        }

        // The small upper floor with a walled 3x3 room on it (walls 11..15 x 15..19, level 1; interior 12..14 x 16..18), its
        // gap at (13, 15) planned as a wall; the stairs' exit (10, 12) outside it. Q drafted inside (sealed) or parked far
        // (empty); the builder comes up the stairs from the ground.
        private static void ScSealUp(Map map, bool sealIn)
        {
            SmarterConstruction.Off = false;
            scWalls = new List<IntVec3>();
            ScBase(map, ScFix + 7, () =>
            {
                ScSlab(map);
                for (int x = 11; x <= 15; x++)
                {
                    for (int z = 15; z <= 19; z++)
                    {
                        if ((x == 11 || x == 15 || z == 15 || z == 19) && !(x == 13 && z == 15))
                        {
                            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, RL(x, z, 1, map), map);
                        }
                    }
                }
                ScBlueprint(map, RL(13, 15, 1, map));
                ScWood(map, RL(9, 14, 1, map), 30);
                Teleport(scP, RG(10, 5));
            }, scP);
            if (sealIn && scQ != null)
            {
                Teleport(scQ, RL(13, 17, 1, map));
                Draft(scQ, true);
            }
            IntVec3 gap = RL(13, 15, 1, map);
            CellRect inside = CellRect.FromLimits(RL(12, 16, 1, map), RL(14, 18, 1, map));
            ScRun(sealIn ? "C3u" : "C3ub", sealIn ? 5000 : 8000, () => sealIn ? false : ScBuilt(map, gap), m =>
            {
                bool built = ScBuilt(m, gap);
                bool frame = gap.GetThingList(m).Any(t => t is Frame);
                string ev = $"{ScWallState(m)} after {ticksTotal} ticks; second colonist {scQ} at {scQ?.Position} (inside {inside.Contains(scQ?.Position ?? IntVec3.Invalid)}); builder at {scP.Position} (level {Levels.LevelOf(scP.Position, m)}), job {scP.CurJobDef?.defName}; {ScCounters()}";
                if (sealIn)
                {
                    Result("C3u upstairs too: the wall that would seal a colonist into a room without a stair end is not finished (materials delivered)", !built && frame, ev);
                }
                else
                {
                    Result("C3ub the same upstairs wall with the room empty is built", built, ev);
                }
            });
        }

        // ------------------------------------------------------------------------------------------------ C4

        // Ready frames: UP on the slab (12, 14, level 1), FAR on the ground (40, 4); the builder at (10, 5) on the ground.
        private static void ScOrder(Map map, bool off)
        {
            SmarterConstruction.Off = off;
            scWalls = new List<IntVec3>();
            ScBase(map, ScFix + 5, () =>
            {
                ScSlab(map);
                scUp = ScFrame(map, RL(12, 14, 1, map));
                scFar = ScFrame(map, RG(40, 4));
                Teleport(scP, RG(10, 5));
            }, scP);
            float up3 = Patches.Dist3D.Distance(scP.Position, scUp.Position, map), far3 = Patches.Dist3D.Distance(scP.Position, scFar.Position, map);
            string geo = $"UP {scUp.Position} ({up3:0.0} cells in 3D, flat {(scP.Position - scUp.Position).LengthHorizontal:0.0}), FAR {scFar.Position} ({far3:0.0})";
            Info($"  C4: compat {(off ? "OFF (control)" : "on")}; {geo}");
            ScRun("C4" + (off ? "off" : ""), 3000, () => scP.CurJobDef == JobDefOf.FinishFrame, m =>
            {
                Thing t = scP.CurJobDef == JobDefOf.FinishFrame ? scP.CurJob.targetA.Thing : null;
                string name = t == null ? "none" : t == scUp ? "UP" : t == scFar ? "FAR" : t.ToString();
                string ev = $"first frame {name} after {ticksTotal} ticks; {geo}";
                if (off)
                {
                    Info("C4 control (mod alone, flat distance): " + ev);
                }
                else
                {
                    Result("C4 build order: the builder's first frame is the one upstairs (near in 3D), not the far ground one", t == scUp, ev);
                }
                scP.jobs.StopAll();
                SmarterConstruction.Off = false;
            });
        }

        // 5 ready frames on the ground at seeded places, the builder at (20, 15): the first frame with the compat off and on.
        private static void ScSame(Map map, int layout, bool off)
        {
            SmarterConstruction.Off = off;
            scWalls = new List<IntVec3>();
            List<Thing> frames = new List<Thing>();
            ScBase(map, ScFix + 100 + layout, () =>
            {
                Teleport(scP, RG(20, 15));
                HashSet<IntVec3> used = new HashSet<IntVec3>();
                while (frames.Count < 5)
                {
                    // half of them within the mod's 15-cell priority radius
                    IntVec3 c = frames.Count < 3 ? RG(20 + Rand.RangeInclusive(-12, 12), 15 + Rand.RangeInclusive(-10, 10)) : RG(Rand.RangeInclusive(1, 42), Rand.RangeInclusive(1, 30));
                    if (c == scP.Position || !used.Add(c) || !c.Standable(map))
                    {
                        continue;
                    }
                    frames.Add(ScFrame(map, c));
                }
            }, scP);
            ScRun("C4b" + layout, 3000, () => scP.CurJobDef == JobDefOf.FinishFrame, m =>
            {
                Thing t = scP.CurJobDef == JobDefOf.FinishFrame ? scP.CurJob.targetA.Thing : null;
                int idx = t == null ? -1 : frames.IndexOf(t);
                string ev = $"first frame #{idx} at {t?.Position} after {ticksTotal} ticks of {string.Join(" ", frames.Select(f => f.Position.ToString()))}";
                if (off)
                {
                    scPicks.Clear();
                    scPicks.Add(idx.ToString());
                    Info($"C4b layout {layout} control (mod alone): {ev}");
                }
                else
                {
                    string want = scPicks.FirstOrDefault();
                    Result($"C4b layout {layout}: frames on one level{(ProjectZ.Annex(m) == null ? " (map without floors)" : "")}: the same first frame as the mod alone", idx >= 0 && want == idx.ToString(),$"{ev}; mod alone: #{want}");
                }
                scP.jobs.StopAll();
                SmarterConstruction.Off = false;
            });
        }

        // ------------------------------------------------------------------------------------------------ C5

        // One ready frame upstairs (13, 17, level 1), the builder on the ground at (10, 5): it walks up and finishes it.
        private static void ScStuck(Map map)
        {
            SmarterConstruction.Off = false;
            scWalls = new List<IntVec3>();
            ScBase(map, ScFix + 6, () =>
            {
                ScSlab(map);
                scUp = ScFrame(map, RL(13, 17, 1, map));
                scWalls.Add(scUp.Position);
                ScWood(map, RL(9, 17, 1, map), 20);   // (a botched frame is planned again)
                Teleport(scP, RG(10, 5));
            }, scP);
            ScRun("C5", 6000, () => ScBuilt(map, scWalls[0]), m =>
            {
                bool built = ScBuilt(m, scWalls[0]);
                Result("C5 a builder climbing the stairs to a frame upstairs is not failed by the mod's stuck rule", built && scIncompletable == 0 && climbsColonists - r2ClimbsAt >= 1,
                    $"{ScWallState(m)} after {ticksTotal} ticks; {ScCounters()}");
            });
        }
    }
}
