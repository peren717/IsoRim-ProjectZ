using System;
using System.Collections.Generic;
using System.Diagnostics;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ
{
    // Raiders use ladders when the game's own last resort can't get them there (isorim-pz-siege-ladders-design.md, stage 2;
    // the vertical axis is just another direction). JobGiver_AIClimb sits right after JobGiver_AISapper in the duties that
    // end in it (AssaultColony, Sapper, PrisonerAssaultColony, HuntDownColonists, AssaultThing, PrisonerEscapeSapper), after
    // the walk in PrisonerEscape, and at the end of Breaching,
    // HuntEnemiesIndividual and the exit duties (ExitMap*, Kidnap, Steal, TakeWoundedGuest: Patches/PZ_RaidClimb.xml). It
    // acts only where the game's last resort found no route at all (TraverseMode.PassAllDestroyableThings: walls, doors
    // and rock count as passable), with the sapper's target rule: a player target within the game's 50 cells (3D distance,
    // Dist3D), or the Sapper duty's focus; for exits, a map edge on the ground. Raiders whose target is reachable never
    // get here: AISapper (and the nodes before it) take them, as without ProjectZ.
    // Who (MayClimb): humanlike raiders and mechanoids (a lift), hostile to the player, in a lord; escaping prisoners
    // (PrisonerEscape, PrisonerEscapeSapper); in the exit duties only, other factions' humanlike visitors, traders and
    // their guards. Never animals, mutants
    // (shamblers) or Anomaly entities.
    // Planner (ClimbPlanner): ladders still to be placed are virtual edges known only to it, never to reachability. Then:
    // walk to the ladder's near end the game's way (a PassAllDestroyableThings path, its first blocker cleared with the
    // game's DigUtility.PassBlockerJob, as AISapper does), place it there (JobDriver_PlaceSiegeLadder: from the foot going
    // up, from the top going down; a lift for mechanoids), and think again: the target is now reachable over the ladder,
    // so AISapper and the router take over (they climb it). No plan: the game's fallback (wander, give up, exit). A plan is
    // made once per lord and target and shared (ClimbPlans), inside the router's per-tick budget (RouteGraph.BudgetMs:
    // spent already, a short wander and think again).
    public class JobGiver_AIClimb : ThinkNode_JobGiver
    {
        public bool exit;
        public bool canMineNonMineables;

        public override ThinkNode DeepCopy(bool resolve = true)
        {
            JobGiver_AIClimb o = (JobGiver_AIClimb)base.DeepCopy(resolve);
            o.exit = exit;
            o.canMineNonMineables = canMineNonMineables;
            return o;
        }

        protected override Job TryGiveJob(Pawn pawn)
        {
            try
            {
                RaidClimb.Invoked++;
                return RaidClimb.TryGive(pawn, exit, exit || canMineNonMineables);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Raider ladder planning failed: " + e, 81512377);
                return null;
            }
        }
    }

    public static class RaidClimb
    {
        // test switch -projectznoraidladders: the job giver gives nothing (the game's behaviour)
        public static bool Off;

        // (tests) every plan is made a second time right away, for its timing
        public static bool RepeatTiming;

        public const int RadiusSq = 2500;    // JobGiver_AISapper's 50 cells
        public const int PlanTicks = 2500;   // a lord's plan is kept this long (while its first ladder's site stays good)
        public const int MemoTicks = 250;    // "no plan" remembered (as the router's memo)
        public const int WanderTicks = 30;   // over the per-tick budget: a short wander, then think again

        // counters (tests)
        public static int Invoked, Asked, Excluded, Reachable, NoTarget, Plans, PlanFails, MemoHits, Reused, BudgetWaits, PlaceJobs, BlockerJobs, VerticalJobs;
        public static double PlanMsTotal, PlanMsMax;
        public static readonly List<double> PlanMs = new List<double>();

        // Who may climb: a humanlike or a mechanoid, never an animal, a mutant (shambler) or an Anomaly entity.
        public static bool Climber(Pawn p)
        {
            if (p == null || p.RaceProps == null || p.Downed || p.Dead)
            {
                return false;
            }
            if (p.RaceProps.Animal || p.IsMutant || p.IsShambler || p.RaceProps.IsAnomalyEntity || p.IsEntity)
            {
                return false;
            }
            return p.RaceProps.Humanlike || p.RaceProps.IsMechanoid;
        }

        // Whose duty may climb (user 2026-10-20): raiders (hostile to the player) in every duty that has the climb; escaping
        // prisoners; and, in the exit duties only, other factions' humanlikes (visitors, traders and their guards) leaving.
        public static bool MayClimb(Pawn pawn, bool exit)
        {
            if (pawn.Faction == Faction.OfPlayer && !pawn.IsPrisoner)
            {
                return false;
            }
            if (pawn.IsPrisoner)
            {
                return PrisonBreakUtility.IsPrisonBreaking(pawn);
            }
            if (pawn.Faction == null)
            {
                return false;
            }
            if (pawn.HostileTo(Faction.OfPlayer))
            {
                return true;
            }
            return exit && pawn.RaceProps.Humanlike;
        }

        public static Job TryGive(Pawn pawn, bool exit, bool mineNonMineables)
        {
            if (Off || pawn == null || !pawn.Spawned)
            {
                return null;
            }
            Map map = pawn.Map;
            RouteGraph g = RouteGraph.For(map);
            if (g == null || !FailSoft.IsApplied(nameof(CheckCellBasedReachability_Patch)))
            {
                return null;
            }
            if (!Climber(pawn) || !MayClimb(pawn, exit))
            {
                Excluded++;
                return null;
            }
            Lord lord = pawn.GetLord();
            if (lord == null)
            {
                return null;
            }
            Asked++;
            ClimbPlans cp = ClimbPlans.For(map);
            if (cp == null)
            {
                return null;
            }
            // up on a ladder whose top it clears (stage 3): the next blow, the game's way
            LadderPerch.Rec perch = LadderPerch.Get(pawn);
            if (perch != null && perch.atTop)
            {
                Job cj = RaidVertical.ClearJob(pawn, perch);
                if (cj != null)
                {
                    return cj;
                }
            }
            if (map.regionDirtyer.AnyDirty)
            {
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            }
            int startComp = ClimbPlanner.CompAt(g, pawn.Position, map);
            if (startComp == int.MinValue)
            {
                return null;
            }
            // the goal: the sapper's target rule, or a ground edge
            int goalKey;
            IntVec3 goalCell = IntVec3.Invalid;
            if (exit)
            {
                if (ClimbPlanner.EdgeComps(g, map).Contains(startComp))
                {
                    Reachable++;
                    return null;   // the game's exit (or its fallback) has a way
                }
                goalKey = -1;
            }
            else
            {
                IntVec3 focus = pawn.mindState?.duty != null ? pawn.mindState.duty.focus.Cell : IntVec3.Invalid;
                if (focus.IsValid)
                {
                    if (Dist3D.DistSq(focus, pawn.Position, map) < 100 && focus.GetRoom(map) == pawn.GetRoom() && focus.WithinRegions(pawn.Position, map, 9, TraverseMode.NoPassClosedDoors))
                    {
                        return null;   // (AISapper's own test: at its duty spot)
                    }
                    if (ClimbPlanner.CompAt(g, focus, map) == startComp)
                    {
                        Reachable++;
                        return null;
                    }
                    goalCell = focus;
                    goalKey = focus.GetHashCode();
                }
                else
                {
                    Thing t = PickTarget(pawn, lord, g, startComp, cp, out bool anyReachable);
                    if (anyReachable)
                    {
                        Reachable++;
                        return null;
                    }
                    if (t == null)
                    {
                        NoTarget++;
                        return null;
                    }
                    goalCell = t.Position;
                    goalKey = t.thingIDNumber;
                }
            }
            RaidVertical.PawnCost pc = RaidVertical.PawnCost.For(pawn, mineNonMineables);
            goalKey = Gen.HashCombineInt(goalKey, pc.caps);   // (plans per lord, target and what the pawn can do)
            (int, int, int) key = (lord.loadID, goalKey, startComp);
            ClimbPlanner.Plan plan = cp.Get((lord.loadID, goalKey), map, g, startComp, out int step);
            if (plan != null)
            {
                Reused++;
            }
            else
            {
                step = 0;
                if (cp.Memo(key))
                {
                    MemoHits++;
                    return null;
                }
                if (RouteGraph.OverBudget)
                {
                    BudgetWaits++;
                    return JobMaker.MakeJob(JobDefOf.Wait_Wander, WanderTicks);
                }
                int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
                long t0 = Stopwatch.GetTimestamp();
                plan = ClimbPlanner.Find(map, g, pawn, startComp, exit, goalCell, pc);
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                RouteGraph.Spend(ms);
                Plans++;
                PlanMsTotal += ms;
                PlanMsMax = Math.Max(PlanMsMax, ms);
                if (PlanMs.Count < 20000)
                {
                    PlanMs.Add(ms);
                }
                string why = !Dev.TestRunner.Active ? null : $"in {ms:0.00} ms (sites {ClimbPlanner.LastSites}, components {ClimbPlanner.LastComps}, collect {ClimbPlanner.LastCollectMs:0.00}, search {ClimbPlanner.LastSearchMs:0.00}, legs {ClimbPlanner.LastRefines} {ClimbPlanner.LastLegMs:0.00} ms, expanded {ClimbPlanner.Expanded}, pushes {ClimbPlanner.Pushes}, relaxed {ClimbPlanner.Relaxed}, GCs meanwhile {GC.CollectionCount(0) - gc0}/{GC.CollectionCount(1) - gc1}/{GC.CollectionCount(2) - gc2})";
                if (Dev.TestRunner.Active && RepeatTiming)
                {
                    long t1 = Stopwatch.GetTimestamp();
                    ClimbPlanner.Find(map, g, pawn, startComp, exit, goalCell, pc);
                    why += $"; again at once {(Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency:0.00} ms (collect {ClimbPlanner.LastCollectMs:0.00}, search {ClimbPlanner.LastSearchMs:0.00}, legs {ClimbPlanner.LastLegMs:0.00}); a fixed loop {ClimbPlanner.Bench():0.00} ms";
                }
                if (plan == null)
                {
                    if (Dev.TestRunner.Active)
                    {
                        Log.Message($"[ProjectZ] no climb plan for {pawn} (lord {lord.loadID}) at {pawn.Position} to {(exit ? "a map edge" : goalCell.ToString())} {why}");
                    }
                    PlanFails++;
                    cp.AddMemo(key);
                    return null;
                }
                cp.Put((lord.loadID, goalKey), plan);
                if (Dev.TestRunner.Active)
                {
                    Log.Message($"[ProjectZ] climb plan for {pawn} (lord {lord.loadID}) at {pawn.Position}: {plan} {why}");
                }
            }
            ClimbPlanner.Hop h = plan.hops[step];
            IntVec3 stand = h.Stand;
            Building_SiegeLadder standing = h.kind == 1 ? ClimbPlanner.LadderAt(h.foot, h.top, map) : null;
            if (h.kind == 2)
            {
                // a ceiling to take off first: a blast from where the pawn can stand now, or the walk to the foot
                if (h.how == RaidVertical.Method.BlastRock || h.how == RaidVertical.Method.BlastSlab || h.how == RaidVertical.Method.DigSlab || pawn.Position == stand)
                {
                    Job rj = RaidVertical.RemovalJob(pawn, pc, h.how, h.foot, h.work);
                    if (rj != null)
                    {
                        VerticalJobs++;
                        return rj;
                    }
                    ProjectZ.DebugLog($"{pawn} at {pawn.Position}: no {h.how} job for the ceiling {h.work} over {h.foot} now");
                }
            }
            else if (standing != null && pawn.Position == stand)
            {
                // its ladder stands, the top still blocked: up it (one at a time), to clear the top from there
                if (LadderPerch.Busy(standing, pawn))
                {
                    return JobMaker.MakeJob(JobDefOf.Wait, 60, checkOverrideOnExpiry: true);
                }
                VerticalJobs++;
                RaidVertical.PerchJobs++;
                Job pj = JobMaker.MakeJob(PZVJobDefOf.PZ_LadderPerch, standing, h.top);
                pj.count = mineNonMineables ? 1 : 0;
                return pj;
            }
            if (pawn.Position != stand)
            {
                // the way to the near end, the game's last resort's way: its first blocker cleared (the planner's own
                // walk to it, when it ran one for this pawn just now)
                Thing b;
                IntVec3 before;
                if (!ClimbPlanner.LastLeg(pawn, stand, out b, out before))
                {
                    b = null;
                    before = IntVec3.Invalid;
                    using (PawnPath path = map.pathFinder.FindPathNow(pawn.Position, stand, TraverseParms.For(pawn, Danger.Deadly, TraverseMode.PassAllDestroyableThings)))
                    {
                        if (path != null && path.Found)
                        {
                            b = path.FirstBlockingBuilding(out before, pawn);
                        }
                    }
                }
                if (b != null)
                {
                    Job j = DigUtility.PassBlockerJob(pawn, b, before, true, mineNonMineables);
                    if (j != null)
                    {
                        BlockerJobs++;
                        return j;
                    }
                }
            }
            if (h.kind == 2)
            {
                // at the foot (or the walk there found no blocker) and nothing to do yet: the walk
                return pawn.Position == stand ? null : JobMaker.MakeJob(JobDefOf.Goto, stand, 500, checkOverrideOnExpiry: true);
            }
            if (standing != null)
            {
                return JobMaker.MakeJob(JobDefOf.Goto, stand, 500, checkOverrideOnExpiry: true);
            }
            PlaceJobs++;
            return JobDriver_PlaceSiegeLadder.Make(h.foot, h.top, stand);
        }

        // The sapper's target rule (JobGiver_AISapper): a player target within 50 cells (3D). The lord's current target
        // when this pawn has it in range, else one at random (the game's TryRandomElement). anyReachable: one of them can
        // be reached the last resort's way (AISapper's business, or none of ours in a duty without it).
        private static Thing PickTarget(Pawn pawn, Lord lord, RouteGraph g, int startComp, ClimbPlans cp, out bool anyReachable)
        {
            anyReachable = false;
            Map map = pawn.Map;
            List<IAttackTarget> all = map.attackTargetsCache.GetPotentialTargetsFor(pawn);
            List<Thing> cands = new List<Thing>();
            for (int i = 0; i < all.Count; i++)
            {
                IAttackTarget x = all[i];
                if (x.ThreatDisabled(pawn) || x.Thing.Faction != Faction.OfPlayer || Dist3D.DistSq(pawn.Position, x.Thing.Position, map) > RadiusSq)
                {
                    continue;
                }
                if (ClimbPlanner.CompAt(g, x.Thing.Position, map) == startComp)
                {
                    anyReachable = true;
                    return null;
                }
                cands.Add(x.Thing);
            }
            if (cands.Count == 0)
            {
                return null;
            }
            Thing kept = cp.LordTarget(lord.loadID, map);
            if (kept != null && cands.Contains(kept))
            {
                return kept;
            }
            Thing t = cands.RandomElement();
            cp.SetLordTarget(lord.loadID, t);
            return t;
        }

        public static string Stats()
        {
            List<double> s = new List<double>(PlanMs);
            s.Sort();
            double P(double q) => s.Count == 0 ? 0 : s[Mathf.Clamp((int)(q * (s.Count - 1)), 0, s.Count - 1)];
            return $"consulted {Invoked}, asked {Asked}, excluded {Excluded}, reachable {Reachable}, no target {NoTarget}, plans {Plans} (failed {PlanFails}), reused {Reused}, memo hits {MemoHits}, budget waits {BudgetWaits}, place jobs {PlaceJobs}, blocker jobs {BlockerJobs}, vertical jobs {VerticalJobs} (perch {RaidVertical.PerchJobs}, mine {RaidVertical.MineJobs}, blast {RaidVertical.BlastJobs}, dig {RaidVertical.DigJobs}; perches {RaidVertical.Perches}, stepped in {RaidVertical.PerchSteppedIn}, left {RaidVertical.PerchLeft}, fell {RaidVertical.PerchFalls}; slab hits blast {RaidVertical.SlabHitsBlast} dig {RaidVertical.SlabHitsDig}; rock blasts {RaidVertical.RockBlasts} opened {RaidVertical.RockBlastOpened}; ally waits {RaidVertical.AllyWaits}; vertical dests {RaidVertical.VerticalDests}, breach segments {RaidVertical.BreachSegments}); plan ms mean {(Plans == 0 ? 0 : PlanMsTotal / Plans):0.000}, median {P(0.5):0.000}, p95 {P(0.95):0.000}, max {PlanMsMax:0.000}";
        }

        public static void ResetStats()
        {
            Invoked = Asked = Excluded = Reachable = NoTarget = Plans = PlanFails = MemoHits = Reused = BudgetWaits = PlaceJobs = BlockerJobs = VerticalJobs = 0;
            RaidVertical.ResetStats();
            PlanMsTotal = PlanMsMax = 0;
            PlanMs.Clear();
        }
    }

    // A lord's plans, shared by its pawns: (lord, target) -> the plans made for it (a few, newest last), each kept
    // PlanTicks; a pawn takes the step of one whose ladder it can place from where it stands (its component), so the
    // lord's raiders converge on the same ladders, also after the first step. "No plan" remembered MemoTicks per (lord,
    // target, component); the lord's current target. Nothing is saved.
    public class ClimbPlans : MapComponent
    {
        public const int MaxPlans = 4;
        private readonly Dictionary<(int, int), List<(ClimbPlanner.Plan plan, int tick)>> plans = new Dictionary<(int, int), List<(ClimbPlanner.Plan, int)>>();
        private readonly Dictionary<(int, int, int), int> memo = new Dictionary<(int, int, int), int>();
        private readonly Dictionary<int, Thing> lordTargets = new Dictionary<int, Thing>();

        public ClimbPlans(Map map) : base(map)
        {
        }

        private static Map lastMap;
        private static ClimbPlans last;

        public static ClimbPlans For(Map map)
        {
            if (map == null)
            {
                return null;
            }
            if (!ReferenceEquals(map, lastMap))
            {
                last = map.components == null ? null : map.GetComponent<ClimbPlans>();
                lastMap = map;
            }
            return last;
        }

        public ClimbPlanner.Plan Get((int, int) key, Map map, RouteGraph g, int comp, out int step)
        {
            step = -1;
            if (!plans.TryGetValue(key, out var list))
            {
                return null;
            }
            int now = Find.TickManager.TicksGame;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (now - list[i].tick > RaidClimb.PlanTicks)
                {
                    list.RemoveAt(i);
                    continue;
                }
                step = list[i].plan.StepFor(map, g, comp);
                if (step >= 0)
                {
                    return list[i].plan;
                }
            }
            return null;
        }

        public void Put((int, int) key, ClimbPlanner.Plan plan)
        {
            if (!plans.TryGetValue(key, out var list))
            {
                plans[key] = list = new List<(ClimbPlanner.Plan, int)>();
            }
            list.Add((plan, Find.TickManager.TicksGame));
            if (list.Count > MaxPlans)
            {
                list.RemoveAt(0);
            }
        }

        public bool Memo((int, int, int) key)
        {
            if (memo.TryGetValue(key, out int until))
            {
                if (Find.TickManager.TicksGame < until)
                {
                    return true;
                }
                memo.Remove(key);
            }
            return false;
        }

        public void AddMemo((int, int, int) key) => memo[key] = Find.TickManager.TicksGame + RaidClimb.MemoTicks;

        public Thing LordTarget(int lord, Map map)
        {
            return lordTargets.TryGetValue(lord, out Thing t) && t != null && t.Spawned && t.Map == map ? t : null;
        }

        public void SetLordTarget(int lord, Thing t) => lordTargets[lord] = t;

        public void Clear()
        {
            plans.Clear();
            memo.Clear();
            lordTargets.Clear();
        }

        // Sapper and breach lords to turn into an ordinary assault (RaidDegrade), with the toil they were in.
        private readonly List<(Lord lord, LordToil from)> toAssault = new List<(Lord, LordToil)>();

        public void QueueAssault(Lord lord)
        {
            for (int i = 0; i < toAssault.Count; i++)
            {
                if (toAssault[i].lord == lord)
                {
                    return;
                }
            }
            toAssault.Add((lord, lord.CurLordToil));
        }

        public override void MapComponentTick()
        {
            if (toAssault.Count > 0)
            {
                List<(Lord, LordToil)> queued = new List<(Lord, LordToil)>(toAssault);
                toAssault.Clear();
                foreach ((Lord l, LordToil from) in queued)
                {
                    RaidDegrade.ToAssault(l, from);
                }
            }
            if ((Find.TickManager.TicksGame + map.uniqueID) % 2500 != 0 || plans.Count == 0 && memo.Count == 0)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            List<(int, int)> stalePlans = new List<(int, int)>();
            foreach (var kv in plans)
            {
                kv.Value.RemoveAll(e => now - e.tick > RaidClimb.PlanTicks);
                if (kv.Value.Count == 0)
                {
                    stalePlans.Add(kv.Key);
                }
            }
            foreach (var k in stalePlans)
            {
                plans.Remove(k);
            }
            List<(int, int, int)> stale = new List<(int, int, int)>();
            foreach (var kv in memo)
            {
                if (now >= kv.Value)
                {
                    stale.Add(kv.Key);
                }
            }
            foreach (var k in stale)
            {
                memo.Remove(k);
            }
            List<int> gone = new List<int>();
            foreach (var kv in lordTargets)
            {
                if (kv.Value == null || !kv.Value.Spawned)
                {
                    gone.Add(kv.Key);
                }
            }
            foreach (int k in gone)
            {
                lordTargets.Remove(k);
            }
        }
    }

    // The raiders' ladder planner. Nodes: the dig components (RouteGraph dig parts of PassAllDestroyableThings joined over
    // the links that stand: existing stairs and ladders are already inside a component). Virtual edges: ladder sites, a
    // top on an upper level (a walkable cell beside an open-air column, not a blocker in this stage) and its foot
    // (Column.Landing of that column), joining the top's component and the foot's; both ways. Candidates: the tops in the
    // start's component and the goal's, and in the components their ladders land in (to MaxDepth): "down" from the
    // raider's side, "up" into the target's side, valleys (a moat: down then up) and terraces between.
    // Cost in ticks: the walk (ground octile x the raider's move ticks, plus a stairs step per level within a component)
    // + 60 per new ladder + 180 per floor climbed. A* over the ladder ends; the chosen ladders are checked with the
    // placement rule (LadderSite.Check) and the walk to the first one is run for real (FindPathNow, the last resort's
    // mode); a dearer or failed leg makes it search again (MaxRefine).
    // Stage 3 (RaidVertical.cs), up only, only for the pawn that can take them (PawnCost; plans are kept per lord, target
    // and abilities): a ladder whose top is a blocker (kind 1: the blocker rims near the start or the goal, BlockR, and
    // ladders standing with their top still blocked; + the game's clearing time), and a ceiling taken off from below
    // first (kind 2: over the cells near the goal, RemR, and the start, RemStartR; + mining, blasting or digging time,
    // + the clearing of a blocked top beside it). A breach lord's Perlin noise adds NoiseTicks to the sites whose foot is
    // in it, as the game varies its breach path.
    public static class ClimbPlanner
    {
        public const int MaxDepth = 4;
        public const int MaxComps = 16;
        public const int MaxRefine = 3;
        public const float LegSlack = 1.3f;   // a real first leg up to this much over its estimate (terrain, a pawn in the way) is taken as it is

        public sealed class Site
        {
            public IntVec3 foot, top;
            public int span, footComp, topComp;
            public IntVec3 footG, topG;   // their ground cells
            public int footL, topL;
            public bool banned;
            // stage 3 (RaidVertical): 0 a ladder; 1 a ladder whose top is a blocker to clear from it; 2 a ceiling over
            // the foot to take off first (work = that cell), then a ladder (its top possibly a blocker). 1 and 2 go up only.
            public int kind;
            public bool existing;          // kind 1: its ladder stands already (the top still blocked)
            public IntVec3 work = IntVec3.Invalid;
            public RaidVertical.Removal removal;

            public bool UpOnly => kind != 0;
        }

        public struct Hop
        {
            public IntVec3 foot, top;
            public int span;
            public bool up;   // placed from the foot, climbed up (else from the top, climbed down)
            public int kind;
            public bool existing;
            public IntVec3 work;
            public RaidVertical.Removal removal;
            public RaidVertical.Method how;

            public IntVec3 Stand => up ? foot : top;
        }

        public sealed class Plan
        {
            public readonly List<Hop> hops = new List<Hop>();
            public float cost;

            // The first ladder can still be placed from the component the plan was made in.
            // The step of this plan a pawn in component comp takes next: the first ladder whose near end is in comp and whose
            // site is still good; -1 when none.
            public int StepFor(Map map, RouteGraph g, int comp)
            {
                for (int i = 0; i < hops.Count; i++)
                {
                    Hop h = hops[i];
                    if (CompAt(g, h.Stand, map) == comp)
                    {
                        return HopOk(h, map) ? i : -1;
                    }
                }
                return -1;
            }

            public override string ToString()
            {
                List<string> s = new List<string>();
                foreach (Hop h in hops)
                {
                    string what = h.kind == 2 ? $" after {h.how} of the {h.removal} at {h.work}" : h.kind == 1 ? (h.existing ? " (standing) top blocked" : " top blocked") : "";
                    s.Add($"{(h.up ? "up" : "down")} {h.span} at foot {h.foot} top {h.top}{what}");
                }
                return $"cost {cost:0}, " + string.Join("; ", s);
            }
        }

        // The ladder standing from foot to top, or null.
        public static Building_SiegeLadder LadderAt(IntVec3 foot, IntVec3 top, Map map)
        {
            StairGrid sg = StairGrid.For(map);
            if (sg == null)
            {
                return null;
            }
            for (int i = 0; i < sg.links.Count; i++)
            {
                VLink l = sg.links[i];
                if (l.IsLadder && l.bottom == foot && l.top == top)
                {
                    return l.thing as Building_SiegeLadder;
                }
            }
            return null;
        }

        // A plan's step can still be taken: its ladder site is good (a blocked top still clearable, or its ladder standing
        // with the top still blocked); a ceiling still there to take off.
        public static bool HopOk(Hop h, Map map)
        {
            if (h.kind == 2)
            {
                return RaidVertical.CeilingOver(h.foot, map, out IntVec3 u) == h.removal && u == h.work;
            }
            if (h.kind == 1)
            {
                if (LadderAt(h.foot, h.top, map) != null)
                {
                    return RaidVertical.ClearableTop(h.top, map);
                }
                return RaidVertical.ClearableTop(h.top, map) && LadderSite.Check(h.foot, h.top, map, out _, out _, true);
            }
            return LadderSite.Check(h.foot, h.top, map, out _, out _);
        }

        // The dig component (PassAllDestroyableThings) at c, or of a neighbour as the game's reachability takes a start
        // that can't be stood on; int.MinValue: none.
        public static int CompAt(RouteGraph g, IntVec3 c, Map map)
        {
            if (!c.IsValid || !c.InBounds(map))
            {
                return int.MinValue;
            }
            int p = g.DigPartAt(c, 0);
            for (int i = 0; i < 8 && p <= 0; i++)
            {
                IntVec3 n = c + GenAdj.AdjacentCells[i];
                if (n.InBounds(map) && ProjectZ.SameBand(n, c, map))
                {
                    p = g.DigPartAt(n, 0);
                }
            }
            return p <= 0 ? int.MinValue : g.DigCompOf(p, 0);
        }

        // ------------------------------------------------------------------------------------------------ edges of the ground

        private static readonly HashSet<int> edgeComps = new HashSet<int>();
        private static Map edgeMap;
        private static int edgeFor = -1;

        // The components holding a ground edge cell (the way off the map).
        public static HashSet<int> EdgeComps(RouteGraph g, Map map)
        {
            if (!ReferenceEquals(edgeMap, map) || edgeFor != g.anyVersion)
            {
                edgeComps.Clear();
                edgeMap = map;
                AnnexMapComponent a = ProjectZ.Annex(map);
                int w = map.Size.x, h = a.groundZ;
                for (int x = 0; x < w; x++)
                {
                    AddEdge(g, new IntVec3(x, 0, 0));
                    AddEdge(g, new IntVec3(x, 0, h - 1));
                }
                for (int z = 1; z < h - 1; z++)
                {
                    AddEdge(g, new IntVec3(0, 0, z));
                    AddEdge(g, new IntVec3(w - 1, 0, z));
                }
                edgeFor = g.anyVersion;
            }
            return edgeComps;
        }

        private static void AddEdge(RouteGraph g, IntVec3 c)
        {
            int p = g.DigPartAt(c, 0);
            if (p > 0)
            {
                edgeComps.Add(g.DigCompOf(p, 0));
            }
        }

        // ------------------------------------------------------------------------------------------------ rims

        // Per level: the walkable cells beside open air (on that level), grouped by dig part; kept per level version.
        private sealed class LevelRims
        {
            public int version = -1;
            public readonly Dictionary<int, List<IntVec3>> byPart = new Dictionary<int, List<IntVec3>>();
            // blockers beside open air (walls, doors, rock on a floor: RaidVertical.ClearableTop), by dig part
            public readonly Dictionary<int, List<IntVec3>> blockByPart = new Dictionary<int, List<IntVec3>>();
        }

        private static readonly Dictionary<int, LevelRims> rims = new Dictionary<int, LevelRims>();
        private static Map rimMap;
        public static int RimScans;
        public static double RimMs;

        private static LevelRims RimsOf(RouteGraph g, Map map, int L)
        {
            if (!ReferenceEquals(rimMap, map))
            {
                rims.Clear();
                rimMap = map;
            }
            if (!rims.TryGetValue(L, out LevelRims lr))
            {
                rims[L] = lr = new LevelRims();
            }
            int ver = g.LevelVersion(L);
            if (lr.version == ver)
            {
                return lr;
            }
            long t0 = Stopwatch.GetTimestamp();
            lr.byPart.Clear();
            lr.blockByPart.Clear();
            lr.version = ver;
            AnnexMapComponent a = ProjectZ.Annex(map);
            int w = map.Size.x, h = a.groundZ, shift = Levels.Shift(map, L);
            PathGrid pg = map.pathing.Normal.pathGrid;
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    IntVec3 c = new IntVec3(x, 0, z + shift);
                    bool block = false;
                    if (!pg.WalkableFast(c))
                    {
                        if (c.GetEdifice(map) == null)
                        {
                            continue;
                        }
                        block = true;
                    }
                    bool rim = false;
                    for (int d = 0; d < 4 && !rim; d++)
                    {
                        IntVec3 o = GenAdj.CardinalDirections[d];
                        int nx = x + o.x, nz = z + o.z;
                        if (nx < 0 || nz < 0 || nx >= w || nz >= h)
                        {
                            continue;
                        }
                        IntVec3 n = new IntVec3(nx, 0, nz + shift);
                        rim = !ProjectZ.HasFloor(n, map) && n.GetEdifice(map) == null;
                    }
                    if (!rim || block && !RaidVertical.ClearableTop(c, map))
                    {
                        continue;
                    }
                    int p = g.DigPartAt(c, 0);
                    if (p <= 0)
                    {
                        continue;
                    }
                    Dictionary<int, List<IntVec3>> into = block ? lr.blockByPart : lr.byPart;
                    if (!into.TryGetValue(p, out List<IntVec3> list))
                    {
                        into[p] = list = new List<IntVec3>();
                    }
                    list.Add(c);
                }
            }
            RimScans++;
            RimMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            return lr;
        }

        // ------------------------------------------------------------------------------------------------ sites

        private static readonly List<IntVec3> broken = new List<IntVec3>();

        // A ladder site from top t (level L) toward side d: the column beside it is open air down to a floor (no roof on
        // the way), the foot standable and free. Placement's other rules are checked on the chosen plan (LadderSite.Check).
        private static Site MakeSite(RouteGraph g, Map map, IntVec3 t, int L, int topComp, IntVec3 d)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            IntVec3 gr = Levels.Ground(t, map) + d;
            if (gr.x < 0 || gr.z < 0 || gr.x >= map.Size.x || gr.z >= a.groundZ)
            {
                return null;
            }
            IntVec3 col = Levels.Cell(gr, L, map);
            if (!col.IsValid || ProjectZ.HasFloor(col, map) || col.GetEdifice(map) != null)
            {
                return null;
            }
            IntVec3 land = Column.Landing(col, map, false, true, out int fallen, out _, broken);
            if (!land.IsValid || fallen < 1 || broken.Count > 0)
            {
                broken.Clear();
                return null;
            }
            if (!land.Standable(map) || land.GetEdifice(map) != null)
            {
                return null;
            }
            int fp = g.DigPartAt(land, 0);
            if (fp <= 0)
            {
                return null;
            }
            int fc = g.DigCompOf(fp, 0);
            if (fc == topComp)
            {
                return null;
            }
            return new Site { foot = land, top = t, span = fallen, footComp = fc, topComp = topComp, footG = Levels.Ground(land, map), topG = Levels.Ground(t, map), footL = Levels.LevelOf(land, map), topL = L, kind = t.Walkable(map) ? 0 : 1 };
        }

        // Stage 3 candidate limits (the mountain map stays fast): blocker tops within BlockR cells (ground) of the start or
        // the goal; ceilings over the cells within RemR of the goal's ground cell (and RemStartR of the start's).
        public const float BlockR = 14f;
        public const float RemR = 4f;
        public const float RemStartR = 1.5f;

        // The blocker-top sites with their top in component C near the start or the goal (made per plan: not kept), and
        // ladders standing there whose top is still blocked.
        private static void BlockerSitesWithTopIn(RouteGraph g, Map map, int C, IntVec3 startG, IntVec3 goalG, List<Site> into)
        {
            into.Clear();
            StairGrid sg = StairGrid.For(map);
            if (sg != null)
            {
                for (int i = 0; i < sg.links.Count; i++)
                {
                    VLink l = sg.links[i];
                    if (!l.IsLadder || !(l.thing is Building_SiegeLadder) || l.top.Walkable(map) || !RaidVertical.ClearableTop(l.top, map))
                    {
                        continue;
                    }
                    int tp = g.DigPartAt(l.top, 0);
                    int fp = g.DigPartAt(l.bottom, 0);
                    if (tp <= 0 || fp <= 0 || g.DigCompOf(tp, 0) != C)
                    {
                        continue;
                    }
                    int fc = g.DigCompOf(fp, 0);
                    if (fc == C)
                    {
                        continue;
                    }
                    into.Add(new Site { foot = l.bottom, top = l.top, span = l.span, footComp = fc, topComp = C, footG = Levels.Ground(l.bottom, map), topG = Levels.Ground(l.top, map), footL = Levels.LevelOf(l.bottom, map), topL = Levels.LevelOf(l.top, map), kind = 1, existing = true });
                }
            }
            float r2 = BlockR * BlockR;
            int lo = Levels.Min(map), hi = Levels.Max(map);
            for (int L = lo + 1; L <= hi; L++)
            {
                LevelRims lr = RimsOf(g, map, L);
                foreach (KeyValuePair<int, List<IntVec3>> kv in lr.blockByPart)
                {
                    if (g.DigCompOf(kv.Key, 0) != C)
                    {
                        continue;
                    }
                    List<IntVec3> cells = kv.Value;
                    for (int i = 0; i < cells.Count; i++)
                    {
                        IntVec3 cg = Levels.Ground(cells[i], map);
                        if ((!startG.IsValid || (cg - startG).LengthHorizontalSquared > r2) && (!goalG.IsValid || (cg - goalG).LengthHorizontalSquared > r2))
                        {
                            continue;
                        }
                        for (int d = 0; d < 4; d++)
                        {
                            Site s = MakeSite(g, map, cells[i], L, C, GenAdj.CardinalDirections[d]);
                            if (s != null)
                            {
                                s.kind = 1;
                                into.Add(s);
                            }
                        }
                    }
                }
            }
        }

        // The ceilings over cells of component C (on any level) near the goal and the start that a raider could take off
        // from below, each with the top beside it it leads to (RaidVertical.CeilingOver, TopBeside).
        private static void RemovalSitesFrom(RouteGraph g, Map map, int C, IntVec3 startG, IntVec3 goalG, List<Site> into)
        {
            into.Clear();
            int lo = Levels.Min(map), hi = Levels.Max(map);
            int sx = map.Size.x, gz = ProjectZ.Annex(map).groundZ;
            foreach ((IntVec3 centre, float radius) in new[] { (goalG, RemR), (startG, RemStartR) })
            {
                if (!centre.IsValid)
                {
                    continue;
                }
                int n = GenRadial.NumCellsInRadius(radius);
                for (int i = 0; i < n; i++)
                {
                    IntVec3 gr = centre + GenRadial.RadialPattern[i];
                    if (gr.x < 0 || gr.z < 0 || gr.x >= sx || gr.z >= gz)
                    {
                        continue;
                    }
                    for (int f = lo; f < hi; f++)
                    {
                        IntVec3 F = Levels.Cell(gr, f, map);
                        if (!F.IsValid || !F.InBounds(map))
                        {
                            continue;
                        }
                        int fp = g.DigPartAt(F, 0);
                        if (fp <= 0 || g.DigCompOf(fp, 0) != C)
                        {
                            continue;
                        }
                        Building fe = F.GetEdifice(map);
                        if (fe != null && !(fe is Mineable) || f > 0 && !ProjectZ.HasFloor(F, map) || Stairwell.StairCellAt(F, map))
                        {
                            continue;
                        }
                        RaidVertical.Removal r = RaidVertical.CeilingOver(F, map, out IntVec3 U);
                        if (r == RaidVertical.Removal.None || r == RaidVertical.Removal.Slab && !F.Standable(map))
                        {
                            continue;
                        }
                        IntVec3 T = RaidVertical.TopBeside(g, U, map, C, out int tc);
                        if (!T.IsValid)
                        {
                            continue;
                        }
                        int u = Levels.LevelOf(U, map);
                        into.Add(new Site { foot = F, top = T, span = u - f, footComp = C, topComp = tc, footG = gr, topG = Levels.Ground(T, map), footL = f, topL = u, kind = 2, work = U, removal = r });
                    }
                }
            }
        }

        private static readonly List<Site> tmpSites = new List<Site>();

        private static void AddSite(Site s, int depth)
        {
            long k = ((long)s.foot.x << 48) ^ ((long)s.foot.z << 32) ^ ((long)s.top.x << 16) ^ s.top.z ^ ((long)s.kind << 62);
            if (!seenSite.Add(k))
            {
                return;
            }
            s.banned = false;
            int id = sites.Count;
            sites.Add(s);
            Index(s.footComp, id, true);
            if (!s.UpOnly)
            {
                Index(s.topComp, id, false);
            }
            if (depth + 1 < MaxDepth)
            {
                if (!scanned.Contains(s.footComp))
                {
                    compQueue.Enqueue((s.footComp, depth + 1));
                }
                if (s.UpOnly && !scanned.Contains(s.topComp))
                {
                    compQueue.Enqueue((s.topComp, depth + 1));
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ search

        private static readonly List<Site> sites = new List<Site>();
        private static readonly Dictionary<int, List<(int site, bool fromFoot)>> byComp = new Dictionary<int, List<(int, bool)>>();
        private static readonly HashSet<int> scanned = new HashSet<int>();
        private static readonly Queue<(int comp, int depth)> compQueue = new Queue<(int, int)>();
        private static readonly HashSet<long> seenSite = new HashSet<long>();

        public static int LastSites, LastComps, LastRefines;
        public static double LastCollectMs, LastSearchMs, LastLegMs;

        private static double Ms(long t0) => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

        private static void Collect(RouteGraph g, Map map, int startComp, IEnumerable<int> goalComps, IntVec3 startG, IntVec3 goalG, bool vertical)
        {
            sites.Clear();
            byComp.Clear();
            scanned.Clear();
            compQueue.Clear();
            seenSite.Clear();
            compQueue.Enqueue((startComp, 0));
            foreach (int gc in goalComps)
            {
                compQueue.Enqueue((gc, 0));
            }
            if (!ReferenceEquals(compSitesMap, map) || compSitesFor != g.anyVersion)
            {
                compSites.Clear();
                compSitesMap = map;
                compSitesFor = g.anyVersion;
            }
            while (compQueue.Count > 0 && scanned.Count < MaxComps)
            {
                (int C, int depth) = compQueue.Dequeue();
                if (!scanned.Add(C))
                {
                    continue;
                }
                List<Site> list = SitesWithTopIn(g, map, C);
                for (int i = 0; i < list.Count; i++)
                {
                    AddSite(list[i], depth);
                }
                if (!vertical)
                {
                    continue;
                }
                BlockerSitesWithTopIn(g, map, C, startG, goalG, tmpSites);
                for (int i = 0; i < tmpSites.Count; i++)
                {
                    AddSite(tmpSites[i], depth);
                }
                if (goalG.IsValid)
                {
                    RemovalSitesFrom(g, map, C, startG, goalG, tmpSites);
                    for (int i = 0; i < tmpSites.Count; i++)
                    {
                        AddSite(tmpSites[i], depth);
                    }
                }
            }
            LastSites = sites.Count;
            LastComps = scanned.Count;
        }

        // The ladder sites whose top is in component C (from the level rims), kept while the graph's version stands.
        private static readonly Dictionary<int, List<Site>> compSites = new Dictionary<int, List<Site>>();
        private static Map compSitesMap;
        private static int compSitesFor = -1;

        private static List<Site> SitesWithTopIn(RouteGraph g, Map map, int C)
        {
            if (compSites.TryGetValue(C, out List<Site> list))
            {
                return list;
            }
            list = new List<Site>();
            int lo = Levels.Min(map), hi = Levels.Max(map);
            for (int L = lo + 1; L <= hi; L++)
            {
                LevelRims lr = RimsOf(g, map, L);
                foreach (KeyValuePair<int, List<IntVec3>> kv in lr.byPart)
                {
                    if (g.DigCompOf(kv.Key, 0) != C)
                    {
                        continue;
                    }
                    List<IntVec3> cells = kv.Value;
                    for (int i = 0; i < cells.Count; i++)
                    {
                        for (int d = 0; d < 4; d++)
                        {
                            Site s = MakeSite(g, map, cells[i], L, C, GenAdj.CardinalDirections[d]);
                            if (s != null)
                            {
                                list.Add(s);
                            }
                        }
                    }
                }
            }
            compSites[C] = list;
            return list;
        }

        private static void Index(int comp, int site, bool fromFoot)
        {
            if (!byComp.TryGetValue(comp, out List<(int, bool)> l))
            {
                byComp[comp] = l = new List<(int, bool)>();
            }
            l.Add((site, fromFoot));
        }

        private static int Octile(IntVec3 a, IntVec3 b, int tc, int td)
        {
            int dx = Math.Abs(a.x - b.x), dz = Math.Abs(a.z - b.z);
            int mn = Math.Min(dx, dz), mx = Math.Max(dx, dz);
            return (mx - mn) * tc + mn * td;
        }

        // The walk inside a component: ground octile, and a stairs step per level between (its links are inside).
        private static float Walk(Map map, IntVec3 a, IntVec3 b, int tc, int td)
        {
            return Octile(Levels.Ground(a, map), Levels.Ground(b, map), tc, td) + Math.Abs(Levels.LevelOf(a, map) - Levels.LevelOf(b, map)) * Climb.StepTicks;
        }


        public static float LadderCost(int span, bool existing = false) => (existing ? 0 : JobDriver_PlaceSiegeLadder.Ticks) + VLink.LadderTicksPerFloor * span;

        private static float[] dist = new float[0];
        private static int[] parentPt = new int[0];
        private static bool[] closed = new bool[0];
        public static int Expanded, Pushes, Relaxed;

        // (tests) a fixed amount of plain arithmetic, timed: tells a slow context from slow code
        public static double Bench()
        {
            long t0 = Stopwatch.GetTimestamp();
            long acc = 0;
            for (int i = 0; i < 2000000; i++)
            {
                acc += Octile(new IntVec3(i & 255, 0, i >> 8 & 255), new IntVec3(7, 0, 9), 13, 18);
            }
            BenchSink = acc;
            return Ms(t0);
        }

        public static long BenchSink;
        private static readonly PriorityQueue<int, float> open = new PriorityQueue<int, float>();

        // A* over points: 0 = the start, 1 + 2s = site s's foot reached, 2 + 2s = its top reached, goal = last.
        public const float NoiseTicks = 300f;
        private static float[] extra = new float[0];
        private static RaidVertical.Method[] hows = new RaidVertical.Method[0];

        // A site's cost over its ladder (the pawn's clearing or removal time; +inf: this pawn can't take it), per plan.
        private static void Extras(Map map, RaidVertical.PawnCost pc)
        {
            int n = sites.Count;
            if (extra.Length < n)
            {
                extra = new float[n];
                hows = new RaidVertical.Method[n];
            }
            for (int i = 0; i < n; i++)
            {
                Site s = sites[i];
                hows[i] = RaidVertical.Method.None;
                switch (s.kind)
                {
                    case 1:
                        extra[i] = pc.ClearTicks(s.top.GetEdifice(map));
                        break;
                    case 2:
                    {
                        float t = RaidVertical.RemovalTicks(pc, s.removal, s.foot, s.work, s.span, map, out RaidVertical.Method how);
                        if (how == RaidVertical.Method.BlastSlab || how == RaidVertical.Method.BlastRock && pc.blastRanged)
                        {
                            // (somewhere near the foot to blast from; else the pawn's other way, if any)
                            RouteGraph g = RouteGraph.For(map);
                            if (!RaidBlast.AnyStand(g, s.work, s.foot, s.footComp, how == RaidVertical.Method.BlastSlab, pc.explosionRadius, pc.blast.EffectiveRange, map))
                            {
                                t = float.PositiveInfinity;
                                if (how == RaidVertical.Method.BlastRock && pc.mine && s.span == 1)
                                {
                                    t = pc.MineTicks(s.work.GetEdifice(map)?.HitPoints ?? 1000, true);
                                    how = RaidVertical.Method.Mine;
                                }
                                else if (how == RaidVertical.Method.BlastSlab && pc.mineNonMineables && s.span == 1)
                                {
                                    t = RaidVertical.SlabHits(s.work, map) * pc.MineTicks(RaidVertical.SlabChunkHp(s.work, map), false);
                                    how = RaidVertical.Method.DigSlab;
                                }
                            }
                        }
                        if (!s.top.Walkable(map) && !float.IsInfinity(t))
                        {
                            t += pc.ClearTicks(s.top.GetEdifice(map));
                        }
                        extra[i] = t;
                        hows[i] = how;
                        break;
                    }
                    default:
                        extra[i] = 0f;
                        break;
                }
                if (pc.breach != null && !float.IsInfinity(extra[i]) && pc.breach.WithinNoise(s.foot))
                {
                    extra[i] += NoiseTicks;   // (the game's breach path pays its Perlin cost the same way)
                }
            }
        }

        private static List<(int site, bool up)> Search(Map map, IntVec3 start, int startComp, HashSet<int> goalComps, bool exit, IntVec3 goalCell, int tc, int td, Dictionary<int, float> firstLeg, out float total)
        {
            total = 0;
            int n = 2 + 2 * sites.Count;
            int goal = n - 1;
            if (dist.Length < n)
            {
                dist = new float[n];
                parentPt = new int[n];
            }
            for (int i = 0; i < n; i++)
            {
                dist[i] = float.MaxValue;
                parentPt[i] = -1;
            }
            if (closed.Length < n)
            {
                closed = new bool[n];
            }
            Array.Clear(closed, 0, n);
            open.Clear();
            dist[0] = 0;
            IntVec3 startG = Levels.Ground(start, map);
            int startL = Levels.LevelOf(start, map);
            IntVec3 goalG = goalCell.IsValid ? Levels.Ground(goalCell, map) : IntVec3.Invalid;
            int goalL = goalCell.IsValid ? Levels.LevelOf(goalCell, map) : 0;
            int sx = map.Size.x, gz = ProjectZ.Annex(map).groundZ;
            // A*: the heuristic is the walk left as if no ladder were needed (ground octile, a stairs step per level; to a
            // map edge: its straight distance), never more than the real rest (a ladder costs more than a step)
            float H(IntVec3 aG, int aL) => exit ? Math.Min(Math.Min(aG.x, sx - 1 - aG.x), Math.Min(aG.z, gz - 1 - aG.z)) * tc + aL * Climb.StepTicks : Octile(aG, goalG, tc, td) + Math.Abs(aL - goalL) * Climb.StepTicks;
            open.Enqueue(0, H(startG, startL));
            Expanded = 0;
            Pushes = 0;
            Relaxed = 0;
            while (open.TryDequeue(out int u, out float _))
            {
                if (closed[u])
                {
                    continue;
                }
                closed[u] = true;
                if (u == goal)
                {
                    break;
                }
                Expanded++;
                float du = dist[u];
                IntVec3 atG;
                int atL, comp;
                if (u == 0)
                {
                    atG = startG;
                    atL = startL;
                    comp = startComp;
                }
                else
                {
                    Site s = sites[(u - 1) / 2];
                    bool atTop = (u - 1) % 2 == 1;
                    atG = atTop ? s.topG : s.footG;
                    atL = atTop ? s.topL : s.footL;
                    comp = atTop ? s.topComp : s.footComp;
                }
                if (goalComps.Contains(comp))
                {
                    float fin = du + H(atG, atL);
                    if (fin < dist[goal])
                    {
                        dist[goal] = fin;
                        parentPt[goal] = u;
                        open.Enqueue(goal, fin);
                    }
                }
                if (!byComp.TryGetValue(comp, out List<(int site, bool fromFoot)> list))
                {
                    continue;
                }
                for (int i = 0; i < list.Count; i++)
                {
                    (int si, bool fromFoot) = list[i];
                    Relaxed++;
                    Site s = sites[si];
                    if (s.banned)
                    {
                        continue;
                    }
                    int v = fromFoot ? 2 + 2 * si : 1 + 2 * si;
                    if (closed[v])
                    {
                        continue;
                    }
                    float walk = u == 0 && firstLeg.TryGetValue(si * 2 + (fromFoot ? 0 : 1), out float real) ? real : Octile(atG, fromFoot ? s.footG : s.topG, tc, td) + Math.Abs(atL - (fromFoot ? s.footL : s.topL)) * Climb.StepTicks;
                    if (float.IsInfinity(extra[si]))
                    {
                        continue;
                    }
                    float nd = du + walk + LadderCost(s.span, s.existing) + extra[si];
                    if (nd < dist[v])
                    {
                        dist[v] = nd;
                        parentPt[v] = u;
                        open.Enqueue(v, nd + (fromFoot ? H(s.topG, s.topL) : H(s.footG, s.footL)));
                        Pushes++;
                    }
                }
            }
            if (dist[goal] == float.MaxValue)
            {
                return null;
            }
            total = dist[goal];
            List<(int, bool)> hops = new List<(int, bool)>();
            for (int v = parentPt[goal]; v > 0; v = parentPt[v])
            {
                int si = (v - 1) / 2;
                bool arrivedTop = (v - 1) % 2 == 1;
                hops.Add((si, arrivedTop));
            }
            hops.Reverse();
            return hops;
        }

        private static readonly HashSet<int> goalSet = new HashSet<int>();

        // A plan for pawn from its component to the goal (a cell, or a ground edge), or null.
        // (tests) -projectznovertical: stage 3's edges left out
        public static bool NoVertical;

        public static Plan Find(Map map, RouteGraph g, Pawn pawn, int startComp, bool exit, IntVec3 goalCell, RaidVertical.PawnCost pc = null)
        {
            goalSet.Clear();
            if (exit)
            {
                foreach (int c in EdgeComps(g, map))
                {
                    goalSet.Add(c);
                }
            }
            else
            {
                int gc = CompAt(g, goalCell, map);
                if (gc == int.MinValue)
                {
                    return null;
                }
                goalSet.Add(gc);
            }
            if (goalSet.Count == 0 || goalSet.Contains(startComp))
            {
                return null;
            }
            long tc0 = Stopwatch.GetTimestamp();
            LastSearchMs = LastLegMs = 0;
            pc ??= RaidVertical.PawnCost.For(pawn, false);
            Collect(g, map, startComp, goalSet, Levels.Ground(pawn.Position, map), goalCell.IsValid ? Levels.Ground(goalCell, map) : IntVec3.Invalid, !NoVertical);
            Extras(map, pc);
            LastCollectMs = Ms(tc0);
            if (sites.Count == 0)
            {
                return null;
            }
            StairRouter.MoveTicks(TraverseParms.For(pawn), out int tc, out int td);
            Dictionary<int, float> firstLeg = new Dictionary<int, float>();
            LastRefines = 0;
            for (int iter = 0; iter < 3 * MaxRefine; iter++)
            {
                long ts0 = Stopwatch.GetTimestamp();
                List<(int site, bool up)> r = Search(map, pawn.Position, startComp, goalSet, exit, goalCell, tc, td, firstLeg, out float total);
                LastSearchMs += Ms(ts0);
                if (r == null || r.Count == 0)
                {
                    return null;
                }
                // the placement rule for every ladder of it
                bool bad = false;
                foreach (var (si, _) in r)
                {
                    Site s = sites[si];
                    bool ok = s.kind == 2 || s.kind == 1 && s.existing || LadderSite.Check(s.foot, s.top, map, out int span, out _, s.kind == 1) && span == s.span;
                    if (!ok)
                    {
                        s.banned = true;
                        bad = true;
                    }
                }
                if (bad)
                {
                    continue;
                }
                // the walk to the first one, for real (the last resort's mode)
                (int f, bool fUp) = r[0];
                Site fs = sites[f];
                int fk = f * 2 + (fUp ? 0 : 1);
                IntVec3 stand = fUp ? fs.foot : fs.top;
                if (!firstLeg.ContainsKey(fk) && LastRefines < MaxRefine)
                {
                    LastRefines++;
                    float est = Walk(map, pawn.Position, stand, tc, td);
                    float real;
                    if (pawn.Position == stand)
                    {
                        real = 0;
                    }
                    else
                    {
                        long tl0 = Stopwatch.GetTimestamp();
                        using (PawnPath p = map.pathFinder.FindPathNow(pawn.Position, stand, TraverseParms.For(pawn, Danger.Deadly, TraverseMode.PassAllDestroyableThings)))
                        {
                            real = p != null && p.Found ? p.TotalCost : -1f;
                            if (real >= 0)
                            {
                                Thing b = p.FirstBlockingBuilding(out IntVec3 before, pawn);
                                legBlock[fk] = (b, before);
                            }
                            LastLegMs += Ms(tl0);
                        }
                    }
                    if (real < 0)
                    {
                        fs.banned = true;
                        continue;
                    }
                    firstLeg[fk] = real;
                    if (real > est * LegSlack + 30f)
                    {
                        continue;   // dearer than its estimate: the cheapest plan may be another one now
                    }
                }
                Plan plan = new Plan { cost = total };
                foreach ((int si, bool up) in r)
                {
                    Site s = sites[si];
                    plan.hops.Add(new Hop { foot = s.foot, top = s.top, span = s.span, up = up, kind = s.kind, existing = s.existing, work = s.work, removal = s.removal, how = hows[si] });
                }
                lastLegPawn = legBlock.TryGetValue(fk, out var lb) ? pawn : null;
                lastLegAt = pawn.Position;
                lastLegStand = stand;
                lastLegTick = Verse.Find.TickManager.TicksGame;
                lastLegBlocker = lb.b;
                lastLegBefore = lb.before;
                legBlock.Clear();
                return plan;
            }
            legBlock.Clear();
            return null;
        }

        private static readonly Dictionary<int, (Thing b, IntVec3 before)> legBlock = new Dictionary<int, (Thing, IntVec3)>();
        private static Pawn lastLegPawn;
        private static IntVec3 lastLegAt, lastLegStand, lastLegBefore;
        private static int lastLegTick = -1;
        private static Thing lastLegBlocker;

        // The first blocker on the walk the planner ran for this pawn to this ladder end just now (same tick, same cell).
        public static bool LastLeg(Pawn pawn, IntVec3 stand, out Thing blocker, out IntVec3 before)
        {
            blocker = null;
            before = IntVec3.Invalid;
            if (lastLegPawn != pawn || lastLegStand != stand || lastLegAt != pawn.Position || lastLegTick != Verse.Find.TickManager.TicksGame)
            {
                return false;
            }
            lastLegPawn = null;
            blocker = lastLegBlocker != null && lastLegBlocker.Spawned ? lastLegBlocker : null;
            before = lastLegBefore;
            return true;
        }
    }
}
