using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Test mode shipfloors (-projectzfloors=3, Odyssey): a gravship taller than its destination's floors. The ship is the
    // 13 x 11 test ship with ship decks over all of it on floor 2 and over its west half on floor 3 (height 2: it needs 3
    // floors), a wall, items and a colonist on every floor. One case per run, -projectzsq=<case>:
    //   t1  T1 the destination on the world map, no flight. The validator the game registered through the console's own
    //       call (launch and "View range") is asked: an empty tile accepted; a tile whose map has 2 floors, and one whose
    //       map has no floors above the ground, refused with the landing's own message; a tile whose map has 3 floors
    //       accepted; with empty tanks the game's own reason comes first and ours is not added; another user of the
    //       picker gets its own validator back, unwrapped.
    //   n2  T2 a new map whose plan (Api floor planner) says 2 floors: real flight (console, tile picker, cutscenes); the
    //       map has the 3 floors the ship needs, the plan's ground size, no tall mountains; the game's own marker spot is
    //       accepted; Confirm there lands the ship whole (the set as before, every probe on its floor, no error). In
    //       flight: maps generated for something else (no ship in their generation call) keep the plan's or the game's
    //       floors exactly.
    //   n1  T2 the same with a plan of 1 floor (no floors above the ground).
    //   k2  T2 a plan of 2 floors with keepUpperContent: what generation wrote on the plan's floor 2 stays, what it wrote
    //       on the added floor 3 is open air again.
    //   x2  T3 an existing map with 2 floors as the place of arrival (a settlement made there DURING the flight: the
    //       picker refuses such a tile): no cell accepted, Confirm refused by the game, "Abort landing" (its dialog)
    //       leads to a new map with exactly the game's floors (T5, no raise) where the ship lands whole.
    //   g   T5 no plan, tall mountains (-projectztallmountains), the most mountainous neighbouring tile: exactly the game's
    //       floors, no raise, tall mountains on, the marker spot accepted, the ship lands whole.
    // T4 (the height asked before the launch is the height in flight) is checked by the shared flight driver in every
    // real launch of every ship mode (TestShipFly.SfFly).
    public static partial class TestRunner
    {
        private static string sqCase = "";
        internal static bool sqExisting;   // (the arrival is at an existing map: SfFly waits for landingMap, not for a spawned marker)
        private static int sqPlanFloors, sqErrorsAt, sqRaisesAt, sqLanding, sqSetBefore, sqProbeRan;
        private static string sqFloorsBefore = "";
        private static IntVec3 sqKeptCell, sqAddedCell;
        private static PlanetTile sqPlanTile = PlanetTile.Invalid;
        private static Map sqLowMap;
        private static Func<MapParent, MapFloorPlan?> sqPlanner;
        private static readonly List<(string id, string def)> sqThings = new List<(string, string)>();
        private static readonly Dictionary<string, (string id, int floor)> sqProbe = new Dictionary<string, (string, int)>();
        private static readonly AccessTools.FieldRef<List<Message>> sqLive = AccessTools.StaticFieldRefAccess<List<Message>>(AccessTools.Field(typeof(Messages), "liveMessages"));

        private const int SqHeight = 2;    // the test ship's top floor (decks on floors 2 and 3)

        private static void StepShipFloors(Map map)
        {
            switch (stage)
            {
                case "sq_setup": SqSetup(map); return;
                case "sq_launch": SqLaunch(map); return;
                case "sq_picker": SqPicker(map); return;
                case "sq_landed": SqLanded(); return;
                case "sq_end": SqEnd(); return;
            }
        }

        private static void SqEnd()
        {
            if (sqPlanner != null)
            {
                Api.UnregisterMapFloorPlanner(sqPlanner);
                sqPlanner = null;
            }
            SqProbeStep(false);
            Finish();
        }

        private static MapGeneratorDef SqSettlementGenerator => WorldObjectDefOf.Settlement.mapGenerator ?? MapGeneratorDefOf.Base_Player;

        // (k2) the probe genStep in the generation of the player's new settlement map, and out again
        private static void SqProbeStep(bool on)
        {
            List<GenStepDef> steps = SqSettlementGenerator.genSteps;
            steps.Remove(FpProbeDef);
            GenStep_FloorPlanProbe.Body = null;
            if (!on)
            {
                return;
            }
            steps.Add(FpProbeDef);
            GenStep_FloorPlanProbe.Body = m =>
            {
                sqProbeRan = -1;
                if (!Api.TryBeginFullMapSize(m))
                {
                    return;
                }
                try
                {
                    IntVec3 g = new IntVec3(8, 0, 8);
                    sqKeptCell = Levels.Cell(g, 1, m);
                    sqAddedCell = Levels.Cell(g, 2, m);
                    sqProbeRan = 0;
                    foreach (IntVec3 c in new[] { sqKeptCell, sqAddedCell })
                    {
                        if (c.IsValid && c.InBounds(m))
                        {
                            m.terrainGrid.SetTerrain(c, TerrainDefOf.Concrete);
                            sqProbeRan++;
                        }
                    }
                }
                finally
                {
                    Api.EndFullMapSize(m);
                }
            };
        }

        private static void SqSetup(Map map)
        {
            sqCase = GenCommandLine.TryGetCommandLineArg("projectzsq", out string c) ? c.ToLowerInvariant() : "";
            huCase = "sq_" + sqCase;
            sfMidHook = null;
            sfLandHook = null;
            sqExisting = false;
            sqLowMap = null;
            if (!new[] { "t1", "n2", "n1", "k2", "x2", "g" }.Contains(sqCase))
            {
                Result("shipfloors: which case", false, $"-projectzsq=t1, n2, n1, k2, x2, g (got '{sqCase}')");
                Finish();
                return;
            }
            if (!HuField(map, 3))
            {
                return;
            }
            Building_GravEngine e = HuShip(map);
            HuDecks(map, 1, 0, 0, 12, 10);
            HuDecks(map, 2, 0, 0, 6, 10);
            // probes: a wall and an item on each upper deck, a colonist on every floor
            sqProbe.Clear();
            sqProbe["wall on floor 3"] = (Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SFF(2, 2, 2, map), map).ThingID, 2);
            sqProbe["wall on floor 2"] = (Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SFF(10, 2, 1, map), map).ThingID, 1);
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 75;
            sqProbe["steel on floor 3"] = (GenSpawn.Spawn(steel, SFF(3, 4, 2, map), map).ThingID, 2);
            Thing comps = ThingMaker.MakeThing(ThingDefOf.ComponentIndustrial);
            comps.stackCount = 5;
            sqProbe["components on floor 2"] = (GenSpawn.Spawn(comps, SFF(10, 4, 1, map), map).ThingID, 1);
            List<Pawn> cols = Colonists(map).OrderBy(p => p.thingIDNumber).ToList();
            IntVec3[] at = { SF(5, 2), SFF(9, 5, 1, map), SFF(3, 6, 2, map) };
            string[] names = { "colonist on the ground", "colonist on floor 2", "colonist on floor 3" };
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], i < at.Length ? at[i] : SF(4 + i, 3));
                Draft(cols[i], true);
                if (i < at.Length)
                {
                    sqProbe[names[i]] = (cols[i].ThingID, i);
                }
            }
            e.ForceSubstructureDirty();
            sqSetBefore = e.ValidSubstructure.Count;
            sqFloorsBefore = SsFloors(e.ValidSubstructure, map);
            Result("fixture: a ship with decks on floors 2 and 3 in a game with 3 floors",
                ProjectZ.FloorsForNewMaps == 3 && Levels.Max(map) == 2 && sqSetBefore == HuShipGround + HuShipDeck + 77 && cols.Count >= at.Length,
                $"game floors {ProjectZ.FloorsForNewMaps}, home top floor {Levels.Max(map)}, set {sqSetBefore} ({sqFloorsBefore}), colonists {cols.Count}");
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            StartTicks(120, sqCase == "t1" ? "sq_picker" : "sq_launch");
        }

        private static List<PlanetTile> SqNear(PlanetTile from)
        {
            List<PlanetTile> near = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(from, near);
            return near.Where(t => Find.WorldObjects.MapParentAt(t) == null && !Find.WorldGrid[t].WaterCovered && Find.WorldGrid[t].hilliness != Hilliness.Impassable).ToList();
        }

        // ------------------------------------------------------------------ T1 the destination on the world map

        // A player settlement with a small map on tile, generated under a plan of 'floors' floors (0: no plan).
        private static Map SqSettlementMap(PlanetTile tile, int floors)
        {
            Settlement s = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
            s.Tile = tile;
            s.SetFaction(Faction.OfPlayer);
            Find.WorldObjects.Add(s);
            Map m = null;
            if (floors > 0)
            {
                FpWith(x => x == s ? new MapFloorPlan { floors = floors } : (MapFloorPlan?)null, () => m = FpGenerate(s));
            }
            else
            {
                m = FpGenerate(s);
            }
            return m;
        }

        // The validator the game registered in the tile picker through the console's own call.
        private static Func<PlanetTile, bool> SqValidator(CompPilotConsole console, bool launching)
        {
            console.StartChoosingDestination_NewTemp(launching);
            TilePicker picker = Find.TilePicker;
            return picker.Active ? Traverse.Create(picker).Field("validator").GetValue<Func<PlanetTile, bool>>() : null;
        }

        // One ask: the answer and the messages it showed.
        private static (bool ok, List<string> said) SqAsk(Func<PlanetTile, bool> validator, PlanetTile tile)
        {
            sqLive().Clear();
            bool ok = validator(tile);
            List<string> said = sqLive().Select(m => m.text).ToList();
            sqLive().Clear();
            return (ok, said);
        }

        private static string SqSaid((bool ok, List<string> said) a) => $"{(a.ok ? "accepted" : "refused")}, messages [{string.Join(" | ", a.said)}]";

        private static void SqPicker(Map map)
        {
            Building_GravEngine e = GlEngine(map);
            CompPilotConsole console = SsConsole(map, e);
            List<PlanetTile> near = SqNear(map.Tile);
            if (e == null || console == null || near.Count < 4)
            {
                Result("T1 an engine, a console and four free neighbouring tiles", false, $"engine {e != null}, console {console != null}, tiles {near.Count}");
                Finish();
                return;
            }
            if (e.cooldownCompleteTick > Find.TickManager.TicksGame)
            {
                e.cooldownCompleteTick = Find.TickManager.TicksGame;
            }
            e.launchInfo = new LaunchInfo { quality = 1f, doNegativeOutcome = false };
            string ours = "PZ_ShipNotEnoughFloors".Translate();
            int before = HullCompat.HeightBefore(e);
            Result("T1 the ship's height asked from the engine before any launch", before == SqHeight, $"{before} (want {SqHeight}; -1: the product has no such function)");

            Func<PlanetTile, bool> launch = SqValidator(console, true);
            if (launch == null)
            {
                Result("T1 the console opens the tile picker", false, "not active");
                Finish();
                return;
            }
            // (first, before any settlement stands next to it) an empty tile: no map yet, never refused by ProjectZ
            (bool ok, List<string> said) empty = SqAsk(launch, near[3]);
            Result("T1 an empty tile (no map yet) is accepted", empty.ok && empty.said.Count == 0, $"tile {near[3]}: {SqSaid(empty)}");

            Map low2 = SqSettlementMap(near[0], 2), low1 = SqSettlementMap(near[1], 1), high = SqSettlementMap(near[2], 0);
            Current.Game.CurrentMap = map;
            Result("T1 fixture: existing maps with 2 floors, with no floors above the ground, and with the game's 3",
                Levels.Max(low2) == 1 && ProjectZ.Annex(low1) == null && Levels.Max(low1) == 0 && Levels.Max(high) == 2,
                $"top floors {Levels.Max(low2)}, {Levels.Max(low1)} (annex {ProjectZ.Annex(low1) != null}), {Levels.Max(high)} on tiles {near[0]}, {near[1]}, {near[2]}");

            (bool ok, List<string> said) a2 = SqAsk(launch, near[0]), a1 = SqAsk(launch, near[1]), a3 = SqAsk(launch, near[2]);
            Result("T1 launch: the tile of an existing map with 2 floors is refused on the world map, with the landing's own message",
                !a2.ok && a2.said.Count == 1 && a2.said[0] == ours, SqSaid(a2));
            Result("T1 launch: the tile of an existing map with no floors above the ground is refused the same way",
                !a1.ok && a1.said.Count == 1 && a1.said[0] == ours, SqSaid(a1));
            Result("T1 launch: the tile of an existing map with enough floors is accepted, no message", a3.ok && a3.said.Count == 0, SqSaid(a3));
            Result("T1 no launch happened", !WorldComponent_GravshipController.CutsceneInProgress && e.Spawned && Current.Game.Gravship == null, $"cutscene {WorldComponent_GravshipController.CutsceneInProgress}, engine spawned {e.Spawned}");
            AccessTools.Method(typeof(TilePicker), "StopTargetingInt").Invoke(Find.TilePicker, null);

            // "View range": the console's other use of the picker hands over the same validator
            Func<PlanetTile, bool> view = SqValidator(console, false);
            if (view == null)
            {
                Result("T1 View range opens the tile picker", false, "not active");
            }
            else
            {
                (bool ok, List<string> said) v2 = SqAsk(view, near[0]), v3 = SqAsk(view, near[2]);
                Result("T1 View range: the same answers (2 floors refused with our message, 3 floors accepted)",
                    !v2.ok && v2.said.Count == 1 && v2.said[0] == ours && v3.ok && v3.said.Count == 0, $"2 floors: {SqSaid(v2)}; 3 floors: {SqSaid(v3)}");
                AccessTools.Method(typeof(TilePicker), "StopTargetingInt").Invoke(Find.TilePicker, null);
            }

            // the game's own reason first: with empty tanks every tile is refused by the game, and ours is not added
            foreach (Thing t in map.listerThings.AllThings.Where(t => t.def.defName == "ChemfuelTank").ToList())
            {
                CompRefuelable f = t.TryGetComp<CompRefuelable>();
                f?.ConsumeFuel(f.Fuel);
            }
            Func<PlanetTile, bool> dry = SqValidator(console, true);
            if (dry == null)
            {
                Result("T1 the console opens the tile picker with empty tanks", false, "not active");
            }
            else
            {
                (bool ok, List<string> said) d2 = SqAsk(dry, near[0]);
                Result("T1 the game's own reason comes first: with empty tanks the 2-floor map's tile shows the game's message only",
                    e.TotalFuel <= 0f && !d2.ok && d2.said.Count == 1 && d2.said[0] != ours, $"fuel {e.TotalFuel:0}: {SqSaid(d2)}");
                AccessTools.Method(typeof(TilePicker), "StopTargetingInt").Invoke(Find.TilePicker, null);
            }

            // another user of the picker (outside the console's call): its validator is handed over as it is
            Func<PlanetTile, bool> mine = t => true;
            Find.TilePicker.StartTargeting_NewTemp(mine, t => { });
            Func<PlanetTile, bool> got = Traverse.Create(Find.TilePicker).Field("validator").GetValue<Func<PlanetTile, bool>>();
            (bool ok, List<string> said) o2 = SqAsk(got, near[0]);
            Result("T1 another user of the tile picker keeps its own validator, unwrapped", ReferenceEquals(got, mine) && o2.ok && o2.said.Count == 0, $"same delegate {ReferenceEquals(got, mine)}; 2-floor tile: {SqSaid(o2)}");
            AccessTools.Method(typeof(TilePicker), "StopTargetingInt").Invoke(Find.TilePicker, null);
            Find.World.renderer.wantedMode = WorldRenderMode.None;
            Wait(5, "sq_end");
        }

        // ------------------------------------------------------------------ T2, T3, T5 the flights

        private static void SqLaunch(Map map)
        {
            Building_GravEngine e = GlEngine(map);
            List<PlanetTile> near = SqNear(map.Tile);
            if (sqCase == "g")
            {
                near = near.OrderByDescending(t => (int)Find.WorldGrid[t].hilliness).ToList();
            }
            PlanetTile dest = near.FirstOrDefault();
            if (e == null || !dest.Valid)
            {
                Result("shipfloors: an engine and a neighbouring tile", false, $"engine {e != null}, tile {dest.Valid}");
                Finish();
                return;
            }
            sqPlanFloors = sqCase == "n1" ? 1 : sqCase == "g" || sqCase == "x2" ? 0 : 2;
            sqPlanTile = dest;
            if (sqCase != "g")
            {
                // (x2: the plan is for the settlement made on the tile during the flight)
                int floors = sqCase == "n1" ? 1 : 2;
                bool keep = sqCase == "k2";
                sqPlanner = p => p != null && p.Tile == sqPlanTile ? new MapFloorPlan { floors = floors, keepUpperContent = keep } : (MapFloorPlan?)null;
                Api.RegisterMapFloorPlanner(sqPlanner);
            }
            sqProbeRan = 0;
            SqProbeStep(sqCase == "k2");
            Info($"case {sqCase}: destination tile {dest} (hilliness {Find.WorldGrid[dest].hilliness}), plan for a map generated on it: {(sqCase == "g" ? "none" : (sqCase == "n1" ? 1 : 2) + " floor(s)")}{(sqCase == "k2" ? ", keepUpperContent" : "")}");
            sqErrorsAt = errors;
            sqRaisesAt = HullCompat.ShipRaises;
            sqLanding = 1;
            sfMidHook = sqCase == "x2" ? (Action)SqMidExisting : sqCase == "n2" ? (Action)SqMidOtherMaps : null;
            sfLandHook = sqCase == "x2" ? (Action<WorldComponent_GravshipController, GravshipLandingMarker>)SqLandExisting : SqLandNew;
            sfHomeTile = map.Tile;
            sfNewTile = dest;
            sfLeg = 1;
            sfSaved = true;
            sfFootprint = HuShipGround;
            sfLandedStage = "sq_landed";
            SfLaunch(map, e, dest);
        }

        private static Gravship SqFlying() => Current.Game.Gravship ?? Find.WorldObjects.AllWorldObjects.OfType<Gravship>().FirstOrDefault();

        private static void SqRecord(Gravship g)
        {
            sqThings.Clear();
            foreach (Thing t in g.Things)
            {
                sqThings.Add((t.ThingID, t.def.defName));
            }
            foreach (Pawn p in g.Pawns)
            {
                sqThings.Add((p.ThingID, "pawn " + p.LabelShort));
            }
        }

        // n2, in flight: maps generated for something else keep their floors.
        private static void SqMidOtherMaps()
        {
            Gravship g = SqFlying();
            Map current = Find.CurrentMap;
            int r0 = HullCompat.ShipRaises;
            Rand.PushState(FixtureSeed("shipfloors_othermaps"));   // (the tiles of the two test maps; the flight's own random state untouched)
            MapParent p = FpParent();
            Map m = null;
            FpWith(x => x == p ? new MapFloorPlan { floors = 2 } : (MapFloorPlan?)null, () => m = FpGenerate(p));
            Result("T5 in flight: a planned map generated for something else (no ship in its generation call) keeps the plan's 2 floors",
                g != null && Levels.Max(m) == 1 && HullCompat.ShipRaises == r0, $"ship in flight {g != null} (height {(g == null ? -1 : ShipLanding.Height(g))}); {FpShow(m)}; raises {HullCompat.ShipRaises - r0}");
            FpRemove(m, p);
            p = FpParent();
            m = FpGenerate(p);
            Result("T5 in flight: an unplanned map generated for something else has exactly the game's floors", FpGameLayout(m) && HullCompat.ShipRaises == r0, $"{FpShow(m)}; raises {HullCompat.ShipRaises - r0}");
            FpRemove(m, p);
            Rand.PopState();
            if (current != null && Find.Maps.Contains(current))
            {
                Current.Game.CurrentMap = current;
            }
        }

        // x2, in flight: the destination becomes an existing map with 2 floors (a second settlement of the player).
        private static void SqMidExisting()
        {
            int r0 = HullCompat.ShipRaises;
            Map current = Find.CurrentMap;
            Settlement s = SettleUtility.AddNewHome(sqPlanTile, Faction.OfPlayer);
            sqLowMap = GetOrGenerateMapUtility.GetOrGenerateMap(sqPlanTile, Find.World.info.initialMapSize, s.def);
            if (current != null && Find.Maps.Contains(current))
            {
                Current.Game.CurrentMap = current;
            }
            sqExisting = true;
            Result("T3 fixture: during the flight a settlement with a 2-floor map appears on the destination tile (generated without the ship: the plan's floors)",
                sqLowMap != null && Levels.Max(sqLowMap) == 1 && HullCompat.ShipRaises == r0, $"{sqLowMap} size {sqLowMap?.Size}, top floor {Levels.Max(sqLowMap)}; raises {HullCompat.ShipRaises - r0}");
        }

        // Every ground cell asked through the move designator, in two rotations: cells accepted, and a description.
        private static int SqScan(Designator_MoveGravship d, GravshipLandingMarker marker, Map map, out string detail)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            int gz = an?.groundZ ?? map.Size.z, accepted = 0;
            string floors = "PZ_ShipNotEnoughFloors".Translate();
            List<string> parts = new List<string>();
            Rot4 was = marker.GravshipRotation;
            foreach (Rot4 rot in new[] { Rot4.North, Rot4.East })
            {
                marker.GravshipRotation = rot;
                int total = 0, ok = 0, ours = 0, other = 0;
                for (int z = 0; z < gz; z++)
                {
                    for (int x = 0; x < map.Size.x; x++)
                    {
                        total++;
                        AcceptanceReport r = d.CanDesignateCell(new IntVec3(x, 0, z));
                        if (r.Accepted)
                        {
                            ok++;
                        }
                        else if (r.Reason == floors)
                        {
                            ours++;
                        }
                        else
                        {
                            other++;
                        }
                    }
                }
                accepted += ok;
                parts.Add($"{rot.ToStringHuman()}: {total} cells asked, accepted {ok}, refused '{floors}' {ours}, refused for another reason {other}");
            }
            marker.GravshipRotation = was;
            detail = string.Join(" | ", parts);
            return accepted;
        }

        // The arrival at a NEW map: its floors, the game's own marker spot, Confirm there.
        private static void SqLandNew(WorldComponent_GravshipController ctl, GravshipLandingMarker marker)
        {
            Map map = marker.Map;
            Gravship g = marker.gravship;
            AnnexMapComponent an = ProjectZ.Annex(map);
            int raises = HullCompat.ShipRaises - sqRaisesAt, h = ShipLanding.Height(g);
            string shown = $"map {map} (tile {map.Tile}) size {map.Size}, floors {(an == null ? "none above the ground" : an.floors.ToString())}, top floor {Levels.Max(map)}, tall mountains {an?.tallMountains}; ship height {h}; raises {raises}";
            IntVec3 asked = Find.World.info.initialMapSize;
            bool planned = sqLanding == 1 && sqPlanFloors > 0;
            if (planned)
            {
                Result($"T2 the new map for the arriving ship has the floors the ship needs (the plan said {sqPlanFloors})",
                    h == SqHeight && Levels.Max(map) == h && an != null && an.floors == h + 1 && raises == 1, shown);
                Result("T2 the rest of the plan stays: the asked ground size, no tall mountains", an != null && map.Size.x == asked.x && an.groundZ == asked.z && !an.tallMountains,
                    $"ground {map.Size.x} x {an?.groundZ} (asked {asked.x} x {asked.z}), tall mountains {an?.tallMountains}");
            }
            else
            {
                Result($"T5 the new map with no plan{(sqLanding == 2 ? " (after Abort landing)" : "")} has exactly the game's floors, nothing raised",
                    an != null && an.floors == ProjectZ.FloorsForNewMaps && raises == 0 && (sqCase != "g" || an.tallMountains), shown + $"; game floors {ProjectZ.FloorsForNewMaps}");
            }
            if (sqCase == "k2")
            {
                TerrainDef kept = sqKeptCell.InBounds(map) ? sqKeptCell.GetTerrain(map) : null, added = sqAddedCell.InBounds(map) ? sqAddedCell.GetTerrain(map) : null;
                Result("T2 keepUpperContent: what generation wrote on the plan's floor 2 stays, what it wrote on the added floor 3 is open air",
                    sqProbeRan == 2 && kept == TerrainDefOf.Concrete && added == PZDefOf.PZ_OpenAir,
                    $"probe wrote {sqProbeRan} cell(s) (-1: no window); floor 2 {sqKeptCell}: {kept?.defName}; floor 3 {sqAddedCell}: {added?.defName}");
            }
            IntVec3 root = marker.Position;
            AcceptanceReport own = ShipLanding.Check(g, root, map, null);
            Designator_MoveGravship d = ctl.MoveDesignator();
            IntVec3 click = SfClick(d, root, g.Bounds.Size, marker.GravshipRotation);
            AcceptanceReport des = click.IsValid ? d.CanDesignateCell(click) : new AcceptanceReport("no click cell");
            Result($"{(planned ? "T2" : "T5")} the ship may land where the game put the marker", own.Accepted && des.Accepted && marker.Spawned,
                $"marker spawned {marker.Spawned} at {root} ({marker.GravshipRotation.ToStringHuman()}): ShipLanding.Check {GlRep(own)}, the move designator {GlRep(des)}");
            SqRecord(g);
            ZCapture($"sq_marker{sqLanding}");
            // Confirm at the game's own spot, unmoved (the GUI's own condition, SfConfirm)
            SfConfirm(ctl, marker);
        }

        // x2: the existing map with fewer floors.
        private static void SqLandExisting(WorldComponent_GravshipController ctl, GravshipLandingMarker marker)
        {
            Map map = ctl.landingMap;
            Designator_MoveGravship d = ctl.MoveDesignator();
            int accepted = SqScan(d, marker, map, out string detail);
            Result("T3 at the existing map with 2 floors no cell takes the ship", map == sqLowMap && Levels.Max(map) == 1 && accepted == 0, $"{map}, top floor {Levels.Max(map)}; {detail}");
            ZCapture("sq_existing_marker");
            bool confirm = marker.Spawned && Find.DesignatorManager.SelectedDesignator != d;
            Result("T3 Confirm is not taken there (the game's own answer: the marker is not placed)", !confirm, $"marker spawned {marker.Spawned}, move designator selected {Find.DesignatorManager.SelectedDesignator == d}");
            // "Abort landing": what the GUI button does (WorldComponentOnGUI), its dialog accepted
            Gravship g = Find.CurrentGravship;
            Action abort = AccessTools.MethodDelegate<Action>(AccessTools.Method(typeof(WorldComponent_GravshipController), "AbortLanding"), ctl);
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("AbortLandConfirmation".Translate(), abort));
            Dialog_MessageBox box = Find.WindowStack.Windows.OfType<Dialog_MessageBox>().LastOrDefault();
            if (box == null)
            {
                Result("T3 the Abort landing dialog", false, "none");
                Finish();
                return;
            }
            Find.WindowStack.TryRemove(box, doCloseSound: false);
            Rand.PushState(7);
            try
            {
                box.buttonAAction?.Invoke();
            }
            finally
            {
                Rand.PopState();
            }
            PlanetTile to = g?.destinationTile ?? PlanetTile.Invalid;
            Result("T3 Abort landing sends the ship on to a nearby tile with no map", ctl.landingMap == null && to.Valid && to != sqPlanTile && Find.WorldObjects.MapParentAt(to) == null,
                $"landingMap {(ctl.landingMap == null ? "null" : ctl.landingMap.ToString())}, destination {to} (was {sqPlanTile}), a map parent there {Find.WorldObjects.MapParentAt(to) != null}");
            Find.DesignatorManager.Deselect();
            sfNewTile = to;
            sqExisting = false;
            sqLanding = 2;
            sfLandQueued = true;
            sfMarkerSeen = -1;
            sfLandHook = SqLandNew;
        }

        private static void SqLanded()
        {
            Map map = SfNew();
            Building_GravEngine e = map == null ? null : GlEngine(map);
            if (map == null || e == null)
            {
                Result("the ship landed", false, $"map {map}, engine {e != null}");
                Finish();
                return;
            }
            string tag = sqCase == "x2" ? "T3" : sqCase == "g" ? "T5" : "T2";
            HashSet<IntVec3> valid = e.ValidSubstructure;
            Result($"{tag} landed whole: the ship's set is what it was before the launch, on the same floors",
                valid.Count == sqSetBefore && SsFloors(valid, map) == sqFloorsBefore && e.AllConnectedSubstructure.Count == sqSetBefore,
                $"set {valid.Count} ({SsFloors(valid, map)}), all connected {e.AllConnectedSubstructure.Count}; before {sqSetBefore} ({sqFloorsBefore}); engine {GlCell(e.Position, map)}, root {sfRoot}");
            int here = 0;
            List<string> gone = new List<string>();
            foreach ((string id, string def) in sqThings)
            {
                Thing t = SfThingById(map, id) ?? map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.ThingID == id);
                if (t != null && t.Spawned)
                {
                    here++;
                }
                else
                {
                    gone.Add(def);
                }
            }
            Result($"{tag} landed whole: every thing and pawn the ship held is on the map", sqThings.Count > 0 && here == sqThings.Count,
                $"{here} of {sqThings.Count}; missing: {(gone.Count == 0 ? "none" : string.Join(", ", gone.GroupBy(x => x).Select(x => x.Key + " x" + x.Count())))}");
            List<string> off = new List<string>(), where = new List<string>();
            foreach (KeyValuePair<string, (string id, int floor)> kv in sqProbe)
            {
                Thing t = SfThingById(map, kv.Value.id) ?? map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.ThingID == kv.Value.id);
                int k = t == null ? -1 : ProjectZ.FloorOf(t.Position, map);
                bool deck = t != null && (kv.Value.floor == 0 ? map.terrainGrid.FoundationAt(t.Position) == TerrainDefOf.Substructure : ShipDeck.At(t.Position, map));
                where.Add($"{kv.Key}: {(t == null ? "not on the map" : $"floor {k + 1}{(deck ? "" : ", NOT on its deck")}{(t is Pawn p && (p.Downed || p.Dead) ? ", down" : "")}")}");
                if (t == null || k != kv.Value.floor || !deck || t is Pawn q && (q.Downed || q.Dead))
                {
                    off.Add(kv.Key);
                }
            }
            Result($"{tag} landed whole: every probe is on its own floor, on the ship", off.Count == 0, string.Join("; ", where));
            Result($"{tag} no error from the launch to the landing (no storey put on the ground)", errors == sqErrorsAt && ShipLocal.Nets == 0, $"errors {errors - sqErrorsAt}, safety net {ShipLocal.Nets}");
            FloorView.Set(0);
            GlShot(1, sfRoot, 26f, "sq_landed_f1");
            GlShot(3, sfRoot, 26f, "sq_landed_f3");
            GlRun("sq_end");
        }
    }
}
