using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ.Dev
{
    // Mode stairsreentry (GitHub issue 7; run with -projectzfloors=3 -projectzseed=7, alone or with CE and its pack): stairs
    // taken off the map while something stands on their landing. Real flow on seeded ticks, each case on its own strip,
    // under its own seed:
    //   kill cases: a real IED (TrapIED_HighExplosive, its own wick and detonation: the game's Explosion, or CE's ExplosionCE
    //   under CE) two cells off the low end of damaged stairs (1 hit point, so the blast kills them; two of their cells are
    //   in the blast) whose landing they laid on open air with the roof under it; ticks until the explosion has ended and
    //   nothing it threw is in flight (CE fragments);
    //   deconstruct case: the stairs designated for deconstruction (the game's designator), a colonist next to them with
    //   construction work does it; ticks until they are gone.
    //   Then 120 more ticks (the collapse buffer, falls).
    //   g_item    ground -> floor 2, a steel stack on the landing
    //   g_pawn    ground -> floor 2, a drafted colonist on the landing
    //   u_item    floor 2 -> floor 3 on a granite slab pavilion (the IED on the slab), a steel stack on the landing
    //   s_item    ship stairs (Odyssey) on a substructure patch, their landing a ship deck, a steel stack on it
    //   d_item    ground -> floor 2, deconstructed by a colonist, a steel stack on the landing
    //   g_empty   ground -> floor 2, nothing on the landing (control)
    // Checked per case: the landing laid (fixture); the stairs destroyed once (Destroy calls on them counted) and gone from
    // the grids, no error logged during the case, the landing taken with them and the roof they laid under it gone, what
    // stood on the landing came down on the floor below; the former solid end walkable on the path grid as stored (and as
    // the game would work it out now), and a colonist ordered there walks onto it. Recorded: roofs broken by falls.
    // Before the fix the landing's fall broke the roof over the stairs' solid end while the stairs were still despawning:
    // the roof's crush hit them a second time ("Tried to despawn ... which is already destroyed", then "Explosion could not
    // affect cell ...: NullReferenceException" in Building.DeSpawn).
    public static partial class TestRunner
    {
        private sealed class ReentryCase
        {
            public string name;
            public int cx;           // the strip's centre column
            public int k;            // the stairs' level
            public string onLanding; // "item", "pawn" or null
            public bool ship;        // ship stairs on a substructure patch
            public bool deconstruct; // taken down by a colonist (else killed by the IED)
        }

        private static List<ReentryCase> seCases;
        private static int seCase, seErrors0, seDestroyCalls, seRoofsBroken0;
        private static bool sePatched, seRecErrors;
        private static Building_Stairs seStairs;
        private static Thing seIed, seOnLanding;
        private static ThingDef seIedDef;
        private static TerrainDef seSubstructure;
        private static IntVec3 seTop, seBelow, seSolid;
        private static Pawn seLander, seWorker;
        private static readonly List<string> seErrorLines = new List<string>();

        private const string SeIedDefName = "TrapIED_HighExplosive";
        private const string SeSubstructureName = "Substructure";
        private const int SeHalfWidth = 6;         // the strip: columns cx-6..cx+6
        private const int SeHalfDepth = 9;         // rows cz-9..cz+9
        private const int SeIedOffset = 3;         // the IED this many cells south of the stairs' centre (2 off the low end)
        private const int SeSteelStack = 75;       // the stack on the landing
        private const int SeBlastMaxTicks = 600;   // the IED's wick and blast end well within this
        private const int SeDeconMaxTicks = 20000; // deconstruction by one colonist next to the stairs
        private const int SeWalkMaxTicks = 600;    // a colonist two cells away walking onto the former solid end
        private const int SeAfterTicks = 120;

        // (test only) a Destroy call on the case's stairs, nested ones included
        public static void SeDestroy_Prefix(Thing __instance)
        {
            if (seRecErrors && __instance == seStairs)
            {
                seDestroyCalls++;
            }
        }

        // (LogPostfix) the case's errors, first line of each
        internal static void SeSawError(string text)
        {
            if (seRecErrors && seErrorLines.Count < 6)
            {
                seErrorLines.Add((text ?? "").Split('\n')[0]);
            }
        }

        private static bool StepStairsReentry(Map map)
        {
            switch (stage)
            {
                case "se_setup":
                    FQuiet(map, true);
                    Seeded("stairsreentry_setup", () => SetupStairsReentry(map));
                    return true;
                case "se_case":
                    if (seCase >= seCases.Count)
                    {
                        Finish();
                        return true;
                    }
                    Seeded("stairsreentry_" + seCases[seCase].name, () => SeStart(map, seCases[seCase]));
                    return true;
                case "se_gone":
                    tickWatch = null;
                    SeedTicks("stairsreentry_after_" + seCases[seCase].name);
                    StartTicks(SeAfterTicks, "se_report");
                    return true;
                case "se_report":
                    Seeded("stairsreentry_report_" + seCases[seCase].name, () => SeReport(map, seCases[seCase]));
                    return true;
                case "se_walked":
                    tickWatch = null;
                    SeWalked(map, seCases[seCase]);
                    seCase++;
                    Wait(2, "se_case");
                    return true;
            }
            return false;
        }

        private static void SetupStairsReentry(Map map)
        {
            if (!sePatched)
            {
                sePatched = true;
                new Harmony("Peren.IsoRimProjectZ.test.stairsreentry").Patch(AccessTools.Method(typeof(Thing), nameof(Thing.Destroy)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(SeDestroy_Prefix)));
            }
            AnnexMapComponent an = ProjectZ.Annex(map);
            seIedDef = DefDatabase<ThingDef>.GetNamedSilentFail(SeIedDefName);
            seSubstructure = DefDatabase<TerrainDef>.GetNamedSilentFail(SeSubstructureName);
            if (an == null || an.MaxLevel < 2 || seIedDef == null || Colonists(map).Count < 2)
            {
                Result("stairsreentry setup", false, $"needs -projectzfloors=3, the IED def and 2 colonists: top level {an?.MaxLevel}, IED {seIedDef != null}, colonists {Colonists(map).Count}");
                Finish();
                return;
            }
            int w = map.Size.x;
            seCases = new List<ReentryCase>
            {
                new ReentryCase { name = "g_item", cx = w * 1 / 7, k = 0, onLanding = "item" },
                new ReentryCase { name = "g_pawn", cx = w * 2 / 7, k = 0, onLanding = "pawn" },
                new ReentryCase { name = "u_item", cx = w * 3 / 7, k = 1, onLanding = "item" },
                new ReentryCase { name = "s_item", cx = w * 4 / 7, k = 0, onLanding = "item", ship = true },
                new ReentryCase { name = "d_item", cx = w * 5 / 7, k = 0, onLanding = "item", deconstruct = true },
                new ReentryCase { name = "g_empty", cx = w * 6 / 7, k = 0, onLanding = null },
            };
            if (PZDefOf.PZ_ShipStairs == null || seSubstructure == null)
            {
                Info("stairsreentry: no Odyssey (ship stairs or substructure missing): case s_item left out");
                seCases.RemoveAll(c => c.ship);
            }
            List<Pawn> cols = Colonists(map).OrderBy(p => p.thingIDNumber).ToList();
            seLander = cols[0];
            seWorker = cols[1];
            SetWork(map, false);
            foreach (Pawn p in cols)
            {
                Draft(p, true);
                SePark(p, map);
            }
            DebugSettings.enableRandomMentalStates = false;
            Find.PlaySettings.autoRebuild = false;
            map.weatherManager.TransitionTo(WeatherDefOf.Clear);
            Info($"stairsreentry: map {map.Size}, groundZ {an.groundZ}, top level {an.MaxLevel}; IED explosive {seIedDef.GetCompProperties<CompProperties_Explosive>()?.explosiveRadius} radius; CE active {ModsConfig.IsActive("CETeam.CombatExtended")}; cases {string.Join(" ", seCases.Select(c => c.name))}; seed {FixtureSeedBase}");
            seCase = 0;
            Wait(10, "se_case");
        }

        // Where a colonist waits between cases: west of every strip.
        // A colonist ready for its part in a case (its own fixture): alive, unhurt, no job.
        private static void SeReady(Pawn p)
        {
            if (p.Dead)
            {
                ResurrectionUtility.TryResurrect(p);
            }
            foreach (Hediff h in p.health.hediffSet.hediffs.Where(h => h is Hediff_Injury || h.def == HediffDefOf.BloodLoss).ToList())
            {
                p.health.RemoveHediff(h);
            }
            p.jobs?.StopAll();
        }

        private static void SePark(Pawn p, Map map)
        {
            List<Pawn> cols = Colonists(map).OrderBy(x => x.thingIDNumber).ToList();
            p.jobs?.StopAll();
            Teleport(p, new IntVec3(map.Size.x / 20 + cols.IndexOf(p), 0, ProjectZ.Annex(map).groundZ / 2));
        }

        // A case's strip cleared on every level (ground soil, upper levels open air, no roofs, no fog); for stairs on an upper
        // level, a granite slab pavilion under the strip's middle (the ground roofed under it); for ship stairs, a substructure
        // patch under them and around.
        private static void SeFixture(Map map, ReentryCase c, int cz)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            foreach (IntVec3 g in CellRect.FromLimits(c.cx - SeHalfWidth, cz - SeHalfDepth, c.cx + SeHalfWidth, cz + SeHalfDepth))
            {
                for (int k = 0; k <= an.MaxLevel; k++)
                {
                    IntVec3 u = ProjectZ.CellOnFloor(g, k, map);
                    if (!u.IsValid || !u.InBounds(map))
                    {
                        continue;
                    }
                    foreach (Thing t in u.GetThingList(map).ToList())
                    {
                        if (!(t is Pawn) && t.def.destroyable)
                        {
                            t.Destroy();
                        }
                    }
                    if (k == 0)
                    {
                        TerrainDef td = g.GetTerrain(map);
                        if (td.IsWater || td.passability == Traversability.Impassable)
                        {
                            map.terrainGrid.SetTerrain(g, TerrainDefOf.Soil);
                        }
                    }
                    else
                    {
                        map.terrainGrid.SetTerrain(u, PZDefOf.PZ_OpenAir);
                    }
                    map.roofGrid.SetRoof(u, null);
                    map.fogGrid.Unfog(u);
                }
            }
            if (c.k >= 1)
            {
                TerrainDef granite = SlabDefs.For(ThingDefOf.BlocksGranite);
                foreach (IntVec3 g in CellRect.FromLimits(c.cx - SeHalfWidth + 1, cz - SeHalfDepth + 1, c.cx + SeHalfWidth - 1, cz + SeHalfDepth - 1))
                {
                    map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                    for (int k = 1; k <= c.k; k++)
                    {
                        map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(g, k, map), granite);
                    }
                }
            }
            if (c.ship)
            {
                foreach (IntVec3 g in CellRect.FromLimits(c.cx - SeHalfWidth + 2, cz - SeHalfDepth + 2, c.cx + SeHalfWidth - 2, cz + SeHalfDepth - 2))
                {
                    // (a foundation goes only under a plain top: a road or floor there is taken up first)
                    while (map.terrainGrid.UnderTerrainAt(g) != null)
                    {
                        map.terrainGrid.RemoveTopLayer(g, doLeavings: false);
                    }
                    map.terrainGrid.SetFoundation(g, seSubstructure);
                }
            }
        }

        private static void SeStart(Map map, ReentryCase c)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            int cz = an.groundZ / 2;
            SeFixture(map, c, cz);
            IntVec3 center = ProjectZ.CellOnFloor(new IntVec3(c.cx, 0, cz), c.k, map);
            ThingDef def = c.ship ? PZDefOf.PZ_ShipStairs : PZDefOf.PZ_Stairs;
            Building_Stairs s = (Building_Stairs)ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.BlocksGranite : null);
            s.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(s, center, map, Rot4.North);
            seStairs = s;
            seTop = s.TopCell;
            seBelow = FloorRoof.Below(seTop, map);
            seSolid = StairShape.Solid(s.Position, s.Rotation);
            TerrainDef landing = seTop.IsValid ? seTop.GetTerrain(map) : null;
            RoofDef roofUnder = seBelow.IsValid ? map.roofGrid.RoofAt(seBelow) : null;
            Result($"stairsreentry {c.name}: fixture, the {def.label} laid their landing and the roof under it",
                landing != null && landing != PZDefOf.PZ_OpenAir && roofUnder != null, $"landing {seTop} {landing?.defName ?? "-"}, roof under it {seBelow} {roofUnder?.defName ?? "none"}");
            seOnLanding = null;
            if (c.onLanding == "item")
            {
                Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                steel.stackCount = SeSteelStack;
                seOnLanding = GenSpawn.Spawn(steel, seTop, map);
            }
            else if (c.onLanding == "pawn")
            {
                seLander.jobs?.StopAll();
                Teleport(seLander, seTop);
                Draft(seLander, true);
                seOnLanding = seLander;
            }
            seErrors0 = errors;
            seDestroyCalls = 0;
            seRoofsBroken0 = Gravity.RoofsBroken;
            seErrorLines.Clear();
            if (c.deconstruct)
            {
                SeReady(seWorker);
                Teleport(seWorker, center + IntVec3.East * 2);
                Draft(seWorker, false);
                SetWork(map, true);
                Designator_Deconstruct dd = new Designator_Deconstruct();
                AcceptanceReport ok = dd.CanDesignateThing(s);
                if (ok.Accepted)
                {
                    dd.DesignateThing(s);
                }
                Info($"---- {c.name}: {s} at {center} (level {c.k}) hp {s.HitPoints}/{s.MaxHitPoints}; landing {seTop} {landing?.defName}, roof under it {seBelow} {roofUnder?.defName ?? "none"}; on the landing {seOnLanding?.ToString() ?? "nothing"}; deconstruct designated {ok.Accepted} '{ok.Reason}', worker {seWorker} at {seWorker.Position}");
                seRecErrors = true;
                SeedTicks("stairsreentry_decon_" + c.name);
                TickUntil(SeDeconMaxTicks, () => s.Destroyed, "se_gone", 20);
                return;
            }
            s.HitPoints = 1;
            seIed = ThingMaker.MakeThing(seIedDef);
            seIed.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(seIed, center - Rot4.North.FacingCell * SeIedOffset, map, Rot4.North);
            Info($"---- {c.name}: {s} at {center} (level {c.k}) hp {s.HitPoints}/{s.MaxHitPoints}; landing {seTop} {landing?.defName}, roof under it {seBelow} {roofUnder?.defName ?? "none"}; on the landing {seOnLanding?.ToString() ?? "nothing"}{(seOnLanding != null ? " at " + seOnLanding.Position : "")}; IED {seIed} at {seIed.Position}");
            seRecErrors = true;
            seIed.TryGetComp<CompExplosive>().StartWick();
            SeedTicks("stairsreentry_blast_" + c.name);
            TickUntil(SeBlastMaxTicks, () => seIed.Destroyed && !map.listerThings.AllThings.Any(t => t is Explosion) && !map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).Any(), "se_gone", 1);
        }

        private static void SeReport(Map map, ReentryCase c)
        {
            seRecErrors = false;
            if (c.deconstruct)
            {
                SetWork(map, false);
                Draft(seWorker, true);
            }
            int errs = errors - seErrors0;
            Building_Stairs s = seStairs;
            CellRect rect = StairShape.Footprint(s.Position, s.Rotation);
            bool edificeClean = rect.All(x => !x.InBounds(map) || map.edificeGrid[x] != s);
            string landing = seTop.GetTerrain(map).defName;
            RoofDef roof = map.roofGrid.RoofAt(seBelow);
            Info($"{c.name}: stairs destroyed {s.Destroyed}, spawned {s.Spawned}, Destroy calls on them {seDestroyCalls}; edifice grid clean {edificeClean}; landing {seTop} {landing}; roof under it {roof?.defName ?? "none"}; roofs broken by falls {Gravity.RoofsBroken - seRoofsBroken0}; errors during the case {errs}{(seErrorLines.Count > 0 ? ": " + string.Join(" | ", seErrorLines) : "")}");
            Result($"stairsreentry {c.name}: no error logged while the stairs went", errs == 0, $"{errs} errors{(seErrorLines.Count > 0 ? ": " + string.Join(" | ", seErrorLines) : "")}");
            Result($"stairsreentry {c.name}: the stairs destroyed once and gone from the map's grids", s.Destroyed && seDestroyCalls == 1 && !s.Spawned && edificeClean && !rect.Any(x => x.InBounds(map) && x.GetThingList(map).Contains(s)),
                $"destroyed {s.Destroyed}, Destroy calls {seDestroyCalls}, spawned {s.Spawned}, edifice grid clean {edificeClean}");
            Result($"stairsreentry {c.name}: the landing went with the stairs, and the roof they laid under it", landing == PZDefOf.PZ_OpenAir.defName && roof == null,
                $"landing {seTop} {landing}, roof under it {seBelow} {roof?.defName ?? "none"}");
            if (seOnLanding != null)
            {
                Thing t = seOnLanding is Pawn p && p.Dead ? (Thing)p.Corpse : seOnLanding;
                IntVec3 at = t != null && t.Spawned ? t.Position : IntVec3.Invalid;
                int floor = at.IsValid ? ProjectZ.FloorOf(at, map) : -1;
                Result($"stairsreentry {c.name}: what stood on the landing came down on the floor below", at.IsValid && floor == c.k && !Gravity.Air(at, map),
                    $"{seOnLanding} {(seOnLanding is Pawn pp ? (pp.Dead ? "dead" : "alive") : "")} at {at} floor {floor + 1} (want floor {c.k + 1}){(at.IsValid ? ", on " + at.GetTerrain(map).defName : "")}");
            }
            if (seOnLanding == seLander && !seLander.Dead)
            {
                SePark(seLander, map);
            }
            // The former solid end: walkable on the path grid as stored, and as the game works it out now; then a colonist
            // two cells west, ordered there, walks onto it.
            Verse.AI.PathGrid pg = map.pathing.Normal.pathGrid;
            int stored = pg.Cost(seSolid);
            int fresh = pg.CalculatedCostAt(seSolid, true, IntVec3.Invalid);
            Info($"{c.name}: former solid end {seSolid}: path cost stored {stored}, worked out now {fresh}, walkable {pg.Walkable(seSolid)}, standable {seSolid.Standable(map)}");
            Result($"stairsreentry {c.name}: the former solid end is walkable on the path grid", pg.Walkable(seSolid) && stored == fresh, $"{seSolid}: stored {stored}, worked out now {fresh}");
            Pawn walker = seWorker;
            SeReady(walker);
            Teleport(walker, seSolid + IntVec3.West * 2);
            GoTo(walker, seSolid);
            Info($"{c.name}: {walker} at {walker.Position} ordered onto {seSolid}: job {walker.CurJobDef?.defName ?? "none"}");
            SeedTicks("stairsreentry_walk_" + c.name);
            TickUntil(SeWalkMaxTicks, () => walker.Position == seSolid, "se_walked", 5);
        }

        private static void SeWalked(Map map, ReentryCase c)
        {
            Pawn walker = seWorker;
            Result($"stairsreentry {c.name}: a colonist ordered onto the former solid end walks onto it", walker.Position == seSolid, $"{walker} at {walker.Position} after {ticksTotal} ticks (target {seSolid})");
            SePark(walker, map);
        }
    }
}
