using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;
using ProjectZ.Patches;

namespace ProjectZ.Dev
{
    // Wall-height comparison shots (mode wallheight; run once per -projectzwallheight=X, -projectzfloors=2): one scene,
    // shot the same way at every wall height. A 11x8 house open to the south (walls north, west with a door, east), a
    // floor-2 slab over all of it but the stairwell, stacked walls north and west on floor 2; under the slab colonists
    // standing among a bookcase, shelf, tall machines, a standing lamp and a bed; ordinary stairs up along the east wall;
    // two colonists on floor 2; east of the house a plain outdoor wall line with a colonist beside it. Shots: wide and
    // close at the default isometric camera, close and outdoor at a low camera (pitch 20), and a first-person view from
    // a colonist under the slab. results.txt lists every building's model height against the slab clearance.
    public static partial class TestRunner
    {
        private static readonly List<KeyValuePair<string, Action<Map>>> whShots = new List<KeyValuePair<string, Action<Map>>>();
        private static int whIndex;
        private static IntVec3 whO;
        private static Pawn whFp;

        private static bool StepWallHeight(Map map)
        {
            switch (stage)
            {
                case "wh_setup":
                    SetupWallHeight(map);
                    return true;
                case "wh_shot":
                    if (whIndex >= whShots.Count)
                    {
                        stage = "wh_end";
                        return true;
                    }
                    whShots[whIndex].Value(map);
                    Wait(150, "wh_take");
                    return true;
                case "wh_take":
                    foreach (Window w in Find.WindowStack.Windows.Where(w => w.layer == WindowLayer.Dialog).ToList())
                    {
                        Find.WindowStack.TryRemove(w, doCloseSound: false);
                    }
                    Messages.Clear();
                    Capture("wh_" + whShots[whIndex].Key);
                    whIndex++;
                    stage = "wh_shot";
                    return true;
                case "wh_end":
                    FpLeave();
                    Finish();
                    return true;
            }
            return false;
        }

        private static IntVec3 W(int x, int z) => new IntVec3(whO.x + x, 0, whO.z + z);

        private static ThingDef Def(string name)
        {
            ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(name);
            if (d == null)
            {
                Info("missing def " + name);
            }
            return d;
        }

        private static void PlaceIf(string name, IntVec3 c, Map map, Rot4 rot, ThingDef stuff = null)
        {
            ThingDef d = Def(name);
            if (d == null)
            {
                return;
            }
            Thing t = Place(d, stuff, c, map, rot);
            CompRefuelable fuel = t.TryGetComp<CompRefuelable>();
            fuel?.Refuel(fuel.Props.fuelCapacity);
            t.TryGetComp<CompGlower>()?.UpdateLit(map);
            float h = IsoRimBridge.ModelHeight(d);
            Info($"placed {name} at {c} rot {rot}: model height {h:0.000}, clearance {HeightRule.Clearance:0.000}, too tall {HeightRule.TooTall(d)}");
        }

        private static void SetupWallHeight(Map map)
        {
            Info($"wall height {IsoRimBridge.WallHeight:0.000}, storey {IsoRimBridge.StoreyHeight:0.000}, clearance {HeightRule.Clearance:0.000}");
            // every building drawn from a model, tallest first (the slab rule's input)
            List<string> rows = new List<string>();
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefs.Where(d => d.category == ThingCategory.Building && !d.IsFrame && !d.IsBlueprint))
            {
                float h = IsoRimBridge.ModelHeight(d);
                if (h > 0.9f)
                {
                    rows.Add($"MODELH {h:0.000}\t{d.defName}\t{d.label}\t{d.size.x}x{d.size.z}\t{d.modContentPack?.PackageId}\tbuildable={(d.BuildableByPlayer ? 1 : 0)}\ttall={(HeightRule.TooTall(d) ? 1 : 0)}");
                }
            }
            foreach (string r in rows.OrderByDescending(r => r))
            {
                results.Add(r);
            }

            List<Pawn> cols = Colonists(map);
            IntVec3 near = map.Center;   // (not the colonists: their start spots vary between runs)
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 79f, true))
            {
                CellRect r = new CellRect(c.x - 12, c.z - 8, 36, 20);
                if (!r.ExpandedBy(6).InBounds(map) || r.maxZ >= ProjectZ.GroundZ(map) - 12)
                {
                    continue;
                }
                if (r.Cells.All(x => !x.GetTerrain(map).IsWater && !map.roofGrid.Roofed(x) && !x.Fogged(map) && !(x.GetEdifice(map)?.def.building?.isNaturalRock ?? false) && x.GetTerrain(map).passability == Traversability.Standable))
                {
                    found = r;
                    break;
                }
            }
            if (found.Area == 0)
            {
                Result("test spot", false, "no open ground near the colonists");
                Finish();
                return;
            }
            foreach (IntVec3 c in found.ExpandedBy(4))
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable && (t is Plant || t.def.category == ThingCategory.Item || t is Filth || t is Building))
                    {
                        t.Destroy();
                    }
                }
                map.snowGrid.SetDepth(c, 0f);
            }
            DebugSettings.enableRandomMentalStates = false;
            whO = new IntVec3(found.minX + 12, 0, found.minZ + 6);
            TerrainDef plank = DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor");
            ThingDef blocks = DefDatabase<ThingDef>.GetNamed("BlocksGranite");
            CellRect house = new CellRect(whO.x, whO.z, 11, 8);
            IntVec3 sp = W(9, 3);
            HashSet<IntVec3> well = new HashSet<IntVec3>(StairShape.Footprint(sp, Rot4.North).Cells);
            foreach (IntVec3 c in house)
            {
                map.terrainGrid.SetTerrain(c, plank);
                if (!well.Contains(c))
                {
                    map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(c, 1, map), PZDefOf.PZ_UpperFloor);
                }
            }
            // ground walls: north, west (door at 3), east; south open
            for (int x = 0; x <= 10; x++)
            {
                Place(ThingDefOf.Wall, blocks, W(x, 7), map);
            }
            for (int z = 0; z <= 6; z++)
            {
                if (z != 3)
                {
                    Place(ThingDefOf.Wall, blocks, W(0, z), map);
                }
                Place(ThingDefOf.Wall, blocks, W(10, z), map);
            }
            Place(ThingDefOf.Door, blocks, W(0, 3), map);
            // floor 2: walls north and west
            for (int x = 0; x <= 10; x++)
            {
                Place(ThingDefOf.Wall, blocks, ProjectZ.CellOnFloor(W(x, 7), 1, map), map);
            }
            for (int z = 0; z <= 6; z++)
            {
                Place(ThingDefOf.Wall, blocks, ProjectZ.CellOnFloor(W(0, z), 1, map), map);
            }
            Place(PZDefOf.PZ_Stairs, blocks, sp, map, Rot4.North);
            // furniture under the slab
            PlaceIf("Bookcase", W(1, 5), map, Rot4.East, ThingDefOf.WoodLog);
            PlaceIf("ToolCabinet", W(3, 6), map, Rot4.South);
            PlaceIf("ElectricSmelter", W(6, 6), map, Rot4.South);
            PlaceIf("StandingLamp", W(8, 6), map, Rot4.North);
            PlaceIf("Bed", W(8, 0), map, Rot4.North, ThingDefOf.WoodLog);
            PlaceIf("Shelf", W(1, 1), map, Rot4.East, ThingDefOf.WoodLog);
            // columns under the slab: drawn as tall as the walls, they meet it (and are not too tall for it)
            PlaceIf("Column", W(4, 4), map, Rot4.North, blocks);
            PlaceIf("Column", W(7, 2), map, Rot4.North, blocks);
            ThingDef column = Def("Column");
            if (column != null)
            {
                Designator_Build dColumn = new Designator_Build(column);
                dColumn.SetStuffDef(blocks);
                AcceptanceReport underSlab = dColumn.CanDesignateCell(W(2, 2));
                Result("a column can be built under a floor", underSlab.Accepted, $"at {W(2, 2)}: '{underSlab.Reason}', model height {IsoRimBridge.ModelHeight(column):0.000}, clearance {HeightRule.Clearance:0.000}");
            }
            // floor 2: a lamp
            PlaceIf("StandingLamp", ProjectZ.CellOnFloor(W(1, 6), 1, map), map, Rot4.North);
            // outdoor wall line east of the house
            for (int x = 14; x <= 21; x++)
            {
                Place(ThingDefOf.Wall, blocks, W(x, 2), map);
            }
            // tall things in the open for reference
            PlaceIf("Column", W(14, 5), map, Rot4.North, blocks);
            PlaceIf("Bookcase", W(17, 5), map, Rot4.South, ThingDefOf.WoodLog);

            // colonists: 2 under the slab, 1 first-person, 2 on floor 2, 1 outside
            Rand.PushState(718);
            while (cols.Count < 6)
            {
                Pawn np = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(np, W(-3 - cols.Count, -6), map);
                cols = Colonists(map);
            }
            Rand.PopState();
            cols = cols.OrderBy(p => p.LabelShort).ToList();
            IntVec3[] spots = { W(3, 3), W(6, 3), W(5, 0), ProjectZ.CellOnFloor(W(3, 2), 1, map), ProjectZ.CellOnFloor(W(6, 3), 1, map), W(17, 1) };
            Rot4[] faces = { Rot4.South, Rot4.West, Rot4.North, Rot4.South, Rot4.South, Rot4.South };
            for (int i = 0; i < cols.Count; i++)
            {
                Pawn p = cols[i];
                Draft(p, true);
                Teleport(p, i < spots.Length ? spots[i] : W(-4 - i, -6));
                p.Rotation = i < faces.Length ? faces[i] : Rot4.South;
                if (p.needs?.food != null) p.needs.food.CurLevel = p.needs.food.MaxLevel;
                if (p.needs?.rest != null) p.needs.rest.CurLevel = p.needs.rest.MaxLevel;
            }
            whFp = cols[2];
            Info($"house {house}, stairs {sp}, colonists: " + string.Join("; ", cols.Select(p => $"{p.LabelShort} {p.Position} floor {ProjectZ.FloorOf(p.Position, map) + 1}")));
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            SetHour(map, 11);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            IsoRimBridge.SetOption("isometric", true);
            IsoRimBridge.SetOption("perspective", false);
            IsoRimBridge.SetOption("freeYaw", 0f);
            IsoRimBridge.SetOption("turn", 0);

            whShots.Clear();
            whShots.Add(new KeyValuePair<string, Action<Map>>("1_wide", m => WhView(m, 35.3f, new Vector3(11f, 1f, 4f), 11f)));
            whShots.Add(new KeyValuePair<string, Action<Map>>("2_house", m => WhView(m, 35.3f, new Vector3(5.5f, 1f, 3.5f), 6.5f)));
            whShots.Add(new KeyValuePair<string, Action<Map>>("3_house_low", m => WhView(m, 20f, new Vector3(5.5f, 1f, 3.5f), 6.5f)));
            whShots.Add(new KeyValuePair<string, Action<Map>>("3b_house_low_front", m => WhView(m, 20f, new Vector3(5.5f, 1f, 3f), 6f, -45f)));
            whShots.Add(new KeyValuePair<string, Action<Map>>("3c_door_low", m => WhView(m, 20f, new Vector3(0f, 0.8f, 3.5f), 4.5f, 45f)));
            whShots.Add(new KeyValuePair<string, Action<Map>>("4_outdoor_low", m => WhView(m, 20f, new Vector3(17.5f, 0.8f, 2.5f), 4.5f)));
            whShots.Add(new KeyValuePair<string, Action<Map>>("5_firstperson", m => FpEnter(m)));
            whIndex = 0;
            Wait(120, "wh_shot");
        }

        private static void WhView(Map map, float pitch, Vector3 at, float size, float yaw = 0f)
        {
            FpLeave();
            IsoRimBridge.SetOption("isoPitch", pitch);
            IsoRimBridge.SetOption("freeYaw", yaw);
            Find.CameraDriver.config.sizeRange.min = 1f;
            FloorView.Set(1);
            IsoRimBridge.SetRoofs(false);
            CameraDriver_Update_Patch.AllowAnnexView = false;
            Find.UIRoot.screenshotMode.Active = true;
            Find.Selector.ClearSelection();
            Vector3 g = IsoRimBridge.DrawnGroundPoint(new Vector3(whO.x + at.x, at.y, whO.z + at.z));
            Find.CameraDriver.SetRootPosAndSize(g, size);
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                p.Drawer?.tweener?.ResetTweenedPosToRoot();
            }
        }

        private static void FpEnter(Map map)
        {
            WhView(map, 35.3f, new Vector3(5.5f, 1f, 3.5f), 7f);
            Find.UIRoot.screenshotMode.Active = true;
            Find.Selector.ClearSelection();
            Find.Selector.Select(whFp, playSound: false, forceDesignatorDeselect: false);
            FpToggle();
            Find.Selector.ClearSelection();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FpToggle()
        {
            IsoRim.FirstPerson.Toggle();
            IsoRim.FirstPerson.Look(15f, -6f);
        }

        private static void FpLeave()
        {
            if (IsoRimBridge.Present)
            {
                FpLeaveInner();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FpLeaveInner()
        {
            if (IsoRim.FirstPerson.On)
            {
                IsoRim.FirstPerson.Leave(null);
            }
        }
    }
}
