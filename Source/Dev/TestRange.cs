using System;
using System.Collections.Generic;
using System.Linq;
using ProjectZ.Combat;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // Mode range (weapon range across floors, Range3D + RangePreview; run with -projectzfloors=13). A 5x5 tower T with a
    // slab on every floor (2 to 13) at the south of a cleared field; shooters stand on T's north edge cell on their floor
    // (decision A: the edge sees down), raiders stand on the ground straight north of it at a set distance d (each in its
    // own defend-point lord of radius 0.9, so it stays). Everything runs on real ticks and the pawns' own AI:
    //   1 shotguns (15.9) on the ground vs a colonist on floor 13 (20 cells up; d 3 and 8): never fire.
    //   2 shotguns vs a colonist on floor 2 (d 3 and 8): fire. Then floor 5 (6.7 cells up): d 14 fires (3D 15.5), d 15 doesn't
    //     (3D 16.4: in range flat, out in 3D).
    //   3 a sniper (44.9) on floor 5 fires at a raider at d 47 (beyond its flat range: +4 for 4 floors down), not at d 49.
    //   4 a powered mini-turret (28.9) on floor 2 targets a raider at d 29 (+1), never one at d 31.
    //   5 a rifle raider (36.9) in a defend-point lord (radius 45) where it starts, 52 cells off a colonist on floor 5 walks in and fires from a spot within
    //     the 3D range (its reach over the ground there 36.3), not from the tower's foot.
    //   6 a mortar on the ground: forced targets on floors 2 and 13 (min range 29.9 in 3D) - reported.
    //   7 preview shots (sphere shell and rings): rifle and sniper on floors 1, 2, 5 at two turns, and the turret.
    public static partial class TestRunner
    {
        private static CellRect gArea, gTower;
        private static int gcx, gEz;   // tower edge column x, ground row of the edge cells
        private static Pawn gA;
        private static readonly List<Pawn> gRaiders = new List<Pawn>();
        private static Building_TurretGun gTurret;
        private static int gShot;
        private static float gOldYaw, gOldMax;
        private static readonly HashSet<Thing> gTurretTargets = new HashSet<Thing>();
        private static readonly List<string> gMoves = new List<string>();
        private static IntVec3 gFirstShotFrom = IntVec3.Invalid;
        private static int gFirstShotTick;

        private static IntVec3 GEdge(int k, Map map) => k == 0 ? new IntVec3(gcx, 0, gEz + 1) : ProjectZ.CellOnFloor(new IntVec3(gcx, 0, gEz), k, map);

        private static IntVec3 GField(int d) => new IntVec3(gcx, 0, gEz + d);

        private static int GShotsFrom(Pawn p) => PZCombatStats.shots.Count(s => s.launcher == p.ToString());

        private static string GShotList() => string.Join("; ", PZCombatStats.shots.Take(10).Select(s => $"{s.launcher} (floor {s.fromFloor + 1}) -> {s.target} (floor {s.toFloor + 1})"));

        private static Pawn GRaider(Map map, int d, string gun, bool assault = false)
        {
            Faction f = Find.FactionManager.AllFactions.Where(x => x.HostileTo(Faction.OfPlayer) && x.def.humanlikeFaction && !x.def.hidden && !x.temporary && x.def.pawnGroupMakers != null).OrderBy(x => x.loadID).First();
            Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Mercenary_Gunner"), f, forceGenerateNewPawn: true));
            p.equipment.DestroyAllEquipment();
            p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(gun)));
            p.apparel?.DestroyAll();
            IntVec3 c = GField(d);
            GenSpawn.Spawn(p, c, map);
            if (assault)
            {
                // (an assault lord trashes the tower's generator and turret first) a defend-point lord over the whole field:
                // held where it starts: the AI's own fight-and-position jobs (JobGiver_AIDefendPoint -> CastPositionFinder) within 45
                LordMaker.MakeNewLord(f, new LordJob_DefendPoint(c, 0.9f, 45f, false, false), map, new[] { p });
            }
            else
            {
                LordMaker.MakeNewLord(f, new LordJob_DefendPoint(c, 0.9f, 0.9f, false, false), map, new[] { p });
            }
            gRaiders.Add(p);
            return p;
        }

        private static void GClearRaiders()
        {
            foreach (Pawn r in gRaiders)
            {
                if (!r.Destroyed)
                {
                    r.Destroy();
                }
            }
            gRaiders.Clear();
            PZCombatStats.shots.Clear();
            PZCombatStats.crossFloorShots = 0;
        }

        private static void GPlaceA(int k, string gun, bool fire)
        {
            if (gun != null)
            {
                gA.equipment.DestroyAllEquipment();
                gA.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(gun)));
            }
            gA.drafter.Drafted = true;
            gA.drafter.FireAtWill = fire;
            Teleport(gA, GEdge(k, Find.CurrentMap));
        }

        private static void GKeepA()
        {
            if (gA.needs?.food != null) gA.needs.food.CurLevel = gA.needs.food.MaxLevel;
            if (gA.needs?.rest != null) gA.needs.rest.CurLevel = 1f;
            if (!gA.Drafted && !gA.Downed && !gA.Dead) gA.drafter.Drafted = true;
        }

        private static string GWhere(Pawn p, Map map) => p == null ? "-" : $"{p} at {p.Position} (floor {ProjectZ.FloorOf(p.Position, map) + 1}, d {ProjectZ.GroundCellOf(p.Position, map).z - gEz})";

        private static bool StepRange(Map map)
        {
            switch (stage)
            {
                case "g_setup":
                    SetupRange(map);
                    return true;

                // ---- 1: shotguns on the ground vs a colonist on floor 13
                case "g_p1":
                {
                    GPlaceA(12, "Gun_BoltActionRifle", false);
                    Pawn r3 = GRaider(map, 3, "Gun_PumpShotgun"), r8 = GRaider(map, 8, "Gun_PumpShotgun");
                    Info($"P1: colonist {GWhere(gA, map)}; raiders {GWhere(r3, map)} 3D {Range3D.Distance(r3.Position, gA.Position, map):0.0}, {GWhere(r8, map)} 3D {Range3D.Distance(r8.Position, gA.Position, map):0.0}; shotgun range {r3.equipment.PrimaryEq.PrimaryVerb.EffectiveRange:0.0}");
                    Result("P1 game check: a ground shotgun can't hit floor 13 (CanHitTarget)", !CanHit(r3, gA) && !CanHit(r8, gA), $"d3 {CanHit(r3, gA)}, d8 {CanHit(r8, gA)}");
                    Thing pick = AttackTargetFinder.BestShootTargetFromCurrentPosition(r3, TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable)?.Thing;
                    Result("P1 game check: the raider's target finder doesn't pick the floor-13 colonist", pick != gA, $"picked {pick}");
                    TickUntil(1500, () => { GKeepA(); return false; }, "g_p1_check", 30);
                    return true;
                }
                case "g_p1_check":
                {
                    tickWatch = null;
                    int up = PZCombatStats.shots.Count(s => s.toFloor == 12);
                    Result("P1 real fight: shotgun raiders on the ground never fire at floor 13 (1500 ticks)", up == 0 && gRaiders.All(r => GShotsFrom(r) == 0), $"shots up {up}; all cross-floor shots: {GShotList()}; raider jobs {string.Join(", ", gRaiders.Select(r => r.CurJobDef?.defName))}");
                    // ---- 2a: the same raiders vs floor 2
                    GPlaceA(1, null, false);
                    PZCombatStats.shots.Clear();
                    Info($"P2a: colonist {GWhere(gA, map)}; 3D to the raiders {Range3D.Distance(gRaiders[0].Position, gA.Position, map):0.0}, {Range3D.Distance(gRaiders[1].Position, gA.Position, map):0.0}");
                    Result("P2a game check: the shotguns can hit floor 2 at d 3 and 8", CanHit(gRaiders[0], gA) && CanHit(gRaiders[1], gA), $"d3 {CanHit(gRaiders[0], gA)}, d8 {CanHit(gRaiders[1], gA)}");
                    TickUntil(2000, () => { GKeepA(); return gRaiders.All(r => GShotsFrom(r) > 0); }, "g_p2a_check", 10);
                    return true;
                }
                case "g_p2a_check":
                {
                    tickWatch = null;
                    Result("P2a real fight: shotgun raiders fire at the colonist on floor 2", gRaiders.All(r => GShotsFrom(r) > 0), $"shots: d3 {GShotsFrom(gRaiders[0])}, d8 {GShotsFrom(gRaiders[1])} after {ticksTotal} ticks; colonist {(gA.Downed ? "downed" : "standing")}");
                    GClearRaiders();
                    HealA();
                    // ---- 2b: floor 5, d 14 (3D 15.5) vs d 15 (3D 16.4)
                    GPlaceA(4, null, false);
                    Pawn r14 = GRaider(map, 14, "Gun_PumpShotgun"), r15 = GRaider(map, 15, "Gun_PumpShotgun");
                    Info($"P2b: colonist {GWhere(gA, map)}; 3D d14 {Range3D.Distance(r14.Position, gA.Position, map):0.00}, d15 {Range3D.Distance(r15.Position, gA.Position, map):0.00} (flat 14, 15; shotgun 15.9)");
                    Result("P2b game check: d14 can hit floor 5, d15 can't (in range flat, out in 3D)", CanHit(r14, gA) && !CanHit(r15, gA), $"d14 {CanHit(r14, gA)}, d15 {CanHit(r15, gA)}");
                    TickUntil(2000, () => { GKeepA(); return GShotsFrom(r14) >= 3; }, "g_p2b_check", 10);
                    return true;
                }
                case "g_p2b_check":
                {
                    tickWatch = null;
                    Pawn r14 = gRaiders[0], r15 = gRaiders[1];
                    Result("P2b real fight: the d14 raider fires at floor 5, the d15 raider never does", GShotsFrom(r14) > 0 && GShotsFrom(r15) == 0, $"shots d14 {GShotsFrom(r14)} ({GWhere(r14, map)}), d15 {GShotsFrom(r15)} ({GWhere(r15, map)}) after {ticksTotal} ticks");
                    GClearRaiders();
                    HealA();
                    // ---- 3: sniper on floor 5
                    GPlaceA(4, "Gun_SniperRifle", true);
                    Pawn r47 = GRaider(map, 47, "Gun_PumpShotgun");
                    Verb sv = gA.equipment.PrimaryEq.PrimaryVerb;
                    Info($"P3: sniper {GWhere(gA, map)} range {sv.EffectiveRange:0.0} (+4 down to the ground = {Range3D.Reach(sv.EffectiveRange, 4, 0):0.0}, over the ground {Range3D.HorizontalReach(sv.EffectiveRange, 4, 0):0.00}); raider d47 3D {Range3D.Distance(gA.Position, r47.Position, map):0.00}");
                    Result("P3 game check: the sniper on floor 5 can hit d 47 (beyond its flat range)", CanHit(gA, r47), $"CanHitTarget {CanHit(gA, r47)}");
                    TickUntil(3000, () => { GKeepA(); return GShotsFrom(gA) > 0; }, "g_p3_check", 10);
                    return true;
                }
                case "g_p3_check":
                {
                    tickWatch = null;
                    Pawn r47 = gRaiders[0];
                    Result("P3 real fight: the drafted sniper (fire at will) fires at the raider at d 47", GShotsFrom(gA) > 0, $"shots {GShotsFrom(gA)} after {ticksTotal} ticks; raider {GWhere(r47, map)} {(r47.Dead ? "dead" : r47.Downed ? "downed" : "standing")}; sniper job {gA.CurJobDef?.defName}");
                    GClearRaiders();
                    Pawn r49 = GRaider(map, 49, "Gun_PumpShotgun");
                    Result("P3 game check: not at d 49 (3D 49.5 > 44.9 + 4)", !CanHit(gA, r49), $"3D {Range3D.Distance(gA.Position, r49.Position, map):0.00}, CanHitTarget {CanHit(gA, r49)}");
                    TickUntil(1500, () => { GKeepA(); return GShotsFrom(gA) > 0; }, "g_p3b_check", 10);
                    return true;
                }
                case "g_p3b_check":
                {
                    tickWatch = null;
                    Result("P3 real fight: the sniper never fires at the raider at d 49 (1500 ticks)", GShotsFrom(gA) == 0, $"shots {GShotsFrom(gA)}; raider {GWhere(gRaiders[0], map)}; sniper job {gA.CurJobDef?.defName}");
                    GClearRaiders();
                    // ---- 4: turret on floor 2
                    Teleport(gA, ProjectZ.CellOnFloor(new IntVec3(gTower.maxX, 0, gTower.minZ + 1), 1, map));
                    gA.drafter.FireAtWill = false;
                    SetupTurret(map);
                    Pawn r29 = GRaider(map, 29, "Gun_PumpShotgun"), r31 = GRaider(map, 31, "Gun_PumpShotgun");
                    Verb tv = gTurret.AttackVerb;
                    Info($"P4: {gTurret} at {gTurret.Position} (floor 2) range {tv.EffectiveRange:0.0}, power {gTurret.TryGetComp<CompPowerTrader>()?.PowerOn}; raiders d29 3D {Range3D.Distance(gTurret.Position, r29.Position, map):0.00}, d31 3D {Range3D.Distance(gTurret.Position, r31.Position, map):0.00}");
                    Result("P4 game check: the turret's verb can hit d 29 (flat 29 > 28.9: needs the +1), not d 31", tv.CanHitTarget(r29) && !tv.CanHitTarget(r31), $"d29 {tv.CanHitTarget(r29)}, d31 {tv.CanHitTarget(r31)}");
                    gTurretTargets.Clear();
                    TickUntil(1500, () => { GKeepA(); if (gTurret.CurrentTarget.HasThing && gTurret.CurrentTarget.Thing.Spawned) gTurretTargets.Add(gTurret.CurrentTarget.Thing); return false; }, "g_p4_check", 5);
                    return true;
                }
                case "g_p4_check":
                {
                    tickWatch = null;
                    Pawn r29 = gRaiders[0], r31 = gRaiders[1];
                    Result("P4 real: the turret on floor 2 targets the raider at d 29 and never the one at d 31", gTurretTargets.Contains(r29) && !gTurretTargets.Contains(r31),
                        $"targets {string.Join(", ", gTurretTargets.Select(t => t.ToString()))}; turret shots {PZCombatStats.shots.Count(s => s.launcher == gTurret.ToString())}; d29 {(r29.Dead ? "dead" : r29.Downed ? "downed" : "standing")}");
                    if (!r29.Destroyed)
                    {
                        r29.Destroy();
                    }
                    gTurretTargets.Clear();
                    TickUntil(900, () => { if (gTurret.CurrentTarget.HasThing && gTurret.CurrentTarget.Thing.Spawned) gTurretTargets.Add(gTurret.CurrentTarget.Thing); return false; }, "g_p4b_check", 5);
                    return true;
                }
                case "g_p4b_check":
                {
                    tickWatch = null;
                    Pawn r31 = gRaiders[1];
                    Result("P4 real: alone, the raider at d 31 is never targeted (900 ticks)", !gTurretTargets.Contains(r31), $"targets {string.Join(", ", gTurretTargets.Select(t => t.ToString()))}");
                    GClearRaiders();
                    CompFlickable fl = gTurret.TryGetComp<CompFlickable>();
                    if (fl != null)
                    {
                        fl.SwitchIsOn = false;   // (off during the walk-in test)
                    }
                    // ---- 5: a rifle raider walking in on a colonist on floor 5
                    GPlaceA(4, "Gun_BoltActionRifle", false);
                    Pawn ra = GRaider(map, 52, "Gun_BoltActionRifle", assault: true);
                    Verb rv = ra.equipment.PrimaryEq.PrimaryVerb;
                    Info($"P5: raider {GWhere(ra, map)} range {rv.EffectiveRange:0.0} (reach over the ground up to floor 5: {Range3D.HorizontalReach(rv.EffectiveRange, 0, 4):0.00}); colonist {GWhere(gA, map)}");
                    gMoves.Clear();
                    gFirstShotFrom = IntVec3.Invalid;
                    TickUntil(5000, () =>
                    {
                        GKeepA();
                        Job j = ra.CurJob;
                        string mv = j == null ? "none" : j.def == JobDefOf.Goto ? $"Goto {j.targetA.Cell} (d {j.targetA.Cell.z - gEz})" : j.def.defName;
                        mv += $" [target {ra.mindState.enemyTarget?.ToString() ?? "none"}]";
                        if (gMoves.Count == 0 || gMoves[gMoves.Count - 1] != mv)
                        {
                            gMoves.Add(mv);
                        }
                        if (!gFirstShotFrom.IsValid && GShotsFrom(ra) > 0)
                        {
                            gFirstShotFrom = ra.Position;
                            gFirstShotTick = ticksTotal - ticksLeft;
                        }
                        return gFirstShotFrom.IsValid && GShotsFrom(ra) >= 3;
                    }, "g_p5_check", 5);
                    return true;
                }
                case "g_p5_check":
                {
                    tickWatch = null;
                    Pawn ra = gRaiders[0];
                    Verb rv = ra.equipment.PrimaryEq.PrimaryVerb;
                    float d3 = gFirstShotFrom.IsValid ? Range3D.Distance(gFirstShotFrom, gA.Position, map) : -1f;
                    int dz = gFirstShotFrom.IsValid ? gFirstShotFrom.z - gEz : -1;
                    Info("P5 raider jobs: " + string.Join(" > ", gMoves));
                    Result("P5 real: the raider walks in and fires from within the 3D range, not from the tower's foot", gFirstShotFrom.IsValid && d3 <= rv.EffectiveRange + 0.01f && dz >= 28,
                        $"first shot after {gFirstShotTick} ticks from {gFirstShotFrom} (d {dz}, 3D {d3:0.00}, range {rv.EffectiveRange:0.0}); shots {GShotsFrom(ra)}");
                    GClearRaiders();
                    HealA();
                    // ---- 6: mortar
                    MortarCheck(map);
                    gShot = 0;
                    Wait(2, "g_shot");
                    return true;
                }

                // ---- 7: preview shots
                case "g_shot":
                {
                    if (gShot >= GShots.Length)
                    {
                        IsoRimBridge.SetOption("freeYaw", gOldYaw);
                        Find.CameraDriver.config.sizeRange.max = gOldMax;
                        Find.Targeter.StopTargeting();
                        Find.Selector.ClearSelection();
                        Wait(5, "g_end");
                        return true;
                    }
                    if (gShot == 0)
                    {
                        gOldYaw = (float)IsoRimBridge.SetOption("freeYaw", 0f);
                        gOldMax = Find.CameraDriver.config.sizeRange.max;
                        Find.CameraDriver.config.sizeRange.max = 70f;
                    }
                    var s = GShots[gShot];
                    Find.Targeter.StopTargeting();
                    IsoRimBridge.SetOption("freeYaw", s.yaw);
                    if (s.gun == null)
                    {
                        Teleport(gA, ProjectZ.CellOnFloor(new IntVec3(gTower.maxX, 0, gTower.minZ + 1), 1, map));
                        ShotAt(map, 1, new IntVec3(gcx, 0, gEz + 14), s.size);
                        Find.Selector.Select(gTurret);
                    }
                    else
                    {
                        GPlaceA(s.floor, s.gun, false);
                        ShotAt(map, s.floor, new IntVec3(gcx, 0, gEz + (int)(s.size * 0.35f)), s.size);
                        gA.Drawer?.tweener?.ResetTweenedPosToRoot();
                        Find.Selector.Select(gA);
                        Find.Targeter.BeginTargeting(gA.equipment.PrimaryEq.PrimaryVerb);
                    }
                    Wait(40, "g_shot_take");
                    return true;
                }
                case "g_shot_take":
                    Info($"shot {GShots[gShot].name}: targeting {Find.Targeter.IsTargeting}, selected {Find.Selector.SingleSelectedThing}");
                    ZCapture(GShots[gShot].name);
                    gShot++;
                    Wait(2, "g_shot");
                    return true;
                case "g_end":
                    WatchCheck(map, "range end");
                    Info($"combat stats: cross-floor shots {PZCombatStats.crossFloorShots}, intercepts {PZCombatStats.intercepts}, slab stops {PZCombatStats.slabStops}");
                    Finish();
                    return true;
            }
            return false;
        }

        private static readonly (string name, int floor, string gun, float yaw, float size)[] GShots =
        {
            ("range_rifle_f1_yaw45", 0, "Gun_AssaultRifle", 0f, 34f), ("range_rifle_f1_yaw225", 0, "Gun_AssaultRifle", 180f, 34f),
            ("range_rifle_f2_yaw45", 1, "Gun_AssaultRifle", 0f, 34f), ("range_rifle_f2_yaw225", 1, "Gun_AssaultRifle", 180f, 34f),
            ("range_rifle_f5_yaw45", 4, "Gun_AssaultRifle", 0f, 34f), ("range_rifle_f5_yaw225", 4, "Gun_AssaultRifle", 180f, 34f),
            ("range_sniper_f1_yaw45", 0, "Gun_SniperRifle", 0f, 50f), ("range_sniper_f1_yaw225", 0, "Gun_SniperRifle", 180f, 50f),
            ("range_sniper_f2_yaw45", 1, "Gun_SniperRifle", 0f, 50f), ("range_sniper_f2_yaw225", 1, "Gun_SniperRifle", 180f, 50f),
            ("range_sniper_f5_yaw45", 4, "Gun_SniperRifle", 0f, 50f), ("range_sniper_f5_yaw225", 4, "Gun_SniperRifle", 180f, 50f),
            ("range_turret_f2_yaw45", 1, null, 0f, 32f), ("range_turret_f2_yaw225", 1, null, 180f, 32f),
        };

        private static void HealA()
        {
            bool downed = gA.Downed;
            HealthUtility.HealNonPermanentInjuriesAndRestoreLegs(gA);
            foreach (Hediff h in gA.health.hediffSet.hediffs.Where(h => h is Hediff_Injury || h.def == HediffDefOf.BloodLoss).ToList())
            {
                gA.health.RemoveHediff(h);
            }
            Info($"colonist {gA} healed (was {(downed ? "downed" : "standing")}, now {(gA.Downed ? "downed" : "standing")})");
        }

        private static void SetupTurret(Map map)
        {
            IntVec3 F2(int x, int z) => ProjectZ.CellOnFloor(new IntVec3(x, 0, z), 1, map);
            Place(DefDatabase<ThingDef>.GetNamed("PowerConduit"), null, F2(gcx, gEz - 1), map);
            Place(DefDatabase<ThingDef>.GetNamed("PowerConduit"), null, F2(gcx - 1, gEz - 1), map);
            Thing gen = Place(DefDatabase<ThingDef>.GetNamed("WoodFiredGenerator"), null, F2(gcx - 1, gEz - 3), map);
            gen.TryGetComp<CompRefuelable>()?.Refuel(500f);
            gTurret = (Building_TurretGun)Place(DefDatabase<ThingDef>.GetNamed("Turret_MiniTurret"), ThingDefOf.Steel, F2(gcx, gEz), map);
        }

        private static void MortarCheck(Map map)
        {
            IntVec3 at = new IntVec3(gcx + 12, 0, gEz + 8);
            Building_TurretGun mortar = (Building_TurretGun)Place(DefDatabase<ThingDef>.GetNamed("Turret_Mortar"), null, at, map);
            Verb mv = mortar.AttackVerb;
            float min = mv.verbProps.EffectiveMinRange(allowAdjacentShot: true);
            foreach ((int k, int dx, int dz) in new[] { (12, 0, 20), (1, 0, 40), (1, 0, 29), (4, 0, 35) })
            {
                IntVec3 c = ProjectZ.CellOnFloor(new IntVec3(at.x + dx, 0, Mathf.Min(at.z + dz, gArea.maxZ)), k, map);
                mortar.OrderAttack(LocalTargetInfo.Invalid);
                mortar.OrderAttack(new LocalTargetInfo(c));
                Info($"P6 mortar at {at} (min range {min:0.0}, range {mv.EffectiveRange:0}) ordered at {c} (floor {k + 1}, ground distance {(ProjectZ.GroundCellOf(c, map) - at).LengthHorizontal:0.0}, 3D {Range3D.Distance(at, c, map):0.0}): forced target {(mortar.ForcedTarget.IsValid ? "set" : "refused")}, CanHitTarget {mv.CanHitTarget(c)}");
            }
            mortar.OrderAttack(LocalTargetInfo.Invalid);
            mortar.Destroy();
        }

        private static void SetupRange(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || an.floors < 5)
            {
                Result("range setup", false, $"needs at least 5 floors (run with -projectzfloors=13); floors {an?.floors}");
                Finish();
                return;
            }
            List<Pawn> cols = Colonists(map);
            IntVec3 near = cols.Count > 0 ? new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z)) : map.Center;
            // The 41x64 field with the fewest cells to fix (water, rock, roofs, buildings); then made a plain open field.
            CellRect found = CellRect.Empty;
            int bestBad = int.MaxValue;
            for (int z = 3; z + 64 < an.groundZ - 3; z += 4)
            {
                for (int x = 3; x + 41 < map.Size.x - 3; x += 4)
                {
                    CellRect r = new CellRect(x, z, 41, 64);
                    if (r.ExpandedBy(1).Cells.Any(c => c.InBounds(map) && c.GetThingList(map).Any(t => !t.def.destroyable)))
                    {
                        continue;   // (monoliths, geysers)
                    }
                    int bad = r.Cells.Count(c => c.GetTerrain(map).IsWater || c.GetTerrain(map).passability == Traversability.Impassable || map.roofGrid.Roofed(c) || c.GetFirstBuilding(map) != null);
                    if (bad < bestBad || bad == bestBad && r.CenterCell.DistanceToSquared(near) < found.CenterCell.DistanceToSquared(near))
                    {
                        bestBad = bad;
                        found = r;
                    }
                }
            }
            if (found.Area == 0)
            {
                Result("range test spot", false, "map too small for a 41x64 field");
                Finish();
                return;
            }
            gArea = found;
            foreach (IntVec3 c in found.ExpandedBy(1))
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (!(t is Pawn) && t.def.destroyable)
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
            Info($"field picked with {bestBad} cells fixed (water, rock, roofs, buildings)");
            DebugSettings.enableRandomMentalStates = false;
            gcx = found.CenterCell.x;
            gTower = new CellRect(gcx - 2, found.minZ, 5, 5);
            gEz = gTower.maxZ;
            // the tower: a roof under each floor's slab, slabs on floors 2 to 13 (the roof below becomes the floor's)
            for (int k = 1; k < an.floors; k++)
            {
                foreach (IntVec3 g in gTower)
                {
                    IntVec3 below = ProjectZ.CellOnFloor(g, k - 1, map);
                    map.roofGrid.SetRoof(below, RoofDefOf.RoofConstructed);
                    map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(g, k, map), PZDefOf.PZ_UpperFloor);
                }
            }
            foreach (IntVec3 g in gTower)
            {
                for (int k = 1; k < an.floors; k++)
                {
                    map.fogGrid.Unfog(ProjectZ.CellOnFloor(g, k, map));
                }
            }
            // A: the shooter (one who can shoot); everyone else drafted, holding fire, in a sealed hut far from the field
            gA = cols.Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)).OrderByDescending(p => p.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0).FirstOrDefault() ?? cols.First();
            List<Pawn> others = cols.Where(p => p != gA).ToList();
            IntVec3 hut = IntVec3.Invalid;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(new IntVec3(Mathf.Clamp(found.CenterCell.x > map.Size.x / 2 ? 6 : map.Size.x - 7, 3, map.Size.x - 4), 0, Mathf.Clamp(found.CenterCell.z > an.groundZ / 2 ? 6 : an.groundZ - 7, 3, an.groundZ - 4)), 30f, true))
            {
                CellRect h = new CellRect(c.x - 2, c.z - 2, 5, 5);
                if (h.InBounds(map) && h.maxZ < an.groundZ && !h.Overlaps(found.ExpandedBy(8)) && h.Cells.All(x => x.Standable(map) && x.GetFirstBuilding(map) == null))
                {
                    hut = c;
                    foreach (IntVec3 e in h.EdgeCells)
                    {
                        foreach (Thing t in e.GetThingList(map).ToList())
                        {
                            if (t is Plant || t.def.category == ThingCategory.Item)
                            {
                                t.Destroy();
                            }
                        }
                        Place(ThingDefOf.Wall, ThingDefOf.WoodLog, e, map);
                    }
                    break;
                }
            }
            for (int i = 0; i < others.Count; i++)
            {
                others[i].drafter.Drafted = true;
                others[i].drafter.FireAtWill = false;
                if (hut.IsValid)
                {
                    Teleport(others[i], hut + GenAdj.AdjacentCellsAndInside[i % 9]);
                }
            }
            Info($"field {found} (centre column x {gcx}), tower {gTower}, edge row {gEz}; floors {an.floors}, storey {Range3D.Storey:0.00} cells; shooter {gA} (shooting {gA.skills?.GetSkill(SkillDefOf.Shooting)?.Level}); others in a hut at {hut}");
            FloorView.Set(0);
            Wait(10, "g_p1");
        }
    }
}
