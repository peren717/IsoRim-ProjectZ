using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // What the test Defs name (Dev/LadderTestMod: a throwaway mod, not part of ProjectZ's Defs).
    public class TestLadderBuilding : Building_SiegeLadder
    {
    }

    public class TestTopWorker : PlaceWorker_EmergencyLadder
    {
    }

    public class TestFootWorker : PlaceWorker_LadderFromFoot
    {
    }

    // Mode customladders (Api 7, other mods' ladders; Docs/Api.md). Needs the test mod Source/Dev/LadderTestMod copied to
    // Mods\_PZLadderTest (testmods.sh install ladders) and active after ProjectZ; run with -projectzfloors=5 -projectzseed=7. Every check builds its own
    // fixture in one cleared area under its own random state; building, uninstalling, installing and climbing are real
    // jobs with real ticks.
    //   D   the lookup: which Defs are ladders, how each is placed, their kind
    //   L   FindFromFoot picks the lowest level with a top
    //   R   the refusals: no ledge, column blocked, roof, another link, foot taken
    //   F   a foot-placed ladder built from its blueprint: the shaft held by blueprint and frame, the link, a climb, 3D
    //   T   a top-placed ladder (a subclassed worker), built from the foot, the same checks
    //   N   a buildable ladder with neither worker: no shaft, no link; its code sets the link: drawn
    //   E   the emergency ladder as before
    //   U   uninstalled and installed on another wall: new link, old shaft free, new shaft held by the install blueprint
    //   UT  a standing top-placed ladder reinstalled on another wall
    //   UR  a ladder reinstalled on its own foot, turned to another ledge
    //   V   screenshots for the review sheet (ghosts, blueprint, every kind of ladder)
    //   S   saved and loaded: links as saved (no new look at the site), a pending shaft held, a minified ladder installed
    public static partial class TestRunner
    {
        private static ThingDef cxDef;
        private static IntVec3 cxAt, cxGhostAt;
        private static Rot4 cxRot, cxGhostRot;
        private static ThingDef cxGhostDef;
        private static List<IntVec3> cxShaft = new List<IntVec3>(), cxOld = new List<IntVec3>();
        private static bool cxFrameSeen, cxFrameLost, cxSiteLost, cxOldHeld, cxUp;
        private static string cxTag, cxNext;
        private static Building_SiegeLadder cxB;
        private static Thing cxMini;
        private static int cxView;

        private static ThingDef CxNamed(string n) => DefDatabase<ThingDef>.GetNamedSilentFail(n);

        private static ThingDef CxFoot => CxNamed("PZT_FootLadder");

        private static ThingDef CxFootSub => CxNamed("PZT_FootLadderSub");

        private static ThingDef CxTop => CxNamed("PZT_TopLadder");

        private static ThingDef CxNone => CxNamed("PZT_PlainLadder");

        private static bool StepCustomLadders(Map map)
        {
            Rand.PushState(FixtureSeed("customladders_" + stage));
            try
            {
                return StepCustomLaddersInner(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static bool StepCustomLaddersInner(Map map)
        {
            switch (stage)
            {
                case "cx_setup":
                    CxSetup(map);
                    return true;
                case "cx_lowest":
                    CxLowest(map);
                    return true;
                case "cx_refuse":
                    CxRefuse(map);
                    return true;
                case "cx_f":
                    CxBuildStart(map, CxFoot, "F", LdG(19, 13), Rot4.East, "cx_t");
                    return true;
                case "cx_t":
                    CxBuildStart(map, CxTop, "T", LdL(20, 13, 2, map), Rot4.West, "cx_n");
                    return true;
                case "cx_b_built":
                    CxBuilt(map);
                    return true;
                case "cx_b_up":
                    CxClimbed(map);
                    return true;
                case "cx_b_shot":
                    CxPieces(map, cxTag, 4, "cx_b_next");
                    return true;
                case "cx_b_next":
                    stage = cxNext;
                    return true;
                case "cx_n":
                    CxPlain(map);
                    return true;
                case "cx_n_built":
                    CxPlainBuilt(map);
                    return true;
                case "cx_n_shot":
                    CxPieces(map, "N", 4, "cx_e");
                    return true;
                case "cx_e":
                    CxEmergency(map);
                    return true;
                case "cx_e_shot":
                    CxPieces(map, "E", 4, "cx_u");
                    return true;
                case "cx_u":
                    CxUninstall(map);
                    return true;
                case "cx_u_out":
                    CxUninstalled(map);
                    return true;
                case "cx_u_in":
                    CxInstalled(map);
                    return true;
                case "cx_u_up":
                    CxInstalledClimbed(map);
                    return true;
                case "cx_ut":
                    CxReinstallTop(map);
                    return true;
                case "cx_ut_in":
                    CxReinstallTopDone(map);
                    return true;
                case "cx_ur":
                    CxTurn(map);
                    return true;
                case "cx_ur_in":
                    CxTurnDone(map);
                    return true;
                case "cx_v":
                    CxScene(map);
                    return true;
                case "cx_v_view":
                    CxViewSet(map);
                    return true;
                case "cx_v_take":
                    CxTake(map);
                    return true;
                case "cx_v_next":
                    stage = "cx_v_view";
                    return true;
                case "cx_s":
                    CxSave(map);
                    return true;
                case "cx_s_loaded":
                    CxLoaded(map);
                    return true;
                case "cx_s_in":
                    CxLoadedInstalled(map);
                    return true;
            }
            return false;
        }

        // Per frame (TestRunner.Update): the placing ghost's own drawing, as the designator calls it.
        private static void CxGhostFrame()
        {
            if (cxGhostDef == null)
            {
                return;
            }
            foreach (PlaceWorker w in cxGhostDef.PlaceWorkers)
            {
                w.DrawGhost(cxGhostDef, cxGhostAt, cxGhostRot, Color.white);
            }
        }

        private static string CxLinkOf(Building_SiegeLadder b, Map map)
        {
            StairGrid g = StairGrid.For(map);
            int i = g == null || b == null ? -1 : g.IndexOf(b);
            return i < 0 ? "no link" : $"{g.links[i].kind} {g.links[i].bottom} -> {g.links[i].top} span {g.links[i].span}";
        }

        private static bool CxLinked(Building_SiegeLadder b, IntVec3 foot, IntVec3 top, int span, Map map)
        {
            StairGrid g = StairGrid.For(map);
            int i = g == null || b == null ? -1 : g.IndexOf(b);
            return i >= 0 && g.links[i].bottom == foot && g.links[i].top == top && g.links[i].span == span && g.links[i].kind == LinkKind.Ladder && b.foot == foot && b.top == top && b.span == span;
        }

        private static List<IntVec3> CxColumn(int x, int z, int footL, int topL, Map map)
        {
            List<IntVec3> c = new List<IntVec3>();
            for (int L = footL + 1; L <= topL; L++)
            {
                c.Add(LdL(x, z, L, map));
            }
            return c;
        }

        private static bool CxHeld(List<IntVec3> cells, Map map) => cells.All(c => LadderColumn.Reserved(c, map));

        private static bool CxFree(List<IntVec3> cells, Map map) => cells.All(c => !LadderColumn.Reserved(c, map));

        private static void CxWorkerReady(Map map)
        {
            Heal(r2P);
            Teleport(r2P, LdG(12, 13));
            CxWorkerOn();
        }

        // (work at any hour: a check must not depend on the time of day it happens to reach)
        private static void CxWorkerOn()
        {
            Draft(r2P, false);
            R2Work(r2P, WorkTypeDefOf.Construction, WorkTypeDefOf.Hauling);
            for (int h = 0; h < 24; h++)
            {
                r2P.timetable?.SetAssignment(h, TimeAssignmentDefOf.Work);
            }
        }

        private static void CxFixture(Map map, int n)
        {
            LdFixture(map, n, null, false, r2P);
            CxWorkerReady(map);
        }

        // A second platform on level 1 east of the first (ground x 32..38): its west edge is another wall.
        private static void CxOtherWall(Map map)
        {
            R2Floor(map, new CellRect(LdG(32, 8).x, LdG(32, 8).z, 7, 12), 1);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
        }

        private static void CxSetup(Map map)
        {
            DebugSettings.enableRandomMentalStates = false;
            NoRoofCollapse(map);
            Find.Storyteller.storytellerComps.Clear();
            Find.PlaySettings.autoRebuild = false;
            NoAmbushes(map);
            foreach (Pawn a in map.mapPawns.AllPawnsSpawned.Where(p => !p.IsColonist).ToList())
            {
                a.Destroy();
            }
            if (Levels.Max(map) < 4 || CxFoot == null || CxFootSub == null || CxTop == null || CxNone == null)
            {
                Result("customladders: a map with 5 levels and the test mod", false, $"levels {Levels.Min(map)}..{Levels.Max(map)}, test Defs {CxFoot != null} {CxFootSub != null} {CxTop != null} {CxNone != null}");
                Finish();
                return;
            }
            r2Area = new CellRect(map.Center.x - 22, map.Center.z - 16, 44, 32);
            List<Pawn> cols = Colonists(map);
            r2P = cols.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)) ?? cols.First();
            r2Q = cols.FirstOrDefault(p => p != r2P);
            foreach (Pawn p in cols)
            {
                p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
            }
            R2Park(map);
            string kinds = string.Join(", ", LadderDefs.All.Select(d => d.defName + " " + LadderDefs.PlacementOf(d)));
            Result("D every ladder class Def is found, placement by its PlaceWorker alone", LadderDefs.All.Count == 7
                && LadderDefs.PlacementOf(PZDefOf.PZ_EmergencyLadder) == LadderPlacement.Top && LadderDefs.PlacementOf(PZDefOf.PZ_SiegeLadder) == LadderPlacement.None
                && LadderDefs.PlacementOf(PZDefOf.PZ_SiegeLift) == LadderPlacement.None && LadderDefs.PlacementOf(CxFoot) == LadderPlacement.Foot
                && LadderDefs.PlacementOf(CxFootSub) == LadderPlacement.Foot && LadderDefs.PlacementOf(CxTop) == LadderPlacement.Top
                && LadderDefs.PlacementOf(CxNone) == LadderPlacement.None && LadderDefs.PlacementOf(ThingDefOf.Wall) == LadderPlacement.None, kinds);
            Result("D the sites: blueprint, frame, install blueprint of placed ladders only", LadderDefs.Sites.Contains(CxFoot.blueprintDef) && LadderDefs.Sites.Contains(CxFoot.frameDef) && LadderDefs.Sites.Contains(CxFoot.installBlueprintDef)
                && LadderDefs.Sites.Contains(PZDefOf.PZ_EmergencyLadder.blueprintDef) && !LadderDefs.Sites.Contains(CxNone.blueprintDef), string.Join(", ", LadderDefs.Sites.Select(d => d.defName)));
            Result("D Api 7 or later", Api.Version >= 7, $"{Api.Version}");
            Info($"customladders: area {r2Area}, worker {r2P}, levels {Levels.Min(map)}..{Levels.Max(map)}");
            stage = "cx_lowest";
        }

        private static void CxLowest(Map map)
        {
            LdFixture(map, 2, null, false, r2P);
            R2Floor(map, new CellRect(LdG(20, 8).x, LdG(20, 8).z, 10, 12), 4);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            bool ok = LadderSite.FindFromFoot(ldFoot, Rot4.East, map, out IntVec3 t, out int n, out string why);
            Result("L ledges on levels 2 and 4: the lowest one", ok && t == ldTop && n == 2, $"found {ok} top {t} span {n} ({why}), wanted {ldTop}");
            bool same = LadderSite.Find(t, Rot4.West, map, out IntVec3 f, out int n2, out _) && f == ldFoot && n2 == n;
            Result("L the same site found from the top", same, $"foot {f} span {n2}");
            bool off = LadderSite.FindFromFoot(ldFoot, Rot4.West, map, out _, out _, out string w2);
            Result("L no ledge on the other side", !off && w2 == "no ledge", w2);
            bool up = LadderSite.FindFromFoot(LdL(25, 13, 2, map), Rot4.East, map, out _, out _, out string w3);
            Result("L a cell inside the platform: refused", !up, w3);
            stage = "cx_refuse";
        }

        private static string CxWhy(Map map, Rot4 rot) => GenConstruct.CanPlaceBlueprintAt(CxFoot, ldFoot, rot, map).Reason;

        private static void CxRefuse(Map map)
        {
            LdFixture(map, 2, null, false, r2P);
            AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(CxFoot, ldFoot, Rot4.East, map);
            Result("R on the foot, turned to the ledge: accepted", ok.Accepted, ok.Reason);
            Result("R no ledge on that side", CxWhy(map, Rot4.West) == "PZ_LadderNoLedge".Translate(), CxWhy(map, Rot4.West));

            LdFixture(map, 2, null, false, r2P);
            GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_UpperFloor, Ld2Col(1, map), map, Rot4.North, Faction.OfPlayer, null);
            Result("R a blueprint in the column", CxWhy(map, Rot4.East) == "PZ_LadderUpBlocked".Translate(), CxWhy(map, Rot4.East));

            LdFixture(map, 2, null, false, r2P);
            R2Floor(map, CellRect.SingleCell(LdG(19, 13)), 1);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Result("R a floor over the foot, under the ledge", CxWhy(map, Rot4.East) == "PZ_LadderUpBlocked".Translate(), CxWhy(map, Rot4.East));

            LdFixture(map, 2, null, false, r2P);
            map.roofGrid.SetRoof(ldFoot, RoofDefOf.RoofConstructed);
            Result("R a roof over the foot", CxWhy(map, Rot4.East) == "PZ_LadderRoofInWay".Translate(), CxWhy(map, Rot4.East));
            map.roofGrid.SetRoof(ldFoot, null);

            LdFixture(map, 2, null, false, r2P);
            R2Floor(map, CellRect.SingleCell(LdG(19, 14)), 2);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Thing em = Place(PZDefOf.PZ_EmergencyLadder, null, LdL(19, 14, 2, map), map, Rot4.South);
            Result("R another link on the foot (an emergency ladder from the north)", CxWhy(map, Rot4.East) == "PZ_LadderLinkTaken".Translate(), $"{CxWhy(map, Rot4.East)}; {CxLinkOf(em as Building_SiegeLadder, map)}");

            LdFixture(map, 2, LdRaiders(), false, r2P);
            AcceptanceReport taken = new PlaceWorker_LadderFromFoot().AllowsPlacing(CxFoot, ldFoot, Rot4.East, map);
            Result("R a ladder standing on the foot", taken.Reason == "PZ_LadderLinkTaken".Translate(), taken.Reason);
            stage = "cx_f";
        }

        // ------------------------------------------------------------------------------------------------ F, T: built

        private static void CxBuildStart(Map map, ThingDef def, string tag, IntVec3 at, Rot4 rot, string next)
        {
            CxFixture(map, 2);
            cxDef = def;
            cxTag = tag;
            cxNext = next;
            cxAt = at;
            cxRot = rot;
            cxShaft = CxColumn(19, 13, 0, 2, map);
            AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(def, at, rot, map);
            AcceptanceReport bad = GenConstruct.CanPlaceBlueprintAt(def, at, rot.Opposite, map);
            Result($"{tag} the blueprint: right way accepted, the other refused", ok.Accepted && !bad.Accepted, $"{ok.Accepted} '{ok.Reason}', {bad.Accepted} '{bad.Reason}'");
            bool free = CxFree(cxShaft, map);
            Blueprint bp = GenConstruct.PlaceBlueprintForBuild(def, at, map, rot, Faction.OfPlayer, null);
            Result($"{tag} the blueprint holds the shaft and the roof under it", free && CxHeld(cxShaft, map) && LadderColumn.Plane(ldFoot, map) && LadderColumn.Owner(cxShaft[0], map, false) == bp && !LadderColumn.Reserved(LdL(19, 13, 3, map), map),
                $"free before {free}, held {CxHeld(cxShaft, map)}, plane {LadderColumn.Plane(ldFoot, map)}, owner {LadderColumn.Owner(cxShaft[0], map, false)}");
            AcceptanceReport wall = GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, cxShaft[1], Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
            Result($"{tag} nothing placed in the held shaft", wall.Reason == "PZ_LadderColumnOpen".Translate(), wall.Reason);
            cxFrameSeen = cxFrameLost = cxSiteLost = false;
            SeedTicks("cx_build" + tag);
            TickUntil(4000, () =>
            {
                List<Thing> here = at.GetThingList(map);
                if (here.Any(x => x is Building_SiegeLadder))
                {
                    return true;
                }
                Thing site = here.FirstOrDefault(x => x is Blueprint || x is Frame);
                if (site is Frame)
                {
                    cxFrameSeen = true;
                    cxFrameLost |= !CxHeld(cxShaft, map) || LadderColumn.Owner(cxShaft[0], map, false) != site;
                }
                cxSiteLost |= site == null || !CxHeld(cxShaft, map);
                return false;
            }, "cx_b_built", 1);
        }

        private static void CxBuilt(Map map)
        {
            tickWatch = null;
            Building_SiegeLadder b = cxAt.GetFirstThing<Building_SiegeLadder>(map);
            cxB = b;
            Result($"{cxTag} a colonist builds it; the frame held the shaft all along", b != null && b.def == cxDef && b.Faction == Faction.OfPlayer && cxFrameSeen && !cxFrameLost && !cxSiteLost,
                $"{b} faction {b?.Faction}, frame seen {cxFrameSeen}, frame lost it {cxFrameLost}, site lost it {cxSiteLost}, worker at {r2P.Position} job {r2P.CurJobDef?.defName}");
            Result($"{cxTag} its link: foot to top, 2 floors, kind ladder, shaft held by the ladder", CxLinked(b, ldFoot, ldTop, 2, map) && b?.Kind == LinkKind.Ladder && LadderColumn.Owner(cxShaft[0], map, false) == b, CxLinkOf(b, map));
            if (b == null)
            {
                stage = cxNext;
                return;
            }
            ldDest = LdL(25, 13, 2, map);
            cxUp = false;
            GoTo(r2P, ldDest);
            SeedTicks("cx_up" + cxTag);
            TickUntil(3000, () =>
            {
                cxUp |= Links.Kind(r2P) == StepKind.LadderUp;
                return r2P.Position == ldDest;
            }, "cx_b_up", 1);
        }

        private static void CxClimbed(Map map)
        {
            tickWatch = null;
            Result($"{cxTag} a colonist climbs it", r2P.Position == ldDest && cxUp, $"at {r2P.Position}, on the ladder {cxUp}");
            CxShot(map, ldFoot, "cx_b_shot");
        }

        private static void CxShot(Map map, IntVec3 g, string next)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            ShotAt(map, 4, g, 6f);
            Wait(120, next);
        }

        private static void CxPieces(Map map, string tag, int want, string next)
        {
            int pieces = Time.frameCount - LiftModels.LastFrame <= 2 ? LiftModels.Pieces : 0;
            Capture("cx_" + tag);
            Result($"{tag} drawn in 3D with the ladder's pieces, its sprite not printed", pieces == want && (cxB == null || LiftModels.Replaces(cxB)), $"pieces {pieces}, wanted {want}, replaces {cxB != null && LiftModels.Replaces(cxB)}");
            FloorView.Set(0);
            Wait(3, next);
        }

        // ------------------------------------------------------------------------------------------------ N, E

        private static void CxPlain(Map map)
        {
            CxFixture(map, 2);
            cxShaft = CxColumn(19, 13, 0, 2, map);
            AcceptanceReport can = GenConstruct.CanPlaceBlueprintAt(CxNone, ldFoot, Rot4.East, map);
            Blueprint bp = can.Accepted ? GenConstruct.PlaceBlueprintForBuild(CxNone, ldFoot, map, Rot4.East, Faction.OfPlayer, null) : null;
            Result("N no worker: its blueprint holds no shaft", bp != null && CxFree(cxShaft, map) && !EmergencyLadder.PendingSite(bp, out _, out _) && !EmergencyLadder.IsPending(bp), $"accepted {can.Accepted}, free {CxFree(cxShaft, map)}");
            SeedTicks("cx_plain");
            TickUntil(4000, () => ldFoot.GetFirstThing<Building_SiegeLadder>(map) != null, "cx_n_built", 1);
        }

        private static void CxPlainBuilt(Map map)
        {
            tickWatch = null;
            Building_SiegeLadder b = ldFoot.GetFirstThing<Building_SiegeLadder>(map);
            Result("N built with no worker: no link worked out, no shaft", b != null && !b.top.IsValid && b.span == 0 && StairGrid.For(map)?.IndexOf(b) is null or < 0 && CxFree(cxShaft, map) && !LiftModels.Replaces(b), $"{b} top {b?.top} span {b?.span}, {CxLinkOf(b, map)}");
            b?.Destroy();
            Building_SiegeLadder c = (Building_SiegeLadder)ThingMaker.MakeThing(CxNone);
            c.top = ldTop;
            c.span = 2;
            c.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(c, ldFoot, map, Rot4.East);
            cxB = c;
            Result("N its own code sets the link, as a raid ladder's", CxLinked(c, ldFoot, ldTop, 2, map), CxLinkOf(c, map));
            CxShot(map, ldFoot, "cx_n_shot");
        }

        private static void CxEmergency(Map map)
        {
            CxFixture(map, 2);
            Building_SiegeLadder b = Place(PZDefOf.PZ_EmergencyLadder, null, ldTop, map, Rot4.West) as Building_SiegeLadder;
            cxB = b;
            Result("E the emergency ladder: on the top cell, its link as before", b != null && b.Emergency && b.Placement == LadderPlacement.Top && b.Position == ldTop && CxLinked(b, ldFoot, ldTop, 2, map), CxLinkOf(b, map));
            CxShot(map, ldFoot, "cx_e_shot");
        }

        // ------------------------------------------------------------------------------------------------ U: moved

        private static void CxUninstall(Map map)
        {
            CxFixture(map, 2);
            CxOtherWall(map);
            cxDef = CxFoot;
            cxB = Place(cxDef, null, ldFoot, map, Rot4.East) as Building_SiegeLadder;
            cxOld = CxColumn(19, 13, 0, 2, map);
            Result("U the ladder stands", CxLinked(cxB, ldFoot, ldTop, 2, map) && CxHeld(cxOld, map), CxLinkOf(cxB, map));
            map.designationManager.AddDesignation(new Designation(cxB, DesignationDefOf.Uninstall));
            SeedTicks("cx_uninstall");
            TickUntil(3000, () => !cxB.Spawned, "cx_u_out", 1);
        }

        private static void CxUninstalled(Map map)
        {
            tickWatch = null;
            MinifiedThing mini = map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing).OfType<MinifiedThing>().FirstOrDefault(m => m.InnerThing == cxB);
            Result("U a colonist uninstalls it: minified, link gone, old shaft free, column's roof cleared", !cxB.Spawned && mini != null && StairGrid.For(map)?.IndexOf(cxB) is null or < 0 && CxFree(cxOld, map) && map.roofGrid.RoofAt(ldFoot) != PZDefOf.PZ_StairwellRoof,
                $"spawned {cxB.Spawned}, minified {mini}, free {CxFree(cxOld, map)}, roof {map.roofGrid.RoofAt(ldFoot)?.defName}");
            if (mini == null)
            {
                stage = "cx_ut";
                return;
            }
            cxAt = LdG(31, 13);
            cxShaft = CxColumn(31, 13, 0, 1, map);
            AcceptanceReport can = GenConstruct.CanPlaceBlueprintAt(cxDef, cxAt, Rot4.East, map, false, mini, mini);
            Blueprint_Install bp = can.Accepted ? GenConstruct.PlaceBlueprintForInstall(mini, cxAt, map, Rot4.East, Faction.OfPlayer) : null;
            Result("U the install blueprint on the other wall holds the new shaft, not the old", bp != null && CxHeld(cxShaft, map) && LadderColumn.Owner(cxShaft[0], map, false) == bp && CxFree(cxOld, map), $"accepted {can.Accepted} '{can.Reason}', held {CxHeld(cxShaft, map)}");
            cxSiteLost = false;
            SeedTicks("cx_install");
            TickUntil(3000, () =>
            {
                if (cxAt.GetFirstThing<Building_SiegeLadder>(map) != null)
                {
                    return true;
                }
                cxSiteLost |= !CxHeld(cxShaft, map);
                return false;
            }, "cx_u_in", 1);
        }

        private static void CxInstalled(Map map)
        {
            tickWatch = null;
            IntVec3 top = LdL(32, 13, 1, map);
            Result("U installed: the link worked out afresh (not the old top or foot)", cxB.Spawned && cxB.Position == cxAt && CxLinked(cxB, cxAt, top, 1, map) && !cxSiteLost, $"at {cxB.Position}, {CxLinkOf(cxB, map)}, top {cxB.top}, foot {cxB.foot}, site lost {cxSiteLost}");
            Result("U the old shaft stays free, the new one is the ladder's", CxFree(cxOld, map) && LadderColumn.Owner(cxShaft[0], map, false) == cxB, $"owner {LadderColumn.Owner(cxShaft[0], map, false)}");
            ldDest = LdL(35, 13, 1, map);
            cxUp = false;
            GoTo(r2P, ldDest);
            SeedTicks("cx_u_up");
            TickUntil(3000, () =>
            {
                cxUp |= Links.Kind(r2P) == StepKind.LadderUp;
                return r2P.Position == ldDest;
            }, "cx_u_up", 1);
        }

        private static void CxInstalledClimbed(Map map)
        {
            tickWatch = null;
            Result("U a colonist climbs it where it stands now", r2P.Position == ldDest && cxUp, $"at {r2P.Position}, on the ladder {cxUp}");
            stage = "cx_ut";
        }

        private static void CxReinstallTop(Map map)
        {
            CxFixture(map, 2);
            CxOtherWall(map);
            cxDef = CxTop;
            cxB = Place(cxDef, null, ldTop, map, Rot4.West) as Building_SiegeLadder;
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            cxOld = CxColumn(19, 13, 0, 2, map);
            cxShaft = CxColumn(31, 13, 0, 1, map);
            cxAt = LdL(32, 13, 1, map);
            Result("UT the top-placed ladder stands", CxLinked(cxB, ldFoot, ldTop, 2, map) && cxB.Position == ldTop, CxLinkOf(cxB, map));
            AcceptanceReport can = GenConstruct.CanPlaceBlueprintAt(cxDef, cxAt, Rot4.West, map, false, cxB, cxB);
            Blueprint_Install bp = can.Accepted ? GenConstruct.PlaceBlueprintForReinstall(cxB, cxAt, map, Rot4.West, Faction.OfPlayer) : null;
            Result("UT while it stands, its install blueprint holds the new shaft, it the old", bp != null && CxHeld(cxShaft, map) && CxHeld(cxOld, map) && EmergencyLadder.IsPending(bp), $"accepted {can.Accepted} '{can.Reason}', new held {CxHeld(cxShaft, map)}, old held {CxHeld(cxOld, map)}");
            cxSiteLost = cxOldHeld = false;
            SeedTicks("cx_reinstall");
            TickUntil(4000, () =>
            {
                if (cxB.Spawned && cxB.Position == cxAt)
                {
                    return true;
                }
                cxSiteLost |= !CxHeld(cxShaft, map);
                cxOldHeld |= !cxB.Spawned && CxHeld(cxOld, map);
                return false;
            }, "cx_ut_in", 1);
        }

        private static void CxReinstallTopDone(Map map)
        {
            tickWatch = null;
            IntVec3 foot = LdG(31, 13);
            Result("UT reinstalled: new link, old shaft free, new held all along", cxB.Spawned && cxB.Position == cxAt && CxLinked(cxB, foot, cxAt, 1, map) && CxFree(cxOld, map) && !cxSiteLost && !cxOldHeld,
                $"{cxB} at {cxB.PositionHeld} spawned {cxB.Spawned}, {CxLinkOf(cxB, map)}, old free {CxFree(cxOld, map)}, site lost {cxSiteLost}, old held after it left {cxOldHeld}, worker at {r2P.Position} job {r2P.CurJobDef?.defName}");
            stage = "cx_ur";
        }

        private static void CxTurn(Map map)
        {
            CxFixture(map, 2);
            R2Floor(map, new CellRect(LdG(10, 8).x, LdG(10, 8).z, 9, 12), 1);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            cxDef = CxFootSub;
            cxB = Place(cxDef, null, ldFoot, map, Rot4.East) as Building_SiegeLadder;
            cxAt = ldFoot;
            cxOld = new List<IntVec3> { LdL(19, 13, 2, map) };
            cxShaft = CxColumn(19, 13, 0, 1, map);
            Teleport(r2P, LdG(19, 7));
            PlaceWorker_LadderFromFoot w = new TestFootWorker();
            AcceptanceReport own = w.AllowsPlacing(cxDef, ldFoot, Rot4.West, map, cxB, cxB);
            AcceptanceReport other = w.AllowsPlacing(cxDef, ldFoot, Rot4.West, map);
            Result("UR the ladder's own link does not stop it turning on its foot; anyone else's would", own.Accepted && other.Reason == "PZ_LadderLinkTaken".Translate(), $"own {own.Accepted} '{own.Reason}', other '{other.Reason}'");
            GenConstruct.PlaceBlueprintForReinstall(cxB, ldFoot, map, Rot4.West, Faction.OfPlayer);
            SeedTicks("cx_turn");
            TickUntil(3000, () => cxB.Spawned && cxB.Rotation == Rot4.West, "cx_ur_in", 1);
        }

        private static void CxTurnDone(Map map)
        {
            tickWatch = null;
            IntVec3 top = LdL(18, 13, 1, map);
            Result("UR reinstalled on its foot, turned west: the new ledge, one floor", CxLinked(cxB, ldFoot, top, 1, map) && CxFree(cxOld, map) && CxHeld(cxShaft, map), $"{CxLinkOf(cxB, map)}, turned {cxB.Rotation}");
            stage = "cx_v";
        }

        // ------------------------------------------------------------------------------------------------ V: the sheet

        private sealed class CxView2
        {
            public string name;
            public int floor;
            public float yaw;
            public float size = 7f;
            public bool ghost;
        }

        private static readonly CxView2[] cxViews =
        {
            new CxView2 { name = "shown", floor = 4 },
            new CxView2 { name = "close", floor = 4, size = 4f },
            new CxView2 { name = "turn90", floor = 4, yaw = 90f },
            new CxView2 { name = "turn180", floor = 4, yaw = 180f },
            new CxView2 { name = "ground", floor = 0 },
            new CxView2 { name = "level1", floor = 1 },
            new CxView2 { name = "ghost_ground", floor = 0, ghost = true },
            new CxView2 { name = "ghost_top", floor = 2, ghost = true },
        };


        // Rows on the platform's west edge, level 2: 9 emergency, 11 foot-placed, 13 top-placed, 15 no worker (link by code),
        // 17 a foot-placed blueprint; the ghost at row 19.
        private static void CxScene(Map map)
        {
            CxFixture(map, 2);
            Draft(r2P, true);
            Teleport(r2P, LdG(14, 13));
            Place(PZDefOf.PZ_EmergencyLadder, null, LdL(20, 9, 2, map), map, Rot4.West);
            Place(CxFoot, null, LdG(19, 11), map, Rot4.East);
            Place(CxTop, null, LdL(20, 13, 2, map), map, Rot4.West);
            Building_SiegeLadder c = (Building_SiegeLadder)ThingMaker.MakeThing(CxNone);
            c.top = LdL(20, 15, 2, map);
            c.span = 2;
            c.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(c, LdG(19, 15), map, Rot4.East);
            GenConstruct.PlaceBlueprintForBuild(CxFoot, LdG(19, 17), map, Rot4.East, Faction.OfPlayer, null);
            cxGhostDef = null;
            cxGhostAt = LdG(19, 19);
            cxGhostRot = Rot4.East;
            SetIsoRimOption("freeYaw", 0f);
            cxView = 0;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            stage = "cx_v_view";
        }

        private static void CxViewSet(Map map)
        {
            if (cxView >= cxViews.Length)
            {
                cxGhostDef = null;
                RestoreIsoRimOption("freeYaw");
                FloorView.Set(0);
                stage = "cx_s";
                return;
            }
            CxView2 v = cxViews[cxView];
            SetIsoRimOption("freeYaw", v.yaw);
            cxGhostDef = v.ghost ? CxFoot : null;
            ShotAt(map, v.floor, LdG(v.ghost ? 19 : 20, v.ghost ? 18 : 13), v.size);
            Wait(120, "cx_v_take");
        }

        private static void CxTake(Map map)
        {
            CxView2 v = cxViews[cxView];
            int pieces = Time.frameCount - LiftModels.LastFrame <= 2 ? LiftModels.Pieces : 0;
            Capture("cx_sheet_" + v.name);
            Info($"V {v.name}: pieces {pieces}, view {FloorView.Current}, yaw {v.yaw}");
            if (v.name == "shown")
            {
                Result("V four ladders of four kinds drawn, 4 pieces each", pieces == 16, $"pieces {pieces}");
            }
            cxView++;
            Wait(3, "cx_v_next");
        }

        // ------------------------------------------------------------------------------------------------ S: save, load

        private static IntVec3 cxPendFoot;
        private static List<IntVec3> cxPend = new List<IntVec3>();
        private static string cxPawnId, cxPawnId2;
        private static IntVec3 cxEmTop;

        private static void CxSave(Map map)
        {
            CxFixture(map, 2);
            CxOtherWall(map);
            Draft(r2P, true);
            cxB = Place(CxFoot, null, ldFoot, map, Rot4.East) as Building_SiegeLadder;
            cxEmTop = LdL(20, 16, 2, map);
            Building_SiegeLadder em = Place(PZDefOf.PZ_EmergencyLadder, null, cxEmTop, map, Rot4.West) as Building_SiegeLadder;
            cxPendFoot = LdG(19, 10);
            cxPend = CxColumn(19, 10, 0, 2, map);
            GenConstruct.PlaceBlueprintForBuild(CxFoot, cxPendFoot, map, Rot4.East, Faction.OfPlayer, null);
            // a minified ladder that still remembers where it stood, and a ledge under the standing ladder's top: a fresh
            // look at the site would pick it
            Building_SiegeLadder gone = Place(CxFoot, null, LdG(19, 18), map, Rot4.East) as Building_SiegeLadder;
            cxMini = gone.Uninstall();
            R2Floor(map, CellRect.SingleCell(LdG(20, 13)), 1);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            bool wouldMove = LadderSite.FindFromFoot(ldFoot, Rot4.East, map, out IntVec3 t, out _, out _, false, cxB) && t != ldTop;
            Result("S before saving: a ladder, an emergency ladder, a blueprint, a minified ladder; the site now reads lower", CxLinked(cxB, ldFoot, ldTop, 2, map) && CxLinked(em, LdG(19, 16), cxEmTop, 2, map) && CxHeld(cxPend, map) && cxMini != null && wouldMove, $"{CxLinkOf(cxB, map)}, {CxLinkOf(em, map)}, fresh look {t}");
            cxPawnId = r2P.ThingID;
            cxPawnId2 = r2Q?.ThingID;
            GameDataSaveLoader.SaveGame("pz_customladders");
            Result("S saved", File.Exists(GenFilePaths.FilePathForSavedGame("pz_customladders")), "");
            stage = "cx_s_loaded";
            pendingLoad = "pz_customladders";
        }

        private static void CxLoaded(Map map)
        {
            r2P = ById(map, cxPawnId);
            r2Q = ById(map, cxPawnId2);
            Building_SiegeLadder b = ldFoot.GetFirstThing<Building_SiegeLadder>(map);
            Building_SiegeLadder em = cxEmTop.GetFirstThing<Building_SiegeLadder>(map);
            Result("S loaded: the ladder keeps its saved link", b != null && CxLinked(b, ldFoot, ldTop, 2, map), CxLinkOf(b, map));
            Result("S loaded: the emergency ladder keeps its saved link", em != null && em.Emergency && CxLinked(em, LdG(19, 16), cxEmTop, 2, map), CxLinkOf(em, map));
            Result("S loaded: the blueprint holds its shaft", CxHeld(cxPend, map), $"owner {LadderColumn.Owner(cxPend[0], map, false)}");
            MinifiedThing mini = map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing).OfType<MinifiedThing>().FirstOrDefault(m => m.InnerThing?.def == CxFoot);
            Building_SiegeLadder inner = mini?.InnerThing as Building_SiegeLadder;
            Result("S loaded: the minified ladder, its old link only remembered", inner != null && StairGrid.For(map)?.IndexOf(inner) is null or < 0, $"{mini}, inner top {inner?.top} foot {inner?.foot}");
            if (inner == null || r2P == null)
            {
                Finish();
                return;
            }
            cxB = inner;
            cxAt = LdG(31, 13);
            cxShaft = CxColumn(31, 13, 0, 1, map);
            // (the blueprint at row 10 stays out of the worker's way)
            foreach (Thing t in map.listerThings.ThingsOfDef(CxFoot.blueprintDef).ToList())
            {
                t.Destroy(DestroyMode.Cancel);
            }
            Teleport(r2P, LdG(12, 13));   // (undrafted it would first walk off on a wander job from before the save)
            CxWorkerOn();
            GenConstruct.PlaceBlueprintForInstall(mini, cxAt, map, Rot4.East, Faction.OfPlayer);
            SeedTicks("cx_s_in");
            TickUntil(3000, () => inner.Spawned, "cx_s_in", 1);
        }

        private static void CxLoadedInstalled(Map map)
        {
            tickWatch = null;
            Result("S a minified ladder from the save, installed: its link worked out here", cxB.Spawned && CxLinked(cxB, cxAt, LdL(32, 13, 1, map), 1, map), $"at {cxB.PositionHeld}, {CxLinkOf(cxB, map)}, worker at {r2P.Position} job {r2P.CurJobDef?.defName}");
            Finish();
        }
    }
}
