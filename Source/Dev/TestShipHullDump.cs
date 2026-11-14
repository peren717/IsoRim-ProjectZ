using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ProjectZ.Dev
{
    // The old-ship identity dump (3D connected hull, issue 5; isorim-pz-hull3d-design.md section F): what a stacked ship
    // is, cell for cell and in order, written to <renders>\<tag>\shipdump.txt so the same fixtures can be compared between
    // two builds of the product on one harness (base DLL vs head: the files must be identical).
    //   Switch -projectzshipdump on the flights of the existing modes (their own shared flight code, TestShipFly.SfLaunch /
    //   SfFly): shipfly (both legs) and shipstairs -projectzshipstack; and the mode shipdump, which flies the shipset
    //   fixture (-projectzfloors=3; -projectzshipdumpengineup: its engine standing on a floor-2 deck, placed, as S5 leaves
    //   it) to a neighbouring tile.
    //   Written, per leg:
    //     before the launch  the ship set in fill order (the engine's valid and all-connected sets, Api.ShipCells with and
    //                        without the footprint), and the cut sets at lowered limits (the game's own
    //                        GravshipUtility.GetConnectedSubstructure, which the product answers), each in fill order;
    //     in flight          everything the vanilla Gravship object holds (foundations, terrains, roofs, colours, gases,
    //                        things, pawns, thrusters, exterior doors, room temperatures, extender positions, bounds,
    //                        occupied rects, engineToCenter, launch direction), sorted (their order is no contract);
    //     after the landing  the landed ship set in fill order, and every cell of the landing site on every floor that
    //                        holds anything (foundation, terrain, under layer, roof, things, zone), and the colonists.
    //   Cells are ship-relative: x and z from the engine's ground column, the floor in y.
    //   shipdump_oldsite.txt (information only, no identity contract: left-behind decks follow the new rule): the old
    //   site's cells after the take-off.
    //   Mode shipdump also takes two pictures of the old site after the landing (hd_oldsite_top.png, hd_oldsite_ground.png)
    //   and checks the decks the limit left behind against the game's roof rule (HdLeftBehind).
    //   Only product entry points that exist before and after the hull change are named (the Gravship object, the
    //   engine's sets, GravshipUtility.GetConnectedSubstructure, Api.ShipCells, ProjectZ.GroundCellOf / FloorOf /
    //   CellOnFloor).
    public static partial class TestRunner
    {
        private static bool HdOn => mode == "shipdump" || GenCommandLine.CommandLineArgPassed("projectzshipdump");

        private static bool hdStarted;
        private static PlanetTile hdFromTile = PlanetTile.Invalid;
        private static IntVec3 hdFromEngine;
        private static CellRect hdFromBox;
        private static int hdPastLimit;

        // Lowered limits for the cut sets, besides the floor boundaries of the fixture's own set (HdLimits).
        private static readonly int[] HdFixedLimits = { 1, 2, 9, 10, 40, 100, 200, 300, 400, 500 };

        // How far round the ship's ground rect the landing site and the old site are read.
        private const int HdMargin = 1;

        private static string HdPath(string name) => Path.Combine(outDir, name);

        private static void HdWrite(string file, StringBuilder sb)
        {
            if (!hdStarted)
            {
                hdStarted = true;
                foreach (string f in new[] { "shipdump.txt", "shipdump_oldsite.txt" })
                {
                    if (File.Exists(HdPath(f)))
                    {
                        File.Delete(HdPath(f));
                    }
                }
            }
            File.AppendAllText(HdPath(file), sb.ToString().Replace("\r\n", "\n"));
        }

        private static string HdKey(IntVec3 k) => $"{k.x},{k.y},{k.z}";

        private static IntVec3 HdLocal(IntVec3 c, IntVec3 engineGround, Map map)
        {
            IntVec3 g = ProjectZ.GroundCellOf(c, map);
            return new IntVec3(g.x - engineGround.x, ProjectZ.FloorOf(c, map), g.z - engineGround.z);
        }

        private static IEnumerable<IntVec3> HdSorted(IEnumerable<IntVec3> keys) => keys.OrderBy(k => k.y).ThenBy(k => k.z).ThenBy(k => k.x);

        // A list of cells in the order given, 12 to a line.
        private static void HdCells(StringBuilder sb, string title, IEnumerable<IntVec3> local)
        {
            List<IntVec3> l = local.ToList();
            sb.Append("## ").Append(title).Append(": ").Append(l.Count).Append(" cells (floors ")
                .Append(string.Join(" ", l.GroupBy(k => k.y).OrderBy(g => g.Key).Select(g => g.Key + ":" + g.Count()))).Append(")\n");
            for (int i = 0; i < l.Count; i += 12)
            {
                sb.Append(string.Join(" ", l.Skip(i).Take(12).Select(HdKey))).Append('\n');
            }
        }

        private static void HdLines(StringBuilder sb, string title, IEnumerable<string> lines)
        {
            List<string> l = lines.ToList();
            sb.Append("## ").Append(title).Append(": ").Append(l.Count).Append('\n');
            foreach (string s in l)
            {
                sb.Append(s).Append('\n');
            }
        }

        private static List<int> HdLimits(HashSet<IntVec3> full, IntVec3 eg, Map map)
        {
            List<int> perFloor = full.Select(c => ProjectZ.FloorOf(c, map)).GroupBy(k => k).OrderBy(g => g.Key).Select(g => g.Count()).ToList();
            List<int> limits = new List<int>(HdFixedLimits);
            int sum = 0;
            foreach (int n in perFloor)
            {
                sum += n;
                limits.AddRange(new[] { sum - 1, sum, sum + 1 });
            }
            return limits.Where(n => n >= 1 && n < full.Count).Distinct().OrderBy(n => n).ToList();
        }

        // Before the launch: the set in fill order, and the cut sets.
        private static void HdBefore(Map map, Building_GravEngine e, string label)
        {
            IntVec3 eg = ProjectZ.GroundCellOf(e.Position, map);
            StringBuilder sb = new StringBuilder();
            sb.Append($"# ship identity dump: mode {mode}, {label}; engine on floor {ProjectZ.FloorOf(e.Position, map) + 1}, limit {(int)e.GetStatValue(StatDefOf.SubstructureSupport)}\n");
            HdCells(sb, $"{label} set: valid", e.ValidSubstructure.Select(c => HdLocal(c, eg, map)));
            HdCells(sb, $"{label} set: all connected", e.AllConnectedSubstructure.Select(c => HdLocal(c, eg, map)));
            HashSet<IntVec3> full = new HashSet<IntVec3>();
            Api.ShipCells(e, full, true);
            HdCells(sb, $"{label} set: Api.ShipCells", full.Select(c => HdLocal(c, eg, map)));
            HashSet<IntVec3> loose = new HashSet<IntVec3>();
            Api.ShipCells(e, loose, false);
            HdCells(sb, $"{label} set: Api.ShipCells without the footprint", loose.Select(c => HdLocal(c, eg, map)));
            foreach (int limit in HdLimits(full, eg, map))
            {
                HashSet<IntVec3> cut = new HashSet<IntVec3>();
                GravshipUtility.GetConnectedSubstructure(e, cut, limit, true);
                HdCells(sb, $"{label} cut at limit {limit}", cut.Select(c => HdLocal(c, eg, map)));
            }
            HdWrite("shipdump.txt", sb);
            hdFromTile = map.Tile;
            hdFromEngine = eg;
            hdFromBox = CellRect.FromCellList(full.Select(c => ProjectZ.GroundCellOf(c, map))).ExpandedBy(HdMargin).ClipInsideRect(ProjectZ.GroundRect(map));
            hdPastLimit = e.AllConnectedSubstructure.Count(c => !e.ValidSubstructure.Contains(c) && ShipDeck.Upper(c, map));
            Info($"ship dump {label}: set {e.ValidSubstructure.Count} valid, {full.Count} unlimited, written to {HdPath("shipdump.txt")}");
        }

        // In flight: the Gravship object.
        private static void HdCapture(string label)
        {
            Gravship g = Current.Game.Gravship ?? Find.WorldObjects.AllWorldObjects.OfType<Gravship>().FirstOrDefault();
            StringBuilder sb = new StringBuilder();
            if (g == null)
            {
                sb.Append($"## {label} gravship: none\n");
                HdWrite("shipdump.txt", sb);
                return;
            }
            sb.Append($"## {label} gravship: rotation {g.Rotation.AsInt}, bounds {g.Bounds}, engineToCenter {g.engineToCenter.x:0.000},{g.engineToCenter.y:0.000},{g.engineToCenter.z:0.000}, launch direction {HdKey(g.launchDirection)}\n");
            HdLines(sb, $"{label} gravship foundations", HdSorted(g.Foundations.Keys).Select(k => $"{HdKey(k)} {g.Foundations[k]?.defName ?? "-"}"));
            HdLines(sb, $"{label} gravship terrains", HdSorted(g.Terrains.Keys).Select(k => $"{HdKey(k)} {g.Terrains[k]?.defName ?? "-"}"));
            HdLines(sb, $"{label} gravship roofs", g.Roofs.OrderBy(r => r.Item1.y).ThenBy(r => r.Item1.z).ThenBy(r => r.Item1.x).Select(r => $"{HdKey(r.Item1)} {r.Item2?.defName ?? "-"}"));
            HdLines(sb, $"{label} gravship terrain colours", g.TerrainColors.Where(r => r.Item2 != null).OrderBy(r => r.Item1.y).ThenBy(r => r.Item1.z).ThenBy(r => r.Item1.x).Select(r => $"{HdKey(r.Item1)} {r.Item2.defName}"));
            HdLines(sb, $"{label} gravship terrain designations", g.TerrainDesignations.Where(r => r.Item2 != null).OrderBy(r => r.Item1.y).ThenBy(r => r.Item1.z).ThenBy(r => r.Item1.x).Select(r => $"{HdKey(r.Item1)} {r.Item2.def?.defName}"));
            HdLines(sb, $"{label} gravship gases", g.Gases.Where(r => r.Item2 != 0).OrderBy(r => r.Item1.y).ThenBy(r => r.Item1.z).ThenBy(r => r.Item1.x).Select(r => $"{HdKey(r.Item1)} {r.Item2}"));
            HdLines(sb, $"{label} gravship things", g.ThingPlacements.Select(kv => $"{HdKey(kv.Value.local)} {kv.Key.def.defName} rot {kv.Value.rotation.AsInt}").OrderBy(s => s, StringComparer.Ordinal));
            HdLines(sb, $"{label} gravship pawns", g.PawnPlacements.Select(kv => $"{HdKey(kv.Value.local)} {kv.Key.kindDef?.defName} drafted {kv.Value.drafted}").OrderBy(s => s, StringComparer.Ordinal));
            HdLines(sb, $"{label} gravship thrusters", g.ThrusterPlacements.Select(kv => $"{HdKey(kv.Value.local)} {kv.Key.def.defName} rot {kv.Value.rotation.AsInt}").OrderBy(s => s, StringComparer.Ordinal));
            HdLines(sb, $"{label} gravship exterior doors", g.ExteriorDoorPlacements.Select(kv => $"{HdKey(kv.Value.local)} {kv.Key.def.defName} rot {kv.Value.rotation.AsInt}").OrderBy(s => s, StringComparer.Ordinal));
            HdLines(sb, $"{label} gravship extender positions", HdSorted(g.GravFieldExtenderPositions).Select(HdKey));
            HdLines(sb, $"{label} gravship occupied rects", g.OccupiedRects.Select(r => r.ToString()).OrderBy(s => s, StringComparer.Ordinal));
            HdLines(sb, $"{label} gravship room temperatures", g.RoomTemperatures.OrderBy(r => r.local.y).ThenBy(r => r.local.z).ThenBy(r => r.local.x).Select(r => $"{HdKey(r.local)} {r.temperature:0.0} C vacuum {r.vacuum:0.00}"));
            HdLines(sb, $"{label} gravship powered", g.PoweredOn.Select(kv => $"{kv.Key.def.defName} {kv.Value}").GroupBy(s => s).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key} x{x.Count()}"));
            HdLines(sb, $"{label} gravship connect parents", g.ConnectParents.Select(kv => $"{kv.Key.def.defName} -> {kv.Value?.def.defName}").GroupBy(s => s).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key} x{x.Count()}"));
            HdWrite("shipdump.txt", sb);
            // the old site (information only)
            Map old = Find.Maps.FirstOrDefault(m => m.Tile == hdFromTile);
            StringBuilder so = new StringBuilder();
            if (old == null)
            {
                so.Append($"## {label} old site: its map is gone\n");
            }
            else
            {
                HdSite(so, $"{label} old site after the take-off", old, hdFromBox, hdFromEngine);
            }
            HdWrite("shipdump_oldsite.txt", so);
        }

        // After the landing: the set in fill order and the site.
        private static void HdLanded(string label)
        {
            StringBuilder sb = new StringBuilder();
            Map map = Find.Maps.FirstOrDefault(m => GlEngine(m) != null);
            Building_GravEngine e = map == null ? null : GlEngine(map);
            if (e == null)
            {
                sb.Append($"## {label} landed: no engine on any map\n");
                HdWrite("shipdump.txt", sb);
                return;
            }
            IntVec3 eg = ProjectZ.GroundCellOf(e.Position, map);
            sb.Append($"## {label} landed: rotation {sfRot.AsInt}, engine on floor {ProjectZ.FloorOf(e.Position, map) + 1}, facing {e.Rotation.AsInt}\n");
            HdCells(sb, $"{label} landed set: valid", e.ValidSubstructure.Select(c => HdLocal(c, eg, map)));
            HdCells(sb, $"{label} landed set: all connected", e.AllConnectedSubstructure.Select(c => HdLocal(c, eg, map)));
            CellRect box = CellRect.FromCellList(e.AllConnectedSubstructure.Select(c => ProjectZ.GroundCellOf(c, map))).ExpandedBy(HdMargin).ClipInsideRect(ProjectZ.GroundRect(map));
            HdSite(sb, $"{label} landed cells", map, box, eg);
            HdWrite("shipdump.txt", sb);
            Info($"ship dump {label}: landed on {map}, set {e.ValidSubstructure.Count}");
            if (mode == "shipdump")
            {
                HdLeftBehind();
            }
        }

        // (3D hull, issue 5; the design's shipset S2 row, with a real launch.) The decks the limit left out of the ship stay
        // at the old site only while the game's own roof rule holds them: after the flight every one still standing has
        // its floor roof under it, in range of a roof holder (RoofCollapseUtility.WithinRangeOfRoofHolder); the others fell.
        private static void HdLeftBehind()
        {
            Map old = Find.Maps.FirstOrDefault(m => m.Tile == hdFromTile);
            AnnexMapComponent an = old == null ? null : ProjectZ.Annex(old);
            if (an == null)
            {
                Result("ship dump: the old map after the flight", false, "gone");
                return;
            }
            int stand = 0, loose = 0;
            string first = null;
            for (int k = 1; k < an.floors; k++)
            {
                foreach (IntVec3 g in hdFromBox)
                {
                    IntVec3 c = ProjectZ.CellOnFloor(g, k, old);
                    if (!c.IsValid || !ShipDeck.At(c, old))
                    {
                        continue;
                    }
                    stand++;
                    IntVec3 below = FloorRoof.Below(c, old);
                    if (old.roofGrid.RoofAt(below) != PZDefOf.PZ_FloorRoof || !RoofCollapseUtility.WithinRangeOfRoofHolder(below, old))
                    {
                        loose++;
                        first = first ?? $"{c} f{k + 1}: roof below {old.roofGrid.RoofAt(below)?.defName ?? "none"}";
                    }
                }
            }
            Result("ship dump: of the decks the limit left behind, every one still standing at the old site is held by the game's roof rule (its floor roof under it, a holder in range); the others fell",
                hdPastLimit > 0 && loose == 0, $"{hdPastLimit} upper decks past the limit before the launch: {stand} stand, {hdPastLimit - stand} fell; standing without a held roof {loose}; {first}");
        }

        // Every cell of a ground rect on every floor that holds anything, and the colonists there.
        private static void HdSite(StringBuilder sb, string title, Map map, CellRect box, IntVec3 eg)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            int floors = an?.floors ?? 1;
            TerrainGrid tg = map.terrainGrid;
            List<string> lines = new List<string>();
            List<string> pawns = new List<string>();
            for (int k = 0; k < floors; k++)
            {
                for (int z = box.minZ; z <= box.maxZ; z++)
                {
                    for (int x = box.minX; x <= box.maxX; x++)
                    {
                        IntVec3 g = new IntVec3(x, 0, z);
                        IntVec3 c = k == 0 ? g : ProjectZ.CellOnFloor(g, k, map);
                        if (!c.IsValid || !c.InBounds(map))
                        {
                            continue;
                        }
                        TerrainDef found = tg.FoundationAt(c), top = tg.TopTerrainAt(c), under = tg.UnderTerrainAt(c);
                        RoofDef roof = map.roofGrid.RoofAt(c);
                        List<string> things = new List<string>();
                        foreach (Thing t in map.thingGrid.ThingsListAtFast(c))
                        {
                            if (t.Position != c || t is Mote || t is Filth)
                            {
                                continue;
                            }
                            if (t is Pawn p)
                            {
                                if (p.Faction == Faction.OfPlayer && p.RaceProps.Humanlike)
                                {
                                    pawns.Add($"{x - eg.x},{k},{z - eg.z} {p.kindDef?.defName}");
                                }
                                continue;
                            }
                            things.Add($"{t.def.defName}/{t.Rotation.AsInt}" + (t.stackCount > 1 ? "x" + t.stackCount : ""));
                        }
                        Zone zone = map.zoneManager.ZoneAt(c);
                        bool empty = things.Count == 0 && zone == null && roof == null && found == null && (k == 0 || top == PZDefOf.PZ_OpenAir);
                        if (empty)
                        {
                            continue;
                        }
                        things.Sort(StringComparer.Ordinal);
                        lines.Add($"{x - eg.x},{k},{z - eg.z} F={found?.defName ?? "-"} T={top?.defName ?? "-"} U={under?.defName ?? "-"} R={roof?.defName ?? "-"}"
                            + (things.Count > 0 ? " [" + string.Join(" ", things) + "]" : "") + (zone != null ? " zone=" + zone.GetType().Name : ""));
                    }
                }
            }
            HdLines(sb, title, lines);
            pawns.Sort(StringComparer.Ordinal);
            HdLines(sb, title + ": colonists", pawns);
        }

        // Mode shipdump, after the landing (so long after the take-off, nothing is falling any more): pictures of the old
        // site, every floor in view and the ground alone: what the ship left standing there (hd_oldsite_top.png,
        // hd_oldsite_ground.png; for a base-vs-head pair).
        private static bool HdOldSiteShots()
        {
            Map old = Find.Maps.FirstOrDefault(m => m.Tile == hdFromTile);
            if (old == null)
            {
                Info("ship dump: no old-site pictures, its map is gone");
                return false;
            }
            Current.Game.CurrentMap = old;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            IntVec3 mid = hdFromBox.CenterCell;
            GlShot((ProjectZ.Annex(old)?.floors ?? 1) - 1, mid, 22f, "hd_oldsite_top");
            GlShot(0, mid, 22f, "hd_oldsite_ground");
            glAfter = sfLandedStage;
            Wait(30, "gl_shots");
            return true;
        }

        // ---- mode shipdump: the shipset fixture flown to a neighbouring tile

        private static void StepShipDump(Map map)
        {
            switch (stage)
            {
                case "hd_setup":
                    SetupShipSet(map, "hd_ss_engine");
                    return;
                case "hd_ss_engine":
                {
                    if (GenCommandLine.CommandLineArgPassed("projectzshipdumpengineup"))
                    {
                        // (fixture variant, placed: the engine on the floor-2 deck where shipset S5's colonists install it)
                        Building_GravEngine old = GlEngine(map);
                        old.Destroy(DestroyMode.Vanish);
                        Place(ThingDefOf.GravEngine, null, SSF(12, 3, 1, map), map, Rot4.North);
                        Building_GravEngine up = GlEngine(map);
                        up.nameHidden = false;
                        up.ForceSubstructureDirty();
                        Info($"ship dump: engine placed on floor 2 at {up.Position}");
                    }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    StartTicks(250, "hd_ss_fly");
                    return;
                }
                case "hd_ss_fly":
                    HdShipSetFly(map);
                    return;
                case "hd_landed":
                    // (every flight with the switch on comes through here once the landing cutscene is over)
                    HdLanded($"leg {sfLeg}");
                    if (mode == "shipdump" && HdOldSiteShots())
                    {
                        return;
                    }
                    Wait(1, sfLandedStage);
                    return;
                case "hd_done":
                    Finish();
                    return;
            }
        }

        private static void HdShipSetFly(Map map)
        {
            Building_GravEngine e = GlEngine(map);
            List<PlanetTile> near = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(map.Tile, near);
            PlanetTile dest = near.FirstOrDefault(t => Find.WorldObjects.MapParentAt(t) == null && !Find.WorldGrid[t].WaterCovered && Find.WorldGrid[t].hilliness != Hilliness.Impassable);
            if (e == null || !dest.Valid)
            {
                Result("ship dump: an engine and a neighbouring tile to fly to", false, $"engine {e != null}, tile {dest.Valid}");
                Finish();
                return;
            }
            List<Pawn> cols = Colonists(map);
            for (int i = 0; i < cols.Count; i++)
            {
                Teleport(cols[i], SS(3 + i, 3));
                Draft(cols[i], true);
            }
            sfHomeTile = map.Tile;
            sfNewTile = dest;
            sfLeg = 1;
            sfSaved = true;
            sfFootprint = e.ValidSubstructure.Count(c => ProjectZ.FloorOf(c, map) == 0);
            sfLandedStage = "hd_done";
            SfLaunch(map, e, dest);
        }
    }
}
