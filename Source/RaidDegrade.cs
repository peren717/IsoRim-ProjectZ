using System.Collections.Generic;
using HarmonyLib;
using ProjectZ.Patches;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ
{
    // Sapper and breach raids against a base they can't reach on foot (user 2026-10-20, stage 3 scope change: they behave
    // as the game's in 3D). The game's destination for them (GenAI.RandomRaidDest) is an owned bed, a colony building or a
    // colonist the last resort (PassAllDestroyableThings) reaches, else a random reachable cell. On a map with floors, when
    // the game finds no such target, the same three choices in the same order are offered again with the vertical moves
    // counted in (VerticalRaidDest: the raiders' planner finds a way there, ladders, mining up, blasting or digging through
    // ceilings, by what the lord's pawns can do). The lord keeps its own toil and duties: sappers (JobGiver_AISapper, then
    // the climb on the same duty) and breachers (BreachSegment: the game's breach path runs to the foot of the next vertical
    // step, and from the level reached on). No target even that way: the game's random reachable cell, as without
    // ProjectZ. (Stage 2 turned these lords into an ordinary assault; that is gone: TryDegrade is no longer called.)
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

    // The lord whose toil is choosing a destination now (the game's RandomRaidDest has no lord).
    public static class RaidDest
    {
        [System.ThreadStatic] public static Lord current;
        [System.ThreadStatic] public static bool sappers;

        private static readonly Dictionary<int, bool> compOk = new Dictionary<int, bool>();

        // The game's three choices again, counting the vertical moves (the planner, for the lord's most capable pawn);
        // Invalid when none.
        public static IntVec3 VerticalRaidDest(Lord lord, IntVec3 start, Map map, bool sapperLord)
        {
            RouteGraph g = RouteGraph.For(map);
            if (g == null || RaidClimb.Off || lord == null)
            {
                return IntVec3.Invalid;
            }
            Pawn rep = null;
            RaidVertical.PawnCost repCost = null;
            int best = -1;
            foreach (Pawn p in lord.ownedPawns)
            {
                if (!p.Spawned || !RaidClimb.Climber(p) || !RaidClimb.MayClimb(p, false))
                {
                    continue;
                }
                RaidVertical.PawnCost c = RaidVertical.PawnCost.For(p, sapperLord);
                int n = CountBits(c.caps);
                if (n > best)
                {
                    best = n;
                    rep = p;
                    repCost = c;
                }
            }
            if (rep == null)
            {
                return IntVec3.Invalid;
            }
            int sc = ClimbPlanner.CompAt(g, rep.Position, map);
            if (sc == int.MinValue)
            {
                return IntVec3.Invalid;
            }
            compOk.Clear();
            int tries = 0;
            bool Ok(IntVec3 cell)
            {
                int comp = ClimbPlanner.CompAt(g, cell, map);
                if (comp == int.MinValue)
                {
                    return false;
                }
                if (comp == sc)
                {
                    return true;
                }
                if (compOk.TryGetValue(comp, out bool ok))
                {
                    return ok;
                }
                if (tries >= 6)
                {
                    return false;
                }
                tries++;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                ok = ClimbPlanner.Find(map, g, rep, sc, false, cell, repCost) != null;
                RouteGraph.Spend((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                compOk[comp] = ok;
                return ok;
            }
            List<Building> beds = new List<Building>();
            foreach (ThingDef d in RestUtility.AllBedDefBestToWorst)
            {
                foreach (Building b in map.listerBuildings.AllBuildingsColonistOfDef(d))
                {
                    if (((Building_Bed)b).OwnersForReading.Any() && !b.IsClearableFreeBuilding && Ok(b.Position))
                    {
                        beds.Add(b);
                    }
                }
            }
            if (beds.TryRandomElement(out Building bed))
            {
                RaidVertical.VerticalDests++;
                return bed.Position;
            }
            List<Building> source = map.listerBuildings.allBuildingsColonist.FindAll(b => !b.def.building.ai_combatDangerous && !b.def.building.isInert && !b.def.building.ai_neverTrashThis && !b.IsClearableFreeBuilding);
            if (source.Count > 0)
            {
                for (int i = 0; i < 500; i++)
                {
                    IntVec3 c = source.RandomElement().RandomAdjacentCell8Way();
                    if (c.InBounds(map) && c.Walkable(map) && Ok(c))
                    {
                        RaidVertical.VerticalDests++;
                        return c;
                    }
                }
            }
            if (map.mapPawns.FreeColonistsSpawned.FindAll(x => Ok(x.Position)).TryRandomElement(out Pawn col))
            {
                RaidVertical.VerticalDests++;
                return col.Position;
            }
            return IntVec3.Invalid;
        }

        private static int CountBits(int x)
        {
            int n = 0;
            for (; x != 0; x &= x - 1)
            {
                n++;
            }
            return n;
        }
    }

    [HarmonyPatch(typeof(LordToil_AssaultColonySappers), nameof(LordToil_AssaultColonySappers.UpdateAllDuties))]
    public static class Sappers_Dest_Patch
    {
        public static void Prefix(LordToil_AssaultColonySappers __instance, out Lord __state)
        {
            __state = RaidDest.current;
            RaidDest.current = __instance.lord;
            RaidDest.sappers = true;
        }

        public static void Finalizer(Lord __state)
        {
            RaidDest.current = __state;
        }
    }

    [HarmonyPatch(typeof(LordToil_AssaultColonyBreaching), nameof(LordToil_AssaultColonyBreaching.UpdateAllDuties))]
    public static class Breaching_Dest_Patch
    {
        public static void Prefix(LordToil_AssaultColonyBreaching __instance, out Lord __state)
        {
            __state = RaidDest.current;
            RaidDest.current = __instance.lord;
            RaidDest.sappers = false;
        }

        // the game's breach path made (or kept): its segment for the vertical moves (BreachSegment)
        public static void Postfix(LordToil_AssaultColonyBreaching __instance)
        {
            BreachSegment.Update(__instance);
        }

        public static void Finalizer(Lord __state)
        {
            RaidDest.current = __state;
        }
    }

    // GenAI.RandomRaidDest for a sapper or breach lord on a map with floors: when the game finds no target the last
    // resort reaches, one reachable with the vertical moves (RaidDest.VerticalRaidDest). Every other caller, and a raid
    // that finds a real target, stays the game's.
    [HarmonyPatch(typeof(GenAI), nameof(GenAI.RandomRaidDest))]
    public static class RandomRaidDest_Vertical_Patch
    {
        public static void Postfix(IntVec3 raidSpawnLoc, Map map, ref IntVec3 __result)
        {
            Lord lord = RaidDest.current;
            if (lord == null || map == null || ProjectZ.Annex(map) == null || RaidClimb.Off || !FailSoft.IsApplied(nameof(CheckCellBasedReachability_Patch)))
            {
                return;
            }
            if (RaidDegrade.RealRaidDest(raidSpawnLoc, map))
            {
                ProjectZ.DebugLog($"{lord.faction} raid destination {__result}: a real target on foot");
                return;
            }
            IntVec3 v = RaidDest.VerticalRaidDest(lord, raidSpawnLoc, map, RaidDest.sappers);
            if (!v.IsValid)
            {
                ProjectZ.DebugLog($"{lord.faction} raid destination {__result}: no target even with the vertical moves (the game's random cell)");
            }
            if (v.IsValid)
            {
                ProjectZ.DebugLog($"{lord.faction} {(RaidDest.sappers ? "sapper" : "breach")} raid: no target on foot, {v} (level {Levels.LevelOf(v, map)}) with the vertical moves");
                __result = v;
            }
        }
    }

    // The breach lord's path when its destination needs vertical moves (user 2026-10-20: "vanilla breach path to the foot
    // of a vertical step + the vertical step + vanilla breach path on the next level"). The game makes one breach path
    // (BreachingGrid.CreateBreachPath: the last resort's path with cheap walls and its Perlin variation) from its start to
    // its destination; across levels that path is not found. Here, after the game's update: when the destination is in
    // another dig component than the breach start, the grid is made again, the game's way, from the lord's lead pawn to the
    // foot of the next vertical step of the raiders' plan (or to the destination once the lead is in its component), and
    // again whenever the lead reaches another component. The breachers clear that path; at its end the breach job has
    // nothing left and the climb (next in the Breaching duty) takes the vertical step (a blast, a ladder); the rest follow
    // as escorts. One segment record per lord, nothing saved (a loaded game makes it again).
    public static class BreachSegment
    {
        private sealed class Seg
        {
            public IntVec3 dest;
            public int fromComp;
            public bool toDest;   // made toward the destination (the lead in its component)
        }

        private static readonly Dictionary<int, Seg> segs = new Dictionary<int, Seg>();

        public static void Update(LordToil_AssaultColonyBreaching toil)
        {
            Lord lord = toil.lord;
            Map map = lord?.Map;
            LordToilData_AssaultColonyBreaching d = toil.Data;
            if (map == null || d == null || !d.breachDest.IsValid || ProjectZ.Annex(map) == null || RaidClimb.Off || lord.ownedPawns.Count == 0)
            {
                return;
            }
            RouteGraph g = RouteGraph.For(map);
            if (g == null)
            {
                return;
            }
            int destComp = ClimbPlanner.CompAt(g, d.breachDest, map);
            if (destComp == int.MinValue)
            {
                return;
            }
            segs.TryGetValue(lord.loadID, out Seg seg);
            // the lead: a pawn in the destination's component, else the highest
            Pawn lead = null;
            int leadComp = int.MinValue;
            foreach (Pawn p in lord.ownedPawns)
            {
                if (!p.Spawned || p.Downed || !RaidClimb.Climber(p))
                {
                    continue;   // (never an animal of the raid)
                }
                int c = ClimbPlanner.CompAt(g, p.Position, map);
                if (c == int.MinValue)
                {
                    continue;
                }
                if (c == destComp)
                {
                    lead = p;
                    leadComp = c;
                    break;
                }
                if (lead == null || Levels.LevelOf(p.Position, map) > Levels.LevelOf(lead.Position, map))
                {
                    lead = p;
                    leadComp = c;
                }
            }
            if (lead == null)
            {
                return;
            }
            int startComp = ClimbPlanner.CompAt(g, d.breachStart, map);
            if (seg == null && startComp == destComp)
            {
                return;   // the game's own breach path reaches it
            }
            bool toDest = leadComp == destComp;
            // (component ids change when a ladder joins two: the segment toward the destination is made once the lead is in
            // its component, the one toward a vertical step whenever the lead is in another component)
            if (seg != null && seg.dest == d.breachDest && seg.toDest == toDest && (toDest || seg.fromComp == leadComp))
            {
                return;   // this segment stands
            }
            IntVec3 end = d.breachDest;
            if (leadComp != destComp)
            {
                RaidVertical.PawnCost pc = RaidVertical.PawnCost.For(lead, false);
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                ClimbPlanner.Plan plan = ClimbPlanner.Find(map, g, lead, leadComp, false, d.breachDest, pc);
                RouteGraph.Spend((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                if (plan == null || plan.hops.Count == 0)
                {
                    return;
                }
                end = plan.hops[0].Stand;
            }
            int n = lord.ownedPawns.Count;
            int radius = UnityEngine.Mathf.RoundToInt(UnityEngine.Mathf.Lerp(1f, 2f, n / 120f));
            int margin = UnityEngine.Mathf.RoundToInt(UnityEngine.Mathf.Lerp(2f, 4f, n / 60f));
            IntVec3 start = SegStart(g, lead.Position, end, map);
            d.breachingGrid.Reset();
            d.currentTarget = null;
            d.breachStart = start;
            d.breachingGrid.CreateBreachPath(start, end, radius, margin, toil.useAvoidGrid);
            segs[lord.loadID] = new Seg { dest = d.breachDest, fromComp = leadComp, toDest = toDest };
            RaidVertical.BreachSegments++;
            ProjectZ.DebugLog($"{lord.faction} breach raid: breach path {start} -> {end} (level {Levels.LevelOf(end, map)}), destination {d.breachDest}, lead {lead} at {lead.Position}");
        }

        // The game's breach path runs on one level: from the lead when it is on the end's level, else from the top of the
        // link (a ladder, stairs) on that level in the same component nearest the end (the step the raiders take up).
        private static IntVec3 SegStart(RouteGraph g, IntVec3 lead, IntVec3 end, Map map)
        {
            int L = Levels.LevelOf(end, map);
            if (Levels.LevelOf(lead, map) == L)
            {
                return lead;
            }
            int comp = ClimbPlanner.CompAt(g, end, map);
            StairGrid sg = StairGrid.For(map);
            IntVec3 best = lead;
            int bestD = int.MaxValue;
            if (sg != null)
            {
                for (int i = 0; i < sg.links.Count; i++)
                {
                    VLink l = sg.links[i];
                    foreach (IntVec3 c in new[] { l.top, l.bottom })
                    {
                        // (raiders never ride a player elevator)
                        if (Levels.LevelOf(c, map) != L || !sg.Usable(l) || l.IsElevator || ClimbPlanner.CompAt(g, c, map) != comp)
                        {
                            continue;
                        }
                        int dd = (Levels.Ground(c, map) - Levels.Ground(end, map)).LengthHorizontalSquared;
                        if (dd < bestD)
                        {
                            bestD = dd;
                            best = c;
                        }
                    }
                }
            }
            return best;
        }
    }
}
