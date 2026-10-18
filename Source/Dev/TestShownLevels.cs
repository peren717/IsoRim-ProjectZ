using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode shownlv (run with -projectzfloors=13): the floor HUD and the floor keys offer only the levels in use plus one
    // (ShownLevels, user decision 2026-10-17). Every check compares the cache with a brute-force answer (ShownExpected:
    // every cell's terrain and every spawned thing) and with the HUD's own bars.
    //   L1 a fresh 13-floor world, only the ground: the HUD shows the ground and level 1; the floor keys (key events
    //      through FloorKeys_Patch) go up to level 1 only and back.
    //   L2 a house (placed) with a stairs blueprint and a level-1 slab blueprint over it (the designators): the blueprint
    //      on open air already brings level 2; colonists build the stairs and the slab (real ticks): level 2 stays,
    //      held by the floor now; the keys reach level 2 and stop there.
    //   L3 one slab cell laid on level 2 (TerrainChanged's add path): level 3 shows at once; the keys go up to it and
    //      the view is left on level 3 (the top, nothing built there).
    //   L4 a slab blueprint designated on level 3 and cancelled with the game's cancel designator; then the level-2 cell
    //      taken up (Slab.Remove, TerrainChanged's removal path): the viewed level 3 stays shown; one key down and
    //      it slides out (a mid-slide shot, then settled). Shots of the HUD in each state.
    public static partial class TestRunner
    {
        private static IntVec3 lvO, lvStairs, lvUp2;
        private static CellRect lvHouse;
        private static readonly List<IntVec3> lvSlab = new List<IntVec3>();

        private static IntVec3 LV(int dx, int dz) => new IntVec3(lvO.x + dx, 0, lvO.z + dz);

        // The shown range from scratch: every level with a floor cell or a pawn, item, blueprint, frame or fire, the
        // ground always; one more each way, clamped; the viewed level in.
        private static void ShownExpected(Map map, out int lo, out int hi)
        {
            int usedLo = 0, usedHi = 0;
            foreach (int L in Levels.All(map))
            {
                if (L == 0)
                {
                    continue;
                }
                for (int z = Levels.RowMin(map, L); z <= Levels.RowMax(map, L); z++)
                {
                    bool any = false;
                    for (int x = 0; x < map.Size.x && !any; x++)
                    {
                        any = ProjectZ.HasFloor(new IntVec3(x, 0, z), map);
                    }
                    if (any)
                    {
                        usedLo = Mathf.Min(usedLo, L);
                        usedHi = Mathf.Max(usedHi, L);
                        break;
                    }
                }
            }
            foreach (Thing t in map.listerThings.AllThings)
            {
                if (!t.Spawned || !(t is Pawn || t is Blueprint || t is Frame || t.def.category == ThingCategory.Item || t.def.EverHaulable || t is Fire))
                {
                    continue;
                }
                int L = Levels.LevelOf(t.Position, map);
                if (L != Levels.None)
                {
                    usedLo = Mathf.Min(usedLo, L);
                    usedHi = Mathf.Max(usedHi, L);
                }
            }
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                int L = Levels.LevelOf(p.Position, map);
                if (L != Levels.None)
                {
                    usedLo = Mathf.Min(usedLo, L);
                    usedHi = Mathf.Max(usedHi, L);
                }
            }
            lo = Mathf.Min(Mathf.Max(Levels.Min(map), usedLo - 1), FloorView.Current);
            hi = Mathf.Max(Mathf.Min(Levels.Max(map), usedHi + 1), FloorView.Current);
        }

        private static string LvSlotsWant(int lo, int hi) => string.Join(",", Enumerable.Range(lo, hi - lo + 1));

        // The cache, the brute-force answer and the HUD's bars all agree on lo..hi.
        private static void LvCheck(Map map, string name, int lo, int hi)
        {
            ShownLevels.ForceRefresh(map);
            ShownLevels.Range(map, out int clo, out int chi);
            ShownExpected(map, out int elo, out int ehi);
            string slots = FloorHud.Slots();
            Rect r = FloorHud.LastRect;
            int n = hi - lo + 1;
            bool ok = clo == lo && chi == hi && elo == lo && ehi == hi && slots == LvSlotsWant(lo, hi) && Mathf.Abs(r.height - FloorHud.StackHeight(n)) < 0.01f;
            Result($"{name}: levels {lo}..{hi} shown", ok,
                $"cache {clo}..{chi}, brute force {elo}..{ehi}, HUD bars [{slots}], HUD {R(r)} (want height {FloorHud.StackHeight(n)}); {ShownLevels.Describe(map)}");
        }

        // One floor key press (a key event through the floor keys' own handler, inside OnGUI).
        private static void LvKey(bool up)
        {
            KeyBindingDef k = up ? PZKeyDefOf.PZ_FloorUp : PZKeyDefOf.PZ_FloorDown;
            Event saved = Event.current;
            Event.current = new Event { type = EventType.KeyDown, keyCode = k.MainKey };
            try
            {
                FloorKeys_Patch.Postfix();
            }
            finally
            {
                Event.current = saved;
            }
        }

        // Presses the keys in `ups` (true = up) and checks the levels viewed after each.
        private static void LvKeys(string name, bool[] ups, int[] want)
        {
            guiAction = () =>
            {
                List<int> got = new List<int>();
                foreach (bool u in ups)
                {
                    LvKey(u);
                    got.Add(FloorView.Current);
                }
                Result(name, got.SequenceEqual(want), $"keys {string.Join(" ", ups.Select(u => u ? "up" : "down"))}: viewed {string.Join(",", got)} (want {string.Join(",", want)})");
            };
        }

        private static void LvShot(Map map, string name)
        {
            ShotAt(map, FloorView.Current, lvHouse.Area > 0 ? lvHouse.CenterCell : map.Center, 14f);
            Capture(name);
        }

        private static void LvKeepColonistsFed(Map map)
        {
            foreach (Pawn p in Colonists(map))
            {
                if (p.needs?.food != null) p.needs.food.CurLevel = p.needs.food.MaxLevel;
                if (p.needs?.rest != null) p.needs.rest.CurLevel = 1f;
                if (p.needs?.joy != null) p.needs.joy.CurLevel = 1f;
            }
        }

        // Waits in real time (the HUD refreshes every ShownLevels.RefreshTime and slides for 150 ms, frames vary).
        private static float lvUntil;
        private static string lvNext;

        private static void LvWait(float seconds, string next)
        {
            lvUntil = Time.realtimeSinceStartup + seconds;
            lvNext = next;
            stage = "lv_wait";
        }

        private static bool StepShownLv(Map map)
        {
            switch (stage)
            {
                case "lv_wait":
                    if (Time.realtimeSinceStartup >= lvUntil)
                    {
                        stage = lvNext;
                    }
                    return true;
                case "lv_setup":
                    if (Levels.Count(map) < 6)
                    {
                        Result("shownlv needs a world with many floors (-projectzfloors=13)", false, $"levels {Levels.Count(map)}");
                        Finish();
                        return true;
                    }
                    DebugSettings.enableRandomMentalStates = false;
                    Find.Storyteller.storytellerComps.Clear();   // no raids while the colonists build (test only)
                    Find.PlaySettings.autoRebuild = false;
                    FloorView.Set(0);
                    lvHouse = CellRect.Empty;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    ShotAt(map, 0, map.Center, 14f);
                    LvWait(0.6f, "lv_ground");
                    return true;

                // ---- L1: only the ground
                case "lv_ground":
                    Info($"world: {Levels.Count(map)} levels ({Levels.Min(map)}..{Levels.Max(map)}), map {map.Size}");
                    LvCheck(map, "L1 a fresh world, only the ground", 0, 1);
                    Capture("lv_1_ground");
                    Wait(3, "lv_ground_keys");
                    return true;
                case "lv_ground_keys":
                    LvKeys("L1 floor keys: up, up, up, down, down = levels 1, 1, 1, 0, 0", new[] { true, true, true, false, false }, new[] { 1, 1, 1, 0, 0 });
                    LvWait(0.6f, "lv_house");
                    return true;

                // ---- L2: colonists build a level-1 slab
                case "lv_house":
                    LvHouse(map);
                    return true;
                case "lv_planned":
                    LvCheck(map, "L2 a slab blueprint on open air over level 1 brings level 2", 0, 2);
                    Capture("lv_2_planned");
                    SetWork(map, true);
                    foreach (Pawn p in Colonists(map))
                    {
                        Draft(p, false);
                        if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                        {
                            p.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2);
                        }
                    }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(40000, () =>
                    {
                        LvKeepColonistsFed(map);
                        return lvSlab.All(c => c.GetTerrain(map) != PZDefOf.PZ_OpenAir && !c.GetThingList(map).Any(t => t is Blueprint || t is Frame)) && map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).Count > 0;
                    }, "lv_built", 120);
                    return true;
                case "lv_built":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    int built = lvSlab.Count(c => c.GetTerrain(map) != PZDefOf.PZ_OpenAir);
                    bool stairs = map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).Count > 0;
                    Result("L2 colonists built the stairs and the level-1 slab", built == lvSlab.Count && stairs, $"{built}/{lvSlab.Count} slab cells, stairs {stairs}, after {ticksTotal} ticks");
                    // everyone back on the ground: level 1 is held by its floor alone
                    foreach (Pawn p in Colonists(map))
                    {
                        Teleport(p, LV(-3 - Colonists(map).IndexOf(p), -3));
                        Draft(p, true);
                    }
                    LvWait(0.6f, "lv_built_check");
                    return true;
                }
                case "lv_built_check":
                {
                    int blueprints = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count + map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count;
                    LvCheck(map, $"L2 the built level-1 floor keeps level 2 ({blueprints} blueprints/frames left on the map)", 0, 2);
                    Capture("lv_3_built");
                    Wait(3, "lv_built_keys");
                    return true;
                }
                case "lv_built_keys":
                    LvKeys("L2 floor keys: up x3, down x3 = levels 1, 2, 2, 1, 0, 0", new[] { true, true, true, false, false, false }, new[] { 1, 2, 2, 1, 0, 0 });
                    LvWait(0.6f, "lv_up2");
                    return true;

                // ---- L3: a floor on level 2
                case "lv_up2":
                    lvUp2 = Levels.Cell(LV(3, 3), 2, map);
                    map.terrainGrid.SetTerrain(lvUp2, PZDefOf.PZ_UpperFloor);
                    LvWait(0.6f, "lv_up2_check");
                    return true;
                case "lv_up2_check":
                    LvCheck(map, $"L3 a floor cell on level 2 ({lvUp2}) brings level 3", 0, 3);
                    LvKeys("L3 floor keys: up x4 = levels 1, 2, 3, 3", new[] { true, true, true, true }, new[] { 1, 2, 3, 3 });
                    LvWait(0.6f, "lv_top");
                    return true;
                case "lv_top":
                    LvCheck(map, "L3 viewing level 3 (nothing there)", 0, 3);
                    LvShot(map, "lv_4_view3");
                    LvWait(0.6f, "lv_cancel");
                    return true;

                // ---- L4: the viewed level's floors go
                case "lv_cancel":
                {
                    IntVec3 c3 = Levels.Cell(LV(3, 3), 3, map);
                    Thing bp = GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_UpperFloor, c3, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
                    ShownLevels.ForceRefresh(map);
                    ShownLevels.Range(map, out int lo, out int hi);
                    Result("L4 a slab blueprint on level 3 brings level 4", bp != null && hi == 4, $"blueprint {bp} at {c3}; {ShownLevels.Describe(map)}");
                    Designator_Cancel d = new Designator_Cancel();
                    AcceptanceReport r = d.CanDesignateCell(c3);
                    if (r.Accepted)
                    {
                        d.DesignateSingleCell(c3);
                    }
                    Result("L4 the cancel designator removes it", r.Accepted && !c3.GetThingList(map).Any(t => t is Blueprint), r.Accepted ? "cancelled" : r.Reason);
                    LvWait(0.6f, "lv_cancel_check");
                    return true;
                }
                case "lv_cancel_check":
                    LvCheck(map, "L4 after the cancel: back to 0..3 (level 3 viewed)", 0, 3);
                    Slab.Remove(lvUp2, map, false);
                    LvWait(0.6f, "lv_removed");
                    return true;
                case "lv_removed":
                    Result("L4 the level-2 floor cell is gone", lvUp2.GetTerrain(map) == PZDefOf.PZ_OpenAir, lvUp2.GetTerrain(map)?.defName);
                    LvCheck(map, "L4 level 3 still viewed: it stays shown though nothing is above level 1", 0, 3);
                    LvShot(map, "lv_5_kept");
                    Wait(5, "lv_down");
                    return true;
                case "lv_down":
                    LvKeys("L4 one key down = level 2", new[] { false }, new[] { 2 });
                    Wait(2, "lv_slide");
                    return true;
                case "lv_slide":
                    Info("mid-slide bars: " + FloorHud.Slots() + ", HUD " + R(FloorHud.LastRect));
                    Capture("lv_6_slide");
                    LvWait(0.6f, "lv_after");
                    return true;
                case "lv_after":
                    LvCheck(map, "L4 viewing level 2: level 3 slid out", 0, 2);
                    LvShot(map, "lv_7_after");
                    Wait(5, "lv_end");
                    return true;
                case "lv_end":
                    FloorView.Set(0);
                    Finish();
                    return true;
            }
            return false;
        }

        // House (placed): 7 x 6 wood walls, a south door, a roof. Designated: stairs inside (north), a level-1 slab over
        // the inside except the stairwell. Wood and skilled builders next to it.
        private static void LvHouse(Map map)
        {
            List<Pawn> cols = Colonists(map);
            IntVec3 near = cols.Count > 0 ? new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z)) : map.Center;
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 60f, true))
            {
                CellRect m = new CellRect(c.x - 12, c.z - 10, 26, 22);
                if (!m.InBounds(map) || m.minX < 12 || m.minZ < 12 || m.maxX >= map.Size.x - 12 || m.maxZ >= ProjectZ.GroundZ(map) - 12)
                {
                    continue;
                }
                if (m.Cells.All(x => !x.GetTerrain(map).IsWater && !map.roofGrid.Roofed(x) && !x.Fogged(map) && x.GetTerrain(map).passability == Traversability.Standable && x.GetThingList(map).All(t => t is Pawn || t.def.destroyable)))
                {
                    found = m;
                    break;
                }
            }
            if (found.Area == 0)
            {
                Result("test spot", false, "no open ground near the colonists");
                Finish();
                return;
            }
            foreach (IntVec3 c in found)
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (!(t is Pawn) && t.def.destroyable)
                    {
                        t.Destroy();
                    }
                }
            }
            lvO = new IntVec3(found.minX + 9, 0, found.minZ + 8);
            lvHouse = new CellRect(lvO.x, lvO.z, 7, 6);
            IntVec3 door = LV(3, 0);
            foreach (IntVec3 c in lvHouse.EdgeCells)
            {
                Place(c == door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            foreach (IntVec3 c in lvHouse)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                map.areaManager.Home[c] = true;
            }
            for (int i = 0; i < 6; i++)
            {
                Thing w = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                w.stackCount = 75;
                GenSpawn.Spawn(w, LV(-2 + i, -3), map);
            }
            foreach (Pawn p in cols)
            {
                SkillRecord sk = p.skills?.GetSkill(SkillDefOf.Construction);
                if (sk != null)
                {
                    sk.Level = 20;
                }
                Teleport(p, LV(-3 + cols.IndexOf(p), -5));
                Draft(p, false);
            }
            SetWork(map, false);
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            // (the house alone: still only the ground and level 1)
            ShownLevels.ForceRefresh(map);
            ShownLevels.Range(map, out int lo0, out int hi0);
            Result("L2 the house (ground only) changes nothing", lo0 == 0 && hi0 == 1, ShownLevels.Describe(map));
            // stairs (north inside, going up north) and the slab over the rest of the inside, through the designators
            lvStairs = LV(2, 2);
            Designator_Build ds = new Designator_Build(PZDefOf.PZ_Stairs);
            ds.SetStuffDef(ThingDefOf.WoodLog);
            Find.DesignatorManager.Select(ds);
            SetRot(ds, Rot4.North);
            AcceptanceReport rs = ds.CanDesignateCell(lvStairs);
            if (rs.Accepted)
            {
                ds.DesignateSingleCell(lvStairs);
            }
            Find.DesignatorManager.Deselect();
            Result("L2 stairs designated", rs.Accepted, rs.Accepted ? lvStairs.ToString() : rs.Reason);
            HashSet<IntVec3> well = new HashSet<IntVec3>(StairShape.Footprint(lvStairs, Rot4.North).Cells);
            IntVec3 exit = Levels.Ground(StairShape.Exit(lvStairs, Rot4.North, map), map);
            lvSlab.Clear();
            Designator_Build d = new Designator_Build(PZDefOf.PZ_UpperFloor);
            List<IntVec3> want = lvHouse.ContractedBy(1).Cells.Where(g => !well.Contains(g) && g != exit).Select(g => Levels.Cell(g, 1, map)).ToList();
            for (int pass = 0; pass < 6; pass++)
            {
                foreach (IntVec3 c in want)
                {
                    if (!PlaceWorker_Stairs.FloorPlanned(c, map) && d.CanDesignateCell(c).Accepted)
                    {
                        d.DesignateSingleCell(c);
                    }
                }
            }
            lvSlab.AddRange(want.Where(c => PlaceWorker_Stairs.FloorPlanned(c, map)));
            Result("L2 level-1 slab designated over the house", lvSlab.Count > 0, $"{lvSlab.Count}/{want.Count} cells planned; refused: {string.Join(", ", want.Where(c => !lvSlab.Contains(c)).Take(4).Select(c => c + " '" + d.CanDesignateCell(c).Reason + "'"))}");
            LvWait(0.6f, "lv_planned");
        }
    }
}
