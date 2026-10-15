using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    [DefOf]
    public static class PZDefOf
    {
        public static TerrainDef PZ_OpenAir;
        public static TerrainDef PZ_UpperFloor;
        public static ThingDef PZ_Stairs;
        public static RoofDef PZ_FloorRoof;
        public static RoofDef PZ_StairwellRoof;

        static PZDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(PZDefOf));
        }
    }

    // Per-map annex layout. Present on every map (MapComponents are filled in automatically); floors <= 1 means
    // "no annex" (maps made without the addon, pocket maps, or with floors = 1), and then nothing changes.
    //
    // Layout (design §2.1, §3): the ground is rows [0, groundZ). Floor k (1..floors-1) is rows [k*stride, k*stride + groundZ).
    // stride = H = groundZ + 1 rounded up to a multiple of 17 (Section.Size), so annex sections line up 1:1 with
    // ground sections and there is always at least one void row between bands. Rows [k*stride + groundZ, (k+1)*stride)
    // are the gap ("void") and never hold a floor. Width is unchanged, so ground cell indices are unchanged.
    public class AnnexMapComponent : MapComponent
    {
        public int groundZ;   // ground rows
        public int stride;    // H
        public int floors = 1; // total storeys including the ground

        public AnnexMapComponent(Map map) : base(map)
        {
        }

        public bool HasAnnex => floors > 1 && groundZ > 0 && stride >= groundZ;

        public void Set(int groundZ, int stride, int floors)
        {
            this.groundZ = groundZ;
            this.stride = stride;
            this.floors = floors;
            ProjectZ.Invalidate();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref groundZ, "groundZ", 0);
            Scribe_Values.Look(ref stride, "stride", 0);
            Scribe_Values.Look(ref floors, "floors", 1);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                ProjectZ.Invalidate();
            }
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            if (HasAnnex && map.Size.z != floors * stride)
            {
                Log.Error($"[ProjectZ] map {map} annex data does not match its size {map.Size} (groundZ {groundZ}, stride {stride}, floors {floors}); annex disabled for this map.");
                floors = 1;
                ProjectZ.Invalidate();
            }
            else if (HasAnnex)
            {
                ProjectZ.DebugLog($"map {map}: size {map.Size}, ground z {groundZ}, stride {stride}, floors {floors}");
                // D6: roofs under upper floors are the floors' own (saves from before M1c, or anything that slipped through).
                FloorRoof.Normalize(map);
            }
        }
    }

    // The one central predicate set. Cheap: one reference compare on the hot path (last map cached).
    public static class ProjectZ
    {
        private sealed class Entry
        {
            public Map map;
            public AnnexMapComponent comp; // null = map has no annex
        }

        private static Entry cache;

        public static void Invalidate()
        {
            cache = null;
        }

        public static int FloorsForNewMaps
        {
            get
            {
                if (Dev.TestRunner.FloorsOverride > 0)
                {
                    return Dev.TestRunner.FloorsOverride;
                }
                return ProjectZMod.Settings == null ? 1 : UnityEngine.Mathf.Clamp(ProjectZMod.Settings.floors, 1, ProjectZSettings.MaxFloors);
            }
        }

        public static bool DebugOn => ProjectZMod.Settings != null && ProjectZMod.Settings.debug || Dev.TestRunner.Active;

        public static void DebugLog(string s)
        {
            if (DebugOn)
            {
                Log.Message("[ProjectZ] " + s);
            }
        }

        // The annex component of a map that has an annex, else null.
        public static AnnexMapComponent Annex(Map map)
        {
            if (map == null)
            {
                return null;
            }
            Entry e = cache;
            if (e != null && ReferenceEquals(e.map, map))
            {
                return e.comp;
            }
            AnnexMapComponent comp = map.components == null ? null : map.GetComponent<AnnexMapComponent>();
            if (comp == null)
            {
                return null; // components not built yet: don't cache
            }
            cache = new Entry { map = map, comp = comp.HasAnnex ? comp : null };
            return comp.HasAnnex ? comp : null;
        }

        public static bool HasAnnex(Map map) => Annex(map) != null;

        // Ground rows of the map (the whole map when it has no annex).
        public static int GroundZ(Map map)
        {
            AnnexMapComponent a = Annex(map);
            return a == null ? map.Size.z : a.groundZ;
        }

        // True for every cell north of the ground: floors and the void gaps.
        public static bool IsAnnex(IntVec3 c, Map map)
        {
            AnnexMapComponent a = Annex(map);
            return a != null && c.z >= a.groundZ;
        }

        // Storey of a cell: 0 = ground, k = floor k; -1 = a void gap row (no floor there).
        public static int FloorOf(IntVec3 c, Map map)
        {
            AnnexMapComponent a = Annex(map);
            if (a == null || c.z < a.groundZ)
            {
                return 0;
            }
            return c.z % a.stride < a.groundZ ? c.z / a.stride : -1;
        }

        // The ground cell under (or at) c; Invalid for a void gap cell.
        public static IntVec3 GroundCellOf(IntVec3 c, Map map)
        {
            AnnexMapComponent a = Annex(map);
            if (a == null || c.z < a.groundZ)
            {
                return c;
            }
            int zg = c.z % a.stride;
            return zg < a.groundZ ? new IntVec3(c.x, c.y, zg) : IntVec3.Invalid;
        }

        // Ground cell g lifted to floor k (k = 0 returns g). Invalid when the map has no such floor.
        public static IntVec3 CellOnFloor(IntVec3 g, int k, Map map)
        {
            if (k == 0)
            {
                return g;
            }
            AnnexMapComponent a = Annex(map);
            if (a == null || k < 0 || k >= a.floors || g.z < 0 || g.z >= a.groundZ)
            {
                return IntVec3.Invalid;
            }
            return new IntVec3(g.x, g.y, g.z + k * a.stride);
        }

        // An upper-floor cell with a floor built on it (any terrain but open air).
        public static bool HasFloor(IntVec3 c, Map map)
        {
            return c.IsValid && FloorOf(c, map) >= 1 && map.terrainGrid.TerrainAt(c) != PZDefOf.PZ_OpenAir;
        }

        // THE gameplay map (design "M1c-2 coordinates", section 2): a world position on any floor to its virtual 3D
        // position: x and z over the ground, y its height above the ground in cells (floor k at k * Storey). Every
        // geometry decision between floors (facing, AI radii, ground distance) goes through this.
        public static Vector3 Virtual(Vector3 p, Map map)
        {
            AnnexMapComponent a = Annex(map);
            if (a == null || p.z < a.groundZ)
            {
                return new Vector3(p.x, 0f, p.z);
            }
            int k = Mathf.FloorToInt(p.z) / a.stride;
            return new Vector3(p.x, k * Combat.Heights.Storey, p.z - k * a.stride);
        }

        public static Vector3 Virtual(IntVec3 c, Map map) => Virtual(c.ToVector3Shifted(), map);

        // Flat distance between two positions over the ground.
        public static float VirtualFlatDistance(Vector3 a, Vector3 b, Map map)
        {
            Vector3 d = Virtual(b, map) - Virtual(a, map);
            return Mathf.Sqrt(d.x * d.x + d.z * d.z);
        }

        // Ground rectangle (the whole map without an annex).
        public static CellRect GroundRect(Map map) => new CellRect(0, 0, map.Size.x, GroundZ(map));

        // H for a ground height: groundZ + 1 rounded up to a multiple of the 17-cell section size.
        public static int StrideFor(int groundZ) => (groundZ + 1 + 16) / 17 * 17;
    }
}
