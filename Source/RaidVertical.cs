using System;
using System.Collections.Generic;
using HarmonyLib;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using Verse.Sound;

namespace ProjectZ
{
    // Raider vertical moves, stage 3 (isorim-pz-siege-ladders-design.md "三种竖直走法", step 3; the vertical axis is just
    // another direction). Three more virtual edges for the raiders' planner (ClimbPlanner), raiders only, costed in ticks:
    //   - a ladder whose top is a blocker the game's PassBlockerJob can clear (a wall, a door, natural rock): the climber
    //     goes up, stays on the ladder at the top (LadderPerch: drawn up there, standing at the foot, touching the top
    //     across the foot <-> top pairing) and clears it the game's way (mine, a building-destroying weapon, melee), then
    //     steps in;
    //   - a ceiling over a cell the raider can get to, removed from below (Removal): natural rock mined up by pawns the
    //     game lets mine (the Mine above job without the player's mark, Shaft.Open), a bare natural top opened the same
    //     way, rock blasted by breach tools (the column opens through Shaft.Open as when mined), a player's slab blasted
    //     by a building-destroying weapon or breach tool (the slab rule: SlabDamage.HeavyHit; wood once, stone and metal
    //     twice) or dug through by sappers (the slab rule, one heavy hit per a wall's worth of mining of the slab's stuff);
    //     then a ladder in the opening (its top possibly a blocker);
    //   - the blaster never stands under the slab it blasts or the cells the blast can drop (RaidBlast.Stand), and waits
    //     while an ally stands there.
    // Who: PawnCost (the pawn's own abilities, as the game asks them: MiningSpeed, PassBlockerJob's weapon rule, the breach
    // lord's FindVerbToUseForBreaching).
    public static class RaidVertical
    {
        // counters (tests)
        public static int Perches, PerchSteppedIn, PerchFalls, PerchLeft, MineUps, TopsOpened, SlabHitsBlast, SlabHitsDig, RockBlasts, RockBlastOpened, BlastJobs, DigJobs, MineJobs, PerchJobs, AllyWaits, VerticalDests, BreachSegments;

        // (tests) every blast from below: who, from where, at what, the slab before and after
        public struct BlastRec
        {
            public int tick, mode;
            public Pawn pawn;
            public IntVec3 blaster, target;
            public float radius;
            public string before, after, weapon;
        }

        public static readonly List<BlastRec> Blasts = new List<BlastRec>();

        public static void ResetStats()
        {
            Blasts.Clear();
            Perches = PerchSteppedIn = PerchFalls = PerchLeft = MineUps = TopsOpened = SlabHitsBlast = SlabHitsDig = RockBlasts = RockBlastOpened = BlastJobs = DigJobs = MineJobs = PerchJobs = AllyWaits = VerticalDests = BreachSegments = 0;
        }

        // A ladder top the game's PassBlockerJob can clear: a wall, door or rock (destroyable, standing on a floor), so the
        // cell is walkable once it is gone.
        public static bool ClearableTop(IntVec3 c, Map map)
        {
            if (map == null || !c.IsValid || !c.InBounds(map) || c.Walkable(map) || !ProjectZ.HasFloor(c, map) || Levels.LevelOf(c, map) < 1)
            {
                return false;
            }
            Building e = c.GetEdifice(map);
            if (e == null || !PathUtility.IsDestroyable(e) || e is Building_SiegeLadder || e is Building_Stairs)
            {
                return false;
            }
            return e.def.passability == Traversability.Impassable || e is Building_Door;
        }

        // ------------------------------------------------------------------------------------------------ the pawn

        public sealed class PawnCost
        {
            public Pawn pawn;
            public bool mine;            // the game lets it mine (MiningSpeed not disabled)
            public bool mineNonMineables;// its duty mines walls too (sappers): digs through a slab
            public Verb blast;           // a building-destroying verb (breach tool or the primary weapon)
            public bool blastRanged;
            public float castTicks, castDamage, explosionRadius;
            public Verb destroyerPrimary;// PassBlockerJob's weapon rule (ranged, not incendiary)
            public float ticksPerHit;    // a pick hit
            public float meleeDps;
            public int caps;             // bits, for the plan key
            public BreachingGrid breach; // a breach lord's grid: its Perlin noise varies where the vertical step is (as the game varies the breach path)

            public static PawnCost For(Pawn p, bool mineNonMineables)
            {
                PawnCost c = new PawnCost { pawn = p };
                // (breachers in their Breaching duty never mine in the game: they breach rock with their tools)
                c.mine = (p.RaceProps.Humanlike || p.RaceProps.IsMechanoid) && !StatDefOf.MiningSpeed.Worker.IsDisabledFor(p) && p.mindState?.duty?.def != DutyDefOf.Breaching;
                c.mineNonMineables = mineNonMineables && c.mine;
                float speed = c.mine ? p.GetStatValue(StatDefOf.MiningSpeed) : 0f;
                if (p.Faction != Faction.OfPlayer)
                {
                    speed = Mathf.Max(speed, 0.6f);   // (JobDriver_Mine's floor for non-player miners)
                }
                c.ticksPerHit = Mathf.Round(100f / Mathf.Max(0.05f, speed));
                c.meleeDps = Mathf.Max(1f, p.GetStatValue(StatDefOf.MeleeDPS));
                Verb prim = p.equipment?.PrimaryEq?.PrimaryVerb;
                if (prim != null && prim.Available() && prim.verbProps.ai_IsBuildingDestroyer && !prim.IsMeleeAttack && !prim.IsIncendiary_Ranged())
                {
                    c.destroyerPrimary = prim;
                }
                c.blast = BlastVerb(p);
                c.breach = BreachingUtility.BreachingGridFor(p);
                if (c.blast != null)
                {
                    c.blastRanged = !c.blast.IsMeleeAttack;
                    c.castTicks = Mathf.Max(60f, c.blast.verbProps.AdjustedFullCycleTime(c.blast, p) * 60f);
                    ThingDef proj = c.blast.GetProjectile();
                    if (proj?.projectile != null)
                    {
                        c.explosionRadius = proj.projectile.explosionRadius;
                        DamageDef d = proj.projectile.damageDef;
                        c.castDamage = proj.projectile.GetDamageAmount(c.blast.EquipmentSource) * BuildingFactor(d);
                    }
                    else
                    {
                        DamageDef d = c.blast.GetDamageDef() ?? DamageDefOf.Blunt;
                        c.castDamage = c.blast.verbProps.AdjustedMeleeDamageAmount(c.blast, p) * BuildingFactor(d);
                    }
                    c.castDamage = Mathf.Max(1f, c.castDamage);
                }
                c.caps = (c.mine ? 1 : 0) | (c.mineNonMineables ? 2 : 0) | (c.blast != null ? (c.blastRanged ? 4 : 8) : 0) | (c.destroyerPrimary != null ? 16 : 0);
                return c;
            }

            private static float BuildingFactor(DamageDef d) => d == null ? 1f : d.buildingDamageFactor * d.buildingDamageFactorImpassable;

            public float MineTicks(float hp, bool natural) => Mathf.Ceil(hp / (natural ? 80f : 40f)) * ticksPerHit;

            public float MeleeTicks(float hp) => hp / meleeDps * 60f;

            // The time to clear a blocker the game's way (DigUtility.PassBlockerJob's choice).
            public float ClearTicks(Thing b)
            {
                if (b == null)
                {
                    return 0f;
                }
                float hp = b.HitPoints;
                if (b.def.mineable)
                {
                    return mine ? MineTicks(hp, b.def.building?.isNaturalRock ?? false) : MeleeTicks(hp);
                }
                if (destroyerPrimary != null)
                {
                    float dmg = PrimaryDamage();
                    return Mathf.Ceil(hp / dmg) * Mathf.Max(60f, destroyerPrimary.verbProps.AdjustedFullCycleTime(destroyerPrimary, pawn) * 60f);
                }
                if (mineNonMineables)
                {
                    return MineTicks(hp, false);
                }
                return MeleeTicks(hp);
            }

            private float PrimaryDamage()
            {
                ThingDef proj = destroyerPrimary.GetProjectile();
                DamageDef d = proj?.projectile?.damageDef;
                float a = proj?.projectile != null ? proj.projectile.GetDamageAmount(destroyerPrimary.EquipmentSource) : 10f;
                return Mathf.Max(1f, a * BuildingFactor(d));
            }
        }

        // The verb a pawn blasts with from below: in a breach lord's Breaching duty its breach tool (the game's choice,
        // FindVerbToUseForBreaching), otherwise its primary weapon; either only when it destroys buildings
        // (ai_IsBuildingDestroyer, PassBlockerJob's rule; incendiaries never).
        public static Verb BlastVerb(Pawn p)
        {
            Verb v = null;
            if (p.mindState?.duty?.def == DutyDefOf.Breaching)
            {
                v = BreachingUtility.FindVerbToUseForBreaching(p);
            }
            if (v == null || !v.verbProps.ai_IsBuildingDestroyer)
            {
                v = p.equipment?.PrimaryEq?.PrimaryVerb;
            }
            if (v == null || !v.Available() || !v.verbProps.ai_IsBuildingDestroyer || !v.IsMeleeAttack && v.IsIncendiary_Ranged())
            {
                return null;
            }
            return v;
        }

        // ------------------------------------------------------------------------------------------------ removal sites

        public enum Removal
        {
            None,
            Rock,   // natural rock right over the foot
            Top,    // a bare natural top right over the foot (Remove roof's opening, Hole.CanOpen)
            Slab    // a player's slab (over open air down to the foot, or right over it)
        }

        public enum Method
        {
            None,
            Mine,       // MineVertical job (rock or top)
            BlastRock,  // RaidBlast on the rock
            BlastSlab,  // RaidBlast on the slab
            DigSlab     // DigSlabUp (sappers)
        }

        // What closes the column over foot cell F: the first floor above it (U, level u) with only open air between, and
        // what kind of ceiling it is. Removal.None when there is nothing removable.
        public static Removal CeilingOver(IntVec3 F, Map map, out IntVec3 U)
        {
            U = IntVec3.Invalid;
            int f = Levels.LevelOf(F, map);
            if (f == Levels.None)
            {
                return Removal.None;
            }
            IntVec3 g = Levels.Ground(F, map);
            int hi = Levels.Max(map);
            for (int L = f + 1; L <= hi; L++)
            {
                IntVec3 x = Levels.Cell(g, L, map);
                if (!x.IsValid || !x.InBounds(map))
                {
                    return Removal.None;
                }
                if (L - 1 > f)
                {
                    RoofDef r = map.roofGrid.RoofAt(Levels.Cell(g, L - 1, map));
                    if (r != null && r != PZDefOf.PZ_StairwellRoof && !ProjectZ.HasFloor(x, map))
                    {
                        return Removal.None;
                    }
                }
                if (!ProjectZ.HasFloor(x, map))
                {
                    if (x.GetEdifice(map) != null)
                    {
                        return Removal.None;
                    }
                    continue;
                }
                U = x;
                if (StairwellFloor.IsStairsExit(x, map) || LadderColumn.Reserved(x, map))
                {
                    return Removal.None;
                }
                if (SlabDamage.SlabAt(x, map) != null)
                {
                    return Removal.Slab;
                }
                if (L != f + 1 || !Mountains.On(map))
                {
                    return Removal.None;
                }
                Building e = x.GetEdifice(map);
                if (e is Mineable m && m.def.building.isNaturalRock)
                {
                    return Removal.Rock;
                }
                if (e == null && Floors.UnbuiltNatural(x, map) && Hole.TopBlocked(x, map) == null)
                {
                    return Removal.Top;
                }
                return Removal.None;
            }
            return Removal.None;
        }

        // The cost of taking the ceiling U (of kind r, over foot F, span floors) off from below for this pawn, and how.
        public static float RemovalTicks(PawnCost pc, Removal r, IntVec3 F, IntVec3 U, int span, Map map, out Method how)
        {
            how = Method.None;
            float best = float.PositiveInfinity;
            switch (r)
            {
                case Removal.Rock:
                {
                    Thing rock = U.GetEdifice(map);
                    float hp = rock?.HitPoints ?? 1000f;
                    if (pc.mine && span == 1)
                    {
                        best = pc.MineTicks(hp, true);
                        how = Method.Mine;
                    }
                    if (pc.blast != null)
                    {
                        float t = Mathf.Ceil(hp / pc.castDamage) * pc.castTicks;
                        if (t < best)
                        {
                            best = t;
                            how = Method.BlastRock;
                        }
                    }
                    break;
                }
                case Removal.Top:
                    if (pc.mine && span == 1)
                    {
                        best = JobDriver_MineVertical.TopWork / (1.7f * Mathf.Max(0.05f, 100f / pc.ticksPerHit));
                        how = Method.Mine;
                    }
                    break;
                case Removal.Slab:
                {
                    int hits = SlabHits(U, map);
                    if (pc.blast != null && pc.blastRanged)
                    {
                        best = hits * pc.castTicks;
                        how = Method.BlastSlab;
                    }
                    if (pc.mineNonMineables && span == 1)
                    {
                        float t = hits * pc.MineTicks(SlabChunkHp(U, map), false);
                        if (t < best)
                        {
                            best = t;
                            how = Method.DigSlab;
                        }
                    }
                    break;
                }
            }
            return best;
        }

        // Heavy hits that take the slab at U down (SlabDamage: wood 1; stone and metal 2, a damaged one 1).
        public static int SlabHits(IntVec3 U, Map map)
        {
            TerrainDef s = SlabDamage.SlabAt(U, map);
            return s != null && SlabDefs.DamagedOf(s) != null ? 2 : 1;
        }

        // A sapper's mining for one heavy hit on a slab: a wall of the slab's stuff (as vanilla sappers mine walls, 40 a pick
        // hit).
        public static float SlabChunkHp(IntVec3 U, Map map)
        {
            TerrainDef s = SlabDamage.SlabAt(U, map);
            ThingDef stuff = SlabDefs.StuffOf(s);
            if (stuff == null && s != null)
            {
                foreach (TerrainDef t in SlabDefs.All)
                {
                    if (SlabDefs.DamagedOf(t) == s)
                    {
                        stuff = SlabDefs.StuffOf(t);
                        break;
                    }
                }
            }
            stuff ??= ThingDefOf.WoodLog;
            return Mathf.Max(40f, ThingDefOf.Wall.GetStatValueAbstract(StatDefOf.MaxHitPoints, stuff));
        }

        // The top a removal leads to: a cardinal neighbour of U on its level, walkable (else a clearable blocker) and in a
        // dig component other than the foot's. Invalid when none.
        public static IntVec3 TopBeside(RouteGraph g, IntVec3 U, Map map, int footComp, out int topComp)
        {
            topComp = int.MinValue;
            IntVec3 blockerTop = IntVec3.Invalid;
            int blockerComp = int.MinValue;
            for (int d = 0; d < 4; d++)
            {
                IntVec3 t = U + GenAdj.CardinalDirections[d];
                if (!t.InBounds(map) || !ProjectZ.SameBand(t, U, map))
                {
                    continue;
                }
                int p = g.DigPartAt(t, 0);
                if (p <= 0)
                {
                    continue;
                }
                int c = g.DigCompOf(p, 0);
                if (c == footComp)
                {
                    continue;
                }
                if (t.Walkable(map) && ProjectZ.HasFloor(t, map))
                {
                    topComp = c;
                    return t;
                }
                if (!blockerTop.IsValid && ClearableTop(t, map))
                {
                    blockerTop = t;
                    blockerComp = c;
                }
            }
            topComp = blockerComp;
            return blockerTop;
        }

        // ------------------------------------------------------------------------------------------------ jobs

        // The job that takes ceiling U off from below by method 'how' (null: not now; a walk is needed first, or nothing).
        public static Job RemovalJob(Pawn pawn, PawnCost pc, Method how, IntVec3 F, IntVec3 U)
        {
            Map map = pawn.Map;
            switch (how)
            {
                case Method.Mine:
                {
                    if (!MineSide.CanWork(F, map, MineDir.FromBelow).Accepted)
                    {
                        return null;
                    }
                    Mineable rock = U.GetFirstMineable(map);
                    if (rock != null && !pawn.CanReserve(rock) || !pawn.CanReserve(F, 1, -1, ReservationLayerDefOf.Ceiling))
                    {
                        return JobMaker.MakeJob(JobDefOf.Wait, 30, checkOverrideOnExpiry: true);
                    }
                    Job j = MineVertical.MakeJob(pawn, rock, F);
                    if (j != null)
                    {
                        j.ignoreDesignations = true;
                        MineJobs++;
                    }
                    return j;
                }
                case Method.BlastRock:
                case Method.BlastSlab:
                {
                    bool slab = how == Method.BlastSlab;
                    IntVec3 stand = RaidBlast.Stand(pawn, pc.blast, U, F, slab, pc.explosionRadius);
                    if (!stand.IsValid)
                    {
                        return null;
                    }
                    if (!pawn.CanReserve(U, 1, -1, ReservationLayerDefOf.Ceiling))
                    {
                        // another blaster has it: wait where a blast can't drop anything on this one
                        return pawn.Position == stand ? JobMaker.MakeJob(JobDefOf.Wait_Combat, 90, checkOverrideOnExpiry: true) : JobMaker.MakeJob(JobDefOf.Goto, stand, 500, checkOverrideOnExpiry: true);
                    }
                    BlastJobs++;
                    return RaidBlast.Make(slab ? (LocalTargetInfo)U : (LocalTargetInfo)(U.GetEdifice(map) ?? (Thing)null), stand, F, pc.blast, slab ? 1 : 2);
                }
                case Method.DigSlab:
                {
                    IntVec3 stand = JobDriver_DigSlabUp.Stand(pawn, F, U);
                    if (!stand.IsValid)
                    {
                        return null;
                    }
                    if (!pawn.CanReserve(U, 1, -1, ReservationLayerDefOf.Ceiling))
                    {
                        return JobMaker.MakeJob(JobDefOf.Wait_Combat, 90, checkOverrideOnExpiry: true);
                    }
                    DigJobs++;
                    Job j = JobMaker.MakeJob(PZVJobDefOf.PZ_DigSlabUp, U, F, stand);
                    j.expiryInterval = 20000;
                    j.checkOverrideOnExpire = true;
                    return j;
                }
            }
            return null;
        }

        // The job a pawn perched at the top of a ladder clears its top with: the game's PassBlockerJob, from the foot
        // across the pairing; a building-destroying weapon fires from the ladder (RaidBlast; a shot from the foot's level
        // could not see over the top's floor).
        public static Job ClearJob(Pawn pawn, LadderPerch.Rec r)
        {
            Map map = pawn.Map;
            Building b = r.ladder.top.GetEdifice(map);
            if (b == null)
            {
                return null;
            }
            Job j = DigUtility.PassBlockerJob(pawn, b, r.ladder.foot, true, r.mineNonMineables);
            if (j == null)
            {
                return null;
            }
            if (j.def == JobDefOf.UseVerbOnThing && j.verbToUse != null)
            {
                return RaidBlast.Make(b, IntVec3.Invalid, IntVec3.Invalid, j.verbToUse, 0);
            }
            if (j.def != JobDefOf.Mine && j.def != JobDefOf.AttackMelee)
            {
                return JobMaker.MakeJob(JobDefOf.Wait, 30, checkOverrideOnExpiry: true);   // (reserved by another: wait up there)
            }
            return j;
        }

        // ------------------------------------------------------------------------------------------------ safety

        // Pawns of this faction (not the blaster) standing under the cells a blast at U can drop: within radius of U's
        // ground cell, on a level below U's.
        public static bool AllyUnder(Pawn blaster, IntVec3 U, float radius)
        {
            Map map = blaster.Map;
            IntVec3 g = Levels.Ground(U, map);
            int u = Levels.LevelOf(U, map);
            float r2 = (radius + 1.5f) * (radius + 1.5f);
            IReadOnlyList<Pawn> list = map.mapPawns.SpawnedPawnsInFaction(blaster.Faction);
            for (int i = 0; i < list.Count; i++)
            {
                Pawn p = list[i];
                if (p == blaster || p.Dead)
                {
                    continue;
                }
                if (Levels.LevelOf(p.Position, map) < u && (Levels.Ground(p.Position, map) - g).LengthHorizontalSquared <= r2)
                {
                    return true;
                }
            }
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------------------- the perch

    // Raiders at the top of a ladder whose top is a blocker: one per ladder. The pawn stands at the foot (its cell, its
    // regions) and is drawn up there (Links.Rise); it touches the top across the foot <-> top pairing (StairTouch), melee
    // included (StairTouch.NoMeleeAcross). While it holds the perch its job is not overridden. It ends: the blocker gone
    // (the pawn steps onto the top), downed or the ladder gone (it falls from the height it had: Gravity.Bruise), its job
    // no longer clearing that top or the pawn moved (it climbed down).
    public class LadderPerch : MapComponent
    {
        public sealed class Rec
        {
            public Pawn pawn;
            public Building_SiegeLadder ladder;
            public float height;
            public bool atTop;
            public bool mineNonMineables;
        }

        public readonly List<Rec> recs = new List<Rec>();

        public LadderPerch(Map map) : base(map)
        {
        }

        private static Map lastMap;
        private static LadderPerch last;

        public static LadderPerch For(Map map)
        {
            if (map == null)
            {
                return null;
            }
            if (!ReferenceEquals(map, lastMap))
            {
                last = map.components == null ? null : map.GetComponent<LadderPerch>();
                lastMap = map;
            }
            return last;
        }

        public Rec Of(Pawn p)
        {
            for (int i = 0; i < recs.Count; i++)
            {
                if (recs[i].pawn == p)
                {
                    return recs[i];
                }
            }
            return null;
        }

        public static Rec Get(Pawn p)
        {
            if (p == null || !p.Spawned)
            {
                return null;
            }
            LadderPerch lp = For(p.Map);
            return lp == null || lp.recs.Count == 0 ? null : lp.Of(p);
        }

        public Pawn Holder(Thing ladder)
        {
            for (int i = 0; i < recs.Count; i++)
            {
                if (recs[i].ladder == ladder)
                {
                    return recs[i].pawn;
                }
            }
            return null;
        }

        public static bool Busy(Thing ladder, Pawn asker)
        {
            LadderPerch lp = For(ladder.Map);
            Pawn h = lp?.Holder(ladder);
            if (h != null && h != asker)
            {
                return true;
            }
            LinkSteps s = LinkSteps.For(ladder.Map);
            Pawn c = s?.Holder(ladder);
            return c != null && c != asker;
        }

        // floors over the foot the pawn is drawn at (0: not perched)
        public static float Height(Pawn p)
        {
            if (p?.Map == null)
            {
                return 0f;
            }
            LadderPerch lp = For(p.Map);
            if (lp == null || lp.recs.Count == 0)
            {
                return 0f;
            }
            Rec r = lp.Of(p);
            return r == null ? 0f : r.height;
        }

        public static bool Holds(Pawn p) => p != null && !p.Downed && Height(p) > 0f;

        // a perched pawn is on this ladder step (melee across it is allowed: it is up there)
        public static bool OnLink(VLink l, Map map)
        {
            LadderPerch lp = For(map);
            if (lp == null || lp.recs.Count == 0 || l.thing == null)
            {
                return false;
            }
            for (int i = 0; i < lp.recs.Count; i++)
            {
                if (lp.recs[i].ladder == l.thing && lp.recs[i].atTop)
                {
                    return true;
                }
            }
            return false;
        }

        public Rec Begin(Pawn p, Building_SiegeLadder ladder, bool mineNonMineables)
        {
            Rec r = Of(p);
            if (r == null)
            {
                r = new Rec { pawn = p };
                recs.Add(r);
            }
            r.ladder = ladder;
            r.height = 0.001f;
            r.atTop = false;
            r.mineNonMineables = mineNonMineables;
            return r;
        }

        public void End(Rec r) => recs.Remove(r);

        private static bool ClearingJob(Pawn p, Rec r)
        {
            Job j = p.CurJob;
            if (j == null)
            {
                return false;
            }
            if (j.def == JobDefOf.Wait || j.def == JobDefOf.Wait_Combat || j.def == JobDefOf.Wait_MaintainPosture)
            {
                return true;
            }
            if (j.def != JobDefOf.Mine && j.def != JobDefOf.AttackMelee && j.def != PZVJobDefOf.PZ_RaidBlast && j.def != JobDefOf.UseVerbOnThing)
            {
                return false;
            }
            LocalTargetInfo a = j.targetA;
            return a.IsValid && (a.HasThing ? a.Thing.Position == r.ladder.top : a.Cell == r.ladder.top);
        }

        public override void MapComponentTick()
        {
            if (recs.Count == 0)
            {
                return;
            }
            for (int i = recs.Count - 1; i >= 0; i--)
            {
                Rec r = recs[i];
                Pawn p = r.pawn;
                if (p == null || p.Destroyed || !p.Spawned || p.Map != map || p.Dead)
                {
                    recs.RemoveAt(i);
                    continue;
                }
                Building_SiegeLadder l = r.ladder;
                bool gone = l == null || !l.Spawned || l.Destroyed;
                if (p.Downed || gone)
                {
                    recs.RemoveAt(i);
                    int fallen = Mathf.FloorToInt(r.height + 0.001f);
                    RaidVertical.PerchFalls++;
                    ProjectZ.DebugLog($"{p} falls from the top of {l} ({r.height:0.00} floors)");
                    p.Notify_Teleported(endCurrentJob: !p.Downed, resetTweenedPos: true);
                    Gravity.Bruise(p, fallen);
                    continue;
                }
                if (p.Position != l.foot)
                {
                    recs.RemoveAt(i);
                    RaidVertical.PerchLeft++;
                    continue;
                }
                if (!r.atTop)
                {
                    if (p.CurJobDef != PZVJobDefOf.PZ_LadderPerch)
                    {
                        recs.RemoveAt(i);   // the climb ended early: back at the foot
                        RaidVertical.PerchLeft++;
                    }
                    continue;
                }
                if (l.top.Walkable(map))
                {
                    // the blocker is gone: step onto the top (the ladder's link is a plain ladder from now on)
                    recs.RemoveAt(i);
                    RaidVertical.PerchSteppedIn++;
                    l.topBlocker = false;
                    map.GetComponent<StairGrid>()?.Register(VLink.Ladder(l.Kind, l, l.foot, l.top, l.span, false, map));
                    p.Position = l.top;
                    p.Notify_Teleported(endCurrentJob: true, resetTweenedPos: true);
                    ProjectZ.DebugLog($"{p} cleared the top of {l} and steps onto {l.top}");
                    continue;
                }
                if (!ClearingJob(p, r))
                {
                    recs.RemoveAt(i);
                    RaidVertical.PerchLeft++;
                    ProjectZ.DebugLog($"{p} leaves the top of {l} for {p.CurJobDef?.defName}");
                }
            }
        }
    }

    // Climb a ladder whose top is a blocker and stay up there (A = the ladder, B = its top; count 1: the duty mines walls
    // too). The climb pays the ladder's time (VLink.LadderTicksPerFloor a floor); at the top the clearing job comes next
    // (queued first: RaidVertical.ClearJob).
    public class JobDriver_LadderPerch : JobDriver
    {
        private Building_SiegeLadder Ladder => job.targetA.Thing as Building_SiegeLadder;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => Ladder == null || !Ladder.Spawned || Ladder.top.Walkable(Map) || LadderPerch.Busy(Ladder, pawn));
            yield return Toils_Goto.GotoCell(Ladder.foot, PathEndMode.OnCell);
            int total = Mathf.Max(1, VLink.LadderTicksPerFloor * Mathf.Max(1, Ladder.span));
            Toil climb = Toils_General.Wait(total, TargetIndex.None);
            climb.initAction = delegate
            {
                LadderPerch.For(Map)?.Begin(pawn, Ladder, job.count == 1);
                RaidVertical.Perches++;
            };
            climb.AddPreTickIntervalAction(delegate
            {
                LadderPerch.Rec r = LadderPerch.For(Map)?.Of(pawn);
                if (r != null)
                {
                    r.height = Mathf.Max(0.001f, Ladder.span * (1f - (float)pawn.jobs.curDriver.ticksLeftThisToil / total));
                }
            });
            yield return climb;
            yield return Toils_General.Do(delegate
            {
                LadderPerch.Rec r = LadderPerch.For(Map)?.Of(pawn);
                if (r == null)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                r.height = Ladder.span;
                r.atTop = true;
                Job next = RaidVertical.ClearJob(pawn, r);
                if (next == null)
                {
                    LadderPerch.For(Map)?.End(r);
                    return;
                }
                pawn.jobs.jobQueue.EnqueueFirst(next);
            });
        }
    }

    // ---------------------------------------------------------------------------------------------------- blasting

    // A cast at something the game's own shot can't line up (a slab or rock overhead, a ladder's top from up on it): the
    // verb's whole cycle (warm-up with the aim, the cast's sound and flash, cool-down), then what the shot does there:
    //   - an explosive projectile: its explosion at the target cell (a slab: full radius, the slab rule through the
    //     game's terrain damage; rock overhead: that cell only), then one heavy hit on the slab if the explosion did not
    //     make one (a non-Bomb breach tool: Thump);
    //   - anything else: its damage on the target thing (the game applies the building factors), one heavy hit on a slab.
    // Rock overhead destroyed: its column opens as when mined (Shaft.Open on the cell under it). Modes (job.count): 0 a
    // ladder's top blocker, 1 a slab overhead, 2 rock overhead. A = target (a cell for a slab, else the thing), B = where
    // to stand (Invalid: here), C = the cell under the ceiling (modes 1, 2).
    public class JobDriver_RaidBlast : JobDriver
    {
        private int allyWaits;

        // one blaster per ceiling at a time (the others wait clear of it: RaidVertical.RemovalJob)
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (job.count == 0)
            {
                return true;
            }
            return pawn.Reserve(TargetCell, job, 1, -1, ReservationLayerDefOf.Ceiling, errorOnFailed);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref allyWaits, "pzAllyWaits", 0);
        }

        private IntVec3 TargetCell => job.targetA.HasThing ? job.targetA.Thing.Position : job.targetA.Cell;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            Verb verb = job.verbToUse;
            int mode = job.count;
            this.FailOn(() => verb == null || !verb.Available() || pawn.equipment?.Primary == null && verb.EquipmentSource != null);
            this.FailOn(() => mode == 1 ? SlabDamage.SlabAt(job.targetA.Cell, Map) == null : !job.targetA.HasThing || job.targetA.Thing.Destroyed || !job.targetA.Thing.Spawned);
            if (job.targetB.IsValid)
            {
                yield return Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
            }
            AddFinishAction(delegate (JobCondition c)
            {
                if (c != JobCondition.Succeeded)
                {
                    ProjectZ.DebugLog($"{pawn}'s blast at {TargetCell} (mode {mode}) ended {c} at {pawn.Position}");
                }
            });
            int warm = Mathf.Max(30, Mathf.RoundToInt(verb.verbProps.warmupTime * 60f));
            Toil aim = Toils_General.Wait(warm, TargetIndex.None);
            aim.initAction = delegate
            {
                IntVec3 c = TargetCell;
                if (c.IsValid)
                {
                    pawn.rotationTracker.FaceCell(ProjectZ.CellOnFloor(ProjectZ.GroundCellOf(c, Map), ProjectZ.FloorOf(pawn.Position, Map), Map));
                }
            };
            aim.WithProgressBarAnyFaction(TargetIndex.None, () => 1f - (float)aim.actor.jobs.curDriver.ticksLeftThisToil / warm);
            yield return aim;
            Toil fire = ToilMaker.MakeToil("RaidBlastFire");
            fire.initAction = delegate
            {
                if (mode == 1 && RaidVertical.AllyUnder(pawn, job.targetA.Cell, ExplosionRadius(verb)))
                {
                    RaidVertical.AllyWaits++;
                    if (++allyWaits > 12)
                    {
                        EndJobWith(JobCondition.Incompletable);
                        return;
                    }
                    JumpToToil(aim);
                    return;
                }
                Fire(verb, mode);
            };
            fire.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return fire;
            int cool = Mathf.Max(1, Mathf.RoundToInt(verb.verbProps.AdjustedCooldown(verb, pawn) * 60f));
            yield return Toils_General.Wait(cool, TargetIndex.None);
        }

        private static float ExplosionRadius(Verb verb) => verb.GetProjectile()?.projectile?.explosionRadius ?? 0f;

        private void Fire(Verb verb, int mode)
        {
            Map map = Map;
            IntVec3 at = TargetCell;
            verb.verbProps.soundCast?.PlayOneShot(new TargetInfo(pawn.Position, map));
            if (verb.verbProps.muzzleFlashScale > 0.01f)
            {
                FleckMaker.Static(pawn.Position, map, FleckDefOf.ShotFlash, verb.verbProps.muzzleFlashScale);
            }
            TerrainDef slabBefore = mode == 1 ? SlabDamage.SlabAt(at, map) : null;
            Thing target = job.targetA.HasThing ? job.targetA.Thing : null;
            ThingDef proj = verb.GetProjectile();
            ThingDef weapon = verb.EquipmentSource?.def;
            bool explosive = proj?.projectile != null && proj.projectile.explosionRadius > 0f;
            // the target itself takes the cast's own hit (a slab: one heavy hit, the slab rule; a thing: its damage, the
            // game's building factors); an explosion goes off round it on its level (a slab: everything in the blast,
            // other slabs too, as any grenade on a floor; rock or a ladder's top: the sound and flash only, the blast
            // stays in the rock)
            if (explosive)
            {
                bool full = mode == 1;
                GenExplosion.DoExplosion(at, map, full ? proj.projectile.explosionRadius : 0.9f, proj.projectile.damageDef, pawn, full ? proj.projectile.GetDamageAmount(verb.EquipmentSource) : 0, proj.projectile.GetArmorPenetration(verb.EquipmentSource), proj.projectile.soundExplode, weapon, proj, target, excludeRadius: full ? 0.5f : 0f);
            }
            if (target != null && !target.Destroyed)
            {
                DamageDef d = explosive ? proj.projectile.damageDef : verb.GetDamageDef() ?? DamageDefOf.Blunt;
                float amount = proj?.projectile != null ? proj.projectile.GetDamageAmount(verb.EquipmentSource) : verb.verbProps.AdjustedMeleeDamageAmount(verb, pawn);
                target.TakeDamage(new DamageInfo(d, amount, proj?.projectile?.GetArmorPenetration(verb.EquipmentSource) ?? 0f, -1f, pawn, null, weapon));
            }
            if (mode == 1)
            {
                if (slabBefore != null && SlabDamage.SlabAt(at, map) == slabBefore)
                {
                    SlabDamage.HeavyHit(at, map);
                }
                RaidVertical.SlabHitsBlast++;
            }
            else if (mode == 2)
            {
                RaidVertical.RockBlasts++;
                IntVec3 below = job.targetC.IsValid ? job.targetC.Cell : FloorRoof.Below(at, map);
                if ((target == null || target.Destroyed) && at.GetFirstMineable(map) == null && ProjectZ.HasFloor(at, map) && below.IsValid && !Shaft.Is(below, map))
                {
                    RaidVertical.RockBlastOpened++;
                    Shaft.Open(below, map, pawn);
                }
            }
            RaidVertical.Blasts.Add(new RaidVertical.BlastRec { tick = Find.TickManager.TicksGame, pawn = pawn, blaster = pawn.Position, target = at, radius = proj?.projectile?.explosionRadius ?? 0f, mode = mode, before = slabBefore?.defName, after = mode == 1 ? (SlabDefs.DamagedOf(slabBefore)?.defName ?? "broken") : null, weapon = weapon?.defName });
            ProjectZ.DebugLog($"{pawn} blasts {(target != null ? target.ToString() : "the slab")} at {at} (mode {mode}) with {verb.verbProps.label ?? weapon?.defName}");
        }
    }

    public static class RaidBlast
    {
        public static Job Make(LocalTargetInfo target, IntVec3 stand, IntVec3 under, Verb verb, int mode)
        {
            Job j = JobMaker.MakeJob(PZVJobDefOf.PZ_RaidBlast, target, stand.IsValid ? (LocalTargetInfo)stand : LocalTargetInfo.Invalid, under.IsValid ? (LocalTargetInfo)under : LocalTargetInfo.Invalid);
            j.verbToUse = verb;
            j.count = mode;
            j.expiryInterval = 2000;
            j.checkOverrideOnExpire = true;
            return j;
        }

        // Where to blast ceiling U (the cell under it on the shooter's level: F) from: a standable cell on F's level the
        // pawn can reach, within the verb's range of F, not under U or any cell the blast can drop (U's radius + 1.5 on
        // the ground; a slab's explosion breaks every slab in its radius), with a clear line: the game's sight on F's
        // level to F, and no solid floor between the levels where the line rises through them. Melee breach tools on rock:
        // F itself (rock does not fall). Nearest the pawn; Invalid when none.
        public static IntVec3 Stand(Pawn pawn, Verb verb, IntVec3 U, IntVec3 F, bool slab, float radius)
        {
            Map map = pawn.Map;
            if (verb == null)
            {
                return IntVec3.Invalid;
            }
            if (verb.IsMeleeAttack)
            {
                return !slab && F.Standable(map) && pawn.CanReach(F, PathEndMode.OnCell, Danger.Deadly) ? F : IntVec3.Invalid;
            }
            int f = Levels.LevelOf(F, map), u = Levels.LevelOf(U, map);
            IntVec3 gF = Levels.Ground(F, map);
            float minD = (slab ? radius : 0f) + 1.5f;
            float maxD = Mathf.Min(verb.EffectiveRange, 14f);
            if (maxD <= minD)
            {
                return IntVec3.Invalid;
            }
            IntVec3 best = IntVec3.Invalid;
            float bestScore = float.MaxValue;
            int n = GenRadial.NumCellsInRadius(maxD);
            for (int i = 0; i < n; i++)
            {
                IntVec3 g = gF + GenRadial.RadialPattern[i];
                float d = (g - gF).LengthHorizontal;
                if (d < minD)
                {
                    continue;
                }
                IntVec3 c = Levels.Cell(g, f, map);
                if (!c.IsValid || !c.InBounds(map) || !c.Standable(map))
                {
                    continue;
                }
                float score = (c - pawn.Position).LengthHorizontalSquared;
                if (score >= bestScore || !Clear(c, F, f, u, map) || !pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly))
                {
                    continue;
                }
                best = c;
                bestScore = score;
            }
            return best;
        }

        // The plan's test: some cell in a ring around F (in component comp) could blast U.
        public static bool AnyStand(RouteGraph rg, IntVec3 U, IntVec3 F, int comp, bool slab, float radius, float range, Map map)
        {
            int f = Levels.LevelOf(F, map), u = Levels.LevelOf(U, map);
            IntVec3 gF = Levels.Ground(F, map);
            float minD = (slab ? radius : 0f) + 1.5f;
            float maxD = Mathf.Min(range, minD + 4f);
            if (Mathf.Min(range, 14f) <= minD)
            {
                return false;
            }
            int n = GenRadial.NumCellsInRadius(maxD);
            for (int i = 0; i < n; i++)
            {
                IntVec3 g = gF + GenRadial.RadialPattern[i];
                if ((g - gF).LengthHorizontal < minD)
                {
                    continue;
                }
                IntVec3 c = Levels.Cell(g, f, map);
                if (!c.IsValid || !c.InBounds(map) || !c.Standable(map) || ClimbPlanner.CompAt(rg, c, map) != comp)
                {
                    continue;
                }
                if (Clear(c, F, f, u, map))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Clear(IntVec3 c, IntVec3 F, int f, int u, Map map)
        {
            if (c != F && !GenSight.LineOfSight(c, F, map, skipFirstCell: true))
            {
                return false;
            }
            if (u - f <= 1)
            {
                return true;
            }
            IntVec3 a = Levels.Ground(c, map), b = Levels.Ground(F, map);
            for (int L = f + 1; L < u; L++)
            {
                float t = (float)(L - f) / (u - f);
                IntVec3 g = new IntVec3(Mathf.RoundToInt(Mathf.Lerp(a.x, b.x, t)), 0, Mathf.RoundToInt(Mathf.Lerp(a.z, b.z, t)));
                if (Column.PlaneSolid(g, L, map))
                {
                    return false;
                }
            }
            return true;
        }
    }

    // ---------------------------------------------------------------------------------------------------- sappers

    // A sapper digs through a slab overhead (A = the slab cell U, B = the cell under it F, C = where it stands: next to
    // F, not under U). Vanilla's sapper mines walls (40 a pick hit at its mining speed); one heavy hit on the slab
    // (SlabDamage: wood breaks, stone and metal are damaged first) per a wall's hit points of the slab's stuff.
    public class JobDriver_DigSlabUp : JobDriver
    {
        private float dug;
        private int ticksToHit = -1000;
        private Effecter dust;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA.Cell, job, 1, -1, ReservationLayerDefOf.Ceiling, errorOnFailed);

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref dug, "pzDug", 0f);
            Scribe_Values.Look(ref ticksToHit, "pzTicksToHit", -1000);
        }

        public static IntVec3 Stand(Pawn pawn, IntVec3 F, IntVec3 U)
        {
            Map map = pawn.Map;
            IntVec3 best = IntVec3.Invalid;
            int bestD = int.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = F + GenAdj.AdjacentCells[i];
                if (!n.InBounds(map) || !ProjectZ.SameBand(n, F, map) || !n.Standable(map) || !ReachabilityImmediate.CanReachImmediate(n, F, map, PathEndMode.Touch, pawn))
                {
                    continue;
                }
                int d = (n - pawn.Position).LengthHorizontalSquared;
                if (d < bestD && pawn.CanReach(n, PathEndMode.OnCell, Danger.Deadly))
                {
                    bestD = d;
                    best = n;
                }
            }
            return best;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            IntVec3 U = job.targetA.Cell;
            this.FailOn(() => SlabDamage.SlabAt(U, Map) == null);
            yield return Toils_Goto.GotoCell(TargetIndex.C, PathEndMode.OnCell);
            Toil dig = ToilMaker.MakeToil("DigSlabUp");
            dig.tickIntervalAction = delegate (int delta)
            {
                Pawn actor = dig.actor;
                if (ticksToHit < -100)
                {
                    ResetTicks();
                }
                actor.skills?.Learn(SkillDefOf.Mining, 0.07f * delta);
                ticksToHit -= delta;
                if (ticksToHit > 0)
                {
                    return;
                }
                ResetTicks();
                dust ??= EffecterDefOf.Mine.Spawn();
                dust.Trigger(actor, new TargetInfo(job.targetB.Cell, Map));
                dug += 40f;
                float chunk = RaidVertical.SlabChunkHp(U, Map);
                if (dug < chunk)
                {
                    return;
                }
                if (RaidVertical.AllyUnder(actor, U, 0f))
                {
                    RaidVertical.AllyWaits++;
                    dug = chunk;   // (holds the blow until the cell under it is clear)
                    return;
                }
                dug = 0f;
                SlabDamage.HeavyHit(U, Map);
                RaidVertical.SlabHitsDig++;
                if (SlabDamage.SlabAt(U, Map) == null)
                {
                    ReadyForNextToil();
                }
            };
            dig.defaultCompleteMode = ToilCompleteMode.Never;
            dig.WithProgressBarAnyFaction(TargetIndex.B, () => Mathf.Clamp01(dug / Mathf.Max(1f, RaidVertical.SlabChunkHp(U, Map))));
            dig.FailOnCannotTouch(TargetIndex.B, PathEndMode.Touch);
            dig.activeSkill = () => SkillDefOf.Mining;
            dig.AddFinishAction(delegate
            {
                dust?.Cleanup();
                dust = null;
            });
            yield return dig;
        }

        private void ResetTicks()
        {
            float speed = pawn.GetStatValue(StatDefOf.MiningSpeed);
            if (pawn.Faction != Faction.OfPlayer)
            {
                speed = Mathf.Max(speed, 0.6f);
            }
            ticksToHit = Mathf.RoundToInt(100f / Mathf.Max(0.05f, speed));
        }
    }

    [DefOf]
    public static class PZVJobDefOf
    {
        public static JobDef PZ_LadderPerch;
        public static JobDef PZ_RaidBlast;
        public static JobDef PZ_DigSlabUp;

        static PZVJobDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(PZVJobDefOf));
        }
    }
}

namespace ProjectZ.Patches
{
    // A perched raider's job is not overridden (as a climber's: LinkSteps).
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.CheckForJobOverride))]
    [PatchGroup(FailSoft.StairRouting)]
    public static class CheckForJobOverride_Perch_Patch
    {
        private static readonly AccessTools.FieldRef<Pawn_JobTracker, Pawn> PawnRef = AccessTools.FieldRefAccess<Pawn_JobTracker, Pawn>("pawn");

        public static bool Prefix(Pawn_JobTracker __instance) => !LadderPerch.Holds(PawnRef(__instance));
    }
}
