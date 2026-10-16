using System.Collections.Generic;
using Verse;

namespace ProjectZ
{
    // Buildings that only make sense on the ground (user decision 2026-10-16): drills and scanners reach into the rock,
    // graves are dug, bridges and gravship substructure are foundations. The list is XML (Defs/Misc/PZ_GroundOnly.xml);
    // other mods add their own GroundOnlyDef or patch ours. Things that need gravship substructure follow by themselves
    // (no substructure upstairs). The check is in CanPlaceBlueprintAt_Messages_Patch, where every placement goes through.
    public class GroundOnlyDef : Def
    {
        public List<ThingDef> things = new List<ThingDef>();
        public List<TerrainDef> terrains = new List<TerrainDef>();
    }

    public static class GroundOnly
    {
        private static HashSet<BuildableDef> set;

        public static bool Is(BuildableDef def)
        {
            if (def == null)
            {
                return false;
            }
            if (set == null)
            {
                set = new HashSet<BuildableDef>();
                foreach (GroundOnlyDef g in DefDatabase<GroundOnlyDef>.AllDefs)
                {
                    foreach (ThingDef t in g.things)
                    {
                        if (t != null)
                        {
                            set.Add(t);
                        }
                    }
                    foreach (TerrainDef t in g.terrains)
                    {
                        if (t != null)
                        {
                            set.Add(t);
                        }
                    }
                }
            }
            return set.Contains(def);
        }

        // Any of the footprint's cells above the ground (a floor or the void between floors).
        public static bool Upstairs(BuildableDef def, IntVec3 center, Rot4 rot, Map map)
        {
            foreach (IntVec3 c in GenAdj.OccupiedRect(center, rot, def.Size))
            {
                if (ProjectZ.IsAnnex(c, map))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
