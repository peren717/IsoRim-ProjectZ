using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // Wires between two floors (bug M228, user decisions 2026-11-07). A power hookup to the next floor (PowerHookup) pairs
    // a connector with a transmitter in another band of the map. The game draws such a wire flat between the two raw
    // positions, about a band (153 rows on a 150 map) long; IsoRim then moves the whole piece by ONE floor's offset (a
    // section mesh by its section's floor, a single mesh by its matrix's row), so one end shows ~150 cells away.
    //
    // One rule: a wire whose two ends are on different floors is not drawn by the game. ProjectZ draws it in the 3D view
    // as one straight line through the slab, from the connector to its transmitter, each end where its floor stands;
    // an end on a hidden floor (above the floor viewed) is cut where the line meets that floor's slab. Wires on one floor
    // stay the game's own. Inventory of the game's drawing of a thing-to-thing line with one flat piece (Assembly-CSharp
    // 1.7; lines drawn with GenDraw.DrawLineBetween and MoteDualAttached are already mapped per end by IsoRim):
    //   1. PowerNetGraphics.PrintWirePieceConnecting, printed into a section mesh, two callers:
    //      CompPower.PostPrintOnto (the grey wire, things layer) and CompPower.CompPrintForPowerGrid (the power overlay's
    //      line, SectionLayer_ThingsPowerGrid). Prefix: a cross-floor pair prints nothing; Draw prints it here every frame
    //      (the same filters: no grey wire to a hidden conduit; the overlay only when shown, the player's or no faction's,
    //      a wall attachment from its wall).
    //   2. PowerNetGraphics.RenderAnticipatedWirePieceConnecting (BuildDesignatorUtility: the build ghost's wire). Prefix.
    //   3. CompDeathrestBindable.PostDraw (the hose from a deathrest bed to a bound building). Its Graphics.DrawMesh call is
    //      swapped for Hose (transpiler), which keeps the game's draw unless the two are on different floors.
    // Multi-floor maps are locked to the 3D view; with it off (should not happen) a cross-floor wire is simply not drawn.
    // Fail-soft: each patch class on its own (FailSoft); 1 and its frame draw are one group (no print skipped without the
    // draw); any exception in a draw is logged once and the game's draw runs instead.
    public static class CrossFloorWires
    {
        private const float Width = 1f;   // the game's wire planes are 1 cell wide
        private const float SlabThickness = 0.12f;   // IsoRim's slab (Floors.SlabThickness): a cut end stops under it

        // the game's grey wire material (read when drawn: no Material field here, assets load on the main thread)
        private static readonly FieldInfo WireMatField = AccessTools.Field(typeof(PowerNetGraphics), "WireMat");

        private static Material WireMat => WireMatField?.GetValue(null) as Material;

        // Tests: what was drawn in the last frame that drew (true-space ends as drawn, after any cut).
        public struct Segment
        {
            public string kind;          // "wire", "overlay", "ghost", "hose"
            public Vector3 a, b;         // drawn space
            public int floorA, floorB;   // the two things' floors
            public bool cut;             // b was cut at a hidden floor's slab
        }

        public static readonly List<Segment> LastFrame = new List<Segment>();
        public static int lastFrame = -1;
        private static int collecting = -1;

        public static bool OnFloors(Map map) => map != null && ProjectZ.Annex(map) != null;

        // Two things on different floors (neither in a void row).
        public static bool Across(Thing a, Thing b)
        {
            if (a == null || b == null || !a.Spawned || !b.Spawned || a.Map != b.Map || !OnFloors(a.Map))
            {
                return false;
            }
            return Across(a.Position, b.Position, a.Map);
        }

        public static bool Across(IntVec3 a, IntVec3 b, Map map)
        {
            int ka = ProjectZ.FloorOf(a, map), kb = ProjectZ.FloorOf(b, map);
            return ka >= 0 && kb >= 0 && ka != kb;
        }

        // A world point (raw rows) in drawn space at altitude alt over its floor; false in a void row.
        private static bool ToDrawn(Vector3 w, IntVec3 cell, Map map, float alt, out Vector3 d, out int floor)
        {
            d = Vector3.zero;
            floor = ProjectZ.FloorOf(cell, map);
            IntVec3 g = ProjectZ.GroundCellOf(cell, map);
            if (floor < 0 || !g.IsValid)
            {
                return false;
            }
            d = new Vector3(w.x, floor * IsoRimBridge.StoreyHeight / IsoRimBridge.Squash + alt, w.z - (cell.z - g.z));
            return true;
        }

        // THE draw: a flat wire quad of the given material from world point a (thing cell ca) to world point b (cell cb),
        // both drawn on their floors, turned round its length to face the camera. An end above the floor viewed is cut at
        // that floor's slab; both above: nothing.
        public static void DrawLine(Vector3 a, IntVec3 ca, Vector3 b, IntVec3 cb, Map map, Material mat, float alt, string kind)
        {
            if (mat == null || !IsoRimBridge.View3DOn || !ToDrawn(a, ca, map, alt, out Vector3 da, out int fa) || !ToDrawn(b, cb, map, alt, out Vector3 db, out int fb))
            {
                return;
            }
            int view = FloorView.Current;
            if (fa > fb)
            {
                (da, db) = (db, da);
                (fa, fb) = (fb, fa);
            }
            if (fa > view)
            {
                return;
            }
            bool cut = false;
            if (fb > view)
            {
                float slab = ((view + 1) * IsoRimBridge.StoreyHeight - SlabThickness) / IsoRimBridge.Squash + alt;
                float t = Mathf.Approximately(db.y, da.y) ? 1f : Mathf.Clamp01((slab - da.y) / (db.y - da.y));
                db = da + (db - da) * t;
                cut = true;
            }
            Vector3 dir = db - da;
            float len = dir.magnitude;
            if (len < 0.01f)
            {
                return;
            }
            Vector3 f = dir / len;
            Vector3 cam = IsoRimBridge.TowardCamera();
            Vector3 n = cam - Vector3.Dot(cam, f) * f;
            if (n.sqrMagnitude < 1e-6f)
            {
                n = Vector3.Cross(f, Vector3.right).sqrMagnitude > 1e-6f ? Vector3.Cross(f, Vector3.right) : Vector3.up;
            }
            // the game's own wire quad (plane10, 1 wide, the texture stretched once over the length; the wire texture
            // itself sags, as the game's wires do), laid along the line
            Matrix4x4 m = Matrix4x4.TRS((da + db) * 0.5f, Quaternion.LookRotation(f, n.normalized), new Vector3(Width, 1f, len));
            Graphics.DrawMesh(MeshPool.plane10, m, mat, 0);
            if (collecting != Time.frameCount)
            {
                collecting = Time.frameCount;
                LastFrame.Clear();
            }
            lastFrame = Time.frameCount;
            LastFrame.Add(new Segment { kind = kind, a = da, b = db, floorA = fa, floorB = fb, cut = cut });
        }

        // A thing's wire end as the game's PrintWirePieceConnecting takes it.
        private static Vector3 End(Thing t) => t.TrueCenter() + (t.Graphic?.DrawOffset(t.Rotation) ?? Vector3.zero);

        // Site 1, every frame: the wires the prefix kept out of the section meshes.
        public static void Draw(Map map)
        {
            if (!OnFloors(map) || !IsoRimBridge.View3DOn)
            {
                return;
            }
            bool overlay = OverlayDrawHandler.ShouldDrawPowerGrid;
            float wireAlt = AltitudeLayer.SmallWire.AltitudeFor();
            float overlayAlt = AltitudeLayer.MapDataOverlay.AltitudeFor();
            List<PowerNet> nets = map.powerNetManager.AllNetsListForReading;
            for (int i = 0; i < nets.Count; i++)
            {
                List<CompPower> cons = nets[i].connectors;
                for (int j = 0; j < cons.Count; j++)
                {
                    CompPower c = cons[j];
                    Thing a = c?.parent;
                    Thing b = c?.connectParent?.parent;
                    if (!Across(a, b) || a.Position.Fogged(map) || b.Position.Fogged(map))
                    {
                        continue;
                    }
                    if (b.def != ThingDefOf.HiddenConduit)
                    {
                        DrawLine(End(a), a.Position, End(b), b.Position, map, WireMat, wireAlt, "wire");
                    }
                    if (overlay && a is Building building && (a.Faction == null || a.Faction == Faction.OfPlayer))
                    {
                        Thing from = a;
                        if (building.def.building.isAttachment)
                        {
                            from = GenConstruct.GetWallAttachedTo(a) ?? a;
                        }
                        DrawLine(from.TrueCenter(), from.Position, b.TrueCenter(), b.Position, map, PowerOverlayMats.MatConnectorLine, overlayAlt, "overlay");
                    }
                }
            }
        }

        // Site 3: the deathrest hose (the swapped Graphics.DrawMesh call of CompDeathrestBindable.PostDraw).
        public static int hoseCalls;   // (tests)

        public static void Hose(Mesh mesh, Matrix4x4 matrix, Material material, int layer, CompDeathrestBindable comp)
        {
            hoseCalls++;
            try
            {
                Thing bed = comp?.BoundPawn?.CurrentBed();
                if (bed != null && Across(bed, comp.parent))
                {
                    DrawLine(bed.TrueCenter(), bed.Position, comp.parent.TrueCenter(), comp.parent.Position, bed.Map, material, AltitudeLayer.SmallWire.AltitudeFor(), "hose");
                    return;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Drawing a deathrest hose between floors failed: " + e, 61190431);
            }
            Graphics.DrawMesh(mesh, matrix, material, layer);
        }

        internal static readonly MethodInfo DrawMesh4 = AccessTools.Method(typeof(Graphics), nameof(Graphics.DrawMesh), new[] { typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int) });
        internal static readonly MethodInfo HoseMethod = AccessTools.Method(typeof(CrossFloorWires), nameof(Hose));
    }

    // Site 1: the printed wire (both callers).
    [HarmonyPatch(typeof(PowerNetGraphics), nameof(PowerNetGraphics.PrintWirePieceConnecting))]
    [PatchGroup("cross-floor wires", key: true)]
    public static class PrintWirePieceConnecting_CrossFloor_Patch
    {
        public static bool Prefix(Thing A, Thing B)
        {
            try
            {
                return !CrossFloorWires.Across(A, B);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Checking a wire between floors failed: " + e, 61190432);
                return true;
            }
        }
    }

    // Site 1's draw (after the map's meshes, as RiserView).
    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.DrawMapMesh))]
    [PatchGroup("cross-floor wires", key: true)]
    public static class MapDrawer_CrossFloorWires_Patch
    {
        public static void Postfix()
        {
            try
            {
                CrossFloorWires.Draw(Find.CurrentMap);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Drawing the wires between floors failed: " + e, 61190433);
            }
        }
    }

    // Site 2: the build ghost's wire.
    [HarmonyPatch(typeof(PowerNetGraphics), nameof(PowerNetGraphics.RenderAnticipatedWirePieceConnecting))]
    public static class RenderAnticipatedWire_CrossFloor_Patch
    {
        public static bool Prefix(IntVec3 userPos, Rot4 rotation, IntVec2 thingSize, Thing transmitter)
        {
            try
            {
                Map map = transmitter?.Map;
                if (!CrossFloorWires.OnFloors(map) || !CrossFloorWires.Across(userPos, transmitter.Position, map))
                {
                    return true;
                }
                CrossFloorWires.DrawLine(GenThing.TrueCenter(userPos, rotation, thingSize, 0f), userPos, transmitter.TrueCenter(), transmitter.Position, map,
                    PowerOverlayMats.MatConnectorAnticipated, AltitudeLayer.MapDataOverlay.AltitudeFor(), "ghost");
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Drawing a build ghost's wire between floors failed: " + e, 61190434);
                return true;
            }
        }
    }

    // Site 3: the deathrest hose.
    [HarmonyPatch(typeof(CompDeathrestBindable), nameof(CompDeathrestBindable.PostDraw))]
    public static class DeathrestHose_CrossFloor_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            return FailSoft.Transpile(nameof(DeathrestHose_CrossFloor_Patch), original, instructions,
                code => PowerRisers.Swap(code, CrossFloorWires.DrawMesh4, CrossFloorWires.HoseMethod, 1, new CodeInstruction(OpCodes.Ldarg_0)));
        }
    }
}
