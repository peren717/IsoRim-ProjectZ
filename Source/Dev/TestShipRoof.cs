using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ProjectZ.Dev
{
    // Test mode shipstairs with -projectzshiproof (user bug 2026-10-17: with the roof display on, the roof over ship stairs
    // in a roofed ship room looked like an ordinary roof): stairs in roofed rooms, the roof built by colonists before and
    // after the stairs, ship stairs and ordinary stairs side by side.
    //   The fresh shipfly ship (one storey, nothing upstairs), its roof taken off; west of it two wooden rooms on the ground.
    //   R1 roof first: colonists build the roof over the ship's west half (Build roof area) and over room 1 (walls placed:
    //      the game's own auto-roof area); ship stairs B (ship's east half) and ordinary stairs D (where room 2 will stand)
    //      are built meanwhile under open sky.
    //   R2 then: room 2's walls go up round stairs D (auto-roof area), the ship's east half gets a Build roof area; ship
    //      stairs A (roofed west half) and ordinary stairs C (roofed room 1) are built; colonists roof everything.
    //   R3 every stairs alike: the ramp's cells carry the stairwell roof, the column above is open, the landing is there
    //      (a ship upper deck or a slab) with the floor's roof under it; shots with the roof display on and off.
    //   R4 the user's ship: ship upper decks and hull walls on floor 2 round stairs A's opening; colonists (up stairs A) roof
    //      that room, the opening too: it is part of the ship (ShipStairs.Opening, IsoRim draws its roof as the ship's).
    //   R5 the ship flies to a neighbouring tile: the roof over the opening flies with it, the floor-2 room lands closed.
    // With mode save and -projectzsaveroof (-projectzsave=<name>): a player's save, the roofs round its stairs, shots.
    public static partial class TestRunner
    {
        private static readonly List<IntVec3> srRoofArea = new List<IntVec3>();
        private static int srRoofTick;

        private static void SrSetup(Map map, Building_GravEngine e, List<Pawn> cols)
        {
            while (cols.Count < 5)
            {
                Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(p, SP(-5, -4), map);
                cols.Add(p);
            }
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], SP(-4 - i, -4));
                Draft(cols[i], true);
            }
            // the ship's roof off (the player roofs it), no roof areas yet
            for (int x = 1; x <= 11; x++)
            {
                for (int z = 1; z <= 9; z++)
                {
                    map.roofGrid.SetRoof(SP(x, z), null);
                }
            }
            foreach (IntVec3 c in map.areaManager.BuildRoof.ActiveCells.ToList())
            {
                map.areaManager.BuildRoof[c] = false;
            }
            // room 1 (walls x -11..-7, z 0..6, door south)
            SrRoom(map, -11, -7, -10);
            srRoofArea.Clear();
            for (int x = 1; x <= 6; x++)
            {
                for (int z = 1; z <= 9; z++)
                {
                    srRoofArea.Add(SP(x, z));
                }
            }
            for (int x = -10; x <= -8; x++)
            {
                for (int z = 1; z <= 5; z++)
                {
                    srRoofArea.Add(SP(x, z));
                }
            }
            foreach (IntVec3 c in srRoofArea)
            {
                map.areaManager.BuildRoof[c] = true;
            }
            foreach ((ThingDef def, int n, IntVec3 at) in new[] { (ThingDefOf.Steel, 75, SP(-2, -6)), (ThingDefOf.Steel, 75, SP(-3, -6)), (ThingDefOf.Steel, 75, SP(-4, -6)), (ThingDefOf.Steel, 75, SP(-5, -6)),
                (DefDatabase<ThingDef>.GetNamed("GravlitePanel"), 25, SP(-6, -6)), (DefDatabase<ThingDef>.GetNamed("GravlitePanel"), 25, SP(-7, -6)), (ThingDefOf.ComponentIndustrial, 12, SP(-8, -6)),
                (ThingDefOf.WoodLog, 75, SP(-9, -6)), (ThingDefOf.WoodLog, 75, SP(-10, -6)), (ThingDefOf.WoodLog, 75, SP(-11, -6)), (ThingDefOf.WoodLog, 75, SP(-12, -6)) })
            {
                Thing t = ThingMaker.MakeThing(def);
                t.stackCount = n;
                GenSpawn.Spawn(t, at, map);
            }
            SpExpect("R1 ship stairs B (ship's east half, open sky) accepted", PZDefOf.PZ_ShipStairs, null, SP(11, 2), Rot4.North, map, null);
            SpExpect("R1 ordinary stairs D (open sky, room 2 later) accepted", PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, SP(-5, 2), Rot4.North, map, null);
            GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_ShipStairs, SP(11, 2), map, Rot4.North, Faction.OfPlayer, null);
            GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, SP(-5, 2), map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
            e.ForceSubstructureDirty();
            SpWork(map, construct: true);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            TickUntil(60000, () =>
            {
                SpKeep(map);
                return SrStairs(map).Count >= 2 && srRoofArea.All(c => map.roofGrid.Roofed(c));
            }, "sp_r_phase2", 120);
        }

        private static void SrRoom(Map map, int x0, int x1, int doorX)
        {
            for (int x = x0; x <= x1; x++)
            {
                for (int z = 0; z <= 6; z++)
                {
                    bool ring = x == x0 || x == x1 || z == 0 || z == 6;
                    if (!ring || x0 > -11 && x == x0)
                    {
                        continue;   // (room 2 shares room 1's east wall)
                    }
                    if (x == doorX && z == 0)
                    {
                        Place(ThingDefOf.Door, ThingDefOf.WoodLog, SP(x, z), map);
                    }
                    else
                    {
                        Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SP(x, z), map);
                    }
                }
            }
        }

        private static List<Building_Stairs> SrStairs(Map map) =>
            StairDefs.All.SelectMany(d => map.listerThings.ThingsOfDef(d)).OfType<Building_Stairs>().Where(s => ProjectZ.FloorOf(s.Position, map) == 0).ToList();

        private static void StepShipRoof(Map map)
        {
            switch (stage)
            {
                case "sp_r_phase2":
                    SrPhase2(map);
                    return;
                case "sp_r_check":
                    SrCheck(map);
                    return;
                case "sp_r_room":
                    SrRoom2(map);
                    return;
                case "sp_r_roomed":
                    SrRoomed(map);
                    return;
                case "sp_r_launch":
                    SrLaunch(map);
                    return;
                case "sp_r_landed":
                    SrLanded();
                    return;
                case "sp_r_end":
                    Finish();
                    return;
            }
        }

        private static void SrPhase2(Map map)
        {
            tickWatch = null;
            Result("R1 colonists built ship stairs B and stairs D under open sky, and the roof over the ship's west half and room 1",
                SrStairs(map).Count == 2 && srRoofArea.All(c => map.roofGrid.Roofed(c)), $"stairs {SrStairs(map).Count}, roofed {srRoofArea.Count(c => map.roofGrid.Roofed(c))} of {srRoofArea.Count} after {ticksTotal} ticks");
            SrDump(map, "R1");
            // room 2 round stairs D (the game's auto-roof area), the ship's east half by the Build roof area
            SrRoom(map, -7, -3, -4);
            List<IntVec3> area2 = new List<IntVec3>();
            for (int x = 7; x <= 11; x++)
            {
                for (int z = 1; z <= 9; z++)
                {
                    area2.Add(SP(x, z));
                }
            }
            foreach (IntVec3 c in area2)
            {
                map.areaManager.BuildRoof[c] = true;
            }
            srRoofArea.Clear();
            srRoofArea.AddRange(area2);
            for (int x = -6; x <= -4; x++)
            {
                for (int z = 1; z <= 5; z++)
                {
                    srRoofArea.Add(SP(x, z));
                }
            }
            SpExpect("R2 ship stairs A (roofed west half) accepted", PZDefOf.PZ_ShipStairs, null, SP(1, 2), Rot4.North, map, null);
            SpExpect("R2 ordinary stairs C (roofed room 1) accepted", PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, SP(-9, 2), Rot4.North, map, null);
            GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_ShipStairs, SP(1, 2), map, Rot4.North, Faction.OfPlayer, null);
            GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, SP(-9, 2), map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
            srRoofTick = 0;
            TickUntil(60000, () =>
            {
                SpKeep(map);
                srRoofTick += 120;
                return SrStairs(map).Count >= 4 && (srRoofArea.All(c => map.roofGrid.Roofed(c)) || srRoofTick > 40000);
            }, "sp_r_check", 120);
        }

        // Mode save with -projectzsaveroof: a player's save as loaded, the roofs and terrain round every stairs on its floor
        // and the floor above, and shots with the roof display on and off (no ticks).
        private static void SrSave(Map map)
        {
            foreach (Building_Stairs s in StairDefs.All.SelectMany(d => map.listerThings.ThingsOfDef(d)).OfType<Building_Stairs>())
            {
                Info($"{s.def.defName} at {SpCell(s.Position, map)} {s.Rotation}, exit {SpCell(s.TopCell, map)}");
                CellRect r = s.OccupiedRect().ExpandedBy(1);
                foreach (int k in new[] { 0, 1 })
                {
                    for (int z = r.maxZ + 1; z >= r.minZ; z--)
                    {
                        string row = $"  f{ProjectZ.FloorOf(s.Position, map) + 1 + k} z{z}:";
                        for (int x = r.minX; x <= r.maxX; x++)
                        {
                            IntVec3 c = new IntVec3(x, 0, z);
                            if (k == 1)
                            {
                                c = StairShape.Above(c, map);
                            }
                            row += $" [{map.roofGrid.RoofAt(c)?.defName ?? "-"}|{map.terrainGrid.FoundationAt(c)?.defName ?? c.GetTerrain(map)?.defName}|{c.GetEdifice(map)?.def.defName ?? ""}]";
                        }
                        Info(row);
                    }
                }
                IntVec3 g = ProjectZ.GroundCellOf(s.Position, map);
                int f = ProjectZ.FloorOf(s.Position, map);
                foreach (int k in new[] { f, f + 1, f + 2 })
                {
                    GlShot(k, g, 9f, $"save_f{k + 1}_roofs");
                    glRoofShots.Add($"save_f{k + 1}_roofs");
                    GlShot(k, g, 9f, $"save_f{k + 1}_noroofs");
                }
            }
            glAfter = "sp_r_end";
            Wait(2, "gl_shots");
        }

        private static void SrDump(Map map, string when)
        {
            foreach (Building_Stairs s in SrStairs(map))
            {
                IntVec3 top = s.TopCell;
                IntVec3 below = FloorRoof.Below(top, map);
                string ramp = string.Join(", ", s.OccupiedRect().Cells.Select(c => map.roofGrid.RoofAt(c)?.defName ?? "none"));
                string above = string.Join(", ", s.OccupiedRect().Cells.Select(c => StairShape.Above(c, map).GetTerrain(map)?.defName ?? "?"));
                Info($"{when} {s.def.defName} at {SpCell(s.Position, map)}: ramp roofs [{ramp}], above [{above}], exit {SpCell(top, map)} terrain {top.GetTerrain(map)?.defName} foundation {map.terrainGrid.FoundationAt(top)?.defName ?? "none"}, under the exit {map.roofGrid.RoofAt(below)?.defName ?? "none"}");
            }
        }

        private static void SrCheck(Map map)
        {
            tickWatch = null;
            List<Building_Stairs> st = SrStairs(map);
            Result("R2 all four stairs built, the rest of the roof built", st.Count == 4, $"stairs {st.Count}, roofed {srRoofArea.Count(c => map.roofGrid.Roofed(c))} of {srRoofArea.Count} after {ticksTotal} ticks");
            SrDump(map, "R3");
            foreach (Building_Stairs s in st)
            {
                string name = $"{s.def.defName} at {SpCell(s.Position, map)}";
                int well = s.OccupiedRect().Cells.Count(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_StairwellRoof);
                int open = s.OccupiedRect().Cells.Count(c => StairShape.Above(c, map).GetTerrain(map) == PZDefOf.PZ_OpenAir);
                Result($"R3 {name}: stairwell roof over the ramp, the column above open", well == 3 && open == 3, $"stairwell roofs {well}/3, open above {open}/3");
                IntVec3 top = s.TopCell;
                IntVec3 below = FloorRoof.Below(top, map);
                bool landing = ShipStairs.Is(s.def) ? ShipDeck.At(top, map) : Slab.At(top, map);
                Result($"R3 {name}: the landing is there with the floor's roof under it", landing && top.Standable(map) && map.roofGrid.RoofAt(below) == PZDefOf.PZ_FloorRoof,
                    $"exit {SpCell(top, map)} {top.GetTerrain(map)?.defName}, under it {map.roofGrid.RoofAt(below)?.defName ?? "none"}");
                Room room = s.Position.GetRoom(map);
                Result($"R3 {name}: the room round the ramp is indoors", room != null && !room.PsychologicallyOutdoors && room.OpenRoofCount == 0, $"room {room?.ID} cells {room?.CellCount} open roof {room?.OpenRoofCount}");
            }
            foreach (int k in new[] { 0, 1 })
            {
                GlShot(k, SP(6, 4), 9f, $"sr_ship_f{k + 1}_roofs");
                glRoofShots.Add($"sr_ship_f{k + 1}_roofs");
                GlShot(k, SP(-7, 3), 8f, $"sr_rooms_f{k + 1}_roofs");
                glRoofShots.Add($"sr_rooms_f{k + 1}_roofs");
            }
            GlShot(0, SP(6, 4), 9f, "sr_ship_f1_noroofs");
            GlShot(0, SP(-7, 3), 8f, "sr_rooms_f1_noroofs");
            GlShot(1, SP(6, 4), 9f, "sr_ship_f2_noroofs");
            glAfter = "sp_r_room";
            Wait(2, "gl_shots");
        }

        private static readonly List<IntVec3> srHole = new List<IntVec3>();
        private static readonly List<IntVec3> srRoomOffs = new List<IntVec3>();
        private static readonly List<IntVec3> srHoleOffs = new List<IntVec3>();

        // R4 (the user's ship): ship upper decks over the west part, ship hull walls on them round stairs A's opening,
        // colonists (up stairs A) roof that floor-2 room, the opening with it.
        private static void SrRoom2(Map map)
        {
            srHole.Clear();
            srHole.AddRange(new[] { SPF(1, 1, 1, map), SPF(1, 2, 1, map), SPF(1, 3, 1, map) });
            for (int x = 0; x <= 5; x++)
            {
                for (int z = 0; z <= 6; z++)
                {
                    IntVec3 c = SPF(x, z, 1, map);
                    if (!srHole.Contains(c) && !ShipDeck.At(c, map))
                    {
                        map.terrainGrid.SetTerrain(c, PZDefOf.PZ_ShipDeck);
                    }
                }
            }
            for (int x = 0; x <= 5; x++)
            {
                for (int z = 0; z <= 6; z++)
                {
                    if (x == 0 || x == 5 || z == 0 || z == 6)
                    {
                        Place(DefDatabase<ThingDef>.GetNamed("GravshipHull"), ThingDefOf.Steel, SPF(x, z, 1, map), map);
                    }
                }
            }
            GlEngine(map).ForceSubstructureDirty();
            srRoofArea.Clear();
            for (int x = 1; x <= 4; x++)
            {
                for (int z = 1; z <= 5; z++)
                {
                    srRoofArea.Add(SPF(x, z, 1, map));
                }
            }
            foreach (IntVec3 c in srRoofArea)
            {
                map.areaManager.BuildRoof[c] = true;
            }
            Result("R4 the opening over stairs A is part of the ship (ShipStairs.Opening), the decks round it are not openings",
                srHole.All(c => ShipStairs.Opening(c, map)) && !ShipStairs.Opening(SPF(2, 2, 1, map), map) && !ShipStairs.Opening(SPF(1, 4, 1, map), map),
                string.Join(", ", srHole.Select(c => $"{SpCell(c, map)} {ShipStairs.Opening(c, map)}")));
            SpWork(map, construct: true);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            TickUntil(40000, () =>
            {
                SpKeep(map);
                return srRoofArea.All(c => map.roofGrid.Roofed(c));
            }, "sp_r_roomed", 120);
        }

        private static void SrRoomed(Map map)
        {
            tickWatch = null;
            Result("R4 colonists roofed the floor-2 room, its opening over stairs A too", srRoofArea.All(c => map.roofGrid.Roofed(c)),
                $"roofed {srRoofArea.Count(c => map.roofGrid.Roofed(c))} of {srRoofArea.Count} after {ticksTotal} ticks; opening roofs {string.Join(", ", srHole.Select(c => map.roofGrid.RoofAt(c)?.defName ?? "none"))}");
            SrDump(map, "R4");
            Building_Stairs a = SrStairs(map).FirstOrDefault(s => s.Position == SP(1, 2));
            Result("R4 stairs A still have the stairwell roof over the ramp and their landing", a != null && a.OccupiedRect().Cells.All(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_StairwellRoof) && ShipDeck.At(a.TopCell, map),
                a == null ? "no stairs A" : string.Join(", ", a.OccupiedRect().Cells.Select(c => map.roofGrid.RoofAt(c)?.defName ?? "none")));
            Room room = SPF(2, 2, 1, map).GetRoom(map);
            Result("R4 the floor-2 room is closed (no open roof)", room != null && room.OpenRoofCount == 0, $"room cells {room?.CellCount}, open roof {room?.OpenRoofCount}");
            foreach (int k in new[] { 0, 1, 2 })   // 0: the ship room under the deck, seen from the ground (no lid over it)
            {
                GlShot(k, SP(3, 4), 8f, $"sr_room_f{k + 1}_roofs");
                glRoofShots.Add($"sr_room_f{k + 1}_roofs");
            }
            GlShot(0, SP(3, 4), 8f, "sr_room_f1_noroofs");
            GlShot(1, SP(3, 4), 8f, "sr_room_f2_noroofs");
            glAfter = "sp_r_launch";
            Wait(2, "gl_shots");
        }

        // R5 the ship flies to a neighbouring tile: the floor-2 room's roof goes with it, the roof over the opening too.
        private static void SrLaunch(Map map)
        {
            Building_GravEngine e = GlEngine(map);
            List<Pawn> cols = Colonists(map);
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], SP(2 + i % 3, 8));
                Draft(cols[i], true);
            }
            srRoomOffs.Clear();
            srHoleOffs.Clear();
            foreach (IntVec3 c in srRoofArea)
            {
                (srHole.Contains(c) ? srHoleOffs : srRoomOffs).Add(ShipLocal.From(c, e.Position, map));
            }
            Info($"R5 launch: set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)}), openings in the set {srHole.Count(c => e.ValidSubstructure.Contains(c))} of {srHole.Count}");
            List<PlanetTile> near = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(map.Tile, near);
            PlanetTile dest = near.FirstOrDefault(t => Find.WorldObjects.MapParentAt(t) == null && !Find.WorldGrid[t].WaterCovered && Find.WorldGrid[t].hilliness != Hilliness.Impassable);
            if (!dest.Valid)
            {
                Result("R5 a neighbouring tile to fly to", false, "none");
                Finish();
                return;
            }
            sfNewTile = dest;
            sfLeg = 1;
            sfSaved = true;
            sfLandedStage = "sp_r_landed";
            SfLaunch(map, e, dest);
        }

        private static void SrLanded()
        {
            Map map = SfNew();
            Building_GravEngine e = map == null ? null : GlEngine(map);
            if (e == null)
            {
                Result("R5 landed", false, "no engine on " + map);
                Finish();
                return;
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            int room = srRoomOffs.Count(o => map.roofGrid.Roofed(SfAt(o, sfRoot, sfRot, map)));
            List<IntVec3> holes = srHoleOffs.Select(o => SfAt(o, sfRoot, sfRot, map)).ToList();
            int hole = holes.Count(c => map.roofGrid.Roofed(c));
            Result("R5 the floor-2 room's roof flew with the ship, over the opening too", room == srRoomOffs.Count && hole == srHoleOffs.Count,
                $"room roofs {room}/{srRoomOffs.Count}, opening roofs {hole}/{srHoleOffs.Count} ({string.Join(", ", holes.Select(c => $"{SpCell(c, map)} {map.roofGrid.RoofAt(c)?.defName ?? "none"} opening {ShipStairs.Opening(c, map)}"))})");
            Map old = Find.Maps.FirstOrDefault(m => m.Tile == sfHomeTile);
            if (old != null)
            {
                int left = srHole.Count(c => old.roofGrid.Roofed(c));
                Result("R5 no roof left hanging over the old ship's opening", left == 0, $"{left} of {srHole.Count} still roofed on {old}");
            }
            Building_Stairs a = SpShipStairs(map).FirstOrDefault(s => holes.Contains(StairShape.Above(s.Position, map)));
            if (a != null)
            {
                Room r = StairShape.Above(a.Position, map).GetRoom(map);
                Result("R5 the floor-2 room landed closed", r != null && r.OpenRoofCount == 0, $"open roof {r?.OpenRoofCount}");
                IntVec3 g = ProjectZ.GroundCellOf(a.Position, map);
                GlShot(1, g, 8f, "sr_landed_f2_roofs");
                glRoofShots.Add("sr_landed_f2_roofs");
                GlShot(1, g, 8f, "sr_landed_f2_noroofs");
            }
            glAfter = "sp_r_end";
            Wait(2, "gl_shots");
        }
    }
}
