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

namespace ProjectZ.Dev
{
    // Mode billradius (a bill's ingredient search radius in 3D, Patches/BillRadius3D.cs). Run with -projectzfloors=5
    // -projectzmapsize=250 -projectzseed=7 and -isorim3d;
    // with -projectzfloors=1 only the map-without-floors check runs. Router2's fixture tools: every check wipes the area on
    // all levels and sets its own fixed random state (fixture and ticks); real bills, real DoBill jobs, ticks. Each check
    // also asks the game's own search with the patch off (BillRadius3D.Off) as the control.
    //   P0  the patches landed (prefix, the entry-test wrap, the ring);
    //   B1  ground bench, radius 10: the chunk one floor up (3D 6.3) is fetched, not the ground one 29 cells off;
    //   B2  radius 6: the chunk one floor up 2 cells off (3D 7.3) is left, the ground one 3 cells off is used;
    //   B3  bench upstairs, radius 10: the ground chunk under it is fetched (the reverse);
    //   B4  Unlimited, the only chunk on level 4 (over 999 rows north): fetched;
    //   B5a a campfire's burn-apparel bill, radius 12, an outfit stand just across the band gap (flat 10, far in 3D): not
    //       used; B5b an outfit stand straight upstairs (3D 7.3): its shirt is used;
    //   B6  one floor: on seeded layouts (ground, level 1) the search's choice is the game's with the patch on and off;
    //       on a map without floors (-projectzfloors=1) the same, the patch inactive, and a real job takes the predicted one;
    //   B7  timing of the bill search on a busy five-floor map, patch on and off;
    //   B8  the ring shots, bill dialog open: the bench floor and the floor above in the 3D view, then both in the flat view
    //       (the 3D view turned off for those two shots, test only: ProjectZ locks it on).
    public static partial class TestRunner
    {
        private const int BrFix = 8100;   // the base of this mode's fixture ids (FixtureSeed of the mode and the id)
        private static Thing brBench, brUp, brFar, brGround, brItem;
        private static Thing brFirst, brCarried;
        private static string brChoice;
        private static Bill_Production brBill;
        private static Building_OutfitStand brStand;
        private static int brShot;
        private static int brRingAt;
        private static Dialog_BillConfig brDialog;
        private static MethodInfo BrFindMBound;
        private static MethodInfo BrFindM => BrFindMBound ??= AccessTools.Method(typeof(WorkGiver_DoBill), "TryFindBestBillIngredients");   // (bound on first use: nothing for players)

        private static ThingDef BrChunkDef => DefDatabase<ThingDef>.GetNamed("ChunkGranite");

        private static bool StepBillRadius(Map map)
        {
            switch (stage)
            {
                case "br_setup": BrSetup(map); return true;
                case "br_1": BrB1(map); return true;
                case "br_1_check": BrB1Check(map); return true;
                case "br_2": BrB2(map); return true;
                case "br_2_check": BrB2Check(map); return true;
                case "br_3": BrB3(map); return true;
                case "br_3_check": BrB3Check(map); return true;
                case "br_4": BrB4(map); return true;
                case "br_4_check": BrB4Check(map); return true;
                case "br_5a": BrB5a(map); return true;
                case "br_5a_check": BrB5aCheck(map); return true;
                case "br_5b": BrB5b(map); return true;
                case "br_5b_check": BrB5bCheck(map); return true;
                case "br_6": BrB6(map); return true;
                case "br_7": BrB7(map); return true;
                case "br_8": BrB8(map); return true;
                case "br_8_take": BrB8Take(map); return true;
                case "br_8_done": BrB8Done(map); return true;
                case "br_flat": BrFlat(map); return true;
                case "br_flat_check": BrFlatCheck(map); return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------------ fixture

        private static void BrSetup(Map map)
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
            int gz = annex?.groundZ ?? map.Size.z;
            r2Area = new CellRect(map.Size.x / 2 - 22, gz / 2 - 16, 44, 32);
            List<Pawn> cols = Colonists(map);
            r2P = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Crafting) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)) ?? cols.First();
            r2Q = cols.FirstOrDefault(p => p != r2P);
            foreach (Zone z in map.zoneManager.AllZones.ToList())
            {
                z.Delete(false);
            }
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)).ToList())
            {
                t.Destroy();
            }
            // nothing from the start that a bill could take: every stone chunk and every loose apparel, map-wide
            foreach (Thing t in map.listerThings.AllThings.Where(t => t.def.thingCategories != null && (t.def.IsApparel || t.def.thingCategories.Contains(ThingCategoryDefOf.StoneChunks))).ToList())
            {
                t.Destroy();
            }
            bool ok = FailSoft.IsApplied(nameof(BillRadius3D_Search_Patch)) && FailSoft.IsApplied(nameof(BillRadius3D_Prune_Patch)) && FailSoft.IsApplied(nameof(BillRadius3D_Ring_Patch));
            Result("P0 patches: the search prefix, the entry-test wrap and the ring landed", ok && BillRadius3D_Prune_Patch.Stores >= 1,
                $"search {FailSoft.IsApplied(nameof(BillRadius3D_Search_Patch))}, prune {FailSoft.IsApplied(nameof(BillRadius3D_Prune_Patch))} (entry-test stores wrapped {BillRadius3D_Prune_Patch.Stores}), ring {FailSoft.IsApplied(nameof(BillRadius3D_Ring_Patch))}; IsoRim {IsoRimBridge.Present}, 3D view {IsoRimBridge.View3DOn}");
            if (annex == null)
            {
                Info($"billradius: map {map.Size} without floors: the map-without-floors check only; area {r2Area}, crafter {r2P}");
                stage = "br_flat";
                return;
            }
            Info($"billradius: area {r2Area}, crafter {r2P}, map {map.Size}, groundZ {annex.groundZ}, stride {annex.stride}, levels {Levels.Min(map)}..{Levels.Max(map)}, storey {Dist3D.StoreyCells:0.00} cells");
            if (annex.MaxLevel < 4)
            {
                Result("billradius: a map with 5 levels", false, $"levels {Levels.Min(map)}..{Levels.Max(map)} (run with -projectzfloors=5 -projectzmapsize=250)");
                Finish();
                return;
            }
            stage = "br_1";
        }

        // Floors 1..up over ground (8..31, 12..23); stairs ground -> 1 at (10, 9) north, then L -> L+1 eastward on each floor
        // at x = 13, 19, 25 (row 16). P on the ground at home, its work set to 'types'.
        private static void BrScene(Map map, int seed, int up, params WorkTypeDef[] types)
        {
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            Rand.PushState(FixtureSeed($"{mode}_{seed}"));
            try
            {
                for (int L = 1; L <= up; L++)
                {
                    R2Floor(map, new CellRect(RG(8, 12).x, RG(8, 12).z, 24, 12), L);
                }
                r2SA = R2Stairs(map, RG(10, 9), Rot4.North);
                List<string> bad = new List<string>();
                if (r2SA == null)
                {
                    bad.Add("ground stairs");
                }
                for (int L = 1; L < up; L++)
                {
                    IntVec3 c = RL(13 + 6 * (L - 1), 16, L, map);
                    RpOpenWell(map, c, Rot4.East);
                    AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, c, Rot4.East, map, stuffDef: ThingDefOf.WoodLog);
                    if (ok.Accepted)
                    {
                        Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, c, map, Rot4.East);
                    }
                    else
                    {
                        bad.Add($"level {L} stairs at {c}: {ok.Reason}");
                    }
                }
                if (bad.Count > 0)
                {
                    Info("scene: stairs not placed: " + string.Join("; ", bad));
                }
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                Teleport(r2P, RG(14, 8));
                Draft(r2P, false);
                R2Work(r2P, types);
                r2P.inventory?.innerContainer?.ClearAndDestroyContents();
            }
            finally
            {
                Rand.PopState();
            }
            brFirst = brCarried = null;
            brUp = brFar = brGround = brItem = null;
            brStand = null;
        }

        private static Thing BrChunk(IntVec3 c, Map map)
        {
            Thing t = GenSpawn.Spawn(ThingMaker.MakeThing(BrChunkDef), c, map);
            t.SetForbidden(false, false);
            return t;
        }

        // A stonecutter's table with a granite-block bill of radius r.
        private static Thing BrStonecutter(IntVec3 c, Map map, float r)
        {
            Thing b = Place(DefDatabase<ThingDef>.GetNamed("TableStonecutter"), ThingDefOf.WoodLog, c, map, Rot4.South);
            brBill = (Bill_Production)DefDatabase<RecipeDef>.GetNamed("Make_StoneBlocksGranite").MakeNewBill();
            brBill.ingredientSearchRadius = r;
            ((Building_WorkTable)b).BillStack.AddBill(brBill);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            return b;
        }

        // A fuelled campfire with a burn-apparel bill of radius r.
        private static Thing BrCampfire(IntVec3 c, Map map, float r)
        {
            Thing b = Place(DefDatabase<ThingDef>.GetNamed("Campfire"), null, c, map, Rot4.North);
            CompRefuelable f = b.TryGetComp<CompRefuelable>();
            f?.Refuel(f.Props.fuelCapacity);
            brBill = (Bill_Production)DefDatabase<RecipeDef>.GetNamed("BurnApparel").MakeNewBill();
            brBill.ingredientSearchRadius = r;
            ((Building_WorkTable)b).BillStack.AddBill(brBill);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            return b;
        }

        private static Building_OutfitStand BrStand(IntVec3 c, Map map, out Apparel shirt)
        {
            Building_OutfitStand s = (Building_OutfitStand)Place(DefDatabase<ThingDef>.GetNamed("Building_OutfitStand"), ThingDefOf.WoodLog, c, map, Rot4.South);
            ThingDef def = DefDatabase<ThingDef>.GetNamed("Apparel_BasicShirt");
            shirt = (Apparel)ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));
            AccessTools.Field(typeof(Building_OutfitStand), "allowRemovingItems").SetValue(s, true);   // (the player's "allow removing items" toggle: a haul source only then)
            bool added = s.AddApparel(shirt);
            Info($"outfit stand at {BrWhere(c, map)}: shirt added {added}, holds {((IThingHolder)s).GetDirectlyHeldThings().Count}");
            return s;
        }

        private static string BrWhere(IntVec3 c, Map map) => $"{c} (level {Levels.LevelOf(c, map)}, ground {Levels.Ground(c, map)})";

        private static string BrName(Thing t, Map map)
        {
            if (t == null)
            {
                return "none";
            }
            string n = t == brUp ? "UP" : t == brFar ? "FAR" : t == brGround ? "GROUND" : t == brItem ? "ITEM" : t.LabelShort;
            return n + " at " + BrWhere(t.PositionHeld, map);
        }

        // The game's bill search for the crafter, patch off (control) or on.
        private static string BrAsk(Map map, bool off)
        {
            bool was = BillRadius3D.Off;
            BillRadius3D.Off = off;
            try
            {
                List<ThingCount> chosen = new List<ThingCount>();
                List<IngredientCount> missing = new List<IngredientCount>();
                bool ok = (bool)BrFindM.Invoke(null, new object[] { brBill, r2P, brBench, chosen, missing });
                return ok ? string.Join(", ", chosen.Select(c => BrName(c.Thing, map))) : "none";
            }
            finally
            {
                BillRadius3D.Off = was;
            }
        }

        // Diagnostics: the search's root region, its neighbours (type, entry, passable), what a plain flood from it
        // processes, and the haul sources.
        private static string BrDiag(Map map)
        {
            IntVec3 root = brBench.def.hasInteractionCell ? brBench.InteractionCell : brBench.Position;
            Region rr = root.GetRegion(map);
            if (rr == null)
            {
                return $"diag: root {root} has no region";
            }
            TraverseParms tp = TraverseParms.For(r2P);
            string nb = string.Join(", ", rr.Neighbors.Select(n => $"{n.id}:{n.type}/{(n.Allows(tp, false) ? "allow" : "no")}/{n.extentsClose}"));
            int processed = 0;
            List<string> got = new List<string>();
            RegionTraverser.BreadthFirstTraverse(rr, (Region f, Region r) => r.Allows(tp, false), r =>
            {
                processed++;
                foreach (Thing t in r.ListerThings.ThingsMatching(ThingRequest.ForGroup(ThingRequestGroup.HaulableEver)))
                {
                    got.Add(t.LabelShort + "@" + t.Position);
                }
                return processed >= 12;
            }, 60);
            string hs = string.Join(", ", map.haulDestinationManager.AllHaulSourcesListForReading.Select(h => h is Thing th ? $"{th.def.defName}@{th.Position} enabled {h.HaulSourceEnabled} forbidden {th.IsForbidden(r2P)}" : h.ToString()));
            string item = brItem == null ? "" : $"; item {brItem.LabelShort}: allowed {brBill.IsFixedOrAllowedIngredient(brItem)}, forbidden {brItem.IsForbidden(r2P)}, reserve {r2P.CanReserve(brItem)}";
            return $"diag: root {root} region {rr.id} {rr.type} {rr.extentsClose}, neighbours [{nb}]; first 12 regions of a flood: haulables [{string.Join(", ", got.Take(12))}]; haul sources [{hs}]{item}";
        }

        // Ticks until the crafter carries an ingredient of its DoBill job (the first ingredient target is kept).
        private static void BrWatch(int maxTicks, string next, int seed)
        {
            brFirst = brCarried = null;
            R2Mark();
            nextTickSeed = FixtureSeed($"{mode}_ticks_{seed}");
            TickUntil(maxTicks, () =>
            {
                Job j = r2P.CurJob;
                if (brFirst == null && j != null && j.def == JobDefOf.DoBill)
                {
                    // (the driver moves the queue's first ingredient into target B as it starts)
                    brFirst = j.targetQueueB != null && j.targetQueueB.Count > 0 ? j.targetQueueB[0].Thing : j.targetB.Thing != brBench ? j.targetB.Thing : null;
                }
                Thing held = r2P.carryTracker.CarriedThing;
                if (held != null && j?.def == JobDefOf.DoBill)
                {
                    brCarried = held;
                    return true;
                }
                return false;
            }, next, 5);
        }

        private static string BrJob() => $"first DoBill ingredient {BrName(brFirst, Find.CurrentMap)}, carried {BrName(brCarried, Find.CurrentMap)}, crafter at {BrWhere(r2P.Position, Find.CurrentMap)}, job {r2P.CurJobDef?.defName}";

        private static void BrEnd(Map map)
        {
            r2P.jobs.StopAll();
            r2P.carryTracker?.innerContainer?.ClearAndDestroyContents();
        }

        // ------------------------------------------------------------------------------------------------ B1

        private static void BrB1(Map map)
        {
            BrScene(map, BrFix + 1, 1, WorkTypeDefOf.Crafting);
            brBench = BrStonecutter(RG(12, 13), map, 10f);
            brUp = BrChunk(RL(12, 14, 1, map), map);
            brFar = BrChunk(RG(40, 4), map);
            brChoice = $"bench {BrWhere(brBench.Position, map)} r 10; UP 3D {Dist3D.Distance(brBench.Position, brUp.Position, map):0.0} (flat {(brUp.Position - brBench.Position).LengthHorizontal:0}), FAR {Dist3D.Distance(brBench.Position, brFar.Position, map):0.0}; search: control (patch off) [{BrAsk(map, true)}], patch on [{BrAsk(map, false)}]";
            Info("B1 " + brChoice);
            Info("B1 " + BrDiag(map));
            BrWatch(4000, "br_1_check", BrFix + 11);
        }

        private static void BrB1Check(Map map)
        {
            tickWatch = null;
            Result("B1 ground bench, radius 10: the crafter fetches the chunk one floor up (3D 6.3), not the ground one 29 cells off",
                brFirst == brUp && brCarried == brUp && climbsColonists - r2ClimbsAt >= 1,
                $"{brChoice}; {BrJob()}; {R2Stats()}");
            BrEnd(map);
            stage = "br_2";
        }

        // ------------------------------------------------------------------------------------------------ B2

        private static void BrB2(Map map)
        {
            BrScene(map, BrFix + 2, 1, WorkTypeDefOf.Crafting);
            brBench = BrStonecutter(RG(12, 13), map, 6f);
            brUp = BrChunk(RL(14, 13, 1, map), map);
            brGround = BrChunk(RG(12, 10), map);
            brChoice = $"bench {BrWhere(brBench.Position, map)} r 6; UP 3D {Dist3D.Distance(brBench.Position, brUp.Position, map):0.0} (ground offset 2), GROUND {Dist3D.Distance(brBench.Position, brGround.Position, map):0.0}; search: control [{BrAsk(map, true)}], patch on [{BrAsk(map, false)}]";
            Info("B2 " + brChoice);
            Info("B2 " + BrDiag(map));
            BrWatch(3000, "br_2_check", BrFix + 21);
        }

        private static void BrB2Check(Map map)
        {
            tickWatch = null;
            Result("B2 radius 6: the chunk one floor up 2 cells off (3D 7.3) is left, the ground one 3 cells off is used",
                brFirst == brGround && brCarried == brGround && brUp.Spawned && Levels.LevelOf(brUp.Position, map) == 1,
                $"{brChoice}; {BrJob()}; UP still upstairs {brUp.Spawned}; {R2Stats()}");
            BrEnd(map);
            stage = "br_3";
        }

        // ------------------------------------------------------------------------------------------------ B3

        private static void BrB3(Map map)
        {
            BrScene(map, BrFix + 3, 1, WorkTypeDefOf.Crafting);
            brBench = BrStonecutter(RL(12, 17, 1, map), map, 10f);
            brGround = BrChunk(RG(12, 15), map);
            brFar = BrChunk(RL(30, 22, 1, map), map);
            Teleport(r2P, RL(20, 14, 1, map));
            brChoice = $"bench {BrWhere(brBench.Position, map)} r 10; GROUND (under it) 3D {Dist3D.Distance(brBench.Position, brGround.Position, map):0.0}, FAR (same floor) {Dist3D.Distance(brBench.Position, brFar.Position, map):0.0}; search: control [{BrAsk(map, true)}], patch on [{BrAsk(map, false)}]";
            Info("B3 " + brChoice);
            Info("B3 " + BrDiag(map));
            BrWatch(4000, "br_3_check", BrFix + 31);
        }

        private static void BrB3Check(Map map)
        {
            tickWatch = null;
            Result("B3 bench upstairs, radius 10: the crafter fetches the ground chunk under it, not the one on its own floor 19 cells off",
                brFirst == brGround && brCarried == brGround && climbsColonists - r2ClimbsAt >= 1,
                $"{brChoice}; {BrJob()}; {R2Stats()}");
            BrEnd(map);
            stage = "br_4";
        }

        // ------------------------------------------------------------------------------------------------ B4

        private static void BrB4(Map map)
        {
            BrScene(map, BrFix + 4, 4, WorkTypeDefOf.Crafting);
            brBench = BrStonecutter(RG(12, 13), map, 999f);
            brUp = BrChunk(RL(29, 19, 4, map), map);
            bool reach = r2P.CanReach(brUp, PathEndMode.ClosestTouch, Danger.Deadly);
            brChoice = $"bench {BrWhere(brBench.Position, map)} Unlimited; UP on level 4 {BrWhere(brUp.Position, map)}: flat {(brUp.Position - brBench.Position).LengthHorizontal:0} rows, 3D {Dist3D.Distance(brBench.Position, brUp.Position, map):0.0}, reachable {reach}; search: control [{BrAsk(map, true)}], patch on [{BrAsk(map, false)}]";
            Info("B4 " + brChoice);
            Info("B4 " + BrDiag(map));
            BrWatch(9000, "br_4_check", BrFix + 41);
        }

        private static void BrB4Check(Map map)
        {
            tickWatch = null;
            Result("B4 Unlimited: the only chunk, on level 4 (over 999 rows north), is fetched",
                brFirst == brUp && brCarried == brUp && climbsColonists - r2ClimbsAt >= 4,
                $"{brChoice}; {BrJob()}; {R2Stats()}");
            BrEnd(map);
            stage = "br_5a";
        }

        // ------------------------------------------------------------------------------------------------ B5

        // A campfire in the ground's north rows, an outfit stand on level 1 over ground row 2: the stand is 10 rows north
        // of the campfire across the band gap (the game's flat test: inside 12), and 245 ground rows plus a storey in 3D.
        private static void BrB5a(Map map)
        {
            BrScene(map, BrFix + 5, 1, WorkTypeDefOf.Hauling);
            AnnexMapComponent an = ProjectZ.Annex(map);
            int x0 = r2Area.minX + 5;
            IntVec3 fire = new IntVec3(x0, 0, an.groundZ - 3);
            IntVec3 standG = new IntVec3(x0, 0, 2);   // ground row 2, under the stand on level 1
            BrClearSpot(map, new CellRect(x0 - 4, an.groundZ - 8, 9, 8), 0);
            BrClearSpot(map, new CellRect(x0 - 2, 0, 5, 6), 1);
            R2Floor(map, new CellRect(x0 - 2, 0, 5, 6), 1);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            brBench = BrCampfire(fire, map, 12f);
            IntVec3 standAt = Levels.Cell(standG, 1, map);
            brStand = BrStand(standAt, map, out Apparel shirt);
            brItem = shirt;
            Teleport(r2P, new IntVec3(x0 + 2, 0, an.groundZ - 6));
            brChoice = $"campfire {BrWhere(fire, map)} r 12; stand {BrWhere(standAt, map)}: flat {(standAt - fire).LengthHorizontal:0.0}, 3D {Dist3D.Distance(fire, standAt, map):0.0}; search: control [{BrAsk(map, true)}], patch on [{BrAsk(map, false)}]";
            Info("B5a " + brChoice);
            Info("B5a " + BrDiag(map));
            BrWatch(1500, "br_5a_check", BrFix + 51);
        }

        private static void BrClearSpot(Map map, CellRect rect, int L)
        {
            foreach (IntVec3 g in rect.ClipInsideMap(map))
            {
                IntVec3 c = Levels.Cell(g, L, map);
                if (!c.IsValid || !c.InBounds(map))
                {
                    continue;
                }
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t is Pawn p)
                    {
                        if (p.IsColonist)
                        {
                            Teleport(p, R2ParkCell(map, 0));
                        }
                        else
                        {
                            p.Destroy();
                        }
                    }
                    else if (!t.Destroyed)
                    {
                        t.Destroy();
                    }
                }
                map.terrainGrid.SetTerrain(c, L == 0 ? TerrainDefOf.Soil : PZDefOf.PZ_OpenAir);
                map.roofGrid.SetRoof(c, null);
                if (map.fogGrid.IsFogged(c))
                {
                    map.fogGrid.Unfog(c);
                }
            }
        }

        private static void BrB5aCheck(Map map)
        {
            tickWatch = null;
            bool inStand = brItem != null && !brItem.Destroyed && brItem.ParentHolder == brStand;
            Result("B5a outfit stand across the band gap (flat 10, 3D ~250): its shirt is not used for the campfire's radius-12 bill",
                brFirst == null && brCarried == null && inStand,
                $"{brChoice}; {BrJob()}; shirt still in the stand {inStand}; {R2Stats()}");
            BrEnd(map);
            BrClearSpot(map, new CellRect(r2Area.minX, 0, 10, 6), 1);
            BrClearSpot(map, new CellRect(r2Area.minX, ProjectZ.Annex(map).groundZ - 8, 12, 8), 0);
            stage = "br_5b";
        }

        private static void BrB5b(Map map)
        {
            BrScene(map, BrFix + 6, 1, WorkTypeDefOf.Hauling);
            brBench = BrCampfire(RG(12, 13), map, 10f);
            brStand = BrStand(RL(12, 15, 1, map), map, out Apparel shirt);
            brItem = shirt;
            brChoice = $"campfire {BrWhere(brBench.Position, map)} r 10; stand {BrWhere(brStand.Position, map)}: 3D {Dist3D.Distance(brBench.Position, brStand.Position, map):0.0} (flat {(brStand.Position - brBench.Position).LengthHorizontal:0}); search: control [{BrAsk(map, true)}], patch on [{BrAsk(map, false)}]";
            Info("B5b " + brChoice);
            Info("B5b " + BrDiag(map));
            BrWatch(4000, "br_5b_check", BrFix + 61);
        }

        private static void BrB5bCheck(Map map)
        {
            tickWatch = null;
            Result("B5b outfit stand straight upstairs (3D 7.3): its shirt is taken for the radius-10 bill",
                brFirst == brItem && brCarried == brItem && climbsColonists - r2ClimbsAt >= 1,
                $"{brChoice}; {BrJob()}; {R2Stats()}");
            BrEnd(map);
            stage = "br_6";
        }

        // ------------------------------------------------------------------------------------------------ B6

        // Seeded one-floor layouts: a bench, 5 chunks, a radius from {4, 8, 15, Unlimited}; the search's choice with the
        // patch on and off. L = 0 (floors above, nothing there) and L = 1 (level 1 alone, no stairs).
        private static int BrLayouts(Map map, int L, int seed0, int count, List<string> diff)
        {
            int same = 0;
            float[] radii = { 4f, 8f, 15f, 999f };
            for (int s = 0; s < count; s++)
            {
                BrScene(map, seed0 + s, L == 0 ? 1 : 0, WorkTypeDefOf.Crafting);
                if (L == 1)
                {
                    R2Floor(map, new CellRect(r2Area.minX + 1, r2Area.minZ + 1, r2Area.Width - 2, r2Area.Height - 2), 1);
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                }
                Rand.PushState(FixtureSeed($"{mode}_{seed0 + 1000 + s}"));
                try
                {
                    IntVec3 Rnd() => Levels.Cell(new IntVec3(r2Area.minX + Rand.RangeInclusive(4, r2Area.Width - 5), 0, r2Area.minZ + Rand.RangeInclusive(3, r2Area.Height - 4)), L, map);
                    IntVec3 bc = Rnd();
                    brBench = BrStonecutter(bc, map, radii[s % radii.Length]);
                    Teleport(r2P, Levels.Cell(new IntVec3(r2Area.minX + 3, 0, r2Area.minZ + 3), L, map));
                    for (int i = 0; i < 5; i++)
                    {
                        IntVec3 c = Rnd();
                        if (c.Standable(map) && c.GetFirstItem(map) == null && c.GetEdifice(map) == null)
                        {
                            BrChunk(c, map);
                        }
                    }
                    string a = BrAsk(map, true), b = BrAsk(map, false);
                    if (a == b)
                    {
                        same++;
                    }
                    else if (diff.Count < 5)
                    {
                        diff.Add($"L{L} s{s} r{brBill.ingredientSearchRadius}: off [{a}] on [{b}]");
                    }
                }
                finally
                {
                    Rand.PopState();
                }
            }
            return same;
        }

        private static void BrB6(Map map)
        {
            List<string> diff = new List<string>();
            int searches0 = BillRadius3D.Searches;
            int same = BrLayouts(map, 0, BrFix + 100, 20, diff) + BrLayouts(map, 1, BrFix + 200, 20, diff);
            Result("B6 one floor: on 40 seeded layouts (20 ground, 20 on level 1) the bill search picks the same with the patch on and off",
                same == 40, $"identical {same}/40, 3D searches {BillRadius3D.Searches - searches0}; {string.Join(" | ", diff)}");
            stage = "br_7";
        }

        // ------------------------------------------------------------------------------------------------ B7

        // A busy five-floor scene: 150 granite chunks over levels 0..4; the bill search timed (radius 10, Unlimited, and a
        // marble bill with no marble anywhere: the search runs out), patch off and on in alternating rounds.
        private static void BrB7(Map map)
        {
            BrScene(map, BrFix + 7, 4, WorkTypeDefOf.Crafting);
            brBench = BrStonecutter(RG(12, 13), map, 10f);
            Rand.PushState(FixtureSeed($"{mode}_{BrFix + 700}"));
            int spawned = 0;
            try
            {
                for (int i = 0; i < 150; i++)
                {
                    IntVec3 c = Levels.Cell(new IntVec3(r2Area.minX + Rand.RangeInclusive(1, r2Area.Width - 2), 0, r2Area.minZ + Rand.RangeInclusive(1, r2Area.Height - 2)), i % 5, map);
                    if (c.IsValid && c.Standable(map) && c.GetFirstItem(map) == null && c.GetEdifice(map) == null)
                    {
                        BrChunk(c, map);
                        spawned++;
                    }
                }
            }
            finally
            {
                Rand.PopState();
            }
            Bill_Production granite = brBill;
            Bill_Production marble = (Bill_Production)DefDatabase<RecipeDef>.GetNamed("Make_StoneBlocksMarble").MakeNewBill();
            ((Building_WorkTable)brBench).BillStack.AddBill(marble);
            (string name, Bill_Production bill, float r)[] cases = { ("granite r10", granite, 10f), ("granite Unlimited", granite, 999f), ("marble r10 (none)", marble, 10f), ("marble Unlimited (none)", marble, 999f) };
            const int N = 300, Rounds = 6;
            List<string> lines = new List<string>();
            double worst = 0;
            foreach (var cs in cases)
            {
                cs.bill.ingredientSearchRadius = cs.r;
                brBill = cs.bill;
                double[] ms = new double[2];
                for (int round = 0; round < Rounds; round++)
                {
                    bool off = round % 2 == 0;
                    BillRadius3D.Off = off;
                    try
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        for (int i = 0; i < N; i++)
                        {
                            BrFindM.Invoke(null, new object[] { brBill, r2P, brBench, new List<ThingCount>(), new List<IngredientCount>() });
                        }
                        sw.Stop();
                        if (round >= 2)   // (the first pair warms up)
                        {
                            ms[off ? 0 : 1] += sw.Elapsed.TotalMilliseconds;
                        }
                    }
                    finally
                    {
                        BillRadius3D.Off = false;
                    }
                }
                double perOff = ms[0] * 1000.0 / (N * (Rounds - 2) / 2), perOn = ms[1] * 1000.0 / (N * (Rounds - 2) / 2);
                worst = Math.Max(worst, perOn - perOff);
                lines.Add($"{cs.name}: off {perOff:0.0} us, on {perOn:0.0} us (+{perOn - perOff:0.0})");
            }
            Info($"B7 {spawned} chunks over 5 levels, wrap counters: regions cut {BillRadius3D.RegionsCut}, stairs let through {BillRadius3D.StairPasses}, things cut {BillRadius3D.ThingsCut}, reach cut {BillRadius3D.ReachCut}");
            Result("B7 timing: the bill search on a busy five-floor map costs at most 0.1 ms more per call with the patch on",
                worst <= 100.0, string.Join("; ", lines));
            ((Building_WorkTable)brBench).BillStack.Delete(marble);
            brBill = granite;
            brShot = 0;
            stage = "br_8";
        }

        // ------------------------------------------------------------------------------------------------ B8: ring shots

        // ring3d_floor0/1: IsoRim's 3D view (ProjectZ locks it on) on the bench floor and the floor above; ring2d_floor0/1:
        // the flat view, the 3D view turned off for these two shots only (test only: unlocked, then locked on again).
        private static readonly string[] BrShots = { "ring3d_floor0", "ring3d_floor1", "ring2d_floor0", "ring2d_floor1" };

        private static void BrView3D(bool on)
        {
            Type v = AccessTools.TypeByName("IsoRim.View3D");
            if (v == null)
            {
                return;
            }
            if (!on)
            {
                AccessTools.Field(v, "LockedOn").SetValue(null, false);
                AccessTools.Field(v, "Active").SetValue(null, false);
            }
            else
            {
                AccessTools.Field(v, "Active").SetValue(null, true);
                AccessTools.Field(v, "LockedOn").SetValue(null, true);
            }
        }

        private static void BrB8(Map map)
        {
            if (brShot == 0)
            {
                BrScene(map, BrFix + 8, 2, WorkTypeDefOf.Crafting);
                brBench = BrStonecutter(RG(14, 16), map, 15f);
                Draft(r2P, true);
                brDialog = new Dialog_BillConfig(brBill, brBench.Position);
                Find.WindowStack.Add(brDialog);
            }
            if (brShot >= BrShots.Length)
            {
                stage = "br_8_done";
                return;
            }
            int floor = brShot % 2;
            bool flat = brShot >= 2;
            IntVec3 g = new IntVec3(RG(14, 16).x + 8, 0, RG(14, 16).z);
            if (!flat)
            {
                ShotAt(map, floor, g, 42f);
            }
            else
            {
                BrView3D(false);
                FloorView.Set(0);
                CameraDriver_Update_Patch.AllowAnnexView = true;
                Find.Selector.ClearSelection();
                IntVec3 c = Levels.Cell(g, floor, map);
                Find.CameraDriver.SetRootPosAndSize(new Vector3(c.x + 0.5f, 0f, c.z + 0.5f), 42f);
            }
            if (brDialog != null)
            {
                brDialog.windowRect.x = UI.screenWidth - brDialog.windowRect.width * 0.45f;
                brDialog.windowRect.y = 10f;
            }
            brRingAt = BillRadius3D_Ring_Patch.Drawn;
            Wait(40, "br_8_take");
        }

        private static void BrB8Take(Map map)
        {
            string name = BrShots[brShot];
            int added = BillRadius3D_Ring_Patch.Drawn - brRingAt;
            bool view = IsoRimBridge.View3DOn;
            Info($"shot {name}: 3D view {view}, dialog open {Find.WindowStack.IsOpen(typeof(Dialog_BillConfig))}, rings added on other floors over 40 frames {added}, view floor {FloorView.Current}");
            if (brShot == 0)
            {
                Result("B8 ring " + name + ": 3D view on the bench floor: the game's ring only (no added ring)", view && added == 0, $"3D view {view}, rings added {added}");
            }
            else if (brShot == 1)
            {
                Result("B8 ring " + name + ": 3D view on the floor above: its cross-section ring drawn (9.7 cells)", view && added > 0 && added <= 41, $"3D view {view}, rings added {added} (one a frame)");
            }
            else
            {
                Result("B8 ring " + name + ": flat view: the ring on every other floor in reach drawn (floor 1: 9.7 cells, floor 2: 4.4)", !view && added >= 2 * 35, $"3D view {view}, rings added {added} (two a frame)");
            }
            ZCapture(name);
            brShot++;
            Wait(5, "br_8");
        }

        private static void BrB8Done(Map map)
        {
            Find.WindowStack.TryRemove(typeof(Dialog_BillConfig), doCloseSound: false);
            CameraDriver_Update_Patch.AllowAnnexView = false;
            BrView3D(true);
            FloorView.Set(0);
            Finish();
        }

        // ------------------------------------------------------------------------------------------------ B6 flat map

        private static void BrFlat(Map map)
        {
            int searches0 = BillRadius3D.Searches;
            List<string> diff = new List<string>();
            int same = BrLayouts(map, 0, BrFix + 300, 20, diff);
            Result("B6 map without floors: on 20 seeded layouts the bill search picks the same with the patch on and off, the patch inactive",
                same == 20 && BillRadius3D.Searches == searches0, $"identical {same}/20, 3D searches {BillRadius3D.Searches - searches0}; {string.Join(" | ", diff)}");
            // a real job: radius 8, a chunk 5 cells off and one 12 cells off
            BrScene(map, BrFix + 301, 0, WorkTypeDefOf.Crafting);
            brBench = BrStonecutter(RG(12, 13), map, 8f);
            brGround = BrChunk(RG(17, 13), map);
            brFar = BrChunk(RG(24, 13), map);
            brChoice = $"bench {brBench.Position} r 8; GROUND 5 cells, FAR 12; search: control [{BrAsk(map, true)}], patch on [{BrAsk(map, false)}]";
            Info("B6 flat " + brChoice);
            BrWatch(3000, "br_flat_check", BrFix + 302);
        }

        private static void BrFlatCheck(Map map)
        {
            tickWatch = null;
            Result("B6 map without floors: a real job takes the chunk within the radius (as the game)",
                brFirst == brGround && brCarried == brGround, $"{brChoice}; {BrJob()}");
            BrEnd(map);
            Finish();
        }
    }
}
