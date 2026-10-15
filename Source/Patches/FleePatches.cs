using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Patches
{
    // Where a person flees to (CellFinderLoose.GetFleeDestToolUser: fire, danger, panic) on maps with floors: the game's
    // own scoring, but with distances in the virtual 3D space (ProjectZ.Virtual, design "M1c-2 coordinates" §4: decision
    // points use virtual positions). The region search already reaches the floor below over jumps and stairs (J2); with
    // raw cell distances every cell on another floor lay "150 cells away" from the pawn and scored nothing, so nobody fled
    // to another floor.
    [HarmonyPatch(typeof(CellFinderLoose), nameof(CellFinderLoose.GetFleeDestToolUser))]
    public static class GetFleeDestToolUser_Patch
    {
        public static bool Prefix(Pawn pawn, List<Thing> threats, float distance, ref IntVec3 __result)
        {
            Map map = pawn?.Map;
            if (map == null || ProjectZ.Annex(map) == null || threats == null || threats.Count == 0)
            {
                return true;
            }
            IntVec3 bestPos = pawn.Position;
            float bestScore = -1f;
            Vector3 from = ProjectZ.Virtual(pawn.Position, map);
            TraverseParms tp = TraverseParms.For(pawn);
            RegionTraverser.BreadthFirstTraverse(pawn.GetRegion(), (Region a, Region reg) => reg.Allows(tp, isDestination: false), delegate(Region reg)
            {
                Danger danger = reg.DangerFor(pawn);
                foreach (IntVec3 cell in reg.Cells)
                {
                    if (!cell.Standable(map) || reg.IsDoorway)
                    {
                        continue;
                    }
                    if (cell.GetTerrain(map).dangerous)
                    {
                        return false;
                    }
                    Vector3 at = ProjectZ.Virtual(cell, map);
                    Thing thing = null;
                    float near = 0f;
                    for (int i = 0; i < threats.Count; i++)
                    {
                        float d = (at - ProjectZ.Virtual(threats[i].Position, map)).sqrMagnitude;
                        if (thing == null || d < near)
                        {
                            thing = threats[i];
                            near = d;
                        }
                    }
                    float dist = Mathf.Sqrt(near);
                    float score = Mathf.Pow(Mathf.Min(dist, distance), 1.2f);
                    score *= Mathf.InverseLerp(50f, 0f, (at - from).magnitude);
                    if (cell.GetRoom(map) != thing.GetRoom())
                    {
                        score *= 4.2f;
                    }
                    else if (dist < 8f)
                    {
                        score *= 0.05f;
                    }
                    if (!map.pawnDestinationReservationManager.CanReserve(cell, pawn))
                    {
                        score *= 0.5f;
                    }
                    if (danger == Danger.Deadly)
                    {
                        score *= 0.8f;
                    }
                    if (ModsConfig.AnomalyActive && (pawn.RaceProps.Humanlike || pawn.IsPlayerControlled) && map.gameConditionManager.MapBrightness < 0.1f && map.glowGrid.PsychGlowAt(cell) == PsychGlow.Dark)
                    {
                        score *= 0.1f;
                    }
                    if (score > bestScore)
                    {
                        bestPos = cell;
                        bestScore = score;
                    }
                }
                return false;
            }, 20);
            __result = bestPos;
            return false;
        }
    }
}
