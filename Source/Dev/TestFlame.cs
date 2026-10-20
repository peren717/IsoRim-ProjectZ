using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ProjectZ.Combat;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ProjectZ.Dev
{
    // Mode flamediag (DIAGNOSTIC, report only; run with -projectzfloors=3 -projectzseed=717). Directional effects between
    // floors: a 7x7 tower with a slab on floor 2 at the south of a cleared field. Colonist A on floor 2 at the tower's
    // north edge, B on the ground. Every case is a real order (ability cast job / AttackStatic) run on real ticks against
    // a stunned raider; logged: the cone/area cells (floors, ground direction vs the true direction to the target), the
    // explosion's centre and override cells, fires started, damage taken, flecks and motes made (positions, angles),
    // beam hit cells and incinerator sprayer motions. Screenshot in the 3D view at the cast.
    public static partial class TestRunner
    {
        private sealed class FlameCase
        {
            public string name;
            public string ability;   // AbilityDef (cast), or null
            public string gun;       // weapon (AttackStatic), or null
            public char shooter;     // 'A' floor 2, 'B' ground
            public int tdx, tdz;     // target ground offset from the tower's north edge centre
            public int tk;           // target floor (0 ground, 1 floor 2)
            public int bdx, bdz;     // B's ground offset (shooter B)
        }

        private static readonly FlameCase[] fzCases =
        {
            new FlameCase { name = "firespew_down", ability = "FireSpew", shooter = 'A', tdx = 3, tdz = 4, tk = 0 },
            new FlameCase { name = "firespew_down_east", ability = "FireSpew", shooter = 'A', tdx = 5, tdz = 1, tk = 0 },
            new FlameCase { name = "firespew_up", ability = "FireSpew", shooter = 'B', tdx = 2, tdz = -1, tk = 1, bdx = -2, bdz = 4 },
            new FlameCase { name = "firespew_flat", ability = "FireSpew", shooter = 'B', tdx = 3, tdz = 7, tk = 0, bdx = -2, bdz = 4 },
            new FlameCase { name = "acidspray_down", ability = "AcidSpray", shooter = 'A', tdx = 3, tdz = 4, tk = 0 },
            new FlameCase { name = "acidspray_flat", ability = "AcidSpray", shooter = 'B', tdx = 3, tdz = 7, tk = 0, bdx = -2, bdz = 4 },
            new FlameCase { name = "hellcatburner_down", ability = "HellcatBurner", shooter = 'A', tdx = 3, tdz = 4, tk = 0 },
            new FlameCase { name = "hellcatburner_flat", ability = "HellcatBurner", shooter = 'B', tdx = 3, tdz = 7, tk = 0, bdx = -2, bdz = 4 },
            new FlameCase { name = "incinerator_down", gun = "Gun_Incinerator", shooter = 'A', tdx = 4, tdz = 8, tk = 0 },
            new FlameCase { name = "incinerator_flat", gun = "Gun_Incinerator", shooter = 'B', tdx = 4, tdz = 12, tk = 0, bdx = -2, bdz = 4 },
            new FlameCase { name = "graser_down", gun = "Gun_BeamGraser", shooter = 'A', tdx = 4, tdz = 8, tk = 0 },
        };

        private static Pawn fzA, fzB, fzH;
        private static int fzCx, fzEz, fzCase, fzAppliedAt, fzBeamShots, fzFrame;
        private static bool fzRec;
        private static readonly List<string> fzFlecks = new List<string>();
        private static readonly List<string> fzFires = new List<string>();
        private static readonly List<string> fzDamage = new List<string>();
        private static readonly List<string> fzExpl = new List<string>();
        private static readonly List<string> fzBeamHits = new List<string>();
        private static Map fzMap;
        private static bool fzPatched;

        private static IntVec3 FzEdge(Map map) => ProjectZ.CellOnFloor(new IntVec3(fzCx, 0, fzEz), 1, map);

        // a cell as "(x,z) F<floor> g(x,z)"
        private static string FzCell(IntVec3 c)
        {
            Map map = fzMap;
            int k = ProjectZ.FloorOf(c, map);
            IntVec3 g = ProjectZ.GroundCellOf(c, map);
            return k < 0 ? $"({c.x},{c.z}) VOID" : $"({c.x},{c.z}) F{k + 1} g({g.x},{g.z})";
        }

        private static string FzVec(Vector3 v)
        {
            Map map = fzMap;
            IntVec3 c = v.ToIntVec3();
            int k = c.InBounds(map) ? ProjectZ.FloorOf(c, map) : -9;
            float gz = Levels.GroundZLoose(map, v.z);
            return k < 0 ? $"({v.x:0.0},{v.z:0.0}) {(k == -9 ? "OUT" : "VOID")} g~({v.x:0.0},{gz:0.0})" : $"({v.x:0.0},{v.z:0.0}) F{k + 1} g({v.x:0.0},{gz:0.0})";
        }

        // ---- recording hooks (test only)
        public static void FzFleck_Prefix(FleckCreationData fleckData)
        {
            if (fzRec && fzFlecks.Count < 400)
            {
                fzFlecks.Add($"{fleckData.def?.defName} at {FzVec(fleckData.spawnPosition)} rot {fleckData.rotation:0} velAngle {fleckData.velocityAngle:0} speed {fleckData.velocitySpeed:0.0}");
            }
        }

        public static void FzFire_Prefix(IntVec3 c)
        {
            if (fzRec)
            {
                fzFires.Add(FzCell(c));
            }
        }

        public static void FzDamage_Prefix(Thing __instance, DamageInfo dinfo)
        {
            if (fzRec && __instance.Spawned && !(__instance is Mote) && fzDamage.Count < 200)
            {
                fzDamage.Add($"{__instance.def.defName}{(__instance is Pawn ? "(" + __instance.LabelShort + ")" : "")} at {FzCell(__instance.Position)} {dinfo.Def?.defName} {dinfo.Amount:0.0}");
            }
        }

        public static void FzExpl_Prefix(IntVec3 center, float radius, DamageDef damType, List<IntVec3> overrideCells)
        {
            if (!fzRec)
            {
                return;
            }
            string oc = overrideCells == null ? "none" : $"{overrideCells.Count}: floors " + string.Join(",", overrideCells.GroupBy(c => ProjectZ.FloorOf(c, fzMap)).Select(g => (g.Key < 0 ? "void" : "F" + (g.Key + 1)) + "x" + g.Count()));
            fzExpl.Add($"explosion {damType?.defName} centre {FzCell(center)} radius {radius:0.0}; overrideCells {oc}");
        }

        public static void FzActivate_Prefix(Ability __instance)
        {
            if (fzRec && fzAppliedAt < 0)
            {
                fzAppliedAt = Find.TickManager.TicksGame;
            }
        }

        public static void FzBeamHit_Prefix(IntVec3 cell, IntVec3 sourceCell)
        {
            if (fzRec)
            {
                fzBeamShots++;
                if (fzBeamHits.Count < 60)
                {
                    fzBeamHits.Add($"{FzCell(cell)} src {FzCell(sourceCell)}");
                }
            }
        }

        private static void FzPatch()
        {
            if (fzPatched)
            {
                return;
            }
            fzPatched = true;
            Harmony h = new Harmony("Peren.IsoRimProjectZ.test.flamediag");
            h.Patch(AccessTools.Method(typeof(FleckManager), nameof(FleckManager.CreateFleck), new[] { typeof(FleckCreationData) }), prefix: new HarmonyMethod(typeof(TestRunner), nameof(FzFleck_Prefix)) { priority = Priority.First });
            foreach (var mi in AccessTools.GetDeclaredMethods(typeof(FireUtility)).Where(m => m.Name == "TryStartFireIn"))
            {
                h.Patch(mi, prefix: new HarmonyMethod(typeof(TestRunner), nameof(FzFire_Prefix)));
            }
            h.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.TakeDamage)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(FzDamage_Prefix)));
            h.Patch(AccessTools.Method(typeof(GenExplosion), nameof(GenExplosion.DoExplosion)), prefix: new HarmonyMethod(typeof(TestRunner), nameof(FzExpl_Prefix)));
            h.Patch(AccessTools.Method(typeof(Ability), nameof(Ability.Activate), new[] { typeof(LocalTargetInfo), typeof(LocalTargetInfo) }), prefix: new HarmonyMethod(typeof(TestRunner), nameof(FzActivate_Prefix)));
            h.Patch(AccessTools.Method(typeof(Verb_ShootBeam), "HitCell"), prefix: new HarmonyMethod(typeof(TestRunner), nameof(FzBeamHit_Prefix)));
        }

        private static bool StepFlame(Map map)
        {
            switch (stage)
            {
                case "fz_setup":
                    SetupFlame(map);
                    return true;
                case "fz_case":
                    if (fzCase >= fzCases.Length)
                    {
                        IsoRimBridge.SetOption("freeYaw", 0f);
                        Finish();
                        return true;
                    }
                    FzStart(map, fzCases[fzCase]);
                    return true;
                case "fz_cast":
                {
                    tickWatch = null;
                    FlameCase c = fzCases[fzCase];
                    Info($"{c.name}: cast seen {(fzAppliedAt >= 0 ? "yes" : "NO")}, beam hits {fzBeamShots}, flecks so far {fzFlecks.Count}");
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    fzFrame = 0;
                    Wait(1, "fz_yaw");
                    return true;
                }
                case "fz_yaw":
                {
                    FlameCase c = fzCases[fzCase];
                    IsoRimBridge.SetOption("freeYaw", fzFrame == 1 ? 180f : 0f);
                    Find.CameraDriver.config.sizeRange.min = 2f;
                    if (fzFrame == 2) { ShotAt(map, 1, new IntVec3(fzCx, 0, fzEz - 6), 18f); } else { ShotAt(map, 1, new IntVec3(fzCx, 0, fzEz + 3), 9f); }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Wait(30, "fz_take");
                    return true;
                }
                case "fz_take":
                {
                    FlameCase c = fzCases[fzCase];
                    ZCapture($"flame_{c.name}_{(fzFrame == 0 ? "yaw45" : fzFrame == 1 ? "yaw225" : "wide")}");
                    if (fzFrame == 0)
                    {
                        FzLogMotes(map, c);
                    }
                    fzFrame++;
                    Wait(3, fzFrame < 3 ? "fz_yaw" : "fz_after");
                    return true;
                }
                case "fz_after":
                    // let the effect finish (fires catch, damage lands)
                    StartTicks(240, "fz_report");
                    return true;
                case "fz_report":
                    FzReport(map, fzCases[fzCase]);
                    fzRec = false;
                    FzReset(map);
                    fzCase++;
                    Wait(2, "fz_case");
                    return true;
            }
            return false;
        }

        private static void FzStart(Map map, FlameCase c)
        {
            FzReset(map);
            fzFlecks.Clear();
            fzFires.Clear();
            fzDamage.Clear();
            fzExpl.Clear();
            fzBeamHits.Clear();
            fzAppliedAt = -1;
            fzBeamShots = 0;
            Pawn s = c.shooter == 'A' ? fzA : fzB;
            Teleport(fzA, FzEdge(map));
            Teleport(fzB, new IntVec3(fzCx + c.bdx, 0, fzEz + c.bdz));
            IntVec3 tg = new IntVec3(fzCx + c.tdx, 0, fzEz + c.tdz);
            IntVec3 tc = c.tk == 0 ? tg : ProjectZ.CellOnFloor(tg, c.tk, map);
            Teleport(fzH, tc);
            fzH.stances.stunner.StunFor(100000, null, addBattleLog: false);
            fzH.Drawer?.tweener?.ResetTweenedPosToRoot();
            foreach (Pawn p in new[] { fzA, fzB })
            {
                p.drafter.Drafted = true;
                p.drafter.FireAtWill = false;
            }
            FloorView.Set(1);
            LocalTargetInfo t = new LocalTargetInfo(fzH);
            IntVec3 sg = ProjectZ.GroundCellOf(s.Position, map);
            float trueAngle = (tg - sg).AngleFlat;
            float rawAngle = (tc - s.Position).AngleFlat;
            float d3 = Range3D.Distance(s.Position, tc, map);
            Info($"---- {c.name}: shooter {s.LabelShort} at {FzCell(s.Position)} -> raider {FzCell(tc)}; true ground angle {trueAngle:0} deg, raw map angle {rawAngle:0} deg, 3D distance {d3:0.0}, raw distance {(tc - s.Position).LengthHorizontal:0.0}");
            bool ok;
            fzRec = true;
            if (c.ability != null)
            {
                AbilityDef ad = DefDatabase<AbilityDef>.GetNamedSilentFail(c.ability);
                if (ad == null)
                {
                    Result($"{c.name}: ability def", false, c.ability + " missing (DLC?)");
                    Wait(1, "fz_report");
                    return;
                }
                if (s.abilities.GetAbility(ad) == null)
                {
                    s.abilities.GainAbility(ad);
                }
                Ability ab = s.abilities.GetAbility(ad);
                ab.ResetCooldown();
                // what the player sees while aiming: the comp's preview cells (AffectedCells) for the cone/spray comps
                foreach (CompAbilityEffect comp in ab.EffectComps)
                {
                    var mi = AccessTools.Method(comp.GetType(), "AffectedCells", new[] { typeof(LocalTargetInfo) });
                    if (mi == null)
                    {
                        continue;
                    }
                    List<IntVec3> cells = new List<IntVec3>((IEnumerable<IntVec3>)mi.Invoke(comp, new object[] { t }));
                    FzCellsInfo(map, c.name + " " + comp.GetType().Name + ".AffectedCells (preview = applied)", cells, sg, tg, trueAngle);
                }
                Info($"{c.name}: CanApplyOn {ab.CanApplyOn(t)}, verb.CanHitTarget {ab.verb.CanHitTarget(t)}, ValidateTarget {ab.verb.ValidateTarget(t, false)}, CanCast {ab.CanCast.Accepted}");
                ab.QueueCastingJob(t, t);
                ok = s.CurJob?.ability == ab;
                Info($"{c.name}: cast job {(ok ? "taken" : "NOT taken")} ({s.CurJobDef?.defName})");
            }
            else
            {
                s.equipment.DestroyAllEquipment();
                s.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(c.gun)));
                Job j = JobMaker.MakeJob(JobDefOf.AttackStatic, t);
                j.playerForced = true;
                ok = s.jobs.TryTakeOrderedJob(j, JobTag.DraftedOrder);
                Info($"{c.name}: {c.gun} AttackStatic order {ok}; CanHitTarget {s.equipment.PrimaryEq.PrimaryVerb.CanHitTarget(t)}");
            }
            bool beam = c.gun != null;
            TickUntil(600, () => beam ? fzBeamShots >= 6 : fzAppliedAt >= 0 && Find.TickManager.TicksGame - fzAppliedAt >= 2, "fz_cast", 1);
        }

        private static void FzCellsInfo(Map map, string what, List<IntVec3> cells, IntVec3 sg, IntVec3 tg, float trueAngle)
        {
            if (cells.Count == 0)
            {
                Info($"{what}: EMPTY");
                return;
            }
            var floors = cells.GroupBy(x => ProjectZ.FloorOf(x, map)).Select(g => (g.Key < 0 ? "void" : "F" + (g.Key + 1)) + "x" + g.Count());
            List<IntVec3> gs = cells.Select(x => ProjectZ.GroundCellOf(x, map)).Where(x => x.IsValid).ToList();
            string dir = "n/a";
            if (gs.Count > 0)
            {
                Vector3 mean = new Vector3(gs.Average(x => x.x + 0.5f), 0f, gs.Average(x => x.z + 0.5f));
                float a = (mean - sg.ToVector3Shifted()).AngleFlat();
                dir = $"centroid g({mean.x:0.0},{mean.z:0.0}) at ground angle {a:0} deg from the shooter (target at {trueAngle:0}; off by {Mathf.Abs(Mathf.DeltaAngle(a, trueAngle)):0} deg); contains target ground cell {gs.Contains(tg)}";
            }
            Info($"{what}: {cells.Count} cells, {string.Join(",", floors)}; {dir}; first {string.Join(" ", cells.Take(6).Select(FzCell))}");
        }

        private static void FzLogMotes(Map map, FlameCase c)
        {
            List<Mote> motes = map.listerThings.AllThings.OfType<Mote>().Where(m => m.Spawned).ToList();
            foreach (Mote m in motes.Take(25))
            {
                string extra = "";
                if (m is MoteDualAttached md)
                {
                    MoteAttachLink l2 = Traverse.Create(md).Field("link2").GetValue<MoteAttachLink>(); extra = $" link1 {(md.link1.Linked ? FzVec(md.link1.LastDrawPos) : "-")} link2 {(l2.Linked ? FzVec(l2.LastDrawPos) : "-")}";
                }
                Info($"{c.name} mote {m.def.defName} at {FzVec(m.DrawPos)} rot {m.exactRotation:0}{extra}");
            }
            foreach (Thing t in map.listerThings.AllThings.Where(t => t.def == ThingDefOf.IncineratorSpray).ToList())
            {
                var list = Traverse.Create(t).Field("projectiles").GetValue() as System.Collections.IEnumerable;
                int n = 0;
                if (list != null)
                {
                    foreach (object o in list)
                    {
                        if (n++ >= 8)
                        {
                            break;
                        }
                        Traverse tr = Traverse.Create(o);
                        Vector3 ws = tr.Field("worldSource").GetValue<Vector3>();
                        Vector3 wt = tr.Field("worldTarget").GetValue<Vector3>();
                        Info($"{c.name} sprayer {t.Position} motion: worldSource {FzVec(ws)} -> worldTarget {FzVec(wt)}; drawn direction raw angle {(wt - ws).AngleFlat():0}");
                    }
                }
            }
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).Take(12))
            {
                Projectile p = (Projectile)t;
                Info($"{c.name} projectile {p.def.defName} at {FzVec(p.ExactPosition)} -> dest {FzVec(Traverse.Create(p).Field("destination").GetValue<Vector3>())} cross {Flights.cross.TryGetValue(p, out _)}");
            }
        }

        private static void FzReport(Map map, FlameCase c)
        {
            foreach (string s in fzExpl)
            {
                Info($"{c.name} {s}");
            }
            Info($"{c.name}: flecks {fzFlecks.Count}: " + string.Join(" | ", fzFlecks.GroupBy(f => f.Split(' ')[0]).Select(g => g.Key + " x" + g.Count())));
            foreach (string s in fzFlecks.Where((f, i) => i % Math.Max(1, fzFlecks.Count / 12) == 0).Take(14))
            {
                Info($"{c.name} fleck {s}");
            }
            if (fzBeamHits.Count > 0)
            {
                Info($"{c.name}: beam HitCell calls {fzBeamShots}; " + string.Join(" ; ", fzBeamHits.Take(25)));
            }
            Info($"{c.name}: fires started {fzFires.Count}: " + string.Join(" ; ", fzFires.Distinct().Take(30)));
            Info($"{c.name}: damage events {fzDamage.Count}: " + string.Join(" ; ", fzDamage.Take(30)));
            List<Fire> fires = map.listerThings.ThingsInGroup(ThingRequestGroup.Fire).OfType<Fire>().ToList();
            Info($"{c.name}: fires on map now {fires.Count}: " + string.Join(" ", fires.GroupBy(f => ProjectZ.FloorOf(f.Position, map)).Select(g => (g.Key < 0 ? "void" : "F" + (g.Key + 1)) + "x" + g.Count())));
            List<Filth> filth = map.listerThings.ThingsInGroup(ThingRequestGroup.Filth).OfType<Filth>().Where(f => f.def.defName.Contains("Bile") || f.def.defName.Contains("Ash") || f.def.defName.Contains("Acid") || f.def.defName.Contains("Fuel")).ToList();
            if (filth.Count > 0)
            {
                Info($"{c.name}: bile/ash/acid filth {filth.Count}: " + string.Join(" ; ", filth.Take(15).Select(f => f.def.defName + " " + FzCell(f.Position))));
            }
            int inj = fzH.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);
            bool burning = fzH.HasAttachment(ThingDefOf.Fire);
            Result($"{c.name}: target raider hit", inj > 0 || burning || fzDamage.Any(d => d.Contains("(" + fzH.LabelShort + ")")), $"injuries {inj}, burning {burning}, at {FzCell(fzH.Position)}");
        }

        private static void FzReset(Map map)
        {
            foreach (Pawn p in new[] { fzA, fzB, fzH })
            {
                if (p == null)
                {
                    continue;
                }
                p.jobs?.StopAll();
                if (p.Dead)
                {
                    ResurrectionUtility.TryResurrect(p);
                }
                foreach (Hediff h in p.health.hediffSet.hediffs.Where(h => h is Hediff_Injury || h.def == HediffDefOf.BloodLoss).ToList())
                {
                    p.health.RemoveHediff(h);
                }
                foreach (Thing a in (p.GetComp<CompAttachBase>()?.attachments ?? new List<AttachableThing>()).Cast<Thing>().ToList())
                {
                    a.Destroy();
                }
            }
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Fire).ToList())
            {
                t.Destroy();
            }
            foreach (Thing t in map.listerThings.AllThings.Where(t => t.def == ThingDefOf.IncineratorSpray).ToList()) { t.Destroy(); }
            foreach (Pawn p in new[] { fzA, fzB }) { if (p?.equipment?.PrimaryEq != null) { p.equipment.PrimaryEq.PrimaryVerb.Reset(); } p?.stances?.CancelBusyStanceHard(); }
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Filth).ToList())
            {
                t.Destroy();
            }
        }

        private static void SetupFlame(Map map)
        {
            fzMap = map;
            FzPatch();
            AnnexMapComponent an = ProjectZ.Annex(map);
            List<Pawn> cols = Colonists(map).Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)).ToList();
            for (int i = 0; i < 3 && cols.Count < 2; i++)
            {
                Pawn extra = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true, mustBeCapableOfViolence: true));
                GenSpawn.Spawn(extra, CellFinder.RandomClosewalkCellNear(map.Center, map, 6), map);
                cols = Colonists(map).Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)).ToList();
            }
            if (an == null || an.floors < 2 || cols.Count < 2)
            {
                Result("flamediag setup", false, $"floors {an?.floors}, colonists {cols.Count}");
                Finish();
                return;
            }
            CellRect found = CellRect.Empty;
            int bestBad = int.MaxValue;
            for (int z = 20; z + 30 < an.groundZ - 3; z += 4)
            {
                for (int x = 3; x + 21 < map.Size.x - 3; x += 4)
                {
                    CellRect r = new CellRect(x, z, 21, 30);
                    if (r.ExpandedBy(1).Cells.Any(c => c.InBounds(map) && c.GetThingList(map).Any(t => !t.def.destroyable)))
                    {
                        continue;
                    }
                    int bad = r.Cells.Count(c => c.GetTerrain(map).IsWater || c.GetTerrain(map).passability == Traversability.Impassable || map.roofGrid.Roofed(c) || c.GetFirstBuilding(map) != null);
                    if (bad < bestBad)
                    {
                        bestBad = bad;
                        found = r;
                    }
                }
            }
            if (found.Area == 0)
            {
                Result("flamediag field", false, "map too small");
                Finish();
                return;
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
            fzCx = found.CenterCell.x;
            CellRect tower = new CellRect(fzCx - 3, found.minZ, 7, 7);
            fzEz = tower.maxZ;
            foreach (IntVec3 g in tower)
            {
                map.roofGrid.SetRoof(g, RoofDefOf.RoofConstructed);
                map.terrainGrid.SetTerrain(ProjectZ.CellOnFloor(g, 1, map), PZDefOf.PZ_UpperFloor);
                map.fogGrid.Unfog(ProjectZ.CellOnFloor(g, 1, map));
            }
            cols = cols.OrderByDescending(p => p.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0).ToList();
            fzA = cols[0];
            fzB = cols[1];
            foreach (Pawn p in Colonists(map).Where(p => p != fzA && p != fzB))
            {
                p.drafter.Drafted = true;
                p.drafter.FireAtWill = false;
                Teleport(p, CellFinder.RandomClosewalkCellNear(new IntVec3(found.minX > map.Size.x / 2 ? 5 : map.Size.x - 6, 0, 5), map, 4));
            }
            Faction f = LEnemy();
            fzH = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Mercenary_Gunner"), f, forceGenerateNewPawn: true));
            fzH.equipment.DestroyAllEquipment();
            fzH.apparel?.DestroyAll();
            GenSpawn.Spawn(fzH, new IntVec3(fzCx + 3, 0, fzEz + 5), map);
            LordMaker.MakeNewLord(f, new LordJob_DefendPoint(fzH.Position, 0.9f, 0.9f, false, false), map, new[] { fzH });
            Info($"flamediag: field {found}, tower {tower} (slab on floor 2), edge row {fzEz}, groundZ {an.groundZ}, stride {an.stride}, floors {an.floors}; storey {IsoRimBridge.StoreyHeight:0.00}; A {fzA}, B {fzB}, raider {fzH} ({f})");
            Info($"DLCs: Biotech {ModsConfig.BiotechActive}, Anomaly {ModsConfig.AnomalyActive}, Odyssey {ModsConfig.OdysseyActive}");
            fzCase = 0;
            Wait(10, "fz_case");
        }
    }
}
