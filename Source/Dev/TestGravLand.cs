using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Test mode gravland (-projectzfloors=2, Odyssey): gravships land only on the ground. A one-storey gravship is built
    // on the ground next to an unroofed floor-2 slab platform, launched for real (pilot console, tile picker, take-off
    // cutscene) back to its own map; at the landing marker the move designator refuses the platform, the ground under it,
    // a void row and a spot straddling the ground's north edge, and accepts a ground spot; the ship then lands there on
    // ticks with a colonist aboard.
    public static partial class TestRunner
    {
        private static CellRect glField, glPlat;
        private static IntVec3 glShipO, glEngineGround, glLandRoot;
        private static int glFrames, glMarkerSeen, glGroundDeck;
        private static string glLastStage = "", glAfter, glAboard;
        private static bool glLandedSeen, glLandQueued;
        private static readonly List<(int, IntVec3, float, string)> glShots = new List<(int, IntVec3, float, string)>();
        private static readonly HashSet<string> glRoofShots = new HashSet<string>();   // shots taken with the roof display on

        private static readonly AccessTools.FieldRef<WorldComponent_GravshipController, bool> glIsTakeoff =
            AccessTools.FieldRefAccess<WorldComponent_GravshipController, bool>("isTakeoff");
        private static readonly AccessTools.FieldRef<WorldComponent_GravshipController, GravshipLandingMarker> glMarker =
            AccessTools.FieldRefAccess<WorldComponent_GravshipController, GravshipLandingMarker>("landingMarker");

        private static void GlShot(int floor, IntVec3 g, float size, string name) => glShots.Add((floor, g, size, name));

        private static void GlRun(string next)
        {
            glAfter = next;
            Wait(2, "gl_shots");
        }

        private static IntVec3 GlP(int dx, int dz) => new IntVec3(glPlat.minX + dx, 0, glPlat.minZ + dz);

        private static Building_GravEngine GlEngine(Map map) => map.listerThings.ThingsOfDef(ThingDefOf.GravEngine).OfType<Building_GravEngine>().FirstOrDefault();

        private static string GlCell(IntVec3 c, Map map) =>
            $"{c} f{ProjectZ.FloorOf(c, map) + 1} top {c.GetTerrain(map)?.defName} roof {map.roofGrid.RoofAt(c)?.defName ?? "-"}";

        private static string GlRep(AcceptanceReport r) => r.Accepted ? "accepted" : "refused: " + r.Reason;

        private static void StepGravLand(Map map)
        {
            switch (stage)
            {
                case "gl_setup":
                    SetupGravLand(map);
                    return;
                case "gl_launch":
                {
                    Building_GravEngine e = GlEngine(map);
                    glGroundDeck = e.ValidSubstructure.Count;
                    Info($"ship built: engine {GlCell(e.Position, map)}, substructure {glGroundDeck}");
                    GlShot(1, glField.CenterCell, 22f, "gl_0_before");
                    GlRun("gl_launch_go");
                    return;
                }
                case "gl_launch_go":
                    GlLaunch(map, GlEngine(map));
                    return;
                case "gl_shots":
                    if (glShots.Count == 0)
                    {
                        Wait(3, glAfter);
                        return;
                    }
                    ShotAt(map, glShots[0].Item1, glShots[0].Item2, glShots[0].Item3, glRoofShots.Contains(glShots[0].Item4));
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(glRoofShots.Contains(glShots[0].Item4) ? 240 : 25, "gl_shot_take");   // (roofs switched on: IsoRim redraws them over many frames)
                    return;
                case "gl_shot_take":
                    ZCapture(glShots[0].Item4);
                    glShots.RemoveAt(0);
                    Wait(3, "gl_shots");
                    return;
                case "gl_fly":
                    GlFly(map);
                    return;
                case "gl_landed":
                    GlLanded(map);
                    return;
                case "gl_end":
                    Finish();
                    return;
            }
        }

        private static void GlLaunch(Map map, Building_GravEngine engine)
        {
            if (engine.cooldownCompleteTick > Find.TickManager.TicksGame)
            {
                engine.cooldownCompleteTick = Find.TickManager.TicksGame;
            }
            CompPilotConsole console = map.listerThings.AllThings.Select(t => t.TryGetComp<CompPilotConsole>()).FirstOrDefault(c => c != null && c.engine == engine);
            if (console == null)
            {
                Result("launch", false, "no console");
                Finish();
                return;
            }
            Info($"launch: engine {GlCell(engine.Position, map)}, substructure {engine.ValidSubstructure.Count}, fuel {engine.TotalFuel:0}");
            engine.launchInfo = new LaunchInfo { quality = 1f, doNegativeOutcome = false };
            console.StartChoosingDestination_NewTemp();
            TilePicker picker = Find.TilePicker;
            Func<PlanetTile, bool> validator = Traverse.Create(picker).Field("validator").GetValue<Func<PlanetTile, bool>>();
            Action<PlanetTile> chosen = Traverse.Create(picker).Field("tileChosen").GetValue<Action<PlanetTile>>();
            if (!picker.Active || validator == null || chosen == null || !validator(map.Tile))
            {
                Result("launch", false, "picker refused / not started");
                Finish();
                return;
            }
            AccessTools.Method(typeof(TilePicker), "StopTargetingInt").Invoke(picker, null);
            chosen(map.Tile);
            foreach (Window w in Find.WindowStack.Windows.ToList())
            {
                if (w is Dialog_MessageBox box)
                {
                    Info("accepting dialog: " + box.text);
                    Find.WindowStack.TryRemove(box, doCloseSound: false);
                    box.buttonAAction?.Invoke();
                }
            }
            glFrames = 0;
            glLandedSeen = false;
            glLandQueued = true;
            glMarkerSeen = -1;
            glLastStage = "";
            stage = "gl_fly";
        }

        private static void GlFly(Map map)
        {
            glFrames++;
            WorldComponent_GravshipController ctl = Find.GravshipController;
            bool cut = WorldComponent_GravshipController.CutsceneInProgress;
            string st = !cut ? (ctl.IsGravshipTravelling ? "travelling" : (ctl.LandingAreaConfirmationInProgress ? "marker" : "idle"))
                : (glIsTakeoff(ctl) ? "takeoff" : "landing");
            if (st != glLastStage)
            {
                Info($"stage {st} at frame {glFrames}, tick {Find.TickManager.TicksGame}");
                glLastStage = st;
            }
            if (cut && !glIsTakeoff(ctl))
            {
                glLandedSeen = true;
            }
            if (glLandQueued && !cut && ctl.LandingAreaConfirmationInProgress && ctl.landingMap != null && Find.CurrentMap == ctl.landingMap && WorldRendererUtility.DrawingMap)
            {
                if (glMarkerSeen < 0)
                {
                    glMarkerSeen = glFrames;
                }
                else if (glFrames - glMarkerSeen >= 30)
                {
                    glLandQueued = false;
                    GlLand(ctl, map);
                    return;
                }
            }
            if (glLandedSeen && !cut)
            {
                Info($"landing ended after {glFrames} frames");
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                Wait(30, "gl_landed");
                return;
            }
            if (glFrames > 20000)
            {
                Result("flight", false, "no landing after 20000 frames, stage " + st);
                Finish();
            }
        }

        // The cell to click for the ship's root to come out at root (the designator offsets the mouse cell by half the
        // ship's size).
        private static IntVec3 GlClick(IntVec3 root, IntVec2 size, Rot4 rot)
        {
            int reach = Math.Max(size.x, size.z) + 2;
            IntVec3 half = new IntVec3(size.x / 2, 0, size.z / 2);
            for (int dx = -reach; dx <= reach; dx++)
            {
                for (int dz = -reach; dz <= reach; dz++)
                {
                    IntVec3 c = root + new IntVec3(dx, 0, dz);
                    if (PrefabUtility.GetRoot(c + half, size, rot) == root)
                    {
                        return c;
                    }
                }
            }
            return IntVec3.Invalid;
        }

        private static void GlLand(WorldComponent_GravshipController ctl, Map map)
        {
            GravshipLandingMarker marker = glMarker(ctl);
            marker.GravshipRotation = Rot4.North;
            Designator_MoveGravship d = ctl.MoveDesignator();
            IntVec2 size = marker.gravship.Bounds.Size;
            AnnexMapComponent an = ProjectZ.Annex(map);
            string ground = "PZ_GravshipGroundOnly".Translate();
            IntVec3 platUp = ProjectZ.CellOnFloor(glPlat.CenterCell, 1, map);
            IntVec3 voidRow = new IntVec3(glPlat.CenterCell.x, 0, an.stride - 1);
            IntVec3 edge = new IntVec3(glEngineGround.x, 0, an.groundZ - 2);
            Info($"marker: {marker.GravshipCells.Count()} cells, bounds {size}; void row {voidRow} f{ProjectZ.FloorOf(voidRow, map) + 1}, ground edge {edge}");
            foreach ((string n, IntVec3 r, bool want, bool ours) in new[]
            {
                ("on the floor-2 platform", platUp, false, true),
                ("on a void row", voidRow, false, true),
                ("across the ground's north edge", edge, false, false),
                ("on the ground under the platform", glPlat.CenterCell, false, false),
                ("on the ground", glEngineGround, true, false),
            })
            {
                FloorView.Set(Math.Max(0, ProjectZ.FloorOf(r, map)));
                AcceptanceReport a = d.CanDesignateCell(GlClick(r, size, Rot4.North));
                bool ok = a.Accepted == want && (!ours || a.Reason == ground);
                Result($"landing {n} {(want ? "accepted" : "refused")}", ok, $"{r}: {GlRep(a)}");
            }
            FloorView.Set(0);
            glLandRoot = glEngineGround;
            IntVec3 click = GlClick(glLandRoot, size, Rot4.North);
            if (!d.CanDesignateCell(click).Accepted)
            {
                Finish();
                return;
            }
            d.DesignateSingleCell(click);
            if (marker.Spawned && Find.DesignatorManager.SelectedDesignator != d)
            {
                marker.BeginLanding(ctl);
                glMarker(ctl) = null;
                Info("confirmed, marker at " + marker.Position);
            }
            else
            {
                Result("marker placed", false, "not placed");
                Finish();
            }
        }

        private static void GlLanded(Map map)
        {
            Building_GravEngine e = GlEngine(map);
            if (e == null)
            {
                Result("landed on the ground", false, "no grav engine on the map");
                Finish();
                return;
            }
            Result("landed on the ground", ProjectZ.FloorOf(e.Position, map) == 0 && e.Position == glEngineGround, GlCell(e.Position, map));
            Result("whole deck on the ground", e.ValidSubstructure.Count == glGroundDeck && e.ValidSubstructure.All(c => ProjectZ.FloorOf(c, map) == 0), $"substructure {e.ValidSubstructure.Count} (built {glGroundDeck})");
            Pawn p = ById(map, glAboard);
            Result("colonist aboard came along", p != null && p.Spawned && e.ValidSubstructure.Contains(p.Position), $"{p?.Position}");
            int slab = glPlat.Cells.Count(g => SlabDefs.Is(ProjectZ.CellOnFloor(g, 1, map).GetTerrain(map)));
            Result("platform untouched", slab == glPlat.Area, $"slab cells {slab} of {glPlat.Area}");
            GlShot(1, glField.CenterCell, 22f, "gl_1_landed");
            GlShot(0, ProjectZ.GroundCellOf(e.Position, map), 16f, "gl_1_landed_view1");
            GlRun("gl_end");
        }

        private static void SetupGravLand(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || an.floors < 2 || !ModsConfig.OdysseyActive)
            {
                Result("gravland setup", false, $"needs 2 floors and Odyssey; floors {an?.floors}");
                Finish();
                return;
            }
            // field: 52 wide on the ground up to near its north edge (the edge and void-row checks need the last ground rows clear)
            CellRect found = CellRect.Empty;
            int bestBad = int.MaxValue;
            for (int z = an.groundZ - 44; z >= 4; z -= 4)
            {
                for (int x = 6; x + 52 < map.Size.x - 6; x += 4)
                {
                    CellRect r = new CellRect(x, z, 52, an.groundZ - 3 - z);
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
                if (bestBad == 0)
                {
                    break;
                }
            }
            if (found.Area == 0)
            {
                Result("gravland field", false, "map too small");
                Finish();
                return;
            }
            glField = found;
            List<Pawn> cols = Colonists(map);
            foreach (IntVec3 c in found.ExpandedBy(3).ClipInsideMap(map))
            {
                if (ProjectZ.IsAnnex(c, map))
                {
                    continue;
                }
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable && !(t is Pawn p && cols.Contains(p)))
                    {
                        t.Destroy();
                    }
                }
                if (map.terrainGrid.FoundationAt(c) != null)
                {
                    map.terrainGrid.RemoveFoundation(c, false);
                }
                TerrainDef td = c.GetTerrain(map);
                // (a laid floor, e.g. an old road, has an under terrain: the ship's substructure can't go on it)
                if (map.terrainGrid.UnderTerrainAt(c) != null || td.IsWater || td.passability == Traversability.Impassable || !td.affordances.Contains(TerrainAffordanceDefOf.Heavy))
                {
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
            }
            DebugSettings.enableRandomMentalStates = false;
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            for (int i = 0; i < 24 && GenLocalDate.HourOfDay(map) != 10; i++)
            {
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + GenDate.TicksPerHour);
            }
            // platform: 17x17 unroofed slab on floor 2 over a columned ground area
            glPlat = new CellRect(found.minX + 32, found.minZ + 5, 17, 17);
            foreach (IntVec3 g in glPlat)
            {
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(g, 1, map), PZDefOf.PZ_UpperFloor);
            }
            foreach (IntVec3 c in glPlat.ExpandedBy(8).ClipInsideMap(map))
            {
                IntVec3 up = ProjectZ.CellOnFloor(c, 1, map);
                if (up.IsValid)
                {
                    map.fogGrid.Unfog(up);
                }
            }
            ThingDef column = DefDatabase<ThingDef>.GetNamed("Column");
            int[] offs = { 0, 6, 12, 16 };
            foreach (int dx in offs)
            {
                foreach (int dz in offs)
                {
                    Place(column, ThingDefOf.BlocksGranite, GlP(dx, dz), map);
                }
            }
            Info($"platform {glPlat} supported {SlabSupport.Supported(glPlat.CenterCell, map)}");
            glShipO = new IntVec3(found.minX + 3, 0, found.minZ + 7);
            BuildShip(map, glShipO, 13, 11);
            glEngineGround = glShipO + new IntVec3(6, 0, 5);
            while (cols.Count < 2)
            {
                Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(p, found.CenterCell, map);
                cols.Add(p);
            }
            foreach (Pawn p in cols)
            {
                p.drafter.Drafted = true;
            }
            glAboard = cols[0].ThingID;
            Teleport(cols[0], glShipO + new IntVec3(2, 0, 8));
            for (int i = 1; i < cols.Count; i++)
            {
                Teleport(cols[i], new IntVec3(found.minX + 26 + i, 0, found.minZ + 1));
            }
            Info($"field {found}, ship SW {glShipO}, engine {glEngineGround}, platform {glPlat}, ground rows {an.groundZ}, stride {an.stride}");
            Wait(30, "gl_launch");
        }

        // A 13x11 deck with hull, door, engine, console, two full chemfuel tanks, two thrusters and a grav anchor.
        private static void BuildShip(Map map, IntVec3 o, int w, int h)
        {
            TerrainDef deck = DefDatabase<TerrainDef>.GetNamed("Substructure");
            int ex = w / 2, ez = h / 2;
            int[] thrusterX = { 3, w - 4 };
            for (int x = 0; x < w; x++)
            {
                for (int z = -2; z < h; z++)
                {
                    if (z >= 0 || Array.IndexOf(thrusterX, x) >= 0)
                    {
                        map.terrainGrid.SetTerrain(o + new IntVec3(x, 0, z), deck);
                    }
                }
            }
            Func<string, IntVec3, Thing> put = (defName, c) => Place(DefDatabase<ThingDef>.GetNamed(defName), ThingDefOf.Steel, c, map, Rot4.North);
            IntVec3 door = o + new IntVec3(ex, 0, h - 1);
            for (int x = 0; x < w; x++)
            {
                for (int z = 0; z < h; z++)
                {
                    IntVec3 c = o + new IntVec3(x, 0, z);
                    bool ring = x == 0 || z == 0 || x == w - 1 || z == h - 1;
                    if (ring && c != door)
                    {
                        put("GravshipHull", c);
                    }
                    if (!ring)
                    {
                        map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                    }
                }
            }
            put("Door", door);
            put("GravEngine", o + new IntVec3(ex, 0, ez));
            put("PilotConsole", o + new IntVec3(ex, 0, ez + 3));
            foreach (int dx in new[] { -3, 3 })
            {
                Thing tank = put("ChemfuelTank", o + new IntVec3(ex + dx, 0, ez));
                CompRefuelable f = tank.TryGetComp<CompRefuelable>();
                f?.Refuel(f.Props.fuelCapacity - f.Fuel);
            }
            foreach (int x in thrusterX)
            {
                put("SmallThruster", o + new IntVec3(x, 0, -2));
            }
            put("GravAnchor", o + new IntVec3(w + 8, 0, ez));
            (o + new IntVec3(ex, 0, ez)).GetFirstThing<Building_GravEngine>(map)?.ForceSubstructureDirty();
        }
    }
}
