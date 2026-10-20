using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // Multi-level gravships, phase P4 (isorim-pz-multilevel-gravship.md, v2.1 F, v2.4): upper decks fly.
    //
    // The one encoding: a ship-relative offset keeps the floor in IntVec3.y. A cell on floor k is stored as
    // (gx - ogx, k, gz - ogz): its ground column relative to the engine's ground column, plus its absolute floor. Maps never
    // use y, saves keep it, and vanilla's rotation (PrefabUtility.GetAdjustedLocalPosition, GenAdj.AdjustForRotation)
    // turns x and z and keeps y, so the floor turns with the ship for free and doesn't depend on the target map's stride.
    // Every flat reader (bounds, occupied rects, the marker, the orbit map's size) sees the footprint only.
    //   - Capture (Gravship..ctor: TransferZones, CopyCellContents, CopyAreas): every "cell - origin" is From.
    //     The ship set already holds the deck cells (P2), so the decks, things, pawns, items, filth, zones, areas, roofs,
    //     gases, designations, power links and room temperatures of upper floors are captured by vanilla's own code.
    //     An upper cell's foundation is its deck (never null), its terrain the deck or what is laid on it.
    //   - Take-off removal is vanilla's (top = under ?? top = open air, the foundation and roofs go); afterwards
    //     ProjectZ's caches are refreshed (GenerateGravship postfix; left-behind decks fall: ShipDeck.CheckAll).
    //   - Placement (GravshipPlacementUtility, MoveableStockpile / GrowZone / Area_Allowed): every "root + local" is To:
    //     the ground cell root + (x, 0, z) lifted to floor y on the target map.
    //   - Safety net: a cell with y != 0 reaching GenSpawn, the terrain, roof, zone or area grids is an error (logged once)
    //     and is put on its floor (a missed or new site).
    //   - The engine on a deck: the cutscene's engine position and engineToCenter use its ground column.
    // If a transpiler doesn't find its site (a game update, another mod), upper decks can't launch (Ready: one log line).
    // Landing (the marker, the column check): ShipLanding.cs.
    public static class ShipLocal
    {
        private static Map capMap;            // while a Gravship captures (its engine's map)
        [ThreadStatic] private static Map placeMap;   // while a gravship is placed (the landing map)

        // Every site found (capture and placement); else upper decks can't launch.
        public static bool Ready => CapturePatch.Ok && PlacePatch.Ok;

        // The ship-relative offset of cell a from origin (both raw map cells): ground columns subtracted, a's floor in y.
        public static IntVec3 From(IntVec3 a, IntVec3 origin, Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || a.z < an.groundZ && origin.z < an.groundZ)
            {
                return a - origin;
            }
            IntVec3 ga = ProjectZ.GroundCellOf(a, map), go = ProjectZ.GroundCellOf(origin, map);
            int k = ProjectZ.FloorOf(a, map);
            if (!ga.IsValid || !go.IsValid || k < 0)
            {
                Log.ErrorOnce($"[ProjectZ] gravship capture: cell {a} (origin {origin}) is on no floor", 81723401);
                return a - origin;
            }
            return new IntVec3(ga.x - go.x, k, ga.z - go.z);
        }

        // The map cell for a ship-relative offset at root (a ground cell): root + (x, 0, z) lifted to floor y.
        public static IntVec3 To(IntVec3 root, IntVec3 local, Map map)
        {
            int k = root.y + local.y;
            IntVec3 g = new IntVec3(root.x + local.x, 0, root.z + local.z);
            if (k == 0)
            {
                return g;
            }
            IntVec3 c = map == null ? IntVec3.Invalid : ProjectZ.CellOnFloor(g, k, map);
            if (!c.IsValid)
            {
                // (landing refuses maps with too few floors; this is the last resort: the ground cell)
                Log.ErrorOnce($"[ProjectZ] gravship placement: floor {k + 1} of {g} doesn't exist on {map?.ToString() ?? "no map"}; put on the ground", 81723402);
                return g;
            }
            return c;
        }

        // Transpiled into the capture: "a - origin".
        public static IntVec3 Sub(IntVec3 a, IntVec3 origin)
        {
            return capMap == null ? a - origin : From(a, origin, capMap);
        }

        // Transpiled into the placement: "root + local".
        public static IntVec3 Add(IntVec3 root, IntVec3 local)
        {
            if (root.y + local.y == 0)
            {
                return new IntVec3(root.x + local.x, 0, root.z + local.z);
            }
            return To(root, local, placeMap);
        }

        internal static void BeginCapture(Map map) => capMap = ProjectZ.Annex(map) != null ? map : null;

        internal static void EndCapture() => capMap = null;

        internal static Map BeginPlace(Map map)
        {
            Map was = placeMap;
            placeMap = map;
            return was;
        }

        internal static void EndPlace(Map was) => placeMap = was;

        // The safety net: a cell carrying a floor tag on a real map.
        public static bool Fix(ref IntVec3 c, Map map, string where)
        {
            if (c.y == 0 || !c.IsValid)
            {
                return false;
            }
            IntVec3 was = c;
            IntVec3 g = new IntVec3(c.x, 0, c.z);
            AnnexMapComponent an = ProjectZ.Annex(map);
            IntVec3 up = an != null && c.y > 0 && c.y <= an.MaxLevel && g.z < an.groundZ ? ProjectZ.CellOnFloor(g, c.y, map) : IntVec3.Invalid;
            c = up.IsValid ? up : g;
            Nets++;
            Log.ErrorOnce($"[ProjectZ] {where}: cell {was} still carries a gravship floor tag; put at {c}", 81723403 ^ where.GetHashCode());
            return true;
        }

        public static int Nets;   // cells the safety net put right (tests: 0)

        // After a take-off or a landing (vanilla writes the grids directly there): ProjectZ's caches.
        internal static void Refresh(Map map)
        {
            if (map == null || ProjectZ.Annex(map) == null)
            {
                return;
            }
            FloorHeat.Dirty();
            SlabPeel.Dirty(map);
            ReachFog.Request(map);
            map.reachability?.ClearCache();
            // The upper decks' cells turned to open air (walkability changed: vanilla's path-cost pass dirtied their
            // regions). Rebuild now: vanilla's vacuum rebuild right after a take-off (a map with a grav anchor) walks
            // the room list and asks rooms by cell, which would rebuild regions mid-walk ("collection was modified").
            if (map.regionDirtyer.AnyDirty)
            {
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            }
        }
    }

    // ---- capture: "cell - origin" -> ShipLocal.Sub

    [HarmonyPatch]
    public static class CapturePatch
    {
        private static readonly MethodInfo sub = AccessTools.Method(typeof(IntVec3), "op_Subtraction", new[] { typeof(IntVec3), typeof(IntVec3) });
        private static readonly MethodInfo ours = AccessTools.Method(typeof(ShipLocal), nameof(ShipLocal.Sub));

        // swapped per method (the test checks them), and the least each must have
        public static readonly Dictionary<string, int> PerMethod = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> Need = new Dictionary<string, int> { { "TransferZones", 2 }, { "CopyAreas", 3 }, { "CopyCellContents", 3 } };

        public static bool Ok => Need.All(kv => PerMethod.TryGetValue(kv.Key, out int n) && n >= kv.Value);

        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in Need.Keys)
            {
                yield return AccessTools.Method(typeof(Gravship), name);
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            int n = 0;
            foreach (CodeInstruction ci in code)
            {
                if (ci.Calls(sub))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = ours;
                    n++;
                }
            }
            PerMethod[original.Name] = n;
            if (n < Need[original.Name])
            {
                Log.Warning($"[ProjectZ] multi-level gravships: Gravship.{original.Name} has {n} of {Need[original.Name]} offset sites; upper decks won't launch");
            }
            ProjectZ.DebugLog($"multi-level gravships: Gravship.{original.Name}: {n} offset(s) floor-tagged");
            return code;
        }
    }

    // The capture's map, and engineToCenter from the engine's ground column (an engine on a deck).
    [HarmonyPatch(typeof(Gravship), MethodType.Constructor, typeof(Building_GravEngine))]
    public static class GravshipCtor_ShipLocal_Patch
    {
        public static void Prefix(Building_GravEngine engine)
        {
            ShipLocal.BeginCapture(engine?.Map);
        }

        public static void Postfix(Gravship __instance, Building_GravEngine engine)
        {
            Map map = engine?.Map;
            if (map != null && ProjectZ.Annex(map) != null && ModsConfig.OdysseyActive)
            {
                ShipStairs.TakeOpeningRoofs(__instance, engine, map);
            }
            if (map != null && ProjectZ.FloorOf(engine.Position, map) > 0 && ModsConfig.OdysseyActive)
            {
                IntVec3 g = ProjectZ.GroundCellOf(engine.Position, map);
                __instance.engineToCenter = CellRect.FromCellList(ShipSet.Ground(engine)).CenterVector3 - g.ToVector3();
            }
        }

        public static void Finalizer()
        {
            ShipLocal.EndCapture();
        }
    }

    // ---- placement: "root + local" -> ShipLocal.Add

    [HarmonyPatch]
    public static class PlacePatch
    {
        private static readonly MethodInfo add = AccessTools.Method(typeof(IntVec3), "op_Addition", new[] { typeof(IntVec3), typeof(IntVec3) });
        private static readonly MethodInfo ours = AccessTools.Method(typeof(ShipLocal), nameof(ShipLocal.Add));

        public static readonly Dictionary<string, int> PerMethod = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> Need = new Dictionary<string, int>();

        public static bool Ok => Need.Count > 0 && Need.All(kv => PerMethod.TryGetValue(kv.Key, out int n) && n >= kv.Value);

        private static string Key(MethodBase m) => m.DeclaringType.Name + "." + m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")";

        public static IEnumerable<MethodBase> TargetMethods()
        {
            Type u = typeof(GravshipPlacementUtility);
            List<(MethodBase, int)> list = new List<(MethodBase, int)>
            {
                (AccessTools.Method(u, "SpawnTerrain", new[] { typeof(Map), typeof(IntVec3), typeof(Dictionary<IntVec3, TerrainDef>), typeof(bool) }), 1),
                (AccessTools.Method(u, "SpawnTerrain", new[] { typeof(Gravship), typeof(Map), typeof(IntVec3) }), 1),
                (AccessTools.Method(u, "SpawnPawns"), 1),
                (AccessTools.Method(u, "SpawnNonPawnThings"), 1),
                (AccessTools.Method(u, "SpawnRoofs"), 1),
                (AccessTools.Method(u, "SpawnGas"), 1),
                (AccessTools.Method(u, "CopyDesignations"), 1),
                (AccessTools.Method(u, "CopyAreasIntoMap"), 5),
                (AccessTools.Method(u, nameof(GravshipPlacementUtility.ApplyTemperatureVacuumFromBase)), 1),
                (AccessTools.Method(typeof(MoveableStockpile), nameof(MoveableStockpile.TryCreateStockpile)), 1),
                (AccessTools.Method(typeof(MoveableGrowZone), nameof(MoveableGrowZone.TryCreateGrowZone)), 1),
                (AccessTools.Method(typeof(MoveableArea_Allowed), nameof(MoveableArea_Allowed.TryCreateArea)), 1),
            };
            foreach ((MethodBase m, int need) in list)
            {
                if (m == null)
                {
                    Need["missing " + Need.Count] = 1;
                    Log.Warning("[ProjectZ] multi-level gravships: a placement method was not found; upper decks won't launch");
                    continue;
                }
                Need[Key(m)] = need;
                yield return m;
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            int n = 0;
            foreach (CodeInstruction ci in code)
            {
                if (ci.Calls(add))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = ours;
                    n++;
                }
            }
            string key = Key(original);
            PerMethod[key] = n;
            if (Need.TryGetValue(key, out int need) && n < need)
            {
                Log.Warning($"[ProjectZ] multi-level gravships: {key} has {n} of {need} placement sites; upper decks won't launch");
            }
            ProjectZ.DebugLog($"multi-level gravships: {key}: {n} placement(s) floor-aware");
            return code;
        }
    }

    // The landing map while the ship is placed (and its rooms' temperatures restored); afterwards ProjectZ's caches.
    [HarmonyPatch(typeof(GravshipPlacementUtility), nameof(GravshipPlacementUtility.PlaceGravshipInMap))]
    public static class PlaceGravshipInMap_ShipLocal_Patch
    {
        public static void Prefix(Map map, out Map __state)
        {
            __state = ShipLocal.BeginPlace(map);
        }

        public static void Postfix(Map map)
        {
            ShipLocal.Refresh(map);
        }

        public static void Finalizer(Map __state)
        {
            ShipLocal.EndPlace(__state);
        }
    }

    [HarmonyPatch(typeof(GravshipPlacementUtility), nameof(GravshipPlacementUtility.ApplyTemperatureVacuumFromBase))]
    public static class ApplyTemperature_ShipLocal_Patch
    {
        public static void Prefix(Map map, out Map __state)
        {
            __state = ShipLocal.BeginPlace(map);
        }

        public static void Finalizer(Map __state)
        {
            ShipLocal.EndPlace(__state);
        }
    }

    // After the take-off removal (vanilla, without terrain events): ProjectZ's caches. (Left-behind decks fall:
    // GenerateGravship_ShipDeck_Patch.)
    [HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.GenerateGravship))]
    public static class GenerateGravship_ShipLocal_Patch
    {
        public static void Prefix(Building_GravEngine engine, out Map __state)
        {
            __state = engine?.Map;
        }

        public static void Postfix(Map __state)
        {
            ShipLocal.Refresh(__state);
        }
    }

    // The cutscene's engine position: the engine's ground column (vanilla passes its raw position at take-off).
    [HarmonyPatch(typeof(GravshipRenderer), nameof(GravshipRenderer.BeginCutscene))]
    public static class BeginCutscene_ShipLocal_Patch
    {
        public static void Prefix(ref Vector3 takeoffOrLandingEnginePos)
        {
            Map map = Find.CurrentMap;
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || takeoffOrLandingEnginePos.z < an.groundZ)
            {
                return;
            }
            IntVec3 g = ProjectZ.GroundCellOf(takeoffOrLandingEnginePos.ToIntVec3(), map);
            if (g.IsValid)
            {
                takeoffOrLandingEnginePos = new Vector3(takeoffOrLandingEnginePos.x, takeoffOrLandingEnginePos.y, g.z + (takeoffOrLandingEnginePos.z - Mathf.Floor(takeoffOrLandingEnginePos.z)));
            }
        }
    }

    // ---- upper decks can't launch only when a site above was not found (fail soft)

    [HarmonyPatch(typeof(Building_GravEngine), nameof(Building_GravEngine.CanLaunch))]
    public static class CanLaunch_ShipLocal_Patch
    {
        public static void Postfix(Building_GravEngine __instance, ref AcceptanceReport __result)
        {
            if (__result.Accepted && !ShipLocal.Ready && ShipSet.Upper(__instance) > 0)
            {
                __result = new AcceptanceReport("PZ_ShipUpperCantFly".Translate());
            }
        }
    }

    [HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.InitiateTakeoff))]
    public static class InitiateTakeoff_ShipLocal_Patch
    {
        public static bool Prefix(Building_GravEngine engine)
        {
            if (ShipLocal.Ready || engine == null || ShipSet.Upper(engine) == 0)
            {
                return true;
            }
            Log.Warning("[ProjectZ] take-off refused: a multi-level gravship patch failed");
            Messages.Message("PZ_ShipUpperCantFly".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }
    }

    // ---- the safety net: no floor-tagged cell reaches a map

    [HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.Spawn), new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool) })]
    public static class GenSpawn_ShipLocalNet_Patch
    {
        [HarmonyPriority(Priority.First)]
        public static void Prefix(ref IntVec3 loc, Map map)
        {
            if (loc.y != 0)
            {
                ShipLocal.Fix(ref loc, map, "GenSpawn.Spawn");
            }
        }
    }

    [HarmonyPatch]
    public static class Grids_ShipLocalNet_Patch
    {
        private static readonly AccessTools.FieldRef<TerrainGrid, Map> terrainMap = AccessTools.FieldRefAccess<TerrainGrid, Map>("map");
        private static readonly AccessTools.FieldRef<RoofGrid, Map> roofMap = AccessTools.FieldRefAccess<RoofGrid, Map>("map");

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain), new[] { typeof(IntVec3), typeof(TerrainDef) });
            yield return AccessTools.Method(typeof(TerrainGrid), nameof(TerrainGrid.SetFoundation), new[] { typeof(IntVec3), typeof(TerrainDef) });
            yield return AccessTools.Method(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrainColor), new[] { typeof(IntVec3), typeof(ColorDef) });
            yield return AccessTools.Method(typeof(RoofGrid), nameof(RoofGrid.SetRoof), new[] { typeof(IntVec3), typeof(RoofDef) });
        }

        [HarmonyPriority(Priority.First)]
        public static void Prefix(object __instance, ref IntVec3 c, MethodBase __originalMethod)
        {
            if (c.y == 0)
            {
                return;
            }
            Map map = __instance is TerrainGrid tg ? terrainMap(tg) : __instance is RoofGrid rg ? roofMap(rg) : null;
            ShipLocal.Fix(ref c, map, __originalMethod.DeclaringType.Name + "." + __originalMethod.Name);
        }
    }

    [HarmonyPatch(typeof(Zone), nameof(Zone.AddCell))]
    public static class ZoneAddCell_ShipLocalNet_Patch
    {
        [HarmonyPriority(Priority.First)]
        public static void Prefix(Zone __instance, ref IntVec3 c)
        {
            if (c.y != 0)
            {
                ShipLocal.Fix(ref c, __instance.Map, "Zone.AddCell");
            }
        }
    }

    [HarmonyPatch(typeof(Area), "set_Item", new[] { typeof(IntVec3), typeof(bool) })]
    public static class AreaSet_ShipLocalNet_Patch
    {
        [HarmonyPriority(Priority.First)]
        public static void Prefix(Area __instance, ref IntVec3 c)
        {
            if (c.y != 0)
            {
                ShipLocal.Fix(ref c, __instance.Map, "Area[]");
            }
        }
    }
}
