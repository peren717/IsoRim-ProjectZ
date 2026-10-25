using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace ProjectZ.Dev
{
    // Mode deckkin (2026-10-25): gravship deck foundations without Substructure's tag (DeckFoundation: the tag or the
    // game's Substructure affordance). Run with -projectzfloors=2 -projectzseed=7, Odyssey. The kin decks are every loaded
    // foundation with the Substructure affordance and without the tag, or the defs named by -projectzdeckkin=a,b,c.
    // Fixtures, each on its own patch of ground (3 x 8, a wood wall beside the tested cell holds the roofs): vanilla Substructure,
    // each kin deck and a concrete control. The real designators answer:
    //   DK1 an ordinary slab over the deck refused (over a gravship: use the ship deck), the control accepted;
    //   DK2 ordinary stairs on the deck refused (use the ship stairs), the control accepted;
    //   DK3 a ship deck over the deck is not refused for "no ship below" (the deck is the ship's column);
    // A small gravship (TestGravLand.BuildShip) with 4-cell strips against its east hull: concrete (the control) and each
    // kin deck (up to 3); a wood wall on each strip holds the roofs:
    //   DK4 the ship set (ValidSubstructure, AllConnectedSubstructure) has the ship's substructure and none of the strip
    //       (the game's own test, the tag: a kin deck never flies with the player's ship);
    //   DK5 a ship deck over a kin strip accepted, an ordinary slab over it refused (over the concrete strip: the deck
    //       refused for "no ship below", the slab accepted);
    //   DK6 a ship deck over the strip falls when the kin foundation under it is removed (ticks).
    public static partial class TestRunner
    {
        private static readonly List<TerrainDef> dkKinds = new List<TerrainDef>();
        private static readonly List<IntVec3> dkPatch = new List<IntVec3>();
        private static readonly List<IntVec3> dkStrip = new List<IntVec3>();
        private static IntVec3 dkShip;
        private static int dkKinCount;

        private static bool StepDeckKin(Map map)
        {
            switch (stage)
            {
                case "dk_setup":
                    DkSetup(map);
                    return true;
                case "dk_check":
                    DkCheck(map);
                    return true;
                case "dk_fall_check":
                    DkFallCheck(map);
                    return true;
            }
            return false;
        }

        private static IntVec3 DkUp(IntVec3 g, Map map) => ProjectZ.CellOnFloor(g, 1, map);

        private static void DkExpect(string name, Designator d, IntVec3 c, bool accept, string want = null, string notWant = null)
        {
            AcceptanceReport r = d.CanDesignateCell(c);
            bool ok = accept ? r.Accepted : !r.Accepted && (want == null || r.Reason == want) && (notWant == null || r.Reason != notWant);
            Result(name, ok, $"{c} f{ProjectZ.FloorOf(c, Find.CurrentMap) + 1}: {SdWhy(r)}" + (ok ? "" : accept ? " (want accepted)" : want != null ? $" (want '{want}')" : $" (want refused, not '{notWant}')"));
        }

        private static void DkSetup(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            TerrainDef sub = DefDatabase<TerrainDef>.GetNamedSilentFail("Substructure");
            TerrainAffordanceDef aff = DefDatabase<TerrainAffordanceDef>.GetNamedSilentFail("Substructure");
            if (an == null || an.MaxLevel < 1 || sub == null || aff == null || PZDefOf.PZ_ShipDeck == null)
            {
                Result("deckkin: a map with floors and Odyssey", false, $"annex {an != null}, substructure {sub != null}");
                Finish();
                return;
            }
            dkKinds.Clear();
            dkKinds.Add(sub);
            List<TerrainDef> kin;
            if (GenCommandLine.TryGetCommandLineArg("projectzdeckkin", out string names) && !string.IsNullOrEmpty(names))
            {
                kin = names.Split(',').Select(n => DefDatabase<TerrainDef>.GetNamedSilentFail(n.Trim())).ToList();
                if (kin.Any(t => t == null))
                {
                    Result("deckkin: the named kin decks are loaded", false, names);
                    Finish();
                    return;
                }
            }
            else
            {
                kin = DefDatabase<TerrainDef>.AllDefs.Where(t => t.isFoundation && !t.IsSubstructure && t.affordances != null && t.affordances.Contains(aff)).OrderBy(t => t.defName).ToList();
            }
            dkKinCount = kin.Count;
            dkKinds.AddRange(kin);
            dkKinds.Add(TerrainDefOf.Concrete);
            Info("kinds: " + string.Join(", ", dkKinds.Select(t => $"{t.defName} (tag {t.IsSubstructure}, foundation {t.isFoundation})")));
            Result("deckkin: at least one kin deck", kin.Count > 0, kin.Count + " kin decks");

            // the field: below the floors, clear of undestroyable things
            int W = Math.Max(40, 4 * dkKinds.Count + 2) + 30, H = 22;
            CellRect found = CellRect.Empty;
            int bestBad = int.MaxValue;
            for (int z = an.groundZ - H - 4; z >= 4 && bestBad > 0; z -= 3)
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
            }
            if (found.Area == 0)
            {
                Result("deckkin field", false, "map too small");
                Finish();
                return;
            }
            DebugSettings.enableRandomMentalStates = false;
            Find.Storyteller.storytellerComps.Clear();
            Find.Storyteller.incidentQueue.Clear();
            Find.PlaySettings.autoRebuild = false;
            Find.ResearchManager.gravEngineInspected = true;
            List<Pawn> cols = Colonists(map);
            foreach (Pawn hostile in map.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer)).ToList())
            {
                hostile.Destroy(DestroyMode.Vanish);
            }
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
                IntVec3 u = DkUp(c, map);
                if (u.IsValid)
                {
                    map.fogGrid.Unfog(u);
                }
            }
            // colonists out of the way (west of the field)
            for (int i = 0; i < cols.Count; i++)
            {
                IntVec3 to = new IntVec3(found.minX - 2, 0, found.minZ + i);
                if (to.InBounds(map) && cols[i].Position != to)
                {
                    cols[i].jobs?.StopAll();
                    cols[i].pather?.StopDead();
                    cols[i].Position = to;
                    cols[i].Notify_Teleported();
                }
            }
            // the patches: x = minX + 1 + 4i, z = minZ + 1 .. + 8 (far from the ship: outside every footprint)
            dkPatch.Clear();
            for (int i = 0; i < dkKinds.Count; i++)
            {
                IntVec3 o = new IntVec3(found.minX + 1 + 4 * i, 0, found.minZ + 1);
                dkPatch.Add(o);
                for (int x = 0; x < 3; x++)
                {
                    for (int z = 0; z < 8; z++)
                    {
                        map.terrainGrid.SetTerrain(o + new IntVec3(x, 0, z), dkKinds[i]);
                    }
                }
                // (holds the roof under the slab or deck tested at (1, 6))
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, o + new IntVec3(2, 0, 6), map);
            }
            // the ship: 13 x 11 at the east end of the field; east of its hull 4-cell strips: concrete (the control) on row
            // 0, a kin deck on rows 3, 6, 9; a wood wall on each strip's first cell holds the roofs
            dkShip = new IntVec3(found.maxX - 22, 0, found.minZ + 4);
            BuildShip(map, dkShip, 13, 11);
            dkStrip.Clear();
            for (int i = 0; i <= Math.Min(3, dkKinCount); i++)
            {
                IntVec3 s = dkShip + new IntVec3(13, 0, 3 * i);
                dkStrip.Add(s);
                for (int x = 0; x < 4; x++)
                {
                    map.terrainGrid.SetTerrain(s + new IntVec3(x, 0, 0), i == 0 ? TerrainDefOf.Concrete : dkKinds[i]);
                }
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, s, map);
            }
            GlEngine(map)?.ForceSubstructureDirty();
            Info($"field {found}, patches from {dkPatch[0]}, ship SW {dkShip}, engine {GlEngine(map)?.Position}, strips {string.Join(" ", dkStrip)}");
            Wait(30, "dk_check");
        }

        private static void DkCheck(Map map)
        {
            FloorView.Set(0);
            Designator_Build slab = new Designator_Build(PZDefOf.PZ_UpperFloor);
            Designator_Build stairs = new Designator_Build(PZDefOf.PZ_Stairs);
            stairs.SetStuffDef(ThingDefOf.WoodLog);
            Designator_Build deck = new Designator_Build(PZDefOf.PZ_ShipDeck);
            string useDeck = "PZ_OverGravshipUseDeck".Translate();
            string useStairs = "PZ_OverGravshipUseStairs".Translate();
            string noShip = "PZ_DeckNeedsShipBelow".Translate();
            for (int i = 0; i < dkKinds.Count; i++)
            {
                TerrainDef t = dkKinds[i];
                IntVec3 o = dkPatch[i];
                bool control = i == dkKinds.Count - 1;
                IntVec3 g = o + new IntVec3(1, 0, 6);
                Info($"{t.defName}: DeckFoundation {DeckFoundation.Is(t)}, SlabSupport.Gravship {SlabSupport.Gravship(g, map)}, ShipColumn {ShipColumn.Cell(g, map)}, ShipDeck.At (ground) {ShipDeck.At(g, map)}");
                if (control)
                {
                    DkExpect($"DK1 slab over {t.defName} (control) accepted", slab, DkUp(g, map), true);
                    DkExpect($"DK2 stairs on {t.defName} (control) accepted", stairs, o + new IntVec3(0, 0, 2), true);
                    // (DK3's control is on the strip by the ship: out here the footprint refuses first)
                }
                else
                {
                    DkExpect($"DK1 slab over {t.defName} refused", slab, DkUp(g, map), false, useDeck);
                    DkExpect($"DK2 stairs on {t.defName} refused", stairs, o + new IntVec3(0, 0, 2), false, useStairs);
                    DkExpect($"DK3 ship deck over {t.defName}: it is the ship's column", deck, DkUp(g, map), false, null, noShip);
                }
            }
            // the ship set
            Building_GravEngine e = GlEngine(map);
            e.ForceSubstructureDirty();
            HashSet<IntVec3> valid = e.ValidSubstructure, all = e.AllConnectedSubstructure;
            List<IntVec3> strip = dkStrip.Skip(1).SelectMany(s => Enumerable.Range(0, 4).Select(x => s + new IntVec3(x, 0, 0))).ToList();
            int shipCells = 13 * 11 + 4;
            Result("DK4 the ship set has the ship's substructure and none of the kin strips", valid.Count == shipCells && !strip.Any(valid.Contains) && !strip.Any(all.Contains),
                $"valid {valid.Count} (want {shipCells}), all {all.Count}, strip cells in valid {strip.Count(valid.Contains)}, in all {strip.Count(all.Contains)}");
            IntVec3 cu = DkUp(dkStrip[0] + new IntVec3(1, 0, 0), map);
            DkExpect("DK5 ship deck over the concrete strip (control) refused: no ship below", deck, cu, false, noShip);
            DkExpect("DK5 slab over the concrete strip (control) accepted", slab, cu, true);
            for (int i = 1; i < dkStrip.Count; i++)
            {
                IntVec3 s = dkStrip[i];
                string name = dkKinds[i].defName;
                DkExpect($"DK5 ship deck over the {name} strip accepted", deck, DkUp(s + new IntVec3(1, 0, 0), map), true);
                DkExpect($"DK5 slab over the {name} strip refused", slab, DkUp(s + new IntVec3(1, 0, 0), map), false, useDeck);
                // DK6 fixture: a deck laid over the strip's third cell, then the kin foundation under it removed
                IntVec3 g = s + new IntVec3(2, 0, 0), u = DkUp(g, map);
                map.terrainGrid.SetTerrain(u, PZDefOf.PZ_ShipDeck);
                if (!ShipDeck.At(u, map))
                {
                    Result($"DK6 fixture: a ship deck over the {name} strip", false, SdTerrain(u, map));
                }
                map.terrainGrid.RemoveFoundation(g, false);
            }
            SeedTicks("deckkin_fall");
            StartTicks(120, "dk_fall_check");
        }

        private static void DkFallCheck(Map map)
        {
            for (int i = 1; i < dkStrip.Count; i++)
            {
                IntVec3 g = dkStrip[i] + new IntVec3(2, 0, 0), u = DkUp(g, map);
                Result($"DK6 the ship deck over the removed {dkKinds[i].defName} fell", !ShipDeck.At(u, map), SdTerrain(u, map) + $"; ground foundation {map.terrainGrid.FoundationAt(g)?.defName ?? "-"}");
            }
            Finish();
        }
    }
}
