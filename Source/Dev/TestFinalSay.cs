using System;
using System.Collections.Generic;
using System.Linq;
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
    // Test only: a hostile mod (dev switch, patched in by the finalsay mode only). On each hook it is switched on for, its
    // prefix (Priority.First, skipping the original) and an early postfix (Priority.First) write a wrong result. Path and
    // reach lie only for the pawn's own requests (not inside ProjectZ's stair router, which asks for legs on one floor).
    public static class Hostile
    {
        public static bool CanHit, See, Sight, Range, Intercept, Reach, Path;
        public static bool CanHitValue, SeeValue, SightValue, RangeValue, InterceptValue;
        private static bool patched;

        public static void AllOff()
        {
            CanHit = See = Sight = Range = Intercept = Reach = Path = false;
        }

        public static void Patch()
        {
            if (patched)
            {
                return;
            }
            patched = true;
            Harmony h = new Harmony("Peren.IsoRimProjectZ.test.hostile");
            void Both(System.Reflection.MethodBase m, string pre, string post)
            {
                h.Patch(m, prefix: pre == null ? null : new HarmonyMethod(AccessTools.Method(typeof(Hostile), pre)) { priority = Priority.First },
                    postfix: post == null ? null : new HarmonyMethod(AccessTools.Method(typeof(Hostile), post)) { priority = Priority.First });
            }
            Both(AccessTools.Method(typeof(Verb), "CanHitCellFromCellIgnoringRange"), nameof(CanHitPre), nameof(CanHitPost));
            Both(AccessTools.Method(typeof(AttackTargetFinder), nameof(AttackTargetFinder.CanSee)), nameof(SeePre), nameof(SeePost));
            Both(AccessTools.Method(typeof(Verb), nameof(Verb.OutOfRange)), nameof(RangePre), nameof(RangePost));
            Both(AccessTools.Method(typeof(GenSight), nameof(GenSight.LineOfSight), new[] { typeof(IntVec3), typeof(IntVec3), typeof(Map), typeof(bool), typeof(Func<IntVec3, bool>), typeof(int), typeof(int) }), nameof(SightPre), nameof(SightPost));
            Both(AccessTools.Method(typeof(Projectile), "CheckForFreeIntercept"), nameof(InterceptPre), nameof(InterceptPost));
            Both(AccessTools.Method(typeof(Reachability), "GetCachedResult"), nameof(CachePre), nameof(CachePost));
            Both(AccessTools.Method(typeof(Reachability), "CheckRegionBasedReachability"), nameof(RegionPre), nameof(RegionPost));
            Both(AccessTools.Method(typeof(PathFinder), nameof(PathFinder.FindPathNow), new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathFinderCostTuning?), typeof(PathEndMode), typeof(PathRequest.IPathGridCustomizer) }), nameof(PathPre), nameof(PathPost));
            Both(AccessTools.Method(typeof(Pawn_PathFollower), "GenerateNewPathRequest"), null, nameof(RequestPost));
        }

        public static bool CanHitPre(ref bool __result) { if (!CanHit) return true; __result = CanHitValue; return false; }
        public static void CanHitPost(ref bool __result) { if (CanHit) __result = CanHitValue; }
        public static bool SeePre(ref bool __result) { if (!See) return true; __result = SeeValue; return false; }
        public static void SeePost(ref bool __result) { if (See) __result = SeeValue; }
        public static bool RangePre(ref bool __result) { if (!Range) return true; __result = RangeValue; return false; }
        public static void RangePost(ref bool __result) { if (Range) __result = RangeValue; }
        public static bool SightPre(ref bool __result) { if (!Sight) return true; __result = SightValue; return false; }
        public static void SightPost(ref bool __result) { if (Sight) __result = SightValue; }
        public static bool InterceptPre(ref bool __result) { if (!Intercept) return true; __result = InterceptValue; return false; }
        public static void InterceptPost(ref bool __result) { if (Intercept) __result = InterceptValue; }

        private static bool Lying => !StairRouter.busy;

        public static bool CachePre(ref BoolUnknown __result) { if (!Reach || !Lying) return true; __result = BoolUnknown.False; return false; }
        public static void CachePost(ref BoolUnknown __result) { if (Reach && Lying) __result = BoolUnknown.False; }
        public static bool RegionPre(ref bool __result) { if (!Reach || !Lying) return true; __result = false; return false; }
        public static void RegionPost(ref bool __result) { if (Reach && Lying) __result = false; }
        public static bool PathPre(ref PawnPath __result) { if (!Path || !Lying) return true; __result = PawnPath.NotFound; return false; }
        public static void PathPost(ref PawnPath __result) { if (Path && Lying) __result = PawnPath.NotFound; }

        private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> pawnRef = AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, PathEndMode> modeRef = AccessTools.FieldRefAccess<Pawn_PathFollower, PathEndMode>("peMode");

        // the pawn's path request answered at once with "no path"
        public static void RequestPost(Pawn_PathFollower __instance, ref PathRequest __result)
        {
            if (!Path || __result == null)
            {
                return;
            }
            Pawn pawn = pawnRef(__instance);
            Map map = pawn?.Map;
            if (map == null)
            {
                return;
            }
            PathRequest pushed = __result;
            PathRequest answered = map.pathFinder.CreateRequest(__instance.nextCell, __instance.Destination, null, pawn, PathFinderCostTuning.For(pawn), modeRef(__instance));
            pushed.Dispose();
            answered.Resolve(null);
            __result = answered;
        }
    }

    // Modes finalsay and lobbed (run with -projectzfloors=3 -projectzseed=N; the los3d field, LReset before every check,
    // each check under its own random state).
    //   finalsay: ProjectZ has the final say between floors (FinalSay). With the hostile mod (Hostile) lying on sight,
    //     hit, range, line of sight, interception, reach and paths: results between floors stay ProjectZ's, results on one
    //     floor show the hostile value. Real shots (an order, the AI), real interceptions (sandbags), a real walk upstairs
    //     over stairs; CanHitCell timed with and without the final-say postfix.
    //   lobbed: vanilla mortars between floors (target picked behind walls, CanHitCell, shells landing on the top of their
    //     column), then a test-only lobbed-shot rule (Api.RegisterLobbedShot) for the recurve bow: lobbed over a wall
    //     hits, a slab over the target stops it, direct shots unaffected, a flyOverhead arrow declared direct flies direct.
    public static partial class TestRunner
    {
        private sealed class FImp
        {
            public Thing launcher, hit, used, intended;
            public IntVec3 g, at, dest;
            public int floor;
            public bool flight, lobbed, cross;
        }

        private static readonly List<FImp> fImps = new List<FImp>();
        private static Action<Map> fThen;
        private static bool fPatched;

        private static readonly AccessTools.FieldRef<Projectile, Vector3> fDestRef = AccessTools.FieldRefAccess<Projectile, Vector3>("destination");

        private static void FImpactPrefix(Projectile __instance, Thing hitThing)
        {
            Map map = __instance.Map;
            if (map == null)
            {
                return;
            }
            fImps.Add(new FImp
            {
                launcher = __instance.Launcher,
                hit = hitThing,
                used = __instance.usedTarget.Thing,
                intended = __instance.intendedTarget.Thing,
                g = ProjectZ.GroundCellOf(__instance.Position, map),
                at = __instance.Position,
                dest = fDestRef(__instance).ToIntVec3(),
                floor = ProjectZ.FloorOf(__instance.Position, map),
                flight = Flights.Of(__instance) != null,
                lobbed = Lobbed.Of(__instance),
                cross = Flights.cross.TryGetValue(__instance, out _)
            });
        }

        private static void FPatch()
        {
            if (fPatched)
            {
                return;
            }
            fPatched = true;
            Harmony h = new Harmony("Peren.IsoRimProjectZ.test.finalsay");
            h.Patch(AccessTools.Method(typeof(Projectile), "Impact"), prefix: new HarmonyMethod(typeof(TestRunner), nameof(FImpactPrefix)));
            // (shells: Projectile_Explosive.Impact explodes without the base Impact)
            h.Patch(AccessTools.Method(typeof(Projectile_Explosive), "Impact"), prefix: new HarmonyMethod(typeof(TestRunner), nameof(FImpactPrefix)));
            h.Patch(AccessTools.Method(typeof(Projectile), "ImpactSomething"), prefix: new HarmonyMethod(typeof(TestRunner), nameof(FArrowOffPrefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(TestRunner), nameof(FArrowOffFinalizer)));
            if (mode == "finalsay")
            {
                Hostile.Patch();
            }
            Api.RegisterLobbedShot(FRule);
        }

        // ---- the test-only lobbed rule: the recurve bow, as switched
        private enum Lob { Off, Lobbed, Direct }

        private static Lob fLob;

        private static bool? FRule(Verb v, IntVec3 from, IntVec3 to)
        {
            if (fLob == Lob.Off || v?.EquipmentSource == null || v.EquipmentSource.def.defName != "Bow_Recurve")
            {
                return null;
            }
            return fLob == Lob.Lobbed;
        }

        // ---- the arrow as a flyOverhead def (as some mods' arrows): flyOverhead on while it flies, off while it hits
        // (the mod's own ImpactSomething does that), set only by the check that needs it
        private static ThingDef fArrowDef;
        private static bool fArrowOverhead;

        private static void FArrowOffPrefix(Projectile __instance, out bool __state)
        {
            __state = fArrowOverhead && __instance.def == fArrowDef;
            if (__state)
            {
                __instance.def.projectile.flyOverhead = false;
            }
        }

        private static void FArrowOffFinalizer(Projectile __instance, bool __state)
        {
            if (__state)
            {
                __instance.def.projectile.flyOverhead = true;
            }
        }

        private static void FArrowOverhead(bool on)
        {
            fArrowDef = DefDatabase<ThingDef>.GetNamed("Arrow_Recurve");
            fArrowOverhead = on;
            fArrowDef.projectile.flyOverhead = on;
        }

        // ---- flow
        private static void FTicks(int max, Func<bool> cond, Action<Map> then)
        {
            fThen = then;
            TickUntil(max, cond, "m_fs_then", 1);
        }

        private static bool StepFinalSay(Map map)
        {
            if (stage != "m_fs_then")
            {
                return false;
            }
            tickWatch = null;
            Action<Map> a = fThen;
            fThen = null;
            stage = "m_fs_run";
            a?.Invoke(map);
            if (stage == "m_fs_run")
            {
                LEndShot(map);
            }
            return true;
        }

        private static void FEnd()
        {
            Hostile.AllOff();
            fLob = Lob.Off;
            if (fArrowDef != null)
            {
                FArrowOverhead(false);
            }
        }

        private static List<FImp> FImpsOf(Thing launcher, int from) => fImps.Skip(from).Where(i => i.launcher == launcher).ToList();

        private static string FImpList(List<FImp> l) => string.Join("; ", l.Take(12).Select(i => $"{(i.hit == null ? "-" : i.hit.def.defName)}{(i.used != null && i.hit != null && i.hit != i.used ? "(int)" : "")} at {i.g} f{i.floor + 1}{(i.flight ? " flight" : "")}{(i.lobbed ? " lobbed" : "")}"));

        private static Verb FVerb(Pawn p) => p.equipment.PrimaryEq.PrimaryVerb;

        private static bool FCanHit(Pawn p, Thing t) => FVerb(p).CanHitTarget(t);

        // A shooter's order (the float menu's attack option) and its shots: then(map, impacts) after want impacts or max ticks.
        private static void FShots(string name, Pawn shooter, Thing target, int want, int max, Action<Map, List<FImp>> then)
        {
            int b = fImps.Count;
            bool ok = LOrder(shooter, target, out string label, out _);
            Find.Selector.ClearSelection();
            Result($"{name}: the order is taken", ok && shooter.CurJobDef == JobDefOf.AttackStatic, $"'{label}', job {shooter.CurJobDef?.defName}");
            if (!ok)
            {
                return;
            }
            FTicks(max, () => FImpsOf(shooter, b).Count >= want || target.Destroyed || (target as Pawn)?.Dead == true, m => then(m, FImpsOf(shooter, b)));
        }

        // ------------------------------------------------------------------ finalsay

        private static void BuildFinalSayChecks(Action<string, Action<Map>> add)
        {
            FPatch();
            add("fs_sight", FsSight);
            add("fs_shoot", FsShoot);
            add("fs_ai", FsAi);
            add("fs_intercept_cross", m => FsIntercept(m, true, true));
            add("fs_intercept_flat_hostile", m => FsIntercept(m, false, true));
            add("fs_intercept_flat_plain", m => FsIntercept(m, false, false));
            add("fs_path", FsPath);
            add("fs_perf", FsPerf);
        }

        // the sight fixture: the shot slab; A on it (row 3); R1 on the ground in its view, R2 under the slab; on the
        // ground B, R3 in its view, R4 behind a wall
        private static Pawn fR1, fR2, fR3, fR4;

        private static void FsSightFixture(Map map)
        {
            LShotSlab();
            LPut(LGun(mA), LU(20, 22));
            fR1 = LeanRaider(map, LG(19, 18), null);
            fR2 = LeanRaider(map, LG(20, 24), null);
            LPut(LGun(mB), LG(5, 5));
            fR3 = LeanRaider(map, LG(5, 12), null);
            for (int z = 1; z <= 9; z++)
            {
                LWall(LG(8, z));
            }
            fR4 = LeanRaider(map, LG(11, 5), null);
        }

        private static void FsSight(Map map)
        {
            FsSightFixture(map);
            Hostile.AllOff();
            Verb va = FVerb(mA), vb = FVerb(mB);
            bool c1 = FCanHit(mA, fR1), c2 = FCanHit(mA, fR2), s3 = FCanHit(mB, fR3), s4 = FCanHit(mB, fR4);
            Result("fs_sight fixture (no hostile): cross clear, cross blocked, flat clear, flat blocked", c1 && !c2 && s3 && !s4, $"A->R1 {c1}, A->R2 {c2}, B->R3 {s3}, B->R4 {s4}");
            int re = FinalSay.Reasserted, sk = FinalSay.PrefixSkipped;
            // hostile says yes to everything
            Hostile.CanHit = Hostile.See = Hostile.Sight = true;
            Hostile.CanHitValue = Hostile.SeeValue = Hostile.SightValue = true;
            bool hc2 = FCanHit(mA, fR2), hs2 = AttackTargetFinder.CanSee(mA, fR2), hl2 = GenSight.LineOfSight(mA.Position, fR2.Position, map, false, null, 0, 0);
            bool hs4 = FCanHit(mB, fR4), hv4 = AttackTargetFinder.CanSee(mB, fR4), hl4 = GenSight.LineOfSight(mB.Position, fR4.Position, map, false, null, 0, 0);
            Result("fs_sight hostile 'yes': between floors ProjectZ's no stands (can hit, can see, flat line of sight between bands)", !hc2 && !hs2 && !hl2, $"A->R2 can hit {hc2}, can see {hs2}, GenSight {hl2}");
            Result("fs_sight hostile 'yes': on one floor the hostile value shows", hs4 && hv4 && hl4, $"B->R4 can hit {hs4}, can see {hv4}, GenSight {hl4}");
            // hostile says no to everything
            Hostile.CanHitValue = Hostile.SeeValue = Hostile.SightValue = false;
            bool hc1 = FCanHit(mA, fR1), hs1 = AttackTargetFinder.CanSee(mA, fR1);
            bool hs3 = FCanHit(mB, fR3), hv3 = AttackTargetFinder.CanSee(mB, fR3), hl3 = GenSight.LineOfSight(mB.Position, fR3.Position, map, false, null, 0, 0);
            Result("fs_sight hostile 'no': between floors ProjectZ's yes stands (can hit, can see)", hc1 && hs1, $"A->R1 can hit {hc1}, can see {hs1}");
            Result("fs_sight hostile 'no': on one floor the hostile value shows", !hs3 && !hv3 && !hl3, $"B->R3 can hit {hs3}, can see {hv3}, GenSight {hl3}");
            Hostile.AllOff();
            // hostile range: everything out of range
            Hostile.Range = true;
            Hostile.RangeValue = true;
            bool r1 = va.OutOfRange(mA.Position, fR1, fR1.OccupiedRect()), r3 = vb.OutOfRange(mB.Position, fR3, fR3.OccupiedRect());
            Result("fs_sight hostile range 'out of range': between floors in range (ProjectZ), on one floor out of range (hostile)", !r1 && r3, $"A->R1 out of range {r1}, B->R3 out of range {r3}");
            Hostile.AllOff();
            Result("fs_sight: with a Priority.First prefix returning false, our prefix was skipped (Harmony) and the postfix decided", FinalSay.PrefixSkipped > sk, $"postfix decided alone {FinalSay.PrefixSkipped - sk} times, put ProjectZ's result back {FinalSay.Reasserted - re} times");
        }

        private static void FHostileNoShots()
        {
            Hostile.CanHit = Hostile.See = Hostile.Sight = Hostile.Range = true;
            Hostile.CanHitValue = Hostile.SeeValue = Hostile.SightValue = false;
            Hostile.RangeValue = true;
        }

        // real shots under a hostile mod saying "can't hit, can't see, out of range"
        private static void FsShoot(Map map)
        {
            FsSightFixture(map);
            FHostileNoShots();
            string label;
            bool flat = LOrder(mB, fR3, out label, out _);
            Find.Selector.ClearSelection();
            Result("fs_shoot: on one floor the hostile mod refuses the order (B at R3 in clear view)", !flat, $"'{label}'");
            mB.jobs.StopAll();
            mShooter = mA;
            mTarget = fR1;
            FShots("fs_shoot between floors (A on the slab at R1 on the ground)", mA, fR1, 4, 2000, (m, l) =>
            {
                IntVec3 tg = ProjectZ.GroundCellOf(fR1.Position, m);
                int near = l.Count(i => i.floor == 0 && (i.g - tg).LengthHorizontal <= 1.5f);
                Result("fs_shoot between floors: shots land at the target's column on the ground", l.Count >= 4 && near > 0, $"impacts {l.Count}, at the column {near}, hits {l.Count(i => i.hit == fR1)}: {FImpList(l)}");
                Hostile.AllOff();
            });
        }

        // the AI: a raider on the ground with its own lord shoots colonist A upstairs, the hostile mod saying no
        private static void FsAi(Map map)
        {
            LShotSlab();
            LPut(mA, LU(20, 21));
            Pawn r = LeanRaider(map, LG(20, 13), "Gun_AssaultRifle");
            mShooter = r;
            mTarget = mA;
            FHostileNoShots();
            Info($"fs_ai: raider can hit A {FCanHit(r, mA)}, sees {AttackTargetFinder.CanSee(r, mA)} (hostile on)");
            int b = fImps.Count;
            FTicks(2000, () => FImpsOf(r, b).Any(i => i.cross), m =>
            {
                List<FImp> l = FImpsOf(r, b);
                Result("fs_ai: the raider picks and shoots colonist A upstairs on its own (hostile mod saying no)", l.Any(i => i.cross), $"impacts {l.Count} (cross {l.Count(i => i.cross)}) in {ticksTotal} ticks; job {r.CurJobDef?.defName}");
                Hostile.AllOff();
            });
        }

        // Shots at a ground cell right behind a row of sandbags (an attack-ground order: the game lets such shots hit
        // things on the way, and sandbags next to the destination stop them at their fill, 65%). The hostile mod says
        // "never intercepted". cross: A on the slab's edge, the cell 12 out on the ground; flat: B on the ground, the cell
        // 12 off. Interceptions: impacts on a sandbag on the way (not at the shot's destination).
        private static void FsIntercept(Map map, bool cross, bool hostile)
        {
            Pawn shooter;
            IntVec3 cell;
            if (cross)
            {
                LShotSlab();
                shooter = LPut(LGun(mA), LU(20, 20));
                cell = LG(20, 8);
                for (int x = 19; x <= 21; x++)
                {
                    Place(ThingDefOf.Sandbags, null, LG(x, 9), map);
                }
            }
            else
            {
                shooter = LPut(LGun(mB), LG(30, 2));
                cell = LG(30, 14);
                for (int x = 29; x <= 31; x++)
                {
                    Place(ThingDefOf.Sandbags, null, LG(x, 13), map);
                }
            }
            Hostile.Intercept = hostile;
            Hostile.InterceptValue = false;
            string name = $"fs_intercept {(cross ? "between floors" : "on one floor")} {(hostile ? "with the hostile mod" : "without it")}";
            Job j = JobMaker.MakeJob(JobDefOf.AttackStatic, cell);
            j.playerForced = true;
            bool ok = shooter.jobs.TryTakeOrderedJob(j, JobTag.DraftedOrder);
            Result($"{name}: the attack-ground order is taken", ok && shooter.CurJobDef == JobDefOf.AttackStatic, $"job {shooter.CurJobDef?.defName}, can hit the cell {FVerb(shooter).CanHitTarget(cell)}");
            int b = fImps.Count;
            FTicks(4000, () => FImpsOf(shooter, b).Count >= 15, m =>
            {
                List<FImp> l = FImpsOf(shooter, b);
                int inter = l.Count(i => i.hit != null && i.hit.def == ThingDefOf.Sandbags && i.at != i.dest);
                string d = $"impacts {l.Count}, interceptions by the sandbags {inter}: {FImpList(l)}";
                if (cross || !hostile)
                {
                    Result($"{name}: the sandbags intercept shots ({(cross ? "ProjectZ's flight decides" : "the game")})", l.Count >= 10 && inter > 0, d);
                }
                else
                {
                    Result($"{name}: never intercepted (the hostile value shows on one floor)", l.Count >= 10 && inter == 0, d);
                }
                Hostile.AllOff();
            });
        }

        // Paths and reach: the hostile mod answers the pawn's own reach checks "no" and its path requests "no path". B walks
        // over stairs onto the floor above (ProjectZ's answers stand); B can't walk into a room through its door on the
        // ground (the hostile answers show).
        private static void FsPath(Map map)
        {
            // slab x 28..36, z 33..39 on floor 2, held by ground walls under its north row; stairs up at its middle
            IntVec3 stairs = LG(32, 34);
            HashSet<IntVec3> well = new HashSet<IntVec3>(StairShape.Footprint(stairs, Rot4.North).Cells);
            foreach (int x in new[] { 28, 32, 36 })
            {
                LWall(LG(x, 39));
            }
            for (int x = 28; x <= 36; x++)
            {
                for (int z = 33; z <= 39; z++)
                {
                    if (!well.Contains(LG(x, z)))
                    {
                        lMap.roofGrid.SetRoof(LG(x, z), RoofDefOf.RoofConstructed);
                        lMap.terrainGrid.SetTerrain(LU(x, z), PZDefOf.PZ_UpperFloor);
                    }
                }
            }
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, stairs, map, Rot4.North);
            // a ground room x 4..10, z 30..36, door at (7, 30)
            foreach (IntVec3 c in CellRect.FromLimits(LG(4, 30), LG(10, 36)).EdgeCells)
            {
                Place(c == LG(7, 30) ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            IntVec3 up = LU(34, 38), room = LG(7, 33);
            Pawn b = LPut(mB, LG(32, 28));
            bool upOk = map.reachability.CanReach(b.Position, up, PathEndMode.OnCell, TraverseParms.For(b));
            bool roomOk = map.reachability.CanReach(b.Position, room, PathEndMode.OnCell, TraverseParms.For(b));
            Result("fs_path fixture (no hostile): B can reach upstairs and the room", upOk && roomOk, $"upstairs {upOk}, room {roomOk}");
            Hostile.Reach = Hostile.Path = true;
            int re = FinalSay.Reasserted, sk = FinalSay.PrefixSkipped;
            bool hUp = map.reachability.CanReach(b.Position, up, PathEndMode.OnCell, TraverseParms.For(b));
            bool hRoom = map.reachability.CanReach(b.Position, room, PathEndMode.OnCell, TraverseParms.For(b));
            PawnPath pUp = map.pathFinder.FindPathNow(b.Position, up, b);
            PawnPath pRoom = map.pathFinder.FindPathNow(b.Position, room, b);
            Result("fs_path hostile: upstairs reachable with a path (ProjectZ), the room not reachable, no path (hostile)", hUp && pUp.Found && !hRoom && !pRoom.Found,
                $"upstairs reach {hUp} path {pUp.Found}, room reach {hRoom} path {pRoom.Found}; reasserted {FinalSay.Reasserted - re}");
            if (pUp.Found)
            {
                pUp.Dispose();
            }
            // the room first: B is ordered in and never gets there
            Pawn bb = b;
            Teleport(bb, LG(7, 25));
            bb.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, room), JobTag.DraftedOrder);
            FTicks(600, () => bb.Position == room, m =>
            {
                Result("fs_path hostile: on one floor B never walks into the room (the hostile answers show)", bb.Position != room, $"B at {MCell(bb.Position, m)}, job {bb.CurJobDef?.defName}");
                Teleport(bb, LG(32, 28));
                bb.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, up), JobTag.DraftedOrder);
                FTicks(3000, () => bb.Position == up, m2 =>
                {
                    Result("fs_path hostile: B walks over the stairs onto the floor above (ProjectZ's reach and path stand)", bb.Position == up, $"B at {MCell(bb.Position, m2)} in {ticksTotal} ticks, job {bb.CurJobDef?.defName}");
                    Hostile.AllOff();
                });
            });
        }

        // CanHitCell timed (per call, same floor and between floors) with the final-say postfix, then without it.
        private static void FsPerf(Map map)
        {
            FsSightFixture(map);
            Hostile.AllOff();
            System.Reflection.MethodInfo m = AccessTools.Method(typeof(Verb), "CanHitCellFromCellIgnoringRange");
            Func<Verb, IntVec3, IntVec3, bool, bool> call = AccessTools.MethodDelegate<Func<Verb, IntVec3, IntVec3, bool, bool>>(m, null, true);
            Verb va = FVerb(mA), vb = FVerb(mB);
            IntVec3 a = mA.Position, r1 = fR1.Position, b = mB.Position, r3 = fR3.Position;
            const int n = 200000;
            double flatWith = LMicro(n, () => call(vb, b, r3, false));
            double crossWith = LMicro(n / 10, () => call(va, a, r1, false));
            System.Reflection.MethodInfo post = AccessTools.Method(typeof(Verb_CanHitCell_Patch), "Postfix");
            Harmony pz = new Harmony("Peren.IsoRimProjectZ");
            pz.Unpatch(m, post);
            double flatWithout = LMicro(n, () => call(vb, b, r3, false));
            double crossWithout = LMicro(n / 10, () => call(va, a, r1, false));
            pz.Patch(m, postfix: new HarmonyMethod(post) { priority = Priority.Last });
            bool back = Harmony.GetPatchInfo(m)?.Postfixes.Any(p => p.PatchMethod == post) == true;
            Info($"fs_perf CanHitCell per call: same floor {flatWithout:0.000} us without the final-say postfix, {flatWith:0.000} us with; between floors {crossWithout:0.000} us without, {crossWith:0.000} us with");
            Result("fs_perf: the final-say postfix costs under 0.05 us per CanHitCell call (same floor) and is patched back", flatWith - flatWithout < 0.05 && back, $"+{flatWith - flatWithout:0.000} us same floor, +{crossWith - crossWithout:0.000} us between floors; postfix back {back}");
        }

        // ------------------------------------------------------------------ lobbed

        private static void BuildLobbedChecks(Action<string, Action<Map>> add)
        {
            FPatch();
            add("lb_mortar_open_scatter", m => LbMortar(m, false, true));
            add("lb_mortar_open_aimed", m => LbMortar(m, false, false));
            add("lb_mortar_slab_over", m => LbMortar(m, true, false));
            add("lb_bow_over_wall", LbOverWall);
            add("lb_bow_slab_over", LbSlabOver);
            add("lb_direct_unaffected", LbDirect);
            add("lb_arrow_overhead_direct", m => LbOverheadArrow(m, Lob.Direct));
            add("lb_arrow_overhead_lobbed", m => LbOverheadArrow(m, Lob.Lobbed));
        }

        // A slab on floor k over field x0..x1, z0..z1, held by walls on the level below under its north row.
        private static void FSlabHeld(int x0, int z0, int x1, int z1, int k)
        {
            for (int x = x0; x <= x1; x += Math.Max(1, (x1 - x0) / 2))
            {
                IntVec3 c = k == 1 ? LG(x, z1) : LU(x, z1, k - 1);
                if (c.GetEdifice(lMap) == null)
                {
                    LWall(c);
                }
            }
            LSlab(x0, z0, x1, z1, k);
        }

        private static bool FOnTop(FImp i, Map map)
        {
            IntVec3 top = Column.Top(i.g, map);
            return top.IsValid && ProjectZ.FloorOf(top, map) == i.floor;
        }

        // Vanilla mortars between floors (nothing registered): a colonist mans a mortar on the ground; a raider stands on
        // a slab on floor 2 in a ring of walls (no line of sight from the ground). Open: the mortar picks it on its own
        // (sight and target picking), can hit it (CanHitCell), its shells land on the top of their columns (on the slab
        // over it). Slab over: a slab on floor 3 over the whole slab, the raider ordered as the forced target: every shell
        // lands on floor 3 over it, none on floor 2 under it.
        private static readonly AccessTools.FieldRef<VerbProperties, float> missRef = AccessTools.FieldRefAccess<VerbProperties, float>("forcedMissRadius");
        private static readonly AccessTools.FieldRef<VerbProperties, float> missClassicRef = AccessTools.FieldRefAccess<VerbProperties, float>("forcedMissRadiusClassicMortars");

        private static void LbMortar(Map map, bool slabOver, bool scatter)
        {
            FSlabHeld(22, 34, 30, 40, 1);
            IntVec3 rc = LU(26, 37);
            foreach (IntVec3 g in CellRect.FromLimits(LG(25, 36), LG(27, 38)).EdgeCells)
            {
                LWall(Heights.OnFloorOf(g, rc, map));
            }
            if (slabOver)
            {
                FSlabHeld(22, 34, 30, 40, 2);
            }
            Pawn r = LeanRaider(map, rc, "Gun_Revolver");   // armed: a threat to pick
            mTarget = r;
            Building_TurretGun mortar = (Building_TurretGun)Place(DefDatabase<ThingDef>.GetNamed("Turret_Mortar"), null, LG(3, 3), map);
            mortar.gun.TryGetComp<CompChangeableProjectile>()?.LoadShell(DefDatabase<ThingDef>.GetNamed("Shell_Smoke"), 10);
            Pawn crew = mB;
            LPut(crew, LG(4, 2));
            bool manned = crew.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.ManTurret, mortar), JobTag.DraftedOrder);
            Verb v = mortar.AttackVerb;
            // aimed and slab over: no scatter (forced miss radius 0, restored after), so every shell comes down over the
            // raider's column; the scatter check shows the misses landing on the tops of their columns
            float missWas = missRef(v.verbProps), missClassicWas = missClassicRef(v.verbProps);
            if (!scatter)
            {
                missRef(v.verbProps) = 0f;
                missClassicRef(v.verbProps) = 0f;
            }
            bool can = v.CanHitTarget(r), sees = Los3D.CanSee(mortar.Position, r.Position, map);
            string name = slabOver ? "lb_mortar_slab_over" : scatter ? "lb_mortar_open_scatter" : "lb_mortar_open_aimed";
            Result($"{name}: the mortar can hit the raider on floor 2 (CanHitCell) without a line of sight", can && !sees, $"can hit {can}, line of sight {sees}; man order {manned}");
            int b = fImps.Count;
            bool ordered = false;
            bool picked = false;
            List<string> seen = new List<string>();
            FTicks(9000, () =>
            {
                if (mortar.CurrentTarget.IsValid && !seen.Contains(mortar.CurrentTarget.ToString()))
                {
                    seen.Add(mortar.CurrentTarget.ToString());
                }
                if (mortar.CurrentTarget.Thing == r || FImpsOf(crew, b).Any(i => i.intended == r))
                {
                    picked = true;
                }
                if (slabOver && !ordered && mortar.TryGetComp<CompMannable>()?.MannedNow == true)
                {
                    ordered = true;
                    mortar.OrderAttack(r);
                }
                return FImpsOf(crew, b).Count >= 3;
            }, m =>
            {
                List<FImp> l = FImpsOf(crew, b);
                CompChangeableProjectile shell = mortar.gun.TryGetComp<CompChangeableProjectile>();
                Info($"{name} diag: ticks {ticksTotal}, crew {crew} at {crew.Position} job {crew.CurJobDef?.defName}, manned {mortar.TryGetComp<CompMannable>()?.MannedNow}, active {mortar.Active}, verb available {mortar.AttackVerb.Available()}, loaded {shell?.Loaded} ({shell?.LoadedShell}), barrel {mortar.TryGetComp<CompRefuelable>()?.Fuel}, forced {mortar.ForcedTarget}, current {mortar.CurrentTarget}, roofed {mortar.Position.Roofed(m)}, verb state {mortar.AttackVerb.state}, all impacts by anyone since {fImps.Count - b}, raider {r} at {MCell(r.Position, m)} spawned {r.Spawned}; find now {mortar.TryFindNewTarget()}, best for the crew {AttackTargetFinder.BestShootTargetFromCurrentPosition(crew, TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable)?.Thing}");
                CellRect slab = CellRect.FromLimits(LG(22, 34), LG(30, 40));
                List<FImp> over = l.Where(i => slab.Contains(i.g)).ToList();
                bool tops = l.All(i => FOnTop(i, m));
                if (!slabOver)
                {
                    Result($"{name}: the manned mortar picks the raider on floor 2 behind walls on its own", picked, $"targets seen [{string.Join(", ", seen)}], manned {mortar.TryGetComp<CompMannable>()?.MannedNow}");
                    Result(scatter ? $"{name}: every shell (scattered by the forced miss) lands on the top of its column" : $"{name}: every shell lands at the raider's column on the slab (floor 2)",
                        l.Count >= 3 && tops && over.All(i => i.floor == 1) && (scatter || over.Count == l.Count),
                        $"impacts {l.Count} (over the slab {over.Count}): {FImpList(l)}");
                }
                else
                {
                    Result($"{name}: every shell lands on the top of its column; over the slab on floor 3, none on floor 2 under it, the raider never hit", l.Count >= 3 && tops && over.All(i => i.floor == 2) && over.Count > 0 && l.All(i => i.hit != r),
                        $"impacts {l.Count} (over the slab {over.Count}), forced {ordered}: {FImpList(l)}");
                }
                crew.jobs.StopAll();
                mortar.Destroy();
                missRef(v.verbProps) = missWas;
                missClassicRef(v.verbProps) = missClassicWas;
            });
        }

        // the bow fixture: the shot slab, A on it (row 2) with the recurve bow; a raider on the ground 7 out in a ring of
        // walls 2 cells round it (no line of sight from A, no cover next to it)
        private static Pawn LbBowFixture(Map map, string gun = "Bow_Recurve")
        {
            LShotSlab();
            LPut(LGun(mA, gun), LU(20, 21));
            foreach (IntVec3 c in CellRect.FromLimits(LG(18, 12), LG(22, 16)).EdgeCells)
            {
                LWall(c);
            }
            Pawn r = LeanRaider(map, LG(20, 14), null);
            mShooter = mA;
            mTarget = r;
            return r;
        }

        private static void LbOverWall(Map map)
        {
            Pawn r = LbBowFixture(map);
            fLob = Lob.Off;
            bool before = FCanHit(mA, r);
            fLob = Lob.Lobbed;
            bool can = FCanHit(mA, r), sees = AttackTargetFinder.CanSee(mA, r), los = Los3D.CanSee(mA.Position, r.Position, map);
            Result("lb_bow_over_wall: no line of sight; nothing declared: can't hit; declared lobbed: can hit and see", !los && !before && can && sees, $"line {los}, can hit before {before}, lobbed: can hit {can}, sees {sees}");
            FShots("lb_bow_over_wall", mA, r, 8, 4000, (m, l) =>
            {
                int hits = l.Count(i => i.hit == r);
                Result("lb_bow_over_wall: lobbed arrows fly over the walls and hit the raider (no flight, no interception)", hits > 0 && l.All(i => !i.flight) && l.Where(i => i.cross).All(i => i.lobbed),
                    $"impacts {l.Count}, hits {hits}: {FImpList(l)}");
                fLob = Lob.Off;
            });
        }

        // A raider on the ground under the slab, A on the slab: lobbed arrows come down on the slab over it.
        private static void LbSlabOver(Map map)
        {
            LShotSlab();
            LPut(LGun(mA, "Bow_Recurve"), LU(17, 21));
            Pawn r = LeanRaider(map, LG(23, 25), null);
            mShooter = mA;
            mTarget = r;
            fLob = Lob.Lobbed;
            bool can = FCanHit(mA, r);
            Result("lb_bow_slab_over: declared lobbed, the raider under the slab can be shot at", can, $"can hit {can}");
            FShots("lb_bow_slab_over", mA, r, 5, 2500, (m, l) =>
            {
                IntVec3 tg = ProjectZ.GroundCellOf(r.Position, m);
                Result("lb_bow_slab_over: every arrow stops on the slab (floor 2), the raider is never hit", l.Count >= 5 && l.All(i => i.floor == 1 && i.hit != r) && l.Any(i => (i.g - tg).LengthHorizontal <= 1.5f),
                    $"impacts {l.Count}: {FImpList(l)}");
                fLob = Lob.Off;
            });
        }

        // The rule is only for the bow: the assault rifle from the same spot still needs its line (refused); the bow with
        // the rule off too; on one floor the rule is never asked (a bow behind a ring of walls on the ground can't hit).
        private static void LbDirect(Map map)
        {
            Pawn r = LbBowFixture(map, "Gun_AssaultRifle");
            fLob = Lob.Lobbed;
            bool rifle = FCanHit(mA, r);
            bool order = LOrder(mA, r, out string label, out _);
            Find.Selector.ClearSelection();
            Result("lb_direct_unaffected: an undeclared weapon (assault rifle) can't hit over the walls, the order is refused", !rifle && !order, $"can hit {rifle}, order '{label}'");
            LGun(mA, "Bow_Recurve");
            fLob = Lob.Off;
            bool bowOff = FCanHit(mA, r);
            Result("lb_direct_unaffected: the bow with the rule off can't hit", !bowOff, $"can hit {bowOff}");
            fLob = Lob.Lobbed;
            LPut(LGun(mB, "Bow_Recurve"), LG(20, 8));
            bool flat = FCanHit(mB, r);
            Result("lb_direct_unaffected: on one floor the rule is never asked (B with the bow on the ground can't hit over the walls)", !flat, $"can hit {flat}");
            fLob = Lob.Off;
            mA.jobs.StopAll();
        }

        // The arrow as a flyOverhead def. A on the ground 6 out, a raider in the 2nd row of the slab (floor 2) under a slab
        // on floor 3. Declared direct: a line needed (there is one), it flies as a direct shot and hits on floor 2. Declared
        // lobbed: it comes down on floor 3 over the raider.
        private static void LbOverheadArrow(Map map, Lob kind)
        {
            LShotSlab();
            foreach (int x in new[] { SX0, (SX0 + SX1) / 2, SX1 })
            {
                LWall(LU(x, SZ1));
            }
            LSlab(SX0, SZ0, SX1, SZ1, 2);
            LPut(LGun(mA, "Bow_Recurve"), LG(20, 14));
            Pawn r = LeanRaider(map, LU(20, 21), null);
            mShooter = mA;
            mTarget = r;
            FArrowOverhead(true);
            fLob = kind;
            bool can = FCanHit(mA, r), los = Los3D.CanSee(mA.Position, r.Position, map);
            string name = kind == Lob.Direct ? "lb_arrow_overhead_direct" : "lb_arrow_overhead_lobbed";
            Result($"{name}: can hit (line of sight {los})", can, $"can hit {can}");
            FShots(name, mA, r, kind == Lob.Direct ? 10 : 5, 5000, (m, l) =>
            {
                if (kind == Lob.Direct)
                {
                    Result($"{name}: a flyOverhead arrow declared direct flies as a direct shot (flight) and comes down on floor 2, never on the slab over it", l.Count >= 5 && l.All(i => i.flight && !i.lobbed && i.floor == 1) && l.Any(i => i.hit == r),
                        $"impacts {l.Count}, hits {l.Count(i => i.hit == r)}: {FImpList(l)}");
                }
                else
                {
                    Result($"{name}: the same arrow declared lobbed comes down on the slab on floor 3, the raider never hit", l.Count >= 5 && l.All(i => !i.flight && i.lobbed && i.floor == 2 && i.hit != r),
                        $"impacts {l.Count}: {FImpList(l)}");
                }
                fLob = Lob.Off;
                FArrowOverhead(false);
            });
        }
    }
}
