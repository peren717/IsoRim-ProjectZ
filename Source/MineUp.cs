using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    [DefOf]
    public static class PZMineDefOf
    {
        public static JobDef PZ_MineFromBelow;
        public static JobDef PZ_MineFromAbove;
        public static DesignationDef PZ_MineAbove;
        public static DesignationDef PZ_MineBelow;

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

    // Tall mountains, vertical mining (isorim-pz-phase3-mineup-plan.md; user decision 2026-10-19: two commands, no guess).
    // The player views level k and drags over cells of level k with "Mine above" (开采上层) or "Mine below" (开采下层). The
    // mark is a cell designation on that level-k cell (PZ_MineAbove / PZ_MineBelow), so the side is fixed by the command
    // and the mark is seen and cancelled on the floor the player looks at:
    //   - Mine above: the rock right above the cell (k+1) is mined from below; the miner stands on the cell or next to it,
    //     the rock's floor opens (mine up). With no rock there, a bare natural top above opens the same way, by Remove
    //     roof's rule (Hole.CanOpen, Shaft.Open; the same job, Remove roof's work time);
    //   - Mine below: the rock right below the cell (k-1) is mined from above; the miner stands next to the cell, a bare
    //     natural top, which opens (mine down). Never under the ground level.
    // Both open one boundary with Shaft.Open. Vanilla Mine stays vanilla (its own level only). The mark goes with its rock
    // (DesignationManager_MineMarks_Patch). Stairs go into the cells once they are open (vanilla placement); a pit dug
    // from above is built from its rim (StairTouch). Every mining job reserves the rock.
    public static class MineSide
    {
        // A rock marked with vanilla Mine or MineVein (its own level).
        public static bool Marked(IntVec3 c, Map map)
        {
            DesignationManager dm = map.designationManager;
            return dm.DesignationAt(c, DesignationDefOf.Mine) != null || dm.DesignationAt(c, DesignationDefOf.MineVein) != null;
        }

        // The mark that gives the rock on the other side of a level-k cell to dir (Mine above: from below; Mine below: from above).
        public static DesignationDef MarkDef(MineDir dir) => dir == MineDir.FromBelow ? PZMineDefOf.PZ_MineAbove : dir == MineDir.FromAbove ? PZMineDefOf.PZ_MineBelow : null;

        // The level-k cell c carries the mark for dir.
        public static bool MarkAt(IntVec3 c, Map map, MineDir dir)
        {
            return c.IsValid && c.InBounds(map) && map.designationManager.DesignationAt(c, MarkDef(dir)) != null;
        }

        public static bool FromBelow(IntVec3 u, Map map) => Get(u, map) == MineDir.FromBelow;

        // The side the rock at u is marked for (Own: no vertical mark on it). Read from the marks, never guessed.
        public static MineDir Get(IntVec3 u, Map map)
        {
            if (map == null || !u.IsValid || !Mountains.On(map))
            {
                return MineDir.Own;
            }
            if (MarkAt(FloorRoof.Below(u, map), map, MineDir.FromBelow))
            {
                return MineDir.FromBelow;
            }
            if (MarkAt(FloorRoof.Above(u, map), map, MineDir.FromAbove))
            {
                return MineDir.FromAbove;
            }
            return MineDir.Own;
        }

        // The rock u is gone: the marks on the cells right below and above it go too.
        public static void ClearMarks(IntVec3 u, Map map)
        {
            IntVec3 b = FloorRoof.Below(u, map);
            if (b.IsValid)
            {
                map.designationManager.TryRemoveDesignation(b, PZMineDefOf.PZ_MineAbove);
            }
            IntVec3 a = FloorRoof.Above(u, map);
            if (a.IsValid)
            {
                map.designationManager.TryRemoveDesignation(a, PZMineDefOf.PZ_MineBelow);
            }
        }

        // Whether the rock on the other side of the level-k cell c can be mined from c (the command's rules; the designator
        // shows the reason, the work giver and the job ask again): the rock is there; from below, no rock or wall on c and
        // someone can stand on c or next to it; from above, c is a bare natural top with nothing kept on it (Hole.TopBlocked)
        // and someone can stand next to it. No level above the top one, none under the ground.
        // From below with no rock above: a bare natural top above c opens instead, by Remove roof's own rule (Hole.CanOpen,
        // its reasons: something built above, a stairs exit; a player floor or a slab: only bare natural ground opens).
        public static AcceptanceReport CanWork(IntVec3 c, Map map, MineDir dir)
        {
            if (map == null || !Mountains.On(map) || !c.IsValid || !c.InBounds(map) || ProjectZ.FloorOf(c, map) < 0)
            {
                return false;
            }
            bool up = dir == MineDir.FromBelow;
            IntVec3 u = WorkGiver_MineVertical.RockCell(c, map, dir);
            bool rock = u.IsValid && u.InBounds(map) && u.GetFirstMineable(map) != null;
            if (!rock)
            {
                if (!up)
                {
                    return "PZ_NoRockBelow".Translate();
                }
                string top = Hole.TopBlocked(u, map);
                if (top != null)
                {
                    return top.Length > 0 ? top.Translate() : (u.IsValid && u.InBounds(map) && ProjectZ.HasFloor(u, map) ? "PZ_NotNaturalGround" : "PZ_NoRockAbove").Translate();
                }
            }
            if (c.GetFirstMineable(map) != null)
            {
                return "PZ_MineHereFirst".Translate();
            }
            if (up)
            {
                Building ce = c.GetEdifice(map);
                if (ce != null && ce.def.passability == Traversability.Impassable)
                {
                    return "PZ_BuiltHere".Translate();
                }
                if (!rock && !Hole.CanOpen(c, map))
                {
                    return false;   // (the same rule as Remove roof; its reasons are given above)
                }
            }
            else
            {
                string why = Hole.TopBlocked(c, map);
                if (why != null)
                {
                    return (why == "PZ_BuiltAbove" ? "PZ_BuiltHere" : "PZ_NotNaturalGround").Translate();
                }
            }
            for (int i = 0; i < 9; i++)
            {
                IntVec3 s = c + GenAdj.AdjacentCellsAndInside[i];
                if (s.InBounds(map) && ProjectZ.SameBand(s, c, map) && s.Walkable(map))
                {
                    return true;
                }
            }
            return "PZ_NoRoomToStand".Translate();
        }
    }

    // The marks go with their rock (mined from any side, or destroyed): vanilla drops its own Mine mark the same way
    // (removeIfBuildingDespawned), but ours sit on the cell right below or above the rock.
    [HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.Notify_BuildingDespawned))]
    public static class DesignationManager_MineMarks_Patch
    {
        public static void Postfix(DesignationManager __instance, Thing b)
        {
            if (b is Mineable && __instance.map != null && Mountains.On(__instance.map))
            {
                MineSide.ClearMarks(b.Position, __instance.map);
            }
        }
    }

    // THE one place a marked rock's boundary opens: a rock carrying a Mine above / Mine below mark that is mined (every
    // mining path ends in Mineable.DestroyMined: our vertical job, vanilla Mine or MineVein on the rock's own level, a
    // blueprint's blocking rock, sappers, god mode) opens the boundary the mark names (Shaft.Open on the lower cell: the
    // marked cell under the rock, or the rock's own cell under the marked top). The mark is read before the rock goes (the
    // despawn takes it, DesignationManager_MineMarks_Patch). Rock destroyed any other way (explosions) opens nothing.
    // The same place opens a bare top over any mined rock (BareTop, user rule 2026-10-23): what stands right above the rock
    // is a bare natural rock top (no rock above, nothing built, placed or standing on it) -> the top goes too, the rock's
    // cell opens to the sky (a lone ore vein or outcrop no longer leaves its top hanging). Rock above (a mountain's inside)
    // keeps its roof; a top with something on it stays.
    [HarmonyPatch(typeof(Mineable), nameof(Mineable.DestroyMined))]
    public static class Mineable_MinedMarked_Patch
    {
        public static int Opened;

        public static void Prefix(Mineable __instance, Pawn pawn, out (Map map, IntVec3 lower, IntVec3 own) __state)
        {
            __state = (null, IntVec3.Invalid, IntVec3.Invalid);
            Map map = __instance.Map;
            if (map == null || !Mountains.On(map))
            {
                return;
            }
            IntVec3 u = __instance.Position;
            __state = (map, IntVec3.Invalid, u);
            MineDir dir = MineSide.Get(u, map);
            IntVec3 lower = dir == MineDir.FromBelow ? FloorRoof.Below(u, map) : dir == MineDir.FromAbove ? u : IntVec3.Invalid;
            // a raider mining up without the player's mark (RaidVertical: the Mine above job, ignoreDesignations)
            Job j = pawn?.CurJob;
            if (!lower.IsValid && j != null && j.def == PZMineDefOf.PZ_MineFromBelow && j.ignoreDesignations && j.targetA.Thing == __instance && j.targetB.Cell == FloorRoof.Below(u, map))
            {
                lower = j.targetB.Cell;
                RaidVertical.MineUps++;
            }
            if (lower.IsValid)
            {
                __state = (map, lower, u);
            }
        }

        public static void Postfix(Pawn pawn, (Map map, IntVec3 lower, IntVec3 own) __state)
        {
            Map map = __state.map;
            if (map == null)
            {
                return;
            }
            int depth = MineUp.YieldPause();   // (only the rock's yield goes to the miner, not what falls)
            try
            {
                if (__state.lower.IsValid)
                {
                    Opened++;
                    Shaft.Open(__state.lower, map, pawn);
                }
                if (__state.own.IsValid && __state.own != __state.lower && BareTop.Opens(__state.own, map))
                {
                    BareTop.Opened++;
                    Shaft.Open(__state.own, map, pawn);
                }
            }
            finally
            {
                MineUp.YieldResume(depth);
            }
        }
    }

    // The bare top over a mined rock (Mineable_MinedMarked_Patch): the cell right above the rock's cell is a bare natural
    // rock top (Hole.TopBlocked: natural rock floor, no rock, nothing built, no blueprint or frame, no stairs exit) with
    // nothing placed or standing on it (no item, corpse or pawn; plants and filth go with it, as Remove roof's hole).
    public static class BareTop
    {
        public static int Opened;
        public static bool Off;   // (tests: the rule off, as before 2026-10-23)

        public static bool Opens(IntVec3 c, Map map)
        {
            if (Off || map == null || !Mountains.On(map) || !c.IsValid || !c.InBounds(map))
            {
                return false;
            }
            IntVec3 u = FloorRoof.Above(c, map);
            if (!u.IsValid || !u.InBounds(map) || Hole.TopBlocked(u, map) != null)
            {
                return false;
            }
            List<Thing> list = u.GetThingList(map);
            for (int i = 0; i < list.Count; i++)
            {
                Thing t = list[i];
                if (t is Pawn || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Building)
                {
                    return false;
                }
            }
            return true;
        }
    }

    // The two commands (Orders, after vanilla Mine; tall-mountain maps only): a flat drag over cells of the viewed level, as
    // vanilla's area tools (not Designator_Mine, so IsoRim's block picking stays off). Vanilla Mine's icon with an arrow.
    public abstract class Designator_MineVertical : Designator_Cells
    {
        private static Action warnIdeo;

        private readonly Texture2D arrow;

        protected abstract MineDir Dir { get; }

        protected override DesignationDef Designation => MineSide.MarkDef(Dir);

        public override bool DragDrawMeasurements => true;

        public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.Mine;

        public override bool Visible => Mountains.On(Find.CurrentMap);

        protected Designator_MineVertical(string key, string arrowPath)
        {
            defaultLabel = key.Translate();
            defaultDesc = (key + "Desc").Translate();
            icon = ContentFinder<Texture2D>.Get("UI/Designators/Mine");
            arrow = ContentFinder<Texture2D>.Get(arrowPath);
            useMouseIcon = true;
            soundDragSustain = SoundDefOf.Designate_DragStandard;
            soundDragChanged = SoundDefOf.Designate_DragStandard_Changed;
            soundSucceeded = SoundDefOf.Designate_Mine;
        }

        public override void DrawIcon(Rect rect, Material buttonMat, GizmoRenderParms parms)
        {
            base.DrawIcon(rect, buttonMat, parms);
            float s = rect.width * 0.38f;
            GUI.color = parms.lowLight ? new Color(1f, 1f, 1f, 0.6f) : Color.white;
            GUI.DrawTexture(new Rect(rect.xMax - s - rect.width * 0.06f, rect.y + rect.height * 0.06f, s, s), arrow);
            GUI.color = Color.white;
        }

        public override AcceptanceReport CanDesignateCell(IntVec3 c)
        {
            if (!c.InBounds(Map))
            {
                return false;
            }
            if (Map.designationManager.DesignationAt(c, Designation) != null)
            {
                return AcceptanceReport.WasRejected;
            }
            if (c.Fogged(Map))
            {
                return "CannotPlaceInUndiscovered".Translate();
            }
            return MineSide.CanWork(c, Map, Dir);
        }

        public override void DesignateSingleCell(IntVec3 c)
        {
            Map.designationManager.AddDesignation(new Designation(c, Designation));
        }

        protected override void FinalizeDesignationSucceeded()
        {
            base.FinalizeDesignationSucceeded();
            PlayerKnowledgeDatabase.KnowledgeDemonstrated(ConceptDefOf.Mining, KnowledgeAmount.SpecificInteraction);
            // (vanilla Mine's Ideology warning)
            warnIdeo ??= AccessTools.MethodDelegate<Action>(AccessTools.Method(typeof(Designator_Mine), "PossiblyWarnPlayerOnDesignatingMining"));
            warnIdeo();
        }

        public override void SelectedUpdate()
        {
            GenUI.RenderMouseoverBracket();
        }
    }

    public class Designator_MineAbove : Designator_MineVertical
    {
        protected override MineDir Dir => MineDir.FromBelow;

        public Designator_MineAbove() : base("PZ_DesignatorMineAbove", "UI/Buttons/ReorderUp")
        {
        }
    }

    public class Designator_MineBelow : Designator_MineVertical
    {
        protected override MineDir Dir => MineDir.FromAbove;

        public Designator_MineBelow() : base("PZ_DesignatorMineBelow", "UI/Buttons/ReorderDown")
        {
        }
    }

    // Vertical mining jobs (plan R1.4, mirrored for mine down): the marked level-k cells, worked for the rock right above
    // (from below) or right below (from above). The pawn works the boundary as vanilla's roof work does (ClosestTouch: on
    // the cell or next to it; from above it stands next to it, not on the top that opens), reserving the rock and the
    // ceiling of the lower cell.
    public abstract class WorkGiver_MineVertical : WorkGiver_Scanner
    {
        protected abstract MineDir Dir { get; }

        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

        public override Danger MaxPathDanger(Pawn pawn) => Danger.Deadly;

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            Map map = pawn.Map;
            return !Mountains.On(map) || !map.designationManager.AnySpawnedDesignationOfDef(MineSide.MarkDef(Dir));
        }

        // The rock cell worked from the level-k cell c: right above it (from below) or right below it (from above).
        public static IntVec3 RockCell(IntVec3 c, Map map, MineDir dir) => dir == MineDir.FromBelow ? FloorRoof.Above(c, map) : FloorRoof.Below(c, map);

        public override IEnumerable<IntVec3> PotentialWorkCellsGlobal(Pawn pawn)
        {
            foreach (Designation d in pawn.Map.designationManager.SpawnedDesignationsOfDef(MineSide.MarkDef(Dir)))
            {
                yield return d.target.Cell;
            }
        }

        // The rock worked from the marked cell c (null: none).
        public static Mineable MarkedRock(IntVec3 c, Map map, MineDir dir)
        {
            if (!MineSide.MarkAt(c, map, dir))
            {
                return null;
            }
            IntVec3 u = RockCell(c, map, dir);
            return u.IsValid ? u.GetFirstMineable(map) : null;
        }

        // Mine above over a bare natural top (no rock): the top itself opens (MineSide.CanWork).
        public static bool TopOnly(IntVec3 c, Map map, MineDir dir) => dir == MineDir.FromBelow && MineSide.MarkAt(c, map, dir) && MarkedRock(c, map, dir) == null;

        public override bool HasJobOnCell(Pawn pawn, IntVec3 c, bool forced = false)
        {
            Map map = pawn.Map;
            Mineable rock = MarkedRock(c, map, Dir);
            bool top = rock == null && TopOnly(c, map, Dir);
            if (rock == null && !top || rock != null && rock.IsForbidden(pawn) || !MineSide.CanWork(c, map, Dir).Accepted)
            {
                return false;
            }
            if (!MineVertical.CanReserve(pawn, rock, c, forced) || Dir == MineDir.FromAbove && !MineVertical.StandFor(pawn, c, map).IsValid)
            {
                return false;
            }
            return top || new HistoryEvent(HistoryEventDefOf.Mined, pawn.Named(HistoryEventArgsNames.Doer)).Notify_PawnAboutToDo_Job();
        }

        public override Job JobOnCell(Pawn pawn, IntVec3 c, bool forced = false)
        {
            Mineable rock = MarkedRock(c, pawn.Map, Dir);
            return rock != null || TopOnly(c, pawn.Map, Dir) ? MineVertical.MakeJob(pawn, rock, c) : null;
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

        // (rock null: a bare top over the work cell opens, only its ceiling is reserved, as vanilla's roof work does)
        public static bool CanReserve(Pawn pawn, Thing rock, IntVec3 work, bool forced)
        {
            return (rock == null || pawn.CanReserve(rock, 1, -1, null, forced)) && pawn.CanReserve(rock == null ? work : Lower(rock.Position, work, pawn.Map), 1, -1, ReservationLayerDefOf.Ceiling, forced);
        }

        // A = the rock, B = the work cell; from above C = where the miner stands: next to the top that opens (nearest the
        // pawn, cells over other rock about to open last), else on it. No rock (a bare top over the work cell): A = the top's
        // cell.
        public static Job MakeJob(Pawn pawn, Thing rock, IntVec3 work)
        {
            Map map = pawn.Map;
            if (rock == null)
            {
                Job open = JobMaker.MakeJob(PZMineDefOf.PZ_MineFromBelow, FloorRoof.Above(work, map), work);
                open.expiryInterval = 20000;
                open.checkOverrideOnExpire = true;
                return open;
            }
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

        // Only a cell the job can mine from: touching the top by the game's own rule (a diagonal cell with both corner cells
        // open air or walled does not touch it), the same check the mining toil fails on.
        public static IntVec3 StandFor(Pawn pawn, IntVec3 top, Map map)
        {
            IntVec3 best = IntVec3.Invalid;
            int bestScore = int.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = top + GenAdj.AdjacentCells[i];
                if (!n.InBounds(map) || !ProjectZ.SameBand(n, top, map) || !n.Standable(map) || !ReachabilityImmediate.CanReachImmediate(n, top, map, PathEndMode.Touch, pawn)
                    || !pawn.CanReach(n, PathEndMode.OnCell, Danger.Deadly))
                {
                    continue;
                }
                int score = (n - pawn.Position).LengthHorizontalSquared;
                IntVec3 under = FloorRoof.Below(n, map);
                if (under.IsValid && under.GetFirstMineable(map) != null && MineSide.MarkAt(n, map, MineDir.FromAbove))
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

    // The job (plan R2, both ways): A = the rock, B = the work cell (below it: mine up; above it: mine down), C = where a
    // miner from above stands. Vanilla's mining per pick hit (its own damage step, so the 80/40 hits, the yield share, the
    // last hit's DestroyMined and the vein flood are vanilla), the yield at the miner's cell; then the boundary between the
    // two levels opens (Shaft.Open on the lower cell). The mark cancelled: the job ends (as vanilla); the work cell no longer
    // qualifying (MineSide.CanWork): it ends at the next hit, the rock keeps its hit points.
    public class JobDriver_MineVertical : JobDriver_Mine
    {
        private static readonly AccessTools.FieldRef<JobDriver_Mine, int> ticksRef = AccessTools.FieldRefAccess<JobDriver_Mine, int>("ticksToPickHit");

        private delegate void DoDamageFn(JobDriver_Mine driver, Thing target, Toil mine, Pawn actor, IntVec3 pos);

        private static readonly DoDamageFn doDamage = AccessTools.MethodDelegate<DoDamageFn>(AccessTools.Method(typeof(JobDriver_Mine), "DoDamage"));

        // A bare top over the work cell (no rock): it opens after vanilla Remove roof's work (65 at 1.7 a tick, here by the
        // miner's mining speed).
        public const float TopWork = 65f;

        private Effecter dust;

        private float topWorkLeft = TopWork;

        private Thing Rock => job.GetTarget(TargetIndex.A).Thing;

        private bool Top => !job.GetTarget(TargetIndex.A).HasThing;

        private IntVec3 Cell => job.GetTarget(TargetIndex.B).Cell;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref topWorkLeft, "pzTopWorkLeft", TopWork);
        }

        public override string GetReport()
        {
            if (Top)
            {
                TerrainDef t = job.GetTarget(TargetIndex.A).Cell.GetTerrain(pawn.Map);
                return job.def.reportString.Replace("TargetA", t?.label ?? "");
            }
            return base.GetReport();
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (Top)
            {
                return pawn.Reserve(Cell, job, 1, -1, ReservationLayerDefOf.Ceiling, errorOnFailed);
            }
            return pawn.Reserve(Rock, job, 1, -1, null, errorOnFailed) && pawn.Reserve(MineVertical.Lower(Rock.Position, Cell, Map), job, 1, -1, ReservationLayerDefOf.Ceiling, errorOnFailed);
        }

        // The top over the work cell opens (Remove roof's opening: Shaft.Open).
        private Toil OpenTop()
        {
            Toil open = ToilMaker.MakeToil("OpenTop");
            open.initAction = delegate
            {
                topWorkLeft = TopWork;
                dust ??= EffecterDefOf.Mine.Spawn();
                dust.Trigger(open.actor, new TargetInfo(job.GetTarget(TargetIndex.A).Cell, Map));
            };
            open.tickIntervalAction = delegate (int delta)
            {
                topWorkLeft -= open.actor.GetStatValue(StatDefOf.MiningSpeed) * 1.7f * delta;
                if (topWorkLeft > 0f)
                {
                    return;
                }
                if (!MineSide.CanWork(Cell, Map, MineDir.FromBelow).Accepted)
                {
                    EndJobWith(JobCondition.Incompletable);   // (something built on the top meanwhile: the mark waits)
                    return;
                }
                MineUp.TopsOpened++;
                Shaft.Open(Cell, Map, open.actor);
                ReadyForNextToil();
            };
            open.defaultCompleteMode = ToilCompleteMode.Never;
            open.WithProgressBar(TargetIndex.B, () => 1f - topWorkLeft / TopWork);
            open.FailOnCannotTouch(TargetIndex.B, PathEndMode.ClosestTouch);
            open.activeSkill = () => SkillDefOf.Mining;
            open.AddFinishAction(delegate
            {
                dust?.Cleanup();
                dust = null;
            });
            return open;
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
            if (Top)
            {
                this.FailOn(() => pawn.IsPlayerControlled && !job.ignoreDesignations && !MineSide.MarkAt(Cell, Map, MineDir.FromBelow));
                yield return Toils_Goto.Goto(TargetIndex.B, PathEndMode.ClosestTouch);
                yield return OpenTop();
                yield break;
            }
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            bool down = ProjectZ.FloorOf(Cell, Map) > ProjectZ.FloorOf(Rock.Position, Map);
            MineDir side = down ? MineDir.FromAbove : MineDir.FromBelow;
            this.FailOn(() => pawn.IsPlayerControlled && !job.ignoreDesignations && !MineSide.MarkAt(Cell, Map, side));
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
                if (!MineSide.CanWork(Cell, map, side).Accepted)
                {
                    // the work cell no longer qualifies (something built on the top, a wall on the cell): the mark waits
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
                // (the boundary opened as the rock was mined: Mineable_MinedMarked_Patch)
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
        public static int MinedFromBelow, MinedFromAbove, TopsOpened, YieldsMoved;

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

        // Outside the scope for a while (the boundary opening inside DestroyMined: what falls from the top lands below,
        // not at the miner); returns the depth to restore.
        public static int YieldPause()
        {
            int d = yieldDepth;
            yieldDepth = 0;
            return d;
        }

        public static void YieldResume(int depth) => yieldDepth = depth;

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
