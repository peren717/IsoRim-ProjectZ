using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // Temporary siege ladders and lifts (isorim-pz-siege-ladders-design.md, stage 1: the ladder itself, no raid AI).
    // A ladder is one cell at the foot; its vertical link (VLink, kind Ladder or Lift) goes from the foot up an open-air
    // column to the top: a cell on a higher level beside that column. Any number of floors, no mid-landing, no wall. It
    // is a normal link: everyone may climb it, one pawn at a time, 3 s per floor (LinkSteps). Placed by the place-ladder
    // job (JobDriver_PlaceSiegeLadder; a lift for mechanoids), owned by the placer's faction, claimable, not buildable by
    // the player; one day after the raid it falls apart (CompSiegeLadder).
    // The player's emergency ladder (PZ_EmergencyLadder, same class and comp): built by vanilla construction from a
    // blueprint on the TOP cell, turned toward the open side; it stands on the top cell and hangs down the column to its
    // landing (the foot). No materials, about a second's work, built from either end (touched across its step:
    // EmergencyLadder.PendingFoot); it disappears a day after it is built (no raid gate); deconstructing it gives nothing.
    public class Building_SiegeLadder : Building
    {
        public IntVec3 top = IntVec3.Invalid;
        public IntVec3 foot = IntVec3.Invalid;
        public int span;
        public bool topBlocker;   // later stages: the top is a wall or rock to clear (stage 1: always false)

        public LinkKind Kind => def == PZDefOf.PZ_SiegeLift ? LinkKind.Lift : LinkKind.Ladder;

        // The player's emergency ladder: the thing stands on the top cell (the raiders' ladder and lift: on the foot).
        public bool Emergency => def == PZDefOf.PZ_EmergencyLadder;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref top, "top", IntVec3.Invalid);
            Scribe_Values.Look(ref foot, "foot", IntVec3.Invalid);
            Scribe_Values.Look(ref span, "span", 0);
            Scribe_Values.Look(ref topBlocker, "topBlocker", false);
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            if (Emergency && !top.IsValid)
            {
                // just built: down the column on its open side
                if (!LadderSite.Find(Position, Rotation, map, out IntVec3 f, out int n, out string why, allowBlocker: true))
                {
                    ProjectZ.DebugLog($"{this} at {Position} toward {Rotation}: no drop ({why}), no link");
                    return;
                }
                top = Position;
                foot = f;
                span = n;
            }
            if (!foot.IsValid)
            {
                foot = Position;
            }
            if (top.IsValid && span > 0)
            {
                map.GetComponent<StairGrid>()?.Register(VLink.Ladder(Kind, this, foot, top, span, topBlocker, map));
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map?.GetComponent<StairGrid>()?.Deregister(this);
            base.DeSpawn(mode);
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            if (top.IsValid && Map != null)
            {
                string line = "PZ_StairsLeadTo".Translate(ProjectZ.FloorOf(top, Map) + 1);
                s = s.NullOrEmpty() ? line : line + "\n" + s;
            }
            return s;
        }
    }

    // Where a ladder can stand. The top is a cell on a higher level; the column is the cell beside it on that level
    // (toward 'side'), open air, running down (Column.Landing: no roof on the way, no floor between) to the first floor
    // under it: the foot, standable and free. Span = the floors between foot and top (1 .. the map's levels). Stage 1:
    // the top must be walkable (a blocking top is a link property for later stages).
    public static class LadderSite
    {
        private static readonly List<IntVec3> broken = new List<IntVec3>();

        public static bool Find(IntVec3 top, Rot4 side, Map map, out IntVec3 foot, out int span, out string why, bool allowBlocker = false)
        {
            foot = IntVec3.Invalid;
            span = 0;
            why = null;
            if (ProjectZ.Annex(map) == null || !top.IsValid || !top.InBounds(map))
            {
                why = "no floors";
                return false;
            }
            int L = Levels.LevelOf(top, map);
            if (L == Levels.None || L <= Levels.Min(map))
            {
                why = "top on the lowest level";
                return false;
            }
            if (!allowBlocker && !top.Walkable(map))
            {
                why = "top not walkable";
                return false;
            }
            IntVec3 g = Levels.Ground(top, map) + side.FacingCell;
            if (g.x < 0 || g.z < 0 || g.x >= map.Size.x || g.z >= ProjectZ.Annex(map).groundZ)
            {
                why = "column off the map";
                return false;
            }
            IntVec3 col = Levels.Cell(g, L, map);
            if (!col.IsValid || !col.InBounds(map) || ProjectZ.HasFloor(col, map) || col.GetEdifice(map) != null)
            {
                why = "no open air beside the top";
                return false;
            }
            IntVec3 land = Column.Landing(col, map, false, true, out int fallen, out _, broken);
            if (!land.IsValid || fallen < 1 || broken.Count > 0)
            {
                why = broken.Count > 0 ? "a roof in the column" : "nothing to stand on under the column";
                broken.Clear();
                return false;
            }
            if (!land.Standable(map) || land.GetEdifice(map) != null || Stairwell.StairThingAt(land, map))
            {
                why = "foot not free";
                return false;
            }
            StairGrid sg = StairGrid.For(map);
            if (sg != null)
            {
                for (int i = 0; i < sg.links.Count; i++)
                {
                    if (sg.links[i].bottom == land || sg.links[i].top == land || sg.links[i].top == top && sg.links[i].IsLadder)
                    {
                        why = "another link there";
                        return false;
                    }
                }
            }
            foot = land;
            span = fallen;
            return true;
        }

        // The site of a ladder from 'foot' to 'top' is still good (the place-ladder job checks again before it spawns one).
        public static bool Check(IntVec3 foot, IntVec3 top, Map map, out int span, out string why)
        {
            span = 0;
            why = "no side";
            IntVec3 d = Levels.Ground(foot, map) - Levels.Ground(top, map);
            for (int i = 0; i < 4; i++)
            {
                Rot4 r = new Rot4(i);
                if (r.FacingCell != d)
                {
                    continue;
                }
                if (!Find(top, r, map, out IntVec3 f, out span, out why))
                {
                    return false;
                }
                if (f != foot)
                {
                    why = "the column lands elsewhere";
                    return false;
                }
                return true;
            }
            return false;
        }

        // The best side for a top: the shortest valid column (Rot4.Invalid when none).
        public static Rot4 BestSide(IntVec3 top, Map map, out IntVec3 foot, out int span)
        {
            Rot4 best = Rot4.Invalid;
            foot = IntVec3.Invalid;
            span = int.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                if (Find(top, new Rot4(i), map, out IntVec3 f, out int n, out _) && n < span)
                {
                    best = new Rot4(i);
                    foot = f;
                    span = n;
                }
            }
            if (!best.IsValid)
            {
                span = 0;
            }
            return best;
        }

        // A ladder (a lift for mechanoids) at foot up to top, of the faction's. Null when the site is no good.
        public static Building_SiegeLadder Spawn(IntVec3 foot, IntVec3 top, Faction faction, Map map, bool lift)
        {
            if (!Check(foot, top, map, out int span, out string why))
            {
                ProjectZ.DebugLog($"siege ladder at {foot} -> {top} refused: {why}");
                return null;
            }
            Building_SiegeLadder b = (Building_SiegeLadder)ThingMaker.MakeThing(lift ? PZDefOf.PZ_SiegeLift : PZDefOf.PZ_SiegeLadder);
            b.top = top;
            b.span = span;
            b.SetFaction(faction);
            GenSpawn.Spawn(b, foot, map);
            b.GetComp<CompSiegeLadder>()?.SetOwner(faction);
            return b;
        }
    }

    // The ladder falls apart one day after the raid: a countdown that runs only while no threat of the faction that
    // placed it is on the map (the gate of the game's own claim: Thing.FactionPreventsClaimingOrAdopting), keeps running
    // after the player claims it, shows in the inspect pane, then destroys the ladder (climbers fall: LinkSteps).
    public class CompProperties_SiegeLadder : CompProperties
    {
        public int ticks = 60000;
        public bool raidGate = true;   // false: counts down from completion (the player's emergency ladder)

        public CompProperties_SiegeLadder()
        {
            compClass = typeof(CompSiegeLadder);
        }
    }

    public class CompSiegeLadder : ThingComp
    {
        public int ticksLeft = -1;
        public Faction owner;
        private bool running;

        public CompProperties_SiegeLadder Props => (CompProperties_SiegeLadder)props;

        public bool Running => running;

        public void SetOwner(Faction f)
        {
            owner = f;
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (ticksLeft < 0)
            {
                ticksLeft = Props.ticks;
            }
            owner ??= parent.Faction;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref ticksLeft, "ticksLeft", -1);
            Scribe_References.Look(ref owner, "owner");
        }

        // The raid is over for this ladder: no threat of its owner's faction on the map (the game's claim gate).
        public bool RaidOver()
        {
            return !Props.raidGate || !parent.Spawned || !parent.FactionPreventsClaimingOrAdopting(owner, forClaim: true, out _);
        }

        public override void CompTickRare()
        {
            base.CompTickRare();
            running = RaidOver();
            if (!running)
            {
                return;
            }
            ticksLeft -= GenTicks.TickRareInterval;
            if (ticksLeft <= 0 && !parent.Destroyed)
            {
                ProjectZ.DebugLog($"{parent} at {parent.Position} falls apart");
                parent.Destroy(DestroyMode.Vanish);
            }
        }

        public override string CompInspectStringExtra()
        {
            if (!RaidOver())
            {
                return "PZ_LadderAfterRaid".Translate();
            }
            return "PZ_LadderFallsApart".Translate(Mathf.Max(0, ticksLeft).ToStringTicksToPeriod());
        }
    }

    // Place a ladder: go to the foot, a 60-tick wait with a progress bar any faction shows, check the site again, spawn
    // the ladder (a lift for mechanoids) of the pawn's faction. Job targets: A = foot, B = top.
    public class JobDriver_PlaceSiegeLadder : JobDriver
    {
        public const int Ticks = 60;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
            Toil wait = Toils_General.Wait(Ticks, TargetIndex.A);
            wait.WithProgressBarAnyFaction(TargetIndex.A, () => 1f - (float)wait.actor.jobs.curDriver.ticksLeftThisToil / Ticks);
            wait.FailOn(() => !LadderSite.Check(job.targetA.Cell, job.targetB.Cell, pawn.Map, out _, out _));
            yield return wait;
            yield return Toils_General.Do(delegate
            {
                Building_SiegeLadder b = LadderSite.Spawn(job.targetA.Cell, job.targetB.Cell, pawn.Faction, pawn.Map, pawn.RaceProps.IsMechanoid);
                if (b == null)
                {
                    EndJobWith(JobCondition.Incompletable);
                }
            });
        }

        public static Job Make(IntVec3 foot, IntVec3 top)
        {
            return JobMaker.MakeJob(PZJobDefOf.PZ_PlaceSiegeLadder, foot, top);
        }
    }

    public static class SiegeLadderToils
    {
        // (tests) ticks a bar was shown, and whose
        public static int BarTicks;
        public static Faction LastBarFaction;

        // The game's toil progress bar (ToilEffects.WithProgressBar) without its player-faction check: raiders show it too.
        public static Toil WithProgressBarAnyFaction(this Toil toil, TargetIndex ind, System.Func<float> progress, float offsetZ = -0.5f)
        {
            Effecter effecter = null;
            toil.AddPreTickIntervalAction(delegate
            {
                if (effecter == null)
                {
                    effecter = EffecterDefOf.ProgressBar.Spawn();
                    return;
                }
                LocalTargetInfo t = ind == TargetIndex.None ? LocalTargetInfo.Invalid : toil.actor.CurJob.GetTarget(ind);
                effecter.EffectTick(t.IsValid ? t.ToTargetInfo(toil.actor.Map) : (TargetInfo)toil.actor, TargetInfo.Invalid);
                MoteProgressBar mote = ((SubEffecter_ProgressBar)effecter.children[0]).mote;
                if (mote != null)
                {
                    mote.progress = Mathf.Clamp01(progress());
                    mote.offsetZ = offsetZ;
                    mote.alwaysShow = true;
                    BarTicks++;
                    LastBarFaction = toil.actor.Faction;
                }
            });
            toil.AddFinishAction(delegate
            {
                effecter?.Cleanup();
                effecter = null;
            });
            return toil;
        }
    }

    // The player's emergency ladder before it stands (blueprint or frame on the top cell): the foot it will hang down to.
    // A pawn at the foot touches it across the step-to-be (StairTouch.AcrossStep, the touch regions, the work spot, the
    // router's goal leg), so it can be built from either end.
    public static class EmergencyLadder
    {
        public static bool IsPending(Thing t) => t != null && (t is Blueprint || t is Frame) && t.def.entityDefToBuild == PZDefOf.PZ_EmergencyLadder;

        public static bool PendingFoot(Thing t, out IntVec3 foot)
        {
            foot = IntVec3.Invalid;
            if (!IsPending(t) || !t.Spawned || ProjectZ.Annex(t.Map) == null)
            {
                return false;
            }
            return LadderSite.Find(t.Position, t.Rotation, t.Map, out foot, out _, out _, allowBlocker: true);
        }
    }

    // Placing the emergency ladder: on a top cell, turned toward the open side, with an open-air column down to a floor
    // (LadderSite); the ghost shows where it lands.
    public class PlaceWorker_EmergencyLadder : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            if (ProjectZ.Annex(map) == null || Levels.LevelOf(loc, map) <= Levels.Min(map))
            {
                return "PZ_LadderNoDrop".Translate();
            }
            if (!LadderSite.Find(loc, rot, map, out _, out _, out string why, allowBlocker: true))
            {
                return why == "another link there" ? "PZ_LandingBlocked".Translate() : "PZ_LadderNoDrop".Translate();
            }
            return AcceptanceReport.WasAccepted;
        }

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            Map map = Find.CurrentMap;
            if (map != null && LadderSite.Find(center, rot, map, out IntVec3 foot, out _, out _, allowBlocker: true))
            {
                GenDraw.DrawFieldEdges(new List<IntVec3> { foot }, Color.white);
            }
        }
    }

    [DefOf]
    public static class PZJobDefOf
    {
        public static JobDef PZ_PlaceSiegeLadder;

        static PZJobDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(PZJobDefOf));
        }
    }
}
