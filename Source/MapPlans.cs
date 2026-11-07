using System;
using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace ProjectZ
{
    // What a floor planner gives for one map (Api 9, Docs/Api.md).
    public struct MapFloorPlan
    {
        // The ground's size; x or z of 0 or less keeps the size the map was asked with.
        public IntVec3 groundSize;
        // Storeys, the ground counted: 1 = no floors above the ground, whatever the game's floor count is.
        public int floors;
        // What generation put on the upper floors stays. Without it they are filled with open air after the genSteps.
        public bool keepUpperContent;
    }

    // THE per-map override of the game's floors (WorldFloors): read at one place, the GenerateMap prefix.
    internal static class MapPlans
    {
        private static readonly List<Func<MapParent, MapFloorPlan?>> planners = new List<Func<MapParent, MapFloorPlan?>>();

        internal static void Register(Func<MapParent, MapFloorPlan?> planner)
        {
            if (planner != null && !planners.Contains(planner))
            {
                planners.Add(planner);
            }
        }

        internal static void Unregister(Func<MapParent, MapFloorPlan?> planner) => planners.Remove(planner);

        // The plan for a map about to be generated, or null: no planner has one, and today's rules apply.
        internal static MapFloorPlan? For(MapParent parent)
        {
            if (parent == null)
            {
                return null;
            }
            for (int i = 0; i < planners.Count; i++)
            {
                MapFloorPlan? plan;
                try
                {
                    plan = planners[i](parent);
                }
                catch (Exception e)
                {
                    Log.Error($"[ProjectZ] a map floor planner failed for {parent}, ignored: {e}");
                    continue;
                }
                if (plan == null)
                {
                    continue;
                }
                if (plan.Value.floors < 1 || plan.Value.floors > ProjectZSettings.MaxFloors)
                {
                    Log.Error($"[ProjectZ] a map floor planner asked for {plan.Value.floors} floors for {parent} (1 to {ProjectZSettings.MaxFloors}), ignored.");
                    continue;
                }
                return plan;
            }
            return null;
        }
    }
}
