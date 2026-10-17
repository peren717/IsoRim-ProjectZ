using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ProjectZ.Compat
{
    // Dubs Mint Minimap (dubwise.dubsmintminimap): the minimap shows the ground only, not the floor bands above it
    // (design isorim-pz-minimap-compat.md, option 1). No reference to the mod: every member is found by name, and when
    // one is missing nothing is patched (one warning; the minimap then shows the whole map as before).
    //   A. its draw and input methods read the map size through ViewSize: the ground rows on a map with floors, so the
    //      aspect, the camera box, click/drag to the camera and every marker work on the ground rect;
    //   B. its markers read thing positions through ViewPos: an upstairs pawn shows over its ground column;
    //   C. its texture build (UpdateMapTex) paints only the ground rows on a map with floors (BuildTex), keeping its
    //      full-map dirty array (its section hook marks band cells too).
    // The floor shown is a parameter of BuildTex (0 = ground), for a later "show the viewed floor" option.
    [StaticConstructorOnStartup]
    public static class MintMinimap
    {
        public const string PackageId = "dubwise.dubsmintminimap";
        private const string TypeName = "DubsMintMinimap.MainTabWindow_MiniMap";
        private const string HarmonyId = "Peren.IsoRimProjectZ.mintminimap";

        // The methods whose map-size reads go through ViewSize; the marker ones also have their positions swapped.
        private static readonly string[] SizeMethods = { "Minimap", "BaseMapRectSize", "DrawCam", "DrawGrid", "DrawAllPawns", "DrawFires", "DrawThingLocators", "DrawProjectiles", "DrawPings" };
        private static readonly string[] MarkerMethods = { "DrawAllPawns", "DrawFires", "DrawThingLocators", "DrawProjectiles", "DrawPings" };

        public enum Status { Absent, Skipped, Active }

        public static Status State { get; private set; } = Status.Absent;
        public static int SizeSwaps { get; private set; }
        public static int PosSwaps { get; private set; }
        public static string SkipReason { get; private set; }
        // Test only (-projectzmintbreak=<member>): that member of the minimap counts as missing.
        internal static string SimulateMissing => Dev.TestRunner.MintBreak;

        private static readonly MethodInfo MapSize = AccessTools.PropertyGetter(typeof(Map), nameof(Map.Size));
        private static readonly MethodInfo ThingDrawPos = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.DrawPos));

        private static FieldInfo texField, pixelsField, dirtyField;
        private static Func<IntVec3, Map, Color> procCell;

        static MintMinimap()
        {
            if (!ModsConfig.ActiveModsInLoadOrder.Any(m => m.PackageIdNonUnique == PackageId))
            {
                return;
            }
            try
            {
                string missing = Patch();
                if (missing != null)
                {
                    State = Status.Skipped;
                    SkipReason = missing;
                    SizeSwaps = PosSwaps = 0;
                    Log.Warning("[ProjectZ] Dubs Mint Minimap compat off (" + missing + "): the minimap shows the floor bands.");
                    return;
                }
                State = Status.Active;
                Log.Message($"[ProjectZ] Dubs Mint Minimap compat on: {SizeSwaps} size reads, {PosSwaps} marker positions, texture build");
            }
            catch (Exception e)
            {
                State = Status.Skipped;
                SkipReason = e.Message;
                new Harmony(HarmonyId).UnpatchAll(HarmonyId);
                Log.Warning("[ProjectZ] Dubs Mint Minimap compat off (" + e.Message + "): the minimap shows the floor bands.");
            }
        }

        // Resolves every member first and patches only when all of them are there; returns what is missing, else null.
        private static string Patch()
        {
            Type t = SimulateMissing == "type" ? null : AccessTools.TypeByName(TypeName);
            if (t == null)
            {
                return "no " + TypeName;
            }
            texField = Field(t, "MapTexture");
            pixelsField = Field(t, "pixels");
            dirtyField = Field(t, "dirtyGirls");
            MethodInfo proc = Method(t, "procCell", new[] { typeof(IntVec3), typeof(Map) });
            MethodInfo update = Method(t, "UpdateMapTex", new[] { typeof(Map), typeof(bool) });
            if (texField?.FieldType != typeof(Texture2D) || pixelsField?.FieldType != typeof(Color[,]) || dirtyField?.FieldType != typeof(bool[]) || proc?.ReturnType != typeof(Color) || update == null)
            {
                return "texture members not found";
            }
            List<MethodInfo> targets = new List<MethodInfo>();
            foreach (string name in SizeMethods)
            {
                MethodInfo m = Method(t, name, null);
                if (m == null)
                {
                    return "no " + name;
                }
                // every target must read the map size (and the marker ones a thing position), or its IL changed
                List<CodeInstruction> il = PatchProcessor.GetOriginalInstructions(m);
                if (!il.Any(i => i.Calls(MapSize)) || MarkerMethods.Contains(name) && !il.Any(i => i.Calls(ThingDrawPos)))
                {
                    return name + " changed";
                }
                SizeSwaps += il.Count(i => i.Calls(MapSize));
                PosSwaps += il.Count(i => i.Calls(ThingDrawPos));
                targets.Add(m);
            }
            procCell = AccessTools.MethodDelegate<Func<IntVec3, Map, Color>>(proc);
            Harmony h = new Harmony(HarmonyId);
            HarmonyMethod transpiler = new HarmonyMethod(typeof(MintMinimap), nameof(Transpiler));
            foreach (MethodInfo m in targets)
            {
                h.Patch(m, transpiler: transpiler);
            }
            h.Patch(update, prefix: new HarmonyMethod(typeof(MintMinimap), nameof(UpdateMapTexPrefix)));
            return null;
        }

        private static FieldInfo Field(Type t, string name) => name == SimulateMissing ? null : AccessTools.Field(t, name);

        private static MethodInfo Method(Type t, string name, Type[] args) => name == SimulateMissing ? null : AccessTools.Method(t, name, args);

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo viewSize = AccessTools.Method(typeof(MintMinimap), nameof(ViewSize));
            MethodInfo viewPos = AccessTools.Method(typeof(MintMinimap), nameof(ViewPos), new[] { typeof(Thing) });
            foreach (CodeInstruction i in instructions)
            {
                if (i.Calls(MapSize))
                {
                    i.opcode = OpCodes.Call;
                    i.operand = viewSize;
                }
                else if (i.Calls(ThingDrawPos))
                {
                    i.opcode = OpCodes.Call;
                    i.operand = viewPos;
                }
                yield return i;
            }
        }

        // The size the minimap works in: the ground rows on a map with floors.
        public static IntVec3 ViewSize(Map map)
        {
            IntVec3 s = map.Size;
            AnnexMapComponent a = ProjectZ.Annex(map);
            return a == null ? s : new IntVec3(s.x, s.y, a.groundZ);
        }

        // A thing's marker position: lowered from its band onto its ground column.
        public static Vector3 ViewPos(Thing thing)
        {
            Vector3 p = thing.DrawPos;
            AnnexMapComponent a = ProjectZ.Annex(thing.MapHeld);
            if (a == null || p.z < a.groundZ)
            {
                return p;
            }
            return new Vector3(p.x, p.y, p.z - Mathf.FloorToInt(p.z) / a.stride * a.stride);
        }

        private static bool UpdateMapTexPrefix(Map map, bool forced, ref Texture2D __result)
        {
            if (map == null || ProjectZ.Annex(map) == null)
            {
                return true;
            }
            __result = BuildTex(map, forced, 0);
            return false;
        }

        // The minimap texture of one floor (0 = ground) on a map with floors: one pixel per ground cell, coloured by the
        // minimap's own cell colour of that cell on the floor; only forced or dirty cells are painted.
        private static Texture2D BuildTex(Map map, bool forced, int floor)
        {
            int sx = map.Size.x, gz = ProjectZ.GroundZ(map);
            Texture2D tex = (Texture2D)texField.GetValue(null);
            Color[,] pixels = (Color[,])pixelsField.GetValue(null);
            bool[] dirty = (bool[])dirtyField.GetValue(null);
            if (tex.width != sx || tex.height != gz || pixels == null || pixels.GetLength(0) != sx || pixels.GetLength(1) != gz)
            {
                tex.filterMode = FilterMode.Point;
                tex.Reinitialize(sx, gz);
                Color[] black = new Color[sx * gz];
                for (int i = 0; i < black.Length; i++)
                {
                    black[i] = Color.black;
                }
                tex.SetPixels(black);
                tex.Apply(true);
                pixels = new Color[sx, gz];
                for (int x = 0; x < sx; x++)
                {
                    for (int z = 0; z < gz; z++)
                    {
                        pixels[x, z] = Color.black;
                    }
                }
                pixelsField.SetValue(null, pixels);
                forced = true;
            }
            // full map size: the minimap's section hook marks cells of every band
            if (dirty == null || dirty.Length != map.cellIndices.NumGridCells)
            {
                dirty = new bool[map.cellIndices.NumGridCells];
                dirtyField.SetValue(null, dirty);
                forced = true;
            }
            CellIndices ci = map.cellIndices;
            bool any = false;
            for (int z = 0; z < gz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    IntVec3 g = new IntVec3(x, 0, z);
                    IntVec3 s = ProjectZ.CellOnFloor(g, floor, map);
                    if (!s.IsValid)
                    {
                        s = g;
                    }
                    int gi = ci.CellToIndex(g), si = ci.CellToIndex(s);
                    if (!forced && !dirty[si] && !dirty[gi])
                    {
                        continue;
                    }
                    Color c = procCell(s, map);
                    if (!c.Equals(pixels[x, z]))
                    {
                        pixels[x, z] = c;
                        tex.SetPixel(x, z, c);
                        any = true;
                    }
                    dirty[si] = false;
                    dirty[gi] = false;
                }
            }
            if (any)
            {
                tex.Apply(false);
            }
            return tex;
        }
    }
}
