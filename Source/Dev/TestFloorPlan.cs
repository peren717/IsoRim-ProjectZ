using System;
using System.Collections.Generic;
using System.Linq;
using ProjectZ.Patches;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // A GenStep the test sets the body of: what an addon's generation step would do.
    public class GenStep_FloorPlanProbe : GenStep
    {
        public static Action<Map> Body;

        public override int SeedPart => 7714021;

        public override void Generate(Map map, GenStepParams parms) => Body?.Invoke(map);
    }

    // Mode floorplan (Api 9, Docs/Api.md), the Api alone, no addon. Run with -projectzfloors=2 and =1 (and any other). Every
    // check generates its own map on its own world object, under its own random state, and removes it again.
    //   N   no planner: the game's floors; a planner with no answer: the same, asked once
    //   M   a plan with more floors, its own ground size, on a map asked with another size; no tall mountains; the game's
    //       count unchanged
    //   F   a plan of one floor: no annex, whatever the game's count
    //   G   a plan without a ground size keeps the asked size
    //   T   a throwing planner: logged once, ignored, the next planner decides; alone: the game's floors
    //   I   a plan of 0 or 99 floors: logged, ignored
    //   P   a pocket map: no planner asked, no annex
    //   K   keepUpperContent: what a window wrote on floor 1 stays; without it the second fill wipes it
    //   W   the window: rows out of bounds outside it, in bounds inside; nested; a pathfinder call inside keeps it; false and
    //       harmless on a finished map and for another map; a pocket map generated inside leaves the outer map's generation whole
    //   E   failures: a genStep that opens a window and throws (the game goes on with the next step); an exception out of
    //       generation with a window open: the full size is back, the next map is right, EndFullMapSize stays harmless
    public static partial class TestRunner
    {
        private static int fpAsked;
        private static int fpGame => ProjectZ.FloorsForNewMaps;
        private static GenStepDef fpProbeDef;

        private static GenStepDef FpProbeDef
        {
            get
            {
                if (fpProbeDef == null)
                {
                    fpProbeDef = new GenStepDef { defName = "PZ_FloorPlanProbe", order = 1500f, genStep = new GenStep_FloorPlanProbe() };
                    fpProbeDef.genStep.def = fpProbeDef;
                }
                return fpProbeDef;
            }
        }

        private static bool StepFloorPlan(Map map)
        {
            Rand.PushState(FixtureSeed("floorplan_" + stage));
            try
            {
                return StepFloorPlanInner(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static bool StepFloorPlanInner(Map map)
        {
            switch (stage)
            {
                case "fp_setup": Find.Storyteller.storytellerComps.Clear(); Info($"floorplan: game floors {fpGame}, Api {Api.Version}"); Wait(2, "fp_n"); return true;
                case "fp_n": FpNoPlan(); Wait(2, "fp_m"); return true;
                case "fp_m": FpMore(); Wait(2, "fp_f"); return true;
                case "fp_f": FpOne(); Wait(2, "fp_g"); return true;
                case "fp_g": FpKeepsSize(); Wait(2, "fp_t"); return true;
                case "fp_t": FpThrows(); Wait(2, "fp_i"); return true;
                case "fp_i": FpInvalid(); Wait(2, "fp_p"); return true;
                case "fp_p": FpPocket(map); Wait(2, "fp_k"); return true;
                case "fp_k": FpKeep(); Wait(2, "fp_w"); return true;
                case "fp_w": FpWindow(map); Wait(2, "fp_e"); return true;
                case "fp_e": FpFailures(); Wait(2, "fp_end"); return true;
                case "fp_end": Finish(); return true;
            }
            return false;
        }

        // ---- helpers
        private static MapParent FpParent()
        {
            Settlement s = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
            s.Tile = TileFinder.RandomSettlementTileFor(Faction.OfPlayer);
            s.SetFaction(Faction.OfPlayer);
            Find.WorldObjects.Add(s);
            return s;
        }

        private static Map FpGenerate(MapParent parent, Action<Map> step = null, Action<Map> init = null)
        {
            GenStep_FloorPlanProbe.Body = step;
            IEnumerable<GenStepWithParams> extra = step == null ? null : new[] { new GenStepWithParams(FpProbeDef, default(GenStepParams)) };
            try
            {
                return MapGenerator.GenerateMap(new IntVec3(60, 1, 60), parent, MapGeneratorDefOf.Encounter, extra, init);
            }
            finally
            {
                GenStep_FloorPlanProbe.Body = null;
            }
        }

        private static void FpRemove(Map m, MapParent parent)
        {
            if (m != null && Find.Maps.Contains(m))
            {
                Current.Game.DeinitAndRemoveMap(m, false);
            }
            if (parent != null && !parent.Destroyed)
            {
                parent.Destroy();
            }
        }

        private static void FpWith(Func<MapParent, MapFloorPlan?> planner, Action body)
        {
            Api.RegisterMapFloorPlanner(planner);
            try
            {
                body();
            }
            finally
            {
                Api.UnregisterMapFloorPlanner(planner);
            }
        }

        private static string FpShow(Map m)
        {
            AnnexMapComponent a = m?.GetComponent<AnnexMapComponent>();
            return $"size {m?.Size}, floors {a?.floors}, groundZ {a?.groundZ}, stride {a?.stride}, tall {a?.tallMountains}";
        }

        // the size of a 60 x 60 map under the game's floors
        private static bool FpGameLayout(Map m)
        {
            AnnexMapComponent a = m.GetComponent<AnnexMapComponent>();
            return fpGame > 1 ? a.HasAnnex && a.floors == fpGame && a.groundZ == 60 && m.Size.z == fpGame * ProjectZ.StrideFor(60) : !a.HasAnnex && m.Size == new IntVec3(60, 1, 60);
        }

        // ---- the checks
        private static void FpNoPlan()
        {
            MapParent p = FpParent();
            Map m = FpGenerate(p);
            Result("N no planner: the game's floors", FpGameLayout(m), FpShow(m));
            FpRemove(m, p);
            fpAsked = 0;
            p = FpParent();
            Func<MapParent, MapFloorPlan?> none = x =>
            {
                fpAsked++;
                return null;
            };
            FpWith(none, () => m = FpGenerate(p));
            Result("N a planner with no answer: the same, asked once", FpGameLayout(m) && fpAsked == 1, $"{FpShow(m)}, asked {fpAsked}");
            FpRemove(m, p);
        }

        private static void FpMore()
        {
            int want = Math.Max(fpGame + 1, 3);
            MapParent p = FpParent();
            Map m = null;
            FpWith(x => x == p ? new MapFloorPlan { groundSize = new IntVec3(64, 1, 70), floors = want } : (MapFloorPlan?)null, () => m = FpGenerate(p));
            AnnexMapComponent a = m.GetComponent<AnnexMapComponent>();
            int stride = ProjectZ.StrideFor(70);
            Result("M a plan with more floors and its own ground", a.HasAnnex && a.floors == want && a.groundZ == 70 && a.stride == stride && m.Size == new IntVec3(64, 1, want * stride), FpShow(m));
            Result("M no tall mountains on a planned map, upper rows open air", !a.tallMountains && Levels.Cell(new IntVec3(30, 0, 30), 1, m).GetTerrain(m) == PZDefOf.PZ_OpenAir, FpShow(m));
            Result("M the game's count stays", WorldFloors.ForThisGame == fpGame, $"{WorldFloors.ForThisGame}");
            FpRemove(m, p);
        }

        private static void FpOne()
        {
            MapParent p = FpParent();
            Map m = null;
            FpWith(x => new MapFloorPlan { groundSize = new IntVec3(64, 1, 70), floors = 1 }, () => m = FpGenerate(p));
            Result("F a plan of one floor: no annex", !m.GetComponent<AnnexMapComponent>().HasAnnex && !ProjectZ.HasAnnex(m) && m.Size == new IntVec3(64, 1, 70), FpShow(m));
            FpRemove(m, p);
        }

        private static void FpKeepsSize()
        {
            MapParent p = FpParent();
            Map m = null;
            FpWith(x => new MapFloorPlan { floors = 2 }, () => m = FpGenerate(p));
            AnnexMapComponent a = m.GetComponent<AnnexMapComponent>();
            Result("G a plan without a size keeps the asked size", a.HasAnnex && a.floors == 2 && a.groundZ == 60 && m.Size.x == 60, FpShow(m));
            FpRemove(m, p);
        }

        private static void FpThrows()
        {
            int e0 = errors;
            MapParent p = FpParent();
            Map m = null;
            Func<MapParent, MapFloorPlan?> bad = x => throw new InvalidOperationException("planner failure on purpose");
            FpWith(bad, () => FpWith(x => new MapFloorPlan { floors = 3 }, () => m = FpGenerate(p)));
            Result("T a throwing planner is logged once and the next one decides", m.GetComponent<AnnexMapComponent>().floors == 3 && errors == e0 + 1, $"{FpShow(m)}, errors {errors - e0}");
            FpRemove(m, p);
            errors = e0;
            p = FpParent();
            FpWith(bad, () => m = FpGenerate(p));
            Result("T alone: the game's floors", FpGameLayout(m) && errors == e0 + 1, $"{FpShow(m)}, errors {errors - e0}");
            FpRemove(m, p);
            errors = e0;
        }

        private static void FpInvalid()
        {
            foreach (int floors in new[] { 0, 99 })
            {
                int e0 = errors;
                MapParent p = FpParent();
                Map m = null;
                FpWith(x => new MapFloorPlan { floors = floors }, () => m = FpGenerate(p));
                Result($"I a plan of {floors} floors is logged and ignored", FpGameLayout(m) && errors == e0 + 1, $"{FpShow(m)}, errors {errors - e0}");
                FpRemove(m, p);
                errors = e0;
            }
        }

        private static void FpPocket(Map home)
        {
            fpAsked = 0;
            Map m = null;
            FpWith(x =>
            {
                fpAsked++;
                return new MapFloorPlan { floors = 3 };
            }, () => m = PocketMapUtility.GeneratePocketMap(new IntVec3(40, 1, 40), DefDatabase<MapGeneratorDef>.GetNamed("SpacePocket"), null, home));
            Result("P a pocket map is never planned", fpAsked == 0 && !ProjectZ.HasAnnex(m) && m.Size == new IntVec3(40, 1, 40), $"asked {fpAsked}, {FpShow(m)}");
            PocketMapUtility.DestroyPocketMap(m);
        }

        private static void FpKeep()
        {
            foreach (bool keep in new[] { true, false })
            {
                MapParent p = FpParent();
                Map m = null;
                IntVec3 up = IntVec3.Invalid;
                bool outside = true, inside = false;
                FpWith(x => new MapFloorPlan { floors = 2, keepUpperContent = keep }, () => m = FpGenerate(p, g =>
                {
                    up = Levels.Cell(new IntVec3(30, 0, 30), 1, g);
                    outside = up.InBounds(g);
                    if (Api.TryBeginFullMapSize(g))
                    {
                        try
                        {
                            inside = up.InBounds(g);
                            g.terrainGrid.SetTerrain(up, TerrainDefOf.Concrete);
                        }
                        finally
                        {
                            Api.EndFullMapSize(g);
                        }
                    }
                }));
                TerrainDef t = up.GetTerrain(m);
                Result($"K keepUpperContent {keep}: floor 1 {(keep ? "keeps what generation wrote" : "is wiped")}", !outside && inside && t == (keep ? TerrainDefOf.Concrete : PZDefOf.PZ_OpenAir),
                    $"row in bounds shrunk {outside}, in the window {inside}, terrain {t?.defName}");
                FpRemove(m, p);
            }
        }

        private static void FpWindow(Map home)
        {
            MapParent p = FpParent();
            Map m = null, pocket = null;
            int shrunk = 0, full = 0, nested = 0, afterOne = 0, afterTwo = 0, afterPath = 0, afterPocket = 0, stillShrunk = 0;
            bool other = true;
            FpWith(x => new MapFloorPlan { floors = 2, keepUpperContent = true }, () => m = FpGenerate(p, g =>
            {
                shrunk = g.Size.z;
                other = Api.TryBeginFullMapSize(home);
                bool a = Api.TryBeginFullMapSize(g);
                full = g.Size.z;
                bool b = Api.TryBeginFullMapSize(g);
                nested = g.Size.z;
                Api.EndFullMapSize(g);
                afterOne = g.Size.z;
                IntVec3 c = new IntVec3(30, 0, 30);
                PawnPath path = g.pathFinder.FindPathNow(c, c + new IntVec3(3, 0, 0), TraverseParms.For(TraverseMode.PassDoors));
                path?.Dispose();
                afterPath = g.Size.z;
                // an addon's pocket map generated in the middle of this map's generation
                pocket = PocketMapUtility.GeneratePocketMap(new IntVec3(30, 1, 30), DefDatabase<MapGeneratorDef>.GetNamed("SpacePocket"), null, home);
                afterPocket = g.Size.z;
                Api.EndFullMapSize(g);
                afterTwo = g.Size.z;
                Api.EndFullMapSize(g);
                stillShrunk = g.Size.z;
                Info($"window: begin {a}, nested {b}");
            }));
            int stride = ProjectZ.StrideFor(60);
            Result("W another map: false", !other, "");
            Result("W a window, nested, ended twice", shrunk == 60 && full == 2 * stride && nested == 2 * stride && afterOne == 2 * stride && afterTwo == 60 && stillShrunk == 60,
                $"shrunk {shrunk}, full {full}, nested {nested}, after one end {afterOne}, after both {afterTwo}, one end too many {stillShrunk}");
            Result("W a pathfinder call inside keeps the window", afterPath == 2 * stride, $"{afterPath}");
            Result("W a pocket map inside leaves this generation whole", afterPocket == 2 * stride && m.Size.z == 2 * stride && ProjectZ.HasAnnex(m), $"during {afterPocket}, {FpShow(m)}, pending now {AnnexGen.pending != null}");
            Result("W after generation: false and harmless", !Api.TryBeginFullMapSize(m) && m.Size.z == 2 * stride, FpShow(m));
            Api.EndFullMapSize(m);
            Result("W ending on a finished map changes nothing", m.Size.z == 2 * stride, FpShow(m));
            if (pocket != null)
            {
                PocketMapUtility.DestroyPocketMap(pocket);
            }
            FpRemove(m, p);
        }

        private static void FpFailures()
        {
            // a genStep throws with its window open: the game logs it and goes on; FinalizeInit restores the size
            int e0 = errors;
            MapParent p = FpParent();
            Map m = null;
            FpWith(x => new MapFloorPlan { floors = 2, keepUpperContent = true }, () => m = FpGenerate(p, g =>
            {
                Api.TryBeginFullMapSize(g);
                throw new InvalidOperationException("genStep failure on purpose");
            }));
            int stride = ProjectZ.StrideFor(60);
            Result("E a genStep throws with its window open: the map is whole", m != null && m.Size.z == 2 * stride && ProjectZ.HasAnnex(m) && errors >= e0 + 1, $"{FpShow(m)}, errors {errors - e0}");
            Api.EndFullMapSize(m);
            Result("E ending it afterwards changes nothing", m.Size.z == 2 * stride, FpShow(m));
            FpRemove(m, p);
            errors = e0;

            // an exception out of generation (extraInitBeforeContentGen is outside the game's per-step catch), with a window open
            p = FpParent();
            Map failed = null;
            bool thrown = false;
            int mapsBefore = Find.Maps.Count, w0 = warnings;
            FpWith(x => new MapFloorPlan { floors = 2, keepUpperContent = true }, () =>
            {
                try
                {
                    FpGenerate(p, null, g =>
                    {
                        failed = g;
                        Api.TryBeginFullMapSize(g);
                        throw new InvalidOperationException("generation failure on purpose");
                    });
                }
                catch (InvalidOperationException)
                {
                    thrown = true;
                }
            });
            Result("E generation fails: the size is restored, nothing is left pending", thrown && failed != null && failed.Size.z == 2 * stride && AnnexGen.pending == null,
                $"thrown {thrown}, {FpShow(failed)}, pending {AnnexGen.pending != null}, maps {mapsBefore} -> {Find.Maps.Count}, warnings {warnings - w0}");
            Api.EndFullMapSize(failed);
            Result("E EndFullMapSize after the failure changes nothing", failed.Size.z == 2 * stride, FpShow(failed));
            if (failed != null && Find.Maps.Contains(failed))
            {
                Find.Maps.Remove(failed);   // (never finished: the game's removal would walk its unbuilt parts)
            }
            if (!p.Destroyed)
            {
                p.Destroy();
            }
            errors = e0;

            p = FpParent();
            m = FpGenerate(p);
            Result("E the next map is the game's as before", FpGameLayout(m), FpShow(m));
            FpRemove(m, p);
        }
    }
}
