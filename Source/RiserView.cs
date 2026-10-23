using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // What a vertical net link looks like (design "Networks across floors", power overlay decisions 2026-10-16; Api 3 opened
    // it to other nets, 2026-10-23). A riser is a transmitter with one of the same net stacked directly above it. Each net
    // kind is a RiserSource that lists its own links (power is the first, built in: a conduit with a conduit over it, by the
    // same rule that joins them, PowerRisers.Riser, so what is drawn never disagrees with the nets; addons add theirs with
    // Api.AddRiserSource). In the 3D view, for each link:
    //   - a short vertical piece: from the lower cell up through the ceiling (the upper floor's slab) and a little out of
    //     the floor above, in the link's colour, lit like IsoRim's models. An end the game draws invisible (a hidden
    //     conduit or pipe) gets no piece on its own floor, as it gets no sprite;
    //   - with the source's overlay shown: the source's overlay line stood up from the lower cell to the upper one (turned
    //     to face the camera round the vertical) and its end mark at both ends, drawn like the rest of the overlay (lower
    //     floors' overlay keeps showing through). While the upper floor is hidden, the line is a half-storey stub going up.
    // The links of a map are listed once per source and kept until the source marks them stale (power:
    // PowerNetManager.NotifyDrawersForWireUpdate, where the game marks the power drawing dirty for every transmitter
    // spawned, despawned or switched). Nothing is printed into the game's section meshes, so no section on another floor
    // needs regenerating.
    public static class RiserView
    {
        public struct Riser
        {
            public IntVec3 low, high;   // the two cells (high = the floor above)
            public IntVec3 ground;      // their column
            public int floor;           // floor of the lower one
            public bool lowDrawn, highDrawn, overlay;
            internal Color32 colour;
            internal float width;
            internal Material line, end;
        }

        private sealed class Entry
        {
            public bool dirty = true;
            public readonly List<Riser> risers = new List<Riser>();
        }

        private sealed class Cache
        {
            public readonly Dictionary<RiserSource, Entry> entries = new Dictionary<RiserSource, Entry>();
        }

        private static readonly ConditionalWeakTable<Map, Cache> caches = new ConditionalWeakTable<Map, Cache>();

        // Power first; addons' sources after it, in the order added.
        private static readonly List<RiserSource> sources = new List<RiserSource> { PowerRiserSource.Instance };

        private static readonly List<RiserLink> collectBuffer = new List<RiserLink>();

        // Conduit grey (PowerConduit_Atlas' cable) and the piece's size, in cells.
        public static readonly Color32 PipeColor = new Color32(108, 107, 104, 255);
        public const float PipeWidth = 0.22f;
        public const float Stub = 0.25f;   // how far the piece comes out of the floor above

        // Tests: what was drawn in the last frame that drew (all sources together; per source: RiserSource.LastPieces).
        public static int lastPieces, lastWires, lastFrame = -1;

        internal static void Add(RiserSource source)
        {
            if (source != null && !sources.Contains(source))
            {
                sources.Add(source);
            }
        }

        internal static void Remove(RiserSource source)
        {
            if (source != null && source != PowerRiserSource.Instance)
            {
                sources.Remove(source);
            }
        }

        // Every source's links on this map are stale.
        public static void MarkDirty(Map map)
        {
            if (map != null && caches.TryGetValue(map, out Cache c))
            {
                foreach (Entry e in c.entries.Values)
                {
                    e.dirty = true;
                }
            }
        }

        // One source's links on this map are stale.
        internal static void MarkDirty(Map map, RiserSource source)
        {
            if (map != null && source != null && caches.TryGetValue(map, out Cache c) && c.entries.TryGetValue(source, out Entry e))
            {
                e.dirty = true;
            }
        }

        // The power risers of a map (tests and the power rule's checks).
        public static List<Riser> For(Map map) => For(map, PowerRiserSource.Instance);

        internal static List<Riser> For(Map map, RiserSource source)
        {
            Cache c = caches.GetValue(map, m => new Cache());
            if (!c.entries.TryGetValue(source, out Entry e))
            {
                e = new Entry();
                c.entries[source] = e;
            }
            if (e.dirty)
            {
                e.dirty = false;
                Rebuild(map, source, e.risers);
            }
            return e.risers;
        }

        private static void Rebuild(Map map, RiserSource source, List<Riser> list)
        {
            list.Clear();
            if (ProjectZ.Annex(map) == null)
            {
                return;
            }
            collectBuffer.Clear();
            try
            {
                source.Collect(map, collectBuffer);
            }
            catch (System.Exception e)
            {
                Log.ErrorOnce($"[ProjectZ] Riser source {source.GetType().FullName} failed to list its links: {e}", source.GetHashCode() ^ 0x5157e1);
                collectBuffer.Clear();
            }
            for (int i = 0; i < collectBuffer.Count; i++)
            {
                RiserLink l = collectBuffer[i];
                IntVec3 high = Api.StackedCell(l.low, map, true);
                if (!high.IsValid)
                {
                    continue;
                }
                list.Add(new Riser
                {
                    low = l.low,
                    high = high,
                    ground = ProjectZ.GroundCellOf(l.low, map),
                    floor = ProjectZ.FloorOf(l.low, map),
                    lowDrawn = l.lowDrawn,
                    highDrawn = l.highDrawn,
                    overlay = l.overlay,
                    colour = l.colour,
                    width = l.width > 0f ? l.width : PipeWidth,
                    line = l.overlayLine,
                    end = l.overlayEnd,
                });
            }
            collectBuffer.Clear();
        }

        // ---- drawing (postfix of MapDrawer.DrawMapMesh, the current map, every frame)

        // (made on first use, on the main thread; the holder keeps the game's static-asset check quiet)
        [StaticConstructorOnStartup]
        private static class Assets
        {
            public static readonly Dictionary<uint, Mesh> pipes = new Dictionary<uint, Mesh>();
            public static Mesh wire;
        }

        public static void Draw(Map map)
        {
            if (map == null || !IsoRimBridge.View3DOn || ProjectZ.Annex(map) == null)
            {
                return;
            }
            int pieces = 0, wires = 0;
            int view = FloorView.Current;
            float s = IsoRimBridge.Squash;
            float storey = IsoRimBridge.StoreyHeight;
            float alt = AltitudeLayer.MapDataOverlay.AltitudeFor();
            Quaternion face = Quaternion.identity;
            bool faceSet = false;
            for (int k = 0; k < sources.Count; k++)
            {
                RiserSource src = sources[k];
                List<Riser> list = For(map, src);
                int srcPieces = 0, srcWires = 0;
                if (list.Count > 0)
                {
                    bool overlay;
                    try
                    {
                        overlay = src.OverlayShown(map);
                    }
                    catch (System.Exception e)
                    {
                        Log.ErrorOnce($"[ProjectZ] Riser source {src.GetType().FullName} failed in OverlayShown: {e}", src.GetHashCode() ^ 0x5157e2);
                        overlay = false;
                    }
                    if (overlay && !faceSet)
                    {
                        face = FaceCamera();
                        faceSet = true;
                    }
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
                        // the solid piece
                        float bottom = r.lowDrawn ? h0 : h1;
                        float top = h1 + (upShown && r.highDrawn ? Stub : 0f);
                        if ((r.lowDrawn || upShown && r.highDrawn) && top > bottom)
                        {
                            Matrix4x4 m = Matrix4x4.TRS(new Vector3(x, bottom / s, z), Quaternion.identity, new Vector3(r.width, (top - bottom) / s, r.width));
                            IsoRimBridge.DrawLitSolid(PipeMesh(r.colour), m);
                            srcPieces++;
                        }
                        // the overlay's link
                        if (overlay && r.overlay && r.line != null)
                        {
                            float y0 = h0 / s + alt;
                            float y1 = upShown ? h1 / s + alt : (h0 + storey * 0.5f) / s + alt;
                            Graphics.DrawMesh(WireMesh(), Matrix4x4.TRS(new Vector3(x, y0, z), face, new Vector3(1f, y1 - y0, 1f)), r.line, 0);
                            if (r.end != null)
                            {
                                Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(new Vector3(x, y0, z), Quaternion.identity, Vector3.one), r.end, 0);
                                if (upShown)
                                {
                                    Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(new Vector3(x, y1, z), Quaternion.identity, Vector3.one), r.end, 0);
                                }
                            }
                            srcWires++;
                        }
                    }
                }
                src.LastPieces = srcPieces;
                src.LastLinks = srcWires;
                src.LastFrame = Time.frameCount;
                pieces += srcPieces;
                wires += srcWires;
            }
            lastPieces = pieces;
            lastWires = wires;
            lastFrame = Time.frameCount;
        }

        // Turns the upright wire quad (in the x-y plane) to face the camera round the vertical.
        private static Quaternion FaceCamera()
        {
            Vector3 v = IsoRimBridge.TowardCamera();
            v.y = 0f;
            return v.sqrMagnitude < 1e-6f ? Quaternion.identity : Quaternion.LookRotation(v.normalized, Vector3.up);
        }

        private static Mesh PipeMesh(Color32 colour)
        {
            uint key = (uint)colour.r << 24 | (uint)colour.g << 16 | (uint)colour.b << 8 | colour.a;
            if (!Assets.pipes.TryGetValue(key, out Mesh m) || m == null)
            {
                m = Box(colour);
                Assets.pipes[key] = m;
            }
            return m;
        }

        private static Mesh WireMesh()
        {
            if (Assets.wire == null)
            {
                Assets.wire = Upright();
            }
            return Assets.wire;
        }

        // A unit column, 1 x 1 across (centred) and 0..1 up: four sides and a cap, vertex-coloured.
        private static Mesh Box(Color32 colour)
        {
            List<Vector3> v = new List<Vector3>();
            List<Color32> c = new List<Color32>();
            List<int> t = new List<int>();
            const float a = 0.5f;
            Vector3 p000 = new Vector3(-a, 0f, -a), p100 = new Vector3(a, 0f, -a), p101 = new Vector3(a, 0f, a), p001 = new Vector3(-a, 0f, a);
            Vector3 p010 = new Vector3(-a, 1f, -a), p110 = new Vector3(a, 1f, -a), p111 = new Vector3(a, 1f, a), p011 = new Vector3(-a, 1f, a);
            Quad(v, c, t, colour, p000, p010, p110, p100);   // south
            Quad(v, c, t, colour, p100, p110, p111, p101);   // east
            Quad(v, c, t, colour, p101, p111, p011, p001);   // north
            Quad(v, c, t, colour, p001, p011, p010, p000);   // west
            Quad(v, c, t, colour, p010, p011, p111, p110);   // top
            Mesh m = new Mesh { name = "PZ_RiserPipe" };
            m.SetVertices(v);
            m.SetColors(c);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        private static void Quad(List<Vector3> v, List<Color32> c, List<int> t, Color32 colour, Vector3 a, Vector3 b, Vector3 d, Vector3 e)
        {
            int i = v.Count;
            v.Add(a);
            v.Add(b);
            v.Add(d);
            v.Add(e);
            for (int k = 0; k < 4; k++)
            {
                c.Add(colour);
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

    // The power risers (the first source): a conduit with a conduit stacked directly over it (PowerRisers.Riser).
    [StaticConstructorOnStartup]
    internal sealed class PowerRiserSource : RiserSource
    {
        public static readonly PowerRiserSource Instance = new PowerRiserSource();

        private static Material riserLine;

        public override void Collect(Map map, List<RiserLink> into)
        {
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
                into.Add(new RiserLink
                {
                    low = b.Position,
                    colour = RiserView.PipeColor,
                    width = RiserView.PipeWidth,
                    lowDrawn = Drawn(b),
                    highDrawn = Drawn(top),
                    overlay = OnOverlay(b) || OnOverlay(top),
                    overlayLine = RiserLine(),
                    overlayEnd = PowerOverlayMats.MatConnectorBase,
                });
            }
        }

        public override bool OverlayShown(Map map) => OverlayDrawHandler.ShouldDrawPowerGrid;

        // The game draws the conduit at all (a hidden conduit's colour is fully transparent).
        private static bool Drawn(Building b) => b != null && b.def.drawerType != DrawerType.None && b.DrawColor.a > 0.01f;

        // As SectionLayer_ThingsPowerGrid.TakePrintFrom: the player's and nobody's.
        private static bool OnOverlay(Building b) => b != null && (b.Faction == null || b.Faction == Faction.OfPlayer);

        // The overlay's own straight north-south conduit piece (the transmitter atlas tile linked up and down), stood up.
        private static Material RiserLine()
        {
            if (riserLine == null)
            {
                Material atlas = (PowerOverlayMats.LinkedOverlayGraphic as Graphic_Linked)?.SubGraphic?.MatSingle;
                riserLine = atlas != null ? MaterialAtlasPool.SubMaterialFromAtlas(atlas, LinkDirections.Up | LinkDirections.Down) : PowerOverlayMats.MatConnectorLine;
            }
            return riserLine;
        }
    }

    // Every transmitter or connector change goes through here (the game's own "power drawing is stale" call).
    [HarmonyPatch(typeof(PowerNetManager), nameof(PowerNetManager.NotifyDrawersForWireUpdate))]
    public static class PowerNetManager_RiserDirty_Patch
    {
        public static void Postfix(PowerNetManager __instance)
        {
            PowerRiserSource.Instance.MarkDirty(__instance.map);
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
                Log.ErrorOnce("[ProjectZ] Drawing the risers failed: " + e, 61190417);
            }
        }
    }
}
