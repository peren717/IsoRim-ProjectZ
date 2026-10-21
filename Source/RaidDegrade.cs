using System.Collections.Generic;
using HarmonyLib;
using ProjectZ.Patches;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ
{
    // Sapper and breach raids against a base they can't reach (user 2026-10-20, siege ladders stage 2): the game's
    // destination for them (GenAI.RandomRaidDest) is an owned bed, a colony building or a colonist the last resort
    // (PassAllDestroyableThings) reaches; when there is none it falls back to a random reachable cell, and they walk from
    // one random cell to the next until they give up. On a map with floors that fallback turns the whole lord into an
    // ordinary assault instead (as the game already does with lost breach pawns: AssaultColony), so the climb takes over
    // (JobGiver_AIClimb). Raids that find a real target, and maps without floors, stay the game's.
    public static class RaidDegrade
    {
        // lords turned (tests)
        public static int Degraded;

        // RandomRaidDest's first three choices (owned beds, colony buildings, colonists), by the last resort's reach from
        // start: the dig components (the same answer the game's reachability gives for that mode, floors included).
        public static bool RealRaidDest(IntVec3 start, Map map)
        {
            RouteGraph g = RouteGraph.For(map);
            if (g == null)
            {
                return true;
            }
            if (map.regionDirtyer.AnyDirty)
            {
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            }
            int sc = ClimbPlanner.CompAt(g, start, map);
            if (sc == int.MinValue)
            {
                return true;   // (the game's own answer then)
            }
            List<ThingDef> beds = RestUtility.AllBedDefBestToWorst;
            for (int i = 0; i < beds.Count; i++)
            {
                foreach (Building b in map.listerBuildings.AllBuildingsColonistOfDef(beds[i]))
                {
                    if (((Building_Bed)b).OwnersForReading.Any() && !b.IsClearableFreeBuilding && ClimbPlanner.CompAt(g, b.Position, map) == sc)
                    {
                        return true;
                    }
                }
            }
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                if (b.def.building.ai_combatDangerous || b.def.building.isInert || b.def.building.ai_neverTrashThis || b.IsClearableFreeBuilding)
                {
                    continue;
                }
                foreach (IntVec3 c in GenAdj.CellsAdjacent8Way(b))
                {
                    if (c.InBounds(map) && c.Walkable(map) && ClimbPlanner.CompAt(g, c, map) == sc)
                    {
                        return true;
                    }
                }
            }
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (ClimbPlanner.CompAt(g, p.Position, map) == sc)
                {
                    return true;
                }
            }
            return false;
        }

        // The lord's toil would pick a new destination now and none is real: every pawn an assaulter at once, the lord
        // moved to its assault toil on the next tick (ClimbPlans.MapComponentTick: not inside the toil's own update).
        public static bool TryDegrade(Lord lord)
        {
            Map map = lord?.Map;
            if (map == null || ProjectZ.Annex(map) == null || RaidClimb.Off || lord.ownedPawns.Count == 0 || !FailSoft.IsApplied(nameof(CheckCellBasedReachability_Patch)))
            {
                return false;
            }
            if (RealRaidDest(lord.ownedPawns[0].PositionHeld, map))
            {
                return false;
            }
            for (int i = 0; i < lord.ownedPawns.Count; i++)
            {
                lord.ownedPawns[i].mindState.duty = new PawnDuty(DutyDefOf.AssaultColony);
            }
            ClimbPlans.For(map)?.QueueAssault(lord);
            return true;
        }

        // Called a tick later: the lord, still in that toil, goes to its assault toil (LordJob_AssaultColony has one).
        public static void ToAssault(Lord lord, LordToil from)
        {
            if (lord == null || lord.Map == null || !lord.Map.lordManager.lords.Contains(lord) || lord.CurLordToil != from)
            {
                return;
            }
            foreach (LordToil t in lord.Graph.lordToils)
            {
                if (t.GetType() == typeof(LordToil_AssaultColony))
                {
                    Degraded++;
                    ProjectZ.DebugLog($"{lord.faction} raid ({from.GetType().Name}): no colony target the last resort reaches, an ordinary assault now");
                    lord.GotoToil(t);
                    return;
                }
            }
        }
    }

    [HarmonyPatch(typeof(LordToil_AssaultColonySappers), nameof(LordToil_AssaultColonySappers.UpdateAllDuties))]
    public static class Sappers_Degrade_Patch
    {
        public static bool Prefix(LordToil_AssaultColonySappers __instance)
        {
            return !(__instance.data is LordToilData_AssaultColonySappers d && !d.sapperDest.IsValid && RaidDegrade.TryDegrade(__instance.lord));
        }
    }

    [HarmonyPatch(typeof(LordToil_AssaultColonyBreaching), nameof(LordToil_AssaultColonyBreaching.UpdateAllDuties))]
    public static class Breaching_Degrade_Patch
    {
        public static bool Prefix(LordToil_AssaultColonyBreaching __instance)
        {
            return !(__instance.Data is LordToilData_AssaultColonyBreaching d && !d.breachDest.IsValid && RaidDegrade.TryDegrade(__instance.lord));
        }
    }
}
