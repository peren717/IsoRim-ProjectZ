using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode animalladders (user rule 2026-10-23: animals take stairs only, never a ladder of any kind; one stranded upstairs
    // stays there). Run with -projectzseed=7 (3 floors are enough). Every check builds its own fixture (LdFixture: a
    // platform on level 1, ground x 20..29, z 8..19, a ladder from the foot (19, 13) to the top (20, 13); with stairs: a
    // ramp at (17..19, 10) going east, exit (20, 10)) under its own random state, and runs real orders, jobs and ticks:
    //   A1  a dog following its drafted master: the master climbs the ladder, the dog stays below (never on the ladder)
    //   A2  the same with stairs too: the dog follows its master up the stairs (never on the ladder)
    //   A3  an animal sent to a cell on the platform (as for a pen up there): no way up with the ladder only (no reach, the
    //       Goto fails and it stays below); with stairs: reached, over the stairs
    //   A4  a manhunter pack below a ladder-only platform with a colonist on it: none comes up, the colonist unhurt
    //   A5  an animal stranded on a ladder-only platform: it stays up there (wanders, never takes the ladder)
    //   A6  a colonist carries a downed animal up the ladder: allowed (the carrier climbs)
    //   A7  a mechanoid still climbs: it reaches the platform over the ladder
    public static partial class TestRunner
    {
        private static Pawn alA;
        private static List<Pawn> alPack = new List<Pawn>();
        private static bool alOnLadder, alOnStairs;
        private static int alHurtAt;

        private static bool StepAnimalLadders(Map map)
        {
            Rand.PushState((int)(StableHash("animalladders_" + stage) ^ (uint)(SeedArg ?? 717)));
            try
            {
                return StepAnimalLaddersInner(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static bool StepAnimalLaddersInner(Map map)
        {
            switch (stage)
            {
                case "al_setup":
                    AlSetup(map);
                    return true;
                case "al_a1":
                    AlFollow(map, false);
                    return true;
                case "al_a1_check":
                    AlFollowCheck(map, false);
                    return true;
                case "al_a2":
                    AlFollow(map, true);
                    return true;
                case "al_a2_check":
                    AlFollowCheck(map, true);
                    return true;
                case "al_a3":
                    AlSend(map, false);
                    return true;
                case "al_a3_check":
                    AlSendCheck(map, false);
                    return true;
                case "al_a3b":
                    AlSend(map, true);
                    return true;
                case "al_a3b_check":
                    AlSendCheck(map, true);
                    return true;
                case "al_a4":
                    AlManhunters(map);
                    return true;
                case "al_a4_check":
                    AlManhuntersCheck(map);
                    return true;
                case "al_a5":
                    AlStranded(map);
                    return true;
                case "al_a5_check":
                    AlStrandedCheck(map);
                    return true;
                case "al_a6":
                    AlCarry(map);
                    return true;
                case "al_a6_go":
                    AlCarryGo(map);
                    return true;
                case "al_a6_check":
                    AlCarryCheck(map);
                    return true;
                case "al_a7":
                    AlMech(map);
                    return true;
                case "al_a7_check":
                    AlMechCheck(map);
                    return true;
            }
            return false;
        }

        private static void AlSetup(Map map)
        {
            DebugSettings.enableRandomMentalStates = false;
            NoRoofCollapse(map);
            Find.Storyteller.storytellerComps.Clear();
            Find.Storyteller.incidentQueue.Clear();
            Find.PlaySettings.autoRebuild = false;
            NoAmbushes(map);
            foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
            {
                a.Destroy();
            }
            r2Area = new CellRect(map.Center.x - 22, map.Center.z - 16, 44, 32);
            List<Pawn> cols = Colonists(map);
            r2P = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Handling)) ?? cols.First();
            foreach (Pawn p in cols)
            {
                p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
            }
            R2Park(map);
            Info($"animalladders: area {r2Area}, colonist {r2P}, levels {Levels.Min(map)}..{Levels.Max(map)}");
            stage = "al_a1";
        }

        // The ladder fixture on level 1, with the stairs too when asked; animals left from the last check removed.
        private static void AlFixture(Map map, bool stairs, params Pawn[] keep)
        {
            foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal || p.RaceProps.IsMechanoid).ToList())
            {
                a.Destroy();
            }
            alPack.Clear();
            alA = null;
            LdFixture(map, 1, Faction.OfPlayer, false, keep);
            if (stairs)
            {
                Building_Stairs s = R2Stairs(map, LdG(18, 10), Rot4.East);
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                Info($"stairs {s?.Position} exit {s?.TopCell}");
            }
            alOnLadder = alOnStairs = false;
        }

        private static Pawn AlAnimal(Map map, string kind, IntVec3 at, Faction f)
        {
            PawnKindDef k = DefDatabase<PawnKindDef>.GetNamed(kind);
            Pawn a = PawnGenerator.GeneratePawn(new PawnGenerationRequest(k, f, fixedBiologicalAge: 3f));
            GenSpawn.Spawn(a, at, map);
            return a;
        }

        // Every tick watched: a pawn on a ladder or stairs step.
        private static void AlWatch(Pawn p)
        {
            if (p == null || !p.Spawned)
            {
                return;
            }
            StepKind k = Links.Kind(p);
            alOnLadder |= k == StepKind.LadderUp || k == StepKind.LadderDown;
            alOnStairs |= k == StepKind.StairUp || k == StepKind.StairDown;
        }

        private static string AlWhere(Pawn p, Map map) => p == null ? "-" : p.Spawned ? $"{p.Position} level {Levels.LevelOf(p.Position, map)}" : "not spawned";

        // ------------------------------------------------------------------------------------------------ A1 / A2

        private static void AlFollow(Map map, bool stairs)
        {
            AlFixture(map, stairs, r2P);
            Heal(r2P);
            Teleport(r2P, LdG(15, 13));
            alA = AlAnimal(map, "Husky", LdG(15, 11), Faction.OfPlayer);
            alA.training.Train(TrainableDefOf.Obedience, r2P, true);
            alA.playerSettings.Master = r2P;
            alA.playerSettings.followDrafted = true;
            GoTo(r2P, LdL(24, 13, 1, map));
            SeedTicks("al_follow" + stairs);
            TickUntil(stairs ? 4000 : 2500, () =>
            {
                AlWatch(alA);
                return stairs && Levels.LevelOf(alA.Position, map) == 1 && alA.Position.DistanceTo(r2P.Position) < 4f;
            }, stairs ? "al_a2_check" : "al_a1_check", 1);
        }

        private static void AlFollowCheck(Map map, bool stairs)
        {
            tickWatch = null;
            bool masterUp = Levels.LevelOf(r2P.Position, map) == 1;
            int dogLevel = alA.Spawned ? Levels.LevelOf(alA.Position, map) : -1;
            if (!stairs)
            {
                Result("A1 a dog following its master, who climbs the ladder: the master is up, the dog stays below and never takes the ladder",
                    masterUp && dogLevel == 0 && !alOnLadder, $"master {AlWhere(r2P, map)}, dog {AlWhere(alA, map)}, dog on a ladder {alOnLadder}");
                stage = "al_a2";
            }
            else
            {
                Result("A2 with stairs too: the dog follows its master up the stairs, never the ladder",
                    masterUp && dogLevel == 1 && alOnStairs && !alOnLadder, $"master {AlWhere(r2P, map)}, dog {AlWhere(alA, map)}, on stairs {alOnStairs}, on a ladder {alOnLadder}");
                stage = "al_a3";
            }
            R2Park(map);
        }

        // ------------------------------------------------------------------------------------------------ A3

        private static void AlSend(Map map, bool stairs)
        {
            AlFixture(map, stairs);
            alA = AlAnimal(map, "Cow", LdG(15, 12), Faction.OfPlayer);
            IntVec3 up = LdL(25, 14, 1, map);
            bool reach = map.reachability.CanReach(alA.Position, up, PathEndMode.OnCell, TraverseParms.For(alA));
            Info($"A3 {(stairs ? "with stairs" : "ladder only")}: the animal can reach {up}: {reach}");
            alHurtAt = reach ? 1 : 0;
            alA.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, up), JobCondition.InterruptForced);
            SeedTicks("al_send" + stairs);
            TickUntil(stairs ? 3000 : 1500, () =>
            {
                AlWatch(alA);
                return stairs && alA.Position == up;
            }, stairs ? "al_a3b_check" : "al_a3_check", 1);
        }

        private static void AlSendCheck(Map map, bool stairs)
        {
            tickWatch = null;
            int lv = alA.Spawned ? Levels.LevelOf(alA.Position, map) : -1;
            if (!stairs)
            {
                Result("A3 an animal sent up to the platform (a pen up there) with the ladder only: no reach, it stays below, never on the ladder",
                    alHurtAt == 0 && lv == 0 && !alOnLadder, $"reach {alHurtAt == 1}, animal {AlWhere(alA, map)}, on a ladder {alOnLadder}");
                stage = "al_a3b";
            }
            else
            {
                Result("A3 with stairs: reached, over the stairs (never the ladder)",
                    alHurtAt == 1 && lv == 1 && alOnStairs && !alOnLadder, $"reach {alHurtAt == 1}, animal {AlWhere(alA, map)}, on stairs {alOnStairs}, on a ladder {alOnLadder}");
                stage = "al_a4";
            }
        }

        // ------------------------------------------------------------------------------------------------ A4

        private static void AlManhunters(Map map)
        {
            AlFixture(map, false, r2P);
            Heal(r2P);
            Teleport(r2P, LdL(24, 13, 1, map));
            Draft(r2P, true);
            alHurtAt = r2P.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);
            for (int i = 0; i < 3; i++)
            {
                Pawn w = AlAnimal(map, "Wolf_Timber", LdG(14 + i, 15), null);
                w.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.ManhunterPermanent, null, true);
                alPack.Add(w);
            }
            SeedTicks("al_manhunt");
            TickUntil(2500, () =>
            {
                foreach (Pawn w in alPack)
                {
                    AlWatch(w);
                }
                return false;
            }, "al_a4_check", 1);
        }

        private static void AlManhuntersCheck(Map map)
        {
            tickWatch = null;
            int up = alPack.Count(w => w.Spawned && Levels.LevelOf(w.Position, map) >= 1);
            int hurt = r2P.health.hediffSet.hediffs.Count(h => h is Hediff_Injury) - alHurtAt;
            Result("A4 a manhunter pack below a ladder-only platform: none comes up, never on the ladder, the colonist up there unhurt",
                alPack.Count == 3 && up == 0 && !alOnLadder && hurt == 0, $"pack {string.Join(", ", alPack.Select(w => w.LabelShort + " " + AlWhere(w, map) + " " + (w.MentalStateDef?.defName ?? "-")))}; colonist {AlWhere(r2P, map)}, new injuries {hurt}");
            R2Park(map);
            stage = "al_a5";
        }

        // ------------------------------------------------------------------------------------------------ A5

        private static void AlStranded(Map map)
        {
            AlFixture(map, false);
            alA = AlAnimal(map, "Husky", LdL(22, 13, 1, map), Faction.OfPlayer);
            SeedTicks("al_stranded");
            TickUntil(3000, () =>
            {
                AlWatch(alA);
                return false;
            }, "al_a5_check", 1);
        }

        private static void AlStrandedCheck(Map map)
        {
            tickWatch = null;
            Result("A5 an animal stranded on a ladder-only platform: it stays up there, never on the ladder",
                alA.Spawned && Levels.LevelOf(alA.Position, map) == 1 && !alOnLadder, $"animal {AlWhere(alA, map)}, on a ladder {alOnLadder}");
            stage = "al_a6";
        }

        // ------------------------------------------------------------------------------------------------ A6

        private static void AlCarry(Map map)
        {
            AlFixture(map, false, r2P);
            Heal(r2P);
            Teleport(r2P, LdG(15, 13));
            Draft(r2P, true);
            alA = AlAnimal(map, "Husky", LdG(16, 15), Faction.OfPlayer);
            HealthUtility.TryAnesthetize(alA);
            Job job = JobMaker.MakeJob(JobDefOf.CarryDownedPawnDrafted, alA);
            job.count = 1;
            r2P.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder);
            SeedTicks("al_carry");
            TickUntil(1500, () => r2P.carryTracker.CarriedThing == alA, "al_a6_go", 5);
        }

        private static void AlCarryGo(Map map)
        {
            tickWatch = null;
            Info($"A6 the downed animal picked up: {r2P.carryTracker.CarriedThing == alA} (downed {alA.Downed})");
            GoTo(r2P, LdL(24, 13, 1, map));
            alOnLadder = false;
            SeedTicks("al_carry_go");
            TickUntil(2000, () =>
            {
                AlWatch(r2P);
                return Levels.LevelOf(r2P.Position, map) == 1 && Links.Kind(r2P) == StepKind.None;
            }, "al_a6_check", 1);
        }

        private static void AlCarryCheck(Map map)
        {
            tickWatch = null;
            Result("A6 a colonist carries a downed animal up the ladder: allowed, both on the platform",
                Levels.LevelOf(r2P.Position, map) == 1 && r2P.carryTracker.CarriedThing == alA && alOnLadder, $"carrier {AlWhere(r2P, map)}, carrying {r2P.carryTracker.CarriedThing}, climbed {alOnLadder}");
            r2P.carryTracker.TryDropCarriedThing(r2P.Position, ThingPlaceMode.Near, out Thing _);
            R2Park(map);
            stage = "al_a7";
        }

        // ------------------------------------------------------------------------------------------------ A7

        private static void AlMech(Map map)
        {
            AlFixture(map, false);
            alA = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Mech_Scyther"), Faction.OfMechanoids));
            GenSpawn.Spawn(alA, LdG(15, 13), map);
            IntVec3 up = LdL(24, 13, 1, map);
            alHurtAt = map.reachability.CanReach(alA.Position, up, PathEndMode.OnCell, TraverseParms.For(alA)) ? 1 : 0;
            Job job = JobMaker.MakeJob(JobDefOf.Goto, up);
            job.expiryInterval = -1;
            alA.jobs.StartJob(job, JobCondition.InterruptForced);
            SeedTicks("al_mech");
            TickUntil(2000, () =>
            {
                AlWatch(alA);
                return alA.Spawned && Levels.LevelOf(alA.Position, map) == 1;
            }, "al_a7_check", 1);
        }

        private static void AlMechCheck(Map map)
        {
            tickWatch = null;
            Result("A7 a mechanoid still climbs: it can reach the platform and goes up the ladder",
                alHurtAt == 1 && alA.Spawned && Levels.LevelOf(alA.Position, map) == 1 && alOnLadder, $"reach {alHurtAt == 1}, mech {AlWhere(alA, map)}, on a ladder {alOnLadder}");
            if (alA.Spawned)
            {
                alA.Destroy();
            }
            Finish();
        }
    }
}
