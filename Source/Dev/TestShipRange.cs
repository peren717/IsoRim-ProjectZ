using System.Collections.Generic;
using System.Linq;
using ProjectZ.Combat;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Test mode shiprange (multi-level gravships P3; -projectzfloors=3, Odyssey, IsoRim 3D): the footprint range preview.
    //   The shipset fixture (a ground ship with decks over it on floors 2 and 3, a strip running east) plus a grav field
    //   extender on a floor-2 deck (spawned: building it for real is covered by shipset S3). Then:
    //   R1 the cross-sections: on the maker's own floor the cells are vanilla's circle (GenRadial, clipped to the band);
    //      on every floor each cell drawn is inside the footprint (GravshipUtility.InsideFootprint) and the cells next to
    //      the drawn edge are not;
    //   R2 shots through the game's own paths: the engine selected, viewed from the ground, floor 2 and floor 3 (rings on the
    //      ground and the viewed floor, one shell); the extender selected (its shell); placing an extender on a floor-2
    //      deck (the pointer over the deck: the ghost's sphere joins the drawing, its shell shown); placing a ship deck; a
    //      shard inhibitor (Anomaly) placed on floor 2 (the facility ring cut on the ground);
    public static partial class TestRunner
    {
        private static int srShot;
        private static Building srExt;
        private static readonly List<(string name, int view, string what)> srShots = new List<(string, int, string)>
        {
            ("sr_1_engine_view_ground", 0, "engine"),
            ("sr_2_engine_view_f2", 1, "engine"),
            ("sr_3_engine_view_f3", 2, "engine"),
            ("sr_4_extender_selected_f2", 1, "extender"),
            ("sr_5_place_extender_f2", 1, "place_extender"),
            ("sr_6_place_extender_view_ground", 0, "place_extender_ground"),
            ("sr_7_place_deck_f2", 1, "place_deck"),
            ("sr_8_place_inhibitor_f2", 1, "place_cabinet"),
        };
        private static readonly Dictionary<string, string> srSeen = new Dictionary<string, string>();

        private static void StepShipRange(Map map)
        {
            Building_GravEngine e = GlEngine(map);
            switch (stage)
            {
                case "sr_setup":
                    SetupShipSet(map, "sr_ext");
                    return;
                case "sr_ext":
                {
                    ThingDef extDef = DefDatabase<ThingDef>.GetNamed("GravFieldExtender");
                    srExt = (Building)Place(extDef, null, SSF(13, 3, 1, map), map);
                    StartTicks(120, "sr_check");
                    return;
                }
                case "sr_check":
                {
                    CompGravshipFacility f = srExt.TryGetComp<CompGravshipFacility>();
                    Result("R0 the extender on the floor-2 deck links", f != null && f.LinkedBuildings.Contains(e) && f.CanBeActive, $"linked {f?.LinkedBuildings.Count}");
                    AnnexMapComponent an = ProjectZ.Annex(map);
                    float r = e.GetComp<CompSubstructureFootprint>().Props.radius;
                    IntVec3 mg = e.Position;
                    // R1 own floor = vanilla's circle
                    List<IntVec3> own = new List<IntVec3>();
                    ShipRange.Section(mg, 0, r, 0, map, an, own);
                    HashSet<IntVec3> van = new HashSet<IntVec3>(GenRadial.RadialCellsAround(mg, r, true).Where(c => c.x >= 0 && c.x < map.Size.x && c.z >= 0 && c.z < an.groundZ));
                    Result("R1 on the engine's floor the cells are vanilla's circle", own.Count == van.Count && own.All(van.Contains), $"{own.Count} vs vanilla {van.Count}");
                    List<Thing> makers = new List<Thing> { e };
                    for (int k = 0; k < an.floors; k++)
                    {
                        List<IntVec3> sec = new List<IntVec3>();
                        ShipRange.Section(mg, 0, r, k, map, an, sec);
                        HashSet<IntVec3> set = new HashSet<IntVec3>(sec);
                        int inside = sec.Count(c => GravshipUtility.InsideFootprint(c, map, makers));
                        int edgeOut = 0, edge = 0;
                        foreach (IntVec3 c in sec)
                        {
                            foreach (IntVec3 d in GenAdj.CardinalDirections)
                            {
                                IntVec3 n = c + d;
                                if (!set.Contains(n) && ProjectZ.FloorOf(n, map) == k && n.InBounds(map))
                                {
                                    edge++;
                                    if (!GravshipUtility.InsideFootprint(n, map, makers))
                                    {
                                        edgeOut++;
                                    }
                                }
                            }
                        }
                        float rk = Mathf.Sqrt(Mathf.Max(0f, r * r - k * Range3D.Storey * k * Range3D.Storey));
                        Result($"R1 floor {k + 1}: every drawn cell is in the footprint, every cell past the edge is not", inside == sec.Count && edgeOut == edge && sec.Count > 0,
                            $"{sec.Count} cells (radius there {rk:0.00}), in {inside}; edge neighbours {edge}, outside {edgeOut}");
                    }
                    srShot = 0;
                    Wait(5, "sr_shot");
                    return;
                }
                case "sr_shot":
                {
                    Find.DesignatorManager.Deselect();
                    fakeMouse = null;
                    if (srShot >= srShots.Count)
                    {
                        Find.Selector.ClearSelection();
                        Wait(5, "sr_end");
                        return;
                    }
                    var s = srShots[srShot];
                    ShotAt(map, s.view, SS(10, 6), 32f);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    switch (s.what)
                    {
                        case "engine":
                            Find.Selector.Select(e);
                            break;
                        case "extender":
                            Find.Selector.Select(srExt);
                            break;
                        case "place_extender":
                        case "place_extender_ground":
                            Find.DesignatorManager.Select(new Designator_Build(DefDatabase<ThingDef>.GetNamed("GravFieldExtender")));
                            break;
                        case "place_deck":
                            Find.DesignatorManager.Select(new Designator_Build(PZDefOf.PZ_ShipDeck));
                            break;
                        case "place_cabinet":
                            Find.DesignatorManager.Select(new Designator_Build(DefDatabase<ThingDef>.GetNamed("ShardInhibitor")));
                            break;
                    }
                    Wait(20, "sr_point");
                    return;
                }
                case "sr_point":
                {
                    // the pointer: over a floor-2 deck at the ship's north-west (on the ground for the ground view)
                    var s = srShots[srShot];
                    if (s.what.StartsWith("place"))
                    {
                        IntVec3 g = s.what == "place_cabinet" ? SS(16, 1) : SS(3, 10);
                        fakeMouse = ScreenOf(g, s.view);
                    }
                    Wait(20, "sr_take");
                    return;
                }
                case "sr_take":
                {
                    var s = srShots[srShot];
                    string cells = string.Join(", ", ShipRange.LastCells.OrderBy(kv => kv.Key).Select(kv => $"f{kv.Key + 1} {kv.Value}"));
                    bool fresh = Time.frameCount - ShipRange.LastFrame <= 2;
                    string info = $"drawn {fresh}, cells {cells}, shells {ShipRange.LastShells}, mouse {UI.MouseCell()}, facility rings {ShipRange_FacilityRings()}";
                    Info($"shot {s.name}: {info}");
                    srSeen[s.name] = info;
                    switch (s.what)
                    {
                        case "engine":
                            Result($"R2 {s.name}: rings on the ground{(s.view > 0 ? " and the viewed floor" : "")}, one shell", fresh && ShipRange.LastCells.Count == (s.view > 0 ? 2 : 1) && ShipRange.LastShells == 1, info);
                            break;
                        case "extender":
                            Result($"R2 {s.name}: the extender's shell", fresh && ShipRange.LastShells == 1, info);
                            break;
                        case "place_extender":
                        case "place_extender_ground":
                            Result($"R2 {s.name}: the ghost is on the viewed floor, its sphere and shell drawn", fresh && ShipRange.LastShells == 1 && ProjectZ.FloorOf(UI.MouseCell(), map) == s.view, info);
                            break;
                        case "place_deck":
                            Result($"R2 {s.name}: the footprint is drawn while placing a deck, no shell", fresh && ShipRange.LastCells.Count == 2 && ShipRange.LastShells == 0, info);
                            break;
                        case "place_cabinet":
                            Result($"R2 {s.name}: the facility ring cut on the ground", ShipRange_FacilityRings() == 1 && ProjectZ.FloorOf(UI.MouseCell(), map) == 1, info);
                            break;
                    }
                    ZCapture(s.name);
                    srShot++;
                    Wait(3, "sr_shot");
                    return;
                }
                case "sr_end":
                {
                    Finish();
                    return;
                }
            }
        }

        private static int ShipRange_FacilityRings() => ShowFacilitiesRange_Floors_Patch.LastRings;
    }
}
