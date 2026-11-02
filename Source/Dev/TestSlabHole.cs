using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode slabhole (removal design Q6 (b), user 2026-11-01: a slab with a slab above may be taken up; the floor above
    // keeps resting on the roof over the new hole). Run with -projectzfloors=3 -projectzseed=7. Roof collapse is ON in this
    // mode (no NoRoofCollapse). The fixture (ground coords in the cleared area): a 3-storey building, walls round x 9..17,
    // z 9..17 on the ground and on level 1 (a gap at (17, 13) on level 1), slabs on levels 1 and 2 over x 9..17, a level-1
    // ledge x 18..20 east of it, stairs ground -> level 1 at (22, 13) facing west (exit (20, 13)).
    //   H1 the tool may designate the level-1 slab at (13, 13) under the level-2 slab; a colonist takes it up (real job)
    //   H2 the hole: level 1 open, the level-2 floor stays with its floor roof under it, the roof under the hole a plain
    //      roof again; a colonist walks onto the level-2 cell right over the hole; nothing collapses in 2500 ticks; shots
    //   H3 the level-1 walls killed: the roof under level 2 is unsupported, the game's collapse takes it and the level-2
    //      slab falls (SlabCollapse)
    public static partial class TestRunner
    {
        private static IntVec3 shHole, shUp, shDown;

        private static IntVec3 ShG(int x, int z) => RG(x, z);

        private static IntVec3 ShL(int x, int z, int L, Map map) => RL(x, z, L, map);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ShPeek(bool on) => IsoRim.Api.PeekUpperFloors = on;

        private static bool StepSlabHole(Map map)
        {
            Rand.PushState((int)(StableHash("slabhole_" + stage) ^ (uint)(SeedArg ?? 717)));
            try
            {
                return StepSlabHoleInner(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static bool StepSlabHoleInner(Map map)
        {
            switch (stage)
            {
                case "hl_setup":
                {
                    DebugSettings.enableRandomMentalStates = false;
                    Find.Storyteller.storytellerComps.Clear();
                    Find.PlaySettings.autoRebuild = false;
                    NoAmbushes(map);
                    foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
                    {
                        a.Destroy();
                    }
                    if (Levels.Max(map) < 2)
                    {
                        Result("slabhole: a map with 3 levels", false, $"levels {Levels.Min(map)}..{Levels.Max(map)} (run with -projectzfloors=3)");
                        Finish();
                        return true;
                    }
                    r2Area = new CellRect(map.Center.x - 22, map.Center.z - 16, 44, 32);
                    List<Pawn> cols = Colonists(map);
                    r2P = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) ?? cols.First();
                    r2Q = cols.FirstOrDefault(p => p != r2P);
                    foreach (Pawn p in cols)
                    {
                        p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
                    }
                    R2Clear(map);
                    R2Park(map, r2P);
                    R2Feed(map);
                    CellRect ring = new CellRect(ShG(9, 9).x, ShG(9, 9).z, 9, 9);
                    foreach (IntVec3 g in ring.EdgeCells)
                    {
                        R2Wall(map, g);
                    }
                    R2Floor(map, new CellRect(ShG(9, 9).x, ShG(9, 9).z, 12, 9), 1);
                    foreach (IntVec3 g in ring.EdgeCells)
                    {
                        if (g != ShG(17, 13))
                        {
                            R2Wall(map, Levels.Cell(g, 1, map));
                        }
                    }
                    R2Floor(map, ring, 2);
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    Building_Stairs st = R2Stairs(map, ShG(22, 13), Rot4.West);
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    shHole = ShL(13, 13, 1, map);
                    shUp = ShL(13, 13, 2, map);
                    shDown = ShG(13, 13);
                    Info($"slabhole: area {r2Area}, worker {r2P}, stairs {st?.Position}, hole {shHole}, above {shUp}, below {shDown}; roofs below {map.roofGrid.RoofAt(shDown)?.defName}, at the hole {map.roofGrid.RoofAt(shHole)?.defName}");
                    Teleport(r2P, ShL(19, 13, 1, map));
                    SeedTicks("hl_settle");
                    StartTicks(5, "hl_go");
                    return true;
                }
                case "hl_go":
                {
                    Designator_RemoveFoundation tool = new Designator_RemoveFoundation();
                    AcceptanceReport ok = tool.CanDesignateCell(shHole);
                    bool blocked = map.designationManager.DesignationAt(shHole, DesignationDefOf.RemoveFoundation) == null && ok.Accepted;
                    Result("H1 the level-1 slab under a level-2 slab may be designated (no 'floor above' refusal) and nothing blocks the job",
                        ok.Accepted && Slab.Blocker(shHole, map) == null && blocked, $"designate {ok.Accepted} ({ok.Reason}), blocker '{Slab.Blocker(shHole, map)}'");
                    if (ok.Accepted)
                    {
                        tool.DesignateSingleCell(shHole);
                    }
                    Draft(r2P, false);
                    foreach (WorkTypeDef wt in DefDatabase<WorkTypeDef>.AllDefs)
                    {
                        if (!r2P.WorkTypeIsDisabled(wt))
                        {
                            r2P.workSettings.SetPriority(wt, wt == WorkTypeDefOf.Construction ? 1 : 0);
                        }
                    }
                    SeedTicks("hl_remove");
                    TickUntil(10000, () => !ProjectZ.HasFloor(shHole, map), "hl_hole", 10);
                    return true;
                }
                case "hl_hole":
                {
                    tickWatch = null;
                    Draft(r2P, true);
                    bool hole = !ProjectZ.HasFloor(shHole, map);
                    Result("H1 a colonist takes the level-1 slab up (a real job)", hole, $"floor left {!hole}, worker {r2P} at {r2P.Position} job {r2P.CurJobDef?.defName}");
                    elNote = $"level-1 floor {ProjectZ.HasFloor(shHole, map)}, level-2 floor {ProjectZ.HasFloor(shUp, map)}, roof at the hole (under level 2) {map.roofGrid.RoofAt(shHole)?.defName ?? "none"}, under the hole {map.roofGrid.RoofAt(shDown)?.defName ?? "none"}";
                    elFlag = hole && ProjectZ.HasFloor(shUp, map) && map.roofGrid.RoofAt(shHole) == PZDefOf.PZ_FloorRoof && map.roofGrid.RoofAt(shDown) != null && map.roofGrid.RoofAt(shDown) != PZDefOf.PZ_FloorRoof;
                    // a colonist on level 2 walks onto the cell over the hole
                    Teleport(r2P, ShL(11, 11, 2, map));
                    Draft(r2P, true);
                    GoTo(r2P, shUp);
                    SeedTicks("hl_walk");
                    TickUntil(2000, () => r2P.Position == shUp, "hl_walked", 1);
                    return true;
                }
                case "hl_walked":
                {
                    tickWatch = null;
                    Result("H2 the hole on level 1, the level-2 floor stays on its floor roof (the roof under the hole a plain roof again); a colonist walks onto the level-2 cell over the hole",
                        elFlag && r2P.Position == shUp && shUp.Standable(map), $"{elNote}; walker at {r2P.Position} (want {shUp}), standable {shUp.Standable(map)}");
                    etInt = map.listerThings.ThingsOfDef(ThingDefOf.Wall).Count;
                    SeedTicks("hl_stay");
                    StartTicks(2500, "hl_stayed");
                    return true;
                }
                case "hl_stayed":
                {
                    int slabs = new CellRect(ShG(9, 9).x, ShG(9, 9).z, 9, 9).Cells.Count(g => Slab.At(Levels.Cell(g, 2, map), map));
                    Result("H2 nothing collapses in 2500 ticks while held up: all 81 level-2 slab cells, the floor roof under the one over the hole, the walker still up there",
                        slabs == 81 && map.roofGrid.RoofAt(shHole) == PZDefOf.PZ_FloorRoof && r2P.Position == shUp,
                        $"level-2 slabs {slabs}, roof at the hole {map.roofGrid.RoofAt(shHole)?.defName ?? "none"}, walker at {r2P.Position}");
                    Teleport(r2P, ShG(25, 20));
                    ShotAt(map, 2, shDown, 6f);
                    Wait(5, "hl_shot2");
                    return true;
                }
                case "hl_shot2":
                    Capture("slabhole_floor3_kept");
                    ShotAt(map, 1, shDown, 6f);
                    Wait(5, "hl_shot1");
                    return true;
                case "hl_shot1":
                    Capture("slabhole_floor2_hole");
                    if (IsoRimBridge.Present && IsoRimBridge.ApiVersion >= 7)
                    {
                        ShPeek(true);
                    }
                    Wait(10, "hl_shotpeek");
                    return true;
                case "hl_shotpeek":
                    Capture("slabhole_floor2_hole_peek");
                    stage = "hl_unsup";
                    return true;
                case "hl_unsup":
                {
                    if (IsoRimBridge.Present && IsoRimBridge.ApiVersion >= 7)
                    {
                        ShPeek(false);
                    }
                    int killed = 0;
                    foreach (IntVec3 g in new CellRect(ShG(9, 9).x, ShG(9, 9).z, 9, 9).EdgeCells)
                    {
                        Building w = Levels.Cell(g, 1, map).GetEdifice(map);
                        if (w != null && w.def == ThingDefOf.Wall)
                        {
                            w.Kill();
                            killed++;
                        }
                    }
                    elNote = $"level-1 walls killed {killed}";
                    SeedTicks("hl_collapse");
                    StartTicks(300, "hl_collapsed");
                    return true;
                }
                case "hl_collapsed":
                {
                    int slabs = new CellRect(ShG(9, 9).x, ShG(9, 9).z, 9, 9).Cells.Count(g => Slab.At(Levels.Cell(g, 2, map), map));
                    Result("H3 the level-1 walls killed: the roof under level 2 is no longer held, the game's collapse takes it and the level-2 slab falls (over the hole too)",
                        slabs == 0 && !ProjectZ.HasFloor(shUp, map), $"{elNote}; level-2 slabs left {slabs}, over the hole {ProjectZ.HasFloor(shUp, map)}, roof at the hole {map.roofGrid.RoofAt(shHole)?.defName ?? "none"}");
                    ShotAt(map, 1, shDown, 7f);
                    Wait(5, "hl_shot3");
                    return true;
                }
                case "hl_shot3":
                    Capture("slabhole_collapsed");
                    Wait(5, "hl_end");
                    return true;
                case "hl_end":
                    Finish();
                    return true;
            }
            return false;
        }
    }
}
