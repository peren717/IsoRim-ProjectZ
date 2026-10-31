using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ.Combat
{
    // "No line of sight" (user decision 2026-10-18), for every shot: in the shot preview under the game's "Cannot hit
    // target." and as the order refusal's reason. It shows when the target is in range but the verb finds no shoot line:
    // on one floor that is the game's own sight result, between floors Los3D's.
    public static class NoSight
    {
        public static bool Blocked(Verb v, LocalTargetInfo t)
        {
            Thing caster = v?.caster;
            if (caster == null || !caster.Spawned || !t.IsValid || !v.verbProps.requireLineOfSight || v.verbProps.IsMeleeAttack || v.EffectiveRange <= 1.42f)
            {
                return false;
            }
            if (t.HasThing && (t.Thing == caster || t.Thing.Map != caster.Map) || v.ApparelPreventsShooting())
            {
                return false;
            }
            if (t.Pawn != null && t.Pawn.IsPsychologicallyInvisible() && caster.HostileTo(t.Pawn))
            {
                return false;
            }
            CellRect rect = t.HasThing ? t.Thing.OccupiedRect() : CellRect.SingleCell(t.Cell);
            if (v.OutOfRange(caster.Position, t, rect))
            {
                return false;
            }
            return !v.TryFindShootLineFromTo(caster.Position, t, out _);
        }
    }

    // The shot preview (TooltipUtility.ShotCalculationTipString): the line under "Cannot hit target.".
    [HarmonyPatch(typeof(TooltipUtility), nameof(TooltipUtility.ShotCalculationTipString))]
    [PatchGroup(FailSoft.Combat)]
    [CombatPartOf(CombatPart.Sight)]
    public static class ShotTip_NoSight_Patch
    {
        public static void Postfix(Thing target, ref string __result)
        {
            if (string.IsNullOrEmpty(__result) || target == null)
            {
                return;
            }
            // the verb the game reads (the selected pawn's ranged weapon, or the selected turret's)
            Thing sel = Find.Selector.SingleSelectedThing;
            Verb v = null;
            if (sel is Pawn p && p != target && p.equipment?.Primary != null && p.equipment.PrimaryEq.PrimaryVerb is Verb_LaunchProjectile && (!p.IsPlayerControlled || p.Drafted))
            {
                v = p.equipment.PrimaryEq.PrimaryVerb;
            }
            else if (sel is Building_TurretGun tur && tur != target)
            {
                v = tur.AttackVerb;
            }
            if (v == null)
            {
                return;
            }
            string cannot = "CannotHit".Translate();
            int i = __result.IndexOf(cannot);
            if (i < 0 || !NoSight.Blocked(v, target))
            {
                return;
            }
            int nl = __result.IndexOf('\n', i + cannot.Length);
            int at = nl < 0 ? __result.Length : nl + 1;
            __result = __result.Insert(at, (nl < 0 ? "\n" : "") + "PZ_NoLineOfSight".Translate() + "\n");
        }
    }
}
