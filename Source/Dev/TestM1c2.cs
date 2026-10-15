using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using ProjectZ.Patches;

namespace ProjectZ.Dev
{
    // M1c part 2 scripted test (mode m1c2): the user's playtest findings, as screenshots plus checks.
    //   A: a two-storey house: ground room (walls, door, stairs, roof) with a room upstairs on the same wall line; the
    //      upstairs roof comes from the game's own auto-roof and the colonists' roofing work.
    //   B: a ground room with an open upper floor (terrace): a colonist at its north edge, a raider on the ground north.
    // Shots: roofs on/off on floor 1/2, the stacked wall, the roof overlay, the four map corners, a drafted move order to
    // floor 2, facing while aiming between floors, the range ring. Checks: auto-roof upstairs, facing, mortar landing.
    public static partial class TestRunner
    {
        private static CellRect twoStorey, terrace;
        private static Pawn mover, aimer, target2;
        private static IntVec3 moveDest;
        private static Thing turret;
        private static int shotIndex;
        private static readonly List<KeyValuePair<string, Action<Map>>> shots = new List<KeyValuePair<string, Action<Map>>>();
        internal static IntVec3 lastExplosion = IntVec3.Invalid;

        private static IntVec3 Above(IntVec3 g, Map map) => ProjectZ.CellOnFloor(g, 1, map);

        private static bool StepM1c2(Map map)
        {
            switch (stage)
            {
                case "d_setup":
                    SetupM1c2(map);
                    return true;
                case "d_roof_check":
                {
                    tickWatch = null;
                    int inner = twoStorey.ContractedBy(1).Area;
                    int roofed = twoStorey.ContractedBy(1).Cells.Count(g => map.roofGrid.RoofAt(Above(g, map)) == RoofDefOf.RoofConstructed);
                    int area = twoStorey.ContractedBy(1).Cells.Count(g => map.areaManager.BuildRoof[Above(g, map)]);
                    Result("upstairs room gets the game's auto-roof area", area == inner, $"{area} of {inner} upstairs cells in the build-roof area");
                    Result("colonists build the upstairs roof", roofed == inner, $"{roofed} of {inner} roofed after {ticksTotal - ticksLeft} ticks");
                    Result("ground under the upper floor keeps the floor's roof", twoStorey.Cells.All(g => map.roofGrid.RoofAt(g) == PZDefOf.PZ_FloorRoof), "");
                    BuildShots(map);
                    shotIndex = 0;
                    stage = "d_shot";
                    return true;
                }
                case "d_shot":
                    if (shotIndex >= shots.Count)
                    {
                        stage = "d_move";
                        return true;
                    }
                    shots[shotIndex].Value(map);
                    Wait(40, "d_shot_take");
                    return true;
                case "d_shot_take":
                    Capture("m1c2_" + shots[shotIndex].Key);
                    shotIndex++;
                    stage = "d_shot";
                    return true;
                case "d_move":
                    StartMove(map);
                    return true;
                case "d_move_shot":
                    tickWatch = null;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Info($"move order: {mover} at {mover.Position} (floor {ProjectZ.FloorOf(mover.Position, map) + 1}) -> {moveDest}, path {mover.pather.curPath}, nodes {mover.pather.curPath?.NodesLeftCount}");
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(mover);
                    FloorView.Set(1);
                    IsoRimBridge.SetRoofs(false);
                    Find.CameraDriver.SetRootPosAndSize(new Vector3(twoStorey.CenterCell.x + 0.5f, 0f, twoStorey.minZ - 2f), 12f);
                    Wait(40, "d_move_take");
                    return true;
                case "d_move_take":
                    Capture("m1c2_move_order");
                    stage = "d_move_go";
                    return true;
                case "d_move_go":
                    TickUntil(4000, () => mover.Position == moveDest || ProjectZ.FloorOf(mover.Position, map) == 1 && !mover.pather.Moving, "d_move_check", 10);
                    return true;
                case "d_move_check":
                    tickWatch = null;
                    Result("drafted colonist ordered to floor 2 gets there", mover.Position == moveDest, $"{mover} at {mover.Position}, wanted {moveDest}");
                    Find.Selector.ClearSelection();
                    stage = "d_face";
                    return true;
                case "d_face":
                    StartFacing(map);
                    return true;
                case "d_face_wait":
                case "d_face2_wait":
                    // Facing is the game's per-frame post-tick visual (PostTickVisuals runs only on frames the game ticked
                    // itself, not on the test's DoSingleTick): run the same UpdateRotation once, as that frame would.
                    tickWatch = null;
                    (stage == "d_face_wait" ? aimer : target2).rotationTracker.UpdateRotation();
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(10, stage == "d_face_wait" ? "d_face_check" : "d_face2_check");
                    return true;
                case "d_face_check":
                {
                    tickWatch = null;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Stance_Busy busy = aimer.stances.curStance as Stance_Busy;
                    Result("colonist upstairs faces the raider below while aiming (north)", busy != null && aimer.Rotation == Rot4.North,
                        $"{aimer} at {aimer.Position} stance {aimer.stances.curStance?.GetType().Name} -> {busy?.focusTarg} ; rotation {aimer.Rotation.ToStringHuman()}");
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(aimer);
                    FloorView.Set(1);
                    Find.CameraDriver.SetRootPosAndSize(new Vector3(terrace.CenterCell.x + 0.5f, 0f, terrace.maxZ + 3.5f), 7f);
                    Wait(40, "d_face_take");
                    return true;
                }
                case "d_face_take":
                    Capture("m1c2_facing_up_shooter");
                    stage = "d_face2";
                    return true;
                case "d_face2":
                    // The raider aims back up.
                    target2.jobs.StopAll();
                    target2.jobs.StartJob(JobMaker.MakeJob(JobDefOf.AttackStatic, aimer), JobCondition.InterruptForced);
                    aimer.jobs.StopAll();
                    aimer.drafter.FireAtWill = false;
                    TickUntil(400, () => target2.stances.curStance is Stance_Warmup, "d_face2_wait", 1);
                    return true;
                case "d_face2_check":
                {
                    tickWatch = null;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Stance_Busy busy = target2.stances.curStance as Stance_Busy;
                    Result("raider on the ground faces the colonist upstairs while aiming (south)", busy != null && target2.Rotation == Rot4.South,
                        $"{target2} at {target2.Position} stance {target2.stances.curStance?.GetType().Name}; rotation {target2.Rotation.ToStringHuman()}");
                    Wait(20, "d_face2_take");
                    return true;
                }
                case "d_face2_take":
                    Capture("m1c2_facing_raider");
                    stage = "d_ring";
                    return true;
                case "d_ring":
                    target2.jobs.StopAll();
                    // Range ring: the upstairs colonist's weapon, targeting.
                    Verb v = aimer.TryGetAttackVerb(null);
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(aimer);
                    if (v != null)
                    {
                        Find.Targeter.BeginTargeting(v);
                    }
                    Find.CameraDriver.SetRootPosAndSize(new Vector3(terrace.CenterCell.x + 0.5f, 0f, terrace.CenterCell.z + 0.5f), 22f);
                    Wait(40, "d_ring_take");
                    return true;
                case "d_ring_take":
                    Info($"targeting {Find.Targeter.IsTargeting} by {aimer}");
                    Capture("m1c2_range_ring");
                    stage = "d_ring2";
                    return true;
                case "d_ring2":
                    Find.Targeter.StopTargeting();
                    // A turret upstairs, selected: the game draws its range ring round it (its selection overlay).
                    turret = Place(DefDatabase<ThingDef>.GetNamed("Turret_MiniTurret"), ThingDefOf.Steel, Above(new IntVec3(terrace.minX + 1, 0, terrace.minZ + 1), map), map);
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(turret);
                    Wait(40, "d_ring2_take");
                    return true;
                case "d_ring2_take":
                    Capture("m1c2_turret_ring");
                    stage = "d_ring3";
                    return true;
                case "d_ring3":
                    Find.Selector.ClearSelection();
                    turret.Destroy();
                    stage = "d_mortar";
                    return true;
                case "d_mortar":
                    StartMortar(map);
                    return true;
                case "d_mortar_check":
                {
                    tickWatch = null;
                    IntVec3 g = terrace.CenterCell;
                    Result("a shell landing on a cell under an upper floor explodes on the floor above", lastExplosion == Above(g, map),
                        $"aimed at ground {g}; exploded at {lastExplosion} (floor {(lastExplosion.IsValid ? ProjectZ.FloorOf(lastExplosion, map) + 1 : 0)}); floor roof below still {map.roofGrid.RoofAt(g)?.defName}");
                    stage = "d_end";
                    return true;
                }
                case "d_end":
                    IsoRimBridge.SetRoofs(false);   // the harness default
                    Find.PlaySettings.showRoofOverlay = false;
                    WatchCheck(map, "m1c2 end");
                    Finish();
                    return true;
            }
            return false;
        }

        private static void SetupM1c2(Map map)
        {
            List<Pawn> cols = Colonists(map);
            IntVec3 near = cols.Count > 0 ? new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z)) : map.Center;
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 79f, true))
            {
                CellRect r = new CellRect(c.x - 12, c.z - 8, 25, 17);
                CellRect m = r.ExpandedBy(2);
                if (!m.InBounds(map) || m.maxZ >= ProjectZ.GroundZ(map) - 12)
                {
                    continue;
                }
                if (m.Cells.All(x => !x.GetTerrain(map).IsWater && !map.roofGrid.Roofed(x) && !x.Fogged(map) && x.GetFirstBuilding(map) == null && x.GetTerrain(map).passability == Traversability.Standable))
                {
                    found = r;
                    break;
                }
            }
            if (found.Area == 0)
            {
                Result("test spot", false, "no open ground near the colonists");
                Finish();
                return;
            }
            foreach (IntVec3 c in found.ExpandedBy(2))
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t is Plant || t.def.category == ThingCategory.Item || t is Filth)
                    {
                        t.Destroy();
                    }
                }
            }
            DebugSettings.enableRandomMentalStates = false;
            int ox = found.minX, oz = found.minZ;
            twoStorey = new CellRect(ox, oz, 9, 7);
            terrace = new CellRect(ox + 14, oz, 7, 7);
            IntVec3 door = new IntVec3(twoStorey.CenterCell.x, 0, twoStorey.minZ);
            foreach (CellRect h in new[] { twoStorey, terrace })
            {
                foreach (IntVec3 c in h.EdgeCells)
                {
                    Place(c == door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
                }
                foreach (IntVec3 c in h)
                {
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                    map.terrainGrid.SetTerrain(Above(c, map), PZDefOf.PZ_UpperFloor);
                }
            }
            // Stairs inside the two-storey house (built), walls upstairs on the same line; the roof up there is left to
            // the game (auto-roof) and the colonists.
            IntVec3 stairCell = new IntVec3(twoStorey.maxX - 1, 0, twoStorey.maxZ - 1);
            Place(PZDefOf.PZ_Stairs, ThingDefOf.Steel, stairCell, map);
            foreach (IntVec3 g in twoStorey.EdgeCells)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, Above(g, map), map);
            }
            IntVec3 store = new IntVec3(twoStorey.CenterCell.x, 0, twoStorey.minZ - 3);
            for (int i = 0; i < 2; i++)
            {
                Thing t = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                t.stackCount = t.def.stackLimit;
                GenPlace.TryPlaceThing(t, store, map, ThingPlaceMode.Near);
            }
            foreach (Pawn p in cols)
            {
                p.drafter.Drafted = false;
                p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
                if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Construction))
                {
                    p.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                }
            }
            moveDest = Above(new IntVec3(twoStorey.minX + 2, 0, twoStorey.minZ + 2), map);
            Info($"two-storey house {twoStorey} (door {door}, stairs {stairCell}), terrace {terrace}; ground rows {ProjectZ.GroundZ(map)}, map {map.Size}");
            TickUntil(30000, () => twoStorey.ContractedBy(1).Cells.All(g => map.roofGrid.RoofAt(Above(g, map)) == RoofDefOf.RoofConstructed), "d_roof_check", 120);
        }

        private static void View(Map map, int floor, bool roofs, Vector3 at, float size, bool roofOverlay = false)
        {
            FloorView.Set(floor);
            IsoRimBridge.SetRoofs(roofs);
            Find.PlaySettings.showRoofOverlay = roofOverlay;
            CameraDriver_Update_Patch.AllowAnnexView = false;
            Find.UIRoot.screenshotMode.Active = false;
            Find.Selector.ClearSelection();
            Find.CameraDriver.SetRootPosAndSize(at, size);
        }

        private static void BuildShots(Map map)
        {
            shots.Clear();
            Vector3 a = new Vector3(twoStorey.CenterCell.x + 4.5f, 0f, twoStorey.CenterCell.z + 0.5f);
            Vector3 corner = new Vector3(twoStorey.maxX + 1f, 0f, twoStorey.minZ + 1f);
            int gz = ProjectZ.GroundZ(map);
            int sx = map.Size.x;
            shots.Add(new KeyValuePair<string, Action<Map>>("roofs_f2_on", m => View(m, 1, true, a, 13f)));
            shots.Add(new KeyValuePair<string, Action<Map>>("roofs_f2_off", m => View(m, 1, false, a, 13f)));
            shots.Add(new KeyValuePair<string, Action<Map>>("roofs_f1_on", m => View(m, 0, true, a, 13f)));
            shots.Add(new KeyValuePair<string, Action<Map>>("roofs_f1_off", m => View(m, 0, false, a, 13f)));
            shots.Add(new KeyValuePair<string, Action<Map>>("walls_close", m => View(m, 1, false, corner, 5f)));
            shots.Add(new KeyValuePair<string, Action<Map>>("roof_overlay", m => View(m, 1, false, a, 13f, true)));
            shots.Add(new KeyValuePair<string, Action<Map>>("edge_sw", m => View(m, 1, true, new Vector3(4f, 0f, 4f), 22f)));
            shots.Add(new KeyValuePair<string, Action<Map>>("edge_nw", m => View(m, 1, true, new Vector3(4f, 0f, gz - 4f), 22f)));
            shots.Add(new KeyValuePair<string, Action<Map>>("edge_ne", m => View(m, 1, true, new Vector3(sx - 4f, 0f, gz - 4f), 22f)));
            shots.Add(new KeyValuePair<string, Action<Map>>("edge_se", m => View(m, 1, true, new Vector3(sx - 4f, 0f, 4f), 22f)));
            shots.Add(new KeyValuePair<string, Action<Map>>("edge_nw_f1", m => View(m, 0, true, new Vector3(4f, 0f, gz - 4f), 22f)));
        }

        private static void StartMove(Map map)
        {
            View(map, 1, false, new Vector3(twoStorey.CenterCell.x + 0.5f, 0f, twoStorey.CenterCell.z + 0.5f), 12f);
            List<Pawn> cols = Colonists(map);
            mover = cols.First();
            Teleport(mover, new IntVec3(twoStorey.CenterCell.x + 3, 0, twoStorey.minZ - 4));
            mover.drafter.Drafted = true;
            Job job = JobMaker.MakeJob(JobDefOf.Goto, moveDest);
            bool ok = mover.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder);
            Result("move order to floor 2 accepted", ok, $"{mover} -> {moveDest}");
            TickUntil(60, () => mover.pather.curPath != null && mover.pather.curPath.NodesLeftCount > 2, "d_move_shot", 1);
        }

        private static void StartFacing(Map map)
        {
            List<Pawn> cols = Colonists(map);
            aimer = cols.FirstOrDefault(p => p != mover && !p.WorkTagIsDisabled(WorkTags.Violent)) ?? cols.First(p => p != mover);
            Thing rifle = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle"));
            aimer.equipment.DestroyAllEquipment();
            aimer.equipment.AddEquipment((ThingWithComps)rifle);
            IntVec3 edge = Above(new IntVec3(terrace.CenterCell.x, 0, terrace.maxZ), map);
            Teleport(aimer, edge);
            aimer.drafter.Drafted = true;
            aimer.drafter.FireAtWill = false;
            target2 = MakeRaider(map, new IntVec3(terrace.CenterCell.x + 1, 0, terrace.maxZ + 8));
            target2.jobs.StopAll();
            Result("upstairs colonist can hit the raider", CanHit(aimer, target2), $"{aimer} at {edge} -> {target2} at {target2.Position}");
            bool ok = aimer.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.AttackStatic, target2), JobTag.DraftedOrder);
            Info($"attack order {ok}");
            TickUntil(400, () => aimer.stances.curStance is Stance_Warmup, "d_face_wait", 1);
        }

        private static void StartMortar(Map map)
        {
            foreach (Pawn p in new[] { target2 })
            {
                if (p != null && p.Spawned)
                {
                    p.Destroy();
                }
            }
            if (aimer != null && aimer.Spawned)
            {
                Teleport(aimer, new IntVec3(terrace.minX - 3, 0, terrace.minZ - 6));
            }
            IntVec3 g = terrace.CenterCell;
            ThingDef shellDef = DefDatabase<ThingDef>.GetNamed("Bullet_Shell_HighExplosive");
            IntVec3 from = new IntVec3(g.x, 0, Mathf.Max(1, g.z - 30));
            Projectile shell = (Projectile)GenSpawn.Spawn(shellDef, from, map);
            Pawn launcher = Colonists(map).First();
            shell.Launch(launcher, from.ToVector3Shifted(), new LocalTargetInfo(g), new LocalTargetInfo(g), ProjectileHitFlags.IntendedTarget);
            lastExplosion = IntVec3.Invalid;
            Info($"shell {shell} launched from {from} at ground {g} (under the terrace)");
            TickUntil(1500, () => lastExplosion.IsValid, "d_mortar_check", 1);
        }
    }

    // Test only: where explosions start (the mortar check).
    public static class TestExplosionWatch
    {
        public static void Prefix(IntVec3 center)
        {
            TestRunner.lastExplosion = center;
        }
    }
}
