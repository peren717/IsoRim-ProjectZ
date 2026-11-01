using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ.Patches
{
    // A bill's ingredient search radius in 3D (user 2026-10-31). The game measures it flat on the map's raw rows
    // (WorkGiver_DoBill.TryFindBestIngredientsHelper, the one search behind TryFindBestBillIngredients and
    // TryFindBestFixedIngredients), where the floor above is a whole band north: with any finite radius nothing on another
    // floor was ever taken, and "Unlimited" (999) still left out floors more than 999 rows north. Three flat checks:
    //   (a) every found thing: (t.Position - bench).LengthHorizontalSquared < radius^2;
    //   (b) haul sources (bookcases, outfit stands ...): position.InHorDistOf(bench, radius);
    //   (c) the region search's entry test: the region's closest cell within the radius (hand-written dx/dz).
    // On a map with floors (only there):
    //   - the prefix keeps the bill's radius r and passes the search 100000, so (a), (b) and (c) let everything through,
    //     and for r < 999 adds the radius to the thing test in 3D (Dist3D's rule: ground distance + |level diff| x
    //     StoreyCells; on one band the game's own squared compare, bit for bit). Haul-source contents go through the thing
    //     test too, measured at their holder. A thing held in a holder on another floor also needs the pawn to reach the
    //     holder (the region search only reaches what a walk reaches; haul sources are not found by it);
    //   - the transpiler wraps the stored entry test so the region search still stops at the 3D radius (Dist3D.RectDistSq,
    //     the region's extents moved onto the bench's band plus the storeys between); a region at a stairs end is let
    //     through even past the radius (the walk to a thing inside the radius on another floor goes over the stairs, which
    //     may stand a little farther off), its other neighbours measured as usual. If the wrap fails (FailSoft) the result
    //     is the same, only the search is not pruned;
    //   - the ring (BillUtility.TryDrawIngredientSearchRadiusOnMap: the bill dialog and the bills tab's mouse-over) gets
    //     the radius's cross-section on the other floors, r - |level diff| x StoreyCells (at least 1 cell), like the weapon
    //     range preview: in IsoRim's 3D view only the viewed floor, in the flat view every floor in reach.
    // The nearest-first choice among the found things is Dist3D's rows ("ingredients (no mix)/(mix)").
    // Test switch -projectznobillradius: the game's flat checks (the control).
    public static class BillRadius3D
    {
        public static bool Off;

        public const float Wide = 100000f;
        public const float Unlimited = 998.5f;   // the bill's 999 ("Unlimited") and anything near it

        // The search being run (set by the prefix, read by the transpiler's wrap of the entry test).
        public struct Ctx
        {
            public bool on;
            public Map map;
            public IntVec3 bench;
            public float r;
        }

        [ThreadStatic]
        internal static Ctx ctx;

        // (tests) searches made 3D, entry tests wrapped, regions refused by the 3D radius, things refused by it
        public static int Searches, Wrapped, RegionsCut, StairPasses, ThingsCut, ReachCut;

        internal static bool Active(Map map) => !Off && map != null && ProjectZ.Annex(map) != null;

        // Strictly within the bill's radius r of the bench (the game's own test on one band).
        public static bool Within(IntVec3 bench, IntVec3 c, Map map, float r)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || an.BandOfRowLoose(bench.z) == an.BandOfRowLoose(c.z))
            {
                return (float)(c - bench).LengthHorizontalSquared < r * r;
            }
            return Dist3D.Distance(bench, c, map) < r;
        }

        // The game's entry test plus the 3D radius (r < 999 only; else the game's test as it is).
        public static RegionEntryPredicate Wrap(RegionEntryPredicate inner)
        {
            Ctx c = ctx;
            if (!c.on || inner == null || c.r >= Unlimited)
            {
                return inner;
            }
            Wrapped++;
            Map map = c.map;
            IntVec3 bench = c.bench;
            float rSq = c.r * c.r;
            return (Region from, Region to) =>
            {
                if (!inner(from, to))
                {
                    return false;
                }
                CellRect e = to.extentsClose;
                if (Dist3D.RectDistSq(ref e, bench, map) <= rSq)
                {
                    return true;
                }
                // a stairs end: the way to another floor, let through (its other regions are measured as usual), so a
                // thing inside the radius above or below is found when the stairs themselves are a little farther off
                List<RouteGraph.Partner> ps = StairRegions.Partners(to, map, out _);
                if (ps != null && ps.Count > 0)
                {
                    StairPasses++;
                    return true;
                }
                RegionsCut++;
                return false;
            };
        }
    }

    [HarmonyPatch(typeof(WorkGiver_DoBill), "TryFindBestIngredientsHelper")]
    public static class BillRadius3D_Search_Patch
    {
        public static void Prefix(ref Predicate<Thing> thingValidator, Pawn pawn, Thing billGiver, ref float searchRadius, out BillRadius3D.Ctx __state)
        {
            __state = BillRadius3D.ctx;
            BillRadius3D.ctx = default;
            Map map = pawn?.Map;
            if (billGiver == null || thingValidator == null || !BillRadius3D.Active(map) || billGiver.MapHeld != map)
            {
                return;
            }
            float r = searchRadius;
            IntVec3 bench = billGiver.Position;
            BillRadius3D.ctx = new BillRadius3D.Ctx { on = true, map = map, bench = bench, r = r };
            BillRadius3D.Searches++;
            searchRadius = BillRadius3D.Wide;
            Predicate<Thing> usable = thingValidator;
            bool finite = r < BillRadius3D.Unlimited;
            int benchLevel = Levels.LevelOf(bench, map);
            thingValidator = t =>
            {
                if (!usable(t))
                {
                    return false;
                }
                IntVec3 at = t.PositionHeld;
                if (finite && !BillRadius3D.Within(bench, at, map, r))
                {
                    BillRadius3D.ThingsCut++;
                    return false;
                }
                // held in a holder on another floor (a haul source): the pawn must get there
                if (!t.Spawned && Levels.LevelOf(at, map) != benchLevel)
                {
                    Thing holder = t.SpawnedParentOrMe;
                    if (holder == null || !holder.Spawned || !pawn.CanReach(holder, PathEndMode.ClosestTouch, Danger.Deadly))
                    {
                        BillRadius3D.ReachCut++;
                        return false;
                    }
                }
                return true;
            };
        }

        public static void Finalizer(BillRadius3D.Ctx __state)
        {
            BillRadius3D.ctx = __state;
        }
    }

    // The region search's entry test, wrapped where the method stores it (a field of its closure, or a local).
    [HarmonyPatch(typeof(WorkGiver_DoBill), "TryFindBestIngredientsHelper")]
    public static class BillRadius3D_Prune_Patch
    {
        public static int Stores;   // (tests) entry-test stores wrapped in the IL

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            return FailSoft.Transpile(nameof(BillRadius3D_Prune_Patch), original, instructions, Edit);
        }

        private static string Edit(List<CodeInstruction> code)
        {
            MethodInfo wrap = AccessTools.Method(typeof(BillRadius3D), nameof(BillRadius3D.Wrap));
            int n = 0;
            for (int i = 0; i < code.Count; i++)
            {
                CodeInstruction ci = code[i];
                bool store = ci.opcode == OpCodes.Stfld && ci.operand is FieldInfo f && f.FieldType == typeof(RegionEntryPredicate)
                    || ci.IsStloc() && ci.operand is LocalBuilder lb && lb.LocalType == typeof(RegionEntryPredicate);
                if (!store)
                {
                    continue;
                }
                code.Insert(i, new CodeInstruction(OpCodes.Call, wrap).WithLabels(ci.ExtractLabels()));
                i++;
                n++;
            }
            Stores = n;
            return n > 0 ? null : "no store of the region entry test found";
        }
    }

    // The ring on the other floors (see BillRadius3D).
    [HarmonyPatch(typeof(BillUtility), nameof(BillUtility.TryDrawIngredientSearchRadiusOnMap))]
    public static class BillRadius3D_Ring_Patch
    {
        public static int Drawn;   // (tests) rings added

        public static void Postfix(Bill bill, IntVec3 center)
        {
            Map map = Find.CurrentMap;
            if (bill == null || !BillRadius3D.Active(map) || bill.ingredientSearchRadius >= GenRadial.MaxRadialPatternRadius || !center.InBounds(map))
            {
                return;
            }
            float r = bill.ingredientSearchRadius;
            int ks = Levels.LevelOf(center, map);
            IntVec3 g = ProjectZ.GroundCellOf(center, map);
            if (ks == Levels.None || !g.IsValid)
            {
                return;
            }
            if (IsoRimBridge.View3DOn)
            {
                Ring(g, ks, FloorView.Current, r, map);
                return;
            }
            for (int k = Levels.Min(map); k <= Levels.Max(map); k++)
            {
                Ring(g, ks, k, r, map);
            }
        }

        private static void Ring(IntVec3 g, int ks, int k, float r, Map map)
        {
            if (k == ks)
            {
                return;   // the game's own ring
            }
            float rk = r - Math.Abs(k - ks) * Dist3D.StoreyCells;
            if (rk < 1f)
            {
                return;
            }
            IntVec3 c = ProjectZ.CellOnFloor(g, k, map);
            if (c.IsValid && c.InBounds(map))
            {
                GenDraw.DrawRadiusRing(c, rk);
                Drawn++;
            }
        }
    }
}
