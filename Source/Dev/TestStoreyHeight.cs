using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // Mode storeyheight (the storey height setting, IsoRimBridge.ApplyWallHeight; run with -projectzfloors=3
    // -projectzseed=7): one fixture shot at every storey height choice (ProjectZSettings.WallHeights, set as the settings
    // window sets them). The liftmodels platforms on levels 1-2 (x 20..28); stairs up from the ground onto the platform's
    // south edge (x 24, ramp z 5..7, the solid end under the level-1 slab at z 8) with a colonist walking up them (a real
    // drafted goto, shot on the middle ramp cell); the elevator at (19, 13) turned east, its shaft to level 2; a mechanoid
    // lift to level 2 (row 16) and the player's emergency ladder to level 1 (row 10). Shots: the stairs from the front
    // and the side, the lifts from the front and the side. The setting and IsoRimBridge.ApplyWallHeight are found by
    // reflection, so the same file runs on a build without them (only the current height is shot there: base vs head).
    public static partial class TestRunner
    {
        private static List<float> syHeights;
        private static int syIdx, syShot;
        private static float sySaved;
        private static System.Reflection.FieldInfo syField;
        private static System.Reflection.MethodInfo syApply;
        private static IntVec3 syStairs;

        private sealed class SyShot
        {
            public string name;
            public int cx, cz;
            public float yaw, size;
        }

        private static readonly SyShot[] SyShots =
        {
            new SyShot { name = "stairs_front", cx = 24, cz = 7, yaw = 0f, size = 4.5f },
            new SyShot { name = "stairs_side", cx = 24, cz = 7, yaw = 90f, size = 4.5f },
            new SyShot { name = "lifts_front", cx = 20, cz = 13, yaw = 0f, size = 6f },
            new SyShot { name = "lifts_side", cx = 20, cz = 13, yaw = 270f, size = 6f },
        };

        // The camera's pitch for these shots (lower than the default iso pitch, so storeys read side on).
        private const float SyPitch = 30f;

        private static bool StepStoreyHeight(Map map)
        {
            Rand.PushState(FixtureSeed("storeyheight_" + stage + "_" + syIdx));
            try
            {
                switch (stage)
                {
                    case "sy_setup":
                        SySetup(map);
                        return true;
                    case "sy_height":
                        SyHeight(map);
                        return true;
                    case "sy_walk":
                        SyWalk(map);
                        return true;
                    case "sy_view":
                        SyView(map);
                        return true;
                    case "sy_take":
                        SyTake(map);
                        return true;
                }
                return false;
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static void SySetup(Map map)
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
            if (Levels.Max(map) < 2 || !IsoRimBridge.Present || IsoRimBridge.ApiVersion < 8)
            {
                Result("storeyheight: a map with 3 levels and IsoRim Api >= 8", false, $"levels {Levels.Min(map)}..{Levels.Max(map)}, IsoRim {IsoRimBridge.Present} Api {IsoRimBridge.ApiVersion}");
                Finish();
                return;
            }
            syField = AccessTools.Field(typeof(ProjectZSettings), "wallHeight");
            syApply = AccessTools.Method(typeof(IsoRimBridge), "ApplyWallHeight");
            float[] choices = AccessTools.Field(typeof(ProjectZSettings), "WallHeights")?.GetValue(null) as float[];
            bool settable = syField != null && syApply != null && choices != null;
            sySaved = settable ? (float)syField.GetValue(ProjectZMod.Settings) : IsoRimBridge.WallHeight;
            syHeights = settable ? choices.ToList() : new List<float> { IsoRimBridge.WallHeight };
            Info($"storeyheight: setting {(settable ? "found" : "not in this build")}, heights {string.Join(", ", syHeights.Select(h => h.ToString("0.00")))}, saved {sySaved:0.00}");

            r2Area = new CellRect(map.Center.x - 22, map.Center.z - 16, 44, 32);
            List<Pawn> cols = Colonists(map).OrderBy(p => p.LabelShort).ToList();
            r2P = cols.First();
            r2Q = null;
            R2Park(map);
            lmElevators.Clear();
            lmLadders.Clear();
            LmPlatforms(map, 2);
            LmElevator(map, ElL(19, 13, 0, map));
            LmLadder(map, 16, 2, false, true);
            LmLadder(map, 10, 1, true, false);
            syStairs = ElG(24, 6);
            Place(PZDefOf.PZ_Stairs, ThingDefOf.BlocksGranite, syStairs, map, Rot4.North);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            Info($"storeyheight: stairs {syStairs} -> {syStairs.GetEdifice(map)}, elevators {string.Join(", ", lmElevators.Select(e => $"{e.Position} level {e.baseLevel} top {e.LinkedTop}"))}; ladders {lmLadders.Count}; walker {r2P.LabelShort}");
            map.weatherManager.curWeather = WeatherDefOf.Clear;
            map.weatherManager.lastWeather = WeatherDefOf.Clear;
            SetHour(map, 11);
            SetIsoRimOption("isometric", true);
            SetIsoRimOption("perspective", false);
            SetIsoRimOption("isoPitch", SyPitch);
            SetIsoRimOption("freeYaw", 0f);
            syIdx = 0;
            stage = "sy_height";
        }

        // The next height: set as the settings window sets it, then the walker up the stairs from their foot.
        private static void SyHeight(Map map)
        {
            if (syIdx >= syHeights.Count)
            {
                if (syApply != null && syField != null)
                {
                    syField.SetValue(ProjectZMod.Settings, sySaved);
                    syApply.Invoke(null, null);
                }
                RestoreIsoRimOption("freeYaw");
                RestoreIsoRimOption("isoPitch");
                FloorView.Set(0);
                Finish();
                return;
            }
            float h = syHeights[syIdx];
            if (syApply != null && syField != null)
            {
                syField.SetValue(ProjectZMod.Settings, h);
                syApply.Invoke(null, null);
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Teleport(r2P, ElG(24, 2));
            r2P.Rotation = Rot4.North;
            Draft(r2P, true);
            // (IsoRim rebuilds the view after a wall height change: 0.35 s without another)
            Wait(150, "sy_walk");
        }

        private static void SyWalk(Map map)
        {
            float h = syHeights[syIdx];
            Result($"SY {h:0.00}: IsoRim's wall height locked to it", Mathf.Abs(IsoRimBridge.LockedWallHeight - h) < 0.001f && Mathf.Abs(IsoRimBridge.WallHeight - h) < 0.001f,
                $"locked {IsoRimBridge.LockedWallHeight:0.000}, wall {IsoRimBridge.WallHeight:0.000}, storey {IsoRimBridge.StoreyHeight:0.000}");
            GoTo(r2P, ElL(24, 11, 1, map));
            SeedTicks("sy_walk_" + syIdx);
            IntVec3 mid = syStairs;
            TickUntil(2000, () => r2P.Position == mid, "sy_view", 1);
            syShot = 0;
        }

        private static void SyView(Map map)
        {
            tickWatch = null;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            SyShot s = SyShots[syShot];
            SetIsoRimOption("freeYaw", s.yaw);
            ShotAt(map, Levels.Max(map), ElG(s.cx, s.cz), s.size);
            Find.UIRoot.screenshotMode.Active = true;
            Wait(120, "sy_take");   // (the camera turns over some frames)
        }

        private static void SyTake(Map map)
        {
            float h = syHeights[syIdx];
            SyShot s = SyShots[syShot];
            Capture($"sy_{h:0.00}_{s.name}");
            if (syShot == 0)
            {
                float rise = Links.Rise(r2P);
                float ramp = Links.RampHeight(r2P.Position, map);
                // (drawn mid-step, a little past the middle cell's centre: about half a storey up, the same share at every height)
                float share = rise / IsoRimBridge.StoreyHeight;
                Result($"SY {h:0.00}: the walker on the middle tread", r2P.Position == syStairs && share > 1f / 3f && share < 2f / 3f,
                    $"walker {r2P.Position} (stairs {syStairs}), job {r2P.CurJobDef?.defName}, rise {rise:0.000} = {share:0.000} storey, middle cell's centre {ramp:0.000}");
                int pieces = Time.frameCount - LiftModels.LastFrame <= 2 ? LiftModels.Pieces : -1;
                object stretch = AccessTools.PropertyGetter(typeof(IsoRimBridge), "ModelStretch")?.Invoke(null, null);
                Info($"SY {h:0.00}: lift pieces drawn {pieces}; storey {IsoRimBridge.StoreyHeight:0.000}, model stretch {stretch ?? "(not in this build)"}");
            }
            syShot++;
            if (syShot >= SyShots.Length)
            {
                syIdx++;
                Wait(3, "sy_height");
                return;
            }
            Wait(3, "sy_view");
        }
    }
}
