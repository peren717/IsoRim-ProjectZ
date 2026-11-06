using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace ProjectZ.Dev
{
    // Mode stairapi (Api 8: TryGetFloorFor, TryGetStairGeometry; Docs/Api.md). Run with -projectzfloors=3 -projectzseed=7;
    // with -projectzfloors=1 only the map-without-floors checks run (F and N). Every check builds its own fixture in one
    // cleared area (router2's R2Clear) under its own random state; the stairs of B are built by a colonist with real jobs
    // and ticks.
    //   F   TryGetFloorFor: a slab of the same stuff for wood, stone, steel; false and null without one, For unchanged;
    //       never a damaged slab, over every ThingDef
    //   N   a map without floors: false for a position and a blueprint
    //   P   a planned position, nothing built, 4 rotations on two floors: every part where it belongs and on its floor
    //   R   refusals: floors, gap row, top floor, the edges (the cell before each edge is fine), null map, thing, non-stairs;
    //       a sweep over the band edges: what is refused here is refused by the placement too
    //   B   4 rotations, 3 stuffs, one under a floor: blueprint, frame and built stairs give the position's answer; on the
    //       built stairs low, high, exit are the link's, the well is the opening; the landing is TryGetFloorFor's floor;
    //       a colonist walks up to the exit
    //   S   ship stairs: blueprint and built give the position's answer, and the position says yes off a deck (the deck rule
    //       is not asked)
    public static partial class TestRunner
    {
        private class SgCase
        {
            public Rot4 rot;
            public ThingDef stuff;
            public bool underFloor;
        }

        private static List<SgCase> sgCases;
        private static int sgIdx;
        private static StairGeometry sgPlan;
        private static Thing sgAccess;
        private static bool sgFrameSeen, sgFrameSame, sgSawHigh, sgOnSolid;
        private static string sgFrameDetail;

        private static IntVec3 SgCenter => RG(20, 16);

        private static string SgTag(SgCase c) => $"{c.rot.ToStringHuman()} {c.stuff.defName}{(c.underFloor ? " under a floor" : "")}";

        private static bool StepStairApi(Map map)
        {
            Rand.PushState((int)(StableHash("stairapi_" + stage) ^ (uint)(SeedArg ?? 717)));
            try
            {
                return StepStairApiInner(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static bool StepStairApiInner(Map map)
        {
            switch (stage)
            {
                case "sg_setup":
                    SgSetup(map);
                    return true;
                case "sg_f":
                    SgFloors();
                    stage = ProjectZ.Annex(map) == null ? "sg_n" : "sg_p";
                    return true;
                case "sg_n":
                    SgNoFloors(map);
                    stage = "sg_end";
                    return true;
                case "sg_p":
                    SgPlanned(map);
                    stage = "sg_r";
                    return true;
                case "sg_r":
                    SgRefusals(map);
                    stage = "sg_r_sweep";
                    return true;
                case "sg_r_sweep":
                    SgSweep(map);
                    sgIdx = 0;
                    stage = "sg_b";
                    return true;
                case "sg_b":
                    SgBuild(map);
                    return true;
                case "sg_b_built":
                    SgBuilt(map);
                    return true;
                case "sg_b_up":
                    SgWalked(map);
                    return true;
                case "sg_s":
                    SgShip(map);
                    stage = "sg_end";
                    return true;
                case "sg_end":
                    Finish();
                    return true;
            }
            return false;
        }

        private static void SgSetup(Map map)
        {
            DebugSettings.enableRandomMentalStates = false;
            NoRoofCollapse(map);
            Find.Storyteller.storytellerComps.Clear();
            Find.PlaySettings.autoRebuild = false;
            NoAmbushes(map);
            AnnexMapComponent an = ProjectZ.Annex(map);
            r2Area = new CellRect(map.Size.x / 2 - 22, (an?.groundZ ?? map.Size.z) / 2 - 16, 44, 32);
            List<Pawn> cols = Colonists(map);
            r2P = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)) ?? cols.First();
            r2Q = cols.FirstOrDefault(p => p != r2P);
            foreach (Pawn p in cols)
            {
                p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
            }
            R2Park(map);
            sgCases = new List<SgCase>
            {
                new SgCase { rot = Rot4.North, stuff = ThingDefOf.WoodLog, underFloor = true },
                new SgCase { rot = Rot4.East, stuff = ThingDefOf.Steel },
                new SgCase { rot = Rot4.South, stuff = ThingDef.Named("BlocksGranite") },
                new SgCase { rot = Rot4.West, stuff = ThingDefOf.WoodLog },
            };
            Info($"stairapi: area {r2Area}, builder {r2P}, map {map.Size}, groundZ {an?.groundZ}, stride {an?.stride}, levels {Levels.Min(map)}..{Levels.Max(map)}");
            Result("Api.Version is 8", Api.Version == 8, $"{Api.Version}");
            stage = "sg_f";
        }

        // ---------------------------------------------------------------------------------------------- helpers

        private static bool SgSame(StairGeometry a, StairGeometry b)
        {
            return a.center == b.center && a.rot == b.rot && a.floor == b.floor && a.footprint == b.footprint && a.solid == b.solid && a.low == b.low && a.high == b.high && a.exit == b.exit
                && a.ramp != null && b.ramp != null && a.well != null && b.well != null && a.ramp.SequenceEqual(b.ramp) && a.well.SequenceEqual(b.well);
        }

        private static string SgShow(StairGeometry g)
        {
            return $"{g.center} {g.rot.ToStringHuman()} floor {g.floor} rect {g.footprint} ramp {(g.ramp == null ? "null" : string.Join(" ", g.ramp))} solid {g.solid} low {g.low} high {g.high} exit {g.exit} well {(g.well == null ? "null" : string.Join(" ", g.well))}";
        }

        // false, the result empty, no exception
        private static string SgRefused(Func<(bool, StairGeometry)> ask)
        {
            try
            {
                (bool ok, StairGeometry g) = ask();
                return ok ? "true: " + SgShow(g) : g.ramp != null || g.well != null ? "false, but the result is not empty" : null;
            }
            catch (Exception e)
            {
                return "exception: " + e.GetType().Name + " " + e.Message;
            }
        }

        private static (bool, StairGeometry) SgAsk(IntVec3 c, Rot4 rot, Map map)
        {
            bool ok = Api.TryGetStairGeometry(c, rot, map, out StairGeometry g);
            return (ok, g);
        }

        private static (bool, StairGeometry) SgAsk(Thing t)
        {
            bool ok = Api.TryGetStairGeometry(t, out StairGeometry g);
            return (ok, g);
        }

        private static void SgExpectRefused(string name, Func<(bool, StairGeometry)> ask)
        {
            string why = SgRefused(ask);
            Result(name, why == null, why ?? "false, empty");
        }

        // ---------------------------------------------------------------------------------------------- F: floors

        private static void SgFloors()
        {
            foreach (ThingDef stuff in new[] { ThingDefOf.WoodLog, ThingDef.Named("BlocksGranite"), ThingDefOf.Steel })
            {
                bool ok = Api.TryGetFloorFor(stuff, out TerrainDef t);
                Result($"F {stuff.defName}: its own slab, StuffOf maps back, not damaged, the same as For",
                    ok && t != null && SlabDefs.StuffOf(t) == stuff && !SlabDefs.IsDamaged(t) && SlabDefs.All.Contains(t) && t == SlabDefs.For(stuff),
                    $"{ok} {t?.defName}, StuffOf {SlabDefs.StuffOf(t)?.defName}, damaged {SlabDefs.IsDamaged(t)}, For {SlabDefs.For(stuff)?.defName}");
            }
            bool woodOk = Api.TryGetFloorFor(ThingDefOf.WoodLog, out TerrainDef wood);
            Result("F WoodLog: the XML wood slab", woodOk && wood == PZDefOf.PZ_UpperFloor, $"{wood?.defName}");
            ThingDef cloth = ThingDefOf.Cloth;
            bool clothOk = Api.TryGetFloorFor(cloth, out TerrainDef clothFloor);
            bool nullOk = Api.TryGetFloorFor(null, out TerrainDef nullFloor);
            Result("F Cloth and null: false and null; For still gives the wood slab",
                !clothOk && clothFloor == null && !nullOk && nullFloor == null && SlabDefs.For(cloth) == PZDefOf.PZ_UpperFloor && SlabDefs.For(null) == PZDefOf.PZ_UpperFloor,
                $"cloth {clothOk} {clothFloor?.defName}, null {nullOk} {nullFloor?.defName}, For(cloth) {SlabDefs.For(cloth)?.defName}, For(null) {SlabDefs.For(null)?.defName}");
            int yes = 0, bad = 0;
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (Api.TryGetFloorFor(d, out TerrainDef t))
                {
                    yes++;
                    if (t == null || SlabDefs.IsDamaged(t) || !SlabDefs.All.Contains(t) || SlabDefs.StuffOf(t) != d)
                    {
                        bad++;
                    }
                }
                else if (t != null)
                {
                    bad++;
                }
            }
            int broken = SlabDefs.AllDamaged.Count();
            bool damagedOff = SlabDefs.AllDamaged.All(d => Api.TryGetFloorFor(SlabDefs.StuffOf(d), out TerrainDef t) && t != d && SlabDefs.DamagedOf(t) == d);
            Result("F every ThingDef: one buildable slab per stuff that has one, none damaged, the damaged slabs' stuffs give the sound slab",
                bad == 0 && yes == SlabDefs.All.Count() && broken > 0 && damagedOff, $"{yes} stuffs with a slab of {SlabDefs.All.Count()} slabs, {bad} bad; {broken} damaged slabs, all off {damagedOff}");
        }

        // ---------------------------------------------------------------------------------------------- N: no floors

        private static void SgNoFloors(Map map)
        {
            R2Clear(map);
            IntVec3 c = SgCenter;
            Result("N the map has no floors", !Api.HasFloors(map), "");
            foreach (Rot4 rot in s4Rots)
            {
                SgExpectRefused($"N {rot.ToStringHuman()}: a position on a map without floors", () => SgAsk(c, rot, map));
            }
            AcceptanceReport place = new PlaceWorker_Stairs().AllowsPlacing(PZDefOf.PZ_Stairs, c, Rot4.North, map);
            Result("N refused by the placement too", !place.Accepted, place.Reason);
            Thing site = GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, c, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
            SgExpectRefused("N a stairs blueprint on a map without floors", () => SgAsk(site));
            site.Destroy();
        }

        // ---------------------------------------------------------------------------------------------- P: planned

        private static void SgPlanned(Map map)
        {
            R2Clear(map);
            R2Park(map);
            IntVec3[] facing = { new IntVec3(0, 0, 1), new IntVec3(1, 0, 0), new IntVec3(0, 0, -1), new IntVec3(-1, 0, 0) };
            for (int L = 0; L <= 1; L++)
            {
                IntVec3 c = RL(20, 16, L, map);
                IntVec3 up = new IntVec3(0, 0, Levels.Shift(map, L + 1) - Levels.Shift(map, L));
                for (int i = 0; i < 4; i++)
                {
                    Rot4 rot = s4Rots[i];
                    IntVec3 f = facing[i];
                    int things = map.listerThings.AllThings.Count;
                    TerrainDef[] terrain = new[] { c - f, c, c + f, c + f * 2 }.SelectMany(x => new[] { x.GetTerrain(map), (x + up).GetTerrain(map) }).ToArray();
                    bool ok = Api.TryGetStairGeometry(c, rot, map, out StairGeometry g);
                    List<IntVec3> ramp = new List<IntVec3> { c - f, c, c + f };
                    IntVec3 solid = c + f * 2;
                    bool parts = ok && g.center == c && g.rot == rot && g.floor == L && g.ramp.SequenceEqual(ramp) && g.solid == solid && g.low == c - f && g.high == c + f && g.exit == solid + up
                        && g.well.SequenceEqual(ramp.Select(x => x + up));
                    bool rect = ok && g.footprint.Area == 4 && new HashSet<IntVec3>(g.footprint.Cells).SetEquals(ramp.Concat(new[] { solid }));
                    bool floors = ok && g.footprint.Cells.Concat(g.ramp).Concat(new[] { g.center, g.solid, g.low, g.high }).All(x => Api.FloorOf(x, map) == L)
                        && g.well.Concat(new[] { g.exit }).All(x => Api.FloorOf(x, map) == L + 1)
                        && g.well.Select(x => Api.GroundCellOf(x, map)).SequenceEqual(g.ramp.Select(x => Api.GroundCellOf(x, map)))
                        && Api.GroundCellOf(g.exit, map) == Api.GroundCellOf(g.solid, map);
                    bool again = Api.TryGetStairGeometry(c, rot, map, out StairGeometry g2) && SgSame(g, g2);
                    bool untouched = map.listerThings.AllThings.Count == things
                        && new[] { c - f, c, c + f, c + f * 2 }.SelectMany(x => new[] { x.GetTerrain(map), (x + up).GetTerrain(map) }).SequenceEqual(terrain);
                    Result($"P floor {L} {rot.ToStringHuman()}: footprint, ramp, solid, low, high, exit, well right and each on its floor; nothing changed",
                        parts && rect && floors && again && untouched, $"parts {parts}, rect {rect}, floors {floors}, again {again}, untouched {untouched}: {SgShow(g)}");
                }
            }
        }

        // ---------------------------------------------------------------------------------------------- R: refusals

        private static void SgRefusals(Map map)
        {
            R2Clear(map);
            R2Park(map);
            AnnexMapComponent a = ProjectZ.Annex(map);
            IntVec3 c = SgCenter;
            int x = c.x, top = Levels.Max(map);
            int groundEnd = a.groundZ - 1;                       // the last ground row
            int oneStart = Levels.Shift(map, 1);                 // floor 1's first row
            int oneEnd = oneStart + a.groundZ - 1;               // and its last
            SgExpectRefused("R null map", () => SgAsk(c, Rot4.North, null));
            SgExpectRefused("R null thing", () => SgAsk(null));
            SgExpectRefused("R invalid cell", () => SgAsk(IntVec3.Invalid, Rot4.North, map));
            SgExpectRefused("R invalid rotation", () => SgAsk(c, Rot4.Invalid, map));
            SgExpectRefused("R centre out of bounds", () => SgAsk(new IntVec3(map.Size.x + 3, 0, c.z), Rot4.North, map));
            IntVec3 gap = new IntVec3(x, 0, groundEnd + 1);
            Result("R the fixture's gap row is a gap row", Api.FloorOf(gap, map) == -1, $"{Api.FloorOf(gap, map)}");
            foreach (Rot4 rot in s4Rots)
            {
                SgExpectRefused($"R gap row {rot.ToStringHuman()}", () => SgAsk(gap, rot, map));
                SgExpectRefused($"R top floor {rot.ToStringHuman()}", () => SgAsk(RL(20, 16, top, map), rot, map));
            }
            // each edge: the solid end (or the low end) one past it is refused, one cell in is fine
            (string name, IntVec3 fine, IntVec3 past, Rot4 rot)[] edges =
            {
                ("ground's north edge, solid end", new IntVec3(x, 0, groundEnd - 2), new IntVec3(x, 0, groundEnd - 1), Rot4.North),
                ("ground's north edge, low end", new IntVec3(x, 0, groundEnd - 1), new IntVec3(x, 0, groundEnd), Rot4.South),
                ("floor 1's north edge", new IntVec3(x, 0, oneEnd - 2), new IntVec3(x, 0, oneEnd - 1), Rot4.North),
                ("floor 1's south edge, solid end", new IntVec3(x, 0, oneStart + 2), new IntVec3(x, 0, oneStart + 1), Rot4.South),
                ("floor 1's south edge, low end", new IntVec3(x, 0, oneStart + 1), new IntVec3(x, 0, oneStart), Rot4.North),
                ("ground's south edge (map edge)", new IntVec3(x, 0, 2), new IntVec3(x, 0, 1), Rot4.South),
                ("west map edge", new IntVec3(1, 0, c.z), new IntVec3(0, 0, c.z), Rot4.East),
                ("west map edge, solid end", new IntVec3(2, 0, c.z), new IntVec3(1, 0, c.z), Rot4.West),
                ("east map edge", new IntVec3(map.Size.x - 3, 0, c.z), new IntVec3(map.Size.x - 2, 0, c.z), Rot4.East),
            };
            foreach ((string name, IntVec3 fine, IntVec3 past, Rot4 rot) in edges)
            {
                bool ok = Api.TryGetStairGeometry(fine, rot, map, out StairGeometry g);
                Result($"R {name}: one cell in is fine", ok, SgShow(g));
                SgExpectRefused($"R {name}: past it", () => SgAsk(past, rot, map));
            }
            // things that are no stairs, and stairs that are not on the map
            Thing wall = Place(ThingDefOf.Wall, ThingDefOf.WoodLog, RG(6, 6), map);
            Thing wallSite = GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, RG(8, 6), map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
            Thing loose = ThingMaker.MakeThing(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog);
            SgExpectRefused("R a wall", () => SgAsk(wall));
            SgExpectRefused("R a wall blueprint", () => SgAsk(wallSite));
            SgExpectRefused("R stairs not spawned", () => SgAsk(loose));
            Thing gone = Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, RG(30, 16), map, Rot4.North);
            bool stood = Api.TryGetStairGeometry(gone, out _);
            gone.Destroy();
            Result("R stairs that stood give a result", stood, "");
            SgExpectRefused("R stairs destroyed", () => SgAsk(gone));
        }

        // Every cell refused here is refused by the placement: PlaceWorker_Stairs itself where the footprint is on the map;
        // past the map's edge (no cell there to ask a placeworker about) the game's bounds rule refuses before it.
        private static void SgSweep(Map map)
        {
            R2Clear(map);
            R2Park(map);
            AnnexMapComponent a = ProjectZ.Annex(map);
            PlaceWorker_Stairs worker = new PlaceWorker_Stairs();
            List<int> rows = new List<int>();
            for (int b = 0; b < a.floors; b++)
            {
                int start = b * a.stride;
                rows.AddRange(Enumerable.Range(start - 4, 9));
                rows.AddRange(Enumerable.Range(start + a.groundZ - 5, 10));
                rows.Add(start + a.groundZ / 2);
            }
            int[] cols = { 0, 1, 2, 3, r2Area.minX + 20, map.Size.x - 4, map.Size.x - 3, map.Size.x - 2, map.Size.x - 1 };
            int asked = 0, refused = 0, byWorker = 0, byBounds = 0, shapeOk = 0, placeOk = 0;
            List<string> bad = new List<string>();
            foreach (int z in rows.Distinct().Where(z => z >= 0 && z < map.Size.z))
            {
                foreach (int x in cols)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    foreach (Rot4 rot in s4Rots)
                    {
                        asked++;
                        bool shape = Api.TryGetStairGeometry(c, rot, map, out _);
                        AcceptanceReport full = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, c, rot, map, stuffDef: ThingDefOf.WoodLog);
                        placeOk += full.Accepted ? 1 : 0;
                        if (shape)
                        {
                            shapeOk++;
                            continue;
                        }
                        refused++;
                        bool onMap = StairShape.Footprint(c, rot).Cells.All(q => q.InBounds(map));
                        bool workerNo = onMap && !worker.AllowsPlacing(PZDefOf.PZ_Stairs, c, rot, map).Accepted;
                        byWorker += workerNo ? 1 : 0;
                        byBounds += onMap ? 0 : 1;
                        if (full.Accepted || onMap && !workerNo)
                        {
                            bad.Add($"{c} {rot.ToStringHuman()}");
                        }
                    }
                }
            }
            Result("R sweep over the band and map edges: every position refused here is refused by the placement too", bad.Count == 0 && refused > 0 && shapeOk > 0 && byWorker > 0 && byBounds > 0,
                $"{asked} asked, {shapeOk} shape ok, {refused} refused ({byWorker} by PlaceWorker_Stairs, {byBounds} past the map's edge by the game), {placeOk} placeable; {bad.Count} not refused there: {string.Join(", ", bad.Take(12))}");
        }

        // ---------------------------------------------------------------------------------------------- B: built

        private static void SgBuild(Map map)
        {
            SgCase k = sgCases[sgIdx];
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            IntVec3 c = SgCenter;
            Rot4 rot = k.rot;
            sgAccess = null;
            if (k.underFloor)
            {
                // a floor over all 4 cells above the stairs-to-be and round them, held by walls below, and stairs already up
                // to it (its removal is worked from the stairs it is reached by): the fixture of stairs4 B
                R2Floor(map, new CellRect(c.x - 2, c.z - 3, 5, 7), 1);
                foreach (IntVec3 w in new[] { c + new IntVec3(-2, 0, -3), c + new IntVec3(2, 0, -3), c + new IntVec3(-2, 0, 3), c + new IntVec3(2, 0, 3) })
                {
                    Place(ThingDefOf.Wall, ThingDefOf.WoodLog, w, map);
                }
                sgAccess = Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, c + new IntVec3(4, 0, 0), map, Rot4.West);
            }
            IntVec3 store = c + new IntVec3(6, 0, -5);
            foreach (IntVec3 at in new[] { store, store + new IntVec3(1, 0, 0) })
            {
                Thing s = ThingMaker.MakeThing(k.stuff);
                s.stackCount = s.def.stackLimit;
                GenSpawn.Spawn(s, at, map);
                map.areaManager.Home[at] = true;
            }
            AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, c, rot, map, stuffDef: k.stuff);
            bool planned = Api.TryGetStairGeometry(c, rot, map, out sgPlan);
            Thing site = ok.Accepted ? GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, c, map, rot, Faction.OfPlayer, k.stuff) : null;
            bool bp = Api.TryGetStairGeometry(site, out StairGeometry g);
            Result($"B {SgTag(k)}: the blueprint gives the position's result", ok.Accepted && planned && site is Blueprint && bp && SgSame(g, sgPlan),
                $"placement {(ok.Accepted ? "ok" : ok.Reason)}, site {site?.def?.defName}, position {SgShow(sgPlan)}; blueprint {SgShow(g)}");
            sgFrameSeen = sgFrameSame = false;
            sgFrameDetail = "";
            Teleport(r2P, store + new IntVec3(0, 0, 1));
            Draft(r2P, false);
            R2Work(r2P, WorkTypeDefOf.Construction, WorkTypeDefOf.Hauling);
            SeedTicks("sg_b_" + sgIdx);
            TickUntil(30000, () =>
            {
                if (!sgFrameSeen && c.GetThingList(map).FirstOrDefault(t => t is Frame && Stairwell.IsStairThing(t)) is Thing fr)
                {
                    sgFrameSeen = true;
                    sgFrameSame = Api.TryGetStairGeometry(fr, out StairGeometry gf) && SgSame(gf, sgPlan);
                    sgFrameDetail = SgShow(gf);
                }
                return SgStairsAt(map, c) != null;
            }, "sg_b_built", 1);
        }

        private static Building_Stairs SgStairsAt(Map map, IntVec3 c) => c.GetThingList(map).OfType<Building_Stairs>().FirstOrDefault(s => s != sgAccess);

        private static void SgBuilt(Map map)
        {
            tickWatch = null;
            SgCase k = sgCases[sgIdx];
            IntVec3 c = SgCenter;
            Result($"B {SgTag(k)}: the frame gives the position's result", sgFrameSeen && sgFrameSame, $"frame seen {sgFrameSeen}, same {sgFrameSame}: {sgFrameDetail}");
            Building_Stairs s = SgStairsAt(map, c);
            bool built = Api.TryGetStairGeometry(s, out StairGeometry g);
            bool again = Api.TryGetStairGeometry(c, k.rot, map, out StairGeometry gp);
            Result($"B {SgTag(k)}: built by a colonist, the stairs and the position give the planned result", s != null && built && again && SgSame(g, sgPlan) && SgSame(gp, sgPlan),
                $"stairs {s} builder job {r2P.CurJobDef?.defName}: {SgShow(g)}");
            if (s == null)
            {
                SgNext();
                return;
            }
            StairGrid sg = StairGrid.For(map);
            int li = sg?.IndexOf(s) ?? -1;
            VLink l = li >= 0 ? sg.links[li] : default;
            Result($"B {SgTag(k)}: low, high, exit are the link's low, bottom, top", li >= 0 && l.low == g.low && l.bottom == g.high && l.top == g.exit && l.mid == g.center && l.floor == g.floor,
                $"link {li}: {l.low} {l.mid} {l.bottom} -> {l.top}, floor {l.floor}");
            bool wells = g.ramp.All(r => Stairwell.Well(r, map) && Column.OpenAbove(r, map));
            bool open = g.well.All(w => w.GetTerrain(map) == PZDefOf.PZ_OpenAir && w.GetEdifice(map) == null);
            bool stacked = g.ramp.Select(r => StairShape.Above(r, map)).SequenceEqual(g.well) && g.ramp.Select(r => Api.StackedCell(r, map, true)).SequenceEqual(g.well);
            bool solidNoWell = !Stairwell.StairThingAt(g.solid, map) && Stairwell.StairCellAt(g.solid, map) && !g.solid.Walkable(map);
            Result($"B {SgTag(k)}: every well cell is the opening the game keeps (Well on the ramp, open air above); the solid end is no well", wells && open && stacked && solidNoWell,
                $"well on the ramp {wells}, open above {open} ({string.Join(" ", g.well.Select(w => w.GetTerrain(map).defName))}), stacked {stacked}, solid {solidNoWell}");
            bool landing = ProjectZ.HasFloor(g.exit, map) && FloorRoof.Below(g.exit, map) == g.solid;
            bool floorOk = Api.TryGetFloorFor(k.stuff, out TerrainDef want);
            TerrainDef got = g.exit.GetTerrain(map);
            Result($"B {SgTag(k)}: a floor at the exit, over the solid end; the stairs' landing is TryGetFloorFor of their stuff", landing && floorOk && got == want, $"exit {g.exit} {got.defName}, wanted {want?.defName}, below {FloorRoof.Below(g.exit, map)}");
            IntVec3 from = g.low - k.rot.FacingCell * 2;
            Draft(r2P, true);
            Teleport(r2P, from);
            sgSawHigh = sgOnSolid = false;
            IntVec3 high = g.high, solid = g.solid, exit = g.exit;
            GoTo(r2P, exit);
            SeedTicks("sg_b_up_" + sgIdx);
            TickUntil(3000, () =>
            {
                sgSawHigh |= r2P.Position == high;
                sgOnSolid |= r2P.Position == solid;
                return r2P.Position == exit;
            }, "sg_b_up", 1);
        }

        private static void SgWalked(Map map)
        {
            tickWatch = null;
            SgCase k = sgCases[sgIdx];
            bool ok = Api.TryGetStairGeometry(SgStairsAt(map, SgCenter), out StairGeometry g);
            Result($"B {SgTag(k)}: a colonist walks up by the high cell to the exit, one floor up, never on the solid end", ok && r2P.Position == g.exit && Api.FloorOf(r2P) == g.floor + 1 && sgSawHigh && !sgOnSolid,
                $"at {r2P.Position}, exit {g.exit}, floor {Api.FloorOf(r2P)}, by the high cell {sgSawHigh}, on solid {sgOnSolid}");
            SgNext();
        }

        private static void SgNext()
        {
            sgIdx++;
            stage = sgIdx < sgCases.Count ? "sg_b" : "sg_s";
        }

        // ---------------------------------------------------------------------------------------------- S: ship stairs

        private static void SgShip(Map map)
        {
            R2Clear(map);
            R2Park(map);
            if (PZDefOf.PZ_ShipStairs == null)
            {
                Result("S the ship stairs Def (Odyssey)", false, "missing");
                return;
            }
            IntVec3 c = SgCenter;
            Rot4 rot = Rot4.West;
            bool planned = Api.TryGetStairGeometry(c, rot, map, out StairGeometry plan);
            AcceptanceReport deck = ShipStairs.Check(c, rot, map);
            Result("S off a deck: the position still says yes, the ship stairs' own rule refuses (the documented difference)", planned && !deck.Accepted, $"position {planned}, Check {(deck.Accepted ? "accepted" : deck.Reason)}");
            Thing site = GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_ShipStairs, c, map, rot, Faction.OfPlayer, null);
            bool bp = Api.TryGetStairGeometry(site, out StairGeometry gb);
            Result("S a ship stairs blueprint gives the position's result", bp && SgSame(gb, plan), SgShow(gb));
            site.Destroy();
            Thing built = Place(PZDefOf.PZ_ShipStairs, null, c, map, rot);
            bool ok = Api.TryGetStairGeometry(built, out StairGeometry g);
            StairGrid sg = StairGrid.For(map);
            int li = sg?.IndexOf(built) ?? -1;
            VLink l = li >= 0 ? sg.links[li] : default;
            Result("S built ship stairs give the position's result; low, high, exit are the link's", ok && SgSame(g, plan) && li >= 0 && l.low == g.low && l.bottom == g.high && l.top == g.exit, $"{SgShow(g)}; link {l.low} {l.bottom} -> {l.top}");
            built.Destroy();
        }
    }
}
