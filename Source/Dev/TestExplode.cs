using System;
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
    // Mode explode (explosions across floors, Explosions3D; run with -projectzfloors=2 on the default 150 map: 3 void
    // rows between the bands). A cleared field at the ground's north edge, the same columns' south rows, floor-2 slabs:
    //   - grenade: a drafted colonist on slab P1 throws a frag grenade at P1's south edge row (a real AttackStatic job):
    //     a colonist on the ground under the slab is unhurt, one on the slab next to the blast is hit;
    //   - shell: a high-explosive mortar shell launched at the ground under P1's edge (ProjectZ lifts it onto the slab):
    //     the ground cell past the edge is hit (3D 1.9 of 2.9), the one under the slab and one 3 rows out are not;
    //   - doomsday: a doomsday rocket launched along the ground at a cell 3 rows from the ground's north edge (7.8 radius,
    //     plus its 3 flame explosions): floor-2 slab P2's north row (3D 3.4) is hit, a colonist 7 rows in on P2 (3D 7.2,
    //     behind the slab edge) is not, a ground colonist 3 cells off is; the colonists on floor 2's south rows (6 and 7
    //     raw rows away across the void gap: the old leak) are unhurt.
    // Probed for every explosion (a test postfix on Explosion.AffectCell): no cell in a void row, none on another floor
    // beyond the 3D radius or over open air, the spread timing on the 3D distance; new fire and filth only within reach.
    // -projectznoexplfix: the game's flat explosions (Explosions3D.Off), to show the leak.
    public static partial class TestRunner
    {
        internal sealed class ExRec
        {
            public Explosion e;
            public IntVec3 centre;
            public float radius;
            public string dam;
            public int start;
            public float speed;
            public readonly List<KeyValuePair<IntVec3, int>> cells = new List<KeyValuePair<IntVec3, int>>();
        }

        internal static readonly List<ExRec> exRecs = new List<ExRec>();

        private static int exO, exGz, exFrom;
        private static HashSet<int> exBefore;
        private static string exCase;
        private static readonly Dictionary<string, Pawn> exPawns = new Dictionary<string, Pawn>();
        private static readonly Dictionary<string, int> exInj = new Dictionary<string, int>();

        internal static void PatchExplodeProbe(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Explosion), nameof(Explosion.StartExplosion)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(ExStartPrefix)));
            h.Patch(AccessTools.Method(typeof(Explosion), "AffectCell"), prefix: new HarmonyMethod(typeof(TestRunner), nameof(ExAffectPrefix)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(ExAffectPostfix)));
        }

        private static void ExStartPrefix(Explosion __instance)
        {
            exRecs.Add(new ExRec { e = __instance, centre = __instance.Position, radius = __instance.radius, dam = __instance.damType?.defName, start = Find.TickManager.TicksGame, speed = __instance.propagationSpeed });
        }

        // Cells that had a floor when the blast reached them (a heavy hit may break the slab right after: SlabDamage).
        internal static readonly HashSet<IntVec3> exHadFloor = new HashSet<IntVec3>();

        private static void ExAffectPrefix(Explosion __instance, IntVec3 c)
        {
            if (__instance.Map != null && ProjectZ.HasFloor(c, __instance.Map))
            {
                exHadFloor.Add(c);
            }
        }

        private static void ExAffectPostfix(Explosion __instance, IntVec3 c, bool __runOriginal)
        {
            if (!__runOriginal)
            {
                return;
            }
            for (int i = exRecs.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(exRecs[i].e, __instance))
                {
                    exRecs[i].cells.Add(new KeyValuePair<IntVec3, int>(c, Find.TickManager.TicksGame));
                    return;
                }
            }
        }

        private static IntVec3 EG(int x, int z) => new IntVec3(exO + x, 0, z);          // ground, z absolute

        private static IntVec3 EU(int x, int z, Map map) => ProjectZ.CellOnFloor(EG(x, z), 1, map);

        private static Pawn ExPawn(Map map, string name, IntVec3 at)
        {
            Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true, mustBeCapableOfViolence: true));
            GenSpawn.Spawn(p, at, map);
            Draft(p, true);
            exPawns[name] = p;
            exInj[name] = Injuries(p);
            return p;
        }

        private static bool ExHit(string name)
        {
            Pawn p = exPawns[name];
            return p.Dead || Injuries(p) > exInj[name];
        }

        private static string ExDesc(string name, Map map)
        {
            Pawn p = exPawns[name];
            IntVec3 at = p.Dead ? p.Corpse?.Position ?? IntVec3.Invalid : p.Position;
            return $"{name} at {at} (floor {ProjectZ.FloorOf(at, map) + 1}): {(p.Dead ? "dead" : $"{Injuries(p) - exInj[name]} new injuries")} ({string.Join(", ", p.health.hediffSet.hediffs.OfType<Hediff_Injury>().Select(h => h.def.defName + (h.sourceDef != null ? " by " + h.sourceDef.defName : "")))})";
        }

        private static void ExSnapshot(Map map, string name)
        {
            exCase = name;
            exFrom = exRecs.Count;
            exBefore = new HashSet<int>(map.listerThings.ThingsOfDef(ThingDefOf.Fire).Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.Filth)).Select(t => t.thingIDNumber));
        }

        private static bool ExDone() => exRecs.Count > exFrom && exRecs.Skip(exFrom).All(r => !r.e.Spawned);

        // The probes over every explosion of this case.
        private static void ExCheck(Map map)
        {
            List<ExRec> recs = exRecs.Skip(exFrom).ToList();
            int voids = 0, beyond = 0, overAir = 0, cross = 0, own = 0, late = 0;
            List<string> bad = new List<string>();
            foreach (ExRec r in recs)
            {
                int kc = ProjectZ.FloorOf(r.centre, map);
                Dictionary<int, int> perFloor = new Dictionary<int, int>();
                foreach (KeyValuePair<IntVec3, int> kv in r.cells)
                {
                    IntVec3 c = kv.Key;
                    int k = ProjectZ.FloorOf(c, map);
                    perFloor[k] = perFloor.TryGetValue(k, out int n) ? n + 1 : 1;
                    float d = c.DistanceTo(r.centre);
                    if (k < 0)
                    {
                        voids++;
                        bad.Add($"void {c}");
                        continue;
                    }
                    if (k != kc)
                    {
                        cross++;
                        d = Range3D.Distance(c, r.centre, map);
                        if (d > r.radius + 0.01f)
                        {
                            beyond++;
                            bad.Add($"{c} floor {k + 1}: 3D {d:0.0} > {r.radius}");
                        }
                        if (k >= 1 && !ProjectZ.HasFloor(c, map) && !exHadFloor.Contains(c))
                        {
                            overAir++;
                        }
                    }
                    else
                    {
                        own++;
                    }
                    int allowed = Mathf.Max(0, Mathf.FloorToInt(d * 1.5f / Mathf.Max(0.01f, r.speed))) + 1;
                    if (kv.Value - r.start > allowed)
                    {
                        late++;
                        bad.Add($"{c} late {kv.Value - r.start} > {allowed}");
                    }
                }
                Info($"{exCase}: {r.dam} r {r.radius} at {r.centre} (floor {kc + 1}): cells per floor {string.Join(", ", perFloor.OrderBy(x => x.Key).Select(x => (x.Key < 0 ? "void" : "floor " + (x.Key + 1)) + " " + x.Value))}");
            }
            Result($"{exCase}: explosions happened", recs.Count > 0 && own > 0, $"{recs.Count} explosions, {own} cells on their own floor, {cross} on other floors");
            Result($"{exCase}: no cell in a void row, none on another floor beyond the 3D radius or over open air", voids == 0 && beyond == 0 && overAir == 0,
                $"void {voids}, beyond {beyond}, open air {overAir}; {string.Join("; ", bad.Take(8))}");
            Result($"{exCase}: each cell is hit on the spread timing of its 3D distance", late == 0, $"late {late}");
            // new fire and filth: never in a void row, on another floor only within the 3D reach of a blast of this case
            int newOnes = 0, wrong = 0;
            List<string> wrongs = new List<string>();
            foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Fire).Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.Filth)).ToList())
            {
                if (exBefore.Contains(t.thingIDNumber) || t.def.defName.StartsWith("Filth_Blood"))
                {
                    continue;
                }
                newOnes++;
                IntVec3 c = t.Position;
                int k = ProjectZ.FloorOf(c, map);
                bool ok = k >= 0 && recs.Any(r => ProjectZ.FloorOf(r.centre, map) == k ? c.DistanceTo(r.centre) <= r.radius + 3f : Range3D.Distance(c, r.centre, map) <= r.radius + 1.5f);
                if (!ok)
                {
                    wrong++;
                    wrongs.Add($"{t.def.defName} {c} (floor {k + 1})");
                }
            }
            Result($"{exCase}: new fire and filth only within the blasts' 3D reach", wrong == 0, $"{newOnes} new, {wrong} out of reach: {string.Join(", ", wrongs.Take(8))}");
        }

        // Under a slab: unhurt, or (the slab over it broken by a heavy hit) hit by the falling roof (SlabDamage.Break).
        private static void ExCrushOrNot(string name, bool broken, Map map)
        {
            if (!broken)
            {
                ExExpect(name, false, "the slab is between", map);
                return;
            }
            Result($"{exCase}: {name}: the slab over it broke, the roof under it falls on it", Ceilinged(exPawns[name]), ExDesc(name, map));
        }

        private static void ExExpect(string name, bool hit, string why, Map map)
        {
            Result($"{exCase}: {name} {(hit ? "is hit" : "is unhurt")} ({why})", ExHit(name) == hit, ExDesc(name, map));
        }

        private static void ExClear(Map map, CellRect rect)
        {
            foreach (IntVec3 c in rect)
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (!(t is Pawn) && t.def.destroyable)
                    {
                        t.Destroy();
                    }
                }
                TerrainDef td = c.GetTerrain(map);
                if (td.IsWater || td.passability == Traversability.Impassable)
                {
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
            }
        }

        private static void ExSlab(Map map, CellRect ground)
        {
            foreach (IntVec3 g in ground)
            {
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                IntVec3 u = ProjectZ.CellOnFloor(g, 1, map);
                map.terrainGrid.SetTerrain(u, PZDefOf.PZ_UpperFloor);
                map.fogGrid.Unfog(u);
            }
        }

        private static void ExAway(Map map)
        {
            foreach (Pawn p in exPawns.Values.ToList())
            {
                if (p.Spawned && !p.Dead)
                {
                    Teleport(p, CellFinder.RandomClosewalkCellNear(new IntVec3(exO > map.Size.x / 2 ? 5 : map.Size.x - 6, 0, exGz / 2), map, 6));
                }
            }
            exPawns.Clear();
            exInj.Clear();
        }

        private static bool StepExplode(Map map)
        {
            switch (stage)
            {
                case "ex_setup":
                {
                    AnnexMapComponent an = ProjectZ.Annex(map);
                    if (an == null || an.floors < 2)
                    {
                        Result("explode setup", false, $"needs 2 floors (-projectzfloors=2); floors {an?.floors}");
                        Finish();
                        return true;
                    }
                    exGz = an.groundZ;
                    Result("patched: the explosion's distance calls swapped for the 3D one in each of its 10 methods", ExplosionDistance_Patch.PerMethod.Count == 10 && ExplosionDistance_Patch.PerMethod.Values.All(v => v >= 1), string.Join(", ", ExplosionDistance_Patch.PerMethod.Select(kv => kv.Key + " " + kv.Value)));
                    int best = -1, bestBad = int.MaxValue;
                    for (int x = 3; x + 40 < map.Size.x - 3; x += 4)
                    {
                        CellRect n = new CellRect(x, exGz - 32, 40, 32);
                        CellRect s = new CellRect(x, 0, 40, 8);
                        int bad = n.Cells.Concat(s.Cells).Count(c => c.GetThingList(map).Any(t => !t.def.destroyable) || c.GetTerrain(map).IsWater || c.GetTerrain(map).passability == Traversability.Impassable || map.roofGrid.RoofAt(c)?.isThickRoof == true);
                        if (bad < bestBad)
                        {
                            bestBad = bad;
                            best = x;
                        }
                    }
                    exO = best;
                    ExClear(map, new CellRect(exO - 1, exGz - 33, 42, 33));
                    ExClear(map, new CellRect(exO - 1, 0, 42, 9));
                    DebugSettings.enableRandomMentalStates = false;
                    foreach (Pawn p in Colonists(map))
                    {
                        Draft(p, true);
                        Teleport(p, CellFinder.RandomClosewalkCellNear(new IntVec3(exO > map.Size.x / 2 ? 5 : map.Size.x - 6, 0, exGz / 2), map, 6));
                    }
                    // P1 (grenade, shell): ground rows gz-25..gz-17, columns 26..34; P2 (doomsday): rows gz-12..gz-6, columns 7..13;
                    // L (floor 2's south rows over the ground's rows 0..5), columns 7..13
                    ExSlab(map, new CellRect(exO + 26, exGz - 25, 9, 9));
                    ExSlab(map, new CellRect(exO + 7, exGz - 12, 7, 7));
                    ExSlab(map, new CellRect(exO + 7, 0, 7, 6));
                    Info($"field x {exO}..{exO + 39}, ground rows {exGz}, stride {an.stride} (void rows {an.stride - exGz}), storey {Range3D.Storey:0.00} cells, bad cells {bestBad}");
                    Wait(5, "ex_grenade");
                    return true;
                }
                case "ex_grenade":
                {
                    ExSnapshot(map, "grenade");
                    Pawn t = ExPawn(map, "thrower", EU(30, exGz - 20, map));
                    t.skills.GetSkill(SkillDefOf.Shooting).Level = 20;
                    t.equipment.DestroyAllEquipment();
                    t.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Weapon_GrenadeFrag")));
                    ExPawn(map, "under the slab", EG(30, exGz - 24));
                    ExPawn(map, "on the ground past the edge", EG(30, exGz - 26));
                    ExPawn(map, "on the slab next to the blast", EU(31, exGz - 24, map));
                    IntVec3 x = EU(30, exGz - 25, map);
                    Job j = JobMaker.MakeJob(JobDefOf.AttackStatic, new LocalTargetInfo(x));
                    j.playerForced = true;
                    bool ok = t.jobs.TryTakeOrderedJob(j, JobTag.DraftedOrder);
                    Info($"grenade: {t} at {t.Position} (floor 2) throws at {x} (floor 2, the slab's south edge row): order {ok}");
                    TickUntil(1500, ExDone, "ex_grenade2", 1);
                    return true;
                }
                case "ex_grenade2":
                {
                    tickWatch = null;
                    Pawn t = exPawns["thrower"];
                    Info($"grenade: thrower job {t.CurJob?.def?.defName}, stance {t.stances?.curStance}, weapon {t.equipment?.Primary?.def?.defName}, violent {!t.WorkTagIsDisabled(WorkTags.Violent)}, after {ticksTotal} ticks");
                    t.jobs.StopAll();
                    ExCheck(map);
                    ExRec r = exRecs.Skip(exFrom).FirstOrDefault();
                    bool onSlab = r != null && ProjectZ.HasFloor(r.centre, map);
                    Info($"grenade landed at {r?.centre} (on a slab: {onSlab}), {(r == null ? -1 : r.centre.DistanceTo(EU(30, exGz - 25, map))):0.0} from the aim");
                    // (a heavy hit that breaks the slab over it crushes it with the roof under the slab: SlabDamage.Break)
                    bool brokenOver = !ProjectZ.HasFloor(EU(30, exGz - 24, map), map);
                    ExCrushOrNot("under the slab", brokenOver, map);
                    if (r != null && r.centre.DistanceTo(exPawns["on the slab next to the blast"].Position) <= 1.5f)
                    {
                        ExExpect("on the slab next to the blast", true, "same floor, next cell", map);
                    }
                    else
                    {
                        Info("grenade: " + ExDesc("on the slab next to the blast", map) + " (the grenade landed further off)");
                    }
                    Info("grenade: " + ExDesc("on the ground past the edge", map) + $" (3D {Range3D.Distance(exPawns["on the ground past the edge"].Position, r?.centre ?? IntVec3.Zero, map):0.00} of the frag's 1.9)");
                    StartTicks(30, "ex_shell");
                    return true;
                }
                case "ex_shell":
                {
                    ExAway(map);
                    ExSlab(map, new CellRect(exO + 26, exGz - 25, 9, 9));   // (laid again where the grenade broke it)
                    ExSnapshot(map, "shell");
                    ExPawn(map, "under the slab", EG(28, exGz - 24));
                    ExPawn(map, "on the ground past the edge", EG(28, exGz - 26));
                    ExPawn(map, "on the ground 3 rows out", EG(28, exGz - 28));
                    ExPawn(map, "on the slab 2 cells off", EU(29, exGz - 23, map));
                    IntVec3 g = EG(28, exGz - 25);
                    IntVec3 from = EG(28, 12);
                    Projectile shell = (Projectile)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("Bullet_Shell_HighExplosive"), from, map);
                    shell.Launch(Colonists(map).First(p => !exPawns.ContainsValue(p)), from.ToVector3Shifted(), new LocalTargetInfo(g), new LocalTargetInfo(g), ProjectileHitFlags.IntendedTarget);
                    Info($"shell launched from {from} at ground {g} (under P1's south edge row)");
                    TickUntil(1500, ExDone, "ex_shell2", 1);
                    return true;
                }
                case "ex_shell2":
                {
                    tickWatch = null;
                    ExCheck(map);
                    ExRec r = exRecs.Skip(exFrom).FirstOrDefault();
                    Result("shell: lands on the slab over its aim", r != null && r.centre == EU(28, exGz - 25, map), $"exploded at {r?.centre}");
                    bool shellBroke = !ProjectZ.HasFloor(EU(28, exGz - 24, map), map);
                    ExCrushOrNot("under the slab", shellBroke, map);
                    ExExpect("on the ground past the edge", true, $"3D {Range3D.Distance(EG(28, exGz - 26), EU(28, exGz - 25, map), map):0.00} of 2.9, past the edge", map);
                    ExExpect("on the ground 3 rows out", false, $"3D {Range3D.Distance(EG(28, exGz - 28), EU(28, exGz - 25, map), map):0.00} of 2.9", map);
                    ExExpect("on the slab 2 cells off", true, "same floor", map);
                    StartTicks(30, "ex_doom");
                    return true;
                }
                case "ex_doom":
                {
                    ExAway(map);
                    ExSnapshot(map, "doomsday");
                    int bx = 10;
                    ExPawn(map, "on slab P2's north row", EU(bx, exGz - 6, map));
                    ExPawn(map, "on P2 7 rows in", EU(bx, exGz - 10, map));
                    ExPawn(map, "on the ground 3 cells off", EG(bx + 3, exGz - 3));
                    ExPawn(map, "on floor 2's south row (6 raw rows off)", EU(bx, 0, map));
                    ExPawn(map, "on floor 2's second row (7 raw rows off)", EU(bx + 1, 1, map));
                    IntVec3 b = EG(bx, exGz - 3);
                    IntVec3 from = EG(bx, exGz - 25);
                    Projectile rocket = (Projectile)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("Bullet_DoomsdayRocket"), from, map);
                    rocket.Launch(Colonists(map).First(p => !exPawns.ContainsValue(p)), from.ToVector3Shifted(), new LocalTargetInfo(b), new LocalTargetInfo(b), ProjectileHitFlags.IntendedTarget);
                    Info($"doomsday rocket launched from {from} at {b} ({exGz - b.z} rows from the ground's north edge)");
                    TickUntil(600, ExDone, "ex_doom2", 1);
                    return true;
                }
                case "ex_doom2":
                {
                    tickWatch = null;
                    ExCheck(map);
                    IntVec3 b = EG(10, exGz - 3);
                    ExRec r = exRecs.Skip(exFrom).FirstOrDefault();
                    Result("doomsday: explodes where aimed", r != null && r.centre == b, $"at {r?.centre}");
                    ExExpect("on slab P2's north row", true, $"3D {Range3D.Distance(EU(10, exGz - 6, map), b, map):0.00} of 7.8, line past the slab edge", map);
                    ExExpect("on P2 7 rows in", false, $"3D {Range3D.Distance(EU(10, exGz - 10, map), b, map):0.00} of 7.8 but behind the slab edge", map);
                    ExExpect("on the ground 3 cells off", true, "same floor", map);
                    ExExpect("on floor 2's south row (6 raw rows off)", false, "the old leak", map);
                    ExExpect("on floor 2's second row (7 raw rows off)", false, "the old leak", map);
                    Finish();
                    return true;
                }
            }
            return false;
        }
    }
}
