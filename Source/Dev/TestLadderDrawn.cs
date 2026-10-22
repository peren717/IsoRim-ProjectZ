using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode ladderpose, part PW (bugs3fix 2026-10-21): the DRAWN figure on a ladder, in real time. IsoRim draws a moving
    // figure facing the way it goes; ProjectZ holds a climber to the game's facing (IsoRim Api 19 AddFacingHold with
    // Links.LadderPose, IsoRimBridge), so it faces the ladder going down too (before: its back to the wall, 180 deg off).
    // The game runs at Normal speed with the camera on the ladder, so IsoRim draws every frame; each frame on the ladder
    // the figure's drawn yaw (IsoRim Api.Dev.DrawnYaw) is checked against the ladder's facing:
    //   - one short turn when it takes hold (from the side going up, turning round at the top going down): within 45 deg
    //     of the ladder by 20% of the climb at the latest,
    //   - after that never more than 45 deg off, and within 5 deg over the second half of the climb;
    //   - drawn on every frame on the ladder; the game's facing is the ladder's on every frame.
    //   PW  the player's emergency ladder against a one-floor wall block (walls on the ground, a floor on level 1 over
    //       them), at the 4 ladder turns (open side S, E, N, W): a drafted colonist walks to it from the side, climbs up
    //       onto the block, then back down to the ground;
    //   PWR a raider climbs the raiders' siege ladder of 2 floors up and back down.
    // Screenshots at 50% of each climb. Needs IsoRim with Api 19 (without it: one FAIL saying so).
    public static partial class TestRunner
    {
        private static readonly Rot4[] pwTurns = { Rot4.South, Rot4.East, Rot4.North, Rot4.West };
        private static int pwI;
        private static bool pwUp, pwShot, pwSettled;
        private static Pawn pwP;
        private static Rot4 pwWant;
        private static string pwName;
        private static IntVec3 pwC, pwFoot, pwDest;
        private static int pwFrames, pwPosed, pwGameWrong, pwNotDrawn, pwOff, pwT0;
        private static float pwTurnPct, pwMaxLate;
        private static string pwFirstWrong, pwTrace;

        private static bool StepLadderDrawn(Map map)
        {
            switch (stage)
            {
                case "ld_pw":
                    if (!IsoRimBridge.HasDrawnYaw)
                    {
                        Result("PW the drawn figure on a ladder: needs IsoRim Api 19 (Api.Dev.DrawnYaw)", false, $"IsoRim present {IsoRimBridge.Present}, Api {IsoRimBridge.ApiVersion}");
                        stage = "ld_ps";
                        return true;
                    }
                    pwI = 0;
                    stage = "ld_pw_fixture";
                    return true;
                case "ld_pw_fixture":
                    PwFixture(map);
                    return true;
                case "ld_pw_go":
                    PwGo(map);
                    return true;
                case "ld_pw_run":
                    PwRun(map);
                    return true;
                case "ld_pwr":
                    PwRaider(map);
                    return true;
                case "ld_pwr_go":
                    PwRaiderGo(map);
                    return true;
                case "ld_pwr_run":
                    PwRun(map);
                    return true;
            }
            return false;
        }

        // A 5x5 block (ground x 20..24, z 12..16): walls round its rim on the ground, a floor on level 1 over all of it.
        // The ladder's top: the rim cell on level 1 toward the turn (the open side); its foot: the ground cell beyond.
        private static void PwFixture(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Rot4 turn = pwTurns[pwI];
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            LinkSteps.For(map)?.steps.Clear();
            CellRect block = new CellRect(LdG(20, 12).x, LdG(20, 12).z, 5, 5);
            pwC = LdG(22, 14);
            foreach (IntVec3 g in block.EdgeCells)
            {
                R2Wall(map, g);
            }
            R2Floor(map, block, 1);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            IntVec3 rim = pwC + turn.FacingCell * 2;
            IntVec3 top = Levels.Cell(rim, 1, map);
            Building_SiegeLadder lad = Place(PZDefOf.PZ_EmergencyLadder, null, top, map, turn) as Building_SiegeLadder;
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            pwFoot = lad != null ? lad.foot : IntVec3.Invalid;
            Result($"PW {turn.ToStringHuman()} fixture: emergency ladder against the wall block, open side {turn.ToStringHuman()}",
                lad != null && lad.top == top && pwFoot == rim + turn.FacingCell, $"top {lad?.top} foot {lad?.foot} span {lad?.span}");
            // from the side: the colonist starts 4 cells out and 3 to the side (approach not along the facing)
            IntVec3 side = turn.Rotated(RotationDirection.Clockwise).FacingCell;
            Teleport(r2P, rim + turn.FacingCell * 4 + side * 3);
            Draft(r2P, true);
            r2P.Rotation = Rot4.South;
            pwP = r2P;
            pwWant = turn.Opposite;   // facing the ladder: toward the wall
            pwUp = true;
            stage = "ld_pw_go";
        }

        private static void PwGo(Map map)
        {
            Rot4 turn = pwTurns[pwI];
            pwName = $"PW {turn.ToStringHuman()} {(pwUp ? "up" : "down")}";
            pwDest = pwUp ? Levels.Cell(pwC, 1, map) : pwFoot + turn.FacingCell * 3 + turn.Rotated(RotationDirection.Counterclockwise).FacingCell * 2;
            PwStart(map, 1, pwFoot);
            GoTo(r2P, pwDest);
        }

        private static void PwStart(Map map, int floor, IntVec3 at)
        {
            pwFrames = pwPosed = pwGameWrong = pwNotDrawn = pwOff = 0;
            pwTurnPct = -1f;
            pwMaxLate = 0f;
            pwSettled = false;
            pwFirstWrong = null;
            pwTrace = "";
            pwShot = false;
            ShotAt(map, floor, at, 7f);   // the camera on the ladder, the floor shown, the default camera yaw
            pwT0 = Find.TickManager.TicksGame;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            stage = stage.StartsWith("ld_pwr") ? "ld_pwr_run" : "ld_pw_run";
        }

        // Each frame: the climber on its ladder (taken hold of and not yet stepped off).
        private static void PwRun(Map map)
        {
            Pawn p = pwP;
            bool onLadder = p.Spawned && LinkSteps.Climbing(p) && Links.Kind(p) != StepKind.None;
            if (onLadder)
            {
                pwFrames++;
                float pct = p.pather.MovePercentage;
                if (Links.LadderPose(p, out _, out _))
                {
                    pwPosed++;
                }
                if (p.Rotation != pwWant)
                {
                    pwGameWrong++;
                }
                float? yaw = IsoRimBridge.DrawnYaw(p, out int age);
                if (!yaw.HasValue || age > 1)
                {
                    pwNotDrawn++;
                }
                else
                {
                    float d = Mathf.Abs(Mathf.DeltaAngle(yaw.Value, pwWant.AsAngle));
                    if (!pwSettled && d <= 45f)
                    {
                        pwSettled = true;
                        pwTurnPct = pct;
                    }
                    if (pwSettled ? d > 45f : pct > 0.2f)
                    {
                        pwOff++;
                        pwFirstWrong = pwFirstWrong ?? $"at {pct:P0}: drawn yaw {yaw.Value:0} vs ladder {pwWant.AsAngle:0}, game facing {p.Rotation.ToStringHuman()}";
                    }
                    if (pct >= 0.5f)
                    {
                        pwMaxLate = Mathf.Max(pwMaxLate, d);
                    }
                    if (pwFrames % 20 == 1)
                    {
                        pwTrace += $" {pct:P0}:{yaw.Value:0}";
                    }
                }
                if (!pwShot && pct >= 0.5f)
                {
                    pwShot = true;
                    string shot = pwName.Replace("PW", "pw").Replace(' ', '_').ToLowerInvariant();
                    Info($"{shot}_50: {p} at {pct:P0}, game facing {p.Rotation.ToStringHuman()}, drawn yaw {(yaw.HasValue ? yaw.Value.ToString("0") : "?")} (ladder at {pwWant.AsAngle:0})");
                    Capture(shot + "_50");
                }
            }
            // (the raider's next job may start in the tick it arrives: arrived when it is there)
            bool arrived = p.Position == pwDest && (!p.pather.Moving || stage == "ld_pwr_run");
            if (!arrived && Find.TickManager.TicksGame - pwT0 < 3000)
            {
                return;
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            if (stage == "ld_pwr_run" && p.Spawned)
            {
                p.jobs.StopAll();
                p.pather.StopDead();
            }
            Result($"{pwName}: the drawn figure faces the ladder ({pwWant.ToStringHuman()}) while climbing, after one short turn",
                arrived && pwFrames > 30 && pwSettled && pwTurnPct <= 0.2f && pwOff == 0 && pwMaxLate <= 5f && pwNotDrawn == 0 && pwGameWrong == 0,
                $"arrived {arrived} (at {p.Position}, wanted {pwDest}, job {p.CurJobDef?.defName ?? "none"}), frames on the ladder {pwFrames} (posed {pwPosed}), turned to the ladder by {pwTurnPct:P0}, off >45deg after that {pwOff}, max off over the second half {pwMaxLate:0.0}, not drawn {pwNotDrawn}, game facing wrong {pwGameWrong}; first wrong {pwFirstWrong ?? "-"}; drawn yaw trace{pwTrace}");
            if (stage == "ld_pwr_run")
            {
                if (pwUp)
                {
                    pwUp = false;
                    Wait(10, "ld_pwr_go");
                    return;
                }
                if (!pwP.Destroyed)
                {
                    pwP.Destroy();
                }
                R2Unhide(map);
                Wait(10, "ld_ps");
                return;
            }
            if (pwUp)
            {
                pwUp = false;
                Wait(10, "ld_pw_go");
                return;
            }
            pwI++;
            Wait(10, pwI < pwTurns.Length ? "ld_pw_fixture" : "ld_pwr");
        }

        // PWR: a raider (a villager of a hostile faction, its own siege ladder of 2 floors facing east), the colonists away.
        private static void PwRaider(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Faction raiders = LdRaiders();
            LdFixture(map, 2, raiders, false, r2P);
            R2Hide(map);
            pwP = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Villager, raiders, forceGenerateNewPawn: true));
            GenSpawn.Spawn(pwP, LdG(15, 13), map);
            Result("PWR fixture: the raiders' siege ladder of 2 floors, a raider at its foot",
                ldLadder != null && ldLadder.def == PZDefOf.PZ_SiegeLadder && StairGrid.For(map)?.IndexOf(ldLadder) >= 0 && pwP.Spawned, LdLink(map));
            pwFoot = ldFoot;
            pwWant = Rot4.East;
            pwUp = true;
            stage = "ld_pwr_go";
        }

        private static void PwRaiderGo(Map map)
        {
            pwName = $"PWR raider {(pwUp ? "up" : "down")}";
            pwDest = pwUp ? LdL(24, 13, 2, map) : LdG(15, 13);
            PwStart(map, 2, pwFoot);
            pwP.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, pwDest), JobCondition.InterruptForced);
        }
    }
}
