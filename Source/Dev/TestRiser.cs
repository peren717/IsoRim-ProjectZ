using System.Collections.Generic;
using System.Linq;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode riser (how a vertical power link looks, RiserView.cs; run with -projectzfloors=3 and IsoRim's 3D view). House
    // H (11 x 8): ground walls and a door, a slab on floor 2 with stairs up inside. A wood-fired generator west of H feeds
    // a ground conduit line into H (a hidden conduit under the west wall). Colonists build from blueprints:
    //   R on the ground at the end of the line, and R2 over it on floor 2 (they carry steel up the stairs); floor 2 has a
    //   conduit line on from R2 to a lamp L2.
    // Prebuilt pair S: a hidden conduit under H's east wall with a normal conduit over it on floor 2 (the piece shows only
    // on floor 2, as the hidden conduit shows nothing).
    // Checks: the drawn risers are exactly the conduit pairs the power rule links, ends on one net; R + R2 appear once
    // both are built (L2 lit); a colonist deconstructs R: that riser goes (L2 dark), S stays. Shots: normal view and
    // power overlay, floors 1 and 2, yaw 45 and 225, after the build and after the cut.
    public static partial class TestRunner
    {
        private static IntVec3 jO, jGen, jR, jS, jL2g;
        private static CellRect jHouse;
        private static ThingDef jConduit, jHidden, jLamp;
        private static int jShot;
        private static string jPhase;
        private static float jOldYaw;

        private static readonly (int floor, bool overlay, float yaw)[] JShots =
        {
            (0, false, 0f), (0, false, 180f), (1, false, 0f), (1, false, 180f),
            (0, true, 0f), (0, true, 180f), (1, true, 0f), (1, true, 180f),
        };

        private static IntVec3 J(int dx, int dz) => new IntVec3(jO.x + dx, 0, jO.z + dz);

        private static IntVec3 JUp(IntVec3 g) => ProjectZ.CellOnFloor(g, 1, Find.CurrentMap);

        private static bool StepRiser(Map map)
        {
            switch (stage)
            {
                case "j_setup":
                    SetupRiser(map);
                    return true;
                case "j_built":
                    tickWatch = null;
                    SetWork(map, false);
                    Result("colonists build R (ground) and R2 over it (floor 2)", NThing(map, jR, jConduit) != null && NThing(map, JUp(jR), jConduit) != null, $"after {ticksTotal} ticks");
                    StartTicks(600, "j_check1");
                    return true;
                case "j_check1":
                {
                    JCheck(map, "built");
                    bool has = RiserView.For(map).Any(r => r.low == jR && r.high == JUp(jR));
                    CompPowerTrader l2 = NLampAt(map, JUp(jL2g));
                    Result("built: riser R-R2 is drawn, on the generator's net, L2 lit", has && NNet(map, JUp(jR)) == NNet(map, jGen) && l2 != null && l2.PowerOn, $"riser {has}, R2 net {NNet(map, JUp(jR))?.GetHashCode()}, ground {NNet(map, jGen)?.GetHashCode()}, L2 {NOn(l2)}");
                    jPhase = "1built";
                    jShot = 0;
                    Wait(2, "j_shot");
                    return true;
                }
                case "j_shot":
                {
                    if (jShot >= JShots.Length)
                    {
                        Find.DesignatorManager.Deselect();
                        fakeMouse = null;
                        IsoRimBridge.SetOption("freeYaw", jOldYaw);
                        Wait(5, jPhase == "1built" ? "j_decon" : "j_end");
                        return true;
                    }
                    if (jShot == 0)
                    {
                        jOldYaw = (float)(IsoRimBridge.SetOption("freeYaw", 0f) ?? 0f);
                    }
                    var s = JShots[jShot];
                    IsoRimBridge.SetOption("freeYaw", s.yaw);
                    if (s.overlay)
                    {
                        fakeMouse = new Vector2(4f, 4f);
                        if (!(Find.DesignatorManager.SelectedDesignator is Designator_Build))
                        {
                            Find.DesignatorManager.Select(BuildCopyCommandUtility.FindAllowedDesignator(jConduit) ?? new Designator_Build(jConduit));
                        }
                    }
                    else
                    {
                        Find.DesignatorManager.Deselect();
                        fakeMouse = null;
                    }
                    ShotAt(map, s.floor, J(6, 4), 4.5f);
                    Wait(45, "j_shot_take");
                    return true;
                }
                case "j_shot_take":
                {
                    var s = JShots[jShot];
                    string name = $"riser_{jPhase}_f{s.floor + 1}_{(s.overlay ? "overlay" : "normal")}_yaw{(s.yaw == 0f ? 45 : 225)}";
                    Info($"shot {name}: drawn pieces {RiserView.lastPieces}, overlay links {RiserView.lastWires} (frame {RiserView.lastFrame}, now {Time.frameCount}), power overlay {OverlayDrawHandler.ShouldDrawPowerGrid}");
                    // built: R-R2 and S on floor 2, R only on floor 1 (S's lower end is a hidden conduit); cut: S only, on floor 2
                    int want = jPhase == "1built" ? 2 : 1;
                    int wantPieces = jPhase == "1built" ? (s.floor == 0 ? 1 : 2) : (s.floor == 0 ? 0 : 1);
                    if (s.overlay)
                    {
                        Result($"{name}: one overlay link per riser", RiserView.lastWires == want, $"{RiserView.lastWires} of {want}");
                    }
                    else
                    {
                        Result($"{name}: no overlay link without the power overlay", RiserView.lastWires == 0, $"{RiserView.lastWires}");
                    }
                    Result($"{name}: one conduit piece per riser", RiserView.lastPieces == wantPieces, $"{RiserView.lastPieces} of {wantPieces}");
                    ZCapture(name);
                    jShot++;
                    Wait(2, "j_shot");
                    return true;
                }

                // ---- a colonist deconstructs R
                case "j_decon":
                {
                    Thing r = NThing(map, jR, jConduit);
                    Designator_Deconstruct dd = new Designator_Deconstruct();
                    AcceptanceReport ok = r == null ? "no R" : dd.CanDesignateThing(r);
                    if (ok.Accepted)
                    {
                        dd.DesignateThing(r);
                    }
                    Info($"deconstruct designated on R {r} at {jR}: {ok.Accepted} '{ok.Reason}'");
                    SetWork(map, true);
                    TickUntil(20000, () => NThing(map, jR, jConduit) == null, "j_cut", 20);
                    return true;
                }
                case "j_cut":
                    tickWatch = null;
                    SetWork(map, false);
                    Result("a colonist deconstructs R", NThing(map, jR, jConduit) == null, $"after {ticksTotal} ticks");
                    StartTicks(600, "j_cut2");
                    return true;
                case "j_cut2":
                {
                    JCheck(map, "cut");
                    bool has = RiserView.For(map).Any(r => r.ground == jR);
                    bool s = RiserView.For(map).Any(r => r.low == jS);
                    CompPowerTrader l2 = NLampAt(map, JUp(jL2g));
                    Result("cut: riser R-R2 is gone, S stays, L2 dark", !has && s && l2 != null && !l2.PowerOn, $"R riser {has}, S riser {s}, L2 {NOn(l2)}");
                    jPhase = "2cut";
                    jShot = 0;
                    Wait(2, "j_shot");
                    return true;
                }
                case "j_end":
                    WatchCheck(map, "riser end");
                    Finish();
                    return true;
            }
            return false;
        }

        // The drawn risers against the power rule and the nets: every drawn riser joins two conduits of one net, and every
        // conduit with a conduit stacked over it is drawn (so the list is not stale).
        private static void JCheck(Map map, string when)
        {
            List<RiserView.Riser> drawn = RiserView.For(map);
            int bad = 0;
            foreach (RiserView.Riser r in drawn)
            {
                PowerNet a = NNet(map, r.low), b = NNet(map, r.high);
                if (a == null || a != b)
                {
                    bad++;
                    Info($"{when}: riser {r.low} -> {r.high} joins nets {a?.GetHashCode()} / {b?.GetHashCode()}");
                }
            }
            int stacked = 0, missing = 0;
            foreach (Thing t in map.listerThings.AllThings.Where(x => x is Building && PowerRisers.IsConduit(x.def)).ToList())
            {
                IntVec3 up = StairShape.Above(t.Position, map);
                if (PowerRisers.ConduitAt(up, map) != null)
                {
                    stacked++;
                    if (!drawn.Any(r => r.low == t.Position && r.high == up))
                    {
                        missing++;
                    }
                }
            }
            Result($"{when}: drawn risers = stacked conduit pairs, each on one net", bad == 0 && missing == 0 && drawn.Count == stacked,
                $"{drawn.Count} drawn ({string.Join(", ", drawn.Select(r => $"{r.low}->{r.high} drawn {r.lowDrawn}/{r.highDrawn}"))}), {stacked} stacked pairs, {missing} missing, {bad} on split nets");
        }

        private static void SetupRiser(Map map)
        {
            NResolve(map);
            jConduit = nConduit;
            jHidden = nHidden;
            jLamp = nLamp;
            List<Pawn> cols = Colonists(map);
            IntVec3 near = cols.Count > 0 ? new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z)) : map.Center;
            CellRect found = CellRect.Empty;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 79f, true))
            {
                CellRect r = new CellRect(c.x - 8, c.z - 5, 19, 10);
                CellRect m = r.ExpandedBy(2);
                if (!m.InBounds(map) || m.maxZ >= ProjectZ.GroundZ(map) - 8)
                {
                    continue;
                }
                if (m.Cells.All(x => !x.GetTerrain(map).IsWater && !map.roofGrid.Roofed(x) && !x.Fogged(map) && !(x.GetEdifice(map)?.def.building?.isNaturalRock ?? false) && x.GetTerrain(map).passability == Traversability.Standable))
                {
                    found = r;
                    break;
                }
            }
            if (found.Area == 0)
            {
                Result("test spot", false, "no open ground near the colonists");
                Finish();
                return;
            }
            foreach (IntVec3 c in found.ExpandedBy(2))
            {
                NClear(c, map);
            }
            DebugSettings.enableRandomMentalStates = false;
            Find.PlaySettings.autoRebuild = false;
            jO = new IntVec3(found.minX + 6, 0, found.minZ + 1);
            jHouse = new CellRect(jO.x, jO.z, 11, 8);
            IntVec3 door = J(5, 0);
            IntVec3 stairs = J(2, 3);   // up to the north: footprint J(2, 2..4), landing over J(2, 5)
            HashSet<IntVec3> well = new HashSet<IntVec3>(StairShape.Ramp(stairs, Rot4.North));
            foreach (IntVec3 g in jHouse.EdgeCells)
            {
                Place(g == door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog, g, map);
            }
            foreach (IntVec3 g in jHouse)
            {
                map.areaManager.Home[g] = true;
                if (!well.Contains(g))
                {
                    map.terrainGrid.SetTerrain(JUp(g), PZDefOf.PZ_UpperFloor);
                }
            }
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, stairs, map, Rot4.North);
            // generator west of H, ground line along row 3 to R's west neighbour (hidden under the west wall)
            jGen = J(-4, 3);
            Thing gen = Place(DefDatabase<ThingDef>.GetNamed("WoodFiredGenerator"), null, jGen, map);
            gen.TryGetComp<CompRefuelable>()?.Refuel(500f);
            jR = J(6, 3);
            for (int x = gen.OccupiedRect().maxX + 1; x < jR.x; x++)
            {
                IntVec3 c = new IntVec3(x, 0, jO.z + 3);
                if (!well.Contains(c))
                {
                    Place(c.x == jO.x ? jHidden : jConduit, null, c, map);
                }
            }
            // the stairs sit on row 3 too: the line goes round them one row south
            foreach (IntVec3 c in new[] { J(1, 2), J(2, 1), J(3, 2), J(1, 1), J(3, 1) })
            {
                if (!c.GetThingList(map).Any(t => t.def == jConduit))
                {
                    Place(jConduit, null, c, map);
                }
            }
            // floor 2: line from R2 east to a lamp L2
            Place(jConduit, null, JUp(J(7, 3)), map);
            Place(jConduit, null, JUp(J(8, 3)), map);
            jL2g = J(8, 4);
            Place(jLamp, null, JUp(jL2g), map);
            // pair S: hidden conduit under the east wall, a conduit over it on floor 2
            jS = J(10, 5);
            Place(jHidden, null, jS, map);
            Place(jConduit, null, JUp(jS), map);
            // blueprints R and R2, steel by the door
            GenConstruct.PlaceBlueprintForBuild(jConduit, jR, map, Rot4.North, Faction.OfPlayer, null);
            GenConstruct.PlaceBlueprintForBuild(jConduit, JUp(jR), map, Rot4.North, Faction.OfPlayer, null);
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 20;
            GenSpawn.Spawn(steel, J(5, 1), map);
            foreach (Pawn p in cols)
            {
                Draft(p, false);
            }
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            Info($"riser: house {jHouse}, stairs {stairs} (landing {StairShape.Exit(stairs, Rot4.North, map)}), generator {jGen}, R {jR} -> R2 {JUp(jR)}, S {jS} -> {JUp(jS)}, lamp L2 {JUp(jL2g)}; ground rows {ProjectZ.GroundZ(map)}, stride {ProjectZ.Annex(map)?.stride}, floors {ProjectZ.Annex(map)?.floors}");
            AnnexWatch.Reset();
            SetWork(map, true);
            TickUntil(40000, () => NThing(map, jR, jConduit) != null && NThing(map, JUp(jR), jConduit) != null, "j_built", 30);
        }
    }
}
