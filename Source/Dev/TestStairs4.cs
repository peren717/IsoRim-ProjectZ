using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode stairs4 (stairs 1x4, user 2026-10-20): the ramp (3 cells) and the solid end past it, the exit right above the
    // solid end. Run with -projectzfloors=3 -projectzseed=7. Every check builds its own fixture in one cleared area
    // (router2's R2Clear) under its own random state, with real orders, jobs and ticks:
    //   G   each rotation (N, E, S, W): the footprint is low end, middle, high end and the solid end along the facing; the
    //       solid end is not walkable, the ramp is; the link's bottom is the high end and its top the exit over the solid
    //       end; the stairwell is over the ramp only (the landing over the solid end). A colonist walks up past the exit
    //       and back down (goto orders) and never stands on the solid end.
    //   P   placement: nothing else on the solid end of built stairs (a wall, a conduit) or of a stairs blueprint; stairs
    //       whose solid end would be on a wall are refused; rock in the footprint says which cells to mine ("end block").
    //   B   a colonist builds stairs from a blueprint (real construction) under a floor laid over all 4 cells above:
    //       the 3 stairwell cells are taken up (designated, worked), the exit's floor stays; steel lying on the future
    //       solid end is hauled off first.
    //   S   screenshots, IsoRim 3D: the stairs from their own floor and from the floor above, and the placement ghost.
    public static partial class TestRunner
    {
        private static readonly Rot4[] s4Rots = { Rot4.North, Rot4.East, Rot4.South, Rot4.West };
        private static int s4Idx;
        private static Building_Stairs s4Stairs;
        private static IntVec3 s4Start;
        private static bool s4OnSolid;
        private static Thing s4Steel;
        private static Thing s4Site;

        private static IntVec3 S4Center => RG(20, 16);

        private static bool StepStairs4(Map map)
        {
            Rand.PushState((int)(StableHash("stairs4_" + stage) ^ (uint)(SeedArg ?? 717)));
            try
            {
                return StepStairs4Inner(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static bool StepStairs4Inner(Map map)
        {
            switch (stage)
            {
                case "s4_setup":
                    DebugSettings.enableRandomMentalStates = false;
                    NoRoofCollapse(map);
                    Find.Storyteller.storytellerComps.Clear();
                    Find.PlaySettings.autoRebuild = false;
                    NoAmbushes(map);
                    r2Area = new CellRect(map.Center.x - 22, map.Center.z - 16, 44, 32);
                    List<Pawn> cols = Colonists(map);
                    r2P = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)) ?? cols.First();
                    r2Q = cols.FirstOrDefault(p => p != r2P);
                    foreach (Pawn p in cols)
                    {
                        p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
                    }
                    R2Park(map);
                    Info($"stairs4: area {r2Area}, walker {r2P}, levels {Levels.Min(map)}..{Levels.Max(map)}");
                    s4Idx = 0;
                    stage = "s4_g";
                    return true;
                case "s4_g":
                    S4Geometry(map);
                    return true;
                case "s4_g_up":
                    S4GeometryUp(map);
                    return true;
                case "s4_g_down":
                    S4GeometryDown(map);
                    return true;
                case "s4_p":
                    S4Placement(map);
                    return true;
                case "s4_b":
                    S4Build(map);
                    return true;
                case "s4_b_check":
                    S4BuildCheck(map);
                    return true;
                case "s4_s_below":
                    ShotAt(map, 0, S4Center, 6f);
                    Wait(40, "s4_s_below_take");
                    return true;
                case "s4_s_below_take":
                    Capture("stairs4_below");
                    ShotAt(map, 1, S4Center, 6f);
                    Wait(40, "s4_s_above_take");
                    return true;
                case "s4_s_above_take":
                    Capture("stairs4_above");
                    S4GhostView(map);
                    Wait(5, "s4_s_ghost_point");
                    return true;
                case "s4_s_ghost_point":
                    S4GhostPoint(map);
                    Wait(40, "s4_s_ghost_take");
                    return true;
                case "s4_s_ghost_take":
                    Capture(s4GhostIdx == 0 ? "stairs4_ghost" : "stairs4_ghost_xray");
                    s4GhostIdx++;
                    Wait(40, s4GhostIdx < 2 ? "s4_s_ghost2" : "s4_end");
                    return true;
                case "s4_s_ghost2":
                    S4GhostView(map);
                    Wait(5, "s4_s_ghost_point");
                    return true;
                case "s4_end":
                    fakeMouse = null;
                    Find.DesignatorManager.Deselect();
                    Find.UIRoot.screenshotMode.Active = false;
                    Finish();
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------------ G: geometry

        private static void S4Geometry(Map map)
        {
            Rot4 rot = s4Rots[s4Idx];
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            IntVec3 c = S4Center;
            AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, c, rot, map, stuffDef: ThingDefOf.WoodLog);
            s4Stairs = Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, c, map, rot) as Building_Stairs;
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            IntVec3 f = rot.FacingCell;
            IntVec3 solid = c + f * 2;
            HashSet<IntVec3> want = new HashSet<IntVec3> { c - f, c, c + f, solid };
            HashSet<IntVec3> got = new HashSet<IntVec3>(s4Stairs.OccupiedRect().Cells);
            StairGrid sg = StairGrid.For(map);
            int li = sg == null ? -1 : sg.IndexOf(s4Stairs);
            VLink l = li >= 0 ? sg.links[li] : default;
            IntVec3 exit = s4Stairs.TopCell;
            bool rampWalk = (c - f).Walkable(map) && c.Walkable(map) && (c + f).Walkable(map);
            bool wellOpen = new[] { c - f, c, c + f }.All(x => StairShape.Above(x, map).GetTerrain(map) == PZDefOf.PZ_OpenAir);
            bool landing = ProjectZ.HasFloor(exit, map);
            Result($"G {rot.ToStringHuman()}: footprint = low, middle, high end + solid end; solid end not walkable, ramp walkable; link H -> exit over the solid end; stairwell over the ramp only",
                ok.Accepted && want.SetEquals(got) && !solid.Walkable(map) && rampWalk && li >= 0 && l.bottom == c + f && l.top == exit && exit.IsValid && FloorRoof.Below(exit, map) == solid && wellOpen && landing,
                $"placement {(ok.Accepted ? "ok" : ok.Reason)}, rect {s4Stairs.OccupiedRect()}, solid {solid} walkable {solid.Walkable(map)}, ramp walkable {rampWalk}, link {li} {l.bottom}->{l.top}, exit {exit} over {FloorRoof.Below(exit, map)}, well open {wellOpen}, landing {landing}");
            s4Start = c - f * 3;
            Teleport(r2P, s4Start);
            Draft(r2P, true);
            s4OnSolid = false;
            IntVec3 past = exit + f;
            R2Floor(map, CellRect.SingleCell(Levels.Ground(past, map)), 1);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            GoTo(r2P, past);
            SeedTicks("s4_g_up_" + s4Idx);
            TickUntil(3000, () =>
            {
                s4OnSolid |= r2P.Position == solid;
                return r2P.Position == past;
            }, "s4_g_up", 1);
        }

        private static void S4GeometryUp(Map map)
        {
            tickWatch = null;
            Rot4 rot = s4Rots[s4Idx];
            IntVec3 past = s4Stairs.TopCell + rot.FacingCell;
            Result($"G {rot.ToStringHuman()}: a colonist walks up the ramp and on past the exit, never on the solid end", r2P.Position == past && !s4OnSolid, $"at {r2P.Position}, wanted {past}, on solid {s4OnSolid}");
            GoTo(r2P, s4Start);
            SeedTicks("s4_g_down_" + s4Idx);
            IntVec3 solid = StairShape.Solid(s4Stairs.Position, rot);
            TickUntil(3000, () =>
            {
                s4OnSolid |= r2P.Position == solid;
                return r2P.Position == s4Start;
            }, "s4_g_down", 1);
        }

        private static void S4GeometryDown(Map map)
        {
            tickWatch = null;
            Rot4 rot = s4Rots[s4Idx];
            Result($"G {rot.ToStringHuman()}: and back down, never on the solid end", r2P.Position == s4Start && !s4OnSolid, $"at {r2P.Position}, wanted {s4Start}, on solid {s4OnSolid}");
            s4Idx++;
            stage = s4Idx < s4Rots.Length ? "s4_g" : "s4_p";
        }

        // ------------------------------------------------------------------------------------------------ P: placement

        private static void S4Placement(Map map)
        {
            R2Clear(map);
            R2Park(map);
            IntVec3 c = S4Center;
            Rot4 rot = Rot4.North;
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, c, map, rot);
            IntVec3 solid = c + rot.FacingCell * 2;
            string solidKey = "PZ_StairsSolid".Translate();
            AcceptanceReport wall = GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, solid, Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
            AcceptanceReport conduit = GenConstruct.CanPlaceBlueprintAt(ThingDefOf.PowerConduit, solid, Rot4.North, map);
            AcceptanceReport rampConduit = GenConstruct.CanPlaceBlueprintAt(ThingDefOf.PowerConduit, c, Rot4.North, map);
            Result("P nothing else on the solid end of built stairs (a wall, a conduit: 'Part of the stairs'); the ramp's own rules unchanged",
                !wall.Accepted && !conduit.Accepted && conduit.Reason == solidKey,
                $"wall {(wall.Accepted ? "accepted" : wall.Reason)}, conduit {(conduit.Accepted ? "accepted" : conduit.Reason)}, conduit on the ramp {(rampConduit.Accepted ? "accepted" : rampConduit.Reason)}");
            // a stairs blueprint: its solid end too
            IntVec3 c2 = RG(8, 16);
            GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, c2, map, Rot4.East, Faction.OfPlayer, ThingDefOf.WoodLog);
            IntVec3 solid2 = c2 + Rot4.East.FacingCell * 2;
            AcceptanceReport bpWall = GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, solid2, Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
            AcceptanceReport bpConduit = GenConstruct.CanPlaceBlueprintAt(ThingDefOf.PowerConduit, solid2, Rot4.North, map);
            Result("P nothing else on the solid end of a stairs blueprint", !bpWall.Accepted && !bpConduit.Accepted,
                $"wall {(bpWall.Accepted ? "accepted" : bpWall.Reason)}, conduit {(bpConduit.Accepted ? "accepted" : bpConduit.Reason)}");
            // stairs whose solid end would be on a wall
            IntVec3 c3 = RG(32, 10);
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, c3 + Rot4.North.FacingCell * 2, map);
            AcceptanceReport onWall = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, c3, Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
            Result("P stairs whose solid end would be on a wall are refused", !onWall.Accepted, $"{(onWall.Accepted ? "accepted" : onWall.Reason)}");
            // rock in the footprint: which cells
            IntVec3 c4 = RG(32, 22);
            ThingDef rock = DefDatabase<ThingDef>.GetNamed("Granite");
            GenSpawn.Spawn(rock, c4 + Rot4.South.FacingCell * 2, map);
            AcceptanceReport mineS = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, c4, Rot4.South, map, stuffDef: ThingDefOf.WoodLog);
            GenSpawn.Spawn(rock, c4, map);
            AcceptanceReport mine2 = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, c4, Rot4.South, map, stuffDef: ThingDefOf.WoodLog);
            string endBlock = "PZ_StairsPart3".Translate(), middle = "PZ_StairsPart1".Translate();
            Result("P rock in the footprint (dig first, no exceptions) names the cells: the solid end ('end block'), then the middle step too",
                !mineS.Accepted && mineS.Reason == "PZ_StairsMineFirst".Translate(endBlock).ToString() && !mine2.Accepted && mine2.Reason.Contains(endBlock) && mine2.Reason.Contains(middle),
                $"solid end on rock: {mineS.Reason}; and the middle: {mine2.Reason}");
            stage = "s4_b";
        }

        // ------------------------------------------------------------------------------------------------ B: build

        private static void S4Build(Map map)
        {
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            IntVec3 c = S4Center;
            Rot4 rot = Rot4.North;
            IntVec3 f = rot.FacingCell;
            // a floor over all 4 cells above the stairs-to-be and round them, held by walls below
            R2Floor(map, new CellRect(c.x - 2, c.z - 3, 5, 7), 1);
            foreach (IntVec3 w in new[] { c + new IntVec3(-2, 0, -3), c + new IntVec3(2, 0, -3), c + new IntVec3(-2, 0, 3), c + new IntVec3(2, 0, 3) })
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, w, map);
            }
            // stairs already up to that floor (the floor's removal is worked from the stairs it is reached by: SlabPeel),
            // facing west, their exit on the patch's east edge
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, c + new IntVec3(4, 0, 0), map, Rot4.West);
            s4Steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            s4Steel.stackCount = 20;
            GenSpawn.Spawn(s4Steel, c + f * 2, map);
            Thing wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
            wood.stackCount = 75;
            GenSpawn.Spawn(wood, c + new IntVec3(4, 0, -3), map);
            map.areaManager.Home[c + new IntVec3(4, 0, -3)] = true;
            AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, c, rot, map, stuffDef: ThingDefOf.WoodLog);
            s4Site = ok.Accepted ? GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, c, map, rot, Faction.OfPlayer, ThingDefOf.WoodLog) : null;
            int marked = new[] { c - f, c, c + f, c + f * 2 }.Count(x => map.designationManager.DesignationAt(StairShape.Above(x, map), DesignationDefOf.RemoveFoundation) != null);
            bool exitMarked = map.designationManager.DesignationAt(StairShape.Above(c + f * 2, map), DesignationDefOf.RemoveFoundation) != null;
            Info($"B blueprint {(ok.Accepted ? "placed" : ok.Reason)}; removal designations over the 4 cells: {marked} (exit {exitMarked})");
            Result("B the stairs blueprint under a floor marks the 3 stairwell cells for removal, not the exit over the solid end", s4Site != null && marked == 3 && !exitMarked, $"marked {marked}, exit {exitMarked}");
            Teleport(r2P, c + new IntVec3(3, 0, -2));
            Draft(r2P, false);
            R2Work(r2P, WorkTypeDefOf.Construction, WorkTypeDefOf.Hauling);
            SeedTicks("s4_b");
            TickUntil(30000, () => map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).Any(t => t.Position == c), "s4_b_check", 60);
        }

        private static void S4BuildCheck(Map map)
        {
            tickWatch = null;
            IntVec3 c = S4Center;
            IntVec3 f = Rot4.North.FacingCell;
            Building_Stairs s = map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).OfType<Building_Stairs>().FirstOrDefault(t => t.Position == c);
            bool wellOpen = new[] { c - f, c, c + f }.All(x => StairShape.Above(x, map).GetTerrain(map) == PZDefOf.PZ_OpenAir);
            IntVec3 exit = s?.TopCell ?? IntVec3.Invalid;
            bool exitFloor = exit.IsValid && ProjectZ.HasFloor(exit, map);
            bool steelOff = s4Steel.Destroyed || s4Steel.Spawned && s4Steel.Position != c + f * 2 || !s4Steel.Spawned;
            Result("B built by a colonist: the stairwell's 3 cells taken up, the exit's floor kept, the steel hauled off the solid end, the link up",
                s != null && wellOpen && exitFloor && steelOff && StairGrid.For(map)?.IndexOf(s) >= 0,
                $"stairs {s}, well open {wellOpen}, exit {exit} floor {exitFloor}, steel {(s4Steel.Spawned ? s4Steel.Position.ToString() : s4Steel.Destroyed ? "destroyed" : "carried/unspawned")}, builder {r2P} at {r2P.Position} job {r2P.CurJobDef?.defName}");
            Draft(r2P, true);
            Teleport(r2P, c + new IntVec3(3, 0, 0));
            stage = "s4_s_below";
        }

        // The placement ghost: the stairs designator in hand, the pointer on a free cell next to the built stairs.
        private static void S4GhostView(Map map)
        {
            IntVec3 at = S4GhostCell;
            ShotAt(map, 0, at, 6f);
            Find.UIRoot.screenshotMode.Active = true;
            Designator_Build d = new Designator_Build(PZDefOf.PZ_Stairs);
            d.SetStuffDef(ThingDefOf.WoodLog);
            Find.DesignatorManager.Select(d);
        }

        // open ground with nothing upstairs (the pointer picks the ground there); the second beside the floor over the built
        // stairs (the x-ray shows it)
        private static IntVec3 S4GhostCell => s4GhostIdx == 0 ? RG(8, 8) : S4Center + new IntVec3(-5, 0, -1);
        private static int s4GhostIdx;

        // A frame later (the camera moved): the pointer on the cell.
        private static void S4GhostPoint(Map map)
        {
            IntVec3 at = S4GhostCell;
            fakeMouse = IsoRimBridge.Present ? PickScreen(new Vector3(at.x + 0.5f, 0f, at.z + 0.5f)) : (Vector2?)null;
            Info($"ghost at {at}: pointer {fakeMouse}, mouse cell {UI.MouseCell()}, placeable {GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, at, Rot4.North, map, stuffDef: ThingDefOf.WoodLog).Accepted}");
        }
    }
}
