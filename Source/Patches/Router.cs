using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Patches
{
    // Paths between floors (isorim-pz-pathing-redesign.md; path splicing idea credited to MigCorp - Skiptech, written here
    // from the 1.7 API only). The game's path finder knows one floor; a path to another floor is legs on single floors
    // (each a normal FindPathNow: the game's own costs, doors, avoid grid, fog) joined by the stairs:
    //   up:   ... leg to the ramp's low end L0, L1, H (walked as fixed nodes along the ramp, costed like the game's cells:
    //         path grid cost incl. fire, fog, a pawn in the way, move ticks), the stair step H -> E (Climb.StepTicks), leg ...
    //   down: ... leg to E, the step E -> H, H, L1, L0, leg ...
    // The route is chosen by A* over the stairs' ends (RouteGraph): nodes Low/High/Top per stairs, plus the start and the
    // goal attached exactly as the game's reachability attaches them (its start regions, its destination regions with the
    // touch rules). Edges: the stair step (time), the ramp (cells), and legs between nodes of one part, costed lazily:
    // first an estimate that never exceeds the real cost (ground octile x the traveller's move ticks), then, for the legs
    // of the route A* picked, the real leg; A* runs again until its route is all real legs (a few extra legs at most).
    // The heuristic: ground octile to the goal + |levels between| x (StepTicks - move ticks) (each floor change costs at
    // least that over the walk its ground distance already counts): admissible and consistent, so the route found is the
    // cheapest one the legs allow. A leg that fails is banned and A* runs again (at most MaxFailures); then "no route" is
    // remembered for a while (RouteGraph memo) and reachability says no meanwhile.
    // A stair step is two path nodes that are not neighbours: the path follower sets pawn.Position = nextCell without
    // checking adjacency. The destination and end mode are never changed.
    internal static class StairRouter
    {
        private static readonly AccessTools.FieldRef<PawnPath, float> CostRef = AccessTools.FieldRefAccess<PawnPath, float>("totalCostInt");
        private static readonly AccessTools.FieldRef<PawnPath, int> IndexRef = AccessTools.FieldRefAccess<PawnPath, int>("curNodeIndex");

        public const float DetourFactor = 1.2f;   // option C: a same-floor path this much over the stairs' lower bound is checked
        public const int ExtraLegs = 8;           // legs computed beyond the final route's, then the route is taken as it is (the review's ~4 left 2 of 240 oracle pairs 6 ticks over the optimum)
        public const int MaxFailures = 3;         // legs that failed before "no route"
        public const int FogCost = 600;           // the game's PathGridJob.Cost_AvoidFog
        public const int BlockedCost = 175;       // the game's PathFinderJob.Cost_Blocked

        // Set while a route is worked out (its legs are FindPathNow calls of their own: never routed again).
        [ThreadStatic]
        internal static bool busy;

        [ThreadStatic]
        private static bool resolving;

        // Our own answers to a request (the detour hook leaves them alone).
        internal static void Resolve(PathRequest req, PawnPath path)
        {
            resolving = true;
            try
            {
                req.Resolve(path);
            }
            finally
            {
                resolving = false;
            }
        }

        internal static bool Resolving => resolving;

        // ------------------------------------------------------------------------------------------------ decision

        // THE test for the router: the destination on another floor, or on this floor but not reachable on it alone
        // (the game's reachability with the stairs left out, Reach2): both only over stairs. The same test on every floor.
        public static bool Needed(Map map, IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, TraverseParms tp)
        {
            if (busy || !dest.IsValid || !start.IsValid)
            {
                return false;
            }
            RouteGraph g = RouteGraph.For(map);
            // (no links on the map: nothing between floors, unless the target is an emergency ladder still to be built, touched
            // from its foot on another floor)
            if (g == null || !start.InBounds(map) || !dest.Cell.InBounds(map) || StairGrid.For(map) == null && !(dest.HasThing && EmergencyLadder.IsPending(dest.Thing)))
            {
                return false;
            }
            int ls = Levels.LevelOf(start, map), ld = Levels.LevelOf(dest.Cell, map);
            if (ls == Levels.None || ld == Levels.None)
            {
                return false;
            }
            if (ls != ld)
            {
                return true;
            }
            if (RouteGraph.DigMode(tp.mode) || tp.mode == TraverseMode.NoPassClosedDoorsOrWater)
            {
                return false;   // (the game's cell flood on one floor: left to the game on one floor)
            }
            if (g.FloorReach(start, dest, peMode, tp))
            {
                return false;
            }
            // Not on this floor alone: the router only when the map's reachability (stairs included) says yes. When it says
            // no the game's own answer (no path) stands, as on any floor (FinalSay: one floor is other mods' and the game's).
            return !ReachRefs.WorkingRef(map.reachability) && map.reachability.CanReach(start, dest, peMode, tp);
        }

        // ------------------------------------------------------------------------------------------------ search state

        private const int KindLeg = 0, KindRamp = 1, KindStep = 2, KindZero = 3;

        private sealed class Edge
        {
            public int kind;
            public float est;
            public float cost = -1f;     // real cost, -1 unknown
            public bool banned;
            public List<IntVec3> nodes;  // walking order, without the first cell (legs and ramps)
            public LocalTargetInfo target;
            public PathEndMode mode;
            public IntVec3 from;
        }

        private sealed class Ctx
        {
            public Map map;
            public RouteGraph g;
            public TraverseParms tp;
            public TraverseParms reachTp;   // the traveller's test for the floor checks (dig modes: pass doors)
            public PathFinderCostTuning? tuning;
            public IntVec3 start;
            public IntVec3 startGround;
            public int startLevel;
            public LocalTargetInfo dest;
            public PathEndMode mode;
            public CellRect goalRect;       // ground cells (Touch: the target's rect expanded by one)
            public readonly List<int> goalLevels = new List<int>();
            public readonly HashSet<int> startParts = new HashSet<int>();
            public readonly HashSet<int> goalParts = new HashSet<int>();
            public int startMemoPart;
            public int destLevel;
            public bool rimGoal;
            public IntVec3 footGoal;        // an emergency ladder not built yet: touched from its foot (EmergencyLadder)
            public int tc, td, stepExtra;
            public bool dig;
            public int digKey;
            public int who;
            public readonly Dictionary<long, Edge> edges = new Dictionary<long, Edge>();
            public int legsComputed, failures;
            public bool estimatesOnly;
            public bool noDirect;
            public PawnPath knownDirect;
            public PathingContext pc;

            public int N => g.nodes.Count;
            public int S => g.nodes.Count;
            public int G => g.nodes.Count + 1;
        }

        private static long Key(int u, int v) => ((long)u << 32) | (uint)v;

        // ------------------------------------------------------------------------------------------------ costs

        private static int Octile(IntVec3 a, IntVec3 b, int tc, int td)
        {
            int dx = Math.Abs(a.x - b.x), dz = Math.Abs(a.z - b.z);
            int mn = Math.Min(dx, dz), mx = Math.Max(dx, dz);
            return (mx - mn) * tc + mn * td;
        }

        private static int OctileRect(IntVec3 a, CellRect r, int tc, int td)
        {
            int dx = a.x < r.minX ? r.minX - a.x : a.x > r.maxX ? a.x - r.maxX : 0;
            int dz = a.z < r.minZ ? r.minZ - a.z : a.z > r.maxZ ? a.z - r.maxZ : 0;
            int mn = Math.Min(dx, dz), mx = Math.Max(dx, dz);
            return (mx - mn) * tc + mn * td;
        }

        private static int H(Ctx c, int u)
        {
            if (u == c.G)
            {
                return 0;
            }
            IntVec3 gr;
            int L;
            if (u == c.S)
            {
                gr = c.startGround;
                L = c.startLevel;
            }
            else
            {
                RouteGraph.Node n = c.g.nodes[u];
                gr = n.ground;
                L = n.level;
            }
            int dl = int.MaxValue;
            for (int i = 0; i < c.goalLevels.Count; i++)
            {
                dl = Math.Min(dl, Math.Abs(L - c.goalLevels[i]));
            }
            if (dl == int.MaxValue)
            {
                dl = 0;
            }
            return OctileRect(gr, c.goalRect, c.tc, c.td) + dl * c.stepExtra;
        }

        // What the game's path finder adds for entering cell x (PathGridJob + PathFinderJob): the path grid's cost (terrain,
        // buildings, fire), fog for a traveller that avoids it, a pawn in the way, and the move ticks.
        internal static int CellCost(Map map, IntVec3 x, TraverseParms tp, int moveTicks)
        {
            PathingContext pc = map.pathing.For(tp);
            int c = pc.pathGrid.Cost(x);
            if (c >= 10000)
            {
                return 100000;
            }
            if (tp.avoidFog && x.Fogged(map))
            {
                c += FogCost;
            }
            if (tp.pawn != null && PawnUtility.AnyPawnBlockingPathAt(x, tp.pawn))
            {
                c += BlockedCost;
            }
            return c + moveTicks;
        }

        // A link step's cost: its time (stairs Climb.StepTicks, ladders 180 per floor: VLink.stepTicks), plus fog on the cell
        // stepped onto and a pawn in the way there.
        internal static int StepCost(Map map, int stepTicks, IntVec3 onto, TraverseParms tp)
        {
            int c = stepTicks;
            if (tp.avoidFog && onto.Fogged(map))
            {
                c += FogCost;
            }
            if (tp.pawn != null && PawnUtility.AnyPawnBlockingPathAt(onto, tp.pawn))
            {
                c += BlockedCost;
            }
            return c;
        }

        internal static void MoveTicks(TraverseParms tp, out int tc, out int td)
        {
            if (tp.pawn != null)
            {
                tc = Mathf.RoundToInt(tp.pawn.TicksPerMoveCardinal);
                td = Mathf.RoundToInt(tp.pawn.TicksPerMoveDiagonal);
            }
            else
            {
                tc = 18;
                td = 18;
            }
        }

        // ------------------------------------------------------------------------------------------------ attachment

        private static readonly List<Region> tmpRegions = new List<Region>();

        // The start's regions, as Reachability.DetermineStartRegions takes them.
        private static void StartRegions(Map map, IntVec3 start, TraverseParms tp, List<Region> into)
        {
            into.Clear();
            PathGrid pg = map.pathing.For(tp).pathGrid;
            if (pg.WalkableFast(start))
            {
                Region r = map.regionGrid.GetValidRegionAt(start);
                if (r != null)
                {
                    into.Add(r);
                }
                return;
            }
            for (int i = 0; i < 8; i++)
            {
                IntVec3 c = start + GenAdj.AdjacentCells[i];
                if (c.InBounds(map) && pg.WalkableFast(c))
                {
                    Region r = map.regionGrid.GetValidRegionAt(c);
                    if (r != null && !into.Contains(r))
                    {
                        into.Add(r);
                    }
                }
            }
        }

        // The destination's regions, as Reachability.CanReach takes them (its path mode resolved first).
        private static void DestRegions(Map map, LocalTargetInfo dest, PathEndMode peMode, TraverseParms tp, List<Region> into)
        {
            into.Clear();
            PathEndMode m = peMode;
            LocalTargetInfo d = (LocalTargetInfo)GenPath.ResolvePathMode(tp.pawn, dest.ToTargetInfo(map), ref m);
            if (m == PathEndMode.OnCell)
            {
                Region r = d.Cell.GetRegion(map);
                if (r != null && r.Allows(tp, isDestination: true))
                {
                    into.Add(r);
                }
            }
            else if (m == PathEndMode.Touch)
            {
                TouchPathEndModeUtility.AddAllowedAdjacentRegions(d, tp, map, into);
            }
        }

        private static bool Setup(Ctx c, Map map, IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, TraverseParms tp, PathFinderCostTuning? tuning)
        {
            c.map = map;
            c.g = RouteGraph.For(map);
            c.tp = tp;
            c.tuning = tuning;
            c.start = start;
            c.startGround = Levels.Ground(start, map);
            c.startLevel = Levels.LevelOf(start, map);
            c.dest = dest;
            c.mode = peMode;
            c.edges.Clear();
            c.legsComputed = 0;
            c.failures = 0;
            c.who = RouteGraph.Who(tp);
            c.pc = map.pathing.For(tp);
            c.dig = false;
            c.digKey = RouteGraph.DigKey(tp.mode);
            c.reachTp = tp;
            if (RouteGraph.DigMode(tp.mode))
            {
                c.reachTp.mode = TraverseMode.PassDoors;
            }
            MoveTicks(tp, out c.tc, out c.td);
            // (the cheapest floor change of any link: stairs, Climb.StepTicks per floor; a ladder costs 180 per floor, more:
            // the heuristic stays admissible with ladders)
            c.stepExtra = Math.Max(0, Climb.StepTicks - c.tc);
            if (c.g == null || c.startLevel == Levels.None || !c.startGround.IsValid)
            {
                return false;
            }
            c.g.EnsureComps();
            // goal rect, in ground cells
            CellRect rect = dest.HasThing && peMode != PathEndMode.OnCell ? dest.Thing.OccupiedRect() : CellRect.SingleCell(dest.Cell);
            if (peMode != PathEndMode.OnCell)
            {
                rect = rect.ExpandedBy(1);
            }
            IntVec3 dg = Levels.Ground(dest.Cell, map);
            c.destLevel = Levels.LevelOf(dest.Cell, map);
            if (!dg.IsValid || c.destLevel == Levels.None)
            {
                return false;
            }
            int dz = dest.Cell.z - dg.z;
            c.goalRect = new CellRect(rect.minX, rect.minZ - dz, rect.Width, rect.Height);
            c.rimGoal = peMode == PathEndMode.Touch && dest.HasThing && StairTouch.Is(dest.Thing);
            c.footGoal = peMode != PathEndMode.OnCell && dest.HasThing && EmergencyLadder.PendingFoot(dest.Thing, out IntVec3 ft) ? ft : IntVec3.Invalid;
            StartRegions(map, start, tp, tmpRegions);
            c.startParts.Clear();
            c.startMemoPart = -1;
            for (int i = 0; i < tmpRegions.Count; i++)
            {
                int p = c.g.PartOf(tmpRegions[i]);
                if (p >= 0)
                {
                    c.startParts.Add(p);
                    if (c.startMemoPart < 0)
                    {
                        c.startMemoPart = p;
                    }
                }
            }
            DestRegions(map, dest, peMode, tp, tmpRegions);
            c.goalParts.Clear();
            c.goalLevels.Clear();
            for (int i = 0; i < tmpRegions.Count; i++)
            {
                int p = c.g.PartOf(tmpRegions[i]);
                if (p >= 0 && c.goalParts.Add(p))
                {
                    int L = RouteGraph.LevelOfPart(p);
                    if (!c.goalLevels.Contains(L))
                    {
                        c.goalLevels.Add(L);
                    }
                }
            }
            if (c.goalLevels.Count == 0)
            {
                c.goalLevels.Add(Levels.LevelOf(dest.Cell, map));
            }
            tmpRegions.Clear();
            return true;
        }

        // The dig parts take the place of the region parts (a traveller breaking walls, when no route passes doors).
        private static void UseDigParts(Ctx c)
        {
            c.dig = true;
            c.failures = 0;
            c.edges.Clear();
            c.startParts.Clear();
            c.goalParts.Clear();
            int sp = c.g.DigPartAt(c.start, c.digKey);
            if (sp > 0)
            {
                c.startParts.Add(sp);
            }
            foreach (IntVec3 x in DigGoalCells(c))
            {
                int p = c.g.DigPartAt(x, c.digKey);
                if (p > 0)
                {
                    c.goalParts.Add(p);
                }
            }
        }

        private static IEnumerable<IntVec3> DigGoalCells(Ctx c)
        {
            CellRect r = c.dest.HasThing ? c.dest.Thing.OccupiedRect() : CellRect.SingleCell(c.dest.Cell);
            if (c.mode != PathEndMode.OnCell)
            {
                r = r.ExpandedBy(1);
            }
            foreach (IntVec3 x in r)
            {
                if (x.InBounds(c.map) && ProjectZ.SameBand(x, c.dest.Cell, c.map))
                {
                    yield return x;
                }
            }
        }

        private static int NodePart(Ctx c, int n) => c.dig ? c.g.DigPartAt(c.g.nodes[n].cell, c.digKey) : c.g.NodePart(n);

        private static readonly List<int> digNodes = new List<int>();

        private static List<int> NodesIn(Ctx c, int part)
        {
            if (!c.dig)
            {
                return c.g.NodesIn(part);
            }
            digNodes.Clear();
            for (int i = 0; i < c.N; i++)
            {
                if (c.g.nodes[i].kind != RouteGraph.High && c.g.Usable(c.g.nodes[i].link) && c.g.DigPartAt(c.g.nodes[i].cell, c.digKey) == part)
                {
                    digNodes.Add(i);
                }
            }
            return digNodes;
        }

        // ------------------------------------------------------------------------------------------------ edges

        private static Edge GetEdge(Ctx c, int u, int v, int kind, float est, IntVec3 from, LocalTargetInfo target, PathEndMode mode)
        {
            long k = Key(u, v);
            if (c.edges.TryGetValue(k, out Edge e))
            {
                return e;
            }
            e = new Edge { kind = kind, est = est, from = from, target = target, mode = mode };
            if (kind == KindLeg && !c.estimatesOnly)
            {
                RouteGraph.LegResult known = c.g.KnownLeg(c.tp.pawn?.thingIDNumber ?? -1, c.who, from, target, mode);
                if (known != null)
                {
                    if (known.nodes == null)
                    {
                        e.banned = true;
                    }
                    else
                    {
                        e.cost = known.cost;
                        e.nodes = WalkOrder(known.nodes);
                    }
                }
            }
            c.edges[k] = e;
            return e;
        }

        private static Edge Exact(Ctx c, int u, int v, int kind, float cost, List<IntVec3> nodes)
        {
            long k = Key(u, v);
            if (!c.edges.TryGetValue(k, out Edge e))
            {
                e = new Edge { kind = kind, est = cost, cost = cost, nodes = nodes };
                c.edges[k] = e;
            }
            return e;
        }

        private static List<IntVec3> WalkOrder(List<IntVec3> reversed)
        {
            List<IntVec3> w = new List<IntVec3>(Math.Max(0, reversed.Count - 1));
            for (int i = reversed.Count - 2; i >= 0; i--)
            {
                w.Add(reversed[i]);
            }
            return w;
        }

        private static IntVec3 CellOf(Ctx c, int u) => u == c.S ? c.start : c.g.nodes[u].cell;

        private static bool Immediate(Ctx c, IntVec3 at)
        {
            return ReachabilityImmediate.CanReachImmediate(at, c.dest, c.map, c.mode, c.tp.pawn);
        }

        // A goal leg the game's path finder can walk: on the destination's floor, or a stairs thing touched from the floor
        // above (its rim). A target touched across a stair step is reached only by standing on the other end (Immediate).
        private static bool GoalLegOk(Ctx c, int level) => level == c.destLevel || c.rimGoal && level == Levels.LevelOf(c.dest.Thing.Position, c.map) + 1 || c.footGoal.IsValid && level == Levels.LevelOf(c.footGoal, c.map);

        // Dest on this stairs' ramp (or the stairs themselves): the pawn may walk to it from H.
        private static bool DestOnRamp(Ctx c, int link)
        {
            if (c.dest.HasThing && ReferenceEquals(c.dest.Thing, c.g.links[link].thing))
            {
                return true;
            }
            IntVec3 d = c.dest.Cell;
            return d == c.g.links[link].bottom || d == c.g.MidCell(link);
        }

        private static readonly List<(int v, Edge e)> nbrs = new List<(int, Edge)>();

        private static List<(int v, Edge e)> Neighbours(Ctx c, int u)
        {
            nbrs.Clear();
            RouteGraph g = c.g;
            if (u == c.G)
            {
                return nbrs;
            }
            IntVec3 at = CellOf(c, u);
            IntVec3 atGround = u == c.S ? c.startGround : g.nodes[u].ground;
            int kind = u == c.S ? -1 : g.nodes[u].kind;
            if (kind == RouteGraph.High)
            {
                int link = g.nodes[u].link;
                if (!c.dig ? g.Passable(link, c.tp) : g.Usable(link))
                {
                    IntVec3 top = g.links[link].top;
                    nbrs.Add((u + 1, Exact(c, u, u + 1, KindStep, StepCost(c.map, g.links[link].stepTicks, top, c.tp), new List<IntVec3> { top })));
                }
                if (g.links[link].HasRamp)
                {
                    IntVec3 mid = g.MidCell(link), low = g.nodes[u - 1].cell;
                    nbrs.Add((u - 1, Exact(c, u, u - 1, KindRamp, CellCost(c.map, mid, c.tp, c.tc) + CellCost(c.map, low, c.tp, c.tc), new List<IntVec3> { mid, low })));
                }
                else
                {
                    nbrs.Add((u - 1, Exact(c, u, u - 1, KindRamp, 0, new List<IntVec3>())));   // (a ladder: Low and High are the foot)
                }
                if (Immediate(c, at))
                {
                    nbrs.Add((c.G, Exact(c, u, c.G, KindZero, 0, null)));
                }
                else if (DestOnRamp(c, link))
                {
                    nbrs.Add((c.G, GetEdge(c, u, c.G, KindLeg, OctileRect(atGround, c.goalRect, c.tc, c.td), at, c.dest, c.mode)));
                }
                return nbrs;
            }
            // legs to the other nodes of the part(s)
            if (u == c.S)
            {
                foreach (int p in c.startParts)
                {
                    List<int> list = NodesIn(c, p);
                    for (int i = 0; i < list.Count; i++)
                    {
                        int v = list[i];
                        nbrs.Add((v, GetEdge(c, u, v, KindLeg, Octile(atGround, g.nodes[v].ground, c.tc, c.td), at, g.nodes[v].cell, PathEndMode.OnCell)));
                    }
                }
                // a start on a ramp: on up it (the rest of the ramp is walked to H)
                int rl = g.RampLink(c.start);
                if (rl >= 0 && c.start != g.nodes[rl * 3].cell && g.Usable(rl))
                {
                    int hi = rl * 3 + 1;
                    float cost = c.start == g.links[rl].bottom ? 0 : CellCost(c.map, g.links[rl].bottom, c.tp, c.tc);
                    nbrs.Add((hi, Exact(c, u, hi, KindRamp, cost, c.start == g.links[rl].bottom ? new List<IntVec3>() : new List<IntVec3> { g.links[rl].bottom })));
                }
                bool direct = c.knownDirect != null;
                if (!direct && !c.noDirect)
                {
                    foreach (int p in c.startParts)
                    {
                        direct |= c.goalParts.Contains(p) && GoalLegOk(c, c.startLevel);
                    }
                }
                if (direct && !c.noDirect)
                {
                    if (c.knownDirect != null)
                    {
                        nbrs.Add((c.G, Exact(c, u, c.G, KindLeg, c.knownDirect.TotalCost, WalkOrder(c.knownDirect.NodesReversed))));
                    }
                    else
                    {
                        nbrs.Add((c.G, GetEdge(c, u, c.G, KindLeg, OctileRect(atGround, c.goalRect, c.tc, c.td), at, c.dest, c.mode)));
                    }
                }
                return nbrs;
            }
            int part = NodePart(c, u);
            List<int> same = NodesIn(c, part);
            for (int i = 0; i < same.Count; i++)
            {
                int v = same[i];
                if (v != u)
                {
                    nbrs.Add((v, GetEdge(c, u, v, KindLeg, Octile(atGround, g.nodes[v].ground, c.tc, c.td), at, g.nodes[v].cell, PathEndMode.OnCell)));
                }
            }
            int lk = g.nodes[u].link;
            int h = lk * 3 + 1;
            if (kind == RouteGraph.Low)
            {
                if (g.links[lk].HasRamp)
                {
                    IntVec3 mid = g.MidCell(lk), hc = g.links[lk].bottom;
                    nbrs.Add((h, Exact(c, u, h, KindRamp, CellCost(c.map, mid, c.tp, c.tc) + CellCost(c.map, hc, c.tp, c.tc), new List<IntVec3> { mid, hc })));
                }
                else
                {
                    nbrs.Add((h, Exact(c, u, h, KindRamp, 0, new List<IntVec3>())));
                }
            }
            else if (!c.dig ? g.Passable(lk, c.tp) : g.Usable(lk))
            {
                IntVec3 hc = g.links[lk].bottom;
                nbrs.Add((h, Exact(c, u, h, KindStep, StepCost(c.map, g.links[lk].stepTicks, hc, c.tp), new List<IntVec3> { hc })));
            }
            if (Immediate(c, at))
            {
                nbrs.Add((c.G, Exact(c, u, c.G, KindZero, 0, null)));
            }
            else if (c.goalParts.Contains(part) && GoalLegOk(c, g.nodes[u].level))
            {
                nbrs.Add((c.G, GetEdge(c, u, c.G, KindLeg, OctileRect(atGround, c.goalRect, c.tc, c.td), at, c.dest, c.mode)));
            }
            return nbrs;
        }

        // ------------------------------------------------------------------------------------------------ A*

        private static float[] gScore = new float[0];
        private static int[] parent = new int[0];
        private static bool[] closed = new bool[0];
        private static readonly PriorityQueue<int, float> open = new PriorityQueue<int, float>();
        private static readonly List<int> route = new List<int>();

        private static float EdgeCost(Ctx c, Edge e) => e.cost >= 0 ? e.cost : e.est;

        // The cheapest route by what is known (real costs, else estimates); null when none. Node ids, start first.
        private static List<int> AStar(Ctx c)
        {
            int n = c.N + 2;
            if (gScore.Length < n)
            {
                gScore = new float[n];
                parent = new int[n];
                closed = new bool[n];
            }
            for (int i = 0; i < n; i++)
            {
                gScore[i] = float.MaxValue;
                parent[i] = -1;
                closed[i] = false;
            }
            open.Clear();
            gScore[c.S] = 0;
            open.Enqueue(c.S, H(c, c.S));
            int expanded = 0;
            while (open.Count > 0)
            {
                int u = open.Dequeue();
                if (closed[u])
                {
                    continue;
                }
                if (u == c.G)
                {
                    route.Clear();
                    for (int x = u; x >= 0; x = parent[x])
                    {
                        route.Add(x);
                    }
                    route.Reverse();
                    return route;
                }
                closed[u] = true;
                expanded++;
                List<(int v, Edge e)> list = Neighbours(c, u);
                for (int i = 0; i < list.Count; i++)
                {
                    (int v, Edge e) = list[i];
                    if (e.banned || closed[v])
                    {
                        continue;
                    }
                    float ng = gScore[u] + EdgeCost(c, e);
                    if (ng < gScore[v])
                    {
                        gScore[v] = ng;
                        parent[v] = u;
                        open.Enqueue(v, ng + H(c, v));
                    }
                }
            }
            Dev.PZStats.routerExpanded += expanded;
            return null;
        }

        // ------------------------------------------------------------------------------------------------ legs

        public static int AdmissibilityViolations;
        public static string LastLegFail;
        public static string LastViolation;

        // The real leg of an edge: true when found (cost and nodes set), false when there is none (banned).
        private static bool Refine(Ctx c, Edge e)
        {
            if (e.cost >= 0 || e.banned)
            {
                return !e.banned;
            }
            if (e.target.Cell == e.from && e.mode == PathEndMode.OnCell)
            {
                e.cost = 0;
                e.nodes = new List<IntVec3>();
                return true;
            }
            // goal: a stairs thing touched from the floor above ends on its rim (StairTouch T5)
            bool rim = e.mode == PathEndMode.Touch && e.target.HasThing && StairTouch.Is(e.target.Thing) && Levels.LevelOf(e.from, c.map) == Levels.LevelOf(e.target.Thing.Position, c.map) + 1;
            // goal: an emergency ladder not built yet, from the floor of its foot: the leg ends on the foot
            bool foot = c.footGoal.IsValid && e.target.HasThing && ReferenceEquals(e.target.Thing, c.dest.Thing) && Levels.LevelOf(e.from, c.map) == Levels.LevelOf(c.footGoal, c.map) && Levels.LevelOf(e.from, c.map) != c.destLevel;
            if (foot)
            {
                e.target = c.footGoal;
                e.mode = PathEndMode.OnCell;
            }
            if (!c.dig && !rim && !c.g.FloorReach(e.from, e.target, e.mode, c.reachTp))
            {
                e.banned = true;
                c.g.KeepLeg(c.tp.pawn?.thingIDNumber ?? -1, c.who, e.from, e.target, e.mode, new RouteGraph.LegResult());
                Dev.PZStats.routerLegsRefused++;
                return false;
            }
            c.legsComputed++;
            Dev.PZStats.findLegs++;
            PawnPath p = null;
            if (rim)
            {
                List<IntVec3> cells = new List<IntVec3>();
                StairTouch.UpperCells(e.target.Thing, cells);
                IntVec3 from = e.from;
                cells.SortBy(x => (x - from).LengthHorizontalSquared);
                for (int i = 0; i < cells.Count && i < 3 && p == null; i++)
                {
                    if (!StairTouch.TouchAllowedFrom(cells[i], e.target.Thing))
                    {
                        continue;
                    }
                    p = c.map.pathFinder.FindPathNow(e.from, cells[i], c.tp, c.tuning, PathEndMode.OnCell);
                    if (p != null && !p.Found)
                    {
                        p.Dispose();
                        p = null;
                    }
                }
                if (p != null)
                {
                    Dev.PZStats.rimRoutes++;
                }
            }
            else
            {
                long lt0 = Stopwatch.GetTimestamp();
                p = c.map.pathFinder.FindPathNow(e.from, e.target, c.tp, c.tuning, e.mode);
                Dev.PZStats.routerLegMs += (Stopwatch.GetTimestamp() - lt0) * 1000.0 / Stopwatch.Frequency;
            }
            if (p == null || !p.Found || p.NodesReversed.Count == 0)
            {
                p?.Dispose();
                e.banned = true;
                c.failures++;
                c.g.KeepLeg(c.tp.pawn?.thingIDNumber ?? -1, c.who, e.from, e.target, e.mode, new RouteGraph.LegResult());
                Dev.PZStats.routerLegFails++;
                if (Dev.TestRunner.Active)
                {
                    LastLegFail = $"leg {e.from} L{Levels.LevelOf(e.from, c.map)} -> {e.target} L{Levels.LevelOf(e.target.Cell, c.map)} ({e.mode}) for {c.tp.pawn} at {c.tp.pawn?.Position}: path {(p == null ? "null" : "not found")}; floor reach {c.g.FloorReach(e.from, e.target, e.mode, c.reachTp)}, the pawn's own reach {c.tp.pawn?.CanReach(e.target, e.mode, Danger.Deadly)}";
                }
                return false;
            }
            if (ReferenceEquals(e.target.Thing, c.dest.Thing) && e.target.Cell == c.dest.Cell && e.mode == c.mode && !Immediate(c, p.NodesReversed[0]))
            {
                // (a goal leg that does not end touching the target: the touch rules of a stairs being taken up, K2)
                LastLegFail = $"goal leg {e.from} -> {e.target} ({e.mode}) ends at {p.NodesReversed[0]}, not touching";
                p.Dispose();
                e.banned = true;
                c.failures++;
                Dev.PZStats.routerLegFails++;
                return false;
            }
            e.cost = p.TotalCost;
            e.nodes = WalkOrder(p.NodesReversed);
            if (e.cost + 0.01f < e.est)
            {
                AdmissibilityViolations++;
                LastViolation = $"leg {e.from} -> {e.target} ({e.mode}): real {e.cost} < estimate {e.est}";
            }
            Dev.PZStats.routerLegsChecked++;
            c.g.KeepLeg(c.tp.pawn?.thingIDNumber ?? -1, c.who, e.from, e.target, e.mode, new RouteGraph.LegResult { nodes = new List<IntVec3>(p.NodesReversed), cost = p.TotalCost });
            p.Dispose();
            return true;
        }

        // ------------------------------------------------------------------------------------------------ Find

        private static readonly Ctx ctx = new Ctx();

        // The stats of the last Find (tests).
        public static int LastLegs, LastIterations;
        public static bool LastFinal;
        public static float LastCost;
        public static List<int> LastRoute = new List<int>();

        // The joined path, or null. knownDirect: the game's own same-floor path (option C's check): kept when the stairs
        // are no cheaper (the same object is returned).
        public static PawnPath Find(Map map, IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, TraverseParms tp, PathFinderCostTuning? tuning, PawnPath knownDirect = null)
        {
            if (busy || RouteGraph.For(map) == null)
            {
                return null;
            }
            busy = true;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                if (map.regionDirtyer.AnyDirty)
                {
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                }
                Ctx c = ctx;
                c.estimatesOnly = false;
                c.noDirect = false;
                c.knownDirect = knownDirect;
                if (!Setup(c, map, start, dest, peMode, tp, tuning))
                {
                    return knownDirect;
                }
                if (c.g.Memo(tp.pawn, dest.Cell, c.startMemoPart))
                {
                    Dev.PZStats.routeMemoHits++;
                    return knownDirect;
                }
                // Nothing to search for where reachability says no (the same graph: components, then the traveller).
                if (knownDirect == null && !ReachRefs.WorkingRef(map.reachability) && !map.reachability.CanReach(start, dest, peMode, tp))
                {
                    Dev.PZStats.routeUnreachable++;
                    return null;
                }
                List<int> r = Search(c);
                if (r == null && RouteGraph.DigMode(tp.mode))
                {
                    UseDigParts(c);
                    r = Search(c);
                }
                if (r == null)
                {
                    if (knownDirect == null)
                    {
                        c.g.AddMemo(tp.pawn, dest.Cell, c.startMemoPart);
                    }
                    return knownDirect;
                }
                return Build(c, r);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Finding a path over the stairs failed: " + e, 81512301);
                return knownDirect;
            }
            finally
            {
                busy = false;
                ctx.edges.Clear();
                ctx.knownDirect = null;
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                Dev.PZStats.finds++;
                Dev.PZStats.findMsTotal += ms;
                Dev.PZStats.RecordFind(ms);
                if (ms > Dev.PZStats.findMsMax)
                {
                    Dev.PZStats.findMsMax = ms;
                }
            }
        }

        // A*, refining the legs of the route it picks until they are all real.
        private static List<int> Search(Ctx c)
        {
            bool final = false;
            LastFinal = false;
            LastIterations = 0;
            for (int iter = 0; iter < 200; iter++)
            {
                LastIterations++;
                List<int> r = AStar(c);
                if (r == null)
                {
                    return null;
                }
                bool allReal = true;
                int realOnRoute = 0;
                for (int i = 0; i + 1 < r.Count; i++)
                {
                    Edge e = c.edges[Key(r[i], r[i + 1])];
                    if (e.cost < 0)
                    {
                        allReal = false;
                    }
                    else if (e.kind == KindLeg)
                    {
                        realOnRoute++;
                    }
                }
                if (allReal)
                {
                    LastLegs = c.legsComputed;
                    return new List<int>(r);
                }
                if (c.legsComputed - realOnRoute >= ExtraLegs)
                {
                    final = true;   // enough legs spent: this route is walked as it is, unless one of its legs fails
                    LastFinal = true;
                }
                // Lazy evaluation: the first estimated leg of the route is run for real, then A* again (a leg dearer than
                // its estimate may make another route the cheapest). Once final, the route's remaining legs are run.
                bool failed = false;
                for (int i = 0; i + 1 < r.Count && !failed; i++)
                {
                    Edge e = c.edges[Key(r[i], r[i + 1])];
                    if (e.cost >= 0)
                    {
                        continue;
                    }
                    float est = e.est;
                    if (!Refine(c, e))
                    {
                        failed = true;
                    }
                    else if (!final && e.cost > est + 0.5f)
                    {
                        break;   // dearer than its estimate: the cheapest route may be another one now
                    }
                }
                if (c.failures >= MaxFailures)
                {
                    Dev.PZStats.routeGiveUps++;
                    return null;
                }
                if (final && !failed)
                {
                    LastLegs = c.legsComputed;
                    return new List<int>(r);
                }
            }
            return null;
        }

        private static PawnPath Build(Ctx c, List<int> r)
        {
            if (c.knownDirect != null && r.Count == 2)
            {
                LastCost = c.knownDirect.TotalCost;
                LastRoute = new List<int>(r);
                return c.knownDirect;   // the game's own path stays
            }
            List<IntVec3> walk = new List<IntVec3> { c.start };
            float cost = 0;
            for (int i = 0; i + 1 < r.Count; i++)
            {
                Edge e = c.edges[Key(r[i], r[i + 1])];
                cost += e.cost;
                if (e.nodes != null)
                {
                    walk.AddRange(e.nodes);
                }
            }
            PawnPath p = new PawnPath();
            for (int i = walk.Count - 1; i >= 0; i--)
            {
                p.AddNode(walk[i]);
            }
            IndexRef(p) = p.NodesReversed.Count - 1;
            CostRef(p) = cost;
            LastCost = cost;
            LastRoute = new List<int>(r);
            Dev.PZStats.routesBuilt++;
            return p;
        }

        // ------------------------------------------------------------------------------------------------ option C

        // The lower bound of any route over the stairs from start to dest (estimates only, no leg is run), or
        // float.MaxValue when no stairs route can exist. Used before a same-floor path is taken as it is.
        public static float StairLowerBound(Map map, IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, TraverseParms tp)
        {
            RouteGraph g = RouteGraph.For(map);
            if (busy || g == null || StairGrid.For(map) == null)
            {
                return float.MaxValue;
            }
            busy = true;
            try
            {
                Ctx c = ctx;
                c.estimatesOnly = true;
                c.noDirect = true;
                c.knownDirect = null;
                if (!Setup(c, map, start, dest, peMode, tp, null))
                {
                    return float.MaxValue;
                }
                bool any = false;
                foreach (int p in c.startParts)
                {
                    any |= g.NodesIn(p).Count > 0;
                }
                if (!any)
                {
                    return float.MaxValue;
                }
                return LowerBoundOverStairs(c);
            }
            finally
            {
                busy = false;
                ctx.edges.Clear();
                ctx.estimatesOnly = false;
                ctx.noDirect = false;
            }
        }

        private static float[] lb = new float[0];
        private static readonly PriorityQueue<int, float> lbOpen = new PriorityQueue<int, float>();

        // Dijkstra over estimates in two layers (no stair step taken yet / at least one taken): the cheapest way to the
        // goal that takes at least one stair step. A route that never leaves the floor is the game's own path's business.
        private static float LowerBoundOverStairs(Ctx c)
        {
            int n = c.N + 2;
            if (lb.Length < 2 * n)
            {
                lb = new float[2 * n];
            }
            for (int i = 0; i < 2 * n; i++)
            {
                lb[i] = float.MaxValue;
            }
            lbOpen.Clear();
            lb[c.S] = 0;
            lbOpen.Enqueue(c.S, 0);
            float best = float.MaxValue;
            while (lbOpen.TryDequeue(out int s, out float d))
            {
                if (d > lb[s] || d >= best)
                {
                    continue;
                }
                int u = s % n, layer = s / n;
                List<(int v, Edge e)> list = Neighbours(c, u);
                for (int i = 0; i < list.Count; i++)
                {
                    (int v, Edge e) = list[i];
                    if (e.banned)
                    {
                        continue;
                    }
                    float nd = d + EdgeCost(c, e);
                    if (v == c.G)
                    {
                        if (layer == 1 && nd < best)
                        {
                            best = nd;
                        }
                        continue;
                    }
                    int nl = layer == 1 || e.kind == KindStep ? 1 : 0;
                    int t = nl * n + v;
                    if (nd < lb[t])
                    {
                        lb[t] = nd;
                        lbOpen.Enqueue(t, nd);
                    }
                }
            }
            return best;
        }

        // ------------------------------------------------------------------------------------------------ test oracle

        // Test only: the real cost of every route over the stairs (each stairs used at most once), by enumeration, with
        // real legs (FindPathNow) and the same ramp and step costs; the cheapest, or float.MaxValue. sameFloorDirect adds
        // the game's own direct path when start and dest share a floor.
        public static float OracleCost(Map map, IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, TraverseParms tp, PathFinderCostTuning? tuning, out string best)
        {
            best = "none";
            RouteGraph g = RouteGraph.For(map);
            if (g == null)
            {
                return float.MaxValue;
            }
            g.EnsureComps();
            MoveTicks(tp, out int tc, out int td);
            Dictionary<(IntVec3, IntVec3), float> legCache = new Dictionary<(IntVec3, IntVec3), float>();
            busy = true;
            try
            {
                float Leg(IntVec3 a, LocalTargetInfo b, PathEndMode m)
                {
                    if (m == PathEndMode.OnCell && a == b.Cell)
                    {
                        return 0;
                    }
                    if (legCache.TryGetValue((a, m == PathEndMode.OnCell ? b.Cell : new IntVec3(-1, -1, -1)), out float v))
                    {
                        return v;
                    }
                    if (Levels.LevelOf(a, map) != Levels.LevelOf(b.Cell, map) && !(m == PathEndMode.Touch && ReachabilityImmediate.CanReachImmediate(a, b, map, m, tp.pawn)))
                    {
                        if (!(m == PathEndMode.Touch && b.HasThing && StairTouch.Is(b.Thing)))
                        {
                            return float.MaxValue;
                        }
                    }
                    if (ReachabilityImmediate.CanReachImmediate(a, b, map, m, tp.pawn))
                    {
                        v = 0;
                    }
                    else
                    {
                        PawnPath p = map.pathFinder.FindPathNow(a, b, tp, tuning, m);
                        v = p != null && p.Found ? p.TotalCost : float.MaxValue;
                        p?.Dispose();
                    }
                    legCache[(a, m == PathEndMode.OnCell ? b.Cell : new IntVec3(-1, -1, -1))] = v;
                    return v;
                }
                float bestCost = float.MaxValue;
                string bestName = "none";
                bool[] used = new bool[g.links.Count];
                List<string> names = new List<string>();
                void Go(IntVec3 at, float sofar, int depth)
                {
                    if (sofar >= bestCost)
                    {
                        return;
                    }
                    float fin = Leg(at, dest, peMode);
                    if (fin < float.MaxValue && sofar + fin < bestCost)
                    {
                        bestCost = sofar + fin;
                        bestName = names.Count == 0 ? "direct" : string.Join(" ", names);
                    }
                    if (depth >= 8)
                    {
                        return;
                    }
                    int L = Levels.LevelOf(at, map);
                    for (int i = 0; i < g.links.Count; i++)
                    {
                        if (used[i] || !g.Passable(i, tp))
                        {
                            continue;
                        }
                        VLink l = g.links[i];
                        IntVec3 low = l.low, mid = l.mid;
                        int lk = Levels.LevelOf(low, map);
                        used[i] = true;
                        if (lk == L)
                        {
                            // up: walk to L0 (or from the ramp), L1, H, step (a ladder: walk to the foot, step)
                            float a = !l.HasRamp ? (at == l.bottom ? 0 : Leg(at, low, PathEndMode.OnCell)) : at == mid ? CellCost(map, l.bottom, tp, tc) : at == l.bottom ? 0 : Leg(at, low, PathEndMode.OnCell) + CellCost(map, mid, tp, tc) + CellCost(map, l.bottom, tp, tc);
                            if (a < float.MaxValue)
                            {
                                names.Add("up" + i);
                                Go(l.top, sofar + a + StepCost(map, l.stepTicks, l.top, tp), depth + 1);
                                names.RemoveAt(names.Count - 1);
                            }
                        }
                        else if (Levels.LevelOf(l.top, map) == L)
                        {
                            float a = Leg(at, l.top, PathEndMode.OnCell);
                            if (a < float.MaxValue)
                            {
                                float st = sofar + a + StepCost(map, l.stepTicks, l.bottom, tp);
                                names.Add("down" + i);
                                // the goal from H (the stairs or their ramp, or a touch across the step)
                                if (dest.Cell == l.bottom || dest.Cell == mid || dest.HasThing && dest.Thing == l.thing || ReachabilityImmediate.CanReachImmediate(l.bottom, dest, map, peMode, tp.pawn))
                                {
                                    float f2 = Leg(l.bottom, dest, peMode);
                                    if (f2 < float.MaxValue && st + f2 < bestCost)
                                    {
                                        bestCost = st + f2;
                                        bestName = string.Join(" ", names) + " (from H)";
                                    }
                                }
                                Go(low, st + (l.HasRamp ? CellCost(map, mid, tp, tc) + CellCost(map, low, tp, tc) : 0), depth + 1);
                                names.RemoveAt(names.Count - 1);
                            }
                        }
                        used[i] = false;
                    }
                }
                Go(start, 0, 0);
                best = bestName;
                return bestCost;
            }
            finally
            {
                busy = false;
            }
        }
    }
}
