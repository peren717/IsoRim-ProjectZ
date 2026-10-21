using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // Pawns in the middle of a link step (isorim-pz-siege-ladders-design.md, stage 1). One record per step, from the
    // moment the path follower sets it up (SetupMoveIntoNextCell) until the pawn stands on the other end or the step ends:
    //   - ladders: one pawn at a time. The first pawn whose step pays time on a ladder holds it ("climbing"); any other
    //     pawn stepping onto that ladder, either way, waits at its end (its step pays nothing) until the ladder is free.
    //     A ladder step pays one tick per tick, whatever its length (the game caps a step at 450 ticks).
    //   - a climbing pawn's job is not overridden (the job tracker's expiry and constant think tree, CheckForJobOverride).
    //   - the step ends early: downed (or the link gone) -> the pawn falls from the height it had reached to the bottom
    //     (floors fallen = the part climbed, the one fall rule: Gravity.Bruise -> JumpGrid.Landed, hurt from 2 floors);
    //     dead -> the corpse comes down to the bottom; ended otherwise (a forced job) -> it stays at its own end.
    //   - the link gone (LinkGone, from StairGrid.Deregister): climbers fall at once, waiting pawns' paths fail. Stairs
    //     too (their steps are one floor: a fall of under a floor only moves the pawn to the bottom).
    //   - a powered elevator (Elevator.cs) is no ladder: any number ride at once, each step pays one tick per tick; a rider
    //     is not overridden either; downed or dead in the car, it gets out at the nearer end of its ride (no fall); the
    //     elevator gone, it falls down the shaft like a climber. Without power its links are ladders (ElevatorLadder).
    // Nothing is saved: a pawn saved mid-step starts its step again (TryResumePathingAfterLoading_Patch).
    public class LinkSteps : MapComponent
    {
        public sealed class Step
        {
            public Pawn pawn;
            public VLink link;
            public IntVec3 from, to;
            public bool up;
            public bool climbing;
            public float height;     // floors over the bottom, last seen
            public Pawn carried;
        }

        public readonly List<Step> steps = new List<Step>();

        // counters (tests)
        public static int Falls, FallFloors, Waits, CorpsesLowered, Stopped;

        public LinkSteps(Map map) : base(map)
        {
        }

        private static Map lastMap;
        private static LinkSteps last;

        public static LinkSteps For(Map map)
        {
            if (map == null)
            {
                return null;
            }
            if (!ReferenceEquals(map, lastMap))
            {
                last = map.components == null ? null : map.GetComponent<LinkSteps>();
                lastMap = map;
            }
            return last;
        }

        public Step Of(Pawn p)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i].pawn == p)
                {
                    return steps[i];
                }
            }
            return null;
        }

        // The pawn holding a ladder (climbing it), or null.
        public Pawn Holder(Thing ladder)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                Step st = steps[i];
                if (st.climbing && st.link.IsLadder && st.link.thing == ladder && st.pawn.Spawned && st.pawn.Position == st.from && st.pawn.pather.nextCell == st.to)
                {
                    return st.pawn;
                }
            }
            return null;
        }

        // A pawn on a ladder step, holding it (its job is not to be overridden).
        public static bool Climbing(Pawn p)
        {
            if (p?.pather == null || !p.Spawned)
            {
                return false;
            }
            LinkSteps s = For(p.Map);
            if (s == null || s.steps.Count == 0)
            {
                return false;
            }
            Step st = s.Of(p);
            return st != null && st.climbing && (st.link.IsLadder || st.link.IsElevator) && !p.Downed && !p.Dead && p.pather.Moving && p.pather.nextCell == st.to && p.Position == st.from;
        }

        // The path follower set up a link step.
        public void Began(Pawn p, VLink l)
        {
            Step st = Of(p);
            if (st == null)
            {
                st = new Step { pawn = p };
                steps.Add(st);
            }
            st.link = l;
            st.from = p.Position;
            st.to = p.pather.nextCell;
            st.up = st.to == l.top;
            st.climbing = !l.IsLadder;
            st.height = st.up ? 0 : l.span;
            st.carried = p.carryTracker?.CarriedThing as Pawn;
            ClearEnd(st, p);   // (at once: before the path follower sees the pawn there and looks for another way)
        }

        // The time a step pays this tick (CostToPayThisTick): a ladder step 1 when the pawn holds the ladder (taking it when
        // free), else 0 (waiting at its end). -1: not ours (the game's own cost).
        public float Pay(Pawn p)
        {
            Step st = Of(p);
            if (st == null || !(st.link.IsLadder || st.link.IsElevator) || p.pather.nextCell != st.to || p.Position != st.from)
            {
                return -1f;
            }
            if (!st.climbing)
            {
                Pawn h = Holder(st.link.thing);
                if (h != null && h != p)
                {
                    Waits++;
                    return 0f;
                }
                st.climbing = true;
            }
            return 1f;
        }

        public override void MapComponentTick()
        {
            if (steps.Count == 0)
            {
                return;
            }
            for (int i = steps.Count - 1; i >= 0; i--)
            {
                Step st = steps[i];
                Pawn p = st.pawn;
                if (p == null || p.Destroyed && !p.Dead)
                {
                    steps.RemoveAt(i);
                    continue;
                }
                if (p.Dead)
                {
                    steps.RemoveAt(i);
                    if (st.climbing && st.link.kind == LinkKind.Elevator && StairGrid.UsableIn(st.link, map))
                    {
                        CarCorpse(st);
                    }
                    else if (st.climbing)
                    {
                        LowerCorpse(st);
                    }
                    continue;
                }
                if (!p.Spawned || p.Map != map)
                {
                    steps.RemoveAt(i);
                    continue;
                }
                if (p.pather != null && p.pather.Moving && p.pather.nextCell == st.to && p.Position == st.from)
                {
                    if (st.climbing)
                    {
                        st.height = Height(st, p.pather.MovePercentage);
                    }
                    ClearEnd(st, p);
                    continue;
                }
                steps.RemoveAt(i);
                if (p.Position == st.to)
                {
                    continue;   // arrived
                }
                if (st.climbing && p.Downed && st.link.kind == LinkKind.Elevator && StairGrid.UsableIn(st.link, map))
                {
                    GetOut(st);
                }
                else if (st.climbing && (p.Downed || !StairGrid.UsableIn(st.link, map)))
                {
                    Fall(st);
                }
            }
        }

        // The climber has priority at the end of its ladder (user 2026-10-20): a pawn on that end cell that would stop it
        // (the game's collision: a hostile, or a pawn on a collide-with-pawns job) steps aside to a free neighbour, as a
        // pawn on a top that opens does (Shaft.StepAside). Only for the pawn that holds the ladder or may take it now (a
        // pawn waiting for it pushes no one); otherwise both would wait for each other at its two ends.
        private void ClearEnd(Step st, Pawn p)
        {
            if (!(st.link.IsLadder || st.link.IsElevator) || !st.to.InBounds(map) || !PawnUtility.ShouldCollideWithPawns(p))
            {
                return;
            }
            if (!st.climbing)
            {
                Pawn h = Holder(st.link.thing);
                if (h != null && h != p)
                {
                    return;
                }
            }
            for (int n = 0; n < 4; n++)
            {
                Pawn b = PawnUtility.PawnBlockingPathAt(st.to, p);
                if (b == null || b == p)
                {
                    return;
                }
                IntVec3 was = b.Position;
                Shaft.StepAside(b, st.to, map);
                if (b.Position == was)
                {
                    return;   // nowhere free to step to
                }
                EndsCleared++;
            }
        }

        public static int EndsCleared;

        private static float Height(Step st, float pct)
        {
            if (st.link.IsElevator)
            {
                return Links.ElevatorHeight(st.up, st.link, pct);
            }
            if (st.link.IsLadder)
            {
                return Links.LadderHeight(st.up ? StepKind.LadderUp : StepKind.LadderDown, st.link, pct);
            }
            return st.up ? pct * st.link.span : (1f - pct) * st.link.span;
        }

        // The pawn comes down to the bottom from the height it had reached (the one fall rule).
        private void Fall(Step st)
        {
            Pawn p = st.pawn;
            // (an elevator's rider falls down the shaft to its lowest stop, the floors under its ride counted too)
            IntVec3 bottom = ElevatorRule.ShaftBottom(st.link, map, out int below);
            int fallen = Mathf.FloorToInt(st.height + 0.001f) + below;
            Falls++;
            FallFloors += fallen;
            ProjectZ.DebugLog($"{p} falls off {st.link.thing} ({st.link.kind}) from {st.height:0.00} floors ({fallen} counted), at {p.Position} -> {bottom}");
            if (p.Position != bottom && bottom.InBounds(map))
            {
                p.Position = bottom;
            }
            p.Notify_Teleported(endCurrentJob: true, resetTweenedPos: true);
            Gravity.Bruise(p, fallen);
            // a pawn it was carrying and dropped up there comes down too
            Pawn c = st.carried;
            if (c != null && c.Spawned && !c.Dead && c.Map == map && c.ParentHolder is Map && Levels.LevelOf(c.Position, map) != Levels.LevelOf(bottom, map))
            {
                IntVec3 to = CellFinder.StandableCellNear(bottom, map, 2f);
                c.Position = to.IsValid ? to : bottom;
                c.Notify_Teleported(endCurrentJob: true, resetTweenedPos: true);
                Gravity.Bruise(c, fallen);
            }
        }

        // Downed in a powered elevator: out at the nearer end of the ride, unhurt.
        private void GetOut(Step st)
        {
            Pawn p = st.pawn;
            IntVec3 at = st.height * 2f >= st.link.span ? st.link.top : st.link.bottom;
            CarExits++;
            ProjectZ.DebugLog($"{p} downed in {st.link.thing} at {st.height:0.00} floors: out at {at}");
            if (p.Position != at && at.InBounds(map))
            {
                p.Position = at;
            }
            p.Notify_Teleported(endCurrentJob: true, resetTweenedPos: true);
        }

        private void CarCorpse(Step st)
        {
            Corpse corpse = st.pawn.Corpse;
            IntVec3 at = st.height * 2f >= st.link.span ? st.link.top : st.link.bottom;
            if (corpse != null && corpse.Spawned && corpse.Map == map && corpse.Position != at && at.InBounds(map))
            {
                corpse.Position = at;
                CarExits++;
            }
        }

        public static int CarExits;

        private void LowerCorpse(Step st)
        {
            Corpse corpse = st.pawn.Corpse;
            IntVec3 bottom = ElevatorRule.ShaftBottom(st.link, map, out _);
            if (corpse != null && corpse.Spawned && corpse.Map == map && corpse.Position != bottom && bottom.InBounds(map))
            {
                corpse.Position = bottom;
                CorpsesLowered++;
            }
        }

        private static readonly Action<Pawn_PathFollower> PatherFailed = AccessTools.MethodDelegate<Action<Pawn_PathFollower>>(AccessTools.Method(typeof(Pawn_PathFollower), "PatherFailed"));

        internal static void FailPath(Pawn p)
        {
            Stopped++;
            PatherFailed(p.pather);
        }

        // A link went (StairGrid.Deregister): its climbers fall now; pawns waiting on it, or about to step onto it, give up
        // their path (the job asks again).
        public static void LinkGone(Map map, Building thing) => LinkGone(map, thing, IntVec3.Invalid, IntVec3.Invalid);

        // Only the link of thing with these ends (Invalid: every link of it).
        public static void LinkGone(Map map, Building thing, IntVec3 bottom, IntVec3 top)
        {
            LinkSteps s = For(map);
            if (s == null || s.steps.Count == 0)
            {
                return;
            }
            for (int i = s.steps.Count - 1; i >= 0; i--)
            {
                Step st = s.steps[i];
                if (st.link.thing != thing || bottom.IsValid && (st.link.bottom != bottom || st.link.top != top))
                {
                    continue;
                }
                s.steps.RemoveAt(i);
                Pawn p = st.pawn;
                // (only a pawn still on that step: a record left by a pawn moved away since, e.g. teleported, is dropped)
                if (p == null || p.Dead || !p.Spawned || p.Map != map || p.Position != st.from || p.pather == null || !p.pather.Moving || p.pather.nextCell != st.to)
                {
                    continue;
                }
                if (st.climbing)
                {
                    st.height = Height(st, p.pather.MovePercentage);
                    s.Fall(st);
                }
                else
                {
                    FailPath(p);
                }
            }
        }
    }
}

namespace ProjectZ.Patches
{
    // A link step pays its own time (LinkSteps.Pay): a ladder holder 1 per tick (no 450-tick cap), a pawn waiting for the
    // ladder nothing.
    [HarmonyPatch(typeof(Pawn_PathFollower), "CostToPayThisTick")]
    [PatchGroup(FailSoft.StairRouting)]
    public static class CostToPayThisTick_Ladder_Patch
    {
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> PawnRef = AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");

        public static void Postfix(Pawn_PathFollower __instance, ref float __result)
        {
            Pawn p = PawnRef(__instance);
            LinkSteps s = p?.Map == null ? null : LinkSteps.For(p.Map);
            if (s == null || s.steps.Count == 0)
            {
                return;
            }
            float pay = s.Pay(p);
            if (pay >= 0f)
            {
                __result = pay;
            }
        }
    }

    // A climber's job is not overridden (the design: only downed, dead or the ladder gone end a climb).
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.CheckForJobOverride))]
    [PatchGroup(FailSoft.StairRouting)]
    public static class CheckForJobOverride_Ladder_Patch
    {
        private static readonly AccessTools.FieldRef<Pawn_JobTracker, Pawn> PawnRef = AccessTools.FieldRefAccess<Pawn_JobTracker, Pawn>("pawn");

        public static bool Prefix(Pawn_JobTracker __instance)
        {
            if (LinkSteps.Climbing(PawnRef(__instance)))
            {
                Dev.PZStats.climbOverridesHeld++;
                return false;
            }
            return true;
        }
    }

    // ... nor ended by its expiry (Goto's expiryInterval) or replaced from the constant think tree: while climbing, only the
    // job driver ticks.
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.JobTrackerTickInterval))]
    [PatchGroup(FailSoft.StairRouting)]
    public static class JobTrackerTickInterval_Ladder_Patch
    {
        private static readonly AccessTools.FieldRef<Pawn_JobTracker, Pawn> PawnRef = AccessTools.FieldRefAccess<Pawn_JobTracker, Pawn>("pawn");

        public static bool Prefix(Pawn_JobTracker __instance, int delta)
        {
            if (!LinkSteps.Climbing(PawnRef(__instance)))
            {
                return true;
            }
            __instance.curDriver?.DriverTickInterval(delta);
            return false;
        }
    }
}
