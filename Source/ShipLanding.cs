using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ProjectZ
{
    // Multi-level gravships, phase P4 (isorim-pz-multilevel-gravship.md, v2.1 F, user decision 6; the 3D hull:
    // isorim-pz-hull3d-design.md, G3): landing a ship with upper decks. Its data keys carry the floor in y (ShipLocal);
    // the landing marker has the ship's shadow, the ground cell of every key (drawn and checked by vanilla, cell by cell,
    // and by ProjectZ's ground-only rule), the thruster zones of upper thrusters are drawn on it. So the ground under an
    // overhang or inside a hollow hull must be a spot the game would let a ship cell land on, though nothing lands there.
    // The column over every shadow cell must be empty up to the ship's height: on each floor up to it open air with
    // nothing on it and no roof, above it no floor and no building; the map needs more floors than the ship has. Under a
    // deck with no ship cell on the ground, nothing too tall for the storey may stand (HeightRule); trees there go as the
    // game's landing takes those round the ship (its own DestroyTreesAroundSubstructure).
    // Refused cells show red on the marker, with a short reason.
    public static class ShipLanding
    {
        private static readonly System.WeakReference<Gravship> heightOf = new System.WeakReference<Gravship>(null);
        private static int heightKept;

        // The ship's top floor (0: a one-storey ship): the highest floor it brings a deck to, or a roof (the roof over
        // open cells above the ship flies with it, ShipRoofs: also over a ship with no deck up there, parked in a tall
        // hall). The one place for the ship's height: the floors the landing map needs and the column that must be empty
        // both read it. Kept per ship (what a ship carries does not change in flight).
        public static int Height(Gravship g)
        {
            if (g == null)
            {
                return 0;
            }
            if (heightOf.TryGetTarget(out Gravship kept) && ReferenceEquals(kept, g))
            {
                return heightKept;
            }
            int h = ShipRoofs.TopFloor(g);
            foreach (IntVec3 k in g.Foundations.Keys)
            {
                if (k.y > h)
                {
                    h = k.y;
                }
            }
            heightOf.SetTarget(g);
            heightKept = h;
            return h;
        }

        // Can the ship (in its current rotation) land with its root at root? Failing ground cells go into bad.
        public static AcceptanceReport Check(Gravship g, IntVec3 root, Map map, ICollection<IntVec3> bad)
        {
            int h = Height(g);
            if (h == 0 || map == null)
            {
                return AcceptanceReport.WasAccepted;
            }
            AnnexMapComponent an = ProjectZ.Annex(map);
            AcceptanceReport result = AcceptanceReport.WasAccepted;
            Dictionary<IntVec3, TerrainDef> keys = g.Foundations;
            bool Fits(IntVec3 flat, int lo)
            {
                IntVec3 c = new IntVec3(root.x + flat.x, 0, root.z + flat.z);
                if (!c.InBounds(map))
                {
                    return true;   // (vanilla refuses it)
                }
                string why = an == null || h > an.MaxLevel ? "PZ_ShipNotEnoughFloors".Translate().ToString() : Column(c, h, map, an) ?? Under(c, lo, map);
                if (why == null)
                {
                    return true;
                }
                result = new AcceptanceReport(why);
                bad?.Add(c);
                return bad != null;
            }
            // the columns with a ship cell on the ground (as before the 3D hull, in the keys' order) ...
            foreach (IntVec3 key in keys.Keys)
            {
                if (key.y == 0 && !Fits(key, 0))
                {
                    return result;
                }
            }
            // ... then those without one (under an overhang, inside a hollow hull), each with its lowest deck
            foreach (KeyValuePair<IntVec3, int> kv in UnderDecks(g))
            {
                if (!Fits(kv.Key, kv.Value))
                {
                    return result;
                }
            }
            return result;
        }

        private static readonly System.WeakReference<Gravship> underOf = new System.WeakReference<Gravship>(null);
        private static Rot4 underRot;
        private static readonly Dictionary<IntVec3, int> under = new Dictionary<IntVec3, int>();

        // The ship's shadow cells with no ship cell on the ground (under an overhang, inside a hollow hull), as ground
        // offsets from the ship's root in its current rotation, each with the lowest floor that has a deck over it
        // (ShipLocal.NoGround). Kept per ship and rotation: the move designator asks every frame. Read only.
        public static Dictionary<IntVec3, int> UnderDecks(Gravship g)
        {
            if (!underOf.TryGetTarget(out Gravship kept) || !ReferenceEquals(kept, g) || underRot != g.Rotation)
            {
                Dictionary<IntVec3, TerrainDef> keys = g.Foundations;
                ShipLocal.NoGround(keys.Keys, keys.ContainsKey, under);
                underOf.SetTarget(g);
                underRot = g.Rotation;
            }
            return under;
        }

        // Why ground cell c, under a deck landing on floor lo with no ship cell on the ground, can't have that deck over
        // it, or null (lo 0: a ship cell lands on c itself): something too tall for the storey when the deck lands on the
        // next floor (HeightRule, as for a deck laid there; on higher floors the column check keeps the air under it empty).
        private static string Under(IntVec3 c, int lo, Map map)
        {
            if (lo == 1)
            {
                Thing tall = HeightRule.TallThingAt(c, map);
                if (tall != null)
                {
                    return "GravshipBlockedBy".Translate(tall);
                }
            }
            return null;
        }

        // Why the column over ground cell c can't take a ship h floors high, or null.
        private static string Column(IntVec3 c, int h, Map map, AnnexMapComponent an)
        {
            if (c.z >= an.groundZ)
            {
                return null;   // (not a ground cell: the ground-only rule refuses it)
            }
            TerrainGrid tg = map.terrainGrid;
            for (int k = 1; k <= an.MaxLevel; k++)
            {
                IntVec3 u = ProjectZ.CellOnFloor(c, k, map);
                if (u.IsValid && (tg.FoundationAt(u) != null || tg.TerrainAt(u) != PZDefOf.PZ_OpenAir))
                {
                    return "PZ_ShipFloorAbove".Translate();
                }
            }
            for (int k = 1; k <= an.MaxLevel; k++)
            {
                IntVec3 u = ProjectZ.CellOnFloor(c, k, map);
                if (!u.IsValid)
                {
                    continue;
                }
                RoofDef roof = map.roofGrid.RoofAt(u);
                if (roof != null && (k <= h || roof.isThickRoof))
                {
                    return "GravshipBlockedByRoof".Translate();
                }
                List<Thing> list = map.thingGrid.ThingsListAtFast(u);
                for (int i = 0; i < list.Count; i++)
                {
                    Thing t = list[i];
                    if (t is Mote || t.def.category == ThingCategory.Ethereal && !(t is Blueprint) && !(t is Frame))
                    {
                        continue;
                    }
                    if (k <= h || t is Building || t is Blueprint || t is Frame)
                    {
                        return "GravshipBlockedBy".Translate(t);
                    }
                }
            }
            return null;
        }
    }

    // The marker's cells: the ship's shadow (the ground keys, then the ground cells under decks that have none: an
    // overhang, a deck over a hollow hull); upper thrusters' zones drawn on the ground.
    [HarmonyPatch(typeof(GravshipLandingMarker), nameof(GravshipLandingMarker.CacheCells))]
    public static class CacheCells_ShipLanding_Patch
    {
        private static readonly AccessTools.FieldRef<GravshipLandingMarker, List<IntVec3>> cellsRef = AccessTools.FieldRefAccess<GravshipLandingMarker, List<IntVec3>>("gravshipCells");
        private static readonly AccessTools.FieldRef<GravshipLandingMarker, List<IntVec3>> thrustersRef = AccessTools.FieldRefAccess<GravshipLandingMarker, List<IntVec3>>("thrusterCells");

        public static void Postfix(GravshipLandingMarker __instance)
        {
            List<IntVec3> cells = cellsRef(__instance);
            cells.RemoveAll(c => c.y != 0);
            if (__instance.gravship != null)
            {
                cells.AddRange(ShipLanding.UnderDecks(__instance.gravship).Keys);
            }
            List<IntVec3> zone = thrustersRef(__instance);
            for (int i = 0; i < zone.Count; i++)
            {
                zone[i] = ShipLocal.Flat(zone[i]);
            }
        }
    }

    // The landing starts: the game takes the trees (and other big plants: its own test) off the cells round the ship, and
    // clears the ship's own cells when it lays them. Under a deck with no ship cell on the ground a tree would stand
    // through the deck and nothing is laid there to clear it. So the game's own routine is asked once more, for the cells
    // round every shadow cell, with the ship's ground cells left out: that is every cell under such a deck (each has a
    // shadow cell next to it) plus the first ring round the shadow, which the game's own call clears anyway. Only those
    // plants go; whatever else is under the deck stays as it is.
    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.InitiateLanding))]
    public static class InitiateLanding_ShipLanding_Patch
    {
        public static void Prefix(Gravship gravship, Map map, IntVec3 landingPos)
        {
            if (gravship == null || map == null || ProjectZ.Annex(map) == null || ShipLanding.UnderDecks(gravship).Count == 0)
            {
                return;
            }
            HashSet<IntVec3> shadow = new HashSet<IntVec3>(), ground = new HashSet<IntVec3>();
            foreach (IntVec3 key in gravship.Foundations.Keys)
            {
                if (key.y == 0)
                {
                    ground.Add(landingPos + key);
                }
            }
            shadow.UnionWith(ground);
            foreach (IntVec3 flat in ShipLanding.UnderDecks(gravship).Keys)
            {
                shadow.Add(landingPos + flat);
            }
            WorldComponent_GravshipController.DestroyTreesAroundSubstructure(map, ground, 0, shadow);
        }
    }

    // The column check, after vanilla's cell checks (the designator's placement, its red cells and its reason).
    [HarmonyPatch(typeof(Designator_MoveGravship), "ValidGravshipLocation")]
    public static class ValidGravshipLocation_ShipLanding_Patch
    {
        private static readonly Func<Designator_MoveGravship, IntVec3, IntVec3> adjusted =
            AccessTools.MethodDelegate<Func<Designator_MoveGravship, IntVec3, IntVec3>>(AccessTools.Method(typeof(Designator_MoveGravship), "GetSizeRotAdjustedCell"));
        private static readonly HashSet<IntVec3> bad = new HashSet<IntVec3>();

        public static void Postfix(Designator_MoveGravship __instance, IntVec3 loc, List<IntVec3> validCells, List<IntVec3> invalidCells, ref AcceptanceReport __result)
        {
            bool lists = validCells != null || invalidCells != null;
            Gravship g = __instance.marker?.gravship;
            Map map = Find.CurrentMap;
            if (g == null || map == null || ShipLanding.Height(g) == 0)
            {
                return;
            }
            IntVec3 root = PrefabUtility.GetRoot(adjusted(__instance, loc), g.Bounds.Size, __instance.marker.GravshipRotation);
            bad.Clear();
            AcceptanceReport r = ShipLanding.Check(g, root, map, lists ? bad : null);
            if (r.Accepted)
            {
                return;
            }
            // (a floor above is the reason that counts: vanilla's "blocked by roof" there is the atrium roof under it, Atrium.cs)
            if (__result.Accepted || r.Reason == "PZ_ShipFloorAbove".Translate())
            {
                __result = r;
            }
            if (lists && bad.Count > 0)
            {
                validCells?.RemoveAll(c => bad.Contains(c));
                if (invalidCells != null)
                {
                    bad.ExceptWith(invalidCells);
                    invalidCells.AddRange(bad);
                }
            }
            bad.Clear();
        }
    }
}
