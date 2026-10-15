using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using ProjectZ.Combat;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // M1c part 1 scripted test (mode m1c): roofs under upper floors (D6), heat between floors (D8) and shooting between
    // floors (decision A, D7). Three houses side by side on the home map:
    //   A: sealed ground room, upper floor over it with walls and a roof (a sealed upstairs room) and a powered heater
    //      upstairs (wood-fired generator, conduits): the two rooms' temperatures over time;
    //   C: the same ground room with only a roof (the reference for A's ground room); the idle colonists wait in it;
    //   B: sealed ground room with an open upper floor (a terrace): a drafted colonist upstairs, raiders on the ground
    //      north of it (behind sandbags) and one raider inside the room below.
    // Setup is placed directly; the checks are the game's own functions (Verb.CanHitTarget, ShotReport, the target
    // finder) and real fighting with real ticks (the raiders' lord, the colonist firing at will).
    public static partial class TestRunner
    {
        private static CellRect houseA, houseB, houseC;
        private static Building heater;
        private static Thing genThing;
        private static Pawn shooter;
        private static readonly List<Pawn> raiders = new List<Pawn>();
        private static Pawn raiderInside;
        private static IntVec3 edgeCell, centreCell, parapetCell, raiderCell;
        private static readonly List<string> tempLog = new List<string>();
        private static bool shotDown, shotUp;
        private static int startInjuriesShooter, startInjuriesRaiders;

        private static readonly AccessTools.FieldRef<Projectile, Vector3> projOrigin = AccessTools.FieldRefAccess<Projectile, Vector3>("origin");
        private static readonly AccessTools.FieldRef<Projectile, Vector3> projDest = AccessTools.FieldRefAccess<Projectile, Vector3>("destination");

        private static IntVec3 Up(IntVec3 g, Map map) => ProjectZ.CellOnFloor(g, 1, map);

        private static bool StepM1c(Map map)
        {
            switch (stage)
            {
                case "c_setup":
                    SetupM1c(map);
                    return true;
                case "c_roof":
                    CheckRoofs(map);
                    return true;
                case "c_roof_work_check":
                    CheckRoofWork(map);
                    return true;
                case "c_heat":
                    HeatLog(map, true);
                    TickUntil(15000, () =>
                    {
                        if ((ticksTotal - ticksLeft) % 1250 == 0)
                        {
                            HeatLog(map, false);
                        }
                        return false;
                    }, "c_heat_check", 50);
                    return true;
                case "c_heat_check":
                    CheckHeat(map);
                    return true;
                case "c_shot_heat":
                    Capture("m1c1_heat_houses");
                    stage = "c_sight";
                    return true;
                case "c_sight":
                    CheckSight(map);
                    return true;
                case "c_fight":
                    StartFight(map);
                    return true;
                case "c_shot_down":
                    tickWatch = null;
                    if (shotDown)
                    {
                        Capture("m1c1_shot_down");
                    }
                    TickUntil(3000, () => { FightLog(map); return MidFlight(map, false) != null; }, "c_shot_up", 1);
                    return true;
                case "c_shot_up":
                    tickWatch = null;
                    if (shotUp)
                    {
                        Capture("m1c1_shot_up");
                    }
                    StartTicks(900, "c_fight_check");
                    return true;
                case "c_fight_check":
                    CheckFight(map);
                    return true;
                case "c_save":
                    savedTicks = Find.TickManager.TicksGame;
                    GameDataSaveLoader.SaveGame("pz_m1c");
                    Result("saved pz_m1c", File.Exists(GenFilePaths.FilePathForSavedGame("pz_m1c")), "");
                    stage = "c_loaded";
                    pendingLoad = "pz_m1c";
                    return true;
                case "c_loaded":
                    AfterLoad(map);
                    return true;
                case "c_end":
                    WatchCheck(map, "m1c end");
                    Info($"combat stats: cross-floor shots {PZCombatStats.crossFloorShots}, intercepts {PZCombatStats.intercepts}, passed over {PZCombatStats.passedOver}, stopped by a slab {PZCombatStats.slabStops}");
                    Finish();
                    return true;
            }
            return false;
        }

        private static Thing Place(ThingDef def, ThingDef stuff, IntVec3 c, Map map, Rot4 rot = default(Rot4))
        {
            Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? stuff ?? GenStuff.DefaultStuffFor(def) : null);
            if (t.def.CanHaveFaction)
            {
                t.SetFaction(Faction.OfPlayer);
            }
            return GenSpawn.Spawn(t, c, map, rot);
        }

        private static void Teleport(Pawn p, IntVec3 c)
        {
            p.jobs?.StopAll();
            p.pather?.StopDead();
            p.Position = c;
            p.Notify_Teleported();
        }

        private static void SetupM1c(Map map)
        {
            List<Pawn> cols = Colonists(map);
            IntVec3 near = cols.Count > 0 ? new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z)) : map.Center;
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 70f, true))
            {
                CellRect r = new CellRect(c.x - 13, c.z - 9, 27, 19);
                CellRect m = r.ExpandedBy(2);
                if (!m.InBounds(map) || m.maxZ >= ProjectZ.GroundZ(map) - 12)
                {
                    continue;
                }
                bool ok = true;
                foreach (IntVec3 x in m)
                {
                    if (x.GetTerrain(map).IsWater || map.roofGrid.Roofed(x) || x.Fogged(map) || x.GetFirstBuilding(map) != null || x.GetTerrain(map).passability != Traversability.Standable)
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
                Result("test spot", false, "no 31x23 open ground near the colonists");
                Finish();
                return;
            }
            foreach (IntVec3 c in found.ExpandedBy(2))
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t is Plant || t.def.category == ThingCategory.Item || t is Filth || t is Pawn p && !p.IsColonist)
                    {
                        t.Destroy();
                    }
                }
            }
            DebugSettings.enableRandomMentalStates = false;
            int ox = found.minX, oz = found.minZ;
            houseA = new CellRect(ox, oz, 7, 7);
            houseC = new CellRect(ox + 10, oz, 7, 7);
            houseB = new CellRect(ox + 20, oz, 7, 7);
            foreach (CellRect h in new[] { houseA, houseB, houseC })
            {
                foreach (IntVec3 c in h.EdgeCells)
                {
                    Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
                }
                foreach (IntVec3 c in h)
                {
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                }
            }
            // Upper floors over A and B (directly, as built floors: the terrain change puts the floor's roof under them).
            foreach (CellRect h in new[] { houseA, houseB })
            {
                foreach (IntVec3 g in h)
                {
                    map.terrainGrid.SetTerrain(Up(g, map), PZDefOf.PZ_UpperFloor);
                }
            }
            // A's upstairs room: walls round the edge, a roof, a heater on a wood-fired generator.
            foreach (IntVec3 g in houseA.EdgeCells)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, Up(g, map), map);
            }
            foreach (IntVec3 g in houseA.ContractedBy(1))
            {
                map.roofGrid.SetRoof(Up(g, map), RoofDefOf.RoofConstructed);
            }
            IntVec3 h0 = Up(new IntVec3(houseA.minX + 1, 0, houseA.minZ + 1), map);
            foreach (IntVec3 c in new[] { h0, h0 + IntVec3.East, h0 + IntVec3.East * 2, h0 + new IntVec3(2, 0, 1), h0 + new IntVec3(2, 0, 2) })
            {
                Place(DefDatabase<ThingDef>.GetNamed("PowerConduit"), null, c, map);
            }
            Thing gen = genThing = Place(DefDatabase<ThingDef>.GetNamed("WoodFiredGenerator"), null, h0 + new IntVec3(2, 0, 2), map);
            gen.TryGetComp<CompRefuelable>()?.Refuel(500f);
            heater = (Building)Place(DefDatabase<ThingDef>.GetNamed("Heater"), null, h0, map);
            CompTempControl tc = heater.TryGetComp<CompTempControl>();
            if (tc != null)
            {
                tc.targetTemperature = 45f;
            }
            // B: the terrace.
            int cx = houseB.CenterCell.x;
            edgeCell = Up(new IntVec3(cx, 0, houseB.maxZ), map);
            centreCell = Up(houseB.CenterCell, map);
            parapetCell = Up(new IntVec3(cx - 2, 0, houseB.maxZ - 1), map);
            Place(ThingDefOf.Sandbags, null, Up(new IntVec3(cx - 2, 0, houseB.maxZ), map), map);
            raiderCell = new IntVec3(cx, 0, houseB.maxZ + 10);
            Place(ThingDefOf.Sandbags, null, raiderCell + IntVec3.South, map);
            // Roof-work cells outside the houses (reachable): one under a floor cell north of A, one plain north of C.
            roofCellA = new IntVec3(houseA.minX + 2, 0, houseA.maxZ + 1);
            roofCellC = new IntVec3(houseC.minX + 2, 0, houseC.maxZ + 1);
            map.roofGrid.SetRoof(roofCellA, RoofDefOf.RoofConstructed);
            map.roofGrid.SetRoof(roofCellC, RoofDefOf.RoofConstructed);
            map.terrainGrid.SetTerrain(Up(roofCellA, map), PZDefOf.PZ_UpperFloor);
            // Colonists: the first one is the shooter (later drafted, with a rifle).
            shooter = cols.FirstOrDefault(p => !p.WorkTagIsDisabled(WorkTags.Violent) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hunting)) ?? cols.First();
            Thing rifle = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle"));
            shooter.equipment.DestroyAllEquipment();
            shooter.equipment.AddEquipment((ThingWithComps)rifle);
            Info($"houses A {houseA}, C {houseC}, B {houseB}; terrace edge {edgeCell}, centre {centreCell}, parapet {parapetCell}, raiders at {raiderCell}; colonists {cols.Count}; outdoor {map.mapTemperature.OutdoorTemp:0.0} C");
            Wait(10, "c_roof");
        }

        private static void CheckRoofs(Map map)
        {
            IntVec3 gA = houseA.CenterCell;
            Room room = gA.GetRoom(map);
            RoofDef roof = map.roofGrid.RoofAt(gA);
            int floorRoofs = houseA.Cells.Count(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_FloorRoof);
            Result("roof under an upper floor is the floor's roof", roof == PZDefOf.PZ_FloorRoof && floorRoofs == houseA.Area, $"{gA}: {roof?.defName}; {floorRoofs} of {houseA.Area} cells");
            Result("room under an upper floor is indoors", room != null && !room.PsychologicallyOutdoors && !room.UsesOutdoorTemperature && room.OpenRoofCount == 0,
                $"room {room?.ID} cells {room?.CellCount}, outdoors {room?.PsychologicallyOutdoors}, outdoor temp {room?.UsesOutdoorTemperature}, open roof {room?.OpenRoofCount}");
            Room up = Up(gA, map).GetRoom(map);
            Result("upstairs room separate and indoors", up != null && up != room && !up.UsesOutdoorTemperature, $"upstairs room {up?.ID} cells {up?.CellCount}");
            Room terrace = centreCell.GetRoom(map);
            Result("open upper floor (terrace) is outdoors", terrace != null && terrace.UsesOutdoorTemperature, $"terrace room {terrace?.ID} cells {terrace?.CellCount} open roof {terrace?.OpenRoofCount}");
            // Anything that writes "no roof" there keeps the floor's roof; a roof collapse there drops nothing.
            IntVec3 probe = new IntVec3(houseA.minX + 2, 0, houseA.minZ + 3);
            map.roofGrid.SetRoof(probe, null);
            bool kept = map.roofGrid.RoofAt(probe) == PZDefOf.PZ_FloorRoof;
            RoofCollapserImmediate.DropRoofInCells(probe, map);
            RoofCollapserImmediate.DropRoofInCells(new List<IntVec3> { probe }, map);
            Result("floor's roof can't be cleared or dropped", kept && map.roofGrid.RoofAt(probe) == PZDefOf.PZ_FloorRoof, $"{probe}: {map.roofGrid.RoofAt(probe)?.defName}");
            // Removing a floor cell turns the roof under it back into a constructed roof; building it again, the floor's.
            IntVec3 corner = houseB.CenterCell + IntVec3.West;
            map.terrainGrid.SetTerrain(Up(corner, map), PZDefOf.PZ_OpenAir);
            RoofDef after = map.roofGrid.RoofAt(corner);
            map.terrainGrid.SetTerrain(Up(corner, map), PZDefOf.PZ_UpperFloor);
            Result("floor removed: plain roof again; rebuilt: floor's roof", after == RoofDefOf.RoofConstructed && map.roofGrid.RoofAt(corner) == PZDefOf.PZ_FloorRoof, $"{corner}: {after?.defName} -> {map.roofGrid.RoofAt(corner)?.defName}");
            // Real flow: the no-roof area over both a floor's roof and a plain roof; colonists on roofing work.
            IntVec3 cellA = roofCellA;
            IntVec3 cellC = roofCellC;
            Result("roof-work cells set up", map.roofGrid.RoofAt(cellA) == PZDefOf.PZ_FloorRoof && map.roofGrid.RoofAt(cellC) == RoofDefOf.RoofConstructed, $"{cellA}: {map.roofGrid.RoofAt(cellA)?.defName}, {cellC}: {map.roofGrid.RoofAt(cellC)?.defName}");
            map.areaManager.NoRoof[cellA] = true;
            map.areaManager.NoRoof[cellC] = true;
            foreach (Pawn p in Colonists(map))
            {
                p.drafter.Drafted = false;
                p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
                if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Construction))
                {
                    p.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                }
            }
            TickUntil(6000, () => !map.roofGrid.Roofed(cellC), "c_roof_work_check");
            pendingCells = new[] { cellA, cellC };
        }

        private static IntVec3[] pendingCells;
        private static IntVec3 roofCellA, roofCellC;

        private static void CheckRoofWork(Map map)
        {
            tickWatch = null;
            IntVec3 cellA = pendingCells[0], cellC = pendingCells[1];
            Result("colonists remove a plain roof (control)", !map.roofGrid.Roofed(cellC), $"{cellC}: {map.roofGrid.RoofAt(cellC)?.defName ?? "none"} after {ticksTotal - ticksLeft} ticks");
            Result("but not the roof under an upper floor", map.roofGrid.RoofAt(cellA) == PZDefOf.PZ_FloorRoof, $"{cellA}: {map.roofGrid.RoofAt(cellA)?.defName ?? "none"}");
            map.areaManager.NoRoof[cellA] = false;
            map.areaManager.NoRoof[cellC] = false;
            map.roofGrid.SetRoof(cellC, RoofDefOf.RoofConstructed);
            // Everyone waits in C (sealed, no view out) during the heat test; the shooter comes out for the shooting.
            List<IntVec3> inC = houseC.ContractedBy(1).Cells.ToList();
            List<Pawn> cols = Colonists(map);
            for (int i = 0; i < cols.Count; i++)
            {
                cols[i].drafter.Drafted = true;
                cols[i].drafter.FireAtWill = false;
                Teleport(cols[i], inC[i % inC.Count]);
            }
            stage = "c_heat";
        }

        private static void HeatLog(Map map, bool first)
        {
            Room ga = houseA.CenterCell.GetRoom(map);
            Room ua = Up(houseA.CenterCell, map).GetRoom(map);
            Room gc = houseC.CenterCell.GetRoom(map);
            string line = $"tick {Find.TickManager.TicksGame} outdoor {map.mapTemperature.OutdoorTemp:0.0} | A upstairs (heater) {ua?.Temperature:0.0} | A ground (under it) {ga?.Temperature:0.0} | C ground (reference) {gc?.Temperature:0.0} | heater on {heater?.TryGetComp<CompPowerTrader>()?.PowerOn} net {heater?.TryGetComp<CompPowerTrader>()?.PowerNet?.CurrentEnergyGainRate():0.00} gen {genThing?.TryGetComp<CompPowerPlant>()?.PowerOutput:0} fuel {genThing?.TryGetComp<CompRefuelable>()?.Fuel:0}";
            if (first)
            {
                tempLog.Clear();
            }
            tempLog.Add(line);
            Out("  temp " + line);
        }

        private static void CheckHeat(Map map)
        {
            tickWatch = null;
            HeatLog(map, false);
            Room ga = houseA.CenterCell.GetRoom(map);
            Room ua = Up(houseA.CenterCell, map).GetRoom(map);
            Room gc = houseC.CenterCell.GetRoom(map);
            foreach (string l in tempLog)
            {
                Info("temperature: " + l);
            }
            bool heated = ua != null && ga != null && gc != null && ua.Temperature > gc.Temperature + 5f;
            bool passed = ga != null && gc != null && ga.Temperature > gc.Temperature + 1.5f;
            Result("heater warms the upstairs room", heated, $"upstairs {ua?.Temperature:0.0} vs reference ground room {gc?.Temperature:0.0}");
            Result("heat passes down through the floor", passed, $"ground room under it {ga?.Temperature:0.0} vs reference {gc?.Temperature:0.0} (same walls and size, plain roof)");
            Room terrace = centreCell.GetRoom(map);
            Info($"terrace (outdoors) {terrace?.Temperature:0.0}; B ground room {houseB.CenterCell.GetRoom(map)?.Temperature:0.0}");
            FloorView.Set(1);
            CameraDriver_Update_Patch.AllowAnnexView = false;
            Find.UIRoot.screenshotMode.Active = false;
            Find.CameraDriver.SetRootPosAndSize(new Vector3(houseC.CenterCell.x + 0.5f, 0f, houseC.CenterCell.z + 2.5f), 16f);
            Wait(30, "c_shot_heat");
        }

        private static string Report(Thing from, Thing to)
        {
            Verb v = from is Pawn p ? p.TryGetAttackVerb(to) : null;
            if (v == null)
            {
                return "no verb";
            }
            ShotReport r = ShotReport.HitReportFor(from, v, to);
            return $"hit {r.TotalEstimatedHitChance:P0} (aim {r.AimOnTargetChance:P0}, pass cover {r.PassCoverChance:P0}) | " + r.GetTextReadout().Replace("\n", " / ").Replace("   ", " ");
        }

        private static bool CanHit(Pawn from, Thing to)
        {
            Verb v = from.TryGetAttackVerb(to);
            return v != null && v.CanHitTarget(to);
        }

        private static Pawn MakeRaider(Map map, IntVec3 c)
        {
            Faction f = Find.FactionManager.AllFactions.Where(x => x.HostileTo(Faction.OfPlayer) && x.def.humanlikeFaction && !x.def.hidden && !x.temporary && x.def.pawnGroupMakers != null).RandomElement();
            Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Mercenary_Gunner"), f, forceGenerateNewPawn: true));
            p.equipment.DestroyAllEquipment();
            p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle")));
            GenSpawn.Spawn(p, c, map);
            return p;
        }

        private static void CheckSight(Map map)
        {
            raiders.Clear();
            raiders.Add(MakeRaider(map, raiderCell));
            raiderInside = MakeRaider(map, houseB.CenterCell + IntVec3.South);
            Pawn raider = raiders[0];
            // (the game undrafts idle pawns after a while: draft again)
            shooter.drafter.Drafted = true;
            shooter.drafter.FireAtWill = false;
            Teleport(shooter, edgeCell);
            // Same-floor reference: a colonist on the ground just north of B, as far from the raider (sandbags in between).
            Pawn refPawn = Colonists(map).Where(p => p != shooter).OrderBy(p => p.WorkTagIsDisabled(WorkTags.Violent) ? 1 : 0).FirstOrDefault();   // (one who can shoot: the hit report reads the shooting stat)
            IntVec3 refHome = refPawn?.Position ?? IntVec3.Invalid;
            if (refPawn != null)
            {
                Thing gun = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle"));
                refPawn.equipment.DestroyAllEquipment();
                refPawn.equipment.AddEquipment((ThingWithComps)gun);
                Teleport(refPawn, new IntVec3(edgeCell.x, 0, houseB.maxZ + 1));
            }
            bool edgeSees = CanHit(shooter, raider);
            Result("upstairs colonist at the slab edge can shoot the raider on the ground", edgeSees, $"{shooter} at {edgeCell} -> {raider} at {raider.Position} (ground distance {Heights.GroundDistance(edgeCell, raider.Position, map):0.0})");
            Info("hit chance, edge colonist (floor 2) -> raider (ground, behind sandbags): " + Report(shooter, raider));
            if (refPawn != null)
            {
                Info("hit chance, reference colonist on the ground (same distance, same skill? " + (refPawn.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? -1) + " vs " + (shooter.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? -1) + ") -> raider: " + Report(refPawn, raider));
                ShotReport rr = ShotReport.HitReportFor(refPawn, refPawn.TryGetAttackVerb(raider), raider);
                ShotReport ru = ShotReport.HitReportFor(shooter, shooter.TryGetAttackVerb(raider), raider);
                Result("sandbags cover from the ground but not from the floor above", rr.PassCoverChance < 0.99f && ru.PassCoverChance > 0.99f, $"pass cover: ground {rr.PassCoverChance:P0}, upstairs {ru.PassCoverChance:P0}");
            }
            bool upSeen = CanHit(raider, shooter);
            Result("raider on the ground can shoot the edge colonist", upSeen, $"{raider} -> {shooter}");
            Info("hit chance, raider (ground) -> edge colonist (floor 2): " + Report(raider, shooter));
            Pawn pick = AttackTargetFinder.BestShootTargetFromCurrentPosition(raider, TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable)?.Thing as Pawn;
            // The reference colonist is nearer and on the ground: move it away first for the pick test.
            if (refPawn != null)
            {
                Teleport(refPawn, refHome);
            }
            Pawn pick2 = AttackTargetFinder.BestShootTargetFromCurrentPosition(raider, TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable)?.Thing as Pawn;
            Result("raider's target finder picks the upstairs colonist", pick2 == shooter, $"with a ground colonist near: {pick}; alone: {pick2}");
            Pawn cpick = AttackTargetFinder.BestShootTargetFromCurrentPosition(shooter, TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable)?.Thing as Pawn;
            Result("colonist's target finder picks the raider below", cpick == raider, $"picked {cpick}");
            // Parapet: one cell back from the edge, behind sandbags on the edge cell.
            Teleport(shooter, parapetCell);
            bool parapetSees = CanHit(shooter, raider) && CanHit(raider, shooter);
            ShotReport rp = ShotReport.HitReportFor(raider, raider.TryGetAttackVerb(shooter), shooter);
            Result("behind a parapet at the edge: both see, the parapet covers", parapetSees && rp.PassCoverChance < 0.99f, $"pass cover for the shot up {rp.PassCoverChance:P0}");
            Info("hit chance, raider -> colonist behind the parapet: " + Report(raider, shooter));
            // Slab centre: sees nothing below, seen by nobody below.
            Teleport(shooter, centreCell);
            Result("same colonist at the slab centre can't see the raider", !CanHit(shooter, raider) && !CanHit(raider, shooter) && !raider.CanSee(shooter),
                $"centre {centreCell}: colonist->raider {CanHit(shooter, raider)}, raider->colonist {CanHit(raider, shooter)}");
            Pawn cpick2 = AttackTargetFinder.BestShootTargetFromCurrentPosition(shooter, TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable)?.Thing as Pawn;
            Result("colonist at the centre picks no target below", cpick2 == null || ProjectZ.FloorOf(cpick2.Position, map) == 1, $"picked {cpick2}");
            // The raider in the room below the terrace: can't target the colonist above, at the centre or at the edge.
            bool insideCentre = CanHit(raiderInside, shooter);
            Teleport(shooter, edgeCell);
            bool insideEdge = CanHit(raiderInside, shooter);
            Pawn ipick = AttackTargetFinder.BestShootTargetFromCurrentPosition(raiderInside, TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable)?.Thing as Pawn;
            Result("pawn in the room below can't target the colonist upstairs", !insideCentre && !insideEdge && ipick != shooter, $"{raiderInside} at {raiderInside.Position}: centre {insideCentre}, edge {insideEdge}, picked {ipick}");
            stage = "c_fight";
        }

        private static void StartFight(Map map)
        {
            Pawn raider = raiders[0];
            raiders.Add(MakeRaider(map, raiderCell + IntVec3.East * 2));
            LordMaker.MakeNewLord(raider.Faction, new LordJob_DefendPoint(raiderCell, 3f, 3f, false, false), map, raiders);
            LordMaker.MakeNewLord(raiderInside.Faction, new LordJob_AssaultColony(raiderInside.Faction), map, new[] { raiderInside });
            shooter.drafter.Drafted = true;
            shooter.drafter.FireAtWill = true;
            Teleport(shooter, edgeCell);
            startInjuriesShooter = shooter.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);
            startInjuriesRaiders = raiders.Sum(r => r.health.hediffSet.hediffs.Count(h => h is Hediff_Injury));
            PZCombatStats.crossFloorShots = 0;
            PZCombatStats.shots.Clear();
            FloorView.Set(1);
            Find.CameraDriver.SetRootPosAndSize(new Vector3(edgeCell.x + 0.5f, 0f, houseB.maxZ + 5.5f), 9f);
            TickUntil(3000, () => { FightLog(map); return MidFlight(map, true) != null; }, "c_shot_down", 1);
        }

        // A projectile between floors half-way: down (from the colonist) or up (from a raider).
        private static Projectile MidFlight(Map map, bool down)
        {
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile))
            {
                if (!(t is Projectile p) || Flights.Of(p) == null || p.Launcher == null)
                {
                    continue;
                }
                bool fromUp = ProjectZ.FloorOf(p.Launcher.Position, map) == 1;
                if (fromUp != down)
                {
                    continue;
                }
                Vector3 o = projOrigin(p), d = projDest(p);
                float total = (d - o).MagnitudeHorizontal();
                float f = total > 0.1f ? (p.ExactPosition - o).MagnitudeHorizontal() / total : 0f;
                if (f > 0.3f && f < 0.75f)
                {
                    if (down)
                    {
                        shotDown = true;
                    }
                    else
                    {
                        shotUp = true;
                    }
                    Info($"mid-flight {(down ? "down" : "up")}: {p} from {p.Launcher} at {p.Launcher.Position} (floor {ProjectZ.FloorOf(p.Launcher.Position, map) + 1}) to {p.usedTarget} at {p.usedTarget.Cell}, {f:P0} of the way, drawn at {p.DrawPos}, flight height {Flights.Of(p).hs:0.00} -> {Flights.Of(p).ht:0.00}");
                    return p;
                }
            }
            return null;
        }

        private static void FightLog(Map map)
        {
            if (Find.TickManager.TicksGame % 200 != 0)
            {
                return;
            }
            Out($"  fight: shooter {shooter} at {shooter.Position} job {shooter.CurJobDef?.defName} stance {shooter.stances?.curStance} drafted {shooter.Drafted} fire {shooter.drafter?.FireAtWill} verb {shooter.CurrentEffectiveVerb} | "
                + string.Join("; ", raiders.Select(r => $"{r} at {r.Position} {(r.Dead ? "dead" : r.Downed ? "downed" : r.CurJobDef?.defName)} -> {r.CurJob?.targetA} hp {r.health.summaryHealth.SummaryHealthPercent:P0}")));
        }

        private static void CheckFight(Map map)
        {
            tickWatch = null;
            int down = PZCombatStats.shots.Count(s => s.fromFloor == 1 && s.toFloor == 0);
            int up = PZCombatStats.shots.Count(s => s.fromFloor == 0 && s.toFloor == 1);
            int injShooter = shooter.health.hediffSet.hediffs.Count(h => h is Hediff_Injury) - startInjuriesShooter;
            int injRaiders = raiders.Sum(r => r.Destroyed ? 0 : r.health.hediffSet.hediffs.Count(h => h is Hediff_Injury)) - startInjuriesRaiders;
            Info("shots between floors: " + string.Join("; ", PZCombatStats.shots.Take(12).Select(s => $"{s.launcher} (floor {s.fromFloor + 1}) -> {s.target} (floor {s.toFloor + 1})")));
            Result("colonist upstairs fires at the raiders below (fire at will)", down > 0, $"{down} shots down; raider injuries +{injRaiders}, raiders down/dead {raiders.Count(r => r.Dead || r.Downed)}");
            Result("raiders on the ground fire at the colonist upstairs (their own AI)", up > 0, $"{up} shots up; colonist injuries +{injShooter}, colonist {(shooter.Dead ? "dead" : shooter.Downed ? "downed" : "standing")}");
            Result("shots between floors land (injuries)", injRaiders + injShooter > 0, $"raiders +{injRaiders}, colonist +{injShooter}; intercepts {PZCombatStats.intercepts}, passed over {PZCombatStats.passedOver}, slab stops {PZCombatStats.slabStops}");
            Result("the raider in the room below never shot upstairs", !PZCombatStats.shots.Any(s => s.launcher == raiderInside?.ToString()), $"{raiderInside} job {raiderInside?.CurJobDef?.defName}");
            stage = "c_save";
        }

        private static void AfterLoad(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int floorRoofs = houseA.Cells.Count(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_FloorRoof);
            Result("after reload: floors' roofs kept", a != null && floorRoofs == houseA.Area && map.roofGrid.RoofAt(houseB.CenterCell) == PZDefOf.PZ_FloorRoof, $"{floorRoofs} of {houseA.Area}");
            bool seeEdge = Heights.CanSee(raiderCell, edgeCell, map) && Heights.CanSee(edgeCell, raiderCell, map);
            bool seeCentre = Heights.CanSee(raiderCell, centreCell, map) || Heights.CanSee(centreCell, raiderCell, map);
            Result("after reload: sight between floors the same", seeEdge && !seeCentre, $"edge {seeEdge}, centre {seeCentre}");
            Room ga = houseA.CenterCell.GetRoom(map);
            Room gc = houseC.CenterCell.GetRoom(map);
            Result("after reload: temperatures kept", ga != null && gc != null && ga.Temperature > gc.Temperature, $"A ground {ga?.Temperature:0.0}, C {gc?.Temperature:0.0}");
            StartTicks(1250, "c_end");
        }
    }
}
