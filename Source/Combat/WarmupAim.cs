using HarmonyLib;
using UnityEngine;
using Verse;

namespace ProjectZ.Combat
{
    // A warm-up's aim indicators between floors (Stance_Warmup: the aim line, the charge mote and the target mote of the
    // verbs that have them: the hellsphere cannon, the fleshmelter bolt, the beam repeater). The game works the aim
    // direction out in map rows, where another floor is a whole band of rows away: it points (nearly) due north or south.
    // Drawing only: what the shot does is not touched. On one floor, on a map without floors and for void rows the game's
    // own values stay.
    // Not a combat part (always on, as TurretTop_Aim_Patch): the patches act only on a warm-up that already aims at
    // another floor, whichever combat system allowed that shot.
    internal static class WarmupAim
    {
        // The stance's caster aims at another floor (Heights.CrossFloor: THE test of two cells for it): the map rows from
        // the caster's floor to the target's. False without a caster on a map or without a target.
        private static bool CrossFloor(Stance_Warmup stance, out int rows)
        {
            rows = 0;
            Thing caster = stance.verb?.Caster;
            Map map = caster?.Map;
            if (map == null || !stance.focusTarg.IsValid)
            {
                return false;
            }

            IntVec3 from = caster.Position;
            IntVec3 to = stance.focusTarg.Cell;
            if (!Heights.CrossFloor(from, to, map))
            {
                return false;
            }

            rows = Levels.Shift(map, Levels.LevelOf(to, map)) - Levels.Shift(map, Levels.LevelOf(from, map));
            return true;
        }

        // Copied from vanilla Stance_Warmup.AimDir (watched, CopyWatches): the target's centre less the caster's drawn
        // position, flat and normalised, here with the rows between the two floors taken off z first. (The game's own
        // result is normalised already: the rows cannot be taken off it afterwards.)
        // When another mod skipped the game's AimDir (its own direction), the result is left alone: that mod's value stands.
        [HarmonyPatch(typeof(Stance_Warmup), "AimDir")]
        private static class Stance_Warmup_AimDir_Patch
        {
            private static void Postfix(Stance_Warmup __instance, ref Vector3 __result, bool __runOriginal)
            {
                if (!__runOriginal || !CrossFloor(__instance, out int rows))
                {
                    return;
                }

                Vector3 direction = __instance.focusTarg.CenterVector3 - __instance.verb.Caster.DrawPos;
                direction.y = 0f;
                direction.z -= rows;
                __result = direction.normalized;
            }
        }

        // The far end of the aim line. Between floors a fixed-length aim line (the hellsphere cannon, the fleshmelter bolt)
        // ends on the target instead of at its fixed length from the caster: what the player sees is a line to the target on
        // its floor, shorter or longer than on one floor. This restates the other branch of vanilla Stance_Warmup.TargetPos
        // (without a fixed length: the target's centre) and rests on its present shape (watched, CopyWatches).
        // When another mod skipped the game's TargetPos (its own end point), the result is left alone.
        [HarmonyPatch(typeof(Stance_Warmup), "TargetPos")]
        private static class Stance_Warmup_TargetPos_Patch
        {
            private static void Postfix(Stance_Warmup __instance, ref Vector3 __result, bool __runOriginal)
            {
                // The renderer resolves the floor from each mote link's map position.
                // A fixed-length offset from the caster loses the target floor.
                if (__runOriginal && __instance.verb?.verbProps.aimingLineMoteFixedLength.HasValue == true &&
                    CrossFloor(__instance, out _))
                {
                    __result = __instance.focusTarg.CenterVector3;
                }
            }
        }
    }
}
