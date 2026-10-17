using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ProjectZ
{
    // Multi-level gravships, phase P4 (isorim-pz-multilevel-gravship.md, v2.1 F, user decision 6): landing a ship with
    // upper decks. Its data keys carry the floor in y (ShipLocal); the landing marker keeps the ground cells (the footprint
    // drawn and checked by vanilla, and ProjectZ's ground-only rule), the thruster zones of upper thrusters are drawn on it.
    // The column over every footprint cell must be empty up to the ship's height: on each floor up to it open air with
    // nothing on it and no roof, above it no floor and no building; the map needs more floors than the ship has.
    // Refused cells show red on the marker, with a short reason.
    public static class ShipLanding
    {
        // The ship's top floor (0: a one-storey ship).
        public static int Height(Gravship g)
        {
            int h = 0;
            if (g == null)
            {
                return 0;
            }
            foreach (IntVec3 k in g.Foundations.Keys)
            {
                if (k.y > h)
                {
                    h = k.y;
                }
            }
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
            foreach (IntVec3 key in g.Foundations.Keys)
            {
                if (key.y != 0)
                {
                    continue;
                }
                IntVec3 c = new IntVec3(root.x + key.x, 0, root.z + key.z);
                if (!c.InBounds(map))
                {
                    continue;   // (vanilla refuses it)
                }
                string why = an == null || h > an.MaxLevel ? "PZ_ShipNotEnoughFloors".Translate().ToString() : Column(c, h, map, an);
                if (why == null)
                {
                    continue;
                }
                result = new AcceptanceReport(why);
                if (bad == null)
                {
                    return result;
                }
                bad.Add(c);
            }
            return result;
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

    // The marker's cells: the footprint (ground keys); upper thrusters' zones drawn on the ground.
    [HarmonyPatch(typeof(GravshipLandingMarker), nameof(GravshipLandingMarker.CacheCells))]
    public static class CacheCells_ShipLanding_Patch
    {
        private static readonly AccessTools.FieldRef<GravshipLandingMarker, List<IntVec3>> cellsRef = AccessTools.FieldRefAccess<GravshipLandingMarker, List<IntVec3>>("gravshipCells");
        private static readonly AccessTools.FieldRef<GravshipLandingMarker, List<IntVec3>> thrustersRef = AccessTools.FieldRefAccess<GravshipLandingMarker, List<IntVec3>>("thrusterCells");

        public static void Postfix(GravshipLandingMarker __instance)
        {
            cellsRef(__instance).RemoveAll(c => c.y != 0);
            List<IntVec3> zone = thrustersRef(__instance);
            for (int i = 0; i < zone.Count; i++)
            {
                if (zone[i].y != 0)
                {
                    zone[i] = new IntVec3(zone[i].x, 0, zone[i].z);
                }
            }
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
            if (!__result.Accepted && !lists)
            {
                return;
            }
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
            if (__result.Accepted)
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
