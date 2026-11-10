using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using ProjectZ.Combat;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode scope3d (Api 10; run with -projectzfloors=3 -projectzseed=N), on the api6 fixture (TestApi6: a slab on floor 1
    // with a roof under it, real stairs and their stairwell opening, a roof only, a ground wall, open air elsewhere).
    // Small probe methods call one game primitive each and are registered with Api.Scope3DMethod; their twins
    // (not registered) give the game's answer. Every check runs on its own random state (Seeded).
    //   Z0 registration: each probe one swapped call, a lambda's and an iterator's too, a method without one: 0.
    //   Z1 no scope: every probe equals the game's own call for every pair (same floor and across floors).
    //   Z2 scope on two floors: each probe equals ProjectZ's own 3D answer (Los3D, Distance3D, OpenSkyOver, the centre's
    //      floor), Standable the game's; and differs from the game's where the game is flat (counted).
    //   Z3 scope on one floor, and pairs on one floor inside a scope on two: the game's answers (searches on the floor).
    //   Z4 nesting (the innermost answers, an inert map-less scope shadows, a stale handle closes nothing).
    //   Z5 threads: a worker thread inside an open scope gets the game's answers; a scope opened there does nothing.
    //   Z6 getters: AimHeights, EyeHeight, FloorStride, OpenAirTerrain are what ProjectZ uses.
    //   Z7 the trial: Verb_CanHitCell_Patch.Decide (the game's own method in the scope) equals the rule it replaced
    //      (its Fallback, the old code) for every pair, verb flags, lobbed declarations and corner walks; off the main
    //      thread the fallback answers.
    //   Z8 cost: a registered probe with no scope open against its unregistered twin; 600 real ticks timed.
    public static partial class TestRunner
    {
        private static readonly Dictionary<string, int> z3Swapped = new Dictionary<string, int>();
        private static bool? z3Lobbed;
        private static bool z3RuleOn;

        private static bool StepScope3D(Map map)
        {
            switch (stage)
            {
                case "z3_setup":
                    Z3Register();
                    p6Next = "z3_check";
                    P6Setup(map);
                    return true;
                case "z3_check": Z3Check(map); return true;
                case "z3_ticks": Z3Ticks(map); return true;
            }
            return false;
        }

        // ---- the probes (registered) and their twins (the game's own calls)
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool ZLos(IntVec3 a, IntVec3 b, Map m) => GenSight.LineOfSight(a, b, m, true);
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool ZLos3(IntVec3 a, IntVec3 b, Map m) => GenSight.LineOfSight(a, b, m);
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool ZLosHalf(IntVec3 a, IntVec3 b, Map m, int hx, int hz) => GenSight.LineOfSight(a, b, m, false, null, hx, hz);
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool ZLosRect(IntVec3 a, IntVec3 b, Map m) => GenSight.LineOfSight(a, b, m, CellRect.SingleCell(a), CellRect.SingleCell(b));
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool ZEdges(IntVec3 a, IntVec3 b, Map m) => GenSight.LineOfSightToEdges(a, b, m, true);
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool ZRoofed(IntVec3 c, Map m) => c.Roofed(m);
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool ZGridRoofed(IntVec3 c, Map m) => m.roofGrid.Roofed(c);
        [MethodImpl(MethodImplOptions.NoInlining)] public static float ZDist(IntVec3 a, IntVec3 b) => a.DistanceTo(b);
        [MethodImpl(MethodImplOptions.NoInlining)] public static int ZDistSq(IntVec3 a, IntVec3 b) => a.DistanceToSquared(b);
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool ZInHor(IntVec3 a, IntVec3 b, float r) => a.InHorDistOf(b, r);
        [MethodImpl(MethodImplOptions.NoInlining)] public static List<IntVec3> ZRadial(IntVec3 c, float r) => GenRadial.RadialCellsAround(c, r, true).ToList();
        [MethodImpl(MethodImplOptions.NoInlining)] public static List<IntVec3> ZRadial2(IntVec3 c, float r0, float r1) => GenRadial.RadialCellsAround(c, r0, r1).ToList();
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool ZNear(IntVec3 root, Map m, int r, Predicate<IntVec3> v, out IntVec3 res) => CellFinder.TryFindRandomCellNear(root, m, r, v, out res);
        [MethodImpl(MethodImplOptions.NoInlining)] public static float ZLambda(IntVec3 a, IntVec3 b) { Func<float> f = () => a.DistanceTo(b); return f(); }
        [MethodImpl(MethodImplOptions.NoInlining)] public static IEnumerable<float> ZIter(IntVec3 a, IntVec3 b) { yield return a.DistanceTo(b); }
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool ZNothing(IntVec3 c, Map m) => c.Standable(m);
        // twins for the cost check (never registered)
        [MethodImpl(MethodImplOptions.NoInlining)] public static float ZDistPlain(IntVec3 a, IntVec3 b) => a.DistanceTo(b);
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool ZRoofedPlain(IntVec3 c, Map m) => c.Roofed(m);

        private static readonly string[] z3Probes = { nameof(ZLos), nameof(ZLos3), nameof(ZLosHalf), nameof(ZLosRect), nameof(ZEdges), nameof(ZRoofed), nameof(ZGridRoofed), nameof(ZDist), nameof(ZDistSq), nameof(ZInHor), nameof(ZRadial), nameof(ZRadial2), nameof(ZNear), nameof(ZLambda), nameof(ZIter) };

        private static void Z3Register()
        {
            foreach (string n in z3Probes)
            {
                z3Swapped[n] = Api.Scope3DMethod(AccessTools.Method(typeof(TestRunner), n));
            }
            z3Swapped[nameof(ZNothing)] = Api.Scope3DMethod(AccessTools.Method(typeof(TestRunner), nameof(ZNothing)));
            z3Swapped["again"] = Api.Scope3DMethod(AccessTools.Method(typeof(TestRunner), nameof(ZDist)));
            z3Swapped["dist3d row"] = Api.Scope3DMethod(AccessTools.Method(typeof(GenClosest), nameof(GenClosest.ClosestThing_Global)));
            Api.RegisterLobbedShot(Z3Rule);
        }

        private static bool? Z3Rule(Verb verb, IntVec3 from, IntVec3 to) => z3RuleOn ? z3Lobbed : null;

        private static List<IntVec3> Z3Cells(Map map)
        {
            List<IntVec3> cells = new List<IntVec3>();
            for (int k = 0; k <= 2; k++)
            {
                for (int x = 0; x < 20; x += 3)
                {
                    for (int z = 0; z < 14; z += 3)
                    {
                        cells.Add(k == 0 ? PG(x, z) : PU(x, z, k));
                    }
                }
                cells.Add(k == 0 ? PG(11, 3) : PU(11, 3, k));   // the stairs' column (the stairwell opening on the ground)
                cells.Add(k == 0 ? PG(4, 4) : PU(4, 4, k));     // under / on the slab
            }
            return cells;
        }

        private static string Z(IntVec3 c, Map map) => MCell(c, map);

        private static void Z3Check(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            z3Errors0 = errors;
            AnnexMapComponent an = ProjectZ.Annex(map);
            List<IntVec3> cells = Z3Cells(map);
            IntVec3 well = p6Stairwell.IsValid ? p6Stairwell : PG(11, 3);
            Info($"scope3d: Api.Version {Api.Version}, {cells.Count} cells on floors 0-2, stairwell opening over {well}");
            Result("scope3d: Api.Version 10 or later", Api.Version >= 10, $"{Api.Version}");

            // ---- Z0 registration
            bool regOk = z3Probes.All(n => z3Swapped[n] == 1) && z3Swapped[nameof(ZNothing)] == 0 && z3Swapped["again"] == 1 && z3Swapped["dist3d row"] == 0
                && !Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m).Transpilers.Any(t => t.owner == "Peren.IsoRimProjectZ.scope3d") && (m.DeclaringType == typeof(GenClosest) || m.DeclaringType?.DeclaringType == typeof(GenClosest)));
            Result("Z0 registration: one swapped call per probe (a lambda's and an iterator's too), none for Standable, a second registration its count, a Dist3D method refused (not patched)",
                regOk, string.Join(", ", z3Swapped.Select(kv => kv.Key + " " + kv.Value)));

            // ---- Z1 no scope: the game's answers
            List<string> diff = new List<string>();
            int pairs = 0;
            Seeded("z3_noscope", () =>
            {
                foreach (IntVec3 a in cells)
                {
                    foreach (IntVec3 b in cells)
                    {
                        pairs++;
                        Z3Same(a, b, map, diff, "no scope");
                    }
                }
            });
            Result("Z1 no scope: every probe is the game's own call (all pairs, one floor and two)", diff.Count == 0 && pairs > 5000 && !Api.InScope3D,
                $"{pairs} pairs" + (diff.Count > 0 ? "; " + string.Join("; ", diff.Take(6)) : ""));

            // ---- Z2 a scope on two floors: ProjectZ's 3D answers
            diff.Clear();
            int cross = 0, losDiff = 0, distDiff = 0, roofDiff = 0, radialDiff = 0, a3 = Scope3DCore.Answered3D;
            Seeded("z3_cross", () =>
            {
                foreach (IntVec3 a in cells)
                {
                    foreach (IntVec3 b in cells)
                    {
                        if (!Api.OnDifferentFloors(a, b, map))
                        {
                            continue;
                        }
                        cross++;
                        using (Api.Scope3D(map, a, b))
                        {
                            float d = Api.Distance3D(a, b, map);
                            Check(diff, a, b, map, "LineOfSight", ZLos(a, b, map), Los3D.CanSee(a, b, map));
                            Check(diff, a, b, map, "LineOfSight is CanSeeBetweenFloors", ZLos(a, b, map), Api.CanSeeBetweenFloors(a, b, map));
                            Check(diff, a, b, map, "LineOfSight(3)", ZLos3(a, b, map), Los3D.CanSee(a, b, map));
                            Check(diff, a, b, map, "LineOfSight(rects)", ZLosRect(a, b, map), Los3D.CanSee(a, b, map));
                            Check(diff, a, b, map, "LineOfSight(half 1, 0)", ZLosHalf(a, b, map, 1, 0), Los3D.CanSeeTowards(a, b, map, 1, 0));
                            Check(diff, a, b, map, "LineOfSight(half 0, -1)", ZLosHalf(a, b, map, 0, -1), Los3D.CanSeeTowards(a, b, map, 0, -1));
                            Check(diff, a, b, map, "LineOfSightToEdges", ZEdges(a, b, map), Los3D.CanSee(a, b, map, true));
                            Check(diff, a, b, map, "DistanceTo", Mathf.Abs(ZDist(a, b) - d) < 1e-5f, true);
                            Check(diff, a, b, map, "DistanceToSquared", ZDistSq(a, b) == Mathf.RoundToInt(d * d), true);
                            Check(diff, a, b, map, "InHorDistOf", ZInHor(a, b, d + 0.01f) && !ZInHor(a, b, d - 0.01f), true);
                            Check(diff, a, b, map, "a lambda", Mathf.Abs(ZLambda(a, b) - d) < 1e-5f, true);
                            Check(diff, a, b, map, "an iterator", Mathf.Abs(ZIter(a, b).First() - d) < 1e-5f, true);
                            Check(diff, a, b, map, "Roofed(source)", ZRoofed(a, map), !Api.OpenSkyOver(a, map));
                            Check(diff, a, b, map, "Roofed(target)", ZRoofed(b, map), !Api.OpenSkyOver(b, map));
                            Check(diff, a, b, map, "RoofGrid.Roofed", ZGridRoofed(b, map), !Api.OpenSkyOver(b, map));
                            Check(diff, a, b, map, "Standable (not swapped)", b.Standable(map), GenGrid.Standable(b, map));
                            if (ZLos(a, b, map) != GenSight.LineOfSight(a, b, map, true))
                            {
                                losDiff++;
                            }
                            if (Mathf.Abs(ZDist(a, b) - a.DistanceTo(b)) > 0.01f)
                            {
                                distDiff++;
                            }
                            if (ZRoofed(b, map) != b.Roofed(map))
                            {
                                roofDiff++;
                            }
                        }
                    }
                }
                // searches round a centre: on the centre's floor (any scope)
                foreach (IntVec3 c in cells.Where((c, i) => i % 5 == 0))
                {
                    using (Api.Scope3D(map, c, cells[cells.Count - 1]))
                    {
                        List<IntVec3> mine = ZRadial(c, 75f);
                        List<IntVec3> game = GenRadial.RadialCellsAround(c, 75f, true).Where(x => ProjectZ.SameBand(x, c, map)).ToList();
                        List<IntVec3> mine2 = ZRadial2(c, 3f, 75f);
                        List<IntVec3> game2 = GenRadial.RadialCellsAround(c, 3f, 75f).Where(x => ProjectZ.SameBand(x, c, map)).ToList();
                        Check(diff, c, c, map, "RadialCellsAround on the centre's floor", mine.SequenceEqual(game) && mine2.SequenceEqual(game2) && mine.Count > 0, true);
                        if (GenRadial.RadialCellsAround(c, 75f, true).Count() != mine.Count)
                        {
                            radialDiff++;
                        }
                        bool found = ZNear(c, map, 30, x => x.Standable(map), out IntVec3 r);
                        bool any = CellRect.CenteredOn(c, 30).ClipInsideMap(map).Cells.Any(x => ProjectZ.SameBand(x, c, map) && x.Standable(map));
                        Check(diff, c, r, map, "TryFindRandomCellNear on the root's floor", found ? any && ProjectZ.SameBand(r, c, map) && r.Standable(map) : !any, true);
                    }
                }
            });
            Result("Z2 a scope on two floors: sight (4 overloads, half offsets, edges) is Los3D, distances the 3D distance, Roofed not OpenSkyOver, Standable the game's, searches on the centre's floor",
                diff.Count == 0 && cross > 2000 && losDiff > 0 && distDiff > 0 && roofDiff > 0 && radialDiff > 0 && Scope3DCore.Answered3D > a3,
                $"{cross} pairs; differs from the game (flat) in sight {losDiff}, distance {distDiff}, Roofed {roofDiff}, radial {radialDiff}; answered in 3D {Scope3DCore.Answered3D - a3}"
                + (diff.Count > 0 ? "; " + string.Join("; ", diff.Take(8)) : ""));

            // ---- Z3 one floor
            diff.Clear();
            int same = 0;
            Seeded("z3_same", () =>
            {
                foreach (IntVec3 a in cells)
                {
                    foreach (IntVec3 b in cells)
                    {
                        if (!Api.SameFloor(a, b, map))
                        {
                            continue;
                        }
                        same++;
                        using (Api.Scope3D(map, a, b))
                        {
                            Z3Same(a, b, map, diff, "scope on one floor", searches: false);
                        }
                        // a pair on one floor inside a scope on two: the pair primitives are the game's
                        using (Api.Scope3D(map, a, Levels.Cell(ProjectZ.GroundCellOf(a, map), ProjectZ.FloorOf(a, map) == 0 ? 1 : 0, map)))
                        {
                            Check(diff, a, b, map, "pair on one floor in a 2-floor scope: LineOfSight", ZLos(a, b, map), GenSight.LineOfSight(a, b, map, true));
                            Check(diff, a, b, map, "pair on one floor in a 2-floor scope: DistanceTo", ZDist(a, b) == a.DistanceTo(b), true);
                        }
                    }
                }
            });
            Result("Z3 a scope on one floor answers as the game (sight, distance, Roofed); one-floor pairs in a 2-floor scope too",
                diff.Count == 0 && same > 2000, $"{same} pairs" + (diff.Count > 0 ? "; " + string.Join("; ", diff.Take(6)) : ""));

            // ---- Z4 nesting
            IntVec3 g0 = PG(9, 10), u1 = PU(9, 10);
            bool wellGame = well.Roofed(map), well3D = !Api.OpenSkyOver(well, map);
            List<string> nest = new List<string>();
            Scope3DHandle outer = Api.Scope3D(map, g0, u1);
            nest.Add($"outer {ZRoofed(well, map)}");
            bool n1 = ZRoofed(well, map) == well3D && Api.InScope3D;
            Scope3DHandle inner = Api.Scope3D(map, g0, PG(9, 11));
            bool n2 = ZRoofed(well, map) == wellGame;
            nest.Add($"inner (one floor) {ZRoofed(well, map)}");
            Scope3DHandle inert = Api.Scope3D(null, g0, u1);
            bool n3 = ZRoofed(well, map) == wellGame && !Api.InScope3D && ZDist(g0, u1) == g0.DistanceTo(u1);
            nest.Add($"inert {ZRoofed(well, map)}, in scope {Api.InScope3D}");
            inert.Dispose();
            inner.Dispose();
            bool n4 = ZRoofed(well, map) == well3D && Api.InScope3D;
            nest.Add($"inner closed {ZRoofed(well, map)}");
            Scope3DHandle again = Api.Scope3D(map, g0, PG(9, 11));   // same level as the closed inner one
            inner.Dispose();                                          // stale: closes nothing
            bool n5 = again.Open && ZRoofed(well, map) == wellGame;
            again.Dispose();
            inner = Api.Scope3D(map, g0, PG(9, 11));
            outer.Dispose();                                          // closes the inner one left open too
            bool n6 = !inner.Open && !Api.InScope3D && ZRoofed(well, map) == wellGame && Scope3DCore.depth == 0;
            nest.Add($"all closed {ZRoofed(well, map)}, depth {Scope3DCore.depth}");
            Result("Z4 nesting: the innermost scope answers; an inert scope (no map) shadows; a stale handle closes nothing; closing the outer closes all",
                wellGame != well3D && n1 && n2 && n3 && n4 && n5 && n6, $"stairwell opening: game {wellGame}, 3D {well3D}; {string.Join(", ", nest)}; {n1} {n2} {n3} {n4} {n5} {n6}");

            // ---- Z5 threads
            bool tGame = false, tInScope = true, tOpened = true;
            int depthOnWorker = -1;
            using (Api.Scope3D(map, g0, u1))
            {
                Task.Run(() =>
                {
                    tGame = ZRoofed(well, map) == wellGame && ZDist(g0, u1) == g0.DistanceTo(u1) && ZLos(g0, u1, map) == GenSight.LineOfSight(g0, u1, map, true);
                    tInScope = Api.InScope3D;
                    Scope3DHandle w = Api.Scope3D(map, g0, u1);
                    tOpened = w.Open;
                    depthOnWorker = Scope3DCore.depth;
                    w.Dispose();
                }).Wait();
                bool stillOpen = Api.InScope3D && ZRoofed(well, map) == well3D;
                Result("Z5 threads: inside an open scope a worker thread gets the game's answers; a scope opened there does nothing",
                    tGame && !tInScope && !tOpened && depthOnWorker == 1 && stillOpen, $"game answers {tGame}, in scope {tInScope}, opened {tOpened}, depth {depthOnWorker}, main still open {stillOpen}");
            }

            // ---- Z6 getters
            float[] aims = (float[])AccessTools.Field(typeof(Los3D), "Aims").GetValue(null);
            IReadOnlyList<float> ah = Api.AimHeights;
            Result("Z6 AimHeights: Los3D's aims (head, chest, waist), read-only; EyeHeight: Los3D's eye",
                ah.SequenceEqual(aims) && ah.Count == 3 && ah[0] == Los3D.AimHead && ah[1] == Los3D.AimChest && ah[2] == Los3D.AimWaist && !(ah is float[]) && Api.EyeHeight == Los3D.Eye,
                $"[{string.Join(", ", ah.Select(x => x.ToString("0.00")))}], eye {Api.EyeHeight:0.00}");
            Result("Z6 FloorStride: the annex stride = map rows / floors = FloorRowShift(1)",
                Api.FloorStride(map) == an.stride && Api.FloorStride(map) == map.Size.z / an.floors && Api.FloorStride(map) == Api.FloorRowShift(1, map) && Api.FloorStride(null) == 0,
                $"{Api.FloorStride(map)} (stride {an.stride}, {map.Size.z} rows / {an.floors})");
            Result("Z6 OpenAirTerrain: PZ_OpenAir, the terrain of an upper cell with no floor",
                Api.OpenAirTerrain == PZDefOf.PZ_OpenAir && Api.OpenAirTerrain != null && PU(17, 10).GetTerrain(map) == Api.OpenAirTerrain && PU(4, 4).GetTerrain(map) != Api.OpenAirTerrain,
                $"{Api.OpenAirTerrain?.defName}; open cell {PU(17, 10).GetTerrain(map)?.defName}, slab cell {PU(4, 4).GetTerrain(map)?.defName}");

            Z3Trial(map, cells);
            Z3Cost(map, well);

            // real ticks after all this: the game runs on, no scope left open
            z3TickStart = Find.TickManager.TicksGame;
            z3TickWatch.Restart();
            for (int i = 0; i < 600; i++)
            {
                Find.TickManager.DoSingleTick();
            }
            z3TickWatch.Stop();
            Wait(10, "z3_ticks");
        }

        private static int z3TickStart, z3Errors0;
        private static readonly Stopwatch z3TickWatch = new Stopwatch();

        private static void Z3Ticks(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            int n = Find.TickManager.TicksGame - z3TickStart;
            Info($"Z8 600 real ticks on the fixture map: {z3TickWatch.Elapsed.TotalMilliseconds / Math.Max(1, n):0.000} ms per tick");
            Result("Z8 the game ticks on (600 real ticks), no scope left open, no error", n == 600 && Scope3DCore.depth == 0 && errors == z3Errors0, $"ticked {n}, depth {Scope3DCore.depth}, errors during the checks {errors - z3Errors0}");
            Api.UnregisterLobbedShot(Z3Rule);
            Finish();
        }

        // Probes against the game's calls for one pair (no scope, or a scope on one floor).
        private static void Z3Same(IntVec3 a, IntVec3 b, Map map, List<string> diff, string what, bool searches = true)
        {
            Check(diff, a, b, map, what + ": LineOfSight", ZLos(a, b, map), GenSight.LineOfSight(a, b, map, true));
            Check(diff, a, b, map, what + ": LineOfSight(3)", ZLos3(a, b, map), GenSight.LineOfSight(a, b, map));
            Check(diff, a, b, map, what + ": LineOfSight(rects)", ZLosRect(a, b, map), GenSight.LineOfSight(a, b, map, CellRect.SingleCell(a), CellRect.SingleCell(b)));
            Check(diff, a, b, map, what + ": LineOfSight(half)", ZLosHalf(a, b, map, 1, 0), GenSight.LineOfSight(a, b, map, false, null, 1, 0));
            Check(diff, a, b, map, what + ": LineOfSightToEdges", ZEdges(a, b, map), GenSight.LineOfSightToEdges(a, b, map, true));
            Check(diff, a, b, map, what + ": DistanceTo", ZDist(a, b) == a.DistanceTo(b), true);
            Check(diff, a, b, map, what + ": DistanceToSquared", ZDistSq(a, b) == a.DistanceToSquared(b), true);
            Check(diff, a, b, map, what + ": InHorDistOf", ZInHor(a, b, 7.5f) == a.InHorDistOf(b, 7.5f), true);
            Check(diff, a, b, map, what + ": Roofed", ZRoofed(b, map), b.Roofed(map));
            Check(diff, a, b, map, what + ": RoofGrid.Roofed", ZGridRoofed(a, map), map.roofGrid.Roofed(a));
            if (searches && a == b)
            {
                Check(diff, a, b, map, what + ": RadialCellsAround", ZRadial(a, 9f).SequenceEqual(GenRadial.RadialCellsAround(a, 9f, true)), true);
            }
        }

        private static void Check(List<string> diff, IntVec3 a, IntVec3 b, Map map, string what, bool got, bool want)
        {
            if (got != want && diff.Count < 40)
            {
                diff.Add($"{what} {Z(a, map)} -> {Z(b, map)}: {got}, expected {want}");
            }
        }

        // ---- Z7 the trial
        private static void Z3Trial(Map map, List<IntVec3> cells)
        {
            Pawn p = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            if (p == null)
            {
                Result("Z7 trial: a colonist", false, "none");
                return;
            }
            p.equipment.DestroyAllEquipment();
            p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_AssaultRifle")));
            Verb verb = p.equipment.PrimaryEq.PrimaryVerb;
            VerbProperties original = verb.verbProps;
            MethodInfo fallback = AccessTools.Method(typeof(Verb_CanHitCell_Patch), "Fallback");
            MethodInfo clone = AccessTools.Method(typeof(object), "MemberwiseClone");
            List<string> diff = new List<string>();
            int n = 0, yes = 0, fb0 = Verb_CanHitCell_Patch.Fallbacks;
            bool ready = Verb_CanHitCell_Patch.GameReady();
            // a target cell with a pawn on it for mustCastOnOpenGround (the shooter itself stands somewhere else)
            try
            {
                foreach (bool los in new[] { true, false })
                {
                    foreach (bool open in new[] { false, true })
                    {
                        VerbProperties vp = (VerbProperties)clone.Invoke(original, null);
                        vp.requireLineOfSight = los;
                        vp.mustCastOnOpenGround = open;
                        verb.verbProps = vp;
                        foreach (bool? lob in new bool?[] { null, true, false })
                        {
                            z3RuleOn = true;
                            z3Lobbed = lob;
                            foreach (IntVec3 a in cells)
                            {
                                foreach (IntVec3 b in cells)
                                {
                                    if (!Api.OnDifferentFloors(a, b, map))
                                    {
                                        continue;
                                    }
                                    foreach (bool corners in new[] { false, true })
                                    {
                                        n++;
                                        byte got = Verb_CanHitCell_Patch.Decide(verb, a, b, corners);
                                        byte want = (byte)fallback.Invoke(null, new object[] { verb, a, b, corners, map });
                                        if (got == FinalSay.Yes)
                                        {
                                            yes++;
                                        }
                                        if (got != want && diff.Count < 20)
                                        {
                                            diff.Add($"los {los}, open ground {open}, lobbed {lob?.ToString() ?? "-"}, corners {corners}: {Z(a, map)} -> {Z(b, map)}: {got}, old rule {want}");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                verb.verbProps = original;
                z3RuleOn = false;
                z3Lobbed = null;
            }
            int fbMain = Verb_CanHitCell_Patch.Fallbacks - fb0;
            Result("Z7 trial: between floors the game's own CanHitCellFromCellIgnoringRange in the scope answers exactly as the rule it replaced (verb flags, lobbed declarations, corners)",
                ready && diff.Count == 0 && n > 20000 && yes > 0 && yes < n && fbMain == 0,
                $"copy ready {ready}, {n} cases, {yes} yes, fallbacks on the main thread {fbMain}" + (diff.Count > 0 ? "; " + string.Join("; ", diff.Take(6)) : ""));
            // the real method (prefix + final say) for the same pairs, the rifle's flags
            int real = 0, realDiff = 0;
            foreach (IntVec3 a in cells)
            {
                foreach (IntVec3 b in cells.Where(c => Api.OnDifferentFloors(a, c, map)))
                {
                    real++;
                    bool got = Z3CanHit(verb, a, b);
                    bool want = (byte)fallback.Invoke(null, new object[] { verb, a, b, false, map }) == FinalSay.Yes;
                    if (got != want)
                    {
                        realDiff++;
                    }
                }
            }
            // off the main thread: the fallback answers
            IntVec3 s0 = cells[0], t0 = cells.First(c => Api.OnDifferentFloors(s0, c, map));
            byte onWorker = 0;
            int fb1 = Verb_CanHitCell_Patch.Fallbacks;
            Task.Run(() => onWorker = Verb_CanHitCell_Patch.Decide(verb, s0, t0, false)).Wait();
            byte onMain = Verb_CanHitCell_Patch.Decide(verb, s0, t0, false);
            Result("Z7 trial: through the game's call (patches and final say) the same; off the main thread the old rule answers",
                realDiff == 0 && real > 1000 && Verb_CanHitCell_Patch.Fallbacks == fb1 + 1 && onWorker == onMain,
                $"{real} pairs, {realDiff} differ; worker {onWorker}, main {onMain}, fallbacks +{Verb_CanHitCell_Patch.Fallbacks - fb1}");
        }

        private static readonly Func<Verb, IntVec3, IntVec3, bool, bool> z3CanHit = AccessTools.MethodDelegate<Func<Verb, IntVec3, IntVec3, bool, bool>>(AccessTools.Method(typeof(Verb), "CanHitCellFromCellIgnoringRange"), null, true, null);

        private static bool Z3CanHit(Verb v, IntVec3 a, IntVec3 b) => z3CanHit(v, a, b, false);

        // ---- Z8 cost
        private static void Z3Cost(Map map, IntVec3 well)
        {
            IntVec3 a = PG(2, 2), b = PU(15, 11);
            const int N = 400000;
            double Time(Func<float> f)
            {
                double best = double.MaxValue;
                for (int r = 0; r < 5; r++)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    float s = 0f;
                    for (int i = 0; i < N; i++)
                    {
                        s += f();
                    }
                    sw.Stop();
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / N);
                }
                return best;
            }
            double reg = Time(() => ZDist(a, b)), plain = Time(() => ZDistPlain(a, b));
            double regR = Time(() => ZRoofed(well, map) ? 1f : 0f), plainR = Time(() => ZRoofedPlain(well, map) ? 1f : 0f);
            double inScope;
            using (Api.Scope3D(map, a, b))
            {
                inScope = Time(() => ZDist(a, b));
            }
            Info($"Z8 cost per call, no scope: DistanceTo registered {reg:0.0} ns vs the game's {plain:0.0} ns; Roofed {regR:0.0} vs {plainR:0.0} ns; in a scope (3D distance) {inScope:0.0} ns");
            Result("Z8 cost: a swapped call with no scope open costs about the game's call (+ at most 3 ns)", reg - plain < 3.0 && regR - plainR < 3.0,
                $"DistanceTo +{reg - plain:0.0} ns, Roofed +{regR - plainR:0.0} ns");
        }
    }
}
