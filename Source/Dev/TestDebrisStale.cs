using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace ProjectZ.Dev
{
    // Mode debrisstale (run with -projectzfloors=2; the map's seed is fixed, 717 unless -projectzseed): IsoRim's collapse
    // debris when a slab falls while its floor is hidden (tester report 2026-10-19: "Taking the collapse debris failed:
    // ArgumentOutOfRangeException"). IsoRim indexes a block's meshes once per tick; the first cell's collapse dirties the
    // block, the next cell brings the hidden block up to date, and the old index pointed past the new meshes.
    // Each check has its own fixture in its own cleared block (17 x 17, aligned with the game's sections, so the whole slab
    // is in one block on floor 2): a 7 x 7 roof held by one wooden column, a slab on it with granite walls along two edges,
    // one inside and a table. The block is drawn with floor 2 shown first (its meshes up to date, as in play), then the
    // column is killed, a few frames pass (paused, as between two ticks in play) and the game ticks until the roof is down
    // (the game's roof collapse buffer, TickManager.DoSingleTick), each part with its own fixed seed:
    //   S1 floor 2 shown at the collapse: no debris error, IsoRim's mesh index of the tick matches the meshes right after
    //      the collapse, pieces made, the slab and what stood on it gone.
    //   S2 floor 2 hidden (the ground shown) at the collapse: the same (failed before the IsoRim fix).
    // (S1 runs first: the error is logged once per session.)
    public static partial class TestRunner
    {
        private static readonly List<CellRect> dsRects = new List<CellRect>();
        private static int dsCheck, dsErrors, dsErrorsAt, dsWait;

        private static readonly string[] dsNames = { "S1 floor 2 shown", "S2 floor 2 hidden" };

        // (counted from the log hook)
        internal static void DsSawError(string text)
        {
            if (text != null && text.Contains("Taking the collapse debris failed"))
            {
                dsErrors++;
            }
        }

        private static bool StepDebrisStale(Map map)
        {
            switch (stage)
            {
                case "ds_setup":
                {
                    AnnexMapComponent an = ProjectZ.Annex(map);
                    if (an == null || an.floors < 2)
                    {
                        Result("debrisstale setup", false, $"needs 2 floors (-projectzfloors=2); floors {an?.floors}");
                        Finish();
                        return true;
                    }
                    DebugSettings.enableRandomMentalStates = false;
                    dsRects.Clear();
                    dsRects.AddRange(DsBlocks(map, dsNames.Length));
                    if (dsRects.Count < dsNames.Length)
                    {
                        Result("debrisstale fixture space", false, $"{dsRects.Count} blocks found");
                        Finish();
                        return true;
                    }
                    dsCheck = 0;
                    stage = "ds_build";
                    return true;
                }
                case "ds_build":
                {
                    if (dsCheck >= dsNames.Length)
                    {
                        Finish();
                        return true;
                    }
                    CellRect r = dsRects[dsCheck];
                    Rand.PushState(9300 + dsCheck);
                    DsBuild(map, r);
                    Rand.PopState();
                    Info($"{dsNames[dsCheck]}: fixture at {r}, column {r.CenterCell}");
                    ShotAt(map, 1, r.CenterCell, 9f);
                    Find.UIRoot.screenshotMode.Active = true;
                    Wait(40, "ds_drawn");
                    return true;
                }
                case "ds_drawn":
                {
                    CellRect r = dsRects[dsCheck];
                    ZCapture($"debrisstale_s{dsCheck + 1}_before");
                    bool hidden = dsCheck == 1;
                    ShotAt(map, hidden ? 0 : 1, r.CenterCell, 9f, roofs: hidden);
                    Find.UIRoot.screenshotMode.Active = true;
                    Wait(40, "ds_go");
                    return true;
                }
                case "ds_go":
                {
                    CellRect r = dsRects[dsCheck];
                    Section sec = map.mapDrawer.SectionAt(UpS(r.CenterCell, map));
                    Info($"{dsNames[dsCheck]}: shown floor {FloorView.Current}, floor-2 block {sec.botLeft} dirty flags {sec.dirtyFlags} before the kill");
                    IsoRimBridge.DebrisClear();
                    IsoRimBridge.DebrisHold = true;
                    dsErrorsAt = dsErrors;
                    // the column goes (its own seed); frames pass before the next tick drops the roof, as in play (the
                    // drawer brings the blocks the kill touched up to date in between)
                    Rand.PushState(9400 + dsCheck);
                    try
                    {
                        r.CenterCell.GetEdifice(map)?.Kill();
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;   // (no tick until ds_tick)
                    Wait(10, "ds_tick");
                    return true;
                }
                case "ds_tick":
                {
                    CellRect r = dsRects[dsCheck];
                    Section sec = map.mapDrawer.SectionAt(UpS(r.CenterCell, map));
                    Info($"{dsNames[dsCheck]}: column gone, floor-2 block dirty flags {sec.dirtyFlags} before the collapse tick");
                    // the game ticks (TickManager.DoSingleTick, own seed) until the roof is down
                    int ticks = 0;
                    Rand.PushState(9500 + dsCheck);
                    try
                    {
                        while (ticks < 30 && r.Cells.Any(g => map.roofGrid.Roofed(g)))
                        {
                            Find.TickManager.DoSingleTick();
                            ticks++;
                        }
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                    ticksTotal = ticks;
                    // right after the ticks (no frame between: the hidden block is not redrawn): IsoRim's mesh index of this
                    // tick against the meshes as they are now
                    int stale = DsStaleEntries(map, out int entries, out int layers);
                    Result($"debrisstale {dsNames[dsCheck]}: IsoRim's debris index matches the block's meshes", entries > 0 && stale == 0, $"{stale} of {entries} entries in {layers} layer(s) point at a missing triangle or one of another cell");
                    stage = "ds_fell";
                    return true;
                }
                case "ds_fell":
                {
                    CellRect r = dsRects[dsCheck];
                    int slabs = r.Cells.Count(g => Slab.At(UpS(g, map), map));
                    int built = r.Cells.Count(g => UpS(g, map).GetFirstBuilding(map) != null);
                    int roofs = r.Cells.Count(g => map.roofGrid.Roofed(g));
                    Result($"debrisstale {dsNames[dsCheck]}: the roof, the slab and what stood on it fell", slabs == 0 && built == 0 && roofs == 0, $"after {ticksTotal} ticks: slab cells {slabs}, buildings upstairs {built}, roofs {roofs}");
                    dsWait = 0;
                    Wait(3, "ds_pieces");
                    return true;
                }
                case "ds_pieces":
                {
                    if (IsoRimBridge.DebrisPending > 0 && dsWait++ < 600)
                    {
                        return true;
                    }
                    int errs = dsErrors - dsErrorsAt;
                    Result($"debrisstale {dsNames[dsCheck]}: no debris error", errs == 0, $"{errs} 'Taking the collapse debris failed' error(s)");
                    Result($"debrisstale {dsNames[dsCheck]}: pieces made", IsoRimBridge.DebrisLive > 0, $"{IsoRimBridge.DebrisLive} pieces; {IsoRimBridge.DebrisStats}");
                    IsoRimBridge.DebrisAdvance(0.35f);
                    Wait(3, "ds_after");
                    return true;
                }
                case "ds_after":
                    ZCapture($"debrisstale_s{dsCheck + 1}_after");
                    IsoRimBridge.DebrisAdvance(5f);
                    dsCheck++;
                    Wait(5, "ds_build");
                    return true;
            }
            return false;
        }

        // IsoRim.CollapseDebris.index (this tick): cell -> (submesh, triangle) per layer. An entry is stale when the triangle
        // is gone or lies in another cell (read as IsoRim's Index reads it: the cut tag's owner cell, else the middle).
        private static int DsStaleEntries(Map map, out int entries, out int layers)
        {
            entries = 0;
            layers = 0;
            int stale = 0;
            System.Type type = HarmonyLib.AccessTools.TypeByName("IsoRim.CollapseDebris");
            Dictionary<SectionLayer, Dictionary<int, List<long>>> index = type == null ? null : HarmonyLib.AccessTools.Field(type, "index")?.GetValue(null) as Dictionary<SectionLayer, Dictionary<int, List<long>>>;
            if (index == null)
            {
                Info("debrisstale: IsoRim.CollapseDebris.index not found");
                return -1;
            }
            int w = map.Size.x;
            foreach (KeyValuePair<SectionLayer, Dictionary<int, List<long>>> kv in index)
            {
                layers++;
                List<LayerSubMesh> subs = kv.Key.subMeshes;
                foreach (KeyValuePair<int, List<long>> cell in kv.Value)
                {
                    foreach (long e in cell.Value)
                    {
                        entries++;
                        int i = (int)(e >> 32);
                        int k = (int)(e & 0xffffffff);
                        if (i >= subs.Count || k + 2 >= subs[i].tris.Count)
                        {
                            stale++;
                            continue;
                        }
                        LayerSubMesh sm = subs[i];
                        List<UnityEngine.Vector3> v = sm.verts;
                        List<int> t = sm.tris;
                        List<UnityEngine.Vector3> tag = sm.uvsChannelTwo.Count == v.Count ? sm.uvsChannelTwo : null;
                        int flags = tag != null ? (int)(tag[t[k]].z + 0.5f) : 0;
                        int cx, cz;
                        if (flags != 0)
                        {
                            cx = UnityEngine.Mathf.FloorToInt(tag[t[k]].x);
                            cz = UnityEngine.Mathf.FloorToInt(tag[t[k]].y);
                        }
                        else
                        {
                            cx = UnityEngine.Mathf.FloorToInt((v[t[k]].x + v[t[k + 1]].x + v[t[k + 2]].x) / 3f);
                            cz = UnityEngine.Mathf.FloorToInt((v[t[k]].z + v[t[k + 1]].z + v[t[k + 2]].z) / 3f);
                        }
                        if (cz * w + cx != cell.Key)
                        {
                            stale++;
                        }
                    }
                }
            }
            return stale;
        }

        // Ground blocks aligned with the sections (the floor-2 rows of a block are one section too: the stride is a
        // multiple of 17), the fewest cells to fix first; two blocks apart from each other.
        private static List<CellRect> DsBlocks(Map map, int want)
        {
            int gz = ProjectZ.Annex(map).groundZ;
            List<KeyValuePair<CellRect, int>> all = new List<KeyValuePair<CellRect, int>>();
            for (int z = 17; z + 34 <= gz; z += 17)
            {
                for (int x = 17; x + 34 <= map.Size.x; x += 17)
                {
                    CellRect block = new CellRect(x, z, 17, 17);
                    if (block.ExpandedBy(1).Cells.Any(c => c.GetThingList(map).Any(t => !t.def.destroyable)))
                    {
                        continue;
                    }
                    int bad = block.Cells.Count(c => c.GetTerrain(map).IsWater || c.GetTerrain(map).passability == Traversability.Impassable || map.roofGrid.Roofed(c) || c.GetFirstBuilding(map) != null || map.fogGrid.IsFogged(c));
                    all.Add(new KeyValuePair<CellRect, int>(block, bad));
                }
            }
            List<CellRect> picked = new List<CellRect>();
            foreach (KeyValuePair<CellRect, int> kv in all.OrderBy(k => k.Value).ThenBy(k => k.Key.minZ).ThenBy(k => k.Key.minX))
            {
                if (picked.Count >= want)
                {
                    break;
                }
                if (picked.Any(p => p.ExpandedBy(17).Overlaps(kv.Key)))
                {
                    continue;
                }
                picked.Add(kv.Key);
            }
            return picked.Select(b => new CellRect(b.minX + 5, b.minZ + 5, 7, 7)).ToList();
        }

        private static void DsBuild(Map map, CellRect r)
        {
            // the block and a margin (no other roof support within reach): bare soil, no roof, nothing on it, floor 2 open air
            foreach (IntVec3 c in r.ExpandedBy(7))
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                foreach (IntVec3 cc in new[] { c, UpS(c, map) })
                {
                    foreach (Thing t in cc.GetThingList(map).ToList())
                    {
                        if (t is Pawn p)
                        {
                            if (p.Faction == Faction.OfPlayer)
                            {
                                Teleport(p, new IntVec3(r.CenterCell.x, 0, r.minZ - 10));
                            }
                            else
                            {
                                p.Destroy();
                            }
                        }
                        else if (t.def.destroyable)
                        {
                            t.Destroy();
                        }
                    }
                    map.fogGrid.Unfog(cc);
                }
                TerrainDef td = c.GetTerrain(map);
                if (td.IsWater || td.passability == Traversability.Impassable)
                {
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
                map.roofGrid.SetRoof(c, null);
                map.terrainGrid.SetTerrain(UpS(c, map), PZDefOf.PZ_OpenAir);
            }
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, r.CenterCell, map);
            foreach (IntVec3 g in r)
            {
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(UpS(g, map), PZDefOf.PZ_UpperFloor);
            }
            foreach (IntVec3 g in r)
            {
                if (g.z == r.maxZ || g.x == r.minX)
                {
                    Place(ThingDefOf.Wall, ThingDefOf.BlocksGranite, UpS(g, map), map);
                }
            }
            Place(DefDatabase<ThingDef>.GetNamed("Table2x2c"), ThingDefOf.WoodLog, UpS(r.CenterCell + new IntVec3(1, 0, -1), map), map);
            Place(ThingDefOf.Wall, ThingDefOf.BlocksGranite, UpS(r.CenterCell + new IntVec3(-1, 0, 1), map), map);
        }
    }
}
