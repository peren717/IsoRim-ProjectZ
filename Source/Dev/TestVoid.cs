using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ProjectZ.Combat;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // The void barrier (VoidRule, BandScope, Shields3D; isorim-pz-void-barrier.md §5) and slabs broken by heavy hits
    // crushing what is below (SlabDamage.Break, isorim-orbital-strike-3d.md).
    //   void (-projectzfloors=2, default 150 map: 3 void rows): patches swapped, rooms per band, tox gas thrown at the
    //        ground's north row, a torch there, beauty and radial things across the gap, shield bubbles (ground shot,
    //        floor-2 shot across the gap, a shot down from floor 2 into the dome), a mech booster's aura, the mechanitor's
    //        range, a tornado driving north, a flashstorm on the ground and one on floor 2. With -projectznovoidrule every
    //        check expects the old leak instead (the result names say "leak").
    //   voidfire (-projectzmapsize=101: 1 void row): grass burning on the ground's north rows next to fuel on floor 2's
    //        south row (sparks reach 2 cells), and a walled, roofed floor-2 room over the void row (its wall exchange).
    //   voidsave (-projectznovoidrule): tox gas into the void, saved as pz_voidold; voidload: that save loaded with the
    //        rule (gas in the void cleared, no void regions, rooms rebuilt).
    //   voidperf (-projectzmapsize=300 -projectzfloors=13): the rule's load time and a full region rebuild.
    //   crush (-projectzfloors=2): a frag grenade breaks a wood slab with a colonist under it: crushed, the game's letter.
    public static partial class TestRunner
    {
        private static int voO, voGz;
        private static bool Leak => VoidRule.Off;
        private static readonly Dictionary<string, Pawn> voPawns = new Dictionary<string, Pawn>();
        private static int voLaunches, voIntercepts, voStrikeFrom;
        private static readonly List<IntVec3> voStrikes = new List<IntVec3>();
        private static Thing voTornado;
        private static int voTornadoMaxZ;
        private static readonly Dictionary<Thing, int> voHp = new Dictionary<Thing, int>();
        private static int voRoofs;
        private static float voBefore;
        private static GameCondition voStorm;
        private static int voFireOnFloor, voFireMax;

        internal static void PatchVoidProbe(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Projectile), nameof(Projectile.Launch), new[] { typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef) }),
                postfix: new HarmonyMethod(typeof(TestRunner), nameof(VoLaunchPostfix)));
            h.Patch(AccessTools.Method(typeof(CompProjectileInterceptor), nameof(CompProjectileInterceptor.CheckIntercept)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(VoInterceptPostfix)));
            h.Patch(AccessTools.Method(typeof(WeatherEventHandler), nameof(WeatherEventHandler.AddEvent)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(VoWeatherPostfix)));
            h.Patch(AccessTools.Method(typeof(FireUtility), nameof(FireUtility.ChanceToStartFireIn)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(VoFireChancePostfix)));
        }

        private static void VoLaunchPostfix() => voLaunches++;

        private static readonly AccessTools.FieldRef<BattleLogEntry_DamageTaken, Pawn> dtRecipient = AccessTools.FieldRefAccess<BattleLogEntry_DamageTaken, Pawn>("recipientPawn");
        private static readonly AccessTools.FieldRef<BattleLogEntry_DamageTaken, RulePackDef> dtRule = AccessTools.FieldRefAccess<BattleLogEntry_DamageTaken, RulePackDef>("ruleDef");

        // The game logged a falling roof hitting this pawn (its roof crush; armour may still stop the damage).
        internal static bool Ceilinged(Pawn p)
        {
            return Find.BattleLog.Battles.SelectMany(b => b.Entries).OfType<BattleLogEntry_DamageTaken>()
                .Any(e => dtRecipient(e) == p && dtRule(e) == RulePackDefOf.DamageEvent_Ceiling);
        }

        private static int voSparkTries, voSparkOk;

        private static void VoFireChancePostfix(IntVec3 c, Map map, float __result)
        {
            if (ProjectZ.Annex(map) != null && ProjectZ.FloorOf(c, map) == 1 && stage.StartsWith("vf_"))
            {
                voSparkTries++;
                if (__result > 0f)
                {
                    voSparkOk++;
                }
            }
        }

        private static void VoInterceptPostfix(bool __result)
        {
            if (__result)
            {
                voIntercepts++;
            }
        }

        private static readonly AccessTools.FieldRef<WeatherEvent_LightningStrike, IntVec3> strikeLocRef = AccessTools.FieldRefAccess<WeatherEvent_LightningStrike, IntVec3>("strikeLoc");

        private static void VoWeatherPostfix(WeatherEvent newEvent)
        {
            if (newEvent is WeatherEvent_LightningStrike s)
            {
                voStrikes.Add(strikeLocRef(s));
            }
        }

        private static IntVec3 VG(int x, int z) => new IntVec3(voO + x, 0, z);                       // ground, z absolute

        private static IntVec3 VU(int x, int z, Map map) => ProjectZ.CellOnFloor(VG(x, z), 1, map);  // floor 2 over ground (x, z)

        // A check that holds with the rule, and fails (the old leak) with -projectznovoidrule.
        private static void VoExpect(string name, bool blocked, string detail)
        {
            Result(Leak ? "LEAK " + name : name, Leak ? !blocked : blocked, detail);
        }

        private static Pawn VoPawn(Map map, string name, IntVec3 at, string weapon = null)
        {
            Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true, mustBeCapableOfViolence: true));
            GenSpawn.Spawn(p, at, map);
            Draft(p, true);
            if (weapon != null)
            {
                p.skills.GetSkill(SkillDefOf.Shooting).Level = 20;
                p.equipment.DestroyAllEquipment();
                p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(weapon)));
            }
            voPawns[name] = p;
            return p;
        }

        private static void VoAway(Map map)
        {
            foreach (Pawn p in voPawns.Values.ToList())
            {
                if (p.Spawned)
                {
                    p.jobs?.StopAll();
                    p.Destroy();
                }
            }
            voPawns.Clear();
        }

        private static void VoAttack(Pawn p, IntVec3 at)
        {
            Job j = JobMaker.MakeJob(JobDefOf.AttackStatic, new LocalTargetInfo(at));
            j.playerForced = true;
            p.jobs.TryTakeOrderedJob(j, JobTag.DraftedOrder);
        }

        private static void VoSlab(Map map, CellRect ground)
        {
            foreach (IntVec3 g in ground)
            {
                map.roofGrid.SetRoof(g, PZDefOf.PZ_FloorRoof);
                IntVec3 u = ProjectZ.CellOnFloor(g, 1, map);
                map.terrainGrid.SetTerrain(u, PZDefOf.PZ_UpperFloor);
                map.fogGrid.Unfog(u);
            }
        }

        // A fresh skipshield (4.9; it lasts 900 ticks) on the ground's north row.
        private static void VoShield(Map map)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamed("BulletShieldPsychic");
            foreach (Thing t in map.listerThings.ThingsOfDef(def).ToList())
            {
                t.Destroy();
            }
            GenSpawn.Spawn(ThingMaker.MakeThing(def), VG(48, voGz - 1), map);
        }

        private static int GasIn(Map map, IEnumerable<IntVec3> cells)
        {
            int n = 0;
            foreach (IntVec3 c in cells)
            {
                if (c.InBounds(map) && map.gasGrid.AnyGasAt(c))
                {
                    n++;
                }
            }
            return n;
        }

        private static IEnumerable<IntVec3> VoRect(int x0, int z0, int w, int h)
        {
            return new CellRect(x0, z0, w, h).Cells;
        }

        private static IEnumerable<IntVec3> VoidCells(Map map, int x0, int w)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            return VoRect(x0, a.groundZ, w, a.stride - a.groundZ);
        }

        private static IEnumerable<IntVec3> Floor1South(Map map, int x0, int w, int rows)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            return VoRect(x0, a.stride, w, rows);
        }

        private static bool LightBlockerBit(Map map, IntVec3 c)
        {
            object bits = AccessTools.Field(typeof(GlowGrid), "lightBlockers").GetValue(map.glowGrid);
            MethodInfo isSet = bits.GetType().GetMethod("IsSet", new[] { typeof(int) });
            return (bool)isSet.Invoke(bits, new object[] { map.cellIndices.CellToIndex(c) });
        }

        private static int PickField(Map map, int width, int northRows, int southRows)
        {
            int best = 3, bestBad = int.MaxValue;
            for (int x = 3; x + width < map.Size.x - 3; x += 3)
            {
                CellRect n = new CellRect(x, voGz - northRows, width, northRows);
                CellRect s = new CellRect(x, 0, width, southRows);
                int bad = n.Cells.Concat(s.Cells).Count(c => c.GetThingList(map).Any(t => !t.def.destroyable) || c.GetTerrain(map).IsWater || c.GetTerrain(map).passability == Traversability.Impassable || map.roofGrid.RoofAt(c)?.isThickRoof == true);
                if (bad < bestBad)
                {
                    bestBad = bad;
                    best = x;
                }
            }
            return best;
        }

        private static void VoColonistsAway(Map map)
        {
            DebugSettings.enableRandomMentalStates = false;
            foreach (Pawn p in Colonists(map))
            {
                Draft(p, true);
                Teleport(p, CellFinder.RandomClosewalkCellNear(new IntVec3(map.Size.x / 2, 0, voGz / 2), map, 6));
            }
        }

        private static bool StepVoid(Map map)
        {
            switch (stage)
            {
                // ================================================================ void
                case "vo_setup":
                {
                    AnnexMapComponent an = ProjectZ.Annex(map);
                    if (an == null || an.floors < 2)
                    {
                        Result("void setup", false, $"needs 2 floors; floors {an?.floors}");
                        Finish();
                        return true;
                    }
                    voGz = an.groundZ;
                    voO = PickField(map, 92, 32, 32);
                    ExClear(map, new CellRect(voO - 1, voGz - 33, 94, 33));
                    ExClear(map, new CellRect(voO - 1, 0, 94, 33));
                    VoColonistsAway(map);
                    Info($"void: field x {voO}..{voO + 91}, ground rows {voGz}, stride {an.stride} (void rows {an.stride - voGz}), rule {(Leak ? "OFF (-projectznovoidrule)" : "on")}; applied on load: {VoidRule.LastCells} void cells, {VoidRule.LastGasCleared} gas cleared, {VoidRule.LastApplyMs:0.0} ms");
                    // patches took effect (the small methods: through their effect, not by calling them)
                    Result("patched: GasGrid's own GasCanMoveTo calls swapped", GasGridCalls_Void_Patch.PerMethod.Count >= 4 && !GasGridCalls_Void_Patch.PerMethod.ContainsKey("Overflow") && GasGridCalls_Void_Patch.PerMethod.Values.All(v => v >= 1), string.Join(", ", GasGridCalls_Void_Patch.PerMethod.Select(kv => kv.Key + " " + kv.Value)));
                    Result("patched: band scope rows", BandScope_Patch.Missing.Count == 0 && BandScope_Patch.PerMethod.Count >= 16 && BandScope_Patch.PerMethod.Values.All(v => v >= 1),
                        $"{BandScope_Patch.PerMethod.Count} methods: {string.Join(", ", BandScope_Patch.PerMethod.Select(kv => kv.Key + " " + kv.Value))}; missing {string.Join(", ", BandScope_Patch.Missing)}");
                    Result("patched: RadialDistinctThingsAround's iterator, the aura distance", RadialDistinctThings_Band_Patch.Swapped == 1 && CauseHediffAoE_3D_Patch.Swapped == 1, $"iterator {RadialDistinctThings_Band_Patch.Swapped}, aura {CauseHediffAoE_3D_Patch.Swapped}");
                    IntVec3 v = VG(10, voGz + 1);
                    VoExpect("void cell: no region, light blocker set, gas refused", v.GetRegion(map, RegionType.Set_All) == null && LightBlockerBit(map, v) && !map.gasGrid.GasCanMoveTo(v),
                        $"{v}: region {v.GetRegion(map, RegionType.Set_All)?.type.ToString() ?? "none"}, light blocker {LightBlockerBit(map, v)}, GasCanMoveTo {map.gasGrid.GasCanMoveTo(v)}");
                    IntVec3 g = VG(10, voGz - 1);
                    IntVec3 u = VU(10, 0, map);
                    bool los = GenSight.LineOfSight(g, u, map);
                    bool los7 = GenSight.LineOfSight(g, u, map, skipFirstCell: true);
                    VoExpect("flat line of sight between the bands refused (3- and 7-argument calls)", !los && !los7, $"{g} -> {u}: {los}, {los7}; refused so far {LineOfSight_Void_Patch.Refused}");
                    Result("flat line of sight on one band unchanged", GenSight.LineOfSight(g, VG(14, voGz - 1), map), $"{g} -> {VG(14, voGz - 1)}");
                    Wait(5, "vo_rooms");
                    return true;
                }
                case "vo_rooms":
                {
                    // a walled, roofed room on floor 2 (well inside), floor 2's open air, the ground's outdoors
                    CellRect rg = new CellRect(voO + 4, voGz - 30, 7, 5);
                    VoSlab(map, rg);
                    foreach (IntVec3 g in rg)
                    {
                        IntVec3 c = ProjectZ.CellOnFloor(g, 1, map);
                        if (g.x == rg.minX || g.x == rg.maxX || g.z == rg.minZ || g.z == rg.maxZ)
                        {
                            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
                        }
                        else
                        {
                            map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                        }
                    }
                    Wait(5, "vo_rooms2");
                    return true;
                }
                case "vo_rooms2":
                {
                    CellRect rg = new CellRect(voO + 4, voGz - 30, 7, 5);
                    Room inner = ProjectZ.CellOnFloor(rg.CenterCell, 1, map).GetRoom(map);
                    Room ground = VG(20, voGz - 1).GetRoom(map);
                    Room air = VU(40, 40, map).GetRoom(map);
                    Room nearEdge = VU(40, 0, map).GetRoom(map);
                    Room voidRoom = VG(40, voGz + 1).GetRoom(map);
                    Result("rooms: a walled, roofed floor-2 room is its own indoor room", inner != null && !inner.UsesOutdoorTemperature && !inner.PsychologicallyOutdoors && inner.CellCount == 15,
                        $"cells {inner?.CellCount}, outdoor temp {inner?.UsesOutdoorTemperature}, outdoors {inner?.PsychologicallyOutdoors}");
                    Result("rooms: the ground's outdoors touches the map edge", ground != null && ground.TouchesMapEdge && ground.PsychologicallyOutdoors, $"cells {ground?.CellCount}");
                    VoExpect("rooms: floor 2's open air is its own outdoor room (not the ground's), the void has none",
                        air != null && air != ground && nearEdge == air && air.UsesOutdoorTemperature && air.PsychologicallyOutdoors && voidRoom == null,
                        $"floor-2 air room {air?.ID} cells {air?.CellCount} outdoor temp {air?.UsesOutdoorTemperature} outdoors {air?.PsychologicallyOutdoors}; ground room {ground?.ID} cells {ground?.CellCount}; void room {voidRoom?.ID.ToString() ?? "none"}");
                    Wait(5, "vo_gas");
                    return true;
                }
                case "vo_gas":
                {
                    ThingDef tox = DefDatabase<ThingDef>.GetNamedSilentFail("Weapon_GrenadeTox");
                    if (tox == null)
                    {
                        Result("gas: tox grenade", false, "Weapon_GrenadeTox missing (Biotech?)");
                        Wait(5, "vo_light");
                        return true;
                    }
                    Pawn t = VoPawn(map, "gas thrower", VG(5, voGz - 8), "Weapon_GrenadeTox");
                    VoAttack(t, VG(5, voGz - 1));
                    Info($"gas: {t} at {t.Position} throws tox at {VG(5, voGz - 1)} (the ground's north row)");
                    StartTicks(2700, "vo_gas2");
                    return true;
                }
                case "vo_gas2":
                {
                    int ground = GasIn(map, VoRect(voO - 5, voGz - 10, 22, 10));
                    int vd = GasIn(map, VoidCells(map, voO - 5, 22));
                    int f1 = GasIn(map, Floor1South(map, voO - 5, 22, 10));
                    Result("gas: the grenade's gas spread on the ground", ground > 3, $"{ground} ground cells with gas");
                    VoExpect("gas: none in the void, none on floor 2's south rows", vd == 0 && f1 == 0, $"void cells with gas {vd}, floor-2 cells {f1}");
                    VoAway(map);
                    Wait(5, "vo_light");
                    return true;
                }
                case "vo_light":
                {
                    voBefore = Floor1South(map, voO + 11, 9, 3).Sum(c => (float)map.glowGrid.VisualGlowAt(c).r + map.glowGrid.VisualGlowAt(c).g + map.glowGrid.VisualGlowAt(c).b);
                    Thing torch = Place(DefDatabase<ThingDef>.GetNamed("TorchLamp"), null, VG(15, voGz - 1), map);
                    CompRefuelable fuel = torch.TryGetComp<CompRefuelable>();
                    fuel?.Refuel(fuel.Props.fuelCapacity);
                    torch.TryGetComp<CompGlower>()?.UpdateLit(map);
                    Wait(30, "vo_light2");
                    return true;
                }
                case "vo_light2":
                {
                    float after = Floor1South(map, voO + 11, 9, 3).Sum(c => (float)map.glowGrid.VisualGlowAt(c).r + map.glowGrid.VisualGlowAt(c).g + map.glowGrid.VisualGlowAt(c).b);
                    Color32 near = map.glowGrid.VisualGlowAt(VG(15, voGz - 3));
                    Result("light: the torch lights the ground", near.r + near.g + near.b > 0, $"glow 2 cells south {near}");
                    VoExpect("light: floor 2's south rows across the gap stay as dark as before", Mathf.Abs(after - voBefore) < 0.5f, $"floor-2 glow sum {voBefore} -> {after}");
                    Wait(5, "vo_beauty");
                    return true;
                }
                case "vo_beauty":
                {
                    voBefore = BeautyUtility.AverageBeautyPerceptible(VG(25, voGz - 1), map);
                    VoSlab(map, new CellRect(voO + 22, 0, 7, 2));
                    ThingDef sculpt = DefDatabase<ThingDef>.GetNamed("SculptureSmall");
                    for (int i = 0; i < 4; i++)
                    {
                        Thing s = Place(sculpt, DefDatabase<ThingDef>.GetNamed("BlocksMarble"), VU(23 + i, 0, map), map);
                        s.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Legendary, ArtGenerationContext.Colony);
                    }
                    Wait(5, "vo_beauty2");
                    return true;
                }
                case "vo_beauty2":
                {
                    float after = BeautyUtility.AverageBeautyPerceptible(VG(25, voGz - 1), map);
                    VoExpect("beauty: sculptures on floor 2's south row don't count for a ground cell 4 raw rows away", Mathf.Abs(after - voBefore) < 0.01f, $"beauty at {VG(25, voGz - 1)}: {voBefore:0.00} -> {after:0.00}");
                    List<Thing> around = GenRadial.RadialDistinctThingsAround(VG(25, voGz - 1), map, 6f, true).ToList();
                    int up = around.Count(t => ProjectZ.FloorOf(t.Position, map) != 0);
                    VoExpect("radial things around a ground cell stay on the ground", up == 0, $"{around.Count} things, {up} off the ground band ({string.Join(", ", around.Where(t => ProjectZ.FloorOf(t.Position, map) != 0).Select(t => t.def.defName).Distinct())})");
                    Wait(5, "vo_shield");
                    return true;
                }
                // ---- shield bubbles: a skipshield (4.9) on the ground's north row
                case "vo_shield":
                {
                    VoShield(map);
                    VoSlab(map, new CellRect(voO + 40, 0, 17, 2));
                    VoSlab(map, new CellRect(voO + 45, voGz - 12, 7, 5));
                    voLaunches = voIntercepts = 0;
                    VoAttack(VoPawn(map, "ground shooter", VG(40, voGz - 2), "Gun_BoltActionRifle"), VG(56, voGz - 2));
                    StartTicks(400, "vo_shield2");
                    return true;
                }
                case "vo_shield2":
                {
                    Result("shield: a ground shot across the dome is stopped (the game's own check)", voLaunches > 0 && voIntercepts > 0, $"{voLaunches} shots, {voIntercepts} stopped");
                    VoAway(map);
                    VoShield(map);
                    voLaunches = voIntercepts = 0;
                    VoAttack(VoPawn(map, "floor-2 shooter", VU(42, 0, map), "Gun_BoltActionRifle"), VU(54, 0, map));
                    StartTicks(400, "vo_shield3");
                    return true;
                }
                case "vo_shield3":
                {
                    VoExpect("shield: a floor-2 shot along floor 2's south row (4 raw rows from the dome) passes", voLaunches > 0 && voIntercepts == 0, $"{voLaunches} shots, {voIntercepts} stopped; 3D checks {ProjectileInterceptor_3D_Patch.Checked}");
                    VoAway(map);
                    VoShield(map);
                    voLaunches = voIntercepts = 0;
                    int crossBefore = PZCombatStats.crossFloorShots;
                    Pawn s = VoPawn(map, "floor-2 shooter above", VU(48, voGz - 8, map), "Gun_BoltActionRifle");
                    VoAttack(s, VG(48, voGz - 2));
                    voFireMax = crossBefore;
                    StartTicks(400, "vo_shield4");
                    return true;
                }
                case "vo_shield4":
                {
                    int cross = PZCombatStats.crossFloorShots - voFireMax;
                    Result("shield: a shot down from floor 2 (outside the dome) into it is stopped in 3D (cross-floor sight works)", voLaunches > 0 && cross > 0 && voIntercepts > 0,
                        $"{voLaunches} shots ({cross} across floors), {voIntercepts} stopped; 3D entries {ProjectileInterceptor_3D_Patch.Entered}");
                    VoAway(map);
                    foreach (Thing t in map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("BulletShieldPsychic")).ToList())
                    {
                        t.Destroy();
                    }
                    Wait(5, "vo_aura");
                    return true;
                }
                // ---- aura (mech booster 9.9) and the mechanitor's range (24.9)
                case "vo_aura":
                {
                    ThingDef booster = DefDatabase<ThingDef>.GetNamedSilentFail("MechBooster");
                    PawnKindDef militor = DefDatabase<PawnKindDef>.GetNamedSilentFail("Mech_Militor");
                    if (booster == null || militor == null)
                    {
                        Result("aura", false, "MechBooster / Mech_Militor missing (Biotech?)");
                        Wait(5, "vo_tornado");
                        return true;
                    }
                    Thing b = Place(booster, null, VG(65, voGz - 2), map);
                    CompPowerTrader pt = b.TryGetComp<CompPowerTrader>();
                    if (pt != null)
                    {
                        pt.PowerOn = true;
                    }
                    VoSlab(map, new CellRect(voO + 63, 0, 5, 2));
                    VoSlab(map, new CellRect(voO + 63, voGz - 4, 5, 3));
                    foreach (var kv in new[] { ("mech on floor 2 across the gap", VU(65, 0, map)), ("mech on floor 2 over the booster", VU(66, voGz - 2, map)), ("mech on the ground 6 off", VG(71, voGz - 2)) })
                    {
                        Pawn m = PawnGenerator.GeneratePawn(new PawnGenerationRequest(militor, Faction.OfPlayer));
                        GenSpawn.Spawn(m, kv.Item2, map);
                        voPawns[kv.Item1] = m;
                    }
                    StartTicks(200, "vo_aura2");
                    return true;
                }
                case "vo_aura2":
                {
                    HediffDef boost = DefDatabase<HediffDef>.GetNamed("MechBoost");
                    bool Has(string n) => voPawns[n].health.hediffSet.HasHediff(boost);
                    string d = string.Join("; ", voPawns.Select(kv => $"{kv.Key} at {kv.Value.Position}: {Has(kv.Key)} (3D {Range3D.Distance(kv.Value.Position, VG(65, voGz - 2), map):0.0}, raw {kv.Value.Position.DistanceTo(VG(65, voGz - 2)):0.0})"));
                    Result("aura: a mech on the ground within range is boosted", Has("mech on the ground 6 off"), d);
                    Result("aura: a mech on floor 2 over the booster (3D in range) is boosted", Leak ? !Has("mech on floor 2 over the booster") : Has("mech on floor 2 over the booster"), d);
                    VoExpect("aura: a mech on floor 2 across the gap (raw 4, 3D far) is not", !Has("mech on floor 2 across the gap"), d);
                    VoAway(map);
                    // mechanitor
                    Pawn mn = VoPawn(map, "mechanitor", VG(80, voGz - 2));
                    mn.health.AddHediff(HediffDefOf.MechlinkImplant, mn.health.hediffSet.GetBrain());
                    Pawn mech = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Mech_Militor"), Faction.OfPlayer));
                    GenSpawn.Spawn(mech, VG(82, voGz - 4), map);
                    mn.relations.AddDirectRelation(PawnRelationDefOf.Overseer, mech);
                    voPawns["mech"] = mech;
                    VoSlab(map, new CellRect(voO + 78, voGz - 4, 5, 3));
                    VoSlab(map, new CellRect(voO + 78, 0, 5, 2));
                    IntVec3 above = VU(80, voGz - 3, map);
                    IntVec3 across = VU(80, 0, map);
                    IntVec3 far = VG(80, voGz - 30);
                    bool hasTracker = mn.mechanitor != null;
                    bool cAbove = hasTracker && mn.mechanitor.CanCommandTo(above);
                    bool cAcross = hasTracker && mn.mechanitor.CanCommandTo(across);
                    bool cFar = hasTracker && mn.mechanitor.CanCommandTo(far);
                    bool rAcross = MechanitorUtility.InMechanitorCommandRange(mech, across);
                    bool rAbove = MechanitorUtility.InMechanitorCommandRange(mech, above);
                    string md = $"tracker {hasTracker}; over him {cAbove}/{rAbove} (3D {Range3D.Distance(mn.Position, above, map):0.0}), across the gap {cAcross}/{rAcross} (raw {mn.Position.DistanceTo(across):0.0}), 28 south {cFar}";
                    Result("mechanitor: range on the ground unchanged (28 off: out)", hasTracker && !cFar, md);
                    Result("mechanitor: a cell on floor 2 over him is in range (3D)", Leak || cAbove && rAbove, md);
                    VoExpect("mechanitor: a cell on floor 2 across the gap is out of range (3D far), also through InMechanitorCommandRange", !cAcross && !rAcross, md);
                    VoAway(map);
                    Wait(5, "vo_tornado");
                    return true;
                }
                // ---- tornado driving north into the void
                case "vo_tornado":
                {
                    VoSlab(map, new CellRect(voO + 30, 0, 11, 3));
                    voHp.Clear();
                    for (int x = 31; x <= 39; x += 2)
                    {
                        Thing w = Place(ThingDefOf.Wall, ThingDefOf.WoodLog, VU(x, 1, map), map);
                        voHp[w] = w.HitPoints;
                    }
                    foreach (IntVec3 c in Floor1South(map, voO + 30, 11, 3))
                    {
                        if (c.GetEdifice(map) == null)
                        {
                            map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                        }
                    }
                    voRoofs = Floor1South(map, voO + 30, 11, 3).Count(c => map.roofGrid.Roofed(c));
                    voPawns["on floor 2 by the walls"] = VoPawn(map, "on floor 2 by the walls", VU(36, 0, map));
                    voBefore = Injuries(voPawns["on floor 2 by the walls"]);
                    voTornado = GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Tornado")), VG(35, voGz - 3), map);
                    Traverse.Create(voTornado).Field("direction").SetValue(270f);
                    voTornadoMaxZ = 0;
                    TickUntil(2500, () =>
                    {
                        if (voTornado.Spawned)
                        {
                            voTornadoMaxZ = Math.Max(voTornadoMaxZ, voTornado.Position.z);
                        }
                        return voTornado.Destroyed;
                    }, "vo_tornado2", 5);
                    return true;
                }
                case "vo_tornado2":
                {
                    tickWatch = null;
                    int damaged = voHp.Count(kv => kv.Key.Destroyed || kv.Key.HitPoints < kv.Value);
                    int roofs = Floor1South(map, voO + 30, 11, 3).Count(c => map.roofGrid.Roofed(c));
                    Pawn p = voPawns["on floor 2 by the walls"];
                    VoExpect("tornado: ends at the void like at the map edge, never onto floor 2", voTornado.Destroyed && voTornadoMaxZ < voGz, $"destroyed {voTornado.Destroyed}, furthest north row {voTornadoMaxZ} (ground rows {voGz}), after {ticksTotal} ticks");
                    VoExpect("tornado: nothing on floor 2's south rows damaged, no roof lost", damaged == 0 && roofs == voRoofs && (p.Dead || Injuries(p) == (int)voBefore) && !p.Dead,
                        $"walls damaged {damaged} of {voHp.Count}, roofs {voRoofs} -> {roofs}, colonist injuries {voBefore} -> {Injuries(p)}");
                    VoAway(map);
                    Wait(5, "vo_storm");
                    return true;
                }
                // ---- flashstorm: centre on the ground near the north edge, then one on a floor-2 slab
                case "vo_storm":
                {
                    GameConditionDef def = DefDatabase<GameConditionDef>.GetNamedSilentFail("Flashstorm");
                    if (def == null)
                    {
                        Result("flashstorm", false, "def missing (Royalty?)");
                        Finish();
                        return true;
                    }
                    GameCondition_Flashstorm fs = (GameCondition_Flashstorm)GameConditionMaker.MakeCondition(def, 20000);
                    fs.centerLocation = new IntVec2(voO + 20, voGz - 3);
                    fs.areaRadiusOverride = new IntRange(7, 7);
                    map.gameConditionManager.RegisterCondition(fs);
                    voStorm = fs;
                    voStrikeFrom = voStrikes.Count;
                    TickUntil(9000, () => voStrikes.Count - voStrikeFrom >= 8, "vo_storm2", 30);
                    return true;
                }
                case "vo_storm2":
                {
                    tickWatch = null;
                    List<IntVec3> s = voStrikes.Skip(voStrikeFrom).ToList();
                    int off = s.Count(c => ProjectZ.FloorOf(c, map) != 0);
                    Result("flashstorm on the ground: every strike on the ground band", s.Count > 0 && off == 0, $"{s.Count} strikes, {off} off the band: {string.Join(" ", s.Take(12))}");
                    voStorm.End();
                    VoSlab(map, new CellRect(voO + 60, 20, 16, 12));
                    GameCondition_Flashstorm fs = (GameCondition_Flashstorm)GameConditionMaker.MakeCondition(DefDatabase<GameConditionDef>.GetNamed("Flashstorm"), 20000);
                    IntVec3 c2 = VU(67, 25, map);
                    fs.centerLocation = new IntVec2(c2.x, c2.z);
                    fs.areaRadiusOverride = new IntRange(5, 5);
                    map.gameConditionManager.RegisterCondition(fs);
                    voStorm = fs;
                    voStrikeFrom = voStrikes.Count;
                    TickUntil(9000, () => voStrikes.Count - voStrikeFrom >= 6, "vo_storm3", 30);
                    return true;
                }
                case "vo_storm3":
                {
                    tickWatch = null;
                    List<IntVec3> s = voStrikes.Skip(voStrikeFrom).ToList();
                    int off = s.Count(c => ProjectZ.FloorOf(c, map) != 1);
                    VoExpect("flashstorm cast on floor 2: it strikes, every strike on floor 2", s.Count > 0 && off == 0, $"{s.Count} strikes, {off} off the band: {string.Join(" ", s.Take(12))}");
                    voStorm.End();
                    Finish();
                    return true;
                }

                // ================================================================ voidfire (1 void row)
                case "vf_setup":
                {
                    AnnexMapComponent an = ProjectZ.Annex(map);
                    voGz = an.groundZ;
                    Info($"voidfire: ground rows {voGz}, stride {an.stride}, void rows {an.stride - voGz}, rule {(Leak ? "OFF" : "on")}");
                    if (an.stride - voGz != 1)
                    {
                        Result("voidfire setup", false, "needs a 1-row gap (-projectzmapsize=101)");
                        Finish();
                        return true;
                    }
                    voO = PickField(map, 50, 12, 6);
                    ExClear(map, new CellRect(voO - 1, voGz - 12, 52, 12));
                    VoColonistsAway(map);
                    // grass on the ground's 3 north rows, wood slabs with wood on floor 2's 2 south rows
                    ThingDef grass = DefDatabase<ThingDef>.GetNamed("Plant_Grass");
                    foreach (IntVec3 c in VoRect(voO, voGz - 3, 30, 3))
                    {
                        Plant pl = (Plant)ThingMaker.MakeThing(grass);
                        pl.Growth = 1f;
                        GenSpawn.Spawn(pl, c, map);
                    }
                    VoSlab(map, new CellRect(voO, 0, 30, 2));
                    foreach (IntVec3 c in Floor1South(map, voO, 30, 2))
                    {
                        Thing w = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                        w.stackCount = 75;
                        GenSpawn.Spawn(w, c, map);
                    }
                    // a room on floor 2 against the one void row below it: walls west, north, east, roofed, its south side
                    // the void (its wall-exchange cells two rows out are on the ground's north row)
                    CellRect r = new CellRect(voO + 38, 0, 7, 4);
                    VoSlab(map, r);
                    foreach (IntVec3 g in r)
                    {
                        IntVec3 c = ProjectZ.CellOnFloor(g, 1, map);
                        if (g.x == r.minX || g.x == r.maxX || g.z == r.maxZ)
                        {
                            Place(ThingDefOf.Wall, ThingDefOf.BlocksGranite, c, map);
                        }
                        else
                        {
                            map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                        }
                    }
                    // (wood on the ground's north row too: fires there last, and keep throwing sparks)
                    foreach (IntVec3 c in VoRect(voO, voGz - 1, 30, 1))
                    {
                        Thing w = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                        w.stackCount = 75;
                        GenSpawn.Spawn(w, c, map);
                    }
                    for (int x = 0; x < 30; x += 2)
                    {
                        FireUtility.TryStartFireIn(VG(x, voGz - 1), map, 1f, null);
                    }
                    voFireOnFloor = 0;
                    voFireMax = 0;
                    TickUntil(6000, () =>
                    {
                        List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
                        voFireMax = Math.Max(voFireMax, fires.Count);
                        voFireOnFloor = Math.Max(voFireOnFloor, fires.Count(f => ProjectZ.FloorOf(f.Position, map) != 0));
                        return fires.Count == 0;
                    }, "vf_fire2", 10);
                    return true;
                }
                case "vf_fire2":
                {
                    tickWatch = null;
                    int wood = Floor1South(map, voO, 30, 2).Sum(c => c.GetThingList(map).Where(t => t.def == ThingDefOf.WoodLog).Sum(t => t.stackCount));
                    Result("fire: the grass burned", voFireMax > 5, $"up to {voFireMax} fires, after {ticksTotal} ticks");
                    Result("fire: no spark across the one void row onto floor 2's wood (sparks are projectiles: also stopped by the slab from below)", voFireOnFloor == 0 && wood == 30 * 2 * 75, $"fires on floor 2 at most {voFireOnFloor}; wood left there {wood} of {30 * 2 * 75}; fire chance asked for floor-2 cells {voSparkTries} times ({voSparkOk} above 0), flat sight refused {LineOfSight_Void_Patch.Refused}");
                    Room room = VU(41, 0, map).GetRoom(map);
                    List<IntVec3> eq = room?.TempTracker?.EqualizeCellsForReading ?? new List<IntVec3>();
                    int offBand = eq.Count(c => !ProjectZ.SameBand(c, room.Cells.First(), map));
                    Result("temperature: a floor-2 room over the one void row exchanges with nothing on the ground", Leak || offBand == 0 && RegenerateEqualization_Void_Patch.Dropped > 0 && !room.UsesOutdoorTemperature, $"room {room?.ID} cells {room?.CellCount} outdoor temp {room?.UsesOutdoorTemperature} touches edge {room?.TouchesMapEdge}; equalize cells {eq.Count}, on another band {offBand}; dropped so far {RegenerateEqualization_Void_Patch.Dropped}");
                    Finish();
                    return true;
                }

                // ================================================================ voidsave / voidload
                case "vs_setup":
                {
                    AnnexMapComponent an = ProjectZ.Annex(map);
                    voGz = an.groundZ;
                    voO = PickField(map, 20, 10, 2);
                    ExClear(map, new CellRect(voO - 1, voGz - 10, 22, 10));
                    VoColonistsAway(map);
                    Pawn t = VoPawn(map, "gas thrower", VG(5, voGz - 8), "Weapon_GrenadeTox");
                    VoAttack(t, VG(5, voGz - 1));
                    StartTicks(1500, "vs_save");
                    return true;
                }
                case "vs_save":
                {
                    int vd = GasIn(map, VoidCells(map, 0, map.Size.x));
                    Result("voidsave: gas leaked into the void (rule off)", !Leak || vd > 0, $"void cells with gas {vd}");
                    VoAway(map);
                    GameDataSaveLoader.SaveGame("pz_voidold");
                    Result("saved pz_voidold", System.IO.File.Exists(GenFilePaths.FilePathForSavedGame("pz_voidold")), $"void gas cells {vd}");
                    Finish();
                    return true;
                }
                case "vl_load":
                    pendingLoad = "pz_voidold";
                    stage = "vl_check";
                    return true;
                case "vl_check":
                {
                    AnnexMapComponent an = ProjectZ.Annex(map);
                    int vd = GasIn(map, VoidCells(map, 0, map.Size.x));
                    IntVec3 v = new IntVec3(map.Size.x / 2, 0, an.groundZ + 1);
                    Room ground = new IntVec3(map.Size.x / 2, 0, an.groundZ - 1).GetRoom(map);
                    Room air = ProjectZ.CellOnFloor(new IntVec3(map.Size.x / 2, 0, an.groundZ / 2), 1, map).GetRoom(map);
                    Result("voidload: the old save's void gas is cleared on load", vd == 0 && VoidRule.LastGasCleared > 0, $"void gas cells now {vd}, cleared on load {VoidRule.LastGasCleared}");
                    Result("voidload: no void regions, floor 2's open air its own room", v.GetRegion(map, RegionType.Set_All) == null && air != null && air != ground && LightBlockerBit(map, v), $"void region {v.GetRegion(map, RegionType.Set_All)?.type.ToString() ?? "none"}, ground room {ground?.ID}, floor-2 room {air?.ID}");
                    StartTicks(600, "vl_end");
                    return true;
                }
                case "vl_end":
                    Result("voidload: ticks on", true, "600 ticks");
                    Finish();
                    return true;

                // ================================================================ voidperf
                case "vp_setup":
                {
                    AnnexMapComponent an = ProjectZ.Annex(map);
                    Stopwatch sw = Stopwatch.StartNew();
                    map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                    double rebuild = sw.Elapsed.TotalMilliseconds;
                    int regions = map.regionGrid.AllRegions_NoRebuild_InvalidAllowed.Count();
                    int rooms = map.regionGrid.AllRooms.Count;
                    sw.Restart();
                    VoidRule.Apply(map);
                    double apply = sw.Elapsed.TotalMilliseconds;
                    Info($"voidperf: map {map.Size}, floors {an.floors}, rule {(Leak ? "OFF" : "on")}: full region rebuild {rebuild:0.0} ms ({regions} regions, {rooms} rooms); void rule apply {apply:0.0} ms over {VoidRule.LastCells} cells (on load: {VoidRule.LastApplyMs:0.0} ms)");
                    Result("voidperf: measured", true, $"rebuild {rebuild:0.0} ms, apply {apply:0.0} ms");
                    Finish();
                    return true;
                }

                // ================================================================ crush (task B)
                case "cr_setup":
                {
                    AnnexMapComponent an = ProjectZ.Annex(map);
                    voGz = an.groundZ;
                    voO = PickField(map, 20, 30, 2);
                    ExClear(map, new CellRect(voO - 1, voGz - 30, 22, 30));
                    VoColonistsAway(map);
                    VoSlab(map, new CellRect(voO + 5, voGz - 25, 9, 9));
                    Pawn t = VoPawn(map, "thrower", VU(9, voGz - 18, map), "Weapon_GrenadeFrag");
                    VoPawn(map, "under the slab", VG(9, voGz - 22));
                    voFireMax = Injuries(voPawns["under the slab"]);   // (old wounds of a generated colonist)
                    voBefore = Find.LetterStack.LettersListForReading.Count;
                    VoAttack(t, VU(9, voGz - 22, map));
                    Info($"crush: {t} on floor 2 throws a frag grenade at {VU(9, voGz - 22, map)} (wood slab), a colonist under it at {VG(9, voGz - 22)}");
                    TickUntil(1500, () => SlabDamage.Broken > 0 && !ProjectZ.HasFloor(VU(9, voGz - 22, map), map), "cr_check", 1);
                    return true;
                }
                case "cr_check":
                {
                    tickWatch = null;
                    StartTicks(5, "cr_check2");
                    return true;
                }
                case "cr_check2":
                {
                    Pawn p = voPawns["under the slab"];
                    bool crushed = Ceilinged(p);   // (the game logged the falling roof on it; armour may stop the damage)
                    List<Letter> letters = Find.LetterStack.LettersListForReading.Skip((int)voBefore).ToList();
                    bool letter = letters.Any(l => l.Label.ToString() == "LetterLabelRoofCollapsed".Translate().ToString() && l is ChoiceLetter cl && cl.Text.ToString().Contains(p.LabelCap));
                    IntVec3 below = VG(9, voGz - 22);
                    Result("crush: the slab broke and the roof under it came down", SlabDamage.Broken > 0 && !ProjectZ.HasFloor(VU(9, voGz - 22, map), map) && !map.roofGrid.Roofed(below),
                        $"broken {SlabDamage.Broken}, roof under it {map.roofGrid.RoofAt(below)?.defName ?? "none"}");
                    Result("crush: the colonist under it is crushed (the game's roof crush)", crushed,
                        $"{p} dead {p.Dead}; ceiling hit logged {crushed}, injuries before {voFireMax}, now: {string.Join(", ", p.health.hediffSet.hediffs.OfType<Hediff_Injury>().Select(h => h.def.defName + " " + h.Severity.ToString("0")))}");
                    Result("crush: the game's roof-collapsed letter, naming the colonist", letter, $"new letters: {string.Join(" | ", letters.Select(l => l.Label.ToString() + ": " + ((l as ChoiceLetter)?.Text.ToString() ?? "").Replace('\n', ' ')))}");
                    Pawn t = voPawns["thrower"];
                    Info($"crush: thrower now at {t.Position} (floor {ProjectZ.FloorOf(t.Position, map) + 1}), injuries {Injuries(t)}");
                    Finish();
                    return true;
                }
            }
            return false;
        }
    }
}
