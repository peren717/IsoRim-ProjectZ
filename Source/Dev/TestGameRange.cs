using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ProjectZ.Combat;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // Mode gamerange (single source of truth, step 1: every range through GameRange, the copied constants). Run with
    // -projectzfloors=3 -projectzseed=7 -projectzmapsize=120 and -isorim3d. Router2's fixture tools: one fixture per check
    // (area wiped on all floors, its own fixed random state), real jobs, lords and ticks.
    //   D0  the walking storey (Dist3D.StoreyCells) from the Human def's move speed: unmodified exactly as the old 60/4.6.
    //   W1  a drafted sniper on floor 1 targeting (Targeter, the game's ring call): ProjectZ's 3D rings and shell are drawn
    //       exactly when the game drew the weapon's ring (its own limit, no copied 75); W2 a selected turret the same.
    //   J1  the flight between floors reads the locust jump def (range, cooldown); a duck flies to a floor-1 landing 15
    //       cells away (3D) and another to one 22.5 away: the far one only when the def's range reaches it.
    //   C1  a lamp on the ground 5 and 8 cells (mapped) from a floor-1 conduit hooks up as the game's own hookup range says
    //       (rule a); C2 a lamp waiting before the conduit is built (rule b).
    //   S1  a sapper on the ground and a colonist on a floor-1 platform 55 cells away (Dist3D): the climb takes it as a
    //       target exactly when the game's sapper filter does (its radius).
    //   G1  parity, vanilla Projectile.CheckForFreeIntercept against CombatPatches' InterceptChance (one floor);
    //   G2  vanilla ShotReport's blind-smoke factor against ShotReport_Patch.SmokeFactor (one floor);
    //   G3  vanilla CastPositionFinder (its search state, its own cell evaluation) against CastSpots.Preference (the game's
    //       method asked through its private copy, with the avoid factor): every term, cover wanted or not;
    //   G4  vanilla AvoidGrid.PrintAvoidGridAroundTurret against AvoidGrid_Turret_Patch.Mark on the turret's own floor.
    //   T0  frame time with a drafted shooter targeting and a turret selected (3D view, floor 1 viewed).
    // Stand-ins for other mods (test Harmony id, applied at setup): -projectzconnectdist=N (vanilla's hookup range 6
    // becomes N in both hookup searches), -projectzsapperradius=N (the sapper filter's 2500 becomes N * N). The jump and
    // weapon ranges come from the XML test mod Source/Dev/GameRangeTestMod (sniper range 78, longjump 25.9; put in
    // place with testmods.sh install gamerange, packageId Peren.PZGameRangeTest).
    public static partial class TestRunner
    {
        private const int GrSeed = 9500;
        private static Harmony grModHarmony, grHarmony;
        private static int grConnect = 6, grSapper = 50, grModSwaps;
        private static int grStep, grFrames, grRingsAt, grShellsAt;
        private static Pawn grA, grB;
        private static Thing grT;
        private static readonly List<float> grChances = new List<float>();
        private static readonly List<Thing> grImpacts = new List<Thing>();
        private static float grFrameMs;

        private static bool StepGameRange(Map map)
        {
            switch (stage)
            {
                case "gr_setup": GrSetup(map); return true;
                case "gr_d0": GrD0(map); return true;
                case "gr_w1": GrW1(map); return true;
                case "gr_w1_take": GrW1Take(map); return true;
                case "gr_j1": GrJ1(map); return true;
                case "gr_j1_check": GrJ1Check(map); return true;
                case "gr_j1_shot": GrJ1Shot(map); return true;
                case "gr_c1": GrC1(map); return true;
                case "gr_c1_check": GrC1Check(map); return true;
                case "gr_s1": GrS1(map); return true;
                case "gr_s1_check": GrS1Check(map); return true;
                case "gr_g1": GrG1(map); return true;
                case "gr_g2": GrG2(map); return true;
                case "gr_g3": GrG3(map); return true;
                case "gr_g4": GrG4(map); return true;
                case "gr_t0": GrT0(map); return true;
                case "gr_t0_take": GrT0Take(map); return true;
                case "gr_done": GrDone(map); return true;
            }
            return false;
        }

        private static void GrSetup(Map map)
        {
            DebugSettings.enableRandomMentalStates = false;
            NoRoofCollapse(map);
            Find.Storyteller.storytellerComps.Clear();
            Find.PlaySettings.autoRebuild = false;
            NoAmbushes(map);
            foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
            {
                a.Destroy();
            }
            foreach (Zone z in map.zoneManager.AllZones.ToList())
            {
                z.Delete(false);
            }
            AnnexMapComponent annex = ProjectZ.Annex(map);
            if (annex == null || annex.MaxLevel < 2)
            {
                Result("gamerange: a map with 3 floors", false, $"annex {annex != null} (run with -projectzfloors=3)");
                Finish();
                return;
            }
            grModHarmony = new Harmony("Peren.IsoRimProjectZ.test.gamerangemod");
            if (GenCommandLine.TryGetCommandLineArg("projectzconnectdist", out string cd) && int.TryParse(cd, out int n) && n > 0)
            {
                grConnect = n;
                grModHarmony.Patch(AccessTools.Method(typeof(PowerConnectionMaker), nameof(PowerConnectionMaker.BestTransmitterForConnector)), transpiler: new HarmonyMethod(typeof(TestRunner), nameof(GrConnectTranspiler)));
                grModHarmony.Patch(AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(PowerConnectionMaker), "PotentialConnectorsForTransmitter")), transpiler: new HarmonyMethod(typeof(TestRunner), nameof(GrConnectTranspiler)));
            }
            if (GenCommandLine.TryGetCommandLineArg("projectzsapperradius", out string sr) && int.TryParse(sr, out int r) && r > 0)
            {
                grSapper = r;
                grModHarmony.Patch(SapperRule.FilterMethod(), transpiler: new HarmonyMethod(typeof(TestRunner), nameof(GrSapperTranspiler)));
            }
            int gz = annex.groundZ;
            r2Area = new CellRect(map.Size.x / 2 - 32, gz / 2 - 30, 64, 60);
            List<Pawn> cols = Colonists(map);
            r2P = cols.First();
            r2Q = cols.FirstOrDefault(p => p != r2P);
            AbilityDef jd = FloorFlight.JumpDef;
            Info($"gamerange: area {r2Area}, map {map.Size}, groundZ {gz}, storey {IsoRimBridge.StoreyHeight:0.00} cells; stand-ins: hookup range {grConnect}, sapper radius {grSapper} ({grModSwaps} constants swapped); "
                + $"sniper range {ThingDef.Named("Gun_SniperRifle").Verbs[0].range}, locust jump def range {jd?.verbProperties?.range} cooldown {jd?.cooldownTicksRange}; patches: rect {FailSoft.IsApplied(nameof(PotentialConnectors_Rect_Patch))}, ring seen {FailSoft.IsApplied(nameof(GenDraw_RadiusRing_Seen_Patch))}");
            Info("copy watches: registered " + CopyWatches.Registered + "; hashes " + CopyWatches.Hashes());
            Result("W0 the copied methods are watched (IsoRim copy watch), their hashes as registered", CopyWatches.Registered == CopyWatches.Count && !CopyWatches.Hashes().Contains("(registered"), CopyWatches.Hashes());
            stage = "gr_d0";
        }

        // (stand-in mod) vanilla's hookup range 6 (ExpandedBy(6)) becomes grConnect
        public static IEnumerable<CodeInstruction> GrConnectTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo expanded = AccessTools.Method(typeof(CellRect), nameof(CellRect.ExpandedBy), new[] { typeof(int) });
            List<CodeInstruction> code = instructions.ToList();
            for (int i = 0; i + 1 < code.Count; i++)
            {
                if (code[i].LoadsConstant(6) && code[i + 1].Calls(expanded))
                {
                    code[i] = new CodeInstruction(OpCodes.Ldc_I4, grConnect).MoveLabelsFrom(code[i]);
                    grModSwaps++;
                }
            }
            return code;
        }

        // (stand-in mod) the sapper filter's 2500 becomes grSapper^2
        public static IEnumerable<CodeInstruction> GrSapperTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = instructions.ToList();
            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].LoadsConstant(2500))
                {
                    code[i] = new CodeInstruction(OpCodes.Ldc_I4, grSapper * grSapper).MoveLabelsFrom(code[i]);
                    grModSwaps++;
                }
            }
            return code;
        }

        private static void GrScene(Map map, int seed)
        {
            R2Clear(map);
            R2Unhide(map);
            R2Park(map, r2P, r2Q);
            R2Feed(map);
            Find.Targeter.StopTargeting();
            Find.Selector.ClearSelection();
            Rand.PushState(seed);
        }

        // ---- D0

        private static void GrD0(Map map)
        {
            float old = 3f + 30 / (60f / 4.6f);
            float human = ThingDefOf.Human.statBases.GetStatValueFromList(StatDefOf.MoveSpeed, -1f);
            float expect = Dist3D.RampCells + Climb.StepTicks / (GenTicks.TicksPerRealSecond / human);
            bool modded = Mathf.Abs(human - 4.6f) > 0.0001f;
            Result("D0 storey in walking cells from the Human def's move speed (unmodified: exactly the old 60 / 4.6)",
                Dist3D.StoreyCells == expect && (modded || Dist3D.StoreyCells == old),
                $"StoreyCells {Dist3D.StoreyCells:R}, old constant {old:R}, Human MoveSpeed {human}, MoveSpeed default {StatDefOf.MoveSpeed.defaultBaseValue}");
            stage = "gr_w1";
        }

        // ---- W1, W2: the weapon ring follows the game's own ring

        private static void GrW1(Map map)
        {
            GrScene(map, GrSeed + 1);
            try
            {
                IntVec3 c = RG(20, 30);
                R2Floor(map, new CellRect(c.x - 6, c.z - 4, 30, 9), 1);
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                Teleport(r2P, Levels.Cell(c, 1, map));
                Draft(r2P, true);
                r2P.equipment.DestroyAllEquipment();
                r2P.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(ThingDef.Named("Gun_SniperRifle")));
                grT = Place(ThingDef.Named("Turret_MiniTurret"), null, Levels.Cell(c + new IntVec3(10, 0, 0), 1, map), map);
                BrView3D(true);
                ShotAt(map, 0, c + new IntVec3(6, 0, 0), 60f);
            }
            finally
            {
                Rand.PopState();
            }
            Find.Selector.Select(r2P, playSound: false, forceDesignatorDeselect: false);
            Find.Selector.Select(grT, playSound: false, forceDesignatorDeselect: false);
            Find.Targeter.BeginTargeting(r2P.equipment.PrimaryEq.PrimaryVerb);
            grRingsAt = RangePreview.Rings3D;
            grShellsAt = RangePreview.Shells;
            grStep = 0;
            Wait(30, "gr_w1_take");
        }

        private static void GrW1Take(Map map)
        {
            if (grStep == 0)
            {
                Capture("gr_ring_sniper");
                grStep = 1;
                return;
            }
            Verb v = r2P.equipment.PrimaryEq.PrimaryVerb;
            float range = v.EffectiveRange;
            bool gameRing = range < GenRadial.MaxRadialPatternRadius && range < map.Size.x + map.Size.z;
            int rings = RangePreview.Rings3D - grRingsAt, shells = RangePreview.Shells - grShellsAt;
            // (two callers a frame at most: the sniper while targeting, the turret selected; each once a frame)
            Result($"W1 a drafted sniper (range {range}) targeting: 3D rings and a shell exactly when the game drew its ring",
                gameRing ? rings > 0 && shells > 0 : true,
                $"the game draws the ring {gameRing} (radial limit {GenRadial.MaxRadialPatternRadius}); rings added {rings}, shells {shells} over 30 frames (sniper + turret)");
            Verb tv = ((Building_TurretGun)grT).AttackVerb;
            Result($"W2 a selected turret (range {tv.EffectiveRange}): its 3D rings drawn", rings > 0, $"rings {rings}");
            Find.Targeter.StopTargeting();
            Find.Selector.ClearSelection();
            stage = "gr_j1";
        }

        // ---- J1: the flight reads the locust jump def

        private static float grNear = 15f, grFar = 22.5f;
        private static IntVec3 grLandNear, grLandFar;

        private static void GrJ1(Map map)
        {
            GrScene(map, GrSeed + 2);
            float s = IsoRimBridge.StoreyHeight;
            try
            {
                AbilityDef jd = FloorFlight.JumpDef;
                float defRange = jd?.verbProperties?.range ?? -1f;
                Result("J1 the flight's range and warm-up are the longjump def's, its cooldown the locust jump def's",
                    jd != null && FloorFlight.Range == defRange && FloorFlight.CooldownTicks == FloorFlight.LocustDef.cooldownTicksRange.max && FloorFlight.WarmupTicks == jd.verbProperties.warmupTime.SecondsToTicks(),
                    $"range {FloorFlight.Range} (def {defRange}), warm-up {FloorFlight.WarmupTicks} ticks (def {jd?.verbProperties?.warmupTime} s), cooldown {FloorFlight.CooldownTicks} (locust def {FloorFlight.LocustDef?.cooldownTicksRange}); range def {jd?.defName}");
                int hn = Mathf.RoundToInt(Mathf.Sqrt(grNear * grNear - s * s)), hf = Mathf.RoundToInt(Mathf.Sqrt(grFar * grFar - s * s));
                IntVec3 a = RG(4, 12), b = RG(4, 40);
                R2Floor(map, new CellRect(a.x + hn - 2, a.z - 2, 5, 5), 1);
                R2Floor(map, new CellRect(b.x + hf - 2, b.z - 2, 5, 5), 1);
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                grLandNear = Levels.Cell(a + new IntVec3(hn, 0, 0), 1, map);
                grLandFar = Levels.Cell(b + new IntVec3(hf, 0, 0), 1, map);
                grNear = Range3D.Distance(a, grLandNear, map);
                grFar = Range3D.Distance(b, grLandFar, map);
                grA = FjMake(map, "Duck", "gr_duckA", null, a);
                grB = FjMake(map, "Duck", "gr_duckB", null, b);
            }
            finally
            {
                Rand.PopState();
            }
            if (grA == null || grB == null || !FloorFlight.Flies(grA))
            {
                Result("J1 flights", false, "no flying duck");
                stage = "gr_c1";
                return;
            }
            grA.jobs.StartJob(FloorFlight.MakeJob(grLandNear), JobCondition.InterruptForced);
            grB.jobs.StartJob(FloorFlight.MakeJob(grLandFar), JobCondition.InterruptForced);
            SeedTicks("gr_j1");
            TickUntil(400, () => Levels.LevelOf(grA.Position, map) == 1 && Levels.LevelOf(grB.Position, map) == 1, "gr_j1_check", 10);
        }

        private static void GrJ1Check(Map map)
        {
            tickWatch = null;
            bool nearUp = grA.Spawned && Levels.LevelOf(grA.Position, map) == 1;
            bool farUp = grB.Spawned && Levels.LevelOf(grB.Position, map) == 1;
            bool farExpected = grFar <= FloorFlight.Range;
            Result($"J1 real flights: 3D {grNear:0.0} always, 3D {grFar:0.0} only within the def's range {FloorFlight.Range}",
                nearUp && farUp == farExpected, $"near landed {nearUp}, far landed {farUp} (expected {farExpected}); flights {FloorFlight.Flights}");
            BrView3D(true);
            ShotAt(map, 1, grLandFar + new IntVec3(-8, 0, 0), 40f);
            grFrames = 0;
            stage = "gr_j1_shot";
        }

        // (the sheet) the flight's reach drawn on floor 1 round the far duck's take-off column (a test overlay)
        private static void GrJ1Shot(Map map)
        {
            IntVec3 a = RG(4, 40);
            float h = GameRange.HorizontalReach(FloorFlight.Range, true, 0, 1);
            if (h > 1f)
            {
                GenDraw.DrawRadiusRing(Levels.Cell(a, 1, map), Mathf.Min(h, GenRadial.MaxRadialPatternRadius - 1f), Color.cyan);
            }
            if (++grFrames == 10)
            {
                Capture("gr_flight_range");
            }
            if (grFrames >= 12)
            {
                stage = "gr_c1";
            }
        }

        // ---- C1, C2: hookups follow the game's own hookup range

        private static Building grLampNear, grLampFar, grLampWait;

        private static void GrC1(Map map)
        {
            GrScene(map, GrSeed + 3);
            try
            {
                IntVec3 a = RG(30, 10), b = RG(30, 40);
                R2Floor(map, new CellRect(a.x - 3, a.z - 3, 7, 7), 1);
                R2Floor(map, new CellRect(b.x - 3, b.z - 3, 7, 7), 1);
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                ThingDef lamp = ThingDef.Named("StandingLamp");
                // rule b: a lamp waiting before its conduit upstairs exists
                grLampWait = (Building)Place(lamp, null, b + new IntVec3(8, 0, 0), map);
                // rule a: the conduit first, the lamps after
                Place(ThingDefOf.PowerConduit, null, Levels.Cell(a, 1, map), map);
                grLampNear = (Building)Place(lamp, null, a + new IntVec3(-5, 0, 0), map);
                grLampFar = (Building)Place(lamp, null, a + new IntVec3(8, 0, 0), map);
            }
            finally
            {
                Rand.PopState();
            }
            grStep = 0;
            SeedTicks("gr_c1");
            StartTicks(5, "gr_c1_check");
        }

        private static void GrC1Check(Map map)
        {
            if (grStep == 0)
            {
                Place(ThingDefOf.PowerConduit, null, Levels.Cell(RG(30, 40), 1, map), map);
                grStep = 1;
                SeedTicks("gr_c1b");
                StartTicks(5, "gr_c1_check");
                return;
            }
            bool far = grConnect >= 8;
            CompPower n = grLampNear.PowerComp, f = grLampFar.PowerComp, w = grLampWait.PowerComp;
            Result($"C1 lamps 5 and 8 cells from a floor-1 conduit hook up by the game's range ({grConnect})",
                n.connectParent != null && (f.connectParent != null) == far,
                $"near {n.connectParent?.parent}, far {f.connectParent?.parent} (expected {(far ? "hooked" : "none")})");
            Result($"C2 a lamp 8 cells off, waiting, hooks up when the conduit upstairs is built, by the game's range ({grConnect})",
                (w.connectParent != null) == far, $"waiting lamp {w.connectParent?.parent} (expected {(far ? "hooked" : "none")})");
            stage = "gr_s1";
        }

        // ---- S1: the climb's sapper target rule is the game's

        private static int grNoTargetAt, grPlannedAt, grSapperSq;
        private static float grSapperDist;
        private static bool grSapperTakes;

        private static int GrPlanned() => RaidClimb.Plans + RaidClimb.PlanFails + RaidClimb.Reused + RaidClimb.MemoHits + RaidClimb.BudgetWaits;

        private static void GrS1(Map map)
        {
            GrScene(map, GrSeed + 4);
            Pawn raider;
            try
            {
                IntVec3 c = RG(8, 30);
                R2Floor(map, new CellRect(c.x - 3, c.z - 3, 7, 7), 1);
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                R2Hide(map, r2P);
                Teleport(r2P, Levels.Cell(c, 1, map));
                Draft(r2P, true);
                raider = MakeRaider(map, RG(58, 30));
                raider.equipment.DestroyAllEquipment();
                grA = raider;
                LordMaker.MakeNewLord(raider.Faction, new LordJob_AssaultColony(raider.Faction, canKidnap: false, canTimeoutOrFlee: false, sappers: true, useAvoidGridSmart: false, canSteal: false), map, new List<Pawn> { raider });
            }
            finally
            {
                Rand.PopState();
            }
            grSapperDist = Dist3D.Distance(raider.Position, r2P.Position, map);
            grSapperSq = Patches.Dist3D.DistSq(raider.Position, r2P.Position, map);
            grSapperTakes = SapperRule.Takes(raider, r2P);   // (where they stand, before any move)
            grNoTargetAt = RaidClimb.NoTarget;
            grPlannedAt = GrPlanned();
            SeedTicks("gr_s1");
            StartTicks(240, "gr_s1_check");
        }

        private static void GrS1Check(Map map)
        {
            Pawn raider = grA;
            int sq = grSapperSq;
            bool expected = sq <= grSapper * grSapper;
            bool takes = grSapperTakes;
            int planned = GrPlanned() - grPlannedAt, none = RaidClimb.NoTarget - grNoTargetAt;
            Result($"S1 the climb takes the colonist {grSapperDist:0.0} cells off (3D walk) as a sapper target exactly as the game's filter (radius {grSapper})",
                takes == expected && (expected ? planned > 0 : planned == 0 && none > 0),
                $"distance squared {sq}, filter takes {takes} (expected {expected}); in 240 ticks: climb plans or reuses {planned}, no target {none}; duty {raider.mindState.duty?.def?.defName}");
            raider.Destroy();
            R2Unhide(map);
            stage = "gr_g1";
        }

        // ---- G1: intercept parity

        public static IEnumerable<CodeInstruction> GrInterceptTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo chance = AccessTools.Method(typeof(Rand), nameof(Rand.Chance));
            MethodInfo impact = AccessTools.Method(typeof(Projectile), "Impact");
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(chance))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TestRunner), nameof(GrChance))).MoveLabelsFrom(ci);
                }
                else if (ci.Calls(impact))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TestRunner), nameof(GrImpact))).MoveLabelsFrom(ci);
                }
                else
                {
                    yield return ci;
                }
            }
        }

        public static bool GrChance(float p)
        {
            grChances.Add(p);
            return false;
        }

        public static void GrImpact(Projectile p, Thing hit, bool blocked)
        {
            grImpacts.Add(hit);
        }

        private static void GrG1(Map map)
        {
            GrScene(map, GrSeed + 5);
            List<string> bad = new List<string>(), all = new List<string>();
            try
            {
                MethodInfo check = AccessTools.Method(typeof(Projectile), "CheckForFreeIntercept");
                grHarmony = new Harmony("Peren.IsoRimProjectZ.test.gamerange");
                grHarmony.Patch(check, transpiler: new HarmonyMethod(typeof(TestRunner), nameof(GrInterceptTranspiler)));
                Teleport(r2P, RG(6, 20));
                IntVec3 dest = RG(44, 20);
                Pawn raider = MakeRaider(map, RG(20, 20));
                Pawn downed = MakeRaider(map, RG(24, 20));
                HealthUtility.DamageUntilDowned(downed, allowBleedingWounds: false);
                Teleport(r2Q, RG(28, 20));
                Thing sandFar = Place(ThingDef.Named("Sandbags"), ThingDefOf.Steel, RG(32, 20), map);
                Thing sandNear = Place(ThingDef.Named("Sandbags"), ThingDefOf.Steel, dest + new IntVec3(-1, 0, 0), map);
                Thing wall = Place(ThingDefOf.Wall, ThingDefOf.WoodLog, RG(36, 20), map);
                Thing chunk = GenSpawn.Spawn(ThingDef.Named("ChunkGranite"), RG(16, 20), map);
                Building_Door door = (Building_Door)Place(ThingDefOf.Door, ThingDefOf.WoodLog, RG(40, 20), map);
                AccessTools.Field(typeof(Building_Door), "openInt").SetValue(door, true);
                foreach (bool ff in new[] { false, true })
                {
                    Projectile proj = (Projectile)GenSpawn.Spawn(ThingDef.Named("Bullet_SniperRifle"), r2P.Position, map);
                    proj.Launch(r2P, r2P.DrawPos, new LocalTargetInfo(dest), new LocalTargetInfo(dest), ProjectileHitFlags.All, preventFriendlyFire: ff);
                    Vector3 origin = (Vector3)AccessTools.Field(typeof(Projectile), "origin").GetValue(proj);
                    Vector3 destination = (Vector3)AccessTools.Field(typeof(Projectile), "destination").GetValue(proj);
                    MethodInfo canHit = AccessTools.Method(typeof(Projectile), "CanHit");
                    foreach (Thing t in new[] { raider, downed, (Thing)r2Q, sandFar, sandNear, wall, chunk, door })
                    {
                        IntVec3 c = t.Position;
                        grChances.Clear();
                        grImpacts.Clear();
                        check.Invoke(proj, new object[] { c });
                        string game = grImpacts.Count > 0 ? "wall" : grChances.Count > 0 ? grChances[0].ToString("R") : "none";
                        string ours = "none";
                        if ((bool)canHit.Invoke(proj, new object[] { t }))
                        {
                            float n = Projectile_Intercept_Patch.InterceptChance(t, c, destination.ToIntVec3(), r2P, ff, VerbUtility.InterceptChanceFactorFromDistance(origin, c), out bool w);
                            ours = w ? "wall" : n > 1E-05f ? n.ToString("R") : "none";
                        }
                        string line = $"{t.def.defName}{(t == downed ? "(down)" : "")}{(ff ? " noFF" : "")}: game {game}, ours {ours}";
                        all.Add(line);
                        if (game != ours)
                        {
                            bad.Add(line);
                        }
                    }
                    proj.Destroy();
                }
                raider.Destroy();
                downed.Destroy();
            }
            catch (Exception e)
            {
                bad.Add("error " + e);
            }
            finally
            {
                Rand.PopState();
                grHarmony?.UnpatchAll("Peren.IsoRimProjectZ.test.gamerange");
            }
            Result("G1 parity: intercept chances, vanilla Projectile.CheckForFreeIntercept = ours (one floor)", bad.Count == 0 && all.Count == 16,
                (bad.Count > 0 ? "DIFF " + string.Join("; ", bad) + " | " : "") + string.Join("; ", all));
            stage = "gr_g2";
        }

        // ---- G2: blind smoke parity

        private static void GrG2(Map map)
        {
            GrScene(map, GrSeed + 6);
            List<string> parts = new List<string>();
            bool ok = true;
            try
            {
                Teleport(r2P, RG(6, 20));
                Draft(r2P, true);
                r2P.equipment.DestroyAllEquipment();
                r2P.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(ThingDef.Named("Gun_BoltActionRifle")));
                Pawn raider = MakeRaider(map, RG(26, 20));
                Verb v = r2P.equipment.PrimaryEq.PrimaryVerb;
                FieldInfo gas = AccessTools.Field(typeof(ShotReport), "factorFromCoveringGas");
                FieldInfo line = AccessTools.Field(typeof(ShotReport), "shootLine");
                foreach ((string name, IntVec3 smoke) in new[] { ("no smoke", IntVec3.Invalid), ("smoke on the line", RG(16, 20)), ("smoke off the line", RG(16, 26)) })
                {
                    if (smoke.IsValid)
                    {
                        GasUtility.AddGas(smoke, map, GasType.BlindSmoke, 255);
                    }
                    object rep = ShotReport.HitReportFor(r2P, v, raider);
                    float game = (float)gas.GetValue(rep);
                    float ours = ShotReport_Patch.SmokeFactor(v, (ShootLine)line.GetValue(rep), map);
                    ok &= game == ours;
                    parts.Add($"{name}: game {game}, ours {ours}");
                    if (smoke.IsValid)
                    {
                        map.gasGrid.Debug_ClearAll();
                    }
                }
                raider.Destroy();
            }
            catch (Exception e)
            {
                ok = false;
                parts.Add("error " + e);
            }
            finally
            {
                Rand.PopState();
            }
            Result("G2 parity: blind-smoke factor, vanilla ShotReport.HitReportFor = ours (one floor)", ok && parts.Count == 3, string.Join("; ", parts));
            stage = "gr_g3";
        }

        // ---- G3: CastPositionFinder parity. The game's own search state comes from a real TryFindCastPosition; each
        // cell's preference from its own EvaluateCell (which also applies the avoid grid), against CastSpots.Preference
        // (the game's method asked through its private copy).

        private static float GrVanillaPref(CastPositionRequest req, IntVec3 c, AvoidGrid avoid, float rft, float rftSq, float optSq)
        {
            Type t = typeof(CastPositionFinder);
            void Set(string f, object val) => AccessTools.Field(t, f).SetValue(null, val);
            Set("req", req);
            Set("casterLoc", req.caster.Position);
            Set("targetLoc", req.target.Position);
            Set("verb", req.verb);
            if (avoid != null)
            {
                Set("avoidGrid", avoid.Grid);
            }
            else
            {
                Set("avoidGrid", ((Unity.Collections.NativeArray<byte>)AccessTools.Field(t, "emptyByteArray").GetValue(null)).AsReadOnly());
            }
            Set("maxRangeFromCasterSquared", req.maxRangeFromCaster * req.maxRangeFromCaster);
            Set("maxRangeFromTargetSquared", 0f);
            Set("maxRangeFromLocusSquared", 0f);
            Set("rangeFromTarget", rft);
            Set("rangeFromTargetSquared", rftSq);
            Set("optimalRangeSquared", optSq);
            Set("bestSpot", IntVec3.Invalid);
            Set("bestSpotPref", 0.001f);
            AccessTools.Method(t, "EvaluateCell").Invoke(null, new object[] { c });
            IntVec3 best = (IntVec3)AccessTools.Field(t, "bestSpot").GetValue(null);
            float pref = best == c ? (float)AccessTools.Field(t, "bestSpotPref").GetValue(null) : -1f;
            Set("req", default(CastPositionRequest));
            Set("verb", null);
            return pref;
        }

        private static void GrG3(Map map)
        {
            GrScene(map, GrSeed + 7);
            List<string> parts = new List<string>();
            bool ok = true;
            AvoidGrid grid = null;
            try
            {
                Teleport(r2P, RG(30, 44));
                Draft(r2P, true);
                Pawn raider = MakeRaider(map, RG(26, 12));
                Verb v = raider.equipment.PrimaryEq.PrimaryVerb;
                IntVec3 tp = r2P.Position;
                IntVec3 c1 = tp + new IntVec3(-6, 0, -12), c2 = tp + new IntVec3(6, 0, -12), c3 = tp + new IntVec3(2, 0, -2), c4 = tp + new IntVec3(4, 0, 6);
                Thing bags = Place(ThingDef.Named("Sandbags"), ThingDefOf.Steel, c1 + new IntVec3(0, 0, 1), map);
                IntVec3 c5 = bags.Position;
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                grid = new AvoidGrid(map);
                AccessTools.Field(typeof(AvoidGrid), "gridDirty").SetValue(grid, false);
                AccessTools.Method(typeof(AvoidGrid), "IncrementAvoidGrid").Invoke(grid, new object[] { c2, 30 });
                AccessTools.Method(typeof(AvoidGrid), "IncrementAvoidGrid").Invoke(grid, new object[] { c3, 45 });
                foreach (bool cover in new[] { false, true })
                {
                    CastPositionRequest req = new CastPositionRequest { caster = raider, target = r2P, verb = v, maxRangeFromCaster = 500f, wantCoverFromTarget = cover };
                    // the game's own search state, from a real search
                    CastPositionFinder.TryFindCastPosition(req, out IntVec3 _);
                    float rft = (float)AccessTools.Field(typeof(CastPositionFinder), "rangeFromTarget").GetValue(null);
                    float rftSq = (float)AccessTools.Field(typeof(CastPositionFinder), "rangeFromTargetSquared").GetValue(null);
                    float optSq = (float)AccessTools.Field(typeof(CastPositionFinder), "optimalRangeSquared").GetValue(null);
                    CastSpots.PrefBegin(req, map);
                    try
                    {
                        bool st = rft == CastSpots.LastRangeFromTarget && rftSq == CastSpots.LastRangeFromTargetSq && optSq == CastSpots.LastOptimalSq;
                        ok &= st;
                        parts.Add($"cover {cover}: state game {rft:R}/{rftSq:R}/{optSq:R}, ours {CastSpots.LastRangeFromTarget:R}/{CastSpots.LastRangeFromTargetSq:R}/{CastSpots.LastOptimalSq:R} {(st ? "same" : "DIFF")}");
                        foreach ((string name, IntVec3 c) in new[] { ("cover", c1), ("open", c2), ("near target", c3), ("past target", c4), ("on sandbags", c5), ("caster's cell", raider.Position) })
                        {
                            foreach (AvoidGrid a in new[] { null, grid })
                            {
                                float game = GrVanillaPref(req, c, a, rft, rftSq, optSq);
                                float ours = CastSpots.Preference(c, a);
                                bool same = game < 0f || ours == game;   // (game < 0: the game refuses the cell for another reason, no preference to compare)
                                string fb = "";
                                ok &= same;
                                parts.Add($"{name}{(a != null ? " avoid" : "")}: game {game:R}, ours {ours:R}{fb}{(same ? "" : " DIFF")}");
                            }
                        }
                    }
                    finally
                    {
                        CastSpots.PrefEnd();
                    }
                }
                raider.Destroy();
            }
            catch (Exception e)
            {
                ok = false;
                parts.Add("error " + e);
            }
            finally
            {
                grid?.Dispose();
                Rand.PopState();
            }
            Result($"G3 parity: shooting-spot preference, vanilla CastPositionFinder (its search state and EvaluateCell) = CastSpots.Preference (one floor; the game's method through its copy: {CastSpots.UsesCopy})", ok && CastSpots.UsesCopy, string.Join("; ", parts));
            stage = "gr_g4";
        }

        // ---- G4: turret avoid grid parity

        private static void GrG4(Map map)
        {
            GrScene(map, GrSeed + 8);
            List<string> parts = new List<string>();
            bool ok = true;
            AvoidGrid grid = null;
            try
            {
                IntVec3 c = RG(30, 30);
                Building_TurretGun tur = (Building_TurretGun)Place(ThingDef.Named("Turret_MiniTurret"), null, c, map);
                for (int i = -3; i <= 3; i++)
                {
                    R2Wall(map, c + new IntVec3(i, 0, 6));
                    R2Wall(map, c + new IntVec3(8, 0, i));
                }
                Place(ThingDef.Named("Sandbags"), ThingDefOf.Steel, c + new IntVec3(-5, 0, -2), map);
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                grid = new AvoidGrid(map);
                AccessTools.Field(typeof(AvoidGrid), "gridDirty").SetValue(grid, false);
                AccessTools.Method(typeof(AvoidGrid), "PrintAvoidGridAroundTurret").Invoke(grid, new object[] { tur });
                Dictionary<IntVec3, int> ours = new Dictionary<IntVec3, int>();
                int ks = ProjectZ.FloorOf(tur.Position, map);
                AvoidGrid_Turret_Patch.Mark(tur, ks, map, (x, m) => ours[x] = (ours.TryGetValue(x, out int had) ? had : 0) + m);
                int r = Mathf.CeilToInt(tur.AttackVerb.verbProps.range) + 6, same = 0, diff = 0;
                string first = null;
                foreach (IntVec3 x in CellRect.CenteredOn(c, r))
                {
                    if (!x.InBounds(map) || ProjectZ.FloorOf(x, map) != ks)
                    {
                        continue;
                    }
                    int g = grid[x], o = Mathf.Min(255, ours.TryGetValue(x, out int v) ? v : 0);
                    if (g == o)
                    {
                        same += g > 0 ? 1 : 0;
                    }
                    else
                    {
                        diff++;
                        first = first ?? $"{x - c}: game {g}, ours {o}";
                    }
                }
                ok = diff == 0 && same > 0;
                parts.Add($"turret range {tur.AttackVerb.verbProps.range}, min {tur.AttackVerb.verbProps.EffectiveMinRange(true)}: cells marked alike {same}, different {diff}{(first != null ? ", first " + first : "")}");
            }
            catch (Exception e)
            {
                ok = false;
                parts.Add("error " + e);
            }
            finally
            {
                grid?.Dispose();
                Rand.PopState();
            }
            Result("G4 parity: turret avoid marks, vanilla AvoidGrid.PrintAvoidGridAroundTurret = AvoidGrid_Turret_Patch.Mark (its own floor)", ok, string.Join("; ", parts));
            stage = "gr_t0";
        }

        // ---- T0: frame time

        private static void GrT0(Map map)
        {
            GrScene(map, GrSeed + 9);
            try
            {
                IntVec3 c = RG(20, 30);
                R2Floor(map, new CellRect(c.x - 6, c.z - 4, 30, 9), 1);
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                Teleport(r2P, Levels.Cell(c, 1, map));
                Draft(r2P, true);
                r2P.equipment.DestroyAllEquipment();
                r2P.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(ThingDef.Named("Gun_AssaultRifle")));
                grT = Place(ThingDef.Named("Turret_MiniTurret"), null, Levels.Cell(c + new IntVec3(10, 0, 0), 1, map), map);
                BrView3D(true);
                ShotAt(map, 1, c + new IntVec3(6, 0, 0), 50f);
            }
            finally
            {
                Rand.PopState();
            }
            Find.Selector.Select(r2P, playSound: false, forceDesignatorDeselect: false);
            Find.Selector.Select(grT, playSound: false, forceDesignatorDeselect: false);
            Find.Targeter.BeginTargeting(r2P.equipment.PrimaryEq.PrimaryVerb);
            grFrames = 0;
            grFrameMs = 0f;
            grRingsAt = RangePreview.Rings3D;
            stage = "gr_t0_take";
        }

        private static void GrT0Take(Map map)
        {
            grFrames++;
            if (grFrames > 20)
            {
                grFrameMs += Time.unscaledDeltaTime * 1000f;
            }
            if (grFrames < 140)
            {
                return;
            }
            int rings = RangePreview.Rings3D - grRingsAt;
            Info($"T0 frame time with a drafted rifleman targeting and a turret selected (3D view, floor 1 viewed): {grFrameMs / 120f:0.00} ms a frame over 120 frames; 3D rings added {rings}");
            Find.Targeter.StopTargeting();
            Find.Selector.ClearSelection();
            stage = "gr_done";
        }

        private static void GrDone(Map map)
        {
            grModHarmony?.UnpatchAll("Peren.IsoRimProjectZ.test.gamerangemod");
            Finish();
        }
    }
}
