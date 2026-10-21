using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Test mode shipstairs (multi-level gravships P5; -projectzfloors=4, Odyssey): the ship stairs.
    //   The shipfly ship (TestGravLand.BuildShip, 13 x 11, grav anchor) with ship decks over all of it on floor 2 and over its
    //   west part on floor 3, room A's walls on floor 2 (they hold the floor-3 decks up), and the ordinary access west of the
    //   ship (stairs A to a floor-2 walkway, stairs B on it to floor 3), as in shipfly.
    //   T0 the look: four ship stairs turned N, S, E, W and ordinary steel stairs on plain ground, 3D and 2D (display only);
    //   T1 placement: refused on plain ground, overhanging the floor-2 deck edge, with the exit over open air (no floor-3
    //      deck there), under a wall on the floor-3 deck (stairwell not open); ordinary stairs on the ship refused; the two
    //      ship stairs accepted;
    //   T2 colonists build them for real (blueprints, materials on the ground outside): ship stairs 1 on the ground up to the
    //      floor-2 deck, ship stairs 2 in room A on floor 2 up to the floor-3 deck. Their stairwell decks are marked for
    //      removal when the blueprints go down, taken up by colonists (the game's remove-foundation job) and the stairs wait
    //      for that; then built. The stairwell cells are open air with the stairwell roof under them, the links usable, the
    //      exit deck can't be removed, the stairs are on the ship (OnValidSubstructure);
    //   T3 the icon: the copy gizmo, the designators and GetIconFor give square proportions for both stairs defs (logged);
    //      shots: the ship stairs in 3D on the ship (ground and floor 2), the copy gizmo, 2D;
    //   T4 the ordinary access is taken down: a colonist walks from outside up both ship stairs to floor 3 and back down
    //      (seen on each ramp); colonists haul steel from the ground deck to a floor-2 stockpile over ship stairs 1;
    //   T5 the 3-storey ship flies to a neighbouring tile and lands turned (shipfly's flight and landing): both ship stairs
    //      on their floor and turned place, links usable, stairwell roofs; a colonist walks up both and back down there.
    // With -projectzshipfresh instead (the landing fix): the same ship with nothing upstairs and no other way up.
    //   F0 no floor, deck or stairs upstairs; F1 refused: a ramp on a substructure strip whose exit is over open ground, plain
    //   ground; ship stairs 1 accepted; F2 colonists build them, the landing deck is laid (floor roof under it, part of the
    //   ship, can't be removed), the link usable; F3 a colonist walks up onto it, decks planned next to it; F4 colonists build
    //   them from up there, then ship stairs 2 on them up to floor 3 (their landing deck laid too); F5 a colonist walks up
    //   both; then T5's flight and landing, plus every deck cell landed; F6 colonists take ship stairs 2 down: their landing
    //   deck goes with them, stairs 1 keep theirs.
    // With -projectzshipstack: ship stairs stacked over the opening of the ones below (TestShipStack.cs).
    public static partial class TestRunner
    {
        private static IntVec3 spO;
        private static string spWalker, spBp1, spBp2;
        private static int spSeen1, spSeen2, spHaulSeen;
        private static readonly HashSet<IntVec3> spFoot1 = new HashSet<IntVec3>(), spFoot2 = new HashSet<IntVec3>();
        private static readonly List<Thing> spDisplay = new List<Thing>();
        private static readonly List<(string id, List<IntVec3> offs, Rot4 rot)> spRec = new List<(string, List<IntVec3>, Rot4)>();
        private static IntVec3 spUpOff, spDownOff, spGoal, spBack;
        private static bool spFresh;
        private static readonly List<IntVec3> spDeckRec = new List<IntVec3>();
        private static readonly List<IntVec3> spF2Decks = new List<IntVec3>();
        private static int spOnF2Seen;

        private static IntVec3 SP(int dx, int dz) => new IntVec3(spO.x + dx, 0, spO.z + dz);

        private static IntVec3 SPF(int dx, int dz, int k, Map map) => ProjectZ.CellOnFloor(SP(dx, dz), k, map);

        private static List<Building_Stairs> SpShipStairs(Map map) => map.listerThings.ThingsOfDef(PZDefOf.PZ_ShipStairs).OfType<Building_Stairs>().OrderBy(s => ProjectZ.FloorOf(s.Position, map)).ToList();

        private static string SpCell(IntVec3 c, Map map) => $"{c} f{ProjectZ.FloorOf(c, map) + 1}";

        private static void StepShipStairs(Map map)
        {
            switch (stage)
            {
                case "sp_setup":
                    SetupShipStairs(map);
                    return;
                case "sp_display2d":
                    SpView3D(false, map);
                    GlShot(0, SP(25, 4), 8f, "sp_0_rotations_2d");
                    glAfter = "sp_display_end";
                    Wait(10, "gl_shots");
                    return;
                case "sp_display_end":
                    SpView3D(true, map);
                    foreach (Thing t in spDisplay)
                    {
                        if (!t.Destroyed)
                        {
                            t.Destroy();
                        }
                    }
                    Wait(10, "sp_refuse");
                    return;
                case "sp_refuse":
                    SpRefuse(map);
                    return;
                case "sp_build":
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    SpBuildWait(map);
                    return;
                case "sp_built":
                    SpBuilt(map);
                    return;
                case "sp_gizmo":
                {
                    Building_Stairs s1 = SpShipStairs(map).FirstOrDefault();
                    ShotAt(map, 0, s1?.Position ?? SP(10, 2), 9f);
                    if (s1 != null)
                    {
                        Find.Selector.Select(s1, playSound: false, forceDesignatorDeselect: false);
                    }
                    Wait(25, "sp_gizmo_take");
                    return;
                }
                case "sp_gizmo_take":
                    ZCapture("sp_2_copy_gizmo");
                    Wait(5, "sp_2d");
                    return;
                case "sp_2d":
                    Find.Selector.ClearSelection();
                    SpView3D(false, map);
                    GlShot(0, SP(9, 4), 9f, "sp_2_ship_2d_ground");
                    GlShot(1, SP(5, 4), 9f, "sp_2_ship_2d_f2");
                    glAfter = "sp_2d_end";
                    Wait(10, "gl_shots");
                    return;
                case "sp_2d_end":
                    SpView3D(true, map);
                    Wait(10, "sp_walk");
                    return;
                case "sp_walk":
                    SpWalk(map);
                    return;
                case "sp_walked_up":
                    SpWalkedUp(map);
                    return;
                case "sp_walked_down":
                    SpWalkedDown(map);
                    return;
                case "sp_hauled":
                    SpHauled(map);
                    return;
                case "sp_launch":
                    SpLaunch(map);
                    return;
                case "sp_f_built1":
                    SpFreshBuilt1(map);
                    return;
                case "sp_f_walk1":
                    SpFreshWalk1(map);
                    return;
                case "sp_f_walked1":
                    SpFreshWalked1(map);
                    return;
                case "sp_f_decks":
                    SpFreshDecks(map);
                    return;
                case "sp_f_built2":
                    SpFreshBuilt2(map);
                    return;
                case "sp_f_walked2":
                    SpFreshWalked2(map);
                    return;
                case "sp_f_decon":
                    SpFreshDecon(SfNew());
                    return;
                case "sp_f_deconned":
                    SpFreshDeconned(SfNew());
                    return;
                case "sp_landed":
                    SpLanded();
                    return;
                case "sp_landed_up":
                    SpLandedWalk(false);
                    return;
                case "sp_landed_down":
                    SpLandedWalk(true);
                    return;
                case "sp_end":
                    Result("no floor-tagged cell reached a map (safety net)", ShipLocal.Nets == 0, $"{ShipLocal.Nets}");
                    Finish();
                    return;
                default:
                    if (stage.StartsWith("sp_k_"))
                    {
                        StepShipStack(map);
                        return;
                    }
                    StepShipRoof(map);
                    return;
            }
        }

        // IsoRim's 3D view on or off (the 2D look of the stairs).
        private static void SpView3D(bool on, Map map)
        {
            if (!IsoRimBridge.Present)
            {
                return;
            }
            IsoRimBridge.SetView3DActive(on);
            map.mapDrawer.RegenerateEverythingNow();
        }

        private static void SetupShipStairs(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || an.floors < 4 || !ModsConfig.OdysseyActive || PZDefOf.PZ_ShipDeck == null || PZDefOf.PZ_ShipStairs == null)
            {
                Result("shipstairs setup", false, $"needs 4 floors and Odyssey; floors {an?.floors}");
                Finish();
                return;
            }
            const int W = 58, H = 28;
            CellRect found = CellRect.Empty;
            int bestBad = int.MaxValue;
            for (int z = an.groundZ - H - 4; z >= 4; z -= 3)
            {
                for (int x = 4; x + W < map.Size.x - 4; x += 3)
                {
                    CellRect r = new CellRect(x, z, W, H);
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
                Result("shipstairs field", false, "map too small");
                Finish();
                return;
            }
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
                if (map.terrainGrid.UnderTerrainAt(c) != null || td.IsWater || td.passability == Traversability.Impassable || !td.affordances.Contains(TerrainAffordanceDefOf.Heavy))
                {
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
                for (int k = 1; k < an.floors; k++)
                {
                    IntVec3 u = ProjectZ.CellOnFloor(c, k, map);
                    if (u.IsValid)
                    {
                        map.fogGrid.Unfog(u);
                    }
                }
            }
            DebugSettings.enableRandomMentalStates = false;
            Find.PlaySettings.autoRebuild = false;
            Find.ResearchManager.gravEngineInspected = true;
            ResearchProjectDef grav = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("BasicGravtech");
            if (grav != null && !grav.IsFinished)
            {
                Find.ResearchManager.FinishProject(grav, doCompletionDialog: false);
            }
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            for (int i = 0; i < 24 && GenLocalDate.HourOfDay(map) != 10; i++)
            {
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + GenDate.TicksPerHour);
            }
            sfHomeTile = map.Tile;
            spO = new IntVec3(found.minX + 8, 0, found.minZ + 6);
            sfO = spO;
            BuildShip(map, spO, 13, 11);
            Building_GravEngine e = GlEngine(map);
            e.nameHidden = false;
            spStack = GenCommandLine.CommandLineArgPassed("projectzshipstack");
            spFresh = GenCommandLine.CommandLineArgPassed("projectzshipfresh") || spStack;
            spDeckRec.Clear();
            if (GenCommandLine.CommandLineArgPassed("projectzshiproof"))
            {
                SrSetup(map, e, cols);
                return;
            }
            if (spFresh)
            {
                SpFreshSetup(map, e, cols);
                return;
            }
            TerrainDef wood = PZDefOf.PZ_UpperFloor;
            ThingDef woodLog = ThingDefOf.WoodLog;
            // the ordinary access (as shipfly): stairs A on the ground up to a floor-2 walkway west of the ship, stairs B on it
            // up to a floor-3 walkway cell next to the floor-3 decks
            foreach (int dz in Enumerable.Range(0, 10))
            {
                map.terrainGrid.SetTerrain(SPF(-1, dz, 1, map), wood);
            }
            foreach (int dz in new[] { 4, 5, 6, 7 })   // (7: under stairs B's solid end, 1x4 stairs)
            {
                map.terrainGrid.SetTerrain(SPF(-2, dz, 1, map), wood);
            }
            Place(ThingDefOf.Wall, woodLog, SP(-2, 2), map);
            Place(ThingDefOf.Wall, woodLog, SP(-2, 8), map);
            Place(PZDefOf.PZ_Stairs, woodLog, SP(-2, -2), map, Rot4.North);
            Place(PZDefOf.PZ_Stairs, woodLog, SPF(-2, 5, 1, map), map, Rot4.North);
            map.terrainGrid.SetTerrain(SPF(-1, 7, 2, map), wood);
            // decks: floor 2 over the whole 13 x 11, floor 3 over its west part (x 0-6)
            for (int x = 0; x < 13; x++)
            {
                for (int z = 0; z < 11; z++)
                {
                    map.terrainGrid.SetTerrain(SPF(x, z, 1, map), PZDefOf.PZ_ShipDeck);
                    if (x <= 6)
                    {
                        map.terrainGrid.SetTerrain(SPF(x, z, 2, map), PZDefOf.PZ_ShipDeck);
                    }
                }
            }
            // floor 2, room A (x 0-6): walls (they hold the floor-3 decks), doors west (to the walkway) and east
            for (int x = 0; x <= 6; x++)
            {
                for (int z = 0; z <= 10; z++)
                {
                    bool ring = x == 0 || x == 6 || z == 0 || z == 10;
                    if (ring && !(x == 0 && z == 5) && !(x == 6 && z == 5))
                    {
                        Place(ThingDefOf.Wall, woodLog, SPF(x, z, 1, map), map);
                    }
                }
            }
            Place(ThingDefOf.Door, woodLog, SPF(0, 5, 1, map), map);
            Place(ThingDefOf.Door, woodLog, SPF(6, 5, 1, map), map);
            e.ForceSubstructureDirty();
            // T0 display: ship stairs turned N, S, E, W and ordinary steel stairs, on plain ground east of the ship
            spDisplay.Clear();
            spDisplay.Add(Place(PZDefOf.PZ_ShipStairs, null, SP(20, 4), map, Rot4.North));
            spDisplay.Add(Place(PZDefOf.PZ_ShipStairs, null, SP(22, 4), map, Rot4.South));
            spDisplay.Add(Place(PZDefOf.PZ_ShipStairs, null, SP(25, 2), map, Rot4.East));
            spDisplay.Add(Place(PZDefOf.PZ_ShipStairs, null, SP(25, 6), map, Rot4.West));
            spDisplay.Add(Place(PZDefOf.PZ_Stairs, ThingDefOf.Steel, SP(28, 4), map, Rot4.North));
            while (cols.Count < 5)
            {
                Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(p, SP(-5, -4), map);
                cols.Add(p);
            }
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], SP(-4 - i, -4));
                Draft(cols[i], true);
            }
            spWalker = cols[0].ThingID;
            Info($"field {found}, ship SW {spO}, engine {GlCell(e.Position, map)}, set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)}), ground rows {an.groundZ}, stride {an.stride}, floors {an.floors}");
            GlShot(0, SP(25, 4), 8f, "sp_0_rotations_3d");
            glAfter = "sp_display2d";
            Wait(30, "gl_shots");
        }

        private static void SpExpect(string label, BuildableDef def, ThingDef stuff, IntVec3 c, Rot4 rot, Map map, string want)
        {
            AcceptanceReport r = GenConstruct.CanPlaceBlueprintAt(def, c, rot, map, stuffDef: stuff);
            bool ok = want == null ? r.Accepted : !r.Accepted && r.Reason == want;
            Result(label, ok, $"{SpCell(c, map)} {rot}: {GlRep(r)}" + (ok || want == null ? "" : $" (want '{want}')"));
        }

        // T1 the refusals, then T2 the two blueprints.
        private static void SpRefuse(Map map)
        {
            ThingDef ss = PZDefOf.PZ_ShipStairs;
            // T0 the texts (P7): both build-menu entries, and every multi-level gravship key in the active language
            foreach (BuildableDef bd in new BuildableDef[] { PZDefOf.PZ_ShipDeck, ss })
            {
                Designator_Build menu = BuildCopyCommandUtility.FindAllowedDesignator(bd);
                Result($"T0 {bd.defName} in the build menu", menu != null && !menu.Label.NullOrEmpty() && !menu.Desc.NullOrEmpty(),
                    $"[{LanguageDatabase.activeLanguage?.folderName}] {menu?.Label} / {menu?.Desc}");
            }
            foreach (string key in new[] { "PZ_DeckNeedsShipBelow", "PZ_DeckAboveFirst", "PZ_DeckBelowFirst", "PZ_OverGravship", "PZ_OverGravshipUseDeck",
                "PZ_OverGravshipUseStairs", "PZ_ShipUpperCantFly", "PZ_ShipNotEnoughFloors", "PZ_ShipFloorAbove", "PZ_ShipStairsOnDeck", "PZ_GravshipGroundOnly" })
            {
                Result($"T0 key {key} translated", key.CanTranslate(), key.Translate());
            }
            string onDeck = "PZ_ShipStairsOnDeck".Translate();
            SpExpect("T1 ship stairs on plain ground refused", ss, null, SP(17, 2), Rot4.North, map, onDeck);
            SpExpect("T1 ship stairs overhanging the floor-2 deck edge refused", ss, null, SPF(12, 10, 1, map), Rot4.North, map, onDeck);
            SpExpect("T1 ship stairs whose exit is over open air outside the ship refused", ss, null, SPF(11, 9, 1, map), Rot4.North, map, onDeck);
            SpExpect("T1 ship stairs whose exit is over the ship's deck with no floor-3 deck yet accepted (the landing is laid with them)", ss, null, SPF(9, 7, 1, map), Rot4.North, map, null);
            Thing wall = Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SPF(2, 3, 2, map), map);
            SpExpect("T1 ship stairs under a wall on the deck above refused (stairwell)", ss, null, SPF(2, 3, 1, map), Rot4.North, map, "PZ_WellNotOpen".Translate());
            wall.Destroy();
            SpExpect("T1 ordinary stairs on the ship refused", PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, SP(2, 2), Rot4.North, map, "PZ_OverGravshipUseStairs".Translate());
            SpExpect("T1 ship stairs 1 (ground deck up to the floor-2 deck) accepted", ss, null, SP(10, 2), Rot4.North, map, null);
            SpExpect("T1 ship stairs 2 (floor-2 deck up to the floor-3 deck) accepted", ss, null, SPF(3, 3, 1, map), Rot4.North, map, null);
            Blueprint b1 = GenConstruct.PlaceBlueprintForBuild(ss, SP(10, 2), map, Rot4.North, Faction.OfPlayer, null) as Blueprint;
            Blueprint b2 = GenConstruct.PlaceBlueprintForBuild(ss, SPF(3, 3, 1, map), map, Rot4.North, Faction.OfPlayer, null) as Blueprint;
            spBp1 = b1?.ThingID;
            spBp2 = b2?.ThingID;
            List<IntVec3> wells = new List<IntVec3> { SPF(10, 1, 1, map), SPF(10, 2, 1, map), SPF(10, 3, 1, map), SPF(3, 2, 2, map), SPF(3, 3, 2, map), SPF(3, 4, 2, map) };
            int marked = wells.Count(c => map.designationManager.DesignationAt(c, DesignationDefOf.RemoveFoundation) != null);
            Result("T2 the decks over both stairwells are marked for removal", b1 != null && b2 != null && marked == 6, $"{marked} of 6 marked; blueprints {b1 != null} {b2 != null}");
            bool can = b2 != null && GenConstruct.CanConstruct(b2, Colonists(map)[0], false, true);
            Result("T2 the stairs wait for their stairwell deck", !can, $"CanConstruct {can}: {StairwellFloor.WaitingLine}");
            // materials on the ground outside the ship
            foreach ((ThingDef def, int n, IntVec3 at) in new[] { (ThingDefOf.Steel, 75, SP(-6, 1)), (ThingDefOf.Steel, 75, SP(-6, 2)), (DefDatabase<ThingDef>.GetNamed("GravlitePanel"), 25, SP(-6, 3)), (ThingDefOf.ComponentIndustrial, 6, SP(-6, 4)) })
            {
                Thing t = ThingMaker.MakeThing(def);
                t.stackCount = n;
                GenSpawn.Spawn(t, at, map);
            }
            SpWork(map, construct: true);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            GlShot(1, SP(6, 4), 12f, "sp_1_planned_f2");
            glAfter = "sp_build";
            Wait(2, "gl_shots");
        }

        private static void SpWork(Map map, bool construct)
        {
            SetWork(map, construct);
            foreach (Pawn p in Colonists(map))
            {
                Draft(p, false);
                if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                {
                    p.workSettings.SetPriority(WorkTypeDefOf.Hauling, construct ? 2 : 1);
                }
            }
        }

        // Needs kept up during the long waits (no breaks, no meals to cook).
        private static void SpKeep(Map map)
        {
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.needs?.food != null) p.needs.food.CurLevel = p.needs.food.MaxLevel;
                if (p.needs?.joy != null) p.needs.joy.CurLevel = 1f;
                if (p.needs?.rest != null) p.needs.rest.CurLevel = 1f;
                if (p.InMentalState) p.MentalState.RecoverFromState();
            }
        }

        private static void SpBuildWait(Map map)
        {
            TickUntil(120000, () =>
            {
                SpKeep(map);
                return SpShipStairs(map).Count >= 2;
            }, "sp_built", 120);
        }

        // T2 built.
        private static void SpBuilt(Map map)
        {
            tickWatch = null;
            List<Building_Stairs> st = SpShipStairs(map);
            Result("T2 colonists built both ship stairs", st.Count == 2, $"{st.Count} after {ticksTotal} ticks; sites left {map.listerThings.AllThings.Count(t => (t is Blueprint || t is Frame) && ShipStairs.Is(t.def.entityDefToBuild))}");
            if (st.Count < 2)
            {
                foreach (Thing t in map.listerThings.AllThings.Where(t => (t is Blueprint || t is Frame) && ShipStairs.Is(t.def.entityDefToBuild)).ToList())
                {
                    Pawn c0 = Colonists(map).FirstOrDefault();
                    JobFailReason.Clear();
                    bool can = c0 != null && GenConstruct.CanConstruct(t, c0, false, true);
                    Info($"T2 site left: {t} at {t.Position} {t.Rotation} f{ProjectZ.FloorOf(t.Position, map)}, waiting {StairwellFloor.Waiting(t)}, can construct {can} ({JobFailReason.Reason}), reach {c0?.CanReach(t, PathEndMode.Touch, Danger.Deadly)}, on its cells: {string.Join("; ", t.OccupiedRect().Cells.Select(x => x + " " + string.Join(",", x.GetThingList(map).Select(y => y.def.defName))))}");
                }
                Finish();
                return;
            }
            Building_Stairs s1 = st[0], s2 = st[1];
            spFoot1.Clear();
            spFoot1.AddRange(s1.OccupiedRect().Cells);
            spFoot2.Clear();
            spFoot2.AddRange(s2.OccupiedRect().Cells);
            StairGrid g = StairGrid.For(map);
            int usable = g == null ? 0 : g.links.Count(l => (l.stairs == s1 || l.stairs == s2) && g.Usable(l));
            Result("T2 both links usable (ground to floor 2, floor 2 to floor 3)", usable == 2 && ProjectZ.FloorOf(s1.Position, map) == 0 && ProjectZ.FloorOf(s2.Position, map) == 1 && ShipDeck.At(s1.TopCell, map) && ShipDeck.At(s2.TopCell, map),
                $"{usable} usable; stairs 1 {SpCell(s1.Position, map)} exit {SpCell(s1.TopCell, map)}, stairs 2 {SpCell(s2.Position, map)} exit {SpCell(s2.TopCell, map)}");
            int open = 0, wellRoof = 0, marks = 0;
            foreach (Building_Stairs s in st)
            {
                foreach (IntVec3 c in s.OccupiedRect())
                {
                    IntVec3 up = StairShape.Above(c, map);
                    if (up.GetTerrain(map) == PZDefOf.PZ_OpenAir && map.terrainGrid.FoundationAt(up) == null)
                    {
                        open++;
                    }
                    if (map.roofGrid.RoofAt(c) == PZDefOf.PZ_StairwellRoof)
                    {
                        wellRoof++;
                    }
                    if (map.designationManager.DesignationAt(up, DesignationDefOf.RemoveFoundation) != null)
                    {
                        marks++;
                    }
                }
            }
            Result("T2 the stairwell decks were taken up (open air, stairwell roof under it, no designation left)", open == 6 && wellRoof == 6 && marks == 0, $"open {open}/6, stairwell roofs {wellRoof}/6, designations {marks}");
            Building_GravEngine e = GlEngine(map);
            Result("T2 the ship stairs are part of the ship", e.OnValidSubstructure(s1) && e.OnValidSubstructure(s2), $"{e.OnValidSubstructure(s1)} {e.OnValidSubstructure(s2)}; set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)})");
            FloorView.Set(1);
            AcceptanceReport rem = new Designator_RemoveFoundation().CanDesignateCell(s1.TopCell);
            Result("T2 the exit deck of ship stairs can't be removed", !rem.Accepted && rem.Reason == "PZ_StairsLandHere".Translate(), GlRep(rem));
            FloorView.Set(0);
            // T3 the icons
            string copyLabel = "CommandBuildCopy".Translate();
            Building_Stairs ordinary = map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).OfType<Building_Stairs>().FirstOrDefault();
            foreach (Building_Stairs s in new[] { s1, ordinary })
            {
                if (s == null)
                {
                    continue;
                }
                Command copy = s.GetGizmos().OfType<Command>().FirstOrDefault(c => c.defaultLabel == copyLabel);
                Designator_Build d = new Designator_Build(s.def);
                Widgets.GetIconFor(s, new Vector2(75f, 75f), s.def.defaultPlacingRot, false, out float _, out float _, out Vector2 prop, out Color _, out Material _);
                Designator_Build menu = BuildCopyCommandUtility.FindAllowedDesignator(s.def);
                Result($"T3 {s.def.defName}: the icon is drawn square (copy gizmo, designators, GetIconFor)",
                    copy != null && copy.iconProportions == Vector2.one && d.iconProportions == Vector2.one && prop == Vector2.one && menu != null && menu.iconProportions == Vector2.one,
                    $"copy gizmo iconProportions {copy?.iconProportions.ToString() ?? "no gizmo"}, new designator {d.iconProportions}, build menu designator {menu?.iconProportions.ToString() ?? "none"}, GetIconFor {prop}, drawSize {s.def.graphicData.drawSize}");
            }
            GlShot(0, SP(9, 3), 9f, "sp_2_ship_3d_ground");
            GlShot(1, SP(6, 4), 10f, "sp_2_ship_3d_f2");
            GlShot(2, SP(4, 5), 9f, "sp_2_ship_3d_f3");
            glAfter = "sp_gizmo";
            Wait(2, "gl_shots");
        }

        // T4: the ordinary access goes; a colonist walks up both ship stairs to floor 3.
        private static void SpWalk(Map map)
        {
            foreach (Building_Stairs s in map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).OfType<Building_Stairs>().ToList())
            {
                s.Destroy();
            }
            Pawn w = SfPawn(spWalker);
            Teleport(w, SP(6, 13));
            spGoal = SPF(4, 8, 2, map);
            spBack = SP(6, 13);
            GoTo(w, spGoal);
            spSeen1 = spSeen2 = 0;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            TickUntil(8000, () => SpWatchWalker(map) && w.Position == spGoal, "sp_walked_up", 1);
        }

        private static bool SpWatchWalker(Map map)
        {
            Pawn w = SfPawn(spWalker);
            if (w != null && w.Map == map)
            {
                if (spFoot1.Contains(w.Position))
                {
                    spSeen1++;
                }
                if (spFoot2.Contains(w.Position))
                {
                    spSeen2++;
                }
            }
            if (Find.TickManager.TicksGame % 250 == 0)
            {
                SpKeep(map);
            }
            return true;
        }

        private static void SpWalkedUp(Map map)
        {
            tickWatch = null;
            Pawn w = SfPawn(spWalker);
            Result("T4 a colonist walks from outside up both ship stairs to floor 3", w?.Position == spGoal && spSeen1 > 0 && spSeen2 > 0, $"at {SpCell(w?.Position ?? IntVec3.Invalid, map)}, ticks on ramp 1 {spSeen1}, ramp 2 {spSeen2}, after {ticksTotal} ticks");
            spSeen1 = spSeen2 = 0;
            GoTo(w, spBack);
            TickUntil(8000, () => SpWatchWalker(map) && w.Position == spBack, "sp_walked_down", 1);
        }

        private static void SpWalkedDown(Map map)
        {
            tickWatch = null;
            Pawn w = SfPawn(spWalker);
            Result("T4 and back down both ship stairs to the ground outside", w?.Position == spBack && spSeen1 > 0 && spSeen2 > 0, $"at {SpCell(w?.Position ?? IntVec3.Invalid, map)}, ticks on ramp 1 {spSeen1}, ramp 2 {spSeen2}, after {ticksTotal} ticks");
            // hauling: steel on the ground deck to a stockpile on the floor-2 deck (east), over ship stairs 1
            Zone_Stockpile zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            for (int x = 8; x <= 11; x++)
            {
                for (int z = 7; z <= 9; z++)
                {
                    zone.AddCell(SPF(x, z, 1, map));
                }
            }
            // (the leftover materials outside go too: only the steel on the ship is to be hauled)
            foreach (Thing t in map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item && ProjectZ.FloorOf(t.Position, map) == 0 && !GlEngine(map).ValidSubstructure.Contains(t.Position)).ToList())
            {
                t.Destroy();
            }
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 30;
            GenSpawn.Spawn(steel, SP(8, 2), map);
            SpWork(map, construct: false);
            spHaulSeen = 0;
            TickUntil(10000, () =>
            {
                foreach (Pawn p in Colonists(map))
                {
                    if (spFoot1.Contains(p.Position) && p.carryTracker.CarriedThing?.def == ThingDefOf.Steel)
                    {
                        spHaulSeen++;
                    }
                }
                if (Find.TickManager.TicksGame % 250 == 0)
                {
                    SpKeep(map);
                }
                return CountOn(map, ThingDefOf.Steel, 1) >= 30;
            }, "sp_hauled", 1);
        }

        private static void SpHauled(Map map)
        {
            tickWatch = null;
            int up = CountOn(map, ThingDefOf.Steel, 1);
            bool inZone = map.listerThings.ThingsOfDef(ThingDefOf.Steel).Where(t => ProjectZ.FloorOf(t.Position, map) == 1).All(t => map.zoneManager.ZoneAt(t.Position) is Zone_Stockpile);
            Result("T4 colonists haul steel up ship stairs 1 to the floor-2 stockpile", up >= 30 && inZone && spHaulSeen > 0, $"{up} steel on floor 2 (in the stockpile {inZone}), carried over the ramp {spHaulSeen} ticks, after {ticksTotal} ticks");
            Wait(10, "sp_launch");
        }

        // T5: aboard (one on each floor), a launch to a neighbouring tile, shipfly's flight and landing.
        private static void SpLaunch(Map map)
        {
            Building_GravEngine e = GlEngine(map);
            List<Pawn> cols = Colonists(map);
            // (fresh: no one on the single floor-3 landing cell, the walker's goal)
            IntVec3[] aboard = spStack ? new[] { SP(1, 9), SPF(5, 2, 1, map) } : spFresh ? new[] { SP(1, 9), SPF(2, 6, 1, map) } : new[] { SP(1, 9), SPF(9, 5, 1, map), SPF(5, 8, 2, map) };
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], i < aboard.Length ? aboard[i] : SP(-8 - i, 4));
                Draft(cols[i], true);
            }
            spWalker = cols[0].ThingID;
            spDownOff = ShipLocal.From(SP(1, 9), e.Position, map);
            spUpOff = ShipLocal.From(spStack ? SPF(1, 2, 2, map) : spFresh ? SPF(1, 8, 2, map) : SPF(4, 8, 2, map), e.Position, map);
            if (spFresh)
            {
                spDeckRec.Clear();
                for (int k = 1; k < ProjectZ.Annex(map).floors; k++)
                {
                    for (int x = -1; x <= 13; x++)
                    {
                        for (int z = -1; z <= 11; z++)
                        {
                            IntVec3 c = SPF(x, z, k, map);
                            if (ShipDeck.At(c, map))
                            {
                                spDeckRec.Add(ShipLocal.From(c, e.Position, map));
                            }
                        }
                    }
                }
                Info($"fresh: {spDeckRec.Count} deck cells to fly; set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)})");
            }
            spRec.Clear();
            foreach (Building_Stairs s in SpShipStairs(map))
            {
                spRec.Add((s.ThingID, s.OccupiedRect().Cells.Select(c => ShipLocal.From(c, e.Position, map)).ToList(), s.Rotation));
            }
            List<PlanetTile> near = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(map.Tile, near);
            PlanetTile dest = near.FirstOrDefault(t => Find.WorldObjects.MapParentAt(t) == null && !Find.WorldGrid[t].WaterCovered && Find.WorldGrid[t].hilliness != Hilliness.Impassable);
            if (!dest.Valid)
            {
                Result("T5 a neighbouring tile to fly to", false, "none");
                Finish();
                return;
            }
            sfNewTile = dest;
            sfLeg = 1;
            sfSaved = true;   // (no mid-flight save here: shipfly covers it)
            sfLandedStage = "sp_landed";
            SfLaunch(map, e, dest);
        }

        private static void SpLanded()
        {
            Map map = SfNew();
            Building_GravEngine e = map == null ? null : GlEngine(map);
            if (e == null)
            {
                Result("T5 landed", false, "no engine on " + map);
                Finish();
                return;
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            List<Building_Stairs> st = SpShipStairs(map);
            int bad = 0;
            string first = null;
            foreach ((string id, List<IntVec3> offs, Rot4 rot) in spRec)
            {
                Building_Stairs s = st.FirstOrDefault(x => x.ThingID == id);
                HashSet<IntVec3> want = new HashSet<IntVec3>(offs.Select(o => SfAt(o, sfRoot, sfRot, map)));
                Rot4 wantRot = new Rot4((rot.AsInt + sfRot.AsInt) % 4);
                if (s == null || !want.SetEquals(s.OccupiedRect().Cells) || s.Rotation != wantRot)
                {
                    bad++;
                    first = first ?? $"{id}: {(s == null ? "missing" : s.OccupiedRect() + " " + s.Rotation)}, want {string.Join(" ", want)} {wantRot}";
                }
            }
            Result($"T5 landed turned {sfRot}: both ship stairs on their floor and turned place", st.Count == 2 && bad == 0, $"{st.Count} stairs, {bad} misplaced; {first}");
            StairGrid g = StairGrid.For(map);
            int usable = g == null ? 0 : g.links.Count(l => st.Contains(l.stairs) && g.Usable(l));
            int wellRoof = st.Sum(s => s.OccupiedRect().Cells.Count(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_StairwellRoof));
            int open = st.Sum(s => s.OccupiedRect().Cells.Count(c => StairShape.Above(c, map).GetTerrain(map) == PZDefOf.PZ_OpenAir));
            Result("T5 their links usable, stairwells open with the stairwell roof", usable == 2 && wellRoof == (spFresh ? 3 : 6) && open == 6 && st.All(s => ShipDeck.At(s.TopCell, map)),
                $"{usable} usable links, stairwell roofs {wellRoof}/6, open {open}/6, exits {string.Join(", ", st.Select(s => SpCell(s.TopCell, map) + " " + s.TopCell.GetTerrain(map)?.defName))}");
            if (spFresh)
            {
                int decks = spDeckRec.Count(o => ShipDeck.At(SfAt(o, sfRoot, sfRot, map), map));
                Result("F5 every deck flew and landed with the ship (both stairs landings and the decks built from them)", decks == spDeckRec.Count && spDeckRec.Count > 0,
                    $"{decks} of {spDeckRec.Count} deck cells on the new map; set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)})");
            }
            if (spStack)
            {
                SkLandedChecks(map, e);
            }
            spFoot1.Clear();
            spFoot2.Clear();
            if (st.Count == 2)
            {
                spFoot1.AddRange(st[0].OccupiedRect().Cells);
                spFoot2.AddRange(st[1].OccupiedRect().Cells);
            }
            Pawn w = SfPawn(spWalker);
            spGoal = SfAt(spUpOff, sfRoot, sfRot, map);
            spBack = SfAt(spDownOff, sfRoot, sfRot, map);
            Info($"walker {w?.LabelShort} at {SpCell(w?.Position ?? IntVec3.Invalid, map)}, goal {SpCell(spGoal, map)}");
            if (w == null || w.Map != map)
            {
                Result("T5 the walker landed", false, $"{w?.Map}");
                Finish();
                return;
            }
            if (w.Position != spBack)
            {
                Teleport(w, spBack);
            }
            GoTo(w, spGoal);
            spSeen1 = spSeen2 = 0;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            TickUntil(8000, () => SpWatchWalker(map) && w.Position == spGoal, "sp_landed_up", 1);
        }

        private static void SpLandedWalk(bool down)
        {
            Map map = SfNew();
            tickWatch = null;
            Pawn w = SfPawn(spWalker);
            IntVec3 want = down ? spBack : spGoal;
            Result(down ? "T5 and back down to the ground deck" : "T5 after the landing a colonist walks up both ship stairs to floor 3",
                w?.Position == want && spSeen1 > 0 && spSeen2 > 0, $"at {SpCell(w?.Position ?? IntVec3.Invalid, map)}, ticks on ramp 1 {spSeen1}, ramp 2 {spSeen2}, after {ticksTotal} ticks");
            if (!down)
            {
                spSeen1 = spSeen2 = 0;
                GoTo(w, spBack);
                TickUntil(8000, () => SpWatchWalker(map) && w.Position == spBack, "sp_landed_down", 1);
                return;
            }
            Building_GravEngine e = GlEngine(map);
            IntVec3 eg = ProjectZ.GroundCellOf(e.Position, map);
            GlShot(0, eg, 12f, "sp_3_landed_ground");
            GlShot(1, eg, 12f, "sp_3_landed_f2");
            glAfter = spStack ? "sp_k_rm" : spFresh ? "sp_f_decon" : "sp_end";
            Wait(2, "gl_shots");
        }

        // ---- -projectzshipfresh: the first ship stairs on a fresh one-storey ship (no deck upstairs, no other way up)

        private static void SpFreshSetup(Map map, Building_GravEngine e, List<Pawn> cols)
        {
            while (cols.Count < 5)
            {
                Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(p, SP(-5, -4), map);
                cols.Add(p);
            }
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], SP(-4 - i, -4));
                Draft(cols[i], true);
            }
            spWalker = cols[0].ThingID;
            AnnexMapComponent an = ProjectZ.Annex(map);
            int upper = 0;
            for (int k = 1; k < an.floors; k++)
            {
                foreach (IntVec3 g in new CellRect(spO.x - 12, spO.z - 6, 40, 24).ClipInsideMap(map))
                {
                    IntVec3 u = ProjectZ.CellOnFloor(g, k, map);
                    if (u.IsValid && ProjectZ.HasFloor(u, map))
                    {
                        upper++;
                    }
                }
            }
            int stairs = map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).Count + map.listerThings.ThingsOfDef(PZDefOf.PZ_ShipStairs).Count;
            Result("F0 a fresh one-storey ship: no floor or deck upstairs, no stairs", upper == 0 && stairs == 0, $"upper floor cells {upper}, stairs {stairs}; set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)})");
            ThingDef ss = PZDefOf.PZ_ShipStairs;
            string onDeck = "PZ_ShipStairsOnDeck".Translate();
            // a substructure strip east of the ship: a ramp on it whose exit is over open ground
            TerrainDef sub = DefDatabase<TerrainDef>.GetNamed("Substructure");
            IntVec3[] strip = { SP(13, 5), SP(14, 5), SP(15, 5) };
            foreach (IntVec3 c in strip)
            {
                map.terrainGrid.SetTerrain(c, sub);
            }
            SpExpect("F1 ship stairs whose exit is over open ground outside the ship refused", ss, null, SP(14, 5), Rot4.East, map, onDeck);
            foreach (IntVec3 c in strip)
            {
                map.terrainGrid.RemoveFoundation(c, false);
            }
            SpExpect("F1 ship stairs on plain ground refused", ss, null, SP(17, 2), Rot4.North, map, onDeck);
            SpExpect("F1 ship stairs 1 on the fresh ship (exit over the ship, nothing upstairs) accepted", ss, null, SP(1, 2), Rot4.North, map, null);
            Blueprint b1 = GenConstruct.PlaceBlueprintForBuild(ss, SP(1, 2), map, Rot4.North, Faction.OfPlayer, null) as Blueprint;
            spBp1 = b1?.ThingID;
            foreach ((ThingDef def, int n, IntVec3 at) in new[] { (ThingDefOf.Steel, 75, SP(-6, 1)), (ThingDefOf.Steel, 75, SP(-6, 2)), (ThingDefOf.Steel, 75, SP(-6, 3)),
                (DefDatabase<ThingDef>.GetNamed("GravlitePanel"), 25, SP(-6, 4)), (DefDatabase<ThingDef>.GetNamed("GravlitePanel"), 25, SP(-6, 5)), (ThingDefOf.ComponentIndustrial, 6, SP(-6, 6)) })
            {
                Thing t = ThingMaker.MakeThing(def);
                t.stackCount = n;
                GenSpawn.Spawn(t, at, map);
            }
            e.ForceSubstructureDirty();
            Info($"fresh ship SW {spO}, engine {GlCell(e.Position, map)}, blueprint {b1 != null}");
            SpWork(map, construct: true);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            TickUntil(60000, () =>
            {
                SpKeep(map);
                return SpShipStairs(map).Count >= 1;
            }, "sp_f_built1", 120);
        }

        private static bool SpLandingOk(Building_Stairs s, Map map, out string detail)
        {
            IntVec3 top = s.TopCell;
            IntVec3 below = FloorRoof.Below(top, map);
            detail = $"exit {SpCell(top, map)}: foundation {map.terrainGrid.FoundationAt(top)?.defName ?? "none"}, standable {top.Standable(map)}, roof below {map.roofGrid.RoofAt(below)?.defName ?? "none"}";
            return ShipDeck.At(top, map) && top.Standable(map) && map.roofGrid.RoofAt(below) == PZDefOf.PZ_FloorRoof;
        }

        private static void SpFreshBuilt1(Map map)
        {
            tickWatch = null;
            List<Building_Stairs> st = SpShipStairs(map);
            Result("F2 colonists built ship stairs 1 on the fresh ship", st.Count == 1, $"{st.Count} after {ticksTotal} ticks");
            if (st.Count < 1)
            {
                Finish();
                return;
            }
            Building_Stairs s1 = st[0];
            IntVec3 top = s1.TopCell;
            Result("F2 the stairs laid their landing: a ship upper deck at the exit, the floor's roof under it", SpLandingOk(s1, map, out string d), d);
            Building_GravEngine e = GlEngine(map);
            Result("F2 the landing deck is part of the ship", e.ValidSubstructure.Contains(top), $"set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)})");
            FloorView.Set(1);
            AcceptanceReport rem = new Designator_RemoveFoundation().CanDesignateCell(top);
            Result("F2 the landing deck can't be removed while the stairs stand", !rem.Accepted && rem.Reason == "PZ_StairsLandHere".Translate(), GlRep(rem));
            FloorView.Set(0);
            StairGrid g = StairGrid.For(map);
            Result("F2 the link is usable", g != null && g.links.Any(l => l.stairs == s1 && g.Usable(l)), $"{g?.Count ?? 0} links");
            spFoot1.Clear();
            spFoot1.AddRange(s1.OccupiedRect().Cells);
            spFoot2.Clear();
            Pawn w = SfPawn(spWalker);
            Teleport(w, SP(6, 13));
            spGoal = top;
            GlShot(0, SP(3, 4), 9f, "sp_f1_stairs1_ground");
            GlShot(1, SP(3, 4), 9f, "sp_f1_stairs1_f2");
            glAfter = "sp_f_walk1";
            Wait(2, "gl_shots");
        }

        private static void SpFreshWalk1(Map map)
        {
            Pawn w = SfPawn(spWalker);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            GoTo(w, spGoal);
            spSeen1 = spSeen2 = 0;
            TickUntil(8000, () => SpWatchWalker(map) && w.Position == spGoal, "sp_f_walked1", 1);
        }

        private static void SpFreshWalked1(Map map)
        {
            tickWatch = null;
            Pawn w = SfPawn(spWalker);
            Result("F3 a colonist walks from outside up ship stairs 1 onto the landing deck on floor 2", w?.Position == spGoal && spSeen1 > 0, $"at {SpCell(w?.Position ?? IntVec3.Invalid, map)}, ticks on the ramp {spSeen1}, after {ticksTotal} ticks");
            if (spStack)
            {
                SkPlan(map);
                return;
            }
            // decks on floor 2 from the landing out: a row north (ship stairs 2 go on it) and one beside it
            spF2Decks.Clear();
            for (int z = 5; z <= 8; z++)
            {
                spF2Decks.Add(SPF(1, z, 1, map));
            }
            for (int z = 4; z <= 7; z++)
            {
                spF2Decks.Add(SPF(2, z, 1, map));
            }
            int ok = 0;
            foreach (IntVec3 c in spF2Decks)
            {
                AcceptanceReport r = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_ShipDeck, c, Rot4.North, map);
                if (r.Accepted && GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_ShipDeck, c, map, Rot4.North, Faction.OfPlayer, null) != null)
                {
                    ok++;
                }
                else
                {
                    Info($"deck at {SpCell(c, map)}: {GlRep(r)}");
                }
            }
            Result("F3 floor-2 decks planned next to the landing", ok == spF2Decks.Count, $"{ok} of {spF2Decks.Count}");
            SpWork(map, construct: true);
            spOnF2Seen = 0;
            TickUntil(60000, () =>
            {
                if (Find.TickManager.TicksGame % 240 == 0)
                {
                    SpKeep(map);
                }
                spOnF2Seen += Colonists(map).Count(p => ProjectZ.FloorOf(p.Position, map) == 1 && p.CurJobDef == JobDefOf.FinishFrame);
                return spF2Decks.All(c => ShipDeck.At(c, map));
            }, "sp_f_decks", 60);
        }

        private static void SpFreshDecks(Map map)
        {
            tickWatch = null;
            int built = spF2Decks.Count(c => ShipDeck.At(c, map));
            Result("F4 colonists built the floor-2 decks, working up there (reached over ship stairs 1)", built == spF2Decks.Count && spOnF2Seen > 0, $"{built} of {spF2Decks.Count} after {ticksTotal} ticks, builder seen on floor 2 {spOnF2Seen}x");
            SpExpect("F4 ship stairs 2 on the floor-2 decks (exit over them, nothing on floor 3) accepted", PZDefOf.PZ_ShipStairs, null, SPF(1, 6, 1, map), Rot4.North, map, null);
            Blueprint b2 = GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_ShipStairs, SPF(1, 6, 1, map), map, Rot4.North, Faction.OfPlayer, null) as Blueprint;
            spBp2 = b2?.ThingID;
            TickUntil(60000, () =>
            {
                SpKeep(map);
                return SpShipStairs(map).Count >= 2;
            }, "sp_f_built2", 120);
        }

        private static void SpFreshBuilt2(Map map)
        {
            tickWatch = null;
            List<Building_Stairs> st = SpShipStairs(map);
            Result("F4 colonists built ship stairs 2 on floor 2", st.Count == 2 && ProjectZ.FloorOf(st[st.Count - 1].Position, map) == 1, $"{st.Count} after {ticksTotal} ticks");
            if (st.Count < 2)
            {
                Finish();
                return;
            }
            Building_Stairs s1 = st[0], s2 = st[1];
            Result("F4 ship stairs 2 laid their landing deck on floor 3", SpLandingOk(s2, map, out string d) && ProjectZ.FloorOf(s2.TopCell, map) == 2, d);
            StairGrid g = StairGrid.For(map);
            int usable = g == null ? 0 : g.links.Count(l => (l.stairs == s1 || l.stairs == s2) && g.Usable(l));
            int open = st.Sum(s => StairShape.Ramp(s.Position, s.Rotation).Count(c => StairShape.Above(c, map).GetTerrain(map) == PZDefOf.PZ_OpenAir && map.terrainGrid.FoundationAt(StairShape.Above(c, map)) == null));
            int wellRoof = st.Sum(s => StairShape.Ramp(s.Position, s.Rotation).Count(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_StairwellRoof));
            // (stairs 1 stand under the ground room's roof: their stairwell roof; stairs 2 on floor 2 have open sky over them, no roof)
            Result("F4 both links usable, both stairwells open, the stairwell roof over stairs 1 (stairs 2 stand outdoors)", usable == 2 && open == 6 && wellRoof == 3 && StairShape.Ramp(s2.Position, s2.Rotation).All(c => map.roofGrid.RoofAt(c) == null), $"{usable} usable, open {open}/6, stairwell roofs {wellRoof}/3");
            Building_GravEngine e = GlEngine(map);
            Result("F4 the stairs and decks are part of the ship", e.OnValidSubstructure(s1) && e.OnValidSubstructure(s2) && e.ValidSubstructure.Contains(s2.TopCell) && spF2Decks.All(c => e.ValidSubstructure.Contains(c)),
                $"set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)})");
            spFoot1.Clear();
            spFoot1.AddRange(s1.OccupiedRect().Cells);
            spFoot2.Clear();
            spFoot2.AddRange(s2.OccupiedRect().Cells);
            Pawn w = SfPawn(spWalker);
            Teleport(w, SP(6, 13));
            spGoal = s2.TopCell;
            GoTo(w, spGoal);
            spSeen1 = spSeen2 = 0;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            TickUntil(8000, () => SpWatchWalker(map) && w.Position == spGoal, "sp_f_walked2", 1);
        }

        private static void SpFreshWalked2(Map map)
        {
            tickWatch = null;
            Pawn w = SfPawn(spWalker);
            Result("F5 a colonist walks from outside up both ship stairs to the floor-3 landing", w?.Position == spGoal && spSeen1 > 0 && spSeen2 > 0, $"at {SpCell(w?.Position ?? IntVec3.Invalid, map)}, ticks on ramp 1 {spSeen1}, ramp 2 {spSeen2}, after {ticksTotal} ticks");
            GlShot(0, SP(3, 4), 9f, "sp_f2_ship_ground");
            GlShot(1, SP(3, 5), 9f, "sp_f2_ship_f2");
            GlShot(2, SP(3, 6), 9f, "sp_f2_ship_f3");
            glAfter = "sp_launch";
            Wait(2, "gl_shots");
        }

        private static IntVec3 spTop1, spTop2;

        // F6 (landed): colonists take ship stairs 2 down; their landing deck goes with them (nothing on it, no deck above),
        // stairs 1 and their landing (decks next to it) stay.
        private static void SpFreshDecon(Map map)
        {
            List<Building_Stairs> st = SpShipStairs(map);
            if (st.Count < 2)
            {
                Result("F6 both ship stairs on the landed map", false, $"{st.Count}");
                Finish();
                return;
            }
            spTop1 = st[0].TopCell;
            spTop2 = st[1].TopCell;
            Result("F6 before: both landing decks there", ShipDeck.At(spTop1, map) && ShipDeck.At(spTop2, map), $"{SpCell(spTop1, map)} {SpCell(spTop2, map)}");
            map.designationManager.AddDesignation(new Designation(st[1], DesignationDefOf.Deconstruct));
            SpWork(map, construct: true);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            TickUntil(30000, () =>
            {
                SpKeep(map);
                return SpShipStairs(map).Count < 2;
            }, "sp_f_deconned", 120);
        }

        private static void SpFreshDeconned(Map map)
        {
            tickWatch = null;
            List<Building_Stairs> st = SpShipStairs(map);
            Result("F6 colonists took ship stairs 2 down", st.Count == 1, $"{st.Count} left after {ticksTotal} ticks");
            Result("F6 their landing deck went with them (open air), stairs 1 kept theirs",
                !ShipDeck.At(spTop2, map) && spTop2.GetTerrain(map) == PZDefOf.PZ_OpenAir && ShipDeck.At(spTop1, map) && st.Count == 1 && st[0].TopCell == spTop1,
                $"exit 2 {SpCell(spTop2, map)}: {spTop2.GetTerrain(map)?.defName}, foundation {map.terrainGrid.FoundationAt(spTop2)?.defName ?? "none"}; exit 1 deck {ShipDeck.At(spTop1, map)}");
            Wait(10, "sp_end");
        }
    }
}
