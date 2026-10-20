using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using ProjectZ.Patches;

namespace ProjectZ.Dev
{
    // Mode drops (drop pods and skyfallers with upper floors; ExclusionPatches DropRules). Run with -projectzmapsize=120.
    // Scene: an open platform P on floor 2 (slab over a roofed, columned ground area, stairs up); house H (ground room
    // under a slab, a floor-2 room R2 with walls and a normal thin roof, stairs inside); ground room G with a thin roof.
    //   A1 the game's drop finders (sampled): raid drop centres, drop spots near a centre under the slab, random drop
    //      spots, trade drop spots (an orbital trade beacon on the platform), safe landing spots, the transport pods'
    //      landing check: never off the floors or under a slab; upper floors used; thin roofs still punched.
    //   A2 a centre-drop raid with every colonist in R2: pods land inside R2 (its thin roof punched), the slab stays.
    //   A3 a centre-drop raid with every colonist in H's ground room (under the slab): no pod lands under a slab.
    //   A4 a scattered (random) drop raid: pods land on the platform too; the raiders who landed upstairs come down
    //      by the stairs (no jumps since 2026-10-19) while assaulting, then all retreat off the map (exit lord); none stuck.
    public static partial class TestRunner
    {
        private static IntVec3 aO, aStairsP, aStairsH;
        private static CellRect aPlat, aHouse, aRoomG;
        private static Faction aFaction;
        private static readonly HashSet<string> aUpIds = new HashSet<string>();
        private static readonly HashSet<string> aDownIds = new HashSet<string>();
        private static int aSlabCells, aKeptRoofs;

        private static IntVec3 A(int dx, int dz) => new IntVec3(aO.x + dx, 0, aO.z + dz);

        // R2: H's inside on floor 2.
        private static bool InR2(IntVec3 c, Map map) => ProjectZ.FloorOf(c, map) == 1 && aHouse.ContractedBy(1).Contains(ProjectZ.GroundCellOf(c, map));

        private static string ACell(IntVec3 c, Map map) => $"{c} floor {ProjectZ.FloorOf(c, map) + 1} roof {map.roofGrid.RoofAt(c)?.defName ?? "none"}";

        private static int SlabCount(Map map) => map.AllCells.Count(c => ProjectZ.HasFloor(c, map));

        private static int KeptCount(Map map) => map.AllCells.Count(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_FloorRoof);

        private static List<DropPodIncoming> Pods(Map map) => map.listerThings.AllThings.OfType<DropPodIncoming>().ToList();

        private static bool StepDrops(Map map)
        {
            switch (stage)
            {
                case "a_setup":
                    SetupDrops(map);
                    return true;

                // ---- A1: the finders
                case "a_finders":
                {
                    int under = 0, r2 = 0, g = 0, up = 0, n = 300;
                    for (int i = 0; i < n; i++)
                    {
                        DropCellFinder.TryFindRaidDropCenterClose(out IntVec3 c, map);
                        under += FloorRoof.Kept(c, map) ? 1 : 0;
                        r2 += InR2(c, map) ? 1 : 0;
                        g += aRoomG.Contains(c) ? 1 : 0;
                        up += ProjectZ.FloorOf(c, map) >= 1 ? 1 : 0;
                    }
                    Result("raid drop centres (close): never under a slab; inside R2 (thin roof, floor 2) and inside ground room G (thin roof) both used",
                        under == 0 && r2 > 0 && g > 0, $"{n} picks: under a slab {under}, R2 {r2}, G {g}, any floor 2 {up}");
                    IntVec3 slabRoom = A(48, 3);
                    int near = 0, nearUnder = 0, nearFail = 0;
                    for (int i = 0; i < 200; i++)
                    {
                        if (DropCellFinder.TryFindDropSpotNear(slabRoom, map, out IntVec3 c, allowFogged: false, canRoofPunch: true))
                        {
                            near++;
                            nearUnder += FloorRoof.Kept(c, map) || ProjectZ.FloorOf(c, map) < 0 ? 1 : 0;
                        }
                        else
                        {
                            nearFail++;
                        }
                    }
                    Result("drop spots near a centre in the room under the slab: none under a slab", nearUnder == 0 && near > 0, $"{near} found ({nearFail} none), under a slab {nearUnder}; centre {ACell(slabRoom, map)}");
                    int[] perFloor = new int[2];
                    int bad = 0, roofed = 0, plat = 0;
                    for (int i = 0; i < 2000; i++)
                    {
                        IntVec3 c = DropCellFinder.RandomDropSpot(map);
                        int k = ProjectZ.FloorOf(c, map);
                        if (k < 0 || DropRules.Blocked(c, map) || !c.Standable(map))
                        {
                            bad++;
                            continue;
                        }
                        perFloor[k]++;
                        roofed += c.Roofed(map) ? 1 : 0;
                        plat += k == 1 && aPlat.Contains(ProjectZ.GroundCellOf(c, map)) ? 1 : 0;
                    }
                    Result("random drop spots: on the ground and on floor 2 (the platform), never off the floors or roofed", bad == 0 && roofed == 0 && perFloor[1] > 0 && plat > 0,
                        $"2000 spots: ground {perFloor[0]}, floor 2 {perFloor[1]} (platform {plat}), roofed {roofed}, bad {bad}");
                    int trUp = 0, trBad = 0;
                    List<string> trade = new List<string>();
                    for (int i = 0; i < 40; i++)
                    {
                        IntVec3 c = DropCellFinder.TradeDropSpot(map);
                        trUp += ProjectZ.FloorOf(c, map) == 1 ? 1 : 0;
                        trBad += DropRules.Blocked(c, map) || c.Roofed(map) ? 1 : 0;
                        if (i < 4)
                        {
                            trade.Add(c.ToString());
                        }
                    }
                    Result("trade drop spots go next to the orbital trade beacon on the platform (floor 2)", trUp == 40 && trBad == 0, $"floor 2 {trUp}/40, blocked or roofed {trBad}; e.g. {string.Join(" ", trade)}; beacon {UpS(A(20, 15), map)}");
                    int safeUp = 0, safeBad = 0;
                    for (int i = 0; i < 30; i++)
                    {
                        IntVec3 c = DropCellFinder.TryFindSafeLandingSpotCloseToColony(map, new IntVec2(3, 3));
                        safeUp += ProjectZ.FloorOf(c, map) == 1 ? 1 : 0;
                        safeBad += GenAdj.OccupiedRect(c, Rot4.North, new IntVec2(3, 3)).Cells.Any(x => DropRules.Blocked(x, map)) ? 1 : 0;
                    }
                    Result("safe landing spots near the colony (3x3 skyfallers, shuttles): never blocked", safeBad == 0, $"30 spots: floor 2 {safeUp}, blocked {safeBad}");
                    IntVec3 under1 = A(48, 3), platC = UpS(A(10, 10), map), r2C = UpS(A(48, 5), map), air = UpS(A(30, 40), map);
                    bool[] tp = { DropCellFinder.IsGoodDropSpot(under1, map, false, true), DropCellFinder.IsGoodDropSpot(platC, map, false, true), DropCellFinder.IsGoodDropSpot(r2C, map, false, true), DropCellFinder.IsGoodDropSpot(air, map, false, true) };
                    Result("transport pods' landing check (IsGoodDropSpot, roof punch): under a slab no, platform yes, R2 (thin roof) yes, open air no",
                        !tp[0] && tp[1] && tp[2] && !tp[3], $"{ACell(under1, map)} {tp[0]}; {ACell(platC, map)} {tp[1]}; {ACell(r2C, map)} {tp[2]}; {ACell(air, map)} {tp[3]}");
                    Result("skyfaller landing (3x3) refused over a slab-covered room, allowed on the platform",
                        !DropCellFinder.SkyfallerCanLandAt(A(48, 4), map, new IntVec2(3, 3)) && DropCellFinder.SkyfallerCanLandAt(UpS(A(8, 20), map), map, new IntVec2(3, 3)), "");
                    stage = "a_center1";
                    return true;
                }

                // ---- A2: centre drop with everyone in R2
                case "a_center1":
                {
                    List<Pawn> cols = Colonists(map);
                    for (int i = 0; i < cols.Count; i++)
                    {
                        Teleport(cols[i], UpS(A(46 + i * 2, 6), map));
                        Draft(cols[i], true);
                    }
                    aSlabCells = SlabCount(map);
                    aKeptRoofs = KeptCount(map);
                    List<DropPodIncoming> pods = FireDropRaid(map, PawnsArrivalModeDefOf.CenterDrop, 300f);
                    int inR2 = pods.Count(p => InR2(p.Position, map)), under = pods.Count(p => FloorRoof.Kept(p.Position, map));
                    Result("centre drop, everyone in R2: pods come down inside R2 (thin roof), none under a slab", pods.Count > 0 && inR2 > 0 && under == 0,
                        $"{pods.Count} pods: R2 {inR2}, under a slab {under}; {string.Join("; ", pods.Take(6).Select(p => ACell(p.Position, map)))}");
                    oPodCells = pods.Select(p => p.Position).ToList();
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    StartTicks(400, "a_center1_landed");
                    return true;
                }
                case "a_center1_landed":
                {
                    int punched = oPodCells.Count(c => InR2(c, map) && !c.Roofed(map));
                    Result("R2's thin roof punched where pods landed; every slab and its roof still there", punched > 0 && SlabCount(map) == aSlabCells && KeptCount(map) == aKeptRoofs,
                        $"R2 pod cells now unroofed {punched}; slabs {aSlabCells} -> {SlabCount(map)}, floor roofs {aKeptRoofs} -> {KeptCount(map)}");
                    ShotAt(map, 1, aHouse.CenterCell, 10f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(20, "a_center1_shot");
                    return true;
                }
                case "a_center1_shot":
                    Capture("drops_center_r2");
                    Wait(3, "a_wallpods");
                    return true;
                // pods into R2 right next to its walls and corners (the punch also drops the roof in the ring around a pod)
                case "a_wallpods":
                {
                    ClearRaiders(map);
                    aSlabCells = SlabCount(map);
                    aKeptRoofs = KeptCount(map);
                    oPodCells = new List<IntVec3> { UpS(A(43, 1), map), UpS(A(48, 1), map), UpS(A(53, 8), map), UpS(A(50, 8), map), UpS(A(44, 5), map), UpS(A(45, 3), map), UpS(A(43, 6), map) };
                    foreach (IntVec3 c in oPodCells)
                    {
                        ActiveTransporterInfo info = new ActiveTransporterInfo();
                        Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                        steel.stackCount = 10;
                        info.innerContainer.TryAdd(steel);
                        DropPodUtility.MakeDropPodAt(c, map, info);
                    }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(1500, () => !map.listerThings.AllThings.Any(t => t is DropPodIncoming), "a_wallpods_landed", 30);
                    return true;
                }
                case "a_wallpods_landed":
                    StartTicks(200, "a_wallpods_check");
                    return true;
                case "a_wallpods_check":
                {
                    Result("supply pods next to R2's walls: thin roof punched, every slab, its roof and the walls still there",
                        SlabCount(map) == aSlabCells && KeptCount(map) == aKeptRoofs && aHouse.EdgeCells.All(g => UpS(g, map).GetEdifice(map) != null),
                        $"pod cells {string.Join(" ", oPodCells.Select(c => ACell(c, map)))}; slabs {aSlabCells} -> {SlabCount(map)}, floor roofs {aKeptRoofs} -> {KeptCount(map)}, upstairs wall cells missing {aHouse.EdgeCells.Count(g => UpS(g, map).GetEdifice(map) == null)}");
                    Wait(3, "a_center2");
                    return true;
                }

                // ---- A3: centre drop with everyone under the slab
                case "a_center2":
                {
                    ClearRaiders(map);
                    List<Pawn> cols = Colonists(map);
                    for (int i = 0; i < cols.Count; i++)
                    {
                        Teleport(cols[i], A(46 + i * 2, 4));
                        Draft(cols[i], true);
                    }
                    aSlabCells = SlabCount(map);
                    aKeptRoofs = KeptCount(map);
                    List<DropPodIncoming> pods = FireDropRaid(map, PawnsArrivalModeDefOf.CenterDrop, 300f);
                    int under = pods.Count(p => FloorRoof.Kept(p.Position, map) || DropRules.Blocked(p.Position, map));
                    Result("centre drop, everyone in the room under the slab: no pod under a slab", pods.Count > 0 && under == 0,
                        $"{pods.Count} pods: under a slab {under}; {string.Join("; ", pods.Take(6).Select(p => ACell(p.Position, map)))}");
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    StartTicks(400, "a_center2_landed");
                    return true;
                }
                case "a_center2_landed":
                    Result("slabs and their roofs intact after the second drop", SlabCount(map) == aSlabCells && KeptCount(map) == aKeptRoofs, $"slabs {aSlabCells} -> {SlabCount(map)}, floor roofs {aKeptRoofs} -> {KeptCount(map)}");
                    stage = "a_scatter";
                    return true;

                // ---- A4: scattered drop raid
                case "a_scatter":
                {
                    ClearRaiders(map);
                    List<Pawn> cols = Colonists(map);
                    for (int i = 0; i < cols.Count; i++)
                    {
                        Teleport(cols[i], A(60 + i * 2, 20));
                        Draft(cols[i], false);
                    }
                    aUpIds.Clear();
                    aDownIds.Clear();
                    aBefore.Clear();
                    aBefore.UnionWith(map.mapPawns.AllPawnsSpawned.Where(p => p.Faction != null && p.Faction.HostileTo(Faction.OfPlayer)).Select(p => p.ThingID));
                    // (a pod lands on the platform about one time in nine here: one more raid of the same faction until a
                    // few have, at most four)
                    List<DropPodIncoming> pods = new List<DropPodIncoming>();
                    Faction first = null;
                    for (int r = 0; r < 4 && pods.Count(p => ProjectZ.FloorOf(p.Position, map) == 1) < 3; r++)
                    {
                        pods.AddRange(FireDropRaid(map, PawnsArrivalModeDefOf.RandomDrop, 2500f, first));
                        first = first ?? aFaction;
                    }
                    int up = pods.Count(p => ProjectZ.FloorOf(p.Position, map) == 1), bad = pods.Count(p => DropRules.Blocked(p.Position, map));
                    Result("scattered drop: some pods on floor 2, none off the floors or under a slab", up > 0 && bad == 0,
                        $"{pods.Count} pods: floor 2 {up} ({string.Join("; ", pods.Where(p => ProjectZ.FloorOf(p.Position, map) == 1).Select(p => ACell(p.Position, map)))}), blocked {bad}");
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(3000, () => !map.listerThings.AllThings.Any(t => t is DropPodIncoming || t.def == ThingDefOf.ActiveDropPod), "a_scatter_landed", 30);
                    return true;
                }
                case "a_scatter_landed":
                {
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(p => AScatter(p)))
                    {
                        if (ProjectZ.FloorOf(p.Position, map) == 1)
                        {
                            aUpIds.Add(p.ThingID);
                        }
                    }
                    Info($"raiders landed: {map.mapPawns.AllPawnsSpawned.Count(p => AScatter(p))}, on floor 2 {aUpIds.Count}");
                    ShotAt(map, 1, aPlat.CenterCell, 14f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(20, "a_scatter_shot");
                    return true;
                }
                case "a_scatter_shot":
                    Capture("drops_scatter_platform");
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(6000, () =>
                    {
                        foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                        {
                            if (aUpIds.Contains(p.ThingID) && ProjectZ.FloorOf(p.Position, map) == 0)
                            {
                                aDownIds.Add(p.ThingID);
                            }
                        }
                        return aUpIds.Count > 0 && aUpIds.All(id => aDownIds.Contains(id) || Gone(map, id));
                    }, "a_assault", 30);
                    return true;
                case "a_assault":
                {
                    int gone = aUpIds.Count(id => Gone(map, id));
                    Result("raiders who landed on floor 2 come down to the ground while assaulting (by the stairs: no jumps)", aUpIds.Count > 0 && aDownIds.Count + gone == aUpIds.Count && aDownIds.Count > 0,
                        $"{aUpIds.Count} landed upstairs: reached the ground {aDownIds.Count}, dead/downed/gone {gone}; {Counts()}; still upstairs {string.Join(", ", aUpIds.Where(id => !aDownIds.Contains(id) && !Gone(map, id)).Select(id => AWhere(map, id)))}");
                    // retreat: each raid lord goes to its own exit toil (what its give-up and losses triggers do)
                    List<Pawn> raiders = map.mapPawns.AllPawnsSpawned.Where(p => AScatter(p) && !p.Downed && !p.Dead).ToList();
                    int toExit = 0;
                    // (doors held open: a raider shut in a room by a player door is the game's own business, not the floors')
                    foreach (Building_Door door in map.listerBuildings.allBuildingsColonist.OfType<Building_Door>())
                    {
                        HarmonyLib.Traverse.Create(door).Field("holdOpenInt").SetValue(true);
                    }
                    foreach (Lord lord in raiders.Select(p => p.GetLord()).Where(l => l != null).Distinct().ToList())
                    {
                        LordToil exit = lord.Graph.lordToils.FirstOrDefault(t => t is LordToil_ExitMap);
                        if (exit != null && lord.CurLordToil != exit)
                        {
                            lord.GotoToil(exit);
                            toExit++;
                        }
                    }
                    oPodCells = raiders.Select(p => p.Position).ToList();
                    Info($"{toExit} raid lord(s) to their exit toil, {raiders.Count} raiders, upstairs now {raiders.Count(p => ProjectZ.FloorOf(p.Position, map) == 1)}");
                    TickUntil(20000, () => !map.mapPawns.AllPawnsSpawned.Any(p => AScatter(p) && !p.Downed && !p.Dead), "a_exit", 120);
                    return true;
                }
                case "a_exit":
                {
                    List<Pawn> left = map.mapPawns.AllPawnsSpawned.Where(p => AScatter(p) && !p.Downed && !p.Dead).ToList();
                    Result("all standing raiders retreat off the map, none stuck", left.Count == 0, $"left {left.Count}: {string.Join(", ", left.Select(p => $"{p} at {ACell(p.Position, map)} job {p.CurJobDef?.defName}"))}");
                    Finish();
                    return true;
                }
            }
            return false;
        }

        private static List<IntVec3> oPodCells = new List<IntVec3>();
        private static readonly HashSet<string> aBefore = new HashSet<string>();

        // A pawn of the scattered raid(s): hostile and not on the map before them.
        private static bool AScatter(Pawn p) => p.Faction != null && p.Faction.HostileTo(Faction.OfPlayer) && !aBefore.Contains(p.ThingID);

        private static bool Gone(Map map, string id)
        {
            Pawn p = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.ThingID == id);
            return p == null || p.Dead || p.Downed;
        }

        private static string AWhere(Map map, string id)
        {
            Pawn p = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.ThingID == id);
            return p == null ? id + " gone" : $"{p} {ACell(p.Position, map)} {p.CurJobDef?.defName}";
        }

        private static void ClearRaiders(Map map)
        {
            // (pods first: a pod destroyed with someone inside lets them out)
            foreach (Thing t in map.listerThings.AllThings.Where(t => t is DropPodIncoming || t.def == ThingDefOf.ActiveDropPod || t is Corpse).ToList())
            {
                t.Destroy();
            }
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(p => p.Faction != null && p.Faction.HostileTo(Faction.OfPlayer)).ToList())
            {
                p.Destroy();
            }
            foreach (Thing t in map.listerThings.AllThings.Where(t => t is DropPodIncoming || t.def == ThingDefOf.ActiveDropPod || t is Corpse).ToList())
            {
                t.Destroy();
            }
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(p => p.Faction != null && p.Faction.HostileTo(Faction.OfPlayer)).ToList())
            {
                p.Destroy();
            }
            foreach (Pawn c in Colonists(map))
            {
                Heal(c);
            }
        }

        // A raid of a pod-using faction with this arrival (another faction is tried if one fails to form); the incoming
        // pods right after it fired.
        private static List<DropPodIncoming> FireDropRaid(Map map, PawnsArrivalModeDef arrive, float points, Faction only = null)
        {
            List<DropPodIncoming> before = Pods(map);
            List<Faction> enemies = Find.FactionManager.AllFactions.Where(f => f.HostileTo(Faction.OfPlayer) && !f.def.hidden && f.def.humanlikeFaction && !f.temporary && f.def.pawnGroupMakers != null && f.def.techLevel >= TechLevel.Industrial).InRandomOrder().ToList();
            bool fired = false;
            foreach (Faction f in only != null ? new List<Faction> { only } : enemies)
            {
                IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
                parms.faction = f;
                parms.raidStrategy = RaidStrategyDefOf.ImmediateAttack;
                parms.raidArrivalMode = arrive;
                parms.points = points;
                parms.forced = true;
                if (IncidentDefOf.RaidEnemy.Worker.TryExecute(parms))
                {
                    aFaction = f;
                    fired = true;
                    break;
                }
            }
            List<DropPodIncoming> pods = Pods(map).Except(before).ToList();
            Info($"{arrive.defName} raid ({aFaction?.def.defName}, {points} points) fired {fired}: {pods.Count} pods");
            return pods;
        }

        private static void SetupDrops(Map map)
        {
            FQuiet(map, true);   // (only the raids fired here: no incidents, wild animals or visitors)
            List<Pawn> cols = Colonists(map);
            // The middle of the map, levelled: rock, water, ruins and natural roofs cleared (the scene, not under test).
            int gz = ProjectZ.GroundZ(map);
            CellRect found = new CellRect(Mathf.Max(4, map.Size.x / 2 - 34), Mathf.Max(4, gz / 2 - 23), 68, 46);
            if (!found.ExpandedBy(2).InBounds(map) || found.maxZ >= gz - 6)
            {
                Result("test spot", false, "map too small (run with -projectzmapsize=120)");
                Finish();
                return;
            }
            TerrainDef soil = DefDatabase<TerrainDef>.GetNamed("Soil");
            // (roofs off first: rock mined under a roof would queue that roof to collapse, onto the scene built here)
            foreach (IntVec3 c in found.ExpandedBy(2))
            {
                map.roofGrid.SetRoof(c, null);
            }
            foreach (IntVec3 c in found.ExpandedBy(2))
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
            aO = new IntVec3(found.minX, 0, found.minZ);
            // Platform P (40 x 30) on floor 2 over a roofed ground area held by columns every 6 cells; stairs up at its south.
            aPlat = new CellRect(aO.x, aO.z, 40, 30);
            aStairsP = A(20, 2);
            HashSet<IntVec3> wellP = new HashSet<IntVec3>(StairShape.Footprint(aStairsP, Rot4.North).Cells);
            foreach (IntVec3 g in aPlat)
            {
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                if ((g.x - aO.x) % 6 == 3 && (g.z - aO.z) % 6 == 3)
                {
                    Place(ThingDefOf.Wall, ThingDefOf.Steel, g, map);
                }
            }
            foreach (IntVec3 g in aPlat)
            {
                if (!wellP.Contains(g))
                {
                    map.terrainGrid.SetTerrain(UpS(g, map), PZDefOf.PZ_UpperFloor);
                }
            }
            Place(PZDefOf.PZ_Stairs, ThingDefOf.Steel, aStairsP, map, Rot4.North);
            Place(ThingDefOf.OrbitalTradeBeacon, null, UpS(A(20, 15), map), map);
            // House H (13 x 10): ground room under a slab; on floor 2 room R2 (walls, a door, a normal thin roof).
            aHouse = new CellRect(aO.x + 42, aO.z, 13, 10);
            IntVec3 door = A(48, 0);
            foreach (IntVec3 c in aHouse.EdgeCells)
            {
                Place(c == door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            foreach (IntVec3 c in aHouse)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                map.areaManager.Home[c] = true;
            }
            aStairsH = A(44, 3);
            HashSet<IntVec3> wellH = new HashSet<IntVec3>(StairShape.Footprint(aStairsH, Rot4.North).Cells);
            foreach (IntVec3 g in aHouse)
            {
                if (!wellH.Contains(g))
                {
                    map.terrainGrid.SetTerrain(UpS(g, map), PZDefOf.PZ_UpperFloor);
                }
            }
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, aStairsH, map, Rot4.North);
            IntVec3 doorUp = UpS(A(54, 5), map);
            foreach (IntVec3 g in aHouse.EdgeCells)
            {
                IntVec3 u = UpS(g, map);
                Place(u == doorUp ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, u, map);
            }
            foreach (IntVec3 g in aHouse)
            {
                if (ProjectZ.HasFloor(UpS(g, map), map))
                {
                    map.roofGrid.SetRoof(UpS(g, map), RoofDefOf.RoofConstructed);
                }
            }
            // Ground room G (9 x 7), plain thin roof.
            aRoomG = new CellRect(aO.x + 42, aO.z + 14, 9, 7);
            IntVec3 doorG = A(46, 14);
            foreach (IntVec3 c in aRoomG.EdgeCells)
            {
                Place(c == doorG ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            foreach (IntVec3 c in aRoomG)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            aRoomG = aRoomG.ContractedBy(1);
            // colonists: one in R2, one in H's ground room (under the slab), one in G
            Teleport(cols[0], UpS(A(48, 5), map));
            Teleport(cols[1], A(48, 4));
            Teleport(cols[2 % cols.Count], A(46, 17));
            foreach (Pawn p in cols)
            {
                Draft(p, true);
            }
            int kept = aPlat.Cells.Count(g => map.roofGrid.RoofAt(g) == PZDefOf.PZ_FloorRoof) + aHouse.Cells.Count(g => map.roofGrid.RoofAt(g) == PZDefOf.PZ_FloorRoof);
            Info($"drops: platform {aPlat} (stairs {aStairsP}), house {aHouse} (stairs {aStairsH}), room G {aRoomG}; floor roofs under slabs {kept}; R2 roof {map.roofGrid.RoofAt(UpS(A(48, 5), map))?.defName}");
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Wait(10, "a_finders");
        }
    }
}
