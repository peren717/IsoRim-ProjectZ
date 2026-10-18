using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode debris (IsoRim's collapse debris, IsoRim API 4; run with -projectzfloors=2). In a cleared field:
    //   - a 10 x 10 pavilion: a slab on floor 2 over a roof held by four wooden columns, with a wall, a bed, a table,
    //     chairs and a plant pot on the slab. The four columns are killed together: the roof under the slab collapses
    //     and the slab with it (SlabCollapse, which hands each cell to IsoRim first). The pieces are held and stepped
    //     (Api.Dev.DebrisHold / DebrisAdvance) and filmed every 5/60 s for 3 s, then at 3.5 to 5.5 s; the frame time is
    //     measured with the pieces in view against none;
    //   - a 9 x 9 granite room with a built roof beside it: its walls are killed, the game drops the roof (IsoRim's roof
    //     drop hook: the roof's pieces only), filmed the same way.
    //   - real flow: a colonist deconstructs (designator, work giver, job) the one column under a 5 x 5 slab with a wall
    //     and a table on it; the slab collapses and shows debris.
    // Checks: pieces were made for both, all gone after their life, the slab and roofs gone as before.
    public static partial class TestRunner
    {
        private static CellRect dbPav, dbRoom;
        private static readonly List<IntVec3> dbColumns = new List<IntVec3>();
        private static float dbOldYaw, dbOldPitch, dbOldMin;
        private static string dbCase;
        private static int dbShot, dbPerfFrames, dbLive, dbWait;
        private static float dbPerfSum, dbPerfWith;
        private static string dbNextAfterFilm;
        private static IntVec3 dbRealCol;
        private static CellRect dbReal;
        private static int dbRealPieces;

        private static int an_groundZ(Map map) => ProjectZ.Annex(map)?.groundZ ?? map.Size.z;

        // sim times (seconds) of the film's frames: every 5/60 s to 3 s, then the pieces lying and sinking
        private static readonly List<float> dbTimes = BuildDbTimes();

        private static List<float> BuildDbTimes()
        {
            List<float> l = new List<float>();
            for (int i = 0; i <= 36; i++)
            {
                l.Add(i * 5f / 60f);
            }
            l.AddRange(new[] { 3.5f, 4f, 4.5f, 5f, 5.5f });
            return l;
        }

        private static bool StepDebris(Map map)
        {
            switch (stage)
            {
                case "db_setup":
                    SetupDebris(map);
                    return true;
                case "db_before":
                    ShotAt(map, 1, dbPav.CenterCell, 9f);
                    Find.UIRoot.screenshotMode.Active = true;   // frames without the UI
                    Wait(40, "db_before_take");
                    return true;
                case "db_before_take":
                    ZCapture("debris_slab_before");
                    Wait(3, "db_go");
                    return true;
                case "db_go":
                {
                    IsoRimBridge.DebrisClear();
                    IsoRimBridge.DebrisHold = true;
                    dbCase = "slab";
                    int before = PZStats.slabCollapses;
                    foreach (IntVec3 c in dbColumns)
                    {
                        c.GetEdifice(map)?.Kill();
                    }
                    Info($"pavilion columns killed ({dbColumns.Count}); slab collapses so far {before}");
                    TickUntil(30, () => dbPav.Cells.All(g => !Slab.At(UpS(g, map), map)), "db_collapsed", 1);
                    return true;
                }
                case "db_collapsed":
                {
                    tickWatch = null;
                    int left = dbPav.Cells.Count(g => Slab.At(UpS(g, map), map));
                    int roofs = dbPav.Cells.Count(g => map.roofGrid.Roofed(g));
                    Result("debris: the pavilion's slab and roof collapse as before", left == 0 && roofs == 0, $"slab cells left {left}, roofs left {roofs}, after {ticksTotal} ticks");
                    ShotAt(map, 1, dbPav.CenterCell, 9f);
                    Find.UIRoot.screenshotMode.Active = true;   // frames without the UI
                    Wait(3, "db_spawned");
                    return true;
                }
                case "db_spawned":
                    if (IsoRimBridge.DebrisPending > 0 && dbWait++ < 600)
                    {
                        return true;   // the pieces are built over a few frames
                    }
                    Info($"pieces built over {dbWait + 3} frames");
                    dbWait = 0;
                    dbLive = IsoRimBridge.DebrisLive;
                    Result("debris: the slab collapse made pieces", dbLive > 0, $"{dbLive} pieces; {IsoRimBridge.DebrisStats}");
                    dbPerfFrames = 0;
                    dbPerfSum = 0f;
                    stage = "db_perf_with";
                    return true;
                case "db_perf_with":
                    // 120 frames with the pieces in view (held, mid-air)
                    if (dbPerfFrames++ > 0)
                    {
                        dbPerfSum += Time.unscaledDeltaTime;
                    }
                    if (dbPerfFrames > 120)
                    {
                        dbPerfWith = dbPerfSum / 120f * 1000f;
                        Info($"frame time with {IsoRimBridge.DebrisLive} pieces in view: {dbPerfWith:0.00} ms (avg of 120 frames)");
                        dbShot = 0;
                        dbNextAfterFilm = "db_perf_none";
                        stage = "db_film";
                    }
                    return true;
                case "db_film":
                    if (dbShot >= dbTimes.Count)
                    {
                        Info($"film {dbCase} done: {IsoRimBridge.DebrisStats}");
                        IsoRimBridge.DebrisAdvance(3f);   // past every piece's life
                        Wait(3, "db_film_end");
                        return true;
                    }
                    IsoRimBridge.DebrisAdvance(dbTimes[dbShot] - (dbShot > 0 ? dbTimes[dbShot - 1] : 0f));
                    Wait(2, "db_film_take");
                    return true;
                case "db_film_take":
                    ZCapture($"debris_{dbCase}_f{Mathf.RoundToInt(dbTimes[dbShot] * 60f):000}");
                    dbShot++;
                    Wait(1, "db_film");
                    return true;
                case "db_film_end":
                    Result($"debris ({dbCase}): every piece gone after its life", IsoRimBridge.DebrisLive == 0, $"live {IsoRimBridge.DebrisLive}; {IsoRimBridge.DebrisStats}");
                    stage = dbNextAfterFilm;
                    dbPerfFrames = 0;
                    dbPerfSum = 0f;
                    return true;
                case "db_perf_none":
                    if (dbPerfFrames++ > 0)
                    {
                        dbPerfSum += Time.unscaledDeltaTime;
                    }
                    if (dbPerfFrames > 120)
                    {
                        float none = dbPerfSum / 120f * 1000f;
                        Info($"frame time with no pieces, same view: {none:0.00} ms (avg of 120 frames); with the pieces {dbPerfWith:0.00} ms: +{dbPerfWith - none:0.00} ms");
                        stage = "db_room_before";
                    }
                    return true;

                // ---- the vanilla roof drop
                case "db_room_before":
                    ShotAt(map, 0, dbRoom.CenterCell, 9f, roofs: true);
                    Find.UIRoot.screenshotMode.Active = true;   // frames without the UI
                    Wait(40, "db_room_before_take");
                    return true;
                case "db_room_before_take":
                    ZCapture("debris_roof_before");
                    Wait(3, "db_room_go");
                    return true;
                case "db_room_go":
                {
                    IsoRimBridge.DebrisClear();
                    IsoRimBridge.DebrisHold = true;
                    dbCase = "roof";
                    List<Thing> walls = dbRoom.EdgeCells.Select(c => c.GetEdifice(map)).Where(b => b != null).Cast<Thing>().ToList();
                    foreach (Thing w in walls)
                    {
                        if (!w.Destroyed)
                        {
                            w.Kill();
                        }
                    }
                    Info($"room walls killed ({walls.Count})");
                    TickUntil(30, () => dbRoom.ContractedBy(1).Cells.All(g => !map.roofGrid.Roofed(g)), "db_room_collapsed", 1);
                    return true;
                }
                case "db_room_collapsed":
                {
                    tickWatch = null;
                    int roofs = dbRoom.Cells.Count(g => map.roofGrid.Roofed(g));
                    Result("debris: the room's roof is dropped by the game as before", roofs == 0, $"roofs left {roofs} after {ticksTotal} ticks");
                    ShotAt(map, 0, dbRoom.CenterCell, 9f, roofs: true);
                    Find.UIRoot.screenshotMode.Active = true;   // frames without the UI
                    Wait(3, "db_room_spawned");
                    return true;
                }
                case "db_room_spawned":
                    if (IsoRimBridge.DebrisPending > 0 && dbWait++ < 600)
                    {
                        return true;
                    }
                    Info($"pieces built over {dbWait + 3} frames");
                    dbWait = 0;
                    dbLive = IsoRimBridge.DebrisLive;
                    Result("debris: the roof drop made pieces", dbLive > 0, $"{dbLive} pieces; {IsoRimBridge.DebrisStats}");
                    dbShot = 0;
                    dbNextAfterFilm = "db_real_setup";
                    stage = "db_film";
                    return true;

                // ---- real flow: a colonist deconstructs the one column under a 5 x 5 slab
                case "db_real_setup":
                {
                    IsoRimBridge.DebrisClear();
                    IsoRimBridge.DebrisHold = true;
                    dbCase = "real";
                    dbRealCol = new IntVec3(dbRoom.maxX + 8, 0, dbRoom.minZ + 4);
                    dbReal = CellRect.CenteredOn(dbRealCol, 2);
                    Thing col = Place(ThingDefOf.Wall, ThingDefOf.WoodLog, dbRealCol, map);
                    foreach (IntVec3 g in dbReal)
                    {
                        map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                        map.terrainGrid.SetTerrain(UpS(g, map), PZDefOf.PZ_UpperFloor);
                        map.fogGrid.Unfog(UpS(g, map));
                    }
                    Place(ThingDefOf.Wall, ThingDefOf.BlocksGranite, UpS(dbRealCol + new IntVec3(-1, 0, 1), map), map);
                    Place(DefDatabase<ThingDef>.GetNamed("Table2x2c"), ThingDefOf.WoodLog, UpS(dbRealCol + new IntVec3(0, 0, -1), map), map);
                    Designator_Deconstruct dd = new Designator_Deconstruct();
                    AcceptanceReport ok = dd.CanDesignateThing(col);
                    if (ok.Accepted)
                    {
                        dd.DesignateThing(col);
                    }
                    Result("debris real flow: the column under the slab can be marked for deconstruction", ok.Accepted, $"'{ok.Reason}'");
                    Pawn worker = Colonists(map).FirstOrDefault(p => !p.Downed && !p.Dead);
                    foreach (Pawn p in Colonists(map))
                    {
                        Draft(p, p != worker);
                    }
                    if (worker != null)
                    {
                        Teleport(worker, dbReal.maxZ + 3 < an_groundZ(map) ? new IntVec3(dbRealCol.x, 0, dbReal.maxZ + 3) : new IntVec3(dbRealCol.x, 0, dbReal.minZ - 3));
                    }
                    SetWork(map, true);
                    dbRealPieces = 0;
                    Info($"real flow: pavilion {dbReal}, column {dbRealCol}, worker {worker}");
                    TickUntil(20000, () => dbRealCol.GetEdifice(map) == null, "db_real_gone", 10);
                    return true;
                }
                case "db_real_gone":
                    tickWatch = null;
                    Info($"column gone after {ticksTotal} ticks (deconstructed by the colonist's job)");
                    TickUntil(60, () => dbReal.Cells.All(g => !Slab.At(UpS(g, map), map)), "db_real_fell", 1);
                    return true;
                case "db_real_fell":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    int left = dbReal.Cells.Count(g => Slab.At(UpS(g, map), map));
                    Result("debris real flow: the slab collapses after the deconstruction", left == 0, $"slab cells left {left}");
                    ShotAt(map, 1, dbRealCol, 7f);
                    Find.UIRoot.screenshotMode.Active = true;
                    dbWait = 0;
                    Wait(3, "db_real_built");
                    return true;
                }
                case "db_real_built":
                    if (IsoRimBridge.DebrisPending > 0 && dbWait++ < 600)
                    {
                        return true;
                    }
                    dbRealPieces = IsoRimBridge.DebrisLive;
                    Result("debris real flow: the collapse shows debris", dbRealPieces > 0, $"{dbRealPieces} pieces; {IsoRimBridge.DebrisStats}");
                    ZCapture("debris_real_f000");
                    Wait(2, "db_real_adv");
                    return true;
                case "db_real_adv":
                    IsoRimBridge.DebrisAdvance(0.2f);
                    Wait(2, "db_real_take");
                    return true;
                case "db_real_take":
                    ZCapture("debris_real_f012");
                    Wait(2, "db_end");
                    return true;
                case "db_end":
                    IsoRimBridge.DebrisHold = false;
                    IsoRimBridge.SetOption("freeYaw", dbOldYaw);
                    IsoRimBridge.SetOption("isoPitch", dbOldPitch);
                    Find.CameraDriver.config.sizeRange.min = dbOldMin;
                    Finish();
                    return true;
            }
            return false;
        }

        private static void SetupDebris(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || an.floors < 2)
            {
                Result("debris setup", false, $"needs 2 floors (-projectzfloors=2); floors {an?.floors}");
                Finish();
                return;
            }
            // a 40 x 18 open field, the one with the fewest cells to fix
            CellRect found = CellRect.Empty;
            int bestBad = int.MaxValue;
            for (int z = 3; z + 18 < an.groundZ - 3; z += 4)
            {
                for (int x = 3; x + 40 < map.Size.x - 3; x += 4)
                {
                    CellRect r = new CellRect(x, z, 40, 18);
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
            }
            if (found.Area == 0)
            {
                Result("debris field", false, "map too small");
                Finish();
                return;
            }
            foreach (IntVec3 c in found.ExpandedBy(1))
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable)
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
            DebugSettings.enableRandomMentalStates = false;
            // the pavilion: four columns holding a roof, the slab on it, things on the slab
            dbPav = new CellRect(found.minX + 3, found.minZ + 4, 10, 10);
            dbColumns.Clear();
            foreach (IntVec3 o in new[] { new IntVec3(2, 0, 2), new IntVec3(7, 0, 2), new IntVec3(2, 0, 7), new IntVec3(7, 0, 7) })
            {
                IntVec3 c = new IntVec3(dbPav.minX + o.x, 0, dbPav.minZ + o.z);
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
                dbColumns.Add(c);
            }
            foreach (IntVec3 g in dbPav)
            {
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(UpS(g, map), PZDefOf.PZ_UpperFloor);
                map.fogGrid.Unfog(UpS(g, map));
            }
            IntVec3 P(int dx, int dz) => UpS(new IntVec3(dbPav.minX + dx, 0, dbPav.minZ + dz), map);
            for (int dx = 1; dx <= 5; dx++)
            {
                Place(ThingDefOf.Wall, ThingDefOf.BlocksGranite, P(dx, 8), map);
            }
            List<string> placed = new List<string>();
            void Try(string def, ThingDef stuff, IntVec3 c, Rot4 rot)
            {
                ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(def);
                if (d == null)
                {
                    placed.Add(def + " (no def)");
                    return;
                }
                Place(d, stuff, c, map, rot);
                placed.Add(def);
            }
            Try("Bed", ThingDefOf.WoodLog, P(7, 4), Rot4.North);
            Try("Table2x2c", ThingDefOf.WoodLog, P(3, 4), Rot4.North);
            Try("DiningChair", ThingDefOf.WoodLog, P(2, 4), Rot4.East);
            Try("DiningChair", ThingDefOf.WoodLog, P(5, 5), Rot4.West);
            Try("PlantPot", ThingDefOf.BlocksGranite, P(1, 1), Rot4.North);
            Try("StandingLamp", ThingDefOf.Steel, P(8, 1), Rot4.North);
            // the room: granite walls and a built roof
            dbRoom = new CellRect(dbPav.maxX + 8, dbPav.minZ, 9, 9);
            foreach (IntVec3 c in dbRoom.EdgeCells)
            {
                Place(ThingDefOf.Wall, ThingDefOf.BlocksGranite, c, map);
            }
            foreach (IntVec3 c in dbRoom)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            Try("Table2x2c", ThingDefOf.WoodLog, new IntVec3(dbRoom.minX + 3, 0, dbRoom.minZ + 3), Rot4.North);
            // colonists out of the field, drafted
            foreach (Pawn p in Colonists(map))
            {
                p.drafter.Drafted = true;
                Teleport(p, CellFinder.RandomClosewalkCellNear(new IntVec3(found.minX > map.Size.x / 2 ? 5 : map.Size.x - 6, 0, 5), map, 4));
            }
            dbOldYaw = (float)IsoRimBridge.SetOption("freeYaw", 0f);
            dbOldPitch = (float)IsoRimBridge.SetOption("isoPitch", 35.3f);
            dbOldMin = Find.CameraDriver.config.sizeRange.min;
            Find.CameraDriver.config.sizeRange.min = 2f;
            Info($"field {found}, pavilion {dbPav} (columns {string.Join(" ", dbColumns)}), room {dbRoom}; on the slab: walls, {string.Join(", ", placed)}; storey {IsoRimBridge.StoreyHeight:0.00}; IsoRim API {IsoRimBridge.ApiVersion}");
            Wait(30, "db_before");
        }
    }
}
