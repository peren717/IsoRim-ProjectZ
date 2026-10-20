using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using ProjectZ.Combat;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // DIAG mode jumpdiag (branch jumpdiag, report-only investigation 2026-10-20): cross-floor jumps flown at normal game
    // speed (the game ticks itself, one frame per draw), every drawn frame of the flyer logged to frames.tsv: the flyer's
    // tick, Position, the game's groundPos/effectivePos, JumpFlights' pose, where IsoRim shows the effectivePos
    // (Floors.Drawn) and what the pawn renderer gets (RenderPawnAt drawLoc, DrawHook floor matrix). Run with
    // -projectzfloors=3.
    public static partial class TestRunner
    {
        private sealed class JdCase
        {
            public string name;
            public IntVec3 from, to;
            public string who = "pack";
            public int view;   // floor viewed for the shots (0 ground)
        }

        private static readonly AccessTools.FieldRef<PawnFlyer, Vector3> jdEff = AccessTools.FieldRefAccess<PawnFlyer, Vector3>("effectivePos");
        private static readonly AccessTools.FieldRef<PawnFlyer, Vector3> jdGround = AccessTools.FieldRefAccess<PawnFlyer, Vector3>("groundPos");
        private static readonly AccessTools.FieldRef<PawnFlyer, Vector3> jdStart = AccessTools.FieldRefAccess<PawnFlyer, Vector3>("startVec");
        private static readonly AccessTools.FieldRef<PawnFlyer, IntVec3> jdDest = AccessTools.FieldRefAccess<PawnFlyer, IntVec3>("destCell");
        private static readonly AccessTools.FieldRef<PawnFlyer, int> jdTicks = AccessTools.FieldRefAccess<PawnFlyer, int>("ticksFlying");
        private static readonly AccessTools.FieldRef<PawnFlyer, int> jdTotal = AccessTools.FieldRefAccess<PawnFlyer, int>("ticksFlightTime");

        private static IntVec3 jdO;
        private static List<JdCase> jdCases;
        private static int jdCase, jdFrames, jdShots;
        private static Pawn jdJumper, jdSang;
        private static Verb jdVerb;
        private static PawnFlyer jdFlyer;
        private static string jdFile;
        private static bool jdPatched;
        private static string jdCaseName = "";
        private static float jdMaxOff;   // the largest jump of the drawn ground point between two frames
        private static Vector3 jdLastDrawn;
        private static bool jdHaveLast;
        private static string jdFirstOff;
        private static int jdFails;

        private static IntVec3 JdO(int x, int z) => new IntVec3(jdO.x + x, 0, jdO.z + z);

        private static IntVec3 JdUp(int x, int z, int k, Map map) => ProjectZ.CellOnFloor(JdO(x, z), k, map);

        private static Pawn JdWho(JdCase c) => c.who == "sang" ? jdSang : jdJumper;

        private static Verb JdVerb(JdCase c) => c.who == "sang" ? jdSang.abilities.GetAbility(DefDatabase<AbilityDef>.GetNamed("Longjump")).verb : jdVerb;

        private static void JdLog(string s)
        {
            try
            {
                File.AppendAllText(jdFile, s + "\n");
            }
            catch
            {
            }
        }

        private static string V(Vector3 v) => $"({v.x:0.00},{v.y:0.00},{v.z:0.00})";

        // the flyer drawn (DrawPhase.Draw): the game's positions and where IsoRim shows them
        public static void JdFlyerDrawn(PawnFlyer __instance, DrawPhase phase)
        {
            if (phase != DrawPhase.Draw || !Active || __instance == null || !__instance.Spawned)
            {
                return;
            }
            Map map = __instance.Map;
            Vector3 eff = jdEff(__instance);
            Vector3 drawn = eff;
            bool shown = IsoRim.Floors.Drawn(ref drawn);
            string pose = JumpFlights.PoseOf(__instance, out JumpFlights.Pose p) ? $"{p.band}\t{p.rise:0.00}\t{p.height:0.00}\t{p.ground}" : "-\t-\t-\t-";
            JdLog($"{jdCaseName}\tF\t{Time.frameCount}\t{Find.TickManager.TicksGame}\t{jdTicks(__instance)}/{jdTotal(__instance)}\t{__instance.Position}\t{ProjectZ.FloorOf(__instance.Position, map)}\t{V(jdGround(__instance))}\t{V(eff)}\t{pose}\t{shown}\t{V(drawn)}");
        }

        // what the renderer gets for the flying pawn
        public static void JdRenderPawn(PawnRenderer __instance, Vector3 __0)
        {
            if (!Active || jdFlyer == null || !jdFlyer.Spawned)
            {
                return;
            }
            Pawn pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            if (pawn == null || pawn.ParentHolder != jdFlyer)
            {
                return;
            }
            Vector3 drawn = __0;
            bool shown = IsoRim.Floors.Drawn(ref drawn);
            Matrix4x4 fm = IsoRim.DrawHook.floorMatrix;
            Vector3 fmT = fm.GetColumn(3);
            // the ground point the 3D view shows the pawn over (Drawn of drawLoc, plus the DrawHook floor matrix's shift)
            Vector3 g = new Vector3(drawn.x + (IsoRim.DrawHook.floor ? fmT.x : 0f), 0f, drawn.z + (IsoRim.DrawHook.floor ? fmT.z : 0f));
            if (jdHaveLast)
            {
                float d = (g - jdLastDrawn).magnitude;
                if (d > jdMaxOff)
                {
                    jdMaxOff = d;
                }
                if (d > 3f && jdFirstOff == null)
                {
                    jdFirstOff = $"frame {Time.frameCount} tick {jdTicks(jdFlyer)}/{jdTotal(jdFlyer)}: drawn ground point {V(jdLastDrawn)} -> {V(g)}";
                }
            }
            jdLastDrawn = g;
            jdHaveLast = true;
            JdLog($"{jdCaseName}\tP\t{Time.frameCount}\t{Find.TickManager.TicksGame}\t{jdTicks(jdFlyer)}/{jdTotal(jdFlyer)}\tpawnPos {pawn.Position} floor {ProjectZ.FloorOf(pawn.Position, jdFlyer.Map)}\tdrawLoc {V(__0)}\tshown {shown}\tdrawn {V(drawn)}\thookFloor {IsoRim.DrawHook.floor} {V(fmT)}\tground {V(g)}");
        }

        private static bool StepJumpDiag(Map map)
        {
            switch (stage)
            {
                case "jd_setup":
                    if (GenCommandLine.TryGetCommandLineArg("projectzsave", out string save))
                    {
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                        stage = "jd_user";
                        pendingLoad = save;
                        return true;
                    }
                    SetupJumpDiag(map);
                    return true;
                case "jd_user":
                    SetupJumpDiagUser(map);
                    return true;
                case "jd_case":
                {
                    if (jdCase >= jdCases.Count)
                    {
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                        Finish();
                        return true;
                    }
                    JdCase c = jdCases[jdCase];
                    Pawn p = JdWho(c);
                    Heal(p);
                    Teleport(p, c.from);
                    Draft(p, true);
                    JdRecharge();
                    Verb v = JdVerb(c);
                    bool ok = v.ValidateTarget(c.to, false);
                    jdCaseName = c.name;
                    Info($"{c.name}: {c.who} from {c.from} (level {ProjectZ.FloorOf(c.from, map)}, ground {ProjectZ.GroundCellOf(c.from, map)}) to {c.to} (level {ProjectZ.FloorOf(c.to, map)}, ground {ProjectZ.GroundCellOf(c.to, map)}), accepted {ok}, 3D {Range3D.Distance(c.from, c.to, map):0.0}, range {v.EffectiveRange:0.0}");
                    if (!ok)
                    {
                        Result($"{c.name}: target accepted", false, "refused");
                        jdCase++;
                        Wait(2, "jd_case");
                        return true;
                    }
                    jdMaxOff = 0f;
                    jdHaveLast = false;
                    jdFirstOff = null;
                    jdShots = 0;
                    IntVec3 ga = ProjectZ.GroundCellOf(c.from, map), gb = ProjectZ.GroundCellOf(c.to, map);
                    ShotAt(map, c.view, new IntVec3((ga.x + gb.x) / 2, 0, (ga.z + gb.z) / 2), 12f);
                    v.OrderForceTarget(c.to);
                    jdFlyer = null;
                    jdFrames = 0;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;   // the game ticks itself from here (real play)
                    Wait(1, "jd_fly");
                    return true;
                }
                case "jd_fly":
                {
                    JdCase c = jdCases[jdCase];
                    Pawn p = JdWho(c);
                    jdFrames++;
                    if (jdFlyer == null && p.ParentHolder is PawnFlyer f)
                    {
                        jdFlyer = f;
                        Info($"{c.name}: took off at tick {Find.TickManager.TicksGame}: startVec {V(jdStart(f))} dest {jdDest(f)}, {jdTotal(f)} ticks, ends {JumpFlights.Ends(f, map, out int ks, out int kd)} ({ks}->{kd})");
                    }
                    if (jdFlyer != null && jdFlyer.Spawned)
                    {
                        float frac = (float)jdTicks(jdFlyer) / Mathf.Max(1, jdTotal(jdFlyer));
                        if (jdShots == 0 && frac >= 0.4f || jdShots == 1 && frac >= 0.7f)
                        {
                            ZCapture($"jd_{c.name}_s{jdShots}");
                            Info($"{c.name}: shot {jdShots} at tick {jdTicks(jdFlyer)}/{jdTotal(jdFlyer)}, flyer at {jdFlyer.Position}, eff {V(jdEff(jdFlyer))}");
                            jdShots++;
                        }
                    }
                    if (jdFlyer != null && !jdFlyer.Spawned && p.Spawned || jdFrames > 1500)
                    {
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                        bool there = p.Spawned && ProjectZ.FloorOf(p.Position, map) == ProjectZ.FloorOf(c.to, map) && p.Position.DistanceTo(c.to) <= 1.5f;
                        Result($"{c.name}: lands where asked", there, $"at {p.Position} (level {ProjectZ.FloorOf(p.Position, map)}), asked {c.to}; frames {jdFrames}");
                        Result($"{c.name}: the drawn pawn moves smoothly (no frame-to-frame jump > 3 cells)", jdMaxOff <= 3f, $"largest step {jdMaxOff:0.00} cells; {jdFirstOff ?? "none"}");
                        jdFlyer = null;
                        jdCase++;
                        Wait(5, "jd_case");
                        return true;
                    }
                    Wait(0, "jd_fly");
                    return true;
                }
            }
            return false;
        }

        private static void JdRecharge()
        {
            foreach (Apparel a in jdJumper.apparel.WornApparel)
            {
                CompApparelVerbOwner_Charged c = a.GetComp<CompApparelVerbOwner_Charged>();
                if (c != null)
                {
                    Traverse.Create(c).Field("remainingCharges").SetValue(c.MaxCharges);
                }
            }
            Gene_Hemogen h = jdSang?.genes?.GetFirstGeneOfType<Gene_Hemogen>();
            if (h != null)
            {
                h.Value = h.Max;
            }
            if (jdSang != null)
            {
                foreach (Ability ab in jdSang.abilities.AllAbilitiesForReading)
                {
                    ab.ResetCooldown();
                }
            }
        }

        private static void JdPatch()
        {
            if (!jdPatched)
            {
                Harmony h = new Harmony("Peren.IsoRimProjectZ.jumpdiag");
                h.Patch(AccessTools.Method(typeof(PawnFlyer), nameof(PawnFlyer.DynamicDrawPhaseAt)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(JdFlyerDrawn)));
                h.Patch(AccessTools.Method(typeof(PawnRenderer), "RenderPawnAt"), prefix: new HarmonyMethod(typeof(TestRunner), nameof(JdRenderPawn)) { priority = Priority.Last });
                jdPatched = true;
            }
        }

        // A copy of a player's save (-projectzsave): a colonist with a jump pack jumps from the upper floors in 8 directions
        // round the colony (cells picked in a fixed order, no randomness).
        private static void SetupJumpDiagUser(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            jdFile = Path.Combine(outDir, "frames.tsv");
            File.WriteAllText(jdFile, "case\tkind\tframe\ttick\tflight\tpos\tlevel\tgroundPos\teffectivePos\tband\trise\theight\tposeGround\tshown\tdrawn\n");
            JdPatch();
            DebugSettings.enableRandomMentalStates = false;
            Info($"user save: map {map.Size}, groundZ {an?.groundZ}, stride {an?.stride}, floors {an?.floors}, levels {Levels.Min(map)}..{Levels.Max(map)}, storey {IsoRimBridge.StoreyHeight:0.00}");
            List<Pawn> cols = Colonists(map).OrderBy(p => p.thingIDNumber).ToList();
            foreach (Pawn p in cols)
            {
                Info($"colonist {p} at {p.Position} level {ProjectZ.FloorOf(p.Position, map)} apparel {string.Join(",", p.apparel.WornApparel.Select(a => a.def.defName))}");
            }
            jdJumper = cols.FirstOrDefault(p => p.apparel.WornApparel.Any(a => a.def.defName == "Apparel_PackJump")) ?? cols[0];
            Apparel pack = jdJumper.apparel.WornApparel.FirstOrDefault(a => a.def.defName == "Apparel_PackJump");
            if (pack == null)
            {
                pack = (Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Apparel_PackJump"));
                jdJumper.apparel.Wear(pack, dropReplacedApparel: false);
            }
            jdVerb = pack.GetComp<CompApparelVerbOwner>().AllVerbs.First(v => v is Verb_Jump);
            jdSang = null;
            jdCases = new List<JdCase>();
            // take-off cells: open-sky built cells on levels 1+ near the colony's centre of mass, nearest first
            IntVec3 home = new IntVec3((int)cols.Average(p => ProjectZ.GroundCellOf(p.Position, map).x), 0, (int)cols.Average(p => ProjectZ.GroundCellOf(p.Position, map).z));
            Info($"jumper {jdJumper} at {jdJumper.Position}, range {jdVerb.EffectiveRange:0.0}; colony centre (ground) {home}");
            IntVec3[] dirs = { new IntVec3(0, 0, -1), new IntVec3(1, 0, -1), new IntVec3(1, 0, 0), new IntVec3(1, 0, 1), new IntVec3(0, 0, 1), new IntVec3(-1, 0, 1), new IntVec3(-1, 0, 0), new IntVec3(-1, 0, -1) };
            for (int k = 1; k <= Levels.Max(map) && jdCases.Count < 16; k++)
            {
                List<IntVec3> starts = new List<IntVec3>();
                for (int r = 0; r < 80 && starts.Count < 400; r++)
                {
                    foreach (IntVec3 g in GenRadial.RadialCellsAround(home, r, r + 1))
                    {
                        if (!g.InBounds(map) || g.z >= an.groundZ || (g.x + g.z) % 4 != 0)
                        {
                            continue;
                        }
                        IntVec3 u = ProjectZ.CellOnFloor(g, k, map);
                        if (u.IsValid && u.InBounds(map) && u.Standable(map) && Column.OpenAbove(u, map) && !u.Fogged(map))
                        {
                            starts.Add(u);
                        }
                    }
                }
                Info($"level {k}: {starts.Count} open-sky take-off cells near the centre");
                foreach (IntVec3 d in dirs)
                {
                    bool done = false;
                    foreach (IntVec3 s in starts)
                    {
                        IntVec3 gs = ProjectZ.GroundCellOf(s, map);
                        for (int dist = 9; dist >= 4 && !done; dist--)
                        {
                            IntVec3 gt = gs + d * dist;
                            if (!gt.InBounds(map) || gt.z >= an.groundZ || !gt.Standable(map) || gt.Fogged(map))
                            {
                                continue;
                            }
                            Teleport(jdJumper, s);
                            Draft(jdJumper, true);
                            JdRecharge();
                            bool valid = jdVerb.ValidateTarget(gt, false);
                            if (!valid && jdFails < 12)
                            {
                                jdFails++;
                                Info($"  refused {s} -> {gt}: valid target {JumpUtility.ValidJumpTarget(jdJumper, map, gt)}, can hit {jdVerb.CanHitTarget(gt)}, open {Column.OpenAbove(gt, map)}, arc {JumpRules.Arc(s, gt, map, ThingDefOf.PawnFlyer)}, 3D {Range3D.Distance(s, gt, map):0.0}, available {jdVerb.Available()}, drafted {jdJumper.Drafted}");
                            }
                            if (valid)
                            {
                                jdCases.Add(new JdCase { name = $"L{k}_dir{d.x}{d.z}_d{dist}", from = s, to = gt, view = k });
                                done = true;
                            }
                        }
                        if (done)
                        {
                            break;
                        }
                    }
                }
            }
            jdCase = 0;
            Wait(10, "jd_case");
        }

        private static void SetupJumpDiag(Map map)
        {
            AnnexMapComponent an = ProjectZ.Annex(map);
            jdFile = Path.Combine(outDir, "frames.tsv");
            File.WriteAllText(jdFile, "case\tkind\tframe\ttick\tflight\tpos\tlevel\tgroundPos\teffectivePos\tband\trise\theight\tposeGround\tshown\tdrawn\n");
            if (!jdPatched)
            {
                Harmony h = new Harmony("Peren.IsoRimProjectZ.jumpdiag");
                h.Patch(AccessTools.Method(typeof(PawnFlyer), nameof(PawnFlyer.DynamicDrawPhaseAt)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(JdFlyerDrawn)));
                h.Patch(AccessTools.Method(typeof(PawnRenderer), "RenderPawnAt"), prefix: new HarmonyMethod(typeof(TestRunner), nameof(JdRenderPawn)) { priority = Priority.Last });
                jdPatched = true;
            }
            if (an == null || an.floors < 3)
            {
                Result("jumpdiag setup", false, $"needs 3 floors (-projectzfloors=3); floors {an?.floors}");
                Finish();
                return;
            }
            Info($"map {map.Size}, groundZ {an.groundZ}, stride {an.stride}, floors {an.floors}, storey {IsoRimBridge.StoreyHeight:0.00}");
            CellRect found = CellRect.Empty;
            int bestBad = int.MaxValue;
            for (int z = 3; z + 40 < an.groundZ - 3; z += 4)
            {
                for (int x = 3; x + 40 < map.Size.x - 3; x += 4)
                {
                    CellRect rr = new CellRect(x, z, 40, 40);
                    if (rr.ExpandedBy(1).Cells.Any(c => c.InBounds(map) && c.GetThingList(map).Any(t => !t.def.destroyable)))
                    {
                        continue;
                    }
                    int bad = rr.Cells.Count(c => c.GetTerrain(map).IsWater || c.GetTerrain(map).passability == Traversability.Impassable || map.roofGrid.Roofed(c) || c.GetFirstBuilding(map) != null);
                    if (bad < bestBad)
                    {
                        bestBad = bad;
                        found = rr;
                    }
                }
            }
            foreach (IntVec3 c in found.ExpandedBy(1))
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (!(t is Pawn) && t.def.destroyable)
                    {
                        t.Destroy();
                    }
                }
                TerrainDef td = c.GetTerrain(map);
                if (td.IsWater || td.passability == Traversability.Impassable)
                {
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
            }
            DebugSettings.enableRandomMentalStates = false;
            jdO = new IntVec3(found.minX, 0, found.minZ + 6);
            // P: a 9x9 floor-2 slab (level 1) over roofed ground; T: a 9x9 floor-3 slab (level 2), open air on level 1 under it
            CellRect P = new CellRect(jdO.x + 4, jdO.z + 10, 9, 9);
            CellRect T = new CellRect(jdO.x + 24, jdO.z + 12, 9, 9);
            foreach (IntVec3 g in P)
            {
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                IntVec3 u = ProjectZ.CellOnFloor(g, 1, map);
                map.terrainGrid.SetTerrain(u, PZDefOf.PZ_UpperFloor);
                map.fogGrid.Unfog(u);
            }
            foreach (IntVec3 g in T)
            {
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                map.roofGrid.SetRoof(ProjectZ.CellOnFloor(g, 1, map), RoofDefOf.RoofConstructed);
                IntVec3 u = ProjectZ.CellOnFloor(g, 2, map);
                map.terrainGrid.SetTerrain(u, PZDefOf.PZ_UpperFloor);
                map.fogGrid.Unfog(u);
            }
            List<Pawn> cols = Colonists(map);
            foreach (Pawn p in cols)
            {
                Draft(p, true);
                Teleport(p, CellFinder.RandomClosewalkCellNear(new IntVec3(found.minX > map.Size.x / 2 ? 5 : map.Size.x - 6, 0, 5), map, 4));
            }
            jdJumper = cols[0];
            Apparel pack = (Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Apparel_PackJump"));
            jdJumper.apparel.Wear(pack, dropReplacedApparel: false);
            jdVerb = pack.GetComp<CompApparelVerbOwner>().AllVerbs.First(v => v is Verb_Jump);
            jdSang = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true, forcedXenotype: XenotypeDefOf.Sanguophage));
            GenSpawn.Spawn(jdSang, JdO(2, 2), map);
            Info($"field {found}, origin {jdO}; P {P} (level 1), T {T} (level 2); jumper {jdJumper} pack range {jdVerb.EffectiveRange:0.0}; sanguophage {jdSang}");
            // jump abilities in this game (defs using a jump verb)
            IEnumerable<string> abil = DefDatabase<AbilityDef>.AllDefs.Where(a => a.verbProperties != null && (typeof(Verb_CastAbilityJump).IsAssignableFrom(a.verbProperties.verbClass) || typeof(Verb_Jump).IsAssignableFrom(a.verbProperties.verbClass))).Select(a => $"{a.defName} ({a.verbProperties.verbClass.Name}, {a.modContentPack?.Name})");
            IEnumerable<string> gear = DefDatabase<ThingDef>.AllDefs.Where(d => d.Verbs != null && d.Verbs.Any(vp => vp.verbClass != null && typeof(Verb_Jump).IsAssignableFrom(vp.verbClass)) || d.comps != null && d.comps.Any(cp => cp is CompProperties_ApparelVerbOwner && false)).Select(d => d.defName);
            IEnumerable<string> gear2 = DefDatabase<ThingDef>.AllDefs.Where(d => d.IsApparel && d.comps != null && d.comps.OfType<CompProperties_ApparelVerbOwner>().Any() && d.Verbs != null && d.Verbs.Any(vp => vp.verbClass != null && typeof(Verb_Jump).IsAssignableFrom(vp.verbClass))).Select(d => $"{d.defName} ({d.modContentPack?.Name})");
            Info("jump abilities: " + string.Join("; ", abil));
            Info("jump apparel: " + string.Join("; ", gear2.Concat(gear).Distinct()));
            jdCases = new List<JdCase>
            {
                new JdCase { name = "down_south", from = JdUp(8, 10, 1, map), to = JdO(8, 3), view = 1 },
                new JdCase { name = "down_east", from = JdUp(12, 14, 1, map), to = JdO(19, 15), view = 1 },
                new JdCase { name = "down_north", from = JdUp(8, 18, 1, map), to = JdO(9, 26), view = 1 },
                new JdCase { name = "down_diag_far", from = JdUp(5, 11, 1, map), to = JdO(-3, 0), view = 0 },
                new JdCase { name = "up_south", from = JdO(8, 3), to = JdUp(8, 11, 1, map), view = 1 },
                new JdCase { name = "up_north", from = JdO(9, 26), to = JdUp(8, 17, 1, map), view = 1 },
                new JdCase { name = "same_ground", from = JdO(2, 2), to = JdO(12, 5), view = 0 },
                new JdCase { name = "same_floor2", from = JdUp(5, 12, 1, map), to = JdUp(11, 16, 1, map), view = 1 },
                new JdCase { name = "down3_south", from = JdUp(28, 12, 2, map), to = JdO(28, 5), view = 2 },
                new JdCase { name = "sang_down", from = JdUp(9, 10, 1, map), to = JdO(10, 3), who = "sang", view = 1 },
                new JdCase { name = "sang_up", from = JdO(10, 3), to = JdUp(10, 11, 1, map), who = "sang", view = 1 },
            };
            jdCase = 0;
            Wait(10, "jd_case");
        }
    }
}
