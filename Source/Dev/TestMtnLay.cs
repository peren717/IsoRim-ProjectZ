using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace ProjectZ.Dev
{
    // Mountain fixtures laid, not searched for (as talltower makes its spot): a fixture that needs a mountain of a given
    // shape (a tunnel face whose tops are at level 2, a plateau under a higher block) clears a rect of open ground on every
    // level and raises its columns by the generation's own rules (Mountains.RaiseColumn: the ground under each column
    // given vanilla's thick rock roof of a mountain as Generate finds it, then the rock-roof rule). The fog follows the
    // generation too: on the levels the rim rule of the whole band (Mountains.BandFog), on the ground vanilla's flood from
    // open ground (FloodFillerFog). The same fixture on every map and every seed.
    public static partial class TestRunner
    {
        // A laid spot keeps this far from the map's edges (as the site searches it replaces: edge cells are where raiders
        // and visitors come in, and where the edge's fog and reach rules differ).
        private const int MlEdgeMargin = 6;

        // The fog of the levels is set on the laid rect and this many cells round it: the rim rule looks at a cell's 8
        // neighbours (a top beside the sky) and a face beside such a rim cell, so laid columns change the fog up to 2 out.
        private const int MlFogRing = 2;

        // The nearest spot to the given point within the game's whole radial pattern.
        private static float MlSearchRadius => GenRadial.MaxRadialPatternRadius - 1f;

        // Natural ground cells the roof print compares a laid mountain with (info only).
        private const int MlRoofSample = 2000;

        // The plain natural rock the laid mountains are made of: Generate's own rock for a column with no ground rock to
        // take it from (Mountains.FallbackRock: the world's first plain rock type here). Generate gives a column the type of
        // the nearest rock on the ground; a laid spot is open ground, so the fallback.
        private static ThingDef MlRock(Map map) => Mountains.FallbackRock(map);

        // The nearest rect of the given size to 'near' on the ground rows (MlEdgeMargin from the edges) that is open ground:
        // nothing indestructible in its column on any level, no natural rock, no roof, no colonist; null if none.
        private static CellRect? MlSpot(Map map, IntVec3 near, int width, int depth)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int cap = a?.MaxLevel ?? 0;
            int gz = ProjectZ.GroundZ(map);
            List<Pawn> ours = Colonists(map);
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, MlSearchRadius, true))
            {
                CellRect rect = new CellRect(c.x - width / 2, c.z - depth / 2, width, depth);
                if (rect.minX < MlEdgeMargin || rect.minZ < MlEdgeMargin || rect.maxX >= map.Size.x - MlEdgeMargin || rect.maxZ >= gz - MlEdgeMargin)
                {
                    continue;
                }
                bool ok = rect.Cells.All(g => !g.Roofed(map) && !(g.GetEdifice(map) is Mineable) && g.GetTerrain(map).passability != Traversability.Impassable
                    && Enumerable.Range(0, cap + 1).All(L =>
                    {
                        IntVec3 u = L == 0 ? g : Levels.Cell(g, L, map);
                        return !u.IsValid || u.GetThingList(map).All(t => t is Pawn ? !ours.Contains((Pawn)t) && L == 0 : t.def.destroyable);
                    }));
                if (ok)
                {
                    return rect;
                }
            }
            return null;
        }

        // The rect made open ground on every level: things (strangers too, ClearStrangers) gone, the levels open air without
        // roofs, the ground soil where it was not walkable, no roof, no fog.
        private static void MlClear(Map map, CellRect rect)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int cap = a.MaxLevel;
            foreach (IntVec3 g in rect)
            {
                for (int L = 0; L <= cap; L++)
                {
                    IntVec3 u = L == 0 ? g : Levels.Cell(g, L, map);
                    if (!u.IsValid)
                    {
                        continue;
                    }
                    foreach (Thing t in u.GetThingList(map).Where(t => !(t is Pawn) && t.def.destroyable && !t.Destroyed).ToList())
                    {
                        t.Destroy();
                    }
                }
            }
            ClearStrangers(map, rect);
            foreach (IntVec3 g in rect)
            {
                Mountains.RaiseColumn(map, g, 0, null);   // (height 0: every level open air, no roofs)
                map.roofGrid.SetRoof(g, null);
                if (!Mountains.WalkableRaw(g, map))
                {
                    map.terrainGrid.SetTerrain(g, TerrainDefOf.Soil);
                }
                for (int L = 0; L <= cap; L++)
                {
                    IntVec3 u = L == 0 ? g : Levels.Cell(g, L, map);
                    if (u.IsValid)
                    {
                        map.fogGrid.Unfog(u);
                    }
                }
            }
        }

        // Columns raised in the cleared rect, height(g) each (0: left open): the ground rock under them as a vanilla mountain,
        // then RaiseColumn with the mountain's thick rock roof as the ground's (written once the levels stand, as Generate
        // finds it). Then the fog as generated: the rect's ground rock fogged and vanilla's flood from 'open' (a cell of open
        // ground next to the rect) unfogs what it reaches and the rock next to it; on the levels the rim rule over the whole
        // band, applied to the rect and MlFogRing round it.
        private static void MlRaise(Map map, CellRect rect, Func<IntVec3, int> height, IntVec3 open)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int cap = a.MaxLevel, sx = map.Size.x, gz = a.groundZ;
            ThingDef rock = MlRock(map);
            List<IntVec3> raised = rect.Cells.Where(g => height(g) >= 1).ToList();
            foreach (IntVec3 g in raised)
            {
                map.terrainGrid.SetTerrain(g, rock.building.naturalTerrain);
                GenSpawn.Spawn(ThingMaker.MakeThing(rock), g, map, WipeMode.Vanish);
                Mountains.RaiseColumn(map, g, Math.Min(height(g), cap), rock, RoofDefOf.RoofRockThick);
            }
            foreach (IntVec3 g in raised)
            {
                map.fogGrid.Refog(CellRect.SingleCell(g));
            }
            FloodFillerFog.FloodUnfog(open, map);
            byte[] h = new byte[sx * gz];
            for (int z = 0; z < gz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    h[z * sx + x] = (byte)Column.NaturalTop(new IntVec3(x, 0, z), map);
                }
            }
            CellRect ring = rect.ExpandedBy(MlFogRing).ClipInsideRect(new CellRect(0, 0, sx, gz));
            for (int L = 1; L <= cap; L++)
            {
                bool[] fog = Mountains.BandFog(h, L, sx, gz, out _, out _);
                foreach (IntVec3 g in ring)
                {
                    IntVec3 u = Levels.Cell(g, L, map);
                    if (!u.IsValid)
                    {
                        continue;
                    }
                    if (fog[g.z * sx + g.x])
                    {
                        map.fogGrid.Refog(CellRect.SingleCell(u));
                    }
                    else
                    {
                        map.fogGrid.Unfog(u);
                    }
                }
            }
            // (the ground roofs by height, and the same on the map's own mountain: a laid column must get what a generated one has)
            string Roofs(IEnumerable<IntVec3> cells) => string.Join(", ", cells.GroupBy(g => Column.NaturalTop(g, map)).OrderBy(x => x.Key)
                .Select(x => "height " + x.Key + ": " + string.Join("/", x.GroupBy(g => map.roofGrid.RoofAt(g)?.defName ?? "none").OrderBy(y => y.Key, StringComparer.Ordinal).Select(y => y.Key + " " + y.Count()))));
            List<IntVec3> natural = new List<IntVec3>();
            for (int z = 0; z < gz && natural.Count < MlRoofSample; z += 3)
            {
                for (int x = 0; x < sx && natural.Count < MlRoofSample; x += 3)
                {
                    IntVec3 g = new IntVec3(x, 0, z);
                    int top = Column.NaturalTop(g, map);
                    if (!rect.Contains(g) && top >= 1 && top <= MuAHeight && g.GetEdifice(map) is Mineable)
                    {
                        natural.Add(g);
                    }
                }
            }
            Info($"laid mountain {rect}: {raised.Count} columns of {rock.defName} (heights {string.Join(",", raised.Select(height).Distinct().OrderBy(x => x))}), open from {open}; ground roofs laid: {Roofs(raised)}; on the map's own mountain (every 3rd cell, at most {MlRoofSample}): {Roofs(natural)}");
        }
    }
}
