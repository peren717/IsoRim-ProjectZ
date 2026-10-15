using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ
{
    // A stair link: the stair's own cell (bottom, floor k) and the cell right above it on floor k + 1 (top). Pawns step
    // from one to the other as one (slow) step of their path (Patches/PathPatches.cs); reachability treats the two
    // cells' regions as linked (Patches/ReachPatches.cs). Built and removed with the stairs; nothing is saved (the
    // stairs register again when the map loads).
    public struct StairLink
    {
        public IntVec3 bottom;
        public IntVec3 top;
        public int floor;   // floor of the bottom cell
        public Building_Stairs stairs;
    }

    public class StairGrid : MapComponent
    {
        public readonly List<StairLink> links = new List<StairLink>();

        public StairGrid(Map map) : base(map)
        {
        }

        public int Count => links.Count;

        private static Map lastMap;
        private static StairGrid last;

        // The stairs of a map, or null when it has none (cheap: last map cached).
        public static StairGrid For(Map map)
        {
            if (map == null)
            {
                return null;
            }
            StairGrid g;
            if (ReferenceEquals(map, lastMap))
            {
                g = last;
            }
            else
            {
                g = map.components == null ? null : map.GetComponent<StairGrid>();
                lastMap = map;
                last = g;
            }
            return g != null && g.links.Count > 0 ? g : null;
        }

        public void Register(Building_Stairs s, IntVec3 top)
        {
            Deregister(s, false);
            links.Add(new StairLink { bottom = s.Position, top = top, floor = ProjectZ.FloorOf(s.Position, map), stairs = s });
            Changed();
        }

        public void Deregister(Building_Stairs s, bool notify = true)
        {
            int removed = links.RemoveAll(l => l.stairs == s);
            if (removed > 0 && notify)
            {
                Changed();
            }
        }

        private void Changed()
        {
            map.reachability?.ClearCache();
            ProjectZ.DebugLog($"stairs on {map}: {links.Count} link(s)");
        }

        // True when a step from 'from' to 'to' is a stair step (either way).
        public bool IsStep(IntVec3 from, IntVec3 to)
        {
            for (int i = 0; i < links.Count; i++)
            {
                StairLink l = links[i];
                if (l.bottom == from && l.top == to || l.top == from && l.bottom == to)
                {
                    return true;
                }
            }
            return false;
        }

        // A link is usable when both ends can be stood on.
        public bool Usable(StairLink l)
        {
            return l.stairs != null && l.stairs.Spawned && l.top.InBounds(map) && l.top.Walkable(map) && l.bottom.Walkable(map);
        }
    }

    // A pawn is on a stair step when it stands on one end and its next cell is the other end.
    public static class Climb
    {
        public const int StepTicks = 90;

        // +1 going up, -1 going down, 0 not on a stair step. Cheap for every pawn (one row compare first).
        public static int Direction(Pawn p)
        {
            Pawn_PathFollower pather = p.pather;
            if (pather == null || !pather.Moving || !p.Spawned)
            {
                return 0;
            }
            IntVec3 pos = p.Position;
            IntVec3 next = pather.nextCell;
            int dz = next.z - pos.z;
            if (dz >= -1 && dz <= 1)
            {
                return 0;
            }
            StairGrid g = StairGrid.For(p.Map);
            if (g == null || !g.IsStep(pos, next))
            {
                return 0;
            }
            return dz > 0 ? 1 : -1;
        }
    }

    public class Building_Stairs : Building
    {
        // The cell above the stairs on the next floor up (Invalid without one).
        public IntVec3 TopCell
        {
            get
            {
                int k = ProjectZ.FloorOf(Position, Map);
                IntVec3 g = ProjectZ.GroundCellOf(Position, Map);
                return k < 0 || !g.IsValid ? IntVec3.Invalid : ProjectZ.CellOnFloor(g, k + 1, Map);
            }
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            IntVec3 top = TopCell;
            if (!top.IsValid)
            {
                return;
            }
            if (!respawningAfterLoad && top.GetTerrain(map) == PZDefOf.PZ_OpenAir)
            {
                // The landing: the floor cell above the stairs, made with them.
                map.terrainGrid.SetTerrain(top, PZDefOf.PZ_UpperFloor);
            }
            map.GetComponent<StairGrid>()?.Register(this, top);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map?.GetComponent<StairGrid>()?.Deregister(this);
            base.DeSpawn(mode);
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            IntVec3 top = TopCell;
            if (top.IsValid)
            {
                string line = "PZ_StairsLeadTo".Translate(ProjectZ.FloorOf(top, Map) + 1);
                s = s.NullOrEmpty() ? line : s + "\n" + line;
            }
            return s;
        }
    }

    // Stairs need a floor above them, over this roofed cell (decision D6: an upper floor needs the roof below it).
    public class PlaceWorker_Stairs : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            int k = ProjectZ.FloorOf(loc, map);
            if (a == null || k < 0 || k + 1 >= a.floors)
            {
                return "PZ_NoFloorAbove".Translate();
            }
            if (!map.roofGrid.Roofed(loc))
            {
                return "PZ_NeedsRoofHere".Translate();
            }
            IntVec3 top = ProjectZ.CellOnFloor(ProjectZ.GroundCellOf(loc, map), k + 1, map);
            if (!top.IsValid)
            {
                return "PZ_NoFloorAbove".Translate();
            }
            TerrainDef t = top.GetTerrain(map);
            Building e = top.GetEdifice(map);
            bool terrainOk = t == PZDefOf.PZ_OpenAir || t.passability == Traversability.Standable;
            if (!terrainOk || e != null && e.def.passability == Traversability.Impassable)
            {
                return "PZ_LandingBlocked".Translate();
            }
            return AcceptanceReport.WasAccepted;
        }
    }

    // An upper floor cell needs the cell right below it roofed.
    public class PlaceWorker_UpperFloor : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            if (!SupportedFloorCell(loc, map))
            {
                return ProjectZ.FloorOf(loc, map) >= 1 ? "PZ_NeedsRoofBelow".Translate() : "PZ_OnlyUpperFloors".Translate();
            }
            return AcceptanceReport.WasAccepted;
        }

        public static bool SupportedFloorCell(IntVec3 loc, Map map)
        {
            int k = ProjectZ.FloorOf(loc, map);
            if (k < 1)
            {
                return false;
            }
            IntVec3 below = ProjectZ.CellOnFloor(ProjectZ.GroundCellOf(loc, map), k - 1, map);
            return below.IsValid && map.roofGrid.Roofed(below);
        }
    }
}
