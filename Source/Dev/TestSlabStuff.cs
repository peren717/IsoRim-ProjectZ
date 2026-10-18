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
    // Mode slabstuff (slab redesign §9): slabs of any stuff.
    //   P1 defs: one slab per Woody/Metallic/Stony stuff in one dropdown in Structure, wood first (the old PZ_UpperFloor),
    //      label, colour, icon colour, cost 4, work scaled like a wall's, beauty -1, not flammable, walk speed 100 %.
    //   P2 colonists build wood, steel, granite and marble slabs placed with the dropdown's designators through the UI
    //      pointer on floor 2 (house H, its wood stairs), stuff used from the ground stacks.
    //   P3 a carpet laid by colonists over part of the steel and granite slabs (the slab stays under it).
    //   P4 shots: the four stuffs partly carpeted, two turns, close / play / zoomed out, the ground view; the architect
    //      menu with the slab and stairs icons and the dropdown open.
    //   P5 a granite slab taken up by a colonist: no materials back (user 2026-10-18).
    //   P6 steel stairs built by colonists in the open field: their landing is a steel slab.
    //   P7 a column holding steel and granite slabs deconstructed by a colonist: the slabs collapse.
    // Mode slabold (-projectzsave=<save made before the redesign>): its PZ_UpperFloor slabs load as wood slabs.
    public static partial class TestRunner
    {
        private static IntVec3 pO, pStairs, pLanding, pSteelStairs, pColumn;
        private static CellRect pHouse;
        private static TerrainDef pCarpet;
        private static ThingDef pGranite, pMarble;
        private static readonly Dictionary<ThingDef, List<IntVec3>> pBands = new Dictionary<ThingDef, List<IntVec3>>();
        private static readonly List<IntVec3> pCarpetCells = new List<IntVec3>();
        private static readonly Dictionary<ThingDef, int> pStock = new Dictionary<ThingDef, int>();
        private static IntVec3 pRemove;
        private static int pRemoveBefore, pRemoveBeforeLow, pShot, pCollapses;
        private static float pOldYaw, pOldMin;
        private static Thing pSteelSite;
        private static readonly List<IntVec3> pPavilion = new List<IntVec3>();

        private static IntVec3 P(int dx, int dz) => new IntVec3(pO.x + dx, 0, pO.z + dz);

        private static int Stock(Map map, ThingDef def) => map.listerThings.ThingsOfDef(def).Where(t => t.Spawned).Sum(t => t.stackCount);

        private static readonly (string name, int floor, float yaw, float size)[] PShots =
        {
            ("slabs_yaw45_close", 1, 0f, 5f), ("slabs_yaw45_play", 1, 0f, 10f), ("slabs_yaw45_out", 1, 0f, 26f),
            ("slabs_yaw225_close", 1, 180f, 5f), ("slabs_yaw225_play", 1, 180f, 10f), ("slabs_yaw225_out", 1, 180f, 26f),
            ("slabs_groundview_roofs", 0, 0f, 10f),
        };

        private static bool StepSlabStuff(Map map)
        {
            switch (stage)
            {
                case "p_setup":
                    SetupSlabStuff(map);
                    return true;

                // ---- P2: slabs of four stuffs placed with the dropdown's designators through the UI on floor 2
                case "p_place":
                {
                    ShotAt(map, 1, pHouse.CenterCell, 9f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(10, "p_place2");
                    return true;
                }
                case "p_place2":
                {
                    foreach (KeyValuePair<ThingDef, List<IntVec3>> band in pBands)
                    {
                        TerrainDef slab = SlabDefs.For(band.Key);
                        Designator_Build d = BuildCopyCommandUtility.FindAllowedDesignator(slab);
                        if (d == null)
                        {
                            Result($"{slab.label}: a designator in the build menu", false, "none");
                            continue;
                        }
                        Find.DesignatorManager.Select(d);
                        // (the drag from corner to corner of the band's rectangle: the landing in it is refused as "already a floor")
                        IntVec3 a = ProjectZ.GroundCellOf(new IntVec3(band.Value.Min(c => c.x), 0, band.Value.Min(c => c.z)), map), b = ProjectZ.GroundCellOf(new IntVec3(band.Value.Max(c => c.x), 0, band.Value.Max(c => c.z)), map);
                        AcceptanceReport r = RefusalAt(d, ScreenOf(ProjectZ.GroundCellOf(band.Value.First(), map), 1), out IntVec3 at);
                        UiDesignate(ScreenOf(a, 1), ScreenOf(b, 1));
                        Find.DesignatorManager.Deselect();
                        int planned = PlannedOn(map, band.Value, slab).Count;
                        Result($"{slab.label}: the dropdown's designator places it on floor 2", r.Accepted && at == band.Value.First() && planned == band.Value.Count,
                            $"pointer {at} (want {band.Value.First()}), '{r.Reason}'; blueprints {planned} of {band.Value.Count}");
                    }
                    foreach (ThingDef s in pBands.Keys)
                    {
                        pStock[s] = Stock(map, s);
                    }
                    foreach (Pawn p in Colonists(map))
                    {
                        Draft(p, false);
                    }
                    SetWork(map, true);
                    TickUntil(90000, () => pBands.All(kv => kv.Value.All(c => c.GetTerrain(map) == SlabDefs.For(kv.Key))), "p_built", 250);
                    return true;
                }
                case "p_built":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    foreach (KeyValuePair<ThingDef, List<IntVec3>> band in pBands)
                    {
                        TerrainDef slab = SlabDefs.For(band.Key);
                        int built = band.Value.Count(c => c.GetTerrain(map) == slab);
                        int used = pStock[band.Key] - Stock(map, band.Key);
                        Result($"colonists build the {slab.label} ({slab.defName})", built == band.Value.Count && used >= SlabDefs.Cost * built,
                            $"{built} of {band.Value.Count} cells after {ticksTotal} ticks; {band.Key.label} used {used} (4 a cell = {SlabDefs.Cost * built}; botches {botches}); roof under them {band.Value.Count(c => map.roofGrid.RoofAt(FloorRoof.Below(c, map)) == PZDefOf.PZ_FloorRoof)} PZ_FloorRoof");
                    }
                    // P3: a carpet over part of the steel and granite slabs, through the UI
                    ShotAt(map, 1, pHouse.CenterCell, 9f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(10, "p_carpet");
                    return true;
                }
                case "p_carpet":
                {
                    Designator_Build d = BuildCopyCommandUtility.FindAllowedDesignator(pCarpet) ?? new Designator_Build(pCarpet);
                    Find.DesignatorManager.Select(d);
                    // (two drags, one per row: one rectangle would cover the wood rows between them)
                    foreach (IGrouping<int, IntVec3> row in pCarpetCells.GroupBy(c => c.z))
                    {
                        UiDesignate(ScreenOf(ProjectZ.GroundCellOf(row.First(), map), 1), ScreenOf(ProjectZ.GroundCellOf(row.Last(), map), 1));
                    }
                    Find.DesignatorManager.Deselect();
                    int planned = PlannedOn(map, pCarpetCells, pCarpet).Count;
                    Info($"carpet {pCarpet.defName}: {planned} of {pCarpetCells.Count} cells planned");
                    pStock[ThingDefOf.Cloth] = Stock(map, ThingDefOf.Cloth);
                    SetWork(map, true);
                    TickUntil(60000, () => pCarpetCells.All(c => c.GetTerrain(map) == pCarpet), "p_carpeted", 250);
                    return true;
                }
                case "p_carpeted":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    int laid = pCarpetCells.Count(c => c.GetTerrain(map) == pCarpet && SlabDefs.Is(map.terrainGrid.UnderTerrainAt(c)));
                    string unders = string.Join(",", pCarpetCells.Select(c => map.terrainGrid.UnderTerrainAt(c)?.defName ?? "-").Distinct());
                    Result("colonists lay a carpet on steel and granite slabs; the slab stays under it", laid == pCarpetCells.Count,
                        $"{laid} of {pCarpetCells.Count} after {ticksTotal} ticks; under: {unders}; cloth used {pStock[ThingDefOf.Cloth] - Stock(map, ThingDefOf.Cloth)}");
                    // (a cell with nothing on it: things and filth add their own beauty)
                    IntVec3 c0 = pCarpetCells.FirstOrDefault(c => c.GetThingList(map).Count == 0);
                    if (!c0.IsValid || c0 == IntVec3.Zero)
                    {
                        c0 = pCarpetCells.First();
                    }
                    Result("carpeted slab: beauty is the carpet's, a bare slab's is -1", BeautyUtility.CellBeauty(c0, map) == pCarpet.GetStatValueAbstract(StatDefOf.Beauty) && SlabDefs.For(ThingDefOf.Steel).GetStatValueAbstract(StatDefOf.Beauty) == -1f,
                        $"carpeted cell beauty {BeautyUtility.CellBeauty(c0, map)} (carpet {pCarpet.GetStatValueAbstract(StatDefOf.Beauty)}), bare steel slab {SlabDefs.For(ThingDefOf.Steel).GetStatValueAbstract(StatDefOf.Beauty)}");
                    pShot = 0;
                    Wait(2, "p_shot");
                    return true;
                }

                // ---- P4: shots
                case "p_shot":
                {
                    if (pShot >= PShots.Length)
                    {
                        IsoRimBridge.SetOption("freeYaw", pOldYaw);
                        Find.CameraDriver.config.sizeRange.min = pOldMin;
                        Wait(5, "p_arch");
                        return true;
                    }
                    if (pShot == 0)
                    {
                        pOldYaw = (float)IsoRimBridge.SetOption("freeYaw", 0f);
                        pOldMin = Find.CameraDriver.config.sizeRange.min;
                        Find.CameraDriver.config.sizeRange.min = 3f;
                        SetHour(map, 12);
                        // (the botch texts and messages of the quick-test builders would cover the slabs)
                        foreach (Thing m in map.listerThings.AllThings.Where(t => t is MoteText).ToList())
                        {
                            m.Destroy();
                        }
                        Messages.Clear();
                    }
                    var s = PShots[pShot];
                    IsoRimBridge.SetOption("freeYaw", s.yaw);
                    ShotAt(map, s.floor, pHouse.CenterCell, s.size, roofs: s.floor == 0);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(40, "p_shot_take");
                    return true;
                }
                case "p_shot_take":
                    ZCapture(PShots[pShot].name);
                    pShot++;
                    Wait(2, "p_shot");
                    return true;
                case "p_arch":
                {
                    ShotAt(map, 1, pHouse.CenterCell, 10f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    try
                    {
                        Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Architect, false);
                        MainTabWindow_Architect arch = (MainTabWindow_Architect)MainButtonDefOf.Architect.TabWindow;
                        List<ArchitectCategoryTab> tabs = Traverse.Create(arch).Field("desPanelsCached").GetValue<List<ArchitectCategoryTab>>();
                        arch.selectedDesPanel = tabs?.FirstOrDefault(t => t.def == PZDefOf.PZ_UpperFloor.designationCategory);
                    }
                    catch (Exception e)
                    {
                        Info("architect menu: " + e.Message);
                    }
                    Wait(30, "p_arch_shot");
                    return true;
                }
                case "p_arch_shot":
                    ZCapture("slabs_architect");
                    Wait(2, "p_arch_drop");
                    return true;
                case "p_arch_drop":
                {
                    Designator_Dropdown dd = PZDefOf.PZ_UpperFloor.designationCategory.AllResolvedDesignators.OfType<Designator_Dropdown>().FirstOrDefault(x => x.Elements.Any(e => e is Designator_Build b && b.PlacingDef == PZDefOf.PZ_UpperFloor));
                    try
                    {
                        fakeMouse = new Vector2(UI.screenWidth * Prefs.UIScale * 0.42f, UI.screenHeight * Prefs.UIScale * 0.55f);
                        Window w = (Window)AccessTools.Method(typeof(Designator_Dropdown), "SetupFloatMenu").Invoke(dd, new object[] { new Event() });
                        if (w is FloatMenu fm)
                        {
                            fm.vanishIfMouseDistant = false;
                        }
                        Find.WindowStack.Add(w);
                        Info($"dropdown menu: {w?.GetType().Name} with {dd.Elements.Count} entries");
                    }
                    catch (Exception e)
                    {
                        Info("dropdown menu: " + e.Message);
                    }
                    Wait(30, "p_arch_drop_shot");
                    return true;
                }
                case "p_arch_drop_shot":
                    ZCapture("slabs_architect_dropdown");
                    Wait(2, "p_arch_close");
                    return true;
                case "p_arch_close":
                    fakeMouse = null;
                    Find.WindowStack.TryRemove(typeof(FloatMenu), false);
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    Wait(5, "p_remove");
                    return true;

                // ---- P5: a granite slab taken up
                case "p_remove":
                {
                    Designator_RemoveFoundation tool = AllDesignators().OfType<Designator_RemoveFoundation>().FirstOrDefault();
                    ShotAt(map, 1, pHouse.CenterCell, 9f);
                    Find.DesignatorManager.Select(tool);
                    IntVec3 g = ProjectZ.GroundCellOf(pRemove, map);
                    AcceptanceReport r = RefusalAt(tool, ScreenOf(g, 1), out IntVec3 at);
                    UiDesignate(ScreenOf(g, 1), ScreenOf(g, 1));
                    Find.DesignatorManager.Deselect();
                    bool des = map.designationManager.DesignationAt(pRemove, DesignationDefOf.RemoveFoundation) != null;
                    Result("remove upper floor designated on a bare granite slab through the UI", r.Accepted && at == pRemove && des, $"pointer {at} (want {pRemove}), '{r.Reason}', designated {des}");
                    pRemoveBefore = CountOn(map, pGranite, 1);
                    pRemoveBeforeLow = CountOn(map, pGranite, 0);
                    SetWork(map, true);
                    TickUntil(20000, () => pRemove.GetTerrain(map) == PZDefOf.PZ_OpenAir, "p_removed", 60);
                    return true;
                }
                case "p_removed":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    int back = CountOn(map, pGranite, 1) - pRemoveBefore;
                    int down = CountOn(map, pGranite, 0) - pRemoveBeforeLow;
                    // (user 2026-10-18: taking up a slab returns no materials)
                    Result("a colonist takes the granite slab up: no granite blocks back (user 2026-10-18), the roof below an ordinary roof",
                        pRemove.GetTerrain(map) == PZDefOf.PZ_OpenAir && back == 0 && down == 0 && !OnOpenAir(map) && map.roofGrid.RoofAt(FloorRoof.Below(pRemove, map)) == RoofDefOf.RoofConstructed,
                        $"{pRemove} now {pRemove.GetTerrain(map).defName} after {ticksTotal} ticks; {pGranite.label} on floor 2 +{back}, on the ground +{down}; roof below {map.roofGrid.RoofAt(FloorRoof.Below(pRemove, map))?.defName ?? "none"}");
                    // P6: steel stairs in the open field
                    pSteelSite = GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, pSteelStairs, map, Rot4.North, Faction.OfPlayer, ThingDefOf.Steel);
                    pStock[ThingDefOf.Steel] = Stock(map, ThingDefOf.Steel);
                    SetWork(map, true);
                    TickUntil(40000, () => pSteelStairs.GetFirstThing<Building_Stairs>(map) != null, "p_stairs", 250);
                    return true;
                }
                case "p_stairs":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    Building_Stairs st = pSteelStairs.GetFirstThing<Building_Stairs>(map);
                    IntVec3 top = st?.TopCell ?? IntVec3.Invalid;
                    TerrainDef land = top.IsValid ? top.GetTerrain(map) : null;
                    Result("colonists build steel stairs: their landing is a steel slab", st != null && st.Stuff == ThingDefOf.Steel && land == SlabDefs.For(ThingDefOf.Steel),
                        $"stairs {st} ({st?.Stuff?.defName}) after {ticksTotal} ticks; landing {top} {land?.defName}; the wood stairs' landing {pLanding.GetTerrain(map).defName}");
                    Result("the wood stairs' landing is a wood slab", pLanding.GetTerrain(map) == PZDefOf.PZ_UpperFloor, pLanding.GetTerrain(map).defName);
                    // P7: the column deconstructed by a colonist
                    Thing col = pColumn.GetEdifice(map);
                    Designator_Deconstruct dd = new Designator_Deconstruct();
                    AcceptanceReport ok = col != null ? dd.CanDesignateThing(col) : "no column";
                    if (ok.Accepted)
                    {
                        dd.DesignateThing(col);
                    }
                    Info($"column {col} at {pColumn}: deconstruct designated {ok.Accepted} '{ok.Reason}'; slabs over the pavilion {string.Join(",", pPavilion.Select(c => c.GetTerrain(map).defName))}");
                    pCollapses = PZStats.slabCollapses;
                    SetWork(map, true);
                    TickUntil(20000, () => pColumn.GetEdifice(map) == null && pPavilion.All(c => c.GetTerrain(map) == PZDefOf.PZ_OpenAir), "p_collapsed", 30);
                    return true;
                }
                case "p_collapsed":
                {
                    tickWatch = null;
                    SetWork(map, false);
                    int left = pPavilion.Count(c => Slab.At(c, map));
                    Result("a colonist deconstructs the column: the steel and granite slabs on its roof collapse", pColumn.GetEdifice(map) == null && left == 0 && PZStats.slabCollapses - pCollapses == pPavilion.Count,
                        $"column gone {pColumn.GetEdifice(map) == null} after {ticksTotal} ticks; slab cells left {left}; collapses {PZStats.slabCollapses - pCollapses}; roofs below {string.Join(",", pPavilion.Select(c => map.roofGrid.RoofAt(FloorRoof.Below(c, map))?.defName ?? "none"))}");
                    WatchCheck(map, "slabstuff end");
                    Finish();
                    return true;
                }

                // ---- mode slabold: a save from before the redesign
                case "p_oldload":
                    if (!GenCommandLine.TryGetCommandLineArg("projectzsave", out saveName))
                    {
                        Result("save name", false, "pass -projectzsave=<name>");
                        Finish();
                        return true;
                    }
                    stage = "p_oldloaded";
                    pendingLoad = saveName;
                    return true;
                case "p_oldloaded":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    List<IntVec3> slabs = map.AllCells.Where(c => Slab.At(c, map)).ToList();
                    Dictionary<string, int> byDef = new Dictionary<string, int>();
                    foreach (IntVec3 c in slabs)
                    {
                        TerrainDef t = SlabDefs.Is(map.terrainGrid.TopTerrainAt(c)) ? map.terrainGrid.TopTerrainAt(c) : map.terrainGrid.UnderTerrainAt(c);
                        byDef[t.defName] = byDef.TryGetValue(t.defName, out int n) ? n + 1 : 1;
                    }
                    TerrainDef w = PZDefOf.PZ_UpperFloor;
                    bool wood = SlabDefs.StuffOf(w) == ThingDefOf.WoodLog && w.CostList.Count == 1 && w.CostList[0].thingDef == ThingDefOf.WoodLog && w.DrawColor == ThingDefOf.WoodLog.stuffProps.color;
                    int roofed = slabs.Count(c => map.roofGrid.RoofAt(FloorRoof.Below(c, map)) == PZDefOf.PZ_FloorRoof);
                    Result($"old save {saveName}: its slabs load as wood slabs", slabs.Count > 0 && byDef.Count == 1 && byDef.ContainsKey(w.defName) && wood && errors == 0,
                        $"{slabs.Count} slab cells: {string.Join(", ", byDef.Select(kv => kv.Key + " x" + kv.Value))}; {w.defName} = '{w.label}', cost {w.CostList[0].thingDef.defName} x{w.CostList[0].count}, colour {w.DrawColor}; roofs under them {roofed} PZ_FloorRoof; errors {errors}");
                    IntVec3 at = slabs.Count > 0 ? ProjectZ.GroundCellOf(slabs[slabs.Count / 2], map) : map.Center;
                    ShotAt(map, 1, at, 9f);
                    Wait(40, "p_oldshot");
                    return true;
                }
                case "p_oldshot":
                    ZCapture("oldsave_f2");
                    Wait(2, "p_oldtick");
                    return true;
                case "p_oldtick":
                    StartTicks(2500, "p_oldend");
                    return true;
                case "p_oldend":
                    Result($"old save {saveName}: runs 2500 ticks without errors", errors == 0, $"errors {errors}, warnings {warnings}");
                    Finish();
                    return true;
            }
            return false;
        }

        // P1: the defs, read as the game made them.
        private static void CheckSlabDefs(Map map)
        {
            List<TerrainDef> all = SlabDefs.All.ToList();
            List<ThingDef> stuffs = DefDatabase<ThingDef>.AllDefs.Where(d => d.stuffProps?.categories != null && d.stuffProps.categories.Any(c => c.defName == "Woody" || c.defName == "Metallic" || c.defName == "Stony")).ToList();
            bool perStuff = stuffs.All(s => SlabDefs.For(s) != null && SlabDefs.StuffOf(SlabDefs.For(s)) == s) && all.Count == stuffs.Count;
            Result("one slab def per Woody, Metallic and Stony stuff, the wood one is PZ_UpperFloor", perStuff && SlabDefs.For(ThingDefOf.WoodLog) == PZDefOf.PZ_UpperFloor,
                $"{all.Count} slab defs for {stuffs.Count} stuffs: {string.Join(", ", all.Select(t => t.defName))}");
            List<string> bad = new List<string>();
            foreach (TerrainDef t in all)
            {
                ThingDef s = SlabDefs.StuffOf(t);
                float work = t.GetStatValueAbstract(StatDefOf.WorkToBuild);
                float want = Mathf.Round(SlabDefs.BaseWork * s.stuffProps.statFactors.GetStatFactorFromList(StatDefOf.WorkToBuild) + s.stuffProps.statOffsets.GetStatOffsetFromList(StatDefOf.WorkToBuild));
                bool ok = t.CostList.Count == 1 && t.CostList[0].thingDef == s && t.CostList[0].count == 4 && t.DrawColor == s.stuffProps.color && t.uiIconColor == s.stuffProps.color
                    && Mathf.Abs(work - Mathf.Max(1f, want)) < 0.5f && t.GetStatValueAbstract(StatDefOf.Beauty) == -1f && t.GetStatValueAbstract(StatDefOf.Flammability) == 0f
                    && t.pathCost == 0 && t.designatorDropdown == PZDefOf.PZ_UpperFloor.designatorDropdown && t.uiIcon != null && (t.uiIcon.name ?? "").Contains("UpperFloor_MenuIcon") && !t.cropIcon
                    && t.texturePath == "ProjectZ/Terrain/SlabLattice" && t.blueprintDef != null && t.frameDef != null;
                if (!ok)
                {
                    bad.Add(t.defName);
                }
            }
            Info("slab defs: " + string.Join("; ", all.Select(t => $"{t.defName} '{t.label}' {t.CostList[0].thingDef.defName}x{t.CostList[0].count} work {t.GetStatValueAbstract(StatDefOf.WorkToBuild)} colour {t.DrawColor} effect {t.constructEffect?.defName}")));
            Result("each slab: 4 of its stuff, its colour and icon colour, work scaled like a wall's, beauty -1, never burns, walk speed 100 %, the lattice texture and icon",
                bad.Count == 0, bad.Count == 0 ? $"{all.Count} checked; wood {PZDefOf.PZ_UpperFloor.GetStatValueAbstract(StatDefOf.WorkToBuild)}, steel {SlabDefs.For(ThingDefOf.Steel).GetStatValueAbstract(StatDefOf.WorkToBuild)}, granite {SlabDefs.For(pGranite).GetStatValueAbstract(StatDefOf.WorkToBuild)} work; walk speed {(PZDefOf.PZ_UpperFloor.pathCost == 0 ? "100%" : "slower")}" : "wrong: " + string.Join(", ", bad));
            Designator_Dropdown dd = PZDefOf.PZ_UpperFloor.designationCategory.AllResolvedDesignators.OfType<Designator_Dropdown>().FirstOrDefault(x => x.Elements.Any(e => e is Designator_Build b && b.PlacingDef == PZDefOf.PZ_UpperFloor));
            List<BuildableDef> inDrop = dd?.Elements.OfType<Designator_Build>().Select(b => b.PlacingDef).ToList() ?? new List<BuildableDef>();
            Result("one dropdown in Structure holds every slab, wood first", dd != null && inDrop.Count == all.Count && all.All(t => inDrop.Contains(t)) && inDrop.First() == PZDefOf.PZ_UpperFloor,
                $"dropdown '{dd?.LabelCap}' with {inDrop.Count}: {string.Join(", ", inDrop.Select(b => b.label))}");
            Result("stairs icon: the menu icon, tinted by stuff", PZDefOf.PZ_Stairs.uiIcon != null && (PZDefOf.PZ_Stairs.uiIcon.name ?? "").Contains("Stairs_MenuIcon"), $"{PZDefOf.PZ_Stairs.uiIcon?.name}");
            Info($"language {LanguageDatabase.activeLanguage?.folderName}: {string.Join(", ", new[] { PZDefOf.PZ_UpperFloor, SlabDefs.For(ThingDefOf.Steel), SlabDefs.For(pGranite) }.Select(t => t.label))}; dropdown group '{PZDefOf.PZ_UpperFloor.designatorDropdown.label}'");
        }

        private static void SetupSlabStuff(Map map)
        {
            pGranite = DefDatabase<ThingDef>.GetNamed("BlocksGranite");
            pMarble = DefDatabase<ThingDef>.GetNamed("BlocksMarble");
            List<Pawn> cols = Colonists(map);
            IntVec3 near = cols.Count > 0 ? new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z)) : map.Center;
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 79f, true))
            {
                CellRect r = new CellRect(c.x - 17, c.z - 6, 36, 13);
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
            pO = new IntVec3(found.minX, 0, found.minZ);
            // House H (12 x 9): wood walls, a door, a roof; wood stairs inside up to its floor 2 (landing laid with them).
            pHouse = new CellRect(pO.x, pO.z + 2, 12, 9);
            IntVec3 door = new IntVec3(pHouse.minX + 6, 0, pHouse.minZ);
            foreach (IntVec3 c in pHouse.EdgeCells)
            {
                Place(c == door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, c, map);
            }
            foreach (IntVec3 c in pHouse)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            int x0 = pHouse.minX + 1, x9 = pHouse.maxX - 1, z0 = pHouse.minZ + 1;
            pStairs = new IntVec3(x0, 0, z0 + 1);
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, pStairs, map, Rot4.North);
            pLanding = StairShape.Exit(pStairs, Rot4.North, map);
            // Floor 2 bands (rows, inside the walls): marble z0, granite z0+1..2, wood z0+3..4 (the landing's rows),
            // steel z0+5..6. A carpet over the east half of granite row z0+2 and steel row z0+5.
            List<IntVec3> Band(int za, int zb, int xa) => new CellRect(xa, za, x9 - xa + 1, zb - za + 1).Cells.Select(g => UpS(g, map)).Where(c => c != pLanding).OrderBy(c => c.z).ThenBy(c => c.x).ToList();
            pBands.Clear();
            pBands[ThingDefOf.WoodLog] = Band(z0 + 3, z0 + 4, x0);
            pBands[ThingDefOf.Steel] = Band(z0 + 5, z0 + 6, x0);
            pBands[pGranite] = Band(z0 + 1, z0 + 2, x0 + 1);
            pBands[pMarble] = Band(z0, z0, x0 + 1);
            pCarpetCells.Clear();
            pCarpetCells.AddRange(new CellRect(x0 + 5, z0 + 2, x9 - x0 - 4, 4).Cells.Where(g => g.z == z0 + 2 || g.z == z0 + 5).Select(g => UpS(g, map)));
            pCarpetCells.Sort((a, b) => a.z != b.z ? a.z.CompareTo(b.z) : a.x.CompareTo(b.x));
            // the granite slab taken up later: the bare granite cell farthest from the landing
            pRemove = UpS(new IntVec3(x9, 0, z0 + 1), map);
            pCarpet = DefDatabase<TerrainDef>.AllDefs.Where(t => t.layerable && t.costList != null && t.costList.Any(c => c.thingDef == ThingDefOf.Cloth) && t.constructionSkillPrerequisite == 0 && t.defName.StartsWith("Carpet") && !t.defName.Contains("_")).OrderBy(t => t.defName).FirstOrDefault();
            // Ground stacks: the stuff for the slabs, the carpet and the steel stairs.
            int sx = 14;
            foreach (KeyValuePair<ThingDef, int> kv in new[] { new KeyValuePair<ThingDef, int>(ThingDefOf.WoodLog, 150), new KeyValuePair<ThingDef, int>(ThingDefOf.Steel, 225), new KeyValuePair<ThingDef, int>(pGranite, 150), new KeyValuePair<ThingDef, int>(pMarble, 75), new KeyValuePair<ThingDef, int>(ThingDefOf.Cloth, 150) })
            {
                int left = kv.Value;
                while (left > 0)
                {
                    Thing t = ThingMaker.MakeThing(kv.Key);
                    t.stackCount = Mathf.Min(left, kv.Key.stackLimit);
                    left -= t.stackCount;
                    GenSpawn.Spawn(t, P(sx, 0), map);
                    sx++;
                }
            }
            // Open-field steel stairs site (placed later) and the pavilion: a steel wall column holding a 3 x 2 roof north
            // of it, with steel and granite slabs on it; nothing else holds roofs within 7 cells.
            pSteelStairs = P(20, 7);
            pColumn = P(31, 4);
            pPavilion.Clear();
            foreach (IntVec3 g in new CellRect(pColumn.x - 1, pColumn.z + 1, 3, 2))
            {
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
            }
            Place(ThingDefOf.Wall, ThingDefOf.Steel, pColumn, map);
            int i = 0;
            foreach (IntVec3 g in new CellRect(pColumn.x - 1, pColumn.z + 1, 3, 2))
            {
                IntVec3 u = UpS(g, map);
                map.terrainGrid.SetTerrain(u, i++ % 2 == 0 ? SlabDefs.For(ThingDefOf.Steel) : SlabDefs.For(pGranite));
                pPavilion.Add(u);
            }
            foreach (Pawn p in cols)
            {
                Teleport(p, P(15 + cols.IndexOf(p), 1));
                Draft(p, false);
            }
            SetWork(map, false);
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            Info($"slabstuff: house {pHouse}, stairs {pStairs} landing {pLanding} ({pLanding.GetTerrain(map).defName}); bands {string.Join("; ", pBands.Select(kv => kv.Key.defName + " " + kv.Value.Count + " cells " + kv.Value.First() + ".." + kv.Value.Last()))}; carpet {pCarpet?.defName} {pCarpetCells.Count} cells; remove {pRemove}; steel stairs at {pSteelStairs}; column {pColumn}, pavilion {string.Join(",", pPavilion)}");
            CheckSlabDefs(map);
            AnnexWatch.Reset();
            Wait(30, "p_place");
        }
    }
}
