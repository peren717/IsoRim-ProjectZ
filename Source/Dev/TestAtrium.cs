using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode atrium (player request 2026-11-02, Atrium.cs): a roof taken away under a covered column, or a floor taken up
    // there (FloorRoof.Freed, user 2026-11-14), is the invisible stairwell roof (indoors, no rain, indoor temperature, seen
    // into from above); the column's cover gone: open sky.
    // Run with -projectzfloors=3 -projectzseed=7. Roof collapse ON. Fixtures (ground coords in the cleared area):
    //   main: a 3-storey building, walls round x 10..16, z 10..16 on every level (closed rings), slabs on levels 1 and 2,
    //         a built roof over level 2. The atrium H = x 12..14, z 12..14.
    //   cave: walls round x 24..30, z 10..16 on the ground and on level 1, a slab on level 1, a thick rock roof over
    //         level 1 (the cover); H2 = x 26..28, z 12..14.
    //   A1 a colonist takes up the level-1 slab over H (real job, the middle floor under the level-2 slab): the ground
    //      under it the stairwell roof at once (an atrium, the column open: Column.OpenAbove), the ground room indoors (no
    //      open roof cells, indoor temperature), nothing drawn there
    //   A3 a no-roof area marked on H: no roof-removal job for 1500 ticks, the roofs stay
    //   A4 rain: a colonist standing in H gets no soaking-wet thought; A5 the room keeps its own temperature
    //   shots: from floors 1, 2 and 3
    //   S1 save/load: the atrium stays; a cell written back to "no roof" behind the rules (an older save) is put right; a
    //      floor's roof left with no floor over it (an older save) follows FloorRoof.Freed: the atrium's under the covered
    //      column, an ordinary roof under an open one (the hall's hole)
    //   B1 a Build roof area over one atrium cell: a colonist builds a real roof there (real job)
    //   R1 the level-2 slab over H taken up (real job): the level-1 cells under it the stairwell roof (the level-2 roof
    //      covers them), the ground stays an atrium
    //   R2 the B1 roof on the ground removed (Remove roof area, real job): the stairwell roof again (a roof taken away
    //      under a covered column)
    //   R3 the level-2 roof over H removed (real job): the whole column open to the sky, the ground room outdoor temperature
    //   R4 rain falls in: a colonist in H gets soaking wet
    //   C1 cave: the level-1 slab taken up under the thick roof (real jobs): the ground under it the stairwell roof at
    //      once, the room indoors
    //   CX cave: a slab broken by an explosion under the thick roof collapses as ever (a stool under it takes the crush
    //      damage), the cell the atrium's afterwards
    //   SL a slab that falls silently (two walled rooms, the ground roofed but for a light well north, east and south of
    //      the middle, a slab on level 1 over the middle only, its roof hanging on the one roof cell west of it): a
    //      colonist removes that roof cell (no-roof area, real job), the game takes the slab's roof away as unsupported
    //      and the slab falls, nothing crushed. SL1 under a roof on level 1 (written by the fixture: with real supports a
    //      cover's own holder would hang on the same strip): the cell is the atrium's; SL2 under the open sky: no roof
    //   ST stairs with their own landing under a roofed upstairs room, deconstructed by a colonist: the three ramp cells
    //      and the cell under the landing all an ordinary roof again (Stairwell.Closed)
    //   N  a ground room the colonists build (walls and a door from blueprints) and the game roofs (its own Build-roof
    //      area, never cleared); floors laid over it (fixture). N1 the middle floor taken up: the atrium stays, no
    //      constructor roofs it over again; N2 a Build-roof area marked afterwards on three of its cells: real roofs
    //      (real jobs); N3 a no-roof area on those three: the atrium's roof again (Remove roof under a covered column)
    public static partial class TestRunner
    {
        private static CellRect atRing, atH, atCave, atH2, atHall, atH3, atRow;
        private static bool atLoop;
        private static IntVec3 atOdd, atOrphan, atOrphanOpen, atCx;
        private static Thing atCxStool;
        private static int atCxCollapses;
        private static CellRect atSlA, atSlB, atSt, atHome, atHomeH, atHomeRow;
        private static IntVec3 atStStairs;
        private static Thing atSlStoolA, atSlStoolB;
        private static int atSlCollapses, atHomeBuilt;

        // SL: a walled 7 x 7 room with a door south; the ground roofed but for the cells north, east and south of its middle
        // (a no-roof area, as a player leaves a light well); a slab on level 1 over the middle only, so its roof hangs on
        // the roof cell west of the middle. covered: a roof over that slab on level 1.
        private static void AtSilentFixture(Map map, CellRect ring, bool covered)
        {
            IntVec3 g = ring.CenterCell;
            IntVec3 door = new IntVec3(g.x, 0, ring.minZ);
            foreach (IntVec3 e in ring.EdgeCells)
            {
                if (e == door)
                {
                    Place(ThingDefOf.Door, ThingDefOf.WoodLog, e, map);
                }
                else
                {
                    R2Wall(map, e);
                }
            }
            foreach (IntVec3 c in ring.ContractedBy(1))
            {
                if (c == g + IntVec3.North || c == g + IntVec3.East || c == g + IntVec3.South)
                {
                    map.areaManager.NoRoof[c] = true;
                }
                else
                {
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                }
            }
            map.terrainGrid.SetTerrain(Levels.Cell(g, 1, map), PZDefOf.PZ_UpperFloor);
            if (covered)
            {
                map.roofGrid.SetRoof(Levels.Cell(g, 1, map), RoofDefOf.RoofConstructed);
            }
        }

        private static string AtSilentStr(Map map, CellRect ring, Thing stool)
        {
            IntVec3 g = ring.CenterCell, u = Levels.Cell(g, 1, map);
            Room r = g.GetRoom(map);
            return $"slab {map.terrainGrid.TerrainAt(u)?.defName}, roof over it {map.roofGrid.RoofAt(u)?.defName ?? "none"}, roof under it {map.roofGrid.RoofAt(g)?.defName ?? "none"} (covered {Atrium.Covered(g, map)}), strip {map.roofGrid.RoofAt(g + IntVec3.West)?.defName ?? "none"}; room cells {r?.CellCount}, open roof {r?.OpenRoofCount}, outdoor temperature {r?.UsesOutdoorTemperature}; stool {(stool == null ? "none" : stool.Destroyed ? "destroyed" : stool.HitPoints + "/" + stool.MaxHitPoints)}";
        }

        // ST: a walled 7 x 8 two-storey room (a door on the ground), roofed on the ground and over level 1, a slab on level 1
        // but for the four cells over the stairs: stairs inside, up to the north, laying their own landing.
        private static Building_Stairs AtStairsFixture(Map map)
        {
            CellRect ramp = StairShape.RampRect(atStStairs, Rot4.North);
            IntVec3 solid = StairShape.Solid(atStStairs, Rot4.North);
            IntVec3 door = new IntVec3(atSt.minX, 0, atSt.minZ + 3);
            foreach (IntVec3 e in atSt.EdgeCells)
            {
                if (e == door)
                {
                    Place(ThingDefOf.Door, ThingDefOf.WoodLog, e, map);
                }
                else
                {
                    R2Wall(map, e);
                }
            }
            foreach (IntVec3 g in atSt)
            {
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                if (!ramp.Contains(g) && g != solid)
                {
                    map.terrainGrid.SetTerrain(Levels.Cell(g, 1, map), PZDefOf.PZ_UpperFloor);
                }
            }
            foreach (IntVec3 e in atSt.EdgeCells)
            {
                R2Wall(map, Levels.Cell(e, 1, map));
            }
            foreach (IntVec3 g in atSt)
            {
                map.roofGrid.SetRoof(Levels.Cell(g, 1, map), RoofDefOf.RoofConstructed);
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            return R2Stairs(map, atStStairs, Rot4.North);
        }

        private static IEnumerable<IntVec3> AtStairCells() => StairShape.RampRect(atStStairs, Rot4.North).Cells.Concat(new[] { StairShape.Solid(atStStairs, Rot4.North) });
        private static string atNote;

        private static bool StepAtrium(Map map)
        {
            Rand.PushState(FixtureSeed("atrium_" + stage));
            try
            {
                return StepAtriumInner(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static IEnumerable<IntVec3> AtCells(CellRect r, int L, Map map) => r.Cells.Select(g => Levels.Cell(g, L, map));

        private static int AtCount(CellRect r, int L, Map map, RoofDef d) => AtCells(r, L, map).Count(c => map.roofGrid.RoofAt(c) == d);

        private static string AtRoofs(CellRect r, int L, Map map) => string.Join(",", AtCells(r, L, map).Select(c => map.roofGrid.RoofAt(c)?.defName ?? "none").GroupBy(s => s).Select(g => $"{g.Key} x{g.Count()}"));

        private static void AtDesignateSlabs(Map map, CellRect r, int L)
        {
            Designator_RemoveFoundation tool = new Designator_RemoveFoundation();
            int ok = 0;
            string why = "";
            foreach (IntVec3 c in AtCells(r, L, map))
            {
                AcceptanceReport a = tool.CanDesignateCell(c);
                if (a.Accepted)
                {
                    tool.DesignateSingleCell(c);
                    ok++;
                }
                else
                {
                    why = a.Reason;
                }
            }
            atNote = $"designated {ok} of {r.Area} slab cells on level {L}{(why.NullOrEmpty() ? "" : " (refused: " + why + ")")}";
            Info("atrium: " + atNote);
        }

        private static void AtWork(Pawn p, IntVec3 at)
        {
            Teleport(p, at);
            Draft(p, false);
            R2Work(p, WorkTypeDefOf.Construction);
        }

        private static void AtNoRoof(Map map, CellRect r, int L, bool on)
        {
            foreach (IntVec3 c in AtCells(r, L, map))
            {
                map.areaManager.NoRoof[c] = on;
                if (on)
                {
                    map.areaManager.BuildRoof[c] = false;   // (as the no-roof designator does)
                }
            }
        }

        private static Room AtRoom(Map map) => RG(11, 11).GetRoom(map);

        private static string AtRoomStr(Map map)
        {
            Room r = AtRoom(map);
            return r == null ? "no room" : $"room cells {r.CellCount}, open roof {r.OpenRoofCount}, outdoor temperature {r.UsesOutdoorTemperature}, psychologically outdoors {r.PsychologicallyOutdoors}, {r.Temperature:0.0} C (outdoors {map.mapTemperature.OutdoorTemp:0.0} C)";
        }

        private static void AtRain(Map map)
        {
            WeatherDef rain = DefDatabase<WeatherDef>.GetNamed("Rain");
            map.weatherManager.curWeather = rain;
            map.weatherManager.lastWeather = rain;
            map.weatherManager.curWeatherAge = 0;
        }

        private static void AtUntil(Map map, int max, System.Func<bool> cond, string next)
        {
            TickUntil(max, () =>
            {
                R2Feed(map);
                return cond();
            }, next, 30);
        }

        private static bool AtWet(Pawn p) => p.needs?.mood?.thoughts?.memories?.GetFirstMemoryOfDef(ThoughtDefOf.SoakingWet) != null;

        private static void AtDry(Pawn p) => p.needs?.mood?.thoughts?.memories?.RemoveMemoriesOfDef(ThoughtDefOf.SoakingWet);

        private static bool StepAtriumInner(Map map)
        {
            switch (stage)
            {
                case "at_setup":
                {
                    DebugSettings.enableRandomMentalStates = false;
                    Find.Storyteller.storytellerComps.Clear();
                    Find.PlaySettings.autoRebuild = false;
                    NoAmbushes(map);
                    foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
                    {
                        a.Destroy();
                    }
                    if (Levels.Max(map) < 2)
                    {
                        Result("atrium: a map with 3 levels", false, $"levels {Levels.Min(map)}..{Levels.Max(map)} (run with -projectzfloors=3)");
                        Finish();
                        return true;
                    }
                    r2Area = new CellRect(map.Center.x - 22, map.Center.z - 16, 44, 32);
                    List<Pawn> cols = Colonists(map);
                    r2P = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) ?? cols.First();
                    r2Q = cols.FirstOrDefault(p => p != r2P);
                    foreach (Pawn p in cols)
                    {
                        p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
                    }
                    R2Clear(map);
                    R2Park(map, r2P);
                    R2Feed(map);
                    atRing = new CellRect(RG(10, 10).x, RG(10, 10).z, 7, 7);
                    atH = new CellRect(RG(12, 12).x, RG(12, 12).z, 3, 3);
                    atCave = new CellRect(RG(30, 10).x, RG(30, 10).z, 7, 7);
                    atH2 = new CellRect(RG(32, 12).x, RG(32, 12).z, 3, 3);
                    // main: three storeys, a closed ground room, doors on levels 1 and 2 to ledges reached by stairs (the
                    // slabs' peel order starts at stairs), a built roof on top
                    foreach (IntVec3 g in atRing.EdgeCells)
                    {
                        R2Wall(map, g);
                    }
                    R2Floor(map, atRing, 1);
                    R2Floor(map, new CellRect(RG(17, 10).x, RG(17, 10).z, 6, 7), 1);
                    Place(ThingDefOf.Door, ThingDefOf.WoodLog, RL(16, 13, 1, map), map);
                    foreach (IntVec3 g in atRing.EdgeCells)
                    {
                        R2Wall(map, Levels.Cell(g, 1, map));
                    }
                    R2Floor(map, atRing, 2);
                    R2Floor(map, new CellRect(RG(17, 14).x, RG(17, 14).z, 3, 2), 2);
                    Place(ThingDefOf.Door, ThingDefOf.WoodLog, RL(16, 14, 2, map), map);
                    foreach (IntVec3 g in atRing.EdgeCells)
                    {
                        R2Wall(map, Levels.Cell(g, 2, map));
                    }
                    foreach (IntVec3 c in AtCells(atRing, 2, map))
                    {
                        map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                    }
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    Building_Stairs st1 = R2Stairs(map, RG(24, 13), Rot4.West);
                    Building_Stairs st2 = R2Stairs(map, RL(21, 15, 1, map), Rot4.West);
                    // cave: two storeys under a thick rock roof, stairs from the west
                    foreach (IntVec3 g in atCave.EdgeCells)
                    {
                        R2Wall(map, g);
                    }
                    R2Floor(map, atCave, 1);
                    R2Floor(map, new CellRect(RG(29, 12).x, RG(29, 12).z, 1, 3), 1);
                    Place(ThingDefOf.Door, ThingDefOf.WoodLog, RL(30, 13, 1, map), map);
                    foreach (IntVec3 g in atCave.EdgeCells)
                    {
                        R2Wall(map, Levels.Cell(g, 1, map));
                    }
                    foreach (IntVec3 c in AtCells(atCave, 1, map))
                    {
                        map.roofGrid.SetRoof(c, RoofDefOf.RoofRockThick);
                    }
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    Building_Stairs st3 = R2Stairs(map, RG(27, 13), Rot4.East);
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    // SL: the two rooms of the silent fall (under a roof, under the open sky). (Before the hall: the rooms made
                    // last get the game's roof area after the Clear below, and the hall's top-floor check builds on it.)
                    atSlA = new CellRect(RG(0, 10).x, RG(0, 10).z, 7, 7);
                    atSlB = new CellRect(RG(0, 20).x, RG(0, 20).z, 7, 7);
                    AtSilentFixture(map, atSlA, true);
                    AtSilentFixture(map, atSlB, false);
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    // hall: three storeys with an open double-height hole (no ground roof, no slabs over it on levels 1
                    // and 2, no roof on top); walls round x 10..16, z 20..26 on every level
                    atHall = new CellRect(RG(10, 20).x, RG(10, 20).z, 7, 7);
                    atH3 = new CellRect(RG(12, 22).x, RG(12, 22).z, 3, 3);
                    foreach (IntVec3 g in atHall.EdgeCells)
                    {
                        R2Wall(map, g);
                    }
                    for (int L = 1; L <= 2; L++)
                    {
                        foreach (IntVec3 g in atHall.Cells.Where(g => !atH3.Contains(g)))
                        {
                            R2Floor(map, CellRect.SingleCell(g), L);
                        }
                        foreach (IntVec3 g in atHall.EdgeCells)
                        {
                            R2Wall(map, Levels.Cell(g, L, map));
                        }
                    }
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    map.areaManager.BuildRoof.Clear();   // (left by the game while the fixture was built)
                    Info($"atrium: silent-fall rooms {atSlA} ({AtSilentStr(map, atSlA, null)}) and {atSlB} ({AtSilentStr(map, atSlB, null)})");
                    Info($"atrium: stairs {st1?.Position} (exit {(st1 == null ? IntVec3.Invalid : StairShape.Exit(st1.Position, st1.Rotation, map))}), {st2?.Position} (exit {(st2 == null ? IntVec3.Invalid : StairShape.Exit(st2.Position, st2.Rotation, map))}), cave {st3?.Position} (exit {(st3 == null ? IntVec3.Invalid : StairShape.Exit(st3.Position, st3.Rotation, map))})");
                    Info($"atrium: area {r2Area}, worker {r2P}, stander {r2Q}; ground H roofs {AtRoofs(atH, 0, map)}, level-1 {AtRoofs(atH, 1, map)}, level-2 {AtRoofs(atH, 2, map)}; cave level-1 roofs {AtRoofs(atH2, 1, map)}; {AtRoomStr(map)}");
                    if (r2Q != null)
                    {
                        Teleport(r2Q, R2ParkCell(map, 5));
                        Draft(r2Q, true);
                    }
                    AtDesignateSlabs(map, atH, 1);
                    AtWork(r2P, RL(11, 11, 1, map));
                    SeedTicks("at_slab1");
                    AtUntil(map, 20000, () => AtCells(atH, 1, map).All(c => !ProjectZ.HasFloor(c, map)), "at_slab1");
                    return true;
                }
                case "at_slab1":
                {
                    tickWatch = null;
                    int gone = AtCells(atH, 1, map).Count(c => !ProjectZ.HasFloor(c, map));
                    Room room1 = AtRoom(map);
                    int hidden = AtCells(atH, 0, map).Count(c => IsoRimBridge.Present && IsoRim.Api.RoofHidden(map.roofGrid.RoofAt(c)));
                    IntVec3 mid = RG(13, 13);
                    Result("A1 a colonist takes up the level-1 slab over the 3x3 (real jobs; the middle floor, the level-2 slab over it): the ground under it the stairwell roof at once (an atrium, the column open), the room indoors, nothing drawn there",
                        gone == 9 && AtCount(atH, 0, map, PZDefOf.PZ_StairwellRoof) == 9 && room1 != null && room1.OpenRoofCount == 0 && !room1.UsesOutdoorTemperature && !room1.PsychologicallyOutdoors && hidden == 9
                        && Column.OpenAbove(mid, map) && Atrium.Is(mid, map),
                        $"{atNote}; slabs gone {gone}/9; ground roofs {AtRoofs(atH, 0, map)} (hidden {hidden}); level-1 roofs {AtRoofs(atH, 1, map)}; {AtRoomStr(map)}; open above {Column.OpenAbove(mid, map)}, atrium {Atrium.Is(mid, map)}");
                    // A3/A4/A5: a no-roof area marked on the atrium stays (no removal job); rain; the room warmer than outdoors
                    AtNoRoof(map, atH, 0, true);
                    AtWork(r2P, RG(11, 11));
                    Room room = room1;
                    AtRain(map);
                    if (room != null)
                    {
                        room.Temperature = map.mapTemperature.OutdoorTemp + 20f;
                    }
                    atNote = $"room set to {room?.Temperature:0.0} C, outdoors {map.mapTemperature.OutdoorTemp:0.0} C";
                    if (r2Q != null)
                    {
                        Teleport(r2Q, mid);
                        Draft(r2Q, true);
                        AtDry(r2Q);
                    }
                    atLoop = false;
                    SeedTicks("at_wait");
                    TickUntil(1500, () =>
                    {
                        AtRain(map);
                        atLoop |= r2P.CurJobDef == JobDefOf.RemoveRoof;
                        return false;
                    }, "at_wait", 10);
                    return true;
                }
                case "at_wait":
                {
                    tickWatch = null;
                    int noRoof = AtCells(atH, 0, map).Count(c => map.areaManager.NoRoof[c]);
                    bool hasJob = AtCells(atH, 0, map).Any(c => new WorkGiver_RemoveRoof().HasJobOnCell(r2P, c));
                    Result("A3 the no-roof area left on the atrium: no roof-removal job in 1500 ticks, the roofs stay",
                        noRoof == 9 && !atLoop && !hasJob && AtCount(atH, 0, map, PZDefOf.PZ_StairwellRoof) == 9,
                        $"no-roof cells {noRoof}, removal job seen {atLoop}, job offered {hasJob}, ground roofs {AtRoofs(atH, 0, map)}, worker job {r2P.CurJobDef?.defName}");
                    if (r2Q != null)
                    {
                        Result("A4 rain: a colonist standing in the atrium gets no soaking-wet thought (the cell counts as roofed)",
                            map.weatherManager.CurWeatherLerped.rainRate > 0f && !AtWet(r2Q) && r2Q.Position.Roofed(map),
                            $"weather {map.weatherManager.CurWeatherLerped.defName} (rain {map.weatherManager.RainRate:0.00}), stander at {r2Q.Position} roofed {r2Q.Position.Roofed(map)}, soaking wet {AtWet(r2Q)}");
                    }
                    Room room = AtRoom(map);
                    Result("A5 the atrium room keeps its own temperature (not the outdoor one)",
                        room != null && !room.UsesOutdoorTemperature && room.Temperature - map.mapTemperature.OutdoorTemp > 10f, $"{atNote}; now {AtRoomStr(map)}");
                    float glowIn = map.glowGrid.GroundGlowAt(RG(13, 13)), glowOut = map.glowGrid.GroundGlowAt(RG(20, 5)), sky = map.skyManager.CurSkyGlow;
                    Result("A6 no sun under the atrium (plants grow as indoors): its ground glow below the open ground's", glowOut - glowIn > 0.2f, $"ground glow in the atrium {glowIn:0.00}, outside {glowOut:0.00}, sky {sky:0.00}");
                    if (r2Q != null)
                    {
                        Teleport(r2Q, R2ParkCell(map, 5));
                    }
                    Teleport(r2P, R2ParkCell(map, 6));
                    Draft(r2P, true);
                    ShotAt(map, 2, RG(13, 13), 7f);
                    Wait(5, "at_shot3");
                    return true;
                }
                case "at_shot3":
                    Capture("atrium_floor3");
                    ShotAt(map, 1, RG(13, 13), 7f);
                    Wait(5, "at_shot2");
                    return true;
                case "at_shot2":
                    Capture("atrium_floor2");
                    ShotAt(map, 0, RG(13, 13), 7f);
                    Wait(5, "at_shot1");
                    return true;
                case "at_shot1":
                    Capture("atrium_floor1");
                    Wait(5, "at_save");
                    return true;
                case "at_save":
                {
                    // an older save: "no roof" written straight into the grid under the covered column
                    IntVec3 old = RG(12, 12);
                    map.roofGrid.RemoveRoofUnsafe(map.cellIndices.CellToIndex(old));
                    // and a floor's roof with no floor over it (left by an older version): under the covered column (the
                    // atrium), and under the hall's open hole
                    RoofDef[] grid = AccessTools.FieldRefAccess<RoofGrid, RoofDef[]>("roofGrid")(map.roofGrid);
                    atOrphan = RG(14, 14);
                    atOrphanOpen = RG(12, 22);
                    grid[map.cellIndices.CellToIndex(atOrphan)] = PZDefOf.PZ_FloorRoof;
                    grid[map.cellIndices.CellToIndex(atOrphanOpen)] = PZDefOf.PZ_FloorRoof;
                    atNote = $"raw roofs before saving: {old} {map.roofGrid.RoofAt(old)?.defName ?? "none"}, {atOrphan} {map.roofGrid.RoofAt(atOrphan)?.defName ?? "none"} (covered {Atrium.Covered(atOrphan, map)}), {atOrphanOpen} {map.roofGrid.RoofAt(atOrphanOpen)?.defName ?? "none"} (covered {Atrium.Covered(atOrphanOpen, map)})";
                    GameDataSaveLoader.SaveGame("pz_atrium");
                    stage = "at_loaded";
                    pendingLoad = "pz_atrium";
                    return true;
                }
                case "at_loaded":
                {
                    r2P = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.ThingID == r2P?.ThingID) ?? r2P;
                    r2Q = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.ThingID == r2Q?.ThingID) ?? r2Q;
                    Room room = AtRoom(map);
                    Result("S1 save/load: the atrium stays (stairwell roofs, room indoors); the cell saved with no roof and the floor's roof saved with no floor over it are put right at load (the atrium's roof); the floor's roof saved under the open hole an ordinary roof",
                        AtCount(atH, 0, map, PZDefOf.PZ_StairwellRoof) == 9 && room != null && room.OpenRoofCount == 0 && !room.UsesOutdoorTemperature
                        && map.roofGrid.RoofAt(atOrphanOpen) == RoofDefOf.RoofConstructed,
                        $"{atNote}; after loading ground roofs {AtRoofs(atH, 0, map)}, {atOrphanOpen} {map.roofGrid.RoofAt(atOrphanOpen)?.defName ?? "none"}; {AtRoomStr(map)}");
                    map.roofGrid.SetRoof(atOrphanOpen, null);   // (the hall's hole open again for the HB checks)
                    // B1: a Build roof area over one atrium cell
                    IntVec3 b = RG(13, 13);
                    map.areaManager.NoRoof[b] = false;
                    map.areaManager.BuildRoof[b] = true;
                    AtWork(r2P, RG(11, 11));
                    SeedTicks("at_build");
                    AtUntil(map, 8000, () => map.roofGrid.RoofAt(b) == RoofDefOf.RoofConstructed, "at_built");
                    return true;
                }
                case "at_built":
                {
                    tickWatch = null;
                    IntVec3 b = RG(13, 13);
                    Result("B1 a Build roof area over an atrium cell: a colonist builds a real roof there (real job), the other cells stay the atrium",
                        map.roofGrid.RoofAt(b) == RoofDefOf.RoofConstructed && AtCount(atH, 0, map, PZDefOf.PZ_StairwellRoof) == 8,
                        $"roof at {b} {map.roofGrid.RoofAt(b)?.defName ?? "none"}; ground roofs {AtRoofs(atH, 0, map)}; built by the patch {BuildRoofEffect_Atrium_Patch.Built}");
                    map.areaManager.BuildRoof[b] = false;
                    AtDesignateSlabs(map, atH, 2);
                    AtWork(r2P, RL(11, 11, 2, map));
                    SeedTicks("at_slab2");
                    AtUntil(map, 20000, () => AtCells(atH, 2, map).All(c => !ProjectZ.HasFloor(c, map)), "at_slab2");
                    return true;
                }
                case "at_slab2":
                {
                    tickWatch = null;
                    int gone = AtCells(atH, 2, map).Count(c => !ProjectZ.HasFloor(c, map));
                    Result("R1 the level-2 slab over the 3x3 taken up (real jobs): the level-1 cells under it the stairwell roof (the level-2 roof covers them), the ground stays the atrium",
                        gone == 9 && AtCount(atH, 1, map, PZDefOf.PZ_StairwellRoof) == 9 && AtCount(atH, 0, map, PZDefOf.PZ_StairwellRoof) == 8,
                        $"{atNote}; slabs gone {gone}/9; level-1 roofs {AtRoofs(atH, 1, map)}; ground roofs {AtRoofs(atH, 0, map)}");
                    // R2: the roof B1 built on the ground taken away again (a roof removed under a covered column)
                    IntVec3 b = RG(13, 13);
                    map.areaManager.BuildRoof[b] = false;
                    map.areaManager.NoRoof[b] = true;
                    AtWork(r2P, RG(11, 11));
                    SeedTicks("at_roof1");
                    AtUntil(map, 15000, () => map.roofGrid.RoofAt(b) != RoofDefOf.RoofConstructed, "at_roof1");
                    return true;
                }
                case "at_roof1":
                {
                    tickWatch = null;
                    Result("R2 a colonist removes the B1 roof on the ground (Remove roof area, real job): the stairwell roof again (the level-1 atrium covers it), the level-1 cells unchanged",
                        AtCount(atH, 0, map, PZDefOf.PZ_StairwellRoof) == 9 && AtCount(atH, 1, map, PZDefOf.PZ_StairwellRoof) == 9,
                        $"level-1 roofs {AtRoofs(atH, 1, map)}; ground roofs {AtRoofs(atH, 0, map)}; covers {Atrium.Covers}");
                    AtNoRoof(map, atH, 2, true);
                    AtWork(r2P, RL(11, 11, 2, map));
                    SeedTicks("at_roof2");
                    AtUntil(map, 15000, () => AtCount(atH, 2, map, RoofDefOf.RoofConstructed) == 0, "at_roof2");
                    return true;
                }
                case "at_roof2":
                {
                    tickWatch = null;
                    Room room = AtRoom(map);
                    Result("R3 the level-2 roof over the 3x3 removed (real jobs): the whole column open to the sky, the ground room uses the outdoor temperature",
                        AtCount(atH, 2, map, null) == 9 && AtCount(atH, 1, map, null) == 9 && AtCount(atH, 0, map, null) == 9
                        && room != null && room.OpenRoofCount == 9 && room.UsesOutdoorTemperature,
                        $"level-2 roofs {AtRoofs(atH, 2, map)}; level-1 {AtRoofs(atH, 1, map)}; ground {AtRoofs(atH, 0, map)}; {AtRoomStr(map)}; uncovers {Atrium.Uncovers}");
                    Teleport(r2P, R2ParkCell(map, 6));
                    Draft(r2P, true);
                    AtRain(map);
                    if (r2Q != null)
                    {
                        Teleport(r2Q, RG(12, 12));
                        Draft(r2Q, true);
                        AtDry(r2Q);
                    }
                    SeedTicks("at_wet");
                    TickUntil(1200, () =>
                    {
                        AtRain(map);
                        return r2Q == null || AtWet(r2Q);
                    }, "at_wet", 10);
                    return true;
                }
                case "at_wet":
                {
                    tickWatch = null;
                    if (r2Q != null)
                    {
                        Result("R4 rain falls in: a colonist standing in the reopened column gets soaking wet", AtWet(r2Q) && !r2Q.Position.Roofed(map),
                            $"stander at {r2Q.Position} roofed {r2Q.Position.Roofed(map)}, soaking wet {AtWet(r2Q)}");
                        Teleport(r2Q, R2ParkCell(map, 5));
                    }
                    ShotAt(map, 2, RG(13, 13), 7f);
                    Wait(5, "at_shotsky");
                    return true;
                }
                case "at_shotsky":
                    Capture("atrium_sky_floor3");
                    // C1: the cave
                    AtDesignateSlabs(map, atH2, 1);
                    AtWork(r2P, RL(31, 11, 1, map));
                    SeedTicks("at_cave1");
                    AtUntil(map, 20000, () => AtCells(atH2, 1, map).All(c => !ProjectZ.HasFloor(c, map)), "at_cave1");
                    return true;
                case "at_cave1":
                {
                    tickWatch = null;
                    IntVec3 pc = RL(33, 13, 1, map);
                    WorkGiver_ConstructRemoveFoundation wg = new WorkGiver_ConstructRemoveFoundation();
                    wg.def = DefDatabase<WorkGiverDef>.AllDefs.FirstOrDefault(d => d.giverClass == typeof(WorkGiver_ConstructRemoveFoundation));
                    JobFailReason.Clear();
                    bool pj = wg.HasJobOnCell(r2P, pc);
                    atNote += $"; slabs gone {AtCells(atH2, 1, map).Count(c => !ProjectZ.HasFloor(c, map))}/9, ground roofs then {AtRoofs(atH2, 0, map)}; probe {pc}: peel distance {SlabPeel.For(map)?.Distance(pc)}, workable {SlabPeel.For(map)?.Workable(pc)}, blocker '{Slab.Blocker(pc, map)}', has job {pj} ({JobFailReason.Reason}), worker at {r2P.Position} job {r2P.CurJobDef?.defName}, links {StairGrid.For(map)?.links.Count}";
                    Room room = RG(31, 11).GetRoom(map);
                    Result("C1 cave: the level-1 slab taken up under a thick rock roof (real jobs): the ground under it the stairwell roof at once, the room indoors",
                        AtCells(atH2, 1, map).All(c => !ProjectZ.HasFloor(c, map)) && AtCount(atH2, 0, map, PZDefOf.PZ_StairwellRoof) == 9 && AtCount(atH2, 1, map, RoofDefOf.RoofRockThick) == 9
                        && room != null && room.OpenRoofCount == 0 && !room.UsesOutdoorTemperature,
                        $"{atNote}; ground roofs {AtRoofs(atH2, 0, map)}; level-1 roofs {AtRoofs(atH2, 1, map)}; room open roof {room?.OpenRoofCount}, outdoor temperature {room?.UsesOutdoorTemperature}");
                    Teleport(r2P, R2ParkCell(map, 6));
                    Draft(r2P, true);
                    ShotAt(map, 1, RG(33, 13), 7f);
                    Wait(5, "at_shotcave");
                    return true;
                }
                case "at_shotcave":
                    Capture("atrium_cave_floor2");
                    stage = "at_hb_start";
                    return true;

                // ---- building cover over an open hole (the column rule works both ways)
                case "at_hb_start":
                {
                    Room room = RG(11, 21).GetRoom(map);
                    atNote = $"hole roofs: ground {AtRoofs(atH3, 0, map)}, level-1 {AtRoofs(atH3, 1, map)}, level-2 {AtRoofs(atH3, 2, map)}; hall room open roof {room?.OpenRoofCount}, outdoor temperature {room?.UsesOutdoorTemperature}";
                    // (b) first: a Build roof area on the hole's south row on level 1
                    atRow = new CellRect(atH3.minX, atH3.minZ, 3, 1);
                    foreach (IntVec3 c in AtCells(atRow, 1, map))
                    {
                        map.areaManager.NoRoof[c] = false;
                        map.areaManager.BuildRoof[c] = true;
                    }
                    AtWork(r2P, RL(11, 21, 1, map));
                    SeedTicks("at_hb_b");
                    AtUntil(map, 10000, () => AtCount(atRow, 1, map, RoofDefOf.RoofConstructed) == 3, "at_hb_b");
                    return true;
                }
                case "at_hb_b":
                {
                    tickWatch = null;
                    Result("HB a colonist builds a roof over the floor-2 hole row (real job): the ground cells under it get the invisible roof, the rest of the hole stays open",
                        AtCount(atRow, 1, map, RoofDefOf.RoofConstructed) == 3 && AtCount(atRow, 0, map, PZDefOf.PZ_StairwellRoof) == 3 && AtCount(atH3, 0, map, null) == 6,
                        $"before: {atNote}; level-1 row {AtRoofs(atRow, 1, map)}; ground row {AtRoofs(atRow, 0, map)}; ground hole {AtRoofs(atH3, 0, map)}");
                    foreach (IntVec3 c in AtCells(atRow, 1, map))
                    {
                        map.areaManager.BuildRoof[c] = false;
                    }
                    // (a) rain falls into the open part
                    Teleport(r2P, R2ParkCell(map, 6));
                    Draft(r2P, true);
                    AtRain(map);
                    if (r2Q != null)
                    {
                        Teleport(r2Q, RG(13, 24));
                        Draft(r2Q, true);
                        AtDry(r2Q);
                    }
                    SeedTicks("at_hb_wet");
                    TickUntil(1200, () =>
                    {
                        AtRain(map);
                        return r2Q == null || AtWet(r2Q);
                    }, "at_hb_wet", 10);
                    return true;
                }
                case "at_hb_wet":
                {
                    tickWatch = null;
                    Room room = RG(11, 21).GetRoom(map);
                    if (r2Q != null)
                    {
                        Result("HA1 the open double-height hole with no roof on top: rain falls in (a colonist in it gets soaking wet; 6 open cells of 25: under the game's 25% the hall keeps an indoor temperature)",
                            AtWet(r2Q) && !r2Q.Position.Roofed(map) && room != null && room.OpenRoofCount == 6,
                            $"stander at {r2Q.Position} roofed {r2Q.Position.Roofed(map)}, soaking wet {AtWet(r2Q)}; hall open roof {room?.OpenRoofCount}, outdoor temperature {room?.UsesOutdoorTemperature}");
                        Teleport(r2Q, R2ParkCell(map, 5));
                    }
                    // a Build roof area over the whole hole on the top floor
                    foreach (IntVec3 c in AtCells(atH3, 2, map))
                    {
                        map.areaManager.NoRoof[c] = false;
                        map.areaManager.BuildRoof[c] = true;
                    }
                    AtWork(r2P, RL(11, 21, 2, map));
                    SeedTicks("at_hb_a");
                    AtUntil(map, 12000, () => AtCount(atH3, 2, map, RoofDefOf.RoofConstructed) == 9, "at_hb_a");
                    return true;
                }
                case "at_hb_a":
                {
                    tickWatch = null;
                    if (AtCount(atH3, 2, map, RoofDefOf.RoofConstructed) != 9)
                    {
                        IntVec3 pc = AtCells(atH3, 2, map).First();
                        WorkGiver_BuildRoof wg = new WorkGiver_BuildRoof();
                        JobFailReason.Clear();
                        bool has = wg.HasJobOnCell(r2P, pc);
                        Info($"HA2 diag: worker {r2P} at {r2P.Position} (level {Levels.LevelOf(r2P.Position, map)}) spawned {r2P.Spawned} downed {r2P.Downed} drafted {r2P.Drafted} job {r2P.CurJobDef?.defName} target {r2P.CurJob?.targetA}; cell {pc}: in area {map.areaManager.BuildRoof[pc]}, no-roof {map.areaManager.NoRoof[pc]}, has job {has} ({JobFailReason.Reason}), in range {RoofCollapseUtility.WithinRangeOfRoofHolder(pc, map)}, connected {RoofCollapseUtility.ConnectedToRoofHolder(pc, map, true)}, reach {r2P.CanReach(pc, PathEndMode.Touch, Danger.Deadly)}, reserve {r2P.CanReserve(pc, 1, -1, ReservationLayerDefOf.Ceiling)}; Build-roof cells on the map {map.areaManager.BuildRoof.TrueCount}: {string.Join(" ", map.areaManager.BuildRoof.ActiveCells.Take(12))}");
                    }
                    foreach (IntVec3 c in AtCells(atH3, 2, map))
                    {
                        map.areaManager.BuildRoof[c] = false;
                    }
                    Room room = RG(11, 21).GetRoom(map);
                    Result("HA2 a colonist builds the top roof over the hole (real job): the open cells below in the column get the invisible roof (level 1: 6, the ground: 9), the hall indoors",
                        AtCount(atH3, 2, map, RoofDefOf.RoofConstructed) == 9 && AtCount(atH3, 1, map, PZDefOf.PZ_StairwellRoof) == 6 && AtCount(atRow, 1, map, RoofDefOf.RoofConstructed) == 3
                        && AtCount(atH3, 0, map, PZDefOf.PZ_StairwellRoof) == 9 && room != null && room.OpenRoofCount == 0 && !room.UsesOutdoorTemperature,
                        $"level-2 {AtRoofs(atH3, 2, map)}; level-1 {AtRoofs(atH3, 1, map)}; ground {AtRoofs(atH3, 0, map)}; hall open roof {room?.OpenRoofCount}, outdoor temperature {room?.UsesOutdoorTemperature}; covers {Atrium.Covers}");
                    Teleport(r2P, R2ParkCell(map, 6));
                    Draft(r2P, true);
                    AtRain(map);
                    if (r2Q != null)
                    {
                        Teleport(r2Q, RG(13, 24));
                        Draft(r2Q, true);
                        AtDry(r2Q);
                    }
                    SeedTicks("at_hb_dry");
                    TickUntil(1200, () =>
                    {
                        AtRain(map);
                        return r2Q != null && AtWet(r2Q);
                    }, "at_hb_dry", 10);
                    return true;
                }
                case "at_hb_dry":
                {
                    tickWatch = null;
                    if (r2Q != null)
                    {
                        Result("HA3 rain no longer falls in: the colonist in the hole stays dry for 1200 rainy ticks", !AtWet(r2Q) && r2Q.Position.Roofed(map),
                            $"stander at {r2Q.Position} roofed {r2Q.Position.Roofed(map)}, soaking wet {AtWet(r2Q)}, weather {map.weatherManager.CurWeatherLerped.defName}");
                        Teleport(r2Q, R2ParkCell(map, 5));
                    }
                    ShotAt(map, 2, RG(13, 23), 7f);
                    Wait(5, "at_hb_shot");
                    return true;
                }
                case "at_hb_shot":
                {
                    Capture("atrium_built_floor3");
                    // (c) an old save: the grid as a build from before atriums left it (no roof under the new top roof; and an
                    // invisible roof with no cover over it)
                    RoofDef[] grid = AccessTools.FieldRefAccess<RoofGrid, RoofDef[]>("roofGrid")(map.roofGrid);
                    foreach (IntVec3 c in AtCells(atH3, 0, map).Concat(AtCells(atH3, 1, map).Where(x => !atRow.Contains(Levels.Ground(x, map)))))
                    {
                        grid[map.cellIndices.CellToIndex(c)] = null;
                    }
                    atOdd = RG(20, 28);
                    grid[map.cellIndices.CellToIndex(atOdd)] = PZDefOf.PZ_StairwellRoof;
                    atNote = $"raw before saving: ground {AtRoofs(atH3, 0, map)}, level-1 {AtRoofs(atH3, 1, map)}, uncovered {atOdd} {map.roofGrid.RoofAt(atOdd)?.defName ?? "none"}";
                    GameDataSaveLoader.SaveGame("pz_atrium_old");
                    stage = "at_hb_loaded";
                    pendingLoad = "pz_atrium_old";
                    return true;
                }
                case "at_hb_loaded":
                {
                    // (the colonists of the loaded game: the checks after this one use them)
                    r2P = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.ThingID == r2P?.ThingID) ?? r2P;
                    r2Q = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.ThingID == r2Q?.ThingID) ?? r2Q;
                    Room room = RG(11, 21).GetRoom(map);
                    Result("HC an old save with open sky under existing cover (and an invisible roof under none): put right at load",
                        AtCount(atH3, 0, map, PZDefOf.PZ_StairwellRoof) == 9 && AtCount(atH3, 1, map, PZDefOf.PZ_StairwellRoof) == 6 && map.roofGrid.RoofAt(atOdd) == null
                        && room != null && room.OpenRoofCount == 0 && AtCount(atH, 0, map, null) == 9,
                        $"{atNote}; after loading ground {AtRoofs(atH3, 0, map)}, level-1 {AtRoofs(atH3, 1, map)}, uncovered {map.roofGrid.RoofAt(atOdd)?.defName ?? "none"}; load fix: {Atrium.LoadCovered} covered, {Atrium.LoadOpened} opened; main atrium ground {AtRoofs(atH, 0, map)}; hall open roof {room?.OpenRoofCount}");
                    Wait(5, "at_cx");
                    return true;
                }
                case "at_cx":
                {
                    // CX: a slab broken under the cave's thick roof (a covered column) still collapses as ever (FloorRoof.Freed
                    // keeps the ordinary roof while the game resolves a collapse): a stool on the ground under it takes the falling roof's crush damage
                    atCx = RL(35, 15, 1, map);
                    IntVec3 under = RG(35, 15);
                    atCxStool = Place(DefDatabase<ThingDef>.GetNamed("Stool"), ThingDefOf.WoodLog, under, map);
                    atCxCollapses = PZStats.slabCollapses;
                    atNote = $"before: slab {map.terrainGrid.TerrainAt(atCx)?.defName}, roof over it {map.roofGrid.RoofAt(atCx)?.defName ?? "none"}, roof under it {map.roofGrid.RoofAt(under)?.defName ?? "none"} (covered {Atrium.Covered(under, map)}), stool {atCxStool?.Position}";
                    GenExplosion.DoExplosion(atCx, map, 0.9f, DamageDefOf.Bomb, null, 60);
                    SeedTicks("at_cx_done");
                    StartTicks(120, "at_cx_done");
                    return true;
                }
                case "at_cx_done":
                {
                    IntVec3 under = RG(35, 15);
                    Result("CX a slab broken by an explosion under the cave's thick roof (a covered column): it collapses as before (the roof under it drops on the stool below: crush damage), and the cell is the atrium's afterwards",
                        !ProjectZ.HasFloor(atCx, map) && atCxStool != null && (atCxStool.Destroyed || atCxStool.HitPoints < atCxStool.MaxHitPoints) && PZStats.slabCollapses == atCxCollapses + 1 && map.roofGrid.RoofAt(under) == PZDefOf.PZ_StairwellRoof,
                        $"{atNote}; after: slab {map.terrainGrid.TerrainAt(atCx)?.defName}, stool destroyed {atCxStool?.Destroyed} hit points {atCxStool?.HitPoints}/{atCxStool?.MaxHitPoints}, slab collapses {PZStats.slabCollapses - atCxCollapses}, roof under it {map.roofGrid.RoofAt(under)?.defName ?? "none"}, roof over it {map.roofGrid.RoofAt(atCx)?.defName ?? "none"}");
                    Wait(5, "at_sl");
                    return true;
                }

                // ---- SL: a slab that goes in the game's silent removal of an unsupported roof
                case "at_sl":
                {
                    foreach (CellRect ring in new[] { atSlA, atSlB })
                    {
                        IntVec3 strip = ring.CenterCell + IntVec3.West;
                        map.areaManager.BuildRoof[strip] = false;
                        map.areaManager.NoRoof[strip] = true;   // (as the no-roof designator does)
                    }
                    atSlStoolA = Place(DefDatabase<ThingDef>.GetNamed("Stool"), ThingDefOf.WoodLog, atSlA.CenterCell, map);
                    atSlStoolB = Place(DefDatabase<ThingDef>.GetNamed("Stool"), ThingDefOf.WoodLog, atSlB.CenterCell, map);
                    atSlCollapses = PZStats.slabCollapses;
                    atNote = $"before: under a roof: {AtSilentStr(map, atSlA, atSlStoolA)}; under the sky: {AtSilentStr(map, atSlB, atSlStoolB)}";
                    AtWork(r2P, atSlA.CenterCell + new IntVec3(-1, 0, -1));
                    SeedTicks("at_sl_wait");
                    AtUntil(map, 15000, () => !map.roofGrid.Roofed(atSlA.CenterCell + IntVec3.West) && !map.roofGrid.Roofed(atSlB.CenterCell + IntVec3.West), "at_sl_wait");
                    return true;
                }
                case "at_sl_wait":
                    // (250 more ticks: a roof put back must still be there after the game's next passes)
                    tickWatch = null;
                    SeedTicks("at_sl_done");
                    StartTicks(250, "at_sl_done");
                    return true;
                case "at_sl_done":
                {
                    IntVec3 ga = atSlA.CenterCell, gb = atSlB.CenterCell;
                    Room ra = ga.GetRoom(map), rb = gb.GetRoom(map);
                    bool stoolsOk = atSlStoolA != null && !atSlStoolA.Destroyed && atSlStoolA.HitPoints == atSlStoolA.MaxHitPoints && atSlStoolB != null && !atSlStoolB.Destroyed && atSlStoolB.HitPoints == atSlStoolB.MaxHitPoints;
                    Result("SL1 a colonist removes the roof cell a level-1 slab's roof hangs on (no-roof area, real job), under a roof on level 1: the slab falls silently (nothing below crushed) and the cell under it is the atrium's (the room keeps only its light well and the removed cell open)",
                        !ProjectZ.HasFloor(Levels.Cell(ga, 1, map), map) && map.roofGrid.RoofAt(ga) == PZDefOf.PZ_StairwellRoof && Atrium.Is(ga, map) && ra != null && ra.OpenRoofCount == 4 && !ra.UsesOutdoorTemperature && stoolsOk,
                        $"{atNote}; after: {AtSilentStr(map, atSlA, atSlStoolA)}; slab collapses {PZStats.slabCollapses - atSlCollapses}");
                    Result("SL2 the same under the open sky: the slab falls silently, the cell under it has no roof (as before)",
                        !ProjectZ.HasFloor(Levels.Cell(gb, 1, map), map) && !map.roofGrid.Roofed(gb) && rb != null && rb.OpenRoofCount == 5 && stoolsOk && PZStats.slabCollapses == atSlCollapses + 2,
                        $"after: {AtSilentStr(map, atSlB, atSlStoolB)}; slab collapses {PZStats.slabCollapses - atSlCollapses}");
                    Wait(5, "at_st");
                    return true;
                }

                // ---- ST: stairs with their own landing taken down under a roofed upstairs room
                case "at_st":
                {
                    // (the room is built here, not with the other fixtures: stairs into a closed upstairs room are a dead
                    // end the colonists of the earlier checks would try)
                    atSt = new CellRect(RG(30, 0).x, RG(30, 0).z, 7, 8);
                    atStStairs = RG(33, 3);
                    AtStairsFixture(map);
                    foreach (IntVec3 c in atSt)
                    {
                        // (the area the game marked for the walls just placed: this check is about the stairs)
                        map.areaManager.BuildRoof[c] = false;
                        map.areaManager.BuildRoof[Levels.Cell(c, 1, map)] = false;
                    }
                    Building_Stairs s = atStStairs.GetFirstThing<Building_Stairs>(map);
                    IntVec3 top = s == null ? IntVec3.Invalid : StairShape.Exit(s.Position, s.Rotation, map);
                    atNote = $"before: stairs {s?.Position}, landing {top} {(top.IsValid ? top.GetTerrain(map)?.defName : "-")}, roofs under the stairs {string.Join(",", AtStairCells().Select(c => map.roofGrid.RoofAt(c)?.defName ?? "none"))}, over them on level 1 {string.Join(",", AtStairCells().Select(c => map.roofGrid.RoofAt(Levels.Cell(c, 1, map))?.defName ?? "none"))}";
                    if (s != null)
                    {
                        Designator_Deconstruct dd = new Designator_Deconstruct();
                        if (dd.CanDesignateThing(s).Accepted)
                        {
                            dd.DesignateThing(s);
                        }
                    }
                    AtWork(r2P, new IntVec3(atSt.minX + 1, 0, atSt.minZ + 1));
                    SeedTicks("at_st_done");
                    AtUntil(map, 20000, () => atStStairs.GetFirstThing<Building_Stairs>(map) == null, "at_st_done");
                    return true;
                }
                case "at_st_done":
                {
                    tickWatch = null;
                    IntVec3 top = Levels.Cell(StairShape.Solid(atStStairs, Rot4.North), 1, map);
                    Result("ST stairs with their own landing under a roofed upstairs room, deconstructed by a colonist (real job): the landing goes with them, the three ramp cells and the cell under the landing all an ordinary roof again",
                        atNote.Contains("PZ_UpperFloor") && atStStairs.GetFirstThing<Building_Stairs>(map) == null && top.GetTerrain(map) == PZDefOf.PZ_OpenAir && AtStairCells().All(c => map.roofGrid.RoofAt(c) == RoofDefOf.RoofConstructed)
                        && AtStairCells().All(c => map.roofGrid.RoofAt(Levels.Cell(c, 1, map)) == RoofDefOf.RoofConstructed),
                        $"{atNote}; after: landing {top.GetTerrain(map)?.defName}, roofs under the old stairs {string.Join(",", AtStairCells().Select(c => map.roofGrid.RoofAt(c)?.defName ?? "none"))}, over them on level 1 {string.Join(",", AtStairCells().Select(c => map.roofGrid.RoofAt(Levels.Cell(c, 1, map))?.defName ?? "none"))}");
                    Wait(5, "at_n_setup");
                    return true;
                }

                // ---- N: a room the colonists build and the game roofs; the middle floor over it taken up
                case "at_n_setup":
                {
                    atHome = new CellRect(RG(22, 20).x, RG(22, 20).z, 7, 7);
                    atHomeH = new CellRect(RG(24, 22).x, RG(24, 22).z, 3, 3);
                    atHomeRow = new CellRect(atHomeH.minX, atHomeH.minZ, 3, 1);
                    IntVec3 door = RG(22, 23);
                    foreach (IntVec3 g in atHome.EdgeCells)
                    {
                        GenConstruct.PlaceBlueprintForBuild(g == door ? ThingDefOf.Door : ThingDefOf.Wall, g, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
                    }
                    for (int i = 0; i < 3; i++)
                    {
                        Thing wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                        wood.stackCount = 75;
                        GenPlace.TryPlaceThing(wood, RG(19, 22 + i), map, ThingPlaceMode.Near);
                    }
                    AtWork(r2P, RG(20, 23));
                    if (r2Q != null)
                    {
                        Teleport(r2Q, RG(20, 24));
                        Draft(r2Q, false);
                        R2Work(r2Q, WorkTypeDefOf.Construction);
                    }
                    SeedTicks("at_n_built");
                    AtUntil(map, 60000, () => atHome.EdgeCells.All(g => g.GetEdifice(map) is Building b && (b.def == ThingDefOf.Wall || b is Building_Door))
                        && atHome.ContractedBy(1).Cells.All(c => map.roofGrid.RoofAt(c) == RoofDefOf.RoofConstructed), "at_n_built");
                    return true;
                }
                case "at_n_built":
                {
                    tickWatch = null;
                    List<IntVec3> inner = atHome.ContractedBy(1).Cells.ToList();
                    int built = atHome.EdgeCells.Count(g => g.GetEdifice(map) is Building b && (b.def == ThingDefOf.Wall || b is Building_Door));
                    int roofed = inner.Count(c => map.roofGrid.RoofAt(c) == RoofDefOf.RoofConstructed), area = inner.Count(c => map.areaManager.BuildRoof[c]);
                    Result("N0 colonists build a closed room (walls and a door from blueprints, real jobs) and roof it by the game's own Build-roof area, which stays marked",
                        built == 24 && roofed == 25 && area == 25, $"walls and door {built}/24, roofed {roofed}/25, cells in the Build-roof area {area}/25, after {ticksTotal} ticks");
                    // the floors over it (fixture): a slab on level 1 over the house and a ledge east of it, walls and a door
                    // there, a slab on level 2 over the house (the top floor: its roof covers level 1), stairs up to the ledge
                    R2Floor(map, atHome, 1);
                    R2Floor(map, new CellRect(RG(29, 20).x, RG(29, 20).z, 6, 7), 1);
                    Place(ThingDefOf.Door, ThingDefOf.WoodLog, RL(28, 23, 1, map), map);
                    foreach (IntVec3 g in atHome.EdgeCells)
                    {
                        R2Wall(map, Levels.Cell(g, 1, map));
                    }
                    R2Floor(map, atHome, 2);
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    Building_Stairs st = R2Stairs(map, RG(36, 23), Rot4.West);
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    atNote = $"stairs {st?.Position}; ground roofs under the 3x3 {AtRoofs(atHomeH, 0, map)}, level-1 {AtRoofs(atHomeH, 1, map)}, Build-roof area on the ground room {inner.Count(c => map.areaManager.BuildRoof[c])}/25";
                    AtDesignateSlabs(map, atHomeH, 1);
                    AtWork(r2P, RL(23, 21, 1, map));
                    if (r2Q != null)
                    {
                        Teleport(r2Q, RG(23, 21));
                    }
                    SeedTicks("at_n_hole");
                    AtUntil(map, 20000, () => AtCells(atHomeH, 1, map).All(c => !ProjectZ.HasFloor(c, map)), "at_n_hole");
                    return true;
                }
                case "at_n_hole":
                {
                    tickWatch = null;
                    atHomeBuilt = BuildRoofEffect_Atrium_Patch.Built;
                    Info($"N1 the 3x3 taken up: {atNote}; slabs gone {AtCells(atHomeH, 1, map).Count(c => !ProjectZ.HasFloor(c, map))}/9; ground roofs {AtRoofs(atHomeH, 0, map)}; in the Build-roof area {AtCells(atHomeH, 0, map).Count(c => map.areaManager.BuildRoof[c])}/9");
                    // both colonists free for roofing, the ground room reached through its door
                    AtWork(r2P, RG(23, 25));
                    SeedTicks("at_n_wait");
                    AtUntil(map, 4000, () => false, "at_n_wait");
                    return true;
                }
                case "at_n_wait":
                {
                    tickWatch = null;
                    Room room = RG(23, 21).GetRoom(map);
                    int gone = AtCells(atHomeH, 1, map).Count(c => !ProjectZ.HasFloor(c, map));
                    int inArea = AtCells(atHomeH, 0, map).Count(c => map.areaManager.BuildRoof[c]);
                    int restArea = atHome.ContractedBy(1).Cells.Count(c => !atHomeH.Contains(c) && map.areaManager.BuildRoof[c]);
                    Result("N1 the middle floor taken up over a room the colonists built and the game roofed: the atrium stays (no constructor roofs it over again in 4000 ticks), its cells are out of the Build-roof area, the rest of the room keeps its area",
                        gone == 9 && AtCount(atHomeH, 0, map, PZDefOf.PZ_StairwellRoof) == 9 && BuildRoofEffect_Atrium_Patch.Built == atHomeBuilt && inArea == 0 && restArea == 16 && room != null && room.OpenRoofCount == 0,
                        $"slabs gone {gone}/9; ground roofs {AtRoofs(atHomeH, 0, map)}; roofs built over the atrium {BuildRoofEffect_Atrium_Patch.Built - atHomeBuilt}; Build-roof area: on the 3x3 {inArea}/9, on the rest of the room {restArea}/16; {(room == null ? "no room" : $"room cells {room.CellCount}, open roof {room.OpenRoofCount}")}");
                    // N2: a Build-roof area the player marks afterwards on three atrium cells
                    foreach (IntVec3 c in AtCells(atHomeRow, 0, map))
                    {
                        map.areaManager.NoRoof[c] = false;
                        map.areaManager.BuildRoof[c] = true;
                    }
                    AtWork(r2P, RG(23, 25));
                    SeedTicks("at_n_b");
                    AtUntil(map, 8000, () => AtCount(atHomeRow, 0, map, RoofDefOf.RoofConstructed) == 3, "at_n_b");
                    return true;
                }
                case "at_n_b":
                {
                    tickWatch = null;
                    Result("N2 a Build-roof area marked afterwards on three atrium cells: colonists build real roofs there (real jobs), the other six stay the atrium",
                        AtCount(atHomeRow, 0, map, RoofDefOf.RoofConstructed) == 3 && AtCount(atHomeH, 0, map, PZDefOf.PZ_StairwellRoof) == 6,
                        $"row {AtRoofs(atHomeRow, 0, map)}; ground roofs {AtRoofs(atHomeH, 0, map)}; built by the patch {BuildRoofEffect_Atrium_Patch.Built - atHomeBuilt}");
                    // N3: a no-roof area on those three (the designator's two writes)
                    AtNoRoof(map, atHomeRow, 0, true);
                    AtWork(r2P, RG(23, 25));
                    SeedTicks("at_n_r");
                    AtUntil(map, 8000, () => AtCount(atHomeRow, 0, map, RoofDefOf.RoofConstructed) == 0, "at_n_r");
                    return true;
                }
                case "at_n_r":
                {
                    tickWatch = null;
                    Room room = RG(23, 21).GetRoom(map);
                    Result("N3 a no-roof area on those three real roofs under the covered column: colonists remove them (Remove roof, real jobs) and the cells are the atrium's again, the room indoors",
                        AtCount(atHomeH, 0, map, PZDefOf.PZ_StairwellRoof) == 9 && room != null && room.OpenRoofCount == 0 && !room.UsesOutdoorTemperature,
                        $"row {AtRoofs(atHomeRow, 0, map)}; ground roofs {AtRoofs(atHomeH, 0, map)}; {(room == null ? "no room" : $"room cells {room.CellCount}, open roof {room.OpenRoofCount}, outdoor temperature {room.UsesOutdoorTemperature}")}");
                    Wait(5, "at_end");
                    return true;
                }
                case "at_end":
                    Finish();
                    return true;
            }
            return false;
        }
    }
}
