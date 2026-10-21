using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode talltower (run with -projectzfloors=3): sun shadows of tall buildings (IsoRim Floors.DrawSunShadow).
    //   Colonists build a 3 x 3 wood tower T through the designators and ticks: the ground walls, the floor-2 slab, the
    //   floor-2 walls (a door in the south one), the floor-3 slab, the floor-3 walls, the roof on top. A house A south of
    //   it (placed) holds the stairs up to floor 2 and, on its floor-2 slab, the stairs up to floor 3. References (placed):
    //   R a one-storey 3 x 3 wall block, F a wall on a floor-2 slab over a roofed 3 x 3 room. Everything stands on
    //   concrete (an even receiver). Upper floors are hidden (ground view) while the colonists work.
    //   Shots at 7, 9, 12, 15, 17 h and 0 h (moon), yaw 45 and 225, viewing floor 3; a hidden-floors shot (ground view);
    //   close-ups of the joints between the storeys' shadows at 7 and 17 h, with IsoRim's gap fill and without (A/B).
    //   Then colonists take the top roof off and deconstruct the floor-3 walls (shots), take up the floor-3 slab and the
    //   roof under it and deconstruct the floor-2 walls (shots).
    //   PROFILE / JOINT lines in results.txt: screen pixels (bottom-left origin) along the cast from each caster
    //   (tools: D:\RimWorld17Shot2\tools\tallshadow.ps1 measures the reach in storeys and the joints).
    public static partial class TestRunner
    {
        private static IntVec3 hO;
        private static CellRect hRegion, hTower, hHouse, hRef, hRoom;
        private static IntVec3 hDoor, hStairs1, hStairs2, hFloat;
        private static int hStep, hShot, hTries;
        private static float hSize, hOldYaw, hOldMin;
        private static string hPhase = "";
        private static readonly List<HStep> hSteps = new List<HStep>();
        private static List<HShot> hShots = new List<HShot>();

        private class HStep
        {
            public string name;
            public Action<Map> plan;
            public Func<Map, bool> done;
            public int maxTicks;
            public List<HShot> shots;
        }

        private class HShot
        {
            public string name;
            public int hour;
            public float yaw;
            public int floor;          // viewed floor (0 ground: the upper floors hidden)
            public string close;       // null, or "j1"/"j2": a close-up of the tower's joint (ground/floor 2, floor 2/floor 3)
            public bool noFill;        // IsoRim's gap fill off (A/B)
            public float towerH;       // the tower's expected height in cast lengths (for the frame and the profile's length)
        }

        private static IntVec3 H(int dx, int dz) => new IntVec3(hO.x + dx, 0, hO.z + dz);

        private static IntVec3 OnF(IntVec3 g, int k, Map map) => k == 0 ? g : ProjectZ.CellOnFloor(g, k, map);

        // Storeys in cast lengths (IsoRim: wall height = 1 cast length, a storey a little more).
        private static float HS => IsoRimBridge.StoreyHeight / IsoRimBridge.WallHeight;

        private static bool StepTallTower(Map map)
        {
            switch (stage)
            {
                case "h_setup":
                    SetupTallTower(map);
                    return true;
                case "h_step":
                {
                    if (hStep >= hSteps.Count)
                    {
                        Wait(5, "h_end");
                        return true;
                    }
                    HStep s = hSteps[hStep];
                    hPhase = s.name;
                    if (s.plan == null)
                    {
                        Wait(1, "h_step_done");
                        return true;
                    }
                    FloorView.Set(0);
                    foreach (Pawn p in Colonists(map))
                    {
                        Draft(p, false);
                    }
                    s.plan(map);
                    SetWork(map, true);
                    foreach (Pawn p in Colonists(map))
                    {
                        // hauling too: wood dropped on a blueprint (a botch) is carried away
                        if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                        {
                            p.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2);
                        }
                    }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(s.maxTicks, () => s.done(map), "h_step_done", 120);
                    return true;
                }
                case "h_step_done":
                {
                    tickWatch = null;
                    HStep s = hSteps[hStep];
                    if (s.plan != null)
                    {
                        SetWork(map, false);
                        bool ok = s.done(map);
                        Result("T " + s.name, ok, $"after {ticksTotal} ticks; {TowerState(map)}" + (hOrders > 0 ? $"; prioritized by the player {hOrders} times" : ""));
                        hOrders = 0;
                        if (!ok)
                        {
                            for (int k = 0; k < 3; k++)
                            {
                                foreach (IntVec3 c in Ring(map, k).Concat(Inner(map, k)).Distinct())
                                {
                                    if (c.GetThingList(map).Any(t => t is Blueprint || t is Frame))
                                    {
                                        Info($"unfinished at {c} (floor {k + 1}): {string.Join(",", c.GetThingList(map).Select(t => t.def.defName))}");
                                        YWhyNot(map, c);
                                    }
                                }
                            }
                            Info("peel now: " + PeelState(map, 2));
                            WorkGiver_Scanner rr = DefDatabase<WorkGiverDef>.GetNamed("RemoveRoofs").Worker as WorkGiver_Scanner;
                            Pawn p0 = Colonists(map).FirstOrDefault();
                            for (int k = 1; k < 3; k++)
                            {
                                Info($"roofs floor {k + 1}: " + string.Join("; ", Inner(map, k).Select(c => $"{c} roof {map.roofGrid.RoofAt(c)?.defName ?? "-"} noRoof {map.areaManager.NoRoof[c]} buildRoof {map.areaManager.BuildRoof[c]} job {(p0 != null && rr != null ? rr.HasJobOnCell(p0, c, true) : false)}")));
                            }
                            foreach (Pawn p in Colonists(map))
                            {
                                Info($"{p} at {p.Position} (floor {ProjectZ.FloorOf(p.Position, map) + 1}) job {p.CurJob?.def.defName} {p.CurJob?.targetA}");
                            }
                            Wait(5, "h_end");
                            return true;
                        }
                    }
                    hShots = s.shots ?? new List<HShot>();
                    hShot = 0;
                    Wait(5, "h_shot");
                    return true;
                }
                case "h_shot":
                {
                    if (hShot == 0 && hShots.Count > 0)
                    {
                        hOldYaw = (float)IsoRimBridge.SetOption("freeYaw", 0f);
                        hOldMin = Find.CameraDriver.config.sizeRange.min;
                        ParkColonists(map);
                    }
                    if (hShot >= hShots.Count)
                    {
                        if (hShots.Count > 0)
                        {
                            IsoRimBridge.SetOption("freeYaw", hOldYaw);
                            GapFill(true);
                            Find.CameraDriver.config.sizeRange.min = hOldMin;
                        }
                        hStep++;
                        Wait(5, "h_step");
                        return true;
                    }
                    HShot sh = hShots[hShot];
                    SetHour(map, sh.hour);
                    ClearSnow(map);
                    IsoRimBridge.SetOption("freeYaw", sh.yaw);
                    GapFill(!sh.noFill);
                    hTries = 0;
                    hSize = sh.close != null ? 1.6f : 14f;
                    Find.CameraDriver.config.sizeRange.min = sh.close != null ? 1f : hOldMin;
                    Wait(5, "h_shot_frame");   // the sky sets this hour's cast vector first (the frame follows the shadow)
                    return true;
                }
                case "h_shot_frame":
                    HFrame(map, hShots[hShot]);
                    Wait(45, "h_shot_fit");
                    return true;
                case "h_shot_fit":
                {
                    HShot sh = hShots[hShot];
                    if (sh.close == null && hTries < 6 && !AllOnScreen(map, sh))
                    {
                        hTries++;
                        hSize *= 1.2f;
                        HFrame(map, sh);
                        Wait(30, "h_shot_fit");
                        return true;
                    }
                    Wait(15, "h_shot_take");
                    return true;
                }
                case "h_shot_take":
                {
                    HShot sh = hShots[hShot];
                    Profiles(map, sh);
                    foreach (Window w in Find.WindowStack.Windows.Where(w => w.layer == WindowLayer.Dialog).ToList())
                    {
                        Find.WindowStack.TryRemove(w, doCloseSound: false);   // (the game's "name your faction" and the like)
                    }
                    ZCapture(sh.name);
                    hShot++;
                    Wait(2, "h_shot");
                    return true;
                }
                case "h_end":
                    GapFill(true);
                    WatchCheck(map, "talltower end");
                    Finish();
                    return true;
            }
            return false;
        }

        // ---- the scene

        private static void SetupTallTower(Map map)
        {
            List<Pawn> cols = Colonists(map);
            IntVec3 near = cols.Count > 0 ? new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z)) : map.Center;
            int groundZ = ProjectZ.GroundZ(map);
            CellRect found = CellRect.Empty;
            // T at (0..2, 0..2); its shadow reaches about 21 cells west and east at 7 and 17 h, 8 north; A, R, F to the south.
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 79f, true))
            {
                CellRect r = new CellRect(c.x - 32, c.z - 25, 70, 50);
                if (!r.InBounds(map) || r.maxZ >= groundZ - 3)
                {
                    continue;
                }
                if (r.Cells.All(x => !x.Fogged(map) && !map.roofGrid.Roofed(x) && !x.GetTerrain(map).IsWater && x.GetTerrain(map).passability != Traversability.Impassable && x.GetThingList(map).All(t => t is Pawn || t.def.destroyable)))
                {
                    found = r;
                    break;
                }
            }
            if (found.Area == 0)
            {
                Result("test spot", false, "no open ground near the colonists (70 x 50, unroofed, no water, nothing indestructible)");
                Finish();
                return;
            }
            hRegion = found;
            hO = new IntVec3(found.minX + 32, 0, found.minZ + 25);
            TerrainDef concrete = DefDatabase<TerrainDef>.GetNamedSilentFail("Concrete") ?? TerrainDefOf.Concrete;
            foreach (IntVec3 c in hRegion)
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (!(t is Pawn) && t.def.destroyable)
                    {
                        t.Destroy();
                    }
                }
                map.terrainGrid.SetTerrain(c, concrete);
                map.snowGrid.SetDepth(c, 0f);
            }
            DebugSettings.enableRandomMentalStates = false;
            Find.Storyteller.storytellerComps.Clear();   // no raids or other incidents while the colonists build (test only)
            Find.PlaySettings.autoRebuild = false;
            hTower = new CellRect(hO.x, hO.z, 3, 3);
            hDoor = H(1, 0);
            // House A (5 x 7, placed): walls, a south door, a roof, its floor-2 slab but over the ground stairs' well.
            hHouse = new CellRect(hO.x - 1, hO.z - 7, 5, 7);
            IntVec3 southDoor = H(1, -7);
            foreach (IntVec3 c in hHouse.EdgeCells)
            {
                Place(c == southDoor ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            foreach (IntVec3 c in hHouse)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            hStairs1 = H(2, -4);   // ground -> floor 2, exit (2, -2) on floor 2
            HashSet<IntVec3> well = new HashSet<IntVec3>(StairShape.Ramp(hStairs1, Rot4.North));
            foreach (IntVec3 g in hHouse)
            {
                if (!well.Contains(g))
                {
                    map.terrainGrid.SetTerrain(OnF(g, 1, map), PZDefOf.PZ_UpperFloor);
                }
            }
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, hStairs1, map, Rot4.North);
            // floor 2 -> floor 3 on A's slab, landing (0, -1) on floor 3 beside the tower's door, a floor-3 cell (1, -1) in front
            // of the door (1x4 stairs, 2026-10-20: their solid end must not stand on floor 2's cell in front of the door)
            hStairs2 = OnF(H(0, -3), 1, map);
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, hStairs2, map, Rot4.North);
            map.terrainGrid.SetTerrain(OnF(H(1, -1), 2, map), PZDefOf.PZ_UpperFloor);
            // R: a one-storey 3 x 3 wall block. F: a 3 x 3 room, its floor-2 slab, one wall on it over the room's middle.
            hRef = new CellRect(hO.x, hO.z - 16, 3, 3);
            foreach (IntVec3 c in hRef)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            hRoom = new CellRect(hO.x + 14, hO.z - 16, 3, 3);
            foreach (IntVec3 c in hRoom.EdgeCells)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            foreach (IntVec3 c in hRoom)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(OnF(c, 1, map), PZDefOf.PZ_UpperFloor);
            }
            hFloat = hRoom.CenterCell;
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, OnF(hFloat, 1, map), map);
            // Wood for the tower, south of everything (no shadow falls there).
            for (int i = 0; i < 7; i++)
            {
                Thing w = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                w.stackCount = 75;
                GenSpawn.Spawn(w, H(-3 + i, -22), map);
            }
            foreach (Pawn p in cols)
            {
                // Skilled builders: a botch drops its wood on the cell, and on a corner of a 3 x 3 floor up in the air
                // nobody can reach it to carry it off (the wall there is then never built).
                SkillRecord sk = p.skills?.GetSkill(SkillDefOf.Construction);
                if (sk != null)
                {
                    sk.Level = 20;
                }
                Teleport(p, H(-3 + cols.IndexOf(p), -20));
                Draft(p, false);
            }
            SetWork(map, false);
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            Info($"talltower: region {hRegion}, tower {hTower}, house {hHouse}, stairs {hStairs1} / {hStairs2} (landings {StairShape.Exit(hStairs1, Rot4.North, map)} {StairShape.Exit(hStairs1, Rot4.North, map).GetTerrain(map)?.defName}, {StairShape.Exit(hStairs2, Rot4.North, map)} {StairShape.Exit(hStairs2, Rot4.North, map).GetTerrain(map)?.defName}), ref {hRef}, room {hRoom} wall on its slab {OnF(hFloat, 1, map)}; storey {IsoRimBridge.StoreyHeight:0.000} wall {IsoRimBridge.WallHeight:0.000} = {HS:0.0000} cast lengths; wall sun shadow height {ThingDefOf.Wall.staticSunShadowHeight}; colonists {cols.Count}");
            BuildTallSteps(map);
            hStep = 0;
            Wait(30, "h_step");
        }

        private static List<IntVec3> Ring(Map map, int k) => hTower.EdgeCells.Select(g => OnF(g, k, map)).ToList();

        private static List<IntVec3> Inner(Map map, int k) => hTower.Cells.Select(g => OnF(g, k, map)).ToList();

        private static bool WallAt(IntVec3 c, Map map)
        {
            Building b = c.GetEdifice(map);
            return b != null && (b.def == ThingDefOf.Wall || b.def == ThingDefOf.Door);
        }

        // The ring of floor k: walls, and a door in the south middle above the ground.
        private static void PlanRing(Map map, int k)
        {
            Designator_Build wall = new Designator_Build(ThingDefOf.Wall);
            wall.SetStuffDef(ThingDefOf.WoodLog);
            Designator_Build door = new Designator_Build(ThingDefOf.Door);
            door.SetStuffDef(ThingDefOf.WoodLog);
            List<string> refused = new List<string>();
            foreach (IntVec3 g in hTower.EdgeCells)
            {
                Designator_Build d = k > 0 && g == hDoor ? door : wall;
                IntVec3 c = OnF(g, k, map);
                AcceptanceReport r = d.CanDesignateCell(c);
                if (r.Accepted)
                {
                    d.DesignateSingleCell(c);
                }
                else
                {
                    refused.Add(c + " '" + r.Reason + "'");
                }
            }
            Result($"T floor {k + 1} walls: the designators place them", refused.Count == 0, refused.Count == 0 ? "8 placed" : "refused " + string.Join(", ", refused));
        }

        // The slab of floor k over the tower, placed outward from the cells next to what holds it (as a player drags again).
        private static void PlanSlab(Map map, int k)
        {
            Designator_Build d = new Designator_Build(PZDefOf.PZ_UpperFloor);
            List<IntVec3> want = Inner(map, k);
            for (int pass = 0; pass < 6; pass++)
            {
                foreach (IntVec3 c in want)
                {
                    if (!PlaceWorker_Stairs.FloorPlanned(c, map) && d.CanDesignateCell(c).Accepted)
                    {
                        d.DesignateSingleCell(c);
                    }
                }
            }
            List<IntVec3> missing = want.Where(c => !PlaceWorker_Stairs.FloorPlanned(c, map)).ToList();
            Result($"T floor {k + 1} slab: the designator places it", missing.Count == 0, missing.Count == 0 ? "9 planned" : "not planned " + string.Join(", ", missing.Select(c => c + " '" + d.CanDesignateCell(c).Reason + "'")));
        }

        private static void PlanRoof(Map map, int k, bool on)
        {
            Designator_Cells d = on ? (Designator_Cells)new Designator_AreaBuildRoof() : new Designator_AreaNoRoof();
            List<string> refused = new List<string>();
            foreach (IntVec3 c in Inner(map, k))
            {
                if (!on)
                {
                    map.areaManager.BuildRoof[c] = false;   // the auto-roof's build-roof area off (it would build it again)
                }
                if (on ? map.areaManager.BuildRoof[c] || map.roofGrid.Roofed(c) : map.areaManager.NoRoof[c])
                {
                    continue;   // the game's auto-roof already planned it (an enclosed room)
                }
                AcceptanceReport r = d.CanDesignateCell(c);
                if (r.Accepted)
                {
                    d.DesignateSingleCell(c);
                }
                else
                {
                    refused.Add(c + " '" + r.Reason + "'");
                }
            }
            Result($"T floor {k + 1} roof {(on ? "build" : "remove")} area", refused.Count == 0, refused.Count == 0 ? "9 cells" : "refused " + string.Join(", ", refused));
        }

        private static void PlanDeconstruct(Map map, int k)
        {
            Designator_Deconstruct d = new Designator_Deconstruct();
            List<string> refused = new List<string>();
            foreach (IntVec3 c in Ring(map, k))
            {
                Building b = c.GetEdifice(map);
                AcceptanceReport r = b == null ? "nothing" : d.CanDesignateThing(b);
                if (r.Accepted)
                {
                    d.DesignateThing(b);
                }
                else
                {
                    refused.Add(c + " '" + r.Reason + "'");
                }
            }
            Result($"T floor {k + 1} walls: deconstruct designated", refused.Count == 0, refused.Count == 0 ? "8 designated" : "refused " + string.Join(", ", refused));
        }

        private static void PlanTakeUp(Map map, int k)
        {
            Designator_RemoveFoundation d = AllDesignators().OfType<Designator_RemoveFoundation>().FirstOrDefault();
            List<string> refused = new List<string>();
            foreach (IntVec3 c in Inner(map, k))
            {
                AcceptanceReport r = d == null ? "no tool" : d.CanDesignateCell(c);
                if (r.Accepted)
                {
                    d.DesignateSingleCell(c);
                }
                else
                {
                    refused.Add(c + " '" + r.Reason + "'");
                }
            }
            Info("peel at plan: " + PeelState(map, k));
            Result($"T floor {k + 1} slab: take-up designated", refused.Count == 0, refused.Count == 0 ? "9 designated" : "refused " + string.Join(", ", refused));
        }

        // The colonists left the designated slab alone in some runs (jobs offered, reachable, never taken: a ProjectZ
        // finding, see the report). Here the player steps in as one would: an idle colonist is told to take up the cell
        // ("prioritize", the game's forced work order).
        private static int hOrders;

        private static void PrioritizeTakeUp(Map map)
        {
            WorkGiver_Scanner wg = DefDatabase<WorkGiverDef>.AllDefs.FirstOrDefault(w => w.giverClass == typeof(WorkGiver_ConstructRemoveFoundation))?.Worker as WorkGiver_Scanner;
            if (wg == null || ticksTotal - ticksLeft < 6000)
            {
                return;   // the colonists' own choice first
            }
            foreach (Pawn p in Colonists(map))
            {
                if (p.CurJob != null && p.CurJob.def != JobDefOf.Wait_Wander && p.CurJob.def != JobDefOf.GotoWander)
                {
                    continue;
                }
                IntVec3 c = Inner(map, 2).Where(x => SlabDefs.Is(x.GetTerrain(map)) && wg.HasJobOnCell(p, x, true)).DefaultIfEmpty(IntVec3.Invalid).First();
                if (!c.IsValid)
                {
                    return;
                }
                Job j = wg.JobOnCell(p, c, true);
                if (j != null && p.jobs.TryTakeOrderedJob(j, JobTag.Misc))
                {
                    hOrders++;
                    Out($"  prioritized: {p} takes up {c} (order {hOrders}, tick {ticksTotal - ticksLeft})");
                }
                return;
            }
        }

        private static string PeelState(Map map, int k)
        {
            SlabPeel peel = SlabPeel.For(map);
            WorkGiverDef wd = DefDatabase<WorkGiverDef>.AllDefs.FirstOrDefault(w => w.giverClass == typeof(WorkGiver_ConstructRemoveFoundation));
            WorkGiver_Scanner wg = wd?.Worker as WorkGiver_Scanner;
            Pawn p0 = Colonists(map).FirstOrDefault();
            return string.Join("; ", Inner(map, k).Select(c =>
            {
                JobFailReason.Clear();
                bool job = p0 != null && wg != null && wg.HasJobOnCell(p0, c, true);
                string why = JobFailReason.Reason;
                JobFailReason.Clear();
                bool unforced = p0 != null && wg != null && wg.HasJobOnCell(p0, c, false);
                IntVec3 spot = peel?.Parent(c) ?? IntVec3.Invalid;
                return $"{c} dist {peel?.Distance(c)} workable {peel?.Workable(c)} walk {c.Walkable(map)} blocker '{Slab.Blocker(c, map)}' job {job} '{why}' unforced {unforced} '{JobFailReason.Reason}' touch {p0?.CanReach(c, PathEndMode.Touch, Danger.Deadly)} spot {spot} reach {(spot.IsValid && p0 != null ? p0.CanReach(spot, PathEndMode.OnCell, Danger.Deadly) : false)} forbidden {(p0 != null && c.IsForbidden(p0))} reserve {(p0 != null && p0.CanReserve(c, 1, -1, ReservationLayerDefOf.Floor))}";
            })) + $"; workgiver {wd?.defName} type {wd?.workType?.defName}";
        }

        private static bool RingBuilt(Map map, int k) => Ring(map, k).All(c => WallAt(c, map));

        private static bool RingGone(Map map, int k) => Ring(map, k).All(c => c.GetEdifice(map) == null && !c.GetThingList(map).Any(t => t is Blueprint || t is Frame));

        private static bool SlabBuilt(Map map, int k) => Inner(map, k).All(c => SlabDefs.Is(c.GetTerrain(map)));

        private static bool SlabGone(Map map, int k) => Inner(map, k).All(c => c.GetTerrain(map) == PZDefOf.PZ_OpenAir);

        private static bool Roofed(Map map, int k) => Inner(map, k).All(c => map.roofGrid.RoofAt(c) != null);

        // (not the four corners: a roof over a corner wall can't be reached from inside a 3 x 3 room, as in the game; it
        // stays until the walls go, then falls for want of support)
        private static bool Unroofed(Map map, int k) => hTower.Cells.Where(g => !hTower.IsCorner(g)).Select(g => OnF(g, k, map)).All(c => map.roofGrid.RoofAt(c) == null);

        private static string TowerState(Map map)
        {
            List<string> parts = new List<string>();
            for (int k = 0; k < 3; k++)
            {
                parts.Add($"F{k + 1}: walls {Ring(map, k).Count(c => WallAt(c, map))}/8, slab {(k == 0 ? "-" : Inner(map, k).Count(c => SlabDefs.Is(c.GetTerrain(map))).ToString())}, roofs {Inner(map, k).Count(c => map.roofGrid.RoofAt(c) != null)}");
            }
            return string.Join("; ", parts) + $"; botches {botches}";
        }

        private static void BuildTallSteps(Map map)
        {
            float hs = HS;
            hSteps.Clear();
            hSteps.Add(new HStep { name = "ground walls built by colonists", plan = m => PlanRing(m, 0), done = m => RingBuilt(m, 0), maxTicks = 40000 });
            hSteps.Add(new HStep { name = "floor-2 slab built by colonists", plan = m => PlanSlab(m, 1), done = m => SlabBuilt(m, 1), maxTicks = 40000 });
            hSteps.Add(new HStep { name = "floor-2 walls built by colonists", plan = m => PlanRing(m, 1), done = m => RingBuilt(m, 1), maxTicks = 60000 });
            hSteps.Add(new HStep { name = "floor-3 slab built by colonists", plan = m => PlanSlab(m, 2), done = m => SlabBuilt(m, 2), maxTicks = 40000 });
            hSteps.Add(new HStep { name = "floor-3 walls built by colonists", plan = m => PlanRing(m, 2), done = m => RingBuilt(m, 2), maxTicks = 60000 });
            float full = 2f * hs + 1f;
            List<HShot> main = new List<HShot>();
            foreach (int hour in new[] { 7, 9, 12, 15, 17, 0 })
            {
                foreach (float yaw in new[] { 0f, 180f })
                {
                    main.Add(new HShot { name = $"tall_h{hour:00}_yaw{yaw + 45f:0}_f3", hour = hour, yaw = yaw, floor = 2, towerH = full });
                }
            }
            main.Add(new HShot { name = "tall_h09_yaw45_hidden", hour = 9, yaw = 0f, floor = 0, towerH = full });
            main.Add(new HShot { name = "tall_h15_yaw45_hidden", hour = 15, yaw = 0f, floor = 0, towerH = full });
            foreach (int hour in new[] { 7, 17 })
            {
                foreach (string j in new[] { "j1", "j2" })
                {
                    main.Add(new HShot { name = $"joint_h{hour:00}_{j}_fill", hour = hour, yaw = 0f, floor = 2, close = j, towerH = full });
                    main.Add(new HShot { name = $"joint_h{hour:00}_{j}_nofill", hour = hour, yaw = 0f, floor = 2, close = j, noFill = true, towerH = full });
                }
            }
            hSteps.Add(new HStep { name = "roof on top built by colonists", plan = m => PlanRoof(m, 2, true), done = m => Roofed(m, 2), maxTicks = 30000, shots = main });
            // Removal: the top roof first (the walls hold it), then the floor-3 walls.
            hSteps.Add(new HStep { name = "roof on top removed by colonists", plan = m => PlanRoof(m, 2, false), done = m => Unroofed(m, 2), maxTicks = 30000 });
            float two = hs + 1f;
            hSteps.Add(new HStep
            {
                name = "floor-3 walls deconstructed by colonists", plan = m => PlanDeconstruct(m, 2), done = m => RingGone(m, 2), maxTicks = 40000,
                shots = new List<HShot>
                {
                    new HShot { name = "rm3_h09_yaw45_f3", hour = 9, yaw = 0f, floor = 2, towerH = two },
                    new HShot { name = "rm3_h15_yaw225_f3", hour = 15, yaw = 180f, floor = 2, towerH = two },
                    new HShot { name = "rm3_h09_yaw45_hidden", hour = 9, yaw = 0f, floor = 0, towerH = two },
                },
            });
            // Then the floor-3 slab (the floor-2 walls hold it) and the roof it leaves, then the floor-2 walls.
            hSteps.Add(new HStep { name = "floor-3 slab taken up by colonists", plan = m => PlanTakeUp(m, 2), done = m => { PrioritizeTakeUp(m); return SlabGone(m, 2); }, maxTicks = 90000 });
            hSteps.Add(new HStep { name = "floor-2 roof removed by colonists", plan = m => PlanRoof(m, 1, false), done = m => Unroofed(m, 1), maxTicks = 30000 });
            hSteps.Add(new HStep
            {
                name = "floor-2 walls deconstructed by colonists", plan = m => PlanDeconstruct(m, 1), done = m => RingGone(m, 1), maxTicks = 40000,
                shots = new List<HShot>
                {
                    new HShot { name = "rm2_h09_yaw45_f3", hour = 9, yaw = 0f, floor = 2, towerH = 1f },
                    new HShot { name = "rm2_h15_yaw225_f3", hour = 15, yaw = 180f, floor = 2, towerH = 1f },
                    new HShot { name = "rm2_h09_yaw45_hidden", hour = 9, yaw = 0f, floor = 0, towerH = 1f },
                },
            });
        }

        // ---- shots

        // Colonists out of every shadow (south of everything), drafted so they stay.
        private static void ParkColonists(Map map)
        {
            List<Pawn> cols = Colonists(map);
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], H(-6 + i * 2, -23));
                Draft(cols[i], true);
            }
        }

        private static void ClearSnow(Map map)
        {
            foreach (IntVec3 c in hRegion)
            {
                if (map.snowGrid.GetDepth(c) > 0f)
                {
                    map.snowGrid.SetDepth(c, 0f);
                }
            }
        }

        private static void GapFill(bool on)
        {
            IsoRimBridge.SetShadowGapFill(on);
        }

        private static Vector2 Cast()
        {
            Vector4 v;
            return Shader.TryGetGlobalVector(ShaderPropertyIDs.MapSunLightDirection, out v) ? new Vector2(v.x, v.z) : Vector2.zero;
        }

        private static Vector2 Dir(Vector2 cast) => cast.sqrMagnitude > 1e-6f ? cast.normalized : new Vector2(0f, 1f);

        // Distance from a rect's centre to its edge along a direction.
        private static float Exit(CellRect r, Vector2 d)
        {
            float hx = r.Width * 0.5f, hz = r.Height * 0.5f;
            float ex = Mathf.Abs(d.x) > 1e-4f ? hx / Mathf.Abs(d.x) : float.MaxValue;
            float ez = Mathf.Abs(d.y) > 1e-4f ? hz / Mathf.Abs(d.y) : float.MaxValue;
            return Mathf.Min(ex, ez);
        }

        private static Vector2 Mid(CellRect r) => new Vector2(r.minX + r.Width * 0.5f, r.minZ + r.Height * 0.5f);

        // The casters measured: (name, footprint, expected height in cast lengths).
        private static List<(string obj, CellRect rect, float h)> Casters(HShot sh)
        {
            float hs = HS;
            return new List<(string, CellRect, float)>
            {
                ("tower", hTower, sh.towerH),
                ("ref", hRef, 1f),
                ("float", new CellRect(hFloat.x, hFloat.z, 1, 1), hs + 1f),
            };
        }

        private static void HFrame(Map map, HShot sh)
        {
            Vector2 cast = Cast();
            Vector2 d = Dir(cast);
            Vector2 c = Mid(hTower);
            Vector2 at;
            if (sh.close != null)
            {
                float e = Exit(hTower, d);
                float s = e + (sh.close == "j1" ? 1f : HS + 1f) * cast.magnitude;
                at = c + d * s;
            }
            else
            {
                // the middle of everything measured: the tower, its shadow's end, the references
                Vector2 end = c + d * (Exit(hTower, d) + sh.towerH * cast.magnitude + 2f);
                Vector2 lo = Vector2.Min(Vector2.Min(c, end), Mid(hRef) - new Vector2(2f, 2f));
                Vector2 hi = Vector2.Max(Vector2.Max(c, end), Mid(hRoom) + new Vector2(2f, 2f));
                at = (lo + hi) * 0.5f;
            }
            ShotAt(map, sh.floor, new IntVec3(Mathf.FloorToInt(at.x), 0, Mathf.FloorToInt(at.y)), hSize);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        }

        private static List<Vector2> ProfilePoints(CellRect rect, float h, Vector2 cast, out float e, out float step)
        {
            Vector2 d = Dir(cast);
            Vector2 c = Mid(rect);
            e = Exit(rect, d);
            step = 0.1f;
            float end = e + h * cast.magnitude + 3f;
            List<Vector2> pts = new List<Vector2>();
            for (float s = e; s <= end + 1e-4f; s += step)
            {
                pts.Add(c + d * s);
            }
            return pts;
        }

        private static bool OnScreen(Vector2 p)
        {
            Vector2 s = IsoRimBridge.ScreenOf(new Vector3(p.x, 0f, p.y));
            return s.x >= 6f && s.y >= 6f && s.x <= Screen.width - 6f && s.y <= Screen.height - 6f;
        }

        private static bool AllOnScreen(Map map, HShot sh)
        {
            Vector2 cast = Cast();
            foreach (var cs in Casters(sh))
            {
                foreach (Vector2 p in ProfilePoints(cs.rect, cs.h, cast, out float e, out float st))
                {
                    if (!OnScreen(p))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static string Px(Vector2 p)
        {
            Vector2 s = IsoRimBridge.ScreenOf(new Vector3(p.x, 0f, p.y));
            return $"{s.x:0.0},{s.y:0.0}";
        }

        private static void Profiles(Map map, HShot sh)
        {
            Vector2 cast = Cast();
            float strength = GenCelestial.CurShadowStrength(map);
            string head = $"{sh.name} screen={Screen.width}x{Screen.height} hour={GenLocalDate.HourOfDay(map)} view=f{sh.floor + 1} fill={(sh.noFill ? 0 : 1)} cast={cast.x:0.000},{cast.y:0.000} len={cast.magnitude:0.000} strength={strength:0.00} storey={HS:0.0000}";
            if (sh.close != null)
            {
                Vector2 d = Dir(cast);
                float e = Exit(hTower, d);
                float s0 = e + (sh.close == "j1" ? 1f : HS + 1f) * cast.magnitude;
                List<string> pts = new List<string>();
                for (float s = s0 - 0.5f; s <= s0 + 0.5f + 1e-4f; s += 0.01f)
                {
                    pts.Add(Px(Mid(hTower) + d * s));
                }
                results.Add($"JOINT {head} obj=tower joint={sh.close} at={s0:0.000} from={s0 - 0.5f:0.000} step=0.01 pts={string.Join(";", pts)}");
                return;
            }
            foreach (var cs in Casters(sh))
            {
                List<Vector2> p = ProfilePoints(cs.rect, cs.h, cast, out float e, out float step);
                results.Add($"PROFILE {head} obj={cs.obj} expect={cs.h:0.000} e={e:0.000} skip={(cs.obj == "float" ? Exit(hRoom, Dir(cast)) : 0f):0.000} step={step:0.00} onscreen={(p.All(OnScreen) ? 1 : 0)} pts={string.Join(";", p.Select(Px))}");
            }
        }
    }
}
