using HarmonyLib;
using UnityEngine;
using Verse;

namespace ProjectZ.Combat
{
    internal static class WarmupAim
    {
        private static bool TryGetLevels(Stance_Warmup stance, out Map map, out int source, out int target)
        {
            Thing caster = stance.verb?.Caster;
            map = caster?.Map;
            source = target = Levels.None;
            if (map == null || !stance.focusTarg.IsValid || !ProjectZ.HasAnnex(map))
            {
                return false;
            }

            source = Levels.LevelOf(caster.Position, map);
            target = Levels.LevelOf(stance.focusTarg.Cell, map);
            return source != target && source != Levels.None && target != Levels.None;
        }

        [HarmonyPatch(typeof(Stance_Warmup), "AimDir")]
        private static class DirectionPatch
        {
            private static void Postfix(Stance_Warmup __instance, ref Vector3 __result)
            {
                if (!TryGetLevels(__instance, out Map map, out int source, out int target))
                {
                    return;
                }

                Vector3 direction = __instance.focusTarg.CenterVector3 - __instance.verb.Caster.DrawPos;
                direction.y = 0f;
                direction.z -= Levels.Shift(map, target) - Levels.Shift(map, source);
                __result = direction.normalized;
            }
        }

        [HarmonyPatch(typeof(Stance_Warmup), "TargetPos")]
        private static class EndpointPatch
        {
            private static void Postfix(Stance_Warmup __instance, ref Vector3 __result)
            {
                // The renderer resolves the floor from each mote link's map position.
                // A fixed-length offset from the caster loses the target floor.
                if (__instance.verb?.verbProps.aimingLineMoteFixedLength.HasValue == true &&
                    TryGetLevels(__instance, out _, out _, out _))
                {
                    __result = __instance.focusTarg.CenterVector3;
                }
            }
        }
    }
}
