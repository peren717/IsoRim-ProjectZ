using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // Multi-level gravships (isorim-pz-multilevel-gravship.md, v2.0 - v2.4; the 3D hull: isorim-pz-hull3d-design.md): the
    // ship upper deck PZ_ShipDeck. It IS vanilla gravship substructure (a copy of the Substructure def with its tag and
    // affordances), laid as the foundation over open air on an upper floor.
    // Because of the tag, vanilla treats deck cells as substructure by itself (ship parts placed on them, the
    // substructure overlay, "remove foundation"); ordinary slabs and stairs over it are refused by SlabSupport.BuildBlocked.
    //   - Placement: PlaceWorker_ShipDeck (Check). A deck must be linked to the gravship's deck (ShipLink.Would): next to
    //     substructure or a deck on its floor, or straight above or below one through open cells; built or planned. So a
    //     deck may overhang the ship or span a hollow hull, a loose island is refused. A planned deck waits until it is
    //     linked to a built one (CanConstruct): decks are built outward from the ship.
    //   - Support (user decision 2026-10-17): the game's roof rules, as for any upper floor: the roof under a deck
    //     (PZ_FloorRoof) needs a wall, column or stairs within range below, at placement (planned decks next to it count)
    //     and when built; without one it collapses with the deck (SlabCollapse). Nothing else makes a deck fall.
    //   - Removal: the vanilla remove-foundation tool. A deck or the substructure under one may go (removal design Q6 (b),
    //     user 2026-11-01; hull3d G4): what is over it stays, resting on its roof. Whatever that cuts off from the engine
    //     is no longer in the ship set (ShipSet) and stays behind at a take-off, as vanilla's disconnected substructure.
    //     What stands on a removed deck falls to the floor below (Slab.MoveOff, the gravity rule; the worker stands on a
    //     next cell, WorkSpot), what was laid on it goes. Automatic work takes upper decks up in the slabs' peel order
    //     (SlabPeel, user 2026-11-14: farthest from the stairs first, so none is cut off); a forced order skips it.
    // Ship set, footprint and launch: ShipSet.cs. The engine and extender footprint is a 3D sphere.
    // Without Odyssey the def is not loaded (MayRequire) and nothing here does anything.

    // How the ship hangs together across floors (hull3d rule 1): the one place for the link rule. Only floors count,
    // walls and roofs play no part.
    //   - Gravship floor: ground substructure or a ship deck (Floor).
    //   - On one floor: the 4 neighbours (as vanilla's substructure).
    //   - Between floors: the vertical link (Up, Down). From a cell, its column is walked through open cells (the opening
    //     of ship stairs, a hole, an atrium, plain open air); the first cell that is not open is linked if it is gravship
    //     floor. So a deck over a tall hall or over a hole is linked to the floor under it, and a deck with another floor
    //     (an ordinary slab) between it and the ship is not.
    // Asked by: the ship set (ShipSet.Fill, with the game's own "is substructure" test), deck placement and construction
    // (Would), the ship stairs' exit, the roofs that fly (ShipRoofs), the ordinary-slab and ordinary-stairs refusal over a
    // ship and IsoRim's ship roofs (OverShip).
    public static class ShipLink
    {
        // An open cell: an upper-floor cell with no foundation and open air as its top terrain.
        public static bool Open(IntVec3 c, Map map)
        {
            return c.IsValid && c.InBounds(map) && ProjectZ.FloorOf(c, map) >= 1
                && map.terrainGrid.FoundationAt(c) == null && map.terrainGrid.TopTerrainAt(c) == PZDefOf.PZ_OpenAir;
        }

        // Gravship floor at c: ground substructure or a ship deck; with planned also a blueprint or frame of one.
        public static bool Floor(IntVec3 c, Map map, bool planned = false)
        {
            return SlabSupport.Gravship(c, map) || planned && ShipDeck.Planned(c, map);
        }

        // The vertical link up (or down) from c: the first cell over (under) it that is not open, when it is gravship
        // floor; else Invalid. With planned, a deck planned on an open cell on the way counts as that floor.
        public static IntVec3 Up(IntVec3 c, Map map, bool planned = false) => Walk(c, map, true, planned, null);

        public static IntVec3 Down(IntVec3 c, Map map, bool planned = false) => Walk(c, map, false, planned, null);

        // The same with the caller's own "is ship floor" test (the ship set: the game's tag and the engine's footprint).
        public static IntVec3 Up(IntVec3 c, Map map, System.Predicate<IntVec3> isShip) => Walk(c, map, true, false, isShip);

        public static IntVec3 Down(IntVec3 c, Map map, System.Predicate<IntVec3> isShip) => Walk(c, map, false, false, isShip);

        // The open cells straight over c, bottom-up into run (cleared first), up to the next floor or the top; returns the
        // cell that ends the run (the first that is not open), Invalid at the top. The roofs that fly ask this (ShipRoofs).
        public static IntVec3 OpenRunUp(IntVec3 c, Map map, List<IntVec3> run)
        {
            run.Clear();
            return End(c, map, true, false, run);
        }

        private static IntVec3 Walk(IntVec3 c, Map map, bool up, bool planned, System.Predicate<IntVec3> isShip)
        {
            IntVec3 x = End(c, map, up, planned, null);
            if (!x.IsValid)
            {
                return IntVec3.Invalid;
            }
            if (planned && ShipDeck.Planned(x, map))
            {
                return x;
            }
            return (isShip != null ? isShip(x) : SlabSupport.Gravship(x, map)) ? x : IntVec3.Invalid;
        }

        // The one column walk: from c up (or down) through open cells to the cell that ends the run: the first that is not
        // open or, with planned, the first with a deck planned on it. Invalid: the run goes on to the top (or c is no
        // cell). The open cells passed go into run.
        private static IntVec3 End(IntVec3 c, Map map, bool up, bool planned, List<IntVec3> run)
        {
            if (!c.IsValid || !c.InBounds(map))
            {
                return IntVec3.Invalid;
            }
            for (IntVec3 x = up ? FloorRoof.Above(c, map) : FloorRoof.Below(c, map); x.IsValid && x.InBounds(map); x = up ? FloorRoof.Above(x, map) : FloorRoof.Below(x, map))
            {
                if (planned && ShipDeck.Planned(x, map) || !Open(x, map))
                {
                    return x;
                }
                run?.Add(x);
            }
            return IntVec3.Invalid;
        }

        // A deck at upper-floor cell c would be linked to the gravship's deck: a vertical link, or gravship floor next to
        // it on its floor. With planned (placement), blueprints and frames count, and so do the cells of the drag in
        // progress that are linked through the drag (SlabDrag.Linked: one drag lays a whole overhang).
        public static bool Would(IntVec3 c, Map map, bool planned)
        {
            if (planned && SlabDrag.Sorting)
            {
                return true;   // (the drag's cells are being sorted: the other rules decide, as for support)
            }
            if (Touches(c, map, planned))
            {
                return true;
            }
            return planned && SlabDrag.Linked(c, map);
        }

        // c itself touches gravship floor: straight above or below it, or next to it on its floor.
        public static bool Touches(IntVec3 c, Map map, bool planned)
        {
            if (!c.IsValid || !c.InBounds(map))
            {
                return false;
            }
            if (Up(c, map, planned).IsValid || Down(c, map, planned).IsValid)
            {
                return true;
            }
            for (int i = 0; i < 4; i++)
            {
                IntVec3 n = c + GenAdj.CardinalDirections[i];
                if (n.InBounds(map) && ProjectZ.SameBand(n, c, map) && Floor(n, map, planned))
                {
                    return true;
                }
            }
            return false;
        }

        // An open cell over the ship: gravship floor straight below it through open cells (the opening of ship stairs, a
        // hole in a deck, the air of a tall hall). No ship cell itself (no foundation), but its roof is the ship's
        // (ShipRoofs, IsoRim) and only ship decks and ship stairs go on or over it (SlabSupport.BuildBlocked).
        public static bool OverShip(IntVec3 c, Map map) => Open(c, map) && Down(c, map).IsValid;
    }

    // A gravship deck foundation: Substructure's tag (TerrainDef.IsSubstructure), or the game's Substructure terrain
    // affordance (only a gravship deck carries it; modded decks may have it without the tag). The same rule as IsoRim's
    // ship roofs. ProjectZ's "is this a gravship deck" questions ask this (SlabSupport.Gravship, so ShipLink too; a
    // planned deck; a removed deck foundation), except the ship set (ShipSet.Fill), which keeps the game's own test (the
    // tag) so the cells that fly are exactly vanilla's.
    public static class DeckFoundation
    {
        private static TerrainAffordanceDef affordance;
        private static bool looked;

        public static bool Is(TerrainDef f)
        {
            if (f == null)
            {
                return false;
            }
            if (f.IsSubstructure)
            {
                return true;
            }
            if (!looked)
            {
                affordance = DefDatabase<TerrainAffordanceDef>.GetNamedSilentFail("Substructure");
                looked = true;
            }
            return affordance != null && f.affordances != null && f.affordances.Contains(affordance);
        }
    }

    // A ship deck is the gravship's substructure on an upper floor: at startup (after every def and patch) it gets every
    // terrain affordance vanilla Substructure has and it lacks, also those other mods add to Substructure, so whatever
    // may stand on substructure may stand on a deck.
    [StaticConstructorOnStartup]
    public static class DeckAffordances
    {
        static DeckAffordances()
        {
            TerrainDef deck = PZDefOf.PZ_ShipDeck;
            TerrainDef sub = DefDatabase<TerrainDef>.GetNamedSilentFail("Substructure");
            if (deck == null || sub?.affordances == null)
            {
                return;
            }
            if (deck.affordances == null)
            {
                deck.affordances = new List<TerrainAffordanceDef>();
            }
            List<string> added = new List<string>();
            foreach (TerrainAffordanceDef a in sub.affordances)
            {
                if (a != null && !deck.affordances.Contains(a))
                {
                    deck.affordances.Add(a);
                    added.Add(a.defName);
                }
            }
            if (added.Count > 0)
            {
                ProjectZ.DebugLog("ship deck affordances from Substructure: " + string.Join(", ", added));
            }
        }
    }

    public static class ShipDeck
    {
        // Other foundations registered as decks (Api 4, RegisterDeckTerrain): copy on write, read from any thread.
        private static volatile HashSet<TerrainDef> extra = new HashSet<TerrainDef>();

        public static bool Is(TerrainDef t) => t != null && (t == PZDefOf.PZ_ShipDeck || extra.Contains(t));

        // A registered deck foundation (not the deck itself).
        public static bool IsExtra(BuildableDef d) => d is TerrainDef t && extra.Contains(t);

        public static void Register(TerrainDef def)
        {
            if (def == null || def == PZDefOf.PZ_ShipDeck || extra.Contains(def))
            {
                return;
            }
            if (!def.isFoundation)
            {
                Log.Warning("[ProjectZ] RegisterDeckTerrain: " + def.defName + " is not a foundation; not registered.");
                return;
            }
            lock (typeof(ShipDeck))
            {
                HashSet<TerrainDef> next = new HashSet<TerrainDef>(extra) { def };
                extra = next;
            }
            ProjectZ.DebugLog("deck terrain registered: " + def.defName);
        }

        public static bool Is(BuildableDef d) => d is TerrainDef t && Is(t);

        // An upper floor cell with a ship deck (bare or under a floor laid on it).
        public static bool At(IntVec3 c, Map map) => c.IsValid && c.InBounds(map) && Is(map.terrainGrid.FoundationAt(c));

        // A deck on an upper floor (the ground's substructure is vanilla's).
        public static bool Upper(IntVec3 c, Map map) => ProjectZ.Annex(map) != null && At(c, map) && ProjectZ.FloorOf(c, map) >= 1;

        // An upper deck that can't be taken up now: the deck rules (Blocker), or a building, blueprint or frame on it (as
        // for slabs; items and pawns are moved off instead). The remove tool's work giver asks it
        // (FoundationRemovalBlocked_ShipDeck_Patch), and the peel order leaves such a deck out (SlabPeel).
        public static bool RemovalBlocked(IntVec3 c, Map map)
        {
            if (Blocker(c, map) != null)
            {
                return true;
            }
            List<Thing> list = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is Building || list[i] is Blueprint || list[i] is Frame)
                {
                    return true;
                }
            }
            return false;
        }

        // Where the worker of a forced order stands to take deck c up (compat G2: what stands on a removed deck falls, so
        // never on c itself; automatic work follows the peel order, SlabPeel.Parent): a cell next to it (4 ways) on its
        // floor that the pawn can stand on and reach; one not marked for removal itself first, then the nearest to the
        // pawn. Invalid: none.
        public static IntVec3 WorkSpot(Pawn pawn, IntVec3 c)
        {
            Map map = pawn.Map;
            IntVec3 best = IntVec3.Invalid;
            int bestScore = int.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                IntVec3 x = c + GenAdj.CardinalDirections[i];
                if (!x.InBounds(map) || !ProjectZ.SameBand(x, c, map) || !x.Standable(map) || !pawn.CanReach(x, PathEndMode.OnCell, Danger.Deadly))
                {
                    continue;
                }
                int score = (map.designationManager.DesignationAt(x, DesignationDefOf.RemoveFoundation) != null ? 1000000 : 0) + pawn.Position.DistanceToSquared(x);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = x;
                }
            }
            return best;
        }

        // A blueprint or frame of gravship substructure (vanilla's or a deck) on c.
        public static bool Planned(IntVec3 c, Map map)
        {
            if (!c.IsValid || !c.InBounds(map))
            {
                return false;
            }
            List<Thing> list = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < list.Count; i++)
            {
                if ((list[i] is Blueprint || list[i] is Frame) && list[i].def.entityDefToBuild is TerrainDef t && DeckFoundation.Is(t))
                {
                    return true;
                }
            }
            return false;
        }

        // Where a deck may go: an upper floor cell (open air: the game's affordance check) linked to the gravship's deck
        // (ShipLink.Would: substructure or a deck next to it, or straight above or below it through open cells; built,
        // planned or being dragged); not over a thick roof, a stairwell or anything too tall for the storey (as slabs);
        // and where the game's roof rules hold it. The engine's range is the deck def's own vanilla place worker.
        // (The link is asked after the cell's own rules: a dragged cell counts for the link only if it passes those.)
        public static AcceptanceReport Check(IntVec3 loc, Map map)
        {
            if (ProjectZ.FloorOf(loc, map) < 1)
            {
                return PlaceWorker_UpperFloor.ViewFloorAbove();
            }
            IntVec3 below = FloorRoof.Below(loc, map);
            if (SlabSupport.ThickRoof(below, map))
            {
                return "PZ_ThickRoof".Translate();
            }
            if (Stairwell.StairThingAt(below, map))
            {
                return "PZ_StairwellBelow".Translate();
            }
            if (LadderColumn.Plane(below, map))
            {
                return LadderColumn.Refusal(below, map, true);
            }
            Thing tall = HeightRule.TallThingAt(below, map);
            if (tall != null)
            {
                return "PZ_TallBelow".Translate(tall.LabelShort);
            }
            if (!ShipLink.Would(loc, map, planned: true))
            {
                return "PZ_DeckNeedsShipLink".Translate();
            }
            // roof support as for any upper floor (the game's roof rules; decks planned or being dragged next to it count)
            if (!SlabSupport.Supported(below, map, countPlanned: true))
            {
                return "PZ_NoSupportBelow".Translate();
            }
            return AcceptanceReport.WasAccepted;
        }

        // Why the deck at c can't be removed now. Null: no reason. Only the exit of ship stairs stays while they stand, as
        // a slab landing does (P5). Nothing else of the ship blocks a removal (hull3d G4): the substructure under a deck
        // and a deck under a deck may go, what is over them rests on its roof (the game's roof support).
        public static string Blocker(IntVec3 c, Map map)
        {
            if (SlabSupport.Gravship(c, map) && ProjectZ.FloorOf(c, map) >= 1 && StairwellFloor.IsStairsExit(c, map))
            {
                return "PZ_StairsLandHere".Translate();
            }
            return null;
        }
    }

    public class PlaceWorker_ShipDeck : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            return ShipDeck.Check(loc, map);
        }
    }

    // A floor laid on a deck over open air keeps open air as its under layer (the game would put its default under
    // terrain there, sand, which a removed floor would leave behind on an upper floor).
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain))]
    public static class SetTerrain_ShipDeck_Patch
    {
        private static readonly AccessTools.FieldRef<TerrainGrid, Map> mapRef = AccessTools.FieldRefAccess<TerrainGrid, Map>("map");
        private static readonly AccessTools.FieldRef<TerrainGrid, TerrainDef[]> underRef = AccessTools.FieldRefAccess<TerrainGrid, TerrainDef[]>("underGrid");

        public static void Prefix(TerrainGrid __instance, IntVec3 c, TerrainDef newTerr, out bool __state)
        {
            __state = false;
            if (newTerr == null || !newTerr.layerable || newTerr.isFoundation || newTerr.temporary)
            {
                return;
            }
            Map map = mapRef(__instance);
            if (map != null && c.InBounds(map) && ProjectZ.IsAnnex(c, map) && __instance.FoundationAt(c) != null
                && __instance.UnderTerrainAt(c) == null && __instance.TopTerrainAt(c) == PZDefOf.PZ_OpenAir)
            {
                __state = true;
            }
        }

        public static void Postfix(TerrainGrid __instance, IntVec3 c, bool __state)
        {
            if (__state)
            {
                underRef(__instance)[mapRef(__instance).cellIndices.CellToIndex(c)] = PZDefOf.PZ_OpenAir;
            }
        }
    }

    // A terrain change on an upper floor.
    //   - A cell opened or closed in a column with gravship floor straight above or below it: the vertical link through
    //     it may have come or gone (ShipLink), so the ship set is asked again. (The game does this itself for substructure
    //     changes; this covers any other floor.)
    //   - A deck taken away (the remove tool, god mode, a destroyed foundation, a falling deck) leaves open air and what
    //     stood on it falls (Slab.MoveOff).
    [HarmonyPatch(typeof(TerrainGrid), "DoTerrainChangedEffects")]
    public static class TerrainChanged_ShipDeck_Patch
    {
        private static readonly AccessTools.FieldRef<TerrainGrid, Map> mapRef = AccessTools.FieldRefAccess<TerrainGrid, Map>("map");

        public static void Postfix(TerrainGrid __instance, IntVec3 c, TerrainDef oldTerr, TerrainDef newTerr, TerrainDef oldFoundation)
        {
            Map map = mapRef(__instance);
            if (map == null || ProjectZ.Annex(map) == null)
            {
                return;
            }
            if (oldTerr != newTerr && (oldTerr == PZDefOf.PZ_OpenAir || newTerr == PZDefOf.PZ_OpenAir) && ModsConfig.OdysseyActive
                && ProjectZ.IsAnnex(c, map) && (ShipLink.Up(c, map).IsValid || ShipLink.Down(c, map).IsValid))
            {
                map.substructureGrid?.MarkDirty();
            }
            if (!DeckFoundation.Is(oldFoundation) || __instance.FoundationAt(c) != null)
            {
                return;
            }
            if (ShipDeck.Is(oldFoundation) && ProjectZ.FloorOf(c, map) >= 1)
            {
                if (__instance.TopTerrainAt(c) != PZDefOf.PZ_OpenAir)
                {
                    // (a floor still laid on it: only god mode removes a foundation before the floor)
                    __instance.SetTerrain(c, PZDefOf.PZ_OpenAir);
                }
                Slab.DropZoneCell(c, map);
                Slab.MoveOff(c, map);
            }
        }
    }

    // ---- removal

    // An upper floor deck, as for slabs: only built things (and the exit of ship stairs) block (vanilla would also count
    // items and pawns, whose affordance open air lacks; they are moved off instead).
    [HarmonyPatch(typeof(WorkGiver_ConstructRemoveFoundation), nameof(WorkGiver_ConstructRemoveFoundation.AnyBuildingBlockingFoundationRemoval))]
    public static class FoundationRemovalBlocked_ShipDeck_Patch
    {
        public static bool Prefix(IntVec3 c, Map map, ref bool __result)
        {
            if (!ShipDeck.At(c, map) || ProjectZ.FloorOf(c, map) < 1)
            {
                return true;
            }
            __result = ShipDeck.RemovalBlocked(c, map);
            return false;
        }
    }

    [HarmonyPatch(typeof(Designator_RemoveFoundation), nameof(Designator_RemoveFoundation.CanDesignateCell))]
    public static class RemoveFoundationDesignate_ShipDeck_Patch
    {
        public static void Postfix(Designator_RemoveFoundation __instance, IntVec3 c, ref AcceptanceReport __result)
        {
            if (!__result.Accepted && !__result.Reason.NullOrEmpty() && ProjectZ.Annex(__instance.Map) != null)
            {
                string why = ShipDeck.Blocker(c, __instance.Map);
                if (why != null)
                {
                    __result = why;
                }
            }
        }
    }

    // Taking up an upper deck returns no materials on any path (user 2026-10-18; the remove tool, a stairwell, ship stairs'
    // landing, god mode): nothing falls from the removal, no roof below breaks. (Collapse and launch paths unchanged.)
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.RemoveFoundation))]
    public static class RemoveFoundation_ShipDeckNoLeavings_Patch
    {
        private static readonly AccessTools.FieldRef<TerrainGrid, Map> mapRef = AccessTools.FieldRefAccess<TerrainGrid, Map>("map");

        public static void Prefix(TerrainGrid __instance, IntVec3 c, ref bool doLeavings)
        {
            if (doLeavings && ShipDeck.Upper(c, mapRef(__instance)))
            {
                doLeavings = false;
            }
        }
    }

    // A forced order on an upper deck needs a cell next to it to stand on (WorkSpot, the forced job's target B, Slabs.cs):
    // none, no job. As for slabs, automatic work is not asked this: the peel order decides (SlabPeel.Workable, the worker
    // on SlabPeel.Parent: RemoveFoundationHasJob_Slab_Patch, RemoveFoundationJob_Slab_Patch).
    [HarmonyPatch(typeof(WorkGiver_ConstructRemoveFoundation), nameof(WorkGiver_ConstructRemoveFoundation.HasJobOnCell))]
    public static class RemoveFoundationHasJob_ShipDeck_Patch
    {
        public static void Postfix(Pawn pawn, IntVec3 c, bool forced, ref bool __result)
        {
            if (__result && forced && ShipDeck.Upper(c, pawn.Map) && !ShipDeck.WorkSpot(pawn, c).IsValid)
            {
                JobFailReason.Is("PZ_NoWorkSpot".Translate());
                __result = false;
            }
        }
    }

    // A planned deck is built once it is linked to the gravship's deck as built (ShipLink.Would: decks go up outward from
    // the ship, never as a loose frame), and where the game's roof rules hold it now.
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanConstruct), new[] { typeof(Thing), typeof(Pawn), typeof(bool), typeof(bool), typeof(JobDef) })]
    public static class CanConstruct_ShipDeck_Patch
    {
        public static bool Prefix(Thing t, ref bool __result)
        {
            // (upper floors only: on the ground a registered deck foundation keeps its own rules)
            if (t == null || !t.Spawned || !ShipDeck.Is(t.def.entityDefToBuild) || ProjectZ.Annex(t.Map) == null || ProjectZ.FloorOf(t.Position, t.Map) < 1)
            {
                return true;
            }
            if (!ShipLink.Would(t.Position, t.Map, planned: false))
            {
                JobFailReason.Is("PZ_DeckNeedsShipLink".Translate());
                __result = false;
                return false;
            }
            if (!SlabSupport.Supported(FloorRoof.Below(t.Position, t.Map), t.Map))
            {
                JobFailReason.Is("PZ_NoSupportBelow".Translate());
                __result = false;
                return false;
            }
            return true;
        }
    }
}
