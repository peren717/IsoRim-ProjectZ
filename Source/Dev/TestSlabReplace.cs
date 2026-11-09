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
    // Mode slabreplace (SlabReplace, user 2026-11-09; run with -projectzseed=7): a slab blueprint over a slab of another
    // stuff swaps it in one step, no refund, never open air. Real flows: blueprints placed with the build menu's
    // designators, colonists (construction skill 20: no botches) deliver and build with ticks, the vanilla cancel and
    // deconstruct designators, a save and load in the middle. House H (30 x 10, roofed, wood slab over its inside, stairs
    // up to it); each check has its own cells:
    //   W1 placement: the same slab refused (bare, and under a carpet); a carpet on a damaged slab refused; open air keeps
    //      its rules (held: accepted; nothing near: no support; the ground: view the floor above);
    //   W2 wood -> steel (a drafted colonist and silver on it), steel -> granite (an end table and silver on it), granite
    //      over a carpeted steel slab (only the slab under the carpet changes), damaged granite -> granite (repair),
    //      damaged steel -> wood; watched every tick (and inside each tick by hooks): never open air, no slab removal or
    //      move-off, no floor taken up first, no leavings, the roof below stays the floor roof, nothing on the cell or under
    //      it moves or is hurt, no rubble; materials: exactly 4 of the new stuff per slab, nothing of the old back;
    //   W3 a blueprint cancelled; a frame cancelled and a frame deconstructed half built: the old slab stays;
    //   W4 a half-built frame over a carpet and a blueprint saved and loaded, then finished as replacements;
    //   W5 regressions: a carpet laid on a slab (the slab under it), a slab taken up (open air, no refund, silver falls);
    //   W6 showcase: a 4 x 4 wood slab with an end table, a chair, silver and a drafted colonist, a bed and a table in the
    //      room below, replaced by steel; shots before and after (floor 2 and the room below).
    public static partial class TestRunner
    {
        private static IntVec3 swO;
        private static CellRect swHouse, swShow;
        private static IntVec3 swA, swB, swC, swD, swE, swF, swG, swK, swH, swL, swI, swJ, swStairs;
        private static TerrainDef swCarpet, swWood, swSteel, swGranite;
        private static ThingDef swGraniteBlocks;
        private static Pawn swP;
        private static string swPId;
        private static readonly HashSet<IntVec3> swWatch = new HashSet<IntVec3>();
        private static readonly HashSet<IntVec3> swWatchBelow = new HashSet<IntVec3>();
        private static readonly HashSet<IntVec3> swCarpets = new HashSet<IntVec3>();
        private static readonly Dictionary<Thing, IntVec3> swFixed = new Dictionary<Thing, IntVec3>();
        private static readonly Dictionary<Thing, int> swFixedHp = new Dictionary<Thing, int>();
        private static readonly Dictionary<IntVec3, TerrainDef> swWant = new Dictionary<IntVec3, TerrainDef>();
        private static readonly Dictionary<ThingDef, int> swTotals = new Dictionary<ThingDef, int>();
        private static readonly List<string> swViolLog = new List<string>();
        private static int swViol, swLeavings, swTicks, swReplacedAt, swUnderAt, swRubbleAt, swShot;
        private static float swSavedWork;
        private static bool swPatched;

        private static IntVec3 SW(int dx, int dz) => new IntVec3(swO.x + dx, 0, swO.z + dz);

        private static void SwViol(string what)
        {
            swViol++;
            if (swViolLog.Count < 12)
            {
                swViolLog.Add($"t{Find.TickManager?.TicksGame}: {what}");
            }
        }

        private static string SwViols() => swViol == 0 ? "none" : swViol + ": " + string.Join("; ", swViolLog);

        // ---- in-tick hooks (only on the watched cells)
        private static void SwRemovePrefix(IntVec3 c)
        {
            if (swWatch.Contains(c))
            {
                SwViol("Slab.Remove " + c);
            }
        }

        private static void SwMoveOffPrefix(IntVec3 c)
        {
            if (swWatch.Contains(c))
            {
                SwViol("Slab.MoveOff " + c);
            }
        }

        private static void SwCollapsePrefix(IntVec3 below)
        {
            if (swWatchBelow.Contains(below))
            {
                SwViol("SlabCollapse " + below);
            }
        }

        private static void SwSetTerrainPrefix(IntVec3 c, TerrainDef newTerr)
        {
            if (newTerr == PZDefOf.PZ_OpenAir && swWatch.Contains(c))
            {
                SwViol("SetTerrain open air " + c);
            }
        }

        private static void SwRemoveTopPrefix(IntVec3 c)
        {
            if (swWatch.Contains(c))
            {
                SwViol("RemoveTopLayer " + c);
            }
        }

        private static void SwLeavingsPrefix(TerrainDef terrain, IntVec3 cell)
        {
            if (swWatch.Contains(cell))
            {
                swLeavings++;
                SwViol("leavings of " + terrain?.defName + " at " + cell);
            }
        }

        private static void SwSetRoofPostfix(RoofGrid __instance, IntVec3 c)
        {
            if (swWatchBelow.Contains(c) && __instance.RoofAt(c) != PZDefOf.PZ_FloorRoof)
            {
                SwViol("roof below " + c + " -> " + (__instance.RoofAt(c)?.defName ?? "none"));
            }
        }

        private static void SwPatch()
        {
            if (swPatched)
            {
                return;
            }
            swPatched = true;
            Harmony h = new Harmony("Peren.IsoRimProjectZ.test.slabreplace");
            HarmonyMethod Pre(string n) => new HarmonyMethod(typeof(TestRunner), n);
            h.Patch(AccessTools.Method(typeof(Slab), nameof(Slab.Remove)), prefix: Pre(nameof(SwRemovePrefix)));
            h.Patch(AccessTools.Method(typeof(Slab), nameof(Slab.MoveOff)), prefix: Pre(nameof(SwMoveOffPrefix)));
            h.Patch(AccessTools.Method(typeof(SlabCollapse), nameof(SlabCollapse.Collapse)), prefix: Pre(nameof(SwCollapsePrefix)));
            h.Patch(AccessTools.Method(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain)), prefix: Pre(nameof(SwSetTerrainPrefix)));
            h.Patch(AccessTools.Method(typeof(TerrainGrid), nameof(TerrainGrid.RemoveTopLayer)), prefix: Pre(nameof(SwRemoveTopPrefix)));
            h.Patch(AccessTools.Method(typeof(GenLeaving), nameof(GenLeaving.DoLeavingsFor), new[] { typeof(TerrainDef), typeof(IntVec3), typeof(Map) }), prefix: Pre(nameof(SwLeavingsPrefix)));
            h.Patch(AccessTools.Method(typeof(RoofGrid), nameof(RoofGrid.SetRoof)), postfix: Pre(nameof(SwSetRoofPostfix)));
        }

        // ---- every tick while watching
        private static void SwTick(Map map)
        {
            KeepFed(map);
            swTicks++;
            // (the colonist standing on the cells stays drafted: vanilla undrafts an idle drafted pawn after a while)
            if (swP != null && swP.Spawned && !swP.Drafted && swFixed.ContainsKey(swP))
            {
                Draft(swP, true);
            }
            foreach (IntVec3 c in swWatch)
            {
                if (c.GetTerrain(map) == PZDefOf.PZ_OpenAir || !ProjectZ.HasFloor(c, map) || !Slab.At(c, map))
                {
                    SwViol("no slab at " + c + ": " + c.GetTerrain(map).defName);
                }
                IntVec3 b = FloorRoof.Below(c, map);
                if (map.roofGrid.RoofAt(b) != PZDefOf.PZ_FloorRoof)
                {
                    SwViol("roof below " + c + ": " + (map.roofGrid.RoofAt(b)?.defName ?? "none"));
                }
            }
            foreach (IntVec3 c in swCarpets)
            {
                if (map.terrainGrid.TopTerrainAt(c) != swCarpet)
                {
                    SwViol("carpet gone at " + c + ": " + map.terrainGrid.TopTerrainAt(c).defName);
                }
            }
            foreach (KeyValuePair<Thing, IntVec3> kv in swFixed.ToList())
            {
                Thing t = kv.Key;
                if (!t.Spawned || t.Position != kv.Value)
                {
                    SwViol($"{t.LabelShort} moved: {kv.Value} -> {(t.Spawned ? t.Position.ToString() : "despawned")}{(t is Pawn mp ? $" (drafted {mp.Drafted}, job {mp.CurJob?.def.defName} {mp.CurJob?.targetA})" : "")}");
                    swFixed[t] = t.Spawned ? t.Position : kv.Value;
                }
                else if (swFixedHp.TryGetValue(t, out int hp) && !(t is Pawn) && t.HitPoints < hp)
                {
                    SwViol($"{t.LabelShort} hurt: {hp} -> {t.HitPoints}");
                    swFixedHp[t] = t.HitPoints;
                }
            }
            if (swP != null && swP.Spawned && swP.health.hediffSet.hediffs.Any(x => x is Hediff_Injury))
            {
                SwViol(swP.LabelShort + " hurt");
                Heal(swP);
            }
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                Job j = p.CurJob;
                if (j != null && (j.def == JobDefOf.RemoveFloor || j.def == JobDefOf.RemoveFoundation) && j.targetA.IsValid && swWatch.Contains(j.targetA.Cell))
                {
                    SwViol($"{p.LabelShort} {j.def.defName} on {j.targetA.Cell}");
                }
            }
        }

        private static void SwWatch(Map map, IEnumerable<IntVec3> cells, bool clear = true)
        {
            if (clear)
            {
                swWatch.Clear();
                swWatchBelow.Clear();
                swCarpets.Clear();
                swFixed.Clear();
                swFixedHp.Clear();
            }
            foreach (IntVec3 c in cells)
            {
                swWatch.Add(c);
                swWatchBelow.Add(FloorRoof.Below(c, map));
                if (map.terrainGrid.TopTerrainAt(c) == swCarpet)
                {
                    swCarpets.Add(c);
                }
                foreach (IntVec3 x in new[] { c, FloorRoof.Below(c, map) })
                {
                    foreach (Thing t in x.GetThingList(map))
                    {
                        if (t is Pawn || t.def.category == ThingCategory.Item || t is Building && !(t is Frame))
                        {
                            swFixed[t] = t.Position;
                            swFixedHp[t] = t.HitPoints;
                        }
                    }
                }
            }
        }

        private static int SwRubble(Map map) => map.listerThings.AllThings.Count(t => t.Spawned && swHouse.ExpandedBy(1).Contains(t.Position) && (t.def.defName.Contains("Rubble") || t.def.thingCategories != null && t.def.thingCategories.Contains(ThingCategoryDefOf.Chunks)));

        private static int SwTotal(Map map, ThingDef d) => map.listerThings.ThingsOfDef(d).Where(t => t.Spawned).Sum(t => t.stackCount) + map.mapPawns.AllPawnsSpawned.Sum(p => p.carryTracker?.CarriedThing?.def == d ? p.carryTracker.CarriedThing.stackCount : 0);

        private static void SwSnapTotals(Map map)
        {
            swTotals.Clear();
            foreach (ThingDef d in new[] { ThingDefOf.WoodLog, ThingDefOf.Steel, swGraniteBlocks, ThingDefOf.Cloth })
            {
                swTotals[d] = SwTotal(map, d);
            }
        }

        // The materials since the snapshot: per stuff now - before, with the cost of the slabs built (4 each).
        private static bool SwNoRefund(Map map, Dictionary<ThingDef, int> built, out string detail)
        {
            bool ok = true;
            List<string> parts = new List<string>();
            foreach (KeyValuePair<ThingDef, int> kv in swTotals)
            {
                int want = kv.Value - (built.TryGetValue(kv.Key, out int n) ? n * SlabDefs.Cost : 0);
                int now = SwTotal(map, kv.Key);
                parts.Add($"{kv.Key.defName} {kv.Value} -> {now} (want {want})");
                if (now != want)
                {
                    ok = false;
                }
            }
            detail = string.Join(", ", parts) + $"; botches {botches}";
            return ok && botches == 0;
        }

        private static Designator_Build SwDesignator(BuildableDef def) => BuildCopyCommandUtility.FindAllowedDesignator(def) ?? new Designator_Build(def);

        // A placement the player makes: the designator's own check, then its click.
        private static AcceptanceReport SwPlace(BuildableDef def, IntVec3 c)
        {
            Designator_Build d = SwDesignator(def);
            AcceptanceReport r = d.CanDesignateCell(c);
            if (r.Accepted)
            {
                d.DesignateSingleCell(c);
                d.Finalize(true);
            }
            return r;
        }

        private static Thing SwSite(Map map, IntVec3 c) => c.GetThingList(map).FirstOrDefault(t => (t is Blueprint || t is Frame) && SlabDefs.Is(t.def.entityDefToBuild));

        private static Frame SwFrame(Map map, IntVec3 c) => c.GetThingList(map).OfType<Frame>().FirstOrDefault(f => SlabDefs.Is(f.def.entityDefToBuild));

        private static string SwCell(Map map, IntVec3 c) => $"{c}: top {map.terrainGrid.TopTerrainAt(c).defName}, under {map.terrainGrid.UnderTerrainAt(c)?.defName ?? "-"}";

        private static bool StepSlabReplace(Map map)
        {
            switch (stage)
            {
                case "sw_setup":
                    SwSetup(map);
                    return true;

                // ---- W1 placement
                case "sw_refuse":
                {
                    string isA = "TerrainIsAlready".Translate(swWood.label);
                    AcceptanceReport r1 = SwDesignator(swWood).CanDesignateCell(swA);
                    Result("W1 the same slab is refused: wood on a bare wood slab", !r1.Accepted && r1.Reason == isA, $"'{r1.Reason}'");
                    AcceptanceReport r2 = SwDesignator(swSteel).CanDesignateCell(swC);
                    Result("W1 the same slab is refused: steel on a carpeted steel slab", !r2.Accepted && r2.Reason == "TerrainIsAlready".Translate(swSteel.label).ToString(), $"'{r2.Reason}'");
                    AcceptanceReport r3 = SwDesignator(swCarpet).CanDesignateCell(swD);
                    Result("W1 a carpet on a damaged slab is still refused", !r3.Accepted && r3.Reason == "PZ_DamagedSlabNoFloor".Translate().ToString(), $"'{r3.Reason}'");
                    AcceptanceReport[] ok = { SwDesignator(swSteel).CanDesignateCell(swA), SwDesignator(swGranite).CanDesignateCell(swB), SwDesignator(swGranite).CanDesignateCell(swC), SwDesignator(swGranite).CanDesignateCell(swD), SwDesignator(swWood).CanDesignateCell(swE) };
                    Result("W1 another slab is accepted over wood, steel, carpeted steel, damaged granite (its own stuff: repair), damaged steel", ok.All(x => x.Accepted), string.Join(" | ", ok.Select(x => x.Accepted ? "ok" : "'" + x.Reason + "'")));
                    IntVec3 held = UpS(SW(30, 4), map), far = UpS(SW(40, 4), map), ground = SW(30, 4);
                    AcceptanceReport o1 = SwDesignator(swSteel).CanDesignateCell(held), o2 = SwDesignator(swSteel).CanDesignateCell(far), o3 = SwDesignator(swSteel).CanDesignateCell(ground);
                    Result("W1 open air keeps its rules: next to the house wall accepted, far from any holder no support, the ground view the floor above",
                        o1.Accepted && !o2.Accepted && o2.Reason == "PZ_NoSupportBelow".Translate().ToString() && !o3.Accepted && o3.Reason == PlaceWorker_UpperFloor.ViewFloorAbove(),
                        $"held {held} '{(o1.Accepted ? "ok" : o1.Reason)}', far {far} '{o2.Reason}', ground {ground} '{o3.Reason}'");
                    Wait(5, "sw_place1");
                    return true;
                }

                // ---- W2 the replacements
                case "sw_place1":
                {
                    SwSnapTotals(map);
                    swWant.Clear();
                    swWant[swA] = swSteel;
                    swWant[swB] = swGranite;
                    swWant[swC] = swGranite;
                    swWant[swD] = swGranite;
                    swWant[swE] = swWood;
                    SwWatch(map, swWant.Keys);
                    List<string> placed = new List<string>();
                    foreach (KeyValuePair<IntVec3, TerrainDef> kv in swWant)
                    {
                        AcceptanceReport r = SwPlace(kv.Value, kv.Key);
                        placed.Add($"{kv.Key} {kv.Value.defName} {(r.Accepted && SwSite(map, kv.Key) != null ? "placed" : "NOT '" + r.Reason + "'")}");
                    }
                    Result("W2 five slab blueprints placed with the build menu's designators", swWant.Keys.All(c => SwSite(map, c) != null), string.Join("; ", placed));
                    swReplacedAt = SlabReplace.Replaced;
                    swUnderAt = SlabReplace.UnderSwaps;
                    swRubbleAt = SwRubble(map);
                    swViol = 0;
                    swViolLog.Clear();
                    swLeavings = 0;
                    swTicks = 0;
                    botches = 0;
                    SetWork(map, true);
                    SeedTicks("sw_place1");
                    TickUntil(40000, () =>
                    {
                        SwTick(map);
                        return swWant.All(kv => SlabDamage.SlabAt(kv.Key, map) == kv.Value && SwSite(map, kv.Key) == null);
                    }, "sw_done1", 1);
                    return true;
                }
                case "sw_done1":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    foreach (KeyValuePair<IntVec3, TerrainDef> kv in swWant)
                    {
                        TerrainGrid tg = map.terrainGrid;
                        bool carpet = kv.Key == swC;
                        bool ok = carpet ? tg.TopTerrainAt(kv.Key) == swCarpet && tg.UnderTerrainAt(kv.Key) == kv.Value : tg.TopTerrainAt(kv.Key) == kv.Value && tg.UnderTerrainAt(kv.Key) == null;
                        Result($"W2 {kv.Key} replaced by {kv.Value.defName}{(carpet ? " under the carpet, the carpet kept" : "")}, the old def gone", ok && SwSite(map, kv.Key) == null, SwCell(map, kv.Key));
                    }
                    Result("W2 never open air, no removal, move-off or collapse, no floor taken up first, the roof below kept, nothing moved or hurt (checked every tick and inside it)",
                        swViol == 0, $"{swTicks} ticks watched; violations {SwViols()}");
                    Dictionary<ThingDef, int> built = new Dictionary<ThingDef, int> { [ThingDefOf.Steel] = 1, [swGraniteBlocks] = 3, [ThingDefOf.WoodLog] = 1 };
                    bool noRefund = SwNoRefund(map, built, out string mat);
                    Result("W2 no refund: exactly 4 of the new stuff per slab used, nothing of the old slab back, no leavings", noRefund && swLeavings == 0, mat + $"; leavings calls {swLeavings}");
                    int rubble = SwRubble(map) - swRubbleAt;
                    Result("W2 the room below: no rubble, the things under the cells untouched", rubble == 0 && swViol == 0, $"rubble/chunks +{rubble}; roofs below {string.Join(",", swWant.Keys.Select(c => map.roofGrid.RoofAt(FloorRoof.Below(c, map))?.defName ?? "none").Distinct())}");
                    Result("W2 swaps counted: 5 replacements, 1 under a floor", SlabReplace.Replaced - swReplacedAt == 5 && SlabReplace.UnderSwaps - swUnderAt == 1, $"replaced {SlabReplace.Replaced - swReplacedAt}, under-layer swaps {SlabReplace.UnderSwaps - swUnderAt}");
                    Wait(5, "sw_cancel");
                    return true;
                }

                // ---- W3 cancelled
                case "sw_cancel":
                {
                    SwWatch(map, new[] { swF, swG, swK });
                    swViol = 0;
                    swViolLog.Clear();
                    SwSnapTotals(map);
                    AcceptanceReport r = SwPlace(swSteel, swF);
                    Thing bp = SwSite(map, swF);
                    Designator_Cancel cancel = new Designator_Cancel();
                    bool can = bp != null && cancel.CanDesignateThing(bp).Accepted;
                    if (can)
                    {
                        cancel.DesignateThing(bp);
                    }
                    Result("W3 a blueprint cancelled: the wood slab stays", r.Accepted && can && SwSite(map, swF) == null && map.terrainGrid.TopTerrainAt(swF) == swWood && swViol == 0, $"placed '{(r.Accepted ? "ok" : r.Reason)}', cancel {can}; {SwCell(map, swF)}; violations {SwViols()}");
                    SwPlace(swSteel, swG);
                    SetWork(map, true);
                    SeedTicks("sw_cancel");
                    TickUntil(20000, () =>
                    {
                        SwTick(map);
                        return SwFrame(map, swG)?.workDone > 0f || map.terrainGrid.TopTerrainAt(swG) != swWood;
                    }, "sw_cancel2", 1);
                    return true;
                }
                case "sw_cancel2":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    Frame f = SwFrame(map, swG);
                    float work = f?.workDone ?? 0f;
                    Designator_Cancel cancel = new Designator_Cancel();
                    bool can = f != null && cancel.CanDesignateThing(f).Accepted;
                    if (can)
                    {
                        cancel.DesignateThing(f);
                    }
                    StartTicks(300, "sw_cancel3");
                    tickWatch = () =>
                    {
                        SwTick(map);
                        return false;
                    };
                    watchEvery = 1;
                    Info($"W3 frame at {swG}: work {work:0} of {f?.WorkToBuild:0} cancelled {can}");
                    swSavedWork = can ? work : -1f;
                    return true;
                }
                case "sw_cancel3":
                {
                    tickWatch = null;
                    Result("W3 a half-built frame cancelled: the wood slab stays, nothing fell", swSavedWork > 0f && SwSite(map, swG) == null && map.terrainGrid.TopTerrainAt(swG) == swWood && swViol == 0,
                        $"work done {swSavedWork:0}; {SwCell(map, swG)}; violations {SwViols()}");
                    SwPlace(swSteel, swK);
                    SetWork(map, true);
                    SeedTicks("sw_decon");
                    TickUntil(20000, () =>
                    {
                        SwTick(map);
                        return SwFrame(map, swK)?.workDone > 0f || map.terrainGrid.TopTerrainAt(swK) != swWood;
                    }, "sw_decon2", 1);
                    return true;
                }
                case "sw_decon2":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    Frame f = SwFrame(map, swK);
                    float work = f?.workDone ?? 0f;
                    // (the game's deconstruct tool does not take frames: they are cancelled; a frame can still be destroyed,
                    // e.g. shot to pieces in a raid)
                    Designator_Deconstruct dd = new Designator_Deconstruct();
                    AcceptanceReport can = f != null ? dd.CanDesignateThing(f) : "no frame";
                    Result("W3 the deconstruct tool does not take a frame (vanilla: frames are cancelled)", f != null && !can.Accepted, $"'{can.Reason}'");
                    if (f != null)
                    {
                        f.Kill(new DamageInfo(DamageDefOf.Blunt, 99999f));
                    }
                    swSavedWork = f != null && f.Destroyed ? work : -1f;
                    Info($"W3 frame at {swK}: work {work:0}, destroyed (killed) {f?.Destroyed}");
                    StartTicks(300, "sw_decon3");
                    tickWatch = () =>
                    {
                        SwTick(map);
                        return false;
                    };
                    watchEvery = 1;
                    return true;
                }
                case "sw_decon3":
                {
                    tickWatch = null;
                    Result("W3 a half-built frame destroyed by damage: the wood slab stays, nothing fell", swSavedWork > 0f && SwSite(map, swK) == null && map.terrainGrid.TopTerrainAt(swK) == swWood && swViol == 0,
                        $"work done {swSavedWork:0}; {SwCell(map, swK)}; violations {SwViols()}");
                    Wait(5, "sw_save");
                    return true;
                }

                // ---- W4 save and load
                case "sw_save":
                {
                    SwWatch(map, new[] { swH, swL });
                    swViol = 0;
                    swViolLog.Clear();
                    SwPlace(swSteel, swH);
                    SetWork(map, true);
                    SeedTicks("sw_save");
                    TickUntil(20000, () =>
                    {
                        SwTick(map);
                        return SwFrame(map, swH)?.workDone > 0f || map.terrainGrid.UnderTerrainAt(swH) != swWood;
                    }, "sw_save2", 1);
                    return true;
                }
                case "sw_save2":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    Frame f = SwFrame(map, swH);
                    swSavedWork = f?.workDone ?? -1f;
                    AcceptanceReport r = SwPlace(swGranite, swL);
                    Info($"W4 before the save: frame at {swH} work {swSavedWork:0} of {f?.WorkToBuild:0}; blueprint at {swL} '{(r.Accepted ? "ok" : r.Reason)}'; {SwCell(map, swH)}");
                    swPId = swP?.ThingID;
                    GameDataSaveLoader.SaveGame("pz_slabreplace");
                    stage = "sw_loaded";
                    pendingLoad = "pz_slabreplace";
                    return true;
                }
                case "sw_loaded":
                {
                    swP = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.ThingID == swPId);
                    Frame f = SwFrame(map, swH);
                    Thing bp = SwSite(map, swL);
                    Result("W4 after the load: the half-built frame over the carpet and the blueprint are there, the old slabs too",
                        f != null && Mathf.Abs(f.workDone - swSavedWork) < 0.01f && bp is Blueprint && map.terrainGrid.TopTerrainAt(swH) == swCarpet && map.terrainGrid.UnderTerrainAt(swH) == swWood && map.terrainGrid.TopTerrainAt(swL) == swWood,
                        $"frame {f?.def.defName} work {f?.workDone:0} (saved {swSavedWork:0}); blueprint {bp?.def.defName}; {SwCell(map, swH)}; {SwCell(map, swL)}");
                    // (new Thing objects after the load: watch again)
                    SwWatch(map, new[] { swH, swL });
                    swCarpets.Add(swH);
                    SwSnapTotals(map);
                    SetWork(map, true);
                    SeedTicks("sw_loaded");
                    TickUntil(20000, () =>
                    {
                        SwTick(map);
                        return map.terrainGrid.UnderTerrainAt(swH) == swSteel && map.terrainGrid.TopTerrainAt(swL) == swGranite && SwSite(map, swH) == null && SwSite(map, swL) == null;
                    }, "sw_loaded2", 1);
                    return true;
                }
                case "sw_loaded2":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    bool hOk = map.terrainGrid.TopTerrainAt(swH) == swCarpet && map.terrainGrid.UnderTerrainAt(swH) == swSteel;
                    bool lOk = map.terrainGrid.TopTerrainAt(swL) == swGranite && map.terrainGrid.UnderTerrainAt(swL) == null;
                    bool noRefund = SwNoRefund(map, new Dictionary<ThingDef, int> { [swGraniteBlocks] = 1 }, out string mat);
                    Result("W4 after the load both finish as replacements: steel under the carpet (kept), granite for the wood; never open air; no refund",
                        hOk && lOk && swViol == 0 && noRefund, $"{SwCell(map, swH)}; {SwCell(map, swL)}; violations {SwViols()}; {mat} (the frame's steel was delivered before the save)");
                    Wait(5, "sw_regress");
                    return true;
                }

                // ---- W5 regressions
                case "sw_regress":
                {
                    swWatch.Clear();
                    swWatchBelow.Clear();
                    swCarpets.Clear();
                    swFixed.Clear();
                    AcceptanceReport r = SwPlace(swCarpet, swI);
                    SetWork(map, true);
                    SeedTicks("sw_regress");
                    TickUntil(20000, () =>
                    {
                        KeepFed(map);
                        return map.terrainGrid.TopTerrainAt(swI) == swCarpet;
                    }, "sw_regress2", 30);
                    Info($"W5 carpet blueprint at {swI} '{(r.Accepted ? "ok" : r.Reason)}'");
                    return true;
                }
                case "sw_regress2":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    Result("W5 a carpet laid on a wood slab: the slab stays under it", map.terrainGrid.TopTerrainAt(swI) == swCarpet && map.terrainGrid.UnderTerrainAt(swI) == swWood, SwCell(map, swI));
                    Designator_RemoveFoundation tool = new Designator_RemoveFoundation();
                    AcceptanceReport r = tool.CanDesignateCell(swJ);
                    if (r.Accepted)
                    {
                        tool.DesignateSingleCell(swJ);
                    }
                    SwSnapTotals(map);
                    SetWork(map, true);
                    SeedTicks("sw_remove");
                    TickUntil(20000, () =>
                    {
                        KeepFed(map);
                        return swJ.GetTerrain(map) == PZDefOf.PZ_OpenAir;
                    }, "sw_regress3", 30);
                    Info($"W5 remove upper floor at {swJ} '{(r.Accepted ? "ok" : r.Reason)}'");
                    return true;
                }
                case "sw_regress3":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    bool noRefund = SwNoRefund(map, new Dictionary<ThingDef, int>(), out string mat);
                    // (as mode slabremove: the roof below becomes a plain roof, which the falling silver breaks; compat G2)
                    Result("W5 a steel slab taken up as before: open air, no refund, the silver on it fell to the room below breaking the plain roof",
                        swJ.GetTerrain(map) == PZDefOf.PZ_OpenAir && noRefund && !OnOpenAir(map) && map.roofGrid.RoofAt(FloorRoof.Below(swJ, map)) == null && GenRadial.RadialCellsAround(FloorRoof.Below(swJ, map), 2f, true).Any(x => x.GetThingList(map).Any(t => t.def == ThingDefOf.Silver)),
                        $"{SwCell(map, swJ)}; {mat}; roof below {map.roofGrid.RoofAt(FloorRoof.Below(swJ, map))?.defName ?? "none"}");
                    Wait(5, "sw_show");
                    return true;
                }

                // ---- W6 showcase and shots
                case "sw_show":
                {
                    if (swP != null)
                    {
                        Teleport(swP, UpS(SW(24, 4), map));
                        Draft(swP, true);
                    }
                    SetHour(map, 12);
                    foreach (Thing m in map.listerThings.AllThings.Where(t => t is MoteText).ToList())
                    {
                        m.Destroy();
                    }
                    Messages.Clear();
                    swShot = 0;
                    Wait(5, "sw_shot");
                    return true;
                }
                case "sw_shot":
                case "sw_shot_after":
                {
                    bool after = stage == "sw_shot_after";
                    var shots = SwShots;
                    if (swShot >= shots.Length)
                    {
                        swShot = 0;
                        Wait(2, after ? "sw_end" : "sw_show_place");
                        return true;
                    }
                    var s = shots[swShot];
                    IsoRimBridge.SetOption("freeYaw", s.yaw);
                    ShotAt(map, s.floor, swShow.CenterCell, s.size, roofs: false);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(40, after ? "sw_take_after" : "sw_take");
                    return true;
                }
                case "sw_take":
                case "sw_take_after":
                {
                    bool after = stage == "sw_take_after";
                    ZCapture((after ? "after_" : "before_") + SwShots[swShot].name);
                    swShot++;
                    Wait(2, after ? "sw_shot_after" : "sw_shot");
                    return true;
                }
                case "sw_show_place":
                {
                    List<IntVec3> cells = swShow.Cells.Select(g => UpS(g, map)).ToList();
                    SwWatch(map, cells);
                    swViol = 0;
                    swViolLog.Clear();
                    SwSnapTotals(map);
                    int placed = cells.Count(c => SwPlace(swSteel, c).Accepted);
                    Info($"W6 showcase: {placed} of {cells.Count} steel blueprints placed; {swFixed.Count} things watched");
                    SetWork(map, true);
                    SeedTicks("sw_show");
                    TickUntil(40000, () =>
                    {
                        SwTick(map);
                        return cells.All(c => map.terrainGrid.TopTerrainAt(c) == swSteel && SwSite(map, c) == null);
                    }, "sw_show_done", 1);
                    return true;
                }
                case "sw_show_done":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    List<IntVec3> cells = swShow.Cells.Select(g => UpS(g, map)).ToList();
                    bool noRefund = SwNoRefund(map, new Dictionary<ThingDef, int> { [ThingDefOf.Steel] = cells.Count }, out string mat);
                    Result("W6 the 4 x 4 wood slab with furniture, silver and a colonist on it replaced by steel: never open air, nothing moved, no refund",
                        cells.All(c => map.terrainGrid.TopTerrainAt(c) == swSteel) && swViol == 0 && noRefund, $"violations {SwViols()}; {mat}");
                    if (swP != null)
                    {
                        Draft(swP, true);
                    }
                    foreach (Pawn p in Colonists(map).Where(p => p != swP))
                    {
                        Teleport(p, SW(14 + Colonists(map).IndexOf(p), 12));
                    }
                    foreach (Thing m in map.listerThings.AllThings.Where(t => t is MoteText).ToList())
                    {
                        m.Destroy();
                    }
                    Messages.Clear();
                    swShot = 0;
                    Wait(5, "sw_shot_after");
                    return true;
                }
                case "sw_end":
                    IsoRimBridge.SetOption("freeYaw", 0f);
                    Info("W2 counters: SlabReplace.Replaced " + SlabReplace.Replaced + ", UnderSwaps " + SlabReplace.UnderSwaps);
                    WatchCheck(map, "slabreplace end");
                    Finish();
                    return true;
            }
            return false;
        }

        private static readonly (string name, int floor, float yaw, float size)[] SwShots =
        {
            ("f2_close", 1, 0f, 5f), ("f2_yaw225", 1, 180f, 5f), ("ground_room", 0, 0f, 5f),
        };

        private static void SwSetup(Map map)
        {
            SwPatch();
            swGraniteBlocks = DefDatabase<ThingDef>.GetNamed("BlocksGranite");
            swWood = PZDefOf.PZ_UpperFloor;
            swSteel = SlabDefs.For(ThingDefOf.Steel);
            swGranite = SlabDefs.For(swGraniteBlocks);
            swCarpet = DefDatabase<TerrainDef>.AllDefs.Where(t => t.layerable && t.costList != null && t.costList.Any(c => c.thingDef == ThingDefOf.Cloth) && t.constructionSkillPrerequisite == 0 && t.defName.StartsWith("Carpet") && !t.defName.Contains("_")).OrderBy(t => t.defName).FirstOrDefault();
            List<Pawn> cols = Colonists(map);
            IntVec3 near = cols.Count > 0 ? new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z)) : map.Center;
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 79f, true))
            {
                CellRect r = new CellRect(c.x - 21, c.z - 7, 43, 15);
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
            if (found.Area == 0 || cols.Count < 2 || swCarpet == null)
            {
                Result("test spot", false, $"spot {found}, colonists {cols.Count}, carpet {swCarpet?.defName}");
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
            swO = new IntVec3(found.minX, 0, found.minZ);
            // House H (30 x 10): wood walls, a door south, roof, a wood slab over its inside; wood stairs up to it.
            swHouse = new CellRect(swO.x, swO.z, 30, 10);
            IntVec3 door = SW(5, 0);
            foreach (IntVec3 c in swHouse.EdgeCells)
            {
                Place(c == door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            foreach (IntVec3 c in swHouse)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                map.areaManager.Home[c] = true;
            }
            swStairs = SW(2, 3);
            HashSet<IntVec3> well = new HashSet<IntVec3>(StairShape.Ramp(swStairs, Rot4.North));
            foreach (IntVec3 g in swHouse.ContractedBy(1))
            {
                if (!well.Contains(g))
                {
                    map.terrainGrid.SetTerrain(UpS(g, map), swWood);
                }
            }
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, swStairs, map, Rot4.North);
            // Row 3: W2. Row 6: W3, W4. Row 8: W5. The showcase (W6) in the east.
            swA = UpS(SW(8, 3), map);
            swB = UpS(SW(11, 3), map);
            swC = UpS(SW(14, 3), map);
            swD = UpS(SW(17, 3), map);
            swE = UpS(SW(20, 3), map);
            swF = UpS(SW(8, 6), map);
            swG = UpS(SW(11, 6), map);
            swK = UpS(SW(14, 6), map);
            swH = UpS(SW(17, 6), map);
            swL = UpS(SW(20, 6), map);
            swI = UpS(SW(8, 8), map);
            swJ = UpS(SW(11, 8), map);
            swShow = new CellRect(swO.x + 23, swO.z + 2, 4, 4);
            map.terrainGrid.SetTerrain(swB, swSteel);
            map.terrainGrid.SetTerrain(swC, swSteel);
            map.terrainGrid.SetTerrain(swC, swCarpet);
            map.terrainGrid.SetTerrain(swD, SlabDefs.DamagedOf(swGranite));
            map.terrainGrid.SetTerrain(swE, SlabDefs.DamagedOf(swSteel));
            map.terrainGrid.SetTerrain(swH, swCarpet);
            map.terrainGrid.SetTerrain(swJ, swSteel);
            Thing Silver(IntVec3 c, int n)
            {
                Thing s = ThingMaker.MakeThing(ThingDefOf.Silver);
                s.stackCount = n;
                return GenSpawn.Spawn(s, c, map);
            }
            // On the cells: a drafted colonist (A), an end table (B), silver (A, B, C, J). Under them: components.
            Place(DefDatabase<ThingDef>.GetNamed("EndTable"), ThingDefOf.WoodLog, swB, map);
            foreach (IntVec3 c in new[] { swA, swB, swC, swJ })
            {
                Silver(c, 20);
            }
            foreach (IntVec3 c in new[] { swA, swB, swC, swD, swE, swF, swG, swK, swH, swL })
            {
                Thing comp = ThingMaker.MakeThing(ThingDefOf.ComponentIndustrial);
                comp.stackCount = 3;
                GenSpawn.Spawn(comp, FloorRoof.Below(c, map), map);
            }
            // The showcase: on the slab an end table, a dining chair, silver (and later the colonist); below a bed and a table.
            Place(DefDatabase<ThingDef>.GetNamed("EndTable"), ThingDefOf.WoodLog, UpS(SW(24, 3), map), map);
            Place(DefDatabase<ThingDef>.GetNamed("DiningChair"), ThingDefOf.WoodLog, UpS(SW(25, 3), map), map, Rot4.West);
            Silver(UpS(SW(25, 4), map), 40);
            Place(ThingDefOf.Bed, ThingDefOf.WoodLog, SW(24, 3), map, Rot4.North);
            Place(DefDatabase<ThingDef>.GetNamed("EndTable"), ThingDefOf.WoodLog, SW(25, 3), map);
            // Stuff on floor 2, row 1 (nothing watched there).
            int sx = 6;
            foreach (KeyValuePair<ThingDef, int> kv in new[] { new KeyValuePair<ThingDef, int>(ThingDefOf.Steel, 300), new KeyValuePair<ThingDef, int>(swGraniteBlocks, 75), new KeyValuePair<ThingDef, int>(ThingDefOf.WoodLog, 75), new KeyValuePair<ThingDef, int>(ThingDefOf.Cloth, 75) })
            {
                int left = kv.Value;
                while (left > 0)
                {
                    Thing t = ThingMaker.MakeThing(kv.Key);
                    t.stackCount = Mathf.Min(left, kv.Key.stackLimit);
                    left -= t.stackCount;
                    GenSpawn.Spawn(t, UpS(SW(sx, 1), map), map);
                    sx += 2;
                }
            }
            swP = cols[0];
            Teleport(swP, swA);
            Draft(swP, true);
            for (int i = 0; i < cols.Count; i++)
            {
                SkillRecord sk = cols[i].skills?.GetSkill(SkillDefOf.Construction);
                if (sk != null)
                {
                    sk.Level = 20;
                }
                if (i > 0)
                {
                    Teleport(cols[i], UpS(SW(14 + i, 7), map));
                    Draft(cols[i], false);
                }
            }
            SetWork(map, false);
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            Info($"slabreplace: house {swHouse}, stairs {swStairs}; A {swA} B {swB} C {swC} D {swD} E {swE}; F {swF} G {swG} K {swK} H {swH} L {swL}; I {swI} J {swJ}; showcase {swShow}; carpet {swCarpet.defName}; colonists {cols.Count}; floors {ProjectZ.Annex(map).floors}");
            Info("cells: " + string.Join("; ", new[] { swA, swB, swC, swD, swE, swH, swJ }.Select(c => SwCell(map, c))));
            AnnexWatch.Reset();
            Wait(10, "sw_refuse");
        }
    }
}
