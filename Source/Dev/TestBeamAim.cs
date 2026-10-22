using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // Mode beamaim (2026-10-22; run with -projectzfloors=3 -projectzseed=7): the harness-3 error "Turning a pawn
    // sprite's facing with the camera failed" (out of range in Verb_ShootBeam.get_AimAngleOverride under AimFloor). The
    // beam's aim getter is read while pawns are drawn, on the game's parallel render workers, and AimFloor's scope stack
    // was one list for every thread. A real, seeded fight of beam-graser users (hostile tesserons in DefendPoint lords,
    // drafted colonists firing at will with grasers) in three phases, each with its own seed and a cleared field:
    //   BMX cross: colonists on a floor-2 platform, mechs on the ground south of it (every beam between floors)
    //   BMS same:  everyone on the ground (no AimFloor scope opened: the game's own code)
    //   BMM mixed: mechs and colonists on both floors
    // The camera watches the fight at Normal speed so the pawns are drawn (render workers) every frame. Damage to the
    // fighters is absorbed (test only) so the fight lasts. A test postfix on the getter compares every value with the
    // angle worked out here from the beam's point and the caster's column on the target's floor (cross) or its own
    // position (same), per thread. Checks per phase: beam bursts seen, getter reads on render workers, every read equal
    // to the expected angle (0.5 deg), no error logged. Screenshots of beams in mid sweep.
    public static partial class TestRunner
    {
        private sealed class BmPhase
        {
            public string name, code;
            public int ticks;
            public IntVec2[] cols;   // colonist cells (x, z) in the area; level from colsUp
            public bool[] colsUp;
            public IntVec2[] mechs;
            public bool[] mechsUp;
        }

        private static readonly BmPhase[] bmPhases =
        {
            new BmPhase
            {
                name = "cross", code = "BMX", ticks = 1800,
                cols = new[] { new IntVec2(18, 17), new IntVec2(26, 17) }, colsUp = new[] { true, true },
                mechs = new[] { new IntVec2(12, 6), new IntVec2(20, 4), new IntVec2(28, 5), new IntVec2(35, 8) }, mechsUp = new[] { false, false, false, false },
            },
            new BmPhase
            {
                name = "crowd", code = "BMC", ticks = 2400,
                cols = new[] { new IntVec2(18, 17), new IntVec2(26, 17) }, colsUp = new[] { true, true },
                mechs = new[] { new IntVec2(6, 8), new IntVec2(10, 4), new IntVec2(14, 7), new IntVec2(18, 3), new IntVec2(22, 6), new IntVec2(26, 3), new IntVec2(30, 7), new IntVec2(34, 4), new IntVec2(38, 8), new IntVec2(42, 5) },
                mechsUp = new[] { false, false, false, false, false, false, false, false, false, false },
            },
            new BmPhase
            {
                name = "same", code = "BMS", ticks = 900,
                cols = new[] { new IntVec2(18, 26), new IntVec2(26, 26) }, colsUp = new[] { false, false },
                mechs = new[] { new IntVec2(12, 6), new IntVec2(20, 4), new IntVec2(28, 5), new IntVec2(35, 8) }, mechsUp = new[] { false, false, false, false },
            },
            new BmPhase
            {
                name = "mixed", code = "BMM", ticks = 2400,
                cols = new[] { new IntVec2(18, 17), new IntVec2(8, 12) }, colsUp = new[] { true, false },
                mechs = new[] { new IntVec2(27, 23), new IntVec2(22, 4), new IntVec2(34, 9), new IntVec2(14, 4) }, mechsUp = new[] { true, false, false, false },
            },
        };

        // the platform (level 1) over these ground cells of the area
        private static CellRect BmPlatform => new CellRect(r2Area.minX + 15, r2Area.minZ + 17, 15, 8);

        private static int bmI, bmT0, bmErr0, bmScopes0, bmShots, bmFrames;
        private static int bmBursts, bmBurstsCross;
        private static readonly List<Pawn> bmMechs = new List<Pawn>();
        private static readonly List<Pawn> bmCols = new List<Pawn>();
        private static HashSet<Pawn> bmFighters = new HashSet<Pawn>();
        private static Harmony bmHarmony;
        private static readonly Dictionary<Pawn, bool> bmWasBursting = new Dictionary<Pawn, bool>();

        // getter reads (any thread): [main/worker][same/cross]; reads off by more than 0.5 deg; values the check could
        // not work out (the game's own path list changed under it)
        private static int bmMainSame, bmMainCross, bmWorkSame, bmWorkCross, bmOff, bmUnknown;
        private static string bmLastOff;

        private static bool StepBeamAim(Map map)
        {
            switch (stage)
            {
                case "bm_setup":
                {
                    DebugSettings.enableRandomMentalStates = false;
                    NoRoofCollapse(map);
                    Find.Storyteller.storytellerComps.Clear();
                    NoAmbushes(map);
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
                    var cols = Colonists(map).Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)).OrderBy(p => p.thingIDNumber).ToList();
                    if (cols.Count < 2)
                    {
                        cols = Colonists(map).OrderBy(p => p.thingIDNumber).ToList();
                    }
                    bmCols.Clear();
                    bmCols.AddRange(cols.Take(2));
                    r2P = bmCols[0];
                    r2Q = bmCols.Count > 1 ? bmCols[1] : null;
                    bmHarmony = bmHarmony ?? new Harmony("Peren.IsoRimProjectZ.test.beamaim");
                    bmHarmony.Patch(AccessTools.PropertyGetter(typeof(Verb_ShootBeam), nameof(Verb_ShootBeam.AimAngleOverride)),
                        postfix: new HarmonyMethod(typeof(TestRunner), nameof(BmAimRead)) { priority = Priority.Last });
                    bmHarmony.Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.PreApplyDamage)),
                        postfix: new HarmonyMethod(typeof(TestRunner), nameof(BmAbsorb)));
                    IsoRimBridge.SetOption("freeYaw", 0f);
                    Info($"beamaim: area {r2Area}, platform {BmPlatform} (level 1), colonists {string.Join(", ", bmCols)}, IsoRim {IsoRimBridge.Present} (Api {IsoRimBridge.ApiVersion}), aim on the target's floor {(Combat.AimFloor.Off ? "OFF" : "on")}, seed {Seed}");
                    bmI = 0;
                    stage = "bm_fixture";
                    return true;
                }
                case "bm_fixture":
                    BmFixture(map);
                    return true;
                case "bm_run":
                    BmRun(map);
                    return true;
                case "bm_end":
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    bmHarmony.UnpatchAll(bmHarmony.Id);
                    FloorView.Set(0);
                    Finish();
                    return true;
            }
            return false;
        }

        private static BmPhase BmNow => bmPhases[bmI];

        private static IntVec3 BmCell(IntVec2 p, bool up, Map map) => up ? RL(p.x, p.z, 1, map) : RG(p.x, p.z);

        private static void BmFixture(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            BmPhase ph = BmNow;
            foreach (Pawn m in bmMechs)
            {
                if (!m.Destroyed)
                {
                    m.Destroy();
                }
            }
            bmMechs.Clear();
            R2Clear(map);
            R2Park(map, bmCols.ToArray());
            R2Feed(map);
            R2Floor(map, BmPlatform, 1);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            // this phase's own random stream (as Rand.Seed would set it, without its "initial seed" error)
            AccessTools.Field(typeof(Rand), "seed").SetValue(null, StableHash("beamaim_" + ph.name) ^ (uint)Math.Max(0, Seed));
            AccessTools.Field(typeof(Rand), "iterations").SetValue(null, 0u);
            ThingDef graser = DefDatabase<ThingDef>.GetNamed("Gun_BeamGraser");
            for (int i = 0; i < bmCols.Count; i++)
            {
                Pawn c = bmCols[i];
                Teleport(c, BmCell(ph.cols[i], ph.colsUp[i], map));
                c.equipment.DestroyAllEquipment();
                c.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(graser));
                Draft(c, true);
                c.drafter.FireAtWill = true;
            }
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamed("Mech_Tesseron");
            for (int i = 0; i < ph.mechs.Length; i++)
            {
                Pawn m = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfMechanoids, forceGenerateNewPawn: true));
                IntVec3 at = BmCell(ph.mechs[i], ph.mechsUp[i], map);
                if (m.equipment.Primary?.def != graser)
                {
                    m.equipment.DestroyAllEquipment();
                    m.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(graser));
                }
                GenSpawn.Spawn(m, at, map);
                LordMaker.MakeNewLord(Faction.OfMechanoids, new LordJob_DefendPoint(at, 0f, 2f, addFleeToil: false), map, new[] { m });
                bmMechs.Add(m);
            }
            bmFighters = new HashSet<Pawn>(bmMechs.Concat(bmCols));
            foreach (Pawn p in bmFighters)
            {
                Info($"{ph.code} {ph.name}: {p} ({p.Faction?.Name}) at {p.Position} level {Levels.LevelOf(p.Position, map)} with {p.equipment?.Primary?.def.defName ?? "-"}");
            }
            // the camera on the fight, floor 2 viewed (the ground below drawn too)
            IntVec3 mid = RG(22, 13);
            ShotAt(map, ph.colsUp.Any(u => u) || ph.mechsUp.Any(u => u) ? 1 : 0, mid, ph.name == "crowd" ? 16f : 11f);
            bmErr0 = errors;
            bmScopes0 = Combat.AimFloor.Scopes;
            bmMainSame = bmMainCross = bmWorkSame = bmWorkCross = bmOff = bmUnknown = 0;
            bmLastOff = null;
            bmBursts = bmBurstsCross = bmShots = bmFrames = 0;
            bmWasBursting.Clear();
            bmT0 = Find.TickManager.TicksGame;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            Wait(5, "bm_run");
        }

        private static bool BmCross(Verb v, Map map)
        {
            LocalTargetInfo t = v.CurrentTarget;
            return v.caster != null && t.IsValid && t.Cell.InBounds(map) && Combat.Heights.CrossFloor(v.caster.Position, t.Cell, map);
        }

        private static void BmRun(Map map)
        {
            BmPhase ph = BmNow;
            bmFrames++;
            // fires on the field go out (the field stays the same for the whole phase)
            if (bmFrames % 30 == 0)
            {
                foreach (Thing f in map.listerThings.ThingsInGroup(ThingRequestGroup.Fire).ToList())
                {
                    f.Destroy();
                }
            }
            foreach (Pawn p in bmFighters)
            {
                if (!(p.Spawned && p.CurrentEffectiveVerb is Verb_ShootBeam v))
                {
                    continue;
                }
                bool b = v.state == VerbState.Bursting;
                bmWasBursting.TryGetValue(p, out bool was);
                if (b && !was)
                {
                    bmBursts++;
                    bool cross = BmCross(v, map);
                    if (cross)
                    {
                        bmBurstsCross++;
                    }
                    // a beam in mid sweep: a few screenshots per phase
                    if (bmShots < 3 && (cross || ph.name == "same") && Find.TickManager.TicksGame - bmT0 > 200 * (bmShots + 1))
                    {
                        string name = $"bm_{ph.name}_{bmShots}";
                        Info($"{name}: {p} at {p.Position} (level {Levels.LevelOf(p.Position, map)}) beam at {v.CurrentTarget.Cell} (level {Levels.LevelOf(v.CurrentTarget.Cell, map)}), cross {cross}");
                        bmShots++;
                        Capture(name);
                    }
                }
                bmWasBursting[p] = b;
            }
            if (Find.TickManager.TicksGame - bmT0 < ph.ticks)
            {
                return;
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            int err = errors - bmErr0;
            int scopes = Combat.AimFloor.Scopes - bmScopes0;
            string got = $"ticks {Find.TickManager.TicksGame - bmT0}, frames {bmFrames}, bursts {bmBursts} (between floors {bmBurstsCross}), getter reads main thread same/cross {bmMainSame}/{bmMainCross}, render workers same/cross {bmWorkSame}/{bmWorkCross}, off by > 0.5 deg {bmOff}{(bmLastOff != null ? " (last: " + bmLastOff + ")" : "")}, not checkable {bmUnknown}, AimFloor scopes {scopes}, errors {err}";
            Info($"{ph.code} {ph.name}: {got}");
            if (ph.name == "same")
            {
                Result($"{ph.code} one floor: beams fired, read on render workers, no AimFloor scope (the game's own aim), equal to the expected angle, no error",
                    bmBursts >= 5 && bmWorkSame > 50 && bmWorkCross == 0 && scopes == 0 && bmOff == 0 && err == 0, got);
            }
            else if (ph.name == "cross" || ph.name == "crowd")
            {
                Result($"{ph.code} between floors: beams fired between floors, read on render workers, every read aims from the caster's column on the target's floor, no error",
                    bmBurstsCross >= 5 && bmWorkCross > 50 && bmOff == 0 && err == 0, got);
            }
            else
            {
                Result($"{ph.code} mixed floors: beams on one floor and between floors, read on render workers, all as expected, no error",
                    bmBurstsCross >= 3 && bmBursts > bmBurstsCross && bmWorkCross > 50 && bmWorkSame > 50 && bmOff == 0 && err == 0, got);
            }
            bmI++;
            Wait(10, bmI < bmPhases.Length ? "bm_fixture" : "bm_end");
        }

        // (test postfix, any thread, inside AimFloor's scope) the getter's value against the angle worked out here: from
        // the beam's point to the caster, the caster moved to its column on the target's floor when the target is there
        public static void BmAimRead(Verb_ShootBeam __instance, float? __result)
        {
            if (!__result.HasValue || !(__instance.caster is Pawn p) || !bmFighters.Contains(p) || !p.Spawned)
            {
                return;
            }
            bool main = UnityData.IsInMainThread;
            Map map = p.Map;
            bool cross = BmCross(__instance, map);
            if (main && cross) Interlocked.Increment(ref bmMainCross);
            else if (main) Interlocked.Increment(ref bmMainSame);
            else if (cross) Interlocked.Increment(ref bmWorkCross);
            else Interlocked.Increment(ref bmWorkSame);
            float want;
            try
            {
                Vector3 from = p.DrawPos;
                if (cross)
                {
                    IntVec3 virt = Combat.Heights.OnFloorOf(p.Position, __instance.CurrentTarget.Cell, map);
                    from += (virt - p.Position).ToVector3();
                }
                want = (__instance.InterpolatedPosition - from).AngleFlat();
            }
            catch (Exception)
            {
                Interlocked.Increment(ref bmUnknown);
                return;
            }
            float d = Mathf.Abs(Mathf.DeltaAngle(__result.Value, want));
            if (d > 0.5f)
            {
                Interlocked.Increment(ref bmOff);
                bmLastOff = $"{p} {(main ? "main" : "worker")} {(cross ? "cross" : "same")}: {__result.Value:0.0} vs {want:0.0}";
            }
        }

        // (test postfix) the fighters take no damage, so the fight lasts
        public static void BmAbsorb(Pawn __instance, ref bool absorbed)
        {
            if (bmFighters.Contains(__instance))
            {
                absorbed = true;
            }
        }
    }
}
