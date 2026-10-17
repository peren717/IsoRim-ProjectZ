using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Test mode shipdeck (multi-level gravships P1; -projectzfloors=3, Odyssey): the ship upper deck.
    //   A 13 x 11 gravship on the ground (TestGravLand.BuildShip) with ordinary stairs and a slab walkway beside it (floor
    //   2, and stairs up to a floor-3 walkway), all placed. Then real flows:
    //   D1 refusals of the deck designator: on the ground, over plain ground, an overhang next to the ship, on a slab,
    //      over a slab, over substructure outside the engine's footprint; an ordinary slab over the ship and ordinary
    //      stairs on its substructure (ColumnBlocked);
    //   D2 colonists build 27 decks on floor 2 and 8 on floor 3 (planned at once: a floor-3 deck waits for the one below),
    //      walking up the ordinary stairs; roofs under them are the floors' roofs; colonists walk on the decks;
    //   D3 ship parts on a deck (placement acceptance of thruster, tank, extender; two hull pieces built), a carpet laid
    //      on a deck (open air stays its under layer), ordinary stairs on a deck refused; remove-foundation refused under a
    //      deck (ground and floor 2);
    //   D4 save and reload: decks, carpet and roofs intact (FloorRoof.Normalize fixes nothing, no collapse);
    //   D5 support (the game's roof rules, as for slabs; user decision 2026-10-17): the only wall in range of an open
    //      3 x 3 deck pad deconstructed by a colonist: the pad falls with its roof, as does an ordinary roof held only by
    //      that wall; a second pad with its wall stays, and the ship's decks (held by its hull) stay; then 20000 ticks;
    //   D6 removal top-down: a floor-3 deck, then the floor-2 deck under it with a drafted colonist and steel on it (moved
    //      off), the roof below an ordinary roof again;
    //   D7 launch: since P2 the decks are ship cells (the colonist on one is aboard) and the launch is refused until upper
    //      floors fly (P4); with the ship's decks taken away a real launch: the pad's decks stay (their substructure
    //      stays); the ship lands on the ground elsewhere.
    public static partial class TestRunner
    {
        private static IntVec3 sdO, sdStairsA, sdStairsB, sdPadWall, sdPadBWall, sdControl, sdRm, sdOnDeck;
        private static CellRect sdField, sdPad, sdPadB, sdStrip;
        private static readonly List<IntVec3> sdF2 = new List<IntVec3>();
        private static readonly List<IntVec3> sdF3 = new List<IntVec3>();
        private static readonly List<IntVec3> sdHull = new List<IntVec3>();
        private static readonly List<IntVec3> sdCarpet = new List<IntVec3>();
        private static TerrainDef sdCarpetDef;
        private static int sdOrderBad, sdOnDeckSeen, sdCollapsesAt, sdGround, sdFrames, sdMarkerSeen, sdPanels;
        private static string sdStander, sdLauncher, sdLastStage = "";
        private static bool sdLandedSeen, sdLandQueued;
        private static IntVec3 sdLandRoot;

        private static IntVec3 SD(int dx, int dz) => new IntVec3(sdO.x + dx, 0, sdO.z + dz);

        private static IntVec3 SDF(int dx, int dz, int k, Map map) => ProjectZ.CellOnFloor(SD(dx, dz), k, map);

        private static int Decks(Map map, IEnumerable<IntVec3> cells) => cells.Count(c => ShipDeck.At(c, map));

        // Pad B: its decks and the floor roofs under them all there.
        private static bool PadBOk(Map map) => Decks(map, sdPadB.Cells.Select(g => ProjectZ.CellOnFloor(g, 1, map))) == sdPadB.Area
            && sdPadB.Cells.All(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_FloorRoof);

        private static int DecksOnMap(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int n = 0;
            for (int k = 1; k < a.floors; k++)
            {
                for (int z = 0; z < a.groundZ; z++)
                {
                    for (int x = 0; x < map.Size.x; x++)
                    {
                        if (ShipDeck.At(ProjectZ.CellOnFloor(new IntVec3(x, 0, z), k, map), map))
                        {
                            n++;
                        }
                    }
                }
            }
            return n;
        }

        private static string SdWhy(AcceptanceReport r) => r.Accepted ? "accepted" : "'" + r.Reason + "'";

        private static void SdExpect(string name, Designator d, IntVec3 c, string want)
        {
            AcceptanceReport r = d.CanDesignateCell(c);
            bool ok = want == null ? r.Accepted : !r.Accepted && r.Reason == want;
            Result(name, ok, $"{c} f{ProjectZ.FloorOf(c, Find.CurrentMap) + 1}: {SdWhy(r)}" + (ok || want == null ? "" : $" (want '{want}')"));
        }

        private static void SdPlan(Designator d, IEnumerable<IntVec3> cells, string what)
        {
            List<string> refused = new List<string>();
            int n = 0;
            foreach (IntVec3 c in cells)
            {
                AcceptanceReport r = d.CanDesignateCell(c);
                if (r.Accepted)
                {
                    d.DesignateSingleCell(c);
                    n++;
                }
                else
                {
                    refused.Add(c + " " + SdWhy(r));
                }
            }
            Result(what + ": the designator places it", refused.Count == 0, refused.Count == 0 ? n + " planned" : "refused " + string.Join(", ", refused));
        }

        private static string SdTerrain(IntVec3 c, Map map)
        {
            TerrainGrid tg = map.terrainGrid;
            return $"{c} f{ProjectZ.FloorOf(c, map) + 1} at {tg.TerrainAt(c)?.defName} (top {tg.TopTerrainAt(c)?.defName}, under {tg.UnderTerrainAt(c)?.defName ?? "-"}, foundation {tg.FoundationAt(c)?.defName ?? "-"}) roof below {map.roofGrid.RoofAt(FloorRoof.Below(c, map))?.defName ?? "-"}";
        }

        private static void StepShipDeck(Map map)
        {
            switch (stage)
            {
                case "sd_setup":
                    SetupShipDeck(map);
                    return;

                // ---- D1 refusals, D2 plan and build
                case "sd_plan":
                {
                    FloorView.Set(0);
                    Designator_Build deck = new Designator_Build(PZDefOf.PZ_ShipDeck);
                    string onShip = "PZ_DeckNeedsShipBelow".Translate();
                    SdExpect("D1 deck on the ground refused", deck, SD(6, -6), PlaceWorker_UpperFloor.ViewFloorAbove());
                    SdExpect("D1 deck over plain ground refused", deck, SDF(16, 5, 1, map), onShip);
                    SdExpect("D1 deck overhanging the ship refused", deck, SDF(13, 5, 1, map), onShip);
                    SdExpect("D1 deck on a slab refused", deck, SDF(-1, 3, 1, map), "PZ_AlreadyFloor".Translate());
                    SdExpect("D1 deck over a slab refused", deck, SDF(-1, 3, 2, map), onShip);
                    SdExpect("D1 deck over substructure outside the footprint refused", deck, ProjectZ.CellOnFloor(new IntVec3(sdStrip.minX, 0, sdStrip.minZ), 1, map), "MessageMustBePlacedInRangeOfGravEngineOrExtender".Translate());
                    Designator_Build slab = new Designator_Build(PZDefOf.PZ_UpperFloor);
                    SdExpect("D1 ordinary slab over the ship refused", slab, SDF(5, 2, 1, map), "PZ_OverGravshipUseDeck".Translate());
                    Designator_Build stairs = new Designator_Build(PZDefOf.PZ_Stairs);
                    stairs.SetStuffDef(ThingDefOf.WoodLog);
                    SdExpect("D1 ordinary stairs on the ship's substructure refused", stairs, SD(2, 3), "PZ_OverGravship".Translate());
                    SdPlan(deck, sdF2, "D2 floor-2 decks");
                    SdExpect("D2 floor-3 deck over a planned deck accepted", deck, sdF3[0], null);
                    SdPlan(deck, sdF3, "D2 floor-3 decks");
                    Thing bp3 = sdF3[0].GetThingList(map).FirstOrDefault(t => t is Blueprint);
                    if (bp3 != null)
                    {
                        // (the game's own construction check, as the work giver asks it: the deck below is not there yet)
                        bool can = GenConstruct.CanConstruct(bp3, Colonists(map)[0], false, true);
                        Result("D2 a floor-3 deck waits for the deck below", !can, $"CanConstruct {can}");
                    }
                    GlShot(1, SD(1, 5), 12f, "sd_0_planned_f2");
                    sdOrderBad = 0;
                    sdOnDeckSeen = 0;
                    SetWork(map, true);
                    foreach (Pawn p in Colonists(map))
                    {
                        Draft(p, false);
                        if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                        {
                            p.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2);
                        }
                    }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(60000, () =>
                    {
                        foreach (IntVec3 c in sdF3)
                        {
                            if (ShipDeck.At(c, map) && !ShipDeck.At(FloorRoof.Below(c, map), map))
                            {
                                sdOrderBad++;
                            }
                        }
                        foreach (Pawn p in Colonists(map))
                        {
                            if (ShipDeck.At(p.Position, map))
                            {
                                sdOnDeckSeen++;
                            }
                        }
                        return Decks(map, sdF2) == sdF2.Count && Decks(map, sdF3) == sdF3.Count;
                    }, "sd_built", 120);
                    return;
                }
                case "sd_built":
                {
                    tickWatch = null;
                    int f2 = Decks(map, sdF2), f3 = Decks(map, sdF3);
                    Result("D2 colonists built the floor-2 decks", f2 == sdF2.Count, $"{f2} of {sdF2.Count} after {ticksTotal} ticks");
                    Result("D2 colonists built the floor-3 decks", f3 == sdF3.Count, $"{f3} of {sdF3.Count}");
                    Result("D2 no floor-3 deck before the one below", sdOrderBad == 0, $"{sdOrderBad} sightings");
                    Result("D2 colonists walked on the decks", sdOnDeckSeen > 0, $"{sdOnDeckSeen} sightings of a colonist on a deck");
                    int roofs = sdF2.Concat(sdF3).Count(c => map.roofGrid.RoofAt(FloorRoof.Below(c, map)) == PZDefOf.PZ_FloorRoof);
                    Result("D2 the roof under every deck is the floor's roof", roofs == sdF2.Count + sdF3.Count, $"{roofs} of {sdF2.Count + sdF3.Count}; e.g. {SdTerrain(sdF2[4], map)}");
                    int stand = sdF2.Concat(sdF3).Count(c => c.Standable(map) || c.GetEdifice(map) != null);
                    Result("D2 decks are walkable", stand == sdF2.Count + sdF3.Count, $"{stand} standable");
                    Building_GravEngine e = GlEngine(map);
                    Info($"engine: valid substructure {e.ValidSubstructure.Count}, all connected {e.AllConnectedSubstructure.Count} (ground {sdGround}; decks join the ship in P2); deck def tags {string.Join(",", PZDefOf.PZ_ShipDeck.tags)}, IsSubstructure {PZDefOf.PZ_ShipDeck.IsSubstructure}");
                    // D3 ship parts on a deck: placement, two hull pieces and a carpet built
                    FloorView.Set(1);
                    foreach ((string def, IntVec3 c) in new[] { ("SmallThruster", SDF(1, 2, 1, map)), ("ChemfuelTank", SDF(1, 4, 1, map)), ("GravFieldExtender", SDF(1, 3, 1, map)), ("PilotConsole", SDF(1, 7, 1, map)) })
                    {
                        ThingDef td = DefDatabase<ThingDef>.GetNamedSilentFail(def);
                        if (td == null)
                        {
                            continue;
                        }
                        Designator_Build d = new Designator_Build(td);
                        if (td.MadeFromStuff)
                        {
                            d.SetStuffDef(GenStuff.DefaultStuffFor(td));
                        }
                        Info($"D3 placing a {def} on a floor-2 deck at {c}: {SdWhy(d.CanDesignateCell(c))}");
                    }
                    ThingDef hull = DefDatabase<ThingDef>.GetNamed("GravshipHull");
                    Designator_Build dh = new Designator_Build(hull);
                    if (hull.MadeFromStuff)
                    {
                        dh.SetStuffDef(ThingDefOf.Steel);
                    }
                    SdPlan(dh, sdHull, "D3 hull pieces on floor-2 decks");
                    SdPlan(new Designator_Build(sdCarpetDef), sdCarpet, "D3 a carpet on floor-2 decks");
                    Designator_Build stairs = new Designator_Build(PZDefOf.PZ_Stairs);
                    stairs.SetStuffDef(ThingDefOf.WoodLog);
                    SdExpect("D3 ordinary stairs on a deck refused", stairs, SDF(1, 6, 1, map), "PZ_OverGravship".Translate());
                    Designator_RemoveFoundation rf = AllDesignators().OfType<Designator_RemoveFoundation>().FirstOrDefault();
                    FloorView.Set(0);
                    SdExpect("D3 removing the substructure under a deck refused", rf, SD(1, 5), "PZ_DeckAboveFirst".Translate());
                    FloorView.Set(1);
                    SdExpect("D3 removing a floor-2 deck under a floor-3 deck refused", rf, SDF(1, 6, 1, map), "PZ_DeckAboveFirst".Translate());
                    FloorView.Set(0);
                    GlShot(2, SD(1, 5), 12f, "sd_1_built_f3");
                    GlShot(1, SD(1, 5), 12f, "sd_1_built_f2");
                    GlShot(1, SD(1, 3), 5f, "sd_1_built_f2_close");
                    GlShot(0, SD(1, 5), 12f, "sd_1_built_ground");
                    GlRun("sd_build_parts");
                    return;
                }
                case "sd_build_parts":
                {
                    ThingDef hull = DefDatabase<ThingDef>.GetNamed("GravshipHull");
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(30000, () => sdHull.All(c => c.GetEdifice(map)?.def == hull) && sdCarpet.All(c => map.terrainGrid.TopTerrainAt(c) == sdCarpetDef), "sd_parts", 120);
                    return;
                }
                case "sd_parts":
                {
                    tickWatch = null;
                    ThingDef hull = DefDatabase<ThingDef>.GetNamed("GravshipHull");
                    int built = sdHull.Count(c => c.GetEdifice(map)?.def == hull);
                    Result("D3 hull pieces built on a deck", built == sdHull.Count, $"{built} of {sdHull.Count} after {ticksTotal} ticks");
                    bool carpetOk = sdCarpet.All(c => map.terrainGrid.TopTerrainAt(c) == sdCarpetDef && map.terrainGrid.UnderTerrainAt(c) == PZDefOf.PZ_OpenAir && ShipDeck.At(c, map));
                    Result("D3 a carpet laid on a deck (open air under it, the deck kept)", carpetOk, SdTerrain(sdCarpet[0], map));
                    SetWork(map, false);
                    sdCollapsesAt = PZStats.slabCollapses;
                    GlShot(1, SD(1, 3), 6f, "sd_2_parts_f2");
                    GlRun("sd_save");
                    return;
                }

                // ---- D4 save and reload
                case "sd_save":
                    savedTicks = Find.TickManager.TicksGame;
                    sdDecksSaved = DecksOnMap(map);
                    GameDataSaveLoader.SaveGame("pz_shipdeck");
                    Result("D4 saved pz_shipdeck", File.Exists(GenFilePaths.FilePathForSavedGame("pz_shipdeck")), $"{sdDecksSaved} decks");
                    stage = "sd_loaded";
                    pendingLoad = "pz_shipdeck";
                    return;
                case "sd_loaded":
                    StartTicks(120, "sd_loaded2");
                    return;
                case "sd_loaded2":
                {
                    int n = DecksOnMap(map);
                    int fixedRoofs = FloorRoof.Normalize(map);
                    bool carpetOk = sdCarpet.All(c => map.terrainGrid.TopTerrainAt(c) == sdCarpetDef && map.terrainGrid.UnderTerrainAt(c) == PZDefOf.PZ_OpenAir && ShipDeck.At(c, map));
                    Result("D4 decks intact after reload", n == sdDecksSaved && PZStats.slabCollapses == sdCollapsesAt, $"{n} decks (saved {sdDecksSaved}), collapses since save {PZStats.slabCollapses - sdCollapsesAt}");
                    Result("D4 roofs under the decks intact (nothing for Normalize to fix)", fixedRoofs == 0, $"{fixedRoofs} fixed");
                    Result("D4 the carpet on the deck intact", carpetOk, SdTerrain(sdCarpet[0], map));
                    // D5 support: the wall next to the pad deconstructed by a colonist
                    Building w = sdPadWall.GetEdifice(map);
                    Info($"D5 pad {sdPad}: roofs {sdPad.Cells.Count(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_FloorRoof)} floor roofs; pad B {sdPadB}: {sdPadB.Cells.Count(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_FloorRoof)}; control roof {map.roofGrid.RoofAt(sdControl)?.defName ?? "-"}; wall {w?.def.defName}");
                    if (w == null)
                    {
                        Result("D5 pad wall", false, "missing");
                        Finish();
                        return;
                    }
                    new Designator_Deconstruct().DesignateThing(w);
                    SetWork(map, true);
                    TickUntil(15000, () => sdPadWall.GetEdifice(map) == null, "sd_padwall", 60);
                    return;
                }
                case "sd_padwall":
                    tickWatch = null;
                    Result("D5 the pad wall deconstructed by a colonist", sdPadWall.GetEdifice(map) == null, $"after {ticksTotal} ticks");
                    StartTicks(600, "sd_padwall2");
                    return;
                case "sd_padwall2":
                {
                    // (support since 2026-10-17: the game's roof rules, as for slabs)
                    int held = sdPad.Cells.Count(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_FloorRoof);
                    int decksA = Decks(map, sdPad.Cells.Select(g => ProjectZ.CellOnFloor(g, 1, map)));
                    Result("D5 a roof held only by that wall fell (the game's collapse ran)", !map.roofGrid.Roofed(sdControl), $"control roof {map.roofGrid.RoofAt(sdControl)?.defName ?? "none"}");
                    Result("D5 the open deck pad with no wall in range collapsed with its roof", held == 0 && decksA == 0, $"{held} of {sdPad.Area} floor roofs, {decksA} decks left, collapses {PZStats.slabCollapses - sdCollapsesAt}");
                    Result("D5 the deck pad with a wall in range stays", PadBOk(map), "");
                    SetWork(map, false);
                    StartTicks(20000, "sd_idle");
                    return;
                }
                case "sd_idle":
                {
                    int under = sdF2.Count(c => map.roofGrid.RoofAt(FloorRoof.Below(c, map)) == PZDefOf.PZ_FloorRoof);
                    Result("D5 after 20000 ticks: pad B and the ship's decks (held by its hull) and their roofs intact", PadBOk(map) && under == sdF2.Count && DecksOnMap(map) == sdDecksSaved - sdPad.Area, $"ship floor 2 {under}, decks {DecksOnMap(map)} (saved {sdDecksSaved} less pad A)");
                    // D6 removal from the top down
                    Designator_RemoveFoundation rf = AllDesignators().OfType<Designator_RemoveFoundation>().FirstOrDefault();
                    sdRm = SDF(1, 5, 2, map);
                    sdPanels = Stock(map, ThingDefOf.GravlitePanel);
                    FloorView.Set(2);
                    SdExpect("D6 the floor-3 deck may be removed", rf, sdRm, null);
                    rf.DesignateSingleCell(sdRm);
                    FloorView.Set(0);
                    SetWork(map, true);
                    TickUntil(15000, () => !ShipDeck.At(sdRm, map), "sd_rm1", 60);
                    return;
                }
                case "sd_rm1":
                {
                    tickWatch = null;
                    int panels = Stock(map, ThingDefOf.GravlitePanel);
                    Result("D6 a colonist removed the floor-3 deck", map.terrainGrid.TerrainAt(sdRm) == PZDefOf.PZ_OpenAir, SdTerrain(sdRm, map) + $", gravlite panels {sdPanels} -> {panels}");
                    Designator_RemoveFoundation rf = AllDesignators().OfType<Designator_RemoveFoundation>().FirstOrDefault();
                    sdRm = SDF(1, 5, 1, map);
                    FloorView.Set(1);
                    SdExpect("D6 now the floor-2 deck under it may be removed", rf, sdRm, null);
                    rf.DesignateSingleCell(sdRm);
                    FloorView.Set(0);
                    Pawn stander = Colonists(map).Last();
                    sdStander = stander.ThingID;
                    Teleport(stander, sdRm);
                    Draft(stander, true);
                    Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    steel.stackCount = 10;
                    GenSpawn.Spawn(steel, sdRm, map);
                    steel.SetForbidden(true, false);
                    TickUntil(15000, () => !ShipDeck.At(sdRm, map), "sd_rm2", 60);
                    return;
                }
                case "sd_rm2":
                {
                    tickWatch = null;
                    Pawn stander = ById(map, sdStander);
                    if (ShipDeck.At(sdRm, map))
                    {
                        WorkGiver_ConstructRemoveFoundation wg = (WorkGiver_ConstructRemoveFoundation)DefDatabase<WorkGiverDef>.AllDefs.First(w => w.giverClass == typeof(WorkGiver_ConstructRemoveFoundation)).Worker;
                        Info($"D6 diag: designation {map.designationManager.DesignationAt(sdRm, DesignationDefOf.RemoveFoundation) != null}, blocking {WorkGiver_ConstructRemoveFoundation.AnyBuildingBlockingFoundationRemoval(sdRm, map)}, things {string.Join(",", sdRm.GetThingList(map).Select(x => x.def.defName + "/" + x.def.terrainAffordanceNeeded?.defName))}; " + string.Join("; ", Colonists(map).Select(p => $"{p.LabelShort} at {p.Position} drafted {p.Drafted} job {p.CurJobDef?.defName} hasJob {wg.HasJobOnCell(p, sdRm)} reach {p.CanReach(sdRm, PathEndMode.Touch, Danger.Deadly)}")));
                    }
                    Result("D6 a colonist removed the floor-2 deck", map.terrainGrid.TerrainAt(sdRm) == PZDefOf.PZ_OpenAir, SdTerrain(sdRm, map));
                    Result("D6 the colonist standing on it moved off onto a floor", stander != null && stander.Position != sdRm && stander.Position.Standable(map) && ProjectZ.FloorOf(stander.Position, map) == 1, $"{stander?.Position} f{(stander == null ? 0 : ProjectZ.FloorOf(stander.Position, map) + 1)}");
                    List<Thing> steelNear = map.listerThings.ThingsOfDef(ThingDefOf.Steel).Where(t => t.Spawned && t.Position.InHorDistOf(sdRm, 6f) && ProjectZ.FloorOf(t.Position, map) >= 1).ToList();
                    Result("D6 the steel on it put down on a floor", sdRm.GetThingList(map).All(t => t.def != ThingDefOf.Steel) && steelNear.All(t => map.terrainGrid.TerrainAt(t.Position) != PZDefOf.PZ_OpenAir), string.Join(", ", steelNear.Select(t => t.Position + " x" + t.stackCount)));
                    Result("D6 the roof below an ordinary roof again", map.roofGrid.RoofAt(FloorRoof.Below(sdRm, map)) == RoofDefOf.RoofConstructed, $"{map.roofGrid.RoofAt(FloorRoof.Below(sdRm, map))?.defName ?? "none"}");
                    if (stander != null)
                    {
                        Draft(stander, false);
                    }
                    SetWork(map, false);
                    // D7: a colonist walks onto a deck (up the ordinary stairs) and stays there for the launch
                    Pawn walker = Colonists(map).First();
                    sdLauncher = walker.ThingID;
                    sdOnDeck = SDF(2, 7, 1, map);
                    if (ProjectZ.FloorOf(walker.Position, map) >= 1)
                    {
                        Teleport(walker, SD(-6, -2));
                    }
                    GoTo(walker, sdOnDeck);
                    TickUntil(8000, () => ById(map, sdLauncher)?.Position == sdOnDeck, "sd_launch", 30);
                    return;
                }

                // ---- D7 launch: the decks stay behind and fall
                case "sd_launch":
                {
                    tickWatch = null;
                    Pawn walker = ById(map, sdLauncher);
                    Result("D7 a colonist walked up the ordinary stairs onto a deck", walker?.Position == sdOnDeck, $"{walker?.Position}");
                    // P2: the decks are ship cells now; until upper floors fly (P4) the launch is refused
                    Building_GravEngine e = GlEngine(map);
                    Result("D7 (P2) the decks are part of the ship", e.ValidSubstructure.Count == sdGround + DecksOnMap(map) - sdPadB.Area, $"valid {e.ValidSubstructure.Count}, ground {sdGround}, decks {DecksOnMap(map)} (pad B {sdPadB.Area})");
                    Result("D7 (P2) the colonist on a deck is aboard", walker != null && e.ValidSubstructureAt(walker.Position), "");
                    SsLaunchRefused(map, e, "D7 (P2)");
                    GlShot(1, SD(1, 5), 12f, "sd_3_before_launch_f2");
                    GlRun("sd_launch_go");
                    return;
                }
                case "sd_launch_go":
                {
                    // (fixture: the ship's decks taken away at once, top floor first, so the one-storey ship can fly; the
                    // pad's decks stay on their left-behind substructure)
                    foreach (IntVec3 c in sdHull)
                    {
                        c.GetEdifice(map)?.Destroy();
                    }
                    Pawn walker = ById(map, sdLauncher);
                    if (walker != null)
                    {
                        Teleport(walker, SD(-6, -2));
                    }
                    foreach (IntVec3 c in sdF3.Concat(sdF2))
                    {
                        if (ShipDeck.At(c, map))
                        {
                            map.terrainGrid.RemoveFoundation(c, false);
                        }
                    }
                    Building_GravEngine e = GlEngine(map);
                    Result("D7 without its decks the ship may launch", e.ValidSubstructure.Count == sdGround && (bool)SsConsole(map, e).CanUseNow(), $"valid {e.ValidSubstructure.Count}, console {SdWhy(SsConsole(map, e).CanUseNow())}");
                    sdCollapsesAt = PZStats.slabCollapses;
                    GlLaunch(map, GlEngine(map));
                    if (stage == "gl_fly")
                    {
                        sdFrames = 0;
                        sdMarkerSeen = -1;
                        sdLandQueued = true;
                        sdLandedSeen = false;
                        sdLastStage = "";
                        stage = "sd_fly";
                    }
                    return;
                }
                case "sd_fly":
                    SdFly(map);
                    return;
                case "sd_landed":
                {
                    Building_GravEngine e = GlEngine(map);
                    Result("D7 the ship landed on the ground", e != null && e.Position == sdLandRoot && ProjectZ.FloorOf(e.Position, map) == 0, e == null ? "no engine" : GlCell(e.Position, map));
                    Result("D7 the landed ship has its ground deck", e != null && e.ValidSubstructure.Count == sdGround, $"{e?.ValidSubstructure.Count} (built {sdGround})");
                    Result("D7 pad B's decks still stand", PadBOk(map), "");
                    GlShot(1, sdLandRoot, 14f, "sd_5_landed");
                    GlRun("sd_end");
                    return;
                }
                case "sd_end":
                    Finish();
                    return;
            }
        }

        private static int sdDecksSaved;

        private static void SdFly(Map map)
        {
            sdFrames++;
            WorldComponent_GravshipController ctl = Find.GravshipController;
            bool cut = WorldComponent_GravshipController.CutsceneInProgress;
            string st = !cut ? (ctl.IsGravshipTravelling ? "travelling" : (ctl.LandingAreaConfirmationInProgress ? "marker" : "idle"))
                : (glIsTakeoff(ctl) ? "takeoff" : "landing");
            if (st != sdLastStage)
            {
                Info($"stage {st} at frame {sdFrames}, tick {Find.TickManager.TicksGame}");
                sdLastStage = st;
            }
            if (cut && !glIsTakeoff(ctl))
            {
                sdLandedSeen = true;
            }
            if (sdLandQueued && !cut && ctl.LandingAreaConfirmationInProgress && ctl.landingMap != null && Find.CurrentMap == ctl.landingMap && WorldRendererUtility.DrawingMap)
            {
                if (sdMarkerSeen < 0)
                {
                    sdMarkerSeen = sdFrames;
                }
                else if (sdFrames - sdMarkerSeen >= 30)
                {
                    sdLandQueued = false;
                    SdAfterTakeoff(map);
                    SdLand(ctl, map);
                    return;
                }
            }
            if (sdLandedSeen && !cut)
            {
                Info($"landing ended after {sdFrames} frames");
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                Wait(30, "sd_landed");
                return;
            }
            if (sdFrames > 20000)
            {
                Result("D7 flight", false, "no landing after 20000 frames, stage " + st);
                Finish();
            }
        }

        // The ship is in the air, the marker up on the old map: what stayed behind.
        private static void SdAfterTakeoff(Map map)
        {
            int open = sdF2.Concat(sdF3).Count(c => map.terrainGrid.TerrainAt(c) == PZDefOf.PZ_OpenAir);
            Result("D7 no floor left floating over the ship's old place", open == sdF2.Count + sdF3.Count, $"{open} of {sdF2.Count + sdF3.Count} cells open air, collapses {PZStats.slabCollapses - sdCollapsesAt}");
            Result("D7 pad B's decks (their substructure stays) still stand", PadBOk(map), "");
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        }

        private static void SdLand(WorldComponent_GravshipController ctl, Map map)
        {
            GravshipLandingMarker marker = glMarker(ctl);
            marker.GravshipRotation = Rot4.North;
            Designator_MoveGravship d = ctl.MoveDesignator();
            IntVec2 size = marker.gravship.Bounds.Size;
            FloorView.Set(0);
            sdLandRoot = SD(30, 5);
            IntVec3 click = GlClick(sdLandRoot, size, Rot4.North);
            AcceptanceReport r = d.CanDesignateCell(click);
            if (!r.Accepted)
            {
                Result("D7 landing spot", false, GlRep(r));
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
                Result("D7 marker placed", false, "not placed");
                Finish();
            }
        }

        private static void SetupShipDeck(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || an.floors < 3 || !ModsConfig.OdysseyActive || PZDefOf.PZ_ShipDeck == null)
            {
                Result("shipdeck setup", false, $"needs 3 floors and Odyssey; floors {an?.floors}, deck def {PZDefOf.PZ_ShipDeck?.defName}");
                Finish();
                return;
            }
            const int W = 60, H = 22;
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
                Result("shipdeck field", false, "map too small");
                Finish();
                return;
            }
            sdField = found;
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
                // (a laid floor too: substructure can only go on bare ground)
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
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            for (int i = 0; i < 24 && GenLocalDate.HourOfDay(map) != 10; i++)
            {
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + GenDate.TicksPerHour);
            }
            sdO = new IntVec3(found.minX + 7, 0, found.minZ + 6);
            BuildShip(map, sdO, 13, 11);
            sdGround = GlEngine(map).ValidSubstructure.Count;
            // (named: no naming dialog, which pauses the game)
            GlEngine(map).nameHidden = false;
            // access: stairs A on the ground up to a floor-2 walkway along the ship's west side; stairs B on the walkway up
            // to a floor-3 walkway cell; wood walls on the ground hold the walkway's roofs
            TerrainDef wood = PZDefOf.PZ_UpperFloor;
            foreach (int dz in Enumerable.Range(0, 10))
            {
                map.terrainGrid.SetTerrain(SDF(-1, dz, 1, map), wood);
            }
            foreach (int dz in new[] { 4, 5, 6 })
            {
                map.terrainGrid.SetTerrain(SDF(-2, dz, 1, map), wood);
            }
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SD(-2, 2), map);
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SD(-2, 8), map);
            sdStairsA = SD(-2, -2);
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, sdStairsA, map, Rot4.North);
            sdStairsB = SDF(-2, 5, 1, map);
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, sdStairsB, map, Rot4.North);
            map.terrainGrid.SetTerrain(SDF(-1, 7, 2, map), wood);
            // the decks to build
            sdF2.Clear();
            sdF3.Clear();
            for (int dx = 0; dx <= 2; dx++)
            {
                for (int dz = 1; dz <= 9; dz++)
                {
                    sdF2.Add(SDF(dx, dz, 1, map));
                }
            }
            for (int dx = 0; dx <= 1; dx++)
            {
                for (int dz = 5; dz <= 8; dz++)
                {
                    sdF3.Add(SDF(dx, dz, 2, map));
                }
            }
            // (the floor-3 decks are held by a wood wall on the floor-2 walkway, the roof rules as for slabs: plan them
            // from there outwards)
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SDF(-1, 8, 1, map), map);
            IntVec3 holder3 = SD(-1, 8);
            List<IntVec3> f3 = sdF3.OrderBy(c => (ProjectZ.GroundCellOf(c, map) - holder3).LengthHorizontalSquared).ToList();
            sdF3.Clear();
            sdF3.AddRange(f3);
            sdHull.Clear();
            sdHull.Add(SDF(2, 2, 1, map));
            sdHull.Add(SDF(2, 3, 1, map));
            sdCarpet.Clear();
            sdCarpet.Add(SDF(0, 1, 1, map));
            sdCarpet.Add(SDF(1, 1, 1, map));
            sdCarpetDef = DefDatabase<TerrainDef>.AllDefs.Where(t => t.layerable && t.costList != null && t.costList.Any(c => c.thingDef == ThingDefOf.Cloth) && t.constructionSkillPrerequisite == 0 && t.defName.StartsWith("Carpet") && !t.defName.Contains("_")).OrderBy(t => t.defName).FirstOrDefault();
            // substructure outside the footprint (> 18.9 from the engine), placed
            TerrainDef sub = DefDatabase<TerrainDef>.GetNamed("Substructure");
            sdStrip = new CellRect(sdO.x + 38, sdO.z + 3, 2, 2);
            foreach (IntVec3 g in sdStrip)
            {
                map.terrainGrid.SetTerrain(g, sub);
            }
            // the open pads: 3 x 3 substructure with decks over it (placed), no walls under them. One wood wall south of
            // pad A is its only roof holder (it also holds an ordinary roof cell south of it, the control); pad B's wall
            // stays. Since 2026-10-17 decks follow the game's roof rules like slabs: pad A falls when its wall goes.
            sdPad = new CellRect(sdO.x + 44, sdO.z + 2, 3, 3);
            sdPadB = new CellRect(sdO.x + 50, sdO.z + 2, 3, 3);
            sdPadWall = new IntVec3(sdPad.minX + 1, 0, sdPad.minZ - 1);
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, sdPadWall, map);
            sdPadBWall = new IntVec3(sdPadB.minX + 1, 0, sdPadB.minZ - 1);
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, sdPadBWall, map);
            foreach (IntVec3 g in sdPad.Cells.Concat(sdPadB.Cells))
            {
                map.terrainGrid.SetTerrain(g, sub);
                map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(g, 1, map), PZDefOf.PZ_ShipDeck);
            }
            sdControl = sdPadWall + IntVec3.South;
            map.roofGrid.SetRoof(sdControl, RoofDefOf.RoofConstructed);
            // stocks for the decks, hull pieces and carpet
            int sx = 0;
            foreach ((ThingDef def, int count) in new[] { (ThingDefOf.GravlitePanel, 60), (ThingDefOf.Steel, 450), (ThingDefOf.Cloth, 60) })
            {
                int left = count;
                while (left > 0)
                {
                    Thing t = ThingMaker.MakeThing(def);
                    t.stackCount = Mathf.Min(left, def.stackLimit);
                    left -= t.stackCount;
                    GenSpawn.Spawn(t, SD(-7 + sx % 4, -5 + sx / 4), map);
                    sx++;
                }
            }
            while (cols.Count < 3)
            {
                Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(p, SD(-5, -3), map);
                cols.Add(p);
            }
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], SD(-5 - i, -3));
                Draft(cols[i], false);
            }
            SetWork(map, false);
            Info($"field {found}, ship SW {sdO}, engine {GlEngine(map).Position} (ground substructure {sdGround}), stairs A {sdStairsA} B {sdStairsB}, decks f2 {sdF2.Count} f3 {sdF3.Count}, pad {sdPad}, strip {sdStrip}, carpet {sdCarpetDef?.defName}, ground rows {an.groundZ}, stride {an.stride}; pad: {SdTerrain(ProjectZ.CellOnFloor(sdPad.CenterCell, 1, map), map)}");
            Wait(30, "sd_plan");
        }
    }
}
