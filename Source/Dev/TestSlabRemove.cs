using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode slabremove (removal design §7; run with -projectzfloors=3): taking up upper floors, stairs anywhere, slabs
    // falling. Real flows: the vanilla Remove-foundation designator through the UI pointer on floor 2, colonists doing
    // the jobs with ticks, stairs built and deconstructed by colonists, a wall killed under a slab.
    //   Z1 house H (13 x 10, roofed, slab over its inside, stairs S up to it): the tool reads "Remove upper floor" on
    //      floor 2 and the vanilla label on the ground; refusals: a wall, a table, a wall blueprint, S's landing, a slab
    //      with a slab above (floor 3);
    //   Z2 a 4 x 5 block dragged at H's east end (one cell carpeted, filth on one, a drafted colonist and steel on two)
    //      plus a slab island nobody reaches: colonists take the block up farthest first (peel order), each from the
    //      cell one step nearer the stairs, carpet then slab; compat G2 (user 2026-10-18): the wood (and cloth), the colonist
    //      and the steel fall to the ground, breaking the plain roof below; nobody and nothing on open air; the island never
    //      worked (-projectznogravity: the old rule, wood back on floor 2, roof below plain, the room still indoors);
    //   Z3 stairs T placed in the open field (no roof anywhere): accepted, built by colonists with their landing and the
    //      roof under it, a colonist walks up; shots (two turns and the side); deconstructed: landing and its roof go,
    //      the colonist on the landing falls to the floor below (compat G2; -projectznogravity: the nearest floor cell);
    //   Z4 pavilion P (a 5 x 5 slab on a roof held by one column): a colonist, steel and a table on the slab, a colonist
    //      under it; the column is killed: the slab collapses (colonist falls, bruised and crushed; steel down; table
    //      destroyed; the colonist under it crushed); a slab nobody holds up (as in a save from before) stays put.
    public static partial class TestRunner
    {
        private static IntVec3 zO;
        private static CellRect zHouse;
        private static IntVec3 zStairsS, zIsland, zCarpet, zFilth, zA3, zWall, zTable, zWallBp, zSitterAt, zSteelAt, zLegacy;
        private static IntVec3 zStairsT, zColumn;
        private static CellRect zBlock, zPavilion;
        private static Designator zTool;
        private static Pawn zSitter, zClimber, zOnSlab, zBelow;
        private static readonly Dictionary<IntVec3, int> zDist = new Dictionary<IntVec3, int>();
        private static readonly Dictionary<IntVec3, int> zRemovedAt = new Dictionary<IntVec3, int>();
        private static readonly List<IntVec3> zOrder = new List<IntVec3>();
        private static int zWorkChecks, zWorkBad, zIslandJobs, zCarpetFloorJobs, zCarpetSlabJobs, zTick;
        private static string zWorkBadLine;
        private static int zWoodBefore, zClothBefore, zSteelTotal, zWoodLowBefore, zClothLowBefore;
        private static Thing zSteel, zPTable, zPSteel;
        private static int zInjOnSlab, zInjBelow;
        private static Building_Stairs zT;
        private static bool zPatched;

        private static IntVec3 Z(int dx, int dz) => new IntVec3(zO.x + dx, 0, zO.z + dz);

        // Damage dealt to pawns (def/source), for the collapse checks: armour may soak a hit, a crush may kill before the
        // fall bruises land.
        private static readonly Dictionary<Pawn, List<string>> zHits = new Dictionary<Pawn, List<string>>();

        private static void ZTakeDamagePrefix(Thing __instance, DamageInfo dinfo)
        {
            if (__instance is Pawn p)
            {
                if (!zHits.TryGetValue(p, out List<string> l))
                {
                    zHits[p] = l = new List<string>();
                }
                l.Add(dinfo.Def.defName + "/" + (dinfo.Category == DamageInfo.SourceCategory.Collapse ? "Collapse" : ""));
            }
        }

        private static int ZHits(Pawn p, string what) => zHits.TryGetValue(p, out List<string> l) ? l.Count(s => s == what) : 0;

        private static string ZHitList(Pawn p) => zHits.TryGetValue(p, out List<string> l) ? string.Join(",", l) : "none";

        // (a debug log window opened by an error would hide the middle of the shot)
        private static void ZCapture(string name)
        {
            Find.WindowStack.TryRemove(typeof(LudeonTK.EditWindow_Log), doCloseSound: false);
            Capture(name);
        }

        private static int CountOn(Map map, ThingDef def, int floor) => map.listerThings.ThingsOfDef(def).Where(t => t.Spawned && ProjectZ.FloorOf(t.Position, map) == floor).Sum(t => t.stackCount);

        private static int Injuries(Pawn p) => p.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);

        private static string InjuryNames(Pawn p) => string.Join(",", p.health.hediffSet.hediffs.Where(h => h is Hediff_Injury).Select(h => h.def.defName + "@" + h.Part?.def.defName));

        private static void SetWork(Map map, bool construct)
        {
            foreach (Pawn p in Colonists(map))
            {
                p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
                foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefs)
                {
                    if (!p.WorkTypeIsDisabled(w))
                    {
                        p.workSettings.SetPriority(w, construct && w == WorkTypeDefOf.Construction ? 1 : 0);
                    }
                }
            }
        }

        // Every few ticks while the block is taken up: cells removed (and when), and where the workers stand.
        private static bool ZPeelWatch(Map map)
        {
            zTick += watchEvery;
            foreach (IntVec3 c in zDist.Keys)
            {
                if (!zRemovedAt.ContainsKey(c) && c.GetTerrain(map) == PZDefOf.PZ_OpenAir)
                {
                    zRemovedAt[c] = zTick;
                    zOrder.Add(c);
                }
            }
            foreach (Pawn p in Colonists(map))
            {
                Job j = p.CurJob;
                if (j == null || j.def != JobDefOf.RemoveFoundation && j.def != JobDefOf.RemoveFloor || !j.targetA.IsValid)
                {
                    continue;
                }
                IntVec3 c = j.targetA.Cell;
                if (c == zIsland)
                {
                    zIslandJobs++;
                }
                if (c == zCarpet && p.jobs.curDriver != null)
                {
                    if (j.def == JobDefOf.RemoveFloor)
                    {
                        zCarpetFloorJobs++;
                    }
                    else
                    {
                        zCarpetSlabJobs++;
                    }
                }
                if (!p.pather.Moving && (p.Position == c || p.Position.AdjacentToCardinal(c)))
                {
                    zWorkChecks++;
                    if (p.Position == c || !j.targetB.IsValid || p.Position != j.targetB.Cell)
                    {
                        zWorkBad++;
                        zWorkBadLine = $"{p} at {p.Position} on {j.def.defName} -> {c} (spot {j.targetB})";
                    }
                }
            }
            return zDist.Keys.All(c => zRemovedAt.ContainsKey(c));
        }

        private static bool StepSlabRemove(Map map)
        {
            switch (stage)
            {
                case "z_setup":
                    SetupSlabRemove(map);
                    return true;
                case "z_view":
                    ShotAt(map, 1, zHouse.CenterCell, 10f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    zTool = AllDesignators().OfType<Designator_RemoveFoundation>().FirstOrDefault();
                    if (zTool == null)
                    {
                        Result("remove-foundation designator found", false, "none");
                        Finish();
                        return true;
                    }
                    Find.DesignatorManager.Select(zTool);
                    Wait(5, "z_refuse");
                    return true;
                case "z_refuse":
                {
                    // Z1: label and refusals
                    string up = zTool.LabelCap;
                    string upDesc = zTool.Desc;
                    FloorView.Set(0);
                    string ground = zTool.LabelCap;
                    FloorView.Set(1);
                    string vanilla = ModsConfig.OdysseyActive ? "DesignatorRemoveFoundation".Translate() : "DesignatorRemoveBridge".Translate();
                    Result("the tool reads 'Remove upper floor' on floor 2, the vanilla label on the ground", up == "PZ_RemoveUpperFloor".Translate().CapitalizeFirst() && ground == vanilla.CapitalizeFirst() && upDesc == "PZ_RemoveUpperFloorDesc".Translate(),
                        $"floor 2 '{up}' / '{upDesc}', ground '{ground}' (vanilla '{vanilla}')");
                    List<string> shown = new List<string>();
                    foreach (var kv in new[]
                    {
                        new KeyValuePair<string, IntVec3>("wall", zWall), new KeyValuePair<string, IntVec3>("table", zTable), new KeyValuePair<string, IntVec3>("wall blueprint", zWallBp),
                        new KeyValuePair<string, IntVec3>("stairs landing", StairShape.Exit(zStairsS, Rot4.North, map)), new KeyValuePair<string, IntVec3>("slab above", zA3)
                    })
                    {
                        IntVec3 g = ProjectZ.GroundCellOf(kv.Value, map);
                        AcceptanceReport r = RefusalAt(zTool, ScreenOf(g, 1), out IntVec3 at);
                        string want = kv.Key == "wall" ? null : kv.Key == "stairs landing" ? "PZ_StairsLandHere".Translate().ToString() : kv.Key == "slab above" ? "PZ_FloorAboveFirst".Translate().ToString() : "MessageCannotRemoveSupportingFoundation".Translate().ToString();
                        bool ok = !r.Accepted && at == kv.Value && (want == null || r.Reason == want);
                        Result($"refused: {kv.Key}{(want == null ? " (silently, as vanilla for a wall)" : ": '" + want + "'")}", ok, $"{at} (want {kv.Value}): accepted {r.Accepted}, '{r.Reason}'");
                        if (!r.Reason.NullOrEmpty())
                        {
                            shown.Add(r.Reason);
                        }
                    }
                    foreach (string s in shown.Distinct())
                    {
                        Messages.Message(s, MessageTypeDefOf.RejectInput, false);
                    }
                    // (the wall blueprint would take the wood that comes back)
                    foreach (Thing t in zWallBp.GetThingList(map).Where(t => t is Blueprint).ToList())
                    {
                        t.Destroy(DestroyMode.Cancel);
                    }
                    // Z2: the block (one drag) and the island (one click)
                    zWoodBefore = CountOn(map, ThingDefOf.WoodLog, 1);
                    zClothBefore = CountOn(map, ThingDefOf.Cloth, 1);
                    zWoodLowBefore = CountOn(map, ThingDefOf.WoodLog, 0);
                    zClothLowBefore = CountOn(map, ThingDefOf.Cloth, 0);
                    UiDesignate(ScreenOf(new IntVec3(zBlock.minX, 0, zBlock.minZ), 1), ScreenOf(new IntVec3(zBlock.maxX, 0, zBlock.maxZ), 1));
                    UiDesignate(ScreenOf(ProjectZ.GroundCellOf(zIsland, map), 1), ScreenOf(ProjectZ.GroundCellOf(zIsland, map), 1));
                    Find.DesignatorManager.Deselect();
                    SlabPeel peel = SlabPeel.For(map);
                    zDist.Clear();
                    List<IntVec3> block = zBlock.Cells.Select(g => UpS(g, map)).ToList();
                    foreach (IntVec3 c in block)
                    {
                        zDist[c] = peel.Distance(c);
                    }
                    int des = block.Count(c => map.designationManager.DesignationAt(c, DesignationDefOf.RemoveFoundation) != null);
                    bool island = map.designationManager.DesignationAt(zIsland, DesignationDefOf.RemoveFoundation) != null;
                    Result("dragged over the block and clicked the island: designated", des == block.Count && island,
                        $"{des}/{block.Count} block cells, island {island}; distances {string.Join(" ", zDist.Values.OrderBy(d => d))}; island distance {peel.Distance(zIsland)}");
                    Wait(30, "z_designated_shot");
                    return true;
                }
                case "z_designated_shot":
                    ZCapture("slab_designated_refusals");
                    Wait(2, "z_peel");
                    return true;
                case "z_peel":
                {
                    SetWork(map, true);
                    Draft(zSitter, true);
                    zRemovedAt.Clear();
                    zOrder.Clear();
                    zTick = 0;
                    zWorkChecks = zWorkBad = zIslandJobs = zCarpetFloorJobs = zCarpetSlabJobs = 0;
                    TickUntil(60000, () => ZPeelWatch(map), "z_peeled", 5);
                    return true;
                }
                case "z_peeled":
                {
                    tickWatch = null;
                    List<IntVec3> left = zDist.Keys.Where(c => !zRemovedAt.ContainsKey(c)).ToList();
                    Result("colonists take the whole block up", left.Count == 0, $"after {ticksTotal} ticks; left {string.Join(" ", left)}");
                    // peel order: removal ticks never go up with distance
                    int inversions = 0;
                    string firstInv = "";
                    foreach (IntVec3 a in zOrder)
                    {
                        foreach (IntVec3 b in zOrder)
                        {
                            if (zRemovedAt[a] < zRemovedAt[b] && zDist[a] < zDist[b])
                            {
                                if (inversions++ == 0)
                                {
                                    firstInv = $"{a} (d {zDist[a]}, t {zRemovedAt[a]}) before {b} (d {zDist[b]}, t {zRemovedAt[b]})";
                                }
                            }
                        }
                    }
                    Result("peel order: farthest from the stairs first (no nearer cell before a farther one)", inversions == 0 && zOrder.Count == zDist.Count,
                        $"order (distance@tick): {string.Join(" ", zOrder.Select(c => zDist[c] + "@" + zRemovedAt[c]))}; inversions {inversions} {firstInv}");
                    Result("workers stand one step nearer the stairs, never on the cell being taken up", zWorkChecks > 0 && zWorkBad == 0, $"{zWorkChecks} checks, {zWorkBad} bad {zWorkBadLine}");
                    Result("carpeted slab: the carpet first (remove-floor job), then the slab, one designation", zCarpetFloorJobs > 0 && zCarpetSlabJobs > 0, $"floor-job checks {zCarpetFloorJobs}, slab-job checks {zCarpetSlabJobs}");
                    int wood = CountOn(map, ThingDefOf.WoodLog, 1) - zWoodBefore;
                    int cloth = CountOn(map, ThingDefOf.Cloth, 1) - zClothBefore;
                    int woodLow = CountOn(map, ThingDefOf.WoodLog, 0);
                    List<IntVec3> belowCells = zDist.Keys.Select(c => FloorRoof.Below(c, map)).ToList();
                    Room room = ProjectZ.GroundCellOf(zDist.Keys.First(), map).GetRoom(map);
                    if (Gravity.Off)
                    {
                        Result("half the materials back, on floor 2 (2 wood a slab, the carpet's cloth)", wood == 2 * zDist.Count && cloth >= 3,
                            $"wood +{wood} (want {2 * zDist.Count}), cloth +{cloth}; wood on the ground {woodLow}");
                        Result("roof below a plain roof again, the room below still indoors", belowCells.All(c => map.roofGrid.RoofAt(c) == RoofDefOf.RoofConstructed) && room != null && !room.PsychologicallyOutdoors && room.OpenRoofCount == 0,
                            $"roofs {string.Join(",", belowCells.Select(c => map.roofGrid.RoofAt(c)?.defName ?? "none").Distinct())}; room {room?.ID} outdoors {room?.PsychologicallyOutdoors} open roof {room?.OpenRoofCount}");
                    }
                    else
                    {
                        // compat G2 (user 2026-10-18): what a removed slab leaves falls, breaking the ordinary roof it passes
                        int woodDown = woodLow - zWoodLowBefore;
                        int clothDown = CountOn(map, ThingDefOf.Cloth, 0) - zClothLowBefore;
                        Result("half the materials back, fallen to the ground below (2 wood a slab, the carpet's cloth)", wood == 0 && woodDown == 2 * zDist.Count && cloth + clothDown >= 3,
                            $"wood on the ground +{woodDown} (want {2 * zDist.Count}), on floor 2 +{wood}; cloth on the ground +{clothDown}, on floor 2 +{cloth}");
                        Result("the roof below (an ordinary roof again) broke where the materials fell through", belowCells.All(c => !map.roofGrid.Roofed(c)),
                            $"roofs {string.Join(",", belowCells.Select(c => map.roofGrid.RoofAt(c)?.defName ?? "none").Distinct())}; room {room?.ID} open roof {room?.OpenRoofCount}; gravity {Gravity.Counters}");
                    }
                    Result("filth on a removed cell gone", !zFilth.GetThingList(map).Any(t => t is Filth), $"{zFilth}: {string.Join(",", zFilth.GetThingList(map).Select(t => t.def.defName))}");
                    if (Gravity.Off)
                    {
                        Result("nobody and nothing on open air; the drafted colonist and the steel on floor 2",
                            !OnOpenAir(map) && ProjectZ.FloorOf(zSitter.Position, map) == 1 && zSitter.Position.Standable(map) && CountOn(map, ThingDefOf.Steel, 1) == zSteelTotal,
                            $"{zSitter} at {zSitter.Position}; steel on floor 2 {CountOn(map, ThingDefOf.Steel, 1)}/{zSteelTotal}");
                    }
                    else
                    {
                        Result("nobody and nothing on open air; the drafted colonist and the steel fell to the ground (bruised)",
                            !OnOpenAir(map) && ProjectZ.FloorOf(zSitter.Position, map) == 0 && zSitter.Position.Standable(map) && zSteel.Spawned && ProjectZ.FloorOf(zSteel.Position, map) == 0 && Injuries(zSitter) > 0,
                            $"{zSitter} at {zSitter.Position} (injuries {InjuryNames(zSitter)}); steel at {zSteel.PositionHeld}");
                    }
                    Result("the island nobody reaches is never worked", Slab.At(zIsland, map) && zIslandJobs == 0 && map.designationManager.DesignationAt(zIsland, DesignationDefOf.RemoveFoundation) != null,
                        $"{zIsland}: {zIsland.GetTerrain(map).defName}, jobs seen {zIslandJobs}");
                    Draft(zSitter, false);
                    ShotAt(map, 1, zHouse.CenterCell + new IntVec3(2, 0, 0), 8f);
                    Wait(30, "z_peeled_shot");
                    return true;
                }
                case "z_peeled_shot":
                    ZCapture("slab_removed_f2");
                    stage = "z_peeled_shot2";
                    return true;
                case "z_peeled_shot2":
                    ShotAt(map, 0, zHouse.CenterCell + new IntVec3(2, 0, 0), 8f, roofs: true);
                    Wait(30, "z_peeled_shot2_take");
                    return true;
                case "z_peeled_shot2_take":
                    ZCapture("slab_removed_f1_roofs");
                    Wait(2, "z_tplace");
                    return true;

                // ---- Z3: stairs in the open field
                case "z_tplace":
                {
                    ShotAt(map, 0, zStairsT, 8f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Designator_Build d = BuildCopyCommandUtility.FindAllowedDesignator(PZDefOf.PZ_Stairs);
                    d?.SetStuffDef(ThingDefOf.WoodLog);
                    zTool = d;
                    Find.DesignatorManager.Select(d);
                    Wait(5, "z_tplace2");
                    return true;
                }
                case "z_tplace2":
                {
                    SetRot(zTool, Rot4.North);
                    IntVec3 below = FloorRoof.Below(StairShape.Exit(zStairsT, Rot4.North, map), map);
                    AcceptanceReport r = UiDesignate(ScreenOf(zStairsT, 0), ScreenOf(zStairsT, 0));
                    Find.DesignatorManager.Deselect();
                    Thing site = SiteAt(map, zStairsT);
                    Result("stairs in an open field (no roof under the exit, nothing holding one): placed", r.Accepted && site != null && !map.roofGrid.Roofed(below),
                        $"'{r.Reason}', site {site} at {site?.Position}; cell under the exit {below} roofed {map.roofGrid.Roofed(below)}");
                    SetWork(map, true);
                    TickUntil(40000, () => zStairsT.GetFirstThing<Building_Stairs>(map) != null, "z_tbuilt", 50);
                    return true;
                }
                case "z_tbuilt":
                {
                    tickWatch = null;
                    zT = zStairsT.GetFirstThing<Building_Stairs>(map);
                    IntVec3 top = zT?.TopCell ?? IntVec3.Invalid;
                    IntVec3 below = top.IsValid ? FloorRoof.Below(top, map) : IntVec3.Invalid;
                    Result("colonists build them; the landing comes with them, the roof under it too (held by the stairs)",
                        zT != null && Slab.At(top, map) && map.roofGrid.RoofAt(below) == PZDefOf.PZ_FloorRoof && SlabSupport.Supported(below, map) && !SlabSupport.Supported(below, map, ignore: zT) && StairGrid.For(map).links.Any(l => l.stairs == zT),
                        $"{zT} after {ticksTotal} ticks; landing {top} {(top.IsValid ? top.GetTerrain(map).defName : "-")}; roof under it {(below.IsValid ? map.roofGrid.RoofAt(below)?.defName : "-")}");
                    Result("the landing can't be taken up alone", top.IsValid && Slab.Blocker(top, map) == "PZ_StairsLandHere".Translate(), $"{top}: '{(top.IsValid ? Slab.Blocker(top, map) : "-")}'");
                    SetWork(map, false);
                    zClimber = Colonists(map).First(p => p != zSitter);
                    Teleport(zClimber, zStairsT + new IntVec3(0, 0, -3));
                    GoTo(zClimber, top);
                    TickUntil(3000, () => zClimber.Position == top, "z_tclimbed", 1);
                    return true;
                }
                case "z_tclimbed":
                {
                    tickWatch = null;
                    Result("a colonist walks up the open-field stairs onto the landing", zClimber.Position == zT.TopCell, $"{zClimber} at {zClimber.Position} after {ticksTotal} ticks");
                    zShot = 0;
                    Wait(2, "z_tshot");
                    return true;
                }
                case "z_tshot":
                {
                    // the landing up close: open-field stairs T and in-room stairs S, two turns and both sides each, and T
                    // from the ground view (the floor above hidden)
                    if (zShot >= ZShots.Length)
                    {
                        IsoRimBridge.SetOption("freeYaw", zOldYaw);
                        IsoRimBridge.SetOption("isoPitch", zOldPitch);
                        Find.CameraDriver.config.sizeRange.min = zOldMin;
                        Wait(5, "z_tdecon");
                        return true;
                    }
                    if (zShot == 0)
                    {
                        zOldYaw = (float)IsoRimBridge.SetOption("freeYaw", 0f);
                        zOldPitch = (float)IsoRimBridge.SetOption("isoPitch", 35.3f);
                        zOldMin = Find.CameraDriver.config.sizeRange.min;
                        Find.CameraDriver.config.sizeRange.min = 3f;
                    }
                    var s = ZShots[zShot];
                    IsoRimBridge.SetOption("freeYaw", s.yaw);
                    IsoRimBridge.SetOption("isoPitch", s.pitch > 0f ? s.pitch : zOldPitch);
                    IntVec3 at = s.inRoom ? StairShape.High(zStairsS, Rot4.North) : StairShape.High(zStairsT, Rot4.North);
                    ShotAt(map, s.floor, at, 4.5f);
                    Wait(40, "z_tshot_take");
                    return true;
                }
                case "z_tshot_take":
                    ZCapture(ZShots[zShot].name);
                    zShot++;
                    Wait(2, "z_tshot");
                    return true;
                case "z_tdecon":
                {
                    // the climber stays drafted on the landing; the stairs are deconstructed through the designator
                    Draft(zClimber, true);
                    Designator_Deconstruct dd = new Designator_Deconstruct();
                    AcceptanceReport ok = dd.CanDesignateThing(zT);
                    if (ok.Accepted)
                    {
                        dd.DesignateThing(zT);
                    }
                    Info($"deconstruct designated on {zT}: {ok.Accepted} '{ok.Reason}'");
                    zWoodBefore = CountOn(map, ThingDefOf.WoodLog, 0);
                    SetWork(map, true);
                    TickUntil(20000, () => !zT.Spawned, "z_tgone", 20);
                    return true;
                }
                case "z_tgone":
                {
                    tickWatch = null;
                    IntVec3 top = StairShape.Exit(zStairsT, Rot4.North, map);
                    IntVec3 below = FloorRoof.Below(top, map);
                    Result("deconstructed: the landing goes with the stairs, the roof they brought under it too",
                        !zT.Spawned && top.GetTerrain(map) == PZDefOf.PZ_OpenAir && !map.roofGrid.Roofed(below) && StairGrid.For(map)?.links.Any(l => l.stairs == zT) != true,
                        $"after {ticksTotal} ticks; landing {top} {top.GetTerrain(map).defName}; roof under it {map.roofGrid.RoofAt(below)?.defName ?? "none"}; wood on the ground +{CountOn(map, ThingDefOf.WoodLog, 0) - zWoodBefore}");
                    Result(Gravity.Off ? "the drafted colonist on the landing moved to the nearest floor cell, not left on open air" : "the drafted colonist on the landing fell to the floor below (compat G2), not left on open air",
                        zClimber.Spawned && zClimber.Position.Standable(map) && zClimber.Position.GetTerrain(map) != PZDefOf.PZ_OpenAir && (Gravity.Off || ProjectZ.FloorOf(zClimber.Position, map) == 0), $"{zClimber} at {zClimber.Position} (floor {ProjectZ.FloorOf(zClimber.Position, map) + 1}, {zClimber.Position.GetTerrain(map).defName})");
                    Draft(zClimber, false);
                    SetWork(map, false);
                    Wait(5, "z_collapse");
                    return true;
                }

                // ---- Z4: the pavilion
                case "z_collapse":
                {
                    zOnSlab = Colonists(map).First(p => !p.Downed);
                    zBelow = Colonists(map).First(p => p != zOnSlab && !p.Downed);
                    Heal(zOnSlab);
                    Heal(zBelow);
                    Teleport(zOnSlab, UpS(zColumn + new IntVec3(1, 0, 1), map));
                    Teleport(zBelow, zColumn + new IntVec3(-1, 0, -1));
                    Draft(zOnSlab, true);
                    Draft(zBelow, true);
                    zPSteel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    zPSteel.stackCount = 25;
                    GenSpawn.Spawn(zPSteel, UpS(zColumn + new IntVec3(-1, 0, 1), map), map);
                    zPTable = Place(DefDatabase<ThingDef>.GetNamed("EndTable"), ThingDefOf.WoodLog, UpS(zColumn + new IntVec3(1, 0, -1), map), map);
                    zHits.Clear();
                    zInjOnSlab = Injuries(zOnSlab);
                    zInjBelow = Injuries(zBelow);
                    ShotAt(map, 1, zColumn, 6f);
                    Wait(30, "z_collapse_shot");
                    return true;
                }
                case "z_collapse_shot":
                    ZCapture("collapse_before_f2");
                    stage = "z_collapse_go";
                    return true;
                case "z_collapse_go":
                {
                    Thing col = zColumn.GetEdifice(map);
                    Info($"the column {col} at {zColumn} is destroyed; slab cells {zPavilion.Cells.Count(g => Slab.At(UpS(g, map), map))}, roofs {zPavilion.Cells.Count(g => map.roofGrid.RoofAt(g) == PZDefOf.PZ_FloorRoof)}");
                    zLetters = Find.LetterStack.LettersListForReading.Count;
                    zCollapses = PZStats.slabCollapses;
                    col?.Kill();
                    StartTicks(120, "z_collapsed");
                    return true;
                }
                case "z_collapsed":
                {
                    int left = zPavilion.Cells.Count(g => Slab.At(UpS(g, map), map));
                    int roofs = zPavilion.Cells.Count(g => map.roofGrid.Roofed(g));
                    Result("the column destroyed: the roof under the slab collapses, the slab with it", left == 0 && roofs == 0 && PZStats.slabCollapses - zCollapses == zPavilion.Area,
                        $"slab cells left {left}, roofs left {roofs}, collapses {PZStats.slabCollapses - zCollapses}");
                    Result("the colonist on the slab fell to the ground: the collapse crush (as everyone under it) and, alive, the fall bruises",
                        ProjectZ.FloorOf(zOnSlab.PositionHeld, map) == 0 && ZHits(zOnSlab, "Crush/Collapse") > 0 && (zOnSlab.Dead || ZHits(zOnSlab, "Blunt/") >= 2),
                        $"{zOnSlab} at {zOnSlab.PositionHeld} (floor {ProjectZ.FloorOf(zOnSlab.PositionHeld, map) + 1}), dead {zOnSlab.Dead}, hits {ZHitList(zOnSlab)}, injuries {zInjOnSlab} -> {Injuries(zOnSlab)}: {InjuryNames(zOnSlab)}");
                    Result("the colonist under it took the game's collapse crush", ZHits(zBelow, "Crush/Collapse") > 0, $"{zBelow} at {zBelow.PositionHeld}, hits {ZHitList(zBelow)}, injuries {zInjBelow} -> {Injuries(zBelow)}: {InjuryNames(zBelow)}");
                    Result("the steel fell to the ground, the table was destroyed (its leavings fell too)",
                        zPSteel.Spawned && ProjectZ.FloorOf(zPSteel.Position, map) == 0 && zPTable.Destroyed && !OnOpenAir(map),
                        $"steel {zPSteel.Position} spawned {zPSteel.Spawned} x{zPSteel.stackCount}; table destroyed {zPTable.Destroyed}; on the ground around: {string.Join(",", zPavilion.ExpandedBy(2).Cells.SelectMany(g => g.GetThingList(map)).Where(t => t.def.category == ThingCategory.Item).Select(t => t.def.defName + "x" + t.stackCount))}");
                    Result("the game's 'roof collapsed' letter names the crushed", Find.LetterStack.LettersListForReading.Count > zLetters,
                        string.Join(" | ", Find.LetterStack.LettersListForReading.Skip(zLetters).Select(l => l.Label.ToString())));
                    Result("a slab nobody holds up (as in an older save) stays until a support near it goes, as a vanilla roof", Slab.At(zLegacy, map) && map.roofGrid.RoofAt(FloorRoof.Below(zLegacy, map)) == PZDefOf.PZ_FloorRoof && !SlabSupport.Supported(FloorRoof.Below(zLegacy, map), map),
                        $"{zLegacy}: {zLegacy.GetTerrain(map).defName}, roof below {map.roofGrid.RoofAt(FloorRoof.Below(zLegacy, map))?.defName}");
                    Draft(zOnSlab, false);
                    Draft(zBelow, false);
                    ShotAt(map, 0, zColumn, 6f);
                    Wait(30, "z_collapsed_shot");
                    return true;
                }
                case "z_collapsed_shot":
                    ZCapture("collapse_after_f1");
                    Wait(2, "z_end");
                    return true;
                case "z_end":
                    Finish();
                    return true;
            }
            return false;
        }

        private static int zShot, zLetters, zCollapses;

        private static readonly (string name, bool inRoom, int floor, float yaw, float pitch)[] ZShots =
        {
            ("landing_openfield_yaw45", false, 1, 0f, -1f), ("landing_openfield_yaw225", false, 1, 180f, -1f),
            ("landing_openfield_side_from_west", false, 1, 45f, 20f), ("landing_openfield_side_from_east", false, 1, 225f, 20f),
            ("landing_openfield_groundview", false, 0, 0f, -1f),
            ("landing_inroom_yaw45", true, 1, 0f, -1f), ("landing_inroom_yaw225", true, 1, 180f, -1f),
            ("landing_inroom_side_from_west", true, 1, 45f, 20f), ("landing_inroom_side_from_east", true, 1, 225f, 20f),
        };
        private static float zOldYaw, zOldPitch, zOldMin;

        private static void SetupSlabRemove(Map map)
        {
            if (!zPatched)
            {
                zPatched = true;
                new Harmony("Peren.IsoRimProjectZ.test.slabremove").Patch(AccessTools.Method(typeof(Thing), nameof(Thing.TakeDamage)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(ZTakeDamagePrefix)));
            }
            List<Pawn> cols = Colonists(map);
            IntVec3 near = cols.Count > 0 ? new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z)) : map.Center;
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 79f, true))
            {
                CellRect r = new CellRect(c.x - 19, c.z - 6, 40, 13);
                CellRect m = r.ExpandedBy(3);
                if (!m.InBounds(map) || m.maxZ >= ProjectZ.GroundZ(map) - 12)
                {
                    continue;
                }
                if (m.Cells.All(x => !x.GetTerrain(map).IsWater && !map.roofGrid.Roofed(x) && !x.Fogged(map) && !(x.GetEdifice(map)?.def.building?.isNaturalRock ?? false) && x.GetTerrain(map).passability == Traversability.Standable))
                {
                    found = r;
                    break;
                }
            }
            if (found.Area == 0)
            {
                Result("test spot", false, "no open ground near the colonists");
                Finish();
                return;
            }
            foreach (IntVec3 c in found.ExpandedBy(3))
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable && (t is Plant || t.def.category == ThingCategory.Item || t is Filth || t is Building))
                    {
                        t.Destroy();
                    }
                }
            }
            DebugSettings.enableRandomMentalStates = false;
            Find.PlaySettings.autoRebuild = false;
            zO = new IntVec3(found.minX, 0, found.minZ);
            // House H: walls, door south, roof, slab over the inside except the stairwell; stairs S up to the north.
            zHouse = new CellRect(zO.x, zO.z, 13, 10);
            IntVec3 door = Z(6, 0);
            foreach (IntVec3 c in zHouse.EdgeCells)
            {
                Place(c == door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            foreach (IntVec3 c in zHouse)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                map.areaManager.Home[c] = true;
            }
            zStairsS = Z(2, 3);
            HashSet<IntVec3> well = new HashSet<IntVec3>(StairShape.Footprint(zStairsS, Rot4.North).Cells);
            foreach (IntVec3 g in zHouse.ContractedBy(1))
            {
                if (!well.Contains(g))
                {
                    map.terrainGrid.SetTerrain(UpS(g, map), PZDefOf.PZ_UpperFloor);
                }
            }
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, zStairsS, map, Rot4.North);
            // The block at the east end, the island in the north-east corner (its two neighbours open air).
            zBlock = new CellRect(zO.x + 7, zO.z + 2, 4, 5);
            zIsland = UpS(Z(11, 8), map);
            map.terrainGrid.SetTerrain(UpS(Z(10, 8), map), PZDefOf.PZ_OpenAir);
            map.terrainGrid.SetTerrain(UpS(Z(11, 7), map), PZDefOf.PZ_OpenAir);
            map.terrainGrid.SetTerrain(UpS(Z(10, 7), map), PZDefOf.PZ_OpenAir);
            zCarpet = UpS(Z(8, 4), map);
            TerrainDef carpet = DefDatabase<TerrainDef>.AllDefs.Where(t => t.layerable && t.costList != null && t.costList.Any(c => c.thingDef == ThingDefOf.Cloth)).OrderBy(t => t.defName).FirstOrDefault();
            Info($"carpet: {carpet?.defName}");
            map.terrainGrid.SetTerrain(zCarpet, carpet);
            zFilth = UpS(Z(9, 3), map);
            FilthMaker.TryMakeFilth(zFilth, map, ThingDefOf.Filth_Dirt, 3);
            zSitterAt = UpS(Z(10, 6), map);
            zSteelAt = UpS(Z(9, 5), map);
            zSitter = cols[0];
            Teleport(zSitter, zSitterAt);
            Draft(zSitter, true);
            zSteel = ThingMaker.MakeThing(ThingDefOf.Steel);
            zSteel.stackCount = 30;
            GenSpawn.Spawn(zSteel, zSteelAt, map);
            zSteelTotal = CountOn(map, ThingDefOf.Steel, 1);
            // Blockers on the slab: a wall, a table, a wall blueprint; a floor-3 slab over one cell.
            zWall = UpS(Z(5, 2), map);
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, zWall, map);
            zTable = UpS(Z(4, 7), map);
            Place(DefDatabase<ThingDef>.GetNamed("EndTable"), ThingDefOf.WoodLog, zTable, map);
            zWallBp = UpS(Z(6, 7), map);
            GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, zWallBp, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
            zA3 = UpS(Z(5, 7), map);
            IntVec3 f3 = ProjectZ.CellOnFloor(Z(5, 7), 2, map);
            if (f3.IsValid)
            {
                map.terrainGrid.SetTerrain(f3, PZDefOf.PZ_UpperFloor);
            }
            else
            {
                Info("no floor 3 on this map (run with -projectzfloors=3): the slab-above refusal can't be shown");
            }
            // Open-field stairs T, the pavilion P (a column holding a 5 x 5 roof with a slab on it), a slab no one holds up.
            zStairsT = Z(20, 3);
            zColumn = Z(32, 6);
            zPavilion = CellRect.CenteredOn(zColumn, 2);
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, zColumn, map);
            foreach (IntVec3 g in zPavilion)
            {
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(UpS(g, map), PZDefOf.PZ_UpperFloor);
            }
            zLegacy = UpS(Z(16, 10), map);
            map.terrainGrid.SetTerrain(zLegacy, PZDefOf.PZ_UpperFloor);
            // Wood for the stairs and nothing else to do.
            for (int i = 0; i < 2; i++)
            {
                Thing w = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                w.stackCount = 75;
                GenSpawn.Spawn(w, Z(24 + i, 1), map);
            }
            SetWork(map, false);
            for (int i = 1; i < cols.Count; i++)
            {
                Teleport(cols[i], Z(14 + i, 1));
            }
            Info($"slab removal: house {zHouse}, stairs S {zStairsS} (landing {StairShape.Exit(zStairsS, Rot4.North, map)}), block {zBlock}, island {zIsland}, carpet {zCarpet}, wall {zWall}, table {zTable}, wall blueprint {zWallBp}, slab above {zA3}; open-field stairs {zStairsT}; pavilion {zPavilion} column {zColumn}; legacy slab {zLegacy}; floors {ProjectZ.Annex(map).floors}");
            AnnexWatch.Reset();
            Wait(5, "z_view");
        }
    }
}
