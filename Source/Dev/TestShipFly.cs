using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Test mode shipfly (multi-level gravships P4; -projectzfloors=4, Odyssey): a 3-storey gravship flies.
    //   A 13 x 11 gravship on the ground (TestGravLand.BuildShip, with its grav anchor) with ship decks over all of it on
    //   floor 2 and over its west part on floor 3. Floor 2: room A (wood walls, two doors, carpet, two beds, a standing
    //   lamp on a conduit riser from a ground battery) under the floor-3 decks, and an open deck east with a stockpile,
    //   steel, components, dirt and a pilot console. Floor 3: room B (walls, constructed roof) and a small thruster
    //   exhausting east over open air. Ordinary stairs and a slab walkway west of the ship lead up (as in shipset).
    //   F1 colonists walk up for real (one to a bed in room A, lies down and sleeps; one to the open floor-2 deck, given
    //      steel to carry; one to room B; one on the ground deck; one stays outside); rooms A and B get their own
    //      temperatures; everything on the ship is recorded (cells: foundation, terrain, under, roof; things: their cells;
    //      pawns), offsets ship-relative with the floor in y;
    //   F2 a real launch (console, tile picker, take-off cutscene) to a neighbouring tile; in flight: the old site has
    //      nothing left on any floor (open air, no foundation, roof or thing), the walkway beside it stands; save and
    //      reload mid-flight: the flying ship keeps its floor tags;
    //   F3 the new map: the ship lands turned East (the move designator), every cell, thing and pawn on its floor and turned
    //      place, the carried steel on floor 2, rooms A and B indoors with their temperatures, the lamp powered by the
    //      battery below through the riser, the stockpile on floor 2, carpets over open air (no soil under them), no ship
    //      cell floor-tagged on the map (safety net 0), the ship's bounds the footprint's;
    //   F4 back home (an existing map with a grav anchor) turned South, after the move designator refused: an ordinary
    //      slab on floor 2 over the footprint, a slab on floor 4 over it (above the ship), a wall standing in the column on
    //      floor 3, too few floors (the map's floor count lowered for the check); and accepted a platform with a wall
    //      right beside the footprint. Again every cell, thing and pawn checked against the record made before take-off.
    public static partial class TestRunner
    {
        private sealed class SfThing
        {
            public string id, def;
            public List<IntVec3> offs;
        }

        private sealed class SfCellRec
        {
            public string found, terr, under, roof;
        }

        private static IntVec3 sfO;
        private static int sfLeg, sfFrames, sfMarkerSeen, sfShotsTaken, sfUpperRec;
        private static bool sfLandedSeen, sfLandQueued, sfSaved;
        private static string sfLastStage = "", sfSleeper, sfCarrier, sfUp3, sfGroundP, sfLeft, sfCarried, sfLamp, sfBattery, sfStock;
        private static float sfStageAt, sfTempA, sfTempB;
        private static PlanetTile sfHomeTile = PlanetTile.Invalid, sfNewTile = PlanetTile.Invalid;
        private static IntVec3 sfRoot, sfOldEngine, sfRoomA, sfRoomB, sfOffA, sfOffB;
        private static Rot4 sfRot;
        // (the stage after a landing: shipstairs flies the same way and checks its own landing)
        private static string sfLandedStage = "sf_landed";
        private static readonly Dictionary<IntVec3, SfCellRec> sfCells = new Dictionary<IntVec3, SfCellRec>();
        private static readonly List<SfThing> sfThings = new List<SfThing>();
        private static readonly Dictionary<string, IntVec3> sfPawns = new Dictionary<string, IntVec3>();
        private static readonly List<IntVec3> sfStockOffs = new List<IntVec3>();
        private static readonly List<IntVec3> sfOldGround = new List<IntVec3>();

        private static readonly Func<Designator_MoveGravship, IntVec3, IntVec3> sfAdjusted =
            AccessTools.MethodDelegate<Func<Designator_MoveGravship, IntVec3, IntVec3>>(AccessTools.Method(typeof(Designator_MoveGravship), "GetSizeRotAdjustedCell"));

        private static readonly AccessTools.FieldRef<WorldComponent_GravshipController, float> sfTimeLeft =
            AccessTools.FieldRefAccess<WorldComponent_GravshipController, float>("timeLeft");

        // Cutscene frames (progress 0-1 over vanilla's 10 s): take-off at rest, rising, leaving; landing coming in, low, at the swap.
        private static readonly float[] SfTakeoffAt = { 0.02f, 0.45f, 0.62f };
        private static readonly float[] SfLandingAt = { 0.55f, 0.82f, 0.935f };

        private static IntVec3 SF(int dx, int dz) => new IntVec3(sfO.x + dx, 0, sfO.z + dz);

        private static IntVec3 SFF(int dx, int dz, int k, Map map) => ProjectZ.CellOnFloor(SF(dx, dz), k, map);

        private static Map SfHome() => Find.Maps.FirstOrDefault(m => m.Tile == sfHomeTile);

        private static Map SfNew() => Find.Maps.FirstOrDefault(m => m.Tile == sfNewTile);

        private static Pawn SfPawn(string id) => id == null ? null : Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned).FirstOrDefault(p => p.ThingID == id);

        // Where a recorded ship offset is after landing at root turned rot.
        private static IntVec3 SfAt(IntVec3 off, IntVec3 root, Rot4 rot, Map map) => ShipLocal.To(root, PrefabUtility.GetAdjustedLocalPosition(off, rot), map);

        private static string Def(TerrainDef t) => t?.defName ?? "-";

        private static void StepShipFly(Map map)
        {
            switch (stage)
            {
                case "sf_setup":
                    SetupShipFly(map);
                    return;
                case "sf_walk":
                    SfWalk(map);
                    return;
                case "sf_walked":
                    SfWalked(map);
                    return;
                case "sf_record":
                    SfRecordAndShoot(map);
                    return;
                case "sf_launch1":
                    SfLaunch1(map);
                    return;
                case "sf_fly":
                    SfFly();
                    return;
                case "sf_loaded":
                    SfLoaded();
                    return;
                case "sf_landed":
                    SfLanded();
                    return;
                case "sf_launch2":
                    SfLaunch2();
                    return;
                case "sf_end":
                    Result("F4 the colonist left at home is still there", SfPawn(sfLeft)?.Map == SfHome(), $"{SfPawn(sfLeft)?.Map}");
                    Result("no floor-tagged cell reached a map (safety net)", ShipLocal.Nets == 0, $"{ShipLocal.Nets}");
                    Finish();
                    return;
            }
        }

        private static void SetupShipFly(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || an.floors < 4 || !ModsConfig.OdysseyActive || PZDefOf.PZ_ShipDeck == null)
            {
                Result("shipfly setup", false, $"needs 4 floors and Odyssey; floors {an?.floors}");
                Finish();
                return;
            }
            Result("P4 sites found (capture and placement)", ShipLocal.Ready,
                string.Join(", ", CapturePatch.PerMethod.Concat(PlacePatch.PerMethod).Select(kv => kv.Key + " " + kv.Value)));
            const int W = 58, H = 28;
            CellRect found = CellRect.Empty;
            int bestBad = int.MaxValue;
            for (int z = an.groundZ - H - 4; z >= 4; z -= 3)
            {
                for (int x = 4; x + W < map.Size.x - 4; x += 3)
                {
                    CellRect r = new CellRect(x, z, W, H);
                    if (r.ExpandedBy(1).Cells.Any(c => c.InBounds(map) && c.GetThingList(map).Any(t => !t.def.destroyable)))
                    {
                        continue;
                    }
                    int bad = r.Cells.Count(c => c.GetTerrain(map).IsWater || c.GetTerrain(map).passability == Traversability.Impassable || map.roofGrid.Roofed(c) || c.GetFirstBuilding(map) != null);
                    if (bad < bestBad)
                    {
                        bestBad = bad;
                        found = r;
                    }
                }
                if (bestBad == 0)
                {
                    break;
                }
            }
            if (found.Area == 0)
            {
                Result("shipfly field", false, "map too small");
                Finish();
                return;
            }
            List<Pawn> cols = Colonists(map);
            foreach (IntVec3 c in found.ExpandedBy(3).ClipInsideMap(map))
            {
                if (ProjectZ.IsAnnex(c, map))
                {
                    continue;
                }
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable && !(t is Pawn p && cols.Contains(p)))
                    {
                        t.Destroy();
                    }
                }
                if (map.terrainGrid.FoundationAt(c) != null)
                {
                    map.terrainGrid.RemoveFoundation(c, false);
                }
                TerrainDef td = c.GetTerrain(map);
                if (map.terrainGrid.UnderTerrainAt(c) != null || td.IsWater || td.passability == Traversability.Impassable || !td.affordances.Contains(TerrainAffordanceDefOf.Heavy))
                {
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
                for (int k = 1; k < an.floors; k++)
                {
                    IntVec3 u = ProjectZ.CellOnFloor(c, k, map);
                    if (u.IsValid)
                    {
                        map.fogGrid.Unfog(u);
                    }
                }
            }
            DebugSettings.enableRandomMentalStates = false;
            Find.PlaySettings.autoRebuild = false;
            Find.ResearchManager.gravEngineInspected = true;
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            for (int i = 0; i < 24 && GenLocalDate.HourOfDay(map) != 10; i++)
            {
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + GenDate.TicksPerHour);
            }
            sfHomeTile = map.Tile;
            sfO = new IntVec3(found.minX + 8, 0, found.minZ + 6);
            BuildShip(map, sfO, 13, 11);
            Building_GravEngine e = GlEngine(map);
            e.nameHidden = false;
            TerrainDef wood = PZDefOf.PZ_UpperFloor;
            ThingDef woodLog = ThingDefOf.WoodLog;
            // access as in shipset: stairs A on the ground up to a floor-2 walkway west of the ship, stairs B on it up to a
            // floor-3 walkway cell next to the floor-3 decks
            foreach (int dz in Enumerable.Range(0, 10))
            {
                map.terrainGrid.SetTerrain(SFF(-1, dz, 1, map), wood);
            }
            foreach (int dz in new[] { 4, 5, 6 })
            {
                map.terrainGrid.SetTerrain(SFF(-2, dz, 1, map), wood);
            }
            Place(ThingDefOf.Wall, woodLog, SF(-2, 2), map);
            Place(ThingDefOf.Wall, woodLog, SF(-2, 8), map);
            Place(PZDefOf.PZ_Stairs, woodLog, SF(-2, -2), map, Rot4.North);
            Place(PZDefOf.PZ_Stairs, woodLog, SFF(-2, 5, 1, map), map, Rot4.North);
            map.terrainGrid.SetTerrain(SFF(-1, 7, 2, map), wood);
            // decks: floor 2 over the whole 13 x 11, floor 3 over its west part (x 0-6)
            for (int x = 0; x < 13; x++)
            {
                for (int z = 0; z < 11; z++)
                {
                    map.terrainGrid.SetTerrain(SFF(x, z, 1, map), PZDefOf.PZ_ShipDeck);
                    if (x <= 6)
                    {
                        map.terrainGrid.SetTerrain(SFF(x, z, 2, map), PZDefOf.PZ_ShipDeck);
                    }
                }
            }
            // floor 2, room A (x 0-6): walls, doors west (to the walkway) and east, carpet, beds, a lamp on a riser
            for (int x = 0; x <= 6; x++)
            {
                for (int z = 0; z <= 10; z++)
                {
                    bool ring = x == 0 || x == 6 || z == 0 || z == 10;
                    if (ring && !(x == 0 && z == 5) && !(x == 6 && z == 5))
                    {
                        Place(ThingDefOf.Wall, woodLog, SFF(x, z, 1, map), map);
                    }
                    else if (!ring)
                    {
                        map.terrainGrid.SetTerrain(SFF(x, z, 1, map), DefDatabase<TerrainDef>.GetNamed("CarpetRed"));
                    }
                }
            }
            Place(ThingDefOf.Door, woodLog, SFF(0, 5, 1, map), map);
            Place(ThingDefOf.Door, woodLog, SFF(6, 5, 1, map), map);
            Thing bed = Place(ThingDefOf.Bed, woodLog, SFF(2, 2, 1, map), map, Rot4.North);
            Place(ThingDefOf.Bed, woodLog, SFF(4, 2, 1, map), map, Rot4.North);
            ThingDef conduit = DefDatabase<ThingDef>.GetNamed("PowerConduit");
            Thing lamp = Place(DefDatabase<ThingDef>.GetNamed("StandingLamp"), null, SFF(3, 8, 1, map), map);
            Place(conduit, null, SFF(3, 8, 1, map), map);
            Place(conduit, null, SF(3, 8), map);
            Place(conduit, null, SF(2, 8), map);
            Thing battery = Place(DefDatabase<ThingDef>.GetNamed("Battery"), null, SF(1, 7), map, Rot4.North);
            CompPowerBattery bc = battery.TryGetComp<CompPowerBattery>();
            bc?.AddEnergy(bc.Props.storedEnergyMax);
            sfLamp = lamp.ThingID;
            sfBattery = battery.ThingID;
            // floor 2, the open deck east (x 7-12): stockpile, steel, components, dirt, a pilot console
            Zone_Stockpile zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            for (int x = 8; x <= 11; x++)
            {
                for (int z = 1; z <= 3; z++)
                {
                    zone.AddCell(SFF(x, z, 1, map));
                }
            }
            sfStock = zone.label;
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 75;
            GenSpawn.Spawn(steel, SFF(9, 2, 1, map), map);
            Thing comps = ThingMaker.MakeThing(ThingDefOf.ComponentIndustrial);
            comps.stackCount = 5;
            GenSpawn.Spawn(comps, SFF(10, 2, 1, map), map);
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Filth_Dirt), SFF(11, 6, 1, map), map);
            Place(ThingDefOf.PilotConsole, null, SFF(9, 8, 1, map), map, Rot4.North);
            // floor 3, room B (x 0-4): walls, a door west (to the walkway cell), a constructed roof; a thruster east
            for (int x = 0; x <= 4; x++)
            {
                for (int z = 0; z <= 10; z++)
                {
                    bool ring = x == 0 || x == 4 || z == 0 || z == 10;
                    if (ring && !(x == 0 && z == 7))
                    {
                        Place(ThingDefOf.Wall, woodLog, SFF(x, z, 2, map), map);
                    }
                    else if (!ring)
                    {
                        map.roofGrid.SetRoof(SFF(x, z, 2, map), RoofDefOf.RoofConstructed);
                    }
                }
            }
            Place(ThingDefOf.Door, woodLog, SFF(0, 7, 2, map), map);
            ThingDef small = DefDatabase<ThingDef>.GetNamed("SmallThruster");
            IntVec3 tpos = SFF(6, 2, 2, map);
            foreach (int px in new[] { 5, 6 })
            {
                CellRect rr = GenAdj.OccupiedRect(SFF(px, 2, 2, map), Rot4.West, small.size);
                if (rr.Cells.All(c => ProjectZ.GroundCellOf(c, map).x >= sfO.x + 5 && ProjectZ.GroundCellOf(c, map).x <= sfO.x + 6))
                {
                    tpos = SFF(px, 2, 2, map);
                    break;
                }
            }
            Place(small, null, tpos, map, Rot4.West);
            e.ForceSubstructureDirty();
            while (cols.Count < 5)
            {
                Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(p, SF(-5, -4), map);
                cols.Add(p);
            }
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], SF(-4 - i, -4));
                Draft(cols[i], true);
            }
            sfSleeper = cols[0].ThingID;
            sfCarrier = cols[1].ThingID;
            sfUp3 = cols[2].ThingID;
            sfGroundP = cols[3].ThingID;
            sfLeft = cols[4].ThingID;
            sfRoomA = SFF(3, 5, 1, map);
            sfRoomB = SFF(2, 5, 2, map);
            Info($"field {found}, ship SW {sfO}, engine {GlCell(e.Position, map)}, thruster {tpos}, set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)}), all {e.AllConnectedSubstructure.Count}, ground rows {an.groundZ}, stride {an.stride}, floors {an.floors}, bed {bed.Position}");
            Wait(30, "sf_walk");
        }

        // F1: the colonists walk to their places (stairs A, the walkway, stairs B; the ship's ground door).
        private static void SfWalk(Map map)
        {
            GoTo(SfPawn(sfSleeper), SFF(2, 5, 1, map));
            GoTo(SfPawn(sfCarrier), SFF(10, 5, 1, map));
            GoTo(SfPawn(sfUp3), sfRoomB);
            GoTo(SfPawn(sfGroundP), SF(3, 2));
            GoTo(SfPawn(sfLeft), SF(-8, 4));
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            TickUntil(20000, () => SfPawn(sfSleeper)?.Position == SFF(2, 5, 1, map) && SfPawn(sfCarrier)?.Position == SFF(10, 5, 1, map)
                && SfPawn(sfUp3)?.Position == sfRoomB && SfPawn(sfGroundP)?.Position == SF(3, 2), "sf_walked", 30);
        }

        private static void SfWalked(Map map)
        {
            tickWatch = null;
            Pawn sleeper = SfPawn(sfSleeper), carrier = SfPawn(sfCarrier), up3 = SfPawn(sfUp3), gp = SfPawn(sfGroundP);
            Result("F1 colonists walked up the stairs to floors 2 and 3 and onto the ground deck",
                sleeper?.Position == SFF(2, 5, 1, map) && carrier?.Position == SFF(10, 5, 1, map) && up3?.Position == sfRoomB && gp?.Position == SF(3, 2),
                $"{sleeper?.Position}, {carrier?.Position}, {up3?.Position}, {gp?.Position} after {ticksTotal} ticks");
            // the sleeper lies down in the bed (tired), the carrier is handed steel to carry
            Building_Bed bed = SFF(2, 2, 1, map).GetFirstThing<Building_Bed>(map);
            if (sleeper != null && bed != null)
            {
                Draft(sleeper, false);
                sleeper.needs.rest.CurLevel = 0.05f;
                sleeper.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown, bed), JobCondition.InterruptForced);
            }
            if (carrier != null)
            {
                Thing s = ThingMaker.MakeThing(ThingDefOf.Steel);
                s.stackCount = 20;
                sfCarried = s.ThingID;
                carrier.carryTracker.TryStartCarry(s);
            }
            StartTicks(400, "sf_record");
        }

        // F1: temperatures, the record, pictures; then the launch.
        private static void SfRecordAndShoot(Map map)
        {
            Building_GravEngine e = GlEngine(map);
            Pawn sleeper = SfPawn(sfSleeper), carrier = SfPawn(sfCarrier);
            Result("F1 a colonist sleeps in a bed on floor 2", sleeper != null && sleeper.InBed() && ProjectZ.FloorOf(sleeper.Position, map) == 1, $"{sleeper?.CurJobDef?.defName} at {sleeper?.Position}, in bed {sleeper?.InBed()}");
            Result("F1 a colonist on floor 2 carries steel", carrier?.carryTracker.CarriedThing?.ThingID == sfCarried, $"{carrier?.carryTracker.CarriedThing}");
            Room ra = sfRoomA.GetRoom(map), rb = sfRoomB.GetRoom(map);
            Result("F1 rooms A (floor 2) and B (floor 3) are indoor rooms", ra != null && rb != null && ra != rb && !ra.UsesOutdoorTemperature && !rb.UsesOutdoorTemperature && !ra.PsychologicallyOutdoors && !rb.PsychologicallyOutdoors,
                $"A {ra?.CellCount} cells outdoor {ra?.UsesOutdoorTemperature}, B {rb?.CellCount} cells outdoor {rb?.UsesOutdoorTemperature}");
            if (ra != null)
            {
                ra.Temperature = 6f;
            }
            if (rb != null)
            {
                rb.Temperature = 33f;
            }
            sfTempA = ra?.Temperature ?? 0f;
            sfTempB = rb?.Temperature ?? 0f;
            CompPowerTrader lamp = SfThingById(map, sfLamp)?.TryGetComp<CompPowerTrader>();
            Result("F1 the floor-2 lamp is powered by the ground battery through the riser", lamp != null && lamp.PowerOn && lamp.PowerNet != null && lamp.PowerNet.batteryComps.Any(b => b.parent.ThingID == sfBattery), $"on {lamp?.PowerOn}, net batteries {lamp?.PowerNet?.batteryComps.Count}");
            HashSet<IntVec3> set = e.ValidSubstructure;
            Result("F1 the ship set holds ground + both floors' decks", set.Count == 147 + 143 + 77 && SsOnFloor(set, 1, map) == 143 && SsOnFloor(set, 2, map) == 77, $"{set.Count}: {SsFloors(set, map)}");
            foreach (string id in new[] { sfSleeper, sfCarrier, sfUp3, sfGroundP })
            {
                Pawn p = SfPawn(id);
                if (p == null || !e.ValidSubstructureAt(p.Position))
                {
                    Result("F1 colonist aboard", false, $"{p} at {p?.Position}");
                }
            }
            SfRecord(map, e);
            sfOldEngine = e.Position;
            int roofsUp = sfCells.Count(kv => kv.Key.y > 0 && kv.Value.roof != "-");
            Info($"recorded: {sfCells.Count} cells ({sfCells.Keys.Count(k => k.y == 1)} f2, {sfCells.Keys.Count(k => k.y == 2)} f3, {roofsUp} upper roofs), {sfThings.Count} things, {sfPawns.Count} pawns, stockpile {sfStockOffs.Count} cells, temps A {sfTempA:0.0} B {sfTempB:0.0}");
            GlShot(0, SF(6, 5), 16f, "sf_0_home_ground");
            GlShot(1, SF(6, 5), 16f, "sf_0_home_f2");
            GlShot(2, SF(6, 5), 16f, "sf_0_home_f3");
            glAfter = "sf_launch1";
            Wait(2, "gl_shots");
        }

        private static Thing SfThingById(Map map, string id) => map?.listerThings.AllThings.FirstOrDefault(t => t.ThingID == id);

        // Everything on the ship, by ship offset (the floor in y).
        private static void SfRecord(Map map, Building_GravEngine e)
        {
            sfCells.Clear();
            sfThings.Clear();
            sfPawns.Clear();
            sfStockOffs.Clear();
            HashSet<IntVec3> set = e.ValidSubstructure;
            IntVec3 o = e.Position;
            TerrainGrid tg = map.terrainGrid;
            foreach (IntVec3 c in set)
            {
                sfCells[ShipLocal.From(c, o, map)] = new SfCellRec { found = Def(tg.FoundationAt(c)), terr = Def(tg.TerrainAt(c)), under = Def(tg.UnderTerrainAt(c)), roof = map.roofGrid.RoofAt(c)?.defName ?? "-" };
                if (map.zoneManager.ZoneAt(c) is Zone_Stockpile zs && zs.label == sfStock)
                {
                    sfStockOffs.Add(ShipLocal.From(c, o, map));
                }
            }
            foreach (Thing t in map.listerThings.AllThings)
            {
                // (filth the walkers tracked in comes and goes with the game's own rules: only the dirt laid upstairs is followed)
                if (!t.Spawned || t is Pawn || t is Mote || t is Blueprint || t is Frame || !t.def.bringAlongOnGravship || t is Filth && ProjectZ.FloorOf(t.Position, map) == 0 || !e.OnValidSubstructure(t) || !t.OccupiedRect().Cells.All(set.Contains))
                {
                    continue;
                }
                sfThings.Add(new SfThing { id = t.ThingID, def = t.def.defName, offs = t.OccupiedRect().Cells.Select(c => ShipLocal.From(c, o, map)).ToList() });
            }
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (set.Contains(p.Position))
                {
                    sfPawns[p.ThingID] = ShipLocal.From(p.Position, o, map);
                }
            }
            sfUpperRec = sfCells.Keys.Count(k => k.y != 0);

            sfOffA = ShipLocal.From(sfRoomA, o, map);
            sfOffB = ShipLocal.From(sfRoomB, o, map);
            sfOldGround.Clear();
            sfOldGround.AddRange(set.Where(c => ProjectZ.FloorOf(c, map) == 0));
        }

        // F2: to a neighbouring tile (a new map).
        private static void SfLaunch1(Map map)
        {
            List<PlanetTile> near = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(map.Tile, near);
            Building_GravEngine e = GlEngine(map);
            CompPilotConsole console = SsConsole(map, e);
            PlanetTile dest = PlanetTile.Invalid;
            foreach (PlanetTile t in near)
            {
                if (Find.WorldObjects.MapParentAt(t) == null && !Find.WorldGrid[t].WaterCovered && Find.WorldGrid[t].hilliness != Hilliness.Impassable)
                {
                    dest = t;
                    break;
                }
            }
            if (GenCommandLine.CommandLineArgPassed("projectzshiporbit") && Find.WorldGrid.Orbit != null)
            {
                // (-projectzshiporbit: the first flight goes to orbit instead, the closest free orbit tile; a space map)
                PlanetTile up = Find.WorldGrid.Orbit.GetClosestTile_NewTemp(map.Tile);
                dest = up.Valid && Find.WorldObjects.MapParentAt(up) == null ? up : PlanetTile.Invalid;
                Info($"F2 to orbit: tile {up} ({(dest.Valid ? "free" : "taken")})");
            }
            if (!dest.Valid)
            {
                Result("F2 a neighbouring tile to fly to", false, "none");
                Finish();
                return;
            }
            sfNewTile = dest;
            sfLeg = 1;
            SfLaunch(map, e, dest);
        }

        private static void SfLaunch(Map map, Building_GravEngine engine, PlanetTile dest)
        {
            if (engine.cooldownCompleteTick > Find.TickManager.TicksGame)
            {
                engine.cooldownCompleteTick = Find.TickManager.TicksGame;
            }
            CompPilotConsole console = SsConsole(map, engine);
            AcceptanceReport can = console?.CanUseNow() ?? false;
            Result($"{(sfLeg == 1 ? "F2" : "F4")} the console allows the launch with upper decks", can.Accepted, can.Accepted ? "" : can.Reason);
            if (console == null || !can.Accepted)
            {
                Finish();
                return;
            }
            Info($"leg {sfLeg}: launch from {map} (tile {map.Tile}) to tile {dest}: engine {GlCell(engine.Position, map)}, set {engine.ValidSubstructure.Count} ({SsFloors(engine.ValidSubstructure, map)}), fuel {engine.TotalFuel:0}");
            // (leg 1 flies with the ground in view, leg 2 with the top floor in view: what the player sees in each)
            FloorView.Set(sfLeg == 2 ? 2 : 0);
            engine.launchInfo = new LaunchInfo { quality = 1f, doNegativeOutcome = false };
            console.StartChoosingDestination_NewTemp();
            TilePicker picker = Find.TilePicker;
            Func<PlanetTile, bool> validator = Traverse.Create(picker).Field("validator").GetValue<Func<PlanetTile, bool>>();
            Action<PlanetTile> chosen = Traverse.Create(picker).Field("tileChosen").GetValue<Action<PlanetTile>>();
            if (!picker.Active || validator == null || chosen == null || !validator(dest))
            {
                Result($"leg {sfLeg} launch", false, "picker refused / not started");
                Finish();
                return;
            }
            AccessTools.Method(typeof(TilePicker), "StopTargetingInt").Invoke(picker, null);
            chosen(dest);
            foreach (Window w in Find.WindowStack.Windows.ToList())
            {
                if (w is Dialog_MessageBox box)
                {
                    Info("accepting dialog: " + box.text.ToString().Replace("\n", " | "));
                    Find.WindowStack.TryRemove(box, doCloseSound: false);
                    box.buttonAAction?.Invoke();
                }
            }
            sfFrames = 0;
            sfLandedSeen = false;
            sfLandQueued = true;
            sfMarkerSeen = -1;
            sfLastStage = "";
            sfShotsTaken = 0;
            stage = "sf_fly";
        }

        private static void SfFly()
        {
            sfFrames++;
            WorldComponent_GravshipController ctl = Find.GravshipController;
            bool cut = WorldComponent_GravshipController.CutsceneInProgress;
            string st = !cut ? (ctl.IsGravshipTravelling ? "travelling" : (ctl.LandingAreaConfirmationInProgress ? "marker" : "idle"))
                : (glIsTakeoff(ctl) ? "takeoff" : "landing");
            if (st != sfLastStage)
            {
                if (sfLastStage == "takeoff" || sfLastStage == "landing")
                {
                    // IsoRim (3D) shows every floor during the cutscene and puts the player's view back after it
                    int want = sfLeg == 2 ? 2 : 0;
                    Result($"leg {sfLeg}: floor view back to the player's after the {sfLastStage}", FloorView.Current == want || Find.CurrentMap == null,
                        $"view {FloorView.Current}, want {want}");
                }
                Info($"leg {sfLeg}: stage {st} at frame {sfFrames}, tick {Find.TickManager.TicksGame}");
                sfLastStage = st;
                sfStageAt = Time.realtimeSinceStartup;
                sfShotsTaken = 0;
                if (st == "travelling" && sfLeg == 1 && !sfSaved)
                {
                    SfMidFlight();
                    return;
                }
            }
            // the cutscene as the player sees it (IsoRim 3D): frames at set points of its progress
            float[] at = st == "takeoff" ? SfTakeoffAt : SfLandingAt;
            float left = sfTimeLeft(ctl);
            float progress = Mathf.Clamp01(1f - left / 10f);
            if (cut && sfShotsTaken < at.Length && left > 0f && left <= 10f && progress >= at[sfShotsTaken])
            {
                Info($"leg {sfLeg}: {st} frame {sfShotsTaken + 1} at progress {progress:0.00}, floor view {FloorView.Current}");
                if (sfShotsTaken == 0 && IsoRimBridge.Present && IsoRimBridge.View3DOn)
                {
                    AnnexMapComponent an = ProjectZ.Annex(Find.CurrentMap);
                    Result($"leg {sfLeg}: all floors shown during the {st} (IsoRim 3D)", an != null && FloorView.Current == an.floors - 1, $"view {FloorView.Current}, floors {an?.floors}");
                }
                sfShotsTaken++;
                ZCapture($"sf_leg{sfLeg}_{st}_{sfShotsTaken}");
                return;
            }
            if (cut && !glIsTakeoff(ctl))
            {
                sfLandedSeen = true;
            }
            GravshipLandingMarker marker = glMarker(ctl);
            bool atMarker = sfLandQueued && !cut && ctl.LandingAreaConfirmationInProgress && WorldRendererUtility.DrawingMap
                && (sfLeg == 1 ? marker != null && marker.Spawned && Find.CurrentMap == marker.Map : ctl.landingMap != null && Find.CurrentMap == ctl.landingMap);
            if (atMarker)
            {
                if (sfMarkerSeen < 0)
                {
                    sfMarkerSeen = sfFrames;
                }
                else if (sfFrames - sfMarkerSeen >= 30)
                {
                    sfLandQueued = false;
                    if (sfLeg == 1)
                    {
                        SfLandNew(ctl, marker);
                    }
                    else
                    {
                        SfLandHome(ctl, marker);
                    }
                    return;
                }
            }
            if (sfLandedSeen && !cut)
            {
                Info($"leg {sfLeg}: landing ended after {sfFrames} frames");
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                Wait(30, sfLandedStage);
                return;
            }
            if (sfFrames > 30000)
            {
                Result($"leg {sfLeg} flight", false, "no landing after 30000 frames, stage " + st);
                Finish();
            }
        }

        // F2 in flight: the old site, then a save and a reload.
        private static void SfMidFlight()
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Map home = SfHome();
            AnnexMapComponent an = ProjectZ.Annex(home);
            int bad = 0;
            string first = null;
            foreach (IntVec3 g in sfOldGround)
            {
                List<string> why = new List<string>();
                if (home.terrainGrid.FoundationAt(g) != null)
                {
                    why.Add("foundation " + home.terrainGrid.FoundationAt(g).defName);
                }
                if (home.roofGrid.RoofAt(g) != null)
                {
                    why.Add("roof " + home.roofGrid.RoofAt(g).defName);
                }
                for (int k = 1; k < an.floors; k++)
                {
                    IntVec3 u = ProjectZ.CellOnFloor(g, k, home);
                    if (home.terrainGrid.TerrainAt(u) != PZDefOf.PZ_OpenAir || home.terrainGrid.FoundationAt(u) != null)
                    {
                        why.Add($"f{k + 1} {Def(home.terrainGrid.TerrainAt(u))}");
                    }
                    if (home.roofGrid.RoofAt(u) != null)
                    {
                        why.Add($"f{k + 1} roof {home.roofGrid.RoofAt(u).defName}");
                    }
                    Thing t = u.GetThingList(home).FirstOrDefault(x => !(x is Mote));
                    if (t != null)
                    {
                        why.Add($"f{k + 1} {t.def.defName}");
                    }
                }
                if (why.Count > 0)
                {
                    bad++;
                    first = first ?? g + ": " + string.Join(", ", why);
                }
            }
            Result("F2 nothing left at the old site on any floor", bad == 0, $"{bad} of {sfOldGround.Count} columns; {first}");
            int walk = Enumerable.Range(0, 10).Count(dz => SlabDefs.Is(SFF(-1, dz, 1, home).GetTerrain(home)));
            Result("F2 the walkway beside the old site still stands", walk == 10, $"{walk} of 10");
            Pawn left = SfPawn(sfLeft);
            Result("F2 the colonist left outside stays at home", left != null && left.Map == home, $"{left?.Map}");
            Gravship g2 = Find.WorldObjects.AllWorldObjects.OfType<Gravship>().FirstOrDefault();
            Result("F2 the flying ship's bounds are its footprint", g2 != null && g2.Bounds.Width == 13 && g2.Bounds.Height == 13, $"{g2?.Bounds}");
            Result("F2 the flying ship's data keeps its floors", g2 != null && g2.Foundations.Keys.Count(k => k.y != 0) == sfUpperRec && g2.Foundations.Values.All(v => v != null), $"upper keys {g2?.Foundations.Keys.Count(k => k.y != 0)} of {sfUpperRec}");
            sfSaved = true;
            GameDataSaveLoader.SaveGame("pz_shipfly");
            stage = "sf_loaded";
            pendingLoad = "pz_shipfly";
        }

        private static void SfLoaded()
        {
            Gravship g = Current.Game.Gravship ?? Find.WorldObjects.AllWorldObjects.OfType<Gravship>().FirstOrDefault();
            Info($"after the reload: gravship {(g == null ? "none" : g.Spawned ? "in flight" : "arrived")}, current {Current.Game.Gravship != null}");
            Result("F2 a save made in flight reloads with the ship's floor tags", g != null && g.Foundations.Keys.Count(k => k.y != 0) == sfUpperRec && g.Pawns.Count() == 4,
                $"{(g == null ? "no gravship" : $"upper keys {g.Foundations.Keys.Count(k => k.y != 0)} of {sfUpperRec}, pawns {g.Pawns.Count()}")}");
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            sfLastStage = "travelling";
            stage = "sf_fly";
        }

        // F3: the new map's marker; turned East by the move designator next to it.
        private static void SfLandNew(WorldComponent_GravshipController ctl, GravshipLandingMarker marker)
        {
            Map map = marker.Map;
            Info($"new map {map} size {map.Size}, floors {ProjectZ.Annex(map)?.floors}, marker at {marker.Position}, cells {marker.GravshipCells.Count()} (all ground {marker.GravshipCells.All(c => c.y == 0)})");
            Result("F3 the marker's cells are the footprint", marker.GravshipCells.Count() == 147 && marker.GravshipCells.All(c => c.y == 0), $"{marker.GravshipCells.Count()}");
            IntVec3 start = marker.Position;
            marker.GravshipRotation = Rot4.East;
            Designator_MoveGravship d = ctl.MoveDesignator();
            IntVec2 size = marker.gravship.Bounds.Size;
            IntVec3 root = IntVec3.Invalid, click = IntVec3.Invalid;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(start, 14f, true))
            {
                IntVec3 k = SfClick(d, c, size, Rot4.East);
                if (k.IsValid && d.CanDesignateCell(k).Accepted)
                {
                    root = c;
                    click = k;
                    break;
                }
            }
            if (root.IsValid)
            {
                d.DesignateSingleCell(click);
            }
            else
            {
                Info("no spot for the ship turned East near the marker: lands North where it is");
                marker.GravshipRotation = Rot4.North;
            }
            SfConfirm(ctl, marker);
        }

        private static IntVec3 SfClick(Designator_MoveGravship d, IntVec3 root, IntVec2 size, Rot4 rot)
        {
            int reach = Math.Max(size.x, size.z) + 2;
            for (int dx = -reach; dx <= reach; dx++)
            {
                for (int dz = -reach; dz <= reach; dz++)
                {
                    IntVec3 c = root + new IntVec3(dx, 0, dz);
                    if (PrefabUtility.GetRoot(sfAdjusted(d, c), size, rot) == root)
                    {
                        return c;
                    }
                }
            }
            return IntVec3.Invalid;
        }

        private static void SfConfirm(WorldComponent_GravshipController ctl, GravshipLandingMarker marker)
        {
            if (marker.Spawned && !(Find.DesignatorManager.SelectedDesignator is Designator_MoveGravship))
            {
                sfRoot = marker.Position;
                sfRot = marker.GravshipRotation;
                Info($"leg {sfLeg}: confirmed, root {sfRoot}, rotation {sfRot}");
                FloorView.Set(sfLeg == 2 ? 2 : 0);
                marker.BeginLanding(ctl);
                glMarker(ctl) = null;
            }
            else
            {
                Result($"leg {sfLeg} marker placed", false, "not placed");
                Finish();
            }
        }

        // F4: home (an existing map with a grav anchor): the designator's refusals, then landing turned South.
        private static void SfLandHome(WorldComponent_GravshipController ctl, GravshipLandingMarker marker)
        {
            Map map = ctl.landingMap;
            AnnexMapComponent an = ProjectZ.Annex(map);
            marker.GravshipRotation = Rot4.South;
            Designator_MoveGravship d = ctl.MoveDesignator();
            IntVec2 size = marker.gravship.Bounds.Size;
            FloorView.Set(0);
            // a clean spot east of the old site
            IntVec3 want = SF(34, 6), root = IntVec3.Invalid, click = IntVec3.Invalid;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(want, 8f, true))
            {
                IntVec3 k = SfClick(d, c, size, Rot4.South);
                if (k.IsValid && d.CanDesignateCell(k).Accepted)
                {
                    root = c;
                    click = k;
                    break;
                }
            }
            if (!root.IsValid)
            {
                Result("F4 a clean landing spot at home", false, "none near " + want);
                Finish();
                return;
            }
            List<IntVec3> foot = marker.GravshipCells.Select(c => new IntVec3(root.x + c.x, 0, root.z + c.z)).ToList();
            IntVec3 f0 = foot.OrderBy(c => c.x + c.z * 1000).First();
            IntVec3 outside = f0 + IntVec3.West;
            int guard = 0;
            while (foot.Contains(outside) && guard++ < 20)
            {
                outside += IntVec3.West;
            }
            Info($"home landing spot {root} (South), footprint {foot.Count}, test cell {f0}, outside {outside}");
            MethodInfoRefuse(d, click, f0, map, "F4 refused: an ordinary slab on floor 2 over the footprint", null, () =>
            {
                map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(f0, 1, map), PZDefOf.PZ_UpperFloor);
            }, () =>
            {
                map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(f0, 1, map), PZDefOf.PZ_OpenAir);
                map.roofGrid.SetRoof(f0, null);
            });
            MethodInfoRefuse(d, click, f0, map, "F4 refused: a slab on floor 4 (above the ship's height)", "PZ_ShipFloorAbove".Translate(), () =>
            {
                map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(f0, 3, map), PZDefOf.PZ_UpperFloor);
            }, () =>
            {
                map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(f0, 3, map), PZDefOf.PZ_OpenAir);
                map.roofGrid.SetRoof(ProjectZ.CellOnFloor(f0, 2, map), null);
            });
            Thing wall = null;
            MethodInfoRefuse(d, click, f0, map, "F4 refused: a wall standing in the column on floor 3", null, () =>
            {
                // (a stray wall on open air, as an older save could hold: placed outside gravity, compat G2)
                Gravity.Suspend();
                try
                {
                    wall = Place(ThingDefOf.Wall, ThingDefOf.WoodLog, ProjectZ.CellOnFloor(f0, 2, map), map);
                }
                finally
                {
                    Gravity.Resume();
                }
            }, () => wall?.Destroy());
            // too few floors: the map's count lowered for the check only
            int floors = an.floors;
            an.floors = 2;
            AcceptanceReport few = d.CanDesignateCell(click);
            an.floors = floors;
            Result("F4 refused: too few floors on the map", !few.Accepted && few.Reason == "PZ_ShipNotEnoughFloors".Translate(), GlRep(few));
            // a platform with a wall right beside the footprint: fine
            IntVec3 po = ProjectZ.CellOnFloor(outside, 1, map);
            map.terrainGrid.SetTerrain(po, PZDefOf.PZ_UpperFloor);
            Thing pw = Place(ThingDefOf.Wall, ThingDefOf.WoodLog, po, map);
            AcceptanceReport beside = d.CanDesignateCell(click);
            Result("F4 accepted: a platform with a wall right beside the footprint", beside.Accepted, GlRep(beside));
            pw.Destroy();
            map.terrainGrid.SetTerrain(po, PZDefOf.PZ_OpenAir);
            map.roofGrid.SetRoof(outside, null);
            AcceptanceReport clean = d.CanDesignateCell(click);
            Result("F4 accepted: the clean spot", clean.Accepted, GlRep(clean));
            d.DesignateSingleCell(click);
            SfConfirm(ctl, marker);
        }

        // One refusal: the fixture placed, the designator's reason (and the red cells) read, the fixture taken away.
        private static void MethodInfoRefuse(Designator_MoveGravship d, IntVec3 click, IntVec3 cell, Map map, string label, string reason, Action put, Action undo)
        {
            put();
            AcceptanceReport r = d.CanDesignateCell(click);
            List<IntVec3> valid = new List<IntVec3>(), invalid = new List<IntVec3>();
            AccessTools.Method(typeof(Designator_MoveGravship), "ValidGravshipLocation").Invoke(d, new object[] { click, valid, invalid });
            bool red = invalid.Contains(cell) && !valid.Contains(cell);
            Result(label, !r.Accepted && (reason == null || r.Reason == reason) && red, $"{GlRep(r)}; red cells {invalid.Count}, the cell red {red}");
            undo();
        }

        // F3 / F4: what landed where.
        private static void SfLanded()
        {
            Map map = sfLeg == 1 ? SfNew() : SfHome();
            Building_GravEngine e = map == null ? null : GlEngine(map);
            string leg = sfLeg == 1 ? "F3" : "F4";
            if (e == null)
            {
                Result(leg + " landed", false, "no engine on " + map);
                Finish();
                return;
            }
            AnnexMapComponent an = ProjectZ.Annex(map);
            Result($"{leg} landed on the ground ({sfRot})", ProjectZ.FloorOf(e.Position, map) == 0 && e.ValidSubstructure.Count == 147 + 143 + 77, $"engine {GlCell(e.Position, map)}, root {sfRoot}, set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)})");
            // cells
            TerrainGrid tg = map.terrainGrid;
            int bad = 0;
            string first = null;
            foreach (KeyValuePair<IntVec3, SfCellRec> kv in sfCells)
            {
                IntVec3 c = SfAt(kv.Key, sfRoot, sfRot, map);
                string got = $"{Def(tg.FoundationAt(c))}/{Def(tg.TerrainAt(c))}/{Def(tg.UnderTerrainAt(c))}/{map.roofGrid.RoofAt(c)?.defName ?? "-"}";
                string want = $"{kv.Value.found}/{kv.Value.terr}/{kv.Value.under}/{kv.Value.roof}";
                if (got != want || ProjectZ.FloorOf(c, map) != kv.Key.y)
                {
                    bad++;
                    first = first ?? $"{kv.Key} at {c} f{ProjectZ.FloorOf(c, map) + 1}: {got}, want {want}";
                }
            }
            Result($"{leg} every cell on its floor and place (foundation, terrain, under, roof)", bad == 0, $"{bad} of {sfCells.Count}; {first}");
            int carpets = sfCells.Count(kv => kv.Key.y > 0 && kv.Value.terr.StartsWith("Carpet"));
            int carpetsOk = sfCells.Count(kv => kv.Key.y > 0 && kv.Value.terr.StartsWith("Carpet") && tg.UnderTerrainAt(SfAt(kv.Key, sfRoot, sfRot, map)) == PZDefOf.PZ_OpenAir);
            Result($"{leg} carpets upstairs lie on decks over open air (no soil)", carpets > 0 && carpetsOk == carpets, $"{carpetsOk} of {carpets}");
            // things
            bad = 0;
            first = null;
            foreach (SfThing t in sfThings)
            {
                Thing now = SfThingById(map, t.id);
                HashSet<IntVec3> want = new HashSet<IntVec3>(t.offs.Select(o => SfAt(o, sfRoot, sfRot, map)));
                if (now == null || !now.Spawned || !want.SetEquals(now.OccupiedRect().Cells))
                {
                    bad++;
                    Thing any = now ?? Find.Maps.SelectMany(m => m.listerThings.AllThings).FirstOrDefault(x => x.ThingID == t.id);
                    first = first ?? $"{t.def} {t.id}: {(now == null ? "missing" : now.OccupiedRect().ToString())}, want {string.Join(" ", want)}; {(any == null ? "on no map" : any.Map + " " + any.Position + " destroyed " + any.Destroyed)}";
                }
            }
            Result($"{leg} every thing on its floor and place", bad == 0, $"{bad} of {sfThings.Count}; {first}");
            bad = 0;
            first = null;
            foreach (KeyValuePair<string, IntVec3> kv in sfPawns)
            {
                Pawn p = SfPawn(kv.Key);
                IntVec3 want = SfAt(kv.Value, sfRoot, sfRot, map);
                if (p == null || p.Map != map || p.Position != want)
                {
                    bad++;
                    first = first ?? $"{p?.LabelShort ?? kv.Key} at {p?.Position}, want {want}";
                }
            }
            Result($"{leg} every pawn on its floor and place", bad == 0 && sfPawns.Count >= 4, $"{bad} of {sfPawns.Count}; {first}");
            if (sfLeg == 1)
            {
                Thing carried = SfThingById(map, sfCarried);
                Result("F3 the carried steel came along on floor 2", carried != null && ProjectZ.FloorOf(carried.Position, map) == 1 && e.ValidSubstructure.Contains(carried.Position), $"{carried?.Position} f{(carried == null ? 0 : ProjectZ.FloorOf(carried.Position, map) + 1)}");
            }
            // rooms and temperature
            Room ra = SfAt(sfOffA, sfRoot, sfRot, map).GetRoom(map), rb = SfAt(sfOffB, sfRoot, sfRot, map).GetRoom(map);
            Result($"{leg} rooms A and B are indoor rooms on floors 2 and 3", ra != null && rb != null && ra != rb && !ra.UsesOutdoorTemperature && !rb.UsesOutdoorTemperature && !ra.PsychologicallyOutdoors && !rb.PsychologicallyOutdoors,
                $"A {ra?.CellCount} outdoor {ra?.UsesOutdoorTemperature}, B {rb?.CellCount} outdoor {rb?.UsesOutdoorTemperature}");
            Result($"{leg} their temperatures came along", ra != null && rb != null && Mathf.Abs(ra.Temperature - sfTempA) < 3f && Mathf.Abs(rb.Temperature - sfTempB) < 3f,
                $"A {ra?.Temperature:0.0} (was {sfTempA:0.0}), B {rb?.Temperature:0.0} (was {sfTempB:0.0}), outdoors {map.mapTemperature.OutdoorTemp:0.0}");
            CompPowerTrader lamp = SfThingById(map, sfLamp)?.TryGetComp<CompPowerTrader>();
            Result($"{leg} the floor-2 lamp is powered by the ground battery (riser)", lamp != null && lamp.PowerOn && lamp.PowerNet != null && lamp.PowerNet.batteryComps.Any(b => b.parent.ThingID == sfBattery), $"on {lamp?.PowerOn}, batteries {lamp?.PowerNet?.batteryComps.Count}");
            int stock = sfStockOffs.Count(o => map.zoneManager.ZoneAt(SfAt(o, sfRoot, sfRot, map)) is Zone_Stockpile z && z.label == sfStock);
            Result($"{leg} the stockpile on floor 2 came along", sfStockOffs.Count == 12 && stock == 12, $"{stock} of {sfStockOffs.Count}");
            // nothing of the ship off its floors: every deck on the map is a recorded cell
            int decks = 0;
            for (int k = 1; k < an.floors; k++)
            {
                for (int z = 0; z < an.groundZ; z++)
                {
                    for (int x = 0; x < map.Size.x; x++)
                    {
                        if (ShipDeck.At(ProjectZ.CellOnFloor(new IntVec3(x, 0, z), k, map), map))
                        {
                            decks++;
                        }
                    }
                }
            }
            Result($"{leg} the map's decks are exactly the ship's", decks == sfUpperRec, $"{decks} (recorded {sfUpperRec})");
            Result($"{leg} no floor-tagged cell reached the map", ShipLocal.Nets == 0, $"{ShipLocal.Nets}");
            IntVec3 eg = ProjectZ.GroundCellOf(e.Position, map);
            GlShot(0, eg, 16f, $"sf_{sfLeg}_landed_ground");
            GlShot(1, eg, 16f, $"sf_{sfLeg}_landed_f2");
            GlShot(2, eg, 16f, $"sf_{sfLeg}_landed_f3");
            glAfter = sfLeg == 1 ? "sf_launch2" : "sf_end";
            Wait(2, "gl_shots");
        }

        // F4: back home from the new map (refuelled; the record made again on the new map).
        private static void SfLaunch2()
        {
            Map map = SfNew();
            Building_GravEngine e = map == null ? null : GlEngine(map);
            if (e == null)
            {
                Result("F4 launch from the new map", false, "no engine");
                Finish();
                return;
            }
            foreach (Thing t in map.listerThings.AllThings.Where(t => t.def.defName == "ChemfuelTank").ToList())
            {
                CompRefuelable f = t.TryGetComp<CompRefuelable>();
                f?.Refuel(f.Props.fuelCapacity - f.Fuel);
            }
            // (the room temperatures again, the record from here)
            Room ra = SfAt(sfOffA, sfRoot, sfRot, map).GetRoom(map), rb = SfAt(sfOffB, sfRoot, sfRot, map).GetRoom(map);
            IntVec3 a = SfAt(sfOffA, sfRoot, sfRot, map), b = SfAt(sfOffB, sfRoot, sfRot, map);
            if (ra != null)
            {
                ra.Temperature = 4f;
            }
            if (rb != null)
            {
                rb.Temperature = 36f;
            }
            sfTempA = ra?.Temperature ?? 0f;
            sfTempB = rb?.Temperature ?? 0f;
            sfRoomA = a;
            sfRoomB = b;
            SfRecord(map, e);
            sfLeg = 2;
            SfLaunch(map, e, sfHomeTile);
        }
    }
}
