using System;
using System.Collections.Generic;
using ProjectZ.Combat;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ
{
    // A stranded flyer flies to another floor where it can do what it needs (user 2026-10-26). A flight from danger
    // (FloorFlight.TryFlee) may land where no stairs lead back; so may a flight to a fight. Stranded = what its needs or
    // duties want can't be walked to (stairs count, ladders never) from where it stands, but can from a landing on another
    // floor within the flight's range:
    //   - every flyer: food, when hungry (the game's own food search finds none);
    //   - tamed: its master, when it follows (obedience; drafted or fieldwork); its bed, when tired; its pen (pen animals,
    //     none reachable, the game's own pen check); its allowed area, else the home area (others);
    //   - wild or another faction's: its herd (herd animals: no herdmate within 40 cells reachable, at least two there:
    //     never two loners swapping floors); room to roam (fewer than RoomCells cells reachable here, at least that many
    //     there);
    //   - manhunters: any target (none reachable here). JobGiver_AIJumpAcross takes the near ones first.
    // Goals are asked in that order; the first with a landing wins. The landing: open sky, the flight's own checks
    // (FloorFlight.CanLand), nearest to what it wants (food, master, bed, pen) or inside the area, else the shortest flight.
    // Never while calming down: CalmTicks after a flight from danger, and never with danger (what it would flee from)
    // within CalmRadius of where it stands or of the landing (not for manhunters). After a flight CooldownTicks pass before
    // the next; a miss is remembered for MemoTicks. Same flyers as FloorFlight (lists, locusts keep their jump), no lord,
    // not roped. Hooks: the Animal tree's Animal_PreMain tag (Defs/ThinkTreeDefs/PZ_FlyUnstrand.xml: after lord duties and
    // mental states, before the needs and the wander) and the manhunter branch before its wander (Patches/PZ_FlyUnstrand.xml).
    public class JobGiver_FlyUnstrand : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            try
            {
                return FloorUnstrand.TryGive(pawn);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[ProjectZ] Flight out of a stranded spot failed: " + e, 81512393);
                return null;
            }
        }
    }

    public static class FloorUnstrand
    {
        public const int CooldownTicks = 1200;
        public const int CalmTicks = 600;
        public const float CalmRadius = 24f;   // the game's animal flee distance (JobGiver_AnimalFlee)
        public const int MemoTicks = 250;
        public const int RoomCells = 150;
        public const float HerdRadius = 40f;
        public const int Tries = 12;           // landings checked fully per goal
        public const int AreaRegions = 300;    // regions searched for an area cell

        // counters (tests)
        public static int Asked, Cooling, MemoHits, Calming, NotStranded, NoLanding, Given;
        public static string LastGoal;

        private static readonly Dictionary<int, int> until = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> memo = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> fled = new Dictionary<int, int>();

        private sealed class Goal
        {
            public string name;
            // walking from a landing: the cell it heads for (scored against; the landing itself: any, the shortest
            // flight), Invalid when it can't get there (asked once per district)
            public Func<IntVec3, IntVec3> from;
            // the landing itself does (inside the area or pen): first
            public Func<IntVec3, bool> at;
            private readonly Dictionary<District, IntVec3> seen = new Dictionary<District, IntVec3>();

            public IntVec3 Anchor(IntVec3 c, Map map)
            {
                if (at != null && at(c))
                {
                    return c;
                }
                District d = c.GetDistrict(map);
                if (d == null)
                {
                    return IntVec3.Invalid;
                }
                if (!seen.TryGetValue(d, out IntVec3 a))
                {
                    a = from(c);
                    seen[d] = a;
                }
                return a;
            }
        }

        private static int Now => Find.TickManager.TicksGame;

        // (a value from another game: further ahead than it can be, ignored)
        private static bool Running(Dictionary<int, int> d, int id, int span)
        {
            int now = Now;
            return d.TryGetValue(id, out int u) && now < u && u - now <= span;
        }

        private static void Set(Dictionary<int, int> d, int id, int v)
        {
            if (d.Count > 512)
            {
                d.Clear();
            }
            d[id] = v;
        }

        // A flight from danger (FloorFlight.TryFlee): calm down first.
        public static void NoteFlee(Pawn p)
        {
            Set(fled, p.thingIDNumber, Now);
        }

        public static int CalmLeft(Pawn p)
        {
            int now = Now;
            return fled.TryGetValue(p.thingIDNumber, out int t) && now >= t && now - t < CalmTicks ? CalmTicks - (now - t) : 0;
        }

        public static Job TryGive(Pawn pawn)
        {
            if (FloorFlight.Off || pawn == null || !pawn.Spawned || pawn.Downed || (pawn.roping != null && pawn.roping.IsRoped))
            {
                return null;
            }
            Map map = pawn.Map;
            if (ProjectZ.Annex(map) == null || !CombatGate.Enabled || !FailSoft.IsApplied(nameof(Jump_CanHitTargetFrom_Patch)))
            {
                return null;
            }
            if (JumpAcross.AbilityOf(pawn) != null || !FloorFlight.Flies(pawn))
            {
                return null;
            }
            bool hunter = pawn.MentalState is MentalState_Manhunter;
            if (!hunter && (pawn.InMentalState || pawn.GetLord() != null))
            {
                return null;
            }
            Asked++;
            int id = pawn.thingIDNumber;
            if (!FloorFlight.Ready(pawn) || Running(until, id, CooldownTicks))
            {
                Cooling++;
                return null;
            }
            if (Running(memo, id, MemoTicks))
            {
                MemoHits++;
                return null;
            }
            if (!hunter && (CalmLeft(pawn) > 0 || DangerNear(pawn, pawn.Position)))
            {
                Calming++;
                return null;
            }
            Set(memo, id, Now + MemoTicks);
            TraverseParms tp = hunter ? TraverseParms.For(pawn, Danger.Deadly, TraverseMode.PassDoors) : TraverseParms.For(pawn, Danger.Deadly, TraverseMode.ByPawn);
            List<Goal> goals = Goals(pawn, hunter, tp);
            if (goals.Count == 0)
            {
                NotStranded++;
                return null;
            }
            IntVec3 c = Landing(pawn, goals, hunter, out Goal g);
            if (!c.IsValid)
            {
                NoLanding++;
                return null;
            }
            Set(until, id, Now + CooldownTicks);
            Given++;
            LastGoal = g.name;
            ProjectZ.DebugLog($"{pawn} stranded ({g.name}): flies to {c} (level {Levels.LevelOf(c, map)})");
            return FloorFlight.MakeJob(c);
        }

        // What it wants and can't walk to here, most pressing first.
        private static List<Goal> Goals(Pawn pawn, bool hunter, TraverseParms tp)
        {
            List<Goal> list = new List<Goal>();
            if (hunter)
            {
                Hunt(pawn, tp, list);
                return list;
            }
            Food(pawn, tp, list);
            if (pawn.Faction == Faction.OfPlayer)
            {
                Master(pawn, tp, list);
                Bed(pawn, tp, list);
                if (pawn.Roamer)
                {
                    Pen(pawn, list);
                }
                else
                {
                    AreaGoal(pawn, tp, list);
                }
            }
            else
            {
                Herd(pawn, tp, list);
                Room(pawn, tp, list);
            }
            return list;
        }

        private static void Food(Pawn pawn, TraverseParms tp, List<Goal> list)
        {
            Need_Food f = pawn.needs?.food;
            if (f == null || f.CurCategory < HungerCategory.Hungry)
            {
                return;
            }
            bool desperate = f.CurCategory == HungerCategory.Starving;
            if (FoodUtility.TryFindBestFoodSourceFor(pawn, pawn, desperate, out _, out _, canRefillDispenser: false, canUseInventory: false))
            {
                return;
            }
            Map map = pawn.Map;
            ThingRequest req = (pawn.RaceProps.foodType & (FoodTypeFlags.Plant | FoodTypeFlags.Tree)) != 0
                ? ThingRequest.ForGroup(ThingRequestGroup.FoodSource) : ThingRequest.ForGroup(ThingRequestGroup.FoodSourceNotPlantOrTree);
            int regions = pawn.Faction == Faction.OfPlayer ? 100 : 30;   // as the game's animal food search
            Predicate<Thing> ok = t => t.def.IsNutritionGivingIngestible && t.IngestibleNow && pawn.WillEat(t, pawn)
                && (desperate || ((int)t.def.ingestible.preferability > (int)FoodPreferability.DesperateOnly && !t.IsNotFresh()))
                && !t.IsForbidden(pawn) && !t.IsDessicated();
            list.Add(new Goal
            {
                name = "food",
                from = c =>
                {
                    Thing t = GenClosest.ClosestThingReachable(c, map, req, PathEndMode.OnCell, tp, 9999f, ok, searchRegionsMax: regions);
                    return t != null && !pawn.CanReach(t, PathEndMode.OnCell, Danger.Deadly) ? t.Position : IntVec3.Invalid;
                }
            });
        }

        private static void Master(Pawn pawn, TraverseParms tp, List<Goal> list)
        {
            Pawn m = pawn.playerSettings?.RespectedMaster;
            if (m == null || !m.Spawned || m.Map != pawn.Map || pawn.training == null || !pawn.training.HasLearned(TrainableDefOf.Obedience))
            {
                return;
            }
            bool follows = (pawn.playerSettings.followDrafted && m.Drafted) || (pawn.playerSettings.followFieldwork && m.mindState?.lastJobTag == JobTag.Fieldwork);
            if (!follows || pawn.CanReach(m, PathEndMode.OnCell, Danger.Deadly))
            {
                return;
            }
            Map map = pawn.Map;
            list.Add(new Goal { name = "master", from = c => map.reachability.CanReach(c, m, PathEndMode.Touch, tp) ? m.Position : IntVec3.Invalid });
        }

        private static void Bed(Pawn pawn, TraverseParms tp, List<Goal> list)
        {
            Building_Bed bed = pawn.ownership?.OwnedBed;
            Need_Rest r = pawn.needs?.rest;
            if (bed == null || !bed.Spawned || bed.Map != pawn.Map || r == null || r.CurCategory < RestCategory.Tired || pawn.CanReach(bed, PathEndMode.OnCell, Danger.Deadly))
            {
                return;
            }
            Map map = pawn.Map;
            list.Add(new Goal { name = "bed", from = c => map.reachability.CanReach(c, bed, PathEndMode.OnCell, tp) ? bed.Position : IntVec3.Invalid });
        }

        // A pen animal outside every pen it could be walked to (the game's checks): an enclosed pen it is accepted in.
        private static void Pen(Pawn pawn, List<Goal> list)
        {
            Map map = pawn.Map;
            if (AnimalPenUtility.GetCurrentPenOf(pawn, false) != null || AnimalPenUtility.AnySuitablePens(pawn, false))
            {
                return;
            }
            List<CompAnimalPenMarker> pens = new List<CompAnimalPenMarker>();
            foreach (Building b in map.listerBuildings.allBuildingsAnimalPenMarkers)
            {
                CompAnimalPenMarker m = b.TryGetComp<CompAnimalPenMarker>();
                if (m != null && m.PenState.Enclosed && m.AcceptsToPen(pawn) && !b.IsForbidden(Faction.OfPlayer))
                {
                    pens.Add(m);
                }
            }
            if (pens.Count == 0)
            {
                return;
            }
            TraverseParms ptp = TraverseParms.For(TraverseMode.PassDoors).WithFenceblockedOf(pawn);
            list.Add(new Goal
            {
                name = "pen",
                at = c =>
                {
                    Region r = c.GetRegion(map);
                    return r != null && pens.Exists(m => m.PenState.ContainsConnectedRegion(r));
                },
                from = c =>
                {
                    CompAnimalPenMarker m = pens.Find(x => map.reachability.CanReach(c, x.parent, PathEndMode.Touch, ptp));
                    return m != null ? m.parent.Position : IntVec3.Invalid;
                }
            });
        }

        // Its allowed area, else (none set) the home area.
        private static void AreaGoal(Pawn pawn, TraverseParms tp, List<Goal> list)
        {
            Map map = pawn.Map;
            Area own = pawn.playerSettings?.EffectiveAreaRestrictionInPawnCurrentMap;
            Area a = own ?? map.areaManager.Home;
            if (a == null || a.TrueCount == 0 || AreaReach(pawn.Position, a, map, tp))
            {
                return;
            }
            list.Add(new Goal { name = own != null ? "area" : "home", at = c => a[c], from = c => AreaReach(c, a, map, tp) ? c : IntVec3.Invalid });
        }

        private static void Herd(Pawn pawn, TraverseParms tp, List<Goal> list)
        {
            if (!pawn.RaceProps.herdAnimal)
            {
                return;
            }
            Map map = pawn.Map;
            List<Pawn> mates = new List<Pawn>();
            IReadOnlyList<Pawn> all = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
            {
                Pawn q = all[i];
                if (q != pawn && q.def == pawn.def && q.Faction == pawn.Faction && !q.Dead && !q.Downed && Range3D.Distance(pawn.Position, q.Position, map) <= HerdRadius)
                {
                    if (pawn.CanReach(q, PathEndMode.Touch, Danger.Deadly))
                    {
                        return;
                    }
                    mates.Add(q);
                }
            }
            if (mates.Count < 2)
            {
                return;
            }
            list.Add(new Goal
            {
                name = "herd",
                from = c =>
                {
                    int n = 0;
                    IntVec3 first = IntVec3.Invalid;
                    foreach (Pawn q in mates)
                    {
                        if (map.reachability.CanReach(c, q, PathEndMode.Touch, tp))
                        {
                            n++;
                            if (!first.IsValid)
                            {
                                first = q.Position;
                            }
                        }
                    }
                    return n >= 2 ? first : IntVec3.Invalid;
                }
            });
        }

        private static void Room(Pawn pawn, TraverseParms tp, List<Goal> list)
        {
            Map map = pawn.Map;
            if (ReachCells(pawn.Position, map, tp) >= RoomCells)
            {
                return;
            }
            list.Add(new Goal { name = "room", from = c => ReachCells(c, map, tp) >= RoomCells ? c : IntVec3.Invalid });
        }

        private static void Hunt(Pawn pawn, TraverseParms tp, List<Goal> list)
        {
            Map map = pawn.Map;
            List<KeyValuePair<float, Pawn>> found = new List<KeyValuePair<float, Pawn>>();
            IReadOnlyList<Pawn> all = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
            {
                Pawn t = all[i];
                if (t == pawn || t.Dead || !pawn.HostileTo(t) || t.ThreatDisabled(pawn) || !AttackTargetFinder.IsAutoTargetable(t))
                {
                    continue;
                }
                if (pawn.CanReach(t, PathEndMode.Touch, Danger.Deadly, canBashDoors: true, canBashFences: true, TraverseMode.PassDoors))
                {
                    return;
                }
                found.Add(new KeyValuePair<float, Pawn>(Range3D.Distance(pawn.Position, t.Position, map), t));
            }
            if (found.Count == 0)
            {
                return;
            }
            found.Sort((a, b) => a.Key.CompareTo(b.Key));
            list.Add(new Goal
            {
                name = "target",
                from = c =>
                {
                    for (int i = 0; i < found.Count && i < 6; i++)
                    {
                        if (map.reachability.CanReach(c, found[i].Value, PathEndMode.Touch, tp))
                        {
                            return found[i].Value.Position;
                        }
                    }
                    return IntVec3.Invalid;
                }
            });
        }

        // A landing on another floor for the first goal that has one; Invalid when none.
        private static IntVec3 Landing(Pawn pawn, List<Goal> goals, bool hunter, out Goal chosen)
        {
            chosen = null;
            Map map = pawn.Map;
            IntVec3 root = pawn.Position;
            IntVec3 g = ProjectZ.GroundCellOf(root, map);
            int k = Levels.LevelOf(root, map);
            if (!g.IsValid)
            {
                return IntVec3.Invalid;
            }
            List<IntVec3> cells = new List<IntVec3>();
            int n = GenRadial.NumCellsInRadius(FloorFlight.Range);
            foreach (int L in Levels.All(map))
            {
                if (L == k)
                {
                    continue;
                }
                for (int i = 0; i < n; i++)
                {
                    IntVec3 c = ProjectZ.CellOnFloor(g + GenRadial.RadialPattern[i], L, map);
                    if (!c.IsValid || !c.InBounds(map) || Levels.LevelOf(c, map) != L || !c.Standable(map) || !Column.OpenAbove(c, map)
                        || Range3D.Distance(root, c, map) > FloorFlight.Range || c.GetFirstPawn(map) != null
                        || !map.pawnDestinationReservationManager.CanReserve(c, pawn))
                    {
                        continue;
                    }
                    cells.Add(c);
                }
            }
            if (cells.Count == 0)
            {
                return IntVec3.Invalid;
            }
            List<KeyValuePair<float, IntVec3>> scored = new List<KeyValuePair<float, IntVec3>>();
            foreach (Goal goal in goals)
            {
                scored.Clear();
                foreach (IntVec3 c in cells)
                {
                    IntVec3 a = goal.Anchor(c, map);
                    if (!a.IsValid)
                    {
                        continue;
                    }
                    bool inside = goal.at != null && goal.at(c);
                    scored.Add(new KeyValuePair<float, IntVec3>((inside ? 0f : 1000f) + Range3D.Distance(c, a == c ? root : a, map), c));
                }
                scored.Sort((x, y) => x.Key.CompareTo(y.Key));
                for (int i = 0, tried = 0; i < scored.Count && tried < Tries; i++)
                {
                    IntVec3 c = scored[i].Value;
                    if (!hunter && DangerNear(pawn, c))
                    {
                        continue;
                    }
                    tried++;
                    if (FloorFlight.CanLand(pawn, c))
                    {
                        chosen = goal;
                        return c;
                    }
                }
            }
            return IntVec3.Invalid;
        }

        // Something the pawn would flee from (the game's animal flee: its potential attackers, and for wild animals what
        // always scares them) within CalmRadius (3D) of c.
        public static bool DangerNear(Pawn pawn, IntVec3 c)
        {
            Map map = pawn.Map;
            List<IAttackTarget> targets = map.attackTargetsCache.GetPotentialTargetsFor(pawn);
            for (int i = 0; i < targets.Count; i++)
            {
                Thing t = targets[i].Thing;
                if (t != null && t.Spawned && Range3D.Distance(c, t.Position, map) <= CalmRadius && FleeUtility.ShouldFleeFrom(t, pawn, false, false))
                {
                    return true;
                }
            }
            if (pawn.Faction == null)
            {
                List<Thing> scary = map.listerThings.ThingsInGroup(ThingRequestGroup.AlwaysFlee);
                for (int i = 0; i < scary.Count; i++)
                {
                    if (Range3D.Distance(c, scary[i].Position, map) <= CalmRadius && FleeUtility.ShouldFleeFrom(scary[i], pawn, false, false))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // An area cell can be walked to from root (stairs count).
        private static bool AreaReach(IntVec3 root, Area a, Map map, TraverseParms tp)
        {
            if (a[root])
            {
                return true;
            }
            Region r = root.GetRegion(map);
            if (r == null)
            {
                return false;
            }
            bool found = false;
            RegionTraverser.BreadthFirstTraverse(r, (from, to) => to.Allows(tp, false), reg =>
            {
                foreach (IntVec3 c in reg.Cells)
                {
                    if (a[c])
                    {
                        found = true;
                        return true;
                    }
                }
                return false;
            }, AreaRegions);
            return found;
        }

        // Cells walkable from root (stairs count), counted up to RoomCells.
        private static int ReachCells(IntVec3 root, Map map, TraverseParms tp)
        {
            Region r = root.GetRegion(map);
            if (r == null)
            {
                return 0;
            }
            int n = 0;
            RegionTraverser.BreadthFirstTraverse(r, (from, to) => to.Allows(tp, false), reg =>
            {
                n += reg.CellCount;
                return n >= RoomCells;
            }, 64);
            return n;
        }
    }
}
