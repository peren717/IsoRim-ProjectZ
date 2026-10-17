using System.Collections.Generic;
using RimWorld;
using Verse;

namespace ProjectZ
{
    // Mountains foundation F3 (isorim-pz-mountains-design.md §3): what kind of floor an upper-band cell has, and the
    // roof that kind puts under it.
    public enum FloorKind
    {
        None,        // open air (or not an upper-band cell)
        Slab,        // a built upper floor (any SlabDefs slab, bare or under a floor laid on it; anything else built there)
        ShipDeck,    // the gravship upper deck (ShipDeck, Odyssey)
        NaturalRock, // natural stone terrain in a band (rough or smoothed), the top of a mountain (phase 1)
    }

    // Extension for a modded terrain that counts as natural rock floor in a band (like vanilla rough and smooth stone).
    public class NaturalFloorExtension : DefModExtension
    {
    }

    public static class Floors
    {
        private static HashSet<TerrainDef> natural;

        // Natural stone terrain: every natural rock's rough terrain and its smoothed one (vanilla's Granite_Rough,
        // Granite_Smooth, ...), plus terrains with NaturalFloorExtension. Built once, when first asked.
        public static bool IsNatural(TerrainDef t)
        {
            if (t == null)
            {
                return false;
            }
            if (natural == null)
            {
                HashSet<TerrainDef> s = new HashSet<TerrainDef>();
                foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    TerrainDef rough = d.building != null && d.building.isNaturalRock ? d.building.naturalTerrain : null;
                    if (rough != null)
                    {
                        s.Add(rough);
                        if (rough.smoothedTerrain != null)
                        {
                            s.Add(rough.smoothedTerrain);
                        }
                    }
                }
                foreach (TerrainDef td in DefDatabase<TerrainDef>.AllDefsListForReading)
                {
                    if (td.HasModExtension<NaturalFloorExtension>())
                    {
                        s.Add(td);
                    }
                }
                natural = s;
            }
            return natural.Contains(t);
        }

        // The floor kind at upper-band cell c (None on the ground, a void row or open air).
        public static FloorKind KindAt(IntVec3 c, Map map)
        {
            if (!ProjectZ.HasFloor(c, map))
            {
                return FloorKind.None;
            }
            if (ShipDeck.At(c, map))
            {
                return FloorKind.ShipDeck;
            }
            if (Slab.At(c, map))
            {
                return FloorKind.Slab;
            }
            TerrainGrid tg = map.terrainGrid;
            if (IsNatural(tg.TopTerrainAt(c)) || IsNatural(tg.UnderTerrainAt(c)))
            {
                return FloorKind.NaturalRock;
            }
            return FloorKind.Slab;   // any other terrain up there stands for a built floor (as it always did)
        }

        // The roof a floor kind puts on the cell under it: a built floor (slab or deck) the floor's own roof
        // (PZ_FloorRoof, FloorRoof.cs), natural rock the thick rock roof (vanilla's "overhead mountain"); none for None.
        // Support follows from it: a floor roof needs the game's roof support (and falls with it, SlabCollapse); a thick
        // roof never vanishes (vanilla: it drops rocks and stays), so natural rock needs none.
        public static RoofDef CeilingFor(FloorKind k)
        {
            switch (k)
            {
                case FloorKind.Slab:
                case FloorKind.ShipDeck:
                    return PZDefOf.PZ_FloorRoof;
                case FloorKind.NaturalRock:
                    return RoofDefOf.RoofRockThick;
                default:
                    return null;
            }
        }

        // The roof the floor at upper-band cell "above" puts on the cell under it (null: no floor there).
        public static RoofDef CeilingUnder(IntVec3 above, Map map) => CeilingFor(KindAt(above, map));

        // (Taking a floor away stays per kind where it is: slabs Slab.Remove (Slab.At is slab-only), decks as vanilla
        // substructure; natural rock has no way yet ("dig down", mountains phase 3).)
    }
}
