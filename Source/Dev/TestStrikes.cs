using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProjectZ.Combat;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode strikes (slab damage and strikes from the sky: SlabDamage, SkyStrikes); run with -projectzfloors=3 on the
    // default 150 map. Everything is real game flow with ticks: grenades thrown by drafted colonists (AttackStatic from 2
    // cells: no forced miss), the orbital targeters used through their apparel verbs, lightning added as a weather event,
    // manned mortars, the remove tool done by colonists.
    //   - wood: a grenade on a wood slab breaks it; a steel stack on it falls to the ground; the roof under it goes.
    //   - stone: in a walled house with stairs and granite slabs, a grenade on a carpeted cell makes it a damaged granite
    //     slab (carpet gone, no cloth back, a carpet blueprint on another cell cancelled when that one is hit); laying a
    //     carpet there is refused with the reason; a second grenade breaks it, a colonist and steel on it fall.
    //   - remove: a damaged slab taken up by colonists with the remove tool.
    //   - bombardment over a 3-storey granite pavilion: every shell explodes on its column's top at that moment; per-floor
    //     counts over time.
    //   - power beam over another: one heavy hit per 120 ticks on the columns under it (radius 4): floor 3 damaged, gone,
    //     then floor 2 damaged, gone; outside the beam floor 3 stays.
    //   - lightning aimed at the ground under a slab strikes the slab (a colonist on it hit, one under it not); random
    //     strike cells are column tops open to the sky.
    //   - mortars aimed near the ground's north edge: no shell's destination in a void row or on another band by raw rows,
    //     each one its column's top.
    public static partial class TestRunner
    {
        private sealed class SxBlast
        {
            public IntVec3 centre;
            public bool onTop;
            public int tick;
            public string dam;
            public int amount;
        }

        private static readonly List<SxBlast> sxBlasts = new List<SxBlast>();
        private static readonly List<KeyValuePair<IntVec3, IntVec3>> sxShells = new List<KeyValuePair<IntVec3, IntVec3>>();   // destination, its column top at launch
        private static CellRect sxHouse, sxWood, sxBomb, sxBeam, sxLight, sxEdge;
        private static IntVec3 sxC1, sxC2, sxW;
        private static Pawn sxThrower, sxSitter, sxUser, sxUnder, sxOnSlab;
        private static int sxFrom, sxClothBefore, sxBlocksBefore, sxStart, sxSnap, sxLastLog;
        private static string sxLastCounts;
        private static TerrainDef sxGranite, sxCarpet;
        private static readonly List<string> sxTimeline = new List<string>();
        private static readonly List<Building_TurretGun> sxMortars = new List<Building_TurretGun>();
        private static int sxF3First = -1, sxF2First = -1;
        private static readonly Dictionary<IntVec3, bool> sxF2Order = new Dictionary<IntVec3, bool>();

        internal static void PatchStrikesProbe(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Explosion), nameof(Explosion.StartExplosion)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(SxStartPrefix)));
            h.Patch(AccessTools.Method(typeof(Projectile), nameof(Projectile.Launch), new[] { typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef) }),
                postfix: new HarmonyMethod(typeof(TestRunner), nameof(SxLaunchPostfix)));
        }

        private static void SxStartPrefix(Explosion __instance)
        {
            Map map = __instance.Map;
            if (map == null)
            {
                return;
            }
            sxBlasts.Add(new SxBlast { centre = __instance.Position, onTop = ProjectZ.ColumnTop(__instance.Position, map) == __instance.Position, tick = Find.TickManager.TicksGame, dam = __instance.damType?.defName,
                amount = __instance.damAmount >= 0 ? __instance.damAmount : __instance.damType?.defaultDamage ?? -1 });
        }

        private static void SxLaunchPostfix(Projectile __instance, LocalTargetInfo usedTarget, LocalTargetInfo intendedTarget)
        {
            if (__instance.def.projectile != null && __instance.def.projectile.flyOverhead && __instance.def.defName.Contains("Shell"))
            {
                sxShells.Add(new KeyValuePair<IntVec3, IntVec3>(usedTarget.Cell, ProjectZ.ColumnTop(usedTarget.Cell, __instance.Map)));
            }
        }

        private static IntVec3 SxUp(IntVec3 g, int k, Map map) => ProjectZ.CellOnFloor(g, k, map);

        private static Pawn SxPawn(Map map, IntVec3 at)
        {
            Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true, mustBeCapableOfViolence: true));
            GenSpawn.Spawn(p, at, map);
            Draft(p, true);
            return p;
        }

        private static void SxThrow(Pawn t, IntVec3 target)
        {
            t.skills.GetSkill(SkillDefOf.Shooting).Level = 20;
            if (t.equipment.Primary?.def.defName != "Weapon_GrenadeFrag")
            {
                t.equipment.DestroyAllEquipment();
                t.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Weapon_GrenadeFrag")));
            }
            Job j = JobMaker.MakeJob(JobDefOf.AttackStatic, new LocalTargetInfo(target));
            j.playerForced = true;
            bool ok = t.jobs.TryTakeOrderedJob(j, JobTag.DraftedOrder);
            Info($"{t.LabelShort} at {t.Position} (floor {ProjectZ.FloorOf(t.Position, map: t.Map) + 1}) throws a grenade at {target}: order {ok}");
        }

        private static bool SxBlastDone(Map map) => sxBlasts.Count > sxFrom && Find.TickManager.TicksGame - sxBlasts.Last().tick > 30;

        // Slab state of a cell: intact, damaged, or gone (open air).
        private static string SxState(IntVec3 c, Map map)
        {
            TerrainDef s = SlabDamage.SlabAt(c, map);
            return s == null ? "gone" : SlabDefs.IsDamaged(s) ? "damaged" : "intact";
        }

        private static string SxCounts(Map map, CellRect ground, int k)
        {
            int i = 0, d = 0, g = 0;
            foreach (IntVec3 c in ground)
            {
                switch (SxState(SxUp(c, k, map), map))
                {
                    case "intact": i++; break;
                    case "damaged": d++; break;
                    default: g++; break;
                }
            }
            return $"{i}/{d}/{g}";
        }

        private static void SxPavilion(Map map, CellRect ground, int floors, TerrainDef slab)
        {
            foreach (IntVec3 g in ground)
            {
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                for (int k = 1; k <= floors; k++)
                {
                    IntVec3 u = SxUp(g, k, map);
                    map.terrainGrid.SetTerrain(u, slab);
                    map.fogGrid.Unfog(u);
                }
            }
        }

        private static void SxFireOut(Map map)
        {
            foreach (Thing f in map.listerThings.ThingsOfDef(ThingDefOf.Fire).ToList())
            {
                f.Destroy();
            }
        }

        private static Verb SxTargeter(Pawn p, string def)
        {
            Apparel a = (Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(def));
            p.apparel.Wear(a, dropReplacedApparel: false);
            return a.GetComp<CompApparelVerbOwner>()?.AllVerbs?.FirstOrDefault();
        }

        private static bool StepStrikes(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            switch (stage)
            {
                case "sx_setup":
                {
                    if (an == null || an.floors < 3)
                    {
                        Result("strikes setup", false, $"needs 3 floors (-projectzfloors=3); floors {an?.floors}");
                        Finish();
                        return true;
                    }
                    int gz = an.groundZ;
                    DebugSettings.enableRandomMentalStates = false;
                    Find.PlaySettings.autoRebuild = false;
                    map.weatherManager.TransitionTo(WeatherDefOf.Clear);
                    ExClear(map, new CellRect(5, 10, 140, 25));
                    ExClear(map, new CellRect(20, 35, 115, 60));
                    ExClear(map, new CellRect(45, 95, 55, gz - 95));
                    foreach (Pawn p in Colonists(map))
                    {
                        Draft(p, true);
                        Teleport(p, new IntVec3(75 + Colonists(map).IndexOf(p), 0, 45));
                    }
                    sxGranite = SlabDefs.For(ThingDefOf.BlocksGranite);
                    sxCarpet = DefDatabase<TerrainDef>.AllDefs.Where(t => t.layerable && t.costList != null && t.costList.Any(c => c.thingDef == ThingDefOf.Cloth)).OrderBy(t => t.defName).FirstOrDefault();
                    Result("defs: a damaged slab for every stone and metal slab, none for wood; all count as slabs; not buildable, nothing laid on them",
                        SlabDefs.DamagedOf(sxGranite) != null && SlabDefs.DamagedOf(SlabDefs.For(ThingDefOf.Steel)) != null && SlabDefs.DamagedOf(PZDefOf.PZ_UpperFloor) == null
                        && SlabDefs.AllDamaged.All(d => SlabDefs.Is(d) && !d.BuildableByPlayer && !d.changeable && d.texturePath == SlabDefs.DamagedTexture)
                        && SlabDefs.All.All(s => s.destroyOnBombDamageThreshold == (SaArmoredSpec(SlabDefs.StuffOf(s)) ? 55f : 35f)),
                        $"{SlabDefs.AllDamaged.Count()} damaged defs, e.g. {SlabDefs.DamagedOf(sxGranite)?.defName} \"{SlabDefs.DamagedOf(sxGranite)?.label}\"; threshold {sxGranite.destroyOnBombDamageThreshold}");
                    // House A: walls, door, roof, granite slabs over the inside except the stairwell, stairs north.
                    sxHouse = new CellRect(10, 20, 13, 10);
                    IntVec3 door = new IntVec3(16, 0, 20);
                    foreach (IntVec3 c in sxHouse.EdgeCells)
                    {
                        Place(c == door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.BlocksGranite, c, map);
                    }
                    IntVec3 stairs = new IntVec3(12, 0, 23);
                    HashSet<IntVec3> well = new HashSet<IntVec3>(StairShape.Ramp(stairs, Rot4.North));
                    foreach (IntVec3 g in sxHouse)
                    {
                        map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                        map.areaManager.Home[g] = true;
                    }
                    foreach (IntVec3 g in sxHouse.ContractedBy(1))
                    {
                        if (!well.Contains(g))
                        {
                            map.terrainGrid.SetTerrain(SxUp(g, 1, map), sxGranite);
                            map.fogGrid.Unfog(SxUp(g, 1, map));
                        }
                    }
                    Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, stairs, map, Rot4.North);
                    sxC1 = SxUp(new IntVec3(18, 0, 24), 1, map);
                    sxC2 = SxUp(new IntVec3(20, 0, 24), 1, map);
                    foreach (IntVec3 c in CellRect.CenteredOn(sxC1, 1))
                    {
                        map.terrainGrid.SetTerrain(c, sxCarpet);
                    }
                    // W: a wood pavilion; B, C: 3-storey granite pavilions (no walls: the targeters need a line of sight);
                    // D: a granite slab over a roofed ground; E: 3 storeys at the ground's north edge.
                    sxWood = new CellRect(30, 20, 7, 7);
                    SxPavilion(map, sxWood, 1, PZDefOf.PZ_UpperFloor);
                    sxW = SxUp(new IntVec3(33, 0, 23), 1, map);
                    sxBomb = new CellRect(35, 70, 11, 11);
                    SxPavilion(map, sxBomb, 2, sxGranite);
                    sxBeam = new CellRect(105, 70, 11, 11);
                    SxPavilion(map, sxBeam, 2, sxGranite);
                    sxLight = new CellRect(110, 20, 5, 5);
                    SxPavilion(map, sxLight, 1, sxGranite);
                    sxEdge = new CellRect(80, gz - 10, 9, 10);
                    SxPavilion(map, sxEdge, 2, sxGranite);
                    Info($"house {sxHouse} (stairs {stairs}), c1 {sxC1} (carpet {sxCarpet?.defName}), c2 {sxC2}; wood {sxWood}; bombardment {sxBomb}; beam {sxBeam}; lightning {sxLight}; north edge {sxEdge}; ground rows {gz}, stride {an.stride}");
                    Wait(5, "sx_wood");
                    return true;
                }

                // ---- wood: one grenade breaks the slab
                case "sx_wood":
                {
                    sxThrower = SxPawn(map, SxUp(new IntVec3(33, 0, 25), 1, map));
                    Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    steel.stackCount = 10;
                    GenPlace.TryPlaceThing(steel, sxW, map, ThingPlaceMode.Direct);
                    sxFrom = sxBlasts.Count;
                    SxThrow(sxThrower, sxW);
                    TickUntil(1500, () => SxBlastDone(map), "sx_wood2", 1);
                    return true;
                }
                case "sx_wood2":
                {
                    tickWatch = null;
                    SxBlast b = sxBlasts.Skip(sxFrom).FirstOrDefault();
                    IntVec3 g = ProjectZ.GroundCellOf(sxW, map);
                    Thing steel = map.listerThings.ThingsOfDef(ThingDefOf.Steel).FirstOrDefault(t => ProjectZ.GroundCellOf(t.Position, map).DistanceTo(g) < 3f);
                    Result("wood: the grenade lands on the wood slab", b != null && b.centre == sxW, $"blast at {b?.centre}");
                    Result("wood: one grenade breaks the wood slab, the roof under it goes", sxW.GetTerrain(map) == PZDefOf.PZ_OpenAir && map.roofGrid.RoofAt(g) == null,
                        $"cell {sxW.GetTerrain(map).defName}, roof under it {map.roofGrid.RoofAt(g)?.defName ?? "none"}");
                    Result("wood: the steel on it fell to the ground", steel != null && ProjectZ.FloorOf(steel.Position, map) == 0, $"steel at {steel?.Position}");
                    int goneAround = GenAdj.CellsAdjacent8Way(new TargetInfo(sxW, map)).Count(c => SxState(c, map) == "gone");
                    int intactRing = GenRadial.RadialCellsAround(sxW, 2f, 2.9f).Count(c => SxState(c, map) == "intact");
                    Result("wood: the whole blast (radius 1.9, 50 damage, no falloff) is a heavy hit, cells 2 away are not hit", goneAround == 8 && intactRing == GenRadial.RadialCellsAround(sxW, 2f, 2.9f).Count(), $"{goneAround}/8 neighbours gone, {intactRing} intact 2 to 2.9 away");
                    sxThrower.Destroy();
                    StartTicks(10, "sx_stone1");
                    return true;
                }

                // ---- stone: damaged, refused floor, broken
                case "sx_stone1":
                {
                    sxClothBefore = map.listerThings.ThingsOfDef(ThingDefOf.Cloth).Sum(t => t.stackCount);
                    sxThrower = SxPawn(map, SxUp(new IntVec3(18, 0, 26), 1, map));
                    sxFrom = sxBlasts.Count;
                    SxThrow(sxThrower, sxC1);
                    TickUntil(1500, () => SxBlastDone(map), "sx_stone1b", 1);
                    return true;
                }
                case "sx_stone1b":
                {
                    tickWatch = null;
                    SxBlast b = sxBlasts.Skip(sxFrom).FirstOrDefault();
                    TerrainDef top = map.terrainGrid.TopTerrainAt(sxC1);
                    Result("stone: the grenade lands on the carpeted granite slab", b != null && b.centre == sxC1, $"blast at {b?.centre}");
                    Result("stone: one grenade makes it a damaged granite slab, the carpet on it is gone", top == SlabDefs.DamagedOf(sxGranite) && map.terrainGrid.UnderTerrainAt(sxC1) == null,
                        $"top {top.defName}, under {map.terrainGrid.UnderTerrainAt(sxC1)?.defName ?? "none"}");
                    int hurt = GenAdj.CellsAdjacent8Way(new TargetInfo(sxC1, map)).Count(c => SlabDefs.IsDamaged(c.GetTerrain(map)));
                    Result("stone: the whole 3x3 blast is damaged, all carpet there gone with no cloth back", map.listerThings.ThingsOfDef(ThingDefOf.Cloth).Sum(t => t.stackCount) == sxClothBefore && hurt == 8,
                        $"cloth {sxClothBefore} -> {map.listerThings.ThingsOfDef(ThingDefOf.Cloth).Sum(t => t.stackCount)}, damaged bare neighbours {hurt}/8");
                    // a carpet can't be laid there; a bare slab cell takes one (control)
                    Designator_Build des = new Designator_Build(sxCarpet);
                    AcceptanceReport r = des.CanDesignateCell(sxC1);
                    IntVec3 bare = SxUp(new IntVec3(14, 0, 27), 1, map);
                    AcceptanceReport rc = des.CanDesignateCell(bare);
                    Result("stone: laying a carpet on the damaged slab is refused with the reason", !r.Accepted && r.Reason == "PZ_DamagedSlabNoFloor".Translate(), $"\"{r.Reason}\"");
                    Result("stone: a carpet on an intact slab is still allowed (control)", rc.Accepted, $"{bare}: {rc.Accepted} {rc.Reason}");
                    Result("stone: the remove tool takes the damaged slab", new Designator_RemoveFoundation().CanDesignateCell(sxC1).Accepted, "Designator_RemoveFoundation");
                    // c2: a carpet blueprint on it first
                    GenConstruct.PlaceBlueprintForBuild(sxCarpet, sxC2, map, Rot4.North, Faction.OfPlayer, null);
                    Teleport(sxThrower, SxUp(new IntVec3(20, 0, 26), 1, map));
                    Draft(sxThrower, true);
                    sxFrom = sxBlasts.Count;
                    SxThrow(sxThrower, sxC2);
                    TickUntil(1500, () => SxBlastDone(map), "sx_stone1c", 1);
                    return true;
                }
                case "sx_stone1c":
                {
                    tickWatch = null;
                    bool bp = sxC2.GetThingList(map).Any(t => t is Blueprint || t is Frame);
                    Result("stone: a second cell damaged, the carpet blueprint on it cancelled", SlabDefs.IsDamaged(sxC2.GetTerrain(map)) && !bp, $"c2 {sxC2.GetTerrain(map).defName}, blueprint left {bp}");
                    Find.CameraDriver.config.sizeRange.min = 3f;
                    ShotAt(map, 1, ProjectZ.GroundCellOf(sxC1, map), 4f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(40, "sx_shot");
                    return true;
                }
                case "sx_shot":
                    Find.WindowStack.TryRemove(typeof(LudeonTK.EditWindow_Log), doCloseSound: false);
                    Capture("strikes_damaged_slab");
                    Wait(2, "sx_stone2");
                    return true;
                case "sx_stone2":
                {
                    Teleport(sxThrower, SxUp(new IntVec3(18, 0, 26), 1, map));
                    Draft(sxThrower, true);
                    Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    steel.stackCount = 20;
                    GenPlace.TryPlaceThing(steel, sxC1, map, ThingPlaceMode.Direct);
                    sxSitter = SxPawn(map, sxC1);
                    sxFrom = sxBlasts.Count;
                    SxThrow(sxThrower, sxC1);
                    TickUntil(1500, () => SxBlastDone(map), "sx_stone2b", 1);
                    return true;
                }
                case "sx_stone2b":
                {
                    tickWatch = null;
                    IntVec3 g = ProjectZ.GroundCellOf(sxC1, map);
                    IntVec3 at = sxSitter.Dead ? sxSitter.Corpse?.PositionHeld ?? IntVec3.Invalid : sxSitter.Position;
                    Thing steel = map.listerThings.ThingsOfDef(ThingDefOf.Steel).FirstOrDefault(t => t.stackCount == 20);
                    Result("stone: a second grenade breaks the damaged slab, the roof under it goes", sxC1.GetTerrain(map) == PZDefOf.PZ_OpenAir && map.roofGrid.RoofAt(g) == null,
                        $"cell {sxC1.GetTerrain(map).defName}, roof under it {map.roofGrid.RoofAt(g)?.defName ?? "none"}");
                    Result("stone: the colonist and the steel on it fell to the ground floor", at.IsValid && ProjectZ.FloorOf(at, map) == 0 && steel != null && ProjectZ.FloorOf(steel.Position, map) == 0,
                        $"colonist {(sxSitter.Dead ? "dead" : "alive")} at {at}, steel at {steel?.Position}");
                    sxSitter.Corpse?.Destroy();
                    if (sxSitter.Spawned)
                    {
                        sxSitter.Destroy();
                    }
                    sxThrower.Destroy();
                    // ---- remove: colonists take up the damaged c2 with the remove tool
                    Designator_RemoveFoundation rem = new Designator_RemoveFoundation();
                    AcceptanceReport r = rem.CanDesignateCell(sxC2);
                    if (r.Accepted)
                    {
                        rem.DesignateSingleCell(sxC2);
                    }
                    sxBlocksBefore = CountOn(map, ThingDefOf.BlocksGranite, 1);
                    SetWork(map, true);
                    foreach (Pawn p in Colonists(map))
                    {
                        Draft(p, false);
                        Teleport(p, new IntVec3(15 + Colonists(map).IndexOf(p), 0, 17));
                    }
                    Info($"remove: c2 designated {r.Accepted} {r.Reason}");
                    TickUntil(30000, () => sxC2.GetTerrain(map) == PZDefOf.PZ_OpenAir, "sx_remove2", 60);
                    return true;
                }
                case "sx_remove2":
                {
                    tickWatch = null;
                    Result("remove: colonists take up the damaged slab with the remove tool", sxC2.GetTerrain(map) == PZDefOf.PZ_OpenAir,
                        $"c2 {sxC2.GetTerrain(map).defName} after {ticksTotal} ticks; granite blocks on floor 2 {sxBlocksBefore} -> {CountOn(map, ThingDefOf.BlocksGranite, 1)}");
                    SetWork(map, false);
                    foreach (Pawn p in Colonists(map))
                    {
                        Draft(p, true);
                        Teleport(p, new IntVec3(75 + Colonists(map).IndexOf(p), 0, 45));
                    }
                    StartTicks(10, "sx_bomb");
                    return true;
                }

                // ---- bombardment
                case "sx_bomb":
                {
                    sxUser = SxPawn(map, new IntVec3(40, 0, 37));
                    Verb v = SxTargeter(sxUser, "OrbitalTargeterBombardment");
                    sxFrom = sxBlasts.Count;
                    sxTimeline.Clear();
                    sxLastCounts = null;
                    sxF3First = sxF2First = -1;
                    sxF2Order.Clear();
                    sxStart = Find.TickManager.TicksGame;
                    IntVec3 target = sxBomb.CenterCell;
                    v?.OrderForceTarget(new LocalTargetInfo(target));
                    Info($"bombardment: {sxUser.LabelShort} at {sxUser.Position} uses {v} on the ground under the pavilion {target}: job {sxUser.CurJob?.def?.defName}");
                    TickUntil(2500, () => SxBombWatch(map), "sx_bomb2", 10);
                    return true;
                }
                case "sx_bomb2":
                {
                    tickWatch = null;
                    List<SxBlast> bs = sxBlasts.Skip(sxFrom).Where(b => b.dam == "Bomb" && ProjectZ.GroundCellOf(b.centre, map).DistanceTo(sxBomb.CenterCell) <= 16f).ToList();
                    int off = bs.Count(b => !b.onTop);
                    foreach (string l in sxTimeline)
                    {
                        Info("bombardment: " + l);
                    }
                    Result("bombardment: every shell explodes on its column's top at that moment", bs.Count >= 20 && off == 0,
                        $"{bs.Count} shells, {off} not on the top; on floor 3 {bs.Count(b => ProjectZ.FloorOf(b.centre, map) == 2)}, floor 2 {bs.Count(b => ProjectZ.FloorOf(b.centre, map) == 1)}, ground {bs.Count(b => ProjectZ.FloorOf(b.centre, map) == 0)}");
                    int both = sxBomb.Cells.Count(g => SxState(SxUp(g, 2, map), map) == "gone" && SxState(SxUp(g, 1, map), map) != "intact");
                    int after = sxF2Order.Values.Count(x => x);
                    Result("bombardment: burns down layer by layer (columns burnt through floor 3 into floor 2; floor-2 cells hit before floor 3 above them come from blasts beside the open pavilion)", both > 0,
                        $"floor-2 cells hit {sxF2Order.Count}, {after} of them after floor 3 above; first change on floor 3 at {sxF3First}, on floor 2 at {sxF2First} ticks; {both} columns burnt through floor 3 into floor 2; floor 3 {SxCounts(map, sxBomb, 2)}, floor 2 {SxCounts(map, sxBomb, 1)} (intact/damaged/gone)");
                    SxFireOut(map);
                    sxUser.Destroy();
                    StartTicks(60, "sx_beam");
                    return true;
                }

                // ---- power beam
                case "sx_beam":
                {
                    SxFireOut(map);
                    sxUser = SxPawn(map, new IntVec3(110, 0, 37));
                    Verb v = SxTargeter(sxUser, "OrbitalTargeterPowerBeam");
                    sxStart = -1;
                    sxSnap = 0;
                    sxTimeline.Clear();
                    v?.OrderForceTarget(new LocalTargetInfo(sxBeam.CenterCell));
                    Info($"power beam: {sxUser.LabelShort} at {sxUser.Position} uses {v} on the ground under the pavilion {sxBeam.CenterCell}: job {sxUser.CurJob?.def?.defName}");
                    TickUntil(2500, () => SxBeamWatch(map), "sx_beam2", 1);
                    return true;
                }
                case "sx_beam2":
                {
                    tickWatch = null;
                    foreach (string l in sxTimeline)
                    {
                        Info("power beam: " + l);
                    }
                    List<IntVec3> under = GenRadial.RadialCellsAround(sxBeam.CenterCell, 4f, true).ToList();
                    List<IntVec3> outside = sxBeam.Cells.Where(g => g.DistanceTo(sxBeam.CenterCell) > 5.5f).ToList();
                    bool f3gone = under.All(g => SxState(SxUp(g, 2, map), map) == "gone");
                    bool f2gone = under.All(g => SxState(SxUp(g, 1, map), map) == "gone");
                    bool open = under.All(g => map.roofGrid.RoofAt(g) == null);
                    Result("power beam: 4 heavy hits burn through two stone floors under it (radius 4), the ground is open to the sky", sxSnap == 4 && f3gone && f2gone && open,
                        $"{sxSnap} snapshots; under the beam floor 3 {under.Count(g => SxState(SxUp(g, 2, map), map) == "gone")}/{under.Count} gone, floor 2 {under.Count(g => SxState(SxUp(g, 1, map), map) == "gone")}/{under.Count} gone, open ground {under.Count(g => map.roofGrid.RoofAt(g) == null)}");
                    Result("power beam: outside its radius floor 3 stays", outside.All(g => SxState(SxUp(g, 2, map), map) == "intact"), $"{outside.Count(g => SxState(SxUp(g, 2, map), map) == "intact")}/{outside.Count} intact");
                    SxFireOut(map);
                    sxUser.Destroy();
                    StartTicks(60, "sx_light");
                    return true;
                }

                // ---- lightning
                case "sx_light":
                {
                    SxFireOut(map);
                    IntVec3 g = sxLight.CenterCell;
                    sxUnder = SxPawn(map, g + IntVec3.East);
                    sxOnSlab = SxPawn(map, SxUp(g + IntVec3.East, 1, map));
                    exInj.Clear();
                    exInj["under"] = Injuries(sxUnder);
                    exInj["on"] = Injuries(sxOnSlab);
                    sxFrom = sxBlasts.Count;
                    map.weatherManager.eventHandler.AddEvent(new WeatherEvent_LightningStrike(map, g));
                    Info($"lightning aimed at the ground {g} under the slab");
                    StartTicks(120, "sx_light2");
                    return true;
                }
                case "sx_light2":
                {
                    SxBlast b = sxBlasts.Skip(sxFrom).FirstOrDefault(x => x.dam == "Flame");
                    IntVec3 g = sxLight.CenterCell;
                    Result("lightning: aimed under a slab, it strikes the slab (the column top)", b != null && b.centre == SxUp(g, 1, map), $"strike at {b?.centre}");
                    ExRec er = exRecs.LastOrDefault(r => r.dam == "Flame");
                    bool slabCell = er != null && er.cells.Any(kv => kv.Key == SxUp(g + IntVec3.East, 1, map));
                    bool groundCell = er != null && er.cells.Any(kv => kv.Key == g + IntVec3.East || kv.Key == g);
                    Result("lightning: its blast reaches the colonist on the slab next to it, not the one under the slab (unhurt)",
                        slabCell && !groundCell && !sxUnder.Dead && Injuries(sxUnder) == exInj["under"],
                        $"blast cells {er?.cells.Count}, slab cell in {slabCell}, ground cells in {groundCell}; on the slab: {Injuries(sxOnSlab) - exInj["on"]} new injuries, burning {sxOnSlab.IsBurning()}; under: {Injuries(sxUnder) - exInj["under"]}");
                    int bad = 0, upper = 0, n = 300;
                    for (int i = 0; i < n; i++)
                    {
                        IntVec3 c = SkyStrikes.LightningCell(IntVec3.Invalid, map);
                        if (!c.IsValid || ProjectZ.FloorOf(c, map) < 0 || ProjectZ.ColumnTop(c, map) != c || map.roofGrid.Roofed(c))
                        {
                            bad++;
                        }
                        else if (ProjectZ.FloorOf(c, map) >= 1)
                        {
                            upper++;
                        }
                    }
                    Result("lightning: random strike cells are column tops open to the sky, never a void row", bad == 0, $"{n} picks, {bad} bad, {upper} on upper floors");
                    SxFireOut(map);
                    sxUnder.Destroy();
                    sxOnSlab.Destroy();
                    StartTicks(10, "sx_mortar");
                    return true;
                }

                // ---- mortar misses
                case "sx_mortar":
                {
                    int gz = an.groundZ;
                    IntVec3 target = new IntVec3(75, 0, gz - 3);
                    sxShells.Clear();
                    sxStart = Find.TickManager.TicksGame;
                    sxMortars.Clear();
                    for (int i = 0; i < 4; i++)
                    {
                        IntVec3 at = new IntVec3(60 + 10 * i, 0, gz - 50);
                        Building_TurretGun m = (Building_TurretGun)Place(DefDatabase<ThingDef>.GetNamed("Turret_Mortar"), null, at, map);
                        sxMortars.Add(m);
                        Pawn p = SxPawn(map, at + IntVec3.South * 2);
                        Job j = JobMaker.MakeJob(JobDefOf.ManTurret, m);
                        p.jobs.TryTakeOrderedJob(j, JobTag.DraftedOrder);
                        m.gun.TryGetComp<CompChangeableProjectile>()?.LoadShell(DefDatabase<ThingDef>.GetNamed("Shell_HighExplosive"), 1);
                        // (the gunner reloads from a stock next to it, as in play)
                        Thing shells = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Shell_HighExplosive"));
                        shells.stackCount = 10;
                        GenPlace.TryPlaceThing(shells, at + IntVec3.South * 2 + IntVec3.East, map, ThingPlaceMode.Near);
                        m.OrderAttack(new LocalTargetInfo(target));
                    }
                    Info($"mortars: 4 manned mortars ordered at {target} ({gz - target.z} rows from the ground's north edge; {sxEdge} has floors 2 and 3)");
                    TickUntil(20000, () => SxMortarWatch(map), "sx_mortar2", 30);
                    return true;
                }
                case "sx_mortar2":
                {
                    tickWatch = null;
                    int gz = an.groundZ;
                    int voids = 0, notTop = 0, clamped = 0, lifted = 0;
                    List<string> bad = new List<string>();
                    foreach (KeyValuePair<IntVec3, IntVec3> s in sxShells)
                    {
                        IntVec3 d = s.Key;
                        int k = ProjectZ.FloorOf(d, map);
                        if (k < 0)
                        {
                            voids++;
                            bad.Add($"void {d}");
                            continue;
                        }
                        if (s.Value != d)
                        {
                            notTop++;
                            bad.Add($"{d} not the top ({ProjectZ.ColumnTop(d, map)})");
                        }
                        if (ProjectZ.GroundCellOf(d, map).z == gz - 1)
                        {
                            clamped++;
                        }
                        if (k >= 1)
                        {
                            lifted++;
                        }
                    }
                    List<SxBlast> bl = sxBlasts.Where(b => b.dam == "Bomb" && b.tick >= sxStart).ToList();
                    Result("mortars: no shell aimed into a void row, each one its column's top", sxShells.Count >= 12 && voids == 0 && notTop == 0,
                        $"{sxShells.Count} shells, {voids} void, {notTop} not the top; {clamped} misses kept at the ground's north row, {lifted} on an upper floor (the column top); {string.Join("; ", bad.Take(6))}");
                    Result("mortars: every shell explodes on a column top, none in a void row", bl.All(b => b.onTop && ProjectZ.FloorOf(b.centre, map) >= 0), $"{bl.Count} explosions, {bl.Count(b => !b.onTop)} off the top");
                    foreach (Building_TurretGun m in sxMortars)
                    {
                        m.OrderAttack(LocalTargetInfo.Invalid);
                    }
                    Info($"counters: slabs damaged {SlabDamage.Damaged}, broken {SlabDamage.Broken}, beam hits {SkyStrikes.BeamHits}, shells lifted at impact {SkyStrikes.ShellsLifted}, forced misses moved {ForcedMiss_Patch.Lifted}");
                    Finish();
                    return true;
                }
            }
            return false;
        }

        private static bool SxBombWatch(Map map)
        {
            int now = Find.TickManager.TicksGame - sxStart;
            string counts = $"floor 3 {SxCounts(map, sxBomb, 2)}, floor 2 {SxCounts(map, sxBomb, 1)}";
            if (counts != sxLastCounts && now - sxLastLog >= 30 || sxLastCounts == null)
            {
                sxTimeline.Add($"t+{now}: {counts} (intact/damaged/gone)");
                sxLastLog = now;
            }
            if (sxF3First < 0 && sxBomb.Cells.Any(g => SxState(SxUp(g, 2, map), map) != "intact"))
            {
                sxF3First = now;
            }
            foreach (IntVec3 g in sxBomb)
            {
                if (!sxF2Order.ContainsKey(g) && SxState(SxUp(g, 1, map), map) != "intact")
                {
                    sxF2Order[g] = SxState(SxUp(g, 2, map), map) != "intact";
                }
            }
            if (sxF2First < 0 && sxBomb.Cells.Any(g => SxState(SxUp(g, 1, map), map) != "intact"))
            {
                sxF2First = now;
            }
            sxLastCounts = counts;
            bool strike = map.listerThings.ThingsOfDef(ThingDefOf.Bombardment).Any();
            return now > 400 && !strike;
        }

        private static bool SxBeamWatch(Map map)
        {
            Thing beam = map.listerThings.ThingsOfDef(ThingDefOf.PowerBeam).FirstOrDefault();
            if (sxStart < 0)
            {
                if (beam == null)
                {
                    return false;
                }
                sxStart = Find.TickManager.TicksGame;
                sxTimeline.Add($"beam at {beam.Position} (floor {ProjectZ.FloorOf(beam.Position, map) + 1})");
            }
            int passed = Find.TickManager.TicksGame - sxStart;
            if (passed > 0 && passed >= SkyStrikes.BeamInterval && passed % SkyStrikes.BeamInterval == 2 && sxSnap < 4)
            {
                sxSnap++;
                List<IntVec3> under = GenRadial.RadialCellsAround(sxBeam.CenterCell, 4f, true).ToList();
                string f3 = string.Join(",", under.GroupBy(g => SxState(SxUp(g, 2, map), map)).Select(x => x.Key + " " + x.Count()));
                string f2 = string.Join(",", under.GroupBy(g => SxState(SxUp(g, 1, map), map)).Select(x => x.Key + " " + x.Count()));
                string want3 = sxSnap == 1 ? "damaged" : "gone";
                string want2 = sxSnap <= 2 ? "intact" : sxSnap == 3 ? "damaged" : "gone";
                bool ok = under.All(g => SxState(SxUp(g, 2, map), map) == want3) && under.All(g => SxState(SxUp(g, 1, map), map) == want2);
                Result($"power beam: after hit {sxSnap} the columns under it are floor 3 {want3}, floor 2 {want2}", ok, $"t+{passed}: floor 3 {f3}; floor 2 {f2}; beam at floor {(beam != null ? ProjectZ.FloorOf(beam.Position, map) + 1 : 0)}");
            }
            return sxStart >= 0 && beam == null && passed > 10;
        }

        private static bool SxMortarWatch(Map map)
        {
            if ((ticksTotal - ticksLeft) % 3000 == 0)
            {
                foreach (Building_TurretGun m in sxMortars)
                {
                    CompMannable mc = m.TryGetComp<CompMannable>();
                    CompRefuelable rf = m.TryGetComp<CompRefuelable>();
                    Info($"mortar {m.Position}: active {m.Active}, manned {mc?.MannedNow} by {mc?.ManningPawn?.LabelShort} ({mc?.ManningPawn?.CurJob?.def?.defName}), forced {m.ForcedTarget}, loaded {m.gun.TryGetComp<CompChangeableProjectile>()?.Loaded}, fuel {rf?.Fuel}, verb {m.AttackVerb?.state}");
                }
            }
            return sxShells.Count >= 16 && !map.listerThings.AllThings.Any(t => t is Projectile p && p.def.projectile.flyOverhead);
        }
    }
}
