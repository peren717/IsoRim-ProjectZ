using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // Stairs batch test (mode stairs, design isorim-pz-stairs-jump-design.md §5): 1x3 stairs and stairwell, rebuilding
    // from either floor, jumping down, the slab height rule, the wall-height lock, the ornate door under a floor, with
    // screenshots. The houses, walls and roofs are placed directly; everything under test runs as real jobs, lords and
    // designators with real ticks.
    //   house A: 9x7, stairs inside (east side, up to the north), a floor over all of it but the stairwell;
    //   house B: 7x7, a floor over its west half only; two ornate doors in its south wall (one under the floor);
    //   flows: F4 jump only without a route (+ save mid-jump), stairs shots, F1 break the stairs with everyone upstairs,
    //          F5 the ground never goes up, F6 raiders upstairs, F7 rescue by jumping + panic from fire, F2 rebuild from
    //          upstairs only (+ save mid-frame), F3 auto-rebuild, height rule, lock, door, ghost shots.
    public static partial class TestRunner
    {
        private static CellRect sHouseA, sHouseB;
        private static IntVec3 stairPos;
        private static readonly Rot4 StairRot = Rot4.North;
        private static IntVec3 farGround;
        private static string walkerId, builderId, carrierId, patientId, panicId, raiderId, fleerId;
        private static int tripsLeft, climbsAt, jumpsAt, jumpsColAt, jumpsHosAt, resetsAt, landingsAt;
        private static int rimTicks, offRimWorkTicks, jumpsBeforeLoad;
        private static bool hungry;
        private static bool savedJump, savedFrame;
        private static IntVec3 jumpFrom;
        private static readonly List<Thing> tempWalls = new List<Thing>();

        private static Pawn ById(Map map, string id) => id == null ? null : map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.ThingID == id);

        private static IntVec3 UpS(IntVec3 g, Map map) => ProjectZ.CellOnFloor(g, 1, map);

        private static Building_Stairs StairsA(Map map) => map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).OfType<Building_Stairs>().FirstOrDefault(s => sHouseA.Contains(s.Position));

        private static List<IntVec3> UpInner(Map map, CellRect h) => h.ContractedBy(1).Cells.Select(g => UpS(g, map)).Where(c => c.Standable(map) && c.GetFirstBuilding(map) == null && !Stairwell.StairThingAt(FloorRoof.Below(c, map), map)).ToList();

        private static bool HasBruise(Pawn p) => p != null && p.health.hediffSet.hediffs.Any(h => h.def.defName == "Bruise");

        private static void Heal(Pawn p)
        {
            foreach (Hediff h in p.health.hediffSet.hediffs.Where(h => h is Hediff_Injury).ToList())
            {
                p.health.RemoveHediff(h);
            }
        }

        private static void Draft(Pawn p, bool on)
        {
            if (p.drafter != null)
            {
                p.drafter.Drafted = on;
                if (on)
                {
                    p.drafter.FireAtWill = false;
                }
            }
        }

        private static void GoTo(Pawn p, IntVec3 c)
        {
            Draft(p, true);
            p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, c), JobTag.DraftedOrder);
        }

        private static void Mark()
        {
            climbsAt = climbsColonists + climbsHostiles;
            jumpsAt = PZStats.jumps;
            jumpsColAt = jumpsColonists;
            jumpsHosAt = jumpsHostiles;
            resetsAt = PZStats.climbResets;
            landingsAt = PZStats.landings;
        }

        private static string Counts() => $"stair steps {climbsColonists + climbsHostiles - climbsAt}, jumps {PZStats.jumps - jumpsAt} (colonists {jumpsColonists - jumpsColAt}, hostiles {jumpsHostiles - jumpsHosAt}), landings {PZStats.landings - landingsAt}, jump routes {PZStats.jumpRoutes}, rim routes {PZStats.rimRoutes}";

        private static void ShotAt(Map map, int floor, IntVec3 g, float size, bool roofs = false)
        {
            FloorView.Set(floor);
            IsoRimBridge.SetRoofs(roofs);
            CameraDriver_Update_Patch.AllowAnnexView = false;
            Find.UIRoot.screenshotMode.Active = false;
            Find.Selector.ClearSelection();
            Find.CameraDriver.SetRootPosAndSize(new Vector3(g.x + 0.5f, 0f, g.z + 0.5f), size);
            // The test runs many ticks per frame and shoots while paused, when the game's draw-position spring does not
            // move: put every pawn where it really is.
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                p.Drawer?.tweener?.ResetTweenedPosToRoot();
            }
        }

        private static void StairsHook(Map map)
        {
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (!hungry && p.needs?.food != null) p.needs.food.CurLevel = p.needs.food.MaxLevel;
                if (p.needs?.joy != null) p.needs.joy.CurLevel = 1f;
                if (p.needs?.rest != null) p.needs.rest.CurLevel = 1f;
                if (p.InMentalState && p.ThingID != panicId) p.MentalState.RecoverFromState();
            }
            Pawn b = ById(map, builderId);
            if (b != null && b.CurJob != null && b.CurJob.targetA.HasThing && Stairwell.IsStairThing(b.CurJob.targetA.Thing) && b.CurJob.targetA.Thing.Spawned && b.jobs.curDriver is JobDriver_ConstructFinishFrame)
            {
                if (StairTouch.IsUpperToucher(b.Position, b.CurJob.targetA.Thing))
                {
                    rimTicks += watchEvery;
                }
                else
                {
                    offRimWorkTicks += watchEvery;
                }
            }
        }

        private static bool StepStairs(Map map)
        {
            switch (stage)
            {
                case "s_setup":
                    SetupStairs(map);
                    return true;

                // ---- F4: stairs intact: an upstairs pawn sent to the ground takes the stairs, never jumps
                case "s_f4":
                {
                    tickWatch = null;
                    Pawn w = ById(map, walkerId);
                    if (tripsLeft == 5)
                    {
                        Mark();
                    }
                    if (tripsLeft <= 0)
                    {
                        Result("F4 jump only without a route: with stairs, upstairs -> far ground x5 takes the stairs", PZStats.jumps - jumpsAt == 0 && climbsColonists - climbsAt >= 5 && ProjectZ.FloorOf(w.Position, map) == 0, Counts());
                        stage = "s_f4_wall";
                        return true;
                    }
                    tripsLeft--;
                    List<IntVec3> up = UpInner(map, sHouseA);
                    Teleport(w, up[Rand.Range(0, up.Count)]);
                    GoTo(w, farGround + new IntVec3(Rand.Range(-3, 4), 0, Rand.Range(-2, 3)));
                    TickUntil(3000, () => ProjectZ.FloorOf(w.Position, map) == 0 && !w.pather.Moving, "s_f4", 30);
                    return true;
                }
                case "s_f4_wall":
                {
                    // Wall the exit off from the rest of the upper floor: the only way down is a jump.
                    Pawn w = ById(map, walkerId);
                    Building_Stairs st = StairsA(map);
                    IntVec3 e = st.TopCell;
                    tempWalls.Clear();
                    foreach (IntVec3 d in GenAdj.AdjacentCells)
                    {
                        IntVec3 c = e + d;
                        if (c.InBounds(map) && ProjectZ.HasFloor(c, map) && c.GetEdifice(map) == null)
                        {
                            tempWalls.Add(Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c, map));
                        }
                    }
                    IntVec3 start = UpInner(map, sHouseA).Where(c => c.DistanceToSquared(e) > 8).OrderBy(c => c.x).First();
                    Teleport(w, start);
                    Heal(w);
                    Mark();
                    GoTo(w, farGround);
                    savedJump = false;
                    Info($"F4: exit {e} walled off ({tempWalls.Count} walls); walker {w} at {w.Position}");
                    TickUntil(3000, () => Links.Kind(w) == StepKind.Jump && w.pather.MovePercentage > 0.35f && w.pather.MovePercentage < 0.75f, "s_f4_arc", 1);
                    return true;
                }
                case "s_f4_arc":
                {
                    tickWatch = null;
                    Pawn w = ById(map, walkerId);
                    if (w == null || Links.Kind(w) != StepKind.Jump)
                    {
                        Result("F4 walled off: the pawn jumps", false, $"no jump seen; {w} at {w?.Position}, {Counts()}");
                        stage = "s_shots";
                        return true;
                    }
                    jumpFrom = w.Position;
                    ShotAt(map, 1, ProjectZ.GroundCellOf(w.Position, map), 7f);
                    Wait(2, "s_f4_arc_take");
                    return true;
                }
                case "s_f4_arc_take":
                    Capture("stairs_jump_arc");
                    stage = savedJump ? "s_f4_land" : "s_f4_save";
                    return true;
                case "s_f4_save":
                {
                    // F8a: save in the middle of the jump step, load, the step starts again from its cell.
                    savedJump = true;
                    savedTicks = Find.TickManager.TicksGame;
                    jumpsBeforeLoad = jumpsColonists;
                    GameDataSaveLoader.SaveGame("pz_jump");
                    Result("F8 saved pz_jump mid-jump", File.Exists(GenFilePaths.FilePathForSavedGame("pz_jump")), $"walker at {jumpFrom}");
                    stage = "s_f4_loaded";
                    pendingLoad = "pz_jump";
                    return true;
                }
                case "s_f4_loaded":
                {
                    Pawn w = ById(map, walkerId);
                    // (the game runs a few ticks after loading before the test pauses it: the restarted jump may be done already)
                    bool reset = w != null && PZStats.climbResets > resetsAt && (w.Position == jumpFrom || jumpsColonists > jumpsBeforeLoad);
                    Result("F8 reload mid-jump: reset to its cell, the jump starts again", reset, $"{w} at {w?.Position} (saved at {jumpFrom}), resets {PZStats.climbResets - resetsAt}, job {w?.CurJobDef?.defName}");
                    if (w != null && (!w.Drafted || w.CurJobDef != JobDefOf.Goto))
                    {
                        GoTo(w, farGround);
                    }
                    TickUntil(3000, () => w != null && ProjectZ.FloorOf(w.Position, map) == 0, "s_f4_land", 1);
                    return true;
                }
                case "s_f4_land":
                {
                    tickWatch = null;
                    Pawn w = ById(map, walkerId);
                    if (w != null && ProjectZ.FloorOf(w.Position, map) != 0)
                    {
                        TickUntil(3000, () => ProjectZ.FloorOf(w.Position, map) == 0, "s_f4_land", 1);
                        return true;
                    }
                    Result("F4 walled off: the pawn jumps down, lands bruised and staggered", w != null && jumpsColonists - jumpsColAt >= 1 && HasBruise(w) && !w.Downed, $"{w} at {w?.Position}, bruised {HasBruise(w)}, staggered {w?.stances?.stagger?.Staggered}, downed {w?.Downed}; {Counts()}");
                    ShotAt(map, 1, ProjectZ.GroundCellOf(w.Position, map), 7f);
                    Wait(3, "s_f4_land_take");
                    return true;
                }
                case "s_f4_land_take":
                    Capture("stairs_jump_land");
                    foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Wall).Where(t => ProjectZ.FloorOf(t.Position, map) == 1 && sHouseA.ExpandedBy(1).Contains(ProjectZ.GroundCellOf(t.Position, map))).ToList())
                    {
                        t.Destroy();
                    }
                    stage = "s_shots";
                    return true;

                // ---- stairs and stairwell shots: floor 2, floor 1 (roofs on: no lid over the well), a pawn on the step
                case "s_shots":
                    ShotAt(map, 1, sHouseA.CenterCell, 7f);
                    Wait(40, "s_shot_f2");
                    return true;
                case "s_shot_f2":
                    Capture("stairs_well_f2");
                    stage = "s_shot_f1_view";
                    return true;
                case "s_shot_f1_view":
                    ShotAt(map, 0, sHouseA.CenterCell, 7f, roofs: true);
                    Wait(40, "s_shot_f1");
                    return true;
                case "s_shot_f1":
                    Capture("stairs_well_f1_roofs");
                    stage = "s_shot_f1b_view";
                    return true;
                case "s_shot_f1b_view":
                    ShotAt(map, 0, sHouseA.CenterCell, 7f);
                    Wait(40, "s_shot_f1b");
                    return true;
                case "s_shot_f1b":
                {
                    Capture("stairs_ramp_f1");
                    Pawn w = ById(map, walkerId);
                    Heal(w);
                    GoTo(w, UpInner(map, sHouseA).First());
                    TickUntil(3000, () => Links.Kind(w) == StepKind.StairUp || (Links.RampHeight(w.Position, map) > 1f && w.pather.MovePercentage > 0.4f), "s_shot_climb", 1);
                    return true;
                }
                case "s_shot_climb":
                {
                    tickWatch = null;
                    Pawn w = ById(map, walkerId);
                    Building_Stairs cst = StairsA(map);
                    Vector3 dp = w.DrawPos;
                    Info($"climb shot: {w} at {w.Position} -> {w.pather.nextCell} {w.pather.MovePercentage:P0}, rise {Links.Rise(w):0.00}, draw pos {dp}, stairs {cst?.Position} rot {cst?.Rotation} high end {cst?.HighCell}; screen: pawn {(IsoRimBridge.Present ? PickScreen(new Vector3(dp.x, Links.Rise(w), dp.z)).ToString() : "-")}, stairs centre {(IsoRimBridge.Present && cst != null ? PickScreen(cst.TrueCenter()).ToString() : "-")}");
                    ShotAt(map, 0, ProjectZ.GroundCellOf(w.Position, map), 5f);
                    Wait(2, "s_shot_climb_take");
                    return true;
                }
                case "s_shot_climb_take":
                    Capture("stairs_climb");
                    stage = "s_f1";
                    return true;

                // ---- F1: the stairs break with everyone upstairs; food only on the ground
                case "s_f1":
                {
                    List<Pawn> cols = Colonists(map);
                    List<IntVec3> up = UpInner(map, sHouseA);
                    int i = 0;
                    foreach (Pawn p in cols)
                    {
                        Draft(p, false);
                        Teleport(p, up[i++ % up.Count]);
                        Heal(p);
                    }
                    for (int k = 0; k < 6; k++)
                    {
                        Thing meal = ThingMaker.MakeThing(ThingDefOf.MealSimple);
                        GenPlace.TryPlaceThing(meal, farGround, map, ThingPlaceMode.Near);
                    }
                    hungry = true;
                    foreach (Pawn p in cols)
                    {
                        p.needs.food.CurLevel = 0.05f;
                    }
                    Find.PlaySettings.autoRebuild = false;
                    Mark();
                    climbsAt = climbsColonists + climbsHostiles;
                    StairsA(map).Destroy(DestroyMode.KillFinalize);
                    Info($"F1: stairs destroyed with {OnFloor(map, cols, 1)} colonists upstairs, hungry, meals only on the ground");
                    TickUntil(2500, () => OnFloor(map, Colonists(map), 0) == Colonists(map).Count, "s_f1_check", 30);
                    return true;
                }
                case "s_f1_check":
                {
                    tickWatch = null;
                    hungry = false;
                    List<Pawn> cols = Colonists(map);
                    Result("F1 stairs broken: every upstairs colonist jumps down for food within 2500 ticks", OnFloor(map, cols, 0) == cols.Count && jumpsColonists - jumpsColAt >= cols.Count && climbsColonists + climbsHostiles - climbsAt == 0 && cols.All(p => HasBruise(p) && !p.Downed),
                        $"{OnFloor(map, cols, 0)} of {cols.Count} down after {ticksTotal - ticksLeft} ticks; bruised {cols.Count(HasBruise)}, downed {cols.Count(p => p.Downed)}; {Counts()}");
                    stage = "s_f5";
                    return true;
                }

                // ---- F5: with the stairs gone, the ground never paths up
                case "s_f5":
                {
                    Pawn g = Colonists(map).First();
                    IntVec3 upCell = UpInner(map, sHouseA).First();
                    TraverseParms tp = TraverseParms.For(TraverseMode.PassDoors);
                    bool down = map.reachability.CanReach(upCell, g.Position, PathEndMode.OnCell, tp);
                    bool up = map.reachability.CanReach(g.Position, upCell, PathEndMode.OnCell, tp);
                    bool pawnUp = g.CanReach(upCell, PathEndMode.OnCell, Danger.Deadly);
                    int cacheCount = ((ReachabilityCache)AccessTools.Field(typeof(Reachability), "cache").GetValue(map.reachability)).Count;
                    Result("F5 one way: upstairs -> ground true, then ground -> upstairs false (cache kept)", down && !up && !pawnUp && cacheCount > 0, $"down {down}, up {up}, pawn up {pawnUp}, cache entries {cacheCount}");
                    Mark();
                    GoTo(g, upCell);
                    walkerId = g.ThingID;
                    StartTicks(300, "s_f5_goto");
                    return true;
                }
                case "s_f5_goto":
                {
                    Pawn g = ById(map, walkerId);
                    Result("F5 a drafted order upstairs fails (stays on the ground)", ProjectZ.FloorOf(g.Position, map) == 0 && climbsColonists + climbsHostiles - climbsAt == 0, $"{g} at {g.Position}, job {g.CurJobDef?.defName}");
                    // Melee raiders on the ground, everyone upstairs (drafted, holding fire): they never come up.
                    List<IntVec3> up = UpInner(map, sHouseA);
                    int i = 0;
                    foreach (Pawn p in Colonists(map))
                    {
                        Teleport(p, up[i++ % up.Count]);
                        Draft(p, true);
                    }
                    List<Pawn> raiders = new List<Pawn>();
                    for (int k = 0; k < 3; k++)
                    {
                        Pawn r = MakeRaider(map, CellFinder.StandableCellNear(farGround + new IntVec3(6, 0, -6), map, 4f));
                        r.equipment.DestroyAllEquipment();
                        r.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MeleeWeapon_Club"), ThingDefOf.WoodLog));
                        raiders.Add(r);
                    }
                    LordMaker.MakeNewLord(raiders[0].Faction, new LordJob_AssaultColony(raiders[0].Faction, canKidnap: false, canTimeoutOrFlee: false, canSteal: false), map, raiders);
                    Mark();
                    TickUntil(4000, () => map.mapPawns.AllPawnsSpawned.Any(p => p.HostileTo(Faction.OfPlayer) && ProjectZ.FloorOf(p.Position, map) >= 1), "s_f5_raid", 30);
                    return true;
                }
                case "s_f5_raid":
                {
                    tickWatch = null;
                    List<Pawn> hostiles = map.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer)).ToList();
                    Result("F5 ground melee raiders never get upstairs", !hostiles.Any(p => ProjectZ.FloorOf(p.Position, map) >= 1) && climbsHostiles == 0, $"{hostiles.Count} hostiles after {ticksTotal - ticksLeft} ticks: {string.Join("; ", hostiles.Take(4).Select(h => h + " " + h.Position + " " + h.CurJobDef?.defName))}");
                    foreach (Pawn h in hostiles)
                    {
                        h.Destroy();
                    }
                    stage = "s_f6";
                    return true;
                }

                // ---- F6: raiders upstairs without stairs jump down to attack, and to flee
                case "s_f6":
                {
                    foreach (Pawn p in Colonists(map))
                    {
                        Teleport(p, CellFinder.StandableCellNear(farGround, map, 3f));
                        Draft(p, true);
                        Heal(p);
                    }
                    // a melee raider: one with a gun may just shoot down from the floor edge (decision A), which is fine play
                    Pawn r = MakeRaider(map, UpInner(map, sHouseA).First());
                    r.equipment.DestroyAllEquipment();
                    r.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MeleeWeapon_Club"), ThingDefOf.WoodLog));
                    raiderId = r.ThingID;
                    LordMaker.MakeNewLord(r.Faction, new LordJob_AssaultColony(r.Faction, canKidnap: false, canTimeoutOrFlee: false, canSteal: false), map, new List<Pawn> { r });
                    Pawn f = MakeRaider(map, UpInner(map, sHouseA).Last());
                    fleerId = f.ThingID;
                    LordMaker.MakeNewLord(f.Faction, new LordJob_ExitMapBest(LocomotionUrgency.Jog), map, new List<Pawn> { f });
                    Mark();
                    TickUntil(4000, () =>
                    {
                        Pawn ra = ById(map, raiderId);
                        Pawn fl = ById(map, fleerId);
                        return (ra == null || ProjectZ.FloorOf(ra.Position, map) == 0) && (fl == null || !fl.Spawned);
                    }, "s_f6_check", 30);
                    return true;
                }
                case "s_f6_check":
                {
                    tickWatch = null;
                    Pawn ra = ById(map, raiderId);
                    Pawn fl = ById(map, fleerId);
                    Result("F6 a raider upstairs without stairs jumps down to attack", ra != null && ProjectZ.FloorOf(ra.Position, map) == 0 && jumpsHostiles - jumpsHosAt >= 1, $"{ra} at {ra?.Position} job {ra?.CurJobDef?.defName}; {Counts()}");
                    Result("F6 a fleeing raider upstairs jumps down and leaves the map", fl == null && jumpsHostiles - jumpsHosAt >= 2, $"fleer still here: {fl} at {fl?.Position} {fl?.CurJobDef?.defName}; {Counts()}");
                    foreach (Pawn h in map.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer)).ToList())
                    {
                        h.Destroy();
                    }
                    stage = "s_f7";
                    return true;
                }

                // ---- F7: an upstairs colonist carries a downed one down to a bed by jumping (both bruised)
                case "s_f7":
                {
                    List<Pawn> cols = Colonists(map);
                    Pawn carrier = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)) ?? cols[0];
                    Pawn patient = cols.First(p => p != carrier);
                    carrierId = carrier.ThingID;
                    patientId = patient.ThingID;
                    List<IntVec3> up = UpInner(map, sHouseA);
                    foreach (Pawn p in cols)
                    {
                        Draft(p, false);
                        Heal(p);
                        p.workSettings.EnableAndInitializeIfNotAlreadyInitialized();
                        // only the rescue to do (no fires to fight left over from the raids, no other work)
                        foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefs)
                        {
                            if (!p.WorkTypeIsDisabled(w))
                            {
                                p.workSettings.SetPriority(w, p == carrier && w == WorkTypeDefOf.Doctor ? 1 : 0);
                            }
                        }
                    }
                    foreach (Thing f in map.listerThings.ThingsInGroup(ThingRequestGroup.Fire).ToList())
                    {
                        f.Destroy();
                    }
                    Teleport(carrier, up[0]);
                    Teleport(patient, up[up.Count - 1]);
                    // Downed by anaesthetic: no wounds, so the doctor's only job is the rescue (with wounds he went down for
                    // medicine first, by jumping, and then could not get back up: the documented "no A -> B check" case).
                    HealthUtility.TryAnesthetize(patient);
                    Building_Bed bed = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
                    bed.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(bed, CellFinder.StandableCellNear(farGround + new IntVec3(-4, 0, 0), map, 3f), map, Rot4.North);
                    bed.Medical = true;
                    foreach (Hediff h in carrier.health.hediffSet.hediffs.Where(h => h.def.defName == "Bruise").ToList())
                    {
                        carrier.health.RemoveHediff(h);
                    }
                    bool patientBruisedBefore = HasBruise(patient);
                    Info($"F7: {carrier} (doctor) upstairs with downed {patient} (downed {patient.Downed}, bruised before {patientBruisedBefore}); medical bed {bed.Position} on the ground");
                    Mark();
                    // the rescue as the player orders it (float menu "Rescue"): an idle doctor could wander off first, and wandering
                    // may jump too (Q3)
                    Draft(carrier, true);
                    Job rescue = JobMaker.MakeJob(JobDefOf.Rescue, patient, bed);
                    rescue.count = 1;
                    carrier.jobs.TryTakeOrderedJob(rescue, JobTag.DraftedOrder);
                    TickUntil(5000, () => patient.InBed() || ProjectZ.FloorOf(patient.PositionHeld, map) == 0 && carrier.carryTracker.CarriedThing == null, "s_f7_check", 30);
                    return true;
                }
                case "s_f7_check":
                {
                    tickWatch = null;
                    Pawn carrier = ById(map, carrierId);
                    Pawn patient = map.mapPawns.AllPawns.FirstOrDefault(p => p.ThingID == patientId);
                    Result("F7 a colonist upstairs carries a downed one down by jumping; both bruised (Q4)", carrier != null && patient != null && ProjectZ.FloorOf(patient.PositionHeld, map) == 0 && jumpsColonists - jumpsColAt >= 1 && HasBruise(carrier) && HasBruise(patient),
                        $"{carrier} at {carrier?.Position}; {patient} at {patient?.PositionHeld} in bed {patient?.InBed()}; bruises: carrier {HasBruise(carrier)}, patient {patient?.health.hediffSet.hediffs.Count(h => h.def.defName == "Bruise")}; {Counts()}");
                    stage = "s_f7_panic";
                    return true;
                }
                case "s_f7_panic":
                {
                    MentalStateDef panic = MentalStateDefOf.PanicFleeFire;
                    Pawn c = Colonists(map).FirstOrDefault(p => p.ThingID != patientId && !p.Downed);
                    if (panic == null || c == null)
                    {
                        Info("F7 panic from fire skipped (no PanicFleeFire state)");
                        stage = "s_f2";
                        return true;
                    }
                    panicId = c.ThingID;
                    Draft(c, false);
                    List<IntVec3> up = UpInner(map, sHouseA);
                    Teleport(c, up[0]);
                    for (int k = 1; k < 4 && k < up.Count; k++)
                    {
                        Thing fuel = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                        fuel.stackCount = 20;
                        GenPlace.TryPlaceThing(fuel, up[k], map, ThingPlaceMode.Direct);
                        FireUtility.TryStartFireIn(up[k], map, 0.8f, null);
                    }
                    bool started = c.mindState.mentalStateHandler.TryStartMentalState(panic, null, forced: true);
                    Mark();
                    Info($"F7 panic: fires upstairs {map.listerThings.ThingsInGroup(ThingRequestGroup.Fire).Count}, {c} panics {started}");
                    TickUntil(2500, () => ProjectZ.FloorOf(c.Position, map) == 0, "s_f7_panic_check", 10);
                    return true;
                }
                case "s_f7_panic_check":
                {
                    tickWatch = null;
                    Pawn c = ById(map, panicId);
                    Result("F7 a colonist panicking from a fire upstairs flees by jumping", c != null && ProjectZ.FloorOf(c.Position, map) == 0 && jumpsColonists - jumpsColAt >= 1, $"{c} at {c?.Position} state {c?.MentalStateDef?.defName}; {Counts()}");
                    panicId = null;
                    c?.MentalState?.RecoverFromState();
                    foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Fire).ToList())
                    {
                        t.Destroy();
                    }
                    stage = "s_f2";
                    return true;
                }

                // ---- F2: rebuild from upstairs only: a walled upper room, steel and one builder up there
                case "s_f2":
                    SetupRebuildUpstairs(map);
                    return true;
                case "s_f2_frame":
                {
                    tickWatch = null;
                    Frame fr = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).OfType<Frame>().FirstOrDefault(f => f.def.entityDefToBuild == PZDefOf.PZ_Stairs);
                    if (fr == null)
                    {
                        Result("F2 a frame from upstairs deliveries", false, $"no stairs frame; {Counts()}");
                        stage = "s_f3";
                        return true;
                    }
                    Pawn b = ById(map, builderId);
                    ShotAt(map, 1, ProjectZ.GroundCellOf(b.Position, map), 6f);
                    Wait(3, "s_f2_shot");
                    return true;
                }
                case "s_f2_shot":
                    Capture("stairs_rim_builder");
                    stage = "s_f2_save";
                    return true;
                case "s_f2_save":
                    // F8b: save in the middle of building the frame from upstairs, load, the work goes on.
                    savedFrame = true;
                    savedTicks = Find.TickManager.TicksGame;
                    GameDataSaveLoader.SaveGame("pz_frame");
                    Result("F8 saved pz_frame mid-construction from upstairs", File.Exists(GenFilePaths.FilePathForSavedGame("pz_frame")), "");
                    stage = "s_f2_loaded";
                    pendingLoad = "pz_frame";
                    return true;
                case "s_f2_loaded":
                    Info($"F2 reloaded pz_frame at tick {ticksAfterLoad} (saved {savedTicks})");
                    TickUntil(9000, () => StairsA(map) != null, "s_f2_check", 30);
                    return true;
                case "s_f2_check":
                {
                    tickWatch = null;
                    Pawn b = ById(map, builderId);
                    Building_Stairs st = StairsA(map);
                    StairGrid g = StairGrid.For(map);
                    Result("F2 rebuilt from upstairs: delivered and built by the upstairs pawn on the rim, no jumps", st != null && g != null && g.links.Any(l => l.stairs == st) && b != null && ProjectZ.FloorOf(b.Position, map) == 1 && PZStats.jumps - jumpsAt == 0 && rimTicks > 0,
                        $"stairs {st}, links {g?.Count}, builder {b} at {b?.Position}, ticks working from the rim {rimTicks} (elsewhere {offRimWorkTicks}); {Counts()}");
                    if (st == null)
                    {
                        stage = "s_f3";
                        return true;
                    }
                    GoTo(b, farGround);
                    TickUntil(3000, () => ProjectZ.FloorOf(b.Position, map) == 0, "s_f2_down", 10);
                    return true;
                }
                case "s_f2_down":
                {
                    tickWatch = null;
                    Pawn b = ById(map, builderId);
                    if (b == null || PZStats.jumps - jumpsAt > 0)
                    {
                        LinkDiagnosis(map);
                    }
                    Result("F2 the builder then walks down the new stairs", b != null && ProjectZ.FloorOf(b.Position, map) == 0 && climbsColonists + climbsHostiles - climbsAt >= 1 && PZStats.jumps - jumpsAt == 0, $"{b} at {b?.Position}; {Counts()}");
                    // the ghost check: the walled upstairs room, the pointer's cut-away on
                    ShotAt(map, 1, sHouseA.CenterCell, 7f);
                    Info("ghost view: " + IsoRimBridge.CutState(map, new CellRect(sHouseA.minX - 4, Up(new IntVec3(sHouseA.minX, 0, sHouseA.minZ), map).z - 4, sHouseA.Width + 8, sHouseA.Height + 8)));
                    Wait(40, "s_ghost_take");
                    return true;
                }
                case "s_ghost_take":
                    Capture("stairs_ghost_check");
                    stage = "s_f3";
                    return true;

                // ---- F3: auto-rebuild: the stairs in the home area are destroyed and come back by themselves
                case "s_f3":
                {
                    Building_Stairs st = StairsA(map);
                    if (st == null)
                    {
                        Result("F3 auto-rebuild", false, "no stairs to destroy");
                        StartTicks(300, "s_end");
                        return true;
                    }
                    Find.PlaySettings.autoRebuild = true;
                    foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.ToList())
                    {
                        if (p.Downed)
                        {
                            HealthUtility.HealNonPermanentInjuriesAndRestoreLegs(p);
                        }
                        Draft(p, false);
                        foreach (WorkTypeDef w in new[] { WorkTypeDefOf.Construction, WorkTypeDefOf.Hauling })
                        {
                            if (!p.WorkTypeIsDisabled(w))
                            {
                                p.workSettings.SetPriority(w, 1);
                            }
                        }
                    }
                    Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    steel.stackCount = 150;   // (60 needed; a botched frame loses about half)
                    GenPlace.TryPlaceThing(steel, farGround, map, ThingPlaceMode.Near);
                    IntVec3 pos = st.Position;
                    st.Destroy(DestroyMode.KillFinalize);
                    Blueprint bp = pos.GetThingList(map).OfType<Blueprint>().FirstOrDefault(t => t.def.entityDefToBuild == PZDefOf.PZ_Stairs);
                    Result("F3 auto-rebuild places a blueprint", bp != null, $"at {pos}: {bp}; well roofs {string.Join(",", StairShape.Footprint(pos, StairRot).Cells.Select(c => map.roofGrid.RoofAt(c)?.defName ?? "none"))}");
                    Mark();
                    TickUntil(12000, () => StairsA(map) != null, "s_f3_check", 60);
                    return true;
                }
                case "s_f3_check":
                {
                    tickWatch = null;
                    IntVec3 groundCell = CellFinder.StandableCellNear(farGround, map, 3f);
                    IntVec3 upCell = UpInner(map, sHouseA).First();
                    bool reach = map.reachability.CanReach(groundCell, upCell, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors));
                    if (!reach)
                    {
                        LinkDiagnosis(map);
                        ConstructDiagnosis(map);
                    }
                    Result("F3 auto-rebuilt stairs: ground -> upstairs reachable again", StairsA(map) != null && reach, $"stairs {StairsA(map)} after {ticksTotal - ticksLeft} ticks; reach {reach}");
                    StartTicks(300, "s_end");
                    return true;
                }

                // ---- the slab height rule, both ways, through the game's designators
                case "s_height":
                    HeightRuleChecks(map);
                    return true;
                case "s_height_take":
                    Capture("stairs_reject_messages");
                    stage = "s_door";
                    return true;

                // ---- the wall-height lock and the ornate doors (arch hidden under the floor only)
                case "s_door":
                {
                    if (IsoRimBridge.Present)
                    {
                        Result("wall height locked at 1.65 (storey 1.67)", Mathf.Abs(IsoRimBridge.WallHeight - 1.65f) < 0.001f && Mathf.Abs(IsoRimBridge.StoreyHeight - 1.67f) < 0.001f && Mathf.Abs(IsoRim.View3D.LockedWallHeight - 1.65f) < 0.001f,
                            $"wall {IsoRimBridge.WallHeight}, storey {IsoRimBridge.StoreyHeight}, locked {IsoRim.View3D.LockedWallHeight} by {IsoRim.View3D.LockedBy}, option value {IsoRimBridge.SetOption("wallHeight", IsoRimBridge.SetOption("wallHeight", 1.2f))}");
                    }
                    Building_Door under = map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("OrnateDoor")).OfType<Building_Door>().FirstOrDefault(d => d.OccupiedRect().Cells.Any(c => FloorRoof.FloorAbove(c, map)));
                    Building_Door open = map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("OrnateDoor")).OfType<Building_Door>().FirstOrDefault(d => d != under);
                    Result("ornate doors: one under a floor, one not", under != null && open != null && (!IsoRimBridge.Present || DoorSlab(map, under) && !DoorSlab(map, open)), $"under {under?.Position}, open {open?.Position}");
                    ShotAt(map, 0, new IntVec3(sHouseB.CenterCell.x, 0, sHouseB.minZ), 5f);
                    Wait(40, "s_door_take");
                    return true;
                }
                case "s_door_take":
                    Capture("stairs_ornate_doors_f1");
                    stage = "s_door_view2";
                    return true;
                case "s_door_view2":
                    ShotAt(map, 1, new IntVec3(sHouseB.CenterCell.x, 0, sHouseB.minZ), 5f);
                    Wait(40, "s_door_take2");
                    return true;
                case "s_door_take2":
                    Capture("stairs_ornate_doors_f2");
                    stage = "s_f4";   // (the doors and the height rule are checked first, before the raids can break house B)
                    return true;
                case "s_end":
                    WatchCheck(map, "stairs end");
                    Info($"stats: {Counts()}, path joins {PZStats.splices} (none {PZStats.spliceFails}), FindPathNow joins {PZStats.findNow}, construction botches {botches}");
                    Finish();
                    return true;
            }
            return false;
        }

        // Why the stairs link might not work: the link, the regions at its ends and reachability round it.
        private static void LinkDiagnosis(Map map)
        {
            Building_Stairs st = StairsA(map);
            StairGrid g = StairGrid.For(map);
            if (st == null)
            {
                Info("link diagnosis: no stairs");
                return;
            }
            IntVec3 h = st.HighCell, e = st.TopCell;
            Region rh = map.regionGrid.GetValidRegionAt(h), re = map.regionGrid.GetValidRegionAt(e);
            TraverseParms tp = TraverseParms.For(TraverseMode.PassDoors);
            IntVec3 groundCell = CellFinder.StandableCellNear(farGround, map, 3f);
            IntVec3 upCell = UpInner(map, sHouseA).First();
            Info($"link diagnosis: links {g?.Count} ({string.Join("; ", g?.links.Select(l => l.bottom + "->" + l.top + " usable " + g.Usable(l)) ?? new string[0])}), H {h} walkable {h.Walkable(map)} region {rh?.id} district {rh?.District?.ID}, E {e} walkable {e.Walkable(map)} {e.GetTerrain(map).defName} edifice {e.GetEdifice(map)} region {re?.id} district {re?.District?.ID}; reach H->E {map.reachability.CanReach(h, e, PathEndMode.OnCell, tp)}, E->H {map.reachability.CanReach(e, h, PathEndMode.OnCell, tp)}, ground->H {map.reachability.CanReach(groundCell, h, PathEndMode.OnCell, tp)}, E->up {map.reachability.CanReach(e, upCell, PathEndMode.OnCell, tp)} ({upCell})");
        }

        // What the construction work givers say about the stairs blueprint or frame, for each colonist.
        private static void ConstructDiagnosis(Map map)
        {
            Thing t = map.listerThings.AllThings.FirstOrDefault(x => (x is Blueprint || x is Frame) && x.def.entityDefToBuild == PZDefOf.PZ_Stairs);
            if (t == null)
            {
                Info("construct diagnosis: no stairs blueprint or frame");
                return;
            }
            WorkGiver_ConstructDeliverResourcesToBlueprints wbp = new WorkGiver_ConstructDeliverResourcesToBlueprints();
            WorkGiver_ConstructDeliverResourcesToFrames wfr = new WorkGiver_ConstructDeliverResourcesToFrames();
            WorkGiver_ConstructFinishFrames wfin = new WorkGiver_ConstructFinishFrames();
            foreach (WorkGiverDef d in DefDatabase<WorkGiverDef>.AllDefs)
            {
                if (d.giverClass == typeof(WorkGiver_ConstructDeliverResourcesToBlueprints)) wbp.def = d;
                if (d.giverClass == typeof(WorkGiver_ConstructDeliverResourcesToFrames)) wfr.def = d;
                if (d.giverClass == typeof(WorkGiver_ConstructFinishFrames)) wfin.def = d;
            }
            Frame fr = t as Frame;
            Info($"construct diagnosis: {t} at {t.Position}, blocking {GenConstruct.FirstBlockingThing(t, null)}, things {string.Join(",", t.OccupiedRect().Cells.SelectMany(c => c.GetThingList(map)).Select(x => x.def.defName).Distinct())}, steel on map {map.listerThings.ThingsOfDef(ThingDefOf.Steel).Sum(x => x.stackCount)}");
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                JobFailReason.Clear();
                Job j = null;
                try
                {
                    j = fr == null ? wbp.JobOnThing(p, t, false) : fr.IsCompleted() ? wfin.JobOnThing(p, t, false) : wfr.JobOnThing(p, t, false);
                }
                catch (Exception e)
                {
                    Info("construct diagnosis threw " + e.Message);
                }
                Info($"construct diagnosis: {p} at {p.Position} drafted {p.Drafted} downed {p.Downed} job {p.CurJobDef?.defName} construction {p.workSettings?.GetPriority(WorkTypeDefOf.Construction)}: -> {j?.def?.defName ?? "none"} {JobFailReason.Reason} reach {p.CanReach(t, PathEndMode.Touch, Danger.Deadly)}");
            }
        }

        private static bool DoorSlab(Map map, Building_Door d) => d.OccupiedRect().Cells.Any(c => IsoRim.Floors.SlabOver(map, c));

        private static void SetupStairs(Map map)
        {
            List<Pawn> cols = Colonists(map);
            IntVec3 near = cols.Count > 0 ? new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z)) : map.Center;
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 79f, true))
            {
                CellRect r = new CellRect(c.x - 10, c.z - 8, 21, 17);
                CellRect m = r.ExpandedBy(3);
                if (!m.InBounds(map) || m.maxZ >= ProjectZ.GroundZ(map) - 12)
                {
                    continue;
                }
                // (buildings other than rock, such as ruins, are cleared below)
                if (m.Cells.All(x => !x.GetTerrain(map).IsWater && !map.roofGrid.Roofed(x) && !x.Fogged(map) && !(x.GetEdifice(map)?.def.building?.isNaturalRock ?? false) && x.GetTerrain(map).passability == Traversability.Standable))
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
            foreach (IntVec3 c in found.ExpandedBy(3))
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable && (t is Plant || t.def.category == ThingCategory.Item || t is Filth || t is Building))
                    {
                        t.Destroy();
                    }
                }
            }
            DebugSettings.enableRandomMentalStates = false;
            int ox = found.minX, oz = found.minZ + 8;
            sHouseA = new CellRect(ox, oz, 9, 7);
            sHouseB = new CellRect(ox + 13, oz, 7, 7);
            farGround = new IntVec3(ox + 4, 0, found.minZ + 2);
            IntVec3 doorA = new IntVec3(sHouseA.minX + 2, 0, sHouseA.minZ);
            foreach (IntVec3 c in sHouseA.EdgeCells)
            {
                Place(c == doorA ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            // house B: two ornate doors in the south wall, the west one under the floor (the floor covers x <= minX + 3)
            IntVec3 ornUnder = new IntVec3(sHouseB.minX + 2, 0, sHouseB.minZ);
            IntVec3 ornOpen = new IntVec3(sHouseB.minX + 4, 0, sHouseB.minZ);
            ThingDef ornate = DefDatabase<ThingDef>.GetNamed("OrnateDoor");
            HashSet<IntVec3> doorCells = new HashSet<IntVec3>(GenAdj.OccupiedRect(ornUnder, Rot4.North, ornate.size).Cells.Concat(GenAdj.OccupiedRect(ornOpen, Rot4.North, ornate.size).Cells));
            foreach (IntVec3 c in sHouseB.EdgeCells)
            {
                if (!doorCells.Contains(c))
                {
                    Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
                }
            }
            Place(ornate, ThingDefOf.WoodLog, ornUnder, map, Rot4.North);
            Place(ornate, ThingDefOf.WoodLog, ornOpen, map, Rot4.North);
            foreach (CellRect h in new[] { sHouseA, sHouseB })
            {
                foreach (IntVec3 c in h)
                {
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                    map.areaManager.Home[c] = true;
                }
            }
            stairPos = new IntVec3(sHouseA.maxX - 1, 0, sHouseA.minZ + 2);
            AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, stairPos, StairRot, map, stuffDef: ThingDefOf.Steel);
            Result("stairs placeable (exit over a roofed cell)", ok.Accepted, ok.Reason);
            HashSet<IntVec3> well = new HashSet<IntVec3>(StairShape.Footprint(stairPos, StairRot).Cells);
            foreach (IntVec3 g in sHouseA)
            {
                if (!well.Contains(g) && !(mode == "floorui" && g.x <= sHouseA.maxX - 4))
                {
                    map.terrainGrid.SetTerrain(UpS(g, map), PZDefOf.PZ_UpperFloor);
                }
            }
            foreach (IntVec3 g in sHouseB)
            {
                if (g.x <= sHouseB.minX + 3)
                {
                    map.terrainGrid.SetTerrain(UpS(g, map), PZDefOf.PZ_UpperFloor);
                }
            }
            Place(PZDefOf.PZ_Stairs, ThingDefOf.Steel, stairPos, map, StairRot);
            Building_Stairs st = StairsA(map);
            Result("stairs: stairwell roof invisible-roofed, link registered", st != null && StairGrid.For(map)?.Count == 1 && well.All(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_StairwellRoof),
                $"{st} at {stairPos}, high end {st?.HighCell}, exit {st?.TopCell}; well roofs {string.Join(",", well.Select(c => map.roofGrid.RoofAt(c)?.defName ?? "none"))}");
            foreach (Pawn p in cols)
            {
                p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
                foreach (WorkTypeDef w in new[] { WorkTypeDefOf.Construction, WorkTypeDefOf.Hauling })
                {
                    if (!p.WorkTypeIsDisabled(w))
                    {
                        p.workSettings.SetPriority(w, 0);
                    }
                }
            }
            Find.PlaySettings.autoRebuild = false;
            walkerId = cols.First().ThingID;
            tripsLeft = 5;
            if (GenCommandLine.TryGetCommandLineArg("projectzstart", out string from))
            {
                AnnexWatch.Reset();
                stage = from;
                Info("starting at " + from);
                return;
            }
            if (mode == "floorui")
            {
                Info($"floorui: house A {sHouseA}, floor over x > {sHouseA.maxX - 4} only; stairs {stairPos}; far ground {farGround}");
                stage = "u_start";
                return;
            }
            Info($"house A {sHouseA} (stairs {stairPos} up {StairRot.ToStringHuman()}), house B {sHouseB} (floor over x <= {sHouseB.minX + 3}, ornate doors {ornUnder} under it, {ornOpen} not), far ground {farGround}");
            AnnexWatch.Reset();
            stage = "s_height";
        }

        private static void SetupRebuildUpstairs(Map map)
        {
            Building_Stairs st = StairsA(map);
            if (st != null)
            {
                st.Destroy(DestroyMode.Vanish);
            }
            // Upper walls round house A's floor: no slab edge to jump from; the stairwell is the only opening.
            foreach (IntVec3 g in sHouseA.EdgeCells)
            {
                IntVec3 u = UpS(g, map);
                if (ProjectZ.HasFloor(u, map) && u.GetEdifice(map) == null)
                {
                    Place(ThingDefOf.Wall, ThingDefOf.WoodLog, u, map);
                }
            }
            List<Pawn> cols = Colonists(map);
            Pawn b = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.Downed) ?? cols[0];
            builderId = b.ThingID;
            foreach (Pawn p in cols)
            {
                Draft(p, false);
                Heal(p);
                p.workSettings.EnableAndInitializeIfNotAlreadyInitialized();
                foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefs)
                {
                    if (!p.WorkTypeIsDisabled(w))
                    {
                        p.workSettings.SetPriority(w, p == b && w == WorkTypeDefOf.Construction ? 1 : 0);   // (delivering is construction work)
                    }
                }
            }
            List<IntVec3> up = UpInner(map, sHouseA);
            Teleport(b, up[0]);
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 150;   // (60 needed; a botched frame loses about half)
            GenPlace.TryPlaceThing(steel, up[up.Count / 2], map, ThingPlaceMode.Near);
            Find.PlaySettings.autoRebuild = false;
            AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, stairPos, StairRot, map, stuffDef: ThingDefOf.Steel);
            Blueprint bp = ok.Accepted ? GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, stairPos, map, StairRot, Faction.OfPlayer, ThingDefOf.Steel) as Blueprint : null;
            bool rim = bp != null && b.CanReach(bp, PathEndMode.Touch, Danger.Deadly);
            Result("F2 stairs blueprint placed again and touchable from upstairs", bp != null && rim && map.roofGrid.RoofAt(stairPos) == PZDefOf.PZ_StairwellRoof, $"{ok.Reason}; {bp}; upstairs builder {b} can touch {rim}; well roof {map.roofGrid.RoofAt(stairPos)?.defName}");
            rimTicks = 0;
            offRimWorkTicks = 0;
            Mark();
            savedFrame = false;
            TickUntil(9000, () =>
            {
                Frame fr = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).OfType<Frame>().FirstOrDefault(f => f.def.entityDefToBuild == PZDefOf.PZ_Stairs);
                return StairsA(map) != null || fr != null && fr.WorkLeft < fr.WorkToBuild * 0.6f && rimTicks > 0;
            }, "s_f2_frame", 30);
        }

        private static void HeightRuleChecks(Map map)
        {
            if (!IsoRimBridge.Present)
            {
                Info("height rule skipped: IsoRim not loaded");
                stage = "s_door";
                return;
            }
            ThingDef tall = DefDatabase<ThingDef>.AllDefs.Where(d => d.category == ThingCategory.Building && d.designationCategory != null && d.size.x <= 2 && d.size.z <= 2 && HeightRule.TooTall(d) && !d.IsFrame && !d.IsBlueprint)
                .OrderBy(d => d.size.x * d.size.z).ThenBy(d => d.defName).FirstOrDefault();
            List<string> talls = DefDatabase<ThingDef>.AllDefs.Where(d => d.category == ThingCategory.Building && d.designationCategory != null && HeightRule.TooTall(d)).Select(d => d.defName + " " + IsoRimBridge.ModelHeight(d).ToString("0.00")).ToList();
            Info($"clearance {HeightRule.Clearance:0.000}; buildable models too tall for a floor over them ({talls.Count}): {string.Join(", ", talls)}");
            if (tall == null)
            {
                Result("a tall buildable model exists", false, "none");
                stage = "s_door";
                return;
            }
            Designator_Build dTall = new Designator_Build(tall);
            if (tall.MadeFromStuff)
            {
                dTall.SetStuffDef(GenStuff.DefaultStuffFor(tall));
            }
            // (a) under the floor (house B west half)
            IntVec3 under = new IntVec3(sHouseB.minX + 2, 0, sHouseB.minZ + 3);
            AcceptanceReport a = dTall.CanDesignateCell(under);
            Designator_Build dBed = new Designator_Build(ThingDefOf.Bed);
            dBed.SetStuffDef(ThingDefOf.WoodLog);
            AcceptanceReport aBed = dBed.CanDesignateCell(under);
            Result("height rule: a tall building is refused under a floor (designator)", !a.Accepted && a.Reason == "PZ_TooTallUnderFloor".Translate() && aBed.Accepted,
                $"{tall.defName} ({IsoRimBridge.ModelHeight(tall):0.00} cells) at {under}: '{a.Reason}'; a bed there ({IsoRimBridge.ModelHeight(ThingDefOf.Bed):0.00}): {aBed.Accepted}");
            // (b) placed where there is no floor (house B east half), then a floor over it is refused
            IntVec3 open = new IntVec3(sHouseB.maxX - 1 - (tall.size.x - 1), 0, sHouseB.minZ + 3);
            AcceptanceReport b0 = dTall.CanDesignateCell(open);
            if (b0.Accepted)
            {
                dTall.DesignateSingleCell(open);
            }
            Thing bp = open.GetThingList(map).FirstOrDefault(t => t is Blueprint && t.def.entityDefToBuild == tall);
            Designator_Build dFloor = new Designator_Build(PZDefOf.PZ_UpperFloor);
            IntVec3 over = UpS(open, map);
            AcceptanceReport b1 = dFloor.CanDesignateCell(over);
            Designator_Build dFloor2 = new Designator_Build(PZDefOf.PZ_UpperFloor);
            AcceptanceReport b2 = dFloor2.CanDesignateCell(Up(new IntVec3(sHouseB.maxX - 1, 0, sHouseB.maxZ - 1), map));
            Result("height rule: a floor over a tall building (blueprint) is refused; elsewhere allowed", bp != null && !b1.Accepted && b1.Reason == "PZ_TallBelow".Translate(bp.LabelShort) && b2.Accepted,
                $"blueprint {bp} at {open} (designated {b0.Accepted} {b0.Reason}); floor over it: '{b1.Reason}'; floor over an empty roofed cell: {b2.Accepted} {b2.Reason}");
            AcceptanceReport w = new Designator_Build(PZDefOf.PZ_UpperFloor).CanDesignateCell(UpS(stairPos, map));
            Result("a floor over the stairwell is refused", !w.Accepted && w.Reason == "PZ_StairwellBelow".Translate(), $"'{w.Reason}'");
            // the game shows a refused placement as a reject message: shown here for the screenshot
            Messages.Message(a.Reason, MessageTypeDefOf.RejectInput, false);
            Messages.Message(b1.Reason, MessageTypeDefOf.RejectInput, false);
            Messages.Message(w.Reason, MessageTypeDefOf.RejectInput, false);
            ShotAt(map, 1, sHouseB.CenterCell, 6f);
            Wait(20, "s_height_take");
        }
    }
}
