using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode reachfog: THE fog rule (ReachFog: every place the player's pawns can reach is revealed, and every edge they can
    // stand at shows what lies below it, all the way down). A fixed-seed tall-mountain map (the arguments of rocktop:
    // -projectztallmountains -projectzfloors=4 -projectzmapsize=250) saved once as pz_reachfog; every check reloads it,
    // builds its own fixture and runs real flows with ticks:
    //   R1 a colonist arrives on a level-2 ledge (teleport): the terrace one level below is revealed; it stays out of
    //      reach for pathing (a drafted order there goes nowhere)
    //   R2 a colonist arrives on a level-3 top: the area two or more floors below it is revealed
    //   E2 a colonist arrives on a top over terraces stepping down: every terrace below is revealed, also those only seen
    //      from a terrace seen before (the cascade)
    //   E3 a colonist in a chamber inside a mountain mines sideways through the face: the area below the opening is
    //      revealed when the rock goes, not before
    //   E4 a colonist arrives on a slab over a wall: the walled pen under its edge is revealed; the pen beside it behind a
    //      closed door, under no edge, stays fogged (and both stay fogged before anyone is on the slab)
    //   R3 colonists build stairs from the ground onto a fogged plateau's rim: it opens on completion
    //   R4 a plateau nobody can reach or see stays fogged after runs
    //   R5 a cave sealed by rock stays fogged while colonists mine towards it; it opens when they break through
    //   R6 a room behind a closed door stays fogged; a drafted colonist ordered into the doorway opens it and the room
    //   R7 cost: the full run (every region scanned and every edge worked out, as after a load), the walk-only run, the
    //      run after a wall is built, the run after a roof (a column) changed
    // Every check also verifies that every walkable cell it revealed is reached or seen (RfAllReached: an independent
    // reachability query that ignores fog plus a cell flood over edges and their landings), and that no inner rock
    // opened (RfInner: rock with no revealed open cell next to it, above or below stays fogged; ore counted). The checks
    // that must not open anything compare the whole fog grid.
    // With -projectzsave=<copy of a player's save>: the load-time pass (loaded once with the rule off, once on): how many
    // cells it reveals, all reached or seen, its cost, shots per level with the fog lid on.
    public static partial class TestRunner
    {
        private const string RfSave = "pz_reachfog";
        private static IntVec3 rfT2 = IntVec3.Invalid, rfLand1 = IntVec3.Invalid, rfGoal1 = IntVec3.Invalid, rfStand1 = IntVec3.Invalid;
        private static IntVec3 rfT3 = IntVec3.Invalid, rfLand2 = IntVec3.Invalid;
        private static IntVec3 rfStairs = IntVec3.Invalid, rfExit = IntVec3.Invalid;
        private static Rot4 rfRot;
        private static IntVec3 rfLone = IntVec3.Invalid;
        private static IntVec3 rfCaveE = IntVec3.Invalid, rfCaveD = IntVec3.Invalid;
        private static IntVec3 rfRoomE = IntVec3.Invalid, rfRoomD = IntVec3.Invalid;
        private static IntVec3 rfCasTop = IntVec3.Invalid;
        private static readonly List<List<IntVec3>> rfCasAreas = new List<List<IntVec3>>();
        private static readonly List<int> rfCasDepth = new List<int>();
        private static IntVec3 rfTunF = IntVec3.Invalid, rfTunD = IntVec3.Invalid, rfTunLand = IntVec3.Invalid;
        private static IntVec3 rfPen = IntVec3.Invalid;
        private static readonly List<IntVec3> rfArea = new List<IntVec3>();
        private static readonly List<IntVec3> rfArea2 = new List<IntVec3>();
        private static bool[] rfBefore = new bool[0];
        private static Pawn rfPawn;
        private static int rfShot, rfFog0, rfFog1;
        private static string rfUserSave;

        private static bool[] RfSnap(Map map)
        {
            int n = map.cellIndices.NumGridCells;
            bool[] f = new bool[n];
            for (int i = 0; i < n; i++)
            {
                f[i] = map.fogGrid.IsFogged(i);
            }
            return f;
        }

        private static int RfChanged(Map map, bool[] before)
        {
            int n = 0;
            for (int i = 0; i < before.Length && i < map.cellIndices.NumGridCells; i++)
            {
                n += before[i] != map.fogGrid.IsFogged(i) ? 1 : 0;
            }
            return n;
        }

        // Independent reachability (the game's query with ProjectZ's floor links, no pawn: fog is no obstacle, doors pass)
        // from any of the start cells.
        private static bool RfReach(Map map, IntVec3 c, List<IntVec3> from)
        {
            TraverseParms tp = TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly);
            for (int i = 0; i < from.Count; i++)
            {
                if (map.reachability.CanReach(from[i], c, PathEndMode.OnCell, tp))
                {
                    return true;
                }
            }
            return false;
        }

        private static List<Pawn> RfPlayers(Map map) => map.mapPawns.AllPawnsSpawned.Where(ReachFog.PlayerPawn).ToList();

        private static bool RfOpenAir(IntVec3 c, Map map) => c.InBounds(map) && c.GetTerrain(map) == PZDefOf.PZ_OpenAir && c.GetEdifice(map) == null;

        // The oracle's seen cells (written apart from ReachFog: cells, not regions): from every reached walkable cell (and
        // every seen one, again) each open-air 4-neighbour on its band falls to Column.Landing (no roof on the way); a
        // walkable landing floods its walkable 4-connected cells on its band (a closed door is taken, not passed); an
        // open-air cell with a roof over it on a cell's ceiling column floods from its walkable 4-neighbours.
        private static HashSet<IntVec3> RfSeenCells(Map map, Func<IntVec3, bool> reached)
        {
            HashSet<IntVec3> seen = new HashSet<IntVec3>();
            List<IntVec3> src = new List<IntVec3>();
            List<IntVec3> broken = new List<IntVec3>();
            void Flood(IntVec3 s)
            {
                if (!s.InBounds(map) || !s.Walkable(map) || seen.Contains(s) || reached(s))
                {
                    return;
                }
                Queue<IntVec3> q = new Queue<IntVec3>();
                seen.Add(s);
                q.Enqueue(s);
                while (q.Count > 0)
                {
                    IntVec3 x = q.Dequeue();
                    src.Add(x);
                    if (x.GetEdifice(map) is Building_Door dd && !dd.Open)
                    {
                        continue;
                    }
                    for (int d = 0; d < 4; d++)
                    {
                        IntVec3 n = x + GenAdj.CardinalDirections[d];
                        if (n.InBounds(map) && ProjectZ.SameBand(n, x, map) && n.Walkable(map) && !reached(n) && seen.Add(n))
                        {
                            q.Enqueue(n);
                        }
                    }
                }
            }
            void Source(IntVec3 c)
            {
                for (int d = 0; d < 4; d++)
                {
                    IntVec3 n = c + GenAdj.CardinalDirections[d];
                    if (!RfOpenAir(n, map) || !ProjectZ.SameBand(n, c, map))
                    {
                        continue;
                    }
                    IntVec3 land = Column.Landing(n, map, false, true, out _, out _, broken);
                    if (land.IsValid && land != n && broken.Count == 0)
                    {
                        Flood(land);
                    }
                }
                for (IntVec3 up = FloorRoof.Above(c, map); up.IsValid && RfOpenAir(up, map); up = FloorRoof.Above(up, map))
                {
                    if (map.roofGrid.Roofed(up))
                    {
                        for (int d = 0; d < 4; d++)
                        {
                            IntVec3 w = up + GenAdj.CardinalDirections[d];
                            if (w.InBounds(map) && ProjectZ.SameBand(w, up, map))
                            {
                                Flood(w);
                            }
                        }
                    }
                }
            }
            for (int i = 0; i < map.cellIndices.NumGridCells; i++)
            {
                IntVec3 c = map.cellIndices.IndexToCell(i);
                if (c.z < ProjectZ.GroundZ(map) && !RfOpenAir(FloorRoof.Above(c, map), map))
                {
                    continue;   // (ground cells: no open air beside them; only a ceiling of open air matters)
                }
                bool edge = RfOpenAir(FloorRoof.Above(c, map), map);
                for (int d = 0; d < 4 && !edge; d++)
                {
                    edge = RfOpenAir(c + GenAdj.CardinalDirections[d], map);
                }
                if (edge && c.Walkable(map) && reached(c))
                {
                    Source(c);
                }
            }
            for (int k = 0; k < src.Count; k++)
            {
                Source(src[k]);
            }
            return seen;
        }

        // Everything unfogged since 'before': every walkable cell reached by a player pawn or seen from an edge; the rest
        // (faces, ceilings, fall columns, the open fog vanilla's flood takes along) counted.
        private static bool RfAllReached(Map map, bool[] before, out string why)
        {
            List<IntVec3> from = RfPlayers(map).Select(p => p.Position).ToList();
            Dictionary<Region, bool> byRegion = new Dictionary<Region, bool>();
            bool Reached(IntVec3 c)
            {
                Region r = map.regionGrid.GetValidRegionAt(c);
                if (r == null)
                {
                    return false;
                }
                if (!byRegion.TryGetValue(r, out bool ok))
                {
                    ok = RfReach(map, c, from);
                    byRegion[r] = ok;
                }
                return ok;
            }
            HashSet<IntVec3> seen = RfSeenCells(map, Reached);
            bool Ok(IntVec3 c) => seen.Contains(c) || Reached(c);
            int newly = 0, walk = 0, bad = 0, faces = 0, ceil = 0, other = 0, topCeil = 0, bySight = 0;
            string first = null;
            bool CeilingOfOk(IntVec3 c)
            {
                IntVec3 b = FloorRoof.Below(c, map);
                while (b.IsValid && b.InBounds(map) && b.GetTerrain(map) == PZDefOf.PZ_OpenAir && b.GetEdifice(map) == null)
                {
                    b = FloorRoof.Below(b, map);
                }
                return b.IsValid && b.InBounds(map) && b.Walkable(map) && Ok(b);
            }
            for (int i = 0; i < before.Length && i < map.cellIndices.NumGridCells; i++)
            {
                if (!before[i] || map.fogGrid.IsFogged(i))
                {
                    continue;
                }
                newly++;
                IntVec3 c = map.cellIndices.IndexToCell(i);
                if (c.Walkable(map))
                {
                    walk++;
                    if (seen.Contains(c))
                    {
                        bySight++;
                    }
                    else if (!Reached(c))
                    {
                        if (CeilingOfOk(c))
                        {
                            topCeil++;   // (a top that is the ceiling of a reached cave: seen from below, not walked on)
                        }
                        else
                        {
                            bad++;
                            first ??= $"{c} L{Levels.LevelOf(c, map)} {c.GetTerrain(map).defName}";
                        }
                    }
                    continue;
                }
                bool face = false;
                for (int d = 0; d < 8 && !face; d++)
                {
                    IntVec3 n = c + GenAdj.AdjacentCells[d];
                    face = n.InBounds(map) && n.Walkable(map) && !map.fogGrid.IsFogged(n) && Ok(n);
                }
                if (face)
                {
                    faces++;
                    continue;
                }
                if (CeilingOfOk(c))
                {
                    ceil++;
                }
                else
                {
                    other++;
                }
            }
            why = $"{newly} cell(s) revealed: {walk} walkable ({bySight} seen from an edge, {bad} NOT reached or seen{(first == null ? "" : ", first " + first)}, {topCeil} tops seen from below as a ceiling), {faces} faces, {ceil} ceilings, {other} other (fall columns, vanilla's flood through open fog, faces of open air)";
            return bad == 0;
        }

        // Inner rock: rock (any mineable, ore too) with no revealed open cell among its 8 neighbours, right above it or
        // right below it, as the fog stands now. None of it may have opened since 'before'.
        private static bool RfInner(Map map, bool[] before, out string why)
        {
            FogGrid fog = map.fogGrid;
            bool OpenSeen(IntVec3 x) => x.IsValid && x.InBounds(map) && !fog.IsFogged(x) && !(x.GetEdifice(map) is Building e && e.def.MakeFog);
            int inner = 0, innerFog = 0, ore = 0, oreFog = 0, opened = 0, oreOpened = 0;
            string first = null;
            for (int i = 0; i < map.cellIndices.NumGridCells; i++)
            {
                IntVec3 c = map.cellIndices.IndexToCell(i);
                if (!(c.GetEdifice(map) is Mineable m))
                {
                    continue;
                }
                bool next = OpenSeen(FloorRoof.Above(c, map)) || OpenSeen(FloorRoof.Below(c, map));
                for (int d = 0; d < 8 && !next; d++)
                {
                    next = OpenSeen(c + GenAdj.AdjacentCells[d]);
                }
                if (next)
                {
                    continue;
                }
                bool isOre = m.def.building.isResourceRock;
                bool f = fog.IsFogged(c);
                inner++;
                innerFog += f ? 1 : 0;
                ore += isOre ? 1 : 0;
                oreFog += isOre && f ? 1 : 0;
                if (i < before.Length && before[i] && !f)
                {
                    opened++;
                    oreOpened += isOre ? 1 : 0;
                    first ??= $"{c} L{Levels.LevelOf(c, map)} {m.def.defName}";
                }
            }
            why = $"inner rock {inner} cells ({ore} ore): fogged {innerFog} now (ore {oreFog}), opened since before {opened} (ore {oreOpened}){(first == null ? "" : ", first " + first)}";
            return opened == 0;
        }

        private static void RfFixture(Map map)
        {
            DebugSettings.enableRandomMentalStates = false;
            Find.PlaySettings.autoRebuild = false;
            Find.Storyteller.storytellerComps.Clear();
            Find.Storyteller.incidentQueue.Clear();
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            IsoRimBridge.SetOption("fogLid", true);
        }

        private static void RfNext(string next)
        {
            Find.UIRoot.screenshotMode.Active = false;
            muPointer = IntVec3.Invalid;
            FloorView.Set(0);
            Find.DesignatorManager.Deselect();
            if (next == null)
            {
                Finish();
                return;
            }
            stage = next;
            pendingLoad = rfUserSave ?? RfSave;
        }

        // The cells 4-connected to 'from' on its band through walkable cells (cap).
        private static List<IntVec3> RfFlat(IntVec3 from, Map map, int cap = 4000)
        {
            List<IntVec3> l = new List<IntVec3>();
            if (!from.IsValid || !from.InBounds(map) || !from.Walkable(map))
            {
                return l;
            }
            int band = Levels.BandOfCell(from, map);
            HashSet<IntVec3> seen = new HashSet<IntVec3> { from };
            Queue<IntVec3> q = new Queue<IntVec3>();
            q.Enqueue(from);
            while (q.Count > 0 && l.Count < cap)
            {
                IntVec3 c = q.Dequeue();
                l.Add(c);
                for (int d = 0; d < 4; d++)
                {
                    IntVec3 n = c + GenAdj.CardinalDirections[d];
                    if (n.InBounds(map) && Levels.BandOfCell(n, map) == band && n.Walkable(map) && seen.Add(n))
                    {
                        q.Enqueue(n);
                    }
                }
            }
            return l;
        }

        private static int RfFogged(IEnumerable<IntVec3> cells, Map map) => cells.Count(c => map.fogGrid.IsFogged(c));

        // Where a fall from the open-air cell n lands (Column.Landing), Invalid when a roof is on the way or nothing holds.
        private static IntVec3 RfFall(IntVec3 n, Map map, bool standable, out int fallen)
        {
            List<IntVec3> broken = new List<IntVec3>();
            IntVec3 l = Column.Landing(n, map, standable, true, out fallen, out _, broken);
            return l.IsValid && l != n && broken.Count == 0 ? l : IntVec3.Invalid;
        }

        // A top at level L over g beside open air whose fall lands at least 'fall' floors down on a fogged cell with a
        // fogged area of at least minFog cells round it (and a fogged goal cell 2+ cells inside it).
        private static bool RfLedge(IntVec3 g, int L, int fall, int minFog, Map map, out IntVec3 top, out IntVec3 land, out IntVec3 goal, out IntVec3 stand)
        {
            top = land = goal = stand = IntVec3.Invalid;
            if (g.x < 4 || g.z < 4 || g.x >= map.Size.x - 4 || g.z >= ProjectZ.GroundZ(map) - 4 || Column.NaturalTop(g, map) != L)
            {
                return false;
            }
            IntVec3 t = Levels.Cell(g, L, map);
            if (!t.IsValid || !TmTop(t, map) || !t.Standable(map))
            {
                return false;
            }
            for (int d = 0; d < 4; d++)
            {
                IntVec3 dir = GenAdj.CardinalDirections[d];
                IntVec3 n = t + dir;
                if (!RfOpenAir(n, map) || !ProjectZ.SameBand(n, t, map))
                {
                    continue;
                }
                IntVec3 l = RfFall(n, map, true, out int fallen);
                if (!l.IsValid || fallen < fall || !map.fogGrid.IsFogged(l) || !l.Standable(map))
                {
                    continue;
                }
                List<IntVec3> area = RfFlat(l, map, 400);
                if (RfFogged(area, map) < minFog)
                {
                    continue;
                }
                IntVec3 gl = area.Where(c => map.fogGrid.IsFogged(c) && c.Standable(map) && c.DistanceTo(l) >= 2f && c.DistanceTo(l) <= 5f).OrderBy(c => c.DistanceToSquared(l)).ThenBy(c => c.x).ThenBy(c => c.z).FirstOrDefault();
                IntVec3 st = t - dir;
                if (!gl.IsValid || gl == IntVec3.Zero || !st.InBounds(map) || !TmTop(st, map) || !st.Standable(map))
                {
                    continue;
                }
                top = t;
                land = l;
                goal = gl;
                stand = st;
                return true;
            }
            return false;
        }

        // A top (level 3+) beside open air whose seen closure, worked out on the cells (RfFlat areas joined by falls),
        // takes in fogged terraces (6+ fogged cells) on two or more levels below it, at least one of them only seen from a
        // terrace seen before (depth 2+).
        private static bool RfCascade(IntVec3 g, Map map, HashSet<IntVec3> done)
        {
            int L = Column.NaturalTop(g, map);
            if (L < 3 || g.x < 4 || g.z < 4 || g.x >= map.Size.x - 4 || g.z >= ProjectZ.GroundZ(map) - 4)
            {
                return false;
            }
            IntVec3 t = Levels.Cell(g, L, map);
            if (!t.IsValid || done.Contains(t) || !TmTop(t, map) || !t.Standable(map) || !GenAdj.CardinalDirections.Any(d => RfOpenAir(t + d, map)))
            {
                return false;
            }
            List<List<IntVec3>> areas = new List<List<IntVec3>>();
            List<int> depth = new List<int>();
            HashSet<IntVec3> mark = new HashSet<IntVec3>();
            List<IntVec3> a0 = RfFlat(t, map, 600);
            a0.ForEach(c => { mark.Add(c); done.Add(c); });
            areas.Add(a0);
            depth.Add(0);
            for (int k = 0; k < areas.Count && areas.Count < 40; k++)
            {
                foreach (IntVec3 c in areas[k])
                {
                    for (int d = 0; d < 4; d++)
                    {
                        IntVec3 n = c + GenAdj.CardinalDirections[d];
                        if (!RfOpenAir(n, map) || !ProjectZ.SameBand(n, c, map))
                        {
                            continue;
                        }
                        IntVec3 l = RfFall(n, map, false, out _);
                        if (!l.IsValid || !l.Walkable(map) || mark.Contains(l))
                        {
                            continue;
                        }
                        List<IntVec3> a = RfFlat(l, map, 600);
                        a.ForEach(x => mark.Add(x));
                        areas.Add(a);
                        depth.Add(depth[k] + 1);
                    }
                }
            }
            List<int> keep = Enumerable.Range(1, areas.Count - 1).Where(i => RfFogged(areas[i], map) >= 6 && Levels.LevelOf(areas[i][0], map) >= 1).ToList();
            int levels = keep.Select(i => Levels.LevelOf(areas[i][0], map)).Distinct().Count();
            if (levels < 2 || !keep.Any(i => depth[i] >= 2))
            {
                return false;
            }
            rfCasTop = t;
            rfCasAreas.Clear();
            rfCasDepth.Clear();
            foreach (int i in keep)
            {
                rfCasAreas.Add(areas[i]);
                rfCasDepth.Add(depth[i]);
            }
            return true;
        }

        // A face inside a mountain at level L >= 2: rock F with open air beyond it (dir d) whose fall lands on a fogged
        // area (8+ fogged cells), and solid rock 3 wide and 4 deep behind and round F (the chamber is carved into it).
        private static bool RfTunnel(IntVec3 g, Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (g.x < 6 || g.z < 6 || g.x >= map.Size.x - 6 || g.z >= a.groundZ - 6)
            {
                return false;
            }
            for (int L = 2; L <= a.MaxLevel; L++)
            {
                IntVec3 f = Levels.Cell(g, L, map);
                if (!f.IsValid || !MuRock(f, map))
                {
                    continue;
                }
                for (int k = 0; k < 4; k++)
                {
                    IntVec3 d = GenAdj.CardinalDirections[k];
                    IntVec3 n = f + d;
                    if (!RfOpenAir(n, map) || !ProjectZ.SameBand(n, f, map))
                    {
                        continue;
                    }
                    IntVec3 perp = new IntVec3(d.z, 0, d.x);
                    bool solid = true;
                    for (int b = 0; b <= 3 && solid; b++)
                    {
                        for (int j = -1; j <= 1 && solid; j++)
                        {
                            IntVec3 x = f - d * b + perp * j;
                            solid = x.InBounds(map) && ProjectZ.SameBand(x, f, map) && MuRock(x, map);
                        }
                    }
                    if (!solid)
                    {
                        continue;
                    }
                    IntVec3 l = RfFall(n, map, true, out _);
                    if (!l.IsValid || !l.Standable(map) || !map.fogGrid.IsFogged(l) || RfFogged(RfFlat(l, map, 200), map) < 8)
                    {
                        continue;
                    }
                    rfTunF = f;
                    rfTunD = d;
                    rfTunLand = l;
                    return true;
                }
            }
            return false;
        }

        // A 9x5 patch of open, seen, unroofed ground (open air over it) for the pens and the slab (E4).
        private static bool RfPenSpot(IntVec3 o, Map map)
        {
            CellRect r = new CellRect(o.x, o.z, 9, 5);
            if (!r.ExpandedBy(2).InBounds(map) || r.maxZ + 3 >= ProjectZ.GroundZ(map))
            {
                return false;
            }
            foreach (IntVec3 c in r.ExpandedBy(1))
            {
                if (!c.Standable(map) || c.GetEdifice(map) != null || c.Roofed(map) || map.fogGrid.IsFogged(c) || c.GetFirstPawn(map) != null || c.GetFirstItem(map) != null
                    || c.GetTerrain(map).IsWater || !RfOpenAir(Levels.Cell(c, 1, map), map))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool RfSites(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            List<Pawn> cols = Colonists(map);
            IntVec3 home = new IntVec3(map.Size.x / 2, 0, a.groundZ / 2);   // (fixed: the colonists wander before the setup)
            List<IntVec3> near = Near(home, 110, map).ToList();
            IntVec3 t, l, gl;
            // R1: a level-2 ledge over a fogged level-1 terrace (one floor down)
            foreach (IntVec3 g in near)
            {
                if (RfLedge(g, 2, 1, 12, map, out t, out l, out gl, out IntVec3 st1) && Levels.LevelOf(l, map) == 1)
                {
                    rfT2 = t;
                    rfLand1 = l;
                    rfGoal1 = gl;
                    rfStand1 = st1;
                    break;
                }
            }
            // R2: a level-3 top two or more floors over a fogged landing area, away from R1
            foreach (IntVec3 g in near)
            {
                if ((!rfT2.IsValid || !g.InHorDistOf(ProjectZ.GroundCellOf(rfT2, map), 30f)) && RfLedge(g, 3, 2, 6, map, out t, out l, out gl, out _))
                {
                    rfT3 = t;
                    rfLand2 = gl;
                    break;
                }
            }
            // E2: terraces stepping down from a top (the cascade)
            HashSet<IntVec3> done = new HashSet<IntVec3>();
            foreach (IntVec3 g in near)
            {
                if (RfCascade(g, map, done))
                {
                    break;
                }
            }
            // E3: a mountain face to tunnel through
            foreach (IntVec3 g in near)
            {
                if (RfTunnel(g, map))
                {
                    break;
                }
            }
            // E4: open ground for the pens and the slab
            foreach (IntVec3 o in near)
            {
                if (RfPenSpot(o, map))
                {
                    rfPen = o;
                    break;
                }
            }
            // R3: stairs on the ground whose exit is a rim top with a fogged plateau behind it (as tallfog G2)
            Pawn any = cols.FirstOrDefault();
            foreach (IntVec3 p in near)
            {
                if (rfStairs.IsValid || p.z >= a.groundZ - 3 || p.z < 3 || p.x < 3 || p.x >= map.Size.x - 3)
                {
                    continue;
                }
                for (int r = 0; r < 4 && !rfStairs.IsValid; r++)
                {
                    Rot4 rot = new Rot4(r);
                    IntVec3 exit = StairShape.Exit(p, rot, map);
                    if (!exit.IsValid || !TmTop(exit, map) || map.fogGrid.IsFogged(exit) || Levels.LevelOf(exit, map) != 1)
                    {
                        continue;
                    }
                    // (1x4 stairs: the solid end under the exit may be the mountain's rock, dug first by vanilla Mine on the ground)
                    IntVec3 solid = StairShape.Solid(p, rot);
                    bool solidRock = solid.InBounds(map) && !map.fogGrid.IsFogged(solid) && solid.GetFirstMineable(map) != null;
                    AcceptanceReport okP = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, p, rot, map, stuffDef: ThingDefOf.WoodLog);
                    if (StairShape.Ramp(p, rot).Any(c => !c.InBounds(map) || map.fogGrid.IsFogged(c) || c.Roofed(map) || c.GetEdifice(map) != null || !c.Standable(map))
                        || !solidRock && (!solid.InBounds(map) || map.fogGrid.IsFogged(solid) || solid.GetEdifice(map) != null || !solid.Standable(map))
                        || any == null || !map.reachability.CanReach(any.Position, p, PathEndMode.Touch, TraverseParms.For(TraverseMode.PassDoors))
                        || !(okP.Accepted || solidRock && okP.Reason == "PZ_StairsMineFirst".Translate("PZ_StairsPart3".Translate()).ToString()))
                    {
                        continue;
                    }
                    if (RfFogged(RfFlat(exit, map, 2000), map) < 30)
                    {
                        continue;
                    }
                    rfStairs = p;
                    rfRot = rot;
                    rfExit = exit;
                }
            }
            // R4: a fogged level-1 top with 20+ fogged cells round it, the nearest to home
            foreach (IntVec3 g in near)
            {
                IntVec3 c = Levels.Cell(g, 1, map);
                if (c.IsValid && Column.NaturalTop(g, map) == 1 && TmTop(c, map) && map.fogGrid.IsFogged(c) && RfFogged(RfFlat(c, map, 200), map) >= 20)
                {
                    rfLone = c;
                    break;
                }
            }
            // R5 (cave, k 4) and R6 (room, k 3): pockets of fogged rock beside reachable ground, apart from each other
            foreach (IntVec3 e in near)
            {
                if (!rfCaveE.IsValid && RfPocket(e, 4, map, any, out IntVec3 d))
                {
                    rfCaveE = e;
                    rfCaveD = d;
                }
                else if (!rfRoomE.IsValid && (!rfCaveE.IsValid || !e.InHorDistOf(rfCaveE, 15f)) && RfPocket(e, 3, map, any, out IntVec3 d2))
                {
                    rfRoomE = e;
                    rfRoomD = d2;
                }
                if (rfCaveE.IsValid && rfRoomE.IsValid)
                {
                    break;
                }
            }
            Info($"sites: R1 ledge {rfT2} -> terrace {rfLand1} goal {rfGoal1}; R2 top {rfT3} -> {rfLand2}; E2 top {rfCasTop} over {rfCasAreas.Count} fogged terrace(s) on levels {string.Join(",", rfCasAreas.Select(x => Levels.LevelOf(x[0], map)))} depths {string.Join(",", rfCasDepth)}; E3 face {rfTunF} dir {rfTunD} -> {rfTunLand}; E4 pens at {rfPen}; R3 stairs {rfStairs} {rfRot} exit {rfExit}; R4 plateau {rfLone}; R5 cave beside {rfCaveE} dir {rfCaveD}; R6 room beside {rfRoomE} dir {rfRoomD}");
            return rfT2.IsValid && rfT3.IsValid && rfCasTop.IsValid && rfTunF.IsValid && rfPen.IsValid && rfStairs.IsValid && rfLone.IsValid && rfCaveE.IsValid && rfRoomE.IsValid;
        }

        // A ground cell e (open, seen, reachable) and a direction: rock at e+d, then a 5x5 block of fogged natural rock
        // centred at e+k*d (k = 3: room, 4: cave). Returns the centre.
        private static bool RfPocket(IntVec3 e, int k, Map map, Pawn any, out IntVec3 dir)
        {
            dir = IntVec3.Invalid;
            if (!e.InBounds(map) || e.x < 8 || e.z < 8 || e.x >= map.Size.x - 8 || e.z >= ProjectZ.GroundZ(map) - 8 || !e.Standable(map) || map.fogGrid.IsFogged(e) || e.GetEdifice(map) != null)
            {
                return false;
            }
            for (int d = 0; d < 4; d++)
            {
                IntVec3 dd = GenAdj.CardinalDirections[d];
                IntVec3 c0 = e + dd * k;
                if (!MuRock(e + dd, map) || !HfBlock(c0, 2, map, x => MuRock(x, map) && ((x - e).x * dd.x + (x - e).z * dd.z == 1 || map.fogGrid.IsFogged(x))))
                {
                    continue;
                }
                if (any == null || !map.reachability.CanReach(any.Position, e, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors)))
                {
                    continue;
                }
                dir = dd;
                return true;
            }
            return false;
        }

        // The common tail of a check that opened something: the oracle and the inner rock.
        private static void RfAudit(Map map, string check)
        {
            bool ok = RfAllReached(map, rfBefore, out string why);
            Result($"{check} everything revealed is reached or seen from an edge", ok, why);
            bool inner = RfInner(map, rfBefore, out string why2);
            Result($"{check} no inner rock or ore opened (only surfaces next to seen open space)", inner, why2);
        }

        private static Building RfSpawn(ThingDef def, IntVec3 c, Map map, Rot4 rot)
        {
            Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.WoodLog : null);
            t.SetFaction(Faction.OfPlayer);
            return (Building)GenSpawn.Spawn(t, c, map, rot, WipeMode.Vanish);
        }

        private static bool StepReachFog(Map map)
        {
            switch (stage)
            {
                case "rf_setup":
                {
                    RfFixture(map);
                    if (GenCommandLine.TryGetCommandLineArg("projectzsave", out rfUserSave))
                    {
                        ReachFog.Off = true;
                        stage = "rf_u_off";
                        pendingLoad = rfUserSave;
                        return true;
                    }
                    rfUserSave = null;
                    if (ProjectZ.Annex(map) == null || !Mountains.On(map))
                    {
                        Result("reachfog: a tall-mountain map", false, "no annex or no tall mountains (-projectztallmountains)");
                        Finish();
                        return true;
                    }
                    bool sites = RfSites(map);
                    Result("R sites: a ledge over a fogged terrace, a top over a 2+ floor drop, terraces stepping down, a mountain face, open ground for pens, a stairs spot onto a fogged plateau, a fogged plateau, two rock pockets beside reachable ground", sites, "see sites");
                    if (!sites)
                    {
                        Finish();
                        return true;
                    }
                    FloorView.Set(0);
                    GameDataSaveLoader.SaveGame(RfSave);
                    RfNext("rf_r1");
                    return true;
                }

                // ------------------------------------------------------------ R1 a terrace one level below a ledge
                case "rf_r1":
                {
                    RfFixture(map);
                    ReachFog.Run(map);
                    rfArea.Clear();
                    rfArea.AddRange(RfFlat(rfLand1, map, 400));
                    rfFog0 = RfFogged(rfArea, map);
                    rfBefore = RfSnap(map);
                    Pawn p = Colonists(map).OrderBy(x => x.thingIDNumber).First(x => !x.Downed);
                    Info($"R1 ledge {rfT2} (L2), terrace {rfArea.Count} cells round {rfLand1} (L1), fogged {rfFog0}; {p} arrives on the ledge at {rfStand1} (teleport)");
                    Teleport(p, rfStand1);
                    Draft(p, true);
                    rfPawn = p;
                    Wait(3, "rf_r1_arrived");
                    return true;
                }

                case "rf_r1_arrived":
                {
                    int fog = RfFogged(rfArea, map);
                    Result("R1 a colonist arrived on a level-2 ledge: the terrace one level below is revealed", rfFog0 > 0 && fog == 0,
                        $"terrace cells fogged {fog} of {rfArea.Count} (before {rfFog0}); run {ReachFog.LastMs:0.00} ms (walk {ReachFog.LastWalkMs:0.00} ms, seen {ReachFog.LastSeenMs:0.00} ms), {ReachFog.LastRegions} regions reached, {ReachFog.LastSeen} seen, {ReachFog.LastScanned} scanned");
                    RfAudit(map, "R1");
                    bool can = rfPawn.CanReach(rfGoal1, PathEndMode.OnCell, Danger.Deadly);
                    Info($"R1 pathing: {rfPawn} can reach the terrace goal {rfGoal1}: {can}; ordered there (drafted)");
                    rfFog1 = can ? 1 : 0;
                    rfPawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, rfGoal1), JobTag.DraftedOrder);
                    MuRun(30, 71711, () => Levels.LevelOf(rfPawn.Position, map) != 2, "rf_r1_order");
                    return true;
                }

                case "rf_r1_order":
                {
                    Result("R1 the revealed terrace stays out of reach: the drafted colonist ordered there cannot path to it and stays on the ledge",
                        rfFog1 == 0 && Levels.LevelOf(rfPawn.Position, map) == 2, $"CanReach {rfFog1 == 1}; {rfPawn} at {rfPawn.Position} L{Levels.LevelOf(rfPawn.Position, map)}");
                    MuShot(map, "reachfog_r1_terrace_L1", 1, rfLand1, 14f, "rf_r1_end");
                    return true;
                }

                case "rf_r1_end":
                    RfNext("rf_r2");
                    return true;

                // ------------------------------------------------------------ R2 a drop of two floors or more
                case "rf_r2":
                {
                    RfFixture(map);
                    ReachFog.Run(map);
                    rfArea.Clear();
                    rfArea.AddRange(RfFlat(rfLand2, map, 400));
                    rfFog0 = RfFogged(rfArea, map);
                    rfBefore = RfSnap(map);
                    Pawn p = Colonists(map).OrderBy(x => x.thingIDNumber).First(x => !x.Downed);
                    Info($"R2 top {rfT3} (L3), area {rfArea.Count} cells round {rfLand2} (L{Levels.LevelOf(rfLand2, map)}) two or more floors down, fogged {rfFog0}; {p} arrives on the top");
                    Teleport(p, rfT3);
                    Draft(p, true);
                    Wait(3, "rf_r2_arrived");
                    return true;
                }

                case "rf_r2_arrived":
                {
                    int fog = RfFogged(rfArea, map);
                    Result("R2 a colonist arrived on a level-3 top: the area two or more floors below its edge is revealed", rfFog0 > 0 && fog == 0, $"area cells fogged {fog} of {rfArea.Count} (before {rfFog0})");
                    RfAudit(map, "R2");
                    MuShot(map, $"reachfog_r2_drop_L{Levels.LevelOf(rfLand2, map)}", Levels.LevelOf(rfLand2, map), ProjectZ.GroundCellOf(rfLand2, map), 16f, "rf_r2_end");
                    return true;
                }

                case "rf_r2_end":
                    RfNext("rf_e2");
                    return true;

                // ------------------------------------------------------------ E2 terraces stepping down (cascade)
                case "rf_e2":
                {
                    RfFixture(map);
                    ReachFog.Run(map);
                    rfBefore = RfSnap(map);
                    Pawn p = Colonists(map).OrderBy(x => x.thingIDNumber).First(x => !x.Downed);
                    Info($"E2 top {rfCasTop} (L{Levels.LevelOf(rfCasTop, map)}); terraces below: {string.Join("; ", rfCasAreas.Select((x, i) => $"L{Levels.LevelOf(x[0], map)} depth {rfCasDepth[i]} {x.Count} cells, fogged {RfFogged(x, map)}"))}; {p} arrives on the top");
                    Teleport(p, rfCasTop);
                    Draft(p, true);
                    Wait(3, "rf_e2_arrived");
                    return true;
                }

                case "rf_e2_arrived":
                {
                    List<string> left = new List<string>();
                    for (int i = 0; i < rfCasAreas.Count; i++)
                    {
                        int f = RfFogged(rfCasAreas[i], map);
                        if (f > 0)
                        {
                            left.Add($"L{Levels.LevelOf(rfCasAreas[i][0], map)} depth {rfCasDepth[i]}: {f} fogged");
                        }
                    }
                    Result($"E2 a colonist on a top over terraces stepping down (levels {string.Join(",", rfCasAreas.Select(x => Levels.LevelOf(x[0], map)).Distinct())}): every terrace below is revealed, those seen only from a seen terrace too",
                        left.Count == 0, left.Count == 0 ? $"{rfCasAreas.Count} terrace(s) revealed; run {ReachFog.LastMs:0.00} ms, {ReachFog.LastSeen} regions seen" : string.Join("; ", left));
                    RfAudit(map, "E2");
                    int lo = rfCasAreas.Select(x => Levels.LevelOf(x[0], map)).Min();
                    MuShot(map, $"reachfog_e2_cascade_L{lo}", lo, ProjectZ.GroundCellOf(rfCasTop, map), 24f, "rf_e2_shot2");
                    return true;
                }

                case "rf_e2_shot2":
                {
                    int L = Levels.LevelOf(rfCasTop, map) - 1;
                    MuShot(map, $"reachfog_e2_cascade_L{L}", L, ProjectZ.GroundCellOf(rfCasTop, map), 24f, "rf_e2_end");
                    return true;
                }

                case "rf_e2_end":
                    RfNext("rf_e3");
                    return true;

                // ------------------------------------------------------------ E3 a tunnel through a mountain face
                case "rf_e3":
                {
                    RfFixture(map);
                    IntVec3 c1 = rfTunF - rfTunD, c2 = rfTunF - rfTunD * 2;
                    HfCarve(map, new[] { c1, c2 }, true);
                    rfArea.Clear();
                    rfArea.AddRange(RfFlat(rfTunLand, map, 200));
                    Pawn p = Colonists(map).OrderBy(x => x.thingIDNumber).First(x => !x.Downed && !x.WorkTypeIsDisabled(WorkTypeDefOf.Mining));
                    foreach (Pawn o in Colonists(map).Where(o => o != p))
                    {
                        Draft(o, true);
                    }
                    Teleport(p, c2);
                    MdWork(map, new List<Pawn> { p }, WorkTypeDefOf.Mining);
                    rfPawn = p;
                    Wait(3, "rf_e3_inside");
                    return true;
                }

                case "rf_e3_inside":
                {
                    ReachFog.Run(map);
                    rfFog0 = RfFogged(rfArea, map);
                    rfBefore = RfSnap(map);
                    Result("E3 a colonist in a chamber inside the mountain: the area below the face stays fogged while rock closes the face", rfFog0 == RfFogged(rfArea, map) && rfFog0 >= 8,
                        $"face {rfTunF} L{Levels.LevelOf(rfTunF, map)} {rfTunF.GetEdifice(map)?.def.defName}, area round {rfTunLand} (L{Levels.LevelOf(rfTunLand, map)}): {rfFog0} of {rfArea.Count} fogged");
                    map.designationManager.AddDesignation(new Designation(rfTunF, DesignationDefOf.Mine));
                    Info($"E3 {rfPawn} mines the face {rfTunF} sideways (open air beyond it, dir {rfTunD})");
                    MuRun(300, 71720, () => rfTunF.GetEdifice(map) == null, "rf_e3_mined");
                    return true;
                }

                case "rf_e3_mined":
                    Wait(3, "rf_e3_check");
                    return true;

                case "rf_e3_check":
                {
                    int fog = RfFogged(rfArea, map);
                    bool mined = rfTunF.GetEdifice(map) == null;
                    Result("E3 the colonist mined through the face: the area below the opening is revealed", mined && fog == 0, $"face mined {mined}; area cells fogged {fog} of {rfArea.Count} (before {rfFog0})");
                    RfAudit(map, "E3");
                    MuShot(map, $"reachfog_e3_tunnel_L{Levels.LevelOf(rfTunLand, map)}", Levels.LevelOf(rfTunLand, map), ProjectZ.GroundCellOf(rfTunF, map), 14f, "rf_e3_shot2");
                    return true;
                }

                case "rf_e3_shot2":
                    MuShot(map, $"reachfog_e3_tunnel_L{Levels.LevelOf(rfTunF, map)}", Levels.LevelOf(rfTunF, map), ProjectZ.GroundCellOf(rfTunF, map), 14f, "rf_e3_end");
                    return true;

                case "rf_e3_end":
                    RfNext("rf_e4");
                    return true;

                // ------------------------------------------------------------ E4 a slab edge over a walled pen; a door
                case "rf_e4":
                {
                    RfFixture(map);
                    IntVec3 o = rfPen;
                    rfArea.Clear();
                    rfArea2.Clear();
                    for (int dz = 0; dz < 5; dz++)
                    {
                        for (int dx = 0; dx < 9; dx++)
                        {
                            IntVec3 c = o + new IntVec3(dx, 0, dz);
                            bool border = dx == 0 || dx == 8 || dz == 0 || dz == 4;
                            if (border || dx == 4 && dz != 2)
                            {
                                RfSpawn(ThingDefOf.Wall, c, map, Rot4.North);
                            }
                            else if (dx == 4)
                            {
                                RfSpawn(ThingDefOf.Door, c, map, Rot4.North);
                            }
                            else
                            {
                                (dx < 4 ? rfArea : rfArea2).Add(c);
                            }
                        }
                    }
                    HfCarve(map, rfArea.Concat(rfArea2), false);
                    // the slab over the west wall (level 1), carried by it
                    for (int dz = 0; dz < 5; dz++)
                    {
                        map.terrainGrid.SetTerrain(Levels.Cell(o + new IntVec3(0, 0, dz), 1, map), PZDefOf.PZ_UpperFloor);
                    }
                    Wait(3, "rf_e4_sealed");
                    return true;
                }

                case "rf_e4_sealed":
                {
                    ReachFogMap.For(map)?.Forget();
                    ReachFog.Run(map);
                    int a = RfFogged(rfArea, map), b = RfFogged(rfArea2, map);
                    Result("E4 two sealed pens on the ground, nobody on the slab: both stay fogged", a == rfArea.Count && b == rfArea2.Count, $"pen A fogged {a} of {rfArea.Count}, pen B {b} of {rfArea2.Count}");
                    rfBefore = RfSnap(map);
                    Pawn p = Colonists(map).OrderBy(x => x.thingIDNumber).First(x => !x.Downed);
                    IntVec3 on = Levels.Cell(rfPen + new IntVec3(0, 0, 2), 1, map);
                    Info($"E4 {p} arrives on the slab at {on} (L1, {on.GetTerrain(map).defName}, standable {on.Standable(map)})");
                    Teleport(p, on);
                    Draft(p, true);
                    rfPawn = p;
                    Wait(3, "rf_e4_check");
                    return true;
                }

                case "rf_e4_check":
                {
                    int a = RfFogged(rfArea, map), b = RfFogged(rfArea2, map);
                    Building_Door door = (rfPen + new IntVec3(4, 0, 2)).GetEdifice(map) as Building_Door;
                    Result("E4 a colonist on the slab over the pen wall: the pen under its edge is revealed", Levels.LevelOf(rfPawn.Position, map) == 1 && a == 0,
                        $"{rfPawn} at {rfPawn.Position} L{Levels.LevelOf(rfPawn.Position, map)}; pen A fogged {a} of {rfArea.Count}");
                    Result("E4 the pen beside it behind a closed door (under no edge) stays fogged", door != null && !door.Open && b == rfArea2.Count, $"door open {door?.Open}; pen B fogged {b} of {rfArea2.Count}");
                    RfAudit(map, "E4");
                    MuShot(map, "reachfog_e4_slab_L0", 0, rfPen + new IntVec3(4, 0, 2), 10f, "rf_e4_end");
                    return true;
                }

                case "rf_e4_end":
                    RfNext("rf_r3");
                    return true;

                // ------------------------------------------------------------ R3 stairs onto a fogged plateau
                case "rf_r3":
                {
                    RfFixture(map);
                    ReachFog.Run(map);
                    rfArea.Clear();
                    rfArea.AddRange(RfFlat(rfExit, map, 4000));
                    rfBefore = RfSnap(map);
                    // the solid end's rock first (vanilla Mine on the ground: the exit's top stays)
                    IntVec3 solid = StairShape.Solid(rfStairs, rfRot);
                    if (solid.GetFirstMineable(map) != null)
                    {
                        map.designationManager.AddDesignation(new Designation(solid, DesignationDefOf.Mine));
                        MuCrew(map, WorkTypeDefOf.Mining, 2);
                        Info($"R3 the solid end {solid} is rock: mined first");
                        MuRun(200, 71720, () => solid.GetFirstMineable(map) == null, "rf_r3_place");
                        return true;
                    }
                    stage = "rf_r3_place";
                    return true;
                }

                case "rf_r3_place":
                {
                    IntVec3 solid = StairShape.Solid(rfStairs, rfRot);
                    Result("R3 the stairs' solid end dug on the ground (vanilla Mine), the exit above it still the plateau's rim top", solid.GetFirstMineable(map) == null && TmTop(rfExit, map),
                        $"solid {solid} rock {solid.GetFirstMineable(map)?.def.defName ?? "none"}, exit {rfExit} {rfExit.GetTerrain(map)?.defName}");
                    MuWood(map, rfStairs - rfRot.FacingCell * 3);
                    GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, rfStairs, map, rfRot, Faction.OfPlayer, ThingDefOf.WoodLog);
                    MuCrew(map, WorkTypeDefOf.Construction, 3);
                    Info($"R3 stairs {rfStairs} facing {rfRot}, exit {rfExit}; plateau {rfArea.Count} cells, fogged {RfFogged(rfArea, map)}");
                    MuRun(400, 71730, () => MuStairs(map, rfStairs) != null, "rf_r3_built");
                    return true;
                }

                case "rf_r3_built":
                {
                    bool built = MuStairs(map, rfStairs) != null;
                    int up = Colonists(map).Count(p => ProjectZ.FloorOf(p.Position, map) >= 1);
                    Wait(2, "rf_r3_check");
                    Info($"R3 built {built}, colonists upstairs {up}");
                    return true;
                }

                case "rf_r3_check":
                {
                    bool built = MuStairs(map, rfStairs) != null;
                    int fog = RfFogged(rfArea, map);
                    Result("R3 colonists built stairs onto a fogged plateau's rim: on completion the plateau is revealed", built && fog == 0, $"built {built}; plateau cells still fogged {fog} of {rfArea.Count}");
                    RfAudit(map, "R3");
                    MuShot(map, "reachfog_r3_stairs_L1", 1, rfExit, 18f, "rf_r3_end");
                    return true;
                }

                case "rf_r3_end":
                    RfNext("rf_r4");
                    return true;

                // ------------------------------------------------------------ R4 a plateau nobody can reach or see
                case "rf_r4":
                {
                    RfFixture(map);
                    ReachFog.Run(map);
                    rfArea.Clear();
                    rfArea.AddRange(RfFlat(rfLone, map, 2000));
                    rfFog0 = RfFogged(rfArea, map);
                    rfBefore = RfSnap(map);
                    // real triggers: colonists arrive (teleports on the ground), a wall is built; and a full rescan
                    List<Pawn> cols = Colonists(map).OrderBy(x => x.thingIDNumber).ToList();
                    foreach (Pawn p in cols)
                    {
                        IntVec3 to = CellFinder.StandableCellNear(p.Position, map, 3f);
                        if (to.IsValid)
                        {
                            Teleport(p, to);
                        }
                    }
                    ReachFogMap.For(map)?.Forget();
                    MuRun(10, 71740, () => false, "rf_r4_check");
                    return true;
                }

                case "rf_r4_check":
                {
                    int fog = RfFogged(rfArea, map);
                    List<IntVec3> from = RfPlayers(map).Select(p => p.Position).ToList();
                    bool reach = RfReach(map, rfLone, from);
                    int changed = RfChanged(map, rfBefore);
                    Result("R4 a plateau nobody can reach or see from an edge stays fogged after runs", fog == rfFog0 && changed == 0 && !reach,
                        $"plateau round {rfLone}: {fog} of {rfArea.Count} fogged (before {rfFog0}: its rim seen since generation), reachable {reach}; fog cells changed anywhere {changed}; runs {ReachFog.Runs}");
                    MuShot(map, "reachfog_r4_unreached_L1", 1, rfLone, 18f, "rf_r4_end");
                    return true;
                }

                case "rf_r4_end":
                    RfNext("rf_r5");
                    return true;

                // ------------------------------------------------------------ R5 a cave sealed by rock
                case "rf_r5":
                {
                    RfFixture(map);
                    IntVec3 c0 = rfCaveE + rfCaveD * 4;
                    rfArea.Clear();
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            rfArea.Add(c0 + new IntVec3(dx, 0, dz));
                        }
                    }
                    HfCarve(map, rfArea, false);
                    ReachFog.Run(map);
                    rfBefore = RfSnap(map);
                    List<Pawn> crew = Colonists(map).OrderBy(x => x.thingIDNumber).Where(x => !x.Downed && !x.WorkTypeIsDisabled(WorkTypeDefOf.Mining)).Take(1).ToList();
                    Teleport(crew[0], rfCaveE);
                    MdWork(map, crew, WorkTypeDefOf.Mining);
                    map.designationManager.AddDesignation(new Designation(rfCaveE + rfCaveD, DesignationDefOf.Mine));
                    Info($"R5 cave of {rfArea.Count} cells round {c0} (fogged {RfFogged(rfArea, map)}), sealed by rock; {crew[0]} mines {rfCaveE + rfCaveD}");
                    MuRun(200, 71750, () => (rfCaveE + rfCaveD).GetEdifice(map) == null, "rf_r5_first");
                    return true;
                }

                case "rf_r5_first":
                {
                    int fog = RfFogged(rfArea, map);
                    Wait(2, "rf_r5_first2");
                    Info($"R5 first rock mined {(rfCaveE + rfCaveD).GetEdifice(map) == null}; cave fogged {fog}");
                    return true;
                }

                case "rf_r5_first2":
                {
                    int fog = RfFogged(rfArea, map);
                    Result("R5 colonists mined towards a cave sealed by rock (under no edge): it stays fogged while rock still seals it", (rfCaveE + rfCaveD).GetEdifice(map) == null && fog == rfArea.Count,
                        $"cave cells fogged {fog} of {rfArea.Count}; runs {ReachFog.Runs}");
                    map.designationManager.AddDesignation(new Designation(rfCaveE + rfCaveD * 2, DesignationDefOf.Mine));
                    MuRun(200, 71751, () => (rfCaveE + rfCaveD * 2).GetEdifice(map) == null, "rf_r5_second");
                    return true;
                }

                case "rf_r5_second":
                    Wait(2, "rf_r5_check");
                    return true;

                case "rf_r5_check":
                {
                    int fog = RfFogged(rfArea, map);
                    int ceil = rfArea.Count(c => { IntVec3 u = FloorRoof.Above(c, map); return u.IsValid && map.fogGrid.IsFogged(u); });
                    Result("R5 they broke through: the cave is revealed, its ceiling too", fog == 0 && ceil == 0, $"cave cells fogged {fog}, ceilings fogged {ceil} of {rfArea.Count}");
                    RfAudit(map, "R5");
                    MuShot(map, "reachfog_r5_cave_L0", 0, rfCaveE + rfCaveD * 3, 12f, "rf_r5_end");
                    return true;
                }

                case "rf_r5_end":
                    RfNext("rf_r6");
                    return true;

                // ------------------------------------------------------------ R6 a room behind a closed door
                case "rf_r6":
                {
                    RfFixture(map);
                    IntVec3 c0 = rfRoomE + rfRoomD * 3;
                    rfArea.Clear();
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            rfArea.Add(c0 + new IntVec3(dx, 0, dz));
                        }
                    }
                    IntVec3 door = rfRoomE + rfRoomD;
                    HfCarve(map, new[] { door }, true);
                    Thing d = ThingMaker.MakeThing(ThingDefOf.Door, ThingDefOf.WoodLog);
                    d.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(d, door, map, Rot4.North);
                    HfCarve(map, rfArea, false);
                    // the room: an ancient sealed pocket; a door in its only wall gap
                    Wait(3, "rf_r6_closed");
                    return true;
                }

                case "rf_r6_closed":
                {
                    ReachFogMap.For(map)?.Forget();
                    ReachFog.Run(map);
                    int fog = RfFogged(rfArea, map);
                    Building_Door door = (rfRoomE + rfRoomD).GetEdifice(map) as Building_Door;
                    Result("R6 a room behind a closed door stays fogged (the walk stops at the door)", door != null && !door.Open && fog == rfArea.Count,
                        $"door {door} open {door?.Open}, room cells fogged {fog} of {rfArea.Count}");
                    rfBefore = RfSnap(map);
                    Pawn p = Colonists(map).OrderBy(x => x.thingIDNumber).First(x => !x.Downed);
                    Teleport(p, rfRoomE);
                    Draft(p, true);
                    rfPawn = p;
                    p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Goto, rfRoomE + rfRoomD), JobTag.DraftedOrder);
                    MuRun(60, 71760, () => rfPawn.Position == rfRoomE + rfRoomD, "rf_r6_opened");
                    return true;
                }

                case "rf_r6_opened":
                    Wait(2, "rf_r6_check");
                    return true;

                case "rf_r6_check":
                {
                    int fog = RfFogged(rfArea, map);
                    Result("R6 a drafted colonist ordered into the doorway opened the door: the room is revealed", rfPawn.Position == rfRoomE + rfRoomD && fog == 0,
                        $"{rfPawn} at {rfPawn.Position}; room cells fogged {fog} of {rfArea.Count}");
                    RfAudit(map, "R6");
                    MuShot(map, "reachfog_r6_room_L0", 0, rfRoomE + rfRoomD * 2, 12f, "rf_r6_end");
                    return true;
                }

                case "rf_r6_end":
                    RfNext("rf_r7");
                    return true;

                // ------------------------------------------------------------ R7 cost
                case "rf_r7":
                {
                    RfFixture(map);
                    // (a colonist on the E2 top: the cascade's edges are part of every run measured)
                    Pawn top = Colonists(map).OrderBy(x => x.thingIDNumber).First(x => !x.Downed);
                    Teleport(top, rfCasTop);
                    Draft(top, true);
                    ReachFog.Run(map);
                    double full = 0, walk = 0, wall = 0, roof = 0, seenMs = 0;
                    int regions = 0, seen = 0, edgeRegions = 0, computes = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        ReachFogMap.For(map)?.Forget();
                        int c0 = ReachFog.EdgeComputes;
                        ReachFog.Run(map);
                        full += ReachFog.LastMs;
                        regions = ReachFog.LastRegions;
                        seen = ReachFog.LastSeen;
                        edgeRegions = ReachFog.LastEdgeRegions;
                        computes = ReachFog.EdgeComputes - c0;
                    }
                    for (int k = 0; k < 10; k++)
                    {
                        ReachFog.Run(map);
                        walk += ReachFog.LastMs;
                        seenMs += ReachFog.LastSeenMs;
                    }
                    // a wall built on the ground (regions rebuilt, the jump index dirty): the run after it
                    List<Pawn> cols = Colonists(map).Where(p => p != top).ToList();
                    IntVec3 home = new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z));
                    int walls = 0;
                    foreach (IntVec3 c in GenRadial.RadialCellsAround(home, 20f, false).Where(c => c.InBounds(map) && c.Standable(map) && c.GetFirstPawn(map) == null && c.GetFirstItem(map) == null && !c.Roofed(map)).Take(3).ToList())
                    {
                        GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog), c, map);
                        map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                        ReachFog.Run(map);
                        wall += ReachFog.LastMs;
                        walls++;
                    }
                    // a roof built on the ground (a column changed): the run after it
                    int roofs = 0, rc = ReachFog.EdgeComputes;
                    foreach (IntVec3 c in GenRadial.RadialCellsAround(home, 25f, false).Where(c => c.InBounds(map) && c.Standable(map) && !c.Roofed(map)).Skip(10).Take(3).ToList())
                    {
                        map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                        ReachFog.Run(map);
                        roof += ReachFog.LastMs;
                        roofs++;
                    }
                    rc = ReachFog.EdgeComputes - rc;
                    full /= 3;
                    walk /= 10;
                    seenMs /= 10;
                    wall /= Math.Max(1, walls);
                    roof /= Math.Max(1, roofs);
                    Info($"PERF reach fog on {map.Size.x}x{map.Size.z} ({map.cellIndices.NumGridCells} cells, {regions} regions reached, {seen} seen, {edgeRegions} with edges, {computes} edge lists worked out in a full run): full run {full:0.00} ms, walk only {walk:0.00} ms (of it the seen closure {seenMs:0.00} ms), after a wall {wall:0.00} ms, after a roof {roof:0.00} ms ({rc} edge lists redone for {roofs} roofs); max of all runs {ReachFog.MaxMs:0.00} ms; edge lists remembered {ReachFogMap.For(map)?.EdgesKnown}");
                    Result("R7 cost: a full run (as after a load) under 100 ms, a walk-only run, the run after a wall and after a roof under 5 ms", full < 100 && walk < 5 && wall < 5 && roof < 5,
                        $"full {full:0.00} ms, walk {walk:0.00} ms, after a wall {wall:0.00} ms, after a roof {roof:0.00} ms");
                    RfNext(null);
                    return true;
                }

                // ------------------------------------------------------------ the player's save: the load-time pass
                case "rf_u_off":
                {
                    RfFixture(map);
                    rfBefore = RfSnap(map);
                    AnnexMapComponent a = ProjectZ.Annex(map);
                    Info($"loaded {rfUserSave} WITHOUT the rule: map {map.Size}, levels {a?.MinLevel}..{a?.MaxLevel}, tall mountains {Mountains.On(map)}; player pawns {string.Join(", ", RfPlayers(map).Select(p => $"{p} {p.Position} L{Levels.LevelOf(p.Position, map)}"))}");
                    HfFogStats(map, "save, rule off");
                    ReachFog.Off = false;
                    ReachFog.MaxMs = 0;
                    stage = "rf_u_on";
                    pendingLoad = rfUserSave;
                    return true;
                }

                case "rf_u_on":
                {
                    RfFixture(map);
                    if (ReachFog.Runs == 0)
                    {
                        return true;   // (the load-time run comes on the first frames)
                    }
                    int newly = 0;
                    for (int i = 0; i < rfBefore.Length && i < map.cellIndices.NumGridCells; i++)
                    {
                        newly += rfBefore[i] && !map.fogGrid.IsFogged(i) ? 1 : 0;
                    }
                    Info($"loaded {rfUserSave} WITH the rule: load-time run {ReachFog.MaxMs:0.0} ms ({ReachFog.LastRegions} regions reached, {ReachFog.LastSeen} seen), {newly} cell(s) newly revealed");
                    HfFogStats(map, "save, rule on");
                    RfAudit(map, "U the load-time pass on the player's save:");
                    // cost on the player's map
                    double full = 0, walk = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        ReachFogMap.For(map)?.Forget();
                        ReachFog.Run(map);
                        full += ReachFog.LastMs;
                    }
                    for (int k = 0; k < 10; k++)
                    {
                        ReachFog.Run(map);
                        walk += ReachFog.LastMs;
                    }
                    Info($"PERF on {rfUserSave} ({map.Size.x}x{map.Size.z}, {ReachFog.LastRegions} regions reached, {ReachFog.LastSeen} seen): full run {full / 3:0.00} ms, walk only {walk / 10:0.00} ms");
                    SetHour(map, 12);
                    rfShot = 0;
                    stage = "rf_u_shots";
                    return true;
                }

                case "rf_u_shots":
                {
                    AnnexMapComponent a = ProjectZ.Annex(map);
                    if (rfShot > a.MaxLevel)
                    {
                        RfNext(null);
                        return true;
                    }
                    List<Pawn> cols = Colonists(map);
                    IntVec3 mid = cols.Count == 0 ? map.Center : new IntVec3((int)cols.Average(p => ProjectZ.GroundCellOf(p.Position, map).x), 0, (int)cols.Average(p => ProjectZ.GroundCellOf(p.Position, map).z));
                    int f = rfShot++;
                    MuShot(map, $"reachfog_user_L{f}", f, mid, 30f, "rf_u_shots");
                    return true;
                }
            }
            return false;
        }
    }
}
