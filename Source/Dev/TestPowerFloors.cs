using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode powerfloors (hookups to the next floor, PowerHookup in PowerPatches.cs; -projectzfloors=3 -projectzseed=7).
    // Real game flow, seeded ticks; every check has its own fixture on a cleared concrete field, 22 x 16 cells apart (no
    // fixture's transmitter is in another fixture's range). A fixture: a standing lamp at its origin on floor kL and lines
    // of conduits, each from (dx, dz) east to x 8 on floor k with a charged battery at x 9 (out of the lamp's range, so
    // the line's first conduit is the nearest transmitter). Floors 1-3 = ground, floor 2, floor 3.
    //   lines first, then the lamps (rule a, the connector picks):
    //     F1 ground lamp, conduit on floor 2 (dx 3)                         -> hooks up to it, powered
    //     F2 floor-2 lamp, ground conduit (dx 3)                            -> hooks up to it, powered
    //     F3 ground lamp, own-floor conduit dx 5 + floor-2 conduit dx 1     -> the own floor's
    //     F4 ground lamp, conduit on floor 3 right above (dx 2)             -> nothing (two floors away)
    //     F5 floor-2 lamp, ground dx 2 + floor-3 dx 4                       -> the ground one (nearer)
    //     F6 floor-2 lamp, ground dx 4 + floor-3 dx 2                       -> the floor-3 one (nearer)
    //   lamps first, the lines placed later in one tick (rule b, a new conduit picks up waiting lamps):
    //     F7 ground lamp, then a floor-2 line dx 3                          -> picked up
    //     F8 floor-2 lamp, then ground dx 4 + floor-3 dx 2                  -> the floor-3 one (nearer)
    //     F9 ground lamp, then own floor dx 5 + floor-2 dx 1                -> the own floor's
    //   F10 floor-2 lamp, one ground conduit C in range (the range's corner, dx 6 dz 6; its line runs out of range): a
    //     colonist deconstructs C (the lamp goes dark, hooked to nothing), a colonist builds C again from a blueprint
    //     (hooked up and lit again: the rebuilt conduit picks the lamp up).
    //   The reconnect gizmo (CompPower.TryManualReconnect): F5 cycles to the floor-3 net and back; F3 stays on its own
    //   floor. Shots (3D, 2D, power overlay) of F1. Save + reload: every hookup as before.
    // Mode powerfloors1 (-projectzfloors=1, a map without floors): V1 lamp + conduit dx 3 (hooked), V2 lamp alone (none),
    // V3 lamp then a conduit (picked up) - vanilla, and the hookup rule returns nothing there.
    public static partial class TestRunner
    {
        private sealed class PfFix
        {
            public string name;
            public IntVec3 o;               // ground origin
            public int kL;                  // the lamp's floor
            public List<(int k, int dx, int dz)> lines = new List<(int, int, int)>();
            public bool linesLater;
            public int wantLine = -1;       // index of the line the lamp must hook up to; -1 = none
        }

        private static readonly List<PfFix> pfFix = new List<PfFix>();
        private static PfFix pfC;   // F10
        private static int pfShot;
        private static IntVec3 pfCCell;
        private static readonly string[] PfShots = { "3d_f1", "3d_f2", "3d_overlay_f1", "3d_overlay_f2", "2d_f1", "2d_f2", "2d_overlay_f1", "2d_gap" };

        private static ThingDef PfConduit => DefDatabase<ThingDef>.GetNamed("PowerConduit");

        private static ThingDef PfLampDef => DefDatabase<ThingDef>.GetNamed("StandingLamp");

        private static IntVec3 PfAt(PfFix f, int dx, int dz, int k, Map map) => Levels.Cell(new IntVec3(f.o.x + dx, 0, f.o.z + dz), k, map);

        private static IntVec3 PfLampCell(PfFix f, Map map) => PfAt(f, 0, 0, f.kL, map);

        private static IntVec3 PfLineCell(PfFix f, int i, Map map) => PfAt(f, f.lines[i].dx, f.lines[i].dz, f.lines[i].k, map);

        private static CompPowerTrader PfLamp(PfFix f, Map map) => PfLampCell(f, map).GetThingList(map).FirstOrDefault(t => t.def == PfLampDef)?.TryGetComp<CompPowerTrader>();

        private static PfFix PfAdd(string name, int slot, int kL, bool later, int want, params (int, int, int)[] lines)
        {
            PfFix f = new PfFix { name = name, o = new IntVec3(16 + slot % 4 * 22, 0, 14 + slot / 4 * 16), kL = kL, linesLater = later, wantLine = want };
            f.lines.AddRange(lines);
            pfFix.Add(f);
            return f;
        }

        private static void PfTicks(string name, int n, string next)
        {
            SeedTicks("powerfloors_" + name);
            StartTicks(n, next);
        }

        private static bool StepPowerFloors(Map map)
        {
            switch (stage)
            {
                case "pf_setup":
                    PfSetup(map);
                    return true;
                case "pf_lamps":
                    foreach (PfFix f in pfFix)
                    {
                        Place(PfLampDef, null, PfLampCell(f, map), map);
                    }
                    PfTicks("lamps", 300, "pf_check1");
                    return true;
                case "pf_check1":
                    foreach (PfFix f in pfFix)
                    {
                        if (f.linesLater)
                        {
                            CompPowerTrader l = PfLamp(f, map);
                            Result($"{f.name} before its lines: the lamp is dark, hooked to nothing", l != null && !l.PowerOn && l.connectParent == null, PfState(l, map));
                        }
                        else
                        {
                            PfExpect(f, map, "lines first");
                        }
                    }
                    foreach (PfFix f in pfFix.Where(x => x.linesLater))
                    {
                        PfLines(f, map);
                    }
                    PfTicks("later", 300, "pf_check2");
                    return true;
                case "pf_check2":
                    foreach (PfFix f in pfFix.Where(x => x.linesLater))
                    {
                        PfExpect(f, map, "lines placed after the lamp");
                    }
                    if (ProjectZ.Annex(map) == null)
                    {
                        Result("no floors: the hookup rule finds nothing beyond vanilla", PowerHookup.Fallback(PfLampCell(pfFix[1], map), map, null) == null && PowerHookup.OtherFloors(PfLampCell(pfFix[0], map), map) == null, "");
                        stage = "pf_end";
                        return true;
                    }
                    stage = "pf_gizmo";
                    return true;

                // ---- the reconnect gizmo
                case "pf_gizmo":
                {
                    PfFix f5 = pfFix.First(f => f.name == "F5"), f3 = pfFix.First(f => f.name == "F3");
                    CompPowerTrader l5 = PfLamp(f5, map), l3 = PfLamp(f3, map);
                    l5?.TryManualReconnect(false);
                    IntVec3 first = l5?.connectParent?.parent.Position ?? IntVec3.Invalid;
                    l5?.TryManualReconnect(false);
                    IntVec3 second = l5?.connectParent?.parent.Position ?? IntVec3.Invalid;
                    Result("F5 reconnect gizmo: to the floor-3 conduit, pressed again back to the ground one", first == PfLineCell(f5, 1, map) && second == PfLineCell(f5, 0, map),
                        $"first {first} f{PfFloor(first, map)}, second {second} f{PfFloor(second, map)}; ground {PfLineCell(f5, 0, map)}, floor 3 {PfLineCell(f5, 1, map)}");
                    l3?.TryManualReconnect(false);
                    IntVec3 own = l3?.connectParent?.parent.Position ?? IntVec3.Invalid;
                    Result("F3 reconnect gizmo: stays on its own floor (its only own-floor net)", own == PfLineCell(f3, 0, map), $"hooked to {own} f{PfFloor(own, map)}, own-floor conduit {PfLineCell(f3, 0, map)}");
                    PfTicks("gizmo", 120, "pf_gizmo2");
                    return true;
                }
                case "pf_gizmo2":
                    PfExpect(pfFix.First(f => f.name == "F5"), map, "after the gizmo");
                    PfExpect(pfFix.First(f => f.name == "F3"), map, "after the gizmo");
                    pfShot = 0;
                    Wait(2, "pf_shot");
                    return true;

                // ---- shots of F1 (ground lamp, conduit on floor 2)
                case "pf_shot":
                {
                    if (pfShot >= PfShots.Length)
                    {
                        Find.DesignatorManager.Deselect();
                        fakeMouse = null;
                        BrView3D(true);
                        CameraDriver_Update_Patch.AllowAnnexView = false;
                        Wait(5, "pf_decon");
                        return true;
                    }
                    string n = PfShots[pfShot];
                    PfFix f1 = pfFix.First(f => f.name == "F1");
                    IntVec3 g = new IntVec3(f1.o.x + 2, 0, f1.o.z);
                    int floor = n.EndsWith("f2") ? 1 : 0;
                    if (n.Contains("overlay"))
                    {
                        fakeMouse = new Vector2(4f, 4f);
                        Find.DesignatorManager.Select(BuildCopyCommandUtility.FindAllowedDesignator(PfConduit) ?? new Designator_Build(PfConduit));
                    }
                    else
                    {
                        Find.DesignatorManager.Deselect();
                        fakeMouse = null;
                    }
                    if (n.StartsWith("3d"))
                    {
                        ShotAt(map, floor, g, 9f);
                    }
                    else
                    {
                        BrView3D(false);
                        FloorView.Set(0);
                        CameraDriver_Update_Patch.AllowAnnexView = true;
                        Find.Selector.ClearSelection();
                        IntVec3 c = Levels.Cell(g, floor, map);
                        float size = 9f;
                        if (n == "2d_gap")
                        {
                            // the void rows between the ground and floor 2 above F1's column (the wires crossing them)
                            c = new IntVec3(g.x + 6, 0, ProjectZ.GroundZ(map) + 1);
                            size = 16f;
                        }
                        Find.CameraDriver.SetRootPosAndSize(new Vector3(c.x + 0.5f, 0f, c.z + 0.5f), size);
                    }
                    Wait(45, "pf_shot_take");
                    return true;
                }
                case "pf_shot_take":
                    ZCapture("pf_" + PfShots[pfShot]);
                    pfShot++;
                    Wait(2, "pf_shot");
                    return true;

                // ---- F10: a colonist deconstructs C, then builds it again
                case "pf_decon":
                {
                    Thing c = pfCCell.GetThingList(map).FirstOrDefault(t => t.def == PfConduit);
                    Designator_Deconstruct dd = new Designator_Deconstruct();
                    AcceptanceReport ok = c == null ? "no C" : dd.CanDesignateThing(c);
                    if (ok.Accepted)
                    {
                        dd.DesignateThing(c);
                    }
                    Info($"F10 deconstruct designated on C {c} at {pfCCell}: {ok.Accepted} '{ok.Reason}'");
                    SetWork(map, true);
                    SeedTicks("powerfloors_decon");
                    TickUntil(20000, () => !pfCCell.GetThingList(map).Any(t => t.def == PfConduit), "pf_cut", 20);
                    return true;
                }
                case "pf_cut":
                    tickWatch = null;
                    SetWork(map, false);
                    Result("F10 a colonist deconstructs C", !pfCCell.GetThingList(map).Any(t => t.def == PfConduit), $"after {ticksTotal} ticks");
                    PfTicks("cut", 300, "pf_cut2");
                    return true;
                case "pf_cut2":
                {
                    CompPowerTrader l = PfLamp(pfC, map);
                    Result("F10 C removed: the floor-2 lamp is dark, hooked to nothing", l != null && !l.PowerOn && l.connectParent == null, PfState(l, map));
                    Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    steel.stackCount = 20;
                    GenSpawn.Spawn(steel, PfAt(pfC, 3, 9, 0, map), map);
                    GenConstruct.PlaceBlueprintForBuild(PfConduit, pfCCell, map, Rot4.North, Faction.OfPlayer, null);
                    SetWork(map, true);
                    SeedTicks("powerfloors_rebuild");
                    TickUntil(20000, () => pfCCell.GetThingList(map).Any(t => t.def == PfConduit), "pf_rebuilt", 20);
                    return true;
                }
                case "pf_rebuilt":
                    tickWatch = null;
                    SetWork(map, false);
                    Result("F10 a colonist builds C again (blueprint, construction job)", pfCCell.GetThingList(map).Any(t => t.def == PfConduit), $"after {ticksTotal} ticks");
                    PfTicks("rebuilt", 300, "pf_rebuilt2");
                    return true;
                case "pf_rebuilt2":
                    PfExpect(pfC, map, "C rebuilt by a colonist");
                    stage = "pf_save";
                    return true;

                // ---- save + reload
                case "pf_save":
                    savedTicks = Find.TickManager.TicksGame;
                    GameDataSaveLoader.SaveGame("pz_powerfloors");
                    Result("saved pz_powerfloors", File.Exists(GenFilePaths.FilePathForSavedGame("pz_powerfloors")), "");
                    stage = "pf_loaded";
                    pendingLoad = "pz_powerfloors";
                    return true;
                case "pf_loaded":
                    Info($"reloaded pz_powerfloors at tick {ticksAfterLoad} (saved {savedTicks})");
                    foreach (PfFix f in pfFix)
                    {
                        CompPowerTrader l = PfLamp(f, map);
                        Result($"{f.name} right after the load: hooked up as saved", f.wantLine < 0 ? l != null && l.connectParent == null : l?.connectParent?.parent.Position == PfLineCell(f, f.wantLine, map), PfState(l, map));
                    }
                    PfTicks("loaded", 300, "pf_loaded2");
                    return true;
                case "pf_loaded2":
                    foreach (PfFix f in pfFix)
                    {
                        PfExpect(f, map, "after reload");
                    }
                    stage = "pf_end";
                    return true;
                case "pf_end":
                    WatchCheck(map, "powerfloors end");
                    Finish();
                    return true;
            }
            return false;
        }

        private static int PfFloor(IntVec3 c, Map map) => c.IsValid ? ProjectZ.FloorOf(c, map) + 1 : 0;

        private static string PfState(CompPowerTrader l, Map map)
        {
            if (l == null)
            {
                return "lamp missing";
            }
            IntVec3 p = l.connectParent?.parent.Position ?? IntVec3.Invalid;
            return $"lamp {l.parent.Position} f{PfFloor(l.parent.Position, map)} {(l.PowerOn ? "ON" : "off")}, hooked to {(p.IsValid ? $"{l.connectParent.parent.def.defName} {p} f{PfFloor(p, map)}" : "nothing")}, net {l.PowerNet?.GetHashCode()}";
        }

        private static void PfExpect(PfFix f, Map map, string when)
        {
            CompPowerTrader l = PfLamp(f, map);
            string what;
            bool ok;
            if (f.wantLine < 0)
            {
                what = "hooked to nothing, dark";
                ok = l != null && l.connectParent == null && !l.PowerOn;
            }
            else
            {
                IntVec3 want = PfLineCell(f, f.wantLine, map);
                what = $"hooked to the conduit {want} on floor {PfFloor(want, map)}, lit";
                ok = l != null && l.connectParent?.parent.Position == want && l.PowerOn && l.PowerNet != null && l.PowerNet == map.powerNetGrid.TransmittedPowerNetAt(want);
            }
            string lines = string.Join("; ", f.lines.Select((x, i) => $"line {i}: f{x.k + 1} dx {x.dx} dz {x.dz}"));
            Result($"{f.name} {when}: {what}", ok, $"{PfState(l, map)}; lamp on floor {f.kL + 1}; {lines}");
        }

        private static void PfLines(PfFix f, Map map)
        {
            ThingDef battery = DefDatabase<ThingDef>.GetNamed("Battery");
            foreach ((int k, int dx, int dz) in f.lines)
            {
                for (int x = dx; x <= 8; x++)
                {
                    Place(PfConduit, null, PfAt(f, x, dz, k, map), map);
                }
                Thing b = Place(battery, null, PfAt(f, 9, dz, k, map), map, Rot4.North);
                CompPowerBattery cb = b.TryGetComp<CompPowerBattery>();
                cb?.AddEnergy(cb.Props.storedEnergyMax);
            }
        }

        private static void PfSetup(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            DebugSettings.enableRandomMentalStates = false;
            Find.PlaySettings.autoRebuild = false;
            Find.Storyteller.storytellerComps.Clear();
            Find.Storyteller.incidentQueue.Clear();
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            AnnexMapComponent an = ProjectZ.Annex(map);
            bool floors = mode != "powerfloors1";
            if (floors && (an == null || an.floors < 3))
            {
                Result("powerfloors setup", false, $"needs 3 floors (-projectzfloors=3); floors {an?.floors}");
                Finish();
                return;
            }
            if (!floors && an != null)
            {
                Result("powerfloors1 setup", false, "needs a map without floors (-projectzfloors=1)");
                Finish();
                return;
            }
            pfFix.Clear();
            if (floors)
            {
                PfAdd("F1", 0, 0, false, 0, (1, 3, 0));
                PfAdd("F2", 1, 1, false, 0, (0, 3, 0));
                PfAdd("F3", 2, 0, false, 0, (0, 5, 1), (1, 1, 2));
                PfAdd("F4", 3, 0, false, -1, (2, 2, 0));
                PfAdd("F5", 4, 1, false, 0, (0, 2, 1), (2, 4, 2));
                PfAdd("F6", 5, 1, false, 1, (0, 4, 1), (2, 2, 2));
                PfAdd("F7", 6, 0, true, 0, (1, 3, 0));
                PfAdd("F8", 7, 1, true, 1, (0, 4, 1), (2, 2, 2));
                PfAdd("F9", 8, 0, true, 0, (0, 5, 1), (1, 1, 2));
                pfC = PfAdd("F10", 9, 1, false, 0, (0, 6, 6));
            }
            else
            {
                PfAdd("V1", 0, 0, false, 0, (0, 3, 0));
                PfAdd("V2", 1, 0, false, -1);
                PfAdd("V3", 2, 0, true, 0, (0, 3, 0));
            }
            CellRect field = new CellRect(8, 6, 4 * 22 + 6, 3 * 16 + 4);
            ExClear(map, field);
            foreach (IntVec3 c in field)
            {
                map.terrainGrid.SetTerrain(c, TerrainDefOf.Concrete);
                map.fogGrid.Unfog(c);
            }
            // (roofs the cleared ruins held were marked to collapse; the field is unroofed, the fixtures' slabs carried)
            Info($"roof cells marked to collapse after clearing: {map.roofCollapseBuffer.CellsMarkedToCollapse.Count} (cleared)");
            map.roofCollapseBuffer.Clear();
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (p.Faction != Faction.OfPlayer)
                {
                    p.Destroy();
                }
            }
            ThingDef column = DefDatabase<ThingDef>.GetNamed("Column");
            (int, int)[] posts = { (-1, -1), (4, -1), (10, -1), (-1, 3), (4, 3), (10, 3), (-1, 7), (4, 7), (10, 7) };
            foreach (PfFix f in pfFix)
            {
                // wood slabs on every floor up to the highest one used, carried by columns on the floors below (no slab or
                // roof collapse in the fixture)
                int kmax = f.lines.Select(x => x.k).Concat(new[] { f.kL }).Max();
                for (int k = 1; k <= kmax; k++)
                {
                    for (int dx = -1; dx <= 10; dx++)
                    {
                        for (int dz = -1; dz <= 7; dz++)
                        {
                            IntVec3 u = PfAt(f, dx, dz, k, map);
                            map.terrainGrid.SetTerrain(u, PZDefOf.PZ_UpperFloor);
                            map.fogGrid.Unfog(u);
                        }
                    }
                }
                for (int k = 0; k < kmax; k++)
                {
                    foreach ((int dx, int dz) in posts)
                    {
                        Place(column, ThingDefOf.WoodLog, PfAt(f, dx, dz, k, map), map);
                    }
                }
                if (!f.linesLater)
                {
                    PfLines(f, map);
                }
            }
            if (pfC != null)
            {
                pfCCell = PfLineCell(pfC, 0, map);
            }
            List<Pawn> cols = Colonists(map);
            IntVec3 home = pfC != null ? PfAt(pfC, 3, 10, 0, map) : new IntVec3(field.CenterCell.x, 0, field.maxZ - 1);
            for (int i = 0; i < cols.Count; i++)
            {
                Draft(cols[i], false);
                Teleport(cols[i], home + new IntVec3(i, 0, 0));
            }
            SetWork(map, false);
            Info($"powerfloors: {pfFix.Count} fixtures, field {field}, floors {an?.floors ?? 1}, ground rows {ProjectZ.GroundZ(map)}, stride {an?.stride}; F10 C {pfCCell}; " +
                $"patches across {FailSoft.IsApplied(nameof(Patches.BestTransmitterForConnector_Across_Patch))}/{FailSoft.IsApplied(nameof(Patches.PotentialConnectorsForTransmitter_Across_Patch))}, own-floor clip {FailSoft.IsApplied(nameof(Patches.BestTransmitterForConnector_Floor_Patch))}/{FailSoft.IsApplied(nameof(Patches.PotentialConnectorsForTransmitter_Floor_Patch))}");
            Result("hookup patches applied", FailSoft.IsApplied(nameof(Patches.BestTransmitterForConnector_Across_Patch)) && FailSoft.IsApplied(nameof(Patches.PotentialConnectorsForTransmitter_Across_Patch)), "");
            AnnexWatch.Reset();
            PfTicks("setup", 60, "pf_lamps");
        }
    }
}
