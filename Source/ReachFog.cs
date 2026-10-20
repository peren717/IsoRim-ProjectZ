using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using ProjectZ.Patches;

namespace ProjectZ
{
    // THE fog rule on maps with floors (user 2026-10-19, final): every place the player's pawns can reach is revealed,
    // and every edge a pawn can stand at lets it see below, all the way down.
    // Reach is walking and stairs, both ways, as in vanilla: vanilla's regions and their links plus ProjectZ's stairs
    // links (StairRegions.LinkRegions, the pathing graph's own test). Jumps are not reach.
    // Seen space (reveal only, never for pathing) is the closure over edges: from every reached or seen region, each
    // open-air cell beside one of its cells (a cliff or slab edge, a tunnel opening onto the open air, a hole: one rule,
    // no case of its own) shows its fall column down to where a fall would land (Column.Landing; a roof on the way ends
    // the view there), and the space at the landing is seen: its region and every region joined to it (vanilla's region
    // links; a closed door stops it, rock and walls too), whose own edges show what lies below them, and so on. The one
    // look up: an open-air cell with a cover over it on the ceiling column of a reached or seen cell (a shaft mined up into
    // a cave) shows the space round it; a hole to the sky shows nothing round it.
    // The walk starts from the regions of the player's pawns (vanilla's own set: Pawn.SpawnSetup's unfog, the player's
    // faction or player controlled) and has two rules of its own:
    //   - fog is no obstacle (vanilla: fog only adds path cost; a
    //     reveal that minded it could never reveal anything);
    //   - a closed door stops it (vanilla: the room behind a door opens when a pawn opens the door).
    // Terrain only: no allowed areas, danger or forbidden doors.
    // What is revealed is what can be seen, the surfaces: each reached or seen cell as vanilla reveals a cave mined into
    // (FogGrid.FloodUnfogAdjacent: the open fog floods, the rock faces 8-adjacent show), the ceiling of each (the cell
    // right above it, up through open air to the first cell that is not open air, each open-air cell with the rock faces
    // round it), the open-air cells of each fall column with the rock faces round them, and the rock a fall lands on.
    // Inner rock and ore, and everything neither reached nor under a seen edge, stay fogged.
    // Runs batched (at most once a frame, only when asked) after: regions rebuilt (rock mined, holes, floors, walls,
    // doors built), floor links changed (stairs built or gone: JumpGrid.Dirty), a column changed (a floor or a roof built
    // or gone: ReachFogMap.ColumnChanged), a player pawn opening a door into fog, a player pawn spawned or teleported,
    // and once after load or generation.
    // Remembered per region id: known clear (no fog in it, round it or above it: never scanned again), and its edges
    // (where they land: worked out once, again when its region or a landing region is rebuilt or a column next to it
    // changes). So a run after the first only walks the region graph and the remembered landings.
    public static class ReachFog
    {
        // counters (tests)
        public static int Runs, LastRegions, LastSeen, LastScanned, LastRevealed, LastSeeds, LastEdgeRegions, EdgeComputes;
        public static double LastMs, MaxMs, LastWalkMs, LastSeenMs;
        // cells unfogged on maps with floors (any cause; ReachFogMap counts them)
        public static int Unfogged;

        // test only (-projectznoreachfog): the rule never runs, to show the old fog
        public static bool Off;

        public static bool PlayerPawn(Pawn p) => p != null && (p.Faction == Faction.OfPlayer || p.IsPlayerControlled);

        private static bool Blocker(IntVec3 c, Map map)
        {
            Building e = c.GetEdifice(map);
            return e != null && e.def.MakeFog;
        }

        private static bool OpenAir(IntVec3 c, Map map) => c.GetTerrain(map) == PZDefOf.PZ_OpenAir && c.GetEdifice(map) == null;

        // Ask for a run on this map (batched: ReachFogMap.MapComponentUpdate).
        public static void Request(Map map)
        {
            if (map != null && !Off)
            {
                ReachFogMap.For(map)?.Ask();
            }
        }

        private static readonly HashSet<Region> reached = new HashSet<Region>();
        private static readonly HashSet<Region> all = new HashSet<Region>();
        private static readonly Queue<Region> open = new Queue<Region>();
        private static readonly Dictionary<Region, List<Region>> links = new Dictionary<Region, List<Region>>();
        private static readonly List<Region> scan = new List<Region>();
        private static bool busy;

        private static bool Enters(Region r)
        {
            return r != null && r.valid && r.type.Passable() && (r.door == null || r.door.Open);
        }

        // a one-way connection of the walk (stairs: both ways, two calls)
        private static void Link(Region from, Region to)
        {
            if (from == null || to == null || from == to)
            {
                return;
            }
            if (!links.TryGetValue(from, out List<Region> l))
            {
                links[from] = l = new List<Region>();
            }
            l.Add(to);
        }

        private static void Visit(Region r)
        {
            if (Enters(r) && reached.Add(r))
            {
                open.Enqueue(r);
            }
        }

        private static void See(Region r)
        {
            if (Enters(r) && all.Add(r))
            {
                open.Enqueue(r);
            }
        }

        // The region id a listed cell stands for now (-1: no passable region there, rock).
        internal static int TargetId(IntVec3 c, RegionGrid rg)
        {
            Region t = rg.GetValidRegionAt_NoRebuild(c);
            return t != null && t.type.Passable() ? t.id : -1;
        }

        // THE walk and reveal (run now).
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Run(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (Off || busy || a == null || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            ReachFogMap comp = ReachFogMap.For(map);
            if (comp == null)
            {
                return;
            }
            busy = true;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            int seeds = 0, scanned = 0, revealed = 0, unfog0 = Unfogged, edgeRegions = 0;
            try
            {
                RegionGrid rg = map.regionGrid;
                reached.Clear();
                all.Clear();
                open.Clear();
                links.Clear();
                // the stairs links of the map, both ways (StairRegions.LinkRegions: the pathing graph's own test)
                StairGrid g = StairGrid.For(map);
                if (g != null)
                {
                    for (int i = 0; i < g.links.Count; i++)
                    {
                        if (StairRegions.LinkRegions(g, g.links[i], rg, out Region b, out Region t))
                        {
                            Link(b, t);
                            Link(t, b);
                        }
                    }
                }
                IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn p = pawns[i];
                    if (PlayerPawn(p))
                    {
                        Region r = rg.GetValidRegionAt(p.Position);
                        if (r != null && r.type.Passable() && reached.Add(r))
                        {
                            open.Enqueue(r);   // (its own region even when it stands in a door)
                            seeds++;
                        }
                    }
                }
                // 1. reach: walking and stairs
                while (open.Count > 0)
                {
                    Region r = open.Dequeue();
                    for (int i = 0; i < r.links.Count; i++)
                    {
                        Visit(r.links[i].GetOtherRegion(r));
                    }
                    if (links.TryGetValue(r, out List<Region> up))
                    {
                        for (int i = 0; i < up.Count; i++)
                        {
                            Visit(up[i]);
                        }
                    }
                }
                LastWalkMs = sw.Elapsed.TotalMilliseconds;
                LastRegions = reached.Count;
                // 2. seen: the closure over edges (reveal only), from every reached region
                foreach (Region r in reached)
                {
                    all.Add(r);
                    open.Enqueue(r);
                }
                while (open.Count > 0)
                {
                    Region r = open.Dequeue();
                    if (!reached.Contains(r))
                    {
                        for (int i = 0; i < r.links.Count; i++)
                        {
                            See(r.links[i].GetOtherRegion(r));
                        }
                    }
                    ReachFogMap.Edges e = comp.EdgesOf(r, rg);
                    if (e.cells.Length > 0)
                    {
                        edgeRegions++;
                    }
                    for (int i = 0; i < e.cells.Length; i++)
                    {
                        if (e.ids[i] >= 0)
                        {
                            See(rg.GetValidRegionAt_NoRebuild(e.cells[i]));
                        }
                    }
                }
                LastSeenMs = sw.Elapsed.TotalMilliseconds - LastWalkMs;
                LastSeen = all.Count - reached.Count;
                // 3. reveal what was reached or seen and is not known clear
                scan.Clear();
                foreach (Region r in all)
                {
                    if (!comp.IsClear(r))
                    {
                        scan.Add(r);
                    }
                }
                FogGrid fog = map.fogGrid;
                for (int k = 0; k < scan.Count; k++)
                {
                    Region r = scan[k];
                    if (!r.valid)
                    {
                        continue;
                    }
                    scanned++;
                    foreach (IntVec3 c in r.Cells)
                    {
                        RevealCell(c, map, fog);
                    }
                    if (reached.Contains(r))
                    {
                        int L = Levels.LevelOf(r.AnyCell, map);
                        if (L != Levels.None && L >= 1)
                        {
                            a.MarkReached(L);
                        }
                    }
                    comp.SetClear(r);
                }
            }
            finally
            {
                busy = false;
                reached.Clear();
                all.Clear();
                open.Clear();
                links.Clear();
                scan.Clear();
                Runs++;
                LastSeeds = seeds;
                LastScanned = scanned;
                LastEdgeRegions = edgeRegions;
                LastRevealed = revealed = Unfogged - unfog0;
                LastMs = sw.Elapsed.TotalMilliseconds;
                MaxMs = System.Math.Max(MaxMs, LastMs);
            }

            if (revealed > 0)
            {
                ProjectZ.DebugLog($"reach fog: {seeds} seed region(s), {LastRegions} reached, {LastSeen} seen, {scanned} scanned, {revealed} cell(s) revealed, {LastMs:0.00} ms (walk {LastWalkMs:0.00} ms, seen {LastSeenMs:0.00} ms)");
            }
        }

        private static readonly List<IntVec3> tCells = new List<IntVec3>();
        private static readonly List<int> tIds = new List<int>();
        private static readonly List<IntVec3> broken = new List<IntVec3>();

        private static void Target(IntVec3 c, int id)
        {
            if (id >= 0 && tIds.Contains(id))
            {
                return;
            }
            tCells.Add(c);
            tIds.Add(id);
        }

        // The edges of region r, worked out now: for each open-air cell beside one of its cells (4-way, on its band), the
        // fall column down to the landing (Column.Landing) is revealed (its open-air cells with the rock faces round them;
        // the rock it lands on), and the landing is listed (its region, or -1 for rock); a roof on the way ends the view
        // there. Then the look up: an open-air cell with a cover over it on the ceiling column of r's cells lists the
        // regions round it. The list keeps one cell per region (and every rock landing: mined later, it opens).
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static ReachFogMap.Edges ComputeEdges(Region r, Map map, RegionGrid rg)
        {
            EdgeComputes++;
            tCells.Clear();
            tIds.Clear();
            FogGrid fog = map.fogGrid;
            foreach (IntVec3 c in r.Cells)
            {
                for (int d = 0; d < 4; d++)
                {
                    IntVec3 n = c + GenAdj.CardinalDirections[d];
                    if (!n.InBounds(map) || !OpenAir(n, map) || !ProjectZ.SameBand(n, c, map))
                    {
                        continue;
                    }
                    IntVec3 land = Column.Landing(n, map, false, true, out _, out _, broken);
                    if (!land.IsValid || land == n)
                    {
                        continue;
                    }
                    IntVec3 g = Levels.Ground(n, map);
                    int top = Levels.LevelOf(n, map);
                    int stop = broken.Count > 0 ? Levels.LevelOf(broken[0], map) : Levels.LevelOf(land, map);
                    for (int L = top; L > stop; L--)
                    {
                        IntVec3 x = Levels.Cell(g, L, map);
                        if (x.IsValid)
                        {
                            RevealOpen(x, map, fog);
                        }
                    }
                    if (broken.Count > 0)
                    {
                        continue;   // (seen from above: the roof, not the room under it)
                    }
                    int id = TargetId(land, rg);
                    if (id < 0 && fog.IsFogged(land))
                    {
                        fog.Unfog(land);   // the rock face the fall lands on
                    }
                    Target(land, id);
                }
                for (IntVec3 up = FloorRoof.Above(c, map); up.IsValid && up.InBounds(map) && OpenAir(up, map); up = FloorRoof.Above(up, map))
                {
                    if (!map.roofGrid.Roofed(up))
                    {
                        continue;
                    }
                    for (int d = 0; d < 4; d++)
                    {
                        IntVec3 w = up + GenAdj.CardinalDirections[d];
                        if (w.InBounds(map) && ProjectZ.SameBand(w, up, map) && w.Walkable(map))
                        {
                            int id = TargetId(w, rg);
                            if (id >= 0)
                            {
                                Target(w, id);
                            }
                        }
                    }
                }
            }
            broken.Clear();
            return new ReachFogMap.Edges { cells = tCells.ToArray(), ids = tIds.ToArray() };
        }

        // An open cell seen without being entered (a fall column): it, and the rock faces round it on its band.
        private static void RevealOpen(IntVec3 x, Map map, FogGrid fog)
        {
            if (fog.IsFogged(x))
            {
                fog.Unfog(x);
            }
            for (int i = 0; i < 8; i++)
            {
                IntVec3 f = x + GenAdj.AdjacentCells[i];
                if (f.InBounds(map) && fog.IsFogged(f) && Blocker(f, map) && ProjectZ.SameBand(f, x, map))
                {
                    fog.Unfog(f);
                }
            }
        }

        // One reached or seen cell: vanilla's reveal of a cell mined into (FogGrid.FloodUnfogAdjacent: its open fog
        // floods, the rock faces round it show) when it or a neighbour is fogged; then its ceiling: up through open air (a
        // shaft, the sky) to the first cell that is not open air, each open-air cell with the rock faces round it
        // (vanilla's faces of an unfogged open cell; the open cells beside it stay as they are).
        private static void RevealCell(IntVec3 c, Map map, FogGrid fog)
        {
            bool any = fog.IsFogged(c);
            for (int i = 0; i < 8 && !any; i++)
            {
                IntVec3 x = c + GenAdj.AdjacentCells[i];
                any = x.InBounds(map) && fog.IsFogged(x);
            }
            if (any)
            {
                fog.FloodUnfogAdjacent(c, sendLetters: false);
            }
            for (IntVec3 up = FloorRoof.Above(c, map); up.IsValid && up.InBounds(map); up = FloorRoof.Above(up, map))
            {
                if (fog.IsFogged(up))
                {
                    fog.Unfog(up);
                }
                if (up.GetTerrain(map) != PZDefOf.PZ_OpenAir || up.GetEdifice(map) != null)
                {
                    break;
                }
                for (int i = 0; i < 8; i++)
                {
                    IntVec3 x = up + GenAdj.AdjacentCells[i];
                    if (x.InBounds(map) && fog.IsFogged(x) && Blocker(x, map))
                    {
                        fog.Unfog(x);
                    }
                }
            }
        }
    }

    // Per map: the run request, the regions known clear and the edges of regions (by region id).
    public class ReachFogMap : MapComponent
    {
        public class Edges
        {
            public IntVec3[] cells;
            public int[] ids;
        }

        private bool asked;
        private readonly HashSet<int> clear = new HashSet<int>();
        private readonly Dictionary<int, Edges> edges = new Dictionary<int, Edges>();
        private bool listening;

        public ReachFogMap(Map map) : base(map)
        {
        }

        private static Map lastMap;
        private static ReachFogMap last;

        public static ReachFogMap For(Map map)
        {
            if (map == null || ProjectZ.Annex(map) == null)
            {
                return null;
            }
            if (!ReferenceEquals(map, lastMap))
            {
                last = map.components == null ? null : map.GetComponent<ReachFogMap>();
                lastMap = map;
            }
            return last;
        }

        public void Ask() => asked = true;

        public bool Asked => asked;

        public bool IsClear(Region r) => clear.Contains(r.id);

        public void SetClear(Region r) => clear.Add(r.id);

        public int EdgesKnown => edges.Count;

        // The edges of r: remembered, worked out again when a listed landing's region is not the one it was (rebuilt,
        // rock mined away, a wall put there).
        public Edges EdgesOf(Region r, RegionGrid rg)
        {
            if (edges.TryGetValue(r.id, out Edges e))
            {
                bool stale = false;
                for (int i = 0; i < e.cells.Length && !stale; i++)
                {
                    stale = ReachFog.TargetId(e.cells[i], rg) != e.ids[i];
                }
                if (!stale)
                {
                    return e;
                }
            }
            e = ReachFog.ComputeEdges(r, map, rg);
            edges[r.id] = e;
            return e;
        }

        // A column changed (a floor or a roof built or gone): the edges of the regions round its cells on every level,
        // and the ceilings, are worked out again.
        public static void ColumnChanged(IntVec3 c, Map map)
        {
            if (Current.ProgramState != ProgramState.Playing || ReachFog.Off)
            {
                return;
            }
            ReachFogMap comp = For(map);
            if (comp == null || comp.edges.Count == 0)
            {
                comp?.Ask();
                return;
            }
            IntVec3 g = Levels.Ground(c, map);
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (!g.IsValid || a == null)
            {
                return;
            }
            RegionGrid rg = map.regionGrid;
            for (int L = a.MinLevel; L <= a.MaxLevel; L++)
            {
                IntVec3 x = Levels.Cell(g, L, map);
                if (!x.IsValid || !x.InBounds(map))
                {
                    continue;
                }
                for (int i = -1; i < 8; i++)
                {
                    IntVec3 y = i < 0 ? x : x + GenAdj.AdjacentCells[i];
                    if (y.InBounds(map))
                    {
                        Region r = rg.GetValidRegionAt_NoRebuild(y);
                        if (r != null)
                        {
                            comp.edges.Remove(r.id);
                        }
                    }
                }
            }
            comp.Ask();
        }

        private void Listen()
        {
            if (!listening && map.events != null)
            {
                listening = true;
                map.events.CellFogChanged += FogChanged;
                map.events.MapFogged += Forget;
                // (regions rebuilt: rock mined, holes, floors, walls and doors built; was JumpGrid.Dirty's job)
                map.events.RegionsRoomsChanged += Ask;
            }
        }

        // fog came back (a refog): what was known clear may not be
        private void FogChanged(IntVec3 c, bool fogged)
        {
            if (!fogged)
            {
                ReachFog.Unfogged++;
                return;
            }
            Forget();
        }

        public void Forget()
        {
            clear.Clear();
            edges.Clear();
            asked = true;
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            lastMap = map;
            last = this;
            Listen();
            clear.Clear();
            edges.Clear();
            asked = true;   // once after load or generation
        }

        public override void MapComponentUpdate()
        {
            if (!asked || Current.ProgramState != ProgramState.Playing || ReachFog.Off)
            {
                return;
            }
            asked = false;
            if (clear.Count > 200000)
            {
                clear.Clear();   // (ids of regions long gone)
            }
            if (edges.Count > 100000)
            {
                edges.Clear();
            }
            ReachFog.Run(map);
        }
    }

    // Trigger: a player pawn spawned (drop pod and shuttle exits, a jump's landing: PawnFlyer spawns the pawn again).
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
    public static class Pawn_SpawnSetup_ReachFog_Patch
    {
        public static void Postfix(Pawn __instance, Map map, bool respawningAfterLoad)
        {
            if (!respawningAfterLoad && __instance.Spawned && ReachFog.PlayerPawn(__instance))
            {
                ReachFog.Request(map);
            }
        }
    }

    // Trigger: a player pawn moved without a spawn (skip and farskip; anything else that teleports a pawn).
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Notify_Teleported))]
    public static class Pawn_Notify_Teleported_ReachFog_Patch
    {
        public static void Postfix(Pawn __instance)
        {
            if (__instance.Spawned && ReachFog.PlayerPawn(__instance))
            {
                ReachFog.Request(__instance.Map);
            }
        }
    }

    // Trigger: a player pawn opens a door with fog behind it (vanilla floods the room; the walk goes on from there).
    [HarmonyPatch(typeof(FogGrid), nameof(FogGrid.Notify_PawnEnteringDoor))]
    public static class FogGrid_PawnEnteringDoor_ReachFog_Patch
    {
        private static readonly AccessTools.FieldRef<FogGrid, Map> mapRef = AccessTools.FieldRefAccess<FogGrid, Map>("map");

        public static void Prefix(FogGrid __instance, Building_Door door, Pawn pawn)
        {
            Map map = mapRef(__instance);
            if (map == null || door == null || ProjectZ.Annex(map) == null || !(pawn.Faction == Faction.OfPlayer || pawn.HostFaction == Faction.OfPlayer))
            {
                return;
            }
            for (int i = 0; i < 4; i++)
            {
                IntVec3 c = door.Position + GenAdj.CardinalDirections[i];
                if (c.InBounds(map) && __instance.IsFogged(c))
                {
                    ReachFog.Request(map);
                    return;
                }
            }
        }
    }
}
