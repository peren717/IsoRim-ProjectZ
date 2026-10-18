using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ProjectZ.Dev
{
    // Mode gravity (compat G2b, isorim-pz-compat-g2-plan.md section 5): gravity at the spawn entry points (Gravity.cs).
    // Run with -projectzfloors=3 -projectzmapsize=101 -projectzsave=pz_radial3 (the radial modes' saved map: a 1-row void
    // gap, the worst case). -projectznogravity: no hook acts; a check gravity alone decides is then named "LEAK ..." and
    // expects the old leak. -projectzgravityref=<tag>: compare this run's fingerprints (N2, G9) with that run's
    // (renders/<tag>/gv_fingerprints.txt). Every check: its own fixed seed (Rand.PushState(7400 + check)) for its whole
    // synchronous part, ticks included; its own fixture in an x-strip (all rows of 11 columns), everything it spawned
    // destroyed and the strip's terrain and roofs put back at its end; gravity counters reset. Real flows: pawn jobs and
    // verbs (jump pack, turret pack, grenades, removing a slab or deck, deconstructing, uninstalling, building), comps and
    // incidents ticking through TickManager.DoSingleTick. The events that make a floor vanish at a set tick use the game's
    // own removal (TerrainGrid.RemoveFoundation -> Slab.Remove).
    //   G0 the four hooks are patched (GenSpawn after ShipLocal's net); a map with floors seen.
    //   N2 (first, so both runs start alike) a battery of spawn flows well inside floor 2 and on the ground: fingerprints
    //      equal with and without gravity. G9 (also fingerprinted) a slab collapse: unchanged (its own fall).
    //   G1 a jump's landing slab removed just before it lands: the jumper and its carried steel fall 2 floors to the ground,
    //      the roof passed (the ceiling under the landing) breaks, the jumper is bruised.
    //   G2 a colonist killed in a flight over the void row: the corpse on the ground's north row, nothing in the void.
    //   G3 drops onto open air next to a slab edge (the carry tracker's drop, ThingOwner.TryDrop Direct): to the ground under
    //      open sky; through a built roof and a thin rock roof (both break); a thick rock roof stops it (the slab next to it).
    //   G4 an egg hatching in a flight over open air on floor 3: the chick falls, bruised.
    //   G5 a centre-drop raid onto a floor-2 slab: pods and raiders on floors (DropRules), gravity moves nothing.
    //   G6 a fight and a firefoam popper at floor 2's north edge (a 1-row gap to floor 3): no filth on air, none spread
    //      across the gap.
    //   G7 (Anomaly) a turret pack thrown at open air: no turret on air, no error (the refund); control on a slab.
    //   G8 a table uninstalled and installed again on floor 2: gravity untouched.
    //   G10 slab blueprints placed with the designator over open air and built by a colonist (a ship deck blueprint too,
    //      Odyssey): untouched, built.
    //   G11 stairs built in an open field and onto a slab edge: gravity untouched.
    //   G13 fires at the ground's north row and floor 2's south row: no fire on air or in the void.
    //   G14 a colonist takes up a slab with a pawn and steel on it (user 2026-10-18: they fall, reversing Q7), working from
    //      the next cell; G14d the same for a ship deck (Odyssey).
    //   G15 save and reload after falls, with one stray item left on open air under Gravity.Suspend (an old save's): every
    //      thing where it was saved, the stray stays, nothing moved during the load.
    //   P2 micro cost of Gravity.Air and Column.Landing.
    // Mode gravityn1 (-projectzfloors=1 -projectzsave=pz_radial1): N1, the battery on a map without floors, fingerprinted
    //   (compared with and without gravity: identical) and every gravity counter 0. Mode gravityperf (P1): ms per tick with
    //   30 colonists fighting (run with and without gravity, -projectzfloors=4 and 1, -projectzmapsize=250).
    // (G12, gravship landings, is covered by modes shipfly and shipstack: every mode's results end with the gravity counters.)
    public static partial class TestRunner
    {
        private static Dictionary<string, string> gvRef;
        private static readonly List<string> gvFp = new List<string>();
        private static readonly List<string> gvFpDetail = new List<string>();
        private static readonly AccessTools.FieldRef<PawnFlyer, IntVec3> gvDest = AccessTools.FieldRefAccess<PawnFlyer, IntVec3>("destCell");
        private static readonly AccessTools.FieldRef<CompHatcher, float> gvGestate = AccessTools.FieldRefAccess<CompHatcher, float>("gestateProgress");
        private static readonly PropertyInfo gvRandState = AccessTools.Property(typeof(Rand), "StateCompressed");
        // G15: thing id -> saved position (and the stray's id)
        private static readonly Dictionary<int, IntVec3> gvSaved = new Dictionary<int, IntVec3>();
        private static int gvStray = -1;
        private static int gvLoadFell, gvLoadRefused;

        private static bool GvOff => Gravity.Off;

        internal static void PatchGravityProbe(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Game), nameof(Game.LoadGame)), postfix: new HarmonyMethod(typeof(TestRunner), nameof(GvLoadedPostfix)));
        }

        // The compared runs load one save: the game's rand fixed once it is loaded (as mode radial).
        private static void GvLoadedPostfix()
        {
            AccessTools.Field(typeof(Rand), "seed").SetValue(null, 7400u);
            AccessTools.Field(typeof(Rand), "iterations").SetValue(null, 0u);
        }

        // A check gravity alone decides (leakable), or one that holds without it too.
        private static void GvExpect(string name, bool ok, bool leakable, string detail)
        {
            if (!GvOff)
            {
                Result(name, ok, detail);
            }
            else if (leakable && !ok)
            {
                Result("LEAK " + name, true, detail);
            }
            else if (leakable)
            {
                Info("no leak seen without gravity (the game's own rules caught it): " + name + ": " + detail);
            }
            else
            {
                Result(name + " (also without gravity)", ok, detail);
            }
        }

        // The check's fixture (RdBegin's strip, its own seed 7400 + check), gravity counters reset.
        private static void GvBegin(Map map, int check, string name, int slot)
        {
            RdBegin(map, 100 + check, name, slot);
            Gravity.ResetCounters();
        }

        // Cleanup outside gravity (the fixture's slabs and stairs go without falls); designations in the strip go too.
        private static void GvEnd(Map map)
        {
            if (rdFix == null)
            {
                return;
            }
            foreach (IntVec3 c in RdStrip(map))
            {
                foreach (Designation d in map.designationManager.AllDesignationsAt(c).ToList())
                {
                    d.Delete();
                }
            }
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(p => p.Faction != Faction.OfPlayer && p.HostileTo(Faction.OfPlayer)).ToList())
            {
                p.Destroy();
            }
            Gravity.Suspend();
            try
            {
                foreach (IntVec3 c in RdStrip(map))
                {
                    if (map.terrainGrid.FoundationAt(c) != null && rdFix.terrain.TryGetValue(c, out TerrainDef was) && was != map.terrainGrid.FoundationAt(c))
                    {
                        map.terrainGrid.RemoveFoundation(c, false);
                    }
                }
                RdEnd(map);
            }
            finally
            {
                Gravity.Resume();
            }
        }

        private static string GvCell(IntVec3 c, Map map) => c.IsValid ? $"{c} lv {(Levels.LevelOf(c, map) == Levels.None ? "void" : Levels.LevelOf(c, map).ToString())}{(Gravity.Air(c, map) ? " AIR" : "")}" : "invalid";

        private static string GvAt(Thing t, Map map) => t == null ? "null" : t.Destroyed ? t.def.defName + " destroyed" : t.Spawned ? t.def.defName + " at " + GvCell(t.Position, map) : t.def.defName + " held by " + t.ParentHolder;

        // Things resting on air in the strip (motes, projectiles, ethereal things and blueprints/frames of a floor aside).
        private static List<Thing> GvOnAir(Map map)
        {
            List<Thing> l = new List<Thing>();
            foreach (IntVec3 c in RdStrip(map))
            {
                if (!Gravity.Air(c, map))
                {
                    continue;
                }
                foreach (Thing t in map.thingGrid.ThingsListAtFast(c))
                {
                    if (Gravity.Exempt(t.def) || (t.def.IsBlueprint || t.def.IsFrame) && t.def.entityDefToBuild is TerrainDef || t is AttachableThing at && at.parent != null)
                    {
                        continue;
                    }
                    l.Add(t);
                }
            }
            return l;
        }

        private static string GvList(IEnumerable<Thing> l, Map map) => string.Join(", ", l.Take(8).Select(t => GvAt(t, map)));

        // A colonist with only this work type (null: none), undrafted.
        private static void GvWorker(Pawn p, WorkTypeDef w)
        {
            Draft(p, false);
            p.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
            foreach (WorkTypeDef d in DefDatabase<WorkTypeDef>.AllDefs)
            {
                if (!p.WorkTypeIsDisabled(d))
                {
                    p.workSettings.SetPriority(d, d == w ? 1 : 0);
                }
            }
        }

        private static Thing GvStack(ThingDef def, int n)
        {
            Thing t = ThingMaker.MakeThing(def);
            t.stackCount = n;
            return t;
        }

        private static Verb GvPackVerb(Pawn p, string packDef)
        {
            Apparel pack = (Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(packDef));
            p.apparel.Wear(pack, dropReplacedApparel: false);
            return pack.GetComp<CompApparelVerbOwner>()?.AllVerbs.FirstOrDefault();
        }

        // Ticks one by one until the pawn is in a flyer (true) or max ticks.
        private static PawnFlyer GvTakeOff(Pawn p, int max = 300)
        {
            RdTicks(max, () => p.ParentHolder is PawnFlyer, 1);
            return p.ParentHolder as PawnFlyer;
        }

        // ---- fingerprints (N1, N2, G9): the strip's things, pawns (position, job, hediffs) and the Rand state
        private static void GvFingerprint(Map map, string name, IEnumerable<Pawn> pawns)
        {
            StringBuilder sb = new StringBuilder();
            int things = 0, filth = 0;
            foreach (IntVec3 c in RdStrip(map))
            {
                foreach (Thing t in map.thingGrid.ThingsListAtFast(c).OrderBy(t => t.def.defName).ThenBy(t => t.HitPoints).ThenBy(t => t.stackCount))
                {
                    if (t is Pawn || t is Mote)
                    {
                        continue;
                    }
                    sb.Append(t.def.defName).Append('@').Append(c.x - rdFix.x0).Append(',').Append(c.z).Append(' ').Append(rdFix.before.Contains(t) ? "-" : t.Rotation.AsInt.ToString()).Append(' ').Append(t.HitPoints).Append(' ').Append(t.stackCount).Append(';');
                    things++;
                    if (t is Filth)
                    {
                        filth++;
                    }
                }
            }
            foreach (Pawn p in pawns)
            {
                sb.Append("pawn ").Append(p.Dead ? "dead " + (p.Corpse?.PositionHeld.x - rdFix.x0) + "," + p.Corpse?.PositionHeld.z : (p.PositionHeld.x - rdFix.x0) + "," + p.PositionHeld.z).Append(' ').Append(p.CurJobDef?.defName).Append(' ');
                foreach (Hediff h in p.health.hediffSet.hediffs.OrderBy(h => h.def.defName).ThenBy(h => h.Severity))
                {
                    sb.Append(h.def.defName).Append(':').Append(h.Severity.ToString("0.00")).Append(',');
                }
                sb.Append(';');
            }
            sb.Append("rand ").Append(gvRandState?.GetValue(null));
            uint hash = 2166136261;
            foreach (char ch in sb.ToString())
            {
                hash = (hash ^ ch) * 16777619;
            }
            string line = $"{name}\t{hash:x8}\t{things} things, {filth} filth, {pawns.Count()} pawns";
            gvFp.Add(line);
            gvFpDetail.Add(name + "\t" + sb.ToString().Replace(";", "\n  "));
            Info("fingerprint " + line.Replace('\t', ' '));
            if (gvRef != null)
            {
                if (gvRef.TryGetValue(name, out string other))
                {
                    string mine = line.Split('\t')[1];
                    Result($"{name}: identical with and without gravity", other.Split('\t')[0] == mine, $"this run {mine} ({line.Split('\t')[2]}), reference {other.Replace('\t', ' ')}");
                }
                else
                {
                    Result($"{name}: in the reference run", false, "missing there");
                }
            }
        }

        private static void GvFinish()
        {
            try
            {
                File.WriteAllLines(Path.Combine(outDir, "gv_fingerprints.txt"), gvFp);
                File.WriteAllLines(Path.Combine(outDir, "gv_fingerprints_detail.txt"), gvFpDetail);
            }
            catch (Exception e)
            {
                Out("fingerprints not written: " + e.Message);
            }
            Finish();
        }

        // ==================================================================== driver

        private static bool StepGravity(Map map)
        {
            try
            {
                return StepGravityInner(map);
            }
            catch (Exception e)
            {
                Result("gravity step " + stage, false, e.ToString());
                stage = "gv_end";
                return true;
            }
            finally
            {
                RdEndSeed();
            }
        }

        private static bool StepGravityInner(Map map)
        {
            switch (stage)
            {
                case "gv_start":
                    if (GenCommandLine.TryGetCommandLineArg("projectzsave", out string save) && mode != "gravityperf")
                    {
                        Traverse.Create(typeof(Prefs)).Field("data").Field("pauseOnLoad").SetValue(true);
                        pendingLoad = save;
                        Out("gravity: loading " + save);
                    }
                    stage = "gv_setup";
                    return true;
                case "gv_setup":
                    GvSetup(map);
                    return true;
                case "gv_g0":
                    GvG0(map);
                    Wait(5, mode == "gravityperf" ? "gv_perf" : "gv_n");
                    return true;
                case "gv_n":
                    GvBattery(map);
                    if (mode == "gravityn1")
                    {
                        Wait(5, "gv_end");
                        return true;
                    }
                    GvG9(map);
                    Wait(5, "gv_g1");
                    return true;
                case "gv_g1":
                    GvG1(map);
                    GvG2(map);
                    Wait(5, "gv_g3");
                    return true;
                case "gv_g3":
                    GvG3(map);
                    GvG4(map);
                    Wait(5, "gv_g5");
                    return true;
                case "gv_g5":
                    GvG5(map);
                    GvG6(map);
                    Wait(5, "gv_g7");
                    return true;
                case "gv_g7":
                    GvG7(map);
                    GvG8(map);
                    Wait(5, "gv_g10");
                    return true;
                case "gv_g10":
                    GvG10(map);
                    GvG11(map);
                    Wait(5, "gv_g13");
                    return true;
                case "gv_g13":
                    GvG13(map);
                    GvG14(map);
                    GvG14d(map);
                    Wait(5, "gv_p2");
                    return true;
                case "gv_p2":
                    GvP2(map);
                    GvG15Save(map);
                    return true;
                case "gv_g15load":
                    GvG15Check(map);
                    Wait(5, "gv_end");
                    return true;
                case "gv_perf":
                    GvPerf(map);
                    Wait(5, "gv_end");
                    return true;
                case "gv_end":
                    Info("gravity counters at the end: " + Gravity.Counters);
                    GvFinish();
                    return true;
            }
            return false;
        }

        private static void GvSetup(Map map)
        {
            AnnexMapComponent a = ProjectZ.Annex(map);
            rdFloors = a != null;
            rdGz = ProjectZ.GroundZ(map);
            rdStride = a?.stride ?? map.Size.z;
            if (mode == "gravity" && (a == null || a.floors < 3 || a.stride - a.groundZ != 1))
            {
                Result("gravity setup", false, $"needs 3 floors and a 1-row gap (-projectzfloors=3 -projectzmapsize=101); floors {a?.floors}, ground {rdGz}, stride {rdStride}");
                GvFinish();
                return;
            }
            if (mode == "gravityn1" && a != null)
            {
                Result("gravityn1 setup", false, "needs a map without floors (-projectzfloors=1)");
                GvFinish();
                return;
            }
            if (GenCommandLine.TryGetCommandLineArg("projectzgravityref", out string refTag))
            {
                string p = Path.Combine(GenFilePaths.SaveDataFolderPath, "renders", refTag, "gv_fingerprints.txt");
                if (File.Exists(p))
                {
                    gvRef = File.ReadAllLines(p).Select(l => l.Split(new[] { '\t' }, 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
                }
                else
                {
                    Result("reference fingerprints", false, "missing: " + p);
                }
            }
            DebugSettings.enableRandomMentalStates = false;
            Find.Storyteller.storytellerComps.Clear();   // no other incidents during the test (test only)
            Find.PlaySettings.autoRebuild = false;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(p => p.Faction != Faction.OfPlayer).ToList())
            {
                p.Destroy();
            }
            List<Pawn> cols = Colonists(map);
            for (int i = 0; i < cols.Count; i++)
            {
                RdClear(map, CellRect.SingleCell(RdG(1, rdGz / 3 + i)));
                Teleport(cols[i], RdG(1, rdGz / 3 + i));
                Draft(cols[i], true);
            }
            if (mode != "gravityperf")
            {
                RdClear(map, new CellRect(2, rdGz - 14, map.Size.x - 3, 14));
                RdClear(map, new CellRect(2, rdGz / 2 - 9, map.Size.x - 3, 18));
            }
            Info($"gravity: ground {map.Size.x} x {rdGz}, stride {rdStride}, floors {a?.floors ?? 1}; gravity {(GvOff ? "OFF (-projectznogravity)" : "on")}; reference {(gvRef != null ? refTag : "none")}; tick {Find.TickManager.TicksGame}, world seed {Find.World.info.seedString}");
            Wait(30, "gv_g0");
        }

        // ---- G0 the hooks
        private static void GvG0(Map map)
        {
            MethodBase spawn = AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.Spawn), new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool) });
            MethodBase place = AccessTools.Method(typeof(GenPlace), nameof(GenPlace.TryPlaceThing), new[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(ThingPlaceMode), typeof(Thing).MakeByRefType(), typeof(Action<Thing, int>), typeof(Predicate<IntVec3>), typeof(Rot4?), typeof(int) });
            MethodBase filth = AccessTools.Method(typeof(FilthMaker), "TryMakeFilth", new[] { typeof(IntVec3), typeof(Map), typeof(ThingDef), typeof(IEnumerable<string>), typeof(bool), typeof(Filth).MakeByRefType(), typeof(FilthSourceFlags) });
            MethodBase load = AccessTools.Method(typeof(Map), nameof(Map.FinalizeLoading));
            bool Has(MethodBase m, Type t) => m != null && Harmony.GetPatchInfo(m) is HarmonyLib.Patches pi && pi.Prefixes.Concat(pi.Postfixes).Concat(pi.Finalizers).Any(x => x.PatchMethod.DeclaringType == t);
            HarmonyLib.Patches sp = spawn != null ? Harmony.GetPatchInfo(spawn) : null;
            Patch ours = sp?.Prefixes.FirstOrDefault(x => x.PatchMethod.DeclaringType == typeof(GenSpawn_Gravity_Patch));
            Patch net = sp?.Prefixes.FirstOrDefault(x => x.PatchMethod.DeclaringType == typeof(GenSpawn_ShipLocalNet_Patch));
            Result("G0 the four hooks are patched", Has(spawn, typeof(GenSpawn_Gravity_Patch)) && Has(place, typeof(GenPlace_Gravity_Patch)) && Has(filth, typeof(FilthMaker_Gravity_Patch)) && Has(load, typeof(FinalizeLoading_Gravity_Patch)),
                $"spawn {Has(spawn, typeof(GenSpawn_Gravity_Patch))}, place {Has(place, typeof(GenPlace_Gravity_Patch))}, filth {Has(filth, typeof(FilthMaker_Gravity_Patch))}, load {Has(load, typeof(FinalizeLoading_Gravity_Patch))}");
            Result("G0 the spawn hook runs after ShipLocal's net", ours != null && net != null && net.priority > ours.priority, $"net priority {net?.priority}, ours {ours?.priority}; prefixes on GenSpawn.Spawn: {string.Join(", ", sp?.Prefixes.Select(x => x.owner + "/" + x.PatchMethod.DeclaringType.Name + " " + x.priority) ?? new string[0])}");
            Info($"G0 a map with floors seen: {BandRadial.AnyAnnex}; counters: {Gravity.Counters}");
            if (!rdFloors)
            {
                Result("N1 no map with floors: the flag stays off", !BandRadial.AnyAnnex, "");
            }
            else
            {
                Result("G0 a map with floors seen before the first check", BandRadial.AnyAnnex, "");
            }
        }

        // ==================================================================== N1 / N2: the battery
        private static void GvBattery(Map map)
        {
            string tag = rdFloors ? "N2" : "N1";
            int zc = rdGz / 2;
            List<int> levels = rdFloors ? new List<int> { 1, 0 } : new List<int> { 0 };
            int slot = 0;
            long acts = 0, calls = 0;
            foreach (int k in levels)
            {
                string at = k == 0 ? "ground" : "floor 2";
                // drops: Direct onto the next cell, Near on its own
                GvBegin(map, 50 + slot, $"{tag} drops {at}", slot);
                {
                    if (k > 0)
                    {
                        RdSlab(map, new CellRect(rdFix.x0, zc - 5, 11, 11), k);
                    }
                    Pawn a = RdPawn(map, RdOn(rdFix.x0 + 3, zc, k, map));
                    Pawn b = RdPawn(map, RdOn(rdFix.x0 + 6, zc, k, map));
                    a.carryTracker.TryStartCarry(GvStack(ThingDefOf.Steel, 20));
                    b.carryTracker.TryStartCarry(GvStack(ThingDefOf.WoodLog, 15));
                    a.carryTracker.TryDropCarriedThing(RdOn(rdFix.x0 + 4, zc, k, map), ThingPlaceMode.Direct, out Thing _);
                    b.carryTracker.TryDropCarriedThing(b.Position, ThingPlaceMode.Near, out Thing _);
                    RdTicks(30);
                    GvFingerprint(map, $"{tag} drops {at}", new[] { a, b });
                    GvSum(ref acts, ref calls);
                    GvEnd(map);
                }
                slot++;
                // a corpse
                GvBegin(map, 50 + slot, $"{tag} corpse {at}", slot);
                {
                    if (k > 0)
                    {
                        RdSlab(map, new CellRect(rdFix.x0, zc - 5, 11, 11), k);
                    }
                    Pawn v = RdPawn(map, RdOn(rdFix.x0 + 5, zc, k, map), "Gun_Revolver");
                    v.Kill(null);
                    RdTicks(30);
                    GvFingerprint(map, $"{tag} corpse {at}", new[] { v });
                    GvSum(ref acts, ref calls);
                    GvEnd(map);
                }
                slot++;
                // a fight (blood)
                GvBegin(map, 50 + slot, $"{tag} fight {at}", slot);
                {
                    if (k > 0)
                    {
                        RdSlab(map, new CellRect(rdFix.x0, zc - 5, 11, 11), k);
                    }
                    Pawn a = RdPawn(map, RdOn(rdFix.x0 + 4, zc, k, map), draft: false);
                    Pawn b = RdPawn(map, RdOn(rdFix.x0 + 6, zc, k, map), draft: false);
                    GvWorker(a, null);
                    GvWorker(b, null);
                    a.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Berserk, forced: true);
                    b.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Berserk, forced: true);
                    RdTicks(800);
                    GvFingerprint(map, $"{tag} fight {at}", new[] { a, b });
                    GvSum(ref acts, ref calls);
                    GvEnd(map);
                }
                slot++;
                // a frag grenade
                GvBegin(map, 50 + slot, $"{tag} frag {at}", slot);
                {
                    if (k > 0)
                    {
                        RdSlab(map, new CellRect(rdFix.x0, zc - 5, 11, 11), k);
                    }
                    Pawn s = RdPawn(map, RdOn(rdFix.x0 + 2, zc - 3, k, map), "Weapon_GrenadeFrag");
                    Job j = JobMaker.MakeJob(JobDefOf.AttackStatic, RdOn(rdFix.x0 + 7, zc + 2, k, map));
                    j.playerForced = true;
                    s.jobs.TryTakeOrderedJob(j, JobTag.DraftedOrder);
                    RdTicks(400);
                    GvFingerprint(map, $"{tag} frag {at}", new[] { s });
                    GvSum(ref acts, ref calls);
                    GvEnd(map);
                }
                slot++;
            }
            // a jump on one floor (ground for N1, floor 2 for N2)
            {
                int k = rdFloors ? 1 : 0;
                GvBegin(map, 50 + slot, $"{tag} jump", slot);
                if (k > 0)
                {
                    RdSlab(map, new CellRect(rdFix.x0, zc - 5, 11, 11), k);
                }
                Pawn p = RdPawn(map, RdOn(rdFix.x0 + 2, zc, k, map));
                Verb v = GvPackVerb(p, "Apparel_PackJump");
                v?.OrderForceTarget(RdOn(rdFix.x0 + 8, zc, k, map));
                GvTakeOff(p);
                RdTicks(400, () => p.Spawned, 1);
                RdTicks(20);
                GvFingerprint(map, $"{tag} jump", new[] { p });
                GvSum(ref acts, ref calls);
                GvEnd(map);
            }
            if (!rdFloors)
            {
                Result("N1 every gravity counter 0 (the hooks leave at the flag)", acts == 0 && calls == 0 && !BandRadial.AnyAnnex, $"moves/refusals {acts}, hook calls past the flag {calls}");
            }
            else
            {
                Result("N2 well inside a floor and on the ground nothing falls, is refused or dropped", acts == 0, $"moves/refusals {acts}, hook calls {calls}");
            }
        }

        private static void GvSum(ref long acts, ref long calls)
        {
            acts += Gravity.Fell + Gravity.Refused + Gravity.NotSpawned + Gravity.RoofsBroken + Gravity.ThickStops + Gravity.NoLanding;
            calls += Gravity.SpawnCalls + Gravity.PlaceCalls + Gravity.FilthCalls + Gravity.Walks;
        }

        // ==================================================================== G9 a slab collapse (its own fall)
        private static void GvG9(Map map)
        {
            GvBegin(map, 9, "G9 collapse", 6);
            int x0 = rdFix.x0, zc = rdGz / 2;
            RdClear(map, new CellRect(x0, zc - 7, 11, 15));
            RdSlab(map, new CellRect(x0 + 3, zc - 2, 5, 5), 1);
            Thing column = Place(DefDatabase<ThingDef>.GetNamed("Column"), ThingDefOf.WoodLog, RdG(x0 + 5, zc), map);
            Thing steel = GenSpawn.Spawn(GvStack(ThingDefOf.Steel, 20), RdOn(x0 + 4, zc + 1, 1, map), map);
            Pawn up = RdPawn(map, RdOn(x0 + 6, zc - 1, 1, map));
            Pawn worker = RdPawn(map, RdG(x0 + 1, zc), draft: false);
            GvWorker(worker, WorkTypeDefOf.Construction);
            map.designationManager.AddDesignation(new Designation(column, DesignationDefOf.Deconstruct));
            int collapses = PZStats.slabCollapses;
            int t = RdTicks(4000, () => column.Destroyed && !Slab.At(RdOn(x0 + 5, zc, 1, map), map), 10);
            RdTicks(10);
            bool fell = Levels.LevelOf(steel.PositionHeld, map) == 0 && Levels.LevelOf(up.PositionHeld, map) == 0;
            Result("G9 the column taken down: the slab collapses, what stood on it fell (its own fall, unchanged)", column.Destroyed && PZStats.slabCollapses - collapses == 25 && fell && GvOnAir(map).Count == 0,
                $"after {t} ticks, collapses {PZStats.slabCollapses - collapses}; steel {GvAt(steel, map)}, pawn {GvAt(up, map)}; on air {GvList(GvOnAir(map), map)}");
            GvExpect("G9 gravity stands aside (the collapse is scoped)", Gravity.Fell == 0 && Gravity.Refused == 0 && Gravity.RoofsBroken == 0, false, Gravity.Counters);
            GvFingerprint(map, "G9 collapse", new[] { up, worker });
            GvEnd(map);
        }

        // ==================================================================== G1 a jump's landing taken away
        private static void GvG1(Map map)
        {
            GvBegin(map, 1, "G1 jump landing gone", 0);
            int x0 = rdFix.x0, zc = rdGz / 2;
            RdClear(map, new CellRect(x0, zc - 4, 11, 9));
            RdSlab(map, new CellRect(x0 + 1, zc - 1, 3, 3), 2);
            RdSlab(map, new CellRect(x0 + 7, zc - 1, 3, 3), 2);
            Pawn p = RdPawn(map, RdOn(x0 + 2, zc, 2, map));
            Verb v = GvPackVerb(p, "Apparel_PackJump");
            Thing steel = GvStack(ThingDefOf.Steel, 30);
            p.carryTracker.TryStartCarry(steel);
            int inj = Injuries(p);
            IntVec3 target = RdOn(x0 + 8, zc, 2, map);
            v.OrderForceTarget(target);
            PawnFlyer f = GvTakeOff(p);
            if (f == null)
            {
                Result("G1 takes off", false, $"no flight; job {p.CurJobDef?.defName}");
                GvEnd(map);
                return;
            }
            for (int i = 0; i < 600 && f.Spawned && jpTicksFlying(f) < jpTicksTotal(f); i++)
            {
                Find.TickManager.DoSingleTick();
            }
            IntVec3 d = gvDest(f);
            IntVec3 under = FloorRoof.Below(d, map);
            RoofDef roof0 = map.roofGrid.RoofAt(under);
            // the event: the landing slab is taken up between the last destination check and the landing tick
            map.terrainGrid.RemoveFoundation(d, false);
            RoofDef roof1 = map.roofGrid.RoofAt(under);
            RdTicks(60, () => p.Spawned, 1);
            RdTicks(5);
            IntVec3 g = Levels.Ground(d, map);
            GvExpect("G1 the jumper fell to the ground under its vanished landing (2 floors)", p.Spawned && Levels.LevelOf(p.Position, map) == 0 && p.Position.DistanceTo(g) <= 2f, true,
                $"landing {GvCell(d, map)} gone; jumper {GvAt(p, map)}, ground under it {g}");
            GvExpect("G1 the carried steel came down with it", steel.Spawned ? Levels.LevelOf(steel.Position, map) == 0 : p.carryTracker.CarriedThing == steel && p.Spawned && !Gravity.Air(p.Position, map), true, $"steel {GvAt(steel, map)}");
            GvExpect("G1 the jumper is bruised (2 floors)", Gravity.PawnsBruised == 1 && Injuries(p) > inj, true, $"injuries {inj} -> {Injuries(p)}: {InjuryNames(p)}; {Gravity.Counters}");
            GvExpect("G1 the roof passed broke (the ceiling under the landing, an ordinary roof once the slab went)", Gravity.RoofsBroken == 1 && !map.roofGrid.Roofed(under), true,
                $"{under}: {roof0?.defName} -> after removal {roof1?.defName} -> now {map.roofGrid.RoofAt(under)?.defName ?? "none"}");
            GvExpect("G1 nothing on air", GvOnAir(map).Count == 0, true, GvList(GvOnAir(map), map));
            GvEnd(map);
        }

        // ==================================================================== G2 killed in a flight over open air; the void rows
        private static void GvG2(Map map)
        {
            GvBegin(map, 2, "G2 corpse from a flight, void rows", 1);
            int x0 = rdFix.x0, gz = rdGz, zc = gz / 2;
            RdClear(map, new CellRect(x0, zc - 4, 11, 9));
            RdClear(map, new CellRect(x0, gz - 4, 11, 4));
            RdSlab(map, new CellRect(x0 + 8, zc - 1, 2, 3), 1);
            Pawn p = RdPawn(map, RdG(x0 + 1, zc), "Gun_Revolver");
            Verb v = GvPackVerb(p, "Apparel_PackJump");
            v.OrderForceTarget(RdOn(x0 + 8, zc, 1, map));
            PawnFlyer f = GvTakeOff(p);
            IntVec3 over = IntVec3.Invalid;
            for (int i = 0; i < 400 && f != null && f.Spawned; i++)
            {
                Vector3 _ = f.DrawPos;   // (the renderer's read moves the flyer's cell along its flight: JumpFlights keeps it on a band)
                if (Gravity.Air(f.Position, map))
                {
                    over = f.Position;
                    break;
                }
                Find.TickManager.DoSingleTick();
            }
            if (!over.IsValid)
            {
                Result("G2 the flight passes over open air on floor 2", false, $"flyer {f} at {f?.Position}");
            }
            else
            {
                // the event: killed over open air (Pawn.Kill places the corpse Direct at the flyer's cell)
                p.Kill(null);
                if (f.Spawned && f.GetDirectlyHeldThings().Count == 0)
                {
                    Info("G2 the emptied flyer is destroyed (test cleanup)");
                    f.Destroy();
                }
                RdTicks(60);
                Corpse c = p.Corpse;
                // (the game keeps a pawn killed in a flyer inside it: the corpse lands where the flight lands)
                GvExpect("G2 the corpse came down on a floor (the flight's landing or below it), not on air", c != null && c.Spawned && !Gravity.Air(c.Position, map), true,
                    $"killed over {GvCell(over, map)}; corpse {GvAt(c, map)}; {Gravity.Counters}");
            }
            // the void rows (map-row math as another mod's could aim there): the game's own entry points with a void cell
            RdSlab(map, new CellRect(x0 + 7, gz - 2, 2, 2), 1);
            Gravity.ResetCounters();
            IntVec3 v0 = new IntVec3(x0 + 3, 0, gz);                  // the gap above the ground
            IntVec3 v1 = new IntVec3(x0 + 7, 0, rdStride + gz);       // the gap above floor 2, over a slab on its north row
            IntVec3 v2 = new IntVec3(x0 + 9, 0, rdStride + gz);       // the same, over open air
            Thing s0 = GvStack(ThingDefOf.Steel, 3), s1 = GvStack(ThingDefOf.Steel, 4), s2 = GvStack(ThingDefOf.Steel, 5);
            GenPlace.TryPlaceThing(s0, v0, map, ThingPlaceMode.Direct);
            GenPlace.TryPlaceThing(s1, v1, map, ThingPlaceMode.Direct);
            GenPlace.TryPlaceThing(s2, v2, map, ThingPlaceMode.Near);
            Pawn q = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true));
            GenSpawn.Spawn(q, new IntVec3(x0 + 5, 0, gz), map);
            rdFix.pawns.Add(q);
            Draft(q, true);
            RdTicks(5);
            GvExpect("G2 void rows: to the edge row of the floor below the gap (the ground's north row; floor 2's, onto its slab), else further down",
                s0.Spawned && s0.Position == new IntVec3(v0.x, 0, gz - 1) && s1.Spawned && s1.Position == RdOn(v1.x, gz - 1, 1, map) && s2.Spawned && Levels.LevelOf(s2.Position, map) == 0 && q.Spawned && q.Position == new IntVec3(x0 + 5, 0, gz - 1), true,
                $"{GvAt(s0, map)}; {GvAt(s1, map)}; {GvAt(s2, map)}; pawn {GvAt(q, map)}");
            GvExpect("G2 void rows: no bruises landing on its own band's edge row", Gravity.PawnsBruised == 0, false, Gravity.Counters);
            List<Thing> inVoid = RdStrip(map).Where(x => ProjectZ.IsVoid(x, map)).SelectMany(x => map.thingGrid.ThingsListAtFast(x)).Where(t => !Gravity.Exempt(t.def)).ToList();
            GvExpect("G2 nothing in the void, nothing on air", inVoid.Count == 0 && GvOnAir(map).Count == 0, true, $"void {GvList(inVoid, map)}; air {GvList(GvOnAir(map), map)}");
            GvEnd(map);
        }

        // ==================================================================== G3 drops at a slab edge: sky, roofs, thick roof
        private static void GvG3(Map map)
        {
            GvBegin(map, 3, "G3 drops at a slab edge", 2);
            int x0 = rdFix.x0, zc = rdGz / 2;
            RdClear(map, new CellRect(x0, zc - 6, 11, 13));
            RdSlab(map, new CellRect(x0 + 1, zc - 5, 3, 11), 1);
            string[] lanes = { "open sky", "built roof", "thin rock roof", "thick rock roof" };
            RoofDef[] roofs = { null, RoofDefOf.RoofConstructed, DefDatabase<RoofDef>.GetNamed("RoofRockThin"), RoofDefOf.RoofRockThick };
            for (int i = 0; i < lanes.Length; i++)
            {
                int z = zc - 4 + 2 * i;
                IntVec3 g = RdG(x0 + 4, z);
                map.roofGrid.SetRoof(g, roofs[i]);
                Pawn p = RdPawn(map, RdOn(x0 + 3, z, 1, map));
                Thing steel = GvStack(ThingDefOf.Steel, 10 + i);
                p.carryTracker.TryStartCarry(steel);
                int broken0 = Gravity.RoofsBroken, thick0 = Gravity.ThickStops;
                IntVec3 air = RdOn(x0 + 4, z, 1, map);
                // the game's drop of a carried thing (ThingOwner.TryDrop, Direct) onto the open-air cell next to the edge
                p.carryTracker.TryDropCarriedThing(air, ThingPlaceMode.Direct, out Thing dropped);
                RdTicks(5);
                RoofDef now = map.roofGrid.RoofAt(g);
                if (i == 0)
                {
                    GvExpect("G3 open sky: the steel fell to the ground under the air cell", steel.Spawned && steel.Position == g, true, $"steel {GvAt(steel, map)} (want {g})");
                }
                else if (i < 3)
                {
                    GvExpect($"G3 {lanes[i]}: the roof broke like under a drop pod, the steel in the room", steel.Spawned && steel.Position == g && now == null && Gravity.RoofsBroken - broken0 == 1, true,
                        $"steel {GvAt(steel, map)}; roof {roofs[i].defName} -> {now?.defName ?? "none"}, broken {Gravity.RoofsBroken - broken0}");
                }
                else
                {
                    GvExpect("G3 thick rock roof: never broken, the steel stays on its own floor (the nearest cell)", steel.Spawned && Levels.LevelOf(steel.Position, map) == 1 && !Gravity.Air(steel.Position, map) && now == RoofDefOf.RoofRockThick && Gravity.ThickStops - thick0 == 1, true,
                        $"steel {GvAt(steel, map)}; roof {now?.defName ?? "none"}, thick stops {Gravity.ThickStops - thick0}");
                }
            }
            // control: dropped near on its own slab cell
            Pawn c = RdPawn(map, RdOn(x0 + 2, zc, 1, map));
            Thing wood = GvStack(ThingDefOf.WoodLog, 7);
            c.carryTracker.TryStartCarry(wood);
            c.carryTracker.TryDropCarriedThing(c.Position, ThingPlaceMode.Near, out Thing _);
            Result("G3 control: dropped near on its own slab cell, it stays on floor 2", wood.Spawned && Levels.LevelOf(wood.Position, map) == 1 && !Gravity.Air(wood.Position, map), GvAt(wood, map));
            GvExpect("G3 nothing on air", GvOnAir(map).Count == 0, true, GvList(GvOnAir(map), map) + "; " + Gravity.Counters);
            GvEnd(map);
        }

        // ==================================================================== G4 an egg hatching in flight over open air
        private static void GvG4(Map map)
        {
            GvBegin(map, 4, "G4 hatched over open air", 3);
            int x0 = rdFix.x0, zc = rdGz / 2;
            RdClear(map, new CellRect(x0, zc - 4, 11, 9));
            RdSlab(map, new CellRect(x0, zc - 1, 2, 3), 2);
            RdSlab(map, new CellRect(x0 + 9, zc - 1, 2, 3), 2);
            Pawn p = RdPawn(map, RdOn(x0 + 1, zc, 2, map));
            Verb v = GvPackVerb(p, "Apparel_PackJump");
            ThingDef eggDef = DefDatabase<ThingDef>.GetNamedSilentFail("EggChickenFertilized");
            Thing egg = ThingMaker.MakeThing(eggDef);
            CompHatcher hatcher = egg.TryGetComp<CompHatcher>();
            p.carryTracker.TryStartCarry(egg);
            v.OrderForceTarget(RdOn(x0 + 9, zc, 2, map));
            PawnFlyer f = GvTakeOff(p);
            List<Pawn> before = map.mapPawns.AllPawnsSpawned.ToList();
            IntVec3 over = IntVec3.Invalid;
            for (int i = 0; i < 400 && f != null && f.Spawned && !egg.Destroyed; i++)
            {
                Vector3 _ = f.DrawPos;
                if (!over.IsValid && Gravity.Air(f.Position, map) && f.Position.x >= x0 + 4 && f.Position.x <= x0 + 6)
                {
                    over = f.Position;
                    // the event: the egg is due now (its own comp hatches it on its next tick, the flyer ticks its contents)
                    gvGestate(hatcher) = 0.99999994f;
                }
                Find.TickManager.DoSingleTick();
            }
            RdTicks(10);
            Pawn chick = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => !before.Contains(x) && x.def.race?.Animal == true) ?? Find.WorldPawns.AllPawnsAliveOrDead.FirstOrDefault(x => x.def.defName == "Chicken" && x.Dead && x.Corpse?.Map == map);
            Thing body = chick == null ? null : chick.Dead ? (Thing)chick.Corpse : chick;
            GvExpect("G4 the chick hatched over open air fell to the floor below", over.IsValid && body != null && body.Spawned && !Gravity.Air(body.Position, map) && Levels.LevelOf(body.Position, map) < 2, true,
                $"hatched over {GvCell(over, map)}; chick {GvAt(body, map)}; egg destroyed {egg.Destroyed}; {Gravity.Counters}");
            GvExpect("G4 the chick is bruised by the fall (or killed by it)", chick != null && (chick.Dead || Gravity.PawnsBruised >= 1 && Injuries(chick) > 0), true, chick == null ? "no chick" : $"dead {chick.Dead}, injuries {Injuries(chick)} {InjuryNames(chick)}");
            if (chick != null && !chick.Destroyed)
            {
                rdFix.pawns.Add(chick);
            }
            GvEnd(map);
        }

        // ==================================================================== G5 a centre-drop raid onto a slab
        private static void GvG5(Map map)
        {
            GvBegin(map, 5, "G5 raid drop pods", 4);
            int x0 = rdFix.x0, zc = rdGz / 2;
            RdClear(map, new CellRect(x0, zc - 7, 11, 15));
            RdSlab(map, new CellRect(x0, zc - 6, 11, 13), 1);
            List<Pawn> before = map.mapPawns.AllPawnsSpawned.ToList();
            bool fired = false;
            Faction enemy = null;
            IncidentParms parms = null;
            foreach (Faction ef in Find.FactionManager.AllFactions.Where(x => x.HostileTo(Faction.OfPlayer) && !x.def.hidden && x.def.humanlikeFaction && !x.temporary && x.def.pawnGroupMakers != null && x.def.techLevel >= TechLevel.Industrial).OrderBy(x => x.loadID))
            {
                parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
                parms.faction = ef;
                parms.raidStrategy = RaidStrategyDefOf.ImmediateAttack;
                parms.raidArrivalMode = PawnsArrivalModeDefOf.CenterDrop;
                parms.spawnCenter = RdOn(x0 + 5, zc, 1, map);
                parms.points = 300f;
                parms.forced = true;
                if (IncidentDefOf.RaidEnemy.Worker.TryExecute(parms))
                {
                    fired = true;
                    enemy = ef;
                    break;
                }
            }
            int t = RdTicks(900, () => !map.listerThings.ThingsInGroup(ThingRequestGroup.ActiveTransporter).Any() && !map.listerThings.AllThings.Any(x => x is Skyfaller), 10);
            List<Pawn> raiders = map.mapPawns.AllPawnsSpawned.Where(x => !before.Contains(x) && x.Faction == enemy).ToList();
            Result("G5 the raid came down: every raider on a floor cell", fired && raiders.Count > 0 && raiders.All(r => !Gravity.Air(r.Position, map)),
                $"fired {fired} at {GvCell(parms.spawnCenter, map)}, after {t} ticks {raiders.Count} raiders: {string.Join(", ", raiders.Take(6).Select(r => GvCell(r.Position, map)))}");
            GvExpect("G5 the drop rule decided: gravity moved and refused nothing", Gravity.Fell == 0 && Gravity.Refused == 0, false, Gravity.Counters);
            foreach (Pawn r in raiders)
            {
                r.Destroy();
            }
            GvEnd(map);
        }

        // ==================================================================== G6 filth at floor 2's north edge (1-row gap to floor 3)
        private static void GvG6(Map map)
        {
            GvBegin(map, 6, "G6 filth at the edge", 5);
            int x0 = rdFix.x0, gz = rdGz;
            // floor 2: slab up to the row before its north edge row (that row stays open air), floor 3: its south rows
            RdSlab(map, new CellRect(x0, gz - 6, 8, 6), 1);
            RdSlab(map, new CellRect(x0 + 8, gz - 6, 3, 4), 1);
            RdSlab(map, new CellRect(x0, 0, 11, 4), 2);
            // walls on floor 2's north row (filth on a wall cell spreads to a neighbour: the void row next to it is refused)
            for (int x = x0 + 1; x <= x0 + 6; x++)
            {
                Place(ThingDefOf.Wall, ThingDefOf.WoodLog, RdOn(x, gz - 1, 1, map), map);
            }
            Pawn a = RdPawn(map, RdOn(x0 + 3, gz - 3, 1, map), draft: false);
            Pawn b = RdPawn(map, RdOn(x0 + 4, gz - 3, 1, map), draft: false);
            GvWorker(a, null);
            GvWorker(b, null);
            a.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Berserk, forced: true);
            b.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Berserk, forced: true);
            // a colonist throws frag grenades at the worn walls: they break, their rubble (the game's leavings on a wall cell)
            // spreads to a neighbour, never onto air or into the void
            foreach (IntVec3 c in RdStrip(map))
            {
                Building wall = c.GetEdifice(map);
                if (wall != null && wall.def == ThingDefOf.Wall)
                {
                    wall.HitPoints = 3;
                }
            }
            Pawn thrower = RdPawn(map, RdOn(x0 + 3, gz - 6, 1, map), "Weapon_GrenadeFrag");
            Job throwJob = JobMaker.MakeJob(JobDefOf.AttackStatic, RdOn(x0 + 3, gz - 2, 1, map));
            throwJob.playerForced = true;
            thrower.jobs.TryTakeOrderedJob(throwJob, JobTag.DraftedOrder);
            ThingDef popperDef = DefDatabase<ThingDef>.GetNamedSilentFail("FirefoamPopper");
            Thing popper = popperDef == null ? null : Place(popperDef, null, RdOn(x0 + 6, gz - 3, 1, map), map);
            popper?.TakeDamage(new DamageInfo(DamageDefOf.Flame, 1f));
            int t = RdTicks(1500);
            IEnumerable<Thing> filth = RdStrip(map).SelectMany(c => map.thingGrid.ThingsListAtFast(c)).Where(x => x is Filth);
            int onAir = filth.Count(x => Gravity.Air(x.Position, map));
            int onF3 = filth.Count(x => Levels.LevelOf(x.Position, map) == 2);
            int onGround = filth.Count(x => Levels.LevelOf(x.Position, map) == 0);
            int onF2 = filth.Count(x => Levels.LevelOf(x.Position, map) == 1 && !Gravity.Air(x.Position, map));
            string detail = $"after {t} ticks: popper {(popper == null ? "none" : popper.Destroyed ? "popped" : "not popped")}; filth on floor 2 {onF2}, on air {onAir}, on floor 3 {onF3}, on the ground {onGround}; {Gravity.Counters}";
            Result("G6 no filth on air or in the void", onAir == 0, detail);
            if (!GvOff)
            {
                Result("G6 none spread across the gap onto floor 3", onF3 == 0, detail);
            }
            else if (onF3 > 0)
            {
                Result("LEAK G6 filth spread across the gap", true, detail);
            }
            else
            {
                Info("G6 without gravity: no leak seen with this seed; " + detail);
            }
            // G6b: filth aimed at void cells of the 1-row gap (as a mod's map-row radius would aim it), through the game's
            // own FilthMaker: without gravity it spreads to a random neighbour, on floor 2's north row or floor 3's south row
            int f3 = FilthOnLevel(map, 2), f2 = FilthOnLevel(map, 1);
            int ns0 = Gravity.NotSpawned;
            int made = 0;
            for (int x = x0; x <= x0 + 10; x++)
            {
                for (int n = 0; n < 2; n++)
                {
                    if (FilthMaker.TryMakeFilth(new IntVec3(x, 0, rdStride + gz), map, ThingDefOf.Filth_Blood))
                    {
                        made++;
                    }
                }
            }
            string d6b = $"22 aimed at the gap: made {made}; new filth on floor 3 {FilthOnLevel(map, 2) - f3}, on floor 2 {FilthOnLevel(map, 1) - f2}; refused {Gravity.NotSpawned - ns0}";
            GvExpect("G6b filth aimed at the void is not made and never spreads onto a floor", made == 0 && FilthOnLevel(map, 2) == f3 && FilthOnLevel(map, 1) == f2 && Gravity.NotSpawned - ns0 == 22, true, d6b);
            GvEnd(map);
        }

        private static int FilthOnLevel(Map map, int L) => RdStrip(map).SelectMany(c => map.thingGrid.ThingsListAtFast(c)).Count(x => x is Filth && Levels.LevelOf(x.Position, map) == L);

        // ==================================================================== G7 a turret pack thrown at open air (Anomaly)
        private static void GvG7(Map map)
        {
            if (!ModsConfig.AnomalyActive || DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_PackTurret") == null)
            {
                Info("G7 skipped: Anomaly off");
                return;
            }
            GvBegin(map, 7, "G7 turret pack at open air", 6);
            int x0 = rdFix.x0, zc = rdGz / 2;
            RdClear(map, new CellRect(x0, zc - 5, 11, 11));
            RdSlab(map, new CellRect(x0 + 1, zc - 3, 4, 7), 1);
            ThingDef turret = DefDatabase<ThingDef>.GetNamed("Turret_TacticalTurret");
            int err0 = errors;
            string[] names = { "open air", "a slab (control)" };
            IntVec3[] targets = { RdOn(x0 + 8, zc, 1, map), RdOn(x0 + 1, zc + 3, 1, map) };
            for (int i = 0; i < 2; i++)
            {
                Pawn p = RdPawn(map, RdOn(x0 + 3, zc - 2 + 3 * i, 1, map));
                Verb v = GvPackVerb(p, "Apparel_PackTurret");
                int ref0 = Gravity.Refused;
                bool valid = v != null && v.ValidateTarget(targets[i], false);
                if (valid)
                {
                    v.OrderForceTarget(targets[i]);
                }
                else
                {
                    v?.TryStartCastOn(targets[i]);
                }
                int have = map.listerThings.ThingsOfDef(turret).Count;
                RdTicks(400, () => map.listerThings.ThingsOfDef(turret).Count > have || Gravity.Refused > ref0, 5);
                RdTicks(30);
                List<Thing> here = map.listerThings.ThingsOfDef(turret).Where(x => x.Position.x >= x0 && x.Position.x <= x0 + 10).ToList();
                if (i == 0)
                {
                    // (the game refuses open air as the throw's target; aimed there anyway, ProjectZ's flight carries the capsule
                    // down to the floor below, so the spawn lands on a floor: gravity is the net under it)
                    GvExpect("G7 no turret on open air (it came down on the floor below, or was refused there)", here.All(x => !Gravity.Air(x.Position, map)) && (here.Count == 1 && Levels.LevelOf(here[0].Position, map) == 0 || Gravity.Refused - ref0 == 1), true,
                        $"aimed at {GvCell(targets[i], map)} (target accepted by the verb: {valid}); turrets {GvList(here, map)}; {Gravity.Counters}");
                }
                else
                {
                    Result("G7 control: thrown at a slab cell, the turret stands there", here.Count(x => Levels.LevelOf(x.Position, map) == 1 && !Gravity.Air(x.Position, map)) == 1, $"turrets {GvList(here, map)}");
                }
            }
            Result("G7 no error logged", errors == err0, $"errors {errors - err0}");
            GvEnd(map);
        }

        // ==================================================================== G8 minified install on floor 2
        private static void GvG8(Map map)
        {
            GvBegin(map, 8, "G8 uninstall and install", 7);
            int x0 = rdFix.x0, zc = rdGz / 2;
            RdSlab(map, new CellRect(x0 + 1, zc - 3, 9, 7), 1);
            Thing table = Place(DefDatabase<ThingDef>.GetNamed("EndTable"), ThingDefOf.WoodLog, RdOn(x0 + 3, zc, 1, map), map);
            Pawn w = RdPawn(map, RdOn(x0 + 5, zc - 2, 1, map), draft: false);
            GvWorker(w, WorkTypeDefOf.Construction);
            map.designationManager.AddDesignation(new Designation(table, DesignationDefOf.Uninstall));
            int t1 = RdTicks(4000, () => table.ParentHolder is MinifiedThing m && m.Spawned, 10);
            MinifiedThing mini = table.ParentHolder as MinifiedThing;
            IntVec3 to = RdOn(x0 + 7, zc + 1, 1, map);
            Blueprint_Install bp = mini != null ? GenConstruct.PlaceBlueprintForInstall(mini, to, map, Rot4.North, Faction.OfPlayer) : null;
            int t2 = RdTicks(6000, () => table.Spawned && table.Position == to, 10);
            Result("G8 a colonist uninstalls the table and installs it on another floor-2 cell", table.Spawned && table.Position == to, $"minified after {t1} ticks ({GvAt(mini, map)}), installed after {t2}: {GvAt(table, map)}");
            GvExpect("G8 gravity untouched", Gravity.Fell == 0 && Gravity.Refused == 0 && Gravity.NotSpawned == 0, false, Gravity.Counters);
            GvEnd(map);
        }

        // ==================================================================== G10 slab and deck blueprints on open air
        private static void GvG10(Map map)
        {
            GvBegin(map, 10, "G10 slab blueprints", 5);
            int x0 = rdFix.x0, zc = rdGz / 2;
            RdClear(map, new CellRect(x0, zc - 5, 11, 11));
            RdSlab(map, new CellRect(x0 + 1, zc - 2, 4, 5), 1);
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, RdG(x0 + 4, zc), map);
            GenSpawn.Spawn(GvStack(ThingDefOf.WoodLog, 40), RdOn(x0 + 2, zc + 2, 1, map), map);
            Pawn w = RdPawn(map, RdOn(x0 + 2, zc - 2, 1, map), draft: false);
            GvWorker(w, WorkTypeDefOf.Construction);
            Designator_Build d = BuildCopyCommandUtility.FindAllowedDesignator(PZDefOf.PZ_UpperFloor);
            List<IntVec3> cells = new List<IntVec3> { RdOn(x0 + 5, zc - 1, 1, map), RdOn(x0 + 5, zc, 1, map), RdOn(x0 + 5, zc + 1, 1, map) };
            List<string> refused = new List<string>();
            foreach (IntVec3 c in cells)
            {
                AcceptanceReport r = d?.CanDesignateCell(c) ?? "no designator";
                if (r.Accepted)
                {
                    d.DesignateSingleCell(c);
                }
                else
                {
                    refused.Add($"{c}: {r.Reason}");
                }
            }
            int exempt0 = Gravity.ExemptFloorBuild;
            int t = RdTicks(8000, () => cells.All(c => Slab.At(c, map)), 20);
            Result("G10 slab blueprints placed over open air with the designator, built by a colonist", refused.Count == 0 && cells.All(c => Slab.At(c, map)), $"after {t} ticks: {string.Join(", ", cells.Select(c => c.GetTerrain(map).defName))}; refused {string.Join("; ", refused)}");
            if (!GvOff)
            {
                Result("G10 blueprints and frames of a floor untouched on open air (none refused)", Gravity.Refused == 0 && Gravity.ExemptFloorBuild > 0, Gravity.Counters);
            }
            if (ModsConfig.OdysseyActive && PZDefOf.PZ_ShipDeck != null)
            {
                IntVec3 g = RdG(x0 + 8, zc);
                map.terrainGrid.SetTerrain(g, DefDatabase<TerrainDef>.GetNamed("Substructure"));
                IntVec3 u = RdOn(x0 + 8, zc, 1, map);
                int e0 = Gravity.ExemptFloorBuild;
                Blueprint_Build bp = GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_ShipDeck, u, map, Rot4.North, Faction.OfPlayer, null);
                Result("G10 a ship deck blueprint spawns on open air over substructure (Odyssey)", bp != null && bp.Spawned && bp.Position == u && (GvOff || Gravity.ExemptFloorBuild - e0 == 1), $"blueprint {GvAt(bp, map)}; {Gravity.Counters}");
                bp?.Destroy(DestroyMode.Cancel);
            }
            GvEnd(map);
        }

        // ==================================================================== G11 stairs
        private static void GvG11(Map map)
        {
            GvBegin(map, 11, "G11 stairs", 1);
            int x0 = rdFix.x0, zc = rdGz / 2;
            RdClear(map, new CellRect(x0, zc - 6, 11, 13));
            RdSlab(map, new CellRect(x0 + 7, zc + 2, 3, 3), 1);
            Place(ThingDefOf.Wall, ThingDefOf.WoodLog, RdG(x0 + 9, zc + 4), map);
            GenSpawn.Spawn(GvStack(ThingDefOf.WoodLog, 75), RdG(x0 + 5, zc - 5), map);
            GenSpawn.Spawn(GvStack(ThingDefOf.WoodLog, 75), RdG(x0 + 6, zc - 5), map);
            Pawn w = RdPawn(map, RdG(x0 + 5, zc - 4), draft: false);
            GvWorker(w, WorkTypeDefOf.Construction);
            IntVec3[] at = { RdG(x0 + 2, zc - 1), RdG(x0 + 8, zc - 1) };
            List<string> placed = new List<string>();
            foreach (IntVec3 c in at)
            {
                AcceptanceReport r = GenConstruct.CanPlaceBlueprintAt(PZDefOf.PZ_Stairs, c, Rot4.North, map, stuffDef: ThingDefOf.WoodLog);
                if (r.Accepted)
                {
                    GenConstruct.PlaceBlueprintForBuild(PZDefOf.PZ_Stairs, c, map, Rot4.North, Faction.OfPlayer, ThingDefOf.WoodLog);
                }
                placed.Add($"{c}: {(r.Accepted ? "placed" : r.Reason)}");
            }
            int t = RdTicks(15000, () => at.All(c => c.GetFirstThing<Building_Stairs>(map) != null), 50);
            List<Building_Stairs> built = at.Select(c => c.GetFirstThing<Building_Stairs>(map)).ToList();
            Result("G11 a colonist builds stairs in the open field (the landing laid) and onto a slab edge", built.All(s => s != null && Slab.At(s.TopCell, map)), $"after {t} ticks: {string.Join("; ", placed)}; {string.Join(", ", built.Select(s => s == null ? "not built" : "landing " + GvCell(s.TopCell, map) + " " + s.TopCell.GetTerrain(map).defName))}");
            GvExpect("G11 gravity untouched", Gravity.Fell == 0 && Gravity.Refused == 0 && Gravity.NotSpawned == 0, false, Gravity.Counters);
            GvEnd(map);
        }

        // ==================================================================== G13 fires at band edges
        private static void GvG13(Map map)
        {
            GvBegin(map, 13, "G13 fires at band edges", 2);
            int x0 = rdFix.x0, gz = rdGz;
            RdClear(map, new CellRect(x0, gz - 6, 11, 6));
            RdGrass(map, new CellRect(x0, gz - 4, 11, 4).Cells);
            RdSlab(map, new CellRect(x0 + 2, 0, 7, 3), 1);
            for (int x = x0 + 2; x <= x0 + 8; x++)
            {
                RdWood(map, RdOn(x, 0, 1, map), 20);
            }
            FireUtility.TryStartFireIn(RdG(x0 + 5, gz - 1), map, 1f, null);
            FireUtility.TryStartFireIn(RdOn(x0 + 5, 0, 1, map), map, 1f, null);
            int t = RdTicks(1500);
            List<Thing> fires = RdStrip(map).SelectMany(c => map.thingGrid.ThingsListAtFast(c)).Where(x => x is Fire && ((Fire)x).parent == null).ToList();
            List<Thing> bad = fires.Where(x => Gravity.Air(x.Position, map)).ToList();
            Result("G13 no fire on air or in the void", bad.Count == 0, $"after {t} ticks: fires {fires.Count}, on air {GvList(bad, map)}; {Gravity.Counters}");
            GvExpect("G13 nothing fell", Gravity.Fell == 0, false, Gravity.Counters);
            GvEnd(map);
        }

        // ==================================================================== G14 a slab taken up under a pawn and steel
        private static void GvG14(Map map)
        {
            GvBegin(map, 14, "G14 slab taken up", 3);
            int x0 = rdFix.x0, zc = rdGz / 2;
            RdClear(map, new CellRect(x0, zc - 6, 11, 13));
            // stairs (god mode: built at once, their landing laid) and a slab strip east of the landing
            Place(PZDefOf.PZ_Stairs, ThingDefOf.WoodLog, RdG(x0 + 2, zc - 2), map, Rot4.North);
            RdSlab(map, new CellRect(x0 + 3, zc, 5, 2), 1);
            IntVec3 slab = RdOn(x0 + 7, zc, 1, map);
            IntVec3 under = FloorRoof.Below(slab, map);
            Pawn v = RdPawn(map, slab);
            Thing steel = GenSpawn.Spawn(GvStack(ThingDefOf.Steel, 25), slab, map);
            Pawn w = RdPawn(map, RdG(x0 + 5, zc - 4), draft: false);
            GvWorker(w, WorkTypeDefOf.Construction);
            int inj = Injuries(v);
            new Designator_RemoveFoundation().DesignateSingleCell(slab);
            int checks = 0, bad = 0;
            string badLine = "";
            int t = 0;
            for (; t < 5000 && Slab.At(slab, map); t++)
            {
                Find.TickManager.DoSingleTick();
                if (t % 15 == 0)
                {
                    RdFrame(map);
                }
                Job j = w.CurJob;
                if (j != null && j.def == JobDefOf.RemoveFoundation && j.targetA.Cell == slab && !w.pather.Moving && w.jobs.curDriver?.CurToilIndex >= 1)
                {
                    checks++;
                    if (w.Position == slab || !w.Position.AdjacentToCardinal(slab))
                    {
                        bad++;
                        badLine = $"{w} at {w.Position} (spot {j.targetB})";
                    }
                }
            }
            RdTicks(10);
            int wood = RdStrip(map).SelectMany(c => map.thingGrid.ThingsListAtFast(c)).Where(x => x.def == ThingDefOf.WoodLog && Levels.LevelOf(x.Position, map) == 0).Sum(x => x.stackCount);
            Result("G14 the remover worked from the next cell, never on the slab", checks > 0 && bad == 0, $"{checks} work ticks, {bad} bad {badLine}");
            GvExpect("G14 the pawn on it fell to the ground, bruised (user 2026-10-18: no more stepping off)", !Slab.At(slab, map) && v.Spawned && Levels.LevelOf(v.Position, map) == 0 && Gravity.PawnsBruised >= 1 && Injuries(v) > inj, true,
                $"removed after {t} ticks; pawn {GvAt(v, map)}, injuries {inj} -> {Injuries(v)}");
            GvExpect("G14 the steel on it fell to the ground", steel.Spawned && Levels.LevelOf(steel.Position, map) == 0, true, GvAt(steel, map));
            GvExpect("G14 the slab's materials (half its 4 wood) fell to the ground", wood >= 2, true, $"wood on the ground {wood}");
            GvExpect("G14 the ceiling below (an ordinary roof once the slab went) broke under the falls", !map.roofGrid.Roofed(under) && Gravity.RoofsBroken == 1, true, $"{under}: {map.roofGrid.RoofAt(under)?.defName ?? "none"}; {Gravity.Counters}");
            GvExpect("G14 nothing on air", GvOnAir(map).Count == 0, true, GvList(GvOnAir(map), map));
            GvEnd(map);
        }

        // ==================================================================== G14d a ship deck taken up (Odyssey)
        private static void GvG14d(Map map)
        {
            if (!ModsConfig.OdysseyActive || PZDefOf.PZ_ShipDeck == null)
            {
                Info("G14d skipped: Odyssey off");
                return;
            }
            GvBegin(map, 15, "G14d deck taken up", 4);
            int x0 = rdFix.x0, zc = rdGz / 2;
            RdClear(map, new CellRect(x0, zc - 6, 11, 13));
            TerrainDef sub = DefDatabase<TerrainDef>.GetNamed("Substructure");
            CellRect ship = new CellRect(x0 + 3, zc - 1, 4, 3);
            foreach (IntVec3 g in ship)
            {
                map.terrainGrid.SetTerrain(g, sub);
                map.roofGrid.SetRoof(g, PZDefOf.PZ_FloorRoof);
                map.terrainGrid.SetTerrain(RdOn(g.x, g.z, 1, map), PZDefOf.PZ_ShipDeck);
                map.fogGrid.Unfog(RdOn(g.x, g.z, 1, map));
            }
            Place(ThingDefOf.Wall, ThingDefOf.Steel, RdG(x0 + 2, zc), map);
            IntVec3 deck = RdOn(x0 + 5, zc, 1, map);
            Pawn v = RdPawn(map, deck);
            Pawn w = RdPawn(map, RdOn(x0 + 3, zc + 1, 1, map), draft: false);
            GvWorker(w, WorkTypeDefOf.Construction);
            int inj = Injuries(v);
            new Designator_RemoveFoundation().DesignateSingleCell(deck);
            bool designated = map.designationManager.DesignationAt(deck, DesignationDefOf.RemoveFoundation) != null;
            int checks = 0, bad = 0;
            string badLine = "";
            int t = 0;
            for (; t < 5000 && ShipDeck.At(deck, map); t++)
            {
                Find.TickManager.DoSingleTick();
                if (t % 15 == 0)
                {
                    RdFrame(map);
                }
                Job j = w.CurJob;
                if (j != null && j.def == JobDefOf.RemoveFoundation && j.targetA.Cell == deck && !w.pather.Moving && w.jobs.curDriver?.CurToilIndex >= 1)
                {
                    checks++;
                    if (w.Position == deck || !w.Position.AdjacentToCardinal(deck))
                    {
                        bad++;
                        badLine = $"{w} at {w.Position} (spot {j.targetB})";
                    }
                }
            }
            RdTicks(10);
            Result("G14d the remover worked from the next deck cell, never on the deck it took up", designated && checks > 0 && bad == 0, $"designated {designated}; {checks} work ticks, {bad} bad {badLine}; deck now {deck.GetTerrain(map).defName} after {t} ticks");
            GvExpect("G14d the pawn on it fell to the ground floor (the substructure), bruised", !ShipDeck.At(deck, map) && v.Spawned && Levels.LevelOf(v.Position, map) == 0 && Injuries(v) > inj, true, $"pawn {GvAt(v, map)}, injuries {inj} -> {Injuries(v)}; {Gravity.Counters}");
            GvExpect("G14d nothing on air", GvOnAir(map).Count == 0, true, GvList(GvOnAir(map), map));
            GvEnd(map);
        }

        // ==================================================================== P2 micro cost
        private static void GvP2(Map map)
        {
            int zc = rdGz / 2;
            GvBegin(map, 20, "P2 micro", 5);
            int x0 = rdFix.x0;
            RdSlab(map, new CellRect(x0 + 1, zc, 2, 2), 1);
            IntVec3[] cells = { RdG(x0 + 1, zc), RdOn(x0 + 1, zc, 1, map), RdOn(x0 + 6, zc, 2, map) };
            string[] names = { "ground", "floor", "air (2 floors)" };
            const int n = 1000000;
            for (int i = 0; i < cells.Length; i++)
            {
                IntVec3 c = cells[i];
                bool sink = false;
                Stopwatch sw = Stopwatch.StartNew();
                for (int k = 0; k < n; k++)
                {
                    sink ^= Gravity.Air(c, map);
                }
                double air = sw.Elapsed.TotalMilliseconds * 1e6 / n;
                sw.Restart();
                int f = 0;
                for (int k = 0; k < n; k++)
                {
                    f += Column.Landing(c, map, true, false, out int fallen, out IntVec3 _).x + fallen;
                }
                double land = sw.Elapsed.TotalMilliseconds * 1e6 / n;
                Info($"P2 {names[i]} {GvCell(c, map)}: Gravity.Air {air:0.0} ns, Column.Landing {land:0.0} ns per call ({sink}{f % 2})");
            }
            Result("P2 measured", true, "");
            GvEnd(map);
        }

        // ==================================================================== G15 save and reload
        private static void GvG15Save(Map map)
        {
            GvBegin(map, 16, "G15 save/load", 6);
            int x0 = rdFix.x0, zc = rdGz / 2;
            RdClear(map, new CellRect(x0, zc - 4, 11, 9));
            RdSlab(map, new CellRect(x0 + 1, zc - 2, 3, 5), 1);
            Pawn p = RdPawn(map, RdOn(x0 + 3, zc, 1, map));
            Thing steel = GvStack(ThingDefOf.Steel, 12);
            p.carryTracker.TryStartCarry(steel);
            p.carryTracker.TryDropCarriedThing(RdOn(x0 + 4, zc, 1, map), ThingPlaceMode.Direct, out Thing _);
            Pawn faller = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true));
            GenSpawn.Spawn(faller, RdOn(x0 + 6, zc, 1, map), map);
            rdFix.pawns.Add(faller);
            Draft(faller, true);
            Thing stray = GvStack(ThingDefOf.Plasteel, 5);
            Gravity.Suspend();
            try
            {
                GenSpawn.Spawn(stray, RdOn(x0 + 8, zc, 1, map), map);   // an old save's stray (left on open air)
            }
            finally
            {
                Gravity.Resume();
            }
            RdTicks(30);
            gvSaved.Clear();
            foreach (Thing t in new[] { p, steel, faller, stray })
            {
                if (t.Spawned)
                {
                    gvSaved[t.thingIDNumber] = t.Position;
                }
            }
            gvStray = stray.thingIDNumber;
            Info($"G15 before saving: steel {GvAt(steel, map)}, faller {GvAt(faller, map)}, stray {GvAt(stray, map)}; {Gravity.Counters}");
            GvExpect("G15 before saving: the steel and the pawn fell, the stray is on air", Levels.LevelOf(steel.Position, map) == 0 && Levels.LevelOf(faller.Position, map) == 0 && Gravity.Air(stray.Position, map), true, Gravity.Counters);
            RdEndSeed();
            GameDataSaveLoader.SaveGame("pz_gravity_g15");
            Gravity.ResetCounters();
            Traverse.Create(typeof(Prefs)).Field("data").Field("pauseOnLoad").SetValue(true);
            pendingLoad = "pz_gravity_g15";
            stage = "gv_g15load";
            rdFix = null;
        }

        private static void GvG15Check(Map map)
        {
            gvLoadFell = Gravity.Fell;
            gvLoadRefused = Gravity.Refused;
            int same = 0, moved = 0;
            List<string> lines = new List<string>();
            foreach (KeyValuePair<int, IntVec3> kv in gvSaved)
            {
                Thing t = map.listerThings.AllThings.FirstOrDefault(x => x.thingIDNumber == kv.Key) ?? map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == kv.Key);
                if (t != null && t.Spawned && t.Position == kv.Value)
                {
                    same++;
                }
                else
                {
                    moved++;
                    lines.Add($"{kv.Key}: saved {kv.Value}, now {GvAt(t, map)}");
                }
            }
            Thing stray = map.listerThings.AllThings.FirstOrDefault(x => x.thingIDNumber == gvStray);
            Result("G15 after the reload every thing is where it was saved", moved == 0 && same == gvSaved.Count, $"{same} same, {moved} moved {string.Join("; ", lines)}");
            Result("G15 the old save's stray stays on air (load never moves anything)", stray != null && stray.Spawned && Gravity.Air(stray.Position, map), GvAt(stray, map));
            Result("G15 nothing fell or was refused during the load", gvLoadFell == 0 && gvLoadRefused == 0 && (GvOff || Gravity.OnAirLoading >= 1), Gravity.Counters);
            string path = GenFilePaths.FilePathForSavedGame("pz_gravity_g15");
            Info("G15 save file (the harness deletes it by name): " + path);
        }

        // ==================================================================== P1 (mode gravityperf)
        private static readonly List<double> gvTickMs = new List<double>();

        private static void GvPerf(Map map)
        {
            RdBegin(map, 190, "P1 perf", 0);
            AnnexMapComponent a = ProjectZ.Annex(map);
            CellRect area = new CellRect(map.Size.x / 2 - 20, rdGz / 2 - 20, 40, 40).ClipInsideMap(map);
            RdClear(map, area);
            if (a != null)
            {
                RdSlab(map, area);
            }
            List<Pawn> fighters = new List<Pawn>();
            for (int i = 0; i < 30; i++)
            {
                int k = a != null ? i % Math.Min(2, a.floors) : 0;
                IntVec3 g = new IntVec3(area.minX + 2 + i % 6 * 6, 0, area.minZ + 2 + i / 6 * 6);
                IntVec3 at = RdOn(g.x, g.z, k, map);
                if (!at.Standable(map))
                {
                    at = CellFinder.StandableCellNear(at, map, 5f);
                }
                if (at.IsValid)
                {
                    Pawn p = RdPawn(map, at, draft: false);
                    GvWorker(p, null);
                    fighters.Add(p);
                }
            }
            foreach (Pawn p in fighters)
            {
                p.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Berserk, forced: true);
            }
            RdTicks(300);
            Gravity.ResetCounters();
            gvTickMs.Clear();
            Stopwatch sw = new Stopwatch();
            TickManager tm = Find.TickManager;
            for (int i = 0; i < 2500; i++)
            {
                sw.Restart();
                tm.DoSingleTick();
                gvTickMs.Add(sw.Elapsed.TotalMilliseconds);
                if (i % 15 == 0)
                {
                    RdFrame(map);
                }
            }
            List<double> s = gvTickMs.OrderBy(x => x).ToList();
            Info($"P1 {fighters.Count} colonists fighting, floors {a?.floors ?? 1}, gravity {(GvOff ? "off" : "on")}: median {s[s.Count / 2]:0.000} ms per tick, mean {s.Average():0.000}, p90 {s[(int)(s.Count * 0.9)]:0.000}");
            Info($"P1 hook calls per tick on a map with floors: spawn {Gravity.SpawnCalls / 2500.0:0.00}, place {Gravity.PlaceCalls / 2500.0:0.00}, filth {Gravity.FilthCalls / 2500.0:0.00}, column walks {Gravity.Walks / 2500.0:0.000}; {Gravity.Counters}");
            Result("P1 measured", true, $"{gvTickMs.Count} ticks");
            RdEnd(map);
        }
    }
}
