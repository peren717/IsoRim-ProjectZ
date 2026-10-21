using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // The player elevator (user 2026-10-20). A 1x1 powered building (research Machining, 100 steel + 4 components, 150 W);
    // its rotation is its door side. The shaft is the column over it, up through every floor of the map (user 2026-10-20,
    // second round):
    //   - kept open like a ladder's column (LadderColumn, ElevatorRule.ShaftOwner): no wall, building, blueprint or item in
    //     it, for anyone; while it is only a blueprint or frame, no floor either;
    //   - its cap: a floor laid in the shaft of a built elevator ends it there (it serves the floors below; the placement
    //     says so, ElevatorCap); the floor taken up again, it reaches up again (the roof the floor leaves under it becomes
    //     the shaft's own roof, ColumnRoof via Stairwell.RoofFor). A roof cannot cap it: the shaft is roofed already;
    //   - roofed: every shaft cell from the elevator's to the top one gets the invisible stairwell roof (ColumnRoof): no
    //     rain or snow in it, rooms beside it stay indoors, falls, shots and the link pass (Column.OpenAbove);
    //   - placed under floors: they are marked for removal like a stairwell's (ElevatorSite), and the elevator waits for
    //     them; a floor that cannot be taken up (something on it), a building, rock or a rock roof in the way: refused.
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
    // Taking it down (K2, as for the last stairs): not while its own floor would be cut off from the map edge without it
    // (the worker stands there); a powered rider gets a platform drawn under it (IsoRim Api 8, ElevatorDraw).
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

        // Linked and standing (the shaft's roofs and reservation follow linkedTop only then).
        public bool Ready => ready;

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
            Map map = Map;
            map?.GetComponent<StairGrid>()?.Deregister(this);
            if (map != null)
            {
                ColumnRoof.Clear(map, Levels.Ground(Position, map), baseLevel, Levels.Max(map));
            }
            base.DeSpawn(mode);
        }

        // K2: taking it up would leave its own floor (where the worker stands) cut off from the map edge.
        public bool CutsOffItsFloor()
        {
            Patches.RouteGraph g = Patches.RouteGraph.For(Map);
            return g != null && baseStop.IsValid && g.CutsOff(this, baseStop);
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
                if (!c.IsValid || !below.IsValid || !c.InBounds(map) || ElevatorRule.CapRoof(map.roofGrid.RoofAt(below)) || ProjectZ.HasFloor(c, map) || c.GetFirstBuilding(map) != null)
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
            // the shaft's roofs up to its top; none over its cells above a cap
            IntVec3 g = Levels.Ground(Position, map);
            ColumnRoof.Apply(map, g, baseLevel, top);
            ColumnRoof.Clear(map, g, top + 1, Levels.Max(map));
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
        // A roof that ends the shaft: a floor laid in it brings one (its cap), rock; a built roof does not (it becomes the
        // shaft's own, ColumnRoof).
        public static bool CapRoof(RoofDef r) => r != null && r != PZDefOf.PZ_StairwellRoof && (r == PZDefOf.PZ_FloorRoof || r.isNatural || r.isThickRoof);

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

        // (a built elevator's shaft ends at its cap: linkedTop; a site's runs to the top level)
        private static Thing In(List<Thing> list, IntVec3 g, int L, int maxLevel, bool plane, Map map)
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
                int max = t is Building_Elevator e && e.Ready ? e.LinkedTop : maxLevel;
                if (plane ? L >= b && L < max : L > b && L <= max)
                {
                    return t;
                }
            }
            return null;
        }

        // The shaft over loc can be opened all the way up: in its cells nothing but floors that can be taken up (no
        // building, blueprint, frame or item, no other ladder's or elevator's column), no rock roof under them (a built
        // roof becomes the shaft's own). Owners standing at loc (this elevator's own blueprint or frame) do not count.
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
                    if (ProjectZ.HasFloor(c, map) ? !StairwellFloor.CanClear(c, map) : LadderColumn.Busy(c, map))
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
                    RoofDef r = map.roofGrid.RoofAt(c);
                    if (r != null && (r.isNatural || r.isThickRoof))
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

namespace ProjectZ
{
    // The roof of an open column (user 2026-10-20, second round): the cells of a built elevator's shaft (its level up to
    // its cap or the top level) and of a built ladder's column (the foot's level up to the top's) carry the invisible
    // stairwell roof (PZ_StairwellRoof): rooms beside them stay indoors, no rain or snow falls in, and the column stays
    // open for the link, falls and shots (Column.OpenAbove). Stairwell.RoofFor makes any roof written there this one,
    // so a floor taken up from the column leaves no ordinary roof behind; set when the column opens, taken away when it
    // ends or closes (a cap: the cells above it).
    public static class ColumnRoof
    {
        // c is a cell of an open column.
        public static bool Open(IntVec3 c, Map map)
        {
            if (map == null || !c.IsValid || !c.InBounds(map) || ProjectZ.Annex(map) == null)
            {
                return false;
            }
            int L = Levels.LevelOf(c, map);
            IntVec3 g = Levels.Ground(c, map);
            if (L == Levels.None || !g.IsValid)
            {
                return false;
            }
            ThingDef d = ElevatorDefOf.PZ_Elevator;
            if (d != null)
            {
                List<Thing> els = map.listerThings.ThingsOfDef(d);
                for (int i = 0; i < els.Count; i++)
                {
                    if (els[i] is Building_Elevator e && e.Ready && e.Spawned && Levels.Ground(e.Position, map) == g && L >= e.baseLevel && L <= e.LinkedTop)
                    {
                        return true;
                    }
                }
            }
            StairGrid sg = StairGrid.For(map);
            if (sg != null)
            {
                for (int i = 0; i < sg.links.Count; i++)
                {
                    VLink l = sg.links[i];
                    if (l.IsLadder && !l.IsElevator && Levels.Ground(l.bottom, map) == g && L >= Levels.LevelOf(l.bottom, map) && L <= Levels.LevelOf(l.top, map))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // A roof under a column cell that closes it (the stairwell's does not).
        public static bool Blocks(IntVec3 c, Map map)
        {
            RoofDef r = map.roofGrid.RoofAt(c);
            return r != null && r != PZDefOf.PZ_StairwellRoof;
        }

        // The column over g from level lo to hi gets its roof (no roof, or a built one: the stairwell roof; a floor's
        // roof, a rock roof: left).
        public static void Apply(Map map, IntVec3 g, int lo, int hi)
        {
            if (map == null || !g.IsValid || ProjectZ.Annex(map) == null || PZDefOf.PZ_StairwellRoof == null)
            {
                return;
            }
            for (int L = lo; L <= hi; L++)
            {
                IntVec3 c = Levels.Cell(g, L, map);
                if (!c.IsValid || !c.InBounds(map))
                {
                    continue;
                }
                RoofDef r = map.roofGrid.RoofAt(c);
                if (r == null || r != PZDefOf.PZ_StairwellRoof && r != PZDefOf.PZ_FloorRoof && !r.isNatural && !r.isThickRoof)
                {
                    map.roofGrid.SetRoof(c, PZDefOf.PZ_StairwellRoof);
                    Applied++;
                }
            }
        }

        // The column's roof taken away again (cells that are no stairwell or shaft any more).
        public static void Clear(Map map, IntVec3 g, int lo, int hi)
        {
            if (map == null || !g.IsValid || ProjectZ.Annex(map) == null)
            {
                return;
            }
            for (int L = lo; L <= hi; L++)
            {
                IntVec3 c = Levels.Cell(g, L, map);
                if (c.IsValid && c.InBounds(map) && map.roofGrid.RoofAt(c) == PZDefOf.PZ_StairwellRoof && Stairwell.RoofFor(c, map, PZDefOf.PZ_StairwellRoof) != PZDefOf.PZ_StairwellRoof)
                {
                    map.roofGrid.SetRoof(c, null);
                }
            }
        }

        public static int Applied;
    }

    // The platform under each powered elevator rider, moving with it (IsoRim Api 8: Api.LoadModel / Api.DrawModel;
    // an older IsoRim or none: nothing). Drawn each frame from LinkSteps.MapComponentUpdate, in the shaft (the elevator's
    // cell on the rider's floor's rows), turned to the door side, its deck at the rider's feet (Links.Rise: true height
    // over its own floor, as IsoRim's floors expect). The model: pz_elevator_platform.obj (LiftModels).
    public static class ElevatorDraw
    {
        // (tests) platforms drawn
        public static int Drawn;

        public static void Platforms(Map map, List<LinkSteps.Step> steps)
        {
            if (!IsoRimBridge.Present || IsoRimBridge.ApiVersion < 8 || map != Find.CurrentMap)
            {
                return;
            }
            Draw(map, steps);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Draw(Map map, List<LinkSteps.Step> steps)
        {
            if (!IsoRim.Api.ModelsOn || !(LiftModels.Handle(LiftModels.Platform) is IsoRim.ModelHandle m))
            {
                return;
            }
            int view = FloorView.Current;
            for (int i = 0; i < steps.Count; i++)
            {
                LinkSteps.Step st = steps[i];
                Pawn p = st.pawn;
                if (st.link.kind != LinkKind.Elevator || p == null || !p.Spawned || p.pather == null || !p.pather.Moving || p.Position != st.from || p.pather.nextCell != st.to)
                {
                    continue;
                }
                int floor = ProjectZ.FloorOf(p.Position, map);
                if (!LiftModels.LevelDrawn(floor, view))
                {
                    continue;   // the rider's floor is not drawn (above the viewed one; while peeking with IsoRim Api >= 10 it is, faded: LiftModels)
                }
                Thing el = st.link.thing;
                IntVec3 shaft = el != null ? ProjectZ.CellOnFloor(ProjectZ.GroundCellOf(el.Position, map), floor, map) : IntVec3.Invalid;
                Vector3 at = shaft.IsValid ? shaft.ToVector3Shifted() : p.DrawPos;
                IsoRim.Api.DrawModel(m, new Vector3(at.x, Links.Rise(p) - LiftModels.PlatformDeck, at.z), el != null ? el.Rotation.AsAngle : 0f, Vector3.one, Color.white);
                Drawn++;
            }
        }
    }

    // Placed under floors (user 2026-10-20, second round): as a stairwell, the floors in the shaft get the
    // remove-upper-floor designation when the elevator's blueprint or frame spawns (StairwellMarks keeps which it added),
    // the elevator is not built before they are gone, and a cancelled site takes back the designations it added.
    public static class ElevatorSite
    {
        public static bool Is(Thing t) => t != null && (t is Blueprint || t is Frame) && t.def.entityDefToBuild == ElevatorDefOf.PZ_Elevator && ElevatorDefOf.PZ_Elevator != null;

        private static IEnumerable<IntVec3> Shaft(Thing site)
        {
            Map map = site.Map;
            IntVec3 g = Levels.Ground(site.Position, map);
            int b = Levels.LevelOf(site.Position, map);
            for (int L = b + 1; L <= Levels.Max(map); L++)
            {
                IntVec3 c = Levels.Cell(g, L, map);
                if (c.IsValid && c.InBounds(map))
                {
                    yield return c;
                }
            }
        }

        public static void Mark(Thing site)
        {
            Map map = site.Map;
            StairwellMarks marks = map.GetComponent<StairwellMarks>();
            foreach (IntVec3 c in Shaft(site))
            {
                if (ProjectZ.HasFloor(c, map) && map.designationManager.DesignationAt(c, DesignationDefOf.RemoveFoundation) == null)
                {
                    map.designationManager.AddDesignation(new Designation(c, DesignationDefOf.RemoveFoundation));
                    marks?.cells.Add(c);
                }
            }
        }

        public static void Unmark(Thing site, Map map)
        {
            StairwellMarks marks = map.GetComponent<StairwellMarks>();
            if (marks == null || marks.cells.Count == 0)
            {
                return;
            }
            IntVec3 g = Levels.Ground(site.Position, map);
            int b = Levels.LevelOf(site.Position, map);
            for (int L = b + 1; L <= Levels.Max(map); L++)
            {
                IntVec3 c = Levels.Cell(g, L, map);
                if (c.IsValid && marks.cells.Remove(c))
                {
                    map.designationManager.DesignationAt(c, DesignationDefOf.RemoveFoundation)?.Delete();
                }
            }
        }

        // A site with a floor still in its shaft.
        public static bool Waiting(Thing t)
        {
            if (!Is(t) || !t.Spawned || ProjectZ.Annex(t.Map) == null)
            {
                return false;
            }
            foreach (IntVec3 c in Shaft(t))
            {
                if (ProjectZ.HasFloor(c, t.Map))
                {
                    return true;
                }
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.SpawnSetup))]
    public static class ElevatorSite_Spawn_Patch
    {
        public static void Postfix(Thing __instance, bool respawningAfterLoad)
        {
            if (!respawningAfterLoad && ElevatorSite.Is(__instance) && ProjectZ.Annex(__instance.Map) != null)
            {
                ElevatorSite.Mark(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.DeSpawn))]
    public static class ElevatorSite_DeSpawn_Patch
    {
        public static void Prefix(Thing __instance, DestroyMode mode)
        {
            if (mode == DestroyMode.Cancel && ElevatorSite.Is(__instance) && __instance.Map != null)
            {
                ElevatorSite.Unmark(__instance, __instance.Map);
            }
        }
    }

    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanConstruct), new[] { typeof(Thing), typeof(Pawn), typeof(bool), typeof(bool), typeof(JobDef) })]
    public static class CanConstruct_ElevatorSite_Patch
    {
        public static bool Prefix(Thing t, ref bool __result)
        {
            if (ElevatorSite.Waiting(t))
            {
                JobFailReason.Is(StairwellFloor.WaitingLine);
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Blueprint_Build), nameof(Blueprint_Build.GetInspectString))]
    public static class BlueprintInspect_ElevatorSite_Patch
    {
        public static void Postfix(Blueprint_Build __instance, ref string __result)
        {
            if (ElevatorSite.Waiting(__instance))
            {
                __result = __result.NullOrEmpty() ? StairwellFloor.WaitingLine : __result + "\n" + StairwellFloor.WaitingLine;
            }
        }
    }

    [HarmonyPatch(typeof(Frame), nameof(Frame.GetInspectString))]
    public static class FrameInspect_ElevatorSite_Patch
    {
        public static void Postfix(Frame __instance, ref string __result)
        {
            if (ElevatorSite.Waiting(__instance))
            {
                __result = __result.NullOrEmpty() ? StairwellFloor.WaitingLine : __result + "\n" + StairwellFloor.WaitingLine;
            }
        }
    }

    // A floor planned in a built elevator's shaft caps it: said once when it is placed (placement allows it).
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.PlaceBlueprintForBuild))]
    public static class ElevatorCap
    {
        private static float lastShown = -10f;
        public static int Shown;

        public static void Postfix(BuildableDef sourceDef, IntVec3 center, Map map)
        {
            if (!(sourceDef is TerrainDef) || map == null || ProjectZ.Annex(map) == null || !(LadderColumn.Owner(center, map, false) is Building_Elevator))
            {
                return;
            }
            Shown++;
            if (Time.realtimeSinceStartup - lastShown > 2f)
            {
                lastShown = Time.realtimeSinceStartup;
                Messages.Message("PZ_ElevatorCap".Translate(), MessageTypeDefOf.CautionInput, historical: false);
            }
        }
    }

    // K2 for an elevator: not taken up while its own floor (where the worker stands) would be cut off without it.
    [HarmonyPatch(typeof(WorkGiver_Deconstruct), nameof(WorkGiver_Deconstruct.HasJobOnThing))]
    public static class Deconstruct_ElevatorLastWay_Patch
    {
        public static void Postfix(Thing t, ref bool __result)
        {
            if (__result && t is Building_Elevator e && e.Spawned && e.CutsOffItsFloor())
            {
                JobFailReason.Is("PZ_ElevatorLastWay".Translate());
                __result = false;
            }
        }
    }
}
