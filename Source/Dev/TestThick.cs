using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode thick (thick rock roofs; SlabSupport.ThickRoof, PlaceWorker_Stairs.ThickRoofCheck, the SetRoof guard).
    // Scene: a small mountain M (granite walls, a mined-out cave with a thick rock roof, a mouth on its south side) on
    // levelled open ground.
    //   T1 an upper floor over the cave (designator, UI pointer, floor 2 viewed): "Can't build under a thick rock roof".
    //   T2 stairs inside the cave, and stairs outside whose landing would be over the mountain: refused, same message.
    //   T3 stairs along the mountain's south face under open sky: accepted, built by colonists (real ticks); every
    //      thick roof still there.
    //   T4 the guard: a floor's roof written over a thick-roofed cell, and a slab put straight over the cave, leave the
    //      rock roof.
    //   T5 infestations: the game's infestation cell finder still picks a thick-roofed cell, and a real infestation
    //      spawns its tunnel there.
    public static partial class TestRunner
    {
        private static IntVec3 tO, tStairsOk;
        private static CellRect tMount, tCave;
        private static int tThick;

        private static IntVec3 T(int dx, int dz) => new IntVec3(tO.x + dx, 0, tO.z + dz);

        private static int ThickCount(Map map) => map.AllCells.Count(c => map.roofGrid.RoofAt(c)?.isThickRoof ?? false);

        private static bool StepThick(Map map)
        {
            switch (stage)
            {
                case "t_setup":
                    SetupThick(map);
                    return true;
                case "t_view2":
                    ShotAt(map, 1, tMount.CenterCell, 10f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(10, "t_slab");
                    return true;
                case "t_slab":
                {
                    string msg = "PZ_ThickRoof".Translate();
                    Designator_Build d = new Designator_Build(PZDefOf.PZ_UpperFloor);
                    Find.DesignatorManager.Select(d);
                    AcceptanceReport r = RefusalAt(d, ScreenOf(tCave.CenterCell, 1), out IntVec3 c);
                    Find.DesignatorManager.Deselect();
                    Result("T1 an upper floor over the cave is refused: thick rock roof", ProjectZ.FloorOf(c, map) == 1 && !r.Accepted && r.Reason == msg, $"{c}: '{r.Reason}'");
                    FloorView.Set(0);
                    Wait(10, "t_stairs");
                    return true;
                }
                case "t_stairs":
                {
                    string msg = "PZ_ThickRoof".Translate();
                    // inside the cave, up north
                    AcceptanceReport inCave = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, new IntVec3(tCave.minX + 3, 0, tCave.minZ + 2), Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
                    Designator_Build d = new Designator_Build(PZDefOf.PZ_Stairs);
                    d.SetStuffDef(ThingDefOf.WoodLog);
                    Find.DesignatorManager.Select(d);
                    SetRot(d, Rot4.North);
                    AcceptanceReport ui = RefusalAt(d, ScreenOf(new IntVec3(tCave.minX + 3, 0, tCave.minZ + 2), 0), out IntVec3 cu);
                    // outside, south of the mountain, up north: the landing would be over the mountain's rock
                    AcceptanceReport landing = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, new IntVec3(tMount.minX + 10, 0, tMount.minZ - 2), Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
                    // outside along the south face, east: open sky all the way
                    SetRot(d, Rot4.East);
                    AcceptanceReport ok = RefusalAt(d, ScreenOf(tStairsOk, 0), out IntVec3 co);
                    Result("T2 stairs in the cave refused (thick rock roof), through the designator too", !inCave.Accepted && inCave.Reason == msg && ProjectZ.FloorOf(cu, map) == 0 && !ui.Accepted && ui.Reason == msg,
                        $"placement '{inCave.Reason}'; designator at {cu} '{ui.Reason}'");
                    Result("T2 stairs whose landing would be over the mountain refused", !landing.Accepted && landing.Reason == msg, $"'{landing.Reason}' (landing {StairShape.Exit(new IntVec3(tMount.minX + 10, 0, tMount.minZ - 2), Rot4.North, map)})");
                    Result("T3 stairs along the mountain's south face under open sky: allowed", ok.Accepted && co == tStairsOk, $"{co}: {(ok.Accepted ? "accepted" : ok.Reason)}");
                    if (ok.Accepted)
                    {
                        UiDesignate(ScreenOf(tStairsOk, 0), ScreenOf(tStairsOk, 0));
                    }
                    Find.DesignatorManager.Deselect();
                    tThick = ThickCount(map);
                    SetWork(map, true);
                    foreach (Pawn p in Colonists(map))
                    {
                        if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                        {
                            p.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2);
                        }
                    }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(30000, () => map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).Any(t => t.Position == tStairsOk), "t_built", 120);
                    return true;
                }
                case "t_built":
                {
                    Thing st = map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).FirstOrDefault(t => t.Position == tStairsOk);
                    Result("T3 colonists built the stairs at the mountain's edge; every thick roof still there", st != null && ThickCount(map) == tThick,
                        $"{st} at {st?.Position}, landing {(st == null ? "-" : StairShape.Exit(st.Position, st.Rotation, map).ToString())}; thick roofs {tThick} -> {ThickCount(map)}");
                    SetWork(map, false);
                    // T4 the guard
                    IntVec3 cave = new IntVec3(tCave.minX + 1, 0, tCave.minZ + 5);
                    map.roofGrid.SetRoof(cave, PZDefOf.PZ_FloorRoof);
                    bool keptA = map.roofGrid.RoofAt(cave) == RoofDefOf.RoofRockThick;
                    IntVec3 cave2 = new IntVec3(tCave.minX + 5, 0, tCave.minZ + 5);
                    map.terrainGrid.SetTerrain(UpS(cave2, map), PZDefOf.PZ_UpperFloor);
                    bool keptB = map.roofGrid.RoofAt(cave2) == RoofDefOf.RoofRockThick;
                    map.roofGrid.SetRoof(new IntVec3(tCave.minX + 6, 0, tCave.minZ + 6), PZDefOf.PZ_StairwellRoof);
                    bool keptC = map.roofGrid.RoofAt(new IntVec3(tCave.minX + 6, 0, tCave.minZ + 6)) == RoofDefOf.RoofRockThick;
                    map.terrainGrid.SetTerrain(UpS(cave2, map), PZDefOf.PZ_OpenAir);
                    Result("T4 no path writes a floor's or stairwell's roof over a thick roof", keptA && keptB && keptC && map.roofGrid.RoofAt(cave2) == RoofDefOf.RoofRockThick,
                        $"floor roof written: {map.roofGrid.RoofAt(cave)?.defName}; slab put over: {keptB}; stairwell roof written: {map.roofGrid.RoofAt(new IntVec3(tCave.minX + 6, 0, tCave.minZ + 6))?.defName}; after the slab went: {map.roofGrid.RoofAt(cave2)?.defName}");
                    // T5 infestations
                    int under = 0, n = 0;
                    for (int i = 0; i < 20; i++)
                    {
                        if (InfestationCellFinder.TryFindCell(out IntVec3 c, map))
                        {
                            n++;
                            under += map.roofGrid.RoofAt(c)?.isThickRoof ?? false ? 1 : 0;
                        }
                    }
                    IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
                    parms.points = 300f;
                    parms.forced = true;
                    bool fired = IncidentDefOf.Infestation.Worker.TryExecute(parms);
                    List<Thing> tunnels = map.listerThings.AllThings.Where(t => t is TunnelHiveSpawner).ToList();
                    Result("T5 infestation cells are under thick rock; a real infestation tunnels in there", n > 0 && under == n && fired && tunnels.Count > 0 && tunnels.All(t => map.roofGrid.RoofAt(t.Position)?.isThickRoof ?? false),
                        $"finder {under}/{n} under thick rock; incident fired {fired}, tunnels {string.Join(" ", tunnels.Select(t => t.Position + " " + map.roofGrid.RoofAt(t.Position)?.defName))}");
                    ShotAt(map, 0, tMount.CenterCell, 10f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(20, "t_shot");
                    return true;
                }
                case "t_shot":
                    Capture("thick_mountain_stairs");
                    Wait(3, "t_end");
                    return true;
                case "t_end":
                    Finish();
                    return true;
            }
            return false;
        }

        private static void SetupThick(Map map)
        {
            List<Pawn> cols = Colonists(map);
            int gz = ProjectZ.GroundZ(map);
            CellRect area = new CellRect(Mathf.Max(4, map.Size.x / 2 - 20), Mathf.Max(4, gz / 2 - 20), 40, 40);
            TerrainDef soil = DefDatabase<TerrainDef>.GetNamed("Soil");
            // (roofs off first: rock mined under a roof would queue that roof to collapse, onto the scene built here)
            foreach (IntVec3 c in area.ExpandedBy(2))
            {
                map.roofGrid.SetRoof(c, null);
            }
            foreach (IntVec3 c in area.ExpandedBy(2))
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable && !(t is Pawn))
                    {
                        t.Destroy();
                    }
                }
                if (c.GetTerrain(map).IsWater || c.GetTerrain(map).passability != Traversability.Standable || !c.GetTerrain(map).affordances.Any(a => a.defName == "Heavy"))
                {
                    map.terrainGrid.SetTerrain(c, soil);
                }
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
            }
            map.roofCollapseBuffer.Clear();
            DebugSettings.enableRandomMentalStates = false;
            Find.PlaySettings.autoRebuild = false;
            tO = new IntVec3(area.minX + 2, 0, area.minZ + 8);
            // Mountain M (36 x 30): solid granite with a closed 8 x 8 cave in the middle (deep under the rock, as
            // infestations want), a thick rock roof over all; a colony wall in the cave (infestations come near the colony).
            tMount = new CellRect(tO.x, tO.z, 36, 30);
            tCave = new CellRect(tMount.CenterCell.x - 4, tMount.CenterCell.z - 4, 8, 8);
            ThingDef granite = DefDatabase<ThingDef>.GetNamed("Granite");
            foreach (IntVec3 c in tMount)
            {
                if (!tCave.Contains(c))
                {
                    GenSpawn.Spawn(granite, c, map);
                }
                map.roofGrid.SetRoof(c, RoofDefOf.RoofRockThick);
            }
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, new IntVec3(tCave.maxX, 0, tCave.maxZ), map);
            tStairsOk = new IntVec3(tMount.minX + 2, 0, tMount.minZ - 2);
            for (int i = 0; i < 2; i++)
            {
                Thing w = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                w.stackCount = 75;
                GenPlace.TryPlaceThing(w, new IntVec3(tO.x + 20, 0, tO.z - 5), map, ThingPlaceMode.Near);
            }
            SetWork(map, false);
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], new IntVec3(tO.x + 18 + i, 0, tO.z - 6));
            }
            Info($"thick: mountain {tMount} (closed cave {tCave}), stairs at the edge {tStairsOk} east (landing {StairShape.Exit(tStairsOk, Rot4.East, map)}); thick roofs {ThickCount(map)}");
            AnnexWatch.Reset();
            Wait(5, "t_view2");
        }
    }
}
