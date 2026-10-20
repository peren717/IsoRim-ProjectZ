using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ.Patches
{
    // The floors' graph (isorim-pz-pathing-redesign.md, review 2 §9: "pathing = vanilla + one vertical axis").
    //
    // Two levels:
    //   PARTS: per floor (level), the regions joined by the game's own region links, every passable region counted (doors
    //     open, no traveller): what a floor's walls cut apart. Numbered lazily by a flood from the region asked about and
    //     kept per floor until a region of that floor is made again (LevelChanged, from RegionMaker). A part never spans
    //     floors.
    //   NODES: the stairs' ends, three per stairs link i: Low (the ramp's low end L0, floor k), High (its high end H,
    //     floor k) and Top (the exit E, floor k + 1). Floors are joined only by the stair step High <-> Top.
    //   COMPONENTS: parts joined over usable stairs (union-find): two places in different components are never joined,
    //     for any traveller, so reachability says no at once (O(1)) and nothing searches.
    // What a given traveller can do (doors it may open, fences, danger) is asked of a SECOND Reachability instance with
    // the stairs left out (Reach2): the game's own search on one floor, cached the game's way, cleared with the map's
    // cache. The main instance must never be asked from inside its own search (H-e), so the floor questions go there.
    //
    // Also here: per-traveller leg costs (250 ticks), the "no route" memo, the stairs' region index for region searches,
    // the cut test for taking up the last stairs (K2), dig parts for traversal modes that break walls, and the queue of
    // deferred path requests (a per-tick budget).
    public sealed class RouteGraph : MapComponent
    {
        public RouteGraph(Map map) : base(map)
        {
        }

        private static Map lastMap;
        private static RouteGraph last;

        // The graph of a map with floors, else null (last map cached).
        public static RouteGraph For(Map map)
        {
            if (map == null || ProjectZ.Annex(map) == null)
            {
                return null;
            }
            if (!ReferenceEquals(map, lastMap))
            {
                last = map.components == null ? null : map.GetComponent<RouteGraph>();
                lastMap = map;
            }
            return last;
        }

        // ---------------------------------------------------------------------------------------------- versions

        private int[] levelVersion = new int[0];
        private int levelLo;
        // any floor's version or the stairs changed (everything derived from parts and nodes is keyed by it)
        public int anyVersion = 1;
        public int stairsVersion = 1;

        private int LevelIndex(int L)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int lo = a?.MinLevel ?? 0, hi = a?.MaxLevel ?? 0;
            if (levelVersion.Length != hi - lo + 1 || levelLo != lo)
            {
                levelVersion = new int[hi - lo + 1];
                levelParts = null;
                levelLo = lo;
                anyVersion++;
            }
            int i = L - levelLo;
            return i >= 0 && i < levelVersion.Length ? i : -1;
        }

        public int LevelVersion(int L)
        {
            int i = LevelIndex(L);
            return i < 0 ? 0 : levelVersion[i];
        }

        // A region of level L was made (its floor's walls, doors or walkability changed).
        public void LevelChanged(int L)
        {
            int i = LevelIndex(L);
            if (i >= 0)
            {
                levelVersion[i]++;
            }
            anyVersion++;
        }

        // Stairs registered or gone (StairGrid).
        public void StairsChanged()
        {
            stairsVersion++;
            anyVersion++;
        }

        public static int LevelOfRegion(Region r, Map map) => Levels.LevelOf(new IntVec3(r.extentsClose.minX, 0, r.extentsClose.minZ), map);

        // ---------------------------------------------------------------------------------------------- parts

        private sealed class LevelParts
        {
            public int version = -1;
            public readonly Dictionary<Region, int> part = new Dictionary<Region, int>();
        }

        private LevelParts[] levelParts;
        private int nextPart = 1;
        private readonly HashSet<int> edgeParts = new HashSet<int>();
        private readonly Queue<Region> flood = new Queue<Region>();

        // A part id carries its level: (n << 6) | (L + 32).
        public static int LevelOfPart(int part) => (part & 63) - 32;

        private LevelParts PartsOf(int L)
        {
            int i = LevelIndex(L);
            if (i < 0)
            {
                return null;
            }
            if (levelParts == null || levelParts.Length != levelVersion.Length)
            {
                levelParts = new LevelParts[levelVersion.Length];
            }
            LevelParts lp = levelParts[i] ??= new LevelParts();
            if (lp.version != levelVersion[i])
            {
                lp.part.Clear();
                lp.version = levelVersion[i];
            }
            return lp;
        }

        // The part of a region (-1: none: null, stale or impassable).
        public int PartOf(Region r)
        {
            if (r == null || !r.valid || !r.type.Passable())
            {
                return -1;
            }
            int L = LevelOfRegion(r, map);
            if (L == Levels.None)
            {
                return -1;
            }
            LevelParts lp = PartsOf(L);
            if (lp == null)
            {
                return -1;
            }
            if (lp.part.TryGetValue(r, out int id))
            {
                return id;
            }
            if (nextPart > (1 << 24))
            {
                ResetParts();
                lp = PartsOf(L);
            }
            id = (nextPart++ << 6) | (L + 32);
            bool edge = false;
            flood.Clear();
            flood.Enqueue(r);
            lp.part[r] = id;
            while (flood.Count > 0)
            {
                Region x = flood.Dequeue();
                if (L == 0 && !edge && x.District != null && x.District.TouchesMapEdge)
                {
                    edge = true;
                }
                List<RegionLink> links = x.links;
                for (int i = 0; i < links.Count; i++)
                {
                    Region o = links[i].GetOtherRegion(x);
                    if (o != null && o.valid && o.type.Passable() && !lp.part.ContainsKey(o))
                    {
                        lp.part[o] = id;
                        flood.Enqueue(o);
                    }
                }
            }
            if (edge)
            {
                if (edgeParts.Count > 20000)
                {
                    edgeParts.Clear();
                    LevelChanged(0);   // (the ground's parts are numbered again, their edge marks with them)
                    return PartOf(r);
                }
                edgeParts.Add(id);
            }
            return id;
        }

        private void ResetParts()
        {
            nextPart = 1;
            edgeParts.Clear();
            for (int i = 0; i < levelVersion.Length; i++)
            {
                levelVersion[i]++;
            }
            anyVersion++;
        }

        // The part of the region at a cell (-1: none).
        public int PartAt(IntVec3 c)
        {
            return c.IsValid && c.InBounds(map) ? PartOf(map.regionGrid.GetValidRegionAt_NoRebuild(c)) : -1;
        }

        // A ground part touching the map edge (the way out of the map).
        public bool EdgePart(int part) => edgeParts.Contains(part);

        // ---------------------------------------------------------------------------------------------- nodes

        public const int Low = 0, High = 1, Top = 2;

        public struct Node
        {
            public int link;
            public int kind;
            public IntVec3 cell;
            public int level;
            public IntVec3 ground;
        }

        public readonly List<Node> nodes = new List<Node>();
        public readonly List<VLink> links = new List<VLink>();
        private int nodesFor = -1;
        // either end of a step -> the other end (H <-> E), for the touch rule (StairTouch.PairCell)
        private readonly Dictionary<IntVec3, IntVec3> pair = new Dictionary<IntVec3, IntVec3>();
        // ramp cells (L0, L1, H) -> link
        private readonly Dictionary<IntVec3, int> rampOf = new Dictionary<IntVec3, int>();

        public void EnsureNodes()
        {
            if (nodesFor == stairsVersion)
            {
                return;
            }
            nodesFor = stairsVersion;
            nodes.Clear();
            links.Clear();
            pair.Clear();
            rampOf.Clear();
            StairGrid g = map.GetComponent<StairGrid>();
            if (g == null)
            {
                return;
            }
            for (int i = 0; i < g.links.Count; i++)
            {
                VLink l = g.links[i];
                links.Add(l);
                // (a ladder has no ramp: its Low and High nodes are both the foot)
                AddNode(i, Low, l.low);
                AddNode(i, High, l.bottom);
                AddNode(i, Top, l.top);
                pair[l.bottom] = l.top;
                pair[l.top] = l.bottom;
                if (l.HasRamp)
                {
                    rampOf[l.low] = i;
                    rampOf[l.mid] = i;
                    rampOf[l.bottom] = i;
                }
            }
        }

        private void AddNode(int link, int kind, IntVec3 c)
        {
            nodes.Add(new Node { link = link, kind = kind, cell = c, level = Levels.LevelOf(c, map), ground = Levels.Ground(c, map) });
        }

        public int NodeCount
        {
            get
            {
                EnsureNodes();
                return nodes.Count;
            }
        }

        // The other end of a link step at c, or Invalid.
        public IntVec3 PairCell(IntVec3 c)
        {
            EnsureNodes();
            return pair.TryGetValue(c, out IntVec3 o) ? o : IntVec3.Invalid;
        }

        // The stairs link whose ramp holds c, or -1.
        public int RampLink(IntVec3 c)
        {
            EnsureNodes();
            return rampOf.TryGetValue(c, out int i) ? i : -1;
        }

        public IntVec3 MidCell(int link) => links[link].mid;

        // The link of a stairs (or ladder) thing, or -1.
        public int LinkOf(Thing s)
        {
            EnsureNodes();
            for (int i = 0; i < links.Count; i++)
            {
                if (ReferenceEquals(links[i].thing, s))
                {
                    return i;
                }
            }
            return -1;
        }

        // The link whose exit is c, or -1.
        public int LinkOfTop(IntVec3 c)
        {
            EnsureNodes();
            for (int i = 0; i < links.Count; i++)
            {
                if (links[i].top == c)
                {
                    return i;
                }
            }
            return -1;
        }

        // A link can be walked: its thing stands, and the ramp and both ends can be stood on (StairGrid.UsableIn).
        public bool Usable(int link) => StairGrid.UsableIn(links[link], map);

        // THE link test for a traveller: usable, and the regions of its cells let it in as the game's region search would
        // (Region.Allows: a door it may not open, a fence, danger).
        public bool Passable(int link, TraverseParms tp) => StairGrid.PassableIn(links[link], tp, map);

        // ---------------------------------------------------------------------------------------------- node parts

        private int[] nodePart = new int[0];
        private int nodePartFor = -1;
        private readonly Dictionary<int, List<int>> partNodes = new Dictionary<int, List<int>>();
        private static readonly List<int> NoNodes = new List<int>();

        public void EnsureNodeParts()
        {
            EnsureNodes();
            if (nodePartFor == anyVersion)
            {
                return;
            }
            int n = nodes.Count;
            if (nodePart.Length != n)
            {
                nodePart = new int[n];
            }
            partNodes.Clear();
            for (int i = 0; i < n; i++)
            {
                nodePart[i] = PartAt(nodes[i].cell);
            }
            for (int i = 0; i < n; i++)
            {
                if (nodes[i].kind == High || nodePart[i] < 0 || !Usable(nodes[i].link))
                {
                    continue;
                }
                if (!partNodes.TryGetValue(nodePart[i], out List<int> l))
                {
                    partNodes[nodePart[i]] = l = new List<int>();
                }
                l.Add(i);
            }
            // (PartAt may have numbered a floor again: everything here is for the version now)
            nodePartFor = anyVersion;
        }

        public int NodePart(int node) => nodePart[node];

        // The Low and Top nodes of usable stairs in a part.
        public List<int> NodesIn(int part)
        {
            return part >= 0 && partNodes.TryGetValue(part, out List<int> l) ? l : NoNodes;
        }

        // ---------------------------------------------------------------------------------------------- components

        private readonly Dictionary<int, int> uf = new Dictionary<int, int>();
        private int compFor = -1;

        public void EnsureComps()
        {
            EnsureNodeParts();
            if (compFor == anyVersion)
            {
                return;
            }
            uf.Clear();
            for (int i = 0; i < links.Count; i++)
            {
                if (!Usable(i))
                {
                    continue;
                }
                int a = nodePart[i * 3], h = nodePart[i * 3 + 1], b = nodePart[i * 3 + 2];
                if (a >= 0 && h >= 0)
                {
                    Union(uf, a, h);
                }
                if (h >= 0 && b >= 0)
                {
                    Union(uf, h, b);
                }
            }
            compFor = anyVersion;
        }

        private static int FindRoot(Dictionary<int, int> d, int x)
        {
            int r = x;
            while (d.TryGetValue(r, out int p) && p != r)
            {
                r = p;
            }
            while (x != r && d.TryGetValue(x, out int p) && p != r)
            {
                d[x] = r;
                x = p;
            }
            return r;
        }

        private static void Union(Dictionary<int, int> d, int a, int b)
        {
            int ra = FindRoot(d, a), rb = FindRoot(d, b);
            if (ra != rb)
            {
                d[ra] = rb;
                if (!d.ContainsKey(rb))
                {
                    d[rb] = rb;
                }
            }
        }

        // The component of a part (a part on no stairs is its own).
        public int CompOf(int part)
        {
            EnsureComps();
            return part < 0 ? int.MinValue : FindRoot(uf, part);
        }

        // ---------------------------------------------------------------------------------------------- K2: the cut

        private int cutFor = -1;
        private readonly Dictionary<int, int> cutCache = new Dictionary<int, int>();

        // Taking stairs link i up (or the floor of its exit): 0 when the floors stay joined over other stairs (or neither
        // side reaches the map edge, or both do); +1 when its TOP side would be cut off from the map edge (work from the
        // low side), -1 when its LOW side would be (work from the top side).
        public int CutSide(int link)
        {
            EnsureComps();
            if (link < 0 || link >= links.Count || !Usable(link))
            {
                return 0;
            }
            if (cutFor != anyVersion)
            {
                cutCache.Clear();
                cutFor = anyVersion;
            }
            if (cutCache.TryGetValue(link, out int known))
            {
                return known;
            }
            Dictionary<int, int> d = new Dictionary<int, int>();
            for (int i = 0; i < links.Count; i++)
            {
                if (i == link || !Usable(i))
                {
                    continue;
                }
                int a = nodePart[i * 3], h = nodePart[i * 3 + 1], b = nodePart[i * 3 + 2];
                if (a >= 0 && h >= 0)
                {
                    Union(d, a, h);
                }
                if (h >= 0 && b >= 0)
                {
                    Union(d, h, b);
                }
            }
            int lowPart = nodePart[link * 3 + 1], topPart = nodePart[link * 3 + 2];
            int side = 0;
            if (lowPart >= 0 && topPart >= 0 && FindRoot(d, lowPart) != FindRoot(d, topPart))
            {
                int lowRoot = FindRoot(d, lowPart), topRoot = FindRoot(d, topPart);
                bool lowEdge = false, topEdge = false;
                foreach (int p in edgeParts)
                {
                    int r = FindRoot(d, p);
                    lowEdge |= r == lowRoot;
                    topEdge |= r == topRoot;
                }
                // (an edge part not yet numbered: the ground part of the low end is numbered by EnsureNodeParts already)
                side = lowEdge && !topEdge ? 1 : topEdge && !lowEdge ? -1 : 0;
            }
            cutCache[link] = side;
            return side;
        }

        // ---------------------------------------------------------------------------------------------- dig parts

        // Traversal modes that break walls (sappers, breachers, a pawn digging out): per floor, the cells joined over
        // cells they can stand on or break (the game's own cell test in CheckCellBasedReachability), numbered by a cell
        // flood per floor version. Their components over the stairs say whether such a traveller can get from one floor
        // to another at all.
        private sealed class DigLevel
        {
            public int version = -1;
            public int[] part;
        }

        private readonly Dictionary<int, DigLevel> digLevels = new Dictionary<int, DigLevel>();
        private int nextDig = 1;
        private readonly Queue<IntVec3> digFlood = new Queue<IntVec3>();

        public static int DigKey(TraverseMode m) => m == TraverseMode.PassAllDestroyablePlayerOwnedThings ? 1 : m == TraverseMode.PassAllDestroyableThingsNotWater ? 2 : 0;

        public static bool DigMode(TraverseMode m) => m == TraverseMode.PassAllDestroyableThings || m == TraverseMode.PassAllDestroyablePlayerOwnedThings || m == TraverseMode.PassAllDestroyableThingsNotWater;

        private bool DigPassable(IntVec3 c, int key, PathGrid pg)
        {
            if (key == 2 && c.GetTerrain(map).IsWater)
            {
                return false;
            }
            if (pg.WalkableFast(c))
            {
                return true;
            }
            Building e = c.GetEdifice(map);
            if (e == null || !PathUtility.IsDestroyable(e))
            {
                return false;
            }
            return key != 1 || e.Faction == Faction.OfPlayer;
        }

        // The dig part of a cell for mode key (-1: none).
        public int DigPartAt(IntVec3 c, int key)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null || !c.IsValid || !c.InBounds(map))
            {
                return -1;
            }
            int L = Levels.LevelOf(c, map);
            if (L == Levels.None)
            {
                return -1;
            }
            int dk = (L + 32) * 4 + key;
            if (!digLevels.TryGetValue(dk, out DigLevel dl))
            {
                digLevels[dk] = dl = new DigLevel();
            }
            int ver = LevelVersion(L);
            int w = map.Size.x, h = a.groundZ;
            if (dl.version != ver || dl.part == null)
            {
                if (dl.part == null || dl.part.Length != w * h)
                {
                    dl.part = new int[w * h];
                }
                else
                {
                    Array.Clear(dl.part, 0, dl.part.Length);
                }
                dl.version = ver;
            }
            IntVec3 g = Levels.Ground(c, map);
            int gi = g.z * w + g.x;
            if (dl.part[gi] != 0)
            {
                return dl.part[gi];
            }
            PathGrid pg = map.pathing.Normal.pathGrid;
            if (!DigPassable(c, key, pg))
            {
                return -1;
            }
            int id = nextDig++;
            int shift = Levels.Shift(map, L);
            digFlood.Clear();
            digFlood.Enqueue(g);
            dl.part[gi] = id;
            while (digFlood.Count > 0)
            {
                IntVec3 x = digFlood.Dequeue();
                for (int d = 0; d < 8; d++)
                {
                    IntVec3 n = x + GenAdj.AdjacentCells[d];
                    if (n.x < 0 || n.z < 0 || n.x >= w || n.z >= h)
                    {
                        continue;
                    }
                    int ni = n.z * w + n.x;
                    if (dl.part[ni] != 0)
                    {
                        continue;
                    }
                    IntVec3 wc = new IntVec3(n.x, 0, n.z + shift);
                    if (!DigPassable(wc, key, pg))
                    {
                        dl.part[ni] = -1;
                        continue;
                    }
                    dl.part[ni] = id;
                    digFlood.Enqueue(n);
                }
            }
            return id;
        }

        private readonly Dictionary<int, int>[] digUf = new Dictionary<int, int>[3];
        private readonly int[] digUfFor = { -1, -1, -1 };

        // The dig component of a dig part.
        public int DigCompOf(int digPart, int key)
        {
            EnsureNodes();
            Dictionary<int, int> d = digUf[key] ??= new Dictionary<int, int>();
            if (digUfFor[key] != anyVersion)
            {
                d.Clear();
                for (int i = 0; i < links.Count; i++)
                {
                    if (!Usable(i))
                    {
                        continue;
                    }
                    int a = DigPartAt(nodes[i * 3].cell, key), b = DigPartAt(nodes[i * 3 + 2].cell, key);
                    if (a > 0 && b > 0)
                    {
                        Union(d, a, b);
                    }
                }
                digUfFor[key] = anyVersion;
            }
            return digPart <= 0 ? int.MinValue : FindRoot(d, digPart);
        }

        // ---------------------------------------------------------------------------------------------- Reach2

        private static readonly AccessTools.FieldRef<Reachability, uint> ReachedRef = AccessTools.FieldRefAccess<Reachability, uint>("reachedIndex");
        private static readonly HashSet<Reachability> floorOnly = new HashSet<Reachability>();
        private Reachability reach2;

        public Reachability Reach2
        {
            get
            {
                if (reach2 == null)
                {
                    reach2 = new Reachability(map);
                    floorOnly.Add(reach2);
                }
                return reach2;
            }
        }

        // The instance that answers for one floor (the stairs left out).
        public static bool IsFloorOnly(Reachability r) => floorOnly.Count > 0 && floorOnly.Contains(r);

        public static int FloorReachCalls;

        // Can the traveller get from start to dest on start's floor alone (the game's own reachability, no stairs)? The
        // regions' reached marks are shared by both instances: the counters are kept apart (each above the other's last).
        public bool FloorReach(IntVec3 start, LocalTargetInfo dest, PathEndMode mode, TraverseParms tp)
        {
            Reachability main = map.reachability;
            Reachability r2 = Reach2;
            uint m = ReachedRef(main);
            if (ReachedRef(r2) < m)
            {
                ReachedRef(r2) = m;
            }
            FloorReachCalls++;
            try
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool ok = r2.CanReach(start, dest, mode, tp);
                Dev.PZStats.floorReachMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                return ok;
            }
            finally
            {
                uint q = ReachedRef(r2);
                if (ReachedRef(main) < q)
                {
                    ReachedRef(main) = q;
                }
            }
        }

        private bool[] seen = new bool[0];
        private readonly Queue<int> nodeQueue = new Queue<int>();

        // Reach over the stairs for one traveller: from start (its parts startParts) over stairs ends to the destination
        // (its regions' parts destParts), every floor question asked of Reach2 (the game's reachability on one floor). The
        // nodes are few (the stairs' ends); a floor question between two ends in one district is answered by the game at
        // once, the rest from Reach2's cache.
        public bool StairReach(IntVec3 start, HashSet<int> startParts, LocalTargetInfo dest, PathEndMode mode, HashSet<int> destParts, TraverseParms tp)
        {
            EnsureComps();
            int n = nodes.Count;
            if (n == 0)
            {
                return false;
            }
            if (seen.Length < n)
            {
                seen = new bool[n];
            }
            Array.Clear(seen, 0, n);
            nodeQueue.Clear();
            Dev.PZStats.stairReach++;
            foreach (int sp in startParts)
            {
                List<int> list = NodesIn(sp);
                for (int i = 0; i < list.Count; i++)
                {
                    int v = list[i];
                    if (!seen[v] && FloorReach(start, nodes[v].cell, PathEndMode.OnCell, tp))
                    {
                        seen[v] = true;
                        nodeQueue.Enqueue(v);
                    }
                }
            }
            while (nodeQueue.Count > 0)
            {
                int u = nodeQueue.Dequeue();
                int part = nodePart[u];
                if (destParts.Contains(part) && FloorReach(nodes[u].cell, dest, mode, tp))
                {
                    nodeQueue.Clear();
                    return true;
                }
                int link = nodes[u].link;
                int other = nodes[u].kind == Low ? link * 3 + 2 : link * 3;
                if (!seen[other] && Passable(link, tp))
                {
                    seen[other] = true;
                    nodeQueue.Enqueue(other);
                }
                List<int> same = NodesIn(part);
                for (int i = 0; i < same.Count; i++)
                {
                    int v = same[i];
                    if (!seen[v] && FloorReach(nodes[u].cell, nodes[v].cell, PathEndMode.OnCell, tp))
                    {
                        seen[v] = true;
                        nodeQueue.Enqueue(v);
                    }
                }
            }
            return false;
        }

        public void ClearReach2()
        {
            reach2?.ClearCache();
            memo.Clear();
            // (the legs went through doors, fences and danger the game now judges again: a door forbidden, held open ...)
            legsByPawn.Clear();
        }

        // One pawn's reachability changed (its faction, guest status, a rope ...): its own legs and memo go.
        public void ClearReach2For(Pawn p)
        {
            reach2?.ClearCacheFor(p);
            if (p == null)
            {
                return;
            }
            int id = p.thingIDNumber;
            if (memo.Count > 0)
            {
                stalePawn.Clear();
                foreach ((int who, IntVec3 dest, int part) k in memo.Keys)
                {
                    if (k.who == id)
                    {
                        stalePawn.Add(k);
                    }
                }
                for (int i = 0; i < stalePawn.Count; i++)
                {
                    memo.Remove(stalePawn[i]);
                }
                stalePawn.Clear();
            }
            legsByPawn.Remove(id);
        }

        private readonly List<(int, IntVec3, int)> stalePawn = new List<(int, IntVec3, int)>();

        public void ClearReach2ForHostile(Thing t)
        {
            reach2?.ClearCacheForHostile(t);
        }

        // ---------------------------------------------------------------------------------------------- partners

        // The regions one stair step away from a region (H's region <-> E's region), for the game's region searches
        // (BFSWorker_Patch). Indexed per region, rebuilt when the regions or the stairs change.
        private readonly Dictionary<Region, List<Region>> partners = new Dictionary<Region, List<Region>>();
        private int partnersFor = -1;

        public List<Region> PartnersOf(Region r)
        {
            EnsureNodes();
            if (links.Count == 0)
            {
                return null;
            }
            if (partnersFor != anyVersion)
            {
                partners.Clear();
                RegionGrid rg = map.regionGrid;
                for (int i = 0; i < links.Count; i++)
                {
                    if (!Usable(i))
                    {
                        continue;
                    }
                    Region b = rg.GetValidRegionAt_NoRebuild(links[i].bottom);
                    Region t = rg.GetValidRegionAt_NoRebuild(links[i].top);
                    if (b == null || t == null || b == t)
                    {
                        continue;
                    }
                    AddPartner(b, t);
                    AddPartner(t, b);
                }
                partnersFor = anyVersion;
            }
            return partners.TryGetValue(r, out List<Region> l) ? l : null;
        }

        private void AddPartner(Region a, Region b)
        {
            if (!partners.TryGetValue(a, out List<Region> l))
            {
                partners[a] = l = new List<Region>();
            }
            if (!l.Contains(b))
            {
                l.Add(b);
            }
        }

        // ---------------------------------------------------------------------------------------------- memo

        // "No route" (the router found none where reachability said yes): per traveller, destination and start part, for
        // MemoTicks; reachability says no meanwhile, so the work givers stop handing out the job.
        public const int MemoTicks = 250;
        private readonly Dictionary<(int who, IntVec3 dest, int part), int> memo = new Dictionary<(int, IntVec3, int), int>();

        public bool Memo(Pawn p, IntVec3 dest, int part)
        {
            if (memo.Count == 0 || p == null)
            {
                return false;
            }
            if (memo.TryGetValue((p.thingIDNumber, dest, part), out int until))
            {
                if (Find.TickManager.TicksGame < until)
                {
                    return true;
                }
                memo.Remove((p.thingIDNumber, dest, part));
            }
            return false;
        }

        public void AddMemo(Pawn p, IntVec3 dest, int part)
        {
            if (p != null)
            {
                memo[(p.thingIDNumber, dest, part)] = Find.TickManager.TicksGame + MemoTicks;
                Dev.PZStats.routeMemos++;
            }
        }

        // ---------------------------------------------------------------------------------------------- leg cache

        public const int LegTicks = 250;

        public sealed class LegResult
        {
            public List<IntVec3> nodes;   // reversed (as PawnPath.NodesReversed); null = no path
            public float cost;
            public int tick;
            public int version;           // the floor's version when found
        }

        // per pawn (key -1: no pawn), so one pawn's changes clear its own legs only (ClearReach2For)
        private readonly Dictionary<int, Dictionary<(int who, IntVec3 from, IntVec3 to, int thing, PathEndMode mode), LegResult>> legsByPawn = new Dictionary<int, Dictionary<(int, IntVec3, IntVec3, int, PathEndMode), LegResult>>();

        public static int Who(TraverseParms tp) => HashCode.Combine(tp.pawn?.thingIDNumber ?? -1, (int)tp.mode, (int)tp.maxDanger, tp.canBashDoors, tp.canBashFences, tp.avoidFog, tp.fenceBlocked, tp.avoidPersistentDanger);

        public LegResult KnownLeg(int pid, int who, IntVec3 from, LocalTargetInfo to, PathEndMode mode)
        {
            if (!legsByPawn.TryGetValue(pid, out var legs) || !legs.TryGetValue((who, from, to.Cell, to.HasThing ? to.Thing.thingIDNumber : -1, mode), out LegResult r))
            {
                return null;
            }
            int L = Levels.LevelOf(from, map);
            if (Find.TickManager.TicksGame - r.tick > LegTicks || r.version != LevelVersion(L))
            {
                return null;
            }
            return r;
        }

        public void KeepLeg(int pid, int who, IntVec3 from, LocalTargetInfo to, PathEndMode mode, LegResult r)
        {
            r.tick = Find.TickManager.TicksGame;
            r.version = LevelVersion(Levels.LevelOf(from, map));
            if (!legsByPawn.TryGetValue(pid, out var legs))
            {
                legsByPawn[pid] = legs = new Dictionary<(int, IntVec3, IntVec3, int, PathEndMode), LegResult>();
            }
            legs[(who, from, to.Cell, to.HasThing ? to.Thing.thingIDNumber : -1, mode)] = r;
        }

        private readonly List<(int, IntVec3, IntVec3, int, PathEndMode)> stale = new List<(int, IntVec3, IntVec3, int, PathEndMode)>();
        private readonly List<int> stalePawns = new List<int>();

        private void PruneLegs()
        {
            int now = Find.TickManager.TicksGame;
            stalePawns.Clear();
            foreach (KeyValuePair<int, Dictionary<(int, IntVec3, IntVec3, int, PathEndMode), LegResult>> pk in legsByPawn)
            {
                stale.Clear();
                foreach (KeyValuePair<(int, IntVec3, IntVec3, int, PathEndMode), LegResult> kv in pk.Value)
                {
                    if (now - kv.Value.tick > LegTicks)
                    {
                        stale.Add(kv.Key);
                    }
                }
                for (int i = 0; i < stale.Count; i++)
                {
                    pk.Value.Remove(stale[i]);
                }
                if (pk.Value.Count == 0)
                {
                    stalePawns.Add(pk.Key);
                }
            }
            for (int i = 0; i < stalePawns.Count; i++)
            {
                legsByPawn.Remove(stalePawns[i]);
            }
            stale.Clear();
            stalePawns.Clear();
        }

        // ---------------------------------------------------------------------------------------------- deferred requests

        // A path request of the path follower that needs the router (GenerateNewPathRequest_Patch) or a same-floor path
        // that may have a shorter way over the stairs (option C, PatherTick_Detour_Patch): answered in MapComponentTick
        // within the per-tick budget, drafted pawns and forced jobs first. FindPathNow callers are answered at once.
        public sealed class Pending
        {
            public Pawn pawn;
            public PathRequest req;
            public IntVec3 start;
            public LocalTargetInfo dest;
            public PathEndMode mode;
            public TraverseParms tp;
            public PathFinderCostTuning tuning;
            public PawnPath vanilla;   // the game's own same-floor path (detour check), or null
            public int seq;
            public int tick;
        }

        private readonly List<Pending> queue = new List<Pending>();
        private int seq;
        public static double BudgetMs = 2.0;
        private static int budgetTick = -1;
        private static double spentMs;

        public void Enqueue(Pending p)
        {
            p.seq = seq++;
            p.tick = Find.TickManager.TicksGame;
            queue.Add(p);
            Dev.PZStats.routerDeferred++;
            if (queue.Count > Dev.PZStats.routerQueueMax)
            {
                Dev.PZStats.routerQueueMax = queue.Count;
            }
        }

        public int QueueCount => queue.Count;

        private static bool Urgent(Pending p) => p.pawn != null && (p.pawn.Drafted || p.pawn.CurJob != null && p.pawn.CurJob.playerForced);

        public void ProcessQueue()
        {
            if (queue.Count == 0)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (budgetTick != now)
            {
                budgetTick = now;
                spentMs = 0;
            }
            int done = 0;
            while (queue.Count > 0)
            {
                if (done > 0 && spentMs >= BudgetMs)
                {
                    Dev.PZStats.routerBudgetWaits++;
                    break;
                }
                int pick = 0;
                for (int i = 0; i < queue.Count; i++)
                {
                    bool u = Urgent(queue[i]), up = Urgent(queue[pick]);
                    if (u && !up || u == up && queue[i].seq < queue[pick].seq)
                    {
                        pick = i;
                    }
                }
                Pending p = queue[pick];
                queue.RemoveAt(pick);
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                Answer(p);
                spentMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                done++;
                int waited = now - p.tick;
                if (waited > Dev.PZStats.routerWaitTicksMax)
                {
                    Dev.PZStats.routerWaitTicksMax = waited;
                }
            }
        }

        private void Answer(Pending p)
        {
            if (p.req.Cancelled || p.req.ResultIsReady)
            {
                p.vanilla?.Dispose();
                return;
            }
            if (p.pawn == null || p.pawn.Dead || !p.pawn.Spawned || p.pawn.Map != map)
            {
                p.vanilla?.Dispose();
                StairRouter.Resolve(p.req, null);
                return;
            }
            PawnPath path = StairRouter.Find(map, p.start, p.dest, p.mode, p.tp, p.tuning, p.vanilla);
            if (p.vanilla != null && path != null && !ReferenceEquals(path, p.vanilla))
            {
                Dev.PZStats.sameFloorWins++;
            }
            if (p.vanilla != null && !ReferenceEquals(path, p.vanilla))
            {
                p.vanilla.Dispose();
            }
            StairRouter.Resolve(p.req, path);
        }

        public override void MapComponentTick()
        {
            ProcessQueue();
            if ((Find.TickManager.TicksGame + map.uniqueID) % LegTicks == 0)
            {
                PruneLegs();
            }
        }
    }

    // A region was made: its floor's parts are numbered again (and everything keyed by the graph's version).
    [HarmonyPatch(typeof(RegionMaker), nameof(RegionMaker.TryGenerateRegionFrom))]
    [PatchGroup(FailSoft.StairRouting, key: true)]
    public static class RegionMade_RouteGraph_Patch
    {
        public static void Postfix(Region __result)
        {
            if (__result == null)
            {
                return;
            }
            Map map = __result.Map;
            RouteGraph g = RouteGraph.For(map);
            if (g == null)
            {
                return;
            }
            int L = RouteGraph.LevelOfRegion(__result, map);
            if (L != Levels.None)
            {
                g.LevelChanged(L);
            }
        }
    }

    // Reach2 is cleared whenever the map's own cache is (the game knows when reachability changes: regions, doors,
    // forbidding, a pawn's faction ...).
    [HarmonyPatch(typeof(Reachability), nameof(Reachability.ClearCache))]
    [PatchGroup(FailSoft.StairRouting, key: true)]
    public static class ReachClear_RouteGraph_Patch
    {
        public static void Postfix(Reachability __instance)
        {
            if (!RouteGraph.IsFloorOnly(__instance))
            {
                RouteGraph.For(ReachRefs.MapRef(__instance))?.ClearReach2();
            }
        }
    }

    [HarmonyPatch(typeof(Reachability), nameof(Reachability.ClearCacheFor))]
    [PatchGroup(FailSoft.StairRouting, key: true)]
    public static class ReachClearFor_RouteGraph_Patch
    {
        public static void Postfix(Reachability __instance, Pawn pawn)
        {
            if (!RouteGraph.IsFloorOnly(__instance))
            {
                RouteGraph.For(ReachRefs.MapRef(__instance))?.ClearReach2For(pawn);
            }
        }
    }

    [HarmonyPatch(typeof(Reachability), nameof(Reachability.ClearCacheForHostile))]
    [PatchGroup(FailSoft.StairRouting, key: true)]
    public static class ReachClearHostile_RouteGraph_Patch
    {
        public static void Postfix(Reachability __instance, Thing hostileTo)
        {
            if (!RouteGraph.IsFloorOnly(__instance))
            {
                RouteGraph.For(ReachRefs.MapRef(__instance))?.ClearReach2ForHostile(hostileTo);
            }
        }
    }
}
