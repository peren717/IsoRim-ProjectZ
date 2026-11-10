using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ.Patches
{
    // Power across floors (design "Networks across floors", user decision 2026-10-16): a power conduit (any def with
    // building.isPowerConduit: PowerConduit, HiddenConduit, WaterproofConduit and modded conduits made the same way) also
    // touches a conduit in the stacked cell directly above or below it (same column, next floor). Every other
    // transmitter (batteries, switches, buildings) keeps vanilla's horizontal-only adjacency.
    //
    // One rule, "power adjacency" = the cardinal cells + the conduit risers (PowerRisers.Adjacent). Vanilla asks for a
    // transmitter's neighbours in exactly three places, all GenAdj.CellsAdjacentCardinal calls (Assembly-CSharp 1.7):
    //   1. PowerNetMaker.ContiguousPowerBuildings: the flood fill that makes a net (the only net builder; it is reached
    //      only through NewPowerNetStartingFrom <- PowerNetManager.TryCreateNetAt).
    //   2. PowerNetManager.UpdatePowerNetsAndConnections_First, RegisterTransmitter: the nets next to a new transmitter
    //      are destroyed (so a conduit added on either floor dissolves the net on the other floor too).
    //   3. the same method, the rebuild pass after Register/DeregisterTransmitter: nets are re-made from the cell and its
    //      neighbours (so removing a riser conduit re-makes the cut-off part on the other floor). Here the thing may be
    //      gone, so every stacked cell holding a conduit is a candidate; TryCreateNetAt does nothing where a net exists.
    // Each call is swapped for the matching PowerRisers.Adjacent overload by one transpiler. Nets aren't saved: after a
    // load every transmitter re-registers and the same code joins the floors again. PowerNetGrid, the power overlay
    // and the conduit link graphics work per cell and need nothing; the vertical link is drawn by RiserView (same rule,
    // PowerRisers.Riser).
    //
    // Second, separate rule (ConnectRange): a building's wireless hookup to a transmitter in the game's range stays on its
    // own floor. Vanilla searches a rect clipped to the map; floors are bands of the same map with only a few void rows
    // between them (3 on a 150 map), so near a band edge a lamp could hook up to a conduit on the next floor. Both
    // searches (PowerConnectionMaker.BestTransmitterForConnector and PotentialConnectorsForTransmitter) clip their rect
    // with CellRect.ClipInsideMap; that call becomes PowerRisers.ClipInsideFloor (also clipped to the centre's band).
    //
    // Third rule, built on the second (hookups to the next floor, user decision 2026-10-26; PowerHookup): a building that
    // finds no transmitter in range on its own floor hooks up to one on the floor directly above or below. The range is
    // mapped there by the stacked-cell rule (Api.StackedCell of the connector's cell, the game's own search round it, clipped
    // to that band; never a raw rect across the void rows); only +-1 floor; both found -> the nearer by horizontal
    // distance in the mapped frame (a tie: the floor above). Vanilla picks a transmitter for a connector in exactly two
    // functions (Assembly-CSharp 1.7), both get the rule:
    //   a. PowerConnectionMaker.BestTransmitterForConnector (postfix, only when the own-floor search found nothing): used
    //      by TryConnectToAnyPowerNet (every new, re-spawned or reinstalled connector; one whose transmitter went away or
    //      stopped transmitting, e.g. a switch flicked off; a gravship connector whose transmitter stayed behind),
    //      CompPower.TryManualReconnect (the reconnect gizmo; its "skip these nets" list never pushes a connector off its
    //      own floor: other floors are searched only when the own floor has no transmitter at all) and the build preview
    //      (BuildDesignatorUtility, its anticipated wire).
    //   b. PowerConnectionMaker.PotentialConnectorsForTransmitter (postfix, after the own-floor connectors): a new net's
    //      transmitters pick up waiting connectors; from the floors above and below only a connector whose own pick (rule
    //      a) is this very transmitter, so both directions agree and own floor first holds.
    // Not a pick: a load restores the saved connectParent (a reference, any floor), a gravship landing restores its saved
    // pairs (Gravship.connectParents), RoomPart_ConnectConduits takes the transmitter in the same cell. Nets and
    // PowerNetGrid are per transmitter cell and do not care where a connector is; the wire to a transmitter on another
    // floor is drawn as the game draws it (user decision 2026-11-01: no extra marks).
    public static class PowerRisers
    {
        public static bool IsConduit(ThingDef def) => def?.building != null && def.building.isPowerConduit;

        // A transmitting conduit in cell c, or null.
        public static Building ConduitAt(IntVec3 c, Map map)
        {
            if (!c.IsValid || !c.InBounds(map))
            {
                return null;
            }
            List<Thing> list = c.GetThingList(map);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is Building b && IsConduit(b.def) && b.TransmitsPowerNow)
                {
                    return b;
                }
            }
            return null;
        }

        // The one rule for a vertical link (also what RiserView draws): the stacked cell directly above (up) or below c,
        // if it holds a conduit; IntVec3.Invalid otherwise. A link needs a conduit at both ends: Adjacent only asks from a
        // conduit, and RiserView only from a conduit cell.
        public static IntVec3 Riser(IntVec3 c, Map map, bool up)
        {
            IntVec3 s = Api.StackedCell(c, map, up);   // (the column rule, public since Api 3)
            return ConduitAt(s, map) != null ? s : IntVec3.Invalid;
        }

        // The stacked cells above and below c that hold a conduit.
        public static IEnumerable<IntVec3> Risers(IntVec3 c, Map map)
        {
            IntVec3 up = Riser(c, map, true);
            if (up.IsValid)
            {
                yield return up;
            }
            IntVec3 down = Riser(c, map, false);
            if (down.IsValid)
            {
                yield return down;
            }
        }

        // Sites 1 and 2: a transmitter's power neighbours.
        public static IEnumerable<IntVec3> Adjacent(Thing t)
        {
            IEnumerable<IntVec3> cardinal = GenAdj.CellsAdjacentCardinal(t);
            Map map = t.MapHeld;
            if (!IsConduit(t.def) || ProjectZ.Annex(map) == null)
            {
                return cardinal;
            }
            return WithRisers(cardinal, GenAdj.OccupiedRect(t.Position, t.Rotation, t.def.size), map);
        }

        // Site 3: neighbours of a cell where a transmitter was added or removed (the thing may be gone already).
        public static IEnumerable<IntVec3> Adjacent(IntVec3 center, Rot4 rot, IntVec2 size, PowerNetManager manager)
        {
            IEnumerable<IntVec3> cardinal = GenAdj.CellsAdjacentCardinal(center, rot, size);
            Map map = manager?.map;
            if (ProjectZ.Annex(map) == null)
            {
                return cardinal;
            }
            return WithRisers(cardinal, GenAdj.OccupiedRect(center, rot, size), map);
        }

        private static IEnumerable<IntVec3> WithRisers(IEnumerable<IntVec3> cardinal, CellRect occupied, Map map)
        {
            foreach (IntVec3 c in cardinal)
            {
                yield return c;
            }
            foreach (IntVec3 c in occupied)
            {
                foreach (IntVec3 r in Risers(c, map))
                {
                    yield return r;
                }
            }
        }

        // ConnectRange: ClipInsideMap, then also inside the band (floor) of the rect's centre.
        public static CellRect ClipInsideFloor(ref CellRect rect, Map map)
        {
            CellRect clipped = rect.ClipInsideMap(map);
            AnnexMapComponent a = ProjectZ.Annex(map);
            if (a == null)
            {
                return clipped;
            }
            int k = ProjectZ.FloorOf(rect.CenterCell, map);
            if (k < 0)
            {
                return clipped;
            }
            int lo = Levels.RowMin(map, k);
            int hi = Levels.RowMax(map, k);
            if (clipped.minZ < lo)
            {
                clipped.minZ = lo;
            }
            if (clipped.maxZ > hi)
            {
                clipped.maxZ = hi;
            }
            return clipped;
        }

        internal static readonly MethodInfo CardinalThing = AccessTools.Method(typeof(GenAdj), nameof(GenAdj.CellsAdjacentCardinal), new[] { typeof(Thing) });
        internal static readonly MethodInfo CardinalRect = AccessTools.Method(typeof(GenAdj), nameof(GenAdj.CellsAdjacentCardinal), new[] { typeof(IntVec3), typeof(Rot4), typeof(IntVec2) });
        internal static readonly MethodInfo AdjacentThing = AccessTools.Method(typeof(PowerRisers), nameof(Adjacent), new[] { typeof(Thing) });
        internal static readonly MethodInfo AdjacentRect = AccessTools.Method(typeof(PowerRisers), nameof(Adjacent), new[] { typeof(IntVec3), typeof(Rot4), typeof(IntVec2), typeof(PowerNetManager) });
        internal static readonly MethodInfo ClipMap = AccessTools.Method(typeof(CellRect), nameof(CellRect.ClipInsideMap));
        internal static readonly MethodInfo ClipFloor = AccessTools.Method(typeof(PowerRisers), nameof(ClipInsideFloor));
        internal static readonly MethodInfo RectThing = AccessTools.Method(typeof(GenAdj), nameof(GenAdj.OccupiedRect), new[] { typeof(Thing) });
        internal static readonly MethodInfo RectFor = AccessTools.Method(typeof(PowerHookup), nameof(PowerHookup.RectFor));
    }

    // The hookup rule (third rule above): own floor first, else the nearer of the floors directly above and below.
    // The range and the test are the game's own (no copied PowerConnectionMaker.ConnectMaxDist): every search is the
    // game's finder itself, asked at the mapped cell; only where it looks is swapped (the smallest change that keeps
    // another mod's range or test):
    //   - a transmitter for a connector: PowerConnectionMaker.BestTransmitterForConnector at the connector's cell (own
    //     floor) or at its stacked cell above or below, its search clipped to that cell's band by ConnectRange
    //     (BestTransmitterForConnector_Floor_Patch);
    //   - the connectors round a new transmitter on the floors above and below: PotentialConnectorsForTransmitter with
    //     the transmitter's rect moved to its stacked cell there (PotentialConnectors_Rect_Patch), clipped to that band.
    // While ProjectZ asks (Asking), the hookup postfixes below stay out (no recursion).
    public static class PowerHookup
    {
        // ProjectZ is asking the game's finders (the across-floor postfixes stay out)
        public static bool Asking;

        // (PotentialConnectors_Rect_Patch) the transmitter's rect moved here while asking for another floor; Invalid: its own
        private static IntVec3 rectAt = IntVec3.Invalid;

        private static readonly Func<CompPower, IEnumerable<CompPower>> potential =
            AccessTools.MethodDelegate<Func<CompPower, IEnumerable<CompPower>>>(AccessTools.Method(typeof(PowerConnectionMaker), "PotentialConnectorsForTransmitter"));

        // Where a connector hooks up from (vanilla: a wall attachment from its wall).
        public static IntVec3 ConnectorPos(CompPower pc)
        {
            ThingWithComps p = pc.parent;
            if (p.def.building != null && p.def.building.isAttachment)
            {
                Thing wall = GenConstruct.GetWallAttachedTo(p);
                if (wall != null)
                {
                    return wall.Position;
                }
            }
            return p.Position;
        }

        // The game's pick for a connector at c (its search on c's own floor).
        private static CompPower Ask(IntVec3 c, Map map, List<PowerNet> disallowed)
        {
            bool was = Asking;
            Asking = true;
            try
            {
                return PowerConnectionMaker.BestTransmitterForConnector(c, map, disallowed);
            }
            finally
            {
                Asking = was;
            }
        }

        // The game's searches stay on one floor only while ConnectRange holds (its clips landed).
        private static bool Clipped => FailSoft.IsApplied(nameof(BestTransmitterForConnector_Floor_Patch)) && FailSoft.IsApplied(nameof(PotentialConnectorsForTransmitter_Floor_Patch));

        // A transmitter in range on the connector's own floor (the game's search, clipped to the band).
        public static CompPower OwnFloor(IntVec3 pos, Map map, List<PowerNet> disallowed = null)
        {
            return Ask(pos, map, disallowed);
        }

        // The nearest transmitter in range on the floor directly above or below (the game's search at the stacked cell);
        // null without floors, from a void row, or when there is none.
        public static CompPower OtherFloors(IntVec3 pos, Map map, List<PowerNet> disallowed = null)
        {
            if (!Clipped)
            {
                return null;
            }
            CompPower best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < 2; i++)
            {
                IntVec3 s = Api.StackedCell(pos, map, i == 0);
                if (!s.IsValid)
                {
                    continue;
                }
                CompPower pc = Ask(s, map, disallowed);
                if (pc == null)
                {
                    continue;
                }
                float d = (pc.parent.Position - s).LengthHorizontalSquared;   // (the game's own measure for its pick)
                if (d < bestD)
                {
                    best = pc;
                    bestD = d;
                }
            }
            return best;
        }

        // Rule a, after vanilla's own-floor search found nothing.
        public static CompPower Fallback(IntVec3 pos, Map map, List<PowerNet> disallowed)
        {
            if (ProjectZ.Annex(map) == null || !pos.IsValid || !pos.InBounds(map))
            {
                return null;
            }
            if (disallowed != null && disallowed.Count > 0 && OwnFloor(pos, map) != null)
            {
                return null;   // the own floor has transmitters, only of skipped nets: stay on this floor
            }
            return OtherFloors(pos, map, disallowed);
        }

        // (PotentialConnectors_Rect_Patch) the rect the game's search goes round.
        public static CellRect RectFor(Thing t)
        {
            CellRect occ = t.OccupiedRect();
            return rectAt.IsValid ? new CellRect(rectAt.x, rectAt.z, occ.Width, occ.Height) : occ;
        }

        // Rule b: the own-floor connectors (vanilla), then the waiting connectors on the floors above and below whose own
        // pick is t.
        public static IEnumerable<CompPower> WithOtherFloors(IEnumerable<CompPower> own, CompPower t)
        {
            foreach (CompPower c in own)
            {
                yield return c;
            }
            ThingWithComps p = t?.parent;
            if (p == null || !p.Spawned || !Clipped || !FailSoft.IsApplied(nameof(PotentialConnectors_Rect_Patch)))
            {
                yield break;
            }
            Map map = p.Map;
            if (ProjectZ.Annex(map) == null)
            {
                yield break;
            }
            CellRect occ = p.OccupiedRect();
            List<CompPower> found = new List<CompPower>();
            for (int i = 0; i < 2; i++)
            {
                IntVec3 s = Api.StackedCell(occ.Min, map, i == 0);
                if (!s.IsValid)
                {
                    continue;
                }
                found.Clear();
                bool was = Asking;
                Asking = true;
                rectAt = s;
                try
                {
                    found.AddRange(potential(t));   // the game's search round the rect moved to floor s
                }
                finally
                {
                    rectAt = IntVec3.Invalid;
                    Asking = was;
                }
                foreach (CompPower pc in found)
                {
                    if (pc == null || pc.connectParent != null)
                    {
                        continue;
                    }
                    IntVec3 pos = ConnectorPos(pc);
                    if (OwnFloor(pos, map) == null && OtherFloors(pos, map) == t)
                    {
                        yield return pc;
                    }
                }
            }
        }
    }

    // Site 1 (sites 1-3 work together: one failing turns the risers off, FailSoft group).
    [HarmonyPatch(typeof(PowerNetMaker), "ContiguousPowerBuildings")]
    [PatchGroup("power risers", key: true)]
    public static class PowerNetMaker_Risers_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            return FailSoft.Transpile(nameof(PowerNetMaker_Risers_Patch), original, instructions, code => CallSwap.Swap(code, PowerRisers.CardinalThing, PowerRisers.AdjacentThing, 1));
        }
    }

    // Sites 2 and 3.
    [HarmonyPatch(typeof(PowerNetManager), nameof(PowerNetManager.UpdatePowerNetsAndConnections_First))]
    [PatchGroup("power risers", key: true)]
    public static class PowerNetManager_Risers_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            return FailSoft.Transpile(nameof(PowerNetManager_Risers_Patch), original, instructions, code =>
                CallSwap.Swap(code, PowerRisers.CardinalThing, PowerRisers.AdjacentThing, 1)   // register
                ?? CallSwap.Swap(code, PowerRisers.CardinalRect, PowerRisers.AdjacentRect, 1, new CodeInstruction(OpCodes.Ldarg_0)));   // rebuild
        }
    }

    // ConnectRange.
    [HarmonyPatch(typeof(PowerConnectionMaker), nameof(PowerConnectionMaker.BestTransmitterForConnector))]
    public static class BestTransmitterForConnector_Floor_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            return FailSoft.Transpile(nameof(BestTransmitterForConnector_Floor_Patch), original, instructions, code => CallSwap.Swap(code, PowerRisers.ClipMap, PowerRisers.ClipFloor, 2));
        }
    }

    [HarmonyPatch]
    public static class PotentialConnectorsForTransmitter_Floor_Patch
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(PowerConnectionMaker), "PotentialConnectorsForTransmitter"));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            return FailSoft.Transpile(nameof(PotentialConnectorsForTransmitter_Floor_Patch), original, instructions, code => CallSwap.Swap(code, PowerRisers.ClipMap, PowerRisers.ClipFloor, 1));
        }
    }

    // Hookups to the next floor (PowerHookup; both directions or neither).
    [HarmonyPatch(typeof(PowerConnectionMaker), nameof(PowerConnectionMaker.BestTransmitterForConnector))]
    [PatchGroup("power hookup across floors", key: true)]
    public static class BestTransmitterForConnector_Across_Patch
    {
        public static void Postfix(ref CompPower __result, IntVec3 connectorPos, Map map, List<PowerNet> disallowedNets)
        {
            if (__result == null && !PowerHookup.Asking)
            {
                __result = PowerHookup.Fallback(connectorPos, map, disallowedNets);
            }
        }
    }

    [HarmonyPatch(typeof(PowerConnectionMaker), "PotentialConnectorsForTransmitter")]
    [PatchGroup("power hookup across floors", key: true)]
    public static class PotentialConnectorsForTransmitter_Across_Patch
    {
        public static void Postfix(ref IEnumerable<CompPower> __result, CompPower b)
        {
            if (!PowerHookup.Asking && b?.parent?.Map != null && ProjectZ.Annex(b.parent.Map) != null)
            {
                __result = PowerHookup.WithOtherFloors(__result, b);
            }
        }
    }

    // The game's connector search round a transmitter, asked for the floor above or below (PowerHookup.WithOtherFloors):
    // its rect round the transmitter becomes PowerHookup.RectFor (the rect moved to the stacked cell while asking; its
    // own rect otherwise). The range and the test stay the game's. A live swap (CallSwap), not a private copy
    // (Scope3DCore.ReverseCopy) nor the 3D scope: the search is an iterator (its body is the state machine's MoveNext, which
    // a copy cannot be called as), and the swapped call is "where to look", not a 3D question. Outside ProjectZ's asking
    // RectFor is the game's own OccupiedRect.
    [HarmonyPatch]
    [PatchGroup("power hookup across floors", key: true)]
    public static class PotentialConnectors_Rect_Patch
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(PowerConnectionMaker), "PotentialConnectorsForTransmitter"));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            return FailSoft.Transpile(nameof(PotentialConnectors_Rect_Patch), original, instructions, code => CallSwap.Swap(code, PowerRisers.RectThing, PowerRisers.RectFor, 1));
        }
    }
}
