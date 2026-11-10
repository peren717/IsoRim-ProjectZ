using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProjectZ.Patches;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode mechrange (the mechanitor's command range drawn between floors, MechanitorRange_Preview_Patch). Run with
    // -projectzfloors=3 -projectzseed=7 -projectzmapsize=120 and -isorim3d. Router2's fixture tools: one fixture per case
    // (area wiped on all floors, its own fixed random state); a mechanitor, two of its militors drafted and selected.
    // Every case records the white field edges drawn over 30 frames (the game's ring and ProjectZ's) and compares them,
    // cell by cell on a 57 x 57 grid round the mechanitor on every drawn floor, with CanCommandTo (the 3D rule).
    //   P0  the patches landed (CanCommandTo 3D, the ring);
    //   A0  mechanitor on the ground, the ground viewed: the game's ring only (every 3D case: the white shell drawn);
    //   A1  the same, floor 1 viewed: its cross-section ring; a militor on floor 1 just inside (drawn, commanded) and one
    //       just outside (not drawn, refused);
    //   R1  real orders through the float menu: the inside militor's "go here" to a cell inside the ring on floor 1, taken
    //       and walked with ticks; the outside militor's to a cell past the ring refused (out of command range);
    //   B0  mechanitor on floor 1, the ground viewed: the ground's cross-section; B2: floor 2 viewed (three floors drawn);
    //   F0, F1  the flat view (the 3D view turned off for these shots only, test only): the game's ring only, unchanged (no ring, no shell added).
    public static partial class TestRunner
    {
        private const int MrSeed = 9300;
        private static Pawn mrMn;
        private static readonly List<Pawn> mrMechs = new List<Pawn>();
        private static int mrCase, mrVanillaCalls, mrVanillaAt, mrAddedAt, mrShellsAt;
        private static bool mrCapture;
        private static readonly HashSet<IntVec3> mrDrawn = new HashSet<IntVec3>();
        private static IntVec3 mrC;   // the mechanitor's ground column
        private static int mrIn, mrOut;   // ground offsets east of the column: just inside / just outside on floor 1
        private static Harmony mrHarmony;

        // name, mechanitor floor, mechs floor, viewed floor, 3D view
        private static readonly (string name, int mk, int bk, int view, bool v3d)[] MrCases =
        {
            ("A0_ground_view0", 0, 1, 0, true),
            ("A1_ground_view1", 0, 1, 1, true),
            ("B0_upper_view0", 1, 0, 0, true),
            ("B2_upper_view2", 1, 0, 2, true),
            ("F0_flat_ground", 0, 1, 0, false),
            ("F1_flat_upper", 1, 0, 1, false),
        };

        private static bool StepMechRange(Map map)
        {
            switch (stage)
            {
                case "mr_setup": MrSetup(map); return true;
                case "mr_case": MrCaseStart(map); return true;
                case "mr_take": MrCaseTake(map); return true;
                case "mr_orders": MrOrders(map); return true;
                case "mr_orders_check": MrOrdersCheck(map); return true;
                case "mr_done": MrDone(map); return true;
            }
            return false;
        }

        private static void MrSetup(Map map)
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
            foreach (Zone z in map.zoneManager.AllZones.ToList())
            {
                z.Delete(false);
            }
            AnnexMapComponent annex = ProjectZ.Annex(map);
            bool p0 = FailSoft.IsApplied("MechanitorRange_3D_Patch") && FailSoft.IsApplied("MechanitorRange_Preview_Patch");
            Result("P0 patches: CanCommandTo in 3D and the command range ring landed", p0,
                $"CanCommandTo {FailSoft.IsApplied("MechanitorRange_3D_Patch")}, ring {FailSoft.IsApplied("MechanitorRange_Preview_Patch")}; IsoRim {IsoRimBridge.Present}, 3D view {IsoRimBridge.View3DOn}, Biotech {ModsConfig.BiotechActive}");
            if (annex == null || annex.MaxLevel < 2 || !ModsConfig.BiotechActive)
            {
                Result("mechrange: a map with 3 floors and Biotech", false, $"annex {annex != null}, levels {Levels.Min(map)}..{Levels.Max(map)} (run with -projectzfloors=3)");
                Finish();
                return;
            }
            int gz = annex.groundZ;
            r2Area = new CellRect(map.Size.x / 2 - 32, gz / 2 - 30, 64, 60);
            List<Pawn> cols = Colonists(map);
            r2P = cols.First();
            r2Q = cols.FirstOrDefault(p => p != r2P);
            mrMn = r2P;
            mrMn.health.AddHediff(HediffDefOf.MechlinkImplant, mrMn.health.hediffSet.GetBrain());
            PawnKindDef militor = DefDatabase<PawnKindDef>.GetNamed("Mech_Militor");
            Rand.PushState(MrSeed);
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    Pawn m = PawnGenerator.GeneratePawn(new PawnGenerationRequest(militor, Faction.OfPlayer));
                    GenSpawn.Spawn(m, R2ParkCell(map, 4 + i), map);
                    mrMn.relations.AddDirectRelation(PawnRelationDefOf.Overseer, m);
                    PawnComponentsUtility.AddAndRemoveDynamicComponents(m);
                    mrMechs.Add(m);
                }
            }
            finally
            {
                Rand.PopState();
            }
            mrHarmony = new Harmony("Peren.IsoRimProjectZ.test.mechrange");
            mrHarmony.Patch(AccessTools.Method(typeof(Pawn_MechanitorTracker), nameof(Pawn_MechanitorTracker.DrawCommandRadius)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(MrVanillaPostfix)));
            mrHarmony.Patch(AccessTools.Method(typeof(GenDraw), nameof(GenDraw.DrawFieldEdges), new[] { typeof(List<IntVec3>), typeof(Color), typeof(float?), typeof(HashSet<IntVec3>), typeof(int) }),
                prefix: new HarmonyMethod(typeof(TestRunner), nameof(MrEdgesPrefix)));
            float s = IsoRimBridge.StoreyHeight;
            mrIn = 0;
            while ((mrIn + 1) * (mrIn + 1) + s * s < 620.01f)
            {
                mrIn++;
            }
            mrOut = mrIn + 1;
            Info($"mechrange: area {r2Area}, map {map.Size}, groundZ {gz}, storey {s:0.00} cells, floor-1 reach {Mathf.Sqrt(620.01f - s * s):0.00} (inside +{mrIn}, outside +{mrOut}), floor-2 reach {(620.01f - 4 * s * s > 0 ? Mathf.Sqrt(620.01f - 4 * s * s) : -1):0.00}; mechanitor {mrMn}, tracker {mrMn.mechanitor != null}, militors {string.Join(", ", mrMechs)} (overseer {mrMechs[0].GetOverseer()}, drafter {mrMechs[0].drafter != null})");
            mrCase = 0;
            stage = "mr_case";
        }

        public static void MrVanillaPostfix()
        {
            mrVanillaCalls++;
        }

        public static void MrEdgesPrefix(List<IntVec3> cells, Color color)
        {
            if (mrCapture && cells != null && color == Color.white)
            {
                mrDrawn.UnionWith(cells);
            }
        }

        // The fixture: the area wiped, an upper floor over a strip round the column on floors 1 and 2, the mechanitor on its
        // floor at the column (drafted), the militors on theirs just inside and just outside (drafted).
        private static void MrScene(Map map, int seed, int mk, int bk)
        {
            // (R2Clear destroys every pawn in the area but colonists: the militors leave the map for it)
            foreach (Pawn m in mrMechs.Where(m => m.Spawned))
            {
                m.DeSpawn();
            }
            R2Clear(map);
            R2Park(map, mrMn);
            R2Feed(map);
            Rand.PushState(seed);
            try
            {
                mrC = RG(32, 30);
                for (int L = 1; L <= 2; L++)
                {
                    R2Floor(map, new CellRect(mrC.x - 6, mrC.z - 4, 40, 9), L);
                }
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                Teleport(mrMn, Levels.Cell(mrC, mk, map));
                Draft(mrMn, true);
                IntVec3[] at = { Levels.Cell(mrC + new IntVec3(mrIn, 0, 0), bk, map), Levels.Cell(mrC + new IntVec3(mrOut, 0, 0), bk, map) };
                for (int i = 0; i < mrMechs.Count; i++)
                {
                    Pawn m = mrMechs[i];
                    GenSpawn.Spawn(m, at[i], map);
                    if (m.needs?.energy != null) m.needs.energy.CurLevelPercentage = 1f;
                    Draft(m, true);
                }
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static void MrCaseStart(Map map)
        {
            if (mrCase >= MrCases.Length)
            {
                stage = "mr_done";
                return;
            }
            var cs = MrCases[mrCase];
            MrScene(map, MrSeed + 10 * mrCase, cs.mk, cs.bk);
            IntVec3 g = new IntVec3(mrC.x + 4, 0, mrC.z);
            if (cs.v3d)
            {
                BrView3D(true);
                ShotAt(map, cs.view, g, 44f);
            }
            else
            {
                BrView3D(false);
                FloorView.Set(0);
                CameraDriver_Update_Patch.AllowAnnexView = true;
                Find.Selector.ClearSelection();
                IntVec3 c = Levels.Cell(g, cs.view, map);
                Find.CameraDriver.SetRootPosAndSize(new Vector3(c.x + 0.5f, 0f, c.z + 0.5f), 44f);
            }
            foreach (Pawn m in mrMechs)
            {
                Find.Selector.Select(m, playSound: false, forceDesignatorDeselect: false);
            }
            mrDrawn.Clear();
            mrCapture = true;
            mrVanillaAt = mrVanillaCalls;
            mrAddedAt = MrAdded();
            mrShellsAt = MrAdded("Shells");
            Wait(30, "mr_take");
        }

        // (rings ProjectZ added; read by name so the test also builds and runs without the ring patch, for the before shots)
        private static int MrAdded(string field = "Drawn")
        {
            Type t = AccessTools.TypeByName("ProjectZ.MechanitorRange_Preview_Patch");
            System.Reflection.FieldInfo fi = t == null ? null : AccessTools.Field(t, field);
            return fi == null ? 0 : (int)fi.GetValue(null);
        }

        // The drawn cells against CanCommandTo on the 57 x 57 grid round the column, per floor.
        private static string MrCompare(Map map, IEnumerable<int> floors, out int bad, out int otherDrawn)
        {
            bad = 0;
            otherDrawn = 0;
            List<string> parts = new List<string>();
            HashSet<int> fs = new HashSet<int>(floors);
            for (int k = Levels.Min(map); k <= Levels.Max(map); k++)
            {
                int inRange = 0, drawn = 0, mism = 0;
                string first = null;
                for (int dx = -28; dx <= 28; dx++)
                {
                    for (int dz = -28; dz <= 28; dz++)
                    {
                        IntVec3 c = Levels.Cell(mrC + new IntVec3(dx, 0, dz), k, map);
                        if (!c.IsValid || !c.InBounds(map))
                        {
                            continue;
                        }
                        bool can = mrMn.mechanitor.CanCommandTo(c);
                        bool d = mrDrawn.Contains(c);
                        inRange += can ? 1 : 0;
                        drawn += d ? 1 : 0;
                        if (fs.Contains(k) && can != d)
                        {
                            mism++;
                            first = first ?? $"({dx},{dz}) can {can} drawn {d}";
                        }
                    }
                }
                if (fs.Contains(k))
                {
                    bad += mism;
                }
                else
                {
                    otherDrawn += drawn;
                }
                parts.Add($"floor {k}{(fs.Contains(k) ? "" : " (not drawn here)")}: commandable {inRange}, drawn {drawn}, mismatches {mism}{(first != null ? " first " + first : "")}");
            }
            return string.Join("; ", parts);
        }

        private static void MrCaseTake(Map map)
        {
            var cs = MrCases[mrCase];
            mrCapture = false;
            int vanilla = mrVanillaCalls - mrVanillaAt;
            int added = MrAdded() - mrAddedAt;
            int shells = MrAdded("Shells") - mrShellsAt;
            int mk = cs.mk;
            bool view = IsoRimBridge.View3DOn;
            // drawn: the 3D view shows the viewed floor and those under it (the game's ring only while its floor shows)
            List<int> floors = new List<int>();
            if (!cs.v3d || mk <= cs.view)
            {
                floors.Add(mk);
            }
            if (cs.v3d)
            {
                floors.Add(0);
                floors.Add(cs.view);
            }
            string cmp = MrCompare(map, floors.Distinct(), out int bad, out int other);
            string head = $"{cs.name}: 3D view {view}, view floor {FloorView.Current}, mechanitor {BrWhere(mrMn.Position, map)}, militors {string.Join(", ", mrMechs.Select(m => BrWhere(m.Position, map) + " commandable " + mrMn.mechanitor.CanCommandTo(m.Position) + " drawn " + mrDrawn.Contains(m.Position)))}; game's ring calls over 30 frames {vanilla}, rings added {added}, shells {shells}";
            Info(head);
            if (cs.v3d)
            {
                bool rings = mk == 0 && cs.view == 0 ? added == 0 : added > 0;
                Result($"{cs.name} 3D: the drawn range on floors {string.Join(",", floors.Distinct())} is exactly where CanCommandTo is true", view && (vanilla > 0 || mk > cs.view) && rings && shells > 0 && bad == 0, head + "; " + cmp);
                if (cs.name.StartsWith("A1") || cs.name.StartsWith("B0"))
                {
                    Pawn pin = mrMechs[0], pout = mrMechs[1];
                    bool ok = mrMn.mechanitor.CanCommandTo(pin.Position) && mrDrawn.Contains(pin.Position) && !mrMn.mechanitor.CanCommandTo(pout.Position) && !mrDrawn.Contains(pout.Position);
                    Result($"{cs.name} militor just inside (+{mrIn}) is inside the drawn ring and commandable, the one just outside (+{mrOut}) is neither", ok, head);
                }
            }
            else
            {
                Result($"{cs.name} flat view: unchanged, the game's ring on the mechanitor's floor only", !view && vanilla > 0 && added == 0 && shells == 0 && bad == 0 && other == 0, head + "; " + cmp);
            }
            ZCapture("mr_" + cs.name);
            if (cs.name.StartsWith("A1"))
            {
                Wait(5, "mr_orders");
                return;
            }
            mrCase++;
            Wait(5, "mr_case");
        }

        // ------------------------------------------------------------------------------------------------ R1 real orders

        private static IntVec3 mrInTarget, mrOutTarget;
        private static string mrOrderNote;

        private static FloatMenuOption MrGoto(Pawn mech, IntVec3 c)
        {
            Find.Selector.ClearSelection();
            Find.Selector.Select(mech, playSound: false, forceDesignatorDeselect: false);
            List<FloatMenuOption> opts = FloatMenuMakerMap.GetOptions(new List<Pawn> { mech }, c.ToVector3Shifted(), out _);
            return opts.FirstOrDefault(o => o.isGoto) ?? opts.FirstOrDefault(o => o.Label.Contains("OutOfCommandRange".Translate()));
        }

        private static void MrOrders(Map map)
        {
            Map m = map;
            Pawn pin = mrMechs[0], pout = mrMechs[1];
            mrInTarget = Levels.Cell(mrC + new IntVec3(mrIn - 3, 0, 2), 1, m);
            mrOutTarget = Levels.Cell(mrC + new IntVec3(mrOut + 6, 0, 2), 1, m);
            FloatMenuOption oin = MrGoto(pin, mrInTarget);
            FloatMenuOption oout = MrGoto(pout, mrOutTarget);
            mrOrderNote = $"inside target {BrWhere(mrInTarget, m)} commandable {mrMn.mechanitor.CanCommandTo(mrInTarget)}: option '{oin?.Label}' disabled {oin?.Disabled}; outside target {BrWhere(mrOutTarget, m)} commandable {mrMn.mechanitor.CanCommandTo(mrOutTarget)}: option '{oout?.Label}' disabled {oout?.Disabled}";
            Info("R1 " + mrOrderNote);
            bool refused = oout == null || oout.Disabled && oout.Label.Contains("OutOfCommandRange".Translate());
            Result("R1 the militor just outside: a go-here past the ring is refused (out of command range)", refused, mrOrderNote);
            if (oin == null || oin.Disabled || oin.action == null)
            {
                Result("R1 the militor just inside: a go-here inside the ring on floor 1 is offered", false, mrOrderNote);
                mrCase++;
                Wait(5, "mr_case");
                return;
            }
            Find.Selector.ClearSelection();
            Find.Selector.Select(pin, playSound: false, forceDesignatorDeselect: false);
            oin.action();
            nextTickSeed = MrSeed + 77;
            TickUntil(2000, () => pin.Position == mrInTarget || pin.CurJobDef != JobDefOf.Goto && pin.Position.DistanceTo(mrInTarget) < 1.5f, "mr_orders_check", 5);
        }

        private static void MrOrdersCheck(Map map)
        {
            tickWatch = null;
            Pawn pin = mrMechs[0];
            Result("R1 the militor just inside: its go-here inside the ring on floor 1 is taken and walked (ticks)",
                pin.Position.DistanceTo(mrInTarget) < 1.5f && ProjectZ.FloorOf(pin.Position, map) == 1,
                $"{mrOrderNote}; militor now {BrWhere(pin.Position, map)}, job {pin.CurJobDef?.defName}");
            mrCase++;
            Wait(5, "mr_case");
        }

        private static void MrDone(Map map)
        {
            mrCapture = false;
            CameraDriver_Update_Patch.AllowAnnexView = false;
            BrView3D(true);
            FloorView.Set(0);
            mrHarmony?.UnpatchAll("Peren.IsoRimProjectZ.test.mechrange");
            Finish();
        }
    }
}
