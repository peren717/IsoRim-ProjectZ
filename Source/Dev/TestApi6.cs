using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ProjectZ.Combat;
using RimWorld;
using UnityEngine;
using Verse;

namespace ProjectZ.Dev
{
    // ProjectZ Api 6 (Dev/TestApi6.cs).
    //   mode parts: the combat part switch (CombatGate): every part's state and every [CombatPartOf] class / part entry
    //     applied exactly when its part is on, the rule parts' values, and with a foreign combat system the deferral
    //     (a test-only declaration made from ProjectZ's constructor is patched after startup). Test-only declarations
    //     (dev code, not shipped behaviour), from TestRunner.Init (inside ProjectZ's Mod constructor, after its patching):
    //       -projectztestkeep             KeepCombatParts(All & ~(RangeHeightBonus | HeightAccuracy)) (the CE pack's set)
    //       -projectztestkeep=<A,B>       KeepCombatParts(A | B)
    //       -projectztestrelease=<A,B>    ReleaseCombatParts(A | B) (undoes their patches, landed already)
    //     Run with and without Combat Extended. No map work but a few real ticks.
    //   mode api6: the 3D space queries on a two-floor fixture (slab, stairwell of real stairs, roof only, open air, wall),
    //     run with -projectzfloors=3 -projectzseed=N.
    public static partial class TestRunner
    {
        private static CombatPart p6Keep, p6Release;
        private static bool p6KeepAsked, p6CanHitAtCtor, p6ForeignAtCtor;
        private static int p6Ticked;

        // From Init (ProjectZ's constructor, after its own patching).
        private static void Api6Init()
        {
            p6CanHitAtCtor = FailSoft.IsApplied(nameof(Verb_CanHitCell_Patch));
            p6ForeignAtCtor = CombatGate.Foreign;
            if (GenCommandLine.TryGetCommandLineArg("projectztestkeep", out string k) || GenCommandLine.CommandLineArgPassed("projectztestkeep"))
            {
                p6KeepAsked = true;
                p6Keep = string.IsNullOrEmpty(k) ? CombatPart.All & ~(CombatPart.RangeHeightBonus | CombatPart.HeightAccuracy) : Parts(k);
                Api.KeepCombatParts(p6Keep, "ProjectZ test");
            }
            if (GenCommandLine.TryGetCommandLineArg("projectztestrelease", out string r) && !string.IsNullOrEmpty(r))
            {
                p6Release = Parts(r);
                Api.ReleaseCombatParts(p6Release, "ProjectZ test");
            }
        }

        private static CombatPart Parts(string s)
        {
            CombatPart p = CombatPart.None;
            foreach (string n in s.Split(',', '|', '+'))
            {
                if (Enum.TryParse(n.Trim(), true, out CombatPart one))
                {
                    p |= one;
                }
            }
            return p;
        }

        private static bool StepApi6(Map map)
        {
            switch (stage)
            {
                case "p6_parts": P6Parts(map); return true;
                case "p6_parts_ticks": P6PartsTicks(map); return true;
                case "p6_setup": P6Setup(map); return true;
                case "p6_check": P6Check(map); return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------------ parts

        private static bool P6Expected(CombatPart b, bool foreign)
        {
            if ((p6Release & b) != 0)
            {
                return false;
            }
            return (CombatGate.ForeignDefault & b) != 0 || !foreign || (p6Keep & b) != 0;
        }

        private static void P6Parts(Map map)
        {
            bool foreign = CombatGate.Foreign;
            Info($"parts: foreign combat system {foreign}, kept {(p6KeepAsked ? p6Keep.ToString() : "none")}, released {p6Release}; Api.Version {Api.Version}");
            Result("parts: Api.Version 6, ForeignCombatSystem as loaded", Api.Version == 6 && Api.ForeignCombatSystem == foreign && foreign == ModsConfig.ActiveModsInLoadOrder.Any(m => m.PackageIdPlayerFacing.ToLowerInvariant().Contains("combatextended")),
                $"version {Api.Version}, foreign {Api.ForeignCombatSystem}");
            // P1 every part as the rule says
            List<string> wrong = new List<string>();
            List<string> on = new List<string>();
            for (long b = 1; b <= (long)CombatPart.All; b <<= 1)
            {
                CombatPart p = (CombatPart)b;
                bool exp = P6Expected(p, foreign);
                if (Api.CombatPartOn(p))
                {
                    on.Add(p.ToString());
                }
                if (Api.CombatPartOn(p) != exp)
                {
                    wrong.Add($"{p} {(exp ? "should be on" : "should be off")}");
                }
            }
            Result("parts P1: each part on exactly as declared (foreign default: Explosions, Shields, SkyStrikes)", wrong.Count == 0,
                $"on [{string.Join(", ", on)}]" + (wrong.Count > 0 ? "; wrong: " + string.Join("; ", wrong) : ""));
            // P2 every tagged class: applied (and its patch methods on some method) exactly when its part is on
            List<string> bad = new List<string>();
            int classes = 0, appliedN = 0;
            HashSet<Type> owners = new HashSet<Type>();
            foreach (MethodBase m in Harmony.GetAllPatchedMethods())
            {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(m);
                foreach (Patch pt in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
                {
                    if (pt.owner == "Peren.IsoRimProjectZ" && pt.PatchMethod?.DeclaringType != null)
                    {
                        owners.Add(pt.PatchMethod.DeclaringType);
                    }
                }
            }
            foreach (Type t in typeof(Api).Assembly.GetTypes())
            {
                CombatPartOfAttribute cp = t.GetCustomAttribute<CombatPartOfAttribute>();
                if (cp == null)
                {
                    continue;
                }
                classes++;
                bool exp = P6Expected(cp.part, foreign);
                bool ap = FailSoft.IsApplied(t.Name);
                bool live = owners.Contains(t);
                if (ap)
                {
                    appliedN++;
                }
                if (ap != exp || live != exp)
                {
                    bad.Add($"{t.Name} ({cp.part}) applied {ap}, patches live {live}, expected {exp}");
                }
                if ((p6Release & cp.part) != 0 && !foreign && !FailSoft.Released.Contains(t.Name))
                {
                    bad.Add($"{t.Name} released but not listed as released (undone)");
                }
            }
            Result("parts P2: every [CombatPartOf] patch class applied and live exactly when its part is on", bad.Count == 0 && classes >= 40,
                $"{classes} classes, {appliedN} applied" + (bad.Count > 0 ? "; " + string.Join("; ", bad) : ""));
            // P3 AimFloor's list entries (combat part entries)
            int rows = AimFloor.Rows.Length, rowsOn = AimFloor.Rows.Count(r => FailSoft.IsApplied(r.Entry));
            bool aimExp = P6Expected(CombatPart.AimFloor, foreign);
            Result("parts P3: AimFloor's entries applied exactly when AimFloor is on", aimExp ? rowsOn >= rows - 1 : rowsOn == 0, $"{rowsOn} of {rows} rows applied, expected {(aimExp ? "on" : "off")}");
            // P4 the rule parts read at run time
            float bonus = Range3D.BonusPerFloor, acc = CombatGate.HeightBonus;
            bool rbExp = P6Expected(CombatPart.RangeHeightBonus, foreign), haExp = P6Expected(CombatPart.HeightAccuracy, foreign);
            Result("parts P4: range bonus per floor and height accuracy follow their rule parts", bonus == (rbExp ? 1f : 0f) && (haExp ? acc == ProjectZMod.Settings.heightAccuracy : acc == 0f)
                && Mathf.Abs(Range3D.ShellRadius(20f, -1f) - (rbExp ? 20f / (1f - 1f / Range3D.Storey) : 20f)) < 1e-3f,
                $"BonusPerFloor {bonus}, HeightBonus {acc} (setting {ProjectZMod.Settings.heightAccuracy}), shell radius 1 cell down {Range3D.ShellRadius(20f, -1f):0.00}");
            // P5 with a foreign system: the gated classes were not applied in the constructor, and are now when kept
            if (foreign)
            {
                bool keptSight = (p6Keep & CombatPart.Sight) != 0 && (p6Release & CombatPart.Sight) == 0;
                Result("parts P5: with a foreign combat system the combat group is patched after the constructors (deferred)",
                    p6ForeignAtCtor && !p6CanHitAtCtor && FailSoft.IsApplied(nameof(Verb_CanHitCell_Patch)) == keptSight && CombatGate.Settled,
                    $"Verb_CanHitCell_Patch applied in the constructor {p6CanHitAtCtor}, now {FailSoft.IsApplied(nameof(Verb_CanHitCell_Patch))} (kept: {keptSight}), settled {CombatGate.Settled}");
            }
            else
            {
                bool expSight = (p6Release & CombatPart.Sight) == 0;
                Result("parts P5: without a foreign combat system the combat group is patched in the constructor, as before",
                    !p6ForeignAtCtor && p6CanHitAtCtor == true && FailSoft.IsApplied(nameof(Verb_CanHitCell_Patch)) == expSight,
                    $"Verb_CanHitCell_Patch applied in the constructor {p6CanHitAtCtor}, now {FailSoft.IsApplied(nameof(Verb_CanHitCell_Patch))}");
            }
            Result("parts P6: no combat patch failed, nothing undone by a closed group", !FailSoft.Failed.Any(f => !f.Contains(":")) && FailSoft.Undone.Count == 0,
                $"failed [{string.Join(", ", FailSoft.Failed)}], undone [{string.Join(", ", FailSoft.Undone)}], released [{string.Join(", ", FailSoft.Released)}]");
            int t0 = Find.TickManager.TicksGame;
            for (int i = 0; i < 120; i++)
            {
                Find.TickManager.DoSingleTick();
            }
            p6Ticked = Find.TickManager.TicksGame - t0;
            Wait(30, "p6_parts_ticks");
        }

        private static void P6PartsTicks(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Result("parts: the game ticks on with the parts as set (120 real ticks, no error)", p6Ticked == 120 && errors == 0, $"ticked {p6Ticked}, errors {errors}");
            Finish();
        }

        // ------------------------------------------------------------------------------------------------ api6 fixture

        private static CellRect p6Field;
        private static IntVec3 p6Stairwell = IntVec3.Invalid;

        private static IntVec3 PG(int x, int z) => new IntVec3(p6Field.minX + x, 0, p6Field.minZ + z);

        private static IntVec3 PU(int x, int z, int k = 1) => Levels.Cell(PG(x, z), k, lMap);

        // Field coordinates (20 x 14): slab on floor 1 over x 2..7, z 2..7 (roof under it); stairs (1x3/1x4, north) at
        // x 11, z 2 with their stairwell opening; roof only (no floor above) at x 14..15, z 2..3; a ground wall at 17, 5;
        // open air everywhere else (x 17, z 10 checked).
        private static void P6Setup(Map map)
        {
            lMap = map;
            DebugSettings.enableRandomMentalStates = false;
            NoRoofCollapse(map);
            Find.Storyteller.storytellerComps.Clear();
            AnnexMapComponent an = ProjectZ.Annex(map);
            if (an == null || an.MaxLevel < 2)
            {
                Result("api6: a map with 3 levels", false, "run with -projectzfloors=3");
                Finish();
                return;
            }
            int z0 = Mathf.Clamp(an.groundZ / 2 - 7, 3, an.groundZ - 17);
            p6Field = new CellRect(map.Size.x / 2 - 10, z0, 20, 14);
            Seeded("api6_fixture", () =>
            {
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(p => p6Field.ExpandedBy(3).Contains(ProjectZ.GroundCellOf(p.Position, map))).ToList())
                {
                    if (p.IsColonist)
                    {
                        Teleport(p, CellFinder.RandomClosewalkCellNear(new IntVec3(map.Size.x / 2, 0, Mathf.Min(an.groundZ - 3, p6Field.maxZ + 12)), map, 4));
                    }
                    else
                    {
                        p.Destroy();
                    }
                }
                for (int k = an.MaxLevel; k >= 0; k--)
                {
                    foreach (IntVec3 g in p6Field.ExpandedBy(2))
                    {
                        IntVec3 c = Levels.Cell(g, k, map);
                        if (!c.IsValid || !c.InBounds(map))
                        {
                            continue;
                        }
                        foreach (Thing t in c.GetThingList(map).ToList())
                        {
                            if (!(t is Pawn) && t.def.destroyable && !t.Destroyed)
                            {
                                t.Destroy();
                            }
                        }
                        map.terrainGrid.SetTerrain(c, k > 0 ? PZDefOf.PZ_OpenAir : TerrainDefOf.Soil);
                        map.roofGrid.SetRoof(c, null);
                        map.fogGrid.Unfog(c);
                    }
                }
                map.roofCollapseBuffer.Clear();
                for (int x = 2; x <= 7; x++)
                {
                    for (int z = 2; z <= 7; z++)
                    {
                        map.roofGrid.SetRoof(PG(x, z), RoofDefOf.RoofConstructed);
                        map.terrainGrid.SetTerrain(PU(x, z), PZDefOf.PZ_UpperFloor);
                    }
                }
                for (int x = 14; x <= 15; x++)
                {
                    for (int z = 2; z <= 3; z++)
                    {
                        map.roofGrid.SetRoof(PG(x, z), RoofDefOf.RoofConstructed);
                    }
                }
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, PG(17, 5), map);
                // real stairs, roofed over as a builder would: their cells get the stairwell opening (Stairwell.RoofFor)
                Thing stairs = Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, PG(11, 2), map, Rot4.North);
                foreach (IntVec3 c in stairs.OccupiedRect())
                {
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                }
            });
            p6Stairwell = p6Field.Cells.FirstOrDefault(g => map.roofGrid.RoofAt(g) == PZDefOf.PZ_StairwellRoof);
            if (!p6Stairwell.IsValid || p6Stairwell == default(IntVec3))
            {
                p6Stairwell = IntVec3.Invalid;
            }
            Info($"api6 field {p6Field}, stride {an.stride}, groundZ {an.groundZ}, storey {Api.StoreyHeight:0.000}, wall {Api.WallHeight:0.000}, stairwell opening over {p6Stairwell}");
            for (int i = 0; i < 60; i++)
            {
                Find.TickManager.DoSingleTick();   // real ticks: nothing falls, nothing collapses
            }
            Wait(10, "p6_check");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int P6RadialProbe(IntVec3 centre, float radius, Map map)
        {
            int off = 0;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(centre, radius, true))
            {
                if (c.InBounds(map) && !ProjectZ.SameBand(c, centre, map))
                {
                    off++;
                }
            }
            return off;
        }

        private static bool Near(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-6f;

        private static void P6Check(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            AnnexMapComponent an = ProjectZ.Annex(map);
            float S = Api.StoreyHeight;
            IntVec3 slab = PG(4, 4), roofOnly = PG(14, 2), wall = PG(17, 5), open = PG(17, 10);
            Result("api6 fixture: slab, roof only and stairwell opening stand after the ticks", Api.HasFloorAt(slab, 1, map) && map.roofGrid.Roofed(roofOnly) && p6Stairwell.IsValid,
                $"slab floor {Api.HasFloorAt(slab, 1, map)}, roof only {map.roofGrid.RoofAt(roofOnly)?.defName}, stairwell {p6Stairwell}");
            IntVec3 well = p6Stairwell.IsValid ? p6Stairwell : PG(11, 3);

            // ---- frame helpers
            Result("api6 F1 FloorRowShift: 0, stride, 2 x stride; 0 for a floor the map lacks", Api.FloorRowShift(0, map) == 0 && Api.FloorRowShift(1, map) == PU(4, 4).z - slab.z
                && Api.FloorRowShift(2, map) == PU(4, 4, 2).z - slab.z && Api.FloorRowShift(7, map) == 0 && Api.FloorRowShift(1, null) == 0,
                $"{Api.FloorRowShift(0, map)}, {Api.FloorRowShift(1, map)}, {Api.FloorRowShift(2, map)}, floor 7 {Api.FloorRowShift(7, map)} (stride {an.stride})");
            Result("api6 F2 WallHeight is IsoRim's locked wall, StoreyHeight 0.02 more", Mathf.Approximately(Api.WallHeight, IsoRimBridge.WallHeight) && Mathf.Abs(S - Api.WallHeight - 0.02f) < 1e-4f,
                $"wall {Api.WallHeight:0.000}, storey {S:0.000}");
            Vector3 mp = PU(4, 4).ToVector3Shifted();
            bool tv = Api.ToVirtual(mp, 0.4f, map, out Vector3 v);
            bool fv = Api.FromVirtual(v, map, out Vector3 back, out int fl, out float hof);
            Result("api6 F3 ToVirtual / FromVirtual round trip on floor 1", tv && fv && Near(v, new Vector3(slab.x + 0.5f, S + 0.4f, slab.z + 0.5f)) && fl == 1 && Mathf.Abs(hof - 0.4f) < 1e-4f
                && Mathf.Abs(back.x - mp.x) < 1e-4f && Mathf.Abs(back.z - mp.z) < 1e-4f && Mathf.Abs(back.y - 0.4f) < 1e-4f,
                $"v {v.x:0.00},{v.y:0.000},{v.z:0.00}; back {back.x:0.00},{back.y:0.000},{back.z:0.00} floor {fl} height {hof:0.000}");
            Vector3 voidPos = new Vector3(slab.x + 0.5f, 0f, an.groundZ + 0.5f);
            bool vVoid = Api.ToVirtual(voidPos, 0f, map, out _);
            bool vTop = Api.FromVirtual(new Vector3(slab.x + 0.5f, 10f * S, slab.z + 0.5f), map, out Vector3 topPos, out int topFl, out float topH);
            bool vOut = Api.FromVirtual(new Vector3(-1f, 0.5f, slab.z), map, out _, out _, out _);
            bool vNorth = Api.FromVirtual(new Vector3(slab.x, 0.5f, an.groundZ + 0.2f), map, out _, out _, out _);
            bool vLow = Api.FromVirtual(new Vector3(slab.x + 0.5f, -1f, slab.z + 0.5f), map, out _, out int lowFl, out float lowH);
            Result("api6 F4 edges: void row -> false; above the top floor -> top floor; off the ground rect -> false; below -> lowest floor",
                !vVoid && vTop && topFl == an.MaxLevel && Mathf.Abs(topH - (10f - an.MaxLevel) * S) < 1e-3f && Mathf.Abs(topPos.z - (slab.z + 0.5f + Api.FloorRowShift(an.MaxLevel, map))) < 1e-4f
                && !vOut && !vNorth && vLow && lowFl == an.MinLevel && Mathf.Abs(lowH + 1f - an.MinLevel * S) < 1e-4f,
                $"void {vVoid}; top floor {topFl} height {topH:0.00}; off x {vOut}; off north {vNorth}; below floor {lowFl} height {lowH:0.00}");

            // ---- column queries
            Result("api6 Q1 HasFloorAt: slab yes, stairwell, roof only, open no; ground always; a floor the map lacks no",
                Api.HasFloorAt(slab, 1, map) && !Api.HasFloorAt(well, 1, map) && !Api.HasFloorAt(roofOnly, 1, map) && !Api.HasFloorAt(open, 1, map)
                && Api.HasFloorAt(open, 0, map) && !Api.HasFloorAt(slab, 2, map) && !Api.HasFloorAt(slab, 7, map) && !Api.HasFloorAt(PU(4, 4), 1, map),
                $"slab {Api.HasFloorAt(slab, 1, map)}, well {Api.HasFloorAt(well, 1, map)}, roof {Api.HasFloorAt(roofOnly, 1, map)}, open {Api.HasFloorAt(open, 1, map)}, ground {Api.HasFloorAt(open, 0, map)}, floor 2 {Api.HasFloorAt(slab, 2, map)}");
            Result("api6 Q2 PlaneSolid(g, 1): slab and roof yes, stairwell and open air no; plane 2 and the top roof open here",
                Api.PlaneSolid(slab, 1, map) && Api.PlaneSolid(roofOnly, 1, map) && !Api.PlaneSolid(well, 1, map) && !Api.PlaneSolid(open, 1, map)
                && !Api.PlaneSolid(slab, 2, map) && !Api.PlaneSolid(slab, an.MaxLevel + 1, map),
                $"slab {Api.PlaneSolid(slab, 1, map)}, roof {Api.PlaneSolid(roofOnly, 1, map)}, well {Api.PlaneSolid(well, 1, map)}, open {Api.PlaneSolid(open, 1, map)}, plane 2 {Api.PlaneSolid(slab, 2, map)}, top {Api.PlaneSolid(slab, an.MaxLevel + 1, map)}");
            Result("api6 Q3 CeilingSolid: under the slab and the roof yes; the stairwell opening, open air and on the slab no",
                Api.CeilingSolid(slab, map) && Api.CeilingSolid(roofOnly, map) && !Api.CeilingSolid(well, map) && !Api.CeilingSolid(open, map) && !Api.CeilingSolid(PU(4, 4), map),
                $"slab {Api.CeilingSolid(slab, map)}, roof {Api.CeilingSolid(roofOnly, map)}, well {Api.CeilingSolid(well, map)} ({map.roofGrid.RoofAt(well)?.defName}), open {Api.CeilingSolid(open, map)}");
            Result("api6 Q4 SightBlockerAt: the wall on the ground only", Api.SightBlockerAt(wall, 0, map) && !Api.SightBlockerAt(wall, 1, map) && !Api.SightBlockerAt(open, 0, map) && !Api.SightBlockerAt(slab, 1, map),
                $"wall 0 {Api.SightBlockerAt(wall, 0, map)}, wall 1 {Api.SightBlockerAt(wall, 1, map)}, open {Api.SightBlockerAt(open, 0, map)}");
            IntVec3 voidCell = new IntVec3(slab.x, 0, an.groundZ);
            Result("api6 Q5 ColumnTop: the slab's cell, else the ground; Invalid for a void row", Api.ColumnTop(slab, map) == PU(4, 4) && Api.ColumnTop(PU(4, 4, 2), map) == PU(4, 4)
                && Api.ColumnTop(open, map) == open && Api.ColumnTop(roofOnly, map) == roofOnly && !Api.ColumnTop(voidCell, map).IsValid,
                $"slab {Api.ColumnTop(slab, map)} (floor-1 cell {PU(4, 4)}), open {Api.ColumnTop(open, map)}, roof {Api.ColumnTop(roofOnly, map)}, void {Api.ColumnTop(voidCell, map)}");

            // ---- LineClear3D
            IntVec3 wellUp = Levels.Cell(well, 1, map);
            Result("api6 L1 LineClear3D straight down: through the stairwell opening and open air clear; slab and roof blocked",
                Api.LineClear3D(wellUp, 0.5f, well, 0.5f, map) && Api.LineClear3D(PU(17, 10), 0.5f, open, 0.5f, map)
                && !Api.LineClear3D(PU(4, 4), 0.5f, slab, 0.5f, map) && !Api.LineClear3D(PU(14, 2), 0.5f, roofOnly, 0.5f, map),
                $"well {Api.LineClear3D(wellUp, 0.5f, well, 0.5f, map)}, open {Api.LineClear3D(PU(17, 10), 0.5f, open, 0.5f, map)}, slab {Api.LineClear3D(PU(4, 4), 0.5f, slab, 0.5f, map)}, roof {Api.LineClear3D(PU(14, 2), 0.5f, roofOnly, 0.5f, map)}");
            Result("api6 L2 LineClear3D on the ground: the wall blocks, open ground is clear (no floors needed)",
                !Api.LineClear3D(PG(17, 2), 1.1f, PG(17, 8), 0.8f, map) && Api.LineClear3D(PG(18, 2), 1.1f, PG(18, 8), 0.8f, map),
                $"past the wall {Api.LineClear3D(PG(17, 2), 1.1f, PG(17, 8), 0.8f, map)}, beside it {Api.LineClear3D(PG(18, 2), 1.1f, PG(18, 8), 0.8f, map)}");
            // the same blocks as CanSeeBetweenFloors: any of its three aims clear <=> it sees
            int pairs = 0;
            List<string> diff = new List<string>();
            for (int i = 0; i < 20; i += 3)
            {
                for (int j = 0; j < 14; j += 3)
                {
                    foreach ((IntVec3 a, IntVec3 b) in new[] { (PU(i, j), PG(19 - i, 13 - j)), (PG(i, j), PU(19 - i, 13 - j)), (PU(4, 4), PG(i, j)), (PG(4, 4), PU(i, j)) })
                    {
                        if (!a.IsValid || !b.IsValid)
                        {
                            continue;
                        }
                        pairs++;
                        bool any = Api.LineClear3D(a, Los3D.Eye, b, Los3D.AimHead, map) || Api.LineClear3D(a, Los3D.Eye, b, Los3D.AimChest, map) || Api.LineClear3D(a, Los3D.Eye, b, Los3D.AimWaist, map);
                        bool sees = Api.CanSeeBetweenFloors(a, b, map);
                        if (any != sees)
                        {
                            diff.Add($"{MCell(a, map)} -> {MCell(b, map)}: line {any}, sees {sees}");
                        }
                    }
                }
            }
            Result("api6 L3 LineClear3D at the eye and the three aims agrees with CanSeeBetweenFloors", diff.Count == 0 && pairs > 50, $"{pairs} pairs" + (diff.Count > 0 ? "; " + string.Join("; ", diff.Take(6)) : ""));

            // ---- WalkLine3D
            List<Api.LineStep> steps = new List<Api.LineStep>(128);
            Vector3 V(IntVec3 g, float y) => new Vector3(g.x + 0.5f, y, g.z + 0.5f);
            int nSlab = Api.WalkLine3D(V(slab, S + 0.5f), V(slab, 0.5f), map, steps);
            Api.LineStep sSlab = nSlab > 0 ? steps[0] : default;
            int nWell = Api.WalkLine3D(V(well, S + 0.5f), V(well, 0.5f), map, steps);
            Api.LineStep sWell = nWell > 0 ? steps[0] : default;
            Result("api6 W1 WalkLine3D straight down: one step; the slab's plane 1 found, none at the stairwell",
                nSlab == 1 && sSlab.solidPlane == 1 && sSlab.cell == slab && nWell == 1 && sWell.solidPlane == int.MinValue,
                $"slab: {nSlab} step, plane {sSlab.solidPlane}, cell {sSlab.cell} floor {sSlab.floor} {sSlab.lo:0.00}..{sSlab.hi:0.00}; well: {nWell}, plane {(sWell.solidPlane == int.MinValue ? "none" : sWell.solidPlane.ToString())}");
            // from floor 1 over open air (x 9, z 10) down to the ground under the slab (x 3, z 3): it crosses plane 1 over the slab
            Vector3 from = V(PG(9, 10), S + 1.5f), to = V(PG(3, 3), 0.3f);
            int nFull = Api.WalkLine3D(from, to, map, steps, false);
            int expected = 1 + 6 + 7;
            int firstPlane = steps.FindIndex(s => s.solidPlane != int.MinValue);
            List<Api.LineStep> fullCopy = steps.ToList();
            int nStop = Api.WalkLine3D(from, to, map, steps, true);
            bool cellsOk = fullCopy.All(s => s.cell.IsValid && ProjectZ.FloorOf(s.cell, map) == s.floor && s.lo <= s.hi);
            bool floorsOk = fullCopy.Count > 0 && fullCopy[0].floor == 1 && fullCopy[fullCopy.Count - 1].floor == 0;
            Result("api6 W2 WalkLine3D down a slope: the game's walk (1 + dx + dz columns), floor 1 to the ground; stops at the slab's plane when asked",
                nFull == expected && cellsOk && floorsOk && firstPlane >= 0 && fullCopy[firstPlane].solidPlane == 1 && nStop == firstPlane + 1
                && Api.HasFloorAt(ProjectZ.GroundCellOf(fullCopy[firstPlane].cell, map), 1, map),
                $"{nFull} steps (expected {expected}), first plane at step {firstPlane} ({(firstPlane >= 0 ? MCell(fullCopy[firstPlane].cell, map) : "-")}), stopped walk {nStop} steps; floors {string.Join("", fullCopy.Select(s => s.floor))}");
            // no allocation
            for (int i = 0; i < 20; i++)
            {
                Api.WalkLine3D(from, to, map, steps, false);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 500; i++)
            {
                Api.WalkLine3D(from, to, map, steps, i % 2 == 0);
            }
            long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
            Result("api6 W3 WalkLine3D allocates nothing (500 walks into one list)", alloc == 0, $"{alloc} bytes");
            int nOff = Api.WalkLine3D(V(PG(4, 4), 0.5f), new Vector3(slab.x, 0.5f, an.groundZ + 1.5f), map, steps);
            Result("api6 W4 WalkLine3D with an end off the ground rows: 0 steps", nOff == 0 && steps.Count == 0, $"{nOff}");
            P6Corners(map, S, steps);

            // ---- ExplosionCells3D
            IntVec3 centre = PU(7, 4);   // on the slab's east edge
            float r = 3.9f;
            List<IntVec3> game = DamageDefOf.Bomb.Worker.ExplosionCellsToHit(centre, map, r).ToList();
            List<IntVec3> mine = new List<IntVec3>();
            Api.ExplosionCells3D(centre, map, r, game, mine);
            HashSet<IntVec3> gs = new HashSet<IntVec3>(game), ms = new HashSet<IntVec3>(mine);
            int underSlab = mine.Count(c => ProjectZ.FloorOf(c, map) == 0 && Api.HasFloorAt(c, 1, map));
            int pastEdge = mine.Count(c => ProjectZ.FloorOf(c, map) == 0 && !Api.HasFloorAt(c, 1, map));
            int onUpper = mine.Count(c => ProjectZ.FloorOf(c, map) == 1);
            Result("api6 E1 ExplosionCells3D on the slab's edge: the game's own (patched) cells again; none under the slab, some past its edge",
                gs.SetEquals(ms) && mine.Count == ms.Count && underSlab == 0 && pastEdge > 0 && onUpper > 0,
                $"{mine.Count} cells (game {game.Count}), floor 1 {onUpper}, ground under the slab {underSlab}, ground past the edge {pastEdge}");
            List<IntVec3> high = new List<IntVec3>();
            Api.ExplosionCells3D(centre, map, r, game, high, centreHeight: 0.05f);
            int highPast = high.Count(c => ProjectZ.FloorOf(c, map) == 0);
            Result("api6 E2 ExplosionCells3D centreHeight: a blast lying on the slab reaches no more ground than the default", highPast <= pastEdge,
                $"ground cells: default {pastEdge}, centre at 0.05 {highPast}");

            // ---- ClipRadialLoops (a test method: a radius loop at the ground band's north edge)
            IntVec3 edge = new IntVec3(slab.x, 0, an.groundZ - 2);
            MethodInfo probe = AccessTools.Method(typeof(TestRunner), nameof(P6RadialProbe));
            int before6 = P6RadialProbe(edge, 5.9f, map);
            bool clipped = Api.ClipRadialLoops(probe, "arg:map");
            int after6 = P6RadialProbe(edge, 5.9f, map);
            int other = P6RadialProbe(slab, 5.9f, map);
            Result("api6 C1 ClipRadialLoops: another mod's radius loop stays on its centre's floor", before6 > 0 && clipped && after6 == 0 && other == 0,
                $"cells off the centre's floor: before {before6}, after {after6} (patched {clipped}); mid-field {other}");
            Finish();
        }

        // A float near 'guess' (up to 400 ulps away) with f(x) == target exactly, for lines that meet a plane exactly at a
        // cell corner (f repeats the walker's own height formula); NaN: none.
        private static float P6Exact(Func<float, float> f, float target, float guess)
        {
            int bits = BitConverter.ToInt32(BitConverter.GetBytes(guess), 0);
            for (int i = 0; i <= 400; i++)
            {
                for (int s = -1; s <= 1; s += 2)
                {
                    float x = BitConverter.ToSingle(BitConverter.GetBytes(bits + s * i), 0);
                    if (f(x) == target)
                    {
                        return x;
                    }
                }
            }
            return float.NaN;
        }

        // The solid planes a full walk reports: how many steps carry one, and whether each is plane 1 over a slab column.
        private static string P6Planes(Vector3 from, Vector3 to, Map map, List<Api.LineStep> steps, out int planes, out bool onSlab)
        {
            int n = Api.WalkLine3D(from, to, map, steps, false);
            planes = 0;
            onSlab = true;
            string at = "";
            for (int i = 0; i < n; i++)
            {
                if (steps[i].solidPlane != int.MinValue)
                {
                    planes++;
                    IntVec3 g = ProjectZ.GroundCellOf(steps[i].cell, map);
                    onSlab &= steps[i].solidPlane == 1 && Api.HasFloorAt(g, 1, map);
                    at += $" step {i} plane {steps[i].solidPlane} at ({g.x - p6Field.minX},{g.z - p6Field.minZ})";
                }
            }
            return $"{n} steps,{(planes == 0 ? " no plane" : at)}";
        }

        // Lines through exact cell corners and near-corner diagonals (the walk takes the cell beside the one the line
        // crosses): every crossing of the slab's plane is reported on exactly one step, from above and from below; lines
        // through the stairwell opening stay clear. Each check builds its own lines (fixed seeds), the fixture is P6Setup's.
        private static void P6Corners(Map map, float S, List<Api.LineStep> steps)
        {
            float fx = p6Field.minX, fz = p6Field.minZ;
            Vector3 P(float x, float y, float z) => new Vector3(fx + x, y, fz + z);

            // W5: exact corners (field (3,3) to (5,5) diagonals, the plane level met exactly at the first corner)
            float y1 = P6Exact(y => (S + 0.5f) + (y - (S + 0.5f)) * 0.25f, S, S - 1.5f);
            string dn = P6Planes(P(3.5f, S + 0.5f, 3.5f), P(5.5f, y1, 5.5f), map, steps, out int pDn, out bool sDn);
            float y0 = P6Exact(y => y + ((S + 1.5f) - y) * 0.25f, S, S - 0.5f);
            string up = P6Planes(P(5.5f, y0, 5.5f), P(3.5f, S + 1.5f, 3.5f), map, steps, out int pUp, out bool sUp);
            Result("api6 W5 WalkLine3D through exact cell corners, the slab's plane met at a corner: one solid step, from above and below",
                !float.IsNaN(y1) && !float.IsNaN(y0) && pDn == 1 && sDn && pUp == 1 && sUp,
                $"down: {dn}; up: {up}" + (float.IsNaN(y1) || float.IsNaN(y0) ? "; no exact corner height found" : ""));

            // W6: a near-corner diagonal: the line crosses (3,4) where the walk takes (4,3), the plane level in between
            dn = P6Planes(P(3.5f, S + 0.25f, 3.8f), P(6.5f, S - 1.75f, 6.2f), map, steps, out pDn, out sDn);
            up = P6Planes(P(6.5f, S - 1.75f, 6.2f), P(3.5f, S + 0.25f, 3.8f), map, steps, out pUp, out sUp);
            Result("api6 W6 WalkLine3D near-corner diagonal through the slab (walk beside the line's cell): one solid step, from above and below",
                pDn == 1 && sDn && pUp == 1 && sUp, $"down: {dn}; up: {up}");

            // W7: 400 seeded lines each way between points over the slab (x, z 2..8), one end above it, one below
            System.Random rnd = new System.Random(6107);
            float R(float a, float b) => a + (float)rnd.NextDouble() * (b - a);
            int bad = 0, lines = 0;
            List<string> badAt = new List<string>();
            for (int i = 0; i < 800; i++)
            {
                Vector3 hi = P(R(2f, 8f), R(S + 0.05f, S + 1.2f), R(2f, 8f));
                Vector3 lo = P(R(2f, 8f), R(0.05f, S - 0.05f), R(2f, 8f));
                Vector3 from = i % 2 == 0 ? hi : lo, to = i % 2 == 0 ? lo : hi;
                string what = P6Planes(from, to, map, steps, out int pl, out bool sl);
                lines++;
                if (pl != 1 || !sl)
                {
                    bad++;
                    if (badAt.Count < 4)
                    {
                        badAt.Add($"({from.x - fx:0.000},{from.y:0.000},{from.z - fz:0.000})->({to.x - fx:0.000},{to.y:0.000},{to.z - fz:0.000}): {what}");
                    }
                }
            }
            Result("api6 W7 WalkLine3D 800 seeded lines through the slab (half down, half up): each reports the slab's plane on exactly one step",
                bad == 0 && lines == 800, $"{bad} of {lines} wrong" + (badAt.Count > 0 ? "; " + string.Join("; ", badAt) : ""));

            // L4: LineClear3D through an exact corner of the slab (the plane level met exactly at the corner)
            float ht = P6Exact(h => (1 * S + 0.5f) + (h - (1 * S + 0.5f)) * 0.25f, S, S - 1.5f);
            float hs = P6Exact(h => h + ((1 * S + 0.5f) - h) * 0.25f, S, S - 1f / 6f);
            bool cDn = Api.LineClear3D(PU(3, 3), 0.5f, PG(5, 5), ht, map);
            bool cUp = Api.LineClear3D(PG(5, 5), hs, PU(3, 3), 0.5f, map);
            Result("api6 L4 LineClear3D through an exact corner of the slab at its plane's level: blocked from above and below",
                !float.IsNaN(ht) && !float.IsNaN(hs) && !cDn && !cUp, $"down clear {cDn} (to height {ht:0.0000}), up clear {cUp} (from height {hs:0.0000})");

            // W8 / L5 control: the stairwell opening (x 11) with open air beside it (x 10, 12): no plane anywhere
            List<IntVec3> well = p6Field.Cells.Where(g => map.roofGrid.RoofAt(g) == PZDefOf.PZ_StairwellRoof).ToList();
            int zMin = well.Count > 0 ? well.Min(g => g.z) - p6Field.minZ : 2, zMax = well.Count > 0 ? well.Max(g => g.z) - p6Field.minZ : 4;
            bool open = well.Count >= 3 && zMax - zMin >= 2;
            for (int x = 10; x <= 12; x++)
            {
                for (int z = zMin; z <= zMax; z++)
                {
                    open &= !Api.PlaneSolid(PG(x, z), 1, map) && !Api.PlaneSolid(PG(x, z), 2, map);
                }
            }
            rnd = new System.Random(6108);
            int leaks = 0;
            List<string> leakAt = new List<string>();
            for (int i = 0; i < 400; i++)
            {
                Vector3 hi = P(R(10f, 13f), R(S + 0.05f, S + 0.9f), R(zMin, zMax + 1));
                Vector3 lo = P(R(10f, 13f), R(0.05f, S - 0.05f), R(zMin, zMax + 1));
                Vector3 from = i % 2 == 0 ? hi : lo, to = i % 2 == 0 ? lo : hi;
                string what = P6Planes(from, to, map, steps, out int pl, out _);
                if (pl != 0)
                {
                    leaks++;
                    if (leakAt.Count < 4)
                    {
                        leakAt.Add($"({from.x - fx:0.000},{from.y:0.000},{from.z - fz:0.000})->({to.x - fx:0.000},{to.y:0.000},{to.z - fz:0.000}): {what}");
                    }
                }
            }
            float yw = P6Exact(y => (S + 0.5f) + (y - (S + 0.5f)) * 0.25f, S, S - 1.5f);
            string cw = P6Planes(P(10.5f, S + 0.5f, zMin + 0.5f), P(12.5f, yw, zMin + 2.5f), map, steps, out int pw, out _);
            Result("api6 W8 control: WalkLine3D through the stairwell opening (400 seeded lines, one exact corner): no solid plane",
                open && leaks == 0 && pw == 0 && !float.IsNaN(yw), $"opening rows {zMin}..{zMax}, all open {open}; {leaks} of 400 report a plane" + (leakAt.Count > 0 ? "; " + string.Join("; ", leakAt) : "") + $"; corner line {cw}");
            float hw = P6Exact(h => (1 * S + 0.5f) + (h - (1 * S + 0.5f)) * 0.25f, S, S - 1.5f);
            bool blockers = Enumerable.Range(10, 3).Any(x => Enumerable.Range(zMin, 3).Any(z => Api.SightBlockerAt(PG(x, z), 0, map) || Api.SightBlockerAt(PG(x, z), 1, map)));
            bool wDn = Api.LineClear3D(PU(10, zMin), 0.5f, PG(12, zMin + 2), hw, map);
            bool wUp = Api.LineClear3D(PG(12, zMin + 2), hw, PU(10, zMin), 0.5f, map);
            Result("api6 L5 control: LineClear3D through an exact corner over the stairwell opening at plane 1's level: clear both ways",
                open && !blockers && wDn && wUp, $"down {wDn}, up {wUp}, sight blockers in the box {blockers}");
        }
    }
}
