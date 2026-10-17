using System;
using System.Collections.Generic;
using HarmonyLib;
using ProjectZ.Combat;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // Multi-level gravships, phase P3 (isorim-pz-multilevel-gravship.md, decision 10, v2 B, v2.5 answer 6): the footprint
    // range preview on maps with floors. The footprint is a true 3D sphere (ShipSet.InFootprint); vanilla draws it as flat
    // circles round each maker's raw cell, which is right on the maker's own floor only and runs on into the rows of the
    // next band. Every vanilla footprint preview goes through SubstructureGrid.DrawSubstructureFootprint (the gravship's
    // substructure overlay while an engine or extender is selected or substructure / a ship deck is being placed, and the
    // engine or extender placing ghost through DrawSubstructureFootprintWithExtra), so both are replaced here on maps with
    // floors by one drawing:
    //   - on the ground and on the viewed floor, each maker's sphere cut at that floor (the cells the footprint rule
    //     accepts there), engine and linked extenders merged per engine as vanilla does, cut at the band's edge;
    //   - in IsoRim's 3D view, the faint sphere shell (RangeShell, sphere mode) for the engine or extender being placed
    //     or selected only.
    // The placing ghost's link lines to the engine (PlaceWorker_InRangeOfGravEngine) are already 3D (FacilityPatches).
    // Maps without floors draw vanilla's own preview.
    public static class ShipRange
    {
        private static int drawnFrame = -1;
        private static readonly List<IntVec3> engineCells = new List<IntVec3>();
        private static readonly HashSet<IntVec3> engineSet = new HashSet<IntVec3>();
        private static readonly List<IntVec3> otherCells = new List<IntVec3>();
        private static readonly HashSet<IntVec3> otherSet = new HashSet<IntVec3>();
        private static readonly HashSet<IntVec3> cut = new HashSet<IntVec3>();
        private static readonly HashSet<IntVec3> ignore = new HashSet<IntVec3>();
        private static readonly List<int> floors = new List<int>(2);
        private static readonly List<(IntVec3 g, int k, float r)> shells = new List<(IntVec3, int, float)>();

        // Test read-out of the last drawing: cells drawn per floor (engine circles + extender parts), shells drawn.
        public static readonly Dictionary<int, int> LastCells = new Dictionary<int, int>();
        public static int LastShells;
        public static int LastFrame = -1;

        // The cells of the sphere (ground column mg, floor km, radius r) cut at floor k, as raw cells of that floor: the
        // ground cells the footprint rule accepts there (ShipSet.InFootprint), lifted to floor k.
        public static void Section(IntVec3 mg, int km, float r, int k, Map map, AnnexMapComponent an, List<IntVec3> into)
        {
            float dy = (k - km) * Range3D.Storey;
            float s = r * r - dy * dy;
            if (s < 0f)
            {
                return;
            }
            int n = Mathf.CeilToInt(Mathf.Sqrt(s));
            for (int x = mg.x - n; x <= mg.x + n; x++)
            {
                if (x < 0 || x >= map.Size.x)
                {
                    continue;
                }
                for (int z = mg.z - n; z <= mg.z + n; z++)
                {
                    if (z < 0 || z >= an.groundZ)
                    {
                        continue;
                    }
                    int ddx = x - mg.x, ddz = z - mg.z;
                    if (ddx * ddx + ddz * ddz + dy * dy <= r * r)
                    {
                        into.Add(new IntVec3(x, 0, z + k * an.stride));
                    }
                }
            }
        }

        private static bool Maker(Thing t, Map map, out IntVec3 g, out int k)
        {
            g = ProjectZ.GroundCellOf(t.Position, map);
            k = ProjectZ.FloorOf(t.Position, map);
            return g.IsValid && k >= 0;
        }

        // From the two vanilla entry points (patches below). extraAt / extraRadius: the engine or extender being placed
        // (Invalid when none); exclude: the thing it stands for (reinstalling), left out as vanilla does.
        public static void Draw(Map map, AnnexMapComponent an, IntVec3 extraAt, float extraRadius, Thing exclude)
        {
            if (drawnFrame == Time.frameCount)
            {
                return;   // once a frame, the first call wins (vanilla's own rule)
            }
            drawnFrame = Time.frameCount;
            LastFrame = Time.frameCount;
            LastCells.Clear();
            LastShells = 0;
            floors.Clear();
            floors.Add(0);
            int view = FloorView.Current;
            if (view > 0)
            {
                floors.Add(view);
            }
            bool hasExtra = extraAt.IsValid;
            IntVec3 eg = IntVec3.Invalid;
            int ek = -1;
            if (hasExtra)
            {
                eg = ProjectZ.GroundCellOf(extraAt, map);
                ek = ProjectZ.FloorOf(extraAt, map);
                hasExtra = eg.IsValid && ek >= 0;
            }
            Color color = GravshipUtility.ConnectedSubstructureColor;
            foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.GravEngine))
            {
                if (!(t is Building_GravEngine engine))
                {
                    continue;
                }
                foreach (int k in floors)
                {
                    engineCells.Clear();
                    engineSet.Clear();
                    otherCells.Clear();
                    otherSet.Clear();
                    if (engine != exclude && Maker(engine, map, out IntVec3 g0, out int k0) && engine.TryGetComp(out CompSubstructureFootprint ec))
                    {
                        Section(g0, k0, ec.Props.radius, k, map, an, engineCells);
                        engineSet.AddRange(engineCells);
                    }
                    foreach (Thing f in engine.AffectedByFacilities.LinkedFacilitiesListForReading)
                    {
                        if (exclude != null && f == exclude || !f.TryGetComp(out CompSubstructureFootprint comp) || !comp.Valid || !Maker(f, map, out IntVec3 fg, out int fk))
                        {
                            continue;
                        }
                        AddOther(fg, fk, comp.Props.radius, k, map, an);
                    }
                    if (hasExtra)
                    {
                        AddOther(eg, ek, extraRadius, k, map, an);
                    }
                    Cut(k, map, an);
                    GenDraw.DrawFieldEdges(engineCells, color, null, cut);
                    ignore.Clear();
                    ignore.UnionWith(cut);
                    ignore.UnionWith(engineSet);
                    GenDraw.DrawFieldEdges(otherCells, color, null, ignore);
                    LastCells.TryGetValue(k, out int had);
                    LastCells[k] = had + engineCells.Count + otherCells.Count;
                }
            }
            // the shell: the maker being placed or selected (3D view only)
            if (!IsoRimBridge.Present || !IsoRimBridge.View3DOn)
            {
                return;
            }
            shells.Clear();
            if (hasExtra)
            {
                shells.Add((eg, ek, extraRadius));
            }
            foreach (object o in Find.Selector.SelectedObjects)
            {
                if (o is Thing st && st.Spawned && st.Map == map && st != exclude && st.TryGetComp(out CompSubstructureFootprint sc) && sc.Valid && sc.DisplaySubstructureOverlay
                    && Maker(st, map, out IntVec3 sg, out int sk) && !shells.Contains((sg, sk, sc.Props.radius)))
                {
                    shells.Add((sg, sk, sc.Props.radius));
                }
            }
            foreach ((IntVec3 g, int k, float r) in shells)
            {
                RangeShell.Draw(new Vector3(g.x + 0.5f, k * Range3D.Storey, g.z + 0.5f), r, 0f, sphere: true);
                LastShells++;
            }
        }

        // An extender's (or the placed maker's) part on floor k: its cells there not already the engine's, not fogged.
        private static void AddOther(IntVec3 g, int km, float r, int k, Map map, AnnexMapComponent an)
        {
            int from = otherCells.Count;
            Section(g, km, r, k, map, an, otherCells);
            int w = from;
            for (int i = from; i < otherCells.Count; i++)
            {
                IntVec3 c = otherCells[i];
                if (!c.Fogged(map) && !engineSet.Contains(c) && otherSet.Add(c))
                {
                    otherCells[w++] = c;
                }
            }
            otherCells.RemoveRange(w, otherCells.Count - w);
        }

        // The rows just outside floor k's band: no edge is drawn along them (the band's own edges are the map's edges).
        private static void Cut(int k, Map map, AnnexMapComponent an)
        {
            cut.Clear();
            int lo = k * an.stride - 1, hi = k * an.stride + an.groundZ;
            for (int x = 0; x < map.Size.x; x++)
            {
                if (lo >= 0)
                {
                    cut.Add(new IntVec3(x, 0, lo));
                }
                cut.Add(new IntVec3(x, 0, hi));
            }
        }
    }

    // The substructure overlay's footprint (selected engine or extender, placing substructure or a ship deck, and the
    // ghost below) on maps with floors.
    [HarmonyPatch(typeof(SubstructureGrid), nameof(SubstructureGrid.DrawSubstructureFootprint))]
    public static class DrawSubstructureFootprint_ShipRange_Patch
    {
        public static bool Prefix(List<IntVec3> extraCells, Thing thingToExclude)
        {
            Map map = Find.CurrentMap;
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null)
            {
                return true;
            }
            try
            {
                // extraCells only come from DrawSubstructureFootprintWithExtra (patched below); another caller's list
                // has no sphere to cut, so its cells are left to vanilla on maps with floors.
                if (extraCells != null && extraCells.Count > 0)
                {
                    return true;
                }
                ShipRange.Draw(map, an, IntVec3.Invalid, 0f, thingToExclude);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Drawing the gravship footprint failed: " + e, 61190331);
            }
            return false;
        }
    }

    // The engine or extender placing ghost (CompProperties_SubstructureFootprint.DrawGhost): its own sphere joins the drawing.
    [HarmonyPatch(typeof(SubstructureGrid), nameof(SubstructureGrid.DrawSubstructureFootprintWithExtra))]
    public static class DrawSubstructureFootprintWithExtra_ShipRange_Patch
    {
        public static bool Prefix(CompProperties_SubstructureFootprint footprint, IntVec3 center, Thing thing)
        {
            Map map = Find.CurrentMap;
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || footprint == null)
            {
                return true;
            }
            try
            {
                ShipRange.Draw(map, an, center, footprint.radius, thing);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Drawing the gravship footprint failed: " + e, 61190332);
            }
            return false;
        }
    }

    // A facility's link range while it is placed or selected (PlaceWorker_ShowFacilitiesRange: tool cabinets, vitals
    // monitors, ...): links measure the 3D distance (FacilityPatches), so in the 3D view the ground and the viewed floor
    // also get the ring cut from that sphere, as the weapon range preview does. Its own floor keeps the game's ring.
    [HarmonyPatch(typeof(PlaceWorker_ShowFacilitiesRange), nameof(PlaceWorker_ShowFacilitiesRange.DrawGhost))]
    public static class ShowFacilitiesRange_Floors_Patch
    {
        public static int LastRings;   // test read-out

        public static void Postfix(ThingDef def, IntVec3 center)
        {
            LastRings = 0;
            Map map = Find.CurrentMap;
            CompProperties_Facility props = def?.GetCompProperties<CompProperties_Facility>();
            if (props == null || ProjectZ.Annex(map) == null || !IsoRimBridge.Present || !IsoRimBridge.View3DOn || Patches.Facilities3D.Off)
            {
                return;
            }
            int kc = ProjectZ.FloorOf(center, map);
            IntVec3 g = ProjectZ.GroundCellOf(center, map);
            if (kc < 0 || !g.IsValid)
            {
                return;
            }
            float r = props.maxDistance - 0.1f;
            if (r > GenRadial.MaxRadialPatternRadius)
            {
                return;   // the game draws no ring either
            }
            int view = FloorView.Current;
            foreach (int k in view > 0 ? new[] { 0, view } : new[] { 0 })
            {
                if (k == kc)
                {
                    continue;
                }
                float dy = (kc - k) * Range3D.Storey;
                float s = r * r - dy * dy;
                IntVec3 c = ProjectZ.CellOnFloor(g, k, map);
                if (s < 1f || !c.IsValid)
                {
                    continue;
                }
                GenDraw.DrawRadiusRing(c, Mathf.Sqrt(s));
                LastRings++;
            }
        }
    }
}
