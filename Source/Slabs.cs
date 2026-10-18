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
    //   - Blocker: what keeps a slab from being taken up (built things on it, stairs landing there, a slab above).
    //   - SlabPeel: the order of work (farthest from the stairs first, the worker on the cell one step nearer).
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
        // blueprint or frame), the landing of stairs (part of the stairs: it goes with them), a slab right above (from
        // the top down).
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
            IntVec3 up = StairShape.Above(c, map);
            if (up.IsValid && ProjectZ.HasFloor(up, map))
            {
                return "PZ_FloorAboveFirst".Translate();
            }
            return null;
        }

        // Takes the slab (and any floor still laid on it) down to open air: the roof below becomes a plain roof again
        // (FloorRoof), the materials come back as from any removed floor (on this floor: GenPlace patch), pawns and
        // items move off, the zone cell and the removal designation go.
        public static void Remove(IntVec3 c, Map map, bool doLeavings, Thing cause = null)
        {
            TerrainGrid tg = map.terrainGrid;
            TerrainDef top = tg.TopTerrainAt(c);
            TerrainDef under = tg.UnderTerrainAt(c);
            DropZoneCell(c, map);
            tg.SetTerrain(c, PZDefOf.PZ_OpenAir);
            if (doLeavings)
            {
                GenLeaving.DoLeavingsFor(top, c, map);
                if (under != null && under != PZDefOf.PZ_OpenAir)
                {
                    GenLeaving.DoLeavingsFor(under, c, map);
                }
            }
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

        // Pawns and items on a cell that just became open air go to the nearest cell on the same floor that holds them
        // (as the game's own recovery from an unwalkable cell); with none on the whole floor, to the nearest one on the
        // floor below (Q7: stepping off, no fall).
        public static void MoveOff(IntVec3 c, Map map, Thing cause = null)
        {
            List<Thing> list = c.GetThingList(map);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Thing t = list[i];
                if (t is Pawn p)
                {
                    IntVec3 to = NearestStandable(c, map, ProjectZ.FloorOf(c, map), true);
                    if (to.IsValid)
                    {
                        // The pawn taking the floor up keeps its job (it is finishing), as does one taking down the thing
                        // that takes this floor with it (the stairs and their landing); anyone else starts again.
                        bool remover = p.CurJob != null && ((p.CurJob.def == JobDefOf.RemoveFoundation || p.CurJob.def == JobDefOf.RemoveFloor) && p.CurJob.targetA.Cell == c || cause != null && p.CurJob.targetA.Thing == cause);
                        p.Position = to;
                        p.Notify_Teleported(endCurrentJob: !remover, resetTweenedPos: false);
                    }
                }
                else if (t.def.category == ThingCategory.Item)
                {
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
        // on the substructure and would leave the floor above behind. (Two-storey gravships: backlog.)
        public static bool Gravship(IntVec3 c, Map map) => c.IsValid && c.InBounds(map) && (map.terrainGrid.FoundationAt(c)?.IsSubstructure ?? false);

        // Thick roof or gravship under a cell: the reason no floor or stairs may use it, or null. The ship is its column
        // (ShipColumn: a ship stairs opening too; over it only ship decks and ship stairs go).
        public static string ColumnBlocked(IntVec3 c, Map map)
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
            string key = ColumnBlocked(c, map);
            return key == "PZ_OverGravship" && PZDefOf.PZ_ShipDeck != null ? "PZ_OverGravshipUseDeck" : key;
        }

        // The same for ordinary stairs: on (or landing over) a gravship it points to the ship stairs (Odyssey).
        public static string StairsColumnBlocked(IntVec3 c, Map map)
        {
            string key = ColumnBlocked(c, map);
            return key == "PZ_OverGravship" && PZDefOf.PZ_ShipStairs != null ? "PZ_OverGravshipUseStairs" : key;
        }

        private static readonly Queue<IntVec3> open = new Queue<IntVec3>();
        private static readonly HashSet<IntVec3> seen = new HashSet<IntVec3>();

        // The game's "a roof could be held here" (RoofCollapseUtility.WithinRangeOfRoofHolder, as JobDriver_BuildRoof
        // checks it): a roof holder (wall, column, stairs) within 6.9 cells, reached over roofed cells. For planning,
        // cells with a slab planned above count as roofed (a floor dragged out from a wall); 'ignore' is a holder about
        // to go (stairs being taken down).
        public static bool Supported(IntVec3 root, Map map, Thing ignore = null, bool countPlanned = false)
        {
            if (!root.IsValid || !root.InBounds(map))
            {
                return false;
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
                        && (map.roofGrid.Roofed(n) || countPlanned && (PlaceWorker_Stairs.FloorPlanned(StairShape.Above(n, map), map) || ShipDeck.Planned(StairShape.Above(n, map), map))))
                    {
                        seen.Add(n);
                        open.Enqueue(n);
                    }
                }
            }
            open.Clear();
            seen.Clear();
            return found;
        }
    }

    // The peel order (§7): per upper floor, the walking distance from the stairs landings over slab cells (4 ways, so
    // every step is one a pawn can walk). Only the designated slabs farthest away are worked (same distance in
    // parallel), from the cell one step nearer the stairs. A slab's shortest way to the stairs only passes nearer cells,
    // so taking a farthest one up cuts no other designated slab off. Designated slabs no stairs reach are never worked.
    // The distances are rebuilt only after slabs, stairs or walkability changed; the farthest designated distance is
    // read again at most once a tick (designations and blockers change it).
    public class SlabPeel : MapComponent
    {
        private int[] dist;
        private int[] parent;
        private int[] maxDist;
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
                foreach (StairLink l in g.links)
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
            if (maxDist == null || maxDist.Length != a.floors)
            {
                maxDist = new int[a.floors];
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
                int k = ProjectZ.FloorOf(c, map);
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
            return d > 0 && d == maxDist[ProjectZ.FloorOf(c, map)];
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
    }

    // A slab falls when the roof under it collapses (§7, replacing "a floor's roof never collapses"). The roof under a
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

        // The slab over the roof cell 'below' falls.
        public static void Collapse(IntVec3 below, Map map, List<Thing> crushed)
        {
            IntVec3 u = StairShape.Above(below, map);
            if (!u.IsValid || !u.InBounds(map) || !ProjectZ.HasFloor(u, map))
            {
                return;
            }
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

        // Where things falling from upper floor cell u come down: the first floor below with ground or a slab there,
        // on that cell or the nearest standable one on that floor.
        public static IntVec3 FallTo(IntVec3 u, Map map, out int fallen)
        {
            fallen = 0;
            int k = ProjectZ.FloorOf(u, map);
            IntVec3 g = ProjectZ.GroundCellOf(u, map);
            for (int j = k - 1; j >= 0; j--)
            {
                IntVec3 cj = ProjectZ.CellOnFloor(g, j, map);
                if (!cj.IsValid || j > 0 && !ProjectZ.HasFloor(cj, map))
                {
                    continue;
                }
                fallen = k - j;
                if (cj.Standable(map))
                {
                    return cj;
                }
                IntVec3 near = Slab.NearestStandable(cj, map, j, false);
                if (near.IsValid)
                {
                    return near;
                }
            }
            return IntVec3.Invalid;
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
            if (!__result && Slab.At(c, mapRef(__instance)))
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

    // The refusal says which blocker it is (vanilla's message covers only buildings).
    [HarmonyPatch(typeof(Designator_RemoveFoundation), nameof(Designator_RemoveFoundation.CanDesignateCell))]
    public static class RemoveFoundationDesignate_Slab_Patch
    {
        public static void Postfix(Designator_RemoveFoundation __instance, IntVec3 c, ref AcceptanceReport __result)
        {
            if (!__result.Accepted && !__result.Reason.NullOrEmpty() && Slab.At(c, __instance.Map))
            {
                string why = Slab.Blocker(c, __instance.Map);
                if (why != null)
                {
                    __result = why;
                }
            }
        }
    }

    // Peel order: only the farthest designated slabs have work.
    [HarmonyPatch(typeof(WorkGiver_ConstructRemoveFoundation), nameof(WorkGiver_ConstructRemoveFoundation.HasJobOnCell))]
    public static class RemoveFoundationHasJob_Slab_Patch
    {
        public static void Postfix(Pawn pawn, IntVec3 c, ref bool __result)
        {
            if (__result && Slab.At(c, pawn.Map) && SlabPeel.For(pawn.Map)?.Workable(c) != true)
            {
                if (SlabPeel.For(pawn.Map)?.Distance(c) > 0)
                {
                    JobFailReason.Is("PZ_FartherFirst".Translate());
                }
                __result = false;
            }
        }
    }

    // ... and the worker stands one step nearer the stairs (job target B), for the covering and for the slab.
    [HarmonyPatch(typeof(WorkGiver_ConstructRemoveFoundation), nameof(WorkGiver_ConstructRemoveFoundation.JobOnCell))]
    public static class RemoveFoundationJob_Slab_Patch
    {
        public static void Postfix(Pawn pawn, IntVec3 c, Job __result)
        {
            if (__result != null && Slab.At(c, pawn.Map))
            {
                IntVec3 from = SlabPeel.For(pawn.Map)?.Parent(c) ?? IntVec3.Invalid;
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
            bool spot = job != null && job.targetB.IsValid && !job.targetB.HasThing && Slab.At(job.targetA.Cell, __instance.pawn.Map);
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

    // Anything put down "near" a cell stays on that cell's floor (leavings of a removed floor or slab, a building's
    // leavings, drops, items moved off): the game's search radius reaches the floor below at a floor's south edge.
    // Every near/radius placement goes through this overload (the other one and ThingOwner.TryDrop call it).
    [HarmonyPatch(typeof(GenPlace), nameof(GenPlace.TryPlaceThing), new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(ThingPlaceMode), typeof(Thing), typeof(System.Action<Thing, int>), typeof(System.Predicate<IntVec3>), typeof(Rot4?), typeof(int) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal })]
    public static class TryPlaceThing_SameFloor_Patch
    {
        public static void Prefix(IntVec3 center, Map map, ThingPlaceMode mode, ref System.Predicate<IntVec3> extraValidator)
        {
            if (mode == ThingPlaceMode.Direct || ProjectZ.Annex(map) == null || !center.InBounds(map))
            {
                return;
            }
            int k = ProjectZ.FloorOf(center, map);
            System.Predicate<IntVec3> inner = extraValidator;
            extraValidator = x => ProjectZ.FloorOf(x, map) == k && (inner == null || inner(x));
        }
    }
}
