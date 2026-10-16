using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode power (power across floors, PowerPatches.cs; run with -projectzfloors=3). A house H (13 x 9) with slabs on
    // floors 2 and 3 and walls on the ground and on floor 2. Ground: a wood-fired generator west of H, a conduit line into
    // H through a hidden conduit under the west wall, a stub P1 off the line, and a branch to a hidden conduit W1 under
    // the north wall.
    //   riser P: P2 (floor 2) over P1, P3 (floor 3) over P2; lamps L2, L3 next to them  -> powered over 3 floors
    //   riser W: W2, a hidden conduit under the floor-2 wall over W1, lamp LW             -> powered (conduit under a wall)
    //   battery B (charged, alone) on the ground, conduit Q2 over it on floor 2, lamp LQ  -> NOT joined (only conduits)
    //   edge pair: a lamp on the ground's top row under a conduit on floor 2's bottom row (and the other way round), 4
    //   rows apart over the void; the pairs are 10 cells apart, so neither lamp has a conduit in range on its own floor
    //                                                                                     -> no hookup across floors
    // Then a colonist deconstructs P1 (L2, L3 go dark, LW stays lit, P2 + P3 stay one net), a colonist rebuilds it from
    // a blueprint (lit again), save + reload (same nets). Shots with the power overlay on floors 1-3.
    public static partial class TestRunner
    {
        private static IntVec3 nO, nGen, nP1, nW1, nB, nL2g, nLWg, nQ2g, nLQg, nEdgeLampG, nEdgeConG, nEdgeConU, nEdgeLampU;
        private static CellRect nHouse;
        private static ThingDef nConduit, nHidden, nLamp;
        private static int nShot;
        private static string nPhase;

        private static IntVec3 N(int dx, int dz) => new IntVec3(nO.x + dx, 0, nO.z + dz);

        private static IntVec3 NF(IntVec3 g, int k, Map map) => ProjectZ.CellOnFloor(g, k, map);

        private static Thing NThing(Map map, IntVec3 c, ThingDef def) => c.IsValid ? c.GetThingList(map).FirstOrDefault(t => t.def == def) : null;

        private static CompPowerTrader NLampAt(Map map, IntVec3 c) => NThing(map, c, nLamp)?.TryGetComp<CompPowerTrader>();

        private static PowerNet NNet(Map map, IntVec3 c) => c.IsValid ? map.powerNetGrid.TransmittedPowerNetAt(c) : null;

        private static string NOn(CompPowerTrader l) => l == null ? "missing" : $"{(l.PowerOn ? "ON" : "off")} (hooked to {l.connectParent?.parent.Position.ToString() ?? "nothing"})";

        private static bool StepPower(Map map)
        {
            switch (stage)
            {
                case "n_setup":
                    SetupPower(map);
                    return true;
                case "n_check1":
                    NCheck(map, "built", ground: true);
                    NEdgeCheck(map);
                    nPhase = "1built";
                    nShot = 0;
                    Wait(2, "n_shot");
                    return true;
                case "n_shot":
                {
                    if (nShot >= 3)
                    {
                        Find.DesignatorManager.Deselect();
                        fakeMouse = null;
                        Wait(5, nPhase == "1built" ? "n_decon" : nPhase == "2cut" ? "n_rebuild" : nPhase == "3rebuilt" ? "n_save" : "n_end");
                        return true;
                    }
                    if (nShot == 0)
                    {
                        // the power overlay shows while a power designator is selected; the build ghost is parked in a corner
                        fakeMouse = new Vector2(4f, 4f);
                        Find.DesignatorManager.Select(BuildCopyCommandUtility.FindAllowedDesignator(nConduit) ?? new Designator_Build(nConduit));
                    }
                    ShotAt(map, nShot, nHouse.CenterCell, 9f);
                    Wait(45, "n_shot_take");
                    return true;
                }
                case "n_shot_take":
                    ZCapture($"power_{nPhase}_f{nShot + 1}");
                    nShot++;
                    Wait(2, "n_shot");
                    return true;

                // ---- a colonist deconstructs P1 (the ground conduit under the riser)
                case "n_decon":
                {
                    Thing p1 = NThing(map, nP1, nConduit);
                    Designator_Deconstruct dd = new Designator_Deconstruct();
                    AcceptanceReport ok = p1 == null ? "no P1" : dd.CanDesignateThing(p1);
                    if (ok.Accepted)
                    {
                        dd.DesignateThing(p1);
                    }
                    Info($"deconstruct designated on P1 {p1} at {nP1}: {ok.Accepted} '{ok.Reason}'");
                    SetWork(map, true);
                    TickUntil(20000, () => NThing(map, nP1, nConduit) == null, "n_cut", 20);
                    return true;
                }
                case "n_cut":
                    tickWatch = null;
                    SetWork(map, false);
                    Result("a colonist deconstructs P1", NThing(map, nP1, nConduit) == null, $"after {ticksTotal} ticks");
                    StartTicks(600, "n_cut2");
                    return true;
                case "n_cut2":
                {
                    CompPowerTrader l2 = NLampAt(map, NF(nL2g, 1, map)), l3 = NLampAt(map, NF(nL2g, 2, map)), lw = NLampAt(map, NF(nLWg, 1, map));
                    PowerNet n2 = NNet(map, NF(nP1, 1, map)), n3 = NNet(map, NF(nP1, 2, map)), ng = NNet(map, nGen);
                    Result("P1 removed: the lamps upstairs on riser P lose power", l2 != null && l3 != null && !l2.PowerOn && !l3.PowerOn, $"L2 {NOn(l2)}, L3 {NOn(l3)}");
                    Result("P1 removed: P2 and P3 stay one net, cut from the ground", n2 != null && n2 == n3 && n2 != ng, $"P2 net {n2?.GetHashCode()}, P3 net {n3?.GetHashCode()}, ground net {ng?.GetHashCode()}; P2 net transmitters {n2?.transmitters.Count}");
                    Result("P1 removed: riser W (hidden conduits under walls) still powers LW", lw != null && lw.PowerOn && NNet(map, NF(nW1, 1, map)) == ng, $"LW {NOn(lw)}");
                    nPhase = "2cut";
                    nShot = 0;
                    Wait(2, "n_shot");
                    return true;
                }

                // ---- a colonist builds P1 again from a blueprint
                case "n_rebuild":
                {
                    Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    steel.stackCount = 20;
                    GenSpawn.Spawn(steel, N(6, -2), map);
                    GenConstruct.PlaceBlueprintForBuild(nConduit, nP1, map, Rot4.North, Faction.OfPlayer, null);
                    SetWork(map, true);
                    TickUntil(20000, () => NThing(map, nP1, nConduit) != null, "n_rebuilt", 20);
                    return true;
                }
                case "n_rebuilt":
                    tickWatch = null;
                    SetWork(map, false);
                    Result("a colonist builds P1 again", NThing(map, nP1, nConduit) != null, $"after {ticksTotal} ticks");
                    StartTicks(600, "n_rebuilt2");
                    return true;
                case "n_rebuilt2":
                    NCheck(map, "P1 rebuilt", ground: false);
                    nPhase = "3rebuilt";
                    nShot = 0;
                    Wait(2, "n_shot");
                    return true;

                // ---- save + reload
                case "n_save":
                    savedTicks = Find.TickManager.TicksGame;
                    GameDataSaveLoader.SaveGame("pz_power");
                    Result("saved pz_power", File.Exists(GenFilePaths.FilePathForSavedGame("pz_power")), "");
                    stage = "n_loaded";
                    pendingLoad = "pz_power";
                    return true;
                case "n_loaded":
                    Info($"reloaded pz_power at tick {ticksAfterLoad} (saved {savedTicks})");
                    NResolve(map);
                    StartTicks(600, "n_loaded2");
                    return true;
                case "n_loaded2":
                    NCheck(map, "after reload", ground: false);
                    NEdgeCheck(map);
                    nPhase = "4reloaded";
                    nShot = 0;
                    Wait(2, "n_shot");
                    return true;
                case "n_end":
                    WatchCheck(map, "power end");
                    Finish();
                    return true;
            }
            return false;
        }

        // Defs again after a load (statics survive; defs are the same objects, but keep it explicit).
        private static void NResolve(Map map)
        {
            nConduit = DefDatabase<ThingDef>.GetNamed("PowerConduit");
            nHidden = DefDatabase<ThingDef>.GetNamed("HiddenConduit");
            nLamp = DefDatabase<ThingDef>.GetNamed("StandingLamp");
        }

        private static void NCheck(Map map, string when, bool ground)
        {
            CompPowerTrader l2 = NLampAt(map, NF(nL2g, 1, map)), l3 = NLampAt(map, NF(nL2g, 2, map)), lw = NLampAt(map, NF(nLWg, 1, map)), lq = NLampAt(map, NF(nLQg, 1, map));
            PowerNet ng = NNet(map, nGen), n1 = NNet(map, nP1), n2 = NNet(map, NF(nP1, 1, map)), n3 = NNet(map, NF(nP1, 2, map)), nw2 = NNet(map, NF(nW1, 1, map));
            PowerNet nb = NNet(map, nB), nq = NNet(map, NF(nQ2g, 1, map));
            Building bat = nB.GetFirstBuilding(map);
            Info($"{when}: ground net {ng?.GetHashCode()} ({ng?.transmitters.Count} transmitters, gain {ng?.CurrentEnergyGainRate() * 60000f:0} W), P1 {n1?.GetHashCode()}, P2 {n2?.GetHashCode()}, P3 {n3?.GetHashCode()}, W2 {nw2?.GetHashCode()}, battery {nb?.GetHashCode()} (stored {bat?.TryGetComp<CompPowerBattery>()?.StoredEnergy:0}), Q2 {nq?.GetHashCode()}; nets on the map {map.powerNetManager.AllNetsListForReading.Count}");
            if (ground)
            {
                CompPowerTrader lg = NLampAt(map, N(1, 1));
                Result($"{when}: the ground lamp is powered (generator works)", lg != null && lg.PowerOn, $"ground lamp {NOn(lg)}");
            }
            Result($"{when}: riser P joins floors 1-3 into the ground net", ng != null && n1 == ng && n2 == ng && n3 == ng, $"nets P1 {n1?.GetHashCode()} P2 {n2?.GetHashCode()} P3 {n3?.GetHashCode()} ground {ng?.GetHashCode()}");
            Result($"{when}: lamps on floor 2 and floor 3 are powered", l2 != null && l3 != null && l2.PowerOn && l3.PowerOn, $"L2 {NOn(l2)}, L3 {NOn(l3)}");
            Result($"{when}: hidden conduits under walls on both floors join (riser W), LW powered", nw2 == ng && lw != null && lw.PowerOn, $"W2 net {nw2?.GetHashCode()}, LW {NOn(lw)}");
            Result($"{when}: a battery under a conduit does NOT join (only conduit to conduit)", nb != null && nq != null && nb != nq && lq != null && !lq.PowerOn, $"battery net {nb?.GetHashCode()}, Q2 net {nq?.GetHashCode()} ({nq?.transmitters.Count} transmitters), LQ {NOn(lq)}");
        }

        // Wireless hookups stay on their own floor (4 void rows apart at the band edge).
        private static void NEdgeCheck(Map map)
        {
            CompPowerTrader lg = NLampAt(map, nEdgeLampG), lu = NLampAt(map, nEdgeLampU);
            Result("a ground lamp at the band edge does not hook up to a conduit on floor 2 (4 rows away over the void)", lg != null && lg.connectParent == null,
                $"lamp {nEdgeLampG} {NOn(lg)}; conduit on floor 2 at {nEdgeConU}");
            Result("a floor-2 lamp at the band edge does not hook up to a conduit on the ground", lu != null && lu.connectParent == null,
                $"lamp {nEdgeLampU} {NOn(lu)}; ground conduit at {nEdgeConG}");
        }

        private static void SetupPower(Map map)
        {
            NResolve(map);
            List<Pawn> cols = Colonists(map);
            IntVec3 near = cols.Count > 0 ? new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z)) : map.Center;
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 79f, true))
            {
                CellRect r = new CellRect(c.x - 9, c.z - 6, 20, 14);
                CellRect m = r.ExpandedBy(2);
                if (!m.InBounds(map) || m.maxZ >= ProjectZ.GroundZ(map) - 8)
                {
                    continue;
                }
                if (m.Cells.All(x => !x.GetTerrain(map).IsWater && !map.roofGrid.Roofed(x) && !x.Fogged(map) && !(x.GetEdifice(map)?.def.building?.isNaturalRock ?? false) && x.GetTerrain(map).passability == Traversability.Standable))
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
            foreach (IntVec3 c in found.ExpandedBy(2))
            {
                NClear(c, map);
            }
            DebugSettings.enableRandomMentalStates = false;
            Find.PlaySettings.autoRebuild = false;
            nO = new IntVec3(found.minX + 6, 0, found.minZ + 3);
            nHouse = new CellRect(nO.x, nO.z, 13, 9);
            IntVec3 door = N(6, 0);
            // ground walls + door; slabs on floors 2 and 3; floor-2 walls (they carry floor 3)
            foreach (IntVec3 g in nHouse.EdgeCells)
            {
                Place(g == door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, g, map);
            }
            foreach (IntVec3 g in nHouse)
            {
                map.terrainGrid.SetTerrain(NF(g, 1, map), PZDefOf.PZ_UpperFloor);
                map.terrainGrid.SetTerrain(NF(g, 2, map), PZDefOf.PZ_UpperFloor);
            }
            foreach (IntVec3 g in nHouse.EdgeCells)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, NF(g, 1, map), map);
            }
            // ground: generator west of H, line along row 3 into H (hidden conduit under the west wall) to x+8,
            // stub P1 north of the line at x+3, branch at x+8 north to W1 (hidden conduit under the north wall)
            nGen = N(-4, 3);
            Thing gen = Place(DefDatabase<ThingDef>.GetNamed("WoodFiredGenerator"), null, nGen, map);
            gen.TryGetComp<CompRefuelable>()?.Refuel(500f);
            CellRect genRect = gen.OccupiedRect();
            for (int x = genRect.maxX + 1; x <= nO.x + 8; x++)
            {
                IntVec3 c = new IntVec3(x, 0, nO.z + 3);
                Place(c.x == nO.x ? nHidden : nConduit, null, c, map);
            }
            nP1 = N(3, 4);
            Place(nConduit, null, nP1, map);
            for (int z = 4; z <= 7; z++)
            {
                Place(nConduit, null, N(8, z), map);
            }
            nW1 = N(8, 8);
            Place(nHidden, null, nW1, map);
            Place(nLamp, null, N(1, 1), map); // ground lamp
            // floor 2: P2 + a conduit north of it, lamp L2; floor 3: P3, lamp L3
            Place(nConduit, null, NF(nP1, 1, map), map);
            Place(nConduit, null, NF(N(3, 5), 1, map), map);
            nL2g = N(1, 3);
            Place(nLamp, null, NF(nL2g, 1, map), map);
            Place(nConduit, null, NF(nP1, 2, map), map);
            Place(nLamp, null, NF(nL2g, 2, map), map);
            // floor 2: W2 = hidden conduit under the floor-2 north wall over W1, lamp LW
            Place(nHidden, null, NF(nW1, 1, map), map);
            nLWg = N(10, 7);
            Place(nLamp, null, NF(nLWg, 1, map), map);
            // battery alone on the ground (charged), conduit Q2 over it on floor 2, lamp LQ
            nB = N(10, 1);
            Thing bat = Place(DefDatabase<ThingDef>.GetNamed("Battery"), null, nB, map, Rot4.North);
            CompPowerBattery cb = bat.TryGetComp<CompPowerBattery>();
            cb?.AddEnergy(cb.Props.storedEnergyMax);
            nQ2g = nB;
            Place(nConduit, null, NF(nQ2g, 1, map), map);
            nLQg = N(11, 1); // out of W2's 6-cell hookup range (vanilla hooks a lamp to the first net made near it, not the nearest)
            Place(nLamp, null, NF(nLQg, 1, map), map);
            // edge pair: ground top row <-> floor 2 bottom row, far from H
            int gz = ProjectZ.GroundZ(map);
            int ex = Mathf.Clamp(nO.x, 5, map.Size.x - 16);
            nEdgeLampG = new IntVec3(ex, 0, gz - 1);
            nEdgeConU = NF(new IntVec3(ex, 0, 0), 1, map);
            nEdgeConG = new IntVec3(ex + 10, 0, gz - 1);
            nEdgeLampU = NF(new IntVec3(ex + 10, 0, 0), 1, map);
            foreach (IntVec3 c in new[] { nEdgeLampG, nEdgeConG, nEdgeConG + IntVec3.West, nEdgeConG + IntVec3.East })
            {
                NClear(c, map);
            }
            foreach (IntVec3 c in new[] { nEdgeConU, nEdgeLampU, nEdgeConU + IntVec3.East })
            {
                map.terrainGrid.SetTerrain(c, PZDefOf.PZ_UpperFloor);
            }
            Place(nLamp, null, nEdgeLampG, map);
            Place(nConduit, null, nEdgeConU, map);
            Place(nConduit, null, nEdgeConG, map);
            Place(nLamp, null, nEdgeLampU, map);
            foreach (Pawn p in cols)
            {
                Draft(p, false);
            }
            SetWork(map, false);
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            Info($"power: house {nHouse}, generator {nGen} (rect {genRect}), P1 {nP1} -> P2 {NF(nP1, 1, map)} -> P3 {NF(nP1, 2, map)}, W1 {nW1} -> W2 {NF(nW1, 1, map)}, battery {nB} under Q2 {NF(nQ2g, 1, map)}; edge: ground lamp {nEdgeLampG} / floor-2 conduit {nEdgeConU}, ground conduit {nEdgeConG} / floor-2 lamp {nEdgeLampU}; ground rows {gz}, stride {ProjectZ.Annex(map)?.stride}, floors {ProjectZ.Annex(map)?.floors}");
            AnnexWatch.Reset();
            StartTicks(600, "n_check1");
        }

        private static void NClear(IntVec3 c, Map map)
        {
            foreach (Thing t in c.GetThingList(map).ToList())
            {
                if (t.def.destroyable && (t is Plant || t.def.category == ThingCategory.Item || t is Filth || t is Building))
                {
                    t.Destroy();
                }
            }
        }
    }
}
