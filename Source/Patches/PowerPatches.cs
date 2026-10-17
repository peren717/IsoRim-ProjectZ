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
    // Second, separate rule (ConnectRange): a building's wireless hookup to a transmitter within 6 cells stays on its
    // own floor. Vanilla searches a rect clipped to the map; floors are bands of the same map with only a few void rows
    // between them (3 on a 150 map), so near a band edge a lamp could hook up to a conduit on the next floor. Both
    // searches (PowerConnectionMaker.BestTransmitterForConnector and PotentialConnectorsForTransmitter) clip their rect
    // with CellRect.ClipInsideMap; that call becomes PowerRisers.ClipInsideFloor (also clipped to the centre's band).
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
            IntVec3 s = up ? StairShape.Above(c, map) : FloorRoof.Below(c, map);
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

        // Replace calls to 'from' by calls to 'to'; 'extraArgs' are pushed right before the call. Logs if the count differs.
        internal static List<CodeInstruction> Swap(IEnumerable<CodeInstruction> instructions, MethodInfo from, MethodInfo to, int expected, string where, params CodeInstruction[] extraArgs)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int done = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if ((list[i].opcode == OpCodes.Call || list[i].opcode == OpCodes.Callvirt) && list[i].operand as MethodInfo == from)
                {
                    list[i].opcode = OpCodes.Call;
                    list[i].operand = to;
                    for (int j = 0; j < extraArgs.Length; j++)
                    {
                        list.Insert(i, extraArgs[extraArgs.Length - 1 - j].Clone());
                    }
                    i += extraArgs.Length;
                    done++;
                }
            }
            if (done != expected)
            {
                Log.Error($"[ProjectZ] {where} is not in the expected form ({done} of {expected} calls swapped): power may not cross floors correctly.");
            }
            return list;
        }

        internal static readonly MethodInfo CardinalThing = AccessTools.Method(typeof(GenAdj), nameof(GenAdj.CellsAdjacentCardinal), new[] { typeof(Thing) });
        internal static readonly MethodInfo CardinalRect = AccessTools.Method(typeof(GenAdj), nameof(GenAdj.CellsAdjacentCardinal), new[] { typeof(IntVec3), typeof(Rot4), typeof(IntVec2) });
        internal static readonly MethodInfo AdjacentThing = AccessTools.Method(typeof(PowerRisers), nameof(Adjacent), new[] { typeof(Thing) });
        internal static readonly MethodInfo AdjacentRect = AccessTools.Method(typeof(PowerRisers), nameof(Adjacent), new[] { typeof(IntVec3), typeof(Rot4), typeof(IntVec2), typeof(PowerNetManager) });
        internal static readonly MethodInfo ClipMap = AccessTools.Method(typeof(CellRect), nameof(CellRect.ClipInsideMap));
        internal static readonly MethodInfo ClipFloor = AccessTools.Method(typeof(PowerRisers), nameof(ClipInsideFloor));
    }

    // Site 1.
    [HarmonyPatch(typeof(PowerNetMaker), "ContiguousPowerBuildings")]
    public static class PowerNetMaker_Risers_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return PowerRisers.Swap(instructions, PowerRisers.CardinalThing, PowerRisers.AdjacentThing, 1, "PowerNetMaker.ContiguousPowerBuildings");
        }
    }

    // Sites 2 and 3.
    [HarmonyPatch(typeof(PowerNetManager), nameof(PowerNetManager.UpdatePowerNetsAndConnections_First))]
    public static class PowerNetManager_Risers_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = PowerRisers.Swap(instructions, PowerRisers.CardinalThing, PowerRisers.AdjacentThing, 1, "PowerNetManager.UpdatePowerNetsAndConnections_First (register)");
            return PowerRisers.Swap(list, PowerRisers.CardinalRect, PowerRisers.AdjacentRect, 1, "PowerNetManager.UpdatePowerNetsAndConnections_First (rebuild)", new CodeInstruction(OpCodes.Ldarg_0));
        }
    }

    // ConnectRange.
    [HarmonyPatch(typeof(PowerConnectionMaker), nameof(PowerConnectionMaker.BestTransmitterForConnector))]
    public static class BestTransmitterForConnector_Floor_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return PowerRisers.Swap(instructions, PowerRisers.ClipMap, PowerRisers.ClipFloor, 2, "PowerConnectionMaker.BestTransmitterForConnector");
        }
    }

    [HarmonyPatch]
    public static class PotentialConnectorsForTransmitter_Floor_Patch
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(PowerConnectionMaker), "PotentialConnectorsForTransmitter"));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return PowerRisers.Swap(instructions, PowerRisers.ClipMap, PowerRisers.ClipFloor, 1, "PowerConnectionMaker.PotentialConnectorsForTransmitter");
        }
    }
}
