using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ.Dev
{
    // Test-side words for the 3D connected hull (issue 5, isorim-pz-hull3d-design.md), so the Dev sources compile and mean
    // the same against the stacked-column product (ShipColumn, master 96e4e61) and the 3D hull product (ShipLink): the
    // tests never name ShipColumn / ShipLink / ShipRoofs / ShipDeck.CheckAll directly.
    //   - The cell words (Open, Down, Up, OverShip, Hole) are read from game state only (terrain grid, the product's own
    //     "is this a gravship deck" test SlabSupport.Gravship, ShipStairs.Opening): they are the design's definitions
    //     ("open cell", "vertical link"), not a copy of a product rule the tests then check against itself.
    //   - Product members that exist on one side only are reached by reflection, here and nowhere else.
    internal static class HullCompat
    {
        private static readonly MethodInfo checkAll = AccessTools.Method(typeof(ShipDeck), "CheckAll", new[] { typeof(Map) });

        // The 3D hull product is loaded (its link helper exists).
        public static readonly bool NewHull = AccessTools.TypeByName("ProjectZ.ShipLink") != null;

        // Design "open cell": an upper-floor cell with no foundation and open air as top terrain.
        public static bool Open(IntVec3 c, Map map)
        {
            return c.IsValid && c.InBounds(map) && ProjectZ.FloorOf(c, map) >= 1 && map.terrainGrid.FoundationAt(c) == null && map.terrainGrid.TopTerrainAt(c) == PZDefOf.PZ_OpenAir;
        }

        // Design "ship floor" as the product asks it everywhere outside the ship set: a gravship deck foundation.
        public static bool ShipFloor(IntVec3 c, Map map) => SlabSupport.Gravship(c, map);

        // Design "vertical link": from c down (up) its column through open cells; the first cell that is not open.
        public static IntVec3 Down(IntVec3 c, Map map)
        {
            IntVec3 b = FloorRoof.Below(c, map);
            while (b.IsValid && Open(b, map))
            {
                b = FloorRoof.Below(b, map);
            }
            return b;
        }

        public static IntVec3 Up(IntVec3 c, Map map)
        {
            IntVec3 u = FloorRoof.Above(c, map);
            while (u.IsValid && Open(u, map))
            {
                u = FloorRoof.Above(u, map);
            }
            return u;
        }

        // An open cell whose link down is ship floor (a ship stairs opening, a hole in a deck, the air of a tall hall).
        public static bool OverShip(IntVec3 c, Map map) => Open(c, map) && ShipFloor(Down(c, map), map);

        // A hole in the ship under a kept deck (removal design Q6 (b)): an open cell that is no ship stairs opening, with
        // a ship deck linked above it and the ship linked below it.
        public static bool Hole(IntVec3 c, Map map)
        {
            return Open(c, map) && !ShipStairs.Opening(c, map) && ShipDeck.At(Up(c, map), map) && ShipFloor(Down(c, map), map);
        }

        // The stacked-column product's load-time deck check (ShipDeck.CheckAll): how many decks it would drop. The 3D hull
        // has no such check (a cut-off part simply is not ship): 0.
        public static int DeckCheckAll(Map map) => checkAll == null ? 0 : (int)checkAll.Invoke(null, new object[] { map });

        // ---- a ship taller than its destination's floors (mode shipfloors): the product members that round added, so the
        // same tests also run against the product before it (where they must fail).
        private static readonly MethodInfo heightBefore = AccessTools.Method(typeof(ShipLanding), "Height", new[] { typeof(Building_GravEngine) });
        private static readonly FieldInfo shipRaises = AccessTools.Field(typeof(Patches.AnnexGen), "ShipRaises");

        // The ship's height asked from the engine before the launch (-1: the product has no such function).
        public static int HeightBefore(Building_GravEngine e) => heightBefore == null ? -1 : (int)heightBefore.Invoke(null, new object[] { e });

        // Maps given more floors for an arriving gravship so far (-1: the product has no such rule).
        public static int ShipRaises => shipRaises == null ? -1 : (int)shipRaises.GetValue(null);
    }
}
