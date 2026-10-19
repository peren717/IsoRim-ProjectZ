using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    [DefOf]
    public static class PZMineDefOf
    {
        public static JobDef PZ_MineFromBelow;

        static PZMineDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(PZMineDefOf));
        }
    }

    // Tall mountains phase 3, mine up (isorim-pz-phase3-mineup-plan.md, "Revision: vanilla commands"). No new command:
    // the player marks rock on an upper level with vanilla Mine. Rock the pawns can dig on its own level (counting the
    // other rock marked with it) is vanilla mining there; rock they can't reach up there is mined from the cell right
    // below it (C, and its 8-ring), and the ceiling opens (a shaft, Shaft.Open). One deterministic rule decides per rock
    // (FromBelow); vanilla's Miner is not patched: it never sees a rock with no walkable neighbour on its own level, and
    // both jobs reserve the rock.
    public static class MineSide
    {
        private static readonly Dictionary<IntVec3, bool> memo = new Dictionary<IntVec3, bool>();
        private static int memoTick = -1;
        private static Map memoMap;
        private static readonly Queue<IntVec3> open = new Queue<IntVec3>();
        private static readonly HashSet<IntVec3> group = new HashSet<IntVec3>();
        private static readonly HashSet<Region> seenRegions = new HashSet<Region>();
        private static readonly List<IntVec3> stands = new List<IntVec3>();
        private static readonly List<IntVec3> goals = new List<IntVec3>();

        // the bound of the marked group's flood (a bigger block counts as reachable on its own level)
        public const int MaxGroup = 4096;

        // counters (tests)
        public static int Computed, LastGroup;

        // A marked rock (Mine or MineVein) on an upper level.
        public static bool Marked(IntVec3 c, Map map)
        {
            DesignationManager dm = map.designationManager;
            return dm.DesignationAt(c, DesignationDefOf.Mine) != null || dm.DesignationAt(c, DesignationDefOf.MineVein) != null;
        }

        // The rock at upper-level cell u is mined from below: the pawns can't walk to it (or to the rock marked with it) on
        // its own level from the cell below it, the cell below is no rock or wall, and someone can stand there or next to it.
        // Memoised per tick.
        public static bool FromBelow(IntVec3 u, Map map)
        {
            if (map == null || !u.IsValid || !Mountains.On(map))
            {
                return false;
            }
            int tick = Find.TickManager?.TicksGame ?? 0;
            if (tick != memoTick || !ReferenceEquals(map, memoMap))
            {
                memo.Clear();
                memoTick = tick;
                memoMap = map;
            }
            if (memo.TryGetValue(u, out bool r))
            {
                return r;
            }
            r = Compute(u, map);
            memo[u] = r;
            return r;
        }

        // (the memo dropped: a test changing the map inside one tick)
        public static void Forget() => memo.Clear();

        private static bool Compute(IntVec3 u, Map map)
        {
            Computed++;
            if (ProjectZ.FloorOf(u, map) < 1 || u.GetFirstMineable(map) == null)
            {
                return false;
            }
            IntVec3 c = FloorRoof.Below(u, map);
            if (!c.IsValid || !c.InBounds(map))
            {
                return false;
            }
            Building ce = c.GetEdifice(map);
            if (ce != null && ce.def.passability == Traversability.Impassable)
            {
                return false;   // rock or a wall below: mine that first
            }
            // where a pawn would stand: C, or a walkable cell of its 8-ring on its level (one per region)
            stands.Clear();
            seenRegions.Clear();
            for (int i = 0; i < 9; i++)
            {
                IntVec3 s = c + GenAdj.AdjacentCellsAndInside[i];
                if (s.InBounds(map) && ProjectZ.SameBand(s, c, map) && s.Walkable(map))
                {
                    Region reg = map.regionGrid.GetValidRegionAt_NoRebuild(s);
                    if (reg == null || seenRegions.Add(reg))
                    {
                        stands.Add(s);
                    }
                }
            }
            if (stands.Count == 0)
            {
                return false;
            }
            // the marked group: u and the marked rock 8-connected to it on its level
            group.Clear();
            open.Clear();
            group.Add(u);
            open.Enqueue(u);
            while (open.Count > 0 && group.Count < MaxGroup)
            {
                IntVec3 x = open.Dequeue();
                for (int i = 0; i < 8; i++)
                {
                    IntVec3 n = x + GenAdj.AdjacentCells[i];
                    if (n.InBounds(map) && !group.Contains(n) && ProjectZ.SameBand(n, u, map) && Marked(n, map) && n.GetFirstMineable(map) != null)
                    {
                        group.Add(n);
                        open.Enqueue(n);
                    }
                }
            }
            LastGroup = group.Count;
            if (group.Count >= MaxGroup)
            {
                open.Clear();
                group.Clear();
                return false;
            }
            // the walkable cells next to the group on its level (one per region)
            goals.Clear();
            seenRegions.Clear();
            foreach (IntVec3 x in group)
            {
                for (int i = 0; i < 8; i++)
                {
                    IntVec3 n = x + GenAdj.AdjacentCells[i];
                    if (n.InBounds(map) && !group.Contains(n) && ProjectZ.SameBand(n, u, map) && n.Walkable(map))
                    {
                        Region reg = map.regionGrid.GetValidRegionAt_NoRebuild(n);
                        if (reg == null || seenRegions.Add(reg))
                        {
                            goals.Add(n);
                        }
                    }
                }
            }
            group.Clear();
            bool own = false;
            if (goals.Count > 0)
            {
                // "can a pawn standing below walk to the rock's level and dig to it" (no pawn: doors passable, the
                // player's fog rule over floor links)
                TraverseParms tp = TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly, fogBlocked: true);
                for (int i = 0; i < stands.Count && !own; i++)
                {
                    for (int j = 0; j < goals.Count && !own; j++)
                    {
                        own = map.reachability.CanReach(stands[i], goals[j], PathEndMode.OnCell, tp);
                    }
                }
            }
            return !own;
        }
    }

    // Mine from below (plan R1.4): the cells under marked upper-level rock that FromBelow gives to this side. The pawn
    // works the cell's ceiling as vanilla's roof work does (ClosestTouch: on the cell or next to it), reserving the rock
    // and the cell's ceiling. No button: the player's order is vanilla Mine on the rock.
    public class WorkGiver_MineFromBelow : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

        public override Danger MaxPathDanger(Pawn pawn) => Danger.Deadly;

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            Map map = pawn.Map;
            return !Mountains.On(map) || !map.designationManager.AnySpawnedDesignationOfDef(DesignationDefOf.Mine) && !map.designationManager.AnySpawnedDesignationOfDef(DesignationDefOf.MineVein);
        }

        public override IEnumerable<IntVec3> PotentialWorkCellsGlobal(Pawn pawn)
        {
            Map map = pawn.Map;
            foreach (DesignationDef def in new[] { DesignationDefOf.Mine, DesignationDefOf.MineVein })
            {
                foreach (Designation d in map.designationManager.SpawnedDesignationsOfDef(def))
                {
                    IntVec3 u = d.target.Cell;
                    if (ProjectZ.FloorOf(u, map) >= 1)
                    {
                        IntVec3 c = FloorRoof.Below(u, map);
                        if (c.IsValid)
                        {
                            yield return c;
                        }
                    }
                }
            }
        }

        // The marked rock over cell c (null: none).
        public static Mineable RockAbove(IntVec3 c, Map map)
        {
            IntVec3 u = FloorRoof.Above(c, map);
            if (!u.IsValid || !MineSide.Marked(u, map))
            {
                return null;
            }
            return u.GetFirstMineable(map);
        }

        public override bool HasJobOnCell(Pawn pawn, IntVec3 c, bool forced = false)
        {
            Map map = pawn.Map;
            Mineable rock = RockAbove(c, map);
            if (rock == null || rock.IsForbidden(pawn) || !MineSide.FromBelow(rock.Position, map))
            {
                return false;
            }
            if (!pawn.CanReserve(rock, 1, -1, null, forced) || !pawn.CanReserve(c, 1, -1, ReservationLayerDefOf.Ceiling, forced))
            {
                return false;
            }
            return new HistoryEvent(HistoryEventDefOf.Mined, pawn.Named(HistoryEventArgsNames.Doer)).Notify_PawnAboutToDo_Job();
        }

        public override Job JobOnCell(Pawn pawn, IntVec3 c, bool forced = false)
        {
            Mineable rock = RockAbove(c, pawn.Map);
            if (rock == null)
            {
                return null;
            }
            Job job = JobMaker.MakeJob(PZMineDefOf.PZ_MineFromBelow, rock, c);
            job.expiryInterval = 20000;   // (as vanilla's mining job)
            job.checkOverrideOnExpire = true;
            return job;
        }
    }

    // The job (plan R2): A = the rock above, B = the cell under it. Vanilla's mining per pick hit (its own damage step,
    // so the 80/40 hits, the yield share, the last hit's DestroyMined and the vein flood are vanilla), the yield at the
    // miner's cell; then the ceiling opens (Shaft.Open). If the rule flips while working (stairs finished up there, a face
    // mined open) the job ends at the next hit and vanilla's Miner takes the rock (it keeps its hit points).
    public class JobDriver_MineFromBelow : JobDriver_Mine
    {
        private static readonly AccessTools.FieldRef<JobDriver_Mine, int> ticksRef = AccessTools.FieldRefAccess<JobDriver_Mine, int>("ticksToPickHit");

        private delegate void DoDamageFn(JobDriver_Mine driver, Thing target, Toil mine, Pawn actor, IntVec3 pos);

        private static readonly DoDamageFn doDamage = AccessTools.MethodDelegate<DoDamageFn>(AccessTools.Method(typeof(JobDriver_Mine), "DoDamage"));

        private Effecter dust;

        private Thing Rock => job.GetTarget(TargetIndex.A).Thing;

        private IntVec3 Cell => job.GetTarget(TargetIndex.B).Cell;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(Rock, job, 1, -1, null, errorOnFailed) && pawn.Reserve(Cell, job, 1, -1, ReservationLayerDefOf.Ceiling, errorOnFailed);
        }

        private void ResetTicks()
        {
            float speed = pawn.GetStatValue(StatDefOf.MiningSpeed);
            if (speed < 0.6f && pawn.Faction != Faction.OfPlayer)
            {
                speed = 0.6f;
            }
            ticksRef(this) = (int)Math.Round(100f / speed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOn(() => pawn.IsPlayerControlled && !job.ignoreDesignations && Rock != null && !MineSide.Marked(Rock.Position, Map));
            yield return Toils_Goto.Goto(TargetIndex.B, PathEndMode.ClosestTouch);
            Toil mine = ToilMaker.MakeToil("MineFromBelow");
            mine.tickIntervalAction = delegate (int delta)
            {
                Pawn actor = mine.actor;
                Thing rock = Rock;
                if (ticksRef(this) < -100)
                {
                    ResetTicks();
                }
                if (actor.skills != null && (rock.Faction != actor.Faction || actor.Faction == null))
                {
                    actor.skills.Learn(SkillDefOf.Mining, 0.07f * delta);
                }
                ticksRef(this) -= delta;
                if (ticksRef(this) > 0)
                {
                    return;
                }
                IntVec3 pos = rock.Position;
                Map map = Map;
                if (!MineSide.FromBelow(pos, map))
                {
                    // the rock is reachable on its own level now: vanilla mines it there
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                if (dust == null)
                {
                    dust = EffecterDefOf.Mine.Spawn();
                }
                dust.Trigger(actor, rock);
                ThingDef def = rock.def;
                MineUp.YieldEnter(actor.Position);
                try
                {
                    doDamage(this, rock, mine, actor, pos);
                }
                finally
                {
                    MineUp.YieldExit();
                }
                if (!rock.Destroyed)
                {
                    ResetTicks();
                    return;
                }
                map.mineStrikeManager.CheckStruckOre(pos, def, actor);
                actor.records.Increment(RecordDefOf.CellsMined);
                if (pawn.Faction == Faction.OfPlayer && MineStrikeManager.MineableIsVeryValuable(def))
                {
                    TaleRecorder.RecordTale(TaleDefOf.MinedValuable, pawn, def.building.mineableThing);
                }
                if (pawn.Faction == Faction.OfPlayer && MineStrikeManager.MineableIsValuable(def) && !map.IsPlayerHome)
                {
                    TaleRecorder.RecordTale(TaleDefOf.CaravanRemoteMining, pawn, def.building.mineableThing);
                }
                MineUp.MinedFromBelow++;
                Shaft.Open(Cell, map, actor);
                ReadyForNextToil();
            };
            mine.defaultCompleteMode = ToilCompleteMode.Never;
            mine.WithProgressBar(TargetIndex.B, () => Rock == null ? 1f : 1f - (float)Rock.HitPoints / Rock.MaxHitPoints);
            mine.FailOnCannotTouch(TargetIndex.B, PathEndMode.ClosestTouch);
            mine.activeSkill = () => SkillDefOf.Mining;
            mine.AddFinishAction(delegate
            {
                dust?.Cleanup();
                dust = null;
            });
            yield return mine;
        }
    }

    public static class MineUp
    {
        // counters (tests)
        public static int MinedFromBelow, YieldsMoved;

        // The yield of a rock mined from below lands at the miner's cell (plan §5): inside this scope the centre of every
        // near placement is the miner's cell (GenPlace_MineYield_Patch), so the same-floor rules keep it on the miner's floor.
        [ThreadStatic] private static int yieldDepth;
        [ThreadStatic] private static IntVec3 yieldAt;

        public static void YieldEnter(IntVec3 at)
        {
            yieldDepth++;
            yieldAt = at;
        }

        public static void YieldExit() => yieldDepth--;

        internal static bool YieldScope(out IntVec3 at)
        {
            at = yieldAt;
            return yieldDepth > 0;
        }
    }

    [HarmonyPatch(typeof(GenPlace), nameof(GenPlace.TryPlaceThing), new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(ThingPlaceMode), typeof(Thing), typeof(Action<Thing, int>), typeof(Predicate<IntVec3>), typeof(Rot4?), typeof(int) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal })]
    public static class GenPlace_MineYield_Patch
    {
        [HarmonyPriority(Priority.First)]
        public static void Prefix(ref IntVec3 center)
        {
            if (MineUp.YieldScope(out IntVec3 at))
            {
                center = at;
                MineUp.YieldsMoved++;
            }
        }
    }
}
