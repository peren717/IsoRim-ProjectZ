using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode tallfog (tall-mountain fog, isorim-pz-mountains-fog-plan.md §5, adapted to rock-top floors): a fixed-seed
    // tall-mountain map (as rocktop: Mountainous, -projectztallmountains, -projectzfloors=4, -projectzmapsize=250) saved
    // once as pz_tallfog; every check reloads it (G7 reloads pz_tallfog_rev, saved by G2 after its plateau opened), picks
    // its site the same way every run (nearest the colonists) and ends by checking that no fog changed outside the cells
    // it expects. Real flows with ticks:
    //   G1 generation: an independent recount per band (sky unfogged, rim = tops with sky among their 8 neighbours,
    //      faces = rock next to sky or rim, holes and everything else fogged, void rows open), the fill's counters; the
    //      floor HUD offers only the ground and level 1 (no level reached); save + load keeps the fog and the reached list
    //   G2 colonists build stairs from the ground onto a lip on the rim: on completion (nobody walked up) the plateau
    //      behind opens with the rock faces next to it, inner rock and ore stay fogged, nothing else changes; level 1 now
    //      counts as reached (HUD)
    //   G2b colonists put on one plateau bridge a gap of open air to another with slabs: the other plateau opens when
    //      the bridge is done (ReachFog: it is a place the colonists can walk to now)
    //   G3 a colonist with a jump pack jumps from the ground onto a rim cell (close to the cliff and further off): the
    //      plateau opens on landing
    //   G4 a drop pod with only a tame animal, one with only a colony mech, each onto a rim cell: nothing opens while the
    //      pod is closed, the plateau opens when the pawn comes out; a shuttle can't land on the fogged plateau, can on
    //      the opened one
    //   G5 an enemy with a jump pack jumps onto a rim, a wild animal, a guest and a prisoner arrive by pod and walk along
    //      the rim: no fog changes
    //   G6 no placement in fog: a wall, a zone, a jump, a drop pod, a shuttle, stairs whose exit is fogged (refused with
    //      "CannotPlaceInUndiscovered", before any other reason)
    //   G7 colonists on an opened plateau mine a cliff face: vanilla reveals the rock behind, nothing on another level
    //   G8 a colonist skips onto a rim (Royalty): the plateau opens
    //   G9 cost: ReachFog runs (the slowest this mode, a full run, a walk-only run)
    public static partial class TestRunner
    {
        private static bool[] fgBefore;
        private static HashSet<int> fgAllowed;
        private static int fgGenHash;
        private static IntVec3 fgStairs, fgExit, fgU, fgB, fgF, fgStand, fgMine, fgBehind;
        private static Rot4 fgRot;
        private static Pawn fgPawn;
        private static Verb fgVerb;
        private static int fgVariant;
        private static HashSet<int> fgE, fgEB, fgE2;
        private static readonly List<IntVec3> fgGap = new List<IntVec3>();
        private static readonly List<Pawn> fgActors = new List<Pawn>();
        private static readonly Dictionary<Pawn, IntVec3> fgUpAt = new Dictionary<Pawn, IntVec3>();

        // G5: every actor has stood on level 1 (its rim) at some check; the first cell each stood on.
        private static bool FgSeenUp(Map map)
        {
            foreach (Pawn p in fgActors)
            {
                if (!fgUpAt.ContainsKey(p) && p.Spawned && ProjectZ.FloorOf(p.Position, map) == 1)
                {
                    fgUpAt[p] = p.Position;
                }
            }
            return fgUpAt.Count == fgActors.Count;
        }

        private static readonly List<IntVec3> fgWalkTo = new List<IntVec3>();
        private static readonly Dictionary<Pawn, IntVec3> fgActorTo = new Dictionary<Pawn, IntVec3>();

        // ---------------------------------------------------------------- helpers

        // Where every check looks for its site from: a fixed point (the middle of the ground rows), not where the
        // colonists happen to stand after the start's frames, so the sites are the same every run.
        private static IntVec3 FgHome(Map map) => new IntVec3(map.Size.x / 2, 0, ProjectZ.GroundZ(map) / 2);

        private static bool FgBlocker(IntVec3 c, Map map)
        {
            Building e = c.GetEdifice(map);
            return e != null && e.def.MakeFog;
        }

        private static bool[] FgSnap(Map map)
        {
            int n = map.cellIndices.NumGridCells;
            bool[] f = new bool[n];
            for (int i = 0; i < n; i++)
            {
                f[i] = map.fogGrid.IsFogged(i);
            }
            return f;
        }

        private static int FgHash(Map map)
        {
            int h = 17;
            int n = map.cellIndices.NumGridCells;
            for (int i = 0; i < n; i++)
            {
                if (map.fogGrid.IsFogged(i))
                {
                    h = Gen.HashCombineInt(h, i);
                }
            }
            return h;
        }

        // The cells a reveal from `start` (an upper band cell) is expected to open: what a pawn there can walk to and what
        // it can see from the edges, read from the map (terrain, buildings, roofs), not from ReachFog:
        //   - the walk area: the walkable cells 4-connected to it on its band (rock, walls and closed doors stop it; fog
        //     does not), with the rock 8-adjacent to them (vanilla's faces) and the ceiling of each (up through open air to
        //     the first cell that is not open air, each open-air cell with its faces);
        //   - each open-air cell 4-adjacent to the area on its band is an edge: its column down through open air (each
        //     open-air cell with its faces) to the first cell that is not open air: rock is seen (that cell), a walkable
        //     cell is seen with its own walk area on that band (same rules), whose edges are seen in turn (the cascade). A
        //     roof on the way ends the view (a thick roof: nothing of that column is seen);
        //   - an open-air cell with a roof over it on a ceiling column shows the walk areas round it.
        // Nothing floods through open air: a top only reachable across a hole is neither walked to nor seen.
        // Built per map state (the same tick and things) and kept: the candidate loops call this many times.
        private static Map fgxMap;
        private static int fgxTick = -1, fgxThings = -1;
        private static readonly Dictionary<int, int> fgxAreaOf = new Dictionary<int, int>();
        private static readonly List<List<int>> fgxOwn = new List<List<int>>();
        private static readonly List<List<int>> fgxNext = new List<List<int>>();

        private static bool FgOpenAir(IntVec3 c, Map map) => c.InBounds(map) && c.GetTerrain(map) == PZDefOf.PZ_OpenAir && c.GetEdifice(map) == null;

        private static bool FgClosedDoor(IntVec3 c, Map map) => c.GetEdifice(map) is Building_Door d && !d.Open;

        // An area can be entered at c: walkable, not a closed door.
        private static bool FgEnters(IntVec3 c, Map map) => c.InBounds(map) && c.Walkable(map) && !FgClosedDoor(c, map);

        private static void FgFaces(IntVec3 c, Map map, List<int> own)
        {
            foreach (IntVec3 d in GenAdj.AdjacentCells)
            {
                IntVec3 n = c + d;
                if (n.InBounds(map) && FgBlocker(n, map) && ProjectZ.SameBand(n, c, map))
                {
                    own.Add(map.cellIndices.CellToIndex(n));
                }
            }
        }

        // The walk area holding cell s (built on first use): its own cells and the areas its edges and covered ceilings show.
        private static int FgArea(IntVec3 s, Map map)
        {
            CellIndices ci = map.cellIndices;
            if (fgxAreaOf.TryGetValue(ci.CellToIndex(s), out int id))
            {
                return id;
            }
            id = fgxOwn.Count;
            List<int> own = new List<int>();
            List<IntVec3> lands = new List<IntVec3>();
            fgxOwn.Add(own);
            fgxNext.Add(new List<int>());
            List<IntVec3> area = new List<IntVec3>();
            Queue<IntVec3> q = new Queue<IntVec3>();
            fgxAreaOf[ci.CellToIndex(s)] = id;
            q.Enqueue(s);
            bool enters = FgEnters(s, map);
            while (q.Count > 0)
            {
                IntVec3 c = q.Dequeue();
                area.Add(c);
                if (!enters)
                {
                    break;   // (a start that is no walkable cell: itself only)
                }
                foreach (IntVec3 d in GenAdj.CardinalDirections)
                {
                    IntVec3 n = c + d;
                    if (n.InBounds(map) && ProjectZ.SameBand(n, c, map) && FgEnters(n, map) && !fgxAreaOf.ContainsKey(ci.CellToIndex(n)))
                    {
                        fgxAreaOf[ci.CellToIndex(n)] = id;
                        q.Enqueue(n);
                    }
                }
            }
            foreach (IntVec3 c in area)
            {
                own.Add(ci.CellToIndex(c));
                FgFaces(c, map, own);
                IntVec3 g = Levels.Ground(c, map);
                int L = Levels.LevelOf(c, map);
                if (!g.IsValid || L == Levels.None)
                {
                    continue;
                }
                // the edges: open air beside it on its band, down its column
                foreach (IntVec3 d in GenAdj.CardinalDirections)
                {
                    IntVec3 n = c + d;
                    if (!FgOpenAir(n, map) || !ProjectZ.SameBand(n, c, map))
                    {
                        continue;
                    }
                    IntVec3 ng = Levels.Ground(n, map);
                    List<int> col = new List<int> { ci.CellToIndex(n) };
                    FgFaces(n, map, col);
                    IntVec3 land = IntVec3.Invalid;
                    bool thick = false;
                    for (int k = L - 1; ; k--)
                    {
                        IntVec3 below = Levels.Cell(ng, k, map);
                        if (!below.IsValid)
                        {
                            break;
                        }
                        RoofDef r = map.roofGrid.RoofAt(below);
                        if (r != null && r != PZDefOf.PZ_StairwellRoof)
                        {
                            thick = r.isThickRoof;
                            break;   // a roof ends the view
                        }
                        if (FgOpenAir(below, map))
                        {
                            col.Add(ci.CellToIndex(below));
                            FgFaces(below, map, col);
                            continue;
                        }
                        land = below;
                        break;
                    }
                    if (thick)
                    {
                        continue;
                    }
                    own.AddRange(col);
                    if (land.IsValid)
                    {
                        if (FgEnters(land, map))
                        {
                            lands.Add(land);
                        }
                        else
                        {
                            own.Add(ci.CellToIndex(land));   // the rock (or whatever holds) the fall lands on
                        }
                    }
                }
                // the ceiling: up through open air to the first cell that is not open air
                for (int k = L + 1; ; k++)
                {
                    IntVec3 up = Levels.Cell(g, k, map);
                    if (!up.IsValid)
                    {
                        break;
                    }
                    own.Add(ci.CellToIndex(up));
                    if (!FgOpenAir(up, map))
                    {
                        break;
                    }
                    FgFaces(up, map, own);
                    if (map.roofGrid.Roofed(up))
                    {
                        foreach (IntVec3 d in GenAdj.CardinalDirections)
                        {
                            IntVec3 w = up + d;
                            if (FgEnters(w, map) && ProjectZ.SameBand(w, up, map))
                            {
                                lands.Add(w);
                            }
                        }
                    }
                }
            }
            foreach (IntVec3 l in lands)
            {
                fgxNext[id].Add(FgArea(l, map));
            }
            return id;
        }

        private static HashSet<int> FgExpected(Map map, IntVec3 start, out int fogged)
        {
            int things = map.listerThings.AllThings.Count;
            if (fgxMap != map || fgxTick != Find.TickManager.TicksGame || fgxThings != things)
            {
                fgxMap = map;
                fgxTick = Find.TickManager.TicksGame;
                fgxThings = things;
                fgxAreaOf.Clear();
                fgxOwn.Clear();
                fgxNext.Clear();
            }
            HashSet<int> e = new HashSet<int>();
            HashSet<int> done = new HashSet<int>();
            Stack<int> st = new Stack<int>();
            st.Push(FgArea(start, map));
            while (st.Count > 0)
            {
                int a = st.Pop();
                if (!done.Add(a))
                {
                    continue;
                }
                e.UnionWith(fgxOwn[a]);
                foreach (int b in fgxNext[a])
                {
                    st.Push(b);
                }
            }
            fogged = 0;
            foreach (int i in e)
            {
                fogged += map.fogGrid.IsFogged(i) ? 1 : 0;
            }
            return e;
        }

        private static void FgStart(Map map, HashSet<int> allowed)
        {
            fgBefore = FgSnap(map);
            fgAllowed = allowed ?? new HashSet<int>();
            DebugSettings.enableRandomMentalStates = false;
            Find.PlaySettings.autoRebuild = false;
            Find.Storyteller.storytellerComps.Clear();
            Find.Storyteller.incidentQueue.Clear();
        }

        // Fog changes since FgStart: how many, and how many outside the allowed cells.
        private static int FgChanged(Map map, out int outside, out string first)
        {
            outside = 0;
            first = null;
            int n = 0;
            for (int i = 0; i < fgBefore.Length; i++)
            {
                if (map.fogGrid.IsFogged(i) != fgBefore[i])
                {
                    n++;
                    if (!fgAllowed.Contains(i))
                    {
                        outside++;
                        if (first == null)
                        {
                            IntVec3 c = map.cellIndices.IndexToCell(i);
                            first = $"{c} level {Levels.LevelOf(c, map)} {(fgBefore[i] ? "unfogged" : "fogged")}";
                        }
                    }
                }
            }
            return n;
        }

        private static int FgFoggedIn(Map map, HashSet<int> cells) => cells.Count(i => map.fogGrid.IsFogged(i));

        private static void FgEnd(Map map, string check, string next, string load = "pz_tallfog")
        {
            int n = FgChanged(map, out int outside, out string first);
            Result($"{check}: no fog changed outside the expected cells", outside == 0, $"{n} cells changed, {outside} outside{(first == null ? "" : ", first " + first)}; last reach fog run {ReachFog.LastMs:0.00} ms ({ReachFog.LastRegions} regions reached, {ReachFog.LastScanned} scanned), slowest so far {ReachFog.MaxMs:0.00} ms");
            tickWatch = null;
            if (next == null)
            {
                Finish();
                return;
            }
            stage = next;
            pendingLoad = load;
        }

        // A rim cell of level 1 (a top, unfogged, standable) over ground cell g, whose plateau has at least minFog fogged
        // cells behind the rim.
        private static bool FgRim(IntVec3 g, Map map, int minFog, out IntVec3 u, out HashSet<int> e)
        {
            u = IntVec3.Invalid;
            e = null;
            if (!g.InBounds(map) || g.z < 2 || g.z >= ProjectZ.GroundZ(map) - 2 || g.x < 2 || g.x >= map.Size.x - 2)
            {
                return false;
            }
            IntVec3 c = Levels.Cell(g, 1, map);
            if (!c.IsValid || !TmTop(c, map) || map.fogGrid.IsFogged(c) || !c.Standable(map))
            {
                return false;
            }
            e = FgExpected(map, c, out int fogged);
            if (fogged < minFog)
            {
                return false;
            }
            u = c;
            return true;
        }

        private static void FgWork(Map map, IEnumerable<Pawn> who, WorkTypeDef w)
        {
            HashSet<Pawn> set = new HashSet<Pawn>(who);
            foreach (Pawn p in Colonists(map))
            {
                p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
                foreach (WorkTypeDef d in DefDatabase<WorkTypeDef>.AllDefs)
                {
                    if (!p.WorkTypeIsDisabled(d))
                    {
                        p.workSettings.SetPriority(d, set.Contains(p) && (d == w || d == WorkTypeDefOf.Hauling && w == WorkTypeDefOf.Construction) ? 1 : 0);
                    }
                }
            }
        }

        // A ground cell 'from' lo..hi cells (straight-line) from ground cell g, open, unroofed, unfogged, no rise.
        private static IntVec3 FgGroundNear(IntVec3 g, Map map, float lo, float hi, System.Func<IntVec3, bool> ok)
        {
            foreach (IntVec3 s in GenRadial.RadialCellsAround(g, hi, false))
            {
                if (!s.InBounds(map) || s.z >= ProjectZ.GroundZ(map) || TH(s, map) != 0 || !s.Standable(map) || s.Roofed(map) || map.fogGrid.IsFogged(s) || s.DistanceTo(g) < lo)
                {
                    continue;
                }
                if (ok == null || ok(s))
                {
                    return s;
                }
            }
            return IntVec3.Invalid;
        }

        // ---------------------------------------------------------------- G1

        private static void FgGenChecks(Map map, AnnexMapComponent a)
        {
            byte[] h = Mountains.LastHeights;
            int sx = map.Size.x, gz = a.groundZ, cap = a.MaxLevel, n = sx * gz;
            if (h == null || h.Length != n)
            {
                Result("G1 the fill's heights", false, "none (the map was not generated in this run)");
                return;
            }
            int bad = 0, rimN = 0, holeN = 0, skyOpen = 0, topsFog = 0, tops = 0, rockFog = 0, rock = 0, oreFog = 0, ore = 0, faceOre = 0;
            string first = null;
            for (int L = 1; L <= cap; L++)
            {
                // sky: open-air groups (4-way) touching the band's edge or of 20+ cells
                bool[] sky = new bool[n];
                int[] grp = new int[n];
                for (int i = 0; i < n; i++)
                {
                    grp[i] = -1;
                }
                int gid = 0;
                for (int i0 = 0; i0 < n; i0++)
                {
                    if (h[i0] >= L || grp[i0] >= 0)
                    {
                        continue;
                    }
                    List<int> cells = new List<int>();
                    Stack<int> st = new Stack<int>();
                    st.Push(i0);
                    grp[i0] = gid;
                    bool edge = false;
                    while (st.Count > 0)
                    {
                        int i = st.Pop();
                        cells.Add(i);
                        int x = i % sx, z = i / sx;
                        edge |= x == 0 || z == 0 || x == sx - 1 || z == gz - 1;
                        int[] nb = { x > 0 ? i - 1 : -1, x < sx - 1 ? i + 1 : -1, z > 0 ? i - sx : -1, z < gz - 1 ? i + sx : -1 };
                        foreach (int j in nb)
                        {
                            if (j >= 0 && h[j] < L && grp[j] < 0)
                            {
                                grp[j] = gid;
                                st.Push(j);
                            }
                        }
                    }
                    bool open = edge || cells.Count >= Mountains.MinGroup;
                    foreach (int i in cells)
                    {
                        sky[i] = open;
                    }
                    holeN += open ? 0 : cells.Count;
                    gid++;
                }
                bool[] rimA = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    if (h[i] == L && Nb8(i, sx, gz, j => sky[j]))
                    {
                        rimA[i] = true;
                        rimN++;
                    }
                }
                for (int i = 0; i < n; i++)
                {
                    IntVec3 c = Levels.Cell(new IntVec3(i % sx, 0, i / sx), L, map);
                    bool want;
                    if (h[i] < L)
                    {
                        want = !sky[i];
                        skyOpen += sky[i] && !map.fogGrid.IsFogged(c) ? 1 : 0;
                    }
                    else if (h[i] == L)
                    {
                        want = !rimA[i];
                        tops++;
                        topsFog += map.fogGrid.IsFogged(c) ? 1 : 0;
                    }
                    else
                    {
                        want = !Nb8(i, sx, gz, j => sky[j] || rimA[j]);
                        rock++;
                        rockFog += map.fogGrid.IsFogged(c) ? 1 : 0;
                        Building b = c.GetEdifice(map);
                        if (b != null && b.def.building.isResourceRock)
                        {
                            ore++;
                            oreFog += map.fogGrid.IsFogged(c) ? 1 : 0;
                            faceOre += want ? 0 : 1;
                        }
                    }
                    if (map.fogGrid.IsFogged(c) != want)
                    {
                        bad++;
                        first = first ?? $"{c} level {L} H{h[i]} fogged {map.fogGrid.IsFogged(c)}, want {want}";
                    }
                }
            }
            int voidOpen = 0, voidCells = 0;
            for (int z = 0; z < map.Size.z; z++)
            {
                if (Levels.BandOfCell(new IntVec3(0, 0, z), map) >= 0)
                {
                    continue;
                }
                for (int x = 0; x < sx; x++)
                {
                    voidCells++;
                    voidOpen += map.fogGrid.IsFogged(new IntVec3(x, 0, z)) ? 0 : 1;
                }
            }
            Result("G1 band fog as the rim rule says: sky open, a one-cell rim of each top open (8-way), rock next to sky or rim open, holes and the rest fogged",
                bad == 0 && tops > 0, $"{bad} cells differ{(first == null ? "" : ", first " + first)}; tops {tops} ({topsFog} fogged), rim {rimN}, holes {holeN}, rock {rock} ({rockFog} fogged), ore {ore} ({oreFog} fogged, {faceOre} on a cliff face)");
            Result("G1 fill counters equal the recount (rim, holes)", rimN == Mountains.LastRim && holeN == Mountains.LastHoles, $"rim {rimN} (fill {Mountains.LastRim}), holes {holeN} (fill {Mountains.LastHoles})");
            Result("G1 the void rows between the bands are open", voidOpen == voidCells, $"{voidOpen} of {voidCells}");
            Result("G1 no level reached yet", a.reachedLevels != null && a.reachedLevels.Count == 0, $"reached [{string.Join(",", a.reachedLevels ?? new List<int>())}]");
        }

        private static bool Nb8(int i, int sx, int gz, System.Func<int, bool> f)
        {
            int x = i % sx, z = i / sx;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, nz = z + dz;
                    if ((dx != 0 || dz != 0) && nx >= 0 && nz >= 0 && nx < sx && nz < gz && f(nz * sx + nx))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- steps

        private static bool StepTallFog(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            switch (stage)
            {
                case "fg_setup":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    if (a == null || !Mountains.On(map))
                    {
                        Result("tallfog: a tall-mountain map", false, "no annex or no tall mountains (-projectztallmountains)");
                        Finish();
                        return true;
                    }
                    Info($"tallfog: map {map.Size}, tile {map.Tile} {map.TileInfo?.hilliness}, floors {a.floors}; fill {Mountains.LastSeconds:0.00} s, tops {Mountains.LastTops}, rim {Mountains.LastRim}, holes {Mountains.LastHoles}, fogged band cells {Mountains.LastFogged}");
                    FQuiet(map, true);   // (no wild animals, visitors or sleeping threats walking into the checks' sites)
                    FgGenChecks(map, a);
                    LvCheck(map, "G1 floor HUD: no level reached, only the ground and level 1 are offered", 0, 1);
                    fgGenHash = FgHash(map);
                    GameDataSaveLoader.SaveGame("pz_tallfog");
                    stage = "fg_loaded";
                    pendingLoad = "pz_tallfog";
                    return true;
                }
                case "fg_loaded":
                    Result("G1 save + load: the fog of every cell and the reached levels kept", FgHash(map) == fgGenHash && a.reachedLevels != null && a.reachedLevels.Count == 0,
                        $"fog hash {fgGenHash} -> {FgHash(map)}, reached [{string.Join(",", a.reachedLevels ?? new List<int>())}] (null {a.reachedLevels == null})");
                    LvCheck(map, "G1 floor HUD after load: still only the ground and level 1", 0, 1);
                    stage = "fg_g2";
                    return true;

                // ------------------------------------------------------------ G2 stairs onto the rim
                case "fg_g2":
                {
                    IntVec3 home = FgHome(map);
                    Pawn any = Colonists(map).FirstOrDefault();
                    fgStairs = IntVec3.Invalid;
                    foreach (IntVec3 p in Near(home, 90, map))
                    {
                        if (p.z >= a.groundZ - 3 || p.z < 3 || p.x < 3 || p.x >= map.Size.x - 3 || fgStairs.IsValid)
                        {
                            continue;
                        }
                        for (int r = 0; r < 4 && !fgStairs.IsValid; r++)
                        {
                            Rot4 rot = new Rot4(r);
                            IntVec3 exit = StairShape.Exit(p, rot, map);
                            if (!exit.IsValid || !TmTop(exit, map) || map.fogGrid.IsFogged(exit))
                            {
                                continue;
                            }
                            if (StairShape.Footprint(p, rot).Cells.Any(c => !c.InBounds(map) || map.fogGrid.IsFogged(c) || c.Roofed(map) || c.GetEdifice(map) != null || !c.Standable(map))
                                || any == null || !map.reachability.CanReach(any.Position, p, PathEndMode.Touch, TraverseParms.For(TraverseMode.PassDoors))
                                || !GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, p, rot, map, stuffDef: ThingDefOf.WoodLog).Accepted)
                            {
                                continue;
                            }
                            HashSet<int> e = FgExpected(map, exit, out int fogged);
                            if (fogged < 30)
                            {
                                continue;
                            }
                            fgStairs = p;
                            fgRot = rot;
                            fgExit = exit;
                            fgE = e;
                        }
                    }
                    if (!fgStairs.IsValid)
                    {
                        Result("G2 a stairs site whose exit is a rim top with a fogged plateau behind", false, "none found");
                        stage = "fg_g2b";
                        pendingLoad = "pz_tallfog";
                        return true;
                    }
                    FgStart(map, fgE);
                    for (int i = 0; i < 4; i++)
                    {
                        Thing wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                        wood.stackCount = 75;
                        GenPlace.TryPlaceThing(wood, fgStairs - fgRot.FacingCell * 3, map, ThingPlaceMode.Near);
                    }
                    GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, fgStairs, map, fgRot, Faction.OfPlayer, ThingDefOf.WoodLog);
                    FgWork(map, Colonists(map), WorkTypeDefOf.Construction);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    int fogBefore = FgFoggedIn(map, fgE);
                    Info($"G2 stairs {fgStairs} facing {fgRot}, exit {fgExit} (rim, over {ProjectZ.GroundCellOf(fgExit, map)}); its plateau and faces {fgE.Count} cells, {fogBefore} fogged");
                    TickUntil(40000, () => map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).Any(t => t.Position == fgStairs), "fg_g2_built", 5);
                    return true;
                }
                case "fg_g2_built":
                {
                    bool built = map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).Any(t => t.Position == fgStairs);
                    int up = Colonists(map).Count(p => ProjectZ.FloorOf(p.Position, map) >= 1);
                    int left = FgFoggedIn(map, fgE);
                    // inner rock: band-1 rock fogged before and not 8-adjacent to the plateau still fogged
                    int band = Levels.BandOfCell(fgExit, map);
                    int innerOre = 0, innerOreFogged = 0, innerRock = 0, innerRockFogged = 0;
                    CellRect box = CellRect.FromLimits(fgE.Min(i => map.cellIndices.IndexToCell(i).x), fgE.Min(i => map.cellIndices.IndexToCell(i).z), fgE.Max(i => map.cellIndices.IndexToCell(i).x), fgE.Max(i => map.cellIndices.IndexToCell(i).z)).ExpandedBy(3).ClipInsideMap(map);
                    foreach (IntVec3 c in box)
                    {
                        Building b = c.GetEdifice(map);
                        if (Levels.BandOfCell(c, map) == band && b != null && b.def.building != null && b.def.building.isNaturalRock && !fgE.Contains(map.cellIndices.CellToIndex(c)) && fgBefore[map.cellIndices.CellToIndex(c)])
                        {
                            innerRock++;
                            innerRockFogged += map.fogGrid.IsFogged(c) ? 1 : 0;
                            if (b.def.building.isResourceRock)
                            {
                                innerOre++;
                                innerOreFogged += map.fogGrid.IsFogged(c) ? 1 : 0;
                            }
                        }
                    }
                    Result("G2 colonists built stairs onto the rim: on completion (nobody up there yet) the plateau behind opened with the rock faces next to it",
                        built && left == 0 && up == 0, $"built {built}; expected cells still fogged {left} of {fgE.Count}; colonists upstairs {up}; reach fog run {ReachFog.LastMs:0.00} ms over {ReachFog.LastRegions} regions");
                    Result("G2 inner rock and ore near the plateau stay fogged", innerRock > 0 && innerRock == innerRockFogged && innerOre == innerOreFogged,
                        $"fogged rock not on a face within 3 cells of the plateau: {innerRockFogged} of {innerRock} still fogged (ore {innerOreFogged} of {innerOre})");
                    Result("G2 level 1 counts as reached", a.Reached(1) && a.reachedLevels.Count == 1, $"reached [{string.Join(",", a.reachedLevels)}]");
                    FgWork(map, new Pawn[0], null);
                    fgE2 = fgE;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    // (the HUD slides for 150 ms and refreshes every 0.25 s, real time)
                    LvWait(0.8f, "fg_g2_hud");
                    return true;
                }
                case "fg_g2_hud":
                {
                    LvCheck(map, "G2 floor HUD: level 1 reached, levels 0..2 offered", 0, 2);
                    // the opened plateau for G7 and the IsoRim lid shots
                    GameDataSaveLoader.SaveGame("pz_tallfog_rev");
                    IntVec3 g = ProjectZ.GroundCellOf(fgExit, map);
                    Info($"FIXTURE pz_tallfog_rev: the plateau over ground cells round {g} (exit {fgExit}) opened by the stairs at {fgStairs}");
                    FgEnd(map, "G2", "fg_g2b");
                    return true;
                }

                // ------------------------------------------------------------ G2b a slab bridge, then a step next to fog
                case "fg_g2b":
                {
                    IntVec3 home = FgHome(map);
                    fgU = fgB = IntVec3.Invalid;
                    fgGap.Clear();
                    int tried = 0;
                    foreach (IntVec3 g in Near(home, 110, map))
                    {
                        if (fgU.IsValid || tried > 4000)
                        {
                            break;
                        }
                        IntVec3 u = Levels.Cell(g, 1, map);
                        if (!g.InBounds(map) || g.z < 3 || g.z >= a.groundZ - 3 || !TmTop(u, map) || map.fogGrid.IsFogged(u) || !u.Standable(map))
                        {
                            continue;
                        }
                        foreach (IntVec3 d in GenAdj.CardinalDirections)
                        {
                            // a gap of 1-3 open-air cells (over open ground) from u to another rim top
                            List<IntVec3> gap = new List<IntVec3>();
                            IntVec3 b = IntVec3.Invalid;
                            for (int k = 1; k <= 4; k++)
                            {
                                IntVec3 c = u + d * k;
                                if (!c.InBounds(map) || Levels.BandOfCell(c, map) != Levels.BandOfCell(u, map))
                                {
                                    break;
                                }
                                if (c.GetTerrain(map) == PZDefOf.PZ_OpenAir && !map.fogGrid.IsFogged(c) && c.GetEdifice(map) == null)
                                {
                                    gap.Add(c);
                                    continue;
                                }
                                if (gap.Count >= 1 && TmTop(c, map) && !map.fogGrid.IsFogged(c) && c.Standable(map))
                                {
                                    b = c;
                                }
                                break;
                            }
                            if (!b.IsValid || gap.Count > 3)
                            {
                                continue;
                            }
                            tried++;
                            HashSet<int> eu = FgExpected(map, u, out int fu);
                            if (eu.Contains(map.cellIndices.CellToIndex(b)))
                            {
                                continue;   // the same plateau
                            }
                            HashSet<int> eb = FgExpected(map, b, out int fb);
                            if (fb < 10 || !gap.All(c => GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_UpperFloor, c, Rot4.North, map).Accepted))
                            {
                                continue;
                            }
                            // a fogged walkable cell of b's plateau to send a colonist to
                            IntVec3 f = eb.Select(i => map.cellIndices.IndexToCell(i)).Where(c => map.fogGrid.IsFogged(c) && c.Walkable(map) && !FgBlocker(c, map)).OrderBy(c => c.DistanceToSquared(b)).Skip(2).FirstOrDefault();
                            if (!f.IsValid || f == IntVec3.Zero)
                            {
                                continue;
                            }
                            fgU = u;
                            fgB = b;
                            fgF = f;
                            fgGap.AddRange(gap);
                            fgEB = eb;
                            break;
                        }
                    }
                    if (!fgU.IsValid)
                    {
                        Result("G2b two plateaus with a 1-3 cell gap of open air a slab bridge can span", false, $"none found ({tried} gaps tried)");
                        stage = "fg_g3";
                        pendingLoad = "pz_tallfog";
                        return true;
                    }
                    // (the arrival on the first plateau is G3/G4's: two builders put on its rim, it opens)
                    fgActors.Clear();
                    fgActors.AddRange(Colonists(map).Where(p => !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)).Take(2));
                    foreach (Pawn p in fgActors)
                    {
                        Teleport(p, fgU);
                    }
                    for (int i = 0; i < 2; i++)
                    {
                        Thing wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                        wood.stackCount = 75;
                        GenPlace.TryPlaceThing(wood, fgU, map, ThingPlaceMode.Near);
                    }
                    foreach (IntVec3 c in fgGap)
                    {
                        GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_UpperFloor, c, map, Rot4.North, Faction.OfPlayer, null);
                    }
                    ReachFog.Run(map);   // (their arrival opened the first plateau: before the snapshot)
                    FgStart(map, fgEB);
                    FgWork(map, fgActors, WorkTypeDefOf.Construction);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    Info($"G2b builders {string.Join(",", fgActors)} on {fgU}; slabs {string.Join(" ", fgGap)}; the other rim {fgB} (its plateau {fgEB.Count} cells, {FgFoggedIn(map, fgEB)} fogged); target in its fog {fgF}");
                    TickUntil(40000, () => fgGap.All(c => c.GetTerrain(map) != PZDefOf.PZ_OpenAir) && FgDraftAll(), "fg_g2b_built", 5);
                    return true;
                }
                case "fg_g2b_built":
                {
                    bool built = fgGap.All(c => c.GetTerrain(map) != PZDefOf.PZ_OpenAir);
                    int left = FgFoggedIn(map, fgEB);
                    int changed = FgChanged(map, out int outside, out _);
                    // (ReachFog: the bridge makes the other plateau a place the colonists can walk to; it opens on
                    // completion, nobody has to step next to its fog)
                    Result("G2b colonists bridged the gap with slabs: the other plateau opened on completion (now reachable)", built && left == 0,
                        $"slabs built {built}; fog changes {changed} ({outside} outside the other plateau); other plateau still fogged {left} of {fgEB.Count}");
                    FgEnd(map, "G2b", "fg_g3");
                    return true;
                }

                // ------------------------------------------------------------ G3 a jump pack onto the rim (close, far)
                case "fg_g3":
                {
                    IntVec3 home = FgHome(map);
                    fgPawn = Colonists(map).FirstOrDefault(p => !p.Downed && p.health.capacities.CapableOf(PawnCapacityDefOf.Moving));
                    Apparel pack = (Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Apparel_PackJump"));
                    fgPawn.apparel.Wear(pack, dropReplacedApparel: false);
                    fgVerb = pack.GetComp<CompApparelVerbOwner>().AllVerbs.First(v => v is Verb_Jump);
                    float lo = fgVariant == 0 ? 1.4f : 5f, hi = fgVariant == 0 ? 2.9f : 6.5f;
                    fgU = fgStand = IntVec3.Invalid;
                    int refusedClose = 0;
                    foreach (IntVec3 g in Near(home, 90, map))
                    {
                        if (!FgRim(g, map, 30, out IntVec3 u, out HashSet<int> e))
                        {
                            continue;
                        }
                        IntVec3 s = FgGroundNear(g, map, lo, hi, x =>
                        {
                            Teleport(fgPawn, x);
                            if (fgVerb.ValidateTarget(u, false))
                            {
                                return true;
                            }
                            refusedClose++;
                            return false;
                        });
                        if (s.IsValid)
                        {
                            fgU = u;
                            fgStand = s;
                            fgE = e;
                            break;
                        }
                        if (refusedClose > 300)
                        {
                            break;
                        }
                    }
                    string which = fgVariant == 0 ? "close to the cliff (1.4-2.9 cells)" : "further off (5-6.5 cells)";
                    if (!fgU.IsValid)
                    {
                        Info($"G3 {which}: no rim cell accepted as a jump target ({refusedClose} tries refused: the jump arc)");
                        if (fgVariant == 1)
                        {
                            Result($"G3 a rim cell a jump pack can target from {which}", false, "none");
                        }
                        FgG3Next(map);
                        return true;
                    }
                    FgStart(map, fgE);
                    Teleport(fgPawn, fgStand);
                    Draft(fgPawn, true);
                    fgVerb.OrderForceTarget(fgU);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    Info($"G3 {which}: {fgPawn} at {fgStand} jumps to the rim {fgU} (plateau {fgE.Count} cells, {FgFoggedIn(map, fgE)} fogged; {refusedClose} other stands refused first)");
                    TickUntil(1500, () => fgPawn.Spawned && ProjectZ.FloorOf(fgPawn.Position, map) == 1, "fg_g3_landed", 5);
                    return true;
                }
                case "fg_g3_landed":
                {
                    int left = FgFoggedIn(map, fgE);
                    string which = fgVariant == 0 ? "close to the cliff" : "further off";
                    Result($"G3 a jump pack jump onto the rim from {which}: the plateau opened on landing", fgPawn.Spawned && ProjectZ.FloorOf(fgPawn.Position, map) == 1 && left == 0,
                        $"{fgPawn} at {fgPawn.Position} level {ProjectZ.FloorOf(fgPawn.Position, map)}, aimed {fgU}; plateau cells still fogged {left} of {fgE.Count}");
                    int n = FgChanged(map, out int outside, out string first);
                    Result($"G3 ({which}): no fog changed outside the expected cells", outside == 0, $"{n} changed, {outside} outside{(first == null ? "" : ", first " + first)}");
                    FgG3Next(map);
                    return true;
                }

                // ------------------------------------------------------------ G4 pods with a tame animal, a mech; shuttles
                case "fg_g4":
                {
                    IntVec3 home = FgHome(map);
                    fgU = IntVec3.Invalid;
                    int skip = fgVariant;
                    foreach (IntVec3 g in Near(home, 90, map))
                    {
                        if (FgRim(g, map, 40, out IntVec3 u, out HashSet<int> e) && skip-- <= 0)
                        {
                            fgU = u;
                            fgE = e;
                            break;
                        }
                    }
                    if (!fgU.IsValid)
                    {
                        Result("G4 a rim cell with a fogged plateau", false, "none");
                        stage = "fg_g5";
                        pendingLoad = "pz_tallfog";
                        return true;
                    }
                    PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamed(fgVariant == 0 ? "Husky" : "Mech_Lifter");
                    fgPawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfPlayer, forceGenerateNewPawn: true));
                    FgStart(map, fgE);
                    if (fgVariant == 0)
                    {
                        // a shuttle can't land on a fogged plateau (its footprint all in a plateau's fog, not rock)
                        IntVec3 f = IntVec3.Invalid;
                        foreach (IntVec3 g in Near(FgHome(map), 110, map))
                        {
                            IntVec3 c = Levels.Cell(g, 1, map);
                            if (c.IsValid && TmTop(c, map) && map.fogGrid.IsFogged(c) && GenAdj.OccupiedRect(c, ThingDefOf.Shuttle.defaultPlacingRot, ThingDefOf.Shuttle.size).ExpandedBy(1).Cells.All(x => x.InBounds(map) && map.fogGrid.IsFogged(x) && TmTop(x, map)))
                            {
                                f = c;
                                break;
                            }
                        }
                        AcceptanceReport r = f.IsValid && f != IntVec3.Zero ? RoyalTitlePermitWorker_CallShuttle.ShuttleCanLandHere(f, map) : AcceptanceReport.WasAccepted;
                        Result("G4 a shuttle can't land on a plateau still in fog", f.IsValid && f != IntVec3.Zero && !r.Accepted, $"at {f}: '{(r.Accepted ? "accepted" : r.Reason)}'");
                    }
                    ActiveTransporterInfo info = new ActiveTransporterInfo();
                    info.innerContainer.TryAdd(fgPawn);
                    DropPodUtility.MakeDropPodAt(fgU, map, info, Faction.OfPlayer);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    Info($"G4 a drop pod with only {fgPawn} ({kind.defName}, faction {fgPawn.Faction}) aimed at the rim {fgU} (plateau {fgE.Count} cells, {FgFoggedIn(map, fgE)} fogged)");
                    TickUntil(1500, () => map.listerThings.AllThings.Any(t => t is ActiveTransporter && t.Position.DistanceTo(fgU) < 3f) || fgPawn.Spawned, "fg_g4_landed", 1);
                    return true;
                }
                case "fg_g4_landed":
                {
                    int changed = FgChanged(map, out _, out _);
                    Result($"G4 ({fgPawn.def.defName}) the pod landed and is still closed: nothing opened", !fgPawn.Spawned && changed == 0, $"pawn spawned {fgPawn.Spawned}, fog changes {changed}");
                    TickUntil(1500, () => fgPawn.Spawned, "fg_g4_out", 5);
                    return true;
                }
                case "fg_g4_out":
                {
                    int left = FgFoggedIn(map, fgE);
                    Result($"G4 ({fgPawn.def.defName}, the player's) came out of the pod on the rim: the plateau opened", fgPawn.Spawned && left == 0,
                        $"{fgPawn} at {(fgPawn.Spawned ? fgPawn.Position.ToString() : "-")}; plateau cells still fogged {left} of {fgE.Count}");
                    if (fgVariant == 0)
                    {
                        // and a shuttle can land on the opened plateau
                        IntVec3 ok = fgE.Select(i => map.cellIndices.IndexToCell(i)).Where(c => RoyalTitlePermitWorker_CallShuttle.ShuttleCanLandHere(c, map).Accepted).FirstOrDefault();
                        Result("G4 a shuttle can land on the opened plateau", ok.IsValid && ok != IntVec3.Zero, $"first accepted at {ok}");
                    }
                    int n = FgChanged(map, out int outside, out string first);
                    Result($"G4 ({fgPawn.def.defName}): no fog changed outside the expected cells", outside == 0, $"{n} changed, {outside} outside{(first == null ? "" : ", first " + first)}");
                    fgVariant++;
                    tickWatch = null;
                    if (fgVariant < 2)
                    {
                        stage = "fg_g4";
                    }
                    else
                    {
                        fgVariant = 0;
                        stage = "fg_g5";
                    }
                    pendingLoad = "pz_tallfog";
                    return true;
                }

                // ------------------------------------------------------------ G5 non-player arrivals open nothing
                case "fg_g5":
                {
                    IntVec3 home = FgHome(map);
                    List<IntVec3> rims = new List<IntVec3>();
                    fgWalkTo.Clear();
                    fgActorTo.Clear();
                    HashSet<int> used = new HashSet<int>();
                    foreach (IntVec3 g in Near(home, 100, map))
                    {
                        if (rims.Count >= 4)
                        {
                            break;
                        }
                        if (FgRim(g, map, 30, out IntVec3 u, out HashSet<int> e) && !used.Contains(map.cellIndices.CellToIndex(u)))
                        {
                            // a rim cell to walk to along the same plateau's rim, 4+ cells away
                            IntVec3 to = e.Select(i => map.cellIndices.IndexToCell(i)).Where(c => !map.fogGrid.IsFogged(c) && c.Walkable(map) && c.DistanceTo(u) >= 4f && c.DistanceTo(u) <= 10f
                                && map.reachability.CanReach(u, c, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors))).FirstOrDefault();
                            if (!to.IsValid || to == IntVec3.Zero)
                            {
                                continue;
                            }
                            rims.Add(u);
                            fgWalkTo.Add(to);
                            used.UnionWith(e);
                        }
                    }
                    if (rims.Count < 4)
                    {
                        Result("G5 four rim cells of different plateaus", false, $"{rims.Count} found");
                        stage = "fg_g6";
                        pendingLoad = "pz_tallfog";
                        return true;
                    }
                    FgStart(map, null);
                    fgActors.Clear();
                    // an enemy with a jump pack, on the ground near the first rim
                    Faction enemy = Find.FactionManager.AllFactions.Where(x => x.HostileTo(Faction.OfPlayer) && x.def.humanlikeFaction && !x.def.hidden && !x.temporary && x.def.pawnGroupMakers != null).OrderBy(x => x.loadID).First();
                    Pawn raider = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Mercenary_Gunner"), enemy, forceGenerateNewPawn: true));
                    raider.equipment.DestroyAllEquipment();
                    Apparel pack = (Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Apparel_PackJump"));
                    raider.apparel.Wear(pack, dropReplacedApparel: false);
                    Verb jump = pack.GetComp<CompApparelVerbOwner>().AllVerbs.First(v => v is Verb_Jump);
                    IntVec3 g0 = ProjectZ.GroundCellOf(rims[0], map);
                    IntVec3 stand = IntVec3.Invalid;
                    GenSpawn.Spawn(raider, FgGroundNear(g0, map, 2f, 6f, null).IsValid ? FgGroundNear(g0, map, 2f, 6f, null) : CellFinder.StandableCellNear(g0, map, 8f), map);
                    foreach (IntVec3 s in GenRadial.RadialCellsAround(g0, 6.5f, false))
                    {
                        if (!s.InBounds(map) || s.z >= a.groundZ || TH(s, map) != 0 || !s.Standable(map) || s.Roofed(map) || s.DistanceTo(g0) < 2f)
                        {
                            continue;
                        }
                        Teleport(raider, s);
                        if (jump.ValidateTarget(rims[0], false))
                        {
                            stand = s;
                            break;
                        }
                    }
                    Job job = JobMaker.MakeJob(JobDefOf.CastJump, rims[0]);
                    job.verbToUse = jump;
                    raider.jobs.StartJob(job, JobCondition.InterruptForced);
                    fgActors.Add(raider);
                    fgActorTo[raider] = fgWalkTo[0];
                    // a wild animal, a guest and a prisoner by pod onto the other three
                    Faction friend = Find.FactionManager.AllFactions.Where(x => !x.IsPlayer && !x.HostileTo(Faction.OfPlayer) && x.def.humanlikeFaction && !x.def.hidden && !x.temporary && x.def.pawnGroupMakers != null).OrderBy(x => x.loadID).FirstOrDefault();
                    Pawn wild = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Husky"), null, forceGenerateNewPawn: true));
                    Pawn guest = friend == null ? null : PawnGenerator.GeneratePawn(new PawnGenerationRequest(friend.RandomPawnKind(), friend, forceGenerateNewPawn: true));
                    Pawn prisoner = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Mercenary_Gunner"), enemy, forceGenerateNewPawn: true));
                    prisoner.equipment.DestroyAllEquipment();
                    prisoner.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
                    Pawn[] podded = { wild, guest, prisoner };
                    for (int i = 0; i < 3; i++)
                    {
                        if (podded[i] == null)
                        {
                            continue;
                        }
                        ActiveTransporterInfo info = new ActiveTransporterInfo();
                        info.innerContainer.TryAdd(podded[i]);
                        DropPodUtility.MakeDropPodAt(rims[i + 1], map, info, null);
                        fgActors.Add(podded[i]);
                        fgActorTo[podded[i]] = fgWalkTo[i + 1];
                    }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    Info($"G5 enemy {raider} ({raider.Faction}) at {raider.Position} (jump target valid from {stand}) jumps to {rims[0]}; pods: wild {wild} -> {rims[1]}, guest {guest?.ToString() ?? "none (no friendly faction)"} ({guest?.Faction}) -> {rims[2]}, prisoner {prisoner} (host {prisoner.HostFaction}) -> {rims[3]}");

                    // (each one counts once it has stood on its rim: one may walk or jump down again before the last
                    // one arrives)
                    fgUpAt.Clear();
                    TickUntil(2400, () => FgSeenUp(map), "fg_g5_up", 10);
                    return true;
                }
                case "fg_g5_up":
                {
                    int changed = FgChanged(map, out _, out _);
                    string where = string.Join("; ", fgActors.Select(p => $"{p} {(fgUpAt.TryGetValue(p, out IntVec3 at) ? "up at " + at : "never up")}, now {(p.Spawned ? p.Position + " level " + ProjectZ.FloorOf(p.Position, map) : "not spawned")}"));
                    Result("G5 an enemy jumped onto a rim, a wild animal, a guest and a prisoner came out of pods on rims: no fog changed",
                        fgUpAt.Count == fgActors.Count && changed == 0, $"{where}; fog changes {changed}");
                    // (one that went down since walks from where it stood on its rim)
                    foreach (Pawn p in fgActors)
                    {
                        if (p.Spawned && ProjectZ.FloorOf(p.Position, map) != 1 && fgUpAt.TryGetValue(p, out IntVec3 back))
                        {
                            Info($"G5 {p} left its rim ({p.Position}): back to {back} for the walk");
                            Teleport(p, back);
                        }
                    }
                    // each walks along its rim (path steps next to the fog)
                    for (int i = 0; i < fgActors.Count; i++)
                    {
                        Pawn p = fgActors[i];
                        if (p.Spawned)
                        {
                            p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, fgActorTo[p]), JobCondition.InterruptForced);
                        }
                    }
                    TickUntil(1500, () => false, "fg_g5_walked", 60);
                    return true;
                }
                case "fg_g5_walked":
                {
                    int changed = FgChanged(map, out _, out _);
                    string where = string.Join("; ", fgActors.Select(p => $"{p} {(p.Spawned ? p.Position + " level " + ProjectZ.FloorOf(p.Position, map) : "not spawned")}"));
                    Result("G5 they walked about on the rims (next to the fog): still no fog changed", changed == 0, $"{where}; fog changes {changed}");
                    fgWalkTo.Clear();
                    FgEnd(map, "G5", "fg_g6");
                    return true;
                }

                // ------------------------------------------------------------ G6 nothing placed in fog
                case "fg_g6":
                {
                    IntVec3 home = FgHome(map);
                    IntVec3 f = IntVec3.Invalid;
                    foreach (IntVec3 g in Near(home, 90, map))
                    {
                        IntVec3 c = Levels.Cell(g, 1, map);
                        if (c.IsValid && TmTop(c, map) && map.fogGrid.IsFogged(c) && c.Walkable(map) && GenAdj.CellsAdjacent8Way(new TargetInfo(c, map)).All(x => x.InBounds(map) && map.fogGrid.IsFogged(x)))
                        {
                            f = c;
                            break;
                        }
                    }
                    if (!f.IsValid)
                    {
                        Result("G6 a fogged plateau cell", false, "none");
                        stage = "fg_g7";
                        pendingLoad = "pz_tallfog_rev";
                        return true;
                    }
                    FgStart(map, null);
                    Pawn col = Colonists(map).First();
                    Designator_Build wall = new Designator_Build(ThingDefOf.Wall);
                    wall.SetStuffDef(ThingDefOf.WoodLog);
                    AcceptanceReport rWall = wall.CanDesignateCell(f);
                    AcceptanceReport rZone = new Designator_ZoneAddStockpile_Resources().CanDesignateCell(f);
                    bool rJump = JumpUtility.ValidJumpTarget(col, map, f);
                    bool rPod = TargetingParameters.ForDropPodsDestination().validator(new TargetInfo(f, map));
                    AcceptanceReport rShuttle = RoyalTitlePermitWorker_CallShuttle.ShuttleCanLandHere(f, map);
                    Result("G6 nothing is placed in fog: a wall, a stockpile, a jump, a drop pod and a shuttle are refused on a fogged plateau cell",
                        !rWall.Accepted && !rZone.Accepted && !rJump && !rPod && !rShuttle.Accepted,
                        $"at {f}: wall '{(rWall.Accepted ? "accepted" : rWall.Reason)}', zone {(rZone.Accepted ? "accepted" : "refused")}, jump {(rJump ? "valid" : "refused")}, pod {(rPod ? "valid" : "refused")}, shuttle '{(rShuttle.Accepted ? "accepted" : rShuttle.Reason)}'");
                    // stairs whose exit is a fogged cell: the landing check comes first
                    IntVec3 loc = IntVec3.Invalid;
                    Rot4 rot = Rot4.North;
                    string why = null;
                    foreach (IntVec3 g in Near(home, 90, map))
                    {
                        for (int r = 0; r < 4 && !loc.IsValid; r++)
                        {
                            IntVec3 exit = StairShape.Exit(g, new Rot4(r), map);
                            if (exit.IsValid && exit.InBounds(map) && map.fogGrid.IsFogged(exit) && StairShape.Footprint(g, new Rot4(r)).Cells.All(c => c.InBounds(map) && ProjectZ.FloorOf(c, map) == 0))
                            {
                                loc = g;
                                rot = new Rot4(r);
                            }
                        }
                        if (loc.IsValid)
                        {
                            break;
                        }
                    }
                    if (loc.IsValid)
                    {
                        AcceptanceReport pw = new PlaceWorker_Stairs().AllowsPlacing(PZDefOf.PZ_Stairs, loc, rot, map);
                        AcceptanceReport full = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, loc, rot, map, stuffDef: ThingDefOf.WoodLog);
                        why = pw.Reason;
                        Result("G6 stairs whose exit is fogged are refused, with the fog as the reason (no other reason tells what the fog hides)",
                            !pw.Accepted && pw.Reason == "CannotPlaceInUndiscovered".Translate() && !full.Accepted,
                            $"stairs at {loc} facing {rot}, exit {StairShape.Exit(loc, rot, map)} fogged: placeworker '{why}', blueprint '{(full.Accepted ? "accepted" : full.Reason)}'");
                    }
                    else
                    {
                        Result("G6 a stairs spot whose exit is fogged", false, "none");
                    }
                    FgEnd(map, "G6", "fg_g7", "pz_tallfog_rev");
                    return true;
                }

                // ------------------------------------------------------------ G7 mining from an opened plateau
                case "fg_g7":
                {
                    IntVec3 home = FgHome(map);
                    fgMine = fgBehind = fgStand = IntVec3.Invalid;
                    int bestOre = -1;
                    foreach (IntVec3 g in Near(home, 100, map))
                    {
                        IntVec3 t = Levels.Cell(g, 1, map);
                        // (on G2's plateau, opened by its stairs in this save)
                        if (!t.IsValid || fgE2 != null && !fgE2.Contains(map.cellIndices.CellToIndex(t)) || !TmTop(t, map) || map.fogGrid.IsFogged(t) || !t.Standable(map))
                        {
                            continue;
                        }
                        foreach (IntVec3 d in GenAdj.CardinalDirections)
                        {
                            IntVec3 m = t + d, b = t + d * 2;
                            if (!m.InBounds(map) || !b.InBounds(map) || !(m.GetEdifice(map) is Mineable) || map.fogGrid.IsFogged(m) || !(b.GetEdifice(map) is Mineable) || !map.fogGrid.IsFogged(b)
                                || Levels.BandOfCell(b, map) != Levels.BandOfCell(t, map))
                            {
                                continue;
                            }
                            int ore = b.GetEdifice(map).def.building.isResourceRock ? 1 : 0;
                            if (ore > bestOre)
                            {
                                bestOre = ore;
                                fgMine = m;
                                fgBehind = b;
                                fgStand = t;
                            }
                        }
                        if (bestOre == 1)
                        {
                            break;
                        }
                    }
                    if (!fgMine.IsValid)
                    {
                        Result("G7 a cliff face next to an opened top with fogged rock behind it", false, "none");
                        stage = "fg_g8";
                        pendingLoad = "pz_tallfog";
                        return true;
                    }
                    fgActors.Clear();
                    fgActors.AddRange(Colonists(map).Where(p => !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining)).Take(2));
                    foreach (Pawn p in fgActors)
                    {
                        Teleport(p, fgStand);
                    }
                    FgStart(map, null);
                    // allowed: band cells of that level only
                    int band = Levels.BandOfCell(fgMine, map);
                    for (int i = 0; i < fgBefore.Length; i++)
                    {
                        if (Levels.BandOfCell(map.cellIndices.IndexToCell(i), map) == band)
                        {
                            fgAllowed.Add(i);
                        }
                    }
                    map.designationManager.AddDesignation(new Designation(fgMine, DesignationDefOf.Mine));
                    FgWork(map, fgActors, WorkTypeDefOf.Mining);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    Info($"G7 miners {string.Join(",", fgActors)} on {fgStand} mine the face {fgMine} ({fgMine.GetEdifice(map).def.defName}); behind it {fgBehind} ({fgBehind.GetEdifice(map).def.defName}, fogged)");
                    TickUntil(30000, () => fgMine.GetEdifice(map) == null, "fg_g7_done", 30);
                    return true;
                }
                case "fg_g7_done":
                {
                    bool mined = fgMine.GetEdifice(map) == null;
                    Result("G7 colonists on an opened plateau mined a cliff face: the rock behind it is revealed (vanilla), nothing on another level changed",
                        mined && !map.fogGrid.IsFogged(fgBehind), $"mined {mined}; behind {fgBehind} fogged {map.fogGrid.IsFogged(fgBehind)}");
                    FgEnd(map, "G7", "fg_g8");
                    return true;
                }

                // ------------------------------------------------------------ G8 a skip onto the rim
                case "fg_g8":
                {
                    AbilityDef skipDef = DefDatabase<AbilityDef>.GetNamedSilentFail("Skip");
                    if (skipDef == null || !ModsConfig.RoyaltyActive)
                    {
                        Info("G8 skip: no Royalty, skipped");
                        stage = "fg_g9";
                        return true;
                    }
                    IntVec3 home = FgHome(map);
                    fgPawn = Colonists(map).Where(p => !p.Downed).OrderByDescending(p => p.GetStatValue(StatDefOf.PsychicSensitivity)).FirstOrDefault();
                    for (int i = 0; i < 6 && fgPawn.GetPsylinkLevel() < 6; i++)
                    {
                        fgPawn.ChangePsylinkLevel(1, sendLetter: false);
                    }
                    fgPawn.abilities.GainAbility(skipDef);
                    Ability skip = fgPawn.abilities.GetAbility(skipDef);
                    fgPawn.psychicEntropy?.OffsetPsyfocusDirectly(1f);
                    fgU = fgStand = IntVec3.Invalid;
                    foreach (IntVec3 g in Near(home, 90, map))
                    {
                        if (!FgRim(g, map, 30, out IntVec3 u, out HashSet<int> e))
                        {
                            continue;
                        }
                        IntVec3 s = FgGroundNear(g, map, 3f, 9f, null);
                        if (s.IsValid)
                        {
                            fgU = u;
                            fgStand = s;
                            fgE = e;
                            break;
                        }
                    }
                    if (!fgU.IsValid)
                    {
                        Result("G8 a rim cell with ground near it for a skip", false, "none");
                        stage = "fg_g9";
                        return true;
                    }
                    FgStart(map, fgE);
                    Teleport(fgPawn, fgStand);
                    Draft(fgPawn, true);
                    CompAbilityEffect_WithDest dest = skip.CompOfType<CompAbilityEffect_WithDest>();
                    bool valid = skip.CanCast.Accepted && (dest == null || dest.CanHitTarget(fgU));
                    skip.QueueCastingJob(fgPawn, fgU);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    Info($"G8 {fgPawn} at {fgStand} skips itself to the rim {fgU} (can cast {skip.CanCast.Accepted} '{skip.CanCast.Reason}', sensitivity {fgPawn.GetStatValue(StatDefOf.PsychicSensitivity):0.00}, psylink {fgPawn.GetPsylinkLevel()}, destination ok {valid}; job {fgPawn.CurJobDef?.defName}); plateau {fgE.Count} cells, {FgFoggedIn(map, fgE)} fogged");
                    TickUntil(1500, () => ProjectZ.FloorOf(fgPawn.Position, map) == 1, "fg_g8_done", 5);
                    return true;
                }
                case "fg_g8_done":
                {
                    int left = FgFoggedIn(map, fgE);
                    Result("G8 a colonist skipped onto the rim: the plateau opened", ProjectZ.FloorOf(fgPawn.Position, map) == 1 && left == 0,
                        $"{fgPawn} at {fgPawn.Position} level {ProjectZ.FloorOf(fgPawn.Position, map)}; plateau cells still fogged {left} of {fgE.Count}");
                    FgEnd(map, "G8", "fg_g9");
                    return true;
                }

                // ------------------------------------------------------------ G9 cost
                case "fg_g9":
                {
                    // (ReachFog has no per-step check: it runs on changes. Its cost: the slowest run this mode saw, a full
                    // run as after a load, and a walk-only run with every region known clear)
                    double slowest = ReachFog.MaxMs;
                    ReachFogMap.For(map)?.Forget();
                    ReachFog.Run(map);
                    double full = ReachFog.LastMs;
                    double walk = 0;
                    for (int i = 0; i < 10; i++)
                    {
                        ReachFog.Run(map);
                        walk += ReachFog.LastMs;
                    }
                    walk /= 10;
                    Info($"PERF reach fog: slowest run this mode {slowest:0.00} ms, full run {full:0.00} ms ({ReachFog.LastRegions} regions reached), walk only {walk:0.00} ms; runs {ReachFog.Runs}");
                    Result("G9 a reach fog run with every region known clear under 5 ms, a full run under 100 ms", walk < 5.0 && full < 100.0, $"walk {walk:0.00} ms, full {full:0.00} ms, slowest this mode {slowest:0.00} ms");
                    Finish();
                    return true;
                }
            }
            return false;
        }

        private static void FgG3Next(Map map)
        {
            fgVariant++;
            tickWatch = null;
            if (fgVariant < 2)
            {
                stage = "fg_g3";
            }
            else
            {
                fgVariant = 0;
                stage = "fg_g4";
            }
            pendingLoad = "pz_tallfog";
        }

        // G2b: once the bridge is built its builders are drafted at once (an idle wander onto the other rim would open it
        // before the check). Always true.
        private static bool FgDraftAll()
        {
            foreach (Pawn p in fgActors)
            {
                if (p.Spawned && !p.Drafted)
                {
                    Draft(p, true);
                }
            }
            return true;
        }
    }
}
