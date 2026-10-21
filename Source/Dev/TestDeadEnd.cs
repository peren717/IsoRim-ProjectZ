using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode deadend: a player's save (新移民1, 2026-10-15) rebuilt from scratch. Two stairs in one room; the nearer one
    // leads up onto a one-cell floor over the west wall (the floor the stairs make for their exit, open air round it),
    // the other one into the room's upper floor. A bed planned upstairs, its wood outside the door. Reachability says
    // the bed is reachable (over the second stairs); the colonists must take the second stairs with the wood and build
    // the bed, not stand with the wood in hand while every path over the first stairs fails.
    //   -projectzgreedyroute: the route keeps the first stairs it can get onto (the old router), to show the failure.
    // Part 2 (TestDeadEndRoutes.cs): unreachable, fogged and 4th-best-stairs targets.
    public static partial class TestRunner
    {
        public static bool GreedyRoute;
        private static CellRect kHouse;
        private static IntVec3 kDeadPos, kGoodPos, kWood;
        private static readonly Rot4 KDeadRot = Rot4.West, KGoodRot = Rot4.North;
        private static int kPatherFails;
        private static bool kPatched;

        private static void KEndJobPrefix(Pawn_JobTracker __instance, JobCondition condition)
        {
            if (condition == JobCondition.ErroredPather && Traverse.Create(__instance).Field("pawn").GetValue<Pawn>()?.IsColonist == true)
            {
                kPatherFails++;
            }
        }

        private static Thing KBed(Map map, bool built) => map.listerThings.AllThings.FirstOrDefault(t => ProjectZ.FloorOf(t.Position, map) == 1 && (built ? t is Building_Bed : (t is Blueprint || t is Frame) && t.def.entityDefToBuild == ThingDefOf.Bed));

        private static bool StepDeadEnd(Map map)
        {
            if (stage.StartsWith("k_r_"))
            {
                return StepRouteCases(map);
            }
            switch (stage)
            {
                case "k_setup":
                    SetupDeadEnd(map);
                    return true;
                case "k_route":
                {
                    Building_Stairs dead = map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).OfType<Building_Stairs>().FirstOrDefault(s => s.Position == kDeadPos);
                    Building_Stairs good = map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).OfType<Building_Stairs>().FirstOrDefault(s => s.Position == kGoodPos);
                    Thing bp = KBed(map, false);
                    Pawn p = Colonists(map).First();
                    bool reach = p.CanReach(bp, PathEndMode.Touch, Danger.Deadly);
                    PawnPath path = map.pathFinder.FindPathNow(p.Position, bp, TraverseParms.For(p), PathFinderCostTuning.For(p), PathEndMode.Touch);
                    bool found = path != null && path.Found;
                    bool viaGood = found && good != null && path.NodesReversed.Contains(good.TopCell);
                    bool viaDead = found && dead != null && path.NodesReversed.Contains(dead.TopCell);
                    path?.Dispose();
                    Result("dead end: a path from the wood to the bed upstairs over the stairs that lead there", reach && found && viaGood && !viaDead,
                        $"{p} at {p.Position} -> {bp} at {bp?.Position}: reach {reach}, path {(found ? "found" : "NONE")}, over the good stairs (exit {good?.TopCell}) {viaGood}, over the dead-end stairs (exit {dead?.TopCell}) {viaDead}");
                    foreach (Pawn c in Colonists(map))
                    {
                        c.jobs?.EndCurrentJob(JobCondition.InterruptForced);
                    }
                    kPatherFails = 0;
                    TickUntil(30000, () => KBed(map, true) != null, "k_check", 120);
                    return true;
                }
                case "k_check":
                {
                    tickWatch = null;
                    Thing bed = KBed(map, true);
                    Result("dead end: the colonists carried the wood up the other stairs and built the bed", bed != null, bed != null ? $"{bed} at {bed.Position} after {ticksTotal} ticks" : $"no bed after {ticksTotal} ticks; left: {KBed(map, false)}");
                    Result("dead end: no job ended for want of a path", kPatherFails == 0, $"{kPatherFails} job(s) ended ErroredPather; splices {PZStats.splices}, splice fails {PZStats.spliceFails}");
                    ShotAt(map, 1, new IntVec3(kHouse.CenterCell.x, 0, kHouse.CenterCell.z), 7f);
                    Wait(20, "k_shot");
                    return true;
                }
                case "k_shot":
                    Capture("deadend_f2_bed");
                    stage = "k_shot2";
                    return true;
                case "k_shot2":
                    ShotAt(map, 0, new IntVec3(kHouse.CenterCell.x, 0, kHouse.CenterCell.z), 7f);
                    Wait(20, "k_shot2_take");
                    return true;
                case "k_shot2_take":
                    Capture("deadend_f1_stairs");
                    stage = "k_r_setup";   // part 2: TestDeadEndRoutes.cs
                    return true;
                case "k_end":
                    Finish();
                    return true;
            }
            return false;
        }

        private static void SetupDeadEnd(Map map)
        {
            if (!kPatched)
            {
                kPatched = true;
                new Harmony("Peren.IsoRimProjectZ.test.deadend").Patch(AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(KEndJobPrefix)));
            }
            List<Pawn> cols = Colonists(map);
            IntVec3 near = cols.Count > 0 ? new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z)) : map.Center;
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 79f, true))
            {
                CellRect r = new CellRect(c.x - 6, c.z - 6, 12, 14);
                CellRect m = r.ExpandedBy(3);
                if (!m.InBounds(map) || m.maxZ >= ProjectZ.GroundZ(map) - 12)
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
            foreach (IntVec3 c in found.ExpandedBy(3))
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable && (t is Plant || t.def.category == ThingCategory.Item || t is Filth || t is Building))
                    {
                        t.Destroy();
                    }
                }
            }
            DebugSettings.enableRandomMentalStates = false;
            // The save's room: 8 x 7, door in the south wall.
            int ox = found.minX + 2, oz = found.minZ + 6;
            kHouse = new CellRect(ox, oz, 8, 7);
            IntVec3 door = new IntVec3(ox + 4, 0, oz);
            foreach (IntVec3 c in kHouse.EdgeCells)
            {
                Place(c == door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            foreach (IntVec3 c in kHouse)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                map.areaManager.Home[c] = true;
            }
            // Dead-end stairs up to the west: exit over the west wall (no floor round it). Good stairs up to the north:
            // exit on the room's upper floor.
            kDeadPos = new IntVec3(ox + 2, 0, oz + 1);
            kGoodPos = new IntVec3(ox + 6, 0, oz + 3);
            HashSet<IntVec3> wells = new HashSet<IntVec3>(StairShape.Ramp(kDeadPos, KDeadRot).Concat(StairShape.Ramp(kGoodPos, KGoodRot)));
            foreach (IntVec3 g in kHouse.ContractedBy(1))
            {
                if (!wells.Contains(g))
                {
                    map.terrainGrid.SetTerrain(UpS(g, map), PZDefOf.PZ_UpperFloor);
                }
            }
            AcceptanceReport okDead = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, kDeadPos, KDeadRot, map, stuffDef: ThingDefOf.WoodLog);
            AcceptanceReport okGood = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, kGoodPos, KGoodRot, map, stuffDef: ThingDefOf.WoodLog);
            Info($"dead end: placement of the dead-end stairs {(okDead.Accepted ? "accepted" : "refused: " + okDead.Reason)}, of the good stairs {(okGood.Accepted ? "accepted" : "refused: " + okGood.Reason)}");
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, kDeadPos, map, KDeadRot);
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, kGoodPos, map, KGoodRot);
            Building_Stairs dead = map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).OfType<Building_Stairs>().First(s => s.Position == kDeadPos);
            Building_Stairs good = map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).OfType<Building_Stairs>().First(s => s.Position == kGoodPos);
            Region deadExit = map.regionGrid.GetValidRegionAt(dead.TopCell);
            Region goodExit = map.regionGrid.GetValidRegionAt(good.TopCell);
            bool sameFloorLinked = false;
            StairRegions.suppress++;
            try
            {
                RegionTraverser.BreadthFirstTraverse(deadExit, (Region _, Region r) => true, r =>
                {
                    sameFloorLinked |= r == goodExit;
                    return sameFloorLinked;
                }, 1000);
            }
            finally
            {
                StairRegions.suppress--;
            }
            Result("dead end: set up as in the save (2 links; the dead-end exit a floor island, the good exit on the room's floor)", StairGrid.For(map)?.Count == 2 && deadExit != null && deadExit.CellCount == 1 && !sameFloorLinked && SlabDefs.Is(dead.TopCell.GetTerrain(map)),
                $"dead-end stairs {kDeadPos} exit {dead.TopCell} ({dead.TopCell.GetTerrain(map)?.defName}, region cells {deadExit?.CellCount}, joined on its floor to the good exit {sameFloorLinked}); good stairs {kGoodPos} exit {good.TopCell}");
            // The bed upstairs, by the north wall; its wood outside the door.
            IntVec3 bedAt = IntVec3.Invalid;
            foreach (IntVec3 g in new[] { new IntVec3(ox + 3, 0, oz + 5), new IntVec3(ox + 4, 0, oz + 5), new IntVec3(ox + 2, 0, oz + 5) })
            {
                if (GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Bed, UpS(g, map), Rot4.South, map, stuffDef: ThingDefOf.WoodLog).Accepted)
                {
                    bedAt = UpS(g, map);
                    break;
                }
            }
            if (!bedAt.IsValid)
            {
                Result("dead end: bed placeable upstairs", false, "no bed spot");
                Finish();
                return;
            }
            GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Bed, bedAt, map, Rot4.South, Faction.OfPlayer, ThingDefOf.WoodLog);
            kWood = new IntVec3(ox + 3, 0, oz - 3);
            int woodLeft = 150;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(kWood, 2f, true))
            {
                if (woodLeft <= 0)
                {
                    break;
                }
                Thing w = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                w.stackCount = Math.Min(75, woodLeft);
                woodLeft -= w.stackCount;
                GenSpawn.Spawn(w, c, map);
            }
            int i = 0;
            foreach (Pawn p in cols)
            {
                p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
                foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefs)
                {
                    if (!p.WorkTypeIsDisabled(w))
                    {
                        p.workSettings.SetPriority(w, w == WorkTypeDefOf.Construction || w == WorkTypeDefOf.Hauling ? 1 : 0);
                    }
                }
                Teleport(p, kWood + new IntVec3(i++ % 3 - 1, 0, -2));
            }
            Find.PlaySettings.autoRebuild = false;
            Info($"dead end: house {kHouse}, dead-end stairs {kDeadPos} {KDeadRot.ToStringHuman()}, good stairs {kGoodPos} {KGoodRot.ToStringHuman()}, bed blueprint {bedAt}, wood at {kWood}, greedy route {GreedyRoute}");
            Dump(map, kDeadPos, 0);
            Dump(map, kDeadPos, 1);
            AnnexWatch.Reset();
            Wait(5, "k_route");
        }
    }
}
