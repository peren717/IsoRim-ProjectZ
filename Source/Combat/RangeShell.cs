using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace ProjectZ.Combat
{
    // The see-through range shell of the preview (RangePreview). Built in true space (cells, height up) round the muzzle:
    // per direction the range rule's reach (Range3D.ShellRadius: the range, more downward by the per-floor bonus), from
    // the top down to where it meets the ground. Vertex alpha is a Fresnel term (faint where the shell faces the camera,
    // brighter toward its outline) for the camera's current direction, so a mesh is kept per shape and view direction
    // (in 2-degree steps) and replaced as the view turns; old ones are destroyed a few frames later (never while the
    // frame that drew them may still use them). Drawn with IsoRim's see-through material (IsoRimBridge.ShellMaterial).
    public static class RangeShell
    {
        private const int Lat = 36;    // rows from the top to the ground cut
        private const int Lon = 72;    // segments round
        private const int KeepFrames = 4;

        private sealed class Entry
        {
            public Mesh mesh;
            public int lastFrame;
        }

        private static readonly Dictionary<long, Entry> meshes = new Dictionary<long, Entry>();
        private static readonly List<long> stale = new List<long>();
        private static int sweptFrame = -1;

        // centre: the muzzle in true space (x, height in cells, z) over the ground cell; muzzle: its height over the floor.
        // sphere: a plain sphere of radius range instead of the range rule (no downward bonus; the gravship footprint,
        // ShipRange), cut at the ground the same way.
        public static void Draw(Vector3 centre, float range, float muzzle, bool sphere = false)
        {
            Material mat = IsoRimBridge.ShellMaterial();
            if (mat == null)
            {
                return;
            }
            Vector3 view = IsoRimBridge.TowardCamera();
            int yawBin = Mathf.RoundToInt(Mathf.Atan2(view.x, view.z) * Mathf.Rad2Deg / 2f);
            int pitchBin = Mathf.RoundToInt(Mathf.Asin(Mathf.Clamp(view.y, -1f, 1f)) * Mathf.Rad2Deg / 2f);
            long key = ((long)Mathf.RoundToInt(range * 10f) << 40) ^ ((long)Mathf.RoundToInt(centre.y * 20f) << 24)
                ^ ((long)(yawBin & 0xff) << 8) ^ (long)(pitchBin & 0xff) ^ ((long)Mathf.RoundToInt(Range3D.Storey * 100f) << 52)
                ^ (sphere ? 1L << 62 : 0L);
            Sweep();
            if (!meshes.TryGetValue(key, out Entry e))
            {
                e = new Entry { mesh = Build(range, centre.y, view, sphere) };
                meshes[key] = e;
            }
            e.lastFrame = Time.frameCount;
            if (e.mesh == null)
            {
                return;
            }
            float s = IsoRimBridge.Squash;
            Matrix4x4 m = Matrix4x4.Translate(new Vector3(centre.x, centre.y / s, centre.z)) * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f));
            Graphics.DrawMesh(e.mesh, m, mat, 0);
        }

        private static void Sweep()
        {
            if (sweptFrame == Time.frameCount)
            {
                return;
            }
            sweptFrame = Time.frameCount;
            stale.Clear();
            foreach (KeyValuePair<long, Entry> kv in meshes)
            {
                if (Time.frameCount - kv.Value.lastFrame > KeepFrames)
                {
                    stale.Add(kv.Key);
                }
            }
            foreach (long k in stale)
            {
                if (meshes[k].mesh != null)
                {
                    Object.Destroy(meshes[k].mesh);
                }
                meshes.Remove(k);
            }
        }

        // Unit direction at polar angle a from straight up and azimuth b.
        private static Vector3 Dir(float a, float b) => new Vector3(Mathf.Sin(a) * Mathf.Cos(b), Mathf.Cos(a), Mathf.Sin(a) * Mathf.Sin(b));

        // Height of the shell over the ground at polar angle a (true space): the muzzle height plus the radius along it.
        private static float HeightAt(float a, float range, float h0, bool sphere)
        {
            float dy = Mathf.Cos(a);
            return h0 + Radius(range, dy, sphere) * dy;
        }

        // The reach along a direction whose height part is dy: the range rule (bulging downward) or a plain sphere.
        private static float Radius(float range, float dy, bool sphere) => sphere ? range : Range3D.ShellRadius(range, dy);

        private static Vector3 Point(float a, float b, float range, bool sphere)
        {
            Vector3 d = Dir(a, b);
            return d * Radius(range, d.y, sphere);
        }

        public static Mesh Build(float range, float h0, Vector3 view, bool sphere = false)
        {
            // The polar angle where the shell meets the ground (height falls steadily past the side).
            float lo = Mathf.PI * 0.5f, hi = Mathf.PI;
            if (HeightAt(lo, range, h0, sphere) <= 0f)
            {
                hi = lo;
                lo = 0f;
            }
            for (int i = 0; i < 40; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (HeightAt(mid, range, h0, sphere) > 0f)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }
            float aMax = lo;
            List<Vector3> verts = new List<Vector3>((Lat + 1) * (Lon + 1));
            List<Color32> cols = new List<Color32>(verts.Capacity);
            List<int> tris = new List<int>(Lat * Lon * 6);
            Color c0 = RangePreview.ShellColor;
            const float eps = 0.002f;
            for (int r = 0; r <= Lat; r++)
            {
                float a = aMax * r / Lat;
                for (int k = 0; k <= Lon; k++)
                {
                    float b = Mathf.PI * 2f * k / Lon;
                    Vector3 p = Point(a, b, range, sphere);
                    if (r == Lat)
                    {
                        p.y = -h0;   // exactly on the ground
                    }
                    // normal from the surface's two tangents (the shell is not a sphere below the muzzle)
                    Vector3 ta = Point(Mathf.Max(a, eps) + eps, b, range, sphere) - Point(Mathf.Max(a, eps) - eps, b, range, sphere);
                    Vector3 tb = Point(Mathf.Max(a, 0.05f), b + eps, range, sphere) - Point(Mathf.Max(a, 0.05f), b - eps, range, sphere);
                    Vector3 n = Vector3.Cross(tb, ta);
                    n = n.sqrMagnitude > 1e-12f ? n.normalized : Dir(a, b);
                    if (a < 0.05f)
                    {
                        n = Vector3.up;
                    }
                    float f = 1f - Mathf.Abs(Vector3.Dot(n, view));
                    float alpha = Mathf.Lerp(RangePreview.ShellAlphaFace, RangePreview.ShellAlphaEdge, f * f * f);
                    verts.Add(p);
                    cols.Add(new Color(c0.r, c0.g, c0.b, alpha));
                }
            }
            int row = Lon + 1;
            for (int r = 0; r < Lat; r++)
            {
                for (int k = 0; k < Lon; k++)
                {
                    int a0 = r * row + k, a1 = a0 + 1, b0 = a0 + row, b1 = b0 + 1;
                    tris.Add(a0); tris.Add(a1); tris.Add(b0);
                    tris.Add(a1); tris.Add(b1); tris.Add(b0);
                }
            }
            Mesh mesh = new Mesh();
            mesh.name = "PZ_RangeShell";
            mesh.SetVertices(verts);
            mesh.SetColors(cols);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
