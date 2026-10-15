using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ.Dev
{
    // Debug mode only: logs anything that spawns in the annex. Nothing may spawn there until floors can be built
    // (M1b), so every hit is a missed choke point. The test runner reads the counters.
    [HarmonyPatch(typeof(Thing), nameof(Thing.SpawnSetup))]
    public static class AnnexWatch
    {
        public static int Spawns;
        public static int AnnexSpawns;
        public static int AnnexMotes;
        public static readonly Dictionary<string, int> AnnexByDef = new Dictionary<string, int>();
        public static readonly Dictionary<string, int> SpawnsByCategory = new Dictionary<string, int>();

        public static void Postfix(Thing __instance, Map map, bool respawningAfterLoad)
        {
            if (!ProjectZ.DebugOn || respawningAfterLoad || map == null)
            {
                return;
            }
            Spawns++;
            string cat = __instance.def.category.ToString();
            SpawnsByCategory[cat] = SpawnsByCategory.TryGetValue(cat, out int n) ? n + 1 : 1;
            if (ProjectZ.IsAnnex(__instance.Position, map) && !OnBuiltFloor(__instance, map))
            {
                // Motes are visual only and placed at an exact position: one thrown over a pawn on the ground's top
                // row (damage text ...) can sit in the first void row. Counted, but not a missed choke point.
                if (__instance is Mote)
                {
                    AnnexMotes++;
                    ProjectZ.DebugLog($"annex mote (visual, allowed): {__instance} at {__instance.Position}");
                    return;
                }
                AnnexSpawns++;
                string key = __instance.def.defName;
                AnnexByDef[key] = AnnexByDef.TryGetValue(key, out int k) ? k + 1 : 1;
                Log.Warning($"[ProjectZ] annex spawn: {__instance} at {__instance.Position} (floor {ProjectZ.FloorOf(__instance.Position, map)})\n{System.Environment.StackTrace}");
            }
        }

        // Since M1b things may stand on built upper floors (and blueprints/frames for them go on open air cells that are
        // supported): only spawns elsewhere in the annex are missed choke points.
        private static bool OnBuiltFloor(Thing t, Map map)
        {
            IntVec3 c = t.Position;
            if (ProjectZ.FloorOf(c, map) < 1)
            {
                return false;
            }
            if (c.GetTerrain(map) != PZDefOf.PZ_OpenAir)
            {
                return true;
            }
            return (t is Blueprint || t is Frame) && PlaceWorker_UpperFloor.SupportedFloorCell(c, map);
        }

        public static void Reset()
        {
            Spawns = 0;
            AnnexSpawns = 0;
            AnnexMotes = 0;
            AnnexByDef.Clear();
            SpawnsByCategory.Clear();
        }
    }
}
