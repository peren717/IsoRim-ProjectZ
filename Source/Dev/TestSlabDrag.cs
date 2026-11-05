using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode slabdrag (SlabDrag, user 2026-11-05: one drag lays the whole area a holder reaches, not ring by ring). Run with
    // -projectzfloors=3 -projectzmapsize=250 -projectzseed=7; -projectznoslabdrag: the old rule (C1 and more then fail).
    // Every drag goes through the game's own input: the mouse is a screen point, DesignatorManager.ProcessInputEvents gets
    // real mouse events, the DesignationDragger runs its frames. Every check has its own fixture (the area wiped first).
    //   C1 rectangle from a house wall past the house: blueprints exactly to 6 cells out; the far ones wait; colonists build
    //      all of it outward, nothing floats or falls
    //   C2 rectangle round one wall: exactly the cells within 6.9 of it
    //   C3 line out from a wall, drawn from the far end: the 6th cell in, the 7th out; a line passing 2 cells off a wall: none
    //   C4 rectangle nothing holds: none, with the current message
    //   C5 a row of tall buildings across the drag: no cell past it (only cells a slab can go on count)
    //   C6 drag across a roofed house, out on both sides
    //   C7 second to third floor, built by colonists up two stairs
    //   C8 ship deck drag over open substructure from the hull, built by colonists
    //   C9 a drag cancelled by right click leaves nothing; single clicks behave as before
    //   C11 support changing under a held drag, unpaused: a wall finished by a colonist, a wall taken down by one. The preview
    //       follows within the refresh; with the answers held stale on purpose the release still decides from the map as it is
    //   C10 cost of a frame of a large drag, also over a grid of columns, mouse still and moving (timings only)
    public static partial class TestRunner
    {
        private const int DgHouseX0 = 20, DgHouseX1 = 28, DgZ0 = 12, DgZ1 = 18;

        private static Designator_Build dgDes;
        private static Type dgStyle;
        private static IntVec3 dgFrom, dgTo;
        private static int dgFloor, dgFloating, dgCollapsesAt, dgPerfLeft, dgPerfCase, dgPasses, dgPlannedAt;
        private static string dgNext;
        private static bool dgCancel;
        private static string dgCheck;
        private static HashSet<IntVec3> dgWant = new HashSet<IntVec3>();
        private static List<IntVec3> dgDragged = new List<IntVec3>();
        private static string dgFailure;
        private static readonly List<IntVec3> dgProbes = new List<IntVec3>();
        private static readonly Dictionary<IntVec3, string> dgProbed = new Dictionary<IntVec3, string>();
        private static readonly List<Pawn> dgCrewAll = new List<Pawn>();
        private static readonly List<double> dgUpdateMs = new List<double>();
        private static readonly List<double> dgCellsMs = new List<double>();
        private static readonly List<double> dgMoveMs = new List<double>();
        private static readonly List<double> dgReleaseMs = new List<double>();
        private static List<double> dgTimeInto;
        private static readonly Stopwatch dgWatch = new Stopwatch();
        private static string dgHold;
        private static int dgChangeFrame, dgStillPreview;
        private static bool dgMoveFlip;
        private static int dgAccFrame = -1;
        private static double dgAcc;
        private static readonly List<int> dgMoveShapes = new List<int>();

        internal static void PatchDragTimer(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(DesignationDragger), nameof(DesignationDragger.DraggerUpdate)),
                prefix: new HarmonyMethod(typeof(TestRunner), nameof(DgTimeStart)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(DgTimeEndUpdate)));
            h.Patch(AccessTools.Method(typeof(DesignationDragger), nameof(DesignationDragger.UpdateDragCellsIfNeeded)),
                prefix: new HarmonyMethod(typeof(TestRunner), nameof(DgTimeStart)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(DgTimeEnd)));
        }

        // The game's own dragger work of a frame, timed: its update, and the full list the build panel's cost readout asks for.
        private static void DgTimeStart() => dgWatch.Restart();

        private static void DgTimeEnd()
        {
            dgWatch.Stop();
            if (dgTimeInto == null)
            {
                return;
            }
            if (Time.frameCount != dgAccFrame)
            {
                if (dgAccFrame >= 0)
                {
                    dgTimeInto.Add(dgAcc);
                }
                dgAcc = 0;
                dgAccFrame = Time.frameCount;
            }
            dgAcc += dgWatch.Elapsed.TotalMilliseconds;
        }

        private static void DgTimeEndUpdate()
        {
            DgTimeEnd();
            if (dgTimeInto == dgMoveMs)
            {
                // (a new cell under the mouse for every frame)
                dgMoveShapes.Add(Find.DesignatorManager.Dragger.CellBuffer.Count);
                dgMoveFlip = !dgMoveFlip;
                fakeMouse = ScreenOf(dgMoveFlip ? dgTo + new IntVec3(-1, 0, 0) : dgTo, 1);
            }
        }

        private static int DgPreview() => Traverse.Create(Find.DesignatorManager.Dragger).Field("numSelectedCells").GetValue<int>();

        private static string DgStats(List<double> l, int skip = 0)
        {
            List<double> s = l.Skip(skip).OrderBy(x => x).ToList();
            return s.Count == 0 ? "-" : $"median {s[s.Count / 2]:0.00} max {s[s.Count - 1]:0.00}";
        }

        // C11's fixture: open ground, one wall site east of the dragged rectangle.
        private static void DgC11(Map map, string check, bool wall)
        {
            DgFresh(map);
            dgCheck = check;
            IntVec3 site = RG(20, 22);
            if (wall)
            {
                R2Wall(map, site);
            }
            else
            {
                GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, site, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
            }
            DgCrew(map, RG(26, 22), ThingDefOf.WoodLog, 75);
            DgSetWant(DgRect(14, 20, 19, 24));
        }

        private static bool DgWallUp(Map map) => RG(20, 22).GetEdifice(map)?.def == ThingDefOf.Wall;

        private static void DgC11Drag(Map map, string hold) =>
            DgDrag(map, DgFloor(), typeof(DrawStyle_FilledRectangle), RG(10, 20), RG(19, 24), 1, hold + "_done", hold: hold);

        private static bool StepSlabDrag(Map map)
        {
            Rand.PushState((int)(StableHash("slabdrag_" + stage) ^ (uint)(SeedArg ?? 717)));
            try
            {
                return StepSlabDragInner(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        // A mouse event through the game's own input handling.
        private static void DgEvent(EventType type, int button)
        {
            guiAction = () =>
            {
                Event old = Event.current;
                try
                {
                    Event.current = new Event { type = type, button = button };
                    Find.DesignatorManager.ProcessInputEvents();
                }
                finally
                {
                    Event.current = old;
                }
            };
        }

        // Select d with a draw style and drag from one ground cell to another on the floor; goes on at stage next.
        private static void DgDrag(Map map, Designator_Build d, Type style, IntVec3 from, IntVec3 to, int floor, string next, bool cancel = false, float size = 30f, string hold = null)
        {
            dgHold = hold;
            dgDes = d;
            dgStyle = style;
            dgFrom = from;
            dgTo = to;
            dgFloor = floor;
            dgNext = next;
            dgCancel = cancel;
            ViewUp(map, new IntVec3((from.x + to.x) / 2, 0, (from.z + to.z) / 2), size);
            FloorView.Set(floor);
            DesignatorManager dm = Find.DesignatorManager;
            dm.Select(d);
            dm.SelectedStyle = d.DrawStyleCategory.styles.First(s => s.DrawStyleWorker.GetType() == style);
            Wait(5, "dg_press");
        }

        private static HashSet<IntVec3> DgPlanned(Map map, int floor)
        {
            HashSet<IntVec3> found = new HashSet<IntVec3>();
            foreach (IntVec3 g in r2Area)
            {
                IntVec3 c = Levels.Cell(g, floor, map);
                if (PlaceWorker_Stairs.FloorPlanned(c, map) || ShipDeck.Planned(c, map))
                {
                    found.Add(g);
                }
            }
            return found;
        }

        private static string DgLocal(IntVec3 g) => $"({g.x - r2Area.minX},{g.z - r2Area.minZ})";

        // The blueprints on the floor are exactly dgWant.
        private static void DgExpect(Map map, string name, int floor)
        {
            HashSet<IntVec3> got = DgPlanned(map, floor);
            List<IntVec3> missing = dgWant.Where(g => !got.Contains(g)).ToList();
            List<IntVec3> extra = got.Where(g => !dgWant.Contains(g)).ToList();
            Result(name, missing.Count == 0 && extra.Count == 0,
                $"{got.Count} planned, {dgWant.Count} wanted, drag had {dgDragged.Count}; missing {missing.Count} [{string.Join(" ", missing.Take(8).Select(DgLocal))}], extra {extra.Count} [{string.Join(" ", extra.Take(8).Select(DgLocal))}]; failure '{dgFailure}'");
        }

        private static void DgProbe(string name, IntVec3 g, string want)
        {
            string got = dgProbed.TryGetValue(g, out string r) ? r : "not probed";
            Result(name, got == want, $"{DgLocal(g)}: '{got}'" + (got == want ? "" : $" (want '{want}')"));
        }

        private static CellRect DgRect(int x0, int z0, int x1, int z1) => CellRect.FromLimits(RG(x0, z0), RG(x1, z1));

        private static void DgSetWant(CellRect r) => dgWant = new HashSet<IntVec3>(r.Cells);

        private static void DgFresh(Map map)
        {
            foreach (Pawn p in Colonists(map))
            {
                Draft(p, true);
            }
            R2Clear(map);
            R2Feed(map);
            dgProbes.Clear();
            dgProbed.Clear();
            dgFloating = 0;
            dgCollapsesAt = PZStats.slabCollapses;
        }

        private static void DgRing(Map map, int level, IntVec3? gap = null)
        {
            foreach (IntVec3 g in DgRect(DgHouseX0, DgZ0, DgHouseX1, DgZ1).EdgeCells)
            {
                if (level == 0 && g == RG(24, DgZ0))
                {
                    // (a way in: what falls into the room stays reachable)
                    Place(ThingDefOf.Door, ThingDefOf.WoodLog, g, map);
                }
                else if (g != gap)
                {
                    R2Wall(map, Levels.Cell(g, level, map));
                }
            }
        }

        // Stairs up the house's east side: a ledge on floor 2 from the wall to their exit.
        private static void DgStairs(Map map)
        {
            R2Floor(map, DgRect(DgHouseX1 + 1, 15, 32, 15), 1);
            Building_Stairs st = R2Stairs(map, RG(34, 15), Rot4.West);
            Info($"stairs {st?.Position}, exit {(st == null ? "-" : StairShape.Exit(st.Position, st.Rotation, map).ToString())}");
        }

        private static void DgCrew(Map map, IntVec3 at, ThingDef stuff, int amount)
        {
            while (amount > 0)
            {
                Thing t = ThingMaker.MakeThing(stuff);
                t.stackCount = Mathf.Min(amount, stuff.stackLimit);
                amount -= t.stackCount;
                GenPlace.TryPlaceThing(t, at, map, ThingPlaceMode.Near);
            }
            int i = 0;
            foreach (Pawn p in Colonists(map))
            {
                Teleport(p, CellFinder.StandableCellNear(at, map, 6f));
                Draft(p, false);
                R2Work(p, WorkTypeDefOf.Construction, WorkTypeDefOf.Hauling);
                i++;
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Info($"crew {i} at {at}: {string.Join(", ", Colonists(map).Select(p => p.LabelShort))}");
        }

        private static bool DgBuiltAt(IntVec3 c, Map map) => Slab.At(c, map) || ShipDeck.At(c, map);

        // The tick watch of a build: no built cell without actual support; true when all of dgWant is built.
        private static bool DgBuilt(Map map, int floor)
        {
            bool all = true;
            foreach (IntVec3 g in dgWant)
            {
                IntVec3 c = Levels.Cell(g, floor, map);
                if (!DgBuiltAt(c, map))
                {
                    all = false;
                }
                else if (!SlabSupport.Supported(FloorRoof.Below(c, map), map))
                {
                    dgFloating++;
                }
            }
            return all;
        }

        // After a build: what is left, for the log.
        private static void DgBuildInfo(Map map, string what, int floor)
        {
            foreach (IntVec3 g in dgWant.Where(x => !DgBuiltAt(Levels.Cell(x, floor, map), map)).Take(6))
            {
                IntVec3 c = Levels.Cell(g, floor, map);
                Thing t = c.GetThingList(map).FirstOrDefault(x => x is Blueprint || x is Frame);
                JobFailReason.Clear();
                bool can = t != null && GenConstruct.CanConstruct(t, Colonists(map).First(p => !p.Downed), false, true);
                Info($"{what} left {DgLocal(g)}: {string.Join(", ", c.GetThingList(map).Select(x => x.def.defName))}; can {can}, reason '{JobFailReason.Reason}', held {SlabSupport.Supported(FloorRoof.Below(c, map), map)}");
            }
            List<string> items = new List<string>();
            for (int L = 0; L <= Levels.Max(map); L++)
            {
                foreach (IntVec3 g in r2Area.ExpandedBy(6).ClipInsideMap(map))
                {
                    foreach (Thing t in Levels.Cell(g, L, map).GetThingList(map))
                    {
                        if (t.def.category == ThingCategory.Item)
                        {
                            items.Add($"{t.def.defName} x{t.stackCount} {DgLocal(g)} L{L}");
                        }
                    }
                }
            }
            Info($"{what} items left: {string.Join("; ", items.Take(30))}");
            Info($"{what} gravity: {Gravity.Counters}");
            foreach (Pawn p in dgCrewAll)
            {
                Info($"{what} {p.LabelShort} at {p.PositionHeld} dead {p.Dead} downed {p.Downed}: {string.Join(", ", p.health.hediffSet.hediffs.Select(h => h.LabelCap + (h.Part != null ? " " + h.Part.Label : "")))}");
            }
        }

        private static void DgBuildResult(Map map, string what, int floor)
        {
            DgBuildInfo(map, what, floor);
            int built = dgWant.Count(g => DgBuiltAt(Levels.Cell(g, floor, map), map));
            int roofs = dgWant.Count(g => map.roofGrid.RoofAt(Levels.Cell(g, floor - 1, map)) == PZDefOf.PZ_FloorRoof);
            Result(what + ": colonists finish every cell, none ever floating, none fell", built == dgWant.Count && roofs == dgWant.Count && dgFloating == 0 && PZStats.slabCollapses == dgCollapsesAt,
                $"built {built}/{dgWant.Count}, floor roofs {roofs}, floating sightings {dgFloating}, collapses {PZStats.slabCollapses - dgCollapsesAt}, {ticksTotal} ticks");
        }

        private static Designator_Build DgFloor()
        {
            return BuildCopyCommandUtility.FindAllowedDesignator(PZDefOf.PZ_UpperFloor);
        }

        private static bool StepSlabDragInner(Map map)
        {
            switch (stage)
            {
                case "dg_setup":
                {
                    DebugSettings.enableRandomMentalStates = false;
                    Find.Storyteller.storytellerComps.Clear();
                    Find.PlaySettings.autoRebuild = false;
                    NoAmbushes(map);
                    AnnexMapComponent an = ProjectZ.Annex(map);
                    if (Levels.Max(map) < 2 || an.groundZ < 130)
                    {
                        Result("slabdrag: a map with 3 levels and 130 ground rows", false, $"levels {Levels.Min(map)}..{Levels.Max(map)}, ground rows {an.groundZ} (run with -projectzfloors=3 -projectzmapsize=250)");
                        Finish();
                        return true;
                    }
                    foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
                    {
                        a.Destroy();
                    }
                    r2Area = new CellRect(map.Size.x / 2 - 30, an.groundZ / 2 - 24, 60, 48);
                    List<Pawn> cols = Colonists(map);
                    r2P = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) ?? cols.First();
                    r2Q = cols.FirstOrDefault(p => p != r2P);
                    dgCrewAll.Clear();
                    dgCrewAll.AddRange(cols);
                    foreach (Pawn p in cols)
                    {
                        p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
                        // (no birth in the middle of a check)
                        foreach (Hediff h in p.health.hediffSet.hediffs.Where(x => x.def.defName.StartsWith("Pregnant") || x.def.defName == "PregnancyLabor" || x.def.defName == "PregnancyLaborPushing").ToList())
                        {
                            p.health.RemoveHediff(h);
                        }
                    }
                    if (DgFloor() == null)
                    {
                        Result("slabdrag: the upper floor designator", false, "not found");
                        Finish();
                        return true;
                    }
                    Info($"SlabDrag off: {SlabDrag.Off}; area {r2Area}; styles {string.Join(", ", DgFloor().DrawStyleCategory.styles.Select(s => s.defName))}");
                    stage = "dg_c1";
                    return true;
                }

                // ---- the drag itself, through the game's input
                case "dg_press":
                {
                    fakeMouse = ScreenOf(dgFrom, dgFloor);
                    IntVec3 start = UI.MouseCell();
                    IntVec3 wantStart = OnFloor(dgFrom, dgFloor, map);
                    if (start != wantStart)
                    {
                        Result($"{dgCheck}: the drag starts on the viewed floor", false, $"{start}, want {wantStart}");
                    }
                    DgEvent(EventType.MouseDown, 0);
                    Wait(3, "dg_move");
                    return true;
                }
                case "dg_move":
                    fakeMouse = ScreenOf(dgTo, dgFloor);
                    Wait(5, dgHold ?? "dg_hold");
                    return true;
                case "dg_hold":
                {
                    DesignatorManager dm = Find.DesignatorManager;
                    dgDragged = dm.Dragger.DragCells.ToList();
                    dgFailure = dm.Dragger.FailureReason;
                    foreach (IntVec3 g in dgProbes)
                    {
                        AcceptanceReport r = dgDes.CanDesignateCell(Levels.Cell(g, dgFloor, map));
                        dgProbed[g] = r.Accepted ? "accepted" : r.Reason;
                    }
                    Info($"{dgCheck}: dragging {dm.Dragger.Dragging}, buffer {dm.Dragger.CellBuffer.Count}, accepted {dgDragged.Count}, failure '{dgFailure}', mouse {UI.MouseCell()}");
                    if (dgCancel)
                    {
                        DgEvent(EventType.MouseDown, 1);
                    }
                    else
                    {
                        DgEvent(EventType.MouseUp, 0);
                    }
                    Wait(3, "dg_after");
                    return true;
                }
                case "dg_after":
                    fakeMouse = null;
                    if (!dgCancel)
                    {
                        Find.DesignatorManager.Deselect();
                    }
                    stage = dgNext;
                    return true;

                // ---- C1 big rectangle from a wall, past the house
                case "dg_c1":
                {
                    DgFresh(map);
                    dgCheck = "C1";
                    DgRing(map, 0);
                    DgStairs(map);
                    DgSetWant(DgRect(14, DgZ0, DgHouseX1, DgZ1));
                    dgProbes.AddRange(new[] { RG(14, 15), RG(13, 15), RG(8, 15) });
                    DgDrag(map, DgFloor(), typeof(DrawStyle_FilledRectangle), RG(8, DgZ0), RG(DgHouseX1, DgZ1), 1, "dg_c1_placed");
                    return true;
                }
                case "dg_c1_placed":
                {
                    if (SlabDrag.Off)
                    {
                        // (the old rule: the player drags again until nothing more goes down)
                        int now = DgPlanned(map, 1).Count;
                        Info($"C1 old rule: drag {++dgPasses} planned {now - dgPlannedAt} new cells");
                        if (now > dgPlannedAt)
                        {
                            dgPlannedAt = now;
                            DgDrag(map, DgFloor(), typeof(DrawStyle_FilledRectangle), RG(8, DgZ0), RG(DgHouseX1, DgZ1), 1, "dg_c1_placed");
                            return true;
                        }
                    }
                    DgExpect(map, "C1 one rectangle from a wall: every cell to 6 past the house, none farther", 1);
                    DgProbe("C1 6 cells past the wall accepted while dragging", RG(14, 15), "accepted");
                    string far = "PZ_NoSupportBelow".Translate();
                    DgProbe("C1 7 cells past the wall refused with the current message", RG(13, 15), far);
                    DgProbe("C1 13 cells past the wall refused", RG(8, 15), far);
                    DgCrew(map, RG(38, 15), ThingDefOf.WoodLog, dgWant.Count * SlabDefs.Cost + 150);
                    // (the builders' rule itself, as the work giver asks it; the cell by the wall may only be out of reach yet)
                    Pawn b = Colonists(map).First(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction));
                    Thing farBp = Levels.Cell(RG(14, 15), 1, map).GetThingList(map).FirstOrDefault(t => t is Blueprint);
                    Thing nearBp = Levels.Cell(RG(19, 15), 1, map).GetThingList(map).FirstOrDefault(t => t is Blueprint);
                    JobFailReason.Clear();
                    bool farCan = farBp != null && GenConstruct.CanConstruct(farBp, b, false, true);
                    string farWhy = JobFailReason.Reason;
                    JobFailReason.Clear();
                    if (nearBp != null)
                    {
                        GenConstruct.CanConstruct(nearBp, b, false, true);
                    }
                    string nearWhy = JobFailReason.Reason;
                    string held = "PZ_NoSupportBelow".Translate();
                    Result("C1 builders go outward: the far blueprint waits for support, the one by the wall does not", farBp != null && nearBp != null && !farCan && farWhy == held && nearWhy != held, $"far {farBp?.Position} can {farCan} '{farWhy}', near {nearBp?.Position} '{nearWhy}'");
                    ViewUp(map, RG(21, 15), 18f);
                    Wait(20, "dg_c1_shot");
                    return true;
                }
                case "dg_c1_shot":
                    Capture("c1_blueprints");
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(150000, () => DgBuilt(map, 1), "dg_c1_built", 250);
                    return true;
                case "dg_c1_built":
                    tickWatch = null;
                    StartTicks(2500, "dg_c1_settled");
                    return true;
                case "dg_c1_settled":
                    DgBuilt(map, 1);
                    DgBuildResult(map, "C1", 1);
                    ViewUp(map, RG(21, 15), 18f);
                    Wait(20, "dg_c1_shot2");
                    return true;
                case "dg_c1_shot2":
                    Capture("c1_built");
                    Wait(2, "dg_c2");
                    return true;

                // ---- C2 rectangle round one wall: the 6.9 circle
                case "dg_c2":
                {
                    DgFresh(map);
                    dgCheck = "C2";
                    IntVec3 w = RG(30, 24);
                    R2Wall(map, w);
                    dgWant = new HashSet<IntVec3>(DgRect(20, 14, 40, 34).Cells.Where(g => (g - w).LengthHorizontalSquared <= RoofCollapseUtility.RoofMaxSupportDistance * RoofCollapseUtility.RoofMaxSupportDistance));
                    dgProbes.AddRange(new[] { RG(36, 27), RG(36, 28), RG(36, 24), RG(37, 24) });
                    DgDrag(map, DgFloor(), typeof(DrawStyle_FilledRectangle), RG(20, 14), RG(40, 34), 1, "dg_c2_done");
                    return true;
                }
                case "dg_c2_done":
                {
                    DgExpect(map, "C2 rectangle round one wall: exactly the cells within 6.9 of it", 1);
                    string far = "PZ_NoSupportBelow".Translate();
                    DgProbe("C2 (6,3) from the wall accepted, 6.7 cells", RG(36, 27), "accepted");
                    DgProbe("C2 (6,4) from the wall refused, 7.2 cells", RG(36, 28), far);
                    DgProbe("C2 (6,0) from the wall accepted", RG(36, 24), "accepted");
                    DgProbe("C2 (7,0) from the wall refused", RG(37, 24), far);
                    stage = "dg_c3";
                    return true;
                }

                // ---- C3 lines
                case "dg_c3":
                {
                    DgFresh(map);
                    dgCheck = "C3";
                    foreach (int z in Enumerable.Range(20, 5))
                    {
                        R2Wall(map, RG(30, z));
                    }
                    DgSetWant(DgRect(24, 22, 29, 22));
                    DgDrag(map, DgFloor(), typeof(DrawStyle_Line), RG(16, 22), RG(29, 22), 1, "dg_c3_done");
                    return true;
                }
                case "dg_c3_done":
                    DgExpect(map, "C3 a line drawn from the far end to the wall: 6 cells out, the 7th not", 1);
                    stage = "dg_c3b";
                    return true;
                case "dg_c3b":
                {
                    DgFresh(map);
                    dgCheck = "C3b";
                    foreach (int z in Enumerable.Range(20, 5))
                    {
                        R2Wall(map, RG(30, z));
                    }
                    dgWant = new HashSet<IntVec3>();
                    DgDrag(map, DgFloor(), typeof(DrawStyle_Line), RG(24, 26), RG(36, 26), 1, "dg_c3b_done");
                    return true;
                }
                case "dg_c3b_done":
                    DgExpect(map, "C3 a line passing 2 cells off the wall end, touching nothing that holds: none", 1);
                    stage = "dg_c4";
                    return true;

                // ---- C4 no holder
                case "dg_c4":
                {
                    DgFresh(map);
                    dgCheck = "C4";
                    R2Wall(map, RG(50, 40));
                    dgWant = new HashSet<IntVec3>();
                    DgDrag(map, DgFloor(), typeof(DrawStyle_FilledRectangle), RG(10, 10), RG(30, 30), 1, "dg_c4_done");
                    return true;
                }
                case "dg_c4_done":
                    DgExpect(map, "C4 a drag reaching no holder: nothing planned", 1);
                    Result("C4 the drag says the current message", dgFailure == "PZ_NoSupportBelow".Translate(), $"'{dgFailure}'");
                    stage = "dg_c5";
                    return true;

                // ---- C5 only cells a slab can go on count
                case "dg_c5":
                {
                    DgFresh(map);
                    dgCheck = "C5";
                    ThingDef tall = DefDatabase<ThingDef>.AllDefs.Where(t => t.BuildableByPlayer && t.size == IntVec2.One && !t.holdsRoof && !t.MadeFromStuff && t.passability != Traversability.Impassable && HeightRule.TooTall(t)).OrderBy(t => t.defName).FirstOrDefault();
                    tall = tall ?? DefDatabase<ThingDef>.AllDefs.Where(t => t.BuildableByPlayer && t.size == IntVec2.One && !t.holdsRoof && HeightRule.TooTall(t)).OrderBy(t => t.defName).FirstOrDefault();
                    if (tall == null)
                    {
                        Result("C5 a tall 1x1 building to test with", false, "none");
                        stage = "dg_c6";
                        return true;
                    }
                    Info($"C5 tall building {tall.defName}");
                    foreach (int z in Enumerable.Range(DgZ0, DgZ1 - DgZ0 + 1))
                    {
                        R2Wall(map, RG(20, z));
                        Place(tall, ThingDefOf.Steel, RG(17, z), map);
                    }
                    DgSetWant(DgRect(18, DgZ0, 19, DgZ1));
                    dgProbes.AddRange(new[] { RG(17, 15), RG(16, 15) });
                    DgDrag(map, DgFloor(), typeof(DrawStyle_FilledRectangle), RG(10, DgZ0), RG(19, DgZ1), 1, "dg_c5_done");
                    return true;
                }
                case "dg_c5_done":
                    DgExpect(map, "C5 a row of tall buildings across the drag: nothing past it", 1);
                    DgProbe("C5 the cell past the row refused for support", RG(16, 15), "PZ_NoSupportBelow".Translate());
                    Result("C5 the cell over the tall building keeps its own refusal", dgProbed.TryGetValue(RG(17, 15), out string tr) && tr != "accepted" && tr != "PZ_NoSupportBelow".Translate(), $"'{tr}'");
                    stage = "dg_c6";
                    return true;

                // ---- C6 across a roofed house
                case "dg_c6":
                {
                    DgFresh(map);
                    dgCheck = "C6";
                    DgRing(map, 0);
                    foreach (IntVec3 g in DgRect(DgHouseX0 + 1, DgZ0 + 1, DgHouseX1 - 1, DgZ1 - 1))
                    {
                        map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                    }
                    DgSetWant(DgRect(14, DgZ0, 34, DgZ1));
                    DgDrag(map, DgFloor(), typeof(DrawStyle_FilledRectangle), RG(6, DgZ0), RG(42, DgZ1), 1, "dg_c6_done");
                    return true;
                }
                case "dg_c6_done":
                    DgExpect(map, "C6 a drag across a roofed house: the house and 6 cells out on both sides", 1);
                    stage = "dg_c7";
                    return true;

                // ---- C7 second to third floor, built up two stairs
                case "dg_c7":
                {
                    DgFresh(map);
                    dgCheck = "C7";
                    DgRing(map, 0);
                    R2Floor(map, DgRect(DgHouseX0, DgZ0, DgHouseX1, DgZ1), 1);
                    DgRing(map, 1, RG(DgHouseX1, 15));
                    DgStairs(map);
                    R2Floor(map, DgRect(23, 15, 23, 15), 2);
                    Thing st2 = Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, RL(25, 15, 1, map), map, Rot4.West);
                    Info($"stairs 2 {st2?.Position}");
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    DgSetWant(DgRect(14, DgZ0, 22, DgZ1));
                    dgProbes.AddRange(new[] { RG(14, 15), RG(13, 15) });
                    DgDrag(map, DgFloor(), typeof(DrawStyle_FilledRectangle), RG(6, DgZ0), RG(22, DgZ1), 2, "dg_c7_placed");
                    return true;
                }
                case "dg_c7_placed":
                    DgExpect(map, "C7 third floor: one rectangle from the second floor's wall", 2);
                    DgProbe("C7 6 cells past the upper wall accepted", RG(14, 15), "accepted");
                    DgProbe("C7 7 cells past the upper wall refused", RG(13, 15), "PZ_NoSupportBelow".Translate());
                    Result("C7 nothing planned on the second floor", DgPlanned(map, 1).Count == 0, $"{DgPlanned(map, 1).Count}");
                    DgCrew(map, RG(38, 15), ThingDefOf.WoodLog, dgWant.Count * SlabDefs.Cost + 150);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(150000, () => DgBuilt(map, 2), "dg_c7_built", 250);
                    return true;
                case "dg_c7_built":
                    tickWatch = null;
                    DgBuildResult(map, "C7", 2);
                    stage = "dg_c9";
                    return true;

                // ---- C9 cancel, then single clicks
                case "dg_c9":
                {
                    DgFresh(map);
                    dgCheck = "C9";
                    DgRing(map, 0);
                    dgWant = new HashSet<IntVec3>();
                    dgProbes.Add(RG(15, 15));
                    DgDrag(map, DgFloor(), typeof(DrawStyle_FilledRectangle), RG(8, DgZ0), RG(19, DgZ1), 1, "dg_c9_cancelled", cancel: true);
                    return true;
                }
                case "dg_c9_cancelled":
                {
                    DesignatorManager dm = Find.DesignatorManager;
                    DgProbe("C9 during the drag a far cell was accepted", RG(15, 15), "accepted");
                    Result("C9 the right click ended the drag", !dm.Dragger.Dragging && dm.SelectedDesignator == null, $"dragging {dm.Dragger.Dragging}, selected {dm.SelectedDesignator}");
                    DgExpect(map, "C9 a cancelled drag leaves nothing", 1);
                    Designator_Build d = DgFloor();
                    dm.Select(d);
                    AcceptanceReport hover = d.CanDesignateCell(Levels.Cell(RG(15, 15), 1, map));
                    Result("C9 afterwards the same cell, not dragging, is refused", !hover.Accepted && hover.Reason == "PZ_NoSupportBelow".Translate(), $"'{hover.Reason}'");
                    dm.Deselect();
                    dgWant = new HashSet<IntVec3>();
                    DgDrag(map, DgFloor(), typeof(DrawStyle_FilledRectangle), RG(15, 15), RG(15, 15), 1, "dg_c9_click1");
                    return true;
                }
                case "dg_c9_click1":
                    DgExpect(map, "C9 a single click 5 cells out is refused as before", 1);
                    dgWant = new HashSet<IntVec3> { RG(19, 15) };
                    DgDrag(map, DgFloor(), typeof(DrawStyle_FilledRectangle), RG(19, 15), RG(19, 15), 1, "dg_c9_click2");
                    return true;
                case "dg_c9_click2":
                    DgExpect(map, "C9 a single click next to the wall is accepted", 1);
                    dgWant.Add(RG(18, 15));
                    DgDrag(map, DgFloor(), typeof(DrawStyle_FilledRectangle), RG(18, 15), RG(18, 15), 1, "dg_c9_click3");
                    return true;
                case "dg_c9_click3":
                    DgExpect(map, "C9 a single click next to a blueprint is accepted, as before", 1);
                    DgDrag(map, DgFloor(), typeof(DrawStyle_FilledRectangle), RG(16, 15), RG(16, 15), 1, "dg_c9_click4");
                    return true;
                case "dg_c9_click4":
                    DgExpect(map, "C9 a single click one cell off the blueprints is refused, as before", 1);
                    stage = "dg_c11a";
                    return true;

                // ---- C11 support changes while the mouse holds still, the game running
                case "dg_c11a":
                    DgC11(map, "C11a", false);
                    DgC11Drag(map, "dg_c11a_hold");
                    return true;
                case "dg_c11a_hold":
                    Result("C11a before the wall: the preview shows nothing", DgPreview() == 0, $"preview {DgPreview()}");
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(30000, () => DgWallUp(map), "dg_c11a_up", 1);
                    return true;
                case "dg_c11a_up":
                    tickWatch = null;
                    Result("C11a a colonist finished the wall during the drag", DgWallUp(map) && Find.DesignatorManager.Dragger.Dragging, $"wall {DgWallUp(map)}, dragging {Find.DesignatorManager.Dragger.Dragging}, speed {Find.TickManager.CurTimeSpeed}, {ticksTotal} ticks");
                    dgChangeFrame = Time.frameCount;
                    stage = "dg_c11a_watch";
                    return true;
                case "dg_c11a_watch":
                    if (DgPreview() != dgWant.Count && Time.frameCount - dgChangeFrame <= 90)
                    {
                        Wait(1, "dg_c11a_watch");
                        return true;
                    }
                    Result("C11a the preview takes the new wall within the refresh", DgPreview() == dgWant.Count && Time.frameCount - dgChangeFrame <= SlabDrag.RefreshFrames + 3, $"preview {DgPreview()}/{dgWant.Count} after {Time.frameCount - dgChangeFrame} frames");
                    stage = "dg_hold";
                    return true;
                case "dg_c11a_hold_done":
                    DgExpect(map, "C11a release: the cells the new wall holds are planned", 1);
                    stage = "dg_c11b";
                    return true;
                case "dg_c11b":
                    DgC11(map, "C11b", false);
                    DgC11Drag(map, "dg_c11b_hold");
                    return true;
                case "dg_c11b_hold":
                    SlabDrag.RefreshFrames = int.MaxValue;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(30000, () => DgWallUp(map), "dg_c11b_up", 1);
                    return true;
                case "dg_c11b_up":
                    tickWatch = null;
                    dgChangeFrame = Time.frameCount;
                    Wait(40, "dg_c11b_stale");
                    return true;
                case "dg_c11b_stale":
                    dgStillPreview = DgPreview();
                    Result("C11b wall up, answers held: the preview is still the old one after 40 frames", DgWallUp(map) && dgStillPreview == 0, $"wall {DgWallUp(map)}, preview {dgStillPreview}");
                    stage = "dg_hold";
                    return true;
                case "dg_c11b_hold_done":
                    SlabDrag.RefreshFrames = 30;
                    Result("C11b the dragger's list of that frame was the stale one (so the release had to redo it)", dgDragged.Count == 0, $"{dgDragged.Count}");
                    DgExpect(map, "C11b release decides fresh: a cell that gained support is accepted", 1);
                    stage = "dg_c11c";
                    return true;
                case "dg_c11c":
                    DgC11(map, "C11c", true);
                    DgC11Drag(map, "dg_c11c_hold");
                    return true;
                case "dg_c11c_hold":
                    Result("C11c with the wall: the preview shows the cells it holds", DgPreview() == dgWant.Count, $"preview {DgPreview()}/{dgWant.Count}");
                    map.designationManager.AddDesignation(new Designation(RG(20, 22).GetEdifice(map), DesignationDefOf.Deconstruct));
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(30000, () => !DgWallUp(map), "dg_c11c_gone", 1);
                    return true;
                case "dg_c11c_gone":
                    tickWatch = null;
                    Result("C11c a colonist took the wall down during the drag", !DgWallUp(map) && Find.DesignatorManager.Dragger.Dragging, $"wall {DgWallUp(map)}, dragging {Find.DesignatorManager.Dragger.Dragging}, {ticksTotal} ticks");
                    dgChangeFrame = Time.frameCount;
                    stage = "dg_c11c_watch";
                    return true;
                case "dg_c11c_watch":
                    if (DgPreview() != 0 && Time.frameCount - dgChangeFrame <= 90)
                    {
                        Wait(1, "dg_c11c_watch");
                        return true;
                    }
                    Result("C11c the preview drops the cells within the refresh", DgPreview() == 0 && Time.frameCount - dgChangeFrame <= SlabDrag.RefreshFrames + 3, $"preview {DgPreview()} after {Time.frameCount - dgChangeFrame} frames");
                    stage = "dg_hold";
                    return true;
                case "dg_c11c_hold_done":
                    dgWant = new HashSet<IntVec3>();
                    DgExpect(map, "C11c release: nothing planned", 1);
                    stage = "dg_c11d";
                    return true;
                case "dg_c11d":
                    DgC11(map, "C11d", true);
                    DgC11Drag(map, "dg_c11d_hold");
                    return true;
                case "dg_c11d_hold":
                    SlabDrag.RefreshFrames = int.MaxValue;
                    map.designationManager.AddDesignation(new Designation(RG(20, 22).GetEdifice(map), DesignationDefOf.Deconstruct));
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(30000, () => !DgWallUp(map), "dg_c11d_gone", 1);
                    return true;
                case "dg_c11d_gone":
                    tickWatch = null;
                    Wait(40, "dg_c11d_stale");
                    return true;
                case "dg_c11d_stale":
                    dgStillPreview = DgPreview();
                    Result("C11d wall gone, answers held: the preview is still the old one after 40 frames", !DgWallUp(map) && dgStillPreview == dgWant.Count, $"wall {DgWallUp(map)}, preview {dgStillPreview}/{dgWant.Count}");
                    stage = "dg_hold";
                    return true;
                case "dg_c11d_hold_done":
                    SlabDrag.RefreshFrames = 30;
                    Result("C11d the dragger's list of that frame was the stale one", dgDragged.Count == dgWant.Count, $"{dgDragged.Count}/{dgWant.Count}");
                    dgWant = new HashSet<IntVec3>();
                    DgExpect(map, "C11d release decides fresh: a cell that lost support is refused", 1);
                    stage = "dg_c8";
                    return true;

                // ---- C8 ship deck, last: it leaves foundations behind
                case "dg_c8":
                {
                    if (!ModsConfig.OdysseyActive || PZDefOf.PZ_ShipDeck == null)
                    {
                        Result("C8 ship deck: Odyssey", false, "not active");
                        stage = "dg_c10";
                        return true;
                    }
                    DgFresh(map);
                    dgCheck = "C8";
                    Find.ResearchManager.gravEngineInspected = true;
                    IntVec3 o = RG(26, 14);
                    Func<int, int, IntVec3> at = (dx, dz) => o + new IntVec3(dx, 0, dz);
                    BuildShip(map, o, 13, 11);
                    GlEngine(map).nameHidden = false;
                    TerrainDef sub = DefDatabase<TerrainDef>.GetNamed("Substructure");
                    foreach (IntVec3 g in CellRect.FromLimits(at(-8, 2), at(-1, 4)))
                    {
                        map.terrainGrid.SetTerrain(g, sub);
                    }
                    GlEngine(map).ForceSubstructureDirty();
                    foreach (int dz in new[] { 5, 6, 7, 8 })
                    {
                        map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(at(-1, dz), 1, map), PZDefOf.PZ_UpperFloor);
                    }
                    Thing st = Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, at(-1, 10), map, Rot4.South);
                    Info($"C8 ship at {DgLocal(o)}, stairs {st?.Position} exit {StairShape.Exit(at(-1, 10), Rot4.South, map)}");
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    dgWant = new HashSet<IntVec3>(CellRect.FromLimits(at(-6, 2), at(-1, 4)).Cells);
                    dgProbes.AddRange(new[] { at(-6, 3), at(-7, 3) });
                    DgDrag(map, new Designator_Build(PZDefOf.PZ_ShipDeck), typeof(DrawStyle_FilledRectangle), at(-8, 2), at(-1, 4), 1, "dg_c8_placed");
                    return true;
                }
                case "dg_c8_placed":
                {
                    IntVec3 o = RG(26, 14);
                    DgExpect(map, "C8 ship deck: one rectangle from the hull over open substructure, 6 cells out", 1);
                    DgProbe("C8 6 cells from the hull accepted", o + new IntVec3(-6, 0, 3), "accepted");
                    DgProbe("C8 7 cells from the hull refused", o + new IntVec3(-7, 0, 3), "PZ_NoSupportBelow".Translate());
                    IntVec3 pile = o + new IntVec3(-4, 0, 12);
                    DgCrew(map, pile, ThingDefOf.Steel, dgWant.Count * 4 + 20);
                    Thing gp = ThingMaker.MakeThing(ThingDefOf.GravlitePanel);
                    gp.stackCount = dgWant.Count + 5;
                    GenPlace.TryPlaceThing(gp, pile, map, ThingPlaceMode.Near);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    TickUntil(120000, () => DgBuilt(map, 1), "dg_c8_built", 250);
                    return true;
                }
                case "dg_c8_built":
                    tickWatch = null;
                    DgBuildResult(map, "C8", 1);
                    stage = "dg_c10";
                    return true;

                // ---- C10 cost of a large drag: three drags, timed per frame with the set rebuilt in each
                case "dg_c10":
                {
                    foreach (Pawn p in Colonists(map))
                    {
                        Draft(p, true);
                    }
                    if (GlEngine(map) != null)
                    {
                        // (the ship stays out of the way: the timing fixture starts on the far side of the map)
                        Info("C10 ship left in place");
                    }
                    AnnexMapComponent an = ProjectZ.Annex(map);
                    r2Area = new CellRect(map.Size.x / 2 - 60, an.groundZ - 124, 120, 120);
                    DgFresh(map);
                    // a house in the middle of the area
                    foreach (IntVec3 g in new CellRect(r2Area.minX + 55, r2Area.minZ + 55, 10, 10).EdgeCells)
                    {
                        R2Wall(map, g);
                    }
                    dgPerfCase = 0;
                    stage = "dg_c10_case";
                    return true;
                }
                case "dg_c10_case":
                {
                    if (dgPerfCase == 4)
                    {
                        Finish();
                        return true;
                    }
                    if (dgPerfCase == 3)
                    {
                        // (the costliest kind: columns everywhere, so no cell is ruled out early and most walk far)
                        for (int x = 0; x < 120; x += 12)
                        {
                            for (int z = 0; z < 120; z += 12)
                            {
                                R2Wall(map, RG(x, z));
                            }
                        }
                    }
                    int half = dgPerfCase == 0 ? 15 : dgPerfCase == 1 ? 30 : 58;
                    float size = dgPerfCase == 0 ? 25f : dgPerfCase == 1 ? 40f : 60f;
                    dgCheck = "C10 " + (2 * half + 1) + (dgPerfCase == 3 ? " columns" : "");
                    IntVec3 mid = RG(60, 60);
                    dgFrom = new IntVec3(mid.x - half, 0, mid.z - half);
                    dgTo = new IntVec3(mid.x + half, 0, mid.z + half);
                    dgFloor = 1;
                    ViewUp(map, mid, size);
                    FloorView.Set(1);
                    Designator_Build d = DgFloor();
                    DesignatorManager dm = Find.DesignatorManager;
                    dm.Select(d);
                    dm.SelectedStyle = d.DrawStyleCategory.styles.First(s => s.DrawStyleWorker is DrawStyle_FilledRectangle);
                    dgDes = d;
                    Wait(5, "dg_c10_press");
                    return true;
                }
                case "dg_c10_press":
                    fakeMouse = ScreenOf(dgFrom, 1);
                    DgEvent(EventType.MouseDown, 0);
                    Wait(3, "dg_c10_move");
                    return true;
                case "dg_c10_move":
                    fakeMouse = ScreenOf(dgTo, 1);
                    dgUpdateMs.Clear();
                    dgCellsMs.Clear();
                    dgMoveMs.Clear();
                    dgMoveShapes.Clear();
                    dgReleaseMs.Clear();
                    dgPerfLeft = 95;
                    Wait(3, "dg_c10_still");
                    dgTimeInto = dgUpdateMs;
                    return true;
                case "dg_c10_still":
                {
                    if (--dgPerfLeft > 0)
                    {
                        Wait(1, "dg_c10_still");
                        return true;
                    }
                    dgTimeInto = dgMoveMs;
                    dgPerfLeft = 40;
                    stage = "dg_c10_moving";
                    return true;
                }
                case "dg_c10_moving":
                {
                    if (--dgPerfLeft > 0)
                    {
                        Wait(1, "dg_c10_moving");
                        return true;
                    }
                    dgTimeInto = null;
                    fakeMouse = ScreenOf(dgTo, 1);
                    dgPerfLeft = 6;
                    Wait(3, "dg_c10_release");
                    return true;
                }
                case "dg_c10_release":
                {
                    // (what the mouse-up costs: everything dropped, the whole list again)
                    DesignatorManager dm = Find.DesignatorManager;
                    Traverse.Create(dm.Dragger).Field("lastUpdateFrame").SetValue(-1);
                    Stopwatch sw = Stopwatch.StartNew();
                    SlabDrag.Release(dm.Dragger);
                    int n = dm.Dragger.DragCells.Count;
                    sw.Stop();
                    dgReleaseMs.Add(sw.Elapsed.TotalMilliseconds);
                    if (--dgPerfLeft > 0)
                    {
                        Wait(1, "dg_c10_release");
                        return true;
                    }
                    Result($"{dgCheck} drag timed (SlabDrag off {SlabDrag.Off})", true,
                        $"buffer {dm.Dragger.CellBuffer.Count}, accepted {n}; dragger work per frame ms: still {DgStats(dgUpdateMs, 5)} ({dgUpdateMs.Count} frames), moving {DgStats(dgMoveMs, 3)} ({dgMoveMs.Count} frames, buffer sizes {string.Join(" ", dgMoveShapes.Take(4))}); release {DgStats(dgReleaseMs)}");
                    DgEvent(EventType.MouseDown, 1);
                    Wait(3, "dg_c10_next");
                    return true;
                }
                case "dg_c10_next":
                    fakeMouse = null;
                    dgPerfCase++;
                    stage = "dg_c10_case";
                    return true;
            }
            return false;
        }
    }
}
