using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Test mode shipset (multi-level gravships P2; -projectzfloors=3, Odyssey): the 3D ship set.
    //   A 17 x 13 gravship on the ground (TestGravLand.BuildShip) with a substructure strip running east from its north
    //   side out to 18 cells from the engine; decks (placed) over the whole ship and the strip on floor 2 and over the
    //   ship's west part and the strip on floor 3; wood walls hold their roofs (the game's roof rules); ordinary stairs and
    //   a slab walkway west of the ship lead up (as in shipdeck). Then:
    //   S1 the set: all connected = ground + decks (and the engine's count shows it); the limit 500 cuts the top floor
    //      first (ground and floor 2 complete); the footprint is a sphere (the strip's end column: ground and floor 2 in
    //      range, floor 3 not); the ground-projection sites (capture bounds, engineToCenter, indoor masks) are patched;
    //   S2 aboard: a colonist on a floor-2 deck counts as aboard, one on a floor-3 deck past the limit as left behind
    //      (the launch dialog's lists); the launch is refused with decks until P4 (the console says why, the take-off
    //      never starts);
    //   S3 colonists build a grav field extender on a floor-2 deck: it links (3D distance), the limit is 750, every deck
    //      is in, the strip's floor-3 end is inside the extender's sphere; a thruster and the pilot console on decks link and
    //      are active; a floor-3 thruster exhausting over open air is free (its zone stays on its floor), a floor-2
    //      thruster exhausting over a deck is blocked;
    //   S4 a colonist deconstructs the extender: the limit is 500 again and the same top-floor cells drop out;
    //   S5 colonists uninstall the engine and install it on a floor-2 deck (real jobs, carried up the stairs): the ship is
    //      found from the deck (ground + decks), its own columns aboard, the console links to it; the launch is refused.
    public static partial class TestRunner
    {
        private static IntVec3 ssO, ssEngineUp;
        private static readonly List<IntVec3> ssStrip = new List<IntVec3>();
        private static readonly List<IntVec3> ssF2 = new List<IntVec3>();
        private static readonly List<IntVec3> ssF3 = new List<IntVec3>();
        private static HashSet<IntVec3> ssCut;
        private static int ssGround, ssExtCells;
        private static string ssUp, ssLeft;
        private static IntVec3 ssExt, ssUpCell, ssLeftCell;

        private static IntVec3 SS(int dx, int dz) => new IntVec3(ssO.x + dx, 0, ssO.z + dz);

        private static IntVec3 SSF(int dx, int dz, int k, Map map) => ProjectZ.CellOnFloor(SS(dx, dz), k, map);

        private static CompPilotConsole SsConsole(Map map, Building_GravEngine e) =>
            map.listerThings.AllThings.Select(t => t.TryGetComp<CompPilotConsole>()).FirstOrDefault(c => c != null && c.engine == e)
            ?? map.listerThings.AllThings.Select(t => t.TryGetComp<CompPilotConsole>()).FirstOrDefault(c => c != null);

        private static int SsOnFloor(IEnumerable<IntVec3> cells, int k, Map map) => cells.Count(c => ProjectZ.FloorOf(c, map) == k);

        private static string SsFloors(IEnumerable<IntVec3> cells, Map map) =>
            $"ground {SsOnFloor(cells, 0, map)}, f2 {SsOnFloor(cells, 1, map)}, f3 {SsOnFloor(cells, 2, map)}";

        // The launch with upper cells in the ship: refused by the console (with the reason) and by the take-off.
        private static void SsLaunchRefused(Map map, Building_GravEngine e, string label)
        {
            CompPilotConsole console = SsConsole(map, e);
            if (console == null)
            {
                Result(label + " launch refused", false, "no console");
                return;
            }
            AcceptanceReport r = console.CanUseNow();
            string want = "PZ_ShipUpperCantFly".Translate();
            Result(label + " the console refuses the launch", !r.Accepted && r.Reason == want, r.Accepted ? "accepted" : r.Reason);
            if (e.cooldownCompleteTick > Find.TickManager.TicksGame)
            {
                e.cooldownCompleteTick = Find.TickManager.TicksGame;
            }
            console.StartChoosingDestination_NewTemp();
            bool picker = Find.TilePicker.Active;
            if (picker)
            {
                HarmonyLib.AccessTools.Method(typeof(TilePicker), "StopTargetingInt").Invoke(Find.TilePicker, null);
            }
            Find.GravshipController.InitiateTakeoff(e, map.Tile);
            bool cut = WorldComponent_GravshipController.CutsceneInProgress;
            Result(label + " no destination picking, no take-off", !picker && !cut && e.Spawned, $"picker {picker}, cutscene {cut}");
        }

        // The launch dialog's text (the game's PreLaunchConfirmation), closed without launching.
        private static string SsDialog(Building_GravEngine e)
        {
            int before = Find.WindowStack.Windows.Count;
            GravshipUtility.PreLaunchConfirmation(e, () => { });
            Dialog_MessageBox box = Find.WindowStack.Windows.OfType<Dialog_MessageBox>().LastOrDefault();
            string text = box?.text.ToString() ?? "";
            if (box != null)
            {
                Find.WindowStack.TryRemove(box, doCloseSound: false);
            }
            return text;
        }

        private static void StepShipSet(Map map)
        {
            Building_GravEngine e = GlEngine(map);
            switch (stage)
            {
                case "ss_setup":
                    SetupShipSet(map);
                    return;

                // ---- S1 the set
                case "ss_count":
                {
                    HashSet<IntVec3> all = e.AllConnectedSubstructure, valid = e.ValidSubstructure;
                    int decks = ssF2.Count + ssF3.Count;
                    Result("S1 all connected = ground + decks", all.Count == ssGround + decks, $"{all.Count}: {SsFloors(all, map)} (ground {ssGround}, decks {ssF2.Count} + {ssF3.Count})");
                    string insp = e.GetInspectString();
                    Result("S1 the engine's count shows ground + decks", insp.Contains(all.Count.ToString()), insp.Replace("\n", " | "));
                    int limit = (int)e.GetStatValue(StatDefOf.SubstructureSupport);
                    Result("S1 the limit cuts the top floor first", limit == 500 && valid.Count == 500 && SsOnFloor(valid, 0, map) == ssGround && SsOnFloor(valid, 1, map) == ssF2.Count && SsOnFloor(valid, 2, map) == 500 - ssGround - ssF2.Count,
                        $"limit {limit}, valid {valid.Count}: {SsFloors(valid, map)}");
                    Result("S1 no accepted cell floats", valid.All(c => ProjectZ.FloorOf(c, map) == 0 || valid.Contains(FloorRoof.Below(c, map))), "");
                    ssCut = new HashSet<IntVec3>(valid);
                    // the sphere: the strip's end column at ground distance 18.68 from the engine (radius 18.9)
                    IntVec3 g = SS(26, 11);
                    bool in0 = GravshipUtility.InsideFootprint(g, map), in1 = GravshipUtility.InsideFootprint(SSF(26, 11, 1, map), map), in2 = GravshipUtility.InsideFootprint(SSF(26, 11, 2, map), map);
                    Result("S1 the footprint is a sphere (ground and floor 2 in, floor 3 out at 18.68 cells)", in0 && in1 && !in2, $"ground {in0}, f2 {in1}, f3 {in2}");
                    Result("S1 a floor-3 cell outside the sphere is not valid even under the limit (all connected, not in range)", all.Contains(SSF(26, 11, 2, map)), "");
                    Result("S1 the 4 flat-area sites read the ground cells", Patches_ShipSetGround(), string.Join(", ", ShipSetGround_Patch.PerMethod.Select(kv => kv.Key + " " + kv.Value)));
                    HashSet<IntVec3> ground = ShipSet.Ground(e);
                    Result("S1 the ship's ground cells", ground.Count == ssGround && ground.All(c => ProjectZ.FloorOf(c, map) == 0), $"{ground.Count}");
                    // S2 aboard: one colonist to a floor-2 deck, one to a floor-3 deck past the limit
                    List<Pawn> cols = Colonists(map);
                    ssUpCell = SSF(4, 6, 1, map);
                    ssLeftCell = ssF3.FirstOrDefault(c => !valid.Contains(c) && c.Standable(map) && ProjectZ.GroundCellOf(c, map).x <= ssO.x + 8);
                    ssUp = cols[0].ThingID;
                    ssLeft = cols[1].ThingID;
                    GoTo(cols[0], ssUpCell);
                    GoTo(cols[1], ssLeftCell);
                    Info($"S2 colonists to {ssUpCell} (f2) and {ssLeftCell} (f3, past the limit)");
                    TickUntil(10000, () => ById(map, ssUp)?.Position == ssUpCell && ById(map, ssLeft)?.Position == ssLeftCell, "ss_aboard", 30);
                    return;
                }
                case "ss_aboard":
                {
                    tickWatch = null;
                    Pawn up = ById(map, ssUp), left = ById(map, ssLeft);
                    Result("S2 colonists walked up onto the decks", up?.Position == ssUpCell && left?.Position == ssLeftCell, $"{up?.Position}, {left?.Position}");
                    string text = SsDialog(e);
                    int listAt = text.IndexOf("PeopleWillBeLeftBehind".Translate().Resolve());
                    string list = listAt < 0 ? "" : text.Substring(listAt);
                    Result("S2 the colonist on a floor-2 deck is aboard", up != null && e.ValidSubstructureAt(up.Position) && !list.Contains(up.LabelShort), "");
                    Result("S2 the colonist on a floor-3 deck past the limit is left behind", left != null && !e.ValidSubstructureAt(left.Position) && list.Contains(left.LabelShort), list.Replace("\n", " | "));
                    Result("S2 the launch dialog warns of disconnected substructure", text.Contains("DisconnectedSubstructure".Translate().Resolve()), "");
                    SsLaunchRefused(map, e, "S2");
                    foreach (Pawn p in new[] { up, left })
                    {
                        if (p != null)
                        {
                            Draft(p, false);
                        }
                    }
                    // S3 colonists build an extender on a floor-2 deck
                    ThingDef extDef = DefDatabase<ThingDef>.GetNamed("GravFieldExtender");
                    ssExt = SSF(13, 3, 1, map);
                    FloorView.Set(1);
                    Designator_Build d = new Designator_Build(extDef);
                    AcceptanceReport r = d.CanDesignateCell(ssExt);
                    Result("S3 an extender may be placed on a floor-2 deck", r.Accepted, GlRep(r));
                    if (!r.Accepted)
                    {
                        Finish();
                        return;
                    }
                    d.DesignateSingleCell(ssExt);
                    FloorView.Set(0);
                    GlShot(1, SS(8, 6), 14f, "ss_0_decks_f2");
                    GlShot(2, SS(8, 6), 14f, "ss_0_decks_f3");
                    SetWork(map, true);
                    SsHaul(map);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(40000, () => ssExt.GetEdifice(map)?.def == extDef, "ss_ext_built", 120);
                    return;
                }
                case "ss_ext_built":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    Building ext = ssExt.GetEdifice(map);
                    Result("S3 colonists built the extender on the deck", ext?.def.defName == "GravFieldExtender", $"{ext?.def.defName} after {ticksTotal} ticks");
                    if (ext == null)
                    {
                        Finish();
                        return;
                    }
                    StartTicks(120, "ss_ext");
                    return;
                }
                case "ss_ext":
                {
                    Building ext = ssExt.GetEdifice(map);
                    CompGravshipFacility f = ext?.TryGetComp<CompGravshipFacility>();
                    HashSet<IntVec3> valid = e.ValidSubstructure;
                    int limit = (int)e.GetStatValue(StatDefOf.SubstructureSupport);
                    Result("S3 the extender links to the engine and is active", f != null && f.LinkedBuildings.Contains(e) && f.CanBeActive, $"linked {f?.LinkedBuildings.Count}, active {f?.CanBeActive}");
                    Result("S3 the limit rose to 750 and every deck is in", limit == 750 && valid.Count == e.AllConnectedSubstructure.Count, $"limit {limit}, valid {valid.Count}: {SsFloors(valid, map)}, all {e.AllConnectedSubstructure.Count}");
                    bool in2 = GravshipUtility.InsideFootprint(SSF(26, 11, 2, map), map);
                    Result("S3 the extender's sphere takes in the strip's floor-3 end", in2 && valid.Contains(SSF(26, 11, 2, map)), $"inside {in2}");
                    ssExtCells = valid.Count;
                    // thrusters and the pilot console (moved up: one per engine) on decks (placed): a floor-3 thruster over open
                    // air, a floor-2 one over a deck
                    SsConsole(map, e)?.parent.Destroy();
                    Thing tFree = Place(DefDatabase<ThingDef>.GetNamed("SmallThruster"), null, SSF(4, 1, 2, map), map, Rot4.North);
                    Thing tBlocked = Place(DefDatabase<ThingDef>.GetNamed("SmallThruster"), null, SSF(15, 8, 1, map), map, Rot4.North);
                    Thing console2 = Place(DefDatabase<ThingDef>.GetNamed("PilotConsole"), null, SSF(6, 9, 2, map), map, Rot4.North);
                    ssThrusters = new[] { tFree, tBlocked, console2 };
                    StartTicks(250, "ss_parts");
                    return;
                }
                case "ss_parts":
                {
                    Thing tFree = ssThrusters[0], tBlocked = ssThrusters[1], console2 = ssThrusters[2];
                    foreach (Thing t in new[] { tFree, console2 })
                    {
                        CompGravshipFacility f = t.TryGetComp<CompGravshipFacility>();
                        Result($"S3 a {t.def.defName} on a f{ProjectZ.FloorOf(t.Position, map) + 1} deck links and is active", f != null && f.LinkedBuildings.Contains(e) && f.CanBeActive, $"{t.Position} linked {f?.LinkedBuildings.Count}, active {f?.CanBeActive}");
                    }
                    bool free = CompGravshipThruster.IsBlocked(tFree.def, map, tFree.Position, tFree.Rotation, out Thing by1, out bool sub1);
                    List<IntVec3> zone = null;
                    tFree.def.GetCompProperties<CompProperties_GravshipThruster>().GetExclusionZone(tFree.Position, tFree.Rotation, ref zone);
                    Result("S3 a floor-3 thruster over open air is free", !free && zone.All(c => ProjectZ.FloorOf(c, map) == 2),
                        $"blocked {free} by {by1?.def.defName} sub {sub1}; zone {zone.Count} cells on floor 3: {string.Join(" ", zone.Select(c => map.terrainGrid.TerrainAt(c)?.defName))}");
                    bool blocked = CompGravshipThruster.IsBlocked(tBlocked.def, map, tBlocked.Position, tBlocked.Rotation, out Thing by2, out bool sub2);
                    CompGravshipFacility fb = tBlocked.TryGetComp<CompGravshipFacility>();
                    Result("S3 a floor-2 thruster over a deck is blocked by it (and, as vanilla, not linked)", blocked && sub2 && fb != null && !fb.CanBeActive, $"blocked {blocked}, by {by2?.def.defName}, substructure {sub2}, linked {fb?.LinkedBuildings.Count}");
                    // the exhaust zone at a band's south edge (a thruster 2 rows into floor 3) stays on that floor
                    AnnexMapComponent an = ProjectZ.Annex(map);
                    IntVec3 edge = new IntVec3(tFree.Position.x, 0, 2 * an.stride + 2);
                    ThrusterZone.clipMap = null;
                    tFree.def.GetCompProperties<CompProperties_GravshipThruster>().GetExclusionZone(edge, Rot4.North, ref zone);
                    int raw = zone.Count, rawOff = zone.Count(c => ProjectZ.FloorOf(c, map) != 2);
                    ThrusterZone.clipMap = map;
                    tFree.def.GetCompProperties<CompProperties_GravshipThruster>().GetExclusionZone(edge, Rot4.North, ref zone);
                    ThrusterZone.clipMap = null;
                    Result("S3 a thruster zone at a band's edge is clipped to its floor", rawOff > 0 && zone.Count == raw - rawOff && zone.All(c => ProjectZ.FloorOf(c, map) == 2), $"{raw} cells, {rawOff} off floor 3, clipped to {zone.Count}");
                    GlShot(1, SS(10, 6), 10f, "ss_1_extender_f2");
                    // S4 a colonist deconstructs the extender
                    Building ext = ssExt.GetEdifice(map);
                    new Designator_Deconstruct().DesignateThing(ext);
                    SetWork(map, true);
                    TickUntil(20000, () => ssExt.GetEdifice(map) == null, "ss_ext_gone", 60);
                    return;
                }
                case "ss_ext_gone":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    StartTicks(120, "ss_ext_gone2");
                    return;
                }
                case "ss_ext_gone2":
                {
                    HashSet<IntVec3> valid = e.ValidSubstructure;
                    int limit = (int)e.GetStatValue(StatDefOf.SubstructureSupport);
                    Result("S4 a colonist deconstructed the extender", ssExt.GetEdifice(map) == null, "");
                    Result("S4 the limit is 500 again: the top floor drops out first", limit == 500 && valid.Count == 500 && SsOnFloor(valid, 0, map) == ssGround && SsOnFloor(valid, 1, map) == ssF2.Count, $"limit {limit}, valid {valid.Count}: {SsFloors(valid, map)} (was {ssExtCells})");
                    Result("S4 the same top-floor cells as before the extender", valid.SetEquals(ssCut), $"{valid.Count(c => !ssCut.Contains(c))} differ");
                    // S5 the engine uninstalled and installed on a floor-2 deck (the parts on decks go; the console back on the
                    // ground: on floor 3 it would be past the limit)
                    foreach (Thing t in ssThrusters)
                    {
                        if (t.Spawned)
                        {
                            t.Destroy();
                        }
                    }
                    Place(DefDatabase<ThingDef>.GetNamed("PilotConsole"), null, SS(8, 9), map, Rot4.North);
                    new Designator_Uninstall().DesignateThing(e);
                    SetWork(map, true);
                    SsHaul(map);
                    TickUntil(20000, () => GlEngine(map) == null && map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("MinifiedGravEngine")).Any(), "ss_minified", 60);
                    return;
                }
                case "ss_minified":
                {
                    tickWatch = null;
                    Thing mini = map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("MinifiedGravEngine")).FirstOrDefault();
                    Result("S5 a colonist uninstalled the engine", mini != null && GlEngine(map) == null, $"{mini?.Position}");
                    if (mini == null)
                    {
                        Finish();
                        return;
                    }
                    ssEngineUp = SSF(12, 3, 1, map);
                    FloorView.Set(1);
                    Designator_Install di = new Designator_Install();
                    SsSelect(mini);
                    AcceptanceReport r = di.CanDesignateCell(ssEngineUp);
                    Result("S5 the engine may be installed on a floor-2 deck", r.Accepted, GlRep(r));
                    if (!r.Accepted)
                    {
                        FloorView.Set(0);
                        Finish();
                        return;
                    }
                    di.DesignateSingleCell(ssEngineUp);
                    FloorView.Set(0);
                    Find.DesignatorManager.Deselect();
                    TickUntil(40000, () => GlEngine(map)?.Position == ssEngineUp, "ss_installed", 120);
                    return;
                }
                case "ss_installed":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    Result("S5 colonists installed the engine on the floor-2 deck", e != null && e.Position == ssEngineUp, $"{e?.Position} f{(e == null ? 0 : ProjectZ.FloorOf(e.Position, map) + 1)} after {ticksTotal} ticks");
                    if (e == null)
                    {
                        Finish();
                        return;
                    }
                    e.nameHidden = false;
                    StartTicks(250, "ss_up");
                    return;
                }
                case "ss_up":
                {
                    HashSet<IntVec3> all = e.AllConnectedSubstructure, valid = e.ValidSubstructure;
                    Result("S5 the ship is found from the deck: ground + decks", all.Count == ssGround + ssF2.Count + ssF3.Count, $"{all.Count}: {SsFloors(all, map)}");
                    bool own = e.OccupiedRect().Cells.All(c => valid.Contains(c) && valid.Contains(FloorRoof.Below(c, map)));
                    Result("S5 the engine's own columns are aboard; ground and floor 2 complete under the limit", own && valid.Count == 500 && SsOnFloor(valid, 0, map) == ssGround && SsOnFloor(valid, 1, map) == ssF2.Count, $"own {own}, valid {valid.Count}: {SsFloors(valid, map)}");
                    CompPilotConsole c = SsConsole(map, e);
                    Result("S5 the ground console links to the engine upstairs and is active", c != null && c.engine == e && c.CanBeActive, $"engine {c?.engine?.Position}, active {c?.CanBeActive}");
                    SsLaunchRefused(map, e, "S5");
                    GlShot(1, SS(10, 4), 10f, "ss_2_engine_f2");
                    GlRun("ss_end");
                    return;
                }
                case "ss_end":
                    Finish();
                    return;
            }
        }

        private static Thing[] ssThrusters;

        private static bool Patches_ShipSetGround()
        {
            Dictionary<string, int> m = ShipSetGround_Patch.PerMethod;
            return m.Count >= 4 && m.Values.All(n => n >= 1) && m.Values.Sum() >= 4;
        }

        private static void SsSelect(Thing mini)
        {
            // (the install designator works on the selected minified thing, as when the player clicks "install")
            Find.Selector.ClearSelection();
            Find.Selector.Select(mini, playSound: false, forceDesignatorDeselect: false);
        }

        private static void SsHaul(Map map)
        {
            foreach (Pawn p in Colonists(map))
            {
                Draft(p, false);
                if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                {
                    p.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2);
                }
            }
        }

        private static void SetupShipSet(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || an.floors < 3 || !ModsConfig.OdysseyActive || PZDefOf.PZ_ShipDeck == null)
            {
                Result("shipset setup", false, $"needs 3 floors and Odyssey; floors {an?.floors}");
                Finish();
                return;
            }
            const int W = 50, H = 26;
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
                Result("shipset field", false, "map too small");
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
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            for (int i = 0; i < 24 && GenLocalDate.HourOfDay(map) != 10; i++)
            {
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + GenDate.TicksPerHour);
            }
            ssO = new IntVec3(found.minX + 6, 0, found.minZ + 6);
            // the ship: 17 x 13, engine at (8, 6)
            BuildShip(map, ssO, 17, 13);
            Building_GravEngine e = GlEngine(map);
            e.nameHidden = false;
            TerrainDef sub = DefDatabase<TerrainDef>.GetNamed("Substructure");
            TerrainDef wood = PZDefOf.PZ_UpperFloor;
            // the strip: dx 9..18 from the engine on its row dz 5 (x 17..26, z 11); wood walls north of it hold the roofs
            ssStrip.Clear();
            for (int x = 17; x <= 26; x++)
            {
                ssStrip.Add(SS(x, 11));
                map.terrainGrid.SetTerrain(SS(x, 11), sub);
            }
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SS(22, 12), map);
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SS(26, 12), map);
            ssGround = 17 * 13 + 4 + ssStrip.Count;
            // access as in shipdeck: stairs A on the ground up to a floor-2 walkway west of the ship, stairs B on it up to a
            // floor-3 walkway cell next to the floor-3 decks
            foreach (int dz in Enumerable.Range(0, 10))
            {
                map.terrainGrid.SetTerrain(SSF(-1, dz, 1, map), wood);
            }
            foreach (int dz in new[] { 4, 5, 6 })
            {
                map.terrainGrid.SetTerrain(SSF(-2, dz, 1, map), wood);
            }
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SS(-2, 2), map);
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SS(-2, 8), map);
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, SS(-2, -2), map, Rot4.North);
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, SSF(-2, 5, 1, map), map, Rot4.North);
            map.terrainGrid.SetTerrain(SSF(-1, 7, 2, map), wood);
            // floor-2 roof holders for the floor-3 decks (wood walls on floor-2 decks), placed first
            ssF2.Clear();
            ssF3.Clear();
            for (int x = 0; x < 17; x++)
            {
                for (int z = 0; z < 13; z++)
                {
                    ssF2.Add(SSF(x, z, 1, map));
                }
            }
            ssF2.AddRange(ssStrip.Select(g => ProjectZ.CellOnFloor(g, 1, map)));
            for (int x = 0; x <= 8; x++)
            {
                for (int z = 1; z <= 11; z++)
                {
                    ssF3.Add(SSF(x, z, 2, map));
                }
            }
            ssF3.AddRange(ssStrip.Select(g => ProjectZ.CellOnFloor(g, 2, map)));
            foreach (IntVec3 c in ssF2)
            {
                map.terrainGrid.SetTerrain(c, PZDefOf.PZ_ShipDeck);
            }
            foreach ((int x, int z) in new[] { (0, 2), (0, 6), (0, 10), (9, 2), (9, 6), (9, 10), (17, 11), (22, 11), (26, 11) })
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SSF(x, z, 1, map), map);
            }
            foreach (IntVec3 c in ssF3)
            {
                map.terrainGrid.SetTerrain(c, PZDefOf.PZ_ShipDeck);
            }
            e.ForceSubstructureDirty();
            // stocks for the extender
            ThingDef extDef = DefDatabase<ThingDef>.GetNamed("GravFieldExtender");
            int sx = 0;
            foreach (ThingDefCountClass cc in extDef.CostList)
            {
                int left = cc.count * 2;
                while (left > 0)
                {
                    Thing t = ThingMaker.MakeThing(cc.thingDef);
                    t.stackCount = Mathf.Min(left, cc.thingDef.stackLimit);
                    left -= t.stackCount;
                    GenSpawn.Spawn(t, SS(-7 + sx % 4, -5 + sx / 4), map);
                    sx++;
                }
            }
            while (cols.Count < 3)
            {
                Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(p, SS(-5, -3), map);
                cols.Add(p);
            }
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], SS(-5 - i, -3));
                Draft(cols[i], false);
                SkillRecord s = cols[i].skills?.GetSkill(SkillDefOf.Construction);
                if (s != null && !s.TotallyDisabled)
                {
                    s.Level = Mathf.Max(s.Level, 10);
                }
            }
            SetWork(map, false);
            Info($"field {found}, ship SW {ssO}, engine {e.Position}, ground {ssGround}, decks f2 {ssF2.Count} f3 {ssF3.Count}, strip {ssStrip.Count}, extender cost {string.Join(", ", extDef.CostList.Select(c => c.thingDef.defName + " " + c.count))}, ground rows {an.groundZ}, stride {an.stride}");
            Wait(30, "ss_count");
        }
    }
}
