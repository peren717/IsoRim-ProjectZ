using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProjectZ.Patches;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode shuttles (shuttles and transport pods on upper floors; run with -projectzfloors=2). Scene: a 25x17 slab on floor
    // 2 over a columned, roofed ground area in a cleared field, open ground south of it. On the slab: a landing pad (four
    // powered ship landing beacons, 9x9 inside), a spot for a 5x3 shuttle (T1), a pod launcher spot. Everything runs on the
    // game's own code and real ticks; pawns walk, board and build.
    //   1 landing checks (the shuttle's own ShuttleCanLandHere for the imperial and the passenger shuttle, the permit's
    //     targeting): slab yes, footprint or interaction cell over open air no, ground under the slab no, open ground yes,
    //     void rows no; then a titled colonist on floor 2 calls a shuttle (call-shuttle permit) onto T1, filmed coming down;
    //     the ground under it (a colonist, a column, steel) is untouched and still walkable.
    //   2 boarding and take-off: two colonists on floor 2 load themselves and steel into it, it takes off (filmed) for a
    //     cell on the ground under the slab; it must come down somewhere else (never under a slab), unload and fly away.
    //   3 a quest shuttle (no cell given) lands on the floor-2 pad, unloads and leaves; with open air inside the pad it
    //     lands elsewhere; a shuttle arriving at the slab's edge picks a cell it fits on.
    //   4 a pod launcher and a transport pod built on floor 2 by colonists, loaded (a colonist, steel), launched (filmed)
    //     to a ground cell, landing there.
    //   5 the Odyssey passenger shuttle on floor 2: boarded, launched (filmed) to T1, landing there on floor 2 (filmed).
    public static partial class TestRunner
    {
        private static CellRect shPlat, shField;
        private static Faction shEmpire;
        private static string shGroundId, shCallerId, shB2Id, shSpareId;
        private static Thing shShuttle, shPassenger, shPod, shLauncher;
        private static string shSnap;
        private static int shSlabs, shKept, shInjuries;

        // filming
        private sealed class ShFrame
        {
            public int mark;     // incoming: ticks to impact; leaving: ticks since take-off; int.MinValue: landed (+10 ticks)
            public int view;     // floor viewed (0 ground: floor 2 hidden, 1 floor 2)
            public float yaw;    // IsoRim freeYaw (0 = 45, 180 = 225)
        }

        private static Skyfaller shSky;
        private static IntVec3 shSkyCell;
        private static bool shSkyLeaving;
        private static string shFilmName, shFilmNext;
        private static List<ShFrame> shFrames;
        private static int shFrame;
        private static readonly List<Vector3> shPoses = new List<Vector3>();   // (ticks, height, floor height)
        private static float shOldYaw, shOldMin;

        private static IntVec3 ShP(int dx, int dz) => new IntVec3(shPlat.minX + dx, 0, shPlat.minZ + dz);

        private static IntVec3 ShU(int dx, int dz, Map map) => ProjectZ.CellOnFloor(ShP(dx, dz), 1, map);

        // (also a pawn inside a shuttle or pod)
        private static Pawn ShPawn(Map map, string id) => ById(map, id) ?? PawnsFinder.AllMapsWorldAndTemporary_Alive.FirstOrDefault(p => p.ThingID == id);

        private static CellRect ShuttleRect(IntVec3 c, ThingDef def, Rot4 rot) => GenAdj.OccupiedRect(c, rot, def.size);

        private static bool ShFootprintOk(Map map, IntVec3 c, ThingDef def, Rot4 rot) =>
            ShuttleRect(c, def, rot).Cells.All(x => x.InBounds(map) && !DropRules.Blocked(x, map) && x.Walkable(map));

        private static string ShGround(Map map, IEnumerable<IntVec3> cells) => string.Join(";", cells.Select(g => g.x + "," + g.z + ":" +
            string.Join("+", g.GetThingList(map).Where(t => !(t is Pawn) && !(t is Mote) && !(t is Filth)).Select(t => t.def.defName + "x" + t.stackCount).OrderBy(s => s)) + "/" + (map.roofGrid.RoofAt(g)?.defName ?? "-") + "/" + g.Walkable(map)));

        private static IEnumerable<IntVec3> ShT1Ground() => ShuttleRect(ShP(18, 5), ThingDefOf.Shuttle, Rot4.North).Cells.Concat(new[] { ShP(18, 3) });

        private static void ShLoad(CompTransporter tr, List<Thing> things, Map map)
        {
            List<CompTransporter> list = new List<CompTransporter> { tr };
            if (!tr.LoadingInProgressOrReadyToLaunch)
            {
                TransporterUtility.InitiateLoading(list);
            }
            foreach (Thing t in things)
            {
                TransferableOneWay tow = new TransferableOneWay();
                tow.things.Add(t);
                tow.AdjustTo(t.stackCount);
                tr.AddToTheToLoadList(tow, t.stackCount);
            }
            TransporterUtility.MakeLordsAsAppropriate(things.OfType<Pawn>().ToList(), list, map);
        }

        private static bool ShLoaded(CompTransporter tr, IEnumerable<Pawn> pawns) => tr != null && pawns.All(p => tr.innerContainer.Contains(p)) && !tr.AnyInGroupHasAnythingLeftToLoad;

        private static Thing ShSpawnStack(ThingDef def, int n, IntVec3 c, Map map)
        {
            Thing t = ThingMaker.MakeThing(def);
            t.stackCount = n;
            return GenSpawn.Spawn(t, c, map);
        }

        private static T ShNew<T>(Map map, List<Thing> before) where T : Thing => map.listerThings.AllThings.OfType<T>().Where(t => !before.Contains(t)).FirstOrDefault();

        // ---- filming a skyfaller on real ticks

        private static void ShFilm(Skyfaller s, string name, List<ShFrame> frames, string next)
        {
            shSky = s;
            shSkyCell = s.Position;
            shSkyLeaving = s.def.skyfaller.reversed;
            shFilmName = name;
            shFrames = frames;
            shFrame = 0;
            shFilmNext = next;
            shPoses.Clear();
            Info($"film {name}: {s.def.defName} ({s.GetType().Name}) at {ACell(s.Position, s.Map)}, ticks {s.ticksToImpact}");
            ShFilmNext(s.Map);
        }

        private static void ShFilmNext(Map map)
        {
            Skyfaller s = shSky;
            while (shFrame < shFrames.Count && shFrames[shFrame].mark != int.MinValue && (s == null || !s.Spawned))
            {
                shFrame++;   // gone before this frame
            }
            if (shFrame >= shFrames.Count)
            {
                ShFilmEnd(map);
                Wait(2, shFilmNext);
                return;
            }
            ShFrame f = shFrames[shFrame];
            if (f.mark == int.MinValue)
            {
                if (s != null && s.Spawned)
                {
                    TickUntil(1200, () => !s.Spawned, "sh_film_landed", 1);
                }
                else
                {
                    StartTicks(10, "sh_film_shoot");
                }
                return;
            }
            if (shSkyLeaving)
            {
                TickUntil(1200, () => !s.Spawned || s.ticksToImpact >= f.mark, "sh_film_shoot", 1);
            }
            else
            {
                TickUntil(1200, () => !s.Spawned || s.ticksToImpact <= f.mark, "sh_film_shoot", 1);
            }
        }

        private static void ShFilmEnd(Map map)
        {
            if (shPoses.Count < 2)
            {
                Info($"film {shFilmName}: {shPoses.Count} poses");
                return;
            }
            float storey = IsoRimBridge.StoreyHeight;
            int k = ProjectZ.FloorOf(shSkyCell, map);
            float expectFloor = k * storey;
            bool floorOk = shPoses.All(p => Mathf.Abs(p.z - expectFloor) < 0.05f);
            bool mono = true;
            for (int i = 1; i < shPoses.Count; i++)
            {
                mono &= shSkyLeaving ? shPoses[i].y >= shPoses[i - 1].y - 0.01f : shPoses[i].y <= shPoses[i - 1].y + 0.01f;
            }
            Vector3 near = shSkyLeaving ? shPoses[0] : shPoses[shPoses.Count - 1];
            // touchdown: 2 ticks out it is within 0.8 cells of its floor (no drop or pop at landing); take-off: 8 ticks in
            // within 1.5 cells
            bool nearFloor = near.y >= expectFloor - 0.05f && (shSkyLeaving ? near.x <= 10f && near.y - expectFloor < 1.5f : near.x <= 3f && near.y - expectFloor < 0.8f);
            Result($"{shFilmName}: drawn over its floor ({k + 1}), {(shSkyLeaving ? "climbing" : "coming down")} steadily, at the floor's height near the {(shSkyLeaving ? "start" : "end")}",
                floorOk && mono && nearFloor && shPoses.All(p => p.y >= expectFloor - 0.05f),
                $"floor height {expectFloor:0.00}; poses (ticks, height, floor height) {string.Join(" ", shPoses.Select(p => $"({p.x:0},{p.y:0.00},{p.z:0.00})"))}");
        }

        private static bool StepShuttles(Map map)
        {
            switch (stage)
            {
                case "sh_setup":
                    SetupShuttles(map);
                    return true;
                case "sh_film_landed":
                    StartTicks(10, "sh_film_shoot");
                    return true;
                case "sh_film_shoot":
                {
                    tickWatch = null;
                    ShFrame f = shFrames[shFrame];
                    IsoRimBridge.SetOption("freeYaw", f.yaw);
                    IntVec3 g = ProjectZ.GroundCellOf(shSkyCell, map);
                    Find.CameraDriver.config.sizeRange.min = 2f;
                    ShotAt(map, f.view, g + new IntVec3(-2, 0, 2), 9f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(25, "sh_film_take");
                    return true;
                }
                case "sh_film_take":
                {
                    ShFrame f = shFrames[shFrame];
                    Skyfaller s = shSky;
                    string pose = "landed";
                    if (s != null && s.Spawned)
                    {
                        Vector3[] p = IsoRimBridge.SkyPose(s);
                        if (p != null)
                        {
                            Vector3 land = s.TrueCenter();
                            float drift = new Vector2(p[0].x - land.x, p[0].z - (land.z + p[1].z)).magnitude;
                            pose = $"ticks {s.ticksToImpact}, ground ({p[0].x:0.00},{p[0].z:0.00}) height {p[1].x:0.00} floor height {p[1].y:0.00}, drift {drift:0.00}";
                            if (f.view == 1 || ProjectZ.FloorOf(s.Position, map) == 0)
                            {
                                shPoses.Add(new Vector3(s.ticksToImpact, p[1].x, p[1].y));
                            }
                        }
                        else
                        {
                            pose = $"ticks {s.ticksToImpact}, not drawn (its floor hidden)";
                        }
                    }
                    string tag = f.mark == int.MinValue ? "landed" : (shSkyLeaving ? "up" : "t") + f.mark;
                    string name = $"sh_{shFilmName}_{tag}_{(f.view == 1 ? "view2" : "view1")}_yaw{(f.yaw == 0f ? 45 : 225)}";
                    Info($"{name}: {pose}");
                    ZCapture(name);
                    shFrame++;
                    Wait(3, "sh_film_next");
                    return true;
                }
                case "sh_film_next":
                    ShFilmNext(map);
                    return true;

                case "sh_power":
                {
                    List<ShipLandingArea> zones = ShipLandingBeaconUtility.GetLandingZones(map);
                    bool beaconsOn = map.listerThings.ThingsOfDef(ThingDefOf.ShipLandingBeacon).All(b => b.TryGetComp<CompShipLandingBeacon>().Active);
                    Result("landing pad on floor 2: four beacons powered, one landing area inside them on floor 2, clear",
                        beaconsOn && zones.Count == 1 && zones[0].Clear && ProjectZ.FloorOf(zones[0].CenterCell, map) == 1,
                        $"powered {beaconsOn}, areas {zones.Count}: {string.Join(" ", zones.Select(z => z.MyRect + " clear " + z.Clear + " centre " + ACell(z.CenterCell, map)))}");
                    stage = "sh_valid";
                    return true;
                }

                // ---- 1: landing checks and the permit shuttle
                case "sh_valid":
                    ShuttleChecks(map);
                    return true;
                case "sh_permit_landed":
                {
                    shShuttle = map.listerThings.ThingsOfDef(ThingDefOf.Shuttle).FirstOrDefault();
                    Pawn gp = ById(map, shGroundId);
                    string now = ShGround(map, ShT1Ground());
                    Result("permit shuttle landed on floor 2 at T1, all of it on the slab",
                        shShuttle != null && shShuttle.Position == ShU(18, 5, map) && ShuttleRect(shShuttle.Position, shShuttle.def, shShuttle.Rotation).Cells.All(c => ProjectZ.HasFloor(c, map)),
                        shShuttle == null ? "no shuttle" : ACell(shShuttle.Position, map));
                    Result("the ground under it is untouched (column, steel, roof, walkable cells), the colonist under it unhurt; slabs and their roofs as before",
                        now == shSnap && gp != null && gp.Spawned && Injuries(gp) == shInjuries && SlabCount(map) == shSlabs && KeptCount(map) == shKept,
                        (now == shSnap ? "same" : $"before {shSnap}\n  after {now}") + $"; colonist {gp?.Position} injuries {shInjuries} -> {(gp == null ? -1 : Injuries(gp))} ({(gp == null ? "" : InjuryNames(gp))}); slabs {shSlabs} -> {SlabCount(map)}, floor roofs {shKept} -> {KeptCount(map)}");
                    if (gp != null)
                    {
                        Teleport(gp, ShP(14, 5));
                        GoTo(gp, ShP(23, 5));
                        TickUntil(1500, () => gp.Position == ShP(23, 5), "sh_walk_under", 10);
                    }
                    else
                    {
                        stage = "sh_board";
                    }
                    return true;
                }
                case "sh_walk_under":
                {
                    Pawn gp = ById(map, shGroundId);
                    Result("a ground colonist walks across under the landed shuttle", gp != null && gp.Position == ShP(23, 5), $"at {gp?.Position}, wanted {ShP(23, 5)}");
                    if (gp != null)
                    {
                        Teleport(gp, ShP(17, 5));
                    }
                    shFrames = new List<ShFrame> { new ShFrame { mark = int.MinValue, view = 0, yaw = 0f } };
                    shFrame = 0;
                    shFilmName = "permit_ground_under";
                    shSky = null;
                    shSkyCell = ShU(18, 5, map);
                    shFilmNext = "sh_board";
                    shPoses.Clear();
                    StartTicks(5, "sh_film_shoot");
                    return true;
                }

                // ---- 2: boarding from floor 2 and take-off
                case "sh_board":
                {
                    CompTransporter tr = shShuttle?.TryGetComp<CompTransporter>();
                    if (tr == null)
                    {
                        Result("boarding", false, "no shuttle");
                        stage = "sh_pad";
                        return true;
                    }
                    List<Pawn> boarders = new[] { shCallerId, shB2Id }.Select(id => ById(map, id)).Where(p => p != null).ToList();
                    foreach (Pawn p in boarders)
                    {
                        Draft(p, false);
                    }
                    Thing steel = ShSpawnStack(ThingDefOf.Steel, 20, ShU(14, 9, map), map);
                    Info($"boarders on floor 2: {string.Join(" ", boarders.Select(p => p.LabelShort + " " + ACell(p.Position, map)))}; steel {ACell(steel.Position, map)}");
                    ShLoad(tr, boarders.Cast<Thing>().Concat(new[] { steel }).ToList(), map);
                    TickUntil(9000, () => ShLoaded(tr, boarders), "sh_boarded", 30);
                    return true;
                }
                case "sh_boarded":
                {
                    CompTransporter tr = shShuttle?.TryGetComp<CompTransporter>();
                    List<Pawn> boarders = new[] { shCallerId, shB2Id }.Select(id => ShPawn(map, id)).Where(p => p != null).ToList();
                    Result("two colonists on floor 2 walk to the shuttle and board it, the steel loaded too", ShLoaded(tr, boarders),
                        $"inside: {string.Join(", ", tr.innerContainer.Select(t => t.LabelShort))}; left to load: {(tr.AnyInGroupHasAnythingLeftToLoad ? tr.FirstThingLeftToLoadInGroup?.LabelShort : "nothing")}");
                    CompShuttle cs = shShuttle.TryGetComp<CompShuttle>();
                    ShipJob job = cs.shipParent?.curJob;
                    MethodInfoLaunch(job, map.Tile, new TransportersArrivalAction_LandInSpecificCell(map.Parent, ShP(8, 8), Rot4.North, true));
                    FlyShipLeaving lv = map.listerThings.AllThings.OfType<FlyShipLeaving>().FirstOrDefault(s => s.def.defName == "ShuttleLeaving");
                    Result("it takes off from floor 2 (its leaving skyfaller at T1)", lv != null && lv.Position == ShU(18, 5, map), lv == null ? $"none; ship job {job?.def?.defName}" : ACell(lv.Position, map));
                    if (lv == null)
                    {
                        stage = "sh_pad";
                        return true;
                    }
                    ShFilm(lv, "permit_takeoff", new List<ShFrame>
                    {
                        new ShFrame { mark = 8, view = 1 }, new ShFrame { mark = 35, view = 1 }, new ShFrame { mark = 35, view = 0 },
                        new ShFrame { mark = 70, view = 1 }, new ShFrame { mark = 120, view = 1 },
                    }, "sh_return");
                    return true;
                }
                case "sh_return":
                    TickUntil(3000, () => map.listerThings.AllThings.Any(t => t is ShuttleIncoming), "sh_return_in", 1);
                    return true;
                case "sh_return_in":
                {
                    ShuttleIncoming si = map.listerThings.AllThings.OfType<ShuttleIncoming>().FirstOrDefault();
                    if (si == null)
                    {
                        Result("the shuttle comes back", false, "no shuttle arrived");
                        stage = "sh_pad";
                        return true;
                    }
                    Thing lands = si.innerContainer.FirstOrDefault();
                    ThingDef sd = ThingDefOf.Shuttle;
                    Result($"asked to land on the ground under the slab ({ACell(ShP(8, 8), map)}), it comes down where it fits instead, never under a slab",
                        ShFootprintOk(map, si.Position, sd, Rot4.North) && !ShuttleRect(si.Position, sd, Rot4.North).Cells.Any(c => c.Roofed(map) && FloorRoof.Kept(c, map)),
                        $"landing at {ACell(si.Position, map)}; footprint {string.Join(" ", ShuttleRect(si.Position, sd, Rot4.North).Cells.Where(c => DropRules.Blocked(c, map)).Select(c => ACell(c, map)))}");
                    ShFilm(si, "permit_return", new List<ShFrame> { new ShFrame { mark = 40, view = ProjectZ.FloorOf(si.Position, map) == 0 ? 0 : 1 }, new ShFrame { mark = 2, view = ProjectZ.FloorOf(si.Position, map) == 0 ? 0 : 1 }, new ShFrame { mark = int.MinValue, view = ProjectZ.FloorOf(si.Position, map) == 0 ? 0 : 1 } }, "sh_return_landed");
                    return true;
                }
                case "sh_return_landed":
                {
                    List<string> ids = new List<string> { shCallerId, shB2Id };
                    TickUntil(4000, () => ids.All(id => ById(map, id) != null) && !map.listerThings.ThingsOfDef(ThingDefOf.Shuttle).Any() && !map.listerThings.AllThings.Any(t => t is FlyShipLeaving), "sh_return_done", 30);
                    return true;
                }
                case "sh_return_done":
                {
                    List<Pawn> back = new[] { shCallerId, shB2Id }.Select(id => ById(map, id)).ToList();
                    Result("the colonists come out on cells they can stand on, and the shuttle flies away again",
                        back.All(p => p != null && ProjectZ.FloorOf(p.Position, map) >= 0 && p.Position.Standable(map)) && !map.listerThings.ThingsOfDef(ThingDefOf.Shuttle).Any(),
                        string.Join("; ", back.Select(p => p == null ? "missing" : p.LabelShort + " " + ACell(p.Position, map))));
                    foreach (Pawn p in back.Where(p => p != null))
                    {
                        Teleport(p, ShU(14, 8 + back.IndexOf(p), map));
                        Draft(p, true);
                    }
                    stage = "sh_pad";
                    return true;
                }

                // ---- 3: the landing pad on floor 2, a blocked pad, the slab's edge
                case "sh_pad":
                {
                    List<Thing> before = map.listerThings.AllThings.ToList();
                    Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    steel.stackCount = 40;
                    TransportShip ts = TransportShipMaker.MakeTransportShip(TransportShipDefOf.Ship_Shuttle, new List<Thing> { steel });
                    ts.ArriveAt(IntVec3.Invalid, map.Parent);
                    ts.AddJobs(ShipJobDefOf.Unload, ShipJobDefOf.FlyAway);
                    ShuttleIncoming si = ShNew<ShuttleIncoming>(map, before);
                    CellRect pad = new CellRect(ShP(2, 2).x, ShP(2, 2).z, 9, 9);
                    Result("a quest shuttle (no landing cell given) comes down on the landing pad on floor 2",
                        si != null && ProjectZ.FloorOf(si.Position, map) == 1 && ShuttleRect(si.Position, ThingDefOf.Shuttle, Rot4.North).Cells.All(c => pad.Contains(ProjectZ.GroundCellOf(c, map))),
                        si == null ? "none" : ACell(si.Position, map) + $"; pad {pad} (ground cells)");
                    if (si == null)
                    {
                        stage = "sh_edge";
                        return true;
                    }
                    ShFilm(si, "pad_landing", new List<ShFrame> { new ShFrame { mark = 60, view = 1 }, new ShFrame { mark = 15, view = 1 }, new ShFrame { mark = 2, view = 1 }, new ShFrame { mark = int.MinValue, view = 1 }, new ShFrame { mark = int.MinValue, view = 1, yaw = 180f } }, "sh_pad_unload");
                    return true;
                }
                case "sh_pad_unload":
                    TickUntil(4000, () => map.listerThings.AllThings.Any(t => t is FlyShipLeaving), "sh_pad_leave", 5);
                    return true;
                case "sh_pad_leave":
                {
                    CellRect pad = new CellRect(ShP(2, 2).x, ShP(2, 2).z, 9, 9);
                    int steelUp = map.listerThings.ThingsOfDef(ThingDefOf.Steel).Where(t => ProjectZ.FloorOf(t.Position, map) == 1 && pad.ExpandedBy(3).Contains(ProjectZ.GroundCellOf(t.Position, map))).Sum(t => t.stackCount);
                    FlyShipLeaving lv = map.listerThings.AllThings.OfType<FlyShipLeaving>().FirstOrDefault();
                    Result("it unloads its cargo onto the pad on floor 2 and takes off from there", steelUp >= 40 && lv != null && ProjectZ.FloorOf(lv.Position, map) == 1,
                        $"steel on the pad {steelUp}; leaving {(lv == null ? "none" : ACell(lv.Position, map))}");
                    if (lv == null)
                    {
                        stage = "sh_pad2";
                        return true;
                    }
                    ShFilm(lv, "pad_takeoff", new List<ShFrame> { new ShFrame { mark = 8, view = 1 }, new ShFrame { mark = 70, view = 1, yaw = 180f } }, "sh_pad2");
                    return true;
                }
                case "sh_pad2":
                {
                    TickUntil(600, () => !map.listerThings.AllThings.Any(t => t is FlyShipLeaving), "sh_pad2_go", 10);
                    return true;
                }
                case "sh_pad2_go":
                {
                    map.terrainGrid.SetTerrain(ShU(6, 6, map), PZDefOf.PZ_OpenAir);
                    List<ShipLandingArea> zones = ShipLandingBeaconUtility.GetLandingZones(map);
                    Info($"open air at {ACell(ShU(6, 6, map), map)} inside the pad: areas {string.Join(" ", zones.Select(z => "clear " + z.Clear + " terrain-blocked " + z.BlockedByTerrainAffordance))}");
                    List<Thing> before = map.listerThings.AllThings.ToList();
                    TransportShip ts = TransportShipMaker.MakeTransportShip(TransportShipDefOf.Ship_Shuttle, null);
                    ts.ArriveAt(IntVec3.Invalid, map.Parent);
                    ts.AddJobs(ShipJobDefOf.Unload, ShipJobDefOf.FlyAway);
                    ShuttleIncoming si = ShNew<ShuttleIncoming>(map, before);
                    CellRect pad = new CellRect(ShP(2, 2).x, ShP(2, 2).z, 9, 9);
                    Result("with open air inside the pad, the next quest shuttle does not use it and lands where it fits",
                        si != null && !ShuttleRect(si.Position, ThingDefOf.Shuttle, Rot4.North).Cells.Any(c => ProjectZ.FloorOf(c, map) == 1 && pad.Contains(ProjectZ.GroundCellOf(c, map))) && ShFootprintOk(map, si.Position, ThingDefOf.Shuttle, Rot4.North),
                        si == null ? "none" : ACell(si.Position, map));
                    TickUntil(4000, () => !map.listerThings.AllThings.Any(t => t is ShuttleIncoming || t is FlyShipLeaving) && !map.listerThings.ThingsOfDef(ThingDefOf.Shuttle).Any(), "sh_edge", 10);
                    return true;
                }
                case "sh_edge":
                {
                    map.terrainGrid.SetTerrain(ShU(6, 6, map), PZDefOf.PZ_UpperFloor);
                    List<Thing> before = map.listerThings.AllThings.ToList();
                    IntVec3 edge = ShU(0, 8, map);
                    TransportersArrivalActionUtility.DropShuttle(new ActiveTransporterInfo(), map, edge);
                    ShuttleIncoming si = ShNew<ShuttleIncoming>(map, before);
                    Result($"a shuttle arriving at the slab's edge ({ACell(edge, map)}: its footprint would hang over open air) lands where all of it fits",
                        si != null && ShFootprintOk(map, si.Position, ThingDefOf.Shuttle, Rot4.North), si == null ? "none" : ACell(si.Position, map));
                    TickUntil(4000, () => !map.listerThings.AllThings.Any(t => t is ShuttleIncoming || t is FlyShipLeaving) && !map.listerThings.ThingsOfDef(ThingDefOf.Shuttle).Any(), "sh_pods", 10);
                    return true;
                }

                // ---- 4: pod launcher and transport pod built on floor 2, loaded, launched
                case "sh_pods":
                {
                    SetWork(map, true);
                    // (no stairs up here: keep the builders on the slab, off the ground's materials)
                    foreach (Thing t in map.listerThings.AllThings.Where(t => (t.def == ThingDefOf.Steel || t.def == ThingDefOf.ComponentIndustrial) && t.Spawned && ProjectZ.FloorOf(t.Position, map) == 0).ToList())
                    {
                        t.SetForbidden(true, false);
                    }
                    List<Pawn> builders = Colonists(map).Where(p => p.ThingID != shGroundId).ToList();
                    int i = 0;
                    foreach (Pawn p in builders)
                    {
                        Teleport(p, ShU(20, 8 + i++, map));
                        Draft(p, false);
                        SkillRecord sk = p.skills?.GetSkill(SkillDefOf.Construction);
                        if (sk != null && sk.Level < 10)
                        {
                            sk.Level = 10;
                        }
                    }
                    ShSpawnStack(ThingDefOf.Steel, 75, ShU(21, 2, map), map);
                    ShSpawnStack(ThingDefOf.Steel, 75, ShU(22, 2, map), map);
                    ShSpawnStack(ThingDefOf.ComponentIndustrial, 4, ShU(23, 2, map), map);
                    ThingDef launcher = DefDatabase<ThingDef>.GetNamed("PodLauncher");
                    AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(launcher, ShU(22, 9, map), Rot4.North, map);
                    Result("a pod launcher can be placed on floor 2", ok.Accepted, ok.Reason ?? "");
                    GenConstruct.PlaceBlueprintForBuild(launcher, ShU(22, 9, map), map, Rot4.North, Faction.OfPlayer, null);
                    TickUntil(40000, () => map.listerThings.ThingsOfDef(launcher).Any(), "sh_pods_pod", 120);
                    return true;
                }
                case "sh_pods_pod":
                {
                    ThingDef launcherDef = DefDatabase<ThingDef>.GetNamed("PodLauncher");
                    shLauncher = map.listerThings.ThingsOfDef(launcherDef).FirstOrDefault();
                    Result("colonists on floor 2 build the pod launcher there", shLauncher != null && ProjectZ.FloorOf(shLauncher.Position, map) == 1, shLauncher == null ? "not built" : ACell(shLauncher.Position, map));
                    if (shLauncher == null)
                    {
                        stage = "sh_pass";
                        return true;
                    }
                    IntVec3 port = FuelingPortUtility.GetFuelingPortCell((Building)shLauncher);
                    ThingDef pod = DefDatabase<ThingDef>.GetNamed("TransportPod");
                    AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(pod, port, Rot4.North, map);
                    Result("a transport pod can be placed at its fueling port on floor 2", ok.Accepted, ACell(port, map) + " " + (ok.Reason ?? ""));
                    GenConstruct.PlaceBlueprintForBuild(pod, port, map, Rot4.North, Faction.OfPlayer, null);
                    TickUntil(30000, () => map.listerThings.ThingsOfDef(pod).Any(), "sh_pods_load", 120);
                    return true;
                }
                case "sh_pods_load":
                {
                    ThingDef podDef = DefDatabase<ThingDef>.GetNamed("TransportPod");
                    shPod = map.listerThings.ThingsOfDef(podDef).FirstOrDefault();
                    Result("they build the transport pod on floor 2", shPod != null && ProjectZ.FloorOf(shPod.Position, map) == 1, shPod == null ? "not built" : ACell(shPod.Position, map));
                    SetWork(map, false);
                    if (shPod == null)
                    {
                        stage = "sh_pass";
                        return true;
                    }
                    ShDaylight(map);
                    shLauncher.TryGetComp<CompRefuelable>().Refuel(150f);
                    Pawn rider = ById(map, shB2Id);
                    Thing steel = ShSpawnStack(ThingDefOf.Steel, 30, ShU(19, 12, map), map);
                    ShLoad(shPod.TryGetComp<CompTransporter>(), new List<Thing> { rider, steel }, map);
                    TickUntil(9000, () => ShLoaded(shPod.TryGetComp<CompTransporter>(), new[] { ShPawn(map, shB2Id) }), "sh_pods_launch", 30);
                    return true;
                }
                case "sh_pods_launch":
                {
                    CompTransporter tr = shPod.TryGetComp<CompTransporter>();
                    CompLaunchable cl = shPod.TryGetComp<CompLaunchable>();
                    AcceptanceReport can = cl.CanLaunch();
                    Result("a colonist on floor 2 gets into the pod, the steel loaded; ready to launch", ShLoaded(tr, new[] { ShPawn(map, shB2Id) }) && can.Accepted,
                        $"inside: {string.Join(", ", tr.innerContainer.Select(t => t.LabelShort))}; can launch {can.Accepted} {can.Reason}");
                    IntVec3 podCell = shPod.Position;
                    List<Thing> before = map.listerThings.AllThings.ToList();
                    cl.TryLaunch(map.Tile, new TransportersArrivalAction_LandInSpecificCell(map.Parent, ShP(6, -10)));
                    FlyShipLeaving lv = ShNew<FlyShipLeaving>(map, before);
                    Result("the pod takes off from floor 2", lv != null && lv.Position == podCell, lv == null ? "no leaving pod" : ACell(lv.Position, map));
                    if (lv == null)
                    {
                        stage = "sh_pass";
                        return true;
                    }
                    ShFilm(lv, "pod_takeoff", new List<ShFrame> { new ShFrame { mark = 8, view = 1 }, new ShFrame { mark = 30, view = 1 }, new ShFrame { mark = 30, view = 0 }, new ShFrame { mark = 60, view = 1, yaw = 180f } }, "sh_pods_fly");
                    return true;
                }
                case "sh_pods_fly":
                    TickUntil(3000, () => map.listerThings.AllThings.Any(t => t is DropPodIncoming), "sh_pods_in", 1);
                    return true;
                case "sh_pods_in":
                {
                    DropPodIncoming pi = map.listerThings.AllThings.OfType<DropPodIncoming>().FirstOrDefault();
                    if (pi == null)
                    {
                        Result("the pod lands", false, "no pod coming in");
                        stage = "sh_pass";
                        return true;
                    }
                    ShFilm(pi, "pod_landing", new List<ShFrame> { new ShFrame { mark = 30, view = 0 }, new ShFrame { mark = 2, view = 0 }, new ShFrame { mark = int.MinValue, view = 0 } }, "sh_pods_landed");
                    return true;
                }
                case "sh_pods_landed":
                    TickUntil(1500, () => ById(map, shB2Id) != null, "sh_pods_out", 10);
                    return true;
                case "sh_pods_out":
                {
                    Pawn rider = ById(map, shB2Id);
                    Result("the pod comes down at the ground cell it was sent to, the colonist gets out there",
                        rider != null && ProjectZ.FloorOf(rider.Position, map) == 0 && rider.Position.DistanceTo(ShP(6, -10)) < 8f,
                        rider == null ? "not out" : $"{ACell(rider.Position, map)}, target {ACell(ShP(6, -10), map)}");
                    if (rider != null)
                    {
                        Teleport(rider, ShU(14, 10, map));
                        Draft(rider, true);
                    }
                    stage = "sh_pass";
                    return true;
                }

                // ---- 5: Odyssey passenger shuttle on floor 2
                case "sh_pass":
                {
                    ThingDef psDef = DefDatabase<ThingDef>.GetNamedSilentFail("PassengerShuttle");
                    if (!ModsConfig.OdysseyActive || psDef == null)
                    {
                        Info("Odyssey not active: passenger shuttle skipped");
                        stage = "sh_end";
                        return true;
                    }
                    ShDaylight(map);
                    shPassenger = Place(psDef, null, ShU(18, 13, map), map, Rot4.East);
                    shPassenger.TryGetComp<CompRefuelable>().Refuel(400f);
                    Pawn pilot = ById(map, shCallerId);
                    Teleport(pilot, ShU(14, 13, map));
                    Draft(pilot, false);
                    Info($"passenger shuttle at {ACell(shPassenger.Position, map)} rot {shPassenger.Rotation}, pilot {pilot.LabelShort} at {ACell(pilot.Position, map)}");
                    ShLoad(shPassenger.TryGetComp<CompTransporter>(), new List<Thing> { pilot }, map);
                    TickUntil(9000, () => ShLoaded(shPassenger.TryGetComp<CompTransporter>(), new[] { ShPawn(map, shCallerId) }), "sh_pass_launch", 30);
                    return true;
                }
                case "sh_pass_launch":
                {
                    CompTransporter tr = shPassenger.TryGetComp<CompTransporter>();
                    CompLaunchable cl = shPassenger.TryGetComp<CompLaunchable>();
                    AcceptanceReport can = cl.CanLaunch();
                    Result("the pilot on floor 2 boards the passenger shuttle; ready to launch", ShLoaded(tr, new[] { ShPawn(map, shCallerId) }) && can.Accepted,
                        $"inside: {string.Join(", ", tr.innerContainer.Select(t => t.LabelShort))}; can launch {can.Accepted} {can.Reason}");
                    AcceptanceReport there = RoyalTitlePermitWorker_CallShuttle.ShuttleCanLandHere(ShU(18, 5, map), map, shPassenger.def, Rot4.East);
                    Info($"passenger shuttle can land at T1 {ACell(ShU(18, 5, map), map)} facing east: {there.Accepted} {there.Reason}");
                    IntVec3 at = shPassenger.Position;
                    List<Thing> before = map.listerThings.AllThings.ToList();
                    cl.TryLaunch(map.Tile, new TransportersArrivalAction_LandInSpecificCell(map.Parent, ShU(18, 5, map), Rot4.East, true));
                    FlyShipLeaving lv = ShNew<FlyShipLeaving>(map, before);
                    Result("the passenger shuttle takes off from floor 2", lv != null && lv.Position == at, lv == null ? "no leaving shuttle" : ACell(lv.Position, map) + " " + lv.def.defName);
                    if (lv == null)
                    {
                        stage = "sh_end";
                        return true;
                    }
                    ShFilm(lv, "passenger_takeoff", new List<ShFrame> { new ShFrame { mark = 8, view = 1 }, new ShFrame { mark = 35, view = 1 }, new ShFrame { mark = 35, view = 0 }, new ShFrame { mark = 80, view = 1, yaw = 180f } }, "sh_pass_fly");
                    return true;
                }
                case "sh_pass_fly":
                    TickUntil(3000, () => map.listerThings.AllThings.Any(t => t is ShuttleIncoming), "sh_pass_in", 1);
                    return true;
                case "sh_pass_in":
                {
                    ShuttleIncoming si = map.listerThings.AllThings.OfType<ShuttleIncoming>().FirstOrDefault();
                    Result("the passenger shuttle comes down at T1 on floor 2", si != null && si.Position == ShU(18, 5, map), si == null ? "none" : ACell(si.Position, map) + " " + si.def.defName);
                    if (si == null)
                    {
                        stage = "sh_end";
                        return true;
                    }
                    ShFilm(si, "passenger_landing", new List<ShFrame>
                    {
                        new ShFrame { mark = 80, view = 1 }, new ShFrame { mark = 40, view = 1 }, new ShFrame { mark = 40, view = 0 },
                        new ShFrame { mark = 12, view = 1 }, new ShFrame { mark = 2, view = 1 }, new ShFrame { mark = int.MinValue, view = 1 }, new ShFrame { mark = int.MinValue, view = 1, yaw = 180f },
                    }, "sh_pass_landed");
                    return true;
                }
                case "sh_pass_landed":
                    TickUntil(2000, () => ById(map, shCallerId) != null, "sh_pass_out", 10);
                    return true;
                case "sh_pass_out":
                {
                    Thing ps = map.listerThings.AllThings.FirstOrDefault(t => t.def.defName == "PassengerShuttle");
                    Pawn pilot = ById(map, shCallerId);
                    Result("the passenger shuttle stands on floor 2 at T1, the pilot gets out on floor 2",
                        ps != null && ps.Position == ShU(18, 5, map) && ShuttleRect(ps.Position, ps.def, ps.Rotation).Cells.All(c => ProjectZ.HasFloor(c, map)) && pilot != null && ProjectZ.FloorOf(pilot.Position, map) == 1,
                        $"shuttle {(ps == null ? "none" : ACell(ps.Position, map) + " rot " + ps.Rotation)}, pilot {(pilot == null ? "inside" : ACell(pilot.Position, map))}");
                    Result("slabs and their roofs as at the start", SlabCount(map) == shSlabs && KeptCount(map) == shKept, $"slabs {shSlabs} -> {SlabCount(map)}, floor roofs {shKept} -> {KeptCount(map)}");
                    stage = "sh_end";
                    return true;
                }
                case "sh_end":
                    IsoRimBridge.SetOption("freeYaw", shOldYaw);
                    Find.CameraDriver.config.sizeRange.min = shOldMin;
                    Finish();
                    return true;
            }
            return false;
        }

        // Morning light for the shots (building takes hours).
        private static void ShDaylight(Map map)
        {
            for (int i = 0; i < 24 && GenLocalDate.HourOfDay(map) != 10; i++)
            {
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + GenDate.TicksPerHour);
            }
        }

        // ShipJob_Wait's own Launch (what choosing a world target does).
        private static void MethodInfoLaunch(ShipJob job, PlanetTile tile, TransportersArrivalAction action)
        {
            if (!(job is ShipJob_Wait))
            {
                Info($"permit shuttle job is {job?.GetType().Name}: no launch");
                return;
            }
            AccessTools.Method(typeof(ShipJob_Wait), "Launch", new[] { typeof(PlanetTile), typeof(TransportersArrivalAction) }).Invoke(job, new object[] { tile, action });
        }

        private static void ShuttleChecks(Map map)
        {
            ThingDef imp = ThingDefOf.Shuttle;
            ThingDef ps = DefDatabase<ThingDef>.GetNamedSilentFail("PassengerShuttle");
            IntVec3 voidCell = new IntVec3(ShP(18, 0).x, 0, ProjectZ.GroundZ(map) + 1);
            var cases = new (string name, IntVec3 c, bool want)[]
            {
                ("floor-2 slab with room for it (T1)", ShU(18, 5, map), true),
                ("footprint over the slab's edge (open air)", ShU(0, 8, map), false),
                ("interaction cell over open air", ShU(6, 1, map), false),
                ("ground under the slab", ShP(8, 8), false),
                ("ground, footprint partly under the slab", ShP(21, -1), false),
                ("open ground beside the slab", ShP(18, -9), true),
                ("void rows between the bands", voidCell, false),
                ("floor-2 open air off the slab", ShU(18, 24, map), false),
            };
            foreach (var k in cases)
            {
                AcceptanceReport a = RoyalTitlePermitWorker_CallShuttle.ShuttleCanLandHere(k.c, map, imp, Rot4.North);
                string extra = "";
                bool okPs = true;
                if (ps != null)
                {
                    AcceptanceReport b = RoyalTitlePermitWorker_CallShuttle.ShuttleCanLandHere(k.c, map, ps, Rot4.East);
                    okPs = b.Accepted == k.want;
                    extra = $"; passenger shuttle (east) {b.Accepted} {b.Reason}";
                }
                Result($"shuttle landing check, {k.name}: {(k.want ? "yes" : "no")}", a.Accepted == k.want && okPs, $"{ACell(k.c, map)}: imperial {a.Accepted} {a.Reason}{extra}");
            }
            foreach (var k in cases.Where(x => x.want))
            {
                Info($"SkyfallerCanLandAt 5x3, {k.name}: {DropCellFinder.SkyfallerCanLandAt(k.c, map, imp.size)}");
            }

            // the call-shuttle permit: a titled colonist on floor 2
            Pawn caller = ById(map, shCallerId);
            Pawn gp = ById(map, shGroundId);
            shSnap = ShGround(map, ShT1Ground());
            shSlabs = SlabCount(map);
            shKept = KeptCount(map);
            shInjuries = gp == null ? 0 : Injuries(gp);
            Info($"ground under T1 before: {shSnap}; colonist under it {gp?.LabelShort} {ACell(gp.Position, map)}");
            RoyalTitlePermitDef permit = DefDatabase<RoyalTitlePermitDef>.GetNamedSilentFail("CallTransportShuttle");
            List<Thing> before = map.listerThings.AllThings.ToList();
            bool called = false;
            if (permit != null && shEmpire != null && caller?.royalty != null)
            {
                RoyalTitlePermitWorker_CallShuttle worker = permit.Worker as RoyalTitlePermitWorker_CallShuttle;
                FloatMenuOption opt = worker.GetRoyalAidOptions(map, caller, shEmpire).FirstOrDefault();
                Info($"royal aid option: {opt?.Label} (enabled {opt?.action != null})");
                if (opt?.action != null)
                {
                    opt.action();
                }
                else
                {
                    AccessTools.Method(typeof(RoyalTitlePermitWorker_CallShuttle), "BeginCallShuttle").Invoke(worker, new object[] { caller, map, shEmpire, true });
                }
                ITargetingSource src = worker;
                bool Pick(IntVec3 c) => src.targetParams.CanTarget(new TargetInfo(c, map), src) && src.ValidateTarget(c, false);
                Info($"permit targeting from {ACell(caller.Position, map)} (range {permit.royalAid.targetingRange}): T1 on floor 2 {Pick(ShU(18, 5, map))}; ground under the slab {Pick(ShP(8, 8))}; open ground beside {Pick(ShP(18, -9))} (range is measured on the map's rows)");
                Result("the permit's target on floor 2 (T1) is accepted from a caller on floor 2; the ground under the slab is refused", Pick(ShU(18, 5, map)) && !src.ValidateTarget(ShP(8, 8), false), "");
                if (Pick(ShU(18, 5, map)))
                {
                    src.OrderForceTarget(ShU(18, 5, map));
                    called = true;
                }
                if (Find.Targeter.IsTargeting)
                {
                    Find.Targeter.StopTargeting();
                }
            }
            else
            {
                Result("call-shuttle permit available", false, $"permit {permit != null}, empire {shEmpire != null}, royalty {caller?.royalty != null}");
            }
            if (!called)
            {
                Thing sh = ThingMaker.MakeThing(ThingDefOf.Shuttle);
                sh.TryGetComp<CompShuttle>().permitShuttle = true;
                TransportShip ts = TransportShipMaker.MakeTransportShip(TransportShipDefOf.Ship_Shuttle, null, sh);
                ts.ArriveAt(ShU(18, 5, map), map.Parent);
                ts.AddJobs(ShipJobDefOf.WaitForever, ShipJobDefOf.Unload_Destination, ShipJobDefOf.FlyAway);
            }
            ShuttleIncoming si = ShNew<ShuttleIncoming>(map, before);
            Result("the shuttle comes in for T1 on floor 2", si != null && si.Position == ShU(18, 5, map), si == null ? "none" : ACell(si.Position, map) + $", ticks {si.ticksToImpact}");
            if (si == null)
            {
                stage = "sh_pad";
                return;
            }
            ShFilm(si, "permit_landing", new List<ShFrame>
            {
                new ShFrame { mark = 120, view = 1 }, new ShFrame { mark = 60, view = 1 }, new ShFrame { mark = 60, view = 0 },
                new ShFrame { mark = 25, view = 1 }, new ShFrame { mark = 2, view = 1 },
                new ShFrame { mark = int.MinValue, view = 1 }, new ShFrame { mark = int.MinValue, view = 1, yaw = 180f },
            }, "sh_permit_landed");
        }

        private static void SetupShuttles(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || an.floors < 2 || !ModsConfig.RoyaltyActive)
            {
                Result("shuttles setup", false, $"needs 2 floors (-projectzfloors=2) and Royalty; floors {an?.floors}, Royalty {ModsConfig.RoyaltyActive}");
                Finish();
                return;
            }
            // a 45x42 open field, the one with the fewest cells to fix
            CellRect found = CellRect.Empty;
            int bestBad = int.MaxValue;
            for (int z = 3; z + 42 < an.groundZ - 3; z += 4)
            {
                for (int x = 3; x + 45 < map.Size.x - 3; x += 4)
                {
                    CellRect r = new CellRect(x, z, 45, 42);
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
                Result("shuttles field", false, "map too small");
                Finish();
                return;
            }
            shField = found;
            List<Pawn> cols = Colonists(map);
            foreach (IntVec3 c in found.ExpandedBy(1))
            {
                if (!c.InBounds(map))
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
                TerrainDef td = c.GetTerrain(map);
                if (td.IsWater || td.passability == Traversability.Impassable)
                {
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
            }
            DebugSettings.enableRandomMentalStates = false;
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            for (int i = 0; i < 24 && GenLocalDate.HourOfDay(map) != 10; i++)
            {
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + GenDate.TicksPerHour);
            }
            shPlat = new CellRect(found.minX + 10, found.minZ + 22, 25, 17);
            foreach (IntVec3 g in shPlat)
            {
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                IntVec3 up = ProjectZ.CellOnFloor(g, 1, map);
                map.terrainGrid.SetTerrain(up, PZDefOf.PZ_UpperFloor);
                map.fogGrid.Unfog(up);
            }
            foreach (IntVec3 c in shPlat.ExpandedBy(6).ClipInsideMap(map))
            {
                IntVec3 up = ProjectZ.CellOnFloor(c, 1, map);
                if (up.IsValid)
                {
                    map.fogGrid.Unfog(up);
                }
            }
            // columns under the slab
            ThingDef column = DefDatabase<ThingDef>.GetNamed("Column");
            foreach (IntVec3 g in shPlat)
            {
                if ((g.x - shPlat.minX) % 6 == 0 && (g.z - shPlat.minZ) % 6 == 0)
                {
                    Place(column, ThingDefOf.BlocksGranite, g, map);
                }
            }
            // the landing pad: four beacons on floor 2, powered by a vanometric cell through conduits on the beacon square
            ThingDef beacon = ThingDefOf.ShipLandingBeacon;
            foreach (IntVec3 b in new[] { ShU(1, 1, map), ShU(11, 1, map), ShU(1, 11, map), ShU(11, 11, map) })
            {
                AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(beacon, b, Rot4.North, map);
                if (!ok.Accepted)
                {
                    Result("a landing beacon can be placed on floor 2", false, ACell(b, map) + " " + ok.Reason);
                }
            }
            for (int d = 1; d <= 11; d++)
            {
                foreach (IntVec3 c in new[] { ShU(d, 1, map), ShU(d, 11, map), ShU(1, d, map), ShU(11, d, map) })
                {
                    if (c.GetFirstBuilding(map) == null)
                    {
                        Place(ThingDefOf.PowerConduit, null, c, map);
                    }
                }
            }
            Place(ThingDefOf.PowerConduit, null, ShU(12, 1, map), map);
            Place(DefDatabase<ThingDef>.GetNamed("VanometricPowerCell"), null, ShU(13, 1, map), map);
            foreach (IntVec3 b in new[] { ShU(1, 1, map), ShU(11, 1, map), ShU(1, 11, map), ShU(11, 11, map) })
            {
                Place(beacon, null, b, map);
            }
            // colonists: one on the ground (under T1), a titled caller and two more on floor 2
            while (cols.Count < 4)
            {
                Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(p, ShP(14, -4), map);
                cols.Add(p);
            }
            shGroundId = cols[0].ThingID;
            shCallerId = cols[1].ThingID;
            shB2Id = cols[2].ThingID;
            shSpareId = cols[3].ThingID;
            Teleport(cols[0], ShP(17, 5));
            Teleport(cols[1], ShU(14, 5, map));
            Teleport(cols[2], ShU(14, 7, map));
            Teleport(cols[3], ShU(14, 12, map));
            for (int i = 4; i < cols.Count; i++)
            {
                Teleport(cols[i], ShP(4 + i, -16));
            }
            foreach (Pawn p in cols)
            {
                Draft(p, true);
            }
            ShSpawnStack(ThingDefOf.Steel, 25, ShP(19, 5), map);
            shEmpire = Faction.OfEmpire;
            if (shEmpire != null)
            {
                if (shEmpire.HostileTo(Faction.OfPlayer))
                {
                    shEmpire.SetRelationDirect(Faction.OfPlayer, FactionRelationKind.Neutral, false);
                }
                Pawn caller = cols[1];
                RoyalTitleDef knight = DefDatabase<RoyalTitleDef>.GetNamedSilentFail("Knight");
                if (caller.royalty != null && knight != null)
                {
                    caller.royalty.SetTitle(shEmpire, knight, false, false, false);
                    RoyalTitlePermitDef permit = DefDatabase<RoyalTitlePermitDef>.GetNamedSilentFail("CallTransportShuttle");
                    if (permit != null)
                    {
                        caller.royalty.AddPermit(permit, shEmpire);
                    }
                    caller.royalty.GainFavor(shEmpire, 40);
                }
            }
            Info($"field {found}, slab {shPlat} on floor 2 (ground rect); storey {IsoRimBridge.StoreyHeight:0.00}; hour {GenLocalDate.HourOfDay(map)}; colonists: ground {cols[0].LabelShort}, caller {cols[1].LabelShort} ({cols[1].royalty?.GetCurrentTitle(shEmpire)?.defName}), {cols[2].LabelShort}, {cols[3].LabelShort}");
            shOldYaw = (float)IsoRimBridge.SetOption("freeYaw", 0f);
            shOldMin = Find.CameraDriver.config.sizeRange.min;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            StartTicks(300, "sh_power");
        }
    }
}
