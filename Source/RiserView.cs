using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // What a vertical power link looks like (design "Networks across floors", power overlay decisions 2026-10-16). A riser
    // is a conduit with a conduit stacked directly above it, by the same rule that joins them into one net
    // (PowerRisers.Riser), so what is drawn never disagrees with the nets. In the 3D view, for each riser:
    //   - a short vertical conduit piece: from the lower conduit up through the ceiling (the upper floor's slab) and a
    //     little out of the floor above, in the conduit's colour, lit like IsoRim's models. A hidden conduit (drawn
    //     invisible by the game) gets no piece on its own floor, as it gets no sprite;
    //   - with the power overlay on: the overlay's own straight conduit piece stood up from the lower conduit to the upper
    //     one (turned to face the camera round the vertical) and the overlay's connector mark at both ends, drawn like the rest of the overlay (lower floors' overlay keeps showing through).
    //     The vanilla overlay has one look for every net, so the link has it too. While the upper floor is hidden, the
    //     wire is a half-storey stub going up.
    // The risers of a map are listed once and kept until a transmitter changes: PowerNetManager.NotifyDrawersForWireUpdate
    // is where the game marks the power grid drawing dirty for every transmitter spawned, despawned or switched (and every
    // connector); its postfix marks the list stale. Nothing is printed into the game's section meshes, so no section
    // on another floor needs regenerating.
    public static class RiserView
    {
        public struct Riser
        {
            public IntVec3 low, high;   // the two conduit cells (high = the floor above)
            public IntVec3 ground;      // their column
            public int floor;           // floor of the lower one
            public bool lowDrawn, highDrawn, overlay;
        }

        private sealed class Cache
        {
            public bool dirty = true;
            public readonly List<Riser> risers = new List<Riser>();
        }

        private static readonly ConditionalWeakTable<Map, Cache> caches = new ConditionalWeakTable<Map, Cache>();

        // Conduit grey (PowerConduit_Atlas' cable) and the piece's size, in cells.
        public static readonly Color32 PipeColor = new Color32(108, 107, 104, 255);
        public const float PipeWidth = 0.22f;
        public const float Stub = 0.25f;   // how far the piece comes out of the floor above

        // Tests: what was drawn in the last frame that drew.
        public static int lastPieces, lastWires, lastFrame = -1;

        public static void MarkDirty(Map map)
        {
            if (map != null && caches.TryGetValue(map, out Cache c))
            {
                c.dirty = true;
            }
        }

        public static List<Riser> For(Map map)
        {
            Cache c = caches.GetValue(map, m => new Cache());
            if (c.dirty)
            {
                c.dirty = false;
                Rebuild(map, c.risers);
            }
            return c.risers;
        }

        private static void Rebuild(Map map, List<Riser> list)
        {
            list.Clear();
            if (ProjectZ.Annex(map) == null)
            {
                return;
            }
            List<Thing> things = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
            for (int i = 0; i < things.Count; i++)
            {
                if (!(things[i] is Building b) || !PowerRisers.IsConduit(b.def) || !b.TransmitsPowerNow || PowerRisers.ConduitAt(b.Position, map) != b)
                {
                    continue;
                }
                IntVec3 up = PowerRisers.Riser(b.Position, map, true);
                if (!up.IsValid)
                {
                    continue;
                }
                Building top = PowerRisers.ConduitAt(up, map);
                list.Add(new Riser
                {
                    low = b.Position,
                    high = up,
                    ground = ProjectZ.GroundCellOf(b.Position, map),
                    floor = ProjectZ.FloorOf(b.Position, map),
                    lowDrawn = Drawn(b),
                    highDrawn = Drawn(top),
                    overlay = OnOverlay(b) || OnOverlay(top),
                });
            }
        }

        // The game draws the conduit at all (a hidden conduit's colour is fully transparent).
        private static bool Drawn(Building b) => b != null && b.def.drawerType != DrawerType.None && b.DrawColor.a > 0.01f;

        // As SectionLayer_ThingsPowerGrid.TakePrintFrom: the player's and nobody's.
        private static bool OnOverlay(Building b) => b != null && (b.Faction == null || b.Faction == Faction.OfPlayer);

        // ---- drawing (postfix of MapDrawer.DrawMapMesh, the current map, every frame)

        // (made on first use, on the main thread; the holder keeps the game's static-asset check quiet)
        [StaticConstructorOnStartup]
        private static class Assets
        {
            public static Mesh pipe, wire;
            public static Material riserLine;
        }

        public static void Draw(Map map)
        {
            if (map == null || !IsoRimBridge.View3DOn || ProjectZ.Annex(map) == null)
            {
                return;
            }
            List<Riser> list = For(map);
            int pieces = 0, wires = 0;
            if (list.Count > 0)
            {
                EnsureMeshes();
                int view = FloorView.Current;
                float s = IsoRimBridge.Squash;
                float storey = IsoRimBridge.StoreyHeight;
                bool overlay = OverlayDrawHandler.ShouldDrawPowerGrid;
                float alt = AltitudeLayer.MapDataOverlay.AltitudeFor();
                Quaternion face = FaceCamera();
                for (int i = 0; i < list.Count; i++)
                {
                    Riser r = list[i];
                    if (r.floor > view)
                    {
                        continue;   // both ends on hidden floors
                    }
                    bool upShown = r.floor + 1 <= view;
                    float h0 = r.floor * storey, h1 = (r.floor + 1) * storey;
                    float x = r.ground.x + 0.5f, z = r.ground.z + 0.5f;
                    // the conduit piece
                    float bottom = r.lowDrawn ? h0 : h1;
                    float top = h1 + (upShown && r.highDrawn ? Stub : 0f);
                    if ((r.lowDrawn || upShown && r.highDrawn) && top > bottom)
                    {
                        Matrix4x4 m = Matrix4x4.TRS(new Vector3(x, bottom / s, z), Quaternion.identity, new Vector3(PipeWidth, (top - bottom) / s, PipeWidth));
                        IsoRimBridge.DrawLitSolid(Assets.pipe, m);
                        pieces++;
                    }
                    // the overlay's link
                    if (overlay && r.overlay)
                    {
                        float y0 = h0 / s + alt;
                        float y1 = upShown ? h1 / s + alt : (h0 + storey * 0.5f) / s + alt;
                        Graphics.DrawMesh(Assets.wire, Matrix4x4.TRS(new Vector3(x, y0, z), face, new Vector3(1f, y1 - y0, 1f)), RiserLine(), 0);
                        Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(new Vector3(x, y0, z), Quaternion.identity, Vector3.one), PowerOverlayMats.MatConnectorBase, 0);
                        if (upShown)
                        {
                            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(new Vector3(x, y1, z), Quaternion.identity, Vector3.one), PowerOverlayMats.MatConnectorBase, 0);
                        }
                        wires++;
                    }
                }
            }
            lastPieces = pieces;
            lastWires = wires;
            lastFrame = Time.frameCount;
        }

        // The overlay's own straight north-south conduit piece (the transmitter atlas tile linked up and down), stood up.
        private static Material RiserLine()
        {
            if (Assets.riserLine == null)
            {
                Material atlas = (PowerOverlayMats.LinkedOverlayGraphic as Graphic_Linked)?.SubGraphic?.MatSingle;
                Assets.riserLine = atlas != null ? MaterialAtlasPool.SubMaterialFromAtlas(atlas, LinkDirections.Up | LinkDirections.Down) : PowerOverlayMats.MatConnectorLine;
            }
            return Assets.riserLine;
        }

        // Turns the upright wire quad (in the x-y plane) to face the camera round the vertical.
        private static Quaternion FaceCamera()
        {
            Vector3 v = IsoRimBridge.TowardCamera();
            v.y = 0f;
            return v.sqrMagnitude < 1e-6f ? Quaternion.identity : Quaternion.LookRotation(v.normalized, Vector3.up);
        }

        private static void EnsureMeshes()
        {
            if (Assets.pipe == null)
            {
                Assets.pipe = Box();
            }
            if (Assets.wire == null)
            {
                Assets.wire = Upright();
            }
        }

        // A unit column, 1 x 1 across (centred) and 0..1 up: four sides and a cap, vertex-coloured.
        private static Mesh Box()
        {
            List<Vector3> v = new List<Vector3>();
            List<Color32> c = new List<Color32>();
            List<int> t = new List<int>();
            const float a = 0.5f;
            Vector3 p000 = new Vector3(-a, 0f, -a), p100 = new Vector3(a, 0f, -a), p101 = new Vector3(a, 0f, a), p001 = new Vector3(-a, 0f, a);
            Vector3 p010 = new Vector3(-a, 1f, -a), p110 = new Vector3(a, 1f, -a), p111 = new Vector3(a, 1f, a), p011 = new Vector3(-a, 1f, a);
            Quad(v, c, t, p000, p010, p110, p100);   // south
            Quad(v, c, t, p100, p110, p111, p101);   // east
            Quad(v, c, t, p101, p111, p011, p001);   // north
            Quad(v, c, t, p001, p011, p010, p000);   // west
            Quad(v, c, t, p010, p011, p111, p110);   // top
            Mesh m = new Mesh { name = "PZ_RiserPipe" };
            m.SetVertices(v);
            m.SetColors(c);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        private static void Quad(List<Vector3> v, List<Color32> c, List<int> t, Vector3 a, Vector3 b, Vector3 d, Vector3 e)
        {
            int i = v.Count;
            v.Add(a);
            v.Add(b);
            v.Add(d);
            v.Add(e);
            for (int k = 0; k < 4; k++)
            {
                c.Add(PipeColor);
            }
            t.Add(i);
            t.Add(i + 1);
            t.Add(i + 2);
            t.Add(i);
            t.Add(i + 2);
            t.Add(i + 3);
        }

        // An upright quad, 1 wide (x, centred) and 0..1 up (y), texture v along y as the game's wire planes; both sides.
        private static Mesh Upright()
        {
            Mesh m = new Mesh { name = "PZ_RiserWire" };
            m.SetVertices(new List<Vector3> { new Vector3(-0.5f, 0f, 0f), new Vector3(-0.5f, 1f, 0f), new Vector3(0.5f, 1f, 0f), new Vector3(0.5f, 0f, 0f) });
            m.SetUVs(0, new List<Vector2> { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) });
            m.SetTriangles(new List<int> { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 }, 0);
            m.RecalculateBounds();
            return m;
        }
    }

    // Every transmitter or connector change goes through here (the game's own "power drawing is stale" call).
    [HarmonyPatch(typeof(PowerNetManager), nameof(PowerNetManager.NotifyDrawersForWireUpdate))]
    public static class PowerNetManager_RiserDirty_Patch
    {
        public static void Postfix(PowerNetManager __instance)
        {
            RiserView.MarkDirty(__instance.map);
        }
    }

    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.DrawMapMesh))]
    public static class MapDrawer_Risers_Patch
    {
        public static void Postfix()
        {
            try
            {
                RiserView.Draw(Find.CurrentMap);
            }
            catch (System.Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Drawing the power risers failed: " + e, 61190417);
            }
        }
    }
}
