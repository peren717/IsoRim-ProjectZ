using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace ProjectZ
{
    // ProjectZ's combat between floors in parts (Api 6). An addon that bridges another combat system keeps the parts that
    // still apply with it (Api.KeepCombatParts); an addon that does a part itself releases it (Api.ReleaseCombatParts).
    // Every patch of a part is listed in Docs/Api.md ("Combat parts"); the patch classes carry [CombatPartOf].
    [Flags]
    public enum CombatPart : long
    {
        None = 0,
        Range = 1 << 0,            // 3D range: Verb.OutOfRange, turret orders, the drafted refusal reason, the turret avoid grid
        RangeHeightBonus = 1 << 1, // +1 cell of range per floor shooting down (a rule inside Range; rings and shell follow)
        Sight = 1 << 2,            // Los3D for vanilla verbs: CanHitCellFromCellIgnoringRange, CanSee, lean sources, "no line of sight"
        TargetPicking = 1 << 3,    // BestAttackTarget across floors, CastPositionFinder (cast spots)
        HitReport = 1 << 4,        // vanilla hit report: ground distance, cover by height, smoke; wild misses
        HeightAccuracy = 1 << 5,   // the accuracy bonus shooting down (a rule inside HitReport; the heightAccuracy setting)
        Flights = 1 << 6,          // vanilla Projectile flights between floors, intercepts, drawn pitch, lobbed-shot rules
        Explosions = 1 << 7,       // explosion cells in 3D (DamageWorker.ExplosionCellsToHit), 3D falloff distances
        Shields = 1 << 8,          // vanilla shields (CompProjectileInterceptor) in 3D, bombardment shields by ground distance
        SkyStrikes = 1 << 9,       // orbital strikes, bombardments, power beams, lightning, forced misses, shells on column tops
        AimFloor = 1 << 10,        // directional effects (spews, sprays, beams) aimed at another floor
        Jumps = 1 << 11,           // jumps and flights between floors (jump verbs, AI jumps, flee and unstrand by flight)
        RangePreview = 1 << 12,    // the range rings on other floors and the range shell
        All = (1 << 13) - 1
    }

    // The part a patch class belongs to (FailSoft: applied, deferred, undone by its part).
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class CombatPartOfAttribute : Attribute
    {
        public readonly CombatPart part;

        public CombatPartOfAttribute(CombatPart part)
        {
            this.part = part;
        }
    }
}

namespace ProjectZ.Combat
{
    // THE switch of ProjectZ's combat parts (Api 6). One rule (On):
    //   - a released part is off;
    //   - the parts that never were tied to Combat Extended (ForeignDefault: explosions, shields, sky strikes) are on;
    //   - a key combat patch failed (FailSoft closed the combat group): the other parts are off;
    //   - no foreign combat system: on; with one: on only when an addon kept it (KeepCombatParts), read once startup has
    //     settled (every Mod constructor has run).
    // Patching: without a foreign system every part is patched in ProjectZ's constructor, as before Api 6. With one, the
    // patches of the parts it turns off (every part but ForeignDefault) are deferred to Settle (a StaticConstructorOnStartup,
    // after every Mod constructor), so a declaration counts whatever the load order. A part released after its patches
    // landed has them undone (FailSoft.UndoParts); a part kept after Settle is patched then.
    public static class CombatGate
    {
        public const CombatPart ForeignDefault = CombatPart.Explosions | CombatPart.Shields | CombatPart.SkyStrikes;

        private static bool? foreign;
        private static string foreignName;
        private static bool settled;
        private static bool keyFailed;
        private static CombatPart kept;
        private static CombatPart released;
        private static readonly List<string> declarations = new List<string>();
        private static readonly List<Action> onSettle = new List<Action>();

        // Another combat system is loaded that ProjectZ can't run under alone (today: Combat Extended).
        public static bool Foreign
        {
            get
            {
                if (foreign == null)
                {
                    ModMetaData ce = ModsConfig.ActiveModsInLoadOrder.FirstOrDefault(m => m.PackageIdPlayerFacing != null && m.PackageIdPlayerFacing.ToLowerInvariant().Contains("combatextended"));
                    foreign = ce != null;
                    foreignName = ce?.Name;
                }
                return foreign.Value;
            }
        }

        // Startup has settled the parts (always true without a foreign system).
        public static bool Settled => settled || !Foreign;

        // Every flag of 'part' is on.
        public static bool On(CombatPart part)
        {
            long bits = (long)(part & CombatPart.All);
            while (bits != 0)
            {
                long b = bits & -bits;
                bits &= bits - 1;
                if (!OnOne((CombatPart)b))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool OnOne(CombatPart b)
        {
            if ((released & b) != 0)
            {
                return false;
            }
            if ((ForeignDefault & b) != 0)
            {
                return true;
            }
            if (keyFailed)
            {
                return false;
            }
            return !Foreign || settled && (kept & b) != 0;
        }

        // The cross-floor shooting core is on (before Api 6 the one combat switch; kept for compatibility).
        public static bool Enabled => On(CombatPart.Sight);

        // A key combat patch failed (FailSoft): every part but ForeignDefault off, as with Combat Extended loaded.
        public static void Disable() => keyFailed = true;

        public static void Keep(CombatPart parts, string by)
        {
            parts &= CombatPart.All;
            kept |= parts;
            declarations.Add($"kept {parts} by {by ?? "?"}");
            if (settled && Foreign)
            {
                Log.Message($"[ProjectZ] {by} kept combat parts {parts} after startup: patched now");
                FailSoft.ApplyCombat();
            }
        }

        public static void Release(CombatPart parts, string by)
        {
            parts &= CombatPart.All;
            released |= parts;
            declarations.Add($"released {parts} by {by ?? "?"}");
            int n = FailSoft.UndoParts(parts);
            if (n > 0)
            {
                Log.Message($"[ProjectZ] {by} released combat parts {parts}: {n} patches undone");
            }
        }

        // Runs a (re-runnable) setup step once the parts are settled: now without a foreign system, else at Settle and
        // again after a late KeepCombatParts. The step checks On itself.
        public static void OnSettle(Action step)
        {
            if (Settled)
            {
                step();
                return;
            }
            onSettle.Add(step);
        }

        internal static void RunSettleSteps()
        {
            foreach (Action a in onSettle)
            {
                try
                {
                    a();
                }
                catch (Exception e)
                {
                    Log.Error("[ProjectZ] combat setup step failed: " + e);
                }
            }
        }

        // From CombatGateSettle (after every Mod constructor): the deferred combat patches of the parts now on.
        public static void Settle()
        {
            if (settled)
            {
                return;
            }
            bool f = Foreign;
            settled = true;
            if (!f)
            {
                return;
            }
            CombatPart on = CombatPart.None;
            for (long b = 1; b <= (long)CombatPart.All; b <<= 1)
            {
                if (OnOne((CombatPart)b))
                {
                    on |= (CombatPart)b;
                }
            }
            Log.Message($"[ProjectZ] {foreignName} is loaded: combat parts on: {on}" + (declarations.Count > 0 ? " (" + string.Join("; ", declarations) + ")" : " (no addon kept any: shooting between floors is left to it)"));
            FailSoft.ApplyCombat();
        }

        // Accuracy bonus for shooting down at a lower floor (decision D7, a setting; 0 with HeightAccuracy off).
        public static float HeightBonus => !On(CombatPart.HeightAccuracy) ? 0f : ProjectZMod.Settings == null ? 0.2f : ProjectZMod.Settings.heightAccuracy;

        // The hit report's shooter factor for a shot between floors over ground distance d (ShotReport_Patch), and the part
        // of it the height bonus gave (after the 100 % cap; 0 unless the shooter stands on a higher floor). Api 2's
        // HeightAccuracyBonus reads the same, so what it says is what the shot gets.
        public static float ShooterFactor(Thing caster, Verb verb, IntVec3 targetCell, float d, Map map, out float bonus)
        {
            bonus = 0f;
            if (!verb.verbProps.canGoWild)
            {
                return 1f;
            }
            float shooter = ShotReport.HitFactorFromShooter(caster, d);
            if (ProjectZ.FloorOf(caster.Position, map) > ProjectZ.FloorOf(targetCell, map))
            {
                float raised = UnityEngine.Mathf.Min(1f, shooter + HeightBonus);
                bonus = raised - shooter;
                shooter = raised;
            }
            return shooter;
        }
    }

    // The deferred combat patches, once every Mod constructor has run (a declaration from any addon's constructor counts).
    [StaticConstructorOnStartup]
    public static class CombatGateSettle
    {
        static CombatGateSettle()
        {
            CombatGate.Settle();
        }
    }
}
