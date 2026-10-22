using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace ProjectZ.Dev
{
    // Mode outcroptop (playtest 3 #7, user rule 2026-10-23): a rock mined under a bare natural top takes the top with it
    // (BareTop, Mineable_MinedMarked_Patch); rock above keeps its roof; a top with something on it stays. The same fixed-seed
    // map and arguments as mineup (-projectzfloors=4 -projectzmapsize=250 -projectztallmountains). Setup finds, from a fixed
    // point, on the ground (each check its own site, 12+ cells apart):
    //   K a lone outcrop: a 4-way connected group of 2..12 natural rock cells (ore counts), every one under a bare level-1
    //     top, nothing else natural within 2 cells, beside open reachable ground;
    //   O1, O2, O3 single rock cells (ore first) under bare level-1 tops at a face beside open reachable ground;
    //   I a ground rock cell with level-1 rock above it at a face of a taller mountain.
    // The game is saved (pz_outcroptop); every check reloads it, puts one miner on the open ground beside its site, marks the
    // rock with vanilla Mine (the real designator) and runs the game's own mining job under fixed seeds:
    //   T1 O1 mined: its top is open air, its cell under the sky (no roof), the yield on the ground; shots before (rule off) / after
    //   T2 O2 with wood on its top: the top stays, the wood stays on it, the roof under it stays
    //   T3 O3 with a drafted colonist standing on its top: the top stays, the colonist does not fall
    //   T4 I (rock above): the level-1 rock stays, the thick roof stays
    //   T5 K mined out whole: every top open, no collapse
    //   T6 (report, the rule off as before): K mined out whole: what is left of its tops (floats or collapses)
    // Each check ends with terrain, roofs and edifices outside its site unchanged. Shots: ot_*.png.
    public static partial class TestRunner
    {
        private static readonly List<IntVec3> otK = new List<IntVec3>();
        private static IntVec3 otO1, otO2, otO3, otI, otKS, otS1, otS2, otS3, otSI;
        private static int otOpened0, otCollapses0;
        private static int otMinerId = -1, otStandId = -1;

        private static bool OtRock(IntVec3 g, Map map) => g.InBounds(map) && g.GetFirstMineable(map) is Mineable m && m.def.building.isNaturalRock && m.def != ThingDefOf.CollapsedRocks;

        private static bool OtBareTop(IntVec3 g, Map map) => Column.NaturalTop(g, map) == 1 && TmTop(L1(g, map), map) && L1(g, map).GetThingList(map).Count(t => !(t is Plant) && t.def.category != ThingCategory.Filth) == 0;

        private static bool OtStand(IntVec3 s, Map map) => MuOpen(s, map) && map.reachability.CanReachMapEdge(s, TraverseParms.For(TraverseMode.PassDoors));

        // An open reachable ground cell beside g (4-way), or Invalid.
        private static IntVec3 OtSide(IntVec3 g, Map map)
        {
            foreach (IntVec3 d in GenAdj.CardinalDirections)
            {
                if (OtStand(g + d, map) && !map.fogGrid.IsFogged(g))
                {
                    return g + d;
                }
            }
            return IntVec3.Invalid;
        }

        private static bool OtFar(IntVec3 g, IEnumerable<IntVec3> taken) => taken.All(t => !t.IsValid || !g.InHorDistOf(t, 12f));

        private static bool OtSites(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            IntVec3 home = new IntVec3(map.Size.x / 2, 0, a.groundZ / 2);
            List<IntVec3> near = Near(home, 120, map).Where(g => g.x >= 6 && g.z >= 6 && g.x < map.Size.x - 6 && g.z < a.groundZ - 6).ToList();
            // K: a lone outcrop
            otK.Clear();
            for (int minSize = 2; minSize >= 1 && otK.Count == 0; minSize--)
            {
            HashSet<IntVec3> seen = new HashSet<IntVec3>();
            foreach (IntVec3 g in near)
            {
                if (seen.Contains(g) || !OtRock(g, map))
                {
                    continue;
                }
                List<IntVec3> group = new List<IntVec3>();
                Queue<IntVec3> q = new Queue<IntVec3>();
                q.Enqueue(g);
                seen.Add(g);
                while (q.Count > 0 && group.Count <= 12)
                {
                    IntVec3 c = q.Dequeue();
                    group.Add(c);
                    foreach (IntVec3 d in GenAdj.CardinalDirections)
                    {
                        IntVec3 n = c + d;
                        if (!seen.Contains(n) && OtRock(n, map))
                        {
                            seen.Add(n);
                            q.Enqueue(n);
                        }
                    }
                }
                if (q.Count > 0 || group.Count < minSize || group.Count > 12 || !group.All(c => OtBareTop(c, map) && !map.fogGrid.IsFogged(c)))
                {
                    continue;
                }
                // no other rock touching it, diagonals too (the whole outcrop goes)
                bool lone = group.All(c => GenAdj.AdjacentCells.All(o => !(c + o).InBounds(map) || group.Contains(c + o) || (c + o).GetFirstMineable(map) == null));
                IntVec3 s = group.Select(c => OtSide(c, map)).FirstOrDefault(x => x.IsValid);
                if (lone && s.IsValid)
                {
                    otK.AddRange(group);
                    otKS = s;
                    break;
                }
            }
            }
            // O1..O3: single rock cells (ore first) under bare level-1 tops beside open reachable ground
            List<IntVec3> taken = new List<IntVec3>(otK);
            IntVec3 Pick(bool ore, out IntVec3 side)
            {
                side = IntVec3.Invalid;
                foreach (IntVec3 g in near)
                {
                    Thing m = g.GetFirstMineable(map);
                    if (!OtRock(g, map) || ore != m.def.building.isResourceRock || !OtBareTop(g, map) || !OtFar(g, taken))
                    {
                        continue;
                    }
                    IntVec3 s = OtSide(g, map);
                    if (s.IsValid)
                    {
                        side = s;
                        taken.Add(g);
                        return g;
                    }
                }
                return IntVec3.Invalid;
            }
            otO1 = Pick(true, out otS1);
            if (!otO1.IsValid)
            {
                otO1 = Pick(false, out otS1);
            }
            otO2 = Pick(false, out otS2);
            otO3 = Pick(false, out otS3);
            // I: ground rock under level-1 rock at a face
            otI = IntVec3.Invalid;
            foreach (IntVec3 g in near)
            {
                if (OtRock(g, map) && Column.NaturalTop(g, map) >= 2 && OtRock(L1(g, map), map) && OtFar(g, taken))
                {
                    IntVec3 s = OtSide(g, map);
                    if (s.IsValid)
                    {
                        otI = g;
                        otSI = s;
                        break;
                    }
                }
            }
            return otK.Count > 0 && otO1.IsValid && otO2.IsValid && otO3.IsValid && otI.IsValid;
        }

        private static Pawn OtPawn(Map map, int id) => map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == id);

        // One miner on the open ground at s, the rest drafted; vanilla Mine on the cells.
        private static void OtMine(Map map, IEnumerable<IntVec3> cells, IntVec3 s, bool ruleOff)
        {
            BareTop.Off = ruleOff;
            MuStart(map, MuRect(cells.Concat(new[] { s }), map, 2), false);
            otOpened0 = BareTop.Opened;
            otCollapses0 = PZStats.slabCollapses;
            Pawn miner = OtPawn(map, otMinerId);
            Draft(miner, false);
            MdWork(map, new List<Pawn> { miner }, WorkTypeDefOf.Mining);
            Teleport(miner, s);
            new Designator_Mine().DesignateMultiCell(cells);
            Info($"mining {string.Join(" ", cells)} from {s} by {miner} (rule {(ruleOff ? "OFF" : "on")}); marked {cells.Count(c => map.designationManager.DesignationAt(c, DesignationDefOf.Mine) != null)}");
        }

        private static string OtTop(IntVec3 g, Map map)
        {
            IntVec3 u = L1(g, map);
            return $"{g}: rock {(g.GetFirstMineable(map)?.def.defName ?? "-")}, roof {map.roofGrid.RoofAt(g)?.defName ?? "none"}, top {u.GetTerrain(map)?.defName}{(OtRock(u, map) ? " (rock)" : "")}";
        }

        private static void OtEnd(Map map, string check, string next)
        {
            BareTop.Off = false;
            MuOutside(map, check);
            muPointer = IntVec3.Invalid;
            Find.UIRoot.screenshotMode.Active = false;
            FloorView.Set(0);
            Find.DesignatorManager.Deselect();
            if (next == null)
            {
                Finish();
                return;
            }
            stage = next;
            pendingLoad = "pz_outcroptop";
        }

        private static bool StepOutcropTop(Map map)
        {
            switch (stage)
            {
                case "ot_setup":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    if (ProjectZ.Annex(map) == null || !Mountains.On(map))
                    {
                        Result("outcroptop: a tall-mountain map", false, "no annex or no tall mountains (-projectztallmountains)");
                        Finish();
                        return true;
                    }
                    bool sites = OtSites(map);
                    Result("outcroptop sites: a lone outcrop K, rock cells O1-O3 under bare level-1 tops, rock I under rock, each beside open ground", sites,
                        $"K [{string.Join(" ", otK)}] from {otKS}; O1 {otO1} ({otO1.GetFirstMineable(map)?.def.defName}) from {otS1}; O2 {otO2} from {otS2}; O3 {otO3} from {otS3}; I {otI} from {otSI}");
                    if (!sites)
                    {
                        Finish();
                        return true;
                    }
                    List<Pawn> all = Colonists(map).Where(p => !p.Downed).OrderBy(p => p.thingIDNumber).ToList();
                    Pawn miner = all.First(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining));
                    Pawn stand = all.First(p => p != miner);
                    otMinerId = miner.thingIDNumber;
                    otStandId = stand.thingIDNumber;
                    Teleport(miner, otS1);
                    Teleport(stand, otS2);
                    SetHour(map, 10);
                    FloorView.Set(0);
                    GameDataSaveLoader.SaveGame("pz_outcroptop");
                    stage = "ot_t1off";
                    pendingLoad = "pz_outcroptop";
                    return true;
                }

                // ------------------------------------------------------------ T1: O1, before (rule off) for the shots, then on
                case "ot_t1off":
                    MuShot(map, "ot_t1_0_unmined", 0, otO1, 6f, "ot_t1off_mine");
                    return true;

                case "ot_t1off_mine":
                    Find.UIRoot.screenshotMode.Active = false;
                    OtMine(map, new[] { otO1 }, otS1, true);
                    MuRun(400, 70100, () => !OtRock(otO1, map), "ot_t1off_shot");
                    return true;

                case "ot_t1off_shot":
                    Info("T1 rule off (before): " + OtTop(otO1, map));
                    MuShot(map, "ot_t1_1_before_rule", 0, otO1, 6f, "ot_t1off_end");
                    return true;

                case "ot_t1off_end":
                    OtEnd(map, "T1 (rule off)", "ot_t1");
                    return true;

                case "ot_t1":
                    OtMine(map, new[] { otO1 }, otS1, false);
                    MuRun(400, 70100, () => !OtRock(otO1, map), "ot_t1_check");
                    return true;

                case "ot_t1_check":
                {
                    IntVec3 u = L1(otO1, map);
                    bool yield = otO1.GetThingList(map).Concat(GenAdj.AdjacentCells.Select(o => otO1 + o).Where(c => c.InBounds(map)).SelectMany(c => c.GetThingList(map))).Any(t => t.def.category == ThingCategory.Item);
                    Result("T1 a lone rock under a bare top mined (vanilla Mine): the top is open air, the cell under the sky, the yield on the ground",
                        !OtRock(otO1, map) && u.GetTerrain(map) == PZDefOf.PZ_OpenAir && !map.roofGrid.Roofed(otO1) && BareTop.Opened - otOpened0 == 1 && yield,
                        $"{OtTop(otO1, map)}; tops opened {BareTop.Opened - otOpened0}; yield {yield}");
                    MuShot(map, "ot_t1_2_after_rule", 0, otO1, 6f, "ot_t1_end");
                    return true;
                }

                case "ot_t1_end":
                    OtEnd(map, "T1", "ot_t2");
                    return true;

                // ------------------------------------------------------------ T2: wood on the top
                case "ot_t2":
                {
                    Thing wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                    wood.stackCount = 20;
                    GenSpawn.Spawn(wood, L1(otO2, map), map);
                    OtMine(map, new[] { otO2 }, otS2, false);
                    MuRun(400, 70200, () => !OtRock(otO2, map), "ot_t2_check");
                    return true;
                }

                case "ot_t2_check":
                {
                    IntVec3 u = L1(otO2, map);
                    bool wood = u.GetThingList(map).Any(t => t.def == ThingDefOf.WoodLog);
                    Result("T2 a top with wood on it stays when the rock under it is mined: the top, the wood on it, the roof under it",
                        !OtRock(otO2, map) && TmTop(u, map) && wood && map.roofGrid.Roofed(otO2) && BareTop.Opened == otOpened0,
                        $"{OtTop(otO2, map)}; wood on top {wood}; tops opened {BareTop.Opened - otOpened0}");
                    MuShot(map, "ot_t2_wood_top_stays", 0, otO2, 6f, "ot_t2_end");
                    return true;
                }

                case "ot_t2_end":
                    OtEnd(map, "T2", "ot_t3");
                    return true;

                // ------------------------------------------------------------ T3: a colonist standing on the top
                case "ot_t3":
                {
                    Pawn stand = OtPawn(map, otStandId);
                    OtMine(map, new[] { otO3 }, otS3, false);
                    Teleport(stand, L1(otO3, map));
                    Draft(stand, true);
                    MuRun(400, 70300, () => !OtRock(otO3, map), "ot_t3_check", () =>
                    {
                        if (!stand.Drafted)
                        {
                            Draft(stand, true);
                        }
                    });
                    return true;
                }

                case "ot_t3_check":
                {
                    Pawn stand = OtPawn(map, otStandId);
                    IntVec3 u = L1(otO3, map);
                    Result("T3 a top with a colonist standing on it stays when the rock under it is mined; the colonist does not fall",
                        !OtRock(otO3, map) && TmTop(u, map) && stand.Position == u && map.roofGrid.Roofed(otO3) && BareTop.Opened == otOpened0,
                        $"{OtTop(otO3, map)}; colonist at {stand.Position} (level {ProjectZ.FloorOf(stand.Position, map)}); tops opened {BareTop.Opened - otOpened0}");
                    OtEnd(map, "T3", "ot_t4");
                    return true;
                }

                // ------------------------------------------------------------ T4: rock above
                case "ot_t4":
                    OtMine(map, new[] { otI }, otSI, false);
                    MuRun(400, 70400, () => !OtRock(otI, map), "ot_t4_check");
                    return true;

                case "ot_t4_check":
                {
                    IntVec3 u = L1(otI, map);
                    Result("T4 a rock under rock (a mountain's inside) mined: the rock above and the thick roof stay",
                        !OtRock(otI, map) && OtRock(u, map) && MuThick(otI, map) && BareTop.Opened == otOpened0,
                        $"{OtTop(otI, map)}; tops opened {BareTop.Opened - otOpened0}");
                    MuShot(map, "ot_t4_inside_keeps_roof", 0, otI, 6f, "ot_t4_end");
                    return true;
                }

                case "ot_t4_end":
                    OtEnd(map, "T4", "ot_t5");
                    return true;

                // ------------------------------------------------------------ T5 / T6: the lone outcrop mined out whole
                case "ot_t5":
                    OtMine(map, otK, otKS, false);
                    MuRun(1500, 70500, () => otK.All(c => !OtRock(c, map)), "ot_t5_check");
                    return true;

                case "ot_t5_check":
                    StartTicks(600, "ot_t5_check2");   // (a collapse for want of support would come within these)
                    return true;

                case "ot_t5_check2":
                {
                    int open = otK.Count(c => L1(c, map).GetTerrain(map) == PZDefOf.PZ_OpenAir && !map.roofGrid.Roofed(c));
                    Result("T5 the lone outcrop mined out whole: every top open to the sky, no collapse",
                        otK.All(c => !OtRock(c, map)) && open == otK.Count && PZStats.slabCollapses == otCollapses0,
                        $"{open}/{otK.Count} open; collapses {PZStats.slabCollapses - otCollapses0}; {string.Join("; ", otK.Select(c => OtTop(c, map)))}");
                    MuShot(map, "ot_t5_outcrop_after_rule", 0, otK[0], 7f, "ot_t5_end");
                    return true;
                }

                case "ot_t5_end":
                    OtEnd(map, "T5", "ot_t6");
                    return true;

                case "ot_t6":
                    OtMine(map, otK, otKS, true);
                    MuRun(1500, 70500, () => otK.All(c => !OtRock(c, map)), "ot_t6_ticks");
                    return true;

                case "ot_t6_ticks":
                    StartTicks(600, "ot_t6_check");
                    return true;

                case "ot_t6_check":
                {
                    int tops = otK.Count(c => TmTop(L1(c, map), map));
                    int roofed = otK.Count(c => map.roofGrid.Roofed(c));
                    Info($"T6 REPORT (rule off, as before): the lone outcrop mined out whole, 600 ticks later: {tops}/{otK.Count} tops still there (floating), {roofed} cells roofed, collapses {PZStats.slabCollapses - otCollapses0}; {string.Join("; ", otK.Select(c => OtTop(c, map)))}");
                    MuShot(map, "ot_t6_outcrop_before_rule", 0, otK[0], 7f, "ot_t6_shot2");
                    return true;
                }

                case "ot_t6_shot2":
                    MuShot(map, "ot_t6_outcrop_before_rule_level1", 1, otK[0], 7f, "ot_t6_end");
                    return true;

                case "ot_t6_end":
                    OtEnd(map, "T6", null);
                    return true;
            }
            return false;
        }
    }
}
