using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode ladders, after the stage 1 checks (TestLadders): the follow-ups of 2026-10-20. Same fixtures (LdFixture: a
    // platform on level n, its west edge cell the top, the column at (19, 13) down to the foot), real orders, jobs, ticks:
    //   COL  the ladder's column stays open like a stairwell: a floor, sandbags or a wall in it refused (for every faction:
    //        the one placement check), a roof over it refused (the roof area designator; a colonist ordered to roof the
    //        cells in the area builds nothing there); a ladder (the place-ladder job, the emergency ladder blueprint) is
    //        refused while a blueprint stands in the column; the safety net: a floor laid in the column (as a gravship
    //        landing or a dev spawn would), a roof put over it, a wall spawned in it: the ladder comes down at once and
    //        its climber falls.
    //   END  a climber reaching the end of the ladder while another pawn stands on that cell: the pawn there steps aside
    //        (a raider on the top while a colonist climbs up; a colonist waiting on the top while a raider climbs up).
    //   MEL  melee across a ladder: over 2 floors the attacker does not hit from the foot, it climbs to its target; over 1
    //        floor it hits from the foot (as across stairs). Work across any span: the emergency ladder built from the
    //        foot (E2, TestLadders).
    public static partial class TestRunner
    {
        private static Pawn ld2Q;
        private static int ld2Hp0, ld2Falls0, ld2Cleared0;
        private static bool ld2HitFromFoot;
        private static int ld2Mode;

        private static bool StepLadders2Inner(Map map)
        {
            switch (stage)
            {
                case "ld_col":
                    Ld2Column(map);
                    return true;
                case "ld_col_roof_check":
                    Ld2ColumnRoofCheck(map);
                    return true;
                case "ld_col_busy":
                    Ld2ColumnBusy(map);
                    return true;
                case "ld_col_busy_check":
                    Ld2ColumnBusyCheck(map);
                    return true;
                case "ld_col_net":
                    Ld2Net(map);
                    return true;
                case "ld_col_net_fill":
                    Ld2NetFill(map);
                    return true;
                case "ld_col_net_check":
                    Ld2NetCheck(map);
                    return true;
                case "ld_end":
                    Ld2End(map, false);
                    return true;
                case "ld_end_check":
                    Ld2EndCheck(map, false);
                    return true;
                case "ld_end2":
                    Ld2End(map, true);
                    return true;
                case "ld_end2_check":
                    Ld2EndCheck(map, true);
                    return true;
                case "ld_mel":
                    Ld2Melee(map, 2);
                    return true;
                case "ld_mel_check":
                    Ld2MeleeCheck(map, 2);
                    return true;
                case "ld_mel1":
                    Ld2Melee(map, 1);
                    return true;
                case "ld_mel1_check":
                    Ld2MeleeCheck(map, 1);
                    return true;
            }
            return false;
        }

        private static IntVec3 Ld2Col(int L, Map map) => LdL(19, 13, L, map);

        // ------------------------------------------------------------------------------------------------ COL

        private static void Ld2Column(Map map)
        {
            LdFixture(map, 2, LdRaiders(), false, r2P);
            string open = "PZ_LadderColumnOpen".Translate();
            AcceptanceReport floor1 = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_UpperFloor, Ld2Col(1, map), Rot4.North, map);
            AcceptanceReport floor2 = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_UpperFloor, Ld2Col(2, map), Rot4.North, map);
            AcceptanceReport sandbags = GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Sandbags, Ld2Col(1, map), Rot4.North, map, stuffDef: ThingDefOf.Cloth);
            AcceptanceReport wall = GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, Ld2Col(2, map), Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
            AcceptanceReport beside = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_UpperFloor, LdL(19, 12, 1, map), Rot4.North, map);
            Result("COL a floor (levels 1 and 2), sandbags or a wall in the ladder's column are refused ('Keep the space along the ladder open')",
                floor1.Reason == open && floor2.Reason == open && sandbags.Reason == open && wall.Reason == open,
                $"floor L1 {floor1.Reason}, floor L2 {floor2.Reason}, sandbags {sandbags.Reason}, wall {wall.Reason}; a floor beside the column: {(beside.Accepted ? "accepted" : beside.Reason)}");
            Designator_AreaBuildRoof roofDes = new Designator_AreaBuildRoof();
            AcceptanceReport roofFoot = roofDes.CanDesignateCell(ldFoot);
            AcceptanceReport roofL1 = roofDes.CanDesignateCell(Ld2Col(1, map));
            Result("COL a roof over the column (the foot's cell, level 1) can't be designated", !roofFoot.Accepted && !roofL1.Accepted && roofFoot.Reason == open,
                $"foot {roofFoot.Reason}, level 1 {roofL1.Reason}");
            // the area painted anyway (it was there before, or auto-roof added it): a colonist with roofing work builds nothing
            // there; the roof beside the ladder is built
            map.areaManager.BuildRoof[ldFoot] = true;
            map.areaManager.BuildRoof[Ld2Col(1, map)] = true;
            IntVec3 beside0 = LdG(18, 13);
            map.areaManager.BuildRoof[beside0] = true;
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, LdG(17, 13), map);
            Teleport(r2P, LdG(16, 12));
            Draft(r2P, false);
            R2Work(r2P, WorkTypeDefOf.Construction);
            SeedTicks("ld_col_roof");
            TickUntil(2500, () => map.roofGrid.Roofed(beside0), "ld_col_roof_check", 30);
        }

        private static void Ld2ColumnRoofCheck(Map map)
        {
            tickWatch = null;
            Draft(r2P, true);
            Result("COL a colonist roofing the area builds the roof beside the column, none over it; the ladder stands",
                map.roofGrid.Roofed(LdG(18, 13)) && !map.roofGrid.Roofed(ldFoot) && !map.roofGrid.Roofed(Ld2Col(1, map)) && ldLadder.Spawned,
                $"beside {map.roofGrid.Roofed(LdG(18, 13))}, foot {map.roofGrid.Roofed(ldFoot)}, level 1 {map.roofGrid.Roofed(Ld2Col(1, map))}, ladder {ldLadder.Spawned}, worker job {r2P.CurJobDef?.defName}");
            map.areaManager.BuildRoof[ldFoot] = false;
            map.areaManager.BuildRoof[Ld2Col(1, map)] = false;
            map.areaManager.BuildRoof[LdG(18, 13)] = false;
            stage = "ld_col_busy";
        }

        // A blueprint in the column: no ladder there (the emergency ladder's placement, a raider's place-ladder job).
        private static void Ld2ColumnBusy(Map map)
        {
            LdFixture(map, 2, null, false, r2P);
            GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_UpperFloor, Ld2Col(1, map), map, Rot4.North, Faction.OfPlayer, null);
            AcceptanceReport em = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_EmergencyLadder, ldTop, Rot4.West, map);
            bool found = LadderSite.Find(ldTop, Rot4.West, map, out _, out _, out string why);
            Result("COL with a floor blueprint in the column the emergency ladder is refused ('Something is in the way down') and no ladder site is found",
                !em.Accepted && em.Reason == "PZ_LadderColumnBlocked".Translate() && !found && why == "column not clear", $"emergency {em.Reason}, site {found} ({why})");
            R2Hide(map);
            ldR = MakeRaider(map, LdG(12, 13));
            ldR.equipment.DestroyAllEquipment();
            ldR.jobs.StartJob(JobDriver_PlaceSiegeLadder.Make(ldFoot, ldTop), JobCondition.InterruptForced);
            SeedTicks("ld_col_busy");
            TickUntil(800, () => ldR.CurJobDef != PZJobDefOf.PZ_PlaceSiegeLadder, "ld_col_busy_check", 10);
        }

        private static void Ld2ColumnBusyCheck(Map map)
        {
            tickWatch = null;
            Result("COL a raider's place-ladder job with a blueprint in the column places no ladder", ldFoot.GetFirstThing<Building_SiegeLadder>(map) == null,
                $"raider job now {ldR.CurJobDef?.defName}, at {ldR.Position}");
            ldR.Destroy();
            R2Unhide(map);
            ld2Mode = 0;
            stage = "ld_col_net";
        }

        // The safety net: a climber half way up, then the column fills (0: a floor laid in it, 1: a roof over the foot,
        // 2: a wall spawned in it).
        private static void Ld2Net(Map map)
        {
            LdFixture(map, 2, LdRaiders(), false, r2P);
            Heal(r2P);
            Teleport(r2P, LdG(16, 13));
            Draft(r2P, true);
            ldDest = LdL(24, 13, 2, map);
            GoTo(r2P, ldDest);
            ld2Falls0 = LinkSteps.Falls;
            SeedTicks("ld_col_net" + ld2Mode);
            TickUntil(2000, () => LdPct(map) >= 0.5f && Links.Kind(r2P) == StepKind.LadderUp, "ld_col_net_fill", 1);
        }

        private static void Ld2NetFill(Map map)
        {
            tickWatch = null;
            ldHeightAt = LdStep(map, r2P)?.height ?? -1f;
            if (ld2Mode == 0)
            {
                map.terrainGrid.SetTerrain(Ld2Col(1, map), PZDefOf.PZ_UpperFloor);
            }
            else if (ld2Mode == 1)
            {
                map.roofGrid.SetRoof(ldFoot, RoofDefOf.RoofConstructed);
            }
            else
            {
                // (a building put there as a landing gravship places its things: gravity leaves them, Gravity.On)
                Gravity.Suspend();
                try
                {
                    GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog), Ld2Col(2, map), map);
                }
                finally
                {
                    Gravity.Resume();
                }
            }
            SeedTicks("ld_col_net_fill" + ld2Mode);
            TickUntil(120, () => ldLadder.Destroyed && Links.Kind(r2P) == StepKind.None, "ld_col_net_check", 1);
        }

        private static void Ld2NetCheck(Map map)
        {
            tickWatch = null;
            string what = ld2Mode == 0 ? "a floor laid in the column" : ld2Mode == 1 ? "a roof put over its foot" : "a wall spawned in the column";
            Result($"COL safety net: {what} brings the ladder down at once; the climber falls from {ldHeightAt:0.0} floors",
                ldLadder.Destroyed && LinkSteps.Falls > ld2Falls0 && Levels.LevelOf(r2P.Position, map) == 0 && StairGrid.For(map)?.IndexOf(ldLadder) is null or < 0,
                $"ladder destroyed {ldLadder.Destroyed}, falls {LinkSteps.Falls - ld2Falls0}, climber at {r2P.Position} L{Levels.LevelOf(r2P.Position, map)}, column kills {LadderColumnWatch.Destroyed}");
            ld2Mode++;
            stage = ld2Mode < 3 ? "ld_col_net" : "ld_end";
        }

        // ------------------------------------------------------------------------------------------------ END

        // raiderUp false: a raider stands on the top, a colonist climbs up; true: a colonist waits on the top, a raider
        // climbs up. Both in a fight (the game's collision is on: anyCloseHostilesRecently).
        private static void Ld2End(Map map, bool raiderUp)
        {
            LdFixture(map, 2, LdRaiders(), false, r2P);
            R2Hide(map, r2P);
            Heal(r2P);
            ld2Cleared0 = LinkSteps.EndsCleared;
            ldDest = LdL(24, 13, 2, map);
            if (!raiderUp)
            {
                ldR = MakeRaider(map, ldTop);
                ldR.equipment.DestroyAllEquipment();
                Job wait = JobMaker.MakeJob(JobDefOf.Wait, 20000);
                ldR.jobs.StartJob(wait, JobCondition.InterruptForced);
                Teleport(r2P, LdG(16, 13));
                GoTo(r2P, ldDest);
            }
            else
            {
                Teleport(r2P, ldTop);
                Draft(r2P, true);
                // (at the foot, so its own AI has no time to pick another job before the climb holds it)
                ldR = MakeRaider(map, ldFoot);
                ldR.equipment.DestroyAllEquipment();
                Job up = JobMaker.MakeJob(JobDefOf.Goto, ldDest);
                up.locomotionUrgency = LocomotionUrgency.Jog;
                ldR.jobs.StartJob(up, JobCondition.InterruptForced);
            }
            ld2Q = raiderUp ? ldR : r2P;   // the climber
            Pawn stander = raiderUp ? r2P : ldR;
            ldFlag = false;
            SeedTicks("ld_end" + raiderUp);
            TickUntil(3000, () =>
            {
                r2P.mindState.anyCloseHostilesRecently = true;
                ldR.mindState.anyCloseHostilesRecently = true;
                if (stander.Position != ldTop)
                {
                    ldFlag = true;   // stepped off the top
                }
                return Levels.LevelOf(ld2Q.Position, map) == 2;   // (on the top: what follows, a fight, is not this check)
            }, raiderUp ? "ld_end2_check" : "ld_end_check", 1);
        }

        private static void Ld2EndCheck(Map map, bool raiderUp)
        {
            tickWatch = null;
            Pawn stander = raiderUp ? r2P : ldR;
            Result(raiderUp ? "END a raider climbing up gets onto the top past a colonist waiting there: the colonist steps aside on its level"
                    : "END a colonist climbing up gets onto the top past a raider standing there: the raider steps aside on its level",
                Levels.LevelOf(ld2Q.Position, map) == 2 && ldFlag && Levels.LevelOf(stander.Position, map) == 2 && LinkSteps.EndsCleared > ld2Cleared0,
                $"climber {ld2Q} at {ld2Q.Position} L{Levels.LevelOf(ld2Q.Position, map)} job {ld2Q.CurJobDef?.defName} (top {ldTop}), stander {stander} at {stander.Position} L{Levels.LevelOf(stander.Position, map)}, ends cleared {LinkSteps.EndsCleared - ld2Cleared0}");
            if (!ldR.Destroyed)
            {
                ldR.Destroy();
            }
            R2Unhide(map);
            stage = raiderUp ? "ld_mel" : "ld_end2";
        }

        // ------------------------------------------------------------------------------------------------ MEL

        private static void Ld2Melee(Map map, int span)
        {
            LdFixture(map, span, LdRaiders(), false, r2P);
            R2Hide(map, r2P);
            Heal(r2P);
            Teleport(r2P, ldTop);
            Draft(r2P, true);
            ldR = MakeRaider(map, ldFoot);
            ldR.equipment.DestroyAllEquipment();
            ld2Hp0 = r2P.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);
            ld2HitFromFoot = false;
            ldR.jobs.StartJob(JobMaker.MakeJob(JobDefOf.AttackMelee, r2P), JobCondition.InterruptForced);
            SeedTicks("ld_mel" + span);
            TickUntil(span == 2 ? 2000 : 600, () =>
            {
                r2P.mindState.anyCloseHostilesRecently = true;
                ldR.mindState.anyCloseHostilesRecently = true;
                bool hurt = r2P.health.hediffSet.hediffs.Count(h => h is Hediff_Injury) > ld2Hp0;
                if (hurt && Levels.LevelOf(ldR.Position, map) == 0)
                {
                    ld2HitFromFoot = true;
                }
                return span == 2 ? Levels.LevelOf(ldR.Position, map) == 2 && hurt : hurt;
            }, span == 2 ? "ld_mel_check" : "ld_mel1_check", 1);
        }

        private static void Ld2MeleeCheck(Map map, int span)
        {
            tickWatch = null;
            bool hurt = r2P.health.hediffSet.hediffs.Count(h => h is Hediff_Injury) > ld2Hp0;
            if (span == 2)
            {
                Result("MEL over a 2-floor ladder the raider does not hit from the foot: it climbs up and hits on the top",
                    !ld2HitFromFoot && Levels.LevelOf(ldR.Position, map) == 2 && hurt, $"raider at {ldR.Position} L{Levels.LevelOf(ldR.Position, map)}, job {ldR.CurJobDef?.defName}, colonist hurt {hurt}, hit from the foot {ld2HitFromFoot}");
            }
            else
            {
                Result("MEL over a 1-floor ladder the raider hits from the foot (as across stairs)", ld2HitFromFoot && hurt,
                    $"raider at {ldR.Position} L{Levels.LevelOf(ldR.Position, map)}, colonist hurt {hurt}, hit from the foot {ld2HitFromFoot}");
            }
            if (!ldR.Destroyed)
            {
                ldR.Destroy();
            }
            Heal(r2P);
            R2Unhide(map);
            if (span == 2)
            {
                stage = "ld_mel1";
                return;
            }
            Info($"ladder follow-ups: ends cleared {LinkSteps.EndsCleared}, column kills {LadderColumnWatch.Destroyed}");
            Finish();
        }
    }
}
