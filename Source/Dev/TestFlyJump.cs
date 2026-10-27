using System;
using System.Collections.Generic;
using System.Linq;
using ProjectZ.Combat;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // Mode flyjump (FloorFlight: flying animals fly between floors as the locust jumps; -projectzfloors=3 -projectzseed=7).
    // A cleared field of lanes, each its own fixture (a 9x9 floor-2 slab over roofed ground, open sky over it), one
    // colonist (unarmed, drafted), a fresh animal per check spawned with a fixed seed, each check on its own tick seed, real
    // ticks and the real think tree:
    //   detect: the game's flight decides (Duck, Goose fly; Rat, Muffalo don't; the locust keeps its jump); the XML lists
    //           (noFly takes a flyer out, fly puts a walker in);
    //   up:     a manhunter duck on the ground, the colonist on the slab, no way up: it flies up (PZ_FlyAcross), lands next
    //           to the colonist and attacks;
    //   back:   right after, the colonist back on the ground: it flies down once the cooldown ran out, and attacks;
    //   roofed: the colonist in a walled, roofed room on the slab: no flight, the miss remembered;
    //   stairs: a manhunter rat (no flight), stairs up: it walks up and attacks, never flies;
    //   ladder: a manhunter rat, a ladder only: it never climbs nor flies, stays on the ground;
    //   optin:  a manhunter rat listed in FloorFlyersDef.fly: it flies up and attacks;
    //   flee:   a duck of a hostile faction (flees from enemies), the colonist close by on the ground, stairs on the lane:
    //           it flies up onto the slab instead of running;
    //   strand: the same without stairs: it still flies up (no walk back asked for);
    //   calm:   then hungry, the colonist still near: no flight down while it calms down (FloorUnstrand);
    //   food:   the colonist far away, hay on the ground: it flies down (goal food) after the calm-down and eats; then
    //           6000 ticks without another flight (no ping-pong);
    //   tamed:  a tamed macaw on a slab without stairs, its allowed area on the ground: it flies into the area; then 6000
    //           ticks without another flight;
    //   pen:    a tamed duck (a pen animal) on a slab without stairs, a fenced pen on the ground: it flies into the pen;
    //           then 6000 ticks without another flight.
    // (A flight reaches 19.9 cells: the strand lane sits over two lanes away from every lane with stairs.)
    public static partial class TestRunner
    {
        private const int FjLaneWidth = 12;

        private static IntVec3 fjO;
        private static Pawn fjCol, fjA;
        private static readonly List<Pawn> fjHidden = new List<Pawn>();
        private static List<string> fjCases;
        private static int fjCase;
        private static FloorFlyersDef fjLists;
        private static Faction fjHostile;

        // what the animal did in the current check
        private static Job fjLastJob;
        private static int fjJobs, fjFlyJobs, fjFlee, fjTakeoffs, fjLandings, fjMeleeTick, fjStartTick, fjMaxLevel, fjLadder;
        private static bool fjInFlight;
        private static IntVec3 fjLandCell;
        private static readonly List<int> fjTakeoffTicks = new List<int>();
        private static readonly Dictionary<string, int> fjJobDefs = new Dictionary<string, int>();
        private static int[] fjBase = new int[8];
        private static int fjAsked, fjMemo, fjNoLanding, fjGiven, fjFlights, fjFleeFlights, fjFleeNone, fjCooling;

        private static readonly string[] FjNames = { "up", "roofed", "stairs", "flee", "ladder", "optin", "strand", "tamed", "pen" };

        // unstrand flights (FloorUnstrand) in the current check
        private static int fjUnBaseGiven, fjUnBaseCalming, fjUnGiven, fjUnCalming, fjFleeTakeoff, fjIngestTick, fjLongTakeoffs, fjLongGiven;
        private static string fjLongName;
        private static Area fjArea;

        private static IntVec3 FjG(int lane, int x, int z) => new IntVec3(fjO.x + lane * FjLaneWidth + x, 0, fjO.z + z);

        private static IntVec3 FjUp(int lane, int x, int z, Map map) => ProjectZ.CellOnFloor(FjG(lane, x, z), 1, map);

        private static CellRect FjSlab(int lane) => new CellRect(fjO.x + lane * FjLaneWidth + 1, fjO.z + 14, 9, 9);

        private static int FjLane(string name) => Array.IndexOf(FjNames, name);

        private static bool StepFlyJump(Map map)
        {
            switch (stage)
            {
                case "fj_setup":
                    FjSetup(map);
                    return true;
                case "fj_case":
                    FjNextCase(map);
                    return true;
                case "fj_check":
                    tickWatch = null;
                    FjCheck(map);
                    return true;
                case "fj_back_check":
                    tickWatch = null;
                    FjBackCheck(map);
                    return true;
                case "fj_calm_check":
                    tickWatch = null;
                    FjCalmCheck(map);
                    return true;
                case "fj_food_check":
                    tickWatch = null;
                    FjFoodCheck(map);
                    return true;
                case "fj_long_check":
                    tickWatch = null;
                    FjLongCheck(map);
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ setup

        private static void FjSetup(Map map)
        {
            DebugSettings.enableRandomMentalStates = false;
            fjLists = DefDatabase<FloorFlyersDef>.GetNamedSilentFail("PZ_FloorFlyers");
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || an.floors < 2 || fjLists == null)
            {
                Result("fly jump setup", false, $"floors {an?.floors}, PZ_FloorFlyers {fjLists != null}");
                Finish();
                return;
            }
            int w = FjNames.Length * FjLaneWidth, h = 28;
            CellRect found = CellRect.Empty;
            int bestBad = int.MaxValue;
            for (int z = 3; z + h < an.groundZ - 3; z += 4)
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
                Result("fly jump field", false, "map too small");
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
            fjO = new IntVec3(found.minX, 0, found.minZ + 2);
            List<Pawn> cols = Colonists(map).OrderBy(p => p.thingIDNumber).ToList();
            fjCol = cols[0];
            foreach (Pawn p in cols.Skip(1))
            {
                p.DeSpawn();
                fjHidden.Add(p);
            }
            fjCol.equipment?.DestroyAllEquipment();
            FjClearPawns(map);
            // the slabs: floor 2 over every lane, open sky over it, its ground roofed
            for (int lane = 0; lane < FjNames.Length; lane++)
            {
                foreach (IntVec3 g in FjSlab(lane))
                {
                    map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                    IntVec3 u = ProjectZ.CellOnFloor(g, 1, map);
                    map.terrainGrid.SetTerrain(u, PZDefOf.PZ_UpperFloor);
                    map.roofGrid.SetRoof(u, null);
                    map.fogGrid.Unfog(u);
                }
            }
            ThingDef granite = DefDatabase<ThingDef>.GetNamedSilentFail("BlocksGranite") ?? ThingDefOf.WoodLog;
            // roofed: a walled room on the slab round the colonist's cell (5, 18), roofed
            int L = FjLane("roofed");
            foreach (IntVec3 g in CellRect.CenteredOn(FjG(L, 5, 18), 2))
            {
                IntVec3 u = ProjectZ.CellOnFloor(g, 1, map);
                map.roofGrid.SetRoof(u, RoofDefOf.RoofConstructed);
                if (g.x == FjG(L, 3, 0).x || g.x == FjG(L, 7, 0).x || g.z == fjO.z + 16 || g.z == fjO.z + 20)
                {
                    Place(ThingDefOf.Wall, granite, u, map).SetFaction(null);
                }
            }
            // stairs and flee: stairs up onto the slab from its west part (ramp on the ground under it, exit on floor 2)
            foreach (string n in new[] { "stairs", "flee" })
            {
                IntVec3 st = FjG(FjLane(n), 2, 17);
                AcceptanceReport can = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, st, Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
                Thing stairs = can.Accepted ? Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, st, map, Rot4.North) : null;
                stairs?.SetFaction(null);
                Result($"{n}: stairs placed", stairs != null, $"at {st} north: {can.Reason}");
            }
            // ladder: the player's emergency ladder from the slab's west edge (top (1, 18) on floor 2) down to (0, 18)
            {
                IntVec3 top = FjUp(FjLane("ladder"), 1, 18, map);
                AcceptanceReport can = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_EmergencyLadder, top, Rot4.West, map);
                Thing lad = can.Accepted ? Place(PZDefOf.PZ_EmergencyLadder, null, top, map, Rot4.West) : null;
                Result("ladder: ladder placed (a link up)", lad != null && StairGrid.For(map)?.IndexOf(lad) >= 0, $"at {top} west: {can.Reason}");
            }
            // pen: a fenced pen on the ground (fences x 2..8, z 1..7), its marker inside, open sky over it
            {
                int P = FjLane("pen");
                ThingDef fence = DefDatabase<ThingDef>.GetNamedSilentFail("Fence"), marker = DefDatabase<ThingDef>.GetNamedSilentFail("PenMarker");
                bool ok = fence != null && marker != null;
                if (ok)
                {
                    foreach (IntVec3 g in new CellRect(FjG(P, 2, 0).x, fjO.z + 1, 7, 7).EdgeCells)
                    {
                        Place(fence, ThingDefOf.WoodLog, g, map);
                    }
                    Place(marker, null, FjG(P, 5, 4), map);
                }
                Result("pen: fences and pen marker placed", ok, $"fence {fence != null}, marker {marker != null}");
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            fjHostile = Find.FactionManager.AllFactionsListForReading.Where(f => !f.IsPlayer && !f.Hidden && !f.defeated && f.HostileTo(Faction.OfPlayer) && f.def.animalsFleeDanger && f.def.humanlikeFaction).OrderBy(f => f.loadID).FirstOrDefault();
            Info($"field {found}, origin {fjO}; colonist {fjCol}; {fjHidden.Count} colonist(s) off the map; flight range {FloorFlight.Range}, cooldown {FloorFlight.CooldownTicks}; hostile faction {fjHostile}");
            FjDetect(map);
            fjCases = new List<string>(FjNames);
            fjCase = -1;
            Wait(10, "fj_case");
        }

        // Detection: the game's flight, the locust's own jump, the XML lists.
        private static void FjDetect(Map map)
        {
            string Line(string kind)
            {
                Pawn p = FjMake(map, kind, "fj_detect_" + kind, null, FjG(0, 5, 3 + kind.Length % 5));
                if (p == null)
                {
                    return kind + " missing";
                }
                string s = $"{kind} flies {FloorFlight.Flies(p)} (CanEverFly {p.flight?.CanEverFly}, jump {JumpAcross.AbilityOf(p) != null})";
                p.Destroy();
                return s;
            }
            Pawn duck = FjMake(map, "Duck", "fj_d1", null, FjG(1, 5, 3));
            Pawn goose = FjMake(map, "Goose", "fj_d2", null, FjG(1, 6, 3));
            Pawn rat = FjMake(map, "Rat", "fj_d3", null, FjG(1, 7, 3));
            Pawn muff = FjMake(map, "Muffalo", "fj_d4", null, FjG(1, 3, 3));
            string extra = string.Join("; ", new[] { "Chicken", "Locust", "Sparrow" }.Select(Line));
            Result("detect: the game's flight decides (Duck, Goose fly; Rat, Muffalo don't)",
                FloorFlight.Flies(duck) && FloorFlight.Flies(goose) && !FloorFlight.Flies(rat) && !FloorFlight.Flies(muff),
                $"duck {FloorFlight.Flies(duck)}, goose {FloorFlight.Flies(goose)}, rat {FloorFlight.Flies(rat)}, muffalo {FloorFlight.Flies(muff)}; {extra}");
            fjLists.noFly.Add(duck.def);
            fjLists.fly.Add(muff.def);
            FloorFlight.ResetLists();
            bool outDuck = !FloorFlight.Flies(duck), inMuff = FloorFlight.Flies(muff);
            fjLists.noFly.Remove(duck.def);
            fjLists.fly.Remove(muff.def);
            FloorFlight.ResetLists();
            Result("detect: FloorFlyersDef noFly takes a flyer out, fly puts a walker in", outDuck && inMuff && FloorFlight.Flies(duck) && !FloorFlight.Flies(muff), $"duck out {outDuck}, muffalo in {inMuff}");
            duck.Destroy();
            goose.Destroy();
            rat.Destroy();
            muff.Destroy();
        }

        // An adult animal of kind at c (seeded: the same animal every run).
        private static Pawn FjMake(Map map, string kind, string seedName, Faction faction, IntVec3 c)
        {
            PawnKindDef k = DefDatabase<PawnKindDef>.GetNamedSilentFail(kind);
            if (k == null)
            {
                return null;
            }
            Pawn p;
            Rand.PushState((int)(StableHash(seedName) ^ (uint)Math.Max(0, SeedArg ?? 717)));
            try
            {
                float adult = k.RaceProps.lifeStageAges.Last().minAge + 0.5f;
                p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(k, faction, forceGenerateNewPawn: true, fixedBiologicalAge: adult, fixedChronologicalAge: adult));
            }
            finally
            {
                Rand.PopState();
            }
            GenSpawn.Spawn(p, c, map);
            return p;
        }

        // ------------------------------------------------------------------ the checks

        private static void FjNextCase(Map map)
        {
            FjCleanup(map);
            FjClearPawns(map);
            fjCase++;
            if (fjCase >= fjCases.Count)
            {
                Finish();
                return;
            }
            string name = fjCases[fjCase];
            int L = FjLane(name);
            IntVec3 colAt = FjUp(L, 5, 18, map), aAt = FjG(L, 5, 3);
            string kind = "Duck";
            Faction f = null;
            bool manhunter = true;
            switch (name)
            {
                case "stairs":
                    colAt = FjUp(L, 6, 19, map);
                    kind = "Rat";
                    break;
                case "ladder":
                case "optin":
                    kind = "Rat";
                    break;
                case "flee":
                case "strand":
                    colAt = FjG(L, 5, 4);
                    aAt = FjG(L, 5, 11);
                    f = fjHostile;
                    manhunter = false;
                    break;
                case "tamed":
                case "pen":
                    colAt = FjG(0, 5, 3);
                    aAt = FjUp(L, 5, 18, map);
                    kind = name == "tamed" ? "Macaw" : "Duck";
                    f = Faction.OfPlayer;
                    manhunter = false;
                    break;
            }
            if (name == "optin")
            {
                fjLists.fly.Add(DefDatabase<ThingDef>.GetNamed("Rat"));
                FloorFlight.ResetLists();
            }
            Heal(fjCol);
            Teleport(fjCol, colAt);
            Draft(fjCol, true);
            FjMarkCounters();
            fjA = FjMake(map, kind, "fj_" + name, f, aAt);
            if (fjA == null)
            {
                Result(name + ": animal made", false, kind + " missing");
                Wait(2, "fj_case");
                return;
            }
            if (manhunter)
            {
                fjA.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.ManhunterPermanent, forced: true);
            }
            if (name == "tamed" || name == "pen")
            {
                // fed and rested (only the stranding counts), the tamed macaw restricted to the lane's ground
                fjA.needs.food.CurLevelPercentage = 1f;
                if (fjA.needs.rest != null)
                {
                    fjA.needs.rest.CurLevelPercentage = 1f;
                }
                if (name == "tamed" && map.areaManager.TryMakeNewAllowed(out Area_Allowed area))
                {
                    fjArea = area;
                    foreach (IntVec3 g in new CellRect(FjG(L, 0, 0).x, fjO.z, FjLaneWidth - 2, 10))
                    {
                        area[g] = true;
                    }
                    fjA.playerSettings.AreaRestrictionInPawnCurrentMap = area;
                }
            }
            FjResetWatch(map);
            Info($"{name}: {kind} {fjA} ({(manhunter ? "wild manhunter" : "faction " + fjA.Faction)}) at {aAt} level {Levels.LevelOf(aAt, map)}, flies {FloorFlight.Flies(fjA)}; colonist at {colAt} level {Levels.LevelOf(colAt, map)}; 3D {Range3D.Distance(aAt, colAt, map):0.0}; can walk to the colonist {fjA.CanReach(fjCol, PathEndMode.Touch, Danger.Deadly, true, true, TraverseMode.PassDoors)}");
            SeedTicks("fj_" + name);
            switch (name)
            {
                case "up":
                case "optin":
                    TickUntil(1500, () => { FjWatch(map); return fjMeleeTick >= 0 && fjLandings >= 1; }, "fj_check", 1);
                    break;
                case "stairs":
                    TickUntil(3000, () => { FjWatch(map); return fjMeleeTick >= 0 && fjA.Spawned && Levels.LevelOf(fjA.Position, map) >= 1; }, "fj_check", 1);
                    break;
                case "flee":
                case "strand":
                    TickUntil(900, () => { FjWatch(map); return fjLandings >= 1; }, "fj_check", 1);
                    break;
                case "tamed":
                case "pen":
                    TickUntil(1500, () => { FjWatch(map); return fjLandings >= 1; }, "fj_check", 1);
                    break;
                default:
                    TickUntil(1500, () => { FjWatch(map); return false; }, "fj_check", 1);
                    break;
            }
        }

        private static void FjCheck(Map map)
        {
            string name = fjCases[fjCase];
            int lvl = fjA.Spawned ? Levels.LevelOf(fjA.Position, map) : -9;
            int colLvl = Levels.LevelOf(fjCol.Position, map);
            string seen = FjSeen(map);
            switch (name)
            {
                case "up":
                case "optin":
                {
                    bool landedNear = fjLandings >= 1 && Levels.LevelOf(fjLandCell, map) == colLvl && fjLandCell.DistanceTo(fjCol.Position) <= 3f;
                    Result($"{name}: it flies once to the colonist's floor (PZ_FlyAcross), lands next to it and attacks",
                        fjFlyJobs == 1 && fjTakeoffs == 1 && landedNear && fjMeleeTick >= 0, seen);
                    if (name == "up")
                    {
                        FjBack(map);
                        return;
                    }
                    break;
                }
                case "roofed":
                {
                    int computed = fjAsked - fjMemo - fjCooling;
                    int bound = 1500 / JumpAcross.MemoTicks + 2;
                    Result("roofed: the colonist in a walled, roofed room upstairs: no flight, the miss remembered",
                        fjFlyJobs == 0 && fjTakeoffs == 0 && fjNoLanding > 0 && computed <= bound && lvl == 0, $"{seen}; searches {computed} (bound {bound})");
                    break;
                }
                case "stairs":
                    Result("stairs: a rat (no flight) walks up the stairs and attacks, never flies", fjTakeoffs == 0 && fjAsked == 0 && fjMaxLevel >= 1 && fjMeleeTick >= 0, seen);
                    break;
                case "ladder":
                    Result("ladder: a rat with a ladder only never climbs nor flies, stays on the ground", fjTakeoffs == 0 && fjAsked == 0 && fjLadder == 0 && fjMaxLevel == 0 && fjMeleeTick < 0, seen);
                    break;
                case "flee":
                    Result("flee: a duck fleeing from the colonist flies up onto the slab (stairs lead back)",
                        fjFleeFlights == 1 && fjTakeoffs == 1 && fjLandings == 1 && Levels.LevelOf(fjLandCell, map) == 1 && Range3D.Distance(fjLandCell, fjCol.Position, map) > Range3D.Distance(FjG(FjLane("flee"), 5, 11), fjCol.Position, map) + 5f, seen);
                    break;
                case "strand":
                    Result("strand: no stairs up: the fleeing duck still flies up onto a slab (no walk back asked for)",
                        fjFleeFlights == 1 && fjTakeoffs == 1 && fjLandings == 1 && Levels.LevelOf(fjLandCell, map) == 1 && fjUnGiven == 0, seen);
                    fjFleeTakeoff = fjTakeoffTicks.Count > 0 ? fjTakeoffTicks[0] : -1;
                    FjCalm(map);
                    return;
                case "tamed":
                case "pen":
                {
                    bool inside = name == "tamed" ? fjLandCell.IsValid && fjArea != null && fjArea[fjLandCell]
                        : fjA.Spawned && AnimalPenUtility.GetCurrentPenOf(fjA, false) != null;
                    string goal = name == "tamed" ? "area" : "pen";
                    Result($"{name}: a tamed {fjA.def.defName} stranded upstairs flies once into its {goal} on the ground",
                        fjUnGiven == 1 && FloorUnstrand.LastGoal == goal && fjTakeoffs == 1 && fjLandings == 1 && Levels.LevelOf(fjLandCell, map) == 0 && inside,
                        $"{seen}; last goal {FloorUnstrand.LastGoal}, inside {inside}");
                    FjLong(map, name);
                    return;
                }
            }
            Wait(2, "fj_case");
        }

        // After the flee flight onto the slab with no stairs: hungry now, the colonist still near: no flight down yet.
        private static void FjCalm(Map map)
        {
            fjA.needs.food.CurLevelPercentage = 0.05f;
            Thing hay = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamedSilentFail("Hay") ?? ThingDefOf.Kibble);
            hay.stackCount = 30;
            GenSpawn.Spawn(hay, FjG(FjLane("strand"), 9, 2), map);
            Info($"calm: duck at {fjA.Position} level {Levels.LevelOf(fjA.Position, map)} made hungry ({fjA.needs.food.CurCategory}), {hay} at {hay.Position}; calm-down left {FloorUnstrand.CalmLeft(fjA)} ticks; colonist at {fjCol.Position}");
            FjMarkCounters();
            FjResetWatch(map);
            TickUntil(900, () => { FjWatch(map); return false; }, "fj_calm_check", 1);
        }

        private static void FjCalmCheck(Map map)
        {
            Result($"calm: hungry upstairs, the colonist still near ({FloorUnstrand.CalmRadius} cells): no flight down",
                fjUnGiven == 0 && fjUnCalming > 0, $"{FjSeen(map)}; calming {fjUnCalming}");
            // the next check starts from the slab (a flee flight down while calming is the flee's own business)
            if (fjA.Spawned && Levels.LevelOf(fjA.Position, map) != 1)
            {
                Info($"food: duck put back on the slab from {fjA.Position}");
                Teleport(fjA, FjUp(FjLane("strand"), 5, 18, map));
            }
            Heal(fjCol);
            Teleport(fjCol, FjG(0, 5, 3));
            Draft(fjCol, true);
            Info($"food: colonist moved away to {fjCol.Position} ({Range3D.Distance(fjA.Position, fjCol.Position, map):0} cells)");
            FjMarkCounters();
            FjResetWatch(map);
            TickUntil(2500, () => { FjWatch(map); return fjLandings >= 1 && fjIngestTick >= 0; }, "fj_food_check", 1);
        }

        private static void FjFoodCheck(Map map)
        {
            int gap = fjTakeoffTicks.Count > 0 && fjFleeTakeoff >= 0 ? fjTakeoffTicks[0] - fjFleeTakeoff : -1;
            Result($"food: the colonist away, the hungry duck flies down to food after the {FloorUnstrand.CalmTicks}-tick calm-down and eats",
                fjUnGiven == 1 && FloorUnstrand.LastGoal == "food" && fjTakeoffs == 1 && fjLandings == 1 && Levels.LevelOf(fjLandCell, map) == 0 && fjIngestTick >= 0 && gap >= FloorUnstrand.CalmTicks,
                $"{FjSeen(map)}; last goal {FloorUnstrand.LastGoal}; take-off {gap} ticks after the flee flight; ingest {(fjIngestTick < 0 ? "none" : "after " + (fjIngestTick - fjStartTick) + " ticks")}");
            FjLong(map, "food");
        }

        // A long run after a flight out of a stranded spot: no more flights (no ping-pong).
        private static void FjLong(Map map, string name)
        {
            fjLongName = name;
            fjLongTakeoffs = fjTakeoffs;
            fjLongGiven = fjUnGiven;
            TickUntil(6000, () => { FjWatch(map); return false; }, "fj_long_check", 1);
        }

        private static void FjLongCheck(Map map)
        {
            Result($"{fjLongName}: no ping-pong: no more flights in the next 6000 ticks",
                fjTakeoffs == fjLongTakeoffs && fjUnGiven == fjLongGiven && fjA.Spawned && Levels.LevelOf(fjA.Position, map) == 0,
                $"{FjSeen(map)}; take-offs {fjTakeoffs - fjLongTakeoffs} more, unstrand flights {fjUnGiven - fjLongGiven} more");
            Wait(2, "fj_case");
        }

        // Right after the flight up: the colonist back on the ground: a flight down, once the cooldown ran out.
        private static void FjBack(Map map)
        {
            int left = FloorFlight.CooldownLeft(fjA);
            Heal(fjCol);
            Teleport(fjCol, FjG(FjLane("up"), 5, 5));
            Draft(fjCol, true);
            Info($"back: colonist put on the ground at {fjCol.Position}; duck at {fjA.Position} level {Levels.LevelOf(fjA.Position, map)}, cooldown left {left} ticks");
            fjMeleeTick = -1;
            TickUntil(1500, () =>
            {
                FjWatch(map);
                if (fjA.Spawned && (Find.TickManager.TicksGame - fjStartTick) % 20 == 0 && fjA.CurJobDef != PZFlightDefOf.PZ_FlyAcross)
                {
                    fjA.jobs.CheckForJobOverride();
                }
                return fjTakeoffs >= 2 && fjLandings >= 2 && fjMeleeTick >= 0;
            }, "fj_back_check", 1);
        }

        private static void FjBackCheck(Map map)
        {
            int gap = fjTakeoffTicks.Count >= 2 ? fjTakeoffTicks[1] - fjTakeoffTicks[0] : -1;
            Result($"back: it flies down to the colonist only after the {FloorFlight.CooldownTicks}-tick cooldown, and attacks",
                fjTakeoffs == 2 && fjFlyJobs == 2 && gap >= FloorFlight.CooldownTicks && fjCooling > 0 && fjLandings == 2 && Levels.LevelOf(fjLandCell, map) == 0 && fjMeleeTick >= 0,
                $"{FjSeen(map)}; take-offs {gap} ticks apart");
            Wait(2, "fj_case");
        }

        // ------------------------------------------------------------------ watching

        private static void FjResetWatch(Map map)
        {
            fjLastJob = null;
            fjJobs = fjFlyJobs = fjFlee = fjTakeoffs = fjLandings = fjLadder = 0;
            fjMeleeTick = fjIngestTick = -1;
            fjInFlight = fjA.ParentHolder is PawnFlyer;
            fjLandCell = IntVec3.Invalid;
            fjTakeoffTicks.Clear();
            fjJobDefs.Clear();
            fjMaxLevel = Levels.LevelOf(fjA.PositionHeld, map);
            fjStartTick = Find.TickManager.TicksGame;
        }

        private static void FjMarkCounters()
        {
            fjBase = new[] { JumpAcross.Asked, JumpAcross.MemoHits, JumpAcross.NoLanding, JumpAcross.Given, FloorFlight.Flights, FloorFlight.FleeFlights, FloorFlight.FleeNone, JumpAcross.Cooling };
            fjAsked = fjMemo = fjNoLanding = fjGiven = fjFlights = fjFleeFlights = fjFleeNone = fjCooling = 0;
            fjUnBaseGiven = FloorUnstrand.Given;
            fjUnBaseCalming = FloorUnstrand.Calming;
            fjUnGiven = fjUnCalming = 0;
        }

        private static void FjWatch(Map map)
        {
            Pawn p = fjA;
            if (p == null)
            {
                return;
            }
            fjAsked = JumpAcross.Asked - fjBase[0];
            fjMemo = JumpAcross.MemoHits - fjBase[1];
            fjNoLanding = JumpAcross.NoLanding - fjBase[2];
            fjGiven = JumpAcross.Given - fjBase[3];
            fjFlights = FloorFlight.Flights - fjBase[4];
            fjFleeFlights = FloorFlight.FleeFlights - fjBase[5];
            fjFleeNone = FloorFlight.FleeNone - fjBase[6];
            fjCooling = JumpAcross.Cooling - fjBase[7];
            fjUnGiven = FloorUnstrand.Given - fjUnBaseGiven;
            fjUnCalming = FloorUnstrand.Calming - fjUnBaseCalming;
            bool flying = p.ParentHolder is PawnFlyer;
            if (flying && !fjInFlight)
            {
                fjTakeoffs++;
                fjTakeoffTicks.Add(Find.TickManager.TicksGame);
            }
            else if (!flying && fjInFlight && p.Spawned)
            {
                fjLandings++;
                fjLandCell = p.Position;
            }
            fjInFlight = flying;
            if (!p.Spawned)
            {
                return;
            }
            StepKind sk = Links.Kind(p);
            if (sk == StepKind.LadderUp || sk == StepKind.LadderDown)
            {
                fjLadder++;
            }
            fjMaxLevel = Math.Max(fjMaxLevel, Levels.LevelOf(p.Position, map));
            Job j = p.CurJob;
            if (j != null && j != fjLastJob)
            {
                fjLastJob = j;
                fjJobs++;
                string k = j.def.defName;
                fjJobDefs[k] = fjJobDefs.TryGetValue(k, out int n) ? n + 1 : 1;
                if (fjJobs <= 25)
                {
                    Out($"  fj job {j.def.defName} A {j.targetA} at {p.Position} L{Levels.LevelOf(p.Position, map)} giver {j.jobGiver?.GetType().Name ?? "none"} tick {Find.TickManager.TicksGame - fjStartTick}");
                }
                if (j.def == PZFlightDefOf.PZ_FlyAcross)
                {
                    fjFlyJobs++;
                }
                if (j.def == JobDefOf.Ingest && fjIngestTick < 0)
                {
                    fjIngestTick = Find.TickManager.TicksGame;
                }
                if (j.def == JobDefOf.Flee)
                {
                    fjFlee++;
                }
                if (j.def == JobDefOf.AttackMelee && j.targetA.Thing == fjCol && fjMeleeTick < 0)
                {
                    fjMeleeTick = Find.TickManager.TicksGame;
                }
            }
        }

        private static string FjSeen(Map map)
        {
            Pawn p = fjA;
            string at = p.Spawned ? $"{p.Position} level {Levels.LevelOf(p.Position, map)}" : "not spawned (" + p.ParentHolder + ")";
            return $"animal now at {at}; fly jobs {fjFlyJobs}, take-offs {fjTakeoffs}, landings {fjLandings} (last at {fjLandCell}, level {(fjLandCell.IsValid ? Levels.LevelOf(fjLandCell, map) : -9)}), attack on the colonist {(fjMeleeTick < 0 ? "none" : "after " + (fjMeleeTick - fjStartTick) + " ticks")}, highest level {fjMaxLevel}, ladder ticks {fjLadder}; jobs {fjJobs} ({string.Join(", ", fjJobDefs.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value))}); giver asked {fjAsked} (cooling {fjCooling}, memo {fjMemo}, no landing {fjNoLanding}, given {fjGiven}); flights {fjFlights}, flee flights {fjFleeFlights}, flee without landing {fjFleeNone}; unstrand flights {fjUnGiven} (calming {fjUnCalming}); colonist at {fjCol.Position} level {Levels.LevelOf(fjCol.Position, map)}";
        }

        private static void FjClearPawns(Map map)
        {
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (p != fjCol && p != fjA)
                {
                    p.Destroy();
                }
            }
        }

        private static void FjCleanup(Map map)
        {
            if (fjA != null && !fjA.Destroyed)
            {
                fjA.Destroy();
            }
            fjA = null;
            if (fjArea != null)
            {
                fjArea.Delete();
                fjArea = null;
            }
            ThingDef rat = DefDatabase<ThingDef>.GetNamedSilentFail("Rat");
            if (fjLists != null && fjLists.fly.Remove(rat))
            {
                FloorFlight.ResetLists();
            }
        }
    }
}
