using System;
using System.Collections.Generic;
using System.Linq;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // Raider vertical moves, stage 3 (isorim-pz-siege-ladders-design.md; RaidVertical.cs), as cases of mode raidladders
    // whose ids start with "v" (left out unless named: -projectzraidonly=v0,v1,...). Run on a tall-mountain map:
    // -projectztallmountains -projectzfloors=5 -projectzmapsize=120 -projectzseed=7. Every case clears the whole map and
    // builds its own fixture (RlClear), fires a real raid and runs real lords, jobs and ticks:
    //   V0  explosions from above (the sky side) on a mountain top and a cliff's rock: the mountain stays closed (no
    //       shaft, the roofs under the top and the rock under it kept)
    //   V1  ImmediateAttack against a sealed mountain colony one level up: mine in, mine up, a ladder, in, fight
    //   V2  the same two levels up (mine up through rock, clear the rock at the ladder's top from the ladder, again)
    //   V3  V4  the same as V1, V2 with a sapper raid: no degrade, the lord stays a sapper raid, they mine up
    //   V5  raiders who can't mine (Mining disabled) against V1's colony: no mining up
    //   V6  mechanoids against V1's colony: they mine up (a lift)
    //   V7  a base on floor 2 with a full wooden wall ring on the slab's rim, no stairs: a ladder to the wall, the wall
    //       cleared from the ladder's top, in, fight
    //   V8  raiders with frag grenades (a building-destroying weapon) under a wood slab (a plasteel wall ring): one blast
    //       breaks it, what was on it falls, they climb in; the blaster never under the slab or its blast; V9 a granite
    //       slab: two blasts
    //   V10 a breach raid against V8's base (wood): its breach tool blasts the slab; V11 granite: twice; no degrade
    //   V12 a breach raid against V1's mountain colony: the breach tool blasts the rock ceiling (the shaft opens)
    //   V13a-c V10's fixture under three seeds: the opening point varies (the breach grid's Perlin noise)
    //   V14 a sapper raid against a base on floor 1 (wood slab, plasteel wall ring): they dig through the slab from below
    // Shots (3D on): a raider at the top of a ladder clearing it (V2 or V7), a slab blasted from below (V8).
    public static partial class TestRunner
    {
        private static ThingDef RvRock => DefDatabase<ThingDef>.GetNamed("Sandstone");

        private static readonly Dictionary<string, IntVec3> rvOpening = new Dictionary<string, IntVec3>();
        private static int rvShaftAt, rvSlabBrokenAt, rvSlabDamagedAt, rvMineUpsAt, rvTopsAt;
        private static CellRect rvRoom;
        private static int rvRoomL;
        private static readonly List<Thing> rvItems = new List<Thing>();
        private static readonly HashSet<IntVec3> rvRockBefore = new HashSet<IntVec3>();

        private static IntVec3 RvCentre(Map map) => new IntVec3(30, 0, ProjectZ.Annex(map).groundZ / 2);

        // A natural mountain over ground rect r up to level H (rock on levels 0..H-1 with the thick roof, a bare top on H),
        // its sealed room (rect room on level roomL: rock taken out, the rock above it kept) and a ring of thick roof round
        // its foot (an overhang: no ladder up its cliffs, so the way in is through the rock).
        private static void RvMountain(Map map, CellRect r, int H, CellRect room, int roomL)
        {
            ThingDef rock = RvRock;
            TerrainDef rough = rock.building.naturalTerrain;
            foreach (IntVec3 g in r)
            {
                for (int L = 0; L <= H; L++)
                {
                    IntVec3 c = Levels.Cell(g, L, map);
                    if (!c.IsValid)
                    {
                        continue;
                    }
                    if (L > 0)
                    {
                        map.terrainGrid.SetTerrain(c, rough);
                    }
                    if (L < H)
                    {
                        map.roofGrid.SetRoof(c, RoofDefOf.RoofRockThick);
                        if (!(L == roomL && room.Contains(g)))
                        {
                            GenSpawn.Spawn(ThingMaker.MakeThing(rock), c, map, WipeMode.Vanish);
                        }
                    }
                }
            }
            foreach (IntVec3 g in r.ExpandedBy(1).EdgeCells)
            {
                if (g.InBounds(map))
                {
                    map.roofGrid.SetRoof(Levels.Cell(g, 0, map), RoofDefOf.RoofRockThick);
                }
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
        }

        private static void RvColonists(Map map, CellRect inside, int L)
        {
            List<IntVec3> spots = inside.Cells.Select(g => Levels.Cell(g, L, map)).ToList();
            int i = 0;
            foreach (Pawn c in rlCols)
            {
                c.equipment?.DestroyAllEquipment();
                Teleport(c, spots[(i++ * 2) % spots.Count]);
                Draft(c, true);
            }
            rlColHealth0 = rlCols.Sum(c => c.health.summaryHealth.SummaryHealthPercent);
            rlInjuries0.Clear();
            foreach (Pawn c in rlCols)
            {
                rlInjuries0[c] = c.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);
            }
        }

        // The mountain colony: an 11x11 mountain west of the centre, a 3x3 room on level roomL.
        private static void RvMountainColony(Map map, int roomL)
        {
            RlClear(map);
            ShaftMarks.For(map)?.cells.Clear();
            IntVec3 c = RvCentre(map);
            CellRect r = CellRect.CenteredOn(c, 5);
            rvRoom = CellRect.CenteredOn(c, 1);
            rvRoomL = roomL;
            RvMountain(map, r, roomL + 2, rvRoom, roomL);
            rlBase = rvRoom;
            rlUpLevel = roomL;
            RvColonists(map, rvRoom, roomL);
            Info($"mountain colony: rock {RvRock.defName} ({RvRock.BaseMaxHitPoints} HP) over {r}, top level {roomL + 2}, room {rvRoom} on level {roomL}");
        }

        // A slab base on level L over a 12x12 rect: a wall ring (of 'wallStuff') on the slab's edge cells, the colonists
        // inside, steel on every free inside cell (it falls when its slab goes).
        private static void RvSlabBase(Map map, int L, ThingDef slabStuff, ThingDef wallStuff)
        {
            RlClear(map);
            CellRect r = RlBaseRect(map);
            rlBase = r;
            rlUpLevel = L;
            TerrainDef slab = slabStuff == null ? PZDefOf.PZ_UpperFloor : SlabDefs.For(slabStuff);
            foreach (IntVec3 g in r)
            {
                map.roofGrid.SetRoof(Levels.Cell(g, L - 1, map), RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(Levels.Cell(g, L, map), slab);
            }
            foreach (IntVec3 g in r.EdgeCells)
            {
                Place(ThingDefOf.Wall, wallStuff, Levels.Cell(g, L, map), map);
            }
            // (a roof over it: the raiders below don't see the colonists, so no grenade fight across the floors)
            foreach (IntVec3 g in r)
            {
                map.roofGrid.SetRoof(Levels.Cell(g, L, map), RoofDefOf.RoofConstructed);
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            CellRect inside = r.ContractedBy(1);
            RvColonists(map, CellRect.CenteredOn(inside.CenterCell, 1), L);
            rvItems.Clear();
            foreach (IntVec3 g in inside)
            {
                IntVec3 c = Levels.Cell(g, L, map);
                if (c.GetFirstPawn(map) == null)
                {
                    Thing s = ThingMaker.MakeThing(ThingDefOf.Steel);
                    s.stackCount = 5;
                    rvItems.Add(GenSpawn.Spawn(s, c, map));
                }
            }
            Info($"slab base on level {L}: {slab.defName} over {r}, a {wallStuff.defName} wall ring, {rvItems.Count} steel stacks inside");
        }

        private static void RvMark(Map map)
        {
            rvShaftAt = Shaft.Opened;
            rvSlabBrokenAt = SlabDamage.Broken;
            rvSlabDamagedAt = SlabDamage.Damaged;
            rvMineUpsAt = RaidVertical.MineUps;
            rvTopsAt = MineUp.TopsOpened;
        }

        // Up there and fighting, or the colonists came down with the slab and the raiders fight them.
        private static bool RvInOrFallen(Map map) => RlUpAndFighting(map) && rlHostileUp > 0 || rlFightSeen > 0 && rlCols.Any(c => c.Spawned && Levels.LevelOf(c.Position, map) < rlUpLevel);

        private static int RvFallen(Map map) => rvItems.Count(t => t.Spawned && Levels.LevelOf(t.Position, map) < rlUpLevel);

        private static string RvSummary(Map map)
        {
            string blasts = string.Join("; ", RaidVertical.Blasts.Take(6).Select(b => $"{b.weapon} mode {b.mode} from {b.blaster} (L{Levels.LevelOf(b.blaster, map)}) at {b.target} (L{Levels.LevelOf(b.target, map)}), ground distance {(Levels.Ground(b.blaster, map) - Levels.Ground(b.target, map)).LengthHorizontal:0.0}, radius {b.radius}, slab {b.before}->{b.after}"));
            return $"shafts opened {Shaft.Opened - rvShaftAt} (raider mine-ups {RaidVertical.MineUps - rvMineUpsAt}, tops {MineUp.TopsOpened - rvTopsAt}), slabs broken {SlabDamage.Broken - rvSlabBrokenAt}, damaged {SlabDamage.Damaged - rvSlabDamagedAt}, items fallen {RvFallen(map)}; blasts [{blasts}]; {RaidClimb.Stats()}; {RlSummary(map)}";
        }

        // The blaster never stood under the slab it blasted nor within its blast (ground distance >= radius + 1.5), and was
        // below it.
        private static bool RvBlastsSafe(Map map, out string why)
        {
            why = "";
            foreach (RaidVertical.BlastRec b in RaidVertical.Blasts.Where(x => x.mode == 1))
            {
                float d = (Levels.Ground(b.blaster, map) - Levels.Ground(b.target, map)).LengthHorizontal;
                if (d < b.radius + 1.5f - 0.01f || Levels.LevelOf(b.blaster, map) >= Levels.LevelOf(b.target, map))
                {
                    why = $"{b.pawn} at {b.blaster} blasted {b.target} (distance {d:0.0}, radius {b.radius})";
                    return false;
                }
            }
            return true;
        }

        // Blasts at slabs up to the first that broke one (0: none broke): a stone slab's first blast damages it and the
        // cells in its blast (Bomb), so the second, on any of them, breaks one.
        private static int RvHitsToBreak(IntVec3 c)
        {
            int n = 0;
            foreach (RaidVertical.BlastRec b in RaidVertical.Blasts.Where(x => x.mode == 1))
            {
                n++;
                if (b.after == "broken")
                {
                    return n;
                }
            }
            return 0;
        }

        private static Faction RvIndustrial() => Find.FactionManager.AllFactions.Where(x => x.HostileTo(Faction.OfPlayer) && x.def.humanlikeFaction && !x.def.hidden && !x.temporary && x.def.pawnGroupMakers != null && x.def.techLevel >= TechLevel.Industrial).OrderBy(x => x.loadID).FirstOrDefault() ?? RlEnemy();

        private static string RvDuties() => string.Join(", ", rlRaiders.Where(p => p.Spawned).GroupBy(p => p.mindState.duty?.def.defName ?? "none").Select(gr => $"{gr.Key} x{gr.Count()}"));

        private static string RvVerbs() => string.Join(", ", rlRaiders.Where(p => p.Spawned).Select(p => BreachingUtility.FindVerbToUseForBreaching(p)).Where(v => v != null).GroupBy(v => (v.EquipmentSource?.def.defName ?? "?") + (v.verbProps.ai_IsBuildingDestroyer ? "*" : "")).Select(gr => $"{gr.Key} x{gr.Count()}"));

        private static void RvGrenades(Map map)
        {
            ThingDef gren = DefDatabase<ThingDef>.GetNamed("Weapon_GrenadeFrag");
            foreach (Pawn p in rlRaiders.Where(p => p.Spawned && p.equipment != null))
            {
                p.equipment.DestroyAllEquipment();
                p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(gren));
                p.jobs?.StopAll();   // (a job taken on arrival with the old weapon)
            }
            ClimbPlans.For(map)?.Clear();
        }

        // A childhood that forbids mining (not violence): the pawn's MiningSpeed is disabled, as the game's own
        // backgrounds do it.
        private static void RvNoMining(Pawn p)
        {
            BackstoryDef bs = DefDatabase<BackstoryDef>.AllDefs.Where(b => b.slot == BackstorySlot.Childhood && (b.workDisables & WorkTags.Mining) != 0 && (b.workDisables & WorkTags.Violent) == 0).OrderBy(b => b.defName).FirstOrDefault();
            if (bs == null || p.story == null)
            {
                return;
            }
            p.story.Childhood = bs;
            p.Notify_DisabledWorkTypesChanged();
            p.skills?.Notify_SkillDisablesChanged();
            p.jobs?.StopAll();   // (a job it took on arrival, before this)
            rlNote = (rlNote ?? "") + $" {p.LabelShort}: {bs.defName}, mining disabled {StatDefOf.MiningSpeed.Worker.IsDisabledFor(p)};";
        }

        // Asks for a shot of a raider up a ladder clearing its top.
        private static void RvWantPerchShot(Map map, string name)
        {
            if (rlShotsTaken.Contains(name) || rlShot != null)
            {
                return;
            }
            LadderPerch lp = LadderPerch.For(map);
            LadderPerch.Rec r = lp?.recs.FirstOrDefault(x => x.atTop && x.pawn.Spawned && x.pawn.CurJob != null && x.pawn.CurJobDef != JobDefOf.Wait);
            if (r != null)
            {
                rlShot = name;
                rlShotPawn = r.pawn;
                rlShotFloor = Levels.LevelOf(r.ladder.top, map);
            }
        }

        // ... and of a slab just broken from below.
        private static void RvWantSlabShot(Map map, string name)
        {
            if (rlShotsTaken.Contains(name) || rlShot != null || SlabDamage.Broken == rvSlabBrokenAt || RaidVertical.Blasts.Count == 0)
            {
                return;
            }
            RaidVertical.BlastRec last = RaidVertical.Blasts[RaidVertical.Blasts.Count - 1];
            if (Find.TickManager.TicksGame - last.tick < 90)
            {
                return;   // (what was on it has come down)
            }
            rlShot = name;
            rlShotPawn = null;
            rlBase = CellRect.CenteredOn(Levels.Ground(last.target, map), 1);
            rlShotFloor = rlUpLevel;
        }

        // Every 2500 ticks: where the raiders are and what they do.
        private static void RvTrace(Map map)
        {
            int t = Find.TickManager.TicksGame - rlT0;
            if (t % 2500 >= 10)
            {
                return;
            }
            Info($"  t{t}: " + string.Join("; ", rlRaiders.Where(p => p.Spawned).Select(p => $"{p.LabelShort} {p.Position} L{Levels.LevelOf(p.Position, map)} {p.CurJobDef?.defName}{(p.CurJob?.targetA.IsValid ?? false ? " " + p.CurJob.targetA : "")} {(p.Downed ? "downed" : "")}")));
        }

        private static IEnumerable<RlCase> RvCases()
        {
            // ---- V0: explosions from above
            yield return new RlCase
            {
                id = "v0", name = "explosions from the sky side on a mountain", maxTicks = 60,
                setup = m =>
                {
                    RvMountainColony(m, 1);
                    rvShaftAt = Shaft.Opened;
                    IntVec3 g = RvCentre(m);
                    IntVec3 edgeTop = Levels.Cell(new IntVec3(g.x - 5, 0, g.z), 3, m);
                    rvRockBefore.Clear();
                    foreach (IntVec3 c in CellRect.CenteredOn(g, 5))
                    {
                        for (int L = 0; L <= 2; L++)
                        {
                            IntVec3 x = Levels.Cell(c, L, m);
                            if (x.GetEdifice(m) != null)
                            {
                                rvRockBefore.Add(x);
                            }
                        }
                    }
                    IntVec3 top = Levels.Cell(g, 3, m);
                    rlNote = $"top {top} ({top.GetTerrain(m)?.defName}, edifice {top.GetEdifice(m)}), the top's edge {edgeTop} (Column.Top {Column.Top(edgeTop, m)})";
                    for (int i = 0; i < 6; i++)
                    {
                        // (what lands from the sky lands on the column's top: SkyStrikes)
                        GenExplosion.DoExplosion(Column.Top(top, m), m, 3.9f, DamageDefOf.Bomb, null, 120);
                        GenExplosion.DoExplosion(Column.Top(edgeTop, m), m, 3.9f, DamageDefOf.Bomb, null, 120);
                    }
                },
                done = m => false,
                check = m =>
                {
                    IntVec3 g = RvCentre(m);
                    CellRect r = CellRect.CenteredOn(g, 5);
                    int open = 0, roofsLost = 0, rockGoneBelowTop = 0;
                    foreach (IntVec3 c in r)
                    {
                        for (int L = 1; L <= 3; L++)
                        {
                            if (!ProjectZ.HasFloor(Levels.Cell(c, L, m), m))
                            {
                                open++;
                            }
                        }
                        for (int L = 0; L <= 2; L++)
                        {
                            IntVec3 x = Levels.Cell(c, L, m);
                            if (!(m.roofGrid.RoofAt(x)?.isThickRoof ?? false))
                            {
                                roofsLost++;
                            }
                            if (!(L == 1 && rvRoom.Contains(c)) && x.GetEdifice(m) == null)
                            {
                                rockGoneBelowTop++;
                                rlNote += $"; no rock at {x} L{L} ({string.Join("/", x.GetThingList(m).Select(t => t.def.defName))}, before {(rvRockBefore.Contains(x) ? "rock" : "none")})";
                            }
                        }
                    }
                    Result("V0 explosions from the sky side (on the column tops: the middle and the edge of a mountain top): no shaft, no open-air cell, the thick roofs and all the rock under the top kept", Shaft.Opened == rvShaftAt && open == 0 && roofsLost == 0 && rockGoneBelowTop == 0,
                        $"{rlNote}; shafts {Shaft.Opened - rvShaftAt}, open-air cells {open}, thick roofs lost {roofsLost}, rock gone under the top level {rockGoneBelowTop}");
                },
            };
            // ---- V1..V6: the mountain colony
            foreach ((string id, int L, string strat, string who) in new[] { ("v1", 1, "ImmediateAttack", "raiders"), ("v2", 2, "ImmediateAttack", "raiders"), ("v3", 1, "ImmediateAttackSappers", "sappers"), ("v4", 2, "ImmediateAttackSappers", "sappers"), ("v5", 1, "ImmediateAttack", "nominers"), ("v6", 1, "ImmediateAttack", "mechs") })
            {
                yield return new RlCase
                {
                    id = id, name = $"{who} against a sealed mountain colony {L} level(s) up", maxTicks = who == "nominers" ? 12000 : 60000,
                    setup = m =>
                    {
                        RvMountainColony(m, L);
                        RvMark(m);
                        Faction f = who == "mechs" ? Faction.OfMechanoids : who == "sappers" ? RvIndustrial() : RlEnemy();
                        RlRaid(m, f, strat, PawnsArrivalModeDefOf.EdgeWalkIn, RlWestEdge(m), who == "mechs" ? 300f : 250f);
                        if (who == "nominers")
                        {
                            foreach (Pawn p in rlRaiders)
                            {
                                RvNoMining(p);
                            }
                        }
                        rlNote = $"first toil {rlLord?.CurLordToil?.GetType().Name}, duties {RvDuties()};" + rlNote;
                    },
                    done = m =>
                    {
                        RvTrace(m);
                        if (id == "v2")
                        {
                            RvWantPerchShot(m, "rv_v2_raider_clearing_ladder_top");
                        }
                        return who != "nominers" && RlUpAndFighting(m) && (id != "v2" || rlShotsTaken.Contains("rv_v2_raider_clearing_ladder_top") || RaidVertical.Perches == 0);
                    },
                    check = m =>
                    {
                        int mined = Shaft.Opened - rvShaftAt;
                        string toil = rlLord?.CurLordToil?.GetType().Name;
                        if (who == "nominers")
                        {
                            Result($"{id.ToUpperInvariant()} raiders who can't mine against a sealed mountain colony: no shaft opened, no mining job up", mined == 0 && RaidVertical.MineJobs == 0 && RaidVertical.MineUps == rvMineUpsAt, $"{rlNote}; {RvSummary(m)}");
                            return;
                        }
                        bool lift = who != "mechs" || rlLadders.Any(b => b.def == PZDefOf.PZ_SiegeLift);
                        bool sapperKept = who != "sappers" || RaidDegrade.Degraded == rlDegradedAt && RaidVertical.VerticalDests > 0;
                        Result($"{id.ToUpperInvariant()} {who} against a sealed mountain colony {L} level(s) up: they mine up (a shaft per level), place a {(who == "mechs" ? "lift" : "ladder")}, get up and fight{(who == "sappers" ? "; the lord stays a sapper raid (no degrade; its target chosen with the vertical moves)" : "")}",
                            mined >= L && rlLadders.Count >= L && rlHostileUp > 0 && RlUpAndFighting(m) && lift && sapperKept,
                            $"{rlNote}; toil now {toil}, duties {RvDuties()}, degraded {RaidDegrade.Degraded - rlDegradedAt}; {RvSummary(m)}");
                    },
                };
            }
            // ---- V7: a wall ring on the rim
            yield return new RlCase
            {
                id = "v7", name = "a base on floor 2 with a wooden wall ring on its rim, no stairs", maxTicks = 30000,
                setup = m =>
                {
                    RvSlabBase(m, 2, null, ThingDefOf.WoodLog);
                    RvMark(m);
                    RlRaid(m, RlEnemy(), "ImmediateAttack", PawnsArrivalModeDefOf.EdgeWalkIn, RlWestEdge(m), 250f);
                },
                done = m =>
                {
                    RvWantPerchShot(m, "rv_v7_raider_clearing_wall_from_ladder");
                    return RlUpAndFighting(m) && RaidVertical.PerchSteppedIn > 0 && rlShotsTaken.Contains("rv_v7_raider_clearing_wall_from_ladder");
                },
                check = m => Result("V7 a wall ring on the rim of a floor-2 base: a ladder to the wall, the raider clears it from the ladder's top, steps in, they fight up there",
                    rlLadders.Any(b => Levels.LevelOf(b.top, m) == 2) && RaidVertical.Perches > 0 && RaidVertical.PerchSteppedIn > 0 && RlUpAndFighting(m), RvSummary(m)),
            };
            // ---- V8, V9: grenades under a slab
            foreach ((string id, string stuff, int hits) in new[] { ("v8", (string)null, 1), ("v9", "BlocksGranite", 2) })
            {
                yield return new RlCase
                {
                    id = id, name = $"raiders with frag grenades under a {(stuff ?? "wood")} slab", maxTicks = 30000,
                    setup = m =>
                    {
                        RvSlabBase(m, 2, stuff == null ? null : DefDatabase<ThingDef>.GetNamed(stuff), DefDatabase<ThingDef>.GetNamed("Plasteel"));
                        RvMark(m);
                        RlRaid(m, RlEnemy(), "ImmediateAttack", PawnsArrivalModeDefOf.EdgeWalkIn, RlWestEdge(m), 250f);
                        RvGrenades(m);
                    },
                    done = m =>
                    {
                        RvTrace(m);
                        RvWantSlabShot(m, $"rv_{id}_slab_blasted_from_below");
                        return RvInOrFallen(m) && SlabDamage.Broken > rvSlabBrokenAt && rlShotsTaken.Contains($"rv_{id}_slab_blasted_from_below");
                    },
                    check = m =>
                    {
                        bool safe = RvBlastsSafe(m, out string why);
                        IntVec3 first = RaidVertical.Blasts.Where(b => b.mode == 1).Select(b => b.target).FirstOrDefault();
                        int onFirst = RvHitsToBreak(first);
                        Result($"{id.ToUpperInvariant()} frag grenades under a {(stuff ?? "wood")} slab: {hits} blast(s) break it, what was on it falls, the raiders climb in (or fight the colonists who fell with it); the blaster never under the slab or its blast",
                            SlabDamage.Broken > rvSlabBrokenAt && onFirst == hits && (hits == 1 || SlabDamage.Damaged > rvSlabDamagedAt) && RvFallen(m) > 0 && RvInOrFallen(m) && safe,
                            $"blasts up to the first break {onFirst} (first at {first}); {why} {RvSummary(m)}");
                    },
                };
            }
            // ---- V10, V11, V13: breach raids under a slab
            foreach ((string id, string stuff) in new[] { ("v10", (string)null), ("v11", "BlocksGranite"), ("v13a", null), ("v13b", null), ("v13c", null) })
            {
                yield return new RlCase
                {
                    id = id, name = $"a breach raid under a {(stuff ?? "wood")} slab", maxTicks = 30000,
                    setup = m =>
                    {
                        RvSlabBase(m, 2, stuff == null ? null : DefDatabase<ThingDef>.GetNamed(stuff), DefDatabase<ThingDef>.GetNamed("Plasteel"));
                        RvMark(m);
                        RlRaid(m, Faction.OfMechanoids, "ImmediateAttackBreaching", PawnsArrivalModeDefOf.EdgeWalkIn, RlWestEdge(m), 600f);
                        rlNote = $"first toil {rlLord?.CurLordToil?.GetType().Name}, breach verbs {RvVerbs()}";
                    },
                    done = m => id.StartsWith("v13") ? RaidVertical.Blasts.Any(b => b.mode == 1) : RvInOrFallen(m) && SlabDamage.Broken > rvSlabBrokenAt,
                    check = m =>
                    {
                        string toil = rlLord?.CurLordToil?.GetType().Name;
                        IntVec3 first = RaidVertical.Blasts.Where(b => b.mode == 1).Select(b => b.target).FirstOrDefault();
                        if (id.StartsWith("v13"))
                        {
                            rvOpening[id] = first;
                            Info($"{id} breach opening at {first}; {rlNote}; {RvSummary(m)}");
                            if (id == "v13c")
                            {
                                int distinct = rvOpening.Values.Where(c => c.IsValid).Distinct().Count();
                                Result("V13 a breach raid's opening point varies from raid to raid (three seeds)", rvOpening.Count == 3 && rvOpening.Values.All(c => c.IsValid) && distinct >= 2, string.Join(", ", rvOpening.Select(kv => kv.Key + " " + kv.Value)));
                            }
                            return;
                        }
                        bool safe = RvBlastsSafe(m, out string why);
                        int onFirst = RvHitsToBreak(first);
                        Result($"{id.ToUpperInvariant()} a mechanoid breach raid under a {(stuff ?? "wood")} slab: no degrade, the breach tool (thump cannon) blasts the slab ({(stuff == null ? "once" : "twice")}), they climb in and fight (or fight the colonists who fell with it)",
                            RaidDegrade.Degraded == rlDegradedAt && SlabDamage.Broken > rvSlabBrokenAt && onFirst == (stuff == null ? 1 : 2) && RvInOrFallen(m) && safe,
                            $"{rlNote}; toil now {toil}, duties {RvDuties()}; {why} {RvSummary(m)}");
                    },
                };
            }
            // ---- V12: a breach raid against the mountain colony
            yield return new RlCase
            {
                id = "v12", name = "a breach raid against a sealed mountain colony one level up", maxTicks = 60000,
                setup = m =>
                {
                    RvMountainColony(m, 1);
                    RvMark(m);
                    RlRaid(m, RlEnemy(), "ImmediateAttackBreaching", PawnsArrivalModeDefOf.EdgeWalkIn, RlWestEdge(m), 500f);
                    rlNote = $"first toil {rlLord?.CurLordToil?.GetType().Name}, breach verbs {RvVerbs()}";
                },
                done = m => RlUpAndFighting(m),
                check = m => Result("V12 a tribal breach raid (breach axes) against a sealed mountain colony: no degrade, the breach tool blasts the rock ceiling (the shaft opens as when mined), they climb in and fight",
                    RaidDegrade.Degraded == rlDegradedAt && RaidVertical.RockBlastOpened > 0 && rlHostileUp > 0 && RlUpAndFighting(m),
                    $"{rlNote}; toil now {rlLord?.CurLordToil?.GetType().Name}, duties {RvDuties()}; {RvSummary(m)}"),
            };
            // ---- V14: sappers dig through a slab
            yield return new RlCase
            {
                id = "v14", name = "a sapper raid against a base on floor 1 (wood slab, plasteel ring)", maxTicks = 40000,
                setup = m =>
                {
                    RvSlabBase(m, 1, null, DefDatabase<ThingDef>.GetNamed("Plasteel"));
                    RvMark(m);
                    RlRaid(m, RvIndustrial(), "ImmediateAttackSappers", PawnsArrivalModeDefOf.EdgeWalkIn, RlWestEdge(m), 300f);
                    rlNote = $"first toil {rlLord?.CurLordToil?.GetType().Name}, duties {RvDuties()}";
                },
                done = m => RlUpAndFighting(m) && SlabDamage.Broken > rvSlabBrokenAt,
                check = m => Result("V14 a sapper raid against a floor-1 base walled in plasteel: no degrade, the sappers dig through the slab from below, climb in and fight",
                    RaidDegrade.Degraded == rlDegradedAt && RaidVertical.SlabHitsDig > 0 && SlabDamage.Broken > rvSlabBrokenAt && rlHostileUp > 0 && RlUpAndFighting(m),
                    $"{rlNote}; toil now {rlLord?.CurLordToil?.GetType().Name}, duties {RvDuties()}; {RvSummary(m)}"),
            };
        }
    }
}
