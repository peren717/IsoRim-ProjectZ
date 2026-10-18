using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode space (Odyssey orbit maps, user bug 2026-10-16: on the orbital start no landing came with the stairs and no
    // upper floor could be laid). The orbital relay map of "The Gravship" start is generated with its annex on a free orbit
    // tile and made the colony's map; the colonists (in vacsuits) are moved there. On a platform laid over space:
    //   E1 stairs placed with the designator and built by colonists: the landing comes with its roof;
    //   E2 a steel column built by colonists; upper floors dragged out from the landing (held by the stairs) and around
    //      the column, refused far from any support; built by colonists;
    //   E3 a colonist walks up the stairs onto the new floor;
    //   E4 a slab taken up by a colonist (remove tool): open air again, the roof under it an ordinary one;
    //   E5 vacuum: the cells under the landing and the slabs (roofed, open sides) as the game's rooms see them.
    public static partial class TestRunner
    {
        private static Map eMap;
        private static IntVec3 eO, eStairs, eColumn, eFar, eRemove;
        private static CellRect ePlat;
        private static readonly List<IntVec3> eRow = new List<IntVec3>();
        private static readonly List<IntVec3> eRing = new List<IntVec3>();
        private static string eWalker;

        private static IntVec3 E(int dx, int dz) => new IntVec3(eO.x + dx, 0, eO.z + dz);

        private static string IsoFloorsOn()
        {
            return $"IsoRim stacked floors on: {IsoRimBridge.FloorsDrawn}, world render mode {WorldRendererUtility.CurrentWorldRenderMode}";
        }

        private static string Census(Map map, IEnumerable<IntVec3> cells)
        {
            Dictionary<string, int> top = new Dictionary<string, int>(), at = new Dictionary<string, int>(), under = new Dictionary<string, int>(), found = new Dictionary<string, int>(), temp = new Dictionary<string, int>(), roof = new Dictionary<string, int>();
            void Add(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out int v) ? v + 1 : 1;
            foreach (IntVec3 c in cells)
            {
                int i = map.cellIndices.CellToIndex(c);
                Add(top, map.terrainGrid.topGrid[i]?.defName ?? "null");
                Add(at, map.terrainGrid.TerrainAt(i)?.defName ?? "null");
                Add(under, map.terrainGrid.UnderTerrainAt(i)?.defName ?? "null");
                Add(found, map.terrainGrid.FoundationAt(i)?.defName ?? "null");
                Add(temp, map.terrainGrid.TempTerrainAt(i)?.defName ?? "null");
                Add(roof, map.roofGrid.RoofAt(c)?.defName ?? "null");
            }
            string S(Dictionary<string, int> d) => string.Join(" ", d.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + "=" + kv.Value));
            return $"TerrainAt [{S(at)}] top [{S(top)}] under [{S(under)}] foundation [{S(found)}] temp [{S(temp)}] roof [{S(roof)}]";
        }

        private static string Vac(Map map, IntVec3 c)
        {
            Room r = c.GetRoom(map);
            return $"{c} roof {map.roofGrid.RoofAt(c)?.defName ?? "none"}, room {r?.ID} cells {r?.CellCount} exposed {r?.ExposedToSpace} vacuum {r?.Vacuum:0.00} outdoors-temp {r?.UsesOutdoorTemperature}, temp {c.GetTemperature(map):0}";
        }

        private static Building_Stairs EStairs(Map map) => map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).OfType<Building_Stairs>().FirstOrDefault(s => s.Position == eStairs);

        private static bool EColumnBuilt(Map map) => eColumn.GetEdifice(map)?.def == ThingDefOf.Column;

        private static bool StepSpace(Map map)
        {
            switch (stage)
            {
                case "e_setup":
                {
                    PlanetLayer orbit = Find.WorldGrid.Orbit;
                    PlanetTile tile = PlanetTile.Invalid;
                    for (int i = 0; orbit != null && i < orbit.TilesCount; i++)
                    {
                        PlanetTile pt = new PlanetTile(i, orbit);
                        if (!Find.WorldObjects.AnyWorldObjectAt(pt))
                        {
                            tile = pt;
                            break;
                        }
                    }
                    if (!tile.Valid)
                    {
                        Result("orbit tile", false, "no free orbit tile");
                        Finish();
                        return true;
                    }
                    // (as ScenPart_ForcedMap + the game's start: a player settlement on the orbit tile, the relay's generator)
                    Settlement s = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
                    s.SetFaction(Faction.OfPlayer);
                    s.Tile = tile;
                    s.Name = "PZ orbit";
                    Find.WorldObjects.Add(s);
                    MapGeneratorDef gen = DefDatabase<MapGeneratorDef>.GetNamed("OrbitalRelay");
                    int n = GenCommandLine.TryGetCommandLineArg("projectzmapsize", out string ms) && int.TryParse(ms, out int nn) ? nn : 150;
                    eMap = MapGenerator.GenerateMap(new IntVec3(n, 1, n), s, gen, s.ExtraGenStepDefs);
                    Info($"orbit map {eMap} generator {eMap.generatorDef.defName} biome {eMap.Biome.defName} (vacuum {eMap.Biome.inVacuum}) size {eMap.Size}");
                    Current.Game.CurrentMap = eMap;
                    Wait(30, "e_census");
                    return true;
                }
                case "e_census":
                {
                    AnnexMapComponent a = ProjectZ.Annex(map);
                    Result("orbit map has its annex", ReferenceEquals(map, eMap) && a != null, a == null ? "none" : $"ground {a.groundZ}, stride {a.stride}, floors {a.floors}");
                    if (a == null)
                    {
                        Finish();
                        return true;
                    }
                    List<IntVec3> floor2 = ProjectZ.GroundRect(map).Cells.Select(g => ProjectZ.CellOnFloor(g, 1, map)).ToList();
                    Info("floor 2 after generation: " + Census(map, floor2));
                    Info("ground after generation: " + Census(map, ProjectZ.GroundRect(map).Cells));
                    SetupSpace(map);
                    return true;
                }
                case "e_stairs":
                {
                    ShotAt(map, 0, ePlat.CenterCell, 12f);
                    Designator_Build d = new Designator_Build(PZDefOf.PZ_Stairs);
                    d.SetStuffDef(ThingDefOf.WoodLog);
                    Find.DesignatorManager.Select(d);
                    SetRot(d, Rot4.North);
                    AcceptanceReport r = RefusalAt(d, ScreenOf(eStairs, 0), out IntVec3 c);
                    Result("E1 stairs on the platform: the designator accepts them", r.Accepted && c == eStairs, $"{c}: {(r.Accepted ? "accepted" : r.Reason)}; landing {StairShape.Exit(eStairs, Rot4.North, map)} terrain {StairShape.Exit(eStairs, Rot4.North, map).GetTerrain(map)?.defName}");
                    if (r.Accepted)
                    {
                        UiDesignate(ScreenOf(eStairs, 0), ScreenOf(eStairs, 0));
                    }
                    Designator_Build col = new Designator_Build(ThingDefOf.Column);
                    col.SetStuffDef(ThingDefOf.Steel);
                    Find.DesignatorManager.Select(col);
                    AcceptanceReport rc = RefusalAt(col, ScreenOf(eColumn, 0), out IntVec3 cc);
                    if (rc.Accepted)
                    {
                        UiDesignate(ScreenOf(eColumn, 0), ScreenOf(eColumn, 0));
                    }
                    Find.DesignatorManager.Deselect();
                    Result("E2 a steel column on the platform: designated", rc.Accepted && cc == eColumn, $"{cc}: {(rc.Accepted ? "accepted" : rc.Reason)}");
                    SetWork(map, true);
                    foreach (Pawn p in Colonists(map))
                    {
                        if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                        {
                            p.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2);
                        }
                    }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(40000, () => EStairs(map) != null && EColumnBuilt(map), "e_built", 120);
                    return true;
                }
                case "e_built":
                {
                    tickWatch = null;
                    Building_Stairs st = EStairs(map);
                    IntVec3 top = st?.TopCell ?? IntVec3.Invalid;
                    IntVec3 below = top.IsValid ? FloorRoof.Below(top, map) : IntVec3.Invalid;
                    Result("E1 colonists built the stairs; the landing came with them, its roof under it", st != null && SlabDefs.Is(top.GetTerrain(map)) && map.roofGrid.RoofAt(below) == PZDefOf.PZ_FloorRoof
                        && StairShape.Footprint(st.Position, st.Rotation).Cells.All(f => map.roofGrid.RoofAt(f) == null || map.roofGrid.RoofAt(f) == PZDefOf.PZ_StairwellRoof),
                        st == null ? $"no stairs after {ticksTotal} ticks" : $"landing {top} {top.GetTerrain(map)?.defName}; under it {below} {map.roofGrid.RoofAt(below)?.defName ?? "no roof"} on {below.GetTerrain(map)?.defName}; well roofs {string.Join(",", StairShape.Footprint(st.Position, st.Rotation).Cells.Select(f => map.roofGrid.RoofAt(f)?.defName ?? "none"))}");
                    Result("E2 colonists built the column", EColumnBuilt(map), $"{eColumn}: {eColumn.GetEdifice(map)}");
                    if (st == null)
                    {
                        Finish();
                        return true;
                    }
                    Info("E5 under the landing: " + Vac(map, below));
                    Info(IsoFloorsOn());
                    ShotAt(map, 1, ePlat.CenterCell, 12f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(20, "e_shot1");
                    return true;
                }
                case "e_shot1":
                    ZCapture("space_stairs_landing");
                    Wait(5, "e_slabs");
                    return true;
                case "e_slabs":
                {
                    // floor 2 viewed: a row out from the landing (held by the stairs) and a ring over the column
                    TerrainDef wood = SlabDefs.For(ThingDefOf.WoodLog);
                    Designator_Build d = new Designator_Build(wood);
                    Find.DesignatorManager.Select(d);
                    AcceptanceReport far = RefusalAt(d, ScreenOf(eFar, 1), out IntVec3 cf);
                    Result("E2 an upper floor far from any support is refused", !far.Accepted && far.Reason == "PZ_NoSupportBelow".Translate() && cf == UpS(eFar, map), $"{cf}: '{far.Reason}'");
                    AcceptanceReport first = RefusalAt(d, ScreenOf(eRow[0], 1), out IntVec3 c0);
                    Result("E2 an upper floor next to the landing is accepted (the stairs hold it)", first.Accepted && c0 == UpS(eRow[0], map), $"{c0}: {(first.Accepted ? "accepted" : first.Reason)}");
                    // The row out to the column: one drag reaches only the cells held now (the game checks each dragged cell
                    // before any is placed); a player drags again from the planned ones, as vanilla roofs are built out.
                    List<IntVec3> want = eRow.Concat(eRing).Distinct().Select(g => UpS(g, map)).ToList();
                    List<string> passes = new List<string>();
                    for (int pass = 0; pass < 10 && want.Any(u => !PlaceWorker_Stairs.FloorPlanned(u, map)); pass++)
                    {
                        int before = want.Count(u => PlaceWorker_Stairs.FloorPlanned(u, map));
                        UiDesignate(ScreenOf(eRow[0], 1), ScreenOf(eRow[eRow.Count - 1], 1));
                        UiDesignate(ScreenOf(eRing[0], 1), ScreenOf(eRing[eRing.Count - 1], 1));
                        passes.Add((want.Count(u => PlaceWorker_Stairs.FloorPlanned(u, map)) - before).ToString());
                    }
                    Find.DesignatorManager.Deselect();
                    int planned = want.Count(u => PlaceWorker_Stairs.FloorPlanned(u, map));
                    Result("E2 the dragged upper floors are planned (drags repeated outward from the support)", planned == want.Count, $"{planned}/{want.Count} planned; new cells per pass of the two drags: {string.Join(",", passes)}");
                    FloorView.Set(0);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(60000, () => want.All(u => SlabDefs.Is(u.GetTerrain(map))), "e_slabs_built", 120);
                    return true;
                }
                case "e_slabs_built":
                {
                    tickWatch = null;
                    List<IntVec3> want = eRow.Concat(eRing).Distinct().Select(g => UpS(g, map)).ToList();
                    int built = want.Count(u => SlabDefs.Is(u.GetTerrain(map)));
                    int roofed = want.Count(u => map.roofGrid.RoofAt(FloorRoof.Below(u, map)) == PZDefOf.PZ_FloorRoof);
                    Result("E2 colonists built the upper floors; each brought its roof", built == want.Count && roofed == want.Count, $"built {built}/{want.Count}, roofs under them {roofed}/{want.Count}, after {ticksTotal} ticks");
                    Info("E5 under a slab by the column: " + Vac(map, eRing[0]));
                    Info("E5 on a slab: " + Vac(map, UpS(eRing[0], map)));
                    // E3: walk up
                    Pawn w = Colonists(map).First();
                    eWalker = w.ThingID;
                    Teleport(w, E(17, 1));
                    Mark();
                    GoTo(w, UpS(eRow[eRow.Count - 1], map));
                    TickUntil(4000, () => w.Position == UpS(eRow[eRow.Count - 1], map) && !w.pather.Moving, "e_walked", 30);
                    return true;
                }
                case "e_walked":
                {
                    tickWatch = null;
                    Pawn w = ById(map, eWalker);
                    Result("E3 a colonist walked up the stairs onto the new floor", w != null && w.Position == UpS(eRow[eRow.Count - 1], map) && climbsColonists - climbsAt >= 1, $"{w} at {w?.Position}, {Counts()}");
                    Draft(w, false);
                    ShotAt(map, 1, ePlat.CenterCell, 12f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(20, "e_shot2");
                    return true;
                }
                case "e_shot2":
                {
                    ZCapture("space_floors_up");
                    // E4: the slab at the row's end taken up with the remove tool
                    Designator_RemoveFoundation tool = AllDesignators().OfType<Designator_RemoveFoundation>().FirstOrDefault();
                    Find.DesignatorManager.Select(tool);
                    AcceptanceReport r = RefusalAt(tool, ScreenOf(eRemove, 1), out IntVec3 c);
                    if (r.Accepted)
                    {
                        UiDesignate(ScreenOf(eRemove, 1), ScreenOf(eRemove, 1));
                    }
                    Find.DesignatorManager.Deselect();
                    bool des = map.designationManager.DesignationAt(UpS(eRemove, map), DesignationDefOf.RemoveFoundation) != null;
                    Result("E4 the remove tool takes the far corner slab", r.Accepted && c == UpS(eRemove, map) && des, $"{c}: {(r.Accepted ? "accepted" : r.Reason)}, designated {des}");
                    FloorView.Set(0);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(20000, () => UpS(eRemove, map).GetTerrain(map) == PZDefOf.PZ_OpenAir, "e_removed", 60);
                    return true;
                }
                case "e_removed":
                {
                    tickWatch = null;
                    IntVec3 u = UpS(eRemove, map);
                    // (compat G2, user 2026-10-18: the slab's materials fall and break the ordinary roof under it; -projectznogravity: it stays)
                    Result(Gravity.Off ? "E4 a colonist took the slab up: open air, the roof under it an ordinary roof" : "E4 a colonist took the slab up: open air, its materials fell through the roof under it (broken)",
                        u.GetTerrain(map) == PZDefOf.PZ_OpenAir && (Gravity.Off ? map.roofGrid.RoofAt(eRemove) == RoofDefOf.RoofConstructed : !map.roofGrid.Roofed(eRemove) && CountOn(map, ThingDefOf.WoodLog, 1) == 0),
                        $"{u} {u.GetTerrain(map)?.defName}; under it {map.roofGrid.RoofAt(eRemove)?.defName ?? "no roof"}; wood on floor 2 {CountOn(map, ThingDefOf.WoodLog, 1)}");
                    Info("E5 under the removed slab: " + Vac(map, eRemove));
                    List<Pawn> cols = Colonists(map);
                    Info("colonists: " + string.Join("; ", cols.Select(p => $"{p.LabelShort} floor {ProjectZ.FloorOf(p.Position, map) + 1} health {p.health.summaryHealth.SummaryHealthPercent:0.00} downed {p.Downed} {InjuryNames(p)}")));
                    Result("colonists in vacsuits stayed well", cols.All(p => !p.Downed), $"{cols.Count(p => p.Downed)} downed");
                    ShotAt(map, 1, ePlat.CenterCell, 12f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(20, "e_shot3");
                    return true;
                }
                case "e_shot3":
                    ZCapture("space_slab_removed");
                    Wait(3, "e_shot4");
                    ShotAt(map, 0, ePlat.CenterCell, 12f);
                    return true;
                case "e_shot4":
                    Wait(20, "e_shot5");
                    return true;
                case "e_shot5":
                    ZCapture("space_ground_view");
                    Wait(5, "e_vac");
                    return true;
                case "e_vac":
                {
                    // E5: walls round the column under the floors (one cell's roof an ordinary one since E4): a closed room
                    // whose roof is the floor above.
                    CellRect box = CellRect.FromLimits(E(9, 4), E(13, 8));
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(p => box.Contains(p.Position)).ToList())
                    {
                        Teleport(p, E(17, 1));
                    }
                    foreach (IntVec3 c in box.EdgeCells)
                    {
                        Place(ThingDefOf.Wall, ThingDefOf.Steel, c, map);
                    }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    StartTicks(1250, "e_vac2");
                    return true;
                }
                case "e_vac2":
                {
                    IntVec3 inside = E(10, 6);
                    Room r = inside.GetRoom(map);
                    Info("E5 closed room under the floors: " + Vac(map, inside) + "; roofs " + string.Join(",", CellRect.FromLimits(E(10, 5), E(12, 7)).Cells.Select(c => map.roofGrid.RoofAt(c)?.defName ?? "none").Distinct()));
                    Result("E5 a closed room roofed by the floor above is not open to space (the game then treats it like any roofed room)", r != null && !r.ExposedToSpace && !r.UsesOutdoorTemperature, $"room {r?.ID}, cells {r?.CellCount}, exposed {r?.ExposedToSpace}, vacuum {r?.Vacuum:0.00}");
                    Finish();
                    return true;
                }
                case "e_end":
                    Finish();
                    return true;
            }
            return false;
        }

        // A 20 x 16 orbital platform laid over open space (as GenStep_OrbitalPlatform does: SetTerrain on Space), away from
        // the relay's own platforms; colonists in vacsuits, wood and steel.
        private static void SetupSpace(Map map)
        {
            TerrainDef space = TerrainDefOf.Space;
            TerrainDef plat = DefDatabase<TerrainDef>.GetNamed("OrbitalPlatform");
            int gz = ProjectZ.GroundZ(map);
            CellRect best = CellRect.Empty;
            for (int z = 6; z + 24 < gz - 6 && best.IsEmpty; z += 2)
            {
                for (int x = 6; x + 28 < map.Size.x - 6; x += 2)
                {
                    CellRect r = new CellRect(x, z, 28, 24);
                    if (r.Cells.All(c => c.GetTerrain(map) == space && c.GetFirstThing<Thing>(map) == null))
                    {
                        best = r;
                        break;
                    }
                }
            }
            if (best.IsEmpty)
            {
                Result("open space for the test platform", false, "none found");
                Finish();
                return;
            }
            ePlat = best.ContractedBy(4);
            foreach (IntVec3 c in ePlat)
            {
                map.terrainGrid.SetTerrain(c, plat);
                map.fogGrid.Unfog(c);
            }
            eO = ePlat.Min;
            eStairs = E(4, 3);                 // up to the north: landing over E(4, 5)
            eColumn = E(11, 6);                // the row passes south of it; a ring of floor over it
            eFar = E(18, 14);
            eRow.Clear();
            for (int x = 5; x <= 12; x++)
            {
                eRow.Add(E(x, 5));
            }
            eRemove = E(12, 7);                // the corner farthest from the stairs
            eRing.Clear();
            foreach (IntVec3 c in CellRect.CenteredOn(eColumn, 1))
            {
                eRing.Add(c);
            }
            Info($"platform {ePlat} over space ({Census(map, ePlat.Cells)}); stairs {eStairs} north, landing {StairShape.Exit(eStairs, Rot4.North, map)}; column {eColumn}");
            List<Pawn> cols = Find.Maps.Where(m => m != map).SelectMany(m => m.mapPawns.FreeColonistsSpawned).ToList();
            ThingDef suit = DefDatabase<ThingDef>.GetNamed("Apparel_Vacsuit"), helmet = DefDatabase<ThingDef>.GetNamed("Apparel_VacsuitHelmet");
            for (int i = 0; i < cols.Count; i++)
            {
                Pawn p = cols[i];
                p.jobs?.StopAll();
                p.DeSpawn();
                GenSpawn.Spawn(p, E(15 + i % 4, 1 + i / 4), map);
                foreach (ThingDef def in new[] { suit, helmet })
                {
                    Apparel ap = (Apparel)ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                    p.apparel.Wear(ap, dropReplacedApparel: false);
                }
            }
            foreach (ThingDef def in new[] { ThingDefOf.WoodLog, ThingDefOf.WoodLog, ThingDefOf.WoodLog, ThingDefOf.Steel })
            {
                Thing t = ThingMaker.MakeThing(def);
                t.stackCount = def.stackLimit;
                GenPlace.TryPlaceThing(t, E(17, 4), map, ThingPlaceMode.Near);
            }
            Info($"colonists moved: {string.Join(", ", Colonists(map).Select(p => p.LabelShort + " " + p.GetStatValue(StatDefOf.VacuumResistance).ToString("0.00")))} (vacuum resistance)");
            DebugSettings.enableRandomMentalStates = false;
            Find.PlaySettings.autoRebuild = false;
            AnnexWatch.Reset();
            Wait(10, "e_stairs");
        }
    }
}
