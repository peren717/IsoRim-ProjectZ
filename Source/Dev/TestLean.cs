using System;
using System.Collections.Generic;
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
    // Mode lean (the game's shoot leaning, a pawn behind a wall corner leaning out to shoot, between floors; run with
    // -projectzfloors=2 or more). An 11x9 slab on floor 2 at the south of a cleared field, its north edge notched: the
    // west part reaches one row further north, with a wall row along that protruding edge. Ground walls south of the
    // slab make a second corner. Every shot is a real ordered attack or the pawns' own auto-attack / AI on real ticks:
    //   down: colonist A on floor 2 behind the east end of the edge wall leans east onto a slab cell at the edge and
    //     shoots a raider on the ground to the north (ordered and fire at will); one cell further in it must not shoot.
    //   up: colonist B on the ground behind a wall corner leans east and shoots a raider at floor 2's south edge; the
    //     same from a ground corner north of the slab at its north edge (the target south: the game's map-row direction
    //     says north there).
    //   ai: a rifle raider (its own AI, defend-point lord) in each of the two corners vs a colonist on the other floor.
    //   controls on one floor: the same ground corner at a ground target, a corner on floor 2 at a floor 2 target.
    // Logged per case: can hit, the shoot line vanilla picked (root or lean cell) with and without the fix, the 3D range
    // from the root and the lean cell, the projectile's spawn cell, its drawn start against the shooter's muzzle, and
    // what it hit. Shots frozen mid-flight at yaw 45 and 225.
    public static partial class TestRunner
    {
        private sealed class LeanCase
        {
            public string name;
            public char shooter;   // 'A', 'B' colonists; 'R' a rifle raider (own AI)
            public string spot;
            public string target;
            public bool auto;
            public bool expect;
        }

        private static readonly LeanCase[] mCases =
        {
            new LeanCase { name = "down_lean_ordered", shooter = 'A', spot = "AL", target = "G", expect = true },
            new LeanCase { name = "down_lean_auto", shooter = 'A', spot = "AL", target = "G", auto = true, expect = true },
            new LeanCase { name = "down_in_ordered", shooter = 'A', spot = "AIn", target = "G", expect = false },
            new LeanCase { name = "down_in_auto", shooter = 'A', spot = "AIn", target = "G", auto = true, expect = false },
            new LeanCase { name = "up_lean_ordered", shooter = 'B', spot = "BL", target = "UE", expect = true },
            new LeanCase { name = "up_lean_auto", shooter = 'B', spot = "BL", target = "UE", auto = true, expect = true },
            new LeanCase { name = "up_south_ordered", shooter = 'B', spot = "BN", target = "UN", expect = true },
            new LeanCase { name = "up_south_auto", shooter = 'B', spot = "BN", target = "UN", auto = true, expect = true },
            new LeanCase { name = "ai_up", shooter = 'R', spot = "BL", target = "UE", auto = true, expect = true },
            new LeanCase { name = "ai_down", shooter = 'R', spot = "AL", target = "G", auto = true, expect = true },
            new LeanCase { name = "flat_ground", shooter = 'B', spot = "BL", target = "GC", expect = true },
            new LeanCase { name = "flat_floor2", shooter = 'A', spot = "FL", target = "FT", expect = true },
        };

        private static Pawn mA, mB, mShooter, mTarget;
        private static readonly List<Pawn> mRaiders = new List<Pawn>();
        private static readonly Dictionary<string, IntVec3> mSpots = new Dictionary<string, IntVec3>();
        private static readonly Dictionary<string, IntVec3> mLeans = new Dictionary<string, IntVec3>();
        private static IntVec3 mHut = IntVec3.Invalid;
        private static int mCase, mFrame, mLaunchesAt;
        private static Projectile mProj;
        private static ShootLine mLine;
        private static float mOldYaw, mOldMin;
        private static bool mPatched;
        private static readonly Dictionary<int, IntVec3> mSpawnCell = new Dictionary<int, IntVec3>();
        private static readonly Dictionary<int, string> mImpact = new Dictionary<int, string>();
        private static readonly List<string> mLaunchers = new List<string>();

        private static readonly List<string> mLaunchLog = new List<string>();
        private static readonly List<(Thing launcher, IntVec3 at, IntVec3 target)> mLaunchRecs = new List<(Thing, IntVec3, IntVec3)>();

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

        // Test only: record every projectile launch (launcher, its cell, the spawn cell = the shoot line's source, targets).
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
        }

        private static void MImpactPrefix(Projectile __instance, Thing hitThing)
        {
            Map map = __instance.Map;
            if (map != null && !mImpact.ContainsKey(__instance.thingIDNumber))
            {
                mImpact[__instance.thingIDNumber] = $"{(hitThing == null ? "nothing (ground)" : hitThing.ToString())} at {__instance.Position} (floor {ProjectZ.FloorOf(__instance.Position, map) + 1}, ground {ProjectZ.GroundCellOf(__instance.Position, map)})";
            }
        }

        private static int MLaunches(Thing t) => mLaunchers.Count(s => s == t.ToString());

        private static string MCell(IntVec3 c, Map map) => c.IsValid ? $"{ProjectZ.GroundCellOf(c, map)} floor {ProjectZ.FloorOf(c, map) + 1}" : "none";

        private static void LeanHook(Map map)
        {
            foreach (Pawn p in new[] { mA, mB })
            {
                if (p.needs?.food != null) p.needs.food.CurLevel = p.needs.food.MaxLevel;
                if (p.needs?.rest != null) p.needs.rest.CurLevel = 1f;
                if (p.needs?.joy != null) p.needs.joy.CurLevel = 1f;
                if (p.InMentalState) p.MentalState.RecoverFromState();
            }
            // target raiders stay where they stand (stunned, no mote); the AI raider is left alone
            foreach (Pawn r in mRaiders)
            {
                if (r.Spawned && r != mShooter && !r.Downed)
                {
                    r.stances?.stunner?.StunFor(30, null, false, false);
                }
            }
        }

        private static bool StepLean(Map map)
        {
            switch (stage)
            {
                case "m_setup":
                    SetupLean(map);
                    return true;
                case "m_sweep":
                    LeanSweep(map);
                    Wait(2, "m_case");
                    return true;
                case "m_case":
                    if (mCase >= mCases.Length)
                    {
                        IsoRimBridge.SetOption("freeYaw", mOldYaw);
                        Find.CameraDriver.config.sizeRange.min = mOldMin;
                        Info($"combat stats: cross-floor shots {PZCombatStats.crossFloorShots}, intercepts {PZCombatStats.intercepts}, passed over {PZCombatStats.passedOver}, slab stops {PZCombatStats.slabStops}");
                        WatchCheck(map, "lean end");
                        Finish();
                        return true;
                    }
                    StartLeanCase(map, mCases[mCase]);
                    return true;
                case "m_noshot":
                {
                    tickWatch = null;
                    LeanCase c = mCases[mCase];
                    int n = MLaunches(mShooter) - mLaunchesAt;
                    Result($"{c.name}: never shoots ({ticksTotal} ticks)", n == 0, $"shots {n}; shooter job {mShooter.CurJobDef?.defName}, stance {mShooter.stances?.curStance}");
                    EndLeanCase(map);
                    return true;
                }
                case "m_frozen":
                {
                    tickWatch = null;
                    LeanCase c = mCases[mCase];
                    if (mProj == null)
                    {
                        Result($"{c.name}: a shot mid-flight", false, $"none within {ticksTotal} ticks; shots {MLaunches(mShooter) - mLaunchesAt}; shooter job {mShooter.CurJobDef?.defName}, stance {mShooter.stances?.curStance}");
                        EndLeanCase(map);
                        return true;
                    }
                    mFrame = 0;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    LogLeanShot(map, c);
                    Wait(1, "m_yaw");
                    return true;
                }
                case "m_yaw":
                {
                    LeanCase c = mCases[mCase];
                    if (!mProj.Spawned)
                    {
                        Info($"{c.name}: projectile gone before frame {mFrame}");
                        StartFly(map);
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
                {
                    LeanCase c = mCases[mCase];
                    ZCapture($"lean_{c.name}_{(mFrame == 1 ? "yaw225" : "yaw45")}");
                    Wait(3, "m_logged");
                    return true;
                }
                case "m_logged":
                    mFrame++;
                    if (mFrame < 2)
                    {
                        Wait(1, "m_yaw");
                        return true;
                    }
                    StartFly(map);
                    return true;
                case "m_impact":
                {
                    tickWatch = null;
                    LeanCase c = mCases[mCase];
                    string hit = mImpact.TryGetValue(mProj.thingIDNumber, out string s) ? s : (mProj.Spawned ? "still flying" : "gone without an impact");
                    IntVec3 shooterG = ProjectZ.GroundCellOf(mShooter.Position, map);
                    IntVec3 targetG = ProjectZ.GroundCellOf(mTarget.Position, map);
                    IntVec3 at = ProjectZ.GroundCellOf(mProj.Position, map);
                    float went = (at - shooterG).LengthHorizontal;
                    float whole = (targetG - shooterG).LengthHorizontal;
                    Result($"{c.name}: the shot flew out to the target (not stopped by the corner)", went >= whole * 0.5f, $"impact: {hit}; {went:0.0} of {whole:0.0} cells; slab stops so far {PZCombatStats.slabStops}");
                    EndLeanCase(map);
                    return true;
                }
                case "m_settle":
                    LeanHeal(map);
                    mCase++;
                    Wait(2, "m_case");
                    return true;
            }
            return false;
        }

        private static void StartFly(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Projectile p = mProj;
            TickUntil(200, () => !p.Spawned, "m_impact", 1);
        }

        private static void StartLeanCase(Map map, LeanCase c)
        {
            foreach (Pawn r in mRaiders)
            {
                if (!r.Destroyed)
                {
                    r.Destroy();
                }
            }
            mRaiders.Clear();
            foreach (Pawn p in new[] { mA, mB })
            {
                p.jobs.StopAll();
                p.drafter.Drafted = true;
                p.drafter.FireAtWill = false;
                Teleport(p, mHut.IsValid ? mHut + (p == mA ? IntVec3.East : IntVec3.West) : p.Position);
            }
            IntVec3 spot = mSpots[c.spot];
            IntVec3 tspot = mSpots[c.target];
            if (c.shooter == 'R')
            {
                mShooter = LeanRaider(map, spot, "Gun_AssaultRifle");
                mTarget = ProjectZ.FloorOf(tspot, map) > 0 ? mA : mB;
                Teleport(mTarget, tspot);
                mTarget.drafter.Drafted = true;
                mTarget.drafter.FireAtWill = false;
            }
            else
            {
                mShooter = c.shooter == 'A' ? mA : mB;
                mShooter.equipment.DestroyAllEquipment();
                mShooter.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_AssaultRifle")));
                Teleport(mShooter, spot);
                mShooter.drafter.Drafted = true;
                mShooter.drafter.FireAtWill = c.auto;
                mTarget = LeanRaider(map, tspot, null);
            }
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                p.Drawer?.tweener?.ResetTweenedPosToRoot();
            }
            FloorView.Set(0);
            Verb v = mShooter.equipment?.PrimaryEq?.PrimaryVerb ?? mShooter.CurrentEffectiveVerb;
            LocalTargetInfo t = new LocalTargetInfo(mTarget);
            bool cross = Heights.CrossFloor(mShooter.Position, mTarget.Position, map);
            List<IntVec3> leans = new List<IntVec3>();
            ShootLeanUtility.LeanShootingSourcesFromTo(mShooter.Position, mTarget.Position, map, leans);
            bool can = v.CanHitTarget(t);
            bool found = v.TryFindShootLineFromTo(mShooter.Position, t, out mLine);
            string old = "";
            if (cross)
            {
                ShootLean_Patch.Off = true;
                List<IntVec3> oldLeans = new List<IntVec3>();
                ShootLeanUtility.LeanShootingSourcesFromTo(mShooter.Position, mTarget.Position, map, oldLeans);
                bool canOld = v.CanHitTarget(t);
                v.TryFindShootLineFromTo(mShooter.Position, t, out ShootLine oldLine);
                ShootLean_Patch.Off = false;
                old = $"; without the fix (game's map-row direction): can hit {canOld}, lean cells [{string.Join(", ", oldLeans.Select(x => MCell(x, map)))}], line from {(canOld ? MCell(oldLine.Source, map) : "-")}";
            }
            float range = v.EffectiveRange;
            int ks = ProjectZ.FloorOf(mShooter.Position, map), kt = ProjectZ.FloorOf(mTarget.Position, map);
            float dRoot = cross ? Range3D.Distance(mShooter.Position, mTarget.Position, map) : (mTarget.Position - mShooter.Position).LengthHorizontal;
            float dLean = found ? (cross ? Range3D.Distance(mLine.Source, mTarget.Position, map) : (mTarget.Position - mLine.Source).LengthHorizontal) : -1f;
            Info($"{c.name}: {mShooter} at {MCell(mShooter.Position, map)} -> {mTarget} at {MCell(mTarget.Position, map)}{(c.auto ? " (auto)" : " (ordered)")}: "
                + $"can hit {can}, lean cells [{string.Join(", ", leans.Select(x => MCell(x, map)))}], line {(found ? MCell(mLine.Source, map) + " -> " + MCell(mLine.Dest, map) : "none")}; "
                + $"range {(cross ? Range3D.Reach(range, ks, kt) : range):0.0} vs {(cross ? "3D " : "")}distance from the root {dRoot:0.00}, from the line's source {dLean:0.00}{old}");
            Result($"{c.name}: {(c.expect ? "can" : "cannot")} hit", can == c.expect, $"can hit {can}, line {(found ? MCell(mLine.Source, map) : "none")}");
            if (c.expect && mLeans.TryGetValue(c.spot, out IntVec3 lean))
            {
                Result($"{c.name}: vanilla's shoot line leaves from the lean cell {MCell(lean, map)}", found && mLine.Source == lean, $"line from {(found ? MCell(mLine.Source, map) : "none")}");
            }
            if (!c.auto)
            {
                Job j = JobMaker.MakeJob(JobDefOf.AttackStatic, t);
                j.playerForced = true;
                bool ok = mShooter.jobs.TryTakeOrderedJob(j, JobTag.DraftedOrder);
                Info($"{c.name}: attack order {ok}");
            }
            mProj = null;
            mLaunchesAt = MLaunches(mShooter);
            Pawn shooter = mShooter;
            if (!c.expect)
            {
                TickUntil(600, () => MLaunches(shooter) > mLaunchesAt, "m_noshot", 1);
                return;
            }
            TickUntil(900, () =>
            {
                mProj = MidFlightOf(map, shooter, shooter, cross);
                return mProj != null;
            }, "m_frozen", 1);
        }

        private static void LogLeanShot(Map map, LeanCase c)
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
            // the lean cell's centre over the ground, for comparison (vanilla launches from the shooter's DrawPos)
            Vector3 leanAt = spawn.IsValid ? OverGround(spawn.ToVector3Shifted(), 0f, map) : Vector3.zero;
            Info($"{c.name}: projectile {p.def.defName} spawned at {MCell(spawn, map)} (the shoot line's source {MCell(mLine.Source, map)}), floor {ko + 1} -> {(cross ? x.to + 1 : ko + 1)}; "
                + $"drawn start {start.x:0.00},{start.y:0.00},{start.z:0.00} vs muzzle of {mShooter} {muzzle.x:0.00},{muzzle.y:0.00},{muzzle.z:0.00} (off {off:0.000}); "
                + $"shooter centre {og.x:0.00},{og.z:0.00}, lean cell centre {leanAt.x:0.00},{leanAt.z:0.00}");
            Result($"{c.name}: projectile spawned on the shoot line's source cell (vanilla)", spawn == mLine.Source, $"spawn {MCell(spawn, map)}, line source {MCell(mLine.Source, map)}");
            Result($"{c.name}: drawn leaving from the shooter's muzzle, as vanilla draws from the shooter (within 0.05 cells)", off < 0.05f, $"off {off:0.000}");
        }

        // Nobody shoots through a floor, leaning or not: a sealed room on the ground under the slab (walls x cx-6..cx-2,
        // z zs..zs+4) with a rifle raider on each inner cell, against colonist A on every slab cell of floor 2, both ways
        // (can hit, and the target finder's sight, Heights.CanSeeLeaning).
        private static void LeanSweep(Map map)
        {
            IntVec3 inner0 = mSpots["RoomMin"];
            Pawn r = LeanRaider(map, inner0, "Gun_AssaultRifle");
            mA.equipment.DestroyAllEquipment();
            mA.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_AssaultRifle")));
            mA.drafter.Drafted = true;
            mA.drafter.FireAtWill = false;
            Verb rv = r.equipment.PrimaryEq.PrimaryVerb;
            Verb av = mA.equipment.PrimaryEq.PrimaryVerb;
            List<IntVec3> slab = new CellRect(mSpots["SlabMin"].x, mSpots["SlabMin"].z, 11, 9).Cells.Where(c => c.Standable(map)).ToList();
            int pairs = 0;
            List<string> bad = new List<string>();
            foreach (IntVec3 rc in new CellRect(inner0.x, inner0.z, 3, 3))
            {
                Teleport(r, rc);
                foreach (IntVec3 sc in slab)
                {
                    Teleport(mA, sc);
                    pairs++;
                    bool up = rv.CanHitTarget(mA), down = av.CanHitTarget(r), seeUp = Heights.CanSeeLeaning(r, mA), seeDown = Heights.CanSeeLeaning(mA, r);
                    if ((up || down || seeUp || seeDown) && bad.Count < 12)
                    {
                        bad.Add($"{MCell(rc, map)} vs {MCell(sc, map)}: up {up}, down {down}, sees up {seeUp}, sees down {seeDown}");
                    }
                }
            }
            Result($"nobody shoots or sees through the floor: raider in a sealed room under the slab vs the colonist on every slab cell ({pairs} pairs, both ways)", bad.Count == 0, string.Join("; ", bad));
            r.Destroy();
            mRaiders.Remove(r);
        }

        private static void EndLeanCase(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            foreach (Pawn p in new[] { mA, mB })
            {
                p.jobs.StopAll();
                p.drafter.FireAtWill = false;
            }
            if (mShooter != null && mRaiders.Contains(mShooter) && !mShooter.Destroyed)
            {
                mShooter.Destroy();
            }
            StartTicks(200, "m_settle");
        }

        private static void LeanHeal(Map map)
        {
            foreach (Pawn p in new[] { mA, mB })
            {
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

        private static Pawn LeanRaider(Map map, IntVec3 c, string gun)
        {
            Faction f = Find.FactionManager.AllFactions.Where(x => x.HostileTo(Faction.OfPlayer) && x.def.humanlikeFaction && !x.def.hidden && !x.temporary && x.def.pawnGroupMakers != null).OrderBy(x => x.loadID).First();
            Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Mercenary_Gunner"), f, forceGenerateNewPawn: true));
            p.equipment.DestroyAllEquipment();
            if (gun != null)
            {
                p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(gun)));
            }
            p.apparel?.DestroyAll();
            GenSpawn.Spawn(p, c, map);
            LordMaker.MakeNewLord(f, new LordJob_DefendPoint(c, 0.9f, 0.9f, false, false), map, new[] { p });
            mRaiders.Add(p);
            return p;
        }

        private static void SetupLean(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            List<Pawn> cols = Colonists(map).Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)).ToList();
            for (int i = 0; i < 3 && cols.Count < 2; i++)
            {
                Pawn extra = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true, mustBeCapableOfViolence: true));
                GenSpawn.Spawn(extra, CellFinder.RandomClosewalkCellNear(map.Center, map, 6), map);
                Info($"added colonist {extra} (the scenario's could not all shoot)");
                cols = Colonists(map).Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)).ToList();
            }
            if (an == null || an.floors < 2 || cols.Count < 2)
            {
                Result("lean setup", false, $"needs 2 floors, 2 colonists able to shoot; floors {an?.floors}, colonists {cols.Count}");
                Finish();
                return;
            }
            // a 23x34 open field, the one with the fewest cells to fix
            CellRect found = CellRect.Empty;
            int bestBad = int.MaxValue;
            for (int z = 3; z + 34 < an.groundZ - 3; z += 4)
            {
                for (int x = 3; x + 23 < map.Size.x - 3; x += 4)
                {
                    CellRect r = new CellRect(x, z, 23, 34);
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
                Result("lean field", false, "map too small");
                Finish();
                return;
            }
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
            DebugSettings.enableRandomMentalStates = false;
            int cx = found.CenterCell.x;
            int zs = found.minZ + 10;
            int ze = zs + 8;
            int zb = zs - 6;
            // the slab on floor 2: x cx-6..cx+4, z zs..ze; east of cx it ends a row short (the notch)
            foreach (IntVec3 g in new CellRect(cx - 6, zs, 11, 9))
            {
                if (g.x > cx && g.z == ze)
                {
                    continue;
                }
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                IntVec3 up = ProjectZ.CellOnFloor(g, 1, map);
                map.terrainGrid.SetTerrain(up, PZDefOf.PZ_UpperFloor);
            }
            foreach (IntVec3 g in new CellRect(cx - 8, zs - 2, 15, 13))
            {
                map.fogGrid.Unfog(ProjectZ.CellOnFloor(g, 1, map));
            }
            IntVec3 Up(int x, int z) => ProjectZ.CellOnFloor(new IntVec3(x, 0, z), 1, map);
            for (int x = cx - 2; x <= cx; x++)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, Up(x, ze), map);         // the protruding edge's wall
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, new IntVec3(x, 0, zb + 1), map);   // the ground corner
            }
            for (int x = cx - 6; x <= cx - 4; x++)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, Up(x, zs + 3), map);     // floor 2's inner corner (control)
            }
            for (int x = cx - 8; x <= cx - 6; x++)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, new IntVec3(x, 0, ze + 5), map);   // a ground corner north of the slab
            }
            mSpots.Clear();
            mLeans.Clear();
            // a sealed room on the ground under the slab's west part (walls round x cx-6..cx-2, z zs..zs+4)
            foreach (IntVec3 g in new CellRect(cx - 6, zs, 5, 5).EdgeCells)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, g, map);
            }
            mSpots["RoomMin"] = new IntVec3(cx - 5, 0, zs + 1);
            mSpots["SlabMin"] = Up(cx - 6, zs);
            mSpots["AL"] = Up(cx, ze - 1);
            mSpots["AIn"] = Up(cx, ze - 2);
            mSpots["G"] = new IntVec3(cx + 2, 0, ze + 8);
            mSpots["BL"] = new IntVec3(cx, 0, zb);
            mSpots["UE"] = Up(cx + 2, zs);
            mSpots["GC"] = new IntVec3(cx + 1, 0, zb + 4);
            mSpots["FL"] = Up(cx - 4, zs + 2);
            mSpots["FT"] = Up(cx - 3, zs + 7);
            mSpots["BN"] = new IntVec3(cx - 6, 0, ze + 6);
            mSpots["UN"] = Up(cx - 4, ze);
            mLeans["BN"] = new IntVec3(cx - 5, 0, ze + 6);
            mLeans["AL"] = Up(cx + 1, ze - 1);
            mLeans["BL"] = new IntVec3(cx + 1, 0, zb);
            mLeans["FL"] = Up(cx - 3, zs + 2);
            cols = cols.OrderByDescending(p => p.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0).ToList();
            mA = cols[0];
            mB = cols[1];
            // a sealed hut far from the field for everyone not in a case
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
            List<Pawn> others = Colonists(map).Where(p => p != mA && p != mB).ToList();
            for (int i = 0; i < others.Count; i++)
            {
                others[i].drafter.Drafted = true;
                others[i].drafter.FireAtWill = false;
                if (mHut.IsValid)
                {
                    Teleport(others[i], mHut + GenAdj.AdjacentCellsAndInside[i % 9]);
                }
            }
            MPatchLaunch();
            Info($"field {found} (centre x {cx}); slab on floor 2 x {cx - 6}..{cx + 4}, z {zs}..{ze} (east of x {cx} to {ze - 1}); edge wall x {cx - 2}..{cx} at z {ze} (floor 2); ground wall x {cx - 2}..{cx} at z {zb + 1}; "
                + $"A {mA}, B {mB}; hut {mHut}; storey {IsoRimBridge.StoreyHeight:0.00}");
            mOldYaw = (float)IsoRimBridge.SetOption("freeYaw", 0f);
            mOldMin = Find.CameraDriver.config.sizeRange.min;
            mCase = 0;
            Wait(10, "m_sweep");
        }
    }
}
