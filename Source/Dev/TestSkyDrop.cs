using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode skydrop (IsoRim's 3D skyfallers over upper floors; run with -projectzfloors=2). A 9x9 slab on floor 2 over a
    // roofed ground area in a cleared field. Drop pods are dropped through the game's own DropPodUtility.DropThingsNear
    // (its drop cell finder, with ProjectZ's drop rules), at a cell of the platform on floor 2 and at a ground cell beside
    // it. Each fall is filmed on real ticks (40, 25, 12, 4 ticks before impact, and after landing) with floor 2 viewed
    // and hidden, at yaw 45 (and 225 for the platform). Each pod's landing cell and floor are logged; the pod over the
    // platform must stay over the platform the whole way (drawn over its floor), the ground pod over the ground.
    public static partial class TestRunner
    {
        private sealed class SkyCase
        {
            public string name;
            public bool upper;   // dropped at a platform cell on floor 2
            public int view;     // floor viewed: 0 ground (floor 2 hidden), 1 floor 2
            public float yaw;    // IsoRim freeYaw (0 = 45, 180 = 225)
        }

        private static readonly SkyCase[] skCases =
        {
            new SkyCase { name = "platform_view2_yaw45", upper = true, view = 1, yaw = 0f },
            new SkyCase { name = "platform_view2_yaw225", upper = true, view = 1, yaw = 180f },
            new SkyCase { name = "platform_view1_yaw45", upper = true, view = 0, yaw = 0f },
            new SkyCase { name = "ground_view1_yaw45", upper = false, view = 0, yaw = 0f },
            new SkyCase { name = "ground_view2_yaw45", upper = false, view = 1, yaw = 0f },
        };

        private static readonly int[] skAt = { 40, 25, 12, 4, -10 };   // ticks before impact (-10: 10 ticks after landing)

        private static CellRect skPlat;
        private static int skCase, skShot;
        private static DropPodIncoming skPod;
        private static IntVec3 skPodCell;
        private static float skOldYaw, skOldMin;

        private static bool StepSkyDrop(Map map)
        {
            switch (stage)
            {
                case "sk_setup":
                    SetupSkyDrop(map);
                    return true;
                case "sk_case":
                {
                    if (skCase >= skCases.Length)
                    {
                        IsoRimBridge.SetOption("freeYaw", skOldYaw);
                        Find.CameraDriver.config.sizeRange.min = skOldMin;
                        Finish();
                        return true;
                    }
                    SkyCase c = skCases[skCase];
                    IntVec3 g = c.upper ? skPlat.CenterCell : new IntVec3(skPlat.CenterCell.x, 0, skPlat.maxZ + 8);
                    IntVec3 target = c.upper ? ProjectZ.CellOnFloor(g, 1, map) : g;
                    List<DropPodIncoming> before = map.listerThings.AllThings.OfType<DropPodIncoming>().ToList();
                    Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    steel.stackCount = 30;
                    DropPodUtility.DropThingsNear(target, map, new List<Thing> { steel }, 110, false, false, true, true, true);
                    skPod = map.listerThings.AllThings.OfType<DropPodIncoming>().Except(before).FirstOrDefault();
                    if (skPod == null)
                    {
                        Result($"{c.name}: a drop pod", false, $"none made at {target}");
                        skCase++;
                        Wait(2, "sk_case");
                        return true;
                    }
                    skPodCell = skPod.Position;
                    Info($"{c.name}: DropThingsNear({target}, floor {ProjectZ.FloorOf(target, map) + 1}) -> pod at {skPodCell} floor {ProjectZ.FloorOf(skPodCell, map) + 1}, ground cell {ProjectZ.GroundCellOf(skPodCell, map)}, ticks {skPod.ticksToImpact}");
                    Result($"{c.name}: lands on the floor asked for ({(c.upper ? "floor 2 platform" : "ground")})",
                        c.upper ? ProjectZ.FloorOf(skPodCell, map) == 1 && skPlat.Contains(ProjectZ.GroundCellOf(skPodCell, map)) : ProjectZ.FloorOf(skPodCell, map) == 0,
                        $"pod cell {skPodCell}");
                    skShot = 0;
                    IsoRimBridge.SetOption("freeYaw", c.yaw);
                    NextSkyShot(map);
                    return true;
                }
                case "sk_shoot":
                {
                    tickWatch = null;
                    SkyCase c = skCases[skCase];
                    IntVec3 g = ProjectZ.GroundCellOf(skPodCell, map);
                    Find.CameraDriver.config.sizeRange.min = 2f;
                    ShotAt(map, c.view, g + new IntVec3(-2, 0, 2), 7f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(25, "sk_take");
                    return true;
                }
                case "sk_take":
                {
                    SkyCase c = skCases[skCase];
                    int t = skAt[skShot];
                    bool falling = skPod != null && skPod.Spawned;
                    Info($"{c.name} shot {skShot}: pod {(falling ? "falling, ticks " + skPod.ticksToImpact : "landed")}; active pods at {string.Join(" ", map.listerThings.ThingsOfDef(ThingDefOf.ActiveDropPod).Select(p => p.Position + " floor " + (ProjectZ.FloorOf(p.Position, map) + 1)))}");
                    ZCapture($"sky_{c.name}_{(t >= 0 ? "t" + t : "landed")}");
                    skShot++;
                    Wait(3, "sk_next");
                    return true;
                }
                case "sk_next":
                    NextSkyShot(map);
                    return true;
            }
            return false;
        }

        private static void NextSkyShot(Map map)
        {
            if (skShot >= skAt.Length)
            {
                foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.ActiveDropPod).ToList())
                {
                    t.Destroy();
                }
                foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Steel).ToList())
                {
                    t.Destroy();
                }
                skCase++;
                Wait(2, "sk_case");
                return;
            }
            int at = skAt[skShot];
            DropPodIncoming pod = skPod;
            if (at >= 0)
            {
                TickUntil(400, () => pod == null || !pod.Spawned || pod.ticksToImpact <= at, "sk_shoot", 1);
            }
            else
            {
                StartTicks(-at, "sk_shoot");
            }
        }

        private static void SetupSkyDrop(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || an.floors < 2)
            {
                Result("skydrop setup", false, $"needs 2 floors (-projectzfloors=2); floors {an?.floors}");
                Finish();
                return;
            }
            // a 21x30 open field, the one with the fewest cells to fix
            CellRect found = CellRect.Empty;
            int bestBad = int.MaxValue;
            for (int z = 3; z + 30 < an.groundZ - 3; z += 4)
            {
                for (int x = 3; x + 21 < map.Size.x - 3; x += 4)
                {
                    CellRect r = new CellRect(x, z, 21, 30);
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
            }
            if (found.Area == 0)
            {
                Result("skydrop field", false, "map too small");
                Finish();
                return;
            }
            foreach (IntVec3 c in found.ExpandedBy(1))
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable)
                    {
                        t.Destroy();
                    }
                }
                TerrainDef td = c.GetTerrain(map);
                if (td.IsWater || td.passability == Traversability.Impassable)
                {
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
            }
            DebugSettings.enableRandomMentalStates = false;
            skPlat = new CellRect(found.CenterCell.x - 4, found.minZ + 2, 9, 9);
            foreach (IntVec3 g in skPlat)
            {
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(g, 1, map), PZDefOf.PZ_UpperFloor);
                map.fogGrid.Unfog(ProjectZ.CellOnFloor(g, 1, map));
            }
            // colonists out of the field
            foreach (Pawn p in Colonists(map))
            {
                p.drafter.Drafted = true;
                Teleport(p, CellFinder.RandomClosewalkCellNear(new IntVec3(found.minX > map.Size.x / 2 ? 5 : map.Size.x - 6, 0, 5), map, 4));
            }
            Info($"field {found}, platform {skPlat} (slab on floor 2 over a roofed ground area); storey {IsoRimBridge.StoreyHeight:0.00}");
            skOldYaw = (float)IsoRimBridge.SetOption("freeYaw", 0f);
            skOldMin = Find.CameraDriver.config.sizeRange.min;
            skCase = 0;
            Wait(10, "sk_case");
        }
    }
}
