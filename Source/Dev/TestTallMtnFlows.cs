using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ProjectZ.Combat;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode tallmtn, second part (real flows on a generated tall-mountain map; -projectztmnoflows skips them):
    //   F1 colonists build stairs on the ground against the mountain, the exit on (or over the lip next to) a level-1
    //      top, and a colonist walks up onto the top;
    //   F2 colonists build a wall and a bed on the top;
    //   F3 a colonist mines a level-1 cliff face, then the cell behind it: rock gone, the rough-hewn floor counts as a
    //      natural rock floor (R40), the thick roof above stays, the face behind is revealed as vanilla;
    //   F4 vanilla collapse under a rock floor: a 15x15 block of band rock taken away, CollapsedRocks fall, the thick
    //      roof and the floor above stay;
    //   F5 a colonist on a level-2 top ordered down to the level-1 top below jumps off the cliff (no stairs there);
    //   F6 a jump pack jump from the ground onto a level-1 top; a rock cell is refused;
    //   F7 sight never through rock (every sampled line through a rock column is blocked), a grenade-size explosion at
    //      the foot of a cliff leaves an animal behind the rim unhurt, a mortar shell aimed at mountain ground explodes
    //      on the top (Column.Top);
    //   F8 drops (Q6, D1): the game's raid centre and random drop finders never give a natural top, a real centre-drop
    //      raid with colonists on a top lands off the tops, cargo pods too; a drop the player aims at a top lands there;
    //   F9 Anomaly large structures ground only (D2);
    //   F10 tall things (Q7): allowed in a dug ground cell under solid rock, refused under a top and under a dug cell;
    //   F11 infestations per band: a dug level-1 room next to a colony building on that band gets candidates, all
    //      within reach of a band-1 building; a child hive never lands on another band (R23);
    //   F12 colonists put on a level-1 top with no way down (as by a transport pod) build stairs downward from the top
    //      (the stairs are touched from the floor above, StairTouch) and walk down them to the ground. A fresh, rested
    //      crew with no hostiles or shells left over: F7's explosions and F8's raid can leave the old colonists hurt.
    public static partial class TestRunner
    {
        private static IntVec3 tfStairs, tfExitTop, tfWalkTo, tfWallAt, tfBedAt, tfMine1, tfMine2, tfJumpFrom, tfJumpTo, tfPackTo;
        private static Rot4 tfRot;
        private static Pawn tfWalker, tfJumper, tfPacker;
        private static int tfJumpsBefore;
        private static Verb tfPackVerb;
        private static ThingDef tfRockDef;

        private static int TH(IntVec3 g, Map map) => Column.NaturalTop(g, map);

        // Ground cells within r of home (any radius; GenRadial stops at ~80), nearest first.
        private static IEnumerable<IntVec3> Near(IntVec3 home, int r, Map map)
        {
            int gz = ProjectZ.GroundZ(map);
            List<IntVec3> l = new List<IntVec3>();
            for (int z = Mathf.Max(0, home.z - r); z <= Mathf.Min(gz - 1, home.z + r); z++)
            {
                for (int x = Mathf.Max(0, home.x - r); x <= Mathf.Min(map.Size.x - 1, home.x + r); x++)
                {
                    l.Add(new IntVec3(x, 0, z));
                }
            }
            return l.OrderBy(c => (c - home).LengthHorizontalSquared);
        }

        private static bool TmTop(IntVec3 c, Map map) => ProjectZ.FloorOf(c, map) >= 1 && Floors.KindAt(c, map) == FloorKind.NaturalRock && c.GetEdifice(map) == null;

        private static IntVec3 Centroid(Map map)
        {
            List<Pawn> cols = Colonists(map);
            // (over the ground: colonists may stand upstairs)
            return cols.Count == 0 ? new IntVec3(map.Size.x / 2, 0, ProjectZ.GroundZ(map) / 2) : new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => Levels.GroundZLoose(map, p.Position.z)));
        }

        // Every colonist injury, once, with the tick it came, the flow step whose ticks it fell in, its source and the
        // colonist's place: what hurts the colonists, and when.
        private static readonly HashSet<Hediff> tfHurtSeen = new HashSet<Hediff>();
        private static readonly List<KeyValuePair<int, string>> tfStepTicks = new List<KeyValuePair<int, string>>();

        private static void TfHurtLog(Map map)
        {
            bool first = tfStepTicks.Count == 0;
            tfStepTicks.Add(new KeyValuePair<int, string>(Find.TickManager.TicksGame, stage));
            foreach (Pawn p in map.mapPawns.AllPawns.Where(x => x.IsColonist))
            {
                foreach (Hediff_Injury h in p.health.hediffSet.hediffs.OfType<Hediff_Injury>())
                {
                    if (!tfHurtSeen.Add(h) || first)
                    {
                        continue;
                    }
                    KeyValuePair<int, string> st = tfStepTicks.LastOrDefault(e => e.Key <= h.tickAdded);
                    string during = $"{st.Value ?? "?"} (from tick {st.Key})";
                    Info($"HURT {p} {h.def.defName} {h.Severity:0.0} on {h.Part?.Label ?? "body"} at tick {h.tickAdded} during step {during} (source {h.sourceDef?.defName ?? h.sourceLabel ?? "none"}); seen at step {stage}, {p} at {(p.Spawned ? p.Position.ToString() : "unspawned")} level {(p.Spawned ? ProjectZ.FloorOf(p.Position, map) : -9)}{(p.Downed ? " downed" : "")}{(p.Dead ? " dead" : "")}");
                }
            }
        }

        private static bool StepTallMtnFlows(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            TfHurtLog(map);
            switch (stage)
            {
                case "tm_f_setup":
                {
                    DebugSettings.enableRandomMentalStates = false;
                    Find.PlaySettings.autoRebuild = false;
                    if (!TfFindStairs(map, a))
                    {
                        Result("F1 a stairs site against the mountain", false, "none found near the colonists");
                        stage = "tm_f_drops";
                        return true;
                    }
                    for (int i = 0; i < 4; i++)
                    {
                        Thing w = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                        w.stackCount = 75;
                        GenPlace.TryPlaceThing(w, tfStairs + tfRot.Opposite.FacingCell * 3, map, ThingPlaceMode.Near);
                    }
                    AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, tfStairs, tfRot, map, stuffDef: ThingDefOf.WoodLog);
                    GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, tfStairs, map, tfRot, Faction.OfPlayer, ThingDefOf.WoodLog);
                    Info($"F1 stairs at {tfStairs} facing {tfRot}, exit {StairShape.Exit(tfStairs, tfRot, map)} ({Floors.KindAt(StairShape.Exit(tfStairs, tfRot, map), map)}), top to walk to {tfWalkTo}; placement '{(ok.Accepted ? "ok" : ok.Reason)}'");
                    SetWork(map, true);
                    foreach (Pawn p in Colonists(map))
                    {
                        if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                        {
                            p.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2);
                        }
                    }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(40000, () => TfStairsBuilt(map), "tm_f_built", 120);
                    return true;
                }
                case "tm_f_built":
                {
                    bool built = TfStairsBuilt(map);
                    Result("F1 colonists built stairs against the mountain (exit on or next to a level-1 top)", built, $"stairs at {tfStairs}: {built}");
                    tfWalker = Colonists(map).FirstOrDefault(p => !p.Downed && p.health.capacities.CapableOf(PawnCapacityDefOf.Moving));
                    if (!built || tfWalker == null)
                    {
                        stage = "tm_f_mine";
                        return true;
                    }
                    SetWork(map, false);
                    tfWalker.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, tfWalkTo), JobTag.Misc);
                    TickUntil(6000, () => tfWalker.Position == tfWalkTo, "tm_f_walked", 30);
                    return true;
                }
                case "tm_f_walked":
                {
                    Result("F1 a colonist walked up the stairs onto the mountain top", tfWalker.Position == tfWalkTo && TmTop(tfWalker.Position, map),
                        $"{tfWalker} at {tfWalker.Position} (level {ProjectZ.FloorOf(tfWalker.Position, map)}, {Floors.KindAt(tfWalker.Position, map)}), target {tfWalkTo}");
                    // F2: a wall and a bed on the top
                    if (!TfWallBed(map, tfWalkTo, tfExitTop, out tfWallAt, out tfBedAt))
                    {
                        Result("F2 places for a wall and a bed on the top", false, $"wall {tfWallAt}, bed {tfBedAt}");
                        stage = "tm_f_mine";
                        return true;
                    }
                    GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, tfWallAt, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
                    GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Bed, tfBedAt, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
                    SetWork(map, true);
                    foreach (Pawn p in Colonists(map))
                    {
                        if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                        {
                            p.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2);
                        }
                    }
                    TickUntil(40000, () => tfWallAt.GetEdifice(map)?.def == ThingDefOf.Wall && map.listerThings.ThingsOfDef(ThingDefOf.Bed).Any(b => b.Position == tfBedAt), "tm_f_built2", 120);
                    return true;
                }
                case "tm_f_built2":
                {
                    bool wall = tfWallAt.GetEdifice(map)?.def == ThingDefOf.Wall, bed = map.listerThings.ThingsOfDef(ThingDefOf.Bed).Any(b => b.Position == tfBedAt);
                    Result("F2 colonists built a wall and a bed on the mountain top", wall && bed, $"wall at {tfWallAt} {wall}, bed at {tfBedAt} {bed}");
                    stage = "tm_f_mine";
                    return true;
                }
                case "tm_f_mine":
                {
                    // F3: a revealed level-1 face reachable from the top the walker stands on (or any reachable one)
                    tfMine1 = tfMine2 = IntVec3.Invalid;
                    IntVec3 from = tfWalkTo.IsValid ? tfWalkTo : tfWalker != null && tfWalker.Spawned ? tfWalker.Position : Centroid(map);
                    foreach (IntVec3 c in GenRadial.RadialCellsAround(from, 20f, true))
                    {
                        if (!c.InBounds(map) || ProjectZ.FloorOf(c, map) < 1 || map.fogGrid.IsFogged(c) || !(c.GetEdifice(map) is Mineable m) || m.def.building.isResourceRock)
                        {
                            continue;
                        }
                        foreach (IntVec3 d in GenAdj.CardinalDirections)
                        {
                            IntVec3 stand = c - d, behind = c + d;
                            if (stand.InBounds(map) && behind.InBounds(map) && ProjectZ.SameBand(stand, c, map) && ProjectZ.SameBand(behind, c, map) && TmTop(stand, map)
                                && behind.GetEdifice(map) is Mineable m2 && !m2.def.building.isResourceRock && map.fogGrid.IsFogged(behind)
                                && map.reachability.CanReach(from, stand, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors)))
                            {
                                tfMine1 = c;
                                tfMine2 = behind;
                                break;
                            }
                        }
                        if (tfMine1.IsValid)
                        {
                            break;
                        }
                    }
                    if (!tfMine1.IsValid)
                    {
                        Result("F3 a level-1 cliff face to mine", false, $"none reachable from {from}");
                        stage = "tm_f_collapse";
                        return true;
                    }
                    tfRockDef = tfMine1.GetEdifice(map).def;
                    map.designationManager.AddDesignation(new Designation(tfMine1, DesignationDefOf.Mine));
                    SetWork(map, false);
                    foreach (Pawn p in Colonists(map))
                    {
                        if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Mining))
                        {
                            p.workSettings.SetPriority(WorkTypeDefOf.Mining, 1);
                        }
                    }
                    Info($"F3 mining {tfMine1} ({tfRockDef.defName}, level {ProjectZ.FloorOf(tfMine1, map)}), behind it {tfMine2} fogged {map.fogGrid.IsFogged(tfMine2)}");
                    TickUntil(20000, () => tfMine1.GetEdifice(map) == null, "tm_f_mined", 60);
                    return true;
                }
                case "tm_f_mined":
                {
                    bool gone = tfMine1.GetEdifice(map) == null;
                    TerrainDef t = tfMine1.GetTerrain(map);
                    RoofDef r = map.roofGrid.RoofAt(tfMine1);
                    bool revealed = !map.fogGrid.IsFogged(tfMine2);
                    int chunks = GenRadial.RadialDistinctThingsAround(tfMine1, map, 2f, true).Count(x => x.def.thingCategories?.Contains(ThingCategoryDefOf.StoneChunks) ?? false);
                    Result("F3 a colonist mined a level-1 cliff face: rock gone, rough-hewn floor is a natural rock floor (R40), thick roof above kept, the rock behind revealed",
                        gone && t == tfRockDef.building.leaveTerrain && Floors.KindAt(tfMine1, map) == FloorKind.NaturalRock && r == RoofDefOf.RoofRockThick && revealed,
                        $"{tfMine1}: edifice gone {gone}, terrain {t?.defName} (leave {tfRockDef.building.leaveTerrain?.defName}), kind {Floors.KindAt(tfMine1, map)}, roof {r?.defName}, behind {tfMine2} revealed {revealed}; chunks near {chunks}");
                    // R40: the mined cell is a natural floor for stairs landing and for drops
                    Result("R40 the mined cell counts as natural: stairs may land there, its roof is not kept like a slab's", SlabSupport.LandingBlocked(tfMine1, map) == null && !FloorRoof.Kept(FloorRoof.Below(tfMine1, map), map),
                        $"landing {SlabSupport.LandingBlocked(tfMine1, map) ?? "allowed"}, kept below {FloorRoof.Kept(FloorRoof.Below(tfMine1, map), map)}");
                    map.designationManager.AddDesignation(new Designation(tfMine2, DesignationDefOf.Mine));
                    TickUntil(20000, () => tfMine2.GetEdifice(map) == null, "tm_f_mined2", 60);
                    return true;
                }
                case "tm_f_mined2":
                    Result("F3 the cell behind (revealed by the first) mined too: a tunnel into the level-1 rock", tfMine2.GetEdifice(map) == null && map.roofGrid.RoofAt(tfMine2) == RoofDefOf.RoofRockThick,
                        $"{tfMine2}: edifice {tfMine2.GetEdifice(map)?.def.defName ?? "none"}, roof {map.roofGrid.RoofAt(tfMine2)?.defName}");
                    stage = "tm_f_collapse";
                    return true;
                case "tm_f_collapse":
                    TfCollapse(map, a);
                    return true;
                case "tm_f_collapsed":
                    TfCollapseCheck(map);
                    stage = "tm_f_jump";
                    return true;
                case "tm_f_jump":
                    TfJump(map, a);
                    return true;
                case "tm_f_jumped":
                {
                    bool ok = tfJumper.Spawned && tfJumper.Position == tfJumpTo;
                    Result("F5 a colonist on a level-2 top ordered to the level-1 top below jumps off the cliff (no stairs there)", ok && PZStats.jumps > tfJumpsBefore,
                        $"{tfJumper} at {tfJumper.Position} (level {ProjectZ.FloorOf(tfJumper.Position, map)}), target {tfJumpTo}; jumps {PZStats.jumps - tfJumpsBefore}");
                    stage = "tm_f_pack";
                    return true;
                }
                case "tm_f_pack":
                    TfPack(map, a);
                    return true;
                case "tm_f_packed":
                {
                    bool ok = tfPacker.Spawned && ProjectZ.FloorOf(tfPacker.Position, map) == 1 && tfPacker.Position.DistanceTo(tfPackTo) <= 2.5f;
                    Result("F6 a jump pack jump from the ground lands on the level-1 top", ok, $"{tfPacker} at {tfPacker.Position} (level {ProjectZ.FloorOf(tfPacker.Position, map)}), aimed {tfPackTo}");
                    stage = "tm_f_sight";
                    return true;
                }
                case "tm_f_sight":
                    TfSight(map, a);
                    TfBlast(map, a);
                    return true;
                case "tm_f_blasted":
                    TfBlastCheck(map);
                    TfMortar(map, a);
                    return true;
                case "tm_f_mortar":
                {
                    tickWatch = null;
                    Result("F7 a mortar shell aimed at mountain ground explodes on the mountain top (Column.Top)", lastExplosion.IsValid && lastExplosion == tfMortarTop,
                        $"aimed {tfMortarG} (top level {TH(tfMortarG, map)}), exploded at {lastExplosion} (level {(lastExplosion.IsValid ? ProjectZ.FloorOf(lastExplosion, map) : -9)}) at tick {Find.TickManager.TicksGame}, top {tfMortarTop}; colonists near it: {string.Join(", ", Colonists(map).Where(p => lastExplosion.IsValid && Range3D.Distance(lastExplosion, p.Position, map) < 6f).Select(p => $"{p} at {p.Position} 3D {Range3D.Distance(lastExplosion, p.Position, map):0.0}"))}");
                    stage = "tm_f_drops";
                    return true;
                }
                case "tm_f_drops":
                    TfDrops(map, a);
                    return true;
                case "tm_f_dropped":
                    TfDropsCheck(map);
                    stage = "tm_f_tall";
                    return true;
                case "tm_f_tall":
                    TfLarge(map);
                    TfTall(map, a);
                    TfInfest(map, a);
                    stage = "tm_f_down";
                    return true;
                case "tm_f_down":
                    TfDown(map, a);
                    return true;
                case "tm_f_down_built":
                {
                    bool built = tfDownStairs != null && TfDownBuilt(map);
                    Result("F12 colonists on a level-1 top with no way down build stairs downward from the top (touch from above)", built,
                        $"stairs at {tfDownAt} facing {tfDownRot}: {built}; blueprint/frame {TfDownThing(map)}; builders {string.Join(", ", tfDownCrew.Select(p => $"{p} at {p.Position} job {p.CurJobDef?.defName} hediffs {string.Join("/", p.health.hediffSet.hediffs.Select(h => h.def.defName))}"))}; hostiles on the map {map.mapPawns.AllPawnsSpawned.Count(h => h.HostileTo(Faction.OfPlayer) && !h.Downed)}");
                    if (!built)
                    {
                        TfDownEnd(map);
                        return true;
                    }
                    tfDownJumps = PZStats.jumps;
                    tfDownCrew[0].jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, tfDownFoot), JobTag.Misc);
                    TickUntil(6000, () => tfDownCrew[0].Position == tfDownFoot, "tm_f_down_walked", 30);
                    return true;
                }
                case "tm_f_down_walked":
                {
                    Pawn w = tfDownCrew[0];
                    Result("F12 a colonist walks down the new stairs from the top to the ground", w.Position == tfDownFoot && ProjectZ.FloorOf(w.Position, map) == 0 && PZStats.jumps == tfDownJumps,
                        $"{w} at {w.Position} (level {ProjectZ.FloorOf(w.Position, map)}), target {tfDownFoot}; jumps {PZStats.jumps - tfDownJumps}");
                    TfDownEnd(map);
                    return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- F1

        // F2's places on the top the walker reaches: the bed first (it needs two cells), then a wall clear of it; both off
        // the way in from the stairs exit.
        private static bool TfWallBed(Map map, IntVec3 walk, IntVec3 exit, out IntVec3 wall, out IntVec3 bed)
        {
            wall = bed = IntVec3.Invalid;
            TraverseParms tp = TraverseParms.For(TraverseMode.PassDoors);
            List<IntVec3> cells = GenRadial.RadialCellsAround(walk, 14f, false).Where(c => c.InBounds(map) && TmTop(c, map) && c.DistanceTo(walk) >= 2f
                && (!exit.IsValid || c.DistanceTo(exit) >= 2.5f) && map.reachability.CanReach(walk, c, PathEndMode.Touch, tp)).ToList();
            foreach (IntVec3 c in cells)
            {
                if (GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Bed, c, Rot4.North, map, stuffDef: ThingDefOf.WoodLog).Accepted)
                {
                    bed = c;
                    break;
                }
            }
            if (!bed.IsValid)
            {
                return false;
            }
            CellRect bedRect = GenAdj.OccupiedRect(bed, Rot4.North, ThingDefOf.Bed.size);
            foreach (IntVec3 c in cells)
            {
                if (!bedRect.ExpandedBy(1).Contains(c) && GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, c, Rot4.North, map, stuffDef: ThingDefOf.WoodLog).Accepted)
                {
                    wall = c;
                    break;
                }
            }
            return wall.IsValid;
        }

        private static bool TfStairsBuilt(Map map) => map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).Any(t => t.Position == tfStairs);

        private static bool TfFindStairs(Map map, AnnexMapComponent a)
        {
            IntVec3 home = Centroid(map);
            Pawn any = Colonists(map).FirstOrDefault();
            int best = int.MaxValue;
            tfStairs = IntVec3.Invalid;
            foreach (IntVec3 p in GenRadial.RadialCellsAround(home, 50f, true))
            {
                if (!p.InBounds(map) || p.z >= a.groundZ - 2 || p.z < 2)
                {
                    continue;
                }
                for (int r = 0; r < 4; r++)
                {
                    Rot4 rot = new Rot4(r);
                    IntVec3 exit = StairShape.Exit(p, rot, map);
                    if (!exit.IsValid)
                    {
                        continue;
                    }
                    IntVec3 eg = ProjectZ.GroundCellOf(exit, map);
                    int he = TH(eg, map);
                    IntVec3 walk = IntVec3.Invalid;
                    int score;
                    if (he == 1)
                    {
                        walk = exit;
                        score = 0;
                    }
                    else if (he == 0 && eg.GetEdifice(map) is Building lip && lip.def.building.isNaturalRock)
                    {
                        IntVec3 next = exit + rot.FacingCell;
                        if (!next.InBounds(map) || !TmTop(next, map) || TH(ProjectZ.GroundCellOf(next, map), map) != 1)
                        {
                            continue;
                        }
                        walk = next;
                        score = 30;
                    }
                    else
                    {
                        continue;
                    }
                    IntVec3 inner = walk + rot.FacingCell * 2;
                    if (inner.InBounds(map) && TmTop(inner, map) && ProjectZ.SameBand(inner, walk, map))
                    {
                        walk = inner;
                    }
                    score += (p - home).LengthManhattan;
                    if (score >= best || map.fogGrid.IsFogged(p) || any != null && !map.reachability.CanReach(any.Position, p, PathEndMode.Touch, TraverseParms.For(TraverseMode.PassDoors)))
                    {
                        continue;
                    }
                    if (StairShape.Footprint(p, rot).Cells.Any(c => !c.InBounds(map) || map.fogGrid.IsFogged(c) || c.Roofed(map) || c.GetEdifice(map) != null || !c.Standable(map)))
                    {
                        continue;
                    }
                    if (!GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, p, rot, map, stuffDef: ThingDefOf.WoodLog).Accepted)
                    {
                        continue;
                    }
                    if (!TfWallBed(map, walk, exit, out _, out _))
                    {
                        continue;   // (a top too small for F2's wall and bed)
                    }
                    best = score;
                    tfStairs = p;
                    tfRot = rot;
                    tfWalkTo = walk;
                    tfExitTop = exit;
                }
            }
            return tfStairs.IsValid;
        }

        // ---------------------------------------------------------------- F4

        private static IntVec3 tfCollapseC;

        private static void TfCollapse(Map map, AnnexMapComponent a)
        {
            // a level-1 block under a level-3 plateau (H >= 3 over 17x17), away from everything
            tfCollapseC = IntVec3.Invalid;
            int gz = a.groundZ;
            for (int z = 12; z < gz - 12 && !tfCollapseC.IsValid; z += 3)
            {
                for (int x = 12; x < map.Size.x - 12 && !tfCollapseC.IsValid; x += 3)
                {
                    IntVec3 g = new IntVec3(x, 0, z);
                    bool all = true;
                    foreach (IntVec3 c in CellRect.CenteredOn(g, 8))
                    {
                        if (TH(c, map) < 3)
                        {
                            all = false;
                            break;
                        }
                    }
                    if (all && (tfWalkTo.IsValid ? (ProjectZ.GroundCellOf(tfWalkTo, map) - g).LengthHorizontal > 25 : true))
                    {
                        tfCollapseC = g;
                    }
                }
            }
            if (!tfCollapseC.IsValid || a.MaxLevel < 3)
            {
                Info("F4 collapse: no 17x17 area of level-3 tops (needs 4 floors), skipped");
                stage = "tm_f_jump";
                return;
            }
            // the block taken away (the game's own despawn: the roof holders go, its collapse check runs), the way
            // mining it would; a 15x15 hall under a 17x17 plateau
            foreach (IntVec3 g in CellRect.CenteredOn(tfCollapseC, 7))
            {
                IntVec3 c = Levels.Cell(g, 1, map);
                c.GetEdifice(map)?.Destroy(DestroyMode.Vanish);
            }
            Info($"F4 collapse: level-1 block 15x15 round {Levels.Cell(tfCollapseC, 1, map)} taken away; roof holder check pending {map.roofCollapseBuffer.CellsMarkedToCollapse.Count}");
            TickUntil(600, () => false, "tm_f_collapsed", 60);
        }

        private static void TfCollapseCheck(Map map)
        {
            IntVec3 c = Levels.Cell(tfCollapseC, 1, map);
            bool rocks = c.GetThingList(map).Any(t => t.def == ThingDefOf.CollapsedRocks);
            int rockCells = CellRect.CenteredOn(tfCollapseC, 7).Cells.Count(g => Levels.Cell(g, 1, map).GetThingList(map).Any(t => t.def == ThingDefOf.CollapsedRocks));
            RoofDef r = map.roofGrid.RoofAt(c);
            IntVec3 up = Levels.Cell(tfCollapseC, 2, map);
            Result("F4 vanilla collapse under a rock floor: collapsed rocks fill the middle of the hall, the thick roof stays, the rock floor above stays",
                rocks && r == RoofDefOf.RoofRockThick && Floors.KindAt(up, map) == FloorKind.NaturalRock,
                $"centre {c}: collapsed rocks {rocks} ({rockCells} of 225 cells), roof {r?.defName}, above {up} {Floors.KindAt(up, map)}");
        }

        // ---------------------------------------------------------------- F5

        private static void TfJump(Map map, AnnexMapComponent a)
        {
            tfJumpFrom = tfJumpTo = IntVec3.Invalid;
            IntVec3 home = tfWalkTo.IsValid ? ProjectZ.GroundCellOf(tfWalkTo, map) : Centroid(map);
            foreach (IntVec3 g in Near(home, 60, map))
            {
                if (!g.InBounds(map) || g.z >= a.groundZ || TH(g, map) != 2)
                {
                    continue;
                }
                IntVec3 from = Levels.Cell(g, 2, map);
                if (map.fogGrid.IsFogged(from) || !from.Standable(map))
                {
                    continue;
                }
                foreach (IntVec3 d in GenAdj.CardinalDirections)
                {
                    IntVec3 ng = g + d;
                    if (!ng.InBounds(map) || ng.z >= a.groundZ || TH(ng, map) != 1)
                    {
                        continue;
                    }
                    IntVec3 to = Levels.Cell(ng, 1, map);
                    if (!to.Standable(map) || map.fogGrid.IsFogged(to) || !JumpGrid.LandingFrom(from, d, map, out IntVec3 land, out _) || land != to)
                    {
                        continue;
                    }
                    tfJumpFrom = from;
                    tfJumpTo = to;
                    break;
                }
                if (tfJumpFrom.IsValid)
                {
                    break;
                }
            }
            tfJumper = Colonists(map).FirstOrDefault(p => p != tfWalker && !p.Downed && p.health.capacities.CapableOf(PawnCapacityDefOf.Moving)) ?? tfWalker;
            if (!tfJumpFrom.IsValid || tfJumper == null)
            {
                Result("F5 a level-2 cliff over a level-1 top", false, $"from {tfJumpFrom}, jumper {tfJumper}");
                stage = "tm_f_pack";
                return;
            }
            // (placed up there: the climb itself is F1's)
            Teleport(tfJumper, tfJumpFrom);
            SetWork(map, false);
            tfJumpsBefore = PZStats.jumps;
            tfJumper.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, tfJumpTo), JobTag.Misc);
            Info($"F5 {tfJumper} on {tfJumpFrom} (level 2) ordered to {tfJumpTo} (level 1)");
            TickUntil(3000, () => tfJumper.Position == tfJumpTo, "tm_f_jumped", 15);
        }

        // ---------------------------------------------------------------- F6

        private static void TfPack(Map map, AnnexMapComponent a)
        {
            tfPacker = Colonists(map).FirstOrDefault(p => p != tfWalker && p != tfJumper && !p.Downed) ?? Colonists(map).FirstOrDefault(p => p != tfJumper && !p.Downed);
            if (tfPacker == null)
            {
                Result("F6 a colonist for the jump pack", false, "none");
                stage = "tm_f_sight";
                return;
            }
            Apparel pack = (Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Apparel_PackJump"));
            tfPacker.apparel.Wear(pack, dropReplacedApparel: false);
            tfPackVerb = pack.GetComp<CompApparelVerbOwner>().AllVerbs.First(v => v is Verb_Jump);
            // a ground cell 4..6 cells from a level-1 top cell, line clear
            tfPackTo = IntVec3.Invalid;
            IntVec3 home = tfStairs.IsValid ? tfStairs : Centroid(map);
            IntVec3 stand = IntVec3.Invalid;
            IntVec3 rockAim = IntVec3.Invalid;
            foreach (IntVec3 g in GenRadial.RadialCellsAround(home, 40f, true))
            {
                if (!g.InBounds(map) || g.z >= a.groundZ || TH(g, map) != 1)
                {
                    continue;
                }
                IntVec3 top = Levels.Cell(g, 1, map);
                if (!top.Standable(map) || map.fogGrid.IsFogged(top))
                {
                    continue;
                }
                foreach (IntVec3 s in GenRadial.RadialCellsAround(g, 6f, false))
                {
                    if (!s.InBounds(map) || s.z >= a.groundZ || TH(s, map) != 0 || !s.Standable(map) || s.Roofed(map) || map.fogGrid.IsFogged(s) || s.DistanceTo(g) < 3.5f)
                    {
                        continue;
                    }
                    Teleport(tfPacker, s);
                    if (tfPackVerb.ValidateTarget(top, false))
                    {
                        stand = s;
                        tfPackTo = top;
                        break;
                    }
                }
                if (tfPackTo.IsValid)
                {
                    break;
                }
            }
            if (!tfPackTo.IsValid)
            {
                Result("F6 a jump pack target on a level-1 top", false, "none valid near the stairs");
                stage = "tm_f_sight";
                return;
            }
            // a band rock cell is refused
            foreach (IntVec3 c in GenRadial.RadialCellsAround(ProjectZ.GroundCellOf(tfPackTo, map), 6f, true))
            {
                IntVec3 u = c.InBounds(map) && c.z < a.groundZ ? Levels.Cell(c, 1, map) : IntVec3.Invalid;
                if (u.IsValid && u.GetEdifice(map) is Mineable)
                {
                    rockAim = u;
                    break;
                }
            }
            bool rockRefused = !rockAim.IsValid || !tfPackVerb.ValidateTarget(rockAim, false);
            Result("F6 a jump pack can't target band rock", rockRefused, $"rock {rockAim}");
            Draft(tfPacker, true);
            tfPackVerb.OrderForceTarget(tfPackTo);
            Info($"F6 {tfPacker} at {stand} jumps to {tfPackTo}");
            TickUntil(1200, () => tfPacker.Spawned && ProjectZ.FloorOf(tfPacker.Position, map) == 1, "tm_f_packed", 5);
        }

        // ---------------------------------------------------------------- F7

        private static Pawn tfHidden, tfOpen;
        private static IntVec3 tfMortarG, tfMortarTop;

        private static void TfSight(Map map, AnnexMapComponent a)
        {
            int lines = 0, through = 0, bad = 0;
            string first = null;
            int gz = a.groundZ;
            Rand.PushState(4242);
            for (int i = 0; i < 30000; i++)
            {
                IntVec3 g1 = new IntVec3(Rand.Range(2, map.Size.x - 2), 0, Rand.Range(2, gz - 2));
                IntVec3 g2 = g1 + new IntVec3(Rand.RangeInclusive(-25, 25), 0, Rand.RangeInclusive(-25, 25));
                if (!g2.InBounds(map) || g2.z >= gz || g1 == g2)
                {
                    continue;
                }
                int L1 = TH(g1, map), L2 = TH(g2, map);
                IntVec3 c1 = Levels.Cell(g1, L1, map), c2 = Levels.Cell(g2, L2, map);
                if (!c1.Standable(map) || !c2.Standable(map))
                {
                    continue;
                }
                lines++;
                // the straight line from eye to middle: inside a rock column (below its top's floor) somewhere in the
                // middle part?
                float hs = L1 * Heights.Storey + Heights.Eye, ht = L2 * Heights.Storey + Heights.Middle;
                float len = (g2 - g1).LengthHorizontal;
                bool inRock = false;
                string where = null;
                // the game's own cell line (Heights.Line), the line's height over each middle cell
                List<IntVec3> cells = Heights.Line(g1, g2).ToList();
                for (int k = 1; k < cells.Count - 1 && !inRock; k++)
                {
                    IntVec3 gc = cells[k];
                    float t = (gc - g1).LengthHorizontal / len;
                    if (t < 1.5f / len || t > 1f - 1.5f / len)
                    {
                        continue;
                    }
                    float y = Mathf.Lerp(hs, ht, t);
                    // inside a rock block (a natural rock edifice on some level, a wall high over its floor)
                    int H = TH(gc, map);
                    for (int L = 0; L <= Mathf.Max(0, H - 1) && !inRock; L++)
                    {
                        IntVec3 lc = Levels.Cell(gc, L, map);
                        if (lc.IsValid && lc.GetEdifice(map) is Building b && (b.def.building?.isNaturalRock ?? false))
                        {
                            inRock = y > L * Heights.Storey + 0.3f && y < L * Heights.Storey + Heights.Wall - 0.3f;
                            if (inRock)
                            {
                                where = $"{lc} {b.def.defName} L{L} at height {y:0.00} (t {t:0.00})";
                            }
                        }
                    }
                }
                if (!inRock)
                {
                    continue;
                }
                through++;
                if (Heights.CanSee(c1, c2, map))
                {
                    bad++;
                    first = (first == null ? "" : first + " || ") + $"{c1} -> {c2} (L{L1} -> L{L2}), inside {where}; cells on the line: {string.Join(" ", Heights.Line(g1, g2).Select(x => x.x + "," + x.z + ":" + TH(x, map) + (x.GetEdifice(map)?.def.defName ?? "")))}";
                }
            }
            Rand.PopState();
            Result("F7 sight never passes through rock: every sampled line from a standable cell to another that runs inside a rock column is blocked",
                bad == 0 && through > 50, $"{lines} lines, {through} through rock, {bad} seen{(first == null ? "" : ", first " + first)}");
        }

        private static IntVec3 tfBlastAt;
        private static float tfHiddenBefore, tfOpenBefore;

        private static float Hurt(Pawn p) => p.health.hediffSet.hediffs.OfType<Hediff_Injury>().Where(h => !h.IsPermanent()).Sum(h => h.Severity);

        private static void TfBlast(Map map, AnnexMapComponent a)
        {
            // on a top of level L at the foot of a one-storey cliff: the hidden animal on the higher top 3 cells in (behind
            // the rim), the open one on the same top 2 cells the other way
            tfBlastAt = IntVec3.Invalid;
            IntVec3 home = Centroid(map);
            IntVec3 hide = IntVec3.Invalid, open = IntVec3.Invalid;
            foreach (IntVec3 g in Near(home, 120, map))
            {
                int L = TH(g, map);
                if (L < 1 || g.z >= a.groundZ - 4 || g.z < 4 || g.x < 4 || g.x >= map.Size.x - 4)
                {
                    continue;
                }
                IntVec3 c = Levels.Cell(g, L, map);
                if (!c.Standable(map) || map.fogGrid.IsFogged(c))
                {
                    continue;
                }
                foreach (IntVec3 d in GenAdj.CardinalDirections)
                {
                    IntVec3 beh = g + d * 3, back = g - d * 2;
                    if (TH(g + d, map) != L + 1 || TH(g + d * 2, map) != L + 1 || TH(beh, map) != L + 1 || TH(back, map) != L || TH(g - d, map) != L)
                    {
                        continue;
                    }
                    IntVec3 hc = Levels.Cell(beh, L + 1, map), bc = Levels.Cell(back, L, map);
                    if (!hc.Standable(map) || !bc.Standable(map))
                    {
                        continue;
                    }
                    tfBlastAt = c;
                    hide = hc;
                    open = bc;
                    break;
                }
                if (tfBlastAt.IsValid)
                {
                    break;
                }
            }
            if (!tfBlastAt.IsValid)
            {
                Result("F7 a cliff foot on a top for the explosion", false, "none");
                stage = "tm_f_blasted";
                return;
            }
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamed("Muffalo");
            tfHidden = PawnGenerator.GeneratePawn(kind);
            tfOpen = PawnGenerator.GeneratePawn(kind);
            GenSpawn.Spawn(tfHidden, hide, map);
            GenSpawn.Spawn(tfOpen, open, map);
            // (fresh injuries only: a generated animal can come with old ones)
            tfHiddenBefore = Hurt(tfHidden);
            tfOpenBefore = Hurt(tfOpen);
            GenExplosion.DoExplosion(tfBlastAt, map, 3.9f, DamageDefOf.Bomb, null, 30);
            Info($"F7 colonists near the explosion: {string.Join(", ", Colonists(map).Where(p => p.Position.DistanceTo(tfBlastAt) < 8f).Select(p => $"{p} at {p.Position} 3D {Range3D.Distance(tfBlastAt, p.Position, map):0.0}"))}; tick {Find.TickManager.TicksGame}");
            Info($"F7 explosion r3.9 at {tfBlastAt} (level {ProjectZ.FloorOf(tfBlastAt, map)}); behind the rim {hide} (level {ProjectZ.FloorOf(hide, map)}, 3D {Range3D.Distance(tfBlastAt, hide, map):0.0}), in the open {open} (3D {Range3D.Distance(tfBlastAt, open, map):0.0})");
            TickUntil(120, () => false, "tm_f_blasted", 30);
        }

        private static void TfBlastCheck(Map map)
        {
            if (tfHidden == null)
            {
                return;
            }
            bool hiddenHurt = tfHidden.Dead || Hurt(tfHidden) > tfHiddenBefore;
            bool openHurt = tfOpen.Dead || Hurt(tfOpen) > tfOpenBefore;
            Result("F7 an explosion on a top at the foot of a cliff hurts an animal in the open, not one on the higher top behind the rim", !hiddenHurt && openHurt, $"behind the rim hurt {hiddenHurt}, open hurt {openHurt}");
            foreach (Pawn p in new[] { tfHidden, tfOpen })
            {
                if (p.Spawned)
                {
                    p.Destroy();
                }
                else if (p.Corpse != null && p.Corpse.Spawned)
                {
                    p.Corpse.Destroy();
                }
            }
        }

        private static void TfMortar(Map map, AnnexMapComponent a)
        {
            tfMortarG = IntVec3.Invalid;
            IntVec3 home = Centroid(map);
            foreach (IntVec3 g in Near(home, 120, map))
            {
                if (g.InBounds(map) && g.z < a.groundZ - 3 && g.z > 33 && TH(g, map) >= 2 && Levels.Cell(g, TH(g, map), map).Standable(map))
                {
                    tfMortarG = g;
                    break;
                }
            }
            if (!tfMortarG.IsValid)
            {
                Result("F7 a mountain cell for the mortar", false, "none");
                stage = "tm_f_drops";
                return;
            }
            tfMortarTop = Column.Top(tfMortarG, map);
            ThingDef shellDef = DefDatabase<ThingDef>.GetNamed("Bullet_Shell_HighExplosive");
            IntVec3 from = new IntVec3(tfMortarG.x, 0, tfMortarG.z - 30);
            Projectile shell = (Projectile)GenSpawn.Spawn(shellDef, from, map);
            shell.Launch(Colonists(map).First(), from.ToVector3Shifted(), new LocalTargetInfo(tfMortarG), new LocalTargetInfo(tfMortarG), ProjectileHitFlags.IntendedTarget);
            lastExplosion = IntVec3.Invalid;
            TickUntil(1500, () => lastExplosion.IsValid, "tm_f_mortar", 1);
        }

        // ---------------------------------------------------------------- F8

        private static List<DropPodIncoming> tfPods;
        private static List<Thing> tfAimed;
        private static IntVec3 tfAimTop;

        private static void TfDrops(Map map, AnnexMapComponent a)
        {
            // the finders, with colonists up on a top (TryFindRaidDropCenterClose drops next to them)
            int onTop = 0, n = 0;
            string first = null;
            void Count(string what, IntVec3 c)
            {
                n++;
                if (c.IsValid && c.InBounds(map) && TmTop(c, map))
                {
                    onTop++;
                    first = first ?? what + " " + c;
                }
            }
            for (int i = 0; i < 200; i++)
            {
                DropCellFinder.TryFindRaidDropCenterClose(out IntVec3 c, map);
                Count("centre close", c);
                Count("random drop spot", DropCellFinder.RandomDropSpot(map));
            }
            for (int i = 0; i < 20; i++)
            {
                Count("centre distant", DropCellFinder.FindRaidDropCenterDistant(map));
                if (DropCellFinder.FindSafeLandingSpot(out IntVec3 s, null, map))
                {
                    Count("safe landing", s);
                }
            }
            int upHere = Colonists(map).Count(p => TmTop(p.Position, map));
            Result("F8 the game's raid centre, random drop and safe landing finders never give a natural top (Q6, D1)", onTop == 0, $"{n} spots, {onTop} on a top{(first == null ? "" : ", first " + first)}; colonists on a top {upHere}");
            // a player-aimed drop at a top still lands there
            tfAimTop = tfWalkTo.IsValid && TmTop(tfWalkTo, map) ? tfWalkTo : IntVec3.Invalid;
            tfAimed = new List<Thing>();
            if (tfAimTop.IsValid)
            {
                Result("F8 a drop the player aims at a top is allowed there (CanPhysicallyDropInto outside the game's finders)", DropCellFinder.CanPhysicallyDropInto(tfAimTop, map, false), $"{tfAimTop}");
                Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                steel.stackCount = 20;
                tfAimed.Add(steel);
                DropPodUtility.DropThingsNear(tfAimTop, map, tfAimed, canRoofPunch: false, forbid: false);
            }
            // a real centre-drop raid (colonists on the top) and a cargo pod crash
            tfPods = new List<DropPodIncoming>();
            List<DropPodIncoming> before = map.listerThings.AllThings.OfType<DropPodIncoming>().ToList();
            foreach (Faction f in Find.FactionManager.AllFactions.Where(f => f.HostileTo(Faction.OfPlayer) && !f.def.hidden && f.def.humanlikeFaction && !f.temporary && f.def.pawnGroupMakers != null && f.def.techLevel >= TechLevel.Industrial).InRandomOrder())
            {
                IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
                parms.faction = f;
                parms.raidStrategy = RaidStrategyDefOf.ImmediateAttack;
                parms.raidArrivalMode = PawnsArrivalModeDefOf.CenterDrop;
                parms.points = 300f;
                parms.forced = true;
                if (IncidentDefOf.RaidEnemy.Worker.TryExecute(parms))
                {
                    break;
                }
            }
            IncidentDef pod = DefDatabase<IncidentDef>.GetNamedSilentFail("ResourcePodCrash");
            for (int i = 0; i < 3 && pod != null; i++)
            {
                IncidentParms parms = StorytellerUtility.DefaultParmsNow(pod.category, map);
                parms.forced = true;
                pod.Worker.TryExecute(parms);
            }
            tfPods = map.listerThings.AllThings.OfType<DropPodIncoming>().Except(before).ToList();
            int podsTop = tfPods.Count(p => TmTop(p.Position, map));
            Result("F8 a real centre-drop raid (colonists on a top) and cargo pod crashes: no pod comes down on a natural top", tfPods.Count > 0 && podsTop == 0,
                $"{tfPods.Count} pods, {podsTop} on a top: {string.Join(" ", tfPods.Take(10).Select(p => p.Position.ToString()))}");
            TickUntil(1200, () => false, "tm_f_dropped", 60);
        }

        private static void TfDropsCheck(Map map)
        {
            List<Pawn> hostiles = map.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer) && p.RaceProps.Humanlike).ToList();
            int hostTop = hostiles.Count(p => TmTop(p.Position, map) || ProjectZ.FloorOf(p.Position, map) >= 1 && p.Position.Roofed(map) == false && Mountains.NaturalFloor(p.Position, map));
            int hostUp = hostiles.Count(p => ProjectZ.FloorOf(p.Position, map) >= 1);
            Info($"F8 raiders 1200 ticks after landing (they may climb the stairs): {hostiles.Count}, {hostUp} on an upper level, {hostTop} on a natural top: {string.Join(" ", hostiles.Take(8).Select(h => h.Position.ToString()))}");
            bool aimedUp = tfAimed.Count == 0 || tfAimed.Any(t => t.Spawned && ProjectZ.FloorOf(t.Position, map) >= 1);
            Result("F8 the aimed drop landed on the top", aimedUp, string.Join(" ", tfAimed.Select(t => t.Spawned ? t.Position.ToString() : "not spawned")));
            foreach (Pawn h in hostiles)
            {
                h.Destroy();
            }
        }

        // ---------------------------------------------------------------- F9

        private static void TfLarge(Map map)
        {
            if (!ModsConfig.AnomalyActive)
            {
                Info("F9 Anomaly not active: large structure finder skipped");
                return;
            }
            int n = 0, up = 0;
            foreach (ThingDef def in new[] { ThingDefOf.PitGate, ThingDefOf.FleshmassHeart, ThingDefOf.PitBurrow })
            {
                for (int i = 0; i < 4; i++)
                {
                    LargeBuildingSpawnParms parms = LargeBuildingSpawnParms.Default.ForThing(def);
                    parms.allowFogged = true;
                    parms.ignoreTerrainAffordance = true;
                    if (LargeBuildingCellFinder.TryFindCell(out IntVec3 c, map, parms, forceRecalculate: true))
                    {
                        n++;
                        up += ProjectZ.IsAnnex(c, map) ? 1 : 0;
                    }
                }
            }
            Result("F9 Anomaly's large structures (pit gate, fleshmass heart, burrow) only on the ground (D2)", up == 0, $"{n} cells found, {up} in a band");
        }

        // ---------------------------------------------------------------- F12

        private static IntVec3 tfDownAt, tfDownFoot, tfDownExit;
        private static Rot4 tfDownRot;
        private static Thing tfDownStairs;
        private static readonly List<Pawn> tfDownCrew = new List<Pawn>();
        private static int tfDownJumps;

        private static bool TfDownBuilt(Map map) => map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).Any(t => t.Position == tfDownAt);

        private static string TfDownThing(Map map) => string.Join(" ", tfDownAt.GetThingList(map).Select(t => t.def.defName));

        // A ground stairs site whose exit is a level-1 top (or the lip next to one) that has no way down: colonists put
        // there (as by a transport pod) build the stairs from the top, then walk down them.
        private static void TfDown(Map map, AnnexMapComponent a)
        {
            tfDownCrew.Clear();
            tfDownStairs = null;
            IntVec3 home = Centroid(map);
            TraverseParms tp = TraverseParms.For(TraverseMode.PassDoors);
            int best = int.MaxValue;
            tfDownAt = IntVec3.Invalid;
            IntVec3 stand = IntVec3.Invalid;
            foreach (IntVec3 p in Near(home, 70, map))
            {
                if (p.z >= a.groundZ - 3 || p.z < 3 || p.x < 3 || p.x >= map.Size.x - 3)
                {
                    continue;
                }
                for (int r = 0; r < 4; r++)
                {
                    Rot4 rot = new Rot4(r);
                    IntVec3 exit = StairShape.Exit(p, rot, map);
                    IntVec3 foot = p - rot.FacingCell * 2;
                    if (!exit.IsValid || !foot.InBounds(map) || !foot.Standable(map) || foot.Roofed(map))
                    {
                        continue;
                    }
                    // where the crew stands: the exit itself (on a top) or the top just past the lip
                    IntVec3 eg = ProjectZ.GroundCellOf(exit, map);
                    bool onTop = TH(eg, map) == 1 && TmTop(exit, map);
                    int score = (p - home).LengthManhattan + (onTop ? 0 : 1000);   // prefer an exit right on the top
                    if (score >= best)
                    {
                        continue;
                    }
                    IntVec3 top = onTop ? exit : exit + rot.FacingCell;
                    if (!top.InBounds(map) || !TmTop(top, map) || TH(ProjectZ.GroundCellOf(top, map), map) != 1 || map.fogGrid.IsFogged(top) || !top.Standable(map))
                    {
                        continue;
                    }
                    if (StairShape.Footprint(p, rot).Cells.Any(c => !c.InBounds(map) || map.fogGrid.IsFogged(c) || c.Roofed(map) || c.GetEdifice(map) != null || !c.Standable(map)))
                    {
                        continue;
                    }
                    if (map.reachability.CanReach(top, foot, PathEndMode.OnCell, tp) || !GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, p, rot, map, stuffDef: ThingDefOf.WoodLog).Accepted)
                    {
                        continue;   // (a top with a way down already)
                    }
                    // a rim the crew can work from: a walkable level-1 cell over the stairwell's ring that the top
                    // reaches (as StairTouch.UpperCells); an exit over the lip with no top beside the stairwell has none
                    IntVec3 topc = top;
                    if (!StairShape.Footprint(p, rot).ExpandedBy(1).Cells.Any(c => c.InBounds(map) && ProjectZ.FloorOf(c, map) == 0
                        && ProjectZ.CellOnFloor(c, 1, map) is IntVec3 u && u.IsValid && u.InBounds(map) && u.Walkable(map) && map.reachability.CanReach(topc, u, PathEndMode.OnCell, tp)))
                    {
                        continue;
                    }
                    // room on the top for the crew and the wood
                    if (GenRadial.RadialCellsAround(top, 4f, true).Count(c => c.InBounds(map) && TmTop(c, map) && c.Standable(map) && map.reachability.CanReach(top, c, PathEndMode.OnCell, tp)) < 8)
                    {
                        continue;
                    }
                    best = score;
                    tfDownAt = p;
                    tfDownRot = rot;
                    tfDownFoot = foot;
                    tfDownExit = exit;
                    stand = top;
                }
            }
            if (!tfDownAt.IsValid)
            {
                Result("F12 a stairs site down from a level-1 top with no way down", false, $"none within 70 of {home}");
                TfDownEnd(map);
                return;
            }
            List<IntVec3> room = GenRadial.RadialCellsAround(stand, 4f, true).Where(c => c.InBounds(map) && TmTop(c, map) && c.Standable(map) && map.reachability.CanReach(stand, c, PathEndMode.OnCell, tp)).ToList();
            // Isolated from the earlier checks: their explosions (F7) and raid (F8) can leave the colonists hurt or
            // downed. Nothing hostile left over (raiders, shells still in the air), the old colonists drafted out of it,
            // and a fresh, rested crew of two put on the top (as by a transport pod).
            int shells = 0, foes = 0;
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).ToList())
            {
                shells++;
                t.Destroy();
            }
            foreach (Pawn h in map.mapPawns.AllPawnsSpawned.Where(h => h.HostileTo(Faction.OfPlayer)).ToList())
            {
                foes++;
                h.Destroy();
            }
            foreach (Pawn p in Colonists(map))
            {
                if (!p.Downed && p.drafter != null)
                {
                    p.drafter.Drafted = true;   // (the old colonists stay out of it)
                }
            }
            for (int i = 0; i < 20 && tfDownCrew.Count < 2; i++)
            {
                Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true));
                if (p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) || p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) || !p.health.capacities.CapableOf(PawnCapacityDefOf.Moving)
                    || !p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                {
                    Find.WorldPawns.PassToWorld(p, RimWorld.Planet.PawnDiscardDecideMode.Discard);
                    continue;
                }
                GenSpawn.Spawn(p, room[tfDownCrew.Count], map);
                foreach (Need n in p.needs.AllNeeds)
                {
                    n.CurLevel = n.MaxLevel;
                }
                tfDownCrew.Add(p);
                tfHurtSeen.UnionWith(p.health.hediffSet.hediffs);   // (old wounds from generation: not news)
            }
            Info($"F12 isolated: {shells} projectiles and {foes} hostiles removed, a fresh crew of {tfDownCrew.Count}");
            for (int i = 0; i < 4; i++)
            {
                Thing w = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                w.stackCount = 75;
                GenSpawn.Spawn(w, room[room.Count - 1 - i], map);
            }
            bool fromTop = tfDownExit == stand;
            GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, tfDownAt, map, tfDownRot, Faction.OfPlayer, ThingDefOf.WoodLog);
            tfDownStairs = tfDownAt.GetThingList(map).FirstOrDefault(t => t.def.entityDefToBuild == PZDefOf.PZ_Stairs);
            List<IntVec3> rim = new List<IntVec3>();
            if (tfDownStairs != null)
            {
                StairTouch.UpperCells(tfDownStairs, rim);
            }
            Info($"F12 stairs down at {tfDownAt} facing {tfDownRot}, exit {tfDownExit} ({(fromTop ? "on the top" : "over the lip")}), crew {string.Join(", ", tfDownCrew)} on the top at {stand}, foot {tfDownFoot}; rim cells from the top {rim.Count} ({string.Join(" ", rim)}); top reaches the foot {map.reachability.CanReach(stand, tfDownFoot, PathEndMode.OnCell, tp)}");
            if (tfDownCrew.Count == 0 || tfDownStairs == null)
            {
                Result("F12 a crew and a blueprint", false, $"crew {tfDownCrew.Count}, blueprint {tfDownStairs}");
                TfDownEnd(map);
                return;
            }
            SetWork(map, true);
            foreach (Pawn p in tfDownCrew)
            {
                if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                {
                    p.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2);
                }
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            TickUntil(30000, () => TfDownBuilt(map), "tm_f_down_built", 120);
        }

        private static void TfDownEnd(Map map)
        {
            tickWatch = null;
            foreach (Pawn p in Colonists(map))
            {
                if (p.Drafted)
                {
                    p.drafter.Drafted = false;
                }
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            stage = "tm_shots";
        }

        // ---------------------------------------------------------------- F10

        private static IntVec3 tfTallSolid = IntVec3.Invalid;

        private static void TfTall(Map map, AnnexMapComponent a)
        {
            ThingDef tall = DefDatabase<ThingDef>.AllDefsListForReading.FirstOrDefault(d => d.category == ThingCategory.Building && d.BuildableByPlayer && d.size == IntVec2.One && HeightRule.TooTall(d) && d.researchPrerequisites.NullOrEmpty() && (d.MadeFromStuff || d.CostList != null));
            if (tall == null)
            {
                Info("F10 no 1x1 tall building without research: skipped");
                return;
            }
            // ground faces (revealed natural rock) under a column with top >= 2 and one with top 1
            IntVec3 solid = IntVec3.Invalid, underTop = IntVec3.Invalid;
            IntVec3 home = Centroid(map);
            foreach (IntVec3 g in Near(home, 160, map))
            {
                if (!g.InBounds(map) || g.z >= a.groundZ - 1 || g.z < 1 || map.fogGrid.IsFogged(g) || !(g.GetEdifice(map) is Mineable m) || !(map.roofGrid.RoofAt(g)?.isThickRoof ?? false))
                {
                    continue;
                }
                int H = TH(g, map);
                if (!solid.IsValid && H >= 3 && Levels.Cell(g, 1, map).GetEdifice(map) is Mineable)
                {
                    solid = g;
                }
                if (!underTop.IsValid && H == 1)
                {
                    underTop = g;
                }
                if (solid.IsValid && underTop.IsValid)
                {
                    break;
                }
            }
            if (!solid.IsValid || !underTop.IsValid)
            {
                Result("F10 ground faces under solid rock and under a top", false, $"solid {solid}, under a top {underTop}");
                return;
            }
            tfTallSolid = solid;
            // dug out (the ground rock taken away, as mining would)
            solid.GetEdifice(map).Destroy(DestroyMode.Vanish);
            underTop.GetEdifice(map).Destroy(DestroyMode.Vanish);
            ThingDef stuff = tall.MadeFromStuff ? GenStuff.DefaultStuffFor(tall) : null;
            AcceptanceReport rSolid = GenConstruct.CanPlaceBlueprintAt(tall, solid, Rot4.North, map, stuffDef: stuff);
            AcceptanceReport rTop = GenConstruct.CanPlaceBlueprintAt(tall, underTop, Rot4.North, map, stuffDef: stuff);
            // the cell above the solid one dug out too
            Levels.Cell(solid, 1, map).GetEdifice(map)?.Destroy(DestroyMode.Vanish);
            AcceptanceReport rDug = GenConstruct.CanPlaceBlueprintAt(tall, solid, Rot4.North, map, stuffDef: stuff);
            string msg = "PZ_TooTallUnderFloor".Translate();
            Result("F10 a tall building (Q7): allowed in a dug ground cell under solid rock, refused under a top and under a dug cell",
                rSolid.Accepted && !rTop.Accepted && rTop.Reason == msg && !rDug.Accepted && rDug.Reason == msg,
                $"{tall.defName}: under solid rock {solid} '{(rSolid.Accepted ? "ok" : rSolid.Reason)}', under a top {underTop} '{(rTop.Accepted ? "ok" : rTop.Reason)}', under the dug cell '{(rDug.Accepted ? "ok" : rDug.Reason)}'");
        }

        // ---------------------------------------------------------------- F11

        private static void TfInfest(Map map, AnnexMapComponent a)
        {
            // a revealed level-1 face, then a tunnel of 6 and a 5x5 hall dug in from it (the game's despawn, in order, so
            // vanilla's fog flood follows the digging)
            IntVec3 face = IntVec3.Invalid, dir = IntVec3.Invalid;
            IntVec3 home = Centroid(map);
            foreach (IntVec3 g in Near(home, 160, map))
            {
                if (!g.InBounds(map) || g.z >= a.groundZ - 16 || g.z < 16 || g.x < 16 || g.x >= map.Size.x - 16)
                {
                    continue;
                }
                IntVec3 c = Levels.Cell(g, 1, map);
                if (map.fogGrid.IsFogged(c) || !(c.GetEdifice(map) is Mineable))
                {
                    continue;
                }
                foreach (IntVec3 d in GenAdj.CardinalDirections)
                {
                    bool deep = true;
                    for (int k = 1; k <= 12 && deep; k++)
                    {
                        for (int s = -3; s <= 3 && deep; s++)
                        {
                            IntVec3 side = new IntVec3(d.z, 0, d.x) * s;
                            deep = TH(g + d * k + side, map) >= 2;
                        }
                    }
                    if (deep && TmTop(Levels.Cell(g - d, 1, map), map))
                    {
                        face = g;
                        dir = d;
                        break;
                    }
                }
                if (face.IsValid)
                {
                    break;
                }
            }
            if (!face.IsValid)
            {
                Result("F11 a level-1 face with deep rock behind it", false, "none");
                return;
            }
            List<IntVec3> dig = new List<IntVec3>();
            for (int k = 0; k < 6; k++)
            {
                dig.Add(face + dir * k);
            }
            IntVec3 hall = face + dir * 8;
            foreach (IntVec3 g in CellRect.CenteredOn(hall, 2).Cells.OrderBy(g => (g - face).LengthHorizontalSquared))
            {
                dig.Add(g);
            }
            dig.Add(face + dir * 6);
            foreach (IntVec3 g in dig)
            {
                Levels.Cell(g, 1, map).GetEdifice(map)?.Destroy(DestroyMode.Vanish);
            }
            // a colony building on band 1 at the tunnel mouth
            IntVec3 mouth = Levels.Cell(face - dir, 1, map);
            Thing campfire = Place(ThingDefOf.Campfire, null, mouth, map);
            IntVec3 hallC = Levels.Cell(hall, 1, map);
            int found = 0, inHall = 0, band1 = 0, far = 0;
            HashSet<IntVec3> hallCells = new HashSet<IntVec3>(CellRect.CenteredOn(hall, 2).Cells.Select(g => Levels.Cell(g, 1, map)));
            List<Thing> band1Buildings = map.listerBuildings.allBuildingsColonist.Where(b => ProjectZ.FloorOf(b.Position, map) == 1).Cast<Thing>().ToList();
            for (int i = 0; i < 30; i++)
            {
                if (!InfestationCellFinder.TryFindCell(out IntVec3 c, map))
                {
                    continue;
                }
                found++;
                if (ProjectZ.FloorOf(c, map) == 1)
                {
                    band1++;
                    inHall += hallCells.Contains(c) ? 1 : 0;
                    if (!band1Buildings.Any(b => b.Position.DistanceTo(c) <= 31f))
                    {
                        far++;
                    }
                }
            }
            Result("F11 infestations per band: a dug level-1 hall near a band-1 colony building gets candidates, none on band 1 out of reach of a band-1 building",
                band1 > 0 && far == 0, $"hall {hallC} (fogged {map.fogGrid.IsFogged(hallC)}, roof {map.roofGrid.RoofAt(hallC)?.defName}, temp {hallC.GetTemperature(map):0}); {found} finds, {band1} on band 1 ({inHall} in the hall), {far} far from band-1 buildings");
            // InfestationBand's mountainousness row (kept in G3b): a band-1 cell near its band's south edge samples only its
            // own band (BandRadial alone would fold the cells past the edge onto the void and count them as rockless samples)
            MethodInfo mtn = AccessTools.Method(typeof(InfestationCellFinder), "GetMountainousnessScoreAt");
            IntVec3 edge = IntVec3.Invalid;
            float own = 0f;
            int samples = 0, off = 0;
            // (the first band-1 cell 2..10 rows from its band's south or north edge with rock round it, the game's minimum 0.17)
            foreach (int gz in Enumerable.Range(2, 9).Concat(Enumerable.Range(a.groundZ - 11, 9)))
            {
                for (int gx = 0; gx < map.Size.x && !edge.IsValid; gx++)
                {
                    IntVec3 g = new IntVec3(gx, 0, gz);
                    if (!g.InBounds(map))
                    {
                        continue;
                    }
                    IntVec3 cell = Levels.Cell(g, 1, map);
                    own = 0f;
                    samples = off = 0;
                    for (int i = 0; i < 700; i += 10)
                    {
                        IntVec3 s = cell + GenRadial.RadialPattern[i];
                        if (!s.InBounds(map))
                        {
                            continue;
                        }
                        if (!ProjectZ.SameBand(s, cell, map))
                        {
                            off++;
                            continue;
                        }
                        Building e = s.GetEdifice(map);
                        own += e != null && e.def.category == ThingCategory.Building && e.def.building.isNaturalRock ? 1f : s.Roofed(map) && s.GetRoof(map).isThickRoof ? 0.5f : 0f;
                        samples++;
                    }
                    if (off > 0 && samples > 0 && own / samples > 0.17f)
                    {
                        edge = cell;
                    }
                }
            }
            float score = mtn != null && edge.IsValid ? (float)mtn.Invoke(null, new object[] { edge, map }) : -1f;
            Result("F11 mountainousness at a band edge counts only its own band's cells (InfestationBand's row)", edge.IsValid && Mathf.Abs(score - own / samples) < 0.0001f,
                $"cell {edge}: game {score:0.0000}, own band only {own / Mathf.Max(1, samples):0.0000} ({samples} samples on the band, {off} past its edge; counted as rockless void samples it would be {own / Mathf.Max(1, samples + off):0.0000})");
            // R23: a child hive's spot on another band is refused (CompSpawnerHives.CanSpawnHiveAt)
            MethodInfo can = AccessTools.Method(typeof(CompSpawnerHives), "CanSpawnHiveAt");
            // (a walkable thick-roofed cell on the ground, F10's dug cell, as the "other band" spot)
            IntVec3 other = tfTallSolid;
            IntVec3 same = Levels.Cell(hall + new IntVec3(1, 0, 1), 1, map);
            bool sameOk = can != null && (bool)can.Invoke(null, new object[] { same, map, hallC, ThingDefOf.Hive, 0f, true });
            bool otherOk = can != null && other.IsValid && (bool)can.Invoke(null, new object[] { other, map, hallC, ThingDefOf.Hive, 0f, true });
            Result("R23 a child hive stays on its parent's band (a spot that is fine on the same band is refused on another)", sameOk && other.IsValid && !otherOk, $"same band {same}: {sameOk}; another band {other}: {otherOk}");
            campfire.Destroy();
        }
    }
}
