using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Test mode shiproofatrium (-projectzfloors=4 -projectzseed=7, Odyssey): a floor taken up under a covered column leaves
    // the invisible atrium roof under it (FloorRoof.Freed, user 2026-11-14), in a house and on a gravship; under an open
    // column an ordinary roof (D6). Real jobs throughout (the game's remove tool, blueprints built by colonists).
    //   Fixtures: a three-storey wooden house (walls round a 7 x 7 ground room and on floors 2 and 3, slabs on floors 2 and
    //   3 over it, a built roof over floor 3, a slab walkway east of it on floor 2 with nothing over it, ordinary stairs up
    //   to the walkway and a door into floor 2), and the shipfly ship (TestGravLand.BuildShip, 13 x 11, grav anchor) with
    //   ship decks over all of it on floor 2, a hull ring there, a built roof over floor 2 and ship stairs inside.
    //   Y1 colonists take up a 3 x 3 of the house's middle floor (floor 2, under the slab of floor 3), a 2 x 2 of the
    //      walkway (open above) and a 2 x 3 of the ship's deck (under the ship's roof):
    //      (a) house: the cells below get the stairwell roof, the room stays indoors, nothing drawn there, the emergency
    //          ladder through the hole may be placed; (c) walkway: an ordinary roof stays below, drawn;
    //      (b) ship: as the house.
    //   Y1L colonists build an emergency ladder down through the house's hole and the ship's hole (real jobs).
    //   Y1R (d) colonists lay floor 2 again over one column of the house's hole: the floor's roof below again.
    //   Y1S (f) save and reload: roofs and ladders kept.
    //   Y2 the ship flies to a neighbouring tile and lands (shipfly's flight): the ladder and its column; the atrium under
    //      two holes one over the other under a kept floor-4 deck lands as one (its cover flies with the ship); the old
    //      hole (3D hull, issue 5): its decks are still gone, the ship's roof over it flew with the ship, every cell under
    //      it has the atrium roof, the rooms above and below are closed, a second ladder may drop into it; no roof is left
    //      hanging at the old site.
    //   Y3 on the landed ship colonists take up another 2 x 3 of the deck and build a ladder down through it.
    //   V  airtight in vacuum (the same ship built on an orbit map): V0 sealed, V1 the deck taken up under the ship's roof
    //      (no exposure, vacuum 0, temperatures held), V2 the roof over one hole cell removed: the expected leak.
    //      (-projectzyavacuum: V alone.)
    //   (e, a Build roof area over the atrium: mode atrium B1, whose atrium is now a floor taken up.)
    public static partial class TestRunner
    {
        private static IntVec3 yaN;
        private static readonly List<IntVec3> yaShipHole1 = new List<IntVec3>(), yaShipHole2 = new List<IntVec3>();
        private static readonly List<IntVec3> yaOffs1 = new List<IntVec3>();
        private static readonly List<string> yaHoleRoofs = new List<string>();   // the roofs over the hole right before the launch, in yaOffs1's order
        private static IntVec3 yaLadderOff1, yaSecondOff1, yaLadder3, yaFoot3;
        private static IntVec3 yaKeptOff;

        // the floor-2 deck under the open middle of the deck tower (x 6, z 7): taken up, a hole under a hole under a kept deck
        private static IntVec3 YaKeptHole(Map map) => SFF(6, 7, 1, map);
        private static Rot4 yaFacing3;

        private static IntVec3 YN(int dx, int dz) => new IntVec3(yaN.x + dx, 0, yaN.z + dz);

        private static IntVec3 YNL(int dx, int dz, int k, Map map) => ProjectZ.CellOnFloor(YN(dx, dz), k, map);

        private static string YaRoof(IntVec3 c, Map map) => c.IsValid && c.InBounds(map) ? map.roofGrid.RoofAt(c)?.defName ?? "none" : "invalid";

        private static string YaRoofs(IEnumerable<IntVec3> cells, Map map) => string.Join(", ", cells.Select(c => YaRoof(c, map)).GroupBy(s => s).Select(g => $"{g.Key} x{g.Count()}"));

        private static IEnumerable<IntVec3> YaBelow(IEnumerable<IntVec3> cells, Map map) => cells.Select(c => FloorRoof.Below(c, map));

        private static bool YaAll(IEnumerable<IntVec3> cells, Map map, RoofDef d) => cells.All(c => map.roofGrid.RoofAt(c) == d);

        // (IsoRim's own answer: the roof is not drawn)
        private static bool YaHidden(IntVec3 c, Map map) => IsoRimBridge.Present && IsoRim.Api.RoofHidden(map.roofGrid.RoofAt(c));

        private static List<IntVec3> YaRect(int x0, int z0, int w, int h, int k, Map map)
        {
            List<IntVec3> l = new List<IntVec3>();
            for (int x = x0; x < x0 + w; x++)
            {
                for (int z = z0; z < z0 + h; z++)
                {
                    l.Add(YNL(x, z, k, map));
                }
            }
            return l;
        }

        // the house: the hole in its middle floor (floor 2 over the ground room), the walkway's (nothing over it), the
        // column laid again
        private static List<IntVec3> YaHouseHole(Map map) => YaRect(2, 2, 3, 3, 1, map);

        private static List<IntVec3> YaWalkHole(Map map) => YaRect(9, 0, 2, 2, 1, map);

        private static List<IntVec3> YaRelaid(Map map) => YaRect(4, 2, 1, 3, 1, map);

        private static IntVec3 YaHouseTop(Map map) => YNL(1, 3, 1, map);

        private static IntVec3 YaShipTop(Map map) => SFF(7, 2, 1, map);

        private static Building_SiegeLadder YaLadderAt(IntVec3 top, Map map) => top.IsValid && top.InBounds(map) ? top.GetFirstThing<Building_SiegeLadder>(map) : null;

        // The ladder at top: the player's, linked down to foot.
        private static bool YaLadderOk(IntVec3 top, IntVec3 foot, Map map, out string note)
        {
            Building_SiegeLadder b = YaLadderAt(top, map);
            StairGrid g = StairGrid.For(map);
            int i = g == null || b == null ? -1 : g.IndexOf(b);
            VLink l = i < 0 ? default : g.links[i];
            note = $"{b?.def.defName ?? "no ladder"} at {top} faction {b?.Faction}, link {(i < 0 ? "none" : l.bottom + " -> " + l.top + " span " + l.span)} (foot {foot})";
            return b != null && b.Faction == Faction.OfPlayer && i >= 0 && l.top == top && l.bottom == foot;
        }

        private static void StepShipRoofAtrium(Map map)
        {
            if (stage.StartsWith("ya_v_"))
            {
                StepShipRoofAtriumVacuum();
                return;
            }
            switch (stage)
            {
                case "ya_setup":
                    YaSetup(map);
                    return;
                case "ya_plan":
                    YaPlan(map, YaHouseHole(map).Concat(YaWalkHole(map)).Concat(yaShipHole1).Concat(new[] { YaKeptHole(map) }).ToList(), "ya_removed");
                    return;
                case "ya_removed":
                    YaRemoved(map);
                    return;
                case "ya_ladders":
                    YaLadders(map);
                    return;
                case "ya_relaid":
                    YaRelaidCheck(map);
                    return;
                case "ya_loaded":
                    YaLoaded(map);
                    return;
                case "ya_launch":
                    YaLaunch(map);
                    return;
                case "ya_landed":
                    YaLanded();
                    return;
                case "ya_plan2":
                    YaPlan(SfNew(), yaShipHole2, "ya_removed2");
                    return;
                case "ya_removed2":
                    YaRemoved2();
                    return;
                case "ya_ladder3":
                    YaLadder3();
                    return;
                case "ya_end":
                    Finish();
                    return;
            }
        }

        // The two-level ship at o: TestGravLand.BuildShip (13 x 11), ship decks over all of it on floor 2, a hull ring
        // there, a built roof over its floor-2 room, ship stairs inside (x 1, z 6..9, north: the exit deck at (1, 9) on
        // floor 2).
        private static Building_GravEngine YaShip(Map map, IntVec3 o)
        {
            BuildShip(map, o, 13, 11);
            Building_GravEngine e = GlEngine(map);
            e.nameHidden = false;
            ThingDef hull = DefDatabase<ThingDef>.GetNamed("GravshipHull");
            for (int x = 0; x < 13; x++)
            {
                for (int z = 0; z < 11; z++)
                {
                    map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(o + new IntVec3(x, 0, z), 1, map), PZDefOf.PZ_ShipDeck);
                }
            }
            for (int x = 0; x < 13; x++)
            {
                for (int z = 0; z < 11; z++)
                {
                    IntVec3 u = ProjectZ.CellOnFloor(o + new IntVec3(x, 0, z), 1, map);
                    if (x == 0 || z == 0 || x == 12 || z == 10)
                    {
                        Place(hull, ThingDefOf.Steel, u, map);
                    }
                    else
                    {
                        map.roofGrid.SetRoof(u, RoofDefOf.RoofConstructed);
                    }
                }
            }
            Place(PZDefOf.PZ_ShipStairs, ThingDefOf.Steel, o + new IntVec3(1, 0, 7), map, Rot4.North);
            e.ForceSubstructureDirty();
            return e;
        }

        private static void YaSetup(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || an.floors < 4 || !ModsConfig.OdysseyActive || PZDefOf.PZ_ShipDeck == null || PZDefOf.PZ_ShipStairs == null)
            {
                Result("shiproofatrium setup", false, $"needs 4 floors and Odyssey; floors {an?.floors}");
                Finish();
                return;
            }
            if (GenCommandLine.CommandLineArgPassed("projectzyavacuum"))
            {
                // (-projectzyavacuum: only the vacuum part, V; for one-off runs with other gravship mods loaded)
                DebugSettings.enableRandomMentalStates = false;
                Find.Storyteller.storytellerComps.Clear();
                Find.PlaySettings.autoRebuild = false;
                Wait(30, "ya_v_setup");
                return;
            }
            const int W = 60, H = 24;
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
                Result("shiproofatrium field", false, "map too small");
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
            foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
            {
                a.Destroy();
            }
            DebugSettings.enableRandomMentalStates = false;
            Find.Storyteller.storytellerComps.Clear();
            Find.PlaySettings.autoRebuild = false;
            NoAmbushes(map);
            Find.ResearchManager.gravEngineInspected = true;
            ResearchProjectDef grav = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("BasicGravtech");
            if (grav != null && !grav.IsFinished)
            {
                Find.ResearchManager.FinishProject(grav, doCompletionDialog: false);
            }
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            sfHomeTile = map.Tile;
            // the ship: decks over all of it on floor 2, a hull ring there, a built roof over its floor-2 room, ship stairs
            // inside (x 1, z 6..9, north: the exit deck at (1, 9) on floor 2)
            sfO = new IntVec3(found.minX + 8, 0, found.minZ + 6);
            Building_GravEngine e = YaShip(map, sfO);
            // a tower of decks over the ship's middle (x 5..7, z 6..8): a ring on floor 3 with its middle open, a full patch
            // on floor 4. The open middle is a ship hole under the kept floor-4 deck (ShipColumn.Hole); the floor-2 deck
            // under it is taken up by colonists (Y1): two holes one over the other, the lower one under the ship's own
            // roof (no floor's roof), which flies with the ship as an opening's roof (ShipStairs.TakeOpeningRoofs) and is
            // put back after the ground's roofs (Y2: the landing keeps the atrium whatever that order)
            for (int x = 5; x <= 7; x++)
            {
                for (int z = 6; z <= 8; z++)
                {
                    if (x != 6 || z != 7)
                    {
                        map.terrainGrid.SetTerrain(SFF(x, z, 2, map), PZDefOf.PZ_ShipDeck);
                    }
                    map.terrainGrid.SetTerrain(SFF(x, z, 3, map), PZDefOf.PZ_ShipDeck);
                }
            }
            e.ForceSubstructureDirty();
            // (2 wide: every deck in it keeps a deck beside it to be taken up from, ShipDeck.WorkSpot)
            yaShipHole1.Clear();
            for (int x = 8; x <= 9; x++)
            {
                for (int z = 1; z <= 3; z++)
                {
                    yaShipHole1.Add(SFF(x, z, 1, map));
                }
            }
            // the house: walls round x 0..6, z 0..6 on all three floors, slabs on floors 2 and 3 over it (floor 2 also a
            // walkway east, x 7..12), a door on floor 2, stairs from the east up to the walkway, a built roof over floor 3
            yaN = sfO + new IntVec3(26, 0, 2);
            CellRect ring = new CellRect(yaN.x, yaN.z, 7, 7);
            foreach (IntVec3 g in ring.EdgeCells)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, g, map);
            }
            foreach (IntVec3 g in ring.Cells.Concat(new CellRect(yaN.x + 7, yaN.z, 6, 7).Cells))
            {
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(g, 1, map), PZDefOf.PZ_UpperFloor);
            }
            Place(ThingDefOf.Door, ThingDefOf.WoodLog, YNL(6, 3, 1, map), map);
            foreach (IntVec3 g in ring.EdgeCells)
            {
                IntVec3 u = ProjectZ.CellOnFloor(g, 1, map);
                if (u.GetEdifice(map) == null)
                {
                    Place(ThingDefOf.Wall, ThingDefOf.WoodLog, u, map);
                }
            }
            foreach (IntVec3 g in ring.Cells)
            {
                map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(g, 2, map), PZDefOf.PZ_UpperFloor);
            }
            foreach (IntVec3 g in ring.EdgeCells)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, ProjectZ.CellOnFloor(g, 2, map), map);
            }
            foreach (IntVec3 g in ring.Cells)
            {
                map.roofGrid.SetRoof(ProjectZ.CellOnFloor(g, 2, map), RoofDefOf.RoofConstructed);
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, YN(14, 3), map, Rot4.West);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            map.areaManager.BuildRoof.Clear();
            map.areaManager.NoRoof.Clear();
            while (cols.Count < 3)
            {
                Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(p, SF(-4, -4), map);
                cols.Add(p);
            }
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], SF(16, -3 + i));
                Draft(cols[i], true);
            }
            Info($"field {found}, ship SW {sfO}, engine {GlCell(e.Position, map)}, set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)}), house SW {yaN}, ground rows {an.groundZ}, stride {an.stride}, floors {an.floors}");
            Info($"before: house ground under the hole {YaRoofs(YaBelow(YaHouseHole(map), map), map)}, its floor-2 roofs {YaRoofs(YaHouseHole(map), map)}, floor-3 {YaRoofs(YaHouseHole(map).Select(c => FloorRoof.Above(c, map)), map)}; ground under the walkway's hole {YaRoofs(YaBelow(YaWalkHole(map), map), map)}; ship ground under the hole {YaRoofs(YaBelow(yaShipHole1, map), map)}; ship stairs ramp roofs {YaRoofs(new[] { SF(1, 6), SF(1, 7), SF(1, 8) }, map)}");
            Wait(30, "ya_plan");
        }

        private static void YaPlan(Map map, List<IntVec3> cells, string next)
        {
            Designator_RemoveFoundation rf = AllDesignators().OfType<Designator_RemoveFoundation>().FirstOrDefault() ?? new Designator_RemoveFoundation();
            FloorView.Set(1);
            int ok = 0;
            List<string> refused = new List<string>();
            foreach (IntVec3 c in cells)
            {
                AcceptanceReport a = rf.CanDesignateCell(c);
                if (a.Accepted)
                {
                    rf.DesignateSingleCell(c);
                    ok++;
                }
                else
                {
                    refused.Add($"{c} '{a.Reason}'");
                }
            }
            FloorView.Set(0);
            Result($"{next}: the remove tool takes every cell", refused.Count == 0, $"{ok} of {cells.Count}" + (refused.Count > 0 ? "; refused " + string.Join(", ", refused) : ""));
            foreach (Pawn p in Colonists(map))
            {
                Draft(p, false);
            }
            SetWork(map, true);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            SeedTicks(next);
            TickUntil(40000, () =>
            {
                R2Feed(map);
                YvTrack(map);
                return cells.All(c => !ProjectZ.HasFloor(c, map));
            }, next, 60);
        }

        // A hole taken up under a covered column: the cells under it have the stairwell roof (an atrium), the room there is
        // indoors, nothing is drawn there, and the emergency ladder from 'top' (facing into the hole) may be placed.
        private static void YaAtrium(string tag, Map map, List<IntVec3> hole, IntVec3 top, Rot4 facing)
        {
            List<IntVec3> below = YaBelow(hole, map).ToList();
            int gone = hole.Count(c => !ProjectZ.HasFloor(c, map));
            Info($"{tag}: floors gone {gone}/{hole.Count}; roofs on the hole's own floor {YaRoofs(hole, map)}; roofs below {YaRoofs(below, map)}; above {YaRoofs(hole.Select(c => FloorRoof.Above(c, map)), map)}; covered {below.Count(c => Atrium.Covered(c, map))}/{below.Count}, open above {below.Count(c => Column.OpenAbove(c, map))}, hidden {below.Count(c => YaHidden(c, map))}");
            Room room = below[0].GetRoom(map);
            string roomStr = room == null ? "none" : $"{room.CellCount} cells, open roof {room.OpenRoofCount}, outdoor temperature {room.UsesOutdoorTemperature}, psychologically outdoors {room.PsychologicallyOutdoors}";
            AcceptanceReport rep = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_EmergencyLadder, top, facing, map);
            bool site = LadderSite.Find(top, facing, map, out IntVec3 foot, out int span, out string why, true, null);
            Info($"{tag}: emergency ladder at {top} f{ProjectZ.FloorOf(top, map) + 1} facing {facing.ToStringHuman()}: {GlRep(rep)}; site {(site ? $"ok foot {foot} span {span}" : why)}");
            Result($"{tag}: the floor taken up under a covered column: the cells below have the stairwell roof, the room is indoors, nothing drawn there",
                gone == hole.Count && YaAll(below, map, PZDefOf.PZ_StairwellRoof) && room != null && room.OpenRoofCount == 0 && !room.UsesOutdoorTemperature && below.All(c => YaHidden(c, map) && Column.OpenAbove(c, map)),
                $"floors gone {gone}/{hole.Count}; roofs below {YaRoofs(below, map)}; hidden {below.Count(c => YaHidden(c, map))}, open above {below.Count(c => Column.OpenAbove(c, map))}; room {roomStr}");
            Result($"{tag}: the emergency ladder may drop through the hole", rep.Accepted, GlRep(rep) + (site ? "" : $" (site: {why})"));
        }

        // (c) a floor taken up under an open column: an ordinary roof stays below, drawn.
        private static void YaOpenColumn(string tag, Map map, List<IntVec3> hole)
        {
            List<IntVec3> below = YaBelow(hole, map).ToList();
            int gone = hole.Count(c => !ProjectZ.HasFloor(c, map));
            Room room = below[0].GetRoom(map);
            Result($"{tag}: the floor taken up under an open column: an ordinary roof below, drawn",
                gone == hole.Count && YaAll(below, map, RoofDefOf.RoofConstructed) && below.All(c => !YaHidden(c, map) && !Column.OpenAbove(c, map)),
                $"floors gone {gone}/{hole.Count}; roofs on the hole's floor {YaRoofs(hole, map)}; roofs below {YaRoofs(below, map)}; covered {below.Count(c => Atrium.Covered(c, map))}; hidden {below.Count(c => YaHidden(c, map))}; room below {room?.CellCount} cells, open roof {room?.OpenRoofCount}");
        }

        private static IntVec3 YaFoot(IntVec3 top, Rot4 facing, Map map)
        {
            return LadderSite.Find(top, facing, map, out IntVec3 foot, out _, out _, true, null) ? foot : IntVec3.Invalid;
        }

        private static void YaRemoved(Map map)
        {
            tickWatch = null;
            SetWork(map, false);
            YaAtrium("Y1 house (a)", map, YaHouseHole(map), YaHouseTop(map), Rot4.East);
            YaOpenColumn("Y1 walkway (c)", map, YaWalkHole(map));
            YaAtrium("Y1 ship (b)", map, yaShipHole1, YaShipTop(map), Rot4.East);
            Building_GravEngine e = GlEngine(map);
            IntVec3 o = e.Position;
            yaOffs1.Clear();
            yaOffs1.AddRange(yaShipHole1.Select(c => ShipLocal.From(c, o, map)));
            yaLadderOff1 = ShipLocal.From(YaShipTop(map), o, map);
            yaSecondOff1 = ShipLocal.From(SFF(10, 2, 1, map), o, map);
            IntVec3 kept = YaKeptHole(map), keptBelow = FloorRoof.Below(kept, map);
            yaKeptOff = ShipLocal.From(kept, o, map);
            Result("Y1 ship: the floor-2 deck taken up under a hole below a kept floor-4 deck: the stairwell roof below (the ship's roof left over the new hole covers it)",
                !ProjectZ.HasFloor(kept, map) && HullCompat.Hole(kept, map) && map.roofGrid.RoofAt(kept) == RoofDefOf.RoofConstructed && map.roofGrid.RoofAt(keptBelow) == PZDefOf.PZ_StairwellRoof,
                $"hole {kept}: deck gone {!ProjectZ.HasFloor(kept, map)}, ship hole {HullCompat.Hole(kept, map)}, roof {YaRoof(kept, map)}, hole above {HullCompat.Hole(FloorRoof.Above(kept, map), map)} roof {YaRoof(FloorRoof.Above(kept, map), map)}; below {YaRoof(keptBelow, map)}");
            Info($"Y1 ship set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)}); hole cells in the set {yaShipHole1.Count(c => e.ValidSubstructure.Contains(c))}, open cells over the ship {yaShipHole1.Count(c => HullCompat.OverShip(c, map))}");
            // Y1L: the emergency ladders, built by colonists
            foreach ((IntVec3 top, Rot4 rot) in new[] { (YaHouseTop(map), Rot4.East), (YaShipTop(map), Rot4.East) })
            {
                if (GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_EmergencyLadder, top, rot, map).Accepted)
                {
                    GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_EmergencyLadder, top, map, rot, Faction.OfPlayer, null);
                }
            }
            SetWork(map, true);
            SeedTicks("ya_ladders");
            TickUntil(8000, () =>
            {
                R2Feed(map);
                return YaLadderAt(YaHouseTop(map), map) != null && YaLadderAt(YaShipTop(map), map) != null;
            }, "ya_ladders", 30);
        }

        private static void YaLadders(Map map)
        {
            tickWatch = null;
            SetWork(map, false);
            Result("Y1L house (a): a colonist builds the emergency ladder down through the hole", YaLadderOk(YaHouseTop(map), FloorRoof.Below(YNL(2, 3, 1, map), map), map, out string hn), hn);
            Result("Y1L ship (b): a colonist builds the emergency ladder down through the hole", YaLadderOk(YaShipTop(map), FloorRoof.Below(SFF(8, 2, 1, map), map), map, out string sn), sn);
            // Y1R (d): floor 2 laid again over the hole's east column (x 4), by colonists
            FloorView.Set(1);
            List<string> refused = new List<string>();
            foreach (IntVec3 c in YaRelaid(map))
            {
                AcceptanceReport a = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_UpperFloor, c, Rot4.North, map);
                if (a.Accepted)
                {
                    GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_UpperFloor, c, map, Rot4.North, Faction.OfPlayer, null);
                }
                else
                {
                    refused.Add($"{c} '{a.Reason}'");
                }
            }
            FloorView.Set(0);
            Info("Y1R the floor's blueprints: " + (refused.Count == 0 ? "all placed" : "refused " + string.Join(", ", refused)));
            Thing wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
            wood.stackCount = 40;
            GenPlace.TryPlaceThing(wood, YN(16, 0), map, ThingPlaceMode.Near);
            SetWork(map, true);
            SeedTicks("ya_relaid");
            TickUntil(20000, () =>
            {
                R2Feed(map);
                return YaRelaid(map).All(c => ProjectZ.HasFloor(c, map));
            }, "ya_relaid", 60);
        }

        private static void YaRelaidCheck(Map map)
        {
            tickWatch = null;
            SetWork(map, false);
            List<IntVec3> relaid = YaRelaid(map);
            List<IntVec3> open = YaHouseHole(map).Where(c => !relaid.Contains(c)).ToList();
            Result("Y1R house (d): colonists lay floor 2 again over one column of the hole: the floor's roof below it again, the rest stays the atrium",
                relaid.All(c => ProjectZ.HasFloor(c, map)) && YaAll(YaBelow(relaid, map), map, PZDefOf.PZ_FloorRoof) && YaAll(YaBelow(open, map), map, PZDefOf.PZ_StairwellRoof),
                $"laid {relaid.Count(c => ProjectZ.HasFloor(c, map))}/{relaid.Count}, below them {YaRoofs(YaBelow(relaid, map), map)}; below the rest {YaRoofs(YaBelow(open, map), map)}");
            // Y1S (f): save and reload
            GameDataSaveLoader.SaveGame("pz_shiproofatrium");
            stage = "ya_loaded";
            pendingLoad = "pz_shiproofatrium";
        }

        private static void YaLoaded(Map map)
        {
            List<IntVec3> relaid = YaRelaid(map);
            List<IntVec3> open = YaHouseHole(map).Where(c => !relaid.Contains(c)).ToList();
            bool hl = YaLadderOk(YaHouseTop(map), FloorRoof.Below(YNL(2, 3, 1, map), map), map, out string hn);
            bool sl = YaLadderOk(YaShipTop(map), FloorRoof.Below(SFF(8, 2, 1, map), map), map, out string sn);
            Result("Y1S save and reload (f): the atrium roofs, the floor's roofs, the walkway's ordinary roofs and both ladders kept",
                YaAll(YaBelow(open, map), map, PZDefOf.PZ_StairwellRoof) && YaAll(YaBelow(relaid, map), map, PZDefOf.PZ_FloorRoof) && YaAll(YaBelow(YaWalkHole(map), map), map, RoofDefOf.RoofConstructed)
                && YaAll(YaBelow(yaShipHole1, map), map, PZDefOf.PZ_StairwellRoof) && hl && sl,
                $"house atrium {YaRoofs(YaBelow(open, map), map)}, relaid {YaRoofs(YaBelow(relaid, map), map)}, walkway {YaRoofs(YaBelow(YaWalkHole(map), map), map)}, ship {YaRoofs(YaBelow(yaShipHole1, map), map)}; {hn}; {sn}");
            GlShot(1, SF(6, 5), 16f, "ya_home_f2");
            GlShot(0, SF(6, 5), 16f, "ya_home_ground");
            GlShot(1, YN(3, 3), 12f, "ya_house_f2");
            GlShot(0, YN(3, 3), 12f, "ya_house_ground");
            // Y2: everyone aboard, the flight
            yaHoleRoofs.Clear();
            yaHoleRoofs.AddRange(yaShipHole1.Select(c => YaRoof(c, map)));
            Info($"Y2 before the launch: roofs over the ship's hole {YaRoofs(yaShipHole1, map)}, under it {YaRoofs(YaBelow(yaShipHole1, map), map)}");
            List<Pawn> cols = Colonists(map);
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], i == 0 ? SF(2, 2) : SF(9 + i % 2, 7));
                Draft(cols[i], true);
            }
            GlRun("ya_launch");
        }

        private static void YaLaunch(Map map)
        {
            List<PlanetTile> near = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(map.Tile, near);
            PlanetTile dest = near.FirstOrDefault(t => Find.WorldObjects.MapParentAt(t) == null && !Find.WorldGrid[t].WaterCovered && Find.WorldGrid[t].hilliness != Hilliness.Impassable);
            if (!dest.Valid)
            {
                Result("Y2 a neighbouring tile to fly to", false, "none");
                Finish();
                return;
            }
            sfNewTile = dest;
            sfLeg = 1;
            sfSaved = true;
            sfLandedStage = "ya_landed";
            SfLaunch(map, GlEngine(map), dest);
        }

        private static void YaLanded()
        {
            Map map = SfNew();
            Building_GravEngine e = map == null ? null : GlEngine(map);
            if (e == null)
            {
                Result("Y2 landed", false, "no engine on " + map);
                Finish();
                return;
            }
            Map home = SfHome();
            Info($"Y2 landed on {map} tile {map.Tile}: engine {GlCell(e.Position, map)}, root {sfRoot}, rotation {sfRot}, set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, home ?? map)})");
            List<IntVec3> hole = yaOffs1.Select(off => SfAt(off, sfRoot, sfRot, map)).ToList();
            List<IntVec3> below = YaBelow(hole, map).ToList();
            IntVec3 top = SfAt(yaLadderOff1, sfRoot, sfRot, map);
            Rot4 east = new Rot4((Rot4.East.AsInt + sfRot.AsInt) % 4), west = new Rot4((Rot4.West.AsInt + sfRot.AsInt) % 4);
            IntVec3 colBelow = FloorRoof.Below(Levels.Cell(Levels.Ground(top, map) + east.FacingCell, Levels.LevelOf(top, map), map), map);
            Info($"Y2 ship after the flight: roofs on the hole's own floor {YaRoofs(hole, map)}; roofs below {YaRoofs(below, map)}; covered {below.Count(c => Atrium.Covered(c, map))}/{below.Count}, open above {below.Count(c => Column.OpenAbove(c, map))}, hidden {below.Count(c => YaHidden(c, map))}");
            AcceptanceReport second = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_EmergencyLadder, SfAt(yaSecondOff1, sfRoot, sfRot, map), west, map);
            Result("Y2 ship after the flight: a second emergency ladder may drop into the old hole from its east side (the hole is still an atrium under the ship's roof)", second.Accepted, GlRep(second));
            // (3D hull, issue 5, design #13 and #15: the roof over a hole in the ship is the ship's and flies with it, and the
            // hidden atrium roof under the hole is put back as such. Before: the roof over the hole was lost in flight, 5
            // of the 6 cells under it landed under an ordinary roof, and this check only asked for "the roof the column
            // rule gives now".)
            int stillGone = hole.Count(c => !ProjectZ.HasFloor(c, map));
            // (kept: roofed, and the roof it had right before the launch: the ship's built roof, the ladder's own cell its own)
            int roofKept = Enumerable.Range(0, hole.Count).Count(i => map.roofGrid.Roofed(hole[i]) && i < yaHoleRoofs.Count && YaRoof(hole[i], map) == yaHoleRoofs[i]);
            int atrium = below.Count(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_StairwellRoof);
            Room roomBelow = below[0].GetRoom(map), roomAbove = hole[0].GetRoom(map);
            Result("Y2 ship after the flight: the roof over the old hole flew with the ship (none lost: every hole cell roofed as before the launch), every cell under the hole has the atrium roof, the decks taken up are still gone, the rooms above and below are closed (no open roof cell)",
                stillGone == hole.Count && roofKept == hole.Count && atrium == below.Count && roomBelow != null && roomBelow.OpenRoofCount == 0 && !roomBelow.UsesOutdoorTemperature
                && roomAbove != null && roomAbove != roomBelow && roomAbove.OpenRoofCount == 0 && !roomAbove.UsesOutdoorTemperature,
                $"decks gone {stillGone}/{hole.Count}; the roof over the hole kept {roofKept}/{hole.Count}, lost {hole.Count(c => !map.roofGrid.Roofed(c))} (now {YaRoofs(hole, map)}; before the launch {string.Join(", ", yaHoleRoofs.GroupBy(s => s).Select(g => $"{g.Key} x{g.Count()}"))}); atrium roof below {atrium}/{below.Count} ({YaRoofs(below, map)}; covered {below.Count(c => Atrium.Covered(c, map))}, ladder column {below.Count(c => Stairwell.Well(c, map))}); room below {(roomBelow == null ? "none" : $"{roomBelow.CellCount} cells, open roof {roomBelow.OpenRoofCount}, outdoor temperature {roomBelow.UsesOutdoorTemperature}")}; room above {(roomAbove == null ? "none" : $"{roomAbove.CellCount} cells, open roof {roomAbove.OpenRoofCount}, outdoor temperature {roomAbove.UsesOutdoorTemperature}")}");
            Result("Y2 ship after the flight: the ladder flew with the ship and still links down through the hole, its own column the stairwell roof",
                YaLadderOk(top, colBelow, map, out string note) && map.roofGrid.RoofAt(colBelow) == PZDefOf.PZ_StairwellRoof,
                $"{note}; roof in its column {YaRoof(colBelow, map)}");
            // (the game puts the roofs back in no order of floors, the ground's before this cover; the decks laid first give
            // the column its roofs from the top down, Atrium.Recheck, so the atrium roof lands as one)
            IntVec3 kept = SfAt(yaKeptOff, sfRoot, sfRot, map), keptBelow = FloorRoof.Below(kept, map);
            Result("Y2 ship after the flight: the atrium under the two holes below the kept floor-4 deck landed as one (the stairwell roof under the roof over the lower hole, which the game puts back after it)",
                HullCompat.Hole(kept, map) && map.roofGrid.RoofAt(kept) == RoofDefOf.RoofConstructed && map.roofGrid.RoofAt(keptBelow) == PZDefOf.PZ_StairwellRoof && Atrium.Covered(keptBelow, map),
                $"hole {kept}: ship hole {HullCompat.Hole(kept, map)}, roof {YaRoof(kept, map)}, hole above {HullCompat.Hole(FloorRoof.Above(kept, map), map)} roof {YaRoof(FloorRoof.Above(kept, map), map)}; below {YaRoof(keptBelow, map)}");
            // (nothing is left hanging at the old site: the roof over the old hole went with the ship, the ground under it is open)
            Result("Y2 the old site: no roof left where the hole was, on floor 2 or on the ground under it",
                home != null && yaShipHole1.All(c => home.roofGrid.RoofAt(c) == null) && YaBelow(yaShipHole1, home).All(c => home.roofGrid.RoofAt(c) == null),
                home == null ? "the old map is gone" : $"the old hole's floor-2 roofs {YaRoofs(yaShipHole1, home)}, ground {YaRoofs(YaBelow(yaShipHole1, home), home)}");
            // Y3: another 2 x 3 of the deck (ship offsets x 2..3, z 1..3), a ladder from x 4 facing west
            yaShipHole2.Clear();
            for (int x = 2; x <= 3; x++)
            {
                for (int z = 1; z <= 3; z++)
                {
                    yaShipHole2.Add(SfAt(new IntVec3(x - 6, 1, z - 5), sfRoot, sfRot, map));
                }
            }
            yaLadder3 = SfAt(new IntVec3(4 - 6, 1, 2 - 5), sfRoot, sfRot, map);
            yaFacing3 = west;
            Info($"Y3 hole cells {string.Join(" ", yaShipHole2)} decks {yaShipHole2.Count(c => ShipDeck.At(c, map))}");
            Current.Game.CurrentMap = map;
            stage = "ya_plan2";
        }

        private static void YaRemoved2()
        {
            Map map = SfNew();
            tickWatch = null;
            SetWork(map, false);
            YaAtrium("Y3 ship after the flight, deck taken up there (b)", map, yaShipHole2, yaLadder3, yaFacing3);
            yaFoot3 = YaFoot(yaLadder3, yaFacing3, map);
            if (GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_EmergencyLadder, yaLadder3, yaFacing3, map).Accepted)
            {
                GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_EmergencyLadder, yaLadder3, map, yaFacing3, Faction.OfPlayer, null);
            }
            SetWork(map, true);
            SeedTicks("ya_ladder3");
            TickUntil(8000, () =>
            {
                R2Feed(map);
                return YaLadderAt(yaLadder3, map) != null;
            }, "ya_ladder3", 30);
        }

        private static void YaLadder3()
        {
            Map map = SfNew();
            tickWatch = null;
            SetWork(map, false);
            Result("Y3 ship after the flight (b): a colonist builds the emergency ladder down through the new hole", YaLadderOk(yaLadder3, yaFoot3, map, out string note), note);
            Building_GravEngine e = GlEngine(map);
            GlShot(1, ProjectZ.GroundCellOf(e.Position, map), 16f, "ya_landed_f2");
            GlShot(0, ProjectZ.GroundCellOf(e.Position, map), 16f, "ya_landed_ground");
            glAfter = "ya_v_setup";
            Wait(2, "gl_shots");
        }

        // ---------------------------------------------------------------------------------------------- V: airtight in vacuum
        // (user 2026-11-14: the atrium roof must stay airtight.) The same two-level ship built on an orbit map (mode space's
        // relay map), its door walled up, the colonists inside in vacsuits, both rooms at 21 C with air.
        //   V0 decks whole: both rooms closed (not exposed, vacuum 0);
        //   V1 colonists take up a 2 x 3 of the deck under the ship's roof: the stairwell roof below; over the removal and
        //      1250 ticks after it neither room is exposed, the vacuum stays 0, the temperatures are held;
        //   V2 a colonist removes the ship's roof over one hole cell (Remove roof area): that column opens to space (the
        //      atrium rule), both rooms are exposed: the expected leak.
        private static Map yvMap;
        private static IntVec3 yvO, yvCover;
        private static readonly List<IntVec3> yvHole = new List<IntVec3>();
        private static float yvMaxLow, yvMaxUp, yvT0Low, yvT0Up;
        private static bool yvExposedSeen;

        // A room "holds its temperature": over the 1250 ticks after the removal it keeps at least this share of its
        // difference to the outdoor temperature (walls and roofs leak a little in a -75 C vacuum, hole or not).
        private const float YvHeldShare = 0.9f;
        private const float YvRoomTemp = 21f;

        private static IntVec3 YV(int dx, int dz) => new IntVec3(yvO.x + dx, 0, yvO.z + dz);

        private static IntVec3 YVF(int dx, int dz, int k, Map map) => ProjectZ.CellOnFloor(YV(dx, dz), k, map);

        private static Room YvLow(Map map) => YV(5, 2).GetRoom(map);

        private static Room YvUp(Map map) => YVF(5, 2, 1, map).GetRoom(map);

        private static string YvRoom(Room r) => r == null ? "none" : $"room {r.ID} cells {r.CellCount} exposed {r.ExposedToSpace} vacuum {r.Vacuum:0.00} outdoor-temp {r.UsesOutdoorTemperature} {r.Temperature:0.0} C";

        // (every watched tick on the orbit map: the highest vacuum of the two rooms, and whether either was exposed)
        private static void YvTrack(Map map)
        {
            if (map != yvMap || yvMap == null)
            {
                return;
            }
            Room low = YvLow(map), up = YvUp(map);
            if (low != null)
            {
                yvMaxLow = UnityEngine.Mathf.Max(yvMaxLow, low.Vacuum);
                yvExposedSeen |= low.ExposedToSpace;
            }
            if (up != null)
            {
                yvMaxUp = UnityEngine.Mathf.Max(yvMaxUp, up.Vacuum);
                yvExposedSeen |= up.ExposedToSpace;
            }
        }

        private static bool YvHeld(Room r, float t0, Map map)
        {
            float outdoor = map.mapTemperature.OutdoorTemp;
            return r != null && !r.UsesOutdoorTemperature && r.Temperature - outdoor >= YvHeldShare * (t0 - outdoor);
        }

        private static void StepShipRoofAtriumVacuum()
        {
            Map map = yvMap;
            switch (stage)
            {
                case "ya_v_setup":
                    yvMap = OrbitMap();
                    if (yvMap == null)
                    {
                        Result("V an orbit tile", false, "no free orbit tile");
                        Finish();
                        return;
                    }
                    Wait(30, "ya_v_build");
                    return;
                case "ya_v_build":
                    YvBuild(map);
                    return;
                case "ya_v_base":
                {
                    tickWatch = null;
                    Room low = YvLow(map), up = YvUp(map);
                    Result("V0 the sealed ship in vacuum, decks whole: both rooms closed (not exposed, vacuum 0)",
                        low != null && up != null && low != up && !yvExposedSeen && yvMaxLow == 0f && yvMaxUp == 0f && !low.UsesOutdoorTemperature && !up.UsesOutdoorTemperature,
                        $"below: {YvRoom(low)}; above: {YvRoom(up)}; highest vacuum {yvMaxLow:0.00} / {yvMaxUp:0.00}; outdoors {map.mapTemperature.OutdoorTemp:0.0} C; ground under the hole {YaRoofs(YaBelow(yvHole, map), map)}, over it {YaRoofs(yvHole, map)}");
                    YaPlan(map, yvHole, "ya_v_removed");
                    return;
                }
                case "ya_v_removed":
                    tickWatch = null;
                    SetWork(map, false);
                    yvT0Low = YvLow(map)?.Temperature ?? 0f;
                    yvT0Up = YvUp(map)?.Temperature ?? 0f;
                    Info($"V1 decks gone {yvHole.Count(c => !ProjectZ.HasFloor(c, map))}/{yvHole.Count} at tick {Find.TickManager.TicksGame}; below: {YvRoom(YvLow(map))}; above: {YvRoom(YvUp(map))}");
                    SeedTicks("ya_v_held");
                    TickUntil(1250, () =>
                    {
                        R2Feed(map);
                        YvTrack(map);
                        return false;
                    }, "ya_v_held", 10);
                    return;
                case "ya_v_held":
                {
                    tickWatch = null;
                    Room low = YvLow(map), up = YvUp(map);
                    List<IntVec3> below = YaBelow(yvHole, map).ToList();
                    int gone = yvHole.Count(c => !ProjectZ.HasFloor(c, map));
                    Result("V1 ship in vacuum: the deck taken up under the ship's roof: the cells below have the stairwell roof",
                        gone == yvHole.Count && YaAll(below, map, PZDefOf.PZ_StairwellRoof), $"decks gone {gone}/{yvHole.Count}; roofs below {YaRoofs(below, map)}; over the hole {YaRoofs(yvHole, map)}");
                    Result("V1 airtight: over the removal and 1250 ticks after it neither room is exposed to space, the vacuum stays 0, the temperatures are held (the room above holds the hole's open-air cells)",
                        gone == yvHole.Count && low != null && up != null && low != up && !yvExposedSeen && !low.ExposedToSpace && !up.ExposedToSpace && yvMaxLow == 0f && yvMaxUp == 0f
                        && YvHeld(low, yvT0Low, map) && YvHeld(up, yvT0Up, map) && yvHole.All(c => c.GetRoom(map) == up) && below.All(c => c.GetRoom(map) == low),
                        $"below: {YvRoom(low)} (1250 ticks before {yvT0Low:0.0} C); above: {YvRoom(up)} (1250 ticks before {yvT0Up:0.0} C); highest vacuum {yvMaxLow:0.00} / {yvMaxUp:0.00}, exposed at any watched tick {yvExposedSeen}; outdoors {map.mapTemperature.OutdoorTemp:0.0} C; hole cells in the room above {yvHole.Count(c => c.GetRoom(map) == up)}/{yvHole.Count}, cells under it in the room below {below.Count(c => c.GetRoom(map) == low)}/{below.Count}");
                    // V2: the cover over one hole cell removed by a colonist
                    map.areaManager.BuildRoof[yvCover] = false;
                    map.areaManager.NoRoof[yvCover] = true;
                    SetWork(map, true);
                    SeedTicks("ya_v_open");
                    TickUntil(8000, () =>
                    {
                        R2Feed(map);
                        return !map.roofGrid.Roofed(yvCover);
                    }, "ya_v_open", 10);
                    return;
                }
                case "ya_v_open":
                    tickWatch = null;
                    SetWork(map, false);
                    SeedTicks("ya_v_leak");
                    StartTicks(250, "ya_v_leak");
                    return;
                case "ya_v_leak":
                {
                    Room low = YvLow(map), up = YvUp(map);
                    IntVec3 under = FloorRoof.Below(yvCover, map);
                    List<IntVec3> rest = YaBelow(yvHole, map).Where(c => c != under).ToList();
                    Result("V2 a colonist removes the ship's roof over one hole cell (Remove roof area, real job): that column opens to space, both rooms are exposed (the expected leak)",
                        !map.roofGrid.Roofed(yvCover) && !map.roofGrid.Roofed(under) && YaAll(rest, map, PZDefOf.PZ_StairwellRoof)
                        && low != null && up != null && low.ExposedToSpace && up.ExposedToSpace && low.Vacuum == 1f && up.Vacuum == 1f,
                        $"cover {YaRoof(yvCover, map)}, under it {YaRoof(under, map)}, the rest below {YaRoofs(rest, map)}; below: {YvRoom(low)}; above: {YvRoom(up)}; outdoors {map.mapTemperature.OutdoorTemp:0.0} C");
                    GlShot(1, YV(6, 5), 16f, "ya_orbit_f2");
                    GlShot(0, YV(6, 5), 16f, "ya_orbit_ground");
                    glAfter = "ya_end";
                    Wait(2, "gl_shots");
                    return;
                }
            }
        }

        private static void YvBuild(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            TerrainDef space = TerrainDefOf.Space;
            TerrainDef plat = DefDatabase<TerrainDef>.GetNamed("OrbitalPlatform");
            int gz = an == null ? 0 : ProjectZ.GroundZ(map);
            // (a 36 x 16 platform: the ship 13 x 13 with its thrusters, and its grav anchor 8 cells east of it)
            CellRect best = CellRect.Empty;
            for (int z = 6; z + 24 < gz - 6 && best.IsEmpty; z += 2)
            {
                for (int x = 6; x + 44 < map.Size.x - 6; x += 2)
                {
                    CellRect r = new CellRect(x, z, 44, 24);
                    if (r.Cells.All(c => c.GetTerrain(map) == space && c.GetFirstThing<Thing>(map) == null))
                    {
                        best = r;
                        break;
                    }
                }
            }
            if (an == null || an.floors < 2 || best.IsEmpty)
            {
                Result("V open space for the ship on the orbit map", false, $"floors {an?.floors}, rect found {!best.IsEmpty}");
                Finish();
                return;
            }
            CellRect pl = best.ContractedBy(4);
            foreach (IntVec3 c in pl)
            {
                map.terrainGrid.SetTerrain(c, plat);
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
            yvO = new IntVec3(pl.minX + 3, 0, pl.minZ + 3);
            Building_GravEngine e = YaShip(map, yvO);
            // (no door: nobody lets the air out)
            IntVec3 door = YV(6, 10);
            door.GetEdifice(map)?.Destroy();
            Place(DefDatabase<ThingDef>.GetNamed("GravshipHull"), ThingDefOf.Steel, door, map);
            e.ForceSubstructureDirty();
            yvHole.Clear();
            for (int x = 8; x <= 9; x++)
            {
                for (int z = 1; z <= 3; z++)
                {
                    yvHole.Add(YVF(x, z, 1, map));
                }
            }
            yvCover = YVF(8, 2, 1, map);
            OrbitColonists(map, i => YV(2 + i % 4, 2 + i / 4));
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            map.areaManager.BuildRoof.Clear();
            map.areaManager.NoRoof.Clear();
            foreach (Room r in new[] { YvLow(map), YvUp(map) })
            {
                if (r != null)
                {
                    r.Temperature = YvRoomTemp;
                    r.Vacuum = 0f;
                }
            }
            yvMaxLow = 0f;
            yvMaxUp = 0f;
            yvExposedSeen = false;
            Info($"V orbit ship SW {yvO} on {map} (vacuum biome {map.Biome.inVacuum}), engine {GlCell(e.Position, map)}, set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)}); colonists {string.Join(", ", Colonists(map).Select(p => p.LabelShort + " at " + p.Position))}; below: {YvRoom(YvLow(map))}; above: {YvRoom(YvUp(map))}");
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            SeedTicks("ya_v_base");
            TickUntil(250, () =>
            {
                R2Feed(map);
                YvTrack(map);
                return false;
            }, "ya_v_base", 10);
        }
    }
}
