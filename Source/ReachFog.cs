using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using ProjectZ.Patches;

namespace ProjectZ
{
    // THE fog rule on maps with floors (user 2026-10-19, final): every place the player's pawns can reach is revealed.
    // Reach is walking and stairs, both ways, as in vanilla: vanilla's regions and their links plus ProjectZ's stairs
    // links (StairRegions.LinkRegions, the pathing graph's own test). Voluntary jumps are not reach (switch Jumps, off).
    // One more connection, for the reveal only (never for pathing): an open hole joins the spaces it opens into, like
    // vanilla mining into a cave (d929578's hole rule, folded in): the space round the hole's top U leads down to where a
    // fall through it lands (Column.Landing); and back up only when U is covered (a shaft inside the mountain: the cave a
    // hole was mined up into); a hole to the sky shows only U and the rock round it, never the plateau round it.
    // The walk starts from the regions of the player's pawns (vanilla's own set: Pawn.SpawnSetup's unfog, the player's
    // faction or player controlled) and has two rules of its own:
    //   - fog is no obstacle (pathing keeps the player's pawns out of fog over floor links, StairRegions.FogBlocks; a
    //     reveal that minded it could never reveal anything);
    //   - a closed door stops it (vanilla: the room behind a door opens when a pawn opens the door).
    // Terrain only: no allowed areas, danger or forbidden doors.
    // The reached space is revealed like vanilla reveals a cave mined into (FogGrid.FloodUnfogAdjacent per reached cell:
    // the open fog floods, the rock faces 8-adjacent show), and the ceiling of every reached cell shows: the cell right
    // above it, and up through open air (a hole's fall column) to the first cell that is not open air, each open-air cell
    // with the rock round it (one cell per level, never a flood). Nothing that is not reached opens: a plateau or terrace
    // only a jump or a climb gets to, a cave sealed by rock, a room behind a closed door.
    // Runs batched (at most once a frame, only when asked) after: regions rebuilt (rock mined, holes, floors, walls,
    // doors built), floor links changed (stairs built or gone, a column's roofs: JumpGrid.Dirty), a player pawn opening
    // a door into fog, a player pawn spawned or teleported, and once after load or generation (existing holes too).
    // Regions found clear (no fog in them, round them or above them) are remembered by id (fog only goes, except a
    // refog, which forgets them all), so a run after the first only walks the region graph.
    public static class ReachFog
    {
        // counters (tests)
        public static int Runs, LastRegions, LastScanned, LastRevealed, LastSeeds;
        public static double LastMs, MaxMs, LastWalkMs;
        // cells unfogged on maps with floors (any cause; ReachFogMap counts them)
        public static int Unfogged;

        // test only (-projectznoreachfog): the rule never runs, to show the old fog
        public static bool Off;

        // Voluntary jumps down count as reach (JumpGrid links). Off: the user removed them from reach (2026-10-19).
        public static bool Jumps;

        public static bool PlayerPawn(Pawn p) => p != null && (p.Faction == Faction.OfPlayer || p.IsPlayerControlled);

        private static bool Blocker(IntVec3 c, Map map)
        {
            Building e = c.GetEdifice(map);
            return e != null && e.def.MakeFog;
        }

        // Ask for a run on this map (batched: ReachFogMap.MapComponentUpdate).
        public static void Request(Map map)
        {
            if (map != null && !Off)
            {
                ReachFogMap.For(map)?.Ask();
            }
        }

        private static readonly HashSet<Region> reached = new HashSet<Region>();
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

        private static readonly List<Region> holeTop = new List<Region>();

        // The open holes (ShaftMarks: bottoms C whose ceiling is open, Shaft.Is): the regions round the top U (its walkable
        // 4-neighbours on U's level) lead to the region where a fall through U lands; that region leads back to them only
        // when U is covered (roofed: inside the mountain).
        private static void HoleLinks(Map map, RegionGrid rg)
        {
            ShaftMarks marks = ShaftMarks.For(map);
            if (marks == null || marks.cells.Count == 0)
            {
                return;
            }
            foreach (IntVec3 c in marks.cells)
            {
                if (!Shaft.Is(c, map))
                {
                    continue;
                }
                IntVec3 u = FloorRoof.Above(c, map);
                if (!u.IsValid || !u.InBounds(map))
                {
                    continue;
                }
                IntVec3 land = Column.Landing(u, map, false, true, out _, out _);
                Region down = land.IsValid && land.InBounds(map) && land != u ? rg.GetValidRegionAt_NoRebuild(land) : null;
                if (down == null)
                {
                    continue;
                }
                bool covered = map.roofGrid.Roofed(u);
                holeTop.Clear();
                for (int d = 0; d < 4; d++)
                {
                    IntVec3 n = u + GenAdj.CardinalDirections[d];
                    if (n.InBounds(map) && ProjectZ.SameBand(n, u, map) && n.Walkable(map))
                    {
                        Region r = rg.GetValidRegionAt_NoRebuild(n);
                        if (r != null && !holeTop.Contains(r))
                        {
                            holeTop.Add(r);
                        }
                    }
                }
                for (int i = 0; i < holeTop.Count; i++)
                {
                    Link(holeTop[i], down);
                    if (covered)
                    {
                        Link(down, holeTop[i]);
                    }
                }
            }
            holeTop.Clear();
        }

        private static void Visit(Region r)
        {
            if (Enters(r) && reached.Add(r))
            {
                open.Enqueue(r);
            }
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
            int seeds = 0, scanned = 0, revealed = 0, unfog0 = Unfogged;
            try
            {
                RegionGrid rg = map.regionGrid;
                reached.Clear();
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
                HoleLinks(map, rg);
                JumpGrid jg = Jumps ? JumpGrid.For(map) : null;
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
                    if (jg != null)
                    {
                        List<Jump> jumps = jg.Down(r);
                        for (int i = 0; i < jumps.Count; i++)
                        {
                            Visit(jumps[i].to);
                        }
                    }
                }
                LastWalkMs = sw.Elapsed.TotalMilliseconds;
                LastRegions = reached.Count;
                // reveal what was reached and is not known clear
                scan.Clear();
                foreach (Region r in reached)
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
                    int L = Levels.LevelOf(r.AnyCell, map);
                    if (L != Levels.None && L >= 1)
                    {
                        a.MarkReached(L);
                    }
                    comp.SetClear(r);
                }
            }
            finally
            {
                busy = false;
                reached.Clear();
                open.Clear();
                links.Clear();
                scan.Clear();
                Runs++;
                LastSeeds = seeds;
                LastScanned = scanned;
                LastRevealed = revealed = Unfogged - unfog0;
                LastMs = sw.Elapsed.TotalMilliseconds;
                MaxMs = System.Math.Max(MaxMs, LastMs);
            }

            if (revealed > 0)
            {
                ProjectZ.DebugLog($"reach fog: {seeds} seed region(s), {scanned} region(s) scanned, {revealed} cell(s) revealed, {LastMs:0.00} ms (walk {LastWalkMs:0.00} ms)");
            }
        }

        // One reached cell: vanilla's reveal of a cell mined into (FogGrid.FloodUnfogAdjacent: its open fog floods, the
        // rock faces round it show) when it or a neighbour is fogged; then its ceiling: up through open air (a shaft, the
        // sky) to the first cell that is not open air, each open-air cell with the rock faces round it (vanilla's faces of
        // an unfogged open cell; the open cells beside it stay as they are: only reach opens them).
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

    // Per map: the run request and the regions known clear.
    public class ReachFogMap : MapComponent
    {
        private bool asked;
        private readonly HashSet<int> clear = new HashSet<int>();
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

        private void Listen()
        {
            if (!listening && map.events != null)
            {
                listening = true;
                map.events.CellFogChanged += FogChanged;
                map.events.MapFogged += Forget;
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
            if (fogged)
            {
                Forget();
            }
        }

        public void Forget()
        {
            clear.Clear();
            asked = true;
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            lastMap = map;
            last = this;
            Listen();
            clear.Clear();
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
