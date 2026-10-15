using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // M1b scripted test (mode m1b): a small two-storey house on the home map, built and used by the colonists through
    // the game's own jobs, then a raid that has to come up the stairs, a save in the middle of a stair step, and 3D
    // screenshots. Setup (ground walls, roof, materials, an upstairs bed and stockpile) is placed directly; everything
    // under test runs as real jobs with real ticks.
    public static partial class TestRunner
    {
        private static CellRect house;            // walls included, on the ground
        private static IntVec3 stairCell;
        private static int climbsColonists;
        private static int climbsHostiles;
        private static int maxUpstairs;
        private static int sleptUpstairsTicks;
        private static int steelTotal;
        private static string bedOwnerId;
        private static string climberId;
        private static IntVec3 climberPos;
        private static Func<bool> tickWatch;
        private static int watchEvery = 60;

        internal static void CountClimb(Pawn p)
        {
            if (p.Faction == Faction.OfPlayer)
            {
                climbsColonists++;
            }
            else if (p.HostileTo(Faction.OfPlayer))
            {
                climbsHostiles++;
            }
        }

        private static void TickUntil(int maxTicks, Func<bool> cond, string next, int every = 60)
        {
            tickWatch = cond;
            watchEvery = every;
            StartTicks(maxTicks, next);
        }

        private static IEnumerable<IntVec3> Upstairs(Map map)
        {
            CellRect inner = house;
            foreach (IntVec3 g in inner)
            {
                yield return ProjectZ.CellOnFloor(g, 1, map);
            }
        }

        private static List<Pawn> Colonists(Map map) => map.mapPawns.FreeColonistsSpawned.ToList();

        private static int OnFloor(Map map, IEnumerable<Pawn> pawns, int k) => pawns.Count(p => p.Spawned && ProjectZ.FloorOf(p.Position, map) == k);

        private static bool StepM1b(Map map)
        {
            switch (stage)
            {
                case "b_setup":
                    SetupHouse(map);
                    return true;
                case "b_stairs_check":
                {
                    StairGrid g = StairGrid.For(map);
                    Building_Stairs st = map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).OfType<Building_Stairs>().FirstOrDefault();
                    tickWatch = null;
                    Result("stairs built by colonists", st != null && g != null && g.Count == 1, st == null ? "no stairs after " + ticksTotal + " ticks" : $"{st} at {st.Position}, top {st.TopCell} terrain {st.TopCell.GetTerrain(map).defName}");
                    if (st == null)
                    {
                        Finish();
                        return true;
                    }
                    Pawn c = Colonists(map).First();
                    IntVec3 top = st.TopCell;
                    Result("reachability over the stairs", c.CanReach(top, PathEndMode.OnCell, Danger.Deadly) && map.reachability.CanReach(top, c.Position, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors)),
                        $"{c} at {c.Position} -> landing {top}");
                    Room rb = st.Position.GetRoom(map);
                    Room rt = top.GetRoom(map);
                    Result("floors stay separate rooms", rb != null && rt != null && rb != rt && st.Position.GetDistrict(map) != top.GetDistrict(map), $"ground room {rb?.ID} (cells {rb?.CellCount}), landing room {rt?.ID} (cells {rt?.CellCount})");
                    // Upper floor: blueprints over the whole roofed house, and one over unroofed ground that must be refused.
                    int placed = 0, refused = 0;
                    List<string> why = new List<string>();
                    foreach (IntVec3 u in Upstairs(map))
                    {
                        if (u.GetTerrain(map) == PZDefOf.PZ_UpperFloor)
                        {
                            continue;
                        }
                        AcceptanceReport r = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_UpperFloor, u, Rot4.North, map);
                        if (r.Accepted)
                        {
                            GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_UpperFloor, u, map, Rot4.North, Faction.OfPlayer, null);
                            placed++;
                        }
                        else
                        {
                            refused++;
                            why.Add(r.Reason);
                        }
                    }
                    IntVec3 outside = ProjectZ.CellOnFloor(new IntVec3(house.maxX + 3, 0, house.CenterCell.z), 1, map);
                    AcceptanceReport bad = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_UpperFloor, outside, Rot4.North, map);
                    Result("upper floor blueprints over the roof", placed == house.Area - 1 && refused == 0, $"{placed} placed, {refused} refused {string.Join(",", why.Distinct())}");
                    Result("upper floor refused without a roof below", !bad.Accepted, $"{outside}: {bad.Reason}");
                    IntVec3 groundOnly = new IntVec3(house.maxX + 3, 0, house.CenterCell.z);
                    AcceptanceReport bad2 = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_UpperFloor, groundOnly, Rot4.North, map);
                    Result("upper floor refused on the ground", !bad2.Accepted, $"{groundOnly}: {bad2.Reason}");
                    AcceptanceReport bad3 = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, groundOnly, Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
                    Result("stairs refused without a roof", !bad3.Accepted, $"{groundOnly}: {bad3.Reason}");
                    int target = house.Area;
                    TickUntil(90000, () => Upstairs(map).Count(u => u.GetTerrain(map) == PZDefOf.PZ_UpperFloor) >= target, "b_floor_check");
                    return true;
                }
                case "b_floor_check":
                {
                    tickWatch = null;
                    int built = Upstairs(map).Count(u => u.GetTerrain(map) == PZDefOf.PZ_UpperFloor);
                    string missing = string.Join("; ", Upstairs(map).Where(u => u.GetTerrain(map) != PZDefOf.PZ_UpperFloor).Select(u => u + " [" + string.Join(",", u.GetThingList(map).Select(t => t.def.defName + (t is Frame f ? " " + f.WorkLeft.ToString("0") + " left, mats " + f.resourceContainer.TotalStackCount : ""))) + "] touchable " + Colonists(map).First().CanReach(u, PathEndMode.Touch, Danger.Deadly)));
                    Result("upper floor built by colonists (upstairs work)", built == house.Area, $"{built} of {house.Area} cells after {ticksTotal - ticksLeft} ticks; colonist stair steps so far {climbsColonists}; not built: {missing}");
                    Furnish(map);
                    return true;
                }
                case "b_haul_check":
                {
                    tickWatch = null;
                    int up = SteelUpstairs(map);
                    Result("colonists haul downstairs steel to the upstairs stockpile", up >= steelTotal, $"{up} of {steelTotal} steel upstairs after {ticksTotal - ticksLeft} ticks; most colonists upstairs at once {maxUpstairs}; colonist stair steps {climbsColonists}");
                    stage = "b_shots";
                    return true;
                }
                case "b_sleep_check":
                {
                    tickWatch = null;
                    Result("colonist sleeps in the upstairs bed", sleptUpstairsTicks >= 600, $"{bedOwnerId}: {sleptUpstairsTicks} ticks asleep upstairs (hour now {GenLocalDate.HourOfDay(map)})");
                    stage = "b_shot2b";
                    return true;
                }
                case "b_shots":
                    PrepareShot(map, 1);
                    Wait(30, "b_shot1");
                    return true;
                case "b_shot1":
                    PickTest(map, 1);
                    Capture("m1b_floor2_view");
                    stage = "b_shot1b";
                    return true;
                case "b_shot1b":
                    PrepareShot(map, 0);
                    Wait(30, "b_shot2");
                    return true;
                case "b_shot2":
                    PickTest(map, 0);
                    Capture("m1b_floor1_view");
                    stage = "b_sleep";
                    return true;
                case "b_sleep":
                    // night: the owner sleeps in the upstairs bed
                    TickUntil(GenDate.TicksPerDay + 2000, () => sleptUpstairsTicks >= 600, "b_sleep_check", 30);
                    return true;
                case "b_shot2b":
                    FloorView.Set(1);
                    // Draft everyone and send them upstairs (ordered goto over the stairs). The first stair step seen is saved.
                    foreach (Pawn p in Colonists(map))
                    {
                        p.drafter.Drafted = true;
                        p.drafter.FireAtWill = false;
                    }
                    List<IntVec3> spots = Upstairs(map).Where(c => c.Standable(map) && c.GetFirstBuilding(map) == null).ToList();
                    int i = 0;
                    foreach (Pawn p in Colonists(map))
                    {
                        Job job = JobMaker.MakeJob(JobDefOf.Goto, spots[i++ % spots.Count]);
                        p.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder);
                    }
                    TickUntil(4000, () => FindClimber(map) != null, "b_climb_save", 1);
                    return true;
                case "b_climb_save":
                {
                    tickWatch = null;
                    Pawn cl = FindClimber(map);
                    if (cl == null)
                    {
                        Result("a pawn on a stair step to save", false, "none seen");
                        stage = "b_up_wait";
                        return true;
                    }
                    climberId = cl.ThingID;
                    climberPos = cl.Position;
                    Info($"saving with {cl} on a stair step: {cl.Position} -> {cl.pather.nextCell}, {cl.pather.MovePercentage:P0}");
                    PrepareShot(map, 1);
                    Find.CameraDriver.SetRootPosAndSize(new Vector3(cl.Position.x + 0.5f, 0f, ProjectZ.GroundCellOf(cl.Position, map).z + 0.5f), 7f);
                    Wait(3, "b_climb_shot");
                    return true;
                }
                case "b_climb_shot":
                    Capture("m1b_climb");
                    stage = "b_climb_save2";
                    return true;
                case "b_climb_save2":
                    savedTicks = Find.TickManager.TicksGame;
                    GameDataSaveLoader.SaveGame("pz_climb");
                    Result("saved pz_climb", File.Exists(GenFilePaths.FilePathForSavedGame("pz_climb")), "");
                    stage = "b_climb_loaded";
                    pendingLoad = "pz_climb";
                    return true;
                case "b_climb_loaded":
                {
                    FloorView.Set(1);
                    Pawn cl = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.ThingID == climberId);
                    // Reset on load (PostMapInit), then the joined path starts the step again from the start: a fresh step.
                    bool ok = cl != null && cl.Position == climberPos && cl.Position.Walkable(map) && PZStats.climbResets >= 1;
                    foreach (Pawn p in Colonists(map))
                    {
                        if (ProjectZ.FloorOf(p.Position, map) != 1 && (!p.Drafted || p.CurJobDef != JobDefOf.Goto))
                        {
                            Info($"after load {p} drafted {p.Drafted}, job {p.CurJobDef?.defName}: ordered upstairs again");
                            p.drafter.Drafted = true;
                            p.drafter.FireAtWill = false;
                            p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, Upstairs(map).Where(c => c.Standable(map) && c.GetFirstBuilding(map) == null).RandomElement()), JobTag.DraftedOrder);
                        }
                    }
                    Result("reload with a pawn mid-climb: reset to its cell", ok, cl == null ? "pawn not found" : $"{cl} at {cl.Position} (saved at {climberPos}), next cell {cl.pather.nextCell} at {cl.pather.MovePercentage:P0} (stair steps reset on load {PZStats.climbResets}), job {cl.CurJobDef?.defName}, ticks on load {ticksAfterLoad} vs saved {savedTicks}");
                    TickUntil(4000, () => OnFloor(map, Colonists(map), 1) == Colonists(map).Count, "b_up_check");
                    return true;
                }
                case "b_up_wait":
                    TickUntil(4000, () => OnFloor(map, Colonists(map), 1) == Colonists(map).Count, "b_up_check");
                    return true;
                case "b_up_check":
                {
                    tickWatch = null;
                    Pawn cl = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.ThingID == climberId);
                    Result("drafted colonists ordered upstairs arrive (incl. the reloaded climber)", OnFloor(map, Colonists(map), 1) == Colonists(map).Count, $"{OnFloor(map, Colonists(map), 1)} of {Colonists(map).Count} upstairs; climber {cl} now at {cl?.Position} floor {(cl == null ? -9 : ProjectZ.FloorOf(cl.Position, map))}");
                    // Raid: everyone is upstairs and holds fire; the raiders must come up the stairs.
                    IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
                    parms.faction = Find.FactionManager.AllFactions.Where(f => f.HostileTo(Faction.OfPlayer) && !f.def.hidden && f.def.humanlikeFaction && !f.temporary && f.def.pawnGroupMakers != null && f.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat) < 250f).RandomElementWithFallback();
                    parms.raidStrategy = RaidStrategyDefOf.ImmediateAttack;
                    parms.raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn;
                    parms.points = 250f;
                    parms.forced = true;
                    bool fired = IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);
                    Info($"raid ({parms.faction?.Name}, {parms.faction?.def.defName}) fired {fired}: {map.mapPawns.AllPawnsSpawned.Count(p => p.HostileTo(Faction.OfPlayer))} hostiles");
                    TickUntil(30000, () =>
                    {
                        if (Find.TickManager.TicksGame % 1500 < 30)
                        {
                            Out("  hostiles: " + string.Join("; ", map.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer) && p.RaceProps.Humanlike).Select(p => $"{p} at {p.Position} {(p.Downed ? "downed" : p.CurJobDef?.defName)} -> {p.CurJob?.targetA} lord {p.GetLord()?.CurLordToil}")) + " | colonists: " + string.Join("; ", Colonists(map).Select(c => $"{c} {c.Position} drafted {c.Drafted} fire {c.drafter?.FireAtWill} hp {c.health.summaryHealth.SummaryHealthPercent:P0}")));
                        }
                        return map.mapPawns.AllPawnsSpawned.Any(p => p.HostileTo(Faction.OfPlayer) && !p.Downed && ProjectZ.FloorOf(p.Position, map) == 1);
                    }, "b_raid_check", 30);
                    return true;
                }
                case "b_raid_check":
                {
                    tickWatch = null;
                    List<Pawn> hostiles = map.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer)).ToList();
                    Pawn up = hostiles.FirstOrDefault(p => !p.Downed && ProjectZ.FloorOf(p.Position, map) == 1);
                    Result("an enemy raider comes up the stairs", up != null, up == null ? $"no hostile upstairs after {ticksTotal - ticksLeft} ticks; hostiles {hostiles.Count}: {string.Join("; ", hostiles.Take(6).Select(h => h + " at " + h.Position + " " + h.CurJobDef?.defName))}" : $"{up} at {up.Position}, job {up.CurJobDef?.defName}; hostile stair steps {climbsHostiles}, path joins {PZStats.splices}, FindPathNow joins {PZStats.findNow}");
                    PrepareShot(map, 1);
                    if (up != null)
                    {
                        Find.CameraDriver.SetRootPosAndSize(new Vector3(house.CenterCell.x, 0f, house.CenterCell.z), 9f);
                    }
                    Wait(20, "b_raid_shot");
                    return true;
                }
                case "b_raid_shot":
                    Capture("m1b_raid_upstairs");
                    StartTicks(600, "b_end");
                    return true;
                case "b_end":
                    WatchCheck(map, "m1b end");
                    Info($"stats: colonist stair steps {climbsColonists}, hostile stair steps {climbsHostiles}, path joins {PZStats.splices} (none found {PZStats.spliceFails}), FindPathNow joins {PZStats.findNow}");
                    Finish();
                    return true;
            }
            return false;
        }

        // Before every tick-watch check: who sleeps upstairs, how many colonists are up.
        private static void TickHook(Map map)
        {
            if (mode != "m1b" || house.Area == 0)
            {
                return;
            }
            List<Pawn> cols = map.mapPawns.FreeColonistsSpawned;
            int up = 0;
            // Keep the quick-test colonists fed and calm, so the test sees their work, not their breakdowns.
            for (int i = 0; i < cols.Count; i++)
            {
                Pawn p = cols[i];
                if (p.needs?.food != null) p.needs.food.CurLevel = p.needs.food.MaxLevel;
                if (p.needs?.joy != null) p.needs.joy.CurLevel = 1f;
                if (p.InMentalState) p.MentalState.RecoverFromState();
            }
            for (int i = 0; i < cols.Count; i++)
            {
                Pawn p = cols[i];
                if (ProjectZ.FloorOf(p.Position, map) == 1)
                {
                    up++;
                    if (p.ThingID == bedOwnerId && p.CurrentBed() != null && p.jobs?.curDriver != null && p.jobs.curDriver.asleep)
                    {
                        sleptUpstairsTicks += watchEvery;
                    }
                }
            }
            maxUpstairs = Mathf.Max(maxUpstairs, up);
        }

        private static Pawn FindClimber(Map map)
        {
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (Climb.Direction(p) != 0 && p.pather.MovePercentage > 0.3f && p.pather.MovePercentage < 0.8f)
                {
                    return p;
                }
            }
            return null;
        }

        private static int SteelUpstairs(Map map)
        {
            int n = 0;
            foreach (IntVec3 u in Upstairs(map))
            {
                foreach (Thing t in u.GetThingList(map))
                {
                    if (t.def == ThingDefOf.Steel)
                    {
                        n += t.stackCount;
                    }
                }
            }
            return n;
        }

        private static void SetupHouse(Map map)
        {
            List<Pawn> cols = Colonists(map);
            IntVec3 near = cols.Count > 0 ? new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z)) : map.Center;
            // A 7x7 spot (walls included) of plain standable ground with a 3-cell margin, nearest the colonists.
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 60f, true))
            {
                CellRect r = new CellRect(c.x - 3, c.z - 3, 7, 7);
                CellRect m = r.ExpandedBy(3);
                if (!m.InBounds(map) || m.maxZ >= ProjectZ.GroundZ(map) - 12)
                {
                    continue;
                }
                bool ok = true;
                foreach (IntVec3 x in m)
                {
                    if (x.GetTerrain(map).IsWater || map.roofGrid.Roofed(x) || x.Fogged(map) || x.GetFirstBuilding(map) != null || x.GetFirstPawn(map) != null || x.GetTerrain(map).passability != Traversability.Standable)
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                {
                    found = r;
                    break;
                }
            }
            if (found.Area == 0)
            {
                Result("house spot", false, "no 13x13 open ground near the colonists");
                Finish();
                return;
            }
            house = found;
            foreach (IntVec3 c in house.ExpandedBy(3))
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t is Plant || t.def.category == ThingCategory.Item || t is Filth)
                    {
                        t.Destroy();
                    }
                }
            }
            DebugSettings.enableRandomMentalStates = false;
            ThingDef wallStuff = ThingDefOf.WoodLog;
            IntVec3 door = new IntVec3(house.CenterCell.x, 0, house.minZ);
            foreach (IntVec3 c in house.EdgeCells)
            {
                Thing b = ThingMaker.MakeThing(c == door ? ThingDefOf.Door : ThingDefOf.Wall, wallStuff);
                b.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(b, c, map);
            }
            foreach (IntVec3 c in house)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            // Materials outside the door, colonists on construction and hauling.
            IntVec3 store = new IntVec3(house.CenterCell.x, 0, house.minZ - 3);
            foreach (ThingDef d in new[] { ThingDefOf.WoodLog, ThingDefOf.WoodLog, ThingDefOf.WoodLog, ThingDefOf.Steel })
            {
                Thing t = ThingMaker.MakeThing(d);
                t.stackCount = d.stackLimit;
                GenPlace.TryPlaceThing(t, store, map, ThingPlaceMode.Near);
            }
            foreach (Pawn p in cols)
            {
                p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
                foreach (WorkTypeDef w in new[] { WorkTypeDefOf.Construction, WorkTypeDefOf.Hauling })
                {
                    if (!p.WorkTypeIsDisabled(w))
                    {
                        p.workSettings.SetPriority(w, 1);
                    }
                }
            }
            stairCell = new IntVec3(house.maxX - 1, 0, house.maxZ - 1);
            AcceptanceReport r0 = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, stairCell, Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
            Result("stairs blueprint placeable inside the roofed room", r0.Accepted, $"{stairCell}: {r0.Reason}");
            if (r0.Accepted)
            {
                GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, stairCell, map, Rot4.North, Faction.OfPlayer, ThingDefOf.Steel);
            }
            Pawn first = cols.First();
            Result("upstairs unreachable before the stairs", !first.CanReach(ProjectZ.CellOnFloor(house.CenterCell, 1, map), PathEndMode.Touch, Danger.Deadly), "");
            Info($"house {house} (door {door}), stairs at {stairCell}, colonists {cols.Count}");
            AnnexWatch.Reset();
            TickUntil(40000, () => StairGrid.For(map) != null, "b_stairs_check");
        }

        private static void Furnish(Map map)
        {
            List<IntVec3> up = Upstairs(map).ToList();
            IntVec3 landing = ProjectZ.CellOnFloor(stairCell, 1, map);
            // Bed in the upstairs south-west corner, owned by the first colonist.
            IntVec3 bedCell = up.Where(c => c != landing && c.GetTerrain(map) == PZDefOf.PZ_UpperFloor && (c + IntVec3.North).GetTerrain(map) == PZDefOf.PZ_UpperFloor && c + IntVec3.North != landing).OrderBy(c => c.x + c.z).First();
            Building_Bed bed = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
            bed.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(bed, bedCell, map, Rot4.North);
            Pawn owner = Colonists(map).First();
            bed.CompAssignableToPawn.TryAssignPawn(owner);
            bedOwnerId = owner.ThingID;
            // Critical steel stockpile on the rest of the upper floor.
            Zone_Stockpile zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (IntVec3 c in up)
            {
                if (c != landing && c.GetFirstBuilding(map) == null && c.Standable(map))
                {
                    zone.AddCell(c);
                }
            }
            zone.settings.filter.SetDisallowAll();
            zone.settings.filter.SetAllow(ThingDefOf.Steel, true);
            zone.settings.Priority = StoragePriority.Critical;
            // Steel on the ground outside.
            steelTotal = 0;
            IntVec3 store = new IntVec3(house.maxX + 3, 0, house.minZ);
            for (int i = 0; i < 3; i++)
            {
                Thing s = ThingMaker.MakeThing(ThingDefOf.Steel);
                s.stackCount = 40;
                steelTotal += 40;
                GenPlace.TryPlaceThing(s, store, map, ThingPlaceMode.Near);
                s.SetForbidden(false, false);
            }
            Thing steel = map.listerThings.ThingsOfDef(ThingDefOf.Steel).FirstOrDefault(t => ProjectZ.FloorOf(t.Position, map) == 0);
            Result("upstairs stockpile reachable for haulers", steel != null && owner.CanReach(zone.Cells.First(), PathEndMode.OnCell, Danger.Deadly), $"zone {zone.Cells.Count} cells, bed {bed} at {bedCell} for {owner}");
            // Count only steel placed by this test (the map may have its own).
            steelTotal += 0;
            Info($"furnished: bed for {owner}, stockpile {zone.Cells.Count} cells, steel {steelTotal}");
            maxUpstairs = 0;
            TickUntil(30000, () => SteelUpstairs(map) >= steelTotal, "b_haul_check");
        }

        private static void PrepareShot(Map map, int floor)
        {
            FloorView.Set(floor);
            CameraDriver_Update_Patch.AllowAnnexView = false;
            Find.UIRoot.screenshotMode.Active = false;
            Find.CameraDriver.SetRootPosAndSize(new Vector3(house.CenterCell.x + 0.5f, 0f, house.CenterCell.z + 0.5f), 11f);
        }

        // The pointer over the middle of the house picks the floor the player works on.
        private static void PickTest(Map map, int floor)
        {
            if (!IsoRimBridge.Present)
            {
                Info("pick test skipped: IsoRim not loaded");
                return;
            }
            IntVec3 g = house.CenterCell;
            float h = floor * IsoRimBridge.StoreyHeight;
            Vector2 px = PickScreen(new Vector3(g.x + 0.5f, h, g.z + 0.5f));
            IntVec3 got = Verse.UI.UIToMapPosition(px / Prefs.UIScale).ToIntVec3();
            IntVec3 want = ProjectZ.CellOnFloor(g, floor, map);
            Result($"pointer picks floor {floor + 1}'s cell", got == want, $"screen {px} -> {got}, expected {want}");
        }

        private static Vector2 PickScreen(Vector3 p) => IsoRim.View3D.WorldToScreen(p);
    }

    // Counters for the test (cheap, always on).
    public static class PZStats
    {
        public static int splices;
        public static int spliceFails;
        public static int findNow;
        public static int climbResets;
    }
}
