using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode thickrock, the checks after MB, each from the reloaded save, with real ticks.
    // 2 storeys (the mountain has no top: Column.Top is the level-1 rock under its thick roof):
    //   S1 a manned mortar fires on the mountain: every shell ends, none explodes under the thick roof, the roofs stay
    //   S2 a bombardment, S3 a power beam by their targeters: they end, the thick roofs and the ground rock stay
    //   S4 the skyfaller incidents (meteorite, ship chunk, cargo pods) and skyfallers aimed at the mountain: all land, none
    //      stuck; a drop pod target there is refused
    // 3 storeys and more (edge rock lies between the ground and the higher tops):
    //   L1 a colonist builds an emergency ladder from the ground onto the edge rock and a second from there onto a higher
    //      top, walks up to the top over both and back down
    // The raiders' way over the edge rock: case mt2 of mode raidladders.
    public static partial class TestRunner
    {
        private static IntVec3 tkG, tkLA, tkLP1, tkLP2, tkLT, tkLDest;
        private static Rot4 tkLR1, tkLR2;
        private static int tkRock0, tkThick0, tkGround0;
        private static bool tkSeen;
        private static CellRect tkC;
        private static int tkCTop, tkCFalls;
        private static Thing tkCSteel;
        private static readonly List<IntVec3> tkOld = new List<IntVec3>();
        private static IntVec3 tkOldNext, tkOldFrom, tkOldUp;
        private static readonly List<IntVec3> tkInner = new List<IntVec3>();
        private static readonly List<string> tkFalls = new List<string>();

        // The rock around tkG.
        private static void TkCount(Map map, out int rock, out int thick, out int ground)
        {
            rock = thick = ground = 0;
            foreach (IntVec3 g in GenRadial.RadialCellsAround(tkG, 25f, true))
            {
                if (!g.InBounds(map) || g.z >= ProjectZ.GroundZ(map))
                {
                    continue;
                }
                IntVec3 u = L1(g, map);
                rock += TkRock(u, map) ? 1 : 0;
                thick += TkThick(g, map) ? 1 : 0;
                ground += TkRock(g, map) ? 1 : 0;
            }
        }

        private static List<Thing> TkFlying(Map map) => map.listerThings.AllThings.Where(t => (t is Skyfaller || t is Projectile || t is Bombardment || t is PowerBeam) && t.def.defName != "FlyerLeaving" && t.def.defName != "Spark").ToList();   // (wild flyers and the sparks of fires come and go)

        // Open ground for a mortar, and a target 32-55 cells off: an open cell at the mountain's foot (the game refuses to aim
        // a shell or an orbital strike at a thick roof; what falls on the mountain is what misses and scatters).
        private static IntVec3 TkTarget(Map map, out IntVec3 at)
        {
            at = IntVec3.Invalid;
            foreach (IntVec3 s in Near(tkS, 25, map))
            {
                if (OtStand(s, map) && GenAdj.AdjacentCells.All(o => OtStand(s + o, map) && (s + o).GetFirstItem(map) == null))
                {
                    at = s;
                    break;
                }
            }
            IntVec3 from = at;
            if (!from.IsValid)
            {
                return IntVec3.Invalid;
            }
            foreach (IntVec3 g in Near(from, 60, map))
            {
                float d = g.DistanceTo(from);
                if (d >= 32f && d <= 55f && !g.Roofed(map) && Mountains.WalkableRaw(g, map) && GenRadial.RadialCellsAround(g, 7f, true).Count(c => c.InBounds(map) && c.z < ProjectZ.GroundZ(map) && TkThick(c, map) && TkRock(L1(c, map), map)) >= 50)
                {
                    return g;
                }
            }
            return IntVec3.Invalid;
        }

        // The thick-roof cell with rock above nearest the target.
        private static IntVec3 TkDeep(Map map) => Near(tkG, 12, map).First(c => TkThick(c, map) && TkRock(L1(c, map), map));

        // An open cell 4+ cells from the target with a line of sight to it (the targeters need one).
        private static IntVec3 TkBeside(Map map) => Near(tkG, 15, map).First(c => c.DistanceTo(tkG) >= 4f && !c.Roofed(map) && c.Standable(map) && GenSight.LineOfSight(c, tkG, map));

        private static IntVec3 tkTunnelFrom(Map map) => new IntVec3(tkG.x, 0, Mathf.Max(1, tkG.z - 30));

        private static void TkStrikeStart(Map map)
        {
            MuStart(map, CellRect.CenteredOn(tkG, 30), false);
            TkCount(map, out tkRock0, out tkThick0, out tkGround0);
            sxBlasts.Clear();
            tkSeen = false;
            // the ground rock inside the mountain: under level-1 rock, rock all round it
            tkInner.Clear();
            tkInner.AddRange(GenRadial.RadialCellsAround(tkG, 25f, true).Where(g => g.InBounds(map) && g.z >= 1 && g.z < ProjectZ.GroundZ(map) - 1 && g.x >= 1 && g.x < map.Size.x - 1 && TkRock(g, map) && TkRock(L1(g, map), map) && GenAdj.AdjacentCells.All(o => TkRock(g + o, map))));
            foreach (IntVec3 c in GenRadial.RadialCellsAround(tkG, 3f, true))
            {
                map.fogGrid.Unfog(c);   // (a player aims at what is seen)
            }
        }

        private static void TkStrikeCheck(Map map, string name, System.Func<bool> ok, string detail, bool groundToo = true)
        {
            TkCount(map, out int rock, out int thick, out int ground);
            List<Thing> left = TkFlying(map);
            string where = string.Join(", ", sxBlasts.GroupBy(b => ProjectZ.FloorOf(b.centre, map)).OrderBy(x => x.Key).Select(x => $"level {x.Key}: {x.Count()}"));
            int inRock = sxBlasts.Count(b => TkRock(b.centre, map)), roofed = sxBlasts.Count(b => TkThick(b.centre, map));
            Info($"{name}: blasts {sxBlasts.Count} ({where}); centred under a thick roof {roofed}, in rock now {inRock}; within 25 cells: level-1 rock {tkRock0} -> {rock}, ground thick roofs {tkThick0} -> {thick}, ground rock {tkGround0} -> {ground}; fires {map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count}");
            TkRule(map, "after " + name.Substring(0, 2));
            int innerLost = tkInner.Count(g => !TkRock(g, map));
            List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire).ToList();
            Info($"{name}: fires by level {string.Join(", ", fires.GroupBy(f => ProjectZ.FloorOf(f.Position, map)).OrderBy(x => x.Key).Select(x => $"{x.Key}: {x.Count()}"))}, in rock {fires.Count(f => TkRock(f.Position, map))}");
            Result(name, left.Count == 0 && (!groundToo || innerLost == 0) && ok(), $"{detail}; still in the air {left.Count} {string.Join(",", left.Select(t => $"{t.def.defName}@{t.Position}"))}; ground rock inside the mountain lost {innerLost} of {tkInner.Count}");
        }

        private static void TkNext(string next)
        {
            Find.UIRoot.screenshotMode.Active = false;
            stage = next;
            pendingLoad = "pz_thickrock";
        }

        private static void TkOrderedGoto(Pawn p, IntVec3 dest)
        {
            Draft(p, true);
            p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, dest), JobTag.DraftedOrder);
        }

        private static Building_SiegeLadder TkLadderAt(IntVec3 c, Map map) => c.GetThingList(map).OfType<Building_SiegeLadder>().FirstOrDefault();

        private static bool TkLadderSite(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            IntVec3 home = new IntVec3(map.Size.x / 2, 0, a.groundZ / 2);
            foreach (IntVec3 ga in Near(home, 120, map))
            {
                if (ga.x < 8 || ga.z < 8 || ga.x >= map.Size.x - 8 || ga.z >= a.groundZ - 8 || !OtStand(ga, map))
                {
                    continue;
                }
                for (int i = 0; i < 4; i++)
                {
                    Rot4 r1 = new Rot4(i);   // from the edge rock towards the open ground
                    IntVec3 p1 = L1(ga - r1.FacingCell, map);
                    if (!TmTop(p1, map) || map.fogGrid.IsFogged(p1) || !LadderSite.Find(p1, r1, map, out IntVec3 foot1, out _, out _) || foot1 != ga)
                    {
                        continue;
                    }
                    // along the edge rock to a cell under a higher top
                    HashSet<IntVec3> seen = new HashSet<IntVec3> { p1 };
                    Queue<IntVec3> q = new Queue<IntVec3>();
                    q.Enqueue(p1);
                    while (q.Count > 0 && seen.Count < 60)
                    {
                        IntVec3 p = q.Dequeue();
                        foreach (IntVec3 d in GenAdj.CardinalDirections)
                        {
                            IntVec3 n = p + d;
                            if (ProjectZ.SameBand(n, p, map) && TmTop(n, map) && Mountains.WalkableRaw(n, map) && seen.Add(n))
                            {
                                q.Enqueue(n);
                            }
                        }
                        if (p == p1)
                        {
                            continue;
                        }
                        for (int k = 0; k < 4; k++)
                        {
                            Rot4 r2 = new Rot4(k);   // from the higher top towards the edge rock
                            IntVec3 gt = Levels.Ground(p, map) - r2.FacingCell;
                            int L = Column.NaturalTop(gt, map);
                            IntVec3 t = L >= 2 ? Levels.Cell(gt, L, map) : IntVec3.Invalid;
                            if (!t.IsValid || !TmTop(t, map) || map.fogGrid.IsFogged(t) || !LadderSite.Find(t, r2, map, out IntVec3 foot2, out _, out _) || foot2 != p)
                            {
                                continue;
                            }
                            List<IntVec3> on = GenAdj.CardinalDirections.Select(d => t + d).Where(n => ProjectZ.SameBand(n, t, map) && TmTop(n, map) && Mountains.WalkableRaw(n, map)).ToList();
                            if (on.Count == 0)
                            {
                                continue;
                            }
                            tkLA = ga;
                            tkLP1 = p1;
                            tkLR1 = r1;
                            tkLP2 = p;
                            tkLT = t;
                            tkLR2 = r2;
                            tkLDest = on[0];
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private static bool StepThickRock2(Map map)
        {
            switch (stage)
            {
                // ------------------------------------------------------------ a map from before the rule
                case "tk_old_make":
                {
                    // (run on master's build: a tunnel dug under H 1 columns, their tops kept, then saved as pz_thickold)
                    IntVec3 s0 = IntVec3.Invalid;
                    List<IntVec3> line = new List<IntVec3>();
                    AnnexMapComponent an = ProjectZ.Annex(map);
                    foreach (IntVec3 s in Near(new IntVec3(map.Size.x / 2, 0, an.groundZ / 2), 120, map).Where(g => OtStand(g, map)))
                    {
                        foreach (IntVec3 d in GenAdj.CardinalDirections)
                        {
                            List<IntVec3> l = new List<IntVec3>();
                            int low = 0;
                            for (int k = 1; k <= 9 && low < 4; k++)
                            {
                                IntVec3 c = s + d * k;
                                if (!OtRock(c, map))
                                {
                                    break;
                                }
                                l.Add(c);
                                if (TkThick(c, map) && Column.NaturalTop(c, map) == 1 && TmTop(L1(c, map), map))
                                {
                                    low++;
                                }
                            }
                            if (low == 4 && !map.fogGrid.IsFogged(l[0]))
                            {
                                line = l;
                                s0 = s;
                                break;
                            }
                        }
                        if (s0.IsValid)
                        {
                            break;
                        }
                    }
                    Result("old map: a tunnel under thick-roof H 1 columns", s0.IsValid, $"{string.Join(" ", line)} from {s0}");
                    if (!s0.IsValid)
                    {
                        Finish();
                        return true;
                    }
                    tkTunnel.Clear();
                    tkTunnel.AddRange(line);
                    otMinerId = Colonists(map).First(p => !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining)).thingIDNumber;
                    OtMine(map, line, s0, true);
                    MuRun(2500, 73000, () => tkTunnel.All(c => !OtRock(c, map)), "tk_old_made");
                    return true;
                }

                case "tk_old_made":
                    BareTop.Off = false;
                    Find.DesignatorManager.Deselect();
                    Info($"old map dug: {string.Join("; ", tkTunnel.Select(c => TkCell(c, map)))}");
                    GameDataSaveLoader.SaveGame("pz_thickold");
                    Finish();
                    return true;

                case "tk_old_load":
                    stage = "tk_old_check";
                    pendingLoad = "pz_thickold";
                    return true;

                case "tk_old_check":
                {
                    tkOld.Clear();
                    AnnexMapComponent an = ProjectZ.Annex(map);
                    foreach (IntVec3 g in new CellRect(0, 0, map.Size.x, an.groundZ))
                    {
                        if (TkThick(g, map) && !OtRock(g, map) && g.GetTerrain(map).defName.Contains("RoughHewn") && !OtRock(L1(g, map), map) && ProjectZ.HasFloor(L1(g, map), map))
                        {
                            tkOld.Add(g);
                        }
                    }
                    int thick = 0, stale = 0;
                    for (int L = 0; L <= an.MaxLevel; L++)
                    {
                        foreach (IntVec3 g in new CellRect(0, 0, map.Size.x, an.groundZ))
                        {
                            IntVec3 c = Levels.Cell(g, L, map);
                            bool t = TkThick(c, map);
                            thick += t ? 1 : 0;
                            stale += t && !OtRock(FloorRoof.Above(c, map), map) ? 1 : 0;
                        }
                    }
                    Info($"O old map loaded: thick roofs {thick}, of them without rock above {stale}; the old tunnel: {string.Join("; ", tkOld.Select(c => TkCell(c, map)))}");
                    Result("O1 a tunnel dug under H 1 columns before the rule is there with its thick roof", tkOld.Count >= 4, $"{tkOld.Count} cells");
                    MuStart(map, CellRect.Empty, false);
                    otMinerId = Colonists(map).First(p => !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining)).thingIDNumber;
                    MdWork(map, new List<Pawn>(), WorkTypeDefOf.Mining);
                    StartTicks(2500, "tk_old_2");
                    return true;
                }

                case "tk_old_2":
                {
                    MuOutside(map, "O2 2500 ticks on the loaded old map (the whole map)");
                    Result("O2 nothing collapsed", PZStats.slabCollapses == muCollapses, $"collapses {PZStats.slabCollapses - muCollapses}");
                    // one more cell at the tunnel's end, under a bare top: as before the rule
                    tkOldNext = IntVec3.Invalid;
                    foreach (IntVec3 c in tkOld)
                    {
                        foreach (IntVec3 d in GenAdj.CardinalDirections)
                        {
                            if (!tkOldNext.IsValid && OtRock(c + d, map) && TkThick(c + d, map) && OtBareTop(c + d, map))
                            {
                                tkOldNext = c + d;
                                tkOldFrom = c;
                            }
                        }
                    }
                    if (!tkOldNext.IsValid)
                    {
                        Info("O3 no thick-roof rock under a bare top beside the tunnel");
                        stage = "tk_old_4";
                        return true;
                    }
                    OtMine(map, new[] { tkOldNext }, tkOldFrom, false);
                    MuRun(600, 73100, () => !OtRock(tkOldNext, map), "tk_old_3");
                    return true;
                }

                case "tk_old_3":
                    Info($"O3 the rock beside the tunnel mined: {TkCell(tkOldNext, map)}; the tunnel now: {string.Join("; ", tkOld.Select(c => TkCell(c, map)))}");
                    Result("O3 a thick-roof rock under a bare top, mined on the old map, opens the sky; the rest of the tunnel keeps its thick roof", !map.roofGrid.Roofed(tkOldNext) && tkOld.All(c => TkThick(c, map)), $"{TkCell(tkOldNext, map)}");
                    stage = "tk_old_4";
                    return true;

                case "tk_old_4":
                {
                    // rock above going on the old map: the cell under it follows the rule from then on
                    TkSites(map);
                    if (tkUp.Count == 0)
                    {
                        Info("O4 no level-1 rock face");
                        Finish();
                        return true;
                    }
                    tkOldUp = tkUp[0];
                    Info($"O4 before: {TkCell(tkOldUp, map)}; below {TkCell(FloorRoof.Below(tkOldUp, map), map)}");
                    OtMine(map, new[] { tkOldUp }, tkUpS, false);
                    MuRun(600, 73200, () => !OtRock(tkOldUp, map), "tk_old_5");
                    return true;
                }

                case "tk_old_5":
                {
                    IntVec3 b = FloorRoof.Below(tkOldUp, map);
                    Info($"O4 after: {TkCell(tkOldUp, map)}; below {TkCell(b, map)}");
                    Result("O4 rock above going on the old map: the cell under it has no thick roof from then on", !TkThick(b, map), TkCell(b, map));
                    Finish();
                    return true;
                }

                // ------------------------------------------------------------ S1: a mortar
                case "tk_s_mortar":
                {
                    tkG = TkTarget(map, out IntVec3 at);
                    if (!tkG.IsValid)
                    {
                        Result("S sites: a mortar spot and a mountain target 32-55 cells off", false, $"spot {at}");
                        Finish();
                        return true;
                    }
                    TkStrikeStart(map);
                    sxShells.Clear();
                    Building_TurretGun m = (Building_TurretGun)Place(DefDatabase<ThingDef>.GetNamed("Turret_Mortar"), null, at, map);
                    Pawn p = SxPawn(map, at + IntVec3.South * 2);
                    // (the gunner stands at the mortar and mans it each tick, as the job's toil does; the turret's aim, miss and
                    // firing are the game's)
                    CompMannable man = m.TryGetComp<CompMannable>();
                    CompChangeableProjectile load = m.gun.TryGetComp<CompChangeableProjectile>();
                    Teleport(p, m.InteractionCell);
                    m.gun.TryGetComp<CompChangeableProjectile>()?.LoadShell(DefDatabase<ThingDef>.GetNamed("Shell_HighExplosive"), 1);
                    Thing shells = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Shell_HighExplosive"));
                    shells.stackCount = 12;
                    GenPlace.TryPlaceThing(shells, at + IntVec3.South * 2 + IntVec3.East, map, ThingPlaceMode.Near);
                    IntVec3 deep = TkDeep(map);
                    Info($"S1 aim on the thick roof {TkCell(deep, map)}: can hit {m.AttackVerb.CanHitTarget(new LocalTargetInfo(deep))}, on its level-1 rock {TkCell(L1(deep, map), map)}: can hit {m.AttackVerb.CanHitTarget(new LocalTargetInfo(L1(deep, map)))}, Column.Top {Column.Top(deep, map)}");
                    m.OrderAttack(new LocalTargetInfo(tkG));
                    Info($"S1 a manned mortar at {at} fires on {TkCell(tkG, map)} ({tkG.DistanceTo(at):0} cells), Column.Top {Column.Top(tkG, map)}; can hit {m.AttackVerb.CanHitTarget(new LocalTargetInfo(tkG))}");
                    MuRun(60, 72000, () => sxShells.Count >= 10, "tk_s_mortar2", () =>
                    {
                        man.ManForATick(p);
                        if (!load.Loaded)
                        {
                            load.LoadShell(DefDatabase<ThingDef>.GetNamed("Shell_HighExplosive"), 1);
                        }
                    });
                    return true;
                }

                case "tk_s_mortar2":
                    foreach (Building_TurretGun t in map.listerThings.AllThings.OfType<Building_TurretGun>().ToList())
                    {
                        Info($"S1 the mortar fired {sxShells.Count} shells at {tkG}");
                        t.Destroy();
                    }
                    // shells that do come down on the rock (a miss, a mod): real flights onto thick-roof cells
                    IntVec3 from = tkTunnelFrom(map);
                    ThingDef shellDef = DefDatabase<ThingDef>.GetNamed("Bullet_Shell_HighExplosive");
                    foreach (IntVec3 c in GenRadial.RadialCellsAround(TkDeep(map), 5f, true).Where(c => TkThick(c, map) && TkRock(L1(c, map), map)).Take(6))
                    {
                        Projectile shell = (Projectile)GenSpawn.Spawn(shellDef, from, map);
                        shell.Launch(Colonists(map).First(), from.ToVector3Shifted(), new LocalTargetInfo(c), new LocalTargetInfo(c), ProjectileHitFlags.IntendedTarget);
                    }
                    StartTicks(900, "tk_s_mortar3");
                    return true;

                case "tk_s_mortar3":
                {
                    int under = sxBlasts.Count(b => TkThick(b.centre, map));
                    TkStrikeCheck(map, "S1 mortar shells on the no-top mountain: every shell ends, none explodes under the thick roof, the inner rock stays", () => sxShells.Count >= 16 && under == 0, $"shells fired {sxShells.Count} (10 by the mortar, 6 straight onto the rock), blasts {sxBlasts.Count}, under a thick roof {under}");
                    TkNext("tk_s_bomb");
                    return true;
                }

                // ------------------------------------------------------------ S2: bombardment
                case "tk_s_bomb":
                {
                    tkG = TkTarget(map, out IntVec3 at);
                    TkStrikeStart(map);
                    Pawn p = SxPawn(map, TkBeside(map));
                    Verb v = SxTargeter(p, "OrbitalTargeterBombardment");
                    v?.OrderForceTarget(new LocalTargetInfo(tkG));
                    Info($"S2 bombardment on {TkCell(tkG, map)} by {p} at {p.Position}: job {p.CurJob?.def?.defName}; can hit {v?.CanHitTarget(new LocalTargetInfo(tkG))}; on the thick roof {TkDeep(map)}: can hit {v?.CanHitTarget(new LocalTargetInfo(TkDeep(map)))}");
                    MuRun(30, 72100, () =>
                    {
                        bool now = map.listerThings.ThingsOfDef(ThingDefOf.Bombardment).Any();
                        tkSeen |= now;
                        return tkSeen && !now;
                    }, "tk_s_bomb2");
                    return true;
                }

                case "tk_s_bomb2":
                    StartTicks(300, "tk_s_bomb3");
                    return true;

                case "tk_s_bomb3":
                    TkStrikeCheck(map, "S2 a bombardment on the no-top mountain ends, the ground rock inside stays", () => tkSeen, $"bombardment seen {tkSeen}, blasts {sxBlasts.Count}");
                    TkNoon(map);
                    MuShot(map, "tk_s_bomb", 1, tkG, 16f, "tk_s_bomb4");
                    return true;

                case "tk_s_bomb4":
                    TkNext("tk_s_beam");
                    return true;

                // ------------------------------------------------------------ S3: power beam
                case "tk_s_beam":
                {
                    tkG = TkTarget(map, out IntVec3 at);
                    TkStrikeStart(map);
                    Pawn p = SxPawn(map, TkBeside(map));
                    Verb v = SxTargeter(p, "OrbitalTargeterPowerBeam");
                    v?.OrderForceTarget(new LocalTargetInfo(tkG));
                    Info($"S3 power beam on {TkCell(tkG, map)} by {p} at {p.Position}: job {p.CurJob?.def?.defName}; can hit {v?.CanHitTarget(new LocalTargetInfo(tkG))}; on the thick roof {TkDeep(map)}: can hit {v?.CanHitTarget(new LocalTargetInfo(TkDeep(map)))}");
                    MuRun(30, 72200, () =>
                    {
                        bool now = map.listerThings.ThingsOfDef(ThingDefOf.PowerBeam).Any();
                        tkSeen |= now;
                        return tkSeen && !now;
                    }, "tk_s_beam2");
                    return true;
                }

                case "tk_s_beam2":
                    StartTicks(300, "tk_s_beam3");
                    return true;

                case "tk_s_beam3":
                    TkStrikeCheck(map, "S3 a power beam on the no-top mountain ends, the ground rock inside stays", () => tkSeen, $"beam seen {tkSeen}");
                    TkNext("tk_s_sky");
                    return true;

                // ------------------------------------------------------------ S4: skyfallers
                case "tk_s_sky":
                {
                    tkG = TkTarget(map, out _);
                    TkStrikeStart(map);
                    tkFalls.Clear();
                    Rand.PushState(72300);
                    foreach (string name in new[] { "MeteoriteImpact", "ShipChunkDrop", "ResourcePodCrash" })
                    {
                        IncidentDef d = DefDatabase<IncidentDef>.GetNamedSilentFail(name);
                        for (int i = 0; d != null && i < 3; i++)
                        {
                            int before = TkFlying(map).Count;
                            bool ran = d.Worker.TryExecute(StorytellerUtility.DefaultParmsNow(d.category, map));
                            tkFalls.Add($"{name} {ran} (+{TkFlying(map).Count - before})");
                        }
                    }
                    Rand.PopState();
                    List<Thing> random = TkFlying(map);
                    int onMtn = random.Count(t => TkThick(ProjectZ.GroundCellOf(t.Position, map), map));
                    Result("S4 the sky incidents never pick the mountain", onMtn == 0, $"{string.Join(", ", tkFalls)}; {random.Count} in the air, {onMtn} over a thick roof");
                    IntVec3 deep = TkDeep(map);
                    bool pod = DropCellFinder.CanPhysicallyDropInto(deep, map, true) || DropCellFinder.CanPhysicallyDropInto(L1(deep, map), map, true);
                    Result("S4 a drop pod can not be aimed at the mountain (ground or level 1)", !pod, $"{deep}: {pod}");
                    // aimed by hand (a mod, a dev tool): a meteorite and a ship chunk on the mountain
                    IntVec3 aim = deep;
                    SkyfallerMaker.SpawnSkyfaller(ThingDefOf.MeteoriteIncoming, new List<Thing> { ThingMaker.MakeThing(ThingDefOf.Steel) }, aim, map);
                    SkyfallerMaker.SpawnSkyfaller(ThingDefOf.ShipChunkIncoming, ThingDefOf.ShipChunk, aim + IntVec3.West, map);
                    MuRun(20, 72400, () => TkFlying(map).Count == 0, "tk_s_sky2");
                    return true;
                }

                case "tk_s_sky2":
                    StartTicks(120, "tk_s_sky3");
                    return true;

                case "tk_s_sky3":
                {
                    List<Thing> near = map.listerThings.AllThings.Where(t => (t.def == ThingDefOf.ShipChunk || t.def == ThingDefOf.Steel) && ProjectZ.GroundCellOf(t.Position, map).DistanceToSquared(tkG) < 900).ToList();
                    Info($"S4 still flying: {string.Join(", ", TkFlying(map).Select(t => $"{t.def.defName} at {t.Position} L{ProjectZ.FloorOf(t.Position, map)}{(t is Skyfaller f ? " ticks to impact " + f.ticksToImpact : "")}"))}");
                    Info($"S4 aimed, what landed: {string.Join(", ", near.Select(t => t.def.defName + " " + t.Position + " L" + ProjectZ.FloorOf(t.Position, map)))}");
                    TkStrikeCheck(map, "S4 skyfallers aimed at the no-top mountain land on the ground (meteors crush rock there, as vanilla), none stuck", () => near.Count >= 2, $"landed {near.Count}", false);
                    Finish();
                    return true;
                }

                // ------------------------------------------------------------ L1: ladders over the edge rock
                case "tk_l_site":
                {
                    bool site = TkLadderSite(map);
                    Result("L1 a site: open ground, edge rock with a level-1 top, a higher top beside that edge rock", site, $"ground {tkLA}; ladder 1 on {tkLP1} {tkLR1}; ladder 2 on {tkLT} {tkLR2} down to {tkLP2}; then {tkLDest}");
                    if (!site)
                    {
                        Finish();
                        return true;
                    }
                    Pawn miner = OtPawn(map, otMinerId);
                    MuStart(map, MuRect(new[] { tkLA, tkLP1, tkLP2, tkLT }, map, 3), false);
                    MdWork(map, new List<Pawn> { miner }, WorkTypeDefOf.Construction);
                    Draft(miner, false);
                    Teleport(miner, tkLA);
                    AcceptanceReport a1 = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_EmergencyLadder, tkLP1, tkLR1, map);
                    AcceptanceReport a2 = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_EmergencyLadder, tkLT, tkLR2, map);
                    Result("L1 both emergency ladders can be placed", a1.Accepted && a2.Accepted, $"edge rock: {a1.Accepted} {a1.Reason}; top: {a2.Accepted} {a2.Reason}");
                    if (!a1.Accepted || !a2.Accepted)
                    {
                        Finish();
                        return true;
                    }
                    GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_EmergencyLadder, tkLP1, map, tkLR1, Faction.OfPlayer, null);
                    GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_EmergencyLadder, tkLT, map, tkLR2, Faction.OfPlayer, null);
                    MuRun(400, 72500, () => TkLadderAt(tkLP1, map) != null && TkLadderAt(tkLT, map) != null, "tk_l_built");
                    return true;
                }

                case "tk_l_built":
                {
                    Pawn miner = OtPawn(map, otMinerId);
                    Result("L1 a colonist on the ground builds both ladders, the second from the edge rock over the first", TkLadderAt(tkLP1, map) != null && TkLadderAt(tkLT, map) != null,
                        $"ladder 1 {TkLadderAt(tkLP1, map)}, ladder 2 {TkLadderAt(tkLT, map)}; builder at {miner.Position} level {Levels.LevelOf(miner.Position, map)}, job {miner.CurJob?.def?.defName}");
                    Info($"L1 from {miner.Position}: reach the top {miner.CanReach(tkLDest, PathEndMode.OnCell, Danger.Deadly)}, reach the ground {miner.CanReach(tkLA, PathEndMode.OnCell, Danger.Deadly)}");
                    TkOrderedGoto(miner, tkLDest);
                    MuRun(120, 72600, () => miner.Position == tkLDest, "tk_l_top");
                    return true;
                }

                case "tk_l_top":
                {
                    Pawn miner = OtPawn(map, otMinerId);
                    Result("L1 the colonist walks up over the edge rock onto the higher top", miner.Position == tkLDest, $"at {miner.Position} level {Levels.LevelOf(miner.Position, map)}, wanted {tkLDest}");
                    TkNoon(map);
                    MuShot(map, "tk_l_top", Levels.LevelOf(tkLT, map), tkLT, 9f, "tk_l_down");
                    return true;
                }

                case "tk_l_down":
                {
                    Find.UIRoot.screenshotMode.Active = false;
                    Pawn miner = OtPawn(map, otMinerId);
                    TkOrderedGoto(miner, tkLA);
                    MuRun(120, 72700, () => miner.Position == tkLA, "tk_l_end");
                    return true;
                }

                case "tk_l_end":
                {
                    Pawn miner = OtPawn(map, otMinerId);
                    Result("L1 the colonist comes back down to the ground over both ladders", miner.Position == tkLA, $"at {miner.Position} level {Levels.LevelOf(miner.Position, map)}");
                    MuOutside(map, "L1");
                    FloorView.Set(0);
                    TkNext("tk_l_collapse");
                    return true;
                }

                // ------------------------------------------------------------ C: vanilla support under a top
                case "tk_l_collapse":
                {
                    // a 15x15 block of the top rock layer gone from under a flat top: past 6.9 cells from rock the thin roof has no
                    // holder and goes as vanilla's thin roofs do, its floor with it; nearer the rock it holds
                    AnnexMapComponent an = ProjectZ.Annex(map);
                    tkC = CellRect.Empty;
                    tkCTop = 0;
                    for (int z = 12; z < an.groundZ - 27 && tkC.IsEmpty; z += 3)
                    {
                        for (int x = 12; x < map.Size.x - 27 && tkC.IsEmpty; x += 3)
                        {
                            CellRect r = new CellRect(x, z, 15, 15);
                            int top = Column.NaturalTop(r.CenterCell, map);
                            if (top >= 2 && r.ExpandedBy(1).Cells.All(g => Column.NaturalTop(g, map) == top && TmTop(Levels.Cell(g, top, map), map) && OtRock(Levels.Cell(g, top - 1, map), map)))
                            {
                                tkC = r;
                                tkCTop = top;
                            }
                        }
                    }
                    Result("C a 15x15 flat block of the top rock layer", !tkC.IsEmpty, $"{tkC}, top level {tkCTop}");
                    if (tkC.IsEmpty)
                    {
                        Finish();
                        return true;
                    }
                    tkG = tkC.CenterCell;
                    Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    steel.stackCount = 30;
                    GenSpawn.Spawn(steel, Levels.Cell(tkG, tkCTop, map), map);
                    tkCSteel = steel;
                    MuStart(map, tkC.ExpandedBy(2), false);
                    tkCFalls = PZStats.slabCollapses;
                    foreach (IntVec3 g in tkC)
                    {
                        Levels.Cell(g, tkCTop - 1, map).GetFirstMineable(map).Destroy(DestroyMode.Vanish);
                    }
                    Info($"C before ticks: centre {TkCell(Levels.Cell(tkG, tkCTop - 1, map), map)}, marked to collapse {map.roofCollapseBuffer.CellsMarkedToCollapse.Count}");
                    StartTicks(600, "tk_l_collapse2");
                    return true;
                }

                case "tk_l_collapse2":
                {
                    int gone = 0, kept = 0, keptFar = 0, goneNear = 0;
                    foreach (IntVec3 g in tkC)
                    {
                        bool floor = ProjectZ.HasFloor(Levels.Cell(g, tkCTop, map), map);
                        // (the game's rule: a holder within 6.9 cells)
                        bool near = GenRadial.RadialCellsAround(g, 6.9f, true).Any(n => n.InBounds(map) && n.z < ProjectZ.GroundZ(map) && OtRock(Levels.Cell(n, tkCTop - 1, map), map));
                        gone += floor ? 0 : 1;
                        kept += floor ? 1 : 0;
                        keptFar += floor && !near ? 1 : 0;
                        goneNear += !floor && near ? 1 : 0;
                    }
                    Info($"C after: floors gone {gone}, kept {kept}, kept beyond the support radius {keptFar}, gone within it {goneNear}; collapses {PZStats.slabCollapses - tkCFalls}; steel at {tkCSteel.PositionHeld} level {ProjectZ.FloorOf(tkCSteel.PositionHeld, map)}");
                    Result("C unsupported thin roofs under a top go as vanilla: past the support radius the floor falls, what was on it comes down, within it the floor stays",
                        gone > 0 && kept > 0 && keptFar == 0 && ProjectZ.FloorOf(tkCSteel.PositionHeld, map) < tkCTop, $"gone {gone}, kept {kept}, kept far {keptFar}, gone near {goneNear}");
                    TkRule(map, "after C");
                    TkNoon(map);
                    MuShot(map, "tk_l_collapse", tkCTop, tkG, 14f, "tk_l_done");
                    return true;
                }

                case "tk_l_done":
                    Find.UIRoot.screenshotMode.Active = false;
                    FloorView.Set(0);
                    Finish();
                    return true;
            }
            return false;
        }
    }
}
