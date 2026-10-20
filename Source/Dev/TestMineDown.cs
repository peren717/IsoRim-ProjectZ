using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode minedown: mine down, stairs from above, the one-floor landing rule and the two-way x-ray (basements design notes
    // "Stairs", "X-ray both ways"; batch after phase 3). The same fixed-seed map as mineup (Mountainous,
    // -projectztallmountains, -projectzfloors=4, -projectzmapsize=250). Setup finds, from a fixed point: a block P of
    // ground rock 9 x 7 under bare level-1 tops (the plateau), a ground tunnel site F (4 rock cells into a face under bare
    // level-1 tops), a cell X of ground rock under level-1 rock under a level-2 top, and plateau edges J1 (level 1) and J2
    // (level 2, if any) over open ground. Three colonists are put on the plateau (the arrival reveal), the game saved as
    // pz_minedown; every check reloads it and works with real designators, work givers and jobs under fixed seeds, and ends
    // with terrain, roofs and edifices outside its site unchanged:
    //   D1 the side rule: interior ground rock under a top -> from above; rock at a reachable face (and the marked rock
    //      behind it, the group) -> its own level; level-1 rock over level-1... over a dug ground cell with a top above ->
    //      from below (never both); the ground surface: no Mine mark, no work from above
    //   D2 mine down: vanilla Mine on two ground rocks (one steel ore) inside P; a colonist on the plateau mines them from
    //      above standing next to the top, not on it; the tops open to the sky; yields at the miner; a drafted colonist and
    //      wood on the top fall into the pit (one floor: no injury, staggered); fog of the pit and its walls; the pit floor
    //      stays (nothing below the ground); saved and reloaded identical
    //   D3 the group rule at F: three marked rocks at a face reachable on the ground are mined there (vanilla), never from above
    //   D5 stairs: vanilla placement refuses them on unmined rock in the fog and on seen rock; the footprint marked with
    //      vanilla Mine from the plateau is mined from above (cells open, revealed); the stairs placed in the pit (ground
    //      view, stairs tool with the x-ray) are built from the rim with nobody going down; the exit top stays; a colonist
    //      walks down into the pit and back up; deconstructed from above
    //   D6 falls: a one-floor jump (J1) does no harm but staggers; a two-floor jump (J2) hurts
    //   D7 IsoRim's x-ray: the stairs tool on the ground shows the floor above only; the Mine tool on the ground the floor
    //      above, on level 1 both ways; no tool nothing
    // Shots (IsoRim): md_*.png.
    public static partial class TestRunner
    {
        private static IntVec3 mdG0, mdJ1, mdJ1Down, mdJ2, mdJ2Down, mdX, mdFS, mdFD;
        private static readonly List<IntVec3> mdF = new List<IntVec3>();
        private static readonly List<int> mdCrewIds = new List<int>();
        private static int mdVictimId = -1;
        private static int mdMinedA, mdOpened, mdStaggers, mdHurts;
        private static bool mdFlag, mdFlag2, mdFlag3;
        private static IntVec3 mdAt, mdAt2;
        private static string mdNote;
        private static float mdHurt0;

        private static IntVec3 MdM1 => mdG0 + new IntVec3(-2, 0, 0);
        private static IntVec3 MdM2 => mdG0 + new IntVec3(-2, 0, -1);
        private static IntVec3 MdStairs => mdG0 + new IntVec3(2, 0, 0);   // rotation north: footprint z -1..1, exit over z + 2
        private static IntVec3 MdExitGround => mdG0 + new IntVec3(2, 0, 2);

        private static IEnumerable<IntVec3> MdFoot => StairShape.Footprint(MdStairs, Rot4.North).Cells;

        private static IntVec3 L1(IntVec3 g, Map map) => Levels.Cell(g, 1, map);

        private static float MdInjury(Pawn p) => p.health.hediffSet.hediffs.OfType<Hediff_Injury>().Sum(h => h.Severity);

        private static List<Pawn> MdCrew(Map map) => mdCrewIds.Select(id => map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == id)).Where(p => p != null).ToList();

        private static Pawn MdVictim(Map map) => map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == mdVictimId);

        // The crew work only these types (the rest drafted where they are).
        private static void MdWork(Map map, List<Pawn> crew, params WorkTypeDef[] types)
        {
            foreach (Pawn p in Colonists(map))
            {
                Draft(p, !crew.Contains(p));
                p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
                foreach (WorkTypeDef d in DefDatabase<WorkTypeDef>.AllDefs)
                {
                    if (!p.WorkTypeIsDisabled(d))
                    {
                        p.workSettings.SetPriority(d, crew.Contains(p) && types.Contains(d) ? 1 : 0);
                    }
                }
            }
        }

        private static void MdEnd(Map map, string check, string next)
        {
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
            pendingLoad = "pz_minedown";
        }

        private static void MdStart(Map map, CellRect site, bool fog)
        {
            MuStart(map, site, fog);
            mdMinedA = MineUp.MinedFromAbove;
            mdOpened = Shaft.Opened;
            mdStaggers = JumpGrid.Staggers;
            mdHurts = JumpGrid.HurtLandings;
        }

        private static bool MdBlockOk(IntVec3 g0, Map map)
        {
            for (int dz = -3; dz <= 3; dz++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    IntVec3 g = g0 + new IntVec3(dx, 0, dz);
                    if (!g.InBounds(map) || !MuRock(g, map) || !MuThick(g, map) || Column.NaturalTop(g, map) != 1 || !TmTop(L1(g, map), map))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool MdSites(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            IntVec3 home = new IntVec3(map.Size.x / 2, 0, a.groundZ / 2);
            List<IntVec3> near = Near(home, 110, map).ToList();
            mdG0 = IntVec3.Invalid;
            foreach (IntVec3 g in near)
            {
                if (g.x < 8 || g.z < 8 || g.x >= map.Size.x - 8 || g.z >= a.groundZ - 8 || !MuRock(g, map) || Column.NaturalTop(g, map) != 1)
                {
                    continue;
                }
                if (MdBlockOk(g, map))
                {
                    mdG0 = g;
                    break;
                }
            }
            if (!mdG0.IsValid)
            {
                return false;
            }
            // F: an open reachable ground cell, then 4 rock cells under bare level-1 tops (a front wall the 4th), side rock
            mdF.Clear();
            foreach (IntVec3 s in near)
            {
                if (s.x < 6 || s.z < 6 || s.x >= map.Size.x - 6 || s.z >= a.groundZ - 6 || s.InHorDistOf(mdG0, 20f) || !MuOpen(s, map)
                    || !map.reachability.CanReachMapEdge(s, TraverseParms.For(TraverseMode.PassDoors)))
                {
                    continue;
                }
                foreach (IntVec3 d in GenAdj.CardinalDirections)
                {
                    IntVec3 side = d.RotatedBy(RotationDirection.Clockwise);
                    bool ok = !map.fogGrid.IsFogged(s + d);
                    for (int i = 1; i <= 4 && ok; i++)
                    {
                        IntVec3 t = s + d * i;
                        ok = MuRock(t, map) && MuThick(t, map) && Column.NaturalTop(t, map) == 1 && TmTop(L1(t, map), map) && MuRock(t + side, map) && MuRock(t - side, map);
                    }
                    if (ok)
                    {
                        mdFS = s;
                        mdFD = d;
                        for (int i = 1; i <= 4; i++)
                        {
                            mdF.Add(s + d * i);
                        }
                        break;
                    }
                }
                if (mdF.Count > 0)
                {
                    break;
                }
            }
            // X: ground rock under level-1 rock under a level-2 top, rock all round on the ground (a fixture for the rule)
            mdX = IntVec3.Invalid;
            foreach (IntVec3 g in near)
            {
                if (!g.InHorDistOf(mdG0, 15f) && g.x > 4 && g.z > 4 && g.x < map.Size.x - 4 && g.z < a.groundZ - 4 && MuRock(g, map) && Column.NaturalTop(g, map) == 2
                    && MuRock(L1(g, map), map) && TmTop(Levels.Cell(g, 2, map), map) && GenAdj.AdjacentCells.All(o => MuRock(g + o, map) && MuRock(L1(g + o, map), map)))
                {
                    mdX = g;
                    break;
                }
            }
            return mdF.Count == 4 && mdX.IsValid;
        }

        // A plateau edge on level L over open ground (cardinal neighbour: open air over L-1 .. 1 and walkable open ground),
        // reachable by the pawn.
        private static bool MdEdge(Map map, Pawn p, int L, bool reach, out IntVec3 edge, out IntVec3 down)
        {
            edge = down = IntVec3.Invalid;
            foreach (IntVec3 g in Near(ProjectZ.GroundCellOf(p.Position, map), 80, map))
            {
                IntVec3 c = Levels.Cell(g, L, map);
                if (!c.IsValid || !TmTop(c, map) || reach && map.fogGrid.IsFogged(c) || Column.NaturalTop(g, map) != L)
                {
                    continue;
                }
                foreach (IntVec3 d in GenAdj.CardinalDirections)
                {
                    IntVec3 ng = g + d;
                    if (ng.InBounds(map) && MuOpen(ng, map) && Column.NaturalTop(ng, map) == 0 && !map.roofGrid.Roofed(ng) && (!reach || p.CanReach(c, PathEndMode.OnCell, Danger.Deadly)))
                    {
                        edge = c;
                        down = ng;
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool StepMineDown(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            switch (stage)
            {
                // ------------------------------------------------------------ setup
                case "md_setup":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    if (a == null || !Mountains.On(map))
                    {
                        Result("minedown: a tall-mountain map", false, "no annex or no tall mountains (-projectztallmountains)");
                        Finish();
                        return true;
                    }
                    xrType = HarmonyLib.AccessTools.TypeByName("IsoRim.MineXray");
                    bool sites = MdSites(map);
                    Result("minedown sites: a 9x7 ground rock block under bare level-1 tops (P), a face F, a rock X under rock under a level-2 top", sites,
                        $"P {mdG0}, F {(mdF.Count > 0 ? $"{mdFS} dir {mdFD}" : "none")}, X {mdX}");
                    if (!sites)
                    {
                        Finish();
                        return true;
                    }
                    // three colonists (miners and builders) on the plateau, south of the block's middle; arrival reveal
                    List<Pawn> all = Colonists(map).Where(p => !p.Downed).OrderBy(p => p.thingIDNumber).ToList();
                    List<Pawn> crew = all.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)).Take(3).ToList();
                    if (crew.Count < 2)
                    {
                        crew = all.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining)).Take(3).ToList();
                    }
                    mdCrewIds.Clear();
                    for (int i = 0; i < crew.Count; i++)
                    {
                        Teleport(crew[i], L1(mdG0 + new IntVec3(-1 + i, 0, -3), map));
                        mdCrewIds.Add(crew[i].thingIDNumber);
                    }
                    // (the drafted colonist on the top that opens in D2: the third of the crew, or another colonist)
                    Pawn victim = all.FirstOrDefault(p => !crew.Contains(p)) ?? (crew.Count >= 3 ? crew[2] : null);
                    mdVictimId = victim?.thingIDNumber ?? -1;
                    ReachFog.Run(map);
                    MdEdge(map, crew[0], 1, true, out mdJ1, out mdJ1Down);
                    MdEdge(map, crew[0], 2, false, out mdJ2, out mdJ2Down);
                    Info($"setup: P {mdG0} (m1 {MdM1}, m2 {MdM2}, stairs {MdStairs}), F {string.Join(" ", mdF)}, X {mdX}; crew {string.Join(", ", crew.Select(p => $"{p} at {p.Position}"))}; victim {victim}; edge J1 {mdJ1} -> {mdJ1Down}, J2 {mdJ2} -> {mdJ2Down}");
                    SetHour(map, 10);
                    FloorView.Set(0);
                    GameDataSaveLoader.SaveGame("pz_minedown");
                    stage = "md_d1";
                    pendingLoad = "pz_minedown";
                    return true;
                }

                // ------------------------------------------------------------ D1: the side rule
                case "md_d1":
                {
                    MdStart(map, MuRect(new[] { mdG0, mdX, mdFS }.Concat(mdF), map, 2), false);
                    // interior ground rock under a top: from above
                    MineDir inner = MineSide.Get(MdM1, map);
                    // a face reachable on the ground (a colonist there: the colony), and the rock behind it marked with it: own
                    // level (the group)
                    Pawn onGround = MdCrew(map).LastOrDefault();
                    if (onGround != null)
                    {
                        Teleport(onGround, mdFS);
                    }
                    new Designator_Mine().DesignateMultiCell(mdF.Take(3));
                    MineDir face = MineSide.Get(mdF[0], map), behind = MineSide.Get(mdF[2], map);
                    // level-1 rock over ground rock X, a level-2 top above: from above; X dug (a fixture): from below, never both
                    IntVec3 ux = L1(mdX, map);
                    MineDir before = MineSide.Get(ux, map);
                    mdX.GetEdifice(map)?.Destroy(DestroyMode.Vanish);
                    MineDir after = MineSide.Get(ux, map);
                    Result("D1 the side rule: interior ground rock under a top is mined from above", inner == MineDir.FromAbove, $"{MdM1}: {inner}");
                    Result("D1 the group rule: a marked face reachable on the ground and the rock behind it: own level", face == MineDir.Own && behind == MineDir.Own, $"face {mdF[0]} {face}, behind {mdF[2]} {behind}");
                    Result("D1 level-1 rock with a top above: from above while rock is below, from below once the cell below is open (one side, below first)",
                        before == MineDir.FromAbove && after == MineDir.FromBelow, $"{ux}: before {before}, after the ground cell {mdX} was dug {after}");
                    // the ground surface is never dug: no Mine mark on open ground, nothing for the from-above giver
                    IntVec3 soil = mdFS;
                    AcceptanceReport mark = new Designator_Mine().CanDesignateCell(soil);
                    Pawn miner = MdCrew(map).FirstOrDefault();
                    WorkGiver_Scanner above = DefDatabase<WorkGiverDef>.GetNamed("PZ_MineFromAbove").Worker as WorkGiver_Scanner;
                    bool noJob = miner != null && !above.HasJobOnCell(miner, L1(soil, map)) && MineSide.Get(soil, map) == MineDir.Own;
                    Result("D1 the ground surface: no Mine mark on open ground, no work from above over it", !mark.Accepted && noJob, $"{soil} {soil.GetTerrain(map).defName}: mark '{(mark.Accepted ? "ok" : mark.Reason)}', no job {noJob}");
                    MdEnd(map, "D1", "md_d2");
                    return true;
                }

                // ------------------------------------------------------------ D2: mine down
                case "md_d2":
                {
                    MdStart(map, MuRect(new[] { mdG0 }, map, 5), true);
                    GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.MineableSteel), MdM2, map, WipeMode.Vanish);
                    IntVec3 t1 = L1(MdM1, map);
                    Pawn victim = MdVictim(map);
                    if (victim != null)
                    {
                        Teleport(victim, t1);
                        Draft(victim, true);
                        mdHurt0 = MdInjury(victim);
                    }
                    Thing wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                    wood.stackCount = 30;
                    GenSpawn.Spawn(wood, t1, map);
                    muFogBefore.Clear();
                    foreach (IntVec3 g in new[] { MdM1, MdM2 })
                    {
                        for (int i = 0; i < 9; i++)
                        {
                            IntVec3 n = g + GenAdj.AdjacentCellsAndInside[i];
                            muFogBefore[n] = map.fogGrid.IsFogged(n);
                        }
                    }
                    Info($"D2 fog before: m1 {(map.fogGrid.IsFogged(MdM1) ? "fogged" : "seen")}, m2 {(map.fogGrid.IsFogged(MdM2) ? "fogged" : "seen")}");
                    new Designator_Mine().DesignateMultiCell(new[] { MdM1, MdM2 });
                    List<Pawn> crew = MdCrew(map).Take(1).ToList();
                    MdWork(map, crew, WorkTypeDefOf.Mining);
                    // (bystanders drafted out of the way: a drafted pawn steps aside when a worker walks through)
                    int far = 0;
                    foreach (Pawn b in Colonists(map).Where(p => !crew.Contains(p) && p != victim).ToList())
                    {
                        Teleport(b, L1(mdG0 + new IntVec3(4 - far++, 0, 3), map));
                    }
                    if (victim != null)
                    {
                        Draft(victim, true);
                    }
                    muP1 = crew.FirstOrDefault();
                    mdAt = mdAt2 = IntVec3.Invalid;
                    mdFlag = mdFlag2 = mdFlag3 = false;
                    mdNote = null;
                    MuRun(300, 41000, () => !MuRockOrOre(MdM1, map) && !MuRockOrOre(MdM2, map), "md_d2_done", () =>
                    {
                        if (!mdAt.IsValid && !MuRockOrOre(MdM2, map))
                        {
                            mdAt = muP1.Position;   // where the miner stood when the ore went
                        }
                        if (!mdFlag && muP1.CurJobDef == PZMineDefOf.PZ_MineFromAbove && muP1.jobs.curDriver.CurToilIndex >= 1 && !muP1.pather.Moving)
                        {
                            mdFlag = true;
                            mdAt2 = muP1.Position;
                            muAt2 = muP1.CurJob.targetB.Cell;
                        }
                        Pawn v = MdVictim(map);
                        if (v != null && !mdFlag2 && !v.Drafted)
                        {
                            Draft(v, true);   // (vanilla's auto-undraft after a few idle hours)
                        }
                        if (v != null && !mdFlag2 && ProjectZ.FloorOf(v.Position, map) == 1 && v.Position != L1(MdM1, map) && mdNote != "moved" && mdNote != "shot")
                        {
                            mdNote = "moved";
                            Info($"D2 the colonist on the top moved before it opened: to {v.Position}, job {v.CurJobDef?.defName}, drafted {v.Drafted}, tick {Find.TickManager.TicksGame}");
                        }
                        if (v != null && !mdFlag2 && ProjectZ.FloorOf(v.Position, map) == 0)
                        {
                            mdFlag2 = true;   // landed
                        }
                        if (v != null && mdFlag2 && !mdFlag3 && v.stances?.stagger != null && v.stances.stagger.Staggered)
                        {
                            mdFlag3 = true;
                            muInterrupt = "md_d2_fallshot";
                        }
                        if (mdFlag && MuRockOrOre(MdM1, map) != MuRockOrOre(MdM2, map) && mdNote != "shot" && muP1.CurJobDef == PZMineDefOf.PZ_MineFromAbove && !muP1.pather.Moving
                            && muP1.CurJob.targetA.Thing is Thing r && r.HitPoints < r.MaxHitPoints)
                        {
                            mdNote = "shot";
                            muInterrupt = "md_d2_shot";
                        }
                    });
                    return true;
                }

                case "md_d2_fallshot":
                {
                    Pawn v = MdVictim(map);
                    MuShot(map, "md_fall_stagger", 0, v != null ? v.Position : MdM1, 6f, "md_d2_resume");
                    return true;
                }

                case "md_d2_shot":
                    MuPose(muP1);
                    MuShot(map, "md_minedown_top", 1, muP1.Position, 6f, "md_d2_shot2");
                    return true;

                case "md_d2_shot2":
                    MuShot(map, "md_minedown_pit", 0, MdM1, 6f, "md_d2_resume");
                    return true;

                case "md_d2_resume":
                    muPointer = IntVec3.Invalid;
                    Find.UIRoot.screenshotMode.Active = false;
                    FloorView.Set(0);
                    MuRun(300, 41100, () => !MuRockOrOre(MdM1, map) && !MuRockOrOre(MdM2, map), "md_d2_done", muEach);
                    return true;

                case "md_d2_done":
                {
                    IntVec3 t1 = L1(MdM1, map), t2 = L1(MdM2, map);
                    bool gone = !MuRockOrOre(MdM1, map) && !MuRockOrOre(MdM2, map);
                    bool air = t1.GetTerrain(map) == PZDefOf.PZ_OpenAir && t2.GetTerrain(map) == PZDefOf.PZ_OpenAir;
                    bool sky = !map.roofGrid.Roofed(MdM1) && !map.roofGrid.Roofed(MdM2) && Shaft.Is(MdM1, map) && Shaft.Is(MdM2, map);
                    Result("D2 a colonist on the plateau mined two ground rocks (one ore) from above: both gone, the tops over them open air, the pits open to the sky",
                        gone && air && sky && MineUp.MinedFromAbove - mdMinedA == 2,
                        $"gone {gone}, open air {air}; roofs {MuRoof(MdM1, map)} {MuRoof(MdM2, map)}, shafts {Shaft.Is(MdM1, map)} {Shaft.Is(MdM2, map)}; mined from above {MineUp.MinedFromAbove - mdMinedA}");
                    Result("D2 the miner worked from the plateau next to the top that opened, not on it", mdFlag && ProjectZ.FloorOf(mdAt2, map) == 1 && mdAt2.InHorDistOf(muAt2, 1.5f) && mdAt2 != muAt2,
                        $"worked at B {muAt2} from {mdAt2} (level {ProjectZ.FloorOf(mdAt2, map)})");
                    List<Thing> steel = map.listerThings.ThingsOfDef(ThingDefOf.Steel).Where(t => t.Spawned && mdAt.IsValid && ProjectZ.FloorOf(t.Position, map) == ProjectZ.FloorOf(mdAt, map) && t.Position.InHorDistOf(mdAt, 1.5f)).ToList();
                    Result("D2 the steel lands at the miner's cell (its floor, within 1 cell)", steel.Sum(t => t.stackCount) > 0,
                        $"miner stood at {mdAt}; steel {string.Join(", ", map.listerThings.ThingsOfDef(ThingDefOf.Steel).Select(t => $"{t.stackCount} at {t.Position} L{ProjectZ.FloorOf(t.Position, map)}"))}");
                    Pawn v = MdVictim(map);
                    float hurt = v == null ? -1 : MdInjury(v) - mdHurt0;
                    Result("D2 a drafted colonist on the top fell one floor into the pit: no injury, staggered", v != null && mdFlag2 && ProjectZ.FloorOf(v.Position, map) == 0 && hurt <= 0f && mdFlag3,
                        $"{v} at {v?.Position} level {(v == null ? -9 : ProjectZ.FloorOf(v.Position, map))}, new injury {hurt:0.0}, staggered seen {mdFlag3}; landings: staggered {JumpGrid.Staggers - mdStaggers}, hurt {JumpGrid.HurtLandings - mdHurts}");
                    List<Thing> wood = map.listerThings.ThingsOfDef(ThingDefOf.WoodLog).Where(t => t.Spawned && ProjectZ.GroundCellOf(t.Position, map).InHorDistOf(MdM1, 1.5f)).ToList();
                    Result("D2 the wood on the top fell into the pit", wood.Count > 0 && wood.All(t => ProjectZ.FloorOf(t.Position, map) == 0), string.Join(", ", wood.Select(t => $"{t.stackCount} at {t.Position} L{ProjectZ.FloorOf(t.Position, map)}")));
                    int faces = 0, facesOpen = 0;
                    foreach (KeyValuePair<IntVec3, bool> kv in muFogBefore)
                    {
                        Building e = kv.Key.GetEdifice(map);
                        if (e != null && e.def.MakeFog)
                        {
                            faces++;
                            facesOpen += map.fogGrid.IsFogged(kv.Key) ? 0 : 1;
                        }
                    }
                    Result("D2 fog: the pits and the rock walls round them are revealed", !map.fogGrid.IsFogged(MdM1) && !map.fogGrid.IsFogged(MdM2) && faces == facesOpen, $"pits seen {!map.fogGrid.IsFogged(MdM1)} {!map.fogGrid.IsFogged(MdM2)}, walls {facesOpen}/{faces}");
                    AcceptanceReport again = new Designator_Mine().CanDesignateCell(MdM1);
                    Result("D2 the pit floor stays (nothing below the ground): natural floor, no further Mine", MdM1.GetTerrain(map) != PZDefOf.PZ_OpenAir && MdM1.Walkable(map) && !again.Accepted,
                        $"{MdM1} {MdM1.GetTerrain(map).defName}, walkable {MdM1.Walkable(map)}, mark '{(again.Accepted ? "ok" : again.Reason)}'");
                    MuOutside(map, "D2");
                    muNote = MuState(map, new[] { MdM1, MdM2, t1, t2 });
                    MuShot(map, "md_pit_up", 1, MdM1, 6f, "md_d2_save");
                    return true;
                }

                case "md_d2_save":
                    Find.UIRoot.screenshotMode.Active = false;
                    FloorView.Set(0);
                    GameDataSaveLoader.SaveGame("pz_minedown_d2");
                    stage = "md_d2_loaded";
                    pendingLoad = "pz_minedown_d2";
                    return true;

                case "md_d2_loaded":
                {
                    string now = MuState(map, new[] { MdM1, MdM2, L1(MdM1, map), L1(MdM2, map) });
                    Result("D2 save + reload: shaft marks, terrain, roofs and fog identical", now == muNote, now == muNote ? now : $"{muNote} -> {now}");
                    stage = "md_d3";
                    pendingLoad = "pz_minedown";
                    return true;
                }

                // ------------------------------------------------------------ D3: the group rule at a ground face
                case "md_d3":
                {
                    MdStart(map, MuRect(mdF.Concat(new[] { mdFS }), map, 3), false);
                    new Designator_Mine().DesignateMultiCell(mdF.Take(3));
                    // a miner on the plateau (from-above work offered if the rule allowed it) and one on the ground
                    List<Pawn> crew = MdCrew(map).Take(1).ToList();
                    Pawn ground = Colonists(map).Where(p => !mdCrewIds.Contains(p.thingIDNumber) && p.thingIDNumber != mdVictimId && !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining)).OrderBy(p => p.thingIDNumber).FirstOrDefault()
                        ?? MdVictim(map);
                    if (ground != null)
                    {
                        Teleport(ground, mdFS);
                        crew.Add(ground);
                    }
                    MdWork(map, crew, WorkTypeDefOf.Mining);
                    mdFlag = false;
                    MuRun(400, 42000, () => mdF.Take(3).All(t => !MuRockOrOre(t, map)), "md_d3_done", () =>
                    {
                        if (Colonists(map).Any(p => p.CurJobDef == PZMineDefOf.PZ_MineFromAbove))
                        {
                            mdFlag = true;
                        }
                    });
                    return true;
                }

                case "md_d3_done":
                {
                    int dug = mdF.Take(3).Count(t => !MuRockOrOre(t, map));
                    int tops = mdF.Take(3).Count(t => TmTop(L1(t, map), map));
                    Result("D3 three marked rocks at a face reachable on the ground: dug there (vanilla), the tops above them stay, never from above",
                        dug == 3 && tops == 3 && !mdFlag && MineUp.MinedFromAbove == mdMinedA && Shaft.Opened == mdOpened,
                        $"dug {dug}/3, tops kept {tops}/3, from-above job seen {mdFlag}, mined from above {MineUp.MinedFromAbove - mdMinedA}, shafts {Shaft.Opened - mdOpened}");
                    MdEnd(map, "D3", "md_d5");
                    return true;
                }

                // ------------------------------------------------------------ D5: stairs from above
                case "md_d5":
                {
                    MdStart(map, MuRect(new[] { mdG0 }, map, 5), true);
                    // vanilla placement: no stairs on unmined rock in the fog
                    AcceptanceReport fogged = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, MdStairs, Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
                    Result("D5 stairs on unmined rock in the fog: refused as vanilla ('Cannot place in undiscovered areas')", !fogged.Accepted && fogged.Reason == "CannotPlaceInUndiscovered".Translate(),
                        $"{MdStairs} fogged {map.fogGrid.IsFogged(MdStairs)}: '{(fogged.Accepted ? "ok" : fogged.Reason)}'");
                    // the footprint marked with vanilla Mine (seen from the plateau): mined from above, the cells open
                    new Designator_Mine().DesignateMultiCell(MdFoot);
                    List<Pawn> crew = MdCrew(map);
                    MdWork(map, crew, WorkTypeDefOf.Mining);
                    MuRun(400, 43000, () => MdFoot.All(c => !MuRockOrOre(c, map)), "md_d5_dug");
                    return true;
                }

                case "md_d5_dug":
                {
                    bool dug = MdFoot.All(c => !MuRockOrOre(c, map) && L1(c, map).GetTerrain(map) == PZDefOf.PZ_OpenAir && !map.fogGrid.IsFogged(c));
                    Result("D5 the stairs' footprint marked with vanilla Mine from the plateau is mined from above: the cells open and revealed",
                        dug && MineUp.MinedFromAbove - mdMinedA == 3, $"{string.Join(", ", MdFoot.Select(c => $"{c} {(MuRockOrOre(c, map) ? "rock" : "open")} {(map.fogGrid.IsFogged(c) ? "fog" : "seen")} roof {MuRoof(c, map)}"))}; mined from above {MineUp.MinedFromAbove - mdMinedA}");
                    // vanilla placement: no stairs on unmined (now seen) rock: the wall east of the pit
                    IntVec3 wall = MdStairs + IntVec3.East;
                    AcceptanceReport rock = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, wall, Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
                    Result("D5 stairs on unmined rock (seen): refused as vanilla ('Space already occupied')", !rock.Accepted && rock.Reason == "SpaceAlreadyOccupied".Translate() && !map.fogGrid.IsFogged(wall),
                        $"{wall} {wall.GetEdifice(map)?.def.defName ?? "-"}, fogged {map.fogGrid.IsFogged(wall)}: '{(rock.Accepted ? "ok" : rock.Reason)}'");
                    AcceptanceReport place = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, MdStairs, Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
                    Result("D5 stairs in the opened pit (ground view): placement accepted", place.Accepted, $"{MdStairs}: '{(place.Accepted ? "ok" : place.Reason)}'");
                    if (!place.Accepted)
                    {
                        MdEnd(map, "D5", "md_d6");
                        return true;
                    }
                    // the stairs tool in the ground view (the x-ray of the level above shows where the exit lands)
                    FloorView.Set(0);
                    ShotAt(map, 0, MdStairs, 7f);
                    MuPointer(MdStairs);
                    Find.UIRoot.screenshotMode.Active = true;
                    Find.DesignatorManager.Select(new Designator_Build(PZDefOf.PZ_Stairs));
                    muShotName = "md_stairs_xray";
                    muShotNext = "md_d5_xray";
                    Wait(40, "mu_shot");
                    return true;
                }

                case "md_d5_xray":
                {
                    if (xrType != null)
                    {
                        Result("D7 the stairs tool on the ground: the x-ray shows the floor above only", (bool)XrGet("ShownUp") && !(bool)XrGet("ShownDown") && (int)XrGet("OutlineQuadsUp") > 0,
                            $"up {XrGet("ShownUp")} ({XrGet("OutlineQuadsUp")} quads), down {XrGet("ShownDown")}");
                    }
                    else
                    {
                        Info("D7 skipped: IsoRim's MineXray not loaded");
                    }
                    muPointer = IntVec3.Invalid;
                    Find.UIRoot.screenshotMode.Active = false;
                    Find.DesignatorManager.Deselect();
                    GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, MdStairs, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
                    for (int i = 0; i < 4; i++)
                    {
                        Thing w = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                        w.stackCount = 75;
                        GenPlace.TryPlaceThing(w, L1(mdG0 + new IntVec3(0, 0, -3), map), map, ThingPlaceMode.Near);
                    }
                    List<Pawn> crew = MdCrew(map);
                    MdWork(map, crew, WorkTypeDefOf.Construction, WorkTypeDefOf.Hauling);
                    mdFlag = false;    // a builder went down to the ground before the stairs stood
                    mdNote = null;
                    MuRun(500, 43050, () => MuStairs(map, MdStairs) != null, "md_d5_built", () =>
                    {
                        foreach (Pawn p in MdCrew(map))
                        {
                            if (ProjectZ.FloorOf(p.Position, map) == 0 && MuStairs(map, MdStairs) == null)
                            {
                                mdFlag = true;
                                mdNote = mdNote ?? $"{p} at {p.Position} ({p.CurJobDef?.defName})";
                            }
                        }
                    });
                    return true;
                }

                case "md_d5_built":
                {
                    Building_Stairs st = MuStairs(map, MdStairs);
                    IntVec3 e = L1(MdExitGround, map);
                    Result("D5 builders on the plateau built the stairs in the pit from the rim", st != null, $"built {st != null}; roofs {string.Join(" ", MdFoot.Select(c => MuRoof(c, map)))}");
                    Result("D5 no builder went down to the ground before the stairs stood", !mdFlag, mdNote ?? "none");
                    Result("D5 the exit is the plateau top (nothing laid), the stairs lead there", st != null && st.TopCell == e && TmTop(e, map),
                        $"exit {st?.TopCell} ({e.GetTerrain(map).defName})");
                    if (st == null)
                    {
                        MdEnd(map, "D5", "md_d6");
                        return true;
                    }
                    muP3 = MdCrew(map).FirstOrDefault();
                    MdWork(map, new List<Pawn>(), WorkTypeDefOf.Mining);
                    Draft(muP3, true);
                    IntVec3 low = MdStairs + Rot4.North.FacingCell * -1;
                    muP3.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, low), JobTag.Misc);
                    MuRun(60, 43100, () => muP3.Position == low, "md_d5_down");
                    return true;
                }

                case "md_d5_down":
                {
                    IntVec3 low = MdStairs + Rot4.North.FacingCell * -1;
                    bool down = muP3.Position == low;
                    IntVec3 back = L1(mdG0 + new IntVec3(0, 0, -3), map);
                    muP3.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, back), JobTag.Misc);
                    mdFlag = down;
                    MuShot(map, "md_stairs_built", 1, MdStairs, 6f, "md_d5_up");
                    return true;
                }

                case "md_d5_up":
                    Find.UIRoot.screenshotMode.Active = false;
                    MuRun(60, 43200, () => muP3.Position == L1(mdG0 + new IntVec3(0, 0, -3), map), "md_d5_walked");
                    return true;

                case "md_d5_walked":
                {
                    bool up = muP3.Position == L1(mdG0 + new IntVec3(0, 0, -3), map);
                    Result("D5 a colonist walked down the stairs into the pit and back up to the plateau", mdFlag && up, $"down {mdFlag}, back up {up} ({muP3.Position})");
                    MuShot(map, "md_stairs_built_ground", 0, MdStairs, 6f, "md_d5_decon");
                    return true;
                }

                case "md_d5_decon":
                {
                    Find.UIRoot.screenshotMode.Active = false;
                    FloorView.Set(0);
                    Building_Stairs st = MuStairs(map, MdStairs);
                    map.designationManager.AddDesignation(new Designation(st, DesignationDefOf.Deconstruct));
                    List<Pawn> crew = MdCrew(map);
                    foreach (Pawn p in crew)
                    {
                        if (ProjectZ.FloorOf(p.Position, map) != 1)
                        {
                            Teleport(p, L1(mdG0 + new IntVec3(0, 0, -3), map));
                        }
                    }
                    MdWork(map, crew, WorkTypeDefOf.Construction);
                    mdFlag = false;
                    MuRun(200, 43300, () => MuStairs(map, MdStairs) == null, "md_d5_gone", () =>
                    {
                        if (MdCrew(map).Any(p => ProjectZ.FloorOf(p.Position, map) == 0))
                        {
                            mdFlag = true;
                        }
                    });
                    return true;
                }

                case "md_d5_gone":
                {
                    bool gone = MuStairs(map, MdStairs) == null;
                    Result("D5 the stairs deconstructed from the plateau (nobody went down)", gone && !mdFlag, $"gone {gone}, went down {mdFlag}");
                    MdEnd(map, "D5", "md_d6");
                    return true;
                }

                // ------------------------------------------------------------ D6: falls
                case "md_d6":
                {
                    MdStart(map, MuRect(new[] { mdG0 }, map, 5), false);
                    Pawn p = MdCrew(map).FirstOrDefault();
                    if (!mdJ1.IsValid || p == null)
                    {
                        Result("D6 a plateau edge over open ground (level 1)", false, "none");
                        MdEnd(map, "D6", "md_d7");
                        return true;
                    }
                    MdWork(map, new List<Pawn>(), WorkTypeDefOf.Mining);
                    muP1 = p;
                    Teleport(p, mdJ1);
                    Draft(p, true);
                    mdHurt0 = MdInjury(p);
                    mdFlag = false;
                    p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, mdJ1Down), JobTag.Misc);
                    MuRun(40, 44000, () => ProjectZ.FloorOf(muP1.Position, map) == 0 && mdFlag, "md_d6_j1", () =>
                    {
                        if (!mdFlag && ProjectZ.FloorOf(muP1.Position, map) == 0 && muP1.stances?.stagger != null && muP1.stances.stagger.Staggered)
                        {
                            mdFlag = true;
                            muInterrupt = "md_d6_shot";   // (the landing, staggered)
                        }
                    });
                    return true;
                }

                case "md_d6_shot":
                    MuShot(map, "md_jump_stagger", 0, muP1.Position, 5f, "md_d6_resume");
                    return true;

                case "md_d6_resume":
                    Find.UIRoot.screenshotMode.Active = false;
                    stage = "md_d6_j1";
                    return true;

                case "md_d6_j1":
                {
                    float hurt = MdInjury(muP1) - mdHurt0;
                    Result("D6 a jump of one floor (plateau edge to the ground): no injury, staggered", ProjectZ.FloorOf(muP1.Position, map) == 0 && hurt <= 0f && mdFlag && JumpGrid.HurtLandings == mdHurts,
                        $"{muP1} at {muP1.Position}, new injury {hurt:0.0}, staggered seen {mdFlag}; landings staggered {JumpGrid.Staggers - mdStaggers}, hurt {JumpGrid.HurtLandings - mdHurts}");
                    if (!mdJ2.IsValid)
                    {
                        Info("D6 no level-2 edge over open ground on this map: the two-floor jump is checked by mode gravity (G1)");
                        MdEnd(map, "D6", "md_d7");
                        return true;
                    }
                    Pawn p = MdCrew(map).Skip(1).FirstOrDefault() ?? muP1;
                    muP2 = p;
                    Teleport(p, mdJ2);
                    ReachFog.Run(map);
                    Draft(p, true);
                    mdHurt0 = MdInjury(p);
                    mdHurts = JumpGrid.HurtLandings;
                    p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, mdJ2Down), JobTag.Misc);
                    MuRun(40, 44100, () => ProjectZ.FloorOf(muP2.Position, map) == 0 && JumpGrid.HurtLandings > mdHurts, "md_d6_j2");
                    return true;
                }

                case "md_d6_j2":
                {
                    float hurt = MdInjury(muP2) - mdHurt0;
                    Result("D6 a jump of two floors (level-2 edge to the ground): bruised", ProjectZ.FloorOf(muP2.Position, map) == 0 && hurt > 0f && JumpGrid.HurtLandings > mdHurts,
                        $"{muP2} at {muP2.Position}, new injury {hurt:0.0}; landings hurt {JumpGrid.HurtLandings - mdHurts}");
                    MdEnd(map, "D6", "md_d7");
                    return true;
                }

                // ------------------------------------------------------------ D7: the two-way x-ray
                case "md_d7":
                {
                    if (xrType == null)
                    {
                        Info("D7 skipped: IsoRim's MineXray not loaded");
                        Finish();
                        return true;
                    }
                    MdStart(map, MuRect(new[] { mdG0 }, map, 5), false);
                    new Designator_Mine().DesignateMultiCell(new[] { MdM1, MdM2 });
                    ShotAt(map, 0, MdM1, 7f);
                    MuPointer(MdM1);
                    Find.UIRoot.screenshotMode.Active = true;
                    Find.DesignatorManager.Select(new Designator_Mine());
                    muShotName = "md_xray_mine_ground";
                    muShotNext = "md_d7_ground";
                    Wait(40, "mu_shot");
                    return true;
                }

                case "md_d7_ground":
                    Result("D7 the Mine tool on the ground: the x-ray shows the floor above (none below)", (bool)XrGet("ShownUp") && !(bool)XrGet("ShownDown"),
                        $"up {XrGet("ShownUp")} ({XrGet("OutlineQuadsUp")} quads), down {XrGet("ShownDown")}");
                    ShotAt(map, 1, MdM1, 7f);
                    MuPointer(L1(MdM1, map));
                    Find.UIRoot.screenshotMode.Active = true;
                    Find.DesignatorManager.Select(new Designator_Mine());
                    muShotName = "md_xray_mine_l1";
                    muShotNext = "md_d7_l1";
                    Wait(40, "mu_shot");
                    return true;

                case "md_d7_l1":
                    Result("D7 the Mine tool on level 1: the x-ray shows both ways", (bool)XrGet("ShownUp") && (bool)XrGet("ShownDown"),
                        $"up {XrGet("ShownUp")} ({XrGet("OutlineQuadsUp")} quads), down {XrGet("ShownDown")} ({XrGet("OutlineQuads")} quads)");
                    Find.DesignatorManager.Deselect();
                    Wait(20, "md_d7_none");
                    return true;

                case "md_d7_none":
                    Result("D7 no tool: no x-ray", !(bool)XrGet("Shown"), $"shown {XrGet("Shown")}");
                    MdEnd(map, "D7", null);
                    return true;
            }
            return false;
        }
    }
}
