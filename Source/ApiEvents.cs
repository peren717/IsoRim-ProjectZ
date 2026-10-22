using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace ProjectZ
{
    // The plumbing behind ProjectZ.Api's events and step hooks (Api 2). Each is fired from the ONE place where that change
    // happens for every cause:
    //   floor built/removed: TerrainGrid.DoTerrainChangedEffects (TerrainChanged_FloorRoof_Patch, FloorRoof.cs), where a
    //     cell of an upper floor turns from open air to a floor or back (built, taken up, collapsed, a ship deck landed);
    //   pawn changed floor: Thing.Position's setter (below), where every move of a spawned pawn lands (path steps over
    //     stairs, ladders and elevators, jumps, falls, teleports);
    //   before a link step: Pawn_PathFollower.SetupMoveIntoNextCell (SetupMoveIntoNextCell_Patch, PathPatches.cs), where
    //     every stair, ladder and elevator step begins.
    // With nobody subscribed each costs one null or count check.
    public static class ApiEvents
    {
        public static event Action<Map, IntVec3, bool> FloorChanged;

        public static event Action<Pawn, int, int> PawnChangedFloor;

        private static readonly List<Func<Pawn, StepKind, Building, bool>> stepHooks = new List<Func<Pawn, StepKind, Building, bool>>();

        public static bool AnyPawnFloor => PawnChangedFloor != null;

        public static void AddStepHook(Func<Pawn, StepKind, Building, bool> hook)
        {
            if (hook != null && !stepHooks.Contains(hook))
            {
                stepHooks.Add(hook);
            }
        }

        public static void RemoveStepHook(Func<Pawn, StepKind, Building, bool> hook) => stepHooks.Remove(hook);

        // A floor laid on (built) or taken from cell c of an upper floor. Not while the map is being generated.
        public static void FireFloorChanged(Map map, IntVec3 c, bool built)
        {
            Action<Map, IntVec3, bool> ev = FloorChanged;
            if (ev == null || ReferenceEquals(MapGenerator.mapBeingGenerated, map))
            {
                return;
            }
            foreach (Delegate d in ev.GetInvocationList())
            {
                try
                {
                    ((Action<Map, IntVec3, bool>)d)(map, c, built);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce($"[ProjectZ] Api.FloorChanged subscriber {d.Method.DeclaringType}.{d.Method.Name} threw: {e}", d.Method.GetHashCode() ^ 0x5a2001);
                }
            }
        }

        public static void FirePawnChangedFloor(Pawn p, int from, int to)
        {
            Action<Pawn, int, int> ev = PawnChangedFloor;
            if (ev == null)
            {
                return;
            }
            foreach (Delegate d in ev.GetInvocationList())
            {
                try
                {
                    ((Action<Pawn, int, int>)d)(p, from, to);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce($"[ProjectZ] Api.PawnChangedFloor subscriber {d.Method.DeclaringType}.{d.Method.Name} threw: {e}", d.Method.GetHashCode() ^ 0x5a2002);
                }
            }
        }

        // The path follower is about to start a link step: every hook is asked (all of them, in the order added); false
        // from any refuses the step. A hook that throws counts as allowing it.
        public static bool AllowStep(Pawn p, StepKind kind, Building link)
        {
            if (stepHooks.Count == 0)
            {
                return true;
            }
            bool allow = true;
            for (int i = 0; i < stepHooks.Count; i++)
            {
                Func<Pawn, StepKind, Building, bool> h = stepHooks[i];
                try
                {
                    if (!h(p, kind, link))
                    {
                        allow = false;
                    }
                }
                catch (Exception e)
                {
                    Log.ErrorOnce($"[ProjectZ] Api step hook {h.Method.DeclaringType}.{h.Method.Name} threw: {e}", h.Method.GetHashCode() ^ 0x5a2003);
                }
            }
            return allow;
        }
    }

    // Api.PawnChangedFloor: a spawned pawn's position set to a cell on another floor of its map.
    [HarmonyPatch(typeof(Thing), nameof(Thing.Position), MethodType.Setter)]
    public static class Position_PawnChangedFloor_Patch
    {
        public static void Prefix(Thing __instance, out int __state)
        {
            __state = Levels.None;
            if (!ApiEvents.AnyPawnFloor || !(__instance is Pawn) || !__instance.Spawned)
            {
                return;
            }
            Map map = __instance.Map;
            if (ProjectZ.Annex(map) != null)
            {
                __state = Levels.LevelOf(__instance.Position, map);
            }
        }

        public static void Postfix(Thing __instance, int __state)
        {
            if (__state == Levels.None || !__instance.Spawned)
            {
                return;
            }
            int now = Levels.LevelOf(__instance.Position, __instance.Map);
            if (now != Levels.None && now != __state)
            {
                ApiEvents.FirePawnChangedFloor((Pawn)__instance, __state, now);
            }
        }
    }
}
