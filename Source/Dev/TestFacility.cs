using System;
using System.Collections.Generic;
using System.Linq;
using ProjectZ.Combat;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode facility (facility links in 3D, Patches/FacilityPatches.cs; run with -projectzfloors=3 on the default map).
    // A cleared field at the ground's north edge, slabs on floors 2 and 3 over part of it, floor 2's south rows slabbed:
    //   - pairs: a stonecutter's table and a tool cabinet (max 8) spawned on given floors and offsets, the link checked
    //     against the 3D distance of their true centres: straight above links, floor 3 at a flat 7.8 doesn't, same-floor
    //     pairs as the game; the old leak (bench on the ground's north row, cabinet on floor 2's south row) doesn't link;
    //     with three cabinets the bench keeps the two nearest in 3D (spawned bench last: the order; bench first: IsBetter);
    //   - gravship: a ground ship (engine, console, tanks, thrusters) links its parts to the engine; the grav field
    //     extender's range (18.9) on floors 2 and 3 is the 3D one, and floor 2's south rows are out of the engine's reach;
    //   - work: a colonist cuts stone blocks (a real bill job) at a ground table with a cabinet straight above it on
    //     floor 2, then at a control table without one: the work done per tick is 6% higher at the first.
    // -projectznofacilityfix: the game's raw distances (Facilities3D.Off), to show the old behaviour.
    public static partial class TestRunner
    {
        private static int faO, faGz;
        private static Building faBenchA, faBenchB;
        private static Pawn faWorker;
        private static Bill faBill;
        private static int faStartTick;
        private static float faStartWork;
        private static readonly List<float> faSamples = new List<float>();   // work per tick / pawn speed, per 60-tick window
        private static float faLastWork, faSumWork, faSumTickSpeed;
        private static int faLastTick;
        private static float faRateA, faRateB, faFactorA, faFactorB;
        private static string faWorkCase;

        private static IntVec3 FG(int x, int z) => new IntVec3(faO + x, 0, z);

        private static IntVec3 FOn(IntVec3 g, int k, Map map) => ProjectZ.CellOnFloor(g, k, map);

        private static ThingDef FaBenchDef => DefDatabase<ThingDef>.GetNamed("TableStonecutter");

        private static ThingDef FaCabinetDef => DefDatabase<ThingDef>.GetNamed("ToolCabinet");

        // The independent measure: true centres over the ground, floors k * Range3D.Storey apart.
        private static float FaDist3D(Thing a, Thing b, Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            int ka = ProjectZ.FloorOf(a.Position, map), kb = ProjectZ.FloorOf(b.Position, map);
            Vector3 va = a.TrueCenter(), vb = b.TrueCenter();
            float dx = va.x - vb.x;
            float dz = (va.z - ka * an.stride) - (vb.z - kb * an.stride);
            float dy = (ka - kb) * Range3D.Storey;
            return Mathf.Sqrt(dx * dx + dz * dz + dy * dy);
        }

        private static float FaRaw(Thing a, Thing b) => Vector3.Distance(a.TrueCenter(), b.TrueCenter());

        private static List<Thing> FaLinks(Thing bench) => bench.TryGetComp<CompAffectedByFacilities>()?.LinkedFacilitiesListForReading.ToList() ?? new List<Thing>();

        private static void FaSlab(Map map, CellRect ground, int k)
        {
            foreach (IntVec3 g in ground)
            {
                IntVec3 below = FOn(g, k - 1, map);
                IntVec3 c = FOn(g, k, map);
                if (!below.IsValid || !c.IsValid)
                {
                    continue;
                }
                map.roofGrid.SetRoof(below, RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(c, PZDefOf.PZ_UpperFloor);
                map.fogGrid.Unfog(c);
            }
        }

        // One pair: bench on floor kb at ground cell gb, cabinet on floor kc at gb + off; link expected by the 3D rule.
        private static void FaPair(Map map, string name, int kb, IntVec3 gb, int kc, IntVec3 off)
        {
            Thing bench = Place(FaBenchDef, ThingDefOf.Steel, FOn(gb, kb, map), map, Rot4.North);
            Thing cab = Place(FaCabinetDef, ThingDefOf.Steel, FOn(gb + off, kc, map), map, Rot4.North);
            float d3 = FaDist3D(bench, cab, map);
            float raw = FaRaw(bench, cab);
            float max = cab.TryGetComp<CompFacility>().Props.maxDistance;
            bool linked = FaLinks(bench).Contains(cab);
            bool expect = d3 <= max;
            Result($"pair {name}: {(expect ? "links" : "doesn't link")}", linked == expect && (kb != kc || Mathf.Abs(d3 - raw) < 0.01f),
                $"bench {bench.Position} floor {kb + 1}, cabinet {cab.Position} floor {kc + 1}: 3D {d3:0.00}, raw rows {raw:0.00} (max {max}), linked {linked}, " +
                $"cabinet sees bench {cab.TryGetComp<CompFacility>().LinkedBuildings.Contains(bench)}");
            // placement preview from the bench's side (lines and the "potentially linked to" list)
            bool preview = CompAffectedByFacilities.PotentialThingsToLinkTo(FaBenchDef, bench.Position, Rot4.North, map).Contains(cab);
            Result($"pair {name}: placement preview agrees", preview == expect, $"preview lists the cabinet: {preview}");
            cab.Destroy();
            bench.Destroy();
        }

        // Three cabinets: ground G at 6.5, floor-2 U1 straight above (1.7) and U2 3 east (3.9). The bench keeps U1 and U2.
        private static void FaThree(Map map, bool benchLast)
        {
            IntVec3 gb = FG(33, faGz - 15);
            Thing bench = null;
            if (!benchLast)
            {
                bench = Place(FaBenchDef, ThingDefOf.Steel, gb, map, Rot4.North);
            }
            Thing g = Place(FaCabinetDef, ThingDefOf.Steel, gb + new IntVec3(-7, 0, 0), map, Rot4.North);
            Thing u2 = Place(FaCabinetDef, ThingDefOf.Steel, FOn(gb + new IntVec3(3, 0, 0), 1, map), map, Rot4.North);
            Thing u1 = Place(FaCabinetDef, ThingDefOf.Steel, FOn(gb, 1, map), map, Rot4.North);
            if (benchLast)
            {
                bench = Place(FaBenchDef, ThingDefOf.Steel, gb, map, Rot4.North);
            }
            List<Thing> links = FaLinks(bench);
            Result($"three cabinets ({(benchLast ? "bench spawned last: link order" : "bench first: better cabinets supplant")}): the two nearest in 3D",
                links.Count == 2 && links.Contains(u1) && links.Contains(u2) && !links.Contains(g),
                $"3D: ground {FaDist3D(bench, g, map):0.00}, above {FaDist3D(bench, u1, map):0.00}, floor 2 east {FaDist3D(bench, u2, map):0.00}; linked: " +
                string.Join(", ", links.Select(t => t == g ? "ground" : t == u1 ? "above" : t == u2 ? "floor 2 east" : t.ToString())));
            foreach (Thing t in new[] { g, u1, u2, bench })
            {
                t.Destroy();
            }
        }

        private static void StepFacility(Map map)
        {
            switch (stage)
            {
                case "fa_setup":
                    FaSetup(map);
                    return;
                case "fa_pairs":
                {
                    IntVec3 p = FG(33, faGz - 15);
                    FaPair(map, "cabinet straight above the bench (floor 2 over the ground)", 0, p, 1, IntVec3.Zero);
                    FaPair(map, "bench straight above the cabinet (ground under floor 2)", 1, p, 0, IntVec3.Zero);
                    FaPair(map, "cabinet on floor 2, 7 east", 0, p, 1, new IntVec3(7, 0, 0));
                    FaPair(map, "cabinet on floor 3, 7 east 2 north (flat 7.8, 3D 8.4)", 0, p, 2, new IntVec3(7, 0, 2));
                    FaPair(map, "same floor (ground), 7 east 2 north", 0, p, 0, new IntVec3(7, 0, 2));
                    FaPair(map, "same floor (ground), 8 east", 0, p, 0, new IntVec3(8, 0, 0));
                    FaPair(map, "same floor (floor 2), 7 east 2 north", 1, p, 1, new IntVec3(7, 0, 2));
                    FaPair(map, "same floor (floor 2), 8 east", 1, p, 1, new IntVec3(8, 0, 0));
                    FaPair(map, "same floor (floor 3), 6 west 3 south", 2, p, 2, new IntVec3(-6, 0, -3));
                    // the old leak: bench on the ground's north row, cabinet on floor 2's south row (raw rows a few apart)
                    FaPair(map, "old leak: bench on the ground's north row, cabinet on floor 2's south row", 0, FG(33, faGz - 2), 1, new IntVec3(0, 0, 2 - faGz));
                    {
                        Thing bench = Place(FaBenchDef, ThingDefOf.Steel, FG(33, faGz - 2), map, Rot4.North);
                        bool can = bench.TryGetComp<CompAffectedByFacilities>().CanPotentiallyLinkTo(FaCabinetDef, FOn(FG(33, 0), 1, map), Rot4.North);
                        Result("old leak: a cabinet being placed on floor 2's south row shows no link to the bench", !can, $"CanPotentiallyLinkTo {can}");
                        bench.Destroy();
                    }
                    FaThree(map, benchLast: true);
                    FaThree(map, benchLast: false);
                    Wait(5, "fa_ship");
                    return;
                }
                case "fa_ship":
                    FaShip(map);
                    return;
                case "fa_work_go":
                    FaWorkGo(map, faBenchA, "A (cabinet straight above on floor 2)");
                    return;
                case "fa_work_measure":
                    FaMeasureStart(map);
                    return;
                case "fa_work_done":
                    FaMeasureDone(map);
                    return;
                case "fa_end":
                    Finish();
                    return;
            }
        }

        private static void FaSetup(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || an.floors < 3)
            {
                Result("facility setup", false, $"needs 3 floors (-projectzfloors=3); floors {an?.floors}");
                Finish();
                return;
            }
            faGz = an.groundZ;
            Result("patched: facility distance calls swapped for the 3D ones in 7 methods", FacilityDistance_Patch.PerMethod.Count == 7 && FacilityDistance_Patch.PerMethod.Values.All(v => v >= 1),
                string.Join(", ", FacilityDistance_Patch.PerMethod.Select(kv => kv.Key + " " + kv.Value)));
            int best = -1, bestBad = int.MaxValue;
            for (int x = 3; x + 47 < map.Size.x - 3; x += 4)
            {
                CellRect n = new CellRect(x, faGz - 35, 47, 35);
                CellRect s = new CellRect(x, 0, 47, 10);
                int bad = n.Cells.Concat(s.Cells).Count(c => c.GetThingList(map).Any(t => !t.def.destroyable) || c.GetTerrain(map).IsWater || c.GetTerrain(map).passability == Traversability.Impassable || map.roofGrid.RoofAt(c)?.isThickRoof == true);
                if (bad < bestBad)
                {
                    bestBad = bad;
                    best = x;
                }
            }
            faO = best;
            ExClear(map, new CellRect(faO - 1, faGz - 36, 49, 36));
            ExClear(map, new CellRect(faO - 1, 0, 49, 11));
            foreach (IntVec3 c in new CellRect(faO - 1, faGz - 36, 49, 36).ClipInsideMap(map))
            {
                if (!c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy))
                {
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
            }
            DebugSettings.enableRandomMentalStates = false;
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            for (int i = 0; i < 24 && GenLocalDate.HourOfDay(map) != 10; i++)
            {
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + GenDate.TicksPerHour);
            }
            // work area under a floor-2 slab (rows gz-33..gz-25), pair area under floors 2 and 3 (rows gz-22..gz-9),
            // floor 2's south rows 0..3 (the old leak)
            FaSlab(map, new CellRect(faO + 26, faGz - 33, 19, 9), 1);
            FaSlab(map, new CellRect(faO + 24, faGz - 22, 22, 14), 1);
            FaSlab(map, new CellRect(faO + 24, faGz - 22, 22, 14), 2);
            FaSlab(map, new CellRect(faO + 24, 0, 22, 4), 1);
            BuildShip(map, FG(1, faGz - 16), 13, 11);
            // the work benches: A with a cabinet straight above it on floor 2, B 12 east without one
            faBenchA = (Building)Place(FaBenchDef, ThingDefOf.Steel, FG(29, faGz - 29), map, Rot4.North);
            faBenchB = (Building)Place(FaBenchDef, ThingDefOf.Steel, FG(41, faGz - 29), map, Rot4.North);
            Place(FaCabinetDef, ThingDefOf.Steel, FOn(FG(29, faGz - 29), 1, map), map, Rot4.North);
            List<Pawn> cols = Colonists(map);
            faWorker = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Crafting) && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation));
            if (faWorker == null)
            {
                faWorker = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true));
                GenSpawn.Spawn(faWorker, FG(35, faGz - 31), map);
            }
            foreach (Pawn p in Colonists(map))
            {
                if (p == faWorker)
                {
                    continue;
                }
                Draft(p, true);
                Teleport(p, CellFinder.RandomClosewalkCellNear(new IntVec3(faO > map.Size.x / 2 ? 5 : map.Size.x - 6, 0, faGz / 2), map, 6));
            }
            Draft(faWorker, false);
            Teleport(faWorker, FG(35, faGz - 31));
            faWorker.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
            foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefs)
            {
                if (!faWorker.WorkTypeIsDisabled(w))
                {
                    faWorker.workSettings.SetPriority(w, w == WorkTypeDefOf.Crafting ? 1 : 0);
                }
            }
            faWorker.needs?.food?.SetInitialLevel();
            if (faWorker.needs?.rest != null)
            {
                faWorker.needs.rest.CurLevel = 1f;
            }
            Info($"field x {faO}..{faO + 46}, ground rows {faGz}, stride {an.stride} (void rows {an.stride - faGz}), storey {Range3D.Storey:0.00}, bad cells {bestBad}, worker {faWorker}");
            Wait(30, "fa_pairs");
        }

        private static void FaShip(Map map)
        {
            Building_GravEngine e = GlEngine(map);
            if (e == null)
            {
                Result("gravship built", false, "no engine");
                Wait(5, "fa_work_go");
                return;
            }
            List<CompGravshipFacility> parts = map.listerThings.AllThings.Select(t => t.TryGetComp<CompGravshipFacility>()).Where(c => c != null).ToList();
            List<string> bad = parts.Where(c => c.engine != e || !c.CanBeActive).Select(c => $"{c.parent.def.defName} engine {(c.engine == e)} active {c.CanBeActive}").ToList();
            Result("gravship on the ground: every part links to the engine and is active", parts.Count >= 5 && bad.Count == 0,
                $"{parts.Count} parts ({string.Join(", ", parts.Select(c => c.parent.def.defName))}), engine components {e.GravshipComponents.Count}; bad: {string.Join("; ", bad)}");
            PlaceWorker_InRangeOfGravEngine pw = new PlaceWorker_InRangeOfGravEngine();
            ThingDef ext = ThingDefOf.GravFieldExtender;
            float max = ext.GetCompProperties<CompProperties_GravshipFacility>().maxDistance;
            IntVec3 eg = e.Position;
            void Check(string name, int k, IntVec3 g, bool expect)
            {
                IntVec3 c = FOn(g, k, map);
                AcceptanceReport r = pw.AllowsPlacing(ext, c, Rot4.North, map);
                float d3 = Range3D.Distance(c, eg, map);
                Result($"grav field extender {name}: {(expect ? "in range" : "out of range")}", r.Accepted == expect && (d3 <= max) == expect,
                    $"{c} floor {k + 1}: 3D {d3:0.00}, raw rows {c.DistanceTo(eg):0.00} (max {max}): {GlRep(r)}");
            }
            Check("on the ground 18 east", 0, eg + new IntVec3(18, 0, 0), true);
            Check("on the ground 19 east", 0, eg + new IntVec3(19, 0, 0), false);
            Check("on floor 2 straight above the engine", 1, eg, true);
            Check("on floor 2, 18 east 5 north (3D 18.8)", 1, eg + new IntVec3(18, 0, 5), true);
            Check("on floor 3, 18 east 5 north (flat 18.7, 3D 19.0)", 2, eg + new IntVec3(18, 0, 5), false);
            Check("on floor 2's south row over the ground's row 2 (raw rows within reach: the old leak)", 1, new IntVec3(eg.x, 0, 2), false);
            Wait(5, "fa_work_go");
        }

        private static void FaWorkGo(Map map, Building bench, string name)
        {
            faWorkCase = name;
            Thing chunk = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("ChunkGranite"));
            GenSpawn.Spawn(chunk, bench.Position + new IntVec3(0, 0, -2), map);
            RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail("Make_StoneBlocksAny") ?? bench.def.AllRecipes.First(r => r.IsIngredient(chunk.def));
            faBill = recipe.MakeNewBill();
            ((IBillGiver)bench).BillStack.AddBill(faBill);
            List<Thing> links = FaLinks(bench);
            Info($"work {name}: bench {bench.Position} floor {ProjectZ.FloorOf(bench.Position, map) + 1}, links {string.Join(", ", links.Select(t => $"{t.def.defName} {t.Position} floor {ProjectZ.FloorOf(t.Position, map) + 1}"))}, " +
                $"WorkTableWorkSpeedFactor {bench.GetStatValue(StatDefOf.WorkTableWorkSpeedFactor):0.000}, recipe {recipe.defName}");
            TickUntil(6000, () => faWorker.CurJob?.bill == faBill && faWorker.jobs.curDriver is JobDriver_DoBill d && d.workLeft > 0f && d.workLeft < faBill.GetWorkAmount(null) - 1f, "fa_work_measure", 10);
        }

        private static JobDriver_DoBill FaDriver() => faWorker.CurJob?.bill == faBill ? faWorker.jobs.curDriver as JobDriver_DoBill : null;

        private static void FaMeasureStart(Map map)
        {
            JobDriver_DoBill d = FaDriver();
            if (d == null)
            {
                Result($"work {faWorkCase}: the colonist works the bill", false, $"job {faWorker.CurJob?.def?.defName} at {faWorker.Position} after {ticksTotal} ticks");
                Finish();
                return;
            }
            faSamples.Clear();
            faSumWork = faSumTickSpeed = 0f;
            faStartTick = faLastTick = Find.TickManager.TicksGame;
            faStartWork = faLastWork = d.workLeft;
            StatDef speed = faBill.recipe.workSpeedStat;
            TickUntil(600, () =>
            {
                JobDriver_DoBill dd = FaDriver();
                if (dd == null)
                {
                    return true;
                }
                int now = Find.TickManager.TicksGame;
                float s = speed == null ? 1f : faWorker.GetStatValue(speed);
                faSamples.Add((faLastWork - dd.workLeft) / Mathf.Max(1, now - faLastTick) / Mathf.Max(0.01f, s));
                faSumWork += faLastWork - dd.workLeft;
                faSumTickSpeed += (now - faLastTick) * s;
                faLastWork = dd.workLeft;
                faLastTick = now;
                return false;
            }, "fa_work_done", 60);
        }

        private static void FaMeasureDone(Map map)
        {
            Building bench = faBill.billStack.billGiver as Building;
            float factor = bench.GetStatValue(StatDefOf.WorkTableWorkSpeedFactor);
            bool still = FaDriver() != null;
            float rate = faSumTickSpeed > 0f ? faSumWork / faSumTickSpeed : 0f;
            Result($"work {faWorkCase}: the colonist works the bill for 600 ticks", still && faSamples.Count >= 9,
                $"{faSamples.Count} windows, work {faStartWork:0} -> {faLastWork:0} over {faLastTick - faStartTick} ticks; per tick / pawn speed {string.Join(" ", faSamples.Select(x => x.ToString("0.000")))}");
            Result($"work {faWorkCase}: work per tick follows the table's speed factor", Mathf.Abs(rate - factor) < 0.01f, $"measured {rate:0.000}, table factor {factor:0.000}");
            if (bench == faBenchA)
            {
                faRateA = rate;
                faFactorA = factor;
                faBill.suspended = true;
                faWorker.jobs.EndCurrentJob(JobCondition.InterruptForced);
                FaWorkGo(map, faBenchB, "B (control, no cabinet)");
                return;
            }
            faRateB = rate;
            faFactorB = factor;
            float fa = faBenchA.GetStatValue(StatDefOf.WorkTableWorkSpeedFactor), fb = faBenchB.GetStatValue(StatDefOf.WorkTableWorkSpeedFactor);
            float oa = faBenchA.GetComp<CompAffectedByFacilities>().GetStatOffset(StatDefOf.WorkTableWorkSpeedFactor);
            float ob = faBenchB.GetComp<CompAffectedByFacilities>().GetStatOffset(StatDefOf.WorkTableWorkSpeedFactor);
            Result("work: the cabinet straight above adds its +0.06 to the table (the control has none), and the table works faster", Mathf.Abs(oa - 0.06f) < 0.001f && Mathf.Abs(ob) < 0.001f && fa > fb + 0.01f,
                $"facility offset A {oa:0.000}, B {ob:0.000}; factors now A {fa:0.000}, B {fb:0.000} (ratio {fa / fb:0.000}); measured A {faRateA:0.000} (factor then {faFactorA:0.000}), B {faRateB:0.000} (factor then {faFactorB:0.000})");
            Info("table A speed factor: " + StatDefOf.WorkTableWorkSpeedFactor.Worker.GetExplanationFull(StatRequest.For(faBenchA), ToStringNumberSense.Factor, fa).Replace("\n", " | "));
            Wait(5, "fa_end");
        }
    }
}
