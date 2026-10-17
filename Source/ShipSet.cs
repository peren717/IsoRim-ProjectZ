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
    // Multi-level gravships, phase P2 (isorim-pz-multilevel-gravship.md, v2.1 A/B/F, v2.4): the 3D ship set.
    //   - Which cells are the ship (GravshipUtility.GetConnectedSubstructure, replaced on maps with floors): the engine's
    //     ground column first, then vanilla's own ground flood fill from there, then floor by floor upwards. A cell on a
    //     floor counts only when the cell right below it counts, so the ship is the ground ship plus its stacked deck
    //     columns, and no accepted cell ever floats. The limit (SubstructureSupport) cuts the order: over it, the top floors
    //     drop out first. An engine standing on a deck is always aboard (its own columns up to its floor come right after
    //     the ground cell it starts from; the ground flood keeps room for them).
    //   - The engine and extender footprint is a true 3D sphere: ground distance plus the height between the floors
    //     (Range3D.Storey per floor), no height bonus. On one floor it is vanilla's circle.
    //   - The 4 vanilla sites that read the ship set as a flat area get its ground cells only: the capture bounds
    //     (GravshipCapturer.BeginGravshipRender), Gravship.engineToCenter and the two indoor-mask bakes of the take-off and
    //     landing cutscenes (WorldComponent_GravshipController).
    //   - A thruster's exhaust zone stays on the thruster's floor (map-cell callers only: IsBlocked and the placing ghost).
    //   - Upper floors fly with the ship since P4 (ShipLocal.cs, ShipLanding.cs).
    // Maps without floors, and ships without decks, behave exactly as vanilla.
    public static class ShipSet
    {
        // Test switch (-projectznoshipset): vanilla's flat flood fill and ground-projection footprint of P1.
        public static bool Off;

        private static readonly List<IntVec3> tmpPrev = new List<IntVec3>();
        private static readonly List<IntVec3> tmpCur = new List<IntVec3>();
        private static readonly List<IntVec3> tmpGround = new List<IntVec3>();

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
                return f != null && f.IsSubstructure && (!requireInsideFootprint || GravshipUtility.InsideFootprint(x, map, makers));
            }

            IntVec3 eg = ProjectZ.GroundCellOf(engine.Position, map);
            int ke = ProjectZ.FloorOf(engine.Position, map);
            if (!eg.IsValid || ke < 0)
            {
                return;
            }
            // the engine's own columns above the ground, up to its floor (an engine on a deck)
            List<IntVec3> pinned = new List<IntVec3>();
            if (ke >= 1)
            {
                foreach (IntVec3 c in engine.OccupiedRect())
                {
                    IntVec3 g = ProjectZ.GroundCellOf(c, map);
                    for (int k = 1; k <= ke; k++)
                    {
                        IntVec3 u = ProjectZ.CellOnFloor(g, k, map);
                        if (u.IsValid && Ok(u))
                        {
                            pinned.Add(u);
                        }
                    }
                }
            }
            int groundMax = maxCells == int.MaxValue ? int.MaxValue : Math.Max(1, maxCells - pinned.Count);
            // the ground: vanilla's own flood fill (same order, so a one-storey ship is cut exactly as vanilla cuts it)
            tmpGround.Clear();
            map.floodFiller.FloodFill(eg, x => x.z < an.groundZ && Ok(x), x =>
            {
                cells.Add(x);
                tmpGround.Add(x);
                return false;
            }, groundMax);
            // the engine's columns, bottom-up: only on a cell that counts below
            foreach (IntVec3 u in pinned.OrderBy(c => c.z))
            {
                if (cells.Count >= maxCells)
                {
                    break;
                }
                if (cells.Contains(FloorRoof.Below(u, map)))
                {
                    cells.Add(u);
                }
            }
            // floor by floor, in the order of the floor below
            tmpPrev.Clear();
            tmpPrev.AddRange(tmpGround);
            for (int k = 1; k < an.floors && tmpPrev.Count > 0 && cells.Count < maxCells; k++)
            {
                tmpCur.Clear();
                for (int i = 0; i < tmpPrev.Count; i++)
                {
                    IntVec3 u = ProjectZ.CellOnFloor(ProjectZ.GroundCellOf(tmpPrev[i], map), k, map);
                    if (!u.IsValid)
                    {
                        continue;
                    }
                    if (cells.Contains(u))
                    {
                        tmpCur.Add(u);   // (an engine column cell, already in)
                        continue;
                    }
                    if (cells.Count >= maxCells)
                    {
                        break;
                    }
                    if (Ok(u))
                    {
                        cells.Add(u);
                        tmpCur.Add(u);
                    }
                }
                tmpPrev.Clear();
                tmpPrev.AddRange(tmpCur);
            }
            tmpPrev.Clear();
            tmpCur.Clear();
            tmpGround.Clear();
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
                float r = comp.Props.radius;
                float dy = (k - km) * Range3D.Storey;
                if ((g - mg).LengthHorizontalSquared + dy * dy <= r * r)
                {
                    return true;
                }
            }
            return false;
        }

        // The ship's cells on the ground (the set itself when it has none upstairs).
        public static HashSet<IntVec3> Ground(Building_GravEngine engine)
        {
            HashSet<IntVec3> all = engine.ValidSubstructure;
            Map map = engine.MapHeld;
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || !all.Any(c => c.z >= an.groundZ))
            {
                return all;
            }
            return new HashSet<IntVec3>(all.Where(c => c.z < an.groundZ));
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
                        if (mg.IsValid && g.InHorDistOf(mg, comp.Props.radius))
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

    // ---- the ship set read as a flat area: its ground cells only

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
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
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
            if (n == 0)
            {
                Log.Warning("[ProjectZ] multi-level gravships: no ship-set read swapped in " + key);
            }
            ProjectZ.DebugLog($"multi-level gravships: {key}: {n} ship-set read(s) on the ground");
            return code;
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
