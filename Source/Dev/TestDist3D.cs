using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // Mode dist3d (3D distance for the game's "nearest" choices, Patches/Dist3D.cs). Run with -projectzfloors=3
    // -projectzseed=7. Router2's fixture tools: every check wipes the area on all levels (isolated) and sets its own fixed
    // random state; real orders, jobs and ticks. One scene shape: a level-1 floor over ground cells (8..31, 12..23), stairs
    // up at (10, 9) (exit (10, 12)); "up" = a cell on level 1 a few cells past the stairs' top, "far" = a ground cell ~26
    // cells away. Each check also asks the game's own decision with the 3D distance off (Dist3D.Off) as the control.
    //   I   inventory: every row resolved and swapped the expected number of distance expressions;
    //   F   food: a hungry colonist eats the meal upstairs, not the far one on the ground;
    //   S   storage: the hauler stores the wood in the stockpile upstairs, not the far one on the ground;
    //   W   work: a builder delivers to the blueprint upstairs; W2 a cleaner clears the snow upstairs (a cell work scan);
    //   C   same level: on 20 seeded layouts on the ground and 20 on level 1 the food, storage and cell-work choices are
    //       identical with the 3D distance on and off (bit for bit the game's);
    //   SP  sappers: a sapper (its duty without a destination) picks the colonist upstairs within its 50 cells in 3D;
    //   RC  raiders: a melee raider goes for the colonist upstairs (17 cells in 3D), not the one 33 cells away;
    //   P   performance: per-call cost of the helpers (fast path, same band, between bands) against the game's expression;
    //       a busy two-floor colony, ticks with the transpilers removed and back in (alternating rounds).
    public static partial class TestRunner
    {
        private static Pawn d3Thing;
        private static Thing d3Up, d3Far, d3Item;
        private static string d3Choice;
        private static int d3Round;
        private static readonly double[] d3Ms = new double[2];
        private static readonly int[] d3Ticks = new int[2];
        private static long d3CallsOn;

        private const int D3Seed = 7300;

        private static IntVec3 D3Up(Map map) => RL(14, 15, 1, map);

        private static IntVec3 D3Far => RG(40, 4);

        private static IntVec3 D3Home => RG(14, 8);

        private static bool StepDist3D(Map map)
        {
            switch (stage)
            {
                case "d3_setup": D3Setup(map); return true;
                case "d3_i": D3Inventory(map); return true;
                case "d3_f": D3Food(map); return true;
                case "d3_f_check": D3FoodCheck(map); return true;
                case "d3_s": D3Store(map); return true;
                case "d3_s_check": D3StoreCheck(map); return true;
                case "d3_w": D3Build(map); return true;
                case "d3_w_check": D3BuildCheck(map); return true;
                case "d3_w2": D3Snow(map); return true;
                case "d3_w2_check": D3SnowCheck(map); return true;
                case "d3_c": D3Same(map); return true;
                case "d3_sp": D3Sapper(map); return true;
                case "d3_sp_check": D3SapperCheck(map); return true;
                case "d3_rc": D3Raid(map); return true;
                case "d3_rc_check": D3RaidCheck(map); return true;
                case "d3_p": D3Micro(map); return true;
                case "d3_p_round": D3PerfRound(map); return true;
                case "d3_p_done": D3PerfDone(map); return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------------ fixture

        private static void D3Setup(Map map)
        {
            DebugSettings.enableRandomMentalStates = false;
            NoRoofCollapse(map);
            Find.Storyteller.storytellerComps.Clear();
            Find.PlaySettings.autoRebuild = false;
            NoAmbushes(map);
            foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
            {
                a.Destroy();
            }
            AnnexMapComponent annex = ProjectZ.Annex(map);
            if (annex == null || annex.MaxLevel < 2)
            {
                Result("dist3d: a map with 3 levels", false, $"levels {Levels.Min(map)}..{Levels.Max(map)} (run with -projectzfloors=3)");
                Finish();
                return;
            }
            r2Area = new CellRect(map.Center.x - 22, map.Center.z - 16, 44, 32);
            List<Pawn> cols = Colonists(map);
            r2P = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Cleaning) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) && !p.WorkTagIsDisabled(WorkTags.Violent)) ?? cols.First();
            r2Q = cols.FirstOrDefault(p => p != r2P);
            // nothing from the start that competes: food, stockpiles, other zones, blueprints, snow-clear area
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree).Where(t => !(t is Pawn)).ToList())
            {
                t.Destroy();
            }
            foreach (Pawn p in cols)
            {
                p.inventory?.innerContainer?.ClearAndDestroyContents();
            }
            foreach (Zone z in map.zoneManager.AllZones.ToList())
            {
                z.Delete(false);
            }
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)).ToList())
            {
                t.Destroy();
            }
            map.areaManager.SnowOrSandClear.Clear();
            Info($"dist3d: area {r2Area}, walker {r2P}, second {r2Q}, groundZ {annex.groundZ}, stride {annex.stride}, storey {Dist3D.StoreyCells:0.00} cells");
            stage = "d3_i";
        }

        // The common scene: a level-1 floor, stairs up; P on the ground at home, its work set to 'types' (none: idle).
        private static void D3Scene(Map map, int seed, params WorkTypeDef[] types)
        {
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            Rand.PushState(seed);
            try
            {
                R2Floor(map, new CellRect(RG(8, 12).x, RG(8, 12).z, 24, 12), 1);
                r2SA = R2Stairs(map, RG(10, 9), Rot4.North);
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                Teleport(r2P, D3Home);
                Draft(r2P, false);
                R2Work(r2P, types);
                r2P.inventory?.innerContainer?.ClearAndDestroyContents();
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static string D3Where(IntVec3 c, Map map) => $"{c} (level {Levels.LevelOf(c, map)}, ground {Levels.Ground(c, map)})";

        private static string D3Name(Thing t, Map map) => t == null ? "none" : t == d3Up ? "UP" : t == d3Far ? "FAR" : t.ToString() + " at " + D3Where(t.PositionHeld, map);

        private static T D3Control<T>(Func<T> f, bool off)
        {
            bool was = Dist3D.Off;
            Dist3D.Off = off;
            try
            {
                return f();
            }
            finally
            {
                Dist3D.Off = was;
            }
        }

        private static Thing D3Spawn(ThingDef def, int count, IntVec3 c, Map map)
        {
            Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
            t.stackCount = count;
            t = GenSpawn.Spawn(t, c, map);
            t.SetForbidden(false, false);
            return t;
        }

        private static Zone_Stockpile D3Zone(Map map, IEnumerable<IntVec3> cells)
        {
            Zone_Stockpile z = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(z);
            foreach (IntVec3 c in cells)
            {
                z.AddCell(c);
            }
            return z;
        }

        private static void D3Wipe(Map map)
        {
            foreach (Zone z in map.zoneManager.AllZones.ToList())
            {
                z.Delete(false);
            }
            map.areaManager.SnowOrSandClear.Clear();
        }

        // ------------------------------------------------------------------------------------------------ I: inventory

        private static void D3Inventory(Map map)
        {
            List<string> bad = new List<string>();
            foreach (Dist3D.Row row in Dist3D.Rows)
            {
                Info($"  row '{row.name}': {row.type.Name}.{row.outer}{(row.inner == null ? "" : " / " + row.inner)}: methods {row.targets.Count}, swapped {row.swaps} (expected {row.expected})");
                if (row.targets.Count == 0 || row.swaps != row.expected)
                {
                    bad.Add($"{row.name} {row.swaps}/{row.expected} in {row.targets.Count} methods");
                }
            }
            Result("I inventory: every row found and swapped as expected", bad.Count == 0, bad.Count == 0 ? $"{Dist3D.Rows.Length} rows, {Dist3D.Rows.Sum(r => r.swaps)} swaps" : string.Join("; ", bad));
            stage = "d3_f";
        }

        // ------------------------------------------------------------------------------------------------ F: food

        private static void D3Food(Map map)
        {
            D3Scene(map, D3Seed + 1);
            d3Up = D3Spawn(ThingDefOf.MealSimple, 1, D3Up(map), map);
            d3Far = D3Spawn(ThingDefOf.MealSimple, 1, D3Far, map);
            Thing off = D3Control(() => FoodUtility.TryFindBestFoodSourceFor(r2P, r2P, false, out Thing f, out _) ? f : null, true);
            Thing on = D3Control(() => FoodUtility.TryFindBestFoodSourceFor(r2P, r2P, false, out Thing f, out _) ? f : null, false);
            d3Choice = $"control (3D off) {D3Name(off, map)}, 3D on {D3Name(on, map)}";
            Info("F " + d3Choice);
            r2P.needs.food.CurLevelPercentage = 0.2f;
            R2Mark();
            TickUntil(3000, () => r2P.CurJobDef == JobDefOf.Ingest && r2P.CurJob.targetA.Thing != null && (r2P.carryTracker.CarriedThing != null || r2P.CurJob.targetA.Thing.Destroyed), "d3_f_check", 5);
        }

        private static void D3FoodCheck(Map map)
        {
            tickWatch = null;
            Job j = r2P.CurJob;
            Thing ate = j?.def == JobDefOf.Ingest ? j.targetA.Thing : null;
            Thing held = r2P.carryTracker.CarriedThing;
            bool up = ate == d3Up;
            Result("F food: a hungry colonist goes upstairs for the meal right above, not the far one on the ground",
                up && !d3Far.Destroyed && climbsColonists - r2ClimbsAt >= 1,
                $"{d3Choice}; ate {D3Name(ate, map)}, carrying {held}, far meal left {!d3Far.Destroyed}, at {D3Where(r2P.Position, map)}; {R2Stats()}");
            r2P.jobs.StopAll();
            stage = "d3_s";
        }

        // ------------------------------------------------------------------------------------------------ S: storage

        private static void D3Store(Map map)
        {
            D3Scene(map, D3Seed + 2, WorkTypeDefOf.Hauling);
            D3Wipe(map);
            Zone_Stockpile up = D3Zone(map, new CellRect(RG(13, 14).x, RG(13, 14).z, 3, 3).Cells.Select(g => Levels.Cell(g, 1, map)));
            Zone_Stockpile far = D3Zone(map, new CellRect(D3Far.x - 1, D3Far.z - 1, 3, 3).Cells);
            d3Item = D3Spawn(ThingDefOf.WoodLog, 20, RG(14, 7), map);
            IntVec3 Pick(bool off) => D3Control(() =>
            {
                Rand.PushState(D3Seed + 20);
                try
                {
                    return StoreUtility.TryFindBestBetterStoreCellFor(d3Item, r2P, map, StoragePriority.Unstored, Faction.OfPlayer, out IntVec3 c) ? c : IntVec3.Invalid;
                }
                finally
                {
                    Rand.PopState();
                }
            }, off);
            IntVec3 cOff = Pick(true), cOn = Pick(false);
            d3Choice = $"control (3D off) {(cOff.IsValid ? D3Where(cOff, map) : "none")}, 3D on {(cOn.IsValid ? D3Where(cOn, map) : "none")}";
            Info("S " + d3Choice);
            r2Thing = null;
            R2Mark();
            d3Thing = null;
            TickUntil(3000, () =>
            {
                if (r2Thing == null && r2P.CurJobDef == JobDefOf.HaulToCell)
                {
                    r2Thing = r2P;
                    d3Choice += $"; first haul job to {D3Where(r2P.CurJob.targetB.Cell, map)}";
                }
                return d3Item.Spawned && (up.ContainsCell(d3Item.Position) || far.ContainsCell(d3Item.Position));
            }, "d3_s_check", 5);
        }

        private static void D3StoreCheck(Map map)
        {
            tickWatch = null;
            bool stored = d3Item.Spawned;
            int L = stored ? Levels.LevelOf(d3Item.Position, map) : -99;
            Result("S storage: the hauler stores the wood in the stockpile upstairs next to it, not the far one on the ground",
                stored && L == 1 && climbsColonists - r2ClimbsAt >= 1,
                $"{d3Choice}; wood at {(stored ? D3Where(d3Item.Position, map) : "carried/gone")}; {R2Stats()}");
            r2P.jobs.StopAll();
            D3Wipe(map);
            stage = "d3_w";
        }

        // ------------------------------------------------------------------------------------------------ W: work

        private static void D3Build(Map map)
        {
            D3Scene(map, D3Seed + 3, WorkTypeDefOf.Construction);
            d3Up = GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, D3Up(map), map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
            d3Far = GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, D3Far, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
            D3Spawn(ThingDefOf.WoodLog, 30, RG(14, 7), map);
            JobGiver_Work jg = new JobGiver_Work();
            string Pick(bool off) => D3Control(() =>
            {
                Rand.PushState(D3Seed + 30);
                try
                {
                    Job j = jg.TryIssueJobPackage(r2P, default(JobIssueParams)).Job;
                    return j == null ? "none" : j.def.defName + " -> " + D3Name(j.targetB.Thing ?? j.targetA.Thing, map);
                }
                finally
                {
                    Rand.PopState();
                }
            }, off);
            d3Choice = $"control (3D off) {Pick(true)}, 3D on {Pick(false)}";
            Info("W " + d3Choice);
            r2Thing = null;
            R2Mark();
            TickUntil(3000, () =>
            {
                Job j = r2P.CurJob;
                if (r2Thing == null && j != null && (j.def == JobDefOf.HaulToContainer || j.def == JobDefOf.FinishFrame))
                {
                    r2Thing = j.targetB.Thing ?? j.targetA.Thing;
                }
                return d3Up.Destroyed || d3Far.Destroyed;   // a blueprint turns into a frame when its resources arrive
            }, "d3_w_check", 5);
        }

        private static void D3BuildCheck(Map map)
        {
            tickWatch = null;
            Result("W work: the builder delivers to the blueprint upstairs first, not the far one on the ground",
                r2Thing == d3Up && d3Up.Destroyed && !d3Far.Destroyed,
                $"{d3Choice}; first delivery to {D3Name(r2Thing, map)}, upstairs blueprint built into a frame {d3Up.Destroyed}, far one {d3Far.Destroyed}; {R2Stats()}");
            r2P.jobs.StopAll();
            stage = "d3_w2";
        }

        private static void D3Snow(Map map)
        {
            D3Scene(map, D3Seed + 4, WorkTypeDefOf.Cleaning);
            D3Wipe(map);
            List<IntVec3> up = new List<IntVec3> { RL(13, 15, 1, map), RL(14, 15, 1, map), RL(15, 15, 1, map) };
            List<IntVec3> far = new List<IntVec3> { D3Far, D3Far + IntVec3.East, D3Far + IntVec3.West };
            foreach (IntVec3 c in up.Concat(far))
            {
                map.areaManager.SnowOrSandClear[c] = true;
                map.snowGrid.SetDepth(c, 0.8f);
            }
            JobGiver_Work jg = new JobGiver_Work();
            string Pick(bool off) => D3Control(() =>
            {
                Rand.PushState(D3Seed + 40);
                try
                {
                    Job j = jg.TryIssueJobPackage(r2P, default(JobIssueParams)).Job;
                    return j == null ? "none" : j.def.defName + " at " + D3Where(j.targetA.Cell, map);
                }
                finally
                {
                    Rand.PopState();
                }
            }, off);
            d3Choice = $"control (3D off) {Pick(true)}, 3D on {Pick(false)}";
            Info("W2 " + d3Choice);
            d3Item = null;
            r2Dest = IntVec3.Invalid;
            R2Mark();
            TickUntil(2000, () =>
            {
                Job j = r2P.CurJob;
                if (!r2Dest.IsValid && j != null && j.def == JobDefOf.ClearSnow)
                {
                    r2Dest = j.targetA.Cell;
                }
                return r2Dest.IsValid && (map.snowGrid.GetDepth(r2Dest) < 0.2f || r2P.CurJobDef != JobDefOf.ClearSnow);
            }, "d3_w2_check", 5);
        }

        private static void D3SnowCheck(Map map)
        {
            tickWatch = null;
            Result("W2 cell work: the cleaner clears the snow upstairs first, not the far snow on the ground",
                r2Dest.IsValid && Levels.LevelOf(r2Dest, map) == 1 && map.snowGrid.GetDepth(r2Dest) < 0.2f,
                $"{d3Choice}; first ClearSnow at {(r2Dest.IsValid ? D3Where(r2Dest, map) : "none")}, depth now {(r2Dest.IsValid ? map.snowGrid.GetDepth(r2Dest) : -1f):0.00}; {R2Stats()}");
            r2P.jobs.StopAll();
            D3Wipe(map);
            stage = "d3_c";
        }

        // ------------------------------------------------------------------------------------------------ C: same level

        // On one level the choices are the game's: food, storage and cell work picked on seeded random layouts with the
        // 3D distance on and off must agree (20 layouts on the ground: the fast path; 20 on level 1: the same-band path).
        private static void D3Same(Map map)
        {
            int layouts = 0, same = 0, crossCalls0 = Dist3D.CrossCalls;
            List<string> diff = new List<string>();
            JobGiver_Work jg = new JobGiver_Work();
            for (int L = 0; L <= 1; L++)
            {
                for (int s = 0; s < 20; s++)
                {
                    D3Scene(map, D3Seed + 100 + 50 * L + s, WorkTypeDefOf.Hauling, WorkTypeDefOf.Cleaning);
                    D3Wipe(map);
                    if (L == 1)
                    {
                        r2SA?.Destroy();   // (level 1 on its own: everything the pawn can reach is on its level)
                        R2Floor(map, new CellRect(r2Area.minX + 1, r2Area.minZ + 1, r2Area.Width - 2, r2Area.Height - 2), 1);
                        map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    }
                    Rand.PushState(D3Seed + 1000 + 50 * L + s);
                    try
                    {
                        IntVec3 Rnd() => Levels.Cell(new IntVec3(r2Area.minX + Rand.RangeInclusive(2, r2Area.Width - 4), 0, r2Area.minZ + Rand.RangeInclusive(2, r2Area.Height - 4)), L, map);
                        Teleport(r2P, Rnd());
                        List<Thing> meals = new List<Thing>();
                        for (int i = 0; i < 4; i++)
                        {
                            IntVec3 c = Rnd();
                            if (c.Standable(map) && c.GetFirstItem(map) == null)
                            {
                                meals.Add(D3Spawn(ThingDefOf.MealSimple, 1, c, map));
                            }
                        }
                        for (int i = 0; i < 3; i++)
                        {
                            IntVec3 c = Rnd();
                            D3Zone(map, new CellRect(c.x, c.z, 2, 2).Cells.Where(x => x.Standable(map) && x.GetZone(map) == null && x.GetFirstItem(map) == null));
                        }
                        IntVec3 ic = Rnd();
                        Thing wood = ic.GetFirstItem(map) == null && ic.GetZone(map) == null ? D3Spawn(ThingDefOf.WoodLog, 10, ic, map) : null;
                        for (int i = 0; i < 4; i++)
                        {
                            IntVec3 c = Rnd();
                            map.areaManager.SnowOrSandClear[c] = true;
                            map.snowGrid.SetDepth(c, 0.8f);
                        }
                        string Decide(bool off) => D3Control(() =>
                        {
                            Thing f = FoodUtility.TryFindBestFoodSourceFor(r2P, r2P, false, out Thing ff, out _) ? ff : null;
                            Rand.PushState(D3Seed + 7);
                            IntVec3 sc = wood != null && StoreUtility.TryFindBestBetterStoreCellFor(wood, r2P, map, StoragePriority.Unstored, Faction.OfPlayer, out IntVec3 x) ? x : IntVec3.Invalid;
                            Job j = jg.TryIssueJobPackage(r2P, default(JobIssueParams)).Job;
                            Rand.PopState();
                            return $"food {f?.Position}, store {sc}, work {j?.def?.defName} {j?.targetA.Cell}";
                        }, off);
                        string a = Decide(true), b = Decide(false);
                        layouts++;
                        if (a == b)
                        {
                            same++;
                        }
                        else if (diff.Count < 5)
                        {
                            diff.Add($"L{L} s{s}: off [{a}] on [{b}]");
                        }
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                    D3Wipe(map);
                }
            }
            Result("C same level: on 40 seeded layouts (20 ground, 20 on level 1) food, storage and cell-work choices are identical with the 3D distance on and off",
                layouts == 40 && same == layouts,
                $"layouts {layouts}, identical {same}, cross-band computations {Dist3D.CrossCalls - crossCalls0} (unreachable things on other levels measured before the reach test); {string.Join(" | ", diff)}");
            stage = "d3_sp";
        }

        // ------------------------------------------------------------------------------------------------ SP: sappers

        // A sapper whose duty has no destination picks a random player target within 50 cells (JobGiver_AISapper). The only
        // one within 50 cells in 3D is the colonist upstairs (ground 16 + one storey); a player wall far off on the ground
        // (over 50) is what the game's flat distance would leave it (its fallbacks go for buildings).
        private static void D3Sapper(Map map)
        {
            D3Scene(map, D3Seed + 5);
            R2Hide(map, r2P);
            Teleport(r2P, RL(20, 18, 1, map));
            Draft(r2P, true);
            r2P.drafter.FireAtWill = false;
            IntVec3 wallAt = new IntVec3(Mathf.Min(r2Area.minX + 2 + 58, map.Size.x - 3), 0, r2Area.minZ + 2);
            Thing wall = R2Wall(map, wallAt);
            Pawn r = MakeRaider(map, RG(20, 2));
            r.equipment.DestroyAllEquipment();
            d3Thing = r;
            float d = Dist3D.Distance(r.Position, r2P.Position, map);
            int raw = r.Position.DistanceToSquared(r2P.Position);
            LordMaker.MakeNewLord(r.Faction, new LordJob_D3Sapper(), map, new List<Pawn> { r });
            d3Choice = $"sapper at {D3Where(r.Position, map)}, colonist at {D3Where(r2P.Position, map)}: 3D {d:0.0} cells (flat {Mathf.Sqrt(raw):0.0}); far wall {(wall != null ? D3Where(wall.Position, map) + " at " + Dist3D.Distance(r.Position, wall.Position, map).ToString("0.0") : "not placed")}; duty {r.mindState.duty?.def?.defName} focus {r.mindState.duty?.focus}";
            Info("SP " + d3Choice);
            d3Far = wall;
            R2Mark();
            TickUntil(4000, () => Levels.LevelOf(r.Position, map) == 1, "d3_sp_check", 10);
        }

        private static void D3SapperCheck(Map map)
        {
            tickWatch = null;
            Pawn r = d3Thing;
            Result("SP sappers: a sapper picks the colonist upstairs (the nearest target, within 50 cells in 3D) and goes up",
                r != null && Levels.LevelOf(r.Position, map) == 1 && climbsHostiles - r2HostClimbsAt >= 1 && r.mindState.duty?.def == DutyDefOf.Sapper && !r.mindState.duty.focus.IsValid,
                $"{d3Choice}; sapper now at {D3Where(r.Position, map)} job {r.CurJobDef?.defName} -> {r.CurJob?.targetA}, duty focus {r.mindState.duty?.focus}; {R2Stats()}");
            foreach (Pawn h in map.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer)).ToList())
            {
                h.Destroy();
            }
            if (d3Far != null && !d3Far.Destroyed)
            {
                d3Far.Destroy();
            }
            Heal(r2P);
            R2Unhide(map);
            stage = "d3_rc";
        }

        // ------------------------------------------------------------------------------------------------ RC: raiders

        private static void D3Raid(Map map)
        {
            D3Scene(map, D3Seed + 6);
            R2Hide(map, r2P, r2Q);
            Teleport(r2P, RL(10, 15, 1, map));
            Teleport(r2Q, RG(43, 4));
            foreach (Pawn p in new[] { r2P, r2Q })
            {
                Draft(p, true);
                p.drafter.FireAtWill = false;
            }
            Pawn r = MakeRaider(map, RG(10, 4));
            r.equipment.DestroyAllEquipment();
            r.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MeleeWeapon_Club"), ThingDefOf.WoodLog));
            d3Thing = r;
            TargetScanFlags flags = TargetScanFlags.NeedReachable | TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable;
            Thing Pick(bool off) => D3Control(() => AttackTargetFinder.BestAttackTarget(r, flags, null, 0f, 65f)?.Thing, off);
            d3Choice = $"raider at {D3Where(r.Position, map)}; up {r2P} {Dist3D.Distance(r.Position, r2P.Position, map):0.0} cells, ground {r2Q} {Dist3D.Distance(r.Position, r2Q.Position, map):0.0}; BestAttackTarget(65): control (3D off) {Pick(true)}, 3D on {Pick(false)}";
            Info("RC " + d3Choice);
            LordMaker.MakeNewLord(r.Faction, new LordJob_AssaultColony(r.Faction, canKidnap: false, canTimeoutOrFlee: false, canSteal: false), map, new List<Pawn> { r });
            R2Mark();
            TickUntil(1500, () => r.mindState.enemyTarget != null, "d3_rc_check", 2);
        }

        private static void D3RaidCheck(Map map)
        {
            tickWatch = null;
            Pawn r = d3Thing;
            Thing t = r?.mindState.enemyTarget;
            Result("RC raider target: a melee raider goes for the colonist upstairs (nearer in 3D), not the one far off on the ground",
                t == r2P,
                $"{d3Choice}; first target {t} at {(t != null ? D3Where(t.Position, map) : "-")}; {R2Stats()}");
            foreach (Pawn h in map.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer)).ToList())
            {
                h.Destroy();
            }
            Heal(r2P);
            Heal(r2Q);
            R2Unhide(map);
            stage = "d3_p";
        }

        // ------------------------------------------------------------------------------------------------ P: performance

        private static void D3Micro(Map map)
        {
            const int N = 2000000;
            IntVec3 g1 = RG(3, 3), g2 = RG(30, 20);
            IntVec3 u1 = RL(3, 3, 1, map), u2 = RL(30, 20, 1, map);
            D3Pair[] pairs = { new D3Pair("ground (fast path)", g1, g2), new D3Pair("level 1, same band", u1, u2), new D3Pair("between bands", g1, u2) };
            List<string> lines = new List<string>();
            long sink = 0;
            object mo = map;
            for (int rep = 0; rep < 2; rep++)   // (the first pass warms the JIT up; the second is measured)
            foreach (D3Pair p in pairs)
            {
                IntVec3 a = p.a, b = p.b;
                Stopwatch sw = Stopwatch.StartNew();
                for (int i = 0; i < N; i++)
                {
                    a.x ^= i & 1;
                    sink += (a - b).LengthHorizontalSquared;
                }
                double raw = sw.Elapsed.TotalMilliseconds * 1e6 / N;
                a = p.a;
                sw.Restart();
                for (int i = 0; i < N; i++)
                {
                    a.x ^= i & 1;
                    sink += Dist3D.SubLenSq(a, b, mo);
                }
                double h = sw.Elapsed.TotalMilliseconds * 1e6 / N;
                if (rep == 0)
                {
                    continue;
                }
                lines.Add($"{p.name}: game {raw:0.0} ns, helper {h:0.0} ns (+{h - raw:0.0} ns)");
                if (lines.Count == 1)
                {
                    d3MicroFast = h;
                    d3MicroOver = h - raw;
                }
            }
            Info($"P micro ({N} calls each; sink {sink & 1}): " + string.Join("; ", lines));
            Result("P per call: the fast path (both on the ground) costs under 5 ns over the game's expression", d3MicroOver < 5.0, string.Join("; ", lines));
            d3Round = 0;
            d3Ms[0] = d3Ms[1] = 0;
            d3Ticks[0] = d3Ticks[1] = 0;
            d3CallsOn = 0;
            stage = "d3_p_round";
        }

        private static double d3MicroFast, d3MicroOver;

        private struct D3Pair
        {
            public string name;
            public IntVec3 a, b;

            public D3Pair(string name, IntVec3 a, IntVec3 b)
            {
                this.name = name;
                this.a = a;
                this.b = b;
            }
        }

        // A busy two-floor colony (all colonists hauling, building, cleaning, eating on both levels), the same fixture
        // (seed) every round; rounds alternate transpilers removed (game) / in (3D). Ticks timed per round.
        private static void D3PerfRound(Map map)
        {
            const int Rounds = 6, Ticks = 900;
            if (d3Round >= Rounds)
            {
                stage = "d3_p_done";
                return;
            }
            bool on = d3Round % 2 == 1;
            D3Patched(on);
            D3Scene(map, D3Seed + 9, WorkTypeDefOf.Hauling, WorkTypeDefOf.Construction, WorkTypeDefOf.Cleaning);
            D3Wipe(map);
            List<Pawn> crew = Colonists(map).Take(5).ToList();
            Rand.PushState(D3Seed + 900);
            try
            {
                R2Floor(map, new CellRect(r2Area.minX + 24, r2Area.minZ + 2, 18, 10), 1);
                Building_Stairs s2 = R2Stairs(map, RG(30, 1), Rot4.North);
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                IntVec3 Rnd(int L) => Levels.Cell(new IntVec3(r2Area.minX + Rand.RangeInclusive(2, r2Area.Width - 4), 0, r2Area.minZ + Rand.RangeInclusive(2, r2Area.Height - 4)), L, map);
                for (int i = 0; i < crew.Count; i++)
                {
                    Teleport(crew[i], RG(4 + 3 * i, 2));
                    Draft(crew[i], false);
                    R2Work(crew[i], WorkTypeDefOf.Hauling, WorkTypeDefOf.Construction, WorkTypeDefOf.Cleaning);
                    crew[i].inventory?.innerContainer?.ClearAndDestroyContents();
                    if (crew[i].needs?.food != null)
                    {
                        crew[i].needs.food.CurLevelPercentage = i % 2 == 0 ? 0.2f : 1f;
                    }
                }
                D3Zone(map, new CellRect(RG(14, 18).x, RG(14, 18).z, 4, 3).Cells.Select(g => Levels.Cell(g, 1, map)));
                D3Zone(map, new CellRect(RG(2, 26).x, RG(2, 26).z, 4, 3).Cells);
                for (int i = 0; i < 30; i++)
                {
                    IntVec3 c = Rnd(i % 2);
                    if (c.Standable(map) && c.GetFirstItem(map) == null && c.GetZone(map) == null)
                    {
                        D3Spawn(i % 3 == 0 ? ThingDefOf.Steel : ThingDefOf.WoodLog, 15, c, map);
                    }
                }
                for (int i = 0; i < 6; i++)
                {
                    IntVec3 c = Rnd(i % 2);
                    if (c.Standable(map) && c.GetFirstItem(map) == null && c.GetZone(map) == null)
                    {
                        D3Spawn(ThingDefOf.MealSimple, 1, c, map);
                    }
                }
                for (int i = 0; i < 10; i++)
                {
                    IntVec3 c = Rnd(i % 2);
                    if (c.Standable(map) && c.GetFirstItem(map) == null && GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, c, Rot4.North, map, stuffDef: ThingDefOf.WoodLog).Accepted)
                    {
                        GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, c, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
                    }
                }
                for (int i = 0; i < 20; i++)
                {
                    IntVec3 c = Rnd(i % 2);
                    map.areaManager.SnowOrSandClear[c] = true;
                    map.snowGrid.SetDepth(c, 0.8f);
                }
            }
            finally
            {
                Rand.PopState();
            }
            long calls0 = Dist3D.Calls;
            TickManager tm = Find.TickManager;
            Rand.PushState(D3Seed + 901);   // (the ticks too: both kinds of round see the same colony)
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                for (int t = 0; t < Ticks; t++)
                {
                    tm.DoSingleTick();
                }
            }
            finally
            {
                sw.Stop();
                Rand.PopState();
            }
            int k = on ? 1 : 0;
            d3Ms[k] += sw.Elapsed.TotalMilliseconds;
            d3Ticks[k] += Ticks;
            if (on)
            {
                d3CallsOn += Dist3D.Calls - calls0;
            }
            Info($"P round {d3Round} ({(on ? "3D" : "game")}): {sw.Elapsed.TotalMilliseconds / Ticks:0.000} ms/tick, helper calls {Dist3D.Calls - calls0}");
            foreach (Pawn p in crew)
            {
                p.jobs?.StopAll();
                Draft(p, true);
            }
            D3Wipe(map);
            d3Round++;
        }

        private static void D3Patched(bool on)
        {
            Harmony h = new Harmony("Peren.IsoRimProjectZ");
            MethodInfo tr = AccessTools.Method(typeof(Dist3D_Patch), "Transpiler");
            foreach (Dist3D.Row row in Dist3D.Rows)
            {
                foreach (MethodBase m in row.targets)
                {
                    bool has = Harmony.GetPatchInfo(m)?.Transpilers.Any(p => p.PatchMethod == tr) ?? false;
                    if (on && !has)
                    {
                        h.Patch(m, transpiler: new HarmonyMethod(tr));
                    }
                    else if (!on && has)
                    {
                        h.Unpatch(m, tr);
                    }
                }
            }
        }

        private static void D3PerfDone(Map map)
        {
            D3Patched(true);
            double game = d3Ms[0] / Math.Max(1, d3Ticks[0]), d3 = d3Ms[1] / Math.Max(1, d3Ticks[1]);
            double perTick = (double)d3CallsOn / Math.Max(1, d3Ticks[1]);
            double est = perTick * d3MicroFast * 1e-6;   // ms per tick in helpers (fast-path cost as the upper bound for ground calls)
            Result("P colony ticks: the transpilers back in (every row swapped again)", Dist3D.Rows.All(r => r.swaps == r.expected), string.Join(", ", Dist3D.Rows.Where(r => r.swaps != r.expected).Select(r => r.name + " " + r.swaps)));
            Result("P colony ticks: a busy two-floor colony is not measurably slower with the 3D distance (within 10 % or 0.05 ms/tick)",
                d3 <= game * 1.10 || d3 - game <= 0.05,
                $"game {game:0.000} ms/tick, 3D {d3:0.000} ms/tick ({(d3 / Math.Max(1e-9, game) - 1) * 100:+0.0;-0.0} %), helper calls {perTick:0.0}/tick ~ {est * 1000:0.00} us/tick ({est / Math.Max(1e-9, d3) * 100:0.000} % of a tick)");
            Finish();
        }
    }

    // Test only: a lord whose one toil gives its pawns the Sapper duty without a destination (JobGiver_AISapper's own
    // target search), never changed.
    public class LordJob_D3Sapper : LordJob
    {
        public override StateGraph CreateGraph()
        {
            StateGraph g = new StateGraph();
            g.AddToil(new LordToil_D3Sapper());
            return g;
        }
    }

    public class LordToil_D3Sapper : LordToil
    {
        public override void UpdateAllDuties()
        {
            foreach (Pawn p in lord.ownedPawns)
            {
                p.mindState.duty = new PawnDuty(DutyDefOf.Sapper);
            }
        }
    }
}
