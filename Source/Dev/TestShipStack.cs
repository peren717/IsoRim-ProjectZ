using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace ProjectZ.Dev
{
    // Test mode shipstairs -projectzshipstack (fix 2026-10-17, "a staircase governs only its own next floor"): ship stairs
    // stacked on a fresh gravship. The ship stairs opening on the floor above them is a ship column cell (ShipColumn): a
    // deck may go over it, so stairs can go on upwards.
    //   F0-F3 of -projectzshipfresh: colonists build ship stairs 1 (ground up to floor 2, their landing deck laid) and a
    //   colonist walks up; then
    //   K1 placement: ship stairs on the opening refused (no deck under them), an ordinary slab over the opening refused
    //      (ship: use a deck), a ship deck over it not refused for want of ship below; floor-2 decks planned beside the
    //      opening, colonists build them from up there;
    //   K2 ship stairs 2 on the floor-2 decks: their exit over the opening (the variant of the bug: their landing deck
    //      over it) and, placement only, the variant whose exit is over a floor-2 deck: both accepted; colonists build the
    //      first: its landing deck laid over the opening (the floor roof under it), part of the ship, the link usable;
    //   K3 a colonist walks from the ground up both stairs to floor 3; floor-3 decks planned over the rest of the opening
    //      and built by colonists from up there; part of the ship; ship stairs 1 may still be designated for deconstruction
    //      (user decision: nothing blocks it, the decks over the opening fall when they go);
    //   K4 save and reload: the decks over the opening stay (also after ticks: nothing falls), the load-time deck check
    //      finds none to drop;
    //   then the flight and landing turned of -projectzshipfresh (every deck flies and lands, both links usable, a walk up
    //   and down), plus: the decks over the opening on the new map with the floor roof under them, no roof left over the
    //   old site's opening;
    //   K5 a floor-3 deck over the opening is removed with the remove-foundation tool (a colonist's job; the roof under it
    //      stays as an ordinary roof, as for any deck), the other decks
    //      stay; then colonists deconstruct ship stairs 1 (a real job): their opening is gone, so the decks over it fall
    //      (no ship column under them any more; one is the landing of ship stairs 2, which then end in open air) and no
    //      deck may be planned there now.
    public static partial class TestRunner
    {
        private static bool spStack;
        private static readonly List<IntVec3> skF2 = new List<IntVec3>();
        private static readonly List<IntVec3> skF3 = new List<IntVec3>();
        private static readonly List<IntVec3> skOpenOff = new List<IntVec3>();
        private static IntVec3 skRm, skKeep, skLanding2;
        private static int skSeenF3;
        private static bool skLandedShots;

        private static void StepShipStack(Map map)
        {
            switch (stage)
            {
                case "sp_k_f2built":
                    SkF2Built(map);
                    return;
                case "sp_k_built2":
                    SkBuilt2(map);
                    return;
                case "sp_k_walked":
                    SkWalked(map);
                    return;
                case "sp_k_f3built":
                    SkF3Built(map);
                    return;
                case "sp_k_save":
                    SkSave(map);
                    return;
                case "sp_k_loaded":
                    SkLoaded(map);
                    return;
                case "sp_k_loaded2":
                    SkLoaded2(map);
                    return;
                case "sp_k_rm":
                    SkRemove(SfNew());
                    return;
                case "sp_k_removed":
                    SkRemoved(SfNew());
                    return;
                case "sp_k_deconned":
                    SkDeconned(SfNew());
                    return;
                case "sp_k_fell":
                    SkFell(SfNew());
                    return;
            }
        }

        private static List<IntVec3> SkOpening(Map map) => new List<IntVec3> { SPF(1, 1, 1, map), SPF(1, 2, 1, map), SPF(1, 3, 1, map) };

        // K1 (after F3: a colonist stands on the landing of ship stairs 1 on floor 2).
        private static void SkPlan(Map map)
        {
            List<IntVec3> open = SkOpening(map);
            Result("K1 the opening of ship stairs 1: open air on floor 2, a ship column cell", open.All(c => ShipStairs.Opening(c, map) && ShipColumn.Cell(c, map)),
                string.Join(", ", open.Select(c => $"{SpCell(c, map)} {c.GetTerrain(map)?.defName}")));
            SpExpect("K1 ship stairs on the opening refused (no deck under them)", PZDefOf.PZ_ShipStairs, null, SPF(1, 2, 1, map), Rot4.North, map, "PZ_ShipStairsOnDeck".Translate());
            AcceptanceReport slab = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_UpperFloor, SPF(1, 1, 2, map), Rot4.North, map);
            Result("K1 an ordinary slab over the opening refused (over a ship: a ship deck)", !slab.Accepted && slab.Reason == "PZ_OverGravshipUseDeck".Translate(), GlRep(slab));
            AcceptanceReport deck = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_ShipDeck, SPF(1, 1, 2, map), Rot4.North, map);
            Result("K1 a ship deck over the opening is not refused for want of ship below (nothing holds its roof yet)", deck.Accepted || deck.Reason != "PZ_DeckNeedsShipBelow".Translate(), GlRep(deck));
            skF2.Clear();
            for (int x = 2; x <= 5; x++)
            {
                for (int z = 1; z <= 3; z++)
                {
                    skF2.Add(SPF(x, z, 1, map));
                }
            }
            skF2.Add(SPF(2, 4, 1, map));
            int ok = 0;
            foreach (IntVec3 c in skF2)
            {
                AcceptanceReport r = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_ShipDeck, c, Rot4.North, map);
                if (r.Accepted && GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_ShipDeck, c, map, Rot4.North, Faction.OfPlayer, null) != null)
                {
                    ok++;
                }
                else
                {
                    Info($"deck at {SpCell(c, map)}: {GlRep(r)}");
                }
            }
            Result("K1 floor-2 decks planned beside the opening", ok == skF2.Count, $"{ok} of {skF2.Count}");
            SpWork(map, construct: true);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            TickUntil(60000, () =>
            {
                if (Find.TickManager.TicksGame % 240 == 0)
                {
                    SpKeep(map);
                }
                return skF2.All(c => ShipDeck.At(c, map));
            }, "sp_k_f2built", 60);
        }

        private static void SkF2Built(Map map)
        {
            tickWatch = null;
            int built = skF2.Count(c => ShipDeck.At(c, map));
            Result("K1 colonists built the floor-2 decks", built == skF2.Count, $"{built} of {skF2.Count} after {ticksTotal} ticks");
            ThingDef ss = PZDefOf.PZ_ShipStairs;
            // variant b (placement only): the exit over a floor-2 deck, (5, 3)
            SpExpect("K2 ship stairs on floor 2 whose exit is over a floor-2 deck accepted", ss, null, SPF(3, 3, 1, map), Rot4.East, map, null);
            // variant a (built): the exit over the opening of ship stairs 1, (1, 2)
            SpExpect("K2 ship stairs on floor 2 whose exit is over the opening of ship stairs 1 accepted (the landing deck over the opening)", ss, null, SPF(3, 2, 1, map), Rot4.West, map, null);
            Blueprint b = GenConstruct.PlaceBlueprintForBuild(ss, SPF(3, 2, 1, map), map, Rot4.West, Faction.OfPlayer, null) as Blueprint;
            spBp2 = b?.ThingID;
            if (b == null)
            {
                Finish();
                return;
            }
            TickUntil(60000, () =>
            {
                SpKeep(map);
                return SpShipStairs(map).Count >= 2;
            }, "sp_k_built2", 120);
        }

        private static void SkBuilt2(Map map)
        {
            tickWatch = null;
            List<Building_Stairs> st = SpShipStairs(map);
            Result("K2 colonists built ship stairs 2 on floor 2", st.Count == 2 && ProjectZ.FloorOf(st[1].Position, map) == 1, $"{st.Count} after {ticksTotal} ticks");
            if (st.Count < 2)
            {
                Finish();
                return;
            }
            Building_Stairs s1 = st[0], s2 = st[1];
            skLanding2 = s2.TopCell;
            IntVec3 below = FloorRoof.Below(skLanding2, map);
            Result("K2 their landing deck is laid on floor 3 over the opening of ship stairs 1, the floor roof under it", SpLandingOk(s2, map, out string d) && ProjectZ.FloorOf(skLanding2, map) == 2 && SkOpening(map).Contains(below) && ShipStairs.Opening(below, map), d);
            Building_GravEngine e = GlEngine(map);
            e.ForceSubstructureDirty();
            Result("K2 the landing deck over the opening is part of the ship (the column goes on through the opening)", e.ValidSubstructure.Contains(skLanding2) && e.OnValidSubstructure(s2) && skF2.All(c => e.ValidSubstructure.Contains(c)),
                $"set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)})");
            StairGrid g = StairGrid.For(map);
            int usable = g == null ? 0 : g.links.Count(l => (l.stairs == s1 || l.stairs == s2) && g.Usable(l));
            int open = st.Sum(s => s.OccupiedRect().Cells.Count(c => StairShape.Above(c, map).GetTerrain(map) == PZDefOf.PZ_OpenAir && map.terrainGrid.FoundationAt(StairShape.Above(c, map)) == null));
            Result("K2 both links usable, both stairwells open", usable == 2 && open == 6, $"{usable} usable, open {open}/6");
            spFoot1.Clear();
            spFoot1.AddRange(s1.OccupiedRect().Cells);
            spFoot2.Clear();
            spFoot2.AddRange(s2.OccupiedRect().Cells);
            Pawn w = SfPawn(spWalker);
            Teleport(w, SP(6, 13));
            spGoal = skLanding2;
            GoTo(w, spGoal);
            spSeen1 = spSeen2 = 0;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            TickUntil(8000, () => SpWatchWalker(map) && w.Position == spGoal, "sp_k_walked", 1);
        }

        private static void SkWalked(Map map)
        {
            tickWatch = null;
            Pawn w = SfPawn(spWalker);
            Result("K3 a colonist walks from the ground up both ship stairs onto the landing over the opening (floor 3)", w?.Position == spGoal && spSeen1 > 0 && spSeen2 > 0,
                $"at {SpCell(w?.Position ?? IntVec3.Invalid, map)}, ticks on ramp 1 {spSeen1}, ramp 2 {spSeen2}, after {ticksTotal} ticks");
            skF3.Clear();
            skF3.Add(SPF(1, 1, 2, map));
            skF3.Add(SPF(1, 3, 2, map));
            int ok = 0;
            foreach (IntVec3 c in skF3)
            {
                AcceptanceReport r = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_ShipDeck, c, Rot4.North, map);
                if (r.Accepted && GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_ShipDeck, c, map, Rot4.North, Faction.OfPlayer, null) != null)
                {
                    ok++;
                }
                else
                {
                    Info($"deck at {SpCell(c, map)}: {GlRep(r)}");
                }
            }
            Result("K3 floor-3 decks over the rest of the opening accepted and planned", ok == skF3.Count, $"{ok} of {skF3.Count}");
            SpWork(map, construct: true);
            skSeenF3 = 0;
            TickUntil(60000, () =>
            {
                if (Find.TickManager.TicksGame % 240 == 0)
                {
                    SpKeep(map);
                }
                skSeenF3 += Colonists(map).Count(p => ProjectZ.FloorOf(p.Position, map) == 2 && p.CurJobDef == JobDefOf.FinishFrame);
                return skF3.All(c => ShipDeck.At(c, map));
            }, "sp_k_f3built", 60);
        }

        private static void SkF3Built(Map map)
        {
            tickWatch = null;
            int built = skF3.Count(c => ShipDeck.At(c, map));
            Result("K3 colonists built the floor-3 decks over the opening, working up there", built == skF3.Count && skSeenF3 > 0, $"{built} of {skF3.Count} after {ticksTotal} ticks, builder seen on floor 3 {skSeenF3}x");
            Building_GravEngine e = GlEngine(map);
            List<IntVec3> over = new List<IntVec3>(skF3) { skLanding2 };
            Result("K3 the decks over the opening are part of the ship, the floor roof under each", over.All(c => e.ValidSubstructure.Contains(c) && map.roofGrid.RoofAt(FloorRoof.Below(c, map)) == PZDefOf.PZ_FloorRoof),
                string.Join(", ", over.Select(c => $"{SpCell(c, map)} in set {e.ValidSubstructure.Contains(c)}, roof below {map.roofGrid.RoofAt(FloorRoof.Below(c, map))?.defName ?? "none"}")));
            Building_Stairs s1 = SpShipStairs(map)[0];
            AcceptanceReport dec = new Designator_Deconstruct().CanDesignateThing(s1);
            Result("K3 ship stairs 1 may still be designated for deconstruction with decks over their opening", dec.Accepted, GlRep(dec));
            Result("K3 the load-time deck check finds no deck to drop", ShipDeck.CheckAll(map) == 0, "");
            GlShot(0, SP(2, 2), 6f, "sp_k1_stack_ground");
            GlShot(1, SP(2, 2), 6f, "sp_k1_stack_f2");
            GlShot(2, SP(2, 2), 6f, "sp_k1_stack_f3");
            glAfter = "sp_k_save";
            Wait(2, "gl_shots");
        }

        // K4: save and reload.
        private static void SkSave(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            GameDataSaveLoader.SaveGame("pz_shipstack");
            stage = "sp_k_loaded";
            pendingLoad = "pz_shipstack";
        }

        private static void SkLoaded(Map map)
        {
            List<IntVec3> over = new List<IntVec3> { SPF(1, 1, 2, map), SPF(1, 2, 2, map), SPF(1, 3, 2, map) };
            Result("K4 after the reload the decks over the opening are there", over.All(c => ShipDeck.At(c, map)), string.Join(", ", over.Select(c => $"{SpCell(c, map)} {map.terrainGrid.FoundationAt(c)?.defName ?? "none"}")));
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            TickUntil(600, () =>
            {
                SpKeep(map);
                return false;
            }, "sp_k_loaded2", 60);
        }

        private static void SkLoaded2(Map map)
        {
            tickWatch = null;
            List<IntVec3> over = new List<IntVec3> { SPF(1, 1, 2, map), SPF(1, 2, 2, map), SPF(1, 3, 2, map) };
            Building_GravEngine e = GlEngine(map);
            List<Building_Stairs> st = SpShipStairs(map);
            StairGrid g = StairGrid.For(map);
            int usable = g == null ? 0 : g.links.Count(l => st.Contains(l.stairs) && g.Usable(l));
            Result("K4 600 ticks after the reload nothing fell: decks over the opening, both stairs and links, all in the ship",
                over.All(c => ShipDeck.At(c, map) && e.ValidSubstructure.Contains(c)) && st.Count == 2 && usable == 2 && ShipDeck.CheckAll(map) == 0,
                $"decks {over.Count(c => ShipDeck.At(c, map))}/3, stairs {st.Count}, usable {usable}; set {e.ValidSubstructure.Count} ({SsFloors(e.ValidSubstructure, map)})");
            skOpenOff.Clear();
            foreach (IntVec3 c in SkOpening(map))
            {
                skOpenOff.Add(ShipLocal.From(c, e.Position, map));
            }
            Wait(10, "sp_launch");
        }

        // After the landing (SpLanded): the decks over the opening and their roofs, the old site.
        private static void SkLandedChecks(Map map, Building_GravEngine e)
        {
            int good = 0;
            string detail = "";
            foreach (IntVec3 off in skOpenOff)
            {
                IntVec3 well = SfAt(off, sfRoot, sfRot, map);
                IntVec3 up = StairShape.Above(well, map);
                bool ok = ShipStairs.Opening(well, map) && ShipDeck.At(up, map) && e.ValidSubstructure.Contains(up) && map.roofGrid.RoofAt(well) == PZDefOf.PZ_FloorRoof;
                good += ok ? 1 : 0;
                detail += $"{SpCell(well, map)} opening {ShipStairs.Opening(well, map)} roof {map.roofGrid.RoofAt(well)?.defName ?? "none"}, deck above {ShipDeck.At(up, map)}; ";
            }
            Result("K4 landed: the three decks over the opening came along, the floor roof under each", good == 3, detail);
            Map home = Find.Maps.FirstOrDefault(m => m.Tile == sfHomeTile);
            if (home != null)
            {
                List<IntVec3> old = SkOpening(home);
                int roofs = old.Count(c => home.roofGrid.Roofed(c));
                int decks = old.Count(c => ShipDeck.At(StairShape.Above(c, home), home));
                Result("K4 the old site: no roof and no deck left over the opening", roofs == 0 && decks == 0, $"roofs {roofs}, decks {decks}");
            }
        }

        // K5: a floor-3 deck over the opening removed with the remove-foundation tool, by a colonist.
        private static void SkRemove(Map map)
        {
            List<Building_Stairs> st = SpShipStairs(map);
            if (st.Count < 2)
            {
                Result("K5 both ship stairs on the landed map", false, $"{st.Count}");
                Finish();
                return;
            }
            Building_Stairs s1 = st[0];
            if (!skLandedShots)
            {
                // the landed stack, each floor, close up
                skLandedShots = true;
                IntVec3 g = ProjectZ.GroundCellOf(s1.Position, map);
                GlShot(0, g, 6f, "sp_k2_landed_ground");
                GlShot(1, g, 6f, "sp_k2_landed_f2");
                GlShot(2, g, 6f, "sp_k2_landed_f3");
                glAfter = "sp_k_rm";
                Wait(2, "gl_shots");
                return;
            }
            List<IntVec3> wells = s1.OccupiedRect().Cells.Select(c => StairShape.Above(c, map)).ToList();
            IntVec3 landing = st[1].TopCell;
            List<IntVec3> others = wells.Select(c => StairShape.Above(c, map)).Where(c => c != landing).ToList();
            skRm = others[0];
            skKeep = others.Count > 1 ? others[1] : IntVec3.Invalid;
            skLanding2 = landing;
            Designator_RemoveFoundation rf = AllDesignators().OfType<Designator_RemoveFoundation>().FirstOrDefault();
            FloorView.Set(2);
            AcceptanceReport r = rf?.CanDesignateCell(skRm) ?? false;
            Result("K5 a floor-3 deck over the opening may be removed with the remove-foundation tool", r.Accepted, $"{SpCell(skRm, map)}: {GlRep(r)}");
            if (r.Accepted)
            {
                rf.DesignateSingleCell(skRm);
            }
            FloorView.Set(0);
            SpWork(map, construct: true);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            TickUntil(20000, () =>
            {
                SpKeep(map);
                return !ShipDeck.At(skRm, map);
            }, "sp_k_removed", 60);
        }

        private static void SkRemoved(Map map)
        {
            tickWatch = null;
            IntVec3 well = FloorRoof.Below(skRm, map);
            // (compat G2, user 2026-10-18: the deck's materials fall and break the ordinary roof under it; -projectznogravity: it stays)
            Result(Gravity.Off ? "K5 a colonist removed it: open air, the roof under it an ordinary roof again (as for any deck); the other decks over the opening stay" : "K5 a colonist removed it: open air, the roof under it broken by its falling materials; the other decks over the opening stay",
                skRm.GetTerrain(map) == PZDefOf.PZ_OpenAir && !ShipDeck.At(skRm, map) && (Gravity.Off ? map.roofGrid.RoofAt(well) == RoofDefOf.RoofConstructed : !map.roofGrid.Roofed(well)) && ShipDeck.At(skKeep, map) && ShipDeck.At(skLanding2, map),
                $"{SpCell(skRm, map)} {skRm.GetTerrain(map)?.defName}, roof below {map.roofGrid.RoofAt(well)?.defName ?? "none"}; kept {ShipDeck.At(skKeep, map)} landing 2 {ShipDeck.At(skLanding2, map)}; after {ticksTotal} ticks");
            // the rule for stairs that are gone: their opening is no ship column cell any more, the decks over it fall
            List<Building_Stairs> st = SpShipStairs(map);
            Building_Stairs s1 = st[0];
            skStairs2 = st[1].ThingID;
            AcceptanceReport dec = new Designator_Deconstruct().CanDesignateThing(s1);
            Result("K5 ship stairs 1 may be designated for deconstruction (decks still over their opening)", dec.Accepted, GlRep(dec));
            map.designationManager.AddDesignation(new Designation(s1, DesignationDefOf.Deconstruct));
            SpWork(map, construct: true);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            TickUntil(30000, () =>
            {
                SpKeep(map);
                return SpShipStairs(map).Count < 2;
            }, "sp_k_deconned", 60);
        }

        private static string skStairs2;

        private static void SkDeconned(Map map)
        {
            tickWatch = null;
            Result("K5 colonists deconstructed ship stairs 1", SpShipStairs(map).Count == 1 && SpShipStairs(map)[0].ThingID == skStairs2, $"{SpShipStairs(map).Count} ship stairs left after {ticksTotal} ticks");
            TickUntil(300, () =>
            {
                SpKeep(map);
                return false;
            }, "sp_k_fell", 60);
        }

        private static void SkFell(Map map)
        {
            tickWatch = null;
            Result("K5 the decks over the old opening of ship stairs 1 fell (no ship column under them now)", !ShipDeck.At(skKeep, map) && !ShipDeck.At(skLanding2, map),
                $"{SpCell(skKeep, map)} {skKeep.GetTerrain(map)?.defName}, {SpCell(skLanding2, map)} {skLanding2.GetTerrain(map)?.defName}");
            // ship stairs 2 (their landing was one of those decks): reported as they are
            Building_Stairs s2 = SpShipStairs(map).FirstOrDefault(s => s.ThingID == skStairs2);
            StairGrid g = StairGrid.For(map);
            bool usable = s2 != null && g != null && g.links.Any(l => l.stairs == s2 && g.Usable(l));
            Info($"K5 ship stairs 2 after their landing fell: standing {s2 != null && s2.Spawned}, exit {(s2 == null ? "-" : SpCell(s2.TopCell, map) + " " + s2.TopCell.GetTerrain(map)?.defName + ", standable " + s2.TopCell.Standable(map))}, link usable {usable}; hit points {s2?.HitPoints}/{s2?.MaxHitPoints}");
            foreach (Pawn p in Colonists(map))
            {
                Info($"K5 colonist {p.LabelShort} at {SpCell(p.Position, map)}, health {p.health.summaryHealth.SummaryHealthPercent:0.00}, downed {p.Downed}");
            }
            Result("K5 ship stairs 2 stand on, their exit open air now (the landing fell), their link not usable", s2 != null && s2.Spawned && s2.TopCell.GetTerrain(map) == PZDefOf.PZ_OpenAir && !usable,
                $"standing {s2 != null}, link usable {usable}");
            AcceptanceReport r = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_ShipDeck, skKeep, Rot4.North, map);
            Result("K5 and no ship deck may be planned there now", !r.Accepted && r.Reason == "PZ_DeckNeedsShipBelow".Translate(), GlRep(r));
            Wait(10, "sp_end");
        }
    }
}
