using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode holefog, the fixtures (fixed-seed tall-mountain map as mode minedown: -projectztallmountains -projectzfloors=4
    // -projectzmapsize=250). Setup finds three sites far apart, saves pz_holefog; every check reloads it, builds its own
    // fixture (rock carved by script into a hidden, fogged cave: what the player would dig into) and runs real jobs:
    //   F1 mine below twice from a level-2 top (a colonist digs a pit into level 1, one falls into it (no jumps), digs on into the ground
    //      rock): a two-floor drop over a hidden ground cave next to the bottom; the column and the cave are revealed,
    //      the outside unchanged
    //   F2 mine above from a ground chamber into level-1 rock next to a hidden level-1 cave: the cave is revealed (a
    //      covered hole joins the spaces above and below it, ReachFog)
    //   F3 Remove roof under a bare level-1 top from a ground chamber: a hole to the sky; the fogged plateau round it is
    //      not opened (nobody can get up there; it opens where pawns can reach, ReachFog)
    public static partial class TestRunner
    {
        private static IntVec3 hfG1 = IntVec3.Invalid, hfG2 = IntVec3.Invalid, hfG3 = IntVec3.Invalid;
        private static readonly List<int> hfCrewIds = new List<int>();
        private static readonly List<IntVec3> hfCave = new List<IntVec3>();
        private static int hfFogAround;
        private static bool hfSeen;
        private static int hfSiteR;

        // (after the fixture is built: what lies outside the site must not change from here on)
        private static void HfWatch(Map map, IntVec3 g) => MuStart(map, MuRect(new[] { g }, map, hfSiteR), true);

        private static IntVec3 HL(IntVec3 g, int L, Map map) => Levels.Cell(g, L, map);

        private static List<Pawn> HfCrew(Map map) => hfCrewIds.Select(id => map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == id)).Where(p => p != null).ToList();

        private static bool HfFogRock(IntVec3 c, Map map) => MuRock(c, map) && map.fogGrid.IsFogged(c);

        // Every cell of the (2r+1)^2 block round g on level L passes 'ok'.
        private static bool HfBlock(IntVec3 g, int r, Map map, Func<IntVec3, bool> ok)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    IntVec3 x = g + new IntVec3(dx, 0, dz);
                    if (!x.InBounds(map) || x.z >= ProjectZ.GroundZ(map) || !ok(x))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool HfSites(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            IntVec3 home = new IntVec3(map.Size.x / 2, 0, a.groundZ / 2);
            List<IntVec3> near = Near(home, 120, map).Where(g => g.x > 10 && g.z > 10 && g.x < map.Size.x - 10 && g.z < a.groundZ - 10).ToList();
            // F1: a level-2 top over level-1 and ground rock (3x3 round g, a standing row at z-2 on level 2); ground rock east
            // of it, fogged, under a thick roof and level-1 rock: the hidden cave
            hfG1 = near.FirstOrDefault(g => Column.NaturalTop(g, map) == 2
                && HfBlock(g, 1, map, x => MuRock(x, map) && MuRock(HL(x, 1, map), map) && TmTop(HL(x, 2, map), map) && Column.NaturalTop(x, map) == 2)
                && HfBlock(g + new IntVec3(0, 0, -2), 1, map, x => TmTop(HL(x, 2, map), map))
                && HfBlock(g + new IntVec3(3, 0, 0), 3, map, x => HfFogRock(x, map) && MuThick(x, map) && Column.NaturalTop(x, map) >= 2 && MuRock(HL(x, 1, map), map)));
            // F2: ground rock under level-1 rock under level-2 rock (radius 4, all fogged on levels 0 and 1)
            hfG2 = near.FirstOrDefault(g => (!hfG1.IsValid || !g.InHorDistOf(hfG1, 25f))
                && HfBlock(g, 4, map, x => HfFogRock(x, map) && MuThick(x, map) && HfFogRock(HL(x, 1, map), map) && MuRock(HL(x, 2, map), map) && Column.NaturalTop(x, map) >= 3));
            // F3: ground rock under a bare level-1 top, the 7x7 plateau round it fogged (its interior)
            hfG3 = near.FirstOrDefault(g => (!hfG1.IsValid || !g.InHorDistOf(hfG1, 25f)) && (!hfG2.IsValid || !g.InHorDistOf(hfG2, 25f))
                && HfBlock(g, 3, map, x => HfFogRock(x, map) && MuThick(x, map) && Column.NaturalTop(x, map) == 1 && TmTop(HL(x, 1, map), map) && map.fogGrid.IsFogged(HL(x, 1, map))));
            return hfG1.IsValid && hfG2.IsValid && hfG3.IsValid;
        }

        // Rock removed by script (a fixture: the cave the player digs into, or a chamber to start in); fog as asked.
        private static void HfCarve(Map map, IEnumerable<IntVec3> cells, bool seen)
        {
            foreach (IntVec3 c in cells)
            {
                c.GetEdifice(map)?.Destroy(DestroyMode.Vanish);
            }
            foreach (IntVec3 c in cells)
            {
                if (seen)
                {
                    map.fogGrid.Unfog(c);
                }
                else if (!map.fogGrid.IsFogged(c))
                {
                    map.fogGrid.Refog(CellRect.SingleCell(c));
                }
            }
        }

        private static int HfFogged(IEnumerable<IntVec3> cells, Map map) => cells.Count(c => map.fogGrid.IsFogged(c));

        private static void HfStart(Map map, IntVec3 g, int r)
        {
            hfSiteR = r;
            DebugSettings.enableRandomMentalStates = false;
            List<Pawn> all = Colonists(map).Where(p => !p.Downed).OrderBy(p => p.thingIDNumber).ToList();
            hfCrewIds.Clear();
            hfCrewIds.AddRange(all.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)).Take(2).Select(p => p.thingIDNumber));
            if (hfCrewIds.Count == 0)
            {
                hfCrewIds.AddRange(all.Take(2).Select(p => p.thingIDNumber));
            }
        }

        private static void HfEnd(Map map, string check, string next)
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
            pendingLoad = "pz_holefog";
        }

        // Designator check + designate (the player's order).
        private static bool HfOrder(Designator_Cells d, IntVec3 c, out string why)
        {
            AcceptanceReport r = d.CanDesignateCell(c);
            why = r.Accepted ? "ok" : r.Reason;
            if (r.Accepted)
            {
                d.DesignateSingleCell(c);
            }
            return r.Accepted;
        }

        private static bool StepHoleFogFix(Map map)
        {
            switch (stage)
            {
                // ------------------------------------------------------------ F1: mine below twice, a two-floor drop into a cave
                case "hf_f1":
                {
                    IntVec3 g = hfG1;
                    HfStart(map, g, 8);
                    hfCave.Clear();
                    hfCave.Add(g + new IntVec3(1, 0, 0));
                    for (int dx = 2; dx <= 4; dx++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            hfCave.Add(g + new IntVec3(dx, 0, dz));
                        }
                    }
                    HfCarve(map, hfCave, false);
                    List<Pawn> crew = HfCrew(map);
                    for (int i = 0; i < crew.Count; i++)
                    {
                        Teleport(crew[i], HL(g + new IntVec3(-1 + 2 * i, 0, -2), 2, map));
                    }
                    ReachFog.Run(map);
                    MdWork(map, crew.Take(1).ToList(), WorkTypeDefOf.Mining);
                    HfWatch(map, g);
                    bool ok = HfOrder(new Designator_MineBelow(), HL(g, 2, map), out string why);
                    Info($"F1 site {g}: top {HL(g, 2, map)}, cave {hfCave.Count} cells fogged {HfFogged(hfCave, map)}; crew {string.Join(", ", crew.Select(p => $"{p} {p.Position}"))}; Mine below on the top: {why}");
                    if (!ok)
                    {
                        Result("F1 Mine below on a level-2 top", false, why);
                        HfEnd(map, "F1", "hf_f2");
                        return true;
                    }
                    MuRun(400, 47000, () => HL(g, 1, map).GetFirstMineable(map) == null && ShaftMarks.Has(HL(g, 1, map), map), "hf_f1_pit1");
                    return true;
                }

                case "hf_f1_pit1":
                {
                    IntVec3 g = hfG1, c1 = HL(g, 1, map), u2 = HL(g, 2, map);
                    bool open = c1.GetFirstMineable(map) == null && u2.GetTerrain(map) == PZDefOf.PZ_OpenAir && !map.fogGrid.IsFogged(c1);
                    Result("F1 a colonist on the plateau mined the level-1 rock from above (Mine below): the pit is open and seen", open,
                        $"pit {HfCell(c1, map)}, top {HfCell(u2, map)}");
                    if (!open)
                    {
                        HfEnd(map, "F1", "hf_f2");
                        return true;
                    }
                    // the pit floor marked Mine below: a colonist must be down in the pit and dig from there
                    bool ok = HfOrder(new Designator_MineBelow(), c1, out string why);
                    Info($"F1 Mine below on the pit floor {c1}: {why}");
                    if (!ok)
                    {
                        Result("F1 Mine below on the pit floor", false, why);
                        HfEnd(map, "F1", "hf_f2");
                        return true;
                    }
                    // (no voluntary jumps since 2026-10-19: one of the crew falls in the way a pawn gets into a pit now, off the
                    // open top: the gravity rule's fall, one floor, no harm)
                    Pawn faller = HfCrew(map).FirstOrDefault();
                    if (faller != null)
                    {
                        Teleport(faller, u2);
                        bool fell = Gravity.FallPawn(faller, u2, false);
                        Info($"F1 {faller} fell into the pit: {fell}, now at {faller.Position} L{Levels.LevelOf(faller.Position, map)}");
                    }
                    hfSeen = HfFogged(hfCave, map) == hfCave.Count;
                    MuRun(600, 47100, () => g.GetFirstMineable(map) == null && Shaft.Is(g, map), "hf_f1_pit2");
                    return true;
                }

                case "hf_f1_pit2":
                {
                    IntVec3 g = hfG1, c1 = HL(g, 1, map), u2 = HL(g, 2, map);
                    bool open = g.GetFirstMineable(map) == null && c1.GetTerrain(map) == PZDefOf.PZ_OpenAir && Shaft.Is(g, map);
                    IntVec3 land = Column.Landing(u2, map, false, true, out int fallen, out _);
                    Result("F1 the colonist who fell into the pit mined the ground rock below it: a shaft two floors deep (level-2 top to the ground)", open && land == g,
                        $"ground {HfCell(g, map)}, level 1 {HfCell(c1, map)}, top {HfCell(u2, map)}; a fall into the top lands at {land}; crew {string.Join(", ", HfCrew(map).Select(p => $"{p} {p.Position} L{Levels.LevelOf(p.Position, map)}"))}");
                    Result("F1 the fall column is revealed down to the landing (level 1 passed, the ground bottom)", !map.fogGrid.IsFogged(c1) && !map.fogGrid.IsFogged(g),
                        $"{HfCell(c1, map)}; {HfCell(g, map)}");
                    int fog = HfFogged(hfCave, map);
                    Result("F1 the hidden cave next to the bottom was fogged before and is revealed now (vanilla's mined-cell reveal at the landing)", hfSeen && fog == 0,
                        $"fogged before {hfSeen}, cave cells still fogged {fog}/{hfCave.Count}");
                    MuShot(map, "hf_f1_top_L2", 2, g, 9f, "hf_f1_shot2");
                    return true;
                }

                case "hf_f1_shot2":
                    MuShot(map, "hf_f1_landing_L0", 0, hfG1, 9f, "hf_f1_end");
                    return true;

                case "hf_f1_end":
                {
                    // the site: the 17x17 round g; the cave is inside
                    HfEnd(map, "F1", "hf_f2");
                    return true;
                }

                // ------------------------------------------------------------ F2: mine above into a level-1 cave
                case "hf_f2":
                {
                    IntVec3 g = hfG2;
                    HfStart(map, g, 6);
                    IntVec3 w = g + IntVec3.West;
                    HfCarve(map, new[] { g, w }, true);
                    hfCave.Clear();
                    for (int dx = 1; dx <= 3; dx++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            hfCave.Add(HL(g + new IntVec3(dx, 0, dz), 1, map));
                        }
                    }
                    HfCarve(map, hfCave, false);
                    hfSeen = HfFogged(hfCave, map) == hfCave.Count;
                    List<Pawn> crew = HfCrew(map).Take(1).ToList();
                    Teleport(crew[0], w);
                    foreach (Pawn o in Colonists(map).Where(o => !crew.Contains(o)))
                    {
                        Draft(o, true);
                    }
                    MdWork(map, crew, WorkTypeDefOf.Mining);
                    ReachFog.Run(map);
                    HfWatch(map, g);
                    bool ok = HfOrder(new Designator_MineAbove(), g, out string why);
                    Info($"F2 site {g}: chamber {g} {w}, level-1 cave {hfCave.Count} cells (fogged {HfFogged(hfCave, map)}); Mine above: {why}");
                    if (!ok)
                    {
                        Result("F2 Mine above from the chamber", false, why);
                        HfEnd(map, "F2", "hf_f3");
                        return true;
                    }
                    MuRun(400, 48000, () => HL(g, 1, map).GetFirstMineable(map) == null && Shaft.Is(g, map), "hf_f2_done");
                    return true;
                }

                case "hf_f2_done":
                {
                    IntVec3 g = hfG2, u = HL(g, 1, map);
                    int fog = HfFogged(hfCave, map);
                    Result("F2 mine above into a hidden level-1 cave: the shaft is open (covered), the cave it opened into is revealed", Shaft.Is(g, map) && hfSeen && fog == 0 && !map.fogGrid.IsFogged(u),
                        $"U {HfCell(u, map)}, cave fogged before {hfSeen}, still fogged {fog}/{hfCave.Count}");
                    MuShot(map, "hf_f2_cave_L1", 1, g, 8f, "hf_f2_end");
                    return true;
                }

                case "hf_f2_end":
                    HfEnd(map, "F2", "hf_f3");
                    return true;

                // ------------------------------------------------------------ F3: Remove roof under a bare top on a fogged plateau
                case "hf_f3":
                {
                    IntVec3 g = hfG3;
                    HfStart(map, g, 6);
                    IntVec3 w = g + IntVec3.West;
                    HfCarve(map, new[] { g, w }, true);
                    hfCave.Clear();
                    for (int dz = -3; dz <= 3; dz++)
                    {
                        for (int dx = -3; dx <= 3; dx++)
                        {
                            if (dx != 0 || dz != 0)
                            {
                                hfCave.Add(HL(g + new IntVec3(dx, 0, dz), 1, map));
                            }
                        }
                    }
                    List<Pawn> crew = HfCrew(map).Take(1).ToList();
                    Teleport(crew[0], w);
                    foreach (Pawn o in Colonists(map).Where(o => !crew.Contains(o)))
                    {
                        Draft(o, true);
                    }
                    MdWork(map, crew, WorkTypeDefOf.Construction);
                    ReachFog.Run(map);   // (the colonist in the chamber: its ceilings show before the count)
                    hfFogAround = HfFogged(hfCave, map);
                    HfWatch(map, g);
                    new Designator_AreaNoRoof().DesignateMultiCell(new[] { g });
                    Info($"F3 site {g}: chamber {g} {w}, plateau round the top: {hfFogAround}/{hfCave.Count} fogged; no-roof area on {g}: {map.areaManager.NoRoof[g]}");
                    MuRun(300, 49000, () => Shaft.Is(g, map), "hf_f3_done");
                    return true;
                }

                case "hf_f3_done":
                {
                    IntVec3 g = hfG3, u = HL(g, 1, map);
                    int fog = HfFogged(hfCave, map);
                    bool open = Shaft.Is(g, map) && u.GetTerrain(map) == PZDefOf.PZ_OpenAir;
                    Result("F3 Remove roof under a bare top from a ground chamber: a hole to the sky, the hole and the chamber seen", open && !map.fogGrid.IsFogged(u) && !map.fogGrid.IsFogged(g),
                        $"C {HfCell(g, map)}, U {HfCell(u, map)}");
                    Result("F3 the fogged plateau round the hole stays fogged (nobody can get up there)", open && fog == hfFogAround,
                        $"plateau cells fogged {hfFogAround} -> {fog} of {hfCave.Count}; seen: {string.Join(" ", hfCave.Where(c => !map.fogGrid.IsFogged(c)).Select(c => HfCell(c, map)))}");
                    float inner = HfFogStats(map, "after F3");
                    Result("F3 inner upper-level rock stays fogged", inner > 0.99f, $"{inner:P1}");
                    MuShot(map, "hf_f3_hole_L1", 1, g, 9f, "hf_f3_end");
                    return true;
                }

                case "hf_f3_end":
                    HfEnd(map, "F3", null);
                    return true;
            }
            return false;
        }
    }
}
