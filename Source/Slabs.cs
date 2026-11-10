using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // Taking up upper floor cells (design isorim-pz-floor-removal-design.md, §7 decisions). A slab is to the game's own
    // "Remove bridge / foundation" tool what a bridge over water is: the supporting layer. ProjectZ teaches that tool
    // about slabs, and every way a slab goes away runs through this file:
    //   - Remove: the designator, its work giver and job (and god mode) via TerrainGrid.CanRemoveFoundationAt /
    //     RemoveFoundation; a stairwell's floor (StairwellFloor, same designation); the stairs' own landing when they go.
    //     A floor laid on the slab (carpet) is a separate vanilla job first. Half the slab's stuff back, pawns and items moved off.
    //   - Blocker: what keeps a slab from being taken up (built things on it, stairs landing there). A slab above does not
    //     (Q6 (b), user 2026-11-01): it stays, resting on the roof over the new hole (vanilla roof support, SlabCollapse).
    //   - SlabPeel: the order of work (farthest from the stairs first, the worker on the cell one step nearer); automatic
    //     work only, a forced order skips it.
    //   - SlabCollapse: the roof under a slab collapses (vanilla support rules), the slab falls with everything on it.
    public static class Slab
    {
        // An upper floor cell with a slab (bare, or under a floor laid on it).
        public static bool At(IntVec3 c, Map map)
        {
            if (map == null || !c.IsValid || !c.InBounds(map) || ProjectZ.FloorOf(c, map) < 1)
            {
                return false;
            }
            TerrainGrid tg = map.terrainGrid;
            return SlabDefs.Is(tg.TopTerrainAt(c)) || SlabDefs.Is(tg.UnderTerrainAt(c));
        }

        // Why a slab can't be taken up now (null: it can). Built things on it (the vanilla foundation rule, any building,
        // blueprint or frame), the landing of stairs (part of the stairs: it goes with them). A slab right above does not
        // block (Q6 (b), user 2026-11-01): the floor above keeps resting on the roof over the new hole, held by the game's
        // roof support (SlabCollapse when that support goes).
        public static string Blocker(IntVec3 c, Map map)
        {
            List<Thing> list = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is Building || list[i] is Blueprint || list[i] is Frame)
                {
                    return "MessageCannotRemoveSupportingFoundation".Translate();
                }
            }
            if (StairwellFloor.IsStairsExit(c, map))
            {
                return "PZ_StairsLandHere".Translate();
            }
            return null;
        }

        // Takes the slab (and any floor still laid on it) down to open air: the roof below becomes a plain roof again
        // (FloorRoof), no materials come back (user 2026-10-18), pawns and items on it fall to the floor below (gravity,
        // MoveOff), the zone cell and the removal designation go.
        public static void Remove(IntVec3 c, Map map, bool doLeavings, Thing cause = null)
        {
            TerrainGrid tg = map.terrainGrid;
            TerrainDef top = tg.TopTerrainAt(c);
            TerrainDef under = tg.UnderTerrainAt(c);
            DropZoneCell(c, map);
            tg.SetTerrain(c, PZDefOf.PZ_OpenAir);
            // (user 2026-10-18: taking up a slab returns no materials on any path, doLeavings or not, a floor laid on it
            // included: nothing falls from a removal, no roof below breaks; the slab and deck descriptions say so)
            map.designationManager.DesignationAt(c, DesignationDefOf.RemoveFoundation)?.Delete();
            MoveOff(c, map, cause);
            ProjectZ.DebugLog($"slab at {c} removed ({top.defName}{(under != null ? " on " + under.defName : "")})");
        }

        public static void DropZoneCell(IntVec3 c, Map map)
        {
            Zone zone = map.zoneManager.ZoneAt(c);
            if (zone != null)
            {
                zone.RemoveCell(c);
                if (zone.cells.Count > 0)
                {
                    zone.CheckContiguous();
                }
            }
        }

        // Pawns and items on a cell whose floor was just taken up (a slab or deck removed, a stairs landing going with its
        // stairs): they FALL to the floor below by the gravity rule (compat G2, user 2026-10-18, reversing Q7's "step off,
        // no fall"): Column.Landing, roofs passed break, pawns bruised from one floor up; a thick roof under it: the nearest
        // cell on the same floor. (The worker taking a floor up stands on the next cell: SlabPeel / ShipDeck work spot.)
        // With gravity switched off (-projectznogravity, or inside its Suspend scope) the old rule: the nearest cell on
        // the same floor, else on the floors below.
        public static void MoveOff(IntVec3 c, Map map, Thing cause = null)
        {
            bool fall = Gravity.On && Gravity.Air(c, map);
            List<Thing> list = c.GetThingList(map);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Thing t = list[i];
                if (t is Pawn p)
                {
                    // The pawn taking the floor up keeps its job (it is finishing), as does one taking down the thing
                    // that takes this floor with it (the stairs and their landing); anyone else starts again.
                    bool remover = p.CurJob != null && ((p.CurJob.def == JobDefOf.RemoveFoundation || p.CurJob.def == JobDefOf.RemoveFloor) && p.CurJob.targetA.Cell == c || cause != null && p.CurJob.targetA.Thing == cause);
                    if (fall)
                    {
                        Gravity.FallPawn(p, c, remover);
                        continue;
                    }
                    IntVec3 to = NearestStandable(c, map, ProjectZ.FloorOf(c, map), true);
                    if (to.IsValid)
                    {
                        p.Position = to;
                        p.Notify_Teleported(endCurrentJob: !remover, resetTweenedPos: false);
                    }
                }
                else if (t.def.category == ThingCategory.Item)
                {
                    if (fall)
                    {
                        // placed again where it is: the gravity hook moves it to its landing
                        t.DeSpawn();
                        if (!GenPlace.TryPlaceThing(t, c, map, ThingPlaceMode.Near))
                        {
                            PlaceDown(t, c, map, ProjectZ.FloorOf(c, map));
                        }
                        continue;
                    }
                    PlaceDown(t, c, map, ProjectZ.FloorOf(c, map));
                }
            }
        }

        // The nearest standable cell to c on floor k (near first, then the whole floor), else on the floors below.
        public static IntVec3 NearestStandable(IntVec3 c, Map map, int k, bool downToo)
        {
            IntVec3 g = ProjectZ.GroundCellOf(c, map);
            for (int j = k; j >= 0; j--)
            {
                IntVec3 cj = ProjectZ.CellOnFloor(g, j, map);
                if (!cj.IsValid)
                {
                    continue;
                }
                int n = GenRadial.RadialPattern.Length;
                for (int r = j == k ? 1 : 0; r < n; r++)
                {
                    IntVec3 x = cj + GenRadial.RadialPattern[r];
                    if (x.InBounds(map) && ProjectZ.FloorOf(x, map) == j && x.Standable(map))
                    {
                        return x;
                    }
                }
                if (!downToo)
                {
                    break;
                }
            }
            return IntVec3.Invalid;
        }

        // An item put back down near c on floor k, else on the floors below.
        public static void PlaceDown(Thing t, IntVec3 c, Map map, int k)
        {
            if (t.Spawned)
            {
                t.DeSpawn();
            }
            IntVec3 g = ProjectZ.GroundCellOf(c, map);
            for (int j = k; j >= 0; j--)
            {
                IntVec3 cj = ProjectZ.CellOnFloor(g, j, map);
                if (cj.IsValid && GenPlace.TryPlaceThing(t, cj, map, ThingPlaceMode.Near))
                {
                    return;
                }
            }
            GenSpawn.Spawn(t, g, map);
        }
    }

    // The cell under an upper floor cell: could a roof (so a slab) cover it? Support follows the game's roofs (§7).
    public static class SlabSupport
    {
        // What the cell under a slab must allow besides support: in the map, not a stairwell, nothing too tall there.
        // This is all a stairs landing needs (the stairs hold its roof).
        public static AcceptanceReport CoverOk(IntVec3 below, Map map)
        {
            if (!below.IsValid || !below.InBounds(map))
            {
                return "PZ_NoExitCell".Translate();
            }
            string blocked = SlabColumnBlocked(below, map);
            if (blocked != null)
            {
                return blocked.Translate();
            }
            if (Stairwell.StairThingAt(below, map))
            {
                return "PZ_StairwellBelow".Translate();
            }
            if (LadderColumn.Plane(below, map))
            {
                return LadderColumn.Refusal(below, map, true);
            }
            Thing tall = HeightRule.TallThingAt(below, map);
            if (tall != null)
            {
                return "PZ_TallBelow".Translate(tall.LabelShort);
            }
            return AcceptanceReport.WasAccepted;
        }

        // A thick (rock) roof is never replaced by a floor's or a stairwell's roof: no slab over it, no stairs under it
        // (user decision 2026-10-16; otherwise a mountain roof could be swapped for a thin, collapsible one).
        public static bool ThickRoof(IntVec3 c, Map map) => c.IsValid && c.InBounds(map) && (map.roofGrid.RoofAt(c)?.isThickRoof ?? false);

        // No upper floor over a gravship, no stairs on one (user decision 2026-10-16): a launch takes only the ground floor
        // on the substructure and would leave the floor above behind. (Two-storey gravships: backlog.) Any gravship deck
        // counts (DeckFoundation: the tag or the Substructure affordance, so another faction's ship deck too).
        public static bool Gravship(IntVec3 c, Map map) => c.IsValid && c.InBounds(map) && DeckFoundation.Is(map.terrainGrid.FoundationAt(c));

        // Mountains foundation F4: "may build" and "may use" are two rules.
        //  - BUILD (BuildBlocked and its slab and stairs forms): a slab, a stairwell or a stairs landing to be made over
        //    a cell with a thick roof (or on a gravship) is refused: their roof would replace the rock roof (user
        //    decision 2026-10-16). Placement and construction ask (CoverOk, ThickRoofCheck, CanConstruct, ShipDeck,
        //    ShipStairs).
        //  - USE (LandingBlocked): a floor that is already there is used as it is (a mountain top over its thick or thin
        //    rock roof, a slab over its floor roof), so stairs may land on it. Drops and jumps only ask the landing cell's
        //    own roof (Column.OpenAbove, DropRules), never the column under it, so they already use any floor.
        //
        // Thick roof or gravship under a cell: the reason no floor or stairs may be built over it, or null. The ship is its
        // column (ShipColumn: a ship stairs opening too; over it only ship decks and ship stairs go).
        public static string BuildBlocked(IntVec3 c, Map map)
        {
            if (ThickRoof(c, map))
            {
                return "PZ_ThickRoof";
            }
            return ShipColumn.Cell(c, map) ? "PZ_OverGravship" : null;
        }

        // The same for a slab (or a stairs landing): over a gravship it points to the ship upper deck (Odyssey).
        public static string SlabColumnBlocked(IntVec3 c, Map map)
        {
            string key = BuildBlocked(c, map);
            return key == "PZ_OverGravship" && PZDefOf.PZ_ShipDeck != null ? "PZ_OverGravshipUseDeck" : key;
        }

        // The same for ordinary stairs: on (or landing over) a gravship it points to the ship stairs (Odyssey).
        public static string StairsColumnBlocked(IntVec3 c, Map map)
        {
            string key = BuildBlocked(c, map);
            return key == "PZ_OverGravship" && PZDefOf.PZ_ShipStairs != null ? "PZ_OverGravshipUseStairs" : key;
        }

        // USE: why ordinary stairs may not land on upper cell top (its column), or null. A floor already there (natural
        // or built) is used as it is: nothing is laid, so its roof stays and only the gravship rule applies; open air (the
        // landing is made with the stairs) keeps the whole build rule for the cell under it.
        public static string LandingBlocked(IntVec3 top, Map map)
        {
            string key = StairsColumnBlocked(FloorRoof.Below(top, map), map);
            return key == "PZ_ThickRoof" && ProjectZ.HasFloor(top, map) ? null : key;
        }

        private static readonly Queue<IntVec3> open = new Queue<IntVec3>();
        private static readonly HashSet<IntVec3> seen = new HashSet<IntVec3>();

        // The game's "a roof could be held here" (RoofCollapseUtility.WithinRangeOfRoofHolder, as JobDriver_BuildRoof
        // checks it): a roof holder (wall, column, stairs) within 6.9 cells, reached over roofed cells. For planning,
        // cells with a slab planned above count as roofed (a floor dragged out from a wall), also the cells of the drag in
        // progress (SlabDrag); 'ignore' is a holder about to go (stairs being taken down).
        public static bool Supported(IntVec3 root, Map map, Thing ignore = null, bool countPlanned = false)
        {
            if (!root.IsValid || !root.InBounds(map))
            {
                return false;
            }
            if (countPlanned && SlabDrag.Sorting)
            {
                return true;
            }
            // (before the walk: sorting the drag's cells asks this method again)
            HashSet<IntVec3> drag = countPlanned ? SlabDrag.Cells(map) : null;
            if (drag != null)
            {
                if (ignore == null && SlabDrag.Answer(root, out bool known))
                {
                    return known;
                }
                if (SlabDrag.NoHolderNear(root))
                {
                    return false;
                }
            }
            open.Clear();
            seen.Clear();
            open.Enqueue(root);
            seen.Add(root);
            bool found = false;
            while (open.Count > 0 && !found)
            {
                IntVec3 x = open.Dequeue();
                for (int i = 0; i < 5; i++)
                {
                    IntVec3 h = x + GenAdj.CardinalDirectionsAndInside[i];
                    if (h.InBounds(map) && h.InHorDistOf(root, RoofCollapseUtility.RoofMaxSupportDistance))
                    {
                        Building e = h.GetEdifice(map);
                        if (e != null && e != ignore && e.def.holdsRoof)
                        {
                            found = true;
                            break;
                        }
                    }
                }
                for (int i = 0; i < 4; i++)
                {
                    IntVec3 n = x + GenAdj.CardinalDirections[i];
                    if (n.InBounds(map) && !seen.Contains(n) && n.InHorDistOf(root, RoofCollapseUtility.RoofMaxSupportDistance)
                        && (map.roofGrid.Roofed(n) || countPlanned && PlannedAbove(n, map, drag)))
                    {
                        seen.Add(n);
                        open.Enqueue(n);
                    }
                }
            }
            open.Clear();
            seen.Clear();
            if (drag != null && ignore == null)
            {
                SlabDrag.Remember(root, found);
            }
            return found;
        }

        private static bool PlannedAbove(IntVec3 n, Map map, HashSet<IntVec3> drag)
        {
            IntVec3 up = StairShape.Above(n, map);
            return drag != null && drag.Contains(up) || PlaceWorker_Stairs.FloorPlanned(up, map) || ShipDeck.Planned(up, map);
        }
    }

    // The slabs of the drag in progress (user 2026-11-05: one drag lays the whole area a holder reaches). The game checks
    // every dragged cell before it places any, so without this only the ring next to a holder, a roof or an older
    // blueprint passed and the player dragged again ring by ring. For the support walk the drag's cells count as planned.
    // Only while the player drags a slab or a ship deck on this map; the 6.9 cell limit and the builders' rule (actual
    // support, CanConstruct) stay: pawns still build outward from the holders.
    //   - Cells: the dragger's raw cell buffer. DragCells is no use here: it is filtered through the very check that asks.
    //     Every dragger path refreshes the buffer before it asks a cell, so it is current; on the viewed upper floor, the
    //     same cells StairShape.Above gives for the walk's cells below.
    //   - Only cells a slab could really go on count, or a drag would reach over a stairwell or a tall building to cells
    //     no builder could ever get to. That is the designator's own check with the support part answered yes (Sorting).
    //   - Sorted once per drag shape, not per cell. With it the holders in reach of the drag are marked: a cell far from any
    //     would otherwise walk its whole 6.9 circle of dragged cells before it is refused.
    //   - While the drag does not change, the sorted cells, the mark and each cell's support answer are kept (M163): the game
    //     asks every dragged cell every frame. They are renewed every RefreshFrames, so what is built or removed during an
    //     unpaused drag shows in the preview. "Does not change" is the whole cell list, compared once a frame: count and
    //     ends alone can match for two shapes (two ovals, an angled line drawn the other way).
    //   - The release never uses them: the game's mouse-up takes DragCells, which DrawPanelReadout may already have
    //     filled this frame, then asks each cell again. Release drops everything first, the dragger's list too.
    public static class SlabDrag
    {
        // (test switch: the old rule, for comparison runs)
        public static bool Off;

        // The drag's cells are being sorted: Supported answers yes, so only the other rules decide.
        public static bool Sorting { get; private set; }

        // How long the kept answers last, in frames (the tests hold them longer to prove the release).
        public static int RefreshFrames = 30;

        private static readonly AccessTools.FieldRef<DesignationDragger, int> draggerFrame = AccessTools.FieldRefAccess<DesignationDragger, int>("lastUpdateFrame");
        private static readonly HashSet<IntVec3> cells = new HashSet<IntVec3>();
        private static readonly List<IntVec3> shape = new List<IntVec3>();
        private static readonly Dictionary<IntVec3, bool> answers = new Dictionary<IntVec3, bool>();
        private static int sortedFrame, comparedFrame = -1;
        private static Map keptMap;
        private static Designator designator;
        private static DrawStyleDef style;
        private static bool[] near = new bool[0];
        private static int nearX, nearZ, nearW, nearH;

        // The kept support answer for a cell under the drag.
        public static bool Answer(IntVec3 below, out bool supported) => answers.TryGetValue(below, out supported);

        public static void Remember(IntVec3 below, bool supported) => answers[below] = supported;

        // The drag ended: nothing is kept for the next one.
        public static void Clear()
        {
            keptMap = null;
            designator = null;
            style = null;
            shape.Clear();
            cells.Clear();
            answers.Clear();
            nearW = nearH = 0;
            comparedFrame = -1;
        }

        // The mouse-up of a slab drag: what it asks next is answered from the map as it is now.
        public static void Release(DesignationDragger dragger)
        {
            if (designator != null)
            {
                Clear();
                draggerFrame(dragger) = -1;
            }
        }

        // A cell under the drag with no holder within 6.9 cells (no walk can find one).
        public static bool NoHolderNear(IntVec3 below)
        {
            int x = below.x - nearX, z = below.z - nearZ;
            return x >= 0 && z >= 0 && x < nearW && z < nearH && !near[z * nearW + x];
        }

        // The upper-floor cells of the slab drag in progress on this map; null when there is none.
        public static HashSet<IntVec3> Cells(Map map)
        {
            if (Off || !(Find.UIRoot is UIRoot_Play))
            {
                return null;
            }
            DesignatorManager dm = Find.DesignatorManager;
            if (!dm.Dragger.Dragging || !(dm.SelectedDesignator is Designator_Build d) || d.Map != map || !(SlabDefs.Is(d.PlacingDef) || ShipDeck.Is(d.PlacingDef)))
            {
                return null;
            }
            List<IntVec3> buffer = dm.Dragger.CellBuffer;
            if (buffer.Count == 0)
            {
                return null;
            }
            int now = UnityEngine.Time.frameCount;
            if (keptMap != map || designator != d || style != dm.SelectedStyle || now - sortedFrame >= RefreshFrames || !SameShape(buffer, now))
            {
                keptMap = map;
                designator = d;
                style = dm.SelectedStyle;
                sortedFrame = comparedFrame = now;
                shape.Clear();
                shape.AddRange(buffer);
                answers.Clear();
                Sort(d, buffer);
                MarkHolders(map);
            }
            return cells;
        }

        // This is asked for every cell: the whole list once a frame (the mouse moves between frames), else its ends.
        private static bool SameShape(List<IntVec3> buffer, int now)
        {
            if (shape.Count != buffer.Count || shape[0] != buffer[0] || shape[shape.Count - 1] != buffer[buffer.Count - 1])
            {
                return false;
            }
            if (comparedFrame == now)
            {
                return true;
            }
            comparedFrame = now;
            for (int i = 0; i < buffer.Count; i++)
            {
                if (shape[i] != buffer[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static void Sort(Designator d, List<IntVec3> buffer)
        {
            cells.Clear();
            Sorting = true;
            try
            {
                for (int i = 0; i < buffer.Count; i++)
                {
                    if (d.CanDesignateCell(buffer[i]).Accepted)
                    {
                        cells.Add(buffer[i]);
                    }
                }
            }
            finally
            {
                Sorting = false;
            }
        }

        private static void MarkHolders(Map map)
        {
            nearW = nearH = 0;
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach (IntVec3 c in cells)
            {
                IntVec3 b = FloorRoof.Below(c, map);
                if (b.IsValid)
                {
                    minX = Math.Min(minX, b.x);
                    minZ = Math.Min(minZ, b.z);
                    maxX = Math.Max(maxX, b.x);
                    maxZ = Math.Max(maxZ, b.z);
                }
            }
            if (minX > maxX)
            {
                return;
            }
            nearX = minX;
            nearZ = minZ;
            nearW = maxX - minX + 1;
            nearH = maxZ - minZ + 1;
            if (near.Length < nearW * nearH)
            {
                near = new bool[nearW * nearH];
            }
            Array.Clear(near, 0, nearW * nearH);
            float reach = RoofCollapseUtility.RoofMaxSupportDistance;
            int pad = (int)reach + 1;
            int pattern = GenRadial.NumCellsInRadius(reach);
            for (int z = minZ - pad; z <= maxZ + pad; z++)
            {
                for (int x = minX - pad; x <= maxX + pad; x++)
                {
                    IntVec3 h = new IntVec3(x, 0, z);
                    Building e = h.InBounds(map) ? h.GetEdifice(map) : null;
                    if (e == null || !e.def.holdsRoof)
                    {
                        continue;
                    }
                    for (int i = 0; i < pattern; i++)
                    {
                        IntVec3 o = GenRadial.RadialPattern[i];
                        int ox = x + o.x - nearX, oz = z + o.z - nearZ;
                        if (ox >= 0 && oz >= 0 && ox < nearW && oz < nearH)
                        {
                            near[oz * nearW + ox] = true;
                        }
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(DesignationDragger), nameof(DesignationDragger.EndDrag))]
    public static class EndDrag_SlabDrag_Patch
    {
        public static void Postfix() => SlabDrag.Clear();
    }

    // (the only place a drag is released: the left mouse-up in the game's designator input)
    [HarmonyPatch(typeof(DesignatorManager), nameof(DesignatorManager.ProcessInputEvents))]
    public static class ProcessInputEvents_SlabDrag_Patch
    {
        public static void Prefix(DesignatorManager __instance)
        {
            UnityEngine.Event e = UnityEngine.Event.current;
            if (e != null && e.type == UnityEngine.EventType.MouseUp && e.button == 0 && __instance.Dragger.Dragging)
            {
                SlabDrag.Release(__instance.Dragger);
            }
        }
    }

    // The peel order (§7): per upper floor, the walking distance from the stairs landings over slab cells (4 ways, so
    // every step is one a pawn can walk). Only the designated slabs farthest away are worked (same distance in
    // parallel), from the cell one step nearer the stairs. A slab's shortest way to the stairs only passes nearer cells,
    // so taking a farthest one up cuts no other designated slab off. Designated slabs no stairs reach are never worked.
    // The order is for automatic work only: a forced order (the player's right-click) skips it and the stairs-exit
    // cut-off check (K2), user 2026-11-02; what that cuts off is the player's. Its worker stands on the cell one step
    // nearer the stairs, else on a cell next to it (WorkSpot).
    // The distances are rebuilt only after slabs, stairs or walkability changed; the farthest designated distance is
    // read again at most once a tick (designations and blockers change it).
    public class SlabPeel : MapComponent
    {
        private int[] dist;
        private int[] parent;
        private int[] maxDist;
        private int maxLowest;
        private bool dirty = true;
        private bool listening;
        private int maxTick = -1;

        public SlabPeel(Map map) : base(map)
        {
        }

        private static Map lastMap;
        private static SlabPeel last;

        public static SlabPeel For(Map map)
        {
            if (map == null || ProjectZ.Annex(map) == null)
            {
                return null;
            }
            if (!ReferenceEquals(map, lastMap))
            {
                last = map.components == null ? null : map.GetComponent<SlabPeel>();
                lastMap = map;
            }
            return last;
        }

        public void Dirty()
        {
            dirty = true;
            maxTick = -1;
        }

        public static void Dirty(Map map) => For(map)?.Dirty();

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            Listen();
        }

        private void Listen()
        {
            if (!listening && map.events != null)
            {
                listening = true;
                // (a wall built or taken down on a slab changes where pawns walk)
                map.events.RegionsRoomsChanged += Dirty;
            }
        }

        private void Build()
        {
            Listen();
            dirty = false;
            maxTick = -1;
            int n = map.cellIndices.NumGridCells;
            if (dist == null || dist.Length != n)
            {
                dist = new int[n];
                parent = new int[n];
            }
            for (int i = 0; i < n; i++)
            {
                dist[i] = -1;
            }
            Queue<IntVec3> q = new Queue<IntVec3>();
            StairGrid g = StairGrid.For(map);
            if (g != null)
            {
                foreach (VLink l in g.links)
                {
                    IntVec3 top = l.top;
                    if (top.InBounds(map) && ProjectZ.HasFloor(top, map) && top.Walkable(map) && dist[map.cellIndices.CellToIndex(top)] < 0)
                    {
                        int ti = map.cellIndices.CellToIndex(top);
                        dist[ti] = 0;
                        parent[ti] = -1;
                        q.Enqueue(top);
                    }
                }
            }
            while (q.Count > 0)
            {
                IntVec3 c = q.Dequeue();
                int ci = map.cellIndices.CellToIndex(c);
                int k = ProjectZ.FloorOf(c, map);
                for (int d = 0; d < 4; d++)
                {
                    IntVec3 x = c + GenAdj.CardinalDirections[d];
                    if (!x.InBounds(map) || ProjectZ.FloorOf(x, map) != k)
                    {
                        continue;
                    }
                    int xi = map.cellIndices.CellToIndex(x);
                    if (dist[xi] >= 0 || !ProjectZ.HasFloor(x, map) || !x.Walkable(map))
                    {
                        continue;
                    }
                    dist[xi] = dist[ci] + 1;
                    parent[xi] = ci;
                    q.Enqueue(x);
                }
            }
        }

        private void Ensure()
        {
            if (dirty || dist == null)
            {
                Build();
            }
            int now = Find.TickManager?.TicksGame ?? 0;
            if (maxTick == now && maxDist != null)
            {
                return;
            }
            maxTick = now;
            AnnexMapComponent a = ProjectZ.Annex(map);
            // (per level, index level - the lowest level)
            int lowest = a.MinLevel, levels = a.MaxLevel - a.MinLevel + 1;
            if (maxDist == null || maxDist.Length != levels || maxLowest != lowest)
            {
                maxDist = new int[levels];
                maxLowest = lowest;
            }
            for (int k = 0; k < maxDist.Length; k++)
            {
                maxDist[k] = -1;
            }
            foreach (Designation des in map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.RemoveFoundation))
            {
                IntVec3 c = des.target.Cell;
                if (!Slab.At(c, map))
                {
                    continue;
                }
                int d = dist[map.cellIndices.CellToIndex(c)];
                int k = ProjectZ.FloorOf(c, map) - lowest;
                if (d > maxDist[k] && Slab.Blocker(c, map) == null)
                {
                    maxDist[k] = d;
                }
            }
        }

        // The designated slab at c may be worked now: reached from the stairs, and among the farthest on its floor.
        public bool Workable(IntVec3 c)
        {
            Ensure();
            int d = dist[map.cellIndices.CellToIndex(c)];
            return d > 0 && d == maxDist[ProjectZ.FloorOf(c, map) - maxLowest];
        }

        // Where the worker stands: the cell one step nearer the stairs. Invalid when the slab isn't reached.
        public IntVec3 Parent(IntVec3 c)
        {
            Ensure();
            int i = map.cellIndices.CellToIndex(c);
            return dist[i] > 0 ? map.cellIndices.IndexToCell(parent[i]) : IntVec3.Invalid;
        }

        public int Distance(IntVec3 c)
        {
            Ensure();
            return dist[map.cellIndices.CellToIndex(c)];
        }

        // Where the worker of a forced order stands: the cell one step nearer the stairs, else a cell next to it the
        // pawn can stand on and reach (as for a deck, never c itself). Invalid: none.
        public static IntVec3 WorkSpot(Pawn pawn, IntVec3 c)
        {
            IntVec3 from = For(pawn.Map)?.Parent(c) ?? IntVec3.Invalid;
            return from.IsValid ? from : ShipDeck.WorkSpot(pawn, c);
        }
    }

    // A slab falls when the roof under it collapses (§7, replacing "a floor's roof never collapses"); so does a natural
    // rock floor over a thin rock roof or a support (floor) roof (rock-top floors: FloorRoof's DropRoofCellList patch), on this
    // same path (the gravity hooks suspended, the landing Column.Landing). The roof under a
    // slab (PZ_FloorRoof) follows the game's support rules like any built roof, so a slab goes on the game's own collapse
    // paths: the collapse buffer (a support taken away: RoofCollapseBufferResolver -> DropRoofInCells) and the silent
    // removal of unsupported roofs (no-roof work, tornadoes, gravship landings: RoofCollapseCellsFinder in removal
    // mode). Collapse:
    //   - buildings, blueprints and frames on the slab are destroyed (the game's leavings for destroyed things);
    //   - the slab (and a floor laid on it) is gone; pawns, animals and items on it fall to the first floor below that
    //     holds them: fall bruises as from a jump (JumpGrid.Landed), and, landing in the collapse, the game's own
    //     crush damage like everyone under it (the roof under the slab is a plain roof by then and drops as one);
    //   - walls on the slab that held roofs above go too, so floors above lose their support in turn (the game's cascade).
    // Drop pods and tornadoes never break a slab, nor does a shell drop the roof under one (Q11: FloorRoof.Kept outside the
    // buffer); explosions break slabs only through heavy hits (SlabDamage), which end here too.
    // Inside the game's collapse finder (removal mode, or the buffer resolved from there) nothing may be destroyed (its
    // flood fills share static lists): the slab is queued and falls when the finder returns.
    public static class SlabCollapse
    {
        private static int resolving;
        private static int finding;
        private static int removing;
        private static readonly List<KeyValuePair<Map, IntVec3>> queued = new List<KeyValuePair<Map, IntVec3>>();

        public static bool Resolving => resolving > 0;

        // The roof at c (under a slab) is coming down on the game's collapse buffer.
        public static void BufferCollapse(IntVec3 c, Map map, List<Thing> crushed)
        {
            if (finding > 0)
            {
                Queue(c, map);
            }
            else
            {
                Collapse(c, map, crushed);
            }
        }

        // "No roof" written under a slab: only the game's removal of unsupported roofs gets through (then the slab falls).
        public static bool LetRoofGo(IntVec3 c, Map map)
        {
            if (IsQueued(c, map))
            {
                return true;
            }
            if (removing > 0)
            {
                Queue(c, map);
                return true;
            }
            return false;
        }

        private static void Queue(IntVec3 c, Map map)
        {
            if (!IsQueued(c, map))
            {
                queued.Add(new KeyValuePair<Map, IntVec3>(map, c));
            }
        }

        private static bool IsQueued(IntVec3 c, Map map)
        {
            for (int i = 0; i < queued.Count; i++)
            {
                if (queued[i].Value == c && queued[i].Key == map)
                {
                    return true;
                }
            }
            return false;
        }

        public static void FinderEnter(bool removalMode)
        {
            finding++;
            if (removalMode)
            {
                removing++;
            }
        }

        public static void FinderExit(bool removalMode)
        {
            finding--;
            if (removalMode)
            {
                removing--;
            }
            if (finding > 0 || queued.Count == 0)
            {
                return;
            }
            List<KeyValuePair<Map, IntVec3>> now = new List<KeyValuePair<Map, IntVec3>>(queued);
            queued.Clear();
            foreach (KeyValuePair<Map, IntVec3> q in now)
            {
                if (q.Key != null && Find.Maps.Contains(q.Key))
                {
                    Collapse(q.Value, q.Key, null);
                }
            }
        }

        public static void ResolverEnter() => resolving++;

        public static void ResolverExit() => resolving--;

        // The slab over the roof cell 'below' falls (its own fall: outside the gravity hooks, Gravity.Suspend).
        public static void Collapse(IntVec3 below, Map map, List<Thing> crushed)
        {
            Gravity.Suspend();
            try
            {
                CollapseInner(below, map, crushed);
            }
            finally
            {
                Gravity.Resume();
            }
        }

        private static void CollapseInner(IntVec3 below, Map map, List<Thing> crushed)
        {
            IntVec3 u = StairShape.Above(below, map);
            if (!u.IsValid || !u.InBounds(map) || !ProjectZ.HasFloor(u, map))
            {
                return;
            }
            // IsoRim's collapse debris (visual): the slab and what stands on it, taken while still whole.
            IsoRimBridge.Debris(map, u);
            List<Thing> list = u.GetThingList(map);
            List<Thing> built = new List<Thing>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is Building || list[i] is Blueprint || list[i] is Frame)
                {
                    built.Add(list[i]);
                }
            }
            foreach (Thing t in built)
            {
                if (t.Destroyed || !t.def.destroyable)
                {
                    continue;
                }
                if (t is Blueprint)
                {
                    t.Destroy(DestroyMode.Cancel);
                    continue;
                }
                if (crushed != null && !crushed.Contains(t))
                {
                    crushed.Add(t);
                }
                t.Kill(new DamageInfo(DamageDefOf.Crush, 99999f, 999f, -1f, null, null, null, DamageInfo.SourceCategory.Collapse));
            }
            // (the leavings of what was destroyed fall with the rest)
            List<Thing> falling = new List<Thing>();
            list = u.GetThingList(map);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is Pawn || list[i].def.category == ThingCategory.Item)
                {
                    falling.Add(list[i]);
                }
            }
            Slab.DropZoneCell(u, map);
            map.designationManager.DesignationAt(u, DesignationDefOf.RemoveFoundation)?.Delete();
            map.terrainGrid.SetTerrain(u, PZDefOf.PZ_OpenAir);
            FleckMaker.ThrowDustPuff(u.ToVector3Shifted(), map, 2f);
            IntVec3 land = FallTo(u, map, out int fallen);
            foreach (Thing t in falling)
            {
                if (t.Destroyed || !t.Spawned)
                {
                    continue;
                }
                if (t is Pawn p)
                {
                    IntVec3 to = land.IsValid ? land : Slab.NearestStandable(u, map, ProjectZ.FloorOf(u, map), true);
                    if (!to.IsValid)
                    {
                        continue;
                    }
                    p.Position = to;
                    p.Notify_Teleported(endCurrentJob: true, resetTweenedPos: true);
                    JumpGrid.For(map)?.Landed(p, System.Math.Max(1, fallen));
                    if (crushed != null && !crushed.Contains(p))
                    {
                        crushed.Add(p);
                    }
                }
                else
                {
                    Slab.PlaceDown(t, land.IsValid ? land : u, map, land.IsValid ? ProjectZ.FloorOf(land, map) : ProjectZ.FloorOf(u, map) - 1);
                }
            }
            // S1 (ship decks): a deck is a foundation, SetTerrain left it; it goes once everything on it has fallen (a
            // no-op for slabs). The decks above it follow (ShipDeck).
            if (map.terrainGrid.FoundationAt(u) != null)
            {
                map.terrainGrid.RemoveFoundation(u, false);
            }
            ProjectZ.DebugLog($"slab at {u} collapsed: {built.Count} built thing(s) destroyed, {falling.Count} fell {fallen} floor(s) to {land}");
            Dev.PZStats.slabCollapses++;
        }

        // Where things falling from upper floor cell u come down: the first floor below (ground, slab, deck or mountain top),
        // on that cell or the nearest standable one on that floor.
        // (Column.Landing, the gravity rule's walk; the collapse's roofs are the game's collapse buffer's, so none broken here)
        public static IntVec3 FallTo(IntVec3 u, Map map, out int fallen)
        {
            return Column.Landing(u, map, true, false, out fallen, out _);
        }
    }

    [HarmonyPatch(typeof(RoofCollapseBufferResolver), nameof(RoofCollapseBufferResolver.CollapseRoofsMarkedToCollapse))]
    public static class CollapseResolver_Slab_Patch
    {
        public static void Prefix() => SlabCollapse.ResolverEnter();

        public static void Finalizer() => SlabCollapse.ResolverExit();
    }

    // The entry points of the game's collapse finder (Notify_RoofHolderDespawned, CheckAndRemoveCollpsingRoofs and
    // RemoveBulkCollapsingRoofs call ProcessRoofHolderDespawned; the no-roof job and tornadoes call
    // CheckCollapseFlyingRoofs).
    [HarmonyPatch(typeof(RoofCollapseCellsFinder), nameof(RoofCollapseCellsFinder.ProcessRoofHolderDespawned))]
    public static class CollapseFinderHolder_Slab_Patch
    {
        public static void Prefix(bool removalMode) => SlabCollapse.FinderEnter(removalMode);

        public static void Finalizer(bool removalMode) => SlabCollapse.FinderExit(removalMode);
    }

    [HarmonyPatch(typeof(RoofCollapseCellsFinder), nameof(RoofCollapseCellsFinder.CheckCollapseFlyingRoofs), new[] { typeof(List<IntVec3>), typeof(Map), typeof(bool), typeof(bool) })]
    public static class CollapseFinderFlyingList_Slab_Patch
    {
        public static void Prefix(bool removalMode) => SlabCollapse.FinderEnter(removalMode);

        public static void Finalizer(bool removalMode) => SlabCollapse.FinderExit(removalMode);
    }

    [HarmonyPatch(typeof(RoofCollapseCellsFinder), nameof(RoofCollapseCellsFinder.CheckCollapseFlyingRoofs), new[] { typeof(CellRect), typeof(Map), typeof(bool), typeof(bool) })]
    public static class CollapseFinderFlyingRect_Slab_Patch
    {
        public static void Prefix(bool removalMode) => SlabCollapse.FinderEnter(removalMode);

        public static void Finalizer(bool removalMode) => SlabCollapse.FinderExit(removalMode);
    }

    // ---- the vanilla remove-foundation tool, for slabs

    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.CanRemoveFoundationAt))]
    public static class CanRemoveFoundation_Slab_Patch
    {
        private static readonly AccessTools.FieldRef<TerrainGrid, Map> mapRef = AccessTools.FieldRefAccess<TerrainGrid, Map>("map");

        public static void Postfix(TerrainGrid __instance, IntVec3 c, ref bool __result)
        {
            if (!__result && (Slab.At(c, mapRef(__instance)) || TopRemoval.Can(c, mapRef(__instance))))
            {
                __result = true;
            }
        }
    }

    // (vanilla would read the empty foundation grid here)
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.RemoveFoundation))]
    public static class RemoveFoundation_Slab_Patch
    {
        private static readonly AccessTools.FieldRef<TerrainGrid, Map> mapRef = AccessTools.FieldRefAccess<TerrainGrid, Map>("map");

        public static bool Prefix(TerrainGrid __instance, IntVec3 c, bool doLeavings)
        {
            Map map = mapRef(__instance);
            if (!Slab.At(c, map))
            {
                if (TopRemoval.Can(c, map))
                {
                    TopRemoval.Open(c, map, null);   // (god mode; the job opens it with its worker: RemoveFoundationEffect_Top_Patch)
                    return false;
                }
                return true;
            }
            Slab.Remove(c, map, doLeavings);
            return false;
        }
    }

    // The designator, the work giver and its jobs ask this.
    [HarmonyPatch(typeof(WorkGiver_ConstructRemoveFoundation), nameof(WorkGiver_ConstructRemoveFoundation.AnyBuildingBlockingFoundationRemoval))]
    public static class FoundationRemovalBlocked_Slab_Patch
    {
        public static bool Prefix(IntVec3 c, Map map, ref bool __result)
        {
            if (!Slab.At(c, map))
            {
                return true;
            }
            __result = Slab.Blocker(c, map) != null;
            return false;
        }
    }

    // The job's end on a natural top: it opens with its worker as the cause (TopRemoval, MineUp.cs).
    [HarmonyPatch(typeof(JobDriver_RemoveFoundation), "DoEffect")]
    public static class RemoveFoundationEffect_Top_Patch
    {
        public static bool Prefix(JobDriver_RemoveFoundation __instance, IntVec3 c)
        {
            Map map = __instance.pawn?.Map;
            if (map == null || !TopRemoval.Can(c, map))
            {
                return true;
            }
            TopRemoval.Open(c, map, __instance.pawn);
            return false;
        }
    }

    // The refusal says which blocker it is (vanilla's message covers only buildings); on a natural rock top, why it can't
    // be opened (TopRemoval.Why).
    [HarmonyPatch(typeof(Designator_RemoveFoundation), nameof(Designator_RemoveFoundation.CanDesignateCell))]
    public static class RemoveFoundationDesignate_Slab_Patch
    {
        public static void Postfix(Designator_RemoveFoundation __instance, IntVec3 c, ref AcceptanceReport __result)
        {
            Map map = __instance.Map;
            if (!__result.Accepted && !__result.Reason.NullOrEmpty() && Slab.At(c, map))
            {
                string why = Slab.Blocker(c, map);
                if (why != null)
                {
                    __result = why;
                }
            }
            else if (!__result.Accepted && c.InBounds(map) && !c.Fogged(map) && map.designationManager.DesignationAt(c, DesignationDefOf.RemoveFoundation) == null)
            {
                string key = TopRemoval.Why(c, map);
                if (!key.NullOrEmpty())
                {
                    __result = key.Translate();
                }
            }
        }
    }

    // Peel order: only the farthest designated slabs have work. A forced order skips it and K2 (user 2026-11-02): only
    // the physical rules stand (vanilla's and Slab.Blocker, already in __result) and a spot to stand on next to the cell.
    [HarmonyPatch(typeof(WorkGiver_ConstructRemoveFoundation), nameof(WorkGiver_ConstructRemoveFoundation.HasJobOnCell))]
    public static class RemoveFoundationHasJob_Slab_Patch
    {
        public static void Postfix(Pawn pawn, IntVec3 c, bool forced, ref bool __result)
        {
            // (a natural top: no peel order, forced or not; a spot to stand on next to it)
            if (__result && !Slab.At(c, pawn.Map) && TopRemoval.Can(c, pawn.Map))
            {
                if (!SlabPeel.WorkSpot(pawn, c).IsValid)
                {
                    JobFailReason.Is("PZ_NoWorkSpot".Translate());
                    __result = false;
                }
                return;
            }
            if (__result && forced && Slab.At(c, pawn.Map))
            {
                if (!SlabPeel.WorkSpot(pawn, c).IsValid)
                {
                    JobFailReason.Is("PZ_NoWorkSpot".Translate());
                    __result = false;
                }
                return;
            }
            if (__result && Slab.At(c, pawn.Map) && SlabPeel.For(pawn.Map)?.Workable(c) != true)
            {
                if (SlabPeel.For(pawn.Map)?.Distance(c) > 0)
                {
                    JobFailReason.Is("PZ_FartherFirst".Translate());
                }
                __result = false;
                return;
            }
            // K2: the floor of a stairs exit, when taking it up cuts the floor above off from the map edge (the worker
            // stands up there, one step nearer other stairs: SlabPeel.Parent)
            if (__result && Slab.At(c, pawn.Map) && StairGrid.For(pawn.Map) != null)
            {
                Patches.RouteGraph g = Patches.RouteGraph.For(pawn.Map);
                int link = g?.LinkOfTop(c) ?? -1;
                if (link >= 0 && g.CutSide(link) > 0)
                {
                    JobFailReason.Is("PZ_WouldCutOff".Translate());
                    __result = false;
                }
            }
        }
    }

    // ... and the worker stands one step nearer the stairs (job target B), for the covering and for the slab (a forced
    // order on a cell the stairs don't reach: a cell next to it, SlabPeel.WorkSpot).
    [HarmonyPatch(typeof(WorkGiver_ConstructRemoveFoundation), nameof(WorkGiver_ConstructRemoveFoundation.JobOnCell))]
    public static class RemoveFoundationJob_Slab_Patch
    {
        public static void Postfix(Pawn pawn, IntVec3 c, bool forced, Job __result)
        {
            if (__result != null && Slab.At(c, pawn.Map))
            {
                IntVec3 from = forced ? SlabPeel.WorkSpot(pawn, c) : SlabPeel.For(pawn.Map)?.Parent(c) ?? IntVec3.Invalid;
                if (from.IsValid)
                {
                    __result.targetB = from;
                }
            }
            else if (__result != null && ShipDeck.Upper(c, pawn.Map))
            {
                // (a deck has no peel order: the worker stands on a next cell, never on the deck it takes up; G2)
                IntVec3 from = ShipDeck.WorkSpot(pawn, c);
                if (from.IsValid)
                {
                    __result.targetB = from;
                }
            }
            else if (__result != null && __result.def == JobDefOf.RemoveFoundation && TopRemoval.Can(c, pawn.Map))
            {
                // (a natural top: the worker stands next to it, never on the top that opens)
                IntVec3 from = SlabPeel.WorkSpot(pawn, c);
                if (from.IsValid)
                {
                    __result.targetB = from;
                }
            }
        }
    }

    // The floor jobs walk to "touch" the cell; for a slab with a work spot (B) they walk onto B instead.
    [HarmonyPatch(typeof(JobDriver_AffectFloor), "MakeNewToils")]
    public static class AffectFloorToils_Slab_Patch
    {
        public static IEnumerable<Toil> Postfix(IEnumerable<Toil> values, JobDriver_AffectFloor __instance)
        {
            Job job = __instance.job;
            bool spot = job != null && job.targetB.IsValid && !job.targetB.HasThing && (Slab.At(job.targetA.Cell, __instance.pawn.Map) || ShipDeck.Upper(job.targetA.Cell, __instance.pawn.Map) || TopRemoval.Can(job.targetA.Cell, __instance.pawn.Map));
            bool first = true;
            foreach (Toil t in values)
            {
                if (first && spot)
                {
                    first = false;
                    yield return Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
                    continue;
                }
                first = false;
                yield return t;
            }
        }
    }

    // Q2: on an upper floor the tool reads "Remove upper floor".
    [HarmonyPatch(typeof(Command), nameof(Command.Label), MethodType.Getter)]
    public static class RemoveFoundationLabel_Slab_Patch
    {
        public static void Postfix(Command __instance, ref string __result)
        {
            if (__instance is Designator_RemoveFoundation && FloorView.Current >= 1 && ProjectZ.Annex(Find.CurrentMap) != null)
            {
                __result = "PZ_RemoveUpperFloor".Translate();
            }
        }
    }

    [HarmonyPatch(typeof(Command), nameof(Command.Desc), MethodType.Getter)]
    public static class RemoveFoundationDesc_Slab_Patch
    {
        public static void Postfix(Command __instance, ref string __result)
        {
            if (__instance is Designator_RemoveFoundation && FloorView.Current >= 1 && ProjectZ.Annex(Find.CurrentMap) != null)
            {
                __result = "PZ_RemoveUpperFloorDesc".Translate();
            }
        }
    }

    // Slabs are built like roofs (§7): from the supports outward. A slab blueprint or frame waits until a roof could be
    // held under it now (the placement already counted the planned slabs next to it).
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanConstruct), new[] { typeof(Thing), typeof(Pawn), typeof(bool), typeof(bool), typeof(JobDef) })]
    public static class CanConstruct_SlabSupport_Patch
    {
        public static bool Prefix(Thing t, ref bool __result)
        {
            if (t == null || !t.Spawned || ProjectZ.Annex(t.Map) == null)
            {
                return true;
            }
            // (no slab over, no stairs under a thick roof or on a gravship, also when that came after the blueprint)
            string column = null;
            if (t.def.entityDefToBuild == PZDefOf.PZ_Stairs)
            {
                AcceptanceReport r = PlaceWorker_Stairs.ThickRoofCheck(t.Position, t.Rotation, t.Map);
                column = r.Accepted ? null : r.Reason;
            }
            else if (SlabDefs.Is(t.def.entityDefToBuild))
            {
                string key = SlabSupport.SlabColumnBlocked(FloorRoof.Below(t.Position, t.Map), t.Map);
                column = key?.Translate();
            }
            if (column != null)
            {
                JobFailReason.Is(column);
                __result = false;
                return false;
            }
            if (!SlabDefs.Is(t.def.entityDefToBuild))
            {
                return true;
            }
            if (!SlabSupport.Supported(FloorRoof.Below(t.Position, t.Map), t.Map))
            {
                JobFailReason.Is("PZ_NoSupportBelow".Translate());
                __result = false;
                return false;
            }
            return true;
        }
    }
}
