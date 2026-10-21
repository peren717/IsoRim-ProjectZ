using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProjectZ.Combat;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // Mode locustjump (JumpAcross: locusts jump to a fight on another floor; -projectzfloors=3 -projectzseed=7, and
    // -projectzfloors=1 for a map without floors). A cleared field of lanes, each its own fixture (a 9x9 floor-2 slab over
    // roofed ground, open sky over it), one colonist (unarmed, drafted, holding fire; the others leave the map), a fresh
    // adult locust per check spawned with a fixed seed, each check on its own tick seed, real ticks and the real think tree:
    //   hooks: the giver sits after the assault's fight, the manhunter's search and the hive defence; the Locust kind has
    //          the jump; no jump button on a hostile or a tame locust;
    //   up: a hostile locust (insects, assault lord, as a disturbed cocoon's) on the ground south of the slab, the colonist
    //       on it, no way up: it jumps up (one cast), lands next to the colonist and attacks; filmed mid-arc;
    //   cooldown: right after, the colonist is put back on the ground: the locust jumps down only once its cooldown ran out;
    //   roofed: the slab roofed over: no jump, the chain after it runs, the miss is remembered (no think loop; jobs counted);
    //   stairs: stairs up to the slab: it walks up and attacks, no jump;
    //   same: the colonist walled in on the ground: no jump (only between floors);
    //   down: the locust on the slab, the colonist on the ground south of it, no stairs: it jumps down and attacks;
    //   manhunter: a wild manhunter locust: it jumps up and attacks (the manhunter tree's hook).
    // Without floors: a hostile locust and a walled-in colonist: the jump is never asked for and never cast.
    public static partial class TestRunner
    {
        private const int LjLaneWidth = 12;

        private sealed class LjCase
        {
            public string name;
            public int lane;
            public bool shots;
        }

        private static IntVec3 ljO;
        private static Pawn ljCol, ljLocust;
        private static readonly List<Pawn> ljHidden = new List<Pawn>();
        private static List<LjCase> ljCases;
        private static int ljCase, ljShot;
        private static PawnKindDef ljKind;
        private static float ljOldMin = 1f;
        private static AbilityDef ljAbility;

        // what the locust did in the current check (LjWatch, every tick)
        private static Job ljLastJob;
        private static int ljJobs, ljCasts, ljTakeoffs, ljLandings, ljMeleeTick, ljStartTick;
        private static bool ljInFlight;
        private static IntVec3 ljLandCell;
        private static readonly List<int> ljTakeoffTicks = new List<int>();
        private static readonly Dictionary<string, int> ljJobDefs = new Dictionary<string, int>();
        private static PawnFlyer ljFlyer;
        private static int ljAsked, ljCooling, ljMemo, ljNoLanding, ljGiven, ljMaxLevel;

        private static readonly float[] ljFracs = { 0.25f, 0.5f, 0.75f };

        private static readonly AccessTools.FieldRef<PawnFlyer, int> ljTicksFlying = AccessTools.FieldRefAccess<PawnFlyer, int>("ticksFlying");
        private static readonly AccessTools.FieldRef<PawnFlyer, int> ljTicksTotal = AccessTools.FieldRefAccess<PawnFlyer, int>("ticksFlightTime");

        // ground cell (x, z) of lane i; the same lifted to floor 2
        private static IntVec3 LjG(int lane, int x, int z) => new IntVec3(ljO.x + lane * LjLaneWidth + x, 0, ljO.z + z);

        private static IntVec3 LjUp(int lane, int x, int z, Map map) => ProjectZ.CellOnFloor(LjG(lane, x, z), 1, map);

        private static CellRect LjSlab(int lane) => new CellRect(ljO.x + lane * LjLaneWidth + 1, ljO.z + 14, 9, 9);

        private static bool StepLocustJump(Map map)
        {
            switch (stage)
            {
                case "lj_setup":
                    LjSetup(map);
                    return true;
                case "lj_case":
                    LjNextCase(map);
                    return true;
                case "lj_up_fly":
                {
                    tickWatch = null;
                    if (ljTakeoffs == 0)
                    {
                        LjCheck(map, ljCases[ljCase].name);
                        return true;
                    }
                    ljFlyer = ljLocust.ParentHolder as PawnFlyer;
                    ljShot = 0;
                    LjShotWait();
                    return true;
                }
                case "lj_shoot":
                {
                    tickWatch = null;
                    Find.CameraDriver.config.sizeRange.min = 2f;
                    LjCase c = ljCases[ljCase];
                    IntVec3 g = LjG(c.lane, 5, c.name == "down" ? 10 : 12);
                    ShotAt(map, 1, g, 7f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    string pose = "landed";
                    if (ljFlyer != null && ljFlyer.Spawned)
                    {
                        pose = $"tick {ljTicksFlying(ljFlyer)}/{ljTicksTotal(ljFlyer)} at {ljFlyer.Position} (level {Levels.LevelOf(ljFlyer.Position, map)})";
                        if (JumpFlights.PoseOf(ljFlyer, out JumpFlights.Pose ps))
                        {
                            pose += $", band {ps.band}, rise {ps.rise:0.00}, height {ps.height:0.00}, shadow on level {ps.shadow}";
                        }
                    }
                    Info($"{c.name} shot {ljShot}: {pose}");
                    Wait(25, "lj_take");
                    return true;
                }
                case "lj_take":
                {
                    LjCase c = ljCases[ljCase];
                    ZCapture($"locustjump_{c.name}_{(ljShot < ljFracs.Length ? "s" + ljShot : "landed")}");
                    ljShot++;
                    if (ljShot < ljFracs.Length)
                    {
                        Wait(3, "lj_shotwait");
                    }
                    else if (ljShot == ljFracs.Length)
                    {
                        Wait(3, "lj_tickland");
                    }
                    else
                    {
                        Wait(3, "lj_after");
                    }
                    return true;
                }
                case "lj_shotwait":
                    LjShotWait();
                    return true;
                case "lj_tickland":
                    TickUntil(600, () => { LjWatch(map); return ljLocust.Spawned; }, "lj_landshot", 1);
                    return true;
                case "lj_landshot":
                    tickWatch = null;
                    TickUntil(20, () => { LjWatch(map); return false; }, "lj_shoot", 1);
                    return true;
                case "lj_after":
                {
                    // after the filmed flight: on to the attack
                    TickUntil(900, () => { LjWatch(map); return ljMeleeTick >= 0; }, "lj_check", 1);
                    return true;
                }
                case "lj_check":
                    tickWatch = null;
                    LjCheck(map, ljCases[ljCase].name);
                    return true;
                case "lj_cool_check":
                    tickWatch = null;
                    LjCoolCheck(map);
                    return true;
                case "lj_plain_check":
                    tickWatch = null;
                    LjPlainCheck(map);
                    return true;
            }
            return false;
        }

        private static void LjShotWait()
        {
            PawnFlyer f = ljFlyer;
            float frac = ljFracs[ljShot];
            Map map = Find.CurrentMap;
            TickUntil(400, () => { LjWatch(map); return f == null || !f.Spawned || ljTicksFlying(f) >= frac * ljTicksTotal(f); }, "lj_shoot", 1);
        }

        // ------------------------------------------------------------------ setup

        private static void LjSetup(Map map)
        {
            DebugSettings.enableRandomMentalStates = false;
            ljOldMin = Find.CameraDriver.config.sizeRange.min;
            ljKind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Locust");
            ljAbility = DefDatabase<AbilityDef>.GetNamedSilentFail("PZ_LocustJump");
            if (ljKind == null || ljAbility == null)
            {
                Result("locust jump setup", false, $"Locust kind {ljKind != null} (needs Odyssey), PZ_LocustJump {ljAbility != null}");
                Finish();
                return;
            }
            AnnexMapComponent an = ProjectZ.Annex(map);
            bool floors = an != null && an.floors >= 2;
            // the field: 6 lanes (or a small one without floors), cleared, unfogged, no roofs
            int w = floors ? 6 * LjLaneWidth : 24, h = 28;
            CellRect found = CellRect.Empty;
            int bestBad = int.MaxValue;
            int maxZ = floors ? an.groundZ : map.Size.z;
            for (int z = 3; z + h < maxZ - 3; z += 4)
            {
                for (int x = 3; x + w < map.Size.x - 3; x += 4)
                {
                    CellRect rr = new CellRect(x, z, w, h);
                    if (rr.ExpandedBy(1).Cells.Any(c => c.InBounds(map) && c.GetThingList(map).Any(t => !t.def.destroyable)))
                    {
                        continue;
                    }
                    int bad = rr.Cells.Count(c => c.GetTerrain(map).IsWater || c.GetTerrain(map).passability == Traversability.Impassable || map.roofGrid.Roofed(c) || c.GetFirstBuilding(map) != null);
                    if (bad < bestBad)
                    {
                        bestBad = bad;
                        found = rr;
                    }
                }
            }
            if (found.Area == 0)
            {
                Result("locust jump field", false, "map too small");
                Finish();
                return;
            }
            foreach (IntVec3 c in found.ExpandedBy(2))
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
                if (td.IsWater || td.passability == Traversability.Impassable)
                {
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
            }
            ljO = new IntVec3(found.minX, 0, found.minZ + 2);
            // one colonist stays (unarmed, drafted, holding fire); the others leave the map for the test
            List<Pawn> cols = Colonists(map).OrderBy(p => p.thingIDNumber).ToList();
            ljCol = cols[0];
            foreach (Pawn p in cols.Skip(1))
            {
                p.DeSpawn();
                ljHidden.Add(p);
            }
            ljCol.equipment?.DestroyAllEquipment();
            LjClearPawns(map);
            LjHooks(map);
            if (!floors)
            {
                LjPlain(map);
                return;
            }
            // the slabs: floor 2 over every lane, open sky over it, its ground roofed
            for (int lane = 0; lane < 6; lane++)
            {
                if (lane == 3)
                {
                    continue;   // "same": no slab
                }
                foreach (IntVec3 g in LjSlab(lane))
                {
                    map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                    IntVec3 u = ProjectZ.CellOnFloor(g, 1, map);
                    map.terrainGrid.SetTerrain(u, PZDefOf.PZ_UpperFloor);
                    map.roofGrid.SetRoof(u, null);
                    map.fogGrid.Unfog(u);
                }
            }
            // lane 1 (roofed): the slab roofed over
            foreach (IntVec3 g in LjSlab(1))
            {
                map.roofGrid.SetRoof(ProjectZ.CellOnFloor(g, 1, map), RoofDefOf.RoofConstructed);
            }
            // lane 2 (stairs): stairs up onto the slab from its west part (ramp on the ground under it, exit on floor 2)
            IntVec3 st = LjG(2, 2, 17);
            AcceptanceReport can = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, st, Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
            Thing stairs = can.Accepted ? Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, st, map, Rot4.North) : null;
            stairs?.SetFaction(null);   // (unowned: no lane's locust trashes it, TrashUtility)
            Result("stairs fixture placed", stairs != null, $"at {st} north: {can.Reason}");
            // lane 3 (same floor): a granite ring round the colonist's ground cell
            IntVec3 cc = LjG(3, 5, 12);
            ThingDef granite = DefDatabase<ThingDef>.GetNamedSilentFail("BlocksGranite") ?? ThingDefOf.WoodLog;
            foreach (IntVec3 c in GenAdj.CellsAdjacent8Way(new TargetInfo(cc, map)))
            {
                Place(ThingDefOf.Wall, granite, c, map).SetFaction(null);
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Info($"field {found}, origin {ljO}, storey {IsoRimBridge.StoreyHeight:0.00}; colonist {ljCol}; {ljHidden.Count} colonist(s) off the map; jump range {ljAbility.verbProperties.range}, cooldown {ljAbility.cooldownTicksRange}");
            ljCases = new List<LjCase>
            {
                new LjCase { name = "up", lane = 0, shots = true },
                new LjCase { name = "roofed", lane = 1 },
                new LjCase { name = "stairs", lane = 2 },
                new LjCase { name = "same", lane = 3 },
                new LjCase { name = "down", lane = 4, shots = true },
                new LjCase { name = "manhunter", lane = 5 },
            };
            ljCase = -1;
            Wait(10, "lj_case");
        }

        // The giver in the think trees, the jump on the Locust kind, and no button for a hostile or a tame locust.
        private static void LjHooks(Map map)
        {
            bool After(List<ThinkNode> nodes, Type before)
            {
                int i = nodes.FindIndex(n => n.GetType() == before);
                return i >= 0 && i + 1 < nodes.Count && nodes[i + 1] is JobGiver_AIJumpAcross;
            }
            List<ThinkNode> assault = DutyDefOf.AssaultColony.thinkNode.subNodes;
            ThinkTreeDef mental = DefDatabase<ThinkTreeDef>.GetNamed("MentalStateNonCritical");
            List<ThinkNode> man = mental.thinkRoot.ThisAndChildrenRecursive.FirstOrDefault(n => n.subNodes != null && n.subNodes.Any(s => s is JobGiver_Manhunter))?.subNodes;
            List<ThinkNode> hive = DefDatabase<DutyDef>.GetNamed("DefendAndExpandHive").thinkNode.subNodes;
            List<ThinkNode> hive2 = DefDatabase<DutyDef>.GetNamed("DefendHiveAggressively").thinkNode.subNodes;
            Result("hooks: the jump giver right after the assault's fight, the manhunter's search and the hive defences",
                After(assault, typeof(JobGiver_AIFightEnemies)) && man != null && After(man, typeof(JobGiver_Manhunter)) && After(hive, typeof(JobGiver_HiveDefense)) && After(hive2, typeof(JobGiver_HiveDefense)),
                $"assault {string.Join(",", assault.Select(n => n.GetType().Name))}; manhunter {(man == null ? "none" : string.Join(",", man.Select(n => n.GetType().Name)))}");
            Result("the Locust kind has the jump (marked JumpAcrossFloors, a jump verb, never used by the game's ability AI)",
                ljKind.abilities != null && ljKind.abilities.Contains(ljAbility) && ljAbility.HasModExtension<JumpAcrossFloors>() && typeof(Verb_CastAbilityJump).IsAssignableFrom(ljAbility.verbProperties.verbClass) && !ljAbility.aiCanUse,
                $"kind abilities {(ljKind.abilities == null ? "none" : string.Join(",", ljKind.abilities.Select(a => a.defName)))}, verb {ljAbility.verbProperties.verbClass?.Name}, aiCanUse {ljAbility.aiCanUse}");
            // buttons (as a player sees them: god mode off)
            bool god = DebugSettings.godMode;
            DebugSettings.godMode = false;
            try
            {
                Pawn hostile = LjMakeLocust(map, "lj_gizmo_hostile", Faction.OfInsects, CellFinder.RandomClosewalkCellNear(LjG(0, 5, 2), map, 2));
                Pawn tame = LjMakeLocust(map, "lj_gizmo_tame", Faction.OfPlayer, CellFinder.RandomClosewalkCellNear(LjG(1, 5, 2), map, 2));
                int gh = hostile.abilities?.GetGizmos().Count() ?? 0;
                int gt = tame.abilities?.GetGizmos().Count() ?? 0;
                Result("no jump button on a hostile or a tame locust", hostile.abilities?.GetAbility(ljAbility) != null && tame.abilities?.GetAbility(ljAbility) != null && gh == 0 && gt == 0,
                    $"hostile: ability {hostile.abilities?.GetAbility(ljAbility) != null}, {gh} button(s); tame: ability {tame.abilities?.GetAbility(ljAbility) != null}, {gt} button(s)");
                hostile.Destroy();
                tame.Destroy();
            }
            finally
            {
                DebugSettings.godMode = god;
            }
        }

        // An adult locust at c (seeded: the same locust every run).
        private static Pawn LjMakeLocust(Map map, string seedName, Faction faction, IntVec3 c)
        {
            Pawn p;
            Rand.PushState((int)(StableHash(seedName) ^ (uint)Math.Max(0, SeedArg ?? 717)));
            try
            {
                p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(ljKind, faction, forceGenerateNewPawn: true, fixedBiologicalAge: 1f, fixedChronologicalAge: 1f));
            }
            finally
            {
                Rand.PopState();
            }
            GenSpawn.Spawn(p, c, map);
            return p;
        }

        // ------------------------------------------------------------------ the checks

        private static void LjNextCase(Map map)
        {
            LjCleanup(map);
            LjClearPawns(map);
            ljCase++;
            if (ljCase >= ljCases.Count)
            {
                LjFinish(map);
                return;
            }
            LjCase c = ljCases[ljCase];
            int L = c.lane;
            IntVec3 colAt, locAt;
            switch (c.name)
            {
                case "same":
                    colAt = LjG(L, 5, 12);
                    locAt = LjG(L, 5, 3);
                    break;
                case "down":
                    colAt = LjG(L, 5, 4);
                    locAt = LjUp(L, 5, 16, map);
                    break;
                case "stairs":
                    colAt = LjUp(L, 6, 19, map);
                    locAt = LjG(L, 5, 3);
                    break;
                default:
                    colAt = LjUp(L, 5, 18, map);
                    locAt = LjG(L, 5, 3);
                    break;
            }
            Heal(ljCol);
            Teleport(ljCol, colAt);
            Draft(ljCol, true);
            LjMarkCounters();
            ljLocust = LjMakeLocust(map, "lj_" + c.name, c.name == "manhunter" ? null : Faction.OfInsects, locAt);
            if (c.name == "manhunter")
            {
                ljLocust.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.ManhunterPermanent, forced: true);
            }
            else
            {
                LordMaker.MakeNewLord(Faction.OfInsects, new LordJob_AssaultColony(Faction.OfInsects, canKidnap: false, canTimeoutOrFlee: false, canSteal: false), map, new[] { ljLocust });
            }
            LjResetWatch(map);
            Ability ab = ljLocust.abilities?.GetAbility(ljAbility);
            float d3 = Range3D.Distance(locAt, colAt, map);
            Info($"{c.name}: locust {ljLocust} ({ljLocust.ageTracker.CurLifeStage?.defName}, {(c.name == "manhunter" ? "wild manhunter" : "insects, assault")}) at {locAt} level {Levels.LevelOf(locAt, map)}; colonist at {colAt} level {Levels.LevelOf(colAt, map)}; 3D {d3:0.0}; jump {(ab == null ? "missing" : "range " + ab.verb.EffectiveRange.ToString("0.0"))}; locust can walk to the colonist {ljLocust.CanReach(ljCol, PathEndMode.Touch, Danger.Deadly, true, true, TraverseMode.PassDoors)}");
            SeedTicks("lj_" + c.name);
            switch (c.name)
            {
                case "up":
                case "down":
                    TickUntil(600, () => { LjWatch(map); return ljTakeoffs > 0; }, "lj_up_fly", 1);
                    break;
                case "manhunter":
                    TickUntil(1500, () => { LjWatch(map); return ljMeleeTick >= 0; }, "lj_check", 1);
                    break;
                case "stairs":
                    TickUntil(3000, () => { LjWatch(map); return ljMeleeTick >= 0 && ljMaxLevel >= 1 && ljLocust.Spawned && Levels.LevelOf(ljLocust.Position, map) >= 1; }, "lj_check", 1);
                    break;
                default:
                    TickUntil(1500, () => { LjWatch(map); return false; }, "lj_check", 1);
                    break;
            }
        }

        private static void LjCheck(Map map, string name)
        {
            LjCase c = ljCases[ljCase];
            int lvl = ljLocust.Spawned ? Levels.LevelOf(ljLocust.Position, map) : -9;
            int colLvl = Levels.LevelOf(ljCol.Position, map);
            string seen = LjSeen(map);
            switch (name)
            {
                case "up":
                case "down":
                case "manhunter":
                {
                    bool landedNear = ljLandings >= 1 && Levels.LevelOf(ljLandCell, map) == colLvl && ljLandCell.DistanceTo(ljCol.Position) <= 3f;
                    Result($"{name}: the locust jumps once to the colonist's floor, lands next to it and attacks",
                        ljCasts == 1 && ljTakeoffs == 1 && landedNear && ljMeleeTick >= 0, seen);
                    if (name == "up")
                    {
                        LjCooldown(map);
                        return;
                    }
                    break;
                }
                case "roofed":
                {
                    int computed = ljAsked - ljMemo - ljCooling;
                    int bound = 1500 / JumpAcross.MemoTicks + 2;
                    Result("roofed: no legal landing, no jump; the chain after it runs (other jobs), the miss is remembered (no think loop)",
                        ljCasts == 0 && ljTakeoffs == 0 && ljNoLanding > 0 && computed <= bound && ljJobs >= 2 && ljJobs <= 60,
                        $"{seen}; searches {computed} (bound {bound})");
                    break;
                }
                case "stairs":
                    Result("stairs: it walks up the stairs and attacks, no jump", ljCasts == 0 && ljTakeoffs == 0 && ljMaxLevel >= 1 && ljMeleeTick >= 0, seen);
                    break;
                case "same":
                    Result("same floor (colonist walled in, out of reach): no jump", ljCasts == 0 && ljTakeoffs == 0 && ljGiven == 0, seen);
                    break;
            }
            Wait(2, "lj_case");
        }

        // Right after the jump up: the colonist back on the ground (no stairs): a jump down, but only once the cooldown ran out.
        private static void LjCooldown(Map map)
        {
            Ability ab = ljLocust.abilities.GetAbility(ljAbility);
            int left = ab.CooldownTicksRemaining;
            if (ljTakeoffTicks.Count == 0)
            {
                Result("cooldown", false, "no first jump to start from");
                Wait(2, "lj_case");
                return;
            }
            Heal(ljCol);
            Teleport(ljCol, LjG(0, 5, 5));
            Draft(ljCol, true);
            Info($"cooldown: colonist put on the ground at {ljCol.Position}; locust at {ljLocust.Position} level {Levels.LevelOf(ljLocust.Position, map)}, cooldown left {left} ticks (first take-off at {ljTakeoffTicks[0] - ljStartTick})");
            ljMeleeTick = -1;
            // (the game asks the think tree again now and then: here every 20 ticks, its own override check, so the
            // giver is asked while the jump cools down)
            TickUntil(1500, () =>
            {
                LjWatch(map);
                if (ljLocust.Spawned && (Find.TickManager.TicksGame - ljStartTick) % 20 == 0 && ljLocust.CurJobDef != JobDefOf.CastJump)
                {
                    ljLocust.jobs.CheckForJobOverride();
                }
                return ljTakeoffs >= 2 && ljLocust.Spawned;
            }, "lj_cool_check", 1);
        }

        private static void LjCoolCheck(Map map)
        {
            int gap = ljTakeoffTicks.Count >= 2 ? ljTakeoffTicks[1] - ljTakeoffTicks[0] : -1;
            int cd = ljAbility.cooldownTicksRange.min;
            Result($"cooldown: no chain jump; the jump down comes only after the {cd}-tick cooldown, refused while cooling",
                ljTakeoffs == 2 && ljCasts == 2 && gap >= cd && ljCooling > 0 && Levels.LevelOf(ljLocust.Position, map) == 0,
                $"{LjSeen(map)}; take-offs {gap} ticks apart");
            Wait(2, "lj_case");
        }

        private static void LjPlain(Map map)
        {
            IntVec3 cc = new IntVec3(ljO.x + 12, 0, ljO.z + 14);
            ThingDef granite = DefDatabase<ThingDef>.GetNamedSilentFail("BlocksGranite") ?? ThingDefOf.WoodLog;
            Heal(ljCol);
            Teleport(ljCol, cc);
            Draft(ljCol, true);
            foreach (IntVec3 c in GenAdj.CellsAdjacent8Way(new TargetInfo(cc, map)))
            {
                Place(ThingDefOf.Wall, granite, c, map).SetFaction(null);
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            LjMarkCounters();
            ljLocust = LjMakeLocust(map, "lj_plain", Faction.OfInsects, new IntVec3(ljO.x + 12, 0, ljO.z + 4));
            LordMaker.MakeNewLord(Faction.OfInsects, new LordJob_AssaultColony(Faction.OfInsects, canKidnap: false, canTimeoutOrFlee: false, canSteal: false), map, new[] { ljLocust });
            LjResetWatch(map);
            Info($"no floors: locust {ljLocust} at {ljLocust.Position} with the jump {ljLocust.abilities?.GetAbility(ljAbility) != null}, colonist walled in at {cc}");
            SeedTicks("lj_plain");
            TickUntil(1500, () => { LjWatch(map); return false; }, "lj_plain_check", 1);
        }

        private static void LjPlainCheck(Map map)
        {
            Result("no floors: the locust has the jump but it is never asked for nor cast (the game's own behaviour)",
                ljLocust.abilities?.GetAbility(ljAbility) != null && ljAsked == 0 && ljCasts == 0 && ljTakeoffs == 0, LjSeen(map));
            LjCleanup(map);
            LjFinish(map);
        }

        // ------------------------------------------------------------------ watching

        private static void LjResetWatch(Map map)
        {
            ljLastJob = null;
            ljJobs = ljCasts = ljTakeoffs = ljLandings = 0;
            ljMeleeTick = -1;
            ljInFlight = false;
            ljLandCell = IntVec3.Invalid;
            ljTakeoffTicks.Clear();
            ljJobDefs.Clear();
            ljFlyer = null;
            ljMaxLevel = Levels.LevelOf(ljLocust.Position, map);
            ljStartTick = Find.TickManager.TicksGame;

        }

        private static int[] ljBase = new int[5];

        // (before the locust joins its lord: that already asks its think tree for a job)
        private static void LjMarkCounters()
        {
            ljBase = new[] { JumpAcross.Asked, JumpAcross.Cooling, JumpAcross.MemoHits, JumpAcross.NoLanding, JumpAcross.Given };
            ljAsked = ljCooling = ljMemo = ljNoLanding = ljGiven = 0;
        }

        private static void LjWatch(Map map)
        {
            Pawn p = ljLocust;
            if (p == null)
            {
                return;
            }
            ljAsked = JumpAcross.Asked - ljBase[0];
            ljCooling = JumpAcross.Cooling - ljBase[1];
            ljMemo = JumpAcross.MemoHits - ljBase[2];
            ljNoLanding = JumpAcross.NoLanding - ljBase[3];
            ljGiven = JumpAcross.Given - ljBase[4];
            bool flying = p.ParentHolder is PawnFlyer;
            if (flying && !ljInFlight)
            {
                ljTakeoffs++;
                ljTakeoffTicks.Add(Find.TickManager.TicksGame);
            }
            else if (!flying && ljInFlight && p.Spawned)
            {
                ljLandings++;
                ljLandCell = p.Position;
            }
            ljInFlight = flying;
            if (!p.Spawned)
            {
                return;
            }
            ljMaxLevel = Math.Max(ljMaxLevel, Levels.LevelOf(p.Position, map));
            Job j = p.CurJob;
            if (j != null && j != ljLastJob)
            {
                ljLastJob = j;
                ljJobs++;
                string k = j.def.defName;
                ljJobDefs[k] = ljJobDefs.TryGetValue(k, out int n) ? n + 1 : 1;
                if (ljJobs <= 25)
                {
                    Out($"  lj job {j.def.defName} A {j.targetA} at {p.Position} L{Levels.LevelOf(p.Position, map)} giver {j.jobGiver?.GetType().Name ?? "none"} tick {Find.TickManager.TicksGame - ljStartTick}");
                }
                if (j.def == JobDefOf.CastJump)
                {
                    ljCasts++;
                }
                if (j.def == JobDefOf.AttackMelee && j.targetA.Thing == ljCol && ljMeleeTick < 0)
                {
                    ljMeleeTick = Find.TickManager.TicksGame;
                }
            }
        }

        private static string LjSeen(Map map)
        {
            Pawn p = ljLocust;
            string at = p.Spawned ? $"{p.Position} level {Levels.LevelOf(p.Position, map)}" : "not spawned (" + p.ParentHolder + ")";
            return $"locust now at {at}; casts {ljCasts}, take-offs {ljTakeoffs}, landings {ljLandings} (last at {ljLandCell}, level {(ljLandCell.IsValid ? Levels.LevelOf(ljLandCell, map) : -9)}), attack on the colonist {(ljMeleeTick < 0 ? "none" : "after " + (ljMeleeTick - ljStartTick) + " ticks")}, highest level {ljMaxLevel}; jobs {ljJobs} ({string.Join(", ", ljJobDefs.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value))}); giver asked {ljAsked} (cooling {ljCooling}, memo {ljMemo}, no landing {ljNoLanding}, given {ljGiven}); colonist at {ljCol.Position} level {Levels.LevelOf(ljCol.Position, map)}";
        }

        // Every other pawn off the map (wild animals and pets are targets for a locust too): the colonist and the locust alone.
        private static void LjClearPawns(Map map)
        {
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (p != ljCol && p != ljLocust)
                {
                    p.Destroy();
                }
            }
        }

        private static void LjCleanup(Map map)
        {
            if (ljLocust != null && !ljLocust.Destroyed)
            {
                ljLocust.Destroy();
            }
            ljLocust = null;
            foreach (Lord l in map.lordManager.lords.Where(l => l.faction == Faction.OfInsects).ToList())
            {
                map.lordManager.RemoveLord(l);
            }
        }

        private static void LjFinish(Map map)
        {
            Find.CameraDriver.config.sizeRange.min = ljOldMin;
            Finish();
        }
    }
}
