using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode save (-projectzmode=save -projectzsave=<name in Saves>): load a player's save, describe its stairs, the cells
    // round them on both floors and every colonist's job, path and reach, then let it run with real ticks and log what
    // the colonists do (ended jobs, waits) until the blueprints upstairs are built or -projectzsaveticks run out.
    public static partial class TestRunner
    {
        private static string saveName;
        private static int saveTicks = 30000;
        private static int saveLogAt;
        private static int saveBuiltStart;

        private static bool StepSave(Map map)
        {
            switch (stage)
            {
                case "v_load":
                    if (!GenCommandLine.TryGetCommandLineArg("projectzsave", out saveName))
                    {
                        Result("save name", false, "pass -projectzsave=<name>");
                        Finish();
                        return true;
                    }
                    if (GenCommandLine.TryGetCommandLineArg("projectzsaveticks", out string st) && int.TryParse(st, out int n))
                    {
                        saveTicks = n;
                    }
                    stage = "v_loaded";
                    pendingLoad = saveName;
                    return true;
                case "v_loaded":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Info($"loaded {saveName}: map {map} size {map.Size}, tick {Find.TickManager.TicksGame}, annex {ProjectZ.Annex(map)?.floors} floors");
                    if (GenCommandLine.CommandLineArgPassed("projectzsaveroof"))
                    {
                        SrSave(map);
                        return true;
                    }
                    new Harmony("Peren.IsoRimProjectZ.test.save").Patch(AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(EndJobPrefix)));
                    DescribeStairs(map);
                    DescribeColonists(map, "on load");
                    saveBuiltStart = UpstairsBuilt(map);
                    Building_Stairs any = map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).OfType<Building_Stairs>().FirstOrDefault();
                    if (any != null)
                    {
                        ShotAt(map, 0, any.Position, 7f);
                    }
                    Wait(20, "v_shot0");
                    return true;
                }
                case "v_shot0":
                    Capture("save_f1_load");
                    stage = "v_shot0b";
                    return true;
                case "v_shot0b":
                {
                    Building_Stairs any = map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).OfType<Building_Stairs>().FirstOrDefault();
                    ShotAt(map, 1, any != null ? any.Position : map.Center, 7f);
                    Wait(20, "v_shot0c");
                    return true;
                }
                case "v_shot0c":
                    Capture("save_f2_load");
                    saveLogAt = 0;
                    TickUntil(saveTicks, () =>
                    {
                        saveLogAt += 250;
                        if (saveLogAt % 1000 == 0)
                        {
                            DescribeColonists(map, $"+{saveLogAt} ticks", brief: true);
                        }
                        return PendingUpstairs(map) == 0;
                    }, "v_check", 250);
                    return true;
                case "v_check":
                {
                    tickWatch = null;
                    DescribeColonists(map, "at the end");
                    int pending = PendingUpstairs(map);
                    Result("save: everything planned upstairs got built", pending == 0, $"{pending} blueprint(s)/frame(s) left upstairs after {ticksTotal} ticks; built upstairs {saveBuiltStart} -> {UpstairsBuilt(map)}; splices {PZStats.splices}, splice fails {PZStats.spliceFails}");
                    Thing bed = map.listerThings.AllThings.FirstOrDefault(t => t is Building_Bed && ProjectZ.FloorOf(t.Position, map) == 1);
                    Info("built upstairs now: " + string.Join(", ", map.listerBuildings.allBuildingsColonist.Where(b => ProjectZ.FloorOf(b.Position, map) >= 1).Select(b => b.LabelShort + "@" + b.Position)));
                    ShotAt(map, 1, bed != null ? ProjectZ.GroundCellOf(bed.Position, map) : map.Center, 7f);
                    Wait(20, "v_shot1");
                    return true;
                }
                case "v_shot1":
                    Capture("save_f2_end");
                    stage = "v_end";
                    return true;
                case "v_end":
                    Finish();
                    return true;
            }
            return false;
        }

        private static int PendingUpstairs(Map map) => map.listerThings.AllThings.Count(t => (t is Blueprint || t is Frame) && t.Spawned && ProjectZ.FloorOf(t.Position, map) >= 1);

        private static int UpstairsBuilt(Map map) => map.listerBuildings.allBuildingsColonist.Count(b => ProjectZ.FloorOf(b.Position, map) >= 1);

        private static void EndJobPrefix(Pawn_JobTracker __instance, JobCondition condition)
        {
            Pawn p = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            if (p == null || !p.IsColonist || __instance.curJob == null || stage == "done")
            {
                return;
            }
            Job j = __instance.curJob;
            if (condition != JobCondition.Succeeded || j.def == JobDefOf.Wait || j.def == JobDefOf.Wait_MaintainPosture)
            {
                Out($"  job end {p.LabelShort} {j.def.defName} {condition} A {Tgt(j.targetA)} B {Tgt(j.targetB)} giver {j.jobGiver?.GetType().Name} wg {j.workGiverDef?.defName} at {p.Position} carrying {p.carryTracker?.CarriedThing}");
            }
        }

        private static string Tgt(LocalTargetInfo t) => !t.IsValid ? "-" : t.HasThing ? $"{t.Thing.LabelShort}@{t.Thing.Position}" : t.Cell.ToString();

        private static void DescribeStairs(Map map)
        {
            StairGrid g = StairGrid.For(map);
            Info($"stair links: {g?.Count ?? 0}");
            foreach (Building_Stairs s in map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).OfType<Building_Stairs>())
            {
                IntVec3 e = s.TopCell;
                VLink? l = g?.links.FirstOrDefault(x => x.stairs == s);
                Region re = e.IsValid ? map.regionGrid.GetValidRegionAt(e) : null;
                Region rh = map.regionGrid.GetValidRegionAt(s.HighCell);
                TraverseParms tp = TraverseParms.For(TraverseMode.ByPawn);
                Pawn c0 = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
                Info($"stairs {s.ThingID} at {s.Position} rot {s.Rotation.ToStringHuman()} footprint {s.OccupiedRect()} high {s.HighCell} exit {e} (ground {ProjectZ.GroundCellOf(e, map)}): exit terrain {e.GetTerrain(map)?.defName} things [{string.Join(",", e.GetThingList(map).Select(t => t.def.defName))}] walkable {e.Walkable(map)} region {re?.id} district {re?.District?.ID} room {re?.District?.Room?.ID} cells {re?.CellCount}; high region {rh?.id} district {rh?.District?.ID}; usable {l.HasValue && g.Usable(l.Value)}, passable for {c0} {l.HasValue && c0 != null && g.Passable(l.Value, TraverseParms.For(c0))}");
                foreach (IntVec3 c in s.OccupiedRect())
                {
                    IntVec3 up = StairShape.Above(c, map);
                    Info($"   ramp cell {c} index {StairShape.Index(c, s.Position, s.Rotation)} roof {map.roofGrid.RoofAt(c)?.defName}; above {up}: {up.GetTerrain(map)?.defName} [{string.Join(",", up.GetThingList(map).Select(t => t.def.defName))}]");
                }
                IntVec3 low = s.Position - s.Rotation.FacingCell;
                Info($"   low end {low}: approach cells {string.Join(" ", GenAdj.CellsAdjacent8Way(new TargetInfo(low, map)).Where(c => !s.OccupiedRect().Contains(c)).Select(c => c + (c.Standable(map) ? " ok" : " BLOCKED")))}");
                Dump(map, s.Position, 0);
                Dump(map, s.Position, 1);
            }
        }

        // A plan of the cells round a ground cell on floor k: # wall/impassable, S stairs, b blueprint/frame, D door,
        // B other building, . floor, ' ' open air, P pawn.
        private static void Dump(Map map, IntVec3 g, int k)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append($"floor {k + 1} round {g} (north up, x {g.x - 8}..{g.x + 8}):\n");
            for (int dz = 8; dz >= -8; dz--)
            {
                sb.Append($"   z{g.z + dz,4} ");
                for (int dx = -8; dx <= 8; dx++)
                {
                    IntVec3 c = ProjectZ.CellOnFloor(new IntVec3(g.x + dx, 0, g.z + dz), k, map);
                    char ch = '?';
                    if (c.IsValid && c.InBounds(map))
                    {
                        List<Thing> list = c.GetThingList(map);
                        if (list.Any(t => t is Pawn))
                        {
                            ch = 'P';
                        }
                        else if (list.Any(t => t.def == PZDefOf.PZ_Stairs))
                        {
                            ch = 'S';
                        }
                        else if (list.Any(t => t is Blueprint || t is Frame))
                        {
                            ch = 'b';
                        }
                        else if (c.GetDoor(map) != null)
                        {
                            ch = 'D';
                        }
                        else if (!c.Walkable(map))
                        {
                            ch = '#';
                        }
                        else if (c.GetEdifice(map) != null)
                        {
                            ch = 'B';
                        }
                        else if (c.GetTerrain(map) == PZDefOf.PZ_OpenAir)
                        {
                            ch = ' ';
                        }
                        else
                        {
                            ch = map.roofGrid.Roofed(c) ? ':' : '.';
                        }
                    }
                    sb.Append(ch);
                }
                sb.Append('\n');
            }
            Out(sb.ToString());
        }

        private static void DescribeColonists(Map map, string when, bool brief = false)
        {
            Out($"colonists {when} (tick {Find.TickManager.TicksGame}):");
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                Job j = p.CurJob;
                string path = p.pather?.curPath != null ? $"{p.pather.curPath.NodesLeftCount} nodes left, last {p.pather.curPath.LastNode}" : "no path";
                Out($"  {p.LabelShort} at {p.Position} floor {ProjectZ.FloorOf(p.Position, map) + 1}: job {j?.def?.defName} A {Tgt(j?.targetA ?? LocalTargetInfo.Invalid)} B {Tgt(j?.targetB ?? LocalTargetInfo.Invalid)} count {j?.count} giver {j?.jobGiver?.GetType().Name} wg {j?.workGiverDef?.defName}, toil {Traverse.Create(p.jobs?.curDriver).Field("curToilIndex").GetValue()}, moving {p.pather?.Moving} dest {p.pather?.Destination} {path}, carrying {p.carryTracker?.CarriedThing}, report \"{p.GetJobReport()}\"");
                if (brief)
                {
                    continue;
                }
                foreach (Thing t in map.listerThings.AllThings.Where(t => (t is Blueprint || t is Frame) && ProjectZ.FloorOf(t.Position, map) >= 1))
                {
                    bool reach = p.CanReach(t, PathEndMode.Touch, Danger.Deadly);
                    PawnPath pp = map.pathFinder.FindPathNow(p.Position, t, TraverseParms.For(p), PathFinderCostTuning.For(p), PathEndMode.Touch);
                    Out($"    -> {t.LabelShort} at {t.Position}: reach {reach}, path {(pp != null && pp.Found ? pp.NodesReversed.Count + " nodes via " + string.Join(" ", pp.NodesReversed.AsEnumerable().Reverse().Where(c => StairGrid.For(map)?.links.Any(l => l.bottom == c || l.top == c) ?? false)) : "NONE")}");
                    pp?.Dispose();
                }
            }
        }
    }
}
