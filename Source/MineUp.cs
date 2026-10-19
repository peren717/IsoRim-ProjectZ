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
        public static JobDef PZ_MineFromAbove;

        static PZMineDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(PZMineDefOf));
        }
    }

    // Where a marked rock is mined from: its own level (vanilla), the cell right below it (mine up) or the cell right
    // above it (mine down).
    public enum MineDir
    {
        Own,
        FromBelow,
        FromAbove
    }

    // Tall mountains, vertical mining (isorim-pz-phase3-mineup-plan.md "Revision: vanilla commands"; mine down: the
    // mirror). No new command: the player marks rock with vanilla Mine on any level. Rock the pawns can dig on its own
    // level (counting the other rock marked with it) is vanilla mining there; rock they can't reach there is mined from the
    // cell right below it (C and its 8-ring; its floor opens, mine up) or, where no one can stand below, from the cell right
    // above it (its ceiling opens, mine down). Both open one boundary with Shaft.Open. One deterministic rule decides per
    // rock (Get): own level, else from below, else from above. Stairs go into the cells once they are open (vanilla
    // placement: no blueprint on rock or in the fog); a pit dug from above is built from its rim (StairTouch).
    // Vanilla's Miner leaves rock given to another level (Miner_OwnLevelOnly_Patch); every
    // mining job reserves the rock.
    public static class MineSide
    {
        private static readonly Dictionary<IntVec3, MineDir> memo = new Dictionary<IntVec3, MineDir>();
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

        // test only (-projectzminenogroup): each rock decided alone (no marked group), to show the trap the group rule closes
        public static bool NoGroup;

        // A marked rock (Mine or MineVein).
        public static bool Marked(IntVec3 c, Map map)
        {
            DesignationManager dm = map.designationManager;
            return dm.DesignationAt(c, DesignationDefOf.Mine) != null || dm.DesignationAt(c, DesignationDefOf.MineVein) != null;
        }

        public static bool FromBelow(IntVec3 u, Map map) => Get(u, map) == MineDir.FromBelow;

        public static bool FromAbove(IntVec3 u, Map map) => Get(u, map) == MineDir.FromAbove;

        // The side the rock at u is mined from. Memoised per tick.
        public static MineDir Get(IntVec3 u, Map map)
        {
            if (map == null || !u.IsValid || !Mountains.On(map))
            {
                return MineDir.Own;
            }
            int tick = Find.TickManager?.TicksGame ?? 0;
            if (tick != memoTick || !ReferenceEquals(map, memoMap))
            {
                memo.Clear();
                memoTick = tick;
                memoMap = map;
            }
            if (memo.TryGetValue(u, out MineDir r))
            {
                return r;
            }
            r = Compute(u, map);
            memo[u] = r;
            return r;
        }

        // (the memo dropped: a test changing the map inside one tick)
        public static void Forget() => memo.Clear();

        // Walkable cells on c's level, c and its 8-ring, one per region.
        private static void Stands(IntVec3 c, Map map, List<IntVec3> into)
        {
            for (int i = 0; i < 9; i++)
            {
                IntVec3 s = c + GenAdj.AdjacentCellsAndInside[i];
                if (s.InBounds(map) && ProjectZ.SameBand(s, c, map) && s.Walkable(map))
                {
                    Region reg = map.regionGrid.GetValidRegionAt_NoRebuild(s);
                    if (reg == null || seenRegions.Add(reg))
                    {
                        into.Add(s);
                    }
                }
            }
        }

        // A pawn can walk to a cell next to the rock on its level and back (no one-way jump on the way).
        public static bool WalksToAndBack(Pawn pawn, IntVec3 rock)
        {
            Map map = pawn.Map;
            TraverseParms tp = TraverseParms.For(pawn, Danger.Deadly);
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = rock + GenAdj.AdjacentCells[i];
                if (n.InBounds(map) && ProjectZ.SameBand(n, rock, map) && n.Walkable(map) && TwoWay(map, pawn.Position, n, tp))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TwoWay(Map map, IntVec3 a, IntVec3 b, TraverseParms tp)
        {
            return map.reachability.CanReach(a, b, PathEndMode.OnCell, tp) && map.reachability.CanReach(b, a, PathEndMode.OnCell, tp);
        }

        private static MineDir Compute(IntVec3 u, Map map)
        {
            Computed++;
            if (u.GetFirstMineable(map) == null || ProjectZ.FloorOf(u, map) < 0)
            {
                return MineDir.Own;
            }
            stands.Clear();
            seenRegions.Clear();
            // below: the cell under it, no rock or wall there (mine that first), and someone can stand there or next to it
            int below = 0;
            IntVec3 c = FloorRoof.Below(u, map);
            if (c.IsValid && c.InBounds(map))
            {
                Building ce = c.GetEdifice(map);
                if (ce == null || ce.def.passability != Traversability.Impassable)
                {
                    Stands(c, map, stands);
                    below = stands.Count;
                }
            }
            // above: the top over it can open (a bare natural floor, nothing built on it), and someone can stand there or
            // next to it. Never the ground surface: only rock is mined, and there is no level under the ground.
            IntVec3 a = FloorRoof.Above(u, map);
            if (a.IsValid && Hole.TopBlocked(a, map) == null)
            {
                Stands(a, map, stands);
            }
            if (stands.Count == 0)
            {
                return MineDir.Own;
            }
            // the marked group: u and the marked rock 8-connected to it on its level
            group.Clear();
            open.Clear();
            group.Add(u);
            open.Enqueue(u);
            while (!NoGroup && open.Count > 0 && group.Count < MaxGroup)
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
                return MineDir.Own;
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
                // "can a pawn standing below or above walk to the rock's level and dig to it, and walk back" (no pawn:
                // doors passable, the player's fog rule over floor links). Both ways: a jump down into a pit is no way
                // to the rock (the pawn could not come back up).
                TraverseParms tp = TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly, fogBlocked: true);
                for (int i = 0; i < stands.Count && !own; i++)
                {
                    for (int j = 0; j < goals.Count && !own; j++)
                    {
                        own = TwoWay(map, stands[i], goals[j], tp);
                    }
                }
                // (or colonists reach the rock's level there and back anyway: a face on the ground under an unexplored top; as
                // vanilla's CanReachColony, by the colonists, but only those not stuck where they are: who can reach the map
                // edge, so a colonist fallen into a pit is no way in for the rock next to it)
                // (as the colonists walk: through the fog)
                TraverseParms walk = TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly);
                var cols = map.mapPawns.FreeColonistsSpawned;
                for (int i = 0; i < cols.Count && !own; i++)
                {
                    bool free = false;
                    for (int j = 0; j < goals.Count && !own; j++)
                    {
                        if (TwoWay(map, cols[i].Position, goals[j], walk))
                        {
                            free = free || map.reachability.CanReachMapEdge(cols[i].Position, walk);
                            own = free;
                        }
                    }
                }
            }
            if (own)
            {
                return MineDir.Own;
            }
            return below > 0 ? MineDir.FromBelow : MineDir.FromAbove;
        }
    }

    // Vertical mining jobs (plan R1.4, mirrored for mine down): the cells next to marked rock on the level below it (from
    // below) or above it (from above) that MineSide gives to that side. The pawn works the boundary as vanilla's roof work
    // does (ClosestTouch: on the cell or next to it; from above it stands next to it, not on the top that opens), reserving
    // the rock and the ceiling of the lower cell. No button: the player's order is vanilla Mine on the rock.
    public abstract class WorkGiver_MineVertical : WorkGiver_Scanner
    {
        protected abstract MineDir Dir { get; }

        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

        public override Danger MaxPathDanger(Pawn pawn) => Danger.Deadly;

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            Map map = pawn.Map;
            return !Mountains.On(map) || !map.designationManager.AnySpawnedDesignationOfDef(DesignationDefOf.Mine) && !map.designationManager.AnySpawnedDesignationOfDef(DesignationDefOf.MineVein);
        }

        // The cell the miner works from for the rock at u (right below or right above it), and back.
        public static IntVec3 WorkCell(IntVec3 u, Map map, MineDir dir) => dir == MineDir.FromBelow ? FloorRoof.Below(u, map) : FloorRoof.Above(u, map);

        public static IntVec3 RockCell(IntVec3 c, Map map, MineDir dir) => dir == MineDir.FromBelow ? FloorRoof.Above(c, map) : FloorRoof.Below(c, map);

        public override IEnumerable<IntVec3> PotentialWorkCellsGlobal(Pawn pawn)
        {
            Map map = pawn.Map;
            foreach (DesignationDef def in new[] { DesignationDefOf.Mine, DesignationDefOf.MineVein })
            {
                foreach (Designation d in map.designationManager.SpawnedDesignationsOfDef(def))
                {
                    IntVec3 c = WorkCell(d.target.Cell, map, Dir);
                    if (c.IsValid)
                    {
                        yield return c;
                    }
                }
            }
        }

        // The marked rock worked from cell c (null: none).
        public static Mineable MarkedRock(IntVec3 c, Map map, MineDir dir)
        {
            IntVec3 u = RockCell(c, map, dir);
            if (!u.IsValid || !MineSide.Marked(u, map))
            {
                return null;
            }
            return u.GetFirstMineable(map);
        }

        // (mine up: the marked rock over a cell)
        public static Mineable RockAbove(IntVec3 c, Map map) => MarkedRock(c, map, MineDir.FromBelow);

        public override bool HasJobOnCell(Pawn pawn, IntVec3 c, bool forced = false)
        {
            Map map = pawn.Map;
            Mineable rock = MarkedRock(c, map, Dir);
            if (rock == null || rock.IsForbidden(pawn) || MineSide.Get(rock.Position, map) != Dir)
            {
                return false;
            }
            if (!MineVertical.CanReserve(pawn, rock, c, forced) || Dir == MineDir.FromAbove && !MineVertical.StandFor(pawn, c, map).IsValid)
            {
                return false;
            }
            return new HistoryEvent(HistoryEventDefOf.Mined, pawn.Named(HistoryEventArgsNames.Doer)).Notify_PawnAboutToDo_Job();
        }

        public override Job JobOnCell(Pawn pawn, IntVec3 c, bool forced = false)
        {
            Mineable rock = MarkedRock(c, pawn.Map, Dir);
            return rock == null ? null : MineVertical.MakeJob(pawn, rock, c);
        }
    }

    public class WorkGiver_MineFromBelow : WorkGiver_MineVertical
    {
        protected override MineDir Dir => MineDir.FromBelow;
    }

    public class WorkGiver_MineFromAbove : WorkGiver_MineVertical
    {
        protected override MineDir Dir => MineDir.FromAbove;
    }

    public static class MineVertical
    {
        // The lower cell of the boundary a vertical job opens (its ceiling): the work cell from below, the rock's from above.
        public static IntVec3 Lower(IntVec3 rock, IntVec3 work, Map map) => ProjectZ.FloorOf(work, map) < ProjectZ.FloorOf(rock, map) ? work : rock;

        public static bool CanReserve(Pawn pawn, Thing rock, IntVec3 work, bool forced)
        {
            return pawn.CanReserve(rock, 1, -1, null, forced) && pawn.CanReserve(Lower(rock.Position, work, pawn.Map), 1, -1, ReservationLayerDefOf.Ceiling, forced);
        }

        // A = the rock, B = the work cell; from above C = where the miner stands: next to the top that opens (nearest the
        // pawn, cells over other rock about to open last), else on it.
        public static Job MakeJob(Pawn pawn, Thing rock, IntVec3 work)
        {
            Map map = pawn.Map;
            bool down = ProjectZ.FloorOf(work, map) > ProjectZ.FloorOf(rock.Position, map);
            Job job;
            if (down)
            {
                IntVec3 stand = StandFor(pawn, work, map);
                if (!stand.IsValid)
                {
                    return null;
                }
                job = JobMaker.MakeJob(PZMineDefOf.PZ_MineFromAbove, rock, work, stand);
            }
            else
            {
                job = JobMaker.MakeJob(PZMineDefOf.PZ_MineFromBelow, rock, work);
            }
            job.expiryInterval = 20000;   // (as vanilla's mining job)
            job.checkOverrideOnExpire = true;
            return job;
        }

        public static IntVec3 StandFor(Pawn pawn, IntVec3 top, Map map)
        {
            IntVec3 best = IntVec3.Invalid;
            int bestScore = int.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = top + GenAdj.AdjacentCells[i];
                if (!n.InBounds(map) || !ProjectZ.SameBand(n, top, map) || !n.Standable(map) || !pawn.CanReach(n, PathEndMode.OnCell, Danger.Deadly))
                {
                    continue;
                }
                int score = (n - pawn.Position).LengthHorizontalSquared;
                IntVec3 under = FloorRoof.Below(n, map);
                if (under.IsValid && under.GetFirstMineable(map) != null && MineSide.Marked(under, map))
                {
                    score += 100000;   // (its own top opens later)
                }
                if (score < bestScore)
                {
                    bestScore = score;
                    best = n;
                }
            }
            if (!best.IsValid && top.Standable(map) && pawn.CanReach(top, PathEndMode.OnCell, Danger.Deadly))
            {
                best = top;
            }
            return best;
        }
    }

    // The other half of "one side per rock": vanilla's Miner leaves a marked rock that MineSide gives to another level,
    // unless this pawn can walk to the rock and back on its own level (a pawn already up there, plan R1.3: the vanilla
    // outcome). Without this a rock next to a pit dug from above counts as reachable for vanilla (a jump down is a path),
    // and the miner would jump into the pit and be stuck there.
    [HarmonyPatch(typeof(WorkGiver_Miner), nameof(WorkGiver_Miner.JobOnThing))]
    public static class Miner_OwnLevelOnly_Patch
    {
        public static void Postfix(Pawn pawn, Thing t, ref Job __result)
        {
            if (__result != null && t is Mineable && pawn?.Map != null && Mountains.On(pawn.Map) && MineSide.Get(t.Position, pawn.Map) != MineDir.Own
                && !MineSide.WalksToAndBack(pawn, t.Position))
            {
                __result = null;
            }
        }
    }

    // The job (plan R2, both ways): A = the rock, B = the work cell (below it: mine up; above it: mine down), C = where a
    // miner from above stands. Vanilla's mining per pick hit (its own damage step, so the 80/40 hits, the yield share, the
    // last hit's DestroyMined and the vein flood are vanilla), the yield at the miner's cell; then the boundary between the
    // two levels opens (Shaft.Open on the lower cell). If the rule flips while working (stairs finished, a face mined open)
    // the job ends at the next hit and the other side takes the rock (it keeps its hit points).
    public class JobDriver_MineVertical : JobDriver_Mine
    {
        private static readonly AccessTools.FieldRef<JobDriver_Mine, int> ticksRef = AccessTools.FieldRefAccess<JobDriver_Mine, int>("ticksToPickHit");

        private delegate void DoDamageFn(JobDriver_Mine driver, Thing target, Toil mine, Pawn actor, IntVec3 pos);

        private static readonly DoDamageFn doDamage = AccessTools.MethodDelegate<DoDamageFn>(AccessTools.Method(typeof(JobDriver_Mine), "DoDamage"));

        private Effecter dust;

        private Thing Rock => job.GetTarget(TargetIndex.A).Thing;

        private IntVec3 Cell => job.GetTarget(TargetIndex.B).Cell;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(Rock, job, 1, -1, null, errorOnFailed) && pawn.Reserve(MineVertical.Lower(Rock.Position, Cell, Map), job, 1, -1, ReservationLayerDefOf.Ceiling, errorOnFailed);
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
            bool down = ProjectZ.FloorOf(Cell, Map) > ProjectZ.FloorOf(Rock.Position, Map);
            MineDir side = down ? MineDir.FromAbove : MineDir.FromBelow;
            if (down && job.GetTarget(TargetIndex.C).IsValid)
            {
                yield return Toils_Goto.GotoCell(TargetIndex.C, PathEndMode.OnCell);
            }
            else
            {
                yield return Toils_Goto.Goto(TargetIndex.B, PathEndMode.ClosestTouch);
            }
            Toil mine = ToilMaker.MakeToil("MineVertical");
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
                if (MineSide.Get(pos, map) != side)
                {
                    // the rule gave the rock to another side now (vanilla on its own level, or the other way)
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
                if (down)
                {
                    MineUp.MinedFromAbove++;
                }
                else
                {
                    MineUp.MinedFromBelow++;
                }
                Shaft.Open(MineVertical.Lower(pos, Cell, map), map, actor);
                ReadyForNextToil();
            };
            mine.defaultCompleteMode = ToilCompleteMode.Never;
            mine.WithProgressBar(TargetIndex.B, () => Rock == null ? 1f : 1f - (float)Rock.HitPoints / Rock.MaxHitPoints);
            // (from above: next to the top, Touch; ClosestTouch would mean standing on it, a standable cell)
            mine.FailOnCannotTouch(TargetIndex.B, down ? PathEndMode.Touch : PathEndMode.ClosestTouch);
            mine.activeSkill = () => SkillDefOf.Mining;
            mine.AddFinishAction(delegate
            {
                dust?.Cleanup();
                dust = null;
            });
            yield return mine;
        }
    }

    // (the mine-up driver's name, kept for its JobDef and for saves)
    public class JobDriver_MineFromBelow : JobDriver_MineVertical
    {
    }

    public class JobDriver_MineFromAbove : JobDriver_MineVertical
    {
    }

    public static class MineUp
    {
        // counters (tests)
        public static int MinedFromBelow, MinedFromAbove, YieldsMoved;

        // The yield of a rock mined from another level lands at the miner's cell (plan §5): inside this scope the centre of
        // every near placement is the miner's cell (GenPlace_MineYield_Patch), so the same-floor rules keep it on the
        // miner's floor.
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

namespace ProjectZ
{
    // Vanilla Remove roof under a bare natural top opens it (plan R3): the no-roof area on a tunnel cell whose ceiling is
    // a mountain top with nothing on it makes a hole to the sky (Shaft.Open), in vanilla's roof-removal time, no stone.
    public static class Hole
    {
        public static int Opened;

        // Why the ceiling of cell c can't be opened by Remove roof, or null when it can. Reasons for the player only where
        // rock or something built stands above ("" for every other refusal: not a bare natural top, a stairs exit, ...).
        public static string Blocked(IntVec3 c, Map map)
        {
            if (map == null || !Mountains.On(map) || !c.IsValid || !c.InBounds(map))
            {
                return "";
            }
            Building ce = c.GetEdifice(map);
            if (ce != null && (ce.def.passability == Traversability.Impassable || ce.def.building != null && ce.def.building.isNaturalRock))
            {
                return "";
            }
            return TopBlocked(FloorRoof.Above(c, map), map);
        }

        // The same for the top u itself (also mine down: the top over a rock mined from above): a bare natural floor with
        // nothing kept on it (no rock, nothing built, no stairs exit). Pawns, items and filth on it are fine: they fall.
        public static string TopBlocked(IntVec3 u, Map map)
        {
            if (!u.IsValid || !u.InBounds(map) || !Floors.UnbuiltNatural(u, map))
            {
                return "";
            }
            List<Thing> list = u.GetThingList(map);
            for (int i = 0; i < list.Count; i++)
            {
                Thing t = list[i];
                if (t is Mineable)
                {
                    return "PZ_MineAboveFirst";
                }
                if (t is Building || t is Blueprint || t is Frame)
                {
                    return "PZ_BuiltAbove";
                }
            }
            if (StairwellFloor.IsStairsExit(u, map))
            {
                return "PZ_BuiltAbove";
            }
            return null;
        }

        public static bool CanOpen(IntVec3 c, Map map) => Blocked(c, map) == null;
    }

    // (1) The no-roof area may be painted under a bare natural top (vanilla: "nothing can remove thick roofs").
    [HarmonyPatch(typeof(Designator_AreaNoRoof), nameof(Designator_AreaNoRoof.CanDesignateCell))]
    public static class NoRoofDesignate_Hole_Patch
    {
        public static void Postfix(Designator_AreaNoRoof __instance, IntVec3 c, ref AcceptanceReport __result)
        {
            Map map = __instance.Map;
            if (__result.Accepted || map == null || !Mountains.On(map) || !c.InBounds(map) || c.Fogged(map) || !(map.roofGrid.RoofAt(c)?.isThickRoof ?? false))
            {
                return;
            }
            string why = Hole.Blocked(c, map);
            if (why == null)
            {
                __result = !map.areaManager.NoRoof[c];
            }
            else if (why.Length > 0)
            {
                __result = why.Translate();
            }
        }
    }

    // (3) The work done: the ceiling opens (instead of the roof just going).
    [HarmonyPatch(typeof(JobDriver_RemoveRoof), "DoEffect")]
    public static class RemoveRoofEffect_Hole_Patch
    {
        public static bool Prefix(JobDriver_RemoveRoof __instance)
        {
            Map map = __instance.pawn?.Map;
            IntVec3 c = __instance.job.targetA.Cell;
            if (map == null || !FloorRoof.FloorAbove(c, map) || !Hole.CanOpen(c, map))
            {
                return true;
            }
            Hole.Opened++;
            Shaft.Open(c, map, __instance.pawn);
            return false;
        }
    }
}
