using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ProjectZ
{
    // The roofs of a gravship with upper decks (the 3D hull, isorim-pz-hull3d-design.md rules 4 and 5). Vanilla takes the
    // roof of every ship cell along and puts it back at the landing; an upper deck's own floor roof on the cell under it
    // comes and goes with the deck (FloorRoof). What is left is decided here, in one place:
    //   - Take (take-off, while the Gravship is captured): the roofs on the open cells over the ship fly with it. From
    //     every ship cell the run of open cells above it is walked, up to the next floor or the top (the opening of ship
    //     stairs, a hole in a deck, the air of a tall hall: ShipLink.OpenRunUp), and every roof found on them is taken:
    //     recorded like the ship cells' roofs (floor-tagged, ShipLocal) and removed as vanilla removes those. So a room
    //     round a stairwell or over a hole lands closed and no roof is left hanging over the old site. As the game takes
    //     the roof of every ship cell whoever holds it, so here: also the roof of a tall hall the ship stands in (the
    //     ship's height counts it, ShipLanding.Height). A run that ends under a floor that stays behind (a deck out of
    //     the ship set) is left whole: that floor's ceiling and the hidden roofs under its cover stay, the old site keeps
    //     its atrium. A room made only of such open cells (the air of a hall) has no ship cell, so its temperature and
    //     vacuum are recorded here too.
    //   - Left (after the take-off removal, which vanilla does without events): under a deck that left, a floor's roof
    //     with no floor over it now is removed, so non-ship ground under an overhang or inside a hollow hull is under open
    //     sky again (the landing lays it again with the deck, FloorRoof.Changed). A floor left behind right over a cell
    //     that left gets its floor's roof back, and the game's own check then says whether anything still holds it. A
    //     cell that left under a column that is still covered (a run left whole, above) gets the hidden roof the column
    //     rule gives any such cell.
    //   - Hidden (landing, after vanilla's SpawnRoofs): the hidden roof of an atrium cell (PZ_StairwellRoof) is only kept
    //     under a covered column (Stairwell.RoofFor). The game writes the roofs in the order they were recorded, the ship
    //     cells' first, so such a cell is written before its cover and gets a built roof instead. Once all roofs are
    //     there the ship's hidden roofs are written again, top floor first.
    public static class ShipRoofs
    {
        private static readonly AccessTools.FieldRef<Gravship, Dictionary<IntVec3, RoofDef>> shipRoofs = AccessTools.FieldRefAccess<Gravship, Dictionary<IntVec3, RoofDef>>("roofs");
        private static readonly AccessTools.FieldRef<Gravship, Dictionary<IntVec3, TerrainDef>> shipFoundations = AccessTools.FieldRefAccess<Gravship, Dictionary<IntVec3, TerrainDef>>("foundations");
        private static readonly AccessTools.FieldRef<Gravship, List<RoomTemperatureVacuum>> shipRooms = AccessTools.FieldRefAccess<Gravship, List<RoomTemperatureVacuum>>("roomTemperatures");

        // (tests) the last flight: open-cell roofs taken along; floor roofs removed under decks that left; floor roofs put
        // back under floors left behind; cells that left under a covered column given the hidden roof; hidden roofs
        // written again after the landing's roofs
        public static int Taken, Opened, Restored, Covered, Again;

        private static readonly List<IntVec3> tmpCells = new List<IntVec3>();
        private static readonly List<IntVec3> tmpRun = new List<IntVec3>();

        // The highest floor the ship brings a roof to (0: none above the ground).
        public static int TopFloor(Gravship ship)
        {
            int top = 0;
            Dictionary<IntVec3, RoofDef> roofs = ship == null ? null : shipRoofs(ship);
            if (roofs != null)
            {
                foreach (KeyValuePair<IntVec3, RoofDef> kv in roofs)
                {
                    if (kv.Value != null && kv.Key.y > top)
                    {
                        top = kv.Key.y;
                    }
                }
            }
            return top;
        }

        public static void Take(Gravship ship, Building_GravEngine engine, Map map)
        {
            Taken = 0;
            Dictionary<IntVec3, RoofDef> roofs = shipRoofs(ship);
            // (not without the floor-tagged offsets, ShipLocal.Ready: a ship with no deck upstairs still launches then,
            // and a roof recorded upstairs could not be placed)
            if (roofs == null || ProjectZ.Annex(map) == null || !ShipLocal.Ready)
            {
                return;
            }
            HashSet<IntVec3> valid = engine.ValidSubstructure;
            tmpCells.Clear();
            foreach (IntVec3 c in valid)
            {
                IntVec3 end = ShipLink.OpenRunUp(c, map, tmpRun);
                // (a run under a floor that stays behind: its ceiling and the hidden roofs under its cover stay)
                if (tmpRun.Count == 0 || end.IsValid && ProjectZ.HasFloor(end, map) && !valid.Contains(end))
                {
                    continue;
                }
                for (int i = 0; i < tmpRun.Count; i++)
                {
                    if (map.roofGrid.RoofAt(tmpRun[i]) != null)
                    {
                        tmpCells.Add(tmpRun[i]);
                    }
                }
            }
            tmpRun.Clear();
            if (tmpCells.Count == 0)
            {
                return;
            }
            // Rooms made only of these open cells (before the roofs go: a room's counts are kept from its roofs).
            // Copied from RimWorld.Planet.Gravship.CopyCellContents (the room record: once per room that is not at outdoor
            // temperature, its cell, temperature and vacuum); that method can't be asked for these cells alone, it records
            // everything of a ship cell. Watched: CopyWatches.
            List<RoomTemperatureVacuum> rooms = shipRooms(ship);
            if (rooms != null)
            {
                HashSet<Room> known = new HashSet<Room>();
                foreach (IntVec3 c in valid)
                {
                    Room r = c.GetRoom(map);
                    if (r != null)
                    {
                        known.Add(r);
                    }
                }
                foreach (IntVec3 up in tmpCells)
                {
                    Room r = up.GetRoom(map);
                    if (r != null && known.Add(r) && !r.UsesOutdoorTemperature)
                    {
                        rooms.Add(new RoomTemperatureVacuum { roomCell = ShipLocal.From(up, ship.originalPosition, map), temperature = r.Temperature, vacuum = r.Vacuum });
                    }
                }
            }
            foreach (IntVec3 up in tmpCells)
            {
                RoofDef r = map.roofGrid.RoofAt(up);
                roofs[ShipLocal.From(up, ship.originalPosition, map)] = r;
                // (as vanilla takes the ship cells' roofs: directly, so the floor-roof rule, which keeps the roof under a
                // deck over the open cell, doesn't hold it back; that deck flies too)
                map.roofGrid.RemoveRoofUnsafe(map.cellIndices.CellToIndex(up));
                // (the room the cell is in is told, as the game's SetRoof tells it: an open cell's room may be a hall that
                // stays, whose roof counts must be right at the old site; the ship's own rooms are rebuilt anyway)
                map.regionGrid.GetValidRegionAt_NoRebuild(up)?.District?.Notify_RoofChanged();
                map.mapDrawer.MapMeshDirty(up, MapMeshFlagDefOf.Roofs);
                map.glowGrid.DirtyCell(up);
                Taken++;
                ProjectZ.DebugLog($"open cell {up} over the ship: its roof {r.defName} flies with the ship");
            }
            tmpCells.Clear();
        }

        public static void Left(Gravship ship, Map map)
        {
            Opened = Restored = Covered = 0;
            if (ship == null || map == null || ProjectZ.Annex(map) == null)
            {
                return;
            }
            Dictionary<IntVec3, TerrainDef> foundations = shipFoundations(ship);
            if (foundations == null)
            {
                return;
            }
            // (the keys as recorded: the ground column relative to the engine's, the floor in y; ShipLocal)
            IntVec3 origin = ProjectZ.GroundCellOf(ship.originalPosition, map);
            if (!origin.IsValid)
            {
                return;
            }
            tmpCells.Clear();
            foreach (IntVec3 key in foundations.Keys)
            {
                IntVec3 c = ShipLocal.To(origin, key, map);
                if (!c.InBounds(map))
                {
                    continue;
                }
                if (FloorRoof.FloorAbove(c, map))
                {
                    // a floor left behind right over a cell that left: its floor's roof back (the SetRoof rule writes it)
                    map.roofGrid.SetRoof(c, null);
                    tmpCells.Add(c);
                    Restored++;
                }
                else if (!map.roofGrid.Roofed(c) && Atrium.Covered(c, map))
                {
                    // a cell that left under a column still covered (an open run left whole under a floor that stays):
                    // no roof under a covered column is the atrium's (the SetRoof rule writes it)
                    map.roofGrid.SetRoof(c, null);
                    Covered++;
                }
                if (key.y >= 1)
                {
                    IntVec3 b = FloorRoof.Below(c, map);
                    if (b.IsValid && map.roofGrid.RoofAt(b) == PZDefOf.PZ_FloorRoof && !FloorRoof.FloorAbove(b, map))
                    {
                        // under a deck that left: open sky (the column rule decides, should anything else cover it)
                        map.roofGrid.SetRoof(b, null);
                        Opened++;
                    }
                }
            }
            if (tmpCells.Count > 0)
            {
                // the game's own check, as it ran for the map's other roofs during the take-off: a roof nothing holds
                // goes, and the floor over it falls (SlabCollapse)
                RoofCollapseCellsFinder.RemoveBulkCollapsingRoofs(tmpCells, map);
                tmpCells.Clear();
            }
            if (Opened + Restored + Covered > 0)
            {
                ProjectZ.DebugLog($"take-off from {map}: {Opened} floor roof(s) under decks that left removed, {Restored} put back under floors left behind, {Covered} cell(s) under a covered column given the hidden roof");
            }
        }

        public static void Hidden(Gravship ship, Map map, IntVec3 root)
        {
            Again = 0;
            if (ship == null || map == null || ProjectZ.Annex(map) == null || PZDefOf.PZ_StairwellRoof == null)
            {
                return;
            }
            tmpCells.Clear();
            foreach ((IntVec3 local, RoofDef def) in ship.Roofs)
            {
                if (def == PZDefOf.PZ_StairwellRoof)
                {
                    IntVec3 c = ShipLocal.To(root, local, map);
                    if (c.InBounds(map))
                    {
                        tmpCells.Add(c);
                    }
                }
            }
            foreach (IntVec3 c in tmpCells.OrderByDescending(c => ProjectZ.FloorOf(c, map)))
            {
                if (map.roofGrid.RoofAt(c) != PZDefOf.PZ_StairwellRoof)
                {
                    map.roofGrid.SetRoof(c, PZDefOf.PZ_StairwellRoof);   // (kept only where the column rule allows it)
                    if (map.roofGrid.RoofAt(c) == PZDefOf.PZ_StairwellRoof)
                    {
                        Again++;
                    }
                }
            }
            tmpCells.Clear();
            if (Again > 0)
            {
                ProjectZ.DebugLog($"landing on {map}: {Again} hidden roof(s) of the ship written again under their cover");
            }
        }
    }

    [HarmonyPatch(typeof(GravshipPlacementUtility), "SpawnRoofs")]
    public static class SpawnRoofs_ShipRoofs_Patch
    {
        public static void Postfix(Gravship gravship, Map map, IntVec3 root)
        {
            ShipRoofs.Hidden(gravship, map, root);
        }
    }
}
