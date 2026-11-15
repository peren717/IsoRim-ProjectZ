using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // A marked floor that waits on cells nobody can work on (user 2026-11-14). The peel order (SlabPeel) hands out only
    // the farthest marked cells of a floor; when no colonist can take any of them (outside everyone's allowed area,
    // behind a forbidden door, ...) every other marked cell of that floor waits. The order stays as it is (skipping the
    // cell would let colonists cut off the far end); the player is told: Alert_FloorStalled.
    // "Can take it" is the game's own answer, never a list of reasons: for every player pawn whose work settings hold
    // the remove-foundation work giver (the work type is on), the work scan's own tests (Hands).
    public static class FloorStall
    {
        // JobGiver_Work's test of a work giver for a pawn (colonist or mech, work tags, capacities, ShouldSkip): its
        // own private method, asked. Missing (a game update): no alert, one warning.
        private static readonly Func<JobGiver_Work, Pawn, WorkGiver, bool> canUse = CanUseMethod();

        private static readonly List<IntVec3> next = new List<IntVec3>();
        private static readonly List<KeyValuePair<Pawn, WorkGiver_Scanner>> workers = new List<KeyValuePair<Pawn, WorkGiver_Scanner>>();
        private static readonly HashSet<int> moving = new HashSet<int>();

        // Set when a look threw (another mod's patch on the work giver or on JobGiver_Work, say): no more looks this
        // session, so the exception is not thrown again at every look (Alert_FloorStalled.Recheck, the one guard).
        public static bool Off { get; private set; }

        public static void Fail(Exception e)
        {
            Off = true;
            next.Clear();
            workers.Clear();
            moving.Clear();
            Log.Warning("[ProjectZ] The alert for a marked floor out of reach (Alert_FloorStalled) threw and is off for this session: " + e);
        }

        private static Func<JobGiver_Work, Pawn, WorkGiver, bool> CanUseMethod()
        {
            try
            {
                return AccessTools.MethodDelegate<Func<JobGiver_Work, Pawn, WorkGiver, bool>>(AccessTools.Method(typeof(JobGiver_Work), "PawnCanUseWorkGiver"));
            }
            catch (Exception e)
            {
                Log.Warning("[ProjectZ] The game's JobGiver_Work.PawnCanUseWorkGiver was not found: no alert for a marked floor out of reach. " + e.Message);
                return null;
            }
        }

        // The cells of map's stalled floors: a floor whose next cells (SlabPeel.Next) nobody takes. Nothing marked, no
        // floors on the map or nobody there who does this work at all: none.
        public static void Stuck(Map map, List<IntVec3> into)
        {
            if (Off || canUse == null || !map.designationManager.AnySpawnedDesignationOfDef(DesignationDefOf.RemoveFoundation))
            {
                return;
            }
            SlabPeel peel = SlabPeel.For(map);
            if (peel == null)
            {
                return;
            }
            next.Clear();
            peel.Next(next);
            if (next.Count == 0)
            {
                return;
            }
            Workers(map);
            if (workers.Count == 0)
            {
                return;
            }
            moving.Clear();
            int first = into.Count;
            for (int i = 0; i < next.Count; i++)
            {
                IntVec3 c = next[i];
                int k = ProjectZ.FloorOf(c, map);
                if (moving.Contains(k))
                {
                    continue;
                }
                if (Taken(c))
                {
                    moving.Add(k);
                    continue;
                }
                into.Add(c);
            }
            // (a floor with a cell somebody takes is not stalled: its other next cells have their turn after it)
            for (int i = into.Count - 1; i >= first; i--)
            {
                if (moving.Contains(ProjectZ.FloorOf(into[i], map)))
                {
                    into.RemoveAt(i);
                }
            }
            workers.Clear();
        }

        // The player's pawns on the map that do this work at all, each with its remove-foundation work giver: the
        // giver is in the pawn's own work lists (the work type is on) and JobGiver_Work would use it for the pawn.
        private static void Workers(Map map)
        {
            workers.Clear();
            List<Pawn> pawns = map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                Pawn_WorkSettings ws = pawn.workSettings;
                if (ws == null || !ws.EverWork)
                {
                    continue;
                }
                JobGiver_Work node = pawn.thinker?.TryGetMainTreeThinkNode<JobGiver_Work>();
                if (node == null)
                {
                    continue;
                }
                Add(pawn, node, ws.WorkGiversInOrderNormal);
                Add(pawn, node, ws.WorkGiversInOrderEmergency);
            }
        }

        private static void Add(Pawn pawn, JobGiver_Work node, List<WorkGiver> givers)
        {
            for (int i = 0; i < givers.Count; i++)
            {
                if (givers[i] is WorkGiver_ConstructRemoveFoundation giver && canUse(node, pawn, giver))
                {
                    workers.Add(new KeyValuePair<Pawn, WorkGiver_Scanner>(pawn, giver));
                }
            }
        }

        // Automatic work would hand the cell to one of the workers. The colonist who is at it or on the way is among
        // them: it holds the cell's floor reservation, which the work giver's own reserve test counts as free for its
        // holder, so reservations are not asked apart (any other reservation of the cell, hauling or meditating there,
        // says nothing about the floor).
        private static bool Taken(IntVec3 c)
        {
            for (int i = 0; i < workers.Count; i++)
            {
                if (Hands(workers[i].Key, workers[i].Value, c))
                {
                    return true;
                }
            }
            return false;
        }

        // Copied from RimWorld JobGiver_Work.TryIssueJobPackage (its ProcessCell: the three tests the work scan puts a
        // cell through; a local function, it cannot be called). Each test is the owner's own answer: the pawn's allowed
        // area, the work giver (unforced; ProjectZ's order and rules are its patches) and the pawn's reach. None of
        // them asks for the pawn's job or whether it is awake, so a busy or sleeping colonist counts as able. What the
        // pawn is doing still enters through the game's own rules, which are kept as they are:
        //   - the allowed area is not applied to a pawn that is drafted, in a mental state, a rebelling slave, an
        //     animal following its master, ... (ForbidUtility.CaresAboutForbidden: such a pawn counts as able), and a
        //     lord's squad flag can add to it;
        //   - reach is asked at the danger the work giver allows the pawn now (MaxPathDanger: any danger while its
        //     current job is a forced one, none for a pawn with a temperature injury that has a safe room).
        // Nothing is reserved and no job is made. Watched: CopyWatches.
        internal static bool Hands(Pawn pawn, WorkGiver_Scanner giver, IntVec3 c)
        {
            return !c.IsForbidden(pawn) && giver.HasJobOnCell(pawn, c) && (giver.AllowUnreachable || pawn.CanReach(c, giver.PathEndMode, giver.MaxPathDanger(pawn)));
        }
    }

    // "Marked floor out of reach": shown while a marked floor is stalled (FloorStall.Stuck); the culprits are the cells
    // it waits on (a click jumps there and shows their floor: FloorJump). The game finds the class by itself
    // (AlertsReadout) and asks it every 24 frames, and every frame while the mouse is over it.
    public class Alert_FloorStalled : Alert
    {
        // A cell counts once it has been stuck this many game ticks (10 seconds at normal speed): a busy or sleeping
        // worker still counts as able (FloorStall.Hands), but a worker can be off the map's grid for a moment (in a
        // jump, carried) or regions are being rebuilt; such moments never show the alert.
        public const int StallTicks = 600;

        // Looked at again at most once in this many frames (under the game's 24, so every one of its turns looks and
        // the calls while the mouse is over the alert cost nothing).
        public const int RecheckFrames = 20;

        private Dictionary<GlobalTargetInfo, int> since = new Dictionary<GlobalTargetInfo, int>();
        private Dictionary<GlobalTargetInfo, int> seen = new Dictionary<GlobalTargetInfo, int>();
        private readonly List<GlobalTargetInfo> culprits = new List<GlobalTargetInfo>();
        private readonly List<IntVec3> cells = new List<IntVec3>();
        private int checkedFrame = -RecheckFrames;

        public Alert_FloorStalled()
        {
            defaultLabel = "PZ_AlertFloorStalled".Translate();
            defaultExplanation = "PZ_AlertFloorStalledDesc".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override AlertReport GetReport()
        {
            int frame = Time.frameCount;
            if (frame - checkedFrame >= RecheckFrames)
            {
                checkedFrame = frame;
                Recheck();
            }
            return AlertReport.CulpritsAre(culprits);
        }

        // The one guard around the look: an exception (the work giver and JobGiver_Work are asked, other mods patch
        // them) turns the alert off for the session with one warning; the game catches it only on its own turn, not on
        // the calls while the mouse is over the alert.
        private void Recheck()
        {
            culprits.Clear();
            seen.Clear();
            if (FloorStall.Off)
            {
                since.Clear();
                return;
            }
            try
            {
                Look();
            }
            catch (Exception e)
            {
                FloorStall.Fail(e);
                culprits.Clear();
                cells.Clear();
                seen.Clear();
                since.Clear();
            }
        }

        private void Look()
        {
            int now = Find.TickManager.TicksGame;
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                cells.Clear();
                FloorStall.Stuck(maps[i], cells);
                for (int j = 0; j < cells.Count; j++)
                {
                    GlobalTargetInfo cell = new GlobalTargetInfo(cells[j], maps[i]);
                    // (first seen stuck now, or in an earlier look and at every look since)
                    int from = since.TryGetValue(cell, out int s) && s <= now ? s : now;
                    seen[cell] = from;
                    if (now - from >= StallTicks)
                    {
                        culprits.Add(cell);
                    }
                }
            }
            Dictionary<GlobalTargetInfo, int> old = since;
            since = seen;
            seen = old;
        }
    }
}
