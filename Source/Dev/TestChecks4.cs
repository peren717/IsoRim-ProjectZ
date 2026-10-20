using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using ProjectZ.Patches;

namespace ProjectZ.Dev
{
    // Mode checks4 (three checks on one map; run with -projectzfloors=3 -projectzseed=N -projectzriver, with or without
    // -projectztallmountains). Each check has its own fixture area and its own random state (Seeded).
    //   S  a wall, a door (and a column) built by colonists under an existing slab, a wall and a door placed (spawned)
    //      under another one: the slab and the roof under it stay; with the first holder gone the new wall holds the slab;
    //      with every holder gone it collapses (vanilla roof support, removal design §7).
    //   M  the drafted right-click menu on an enemy on another floor: melee (no route: not actionable; a route over stairs:
    //      the pawn walks up and every melee hit lands on the target's own floor), the stair-end pair (info), ranged
    //      (the menu follows the verb's can-hit, Los3D).
    //   F  Odyssey floods: a seasonal flood (river cells, a river fixture at the ground's north edge) and a torrential-rain
    //      flood (a pond fixture at the north edge): seeds only on the ground, and no flood water ever above the ground
    //      (void rows, open air, slabs, rock tops), while the water does reach the ground's north row (the ground spread
    //      rule, GroundSpreadPatches). The flood's checks accept the same terrain on a ground cell and refuse it on a band
    //      and a void cell (vanilla's own checks accept all three), and the hook runs during the real spread (not inlined).
    public static partial class TestRunner
    {
        private static CellRect k4S, k4S2, k4A, k4B;
        private static IntVec3 k4Column, k4EastWall, k4Col2;
        private static int k4Collapses, k4Letters, k4SlabCells;
        private static Pawn k4Melee, k4Gun, k4Enemy;
        private static Building_Stairs k4Stairs;
        private static readonly List<string> k4Hits = new List<string>();
        private static Pawn k4WatchAttacker, k4WatchVictim;
        private static List<StorytellerComp> k4Comps;
        private static Thing k4Flood;
        private static int k4FirstLeak, k4WatchTicks, k4FloodStart;
        private static string k4FloodName;
        private static int k4FloodCalls;
        private static CellRect k4River, k4Pond, k4FloorStrip;

        private static IntVec3 K4Up(IntVec3 g, Map map) => ProjectZ.CellOnFloor(g, 1, map);

        private static string K4Cell(IntVec3 c, Map map) => c.IsValid ? $"{c} lv {Levels.LevelOf(c, map)}{(ProjectZ.IsVoid(c, map) ? " void" : "")}" : "invalid";

        internal static void K4Patch(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.PreApplyDamage)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(K4DamagePrefix)));
        }

        private static void K4DamagePrefix(Pawn __instance, ref DamageInfo dinfo)
        {
            if (k4WatchVictim == null || __instance != k4WatchVictim || dinfo.Instigator != k4WatchAttacker || k4WatchAttacker?.Map == null)
            {
                return;
            }
            Map map = k4WatchAttacker.Map;
            bool same = ProjectZ.SameBand(k4WatchAttacker.Position, __instance.Position, map);
            bool adj = k4WatchAttacker.Position.AdjacentTo8WayOrInside(__instance.Position);
            k4Hits.Add($"{dinfo.Def.defName} from {K4Cell(k4WatchAttacker.Position, map)} to {K4Cell(__instance.Position, map)} {(same && adj ? "same floor, adjacent" : "CROSS-FLOOR or not adjacent")}");
        }

        // Clears a ground rect: no buildings, items, roofs, fog; water and rock become soil.
        private static void K4Clear(Map map, CellRect r)
        {
            TerrainDef soil = DefDatabase<TerrainDef>.GetNamed("Soil");
            foreach (IntVec3 c in r.ClipInsideRect(ProjectZ.GroundRect(map)))
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable && !(t is Pawn))
                    {
                        t.Destroy();
                    }
                }
                if (c.GetTerrain(map).IsWater || c.GetTerrain(map).passability != Traversability.Standable)
                {
                    map.terrainGrid.SetTerrain(c, soil);
                }
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
                // the column above: no buildings or items, open air (a tall mountain's rock tops)
                for (int k = 1; k <= Levels.Max(map); k++)
                {
                    IntVec3 u = ProjectZ.CellOnFloor(c, k, map);
                    if (!u.IsValid || !u.InBounds(map))
                    {
                        continue;
                    }
                    foreach (Thing t in u.GetThingList(map).ToList())
                    {
                        if (t.def.destroyable && !(t is Pawn))
                        {
                            t.Destroy();
                        }
                    }
                    if (u.GetTerrain(map) != PZDefOf.PZ_OpenAir)
                    {
                        map.terrainGrid.SetTerrain(u, PZDefOf.PZ_OpenAir);
                    }
                    map.roofGrid.SetRoof(u, null);
                }
            }
        }

        // A roofed ground rect with a slab over it on floor 2, held by a wood wall (column) at 'column'.
        private static void K4Pavilion(Map map, CellRect r, IntVec3 column)
        {
            if (column.IsValid)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, column, map);
            }
            foreach (IntVec3 g in r)
            {
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(K4Up(g, map), PZDefOf.PZ_UpperFloor);
                map.fogGrid.Unfog(K4Up(g, map));
            }
        }

        private static int K4SlabsOver(Map map, CellRect r) => r.Cells.Count(g => Slab.At(K4Up(g, map), map));

        private static int K4FloorRoofs(Map map, CellRect r) => r.Cells.Count(g => map.roofGrid.RoofAt(g) == PZDefOf.PZ_FloorRoof);

        private static void K4Park(Map map, IEnumerable<Pawn> except)
        {
            HashSet<Pawn> keep = new HashSet<Pawn>(except.Where(p => p != null));
            int i = 0;
            foreach (Pawn p in Colonists(map))
            {
                if (!keep.Contains(p))
                {
                    Draft(p, false);
                    Teleport(p, new IntVec3(110 + i++ * 2, 0, 60));
                }
            }
        }

        private static bool StepChecks4(Map map)
        {
            switch (stage)
            {
                case "k4_setup":
                {
                    AnnexMapComponent a = ProjectZ.Annex(map);
                    if (a == null || Levels.Max(map) < 2 || map.Size.x < 130 || a.groundZ < 100)
                    {
                        Result("test spot", false, $"needs 3 floors and a 130 x 100+ ground (run with -projectzfloors=3); levels {Levels.Max(map) + 1}, ground {a?.groundZ}");
                        Finish();
                        return true;
                    }
                    Info($"map {map.Size}, ground {map.Size.x} x {a.groundZ}, stride {a.stride}, floors {a.floors}, tall mountains {a.tallMountains}, rivers on tile {(map.TileInfo as RimWorld.Planet.SurfaceTile)?.Rivers?.Count ?? 0}, outdoor temp {map.mapTemperature.OutdoorTemp:0.0}, season {GenLocalDate.Season(map)}");
                    SetWork(map, false);
                    stage = "k4_s_clear";
                    return true;
                }

                // Each fixture area is cleared first (ground and every floor over it), then the game settles (a cleared
                // mountain's rock tops fall on the game's own paths) before the fixture is built and counted.
                case "k4_s_clear":
                    K4Clear(map, new CellRect(12, 12, 34, 22));
                    StartTicks(600, "k4_s_setup");
                    return true;
                case "k4_m_clear":
                    K4Clear(map, new CellRect(52, 8, 46, 28));
                    StartTicks(600, "k4_m_setup");
                    return true;
                case "k4_f_clear":
                {
                    int gz = ProjectZ.GroundZ(map);
                    K4Clear(map, new CellRect(14, gz - 12, 44, 12));
                    K4Clear(map, new CellRect(14, 0, 44, 4));
                    StartTicks(600, "k4_f_setup");
                    return true;
                }

                // ================================================================ S: walls built under a slab
                case "k4_s_setup":
                {
                    Seeded("k4_s", () =>
                    {
                        // (cleared in k4_s_clear)
                        k4S = new CellRect(20, 20, 7, 5);
                        k4Column = new IntVec3(20, 0, 22);
                        K4Pavilion(map, k4S, k4Column);
                        k4S2 = new CellRect(34, 20, 5, 5);
                        k4Col2 = k4S2.CenterCell;
                        K4Pavilion(map, k4S2, k4Col2);
                        for (int i = 0; i < 3; i++)
                        {
                            Thing w = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                            w.stackCount = 75;
                            GenSpawn.Spawn(w, new IntVec3(22 + i, 0, 16), map);
                        }
                        List<Pawn> cols = Colonists(map);
                        for (int i = 0; i < cols.Count; i++)
                        {
                            Draft(cols[i], false);
                            Teleport(cols[i], new IntVec3(18 + i * 2, 0, 15));
                        }
                    });
                    k4EastWall = new IntVec3(26, 0, 22);
                    int unsupported = k4S.Cells.Count(g => !SlabSupport.Supported(g, map));
                    Info($"S: slab {k4S} on floor 2 held by the column {k4Column}; {unsupported} cells out of support before building; second slab {k4S2} held by {k4Col2}");
                    List<string> placed = new List<string>();
                    foreach ((ThingDef def, IntVec3 at) in new[] { (ThingDefOf.Wall, k4EastWall), (ThingDefOf.Door, new IntVec3(23, 0, 20)), (DefDatabase<ThingDef>.GetNamedSilentFail("Column"), new IntVec3(24, 0, 24)) })
                    {
                        if (def == null)
                        {
                            continue;
                        }
                        ThingDef stuff = def.MadeFromStuff ? ThingDefOf.WoodLog : null;
                        AcceptanceReport r = GenConstruct.CanPlaceBlueprintAt(def, at, Rot4.North, map, stuffDef: stuff);
                        if (r.Accepted)
                        {
                            GenConstruct.PlaceBlueprintForBuild(def, at, map, Rot4.North, Faction.OfPlayer, stuff);
                        }
                        placed.Add($"{def.defName} at {at}: {(r.Accepted ? "placed" : "refused " + r.Reason)}");
                    }
                    Result("S0 blueprints of a wall, a door and a column under the slab are accepted", placed.All(s => s.Contains("placed")), string.Join("; ", placed));
                    // a building drawn taller than a storey: refused under the slab (HeightRule), for the record
                    ThingDef tall = DefDatabase<ThingDef>.AllDefs.Where(d => d.category == ThingCategory.Building && d.BuildableByPlayer && d.size == IntVec2.One && HeightRule.TooTall(d)).OrderBy(d => d.defName).FirstOrDefault();
                    if (tall != null)
                    {
                        AcceptanceReport tr = GenConstruct.CanPlaceBlueprintAt(tall, new IntVec3(22, 0, 23), Rot4.North, map, stuffDef: tall.MadeFromStuff ? GenStuff.DefaultStuffFor(tall) : null);
                        Info($"S0 a building taller than a storey ({tall.defName}) under the slab: {(tr.Accepted ? "accepted" : "refused: " + tr.Reason)}");
                    }
                    k4Collapses = PZStats.slabCollapses;
                    k4Letters = Find.LetterStack.LettersListForReading.Count;
                    k4SlabCells = K4SlabsOver(map, k4S);
                    SetWork(map, true);
                    TickUntil(20000, () => !k4S.ExpandedBy(1).Cells.Any(g => g.GetThingList(map).Any(t => t is Blueprint || t is Frame)), "k4_s_built", 250);
                    return true;
                }
                case "k4_s_built":
                {
                    SetWork(map, false);
                    List<string> built = k4S.Cells.Select(g => g.GetEdifice(map)).Where(e => e != null).Select(e => $"{e.def.defName}@{e.Position}").ToList();
                    bool done = k4EastWall.GetEdifice(map)?.def == ThingDefOf.Wall && !k4S.Cells.Any(g => g.GetThingList(map).Any(t => t is Blueprint || t is Frame));
                    Result("S1 colonists built the wall, door and column under the slab; the slab and the roof under it stay, nothing collapsed",
                        done && K4SlabsOver(map, k4S) == k4SlabCells && K4FloorRoofs(map, k4S) == k4S.Area && PZStats.slabCollapses == k4Collapses,
                        $"after {ticksTotal} ticks: built [{string.Join(", ", built)}]; slab cells {K4SlabsOver(map, k4S)}/{k4SlabCells}, floor roofs {K4FloorRoofs(map, k4S)}/{k4S.Area}, collapses {PZStats.slabCollapses - k4Collapses}, new letters [{string.Join(" | ", Find.LetterStack.LettersListForReading.Skip(k4Letters).Select(l => l.Label.ToString()))}]");
                    // placed (spawned) under the second slab: a wall and a door
                    Place(ThingDefOf.Wall, ThingDefOf.WoodLog, k4S2.CenterCell + new IntVec3(2, 0, 2), map);
                    Place(ThingDefOf.Door, ThingDefOf.WoodLog, k4S2.CenterCell + new IntVec3(-2, 0, -2), map);
                    StartTicks(250, "k4_s_placed");
                    return true;
                }
                case "k4_s_placed":
                {
                    Result("S2 a wall and a door placed (spawned) under the second slab: it stays",
                        K4SlabsOver(map, k4S2) == k4S2.Area && K4FloorRoofs(map, k4S2) == k4S2.Area && PZStats.slabCollapses == k4Collapses,
                        $"slab cells {K4SlabsOver(map, k4S2)}/{k4S2.Area}, floor roofs {K4FloorRoofs(map, k4S2)}, collapses {PZStats.slabCollapses - k4Collapses}");
                    ShotAt(map, 1, k4S.CenterCell + new IntVec3(4, 0, 0), 9f);
                    Wait(30, "k4_s_shot1");
                    return true;
                }
                case "k4_s_shot1":
                    ZCapture("k4_s_walls_built_f2");
                    ShotAt(map, 0, k4S.CenterCell + new IntVec3(4, 0, 0), 9f);
                    Wait(30, "k4_s_shot2");
                    return true;
                case "k4_s_shot2":
                {
                    ZCapture("k4_s_walls_built_f1");
                    Thing col = k4Column.GetEdifice(map);
                    Info($"S3 the first holder {col} at {k4Column} is destroyed");
                    col?.Kill();
                    StartTicks(250, "k4_s_firstgone");
                    return true;
                }
                case "k4_s_firstgone":
                {
                    Result("S3 the first holder gone: the wall built under the slab holds it (nothing collapses)",
                        K4SlabsOver(map, k4S) == k4SlabCells && K4FloorRoofs(map, k4S) == k4S.Area && PZStats.slabCollapses == k4Collapses,
                        $"slab cells {K4SlabsOver(map, k4S)}/{k4SlabCells}, floor roofs {K4FloorRoofs(map, k4S)}, collapses {PZStats.slabCollapses - k4Collapses}");
                    List<Building> holders = k4S.ExpandedBy(7).ClipInsideMap(map).Cells.Select(g => g.GetEdifice(map)).Where(e => e != null && e.def.holdsRoof && !k4S2.Contains(e.Position) && e.Position.DistanceTo(k4S.CenterCell) < 8f).Distinct().ToList();
                    Info($"S4 every holder near the slab is destroyed: {string.Join(", ", holders.Select(t => t.def.defName + "@" + t.Position))}");
                    foreach (Thing t in holders)
                    {
                        t.Kill();
                    }
                    StartTicks(250, "k4_s_allgone");
                    return true;
                }
                case "k4_s_allgone":
                {
                    Result("S4 with every holder gone the slab collapses (vanilla roof support)",
                        K4SlabsOver(map, k4S) == 0 && PZStats.slabCollapses - k4Collapses == k4SlabCells,
                        $"slab cells left {K4SlabsOver(map, k4S)}, collapses {PZStats.slabCollapses - k4Collapses} (of {k4SlabCells})");
                    stage = "k4_m_clear";
                    return true;
                }

                // ================================================================ M: the right-click menu across floors
                case "k4_m_setup":
                {
                    List<Pawn> cols = Colonists(map).Where(p => !p.WorkTagIsDisabled(WorkTags.Violent) && !p.Downed).OrderBy(p => p.thingIDNumber).ToList();
                    if (cols.Count < 2)
                    {
                        Result("M colonists", false, $"needs two colonists capable of violence, have {cols.Count}");
                        stage = "k4_f_clear";
                        return true;
                    }
                    k4Melee = cols[0];
                    k4Gun = cols[1];
                    Seeded("k4_m", () =>
                    {
                        // (cleared in k4_m_clear)
                        k4A = new CellRect(60, 20, 7, 7);
                        K4Pavilion(map, k4A, k4A.CenterCell);
                        k4B = new CellRect(80, 20, 7, 7);
                        K4Pavilion(map, k4B, k4B.CenterCell);
                        k4Stairs = Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, new IntVec3(k4B.minX + 1, 0, k4B.minZ - 2), map, Rot4.North) as Building_Stairs;
                        k4Melee.equipment.DestroyAllEquipment();
                        ThingDef knife = DefDatabase<ThingDef>.GetNamedSilentFail("MeleeWeapon_Knife") ?? DefDatabase<ThingDef>.GetNamed("MeleeWeapon_Club");
                        k4Melee.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(knife, knife.MadeFromStuff ? ThingDefOf.Steel : null));
                        LGun(k4Gun);
                    });
                    K4Park(map, new[] { k4Melee, k4Gun });
                    Info($"M: pavilion A {k4A} (no stairs), B {k4B} with stairs {k4Stairs?.Position} (exit {K4Cell(k4Stairs?.TopCell ?? IntVec3.Invalid, map)}, high {k4Stairs?.HighCell}); melee {k4Melee} ({k4Melee.equipment.Primary?.def.defName}), gun {k4Gun} ({k4Gun.equipment.Primary?.def.defName})");
                    stage = "k4_m1";
                    return true;
                }
                case "k4_m1":
                {
                    // M1: no route between the floors (pavilion A)
                    K4Enemy(map, K4Up(k4A.CenterCell + new IntVec3(1, 0, 1), map));
                    LPut(k4Melee, new IntVec3(k4A.minX - 2, 0, k4A.CenterCell.z));
                    K4Menu(map, k4Melee, k4Enemy, out FloatMenuOption melee, out FloatMenuOption fire, out string all);
                    Result("M1 melee on an enemy upstairs with no route there: no melee order (refused)", melee == null || melee.action == null,
                        $"{k4Melee} at {K4Cell(k4Melee.Position, map)} -> {k4Enemy} at {K4Cell(k4Enemy.Position, map)}, can reach {k4Melee.CanReach(k4Enemy, PathEndMode.Touch, Danger.Deadly)}; options: {all}");
                    // M4: ranged from the ground: a target at the slab edge, then one hidden on the slab over the shooter
                    List<string> rows = new List<string>();
                    bool ok = true;
                    int yes = 0, no = 0;
                    foreach ((IntVec3 shooterAt, IntVec3 targetAt) in new[]
                    {
                        (new IntVec3(k4A.maxX + 6, 0, k4A.CenterCell.z), new IntVec3(k4A.maxX, 0, k4A.CenterCell.z)),
                        (new IntVec3(k4A.minX + 1, 0, k4A.minZ + 1), new IntVec3(k4A.minX + 2, 0, k4A.minZ + 3)),
                    })
                    {
                        K4Enemy(map, K4Up(targetAt, map));
                        LPut(k4Gun, shooterAt);
                        bool can = k4Gun.equipment.PrimaryEq.PrimaryVerb.CanHitTarget(new LocalTargetInfo(k4Enemy));
                        K4Menu(map, k4Gun, k4Enemy, out FloatMenuOption _, out FloatMenuOption f, out string all2);
                        bool offered = f != null && f.action != null;
                        ok &= offered == can;
                        if (offered)
                        {
                            yes++;
                        }
                        else
                        {
                            no++;
                        }
                        rows.Add($"{K4Cell(shooterAt, map)} -> {K4Cell(k4Enemy.Position, map)}: can hit {can}, fire order {(offered ? "offered" : "refused")} ({f?.Label ?? "none"}); options: {all2}");
                    }
                    Result("M4 ranged between floors: the fire order follows the verb's can-hit (Los3D), a clear edge shot offered, a target hidden on the slab above refused", ok && yes == 1 && no == 1, string.Join(" || ", rows));
                    k4Gun.drafter.Drafted = false;
                    Teleport(k4Gun, new IntVec3(100, 0, 60));
                    // M2: a route over the stairs (pavilion B), the colonist away from the stairs
                    K4Enemy(map, K4Up(k4B.CenterCell + new IntVec3(1, 0, 2), map));
                    LPut(k4Melee, new IntVec3(k4B.maxX + 3, 0, k4B.minZ - 4));
                    K4Menu(map, k4Melee, k4Enemy, out FloatMenuOption melee2, out FloatMenuOption _, out string all3);
                    Info($"M2 melee on an enemy upstairs reachable over stairs: {k4Melee} at {K4Cell(k4Melee.Position, map)} -> {K4Cell(k4Enemy.Position, map)}, can reach {k4Melee.CanReach(k4Enemy, PathEndMode.Touch, Danger.Deadly)}; melee option {(melee2 == null ? "none" : melee2.action == null ? "refused: " + melee2.Label : "offered: " + melee2.Label)}; options: {all3}");
                    k4Hits.Clear();
                    k4WatchAttacker = k4Melee;
                    k4WatchVictim = k4Enemy;
                    if (melee2?.action != null)
                    {
                        melee2.action();
                        TickUntil(5000, () => k4Hits.Count > 0, "k4_m2_check", 30);
                    }
                    else
                    {
                        stage = "k4_m2_check";
                    }
                    return true;
                }
                case "k4_m2_check":
                {
                    Result("M2 a melee order on an enemy upstairs over stairs: the colonist walks up and every hit lands on the target's own floor from an adjacent cell",
                        k4Hits.Count > 0 && k4Hits.All(s => s.Contains("same floor, adjacent")),
                        $"hits [{string.Join("; ", k4Hits)}], colonist now {K4Cell(k4Melee.Position, map)} job {k4Melee.CurJobDef?.defName}");
                    // M3: the stair-end pair (the colonist on the stairs' high cell, the enemy on its exit): info only
                    K4Enemy(map, k4Stairs.TopCell);
                    LPut(k4Melee, k4Stairs.HighCell);
                    K4Menu(map, k4Melee, k4Enemy, out FloatMenuOption melee3, out FloatMenuOption _, out string all4);
                    Info($"M3 stair-end pair: {k4Melee} at {K4Cell(k4Melee.Position, map)} -> {k4Enemy} at {K4Cell(k4Enemy.Position, map)}, can reach {k4Melee.CanReach(k4Enemy, PathEndMode.Touch, Danger.Deadly)}, reach immediate {k4Melee.CanReachImmediate(k4Enemy, PathEndMode.Touch)}; melee option {(melee3 == null ? "none" : melee3.action == null ? "refused: " + melee3.Label : "offered: " + melee3.Label)}; options: {all4}");
                    k4Hits.Clear();
                    k4WatchAttacker = k4Melee;
                    k4WatchVictim = k4Enemy;
                    if (melee3?.action != null)
                    {
                        melee3.action();
                        TickUntil(3000, () => k4Hits.Count > 0, "k4_m3_check", 30);
                    }
                    else
                    {
                        stage = "k4_m3_check";
                    }
                    return true;
                }
                case "k4_m3_check":
                {
                    Info($"M3 stair-end pair after the order: hits [{string.Join("; ", k4Hits)}], colonist now {K4Cell(k4Melee.Position, map)}");
                    ShotAt(map, 1, k4B.CenterCell, 8f);
                    Wait(30, "k4_m_shot");
                    return true;
                }
                case "k4_m_shot":
                    ZCapture("k4_m_stairend_f2");
                    k4WatchVictim = null;
                    k4WatchAttacker = null;
                    k4Enemy?.Destroy();
                    k4Enemy = null;
                    Draft(k4Melee, false);
                    Teleport(k4Melee, new IntVec3(104, 0, 60));
                    stage = "k4_f_clear";
                    return true;

                // ================================================================ F: floods
                case "k4_f_setup":
                {
                    AnnexMapComponent a = ProjectZ.Annex(map);
                    int gz = a.groundZ;
                    k4River = new CellRect(20, gz - 5, 6, 5);
                    k4Pond = new CellRect(44, gz - 5, 6, 5);
                    k4FloorStrip = new CellRect(14, 0, 44, 3);   // floor-2 cells over the ground's south rows: right past the void
                    Seeded("k4_f", () =>
                    {
                        // (cleared in k4_f_clear)
                        TerrainDef river = DefDatabase<TerrainDef>.GetNamed("WaterMovingShallow");
                        foreach (IntVec3 c in k4River)
                        {
                            map.terrainGrid.SetTerrain(c, river);
                        }
                        foreach (IntVec3 g in k4FloorStrip)
                        {
                            IntVec3 u = K4Up(g, map);
                            if (u.GetTerrain(map) == PZDefOf.PZ_OpenAir)
                            {
                                map.terrainGrid.SetTerrain(u, PZDefOf.PZ_UpperFloor);
                            }
                        }
                    });
                    K4Park(map, Enumerable.Empty<Pawn>());
                    // no storyteller while the floods run (only terrain is watched)
                    k4Comps = Find.Storyteller.storytellerComps.ToList();
                    Find.Storyteller.storytellerComps.Clear();
                    Info($"F: river fixture {k4River}, pond fixture {k4Pond} (laid for the rain flood), slab strip on floor 2 over {k4FloorStrip}; ground river cells {ProjectZ.GroundRect(map).Cells.Count(c => c.GetTerrain(map).IsRiver)}, river cells in the ground's north row {Enumerable.Range(0, map.Size.x).Count(x => new IntVec3(x, 0, gz - 1).GetTerrain(map).IsRiver)}; outdoor temp {map.mapTemperature.OutdoorTemp:0.0}");
                    IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail("SeasonalFlooding");
                    IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.Misc, map);
                    parms.forced = true;
                    bool canFire = def != null && def.Worker.CanFireNow(parms);
                    bool fired = def != null && def.Worker.TryExecute(parms);
                    k4Flood = map.listerThings.ThingsOfDef(ThingDefOf.SeasonalFlood).FirstOrDefault();
                    K4FloodStart(map, "seasonal", $"incident can fire now {canFire}, fired {fired}");
                    if (k4Flood != null)
                    {
                        // a cleared ground cell of the fixture area (no water, nothing built), the cell over it on floor 2, a void cell
                        IntVec3 fg = new IntVec3(36, 0, a.groundZ - 10);
                        System.Reflection.MethodInfo pot = AccessTools.Method(typeof(Flood), "CanFloodPotentiallySpreadInto");
                        System.Reflection.MethodInfo can = AccessTools.Method(typeof(Flood), "CanFloodSpreadInto");
                        GsGate(map, "F Flood.CanFloodPotentiallySpreadInto", DefDatabase<TerrainDef>.GetNamed("Soil"), fg, K4Up(fg, map), new IntVec3(36, 0, a.groundZ + 1), c => (bool)pot.Invoke(k4Flood, new object[] { c }));
                        GsGate(map, "F Flood.CanFloodSpreadInto", DefDatabase<TerrainDef>.GetNamed("Soil"), fg, K4Up(fg, map), new IntVec3(36, 0, a.groundZ + 1), c => (bool)can.Invoke(k4Flood, new object[] { c }));
                    }
                    k4FloodCalls = GroundSpread.FloodCalls;
                    TickUntil(150000, () => K4FloodWatch(map), "k4_f1_check", 250);
                    return true;
                }
                case "k4_f1_check":
                {
                    K4FloodResult(map, "F1 seasonal flood");
                    ShotAt(map, 0, new IntVec3(k4River.CenterCell.x + 6, 0, k4River.maxZ - 6), 14f);
                    Wait(30, "k4_f1_shot1");
                    return true;
                }
                case "k4_f1_shot1":
                    ZCapture("k4_f1_seasonal_ground_north");
                    ShotAt(map, 1, new IntVec3(k4River.CenterCell.x + 6, 0, 6), 14f);
                    Wait(30, "k4_f1_shot2");
                    return true;
                case "k4_f1_shot2":
                {
                    ZCapture("k4_f1_seasonal_f2_south");
                    k4Flood?.Destroy();
                    int cleared = K4ClearFlood(map);
                    Info($"F1 flood removed, {cleared} flooded cells cleared");
                    // the rain flood: a pond at the north edge, torrential rain
                    TerrainDef pond = DefDatabase<TerrainDef>.GetNamed("WaterShallow");
                    foreach (IntVec3 c in k4Pond)
                    {
                        map.terrainGrid.SetTerrain(c, pond);
                    }
                    WeatherDef rain = DefDatabase<WeatherDef>.GetNamedSilentFail("TorrentialRain");
                    if (rain == null)
                    {
                        Result("F2 torrential rain", false, "no TorrentialRain weather (Odyssey off?)");
                        K4FEnd();
                        return true;
                    }
                    map.weatherManager.TransitionTo(rain);
                    Traverse.Create(map.weatherDecider).Field("curWeatherDuration").SetValue(10000000);
                    k4Flood = map.listerThings.ThingsOfDef(ThingDefOf.TorrentialRainFlood).FirstOrDefault();
                    K4FloodStart(map, "torrential rain", $"weather {map.weatherManager.curWeather?.defName}, pond fresh water body {map.waterBodyTracker?.Bodies?.Count(b => b.cells.Contains(k4Pond.CenterCell))}");
                    k4FloodCalls = GroundSpread.FloodCalls;
                    TickUntil(150000, () => K4FloodWatch(map), "k4_f2_check", 250);
                    return true;
                }
                case "k4_f2_check":
                    K4FloodResult(map, "F2 torrential rain flood");
                    ShotAt(map, 0, new IntVec3(k4Pond.CenterCell.x, 0, k4Pond.maxZ - 6), 14f);
                    Wait(30, "k4_f2_shot1");
                    return true;
                case "k4_f2_shot1":
                    ZCapture("k4_f2_rain_ground_north");
                    ShotAt(map, 1, new IntVec3(k4Pond.CenterCell.x, 0, 6), 14f);
                    Wait(30, "k4_f2_shot2");
                    return true;
                case "k4_f2_shot2":
                    ZCapture("k4_f2_rain_f2_south");
                    K4FEnd();
                    return true;
                case "k4_end":
                    Finish();
                    return true;
            }
            return false;
        }

        private static void K4FEnd()
        {
            Map map = Find.CurrentMap;
            if (k4Flood != null && !k4Flood.Destroyed)
            {
                k4Flood.Destroy();
            }
            if (k4Comps != null)
            {
                Find.Storyteller.storytellerComps.AddRange(k4Comps);
                k4Comps = null;
            }
            Wait(2, "k4_end");
        }

        // A stunned hostile pawn (no weapon, no lord) at c; the previous one is removed.
        private static void K4Enemy(Map map, IntVec3 c)
        {
            if (k4Enemy != null && !k4Enemy.Destroyed)
            {
                k4Enemy.Destroy();
            }
            k4Enemy = LeanRaider(map, c, null, lord: false);
            k4Enemy.stances.stunner.StunFor(100000, null, addBattleLog: false, showMote: false);
        }

        // The drafted right-click menu of p on t (viewing t's floor): its melee and fire options (actionable or refused).
        private static void K4Menu(Map map, Pawn p, Thing t, out FloatMenuOption melee, out FloatMenuOption fire, out string all)
        {
            FloorView.Set(Mathf.Max(0, Levels.LevelOf(t.Position, map)));
            Find.Selector.ClearSelection();
            Find.Selector.Select(p, false, false);
            List<FloatMenuOption> opts = FloatMenuMakerMap.GetOptions(new List<Pawn> { p }, t.Position.ToVector3Shifted(), out FloatMenuContext _);
            string m1 = "MeleeAttack".Translate(t.Label, t);
            string m2 = "MeleeAttackToDeath".Translate(t.Label, t);
            string f1 = "FireAt".Translate(t.Label, t);
            melee = opts.FirstOrDefault(o => o.Label.StartsWith(m1) || o.Label.StartsWith(m2));
            fire = opts.FirstOrDefault(o => o.Label.StartsWith(f1));
            all = string.Join(" | ", opts.Select(o => o.Label + (o.action == null ? " (disabled)" : "")));
        }

        // ---- floods

        private static void K4FloodStart(Map map, string name, string how)
        {
            k4FloodName = name;
            k4FirstLeak = -1;
            k4WatchTicks = 0;
            k4FloodStart = Find.TickManager.TicksGame;
            if (k4Flood == null)
            {
                Result($"{name} flood spawned", false, how + "; no flood thing on the map");
                return;
            }
            HashSet<IntVec3>[] w = Traverse.Create(k4Flood).Field("openCellWeights").GetValue<HashSet<IntVec3>[]>();
            List<IntVec3> seeds = w?.SelectMany(s => s).ToList() ?? new List<IntVec3>();
            int up = seeds.Count(c => ProjectZ.IsAnnex(c, map));
            Info($"{name} flood spawned at {K4Cell(k4Flood.Position, map)} ({how}); seeds {seeds.Count}, in the river fixture {seeds.Count(c => k4River.Contains(c))}, in the pond {seeds.Count(c => k4Pond.Contains(c))}");
            Result($"{name} flood: its seeds (GetInitialCells) are all ground cells", seeds.Count > 0 && up == 0, $"{seeds.Count} seeds, {up} above the ground: {string.Join(" ", seeds.Where(c => ProjectZ.IsAnnex(c, map)).Take(8).Select(c => K4Cell(c, map)))}");
        }

        private static int K4AnnexFlooded(Map map, out string detail)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int n = 0, voidN = 0;
            Dictionary<string, int> byTerrain = new Dictionary<string, int>();
            int maxRow = -1;
            for (int z = a.groundZ; z < map.Size.z; z++)
            {
                for (int x = 0; x < map.Size.x; x++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    if (map.terrainGrid.TempTerrainAt(c) == null)
                    {
                        continue;
                    }
                    n++;
                    maxRow = Mathf.Max(maxRow, z);
                    if (ProjectZ.IsVoid(c, map))
                    {
                        voidN++;
                    }
                    else
                    {
                        string k = map.terrainGrid.BaseTerrainAt(c)?.defName + "/" + map.terrainGrid.TopTerrainAt(c)?.defName;
                        byTerrain[k] = byTerrain.TryGetValue(k, out int v) ? v + 1 : 1;
                    }
                }
            }
            detail = $"{n} flooded cells above the ground: {voidN} void, band cells by base/top terrain [{string.Join(", ", byTerrain.Select(kv => kv.Key + " " + kv.Value))}], northmost row {maxRow} (ground ends at {a.groundZ - 1})";
            return n;
        }

        private static int K4NorthRowFlooded(Map map)
        {
            int gz = ProjectZ.GroundZ(map);
            return Enumerable.Range(0, map.Size.x).Count(x => map.terrainGrid.TempTerrainAt(new IntVec3(x, 0, gz - 1)) != null);
        }

        // Every 250 ticks: the first leak; stop when the leak has gone past the void (or after the budget).
        private static bool K4FloodWatch(Map map)
        {
            k4WatchTicks += 250;
            if (k4Flood == null || k4Flood.Destroyed)
            {
                return true;
            }
            int n = K4AnnexFlooded(map, out string _);
            if (n > 0 && k4FirstLeak < 0)
            {
                k4FirstLeak = k4WatchTicks;
            }
            AnnexMapComponent a = ProjectZ.Annex(map);
            int band1Row = a.stride;
            bool pastVoid = Enumerable.Range(0, map.Size.x).Any(x => map.terrainGrid.TempTerrainAt(new IntVec3(x, 0, band1Row + 2)) != null);
            return pastVoid || k4WatchTicks >= 60000 && K4NorthRowFlooded(map) > 0;
        }

        private static void K4FloodResult(Map map, string name)
        {
            int ground = ProjectZ.GroundRect(map).Cells.Count(c => map.terrainGrid.TempTerrainAt(c) != null);
            int north = K4NorthRowFlooded(map);
            int n = K4AnnexFlooded(map, out string detail);
            Info($"{name}: after {k4WatchTicks} ticks (flood {(k4Flood == null ? "none" : k4Flood.Destroyed ? "destroyed" : "running")}, outdoor temp {map.mapTemperature.OutdoorTemp:0.0}): {ground} ground cells flooded, {north} in the ground's north row; first water above the ground after {(k4FirstLeak < 0 ? "never" : k4FirstLeak + " ticks")}");
            Result($"{name}: the water reaches the ground's north row (the test sees the edge)", north > 0, $"{north} north-row cells flooded");
            Result($"{name}: no flood water above the ground (void rows, open air, slabs, rock tops)", n == 0, detail);
            GsRan(name, GroundSpread.FloodCalls - k4FloodCalls);
        }

        private static int K4ClearFlood(Map map)
        {
            int n = 0;
            foreach (IntVec3 c in map.AllCells)
            {
                if (map.terrainGrid.TempTerrainAt(c) != null)
                {
                    map.terrainGrid.RemoveTempTerrain(c);
                    n++;
                }
            }
            return n;
        }
    }
}
