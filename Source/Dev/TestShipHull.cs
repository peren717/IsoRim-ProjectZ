using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Test mode shiphull (3D connected gravship hull, issue 5; isorim-pz-hull3d-design.md sections A-F): one check per run,
    // -projectzhull=h1 ... h11, each on its own fixture (-projectzfloors=3, h7 and h11: 4; -projectzseed=7 -isorimseed=7; Odyssey).
    // Every fixture starts from TestGravLand.BuildShip (13 x 11 on the ground, engine at (6, 5), hull ring, grav anchor)
    // with ordinary stairs and a slab walkway west of it (as shipdeck). Real flows: the game's designators, colonists'
    // jobs, a real launch (console, tile picker, cutscene: TestShipFly.SfLaunch / SfFly), the move designator, a real
    // landing. Written against the design: on the stacked-column product (master 96e4e61) the new rules fail. Where the
    // old rule refuses a fixture step (a blueprint, a removal) the check FAILS and the step is then put in place directly
    // ("forced"), so the rest of the flow still runs there and shows what else the old rule does.
    //   H1 placement and building: an island deck and a deck beside an ordinary slab refused with PZ_DeckNeedsShipLink; a
    //      deck touching the ship's deck accepted; one rectangle drag (far corner first) lays a 5-deep overhang; a planned
    //      deck a colonist can already reach but that touches no built ship floor waits (same key); colonists build the
    //      overhang, no frame or deck ever without built ship floor beside it; it is part of the ship.
    //   H2 overhang flight: colonists build a 3 x 3 overhang; steel and an animal on the ground under it. Flight to a
    //      neighbouring tile: the old site has no roof there, steel and animal stayed; the Gravship holds the decks, no
    //      ground key. Landing turned East: refused over a roof and over a wall under the overhang (red cell), accepted
    //      clear and with a tree there (the landing clears trees under the shadow); decks placed, the floor's roof on the
    //      ground under them, the ground untouched but for the tree.
    //   H3 hollow hull: colonists take ground substructure up under the floor-2 deck; ring and hollow one indoor room; a
    //      colonist and steel inside are listed as left behind and stay; the hollow's roof goes with the deck and is back
    //      after the landing (where the game put the marker: the hollow's ground cleared with the landing area).
    //   H4 bridge: one drag plans a floor-2 bridge from the ship to above a second ground hull, colonists build it: one
    //      set (a joined cell outside the engine's range stays out, as vanilla); the cut sets at lowered limits stay
    //      linked, lower floors first where reachable; hull B's things and a colonist on it fly.
    //   H5 hole under a roof: a colonist takes a deck up in a roofed floor-2 room; flight; no roof left at the old site;
    //      the hole lands roofed over the atrium roof, both rooms indoors.
    //   H6 removal: a colonist removes the bridge's middle deck: hull B and the far part leave the set and stand; the only
    //      column near them deconstructed: they collapse by the game's roof rule.
    //   H7 tall hall and engine on an overhang: colonists build floor-3 decks over open floor 2 over the ship (linked down
    //      through the open cells); they uninstall the engine and install it on an overhang deck; the ship is found from
    //      there; flight; it lands as built.
    //   H8 orbit vacuum table (design E): one ship with a stacked room pair (ship stairs opening, a deck taken up under the
    //      ship's roof), a walled hall air volume, a room on an overhang, one with a hole in its deck, a hollow hull room
    //      and a room on the decks over it; flown to orbit; after 1250 ticks each room by Room.ExposedToSpace and Vacuum.
    //   H9 the engine's own block over the limit (review 2026-11-14; the shipset fixture, 575 cells for a limit of 500):
    //      B the engine on floor 2 over a ground cell that is substructure but cut off from the hull, A the engine on
    //      floor 3 with a hollow under a corner of its block: at every lowered limit from 40 the whole block is aboard and
    //      the set linked; then a real launch of A over the ship's own limit: the engine flies.
    //   H10 a one-storey ship under the roof of an ordinary building's upper storey (the roof on open cells over the ship):
    //      that roof flies as the roof of any ship cell does; the ship's height counts it: a map with no floor for it
    //      refuses the landing; it lands over the ship, the room it left has its roof count right.
    //   H11 a run of open cells under a deck that stays behind (the shipset fixture with 4 floors: a ship cell, two open
    //      cells with roofs, a deck past the limit over them, held by a wall that stays): the run's roofs stay at the old
    //      site, the cell that left under them gets the hidden roof, nothing of the run lands with the ship.
    // Product names: none of the new ones (ShipLink, ShipRoofs) is called; every check reads game state (terrain and roof
    // grids, the engine's sets, GravshipUtility.GetConnectedSubstructure, placement reports, the Gravship object, rooms),
    // HullCompat for the design's cell words, and product members that exist on both sides (ShipDeck.At,
    // SlabSupport.Gravship, ShipLocal.From / To, FloorRoof.Above / Below, HeightRule.TallThingAt).
    public static partial class TestRunner
    {
        private enum HuWant { Sealed, SealedAir, Open }

        private static string huCase = "";
        private static bool huForced;
        private static readonly List<IntVec3> huOver = new List<IntVec3>();      // the case's main cells on the home map
        private static readonly List<IntVec3> huMore = new List<IntVec3>();      // its second cell list
        private static readonly List<IntVec3> huOverOff = new List<IntVec3>();   // ship offsets of huOver
        private static readonly List<IntVec3> huMoreOff = new List<IntVec3>();
        private static readonly Dictionary<IntVec3, string> huRec = new Dictionary<IntVec3, string>();
        private static readonly Dictionary<string, IntVec3> huThingOff = new Dictionary<string, IntVec3>();
        private static readonly List<string> huUnder = new List<string>();
        private static readonly List<(string name, IntVec3 off, HuWant want)> huRooms = new List<(string, IntVec3, HuWant)>();
        private static bool[] huExposed = new bool[0];
        private static float[] huVacuum = new float[0];
        private static string huA, huLeft, huItem, huAnimal;
        private static IntVec3 huCell, huAOff, huLowOff, huUpOff;
        private static int huFloating;

        private const int HuShipGround = 147;        // BuildShip 13 x 11 + its two thruster stubs
        private const int HuShipDeck = 13 * 11;      // a deck floor over all of it

        private static void StepShipHull(Map map)
        {
            switch (stage)
            {
                case "hu_setup": HuSetup(map); return;
                case "hu_h1_plan": Hu1Plan(map); return;
                case "hu_h1_drag": Hu1Drag(map); return;
                case "hu_h1_built": Hu1Built(map); return;
                case "hu_h2_built": Hu2Built(map); return;
                case "hu_h3_removed": Hu3Removed(map); return;
                case "hu_h3_launch": Hu3Launch(map); return;
                case "hu_h4_drag": Hu4Drag(map); return;
                case "hu_h4_built": Hu4Built(map); return;
                case "hu_h5_removed": Hu5Removed(map); return;
                case "hu_h6_removed":
                    tickWatch = null;
                    SetWork(map, false);
                    StartTicks(600, "hu_h6_stands");
                    return;
                case "hu_h6_stands": Hu6Stands(map); return;
                case "hu_h6_decon":
                    tickWatch = null;
                    SetWork(map, false);
                    StartTicks(600, "hu_h6_fell");
                    return;
                case "hu_h6_fell": Hu6Fell(map); return;
                case "hu_h7_hall": Hu7Hall(map); return;
                case "hu_h7_mini": Hu7Mini(map); return;
                case "hu_h7_installed": Hu7Installed(map); return;
                case "hu_h7_up": Hu7Up(map); return;
                case "hu_h8_rooms": Hu8Rooms(map); return;
                case "hu_h8_check": Hu8Check(); return;
                case "hu_h9_block": Hu9Block(map); return;
                case "hu_h9_fly": Hu9Fly(map, null); return;
                case "hu_h11_column": Hu11Column(map); return;
                case "hu_h11_fly": Hu9Fly(map, Hu11Mid); return;
                case "hu_landed": HuLanded(); return;
                case "hu_end": Finish(); return;
            }
        }

        // ------------------------------------------------------------------ shared fixture parts

        private static void HuSetup(Map map)
        {
            huCase = GenCommandLine.TryGetCommandLineArg("projectzhull", out string hc) ? hc.ToLowerInvariant() : "";
            sfMidHook = null;
            sfLandHook = null;
            huForced = false;
            sfOneStorey = huCase == "h10";
            if (huCase == "h9" || huCase == "h11")
            {
                // (the shipset fixture: 575 cells for a limit of 500)
                SetupShipSet(map, huCase == "h9" ? "hu_h9_block" : "hu_h11_column");
                return;
            }
            if (!HuField(map, huCase == "h7" ? 4 : 3))
            {
                return;
            }
            sfMidHook = null;
            sfLandHook = null;
            huForced = false;
            switch (huCase)
            {
                case "h1": Hu1Setup(map); return;
                case "h2": Hu2Setup(map); return;
                case "h3": Hu3Setup(map); return;
                case "h4": Hu4Setup(map); return;
                case "h5": Hu5Setup(map); return;
                case "h6": Hu6Setup(map); return;
                case "h7": Hu7Setup(map); return;
                case "h8": Hu8Setup(map); return;
                case "h10": Hu10Setup(map); return;
            }
            Result("shiphull: which check", false, $"-projectzhull=h1 ... h11 (got '{huCase}')");
            Finish();
        }

        // A clean field for the ship (west: stairs, walkway, stocks; east: overhangs, hull B), sfO its south-west corner.
        private static bool HuField(Map map, int floorsNeeded)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || an.floors < floorsNeeded || !ModsConfig.OdysseyActive || PZDefOf.PZ_ShipDeck == null || PZDefOf.PZ_ShipStairs == null)
            {
                Result("shiphull setup", false, $"needs {floorsNeeded} floors and Odyssey; floors {an?.floors}");
                Finish();
                return false;
            }
            const int W = 50, H = 32;
            CellRect found = CellRect.Empty;
            int bestBad = int.MaxValue;
            for (int z = an.groundZ - H - 4; z >= 4; z -= 3)
            {
                for (int x = 4; x + W < map.Size.x - 4; x += 3)
                {
                    CellRect r = new CellRect(x, z, W, H);
                    if (r.ExpandedBy(1).Cells.Any(c => c.InBounds(map) && c.GetThingList(map).Any(t => !t.def.destroyable)))
                    {
                        continue;
                    }
                    int bad = r.Cells.Count(c => c.GetTerrain(map).IsWater || c.GetTerrain(map).passability == Traversability.Impassable || map.roofGrid.Roofed(c) || c.GetFirstBuilding(map) != null);
                    if (bad < bestBad)
                    {
                        bestBad = bad;
                        found = r;
                    }
                }
                if (bestBad == 0)
                {
                    break;
                }
            }
            if (found.Area == 0)
            {
                Result("shiphull field", false, "map too small");
                Finish();
                return false;
            }
            List<Pawn> cols = Colonists(map);
            foreach (IntVec3 c in found.ExpandedBy(3).ClipInsideMap(map))
            {
                if (ProjectZ.IsAnnex(c, map))
                {
                    continue;
                }
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t.def.destroyable && !(t is Pawn p && cols.Contains(p)))
                    {
                        t.Destroy();
                    }
                }
                if (map.terrainGrid.FoundationAt(c) != null)
                {
                    map.terrainGrid.RemoveFoundation(c, false);
                }
                TerrainDef td = c.GetTerrain(map);
                if (map.terrainGrid.UnderTerrainAt(c) != null || td.IsWater || td.passability == Traversability.Impassable || !td.affordances.Contains(TerrainAffordanceDefOf.Heavy))
                {
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
                for (int k = 1; k < an.floors; k++)
                {
                    IntVec3 u = ProjectZ.CellOnFloor(c, k, map);
                    if (u.IsValid)
                    {
                        map.fogGrid.Unfog(u);
                    }
                }
            }
            // no incidents, no animals, no hostiles: nothing but the colonists' own jobs happens
            foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
            {
                a.Destroy();
            }
            DebugSettings.enableRandomMentalStates = false;
            Find.Storyteller.storytellerComps.Clear();
            Find.Storyteller.incidentQueue.Clear();
            Find.PlaySettings.autoRebuild = false;
            NoAmbushes(map);
            Find.ResearchManager.gravEngineInspected = true;
            ResearchProjectDef grav = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("BasicGravtech");
            if (grav != null && !grav.IsFinished)
            {
                Find.ResearchManager.FinishProject(grav, doCompletionDialog: false);
            }
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            for (int i = 0; i < 24 && GenLocalDate.HourOfDay(map) != 10; i++)
            {
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + GenDate.TicksPerHour);
            }
            map.areaManager.BuildRoof.Clear();
            map.areaManager.NoRoof.Clear();
            sfHomeTile = map.Tile;
            sfO = new IntVec3(found.minX + 11, 0, found.minZ + 8);
            Info($"shiphull {huCase}: field {found}, ship SW {sfO}, ground rows {an.groundZ}, stride {an.stride}, floors {an.floors}; 3D hull product {HullCompat.NewHull}");
            return true;
        }

        private static Building_GravEngine HuShip(Map map)
        {
            BuildShip(map, sfO, 13, 11);
            Building_GravEngine e = GlEngine(map);
            e.nameHidden = false;
            return e;
        }

        // Ship decks placed on floor k over the ship-relative rect, except where skip says so.
        private static void HuDecks(Map map, int k, int x0, int z0, int x1, int z1, Func<int, int, bool> skip = null)
        {
            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    if (skip == null || !skip(x, z))
                    {
                        map.terrainGrid.SetTerrain(SFF(x, z, k, map), PZDefOf.PZ_ShipDeck);
                    }
                }
            }
        }

        private static List<IntVec3> HuRect(Map map, int k, int x0, int z0, int x1, int z1)
        {
            List<IntVec3> l = new List<IntVec3>();
            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    l.Add(k == 0 ? SF(x, z) : SFF(x, z, k, map));
                }
            }
            return l;
        }

        // The way up, as in shipdeck: ordinary stairs A on the ground up to a floor-2 slab walkway along the ship's west
        // side (wood walls on the ground hold its roofs); toF3: stairs B on the walkway up to a floor-3 walkway cell beside
        // the ship, and a wood wall on the walkway that holds floor-3 roofs.
        private static void HuAccess(Map map, bool toF3)
        {
            TerrainDef wood = PZDefOf.PZ_UpperFloor;
            foreach (int dz in Enumerable.Range(0, 10))
            {
                map.terrainGrid.SetTerrain(SFF(-1, dz, 1, map), wood);
            }
            foreach (int dz in new[] { 4, 5, 6, 7 })
            {
                map.terrainGrid.SetTerrain(SFF(-2, dz, 1, map), wood);
            }
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SF(-2, 2), map);
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SF(-2, 8), map);
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, SF(-2, -2), map, Rot4.North);
            if (toF3)
            {
                Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, SFF(-2, 5, 1, map), map, Rot4.North);
                map.terrainGrid.SetTerrain(SFF(-1, 7, 2, map), wood);
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SFF(-1, 8, 1, map), map);
            }
        }

        // n colonists who can build (construction 10), standing west of the stairs, undrafted, no work yet.
        private static List<Pawn> HuCrew(Map map, int n)
        {
            List<Pawn> cols = Colonists(map);
            for (int guard = 0; cols.Count(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)) < n && guard < 60; guard++)
            {
                Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                if (p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) || p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                {
                    continue;
                }
                GenSpawn.Spawn(p, SF(-5, -4), map);
                cols.Add(p);
            }
            // (the builders first, in the order the game lists them: the same roles every run)
            cols = cols.OrderBy(p => p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) || p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) ? 1 : 0).ThenBy(p => p.thingIDNumber).ToList();
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], SF(-5 - i % 4, -3 - i / 4));
                Draft(cols[i], false);
                SkillRecord s = cols[i].skills?.GetSkill(SkillDefOf.Construction);
                if (s != null && !s.TotallyDisabled)
                {
                    s.Level = Mathf.Max(s.Level, 10);
                }
            }
            SetWork(map, false);
            return cols;
        }

        private static void HuStock(Map map, params (ThingDef def, int count)[] stock)
        {
            int sx = 0;
            foreach ((ThingDef def, int count) in stock)
            {
                int left = count;
                while (left > 0)
                {
                    Thing t = ThingMaker.MakeThing(def);
                    t.stackCount = Mathf.Min(left, def.stackLimit);
                    left -= t.stackCount;
                    GenSpawn.Spawn(t, SF(-9 + sx % 4, -7 + sx / 4), map);
                    sx++;
                }
            }
        }

        // Builders at work: construction first, hauling second, nobody drafted.
        private static void HuWork(Map map)
        {
            SetWork(map, true);
            foreach (Pawn p in Colonists(map))
            {
                Draft(p, false);
                if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                {
                    p.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2);
                }
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
        }

        // Deck blueprints with the game's designator, cell by cell in the order given. Returns how many it placed.
        private static int HuPlan(Map map, IEnumerable<IntVec3> cells, out string detail)
        {
            Designator_Build deck = new Designator_Build(PZDefOf.PZ_ShipDeck);
            List<string> refused = new List<string>();
            int n = 0;
            FloorView.Set(0);
            foreach (IntVec3 c in cells)
            {
                AcceptanceReport r = deck.CanDesignateCell(c);
                if (r.Accepted)
                {
                    deck.DesignateSingleCell(c);
                    n++;
                }
                else
                {
                    refused.Add($"{c} f{ProjectZ.FloorOf(c, map) + 1} '{r.Reason}'");
                }
            }
            detail = refused.Count == 0 ? n + " planned" : $"{n} planned, refused {string.Join(", ", refused.Take(4))}" + (refused.Count > 4 ? $" (+{refused.Count - 4})" : "");
            return n;
        }

        // The old rule refused a fixture step: the decks are put in place directly so the rest of the flow runs.
        private static void HuForce(Map map, IEnumerable<IntVec3> cells, string what)
        {
            int n = 0;
            foreach (IntVec3 c in cells)
            {
                foreach (Thing t in c.GetThingList(map).Where(t => t is Blueprint || t is Frame).ToList())
                {
                    t.Destroy(DestroyMode.Cancel);
                }
                if (!ShipDeck.At(c, map))
                {
                    map.terrainGrid.SetTerrain(c, PZDefOf.PZ_ShipDeck);
                    n++;
                }
            }
            huForced = true;
            GlEngine(map)?.ForceSubstructureDirty();
            Info($"{what}: FORCED, {n} deck(s) put in place directly after the designator refused them");
        }

        private static string HuCellStr(IntVec3 c, Map map) =>
            $"{Def(map.terrainGrid.FoundationAt(c))}/{Def(map.terrainGrid.TerrainAt(c))}/{map.roofGrid.RoofAt(c)?.defName ?? "-"}";

        // Before the launch: every ship cell and every open cell above one (their roofs are the ship's), by ship offset.
        private static void HuRecord(Map map, Building_GravEngine e)
        {
            huRec.Clear();
            IntVec3 o = e.Position;
            foreach (IntVec3 c in e.ValidSubstructure)
            {
                huRec[ShipLocal.From(c, o, map)] = HuCellStr(c, map);
                for (IntVec3 u = FloorRoof.Above(c, map); u.IsValid && HullCompat.Open(u, map); u = FloorRoof.Above(u, map))
                {
                    huRec[ShipLocal.From(u, o, map)] = HuCellStr(u, map);
                }
            }
            Info($"{huCase}: recorded {huRec.Count} cells ({string.Join(", ", huRec.Keys.GroupBy(k => k.y).OrderBy(g => g.Key).Select(g => $"f{g.Key + 1} {g.Count()}"))}), ship set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)})");
        }

        // After the landing: the record again, at the landed root and rotation.
        private static void HuCompare(string tag, Map map)
        {
            int bad = 0;
            string first = null;
            foreach (KeyValuePair<IntVec3, string> kv in huRec)
            {
                IntVec3 c = SfAt(kv.Key, sfRoot, sfRot, map);
                string got = c.InBounds(map) ? HuCellStr(c, map) : "out of bounds";
                if (got != kv.Value || ProjectZ.FloorOf(c, map) != kv.Key.y)
                {
                    bad++;
                    first = first ?? $"{kv.Key} at {c} f{ProjectZ.FloorOf(c, map) + 1}: {got}, want {kv.Value}";
                }
            }
            Result($"{tag} landed: every ship cell, and every open cell above one, on its floor and place (foundation, terrain, roof)", bad == 0 && huRec.Count > 0, $"{bad} of {huRec.Count} differ; {first}");
            Result($"{tag} landed: no floor-tagged cell reached the map (safety net)", ShipLocal.Nets == 0, $"{ShipLocal.Nets}");
        }

        // Cells of a set that no chain of links joins to the engine: 4 ways on a floor, and up or down a column through
        // open cells to the nearest floor.
        private static int HuUnlinked(HashSet<IntVec3> set, Building_GravEngine e, Map map)
        {
            HashSet<IntVec3> seen = new HashSet<IntVec3>();
            Queue<IntVec3> q = new Queue<IntVec3>();
            if (set.Contains(e.Position))
            {
                seen.Add(e.Position);
                q.Enqueue(e.Position);
            }
            while (q.Count > 0)
            {
                IntVec3 c = q.Dequeue();
                for (int i = 0; i < 4; i++)
                {
                    IntVec3 n = c + GenAdj.CardinalDirections[i];
                    if (n.InBounds(map) && ProjectZ.SameBand(n, c, map) && set.Contains(n) && seen.Add(n))
                    {
                        q.Enqueue(n);
                    }
                }
                foreach (IntVec3 v in new[] { HullCompat.Down(c, map), HullCompat.Up(c, map) })
                {
                    if (v.IsValid && set.Contains(v) && seen.Add(v))
                    {
                        q.Enqueue(v);
                    }
                }
            }
            return set.Count - seen.Count;
        }

        // The flight: to a neighbouring tile (or to orbit), landing through hu_landed. footprint: the marker's cells, the
        // ground under every ship cell.
        private static void HuLaunch(Map map, Building_GravEngine e, int footprint, bool orbit)
        {
            PlanetTile dest = PlanetTile.Invalid;
            if (orbit)
            {
                PlanetTile up = Find.WorldGrid.Orbit == null ? PlanetTile.Invalid : Find.WorldGrid.Orbit.GetClosestTile_NewTemp(map.Tile);
                dest = up.Valid && Find.WorldObjects.MapParentAt(up) == null ? up : PlanetTile.Invalid;
            }
            else
            {
                List<PlanetTile> near = new List<PlanetTile>();
                Find.WorldGrid.GetTileNeighbors(map.Tile, near);
                dest = near.FirstOrDefault(t => Find.WorldObjects.MapParentAt(t) == null && !Find.WorldGrid[t].WaterCovered && Find.WorldGrid[t].hilliness != Hilliness.Impassable);
            }
            if (!dest.Valid)
            {
                Result($"{huCase} a tile to fly to", false, orbit ? "no free orbit tile" : "no neighbouring tile");
                Finish();
                return;
            }
            sfHomeTile = map.Tile;
            sfNewTile = dest;
            sfLeg = 1;
            sfSaved = true;
            sfFootprint = footprint;
            sfLandedStage = "hu_landed";
            SfLaunch(map, e, dest);
        }

        // The marker shows the ground under every ship cell (the ship's shadow), ground cells only.
        private static void HuMarker(string tag, GravshipLandingMarker marker)
        {
            Result($"{tag} the landing marker's cells are the ground under every ship cell", marker.GravshipCells.Count() == sfFootprint && marker.GravshipCells.All(c => c.y == 0), $"{marker.GravshipCells.Count()} (want {sfFootprint})");
        }

        // A spot near the marker where the ship, turned rot, may land (as SfLandNew looks for one).
        private static bool HuSpot(WorldComponent_GravshipController ctl, GravshipLandingMarker marker, Rot4 rot, out Designator_MoveGravship d, out IntVec3 root, out IntVec3 click)
        {
            marker.GravshipRotation = rot;
            d = ctl.MoveDesignator();
            IntVec2 size = marker.gravship.Bounds.Size;
            root = IntVec3.Invalid;
            click = IntVec3.Invalid;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(marker.Position, 14f, true))
            {
                IntVec3 k = SfClick(d, c, size, rot);
                if (k.IsValid && d.CanDesignateCell(k).Accepted)
                {
                    root = c;
                    click = k;
                    return true;
                }
            }
            return false;
        }

        private static void HuLanded()
        {
            Map map = SfNew();
            Building_GravEngine e = map == null ? null : GlEngine(map);
            if (e == null)
            {
                Result($"{huCase} landed", false, "no engine on " + map);
                Finish();
                return;
            }
            Info($"{huCase} landed on {map}: engine {GlCell(e.Position, map)}, root {sfRoot}, rotation {sfRot}, set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)})" + (huForced ? "; fixture FORCED (old rule)" : ""));
            switch (huCase)
            {
                case "h2": Hu2Landed(map, e); break;
                case "h3": Hu3Landed(map, e); break;
                case "h4": Hu4Landed(map, e); break;
                case "h5": Hu5Landed(map, e); break;
                case "h7": Hu7Landed(map, e); break;
                case "h8": Hu8Landed(map, e); return;
                case "h9": Hu9Landed(map, e); break;
                case "h10": Hu10Landed(map, e); break;
                case "h11": Hu11Landed(map, e); break;
            }
            Wait(10, "hu_end");
        }

        private static string HuThings(IntVec3 c, Map map) =>
            string.Join("+", c.GetThingList(map).Where(t => !(t is Mote) && !(t is Pawn) && !(t is Filth) && !(t is Plant p && p.def.plant.IsTree)).Select(t => t.def.defName).OrderBy(s => s, StringComparer.Ordinal));

        // ------------------------------------------------------------------ H1 placement and building

        private static void Hu1Setup(Map map)
        {
            Building_GravEngine e = HuShip(map);
            HuAccess(map, false);
            HuDecks(map, 1, 0, 0, 12, 10);
            // (holders, so that only the link rule can refuse these two: beside the island, and beside the cell west of the walkway)
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SF(19, 0), map);
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SF(-4, 5), map);
            // a slab island right beside the overhang's far corner (a wall holds it): a colonist put there can touch the far
            // deck's blueprint before any ship floor does
            map.terrainGrid.SetTerrain(SFF(17, 7, 1, map), PZDefOf.PZ_UpperFloor);
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SF(18, 7), map);
            huOver.Clear();
            huOver.AddRange(HuRect(map, 1, 13, 4, 17, 6));
            List<Pawn> cols = HuCrew(map, 3);
            HuStock(map, (ThingDefOf.GravlitePanel, 30), (ThingDefOf.Steel, 150));
            e.ForceSubstructureDirty();
            huA = cols[0].ThingID;
            huCell = SFF(12, 5, 1, map);
            GoTo(cols[0], huCell);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            TickUntil(8000, () => ById(map, huA)?.Position == huCell, "hu_h1_plan", 30);
        }

        private static void Hu1Plan(Map map)
        {
            tickWatch = null;
            Pawn w = ById(map, huA);
            Result("H1 a colonist walks up the stairs and across the ship's floor-2 deck to its east edge", w?.Position == huCell, $"{w?.Position} (want {huCell}) after {ticksTotal} ticks");
            if (w != null)
            {
                Draft(w, false);
            }
            FloorView.Set(0);
            Designator_Build deck = new Designator_Build(PZDefOf.PZ_ShipDeck);
            string link = "PZ_DeckNeedsShipLink".Translate();
            Result("H1 the text PZ_DeckNeedsShipLink exists", "PZ_DeckNeedsShipLink".CanTranslate(), $"[{LanguageDatabase.activeLanguage?.folderName}] {link}");
            SdExpect("H1 an island deck (plain ground below, nothing beside it, a wall in range) refused: it must connect to the gravship's deck", deck, SFF(19, 1, 1, map), link);
            SdExpect("H1 a deck beside an ordinary slab only (west of the walkway) refused: a slab is no ship floor", deck, SFF(-3, 5, 1, map), link);
            SdExpect("H1 a deck touching the ship's floor-2 deck sideways, plain ground below, accepted", deck, SFF(13, 5, 1, map), null);
            // the drag, through the game's own dragger (a filled rectangle from the far corner back to the ship)
            ShotAt(map, 1, SF(15, 5), 12f);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Designator_Build d = BuildCopyCommandUtility.FindAllowedDesignator(PZDefOf.PZ_ShipDeck) ?? deck;
            Find.DesignatorManager.Select(d);
            Find.DesignatorManager.SelectedStyle = d.DrawStyleCategory?.styles.FirstOrDefault(s => s.DrawStyleWorker is DrawStyle_FilledRectangle);
            Wait(10, "hu_h1_drag");
        }

        private static void Hu1Drag(Map map)
        {
            DesignatorManager dm = Find.DesignatorManager;
            string style = dm.SelectedStyle?.defName ?? "none";
            fakeMouse = ScreenOf(SF(17, 6), 1);
            IntVec3 from = UI.MouseCell();
            fakeMouse = null;
            UiDesignate(ScreenOf(SF(17, 6), 1), ScreenOf(SF(13, 4), 1));
            dm.Deselect();
            FloorView.Set(0);
            int planned = huOver.Count(c => PlannedOn(map, new[] { c }, PZDefOf.PZ_ShipDeck).Count > 0);
            Result("H1 one rectangle drag from the far corner back to the ship lays the whole 5-deep overhang (15 decks planned)", planned == huOver.Count && from == SFF(17, 6, 1, map),
                $"{planned} of {huOver.Count} planned; the drag starts at {from} (want {SFF(17, 6, 1, map)}), style {style}");
            if (planned == 0)
            {
                Result("H1 colonists build the overhang outward from the ship", false, "nothing planned: every cell of the drag was refused");
                Finish();
                return;
            }
            // a colonist on the slab island touches the far corner's blueprint; no built ship floor does yet
            Pawn probe = Colonists(map).Where(p => p.ThingID != huA && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)).OrderBy(p => p.thingIDNumber).First();
            Teleport(probe, SFF(17, 7, 1, map));
            Thing far = PlannedOn(map, new[] { SFF(17, 6, 1, map) }, PZDefOf.PZ_ShipDeck).FirstOrDefault();
            Thing near = PlannedOn(map, new[] { SFF(13, 5, 1, map) }, PZDefOf.PZ_ShipDeck).FirstOrDefault();
            JobFailReason.Clear();
            bool canFar = far != null && GenConstruct.CanConstruct(far, probe, false, true);
            string why = JobFailReason.Reason;
            bool reach = far != null && probe.CanReach(far, PathEndMode.Touch, Danger.Deadly);
            Pawn w = ById(map, huA);
            bool canNear = near != null && w != null && GenConstruct.CanConstruct(near, w, false, true);
            Result("H1 a planned deck waits until built ship floor touches it (the far corner, though a colonist beside it can reach it; text PZ_DeckNeedsShipLink); the cell next to the ship does not wait",
                far != null && near != null && reach && !canFar && why == "PZ_DeckNeedsShipLink".Translate() && canNear, $"far: reachable {reach}, may be built {canFar} ('{why}'); near: may be built {canNear}");
            Teleport(probe, SF(-6, -3));
            huFloating = 0;
            HuWork(map);
            TickUntil(60000, () =>
            {
                R2Feed(map);
                foreach (IntVec3 c in huOver)
                {
                    bool frame = c.GetThingList(map).Any(t => t is Frame && t.def.entityDefToBuild == PZDefOf.PZ_ShipDeck);
                    if ((frame || ShipDeck.At(c, map)) && !GenAdj.CardinalDirections.Any(dir => ShipDeck.At(c + dir, map) && ProjectZ.SameBand(c + dir, c, map)))
                    {
                        huFloating++;
                    }
                }
                return huOver.All(c => ShipDeck.At(c, map));
            }, "hu_h1_built", 60);
        }

        private static void Hu1Built(Map map)
        {
            tickWatch = null;
            SetWork(map, false);
            Building_GravEngine e = GlEngine(map);
            int built = huOver.Count(c => ShipDeck.At(c, map));
            Result("H1 colonists build the overhang outward from the ship: no frame or deck ever without built ship floor beside it", built == huOver.Count && huFloating == 0, $"{built} of {huOver.Count} built after {ticksTotal} ticks, {huFloating} sightings of a loose frame or deck");
            int inSet = huOver.Count(c => e.ValidSubstructure.Contains(c));
            int roofs = huOver.Count(c => map.roofGrid.RoofAt(FloorRoof.Below(c, map)) == PZDefOf.PZ_FloorRoof);
            Result("H1 the overhang is part of the ship (the engine's count: ground + floor 2 + 15), the floor's roof on the ground under every deck",
                inSet == huOver.Count && roofs == huOver.Count && e.AllConnectedSubstructure.Count == HuShipGround + HuShipDeck + huOver.Count,
                $"{inSet} of {huOver.Count} in the set, {roofs} floor roofs below; all connected {e.AllConnectedSubstructure.Count} ({SsFloors(e.AllConnectedSubstructure, map)})");
            Wait(10, "hu_end");
        }

        // ------------------------------------------------------------------ H2 overhang flight

        private static void Hu2Setup(Map map)
        {
            Building_GravEngine e = HuShip(map);
            HuAccess(map, false);
            HuDecks(map, 1, 0, 0, 12, 10);
            huOver.Clear();
            huOver.AddRange(HuRect(map, 1, 13, 4, 15, 6));
            HuCrew(map, 4);
            HuStock(map, (ThingDefOf.GravlitePanel, 20), (ThingDefOf.Steel, 100));
            e.ForceSubstructureDirty();
            int planned = HuPlan(map, huOver, out string why);
            Result("H2 the overhang (3 x 3 on floor 2 beside the ship's deck, plain ground below) is planned with the deck designator", planned == huOver.Count, why);
            if (planned < huOver.Count)
            {
                HuForce(map, huOver, "H2 overhang");
            }
            HuWork(map);
            TickUntil(60000, () =>
            {
                R2Feed(map);
                return huOver.All(c => ShipDeck.At(c, map));
            }, "hu_h2_built", 60);
        }

        private static void Hu2Built(Map map)
        {
            tickWatch = null;
            SetWork(map, false);
            Building_GravEngine e = GlEngine(map);
            int built = huOver.Count(c => ShipDeck.At(c, map));
            Result("H2 colonists built the overhang", built == huOver.Count && !huForced, $"{built} of {huOver.Count} after {ticksTotal} ticks" + (huForced ? "; FORCED (put in place directly)" : ""));
            List<IntVec3> under = huOver.Select(c => FloorRoof.Below(c, map)).ToList();
            int inSet = huOver.Count(c => e.ValidSubstructure.Contains(c));
            Result("H2 the overhang is part of the ship; the floor's roof lies on the plain ground under it",
                inSet == huOver.Count && under.All(g => map.roofGrid.RoofAt(g) == PZDefOf.PZ_FloorRoof && map.terrainGrid.FoundationAt(g) == null),
                $"{inSet} of {huOver.Count} in the set; ground under it: {YaRoofs(under, map)}, substructure {under.Count(g => map.terrainGrid.FoundationAt(g) != null)}");
            // steel and an animal on the ground under the overhang
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 30;
            GenSpawn.Spawn(steel, SF(14, 5), map);
            steel.SetForbidden(true, false);
            huItem = steel.ThingID;
            Pawn animal = PawnGenerator.GeneratePawn(PawnKindDefOf.Muffalo, Faction.OfPlayer);
            GenSpawn.Spawn(animal, SF(14, 4), map);
            animal.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 30000), JobCondition.InterruptForced);
            huAnimal = animal.ThingID;
            // two colonists on the ground deck, one on the overhang, one stays home
            List<Pawn> cols = Colonists(map);
            Teleport(cols[0], SF(2, 2));
            Teleport(cols[1], SF(3, 2));
            Teleport(cols[2], SFF(14, 5, 1, map));
            Teleport(cols[3], SF(-8, 4));
            foreach (Pawn p in cols)
            {
                Draft(p, true);
            }
            huA = cols[2].ThingID;
            huLeft = cols[3].ThingID;
            huAOff = ShipLocal.From(cols[2].Position, e.Position, map);
            huOverOff.Clear();
            huOverOff.AddRange(huOver.Select(c => ShipLocal.From(c, e.Position, map)));
            HuRecord(map, e);
            sfMidHook = Hu2Mid;
            sfLandHook = Hu2Land;
            HuLaunch(map, e, HuShipGround + huOver.Count, false);
        }

        private static void Hu2Mid()
        {
            Map home = SfHome();
            Gravship g = Current.Game.Gravship ?? Find.WorldObjects.AllWorldObjects.OfType<Gravship>().FirstOrDefault();
            if (home == null || g == null)
            {
                Result("H2 in flight: the old map and the flying ship", false, $"map {home}, gravship {g != null}");
                return;
            }
            List<IntVec3> under = huOver.Select(c => FloorRoof.Below(c, home)).ToList();
            int roofs = under.Count(c => home.roofGrid.RoofAt(c) != null);
            int left = huOver.Count(c => home.terrainGrid.FoundationAt(c) != null || home.terrainGrid.TopTerrainAt(c) != PZDefOf.PZ_OpenAir || home.roofGrid.RoofAt(c) != null);
            Result("H2 after the take-off: the overhang is gone (open air, no roof) and no roof is left on the ground under it (open sky)", roofs == 0 && left == 0,
                $"ground under it: {YaRoofs(under, home)}; overhang cells not empty {left} of {huOver.Count}");
            Thing steel = SfThingById(home, huItem);
            Pawn animal = SfPawn(huAnimal);
            Result("H2 the steel and the animal on the ground under the overhang stayed behind, unharmed",
                steel != null && steel.Spawned && steel.Position == SF(14, 5) && animal != null && animal.Map == home && !animal.Dead && !animal.Downed,
                $"steel {(steel == null ? "gone" : steel.Position.ToString())} (want {SF(14, 5)}); animal {(animal == null ? "gone" : $"on {animal.Map} at {animal.Position}, dead {animal.Dead}, downed {animal.Downed}")}");
            int keys = huOverOff.Count(o => g.Foundations.ContainsKey(o));
            int groundKeys = huOverOff.Count(o => g.Foundations.ContainsKey(new IntVec3(o.x, 0, o.z)));
            Result("H2 the flying ship holds the overhang's decks (floor-tagged keys) and no ground cell under them; the colonist who stood on it is aboard",
                keys == huOverOff.Count && groundKeys == 0 && g.Pawns.Any(p => p.ThingID == huA), $"deck keys {keys} of {huOverOff.Count}, ground keys under them {groundKeys}, aboard {g.Pawns.Any(p => p.ThingID == huA)} ({g.Pawns.Count()} pawns)");
        }

        // The new map: turned East; the ground under the overhang must be clear.
        private static void Hu2Land(WorldComponent_GravshipController ctl, GravshipLandingMarker marker)
        {
            Map map = marker.Map;
            HuMarker("H2", marker);
            if (!HuSpot(ctl, marker, Rot4.East, out Designator_MoveGravship d, out IntVec3 root, out IntVec3 click))
            {
                Result("H2 a spot for the ship turned East near the marker", false, "none within 14 cells");
                marker.GravshipRotation = Rot4.North;
                SfConfirm(ctl, marker);
                return;
            }
            List<IntVec3> under = huOverOff.Select(o => SfAt(new IntVec3(o.x, 0, o.z), root, Rot4.East, map)).ToList();
            IntVec3 g = under[under.Count / 2];
            Info($"H2 landing spot {root} (East); ground under the overhang {string.Join(" ", under)}, test cell {g}");
            huUnder.Clear();
            huUnder.AddRange(under.Select(c => $"{Def(map.terrainGrid.TopTerrainAt(c))}/{Def(map.terrainGrid.FoundationAt(c))}/{(c == g || c == under[0] ? "" : HuThings(c, map))}"));
            MethodInfoRefuse(d, click, g, map, "H2 landing refused: a roof over the ground under the overhang (red cell)", null,
                () => map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed), () => map.roofGrid.SetRoof(g, null));
            Thing wall = null;
            MethodInfoRefuse(d, click, g, map, "H2 landing refused: a wall on the ground under the overhang (red cell)", null,
                () => wall = Place(ThingDefOf.Wall, ThingDefOf.WoodLog, g, map), () => wall?.Destroy());
            AcceptanceReport clean = d.CanDesignateCell(click);
            Result("H2 landing accepted with the ground under the overhang clear", clean.Accepted, GlRep(clean));
            // (a tree does not refuse it: the landing clears the trees under the whole shadow, as the game does round a ship)
            huCell = under[0];
            Thing tree = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Plant_TreeOak), huCell, map);
            ((Plant)tree).Growth = 1f;
            AcceptanceReport withTree = d.CanDesignateCell(click);
            Result("H2 landing accepted with a full-grown tree on the ground under the overhang", withTree.Accepted, GlRep(withTree));
            d.DesignateSingleCell(click);
            SfConfirm(ctl, marker);
        }

        private static void Hu2Landed(Map map, Building_GravEngine e)
        {
            HuCompare("H2", map);
            List<IntVec3> decks = huOverOff.Select(o => SfAt(o, sfRoot, sfRot, map)).ToList();
            List<IntVec3> under = decks.Select(c => FloorRoof.Below(c, map)).ToList();
            int placed = decks.Count(c => ShipDeck.At(c, map) && e.ValidSubstructure.Contains(c));
            Result("H2 landed: the overhang's decks are placed on floor 2, part of the ship, the floor's roof on the ground under each",
                placed == decks.Count && under.All(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_FloorRoof), $"{placed} of {decks.Count} decks in the set; ground under them: {YaRoofs(under, map)}");
            IntVec3 g = under[under.Count / 2];
            List<string> now = under.Select(c => $"{Def(map.terrainGrid.TopTerrainAt(c))}/{Def(map.terrainGrid.FoundationAt(c))}/{(c == g || c == huCell ? "" : HuThings(c, map))}").ToList();
            Result("H2 landed: the ground under the overhang is untouched (its terrain, no substructure, what stood there but for trees)", now.SequenceEqual(huUnder) && under.All(c => map.terrainGrid.FoundationAt(c) == null),
                $"now {string.Join(" ", now)}; before {string.Join(" ", huUnder)}");
            Result("H2 landed: the tree that stood on the ground under the overhang is gone (cleared by the landing)", !huCell.GetThingList(map).Any(t => t is Plant p && p.def.plant.IsTree), HuThings(huCell, map) + " / " + string.Join("+", huCell.GetThingList(map).Select(t => t.def.defName)));
            Pawn rider = SfPawn(huA);
            Result("H2 landed: the colonist rode on the overhang", rider != null && rider.Map == map && rider.Position == SfAt(huAOff, sfRoot, sfRot, map), $"{rider?.Position} on {rider?.Map} (want {SfAt(huAOff, sfRoot, sfRot, map)})");
        }

        // ------------------------------------------------------------------ H3 hollow hull

        private static void Hu3Setup(Map map)
        {
            Building_GravEngine e = HuShip(map);
            HuAccess(map, false);
            HuDecks(map, 1, 0, 0, 12, 10);
            huOver.Clear();
            huOver.AddRange(HuRect(map, 0, 10, 8, 11, 9));
            HuCrew(map, 4);
            e.ForceSubstructureDirty();
            Info($"H3 hollow cells {string.Join(" ", huOver)}: buildings {string.Join(",", huOver.Select(c => c.GetEdifice(map)?.def.defName ?? "-"))}, roofs {YaRoofs(huOver, map)}");
            Designator_RemoveFoundation rf = AllDesignators().OfType<Designator_RemoveFoundation>().FirstOrDefault() ?? new Designator_RemoveFoundation();
            FloorView.Set(0);
            List<string> refused = new List<string>();
            foreach (IntVec3 c in huOver)
            {
                AcceptanceReport a = rf.CanDesignateCell(c);
                if (a.Accepted)
                {
                    rf.DesignateSingleCell(c);
                }
                else
                {
                    refused.Add($"{c} '{a.Reason}'");
                }
            }
            Result("H3 the ground substructure under a floor-2 deck may be designated for removal (how a ship gets hollow)", refused.Count == 0, refused.Count == 0 ? $"{huOver.Count} designated" : "refused " + string.Join(", ", refused));
            if (refused.Count > 0)
            {
                foreach (IntVec3 c in huOver)
                {
                    map.designationManager.DesignationAt(c, DesignationDefOf.RemoveFoundation)?.Delete();
                    map.terrainGrid.RemoveFoundation(c, false);
                }
                huForced = true;
                Info("H3 hollow: FORCED, the substructure taken away directly after the designator refused it");
            }
            HuWork(map);
            TickUntil(30000, () =>
            {
                R2Feed(map);
                return huOver.All(c => map.terrainGrid.FoundationAt(c) == null);
            }, "hu_h3_removed", 60);
        }

        private static void Hu3Removed(Map map)
        {
            tickWatch = null;
            SetWork(map, false);
            Building_GravEngine e = GlEngine(map);
            List<IntVec3> above = huOver.Select(c => ProjectZ.CellOnFloor(c, 1, map)).ToList();
            int gone = huOver.Count(c => map.terrainGrid.FoundationAt(c) == null);
            Result("H3 colonists took the substructure up under the deck", gone == huOver.Count && !huForced, $"{gone} of {huOver.Count} after {ticksTotal} ticks" + (huForced ? "; FORCED (taken away directly)" : ""));
            int decks = above.Count(c => ShipDeck.At(c, map)), inSet = above.Count(c => e.ValidSubstructure.Contains(c)), groundIn = huOver.Count(c => e.ValidSubstructure.Contains(c));
            Result("H3 the decks over the hollow stay and are ship cells; the hollow's ground is not; the floor's roof is still over it",
                decks == above.Count && inSet == above.Count && groundIn == 0 && huOver.All(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_FloorRoof),
                $"decks {decks} of {above.Count}, in the set {inSet}, hollow ground in the set {groundIn}, roofs over the hollow {YaRoofs(huOver, map)}; set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)})");
            Room r = huOver[0].GetRoom(map), r0 = SF(5, 2).GetRoom(map);
            Result("H3 the ring and the hollow are one indoor room", r != null && r == r0 && r.OpenRoofCount == 0 && !r.UsesOutdoorTemperature,
                r == null ? "no room" : $"hollow room {r.ID} ({r.CellCount} cells, open roof {r.OpenRoofCount}, outdoor temperature {r.UsesOutdoorTemperature}), ship room {r0?.ID}");
            // a colonist walks into the hollow, steel lies there; the others on the ship's ground deck
            List<Pawn> cols = Colonists(map);
            Teleport(cols[0], SF(2, 2));
            Teleport(cols[1], SF(3, 2));
            Teleport(cols[2], SF(2, 3));
            foreach (Pawn p in cols.Take(3))
            {
                Draft(p, true);
            }
            huLeft = cols[3].ThingID;
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 30;
            GenSpawn.Spawn(steel, huOver[3], map);
            steel.SetForbidden(true, false);
            huItem = steel.ThingID;
            GoTo(cols[3], huOver[0]);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            TickUntil(8000, () => SfPawn(huLeft)?.Position == huOver[0], "hu_h3_launch", 30);
        }

        private static void Hu3Launch(Map map)
        {
            tickWatch = null;
            Building_GravEngine e = GlEngine(map);
            Pawn left = SfPawn(huLeft);
            Result("H3 a colonist walked into the hollow", left?.Position == huOver[0], $"{left?.Position} (want {huOver[0]}) after {ticksTotal} ticks");
            string text = SsDialog(e);
            int listAt = text.IndexOf("PeopleWillBeLeftBehind".Translate().Resolve());
            string list = listAt < 0 ? "" : text.Substring(listAt);
            Result("H3 the launch dialog lists the colonist standing in the hollow as left behind", left != null && !e.ValidSubstructureAt(left.Position) && list.Contains(left.LabelShort), list.Replace("\n", " | "));
            huOverOff.Clear();
            huOverOff.AddRange(huOver.Select(c => ShipLocal.From(c, e.Position, map)));
            huLowOff = ShipLocal.From(SF(5, 2), e.Position, map);
            HuRecord(map, e);
            sfMidHook = Hu3Mid;
            sfLandHook = Hu3Land;
            HuLaunch(map, e, HuShipGround, false);
        }

        private static void Hu3Mid()
        {
            Map home = SfHome();
            Gravship g = Current.Game.Gravship ?? Find.WorldObjects.AllWorldObjects.OfType<Gravship>().FirstOrDefault();
            if (home == null || g == null)
            {
                Result("H3 in flight: the old map and the flying ship", false, $"map {home}, gravship {g != null}");
                return;
            }
            Pawn left = SfPawn(huLeft);
            Thing steel = SfThingById(home, huItem);
            Result("H3 after the take-off: the colonist and the steel in the hollow stayed behind, unharmed",
                left != null && left.Map == home && !left.Dead && !left.Downed && huOver.Contains(left.Position) && steel != null && steel.Spawned && steel.Position == huOver[3],
                $"colonist {(left == null ? "gone" : $"on {left.Map} at {left.Position}, downed {left.Downed}")}; steel {(steel == null ? "gone" : steel.Position.ToString())} (want {huOver[3]})");
            List<IntVec3> above = huOver.Select(c => ProjectZ.CellOnFloor(c, 1, home)).ToList();
            int roofs = huOver.Count(c => home.roofGrid.RoofAt(c) != null);
            int left2 = above.Count(c => home.terrainGrid.FoundationAt(c) != null || home.terrainGrid.TopTerrainAt(c) != PZDefOf.PZ_OpenAir || home.roofGrid.RoofAt(c) != null);
            Result("H3 after the take-off: the hollow's roof went with the deck (open sky), nothing is left above it", roofs == 0 && left2 == 0, $"roofs over the hollow {YaRoofs(huOver, home)}; cells above not empty {left2} of {above.Count}");
            int deckKeys = huOverOff.Count(o => g.Foundations.ContainsKey(new IntVec3(o.x, 1, o.z))), groundKeys = huOverOff.Count(o => g.Foundations.ContainsKey(o));
            Result("H3 the flying ship holds the decks over the hollow and not the hollow's ground", deckKeys == huOverOff.Count && groundKeys == 0, $"deck keys {deckKeys} of {huOverOff.Count}, ground keys {groundKeys}");
        }

        // The new map: where the game put the marker (its reserved landing area), unturned.
        private static void Hu3Land(WorldComponent_GravshipController ctl, GravshipLandingMarker marker)
        {
            Map map = marker.Map;
            HuMarker("H3", marker);
            Designator_MoveGravship d = ctl.MoveDesignator();
            Rot4 rot = marker.GravshipRotation;
            IntVec3 root = marker.Position;
            IntVec3 click = SfClick(d, root, marker.gravship.Bounds.Size, rot);
            AcceptanceReport here = click.IsValid ? d.CanDesignateCell(click) : new AcceptanceReport("no click cell for the marker's own spot");
            List<IntVec3> hollow = huOverOff.Select(o => SfAt(o, root, rot, map)).ToList();
            int clear = hollow.Count(c => c.InBounds(map) && c.GetEdifice(map) == null && HeightRule.TallThingAt(c, map) == null && !map.roofGrid.Roofed(c) && !c.GetThingList(map).Any(t => t is Plant p && p.def.plant.IsTree));
            Result("H3 the new map: the ship may land where the game put the marker, and the hollow's ground there was cleared with the rest of the landing area", here.Accepted && clear == hollow.Count,
                $"marker at {root} ({rot}): {GlRep(here)}; hollow ground clear {clear} of {hollow.Count}: {string.Join(" ", hollow.Select(c => HuThings(c, map)))}");
            if (here.Accepted)
            {
                d.DesignateSingleCell(click);
                SfConfirm(ctl, marker);
            }
            else
            {
                SfLandNew(ctl, marker);
            }
        }

        private static void Hu3Landed(Map map, Building_GravEngine e)
        {
            HuCompare("H3", map);
            List<IntVec3> hollow = huOverOff.Select(o => SfAt(o, sfRoot, sfRot, map)).ToList();
            List<IntVec3> above = hollow.Select(c => ProjectZ.CellOnFloor(c, 1, map)).ToList();
            int decks = above.Count(c => ShipDeck.At(c, map) && e.ValidSubstructure.Contains(c));
            Room r = hollow[0].GetRoom(map), r0 = SfAt(huLowOff, sfRoot, sfRot, map).GetRoom(map);
            Result("H3 landed: the hollow's ground has the floor's roof again and no substructure, under the ship's decks",
                decks == above.Count && hollow.All(c => map.roofGrid.RoofAt(c) == PZDefOf.PZ_FloorRoof && map.terrainGrid.FoundationAt(c) == null),
                $"decks over it {decks} of {above.Count}; roofs {YaRoofs(hollow, map)}; substructure {hollow.Count(c => map.terrainGrid.FoundationAt(c) != null)}");
            Result("H3 landed: the ring and the hollow are one indoor room again", r != null && r == r0 && r.OpenRoofCount == 0 && !r.UsesOutdoorTemperature,
                r == null ? "no room" : $"hollow room {r.ID} ({r.CellCount} cells, open roof {r.OpenRoofCount}, outdoor temperature {r.UsesOutdoorTemperature}), ship room {r0?.ID}");
        }

        // ------------------------------------------------------------------ H4 bridge, H6 removal

        // The ship with a floor-2 deck over all of it, hull B (5 x 5 ground substructure, 7 cells east of the ship, all of it
        // inside the engine's range of 18.9), the bridge's 8 cells on floor 2 from the ship's deck to above hull B's west
        // edge, and a wood column right beside the bridge's ground line near its far end (the ship's hull holds the roofs up
        // to 6 cells out, the column the rest: a deck can only be built with a holder reached over roofs that are there).
        // outOfRange: one more substructure cell east of hull B, joined to it, 19.2 cells from the engine.
        private static readonly IntVec3 HuColumnAt = new IntVec3(19, 0, 9);

        private static IntVec3 HuColumn => SF(HuColumnAt.x, HuColumnAt.z);

        private static IntVec3 HuOutOfRange => SF(25, 8);

        private static Building_GravEngine HuBridge(Map map, bool placed, bool outOfRange = false)
        {
            Building_GravEngine e = HuShip(map);
            HuAccess(map, false);
            HuDecks(map, 1, 0, 0, 12, 10);
            // (the grav anchor east of the ship stands where hull B goes: moved north of the ship; without one the old map
            // would go when the ship leaves)
            foreach (Thing anchor in map.listerThings.AllThings.Where(t => t.def.defName == "GravAnchor").ToList())
            {
                anchor.Destroy();
            }
            Place(DefDatabase<ThingDef>.GetNamed("GravAnchor"), ThingDefOf.Steel, SF(8, 16), map, Rot4.North);
            TerrainDef sub = DefDatabase<TerrainDef>.GetNamed("Substructure");
            huMore.Clear();
            huMore.AddRange(HuRect(map, 0, 20, 6, 24, 10));
            foreach (IntVec3 c in huMore)
            {
                map.terrainGrid.SetTerrain(c, sub);
            }
            if (outOfRange)
            {
                map.terrainGrid.SetTerrain(HuOutOfRange, sub);
            }
            Place(ThingDefOf.Column, ThingDefOf.WoodLog, HuColumn, map);
            huOver.Clear();
            huOver.AddRange(HuRect(map, 1, 13, 8, 20, 8));
            if (placed)
            {
                foreach (IntVec3 c in huOver)
                {
                    map.terrainGrid.SetTerrain(c, PZDefOf.PZ_ShipDeck);
                }
            }
            e.ForceSubstructureDirty();
            return e;
        }

        private static int HuBridgeAll => HuShipGround + HuShipDeck + huOver.Count + huMore.Count;

        private static void Hu4Setup(Map map)
        {
            Building_GravEngine e = HuBridge(map, false, outOfRange: true);
            HuCrew(map, 4);
            HuStock(map, (ThingDefOf.GravlitePanel, 20), (ThingDefOf.Steel, 100));
            Result("H4 before the bridge: hull B is no part of the ship", !huMore.Any(c => e.AllConnectedSubstructure.Contains(c)) && e.AllConnectedSubstructure.Count == HuShipGround + HuShipDeck,
                $"all connected {e.AllConnectedSubstructure.Count} ({SsFloors(e.AllConnectedSubstructure, map)})");
            Thing bed = Place(ThingDefOf.Bed, ThingDefOf.WoodLog, SF(23, 7), map, Rot4.North);
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 30;
            GenSpawn.Spawn(steel, SF(24, 9), map);
            steel.SetForbidden(true, false);
            huThingOff.Clear();
            huThingOff[bed.ThingID] = bed.Position;
            huThingOff[steel.ThingID] = steel.Position;
            // the bridge is planned as a player does it: one drag, from above hull B back to the ship
            ShotAt(map, 1, SF(16, 8), 14f);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Designator_Build d = BuildCopyCommandUtility.FindAllowedDesignator(PZDefOf.PZ_ShipDeck) ?? new Designator_Build(PZDefOf.PZ_ShipDeck);
            Find.DesignatorManager.Select(d);
            Find.DesignatorManager.SelectedStyle = d.DrawStyleCategory?.styles.FirstOrDefault(s => s.DrawStyleWorker is DrawStyle_FilledRectangle);
            Wait(10, "hu_h4_drag");
        }

        private static void Hu4Drag(Map map)
        {
            IntVec3 far = ProjectZ.GroundCellOf(huOver[huOver.Count - 1], map), near = ProjectZ.GroundCellOf(huOver[0], map);
            UiDesignate(ScreenOf(far, 1), ScreenOf(near, 1));
            Find.DesignatorManager.Deselect();
            FloorView.Set(0);
            int planned = huOver.Count(c => PlannedOn(map, new[] { c }, PZDefOf.PZ_ShipDeck).Count > 0);
            Result("H4 the bridge (floor 2, 8 decks from the ship's deck over plain ground to above hull B) is planned in one drag of the deck designator", planned == huOver.Count, $"{planned} of {huOver.Count} planned");
            if (planned < huOver.Count)
            {
                HuForce(map, huOver, "H4 bridge");
            }
            HuWork(map);
            TickUntil(60000, () =>
            {
                R2Feed(map);
                return huOver.All(c => ShipDeck.At(c, map));
            }, "hu_h4_built", 60);
        }

        private static void Hu4Built(Map map)
        {
            tickWatch = null;
            SetWork(map, false);
            Building_GravEngine e = GlEngine(map);
            int built = huOver.Count(c => ShipDeck.At(c, map));
            Result("H4 colonists built the bridge", built == huOver.Count && !huForced, $"{built} of {huOver.Count} after {ticksTotal} ticks" + (huForced ? "; FORCED (put in place directly)" : ""));
            HashSet<IntVec3> all = e.AllConnectedSubstructure;
            int b = huMore.Count(c => all.Contains(c)), br = huOver.Count(c => all.Contains(c));
            // (the cell east of hull B is joined to it but outside the engine's range: connected, not valid, as vanilla)
            Result("H4 two ground hulls and the floor-2 bridge are one ship; the joined cell outside the engine's range is connected but stays out of it",
                all.Count == HuBridgeAll + 1 && b == huMore.Count && br == huOver.Count && e.ValidSubstructure.Count == HuBridgeAll && all.Contains(HuOutOfRange) && !e.ValidSubstructure.Contains(HuOutOfRange),
                $"all connected {all.Count} (want {HuBridgeAll + 1}): hull B {b} of {huMore.Count}, bridge {br} of {huOver.Count}; valid {e.ValidSubstructure.Count} (want {HuBridgeAll}); the cell out of range: connected {all.Contains(HuOutOfRange)}, valid {e.ValidSubstructure.Contains(HuOutOfRange)}");
            string text = SsDialog(e);
            Result("H4 the launch dialog warns of substructure outside the engine's range (that one cell)", text.Contains("SubstructureOutsideFootprint".Translate().Resolve()), "");
            // the limit cut: ship ground first, then floor 2 with the bridge, then hull B behind it (the bridge's foot first)
            int f2All = HuShipDeck + huOver.Count;
            IntVec3 foot = FloorRoof.Below(huOver[huOver.Count - 1], map);
            foreach ((int n, int wantF2, int wantB) in new[] { (HuShipGround, 0, 0), (HuShipGround + 53, 53, 0), (HuShipGround + f2All, f2All, 0), (HuShipGround + f2All + 1, f2All, 1), (HuShipGround + f2All + 11, f2All, 11) })
            {
                HashSet<IntVec3> cut = new HashSet<IntVec3>();
                GravshipUtility.GetConnectedSubstructure(e, cut, n, true);
                int ground = cut.Count(c => ProjectZ.FloorOf(c, map) == 0 && !huMore.Contains(c)), f2 = cut.Count(c => ProjectZ.FloorOf(c, map) == 1), hullB = huMore.Count(c => cut.Contains(c));
                int loose = HuUnlinked(cut, e, map);
                Result($"H4 limit {n}: the cut set stays linked to the engine and takes the lower floor first where it can be reached (ship ground, then floor 2 with the bridge, then hull B from the bridge's foot)",
                    cut.Count == n && loose == 0 && ground == HuShipGround && f2 == wantF2 && hullB == wantB && (wantB == 0 || cut.Contains(foot)),
                    $"{cut.Count} cells: ship ground {ground}, floor 2 {f2} (want {wantF2}), hull B {hullB} (want {wantB}), the bridge's foot in {cut.Contains(foot)}, not linked {loose}");
            }
            // two colonists on the ship's ground deck, one on hull B, one stays home
            List<Pawn> cols = Colonists(map);
            Teleport(cols[0], SF(2, 2));
            Teleport(cols[1], SF(3, 2));
            Teleport(cols[2], SF(24, 7));
            Teleport(cols[3], SF(-8, 4));
            foreach (Pawn p in cols)
            {
                Draft(p, true);
            }
            huA = cols[2].ThingID;
            huAOff = ShipLocal.From(cols[2].Position, e.Position, map);
            foreach (string id in huThingOff.Keys.ToList())
            {
                huThingOff[id] = ShipLocal.From(huThingOff[id], e.Position, map);
            }
            huOverOff.Clear();
            huOverOff.AddRange(huOver.Select(c => ShipLocal.From(c, e.Position, map)));
            huMoreOff.Clear();
            huMoreOff.AddRange(huMore.Select(c => ShipLocal.From(c, e.Position, map)));
            HuRecord(map, e);
            sfMidHook = Hu4Mid;
            // (the marker's cells: the ship's ground, hull B, and the ground under the 7 bridge decks between them)
            HuLaunch(map, e, HuShipGround + huMore.Count + huOver.Count - 1, false);
        }

        private static void Hu4Mid()
        {
            Map home = SfHome();
            if (home == null)
            {
                Result("H4 in flight: the old map", false, "gone");
                return;
            }
            int subLeft = huMore.Count(c => home.terrainGrid.FoundationAt(c) != null), bridgeLeft = huOver.Count(c => home.terrainGrid.FoundationAt(c) != null);
            int things = huThingOff.Keys.Count(id => SfThingById(home, id) != null);
            Result("H4 after the take-off: hull B, the bridge and hull B's things left with the ship; the cell outside the engine's range stayed", subLeft == 0 && bridgeLeft == 0 && things == 0 && SlabSupport.Gravship(HuOutOfRange, home),
                $"hull B substructure left {subLeft} of {huMore.Count}, bridge decks left {bridgeLeft} of {huOver.Count}, things left {things} of {huThingOff.Count}; the cell out of range still there {SlabSupport.Gravship(HuOutOfRange, home)}");
        }

        private static void Hu4Landed(Map map, Building_GravEngine e)
        {
            HuCompare("H4", map);
            int b = huMoreOff.Count(o => e.ValidSubstructure.Contains(SfAt(o, sfRoot, sfRot, map))), br = huOverOff.Count(o => e.ValidSubstructure.Contains(SfAt(o, sfRoot, sfRot, map)));
            Result("H4 landed: one ship still, with hull B and the bridge", e.ValidSubstructure.Count == HuBridgeAll && b == huMoreOff.Count && br == huOverOff.Count,
                $"set {e.ValidSubstructure.Count} (want {HuBridgeAll}): hull B {b} of {huMoreOff.Count}, bridge {br} of {huOverOff.Count}");
            int there = 0;
            string detail = "";
            foreach (KeyValuePair<string, IntVec3> kv in huThingOff)
            {
                Thing t = SfThingById(map, kv.Key);
                IntVec3 want = SfAt(kv.Value, sfRoot, sfRot, map);
                bool ok = t != null && t.Spawned && t.OccupiedRect().Contains(want);
                there += ok ? 1 : 0;
                detail += $"{t?.def.defName ?? kv.Key} {(t == null ? "missing" : t.Position.ToString())} (want {want}); ";
            }
            Pawn rider = SfPawn(huA);
            Result("H4 landed: hull B's bed and steel, and the colonist who stood on hull B, came along to their places", there == huThingOff.Count && rider != null && rider.Map == map && rider.Position == SfAt(huAOff, sfRoot, sfRot, map),
                detail + $"colonist {rider?.Position} on {rider?.Map} (want {SfAt(huAOff, sfRoot, sfRot, map)})");
        }

        private static void Hu6Setup(Map map)
        {
            Building_GravEngine e = HuBridge(map, true);
            HuCrew(map, 3);
            HashSet<IntVec3> all = e.AllConnectedSubstructure;
            Result("H6 before: hull B and the whole bridge are part of the ship", all.Count == HuBridgeAll && huMore.All(c => all.Contains(c)) && huOver.All(c => all.Contains(c)),
                $"all connected {all.Count} (want {HuBridgeAll}): hull B {huMore.Count(c => all.Contains(c))} of {huMore.Count}, bridge {huOver.Count(c => all.Contains(c))} of {huOver.Count}");
            huCell = SFF(18, 8, 1, map);
            Designator_RemoveFoundation rf = AllDesignators().OfType<Designator_RemoveFoundation>().FirstOrDefault() ?? new Designator_RemoveFoundation();
            FloorView.Set(1);
            AcceptanceReport a = rf.CanDesignateCell(huCell);
            Result("H6 the bridge's middle deck may be designated for removal", a.Accepted, GlRep(a));
            if (!a.Accepted)
            {
                FloorView.Set(0);
                Finish();
                return;
            }
            rf.DesignateSingleCell(huCell);
            FloorView.Set(0);
            HuWork(map);
            TickUntil(30000, () =>
            {
                R2Feed(map);
                return !ShipDeck.At(huCell, map);
            }, "hu_h6_removed", 60);
        }

        private static void Hu6Stands(Map map)
        {
            Building_GravEngine e = GlEngine(map);
            HashSet<IntVec3> all = e.AllConnectedSubstructure;
            int cut = huOver.IndexOf(huCell);
            List<IntVec3> near = huOver.Take(cut).ToList(), far = huOver.Skip(cut + 1).ToList();
            Result("H6 a colonist removed the bridge's middle deck", !ShipDeck.At(huCell, map), SdTerrain(huCell, map));
            Result("H6 hull B and the far part of the bridge are no longer ship", !huMore.Any(c => all.Contains(c)) && !far.Any(c => all.Contains(c)),
                $"hull B in the set {huMore.Count(c => all.Contains(c))}, far bridge decks in the set {far.Count(c => all.Contains(c))}");
            Result("H6 the ship keeps the near part of the bridge: ground + floor 2 + 5 bridge decks", all.Count == HuShipGround + HuShipDeck + near.Count && near.All(c => all.Contains(c)),
                $"all connected {all.Count} (want {HuShipGround + HuShipDeck + near.Count}): near bridge decks {near.Count(c => all.Contains(c))} of {near.Count}");
            Result("H6 the cut-off part stands (600 ticks later): hull B's substructure and the far bridge decks on their roofs",
                huMore.All(c => SlabSupport.Gravship(c, map)) && far.All(c => ShipDeck.At(c, map) && map.roofGrid.RoofAt(FloorRoof.Below(c, map)) == PZDefOf.PZ_FloorRoof),
                $"hull B {huMore.Count(c => SlabSupport.Gravship(c, map))} of {huMore.Count}, far decks {far.Count(c => ShipDeck.At(c, map))} of {far.Count}, roofs under them {YaRoofs(far.Select(c => FloorRoof.Below(c, map)), map)}");
            Building col = HuColumn.GetEdifice(map);
            if (col == null)
            {
                Result("H6 the column beside the far part", false, "missing");
                Finish();
                return;
            }
            new Designator_Deconstruct().DesignateThing(col);
            HuWork(map);
            TickUntil(20000, () =>
            {
                R2Feed(map);
                return HuColumn.GetEdifice(map) == null;
            }, "hu_h6_decon", 60);
        }

        private static void Hu6Fell(Map map)
        {
            int cut = huOver.IndexOf(huCell);
            List<IntVec3> near = huOver.Take(cut).ToList(), far = huOver.Skip(cut + 1).ToList();
            Result("H6 a colonist deconstructed the only column near the cut-off part", HuColumn.GetEdifice(map) == null, "");
            Result("H6 the cut-off bridge decks collapse by the game's roof rule (nothing in range holds them); the near part and hull B's ground stay",
                !far.Any(c => ShipDeck.At(c, map)) && near.All(c => ShipDeck.At(c, map)) && huMore.All(c => SlabSupport.Gravship(c, map)),
                $"far decks left {far.Count(c => ShipDeck.At(c, map))} of {far.Count}, near decks {near.Count(c => ShipDeck.At(c, map))} of {near.Count}, hull B {huMore.Count(c => SlabSupport.Gravship(c, map))} of {huMore.Count}");
            Wait(10, "hu_end");
        }

        // ------------------------------------------------------------------ H5 hole under a roof

        private static void Hu5Setup(Map map)
        {
            Building_GravEngine e = YaShip(map, sfO);
            huOver.Clear();
            huOver.AddRange(HuRect(map, 1, 8, 1, 9, 3));
            HuCrew(map, 3);
            Info($"H5 ship {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)}); over the hole-to-be {YaRoofs(huOver, map)}, below {YaRoofs(YaBelow(huOver, map), map)}");
            YaPlan(map, huOver, "hu_h5_removed");
        }

        private static void Hu5Removed(Map map)
        {
            tickWatch = null;
            SetWork(map, false);
            Building_GravEngine e = GlEngine(map);
            List<IntVec3> below = YaBelow(huOver, map).ToList();
            int gone = huOver.Count(c => !ProjectZ.HasFloor(c, map));
            Result("H5 a colonist took a 2 x 3 of the deck up in the roofed floor-2 room: the ship's roof is still over the hole, the hidden atrium roof on the ground under it",
                gone == huOver.Count && YaAll(huOver, map, RoofDefOf.RoofConstructed) && YaAll(below, map, PZDefOf.PZ_StairwellRoof), $"decks gone {gone} of {huOver.Count} after {ticksTotal} ticks; over the hole {YaRoofs(huOver, map)}, below {YaRoofs(below, map)}");
            Room up = SFF(5, 5, 1, map).GetRoom(map), low = SF(5, 2).GetRoom(map);
            Result("H5 before the flight both rooms are indoors", up != null && low != null && up != low && up.OpenRoofCount == 0 && low.OpenRoofCount == 0 && !up.UsesOutdoorTemperature && !low.UsesOutdoorTemperature,
                $"above: {YvRoom(up)}; below: {YvRoom(low)}");
            huOverOff.Clear();
            huOverOff.AddRange(huOver.Select(c => ShipLocal.From(c, e.Position, map)));
            huLowOff = ShipLocal.From(SF(5, 2), e.Position, map);
            huUpOff = ShipLocal.From(SFF(5, 5, 1, map), e.Position, map);
            List<Pawn> cols = Colonists(map);
            Teleport(cols[0], SF(2, 2));
            Teleport(cols[1], SF(3, 2));
            Teleport(cols[2], SF(-8, 4));
            foreach (Pawn p in cols)
            {
                Draft(p, true);
            }
            HuRecord(map, e);
            sfMidHook = Hu5Mid;
            HuLaunch(map, e, HuShipGround, false);
        }

        private static void Hu5Mid()
        {
            Map home = SfHome();
            if (home == null)
            {
                Result("H5 in flight: the old map", false, "gone");
                return;
            }
            List<IntVec3> below = YaBelow(huOver, home).ToList();
            Result("H5 after the take-off: no roof is left at the old site over the hole or under it", huOver.All(c => home.roofGrid.RoofAt(c) == null) && below.All(c => home.roofGrid.RoofAt(c) == null),
                $"over the old hole {YaRoofs(huOver, home)}, under it {YaRoofs(below, home)}");
        }

        private static void Hu5Landed(Map map, Building_GravEngine e)
        {
            HuCompare("H5", map);
            List<IntVec3> hole = huOverOff.Select(o => SfAt(o, sfRoot, sfRot, map)).ToList();
            List<IntVec3> below = YaBelow(hole, map).ToList();
            int gone = hole.Count(c => !ProjectZ.HasFloor(c, map));
            Result("H5 landed: the hole is still a hole and lands roofed: the ship's roof over every hole cell, the atrium roof on the ground under each",
                gone == hole.Count && YaAll(hole, map, RoofDefOf.RoofConstructed) && YaAll(below, map, PZDefOf.PZ_StairwellRoof), $"decks gone {gone} of {hole.Count}; over the hole {YaRoofs(hole, map)}, below {YaRoofs(below, map)}");
            Room up = SfAt(huUpOff, sfRoot, sfRot, map).GetRoom(map), low = SfAt(huLowOff, sfRoot, sfRot, map).GetRoom(map);
            Result("H5 landed: both rooms indoors (no open roof cell), the hole's cells in the upper room",
                up != null && low != null && up != low && up.OpenRoofCount == 0 && low.OpenRoofCount == 0 && !up.UsesOutdoorTemperature && !low.UsesOutdoorTemperature && hole.All(c => c.GetRoom(map) == up),
                $"above: {YvRoom(up)}, open roof {up?.OpenRoofCount}; below: {YvRoom(low)}, open roof {low?.OpenRoofCount}; hole cells in the upper room {hole.Count(c => c.GetRoom(map) == up)} of {hole.Count}");
        }

        // ------------------------------------------------------------------ H7 tall hall, engine on an overhang

        private static void Hu7Setup(Map map)
        {
            Building_GravEngine e = HuShip(map);
            HuAccess(map, true);
            // floor 2 over the ship except the hall (x 0..2, z 5..8); an overhang 3 x 3 east of it (placed)
            HuDecks(map, 1, 0, 0, 12, 10, (x, z) => x <= 2 && z >= 5 && z <= 8);
            huMore.Clear();
            huMore.AddRange(HuRect(map, 1, 13, 4, 15, 6));
            foreach (IntVec3 c in huMore)
            {
                map.terrainGrid.SetTerrain(c, PZDefOf.PZ_ShipDeck);
            }
            // the hall's floor-3 decks, planned from the roof holder on the walkway (-1, 8) outwards
            IntVec3 holder = SF(-1, 8);
            huOver.Clear();
            huOver.AddRange(HuRect(map, 2, 0, 5, 2, 8).OrderBy(c => (ProjectZ.GroundCellOf(c, map) - holder).LengthHorizontalSquared).ThenBy(c => c.x).ThenBy(c => c.z));
            HuCrew(map, 4);
            HuStock(map, (ThingDefOf.GravlitePanel, 20), (ThingDefOf.Steel, 100));
            e.ForceSubstructureDirty();
            int open = huOver.Count(c => HullCompat.Open(FloorRoof.Below(c, map), map) && SlabSupport.Gravship(HullCompat.Down(c, map), map));
            Result("H7 the hall: floor 2 is open there, the ship's ground substructure under it", open == huOver.Count, $"{open} of {huOver.Count}");
            int planned = HuPlan(map, huOver, out string why);
            Result("H7 floor-3 decks over the hall's open floor 2 are planned with the deck designator (linked down through the open cells)", planned == huOver.Count, why);
            if (planned < huOver.Count)
            {
                HuForce(map, huOver, "H7 hall");
            }
            HuWork(map);
            TickUntil(60000, () =>
            {
                R2Feed(map);
                return huOver.All(c => ShipDeck.At(c, map));
            }, "hu_h7_hall", 60);
        }

        private static int Hu7All => HuShipGround + (HuShipDeck - huOver.Count) + huMore.Count + huOver.Count;

        private static void Hu7Hall(Map map)
        {
            tickWatch = null;
            SetWork(map, false);
            Building_GravEngine e = GlEngine(map);
            int built = huOver.Count(c => ShipDeck.At(c, map));
            Result("H7 colonists built the hall's floor-3 decks", built == huOver.Count && !huForced, $"{built} of {huOver.Count} after {ticksTotal} ticks" + (huForced ? "; FORCED (put in place directly)" : ""));
            HashSet<IntVec3> all = e.AllConnectedSubstructure;
            int inSet = huOver.Count(c => all.Contains(c)), over = huMore.Count(c => all.Contains(c));
            Result("H7 the tall hall is linked: its floor-3 decks are ship cells over the open floor 2 (the floor's roof on the open cells), the overhang too",
                inSet == huOver.Count && over == huMore.Count && all.Count == Hu7All && huOver.All(c => HullCompat.Open(FloorRoof.Below(c, map), map) && map.roofGrid.RoofAt(FloorRoof.Below(c, map)) == PZDefOf.PZ_FloorRoof),
                $"hall decks in the set {inSet} of {huOver.Count}, overhang {over} of {huMore.Count}, all connected {all.Count} (want {Hu7All}); roofs on the open cells {YaRoofs(huOver.Select(c => FloorRoof.Below(c, map)), map)}");
            new Designator_Uninstall().DesignateThing(e);
            HuWork(map);
            TickUntil(20000, () =>
            {
                R2Feed(map);
                return GlEngine(map) == null && map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("MinifiedGravEngine")).Any();
            }, "hu_h7_mini", 60);
        }

        private static void Hu7Mini(Map map)
        {
            tickWatch = null;
            Thing mini = map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("MinifiedGravEngine")).FirstOrDefault();
            Result("H7 a colonist uninstalled the engine", mini != null && GlEngine(map) == null, $"{mini?.Position} after {ticksTotal} ticks");
            if (mini == null)
            {
                Finish();
                return;
            }
            huCell = SFF(14, 5, 1, map);
            FloorView.Set(1);
            Designator_Install di = new Designator_Install();
            SsSelect(mini);
            AcceptanceReport r = di.CanDesignateCell(huCell);
            Result("H7 the engine may be installed on the overhang deck (plain ground under it)", r.Accepted, GlRep(r));
            if (!r.Accepted)
            {
                FloorView.Set(0);
                Finish();
                return;
            }
            di.DesignateSingleCell(huCell);
            FloorView.Set(0);
            Find.DesignatorManager.Deselect();
            TickUntil(40000, () =>
            {
                R2Feed(map);
                return GlEngine(map)?.Position == huCell;
            }, "hu_h7_installed", 120);
        }

        private static void Hu7Installed(Map map)
        {
            tickWatch = null;
            SetWork(map, false);
            Building_GravEngine e = GlEngine(map);
            Result("H7 colonists installed the engine on the overhang deck", e != null && e.Position == huCell, $"{e?.Position} (want {huCell}) after {ticksTotal} ticks");
            if (e == null)
            {
                Finish();
                return;
            }
            e.nameHidden = false;
            StartTicks(250, "hu_h7_up");
        }

        private static void Hu7Up(Map map)
        {
            Building_GravEngine e = GlEngine(map);
            HashSet<IntVec3> all = e.AllConnectedSubstructure, valid = e.ValidSubstructure;
            Result("H7 the ship is found from the engine on the overhang: ground, floor 2, the overhang and the hall's floor 3, its own cells aboard",
                all.Count == Hu7All && valid.Count == Hu7All && e.OccupiedRect().Cells.All(c => valid.Contains(c)), $"all connected {all.Count}, valid {valid.Count} (want {Hu7All}): {SsFloors(valid, map)}");
            CompPilotConsole console = SsConsole(map, e);
            Result("H7 the ground console links to the engine on the overhang and is active", console != null && console.engine == e && console.CanBeActive, $"engine {console?.engine?.Position}, active {console?.CanBeActive}");
            List<Pawn> cols = Colonists(map);
            Teleport(cols[0], SF(2, 2));
            Teleport(cols[1], SF(3, 2));
            Teleport(cols[2], SFF(1, 6, 2, map));
            Teleport(cols[3], SF(-8, 4));
            foreach (Pawn p in cols)
            {
                Draft(p, true);
            }
            huA = cols[2].ThingID;
            huAOff = ShipLocal.From(cols[2].Position, e.Position, map);
            huOverOff.Clear();
            huOverOff.AddRange(huOver.Select(c => ShipLocal.From(c, e.Position, map)));
            HuRecord(map, e);
            HuLaunch(map, e, HuShipGround + huMore.Count, false);
        }

        private static void Hu7Landed(Map map, Building_GravEngine e)
        {
            HuCompare("H7", map);
            List<IntVec3> under = e.OccupiedRect().Cells.Select(c => FloorRoof.Below(c, map)).ToList();
            Result("H7 landed: the engine stands on its overhang deck on floor 2 over plain ground (the floor's roof under it)",
                ProjectZ.FloorOf(e.Position, map) == 1 && ProjectZ.GroundCellOf(e.Position, map) == sfRoot && under.All(c => map.terrainGrid.FoundationAt(c) == null && map.roofGrid.RoofAt(c) == PZDefOf.PZ_FloorRoof),
                $"engine {GlCell(e.Position, map)}, root {sfRoot}; ground under it: substructure {under.Count(c => map.terrainGrid.FoundationAt(c) != null)}, roofs {YaRoofs(under, map)}");
            List<IntVec3> hall = huOverOff.Select(o => SfAt(o, sfRoot, sfRot, map)).ToList();
            int ok = hall.Count(c => ShipDeck.At(c, map) && e.ValidSubstructure.Contains(c) && HullCompat.Open(FloorRoof.Below(c, map), map));
            Pawn rider = SfPawn(huA);
            Result("H7 landed: the whole ship again, the hall's floor-3 decks over its open floor 2, the colonist who stood on them there",
                e.ValidSubstructure.Count == Hu7All && ok == hall.Count && rider != null && rider.Map == map && rider.Position == SfAt(huAOff, sfRoot, sfRot, map),
                $"set {e.ValidSubstructure.Count} (want {Hu7All}); hall decks {ok} of {hall.Count}; colonist {rider?.Position} (want {SfAt(huAOff, sfRoot, sfRot, map)})");
        }

        // ------------------------------------------------------------------ H8 orbit vacuum table (design E)

        private static void Hu8Setup(Map map)
        {
            // rows "stacked ship" and "stairs opening": the two-level ship of shiproofatrium (decks over all of it on floor
            // 2, a hull ring there, a built roof over the floor-2 room, ship stairs inside); no door
            Building_GravEngine e = YaShip(map, sfO);
            ThingDef hull = DefDatabase<ThingDef>.GetNamed("GravshipHull");
            TerrainDef sub = DefDatabase<TerrainDef>.GetNamed("Substructure");
            SF(6, 10).GetEdifice(map)?.Destroy();
            Place(hull, ThingDefOf.Steel, SF(6, 10), map);
            // row "tall hall": a walled 3 x 3 of floor 2 (x 9..11, z 1..3) with its decks taken away: an air volume of open
            // cells only, under the ship's roof, over the ground room
            foreach ((int x, int z) in new[] { (8, 1), (8, 2), (8, 3), (8, 4), (9, 4), (10, 4), (11, 4) })
            {
                Place(hull, ThingDefOf.Steel, SFF(x, z, 1, map), map);
            }
            foreach (IntVec3 c in HuRect(map, 1, 9, 1, 11, 3))
            {
                map.terrainGrid.RemoveFoundation(c, false);
            }
            // row "hole under a real roof": one deck of the floor-2 room taken away
            map.terrainGrid.RemoveFoundation(SFF(5, 2, 1, map), false);
            // rows "overhang": two rooms on floor-2 decks east of the ship over plain ground (the ship's own floor-2 hull
            // closes their west side); the southern one with a hole in its deck (row "hole in an overhang deck", G5)
            foreach (int z0 in new[] { 6, 0 })
            {
                for (int x = 13; x <= 16; x++)
                {
                    for (int z = z0; z <= z0 + 4; z++)
                    {
                        IntVec3 u = SFF(x, z, 1, map);
                        map.terrainGrid.SetTerrain(u, PZDefOf.PZ_ShipDeck);
                        if (x == 16 || z == z0 || z == z0 + 4)
                        {
                            Place(hull, ThingDefOf.Steel, u, map);
                        }
                        else
                        {
                            map.roofGrid.SetRoof(u, RoofDefOf.RoofConstructed);
                        }
                    }
                }
            }
            map.terrainGrid.RemoveFoundation(SFF(14, 2, 1, map), false);
            // row "hollow hull": a 5 x 5 north of the ship, substructure and hull only on its ring, plain ground inside;
            // decks over all of it on floor 2 with a hull ring and a built roof (the room on the decks above)
            for (int x = 0; x <= 4; x++)
            {
                for (int z = 11; z <= 15; z++)
                {
                    bool ring = x == 0 || x == 4 || z == 11 || z == 15;
                    if (ring)
                    {
                        map.terrainGrid.SetTerrain(SF(x, z), sub);
                        Place(hull, ThingDefOf.Steel, SF(x, z), map);
                    }
                    IntVec3 u = SFF(x, z, 1, map);
                    map.terrainGrid.SetTerrain(u, PZDefOf.PZ_ShipDeck);
                    if (ring)
                    {
                        Place(hull, ThingDefOf.Steel, u, map);
                    }
                    else
                    {
                        map.roofGrid.SetRoof(u, RoofDefOf.RoofConstructed);
                    }
                }
            }
            e.ForceSubstructureDirty();
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            map.areaManager.BuildRoof.Clear();
            map.areaManager.NoRoof.Clear();
            // one colonist in a vacsuit in the ground room, the others stay home
            List<Pawn> cols = HuCrew(map, 3);
            Teleport(cols[0], SF(3, 3));
            Draft(cols[0], true);
            foreach (ThingDef def in new[] { DefDatabase<ThingDef>.GetNamed("Apparel_Vacsuit"), DefDatabase<ThingDef>.GetNamed("Apparel_VacsuitHelmet") })
            {
                cols[0].apparel.Wear((Apparel)ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null), dropReplacedApparel: false);
            }
            huA = cols[0].ThingID;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            StartTicks(250, "hu_h8_rooms");
        }

        // ground 147 + the hollow's ring 16; floor 2: the ship's 143 less the stairs' opening (3), the hall's 9 and the hole, the overhang rooms 20
        // and 19 (one deck taken away), 25 over the hollow
        private const int Hu8Ground = HuShipGround + 16, Hu8Upper = HuShipDeck - 3 - 9 - 1 + 20 + 19 + 25;
        // (the marker's cells: the ground under every ship cell)
        private const int Hu8Shadow = Hu8Ground + 20 + 19 + 9;

        private static void Hu8Rooms(Map map)
        {
            Building_GravEngine e = GlEngine(map);
            IntVec3 o = e.Position;
            huRooms.Clear();
            huRooms.Add(("the ground room of the stacked ship (under the stairs opening, the hole and the hall)", ShipLocal.From(SF(5, 2), o, map), HuWant.Sealed));
            huRooms.Add(("the floor-2 room with a deck taken away under the ship's roof (hole under a real roof, stairs opening)", ShipLocal.From(SFF(4, 6, 1, map), o, map), HuWant.Sealed));
            huRooms.Add(("the hall's air volume (open cells only, under the ship's roof)", ShipLocal.From(SFF(10, 2, 1, map), o, map), HuWant.SealedAir));
            huRooms.Add(("the room on the overhang", ShipLocal.From(SFF(14, 8, 1, map), o, map), HuWant.Sealed));
            huRooms.Add(("the room on the overhang with a hole in its deck (design G5)", ShipLocal.From(SFF(13, 1, 1, map), o, map), HuWant.Open));
            huRooms.Add(("the hollow hull's ground room (space under it in orbit)", ShipLocal.From(SF(2, 13), o, map), HuWant.Open));
            huRooms.Add(("the room on the decks over the hollow", ShipLocal.From(SFF(2, 13, 1, map), o, map), HuWant.Sealed));
            List<Room> rooms = huRooms.Select(r => ShipLocal.To(ProjectZ.GroundCellOf(o, map), r.off, map).GetRoom(map)).ToList();
            foreach (Room r in rooms)
            {
                if (r != null && !r.UsesOutdoorTemperature)
                {
                    r.Temperature = YvRoomTemp;
                    r.Vacuum = 0f;
                }
            }
            Result("H8 at home: the seven rooms of the table are seven separate indoor rooms", rooms.All(r => r != null && !r.UsesOutdoorTemperature) && rooms.Distinct().Count() == rooms.Count,
                string.Join("; ", rooms.Select((r, i) => $"{i + 1} {YvRoom(r)}")));
            HashSet<IntVec3> all = e.AllConnectedSubstructure;
            Result("H8 the whole build is one ship (stacked part, overhang rooms, the ring and the decks over the hollow)", all.Count == Hu8Ground + Hu8Upper && e.ValidSubstructure.Count == all.Count,
                $"all connected {all.Count} (want {Hu8Ground + Hu8Upper}): {SsFloors(all, map)}; valid {e.ValidSubstructure.Count}");
            huLowOff = ShipLocal.From(SF(14, 8), o, map);
            HuRecord(map, e);
            HuLaunch(map, e, Hu8Shadow, true);
        }

        private static void Hu8Landed(Map map, Building_GravEngine e)
        {
            HuCompare("H8", map);
            huExposed = new bool[huRooms.Count];
            huVacuum = new float[huRooms.Count];
            Info($"H8 in orbit on {map} (vacuum biome {map.Biome.inVacuum}), outdoors {map.mapTemperature.OutdoorTemp:0.0} C; rooms at landing: " + string.Join("; ", huRooms.Select((r, i) => $"{i + 1} {YvRoom(SfAt(r.off, sfRoot, sfRot, map).GetRoom(map))}")));
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            SeedTicks("hu_h8_check");
            TickUntil(1250, () =>
            {
                R2Feed(map);
                for (int i = 0; i < huRooms.Count; i++)
                {
                    Room r = SfAt(huRooms[i].off, sfRoot, sfRot, map).GetRoom(map);
                    if (r != null)
                    {
                        huExposed[i] |= r.ExposedToSpace;
                        huVacuum[i] = Mathf.Max(huVacuum[i], r.Vacuum);
                    }
                }
                return false;
            }, "hu_h8_check", 10);
        }

        private static void Hu8Check()
        {
            tickWatch = null;
            Map map = SfNew();
            for (int i = 0; i < huRooms.Count; i++)
            {
                (string name, IntVec3 off, HuWant want) = huRooms[i];
                IntVec3 c = SfAt(off, sfRoot, sfRot, map);
                Room r = c.GetRoom(map);
                string state = $"{YvRoom(r)}; exposed at any watched tick {huExposed[i]}, highest vacuum {huVacuum[i]:0.00}; its cell {c} f{ProjectZ.FloorOf(c, map) + 1} {HuCellStr(c, map)}";
                bool closed = r != null && !r.UsesOutdoorTemperature && !r.ExposedToSpace && !huExposed[i];
                switch (want)
                {
                    case HuWant.Sealed:
                        Result($"H8 {name}: sealed in orbit (never exposed to space, vacuum 0 over 1250 ticks)", closed && huVacuum[i] == 0f, state);
                        break;
                    case HuWant.SealedAir:
                        Result($"H8 {name}: closed in orbit (never exposed to space over 1250 ticks)", closed, state);
                        Result($"H8 {name}: its air came along (vacuum 0)", closed && huVacuum[i] == 0f, state);
                        break;
                    case HuWant.Open:
                        // (the room itself arrived as built: its cell is roofed, on a deck or under the deck's floor roof)
                        Result($"H8 {name}: arrives as built and is open to space (exposed, vacuum 1)",
                            r != null && map.roofGrid.RoofAt(c) != null && (ShipDeck.At(c, map) || map.roofGrid.RoofAt(c) == PZDefOf.PZ_FloorRoof) && r.ExposedToSpace && r.Vacuum == 1f, state);
                        break;
                }
            }
            IntVec3 g = SfAt(huLowOff, sfRoot, sfRot, map);
            Room outside = g.GetRoom(map);
            Result("H8 the ground under the overhang room: outside (no ship cell, exposed), under the deck's floor roof", map.terrainGrid.FoundationAt(g) == null && map.roofGrid.RoofAt(g) == PZDefOf.PZ_FloorRoof && (outside == null || outside.ExposedToSpace),
                $"{g}: {HuCellStr(g, map)}; {YvRoom(outside)}");
            Wait(10, "hu_end");
        }

        // ------------------------------------------------------------------ H9 the engine's own block over the limit

        // (from 40: under that the first fill ends inside the engine's own columns, and, as before the 3D hull and as
        // the game's own fill with fewer cells than it needs to cover an engine, the block is not whole)
        private static readonly int[] Hu9Limits = { 40, 100, 200, 300, 400, 482, 483, 499, 500, 600 };

        // The engine taken from where it stands and placed at cell at (as mode shipdump's engine-up variant places it).
        private static Building_GravEngine Hu9Engine(Map map, IntVec3 at)
        {
            GlEngine(map)?.Destroy(DestroyMode.Vanish);
            Place(ThingDefOf.GravEngine, null, at, map, Rot4.North);
            Building_GravEngine e = GlEngine(map);
            e.nameHidden = false;
            e.ForceSubstructureDirty();
            return e;
        }

        // The cut sets at lowered limits (the game's own GetConnectedSubstructure, which the product answers): each has
        // every cell under the engine, is linked to it and is as large as the limit allows.
        private static void Hu9Sets(string tag, Building_GravEngine e, Map map)
        {
            List<IntVec3> block = e.OccupiedRect().Cells.ToList();
            HashSet<IntVec3> full = new HashSet<IntVec3>();
            GravshipUtility.GetConnectedSubstructure(e, full, int.MaxValue, true);
            foreach (int n in Hu9Limits)
            {
                HashSet<IntVec3> cut = new HashSet<IntVec3>();
                GravshipUtility.GetConnectedSubstructure(e, cut, n, true);
                int aboard = block.Count(c => cut.Contains(c)), loose = HuUnlinked(cut, e, map);
                Result($"{tag} limit {n}: every cell under the engine is aboard, the set linked to it, as large as the limit allows",
                    aboard == block.Count && loose == 0 && cut.Count == Math.Min(n, full.Count),
                    $"{cut.Count} cells ({SsFloors(cut, map)}) of {full.Count}; under the engine {aboard} of {block.Count}{(aboard < block.Count ? ", missing " + string.Join(" ", block.Where(c => !cut.Contains(c))) : "")}; not linked {loose}");
            }
        }

        private static void Hu9Block(Map map)
        {
            TerrainDef sub = DefDatabase<TerrainDef>.GetNamed("Substructure");
            // B: the engine on floor 2 at (4, 6); the ground cell under its south-west corner (3, 5) stays substructure, the
            // four ground cells round it go: it is cut off from the hull on the ground (two of them are under the block)
            Building_GravEngine e = Hu9Engine(map, SSF(4, 6, 1, map));
            IntVec3 lone = SS(3, 5);
            IntVec3[] gap = { SS(2, 5), SS(4, 5), SS(3, 4), SS(3, 6) };
            foreach (IntVec3 c in gap)
            {
                map.terrainGrid.RemoveFoundation(c, false);
            }
            e.ForceSubstructureDirty();
            Result("H9 B fixture: the engine on floor 2, the ground cell under a corner of its block substructure with no substructure next to it; it is linked from above",
                ProjectZ.FloorOf(e.Position, map) == 1 && SlabSupport.Gravship(lone, map) && gap.All(c => map.terrainGrid.FoundationAt(c) == null) && e.AllConnectedSubstructure.Contains(lone)
                    && e.OccupiedRect().Cells.All(c => ShipDeck.At(c, map)),
                $"engine {GlCell(e.Position, map)}; {lone} {HuCellStr(lone, map)}; round it {string.Join(" ", gap.Select(c => Def(map.terrainGrid.FoundationAt(c))))}; all connected {e.AllConnectedSubstructure.Count} ({SsFloors(e.AllConnectedSubstructure, map)})");
            Hu9Sets("H9 B", e, map);
            foreach (IntVec3 c in gap)
            {
                map.terrainGrid.SetTerrain(c, sub);
            }
            // A: the engine on floor 3 at (4, 6), decks on floors 2 and 3 under and round it; the ground cell under its
            // south-west corner is taken away (a hollow)
            e = Hu9Engine(map, SSF(4, 6, 2, map));
            map.terrainGrid.RemoveFoundation(lone, false);
            e.ForceSubstructureDirty();
            Result("H9 A fixture: the engine on floor 3, decks on floors 2 and 3 under it, the ground under a corner of its block no substructure",
                ProjectZ.FloorOf(e.Position, map) == 2 && map.terrainGrid.FoundationAt(lone) == null && ShipDeck.At(SSF(3, 5, 1, map), map) && e.OccupiedRect().Cells.All(c => ShipDeck.At(c, map)),
                $"engine {GlCell(e.Position, map)}; {lone} {HuCellStr(lone, map)}; all connected {e.AllConnectedSubstructure.Count} ({SsFloors(e.AllConnectedSubstructure, map)})");
            Hu9Sets("H9 A", e, map);
            int limit = (int)e.GetStatValue(StatDefOf.SubstructureSupport);
            HashSet<IntVec3> valid = e.ValidSubstructure;
            Result("H9 A the ship as it will launch is over its own limit, and the engine is on it (the game's own OnValidSubstructure)",
                e.AllConnectedSubstructure.Count > limit && valid.Count == limit && e.OnValidSubstructure(e),
                $"all connected {e.AllConnectedSubstructure.Count}, limit {limit}, valid {valid.Count} ({SsFloors(valid, map)}), engine on valid substructure {e.OnValidSubstructure(e)}");
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            StartTicks(250, "hu_h9_fly");
        }

        // The shipset ship launched for real to a neighbouring tile (as mode shipdump flies it), landing through hu_landed.
        private static void Hu9Fly(Map map, Action mid)
        {
            Building_GravEngine e = GlEngine(map);
            List<PlanetTile> near = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(map.Tile, near);
            PlanetTile dest = near.FirstOrDefault(t => Find.WorldObjects.MapParentAt(t) == null && !Find.WorldGrid[t].WaterCovered && Find.WorldGrid[t].hilliness != Hilliness.Impassable);
            if (e == null || !dest.Valid)
            {
                Result($"{huCase} an engine and a neighbouring tile to fly to", false, $"engine {e != null}, tile {dest.Valid}");
                Finish();
                return;
            }
            List<Pawn> cols = Colonists(map);
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], SS(11 + i, 3));
                Draft(cols[i], true);
            }
            sfHomeTile = map.Tile;
            sfNewTile = dest;
            sfLeg = 1;
            sfSaved = true;
            // (the marker's cells: the ground under every ship cell)
            sfFootprint = e.ValidSubstructure.Select(c => ProjectZ.GroundCellOf(c, map)).Distinct().Count();
            sfLandedStage = "hu_landed";
            sfMidHook = mid;
            Info($"{huCase} launch: engine {GlCell(e.Position, map)}, valid {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)}) of {e.AllConnectedSubstructure.Count}, marker cells {sfFootprint}");
            SfLaunch(map, e, dest);
        }

        private static void Hu9Landed(Map map, Building_GravEngine e)
        {
            HashSet<IntVec3> valid = e.ValidSubstructure;
            Result("H9 A landed: the engine flew with the ship and stands on floor 3, every cell under it a ship cell",
                ProjectZ.FloorOf(e.Position, map) == 2 && e.OccupiedRect().Cells.All(c => valid.Contains(c)) && e.OnValidSubstructure(e),
                $"engine {GlCell(e.Position, map)}, cells under it in the set {e.OccupiedRect().Cells.Count(c => valid.Contains(c))} of 9, set {valid.Count} ({SsFloors(valid, map)})");
            Result("H9 A landed: no floor-tagged cell reached the map (safety net)", ShipLocal.Nets == 0, $"{ShipLocal.Nets}");
        }

        // ------------------------------------------------------------------ H10 a hall's roof over a one-storey ship

        private static int huRoomOpen;

        private static void Hu10Setup(Map map)
        {
            Building_GravEngine e = HuShip(map);
            HuAccess(map, false);
            // the hall: the upper storey of an ordinary building west of the ship (wood walls on the floor-2 walkway); its
            // roof reaches over the ship's west side, on the open cells of floor 2
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SFF(-1, 3, 1, map), map);
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SFF(-1, 7, 1, map), map);
            huOver.Clear();
            huOver.AddRange(HuRect(map, 1, 0, 2, 2, 8));
            foreach (IntVec3 c in huOver)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            e.ForceSubstructureDirty();
            HashSet<IntVec3> valid = e.ValidSubstructure;
            Result("H10 fixture: a one-storey ship; the roof of the building beside it lies on open cells straight over ship cells",
                valid.All(c => ProjectZ.FloorOf(c, map) == 0) && huOver.All(c => HullCompat.OverShip(c, map) && valid.Contains(HullCompat.Down(c, map)) && map.roofGrid.RoofAt(c) == RoofDefOf.RoofConstructed),
                $"ship {valid.Count} ({SsFloors(valid, map)}); roof cells over it {huOver.Count(c => HullCompat.OverShip(c, map))} of {huOver.Count}: {YaRoofs(huOver, map)}; under them {YaRoofs(YaBelow(huOver, map), map)}");
            Room hall = huOver[0].GetRoom(map);
            huRoomOpen = hall?.OpenRoofCount ?? -1;
            huOverOff.Clear();
            huOverOff.AddRange(huOver.Select(c => ShipLocal.From(c, e.Position, map)));
            List<Pawn> cols = HuCrew(map, 3);
            Teleport(cols[0], SF(5, 2));
            Teleport(cols[1], SF(6, 2));
            Teleport(cols[2], SF(-8, 4));
            foreach (Pawn p in cols)
            {
                Draft(p, true);
            }
            HuRecord(map, e);
            sfMidHook = Hu10Mid;
            sfLandHook = Hu10Land;
            HuLaunch(map, e, HuShipGround, false);
        }

        private static void Hu10Mid()
        {
            Map home = SfHome();
            Gravship g = Current.Game.Gravship ?? Find.WorldObjects.AllWorldObjects.OfType<Gravship>().FirstOrDefault();
            if (home == null || g == null)
            {
                Result("H10 in flight: the old map and the flying ship", false, $"map {home}, gravship {g != null}");
                return;
            }
            List<(IntVec3, RoofDef)> roofs = g.Roofs.ToList();
            int keys = huOverOff.Count(o => roofs.Any(r => r.Item1 == o && r.Item2 == RoofDefOf.RoofConstructed));
            Result("H10 after the take-off: the roof over the ship flew with it (the ship holds it floor-tagged, with no deck up there), none of it is left at the old site",
                keys == huOverOff.Count && g.Foundations.Keys.All(k => k.y == 0) && huOver.All(c => home.roofGrid.RoofAt(c) == null),
                $"roof keys on floor 2 {keys} of {huOverOff.Count}, deck keys upstairs {g.Foundations.Keys.Count(k => k.y != 0)}; at the old site {YaRoofs(huOver, home)}");
            Result("H10 the ship's height counts the roof it carries (one floor up)", ShipLanding.Height(g) == 1, $"height {ShipLanding.Height(g)}");
            Room hall = huOver[0].GetRoom(home);
            int open = hall?.OpenRoofCount ?? -1, counted = hall == null ? -1 : hall.Cells.Count(c => !home.roofGrid.Roofed(c));
            Result("H10 the room the roof was taken from has its open-roof count right (the count it keeps = its cells without a roof)", hall != null && open == counted,
                $"room {hall?.ID}: open roof {open}, counted {counted}; before the flight {huRoomOpen} (+{huOver.Count} cells opened)");
        }

        private static void Hu10Land(WorldComponent_GravshipController ctl, GravshipLandingMarker marker)
        {
            Map map = marker.Map;
            HuMarker("H10", marker);
            if (!HuSpot(ctl, marker, Rot4.East, out Designator_MoveGravship d, out IntVec3 root, out IntVec3 click))
            {
                Result("H10 a spot for the ship turned East near the marker", false, "none within 14 cells");
                marker.GravshipRotation = Rot4.North;
                SfConfirm(ctl, marker);
                return;
            }
            AcceptanceReport fine = d.CanDesignateCell(click);
            // too few floors for the roof the ship carries: the map's count lowered for the check only
            AnnexMapComponent an = ProjectZ.Annex(map);
            int floors = an.floors;
            an.floors = 1;
            AcceptanceReport few = d.CanDesignateCell(click);
            an.floors = floors;
            Result("H10 landing accepted on open ground, refused where the map has no floor for the roof the ship carries",
                fine.Accepted && !few.Accepted && few.Reason == "PZ_ShipNotEnoughFloors".Translate(), $"with {floors} floors: {GlRep(fine)}; with 1 floor: {GlRep(few)}");
            d.DesignateSingleCell(click);
            SfConfirm(ctl, marker);
        }

        private static void Hu10Landed(Map map, Building_GravEngine e)
        {
            HuCompare("H10", map);
            List<IntVec3> roof = huOverOff.Select(o => SfAt(o, sfRoot, sfRot, map)).ToList();
            Result("H10 landed: the roof is over the ship again, on the open cells of floor 2",
                roof.All(c => ProjectZ.FloorOf(c, map) == 1 && HullCompat.OverShip(c, map) && map.roofGrid.RoofAt(c) == RoofDefOf.RoofConstructed), $"{YaRoofs(roof, map)}; open cells over the ship {roof.Count(c => HullCompat.OverShip(c, map))} of {roof.Count}");
        }

        // ------------------------------------------------------------------ H11 open cells under a deck that stays behind

        private static IntVec3 Hu11G => SS(0, 4);

        private static void Hu11Column(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            Building_GravEngine e = GlEngine(map);
            if (an == null || an.floors < 4 || e == null)
            {
                Result("H11 setup", false, $"needs 4 floors; floors {an?.floors}, engine {e != null}");
                Finish();
                return;
            }
            IntVec3 g = Hu11G, o1 = SSF(0, 4, 1, map), o2 = SSF(0, 4, 2, map), top = SSF(0, 4, 3, map);
            // a roof holder that stays behind for the deck on floor 4: a wood wall on a floor-3 slab west of the column, the
            // slab's own roof held by a wood wall on the floor-2 walkway (whose roofs the ground walls hold)
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SSF(-1, 3, 1, map), map);
            map.terrainGrid.SetTerrain(SSF(-1, 4, 2, map), PZDefOf.PZ_UpperFloor);
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, SSF(-1, 4, 2, map), map);
            // the column: the decks of floors 3 and 2 taken away, a deck on floor 4
            map.terrainGrid.RemoveFoundation(o2, false);
            map.terrainGrid.RemoveFoundation(o1, false);
            map.terrainGrid.SetTerrain(top, PZDefOf.PZ_ShipDeck);
            // (the column rule for the cells under the new cover: asked again, as when a roof is taken away there)
            map.roofGrid.SetRoof(o1, null);
            map.roofGrid.SetRoof(g, null);
            e.ForceSubstructureDirty();
            HashSet<IntVec3> valid = e.ValidSubstructure, all = e.AllConnectedSubstructure;
            Result("H11 fixture: a ship cell, two open cells over it (the hidden roof, then the ceiling of the deck above) and a deck on floor 4 that is linked to the ship but past its limit",
                valid.Contains(g) && HullCompat.Open(o1, map) && HullCompat.Open(o2, map) && ShipDeck.At(top, map) && all.Contains(top) && !valid.Contains(top)
                    && map.roofGrid.RoofAt(o1) == PZDefOf.PZ_StairwellRoof && map.roofGrid.RoofAt(o2) == PZDefOf.PZ_FloorRoof,
                $"ground {HuCellStr(g, map)} (valid {valid.Contains(g)}); floor 2 {HuCellStr(o1, map)}; floor 3 {HuCellStr(o2, map)}; floor 4 {HuCellStr(top, map)} (connected {all.Contains(top)}, valid {valid.Contains(top)}); valid {valid.Count} of {all.Count}");
            huOverOff.Clear();
            huOverOff.AddRange(new[] { g, o1, o2, top }.Select(c => ShipLocal.From(c, e.Position, map)));
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            StartTicks(250, "hu_h11_fly");
        }

        private static void Hu11Mid()
        {
            Map home = SfHome();
            Gravship ship = Current.Game.Gravship ?? Find.WorldObjects.AllWorldObjects.OfType<Gravship>().FirstOrDefault();
            if (home == null || ship == null)
            {
                Result("H11 in flight: the old map and the flying ship", false, $"map {home}, gravship {ship != null}");
                return;
            }
            IntVec3 g = Hu11G, o1 = SSF(0, 4, 1, home), o2 = SSF(0, 4, 2, home), top = SSF(0, 4, 3, home);
            Result("H11 after the take-off: the deck that stayed stands over its open cells, their roofs still there (the hidden roof under the deck's ceiling); the ship cell that left under them has the hidden roof of a covered column",
                ShipDeck.At(top, home) && home.roofGrid.RoofAt(o2) == PZDefOf.PZ_FloorRoof && home.roofGrid.RoofAt(o1) == PZDefOf.PZ_StairwellRoof
                    && home.terrainGrid.FoundationAt(g) == null && home.roofGrid.RoofAt(g) == PZDefOf.PZ_StairwellRoof,
                $"floor 4 {HuCellStr(top, home)}; floor 3 {HuCellStr(o2, home)}; floor 2 {HuCellStr(o1, home)}; ground {HuCellStr(g, home)}");
            List<(IntVec3, RoofDef)> roofs = ship.Roofs.ToList();
            int taken = roofs.Count(r => r.Item1 == huOverOff[1] || r.Item1 == huOverOff[2]);
            Result("H11 the flying ship carries the ship cell, and no roof of the open cells under the deck that stayed",
                ship.Foundations.ContainsKey(huOverOff[0]) && !ship.Foundations.ContainsKey(huOverOff[3]) && taken == 0, $"ship cell key {ship.Foundations.ContainsKey(huOverOff[0])}, the deck's key {ship.Foundations.ContainsKey(huOverOff[3])}, roof keys of the run {taken}");
        }

        private static void Hu11Landed(Map map, Building_GravEngine e)
        {
            IntVec3 g = SfAt(huOverOff[0], sfRoot, sfRot, map), o1 = SfAt(huOverOff[1], sfRoot, sfRot, map), o2 = SfAt(huOverOff[2], sfRoot, sfRot, map);
            Result("H11 landed: the ship cell is there under a roof; nothing of the run landed: no roof on the open cells over it",
                SlabSupport.Gravship(g, map) && map.roofGrid.Roofed(g) && HullCompat.Open(o1, map) && HullCompat.Open(o2, map) && map.roofGrid.RoofAt(o1) == null && map.roofGrid.RoofAt(o2) == null,
                $"ground {HuCellStr(g, map)}; floor 2 {HuCellStr(o1, map)}; floor 3 {HuCellStr(o2, map)}");
            Result("H11 landed: no floor-tagged cell reached the map (safety net)", ShipLocal.Nets == 0, $"{ShipLocal.Nets}");
        }
    }
}
