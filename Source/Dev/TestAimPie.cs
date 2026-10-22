using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode aimpie (bugs3fix 2026-10-21): the aim pie of a selected pawn warming up (Stance_Warmup.StanceDraw ->
    // GenDraw.DrawAimPie), in real time (Normal speed, the camera on the shooter, a frame per tick or so). With IsoRim
    // (Api 19), a 3D figure aiming a gun shows the pie at its muzzle, stood up facing the camera, its wedge toward the
    // target as seen; everything else keeps the game's pie on the ground (ProjectZ turns that one toward a target on
    // another floor: AimFloor.AimPie). Each case orders a drafted colonist to force-attack a wall (or cast an ability at
    // it), watches the warm-up frame by frame and stops the job just before the shot (nothing is hit or burnt):
    //   APM  a rifle, then a pistol, at walls in several directions and from the camera's 4 yaws: a muzzle pie on (nearly)
    //        every warm-up frame, its wedge within 15 deg of the target's direction on screen (Camera.WorldToScreenPoint
    //        of shooter and target), shrinking as the warm-up runs (the game's own pie size)
    //   APU  the same, not selected: no pie at all (the game draws it only for a selected pawn)
    //   APX  from a level-1 platform at a wall on the ground: a muzzle pie toward the target as seen (down and across)
    //   APA  an ability (fire spew, aimed at a cell): no muzzle pie (the game's pie on the ground)
    //   APM  also a mechanoid (militor, hostile, selected) shooting the wall: at its muzzle when drawn as a model
    //   APC  2D cards (IsoRim's 3D pawns off): no muzzle pie; from level 1 at the ground the game's pie turned by ProjectZ
    //        toward the target's ground cell (within 10 deg)
    //   APR  roped animals (one roped to the colonist, one to a spot): their rope lines are drawn (Pawn_RopeTracker.
    //        RopingDraw runs every frame for the 3D animal)
    // Screenshots at about half the warm-up.
    public static partial class TestRunner
    {
        private sealed class ApCase
        {
            public string name, gun;
            public int dx, dz;        // the target wall, from the shooter
            public float yaw;         // the camera's turn
            public bool upper, cards, ability, unselected;
            public string mech;       // a hostile mechanoid of this kind shoots instead (selected)
        }

        private static readonly ApCase[] apCases =
        {
            new ApCase { name = "rifle_e", gun = "Gun_BoltActionRifle", dx = 9, dz = 0 },
            new ApCase { name = "rifle_ne", gun = "Gun_BoltActionRifle", dx = 4, dz = 8 },
            new ApCase { name = "rifle_w", gun = "Gun_BoltActionRifle", dx = -8, dz = 4 },
            new ApCase { name = "rifle_s", gun = "Gun_BoltActionRifle", dx = 3, dz = -8 },
            new ApCase { name = "rifle_e_yaw90", gun = "Gun_BoltActionRifle", dx = 9, dz = 0, yaw = 90f },
            new ApCase { name = "rifle_e_yaw180", gun = "Gun_BoltActionRifle", dx = 9, dz = 0, yaw = 180f },
            new ApCase { name = "rifle_e_yaw270", gun = "Gun_BoltActionRifle", dx = 9, dz = 0, yaw = 270f },
            new ApCase { name = "pistol_ne", gun = "Gun_Autopistol", dx = 6, dz = 6 },
            new ApCase { name = "pistol_sw_yaw90", gun = "Gun_Autopistol", dx = -6, dz = -5, yaw = 90f },
            new ApCase { name = "rifle_unselected", gun = "Gun_BoltActionRifle", dx = 9, dz = 0, unselected = true },
            new ApCase { name = "rifle_upper", gun = "Gun_BoltActionRifle", dx = 9, dz = 0, upper = true },
            new ApCase { name = "rifle_upper_yaw90", gun = "Gun_BoltActionRifle", dx = 9, dz = 3, upper = true, yaw = 90f },
            new ApCase { name = "ability", gun = "Gun_BoltActionRifle", dx = 7, dz = 2, ability = true },
            new ApCase { name = "cards", gun = "Gun_BoltActionRifle", dx = 9, dz = 0, cards = true },
            new ApCase { name = "cards_upper", gun = "Gun_BoltActionRifle", dx = 9, dz = 0, cards = true, upper = true },
            new ApCase { name = "mech", gun = "-", dx = 9, dz = 2, mech = "Mech_Militor" },
        };

        private static int apI, apT0, apWarmFrames, apPieFrames, apLastCount, apCount0, apFloorPies0, apWrongAngle, apGrow;
        private static float apOldYaw, apFirstDeg, apLastDeg, apMaxDiff, apExpect, apSeenAngle;
        private static bool apShot, apWarm, apEnded;
        private static Thing apWall;
        private static Pawn apShooter;
        private static IntVec3 apTarget;
        private static Pawn apRopedA, apRopedB;
        private static int apRopeA, apRopeB, apRopeFrames;

        private static bool StepAimPie(Map map)
        {
            switch (stage)
            {
                case "ap_setup":
                {
                    DebugSettings.enableRandomMentalStates = false;
                    NoRoofCollapse(map);
                    Find.Storyteller.storytellerComps.Clear();
                    NoAmbushes(map);
                    foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
                    {
                        a.Destroy();
                    }
                    // the area near the middle without things that cannot be cleared away (a steam geyser)
                    r2Area = new CellRect(map.Center.x - 22, map.Center.z - 16, 44, 32);
                    foreach (IntVec3 o in GenRadial.RadialPattern.Take(400))
                    {
                        CellRect r = new CellRect(map.Center.x - 22 + o.x * 6, map.Center.z - 16 + o.z * 6, 44, 32);
                        if (r.ExpandedBy(3).InBounds(map) && !r.ExpandedBy(3).Cells.Any(c => c.GetThingList(map).Any(t => !t.def.destroyable)))
                        {
                            r2Area = r;
                            break;
                        }
                    }
                    var cols = Colonists(map);
                    r2P = cols.OrderBy(p => p.thingIDNumber).FirstOrDefault(p => !p.WorkTagIsDisabled(WorkTags.Violent)) ?? cols.First();
                    r2Q = cols.FirstOrDefault(p => p != r2P);
                    apOldYaw = (float)(IsoRimBridge.SetOption("freeYaw", 0f) ?? 0f);
                    Info($"aimpie: shooter {r2P}, IsoRim present {IsoRimBridge.Present} (Api {IsoRimBridge.ApiVersion})");
                    apI = 0;
                    stage = "ap_fixture";
                    return true;
                }
                case "ap_fixture":
                    ApFixture(map);
                    return true;
                case "ap_go":
                    ApGo(map);
                    return true;
                case "ap_run":
                    ApRun(map);
                    return true;
                case "ap_rope":
                    ApRope(map);
                    return true;
                case "ap_rope_run":
                    ApRopeRun(map);
                    return true;
                case "ap_end":
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    IsoRimBridge.SetOption("freeYaw", apOldYaw);
                    IsoRimBridge.SetOption("pawns3D", true);
                    FloorView.Set(0);
                    Finish();
                    return true;
            }
            return false;
        }

        private static ApCase ApNow => apCases[apI];

        private static void ApFixture(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            ApCase c = ApNow;
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            IsoRimBridge.SetOption("pawns3D", !c.cards);
            IntVec3 at = RG(12, 13);
            if (c.upper)
            {
                R2Floor(map, new CellRect(RG(10, 11).x, RG(10, 11).z, 5, 5), 1);
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                at = RL(12, 13, 1, map);
            }
            apTarget = RG(12 + c.dx, 13 + c.dz);
            apWall = R2Wall(map, apTarget);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            apShooter = r2P;
            if (c.mech != null)
            {
                Teleport(r2P, RG(12, 4));
                apShooter = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed(c.mech), Faction.OfMechanoids, forceGenerateNewPawn: true));
                GenSpawn.Spawn(apShooter, at, map);
            }
            else
            {
                Teleport(r2P, at);
                r2P.equipment.DestroyAllEquipment();
                r2P.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(c.gun)));
                Draft(r2P, true);
            }
            IsoRimBridge.SetOption("freeYaw", c.yaw);
            ShotAt(map, c.upper ? 1 : 0, RG(12 + c.dx / 3, 13 + c.dz / 3), 7f);
            Wait(120, "ap_go");   // (the camera turns over some frames)
        }

        private static void ApGo(Map map)
        {
            ApCase c = ApNow;
            if (!c.unselected)
            {
                Find.Selector.Select(apShooter, playSound: false, forceDesignatorDeselect: false);
            }
            Job job;
            if (c.ability)
            {
                AbilityDef def = DefDatabase<AbilityDef>.GetNamedSilentFail("FireSpew");
                if (def != null && r2P.abilities.GetAbility(def) == null)
                {
                    r2P.abilities.GainAbility(def);
                }
                Ability ab = def != null ? r2P.abilities.GetAbility(def) : null;
                job = ab?.GetJob(apTarget, apTarget);
            }
            else
            {
                job = JobMaker.MakeJob(JobDefOf.AttackStatic, apWall);
            }
            if (job != null && c.mech != null)
            {
                apShooter.jobs.StartJob(job, JobCondition.InterruptForced);
            }
            else if (job != null)
            {
                job.playerForced = true;
                r2P.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder);
            }
            Info($"AP {c.name}: {apShooter} at {apShooter.Position} (level {Levels.LevelOf(apShooter.Position, map)}) with {apShooter.equipment?.Primary?.def.defName ?? "-"}{(c.ability ? " casting FireSpew" : "")} at {apWall} {apTarget}, camera yaw {c.yaw}, cards {c.cards}, selected {Find.Selector.IsSelected(apShooter)}, drawn as a model {IsoRimBridge.DrawnYaw(apShooter, out _).HasValue}, job {job?.def.defName ?? "none"}");
            apT0 = Find.TickManager.TicksGame;
            apWarmFrames = apPieFrames = apWrongAngle = apGrow = 0;
            apFirstDeg = apLastDeg = -1f;
            apMaxDiff = 0f;
            apExpect = apSeenAngle = float.NaN;
            apShot = apWarm = apEnded = false;
            apLastCount = apCount0 = IsoRimBridge.MuzzlePieCount;
            apFloorPies0 = Combat.AimFloor.Pies;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            stage = "ap_run";
        }

        // The target's direction on screen from the shooter (degrees clockwise from up the screen).
        private static float ApScreenAngle(Thing target)
        {
            Camera cam = Find.Camera;
            Vector3 s = cam.WorldToScreenPoint(apShooter.DrawPos);
            Vector3 t = cam.WorldToScreenPoint(target.DrawPos);
            return Mathf.Atan2(t.x - s.x, t.y - s.y) * Mathf.Rad2Deg;
        }

        private static void ApRun(Map map)
        {
            ApCase c = ApNow;
            Stance_Warmup w = apShooter.stances.curStance as Stance_Warmup;
            int count = IsoRimBridge.MuzzlePieCount;
            if (w != null)
            {
                apWarm = true;
                apWarmFrames++;
                if (count > apLastCount)
                {
                    apPieFrames++;
                    float deg = IsoRimBridge.MuzzlePieDegrees;
                    if (apFirstDeg < 0f)
                    {
                        apFirstDeg = deg;
                    }
                    else if (deg > apLastDeg + 0.5f)
                    {
                        apGrow++;
                    }
                    apLastDeg = deg;
                    float want = ApScreenAngle(apWall);
                    float d = Mathf.Abs(Mathf.DeltaAngle(IsoRimBridge.MuzzlePieAngle, want));
                    apMaxDiff = Mathf.Max(apMaxDiff, d);
                    if (d > 15f)
                    {
                        apWrongAngle++;
                    }
                    apExpect = want;
                    apSeenAngle = IsoRimBridge.MuzzlePieAngle;
                }
                if (!apShot && apWarmFrames >= 12)
                {
                    apShot = true;
                    Info($"ap_{c.name}: warm-up {w.ticksLeft} ticks left, verb {w.verb?.GetType().Name}, muzzle pie angle {apSeenAngle:0} (target on screen {apExpect:0}), degrees {apLastDeg:0}");
                    Capture("ap_" + c.name);
                }
                if (w.ticksLeft <= 6 && apShot)
                {
                    apEnded = true;   // stopped before the shot: nothing hit, nothing burnt
                }
            }
            apLastCount = count;
            if (!apEnded && Find.TickManager.TicksGame - apT0 < 600)
            {
                return;
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            apShooter.jobs.StopAll();
            apShooter.stances.CancelBusyStanceHard();
            int muzzle = count - apCount0;
            int floorPies = Combat.AimFloor.Pies - apFloorPies0;
            string got = $"warm-up frames {apWarmFrames}, frames with a muzzle pie {apPieFrames} (pies {muzzle}), angle off max {apMaxDiff:0.0} (frames off >15deg {apWrongAngle}; last pie {apSeenAngle:0} vs target {apExpect:0} on screen), pie degrees {apFirstDeg:0} -> {apLastDeg:0} (grew {apGrow} times), ProjectZ floor pies {floorPies} (last facing {Combat.AimFloor.LastPieFacing:0})";
            if (c.unselected)
            {
                Result($"APU {c.name}: not selected, no pie", apWarm && apWarmFrames > 10 && muzzle == 0 && floorPies == 0, got);
            }
            else if (c.ability)
            {
                Result($"APA {c.name}: an ability's warm-up keeps the game's pie (none at a muzzle)", apWarm && apWarmFrames > 10 && muzzle == 0, got);
            }
            else if (c.cards)
            {
                bool ok = apWarm && apWarmFrames > 10 && muzzle == 0;
                string extra = "";
                if (c.upper)
                {
                    IntVec3 from = ProjectZ.GroundCellOf(apShooter.Position, map);
                    float want = (apTarget - from).AngleFlat;
                    float d = Mathf.Abs(Mathf.DeltaAngle(Combat.AimFloor.LastPieFacing, want));
                    ok = ok && floorPies > 10 && d <= 10f;
                    extra = $"; the game's pie turned toward the ground cell: facing {Combat.AimFloor.LastPieFacing:0} vs {want:0} (off {d:0.0})";
                }
                Result($"APC {c.name}: 2D cards keep the game's pie{(c.upper ? ", turned toward the target on the ground" : "")}", ok, got + extra);
            }
            else if (c.mech != null && !IsoRimBridge.DrawnYaw(apShooter, out _).HasValue)
            {
                Result($"APM {c.name}: a mechanoid drawn as a sprite keeps the game's pie", apWarm && apWarmFrames > 10 && muzzle == 0, got);
            }
            else
            {
                Result($"AP{(c.upper ? "X" : "M")} {c.name}: the pie at the muzzle, toward the target on screen, shrinking with the warm-up",
                    apWarm && apWarmFrames > 10 && apPieFrames >= apWarmFrames * 0.8f && apWrongAngle == 0 && apGrow == 0 && apLastDeg < apFirstDeg && floorPies == 0, got);
            }
            if (apShooter != r2P && !apShooter.Destroyed)
            {
                apShooter.Destroy();
            }
            apI++;
            Wait(10, apI < apCases.Length ? "ap_fixture" : "ap_rope");
        }

        // ---- APR: roped animals

        private static void ApRope(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            IsoRimBridge.SetOption("pawns3D", true);
            IsoRimBridge.SetOption("freeYaw", 0f);
            Teleport(r2P, RG(12, 13));
            Draft(r2P, true);
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamed("Muffalo");
            apRopedA = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfPlayer, forceGenerateNewPawn: true));
            GenSpawn.Spawn(apRopedA, RG(14, 13), map);
            apRopedB = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfPlayer, forceGenerateNewPawn: true));
            GenSpawn.Spawn(apRopedB, RG(14, 10), map);
            r2P.roping.RopePawn(apRopedA);
            apRopedB.roping.RopeToSpot(RG(17, 10));
            apRopeA = apRopeB = apRopeFrames = 0;
            ropeWatch = new HashSet<Pawn> { apRopedA, apRopedB };
            ropeCalls.Clear();
            ropeHarmony = ropeHarmony ?? new Harmony("Peren.IsoRimProjectZ.test.aimpie");
            ropeHarmony.Patch(AccessTools.Method(typeof(Pawn_RopeTracker), nameof(Pawn_RopeTracker.RopingDraw)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(ApRopingDrawn)));
            ShotAt(map, 0, RG(14, 12), 6f);
            Info($"APR: {apRopedA} roped to {r2P} (roped {apRopedA.roping.IsRoped}), {apRopedB} roped to the spot {RG(17, 10)} (roped {apRopedB.roping.IsRoped})");
            apT0 = Find.TickManager.TicksGame;
            apShot = false;
            Wait(30, "ap_rope_run");
        }

        private static Harmony ropeHarmony;
        private static HashSet<Pawn> ropeWatch;
        private static readonly Dictionary<Pawn, int> ropeCalls = new Dictionary<Pawn, int>();

        public static void ApRopingDrawn(Pawn ___pawn)
        {
            Pawn p = ___pawn;
            if (p != null && ropeWatch != null && ropeWatch.Contains(p))
            {
                ropeCalls[p] = (ropeCalls.TryGetValue(p, out int n) ? n : 0) + 1;
            }
        }

        private static void ApRopeRun(Map map)
        {
            apRopeFrames++;
            if (apRopeFrames == 30)
            {
                Capture("ap_roped");
            }
            if (apRopeFrames < 90)
            {
                return;
            }
            ropeHarmony.Unpatch(AccessTools.Method(typeof(Pawn_RopeTracker), nameof(Pawn_RopeTracker.RopingDraw)), HarmonyPatchType.Postfix, ropeHarmony.Id);
            int a = ropeCalls.TryGetValue(apRopedA, out int na) ? na : 0;
            int b = ropeCalls.TryGetValue(apRopedB, out int nb) ? nb : 0;
            float? ya = IsoRimBridge.DrawnYaw(apRopedA, out int ageA);
            Result("APR roped animals drawn as models still draw their rope lines (to the colonist, to a spot)",
                a >= 80 && b >= 80 && ya.HasValue && ageA <= 1 && apRopedA.roping.IsRoped && apRopedB.roping.IsRoped,
                $"rope draws over 90 frames: roped to the colonist {a}, to the spot {b}; drawn as a model {ya.HasValue} (frames ago {ageA})");
            r2P.roping.DropRope(apRopedA);
            apRopedB.roping.BreakAllRopes();
            apRopedA.Destroy();
            apRopedB.Destroy();
            Wait(3, "ap_end");
        }
    }
}
