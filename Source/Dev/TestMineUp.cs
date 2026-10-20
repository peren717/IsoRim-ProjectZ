using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode mineup: tall mountains phase 3 (isorim-pz-phase3-mineup-plan.md, "Revision: vanilla commands", R5 P6). The same
    // fixed-seed map as rocktop (Mountainous, -projectztallmountains, -projectzfloors=4, -projectzmapsize=250). Setup: the
    // generation count of the vertical reveal (V4), then colonists dig two ground tunnels with vanilla Mine (A: 6 cells under
    // a plateau with rock on level 1 and a top on level 2 or higher; B: 4 cells under a level-1 plateau, bare tops above),
    // checked for the vertical reveal (M2), saved as pz_mineup. Every check reloads it, works with real designators, work
    // givers and jobs under fixed seeds (each frame's ticks under their own seed), and ends with the terrain, roofs,
    // edifices (and fog where nothing is revealed by design) outside its site unchanged:
    //   M1  designators: Remove roof under a bare top (ok), under rock ("Mine the rock above first"), under a built thing
    //       ("Something is built above"), fogged (no); vanilla Mine on rock above (ok); stairs under rock ("Ceiling is
    //       rock..."); the rule: rock over rock is never from below, rock over the tunnel is
    //   M3  a covered shaft: vanilla Mine on a rock and an ore above tunnel A; mined from below; yields at the miner; roofs,
    //       fog, room, column; saved and reloaded identical; M10 (cave-in) on the reloaded state: the game's collapse buffer
    //       on the shaft's thick roof drops the rocks at the bottom
    //   M4  a hole in a top: Remove roof under a bare top with a drafted colonist and steel on it; they fall; a stool next to it
    //       stays; the area keeps the hole open; M14 (c) the area cleared and a roof built over the hole: it stays
    //   M5  stairs in a covered shaft: refused under rock; 4 rocks mined from below; stairs built (landing laid); M14 (a) the
    //       no-roof area on the stairs and shafts: no job, roofs kept; a colonist walks up; M8 sight from the bottom to the
    //       landing; M7 a hole opened upward from the landing (stack propagation); the stairs deconstructed
    //   M6  stairs onto the plateau from tunnel B: three holes by Remove roof, stairs built, nothing laid, walk up
    //   M9  cancel mid-job (HP kept, nothing opened); two colonists side by side; saved mid-job, reloaded, finished
    //   M13 (a) a big Mine drag on level 1 over the tunnel reachable from stairs: dug on its own level, no shaft
    //       (-projectzminenogroup: holes); (b) a 3-cell mark under solid rock, stairs finished mid-job: the job ends, vanilla
    //       finishes the rock from level 1 with its hit points kept
    //   M12 costs: the vertical reveal in a whole-map ClearAllFog (on and off), Shaft.Open
    // Shots (IsoRim, for the P7 review): mu_*.png.
    public static partial class TestRunner
    {
        private static readonly List<IntVec3> muA = new List<IntVec3>();   // tunnel A cells t1..t7 (t7 the front wall)
        private static readonly List<IntVec3> muB = new List<IntVec3>();   // tunnel B cells b1..b5 (b5 the front wall)
        private static IntVec3 muSA, muSB, muDA, muDB, muSideA;
        private static Rot4 muRotA, muRotB;
        private static Func<bool> muCond;
        private static Action muEach;
        private static string muNext;
        private static int muFrame, muMax, muSeed;
        private static CellRect muSite;
        private static bool muSiteFog;
        private static int muHashBefore;
        private static readonly Dictionary<IntVec3, string> muBefore = new Dictionary<IntVec3, string>();
        private static readonly Dictionary<IntVec3, bool> muFogBefore = new Dictionary<IntVec3, bool>();
        private static int muCollapses, muMined, muHoles, muLanded;
        private static Pawn muP1, muP2, muP3;
        private static Thing muSteel, muStool, muRock;
        private static IntVec3 muAt, muAt2, muStoolAt;
        private static int muHp, muInt;
        private static bool muFlag, muFlag2;
        private static string muNote;
        private static readonly List<double> muOpenMs = new List<double>();
        private static string muShotName, muShotNext, muInterrupt;
        private static IntVec3 muWorkPos;
        private static IntVec3 muPointer = IntVec3.Invalid;
        private static bool muPointerPatched;

        private static IntVec3 MuUp(IntVec3 c, Map map) => FloorRoof.Above(c, map);

        private static bool MuRock(IntVec3 c, Map map) => c.InBounds(map) && c.GetEdifice(map) is Mineable m && m.def.building.isNaturalRock && !m.def.building.isResourceRock && m.def != ThingDefOf.CollapsedRocks;

        private static bool MuThick(IntVec3 c, Map map) => map.roofGrid.RoofAt(c)?.isThickRoof ?? false;

        private static string MuRoof(IntVec3 c, Map map) => map.roofGrid.RoofAt(c)?.defName ?? "none";

        // Terrain, roof, edifice (and fog) of every cell of every level whose ground cell is outside the site.
        private static int MuHash(Map map, CellRect site, bool fog, Dictionary<IntVec3, string> cells)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int h = 17;
            for (int L = 0; L <= a.MaxLevel; L++)
            {
                for (int z = 0; z < a.groundZ; z++)
                {
                    for (int x = 0; x < map.Size.x; x++)
                    {
                        IntVec3 g = new IntVec3(x, 0, z);
                        if (site.Contains(g))
                        {
                            continue;
                        }
                        IntVec3 c = Levels.Cell(g, L, map);
                        bool fogged = fog && map.fogGrid.IsFogged(c);
                        h = Gen.HashCombineInt(h, c.GetTerrain(map).shortHash);
                        h = Gen.HashCombineInt(h, map.roofGrid.RoofAt(c)?.shortHash ?? 0);
                        h = Gen.HashCombineInt(h, c.GetEdifice(map)?.def.shortHash ?? 0);
                        h = Gen.HashCombineInt(h, fogged ? 1 : 0);
                        if (cells != null)
                        {
                            cells[c] = $"{c.GetTerrain(map).defName} {map.roofGrid.RoofAt(c)?.defName ?? "-"} {c.GetEdifice(map)?.def.defName ?? "-"}{(fogged ? " fog" : "")}";
                        }
                    }
                }
            }
            return h;
        }

        private static void MuStart(Map map, CellRect site, bool fog)
        {
            muSite = site;
            muSiteFog = fog;
            muBefore.Clear();
            muHashBefore = MuHash(map, site, fog, muBefore);
            muCollapses = PZStats.slabCollapses;
            muMined = MineUp.MinedFromBelow;
            muHoles = Hole.Opened;
            muLanded = ShaftCaveIn.Landed;
            DebugSettings.enableRandomMentalStates = false;
            Find.PlaySettings.autoRebuild = false;
            Find.Storyteller.storytellerComps.Clear();
            Find.Storyteller.incidentQueue.Clear();
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        }

        private static void MuOutside(Map map, string check)
        {
            Dictionary<IntVec3, string> after = new Dictionary<IntVec3, string>();
            int now = MuHash(map, muSite, muSiteFog, after);
            List<string> diff = after.Where(kv => !muBefore.TryGetValue(kv.Key, out string was) || was != kv.Value).Take(6)
                .Select(kv => $"{kv.Key} L{ProjectZ.FloorOf(kv.Key, map)}: {(muBefore.TryGetValue(kv.Key, out string w) ? w : "?")} -> {kv.Value}").ToList();
            Result($"{check}: terrain, roofs, edifices{(muSiteFog ? " and fog" : "")} outside the site unchanged", now == muHashBefore, $"site {muSite}{(diff.Count == 0 ? "" : "; changed: " + string.Join("; ", diff))}");
        }

        private static void MuEnd(Map map, string check, string next)
        {
            MuOutside(map, check);
            if (Shaft.LastOpenMs > 0)
            {
                muOpenMs.Add(Shaft.LastOpenMs);
            }
            FloorView.Set(0);
            Find.DesignatorManager.Deselect();
            if (next == null)
            {
                Finish();
                return;
            }
            stage = next;
            pendingLoad = "pz_mineup";
        }

        // Real ticks: up to 300 a frame, each frame under its own fixed seed, until cond (checked every tick) or maxFrames.
        private static void MuRun(int maxFrames, int seed, Func<bool> cond, string next, Action each = null)
        {
            muCond = cond;
            muEach = each;
            muNext = next;
            muFrame = 0;
            muMax = maxFrames;
            muSeed = seed;
            stage = "mu_run";
        }

        private static void MuKeep(Map map)
        {
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.needs?.food != null) p.needs.food.CurLevel = p.needs.food.MaxLevel;
                if (p.needs?.rest != null) p.needs.rest.CurLevel = p.needs.rest.MaxLevel;
                if (p.needs?.joy != null) p.needs.joy.CurLevel = p.needs.joy.MaxLevel;
                if (p.InMentalState) p.MentalState.RecoverFromState();
            }
        }

        // The colonists: workers (undrafted, the work type only), the rest drafted where they are.
        private static List<Pawn> MuCrew(Map map, WorkTypeDef w, int count)
        {
            List<Pawn> all = Colonists(map).Where(p => !p.Downed).OrderBy(p => p.thingIDNumber).ToList();
            List<Pawn> crew = all.Where(p => w == null || !p.WorkTypeIsDisabled(w)).Take(count).ToList();
            foreach (Pawn p in all)
            {
                Draft(p, !crew.Contains(p));
            }
            FgWork(map, crew, w);
            return crew;
        }

        private static void MuShot(Map map, string name, int floor, IntVec3 g, float size, string next, bool mineTool = false)
        {
            ShotAt(map, floor, ProjectZ.GroundCellOf(g, map), size);
            MuPointer(Levels.Cell(ProjectZ.GroundCellOf(g, map), floor, map));
            Find.UIRoot.screenshotMode.Active = true;
            if (mineTool)
            {
                Find.DesignatorManager.Select(new Designator_Mine());
            }
            else
            {
                Find.DesignatorManager.Deselect();
            }
            muShotName = name;
            muShotNext = next;
            Wait(40, "mu_shot");
        }

        // IsoRim's designator cell and cut-away pointer held on a cell (as mode xray), for the Mine tool shots.
        private static void MuPointer(IntVec3 c)
        {
            muPointer = c;
            if (muPointerPatched)
            {
                return;
            }
            System.Type inter = HarmonyLib.AccessTools.TypeByName("IsoRim.Interaction");
            System.Type cut = HarmonyLib.AccessTools.TypeByName("IsoRim.Cutaway");
            if (inter == null || cut == null)
            {
                return;
            }
            muPointerPatched = true;
            // (the cut-away under the pointer on, as minebody: tunnels inside the mountain are seen)
            object st = HarmonyLib.AccessTools.Property(HarmonyLib.AccessTools.TypeByName("IsoRim.IsoRimMod"), "Settings")?.GetValue(null)
                ?? HarmonyLib.AccessTools.Field(HarmonyLib.AccessTools.TypeByName("IsoRim.IsoRimMod"), "Settings")?.GetValue(null);
            if (st != null)
            {
                HarmonyLib.Traverse.Create(st).Field("cutHover").SetValue(true);
            }
            HarmonyLib.Harmony h = new HarmonyLib.Harmony("Peren.IsoRimProjectZ.test.mineup");
            h.Patch(HarmonyLib.AccessTools.Method(inter, "DesignatorMouseCell"), prefix: new HarmonyLib.HarmonyMethod(typeof(TestRunner), nameof(MuCellPrefix)));
            h.Patch(HarmonyLib.AccessTools.Method(cut, "Pointer"), prefix: new HarmonyLib.HarmonyMethod(typeof(TestRunner), nameof(MuPointerPrefix)));
        }

        private static bool MuCellPrefix(ref IntVec3 __result)
        {
            if (!muPointer.IsValid)
            {
                return true;
            }
            __result = muPointer;
            return false;
        }

        private static bool MuPointerPrefix(ref Vector3 ground, ref bool __result)
        {
            if (!muPointer.IsValid)
            {
                return true;
            }
            ground = muPointer.ToVector3Shifted();
            __result = true;
            return false;
        }

        private static CellRect MuRect(IEnumerable<IntVec3> cells, Map map, int expand)
        {
            List<IntVec3> g = cells.Select(c => ProjectZ.GroundCellOf(c, map)).ToList();
            return CellRect.FromLimits(g.Min(c => c.x), g.Min(c => c.z), g.Max(c => c.x), g.Max(c => c.z)).ExpandedBy(expand);
        }

        // (plants are allowed: they come and go with the frames before the test starts; MuClear takes them off)
        private static bool MuOpen(IntVec3 g, Map map) => g.InBounds(map) && Mountains.WalkableRaw(g, map) && !g.Roofed(map) && !map.fogGrid.IsFogged(g) && g.GetEdifice(map) == null;

        private static void MuClear(Map map, IEnumerable<IntVec3> cells)
        {
            foreach (IntVec3 c in cells)
            {
                foreach (Thing t in c.GetThingList(map).Where(t => t.def.category == ThingCategory.Plant).ToList())
                {
                    t.Destroy();
                }
            }
        }

        // Tunnel A: open ground s, then 6 rock cells (thick roof, rock on level 1 above, natural top at 2 or more; t3's top at
        // exactly 2), a rock front wall t7 and rock side walls; three open cells behind s for stairs onto level 1 (M13).
        private static bool MuSiteA(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            Pawn any = Colonists(map).FirstOrDefault();
            foreach (IntVec3 s in Near(new IntVec3(map.Size.x / 2, 0, a.groundZ / 2), 110, map))   // (a fixed point: the same sites every run)
            {
                if (s.x < 6 || s.z < 6 || s.x >= map.Size.x - 6 || s.z >= a.groundZ - 6 || !MuOpen(s, map) || any == null
                    || !map.reachability.CanReachMapEdge(s, TraverseParms.For(TraverseMode.PassDoors)))   // (static: not where the colonists happen to be)
                {
                    continue;
                }
                foreach (IntVec3 d in GenAdj.CardinalDirections)
                {
                    IntVec3 side = d.RotatedBy(RotationDirection.Clockwise);
                    bool ok = true;
                    for (int i = 1; i <= 7 && ok; i++)
                    {
                        IntVec3 t = s + d * i;
                        ok = MuRock(t, map) && MuThick(t, map) && (i == 7 || Column.NaturalTop(t, map) >= 2 && MuRock(t + side, map) && MuRock(t - side, map) && MuRock(MuUp(t, map), map));
                    }
                    ok = ok && Column.NaturalTop(s + d * 3, map) == 2 && !map.fogGrid.IsFogged(s + d);
                    for (int i = 1; i <= 4 && ok; i++)
                    {
                        IntVec3 b = s - d * i;
                        IntVec3 up = MuUp(b, map);
                        ok = MuOpen(b, map) && up.IsValid && up.GetTerrain(map) == PZDefOf.PZ_OpenAir && up.GetEdifice(map) == null;
                    }
                    IntVec3 e0 = MuUp(s, map);
                    ok = ok && e0.IsValid && e0.GetTerrain(map) == PZDefOf.PZ_OpenAir;
                    if (!ok)
                    {
                        continue;
                    }
                    muSA = s;
                    muDA = d;
                    muSideA = side;
                    muRotA = Rot4.FromIntVec3(d);
                    muA.Clear();
                    for (int i = 1; i <= 7; i++)
                    {
                        muA.Add(s + d * i);
                    }
                    return true;
                }
            }
            return false;
        }

        // Tunnel B: open ground s, then 4 rock cells (thick roof, a bare level-1 top above), a rock front wall and side walls,
        // at least 20 cells from tunnel A.
        private static bool MuSiteB(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            Pawn any = Colonists(map).FirstOrDefault();
            foreach (IntVec3 s in Near(new IntVec3(map.Size.x / 2, 0, a.groundZ / 2), 110, map))   // (a fixed point: the same sites every run)
            {
                if (s.x < 6 || s.z < 6 || s.x >= map.Size.x - 6 || s.z >= a.groundZ - 6 || !MuOpen(s, map) || any == null || muSA.IsValid && s.InHorDistOf(muSA, 20f)
                    || !map.reachability.CanReachMapEdge(s, TraverseParms.For(TraverseMode.PassDoors)))   // (static: not where the colonists happen to be)
                {
                    continue;
                }
                foreach (IntVec3 d in GenAdj.CardinalDirections)
                {
                    IntVec3 side = d.RotatedBy(RotationDirection.Clockwise);
                    bool ok = !map.fogGrid.IsFogged(s + d);
                    for (int i = 1; i <= 5 && ok; i++)
                    {
                        IntVec3 t = s + d * i;
                        ok = MuRock(t, map) && MuThick(t, map) && (i == 5 || Column.NaturalTop(t, map) == 1 && TmTop(MuUp(t, map), map) && MuRock(t + side, map) && MuRock(t - side, map));
                    }
                    if (!ok)
                    {
                        continue;
                    }
                    muSB = s;
                    muDB = d;
                    muRotB = Rot4.FromIntVec3(d);
                    muB.Clear();
                    for (int i = 1; i <= 5; i++)
                    {
                        muB.Add(s + d * i);
                    }
                    return true;
                }
            }
            return false;
        }

        private static void MuWood(Map map, IntVec3 near)
        {
            for (int i = 0; i < 4; i++)
            {
                Thing wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                wood.stackCount = 75;
                GenPlace.TryPlaceThing(wood, near, map, ThingPlaceMode.Near);
            }
        }

        private static Building_Stairs MuStairs(Map map, IntVec3 center) => map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).OfType<Building_Stairs>().FirstOrDefault(t => t.Position == center);

        private static bool StepMineUp(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            switch (stage)
            {
                case "mu_run":
                {
                    bool done = muCond();
                    Rand.PushState(muSeed + muFrame);
                    try
                    {
                        for (int t = 0; t < 300 && !done && muInterrupt == null; t++)
                        {
                            if (t % 30 == 0)
                            {
                                MuKeep(map);
                            }
                            Find.TickManager.DoSingleTick();
                            muEach?.Invoke();
                            done = muCond();
                        }
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                    if (muInterrupt != null)
                    {
                        stage = muInterrupt;
                        muInterrupt = null;
                        return true;
                    }
                    if (!done && ++muFrame < muMax)
                    {
                        return true;
                    }
                    Info($"  ticked {muFrame} frame(s), condition {(done ? "met" : "NOT met")}");
                    muEach = null;
                    stage = muNext;
                    return true;
                }

                case "mu_shot":
                    Capture(muShotName);
                    Wait(5, muShotNext);
                    return true;

                // ------------------------------------------------------------ setup
                case "mu_setup":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    if (a == null || !Mountains.On(map))
                    {
                        Result("mineup: a tall-mountain map", false, "no annex or no tall mountains (-projectztallmountains)");
                        Finish();
                        return true;
                    }
                    Info($"mineup: map {map.Size}, tile {map.Tile} {map.TileInfo?.hilliness}, floors {a.floors}; rock {Mountains.LastRock}, tops {Mountains.LastTops}; vertical fog {(VerticalFog.Off ? "OFF (test)" : "on")}");
                    // V4: the generation count against a recount (revealed walkable ground cells whose level-1 cell the rim rule
                    // left fogged), and those level-1 cells unfogged now
                    int sx = map.Size.x, gz = a.groundZ, recount = 0, open = 0;
                    bool[] fog1 = Mountains.BandFog(Mountains.LastHeights, 1, sx, gz, out _, out _);
                    for (int i = 0; i < sx * gz; i++)
                    {
                        IntVec3 g = new IntVec3(i % sx, 0, i / sx);
                        if (fog1[i] && !map.fogGrid.IsFogged(g) && Mountains.WalkableRaw(g, map))
                        {
                            recount++;
                            if (!map.fogGrid.IsFogged(Levels.Cell(g, 1, map)))
                            {
                                open++;
                            }
                        }
                    }
                    Result("V4 generation: the cell above every revealed walkable ground cell is revealed (count = recount, all unfogged)",
                        VerticalFog.LastAboveRevealed == recount && open == recount, $"generation {VerticalFog.LastAboveRevealed}, recount {recount}, unfogged now {open}");
                    bool siteA = MuSiteA(map), siteB = MuSiteB(map);
                    Result("mineup sites: tunnel A (rock above, top at 2+) and tunnel B (bare level-1 tops)", siteA && siteB,
                        $"A {(siteA ? $"{muSA} dir {muDA}, tops {string.Join(",", muA.Take(6).Select(t => Column.NaturalTop(t, map)))}" : "none")}; B {(siteB ? $"{muSB} dir {muDB}" : "none")}");
                    if (!siteA || !siteB)
                    {
                        Finish();
                        return true;
                    }
                    MuClear(map, Enumerable.Range(0, 5).Select(i => muSA - muDA * i).Concat(new[] { muSB }));
                    // M2: the fog of every upper cell over tunnel A before digging
                    muFogBefore.Clear();
                    foreach (IntVec3 g in MuRect(muA, map, 6))
                    {
                        if (!g.InBounds(map))
                        {
                            continue;
                        }
                        for (int L = 1; L <= a.MaxLevel; L++)
                        {
                            IntVec3 c = Levels.Cell(g, L, map);
                            if (c.IsValid)
                            {
                                muFogBefore[c] = map.fogGrid.IsFogged(c);
                            }
                        }
                    }
                    MuStart(map, CellRect.Empty, false);
                    List<IntVec3> dig = muA.Take(6).Concat(muB.Take(4)).ToList();
                    foreach (IntVec3 c in dig)
                    {
                        map.designationManager.AddDesignation(new Designation(c, DesignationDefOf.Mine));
                    }
                    MuCrew(map, WorkTypeDefOf.Mining, 3);
                    Info($"setup: tunnels A {string.Join(" ", muA.Take(6))}, B {string.Join(" ", muB.Take(4))}");
                    MuRun(400, 31000, () => dig.All(c => c.GetEdifice(map) == null), "mu_dug");
                    return true;
                }

                case "mu_dug":
                {
                    List<IntVec3> dig = muA.Take(6).Concat(muB.Take(4)).ToList();
                    Result("setup: colonists dug both tunnels with vanilla Mine", dig.All(c => c.GetEdifice(map) == null), $"{dig.Count(c => c.GetEdifice(map) == null)}/{dig.Count} cells, mined from below {MineUp.MinedFromBelow}");
                    // M2
                    HashSet<IntVec3> expect = new HashSet<IntVec3>(muA.Take(6).Select(t => MuUp(t, map)).Where(u => muFogBefore.TryGetValue(u, out bool f) && f));
                    HashSet<IntVec3> opened = new HashSet<IntVec3>(muFogBefore.Where(kv => kv.Value && !map.fogGrid.IsFogged(kv.Key)).Select(kv => kv.Key));
                    bool allAbove = muA.Take(6).All(t => !map.fogGrid.IsFogged(MuUp(t, map)));
                    Result("M2 vertical reveal: every cell straight above tunnel A is unfogged, nothing else above it opened",
                        allAbove && opened.SetEquals(expect),
                        $"above unfogged {muA.Take(6).Count(t => !map.fogGrid.IsFogged(MuUp(t, map)))}/6, opened {opened.Count} (expected {expect.Count}){(opened.Except(expect).Any() ? ", extra " + string.Join(" ", opened.Except(expect).Take(5)) : "")}, reveals {VerticalFog.Revealed}");
                    Result("M2 tunnel B: the bare tops above it are unfogged", muB.Take(4).All(t => !map.fogGrid.IsFogged(MuUp(t, map))), $"{muB.Take(4).Count(t => !map.fogGrid.IsFogged(MuUp(t, map)))}/4");
                    foreach (Pawn p in Colonists(map))
                    {
                        Draft(p, false);
                    }
                    FgWork(map, new Pawn[0], WorkTypeDefOf.Mining);
                    MuShot(map, "mu_vfog", 1, muA[3], 12f, "mu_save");
                    return true;
                }

                case "mu_save":
                    Find.UIRoot.screenshotMode.Active = false;
                    FloorView.Set(0);
                    SetHour(map, 10);   // (daylight for the shots)
                    GameDataSaveLoader.SaveGame("pz_mineup");
                    stage = "mu_m1";
                    pendingLoad = "pz_mineup";
                    return true;

                // ------------------------------------------------------------ M1
                case "mu_m1":
                {
                    MuStart(map, MuRect(muA.Concat(muB), map, 3), true);
                    Designator_AreaNoRoof nr = new Designator_AreaNoRoof();
                    AcceptanceReport bare = nr.CanDesignateCell(muB[1]);
                    AcceptanceReport rock = nr.CanDesignateCell(muA[1]);
                    IntVec3 built = MuUp(muB[2], map);
                    GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, built, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
                    AcceptanceReport underBuilt = nr.CanDesignateCell(muB[2]);
                    IntVec3 fogged = Near(muA[3], 30, map).FirstOrDefault(g => map.fogGrid.IsFogged(g) && MuThick(g, map));
                    AcceptanceReport fog = fogged.IsValid ? nr.CanDesignateCell(fogged) : AcceptanceReport.WasRejected;
                    Result("M1 Remove roof under a bare natural top: accepted", bare.Accepted, $"{muB[1]}: '{(bare.Accepted ? "ok" : bare.Reason)}'");
                    Result("M1 Remove roof under rock: 'Mine the rock above first'", !rock.Accepted && rock.Reason == "PZ_MineAboveFirst".Translate(), $"{muA[1]}: '{rock.Reason}'");
                    Result("M1 Remove roof under a top with a blueprint on it: 'Something is built above'", !underBuilt.Accepted && underBuilt.Reason == "PZ_BuiltAbove".Translate(), $"{muB[2]}: '{underBuilt.Reason}'");
                    Result("M1 Remove roof in fog: refused (vanilla)", !fog.Accepted, $"{fogged}: '{fog.Reason}'");
                    Designator_Mine dm = new Designator_Mine();
                    IntVec3 u2 = MuUp(muA[1], map);
                    AcceptanceReport mine = dm.CanDesignateCell(u2);
                    dm.DesignateMultiCell(new[] { u2 });
                    Result("M1 vanilla Mine on the rock above tunnel A (level 1): accepted, marked", mine.Accepted && MineSide.Marked(u2, map), $"{u2}: '{(mine.Accepted ? "ok" : mine.Reason)}'");
                    AcceptanceReport stairs = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, muA[1], muRotA, map, stuffDef: ThingDefOf.WoodLog);
                    Result("M1 stairs under rock: 'Ceiling is rock: mine it, or remove roof'", !stairs.Accepted && stairs.Reason == "PZ_CeilingIsRock".Translate(), $"'{stairs.Reason}'");
                    // the rule: rock over the tunnel is mined from below; rock over a rock wall never is
                    IntVec3 wall = muA[1] + muSideA;
                    IntVec3 uw = MuUp(wall, map);
                    if (MuRock(uw, map))
                    {
                        dm.DesignateMultiCell(new[] { uw });
                    }
                    Pawn miner = Colonists(map).Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining)).OrderBy(p => p.thingIDNumber).FirstOrDefault();
                    WorkGiver_Scanner giver = DefDatabase<WorkGiverDef>.GetNamed("PZ_MineFromBelow").Worker as WorkGiver_Scanner;
                    bool overTunnel = MineSide.FromBelow(u2, map) && giver.HasJobOnCell(miner, muA[1]);
                    bool overWall = !MineSide.FromBelow(uw, map) && !giver.HasJobOnCell(miner, wall);
                    Result("M1 the rule: the rock over the tunnel is mined from below, the rock over a rock wall is not", overTunnel && overWall && MuRock(uw, map),
                        $"over tunnel {overTunnel}, over wall {uw} ({uw.GetEdifice(map)?.def.defName ?? "no rock"}) {overWall}");
                    // shot: the Mine tool on level 1 over the tunnel, the x-ray and the marks
                    dm.DesignateMultiCell(muA.Skip(2).Take(2).Select(t => MuUp(t, map)));
                    MuPointer(MuUp(muA[3], map));
                    MuShot(map, "mu_mark_xray", 1, muA[2], 6f, "mu_m1_end", mineTool: true);
                    return true;
                }

                case "mu_m1_end":
                    muPointer = IntVec3.Invalid;
                    Find.UIRoot.screenshotMode.Active = false;
                    MuEnd(map, "M1", "mu_m3");
                    return true;

                // ------------------------------------------------------------ M3 (+ M10)
                case "mu_m3":
                {
                    MuStart(map, MuRect(muA, map, 3), true);
                    // U1 over t3 (rock), U2 over t5 (made steel ore)
                    IntVec3 u1 = MuUp(muA[2], map), u2 = MuUp(muA[4], map);
                    GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.MineableSteel), u2, map, WipeMode.Vanish);
                    muFogBefore.Clear();
                    foreach (IntVec3 u in new[] { u1, u2 })
                    {
                        for (int i = 0; i < 9; i++)
                        {
                            IntVec3 n = u + GenAdj.AdjacentCellsAndInside[i];
                            muFogBefore[n] = map.fogGrid.IsFogged(n);
                        }
                    }
                    new Designator_Mine().DesignateMultiCell(new[] { u1, u2 });
                    List<Pawn> crew = MuCrew(map, WorkTypeDefOf.Mining, 1);
                    muP1 = crew.FirstOrDefault();
                    muAt = IntVec3.Invalid;
                    muFlag = false;
                    Info($"M3: rock {u1} over {muA[2]}, steel ore {u2} over {muA[4]}; miner {muP1}");
                    MuShot(map, "mu_m3_marked", 1, muA[3], 6f, "mu_m3_go");
                    return true;
                }

                case "mu_m3_go":
                {
                    Find.UIRoot.screenshotMode.Active = false;
                    muPointer = IntVec3.Invalid;
                    FloorView.Set(0);
                    IntVec3 u1 = MuUp(muA[2], map), u2 = MuUp(muA[4], map);
                    MuRun(200, 33000, () => !MuRockOrOre(u1, map) && !MuRockOrOre(u2, map), "mu_m3_done", () =>
                    {
                        if (!muAt.IsValid && !MuRockOrOre(u2, map))
                        {
                            muAt = muP1.Position;   // where the miner stood when the ore went
                        }
                        if (!muFlag && muP1.CurJobDef == PZMineDefOf.PZ_MineFromBelow && muP1.jobs.curDriver.CurToilIndex >= 1 && !muP1.pather.Moving)
                        {
                            muFlag = true;
                            muAt2 = muP1.CurJob.targetB.Cell;
                            muWorkPos = muP1.Position;
                        }
                    });
                    return true;
                }

                case "mu_m3_done":
                {
                    IntVec3 u1 = MuUp(muA[2], map), u2 = MuUp(muA[4], map), c1 = muA[2], c2 = muA[4];
                    bool gone = !MuRockOrOre(u1, map) && !MuRockOrOre(u2, map);
                    bool air = u1.GetTerrain(map) == PZDefOf.PZ_OpenAir && u2.GetTerrain(map) == PZDefOf.PZ_OpenAir;
                    bool roofs = MuThick(u1, map) && MuThick(u2, map) && map.roofGrid.RoofAt(c1) == PZDefOf.PZ_StairwellRoof && map.roofGrid.RoofAt(c2) == PZDefOf.PZ_StairwellRoof;
                    Result("M3 colonists mined the rock and the ore above the tunnel from below: both gone, open air, their roofs thick, the cells below get the stairwell roof",
                        gone && air && roofs && MineUp.MinedFromBelow - muMined == 2,
                        $"gone {gone}, open air {air}; roofs U1 {MuRoof(u1, map)} U2 {MuRoof(u2, map)} C1 {MuRoof(c1, map)} C2 {MuRoof(c2, map)}; mined from below {MineUp.MinedFromBelow - muMined}");
                    List<Thing> steel = map.listerThings.ThingsOfDef(ThingDefOf.Steel).Where(t => t.Spawned && ProjectZ.FloorOf(t.Position, map) == 0 && muAt.IsValid && t.Position.InHorDistOf(muAt, 1.5f)).ToList();
                    Result("M3 the steel lands at the miner's cell (its floor, within 1 cell)", steel.Count > 0 && steel.Sum(t => t.stackCount) > 0,
                        $"miner stood at {muAt}; steel {string.Join(", ", map.listerThings.ThingsOfDef(ThingDefOf.Steel).Select(t => $"{t.stackCount} at {t.Position} L{ProjectZ.FloorOf(t.Position, map)}"))}; yields moved {MineUp.YieldsMoved}");
                    // fog: the rock faces round U unfogged, open cells beyond untouched
                    int faces = 0, facesOpen = 0, openChanged = 0;
                    foreach (KeyValuePair<IntVec3, bool> kv in muFogBefore)
                    {
                        Building e = kv.Key.GetEdifice(map);
                        if (e != null && e.def.MakeFog)
                        {
                            faces++;
                            facesOpen += map.fogGrid.IsFogged(kv.Key) ? 0 : 1;
                        }
                        else if (kv.Key != u1 && kv.Key != u2 && kv.Value != map.fogGrid.IsFogged(kv.Key))
                        {
                            openChanged++;
                        }
                    }
                    Result("M3 the shaft walls (rock round U) are revealed, no open cell's fog changed", faces == facesOpen && openChanged == 0, $"rock faces {facesOpen}/{faces} unfogged, open cells changed {openChanged}");
                    Room room = c1.GetRoom(map);
                    IntVec3 g1 = ProjectZ.GroundCellOf(c1, map);
                    Result("M3 the shaft bottom stays roofed (the stairwell roof: indoors, no sky); the column is open above it; the plane between the levels is gone",
                        c1.Roofed(map) && Column.OpenAbove(c1, map) && !Column.PlaneSolid(g1, 1, map) && Shaft.Is(c1, map),
                        $"room open roofs {room?.OpenRoofCount}, outdoors {room?.PsychologicallyOutdoors}, OpenAbove {Column.OpenAbove(c1, map)}, PlaneSolid {Column.PlaneSolid(g1, 1, map)}, Shaft.Is {Shaft.Is(c1, map)}, glow {map.glowGrid.GroundGlowAt(c1):0.00}");
                    Result("M3 the miner worked from the cell below or next to it (B = the tunnel cell)", muFlag && (muAt2 == c1 || muAt2 == c2) && muWorkPos.InHorDistOf(muAt2, 1.5f) && ProjectZ.FloorOf(muWorkPos, map) == 0, $"working at B {muAt2} from {muWorkPos}");
                    MuOutside(map, "M3");
                    muNote = MuState(map, new[] { u1, u2, c1, c2 });
                    MuShot(map, "mu_shaft_covered", 1, muA[3], 6f, "mu_m3_shot2", mineTool: true);
                    return true;
                }

                case "mu_m3_shot2":
                {
                    MuShot(map, "mu_shaft_covered_notool", 1, muA[3], 6f, "mu_m3_save");
                    return true;
                }

                case "mu_m3_save":
                    Find.UIRoot.screenshotMode.Active = false;
                    Find.DesignatorManager.Deselect();
                    FloorView.Set(0);
                    GameDataSaveLoader.SaveGame("pz_mineup_m3");
                    stage = "mu_m3_loaded";
                    pendingLoad = "pz_mineup_m3";
                    return true;

                case "mu_m3_loaded":
                {
                    IntVec3 u1 = MuUp(muA[2], map), u2 = MuUp(muA[4], map), c1 = muA[2], c2 = muA[4];
                    string now = MuState(map, new[] { u1, u2, c1, c2 });
                    Result("M3 save + reload: shaft marks, terrain, roofs and fog identical", now == muNote, now == muNote ? now : $"{muNote} -> {now}");
                    // M10: the game's collapse buffer on the shaft's thick roof (as an unsupported mined area would mark it)
                    Thing wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                    wood.stackCount = 20;
                    GenSpawn.Spawn(wood, c1, map);
                    muSteel = wood;
                    foreach (Pawn p in Colonists(map).Where(p => ProjectZ.GroundCellOf(p.Position, map).InHorDistOf(c1, 1.5f)).ToList())
                    {
                        Teleport(p, muSA - muDA * 2);
                    }
                    MuStart(map, MuRect(muA, map, 3), true);
                    map.roofCollapseBuffer.MarkToCollapse(u1);
                    MuRun(2, 33500, () => !map.roofCollapseBuffer.IsMarkedToCollapse(u1), "mu_m10");
                    return true;
                }

                case "mu_m10":
                {
                    IntVec3 u1 = MuUp(muA[2], map), c1 = muA[2];
                    bool rocksBottom = c1.GetEdifice(map)?.def == ThingDefOf.CollapsedRocks;
                    bool noneUp = u1.GetEdifice(map) == null && u1.GetTerrain(map) == PZDefOf.PZ_OpenAir;
                    Result("M10 a cave-in over the shaft: the rocks land at its bottom (crushing what is there), none in the air, the thick roof stays",
                        rocksBottom && noneUp && MuThick(u1, map) && muSteel.Destroyed && ShaftCaveIn.Landed - muLanded == 1,
                        $"bottom {c1} {c1.GetEdifice(map)?.def.defName ?? "nothing"}, up {u1} {u1.GetEdifice(map)?.def.defName ?? "nothing"} {u1.GetTerrain(map).defName} roof {MuRoof(u1, map)}, wood crushed {muSteel.Destroyed}, landed {ShaftCaveIn.Landed - muLanded}");
                    MuShot(map, "mu_cavein", 0, c1, 6f, "mu_m10_end");
                    return true;
                }

                case "mu_m10_end":
                    Find.UIRoot.screenshotMode.Active = false;
                    MuEnd(map, "M3/M10", "mu_m4");
                    return true;

                // ------------------------------------------------------------ M4 (+ M14 c)
                case "mu_m4":
                {
                    MuStart(map, MuRect(muB, map, 4), false);   // (the colonist put on the top reveals the plateau: arrival rule)
                    IntVec3 c = muB[1], u = MuUp(c, map);
                    List<Pawn> crew = MuCrew(map, WorkTypeDefOf.Construction, 1);
                    muP2 = crew.FirstOrDefault();
                    muP1 = Colonists(map).Where(p => !crew.Contains(p) && !p.Downed).OrderBy(p => p.thingIDNumber).FirstOrDefault();
                    Teleport(muP1, u);
                    Draft(muP1, true);
                    muInt = muP1.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);
                    muSteel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    muSteel.stackCount = 30;
                    GenSpawn.Spawn(muSteel, u, map);
                    muStoolAt = MuUp(muB[2], map);
                    muStool = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Stool"), ThingDefOf.WoodLog);
                    muStool.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(muStool, muStoolAt, map);
                    muInt += 1000 * Gravity.PawnsBruised;
                    new Designator_AreaNoRoof().DesignateMultiCell(new[] { c });
                    muFlag = false;
                    Info($"M4: hole over {c} (top {u}), colonist {muP1} and steel on it, stool at {muStoolAt}; worker {muP2}");
                    MuRun(120, 34000, () => u.GetTerrain(map) == PZDefOf.PZ_OpenAir, "mu_m4_done", () =>
                    {
                        if (!muFlag && muP2.CurJobDef == JobDefOf.RemoveRoof && muP2.jobs.curDriver.CurToilIndex >= 1)
                        {
                            muFlag = true;
                            muInterrupt = "mu_m4_shot";
                        }
                    });
                    return true;
                }

                case "mu_m4_shot":
                    // (the colonist at work under the top, mid-job)
                    MuShot(map, "mu_removeroof_work", 0, muB[1], 6f, "mu_m4_resume");
                    return true;

                case "mu_m4_resume":
                {
                    Find.UIRoot.screenshotMode.Active = false;
                    IntVec3 u = MuUp(muB[1], map);
                    MuRun(120, 34100, () => u.GetTerrain(map) == PZDefOf.PZ_OpenAir, "mu_m4_done");
                    return true;
                }

                case "mu_m4_done":
                {
                    IntVec3 c = muB[1], u = MuUp(c, map);
                    bool open = u.GetTerrain(map) == PZDefOf.PZ_OpenAir && map.roofGrid.RoofAt(c) == null && Column.OpenAbove(c, map);
                    Room room = c.GetRoom(map);
                    Result("M4 Remove roof under a bare top: the top opens to the sky (open air, no roof below, outdoors share)", open && Hole.Opened - muHoles == 1 && room != null && room.OpenRoofCount > 0,
                        $"U {u.GetTerrain(map).defName}, roof below {MuRoof(c, map)}, U roof {MuRoof(u, map)}, room open roofs {room?.OpenRoofCount}, holes {Hole.Opened - muHoles}, shaft {Shaft.Is(c, map)}");
                    int hurt = muP1.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);
                    bool fell = muP1.Spawned && ProjectZ.FloorOf(muP1.Position, map) == 0 && muP1.Position.InHorDistOf(c, 1.5f);
                    bool steelFell = muSteel.Spawned && ProjectZ.FloorOf(muSteel.Position, map) == 0 && muSteel.Position.InHorDistOf(c, 2.5f);
                    Result("M4 the colonist and the steel on the top fell to the tunnel floor; the colonist unhurt (one floor)", fell && steelFell && hurt == muInt % 1000 && Gravity.PawnsBruised == muInt / 1000,
                        $"colonist at {muP1.Position} L{ProjectZ.FloorOf(muP1.Position, map)}, injuries {muInt % 1000} -> {hurt}, bruised count {muInt / 1000} -> {Gravity.PawnsBruised}; steel at {muSteel.Position} L{ProjectZ.FloorOf(muSteel.Position, map)}");
                    Result("M4 the stool on the next top stays; nothing collapsed", muStool.Spawned && muStool.Position == muStoolAt && PZStats.slabCollapses == muCollapses,
                        $"stool {muStool.Position} (was {muStoolAt}), floor collapses {PZStats.slabCollapses - muCollapses}");
                    MuShot(map, "mu_hole_sky", 1, c, 6f, "mu_m4_keep");
                    return true;
                }

                case "mu_m4_keep":
                {
                    Find.UIRoot.screenshotMode.Active = false;
                    IntVec3 c = muB[1];
                    MuCrew(map, WorkTypeDefOf.Construction, 2);
                    MuRun(17, 34200, () => false, "mu_m4_kept");
                    return true;
                }

                case "mu_m4_kept":
                {
                    IntVec3 c = muB[1];
                    Result("M4 the no-roof area keeps the hole open (no roof built back over 2 in-game hours)", map.roofGrid.RoofAt(c) == null && map.areaManager.NoRoof[c], $"roof {MuRoof(c, map)}, no-roof area {map.areaManager.NoRoof[c]}");
                    // M14 (c): the area cleared, a roof built over the hole (as auto-roofing marks it): it stays
                    map.areaManager.NoRoof[c] = false;
                    map.areaManager.BuildRoof[c] = true;
                    MuRun(60, 34300, () => map.roofGrid.Roofed(c), "mu_m4_roofed");
                    return true;
                }

                case "mu_m4_roofed":
                {
                    IntVec3 c = muB[1];
                    muNote = MuRoof(c, map);
                    MuRun(17, 34400, () => false, "mu_m4_roofed2");
                    return true;
                }

                case "mu_m4_roofed2":
                {
                    IntVec3 c = muB[1];
                    Result("M14 (c) a roof built over the hole to the sky stays a built roof (closes it)", muNote == "RoofConstructed" && map.roofGrid.RoofAt(c) == RoofDefOf.RoofConstructed && !Column.OpenAbove(c, map),
                        $"built {muNote}, after 2 h {MuRoof(c, map)}, OpenAbove {Column.OpenAbove(c, map)}");
                    MuEnd(map, "M4", "mu_m5");
                    return true;
                }

                // ------------------------------------------------------------ M5 (+ M14 a, M8, M7)
                case "mu_m5":
                {
                    MuStart(map, MuRect(muA.Concat(new[] { muSA - muDA * 4 }), map, 3), false);
                    IntVec3 e = MuUp(muA[3], map);
                    muFlag2 = Combat.Los3D.CanSee(muA[0], e, map);
                    AcceptanceReport before = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, muA[1], muRotA, map, stuffDef: ThingDefOf.WoodLog);
                    Result("M5 stairs under the rock ceiling are refused: 'Ceiling is rock: mine it, or remove roof'", !before.Accepted && before.Reason == "PZ_CeilingIsRock".Translate(), $"'{before.Reason}'");
                    new Designator_Mine().DesignateMultiCell(muA.Take(4).Select(t => MuUp(t, map)));
                    List<Pawn> crew = MuCrew(map, WorkTypeDefOf.Mining, 2);
                    muP1 = crew.FirstOrDefault();
                    muFlag = false;
                    MuRun(400, 35000, () => muA.Take(4).All(t => !MuRockOrOre(MuUp(t, map), map)), "mu_m5_mined", () =>
                    {
                        Pawn w = crew.FirstOrDefault(p => p.CurJobDef == PZMineDefOf.PZ_MineFromBelow && p.jobs.curDriver.CurToilIndex >= 1 && !p.pather.Moving && p.CurJob.targetA.Thing is Thing r && r.HitPoints < r.MaxHitPoints);
                        if (!muFlag && w != null && muA.Take(4).Count(t => MuRockOrOre(MuUp(t, map), map)) <= 3)
                        {
                            muFlag = true;
                            muP1 = w;
                            muInterrupt = "mu_m5_shot";
                        }
                    });
                    return true;
                }

                case "mu_m5_shot":
                    // a colonist mining from below (pose, facing, dust) with one shaft open: ground view, then level 1 with the Mine tool
                    MuPose(muP1);
                    MuShot(map, "mu_minefrombelow", 0, muP1.Position, 6f, "mu_m5_shot2");
                    return true;

                case "mu_m5_shot2":
                    MuShot(map, "mu_minefrombelow_up", 1, muP1.Position, 6f, "mu_m5_resume", mineTool: true);
                    return true;

                case "mu_m5_resume":
                    muPointer = IntVec3.Invalid;
                    Find.UIRoot.screenshotMode.Active = false;
                    Find.DesignatorManager.Deselect();
                    FloorView.Set(0);
                    MuRun(400, 35100, () => muA.Take(4).All(t => !MuRockOrOre(MuUp(t, map), map)), "mu_m5_mined");
                    return true;

                case "mu_m5_mined":
                {
                    bool mined = muA.Take(4).All(t => !MuRockOrOre(MuUp(t, map), map) && MuUp(t, map).GetTerrain(map) == PZDefOf.PZ_OpenAir && map.roofGrid.RoofAt(t) == PZDefOf.PZ_StairwellRoof);
                    Result("M5 colonists mined the 4 cells above the ramp and the exit from below: open air, stairwell roofs below", mined && MineUp.MinedFromBelow - muMined == 4,
                        $"{string.Join(", ", muA.Take(4).Select(t => $"{t} {MuRoof(t, map)}/{MuUp(t, map).GetTerrain(map).defName}"))}; mined from below {MineUp.MinedFromBelow - muMined}");
                    AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, muA[1], muRotA, map, stuffDef: ThingDefOf.WoodLog);
                    Result("M5 stairs in the covered shaft: placement accepted", ok.Accepted, $"'{(ok.Accepted ? "ok" : ok.Reason)}'");
                    if (!ok.Accepted)
                    {
                        MuEnd(map, "M5", "mu_m6");
                        return true;
                    }
                    MuWood(map, muSA - muDA);
                    GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, muA[1], map, muRotA, Faction.OfPlayer, ThingDefOf.WoodLog);
                    MuCrew(map, WorkTypeDefOf.Construction, 3);
                    MuRun(300, 35200, () => MuStairs(map, muA[1]) != null, "mu_m5_built");
                    return true;
                }

                case "mu_m5_built":
                {
                    IntVec3 e = MuUp(muA[3], map);
                    bool built = MuStairs(map, muA[1]) != null;
                    Result("M5 colonists built the stairs: the landing laid on the exit, the floor roof under it", built && Slab.At(e, map) && map.roofGrid.RoofAt(muA[3]) == PZDefOf.PZ_FloorRoof,
                        $"built {built}, exit {e} {e.GetTerrain(map).defName}, roof under {MuRoof(muA[3], map)}; footprint {string.Join(" ", muA.Take(3).Select(t => MuRoof(t, map)))}");
                    if (!built)
                    {
                        MuEnd(map, "M5", "mu_m6");
                        return true;
                    }
                    // M14 (a): the no-roof area on the stairs and the covered shaft cells
                    new Designator_AreaNoRoof().DesignateMultiCell(muA.Take(3));
                    muNote = string.Join(" ", muA.Take(3).Select(t => MuRoof(t, map)));
                    muFlag = false;
                    MuCrew(map, WorkTypeDefOf.Construction, 3);
                    MuRun(17, 35300, () => false, "mu_m5_noroof", () =>
                    {
                        if (Colonists(map).Any(p => p.CurJobDef == JobDefOf.RemoveRoof))
                        {
                            muFlag = true;
                        }
                    });
                    return true;
                }

                case "mu_m5_noroof":
                {
                    string now = string.Join(" ", muA.Take(3).Select(t => MuRoof(t, map)));
                    Result("M14 (a) the no-roof area on stairs in a covered shaft: no roof job in 2 in-game hours, roofs unchanged", !muFlag && now == muNote && muA.Take(3).All(t => map.areaManager.NoRoof[t]),
                        $"area {muA.Take(3).Count(t => map.areaManager.NoRoof[t])}/3, roof job seen {muFlag}, roofs {muNote} -> {now}");
                    foreach (IntVec3 t in muA.Take(3))
                    {
                        map.areaManager.NoRoof[t] = false;
                    }
                    muP3 = MuCrew(map, null, 1).FirstOrDefault();
                    Draft(muP3, false);
                    IntVec3 e = MuUp(muA[3], map);
                    muP3.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, e), JobTag.Misc);
                    MuRun(60, 35400, () => muP3.Position == e, "mu_m5_up");
                    return true;
                }

                case "mu_m5_up":
                {
                    IntVec3 e = MuUp(muA[3], map);
                    Result("M5 a colonist walked up the stairs in the shaft to the landing; level 1 counts as reached", muP3.Position == e && ProjectZ.Annex(map).Reached(1),
                        $"{muP3} at {muP3.Position} (level {ProjectZ.FloorOf(muP3.Position, map)}), landing {e}, level 1 reached {ProjectZ.Annex(map).Reached(1)}");
                    bool after = Combat.Los3D.CanSee(muA[0], e, map);
                    Result("M8 sight from the shaft bottom to the landing above: blocked before (rock), clear after", !muFlag2 && after, $"before {muFlag2}, after {after}");
                    Draft(muP3, true);
                    MuShot(map, "mu_stairs_shaft", 1, muA[2], 6f, "mu_m7");
                    return true;
                }

                case "mu_m7":
                {
                    // M7: a hole opened upward from the landing: the level-2 top over the shaft cell next to it
                    Find.UIRoot.screenshotMode.Active = false;
                    IntVec3 x = MuUp(muA[2], map);
                    AcceptanceReport r = new Designator_AreaNoRoof().CanDesignateCell(x);
                    new Designator_AreaNoRoof().DesignateMultiCell(new[] { x });
                    Result("M7 Remove roof on the shaft cell next to the landing (a bare level-2 top above): accepted", r.Accepted, $"{x}: '{(r.Accepted ? "ok" : r.Reason)}', top {MuUp(x, map)} {MuUp(x, map).GetTerrain(map).defName}");
                    Draft(muP3, false);
                    MuCrew(map, WorkTypeDefOf.Construction, 1);
                    foreach (Pawn p in Colonists(map))
                    {
                        if (p != muP3)
                        {
                            Draft(p, true);
                        }
                    }
                    FgWork(map, new[] { muP3 }, WorkTypeDefOf.Construction);
                    MuRun(120, 35500, () => MuUp(x, map).GetTerrain(map) == PZDefOf.PZ_OpenAir, "mu_m7_done");
                    return true;
                }

                case "mu_m7_done":
                {
                    IntVec3 x = MuUp(muA[2], map);
                    Result("M7 the level-2 top opens to the sky; the shaft cell's roof none; the ground shaft bottom below follows to none",
                        MuUp(x, map).GetTerrain(map) == PZDefOf.PZ_OpenAir && map.roofGrid.RoofAt(x) == null && map.roofGrid.RoofAt(muA[2]) == null && Shaft.Is(x, map),
                        $"top {MuUp(x, map).GetTerrain(map).defName}, {x} roof {MuRoof(x, map)}, ground {muA[2]} roof {MuRoof(muA[2], map)}, stack roofs {Shaft.StackRoofs}");
                    map.areaManager.NoRoof[x] = false;
                    Building_Stairs st = MuStairs(map, muA[1]);
                    if (st != null)
                    {
                        map.designationManager.AddDesignation(new Designation(st, DesignationDefOf.Deconstruct));
                    }
                    MuShot(map, "mu_shaft_sky_up", 2, muA[2], 6f, "mu_m5_decon");
                    return true;
                }

                case "mu_m5_decon":
                    Find.UIRoot.screenshotMode.Active = false;
                    foreach (Pawn p in Colonists(map))
                    {
                        Draft(p, false);
                    }
                    MuCrew(map, WorkTypeDefOf.Construction, 3);
                    MuRun(200, 35600, () => MuStairs(map, muA[1]) == null, "mu_m5_gone");
                    return true;

                case "mu_m5_gone":
                {
                    IntVec3 e = MuUp(muA[3], map);
                    bool gone = MuStairs(map, muA[1]) == null;
                    bool roofs = map.roofGrid.RoofAt(muA[0]) == PZDefOf.PZ_StairwellRoof && map.roofGrid.RoofAt(muA[1]) == PZDefOf.PZ_StairwellRoof
                        && map.roofGrid.RoofAt(muA[2]) == null && map.roofGrid.RoofAt(muA[3]) == PZDefOf.PZ_StairwellRoof;
                    Result("M5 the stairs deconstructed: the landing gone (open air), the footprint and the cell under the landing back to the shaft roofs (stairwell, sky where opened)",
                        gone && e.GetTerrain(map) == PZDefOf.PZ_OpenAir && roofs,
                        $"gone {gone}, exit {e.GetTerrain(map).defName}; roofs {string.Join(" ", muA.Take(4).Select(t => MuRoof(t, map)))}");
                    MuEnd(map, "M5", "mu_m6");
                    return true;
                }

                // ------------------------------------------------------------ M6
                case "mu_m6":
                {
                    MuStart(map, MuRect(muB, map, 4), false);
                    AcceptanceReport before = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, muB[1], muRotB, map, stuffDef: ThingDefOf.WoodLog);
                    Result("M6 stairs under the bare tops are refused: 'Ceiling is rock: mine it, or remove roof'", !before.Accepted && before.Reason == "PZ_CeilingIsRock".Translate(), $"'{before.Reason}'");
                    new Designator_AreaNoRoof().DesignateMultiCell(muB.Take(3));
                    MuCrew(map, WorkTypeDefOf.Construction, 3);
                    MuRun(200, 36000, () => muB.Take(3).All(t => MuUp(t, map).GetTerrain(map) == PZDefOf.PZ_OpenAir), "mu_m6_holes");
                    return true;
                }

                case "mu_m6_holes":
                {
                    bool holes = muB.Take(3).All(t => MuUp(t, map).GetTerrain(map) == PZDefOf.PZ_OpenAir && map.roofGrid.RoofAt(t) == null);
                    Result("M6 colonists opened the 3 tops above the ramp with Remove roof", holes && Hole.Opened - muHoles == 3, $"holes {Hole.Opened - muHoles}, roofs {string.Join(" ", muB.Take(3).Select(t => MuRoof(t, map)))}");
                    IntVec3 e = MuUp(muB[3], map);
                    muNote = e.GetTerrain(map).defName;
                    AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, muB[1], muRotB, map, stuffDef: ThingDefOf.WoodLog);
                    Result("M6 stairs onto the plateau: placement accepted", ok.Accepted, $"'{(ok.Accepted ? "ok" : ok.Reason)}', exit {e} {muNote}");
                    if (!ok.Accepted)
                    {
                        MuEnd(map, "M6", "mu_m9");
                        return true;
                    }
                    MuWood(map, muSB - muDB);
                    GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, muB[1], map, muRotB, Faction.OfPlayer, ThingDefOf.WoodLog);
                    MuRun(300, 36100, () => MuStairs(map, muB[1]) != null, "mu_m6_built");
                    return true;
                }

                case "mu_m6_built":
                {
                    IntVec3 e = MuUp(muB[3], map);
                    bool built = MuStairs(map, muB[1]) != null;
                    IntVec3 walk = GenRadial.RadialCellsAround(e, 3f, false).FirstOrDefault(c => c.InBounds(map) && TmTop(c, map) && c.DistanceTo(e) >= 2f && ProjectZ.SameBand(c, e, map) && c.Standable(map)
                        && map.reachability.CanReach(e, c, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors)));
                    Result("M6 colonists built the stairs: nothing laid on the exit top; the plateau around it revealed",
                        built && e.GetTerrain(map).defName == muNote && walk.IsValid && !map.fogGrid.IsFogged(walk) && ProjectZ.Annex(map).Reached(1),
                        $"built {built}, exit {e.GetTerrain(map).defName} (was {muNote}), walk target {walk} fogged {(walk.IsValid ? map.fogGrid.IsFogged(walk).ToString() : "-")}, level 1 reached {ProjectZ.Annex(map).Reached(1)}");
                    if (!built || !walk.IsValid)
                    {
                        MuEnd(map, "M6", "mu_m9");
                        return true;
                    }
                    muAt = walk;
                    muP3 = MuCrew(map, null, 1).FirstOrDefault();
                    Draft(muP3, false);
                    muP3.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, walk), JobTag.Misc);
                    MuRun(60, 36200, () => muP3.Position == muAt, "mu_m6_up");
                    return true;
                }

                case "mu_m6_up":
                    Result("M6 a colonist walked up from the tunnel onto the plateau top", muP3.Position == muAt, $"{muP3} at {muP3.Position} (level {ProjectZ.FloorOf(muP3.Position, map)}), target {muAt}");
                    Draft(muP3, true);
                    MuShot(map, "mu_stairs_plateau", 1, muB[2], 6f, "mu_m6_end");
                    return true;

                case "mu_m6_end":
                    Find.UIRoot.screenshotMode.Active = false;
                    MuEnd(map, "M6", "mu_m9");
                    return true;

                // ------------------------------------------------------------ M9
                case "mu_m9":
                {
                    MuStart(map, MuRect(muA, map, 3), true);
                    IntVec3 u = MuUp(muA[1], map);
                    new Designator_Mine().DesignateMultiCell(new[] { u });
                    muP1 = MuCrew(map, WorkTypeDefOf.Mining, 1).FirstOrDefault();
                    muRock = u.GetFirstMineable(map);
                    MuRun(100, 37000, () => muP1.CurJobDef == PZMineDefOf.PZ_MineFromBelow && muRock.HitPoints < muRock.MaxHitPoints, "mu_m9_cancel");
                    return true;
                }

                case "mu_m9_cancel":
                {
                    IntVec3 u = MuUp(muA[1], map);
                    muHp = muRock.HitPoints;
                    // cancelled as the game's cancel tool does (the designation removed)
                    new Designator_Cancel().DesignateSingleCell(u);
                    MuRun(1, 37100, () => false, "mu_m9_cancelled");
                    return true;
                }

                case "mu_m9_cancelled":
                {
                    IntVec3 u = MuUp(muA[1], map);
                    bool ended = muP1.CurJobDef != PZMineDefOf.PZ_MineFromBelow;
                    Result("M9 cancelled mid-job: the job ends, the rock keeps its damage, nothing opened",
                        ended && muRock.Spawned && muRock.HitPoints < muRock.MaxHitPoints && muRock.HitPoints >= muHp - 80 && u.GetTerrain(map) != PZDefOf.PZ_OpenAir && !ShaftMarks.Has(muA[1], map),
                        $"job now {muP1.CurJobDef?.defName ?? "none"}, rock HP {muRock.HitPoints}/{muRock.MaxHitPoints} (at cancel {muHp}), U {u.GetTerrain(map).defName}, roof below {MuRoof(muA[1], map)}");
                    MuEnd(map, "M9 cancel", "mu_m9b");
                    return true;
                }

                case "mu_m9b":
                {
                    MuStart(map, MuRect(muA, map, 3), true);
                    new Designator_Mine().DesignateMultiCell(new[] { MuUp(muA[1], map), MuUp(muA[2], map) });
                    List<Pawn> crew = MuCrew(map, WorkTypeDefOf.Mining, 2);
                    muP1 = crew[0];
                    muP2 = crew.Count > 1 ? crew[1] : null;
                    // (both start in the tunnel, so their work overlaps)
                    Teleport(muP1, muA[0]);
                    if (muP2 != null)
                    {
                        Teleport(muP2, muA[3]);
                    }
                    Func<Pawn, bool> working = p => p != null && p.CurJobDef == PZMineDefOf.PZ_MineFromBelow && p.jobs.curDriver.CurToilIndex >= 1;
                    MuRun(100, 37200, () => working(muP1) && working(muP2), "mu_m9b_both");
                    return true;
                }

                case "mu_m9b_both":
                {
                    bool both = muP2 != null && muP1.CurJobDef == PZMineDefOf.PZ_MineFromBelow && muP2.CurJobDef == PZMineDefOf.PZ_MineFromBelow && muP1.CurJob.targetA.Thing != muP2.CurJob.targetA.Thing;
                    Result("M9 two colonists mine side by side from below at once", both, $"{muP1} {muP1.CurJobDef?.defName} on {muP1.CurJob?.targetA}, {muP2} {muP2?.CurJobDef?.defName} on {muP2?.CurJob?.targetA}");
                    muInt = muP1.thingIDNumber;
                    muHp = muP2?.thingIDNumber ?? -1;
                    GameDataSaveLoader.SaveGame("pz_mineup_mid");
                    stage = "mu_m9b_loaded";
                    pendingLoad = "pz_mineup_mid";
                    return true;
                }

                case "mu_m9b_loaded":
                {
                    muP1 = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == muInt);
                    muP2 = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == muHp);
                    bool resumed = muP1?.CurJobDef == PZMineDefOf.PZ_MineFromBelow && muP2?.CurJobDef == PZMineDefOf.PZ_MineFromBelow;
                    Result("M9 saved mid-job and reloaded: both jobs still on", resumed, $"{muP1?.CurJobDef?.defName}, {muP2?.CurJobDef?.defName}");
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    MuRun(200, 37300, () => !MuRockOrOre(MuUp(muA[1], map), map) && !MuRockOrOre(MuUp(muA[2], map), map), "mu_m9b_done");
                    return true;
                }

                case "mu_m9b_done":
                {
                    bool open = new[] { muA[1], muA[2] }.All(t => MuUp(t, map).GetTerrain(map) == PZDefOf.PZ_OpenAir && map.roofGrid.RoofAt(t) == PZDefOf.PZ_StairwellRoof && Shaft.Is(t, map));
                    Result("M9 after the reload both rocks are mined from below and both shafts open", open, string.Join(", ", new[] { muA[1], muA[2] }.Select(t => $"{t} {MuRoof(t, map)}/{MuUp(t, map).GetTerrain(map).defName}")));
                    MuEnd(map, "M9", "mu_m13");
                    return true;
                }

                // ------------------------------------------------------------ M13
                case "mu_m13":
                {
                    MuStart(map, MuRect(muA.Concat(new[] { muSA - muDA * 4 }), map, 4), false);
                    Building_Stairs st = MuSpawnStairs(map);
                    // a big drag over the tunnel: 2 wide x 4 long on level 1, only rock
                    List<IntVec3> drag = new List<IntVec3>();
                    for (int i = 0; i < 4; i++)
                    {
                        foreach (IntVec3 g in new[] { muA[i], muA[i] + muSideA })
                        {
                            if (MuRock(MuUp(g, map), map))
                            {
                                drag.Add(MuUp(g, map));
                            }
                        }
                    }
                    new Designator_Mine().DesignateMultiCell(drag);
                    MuCrew(map, WorkTypeDefOf.Mining, 3);   // (more miners than faces: without the group rule the third holes the floor)
                    muInt = drag.Count;
                    muNote = string.Join(";", drag);
                    Info($"M13a: stairs {st?.Position} exit {st?.TopCell} ({st?.TopCell.GetTerrain(map)?.defName}); drag {drag.Count} rock: {muNote}");
                    MuRun(500, 38000, () => drag.All(u => !MuRockOrOre(u, map)), "mu_m13_done");
                    return true;
                }

                case "mu_m13_done":
                {
                    List<IntVec3> drag = muNote.Split(';').Where(s => s.Length > 0).Select(s => IntVec3.FromString(s)).ToList();
                    int floors = drag.Count(u => ProjectZ.HasFloor(u, map) && u.GetFirstMineable(map) == null);
                    int thick = muA.Take(4).Count(t => MuThick(t, map));
                    bool ok = floors == drag.Count && MineUp.MinedFromBelow - muMined == 0 && thick == 4;
                    Result("M13 (a) a big Mine drag on level 1 over the tunnel, reachable by stairs: dug on its own level, the floor stays, no shaft",
                        ok,
                        $"{floors}/{drag.Count} cells dug with their floor kept, mined from below {MineUp.MinedFromBelow - muMined}, tunnel roofs thick {thick}/4");
                    MuEnd(map, "M13a", "mu_m13b");
                    return true;
                }

                case "mu_m13b":
                {
                    MuStart(map, MuRect(muA.Concat(new[] { muSA - muDA * 4 }), map, 4), false);
                    new Designator_Mine().DesignateMultiCell(muA.Take(3).Select(t => MuUp(t, map)));
                    muP1 = MuCrew(map, WorkTypeDefOf.Mining, 1).FirstOrDefault();
                    MuRun(150, 38500, () => muP1.CurJobDef == PZMineDefOf.PZ_MineFromBelow && muP1.CurJob.targetA.Thing is Thing r && r.HitPoints < r.MaxHitPoints, "mu_m13b_flip");
                    return true;
                }

                case "mu_m13b_flip":
                {
                    muRock = muP1.CurJob?.targetA.Thing;
                    muHp = muRock?.HitPoints ?? -1;
                    Result("M13 (b) a 3-cell mark under solid rock is mined from below", muP1.CurJobDef == PZMineDefOf.PZ_MineFromBelow && muRock != null && muHp < muRock.MaxHitPoints,
                        $"{muP1} {muP1.CurJobDef?.defName} on {muRock} HP {muHp}");
                    if (muRock == null)
                    {
                        MuEnd(map, "M13b", "mu_m12");
                        return true;
                    }
                    // the stairs are finished now (up to the level-1 face next to the marked rock)
                    MuSpawnStairs(map);
                    muAt = muRock.Position;
                    muFlag = false;     // the from-below job ended on this rock
                    muFlag2 = false;    // vanilla's job took it
                    muInt = -1;         // HP when vanilla's job took it
                    muNote = null;      // the job that finished it
                    MuRun(400, 38600, () => muRock.Destroyed, "mu_m13b_done", () =>
                    {
                        if (!muFlag && muP1.CurJobDef != PZMineDefOf.PZ_MineFromBelow)
                        {
                            muFlag = true;
                        }
                        foreach (Pawn p in Colonists(map))
                        {
                            if (!muFlag2 && p.CurJobDef == JobDefOf.Mine && p.CurJob.targetA.Thing == muRock)
                            {
                                muFlag2 = true;
                                muInt = muRock.HitPoints;
                            }
                            if (p.CurJob != null && p.CurJob.targetA.Thing == muRock)
                            {
                                muNote = p.CurJobDef.defName;
                            }
                        }
                    });
                    return true;
                }

                case "mu_m13b_done":
                {
                    bool floorKept = ProjectZ.HasFloor(muAt, map);
                    Result("M13 (b) stairs finished mid-job: the job from below ends, vanilla's Mine finishes the rock from level 1 with its damage kept, the floor stays",
                        muFlag && muFlag2 && muInt >= 0 && muInt <= muHp && muRock.Destroyed && floorKept && muNote == "Mine" && MineUp.MinedFromBelow - muMined == 0,
                        $"from-below job ended {muFlag}, vanilla took it {muFlag2} at HP {muInt} (was {muHp}), destroyed {muRock.Destroyed} by {muNote}, floor at {muAt} {muAt.GetTerrain(map).defName}, mined from below {MineUp.MinedFromBelow - muMined}");
                    MuEnd(map, "M13b", "mu_m12");
                    return true;
                }

                // ------------------------------------------------------------ M12
                case "mu_m12":
                {
                    CellRect all = new CellRect(0, 0, map.Size.x, map.Size.z);
                    double on = 0, off = 0;
                    bool wasOff = VerticalFog.Off;
                    for (int k = 0; k < 2; k++)
                    {
                        map.fogGrid.Refog(all);
                        VerticalFog.Off = true;
                        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                        map.fogGrid.ClearAllFog();
                        off += sw.Elapsed.TotalMilliseconds;
                        map.fogGrid.Refog(all);
                        VerticalFog.Off = wasOff;
                        sw.Restart();
                        map.fogGrid.ClearAllFog();
                        on += sw.Elapsed.TotalMilliseconds;
                    }
                    on /= 2;
                    off /= 2;
                    List<double> opens = muOpenMs.OrderBy(x => x).ToList();
                    double median = opens.Count == 0 ? 0 : opens[opens.Count / 2];
                    Result("M12 the vertical reveal adds at most 60 ms to a whole-map ClearAllFog", on - off <= 60, $"{map.Size.x}x{map.Size.z}: on {on:0.0} ms, off {off:0.0} ms, added {on - off:0.0} ms");
                    Result("M12 Shaft.Open takes at most 1 ms (median over the checks)", median <= 1.0, $"median {median:0.000} ms, max {Shaft.MaxOpenMs:0.000} ms over {opens.Count} check(s) (last per check), opened {Shaft.Opened}");
                    Finish();
                    return true;
                }
            }
            return false;
        }

        // (info) IsoRim's work pose for a pawn: is it animated, with a tool, and which way it faces.
        private static void MuPose(Pawn p)
        {
            System.Reflection.MethodInfo m = HarmonyLib.AccessTools.Method(HarmonyLib.AccessTools.TypeByName("IsoRim.WorkAnimations"), "Animate");
            if (m == null)
            {
                Info("pose: IsoRim's WorkAnimations not found");
                return;
            }
            object[] args = { p, p.DrawPos, 0f, null, null, 0f, 0f };
            bool on = (bool)m.Invoke(null, args);
            Result("P7 IsoRim's work pose for a colonist mining from below: animated with a tool", on && args[3] != null, $"animated {on}, tool {(args[3] != null ? "yes" : "none")}, yaw {(float)args[2]:0}, lean {(float)args[5]:0.0}, pawn facing {p.Rotation}, job {p.CurJobDef?.defName}, at {p.Position} under {p.CurJob?.targetA.Cell}");
        }

        private static bool MuRockOrOre(IntVec3 c, Map map) => c.GetFirstMineable(map) != null;

        // The stairs onto level 1 behind tunnel A's mouth (the test's fixture for "stairs finished"): complete, wood.
        private static Building_Stairs MuSpawnStairs(Map map)
        {
            IntVec3 p = muSA - muDA * 2;
            Building_Stairs found = MuStairs(map, p);
            if (found != null)
            {
                return found;
            }
            Thing st = ThingMaker.MakeThing(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog);
            st.SetFaction(Faction.OfPlayer);
            return GenSpawn.Spawn(st, p, map, muRotA) as Building_Stairs;
        }

        // Shaft marks, terrain, roofs and fog of some cells (save/reload comparison).
        private static string MuState(Map map, IEnumerable<IntVec3> cells)
        {
            return string.Join("; ", cells.Select(c => $"{c} {(ShaftMarks.Has(c, map) ? "mark " : "")}{c.GetTerrain(map).defName} {MuRoof(c, map)} {(map.fogGrid.IsFogged(c) ? "fog" : "seen")}"));
        }
    }
}
