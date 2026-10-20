using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Dev tools for siege ladders (stage 1: no raid AI). In the debug actions under "ProjectZ".
    public static class LadderDebugActions
    {
        private static Faction RaiderFaction()
        {
            return Find.FactionManager.AllFactionsVisible.Where(f => f.HostileTo(Faction.OfPlayer) && f.def.humanlikeFaction && !f.defeated).FirstOrDefault()
                ?? Find.FactionManager.RandomEnemyFaction(allowNonHumanlike: false) ?? Faction.OfAncientsHostile;
        }

        private static void SpawnAt(IntVec3 top, Rot4 side, bool lift)
        {
            Map map = Find.CurrentMap;
            IntVec3 foot;
            int span;
            string why = null;
            if (!side.IsValid)
            {
                side = LadderSite.BestSide(top, map, out foot, out span);
                if (!side.IsValid)
                {
                    Messages.Message($"No ladder site at {top}", MessageTypeDefOf.RejectInput, false);
                    return;
                }
            }
            else if (!LadderSite.Find(top, side, map, out foot, out span, out why))
            {
                Messages.Message($"No ladder site at {top} toward {side}: {why}", MessageTypeDefOf.RejectInput, false);
                return;
            }
            Building_SiegeLadder b = LadderSite.Spawn(foot, top, lift ? Faction.OfMechanoids : RaiderFaction(), map, lift);
            Messages.Message(b == null ? "Spawn refused" : $"{b.LabelCap}: foot {foot}, top {top}, {span} floor(s)", MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("ProjectZ", "Spawn siege ladder...", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SpawnLadder()
        {
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            void Add(string label, Rot4 side, bool lift)
            {
                opts.Add(new FloatMenuOption(label, delegate
                {
                    DebugTools.curTool = new DebugTool($"siege {(lift ? "lift" : "ladder")} top ({label})", delegate
                    {
                        SpawnAt(UI.MouseCell(), side, lift);
                    });
                }));
            }
            Add("ladder, best side", Rot4.Invalid, false);
            Add("ladder, column north of the top", Rot4.North, false);
            Add("ladder, column east of the top", Rot4.East, false);
            Add("ladder, column south of the top", Rot4.South, false);
            Add("ladder, column west of the top", Rot4.West, false);
            Add("lift, best side", Rot4.Invalid, true);
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        [DebugAction("ProjectZ", "Order pawn to place ladder...", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void OrderPlace()
        {
            DebugTools.curTool = new DebugTool("pick the pawn", delegate
            {
                Pawn p = UI.MouseCell().GetFirstPawn(Find.CurrentMap);
                if (p == null)
                {
                    return;
                }
                DebugTools.curTool = new DebugTool($"pick the top for {p.LabelShort}", delegate
                {
                    IntVec3 top = UI.MouseCell();
                    Rot4 side = LadderSite.BestSide(top, p.Map, out IntVec3 foot, out _);
                    if (!side.IsValid)
                    {
                        Messages.Message($"No ladder site at {top}", MessageTypeDefOf.RejectInput, false);
                        return;
                    }
                    Job job = JobDriver_PlaceSiegeLadder.Make(foot, top);
                    job.playerForced = true;
                    p.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    DebugTools.curTool = null;
                });
            });
        }

        [DebugAction("ProjectZ", "Break ladder (under climber)", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void BreakLadder()
        {
            Map map = Find.CurrentMap;
            IntVec3 c = UI.MouseCell();
            Thing t = c.GetFirstThing<Building_SiegeLadder>(map);
            if (t == null)
            {
                Pawn p = c.GetFirstPawn(map);
                LinkSteps.Step st = p == null ? null : LinkSteps.For(map)?.Of(p);
                t = st?.link.thing;
            }
            if (t != null && !t.Destroyed)
            {
                t.Destroy(DestroyMode.KillFinalize);
            }
        }
    }
}
