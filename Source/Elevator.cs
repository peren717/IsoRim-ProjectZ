using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // The player elevator (user 2026-10-20). A 1x1 powered building (research Machining, 100 steel + 4 components, 150 W);
    // its rotation is its door side. The shaft is the column over it, up through every floor of the map: kept open like a
    // ladder's column (LadderColumn: no floor, wall, building, blueprint or roof in it, for anyone; ElevatorRule.ShaftOwner),
    // and placement is refused where it is not open already.
    //
    // It is a way of moving, not a machine that carries people: every floor along the shaft is joined to every other
    // floor by its own vertical link (VLink kind Elevator: one per pair of floors), between the cells on the door side of
    // the shaft on those floors (the stops). A stop counts only while its cell is walkable (StairGrid.UsableIn reads that
    // at every use), so the route decides where a pawn gets on and off. A ride costs ElevatorBoardTicks + ElevatorTicksPerFloor
    // per floor (0.5 s + 0.65 s per floor). Any number ride at once (no queue: LinkSteps leaves the ladder's one-holder rule
    // to ladders). Who may ride: whoever may open the owner's doors (ElevatorRule.MayUse, the game's door rule); the raiders'
    // dig components and routes leave it out.
    //
    // Without power the same links are an emergency ladder (kind ElevatorLadder: 3 s per floor, one at a time, falls); a
    // rider already on its way finishes as it began (StairGrid.Replace keeps a link with the same ends). Power back: an
    // elevator again. Gone mid-ride (deconstructed, destroyed): riders fall down the shaft to its lowest stop (LinkSteps).
    // The safety net: a shaft filled anyway (dev spawn, a landing ship, another mod) links only the floors up to the first
    // blocked one (checked when a column cell changes, LadderColumnWatch, and every rare tick); open again, it links them
    // again. Breaking it would cost 100 steel for something the player may not even have caused.
    public class Building_Elevator : Building
    {
        private CompPowerTrader power;
        private bool linkedPowered;
        private int linkedTop = int.MinValue;
        private bool ready;

        // (kept when it is gone: where its riders fall)
        public int baseLevel;
        public IntVec3 baseStop = IntVec3.Invalid;

        public static int Relinks;

        public bool Powered => power == null || power.PowerOn;

        public bool LinkedPowered => linkedPowered;

        public int LinkedTop => linkedTop;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref linkedPowered, "linkedPowered", false);
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            power = GetComp<CompPowerTrader>();
            baseLevel = Levels.LevelOf(Position, map);
            IntVec3 door = Position + Rotation.FacingCell;
            baseStop = door.InBounds(map) && ProjectZ.SameBand(door, Position, map) ? door : IntVec3.Invalid;
            ready = true;
            // (loaded: the power net says its state on its first tick; until then the links stay as they were saved)
            Relink(respawningAfterLoad ? linkedPowered : Powered);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            ready = false;
            Map?.GetComponent<StairGrid>()?.Deregister(this);
            base.DeSpawn(mode);
        }

        protected override void ReceiveCompSignal(string signal)
        {
            base.ReceiveCompSignal(signal);
            if (ready && Spawned && (signal == CompPowerTrader.PowerTurnedOnSignal || signal == CompPowerTrader.PowerTurnedOffSignal))
            {
                Relink(Powered);
            }
        }

        public override void TickRare()
        {
            base.TickRare();
            if (ready && Spawned && (Powered != linkedPowered || OpenTop() != linkedTop))
            {
                Relink(Powered);
            }
        }

        // A cell of the shaft may have filled or opened (LadderColumnWatch).
        public void ShaftChanged()
        {
            if (ready && Spawned && OpenTop() != linkedTop)
            {
                Relink(linkedPowered);
            }
        }

        // The highest level the shaft is open to: no roof under a shaft cell, no floor or building in it.
        public int OpenTop()
        {
            Map map = Map;
            IntVec3 g = Levels.Ground(Position, map);
            int top = baseLevel;
            for (int L = baseLevel + 1; L <= Levels.Max(map); L++)
            {
                IntVec3 below = Levels.Cell(g, L - 1, map);
                IntVec3 c = Levels.Cell(g, L, map);
                if (!c.IsValid || !below.IsValid || !c.InBounds(map) || map.roofGrid.Roofed(below) || ProjectZ.HasFloor(c, map) || c.GetFirstBuilding(map) != null)
                {
                    break;
                }
                top = L;
            }
            return top;
        }

        // The stop on level L (Invalid without one).
        public IntVec3 StopOn(int L)
        {
            Map map = Map;
            if (!baseStop.IsValid || map == null)
            {
                return IntVec3.Invalid;
            }
            IntVec3 c = Levels.Cell(Levels.Ground(baseStop, map), L, map);
            return c.IsValid && c.InBounds(map) ? c : IntVec3.Invalid;
        }

        private void Relink(bool on)
        {
            Map map = Map;
            StairGrid sg = map?.GetComponent<StairGrid>();
            if (sg == null)
            {
                return;
            }
            int top = OpenTop();
            linkedPowered = on;
            linkedTop = top;
            Relinks++;
            List<VLink> now = new List<VLink>();
            for (int a = baseLevel; a <= top; a++)
            {
                IntVec3 lo = StopOn(a);
                for (int b = a + 1; b <= top && lo.IsValid; b++)
                {
                    IntVec3 hi = StopOn(b);
                    if (hi.IsValid)
                    {
                        now.Add(VLink.Elevator(this, lo, hi, b - a, on, map));
                    }
                }
            }
            ProjectZ.DebugLog($"{this} at {Position}: {(on ? "powered" : "no power (a ladder)")}, shaft open to level {top}, {now.Count} link(s)");
            sg.Replace(this, now);
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            Map map = Map;
            if (map != null && ready)
            {
                List<string> stops = new List<string>();
                for (int L = baseLevel; L <= linkedTop; L++)
                {
                    IntVec3 c = StopOn(L);
                    if (c.IsValid && c.Walkable(map))
                    {
                        stops.Add((ProjectZ.FloorOf(c, map) + 1).ToString());
                    }
                }
                string line = "PZ_ElevatorStops".Translate(stops.Count == 0 ? "-" : string.Join(", ", stops));
                if (!Powered)
                {
                    line += "\n" + "PZ_ElevatorNoPower".Translate();
                }
                s = s.NullOrEmpty() ? line : s + "\n" + line;
            }
            return s;
        }
    }

    public static class ElevatorRule
    {
        public static bool IsElevatorThing(Thing t) => t != null && ElevatorDefOf.PZ_Elevator != null && (t.def == ElevatorDefOf.PZ_Elevator || t.def.entityDefToBuild == ElevatorDefOf.PZ_Elevator);

        // Who may ride (user 2026-10-20): whoever may open the owner's doors, by the game's door rule
        // (Building_Door.PawnCanOpen, for a door that checks its faction and lets roamers through). No traveller (a
        // region question without a pawn): yes, as an open door.
        public static bool MayUse(Thing elevator, Pawn p)
        {
            if (p == null)
            {
                return true;
            }
            if (!p.CanOpenDoors)
            {
                return false;
            }
            Map map = elevator?.MapHeld;
            if (map?.Parent != null && map.Parent.doorsAlwaysOpenForPlayerPawns && p.Faction == Faction.OfPlayer)
            {
                return true;
            }
            if (p.CanOpenAnyDoor)
            {
                return true;
            }
            Faction f = elevator?.Faction;
            if (f == null)
            {
                return p.RaceProps.canOpenFactionlessDoors;
            }
            if (p.guest != null && p.guest.Released)
            {
                return true;
            }
            return GenAI.MachinesLike(f, p);
        }

        // Where a rider of link l falls (an elevator: down the shaft to its lowest stop, 'below' = the floors between that
        // and the ride's lower end); anything else: the link's bottom.
        public static IntVec3 ShaftBottom(VLink l, Map map, out int below)
        {
            below = 0;
            if (!l.IsElevator || !(l.thing is Building_Elevator e) || !e.baseStop.IsValid || !e.baseStop.InBounds(map) || !e.baseStop.Walkable(map))
            {
                return l.bottom;
            }
            below = Mathf.Max(0, Levels.LevelOf(l.bottom, map) - e.baseLevel);
            return e.baseStop;
        }

        // The elevator (built, blueprint or frame) whose shaft holds c (plane: as a roof cell under the shaft), or null.
        // The shaft: the cells over the elevator on the levels above it, up to the map's top level; the planes: the
        // elevator's level up to the one under the top.
        public static Thing ShaftOwner(IntVec3 c, Map map, bool plane)
        {
            ThingDef d = ElevatorDefOf.PZ_Elevator;
            if (d == null || map == null)
            {
                return null;
            }
            List<Thing> built = map.listerThings.ThingsOfDef(d);
            List<Thing> bps = d.blueprintDef == null ? null : map.listerThings.ThingsOfDef(d.blueprintDef);
            List<Thing> frames = d.frameDef == null ? null : map.listerThings.ThingsOfDef(d.frameDef);
            if (built.Count == 0 && bps.NullOrEmpty() && frames.NullOrEmpty())
            {
                return null;
            }
            int L = Levels.LevelOf(c, map);
            IntVec3 g = Levels.Ground(c, map);
            if (L == Levels.None || !g.IsValid)
            {
                return null;
            }
            int max = Levels.Max(map);
            return In(built, g, L, max, plane, map) ?? In(bps, g, L, max, plane, map) ?? In(frames, g, L, max, plane, map);
        }

        private static Thing In(List<Thing> list, IntVec3 g, int L, int max, bool plane, Map map)
        {
            if (list == null)
            {
                return null;
            }
            for (int i = 0; i < list.Count; i++)
            {
                Thing t = list[i];
                if (!t.Spawned || Levels.Ground(t.Position, map) != g)
                {
                    continue;
                }
                int b = Levels.LevelOf(t.Position, map);
                if (plane ? L >= b && L < max : L > b && L <= max)
                {
                    return t;
                }
            }
            return null;
        }

        // The shaft over loc is open all the way up: nothing in its cells (floor, building, blueprint, frame, item, another
        // ladder's or elevator's column), no roof under them. Owners standing at loc (this elevator's own blueprint or
        // frame) do not count.
        public static bool ShaftClear(IntVec3 loc, Map map)
        {
            IntVec3 g = Levels.Ground(loc, map);
            int b = Levels.LevelOf(loc, map);
            int max = Levels.Max(map);
            for (int L = b; L <= max; L++)
            {
                IntVec3 c = Levels.Cell(g, L, map);
                if (!c.IsValid || !c.InBounds(map))
                {
                    return false;
                }
                if (L > b)
                {
                    if (LadderColumn.Busy(c, map))
                    {
                        return false;
                    }
                    Thing o = LadderColumn.Owner(c, map, false);
                    if (o != null && o.Position != loc)
                    {
                        return false;
                    }
                }
                if (L < max)
                {
                    if (map.roofGrid.Roofed(c))
                    {
                        return false;
                    }
                    Thing o = LadderColumn.Owner(c, map, true);
                    if (o != null && o.Position != loc)
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }

    // Placing an elevator: a level above, its door side on the map, and the shaft open all the way up (ElevatorRule.ShaftClear).
    public class PlaceWorker_Elevator : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            int L = ProjectZ.Annex(map) == null ? Levels.None : Levels.LevelOf(loc, map);
            if (L == Levels.None || L >= Levels.Max(map))
            {
                return "PZ_ElevatorNoFloorAbove".Translate();
            }
            IntVec3 door = loc + rot.FacingCell;
            if (!door.InBounds(map) || !ProjectZ.SameBand(door, loc, map))
            {
                return "PZ_ElevatorNoExit".Translate();
            }
            if (!ElevatorRule.ShaftClear(loc, map))
            {
                return "PZ_ElevatorShaftBlocked".Translate();
            }
            return AcceptanceReport.WasAccepted;
        }

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            Map map = Find.CurrentMap;
            IntVec3 door = center + rot.FacingCell;
            if (map != null && door.InBounds(map))
            {
                GenDraw.DrawFieldEdges(new List<IntVec3> { door }, Color.white);
            }
        }
    }

    [DefOf]
    public static class ElevatorDefOf
    {
        public static ThingDef PZ_Elevator;

        static ElevatorDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(ElevatorDefOf));
        }
    }
}
