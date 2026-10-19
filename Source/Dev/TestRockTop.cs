using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode rocktop (rock-top floors, isorim-pz-rocktop-floors-plan.md §6): the fill's checks on a fixed-seed tall-mountain
    // map (as tallmtn: Mountainous, -projectztallmountains, -projectzfloors=4, -projectzmapsize=250), saved once as
    // pz_rocktop; every check after that reloads it, picks its site the same way every run (nearest the colonists) and
    // ends by checking that terrain, roofs and edifices outside its site did not change. Real flows with ticks:
    //   T1 generation (TmChecks: every ground rock has a top, ground roofs kept or the floor roof, solid rock over tall
    //      things, counters), save + load identical
    //   T2 colonists mine a 6-cell tunnel into a plateau on the ground: rock gone, thick roof kept, floors above stay
    //   T3 colonists mine a hall round a mountain-edge lip with a colonist, a wall, a stool and steel on the lip's top:
    //      the unsupported thin / floor roof falls with its floor (things fall, buildings break, rubble); thick-roofed
    //      cells drop collapsed rocks and keep their floors (vanilla; T4); supported floors stay
    //      G2/G3 x rock-top (merge into compat): a natural floor cannot be taken up; steel and an animal put on the air
    //      over a top fall onto it (bruised); the lip's fall lands where Column.Landing says; the air it leaves is air for
    //      gravity; the hall's roof-holder checks run through BandRadial's clip
    //   T5/T6 colonists build stairs whose exit is a lip top: nothing is laid there; walk up; a wall and a bed on lip tops;
    //      T7 a wood floor laid on a top allows the game's drop finders there, an unbuilt top still refuses them, and
    //      ~440 finder calls never give an unbuilt top; walk down; the stairs deconstructed: the natural floor stays
    //   T8 a colonist with a jump pack on a lip top jumps down to the ground and back up
    //   heat (D8, user 2026-10-18): a lip dug out by colonists and closed into a room, the lip top over it closed into a
    //      room: no heat through the natural floor; a 3x3 house with a slab over it: the built floor still passes heat
    public static partial class TestRunner
    {
        private static int rtGenHash;
        private static CellRect rtSite = CellRect.Empty;
        private static int rtOutside;
        private static readonly Dictionary<IntVec3, string> rtBefore = new Dictionary<IntVec3, string>();
        private static int rtCollapses;
        private static IntVec3 rtL, rtU, rtWall, rtStool, rtFar;
        private static Pawn rtRider, rtJumper;
        private static Thing rtSteel;
        private static IntVec3 rtSteelAt;
        private static int rtSteelLevel;
        private static readonly List<IntVec3> rtCells = new List<IntVec3>();
        private static readonly List<IntVec3> rtThick = new List<IntVec3>();
        private static int rtT3Skip;

        // Rock still to mine in T3's hall (collapsed rocks, vanilla's rubble in unsupported thick-roofed cells, are not).
        private static bool RtRock(IntVec3 c, Map map) => c.GetEdifice(map) is Mineable m && m.def != ThingDefOf.CollapsedRocks;
        private static int rtRiderHurt;
        private static IntVec3 rtStairs, rtExit, rtWalk, rtFoot, rtBedAt, rtWallAt, rtFloorAt, rtJumpTo;
        private static Rot4 rtRot;
        private static TerrainDef rtExitTerrain;
        private static RoofDef rtExitRoof;
        private static Pawn rtWalker;
        private static Verb rtVerb;

        private static bool RtLip(IntVec3 g, Map map)
        {
            if (!g.InBounds(map) || g.z < 1 || g.z >= ProjectZ.GroundZ(map) - 1)
            {
                return false;
            }
            Building b = g.GetEdifice(map);
            IntVec3 u = Levels.Cell(g, 1, map);
            return b != null && b.def.building != null && b.def.building.isNaturalRock && !b.def.building.isResourceRock
                && !(map.roofGrid.RoofAt(g)?.isThickRoof ?? false) && u.IsValid && TmTop(u, map) && u.Standable(map) && !map.fogGrid.IsFogged(u);
        }

        // Terrain, roof and edifice of every cell of every level whose ground cell is outside the site.
        private static int RtHash(Map map, CellRect site) => RtHash(map, site, null);

        // (cells: per cell "x,z L: terrain roof edifice", for naming what changed)
        private static int RtHash(Map map, CellRect site, Dictionary<IntVec3, string> cells)
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
                        h = Gen.HashCombineInt(h, c.GetTerrain(map).shortHash);
                        h = Gen.HashCombineInt(h, map.roofGrid.RoofAt(c)?.shortHash ?? 0);
                        h = Gen.HashCombineInt(h, c.GetEdifice(map)?.def.shortHash ?? 0);
                        if (cells != null)
                        {
                            cells[c] = $"{c.GetTerrain(map).defName} {map.roofGrid.RoofAt(c)?.defName ?? "-"} {c.GetEdifice(map)?.def.defName ?? "-"}";
                        }
                    }
                }
            }
            return h;
        }

        private static readonly HarmonyLib.AccessTools.FieldRef<Pawn_DraftController, AutoUndrafter> autoUndrafterRef = HarmonyLib.AccessTools.FieldRefAccess<Pawn_DraftController, AutoUndrafter>("autoUndrafter");

        private static void RtWork(Map map, WorkTypeDef w)
        {
            foreach (Pawn p in Colonists(map))
            {
                if (p == rtRider && p.Drafted)
                {
                    continue;   // (T3's rider waits on the lip top)
                }
                p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
                foreach (WorkTypeDef d in DefDatabase<WorkTypeDef>.AllDefs)
                {
                    if (!p.WorkTypeIsDisabled(d))
                    {
                        p.workSettings.SetPriority(d, d == w ? 1 : d == WorkTypeDefOf.Hauling && w == WorkTypeDefOf.Construction ? 2 : 0);
                    }
                }
            }
        }

        private static void RtSiteStart(Map map, CellRect site)
        {
            rtSite = site;
            rtBefore.Clear();
            rtOutside = RtHash(map, site, rtBefore);
            rtCollapses = PZStats.slabCollapses;
            DebugSettings.enableRandomMentalStates = false;
            Find.PlaySettings.autoRebuild = false;
            Find.Storyteller.storytellerComps.Clear();   // no incidents in the check's ticks (test only): nothing else lands on the map
            Find.Storyteller.incidentQueue.Clear();
        }

        private static void RtSiteEnd(Map map, string check, string next)
        {
            Dictionary<IntVec3, string> after = new Dictionary<IntVec3, string>();
            int now = RtHash(map, rtSite, after);
            List<string> diff = after.Where(kv => !rtBefore.TryGetValue(kv.Key, out string was) || was != kv.Value).Take(6)
                .Select(kv => $"{kv.Key} L{ProjectZ.FloorOf(kv.Key, map)} ground {ProjectZ.GroundCellOf(kv.Key, map)}: {(rtBefore.TryGetValue(kv.Key, out string w) ? w : "?")} -> {kv.Value}").ToList();
            Result($"{check}: terrain, roofs and edifices outside the site unchanged", now == rtOutside, $"site {rtSite}{(diff.Count == 0 ? "" : "; changed: " + string.Join("; ", diff))}");
            tickWatch = null;
            if (next == null)
            {
                Finish();
                return;
            }
            stage = next;
            pendingLoad = "pz_rocktop";
        }

        // Test support during T3's long mining (about 2-6 game days on a quick-test colony with no food): the colonists are kept
        // fed and rested, and the rider kept drafted on the lip top (the game undrafts a pawn waiting 10000 ticks). Always true.
        private static bool RtKeep(Map map)
        {
            foreach (Pawn p in Colonists(map))
            {
                if (p.needs?.food != null)
                {
                    p.needs.food.CurLevel = p.needs.food.MaxLevel;
                }
                if (p.needs?.rest != null)
                {
                    p.needs.rest.CurLevel = p.needs.rest.MaxLevel;
                }
                if (p.needs?.joy != null)
                {
                    p.needs.joy.CurLevel = p.needs.joy.MaxLevel;
                }
            }
            if (rtSteel != null && rtSteel.Spawned)
            {
                rtSteelAt = rtSteel.Position;
                rtSteelLevel = ProjectZ.FloorOf(rtSteel.Position, map);
            }
            if (rtRider != null && rtRider.Spawned && rtU.IsValid && rtRider.Position == rtU && rtU.GetTerrain(map) != PZDefOf.PZ_OpenAir)
            {
                // (the game's auto-undraft counter put back, as drafting does: no undraft, no job in between)
                if (rtRider.Drafted)
                {
                    autoUndrafterRef(rtRider.drafter)?.Notify_Drafted();
                }
                else
                {
                    Draft(rtRider, true);
                }
            }
            return true;
        }

        private static IntVec3 rtSlabC;
        private static float rtHeatT0, rtHeatSlab;
        private static string rtHeatNat;

        private static void RtWall(Map map, IntVec3 c)
        {
            foreach (Thing t in c.GetThingList(map).Where(t => t.def.category == ThingCategory.Plant || t.def.category == ThingCategory.Item).ToList())
            {
                t.Destroy();
            }
            Thing w = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
            w.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(w, c, map);
        }

        private static IntVec3 RtHome(Map map)
        {
            List<Pawn> cols = Colonists(map);
            return cols.Count == 0 ? new IntVec3(map.Size.x / 2, 0, ProjectZ.GroundZ(map) / 2) : new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z));
        }

        private static bool StepRockTop(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            switch (stage)
            {
                case "rt_setup":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    if (a == null || !Mountains.On(map))
                    {
                        Result("rocktop: a tall-mountain map", false, "no annex or no tall mountains (-projectztallmountains)");
                        Finish();
                        return true;
                    }
                    Info($"rocktop: map {map.Size}, tile {map.Tile} {map.TileInfo?.hilliness}, floors {a.floors}; fill {Mountains.LastSeconds:0.00} s, rock {Mountains.LastRock}, tops {Mountains.LastTops}, lips {Mountains.LastLips} ({Mountains.LastLipRoofs} floor roofs), raised over tall things {Mountains.LastTallRaised}");
                    TmChecks(map, a);
                    rtGenHash = RtHash(map, CellRect.Empty);
                    GameDataSaveLoader.SaveGame("pz_rocktop");
                    stage = "rt_loaded";
                    pendingLoad = "pz_rocktop";
                    return true;
                }
                case "rt_loaded":
                    Result("T1 save + load: terrain, roofs and edifices of every level identical", RtHash(map, CellRect.Empty) == rtGenHash, $"hash {rtGenHash}");
                    stage = "rt_t2";
                    return true;

                // ------------------------------------------------------------ T2
                case "rt_t2":
                {
                    IntVec3 home = RtHome(map);
                    Pawn any = Colonists(map).FirstOrDefault();
                    rtCells.Clear();
                    IntVec3 stand = IntVec3.Invalid;
                    foreach (IntVec3 s in Near(home, 90, map))
                    {
                        if (!s.Standable(map) || s.Roofed(map) || map.fogGrid.IsFogged(s) || any == null || !map.reachability.CanReach(any.Position, s, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors)))
                        {
                            continue;
                        }
                        foreach (IntVec3 d in GenAdj.CardinalDirections)
                        {
                            bool ok = true;
                            for (int i = 1; i <= 6 && ok; i++)
                            {
                                IntVec3 g = s + d * i;
                                Building b = g.InBounds(map) && g.z < a.groundZ ? g.GetEdifice(map) : null;
                                ok = b is Mineable && !b.def.building.isResourceRock && (map.roofGrid.RoofAt(g)?.isThickRoof ?? false)
                                    && Floors.KindAt(Levels.Cell(g, 1, map), map) == FloorKind.NaturalRock && (i > 1 || !map.fogGrid.IsFogged(g));
                            }
                            if (ok)
                            {
                                stand = s;
                                for (int i = 1; i <= 6; i++)
                                {
                                    rtCells.Add(s + d * i);
                                }
                                break;
                            }
                        }
                        if (stand.IsValid)
                        {
                            break;
                        }
                    }
                    if (!stand.IsValid)
                    {
                        Result("T2 a plateau face on the ground for a 6-cell tunnel", false, "none found");
                        stage = "rt_t3";
                        pendingLoad = "pz_rocktop";
                        return true;
                    }
                    RtSiteStart(map, CellRect.FromLimits(rtCells.Min(c => c.x), rtCells.Min(c => c.z), rtCells.Max(c => c.x), rtCells.Max(c => c.z)).ExpandedBy(1));
                    foreach (IntVec3 c in rtCells)
                    {
                        map.designationManager.AddDesignation(new Designation(c, DesignationDefOf.Mine));
                    }
                    RtWork(map, WorkTypeDefOf.Mining);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    Info($"T2 tunnel from {stand}: {string.Join(" ", rtCells)}");
                    TickUntil(80000, () => rtCells.All(c => c.GetEdifice(map) == null), "rt_t2_done", 120);
                    return true;
                }
                case "rt_t2_done":
                {
                    int mined = rtCells.Count(c => c.GetEdifice(map) == null);
                    int thick = rtCells.Count(c => map.roofGrid.RoofAt(c)?.isThickRoof ?? false);
                    int natural = rtCells.Count(c => Floors.IsNatural(c.GetTerrain(map)));
                    int floors = rtCells.Count(c => Floors.KindAt(Levels.Cell(c, 1, map), map) == FloorKind.NaturalRock);
                    int rocks = rtCells.Count(c => c.GetThingList(map).Any(t => t.def == ThingDefOf.CollapsedRocks));
                    Result("T2 colonists mined a tunnel under a plateau: rock gone, natural floor, thick roof kept, the floor above stays, nothing collapsed",
                        mined == 6 && thick == 6 && natural == 6 && floors == 6 && rocks == 0 && PZStats.slabCollapses == rtCollapses,
                        $"mined {mined}/6, thick roof {thick}, natural floor {natural}, floors above {floors}, collapsed rocks {rocks}, floor collapses {PZStats.slabCollapses - rtCollapses}");
                    RtSiteEnd(map, "T2", "rt_t3");
                    return true;
                }

                // ------------------------------------------------------------ T3 (+ T4)
                case "rt_t3":
                {
                    IntVec3 home = RtHome(map);
                    Pawn any = Colonists(map).FirstOrDefault();
                    rtL = IntVec3.Invalid;
                    const float R = 9.5f;
                    int skip = rtT3Skip;
                    foreach (IntVec3 g in Near(home, 110, map))
                    {
                        if (!RtLip(g, map) || !RtLip(g + IntVec3.East, map) || !RtLip(g + IntVec3.West, map) || !RtLip(g + IntVec3.East * 2, map))
                        {
                            continue;
                        }
                        IntVec3 s = g + IntVec3.South;
                        if (!s.Standable(map) || s.Roofed(map) || map.fogGrid.IsFogged(s) || any == null || !map.reachability.CanReach(any.Position, s, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors)))
                        {
                            continue;
                        }
                        // everything holding a roof within R is natural rock (mined below), nothing built, no colonists there
                        bool ok = true;
                        int rock = 0;
                        foreach (IntVec3 c in GenRadial.RadialCellsAround(g, R, true))
                        {
                            if (!c.InBounds(map) || c.z >= a.groundZ)
                            {
                                ok = false;
                                break;
                            }
                            Building b = c.GetEdifice(map);
                            if (b != null && !(b is Mineable) || c.GetThingList(map).Any(t => t is Pawn || t.def.category == ThingCategory.Building && !(t is Mineable)))
                            {
                                ok = false;
                                break;
                            }
                            rock += b is Mineable ? 1 : 0;
                        }
                        if (ok && rock <= 220 && skip-- <= 0)
                        {
                            rtL = g;
                            break;
                        }
                    }
                    if (!rtL.IsValid)
                    {
                        Result("T3 a mountain-edge lip for a hall", false, "none found");
                        stage = "rt_t5";
                        pendingLoad = "pz_rocktop";
                        return true;
                    }
                    RtSiteStart(map, CellRect.CenteredOn(rtL, 11));
                    rtU = Levels.Cell(rtL, 1, map);
                    RtG2Probes(map, home);
                    rtWall = Levels.Cell(rtL + IntVec3.West, 1, map);
                    rtStool = Levels.Cell(rtL + IntVec3.East, 1, map);
                    IntVec3 steelAt = Levels.Cell(rtL + IntVec3.East * 2, 1, map);
                    // a top (or a lip top) just past the hall's reach, for "supported floors stay"
                    rtFar = Near(rtL, 16, map).Where(g => (g - rtL).LengthHorizontal > R + 3f && (g - rtL).LengthHorizontal < R + 6f && TH(g, map) >= 1 && ProjectZ.HasFloor(Levels.Cell(g, 1, map), map)).Select(g => Levels.Cell(g, 1, map)).FirstOrDefault();
                    Thing w = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                    w.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(w, rtWall, map);
                    Thing st = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Stool"), ThingDefOf.WoodLog);
                    st.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(st, rtStool, map);
                    rtSteel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    rtSteel.stackCount = 30;
                    GenPlace.TryPlaceThing(rtSteel, steelAt, map, ThingPlaceMode.Direct);
                    rtRider = Colonists(map).OrderBy(p => p.Position.DistanceToSquared(rtL)).Last();
                    // (the climb up is T5's: the rider is placed on the lip top and drafted there)
                    Teleport(rtRider, rtU);
                    Draft(rtRider, true);
                    rtRider.workSettings?.DisableAll();
                    // (an ordered wait, not Wait_Combat: the game's auto-undraft after 10000 idle ticks leaves it drafted there)
                    rtRider.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Wait, 500000), JobTag.Misc);
                    rtRiderHurt = rtRider.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);
                    rtCells.Clear();
                    foreach (IntVec3 c in GenRadial.RadialCellsAround(rtL, R, true))
                    {
                        if (c.GetEdifice(map) is Mineable)
                        {
                            rtCells.Add(c);
                            map.designationManager.AddDesignation(new Designation(c, DesignationDefOf.Mine));
                        }
                    }
                    RtWork(map, WorkTypeDefOf.Mining);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    Info($"T3 lip {rtL} (roof {map.roofGrid.RoofAt(rtL)?.defName}), top {rtU}; {rtCells.Count} rock cells within {R} designated; rider {rtRider} drafted on the top, wall {rtWall}, stool {rtStool}, steel {rtSteel.Position}; a supported top {rtFar}");
                    rtThick.Clear();
                    rtThick.AddRange(rtCells.Where(c => map.roofGrid.RoofAt(c)?.isThickRoof ?? false));
                    TickUntil(400000, () => RtG2AnimalCheck() && RtKeep(map) && (rtU.GetTerrain(map) == PZDefOf.PZ_OpenAir || rtCells.All(c => !RtRock(c, map))), "rt_t3_settle", 60);
                    return true;
                }
                case "rt_t3_settle":
                {
                    RtG2AnimalCheck();
                    string left = string.Join(" ", rtCells.Where(c => c.GetEdifice(map) is Mineable).Select(c => c + " desig " + (map.designationManager.DesignationAt(c, DesignationDefOf.Mine) != null)
                        + " fog " + map.fogGrid.IsFogged(c) + " roof " + map.roofGrid.RoofAt(c)?.defName + " standable near " + GenAdj.CellsAdjacentCardinal(c, Rot4.North, IntVec2.One).Count(n => n.InBounds(map) && n.Standable(map))));
                    Info($"T3 after {ticksTotal} ticks (rock cells left: {left}): lip top {rtU.GetTerrain(map).defName}");
                    // all the rock mined and the lip still up: collapsed rocks (vanilla, in the hall's thick-roofed cells) hold
                    // its roof; the next site (at most three)
                    if (rtU.GetTerrain(map) != PZDefOf.PZ_OpenAir && rtT3Skip < 2)
                    {
                        int held = GenRadial.RadialCellsAround(rtL, 6.9f, true).Count(c => c.InBounds(map) && c.GetEdifice(map)?.def == ThingDefOf.CollapsedRocks);
                        Info($"T3 the lip at {rtL} is held up ({held} collapsed rocks within 6.9): the next site");
                        rtT3Skip++;
                        tickWatch = null;
                        stage = "rt_t3";
                        pendingLoad = "pz_rocktop";
                        return true;
                    }
                }
                    TickUntil(300, () => false, "rt_t3_fell", 60);
                    return true;
                case "rt_t3_fell":
                {
                    bool fell = rtU.GetTerrain(map) == PZDefOf.PZ_OpenAir;
                    bool riderDown = rtRider.Spawned && ProjectZ.FloorOf(rtRider.Position, map) == 0;
                    int hurt = rtRider.health.hediffSet.hediffs.Count(h => h is Hediff_Injury) - rtRiderHurt;
                    bool rubble = GenRadial.RadialCellsAround(rtL, 1.5f, true).Any(c => c.GetThingList(map).Any(t => t.def.category == ThingCategory.Filth && t.def.defName.Contains("Rubble")));
                    Result("T3 a hall mined under a lip: once its thin or floor roof is unsupported it falls with the lip's floor; the colonist on it falls to the ground hurt (alive), rubble, a floor collapse",
                        fell && riderDown && hurt > 0 && !rtRider.Dead && rubble && PZStats.slabCollapses > rtCollapses,
                        $"lip top {rtU} {rtU.GetTerrain(map).defName}, roof under it {map.roofGrid.RoofAt(rtL)?.defName ?? "none"}; {rtRider} at {(rtRider.Spawned ? rtRider.Position.ToString() : "-")} level {(rtRider.Spawned ? ProjectZ.FloorOf(rtRider.Position, map) : -9)}, dead {rtRider.Dead}, new injuries {hurt} ({string.Join(",", rtRider.health.hediffSet.hediffs.OfType<Hediff_Injury>().Select(h => h.def.defName))}); rubble {rubble}; floor collapses {PZStats.slabCollapses - rtCollapses}");
                    RtG2AfterFall(map, fell);
                    // the rider is taken out of the hall (the rest of it is still being mined) and the mining goes on
                    if (rtRider.Spawned)
                    {
                        Draft(rtRider, false);
                        IntVec3 safe = CellFinder.StandableCellNear(rtL + IntVec3.South * 14, map, 6f);
                        if (safe.IsValid)
                        {
                            Teleport(rtRider, safe);
                        }
                        rtRider.workSettings?.DisableAll();
                    }
                    TickUntil(400000, () => RtKeep(map) && rtCells.All(c => !RtRock(c, map)), "rt_t3_settle2", 120);
                    return true;
                }
                case "rt_t3_settle2":
                    Info($"T3 hall mined after {ticksTotal} more ticks: {rtCells.Count(c => c.GetEdifice(map) is Mineable)} rock cells left");
                    TickUntil(600, () => false, "rt_t3_done", 60);
                    return true;
                case "rt_t3_done":
                {
                    bool wallGone = !rtWall.GetThingList(map).Any(t => t.def == ThingDefOf.Wall);
                    bool stoolGone = !rtStool.GetThingList(map).Any(t => t.def.defName == "Stool");
                    bool steelDown = rtSteelLevel == 0;
                    bool wallFloor = ProjectZ.HasFloor(rtWall, map), stoolFloor = ProjectZ.HasFloor(rtStool, map), steelFloor = ProjectZ.HasFloor(Levels.Cell(rtL + IntVec3.East * 2, 1, map), map);
                    Result("T3 the wall, the stool and the steel on the lip's top: where their floor fell the buildings broke and the steel came down; where it stayed they stayed",
                        wallGone == !wallFloor && stoolGone == !stoolFloor && steelDown == !steelFloor && (wallGone || stoolGone || steelDown),
                        $"wall cell floor {wallFloor}, wall gone {wallGone}; stool cell floor {stoolFloor}, stool gone {stoolGone}; steel cell floor {steelFloor}, steel last seen at {rtSteelAt} level {rtSteelLevel} (spawned now {rtSteel.Spawned})");
                    // T4: the hall's thick-roofed cells: the roof stays thick and the floor above stays (collapsed rocks where unsupported, vanilla)
                    int still = rtThick.Count(c => map.roofGrid.RoofAt(c)?.isThickRoof ?? false);
                    int rocks = rtThick.Count(c => c.GetThingList(map).Any(t => t.def == ThingDefOf.CollapsedRocks));
                    int kept = rtThick.Count(c => ProjectZ.HasFloor(Levels.Cell(c, 1, map), map));
                    Result("T4 thick-roofed cells of the hall: the roof never vanishes and the floor above stays (collapsed rocks where unsupported, vanilla)",
                        rtThick.Count > 0 && still == rtThick.Count && kept == rtThick.Count, $"{rtThick.Count} thick-roofed hall cells: still thick {still}, floors above kept {kept}, collapsed rocks in {rocks}");
                    bool farKept = rtFar.IsValid && ProjectZ.HasFloor(rtFar, map);
                    int supported = rtCells.Count(c => !rtThick.Contains(c) && ProjectZ.HasFloor(Levels.Cell(c, 1, map), map));
                    Result("T3 supported floors stay (a top past the hall)", farKept, $"top {rtFar} floor {farKept}; thin or floor-roofed hall cells still with a floor above {supported} of {rtCells.Count - rtThick.Count}");
                    RtSiteEnd(map, "T3", "rt_t5");
                    return true;
                }

                // ------------------------------------------------------------ T5, T6, T7
                case "rt_t5":
                {
                    IntVec3 home = RtHome(map);
                    Pawn any = Colonists(map).FirstOrDefault();
                    rtStairs = IntVec3.Invalid;
                    foreach (IntVec3 p in Near(home, 90, map))
                    {
                        if (p.z >= a.groundZ - 3 || p.z < 3 || p.x < 3 || p.x >= map.Size.x - 3 || rtStairs.IsValid)
                        {
                            continue;
                        }
                        for (int r = 0; r < 4 && !rtStairs.IsValid; r++)
                        {
                            Rot4 rot = new Rot4(r);
                            IntVec3 exit = StairShape.Exit(p, rot, map);
                            if (!exit.IsValid || !RtLip(ProjectZ.GroundCellOf(exit, map), map))
                            {
                                continue;
                            }
                            if (StairShape.Footprint(p, rot).Cells.Any(c => !c.InBounds(map) || map.fogGrid.IsFogged(c) || c.Roofed(map) || c.GetEdifice(map) != null || !c.Standable(map))
                                || any == null || !map.reachability.CanReach(any.Position, p, PathEndMode.Touch, TraverseParms.For(TraverseMode.PassDoors))
                                || !GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, p, rot, map, stuffDef: ThingDefOf.WoodLog).Accepted)
                            {
                                continue;
                            }
                            IntVec3 walk = GenRadial.RadialCellsAround(exit, 3f, false).FirstOrDefault(c => c.InBounds(map) && TmTop(c, map) && c.DistanceTo(exit) >= 2f && ProjectZ.SameBand(c, exit, map)
                                && map.reachability.CanReach(exit, c, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors)));
                            if (!walk.IsValid || !TfWallBed(map, walk, exit, out _, out _))
                            {
                                continue;
                            }
                            rtStairs = p;
                            rtRot = rot;
                            rtExit = exit;
                            rtWalk = walk;
                            rtFoot = p - rot.FacingCell * 2;
                        }
                    }
                    if (!rtStairs.IsValid)
                    {
                        Result("T5 a stairs site whose exit is a lip top", false, "none found");
                        stage = "rt_t8";
                        pendingLoad = "pz_rocktop";
                        return true;
                    }
                    RtSiteStart(map, CellRect.CenteredOn(ProjectZ.GroundCellOf(rtExit, map), 16));
                    rtExitTerrain = rtExit.GetTerrain(map);
                    rtExitRoof = map.roofGrid.RoofAt(FloorRoof.Below(rtExit, map));
                    for (int i = 0; i < 4; i++)
                    {
                        Thing wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                        wood.stackCount = 75;
                        GenPlace.TryPlaceThing(wood, rtFoot + rtRot.Opposite.FacingCell, map, ThingPlaceMode.Near);
                    }
                    AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, rtStairs, rtRot, map, stuffDef: ThingDefOf.WoodLog);
                    GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, rtStairs, map, rtRot, Faction.OfPlayer, ThingDefOf.WoodLog);
                    Result("T6 stairs whose exit is a lip top: placement accepted (the floor there is used as it is)", ok.Accepted && SlabSupport.LandingBlocked(rtExit, map) == null,
                        $"stairs {rtStairs} facing {rtRot}, exit {rtExit} {rtExitTerrain.defName} over {ProjectZ.GroundCellOf(rtExit, map)} (roof {rtExitRoof?.defName ?? "none"}): '{(ok.Accepted ? "ok" : ok.Reason)}'");
                    RtWork(map, WorkTypeDefOf.Construction);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(40000, () => map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).Any(t => t.Position == rtStairs), "rt_t5_built", 120);
                    return true;
                }
                case "rt_t5_built":
                {
                    bool built = map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).Any(t => t.Position == rtStairs);
                    bool same = rtExit.GetTerrain(map) == rtExitTerrain && map.roofGrid.RoofAt(FloorRoof.Below(rtExit, map)) == rtExitRoof;
                    Result("T5 colonists built the stairs onto the lip top; no landing laid (the exit keeps its natural floor and the roof under it)", built && same,
                        $"built {built}; exit {rtExit} {rtExit.GetTerrain(map).defName} (was {rtExitTerrain.defName}), roof under {map.roofGrid.RoofAt(FloorRoof.Below(rtExit, map))?.defName ?? "none"} (was {rtExitRoof?.defName ?? "none"})");
                    rtWalker = Colonists(map).FirstOrDefault(p => !p.Downed && p.health.capacities.CapableOf(PawnCapacityDefOf.Moving));
                    if (!built || rtWalker == null)
                    {
                        RtSiteEnd(map, "T5", "rt_t8");
                        return true;
                    }
                    RtWork(map, null);
                    rtWalker.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, rtWalk), JobTag.Misc);
                    TickUntil(6000, () => rtWalker.Position == rtWalk, "rt_t5_up", 30);
                    return true;
                }
                case "rt_t5_up":
                {
                    Result("T5 a colonist walked up the stairs onto the lip top and on to the mountain", rtWalker.Position == rtWalk, $"{rtWalker} at {rtWalker.Position} (level {ProjectZ.FloorOf(rtWalker.Position, map)}), target {rtWalk}");
                    TfWallBed(map, rtWalk, rtExit, out rtWallAt, out rtBedAt);
                    // a wood floor on a top beside the walker (a player floor laid on natural stone)
                    rtFloorAt = GenRadial.RadialCellsAround(rtWalk, 5f, false).FirstOrDefault(c => c.InBounds(map) && TmTop(c, map) && c != rtWallAt && !GenAdj.OccupiedRect(rtBedAt, Rot4.North, ThingDefOf.Bed.size).Contains(c)
                        && c.DistanceTo(rtExit) >= 1.5f && GenConstruct.CanPlaceBlueprintAt(TerrainDefOf.WoodPlankFloor, c, Rot4.North, map).Accepted);
                    GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, rtWallAt, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
                    GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Bed, rtBedAt, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
                    if (rtFloorAt.IsValid)
                    {
                        GenConstruct.PlaceBlueprintForBuild(TerrainDefOf.WoodPlankFloor, rtFloorAt, map, Rot4.North, Faction.OfPlayer, null);
                    }
                    Info($"T5 wall {rtWallAt}, bed {rtBedAt}, wood floor {rtFloorAt}");
                    RtWork(map, WorkTypeDefOf.Construction);
                    TickUntil(60000, () => rtWallAt.GetEdifice(map)?.def == ThingDefOf.Wall && map.listerThings.ThingsOfDef(ThingDefOf.Bed).Any(b => b.Position == rtBedAt)
                        && (!rtFloorAt.IsValid || rtFloorAt.GetTerrain(map) == TerrainDefOf.WoodPlankFloor), "rt_t5_built2", 120);
                    return true;
                }
                case "rt_t5_built2":
                {
                    bool wall = rtWallAt.GetEdifice(map)?.def == ThingDefOf.Wall, bed = map.listerThings.ThingsOfDef(ThingDefOf.Bed).Any(b => b.Position == rtBedAt);
                    Result("T5 colonists built a wall and a bed on the tops past the lip", wall && bed, $"wall {rtWallAt} {wall}, bed {rtBedAt} {bed}");
                    // T7: the game's drop finders (inside their scope): a top with a player floor counts, an unbuilt top does not
                    bool floorLaid = rtFloorAt.IsValid && rtFloorAt.GetTerrain(map) == TerrainDefOf.WoodPlankFloor;
                    IntVec3 bare = GenRadial.RadialCellsAround(rtWalk, 6f, false).FirstOrDefault(c => c.InBounds(map) && TmTop(c, map) && Floors.UnbuiltNatural(c, map) && c.Standable(map));
                    Patches.DropRules.noTops++;
                    bool floorOk, bareOk;
                    try
                    {
                        floorOk = floorLaid && DropCellFinder.CanPhysicallyDropInto(rtFloorAt, map, false);
                        bareOk = bare.IsValid && !DropCellFinder.CanPhysicallyDropInto(bare, map, false);
                    }
                    finally
                    {
                        Patches.DropRules.noTops--;
                    }
                    Result("T7 inside the game's drop finders a wood floor laid on a top by colonists is a drop spot, an unbuilt top is not", floorOk && bareOk,
                        $"wood floor {rtFloorAt} laid {floorLaid} (under {rtFloorAt.GetTerrain(map)?.defName}/{map.terrainGrid.UnderTerrainAt(rtFloorAt)?.defName}) allowed {floorOk}; bare top {bare} refused {bareOk}");
                    int n = 0, onBare = 0;
                    string first = null;
                    void Count(IntVec3 c)
                    {
                        n++;
                        if (c.IsValid && c.InBounds(map) && Floors.UnbuiltNatural(c, map))
                        {
                            onBare++;
                            first = first ?? c.ToString();
                        }
                    }
                    for (int i = 0; i < 200; i++)
                    {
                        DropCellFinder.TryFindRaidDropCenterClose(out IntVec3 c, map);
                        Count(c);
                        Count(DropCellFinder.RandomDropSpot(map));
                    }
                    for (int i = 0; i < 20; i++)
                    {
                        Count(DropCellFinder.FindRaidDropCenterDistant(map));
                        if (DropCellFinder.FindSafeLandingSpot(out IntVec3 s, null, map))
                        {
                            Count(s);
                        }
                    }
                    Result("T7 the game's raid centre, random drop and safe landing finders never give an unbuilt top (lips included)", onBare == 0, $"{n} spots, {onBare} on an unbuilt top{(first == null ? "" : ", first " + first)}");
                    RtWork(map, null);
                    rtWalker = Colonists(map).FirstOrDefault(p => !p.Downed && p.health.capacities.CapableOf(PawnCapacityDefOf.Moving) && ProjectZ.FloorOf(p.Position, map) >= 1) ?? rtWalker;
                    rtWalker.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, rtFoot), JobTag.Misc);
                    TickUntil(6000, () => rtWalker.Position == rtFoot, "rt_t5_down", 30);
                    return true;
                }
                case "rt_t5_down":
                {
                    Result("T5 a colonist walked down the stairs from the lip top to the ground", rtWalker.Position == rtFoot && ProjectZ.FloorOf(rtWalker.Position, map) == 0,
                        $"{rtWalker} at {rtWalker.Position} (level {ProjectZ.FloorOf(rtWalker.Position, map)}), target {rtFoot}");
                    Thing st = map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).FirstOrDefault(t => t.Position == rtStairs);
                    if (st != null)
                    {
                        map.designationManager.AddDesignation(new Designation(st, DesignationDefOf.Deconstruct));
                    }
                    RtWork(map, WorkTypeDefOf.Construction);
                    TickUntil(40000, () => !map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).Any(t => t.Position == rtStairs), "rt_t6_gone", 120);
                    return true;
                }
                case "rt_t6_gone":
                {
                    bool gone = !map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).Any(t => t.Position == rtStairs);
                    bool kept = rtExit.GetTerrain(map) == rtExitTerrain && Floors.KindAt(rtExit, map) == FloorKind.NaturalRock && map.roofGrid.RoofAt(FloorRoof.Below(rtExit, map)) == rtExitRoof;
                    Result("T6 the stairs deconstructed: the lip top's natural floor and the roof under it stay", gone && kept,
                        $"stairs gone {gone}; exit {rtExit} {rtExit.GetTerrain(map).defName} ({Floors.KindAt(rtExit, map)}), roof under {map.roofGrid.RoofAt(FloorRoof.Below(rtExit, map))?.defName ?? "none"}");
                    RtSiteEnd(map, "T5-T7", "rt_t8");
                    return true;
                }

                // ------------------------------------------------------------ T8
                case "rt_t8":
                {
                    IntVec3 home = RtHome(map);
                    rtJumper = Colonists(map).FirstOrDefault(p => !p.Downed && p.health.capacities.CapableOf(PawnCapacityDefOf.Moving));
                    if (rtJumper == null)
                    {
                        Result("T8 a colonist", false, "none");
                        Finish();
                        return true;
                    }
                    Apparel pack = (Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Apparel_PackJump"));
                    rtJumper.apparel.Wear(pack, dropReplacedApparel: false);
                    rtVerb = pack.GetComp<CompApparelVerbOwner>().AllVerbs.First(v => v is Verb_Jump);
                    rtU = rtJumpTo = IntVec3.Invalid;
                    foreach (IntVec3 g in Near(home, 90, map))
                    {
                        if (!RtLip(g, map))
                        {
                            continue;
                        }
                        IntVec3 u = Levels.Cell(g, 1, map);
                        foreach (IntVec3 s in GenRadial.RadialCellsAround(g, 6f, false))
                        {
                            if (!s.InBounds(map) || s.z >= a.groundZ || TH(s, map) != 0 || !s.Standable(map) || s.Roofed(map) || map.fogGrid.IsFogged(s) || s.DistanceTo(g) < 3.5f)
                            {
                                continue;
                            }
                            Teleport(rtJumper, u);
                            if (rtVerb.ValidateTarget(s, false))
                            {
                                rtU = u;
                                rtJumpTo = s;
                                break;
                            }
                        }
                        if (rtU.IsValid)
                        {
                            break;
                        }
                    }
                    if (!rtU.IsValid)
                    {
                        Result("T8 a lip top with a ground cell in jump range", false, "none found");
                        Finish();
                        return true;
                    }
                    RtSiteStart(map, CellRect.CenteredOn(ProjectZ.GroundCellOf(rtU, map), 8));
                    // (placed on the lip top: the climb is T5's)
                    Teleport(rtJumper, rtU);
                    Draft(rtJumper, true);
                    rtVerb.OrderForceTarget(rtJumpTo);
                    Info($"T8 {rtJumper} on the lip top {rtU} jumps to {rtJumpTo}");
                    TickUntil(1200, () => rtJumper.Spawned && rtJumper.Position == rtJumpTo, "rt_t8_down", 5);
                    return true;
                }
                case "rt_t8_down":
                {
                    bool down = rtJumper.Spawned && rtJumper.Position == rtJumpTo && ProjectZ.FloorOf(rtJumper.Position, map) == 0;
                    Result("T8 a jump pack jump from a lip top down to the ground", down, $"{rtJumper} at {rtJumper.Position} (level {ProjectZ.FloorOf(rtJumper.Position, map)}), aimed {rtJumpTo}");
                    // (the pack holds 5 charges) back up
                    bool valid = rtVerb.ValidateTarget(rtU, false);
                    rtVerb.OrderForceTarget(rtU);
                    Info($"T8 back up to {rtU}: target valid {valid}");
                    TickUntil(1200, () => rtJumper.Spawned && rtJumper.Position.DistanceTo(rtU) <= 2.5f && ProjectZ.FloorOf(rtJumper.Position, map) == 1, "rt_t8_up", 5);
                    return true;
                }
                case "rt_t8_up":
                {
                    bool up = rtJumper.Spawned && ProjectZ.FloorOf(rtJumper.Position, map) == 1 && rtJumper.Position.DistanceTo(rtU) <= 2.5f;
                    Result("T8 a jump pack jump from the ground back onto the lip top", up, $"{rtJumper} at {rtJumper.Position} (level {ProjectZ.FloorOf(rtJumper.Position, map)}), aimed {rtU}");
                    RtSiteEnd(map, "T8", "rt_heat");
                    return true;
                }

                // ------------------------------------------------------------ heat (D8): only built floors pass it
                case "rt_heat":
                {
                    IntVec3 home = RtHome(map);
                    Pawn any = Colonists(map).FirstOrDefault();
                    rtL = rtSlabC = IntVec3.Invalid;
                    foreach (IntVec3 g in Near(home, 110, map))
                    {
                        // an inner lip cell (lip rock to the south too, rock east, west and north, a top over the north one): dug
                        // out it is a closed one-cell room, and its top a cell walls can close in on every side
                        bool rockAround = new[] { IntVec3.East, IntVec3.West, IntVec3.North }.All(d => (g + d).GetEdifice(map) is Mineable) && RtLip(g + IntVec3.South, map);
                        if (!RtLip(g, map) || !rockAround || !TmTop(Levels.Cell(g + IntVec3.East, 1, map), map) || !TmTop(Levels.Cell(g + IntVec3.West, 1, map), map)
                            || !TmTop(Levels.Cell(g + IntVec3.North, 1, map), map))
                        {
                            continue;
                        }
                        IntVec3 s = g + IntVec3.South * 2;
                        if (!s.Standable(map) || s.Roofed(map) || map.fogGrid.IsFogged(s) || any == null || !map.reachability.CanReach(any.Position, s, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors)))
                        {
                            continue;
                        }
                        // a 3x3 patch of open ground (open air above) 5-14 cells away, for the built-floor pair
                        IntVec3 slabC = Near(g, 14, map).FirstOrDefault(c => (c - g).LengthHorizontal >= 5f && CellRect.CenteredOn(c, 2).Cells.All(x => x.InBounds(map) && x.z < a.groundZ - 1
                            && x.Standable(map) && !x.Roofed(map) && x.GetEdifice(map) == null && !map.fogGrid.IsFogged(x) && Levels.Cell(x, 1, map).GetTerrain(map) == PZDefOf.PZ_OpenAir
                            && !x.GetThingList(map).Any(t => t is Pawn)));
                        if (slabC.IsValid && slabC != IntVec3.Zero)
                        {
                            rtL = g;
                            rtSlabC = slabC;
                            break;
                        }
                    }
                    if (!rtL.IsValid)
                    {
                        Result("heat: a lip and open ground beside it", false, "none found");
                        Finish();
                        return true;
                    }
                    CellRect site = CellRect.CenteredOn(rtL, 3).Encapsulate(CellRect.CenteredOn(rtSlabC, 3));
                    RtSiteStart(map, site);
                    map.designationManager.AddDesignation(new Designation(rtL, DesignationDefOf.Mine));
                    RtWork(map, WorkTypeDefOf.Mining);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    Info($"heat: lip {rtL} (roof {map.roofGrid.RoofAt(rtL)?.defName}) dug by colonists; built-floor pair at {rtSlabC}");
                    TickUntil(30000, () => rtL.GetEdifice(map) == null, "rt_heat_dug", 60);
                    return true;
                }
                case "rt_heat_dug":
                {
                    RtWork(map, null);
                    IntVec3 away = CellFinder.StandableCellNear(rtL + IntVec3.South * 12 + IntVec3.West * 12, map, 8f);
                    foreach (Pawn p in Colonists(map))
                    {
                        if (away.IsValid)
                        {
                            Teleport(p, away);
                        }
                    }
                    // (fixture) the lip top over the dug cell closed into a room (walls on the four tops round it, a roof); the
                    // built pair: a 3x3 house on the ground and one on a
                    // slab right over it, each with a one-cell room in the middle
                    IntVec3 u = Levels.Cell(rtL, 1, map);
                    RtWall(map, Levels.Cell(rtL + IntVec3.South, 1, map));
                    RtWall(map, Levels.Cell(rtL + IntVec3.West, 1, map));
                    RtWall(map, Levels.Cell(rtL + IntVec3.East, 1, map));
                    RtWall(map, Levels.Cell(rtL + IntVec3.North, 1, map));
                    map.roofGrid.SetRoof(u, RoofDefOf.RoofConstructed);
                    TerrainDef slab = DefDatabase<TerrainDef>.GetNamed("PZ_UpperFloor");
                    foreach (IntVec3 c in CellRect.CenteredOn(rtSlabC, 1))
                    {
                        map.terrainGrid.SetTerrain(Levels.Cell(c, 1, map), slab);
                    }
                    foreach (IntVec3 c in CellRect.CenteredOn(rtSlabC, 1).EdgeCells)
                    {
                        RtWall(map, c);
                        RtWall(map, Levels.Cell(c, 1, map));
                    }
                    map.roofGrid.SetRoof(rtSlabC, RoofDefOf.RoofConstructed);   // (becomes the slab's floor roof)
                    map.roofGrid.SetRoof(Levels.Cell(rtSlabC, 1, map), RoofDefOf.RoofConstructed);
                    TickUntil(240, () => false, "rt_heat_set", 60);
                    return true;
                }
                case "rt_heat_set":
                {
                    Room nb = rtL.GetRoom(map), nu = Levels.Cell(rtL, 1, map).GetRoom(map), sb = rtSlabC.GetRoom(map), su = Levels.Cell(rtSlabC, 1, map).GetRoom(map);
                    bool rooms = nb != null && nu != null && sb != null && su != null && nb != nu && sb != su && !nb.UsesOutdoorTemperature && !nu.UsesOutdoorTemperature
                        && !sb.UsesOutdoorTemperature && !su.UsesOutdoorTemperature;
                    Info($"heat rooms: dug {nb?.ID} ({nb?.CellCount} cells, roof {map.roofGrid.RoofAt(rtL)?.defName}), lip top {nu?.ID} ({nu?.CellCount}); built below {sb?.ID} ({sb?.CellCount}, roof {map.roofGrid.RoofAt(rtSlabC)?.defName}), above {su?.ID} ({su?.CellCount}); outdoor {map.mapTemperature.OutdoorTemp:0.0}");
                    if (!rooms)
                    {
                        Result("heat: four sealed indoor rooms", false, "a room is missing, shared or outdoors");
                        RtSiteEnd(map, "heat", null);
                        return true;
                    }
                    float t0 = map.mapTemperature.OutdoorTemp;
                    nb.Temperature = t0;
                    sb.Temperature = t0;
                    nu.Temperature = t0 + 40f;
                    su.Temperature = t0 + 40f;
                    rtHeatT0 = t0;
                    rtHeatNat = FloorHeat.Change(nb.TempTracker, nb).ToString("0.###") + "/" + FloorHeat.Change(nu.TempTracker, nu).ToString("0.###");
                    rtHeatSlab = FloorHeat.Change(sb.TempTracker, sb);
                    TickUntil(1200, () => false, "rt_heat_done", 60);
                    return true;
                }
                case "rt_heat_done":
                {
                    Room nb = rtL.GetRoom(map), nu = Levels.Cell(rtL, 1, map).GetRoom(map), sb = rtSlabC.GetRoom(map);
                    bool natZero = rtHeatNat == "0/0";
                    Result("heat: a dug room under a lip top and the room on the top pass no heat through the natural floor (no floor exchange, either side)",
                        natZero, $"floor exchange per interval (below/above) {rtHeatNat}; after 1200 ticks dug room {nb?.Temperature - rtHeatT0:+0.00;-0.00} C, top room {nu?.Temperature - rtHeatT0:+0.00;-0.00} C from outdoor");
                    float slabRise = (sb?.Temperature ?? rtHeatT0) - rtHeatT0, natRise = (nb?.Temperature ?? rtHeatT0) - rtHeatT0;
                    Result("heat: a built slab between two rooms still passes heat (the room under the warm room warms faster than the dug one)",
                        rtHeatSlab > 0f && slabRise > natRise + 0.5f, $"floor exchange of the room under the slab at the start {rtHeatSlab:+0.000;-0.000} C per interval; after 1200 ticks under the slab {slabRise:+0.00;-0.00} C, dug room {natRise:+0.00;-0.00} C");
                    RtSiteEnd(map, "heat", null);
                    return true;
                }
            }
            return false;
        }

        // ---- where compat G2 (gravity) and G3 (BandRadial) meet rock-top floors (merge of mountains into compat)
        private static BandRadial.Row rtHolderRow;
        private static int rtFell0, rtNoLanding0;

        // At T3's start (before the hall is mined): a natural floor cannot be taken up; a stack and an animal put on the
        // open air over a mountain top fall onto it (the top is a floor for the gravity rule; the animal bruised); BandRadial
        // counts the roof-holder check the mining runs.
        private static bool rtG2Probed;

        private static void RtG2Probes(Map map, IntVec3 home)
        {
            if (!rtG2Probed)
            {
                rtG2Probed = true;   // (once: T3 may pick another lip after a reload)
                RtG2Once(map, home);
            }
            rtHolderRow = BandRadial.Rows.FirstOrDefault(r => r.method == "ProcessRoofHolderDespawned");
            if (rtHolderRow != null)
            {
                rtHolderRow.calls = rtHolderRow.cells = rtHolderRow.folded = rtHolderRow.skipped = 0;
            }
            BandRadial.Counting = true;
            rtFell0 = Gravity.Fell;
            rtNoLanding0 = Gravity.NoLanding;
        }

        private static void RtG2Once(Map map, IntVec3 home)
        {
            AcceptanceReport rfd = new Designator_RemoveFoundation().CanDesignateCell(rtU);
            AcceptanceReport rfl = new Designator_RemoveFloor().CanDesignateCell(rtU);
            Result("G2 x rock-top: a natural floor cannot be taken up (remove foundation and remove floor refused, not a slab)",
                !rfd.Accepted && !rfl.Accepted && !Slab.At(rtU, map) && Floors.KindAt(rtU, map) == FloorKind.NaturalRock,
                $"lip top {rtU} {rtU.GetTerrain(map).defName}: remove foundation {rfd.Accepted} ({rfd.Reason}), remove floor {rfl.Accepted} ({rfl.Reason}), slab {Slab.At(rtU, map)}");
            IntVec3 top = IntVec3.Invalid, air = IntVec3.Invalid;
            foreach (IntVec3 g in Near(home, 110, map))
            {
                if ((g - rtL).LengthHorizontal < 14f || TH(g, map) != 1)
                {
                    continue;
                }
                IntVec3 t = Levels.Cell(g, 1, map), u = Levels.Cell(g, 2, map);
                IntVec3 t2 = Levels.Cell(g + IntVec3.East, 1, map), u2 = Levels.Cell(g + IntVec3.East, 2, map);
                if (u.IsValid && u2.IsValid && TmTop(t, map) && TmTop(t2, map) && t.Standable(map) && t2.Standable(map) && Gravity.Air(u, map) && Gravity.Air(u2, map)
                    && !map.roofGrid.Roofed(t) && !map.roofGrid.Roofed(t2) && !t.GetThingList(map).Any(x => x.def.category == ThingCategory.Item || x is Pawn)
                    && !t2.GetThingList(map).Any(x => x.def.category == ThingCategory.Item || x is Pawn))
                {
                    top = t;
                    air = u;
                    break;
                }
            }
            if (!air.IsValid)
            {
                Result("G2 x rock-top: open air over a mountain top", false, "none found");
            }
            else
            {
                int fell0 = Gravity.Fell, bruised0 = Gravity.PawnsBruised, broken0 = Gravity.RoofsBroken;
                Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                steel.stackCount = 7;
                GenPlace.TryPlaceThing(steel, air, map, ThingPlaceMode.Near);
                IntVec3 steelAt = steel.Spawned ? steel.Position : IntVec3.Invalid;
                Rand.PushState(4711);
                Pawn animal;
                try
                {
                    animal = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Muffalo"), null, forceGenerateNewPawn: true));
                }
                finally
                {
                    Rand.PopState();
                }
                rtAnimalHurt0 = animal.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);
                GenSpawn.Spawn(animal, air + IntVec3.East, map);
                IntVec3 animalAt = animal.Spawned ? animal.Position : IntVec3.Invalid;
                bool steelOk = steelAt.IsValid && ProjectZ.FloorOf(steelAt, map) == 1 && Floors.KindAt(steelAt, map) == FloorKind.NaturalRock;
                bool animalOk = animalAt.IsValid && ProjectZ.FloorOf(animalAt, map) == 1 && Floors.KindAt(animalAt, map) == FloorKind.NaturalRock;
                Result("G2 x rock-top: steel and an animal put on the open air over a mountain top fall onto the top (one floor: the animal not bruised; no roof broken)",
                    steelOk && animalOk && Gravity.Fell >= fell0 + 2 && Gravity.PawnsBruised == bruised0 && Gravity.RoofsBroken == broken0,
                    $"air {air} over top {top}: steel at {steelAt} (level {(steelAt.IsValid ? ProjectZ.FloorOf(steelAt, map) : -9)}), animal at {animalAt} (level {(animalAt.IsValid ? ProjectZ.FloorOf(animalAt, map) : -9)}); fell +{Gravity.Fell - fell0}, bruised +{Gravity.PawnsBruised - bruised0}, roofs broken +{Gravity.RoofsBroken - broken0}");
                if (steel.Spawned)
                {
                    steel.Destroy();
                }
                // (the bruises come on the map's next tick, JumpGrid: checked once the hall is mined, RtG2AnimalCheck)
                rtAnimal = animal.Spawned ? animal : null;
            }
        }

        private static Pawn rtAnimal;
        private static int rtAnimalHurt0;

        // (always true: it rides in T3's tick condition and checks once, on its first test, 60 ticks after the fall)
        private static bool RtG2AnimalCheck()
        {
            if (rtAnimal == null)
            {
                return true;
            }
            int hurt = rtAnimal.health.hediffSet.hediffs.Count(h => h is Hediff_Injury) - rtAnimalHurt0;
            Result("G2 x rock-top: the animal that fell one floor onto the top is unhurt", hurt == 0 && !rtAnimal.Dead,
                $"{rtAnimal}: new injuries {hurt} ({string.Join(",", rtAnimal.health.hediffSet.hediffs.OfType<Hediff_Injury>().Select(h => h.def.defName))}), dead {rtAnimal.Dead}");
            if (rtAnimal.Spawned)
            {
                rtAnimal.Destroy();
            }
            rtAnimal = null;
            return true;
        }

        // At T3's fall: the lip's floor fell on the one collapse path (SlabCollapse under Gravity.Suspend, landing by
        // Column.Landing); the open air it left is air for the gravity rule (a stack put there falls into the hall); the
        // mining's roof-holder checks ran band-clipped (BandRadial on a mountain map).
        private static void RtG2AfterFall(Map map, bool fell)
        {
            BandRadial.Counting = false;
            BandRadial.Row row = rtHolderRow;
            Result("G3 x rock-top: BandRadial on a mountain map: the mined hall's roof-holder checks (RoofCollapseCellsFinder) ran through the band clip",
                row != null && row.target != null && row.swaps == row.expected && row.calls > 0 && row.cells > 0,
                row == null ? "no row" : $"{row.Name}: patched {row.target != null}, swaps {row.swaps}/{row.expected}, calls {row.calls}, cells {row.cells}, folded to the void wall {row.folded}, skipped {row.skipped}");
            if (!fell)
            {
                return;
            }
            IntVec3 land = Column.Landing(rtU, map, true, false, out int fallen, out _);
            Thing body = rtRider.Spawned ? (Thing)rtRider : rtRider.Corpse != null && rtRider.Corpse.Spawned ? rtRider.Corpse : null;   // (crushed in the collapse: its corpse)
            bool riderAtLanding = body != null && land.IsValid && body.Position.DistanceTo(land) <= 1.5f;
            Result("G2 x rock-top: the lip's floor fell on the slab-collapse path: the colonist landed where Column.Landing puts things from the lip top",
                riderAtLanding && fallen == 1, $"landing {land} (fallen {fallen}), {rtRider}{(rtRider.Dead ? " (dead)" : "")} at {(body != null ? body.Position.ToString() : "-")}; gravity hooks during the fall: fell +{Gravity.Fell - rtFell0}, no landing +{Gravity.NoLanding - rtNoLanding0}");
            int fell0 = Gravity.Fell;
            Thing probe = ThingMaker.MakeThing(ThingDefOf.Steel);
            probe.stackCount = 3;
            GenPlace.TryPlaceThing(probe, rtU, map, ThingPlaceMode.Near);
            IntVec3 at = probe.Spawned ? probe.Position : IntVec3.Invalid;
            Result("G2 x rock-top: the open air left by the fallen lip is air for the gravity rule (steel put there falls into the hall below)",
                at.IsValid && ProjectZ.FloorOf(at, map) == 0 && Gravity.Fell > fell0,
                $"steel put on {rtU} ({rtU.GetTerrain(map).defName}) is at {at} (level {(at.IsValid ? ProjectZ.FloorOf(at, map) : -9)}); fell +{Gravity.Fell - fell0}");
            if (probe.Spawned)
            {
                probe.Destroy();
            }
        }
    }
}
