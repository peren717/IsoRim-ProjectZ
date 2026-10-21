using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode ladders, last: the climbing pose (user 2026-10-21, Links.LadderPose). Mode ladderpose: only these, then the
    // screenshot strips. Same fixtures as TestLadders (LdFixture: a platform on level n, its west edge cell the top, the
    // column at (19, 13): every ladder there faces east) and TestElevator (ElFixture: the elevator at (19, 13) turned
    // west), real orders and ticks. The facing is the game's per-frame UpdateRotation, which the test's DoSingleTick does
    // not run: it is run once per tick here, as a frame would.
    //   PO  a colonist up and down a 2-floor emergency ladder: faces the ladder (east) on every tick it holds it, also
    //       going down (the game would face west); a sway across the facing, within SwayCells, changing side once per
    //       rung, in the drawn offset (ThingOffset); off the ladder its facing is free again (drafted and standing: south;
    //       walking off west: west)
    //   POQ two colonists, one ladder: the one waiting for its turn neither sways nor is posed
    //   POM a mechanoid on a lift: faces it, no sway
    //   POE an elevator: a powered ride is unchanged (no pose, no offset across, the game's own facing); without power
    //       (the emergency ladder) the climber faces the shaft's back wall (east), sways, faces the door side on the way out
    // Mode ladderpose, then: strips of a colonist up and down a 2-floor emergency ladder (5 frames each way, each from
    // the camera's three yaws 0, 90, 270; 3D on). Raiders and a mechanoid in real raids: raidladders -projectzposeshots.
    public static partial class TestRunner
    {
        private sealed class PoTrack
        {
            public Pawn p;
            public Func<LinkSteps.Step, Rot4> want;   // the facing wanted on a held ladder step (Invalid: not checked)
            public int hold, missed, wrongFace, crossSway, offMismatch, flips, waitTicks, waitPosed, leaked, elevTicks, elevPosed, elevVanillaWrong;
            public float maxSway, lastSide;
            public bool wasHolding;
            public string afterStep, wrong;
        }

        private static readonly List<PoTrack> poTracks = new List<PoTrack>();
        private static Pawn poMech;
        private static float poOldYaw;
        private static int poIdx, poView;
        private static readonly float[] poYaws = { 0f, 90f, 270f };
        private static readonly float[] poPcts = { 0.1f, 0.3f, 0.5f, 0.7f, 0.9f };

        private static bool StepLadderPoseInner(Map map)
        {
            switch (stage)
            {
                case "ld_po":
                    PoClimb(map);
                    return true;
                case "ld_po_up":
                    PoClimbUp(map);
                    return true;
                case "ld_po_down":
                    PoClimbDown(map);
                    return true;
                case "ld_po_q":
                    PoQueue(map);
                    return true;
                case "ld_po_q_check":
                    PoQueueCheck(map);
                    return true;
                case "ld_po_m":
                    PoMech(map);
                    return true;
                case "ld_po_m_check":
                    PoMechCheck(map);
                    return true;
                case "ld_po_e":
                    ElFixture(map, "ld_po_e_go", true, 2, r2P);
                    return true;
                case "ld_po_e_go":
                    PoElevator(map);
                    return true;
                case "ld_po_e_up":
                    PoElevatorUp(map);
                    return true;
                case "ld_po_e_cut":
                    PoElevatorCut(map);
                    return true;
                case "ld_po_e_down":
                    PoElevatorDown(map);
                    return true;
                case "ld_ps":
                    PsSetup(map);
                    return true;
                case "ld_ps_tick":
                    PsTick(map);
                    return true;
                case "ld_ps_down":
                    tickWatch = null;
                    GoTo(r2P, LdG(15, 13));
                    SeedTicks("ld_ps_down");
                    stage = "ld_ps_tick";
                    return true;
                case "ld_ps_view":
                    PsView(map);
                    return true;
                case "ld_ps_take":
                    PsTake(map);
                    return true;
                case "ld_ps_end":
                    FloorView.Set(0);
                    Finish();
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------------ tracking

        private static PoTrack PoWatch(Pawn p, Func<LinkSteps.Step, Rot4> want)
        {
            PoTrack t = new PoTrack { p = p, want = want };
            poTracks.Add(t);
            return t;
        }

        // Every tick: the frame's facing, then what the pose says against the step the pawn is on.
        private static void PoTick(Map map)
        {
            foreach (PoTrack t in poTracks)
            {
                Pawn p = t.p;
                if (p == null || !p.Spawned || p.Map != map)
                {
                    continue;
                }
                p.rotationTracker.UpdateRotation();
                LinkSteps.Step st = LinkSteps.For(map)?.Of(p);
                bool onStep = st != null && p.pather != null && p.pather.Moving && p.Position == st.from && p.pather.nextCell == st.to;
                bool posed = Links.LadderPose(p, out Rot4 face, out Vector3 sway);
                Vector3 off = PoOffset(p);
                bool holding = onStep && st.climbing && st.link.IsLadder;
                if (holding)
                {
                    t.hold++;
                    if (!posed)
                    {
                        t.missed++;
                    }
                    Rot4 w = t.want(st);
                    if (w.IsValid && p.Rotation != w)
                    {
                        t.wrongFace++;
                        t.wrong = t.wrong ?? $"{p.Rotation.ToStringHuman()} (wanted {w.ToStringHuman()}) at {p.pather.MovePercentage:P0}";
                    }
                    IntVec3 f = face.IsValid ? face.FacingCell : IntVec3.Zero;
                    if (Mathf.Abs(sway.x * f.x + sway.z * f.z) > 1e-5f)
                    {
                        t.crossSway++;
                    }
                    if (Mathf.Abs(off.x - sway.x) + Mathf.Abs(off.z - sway.z) > 1e-4f)
                    {
                        t.offMismatch++;
                    }
                    t.maxSway = Mathf.Max(t.maxSway, sway.magnitude);
                    IntVec3 side = face.IsValid ? face.Rotated(RotationDirection.Clockwise).FacingCell : IntVec3.Zero;
                    float s = sway.x * side.x + sway.z * side.z;
                    if (Mathf.Abs(s) > 0.002f)
                    {
                        if (t.lastSide != 0f && Mathf.Sign(s) != Mathf.Sign(t.lastSide))
                        {
                            t.flips++;
                        }
                        t.lastSide = s;
                    }
                }
                else if (onStep && st.link.IsLadder && !st.climbing)
                {
                    t.waitTicks++;
                    if (posed || Mathf.Abs(off.x) + Mathf.Abs(off.z) > 1e-5f)
                    {
                        t.waitPosed++;
                    }
                }
                else if (onStep && st.link.kind == LinkKind.Elevator)
                {
                    t.elevTicks++;
                    if (posed || Mathf.Abs(off.x) + Mathf.Abs(off.z) > 1e-5f)
                    {
                        t.elevPosed++;
                    }
                    Rot4 v = PoVanillaFacing(p);
                    if (v.IsValid && p.Rotation != v)
                    {
                        t.elevVanillaWrong++;
                    }
                }
                else if (posed)
                {
                    t.leaked++;
                }
                if (t.wasHolding && !holding && t.afterStep == null && p.pather != null && p.pather.Moving && Links.Kind(p) == StepKind.None)
                {
                    Rot4 v = PoVanillaFacing(p);
                    t.afterStep = $"{p.Rotation.ToStringHuman()} (the game's {(v.IsValid ? v.ToStringHuman() : "-")})";
                    if (v.IsValid && p.Rotation != v)
                    {
                        t.afterStep += " WRONG";
                    }
                }
                t.wasHolding = holding;
            }
        }

        // The game's own facing of a moving pawn (Pawn_RotationTracker.UpdateRotation: the next cell; Invalid: it keeps its
        // facing).
        private static Rot4 PoVanillaFacing(Pawn p)
        {
            if (p.pather?.curPath == null || p.pather.curPath.NodesLeftCount < 1 || p.pather.nextCell == p.Position)
            {
                return Rot4.Invalid;
            }
            IntVec3 d = p.pather.nextCell - p.Position;
            return d.x > 0 ? Rot4.East : d.x < 0 ? Rot4.West : d.z > 0 ? Rot4.North : Rot4.South;
        }

        // The offset IsoRim draws the pawn with (FloorLayout.ThingOffset; IsoRim present).
        private static Vector3 PoOffset(Pawn p) => IsoRimBridge.Present ? PoOffsetInner(p) : Vector3.zero;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static Vector3 PoOffsetInner(Pawn p) => new FloorLayout().ThingOffset(p);

        private static string PoStr(PoTrack t) => $"{t.p.LabelShort}: held {t.hold} ticks (not posed {t.missed}), wrong facing {t.wrongFace} {t.wrong}, max sway {t.maxSway:0.0000} (limit {Links.SwayCells}), side changes {t.flips}, sway along the facing {t.crossSway}, drawn offset differs {t.offMismatch}, waiting {t.waitTicks} (posed {t.waitPosed}), posed off a ladder {t.leaked}, powered ride {t.elevTicks} (posed {t.elevPosed}, not the game's facing {t.elevVanillaWrong}), after stepping off {t.afterStep ?? "-"}";

        private static bool PoSwayOk(PoTrack t, int minFlips) => t.maxSway >= Links.SwayCells * 0.8f && t.maxSway <= Links.SwayCells + 1e-4f && t.crossSway == 0 && t.offMismatch == 0 && t.flips >= minFlips;

        // ------------------------------------------------------------------------------------------------ PO: up and down

        private static void PoClimb(Map map)
        {
            poTracks.Clear();
            LdFixture(map, 2, null, false, r2P);
            ldLadder = Place(PZDefOf.PZ_EmergencyLadder, null, ldTop, map, Rot4.West) as Building_SiegeLadder;
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Teleport(r2P, LdG(16, 13));
            Draft(r2P, true);
            Result("PO fixture: the player's emergency ladder of 2 floors down the column", ldLadder != null && ldLadder.foot == ldFoot && StairGrid.For(map)?.IndexOf(ldLadder) >= 0, LdLink(map));
            PoWatch(r2P, st => Rot4.East);
            ldDest = LdL(24, 13, 2, map);
            GoTo(r2P, ldDest);
            SeedTicks("ld_po_up");
            TickUntil(3000, () =>
            {
                PoTick(map);
                return r2P.Position == ldDest;
            }, "ld_po_up", 1);
        }

        private static void PoClimbUp(Map map)
        {
            tickWatch = null;
            PoTrack t = poTracks[0];
            Result("PO up: faces the ladder (east) on every tick it holds it, sways across within bounds, a side per rung, in the drawn offset",
                r2P.Position == ldDest && t.hold >= 355 && t.missed == 0 && t.wrongFace == 0 && t.leaked == 0 && PoSwayOk(t, 6), PoStr(t));
            r2P.rotationTracker.UpdateRotation();
            // (standing drafted, IsoRim keeps a drafted pawn's facing: east here, as after any walk east)
            Result("PO up: stepping off onto the top it faces its way (the game's facing); standing there, no pose", t.afterStep != null && !t.afterStep.Contains("WRONG") && !r2P.pather.Moving && !Links.LadderPose(r2P, out _, out _),
                $"after stepping off {t.afterStep ?? "never seen"}; standing facing {r2P.Rotation.ToStringHuman()}, moving {r2P.pather.Moving}");
            poTracks.Clear();
            PoWatch(r2P, st => Rot4.East);
            ldDest = LdG(15, 13);
            GoTo(r2P, ldDest);
            SeedTicks("ld_po_down");
            TickUntil(3000, () =>
            {
                PoTick(map);
                return r2P.Position == ldDest;
            }, "ld_po_down", 1);
        }

        private static void PoClimbDown(Map map)
        {
            tickWatch = null;
            PoTrack t = poTracks[0];
            Result("PO down: faces the ladder (east, back to the open air; the game would face west) on every tick it holds it, sways as going up",
                r2P.Position == ldDest && t.hold >= 355 && t.missed == 0 && t.wrongFace == 0 && t.leaked == 0 && PoSwayOk(t, 6), PoStr(t));
            Result("PO down: walking off the foot it faces its way again (west, the game's facing)", t.afterStep != null && t.afterStep.StartsWith("West") && !t.afterStep.Contains("WRONG"), $"after stepping off: {t.afterStep ?? "never seen"}");
            stage = "ld_po_q";
        }

        // ------------------------------------------------------------------------------------------------ POQ: waiting

        private static void PoQueue(Map map)
        {
            poTracks.Clear();
            LdFixture(map, 2, null, false, r2P, r2Q);
            ldLadder = Place(PZDefOf.PZ_EmergencyLadder, null, ldTop, map, Rot4.West) as Building_SiegeLadder;
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Teleport(r2P, LdG(16, 12));
            Teleport(r2Q, LdG(16, 14));
            Draft(r2P, true);
            Draft(r2Q, true);
            PoWatch(r2P, st => Rot4.East);
            PoWatch(r2Q, st => Rot4.East);
            GoTo(r2P, LdL(24, 12, 2, map));
            GoTo(r2Q, LdL(24, 14, 2, map));
            SeedTicks("ld_po_q");
            TickUntil(4000, () =>
            {
                PoTick(map);
                return Levels.LevelOf(r2P.Position, map) == 2 && Levels.LevelOf(r2Q.Position, map) == 2 && !r2P.pather.Moving && !r2Q.pather.Moving;
            }, "ld_po_q_check", 1);
        }

        private static void PoQueueCheck(Map map)
        {
            tickWatch = null;
            int wait = poTracks.Sum(x => x.waitTicks), posed = poTracks.Sum(x => x.waitPosed);
            Result("POQ two colonists, one ladder: the one waiting its turn is not posed and does not sway; both face the ladder while climbing",
                Levels.LevelOf(r2P.Position, map) == 2 && Levels.LevelOf(r2Q.Position, map) == 2 && wait >= 100 && posed == 0 && poTracks.All(x => x.hold >= 355 && x.wrongFace == 0 && x.missed == 0 && x.leaked == 0),
                string.Join(" | ", poTracks.Select(PoStr)));
            stage = "ld_po_m";
        }

        // ------------------------------------------------------------------------------------------------ POM: lift

        private static void PoMech(Map map)
        {
            poTracks.Clear();
            LdFixture(map, 2, Faction.OfMechanoids, true, r2P);
            R2Hide(map);
            poMech = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Mech_Scyther, Faction.OfMechanoids, forceGenerateNewPawn: true));
            GenSpawn.Spawn(poMech, LdG(16, 13), map);
            Result("POM fixture: a mechanoids' lift of 2 floors", ldLadder != null && ldLadder.def == PZDefOf.PZ_SiegeLift && StairGrid.For(map)?.IndexOf(ldLadder) >= 0, LdLink(map));
            PoWatch(poMech, st => Rot4.East);
            ldDest = LdL(24, 13, 2, map);
            poMech.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, ldDest), JobCondition.InterruptForced);
            SeedTicks("ld_po_m");
            TickUntil(3000, () =>
            {
                PoTick(map);
                return poMech.Position == ldDest || !poMech.Spawned;
            }, "ld_po_m_check", 1);
        }

        private static void PoMechCheck(Map map)
        {
            tickWatch = null;
            PoTrack t = poTracks[0];
            Result("POM a mechanoid on a lift faces it (east) on every tick it holds it, no sway",
                poMech.Position == ldDest && t.hold >= 355 && t.missed == 0 && t.wrongFace == 0 && t.maxSway == 0f && t.offMismatch == 0 && t.leaked == 0, PoStr(t));
            if (!poMech.Destroyed)
            {
                poMech.Destroy();
            }
            R2Unhide(map);
            stage = "ld_po_e";
        }

        // ------------------------------------------------------------------------------------------------ POE: elevator

        // Without power: the shaft's back wall (the door side's opposite) up and down the shaft, the door side on the way out.
        private static Rot4 PoElevatorWant(LinkSteps.Step st)
        {
            Rot4 door = st.link.thing.Rotation;
            return st.pawn.pather.MovePercentage > 1f - Links.ElevatorDoor(st.link) ? door : door.Opposite;
        }

        private static void PoElevator(Map map)
        {
            tickWatch = null;
            poTracks.Clear();
            Teleport(r2P, ElG(15, 13));
            Draft(r2P, true);
            PoWatch(r2P, PoElevatorWant);
            elDest = ElL(14, 13, 2, map);
            GoTo(r2P, elDest);
            SeedTicks("ld_po_e");
            TickUntil(3000, () =>
            {
                PoTick(map);
                return r2P.Position == elDest;
            }, "ld_po_e_up", 1);
        }

        private static void PoElevatorUp(Map map)
        {
            tickWatch = null;
            PoTrack t = poTracks[0];
            Result("POE a powered elevator ride is unchanged: no pose, no offset across, the game's own facing",
                r2P.Position == elDest && t.elevTicks >= ElWant(2) - 3 && t.elevPosed == 0 && t.elevVanillaWrong == 0 && t.hold == 0 && t.leaked == 0, PoStr(t));
            elBattery.Destroy(DestroyMode.Vanish);
            SeedTicks("ld_po_e_cut");
            TickUntil(400, () => !elE.LinkedPowered, "ld_po_e_cut", 1);
        }

        private static void PoElevatorCut(Map map)
        {
            tickWatch = null;
            poTracks.Clear();
            PoWatch(r2P, PoElevatorWant);
            elDest = ElG(15, 13);
            GoTo(r2P, elDest);
            SeedTicks("ld_po_e_down");
            TickUntil(3000, () =>
            {
                PoTick(map);
                return r2P.Position == elDest;
            }, "ld_po_e_down", 1);
        }

        private static void PoElevatorDown(Map map)
        {
            tickWatch = null;
            PoTrack t = poTracks[0];
            Result("POE without power (the emergency ladder) the climber faces the shaft's back wall (east), sways, faces the door side (west) on the way out",
                r2P.Position == elDest && !elE.LinkedPowered && t.hold >= VLink.LadderTicksPerFloor * 2 - 5 && t.missed == 0 && t.wrongFace == 0 && t.leaked == 0 && PoSwayOk(t, 3), PoStr(t));
            if (mode == "ladderpose")
            {
                stage = "ld_ps";
                return;
            }
            Finish();
        }

        // ------------------------------------------------------------------------------------------------ PS: strips

        private static void PsSetup(Map map)
        {
            poTracks.Clear();
            LdFixture(map, 2, null, false, r2P);
            ldLadder = Place(PZDefOf.PZ_EmergencyLadder, null, ldTop, map, Rot4.West) as Building_SiegeLadder;
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Teleport(r2P, LdG(16, 13));
            Draft(r2P, true);
            poOldYaw = (float)IsoRimBridge.SetOption("freeYaw", 0f);
            poIdx = 0;
            poView = 0;
            ldDest = LdL(24, 13, 2, map);
            GoTo(r2P, ldDest);
            SeedTicks("ld_ps_up");
            stage = "ld_ps_tick";
        }

        private static bool PsUp(int i) => i < poPcts.Length;

        private static void PsTick(Map map)
        {
            bool up = PsUp(poIdx);
            float pct = poPcts[poIdx % poPcts.Length];
            StepKind k = up ? StepKind.LadderUp : StepKind.LadderDown;
            TickUntil(2000, () => Links.Kind(r2P) == k && LinkSteps.Climbing(r2P) && r2P.pather.MovePercentage >= pct, "ld_ps_view", 1);
        }

        private static void PsView(Map map)
        {
            tickWatch = null;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            r2P.rotationTracker.UpdateRotation();
            IsoRimBridge.SetOption("freeYaw", poYaws[poView]);
            ShotAt(map, 2, ldFoot + IntVec3.East, 5f);
            Wait(120, "ld_ps_take");   // (the camera turns over some frames)
        }

        private static void PsTake(Map map)
        {
            bool up = PsUp(poIdx);
            float pct = poPcts[poIdx % poPcts.Length];
            string name = $"pose_{(up ? "up" : "down")}_{Mathf.RoundToInt(pct * 100):00}_yaw{Mathf.RoundToInt(poYaws[poView])}";
            Links.LadderPose(r2P, out Rot4 face, out Vector3 sway);
            Info($"{name}: {r2P} at {r2P.pather.MovePercentage:P0}, facing {r2P.Rotation.ToStringHuman()} (pose {(face.IsValid ? face.ToStringHuman() : "-")}), sway ({sway.x:0.000}, {sway.z:0.000}), rise {Links.Rise(r2P):0.00}");
            Capture(name);
            poView++;
            if (poView < poYaws.Length)
            {
                Wait(3, "ld_ps_view");
                return;
            }
            poView = 0;
            poIdx++;
            IsoRimBridge.SetOption("freeYaw", 0f);
            if (poIdx >= poPcts.Length * 2)
            {
                IsoRimBridge.SetOption("freeYaw", poOldYaw);
                Wait(3, "ld_ps_end");   // (after the last screenshot is written)
                return;
            }
            if (poIdx == poPcts.Length)
            {
                SeedTicks("ld_ps_top");
                TickUntil(2000, () => r2P.Position == ldDest, "ld_ps_down", 1);
                return;
            }
            SeedTicks("ld_ps_" + poIdx);
            Wait(3, "ld_ps_tick");
        }
    }
}
