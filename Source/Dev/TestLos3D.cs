using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using ProjectZ.Combat;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // Mode los3d (line of sight between floors, Los3D; run with -projectzfloors=3 -projectzseed=N). One cleared field;
    // every check starts from a fresh fixture (the field wiped on all floors, then built for that check alone) and its own
    // random state (the seed and the check's name). Shots are real: orders through the player's float menu (the drafted
    // attack option), the pawns' own fire at will and AI, real ticks; facing and the drawn lean at normal game speed.
    //   walk: Los3D's cell walk equals the game's GenSight walk (5000 random pairs, half offsets included).
    //   parity: one random wall layout on the ground and on floor 2: can hit and can see identical for 1500 pairs, and
    //     Los3D never called for them (one floor is always the game's own code).
    //   sealed: a ground room under a slab vs every slab cell, both ways; doors: a closed door blocks, an open one doesn't.
    //   down / up / parapet / corner: the plan's sheet cases as real shots (or refusals with "no line of sight").
    //   batty: the user's case (1-2 cells in from a slab corner, a wall W/E/N, a raider on the ground SW): must shoot,
    //     facing west while aiming; then a target hidden under the slab: the pawn keeps facing it (not south).
    //   waitface: an undrafted raider holding an attack on a hidden colonist upstairs faces it where it really is.
    //   ai: a raider on its own AI shoots a colonist in the 2nd row upstairs.
    //   turrets, smoke: turret sight both ways, blind smoke on the line (target finder, the shot report's 70%).
    //   ui: the shot preview's "No line of sight" and the order refusal (blocked between floors and on one floor; not
    //     for out-of-range or clear shots), drawn into shots.
    //   perf: per-call times; a fight between floors (and the same fight on the ground) timed per tick.
    // Aimed shots between floors along a clear line never stop on a slab (counted over every shot of the run). Shots
    // frozen mid-flight at yaw 45 and 225.
    public static partial class TestRunner
    {
        // ---- seed (-projectzseed=N): the world/map setup and each check's random state
        internal static int Seed = -1;

        private static uint StableHash(string s)
        {
            uint h = 2166136261u;
            foreach (char c in s)
            {
                h ^= c;
                h *= 16777619u;
            }
            return h;
        }

        // A check's fixture is built under its own random state (the seed and its name). The game empties the random state
        // stack every frame, so it covers the check's setup only; the ticks after it draw from the game's own stream.
        private static void Seeded(string name, Action run)
        {
            Rand.PushState((int)(StableHash(name) ^ (uint)Math.Max(0, Seed)));
            try
            {
                run();
            }
            finally
            {
                Rand.PopState();
            }
        }

        // ---- shared shot records (also used by m1c)
        private static Pawn mA, mB, mShooter, mTarget;
        private static readonly List<Pawn> mRaiders = new List<Pawn>();
        private static IntVec3 mHut = IntVec3.Invalid;
        private static int mFrame, mLaunchesAt;
        private static Projectile mProj;
        private static ShootLine mLine;
        private static float mOldYaw, mOldMin;
        private static bool mPatched;
        private static readonly Dictionary<int, IntVec3> mSpawnCell = new Dictionary<int, IntVec3>();
        private static readonly Dictionary<int, string> mImpact = new Dictionary<int, string>();
        private static readonly List<string> mLaunchers = new List<string>();
        private static readonly List<string> mLaunchLog = new List<string>();
        private static readonly List<(Thing launcher, IntVec3 at, IntVec3 target)> mLaunchRecs = new List<(Thing, IntVec3, IntVec3)>();

        private sealed class LImp
        {
            public Thing launcher;
            public IntVec3 g;
            public int floor;
            public Thing hit;
            public bool slab, clear, cross, aimed;
        }

        private static readonly List<LImp> lImps = new List<LImp>();
        private static int lSlabSeen;

        private static void MLaunchPrefix(Projectile __instance, Thing launcher, LocalTargetInfo usedTarget, LocalTargetInfo intendedTarget)
        {
            mSpawnCell[__instance.thingIDNumber] = __instance.Position;
            mLaunchers.Add(launcher?.ToString());
            Map map = __instance.Map;
            if (map != null && mLaunchLog.Count < 400)
            {
                mLaunchRecs.Add((launcher, launcher?.Position ?? IntVec3.Invalid, usedTarget.Cell));
                mLaunchLog.Add($"{launcher} at {launcher?.Position} (floor {(launcher == null ? 0 : ProjectZ.FloorOf(launcher.Position, map) + 1)}) spawn {__instance.Position} -> used {usedTarget} at {usedTarget.Cell} (floor {ProjectZ.FloorOf(usedTarget.Cell, map) + 1}), intended {intendedTarget}");
            }
        }

        // Test only: record every projectile launch (launcher, its cell, the spawn cell = the shoot line's source, targets)
        // and every impact (where, on what, whether a slab or roof stopped it, whether it flew along a clear line).
        private static void MPatchLaunch()
        {
            if (mPatched)
            {
                return;
            }
            mPatched = true;
            Harmony h = new Harmony("Peren.IsoRimProjectZ.test.lean");
            h.Patch(AccessTools.Method(typeof(Projectile), nameof(Projectile.Launch), new[] { typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef) }),
                prefix: new HarmonyMethod(typeof(TestRunner), nameof(MLaunchPrefix)));
            h.Patch(AccessTools.Method(typeof(Projectile), "Impact"), prefix: new HarmonyMethod(typeof(TestRunner), nameof(MImpactPrefix)));
            lSlabSeen = PZCombatStats.slabStops;
        }

        private static void MImpactPrefix(Projectile __instance, Thing hitThing)
        {
            Map map = __instance.Map;
            if (map == null || mImpact.ContainsKey(__instance.thingIDNumber))
            {
                return;
            }
            mImpact[__instance.thingIDNumber] = $"{(hitThing == null ? "nothing (ground)" : hitThing.ToString())} at {__instance.Position} (floor {ProjectZ.FloorOf(__instance.Position, map) + 1}, ground {ProjectZ.GroundCellOf(__instance.Position, map)})";
            bool slab = PZCombatStats.slabStops != lSlabSeen;
            lSlabSeen = PZCombatStats.slabStops;
            Flights.Flight f = Flights.Of(__instance);
            lImps.Add(new LImp
            {
                launcher = __instance.Launcher,
                g = ProjectZ.GroundCellOf(__instance.Position, map),
                floor = ProjectZ.FloorOf(__instance.Position, map),
                hit = hitThing,
                slab = slab,
                clear = f != null && f.clear,
                cross = Flights.cross.TryGetValue(__instance, out _),
                aimed = __instance.usedTarget.HasThing
            });
        }

        private static int MLaunches(Thing t) => mLaunchers.Count(s => s == t.ToString());

        private static string MCell(IntVec3 c, Map map) => c.IsValid ? $"{ProjectZ.GroundCellOf(c, map)} floor {ProjectZ.FloorOf(c, map) + 1}" : "none";

        // ---- the field and its fixtures
        private const int LW = 41, LH = 46;
        private static CellRect lField;
        private static Map lMap;

        private static IntVec3 LG(int x, int z) => new IntVec3(lField.minX + x, 0, lField.minZ + z);

        private static IntVec3 LU(int x, int z, int k = 1) => ProjectZ.CellOnFloor(LG(x, z), k, lMap);

        private sealed class LCheck
        {
            public string name;
            public Action<Map> run;
        }

        private static readonly List<LCheck> lChecks = new List<LCheck>();
        private static int lIdx;
        private static string lCheckName = "";

        private sealed class LShot
        {
            public string name;
            public Pawn shooter;
            public Pawn target;
            public bool expect;
            public IntVec3 lean = IntVec3.Invalid;
            public bool film;
            public int want = 6;
            public bool facing;
            public Rot4 face;
            public Action<Map> after;   // follow-up check after the shots (same fixture)
        }

        private static LShot lCur;
        private static bool lFilmed;
        private static int lFaceFrames, lFaceRight, lFaceWrong, lFaceTicksAt;
        private static float lLeanMax;
        private static string lFaceWrongList = "";
        private static int lImpsBase;

        private static void LeanHook(Map map)
        {
            foreach (Pawn p in Colonists(map))
            {
                if (p.needs?.food != null) p.needs.food.CurLevel = p.needs.food.MaxLevel;
                if (p.needs?.rest != null) p.needs.rest.CurLevel = 1f;
                if (p.needs?.joy != null) p.needs.joy.CurLevel = 1f;
                if (p.InMentalState) p.MentalState.RecoverFromState();
            }
            // target raiders stay where they stand (stunned, no mote) and on their feet; a shooting raider is left alone
            foreach (Pawn r in mRaiders)
            {
                if (r.Spawned && r != mShooter && !r.Dead)
                {
                    r.stances?.stunner?.StunFor(30, null, false, false);
                    List<Hediff> hurt = r.health.hediffSet.hediffs.Where(h => h is Hediff_Injury || h.def == HediffDefOf.BloodLoss).ToList();
                    foreach (Hediff h in hurt)
                    {
                        r.health.RemoveHediff(h);
                    }
                }
            }
        }

        private static bool StepLean(Map map)
        {
            switch (stage)
            {
                case "m_setup":
                    SetupLos(map);
                    return true;
                case "m_check":
                {
                    if (lIdx >= lChecks.Count)
                    {
                        LEnd(map);
                        return true;
                    }
                    LCheck c = lChecks[lIdx++];
                    lCheckName = c.name;
                    fTickRuns = 0;
                    LReset(map);
                    Out($"---- check {c.name}");
                    Seeded(c.name, () => c.run(map));
                    if (stage == "m_check")
                    {
                        Wait(2, "m_check");
                    }
                    return true;
                }
                case "m_face":
                    LFaceFrame(map);
                    return true;
                case "m_wait_face":
                    LWaitFaceFrame(map);
                    return true;
                case "m_noshot":
                {
                    tickWatch = null;
                    int n = MLaunches(mShooter) - mLaunchesAt;
                    Result($"{lCur.name}: fire at will never shoots ({ticksTotal} ticks)", n == 0, $"shots {n}; job {mShooter.CurJobDef?.defName}, stance {mShooter.stances?.curStance}");
                    LEndShot(map);
                    return true;
                }
                case "m_shot_end":
                {
                    tickWatch = null;
                    if (lCur.film && !lFilmed && mProj != null && mProj.Spawned)
                    {
                        lFilmed = true;
                        mFrame = 0;
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                        LogLeanShot(map, lCur);
                        Wait(1, "m_yaw");
                        return true;
                    }
                    LShotResults(map);
                    return true;
                }
                case "m_yaw":
                {
                    if (!mProj.Spawned)
                    {
                        Info($"{lCur.name}: projectile gone before frame {mFrame}");
                        LShotTicks(map);
                        return true;
                    }
                    IsoRimBridge.SetOption("freeYaw", mFrame == 1 ? 180f : 0f);
                    int top = Mathf.Max(ProjectZ.FloorOf(mShooter.Position, map), ProjectZ.FloorOf(mTarget.Position, map));
                    IntVec3 gs = ProjectZ.GroundCellOf(mShooter.Position, map);
                    IntVec3 gt = ProjectZ.GroundCellOf(mTarget.Position, map);
                    IntVec3 mid = new IntVec3((gs.x + gt.x) / 2, 0, (gs.z + gt.z) / 2);
                    Find.CameraDriver.config.sizeRange.min = 2f;
                    ShotAt(map, top, mid, Mathf.Max(5f, (gt - gs).LengthHorizontal * 0.55f));
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(30, "m_take");
                    return true;
                }
                case "m_take":
                    ZCapture($"los3d_{lCur.name}_{(mFrame == 1 ? "yaw225" : "yaw45")}");
                    Wait(3, "m_logged");
                    return true;
                case "m_logged":
                    mFrame++;
                    if (mFrame < 2)
                    {
                        Wait(1, "m_yaw");
                        return true;
                    }
                    IsoRimBridge.SetOption("freeYaw", 0f);
                    LShotTicks(map);
                    return true;
                case "m_ai_end":
                {
                    tickWatch = null;
                    int n = MLaunches(mShooter) - mLaunchesAt;
                    int cross = lImps.Count(i => i.launcher == mShooter && i.cross);
                    Result("ai: the raider on the ground shoots the colonist in the 2nd row upstairs on its own", n > 0 && cross > 0, $"shots {n} in {ticksTotal} ticks, cross-floor impacts {cross}; job {mShooter.CurJobDef?.defName}");
                    LEndShot(map);
                    return true;
                }
                case "m_shot":
                    ZCapture(lShotName);
                    Wait(3, "m_shot_next");
                    return true;
                case "m_shot_next":
                    lGui = null;
                    if (lFloatMenu != null)
                    {
                        Find.WindowStack.TryRemove(lFloatMenu, doCloseSound: false);
                        lFloatMenu = null;
                    }
                    if (lShotQueue.Count > 0)
                    {
                        Action<Map> a = lShotQueue[0];
                        lShotQueue.RemoveAt(0);
                        a(map);
                        return true;
                    }
                    Find.Selector.ClearSelection();
                    Wait(2, "m_check");
                    return true;
                case "m_perf_fight":
                    LPerfFightFrame(map);
                    return true;
                case "m_settle":
                    LeanHeal(map);
                    Wait(2, "m_check");
                    return true;
            }
            return StepFinalSay(map);
        }

        // ------------------------------------------------------------------ setup

        private static void SetupLos(Map map)
        {
            lMap = map;
            if (mode == "finalsay" || mode == "lobbed")
            {
                FQuiet(map, true);
            }
            AnnexMapComponent an = ProjectZ.Annex(map);
            List<Pawn> cols = Colonists(map).Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)).ToList();
            for (int i = 0; i < 3 && cols.Count < 2; i++)
            {
                Pawn extra = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true, mustBeCapableOfViolence: true));
                GenSpawn.Spawn(extra, CellFinder.RandomClosewalkCellNear(map.Center, map, 6), map);
                Info($"added colonist {extra} (the scenario's could not all shoot)");
                cols = Colonists(map).Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)).ToList();
            }
            if (an == null || an.floors < 3 || cols.Count < 2)
            {
                Result("los3d setup", false, $"needs 3 floors, 2 colonists able to shoot; floors {an?.floors}, colonists {cols.Count}");
                Finish();
                return;
            }
            // a LW x LH open field, the one with the fewest cells to fix
            CellRect found = CellRect.Empty;
            int bestBad = int.MaxValue;
            if (mode == "finalsay" || mode == "lobbed")
            {
                // a fixed field (the middle of the ground rows), so it is the same every run even where the generated map
                // differs a little between runs; LReset clears it for every check (rock, water, roofs, things)
                CellRect fixedRect = new CellRect((map.Size.x - LW) / 2, Mathf.Max(3, (an.groundZ - LH) / 2), LW, LH);
                if (fixedRect.maxZ < an.groundZ - 3 && fixedRect.ExpandedBy(2).InBounds(map) && !fixedRect.ExpandedBy(2).Cells.Any(c => c.GetThingList(map).Any(t => !t.def.destroyable)))
                {
                    found = fixedRect;
                    bestBad = -1;
                }
            }
            for (int z = 3; bestBad >= 0 && z + LH < an.groundZ - 3; z += 4)
            {
                for (int x = 3; x + LW < map.Size.x - 3; x += 4)
                {
                    CellRect r = new CellRect(x, z, LW, LH);
                    if (r.ExpandedBy(2).Cells.Any(c => c.InBounds(map) && c.GetThingList(map).Any(t => !t.def.destroyable)))
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
                Result("los3d field", false, "map too small");
                Finish();
                return;
            }
            lField = found;
            DebugSettings.enableRandomMentalStates = false;
            cols = cols.OrderByDescending(p => p.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0).ToList();
            mA = cols[0];
            mB = cols[1];
            // a sealed hut far from the field for everyone not in a check
            foreach (IntVec3 c in GenRadial.RadialCellsAround(new IntVec3(Mathf.Clamp(found.CenterCell.x > map.Size.x / 2 ? 6 : map.Size.x - 7, 3, map.Size.x - 4), 0, Mathf.Clamp(found.CenterCell.z > an.groundZ / 2 ? 6 : an.groundZ - 7, 3, an.groundZ - 4)), 30f, true))
            {
                CellRect h = new CellRect(c.x - 2, c.z - 2, 5, 5);
                if (h.InBounds(map) && h.maxZ < an.groundZ && !h.Overlaps(found.ExpandedBy(8)) && h.Cells.All(x => x.Standable(map) && x.GetFirstBuilding(map) == null))
                {
                    mHut = c;
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
            MPatchLaunch();
            mOldYaw = (float)IsoRimBridge.SetOption("freeYaw", 0f);
            mOldMin = Find.CameraDriver.config.sizeRange.min;
            Info($"field {found}; A {mA}, B {mB}; hut {mHut}; seed {Seed}; storey {IsoRimBridge.StoreyHeight:0.00}, wall {IsoRimBridge.WallHeight:0.00}; eye {Los3D.Eye}, aims {Los3D.AimHead}/{Los3D.AimChest}/{Los3D.AimWaist}");
            BuildChecks();
            lIdx = 0;
            Wait(10, "m_check");
        }

        // Wipes the field (and 2 cells round it) on every floor: buildings, items, plants, filth, gas, roofs, upper floors;
        // ground terrain soil; raiders destroyed; colonists in the hut, drafted, holding fire.
        private static void LReset(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Find.Selector.ClearSelection();
            foreach (Pawn r in mRaiders)
            {
                if (!r.Destroyed)
                {
                    r.Destroy();
                }
            }
            mRaiders.Clear();
            if (mode == "finalsay" || mode == "lobbed")
            {
                FQuiet(map, false);
            }
            mShooter = mTarget = null;
            mProj = null;
            int i = 0;
            foreach (Pawn p in Colonists(map))
            {
                p.jobs.StopAll();
                p.drafter.Drafted = true;
                p.drafter.FireAtWill = false;
                if (mHut.IsValid)
                {
                    Teleport(p, mHut + GenAdj.AdjacentCellsAndInside[i++ % 9]);
                }
            }
            AnnexMapComponent a = ProjectZ.Annex(map);
            CellRect wide = lField.ExpandedBy(2);
            for (int k = a.floors - 1; k >= 0; k--)
            {
                foreach (IntVec3 g in wide)
                {
                    IntVec3 c = k == 0 ? g : ProjectZ.CellOnFloor(g, k, map);
                    if (!c.IsValid || !c.InBounds(map))
                    {
                        continue;
                    }
                    foreach (Thing t in c.GetThingList(map).ToList())
                    {
                        if (t is Pawn pw)
                        {
                            if (!pw.IsColonist && !pw.Destroyed)
                            {
                                pw.Destroy();
                            }
                            continue;
                        }
                        if (t.def.destroyable && !t.Destroyed)
                        {
                            t.Destroy();
                        }
                    }
                    if (k > 0)
                    {
                        if (map.terrainGrid.TerrainAt(c) != PZDefOf.PZ_OpenAir)
                        {
                            map.terrainGrid.SetTerrain(c, PZDefOf.PZ_OpenAir);
                        }
                    }
                    else if (c.GetTerrain(map) != TerrainDefOf.Soil)
                    {
                        map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                    }
                    map.roofGrid.SetRoof(c, null);
                    map.fogGrid.Unfog(c);
                }
            }
            map.gasGrid.Debug_ClearAll();
            // the wiped supports marked roofs to collapse on the next tick: those roofs are gone already, and the
            // next fixture builds its own (a stale mark would drop its slab)
            map.roofCollapseBuffer.Clear();
            FloorView.Set(0);
        }

        // A slab on floor k over ground x0..x1, z0..z1 (field coordinates): the roof under it, then the floor.
        private static void LSlab(int x0, int z0, int x1, int z1, int k = 1)
        {
            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    IntVec3 below = k == 1 ? LG(x, z) : LU(x, z, k - 1);
                    lMap.roofGrid.SetRoof(below, RoofDefOf.RoofConstructed);
                    lMap.terrainGrid.SetTerrain(LU(x, z, k), PZDefOf.PZ_UpperFloor);
                }
            }
        }

        private static Thing LWall(IntVec3 c) => Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c, lMap);

        // The shot cases' slab, held up (the game's roof support rule, 6.9 cells) by three ground walls under its north
        // row, away from every line the checks use.
        private static void LShotSlab()
        {
            foreach (int x in new[] { SX0, (SX0 + SX1) / 2, SX1 })
            {
                LWall(LG(x, SZ1));
            }
            LSlab(SX0, SZ0, SX1, SZ1);
        }

        private static Pawn LGun(Pawn p, string gun = "Gun_AssaultRifle")
        {
            p.equipment.DestroyAllEquipment();
            p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(gun)));
            return p;
        }

        private static Pawn LPut(Pawn p, IntVec3 c)
        {
            Teleport(p, c);
            p.drafter.Drafted = true;
            p.drafter.FireAtWill = false;
            p.Drawer?.tweener?.ResetTweenedPosToRoot();
            return p;
        }

        private static Faction LEnemy() => Find.FactionManager.AllFactions.Where(x => x.HostileTo(Faction.OfPlayer) && x.def.humanlikeFaction && !x.def.hidden && !x.temporary && x.def.pawnGroupMakers != null).OrderBy(x => x.loadID).First();

        // A raider (one faction for all), with this gun or none; lord: a defend point at its cell unless lord is false.
        private static Pawn LeanRaider(Map map, IntVec3 c, string gun, bool lord = true)
        {
            Faction f = LEnemy();
            Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Mercenary_Gunner"), f, forceGenerateNewPawn: true));
            p.equipment.DestroyAllEquipment();
            if (gun != null)
            {
                p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(gun)));
            }
            p.apparel?.DestroyAll();
            GenSpawn.Spawn(p, c, map);
            if (lord)
            {
                LordMaker.MakeNewLord(f, new LordJob_DefendPoint(c, 0.9f, 0.9f, false, false), map, new[] { p });
            }
            mRaiders.Add(p);
            return p;
        }

        private static void LeanHeal(Map map)
        {
            foreach (Pawn p in Colonists(map).Concat(new[] { mA, mB }).Distinct())
            {
                if (p == null)
                {
                    continue;
                }
                if (p.Dead)
                {
                    ResurrectionUtility.TryResurrect(p);
                }
                foreach (Hediff h in p.health.hediffSet.hediffs.Where(h => h is Hediff_Injury || h.def == HediffDefOf.BloodLoss).ToList())
                {
                    p.health.RemoveHediff(h);
                }
            }
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Fire).ToList())
            {
                t.Destroy();
            }
        }

        // ------------------------------------------------------------------ the checks

        // slab of the shot cases: x 16..24, z 20..26 on floor 2; its south row z = 20 is the edge
        private const int SX0 = 16, SX1 = 24, SZ0 = 20, SZ1 = 26;

        private static void BuildChecks()
        {
            lChecks.Clear();
            void Add(string n, Action<Map> a) => lChecks.Add(new LCheck { name = n, run = a });
            if (mode == "finalsay")
            {
                BuildFinalSayChecks(Add);
                return;
            }
            if (mode == "lobbed")
            {
                BuildLobbedChecks(Add);
                return;
            }
            if (Mountains.On(lMap))
            {
                BuildMtnChecks(Add);
                return;
            }
            Add("walk", LWalk);
            Add("parity", LParity);
            Add("sealed", LSealed);
            Add("doors", LDoors);
            // down: shooters on the slab, raiders on the ground south (sheet 1a/1b)
            Add("down_edge_2out", m => LShotCase(m, "down_edge_2out", LU(20, 20), LG(19, 18), true));
            Add("down_row3_2out", m => LShotCase(m, "down_row3_2out", LU(20, 22), LG(19, 18), true));
            Add("down_row4_2out", m => LShotCase(m, "down_row4_2out", LU(20, 23), LG(19, 18), false));
            Add("down_row7_10out", m => LShotCase(m, "down_row7_10out", LU(20, 26), LG(17, 10), true, film: true));
            // up: ground shooters, raiders on the slab (sheet 2b/2c)
            Add("up_row2_from6out", m => LShotCase(m, "up_row2_from6out", LG(20, 14), LU(20, 21), true, film: true));
            Add("up_row3_atfoot", m => LShotCase(m, "up_row3_atfoot", LG(20, 19), LU(20, 22), false));
            // parapet: sandbags along the edge, the shooter right behind them (sheet 4a)
            Add("parapet_2out", m =>
            {
                LShotSlab();
                for (int x = SX0; x <= SX1; x++)
                {
                    Place(ThingDefOf.Sandbags, null, LU(x, SZ0), m);
                }
                LShotCase(m, "parapet_2out", LU(20, 21), LG(20, 18), true);
            });
            // wall corner: walls on the edge row's west part; the shooter behind its east end leans east (sheet 3a)
            Add("corner_lean", m =>
            {
                LShotSlab();
                for (int x = SX0; x <= 20; x++)
                {
                    LWall(LU(x, SZ0));
                }
                LShotCase(m, "corner_lean", LU(20, 21), LG(21, 14), true, lean: LU(21, 21), film: true, facing: true, face: Rot4.South);
            });
            // the user's case: 1 or 2 cells in from the slab's SW corner, a wall beside, a raider on the ground SW
            foreach (string w in new[] { "W", "E", "N" })
            {
                foreach (int inset in new[] { 1, 2 })
                {
                    string n = $"batty_wall{w}_in{inset}";
                    string wall = w;
                    int ins = inset;
                    bool main = wall == "E" && ins == 1;
                    Add(n, m =>
                    {
                        LShotSlab();
                        IntVec3 s = LU(SX0 + ins, SZ0 + ins);
                        IntVec3 wc = wall == "W" ? s + IntVec3.West : wall == "E" ? s + IntVec3.East : s + IntVec3.North;
                        LWall(wc);
                        LShotCase(m, n, s, LG(11, 15), true, film: main, facing: main, face: Rot4.West, after: main ? LBattyHidden : (Action<Map>)null);
                    });
                }
            }
            Add("waitface_raider", LRaiderWaitFace);
            Add("ai_up", LAi);
            Add("turrets", LTurrets);
            Add("smoke", LSmoke);
            Add("ui", LUi);
            Add("perf_calls", LPerfCalls);
            Add("perf_fight", m => LPerfFight(m, true));
            Add("perf_fight_ground", m => LPerfFight(m, false));
        }

        // ---- tall mountains (a tall-mountain world, -projectztallmountains: these checks run instead of the others).
        // Natural rock put in the wiped field as the fill makes it (Mountains.Generate): a column of height h has the rock
        // (with its thick roof) on levels 0..h-1 and the rock's natural floor on levels 1..h; its top is level h. Los3D
        // reads it through the column queries: the natural floors and the thick roofs are planes, the rock on every level
        // a block.
        // the first natural rock of the map's tile (as the fill uses the ground's rock); read when the checks run (defs loaded)
        private static ThingDef lRockDef => Find.World.NaturalRockTypesIn(lMap.Tile).FirstOrDefault(d => d.building != null && d.building.isNaturalRock && !d.building.isResourceRock && d.building.naturalTerrain != null) ?? ThingDef.Named("Granite");

        private static void LMountain(int x0, int z0, int x1, int z1, int h)
        {
            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    for (int L = 0; L <= h; L++)
                    {
                        IntVec3 c = L == 0 ? LG(x, z) : LU(x, z, L);
                        // (the floor first, as the fill: rock upstairs stands on its natural floor)
                        if (L >= 1)
                        {
                            lMap.terrainGrid.SetTerrain(c, lRockDef.building.naturalTerrain);
                        }
                        if (L < h)
                        {
                            lMap.roofGrid.SetRoof(c, RoofDefOf.RoofRockThick);
                            GenSpawn.Spawn(ThingMaker.MakeThing(lRockDef), c, lMap, WipeMode.Vanish);
                        }
                    }
                }
            }
        }

        // A tunnel dug into the ground level of a mountain: the rock gone, its thick roof and the floor above kept.
        private static void LTunnel(int x0, int z0, int x1, int z1)
        {
            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    LG(x, z).GetEdifice(lMap)?.Destroy(DestroyMode.Vanish);
                }
            }
        }

        // The mountain shot cases' plateau: a level-1 top over ground x 14..26, z 20..30; its south row z = 20 is the edge.
        private static void LPlateau() => LMountain(14, 20, 26, 30, 1);

        private static void LMtnShot(Map map, string name, IntVec3 shooterAt, IntVec3 targetAt, bool expect)
        {
            int rock = 0, floors = 0, thick = 0;
            foreach (IntVec3 g in lField)
            {
                for (int L = 0; L <= Levels.Max(map); L++)
                {
                    IntVec3 c = Levels.Cell(g, L, map);
                    rock += c.GetEdifice(map)?.def == lRockDef ? 1 : 0;
                    floors += L >= 1 && Floors.KindAt(c, map) == FloorKind.NaturalRock ? 1 : 0;
                    thick += map.roofGrid.RoofAt(c) == RoofDefOf.RoofRockThick ? 1 : 0;
                }
            }
            Info($"{name}: fixture rock {rock}, natural rock floors {floors}, thick roofs {thick}; shooter on {Floors.KindAt(shooterAt, map)}, target on {Floors.KindAt(targetAt, map)}");
            LShotStart(map, new LShot
            {
                name = name,
                shooter = LPut(LGun(mA), shooterAt),
                target = LeanRaider(map, targetAt, null),
                expect = expect
            });
        }

        private static void BuildMtnChecks(Action<string, Action<Map>> add)
        {
            // down: a shooter on the top 1 to 3 rows in, a raider on the ground 2 and 10 cells out (the 4th row is past
            // the edge's reach for 2 out, as on a slab)
            foreach (int row in new[] { 1, 2, 3 })
            {
                int z = 20 + row - 1;
                add($"mtn_top_row{row}_2out", m => { LPlateau(); LMtnShot(m, $"mtn_top_row{row}_2out", LU(20, z), LG(20, 18), true); });
                add($"mtn_top_row{row}_10out", m => { LPlateau(); LMtnShot(m, $"mtn_top_row{row}_10out", LU(20, z), LG(17, 10), true); });
            }
            add("mtn_top_row4_2out", m => { LPlateau(); LMtnShot(m, "mtn_top_row4_2out", LU(20, 23), LG(20, 18), false); });
            // up: a ground shooter 6 cells out vs a raider in the top's 2nd row; one at the cliff's foot vs the 3rd row
            add("mtn_up_row2_from6out", m => { LPlateau(); LMtnShot(m, "mtn_up_row2_from6out", LG(20, 14), LU(20, 21), true); });
            add("mtn_up_row3_atfoot", m => { LPlateau(); LMtnShot(m, "mtn_up_row3_atfoot", LG(20, 19), LU(20, 22), false); });
            // rock between: a 2-storey ridge (rock on the ground and on level 1) between the top's edge and a raider 12
            // out blocks; over a 1-storey ridge the same line is clear
            add("mtn_ridge2_blocks", m => { LPlateau(); LMountain(12, 13, 28, 15, 2); LMtnShot(m, "mtn_ridge2_blocks", LU(20, 20), LG(20, 8), false); });
            add("mtn_ridge1_over", m => { LPlateau(); LMountain(12, 13, 28, 15, 1); LMtnShot(m, "mtn_ridge1_over", LU(20, 20), LG(20, 8), true); });
            // a tunnel dug into the plateau (x 20, z 20..27): its thick roof and the top's floor block a shooter on the
            // top from a raider inside
            add("mtn_tunnel_roof_blocks", m => { LPlateau(); LTunnel(20, 20, 20, 27); LMtnShot(m, "mtn_tunnel_roof_blocks", LU(20, 28), LG(20, 22), false); });
        }

        // The standard shot fixture: the slab (built once per check), the shooter (colonist A, assault rifle), a raider
        // target without a gun.
        private static void LShotCase(Map map, string name, IntVec3 shooterAt, IntVec3 targetAt, bool expect, IntVec3? lean = null, bool film = false,
            bool facing = false, Rot4 face = default(Rot4), Action<Map> after = null)
        {
            if (!ProjectZ.HasFloor(LU(SX0, SZ0), map))
            {
                LShotSlab();
            }
            LShotStart(map, new LShot
            {
                name = name,
                shooter = LPut(LGun(mA), shooterAt),
                target = LeanRaider(map, targetAt, null),
                expect = expect,
                lean = lean ?? IntVec3.Invalid,
                film = film,
                facing = facing,
                face = face,
                after = after
            });
        }

        private static readonly List<IntVec3> lTmp = new List<IntVec3>();

        // The player's order: the drafted attack option of the float menu on the target (FloatMenuMakerMap.GetOptions,
        // what a right click there gives). true: the option had an action (taken); label: the option's text.
        private static bool LOrder(Pawn p, Thing t, out string label, out List<FloatMenuOption> opts)
        {
            Find.Selector.ClearSelection();
            Find.Selector.Select(p, false, false);
            opts = FloatMenuMakerMap.GetOptions(new List<Pawn> { p }, t.Position.ToVector3Shifted(), out FloatMenuContext _);
            string cannot = "CannotHitTarget".Translate();
            string noSight = "PZ_CannotHitNoLineOfSight".Translate();
            string oor = "OutOfRange".Translate();
            FloatMenuOption fire = opts.FirstOrDefault(o => o.action != null && o.autoTakeablePriority == 40f);
            if (fire != null)
            {
                label = fire.Label;
                fire.action();
                return true;
            }
            FloatMenuOption refused = opts.FirstOrDefault(o => o.action == null && (o.Label.Contains(noSight) || o.Label.Contains(cannot) || o.Label.Contains(oor)));
            label = refused?.Label ?? ("no attack option; options: " + string.Join(" | ", opts.Select(o => o.Label)));
            return false;
        }

        private static void LShotStart(Map map, LShot c)
        {
            lCur = c;
            lFilmed = false;
            mShooter = c.shooter;
            mTarget = c.target;
            mShooter.drafter.FireAtWill = false;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                p.Drawer?.tweener?.ResetTweenedPosToRoot();
            }
            Verb v = mShooter.equipment.PrimaryEq.PrimaryVerb;
            LocalTargetInfo t = new LocalTargetInfo(mTarget);
            bool cross = Heights.CrossFloor(mShooter.Position, mTarget.Position, map);
            lTmp.Clear();
            ShootLeanUtility.LeanShootingSourcesFromTo(mShooter.Position, mTarget.Position, map, lTmp);
            int calls = Los3D.Calls;
            bool can = v.CanHitTarget(t);
            bool found = v.TryFindShootLineFromTo(mShooter.Position, t, out mLine);
            bool sees = mShooter.CanSee(mTarget);
            bool seenBack = mTarget.CanSee(mShooter);
            float d = cross ? Range3D.Distance(mShooter.Position, mTarget.Position, map) : (mTarget.Position - mShooter.Position).LengthHorizontal;
            Info($"{c.name}: {mShooter} at {MCell(mShooter.Position, map)} -> {mTarget} at {MCell(mTarget.Position, map)}: can hit {can}, sees {sees}, seen back {seenBack}, lean cells [{string.Join(", ", lTmp.Select(x => MCell(x, map)))}], "
                + $"line {(found ? MCell(mLine.Source, map) + " -> " + MCell(mLine.Dest, map) : "none")}; distance {d:0.0}, Los3D calls {Los3D.Calls - calls}");
            Result($"{c.name}: {(c.expect ? "can" : "cannot")} hit and see", can == c.expect && sees == c.expect && (c.expect || !seenBack), $"can hit {can}, sees {sees}, seen back {seenBack}");
            if (c.expect && c.lean.IsValid)
            {
                Result($"{c.name}: the shoot line leaves from the lean cell {MCell(c.lean, map)}", found && mLine.Source == c.lean, $"line from {(found ? MCell(mLine.Source, map) : "none")}");
            }
            lImpsBase = lImps.Count(i => i.launcher == mShooter);
            bool ok = LOrder(mShooter, mTarget, out string label, out _);
            Info($"{c.name}: right-click order: {(ok ? "taken" : "refused")}: '{label}'; job {mShooter.CurJobDef?.defName}");
            mProj = null;
            mLaunchesAt = MLaunches(mShooter);
            Pawn shooter = mShooter;
            if (!c.expect)
            {
                string noSight = "PZ_CannotHitNoLineOfSight".Translate();
                Result($"{c.name}: the order is refused with \"{noSight}\"", !ok && label.Contains(noSight), $"'{label}'");
                FloatMenuUtility.GetRangedAttackAction(mShooter, mTarget, out string direct);
                HarmonyLib.Patches pi = Harmony.GetPatchInfo(AccessTools.Method(typeof(FloatMenuUtility), nameof(FloatMenuUtility.GetRangedAttackAction)));
                Info($"{c.name}: FloatMenuUtility.GetRangedAttackAction directly: '{direct}'; postfixes [{(pi == null ? "none" : string.Join(", ", pi.Postfixes.Select(x => x.owner + ":" + x.PatchMethod.DeclaringType.Name)))}]; "
                    + $"range distance {Range3D.RangeDistance(mShooter.Position, mTarget.Position, map):0.00} vs {v.EffectiveRange:0.0}; no-sight {NoSight.Blocked(v, t)}");
                // and fire at will never shoots either
                Find.Selector.ClearSelection();
                mShooter.jobs.StopAll();
                mShooter.drafter.FireAtWill = true;
                TickUntil(400, () => MLaunches(shooter) > mLaunchesAt, "m_noshot", 1);
                return;
            }
            Result($"{c.name}: the order is taken (AttackStatic)", ok && mShooter.CurJobDef == JobDefOf.AttackStatic, $"'{label}', job {mShooter.CurJobDef?.defName}");
            Find.Selector.ClearSelection();
            if (c.facing)
            {
                lFaceFrames = lFaceRight = lFaceWrong = 0;
                lLeanMax = 0f;
                lFaceWrongList = "";
                lFaceTicksAt = Find.TickManager.TicksGame;
                // the camera on the shooter: the game updates the drawn lean only for pawns in view
                ShotAt(map, ProjectZ.FloorOf(mShooter.Position, map), ProjectZ.GroundCellOf(mShooter.Position, map), 10f);
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                stage = "m_face";
                return;
            }
            LShotTicks(map);
        }

        // At normal game speed (frames with ticks, so the game's per-frame facing and drawn lean run): the shooter's
        // facing and drawn lean while it aims, until its first shot.
        private static void LFaceFrame(Map map)
        {
            LeanHook(map);
            lFaceFrames++;
            if (mShooter.stances.curStance is Stance_Warmup)
            {
                if (mShooter.Rotation == lCur.face)
                {
                    lFaceRight++;
                }
                else
                {
                    lFaceWrong++;
                    if (lFaceWrongList.Length < 200)
                    {
                        lFaceWrongList += mShooter.Rotation.ToStringHuman() + " ";
                    }
                }
                lLeanMax = Mathf.Max(lLeanMax, mShooter.Drawer.leaner.LeanOffset.magnitude);
            }
            if (MLaunches(mShooter) > mLaunchesAt || lFaceFrames > 900)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                int ticks = Find.TickManager.TicksGame - lFaceTicksAt;
                Result($"{lCur.name}: faces {lCur.face.ToStringHuman()} while aiming (normal speed, {lFaceFrames} frames, {ticks} ticks)", lFaceRight > 0 && lFaceWrong == 0, $"aiming frames facing {lCur.face.ToStringHuman()} {lFaceRight}, other {lFaceWrong} ({lFaceWrongList.Trim()})");
                if (lCur.lean.IsValid)
                {
                    Result($"{lCur.name}: drawn leaning out toward the lean cell while aiming (vanilla 0.5)", lLeanMax > 0.3f, $"max lean offset {lLeanMax:0.00}");
                }
                else
                {
                    Info($"{lCur.name}: max drawn lean offset while aiming {lLeanMax:0.00}");
                }
                LShotTicks(map);
            }
        }

        private static void LShotTicks(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Pawn shooter = mShooter;
            bool cross = Heights.CrossFloor(mShooter.Position, mTarget.Position, map);
            TickUntil(2400, () =>
            {
                if (lCur.film && !lFilmed)
                {
                    mProj = MidFlightOf(map, shooter, shooter, cross);
                    if (mProj != null)
                    {
                        return true;
                    }
                }
                return lImps.Count(i => i.launcher == shooter) - lImpsBase >= lCur.want || mTarget.Dead;
            }, "m_shot_end", 1);
        }

        private static void LShotResults(Map map)
        {
            LShot c = lCur;
            List<LImp> mine = lImps.Where(i => i.launcher == mShooter).Skip(lImpsBase).ToList();
            IntVec3 tg = ProjectZ.GroundCellOf(mTarget.Position, map);
            int kt = ProjectZ.FloorOf(mTarget.Position, map);
            int near = mine.Count(i => i.floor == kt && (i.g - tg).LengthHorizontal <= 1.5f);
            int hits = mine.Count(i => i.hit == mTarget);
            int aimedSlab = mine.Count(i => i.aimed && i.clear && i.slab);
            int slab = mine.Count(i => i.slab);
            IntVec3 sg = ProjectZ.GroundCellOf(mShooter.Position, map);
            float whole = (tg - sg).LengthHorizontal;
            int flewOut = mine.Count(i => (i.g - sg).LengthHorizontal >= whole * 0.5f);
            Result($"{c.name}: shoots; impacts on the target's floor at its column", mine.Count > 0 && near > 0,
                $"impacts {mine.Count} (hits on the target {hits}, at its column {near}, flew out past half way {flewOut}, aimed along a clear line {mine.Count(i => i.aimed && i.clear)}, slab stops {slab}); shots {MLaunches(mShooter) - mLaunchesAt}");
            Result($"{c.name}: no aimed shot along a clear line stops on a slab or roof", aimedSlab == 0, $"aimed+clear slab stops {aimedSlab}");
            if (c.after != null)
            {
                c.after(map);
                return;
            }
            LEndShot(map);
        }

        private static void LEndShot(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            foreach (Pawn p in Colonists(map))
            {
                p.jobs.StopAll();
                p.drafter.FireAtWill = false;
            }
            StartTicks(120, "m_settle");
        }

        // ---- the user's case, part 2: a fresh raider SW, the attack order taken, then the raider moved under the slab to
        // the east before any tick: the drafted pawn keeps its order (no line now) and faces the target where it really
        // is (east), not south.
        private static void LBattyHidden(Map map)
        {
            mShooter.jobs.StopAll();
            if (!mTarget.Destroyed)
            {
                mTarget.Destroy();
            }
            mRaiders.Remove(mTarget);
            mTarget = LeanRaider(map, LG(11, 15), null);
            bool ok = LOrder(mShooter, mTarget, out string l, out _);
            Find.Selector.ClearSelection();
            Teleport(mTarget, LG(23, 22));
            mTarget.Drawer?.tweener?.ResetTweenedPosToRoot();
            bool can = mShooter.equipment.PrimaryEq.PrimaryVerb.CanHitTarget(mTarget);
            Info($"batty hidden: order {ok} '{l}'; target moved to {MCell(mTarget.Position, map)}, can hit {can}, job {mShooter.CurJobDef?.defName} on {mShooter.CurJob?.targetA}");
            Result("batty hidden: the target under the slab can't be hit (order kept)", !can && mShooter.CurJobDef == JobDefOf.AttackStatic, $"can hit {can}, job {mShooter.CurJobDef?.defName}");
            lFaceFrames = lFaceRight = lFaceWrong = 0;
            lFaceWrongList = "";
            lFaceTicksAt = Find.TickManager.TicksGame;
            lWaitPawn = mShooter;
            lWaitFace = Rot4.East;
            lWaitName = "batty hidden: the drafted pawn holding its attack order";
            mLaunchesAt = MLaunches(mShooter);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            stage = "m_wait_face";
        }

        private static Pawn lWaitPawn;
        private static Rot4 lWaitFace;
        private static string lWaitName;

        private static void LWaitFaceFrame(Map map)
        {
            LeanHook(map);
            lFaceFrames++;
            if (lFaceFrames > 20 && lWaitPawn.CurJobDef == JobDefOf.AttackStatic && !(lWaitPawn.stances.curStance is Stance_Busy))
            {
                if (lWaitPawn.Rotation == lWaitFace)
                {
                    lFaceRight++;
                }
                else
                {
                    lFaceWrong++;
                    if (lFaceWrongList.Length < 200)
                    {
                        lFaceWrongList += lWaitPawn.Rotation.ToStringHuman() + " ";
                    }
                }
            }
            // (a cooldown from earlier shots may run first: count 120 waiting frames)
            if (lFaceRight + lFaceWrong >= 120 || lFaceFrames > 1500)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                int ticks = Find.TickManager.TicksGame - lFaceTicksAt;
                Result($"{lWaitName}: faces {lWaitFace.ToStringHuman()} toward its target on the other floor while waiting (normal speed, {ticks} ticks)", lFaceRight > 30 && lFaceWrong == 0,
                    $"waiting frames facing {lWaitFace.ToStringHuman()} {lFaceRight}, other {lFaceWrong} ({lFaceWrongList.Trim()}); job {lWaitPawn.CurJobDef?.defName}, stance {lWaitPawn.stances.curStance}, shots {MLaunches(lWaitPawn) - mLaunchesAt}");
                LEndShot(map);
            }
        }

        // ---- an undrafted raider on the ground holding an attack job on a colonist upstairs it can't see
        private static void LRaiderWaitFace(Map map)
        {
            LShotSlab();
            Pawn col = LPut(mB, LU(22, 23));
            Pawn r = LeanRaider(map, LG(14, 23), "Gun_AssaultRifle");
            bool can = r.equipment.PrimaryEq.PrimaryVerb.CanHitTarget(col) || r.CanSee(col);
            Result("waitface: the raider can't see the colonist (the line runs under the slab)", !can, $"raider at {MCell(r.Position, map)}, colonist at {MCell(col.Position, map)}");
            r.jobs.StartJob(JobMaker.MakeJob(JobDefOf.AttackStatic, col), JobCondition.InterruptForced);
            mShooter = null;   // the raider is stunned by the hook: it stays put and keeps its job
            mLaunchesAt = MLaunches(r);
            lFaceFrames = lFaceRight = lFaceWrong = 0;
            lFaceWrongList = "";
            lFaceTicksAt = Find.TickManager.TicksGame;
            lWaitPawn = r;
            lWaitFace = Rot4.East;
            lWaitName = "waitface: the undrafted raider with an attack job";
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            stage = "m_wait_face";
        }

        // ---- the AI: a raider on the ground (its own lord) vs colonist A in the slab's 2nd row
        private static void LAi(Map map)
        {
            LShotSlab();
            LPut(mA, LU(20, 21));
            mShooter = LeanRaider(map, LG(20, 13), "Gun_AssaultRifle");
            mTarget = mA;
            Info($"ai: raider can hit {mShooter.equipment.PrimaryEq.PrimaryVerb.CanHitTarget(mA)}, sees {mShooter.CanSee(mA)}");
            mLaunchesAt = MLaunches(mShooter);
            Pawn s = mShooter;
            TickUntil(2000, () => lImps.Any(i => i.launcher == s && i.cross), "m_ai_end", 1);
        }

        // ------------------------------------------------------------------ walk, parity, sealed rooms, doors

        private static void LWalk(Map map)
        {
            List<IntVec3> mine = new List<IntVec3>();
            List<IntVec3> game = new List<IntVec3>();
            int bad = 0, n = 0;
            string first = "";
            CellRect r = lField;
            for (int i = 0; i < 5000; i++)
            {
                IntVec3 a = new IntVec3(Rand.RangeInclusive(r.minX, r.maxX), 0, Rand.RangeInclusive(r.minZ, r.maxZ));
                IntVec3 b = new IntVec3(Rand.RangeInclusive(r.minX, r.maxX), 0, Rand.RangeInclusive(r.minZ, r.maxZ));
                int h = Rand.RangeInclusive(0, 4);
                IntVec3 off = h == 0 ? IntVec3.Zero : GenAdj.CardinalDirections[h - 1];
                game.Clear();
                bool clear = GenSight.LineOfSight(a, b, map, false, c => { game.Add(c); return true; }, off.x, off.z);
                Los3D.Walk(a, b, off.x, off.z, mine);
                n++;
                // the game checks every cell but the last
                bool same = clear && mine.Count == game.Count + 1 && mine[mine.Count - 1] == b && game.SequenceEqual(mine.Take(game.Count));
                if (!same)
                {
                    bad++;
                    if (first.Length == 0)
                    {
                        first = $"{a} -> {b} half {off}: game [{string.Join(" ", game)}], Los3D [{string.Join(" ", mine)}]";
                    }
                }
            }
            Result($"walk: Los3D walks the game's own cells (GenSight.LineOfSight, {n} random pairs with half offsets)", bad == 0, $"different {bad}; {first}");
        }

        private static void LParity(Map map)
        {
            // one random layout: walls, sandbags, closed doors in a 15x15 square, on the ground and on floor 2 over it
            const int x0 = 2, z0 = 28, size = 15;
            LSlab(x0 - 1, z0 - 1, x0 + size, z0 + size);
            char[,] lay = new char[size, size];
            for (int x = 0; x < size; x++)
            {
                for (int z = 0; z < size; z++)
                {
                    float f = Rand.Value;
                    lay[x, z] = f < 0.17f ? '#' : f < 0.20f ? 'b' : f < 0.22f ? 'D' : '.';
                }
            }
            foreach (int k in new[] { 0, 1 })
            {
                for (int x = 0; x < size; x++)
                {
                    for (int z = 0; z < size; z++)
                    {
                        IntVec3 c = k == 0 ? LG(x0 + x, z0 + z) : LU(x0 + x, z0 + z);
                        if (lay[x, z] == '#')
                        {
                            LWall(c);
                        }
                        else if (lay[x, z] == 'b')
                        {
                            Place(ThingDefOf.Sandbags, null, c, map);
                        }
                        else if (lay[x, z] == 'D')
                        {
                            Place(ThingDefOf.Door, ThingDefOf.WoodLog, c, map);
                        }
                    }
                }
            }
            List<(int sx, int sz, int tx, int tz)> pairs = new List<(int, int, int, int)>();
            List<(int x, int z)> open = new List<(int, int)>();
            for (int x = 0; x < size; x++)
            {
                for (int z = 0; z < size; z++)
                {
                    if (lay[x, z] == '.' || lay[x, z] == 'b')
                    {
                        open.Add((x, z));
                    }
                }
            }
            while (pairs.Count < 1500)
            {
                (int x, int z) s = open.RandomElement();
                (int x, int z) t = open.RandomElement();
                if (s != t)
                {
                    pairs.Add((s.x, s.z, t.x, t.z));
                }
            }
            Pawn sh = LPut(LGun(mA), LG(0, 0));
            Pawn tg = LeanRaider(map, LG(0, 1), null);
            Verb v = sh.equipment.PrimaryEq.PrimaryVerb;
            bool[][] hit = { new bool[pairs.Count], new bool[pairs.Count] };
            bool[][] see = { new bool[pairs.Count], new bool[pairs.Count] };
            bool[][] seeBack = { new bool[pairs.Count], new bool[pairs.Count] };
            int[] calls = new int[2];
            foreach (int k in new[] { 0, 1 })
            {
                int c0 = Los3D.Calls;
                for (int i = 0; i < pairs.Count; i++)
                {
                    var p = pairs[i];
                    Teleport(sh, k == 0 ? LG(x0 + p.sx, z0 + p.sz) : LU(x0 + p.sx, z0 + p.sz));
                    Teleport(tg, k == 0 ? LG(x0 + p.tx, z0 + p.tz) : LU(x0 + p.tx, z0 + p.tz));
                    hit[k][i] = v.CanHitTarget(tg);
                    see[k][i] = sh.CanSee(tg);
                    seeBack[k][i] = tg.CanSee(sh);
                }
                calls[k] = Los3D.Calls - c0;
            }
            int diff = 0;
            string first = "";
            for (int i = 0; i < pairs.Count; i++)
            {
                if (hit[0][i] != hit[1][i] || see[0][i] != see[1][i] || seeBack[0][i] != seeBack[1][i])
                {
                    diff++;
                    if (first.Length == 0)
                    {
                        first = $"pair {pairs[i]}: ground hit/see/back {hit[0][i]}/{see[0][i]}/{seeBack[0][i]}, floor 2 {hit[1][i]}/{see[1][i]}/{seeBack[1][i]}";
                    }
                }
            }
            int canG = hit[0].Count(b => b), seeG = see[0].Count(b => b);
            Result($"parity: the same random layout on the ground and on floor 2 gives the same can hit and can see ({pairs.Count} pairs, seed {Seed})", diff == 0, $"different {diff}; can hit {canG}, sees {seeG} of {pairs.Count}; {first}");
            Result("parity: Los3D never called for pairs on one floor (the game's own code)", calls[0] == 0 && calls[1] == 0, $"Los3D calls ground {calls[0]}, floor 2 {calls[1]}");
            Info("parity layout (ground = floor 2), north up:\n" + string.Join("\n", Enumerable.Range(0, size).Reverse().Select(z => new string(Enumerable.Range(0, size).Select(x => lay[x, z]).ToArray()))));
        }

        // Nobody shoots or sees through a floor: a sealed room on the ground under the slab (walls round x 17..21,
        // z 21..25) with a raider on each inner cell, against colonist A on every slab cell, both ways.
        private static void LSealed(Map map)
        {
            LShotSlab();
            foreach (IntVec3 g in new CellRect(LG(17, 21).x, LG(17, 21).z, 5, 5).EdgeCells)
            {
                LWall(g);
            }
            Pawn r = LeanRaider(map, LG(18, 22), "Gun_AssaultRifle");
            LPut(LGun(mA), LU(SX0, SZ0));
            Verb rv = r.equipment.PrimaryEq.PrimaryVerb;
            Verb av = mA.equipment.PrimaryEq.PrimaryVerb;
            int pairs = 0;
            List<string> bad = new List<string>();
            foreach (IntVec3 rc in new CellRect(LG(18, 22).x, LG(18, 22).z, 3, 3))
            {
                Teleport(r, rc);
                for (int x = SX0; x <= SX1; x++)
                {
                    for (int z = SZ0; z <= SZ1; z++)
                    {
                        Teleport(mA, LU(x, z));
                        pairs++;
                        bool up = rv.CanHitTarget(mA), down = av.CanHitTarget(r), seeUp = r.CanSee(mA), seeDown = mA.CanSee(r);
                        if ((up || down || seeUp || seeDown) && bad.Count < 12)
                        {
                            bad.Add($"{MCell(rc, map)} vs {MCell(mA.Position, map)}: up {up}, down {down}, sees up {seeUp}, sees down {seeDown}");
                        }
                    }
                }
            }
            Result($"sealed: nobody shoots or sees through the floor: raider in a sealed room under the slab vs the colonist on every slab cell ({pairs} pairs, both ways)", bad.Count == 0, string.Join("; ", bad));
        }

        // A ground wall row with a door; colonist A at the slab edge above, a raider on the ground just behind the door:
        // the line comes down through the door cell below the wall top. Closed: no line; open: a line.
        private static void LDoors(Map map)
        {
            LShotSlab();
            Building_Door door = null;
            for (int x = 12; x <= 28; x++)
            {
                if (x == 20)
                {
                    door = (Building_Door)Place(ThingDefOf.Door, ThingDefOf.WoodLog, LG(x, 14), map);
                }
                else
                {
                    LWall(LG(x, 14));
                }
            }
            LPut(LGun(mA), LU(20, 20));
            Pawn r = LeanRaider(map, LG(20, 13), "Gun_AssaultRifle");
            Verb av = mA.equipment.PrimaryEq.PrimaryVerb, rv = r.equipment.PrimaryEq.PrimaryVerb;
            bool closedDown = av.CanHitTarget(r), closedUp = rv.CanHitTarget(mA), closedSee = mA.CanSee(r) || r.CanSee(mA);
            AccessTools.Field(typeof(Building_Door), "openInt").SetValue(door, true);
            AccessTools.Field(typeof(Building_Door), "holdOpenInt").SetValue(door, true);
            bool openDown = av.CanHitTarget(r), openUp = rv.CanHitTarget(mA), openSee = mA.CanSee(r) && r.CanSee(mA);
            Result("doors: a closed door on the ground blocks the line down to the raider behind it, both ways", !closedDown && !closedUp && !closedSee, $"down {closedDown}, up {closedUp}, sight {closedSee}");
            Result("doors: the same door open lets it through, both ways", openDown && openUp && openSee, $"door open {door.Open}: down {openDown}, up {openUp}, sight {openSee}");
        }

        // ------------------------------------------------------------------ turrets and smoke

        private static Building_TurretGun LTurret(IntVec3 c)
        {
            return (Building_TurretGun)Place(DefDatabase<ThingDef>.GetNamed("Turret_MiniTurret"), ThingDefOf.Steel, c, lMap);
        }

        private static void LTurrets(Map map)
        {
            LShotSlab();
            Building_TurretGun up = LTurret(LU(20, 20));
            Building_TurretGun far = LTurret(LG(20, 10));
            Building_TurretGun foot = LTurret(LG(23, 19));
            Pawn edge = LeanRaider(map, LU(22, 20), "Gun_AssaultRifle");
            Pawn row3 = LeanRaider(map, LU(23, 22), "Gun_AssaultRifle");
            Pawn row4 = LeanRaider(map, LU(23, 23), "Gun_AssaultRifle");
            Pawn ground = LeanRaider(map, LG(20, 12), "Gun_AssaultRifle");
            Verb vu = up.AttackVerb, vf = far.AttackVerb, vo = foot.AttackVerb;
            bool upGround = vu.CanHitTarget(ground);
            bool farEdge = vf.CanHitTarget(edge), farRow3 = vf.CanHitTarget(row3);
            bool footRow4 = vo.CanHitTarget(row4);
            Result("turrets: a floor-2 turret at the edge hits a raider on the ground 8 out", upGround, $"{upGround}");
            Result("turrets: a ground turret 10 out hits raiders at the edge and in the 3rd row upstairs", farEdge && farRow3, $"edge {farEdge}, 3rd row {farRow3}");
            Result("turrets: a ground turret one cell from the slab's foot can't hit the 4th row (the line passes under the slab)", !footRow4, $"4th row {footRow4}");
            // the floor-2 turret's own search, with only the ground raider in reach
            foreach (Pawn p in new[] { edge, row3, row4 })
            {
                p.Destroy();
                mRaiders.Remove(p);
            }
            LocalTargetInfo pick = up.TryFindNewTarget();
            Result("turrets: the floor-2 turret's own target search finds the raider below", pick.IsValid && pick.Thing == ground, $"picked {pick}");
            LocalTargetInfo pickUp = far.TryFindNewTarget();
            Info($"turrets: the ground turret 10 out picks {pickUp} (the ground raider is nearer)");
        }

        private static void LSmoke(Map map)
        {
            LShotSlab();
            Building_TurretGun up = LTurret(LU(20, 20));
            Pawn r = LeanRaider(map, LG(20, 11), "Gun_AssaultRifle");
            LPut(LGun(mA), LU(21, 20));
            LocalTargetInfo before = up.TryFindNewTarget();
            float gasBefore = LGasFactor(mA, r);
            // blind smoke on the ground in the last third of the line (where it runs inside the ground storey)
            foreach (IntVec3 g in new[] { LG(19, 12), LG(20, 12), LG(21, 12), LG(19, 13), LG(20, 13), LG(21, 13) })
            {
                map.gasGrid.AddGas(g, GasType.BlindSmoke, 255, false);
            }
            LocalTargetInfo after = up.TryFindNewTarget();
            float gasAfter = LGasFactor(mA, r);
            bool canHit = mA.equipment.PrimaryEq.PrimaryVerb.CanHitTarget(r);
            Result("smoke: the floor-2 turret finds the raider below, not through blind smoke on the line (the game's rule)", before.IsValid && before.Thing == r && !after.IsValid, $"before {before}, with smoke {after}");
            Result("smoke: an ordered shot through it is allowed, with the game's 70% factor", canHit && Mathf.Abs(gasBefore - 1f) < 0.001f && Mathf.Abs(gasAfter - 0.7f) < 0.001f, $"can hit {canHit}, gas factor before {gasBefore:0.00}, with smoke {gasAfter:0.00}");
            // smoke on the ground under the part of the line that runs above the ground storey does not count
            map.gasGrid.Debug_ClearAll();
            foreach (IntVec3 g in new[] { LG(19, 18), LG(20, 18), LG(21, 18) })
            {
                map.gasGrid.AddGas(g, GasType.BlindSmoke, 255, false);
            }
            LocalTargetInfo high = up.TryFindNewTarget();
            Result("smoke: smoke on the ground under the high part of the line doesn't block (the line passes over it)", high.IsValid && high.Thing == r, $"picked {high}");
        }

        private static float LGasFactor(Pawn p, Thing t)
        {
            ShotReport rep = ShotReport.HitReportFor(p, p.equipment.PrimaryEq.PrimaryVerb, t);
            return Traverse.Create(rep).Field("factorFromCoveringGas").GetValue<float>();
        }

        // ------------------------------------------------------------------ UI

        private static Action lGui;
        private static FloatMenu lFloatMenu;
        private static string lShotName;
        private static readonly List<Action<Map>> lShotQueue = new List<Action<Map>>();

        // (TestRunner.OnGUI) the test's own drawing for a shot
        internal static void LosGui()
        {
            lGui?.Invoke();
        }

        private static string LTip(Pawn shooter, Thing target)
        {
            Find.Selector.ClearSelection();
            Find.Selector.Select(shooter, false, false);
            return TooltipUtility.ShotCalculationTipString(target);
        }

        private static void LUi(Map map)
        {
            LShotSlab();
            string noSight = "PZ_NoLineOfSight".Translate();
            string refuse = "PZ_CannotHitNoLineOfSight".Translate();
            // blocked between floors: A in the 4th row, a raider 2 out
            LPut(LGun(mA), LU(20, 23));
            Pawn low = LeanRaider(map, LG(19, 18), null);
            string tipBlocked = LTip(mA, low);
            LOrder(mA, low, out string refBlocked, out List<FloatMenuOption> optsBlocked);
            // clear between floors: a raider 10 out
            Pawn far = LeanRaider(map, LG(17, 10), null);
            string tipClear = LTip(mA, far);
            // out of range between floors
            IntVec3 oor = IntVec3.Invalid;
            float range = mA.equipment.PrimaryEq.PrimaryVerb.EffectiveRange;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(LG(20, 10), 45f, true))
            {
                if (c.InBounds(map) && c.z < ProjectZ.Annex(map).groundZ && c.Standable(map) && Range3D.RangeDistance(mA.Position, c, map) > range + 3f && Range3D.RangeDistance(mA.Position, c, map) < range + 9f)
                {
                    oor = c;
                    break;
                }
            }
            Pawn away = oor.IsValid ? LeanRaider(map, oor, null) : null;
            string tipAway = away != null ? LTip(mA, away) : "";
            string refAway = "";
            if (away != null)
            {
                LOrder(mA, away, out refAway, out _);
            }
            // one floor: B on the ground behind a long wall row, a raider on the other side
            for (int x = 0; x <= 14; x++)
            {
                LWall(LG(x, 34));
            }
            LPut(LGun(mB), LG(7, 33));
            Pawn behind = LeanRaider(map, LG(7, 37), null);
            string tipFlat = LTip(mB, behind);
            LOrder(mB, behind, out string refFlat, out _);
            Pawn flatClear = LeanRaider(map, LG(7, 28), null);
            string tipFlatClear = LTip(mB, flatClear);
            Find.Selector.ClearSelection();
            string Q(string s) => "'" + s.Replace("\n", " / ") + "'";
            Result("ui: blocked between floors: the shot preview says \"" + noSight + "\" and the order is refused with it", tipBlocked.Contains(noSight) && refBlocked.Contains(refuse), $"tip {Q(tipBlocked)}, order {Q(refBlocked)}");
            Result("ui: a clear shot between floors has no such line", !tipClear.Contains(noSight) && tipClear.Length > 0, $"tip {Q(tipClear)}");
            string oorText = "OutOfRange".Translate();
            Result("ui: out of range between floors: no such line, the game's out-of-range refusal", away != null && !tipAway.Contains(noSight) && !refAway.Contains(refuse) && refAway.IndexOf(oorText, StringComparison.OrdinalIgnoreCase) >= 0,
                $"target {MCell(oor, map)}: tip {Q(tipAway)}, order {Q(refAway)}; key OutOfRange = {Q(oorText)}");
            Result("ui: blocked on one floor (a wall): the same line (the game's own sight result)", tipFlat.Contains(noSight) && refFlat.Contains(refuse), $"tip {Q(tipFlat)}, order {Q(refFlat)}");
            Result("ui: a clear shot on one floor has no such line", !tipFlatClear.Contains(noSight) && tipFlatClear.Length > 0, $"tip {Q(tipFlatClear)}");
            // shots: the tooltip (the game's tooltip box drawn next to the target with the text the game makes) and the
            // right-click float menu with the refusal
            lShotQueue.Clear();
            lShotQueue.Add(m =>
            {
                ShotAt(m, 1, ProjectZ.GroundCellOf(mA.Position, m), 9f);
                Find.Selector.Select(mA, false, false);
                string text = tipBlocked;
                lGui = () =>
                {
                    Vector2 at = low.DrawPos.MapToUIPosition() + new Vector2(16f, 16f);
                    new ActiveTip(new TipSignal(text)).DrawTooltip(at);
                };
                lShotName = "los3d_ui_tooltip_nolos";
                Wait(20, "m_shot");
            });
            lShotQueue.Add(m =>
            {
                ShotAt(m, 1, ProjectZ.GroundCellOf(mA.Position, m), 9f);
                Find.Selector.Select(mA, false, false);
                lFloatMenu = new FloatMenuMap(optsBlocked, null, low.Position.ToVector3Shifted());
                Find.WindowStack.Add(lFloatMenu);
                lShotName = "los3d_ui_order_refused";
                Wait(20, "m_shot");
            });
            lShotQueue.Add(m =>
            {
                ShotAt(m, 0, LG(7, 34), 9f);
                Find.Selector.Select(mB, false, false);
                string text = tipFlat;
                lGui = () =>
                {
                    Vector2 at = behind.DrawPos.MapToUIPosition() + new Vector2(16f, 16f);
                    new ActiveTip(new TipSignal(text)).DrawTooltip(at);
                };
                lShotName = "los3d_ui_tooltip_nolos_flat";
                Wait(20, "m_shot");
            });
            Action<Map> firstShot = lShotQueue[0];
            lShotQueue.RemoveAt(0);
            firstShot(map);
        }

        // ------------------------------------------------------------------ performance

        private static double LMicro(int n, Func<bool> f)
        {
            for (int i = 0; i < 2000; i++)
            {
                f();
            }
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                f();
            }
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds * 1000.0 / n;
        }

        private static void LPerfCalls(Map map)
        {
            LShotSlab();
            LPut(LGun(mA), LU(20, 22));
            Pawn far = LeanRaider(map, LG(20, 3), null);     // 19 cells, a clear line
            Pawn low = LeanRaider(map, LG(19, 18), null);    // blocked at the slab edge (all three aims walked there)
            LPut(LGun(mB), LG(30, 2));
            Pawn flatG = LeanRaider(map, LG(30, 21), null);  // a ground pair 19 cells apart
            Verb v = mA.equipment.PrimaryEq.PrimaryVerb;
            Verb vb = mB.equipment.PrimaryEq.PrimaryVerb;
            IntVec3 a = mA.Position, bFar = far.Position, bLow = low.Position;
            const int n = 100000;
            double losClear = LMicro(n, () => Los3D.CanSee(a, bFar, map));
            double losBlocked = LMicro(n, () => Los3D.CanSee(a, bLow, map));
            IntVec3 g0 = mB.Position, g1 = flatG.Position;
            double gen = LMicro(n, () => GenSight.LineOfSight(g0, g1, map, true));
            double hitCross = LMicro(n / 4, () => v.CanHitTarget(far));
            double hitCrossBlocked = LMicro(n / 4, () => v.CanHitTarget(low));
            double hitFlat = LMicro(n / 4, () => vb.CanHitTarget(flatG));
            double seeCross = LMicro(n / 4, () => mA.CanSee(far));
            double seeFlat = LMicro(n / 4, () => mB.CanSee(flatG));
            Info($"perf (µs per call): Los3D.CanSee 19 cells clear {losClear:0.000}, blocked at the edge {losBlocked:0.000}; GenSight.LineOfSight 19 cells {gen:0.000}; "
                + $"CanHitTarget cross-floor clear {hitCross:0.000}, blocked {hitCrossBlocked:0.000}, same floor {hitFlat:0.000}; AttackTargetFinder.CanSee cross-floor {seeCross:0.000}, same floor {seeFlat:0.000}");
            // Timings depend on the machine and on what else runs (harness runs share it): reported, checked only with
            // -projectzperfcheck
            if (GenCommandLine.CommandLineArgPassed("projectzperfcheck"))
            {
                Result("perf: Los3D.CanSee within about 1 µs per check (target 0.4 to 1)", losClear < 1.0 && losBlocked < 1.0, $"clear {losClear:0.000}, blocked {losBlocked:0.000} µs");
            }
            else
            {
                Info($"perf: Los3D.CanSee clear {losClear:0.000}, blocked {losBlocked:0.000} µs per check (target 0.4 to 1; checked with -projectzperfcheck)");
            }
        }

        // A fight between floors (or all on the ground): 20 raiders with rifles vs 6 colonists firing at will, timed per
        // tick, with the time inside the target finder and the line-of-sight check (test-only timers).
        private static readonly Stopwatch lBest = new Stopwatch(), lHit = new Stopwatch();
        private static bool lPerfPatched;
        private static bool lPerfCross;
        private static int lPerfTicks, lPerfCalls0;
        private static readonly Stopwatch lPerfTick = new Stopwatch();
        private static double lPerfCrossMs = -1;
        private static int lBestDepth, lHitDepth;

        private static void BestPre()
        {
            if (lBestDepth++ == 0)
            {
                lBest.Start();
            }
        }

        private static void BestPost()
        {
            if (--lBestDepth == 0)
            {
                lBest.Stop();
            }
        }

        private static void HitPre()
        {
            if (lHitDepth++ == 0)
            {
                lHit.Start();
            }
        }

        private static void HitPost()
        {
            if (--lHitDepth == 0)
            {
                lHit.Stop();
            }
        }

        private static void LPerfFight(Map map, bool cross)
        {
            if (!lPerfPatched)
            {
                lPerfPatched = true;
                Harmony h = new Harmony("Peren.IsoRimProjectZ.test.los3dperf");
                h.Patch(AccessTools.Method(typeof(AttackTargetFinder), nameof(AttackTargetFinder.BestAttackTarget)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(BestPre)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(TestRunner), nameof(BestPost)) { priority = Priority.Last });
                h.Patch(AccessTools.Method(typeof(Verb), "CanHitCellFromCellIgnoringRange"), prefix: new HarmonyMethod(typeof(TestRunner), nameof(HitPre)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(TestRunner), nameof(HitPost)) { priority = Priority.Last });
            }
            lPerfCross = cross;
            // supports first (ground walls under floor 2, floor-2 walls under floor 3), then the slabs
            foreach (int x in new[] { 10, 16, 22, 28, 30 })
            {
                foreach (int z in new[] { 29, 35 })
                {
                    LWall(LG(x, z));
                }
            }
            LSlab(10, 25, 30, 35);
            foreach (int x in new[] { 14, 20, 26 })
            {
                foreach (int z in new[] { 28, 33 })
                {
                    LWall(LU(x, z));
                }
            }
            LSlab(14, 28, 26, 33, 2);
            List<Pawn> cols = Colonists(map).Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)).ToList();
            while (cols.Count < 6)
            {
                Pawn extra = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true, mustBeCapableOfViolence: true));
                GenSpawn.Spawn(extra, mHut.IsValid ? mHut : map.Center, map);
                cols.Add(extra);
            }
            IntVec3[] spots = cross
                ? new[] { LG(15, 24), LG(25, 24), LU(12, 25), LU(28, 25), LU(16, 28, 2), LU(24, 28, 2) }
                : new[] { LG(15, 24), LG(25, 24), LG(12, 23), LG(28, 23), LG(16, 22), LG(24, 22) };
            for (int i = 0; i < 6; i++)
            {
                LPut(LGun(cols[i]), spots[i]);
                cols[i].drafter.FireAtWill = true;
            }
            List<Pawn> raiders = new List<Pawn>();
            for (int i = 0; i < 20; i++)
            {
                raiders.Add(LeanRaider(map, LG(Mathf.Min(38, 4 + i * 17 / 10), 2 + i % 3 * 2), "Gun_AssaultRifle", lord: false));
            }
            LordMaker.MakeNewLord(LEnemy(), new LordJob_DefendPoint(LG(20, 6), 6f, 6f, false, false), map, raiders);
            mShooter = null;
            lBest.Reset();
            lHit.Reset();
            lBestDepth = lHitDepth = 0;
            lPerfTick.Reset();
            lPerfTicks = 0;
            lPerfCalls0 = Los3D.Calls;
            stage = "m_perf_fight";
        }

        private static void LPerfFightFrame(Map map)
        {
            TickManager tm = Find.TickManager;
            Stopwatch frame = Stopwatch.StartNew();
            while (lPerfTicks < 1200 && frame.ElapsedMilliseconds < 150)
            {
                lPerfTick.Start();
                tm.DoSingleTick();
                lPerfTick.Stop();
                lPerfTicks++;
                if (lPerfTicks % 20 == 0)
                {
                    // (untimed) everyone patched up, so the fight goes on for the whole measurement
                    foreach (Pawn p in mRaiders.Concat(Colonists(map)))
                    {
                        if (p.Spawned && !p.Dead)
                        {
                            foreach (Hediff h in p.health.hediffSet.hediffs.Where(h => h is Hediff_Injury || h.def == HediffDefOf.BloodLoss).ToList())
                            {
                                p.health.RemoveHediff(h);
                            }
                        }
                    }
                }
            }
            if (lPerfTicks < 1200)
            {
                return;
            }
            double perTick = lPerfTick.Elapsed.TotalMilliseconds / lPerfTicks;
            double best = lBest.Elapsed.TotalMilliseconds / lPerfTicks;
            double hit = lHit.Elapsed.TotalMilliseconds / lPerfTicks;
            int calls = Los3D.Calls - lPerfCalls0;
            Info($"perf fight ({(lPerfCross ? "colonists on floors 1-3" : "all on the ground")}, 20 raiders vs 6 colonists, {lPerfTicks} ticks): {perTick:0.000} ms per tick; in BestAttackTarget {best:0.000} ms ({best / perTick:P1}), "
                + $"in CanHitCellFromCellIgnoringRange {hit:0.000} ms ({hit / perTick:P1}); Los3D calls {calls} ({calls / (double)lPerfTicks:0.0} per tick); colonists standing {Colonists(map).Count(p => !p.Downed)}, raiders standing {mRaiders.Count(r => r.Spawned && !r.Downed)}");
            if (lPerfCross)
            {
                lPerfCrossMs = perTick;
            }
            else if (lPerfCrossMs > 0)
            {
                Info($"perf fight: between floors vs all on the ground: {lPerfCrossMs:0.000} vs {perTick:0.000} ms per tick ({lPerfCrossMs / perTick - 1.0:+0%;-0%})");
            }
            Result($"perf fight ({(lPerfCross ? "between floors" : "ground")}): line-of-sight checks under 10% of the tick", hit / perTick < 0.10, $"{hit / perTick:P1}");
            LEndShot(map);
        }

        // ------------------------------------------------------------------ end

        private static void LEnd(Map map)
        {
            IsoRimBridge.SetOption("freeYaw", mOldYaw);
            Find.CameraDriver.config.sizeRange.min = mOldMin;
            if (mode == "finalsay" || mode == "lobbed")
            {
                FEnd();
                Info($"final say: results put back {FinalSay.Reasserted}, our prefix skipped (decided in the postfix instead) {FinalSay.PrefixSkipped}; combat stats: cross-floor shots {PZCombatStats.crossFloorShots}, intercepts {PZCombatStats.intercepts}, slab stops {PZCombatStats.slabStops}");
                WatchCheck(map, mode + " end");
                Finish();
                return;
            }
            int aimed = lImps.Count(i => i.cross && i.aimed);
            int aimedClear = lImps.Count(i => i.cross && i.aimed && i.clear);
            int clearSlab = lImps.Count(i => i.cross && i.aimed && i.clear && i.slab);
            int slab = lImps.Count(i => i.cross && i.slab);
            // (the tall-mountain run has fewer shot checks)
            Result("flights: no aimed shot along a clear line stopped on a slab or roof over the whole run", clearSlab == 0 && aimedClear >= (Mountains.On(map) ? 15 : 40),
                $"cross-floor impacts {lImps.Count(i => i.cross)}, aimed {aimed}, aimed along a clear line {aimedClear}, of those slab stops {clearSlab}; all slab stops {slab}");
            Info($"combat stats: cross-floor shots {PZCombatStats.crossFloorShots}, intercepts {PZCombatStats.intercepts}, passed over {PZCombatStats.passedOver}, slab stops {PZCombatStats.slabStops}; Los3D calls {Los3D.Calls}");
            WatchCheck(map, "los3d end");
            Finish();
        }

        private static void LogLeanShot(Map map, LShot c)
        {
            Projectile p = mProj;
            bool cross = Flights.cross.TryGetValue(p, out Flights.Cross x);
            int ko = cross ? x.from : ProjectZ.FloorOf(p.Position, map);
            float storey = IsoRimBridge.StoreyHeight;
            IntVec3 spawn = mSpawnCell.TryGetValue(p.thingIDNumber, out IntVec3 sc) ? sc : IntVec3.Invalid;
            Vector3 o = projOrigin(p), d = projDest(p);
            Vector3 dg = OverGround(d, 0f, map);
            Vector3 og = OverGround(mShooter.DrawPos, 0f, map);
            Vector3 dir = new Vector3(dg.x - og.x, 0f, dg.z - og.z).normalized;
            Vector3 m = IsoRimBridge.Muzzle(mShooter, p.EquipmentDef, mShooter.DrawPos, dir);
            Vector3 muzzle = OverGround(m, ko * storey + m.y, map);
            Vector3? ds = IsoRimBridge.DrawnStart(p);
            Vector3 start = ds.HasValue ? OverGround(ds.Value, ds.Value.y, map) : OverGround(o, IsoRimBridge.ProjectileHeight(p, o), map);
            float off = (start - muzzle).magnitude;
            Flights.Flight fl = Flights.Of(p);
            Info($"{c.name}: projectile {p.def.defName} spawned at {MCell(spawn, map)} (the shoot line's source {MCell(mLine.Source, map)}), floor {ko + 1} -> {(cross ? x.to + 1 : ko + 1)}; "
                + $"flight {(fl == null ? "none" : $"{fl.hs:0.00} -> {fl.ht:0.00} wall units, clear line {fl.clear}")}; drawn start {start.x:0.00},{start.y:0.00},{start.z:0.00} vs muzzle {muzzle.x:0.00},{muzzle.y:0.00},{muzzle.z:0.00} (off {off:0.000})");
            if (c.lean.IsValid)
            {
                Result($"{c.name}: projectile spawned on the shoot line's source cell (the lean cell, vanilla)", spawn == c.lean, $"spawn {MCell(spawn, map)}, lean {MCell(c.lean, map)}");
            }
        }
    }
}
