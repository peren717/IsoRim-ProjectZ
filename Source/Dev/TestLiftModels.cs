using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode liftmodels (the elevator, ladder and lift models, LiftModels.cs; run with -projectzfloors=5 -projectzseed=7).
    // Screenshots in the 3D view, each scene built in one cleared area (router2's R2Clear), the camera on it, from several
    // views: every level shown (viewing the top), the ground viewed (upper levels hidden), a middle level viewed, the camera
    // turned 90 and 180 degrees, peeking (IsoRim Api 7), the 3D view off. The pieces drawn are counted per view against
    // what each scene should show.
    //   all    the elevator at (19, 13) turned east, its shaft open to the top (5 storeys); a raiders' ladder up to level 2,
    //          the player's emergency ladder from level 3, a mechanoid lift up to level 4, all on the west edge of
    //          platforms on levels 1-4 (x 20..28)
    //   short  an elevator standing on level 2 (3 storeys) and one on level 4 (1 storey); an emergency ladder of 1 floor
    //   cap    a ground elevator capped by a floor in its shaft on level 3 (serves 0-2; the top piece under the cap)
    //   ride   a colonist riding the elevator from the ground to level 4, half-way (the platform under it)
    public static partial class TestRunner
    {
        private sealed class LmView
        {
            public string name;
            public int floor;
            public float yaw;
            public bool peek, flat, above;
            public float size = 9f;
            public int cx = 20, cz = 13;
            public int expect = -1;
        }

        private sealed class LmCase
        {
            public string id;
            public Action<Map> build;
            public Func<Map, bool> ready;
            public List<LmView> views;
        }

        private static List<LmCase> lmCases;
        private static int lmIdx, lmView;
        private static float lmOldYaw;
        private static readonly List<Building_Elevator> lmElevators = new List<Building_Elevator>();
        private static readonly List<Building_SiegeLadder> lmLadders = new List<Building_SiegeLadder>();

        private static bool StepLiftModels(Map map)
        {
            Rand.PushState((int)(StableHash("liftmodels_" + stage + "_" + lmIdx) ^ (uint)(SeedArg ?? 717)));
            try
            {
                switch (stage)
                {
                    case "lm_setup":
                        LmSetup(map);
                        return true;
                    case "lm_case":
                        LmCaseBuild(map);
                        return true;
                    case "lm_view":
                        LmViewSet(map);
                        return true;
                    case "lm_take":
                        LmTake(map);
                        return true;
                    case "lm_next":
                        LmNext(map);
                        return true;
                }
                return false;
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static void LmSetup(Map map)
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
            if (Levels.Max(map) < 4 || !IsoRimBridge.Present || IsoRimBridge.ApiVersion < 8)
            {
                Result("liftmodels: a map with 5 levels and IsoRim Api >= 8", false, $"levels {Levels.Min(map)}..{Levels.Max(map)}, IsoRim {IsoRimBridge.Present} Api {IsoRimBridge.ApiVersion}");
                Finish();
                return;
            }
            r2Area = new CellRect(map.Center.x - 22, map.Center.z - 16, 44, 32);
            List<Pawn> cols = Colonists(map);
            r2P = cols.First();
            r2Q = cols.FirstOrDefault(p => p != r2P);
            R2Park(map);
            lmOldYaw = (float)IsoRimBridge.SetOption("freeYaw", 0f);
            LmCases();
            lmIdx = 0;
            stage = "lm_case";
        }

        private static List<LmView> LmViews(int all, int ground, int mid, bool peek = false, bool flat = false)
        {
            List<LmView> v = new List<LmView>
            {
                new LmView { name = "shown", floor = 4, expect = all },
                new LmView { name = "close", floor = 4, expect = all, size = 4.5f, cx = 19, cz = 14 },
                new LmView { name = "close_turn270", floor = 4, yaw = 270f, expect = all, size = 4.5f, cx = 19, cz = 14 },
                new LmView { name = "ground", floor = 0, expect = ground },
                new LmView { name = "level2", floor = 2, expect = mid },
                new LmView { name = "turn90", floor = 4, yaw = 90f, expect = all },
                new LmView { name = "turn180", floor = 4, yaw = 180f, expect = all },
            };
            if (peek)
            {
                v.Add(new LmView { name = "peek1", floor = 1, peek = true });
                v.Add(new LmView { name = "peek1_drawn", floor = 1, peek = true, above = true });
            }
            if (flat)
            {
                v.Add(new LmView { name = "3doff", floor = 4, flat = true, expect = 0 });
            }
            return v;
        }

        private static void LmCases()
        {
            lmCases = new List<LmCase>
            {
                new LmCase
                {
                    id = "all",
                    build = map =>
                    {
                        LmPlatforms(map, 4);
                        LmElevator(map, ElL(19, 13, 0, map));
                        LmLadder(map, 10, 2, false, false);
                        LmLadder(map, 18, 3, true, false);
                        LmLadder(map, 16, 4, false, true);
                    },
                    // elevator 4 + 1; raiders' ladder foot + 2 + top; emergency foot + 3 + top; lift base + 3 + top
                    views = LmViews(19, 5, 13, peek: true, flat: true),
                },
                new LmCase
                {
                    id = "short",
                    build = map =>
                    {
                        LmPlatforms(map, 4);
                        R2Floor(map, new CellRect(ElG(19, 13).x, ElG(19, 13).z, 1, 1), 2);
                        R2Floor(map, new CellRect(ElG(19, 9).x, ElG(19, 9).z, 1, 1), 4);
                        map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                        LmElevator(map, ElL(19, 13, 2, map));
                        LmElevator(map, ElL(19, 9, 4, map));
                        LmLadder(map, 17, 1, true, false);
                    },
                    // level 2 elevator: 2 + 1; level 4 elevator: top only; emergency foot + 1 + top
                    views = LmViews(3 + 1 + 3, 2, 3),
                },
                new LmCase
                {
                    id = "cap",
                    build = map =>
                    {
                        LmPlatforms(map, 4);
                        R2Floor(map, new CellRect(ElG(19, 13).x, ElG(19, 13).z, 1, 1), 3);
                        map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                        LmElevator(map, ElL(19, 13, 0, map));
                    },
                    ready = map => lmElevators.Count == 1 && lmElevators[0].LinkedTop == 2,
                    // shaft 1, 2 + the top under the cap on level 2
                    views = LmViews(3, 0, 3),
                },
                new LmCase
                {
                    id = "ride",
                    build = map =>
                    {
                        LmPlatforms(map, 4);
                        LmElevator(map, ElL(19, 13, 0, map));
                        ElNewBattery(map, true);
                        map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                        Teleport(r2P, ElG(22, 13));
                        Draft(r2P, true);
                    },
                    ready = map =>
                    {
                        Building_Elevator e = lmElevators.FirstOrDefault();
                        if (e == null || !e.LinkedPowered)
                        {
                            return false;
                        }
                        if (r2P.CurJobDef != JobDefOf.Goto)
                        {
                            GoTo(r2P, ElL(24, 13, 4, map));
                        }
                        return Links.Kind(r2P) == StepKind.ElevatorUp && r2P.pather.MovePercentage >= 0.5f;
                    },
                    // shaft 4 + top, the platform (ElevatorDraw.Drawn is checked apart)
                    views = new List<LmView>
                    {
                        new LmView { name = "shown", floor = 4, expect = 5 },
                        new LmView { name = "close", floor = 4, expect = 5, size = 6f, cx = 21, cz = 15 },
                        new LmView { name = "close_turn90", floor = 4, yaw = 90f, expect = 5, size = 6f, cx = 19, cz = 14 },
                        new LmView { name = "ground", floor = 0, expect = 0 },
                        new LmView { name = "turn90", floor = 4, yaw = 90f, expect = 5 },
                        new LmView { name = "turn180", floor = 4, yaw = 180f, expect = 5 },
                    },
                },
            };
        }

        // Platforms on levels 1..top east of the column x 19 (x 20..28, z 8..19), as the elevator's mirrored fixture.
        private static void LmPlatforms(Map map, int top)
        {
            R2Clear(map);
            R2Park(map, r2P);
            R2Feed(map);
            LinkSteps.For(map)?.steps.Clear();
            for (int L = 1; L <= top; L++)
            {
                R2Floor(map, new CellRect(ElG(20, 8).x, ElG(20, 8).z, 9, 12), L);
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
        }

        private static void LmElevator(Map map, IntVec3 c)
        {
            if (Place(ElevatorDefOf.PZ_Elevator, null, c, map, Rot4.East) is Building_Elevator e)
            {
                lmElevators.Add(e);
            }
        }

        // A ladder against the platform's west edge at row z, its top on level top: the player's emergency ladder (built on
        // the top cell, turned west to the open column) or the raiders' (LadderSite.Spawn at the foot; a lift for mechs).
        private static void LmLadder(Map map, int z, int top, bool emergency, bool lift)
        {
            IntVec3 topCell = ElL(20, z, top, map);
            Building_SiegeLadder b;
            if (emergency)
            {
                b = Place(PZDefOf.PZ_EmergencyLadder, null, topCell, map, Rot4.West) as Building_SiegeLadder;
            }
            else
            {
                Faction f = lift ? Faction.OfMechanoids : Find.FactionManager.RandomEnemyFaction(allowNonHumanlike: false);
                b = LadderSite.Spawn(ElG(19, z), topCell, f, map, lift);
            }
            if (b != null)
            {
                lmLadders.Add(b);
            }
            Info($"liftmodels: {(lift ? "lift" : emergency ? "emergency ladder" : "raiders' ladder")} at row {z} to level {top}: {b} at {b?.Position}, foot {b?.foot}, top {b?.top}, span {b?.span}");
        }

        private static void LmCaseBuild(Map map)
        {
            if (lmIdx >= lmCases.Count)
            {
                IsoRimBridge.SetOption("freeYaw", lmOldYaw);
                FloorView.Set(0);
                Finish();
                return;
            }
            LmCase c = lmCases[lmIdx];
            lmElevators.Clear();
            lmLadders.Clear();
            c.build(map);
            lmView = 0;
            Info($"liftmodels case {c.id}: elevators {string.Join(", ", lmElevators.Select(e => $"{e.Position} level {e.baseLevel} top {e.LinkedTop}"))}; ladders {lmLadders.Count}");
            SeedTicks("lm_" + c.id);
            TickUntil(c.ready != null ? 3000 : 60, () => c.ready != null && c.ready(map), "lm_view", 1);
        }

        private static void LmViewSet(Map map)
        {
            tickWatch = null;
            LmCase c = lmCases[lmIdx];
            LmView v = c.views[lmView];
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;   // (a rider stays mid-ride over the views)
            IsoRimBridge.SetOption("freeYaw", v.yaw);
            IsoRim.Api.PeekUpperFloors = v.peek;
            LiftModels.DrawAboveView = v.above;
            if (v.flat)
            {
                IsoRimBridge.SetView3DActive(false);
                map.mapDrawer.RegenerateEverythingNow();
            }
            ShotAt(map, v.floor, ElG(v.cx, v.cz), v.size);
            Wait(v.flat ? 30 : 120, "lm_take");   // (the camera turns over some frames)
        }

        private static void LmTake(Map map)
        {
            LmCase c = lmCases[lmIdx];
            LmView v = c.views[lmView];
            string name = $"lift_{c.id}_{v.name}";
            Capture(name);
            int pieces = Time.frameCount - LiftModels.LastFrame <= 2 ? LiftModels.Pieces : 0;
            string detail = $"pieces drawn {pieces} (expected {v.expect}), view level {FloorView.Current}, yaw {v.yaw}, peek {v.peek}, 3D {!v.flat}, platforms drawn so far {ElevatorDraw.Drawn}";
            if (v.expect >= 0)
            {
                Result($"LM {name}: the stacked pieces drawn", pieces == v.expect, detail);
            }
            else
            {
                Info($"LM {name}: {detail}");
            }
            Wait(3, "lm_next");
        }

        private static void LmNext(Map map)
        {
            LmCase c = lmCases[lmIdx];
            LmView v = c.views[lmView];
            IsoRim.Api.PeekUpperFloors = false;
            LiftModels.DrawAboveView = false;
            if (v.flat)
            {
                IsoRimBridge.SetView3DActive(true);
                map.mapDrawer.RegenerateEverythingNow();
            }
            if (c.id == "ride" && lmView == 0)
            {
                Result("LM ride: the platform is drawn under the rider", ElevatorDraw.Drawn > 0, $"platforms drawn {ElevatorDraw.Drawn}, rider {r2P} at {r2P.Position} -> {r2P.pather.nextCell} {r2P.pather.MovePercentage:P0}, rise {Links.Rise(r2P):0.00}");
            }
            lmView++;
            if (lmView >= c.views.Count)
            {
                lmIdx++;
                Wait(v.flat ? 30 : 3, "lm_case");
                return;
            }
            Wait(v.flat ? 30 : 3, "lm_view");
        }
    }
}
