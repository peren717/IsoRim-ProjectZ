using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Patches
{
    // Gameplay geometry between floors (design "M1c-2 coordinates", section 4): the game's distances are inlined
    // (a - b).LengthHorizontalSquared with no shared root to patch, and a global change would be wrong (work ordering
    // should prefer the pawn's own floor). So each decision that must see the virtual 3D position calls ProjectZ.Virtual.

    // Facing while aiming or shooting (the stance's focus): the target on another floor is faced where it really is.
    // Face/FaceCell are small enough to be inlined into UpdateRotation, so the busy branch is taken here.
    [HarmonyPatch(typeof(Pawn_RotationTracker), nameof(Pawn_RotationTracker.UpdateRotation))]
    public static class Pawn_UpdateRotation_Patch
    {
        public static bool Prefix(Pawn ___pawn)
        {
            Pawn pawn = ___pawn;
            if (pawn == null || !pawn.Spawned || pawn.Destroyed || pawn.kindDef.useFixedRotation || pawn.jobs == null || pawn.stances == null)
            {
                return true;
            }
            if (!(pawn.stances.curStance is Stance_Busy busy) || !busy.focusTarg.IsValid || pawn.jobs.HandlingFacing
                || pawn.stances.stunner.Stunned && pawn.stances.stunner.DisableRotation)
            {
                return true;
            }
            Map map = pawn.Map;
            IntVec3 at = busy.focusTarg.HasThing ? busy.focusTarg.Thing.PositionHeld : busy.focusTarg.Cell;
            if (!ProjectZ.HasAnnex(map) || !Combat.Heights.CrossFloor(pawn.Position, at, map))
            {
                return true;
            }
            Vector3 to = busy.focusTarg.HasThing ? busy.focusTarg.Thing.DrawPos : busy.focusTarg.Cell.ToVector3Shifted();
            Vector3 d = ProjectZ.Virtual(to, map) - ProjectZ.Virtual(pawn.DrawPos, map);
            if (d.x * d.x + d.z * d.z > 0.0001f)
            {
                pawn.Rotation = Pawn_RotationTracker.RotFromAngleBiased(d.AngleFlat());
            }
            return false;
        }
    }

    // A turret's gun turns toward its target where it really is (TurretTopTick and ForceFaceTarget aim from the raw cell).
    [HarmonyPatch]
    public static class TurretTop_Aim_Patch
    {
        public static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(TurretTop), nameof(TurretTop.TurretTopTick));
            yield return AccessTools.Method(typeof(TurretTop), nameof(TurretTop.ForceFaceTarget));
        }

        public static void Postfix(TurretTop __instance, Building_Turret ___parentTurret, System.Reflection.MethodBase __originalMethod, object[] __args)
        {
            Building_Turret t = ___parentTurret;
            if (t == null || !t.Spawned || !ProjectZ.HasAnnex(t.Map))
            {
                return;
            }
            LocalTargetInfo targ = __originalMethod.Name == nameof(TurretTop.ForceFaceTarget) ? (LocalTargetInfo)__args[0] : t.CurrentTarget;
            if (!targ.IsValid || !Combat.Heights.CrossFloor(t.Position, targ.Cell, t.Map))
            {
                return;
            }
            Vector3 d = ProjectZ.Virtual(targ.Cell.ToVector3Shifted(), t.Map) - ProjectZ.Virtual(t.DrawPos, t.Map);
            if (d.x * d.x + d.z * d.z > 0.0001f)
            {
                __instance.CurRotation = d.AngleFlat();
            }
        }
    }

    // An AI fighter keeps a target within its keep radius: between floors measured over the ground.
    [HarmonyPatch(typeof(JobGiver_AIFightEnemy), "ShouldLoseTarget")]
    public static class AIFightEnemy_ShouldLoseTarget_Patch
    {
        private static readonly AccessTools.FieldRef<JobGiver_AIFightEnemy, float> keepRadius = AccessTools.FieldRefAccess<JobGiver_AIFightEnemy, float>("targetKeepRadius");

        public static void Postfix(JobGiver_AIFightEnemy __instance, Pawn pawn, ref bool __result)
        {
            Thing target = pawn.mindState?.enemyTarget;
            if (!__result || target == null || target.Destroyed || !target.Spawned || !ProjectZ.HasAnnex(pawn.Map)
                || !Combat.Heights.CrossFloor(pawn.Position, target.Position, pawn.Map))
            {
                return;
            }
            float r = keepRadius(__instance);
            int lose = Traverse.Create(__instance).Property("TicksSinceEngageToLoseTarget").GetValue<int>();
            if (Find.TickManager.TicksGame - pawn.mindState.lastEngageTargetTick > lose || !pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly)
                || Combat.Heights.GroundDistance(pawn.Position, target.Position, pawn.Map) > r)
            {
                return;
            }
            __result = (target as IAttackTarget)?.ThreatDisabled(pawn) ?? false;
        }
    }

    // A shell falling from the sky (flyOverhead) on a cell under an upper floor lands on the top of that column: the
    // highest floor built there (the floor's roof below cannot drop, it is the floor itself).
    [HarmonyPatch(typeof(Projectile), "ImpactSomething")]
    public static class Projectile_ImpactOnTop_Patch
    {
        public static void Prefix(Projectile __instance)
        {
            if (!__instance.def.projectile.flyOverhead || !__instance.Spawned)
            {
                return;
            }
            Map map = __instance.Map;
            IntVec3 pos = __instance.Position;
            if (!ProjectZ.HasAnnex(map) || map.roofGrid.RoofAt(pos) != PZDefOf.PZ_FloorRoof)
            {
                return;
            }
            IntVec3 top = pos;
            IntVec3 g = ProjectZ.GroundCellOf(pos, map);
            for (int k = ProjectZ.FloorOf(pos, map) + 1; g.IsValid; k++)
            {
                IntVec3 up = ProjectZ.CellOnFloor(g, k, map);
                if (!ProjectZ.HasFloor(up, map))
                {
                    break;
                }
                top = up;
            }
            if (top == pos)
            {
                return;
            }
            __instance.Position = top;
            if (__instance.usedTarget.HasThing && __instance.usedTarget.Thing.Position != top)
            {
                __instance.usedTarget = new LocalTargetInfo(top);
            }
            ProjectZ.DebugLog($"{__instance} fell on {pos} under a floor: lands on {top}");
        }
    }
}
