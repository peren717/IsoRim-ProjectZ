using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // Mode raidladders (isorim-pz-siege-ladders-design.md, stage 2: raiders use ladders). Run with -projectzfloors=5
    // -projectzseed=7 -projectzmapsize=120. Every check clears the whole map (all levels), builds its own fixture under
    // its own random state, fires a real raid (or incident) and runs real lords, jobs and ticks (seeded tick by tick):
    //   RA  a colony on the ground, reachable: no ladder, no plan, the climb job giver never gives a job; a fingerprint of
    //       the end state (compare: a run with -projectznoraidladders -projectzraidonly=ra -projectzraidref=<this tag>)
    //   R1  a base on floor 2 (slab, room with a door, a walkway round it), its stairs taken up: an edge walk-in
    //       ImmediateAttack raid climbs and fights; R2 ImmediateAttackSmart; R3 StageThenAttack (no ladder while staging);
    //       R4 a siege until its assault; R5 an EdgeDrop raid
    //   R6  stairs taken up mid-raid (base on floor 1): the raiders switch to a ladder
    //   R7  a big raid: ladders placed against raiders who climbed
    //   R8  a "moat": raiders dropped on an island platform (floor 2), the base on another: down, then up
    //   R10 a kidnapper carries a downed colonist down a ladder and leaves
    //   R11 raiders on an island platform with nothing in range give up and leave down a ladder
    //   R12 a mechanoid raid places a lift
    //   R13 sappers, R14 breachers against a ladder-only base: the lord turns into an ordinary assault, then climbs;
    //       R13V/R14V the same raids against a walled base on the ground stay the game's
    //   R17 a trader caravan stranded on a stairless platform leaves down a ladder
    //   R18 a prison break on floor 1 with the stairs taken up: the prisoner gets out down a ladder
    //   R15 a manhunter pack and a shambler assault never place ladders
    //   R16 a base out of the 50-cell (3D) radius from the ground: raiders wander (recorded)
    //   MT  (-projectztallmountains -projectzfloors=4 -projectzmapsize=250 -projectzraidonly=mt) a base on a natural
    //       mountain top: the raiders climb
    // Screenshots (3D on): raiders on a ladder (R1), on the second ladder of the moat (R8), a mechanoid on a lift (R12).
    public static partial class TestRunner
    {
        private sealed class RlCase
        {
            public string id, name;
            public int maxTicks;
            public Action<Map> setup;
            public Func<Map, bool> done;
            public Action<Map> check;
        }

        private static List<RlCase> rlCases;
        private static int rlIdx, rlT0, rlTicksLeft;
        private static Faction rlFaction;
        private static Lord rlLord;
        private static readonly List<Pawn> rlRaiders = new List<Pawn>();
        private static int rlPlacedAt, rlMaxUp, rlUpLevel, rlFightSeen, rlFirstLadderTick, rlStagedLadders, rlHostileUp, rlHostileDown, rlCarriedDown, rlFirstUpTick;
        private static readonly HashSet<string> rlStepSeen = new HashSet<string>();
        private static readonly HashSet<string> rlJobsUp = new HashSet<string>();
        private static readonly List<Building_SiegeLadder> rlLadders = new List<Building_SiegeLadder>();
        private static CellRect rlBase, rlIsland;
        private static List<Pawn> rlCols = new List<Pawn>();
        private static float rlColHealth0;
        private static string rlShot;
        private static readonly HashSet<string> rlShotsTaken = new HashSet<string>();
        private static Building_Stairs rlStairs;
        private static bool rlFlag;
        private static Pawn rlVictim;
        private static string rlNote;
        private static IntVec3 rlSpawn;
        private static readonly Dictionary<Pawn, int> rlInjuries0 = new Dictionary<Pawn, int>();
        private static int rlDegradedAt;

        private static IntVec3 RlG(int x, int z) => new IntVec3(x, 0, z);

        private static bool StepRaidLadders(Map map)
        {
            Rand.PushState((int)(StableHash("raidladders_" + stage + "_" + rlIdx) ^ (uint)(SeedArg ?? 717)));
            try
            {
                return StepRaidLaddersInner(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static bool StepRaidLaddersInner(Map map)
        {
            switch (stage)
            {
                case "rl_setup":
                    RlSetup(map);
                    return true;
                case "rl_run":
                    RlRun(map);
                    return true;
                case "rl_tick":
                    RlAfterTicks(map);
                    return true;
                case "rl_shot":
                    RlShotTake(map);
                    return true;
                case "rl_shot_done":
                    SeedTicks("rl_resume_" + rlCases[rlIdx].id + "_" + rlShotsTaken.Count);
                    RlTicks();
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------------ driver

        private static void RlSetup(Map map)
        {
            DebugSettings.enableRandomMentalStates = false;
            NoRoofCollapse(map);
            Find.Storyteller.storytellerComps.Clear();
            Find.PlaySettings.autoRebuild = false;
            NoAmbushes(map);
            RaidClimb.Off = GenCommandLine.CommandLineArgPassed("projectznoraidladders");
            RaidClimb.RepeatTiming = GenCommandLine.CommandLineArgPassed("projectzraidtiming");
            string only = GenCommandLine.TryGetCommandLineArg("projectzraidonly", out string o) ? o.ToLowerInvariant() : null;
            rlCases = RlAllCases().Where(c => only == null ? c.id != "mt" : only.Split(',').Contains(c.id)).ToList();
            Info($"raidladders: map {map.Size}, levels {Levels.Min(map)}..{Levels.Max(map)}, cases {string.Join(" ", rlCases.Select(c => c.id))}, feature {(RaidClimb.Off ? "OFF" : "on")}, colonists {string.Join(", ", Colonists(map))}");
            rlIdx = 0;
            stage = "rl_run";
        }

        private static void RlRun(Map map)
        {
            if (rlIdx >= rlCases.Count)
            {
                Info("raid ladders, all cases: " + RaidClimb.Stats() + $"; rim scans {ClimbPlanner.RimScans} ({ClimbPlanner.RimMs:0.0} ms in all)");
                Finish();
                return;
            }
            RlCase c = rlCases[rlIdx];
            Out($"---- raidladders {c.id}: {c.name}");
            RlReset(map);
            c.setup(map);
            rlT0 = Find.TickManager.TicksGame;
            rlTicksLeft = c.maxTicks;
            if (stage == "done")
            {
                return;
            }
            rlTimedInTicks = false;
            RlTime(map);
            SeedTicks("rl_" + c.id);
            RlTicks();
        }

        private static void RlTicks()
        {
            RlCase c = rlCases[rlIdx];
            TickUntil(Math.Max(1, rlTicksLeft), () =>
            {
                Map m = Find.CurrentMap;
                RlTrack(m);
                if (!rlTimedInTicks && Find.TickManager.TicksGame - rlT0 >= 60)
                {
                    rlTimedInTicks = true;
                    RlTime(m, "between ticks (tick " + (Find.TickManager.TicksGame - rlT0) + ")");
                }
                if (rlShot != null)
                {
                    return true;
                }
                return c.done(m);
            }, "rl_tick", 10);
        }

        private static void RlAfterTicks(Map map)
        {
            tickWatch = null;
            RlCase c = rlCases[rlIdx];
            rlTicksLeft = c.maxTicks - (Find.TickManager.TicksGame - rlT0);
            if (rlShot != null && rlTicksLeft > 0)
            {
                RlShot(map);
                return;
            }
            rlShot = null;
            c.check(map);
            Info($"{c.id} stats: {RaidClimb.Stats()}; ladders placed {LadderSite.Placed - rlPlacedAt}; after {Find.TickManager.TicksGame - rlT0} ticks");
            rlIdx++;
            stage = "rl_run";
        }

        // A screenshot in the middle of a case (3D view), then the ticks go on.
        private static Pawn rlShotPawn;
        private static int rlShotFloor;

        private static void RlShot(Map map)
        {
            Pawn p = rlShotPawn;
            IntVec3 g = p != null && p.Spawned ? Levels.Ground(p.Position, map) : rlBase.CenterCell;
            Info($"{rlShot}: {p} at {p?.Position} (level {(p != null ? Levels.LevelOf(p.Position, map) : -1)}) -> {p?.pather?.nextCell}, {p?.pather?.MovePercentage:P0}, kind {(p != null ? Links.Kind(p).ToString() : "-")}");
            Find.WindowStack.TryRemove(typeof(LudeonTK.EditWindow_Log), doCloseSound: false);
            ShotAt(map, rlShotFloor, g + IntVec3.East, 7f);
            Wait(3, "rl_shot");
        }

        private static void RlShotTake(Map map)
        {
            Capture(rlShot);
            rlShotsTaken.Add(rlShot);
            rlShot = null;
            Wait(3, "rl_shot_done");
        }

        // Asks for a shot of a hostile half-way up or down a ladder (once per name).
        private static void RlWantShot(Map map, string name, int floor, Func<Pawn, bool> who = null)
        {
            if (rlShotsTaken.Contains(name) || rlShot != null)
            {
                return;
            }
            LinkSteps s = LinkSteps.For(map);
            if (s == null)
            {
                return;
            }
            foreach (LinkSteps.Step st in s.steps)
            {
                Pawn p = st.pawn;
                if (p != null && p.Spawned && st.climbing && st.link.IsLadder && p.HostileTo(Faction.OfPlayer) && (who == null || who(p)) && p.pather.MovePercentage > 0.4f && p.pather.MovePercentage < 0.65f)
                {
                    rlShot = name;
                    rlShotPawn = p;
                    rlShotFloor = floor;
                    return;
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ tracking

        private static void RlReset(Map map)
        {
            rlFaction = null;
            rlLord = null;
            rlRaiders.Clear();
            rlPlacedAt = LadderSite.Placed;
            rlDegradedAt = RaidDegrade.Degraded;
            rlMaxUp = 0;
            rlUpLevel = 2;
            rlFightSeen = 0;
            rlFirstLadderTick = -1;
            rlFirstUpTick = -1;
            rlStagedLadders = 0;
            rlHostileUp = rlHostileDown = rlCarriedDown = 0;
            rlStepSeen.Clear();
            rlJobsUp.Clear();
            rlLadders.Clear();
            rlBase = CellRect.Empty;
            rlIsland = CellRect.Empty;
            rlStairs = null;
            rlFlag = false;
            rlVictim = null;
            rlNote = null;
            rlShot = null;
            RaidClimb.ResetStats();
            ClimbPlans.For(map)?.Clear();
        }

        private static void RlTrack(Map map)
        {
            KeepFed(map);
            foreach (Pawn c in rlCols)
            {
                if (c.Spawned && !c.Downed && c.drafter != null && !c.Drafted && c != rlVictim)
                {
                    Draft(c, true);
                }
            }
            if (rlFaction != null)
            {
                rlRaiders.RemoveAll(p => p == null);
                foreach (Pawn p in map.mapPawns.SpawnedPawnsInFaction(rlFaction))
                {
                    if (!rlRaiders.Contains(p))
                    {
                        rlRaiders.Add(p);
                    }
                }
            }
            int up = 0;
            foreach (Pawn p in rlRaiders)
            {
                if (!p.Spawned || p.Dead)
                {
                    continue;
                }
                int L = Levels.LevelOf(p.Position, map);
                if (L >= rlUpLevel)
                {
                    up++;
                    if (rlFirstUpTick < 0)
                    {
                        rlFirstUpTick = Find.TickManager.TicksGame - rlT0;
                    }
                    JobDef j = p.CurJobDef;
                    if (j != null)
                    {
                        rlJobsUp.Add(j.defName);
                        if (j == JobDefOf.AttackMelee || j == JobDefOf.AttackStatic || j == JobDefOf.Mine || j == JobDefOf.UseVerbOnThing || j == JobDefOf.Ignite)
                        {
                            rlFightSeen++;
                        }
                    }
                }
            }
            rlMaxUp = Math.Max(rlMaxUp, up);
            foreach (Pawn c in rlCols)
            {
                if (c.Spawned && c.health.hediffSet.hediffs.Count(h => h is Hediff_Injury) > (rlInjuries0.TryGetValue(c, out int n0) ? n0 : 0))
                {
                    rlFightSeen++;
                    break;
                }
            }
            foreach (Thing t in map.listerThings.ThingsOfDef(PZDefOf.PZ_SiegeLadder).Concat(map.listerThings.ThingsOfDef(PZDefOf.PZ_SiegeLift)))
            {
                if (t is Building_SiegeLadder b && !rlLadders.Contains(b))
                {
                    rlLadders.Add(b);
                    if (rlFirstLadderTick < 0)
                    {
                        rlFirstLadderTick = Find.TickManager.TicksGame - rlT0;
                    }
                    if (rlLord != null && rlLord.CurLordToil is LordToil_Stage)
                    {
                        rlStagedLadders++;
                    }
                }
            }
            LinkSteps s = LinkSteps.For(map);
            if (s != null)
            {
                foreach (LinkSteps.Step st in s.steps)
                {
                    if (st.pawn == null || !st.link.IsLadder || !st.climbing || !st.pawn.HostileTo(Faction.OfPlayer) && !rlRaiders.Contains(st.pawn))
                    {
                        continue;
                    }
                    string k = st.pawn.ThingID + "@" + st.link.thing?.ThingID + (st.up ? "u" : "d");
                    if (rlStepSeen.Add(k))
                    {
                        if (st.up)
                        {
                            rlHostileUp++;
                        }
                        else
                        {
                            rlHostileDown++;
                            if (st.carried != null)
                            {
                                rlCarriedDown++;
                            }
                        }
                    }
                }
            }
        }

        private static string RlSummary(Map map)
        {
            int alive = rlRaiders.Count(p => !p.Dead);
            int gone = rlRaiders.Count(p => !p.Dead && !p.Spawned);
            string ladders = string.Join(", ", rlLadders.Select(b => $"{b.def.defName} {b.foot}->{b.top} L{Levels.LevelOf(b.foot, map)}->L{Levels.LevelOf(b.top, map)} {b.Faction?.def.defName}"));
            return $"raiders {rlRaiders.Count} (alive {alive}, off the map {gone}), most on level {rlUpLevel}+ at once {rlMaxUp} (first after {rlFirstUpTick}), ladders {rlLadders.Count} [{ladders}] (first after {rlFirstLadderTick}), hostile climbs up {rlHostileUp} down {rlHostileDown} (carrying {rlCarriedDown}), fighting seen {rlFightSeen}, jobs up there {string.Join("/", rlJobsUp.Take(8))}, toil {rlLord?.CurLordToil?.GetType().Name}";
        }

        // ------------------------------------------------------------------------------------------------ fixtures

        // The whole map wiped on every level: things, roofs and fog gone, soil on the ground, open air above. Every pawn
        // but the colonists goes; the colonists wait drafted in the map's south-west corner.
        private static void RlClear(Map map)
        {
            LinkSteps.For(map)?.steps.Clear();
            // (a colonist killed in an earlier case comes back: every case starts with the same colonists)
            foreach (Corpse cp in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().Where(x => x.InnerPawn != null && x.InnerPawn.Faction == Faction.OfPlayer && x.InnerPawn.RaceProps.Humanlike).ToList())
            {
                ResurrectionUtility.TryResurrect(cp.InnerPawn);
            }
            foreach (Pawn o in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
            {
                o.Destroy();
            }
            foreach (Pawn p in map.mapPawns.AllPawns.Where(p => !p.Spawned && !p.IsColonist && !p.Dead && p.MapHeld == map).ToList())
            {
                if (!p.Destroyed)
                {
                    p.Destroy();
                }
            }
            NoAmbushes(map);
            AnnexMapComponent a = ProjectZ.Annex(map);
            int w = map.Size.x, h = a.groundZ;
            int i = 0;
            foreach (Pawn c in Colonists(map))
            {
                if (c.Downed)
                {
                    HealthUtility.HealNonPermanentInjuriesAndRestoreLegs(c);
                }
                Teleport(c, new IntVec3(2 + 2 * i++, 0, 2));
            }
            foreach (Thing f in map.listerThings.ThingsInGroup(ThingRequestGroup.Fire).ToList())
            {
                f.Destroy();
            }
            for (int L = Levels.Max(map); L >= Levels.Min(map); L--)
            {
                for (int z = 0; z < h; z++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        IntVec3 c = Levels.Cell(new IntVec3(x, 0, z), L, map);
                        if (!c.IsValid || !c.InBounds(map))
                        {
                            continue;
                        }
                        List<Thing> list = map.thingGrid.ThingsListAtFast(c);
                        for (int k = list.Count - 1; k >= 0; k--)
                        {
                            Thing t = list[k];
                            if (!(t is Pawn) && !t.Destroyed && t.def.destroyable)
                            {
                                t.Destroy();
                            }
                        }
                        TerrainDef want = L == 0 ? TerrainDefOf.Soil : PZDefOf.PZ_OpenAir;
                        if (c.GetTerrain(map) != want)
                        {
                            map.terrainGrid.SetTerrain(c, want);
                        }
                        if (map.roofGrid.Roofed(c))
                        {
                            map.roofGrid.SetRoof(c, null);
                        }
                        if (map.fogGrid.IsFogged(c))
                        {
                            map.fogGrid.Unfog(c);
                        }
                    }
                }
            }
            map.roofCollapseBuffer.Clear();
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            rlCols = Colonists(map);
        }

        // A base on level L over ground rect r: the slab, a wooden room inset by 'walk' cells with a door on the west wall,
        // the colonists in it, drafted, without weapons (they don't shoot back; the raiders have to come in).
        private static void RlBase(Map map, CellRect r, int L, int walk = 2, bool room = true, Pawn outside = null)
        {
            rlBase = r;
            if (L > 0)
            {
                R2Floor(map, r, L);
            }
            CellRect inner = r.ContractedBy(walk);
            if (room)
            {
                IntVec3 door = new IntVec3(inner.minX, 0, inner.CenterCell.z);
                foreach (IntVec3 g in inner.EdgeCells)
                {
                    IntVec3 c = Levels.Cell(g, L, map);
                    if (g == door)
                    {
                        Place(ThingDefOf.Door, ThingDefOf.WoodLog, c, map);
                    }
                    else
                    {
                        Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
                    }
                }
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            CellRect inside = room ? inner.ContractedBy(1) : inner;
            List<IntVec3> spots = inside.Cells.Select(g => Levels.Cell(g, L, map)).ToList();
            int i = 0;
            foreach (Pawn c in rlCols)
            {
                if (c == outside)
                {
                    continue;
                }
                c.equipment?.DestroyAllEquipment();
                Teleport(c, spots[(i++ * 3) % spots.Count]);
                Draft(c, true);
            }
            rlColHealth0 = rlCols.Sum(c => c.health.summaryHealth.SummaryHealthPercent);
            rlInjuries0.Clear();
            foreach (Pawn c in rlCols)
            {
                rlInjuries0[c] = c.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);
            }
        }

        // Stairs up to level 1 at the west edge of the base (ramp outside, exit on the slab's edge), as a player's base had.
        private static Building_Stairs RlStairs(Map map, CellRect r)
        {
            IntVec3 center = new IntVec3(r.minX - 2, 0, r.CenterCell.z + 3);
            Building_Stairs s = R2Stairs(map, center, Rot4.East);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            return s;
        }

        private static Faction RlEnemy() => LdRaiders();

        // A real raid (RaidEnemy) of 'faction' with this strategy and arrival, at spawnCenter.
        private static bool RlRaid(Map map, Faction faction, string strategy, PawnsArrivalModeDef arrive, IntVec3 spawnCenter, float points)
        {
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
            parms.faction = faction;
            parms.raidStrategy = DefDatabase<RaidStrategyDef>.GetNamedSilentFail(strategy);
            parms.raidArrivalMode = arrive;
            parms.spawnCenter = spawnCenter;
            parms.points = points;
            parms.forced = true;
            List<Pawn> before = map.mapPawns.AllPawnsSpawned.ToList();
            bool ok = parms.raidStrategy != null && IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);
            rlFaction = faction;
            rlSpawn = spawnCenter;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Except(before).Where(p => p.Faction == faction))
            {
                rlRaiders.Add(p);
            }
            rlLord = map.lordManager.lords.LastOrDefault(l => l.faction == faction);
            Info($"raid {strategy}/{arrive?.defName} of {faction?.Name} ({faction?.def.defName}) at {spawnCenter} (level {Levels.LevelOf(spawnCenter, map)}), {points} points: fired {ok}, {rlRaiders.Count} pawns now, lord {rlLord?.LordJob?.GetType().Name}");
            return ok;
        }

        // Standard base: floor 2 over (30..41, 54..65) on a 120 map (scaled with the map), raid from the west edge.
        private static CellRect RlBaseRect(Map map, int x0 = 30)
        {
            int zc = ProjectZ.Annex(map).groundZ / 2;
            return new CellRect(x0, zc - 6, 12, 12);
        }

        private static IntVec3 RlWestEdge(Map map) => new IntVec3(0, 0, ProjectZ.Annex(map).groundZ / 2);

        private static bool RlUpAndFighting(Map map) => rlMaxUp > 0 && rlFightSeen > 0;

        // ------------------------------------------------------------------------------------------------ cases

        private static IEnumerable<RlCase> RlAllCases()
        {
            // ---- RA: the colony on the ground, reachable
            yield return new RlCase
            {
                id = "ra", name = "colony on the ground, reachable: nothing changes", maxTicks = 2500,
                setup = m =>
                {
                    RlClear(m);
                    RlBase(m, RlBaseRect(m), 0);
                    rlUpLevel = 0;
                    RlRaid(m, RlEnemy(), "ImmediateAttack", PawnsArrivalModeDefOf.EdgeWalkIn, RlWestEdge(m), 300f);
                },
                done = m => false,
                check = m =>
                {
                    string fp = RlFingerprint(m);
                    string me = Path.Combine(outDir, "ra_fingerprint.txt");
                    File.WriteAllText(me, fp);
                    Result("RA a colony on the ground the raiders can reach: no ladder, no plan, the climb job giver gives nothing", rlLadders.Count == 0 && RaidClimb.Plans == 0 && RaidClimb.PlaceJobs == 0 && RaidClimb.BlockerJobs == 0 && RaidClimb.BudgetWaits == 0,
                        $"{RlSummary(m)}; climb giver consulted {RaidClimb.Invoked} times, past its checks {RaidClimb.Asked} (reachable {RaidClimb.Reachable}, no target {RaidClimb.NoTarget}), plans {RaidClimb.Plans}, jobs {RaidClimb.PlaceJobs + RaidClimb.BlockerJobs}");
                    if (GenCommandLine.TryGetCommandLineArg("projectzraidref", out string refTag))
                    {
                        // (recorded: two runs of the same seed with the feature on differ as well, the game's own run-to-run
                        // spread; the proof is above: the job giver never gave a job)
                        string other = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", refTag, "ra_fingerprint.txt");
                        string ofp = File.Exists(other) ? File.ReadAllText(other) : null;
                        Info($"RA end state with the feature {(RaidClimb.Off ? "off" : "on")} against run {refTag}: " + (ofp == null ? "no fingerprint at " + other : ofp == fp ? "the same" : $"different: this {fp.Length} chars, {refTag} {ofp.Length} chars; first difference at {FirstDiff(fp, ofp)}"));
                    }
                    Info("RA fingerprint: " + fp.Substring(0, Math.Min(400, fp.Length)));
                },
            };
            // ---- R1, R2, R5: assaults against a base on floor 2
            foreach ((string id, string strat, string arrive) in new[] { ("r1", "ImmediateAttack", "EdgeWalkIn"), ("r2", "ImmediateAttackSmart", "EdgeWalkIn"), ("r5", "ImmediateAttack", "EdgeDrop") })
            {
                yield return new RlCase
                {
                    id = id, name = $"{strat} {arrive} against a base on floor 2, its stairs taken up", maxTicks = 9000,
                    setup = m =>
                    {
                        RlClear(m);
                        CellRect r = RlBaseRect(m);
                        RlBase(m, r, 2);
                        RlStairsTakenUp(m, r);
                        RlRaid(m, RlEnemy(), strat, DefDatabase<PawnsArrivalModeDef>.GetNamed(arrive), arrive == "EdgeDrop" ? new IntVec3(6, 0, r.CenterCell.z) : RlWestEdge(m), 300f);
                    },
                    done = m =>
                    {
                        if (id == "r1")
                        {
                            RlWantShot(m, "rl_r1_raider_on_ladder", 2);
                        }
                        return RlUpAndFighting(m) && rlLadders.Count > 0;
                    },
                    check = m => Result($"{id.ToUpperInvariant()} {strat} ({arrive}) against a base on floor 2 without stairs: the raiders place a ladder, climb and fight up there", rlLadders.Count > 0 && rlHostileUp > 0 && RlUpAndFighting(m) && rlLadders.All(b => b.Faction == rlFaction), RlSummary(m)),
                };
            }
            // ---- R3: stage then attack
            yield return new RlCase
            {
                id = "r3", name = "StageThenAttack: stages on the ground, then climbs", maxTicks = 30000,
                setup = m =>
                {
                    RlClear(m);
                    RlBase(m, RlBaseRect(m), 2);
                    RlRaid(m, RlEnemy(), "StageThenAttack", PawnsArrivalModeDefOf.EdgeWalkIn, RlWestEdge(m), 300f);
                },
                done = m => RlUpAndFighting(m) && rlLadders.Count > 0,
                check = m => Result("R3 StageThenAttack: no ladder while staging on the ground; after the stage they place one, climb and fight", rlStagedLadders == 0 && rlLadders.Count > 0 && rlHostileUp > 0 && RlUpAndFighting(m), $"ladders while staging {rlStagedLadders}; {RlSummary(m)}"),
            };
            // ---- R4: a siege until its assault
            yield return new RlCase
            {
                id = "r4", name = "a siege that reaches its assault phase", maxTicks = 200000,
                setup = m =>
                {
                    RlClear(m);
                    RlBase(m, RlBaseRect(m), 2);
                    RlRaid(m, RlEnemy(), "Siege", PawnsArrivalModeDefOf.EdgeWalkIn, RlWestEdge(m), 500f);
                },
                done = m =>
                {
                    if (!rlFlag && rlLord != null && rlLord.CurLordToil is LordToil_AssaultColony)
                    {
                        rlFlag = true;
                        rlNote = $"assault from tick {Find.TickManager.TicksGame - rlT0}, raiders at {string.Join(" ", rlRaiders.Where(p => p.Spawned).Take(6).Select(p => p.Position + " d" + Mathf.Sqrt(Dist3D.DistSq(p.Position, Levels.Cell(rlBase.CenterCell, 2, m), m)).ToString("0")))}";
                    }
                    return RlUpAndFighting(m) && rlLadders.Count > 0 || rlLord != null && !m.lordManager.lords.Contains(rlLord);
                },
                check = m => Result("R4 a siege: once its assault starts the raiders place a ladder, climb and fight", rlFlag && rlLadders.Count > 0 && rlHostileUp > 0 && RlUpAndFighting(m), $"{rlNote ?? "no assault yet"}; {RlSummary(m)}"),
            };
            // ---- R6: stairs taken up mid-raid
            yield return new RlCase
            {
                id = "r6", name = "stairs taken up mid-raid (base on floor 1)", maxTicks = 9000,
                setup = m =>
                {
                    RlClear(m);
                    CellRect r = RlBaseRect(m);
                    RlBase(m, r, 1);
                    rlUpLevel = 1;
                    rlStairs = RlStairs(m, r);
                    Info($"R6 stairs {rlStairs} at {rlStairs?.Position}, top {rlStairs?.TopCell}");
                    RlRaid(m, RlEnemy(), "ImmediateAttack", PawnsArrivalModeDefOf.EdgeWalkIn, RlWestEdge(m), 300f);
                },
                done = m =>
                {
                    if (!rlFlag && rlStairs != null && rlRaiders.Any(p => p.Spawned && (p.Position - rlStairs.Position).LengthHorizontal < 8f && Levels.LevelOf(p.Position, m) == 0))
                    {
                        rlFlag = true;
                        rlNote = $"stairs taken up at tick {Find.TickManager.TicksGame - rlT0}, raider jobs {string.Join("/", rlRaiders.Where(p => p.Spawned).Select(p => p.CurJobDef?.defName).Distinct())}";
                        rlStairs.Destroy(DestroyMode.Deconstruct);
                        m.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    }
                    return rlFlag && rlLadders.Count > 0 && rlMaxUp > 0;
                },
                check = m => Result("R6 stairs taken up while the raiders walk to them: they switch to a ladder and get up", rlFlag && rlLadders.Count > 0 && rlHostileUp > 0 && rlMaxUp > 0, $"{rlNote ?? "the stairs were never reached"}; {RlSummary(m)}"),
            };
            // ---- R7: converging on one ladder
            yield return new RlCase
            {
                id = "r7", name = "a big raid: ladders placed against raiders who climbed", maxTicks = 9000,
                setup = m =>
                {
                    RlClear(m);
                    CellRect r = RlBaseRect(m);
                    RlBase(m, r, 2);
                    RlStairsTakenUp(m, r);
                    RlRaid(m, RlEnemy(), "ImmediateAttack", PawnsArrivalModeDefOf.EdgeWalkIn, RlWestEdge(m), 1200f);
                },
                done = m => rlHostileUp >= Math.Min(5, rlRaiders.Count) && rlLadders.Count > 0,
                check = m =>
                {
                    int climbers = rlStepSeen.Select(k => k.Substring(0, k.IndexOf('@'))).Distinct().Count();
                    Result("R7 a big raid converges: far fewer ladders than raiders who climbed", rlLadders.Count > 0 && climbers >= 3 && rlLadders.Count * 3 <= climbers + 2, $"{climbers} raiders climbed over {rlLadders.Count} ladder(s); {RlSummary(m)}");
                },
            };
            // ---- R8: the moat
            yield return new RlCase
            {
                id = "r8", name = "moat: raiders on an island platform (floor 2), the base on another: down, then up", maxTicks = 9000,
                setup = m =>
                {
                    RlClear(m);
                    CellRect r = RlBaseRect(m);
                    RlBase(m, r, 2);
                    rlIsland = new CellRect(r.minX - 16, r.minZ + 2, 8, 8);
                    R2Floor(m, rlIsland, 2);
                    m.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    RlRaid(m, RlEnemy(), "ImmediateAttack", PawnsArrivalModeDefOf.CenterDrop, Levels.Cell(rlIsland.CenterCell, 2, m), 200f);
                },
                done = m =>
                {
                    RlWantShot(m, "rl_r8_moat_second_ladder", 2, p => rlLadders.Any(b => rlBase.Contains(Levels.Ground(b.top, m))) && Links.Kind(p) == StepKind.LadderUp);
                    return rlMaxUp > 0 && rlLadders.Any(b => rlBase.Contains(Levels.Ground(b.top, m))) && RlRaidersOn(m, rlBase, 2) > 0;
                },
                check = m =>
                {
                    bool down = rlLadders.Any(b => rlIsland.Contains(Levels.Ground(b.top, m)));
                    bool up = rlLadders.Any(b => rlBase.Contains(Levels.Ground(b.top, m)));
                    int landed = rlRaiders.Count(p => rlIsland.Contains(Levels.Ground(p.Position, m)) && Levels.LevelOf(p.Position, m) == 2);
                    Result("R8 moat: from the island down a ladder, then up a ladder onto the base", down && up && rlHostileDown > 0 && rlHostileUp > 0 && RlRaidersOn(m, rlBase, 2) > 0, $"ladder off the island {down}, onto the base {up}, raiders on the base now {RlRaidersOn(m, rlBase, 2)}; {RlSummary(m)}");
                },
            };
            // ---- R10: kidnap
            yield return new RlCase
            {
                id = "r10", name = "a kidnapper carries a downed colonist down a ladder and leaves", maxTicks = 15000,
                setup = m =>
                {
                    RlClear(m);
                    CellRect r = RlBaseRect(m);
                    rlVictim = rlCols.Last();
                    RlBase(m, r, 2, outside: rlVictim);
                    rlVictim.equipment?.DestroyAllEquipment();
                    Teleport(rlVictim, Levels.Cell(new IntVec3(r.minX, 0, r.CenterCell.z + 2), 2, m));
                    Draft(rlVictim, false);
                    HealthUtility.DamageUntilDowned(rlVictim, allowBleedingWounds: false);
                    RlRaid(m, RlEnemy(), "ImmediateAttack", PawnsArrivalModeDefOf.EdgeWalkIn, RlWestEdge(m), 300f);
                },
                done = m => rlVictim != null && !rlVictim.Spawned && rlCarriedDown > 0 && Find.FactionManager.AllFactions.Any(f => f.kidnapped?.KidnappedPawnsListForReading.Contains(rlVictim) ?? false),
                check = m => Result("R10 the raiders climb, a kidnapper carries the downed colonist down a ladder and off the map", rlCarriedDown > 0 && !rlVictim.Spawned && !rlVictim.Dead && Find.FactionManager.AllFactions.Any(f => f.kidnapped?.KidnappedPawnsListForReading.Contains(rlVictim) ?? false),
                    $"victim {rlVictim} spawned {rlVictim.Spawned}, dead {rlVictim.Dead}, held by {rlVictim.ParentHolder}, kidnapped {Find.FactionManager.AllFactions.Any(f => f.kidnapped?.KidnappedPawnsListForReading.Contains(rlVictim) ?? false)}; {RlSummary(m)}"),
            };
            // ---- R11: giving up on an island, leaving down a ladder
            yield return new RlCase
            {
                id = "r11", name = "raiders on an island with nothing in range give up and leave down a ladder", maxTicks = 45000,
                setup = m =>
                {
                    RlClear(m);
                    int zc = ProjectZ.Annex(m).groundZ / 2;
                    rlIsland = new CellRect(14, zc - 4, 8, 8);
                    R2Floor(m, rlIsland, 2);
                    // the colonists on the ground, far off (beyond 50 cells)
                    RlBase(m, new CellRect(m.Size.x - 16, zc - 6, 12, 12), 0);
                    rlUpLevel = 2;
                    RlRaid(m, RlEnemy(), "ImmediateAttack", PawnsArrivalModeDefOf.CenterDrop, Levels.Cell(rlIsland.CenterCell, 2, m), 200f);
                },
                done = m =>
                {
                    if (!rlFlag && rlLord != null && rlLord.CurLordToil is LordToil_ExitMap)
                    {
                        rlFlag = true;
                        rlNote = $"gave up at tick {Find.TickManager.TicksGame - rlT0} with {rlLadders.Count} ladder(s) placed before";
                        rlStagedLadders = rlLadders.Count;
                    }
                    return rlFlag && rlRaiders.Count > 0 && rlRaiders.All(p => p.Dead || !p.Spawned);
                },
                check = m =>
                {
                    int left = rlRaiders.Count(p => !p.Dead && !p.Spawned);
                    Result("R11 on an island with nothing in range the raiders place no ladder; once they give up they leave down a ladder", rlFlag && rlStagedLadders == 0 && rlLadders.Count > 0 && rlHostileDown > 0 && left > 0 && left == rlRaiders.Count(p => !p.Dead), $"{rlNote ?? "never gave up"}; left the map {left}; {RlSummary(m)}");
                },
            };
            // ---- R12: mechanoids
            yield return new RlCase
            {
                id = "r12", name = "a mechanoid raid places a lift", maxTicks = 12000,
                setup = m =>
                {
                    RlClear(m);
                    CellRect r = RlBaseRect(m);
                    RlBase(m, r, 2);
                    RlRaid(m, Faction.OfMechanoids, "ImmediateAttack", PawnsArrivalModeDefOf.EdgeWalkIn, RlWestEdge(m), 400f);
                },
                done = m =>
                {
                    RlWantShot(m, "rl_r12_mech_on_lift", 2);
                    return rlLadders.Count > 0 && rlMaxUp > 0 && rlShotsTaken.Contains("rl_r12_mech_on_lift");
                },
                check = m => Result("R12 mechanoids place a lift (not a ladder), climb it and get up", rlLadders.Count > 0 && rlLadders.All(b => b.def == PZDefOf.PZ_SiegeLift) && rlHostileUp > 0 && rlMaxUp > 0, RlSummary(m)),
            };
            // ---- R13, R14: sappers and breachers against a ladder-only base
            foreach ((string id, string strat) in new[] { ("r13", "ImmediateAttackSappers"), ("r14", "ImmediateAttackBreaching") })
            {
                yield return new RlCase
                {
                    id = id, name = strat + " against a ladder-only base", maxTicks = 12000,
                    setup = m =>
                    {
                        RlClear(m);
                        RlBase(m, RlBaseRect(m), 2);
                        Faction f = Find.FactionManager.AllFactions.Where(x => x.HostileTo(Faction.OfPlayer) && x.def.humanlikeFaction && !x.def.hidden && !x.temporary && x.def.pawnGroupMakers != null && x.def.techLevel >= TechLevel.Industrial).OrderBy(x => x.loadID).FirstOrDefault() ?? RlEnemy();
                        if (!RlRaid(m, f, strat, PawnsArrivalModeDefOf.EdgeWalkIn, RlWestEdge(m), 500f))
                        {
                            rlNote = "the raid did not fire";
                        }
                        rlNote = (rlNote ?? "") + $" first toil {rlLord?.CurLordToil?.GetType().Name}";
                    },
                    done = m => RlUpAndFighting(m) && rlLadders.Count > 0,
                    check = m =>
                    {
                        string duties = string.Join(", ", rlRaiders.Where(p => p.Spawned).GroupBy(p => p.mindState.duty?.def.defName ?? "none").Select(gr => $"{gr.Key} x{gr.Count()}"));
                        bool climbed = rlLadders.Count > 0 && rlHostileUp > 0 && RlUpAndFighting(m);
                        Result($"{id.ToUpperInvariant()} {strat} against a ladder-only base: no real destination, so the lord turns into an ordinary assault; then they place a ladder, climb and fight",
                            RaidDegrade.Degraded > rlDegradedAt && climbed && rlLadders.All(b => b.Faction == rlFaction),
                            $"lords turned {RaidDegrade.Degraded - rlDegradedAt}; duties now {duties}; {rlNote}; {RlSummary(m)}");
                    },
                };
            }
            // ---- R13V, R14V: the same raids against a base on the ground they can reach (walls only): the game's own
            foreach ((string id, string strat) in new[] { ("r13v", "ImmediateAttackSappers"), ("r14v", "ImmediateAttackBreaching") })
            {
                yield return new RlCase
                {
                    id = id, name = strat + " against a walled base on the ground: stays the game's", maxTicks = 1500,
                    setup = m =>
                    {
                        RlClear(m);
                        RlBase(m, RlBaseRect(m), 0);
                        rlUpLevel = 0;
                        Faction f = Find.FactionManager.AllFactions.Where(x => x.HostileTo(Faction.OfPlayer) && x.def.humanlikeFaction && !x.def.hidden && !x.temporary && x.def.pawnGroupMakers != null && x.def.techLevel >= TechLevel.Industrial).OrderBy(x => x.loadID).FirstOrDefault() ?? RlEnemy();
                        RlRaid(m, f, strat, PawnsArrivalModeDefOf.EdgeWalkIn, RlWestEdge(m), 500f);
                    },
                    done = m => false,
                    check = m =>
                    {
                        string toil = rlLord?.CurLordToil?.GetType().Name;
                        Result($"{id.ToUpperInvariant()} {strat} against a base it can reach: its lord stays a {strat.Substring("ImmediateAttack".Length).ToLowerInvariant()} raid, no ladder", RaidDegrade.Degraded == rlDegradedAt && rlLadders.Count == 0 && (toil == "LordToil_AssaultColonySappers" || toil == "LordToil_AssaultColonyBreaching"),
                            $"toil {toil}, lords turned {RaidDegrade.Degraded - rlDegradedAt}, ladders {rlLadders.Count}, climb plans {RaidClimb.Plans}");
                    },
                };
            }
            // ---- R17: a trader caravan stranded on a stairless platform leaves down a ladder
            yield return new RlCase
            {
                id = "r17", name = "a trader caravan stranded on a stairless platform leaves", maxTicks = 70000,
                setup = m =>
                {
                    RlClear(m);
                    RlBase(m, RlBaseRect(m), 0);
                    int zc = ProjectZ.Annex(m).groundZ / 2;
                    rlIsland = new CellRect(14, zc - 4, 8, 8);
                    R2Floor(m, rlIsland, 2);
                    m.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    rlUpLevel = 2;
                    Faction f = Find.FactionManager.AllFactions.Where(x => !x.IsPlayer && !x.HostileTo(Faction.OfPlayer) && x.def.humanlikeFaction && !x.def.hidden && !x.temporary && x.def.caravanTraderKinds.Any()).OrderBy(x => x.loadID).FirstOrDefault();
                    List<Pawn> before = m.mapPawns.AllPawnsSpawned.ToList();
                    IncidentParms p = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.Misc, m);
                    p.faction = f;
                    p.forced = true;
                    p.spawnCenter = RlWestEdge(m);
                    bool fired = f != null && IncidentDefOf.TraderCaravanArrival.Worker.TryExecute(p);
                    List<Pawn> all = m.mapPawns.AllPawnsSpawned.Except(before).Where(x => x.Faction == f).ToList();
                    // stranded: the caravan's people up on the platform (animals can't climb: they go)
                    List<IntVec3> spots = rlIsland.ContractedBy(1).Cells.Select(g => Levels.Cell(g, 2, m)).ToList();
                    int i = 0;
                    foreach (Pawn x in all)
                    {
                        if (x.RaceProps.Humanlike)
                        {
                            Teleport(x, spots[i++ % spots.Count]);
                            rlRaiders.Add(x);
                        }
                        else
                        {
                            x.Destroy();
                        }
                    }
                    rlFaction = f;
                    rlLord = m.lordManager.lords.LastOrDefault(l => l.faction == f);
                    rlNote = $"caravan of {f?.Name} fired {fired}: {rlRaiders.Count} people on the platform, lord {rlLord?.LordJob?.GetType().Name}";
                    Info("R17 " + rlNote);
                },
                done = m => rlRaiders.Count > 0 && rlRaiders.All(x => x.Dead || !x.Spawned),
                check = m =>
                {
                    int left = rlRaiders.Count(x => !x.Dead && !x.Spawned);
                    Result("R17 a trader caravan stranded on a stairless platform places a ladder and leaves the map", rlRaiders.Count > 0 && left == rlRaiders.Count && rlLadders.Count > 0 && rlLadders.All(b => b.Faction == rlFaction),
                        $"{rlNote}; left {left} of {rlRaiders.Count}; ladders {string.Join(", ", rlLadders.Select(b => b.Faction?.Name + " " + b.foot + "->" + b.top))}; toil {rlLord?.CurLordToil?.GetType().Name}; after {Find.TickManager.TicksGame - rlT0} ticks");
                },
            };
            // ---- R18: a prison break with the stairs taken up: the prisoner gets out down a ladder
            yield return new RlCase
            {
                id = "r18", name = "a prison break on floor 1, its stairs taken up: out down a ladder", maxTicks = 15000,
                setup = m =>
                {
                    RlClear(m);
                    CellRect r = RlBaseRect(m);
                    List<Pawn> keep = rlCols;
                    rlCols = new List<Pawn>();
                    RlBase(m, r, 1);
                    rlCols = keep;
                    rlUpLevel = 1;
                    rlStairs = RlStairs(m, r);
                    Pawn pr = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Villager, RlEnemy(), forceGenerateNewPawn: true));
                    GenSpawn.Spawn(pr, Levels.Cell(r.CenterCell, 1, m), m);
                    pr.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
                    rlVictim = pr;
                    rlRaiders.Add(pr);
                    PrisonBreakUtility.StartPrisonBreak(pr);
                    rlLord = pr.GetLord();
                    rlFaction = pr.Faction;
                    rlNote = $"prisoner {pr} at {pr.Position}, breaking {PrisonBreakUtility.IsPrisonBreaking(pr)}, lord {rlLord?.LordJob?.GetType().Name}; stairs {rlStairs?.Position}";
                    rlStairs?.Destroy(DestroyMode.Deconstruct);
                    m.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    Info("R18 " + rlNote + ", stairs taken up");
                },
                done = m => rlVictim != null && !rlVictim.Spawned,
                check = m => Result("R18 a prisoner breaking out of a floor-1 prison whose stairs were taken up places a ladder, climbs down and gets off the map", rlVictim != null && !rlVictim.Spawned && !rlVictim.Dead && rlLadders.Count > 0 && rlHostileDown > 0,
                    $"{rlNote}; prisoner spawned {rlVictim?.Spawned} dead {rlVictim?.Dead}; ladders {rlLadders.Count}, down climbs {rlHostileDown}; toil {rlLord?.CurLordToil?.GetType().Name}; after {Find.TickManager.TicksGame - rlT0} ticks"),
            };
            // ---- R15: animals and shamblers
            yield return new RlCase
            {
                id = "r15", name = "a manhunter pack and a shambler assault never place ladders", maxTicks = 5000,
                setup = m =>
                {
                    RlClear(m);
                    RlBase(m, RlBaseRect(m), 2);
                    List<Pawn> before = m.mapPawns.AllPawnsSpawned.ToList();
                    IncidentParms p1 = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, m);
                    p1.points = 300f;
                    p1.forced = true;
                    p1.spawnCenter = RlWestEdge(m);
                    IncidentDef mh = DefDatabase<IncidentDef>.GetNamedSilentFail("ManhunterPack");
                    bool f1 = mh != null && mh.Worker.TryExecute(p1);
                    bool f2 = false;
                    IncidentDef sh = DefDatabase<IncidentDef>.GetNamedSilentFail("ShamblerAssault");
                    if (sh != null && ModsConfig.AnomalyActive)
                    {
                        IncidentParms p2 = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, m);
                        p2.points = 300f;
                        p2.forced = true;
                        p2.faction = Faction.OfEntities;
                        p2.spawnCenter = RlWestEdge(m) + new IntVec3(0, 0, 10);
                        f2 = sh.Worker.TryExecute(p2);
                    }
                    rlRaiders.AddRange(m.mapPawns.AllPawnsSpawned.Except(before));
                    rlNote = $"manhunters fired {f1}, shamblers fired {f2}: {rlRaiders.Count(p => p.RaceProps.Animal && !p.IsShambler)} animals, {rlRaiders.Count(p => p.IsShambler)} shamblers, {rlRaiders.Count(p => !p.RaceProps.Animal && !p.IsShambler)} other";
                },
                done = m => false,
                check = m => Result("R15 manhunting animals and shamblers walk about below: no ladder, no plan", rlLadders.Count == 0 && RaidClimb.Plans == 0 && rlRaiders.Count > 0, $"{rlNote}; climb giver asked {RaidClimb.Asked}, excluded {RaidClimb.Excluded}; {RlSummary(m)}"),
            };
            // ---- R16: out of range
            yield return new RlCase
            {
                id = "r16", name = "a base out of the 50-cell (3D) radius from the ground: raiders wander", maxTicks = 8000,
                setup = m =>
                {
                    RlClear(m);
                    int zc = ProjectZ.Annex(m).groundZ / 2;
                    RlBase(m, new CellRect(m.Size.x / 2 - 6, zc - 6, 12, 12), 2);
                    RlRaid(m, RlEnemy(), "ImmediateAttack", PawnsArrivalModeDefOf.EdgeWalkIn, RlWestEdge(m), 300f);
                },
                done = m => false,
                check = m =>
                {
                    IntVec3 target = Levels.Cell(rlBase.CenterCell, 2, m);
                    float nearest = rlRaiders.Where(p => p.Spawned).Select(p => Mathf.Sqrt(Dist3D.DistSq(p.Position, target, m))).DefaultIfEmpty(-1).Min();
                    Info($"R16 out of range: {RlSummary(m)}; nearest raider now {nearest:0.0} cells (3D) from the base centre; plans {RaidClimb.Plans}, no target {RaidClimb.NoTarget}");
                    Result("R16 (recorded) a base beyond 50 cells: raiders without a target in range place no ladder", RaidClimb.NoTarget > 0, $"ladders {rlLadders.Count}, raiders on floor 2 {rlMaxUp}");
                },
            };
            // ---- MT: a natural mountain top (tall mountains run only)
            yield return new RlCase
            {
                id = "mt", name = "a base on a natural mountain top", maxTicks = 15000,
                setup = m => RlMountainSetup(m),
                done = m =>
                {
                    RlWantShot(m, "rl_mt_raider_on_ladder", rlUpLevel);
                    return rlMaxUp > 0 && rlLadders.Count > 0;
                },
                check = m => Result($"MT a base on a natural mountain top (level {rlUpLevel}): the raiders place a ladder and climb onto it", rlLadders.Count > 0 && rlHostileUp > 0 && rlMaxUp > 0, $"{rlNote}; {RlSummary(m)}"),
            };
        }

        // Planner timings on this fixture, before any tick: the first plan (level rims scanned) and 20 more (rims kept),
        // from a raider of the raid to a colonist on another level (or a ground edge for a raider up on a floor).
        private static bool rlTimedInTicks;

        private static void RlTime(Map map, string when = "before the ticks")
        {
            Pawn p = rlRaiders.FirstOrDefault(x => x.Spawned && RaidClimb.Climber(x));
            RouteGraph g = RouteGraph.For(map);
            if (p == null || g == null)
            {
                return;
            }
            Pawn col = rlCols.FirstOrDefault(c => c.Spawned && !c.Downed && Levels.LevelOf(c.Position, map) != Levels.LevelOf(p.Position, map));
            bool exit = col == null;
            IntVec3 goal = col?.Position ?? IntVec3.Invalid;
            int sc = ClimbPlanner.CompAt(g, p.Position, map);
            if (exit && Levels.LevelOf(p.Position, map) == 0)
            {
                return;
            }
            List<double> ms = new List<double>();
            ClimbPlanner.Plan first = null;
            string firstBreak = null;
            for (int i = 0; i < 21; i++)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                ClimbPlanner.Plan plan = ClimbPlanner.Find(map, g, p, sc, exit, goal);
                ms.Add((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                if (i == 0)
                {
                    first = plan;
                    firstBreak = $"sites {ClimbPlanner.LastSites}, components {ClimbPlanner.LastComps}, collect {ClimbPlanner.LastCollectMs:0.000}, search {ClimbPlanner.LastSearchMs:0.000}, legs {ClimbPlanner.LastRefines} {ClimbPlanner.LastLegMs:0.000} ms";
                }
            }
            List<double> warm = ms.Skip(1).OrderBy(x => x).ToList();
            Info($"{rlCases[rlIdx].id} planner timing {when} ({map.Size.x} wide, {Levels.Count(map)} levels) from {p.Position} to {(exit ? "an edge" : goal.ToString())}: first {ms[0]:0.000} ms ({firstBreak}), then median {warm[warm.Count / 2]:0.000} ms, max {warm.Last():0.000} ms (last: collect {ClimbPlanner.LastCollectMs:0.000}, search {ClimbPlanner.LastSearchMs:0.000}, legs {ClimbPlanner.LastRefines} {ClimbPlanner.LastLegMs:0.000}, expanded {ClimbPlanner.Expanded}, pushes {ClimbPlanner.Pushes}, relaxed {ClimbPlanner.Relaxed}); plan: {first?.ToString() ?? "none"}; a fixed loop {ClimbPlanner.Bench():0.00} ms");
        }

        private static int RlRaidersOn(Map map, CellRect r, int L) => rlRaiders.Count(p => p.Spawned && r.Contains(Levels.Ground(p.Position, map)) && Levels.LevelOf(p.Position, map) == L);

        // Stairs onto the base built and taken up again (a base the player cut off).
        private static void RlStairsTakenUp(Map map, CellRect r)
        {
            // (stairs reach one floor: a stand-in platform on level 1 west of the base carries them; both go again)
            CellRect step = new CellRect(r.minX - 5, r.CenterCell.z - 1, 4, 3);
            R2Floor(map, step, 1);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Building_Stairs s = R2Stairs(map, new IntVec3(step.minX - 2, 0, step.CenterCell.z), Rot4.East);
            Info($"stairs onto a level-1 landing west of the base: {s} at {s?.Position}, top {s?.TopCell}; taken up again with the landing");
            s?.Destroy(DestroyMode.Deconstruct);
            foreach (IntVec3 g in step)
            {
                map.terrainGrid.SetTerrain(Levels.Cell(g, 1, map), PZDefOf.PZ_OpenAir);
                map.roofGrid.SetRoof(g, null);
            }
            foreach (Thing t in map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item && (t.Position - r.CenterCell).LengthHorizontal < 30).ToList())
            {
                t.Destroy();
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
        }

        // The end state of a case: every pawn (kind, cell, health), sorted; the colonists' health.
        private static string RlFingerprint(Map map)
        {
            List<string> rows = map.mapPawns.AllPawnsSpawned.Select(p => $"{p.kindDef.defName} {p.Position} {p.health.summaryHealth.SummaryHealthPercent:0.000} {p.CurJobDef?.defName}").OrderBy(s => s).ToList();
            rows.Add("ticks " + Find.TickManager.TicksGame);
            rows.Add("dead " + rlRaiders.Count(p => p.Dead));
            return string.Join("\n", rows);
        }

        private static string FirstDiff(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                if (a[i] != b[i])
                {
                    int s = Math.Max(0, i - 40);
                    return $"char {i}: '{a.Substring(s, Math.Min(80, a.Length - s))}' vs '{b.Substring(s, Math.Min(80, b.Length - s))}'";
                }
            }
            return a.Length == b.Length ? "none" : "length";
        }

        // MT: a mountain top on level 2 or higher with a ladder site whose foot stands on the open ground near a map edge;
        // the colonists go up there, the raid comes from the nearest edge.
        private static void RlMountainSetup(Map map)
        {
            if (!Mountains.On(map))
            {
                rlNote = "not a tall-mountain map";
                Result("MT a tall-mountain map", false, "run with -projectztallmountains");
                return;
            }
            foreach (Pawn o in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
            {
                o.Destroy();
            }
            NoAmbushes(map);
            rlCols = Colonists(map);
            AnnexMapComponent a = ProjectZ.Annex(map);
            int w = map.Size.x, h = a.groundZ;
            IntVec3 bestTop = IntVec3.Invalid, bestFoot = IntVec3.Invalid, bestEdge = IntVec3.Invalid;
            float bestD = float.MaxValue;
            for (int L = Levels.Max(map); L >= 2; L--)
            {
                for (int z = 2; z < h - 2; z += 1)
                {
                    for (int x = 2; x < w - 2; x += 1)
                    {
                        IntVec3 g = new IntVec3(x, 0, z);
                        int edge = Math.Min(Math.Min(x, w - 1 - x), Math.Min(z, h - 1 - z));
                        if (edge > 38 || Column.NaturalTop(g, map) != L)
                        {
                            continue;
                        }
                        IntVec3 top = Levels.Cell(g, L, map);
                        if (!top.Walkable(map))
                        {
                            continue;
                        }
                        Rot4 side = LadderSite.BestSide(top, map, out IntVec3 foot, out int span);
                        if (!side.IsValid || Levels.LevelOf(foot, map) != 0 || !map.reachability.CanReachMapEdge(foot, TraverseParms.For(TraverseMode.PassDoors)))
                        {
                            continue;
                        }
                        IntVec3 e = x == edge ? new IntVec3(0, 0, z) : w - 1 - x == edge ? new IntVec3(w - 1, 0, z) : z == edge ? new IntVec3(x, 0, 0) : new IntVec3(x, 0, h - 1);
                        float d = Mathf.Sqrt(Dist3D.DistSq(e, top, map));
                        if (d < 42 && d < bestD)
                        {
                            bestD = d;
                            bestTop = top;
                            bestFoot = foot;
                            bestEdge = e;
                        }
                    }
                }
                if (bestTop.IsValid)
                {
                    break;
                }
            }
            if (!bestTop.IsValid)
            {
                rlNote = "no mountain top near an edge with a ladder site";
                Result("MT a mountain top near a map edge with a ladder site onto open ground", false, rlNote);
                return;
            }
            rlUpLevel = Levels.LevelOf(bestTop, map);
            rlBase = CellRect.CenteredOn(Levels.Ground(bestTop, map), 3);
            // the colonists on the top (cells of that top, nearest first)
            List<IntVec3> spots = GenRadial.RadialCellsAround(bestTop, 4f, true).Where(c => c.InBounds(map) && ProjectZ.SameBand(c, bestTop, map) && c.Standable(map)).ToList();
            int i = 0;
            foreach (Pawn c in rlCols)
            {
                c.equipment?.DestroyAllEquipment();
                Teleport(c, spots[i++ % spots.Count]);
                Draft(c, true);
                foreach (IntVec3 f in GenRadial.RadialCellsAround(c.Position, 6f, true))
                {
                    if (f.InBounds(map) && map.fogGrid.IsFogged(f))
                    {
                        map.fogGrid.Unfog(f);
                    }
                }
            }
            rlNote = $"top {bestTop} (level {rlUpLevel}), a site with its foot at {bestFoot}, raid from {bestEdge} ({bestD:0.0} cells 3D)";
            Info("MT " + rlNote);
            RlRaid(map, RlEnemy(), "ImmediateAttack", PawnsArrivalModeDefOf.EdgeWalkIn, bestEdge, 300f);
        }
    }
}
