using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using ProjectZ.Combat;
using ProjectZ.Patches;

namespace ProjectZ.Dev
{
    // Mode mtn (mountains foundations F1-F6, isorim-pz-mountains-design.md §3; run with -projectzfloors=4, and for the
    // performance baseline -projectzmapsize=250 or -projectzmapsize=300 -projectzfloors=13). A fixed world seed (as
    // wallheight) and a Mountainous tile. Nothing here may change what the game does today:
    //   F6 the annex as generated is exactly the fill rule's (open air, unfogged), and FillAnnex changes nothing;
    //   F1 every map cell: FloorOf / IsVoid / GroundCellOf / Virtual and every ground cell x level CellOnFloor equal the
    //      old raw stride formulas; the table is the identity, layoutVersion 2; bad tables are refused; a non-identity
    //      and a signed table (sandbox, restored) round-trip cell <-> level;
    //   F2 every ground cell x level: Column.PlaneSolid / FloorAt / BlockAt / Top equal the old Heights.Plane / HasFloor
    //      / sight / ColumnTop code (copied below), over a fixture with slabs on two levels, a wall and a roof upstairs; a
    //      sample of sight lines between levels (Los3D) equals Los3D reading the old raw rows;
    //   F3 floor kinds: slab, ship deck (Odyssey), none, natural rock (rough and smooth granite put in a band: its ceiling
    //      roof is the thick rock roof), Column.NaturalTop;
    //   F4 build vs use: over a thick roof BuildBlocked / SlabColumnBlocked refuse, LandingBlocked refuses open air and
    //      allows a natural rock floor; real flow: colonists build stairs S1 (open field, control) and S3 (landing on a
    //      natural rock floor over a thick roof, the "use" case), never S2 (its landing's column got a thick roof after
    //      the blueprint: construction refused), the designator refuses S4 (thick roof there first); a colonist walks up
    //      S3 onto the rock floor;
    //   F5 the elevation was captured (one byte per ground cell), every thick-roofed cell is over vanilla's thick-roof
    //      threshold, the stand-in for old maps is deterministic; a real save and load keeps the bytes;
    //   performance: things, mineables, regions, rooms, thick roofs, tick ms (600 ticks), frame ms (paused, 180 frames),
    //      save s and KB, load s, managed MB (PERF lines in results.txt).
    // Without floors (-projectzfloors=1) only the performance part runs.
    public static partial class TestRunner
    {
        private static CellRect mtArea;
        private static IntVec3 mtO, mtS1, mtS2, mtS3, mtS4, mtTop3, mtThickG;
        private static byte[] mtElev;
        private static int mtThings, mtFrames, mtMine, mtBuildings;
        private static float mtFrameSum;
        private static double mtSaveS;
        private static long mtSaveBytes;
        private static readonly List<string> mtPerf = new List<string>();

        private static IntVec3 MtC(int dx, int dz) => new IntVec3(mtO.x + dx, 0, mtO.z + dz);

        // -projectzhilliness=Impassable (modes mtn, tallmtn): the tile's hilliness (default Mountainous).
        private static Hilliness MtHilliness => GenCommandLine.TryGetCommandLineArg("projectzhilliness", out string hs) && System.Enum.TryParse(hs, out Hilliness hv) ? hv : Hilliness.Mountainous;

        // SetupPostfix (mode mtn): the starting tile, mountainous (a temperate forest one if there is one).
        private static void MtnTile()
        {
            SurfaceTile best = null;
            foreach (SurfaceTile tile in Find.WorldGrid.Tiles.InRandomOrder())
            {
                // (impassable tiles are never valid for a new settlement: any land tile of that hilliness, for the test)
                if (tile.hilliness != MtHilliness || (MtHilliness == Hilliness.Impassable ? tile.WaterCovered : !TileFinder.IsValidTileForNewSettlement(tile.tile)))
                {
                    continue;
                }
                if (tile.PrimaryBiome == BiomeDefOf.TemperateForest)
                {
                    best = tile;
                    break;
                }
                best = best ?? tile;
            }
            if (best != null)
            {
                Find.GameInitData.startingTile = best.tile;
            }
            Out($"mtn tile: {best?.tile.ToString() ?? "none found (game default)"} {best?.PrimaryBiome?.defName} {best?.hilliness}");
        }

        // ---------------------------------------------------------------- the old code (before F1/F2), for comparison

        private static int OFloorOf(IntVec3 c, AnnexMapComponent a) => c.z < a.groundZ ? 0 : (c.z % a.stride < a.groundZ ? c.z / a.stride : -1);

        private static bool OIsVoid(IntVec3 c, AnnexMapComponent a) => c.z >= a.groundZ && c.z % a.stride >= a.groundZ;

        private static IntVec3 OGround(IntVec3 c, AnnexMapComponent a)
        {
            if (c.z < a.groundZ)
            {
                return c;
            }
            int zg = c.z % a.stride;
            return zg < a.groundZ ? new IntVec3(c.x, c.y, zg) : IntVec3.Invalid;
        }

        private static IntVec3 OCell(IntVec3 g, int k, AnnexMapComponent a)
        {
            if (k == 0)
            {
                return g;
            }
            if (k < 0 || k >= a.floors || g.z < 0 || g.z >= a.groundZ)
            {
                return IntVec3.Invalid;
            }
            return new IntVec3(g.x, g.y, g.z + k * a.stride);
        }

        private static bool OHasFloor(IntVec3 c, Map map, AnnexMapComponent a) => c.IsValid && OFloorOf(c, a) >= 1 && map.terrainGrid.TerrainAt(c) != PZDefOf.PZ_OpenAir;

        private static Vector3 OVirtual(Vector3 p, AnnexMapComponent a)
        {
            if (p.z < a.groundZ)
            {
                return new Vector3(p.x, 0f, p.z);
            }
            int k = Mathf.FloorToInt(p.z) / a.stride;
            return new Vector3(p.x, k * Heights.Storey, p.z - k * a.stride);
        }

        private static bool OOpenAbove(IntVec3 c, Map map)
        {
            RoofDef r = map.roofGrid.RoofAt(c);
            return r == null || r == PZDefOf.PZ_StairwellRoof;
        }

        private static bool OPlane(IntVec3 g, int k, Map map, AnnexMapComponent a)
        {
            IntVec3 below = OCell(g, k - 1, a);
            if (below.IsValid && !OOpenAbove(below, map))
            {
                return true;
            }
            return k < a.floors && OHasFloor(OCell(g, k, a), map, a);
        }

        private static bool OBlock(IntVec3 g, int k, Map map, AnnexMapComponent a)
        {
            IntVec3 c = OCell(g, k, a);
            return c.IsValid && c.InBounds(map) && !c.CanBeSeenOverFast(map);
        }

        private static IntVec3 OColumnTop(IntVec3 c, Map map, AnnexMapComponent a)
        {
            IntVec3 g = OGround(c, a);
            if (!g.IsValid)
            {
                return IntVec3.Invalid;
            }
            for (int k = a.floors - 1; k >= 1; k--)
            {
                IntVec3 u = OCell(g, k, a);
                if (OHasFloor(u, map, a))
                {
                    return u;
                }
            }
            return g;
        }

        // Los3D as it read the columns before F2 (master's raw-stride adapter: OCell, OPlane, OBlock, floors 0..floors-1),
        // the same walk (Los3D.Walk), cell clipping and heights.
        private static bool OLos(IntVec3 from, IntVec3 to, Map map, AnnexMapComponent a)
        {
            int ks = OFloorOf(from, a), kt = OFloorOf(to, a);
            IntVec3 gs = OGround(from, a), gt = OGround(to, a);
            if (ks < 0 || kt < 0 || !gs.IsValid || !gt.IsValid)
            {
                return false;
            }
            float storey = IsoRimBridge.StoreyHeight, wall = IsoRimBridge.WallHeight;
            float hs = ks * storey + Los3D.Eye;
            List<IntVec3> cells = new List<IntVec3>();
            Los3D.Walk(gs, gt, 0, 0, cells);
            foreach (float aim in new[] { Los3D.AimHead, Los3D.AimChest, Los3D.AimWaist })
            {
                float ht = kt * storey + aim;
                bool blocked = false;
                for (int i = 0; i < cells.Count && !blocked; i++)
                {
                    IntVec3 g = cells[i];
                    OClip(gs.x, gs.z, gt.x, gt.z, g.x, g.z, out float t0, out float t1);
                    float h0 = hs + (ht - hs) * t0, h1 = hs + (ht - hs) * t1;
                    float lo = Mathf.Min(h0, h1), hi = Mathf.Max(h0, h1);
                    int kLo = Mathf.FloorToInt(lo / storey), kHi = Mathf.FloorToInt(hi / storey);
                    for (int k = Mathf.Max(1, kLo); k <= kHi && k <= a.floors && !blocked; k++)
                    {
                        blocked = lo < k * storey && k * storey < hi && OPlane(g, k, map, a);
                    }
                    if (i == 0 || i == cells.Count - 1)
                    {
                        continue;
                    }
                    for (int k = Mathf.Max(0, kLo); k <= kHi && k < a.floors && !blocked; k++)
                    {
                        blocked = hi >= k * storey && lo <= k * storey + wall && OBlock(g, k, map, a);
                    }
                }
                if (!blocked)
                {
                    return true;
                }
            }
            return false;
        }

        private static void OClip(float x0, float z0, float x1, float z1, int cx, int cz, out float t0, out float t1)
        {
            t0 = 0f;
            t1 = 1f;
            float dx = x1 - x0, dz = z1 - z0;
            if (OClipEdge(-dx, x0 - (cx - 0.5f), ref t0, ref t1) && OClipEdge(dx, (cx + 0.5f) - x0, ref t0, ref t1)
                && OClipEdge(-dz, z0 - (cz - 0.5f), ref t0, ref t1) && OClipEdge(dz, (cz + 0.5f) - z0, ref t0, ref t1))
            {
                return;
            }
            float len2 = dx * dx + dz * dz;
            float t = len2 < 1e-6f ? 1f : Mathf.Clamp01(((cx - x0) * dx + (cz - z0) * dz) / len2);
            t0 = t1 = t;
        }

        private static bool OClipEdge(float p, float q, ref float t0, ref float t1)
        {
            if (Mathf.Abs(p) < 1e-7f)
            {
                return q >= 0f;
            }
            float r = q / p;
            if (p < 0f)
            {
                if (r > t1)
                {
                    return false;
                }
                t0 = Mathf.Max(t0, r);
            }
            else
            {
                if (r < t0)
                {
                    return false;
                }
                t1 = Mathf.Min(t1, r);
            }
            return true;
        }

        // ---------------------------------------------------------------- stages

        private static bool StepMtn(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            switch (stage)
            {
                case "mt_setup":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    DebugSettings.enableRandomMentalStates = false;
                    Info($"mtn: map {map.Size}, tile {map.Tile} {map.TileInfo?.PrimaryBiome?.defName} {map.TileInfo?.hilliness}, floors {a?.floors ?? 1}, ground rows {a?.groundZ ?? map.Size.z}, stride {a?.stride ?? 0}");
                    MtPerfMap(map, "after generation");
                    MtTicks(map);
                    mtFrames = 0;
                    mtFrameSum = 0f;
                    stage = "mt_frames";
                    return true;
                }
                case "mt_frames":
                    if (mtFrames > 0)
                    {
                        mtFrameSum += Time.unscaledDeltaTime;
                    }
                    if (++mtFrames <= 180)
                    {
                        return true;
                    }
                    mtPerf.Add($"frame ms (paused, 180 frames, window hidden): {mtFrameSum / 180f * 1000f:0.00}");
                    Info($"PERF frame ms (paused, 180 frames, window hidden): {mtFrameSum / 180f * 1000f:0.00}");
                    if (a == null)
                    {
                        stage = "mt_save";
                        return true;
                    }
                    MtF6(map, a);
                    MtF1(map, a);
                    MtF5(map, a);
                    MtFixture(map, a);
                    ShotAt(map, 0, MtC(18, 6), 14f);
                    Wait(15, "mt_f2");
                    return true;
                case "mt_f2":
                    MtF2(map, a);
                    MtF3(map, a);
                    MtF4Queries(map, a);
                    MtStairs(map, a);
                    return true;
                case "mt_built":
                {
                    Building s1 = MtStairsAt(map, mtS1), s2 = MtStairsAt(map, mtS2), s3 = MtStairsAt(map, mtS3);
                    bool s2Planned = mtS2.GetThingList(map).Any(t => t is Blueprint && t.def.entityDefToBuild == PZDefOf.PZ_Stairs);
                    bool s2Frame = mtS2.GetThingList(map).Any(t => t is Frame);
                    IntVec3 b2 = FloorRoof.Below(StairShape.Exit(mtS2, Rot4.North, map), map);
                    Result("F4 real flow: colonists built S1 (control) and S3 (landing on a natural rock floor over a thick roof)", s1 != null && s3 != null, $"S1 {s1?.Position}, S3 {s3?.Position}");
                    Result("F4 real flow: S2 (landing over a thick roof, made after its blueprint) never built", s2 == null && s2Planned && !s2Frame && (map.roofGrid.RoofAt(b2)?.isThickRoof ?? false),
                        $"stairs {s2?.Position.ToString() ?? "none"}, blueprint {s2Planned}, frame {s2Frame}, roof under the landing {map.roofGrid.RoofAt(b2)?.defName}; CanConstruct {(Colonists(map).Count > 0 ? MtCanConstruct(map, mtS2).ToString() : "-")}");
                    IntVec3 b3 = FloorRoof.Below(mtTop3, map);
                    Result("F4 real flow: S3's landing is still the natural rock floor, its thick roof below kept", Floors.KindAt(mtTop3, map) == FloorKind.NaturalRock && (map.roofGrid.RoofAt(b3)?.isThickRoof ?? false),
                        $"top {mtTop3} {mtTop3.GetTerrain(map)?.defName} ({Floors.KindAt(mtTop3, map)}), roof below {map.roofGrid.RoofAt(b3)?.defName}");
                    Pawn walker = Colonists(map).FirstOrDefault(p => !p.Downed && p.health.capacities.CapableOf(PawnCapacityDefOf.Moving));
                    if (walker == null || s3 == null)
                    {
                        Result("F4 real flow: a colonist walks up S3", false, "no walker or no S3");
                        stage = "mt_save";
                        return true;
                    }
                    SetWork(map, false);
                    walker.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, mtTop3), JobTag.Misc);
                    mtWalker = walker;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(6000, () => mtWalker.Position == mtTop3, "mt_walked", 30);
                    return true;
                }
                case "mt_walked":
                    Result("F4 real flow: a colonist walked up S3 onto the natural rock floor", mtWalker.Position == mtTop3, $"{mtWalker} at {mtWalker.Position} (level {ProjectZ.FloorOf(mtWalker.Position, map)}), target {mtTop3}");
                    ShotAt(map, 1, MtC(18, 6), 14f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(20, "mt_shot");
                    return true;
                case "mt_shot":
                    Capture("mtn_stairs_f2");
                    Wait(5, "mt_save");
                    return true;
                case "mt_save":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    mtElev = a?.elevation == null ? null : (byte[])a.elevation.Clone();
                    mtThings = map.listerThings.AllThings.Count;
                    mtMine = map.listerThings.AllThings.Count(t => t.def.mineable);
                    mtBuildings = map.listerThings.AllThings.Count(t => t is Building);
                    Stopwatch sw = Stopwatch.StartNew();
                    GameDataSaveLoader.SaveGame("pz_mtn");
                    mtSaveS = sw.Elapsed.TotalSeconds;
                    string path = GenFilePaths.FilePathForSavedGame("pz_mtn");
                    mtSaveBytes = File.Exists(path) ? new FileInfo(path).Length : -1;
                    Info($"PERF save s {mtSaveS:0.00}, save KB {mtSaveBytes / 1024}");
                    mtPerf.Add($"save s {mtSaveS:0.00}, save KB {mtSaveBytes / 1024}");
                    if (a?.elevation != null)
                    {
                        int deflated = CompressUtility.Compress(a.elevation).Length;
                        Info($"PERF elevation in the save: {a.elevation.Length} bytes, deflated {deflated} (base64 ~{deflated * 4 / 3 / 1024} KB)");
                        mtPerf.Add($"elevation {a.elevation.Length} B raw, ~{deflated * 4 / 3 / 1024} KB in the save");
                    }
                    stage = "mt_loaded";
                    pendingLoad = "pz_mtn";
                    return true;
                }
                case "mt_loaded":
                {
                    Info($"PERF load s {LastLoadSeconds:0.00} (LoadGame to the first update of the loaded game)");
                    mtPerf.Add($"load s {LastLoadSeconds:0.00}");
                    MtPerfMap(map, "after load");
                    if (a != null)
                    {
                        Result("F5 save + load: the elevation bytes are the same", a.elevation != null && mtElev != null && a.elevation.SequenceEqual(mtElev), $"{a.elevation?.Length} bytes after, {mtElev?.Length} before");
                        Result("F1 save + load: layoutVersion 2, identity table", a.layoutVersion == 2 && a.bandLevel == null && a.MinLevel == 0 && a.MaxLevel == a.floors - 1, $"version {a.layoutVersion}, table {(a.bandLevel == null ? "none" : string.Join(",", a.bandLevel))}, levels {a.MinLevel}..{a.MaxLevel}");
                        int mineAfter = map.listerThings.AllThings.Count(t => t.def.mineable), bldAfter = map.listerThings.AllThings.Count(t => t is Building);
                        Result("save + load: same mineables and buildings", mineAfter == mtMine && bldAfter == mtBuildings, $"mineables {mtMine} -> {mineAfter}, buildings {mtBuildings} -> {bldAfter} (all things {mtThings} -> {map.listerThings.AllThings.Count})");
                    }
                    Info("PERF SUMMARY | " + string.Join(" | ", mtPerf));
                    Finish();
                    return true;
                }
            }
            return false;
        }

        private static Pawn mtWalker;

        private static Building MtStairsAt(Map map, IntVec3 c) => map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).OfType<Building>().FirstOrDefault(t => t.Position == c);

        private static bool MtCanConstruct(Map map, IntVec3 c)
        {
            Thing bp = c.GetThingList(map).FirstOrDefault(t => t is Blueprint);
            Pawn p = Colonists(map).FirstOrDefault(x => !x.WorkTypeIsDisabled(WorkTypeDefOf.Construction));
            return bp != null && p != null && GenConstruct.CanConstruct(bp, p, false, true);
        }

        // ---------------------------------------------------------------- performance

        private static void MtPerfMap(Map map, string when)
        {
            int things = map.listerThings.AllThings.Count;
            int mine = map.listerThings.AllThings.Count(t => t.def.mineable);
            int regions = map.regionGrid.AllRegions.Count();
            int rooms = map.regionGrid.AllRooms.Count;
            int gz = ProjectZ.GroundZ(map), thick = 0;
            for (int z = 0; z < gz; z++)
            {
                for (int x = 0; x < map.Size.x; x++)
                {
                    thick += map.roofGrid.RoofAt(new IntVec3(x, 0, z))?.isThickRoof ?? false ? 1 : 0;
                }
            }
            long mb = GC.GetTotalMemory(false) / (1024 * 1024);
            string s = $"{when}: things {things}, mineables {mine}, regions {regions}, rooms {rooms}, thick roofs {thick}, managed MB {mb}";
            Info("PERF " + s);
            mtPerf.Add(s);
        }

        private static void MtTicks(Map map)
        {
            TickManager tm = Find.TickManager;
            for (int i = 0; i < 60; i++)
            {
                tm.DoSingleTick();   // warm-up
            }
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < 600; i++)
            {
                tm.DoSingleTick();
            }
            double ms = sw.Elapsed.TotalMilliseconds / 600.0;
            Info($"PERF tick ms (600 ticks, colonists as generated): {ms:0.000}");
            mtPerf.Add($"tick ms {ms:0.000}");
        }

        // ---------------------------------------------------------------- F6

        private static void MtF6(Map map, AnnexMapComponent a)
        {
            int differ = 0, n = 0;
            CellIndices ci = map.cellIndices;
            for (int i = ci.CellToIndex(new IntVec3(0, 0, a.groundZ)); i < ci.NumGridCells; i++)
            {
                IntVec3 c = ci.IndexToCell(i);
                AnnexGen.AnnexCell want = AnnexGen.Rule(c, map);
                if (map.terrainGrid.topGrid[i] != want.terrain || map.fogGrid.IsFogged(i) != want.fogged)
                {
                    differ++;
                }
                n++;
            }
            int changed = AnnexGen.FillAnnex(map, a.groundZ, withPathCosts: false);
            Result("F6 the annex as generated is the fill rule's (open air, unfogged); FillAnnex again changes nothing", AnnexGen.Rule == (AnnexGen.FillRule)AnnexGen.OpenAir && differ == 0 && changed == 0,
                $"{n} annex cells, {differ} differ from the rule, FillAnnex changed {changed}");
        }

        // ---------------------------------------------------------------- F1

        private static void MtF1(Map map, AnnexMapComponent a)
        {
            int bad = 0, cells = 0;
            string first = null;
            void Bad(string what)
            {
                bad++;
                first = first ?? what;
            }
            for (int z = 0; z < map.Size.z; z++)
            {
                for (int x = 0; x < map.Size.x; x++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    cells++;
                    if (ProjectZ.FloorOf(c, map) != OFloorOf(c, a))
                    {
                        Bad($"FloorOf {c}");
                    }
                    if (ProjectZ.IsVoid(c, map) != OIsVoid(c, a))
                    {
                        Bad($"IsVoid {c}");
                    }
                    if (ProjectZ.GroundCellOf(c, map) != OGround(c, a))
                    {
                        Bad($"GroundCellOf {c}");
                    }
                    Vector3 p = new Vector3(x + 0.5f, 0f, z + 0.3f);
                    if ((ProjectZ.Virtual(p, map) - OVirtual(p, a)).sqrMagnitude > 1e-8f)
                    {
                        Bad($"Virtual {p}");
                    }
                    int L = Levels.LevelOf(c, map);
                    if (OIsVoid(c, a) ? L != Levels.None : L != OFloorOf(c, a))
                    {
                        Bad($"LevelOf {c}");
                    }
                }
            }
            for (int z = 0; z < a.groundZ; z++)
            {
                for (int x = 0; x < map.Size.x; x++)
                {
                    IntVec3 g = new IntVec3(x, 0, z);
                    for (int k = -1; k <= a.floors; k++)
                    {
                        if (ProjectZ.CellOnFloor(g, k, map) != OCell(g, k, a))
                        {
                            Bad($"CellOnFloor {g} {k}");
                        }
                    }
                }
            }
            for (int k = 0; k < a.floors; k++)
            {
                if (Levels.Shift(map, k) != k * a.stride || Levels.RowMin(map, k) != k * a.stride || Levels.RowMax(map, k) != k * a.stride + a.groundZ - 1
                    || Levels.BandOf(map, k) != k || Levels.LevelOfBand(map, k) != k || !Levels.IsAssigned(map, k))
                {
                    Bad($"band/level {k}");
                }
            }
            Result("F1 identity: every cell's level, void, ground cell, virtual position and every column cell equal the old stride formulas", bad == 0,
                $"{cells} cells, {a.groundZ * map.Size.x} columns x {a.floors + 2} levels; {bad} differ{(first == null ? "" : ", first " + first)}");
            Result("F1 table: identity (none saved), layoutVersion 2, levels 0..floors-1", a.bandLevel == null && a.layoutVersion == AnnexMapComponent.CurrentLayoutVersion && AnnexMapComponent.CurrentLayoutVersion == 2
                && Levels.Min(map) == 0 && Levels.Max(map) == a.floors - 1 && Levels.Count(map) == a.floors && Levels.All(map).SequenceEqual(Enumerable.Range(0, a.floors)),
                $"table {(a.bandLevel == null ? "none" : string.Join(",", a.bandLevel))}, version {a.layoutVersion}, levels {Levels.Min(map)}..{Levels.Max(map)}");
            int n = a.floors;
            List<List<int>> badTables = new List<List<int>>
            {
                Enumerable.Repeat(0, n).ToList(),                          // duplicates
                Enumerable.Range(1, n).ToList(),                           // band 0 not level 0
                Enumerable.Range(0, n).Select(i => i == n - 1 ? n : i).ToList(), // a gap
                Enumerable.Range(0, n + 1).ToList(),                       // wrong count
            };
            bool refused = badTables.All(t => !AnnexMapComponent.ValidTable(t, n) && !a.SetBandLevels(t)) && a.bandLevel == null;
            Result("F1 bad tables are refused (duplicates, band 0 not level 0, a gap, wrong count), nothing assigned", refused, refused ? "all refused" : "one was taken");
            if (n >= 3)
            {
                // sandbox: a shuffled and a signed table, round trips, then back to the identity
                List<int> shuffled = Enumerable.Range(0, n).ToList();
                shuffled[1] = 2;
                shuffled[2] = 1;
                List<int> signed = Enumerable.Range(0, n).Select(i => i == 1 ? -1 : i == 0 ? 0 : i - 1).ToList();
                string why = null;
                foreach (List<int> t in new[] { shuffled, signed })
                {
                    if (!a.SetBandLevels(t))
                    {
                        why = why ?? $"table {string.Join(",", t)} refused";
                        continue;
                    }
                    int lo = t.Min(), hi = t.Max();
                    if (Levels.Min(map) != lo || Levels.Max(map) != hi)
                    {
                        why = why ?? $"levels {Levels.Min(map)}..{Levels.Max(map)} for {string.Join(",", t)}";
                    }
                    for (int b = 0; b < n; b++)
                    {
                        int L = t[b];
                        IntVec3 g = new IntVec3(map.Size.x / 2, 0, a.groundZ / 2);
                        IntVec3 c = Levels.Cell(g, L, map);
                        if (c.z != g.z + b * a.stride || Levels.LevelOf(c, map) != L || Levels.Ground(c, map) != g || Levels.BandOf(map, L) != b || Levels.Shift(map, L) != b * a.stride
                            || ProjectZ.FloorOf(c, map) != L && L >= 0 || Levels.LevelOfRowLoose(map, b * a.stride + a.groundZ) != L)
                        {
                            why = why ?? $"table {string.Join(",", t)}: band {b} level {L} cell {c} -> level {Levels.LevelOf(c, map)}, ground {Levels.Ground(c, map)}";
                        }
                    }
                }
                a.SetBandLevels(null);
                bool back = a.bandLevel == null && Levels.Min(map) == 0 && Levels.Max(map) == n - 1 && Levels.Cell(new IntVec3(1, 0, 1), 1, map).z == 1 + a.stride;
                Result("F1 sandbox: a shuffled and a signed band table round-trip cell <-> level, then the identity is back", why == null && back,
                    why ?? $"[{string.Join(",", shuffled)}] and [{string.Join(",", signed)}] ok; identity back {back}");
            }
        }

        // ---------------------------------------------------------------- F5

        private static void MtF5(Map map, AnnexMapComponent a)
        {
            int sx = map.Size.x, gz = a.groundZ;
            bool captured = NaturalElevation.Captured(map);
            int thick = 0, low = 0, rock = 0;
            float min = 9f, max = -9f, rockMin = 9f;
            for (int z = 0; z < gz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    IntVec3 g = new IntVec3(x, 0, z);
                    float e = NaturalElevation.At(g, map);
                    min = Mathf.Min(min, e);
                    max = Mathf.Max(max, e);
                    if (map.roofGrid.RoofAt(g)?.isThickRoof ?? false)
                    {
                        thick++;
                        if (e < NaturalElevation.ThickRoof - 0.004f)
                        {
                            low++;
                        }
                    }
                    Building b = g.GetEdifice(map);
                    if (b != null && b.def.building != null && b.def.building.isNaturalRock)
                    {
                        rock++;
                        rockMin = Mathf.Min(rockMin, e);
                    }
                }
            }
            Result("F5 elevation captured at generation: one byte per ground cell, every thick-roofed cell over vanilla's thick-roof threshold",
                captured && a.elevation.Length == sx * gz && thick > 0 && low == 0,
                $"captured {captured}, {a.elevation?.Length} bytes for {sx}x{gz}; elevation {min:0.000}..{max:0.000}; thick-roofed cells {thick}, {low} under {NaturalElevation.ThickRoof:0.000}; natural rock cells {rock} (lowest {rockMin:0.000})");
            // the stand-in for maps made before F5: deterministic, thick cells high
            byte[] keep = a.elevation;
            a.elevation = null;
            float sumDiff = 0f, standMin = 9f;
            int nThick = 0;
            bool same = true;
            for (int z = 0; z < gz; z += 3)
            {
                for (int x = 0; x < sx; x += 3)
                {
                    IntVec3 g = new IntVec3(x, 0, z);
                    float s1 = NaturalElevation.At(g, map), s2 = NaturalElevation.At(g, map);
                    same &= s1 == s2;
                    if (map.roofGrid.RoofAt(g)?.isThickRoof ?? false)
                    {
                        nThick++;
                        standMin = Mathf.Min(standMin, s1);
                        sumDiff += Mathf.Abs(s1 - NaturalElevation.Dequantise(keep[z * sx + x]));
                    }
                }
            }
            a.elevation = keep;
            Result("F5 stand-in (maps without a capture): deterministic, thick-roofed cells over the thin-roof threshold", same && (nThick == 0 || standMin > NaturalElevation.ThinRoof) && NaturalElevation.Captured(map),
                $"same twice {same}; {nThick} thick cells sampled, lowest {standMin:0.000}, mean |stand-in - captured| {(nThick == 0 ? 0f : sumDiff / nThick):0.000}");
        }

        // ---------------------------------------------------------------- fixture

        private static void MtFixture(Map map, AnnexMapComponent a)
        {
            int gz = a.groundZ;
            const int W = 34, H = 24;
            // the open-most spot near the middle: fewest natural rock and roofed cells
            CellRect best = CellRect.Empty;
            int bestScore = int.MaxValue;
            for (int z0 = 8; z0 + H + 8 < gz; z0 += 6)
            {
                for (int x0 = 8; x0 + W + 8 < map.Size.x; x0 += 6)
                {
                    CellRect r = new CellRect(x0, z0, W, H);
                    int score = 0;
                    foreach (IntVec3 c in r)
                    {
                        if (c.GetEdifice(map) != null || c.Roofed(map) || c.GetTerrain(map).IsWater)
                        {
                            score++;
                        }
                    }
                    score = score * 4 + (Mathf.Abs(r.CenterCell.x - map.Size.x / 2) + Mathf.Abs(r.CenterCell.z - gz / 2)) / 8;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = r;
                    }
                }
            }
            mtArea = best;
            TerrainDef soil = DefDatabase<TerrainDef>.GetNamed("Soil");
            foreach (IntVec3 c in mtArea.ExpandedBy(2))
            {
                map.roofGrid.SetRoof(c, null);
            }
            foreach (IntVec3 c in mtArea.ExpandedBy(2))
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable && !(t is Pawn))
                    {
                        t.Destroy();
                    }
                }
                if (c.GetTerrain(map).IsWater || c.GetTerrain(map).passability != Traversability.Standable || !c.GetTerrain(map).affordances.Any(af => af.defName == "Heavy"))
                {
                    map.terrainGrid.SetTerrain(c, soil);
                }
                map.fogGrid.Unfog(c);
            }
            map.roofCollapseBuffer.Clear();
            Find.PlaySettings.autoRebuild = false;
            mtO = new IntVec3(mtArea.minX + 2, 0, mtArea.minZ + 2);
            // F2 variety upstairs: slabs on level 1 (6x6) and level 2 (3x3) over the west part, a wall on level 1, a
            // built roof on a level-1 cell; ground walls holding them
            foreach ((int x, int z) in new[] { (0, 12), (5, 12), (0, 17), (5, 17) })
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, MtC(x, z), map);
            }
            for (int x = 0; x < 6; x++)
            {
                for (int z = 12; z < 18; z++)
                {
                    map.terrainGrid.SetTerrain(Levels.Cell(MtC(x, z), 1, map), PZDefOf.PZ_UpperFloor);
                }
            }
            if (a.MaxLevel >= 2)
            {
                for (int x = 1; x < 4; x++)
                {
                    for (int z = 13; z < 16; z++)
                    {
                        map.terrainGrid.SetTerrain(Levels.Cell(MtC(x, z), 2, map), PZDefOf.PZ_UpperFloor);
                    }
                }
            }
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, Levels.Cell(MtC(5, 15), 1, map), map);
            map.roofGrid.SetRoof(Levels.Cell(MtC(4, 16), 1, map), RoofDefOf.RoofConstructed);
            // stairs sites (all facing north, landings 2 north of the centre on level 1)
            mtS1 = MtC(12, 3);
            mtS2 = MtC(16, 3);
            mtS3 = MtC(20, 3);
            mtS4 = MtC(24, 3);
            mtTop3 = StairShape.Exit(mtS3, Rot4.North, map);
            for (int i = 0; i < 3; i++)
            {
                Thing w = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                w.stackCount = 75;
                GenPlace.TryPlaceThing(w, MtC(16, -1), map, ThingPlaceMode.Near);
            }
            SetWork(map, false);
            List<Pawn> cols = Colonists(map);
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], MtC(10 + i, -1));
            }
            // a real thick-roofed mountain cell away from the fixture (F4 queries)
            mtThickG = IntVec3.Invalid;
            for (int z = 0; z < gz && !mtThickG.IsValid; z++)
            {
                for (int x = 0; x < map.Size.x; x++)
                {
                    IntVec3 g = new IntVec3(x, 0, z);
                    if ((map.roofGrid.RoofAt(g)?.isThickRoof ?? false) && !mtArea.ExpandedBy(4).Contains(g))
                    {
                        mtThickG = g;
                        break;
                    }
                }
            }
            Info($"mtn fixture: area {mtArea} (score {bestScore}), stairs S1 {mtS1} S2 {mtS2} S3 {mtS3} (landing {mtTop3}) S4 {mtS4}, thick mountain cell {mtThickG}");
        }

        // ---------------------------------------------------------------- F2

        private static void MtF2(Map map, AnnexMapComponent a)
        {
            int bad = 0, checks = 0;
            string first = null;
            int planes = 0, floorsUp = 0, blocks = 0;
            for (int z = 0; z < a.groundZ; z++)
            {
                for (int x = 0; x < map.Size.x; x++)
                {
                    IntVec3 g = new IntVec3(x, 0, z);
                    for (int L = 0; L <= a.floors; L++)
                    {
                        if (L >= 1)
                        {
                            bool n = Column.PlaneSolid(g, L, map), o = OPlane(g, L, map, a);
                            planes += n ? 1 : 0;
                            checks++;
                            if (n != o)
                            {
                                bad++;
                                first = first ?? $"PlaneSolid {g} L{L}: {n} vs {o}";
                            }
                        }
                        if (L < a.floors)
                        {
                            bool nf = Column.FloorAt(g, L, map), of = L == 0 || OHasFloor(OCell(g, L, a), map, a);
                            bool nb = Column.BlockAt(g, L, map), ob = OBlock(g, L, map, a);
                            floorsUp += nf && L > 0 ? 1 : 0;
                            blocks += nb ? 1 : 0;
                            checks += 2;
                            if (nf != of || nb != ob)
                            {
                                bad++;
                                first = first ?? $"FloorAt/BlockAt {g} L{L}: {nf}/{nb} vs {of}/{ob}";
                            }
                        }
                    }
                    checks++;
                    if (Column.Top(g, map) != OColumnTop(g, map, a) || ProjectZ.ColumnTop(g, map) != OColumnTop(g, map, a))
                    {
                        bad++;
                        first = first ?? $"Top {g}: {Column.Top(g, map)} vs {OColumnTop(g, map, a)}";
                    }
                }
            }
            Result("F2 Column queries equal the old code over every column and level (PlaneSolid, FloorAt, BlockAt, Top)", bad == 0 && floorsUp > 0 && blocks > 0 && planes > 0,
                $"{checks} checks; solid planes {planes}, upper floors {floorsUp}, blocks {blocks}; {bad} differ{(first == null ? "" : ", first " + first)}");
            // sight lines between levels: half around the fixture, half anywhere
            int lines = 0, diff = 0, seen = 0;
            string firstLine = null;
            for (int i = 0; i < 3000; i++)
            {
                IntVec3 g1, g2;
                if (i % 2 == 0)
                {
                    g1 = new IntVec3(Rand.RangeInclusive(mtArea.minX - 6, mtArea.maxX + 6), 0, Rand.RangeInclusive(mtArea.minZ - 6, mtArea.maxZ + 6));
                    g2 = g1 + new IntVec3(Rand.RangeInclusive(-14, 14), 0, Rand.RangeInclusive(-14, 14));
                }
                else
                {
                    g1 = new IntVec3(Rand.Range(0, map.Size.x), 0, Rand.Range(0, a.groundZ));
                    g2 = g1 + new IntVec3(Rand.RangeInclusive(-30, 30), 0, Rand.RangeInclusive(-30, 30));
                }
                if (g2.x < 0 || g2.z < 0 || g2.x >= map.Size.x || g2.z >= a.groundZ || g1.x < 0 || g1.z < 0 || g1.x >= map.Size.x || g1.z >= a.groundZ)
                {
                    continue;
                }
                IntVec3 from = Levels.Cell(g1, Rand.Range(0, Mathf.Min(a.floors, 3)), map);
                IntVec3 to = Levels.Cell(g2, Rand.Range(0, Mathf.Min(a.floors, 3)), map);
                bool n = Los3D.CanSee(from, to, map), o = OLos(from, to, map, a);
                lines++;
                seen += n ? 1 : 0;
                if (n != o)
                {
                    diff++;
                    firstLine = firstLine ?? $"{from} -> {to}: {n} vs {o}";
                }
            }
            Result("F2 sight lines between levels (Los3D over the column queries) equal Los3D over the old raw rows", diff == 0 && lines > 1000, $"{lines} lines, {seen} clear, {diff} differ{(firstLine == null ? "" : ", first " + firstLine)}");
        }

        // ---------------------------------------------------------------- F3

        private static void MtF3(Map map, AnnexMapComponent a)
        {
            IntVec3 slab = Levels.Cell(MtC(2, 14), 1, map);
            IntVec3 air = Levels.Cell(MtC(10, 14), 1, map);
            IntVec3 ground = MtC(10, 14);
            bool slabOk = Floors.KindAt(slab, map) == FloorKind.Slab && map.roofGrid.RoofAt(FloorRoof.Below(slab, map)) == PZDefOf.PZ_FloorRoof && Floors.CeilingUnder(slab, map) == PZDefOf.PZ_FloorRoof;
            bool noneOk = Floors.KindAt(air, map) == FloorKind.None && Floors.KindAt(ground, map) == FloorKind.None && Floors.CeilingFor(FloorKind.None) == null;
            // ship deck (Odyssey): on level 2 over open air, taken away again
            string deck = "no Odyssey";
            bool deckOk = true;
            if (PZDefOf.PZ_ShipDeck != null && a.MaxLevel >= 2)
            {
                IntVec3 d = Levels.Cell(MtC(8, 16), 2, map);
                map.terrainGrid.SetTerrain(d, PZDefOf.PZ_ShipDeck);
                FloorKind k = Floors.KindAt(d, map);
                RoofDef under = Floors.CeilingUnder(d, map);
                map.terrainGrid.RemoveFoundation(d, false);
                if (d.GetTerrain(map) != PZDefOf.PZ_OpenAir)
                {
                    map.terrainGrid.SetTerrain(d, PZDefOf.PZ_OpenAir);
                }
                map.roofGrid.SetRoof(FloorRoof.Below(d, map), null);
                deckOk = k == FloorKind.ShipDeck && under == PZDefOf.PZ_FloorRoof && Floors.KindAt(d, map) == FloorKind.None;
                deck = $"deck {k}, its ceiling {under?.defName}, after removal {Floors.KindAt(d, map)}";
            }
            // natural rock (rock-top floors): rough granite on level 1 over unroofed ground takes the floor roof (the support
            // roof, as under a slab); smooth granite over ground with a thin rock roof keeps that roof; a thick roof written
            // under it stays thick, a constructed one asked for keeps the thin one
            TerrainDef rough = DefDatabase<TerrainDef>.GetNamedSilentFail("Granite_Rough");
            TerrainDef smooth = DefDatabase<TerrainDef>.GetNamedSilentFail("Granite_Smooth");
            IntVec3 r1 = Levels.Cell(MtC(8, 12), 1, map), r2 = Levels.Cell(MtC(9, 12), 1, map);
            map.roofGrid.SetRoof(MtC(8, 12), null);
            map.roofGrid.SetRoof(MtC(9, 12), RoofDefOf.RoofRockThin);
            map.terrainGrid.SetTerrain(r1, rough);
            map.terrainGrid.SetTerrain(r2, smooth);
            FloorKind k1 = Floors.KindAt(r1, map), k2 = Floors.KindAt(r2, map);
            RoofDef u1 = map.roofGrid.RoofAt(MtC(8, 12)), u2 = map.roofGrid.RoofAt(MtC(9, 12));
            map.roofGrid.SetRoof(MtC(9, 12), RoofDefOf.RoofConstructed);
            RoofDef u2c = map.roofGrid.RoofAt(MtC(9, 12));
            map.roofGrid.SetRoof(MtC(9, 12), null);
            RoofDef u2n = map.roofGrid.RoofAt(MtC(9, 12));
            map.roofGrid.SetRoof(MtC(8, 12), RoofDefOf.RoofRockThick);
            RoofDef u1t = map.roofGrid.RoofAt(MtC(8, 12));
            map.roofGrid.SetRoof(MtC(8, 12), RoofDefOf.RoofConstructed);
            RoofDef u1tc = map.roofGrid.RoofAt(MtC(8, 12));
            bool rule = Floors.CeilingFor(FloorKind.NaturalRock) == PZDefOf.PZ_FloorRoof
                && Floors.CeilingFor(FloorKind.NaturalRock, RoofDefOf.RoofRockThick, null) == RoofDefOf.RoofRockThick
                && Floors.CeilingFor(FloorKind.NaturalRock, RoofDefOf.RoofConstructed, RoofDefOf.RoofRockThin) == RoofDefOf.RoofRockThin
                && Floors.CeilingFor(FloorKind.NaturalRock, null, PZDefOf.PZ_FloorRoof) == PZDefOf.PZ_FloorRoof
                && Floors.CeilingFor(FloorKind.Slab, RoofDefOf.RoofRockThin, RoofDefOf.RoofRockThin) == PZDefOf.PZ_FloorRoof;
            int top = Column.NaturalTop(MtC(8, 12), map), topNone = Column.NaturalTop(MtC(2, 14), map);
            bool natOk = rough != null && smooth != null && Floors.IsNatural(rough) && Floors.IsNatural(smooth) && !Floors.IsNatural(PZDefOf.PZ_UpperFloor)
                && k1 == FloorKind.NaturalRock && k2 == FloorKind.NaturalRock && u1 == PZDefOf.PZ_FloorRoof && u2 == RoofDefOf.RoofRockThin
                && u2c == RoofDefOf.RoofRockThin && u2n == RoofDefOf.RoofRockThin && u1t == RoofDefOf.RoofRockThick && u1tc == RoofDefOf.RoofRockThick && rule && top == 1 && topNone == 0;
            string natNote = $"thin kept on a constructed write {u2c?.defName}, on a removal {u2n?.defName}; thick written {u1t?.defName}, then constructed {u1tc?.defName}; rule {rule}";
            map.terrainGrid.SetTerrain(r1, PZDefOf.PZ_OpenAir);
            map.terrainGrid.SetTerrain(r2, PZDefOf.PZ_OpenAir);
            map.roofGrid.SetRoof(MtC(8, 12), null);
            map.roofGrid.SetRoof(MtC(9, 12), null);
            Result("F3 floor kinds and their ceilings: slab (floor roof), none (open air, ground), ship deck, natural rock (keeps a natural rock roof, else the floor roof), NaturalTop",
                slabOk && noneOk && deckOk && natOk,
                $"slab {Floors.KindAt(slab, map)} roof under {map.roofGrid.RoofAt(FloorRoof.Below(slab, map))?.defName}; air {Floors.KindAt(air, map)}, ground {Floors.KindAt(ground, map)}; {deck}; rough {k1} roof under {u1?.defName}, smooth {k2} roof under {u2?.defName}; {natNote}; NaturalTop {top} (slab column {topNone})");
        }

        // ---------------------------------------------------------------- F4

        private static void MtF4Queries(Map map, AnnexMapComponent a)
        {
            if (!mtThickG.IsValid)
            {
                Result("F4 queries", false, "no thick-roofed mountain cell on this map");
                return;
            }
            IntVec3 u = Levels.Cell(mtThickG, 1, map);
            string build = SlabSupport.BuildBlocked(mtThickG, map), slab = SlabSupport.SlabColumnBlocked(mtThickG, map), landAir = SlabSupport.LandingBlocked(u, map);
            AcceptanceReport place = PlaceWorker_UpperFloor.Check(u, map);
            TerrainDef rough = DefDatabase<TerrainDef>.GetNamed("Granite_Rough");
            map.terrainGrid.SetTerrain(u, rough);
            string landRock = SlabSupport.LandingBlocked(u, map), buildAfter = SlabSupport.BuildBlocked(mtThickG, map);
            RoofDef roofKept = map.roofGrid.RoofAt(mtThickG);
            map.terrainGrid.SetTerrain(u, PZDefOf.PZ_OpenAir);
            RoofDef roofAfter = map.roofGrid.RoofAt(mtThickG);
            Result("F4 queries over a mountain's thick roof: build refused (slab, column), landing on open air refused, landing on a natural rock floor allowed; the rock roof kept",
                build == "PZ_ThickRoof" && slab == "PZ_ThickRoof" && landAir == "PZ_ThickRoof" && !place.Accepted && landRock == null && buildAfter == "PZ_ThickRoof"
                && roofKept == RoofDefOf.RoofRockThick && roofAfter == RoofDefOf.RoofRockThick,
                $"at {mtThickG}: build {build}, slab {slab}, upper floor placement '{place.Reason}', landing open air {landAir}, landing on rock {landRock ?? "allowed"}, build then {buildAfter}; roof {roofKept?.defName} -> {roofAfter?.defName}");
        }

        private static void MtStairs(Map map, AnnexMapComponent a)
        {
            TerrainDef rough = DefDatabase<TerrainDef>.GetNamed("Granite_Rough");
            // S3's landing: a natural rock floor over a thick rock roof (a mountain top; laid over open ground it would take
            // the floor roof, F3); S4's landing column: thick first
            map.terrainGrid.SetTerrain(mtTop3, rough);
            map.roofGrid.SetRoof(FloorRoof.Below(mtTop3, map), RoofDefOf.RoofRockThick);
            IntVec3 b4 = FloorRoof.Below(StairShape.Exit(mtS4, Rot4.North, map), map);
            map.roofGrid.SetRoof(b4, RoofDefOf.RoofRockThick);
            Designator_Build d = new Designator_Build(PZDefOf.PZ_Stairs);
            d.SetStuffDef(ThingDefOf.WoodLog);
            Find.DesignatorManager.Select(d);
            SetRot(d, Rot4.North);
            string msg = "PZ_ThickRoof".Translate();
            List<string> got = new List<string>();
            bool allOk = true;
            foreach (IntVec3 s in new[] { mtS1, mtS2, mtS3 })
            {
                AcceptanceReport r = RefusalAt(d, ScreenOf(s, 0), out IntVec3 c);
                got.Add($"{s}: {(r.Accepted ? "ok" : r.Reason)} (pointer {c})");
                allOk &= r.Accepted && c == s;
                if (r.Accepted && c == s)
                {
                    UiDesignate(ScreenOf(s, 0), ScreenOf(s, 0));
                }
            }
            AcceptanceReport r4 = RefusalAt(d, ScreenOf(mtS4, 0), out IntVec3 c4);
            Find.DesignatorManager.Deselect();
            Result("F4 designator: stairs S1 (open), S2 (open, made thick later) and S3 (landing on natural rock over a thick roof) accepted; S4 (thick roof under its landing) refused",
                allOk && !r4.Accepted && r4.Reason == msg && c4 == mtS4, string.Join("; ", got) + $"; S4 {c4}: '{r4.Reason}'");
            // S2's landing column turns thick after its blueprint (as a mountain edge would): construction must refuse it
            IntVec3 b2 = FloorRoof.Below(StairShape.Exit(mtS2, Rot4.North, map), map);
            map.roofGrid.SetRoof(b2, RoofDefOf.RoofRockThick);
            SetWork(map, true);
            foreach (Pawn p in Colonists(map))
            {
                if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                {
                    p.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2);
                }
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            TickUntil(40000, () => MtStairsAt(map, mtS1) != null && MtStairsAt(map, mtS3) != null, "mt_built", 120);
        }
    }
}
