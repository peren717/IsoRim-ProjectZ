using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // Range test diagnostics: what a raider was doing when it did (or didn't) fire.
    public static partial class TestRunner
    {
        internal static int? SeedArg => GenCommandLine.TryGetCommandLineArg("projectzseed", out string s) && int.TryParse(s, out int n) ? n : (int?)null;

        private static readonly Dictionary<Pawn, List<string>> gJobs = new Dictionary<Pawn, List<string>>();

        private static void GTrack(Pawn r)
        {
            if (!gJobs.TryGetValue(r, out List<string> l))
            {
                gJobs[r] = l = new List<string>();
            }
            string s = $"{r.CurJobDef?.defName ?? "none"}/{r.stances?.curStance?.GetType().Name}/{(r.mindState.enemyTarget == null ? "-" : "T")}";
            if (l.Count == 0 || l[l.Count - 1] != s)
            {
                l.Add(s);
            }
        }

        private static string GDiag(Pawn r)
        {
            if (r == null)
            {
                return "-";
            }
            if (!r.Spawned)
            {
                return $"{r} not spawned (dead {r.Dead})";
            }
            Verb v = r.TryGetAttackVerb(gA);
            Thing pick = AttackTargetFinder.BestShootTargetFromCurrentPosition(r, TargetScanFlags.NeedThreat | TargetScanFlags.NeedAutoTargetable)?.Thing;
            string traits = r.story?.traits == null ? "" : string.Join(",", r.story.traits.allTraits.Select(t => t.def.defName + t.Degree));
            string caps = $"sight {r.health.capacities.GetLevel(PawnCapacityDefOf.Sight):0.00} manip {r.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation):0.00} consc {r.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness):0.00}";
            string jobs = gJobs.TryGetValue(r, out List<string> l) ? string.Join(">", l.Take(20)) : "";
            return $"[{r} job {r.CurJobDef?.defName} toil {r.GetLord()?.CurLordToil?.GetType().Name} duty {r.mindState.duty?.def.defName} enemy {r.mindState.enemyTarget} downed {r.Downed} mental {r.MentalStateDef?.defName} traits {traits} {caps} shooting {r.skills?.GetSkill(SkillDefOf.Shooting)?.Level} verb {v?.GetType().Name} state {v?.state} canhit {(v != null && v.CanHitTarget(gA))} pick {pick} stance {r.stances?.curStance?.GetType().Name} A threatoff {((IAttackTarget)gA).ThreatDisabled(r)} A downed {gA.Downed} A at {gA.Position} jobs {jobs}]";
        }
    }
}
