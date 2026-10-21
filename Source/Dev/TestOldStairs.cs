using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode oldstairs (stairs 1x4, user 2026-10-20: no migration, no letter; a save from before must load with no errors).
    // Run with -projectzfloors=3 -projectzseed=7. Two runs:
    //   -projectzoldmake (with the 1x3 build, b043622 + this file): stairs in all four rotations on a cleared area, some
    //     with something on the cell that becomes their solid end (a wall, an item, a torch, a conduit, a colonist; one
    //     upstairs over open air there), saved as pz_oldstairs;
    //   -projectzsave=pz_oldstairs (with the 1x4 build): loaded; per case, what became of the stairs (standing and linked,
    //     refunded by the game's SpawnBuildingAsPossible, standing over what is there), the errors logged by the load;
    //     then a colonist walks up every standing stairs (real goto jobs, real ticks) and deconstructs the one over open
    //     air (a real deconstruct job).
    // The make step uses only code that also exists in the 1x3 build (the solid end is computed here).
    public static partial class TestRunner
    {
        private sealed class OsCase
        {
            public string name;
            public int x, z, level;
            public Rot4 rot;
            public string extra;   // what stands on the future solid end
        }

        private static readonly List<OsCase> osCases = new List<OsCase>
        {
            new OsCase { name = "N clear", x = 3, z = 4, rot = Rot4.North },
            new OsCase { name = "E clear", x = 9, z = 5, rot = Rot4.East },
            new OsCase { name = "S clear", x = 17, z = 7, rot = Rot4.South },
            new OsCase { name = "W clear", x = 25, z = 5, rot = Rot4.West },
            new OsCase { name = "N wall on S", x = 31, z = 4, rot = Rot4.North, extra = "wall" },
            new OsCase { name = "E steel on S", x = 3, z = 18, rot = Rot4.East, extra = "steel" },
            new OsCase { name = "S torch on S", x = 11, z = 20, rot = Rot4.South, extra = "torch" },
            new OsCase { name = "W conduit on S", x = 19, z = 18, rot = Rot4.West, extra = "conduit" },
            new OsCase { name = "N colonist on S", x = 25, z = 16, rot = Rot4.North, extra = "pawn" },
            new OsCase { name = "E upstairs, S over open air", x = 31, z = 18, rot = Rot4.East, level = 1, extra = "air" },
        };

        private static int osErr0;
        private static int osIdx;
        private static string osPawnId;

        private static IntVec3 OsCenter(OsCase c, Map map) => Levels.Cell(RG(c.x, c.z), c.level, map);

        // The cell past the high end (the solid end in the 1x4 build).
        private static IntVec3 OsSolid(OsCase c, Map map) => OsCenter(c, map) + c.rot.FacingCell * 2;

        private static Building_Stairs OsStairs(OsCase c, Map map)
        {
            return OsCenter(c, map).GetThingList(map).OfType<Building_Stairs>().FirstOrDefault(s => s.Rotation == c.rot);
        }

        private static bool StepOldStairs(Map map)
        {
            switch (stage)
            {
                case "os_setup":
                    DebugSettings.enableRandomMentalStates = false;
                    Find.Storyteller.storytellerComps.Clear();
                    Find.PlaySettings.autoRebuild = false;
                    NoRoofCollapse(map);
                    if (GenCommandLine.CommandLineArgPassed("projectzoldmake"))
                    {
                        OsMake(map);
                        return true;
                    }
                    if (!GenCommandLine.TryGetCommandLineArg("projectzsave", out string name))
                    {
                        Result("oldstairs: pass -projectzoldmake or -projectzsave=<name>", false, "");
                        Finish();
                        return true;
                    }
                    osErr0 = errors;
                    stage = "os_loaded";
                    pendingLoad = name;
                    return true;
                case "os_loaded":
                    OsLoaded(map);
                    return true;
                case "os_walk":
                    OsWalk(map);
                    return true;
                case "os_walk_check":
                    OsWalkCheck(map);
                    return true;
                case "os_decon_check":
                    OsDeconCheck(map);
                    return true;
            }
            return false;
        }

        private static void OsArea(Map map)
        {
            r2Area = new CellRect(map.Center.x - 22, map.Center.z - 16, 44, 32);
            List<Pawn> cols = Colonists(map);
            r2P = cols.OrderBy(p => p.thingIDNumber).FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) ?? cols.First();
            r2Q = cols.OrderBy(p => p.thingIDNumber).FirstOrDefault(p => p != r2P);
        }

        private static void OsMake(Map map)
        {
            OsArea(map);
            R2Clear(map);
            R2Park(map);
            foreach (OsCase c in osCases)
            {
                IntVec3 at = OsCenter(c, map);
                if (c.level > 0)
                {
                    // a slab under the ramp only (the cell past it stays open air)
                    IntVec3 f = c.rot.FacingCell;
                    foreach (IntVec3 r in new[] { at - f, at, at + f })
                    {
                        IntVec3 g = Levels.Ground(r, map);
                        R2Floor(map, CellRect.SingleCell(g), c.level);
                    }
                    Place(ThingDefOf.Wall, ThingDefOf.WoodLog, Levels.Cell(Levels.Ground(at, map) - f * 2, 0, map), map);
                }
                AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, at, c.rot, map, stuffDef: ThingDefOf.WoodLog);
                Thing s = Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, at, map, c.rot);
                IntVec3 solid = OsSolid(c, map);
                string put = "nothing";
                switch (c.extra)
                {
                    case "wall":
                        put = Place(ThingDefOf.Wall, ThingDefOf.WoodLog, solid, map)?.ToString();
                        break;
                    case "steel":
                        Thing st = ThingMaker.MakeThing(ThingDefOf.Steel);
                        st.stackCount = 30;
                        put = GenSpawn.Spawn(st, solid, map)?.ToString();
                        break;
                    case "torch":
                        put = Place(DefDatabase<ThingDef>.GetNamed("TorchLamp"), null, solid, map)?.ToString();
                        break;
                    case "conduit":
                        put = Place(ThingDefOf.PowerConduit, null, solid, map)?.ToString();
                        break;
                    case "pawn":
                        Teleport(r2Q, solid);
                        Draft(r2Q, true);
                        osPawnId = r2Q.ThingID;
                        put = r2Q.ToString();
                        break;
                    case "air":
                        put = $"open air ({solid.GetTerrain(map)?.defName})";
                        break;
                }
                Info($"oldstairs make {c.name}: placement {(ok.Accepted ? "accepted" : ok.Reason)}, stairs {s} at {at} {c.rot}, exit {StairShape.Exit(at, c.rot, map)}, past {solid}: {put}");
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            int n = map.listerThings.ThingsOfDef(PZDefOf.PZ_Stairs).Count;
            GameDataSaveLoader.SaveGame("pz_oldstairs");
            Result("oldstairs: the 1x3 save written", n == osCases.Count && System.IO.File.Exists(GenFilePaths.FilePathForSavedGame("pz_oldstairs")), $"{n} stairs; size {PZDefOf.PZ_Stairs.size}");
            Finish();
        }

        private static void OsLoaded(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            OsArea(map);
            int loadErrors = errors - osErr0;
            Result("oldstairs: the 1x3 save loads with no errors", loadErrors == 0, $"{loadErrors} error(s) logged while loading; stairs size now {PZDefOf.PZ_Stairs.size}");
            StairGrid sg = map.GetComponent<StairGrid>();
            foreach (OsCase c in osCases)
            {
                Building_Stairs s = OsStairs(c, map);
                IntVec3 at = OsCenter(c, map);
                IntVec3 solid = OsSolid(c, map);
                string onSolid = string.Join(", ", solid.GetThingList(map).Where(t => t != s).Select(t => t.LabelShort + (t is Pawn ? "" : $" x{t.stackCount}")));
                string around = string.Join(", ", GenRadial.RadialCellsAround(at, 3f, true).Where(x => x.InBounds(map)).SelectMany(x => x.GetThingList(map)).Where(t => t.def == ThingDefOf.WoodLog).Select(t => $"{t.LabelShort} x{t.stackCount} at {t.Position}"));
                string what;
                if (s != null)
                {
                    bool linked = sg != null && sg.IndexOf(s) >= 0;
                    VLink l = linked ? sg.links[sg.IndexOf(s)] : default;
                    what = $"STANDS, linked {linked} (bottom {l.bottom} top {l.top}, exit {s.TopCell}, exit over the solid end {s.TopCell.IsValid && FloorRoof.Below(s.TopCell, map) == solid}), solid walkable {solid.Walkable(map)}, solid terrain {solid.GetTerrain(map)?.defName}, footprint {s.OccupiedRect()}";
                }
                else
                {
                    what = $"GONE (refunded: wood near {(around.Length > 0 ? around : "none")})";
                }
                Info($"oldstairs load {c.name}: {what}; on the solid end: {(onSolid.Length > 0 ? onSolid : "nothing")}");
            }
            Pawn q = osPawnId == null ? null : map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.ThingID == osPawnId);
            OsCase pc = osCases.First(c => c.extra == "pawn");
            IntVec3 ps = OsSolid(pc, map);
            q ??= ps.GetFirstPawn(map) ?? Colonists(map).OrderBy(p => (p.Position - ps).LengthHorizontalSquared).FirstOrDefault();
            Info($"oldstairs load: the colonist saved on a solid end ({ps}): nearest colonist {q} at {q?.Position} (walkable {q?.Position.Walkable(map)}, tick {Find.TickManager.TicksGame})");
            osIdx = 0;
            R2Park(map, r2P);
            R2Feed(map);
            Teleport(r2P, RG(20, 12));
            stage = "os_walk";
        }

        // Up each standing stairs that leads to a floor (real goto jobs), one at a time.
        private static void OsWalk(Map map)
        {
            while (osIdx < osCases.Count)
            {
                OsCase c = osCases[osIdx];
                Building_Stairs s = OsStairs(c, map);
                if (s != null && s.TopCell.IsValid && c.extra != "air")
                {
                    IntVec3 low = s.Position - c.rot.FacingCell * 2;
                    if (Levels.LevelOf(r2P.Position, map) != c.level)
                    {
                        Teleport(r2P, low);
                    }
                    GoTo(r2P, s.TopCell);
                    SeedTicks("os_walk_" + osIdx);
                    TickUntil(3000, () => r2P.Position == s.TopCell, "os_walk_check", 10);
                    return;
                }
                osIdx++;
            }
            OsDecon(map);
        }

        private static void OsWalkCheck(Map map)
        {
            tickWatch = null;
            OsCase c = osCases[osIdx];
            Building_Stairs s = OsStairs(c, map);
            Result($"oldstairs walk {c.name}: a colonist walks up the loaded stairs to the exit", s != null && r2P.Position == s.TopCell, $"at {r2P.Position}, exit {s?.TopCell}");
            Teleport(r2P, RG(20, 12));
            osIdx++;
            stage = "os_walk";
        }

        // The stairs standing over open air (misaligned): removable by a real deconstruct job.
        private static void OsDecon(Map map)
        {
            OsCase c = osCases.First(x => x.extra == "air");
            Building_Stairs s = OsStairs(c, map);
            if (s == null)
            {
                Info("oldstairs decon: the stairs over open air did not load (nothing to take down)");
                Finish();
                return;
            }
            Teleport(r2P, OsCenter(c, map) - c.rot.FacingCell);   // (on its low end: the slab under the ramp is all there is up there)
            Draft(r2P, false);
            R2Work(r2P, WorkTypeDefOf.Construction);
            map.designationManager.AddDesignation(new Designation(s, DesignationDefOf.Deconstruct));
            r2P.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Deconstruct, s), JobTag.Misc);
            SeedTicks("os_decon");
            TickUntil(4000, () => s.Destroyed, "os_decon_check", 30);
        }

        private static void OsDeconCheck(Map map)
        {
            tickWatch = null;
            OsCase c = osCases.First(x => x.extra == "air");
            Result("oldstairs decon: the stairs standing over open air (misaligned) are taken down by a colonist", OsStairs(c, map) == null, $"walker {r2P} at {r2P.Position}, job {r2P.CurJobDef?.defName}");
            Finish();
        }
    }
}
