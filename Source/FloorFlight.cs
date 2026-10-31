using System;
using System.Collections.Generic;
using HarmonyLib;
using ProjectZ.Combat;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // Opt-in and opt-out lists for flying between floors (FloorFlight). Other mods add their own FloorFlyersDef (any
    // defName) or patch ours (Defs/Misc/PZ_FloorFlyers.xml): <fly> races that fly between floors though the game's
    // flight doesn't say so (a mod that flies its own way), <noFly> races that never do.
    public class FloorFlyersDef : Def
    {
        public List<ThingDef> fly = new List<ThingDef>();
        public List<ThingDef> noFly = new List<ThingDef>();
    }

    // Flying animals fly between floors as the locust jumps (user 2026-10-26): to a fight on another floor
    // (JobGiver_AIJumpAcross, JumpAcross), away from danger (Flee_FlyAcross_Patch) and, stranded, to a floor where it can
    // do what it needs (FloorUnstrand). Who flies: an animal the game lets
    // fly (Odyssey's flight: Pawn_FlightTracker.CanEverFly, i.e. the MaxFlightTime stat over 0, not in vacuum for a
    // race that can't fly there, not a mutant whose flight is off), plus FloorFlyersDef.fly, less FloorFlyersDef.noFly.
    // A pawn with its own jump ability marked JumpAcrossFloors (the locust) uses that instead.
    // The flight is the game's jump (JumpUtility.DoJump, a PawnFlyer), so every rule for jumps between floors holds
    // (Combat/JumpFlights.cs: 3D range, open sky over the landing, a clear arc, no void rows, no fog). Never for ordinary
    // moves: paths still join floors by stairs only (an animal never climbs a ladder).
    public static class FloorFlight
    {
        // test switch -projectznoflight: nobody flies between floors
        public static bool Off;

        public const float Range = 19.9f;      // as the locust's jump
        public const int CooldownTicks = 300;  // as the locust's jump
        public const int WarmupTicks = 30;
        public const int FleeTries = 12;       // landings checked fully per flee
        public const float FleeGain = 8f;      // a flight from danger gains this much distance at least (or the walk's)

        // counters (tests)
        public static int Flights, FleeFlights, FleeNone;

        private static HashSet<ThingDef> yes, no;
        private static readonly Dictionary<int, int> cooldown = new Dictionary<int, int>();
        private static VerbProperties verbProps;

        public static void ResetLists()
        {
            yes = null;
            no = null;
        }

        private static void Load()
        {
            if (yes != null)
            {
                return;
            }
            yes = new HashSet<ThingDef>();
            no = new HashSet<ThingDef>();
            foreach (FloorFlyersDef d in DefDatabase<FloorFlyersDef>.AllDefs)
            {
                foreach (ThingDef t in d.fly)
                {
                    if (t != null)
                    {
                        yes.Add(t);
                    }
                }
                foreach (ThingDef t in d.noFly)
                {
                    if (t != null)
                    {
                        no.Add(t);
                    }
                }
            }
        }

        // The pawn flies between floors (the lists, else an animal the game lets fly).
        public static bool Flies(Pawn p)
        {
            if (Off || p?.RaceProps == null)
            {
                return false;
            }
            Load();
            if (no.Contains(p.def))
            {
                return false;
            }
            if (yes.Contains(p.def))
            {
                return true;
            }
            return p.RaceProps.Animal && p.flight != null && p.flight.CanEverFly;
        }

        // Not cooling down from its last flight between floors.
        public static bool Ready(Pawn p)
        {
            int now = Find.TickManager.TicksGame;
            // (a cooldown from another game: further ahead than it can be, ignored)
            return !cooldown.TryGetValue(p.thingIDNumber, out int until) || now >= until || until - now > CooldownTicks;
        }

        public static int CooldownLeft(Pawn p)
        {
            int now = Find.TickManager.TicksGame;
            return cooldown.TryGetValue(p.thingIDNumber, out int until) && until > now && until - now <= CooldownTicks ? until - now : 0;
        }

        // The pawn may land on c from where it stands (the jump's own checks: range and the rules between floors, a cell
        // it can stand on, no fog, no closed door).
        public static bool CanLand(Pawn p, IntVec3 c)
        {
            Map map = p.Map;
            return map != null && JumpUtility.ValidJumpTarget(p, map, c) && JumpUtility.CanHitTargetFrom(p, p.Position, c, Range);
        }

        public static Job MakeJob(IntVec3 c) => JobMaker.MakeJob(PZFlightDefOf.PZ_FlyAcross, c);

        // Take off for c now (checked again); false when it can't.
        public static bool Fly(Pawn p, IntVec3 c)
        {
            if (!p.Spawned || p.Downed || !CanLand(p, c))
            {
                return false;
            }
            if (verbProps == null)
            {
                verbProps = DefDatabase<AbilityDef>.GetNamedSilentFail("PZ_LocustJump")?.verbProperties ?? new VerbProperties();
            }
            if (cooldown.Count > 512)
            {
                cooldown.Clear();
            }
            cooldown[p.thingIDNumber] = Find.TickManager.TicksGame + CooldownTicks;
            if (!JumpUtility.DoJump(p, c, null, verbProps))
            {
                return false;
            }
            Flights++;
            ProjectZ.DebugLog($"{p} flies to {c} (level {Levels.LevelOf(c, p.Map)})");
            return true;
        }

        // A landing on another floor away from danger, for a flee that would walk to dest: the farthest from the danger
        // (3D) that gains FleeGain over where it stands (or is as far as the walk). No walk back asked for (user 2026-10-26:
        // a bird doesn't know in advance); a flyer stranded there flies on later (FloorUnstrand). Invalid when none.
        public static IntVec3 FleeLanding(Pawn p, Thing danger, IntVec3 dest)
        {
            Map map = p.Map;
            IntVec3 root = p.Position;
            IntVec3 g = ProjectZ.GroundCellOf(root, map);
            int k = Levels.LevelOf(root, map);
            if (!g.IsValid || danger == null || !danger.Spawned || danger.Map != map)
            {
                return IntVec3.Invalid;
            }
            IntVec3 dc = danger.Position;
            float need = Math.Min(Range3D.Distance(dest, dc, map), Range3D.Distance(root, dc, map) + FleeGain);
            List<KeyValuePair<float, IntVec3>> found = new List<KeyValuePair<float, IntVec3>>();
            int n = GenRadial.NumCellsInRadius(Range);
            foreach (int L in Levels.All(map))
            {
                if (L == k)
                {
                    continue;
                }
                for (int i = 0; i < n; i++)
                {
                    IntVec3 c = ProjectZ.CellOnFloor(g + GenRadial.RadialPattern[i], L, map);
                    if (!c.IsValid || !c.InBounds(map) || Levels.LevelOf(c, map) != L || !c.Standable(map) || !Column.OpenAbove(c, map))
                    {
                        continue;
                    }
                    if (Range3D.Distance(root, c, map) > Range)
                    {
                        continue;
                    }
                    float d = Range3D.Distance(c, dc, map);
                    if (d >= need)
                    {
                        found.Add(new KeyValuePair<float, IntVec3>(d, c));
                    }
                }
            }
            found.Sort((a, b) => b.Key.CompareTo(a.Key));
            for (int i = 0, tried = 0; i < found.Count && tried < FleeTries; i++)
            {
                IntVec3 c = found[i].Value;
                if (c.GetFirstPawn(map) != null || !map.pawnDestinationReservationManager.CanReserve(c, p))
                {
                    continue;
                }
                tried++;
                if (CanLand(p, c))
                {
                    return c;
                }
            }
            return IntVec3.Invalid;
        }

        // Every flee (JobDriver_Flee: the animal flee, a hunted animal's flight, the flee givers): a flyer that can fly
        // between floors here, ready, flies off to a landing from FleeLanding instead of running. Not for a flee that
        // leaves the map.
        public static bool TryFlee(Pawn p, Job job)
        {
            if (Off || p == null || job == null || job.exitMapOnArrival || !p.Spawned || p.Downed || ProjectZ.Annex(p.Map) == null
                || !CombatGate.On(CombatPart.Jumps) || !FailSoft.IsApplied(nameof(Jump_CanHitTargetFrom_Patch)))
            {
                return false;
            }
            if (JumpAcross.AbilityOf(p) != null || !Flies(p) || !Ready(p))
            {
                return false;
            }
            IntVec3 c = FleeLanding(p, job.targetB.Thing, job.targetA.Cell);
            if (!c.IsValid)
            {
                FleeNone++;
                return false;
            }
            // (the flee ends first: the flyer would put it back in the queue and walk on to its old cell after landing)
            p.jobs.EndCurrentJob(JobCondition.Succeeded, startNewJob: false);
            if (!Fly(p, c))
            {
                return false;
            }
            FleeFlights++;
            FloorUnstrand.NoteFlee(p);
            return true;
        }
    }

    [DefOf]
    public static class PZFlightDefOf
    {
        public static JobDef PZ_FlyAcross;

        static PZFlightDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(PZFlightDefOf));
        }
    }

    // A flight to another floor (JumpAcross for a flyer): faces the landing for a moment, then takes off.
    public class JobDriver_FlyAcross : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            pawn.Map.pawnDestinationReservationManager.Reserve(pawn, job, job.targetA.Cell);
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            Toil wait = Toils_General.Wait(FloorFlight.WarmupTicks);
            wait.initAction = delegate { pawn.rotationTracker.FaceCell(job.targetA.Cell); };
            wait.FailOn(() => pawn.Downed);
            yield return wait;
            Toil go = ToilMaker.MakeToil("FlyAcross");
            go.defaultCompleteMode = ToilCompleteMode.Instant;
            go.initAction = delegate
            {
                if (!FloorFlight.Fly(pawn, job.targetA.Cell))
                {
                    EndJobWith(JobCondition.Incompletable);
                }
            };
            yield return go;
        }
    }

    // Flee by flight (FloorFlight.TryFlee): a first step before the flee's own.
    [HarmonyPatch(typeof(JobDriver_Flee), "MakeNewToils")]
    [PatchGroup(FailSoft.Combat)]
    [CombatPartOf(CombatPart.Jumps)]
    public static class Flee_FlyAcross_Patch
    {
        public static IEnumerable<Toil> Postfix(IEnumerable<Toil> toils, JobDriver_Flee __instance)
        {
            Toil fly = ToilMaker.MakeToil("FleeFlyAcross");
            fly.defaultCompleteMode = ToilCompleteMode.Instant;
            fly.initAction = delegate
            {
                try
                {
                    FloorFlight.TryFlee(__instance.pawn, __instance.job);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[ProjectZ] Flight from danger failed: " + e, 81512392);
                }
            };
            yield return fly;
            foreach (Toil t in toils)
            {
                yield return t;
            }
        }
    }
}
