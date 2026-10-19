using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // Jumping down (design §3): a one-way link from a walkable upper-floor cell c over its open-air neighbour n (one of
    // four sides: a slab edge, a hole, the stairwell) down to the first floor or ground below n whose column is open
    // (Column.OpenAbove: no landing on roofs). Reachability, region searches and the router see it as a link from c's
    // region to the landing's region, never the other way. Pawns only take it when no stair route exists (StairRouter
    // pass 2); landing bruises and staggers them.
    public struct Jump
    {
        public Region to;
        public IntVec3 from;
        public IntVec3 land;
    }

    public class JumpGrid : MapComponent
    {
        public const int JumpTicks = 30;
        public const float DamagePerFloor = 3f;   // Blunt, twice (vanilla bruises)
        public const int StaggerTicks = 60;

        private static readonly List<Jump> None = new List<Jump>();
        private readonly Dictionary<Region, List<Jump>> byRegion = new Dictionary<Region, List<Jump>>();
        private readonly List<KeyValuePair<Pawn, int>> landings = new List<KeyValuePair<Pawn, int>>();
        private bool listening;

        public JumpGrid(Map map) : base(map)
        {
        }

        private static Map lastMap;
        private static JumpGrid last;

        // The jump index of a map with floors, else null (last map cached).
        public static JumpGrid For(Map map)
        {
            if (map == null || ProjectZ.Annex(map) == null)
            {
                return null;
            }
            if (!ReferenceEquals(map, lastMap))
            {
                last = map.components == null ? null : map.GetComponent<JumpGrid>();
                lastMap = map;
            }
            return last;
        }

        public void Dirty()
        {
            byRegion.Clear();
            Patches.GroundParts.Dirty(map);
        }

        private void Listen()
        {
            if (!listening && map.events != null)
            {
                listening = true;
                map.events.RegionsRoomsChanged += Dirty;
                map.events.CellFogChanged += FogChanged;
                map.events.MapFogged += MapFogged;
            }
        }

        // The player's pawns take no stairs or jump into fog (StairRegions.FogBlocks): when fog comes or goes where a
        // jump can land (a column open above) or at a stairs end, reachability answers change. (The game's own cache
        // never minds fog: on the ground it never changes what can be reached.)
        private void FogChanged(IntVec3 c, bool fogged)
        {
            if (c.InBounds(map) && (Column.OpenAbove(c, map) || StairEnd(c)))
            {
                map.reachability?.ClearCache();
            }
        }

        private void MapFogged()
        {
            map.reachability?.ClearCache();
        }

        private bool StairEnd(IntVec3 c)
        {
            StairGrid g = StairGrid.For(map);
            if (g == null)
            {
                return false;
            }
            for (int i = 0; i < g.links.Count; i++)
            {
                if (g.links[i].top == c || g.links[i].bottom == c)
                {
                    return true;
                }
            }
            return false;
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            Listen();
        }

        // A roof or floor changed in a column: landings there may have changed.
        public static void ColumnChanged(IntVec3 c, Map map)
        {
            JumpGrid g = For(map);
            if (g == null)
            {
                return;
            }
            IntVec3 up = StairShape.Above(c, map);
            if (up.IsValid && up.InBounds(map) && up.GetTerrain(map) == PZDefOf.PZ_OpenAir)
            {
                g.Dirty();
                map.reachability?.ClearCache();
            }
        }

        // Where a pawn standing on c lands if it jumps over its neighbour in direction dir; false if it can't.
        public static bool LandingFrom(IntVec3 c, IntVec3 dir, Map map, out IntVec3 land, out int fallen)
        {
            land = IntVec3.Invalid;
            fallen = 0;
            int k = ProjectZ.FloorOf(c, map);
            if (k < 1)
            {
                return false;
            }
            IntVec3 n = c + dir;
            if (!n.InBounds(map) || ProjectZ.FloorOf(n, map) != k || n.GetTerrain(map) != PZDefOf.PZ_OpenAir)
            {
                return false;
            }
            IntVec3 g = ProjectZ.GroundCellOf(n, map);
            for (int j = k - 1; j >= 0; j--)
            {
                IntVec3 cj = ProjectZ.CellOnFloor(g, j, map);
                if (!cj.IsValid)
                {
                    return false;
                }
                if (!Column.FloorAt(g, j, map))
                {
                    continue;   // open air on this floor too: falls on
                }
                if (!cj.Walkable(map) || !Column.OpenAbove(cj, map) || StairSite(cj, map))
                {
                    return false;   // no landing on a roof, on something impassable or on a stairs building site, and no falling further
                }
                land = cj;
                fallen = k - j;
                return true;
            }
            return false;
        }

        // A blueprint or frame of stairs in a cell: nobody lands on the building site (a stairwell over one is no way down
        // until the stairs stand; then they are the way).
        private static bool StairSite(IntVec3 c, Map map)
        {
            List<Thing> list = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < list.Count; i++)
            {
                if ((list[i] is Blueprint || list[i] is Frame) && StairDefs.Is(list[i].def.entityDefToBuild))
                {
                    return true;
                }
            }
            return false;
        }

        // The jumps out of a region (empty for the ground and most regions). Built on first ask.
        public List<Jump> Down(Region region)
        {
            if (region == null || !region.valid)
            {
                return None;
            }
            if (byRegion.TryGetValue(region, out List<Jump> list))
            {
                return list;
            }
            Listen();
            list = None;
            if (ProjectZ.FloorOf(region.AnyCell, map) >= 1)
            {
                RegionGrid rg = map.regionGrid;
                foreach (IntVec3 c in region.Cells)
                {
                    for (int d = 0; d < 4; d++)
                    {
                        if (LandingFrom(c, GenAdj.CardinalDirections[d], map, out IntVec3 land, out int fallen))
                        {
                            Region to = rg.GetValidRegionAt_NoRebuild(land);
                            if (to != null && to != region)
                            {
                                if (ReferenceEquals(list, None))
                                {
                                    list = new List<Jump>();
                                }
                                list.Add(new Jump { to = to, from = c, land = land });
                            }
                        }
                    }
                }
            }
            byRegion[region] = list;
            return list;
        }

        // Is the step from -> to a jump (to = a landing from 'from')? Floors fallen, or 0.
        public static int JumpStep(IntVec3 from, IntVec3 to, Map map)
        {
            for (int d = 0; d < 4; d++)
            {
                if (LandingFrom(from, GenAdj.CardinalDirections[d], map, out IntVec3 land, out int fallen) && land == to)
                {
                    return fallen;
                }
            }
            return 0;
        }

        // A pawn just landed: hurt and staggered on this map's next tick.
        public void Landed(Pawn p, int fallen)
        {
            landings.Add(new KeyValuePair<Pawn, int>(p, fallen));
        }

        public override void MapComponentTick()
        {
            if (landings.Count == 0)
            {
                return;
            }
            for (int i = 0; i < landings.Count; i++)
            {
                Pawn p = landings[i].Key;
                int fallen = landings[i].Value;
                if (p == null || p.Dead || !p.Spawned || p.Map != map)
                {
                    continue;
                }
                FleckMaker.ThrowDustPuff(p.Position, map, 1f);
                // (who is carried is read first: the carrier's own hurt can end its job and drop them)
                Pawn carried = p.carryTracker?.CarriedThing as Pawn;
                ProjectZ.DebugLog($"{p} landed ({fallen} floor(s)), carrying {carried}");
                Hurt(p, fallen);
                if (carried != null && !carried.Dead && !carried.Destroyed)
                {
                    Hurt(carried, fallen);   // Q4: the carried pawn is bruised too
                }
                if (!p.Dead && p.Spawned)
                {
                    p.stances?.stagger?.StaggerFor(StaggerTicks);
                }
                Dev.PZStats.landings++;
            }
            landings.Clear();
        }

        // Vanilla bruises: two blunt hits low on the body, no armour penetration, no attacker (Q6: the same for all).
        private static void Hurt(Pawn p, int fallen)
        {
            for (int i = 0; i < 2 && !p.Dead; i++)
            {
                DamageInfo dinfo = new DamageInfo(DamageDefOf.Blunt, DamagePerFloor * fallen, 0f);
                dinfo.SetBodyRegion(BodyPartHeight.Bottom, BodyPartDepth.Outside);
                p.TakeDamage(dinfo);
            }
        }
    }

    // One step of a path that is not a walk to a neighbouring cell: a stair step (H <-> E) or a jump (c -> landing).
    public enum StepKind
    {
        None,
        StairUp,
        StairDown,
        Jump
    }

    // The single place that says what a pawn's current step is, and how it is drawn (rise and horizontal position).
    public static class Links
    {
        public static StepKind Kind(Pawn p, out int fallen)
        {
            fallen = 0;
            Pawn_PathFollower pather = p.pather;
            if (pather == null || !pather.Moving || !p.Spawned)
            {
                return StepKind.None;
            }
            IntVec3 pos = p.Position;
            IntVec3 next = pather.nextCell;
            int dz = next.z - pos.z;
            int dx = next.x - pos.x;
            if (dz >= -1 && dz <= 1 && dx >= -1 && dx <= 1)
            {
                return StepKind.None;   // a normal step (cheap: no lookups for nearly every pawn)
            }
            Map map = p.Map;
            if (ProjectZ.Annex(map) == null)
            {
                return StepKind.None;
            }
            StairGrid g = StairGrid.For(map);
            if (g != null && g.IsStep(pos, next))
            {
                return ProjectZ.FloorOf(next, map) > ProjectZ.FloorOf(pos, map) ? StepKind.StairUp : StepKind.StairDown;
            }
            fallen = JumpGrid.JumpStep(pos, next, map);
            return fallen > 0 ? StepKind.Jump : StepKind.None;
        }

        public static StepKind Kind(Pawn p) => Kind(p, out int _);

        // How high a ramp cell lifts a pawn standing on it (cells over its floor): the middle of its third.
        public static float RampHeight(IntVec3 c, Map map)
        {
            if (!c.IsValid || !c.InBounds(map) || !(c.GetEdifice(map) is Building_Stairs s))
            {
                return 0f;
            }
            return (StairShape.Index(c, s.Position, s.Rotation) + 0.5f) / 3f * IsoRimBridge.StoreyHeight;
        }

        // The pawn's height over the floor of the cell it stands on. On and next to a ramp it follows where the pawn is
        // DRAWN (the tweened position), projected onto the ramp's axis (RampRise): rising smoothly along L0 -> L1 -> H and
        // on over the step onto the exit, the same function going down; during a jump it falls.
        public static float Rise(Pawn p)
        {
            if (!p.Spawned || ProjectZ.Annex(p.Map) == null)
            {
                return 0f;
            }
            Map map = p.Map;
            StepKind kind = Kind(p, out int fallen);
            float storey = IsoRimBridge.StoreyHeight;
            if (kind == StepKind.Jump)
            {
                float pct = p.pather != null ? Mathf.Clamp01(p.pather.MovePercentage) : 0f;
                return -fallen * storey * pct * pct;
            }
            Building_Stairs s = p.Position.GetEdifice(map) as Building_Stairs;
            if (s == null && p.pather != null && p.pather.Moving && p.pather.nextCell.IsValid && p.pather.nextCell.InBounds(map))
            {
                s = p.pather.nextCell.GetEdifice(map) as Building_Stairs;
            }
            if (s == null)
            {
                return 0f;
            }
            Vector3 drawn = p.Drawer?.tweener != null ? p.Drawer.tweener.TweenedPos : p.Position.ToVector3Shifted();
            return RampRise(s, drawn, map) - (ProjectZ.FloorOf(p.Position, map) - ProjectZ.FloorOf(s.Position, map)) * storey;
        }

        // The height over the stairs' floor at a drawn world position (on any floor's rows): 0 at the low end's outer edge
        // to a storey at the high end's outer edge, linear along the ramp's axis and flat past its ends; it fades out in
        // the half cell beside the ramp (a pawn stepping on or off sideways).
        public static float RampRise(Building_Stairs s, Vector3 drawn, Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            float gz = a == null ? drawn.z : Levels.GroundZLoose(map, drawn.z);
            Vector3 centre = ProjectZ.GroundCellOf(s.Position, map).ToVector3Shifted();
            float dx = drawn.x - centre.x;
            float dz = gz - centre.z;
            IntVec3 f = s.Rotation.FacingCell;
            float along = dx * f.x + dz * f.z;
            float side = Mathf.Abs(dx * f.z - dz * f.x);
            float h = Mathf.Clamp01((along + 1.5f) / 3f) * IsoRimBridge.StoreyHeight;
            return h * Mathf.Clamp01((1f - side) / 0.5f);
        }

        // Where a pawn is drawn across the floor during a stair step or jump: from its cell toward the next one's column,
        // on its own floor's rows (the rise is added by ThingOffset).
        public static bool StepTween(Pawn p, out Vector3 at)
        {
            at = Vector3.zero;
            if (Kind(p) == StepKind.None)
            {
                return false;
            }
            Map map = p.Map;
            IntVec3 pos = p.Position;
            IntVec3 to = ProjectZ.CellOnFloor(ProjectZ.GroundCellOf(p.pather.nextCell, map), ProjectZ.FloorOf(pos, map), map);
            if (!to.IsValid)
            {
                to = pos;
            }
            at = Vector3.Lerp(pos.ToVector3Shifted(), to.ToVector3Shifted(), Mathf.Clamp01(p.pather.MovePercentage));
            return true;
        }
    }

    // Compatibility for older callers (tests): +1 going up the stairs, -1 down, 0 otherwise.
    public static class Climb
    {
        public const int StepTicks = 30;

        public static int Direction(Pawn p)
        {
            StepKind k = Links.Kind(p);
            return k == StepKind.StairUp ? 1 : k == StepKind.StairDown ? -1 : 0;
        }
    }
}
