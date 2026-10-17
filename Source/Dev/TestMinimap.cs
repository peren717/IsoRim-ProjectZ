using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using ProjectZ.Compat;

namespace ProjectZ.Dev
{
    // Dubs Mint Minimap compat (mode minimap, best with -projectzfloors=3; Compat/MintMinimap.cs). The stairs test's
    // houses; a colonist walks up the stairs (real ticks), the minimap window is open (the mod's own default) and draws
    // through its own code each frame. Reads its state by name (no reference to the mod):
    //   - without the mod: the compat is off and patches nothing;
    //   - with -projectzmintbreak=<member> (a missing patch target): the compat is off with one warning, the minimap
    //     shows the whole map with its bands (the "before" shot);
    //   - with the mod: texture = the ground rows only, its pixels = the minimap's own cell colours of the ground; the
    //     map rect has the ground's aspect; the upstairs colonist's marker sits over his ground column; a wall built on
    //     the ground repaints through the mod's dirty refresh, one upstairs does not touch the ground pixel; a drag on
    //     the minimap jumps the camera to that ground point; no errors (the "after" shots).
    public static partial class TestRunner
    {
        private static Type mmType;
        private static IntVec3 mmGroundWall, mmUpWallG;
        private static Color mmGroundBefore;
        private static Vector3 mmJumpWant;

        private static Window MmWindow() => Traverse.Create(mmType).Field("minimapWindow").GetValue<Window>();

        private static Texture2D MmTex() => Traverse.Create(mmType).Field("MapTexture").GetValue<Texture2D>();

        private static Color[,] MmPixels() => Traverse.Create(mmType).Field("pixels").GetValue<Color[,]>();

        private static Color MmProc(IntVec3 c, Map map) => (Color)AccessTools.Method(mmType, "procCell").Invoke(null, new object[] { c, map });

        private static string C(Color c) => $"({c.r:0.000},{c.g:0.000},{c.b:0.000})";

        private static bool Near(Color a, Color b) => Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f;

        private static bool StepMinimap(Map map)
        {
            switch (stage)
            {
                case "mm_start":
                {
                    mmType = AccessTools.TypeByName("DubsMintMinimap.MainTabWindow_MiniMap");
                    bool loaded = ModsConfig.ActiveModsInLoadOrder.Any(m => m.PackageIdNonUnique == MintMinimap.PackageId);
                    bool ours = Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)?.Owners.Contains("Peren.IsoRimProjectZ.mintminimap") ?? false);
                    Info($"minimap mod loaded {loaded}, type {mmType != null}; compat {MintMinimap.State}, {MintMinimap.SizeSwaps} size reads, {MintMinimap.PosSwaps} positions; simulate missing '{MintMinimap.SimulateMissing}'");
                    if (!loaded)
                    {
                        Result("without the minimap mod: compat off, nothing patched", MintMinimap.State == MintMinimap.Status.Absent && !ours, $"state {MintMinimap.State}, our patches {ours}");
                        Result("no errors", errors == 0, $"{errors} errors");
                        Finish();
                        return true;
                    }
                    if (MintMinimap.SimulateMissing != null)
                    {
                        Result("a missing patch target: compat off (warning), nothing patched", MintMinimap.State == MintMinimap.Status.Skipped && !ours && (MintMinimap.SkipReason ?? "").Contains(MintMinimap.SimulateMissing), $"state {MintMinimap.State} ({MintMinimap.SkipReason}), our patches {ours}");
                    }
                    else
                    {
                        Result("compat on: every target patched", MintMinimap.State == MintMinimap.Status.Active && ours && MintMinimap.SizeSwaps >= 9 && MintMinimap.PosSwaps >= 5, $"state {MintMinimap.State}, {MintMinimap.SizeSwaps} size reads, {MintMinimap.PosSwaps} positions");
                    }
                    Pawn w = Colonists(map).First();
                    walkerId = w.ThingID;
                    IntVec3 up = UpInner(map, sHouseA).First();
                    GoTo(w, up);
                    TickUntil(20000, () => ProjectZ.FloorOf(w.Position, map) == 1 && w.Position == up, "mm_up", 60);
                    return true;
                }
                case "mm_up":
                {
                    tickWatch = null;
                    Pawn w = ById(map, walkerId);
                    Result("a colonist walks up the stairs", w != null && ProjectZ.FloorOf(w.Position, map) == 1, $"{w} at {w?.Position} (floor {ProjectZ.FloorOf(w?.Position ?? IntVec3.Zero, map) + 1}) after {ticksTotal} ticks");
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    ShotAt(map, 0, sHouseA.CenterCell, 22f);
                    Find.Selector.Select(w, false, false);
                    Wait(120, "mm_check");
                    return true;
                }
                case "mm_check":
                {
                    Window win = MmWindow();
                    Texture2D tex = MmTex();
                    AnnexMapComponent a = ProjectZ.Annex(map);
                    int sx = map.Size.x, gz = a.groundZ;
                    Pawn w = ById(map, walkerId);
                    Traverse tw = Traverse.Create(win);
                    Rect mr = tw.Field("MapRect").GetValue<Rect>();
                    Vector2 contact = tw.Field("contact").GetValue<Vector2>();
                    Info($"window open {win?.IsOpen} at {win?.windowRect}; texture {tex?.width}x{tex?.height}; map {map.Size}, ground {sx}x{gz}, stride {a.stride}, floors {a.floors}; map rect {mr}; walker draw pos {w.DrawPos}, marker {contact}");
                    if (MintMinimap.State != MintMinimap.Status.Active)
                    {
                        Result("compat off: the minimap shows the whole map (bands)", win != null && win.IsOpen && tex.height == map.Size.z, $"texture {tex.width}x{tex.height}");
                        Capture("minimap_before");
                        stage = "mm_end";
                        return true;
                    }
                    bool[] dirty = Traverse.Create(mmType).Field("dirtyGirls").GetValue<bool[]>();
                    Result("minimap open, texture = the ground rows, dirty flags full map", win != null && win.IsOpen && tex.width == sx && tex.height == gz && dirty?.Length == map.cellIndices.NumGridCells,
                        $"texture {tex.width}x{tex.height}, ground {sx}x{gz}, dirty {dirty?.Length} of {map.cellIndices.NumGridCells}");
                    Color[,] px = MmPixels();
                    int bad = 0, stale = 0, upDiffers = 0;
                    string firstBad = null;
                    foreach (IntVec3 g in ProjectZ.GroundRect(map))
                    {
                        Color want = MmProc(g, map);
                        if (!px[g.x, g.z].Equals(want))
                        {
                            if (dirty[map.cellIndices.CellToIndex(g)])
                            {
                                stale++;
                            }
                            else
                            {
                                bad++;
                                firstBad = firstBad ?? $"{g}: {C(px[g.x, g.z])} want {C(want)}";
                            }
                        }
                        IntVec3 u = ProjectZ.CellOnFloor(g, 1, map);
                        if (u.IsValid && !MmProc(u, map).Equals(want))
                        {
                            upDiffers++;
                        }
                    }
                    IntVec3 col = ProjectZ.GroundCellOf(w.Position, map);
                    Color texPx = tex.GetPixel(col.x, col.z);
                    Result("every ground pixel = the minimap's colour of that ground cell (not the floor above)", bad == 0 && Near(texPx, px[col.x, col.z]),
                        $"{bad} wrong{(firstBad != null ? " (first " + firstBad + ")" : "")}, {stale} awaiting refresh, {upDiffers} cells whose floor-2 colour differs; texture at {col} {C(texPx)}");
                    float aspect = mr.height / mr.width;
                    Result("map rect has the ground's aspect", Mathf.Abs(aspect - (float)gz / sx) < 0.01f, $"{mr.width:0.#}x{mr.height:0.#} = {aspect:0.000}, ground {(float)gz / sx:0.000}, whole map {(float)map.Size.z / sx:0.000}");
                    float wantX = w.DrawPos.x / sx * mr.width;
                    float wantY = mr.height * (1f - (w.DrawPos.z - a.stride) / gz);
                    Result("upstairs colonist's marker over his ground column", Mathf.Abs(contact.x - wantX) < 0.5f && Mathf.Abs(contact.y - wantY) < 0.5f && contact.y > 0.5f,
                        $"marker {contact}, want ({wantX:0.0}, {wantY:0.0}); walker {w.Position}, ground column {col}");
                    Capture("minimap_after");
                    // (the next change waits for the shot: a change in this update would show in it)
                    Wait(2, "mm_dirty");
                    return true;
                }
                case "mm_dirty":
                {
                    mmGroundWall = farGround + new IntVec3(2, 0, 0);
                    mmUpWallG = new IntVec3(sHouseA.minX + 1, 0, sHouseA.minZ + 4);
                    mmGroundBefore = MmPixels()[mmGroundWall.x, mmGroundWall.z];
                    Place(ThingDefOf.Wall, ThingDefOf.WoodLog, mmGroundWall, map);
                    Place(ThingDefOf.Wall, ThingDefOf.WoodLog, UpS(mmUpWallG, map), map);
                    Info($"walls: ground {mmGroundWall} (pixel {C(mmGroundBefore)}), floor 2 over {mmUpWallG} ({UpS(mmUpWallG, map).GetEdifice(map)})");
                    // the mod refreshes dirty cells every 500 frames
                    Wait(700, "mm_dirty_check");
                    return true;
                }
                case "mm_dirty_check":
                {
                    Color[,] px = MmPixels();
                    Color gNow = px[mmGroundWall.x, mmGroundWall.z], gWant = MmProc(mmGroundWall, map);
                    Color uNow = px[mmUpWallG.x, mmUpWallG.z], uGround = MmProc(mmUpWallG, map), uWall = MmProc(UpS(mmUpWallG, map), map);
                    Result("a wall on the ground repaints through the mod's refresh", gNow.Equals(gWant) && !gNow.Equals(mmGroundBefore), $"{C(mmGroundBefore)} -> {C(gNow)}, want {C(gWant)}");
                    Result("a wall upstairs leaves the ground pixel", uNow.Equals(uGround) && !uGround.Equals(uWall), $"pixel {C(uNow)}, ground {C(uGround)}, the wall upstairs {C(uWall)}");
                    stage = "mm_click";
                    return true;
                }
                case "mm_click":
                {
                    // A drag on the minimap: the mod's own mouse-down state, a drag event over the map rect, its own
                    // Minimap(inRect) (input part) as its window runs it.
                    AnnexMapComponent a = ProjectZ.Annex(map);
                    guiAction = () =>
                    {
                        Window win = MmWindow();
                        Traverse tw = Traverse.Create(win);
                        Rect mr = tw.Field("MapRect").GetValue<Rect>();
                        Vector2 pct = new Vector2(0.25f, 0.3f);
                        Vector2 mouse = new Vector2(mr.x + pct.x * mr.width, mr.yMax - pct.y * mr.height + 25f);
                        float margin = tw.Property("Margin").GetValue<float>();
                        Rect inRect = new Rect(0f, 0f, win.windowRect.width, win.windowRect.height).ContractedBy(margin);
                        mmJumpWant = new Vector3(map.Size.x * pct.x, 0f, a.groundZ * pct.y);
                        tw.Field("draggingCam").SetValue(true);
                        Event saved = Event.current;
                        Event.current = new Event { type = EventType.MouseDrag, mousePosition = mouse };
                        try
                        {
                            AccessTools.Method(win.GetType(), "Minimap").Invoke(win, new object[] { inRect });
                        }
                        finally
                        {
                            Event.current = saved;
                            tw.Field("draggingCam").SetValue(false);
                        }
                        Info($"drag at {mouse} (map rect {mr}, in rect {inRect}), want camera at {mmJumpWant}");
                    };
                    Wait(10, "mm_click_check");
                    return true;
                }
                case "mm_click_check":
                {
                    Vector3 root = Traverse.Create(Find.CameraDriver).Field("rootPos").GetValue<Vector3>();
                    Result("a drag on the minimap moves the camera to that ground point", Mathf.Abs(root.x - mmJumpWant.x) < 0.6f && Mathf.Abs(root.z - mmJumpWant.z) < 0.6f, $"camera {root}, want {mmJumpWant}");
                    Pawn w = ById(map, walkerId);
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(w, false, false);
                    Wait(30, "mm_click_shot");
                    return true;
                }
                case "mm_click_shot":
                    Capture("minimap_after_jump");
                    stage = "mm_end";
                    return true;
                case "mm_end":
                    Result("no errors", errors == 0, $"{errors} errors, {warnings} warnings");
                    Finish();
                    return true;
            }
            return false;
        }
    }
}
