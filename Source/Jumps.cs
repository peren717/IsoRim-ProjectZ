using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // Falls and landings between floors (design §3). Voluntary jumping down was taken out of pathing and reachability
    // (user, 2026-10-19: floors are joined by stairs only, isorim-pz-pathing-redesign.md). What stays here: the landing of
    // everything that drops between floors (holes, slab collapses, gravity, shafts: Landed and the one hurt rule Hurts) and
    // the geometry of a fall off an upper-floor edge (LandingFrom: tests and fixtures only, never a path). The class keeps
    // its name: saves list it (a pawn saved in the middle of an old jump starts again where it stands, PathPatches).
    public class JumpGrid : MapComponent
    {
        public const float DamagePerFloor = 3f;   // Blunt, twice (vanilla bruises), from HurtFrom floors down
        public const int HurtFrom = 2;            // a drop of one floor only staggers (user 2026-10-19)
        public const int StaggerTicks = 60;

        private readonly List<KeyValuePair<Pawn, int>> landings = new List<KeyValuePair<Pawn, int>>();

        public JumpGrid(Map map) : base(map)
        {
        }

        private static Map lastMap;
        private static JumpGrid last;

        // The landing component of a map with floors, else null (last map cached).
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

        // A roof or floor changed in a column: where a fall through an open hole lands may have changed, which the reveal
        // rule's hole links read (ReachFog). Pathing does not mind (no jumps).
        public static void ColumnChanged(IntVec3 c, Map map)
        {
            if (For(map) == null)
            {
                return;
            }
            IntVec3 up = StairShape.Above(c, map);
            if (up.IsValid && up.InBounds(map) && up.GetTerrain(map) == PZDefOf.PZ_OpenAir)
            {
                ReachFog.Request(map);
            }
        }

        // Where something stepping off c over its open-air neighbour in direction dir would land, and how many floors it
        // would fall; false where it can't. Geometry only (tests, fixtures): no pawn ever jumps down on its own.
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
                if (!cj.Walkable(map) || !Column.OpenAbove(cj, map))
                {
                    return false;   // no landing on a roof or on something impassable
                }
                land = cj;
                fallen = k - j;
                return true;
            }
            return false;
        }

        // A pawn just landed (every drop between floors comes here: falls through holes and shafts, gravity, slab
        // collapses): staggered on this map's next tick, and bruised from HurtFrom floors down.
        public void Landed(Pawn p, int fallen)
        {
            landings.Add(new KeyValuePair<Pawn, int>(p, fallen));
        }

        // The one landing rule: a drop of one floor does no harm (it still staggers).
        public static bool Hurts(int fallen) => fallen >= HurtFrom;

        // counters (tests): landings that staggered, landings that hurt
        public static int Staggers, HurtLandings;

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
                if (Hurts(fallen))
                {
                    HurtLandings++;
                    Hurt(p, fallen);
                    if (carried != null && !carried.Dead && !carried.Destroyed)
                    {
                        Hurt(carried, fallen);   // Q4: the carried pawn is bruised too
                    }
                }
                if (!p.Dead && p.Spawned)
                {
                    p.stances?.stagger?.StaggerFor(StaggerTicks);
                    Staggers++;
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

    // One step of a path that is not a walk to a neighbouring cell: a stair step (H <-> E).
    public enum StepKind
    {
        None,
        StairUp,
        StairDown
    }

    // The single place that says what a pawn's current step is, and how it is drawn (rise and horizontal position).
    public static class Links
    {
        public static StepKind Kind(Pawn p)
        {
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
            return StepKind.None;
        }

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
        // on over the step onto the exit, the same function going down.
        public static float Rise(Pawn p)
        {
            if (!p.Spawned || ProjectZ.Annex(p.Map) == null)
            {
                return 0f;
            }
            Map map = p.Map;
            float storey = IsoRimBridge.StoreyHeight;
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

        // Where a pawn is drawn across the floor during a stair step: from its cell toward the next one's column, on its
        // own floor's rows (the rise is added by ThingOffset).
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

    // The stair step's time (path cost = time: the step costs StepTicks in the router, Patches/Router.cs) and, for older
    // callers (tests), the direction of a pawn's current step: +1 up, -1 down, 0 otherwise.
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
