using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode minebody (user play test 2026-10-19: a colonist mining in a tall mountain blinks out, its body gone, its pickaxe
    // still there; on flat ground it does not): a fixed-seed tall-mountain map (as rocktop: Mountainous,
    // -projectztallmountains, -projectzfloors=4, -projectzmapsize=250), saved once as pz_minebody; every check reloads it and
    // picks its site the same way every run (nearest the colonists). The colonist who mines fastest, in magenta clothes, mines
    // natural rock with the real job (designation, work giver, JobDriver_Mine); the others are drafted far away. The camera
    // is on the miner, its storey shown, the interface hidden. While the job runs, every frame is filmed with the game paused
    // and three ticks per frame, each frame's ticks under their own fixed seed, and the magenta pixels round the miner are
    // counted in each frame: the body is drawn when there are at least half as many as in the frame with the most. A strip
    // round the worst frame is kept, the other frames deleted.
    //   M1 ground floor, at a mountain face (the miner outside the face, the rock and its rock-top floors ahead)
    //   M2 ground floor, inside the mountain (four cells tunnelled first by the same job, the miner under the thick roof and a
    //      fogged plateau top)
    //   M3 level 1, on a rim top at a level-1 rock face (the ground rock under the miner is a revealed face)
    //   M4 flat ground: a rock one storey high, no thick roof
    //   M5 level 1, deep on a plateau the miner opened by stepping onto its rim (MountainFog), fogged rock under the miner:
    //      the user's case. Failed before the IsoRim fix (RoofClip.FloorBlock): the work stroke dips the figure 0.02 cells,
    //      the shader then took it for a thing standing in the fogged rock below and clipped it at that rock's fog lid.
    // Options: -projectzmbview=top (level 3 shown instead of the miner's own), -projectzmbptr (IsoRim's cut-away pointer
    // held on the miner, cut under the pointer on).
    public static partial class TestRunner
    {
        private const int MbFrames = 240, MbTicksPerFrame = 3;
        private static readonly string[] mbNames = { "M1 ground floor, at a mountain face", "M2 ground floor, inside the mountain", "M3 level 1, on a top at a level-1 face", "M4 control, a low rock on flat ground", "M5 level 1, deep on an opened plateau (fogged rock under the miner)" };
        private static int mbCheck, mbFrame;
        private static Pawn mbPawn;
        private static IntVec3 mbStand, mbTarget;
        private static readonly List<IntVec3> mbCells = new List<IntVec3>();
        private static int mbLevel;
        private static StreamWriter mbLog;
        private static HarmonyLib.AccessTools.FieldRef<Pawn_DrawTracker, JitterHandler> mbJitterBound;
        private static HarmonyLib.AccessTools.FieldRef<Pawn_DrawTracker, JitterHandler> mbJitter => mbJitterBound ??= HarmonyLib.AccessTools.FieldRefAccess<Pawn_DrawTracker, JitterHandler>("jitterer");   // (bound on first use: nothing for players)

        private static string MbShot(int check, int frame) => Path.Combine(outDir, $"minebody_m{check + 1}_f{frame:000}.png");

        private static bool StepMineBody(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            switch (stage)
            {
                case "mb_setup":
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    if (a == null || !Mountains.On(map))
                    {
                        Result("minebody: a tall-mountain map", false, "no annex or no tall mountains (-projectztallmountains)");
                        Finish();
                        return true;
                    }
                    Info($"minebody: map {map.Size}, floors {a.floors}, rock {Mountains.LastRock}, tops {Mountains.LastTops}");
                    GameDataSaveLoader.SaveGame("pz_minebody");
                    mbCheck = 0;
                    stage = "mb_place";
                    pendingLoad = "pz_minebody";
                    return true;

                case "mb_place":
                {
                    if (mbCheck >= mbNames.Length)
                    {
                        mbLog?.Close();
                        mbLog = null;
                        Finish();
                        return true;
                    }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    DebugSettings.enableRandomMentalStates = false;
                    Find.PlaySettings.autoRebuild = false;
                    Find.Storyteller.storytellerComps.Clear();
                    Find.Storyteller.incidentQueue.Clear();
                    mbPawn = Colonists(map).Where(p => !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining)).OrderByDescending(p => p.GetStatValue(StatDefOf.MiningSpeed)).ThenBy(p => p.thingIDNumber).FirstOrDefault();
                    if (mbPawn == null)
                    {
                        Result($"minebody {mbNames[mbCheck]}: a miner", false, "no colonist can mine");
                        Finish();
                        return true;
                    }
                    if (!MbSite(map, mbCheck))
                    {
                        Result($"minebody {mbNames[mbCheck]}: a site", false, "none found");
                        mbCheck++;
                        pendingLoad = "pz_minebody";
                        return true;
                    }
                    if (mbCheck == 4)
                    {
                        Wait(MbReachFrames, "mb_m5_reach");   // (the frames the reach fog's batched run takes after the step onto the rim)
                        return true;
                    }
                    goto case "mb_place2";
                }
                case "mb_m5_reach":
                {
                    // M5's site: the plateau opened from its rim, the ground under the miner's top still fogged rock, the rock
                    // face north of it seen
                    IntVec3 g5 = ProjectZ.GroundCellOf(mbStand, map), face = mbStand + IntVec3.North;
                    bool open = !map.fogGrid.IsFogged(mbStand) && map.fogGrid.IsFogged(g5) && !map.fogGrid.IsFogged(face);
                    if (!open)
                    {
                        Result($"minebody {mbNames[mbCheck]}: a site", false, $"after the rim: top {mbStand} fogged {map.fogGrid.IsFogged(mbStand)}, ground {g5} fogged {map.fogGrid.IsFogged(g5)}, face {face} fogged {map.fogGrid.IsFogged(face)}");
                        mbCheck++;
                        stage = "mb_place";
                        pendingLoad = "pz_minebody";
                        return true;
                    }
                    goto case "mb_place2";
                }
                case "mb_place2":
                {
                    // the others drafted far off; the miner in magenta clothes (counted in the frames)
                    IntVec3 park = IntVec3.Invalid;
                    foreach (IntVec3 g in Near(mbStand.z < a.groundZ ? mbStand : ProjectZ.GroundCellOf(mbStand, map), 60, map))
                    {
                        if ((g - ProjectZ.GroundCellOf(mbStand, map)).LengthHorizontal > 30 && g.Standable(map) && !map.fogGrid.IsFogged(g))
                        {
                            park = g;
                            break;
                        }
                    }
                    foreach (Pawn p in Colonists(map))
                    {
                        if (p == mbPawn)
                        {
                            continue;
                        }
                        if (park.IsValid)
                        {
                            Teleport(p, park);
                        }
                        if (p.drafter != null)
                        {
                            p.drafter.Drafted = true;
                        }
                    }
                    Teleport(mbPawn, mbStand);
                    if (mbPawn.drafter != null)
                    {
                        mbPawn.drafter.Drafted = false;
                    }
                    foreach (Apparel ap in mbPawn.apparel.WornApparel)
                    {
                        ap.SetColor(new Color(1f, 0f, 1f), reportFailure: false);
                    }
                    if (mbPawn.story != null)
                    {
                        mbPawn.story.HairColor = new Color(1f, 0f, 1f);
                    }
                    mbPawn.Drawer.renderer.SetAllGraphicsDirty();
                    foreach (IntVec3 c in mbCells)
                    {
                        map.designationManager.AddDesignation(new Designation(c, DesignationDefOf.Mine));
                    }
                    FgWork(map, new[] { mbPawn }, WorkTypeDefOf.Mining);
                    if (mbPawn.needs?.food != null) mbPawn.needs.food.CurLevel = mbPawn.needs.food.MaxLevel;
                    if (mbPawn.needs?.rest != null) mbPawn.needs.rest.CurLevel = mbPawn.needs.rest.MaxLevel;
                    if (mbPawn.needs?.joy != null) mbPawn.needs.joy.CurLevel = mbPawn.needs.joy.MaxLevel;
                    Info($"minebody {mbNames[mbCheck]}: miner {mbPawn} on {mbStand} (level {mbLevel}), cells {string.Join(" ", mbCells.Select(c => c + " " + c.GetEdifice(map)?.def.defName))}");
                    // (M2: the first two cells are tunnelled at speed by the same job; then the miner works on the third)
                    IntVec3 film = mbCheck == 1 ? mbCells[4] : mbCells[0];
                    mbTarget = film;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;   // (the ticks come from the test only: the same count every run)
                    mbFrame = 0;
                    stage = "mb_toready";
                    return true;
                }
                case "mb_toready":
                {
                    // up to 300 ticks a frame (each frame's ticks under their own fixed seed) until the miner works at the cell
                    System.Func<bool> working = () => mbPawn.jobs.curDriver is JobDriver_Mine && mbPawn.CurJob.targetA.Cell == mbTarget && mbPawn.jobs.curDriver.CurToilIndex >= 1 && !mbPawn.pather.Moving;
                    Rand.PushState(FixtureSeed($"{mode}_{mbCheck}_work") + mbFrame * SeedStride);
                    try
                    {
                        for (int t = 0; t < 300 && !working(); t++)
                        {
                            Find.TickManager.DoSingleTick();
                        }
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                    if (!working() && ++mbFrame < 80)
                    {
                        return true;
                    }
                    stage = "mb_ready";
                    return true;
                }
                case "mb_ready":
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    bool ok = mbPawn.jobs.curDriver is JobDriver_Mine && mbPawn.CurJob.targetA.Cell == mbTarget;
                    if (!ok)
                    {
                        Result($"minebody {mbNames[mbCheck]}: the miner works at {mbTarget}", false, $"job {mbPawn.CurJobDef?.defName} at {mbPawn.CurJob?.targetA}, on {mbPawn.Position}");
                        mbCheck++;
                        stage = "mb_place";
                        pendingLoad = "pz_minebody";
                        return true;
                    }
                    Info($"minebody {mbNames[mbCheck]}: mining {mbTarget} ({mbTarget.GetEdifice(map)?.def.defName}, hp {mbTarget.GetEdifice(map)?.HitPoints}) from {mbPawn.Position}; roof here {map.roofGrid.RoofAt(mbPawn.Position)?.defName ?? "none"}, target fogged {map.fogGrid.IsFogged(mbTarget)}; above: {string.Join(", ", Enumerable.Range(mbLevel + 1, 3).Select(L => { IntVec3 u = Levels.Cell(ProjectZ.GroundCellOf(mbPawn.Position, map), L, map); return u.IsValid ? $"L{L} {u} {Floors.KindAt(u, map)} fogged {map.fogGrid.IsFogged(u)}" : $"L{L} -"; }))}; IsoRim storey {MbStorey()}");
                    ShotAt(map, MbView(map), ProjectZ.GroundCellOf(mbPawn.Position, map), 7f);
                    MbPointer(map);
                    Find.UIRoot.screenshotMode.Active = true;
                    mbFrame = 0;
                    if (mbLog == null)
                    {
                        mbLog = new StreamWriter(Path.Combine(outDir, "minebody_frames.tsv"), false);
                        mbLog.WriteLine("check\tframe\ttick\tpos\tdrawPos\tjitter\ttoil\tticksToPickHit\thp\tfloorView");
                    }
                    Wait(30, "mb_film");
                    return true;
                }
                case "mb_film":
                {
                    if (mbFrame >= MbFrames || !(mbPawn.jobs.curDriver is JobDriver_Mine))
                    {
                        Info($"minebody {mbNames[mbCheck]}: filmed {mbFrame} frames; job now {mbPawn.CurJobDef?.defName}");
                        mbLog.Flush();
                        Wait(20, "mb_count");
                        return true;
                    }
                    Rand.PushState(FixtureSeed($"{mode}_{mbCheck}_mine") + mbFrame * SeedStride);
                    try
                    {
                        for (int t = 0; t < MbTicksPerFrame; t++)
                        {
                            Find.TickManager.DoSingleTick();
                        }
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                    mbPawn.Drawer.tweener.ResetTweenedPosToRoot();
                    Thing tg = mbTarget.GetEdifice(map);
                    JitterHandler j = mbJitter(mbPawn.Drawer);
                    mbLog.WriteLine($"{mbCheck + 1}\t{mbFrame}\t{Find.TickManager.TicksGame}\t{mbPawn.Position}\t{mbPawn.DrawPos.x:0.000},{mbPawn.DrawPos.y:0.000},{mbPawn.DrawPos.z:0.000}\t{j?.CurrentOffset.x:0.000},{j?.CurrentOffset.z:0.000}\t{mbPawn.jobs.curDriver?.CurToilIndex}\t{(mbPawn.jobs.curDriver is JobDriver_Mine jm ? HarmonyLib.Traverse.Create(jm).Field("ticksToPickHit").GetValue<int>() : -999)}\t{tg?.HitPoints}\t{FloorView.Current}");
                    ScreenCapture.CaptureScreenshot(MbShot(mbCheck, mbFrame));
                    mbFrame++;
                    return true;
                }
                case "mb_count":
                {
                    List<int> counts = new List<int>();
                    for (int f = 0; f < mbFrame; f++)
                    {
                        counts.Add(MbMagenta(MbShot(mbCheck, f)));
                    }
                    int max = counts.Count == 0 ? 0 : counts.Max();
                    List<int> gone = Enumerable.Range(0, counts.Count).Where(f => counts[f] < max / 2).ToList();
                    mbLog.WriteLine($"# M{mbCheck + 1} magenta per frame: {string.Join(" ", counts)}");
                    mbLog.Flush();
                    Result($"minebody {mbNames[mbCheck]}: the miner's body is drawn in every filmed frame", counts.Count >= 40 && max >= 50 && gone.Count == 0,
                        $"{counts.Count} frames, magenta pixels max {max}, min {(counts.Count == 0 ? 0 : counts.Min())}; frames with under half: {gone.Count}{(gone.Count == 0 ? "" : " (" + string.Join(",", gone.Take(20)) + ")")}");
                    // kept: every second frame round the one with the fewest (a strip), the rest deleted (this run's own files)
                    int worst = counts.Count == 0 ? 0 : counts.IndexOf(counts.Min());
                    for (int f = 0; f < mbFrame; f++)
                    {
                        if (Mathf.Abs(f - worst) > 8 || (f - worst) % 2 != 0)
                        {
                            File.Delete(MbShot(mbCheck, f));
                        }
                    }
                    Info($"minebody {mbNames[mbCheck]}: frames kept round frame {worst}: minebody_m{mbCheck + 1}_f*.png");
                    mbCheck++;
                    stage = "mb_place";
                    pendingLoad = "pz_minebody";
                    return true;
                }
            }
            return false;
        }

        // M3-M5 laid, not searched for (the seed-7 map has no M5 plateau, and a face south of the miner hid the body behind
        // the rock it mines: the camera looks north). TestMtnLay: the nearest open spot to home cleared and raised; the
        // miner always south of the rock it mines.
        //   M3, M5: a plateau of height 1 (MbPlateauDepth rows) with a block of height 2 north of it, a ring of open ground
        //   round both. M3 on the plateau's west rim top under the block (the ground under it a revealed face), M5 on its
        //   middle top under the block (the ground under it fogged rock) after stepping onto the west rim (MountainFog).
        //   M4: one column of height 1 (a rock one storey high, no thick roof), the miner south of it on open ground.
        private const int MbLaidWidth = 17, MbPlateauDepth = 10, MbBlockDepth = 5, MbRockSpot = 7;

        // Frames after M5's step onto the rim before its site is read: the reach fog runs batched, once a frame when asked
        // (a teleport asks); a few frames of margin.
        private const int MbReachFrames = 5;

        private static bool MbLay(Map map, int check, IntVec3 home)
        {
            if (check == 3)
            {
                CellRect? spot4 = MlSpot(map, home, MbRockSpot, MbRockSpot);
                if (spot4 == null)
                {
                    return false;
                }
                IntVec3 o = spot4.Value.CenterCell;
                MlClear(map, spot4.Value);
                MlRaise(map, spot4.Value, g => g == o ? 1 : 0, o + IntVec3.South);
                mbStand = o + IntVec3.South;
                mbLevel = 0;
                mbCells.Add(o);
                return true;
            }
            int depth = MbPlateauDepth + MbBlockDepth + 2;
            CellRect? spot = MlSpot(map, home, MbLaidWidth, depth);
            if (spot == null)
            {
                return false;
            }
            CellRect r = spot.Value;
            int top = r.minZ + MbPlateauDepth;   // the plateau's top row (rows minZ+1 .. top), the block above it
            MlClear(map, r);
            MlRaise(map, r, g => g.x == r.minX || g.x == r.maxX || g.z == r.minZ || g.z == r.maxZ ? 0 : g.z <= top ? 1 : 2, new IntVec3(r.minX, 0, r.minZ));
            IntVec3 g3 = new IntVec3(check == 2 ? r.minX + 1 : r.CenterCell.x, 0, top);
            IntVec3 t = Levels.Cell(g3, 1, map);
            if (check == 4)
            {
                // onto the west rim: the reach fog's own batched run opens the plateau the miner can walk on (mb_m5_reach)
                Teleport(mbPawn, Levels.Cell(new IntVec3(r.minX + 1, 0, r.minZ + MbPlateauDepth / 2), 1, map));
            }
            mbStand = t;
            mbLevel = 1;
            mbCells.Add(t + IntVec3.North);
            return true;
        }

        // -projectzmbview=top: the highest level in use shown (else the miner's own level).
        private static int MbView(Map map)
        {
            if (GenCommandLine.TryGetCommandLineArg("projectzmbview", out string v) && v == "top")
            {
                return 3;
            }
            return mbLevel;
        }

        // -projectzmbptr: IsoRim's cut-away pointer held on the miner (cut under the pointer on), as when the player points at it.
        private static Vector3 mbPtr;
        private static bool mbPtrPatched;

        private static void MbPointer(Map map)
        {
            if (!GenCommandLine.CommandLineArgPassed("projectzmbptr"))
            {
                return;
            }
            mbPtr = mbPawn.DrawPos;
            if (!mbPtrPatched)
            {
                mbPtrPatched = true;
                new HarmonyLib.Harmony("Peren.IsoRimProjectZ.test.mb").Patch(HarmonyLib.AccessTools.Method(HarmonyLib.AccessTools.TypeByName("IsoRim.Cutaway"), "Pointer"),
                    prefix: new HarmonyLib.HarmonyMethod(typeof(TestRunner), nameof(MbPointerPrefix)));
                object st = HarmonyLib.AccessTools.Property(HarmonyLib.AccessTools.TypeByName("IsoRim.IsoRimMod"), "Settings")?.GetValue(null)
                    ?? HarmonyLib.AccessTools.Field(HarmonyLib.AccessTools.TypeByName("IsoRim.IsoRimMod"), "Settings")?.GetValue(null);
                if (st != null)
                {
                    HarmonyLib.Traverse.Create(st).Field("cutHover").SetValue(true);
                }
                Info($"minebody: cut-away pointer held on the miner, cutHover on (settings {(st != null ? "found" : "missing")})");
            }
        }

        private static bool MbPointerPrefix(ref Vector3 ground, ref bool __result)
        {
            ground = mbPtr;
            __result = true;
            return false;
        }

        // (info) IsoRim's floor heights (true cells), as its shaders get them.
        private static string MbStorey()
        {
            object h = HarmonyLib.AccessTools.Field(HarmonyLib.AccessTools.TypeByName("IsoRim.Floors"), "heights")?.GetValue(null);
            return h is float[] f ? string.Join(",", f.Select(x => x.ToString("0.000"))) : "?";
        }

        // Magenta pixels (the miner's clothes and hair) in the middle of a frame.
        private static int MbMagenta(string path)
        {
            if (!File.Exists(path))
            {
                return -1;
            }
            Texture2D tex = new Texture2D(2, 2);
            if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path), false))
            {
                return -1;
            }
            Color32[] px = tex.GetPixels32();
            int w = tex.width, h = tex.height, n = 0;
            for (int y = h / 5; y < h * 4 / 5; y++)
            {
                for (int x = w / 3; x < w * 2 / 3; x++)
                {
                    Color32 c = px[y * w + x];
                    int lo = Mathf.Min(c.r, c.b);
                    if (lo >= 24 && lo > c.g * 2 + 8)
                    {
                        n++;
                    }
                }
            }
            Object.Destroy(tex);
            return n;
        }

        // The check's site: where the miner stands (mbStand, level mbLevel) and the rock it mines, in order (mbCells).
        private static bool MbSite(Map map, int check)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            IntVec3 home = RtHome(map);
            mbCells.Clear();
            mbStand = IntVec3.Invalid;
            Pawn any = Colonists(map).FirstOrDefault();
            System.Func<IntVec3, bool> rock = c => c.InBounds(map) && c.GetEdifice(map) is Mineable m && m.def.building.isNaturalRock && !m.def.building.isResourceRock;
            if (check >= 2)
            {
                return MbLay(map, check, home);
            }
            foreach (IntVec3 g in Near(home, 110, map))
            {
                if (g.x < 3 || g.z < 3 || g.x >= map.Size.x - 3 || g.z >= a.groundZ - 3)
                {
                    continue;
                }
                // ground: open, unroofed, reachable; ahead 4 cells of tall-mountain rock (thick roof, a top at level 2 or more)
                if (!g.Standable(map) || g.Roofed(map) || map.fogGrid.IsFogged(g) || any == null || !map.reachability.CanReach(any.Position, g, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors)))
                {
                    continue;
                }
                foreach (IntVec3 d in GenAdj.CardinalDirections)
                {
                    bool ok = true;
                    for (int i = 1; i <= 6 && ok; i++)
                    {
                        IntVec3 c = g + d * i;
                        ok = rock(c) && (map.roofGrid.RoofAt(c)?.isThickRoof ?? false) && Column.NaturalTop(c, map) >= 2
                            && rock(c + d.RotatedBy(RotationDirection.Clockwise)) && rock(c + d.RotatedBy(RotationDirection.Counterclockwise));
                    }
                    if (ok)
                    {
                        mbStand = g;
                        mbLevel = 0;
                        for (int i = 1; i <= 6; i++)
                        {
                            mbCells.Add(g + d * i);
                        }
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
