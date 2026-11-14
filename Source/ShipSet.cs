using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ProjectZ.Combat;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // Multi-level gravships (isorim-pz-multilevel-gravship.md, v2.1 A/B/F, v2.4; the 3D hull: isorim-pz-hull3d-design.md
    // A): the 3D ship set.
    //   - Which cells are the ship (GravshipUtility.GetConnectedSubstructure, replaced on maps with floors): everything
    //     linked to the engine over gravship floor: 4 ways on a floor (vanilla's rule) and straight up or down through
    //     open cells (ShipLink). The engine is the only root. So decks may overhang the ground ship, span a hollow hull or
    //     bridge two ground hulls; a part cut off from the engine is not in the set.
    //   - The order (it is the order the limit, SubstructureSupport, cuts): the floor part the engine stands over first
    //     (the lowest gravship floor straight under it: the ground for a stacked ship), by the game's own flood fill; then
    //     always the lowest floor that has cells linked to the set from above or below, flooded from those cells in the
    //     order their columns came into the set. So the set is always linked to the engine, lower floors come first
    //     wherever they can be reached and the top floors drop out first. A ship of stacked decks is filled ground first,
    //     then floor by floor in the ground's order, exactly as before the 3D hull.
    //   - The engine is always aboard, also on a deck and over the limit: its own block (the cells under it, and those of
    //     its columns between the first floor part and its floor) comes right after the first floor part, which keeps
    //     room for it. A block cell comes with the set straight under it, or, with no gravship floor under it at all (a
    //     hollow), with a block cell beside it; a cell under the engine itself also comes with a cell beside it whatever is
    //     under it (a ground cell cut off from the hull). These rules are repeated until nothing changes, so every cell
    //     under the engine is aboard. One exception, as before the 3D hull and as vanilla with fewer cells than its own
    //     fill needs to cover the engine: a limit so small that the first fill ends inside the engine's own columns
    //     (starved); then only what stands straight over cells aboard comes.
    //   - When the limit cuts the first floor part, the room kept goes to the block and to what stands straight over the
    //     cells aboard, nothing spreads sideways or downward any more (as before the 3D hull).
    //   - The engine and extender footprint is a true 3D sphere: ground distance plus the height between the floors
    //     (Range3D.Storey per floor), no height bonus. On one floor it is vanilla's circle.
    //   - The 4 vanilla sites that read the ship set as a flat area get the ship's shadow, the ground cell under every
    //     ship cell (Ground): the capture bounds (GravshipCapturer.BeginGravshipRender), Gravship.engineToCenter and the two
    //     indoor-mask bakes of the take-off and landing cutscenes (WorldComponent_GravshipController).
    //   - A thruster's exhaust zone stays on the thruster's floor (map-cell callers only: IsBlocked and the placing ghost).
    //   - Upper floors fly with the ship (ShipLocal.cs, ShipRoofs.cs, ShipLanding.cs).
    // Maps without floors, and ships without decks, behave exactly as vanilla.
    public static class ShipSet
    {
        // Test switch (-projectznoshipset): vanilla's flat flood fill and ground-projection footprint of P1.
        public static bool Off;

        // scratch of one Fill (main thread; cleared when it ends)
        private static readonly List<IntVec3> tmpWave = new List<IntVec3>();
        private static readonly List<IntVec3> tmpRoots = new List<IntVec3>();
        private static readonly List<List<IntVec3>> tmpSeeds = new List<List<IntVec3>>();
        private static readonly HashSet<IntVec3> tmpSeeded = new HashSet<IntVec3>();
        private static readonly Dictionary<int, int> tmpColumn = new Dictionary<int, int>();

        // GetConnectedSubstructure on a map with floors.
        public static void Fill(Building_GravEngine engine, HashSet<IntVec3> cells, int maxCells, bool requireInsideFootprint)
        {
            Map map = engine.Map;
            AnnexMapComponent an = ProjectZ.Annex(map);
            List<Thing> makers = map.listerThings.ThingsInGroup(ThingRequestGroup.SubstructureFootprint);
            TerrainGrid tg = map.terrainGrid;
            bool Ok(IntVec3 x)
            {
                if (!x.InBounds(map))
                {
                    return false;
                }
                TerrainDef f = tg.FoundationAt(x);
                // (the game's own test, the tag, as vanilla's GetConnectedSubstructure: not DeckFoundation, so a deck
                // without the tag, e.g. another faction's ship, never flies with the player's ship)
                return f != null && f.IsSubstructure && (!requireInsideFootprint || GravshipUtility.InsideFootprint(x, map, makers));
            }
            Predicate<IntVec3> ok = Ok;
            // (a cell of floor f; the ground by its rows, as the flood fill here always asked)
            bool OnFloor(IntVec3 x, int f) => f == 0 ? x.z < an.groundZ : ProjectZ.FloorOf(x, map) == f;

            int ke = ProjectZ.FloorOf(engine.Position, map);
            // the root: the engine's own cell, as vanilla (no gravship floor under the engine: no ship)
            if (ke < 0 || !Ok(engine.Position))
            {
                return;
            }
            // where the fill starts: the lowest gravship floor straight under the engine (a stacked ship: the ground)
            IntVec3 start = engine.Position;
            for (IntVec3 b = ShipLink.Down(start, map, ok); b.IsValid; b = ShipLink.Down(start, map, ok))
            {
                start = b;
            }
            int startFloor = ProjectZ.FloorOf(start, map);
            // the engine's own block on the floors above that, up to its floor (an engine on a deck)
            List<IntVec3> pinned = new List<IntVec3>();
            if (ke > startFloor)
            {
                foreach (IntVec3 c in engine.OccupiedRect())
                {
                    IntVec3 g = ProjectZ.GroundCellOf(c, map);
                    for (int k = startFloor + 1; k <= ke; k++)
                    {
                        IntVec3 u = ProjectZ.CellOnFloor(g, k, map);
                        if (u.IsValid && Ok(u))
                        {
                            pinned.Add(u);
                        }
                    }
                }
            }
            tmpSeeded.Clear();
            tmpColumn.Clear();
            while (tmpSeeds.Count <= an.MaxLevel)
            {
                tmpSeeds.Add(new List<IntVec3>());
            }
            for (int k = 0; k < tmpSeeds.Count; k++)
            {
                tmpSeeds[k].Clear();   // (also at the start: a fill that threw must not leave its seeds to the next)
            }
            // a cell came into the set: its column's place in the order, and the cells linked to it from above and below
            // wait as seeds for their floor's turn
            void Seed(IntVec3 s)
            {
                int k = s.IsValid ? ProjectZ.FloorOf(s, map) : -1;
                if (k >= 0 && k < tmpSeeds.Count && !cells.Contains(s) && tmpSeeded.Add(s))
                {
                    tmpSeeds[k].Add(s);
                }
            }
            // (the limit cut the first floor part: only what stands straight over the cells aboard still comes)
            bool cut = false;
            void Came(IntVec3 x)
            {
                int col = map.cellIndices.CellToIndex(ProjectZ.GroundCellOf(x, map));
                if (!tmpColumn.ContainsKey(col))
                {
                    tmpColumn.Add(col, tmpColumn.Count);
                }
                Seed(ShipLink.Up(x, map, ok));
                if (!cut)
                {
                    Seed(ShipLink.Down(x, map, ok));
                }
            }
            int ColumnOrder(IntVec3 x) => tmpColumn.TryGetValue(map.cellIndices.CellToIndex(ProjectZ.GroundCellOf(x, map)), out int o) ? o : int.MaxValue;

            // the first floor part: vanilla's own flood fill (same order, so a one-storey ship is cut exactly as vanilla
            // cuts it); room is kept for the engine's block
            int firstMax = maxCells == int.MaxValue ? int.MaxValue : Math.Max(1, maxCells - pinned.Count);
            tmpWave.Clear();
            map.floodFiller.FloodFill(start, x => OnFloor(x, startFloor) && Ok(x), x =>
            {
                cells.Add(x);
                tmpWave.Add(x);
                return false;
            }, firstMax);
            cut = tmpWave.Count >= firstMax;
            for (int i = 0; i < tmpWave.Count; i++)
            {
                Came(tmpWave[i]);
            }
            // The first fill ended inside the engine's own columns: a cell of them on the first floor, joined to the start
            // cell within them, is not aboard. So little room that the cells under the engine can't all come (a limit of a
            // few cells; the game's own fill needs more than that to cover an engine too).
            bool starved = false;
            if (cut && pinned.Count > 0)
            {
                HashSet<IntVec3> own = new HashSet<IntVec3>();
                foreach (IntVec3 c in engine.OccupiedRect())
                {
                    IntVec3 s = ProjectZ.CellOnFloor(ProjectZ.GroundCellOf(c, map), startFloor, map);
                    if (s.IsValid && Ok(s))
                    {
                        own.Add(s);
                    }
                }
                tmpRoots.Clear();
                tmpRoots.Add(start);
                own.Remove(start);
                for (int i = 0; i < tmpRoots.Count && !starved; i++)
                {
                    for (int d = 0; d < 4; d++)
                    {
                        IntVec3 n = tmpRoots[i] + GenAdj.CardinalDirections[d];
                        if (own.Remove(n))
                        {
                            starved |= !cells.Contains(n);
                            tmpRoots.Add(n);
                        }
                    }
                }
                tmpRoots.Clear();
            }
            // The engine's block, bottom-up, until nothing changes:
            //   - a cell with the set straight under it;
            //   - a cell with no gravship floor under it at all (a block partly over a hollow), next to a cell aboard;
            //   - a cell under the engine itself, next to a cell aboard, whatever is under it (not when starved).
            // The cells next to the block wait as seeds too: the block is no flood of its own.
            tmpWave.Clear();
            List<IntVec3> block = pinned.OrderBy(c => c.z).ToList();
            for (bool more = true; more && cells.Count < maxCells;)
            {
                more = false;
                foreach (IntVec3 u in block)
                {
                    if (cells.Count >= maxCells)
                    {
                        break;
                    }
                    if (cells.Contains(u))
                    {
                        continue;
                    }
                    IntVec3 b = ShipLink.Down(u, map, ok);
                    bool join = b.IsValid && cells.Contains(b);
                    if (!join && (!b.IsValid || !starved && ProjectZ.FloorOf(u, map) == ke))
                    {
                        for (int d = 0; d < 4 && !join; d++)
                        {
                            IntVec3 n = u + GenAdj.CardinalDirections[d];
                            join = cells.Contains(n) && ProjectZ.SameBand(n, u, map);
                        }
                    }
                    if (join)
                    {
                        cells.Add(u);
                        tmpWave.Add(u);
                        more = true;
                    }
                }
            }
            for (int i = 0; i < tmpWave.Count; i++)
            {
                Came(tmpWave[i]);
                for (int d = 0; d < 4 && !cut; d++)
                {
                    IntVec3 n = tmpWave[i] + GenAdj.CardinalDirections[d];
                    if (n.InBounds(map) && ProjectZ.SameBand(n, tmpWave[i], map) && Ok(n))
                    {
                        Seed(n);
                    }
                }
            }
            // then always the lowest floor with seeds: flooded from them in their columns' order
            while (cells.Count < maxCells)
            {
                int f = -1;
                for (int k = 0; k < tmpSeeds.Count && f < 0; k++)
                {
                    if (tmpSeeds[k].Count > 0)
                    {
                        f = k;
                    }
                }
                if (f < 0)
                {
                    break;
                }
                tmpRoots.Clear();
                foreach (IntVec3 s in tmpSeeds[f].Where(s => !cells.Contains(s)).OrderBy(ColumnOrder))   // (a stable sort)
                {
                    tmpRoots.Add(s);
                }
                tmpSeeds[f].Clear();
                if (tmpRoots.Count == 0)
                {
                    continue;
                }
                tmpWave.Clear();
                int floor = f;
                map.floodFiller.FloodFill(IntVec3.Invalid, x => !cut && OnFloor(x, floor) && Ok(x) && !cells.Contains(x), x =>
                {
                    cells.Add(x);
                    tmpWave.Add(x);
                    return false;
                }, maxCells == int.MaxValue ? int.MaxValue : maxCells - cells.Count, false, tmpRoots);
                for (int i = 0; i < tmpWave.Count; i++)
                {
                    Came(tmpWave[i]);
                }
            }
            for (int k = 0; k < tmpSeeds.Count; k++)
            {
                tmpSeeds[k].Clear();
            }
            tmpSeeded.Clear();
            tmpColumn.Clear();
            tmpWave.Clear();
            tmpRoots.Clear();
        }

        // Is loc inside the field of one of the makers: a true 3D sphere around each.
        public static bool InFootprint(IntVec3 loc, Map map, List<Thing> makers, Thing thingToExclude)
        {
            IntVec3 g = ProjectZ.GroundCellOf(loc, map);
            int k = ProjectZ.FloorOf(loc, map);
            if (!g.IsValid || k < 0)
            {
                return false;
            }
            foreach (Thing maker in makers)
            {
                if (!maker.Spawned || thingToExclude != null && maker == thingToExclude || !maker.TryGetComp(out CompSubstructureFootprint comp) || !comp.Valid)
                {
                    continue;
                }
                IntVec3 mg = ProjectZ.GroundCellOf(maker.Position, map);
                int km = ProjectZ.FloorOf(maker.Position, map);
                if (!mg.IsValid || km < 0)
                {
                    continue;
                }
                if (GameRange.InSphere(GameRange.Footprint.Flat(comp.Props), km, k, (g - mg).LengthHorizontalSquared))
                {
                    return true;
                }
            }
            return false;
        }

        // The ship read as a flat area: its shadow, the ground cell under every ship cell (the set itself when it has no
        // cell upstairs). A stacked ship's shadow is its ground cells; an overhang or a deck over a hollow adds the ground
        // under it, as the game's own Gravship.bounds does (built from the keys' x and z).
        public static HashSet<IntVec3> Ground(Building_GravEngine engine)
        {
            HashSet<IntVec3> all = engine.ValidSubstructure;
            Map map = engine.MapHeld;
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || !all.Any(c => c.z >= an.groundZ))
            {
                return all;
            }
            HashSet<IntVec3> flat = new HashSet<IntVec3>();
            foreach (IntVec3 c in all)
            {
                IntVec3 g = c.z < an.groundZ ? c : ProjectZ.GroundCellOf(c, map);
                if (g.IsValid)
                {
                    flat.Add(g);
                }
            }
            return flat;
        }

        // Cells of the ship on an upper floor (0 on maps without floors).
        public static int Upper(Building_GravEngine engine)
        {
            AnnexMapComponent an = ProjectZ.Annex(engine.MapHeld);
            return an == null || !engine.Spawned ? 0 : engine.ValidSubstructure.Count(c => c.z >= an.groundZ);
        }
    }

    [HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.GetConnectedSubstructure))]
    public static class GetConnectedSubstructure_ShipSet_Patch
    {
        // Low priority: another mod's prefix that adjusts the arguments (raises maxCells, say) runs first and the ship set
        // takes its values; at the default priority a mod registered after ProjectZ would be skipped once this returns false.
        [HarmonyPriority(Priority.Low)]
        public static bool Prefix(Building_GravEngine engine, HashSet<IntVec3> cells, int maxCells, bool requireInsideFootprint)
        {
            if (ShipSet.Off || !ModsConfig.OdysseyActive || engine == null || !engine.Spawned || ProjectZ.Annex(engine.Map) == null)
            {
                return true;
            }
            cells.Clear();
            ShipSet.Fill(engine, cells, maxCells, requireInsideFootprint);
            return false;
        }
    }

    // The engine and extender footprint: a 3D sphere on maps with floors (P1 had the ground projection here).
    [HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.InsideFootprint))]
    public static class InsideFootprint_ShipSet_Patch
    {
        public static bool Prefix(IntVec3 loc, Map map, List<Thing> footprintMakers, Thing thingToExclude, ref bool __result)
        {
            if (!ModsConfig.OdysseyActive || ProjectZ.Annex(map) == null)
            {
                return true;
            }
            if (footprintMakers == null)
            {
                footprintMakers = map.listerThings.ThingsInGroup(ThingRequestGroup.SubstructureFootprint);
            }
            if (ShipSet.Off)
            {
                // (P1: the ground projection)
                __result = false;
                IntVec3 g = ProjectZ.GroundCellOf(loc, map);
                foreach (Thing maker in footprintMakers)
                {
                    if (g.IsValid && maker.Spawned && maker.TryGetComp(out CompSubstructureFootprint comp) && comp.Valid && maker != thingToExclude)
                    {
                        IntVec3 mg = ProjectZ.GroundCellOf(maker.Position, map);
                        if (mg.IsValid && g.InHorDistOf(mg, GameRange.Footprint.Flat(comp.Props)))
                        {
                            __result = true;
                            break;
                        }
                    }
                }
                return false;
            }
            __result = ShipSet.InFootprint(loc, map, footprintMakers, thingToExclude);
            return false;
        }
    }

    // ---- the ship set read as a flat area: its shadow (ShipSet.Ground)

    [HarmonyPatch]
    public static class ShipSetGround_Patch
    {
        private static readonly MethodInfo getValid = AccessTools.PropertyGetter(typeof(Building_GravEngine), nameof(Building_GravEngine.ValidSubstructure));
        private static readonly MethodInfo fromCells = AccessTools.Method(typeof(CellRect), nameof(CellRect.FromCellList));
        private static readonly MethodInfo bake = AccessTools.Method(typeof(SectionLayer_IndoorMask), nameof(SectionLayer_IndoorMask.BakeGravshipIndoorMesh));
        private static readonly MethodInfo ground = AccessTools.Method(typeof(ShipSet), nameof(ShipSet.Ground));

        // swapped calls per method (the test checks them)
        public static readonly Dictionary<string, int> PerMethod = new Dictionary<string, int>();
        private static readonly HashSet<MethodBase> bakers = new HashSet<MethodBase>();

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GravshipCapturer), nameof(GravshipCapturer.BeginGravshipRender));
            yield return AccessTools.Constructor(typeof(Gravship), new[] { typeof(Building_GravEngine) });
            // the cutscene lambdas that bake the indoor mask from the ship set
            Type ctl = typeof(WorldComponent_GravshipController);
            foreach (MethodInfo m in new[] { ctl }.Concat(ctl.GetNestedTypes(AccessTools.all)).SelectMany(t => t.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly)))
            {
                if (Calls(m, bake) && Calls(m, getValid))
                {
                    bakers.Add(m);
                    yield return m;
                }
            }
        }

        private static bool Calls(MethodInfo m, MethodInfo callee)
        {
            try
            {
                return m.GetMethodBody() != null && PatchProcessor.ReadMethodBody(m).Any(p => (p.Key == OpCodes.Call || p.Key == OpCodes.Callvirt) && Equals(p.Value, callee));
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            return FailSoft.Transpile(nameof(ShipSetGround_Patch), original, instructions, code => Edit(code, original));
        }

        private static string Edit(List<CodeInstruction> code, MethodBase original)
        {
            bool all = bakers.Contains(original);
            int n = 0;
            for (int i = 0; i < code.Count; i++)
            {
                // the bakes: every read; elsewhere only a read that goes straight into CellRect.FromCellList
                if (code[i].Calls(getValid) && (all || i + 1 < code.Count && code[i + 1].Calls(fromCells)))
                {
                    code[i].opcode = OpCodes.Call;
                    code[i].operand = ground;
                    n++;
                }
            }
            string key = (original.DeclaringType?.Name ?? "?") + "." + original.Name;
            PerMethod[key] = n;
            ProjectZ.DebugLog($"multi-level gravships: {key}: {n} ship-set read(s) on the ground");
            return n > 0 ? null : "no ship-set read found";
        }
    }

    // ---- thruster exhaust on the thruster's floor

    public static class ThrusterZone
    {
        [ThreadStatic] public static Map clipMap;   // set while a map-cell caller asks for the zone

        public static void Clip(IntVec3 pos, List<IntVec3> cells)
        {
            Map map = clipMap;
            if (map == null || cells == null || ProjectZ.Annex(map) == null)
            {
                return;
            }
            int k = ProjectZ.FloorOf(pos, map);
            cells.RemoveAll(c => c.InBounds(map) && ProjectZ.FloorOf(c, map) != k);
        }
    }

    [HarmonyPatch(typeof(CompProperties_GravshipThruster), nameof(CompProperties_GravshipThruster.GetExclusionZone))]
    public static class GetExclusionZone_ShipSet_Patch
    {
        public static void Postfix(IntVec3 pos, ref List<IntVec3> exclusionCells)
        {
            ThrusterZone.Clip(pos, exclusionCells);
        }
    }

    [HarmonyPatch(typeof(CompGravshipThruster), nameof(CompGravshipThruster.IsBlocked))]
    public static class ThrusterIsBlocked_ShipSet_Patch
    {
        public static void Prefix(Map map, out Map __state)
        {
            __state = ThrusterZone.clipMap;
            ThrusterZone.clipMap = map;
        }

        public static void Finalizer(Map __state)
        {
            ThrusterZone.clipMap = __state;
        }
    }

    [HarmonyPatch(typeof(PlaceWorker_GravshipThruster), nameof(PlaceWorker_GravshipThruster.DrawGhost))]
    public static class ThrusterGhost_ShipSet_Patch
    {
        public static void Prefix(out Map __state)
        {
            __state = ThrusterZone.clipMap;
            ThrusterZone.clipMap = Find.CurrentMap;
        }

        public static void Finalizer(Map __state)
        {
            ThrusterZone.clipMap = __state;
        }
    }
}
