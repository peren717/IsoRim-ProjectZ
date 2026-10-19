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

        // Natural stone terrain: every natural rock's rough terrain, the rough-hewn terrain mining it leaves, and their smoothed
        // forms (vanilla's Granite_Rough, Granite_RoughHewn, Granite_Smooth, ...), plus terrains with NaturalFloorExtension.
        // Built once, when first asked.
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
                    if (d.building == null || !d.building.isNaturalRock)
                    {
                        continue;
                    }
                    // its rough floor, and what mining it leaves (rough-hewn), each with its smoothed form
                    foreach (TerrainDef nt in new[] { d.building.naturalTerrain, d.building.leaveTerrain })
                    {
                        if (nt != null)
                        {
                            s.Add(nt);
                            if (nt.smoothedTerrain != null)
                            {
                                s.Add(nt.smoothedTerrain);
                            }
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

        // The roof a floor kind puts on the cell under it (rock-top floors, isorim-pz-rocktop-floors-plan.md §3.1): a built
        // floor (slab or deck) the floor's own roof (PZ_FloorRoof, FloorRoof.cs); natural rock keeps a natural rock roof (the
        // one asked for, else the one there: vanilla's thick overhead mountain, or the thin roof at a mountain's edge) and
        // otherwise takes the floor roof too (the support roof, as under a slab); none for None. Support follows from it: a
        // floor roof or a thin rock roof needs the game's roof support and falls with its floor (SlabCollapse); a thick roof
        // never vanishes (vanilla: it drops rocks and stays), so the floor over it stays.
        public static RoofDef CeilingFor(FloorKind k, RoofDef asked = null, RoofDef cur = null)
        {
            switch (k)
            {
                case FloorKind.Slab:
                case FloorKind.ShipDeck:
                    return PZDefOf.PZ_FloorRoof;
                case FloorKind.NaturalRock:
                    if (asked != null && asked.isNatural)
                    {
                        return asked;
                    }
                    return cur != null && cur.isNatural ? cur : PZDefOf.PZ_FloorRoof;
                default:
                    return null;
            }
        }

        // The roof the floor at upper-band cell "above" puts on the cell under it (null: no floor there); asked and cur as
        // in CeilingFor (the roof being written there, the roof there now).
        public static RoofDef CeilingUnder(IntVec3 above, Map map, RoofDef asked = null, RoofDef cur = null) => CeilingFor(KindAt(above, map), asked, cur);

        // An unbuilt natural floor: an upper-band cell with a floor whose top terrain is natural stone (a mountain top as
        // generated, rough or smoothed, or a dug cell's floor). A player's floor laid on a top (carpet, tiles) is not one.
        public static bool UnbuiltNatural(IntVec3 c, Map map)
        {
            return ProjectZ.FloorOf(c, map) >= 1 && ProjectZ.HasFloor(c, map) && IsNatural(map.terrainGrid.TopTerrainAt(c));
        }

        // (Taking a floor away stays per kind where it is: slabs Slab.Remove (Slab.At is slab-only), decks as vanilla
        // substructure; natural rock has no way yet ("dig down", mountains phase 3).)
    }
}
