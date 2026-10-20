using Verse;

namespace ProjectZ
{
    // ProjectZ has the final say between floors. With this Harmony (RimWorld 1.7), a prefix that returns false skips the
    // original AND the prefixes after it (measured: VFEM2's warbow prefix on CanHitCellFromCellIgnoringRange left ours
    // uncalled), while every postfix still runs. So each hook where ProjectZ decides a result for two places on
    // different floors works in two steps:
    //   the prefix decides (once) and keeps its decision in __state;
    //   a postfix at Priority.Last writes it back over whatever other mods wrote. When the prefix was skipped
    //   (__state NotRun) the postfix detects the case and decides itself; when it ran, nothing is computed twice.
    // On one floor the decision is Pass and the postfix does nothing: other mods (and the game) decide there as before.
    // Hooks whose ProjectZ part is already a postfix run that postfix at Priority.Last instead (one step).
    // Inventory (see each patch): sight and shooting (Verb.CanHitCellFromCellIgnoringRange, GenSight.LineOfSight,
    // AttackTargetFinder.CanSee, Verb.OutOfRange, ShotReport.HitReportFor, ShootLeanUtility.LeanShootingSourcesFromTo,
    // AttackTargetFinder.BestAttackTarget, CastPositionFinder.TryFindCastPosition, JobGiver_AIFightEnemy.ShouldLoseTarget),
    // projectiles (Projectile.CheckForFreeIntercept, CompProjectileInterceptor.CheckIntercept,
    // DamageWorker.ExplosionCellsToHit), jumps (JumpUtility.CanHitTargetFrom, Verb.TryFindShootLineFromTo for jumps),
    // paths and reach (PathFinder.FindPathNow, Pawn_PathFollower.GenerateNewPathRequest, Reachability.GetCachedResult and
    // CheckRegionBasedReachability, the stairs touched from the floor above: TouchPathEndModeUtility and
    // RCellFinder.TryFindGoodAdjacentSpotToTouch, RegionListersUpdater.GetTouchableRegions), facilities
    // (CompAffectedByFacilities.IsPotentiallyValidFacilityForMe_Static) and the mechanitor's range (CanCommandTo).
    public static class FinalSay
    {
        public const byte NotRun = 0;   // the prefix did not run
        public const byte Pass = 1;     // one floor (or nothing to decide): not ours
        public const byte No = 2;       // decided: false
        public const byte Yes = 3;      // decided: true

        public static byte Of(bool v) => v ? Yes : No;

        public static bool Decided(byte s) => s >= No;

        // Counters for the tests: how often a postfix found another mod's value and put ProjectZ's back; how often a
        // postfix found its prefix skipped (another prefix returned false) and decided itself.
        public static int Reasserted;
        public static int PrefixSkipped;

        // The postfix of a bool hook: ProjectZ's decision back in the result.
        public static void Bool(byte s, ref bool result)
        {
            if (s < No)
            {
                return;
            }
            bool v = s == Yes;
            if (result != v)
            {
                result = v;
                Reasserted++;
            }
        }

        // The postfix of a hook with another result type: the kept value back in the result.
        public static void Value<T>(bool decided, T kept, ref T result)
        {
            if (decided && !Equals(result, kept))
            {
                result = kept;
                Reasserted++;
            }
        }
    }
}
