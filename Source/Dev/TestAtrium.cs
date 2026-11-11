using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode atrium (player request 2026-11-02, Atrium.cs): a roof taken away under a covered column is the invisible
    // stairwell roof (indoors, no rain, indoor temperature, seen into from above); the column's cover gone: open sky.
    // Run with -projectzfloors=3 -projectzseed=7. Roof collapse ON. Fixtures (ground coords in the cleared area):
    //   main: a 3-storey building, walls round x 10..16, z 10..16 on every level (closed rings), slabs on levels 1 and 2,
    //         a built roof over level 2. The atrium H = x 12..14, z 12..14.
    //   cave: walls round x 24..30, z 10..16 on the ground and on level 1, a slab on level 1, a thick rock roof over
    //         level 1 (the cover); H2 = x 26..28, z 12..14.
    //   A1 a colonist takes up the level-1 slab over H (real job): the ground roof under it is a plain roof
    //   A2 a colonist removes the ground roof over H (Remove roof area, real job): the stairwell roof, the ground room
    //      indoors (no open roof cells, indoor temperature), the column open (Column.OpenAbove)
    //   A3 the no-roof area left on H: no roof-removal job for 1500 ticks, the roofs stay
    //   A4 rain: a colonist standing in H gets no soaking-wet thought; A5 the room keeps its own temperature
    //   shots: from floors 1, 2 and 3
    //   S1 save/load: the atrium stays; a cell written back to "no roof" behind the rules (an older save) is put right
    //   B1 a Build roof area over one atrium cell: a colonist builds a real roof there (real job)
    //   R1 the level-2 slab over H taken up (real job): the ground stays an atrium (the level-1 roof covers it)
    //   R2 the level-1 roof over H removed (real job): the stairwell roof (the level-2 roof covers it)
    //   R3 the level-2 roof over H removed (real job): the whole column open to the sky, the ground room outdoor temperature
    //   R4 rain falls in: a colonist in H gets soaking wet
    //   C1 cave: the level-1 slab taken up and the ground roof removed under the thick roof (real jobs): the stairwell roof
    public static partial class TestRunner
    {
        private static CellRect atRing, atH, atCave, atH2, atHall, atH3, atRow;
        private static bool atLoop;
        private static IntVec3 atOdd;
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
                    Result("A1 a colonist takes up the level-1 slab over the 3x3 (real jobs): the ground roof under it a plain roof",
                        gone == 9 && AtCount(atH, 0, map, RoofDefOf.RoofConstructed) == 9, $"{atNote}; slabs gone {gone}/9; ground roofs {AtRoofs(atH, 0, map)}; level-1 roofs {AtRoofs(atH, 1, map)}");
                    AtNoRoof(map, atH, 0, true);
                    AtWork(r2P, RG(11, 11));
                    SeedTicks("at_roof0");
                    AtUntil(map, 15000, () => AtCount(atH, 0, map, RoofDefOf.RoofConstructed) == 0, "at_roof0");
                    return true;
                }
                case "at_roof0":
                {
                    tickWatch = null;
                    Room room = AtRoom(map);
                    IntVec3 mid = RG(13, 13);
                    Result("A2 a colonist removes the ground roof over the 3x3 (Remove roof area, real jobs): the stairwell roof, the room indoors, the column open",
                        AtCount(atH, 0, map, PZDefOf.PZ_StairwellRoof) == 9 && room != null && room.OpenRoofCount == 0 && !room.UsesOutdoorTemperature && !room.PsychologicallyOutdoors
                        && Column.OpenAbove(mid, map) && Atrium.Is(mid, map),
                        $"ground roofs {AtRoofs(atH, 0, map)}; {AtRoomStr(map)}; open above {Column.OpenAbove(mid, map)}, atrium {Atrium.Is(mid, map)}; covers {Atrium.Covers}");
                    // A3/A4/A5: the no-roof area stays; rain; the room warmer than outdoors
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
                    atNote = $"raw roof at {old} before saving: {map.roofGrid.RoofAt(old)?.defName ?? "none"}";
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
                    Result("S1 save/load: the atrium stays (stairwell roofs, room indoors); the cell saved with no roof is put right at load",
                        AtCount(atH, 0, map, PZDefOf.PZ_StairwellRoof) == 9 && room != null && room.OpenRoofCount == 0 && !room.UsesOutdoorTemperature,
                        $"{atNote}; after loading ground roofs {AtRoofs(atH, 0, map)}; {AtRoomStr(map)}");
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
                    Result("R1 the level-2 slab over the 3x3 taken up (real jobs): the level-1 roof a plain roof, the ground stays the atrium",
                        gone == 9 && AtCount(atH, 1, map, RoofDefOf.RoofConstructed) == 9 && AtCount(atH, 0, map, PZDefOf.PZ_StairwellRoof) == 8,
                        $"{atNote}; slabs gone {gone}/9; level-1 roofs {AtRoofs(atH, 1, map)}; ground roofs {AtRoofs(atH, 0, map)}");
                    AtNoRoof(map, atH, 1, true);
                    AtWork(r2P, RL(11, 11, 1, map));
                    SeedTicks("at_roof1");
                    AtUntil(map, 15000, () => AtCount(atH, 1, map, PZDefOf.PZ_StairwellRoof) >= 8, "at_roof1");
                    return true;
                }
                case "at_roof1":
                {
                    tickWatch = null;
                    Result("R2 the level-1 roof over the 3x3 removed (real jobs): the stairwell roof (the level-2 roof covers it) on the 8 cells a colonist can touch (the middle one has open air all round: kept), the ground unchanged",
                        AtCount(atH, 1, map, PZDefOf.PZ_StairwellRoof) == 8 && map.roofGrid.RoofAt(RL(13, 13, 1, map)) == RoofDefOf.RoofConstructed && AtCount(atH, 0, map, PZDefOf.PZ_StairwellRoof) == 8,
                        $"level-1 roofs {AtRoofs(atH, 1, map)}; ground roofs {AtRoofs(atH, 0, map)}");
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
                    Result("R3 the level-2 roof over the 3x3 removed (real jobs): the column open to the sky (the built ground roof cell and the untouchable level-1 middle roof stay), the ground room uses the outdoor temperature",
                        AtCount(atH, 2, map, null) == 9 && AtCount(atH, 1, map, null) == 8 && AtCount(atH, 0, map, null) == 8 && map.roofGrid.RoofAt(RG(13, 13)) == RoofDefOf.RoofConstructed
                        && room != null && room.OpenRoofCount == 8 && room.UsesOutdoorTemperature,
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
                    AtNoRoof(map, atH2, 0, true);
                    AtWork(r2P, RG(31, 11));
                    SeedTicks("at_cave2");
                    AtUntil(map, 15000, () => AtCount(atH2, 0, map, RoofDefOf.RoofConstructed) == 0, "at_cave2");
                    return true;
                }
                case "at_cave2":
                {
                    tickWatch = null;
                    Room room = RG(31, 11).GetRoom(map);
                    Result("C1 cave: the level-1 slab taken up and the ground roof removed under a thick rock roof (real jobs): the stairwell roof, the room indoors",
                        AtCount(atH2, 0, map, PZDefOf.PZ_StairwellRoof) == 9 && AtCount(atH2, 1, map, RoofDefOf.RoofRockThick) == 9 && room != null && room.OpenRoofCount == 0 && !room.UsesOutdoorTemperature,
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
                    Room room = RG(11, 21).GetRoom(map);
                    Result("HC an old save with open sky under existing cover (and an invisible roof under none): put right at load",
                        AtCount(atH3, 0, map, PZDefOf.PZ_StairwellRoof) == 9 && AtCount(atH3, 1, map, PZDefOf.PZ_StairwellRoof) == 6 && map.roofGrid.RoofAt(atOdd) == null
                        && room != null && room.OpenRoofCount == 0 && AtCount(atH, 0, map, null) == 8,
                        $"{atNote}; after loading ground {AtRoofs(atH3, 0, map)}, level-1 {AtRoofs(atH3, 1, map)}, uncovered {map.roofGrid.RoofAt(atOdd)?.defName ?? "none"}; load fix: {Atrium.LoadCovered} covered, {Atrium.LoadOpened} opened; main atrium ground {AtRoofs(atH, 0, map)}; hall open roof {room?.OpenRoofCount}");
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
