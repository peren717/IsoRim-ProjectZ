using UnityEngine;
using Verse;

namespace ProjectZ.Combat
{
    // THE weapon range rule between floors (design "Weapon range across floors", user decisions 2026-10-16).
    //
    // Range is the true 3D distance between the shooter's cell and the target's: over the ground, plus the height between
    // their floors (one storey = IsoRimBridge.StoreyHeight cells, as drawn); the same both ways. A shooter above its
    // target gets +1 cell of range per floor it stands above it (shooting down only; the RangeHeightBonus combat part).
    // The minimum range (mortars) is the plain 3D distance. On one floor this is the game's own flat rule, so only shots
    // between floors change.
    //
    // Every range decision between floors goes through here (inventory in the design note, pz-range/plan.md):
    //   Verb.OutOfRange (behind Verb.CanHitTarget/CanHitTargetFrom, TryFindShootLineFromTo, abilities, drafted orders),
    //   AttackTargetFinder.BestAttackTarget's range filter (AI, turrets, drafted auto-attack, fire at will),
    //   CastPositionFinder (AI shooting positions), Building_TurretGun.OrderAttack (forced targets, mortars), the drafted
    //   attack's refusal reason (FloatMenuUtility), the turret avoid grid, and the range preview (rings and shell).
    public static class Range3D
    {
        // The +1 cell per floor shooting down (the RangeHeightBonus combat part; 0 while it is off: plain 3D distance).
        public static float BonusPerFloor => CombatGate.On(CombatPart.RangeHeightBonus) ? 1f : 0f;

        // One storey in cells.
        public static float Storey => IsoRimBridge.StoreyHeight;

        // The range a shooter on floor ks has against a target on floor kt.
        public static float Reach(float range, int ks, int kt) => range + BonusPerFloor * Mathf.Max(0, ks - kt);

        // Height from floor kt up to floor ks, in cells.
        public static float Rise(int ks, int kt) => (ks - kt) * Storey;

        // Squared 3D distance from root to the nearest cell of rect (the target's cells, around targetCell, on its floor).
        public static float DistSquared(IntVec3 root, CellRect rect, IntVec3 targetCell, Map map, out int ks, out int kt)
        {
            ks = ProjectZ.FloorOf(root, map);
            kt = ProjectZ.FloorOf(targetCell, map);
            IntVec3 r = Heights.OnFloorOf(root, targetCell, map);
            float v = Rise(ks, kt);
            return rect.ClosestDistSquaredTo(r) + v * v;
        }

        // 3D distance between two cells (each on its floor).
        public static float Distance(IntVec3 a, IntVec3 b, Map map)
        {
            return Mathf.Sqrt(DistSquared(a, CellRect.SingleCell(b), b, map, out _, out _));
        }

        // Verb.OutOfRange between floors.
        public static bool OutOfRange(IntVec3 root, CellRect rect, IntVec3 targetCell, float range, float minRange, Map map)
        {
            float d2 = DistSquared(root, rect, targetCell, map, out int ks, out int kt);
            float reach = Reach(range, ks, kt);
            return d2 > reach * reach || d2 < minRange * minRange;
        }

        // What the game's flat range filters compare with a weapon range: the 3D distance less the height bonus.
        public static float RangeDistance(IntVec3 shooter, IntVec3 target, Map map)
        {
            float d2 = DistSquared(shooter, CellRect.SingleCell(target), target, map, out int ks, out int kt);
            return Mathf.Sqrt(d2) - BonusPerFloor * Mathf.Max(0, ks - kt);
        }

        // How far over the ground a shooter on floor ks reaches on floor k: the cross-section of its 3D range there.
        // Negative when floor k is out of reach.
        public static float HorizontalReach(float range, int ks, int k)
        {
            float reach = Reach(range, ks, k);
            float v = Rise(ks, k);
            float s = reach * reach - v * v;
            return s < 0f ? -1f : Mathf.Sqrt(s);
        }

        // The same rule over a continuous height, for the preview shell: distance from the shooter along a unit
        // direction whose height part is dy (negative = down), with the bonus growing per storey of drop.
        public static float ShellRadius(float range, float dy)
        {
            if (dy >= 0f)
            {
                return range;
            }
            float k = 1f + dy * BonusPerFloor / Storey;   // > 0 for every dy >= -1 while a storey is over one cell
            return k <= 0.05f ? range * 20f : range / k;
        }
    }
}
