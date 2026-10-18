using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    [DefOf]
    public static class PZDefOf
    {
        public static TerrainDef PZ_OpenAir;
        public static TerrainDef PZ_UpperFloor;   // the wood slab (SlabDefs: test slabs with SlabDefs.Is, not against this)
        public static ThingDef PZ_Stairs;
        public static RoofDef PZ_FloorRoof;
        public static RoofDef PZ_StairwellRoof;
        [MayRequireOdyssey]
        public static TerrainDef PZ_ShipDeck;     // the ship upper deck (ShipDeck.cs); null without Odyssey
        [MayRequireOdyssey]
        public static ThingDef PZ_ShipStairs;     // the ship stairs (ShipStairs.cs); null without Odyssey

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
    //
    // Layout invariant (layoutVersion 1), kept for a possible grow-on-load (isorim-pz-floors-on-demand.md):
    //  - bands are only ever appended at the top (floor N = rows [N*stride, N*stride + groundZ)); nothing is ever
    //    inserted below or between, so no existing cell index ever moves;
    //  - groundZ and stride are fixed per map once generated (StrideFor is only called at generation).
    // Bump layoutVersion if a future change breaks either rule, and migrate older saves explicitly.
    //
    // layoutVersion 2 (mountains foundation F1, Levels.cs): bands and levels are told apart. Band b holds level
    // bandLevel[b] (saved; missing = identity, so a version-1 save is a version-2 save with the identity table and is
    // upgraded on load as it is). "floors" stays the number of bands.
    public class AnnexMapComponent : MapComponent
    {
        public const int CurrentLayoutVersion = 2;

        public int groundZ;   // ground rows
        public int stride;    // H
        public int floors = 1; // number of bands including the ground (one level each)
        public int layoutVersion = CurrentLayoutVersion; // saves without it are version 1
        // F1: the level of each band (index = band); null = identity (band b is level b). Read through Levels.
        public List<int> bandLevel;
        // F5: vanilla's elevation per ground cell (NaturalElevation), captured at generation; null on maps made before.
        public byte[] elevation;
        // Tall mountains (Mountains.cs): this map was generated with its mountains risen into the bands (saved; the
        // rules that only matter where natural rock floors exist ask it).
        public bool tallMountains;
        // Tall mountains: the levels the player's pawns have reached (MountainFog.Reveal; saved). Until then a level's
        // natural rock floors don't count as used for the floor HUD (ShownLevels, user option 2). Null on a tall map
        // saved before: every level counts as reached (old saves keep their revealed tops, user Q8).
        public List<int> reachedLevels;

        public bool Reached(int L) => reachedLevels == null || reachedLevels.Contains(L);

        public void MarkReached(int L)
        {
            if (reachedLevels != null && !reachedLevels.Contains(L))
            {
                reachedLevels.Add(L);
                ShownLevels.LevelReached(map, L);
            }
        }

        // F1, derived from bandLevel (not saved): level per band, band per level (index L - minLevel).
        private int[] levelOfBand;
        private int[] bandOfLevel;
        private int minLevel, maxLevel;
        // The levels the floor UI offers (ShownLevels), cached per map (not saved).
        public ShownLevels.State shownLevels;

        public AnnexMapComponent(Map map) : base(map)
        {
        }

        public bool HasAnnex => floors > 1 && groundZ > 0 && stride >= groundZ;

        public void Set(int groundZ, int stride, int floors)
        {
            this.groundZ = groundZ;
            this.stride = stride;
            this.floors = floors;
            levelOfBand = null;
            shownLevels = null;
            ProjectZ.Invalidate();
        }

        public int MinLevel
        {
            get
            {
                EnsureLevels();
                return minLevel;
            }
        }

        public int MaxLevel
        {
            get
            {
                EnsureLevels();
                return maxLevel;
            }
        }

        // Band of a world row: 0 below groundZ, -1 for a void row, else the band (past the map's north edge the band
        // number goes on counting, Levels' 1:1 rule).
        public int BandOfRow(int z)
        {
            if (z < groundZ)
            {
                return 0;
            }
            return z % stride < groundZ ? z / stride : -1;
        }

        // The band whose rows or following void rows hold row z (0 below groundZ).
        public int BandOfRowLoose(int z) => z < groundZ ? 0 : z / stride;

        // The level band b holds (b outside 0..floors-1: 1:1 on from the nearest band).
        public int LevelOfBand(int b)
        {
            EnsureLevels();
            if (b < 0)
            {
                return levelOfBand[0] + b;
            }
            if (b >= levelOfBand.Length)
            {
                return levelOfBand[levelOfBand.Length - 1] + (b - (levelOfBand.Length - 1));
            }
            return levelOfBand[b];
        }

        // The band holding level L (L outside MinLevel..MaxLevel: 1:1 on from the band of the nearest end).
        public int BandOfLevel(int L)
        {
            EnsureLevels();
            if (L < minLevel)
            {
                return bandOfLevel[0] - (minLevel - L);
            }
            if (L > maxLevel)
            {
                return bandOfLevel[maxLevel - minLevel] + (L - maxLevel);
            }
            return bandOfLevel[L - minLevel];
        }

        private void EnsureLevels()
        {
            int n = Mathf.Max(1, floors);
            if (levelOfBand != null && levelOfBand.Length == n)
            {
                return;
            }
            if (bandLevel != null && !ValidTable(bandLevel, n))
            {
                Log.Error($"[ProjectZ] map {map}: band level table [{string.Join(", ", bandLevel)}] does not fit {n} band(s) (band 0 = level 0, one run of distinct levels); using one level per band.");
                bandLevel = null;
            }
            int[] lob = new int[n];
            int lo = 0, hi = 0;
            for (int b = 0; b < n; b++)
            {
                lob[b] = bandLevel == null ? b : bandLevel[b];
                lo = Mathf.Min(lo, lob[b]);
                hi = Mathf.Max(hi, lob[b]);
            }
            int[] bol = new int[hi - lo + 1];
            for (int b = 0; b < n; b++)
            {
                bol[lob[b] - lo] = b;
            }
            minLevel = lo;
            maxLevel = hi;
            bandOfLevel = bol;
            levelOfBand = lob;
        }

        // Assigns the band level table (null = identity); false (and nothing changed) when it does not fit the bands.
        public bool SetBandLevels(List<int> table)
        {
            if (table != null && !ValidTable(table, Mathf.Max(1, floors)))
            {
                return false;
            }
            bandLevel = table == null ? null : new List<int>(table);
            levelOfBand = null;
            ProjectZ.Invalidate();
            return true;
        }

        // A band level table for n bands: n entries, band 0 = level 0, distinct, one unbroken run of levels.
        public static bool ValidTable(List<int> t, int n)
        {
            if (t == null || t.Count != n || n < 1 || t[0] != 0)
            {
                return false;
            }
            int lo = 0, hi = 0;
            HashSet<int> seen = new HashSet<int>();
            foreach (int L in t)
            {
                if (!seen.Add(L))
                {
                    return false;
                }
                lo = Mathf.Min(lo, L);
                hi = Mathf.Max(hi, L);
            }
            return hi - lo + 1 == n;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref groundZ, "groundZ", 0);
            Scribe_Values.Look(ref stride, "stride", 0);
            Scribe_Values.Look(ref floors, "floors", 1);
            Scribe_Values.Look(ref layoutVersion, "layoutVersion", 1, forceSave: true);
            Scribe_Collections.Look(ref bandLevel, "bandLevel", LookMode.Value);
            DataExposeUtility.LookByteArray(ref elevation, "elevation");
            Scribe_Values.Look(ref tallMountains, "tallMountains", false);
            if (tallMountains)
            {
                Scribe_Collections.Look(ref reachedLevels, "reachedLevels", LookMode.Value);
            }
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (layoutVersion < 2)
                {
                    // version 1: one level per band, bottom up = the identity table
                    bandLevel = null;
                    layoutVersion = 2;
                }
                levelOfBand = null;
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
                // ship decks over cells that lost their substructure fall (ShipDeck)
                ShipDeck.CheckAllLater(map);
                // The void rows are a virtual wall (VoidRule): light blocked, no gas left there (older saves).
                VoidRule.Apply(map);
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

        // Floors for a map generated now: the current game's, fixed when its world was created (WorldFloors.cs).
        public static int FloorsForNewMaps => WorldFloors.ForThisGame;

        // Tall mountains for a map generated now (the current game's, WorldFloors).
        public static bool TallMountainsForNewMaps => WorldFloors.TallMountainsThisGame;

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

        // Storey of a cell (its level, Levels.LevelOf): 0 = ground, k = floor k; -1 = a void gap row (no floor there).
        // (-1 stands for "void" only while no level is below the ground; code for negative levels uses Levels.LevelOf.)
        public static int FloorOf(IntVec3 c, Map map)
        {
            AnnexMapComponent a = Annex(map);
            if (a == null || c.z < a.groundZ)
            {
                return 0;
            }
            int b = a.BandOfRow(c.z);
            return b < 0 ? -1 : a.LevelOfBand(b);
        }

        // A void gap row cell (between two bands, or above the top band). Never on a map without floors.
        public static bool IsVoid(IntVec3 c, Map map)
        {
            AnnexMapComponent a = Annex(map);
            return a != null && a.BandOfRow(c.z) < 0;
        }

        // The band a cell is in (Levels.BandOfCell): 0 for the ground, -1 for void rows.
        public static int Band(IntVec3 c, Map map) => Levels.BandOfCell(c, map);

        // Two cells in the same band (always true on a map without floors); a void cell is in no band.
        public static bool SameBand(IntVec3 a, IntVec3 b, Map map)
        {
            AnnexMapComponent an = Annex(map);
            if (an == null || a.z < an.groundZ && b.z < an.groundZ)
            {
                return true;
            }
            int ka = an.BandOfRow(a.z);
            return ka >= 0 && ka == an.BandOfRow(b.z);
        }

        // The ground cell under (or at) c; Invalid for a void gap cell (Levels.Ground).
        public static IntVec3 GroundCellOf(IntVec3 c, Map map) => Levels.Ground(c, map);

        // Ground cell g lifted to floor (level) k (k = 0 returns g). Invalid when the map has no such level (Levels.Cell).
        public static IntVec3 CellOnFloor(IntVec3 g, int k, Map map) => Levels.Cell(g, k, map);

        // A cell of an upper band with a floor on it (any terrain but open air).
        public static bool HasFloor(IntVec3 c, Map map)
        {
            if (!c.IsValid)
            {
                return false;
            }
            AnnexMapComponent a = Annex(map);
            return a != null && c.z >= a.groundZ && a.BandOfRow(c.z) >= 1 && map.terrainGrid.TerrainAt(c) != PZDefOf.PZ_OpenAir;
        }

        // The top of the column over c (Column.Top).
        public static IntVec3 ColumnTop(IntVec3 c, Map map) => Column.Top(c, map);

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
            int b = a.BandOfRowLoose(Mathf.FloorToInt(p.z));
            return new Vector3(p.x, a.LevelOfBand(b) * Combat.Heights.Storey, p.z - b * a.stride);
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
